using UnityEngine;
using UnityEngine.XR;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
using RosMessageTypes.Nav;

/// <summary>
/// "Direct" KAT-to-Husky mapping: two independent, open-loop channels that
/// mirror how CmdVelPublisher (keyboard) already works --
///
///   forwardIntent -> linear.x
///   turnIntent    -> angular.z
///
/// published together every tick. Neither channel reads the other's value,
/// its filtered/raw signal, or its dead-zone/active state, and neither reads
/// odometry. That is the entire point of this file: KatHuskyClosedLoopController
/// and KatHuskySettleAnchorController both feed accumulated body yaw into a
/// position/heading closed loop against /odometry/filtered, and separately
/// scale turn sensitivity while walking and scale forward speed while
/// turning. Each of those couplings is individually reasonable, but stacked
/// together they add latency, cross-talk between the two channels, and (via
/// stopWhenOdometryUnavailable) make forward motion depend on a topic that
/// has nothing to do with forward motion. The result was reported as "can't
/// do forward+turn at the same time."
///
/// The bet this file makes: Husky's own base controller already closes a
/// velocity-tracking loop (wheel encoders -> motor PID) on whatever is
/// published to /cmd_vel, and turning-while-driving ("differential" arc
/// motion) is produced by that base controller mixing linear.x/angular.z
/// into left/right wheel speeds -- it is not something this script needs to
/// arbitrate. The only closed loop this design still relies on is the
/// operator's own eyes: same as driving with a keyboard or a joystick, you
/// do not get instantaneous body-angle == robot-angle correspondence, you
/// get rate control (turn your body faster/longer -> robot turns
/// faster/longer) and you visually correct like you would with any other
/// rate-controlled input. Absolute heading precision without watching the
/// robot is a genuinely different requirement (see
/// KatHuskySettleAnchorController) and is not what this file is for.
///
/// /odometry/filtered is still subscribed and shown in the status GUI/log,
/// but purely as a read-only reference for the operator and for logging --
/// it never feeds into linear or angular below. Driving does not stop if
/// odometry is stale or missing.
///
/// This is a separate file on purpose -- KatHuskyClosedLoopController.cs and
/// KatHuskySettleAnchorController.cs are left completely untouched. Swap the
/// component on the robot GameObject to try this version; the public ROS
/// topic/message shape is identical.
/// </summary>
[DisallowMultipleComponent]
public class KatHuskyDirectController : MonoBehaviour
{
    [Header("ROS")]
    public string cmdVelTopic = "/cmd_vel";
    [Tooltip("Subscribed for the status GUI/log only. Never used to compute linear/angular below -- driving does not require or wait on this.")]
    public string odometryTopic = "/odometry/filtered";

    [Header("Forward channel (independent of turn)")]
    public float maxForwardSpeed = 0.6f;
    public float maxReverseSpeed = 0.20f;
    public float walkDeadZone = 0.02f;
    public float inputSpeedForMax = 1.0f;
    public float minDriveSpeed = 0.08f;
    [Range(0.3f, 1f)] public float responseExponent = 0.60f;
    public float forwardAttackTime = 0.04f;
    public float forwardReleaseTime = 0.15f;

    [Header("Step-in-place control (Mini S / Walk C2)")]
    [Tooltip("Let the device-specific step signal drive forward even when moveSpeed stays near zero during a lift-and-step gait. Only ever affects the forward channel.")]
    public bool enableStepInPlaceDrive = true;
    public float stepInPlaceInput = 0.35f;
    public float stepSignalHoldTime = 0.15f;
    public float stepFootSpeedThreshold = 0.05f;
    public bool enableMoveSpeedPulseStepFallback = true;
    public float stepPulseThreshold = 0.08f;
    public float stepPulseHoldTime = 0.60f;

