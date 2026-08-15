using UnityEngine;
using UnityEngine.XR;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
using RosMessageTypes.Nav;

[DisallowMultipleComponent]
public class KatHuskyClosedLoopController : MonoBehaviour
{
    [Header("ROS")]
    public string cmdVelTopic = "/cmd_vel";
    public string odometryTopic = "/odometry/filtered";
    public float odometryTimeout = 0.5f;
    [Tooltip("For safety, do not drive when closed-loop odometry is missing or stale.")]
    public bool stopWhenOdometryUnavailable = true;

    [Header("Linear control")]
    public float maxForwardSpeed = 0.35f;
    public float maxReverseSpeed = 0.20f;
    public float walkDeadZone = 0.10f;
    public float inputSpeedForMax = 0.50f;
    public float minDriveSpeed = 0.08f;
    [Range(0.3f, 1f)] public float responseExponent = 0.60f;
    public float forwardAttackTime = 0.04f;
    public float forwardReleaseTime = 0.05f;

    [Header("Closed-loop heading")]
    [Tooltip("Use -1 when positive KAT yaw corresponds to negative ROS yaw.")]
    public float bodyToRosYawSign = -1f;
    [Tooltip("Robot yaw change requested for each degree of body yaw change.")]
    public float bodyHeadingScale = 0.5f;
    [Tooltip("Immediate body yaw-rate feedforward gain.")]
    public float yawRateFeedforwardGain = 0f;
    [Tooltip("Heading-error proportional gain, in 1/s.")]
    public float headingKp = 0.5f;
    public float headingErrorDeadZoneDeg = 4f;
    [Tooltip("Low-pass time for KAT horizontal heading. Larger values reject more jitter.")]
    public float bodyYawFilterTime = 0.10f;
    [Tooltip("Reject a single KAT heading jump larger than this. This prevents quaternion/Euler discontinuities from moving the robot target.")]
    public float maxAcceptedBodyYawStepDeg = 45f;
    [Tooltip("Consecutive rejected samples before a persistent KAT heading jump is treated as real (e.g. a fast spin) instead of noise, and adopted as the new reference.")]
    public int maxConsecutiveRejectedBodyYawSteps = 4;
    [Tooltip("Reject the body yaw reading when the KAT body sensor tilts more than this many degrees from horizontal (forward leaning/bowing). Near-vertical tilt makes the horizontal-yaw extraction numerically unstable and amplifies small sensor noise into large false heading jumps.")]
    public float maxBodyTiltFromHorizontalDeg = 70f;
    [Range(0f, 1f)]
    [Tooltip("Multiplier applied to body yaw changes while actively walking (raw forward speed above walkDeadZone). Natural gait sway feeds small, mostly-unintentional heading changes into the target while walking; a lower value damps that drift so 'walking straight' actually stays straight, without affecting turning sensitivity while standing still.")]
    public float walkingBodyYawAccumulationScale = 0.4f;
    public float bodyYawRateDeadZoneDegPerSec = 8f;
    public float bodyYawRateSmoothingTime = 0.08f;
    public float maxAngularSpeed = 0.20f;
    public float maxAngularAcceleration = 0.50f;

    [Header("Turn-aware forward speed")]
    [Tooltip("Forward speed begins decreasing above this heading error.")]
    public float slowDownHeadingErrorDeg = 10f;
    [Tooltip("Forward motion reaches its minimum multiplier at this error.")]
    public float stopForwardHeadingErrorDeg = 35f;
    [Range(0f, 1f)] public float minimumTurningForwardMultiplier = 0f;

    [Header("Activation and safety")]
    public bool enableRobotControl = true;
    public XRNode activationHand = XRNode.RightHand;
    public float activationTriggerThreshold = 0.2f;
    [Tooltip("Trigger presses toggle driving on and off.")]
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
    public KeyCode recalibrateKey = KeyCode.C;

    [Header("Diagnostics")]
    public bool enableDebugLog = true;
    public float debugLogInterval = 0.5f;
    public bool showStatusGUI = true;

    public bool HasFreshOdometry => hasOdometry && !OdometryIsStale();
    public bool HeadingCalibrated => headingCalibrated;
    public float RobotYawDeg => robotYawDeg;
    public float DesiredRobotYawDeg => desiredRobotYawDeg;
    public float HeadingErrorDeg => headingErrorDeg;

    private ROSConnection ros;
    private bool hasOdometry;
    private float robotYawDeg;
    private float lastOdometryTime = -999f;

