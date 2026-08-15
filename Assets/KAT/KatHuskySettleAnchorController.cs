using UnityEngine;
using UnityEngine.XR;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
using RosMessageTypes.Nav;

/// <summary>
/// Variant of KatHuskyClosedLoopController that adds "settle anchoring":
/// instead of tracking absolute accumulated body rotation against the ONE
/// reference point set at calibration forever, this controller treats a
/// sustained pause -- the user has actually stopped walking AND body yaw
/// rate has been near zero for a while -- as a signal that "wherever you
/// currently are is correct" and quietly re-anchors the target to the
/// robot's current actual heading.
///
/// Why: the original accumulate-since-calibration model has no way to forget
/// small drift/noise picked up along the way (gait sway, sensor jitter) --
/// every bit of it stays baked into the target forever, which is why walking
/// "straight" could drift and require hunting for the original calibration
/// pose to get the robot going straight again. Re-anchoring whenever the user
/// pauses "clears the tab": whatever offset accumulated before that pause is
/// forgiven, and tracking starts fresh from the pose you actually ended up
/// holding. The anchor deliberately only arms once stepping has stopped --
/// gait sway can make the yaw rate look quiet for a stride or two in the
/// middle of a genuine walk-and-turn, and anchoring on that would chop a
/// continuous turn into interrupted pieces instead of only forgiving drift.
///
/// This is a separate file on purpose -- KatHuskyClosedLoopController.cs is
/// left completely untouched. Swap the component on the robot GameObject to
/// try this version; nothing else in the project needs to change since the
/// public ROS topics/behavior are identical.
/// </summary>
[DisallowMultipleComponent]
public class KatHuskySettleAnchorController : MonoBehaviour
{
    [Header("ROS")]
    public string cmdVelTopic = "/cmd_vel";
    public string odometryTopic = "/odometry/filtered";
    public float odometryTimeout = 0.5f;
    [Tooltip("For safety, do not drive when closed-loop odometry is missing or stale.")]
    public bool stopWhenOdometryUnavailable = true;

    [Header("Linear control")]
    public float maxForwardSpeed = 0.6f;
    public float maxReverseSpeed = 0.20f;
    public float walkDeadZone = 0.02f;
    public float inputSpeedForMax = 1.0f;
    public float minDriveSpeed = 0.08f;
    [Range(0.3f, 1f)] public float responseExponent = 0.60f;
    public float forwardAttackTime = 0.04f;
    public float forwardReleaseTime = 0.15f;

    [Header("Step-in-place control (Mini S / Walk C2)")]
    [Tooltip("Let the device-specific step signal drive forward even when moveSpeed stays near zero during a lift-and-step gait.")]
    public bool enableStepInPlaceDrive = true;
    [Tooltip("Synthetic KAT input used while isMoving is true. This still passes through the normal speed curve and turn-aware limiter.")]
    public float stepInPlaceInput = 0.35f;
    [Tooltip("Keep the step signal active briefly between alternating foot events so cmd_vel does not pulse every stride.")]
    public float stepSignalHoldTime = 0.15f;
    [Tooltip("For Walk C2, treat per-foot horizontal speed above this value as stepping when motionType is inconclusive.")]
    public float stepFootSpeedThreshold = 0.05f;
    [Tooltip("Mini S fallback: treat short moveSpeed bursts as footsteps when its extraData is unavailable/all zero.")]
    public bool enableMoveSpeedPulseStepFallback = true;
    [Tooltip("Horizontal moveSpeed magnitude required to register one fallback footstep pulse.")]
    public float stepPulseThreshold = 0.08f;
    [Tooltip("Keep forward intent alive this long after each moveSpeed pulse. Normal alternating steps refresh it; stopping still times out automatically.")]
    public float stepPulseHoldTime = 0.60f;