    [Header("Turn channel (independent of forward)")]
    [Tooltip("Use -1 when positive KAT yaw corresponds to negative ROS yaw.")]
    public float bodyToRosYawSign = -1f;
    [Tooltip("Same dead zone and low-pass are applied regardless of whether the forward channel is active. Do not gate this by walking state -- that reintroduces the coupling this file exists to remove.")]
    public float turnRateDeadZoneDegPerSec = 6f;
    [Tooltip("Low-pass applied to the raw body yaw ANGLE before it is differentiated into a rate. KAT's raw body yaw reads out quantized to whole degrees (confirmed from field logs -- BodyRaw only ever printed .0), so differentiating it directly turns every quantization step into a brief huge instantaneous rate spike. Smoothing the angle first keeps that spike out of the rate calculation instead of relying on turnRateSmoothingTime to fully absorb it afterward.")]
    public float bodyYawFilterTime = 0.06f;
    public float turnRateSmoothingTime = 0.10f;
    [Tooltip("Filtered body yaw rate that maps to maxAngularSpeed. Calibrate from the KAT DIRECT debug log: watch filteredYawRateDegPerSec while turning at a comfortable brisk pace and set this near that peak.")]
    public float turnRateForMaxOutput = 90f;
    [Range(0.3f, 1f)] public float turnResponseExponent = 1f;
    public float maxAngularSpeed = 0.6f;
    [Tooltip("Reject a single KAT heading jump larger than this (quaternion/Euler discontinuity) instead of letting it spike the turn rate for one tick.")]
    public float maxAcceptedBodyYawStepDeg = 45f;
    [Tooltip("Reject the body yaw reading when the KAT body sensor tilts more than this many degrees from horizontal. This only holds/decays the turn channel -- it never stops forward.")]
    public float maxBodyTiltFromHorizontalDeg = 70f;

    [Header("Activation and safety")]
    public bool enableRobotControl = true;
    public XRNode activationHand = XRNode.RightHand;
    public float activationTriggerThreshold = 0.2f;
    public bool activationLatch = true;
    public bool startActivated = false;
    public XRNode emergencyStopHand = XRNode.RightHand;
    public bool emergencyStopLatch = true;
    public XRNode reverseModeHand = XRNode.RightHand;
    public bool reverseModeLatch = true;
    public float publishRateHz = 20f;

    [Header("Optional keyboard testing")]
    public bool enableKeyboardFallback = false;
    public KeyCode activationKey = KeyCode.T;
    public KeyCode emergencyStopKey = KeyCode.E;
    public KeyCode reverseModeKey = KeyCode.R;

    [Header("Diagnostics")]
    public bool enableDebugLog = true;
    public float debugLogInterval = 0.05f;
    public bool showStatusGUI = true;

    private ROSConnection ros;

    // Odometry: display/log only. Deliberately not read anywhere in the
    // linear/angular calculation below.
    private bool hasOdometry;
    private float robotYawDeg;

    private bool activationEnabled;
    private bool lastActivationButton;
    private bool emergencyStopEngaged;
    private bool lastEmergencyStopButton;
    private bool reverseModeEnabled;
    private bool lastReverseButton;

    // Forward channel state.
    private float filteredForwardSpeed;
    private float lastRawForward;
    private Vector3 lastRawMoveSpeed;

    // Turn channel state.
    private bool hasRawBodyYaw;
    private float lastRawBodyYawDeg;
    private float filteredBodyYawDeg;
    private float filteredYawRateDegPerSec;

    // Step-in-place state (forward channel only).
    private bool hasMiniSExtra;
    private MiniSExtraData.extraInfo miniSExtra;
    private bool hasWalkC2Extra;
    private WalkC2ExtraData.extraInfo walkC2Extra;
    private bool rawStepSignal;
    private float lastStepSignalTime = -999f;
    private float lastMoveSpeedPulseTime = -999f;
    private bool stepInPlaceActive;
    private bool moveSpeedPulseActive;

    private string lastDeviceName = "";
    private float lastPublishedLinear;
    private float lastPublishedAngular;
    private float nextPublishTime;
    private float nextDebugTime;
    private float nextBodyYawWarningTime;
    private string status = "STANDBY";

