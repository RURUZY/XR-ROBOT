using System;
using UnityEngine;
using UnityEngine.XR;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
using RosMessageTypes.Nav;

/// <summary>
/// Experimental 1:1 BODY-heading follower. Intentionally not attached to any scene.
/// 初始校准：机器人目标 = 机器人初始朝向 + sign * 人的累计转角。
/// 人停止转身后仍补齐剩余误差；走路不缩放转角，也不自动 settle/re-anchor。
/// Both headings are unwrapped, so crossing 360 degrees does not lose turns.
///
/// Setup: use this as the ONLY command publisher (or use a ROS command mux).
/// /odometry/filtered must describe the BASE in a continuous, fixed odom frame.
/// Enable Robot Control explicitly. Right trigger: start/pause/resume;
/// right primary: latch/release software stop; right secondary: reverse;
/// left secondary: pause and re-align. Optional keys: T/E/R/C respectively.
/// Normal pause preserves the reference and tracks body movement: resuming may
/// catch up. C/left secondary intentionally discards the old error and stays paused.
/// A sensor fault invalidates calibration; recovery never starts motion itself.
/// Press start again with fresh sensors to establish a new reference after a fault.
///
/// Unity-side prototype using the existing ROS transport, not a robot-local servo.
/// A robot-side cmd_vel watchdog is required if Unity/network stops running.
/// Timestamp checks detect stalled/reordered streams, not absolute network age
/// between unsynchronised clocks. Precision still depends on real sensor feedback.
/// Existing KatStatusHud is typed to DirectController; use the public diagnostics
/// below for a future HUD adapter. No HUD, scene, prefab or SDK changes are needed.
/// </summary>
[DisallowMultipleComponent]
public class KatHuskyHeadingFollowController : MonoBehaviour
{
    [Header("ROS / input freshness")]
    public string cmdVelTopic = "/cmd_vel";
    public string odometryTopic = "/odometry/filtered";
    [Tooltip("Optional KAT serial; empty uses the SDK default device.")]
    public string katSerialNumber = "";
    [Min(0.05f)] public float odometryTimeout = 0.5f;
    [Min(0.05f)] public float katTimeout = 0.5f;
    [Tooltip("Require KAT lastUpdateTimePoint to advance even while stationary. Disable only if this device does not provide a live timestamp.")]
    public bool requireKatTimestampProgress = true;
    [Min(1f)] public float publishRateHz = 20f;

    [Header("1:1 heading tracking")]
    [Tooltip("Usually -1: Unity/KAT clockwise yaw -> ROS counterclockwise yaw. Only the sign is used; the angle gain is always 1.")]
    public float bodyToRosYawSign = -1f;
    [Min(0f)] public float bodyYawFilterTime = 0.06f;
    [Min(0.01f)] public float headingKp = 0.8f;
    [Min(0.1f)] public float headingToleranceDeg = 2f;
    [Min(0.1f)] public float headingRestartToleranceDeg = 3f;
    [Min(0.01f)] public float maxAngularSpeed = 0.3f;
    [Min(0.01f)] public float maxAngularAcceleration = 0.6f;
    [Tooltip("Pause on excessive lag instead of chasing a large accumulated turn. Explicit re-alignment is then required.")]
    [Min(5f)] public float maxTrackingErrorDeg = 120f;
    [Range(1f, 179f)] public float maxBodyYawStepDeg = 45f;
    [Range(1f, 179f)] public float maxRobotYawStepDeg = 45f;
    [Range(0f, 89f)] public float maxBodyTiltDeg = 70f;