    [Header("Closed-loop heading")]
    [Tooltip("Use -1 when positive KAT yaw corresponds to negative ROS yaw.")]
    public float bodyToRosYawSign = -1f;
    [Tooltip("Robot yaw change requested for each degree of body yaw change.")]
    public float bodyHeadingScale = 1f;
    [Tooltip("Immediate body yaw-rate feedforward gain.")]
    public float yawRateFeedforwardGain = 0.15f;
    [Tooltip("Heading-error proportional gain, in 1/s.")]
    public float headingKp = 0.8f;
    public float headingErrorDeadZoneDeg = 2f;
    [Tooltip("Low-pass time for KAT horizontal heading. Larger values reject more jitter.")]
    public float bodyYawFilterTime = 0.10f;
    [Tooltip("Reject a single KAT heading jump larger than this. This prevents quaternion/Euler discontinuities from moving the robot target.")]
    public float maxAcceptedBodyYawStepDeg = 45f;
    [Tooltip("Consecutive rejected samples before a persistent KAT heading jump is treated as real (e.g. a fast spin) instead of noise, and adopted as the new reference.")]
    public int maxConsecutiveRejectedBodyYawSteps = 4;
    [Tooltip("Reject the body yaw reading when the KAT body sensor tilts more than this many degrees from horizontal (forward leaning/bowing). Near-vertical tilt makes the horizontal-yaw extraction numerically unstable and amplifies small sensor noise into large false heading jumps.")]
    public float maxBodyTiltFromHorizontalDeg = 70f;
    [Range(0f, 1f)]
    [Tooltip("Multiplier applied to body yaw changes while actively walking (raw forward speed above walkDeadZone) AND the body yaw rate is at/below swayYawRateThresholdDegPerSec. Natural gait sway feeds small, mostly-unintentional heading changes into the target while walking; a lower value damps that drift so 'walking straight' actually stays straight, without affecting turning sensitivity while standing still. Does NOT apply once the body is turning fast enough to clear turnIntentYawRateThresholdDegPerSec -- see those two fields.")]
    public float walkingBodyYawAccumulationScale = 0.4f;
    [Tooltip("Filtered body yaw rate at/below which a heading change while walking is still treated as gait sway and gets the full walkingBodyYawAccumulationScale damping.")]
    public float swayYawRateThresholdDegPerSec = 8f;
    [Tooltip("Filtered body yaw rate at/above which a heading change while walking is treated as a real, intentional turn and bypasses walkingBodyYawAccumulationScale entirely (full strength, same as standing still). Between swayYawRateThresholdDegPerSec and this value the scale blends linearly, so a walk-and-turn doesn't snap between damped and full strength.")]
    public float turnIntentYawRateThresholdDegPerSec = 30f;
    public float bodyYawRateDeadZoneDegPerSec = 4f;
    public float bodyYawRateSmoothingTime = 0.08f;
    public float maxAngularSpeed = 0.35f;
    public float maxAngularAcceleration = 1f;

    [Header("Settle anchoring")]
    [Tooltip("Once stepping has stopped (see walkDeadZone), body yaw rate below this is considered 'not actively turning'. Ignored entirely while still walking, so an in-progress walk-and-turn can never be settle-anchored.")]
    public float settledYawRateThresholdDegPerSec = 3f;
    [Tooltip("How long the body yaw rate must stay below the threshold, after stepping has stopped, before the current pose is accepted as the new reference (forgiving any drift accumulated before the pause).")]
    public float settledDurationSec = 0.3f;
    [Tooltip("Log a line each time a settle-anchor happens.")]
    public bool logSettleAnchor = true;

