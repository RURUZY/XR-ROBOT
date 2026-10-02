using UnityEngine;
using UnityEngine.XR;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
using RosMessageTypes.Nav;

/// <summary>
/// Derived from the working Direct controller, not the HeadingFollow state machine.
/// Forward: horizontal target speed + device step inputs. Speed-pulse tails
/// decay at measured strength; crossing the pulse threshold never boosts speed.
/// Turn: the body yaw ANGLE is low-passed, then both the 1:1 target and the
/// feedforward rate are taken from that filtered angle -- the raw reading is
/// quantized to whole degrees and carries several degrees of gait sway, and
/// feeding it in raw is what made the heading wander while merely stepping.
/// A low-pass on an accumulating angle only lags, so the finished turn angle is
/// unchanged. Target error determines turn direction. Same-direction body-rate
/// feedforward maps 90 deg/s to about 1.57 rad/s; cumulative
/// 1:1 feedback uses gentle gain near the target and faster bounded catch-up
/// farther away. Measured yaw rate anticipates braking. Published turn commands
/// ramp up/down; faults and explicit stops still publish zero immediately.
/// Stopping body rotation retains the target until the robot catches up: a
/// person out-turns the chassis by several times, so a fast 360 completes
/// afterwards rather than being lost.
/// No walking-dependent angle gain, automatic settle anchor, or KAT timestamp gate.
/// Like the vendor walker/Direct controller, KAT availability uses connected and
/// finite input checks. lastUpdateTimePoint is logged, not assumed to be a heartbeat.
/// This cannot detect a SDK that falsely reports connected while replaying data.
/// Fresh odometry is required for ANGULAR output only; losing it never
/// suppresses the forward channel. Transients (odometry gap, EKF/frame jump,
/// body-yaw glitch, invalid reading) resync or re-anchor the reference by
/// themselves -- a short dropout keeps the pending turn. A stable new body
/// sensor origin preserves accepted intent without inventing motion during the
/// gap; a long odometry gap still drops intent that cannot be tracked. Nothing latches into a state that
/// needs a button press any more; that behaviour silently killed turning for
/// the rest of a session after one transient mid-spin.
/// Trigger toggles start/pause (activationLatch=false offers hold-to-run).
/// Primary right button: software stop; secondary right: reverse; secondary left
/// or optional C: pause for realignment; next activation takes a fresh reference.
/// Each explicit activation starts a new heading reference, as in the old variants.
/// Disable other cmd_vel publishers before manually attaching this version.
/// No occupancy sensor is available: pause before dismounting. A robot-side command
/// watchdog remains necessary for loss of Unity/network. No scene is changed here.
/// </summary>
[DisallowMultipleComponent]
public class KatHuskyDirectAngleController : MonoBehaviour
{
    [Header("ROS")]
    public string cmdVelTopic = "/cmd_vel";
    [Tooltip("Heading feedback only. Forward motion remains independent of this topic.")]
    public string odometryTopic = "/odometry/filtered";
    public float odometryTimeout = 0.5f;
    [Tooltip("Stop commands immediately on invalid KAT data, but retain activation through a short dropout. Longer faults require a fresh start.")]
    public float katInputRecoveryGraceTime = 1f;

    [Header("Forward channel (independent of turn)")]
    public float maxForwardSpeed = 0.8f;
    public float maxReverseSpeed = 0.35f;
    public float walkDeadZone = 0.02f;
    [Tooltip("Mini S SDK horizontal speed that maps to maxForwardSpeed. Field capture reached about 3.45; 3.0 avoids saturating most ordinary steps.")]
    public float inputSpeedForMax = 3.0f;
    public float minDriveSpeed = 0.08f;
    [Tooltip("Input interval above the dead zone over which the minimum drive contribution ramps in. Avoids an instant 0-to-minDriveSpeed jump.")]
    public float forwardStartRampInput = 0.02f;
    [Range(0.3f, 1f)] public float responseExponent = 0.60f;
    [Tooltip("0 means no low-pass: even a one-tick step above the dead zone responds immediately.")]
    public float forwardAttackTime = 0f;
    public float forwardReleaseTime = 0f;

    [Header("Step-in-place control (Mini S / Walk C2)")]
    [Tooltip("Let the device-specific step signal drive forward even when moveSpeed stays near zero during a lift-and-step gait. Only ever affects the forward channel.")]
    public bool enableStepInPlaceDrive = true;
    public float stepInPlaceInput = 0.35f;
    public float stepSignalHoldTime = 0.10f;
    public float stepFootSpeedThreshold = 0.05f;
    public bool enableMoveSpeedPulseStepFallback = true;
    public float stepPulseThreshold = 0.08f;
    [Tooltip("Maximum duration of a decaying speed-pulse tail. Mini S emits step/pause pulses with measured gaps up to about 0.9 s; 0.55 s bridges ordinary cadence while still stopping promptly.")]
    public float stepPulseHoldTime = 0.55f;