    [Header("Walking (independent of heading error)")]
    [Tooltip("Leave off for initial turn-only tests. Uses horizontal moveSpeed magnitude, as in the existing controllers.")]
    public bool enableLinearMotion = false;
    [Min(0f)] public float maxForwardSpeed = 0.35f;
    [Min(0f)] public float maxReverseSpeed = 0.20f;
    [Min(0f)] public float walkDeadZone = 0.02f;
    [Min(0.01f)] public float inputSpeedForMax = 1f;
    [Min(0f)] public float minDriveSpeed = 0.08f;
    [Range(0.3f, 1f)] public float responseExponent = 0.6f;
    [Min(0f)] public float forwardAttackTime = 0.04f;
    [Min(0f)] public float forwardReleaseTime = 0.15f;
    [Tooltip("Optional moveSpeed pulse hold from the Direct version. No dependency on device extraData layouts. Extends motion after a pulse by the hold time.")]
    public bool enableStepPulseHold = false;
    [Min(0.01f)] public float stepPulseThreshold = 0.08f;
    [Min(0f)] public float stepPulseHoldTime = 0.6f;
    [Min(0f)] public float stepPulseInput = 0.35f;

    [Header("Explicit activation")]
    public bool enableRobotControl = false;
    public XRNode controlHand = XRNode.RightHand;
    public XRNode realignHand = XRNode.LeftHand;
    [Range(0f, 1f)] public float triggerThreshold = 0.2f;
    public bool enableKeyboardFallback = false;
    public KeyCode activationKey = KeyCode.T;
    public KeyCode emergencyStopKey = KeyCode.E;
    public KeyCode reverseModeKey = KeyCode.R;
    public KeyCode realignKey = KeyCode.C;

    [Header("Diagnostics (Console / future HUD)")]
    public bool enableDebugLog = true;
    [Min(0.05f)] public float debugLogInterval = 0.5f;
    public string Status { get; private set; } = "DISABLED";
    public bool IsFollowing { get; private set; }
    public bool HeadingCalibrated { get; private set; }
    public bool EmergencyStopEngaged { get; private set; }
    public bool ReverseModeEnabled { get; private set; }
    public float RobotYawDeg { get; private set; }
    public float BodyYawDeg { get; private set; }
    public double DesiredRobotYawDeg { get; private set; }
    public double HeadingErrorDeg { get; private set; }
    public double BodyTurnDeg => bodyUnwrapped - bodyReference;
    public double RobotTurnDeg => robotUnwrapped - robotReference;
    public float LastPublishedLinear { get; private set; }
    public float LastPublishedAngular { get; private set; }
    public bool HasFreshOdometry => hasOdometry && Now - lastOdometryReceipt <= odometryTimeout;
    public bool HasFreshBody => hasBody && Now - lastBodyReceipt <= katTimeout;
    public string BodyInputStatus { get; private set; } = "NO KAT SAMPLE";
    public string LastFault { get; private set; } = "NONE";

    private static float Now => Time.realtimeSinceStartup;
    private ROSConnection ros;
    private string registeredCommandTopic;
    private bool hasOdometry, hasBody, hasKatStamp, headingSettled = true;
    private double lastOdomStamp = double.NegativeInfinity, lastKatStamp;
    private string odomFrame, baseFrame, katDevice;
    private float lastOdometryReceipt = -999f, lastBodyReceipt = -999f;
    private double robotUnwrapped, bodyUnwrapped, filteredBodyTurn;
    private double robotReference, bodyReference;
    private float rawWalking, filteredWalking, angularCommand, lastStepPulse = -999f;
    private float lastTick, nextPublish, nextLog;
    private bool previousTrigger, previousStop, previousReverse, previousRealign;
    private Quaternion sampledBodyRotation;
    private Vector3 sampledMoveSpeed;
    private double sampledKatStamp;
    private bool sampledKatConnected;
    private float sampledHorizontalMagnitude;
    private float lastFaultTime = -1f;