    [Header("Turn-aware forward speed")]
    [Tooltip("Forward speed begins decreasing above this heading error.")]
    public float slowDownHeadingErrorDeg = 10f;
    [Tooltip("Forward motion reaches its minimum multiplier at this error.")]
    public float stopForwardHeadingErrorDeg = 35f;
    [Tooltip("Forward speed floor at/above stopForwardHeadingErrorDeg while standing still (not walking). Kept low/zero to mirror how a real overground turn naturally comes with deceleration -- you physically can't sprint through a sharp turn.")]
    [Range(0f, 1f)] public float minimumTurningForwardMultiplier = 0f;
    [Tooltip("Forward speed floor at/above stopForwardHeadingErrorDeg while actively walking/marching. Higher than minimumTurningForwardMultiplier: marching in place isn't balance-limited the way real overground walking is, so a big turn shouldn't fully cut forward -- it should let you walk an arc through the turn instead of stopping dead.")]
    [Range(0f, 1f)] public float minimumTurningForwardMultiplierWhileWalking = 0.75f;

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
    public float debugLogInterval = 0.05f;
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
    private Vector3 lastRawMoveSpeed;
    private string lastDeviceName = "";
    private float nextPublishTime;
    private float nextDebugTime;
    private float nextBodyYawWarningTime;
    private string status = "STANDBY";

    // Settle-anchor state
    private float settledTimer;
    private bool hasSettledAnchorSinceMoving;
    private bool settleBodySettled;
    private bool settleHeadingConverged;

    // Exposed for direct settle-based testing/detection: don't infer timing
    // from GUI numbers, watch these instead (or grep the Debug.Log lines).
    public bool SettleBodySettled => settleBodySettled;
    public bool SettleHeadingConverged => settleHeadingConverged;
    public float SettledTimer => settledTimer;
    public bool HasSettledAnchorSinceMoving => hasSettledAnchorSinceMoving;