    [Header("Turn channel (independent of forward)")]
    [Tooltip("Use -1 when positive KAT yaw corresponds to negative ROS yaw.")]
    public float bodyToRosYawSign = -1f;
    [Tooltip("Dead zone for rate feedforward only. Slow body turns still accumulate fully in the 1:1 target, so raising this costs nothing in final angle -- it only keeps gait sway and sampling noise out of the instant feedforward.")]
    public float turnRateDeadZoneDegPerSec = 6f;
    [Tooltip("Low-pass applied to the raw body yaw ANGLE before it is differentiated into a rate AND before it becomes the 1:1 target. KAT's raw body yaw reads out quantized to whole degrees (field logs: BodyRaw only ever printed .0) and FixedUpdate polls it on a clock unrelated to the SDK's own update rate, so the raw signal carries 1-2 deg steps that are pure artifacts. Differentiating that directly makes every step a huge instantaneous rate (1 deg in one 50 Hz tick = 50 deg/s), which is what swings the robot left and right while the operator is only stepping. A first-order low-pass on an ACCUMULATING angle converges to the same total, so this is a lag, not a loss of angle.")]
    public float bodyYawFilterTime = 0.06f;
    [Tooltip("Extra multiplier on bodyYawFilterTime while the step-in-place signal is active. Walking or pivoting in place swings the hips and the body ring several degrees per stride; that is a real rotation of the sensor but not an intent to turn. 1 disables the step-dependent behaviour.")]
    [Min(1f)] public float stepTurnFilterMultiplier = 1f;
    [Tooltip("0 disables the second smoothing stage; the angle is already filtered.")]
    public float turnRateSmoothingTime = 0.10f;
    [Tooltip("Body rate reaching the feedforward limit; 90 deg/s maps to about 1.57 rad/s.")]
    public float turnRateForMaxOutput = 90f;
    public float turnResponseExponent = 1f;
    public float openLoopMaxAngularSpeed = 1.5708f;
    [Tooltip("Hard clamp on published angular.z in rad/s. This is a software command limit, not a measured hardware limit or a guarantee of actual yaw speed.")]
    public float maxAngularSpeed = 1.6f;
    [Tooltip("Normal published angular command acceleration, rad/s squared. Faults and explicit stops bypass this ramp.")]
    public float turnAcceleration = 1.5f;
    public float turnDeceleration = 3f;
    [Header("Angle correction (turn only)")]
    public float headingKp = 1.2f;
    public float nearHeadingKp = 0.4f;
    [Tooltip("Within this angle use the gentle near-target gain. Beyond it add the larger gain continuously, without a speed step.")]
    public float headingSlowZoneDeg = 15f;
    [Tooltip("Seconds of measured robot rotation to allow for when reducing correction near the target. Uses odometry-derived yaw rate.")]
    public float turnBrakingLookaheadTime = 0.35f;
    [Tooltip("Maximum angle catch-up command, rad/s. The near-target gain still slows small corrections.")]
    public float maxHeadingCorrectionSpeed = 1.2f;
    public float headingToleranceDeg = 2f;
    [Tooltip("Cap on the proportional error, NOT accumulated turns. After live input stops, catch-up is limited by maxHeadingCorrectionSpeed.")]
    public float maxHeadingErrorDeg = 120f;
    [Tooltip("Stop turning if a sustained turn command produces no measured progress for this many seconds. Large but progressing turns are allowed. A trip re-anchors the target instead of demanding a manual realign, so a blocked robot stops instead of spinning forever.")]
    [Min(0.5f)] public float turnProgressTimeout = 5f;
    [Min(0.1f)] public float minimumTurnProgressDeg = 1f;
    [Tooltip("A body-yaw sample implying more than this rate is a sensor/quaternion glitch and gets skipped (the repeated 135-139 deg jumps in the field logs). A RATE limit rather than a fixed step keeps genuinely fast turns across a frame hitch: 0.2 s of a 300 deg/s spin is a legitimate 60 deg step and must not be thrown away, or a fast 360 silently loses part of its angle.")]
    public float maxBodyYawRateDegPerSec = 720f;
    [Tooltip("Floor for the rate limit above, so a tick with near-zero elapsed time still accepts a normal step.")]
    public float maxAcceptedBodyYawStepDeg = 45f;
    [Tooltip("Short-gap recovery interval. A longer body gap waits for stable readings and rebases only the sensor, preserving accepted intent. A longer odometry gap drops pending intent because actual rotation during the gap is unknown.")]
    public float turnResyncGraceTime = 1f;
    [Tooltip("A persistent body-yaw offset must stay within a small band for this duration before rebasing only the sensor. Already accepted turn intent is retained; motion during the invalid interval is unknown.")]
    public float bodyYawRecoveryStableTime = 0.3f;
    public float bodyYawRecoveryBandDeg = 3f;
    [Tooltip("Invalid heading stops turning until the reading recovers. Forward remains independent.")]
    public float maxBodyTiltFromHorizontalDeg = 70f;

    [Header("Activation and safety")]
    public bool enableRobotControl = true;
    public XRNode activationHand = XRNode.RightHand;
    public float activationTriggerThreshold = 0.2f;
    public float activationTriggerReleaseThreshold = 0.1f;
    public float activationTriggerReleaseTime = 0.08f;
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
    public KeyCode realignKey = KeyCode.C;
    public XRNode realignHand = XRNode.LeftHand;

    [Header("Diagnostics")]
    public bool enableDebugLog = true;
    public float debugLogInterval = 0.05f;
    [Tooltip("Legacy IMGUI debug overlay. Not stereo-aware -- in a VR headset it gets drawn once per eye and shows up as doubled/ghosted text. Fine for flat desktop/Editor testing; leave off for real headset testing and use KatStatusHud instead.")]
    public bool showStatusGUI = false;

    // Read-only accessors for KatStatusHud (or any other VR-safe display) --
    // OnGUI below is not stereo-safe, this is the same data without that
    // problem.
    public string Status => status;
    public float FilteredYawRateDegPerSec => filteredYawRateDegPerSec;
    public float LastPublishedLinear => lastPublishedLinear;
    public float LastPublishedAngular => lastPublishedAngular;
    public float LiveTurnDemand { get; private set; }
    public float CorrectionTurnDemand { get; private set; }
    public float RobotYawDeg => robotYawDeg;
    public bool HasOdometry => hasOdometry;
    public bool HasFreshOdometry => hasOdometry && odometryUsable && Time.realtimeSinceStartup - lastOdometryTime <= Mathf.Max(odometryTimeout, 0.05f);
    public bool HeadingCalibrated => headingCalibrated;
    // The TARGET follows the FILTERED body turn, not the raw one: the raw angle
    // carries whole-degree quantization plus several degrees of gait sway, and
    // feeding that straight into the setpoint is what made the robot twitch and
    // wander while the operator was only stepping. A low-pass on an
    // accumulating value is a lag, so a finished turn still lands on the same
    // 1:1 angle.
    public double DesiredRobotYawDeg => robotReference + headingSign * filteredBodyTurnDeg;
    public double HeadingErrorDeg => headingCalibrated ? DesiredRobotYawDeg - robotUnwrapped : double.NaN;
    public double BodyTurnDeg => headingCalibrated ? bodyTurnDeg : double.NaN;
    public double TargetTurnDeg => headingCalibrated ? headingSign * filteredBodyTurnDeg : double.NaN;
    public double RobotTurnDeg => headingCalibrated ? robotUnwrapped - robotReference : double.NaN;
    public string TurnStatus => turnStatus;

    private ROSConnection ros;