    private void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<TwistMsg>(cmdVelTopic);
        ros.Subscribe<OdometryMsg>(odometryTopic, OnOdometry);
        activationEnabled = startActivated;
        status = activationEnabled ? "ACTIVE" : "PRESS TRIGGER TO START";
    }

    private void OnOdometry(OdometryMsg message)
    {
        // Reference-only: never used below to compute linear/angular.
        QuaternionMsg q = message.pose.pose.orientation;
        double sinYaw = 2.0 * (q.w * q.z + q.x * q.y);
        double cosYaw = 1.0 - 2.0 * (q.y * q.y + q.z * q.z);
        robotYawDeg = (float)(System.Math.Atan2(sinYaw, cosYaw) * Mathf.Rad2Deg);
        hasOdometry = true;
    }

    private void FixedUpdate()
    {
        KATNativeSDK.TreadMillData data = KATNativeSDK.GetWalkStatus();
        if (!data.connected)
        {
            status = "KAT DISCONNECTED";
            ResetAllState();
            PublishStopIfDue();
            return;
        }

        lastDeviceName = data.deviceName ?? "";
        ReadStepSignal(data);

        // Forward channel: computed unconditionally from moveSpeed/step
        // signal. It does not depend on whether the body yaw reading below
        // is valid this tick.
        lastRawMoveSpeed = data.moveSpeed;
        lastRawForward = new Vector2(lastRawMoveSpeed.x, lastRawMoveSpeed.z).magnitude;

        if (enableMoveSpeedPulseStepFallback
            && lastRawForward >= Mathf.Max(stepPulseThreshold, walkDeadZone))
        {
            lastMoveSpeedPulseTime = Time.unscaledTime;
        }
        moveSpeedPulseActive = enableMoveSpeedPulseStepFallback
            && Time.unscaledTime - lastMoveSpeedPulseTime <= Mathf.Max(stepPulseHoldTime, 0f);

        if (enableStepInPlaceDrive && rawStepSignal)
        {
            lastStepSignalTime = Time.unscaledTime;
        }
        bool extraDataStepActive = enableStepInPlaceDrive
            && Time.unscaledTime - lastStepSignalTime <= Mathf.Max(stepSignalHoldTime, 0f);
        stepInPlaceActive = extraDataStepActive || moveSpeedPulseActive;
        if (stepInPlaceActive)
        {
            lastRawForward = Mathf.Max(lastRawForward, Mathf.Max(stepInPlaceInput, 0f));
        }

        // Turn channel: only this part is gated by a valid body yaw reading.
        // An invalid reading holds/decays the turn channel -- it never
        // touches forward.
        if (TryGetHorizontalBodyYaw(data.bodyRotationRaw, out float rawBodyYawDeg))
        {
            UpdateTurnRate(rawBodyYawDeg);
        }
        else
        {
            // Drop the raw-yaw reference so recovery doesn't compute a rate
            // spike across the invalid gap, and decay the filtered rate
            // toward zero at the normal smoothing rate instead of freezing
            // it or snapping it to zero.
            hasRawBodyYaw = false;
            float blend = 1f - Mathf.Exp(-Time.fixedDeltaTime / Mathf.Max(turnRateSmoothingTime, 0.01f));
            filteredYawRateDegPerSec = Mathf.Lerp(filteredYawRateDegPerSec, 0f, blend);
        }

        UpdateActivation();
        UpdateEmergencyStop();
        UpdateReverseMode();

        if (!activationEnabled)
        {
            status = "PRESS TRIGGER TO START";
            PublishStopIfDue();
            return;
        }
        if (emergencyStopEngaged || !enableRobotControl)
        {
            status = emergencyStopEngaged ? "EMERGENCY STOP" : "ROBOT CONTROL OFF";
            PublishStopIfDue();
            return;
        }

        float forward = FilterForward(lastRawForward);
        if (reverseModeEnabled)
        {
            forward = -forward;
        }
        float linear = CalculateLinear(forward);
        float angular = CalculateTurn();

        status = reverseModeEnabled ? "ACTIVE / REVERSE" : "ACTIVE";
        PublishIfDue(linear, angular);
        float debugRawBodyYawDeg = hasRawBodyYaw ? lastRawBodyYawDeg : float.NaN;
        LogIfDue(debugRawBodyYawDeg, linear, angular);
    }

    private void UpdateTurnRate(float rawBodyYawDeg)
    {
        if (!hasRawBodyYaw)
        {
            lastRawBodyYawDeg = rawBodyYawDeg;
            filteredBodyYawDeg = rawBodyYawDeg;
            hasRawBodyYaw = true;
            return;
        }

        // Reject on the RAW step, before any angle smoothing. A genuine
        // quaternion/Euler discontinuity has to be caught here -- if it were
        // allowed into the angle low-pass first, LerpAngle would blend
        // partway toward the bogus value and the glitch would leak into the
        // rate over several ticks instead of being skipped outright.
        float rawStep = Mathf.DeltaAngle(lastRawBodyYawDeg, rawBodyYawDeg);
        lastRawBodyYawDeg = rawBodyYawDeg;

        if (Mathf.Abs(rawStep) > Mathf.Max(maxAcceptedBodyYawStepDeg, 1f))
        {
            if (enableDebugLog && Time.unscaledTime >= nextBodyYawWarningTime)
            {
                nextBodyYawWarningTime = Time.unscaledTime + 0.5f;
                Debug.LogWarning($"[KAT DIRECT] Rejected body yaw jump: step {rawStep:F1} deg. Sample skipped.");
            }
            return;
        }

        // Smooth the ANGLE first, then differentiate the smoothed angle.
        // Differentiating the raw (quantized) angle directly would turn every
        // integer-degree step into a brief huge instantaneous rate that the
        // rate-domain low-pass below only partially absorbs.
        float angleBlend = 1f - Mathf.Exp(-Time.fixedDeltaTime / Mathf.Max(bodyYawFilterTime, 0.01f));
        float previousFilteredBodyYawDeg = filteredBodyYawDeg;
        filteredBodyYawDeg = Mathf.LerpAngle(filteredBodyYawDeg, rawBodyYawDeg, angleBlend);

        float filteredStep = Mathf.DeltaAngle(previousFilteredBodyYawDeg, filteredBodyYawDeg);
        float rawRateDegPerSec = filteredStep / Mathf.Max(Time.fixedDeltaTime, 0.001f);

        float rateBlend = 1f - Mathf.Exp(-Time.fixedDeltaTime / Mathf.Max(turnRateSmoothingTime, 0.01f));
        filteredYawRateDegPerSec = Mathf.Lerp(filteredYawRateDegPerSec, rawRateDegPerSec, rateBlend);
    }

    private bool TryGetHorizontalBodyYaw(Quaternion bodyRotation, out float yawDeg)
    {
        Vector3 forward = bodyRotation * Vector3.forward;
        forward.y = 0f;

        float minHorizontalMagnitude = Mathf.Cos(Mathf.Clamp(maxBodyTiltFromHorizontalDeg, 0f, 89f) * Mathf.Deg2Rad);
        if (forward.sqrMagnitude < minHorizontalMagnitude * minHorizontalMagnitude)
        {
            yawDeg = 0f;
            return false;
        }

        forward.Normalize();
        yawDeg = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        return !float.IsNaN(yawDeg) && !float.IsInfinity(yawDeg);
    }

    private float FilterForward(float rawForward)
    {
        float smoothing = Mathf.Abs(rawForward) > Mathf.Abs(filteredForwardSpeed)
            ? forwardAttackTime
            : forwardReleaseTime;
        float blend = 1f - Mathf.Exp(-Time.fixedDeltaTime / Mathf.Max(smoothing, 0.01f));
        filteredForwardSpeed = Mathf.Lerp(filteredForwardSpeed, rawForward, blend);
        return filteredForwardSpeed;
    }

    private float CalculateLinear(float forward)
    {
        float magnitude = Mathf.Abs(forward);
        if (magnitude < walkDeadZone)
        {
            return 0f;
        }

        float range = Mathf.Max(inputSpeedForMax - walkDeadZone, 0.001f);
        float response = Mathf.Pow(Mathf.Clamp01((magnitude - walkDeadZone) / range), responseExponent);
        float maximum = forward >= 0f ? maxForwardSpeed : maxReverseSpeed;
        float output = Mathf.Lerp(Mathf.Min(minDriveSpeed, maximum), maximum, response);
        return forward >= 0f ? output : -output;
    }

    private float CalculateTurn()
    {
        float rate = filteredYawRateDegPerSec;
        float magnitude = Mathf.Abs(rate);
        if (magnitude < turnRateDeadZoneDegPerSec)
        {
            return 0f;
        }

        float range = Mathf.Max(turnRateForMaxOutput - turnRateDeadZoneDegPerSec, 0.01f);
        float response = Mathf.Pow(Mathf.Clamp01((magnitude - turnRateDeadZoneDegPerSec) / range), turnResponseExponent);
        float output = response * maxAngularSpeed;
        return Mathf.Sign(bodyToRosYawSign) * Mathf.Sign(rate) * output;
    }

    private void ReadStepSignal(KATNativeSDK.TreadMillData data)
    {
        hasMiniSExtra = false;
        hasWalkC2Extra = false;
        rawStepSignal = false;

        if (!string.IsNullOrEmpty(data.deviceName)
            && data.deviceName.IndexOf("Coord2", System.StringComparison.OrdinalIgnoreCase) >= 0)
        {
            try
            {
                walkC2Extra = WalkC2ExtraData.GetExtraInfoC2(data);
                hasWalkC2Extra = true;
                float leftFootHorizontal = new Vector2(walkC2Extra.lFootSpeed.x, walkC2Extra.lFootSpeed.z).magnitude;
                float rightFootHorizontal = new Vector2(walkC2Extra.rFootSpeed.x, walkC2Extra.rFootSpeed.z).magnitude;
                rawStepSignal = walkC2Extra.motionType == 3
                    || walkC2Extra.motionType == 4
                    || leftFootHorizontal >= Mathf.Max(stepFootSpeedThreshold, 0f)
                    || rightFootHorizontal >= Mathf.Max(stepFootSpeedThreshold, 0f);
            }
            catch
            {
                hasWalkC2Extra = false;
            }
            return;
        }

        try
        {
            miniSExtra = MiniSExtraData.GetExtraInfoMiniS(data);
            hasMiniSExtra = true;
            rawStepSignal = miniSExtra.isMoving;
        }
        catch
        {
            hasMiniSExtra = false;
        }
    }

    private void UpdateActivation()
    {
        bool pressed = ReadTrigger(activationHand) || (enableKeyboardFallback && Input.GetKey(activationKey));

        if (activationLatch)
        {
            if (pressed && !lastActivationButton)
            {
                activationEnabled = !activationEnabled;
            }
        }
        else
        {
            activationEnabled = pressed;
        }

        lastActivationButton = pressed;
    }

    private void UpdateEmergencyStop()
    {
        bool pressed = ReadButton(emergencyStopHand, CommonUsages.primaryButton)
            || (enableKeyboardFallback && Input.GetKey(emergencyStopKey));
        if (emergencyStopLatch)
        {
            if (pressed && !lastEmergencyStopButton)
            {
                emergencyStopEngaged = !emergencyStopEngaged;
            }
        }
        else
        {
            emergencyStopEngaged = pressed;
        }
        lastEmergencyStopButton = pressed;
    }

    private void UpdateReverseMode()
    {
        bool pressed = ReadButton(reverseModeHand, CommonUsages.secondaryButton)
            || (enableKeyboardFallback && Input.GetKey(reverseModeKey));
        if (reverseModeLatch)
        {
            if (pressed && !lastReverseButton)
            {
                reverseModeEnabled = !reverseModeEnabled;
            }
        }
        else
        {
            reverseModeEnabled = pressed;
        }
        lastReverseButton = pressed;
    }

    private bool ReadTrigger(XRNode hand)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(hand);
        return device.isValid
            && device.TryGetFeatureValue(CommonUsages.trigger, out float value)
            && value >= activationTriggerThreshold;
    }

    private static bool ReadButton(XRNode hand, InputFeatureUsage<bool> usage)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(hand);
        return device.isValid && device.TryGetFeatureValue(usage, out bool pressed) && pressed;
    }

    private void ResetAllState()
    {
        filteredForwardSpeed = 0f;
        filteredBodyYawDeg = 0f;
        filteredYawRateDegPerSec = 0f;
        hasRawBodyYaw = false;
        hasMiniSExtra = false;
        hasWalkC2Extra = false;
        rawStepSignal = false;
        lastStepSignalTime = -999f;
        lastMoveSpeedPulseTime = -999f;
        stepInPlaceActive = false;
        moveSpeedPulseActive = false;
    }

    private void PublishIfDue(float linear, float angular)
    {
        if (Time.unscaledTime < nextPublishTime) return;
        nextPublishTime = Time.unscaledTime + 1f / Mathf.Max(publishRateHz, 1f);
        Publish(linear, angular);
    }

    private void PublishStopIfDue()
    {
        PublishIfDue(0f, 0f);
    }

    private void Publish(float linear, float angular)
    {
        TwistMsg message = new TwistMsg(
            new Vector3Msg(linear, 0.0, 0.0),
            new Vector3Msg(0.0, 0.0, angular)
        );
        ros?.Publish(cmdVelTopic, message);
        lastPublishedLinear = linear;
        lastPublishedAngular = angular;
    }

    private void LogIfDue(float rawBodyYawDeg, float linear, float angular)
    {
        if (!enableDebugLog || Time.unscaledTime < nextDebugTime) return;
        nextDebugTime = Time.unscaledTime + Mathf.Max(debugLogInterval, 0.1f);
        Debug.Log(
            $"KAT DIRECT | RawFwd={lastRawForward:F3} PulseStep={moveSpeedPulseActive} MoveSpeed=({lastRawMoveSpeed.x:F3},{lastRawMoveSpeed.y:F3},{lastRawMoveSpeed.z:F3}) " +
            $"BodyRaw={rawBodyYawDeg:F1} YawRate={filteredYawRateDegPerSec:F1}deg/s " +
            $"Linear={linear:F3} Angular={angular:F3} | Odom(ref-only) Robot={robotYawDeg:F1} Fresh={hasOdometry}"
        );

        if (hasMiniSExtra)
        {
            // Dump every field of MiniSExtraData.extraInfo, not just isMoving/
            // lFootSpeed/rFootSpeed -- those three read as dead/zero for an
            // entire captured walking session while movement was still
            // happening via moveSpeed, so whatever real per-foot signal this
            // device does expose, if any, is more likely to show up in one of
            // the other fields (ground-contact, static, motionType,
            // skatingSpeed) than in the ones already logged.
            Debug.Log(
                $"KAT MINI-S EXTRA | Device={lastDeviceName} StepDrive={stepInPlaceActive} rawStepSignal={rawStepSignal} " +
                $"isMoving={miniSExtra.isMoving} isForward={miniSExtra.isForward} motionType={miniSExtra.motionType} action={miniSExtra.action} " +
                $"LGround={miniSExtra.isLeftGround} RGround={miniSExtra.isRightGround} LStatic={miniSExtra.isLeftStatic} RStatic={miniSExtra.isRightStatic} " +
                $"Skating=({miniSExtra.skatingSpeed.x:F3},{miniSExtra.skatingSpeed.y:F3},{miniSExtra.skatingSpeed.z:F3}) " +
                $"LFoot=({miniSExtra.lFootSpeed.x:F3},{miniSExtra.lFootSpeed.y:F3},{miniSExtra.lFootSpeed.z:F3}) " +
                $"RFoot=({miniSExtra.rFootSpeed.x:F3},{miniSExtra.rFootSpeed.y:F3},{miniSExtra.rFootSpeed.z:F3})"
            );
        }
        else if (hasWalkC2Extra)
        {
            Debug.Log(
                $"KAT WALK-C2 EXTRA | Device={lastDeviceName} MotionType={walkC2Extra.motionType} StepSignal={rawStepSignal} StepDrive={stepInPlaceActive} " +
                $"LFoot=({walkC2Extra.lFootSpeed.x:F3},{walkC2Extra.lFootSpeed.y:F3},{walkC2Extra.lFootSpeed.z:F3}) " +
                $"RFoot=({walkC2Extra.rFootSpeed.x:F3},{walkC2Extra.rFootSpeed.y:F3},{walkC2Extra.rFootSpeed.z:F3})"
            );
        }
    }

    private void OnGUI()
    {
        if (!showStatusGUI) return;
        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 19,
            fontStyle = FontStyle.Bold
        };
        style.normal.textColor = Color.white;
        GUI.Label(new Rect(20, 20, 900, 30), $"KAT DIRECT: {status}", style);
        GUI.Label(
            new Rect(20, 50, 1100, 30),
            $"YawRate {filteredYawRateDegPerSec:F1}°/s  cmd_vel ({lastPublishedLinear:F3}, {lastPublishedAngular:F3})",
            style
        );
        GUI.Label(
            new Rect(20, 80, 1100, 30),
            $"Odometry (reference only, not used for control): Robot={robotYawDeg:F1}°  Fresh={hasOdometry}",
            style
        );
    }

    private void OnDisable()
    {
        if (ros != null) Publish(0f, 0f);
    }

    private void OnApplicationQuit()
    {
        if (ros != null) Publish(0f, 0f);
    }
}