    // Device-specific extra data supplies the lift-and-step signal that
    // moveSpeed can miss. Mini S and Walk C2 use different binary layouts.
    private MiniSExtraData.extraInfo miniSExtra;
    private bool hasMiniSExtra;
    private WalkC2ExtraData.extraInfo walkC2Extra;
    private bool hasWalkC2Extra;
    private bool rawStepSignal;
    private float lastStepSignalTime = -999f;
    private float lastMoveSpeedPulseTime = -999f;
    private bool stepInPlaceActive;
    private bool moveSpeedPulseActive;

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
            hasMiniSExtra = false;
            hasWalkC2Extra = false;
            rawStepSignal = false;
            lastStepSignalTime = -999f;
            lastMoveSpeedPulseTime = -999f;
            stepInPlaceActive = false;
            moveSpeedPulseActive = false;
            ResetMotionState(true);
            PublishStopIfDue();
            return;
        }

        lastDeviceName = data.deviceName ?? "";
        ReadStepSignal(data);

        if (!TryGetHorizontalBodyYaw(data.bodyRotationRaw, out float rawBodyYawDeg))
        {
            status = "INVALID KAT HEADING";
            ResetMotionState(true);
            PublishStopIfDue();
            return;
        }
        // moveSpeed is documented as "Target Move Speed With Direction" -- it's
        // a 2D step vector on the platform's horizontal plane, not just a
        // forward scalar. Turning while stepping shifts stepping energy from
        // .z into .x (relative to the device's own fixed axes), so reading
        // .z alone makes walking look like it stopped mid-turn even though
        // you're still stepping just as hard. Use the combined horizontal
        // magnitude as the walking intensity instead, so a turn can't make
        // "how hard you're stepping" disappear. Direction (forward/backward)
        // is left to the explicit reverse-mode toggle rather than the raw
        // sign of moveSpeed, since during a turn that sign is not a reliable
        // indicator of intent.
        lastRawMoveSpeed = data.moveSpeed;
        lastRawForward = new Vector2(lastRawMoveSpeed.x, lastRawMoveSpeed.z).magnitude;

        if (enableMoveSpeedPulseStepFallback
            && lastRawForward >= Mathf.Max(stepPulseThreshold, walkDeadZone))
        {
            lastMoveSpeedPulseTime = Time.unscaledTime;
        }
        moveSpeedPulseActive = enableMoveSpeedPulseStepFallback
            && Time.unscaledTime - lastMoveSpeedPulseTime <= Mathf.Max(stepPulseHoldTime, 0f);

        // KAT I/O's step mode can report a real lift-and-step gait through
        // device-specific extra data while the slide/friction-derived
        // moveSpeed remains near zero. Convert that discrete gait signal into
        // a steady forward input, with a short hold to bridge alternating-foot
        // samples. The existing response curve, reverse toggle, heading limiter
        // and all safety gates still apply below.
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
        settledTimer = 0f;
        hasSettledAnchorSinceMoving = false;
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
        //
        // A flat scale here can't tell gait sway apart from a genuine
        // walk-and-turn -- both look like "heading is changing while
        // lastRawForward is above the walk dead zone". Gate the scale by how
        // fast the body is already turning, using LAST frame's already-
        // smoothed filteredBodyYawRate (not this frame's raw delta) so the
        // gate itself doesn't reintroduce single-sample jitter: slow drift
        // stays damped by walkingBodyYawAccumulationScale, a sustained fast
        // turn rises toward full (1x) strength, and the band between the two
        // thresholds blends linearly so a walk-and-turn ramps in smoothly
        // instead of snapping between damped and full strength.
        bool isWalking = Mathf.Abs(lastRawForward) > walkDeadZone;
        float turnIntentBlend = Mathf.InverseLerp(
            swayYawRateThresholdDegPerSec,
            Mathf.Max(turnIntentYawRateThresholdDegPerSec, swayYawRateThresholdDegPerSec + 0.01f),
            Mathf.Abs(filteredBodyYawRate));
        float walkingYawScale = Mathf.Lerp(Mathf.Clamp01(walkingBodyYawAccumulationScale), 1f, turnIntentBlend);
        float scaledDelta = isWalking ? delta * walkingYawScale : delta;
        accumulatedBodyYawDeg += scaledDelta;

        float rawRate = scaledDelta / Mathf.Max(Time.fixedDeltaTime, 0.001f);
        float blend = 1f - Mathf.Exp(-Time.fixedDeltaTime / Mathf.Max(bodyYawRateSmoothingTime, 0.01f));
        filteredBodyYawRate = Mathf.Lerp(filteredBodyYawRate, rawRate, blend);

        desiredRobotYawDeg = robotYawReferenceDeg
            + Mathf.Sign(bodyToRosYawSign) * accumulatedBodyYawDeg * bodyHeadingScale;
        headingErrorDeg = Mathf.DeltaAngle(robotYawDeg, desiredRobotYawDeg);

        UpdateSettleAnchor();
    }

    /// <summary>
    /// Watches the body yaw rate. Once the user has actually stopped walking
    /// AND stayed below settledYawRateThresholdDegPerSec for settledDurationSec
    /// straight, the robot's CURRENT actual heading is accepted as the new
    /// zero reference -- any drift accumulated before this pause is forgiven
    /// rather than being carried forward forever. Fires once per settle
    /// (re-arms only after the body starts actively turning again), so it
    /// doesn't spam every frame while you're standing still.
    /// </summary>
    private void UpdateSettleAnchor()
    {
        // Yaw-rate-quiet alone is NOT enough while actively walking: gait sway
        // routinely drops the filtered yaw rate below the threshold for a
        // stride or two in the middle of a perfectly intentional, continuous
        // walk-and-turn -- anchoring there would erase the still-in-progress
        // turn instead of only forgiving settled drift, chopping "walk while
        // turning" into a series of interrupted micro-turns. Require the user
        // to have actually stopped stepping (same walkDeadZone used for drive
        // output) before a quiet yaw rate is trusted as "really done turning".
        bool isWalking = Mathf.Abs(lastRawForward) > walkDeadZone;

        // Body-rate-quiet-while-stopped still isn't enough on its own: the
        // user routinely twists then holds still WHILE the robot is still
        // slewing (rate-limited) toward an outstanding turn command.
        // Anchoring on body-quiet alone would erase that still-pending turn
        // instead of only forgiving settled drift. Require the heading error
        // to already be converged too, so a settle can only register once the
        // robot has actually caught up.
        settleBodySettled = !isWalking && Mathf.Abs(filteredBodyYawRate) < settledYawRateThresholdDegPerSec;
        settleHeadingConverged = !headingCalibrated || Mathf.Abs(headingErrorDeg) <= headingErrorDeadZoneDeg;

        if (settleBodySettled && settleHeadingConverged)
        {
            settledTimer += Time.fixedDeltaTime;
            if (!hasSettledAnchorSinceMoving && settledTimer >= Mathf.Max(settledDurationSec, 0f))
            {
                RegisterSettleAnchor();
                hasSettledAnchorSinceMoving = true;
            }
        }
        else
        {
            settledTimer = 0f;
            hasSettledAnchorSinceMoving = false;
        }
    }

    private void RegisterSettleAnchor()
    {
        // Captured before being reset below -- this is the direct test signal:
        // a correctly-gated settle should never fire with a meaningful error
        // still outstanding. Grep logs for "PREMATURE" instead of eyeballing
        // GUI numbers against a stopwatch.
        float errorAtFire = headingErrorDeg;

        robotYawReferenceDeg = robotYawDeg;
        accumulatedBodyYawDeg = 0f;
        desiredRobotYawDeg = robotYawDeg;
        headingErrorDeg = 0f;

        if (Mathf.Abs(errorAtFire) > headingErrorDeadZoneDeg + 0.01f)
        {
            Debug.LogWarning(
                $"[SETTLE-ANCHOR TEST] PREMATURE settle: fired with {errorAtFire:F1} deg of heading error " +
                $"still outstanding (dead zone is {headingErrorDeadZoneDeg:F1} deg). A pending turn was likely " +
                $"truncated -- this should not happen; check the settleHeadingConverged gate."
            );
        }
        else if (enableDebugLog && logSettleAnchor)
        {
            Debug.Log(
                $"Settle-anchor: body held steady, robot re-anchored at {robotYawDeg:F1} deg " +
                $"(error at fire {errorAtFire:F1} deg, prior drift forgiven)."
            );
        }
    }

    private static bool TryGetMiniSExtraInfo(KATNativeSDK.TreadMillData data, out MiniSExtraData.extraInfo info)
    {
        // extraData is a raw marshaled byte blob -- only meaningful when the
        // connected device actually is a Walk Mini S. Guard against a mid-air
        // marshal exception (wrong device, malformed/short buffer) taking
        // down the whole control loop over what is, for now, diagnostic-only
        // data.
        try
        {
            info = MiniSExtraData.GetExtraInfoMiniS(data);
            return true;
        }
        catch
        {
            info = default;
            return false;
        }
    }

    private void ReadStepSignal(KATNativeSDK.TreadMillData data)
    {
        hasMiniSExtra = false;
        hasWalkC2Extra = false;
        rawStepSignal = false;

        // The official SDK identifies Walk C2 as "Coord2" and gives it a
        // different extraData layout. Parsing that blob as Mini S makes every
        // useful field appear false/zero, which is exactly what the Console
        // showed. Select the layout from deviceName before reading it.
        if (!string.IsNullOrEmpty(data.deviceName)
            && data.deviceName.IndexOf("Coord2", System.StringComparison.OrdinalIgnoreCase) >= 0)
        {
            try
            {
                walkC2Extra = WalkC2ExtraData.GetExtraInfoC2(data);
                hasWalkC2Extra = true;

                // SDK demo: 3 = MOTION_MICROACTION, 4 = MOTION_MOVE.
                // Both represent active stepping for robot-drive purposes.
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

        hasMiniSExtra = TryGetMiniSExtraInfo(data, out miniSExtra);
        rawStepSignal = hasMiniSExtra && miniSExtra.isMoving;
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
        // yaw swings. Reject the reading well before the vector gets that
        // short instead of only at the last-resort near-zero case.
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

        // Standing still and turning a lot mirrors a real sharp overground
        // turn -- deceleration is natural there, so forward can bottom out at
        // (near) zero. Actively walking/marching is not balance-limited the
        // same way, so a big turn shouldn't fully kill forward -- use the
        // more forgiving floor so "march in place + twist" can actually walk
        // an arc through the turn instead of stalling until the turn finishes.
        bool isWalking = Mathf.Abs(lastRawForward) > walkDeadZone;
        float floor = isWalking ? minimumTurningForwardMultiplierWhileWalking : minimumTurningForwardMultiplier;
        return Mathf.Lerp(1f, floor, t);
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
        settledTimer = 0f;
        hasSettledAnchorSinceMoving = false;
        settleBodySettled = false;
        settleHeadingConverged = false;
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
            $"KAT CLOSED LOOP | Raw={lastRawForward:F3} PulseStep={moveSpeedPulseActive} MoveSpeed=({lastRawMoveSpeed.x:F3},{lastRawMoveSpeed.y:F3},{lastRawMoveSpeed.z:F3}) BodyRaw={rawBodyYawDeg:F1} " +
            $"BodyFiltered={filteredBodyYawDeg:F1} " +
            $"Robot={robotYawDeg:F1} Target={desiredRobotYawDeg:F1} Error={headingErrorDeg:F1} " +
            $"Linear={linear:F3} Angular={angular:F3} OdomFresh={HasFreshOdometry}"
        );

        if (hasMiniSExtra)
        {
            Debug.Log(
                $"KAT MINI-S EXTRA | Device={lastDeviceName} isMoving={miniSExtra.isMoving} StepDrive={stepInPlaceActive} isForward={miniSExtra.isForward} " +
                $"LGround={miniSExtra.isLeftGround} RGround={miniSExtra.isRightGround} " +
                $"LStatic={miniSExtra.isLeftStatic} RStatic={miniSExtra.isRightStatic} " +
                $"Skating=({miniSExtra.skatingSpeed.x:F3},{miniSExtra.skatingSpeed.y:F3},{miniSExtra.skatingSpeed.z:F3}) " +
                $"LFoot=({miniSExtra.lFootSpeed.x:F3},{miniSExtra.lFootSpeed.y:F3},{miniSExtra.lFootSpeed.z:F3}) " +
                $"RFoot=({miniSExtra.rFootSpeed.x:F3},{miniSExtra.rFootSpeed.y:F3},{miniSExtra.rFootSpeed.z:F3})"
            );
        }
        else if (hasWalkC2Extra)
        {
            Debug.Log(
                $"KAT WALK-C2 EXTRA | Device={lastDeviceName} MotionType={walkC2Extra.motionType} " +
                $"StepSignal={rawStepSignal} StepDrive={stepInPlaceActive} " +
                $"LGround={walkC2Extra.isLeftGround} RGround={walkC2Extra.isRightGround} " +
                $"LStatic={walkC2Extra.isLeftStatic} RStatic={walkC2Extra.isRightStatic} " +
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
        GUI.Label(new Rect(20, 20, 900, 30), $"KAT: {status}", style);
        GUI.Label(
            new Rect(20, 50, 1100, 30),
            $"Robot {robotYawDeg:F1}°  Target {desiredRobotYawDeg:F1}°  Error {headingErrorDeg:F1}°  " +
            $"cmd_vel ({lastPublishedLinear:F3}, {lastPublishedAngular:F3})",
            style
        );
        // Direct settle-detection readout for testing: watch this instead of
        // timing GUI numbers by eye. BodySettled/HeadingConverged are the two
        // AND-ed gate conditions; Timer counts up only while both hold, and an
        // anchor can only fire once it reaches settledDurationSec.
        GUI.Label(
            new Rect(20, 80, 1100, 30),
            $"Settle: BodySettled={settleBodySettled} HeadingConverged={settleHeadingConverged} " +
            $"Timer={settledTimer:F2}/{settledDurationSec:F2} Armed={!hasSettledAnchorSinceMoving}",
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