    // Odometry is read only by the turn channel.
    private bool hasOdometry;
    private bool odometryUsable, odometryClockReset, bodyHeadingUsable;
    private float robotYawDeg;
    private double robotUnwrapped, robotReference, bodyTurnDeg, filteredBodyTurnDeg;
    private float lastOdometryTime = -999f, headingSign;
    private double lastOdometryStamp = double.NegativeInfinity;
    private string odomFrame, odomChildFrame;
    private bool headingCalibrated, previousRealign;
    private string turnStatus = "WAITING FOR HEADING";
    private double lastKatTimestamp;
    private bool hasDiagnosticPose;
    private Quaternion previousDiagnosticPose;
    private float previousDiagnosticYaw, previousDiagnosticTime, nextPoseDiagnosticTime;
    private double previousDiagnosticStamp;
    private bool watchingTurnProgress;
    private double progressReferenceYaw;
    private float progressStartTime, progressDirection;

    private bool activationEnabled;
    private bool lastActivationButton;
    private bool physicalTriggerHeld;
    private float triggerReleaseSince = -1f;
    private float katInputFaultSince = -1f;
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
    private float filteredYawRateDegPerSec;
    private float lastBodyYawSampleTime = -999f;
    private float lastBodyYawValidTime = -999f;
    private float bodyYawRejectStartTime = -1f;
    private float rejectedBodyStepLimit;
    private float recoveryCandidateYaw, recoveryCandidateSince = -1f;
    private float measuredRobotYawRate;
    private float lastAngularPublishTime = -999f;

    // Step-in-place state (forward channel only).
    private bool hasMiniSExtra;
    private MiniSExtraData.extraInfo miniSExtra;
    private bool hasWalkC2Extra;
    private WalkC2ExtraData.extraInfo walkC2Extra;
    private bool rawStepSignal;
    private float lastStepSignalTime = -999f;
    private float lastMoveSpeedPulseTime = -999f;
    private float lastMoveSpeedPulseInput;
    private bool stepInPlaceActive;
    private bool moveSpeedPulseActive;

    private string lastDeviceName = "";
    private float lastPublishedLinear;
    private float lastPublishedAngular;
    private float nextPublishTime;
    private float nextDebugTime;
    private float nextBodyYawWarningTime;
    private string status = "STANDBY";

    private void OnEnable()
    {
        activationEnabled = false;
        physicalTriggerHeld = lastActivationButton = false;
        triggerReleaseSince = katInputFaultSince = -1f;
        hasOdometry = false;
        odometryUsable = odometryClockReset = false;
        lastOdometryStamp = double.NegativeInfinity;
        ResetAllState();
    }

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
        if (this == null || !isActiveAndEnabled) return;
        if (message == null || message.header == null || message.header.stamp == null
            || message.pose == null || message.pose.pose == null || message.pose.pose.orientation == null)
        {
            SuspendOdometry("INVALID ODOMETRY (TURN OFF)");
            return;
        }
        QuaternionMsg q = message.pose.pose.orientation;
        double norm = q.w * q.w + q.x * q.x + q.y * q.y + q.z * q.z;
        double stamp = message.header.stamp.sec + message.header.stamp.nanosec * 1e-9;
        if (!Finite(norm) || norm < 1e-12 || stamp <= 0 || message.header.stamp.nanosec >= 1000000000)
        {
            SuspendOdometry("INVALID ODOMETRY (TURN OFF)");
            return;
        }
        if (stamp == lastOdometryStamp) return;
        if (stamp < lastOdometryStamp)
        {
            odometryClockReset = true;
            lastOdometryStamp = stamp;
            SuspendOdometry("ODOMETRY CLOCK RESET (TURN OFF)");
            return;
        }
        float yaw = (float)(System.Math.Atan2(2 * (q.w * q.z + q.x * q.y) / norm,
            1 - 2 * (q.y * q.y + q.z * q.z) / norm) * Mathf.Rad2Deg);
        float step = Mathf.DeltaAngle(robotYawDeg, yaw);
        bool discontinuity = hasOdometry && (odometryClockReset || odomFrame != message.header.frame_id
            || odomChildFrame != message.child_frame_id || Mathf.Abs(step) > 45f);
        bool hadOdometry = hasOdometry;
        float gap = Time.realtimeSinceStartup - lastOdometryTime;
        // A short gap in the SAME frame still contains real robot motion.
        // Only known frame/clock/large-step discontinuities shift the reference.
        bool longGap = gap > Mathf.Max(turnResyncGraceTime, 0f);
        bool continuous = hasOdometry && !longGap && !discontinuity;
        if (continuous && gap > 0.001f)
            measuredRobotYawRate = Mathf.Lerp(measuredRobotYawRate, step / gap, Blend(0.1f, gap));
        else if (!continuous)
            measuredRobotYawRate = 0f;
        double previousUnwrapped = robotUnwrapped;
        robotUnwrapped = continuous ? robotUnwrapped + step : yaw;
        robotYawDeg = yaw;
        odomFrame = message.header.frame_id;
        odomChildFrame = message.child_frame_id;
        lastOdometryStamp = stamp;
        lastOdometryTime = Time.realtimeSinceStartup;
        hasOdometry = true;
        odometryUsable = true;
        odometryClockReset = false;