    private void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        registeredCommandTopic = cmdVelTopic;
        ros.RegisterPublisher<TwistMsg>(registeredCommandTopic);
        ros.Subscribe<OdometryMsg>(odometryTopic, OnOdometry);
        Status = "PAUSED / PRESS TRIGGER TO CALIBRATE";
        lastTick = Now;
    }

    private void OnEnable()
    {
        hasBody = hasOdometry = hasKatStamp = false;
        lastOdomStamp = double.NegativeInfinity;
        IsFollowing = HeadingCalibrated = false;
        lastTick = Now;
    }

    private void OnOdometry(OdometryMsg message)
    {
        // ROSConnection.Unsubscribe removes ALL listeners for a topic in this
        // installed connector. Keep our single subscription and ignore disabled
        // callbacks rather than disconnecting another component's odometry HUD.
        if (this == null || !isActiveAndEnabled) return;
        if (message == null || message.header == null || message.header.stamp == null
            || message.pose == null || message.pose.pose == null
            || !TryRobotYaw(message.pose.pose.orientation, out float yaw))
        {
            hasOdometry = false;
            Fault("INVALID ODOMETRY");
            return;
        }

        double stamp = message.header.stamp.sec + message.header.stamp.nanosec * 1e-9;
        if (message.header.stamp.nanosec >= 1000000000 || stamp <= 0)
        {
            hasOdometry = false;
            Fault("ODOMETRY NEEDS A VALID SOURCE TIMESTAMP");
            return;
        }
        // Duplicate messages must not make a frozen source look fresh.
        if (stamp == lastOdomStamp) return;
        if (stamp < lastOdomStamp)
        {
            hasOdometry = false;
            lastOdomStamp = stamp;
            Fault("ODOMETRY TIME REVERSED / RESTARTED");
            return;
        }

        bool continuous = HasFreshOdometry;
        float delta = Mathf.DeltaAngle(RobotYawDeg, yaw);
        if (hasOdometry && (!continuous || odomFrame != message.header.frame_id
            || baseFrame != message.child_frame_id || Mathf.Abs(delta) > maxRobotYawStepDeg))
        {
            Fault("ODOMETRY GAP / FRAME CHANGE / YAW JUMP");
            continuous = false;
        }
        robotUnwrapped = continuous ? robotUnwrapped + delta : yaw;
        RobotYawDeg = yaw;
        odomFrame = message.header.frame_id;
        baseFrame = message.child_frame_id;
        lastOdomStamp = stamp;
        lastOdometryReceipt = Now;
        hasOdometry = true;
    }

    private void Update()
    {
        float now = Now;
        float dt = Mathf.Max(now - lastTick, 0.0001f);
        lastTick = now;
        if (IsFollowing && dt > Mathf.Min(katTimeout, odometryTimeout))
            Fault("CONTROL UPDATE GAP");
        ReadBody();
        ReadControls(); // Update, not FixedUpdate: do not lose short key presses.

        if (!enableRobotControl && (IsFollowing || HeadingCalibrated))
            Fault("ROBOT CONTROL OFF");
        if (IsFollowing && (!HasFreshOdometry || !HasFreshBody))
            Fault("STALE SENSOR FEEDBACK");

        if (HeadingCalibrated && HasFreshBody && HasFreshOdometry)
        {
            // Filter the accumulated ANGLE; never discard small angular steps.
            // DC gain remains 1 even for slow turns and while walking.
            double rawTurn = bodyUnwrapped - bodyReference;
            filteredBodyTurn += (rawTurn - filteredBodyTurn) * Blend(dt, bodyYawFilterTime);
            DesiredRobotYawDeg = robotReference + Mathf.Sign(bodyToRosYawSign) * filteredBodyTurn;
            HeadingErrorDeg = DesiredRobotYawDeg - robotUnwrapped;
            if (Math.Abs(HeadingErrorDeg) > maxTrackingErrorDeg)
                Fault("EXCESSIVE HEADING LAG / REALIGN REQUIRED");
        }

        float linear = 0f;
        if (IsFollowing)
        {
            double error = Math.Abs(HeadingErrorDeg);
            if (error <= headingToleranceDeg) headingSettled = true;
            else if (error >= headingRestartToleranceDeg) headingSettled = false;
            float targetAngular = headingSettled ? 0f : Mathf.Clamp(
                (float)HeadingErrorDeg * Mathf.Deg2Rad * headingKp, -maxAngularSpeed, maxAngularSpeed);
            angularCommand = Mathf.MoveTowards(angularCommand, targetAngular, maxAngularAcceleration * dt);
            linear = CalculateLinear(dt);
        }
        else ResetCommands();

        PublishIfDue(linear, angularCommand);
        if (enableDebugLog && now >= nextLog)
        {
            nextLog = now + Mathf.Max(debugLogInterval, 0.05f);
            Debug.Log($"[KAT HEADING] t={now:F3} {Status} following={IsFollowing} calibrated={HeadingCalibrated} " +
                $"robotEnabled={enableRobotControl} linearEnabled={enableLinearMotion} stop={EmergencyStopEngaged} " +
                $"body={BodyYawDeg:F1} bodyTurn={BodyTurnDeg:F1} " +
                $"target={DesiredRobotYawDeg:F1} robot={RobotYawDeg:F1} robotTurn={RobotTurnDeg:F1} error={HeadingErrorDeg:F1} " +
                $"odomFresh={HasFreshOdometry} bodyFresh={HasFreshBody} odomStamp={lastOdomStamp:F6} " +
                $"katInput=[{BodyInputStatus}] connected={sampledKatConnected} katStamp={sampledKatStamp:R} " +
                $"katAge={now - lastBodyReceipt:F3} rawMove=({sampledMoveSpeed.x:F4},{sampledMoveSpeed.y:F4},{sampledMoveSpeed.z:F4}) " +
                $"acceptedWalk={rawWalking:F4} filteredWalk={filteredWalking:F4} " +
                $"rawQ=({sampledBodyRotation.x:F5},{sampledBodyRotation.y:F5},{sampledBodyRotation.z:F5},{sampledBodyRotation.w:F5}) " +
                $"horizontal={sampledHorizontalMagnitude:F4} lastFault=[{LastFault}] faultTime={lastFaultTime:F3} " +
                $"cmd=({LastPublishedLinear:F3} m/s, {LastPublishedAngular:F3} rad/s)");
        }
    }

    private void ReadBody()
    {
        KATNativeSDK.TreadMillData data;
        try { data = KATNativeSDK.GetWalkStatus(katSerialNumber); }
        catch (Exception ex)
        {
            RejectBody("KAT SDK ERROR: " + ex.GetType().Name);
            return;
        }
        // Capture the actual SDK sample even if validation rejects it. Previously
        // the log showed only the LAST ACCEPTED yaw, hiding why a poll failed.
        sampledKatConnected = data.connected;
        sampledKatStamp = data.lastUpdateTimePoint;
        sampledBodyRotation = data.bodyRotationRaw;
        sampledMoveSpeed = data.moveSpeed;
        sampledHorizontalMagnitude = -1f;
        if (!data.connected)
        {
            RejectBody("KAT REPORTS DISCONNECTED");
            return;
        }
        if (!TryBodyYaw(data.bodyRotationRaw, out float yaw))
        {
            RejectBody(sampledHorizontalMagnitude < 0f ? "KAT INVALID QUATERNION" : "KAT BODY TILT EXCEEDS LIMIT");
            return;
        }
        if (!Finite(data.moveSpeed.x) || !Finite(data.moveSpeed.z))
        {
            RejectBody("KAT INVALID WALK SPEED");
            return;
        }
        if (!Finite(data.lastUpdateTimePoint))
        {
            RejectBody("KAT INVALID TIMESTAMP");
            return;
        }
        float measuredWalking = new Vector2(data.moveSpeed.x, data.moveSpeed.z).magnitude;
        if (!Finite(measuredWalking))
        {
            RejectBody("KAT INVALID WALK MAGNITUDE");
            return;
        }
        if (requireKatTimestampProgress && hasKatStamp && data.lastUpdateTimePoint == lastKatStamp)
        {
            BodyInputStatus = "REPEATED TIMESTAMP";
            // A repeated stamp cannot authorise additional motion. It must not
            // hide a stop/decrease from the SDK either: the previous early return
            // retained a nonzero walking input until katTimeout, even if this
            // poll already reported zero. Do not refresh a step pulse here.
            rawWalking = Mathf.Min(rawWalking, measuredWalking);
            if (measuredWalking <= walkDeadZone)
            {
                filteredWalking = 0f;
                lastStepPulse = -999f;
                if (LastPublishedLinear != 0f) Publish(0f, angularCommand);
            }
            if (!HasFreshBody)
            {
                BodyInputStatus = "TIMESTAMP STALLED";
                Fault("KAT TIMESTAMP STALLED");
            }
            return;
        }
        bool continuous = HasFreshBody;
        float delta = Mathf.DeltaAngle(BodyYawDeg, yaw);
        string discontinuity = !continuous ? "KAT SAMPLE GAP"
            : katDevice != data.deviceName ? "KAT DEVICE CHANGED"
            : hasKatStamp && data.lastUpdateTimePoint < lastKatStamp ? "KAT TIMESTAMP REVERSED"
            : Mathf.Abs(delta) > maxBodyYawStepDeg ? "KAT BODY YAW JUMP" : null;
        if (hasBody && discontinuity != null)
        {
            // Do not silently import OR erase a rejected turn and keep driving.
            Fault(discontinuity);
            continuous = false;
        }
        bodyUnwrapped = continuous ? bodyUnwrapped + delta : yaw;
        BodyYawDeg = yaw;
        rawWalking = measuredWalking;
        // Refresh pulse hold only on an accepted new sample, not every Unity
        // frame using a cached nonzero speed. This also bounds the hold duration.
        if (IsFollowing && enableLinearMotion && enableStepPulseHold
            && rawWalking >= Mathf.Max(stepPulseThreshold, walkDeadZone)) lastStepPulse = Now;
        katDevice = data.deviceName;
        lastKatStamp = data.lastUpdateTimePoint;
        hasKatStamp = hasBody = true;
        lastBodyReceipt = Now;
        BodyInputStatus = "VALID SAMPLE";
    }

    private void RejectBody(string reason)
    {
        hasBody = false;
        rawWalking = 0f;
        BodyInputStatus = reason;
        Fault(reason);
    }

    /// <summary>First start/fault recovery calibrates; normal resume preserves the reference.</summary>
    public void ResumeFollowing()
    {
        string blocked = !isActiveAndEnabled ? "COMPONENT DISABLED"
            : ros == null ? "ROS NOT INITIALISED"
            : !enableRobotControl ? "ENABLE ROBOT CONTROL IS OFF"
            : EmergencyStopEngaged ? "SOFTWARE STOP IS LATCHED"
            : !HasFreshBody ? "KAT NOT FRESH: " + BodyInputStatus
            : !HasFreshOdometry ? "ODOMETRY NOT FRESH" : null;
        if (blocked != null)
        {
            Status = "START BLOCKED / " + blocked;
            if (enableDebugLog) Debug.LogWarning("[KAT HEADING] " + Status);
            return;
        }
        if (!HeadingCalibrated) Calibrate();
        // Check the unfiltered turn as well: a paused user's new position must
        // not bypass the lag guard for one frame while the angle filter catches up.
        double pending = robotReference + Mathf.Sign(bodyToRosYawSign) * BodyTurnDeg - robotUnwrapped;
        if (Math.Abs(pending) > maxTrackingErrorDeg)
        {
            Fault("EXCESSIVE HEADING LAG / REALIGN REQUIRED");
            return;
        }
        IsFollowing = true;
        Status = ReverseModeEnabled ? "FOLLOWING / REVERSE" : "FOLLOWING";
        lastTick = Now;
    }

    public void PauseFollowing()
    {
        IsFollowing = false;
        Status = "PAUSED / REFERENCE PRESERVED";
        StopImmediately();
    }

    /// <summary>Defines both current headings as forward. Always leaves motion paused.</summary>
    public void RealignHeading()
    {
        PauseFollowing();
        if (!HasFreshBody || !HasFreshOdometry)
        {
            HeadingCalibrated = false;
            Status = "REALIGN BLOCKED / NEED FRESH SENSORS";
            return;
        }
        Calibrate();
        Status = "REALIGNED / PAUSED / PRESS TRIGGER TO RESUME";
    }

    public void SetEmergencyStop(bool engaged)
    {
        EmergencyStopEngaged = engaged;
        Fault(engaged ? "SOFTWARE EMERGENCY STOP" : "STOP RELEASED / PRESS TRIGGER TO RECALIBRATE");
    }

    private void Calibrate()
    {
        bodyReference = bodyUnwrapped;
        robotReference = robotUnwrapped;
        filteredBodyTurn = 0;
        DesiredRobotYawDeg = robotReference;
        HeadingErrorDeg = 0;
        headingSettled = true;
        HeadingCalibrated = true;
        ResetCommands();
        if (enableDebugLog) Debug.Log($"[KAT HEADING] Calibrated body={BodyYawDeg:F1}, robot={RobotYawDeg:F1}; gain=1, sign={Mathf.Sign(bodyToRosYawSign)}");
    }

    private void Fault(string reason)
    {
        bool firstOccurrence = LastFault != reason || HeadingCalibrated || IsFollowing;
        IsFollowing = HeadingCalibrated = false;
        if (firstOccurrence)
        {
            lastFaultTime = Now;
            if (enableDebugLog) Debug.LogWarning($"[KAT HEADING] t={Now:F3} STOPPED: {reason}");
        }
        LastFault = reason;
        Status = "PAUSED AFTER " + reason + " / MANUAL START REQUIRED";
        StopImmediately();
    }

    private float CalculateLinear(float dt)
    {
        if (!enableLinearMotion)
        {
            filteredWalking = 0f;
            lastStepPulse = -999f;
            return 0f;
        }
        float input = enableStepPulseHold && Now - lastStepPulse <= stepPulseHoldTime
            ? Mathf.Max(rawWalking, stepPulseInput) : rawWalking;
        filteredWalking = Mathf.Lerp(filteredWalking, input,
            Blend(dt, input > filteredWalking ? forwardAttackTime : forwardReleaseTime));
        if (filteredWalking <= walkDeadZone) return 0f;
        float response = Mathf.Pow(Mathf.Clamp01((filteredWalking - walkDeadZone)
            / Mathf.Max(inputSpeedForMax - walkDeadZone, 0.001f)), responseExponent);
        float maximum = ReverseModeEnabled ? maxReverseSpeed : maxForwardSpeed;
        return (ReverseModeEnabled ? -1f : 1f) * Mathf.Lerp(Mathf.Min(minDriveSpeed, maximum), maximum, response);
    }

    private void ReadControls()
    {
        var device = InputDevices.GetDeviceAtXRNode(controlHand);
        bool trigger = false;
        if (device.isValid)
        {
            if (device.TryGetFeatureValue(CommonUsages.trigger, out float value)) trigger = value > triggerThreshold;
            else device.TryGetFeatureValue(CommonUsages.triggerButton, out trigger);
        }
        trigger |= Key(activationKey);
        bool stop = Button(controlHand, CommonUsages.primaryButton) || Key(emergencyStopKey);
        bool reverse = Button(controlHand, CommonUsages.secondaryButton) || Key(reverseModeKey);
        bool realign = Button(realignHand, CommonUsages.secondaryButton) || Key(realignKey);
        bool stopEdge = stop && !previousStop;
        bool realignEdge = realign && !previousRealign;
        if (stopEdge) SetEmergencyStop(!EmergencyStopEngaged);
        // Stop/realign wins over a simultaneous trigger press, including release.
        else if (realignEdge) RealignHeading();
        else if (trigger && !previousTrigger)
        {
            if (IsFollowing) PauseFollowing();
            else ResumeFollowing();
        }
        if (reverse && !previousReverse)
        {
            ReverseModeEnabled = !ReverseModeEnabled;
            filteredWalking = 0f;
            lastStepPulse = -999f;
            if (IsFollowing) Status = ReverseModeEnabled ? "FOLLOWING / REVERSE" : "FOLLOWING";
        }
        previousTrigger = trigger;
        previousStop = stop;
        previousReverse = reverse;
        previousRealign = realign;
    }

    private bool Key(KeyCode key) => enableKeyboardFallback && Input.GetKey(key);
    private static bool Button(XRNode hand, InputFeatureUsage<bool> usage)
    {
        var device = InputDevices.GetDeviceAtXRNode(hand);
        return device.isValid && device.TryGetFeatureValue(usage, out bool pressed) && pressed;
    }

    private void ResetCommands()
    {
        filteredWalking = angularCommand = 0f;
        lastStepPulse = -999f;
    }

    private void StopImmediately()
    {
        ResetCommands();
        // Bypass the publish interval on a moving -> stopped transition.
        if (LastPublishedLinear != 0f || LastPublishedAngular != 0f) Publish(0f, 0f);
    }

    private void PublishIfDue(float linear, float angular)
    {
        if (Now < nextPublish) return;
        nextPublish = Now + 1f / Mathf.Max(publishRateHz, 1f);
        Publish(linear, angular);
    }

    private void Publish(float linear, float angular)
    {
        if (!IsFollowing || !enableRobotControl || EmergencyStopEngaged || !HasFreshOdometry || !HasFreshBody
            || !Finite(linear) || !Finite(angular)) linear = angular = 0f;
        if (ros == null) return;
        ros.Publish(registeredCommandTopic, new TwistMsg
        {
            linear = new Vector3Msg { x = linear },
            angular = new Vector3Msg { z = angular }
        });
        LastPublishedLinear = linear;
        LastPublishedAngular = angular;
    }

    private bool TryBodyYaw(Quaternion q, out float yaw)
    {
        yaw = 0f;
        double norm = (double)q.x * q.x + (double)q.y * q.y + (double)q.z * q.z + (double)q.w * q.w;
        if (!Finite(norm) || norm < 1e-12) return false;
        float scale = (float)(1.0 / Math.Sqrt(norm));
        Vector3 forward = new Quaternion(q.x * scale, q.y * scale, q.z * scale, q.w * scale) * Vector3.forward;
        float horizontal = forward.x * forward.x + forward.z * forward.z;
        sampledHorizontalMagnitude = Mathf.Sqrt(horizontal);
        float minimum = Mathf.Cos(maxBodyTiltDeg * Mathf.Deg2Rad);
        if (horizontal < minimum * minimum) return false;
        yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        return Finite(yaw);
    }

    private static bool TryRobotYaw(QuaternionMsg q, out float yaw)
    {
        yaw = 0f;
        if (q == null) return false;
        double norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        if (!Finite(norm) || norm < 1e-12) return false;
        // Quaternion normalisation without converting the ROS frame to Unity.
        yaw = (float)(Math.Atan2(2 * (q.w * q.z + q.x * q.y) / norm,
            1 - 2 * (q.y * q.y + q.z * q.z) / norm) * Mathf.Rad2Deg);
        return Finite(yaw);
    }

    private static float Blend(float dt, float tau) => tau <= 0f ? 1f : 1f - Mathf.Exp(-dt / tau);
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private void OnDisable()
    {
        IsFollowing = HeadingCalibrated = false;
        Status = "DISABLED";
        ResetCommands();
        Publish(0f, 0f);
    }

    private void OnApplicationQuit() => OnDisable();
}