    private bool activationEnabled;
    private bool lastActivationButton;
    private bool emergencyStopEngaged;
    private bool lastEmergencyStopButton;
    private bool reverseModeEnabled;
    private bool lastReverseButton;

    private bool headingCalibrated;
    private float robotYawReferenceDeg;
    private float accumulatedBodyYawDeg;
    private float previousBodyYawDeg;
    private float lastRawBodyYawDeg;
    private bool hasRawBodyYaw;
    private int rejectedBodyYawStepCount;
    private float filteredBodyYawDeg;
    private float filteredBodyYawRate;
    private float filteredForwardSpeed;
    private float desiredRobotYawDeg;
    private float headingErrorDeg;
    private float commandedAngular;
    private float lastPublishedLinear;
    private float lastPublishedAngular;
    private float lastRawForward;
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
        status = activationEnabled ? "WAITING FOR CALIBRATION" : "PRESS TRIGGER TO START";
    }

    private void OnOdometry(OdometryMsg message)
    {
        QuaternionMsg q = message.pose.pose.orientation;
        double sinYaw = 2.0 * (q.w * q.z + q.x * q.y);
        double cosYaw = 1.0 - 2.0 * (q.y * q.y + q.z * q.z);
        robotYawDeg = (float)(System.Math.Atan2(sinYaw, cosYaw) * Mathf.Rad2Deg);
        hasOdometry = true;
        lastOdometryTime = Time.unscaledTime;
    }

    private void FixedUpdate()
    {
        KATNativeSDK.TreadMillData data = KATNativeSDK.GetWalkStatus();
        if (!data.connected)
        {
            status = "KAT DISCONNECTED";
            // The user can turn while KAT is disconnected. The old body/robot
            // relationship is therefore no longer trustworthy.
            ResetMotionState(true);
            PublishStopIfDue();
            return;
        }

        if (!TryGetHorizontalBodyYaw(data.bodyRotationRaw, out float rawBodyYawDeg))
        {
            status = "INVALID KAT HEADING";
            ResetMotionState(true);
            PublishStopIfDue();
            return;
        }
        lastRawForward = data.moveSpeed.z;

        bool justActivated;
        UpdateActivation(out justActivated);
        UpdateEmergencyStop();
        UpdateReverseMode();

        if (Input.GetKeyDown(recalibrateKey) && activationEnabled && HasFreshOdometry)
        {
            CalibrateHeading(rawBodyYawDeg);
        }

        if (justActivated)
        {
            if (HasFreshOdometry)
            {
                CalibrateHeading(rawBodyYawDeg);
            }
            else
            {
                headingCalibrated = false;
            }
        }

        if (!activationEnabled)
        {
            status = "PRESS TRIGGER TO START";
            // Always establish a new body-to-robot reference on activation.
            ResetMotionState(true);
            PublishStopIfDue();
            return;
        }

        if (emergencyStopEngaged || !enableRobotControl)
        {
            status = emergencyStopEngaged ? "EMERGENCY STOP" : "ROBOT CONTROL OFF";
            // The robot or user can change heading while control is suspended.
            ResetMotionState(true);
            PublishStopIfDue();
            return;
        }

        if (!HasFreshOdometry)
        {
            status = hasOdometry ? "ODOMETRY STALE" : "WAITING FOR ODOMETRY";
            headingCalibrated = false;
            if (stopWhenOdometryUnavailable)
            {
                ResetMotionState(true);
                PublishStopIfDue();
                return;
            }
        }

        if (!headingCalibrated && HasFreshOdometry)
        {
            CalibrateHeading(rawBodyYawDeg);
        }

        UpdateBodyHeading(rawBodyYawDeg);
        float forward = FilterForward(lastRawForward);
        if (reverseModeEnabled)
        {
            forward = -forward;
        }

        float linear = CalculateLinear(forward);
        float angular = CalculateAngular();
        linear *= CalculateTurningSpeedMultiplier();

        status = reverseModeEnabled ? "ACTIVE / REVERSE" : "ACTIVE";
        PublishIfDue(linear, angular);
        LogIfDue(rawBodyYawDeg, linear, angular);
    }

    private void CalibrateHeading(float bodyYawDeg)
    {
        robotYawReferenceDeg = robotYawDeg;
        desiredRobotYawDeg = robotYawDeg;
        headingErrorDeg = 0f;
        accumulatedBodyYawDeg = 0f;
        previousBodyYawDeg = bodyYawDeg;
        lastRawBodyYawDeg = bodyYawDeg;
        hasRawBodyYaw = true;
        rejectedBodyYawStepCount = 0;
        filteredBodyYawDeg = bodyYawDeg;
        filteredBodyYawRate = 0f;
        commandedAngular = 0f;
        headingCalibrated = true;
        Debug.Log($"Heading calibrated: KAT {bodyYawDeg:F1} deg -> robot {robotYawDeg:F1} deg.");
    }

    private void UpdateBodyHeading(float rawBodyYawDeg)
    {
        if (!hasRawBodyYaw)
        {
            lastRawBodyYawDeg = rawBodyYawDeg;
            filteredBodyYawDeg = rawBodyYawDeg;
            previousBodyYawDeg = rawBodyYawDeg;
            hasRawBodyYaw = true;
            return;
        }

        float rawStep = Mathf.DeltaAngle(lastRawBodyYawDeg, rawBodyYawDeg);
        if (Mathf.Abs(rawStep) > Mathf.Max(maxAcceptedBodyYawStepDeg, 1f))
        {
            rejectedBodyYawStepCount++;
            if (rejectedBodyYawStepCount < Mathf.Max(maxConsecutiveRejectedBodyYawSteps, 1))
            {
                // Keep the last valid raw reference. A following sane sample can
                // recover without importing this discontinuity into the target.
                filteredBodyYawRate = 0f;
                if (enableDebugLog && Time.unscaledTime >= nextBodyYawWarningTime)
                {
                    nextBodyYawWarningTime = Time.unscaledTime + 0.5f;
                    Debug.LogWarning(
                        $"Rejected KAT heading jump: {lastRawBodyYawDeg:F1} -> {rawBodyYawDeg:F1} " +
                        $"(step {rawStep:F1} deg). Robot target was not changed."
                    );
                }
                return;
            }

            // The same large step has now persisted for several consecutive
            // samples in a row, so this is a real heading change (e.g. a fast
            // spin), not a one-frame glitch. Adopt the new raw value as the
            // baseline so tracking can recover, but do NOT import the jump
            // itself into the filtered/accumulated heading -- that would
            // otherwise snap the robot target by the full jump size.
            if (enableDebugLog)
            {
                Debug.LogWarning(
                    $"KAT heading jump persisted for {rejectedBodyYawStepCount} samples " +
                    $"({lastRawBodyYawDeg:F1} -> {rawBodyYawDeg:F1}); accepting it as the new reference."
                );
            }
            lastRawBodyYawDeg = rawBodyYawDeg;
            filteredBodyYawDeg = rawBodyYawDeg;
            previousBodyYawDeg = rawBodyYawDeg;
            filteredBodyYawRate = 0f;
            rejectedBodyYawStepCount = 0;
            return;
        }

        rejectedBodyYawStepCount = 0;
        lastRawBodyYawDeg = rawBodyYawDeg;
        float yawBlend = 1f - Mathf.Exp(
            -Time.fixedDeltaTime / Mathf.Max(bodyYawFilterTime, 0.01f)
        );
        filteredBodyYawDeg = Mathf.LerpAngle(filteredBodyYawDeg, rawBodyYawDeg, yawBlend);

        float delta = Mathf.DeltaAngle(previousBodyYawDeg, filteredBodyYawDeg);
        previousBodyYawDeg = filteredBodyYawDeg;

        // While actively walking, gait sway feeds small, mostly-unintentional
        // heading changes into the target -- damp them so walking straight
        // actually stays straight. Standing still (not walking) is unaffected,
        // so turning sensitivity there stays exactly as tuned.
        bool isWalking = Mathf.Abs(lastRawForward) > walkDeadZone;
        float scaledDelta = isWalking ? delta * Mathf.Clamp01(walkingBodyYawAccumulationScale) : delta;
        accumulatedBodyYawDeg += scaledDelta;

        float rawRate = scaledDelta / Mathf.Max(Time.fixedDeltaTime, 0.001f);
        float blend = 1f - Mathf.Exp(-Time.fixedDeltaTime / Mathf.Max(bodyYawRateSmoothingTime, 0.01f));
        filteredBodyYawRate = Mathf.Lerp(filteredBodyYawRate, rawRate, blend);

        desiredRobotYawDeg = robotYawReferenceDeg
            + Mathf.Sign(bodyToRosYawSign) * accumulatedBodyYawDeg * bodyHeadingScale;
        headingErrorDeg = Mathf.DeltaAngle(robotYawDeg, desiredRobotYawDeg);
    }

    private bool TryGetHorizontalBodyYaw(Quaternion bodyRotation, out float yawDeg)
    {
        // Extract heading from the quaternion's horizontal forward direction.
        // This avoids Euler Y representation flips when the KAT quaternion also
        // contains pitch or roll.
        Vector3 forward = bodyRotation * Vector3.forward;
        forward.y = 0f;

        // As the body sensor tilts toward vertical (forward lean/bowing), this
        // horizontal projection shrinks toward zero length. Normalizing a
        // near-zero vector amplifies tiny quaternion noise into large, false
        // yaw swings -- this is a likely source of the "instant 80-130 degree
        // jump" events seen in testing even though the raw KAT sensor data
        // itself (as shown in KAT's own app) changes smoothly. Reject the
        // reading well before the vector gets that short instead of only at
        // the last-resort near-zero case.
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

    private float CalculateAngular()
    {
        float sign = Mathf.Sign(bodyToRosYawSign);
        float feedforward = 0f;
        if (Mathf.Abs(filteredBodyYawRate) > bodyYawRateDeadZoneDegPerSec)
        {
            float effectiveRate = filteredBodyYawRate
                - Mathf.Sign(filteredBodyYawRate) * bodyYawRateDeadZoneDegPerSec;
            feedforward = sign * effectiveRate * Mathf.Deg2Rad * yawRateFeedforwardGain;
        }

        float feedback = 0f;
        if (HasFreshOdometry && headingCalibrated && Mathf.Abs(headingErrorDeg) > headingErrorDeadZoneDeg)
        {
            float effectiveError = headingErrorDeg
                - Mathf.Sign(headingErrorDeg) * headingErrorDeadZoneDeg;
            feedback = effectiveError * Mathf.Deg2Rad * headingKp;
        }

        float target = Mathf.Clamp(feedforward + feedback, -maxAngularSpeed, maxAngularSpeed);
        commandedAngular = Mathf.MoveTowards(
            commandedAngular,
            target,
            Mathf.Max(maxAngularAcceleration, 0.01f) * Time.fixedDeltaTime
        );
        return commandedAngular;
    }

    private float CalculateTurningSpeedMultiplier()
    {
        // A heading error is meaningful only while odometry feedback and the
        // body-to-robot calibration are both valid. In feedforward fallback,
        // do not let an old error silently suppress forward motion.
        if (!HasFreshOdometry || !headingCalibrated)
        {
            return 1f;
        }

        float error = Mathf.Abs(headingErrorDeg);
        float range = Mathf.Max(stopForwardHeadingErrorDeg - slowDownHeadingErrorDeg, 0.1f);
        float t = Mathf.Clamp01((error - slowDownHeadingErrorDeg) / range);
        return Mathf.Lerp(1f, minimumTurningForwardMultiplier, t);
    }

    private void UpdateActivation(out bool justActivated)
    {
        bool pressed = ReadTrigger(activationHand) || (enableKeyboardFallback && Input.GetKey(activationKey));
        bool previous = activationEnabled;

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
        justActivated = !previous && activationEnabled;
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

    private bool OdometryIsStale()
    {
        return Time.unscaledTime - lastOdometryTime > Mathf.Max(odometryTimeout, 0.1f);
    }

    private void ResetMotionState(bool resetHeading)
    {
        filteredForwardSpeed = 0f;
        filteredBodyYawRate = 0f;
        commandedAngular = 0f;
        hasRawBodyYaw = false;
        if (resetHeading)
        {
            headingCalibrated = false;
            accumulatedBodyYawDeg = 0f;
        }
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
            $"KAT CLOSED LOOP | Raw={lastRawForward:F3} BodyRaw={rawBodyYawDeg:F1} " +
            $"BodyFiltered={filteredBodyYawDeg:F1} " +
            $"Robot={robotYawDeg:F1} Target={desiredRobotYawDeg:F1} Error={headingErrorDeg:F1} " +
            $"Linear={linear:F3} Angular={angular:F3} OdomFresh={HasFreshOdometry}"
        );
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
        GUI.Label(new Rect(20, 20, 900, 30), $"KAT: {status}", style);
        GUI.Label(
            new Rect(20, 50, 1100, 30),
            $"Robot {robotYawDeg:F1}°  Target {desiredRobotYawDeg:F1}°  Error {headingErrorDeg:F1}°  " +
            $"cmd_vel ({lastPublishedLinear:F3}, {lastPublishedAngular:F3})",
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