        // True reference discontinuities preserve the pending relative turn.
        // Long gaps discard it because orientation alone cannot recover turns.
        if (hadOdometry && !continuous && headingCalibrated)
        {
            watchingTurnProgress = false;
            if (longGap)
                ReanchorTurnTarget("ODOMETRY GAP / TARGET RE-ANCHORED");
            else
            {
                robotReference += robotUnwrapped - previousUnwrapped;
                turnStatus = "ODOMETRY RESYNCED";
            }
        }
    }

    private void Update()
    {
        // Sample button edges in Update rather than missing short presses between
        // physics ticks. A pause clears pulse holds and publishes zero immediately.
        bool wasActive = activationEnabled;
        bool wasStopped = emergencyStopEngaged;
        bool wasReverse = reverseModeEnabled;
        UpdateEmergencyStop();
        UpdateActivation();
        UpdateReverseMode();
        bool realign = ReadButton(realignHand, CommonUsages.secondaryButton)
            || (enableKeyboardFallback && Input.GetKey(realignKey));
        if (realign && !previousRealign) activationEnabled = false;
        if (emergencyStopEngaged || wasStopped != emergencyStopEngaged || !enableRobotControl)
            activationEnabled = false;
        if (wasActive != activationEnabled || wasStopped != emergencyStopEngaged
            || (realign && !previousRealign))
        {
            if (enableDebugLog)
                Debug.Log($"[KAT DIRECT ANGLE] Activation={activationEnabled}; stop={emergencyStopEngaged}; realign={realign}; trigger={lastActivationButton}");
            ResetAllState();
            Publish(0f, 0f);
        }
        if (wasReverse != reverseModeEnabled) ResetForwardIntent();
        previousRealign = realign;
    }

    private void FixedUpdate()
    {
        KATNativeSDK.TreadMillData data;
        try { data = KATNativeSDK.GetWalkStatus(); }
        catch (System.Exception ex)
        {
            HandleKatInputFault("KAT SDK ERROR: " + ex.GetType().Name);
            return;
        }
        LogPoseDiagnostic(data);
        if (!data.connected)
        {
            HandleKatInputFault("KAT DISCONNECTED");
            return;
        }

        lastDeviceName = data.deviceName ?? "";
        lastKatTimestamp = data.lastUpdateTimePoint; // diagnostic, not a heartbeat gate
        ReadStepSignal(data);

        // Forward channel: computed unconditionally from moveSpeed/step
        // signal. It does not depend on whether the body yaw reading below
        // is valid this tick.
        lastRawMoveSpeed = data.moveSpeed;
        lastRawForward = new Vector2(lastRawMoveSpeed.x, lastRawMoveSpeed.z).magnitude;
        if (!Finite(lastRawForward))
        {
            HandleKatInputFault("INVALID WALK INPUT");
            return;
        }
        if (katInputFaultSince >= 0f)
        {
            // Also check elapsed time on recovery: there may have been no ticks
            // during a long frame stall. Never resume an old activation then.
            if (Time.realtimeSinceStartup - katInputFaultSince > Mathf.Max(katInputRecoveryGraceTime, 0f))
                activationEnabled = false;
            if (enableDebugLog)
                Debug.Log($"[KAT DIRECT ANGLE] KAT input recovered; activation retained={activationEnabled}");
            katInputFaultSince = -1f;
        }
        if (!activationEnabled || emergencyStopEngaged || !enableRobotControl)
        {
            status = emergencyStopEngaged ? "EMERGENCY STOP"
                : !enableRobotControl ? "ROBOT CONTROL OFF" : "PRESS TRIGGER TO START";
            ResetAllState();
            PublishStopIfDue();
            LogIfDue(float.NaN, 0f, 0f);
            return;
        }

        if (enableMoveSpeedPulseStepFallback
            && lastRawForward >= Mathf.Max(stepPulseThreshold, walkDeadZone))
        {
            lastMoveSpeedPulseTime = Time.realtimeSinceStartup;
            lastMoveSpeedPulseInput = Mathf.Min(lastRawForward, Mathf.Max(inputSpeedForMax, walkDeadZone));
        }
        moveSpeedPulseActive = enableMoveSpeedPulseStepFallback
            && Time.realtimeSinceStartup - lastMoveSpeedPulseTime <= Mathf.Max(stepPulseHoldTime, 0f);

        if (enableStepInPlaceDrive && rawStepSignal)
        {
            lastStepSignalTime = Time.realtimeSinceStartup;
        }
        bool extraDataStepActive = enableStepInPlaceDrive
            && Time.realtimeSinceStartup - lastStepSignalTime <= Mathf.Max(stepSignalHoldTime, 0f);
        stepInPlaceActive = extraDataStepActive || moveSpeedPulseActive;
        // Footstep intent stays independent of body yaw and pending turn error.
        // Mini S may provide only brief moveSpeed pulses; suppressing this hold
        // during turns removed forward output between pulses, leaving a spin.
        // A body turn alone does not create a step signal here. If pivoting also
        // produces walking sensor data, intent needs better sensor information;
        // yaw rate alone cannot distinguish it from walking while turning.
        // The SDK already supplies target movement speed. A speed pulse must
        // never become a fixed-strength synthetic step. Only bridge its tail at
        // the measured strength, capped at the input that already maps to max speed.
        if (moveSpeedPulseActive)
        {
            float remaining = 1f - Mathf.Clamp01((Time.realtimeSinceStartup - lastMoveSpeedPulseTime)
                / Mathf.Max(stepPulseHoldTime, 0.001f));
            lastRawForward = Mathf.Max(lastRawForward, lastMoveSpeedPulseInput * remaining);
        }
        if (extraDataStepActive)
        {
            lastRawForward = Mathf.Max(lastRawForward, Mathf.Max(stepInPlaceInput, 0f));
        }

        // Turn channel: only this part is gated by a valid body yaw reading.
        // An invalid reading stops the turn channel -- it never
        // touches forward.
        if (TryGetHorizontalBodyYaw(data.bodyRotationRaw, out float rawBodyYawDeg))
        {
            UpdateTurnRate(rawBodyYawDeg);
        }
        else
        {
            InvalidateHeading("INVALID BODY HEADING (TURN OFF)");
        }
        if (!headingCalibrated && hasRawBodyYaw && bodyHeadingUsable && HasFreshOdometry)
            CalibrateHeading();

        float forward = FilterForward(lastRawForward);
        if (reverseModeEnabled)
        {
            forward = -forward;
        }
        float linear = CalculateLinear(forward);
        float angular = CalculateTurn();

        status = (reverseModeEnabled ? "ACTIVE / REVERSE / " : "ACTIVE / ") + turnStatus;
        PublishIfDue(linear, angular);
        float debugRawBodyYawDeg = hasRawBodyYaw ? lastRawBodyYawDeg : float.NaN;
        LogIfDue(debugRawBodyYawDeg, linear, angular);
    }

    private void HandleKatInputFault(string reason)
    {
        if (katInputFaultSince < 0f)
        {
            katInputFaultSince = Time.realtimeSinceStartup;
            if (enableDebugLog) Debug.LogWarning("[KAT DIRECT ANGLE] " + reason + "; commands stopped.");
        }
        if (Time.realtimeSinceStartup - katInputFaultSince > Mathf.Max(katInputRecoveryGraceTime, 0f))
            activationEnabled = false;
        status = reason + (activationEnabled ? " / WAITING FOR RECOVERY" : " / PRESS TRIGGER AFTER RECOVERY");
        ResetForwardIntent();
        InvalidateHeading(reason + " (TURN OFF)");
        if (!activationEnabled) ResetAllState();
        PublishStopIfDue();
        LogIfDue(float.NaN, 0f, 0f);
    }

    private void UpdateTurnRate(float rawBodyYawDeg)
    {
        // Unity's unscaledTime inside FixedUpdate is the physics clock, not wall time.
        // Do not advance the filter repeatedly in back-to-back catch-up ticks.
        float now = Time.realtimeSinceStartup;
        float elapsed = now - lastBodyYawSampleTime;

        if (!hasRawBodyYaw)
        {
            // Seed a new explicitly activated session.
            lastRawBodyYawDeg = rawBodyYawDeg;
            hasRawBodyYaw = true;
            bodyHeadingUsable = true;
            lastBodyYawSampleTime = now;
            lastBodyYawValidTime = now;
            bodyYawRejectStartTime = -1f;
            filteredYawRateDegPerSec = 0f;
            return;
        }

        if (elapsed <= 0.001f) return;
        if (now - lastBodyYawValidTime > Mathf.Max(turnResyncGraceTime, 0f))
        {
            // A sensor origin shift must not cancel a previously accepted turn.
            // Wait for a stable new reading, then rebase the sensor only. Do not
            // infer physical movement that occurred while its reading was invalid.
            InvalidateHeading("BODY HEADING GAP / WAITING FOR STABLE SENSOR");
            if (recoveryCandidateSince < 0f
                || Mathf.Abs(Mathf.DeltaAngle(recoveryCandidateYaw, rawBodyYawDeg)) > Mathf.Max(bodyYawRecoveryBandDeg, 0f))
            {
                recoveryCandidateYaw = rawBodyYawDeg;
                recoveryCandidateSince = now;
                return;
            }
            if (now - recoveryCandidateSince < Mathf.Max(bodyYawRecoveryStableTime, 0f)) return;
            lastRawBodyYawDeg = rawBodyYawDeg;
            lastBodyYawSampleTime = lastBodyYawValidTime = now;
            bodyYawRejectStartTime = -1f;
            recoveryCandidateSince = -1f;
            bodyHeadingUsable = true;
            // Settle the angle filter without differentiating its outstanding
            // tail into artificial live turn feedforward on recovery.
            filteredBodyTurnDeg = bodyTurnDeg;
            filteredYawRateDegPerSec = 0f;
            turnStatus = "BODY SENSOR RESYNCED / ACCEPTED TARGET PRESERVED";
            if (enableDebugLog)
                Debug.Log($"[KAT DIRECT ANGLE] {turnStatus}; pending={HeadingErrorDeg:F1} deg; movement during gap unknown.");
            return;
        }

        // Reject on the RAW step, before any angle smoothing. A genuine
        // quaternion/Euler discontinuity has to be caught here -- if it were
        // allowed into the angle low-pass first, the filter would blend
        // partway toward the bogus value and the glitch would leak into the
        // rate over several ticks instead of being skipped outright.
        float rawStep = Mathf.DeltaAngle(lastRawBodyYawDeg, rawBodyYawDeg);
        float stepLimit = Mathf.Max(Mathf.Max(maxAcceptedBodyYawStepDeg, 1f),
            Mathf.Max(maxBodyYawRateDegPerSec, 1f) * Mathf.Min(elapsed, 0.2f));
        // Do not let a persistent bad offset become valid just by waiting.
        if (bodyYawRejectStartTime >= 0f) stepLimit = rejectedBodyStepLimit;

        if (Mathf.Abs(rawStep) > stepLimit)
        {
            if (bodyYawRejectStartTime < 0f)
            {
                bodyYawRejectStartTime = now;
                rejectedBodyStepLimit = stepLimit;
            }
            InvalidateHeading("BODY YAW JUMP (TURN OFF)");
            if (enableDebugLog && now >= nextBodyYawWarningTime)
            {
                nextBodyYawWarningTime = now + 0.5f;
                Debug.LogWarning($"[KAT DIRECT ANGLE] Skipped body yaw jump: step {rawStep:F1} deg over " +
                    $"{elapsed * 1000f:F0} ms (limit {stepLimit:F1} deg). Sample dropped, turn target kept.");
            }
            // Keep the accepted angle AND its time. Persistent rejection is
            // handled by the last-valid-time grace check above.
            return;
        }

        bodyYawRejectStartTime = -1f;
        recoveryCandidateSince = -1f;
        lastRawBodyYawDeg = rawBodyYawDeg;
        lastBodyYawSampleTime = now;
        lastBodyYawValidTime = now;
        bodyHeadingUsable = true;
        if (!headingCalibrated) return;
        bodyTurnDeg += rawStep;

        // Smooth the ANGLE, then differentiate the smoothed angle: the raw
        // value is quantized to whole degrees, so differentiating it directly
        // turns every quantization step into a brief huge rate. The filtered
        // angle is also what feeds the 1:1 target (see DesiredRobotYawDeg), so
        // gait sway is kept out of the setpoint as well as out of the rate.
        // While stepping, the body ring swings several degrees per stride and
        // the filter is lengthened for exactly that.
        float filterTime = Mathf.Max(bodyYawFilterTime, 0f)
            * (stepInPlaceActive ? Mathf.Max(stepTurnFilterMultiplier, 1f) : 1f);
        float angleBlend = Blend(filterTime, elapsed);
        double previousFilteredTurn = filteredBodyTurnDeg;
        filteredBodyTurnDeg += (bodyTurnDeg - filteredBodyTurnDeg) * angleBlend;
        float filteredStep = (float)(filteredBodyTurnDeg - previousFilteredTurn);
        float rawRateDegPerSec = filteredStep / elapsed;

        float rateBlend = Blend(turnRateSmoothingTime, elapsed);
        filteredYawRateDegPerSec = Mathf.Lerp(filteredYawRateDegPerSec, rawRateDegPerSec, rateBlend);
    }

    // Observe the exact SDK packet before activation gates or yaw filtering.
    // This also works while robot control is disabled for stationary diagnosis.
    private void LogPoseDiagnostic(KATNativeSDK.TreadMillData data)
    {
        if (!enableDebugLog) { hasDiagnosticPose = false; return; }
        Quaternion q = data.bodyRotationRaw;
        double norm2 = (double)q.x*q.x + (double)q.y*q.y + (double)q.z*q.z + (double)q.w*q.w;
        bool usable = TryGetHorizontalBodyYaw(q, out float yaw);
        float now = Time.realtimeSinceStartup;
        double rotationStep = double.NaN;
        float yawStep = float.NaN;
        if (hasDiagnosticPose && usable && Finite(norm2) && norm2 > 1e-12)
        {
            Quaternion p = previousDiagnosticPose;
            double pn = (double)p.x*p.x + (double)p.y*p.y + (double)p.z*p.z + (double)p.w*p.w;
            double dot = ((double)p.x*q.x + (double)p.y*q.y + (double)p.z*q.z + (double)p.w*q.w)
                / System.Math.Sqrt(pn*norm2);
            // q and -q describe the same rotation, so sign flips are not jumps.
            rotationStep = 2 * System.Math.Acos(System.Math.Min(1, System.Math.Abs(dot))) * 180 / System.Math.PI;
            yawStep = Mathf.DeltaAngle(previousDiagnosticYaw, yaw);
        }
        bool jump = System.Math.Abs(yawStep) > 45 || rotationStep > 45;
        if (now >= nextPoseDiagnosticTime || jump)
        {
            nextPoseDiagnosticTime = now + Mathf.Max(debugLogInterval, 0.1f);
            string message = $"KAT POSE RAW | t={now:F3} Connected={data.connected} Device={data.deviceName} " +
                $"Q=({q.x:R},{q.y:R},{q.z:R},{q.w:R}) Norm2={norm2:F6} YawValid={usable} SensorYaw={yaw:F3} " +
                $"PrevQ=({previousDiagnosticPose.x:R},{previousDiagnosticPose.y:R},{previousDiagnosticPose.z:R},{previousDiagnosticPose.w:R}) " +
                $"PrevYaw={previousDiagnosticYaw:F3} SampleDtMs={(now-previousDiagnosticTime)*1000:F1} " +
                $"QuaternionStep={rotationStep:F3} YawStep={yawStep:F3} " +
                $"Stamp={data.lastUpdateTimePoint:R} PrevStamp={previousDiagnosticStamp:R} " +
                $"Activated={activationEnabled} RobotControl={enableRobotControl} Jump={jump}";
            if (jump) Debug.LogWarning(message); else Debug.Log(message);
        }
        hasDiagnosticPose = data.connected && usable;
        previousDiagnosticPose = q;
        previousDiagnosticYaw = yaw;
        previousDiagnosticTime = now;
        previousDiagnosticStamp = data.lastUpdateTimePoint;
    }

    private bool TryGetHorizontalBodyYaw(Quaternion bodyRotation, out float yawDeg)
    {
        double norm = (double)bodyRotation.x * bodyRotation.x + (double)bodyRotation.y * bodyRotation.y
            + (double)bodyRotation.z * bodyRotation.z + (double)bodyRotation.w * bodyRotation.w;
        yawDeg = 0f;
        if (!Finite(norm) || norm < 1e-12) return false;
        float scale = (float)(1 / System.Math.Sqrt(norm));
        bodyRotation = new Quaternion(bodyRotation.x * scale, bodyRotation.y * scale, bodyRotation.z * scale, bodyRotation.w * scale);
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
        float blend = Blend(smoothing);
        filteredForwardSpeed = Mathf.Lerp(filteredForwardSpeed, rawForward, blend);
        return filteredForwardSpeed;
    }

    private float CalculateLinear(float forward)
    {
        float magnitude = Mathf.Abs(forward);
        if (magnitude <= walkDeadZone)
        {
            return 0f;
        }

        float range = Mathf.Max(inputSpeedForMax - walkDeadZone, 0.001f);
        float response = Mathf.Pow(Mathf.Clamp01((magnitude - walkDeadZone) / range), responseExponent);
        float maximum = forward >= 0f ? maxForwardSpeed : maxReverseSpeed;
        float startBlend = Mathf.Clamp01((magnitude - walkDeadZone) / Mathf.Max(forwardStartRampInput, 0.001f));
        float output = Mathf.Lerp(Mathf.Min(minDriveSpeed, maximum) * startBlend, maximum, response);
        return forward >= 0f ? output : -output;
    }

    private float CalculateTurn()
    {
        LiveTurnDemand = CorrectionTurnDemand = 0f;
        if (!HasFreshOdometry)
        {
            watchingTurnProgress = false;
            // No fresh measurement means no way to verify the angle, so the
            // turn channel idles -- but it does NOT latch into a state that
            // needs a button press: the reference is resynced or re-anchored in
            // OnOdometry as soon as data returns.
            turnStatus = headingCalibrated ? "ODOMETRY STALE (TURN OFF)" : "WAITING FOR ODOMETRY (TURN OFF)";
            return 0f;
        }
        if (!headingCalibrated || !bodyHeadingUsable) return 0f;
        double error = HeadingErrorDeg;
        // Human rotation can outrun the chassis by a wide margin. Never discard
        // a valid accumulated turn just because it exceeds maxHeadingErrorDeg:
        // that cap limits the proportional term only, and an earlier version
        // stopped counting part-way through a 360 deg turn.
        float correctionLimit = Mathf.Max(maxHeadingErrorDeg, 5f);
        float feedforward = CalculateOpenLoopTurn(filteredYawRateDegPerSec);
        float feedback = CalculateHeadingCorrection((float)error);
        float feedbackLimit = Mathf.Max(maxHeadingCorrectionSpeed, 0f);
        feedback = Mathf.Clamp(feedback, -feedbackLimit, feedbackLimit);
        // The latest body angle is the target, not a separate rate command.
        // Returning from 90 to 87 degrees while the robot is at 60 still
        // requires +27 degrees. Rate assistance must never reverse that chase,
        // even when predictive braking has reduced feedback to zero.
        if (System.Math.Abs(error) <= headingToleranceDeg || feedforward * error <= 0)
            feedforward = 0f;
        float angular = feedforward + feedback;
        angular = Mathf.Clamp(angular, -maxAngularSpeed, maxAngularSpeed);
        if (!CheckTurnProgress(angular)) return 0f;
        LiveTurnDemand = feedforward;
        CorrectionTurnDemand = feedback;
        turnStatus = System.Math.Abs(error) > correctionLimit ? "CATCHING UP / TARGET PRESERVED"
            : System.Math.Abs(error) <= headingToleranceDeg ? "ANGLE ALIGNED"
            : angular == 0f ? "BRAKING TOWARD TARGET" : "TRACKING ANGLE";
        return angular;
    }

    private float CalculateHeadingCorrection(float error)
    {
        if (Mathf.Abs(error) <= headingToleranceDeg) return 0f;
        float direction = Mathf.Sign(error);
        float approachingRate = Mathf.Max(0f, direction * measuredRobotYawRate);
        float remaining = Mathf.Max(0f, Mathf.Abs(error)
            - approachingRate * Mathf.Max(turnBrakingLookaheadTime, 0f));
        remaining = Mathf.Min(remaining, Mathf.Max(maxHeadingErrorDeg, 5f));
        float near = Mathf.Min(remaining, Mathf.Max(headingSlowZoneDeg, 0f));
        return direction * Mathf.Deg2Rad * (near * Mathf.Max(nearHeadingKp, 0f)
            + (remaining - near) * Mathf.Max(headingKp, 0f));
    }

    private float CalculateOpenLoopTurn(float rate)
    {
        float magnitude = Mathf.Abs(rate);
        if (magnitude <= turnRateDeadZoneDegPerSec) return 0f;
        float range = Mathf.Max(turnRateForMaxOutput - turnRateDeadZoneDegPerSec, 0.01f);
        float response = Mathf.Pow(Mathf.Clamp01((magnitude - turnRateDeadZoneDegPerSec) / range),
            Mathf.Max(turnResponseExponent, 0.01f));
        return headingSign * Mathf.Sign(rate) * response * Mathf.Max(openLoopMaxAngularSpeed, 0f);
    }

    private bool CheckTurnProgress(float angular)
    {
        // Judge actual progress in the commanded direction, not decreasing error:
        // the user may keep turning faster while the robot is correctly moving.
        if (Mathf.Abs(angular) < 0.05f)
        {
            watchingTurnProgress = false;
            return true;
        }
        float direction = Mathf.Sign(angular);
        if (!watchingTurnProgress || direction != progressDirection)
        {
            watchingTurnProgress = true;
            progressReferenceYaw = robotUnwrapped;
            progressStartTime = Time.realtimeSinceStartup;
            progressDirection = direction;
        }
        double progress = direction * (robotUnwrapped - progressReferenceYaw);
        if (progress >= Mathf.Max(minimumTurnProgressDeg, 0.1f))
        {
            progressReferenceYaw = robotUnwrapped;
            progressStartTime = Time.realtimeSinceStartup;
        }
        if (Time.realtimeSinceStartup - progressStartTime < Mathf.Max(turnProgressTimeout, 0.5f)) return true;
        if (enableDebugLog)
            Debug.LogWarning($"[KAT DIRECT ANGLE] No turn progress: bodyTurn={bodyTurnDeg:F1}, " +
                $"robotTurn={RobotTurnDeg:F1}, pending={HeadingErrorDeg:F1} deg. Target dropped (robot blocked or refusing the command).");
        // Drop the unreachable target instead of latching a realign request:
        // the robot stops pushing, and normal tracking resumes by itself on the
        // next body rotation.
        ReanchorTurnTarget("NO TURN PROGRESS / TARGET DROPPED");
        return false;
    }

    private void CalibrateHeading()
    {
        robotReference = robotUnwrapped;
        headingSign = Mathf.Sign(bodyToRosYawSign);
        bodyTurnDeg = filteredBodyTurnDeg = 0;
        filteredYawRateDegPerSec = 0;
        headingCalibrated = true;
        watchingTurnProgress = false;
        turnStatus = "ANGLE ALIGNED";
    }

    /// <summary>
    /// Redefine "robot forward" as the operator's current body direction and
    /// clear the pending turn. Used when a dropout was long enough that the
    /// queued angle can no longer be trusted, or when the robot will not
    /// execute it -- always preferred over latching a manual realign request,
    /// which is what used to stop turning for the rest of the session.
    /// </summary>
    private void ReanchorTurnTarget(string reason)
    {
        robotReference = robotUnwrapped;
        bodyTurnDeg = filteredBodyTurnDeg = 0;
        filteredYawRateDegPerSec = 0f;
        watchingTurnProgress = false;
        turnStatus = reason;
        if (enableDebugLog)
            Debug.Log($"[KAT DIRECT ANGLE] {reason}: turn reference re-anchored at robot yaw {robotYawDeg:F1} deg.");
        if (lastPublishedAngular != 0f) Publish(lastPublishedLinear, 0f);
    }

    private void InvalidateHeading(string reason)
    {
        // A missing reading suspends output, not the accumulated reference.
        // Keep the last accepted yaw/time so short recovery counts real motion.
        bodyHeadingUsable = false;
        watchingTurnProgress = false;
        filteredYawRateDegPerSec = 0;
        turnStatus = reason;
        if (lastPublishedAngular != 0f) Publish(lastPublishedLinear, 0f);
    }

    private void SuspendOdometry(string reason)
    {
        odometryUsable = false;
        watchingTurnProgress = false;
        turnStatus = reason;
        if (lastPublishedAngular != 0f) Publish(lastPublishedLinear, 0f);
    }

    private static float Blend(float seconds) => Blend(seconds, Time.fixedDeltaTime);
    private static float Blend(float seconds, float dt) => seconds <= 0f ? 1f : 1f - Mathf.Exp(-dt / seconds);
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private void ReadStepSignal(KATNativeSDK.TreadMillData data)
    {
        hasMiniSExtra = false;
        hasWalkC2Extra = false;
        rawStepSignal = false;
        if (data.extraData == null || data.extraData.Length < 128) return;

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
        // An unavailable controller is not evidence of a physical release.
        if (!device.isValid) return physicalTriggerHeld;
        if (device.TryGetFeatureValue(CommonUsages.trigger, out float value))
            return UpdateTriggerState(value);
        if (device.TryGetFeatureValue(CommonUsages.triggerButton, out bool pressed))
            return UpdateTriggerState(pressed ? 1f : 0f);
        return physicalTriggerHeld;
    }

    private bool UpdateTriggerState(float value)
    {
        if (!Finite(value)) return physicalTriggerHeld;
        float pressThreshold = Mathf.Clamp(activationTriggerThreshold, 0.01f, 1f);
        float releaseThreshold = Mathf.Clamp(activationTriggerReleaseThreshold, 0f, pressThreshold * 0.9f);
        if (!physicalTriggerHeld)
        {
            if (value >= pressThreshold) physicalTriggerHeld = true;
            triggerReleaseSince = -1f;
        }
        else if (value <= releaseThreshold)
        {
            if (triggerReleaseSince < 0f) triggerReleaseSince = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - triggerReleaseSince >= Mathf.Max(activationTriggerReleaseTime, 0f))
            {
                physicalTriggerHeld = false;
                triggerReleaseSince = -1f;
            }
        }
        else triggerReleaseSince = -1f;
        return physicalTriggerHeld;
    }

    private static bool ReadButton(XRNode hand, InputFeatureUsage<bool> usage)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(hand);
        return device.isValid && device.TryGetFeatureValue(usage, out bool pressed) && pressed;
    }

    private void ResetAllState()
    {
        LiveTurnDemand = CorrectionTurnDemand = 0f;
        ResetForwardIntent();
        filteredYawRateDegPerSec = 0f;
        hasRawBodyYaw = false;
        bodyHeadingUsable = false;
        headingCalibrated = false;
        watchingTurnProgress = false;
        bodyTurnDeg = filteredBodyTurnDeg = 0;
        bodyYawRejectStartTime = -1f;
        recoveryCandidateSince = -1f;
        lastBodyYawSampleTime = -999f;
        lastBodyYawValidTime = -999f;
        turnStatus = "WAITING FOR HEADING";
    }

    private void ResetForwardIntent()
    {
        filteredForwardSpeed = 0f;
        hasMiniSExtra = false;
        hasWalkC2Extra = false;
        rawStepSignal = false;
        lastStepSignalTime = -999f;
        lastMoveSpeedPulseTime = -999f;
        lastMoveSpeedPulseInput = 0f;
        stepInPlaceActive = false;
        moveSpeedPulseActive = false;
    }

    private void PublishIfDue(float linear, float angular)
    {
        // A brief step can fit entirely between two 20 Hz publish slots. Publish
        // start/stop edges immediately so reducing smoothing actually reaches ROS.
        bool motionEdge = (linear == 0f) != (lastPublishedLinear == 0f)
            || (angular == 0f) != (lastPublishedAngular == 0f);
        if (!motionEdge && Time.realtimeSinceStartup < nextPublishTime) return;
        nextPublishTime = Time.realtimeSinceStartup + 1f / Mathf.Max(publishRateHz, 1f);
        // Bound the actual published command, not just an internal FixedUpdate
        // value. A scheduler hitch must not allow an instantaneous full-speed jump.
        float dt = Mathf.Clamp(Time.realtimeSinceStartup - lastAngularPublishTime, 0f, 0.1f);
        float target = angular * lastPublishedAngular < 0f ? 0f : angular;
        float rate = Mathf.Abs(target) > Mathf.Abs(lastPublishedAngular) ? turnAcceleration : turnDeceleration;
        angular = Mathf.MoveTowards(lastPublishedAngular, target, Mathf.Max(rate, 0.01f) * dt);
        Publish(linear, angular);
    }

    private void PublishStopIfDue()
    {
        if (lastPublishedLinear != 0f || lastPublishedAngular != 0f) Publish(0f, 0f);
        else PublishIfDue(0f, 0f);
    }

    private void Publish(float linear, float angular)
    {
        if (!enableRobotControl || !activationEnabled || emergencyStopEngaged
            || !Finite(linear) || !Finite(angular)) linear = angular = 0f;
        if (!headingCalibrated || !bodyHeadingUsable || !HasFreshOdometry) angular = 0f;
        angular = Mathf.Clamp(angular, -Mathf.Min(Mathf.Max(maxAngularSpeed, 0f), 2f), Mathf.Min(Mathf.Max(maxAngularSpeed, 0f), 2f));
        if (angular == 0f) LiveTurnDemand = CorrectionTurnDemand = 0f;
        TwistMsg message = new TwistMsg(
            new Vector3Msg(linear, 0.0, 0.0),
            new Vector3Msg(0.0, 0.0, angular)
        );
        ros?.Publish(cmdVelTopic, message);
        lastPublishedLinear = linear;
        lastPublishedAngular = angular;
        lastAngularPublishTime = Time.realtimeSinceStartup;
    }

    private void LogIfDue(float rawBodyYawDeg, float linear, float angular)
    {
        if (!enableDebugLog || Time.realtimeSinceStartup < nextDebugTime) return;
        nextDebugTime = Time.realtimeSinceStartup + Mathf.Max(debugLogInterval, 0.1f);
        Debug.Log(
            $"KAT DIRECT ANGLE | t={Time.realtimeSinceStartup:F3} {status} Enabled={enableRobotControl} Activated={activationEnabled} " +
            $"InputMagnitude={new Vector2(lastRawMoveSpeed.x, lastRawMoveSpeed.z).magnitude:F3} " +
            $"RawFwd={lastRawForward:F3} FilteredFwd={filteredForwardSpeed:F3} PulseStep={moveSpeedPulseActive} Step={rawStepSignal} StepDrive={stepInPlaceActive} MoveSpeed=({lastRawMoveSpeed.x:F3},{lastRawMoveSpeed.y:F3},{lastRawMoveSpeed.z:F3}) " +
            $"BodyRaw={rawBodyYawDeg:F1} YawRate={filteredYawRateDegPerSec:F1}deg/s " +
            $"Linear={linear:F3} Angular={angular:F3} AngularCommand={lastPublishedAngular:F3} RobotYawRate={measuredRobotYawRate:F1}deg/s LiveTurn={LiveTurnDemand:F3} CorrectionTurn={CorrectionTurnDemand:F3} | BodyTurn={BodyTurnDeg:F1} TargetTurn={TargetTurnDeg:F1} RobotTurn={RobotTurnDeg:F1} Target={DesiredRobotYawDeg:F1} Error={HeadingErrorDeg:F1} " +
            $"Robot={robotYawDeg:F1} Fresh={HasFreshOdometry} Turn=[{turnStatus}] KatStamp={lastKatTimestamp:R}"
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
        GUI.Label(new Rect(20, 20, 900, 30), $"KAT DIRECT ANGLE: {status}", style);
        GUI.Label(
            new Rect(20, 50, 1100, 30),
            $"YawRate {filteredYawRateDegPerSec:F1}°/s  cmd_vel ({lastPublishedLinear:F3}, {lastPublishedAngular:F3})",
            style
        );
        GUI.Label(
            new Rect(20, 80, 1100, 30),
            $"Target={DesiredRobotYawDeg:F1}° Robot={robotYawDeg:F1}° Error={HeadingErrorDeg:F1}° Fresh={HasFreshOdometry}",
            style
        );
    }

    private void OnDisable()
    {
        activationEnabled = false;
        ResetAllState();
        if (ros != null) Publish(0f, 0f);
    }

    private void OnApplicationQuit()
    {
        if (ros != null) Publish(0f, 0f);
    }
}
