using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
using RosMessageTypes.Nav;

/// <summary>
/// Standalone simulation test rig for tuning the KAT-Husky closed-loop heading
/// controller WITHOUT the physical KAT device and without touching
/// KatHuskyClosedLoopController.cs at all. It is a self-contained clone of the
/// same control algorithm (filters, dead zones, P feedback, speed shaping),
/// driven by mouse/keyboard instead of KATNativeSDK.
///
/// Input mapping (mimics "twisting your torso" and "walking speed"):
///   - Mouse X movement (hold Right Mouse Button)  -> simulated body yaw rate.
///     Move the mouse SLOWLY to test the "slow twist -> feels delayed" case,
///     move it QUICKLY to test the "small twist -> turn too large" case.
///   - W / S (or Up / Down)                        -> simulated forward/backward walking speed.
///   - C                                            -> recalibrate heading (like the real controller).
///   - R                                            -> reset simulated body yaw back to 0.
///
/// Talks to ROS exactly like the production controller (/cmd_vel publish,
/// /odometry/filtered subscribe), so you can point it at a real robot, a
/// Gazebo simulation, or the lightweight fake_husky_sim.py node.
/// </summary>
[DisallowMultipleComponent]
public class KatHuskySimTestRig : MonoBehaviour
{
    [Header("ROS")]
    public string cmdVelTopic = "/cmd_vel";
    public string odometryTopic = "/odometry/filtered";
    public float odometryTimeout = 0.5f;
    public float publishRateHz = 20f;

    [Header("Simulated KAT input")]
    [Tooltip("Degrees of simulated body yaw rate per unit of Input.GetAxis(\"Mouse X\"). Larger = more sensitive twist.")]
    public float mouseYawSensitivityDegPerSec = 300f;
    public KeyCode mouseYawEnableKey = KeyCode.Mouse1;
    [Tooltip("Simulated raw walking speed at full W/S input (same units as KAT moveSpeed.z).")]
    public float simulatedMaxRawWalkSpeed = 0.6f;
    public KeyCode recalibrateKey = KeyCode.C;
    public KeyCode resetBodyYawKey = KeyCode.R;

    [Header("Linear control (mirrors production defaults)")]
    public float maxForwardSpeed = 0.35f;
    public float maxReverseSpeed = 0.20f;
    public float walkDeadZone = 0.02f;
    public float inputSpeedForMax = 0.50f;
    public float minDriveSpeed = 0.08f;
    [Range(0.3f, 1f)] public float responseExponent = 0.60f;
    public float forwardAttackTime = 0.04f;
    public float forwardReleaseTime = 0.15f;

    [Header("Closed-loop heading (mirrors current tuned scene values)")]
    public float bodyToRosYawSign = -1f;
    public float bodyHeadingScale = 1f;
    public float yawRateFeedforwardGain = 0.15f;
    public float headingKp = 0.8f;
    public float headingErrorDeadZoneDeg = 2f;
    public float bodyYawFilterTime = 0.10f;
    public float maxAcceptedBodyYawStepDeg = 45f;
    public int maxConsecutiveRejectedBodyYawSteps = 4;
    [Range(0f, 1f)] public float walkingBodyYawAccumulationScale = 0.4f;
    public float bodyYawRateDeadZoneDegPerSec = 4f;
    public float bodyYawRateSmoothingTime = 0.08f;
    public float maxAngularSpeed = 0.35f;
    public float maxAngularAcceleration = 1f;

    [Header("Turn-aware forward speed")]
    public float slowDownHeadingErrorDeg = 10f;
    public float stopForwardHeadingErrorDeg = 35f;
    [Range(0f, 1f)] public float minimumTurningForwardMultiplier = 0f;

    [Header("Diagnostics")]
    public bool showStatusGUI = true;

    private ROSConnection ros;
    private bool hasOdometry;
    private float robotYawDeg;
    private float lastOdometryTime = -999f;

    // Simulated KAT state
    private float simulatedBodyYawDeg;
    private float simulatedRawWalkSpeed;

    // Same internal state as the production controller
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
    private float nextPublishTime;
    private string status = "SIM STANDBY";

    private void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<TwistMsg>(cmdVelTopic);
        ros.Subscribe<OdometryMsg>(odometryTopic, OnOdometry);
        status = "SIM WAITING FOR CALIBRATION";
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
        UpdateSimulatedInput();

        if (Input.GetKeyDown(resetBodyYawKey))
        {
            simulatedBodyYawDeg = 0f;
        }

        bool odometryFresh = hasOdometry && (Time.unscaledTime - lastOdometryTime <= Mathf.Max(odometryTimeout, 0.1f));

        if (Input.GetKeyDown(recalibrateKey) && odometryFresh)
        {
            CalibrateHeading(simulatedBodyYawDeg);
        }

        if (!headingCalibrated && odometryFresh)
        {
            CalibrateHeading(simulatedBodyYawDeg);
        }

        if (!odometryFresh)
        {
            status = hasOdometry ? "SIM: ODOMETRY STALE" : "SIM: WAITING FOR ODOMETRY";
            headingCalibrated = false;
            PublishIfDue(0f, 0f);
            return;
        }

        UpdateBodyHeading(simulatedBodyYawDeg);
        float forward = FilterForward(simulatedRawWalkSpeed);
        float linear = CalculateLinear(forward);
        float angular = CalculateAngular();
        linear *= CalculateTurningSpeedMultiplier(odometryFresh);

        status = "SIM ACTIVE";
        PublishIfDue(linear, angular);
    }

    private void UpdateSimulatedInput()
    {
        float mouseYawRateDegPerSec = 0f;
        if (Input.GetKey(mouseYawEnableKey))
        {
            mouseYawRateDegPerSec = Input.GetAxis("Mouse X") * mouseYawSensitivityDegPerSec;
        }
        simulatedBodyYawDeg += mouseYawRateDegPerSec * Time.fixedDeltaTime;

        float walkAxis = Input.GetAxis("Vertical"); // W/S or Up/Down, -1..1
        simulatedRawWalkSpeed = walkAxis * simulatedMaxRawWalkSpeed;
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
        Debug.Log($"[SIM] Heading calibrated: body {bodyYawDeg:F1} deg -> robot {robotYawDeg:F1} deg.");
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
                filteredBodyYawRate = 0f;
                return;
            }

            // Persisted for several samples in a row -> treat as real, adopt
            // as the new baseline without importing the jump into the target.
            lastRawBodyYawDeg = rawBodyYawDeg;
            filteredBodyYawDeg = rawBodyYawDeg;
            previousBodyYawDeg = rawBodyYawDeg;
            filteredBodyYawRate = 0f;
            rejectedBodyYawStepCount = 0;
            return;
        }

        rejectedBodyYawStepCount = 0;
        lastRawBodyYawDeg = rawBodyYawDeg;
        float yawBlend = 1f - Mathf.Exp(-Time.fixedDeltaTime / Mathf.Max(bodyYawFilterTime, 0.01f));
        filteredBodyYawDeg = Mathf.LerpAngle(filteredBodyYawDeg, rawBodyYawDeg, yawBlend);

        float delta = Mathf.DeltaAngle(previousBodyYawDeg, filteredBodyYawDeg);
        previousBodyYawDeg = filteredBodyYawDeg;

        bool isWalking = Mathf.Abs(simulatedRawWalkSpeed) > walkDeadZone;
        float scaledDelta = isWalking ? delta * Mathf.Clamp01(walkingBodyYawAccumulationScale) : delta;
        accumulatedBodyYawDeg += scaledDelta;

        float rawRate = scaledDelta / Mathf.Max(Time.fixedDeltaTime, 0.001f);
        float blend = 1f - Mathf.Exp(-Time.fixedDeltaTime / Mathf.Max(bodyYawRateSmoothingTime, 0.01f));
        filteredBodyYawRate = Mathf.Lerp(filteredBodyYawRate, rawRate, blend);

        desiredRobotYawDeg = robotYawReferenceDeg
            + Mathf.Sign(bodyToRosYawSign) * accumulatedBodyYawDeg * bodyHeadingScale;
        headingErrorDeg = Mathf.DeltaAngle(robotYawDeg, desiredRobotYawDeg);
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
        if (headingCalibrated && Mathf.Abs(headingErrorDeg) > headingErrorDeadZoneDeg)
        {
            float effectiveError = headingErrorDeg - Mathf.Sign(headingErrorDeg) * headingErrorDeadZoneDeg;
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

    private float CalculateTurningSpeedMultiplier(bool odometryFresh)
    {
        if (!odometryFresh || !headingCalibrated)
        {
            return 1f;
        }

        float error = Mathf.Abs(headingErrorDeg);
        float range = Mathf.Max(stopForwardHeadingErrorDeg - slowDownHeadingErrorDeg, 0.1f);
        float t = Mathf.Clamp01((error - slowDownHeadingErrorDeg) / range);
        return Mathf.Lerp(1f, minimumTurningForwardMultiplier, t);
    }

    private void PublishIfDue(float linear, float angular)
    {
        if (Time.unscaledTime < nextPublishTime) return;
        nextPublishTime = Time.unscaledTime + 1f / Mathf.Max(publishRateHz, 1f);
        TwistMsg message = new TwistMsg(
            new Vector3Msg(linear, 0.0, 0.0),
            new Vector3Msg(0.0, 0.0, angular)
        );
        ros?.Publish(cmdVelTopic, message);
        lastPublishedLinear = linear;
        lastPublishedAngular = angular;
    }

    private void OnGUI()
    {
        if (!showStatusGUI) return;
        GUIStyle style = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
        style.normal.textColor = Color.yellow;
        GUI.Label(new Rect(20, 90, 900, 30), $"[SIM RIG] {status}", style);
        GUI.Label(new Rect(20, 120, 1100, 30),
            $"SimBodyYaw {simulatedBodyYawDeg:F1}°  Robot {robotYawDeg:F1}°  Target {desiredRobotYawDeg:F1}°  Error {headingErrorDeg:F1}°  " +
            $"cmd_vel ({lastPublishedLinear:F3}, {lastPublishedAngular:F3})", style);
        GUI.Label(new Rect(20, 150, 1100, 60),
            "Hold RMB + move mouse X = simulate torso twist (slow = small delay test, fast = big-turn test)\n" +
            "W/S = simulated walking speed   C = recalibrate   R = reset simulated body yaw", style);
    }

    private void OnDisable()
    {
        if (ros != null)
        {
            ros.Publish(cmdVelTopic, new TwistMsg(new Vector3Msg(0, 0, 0), new Vector3Msg(0, 0, 0)));
        }
    }

    private void OnApplicationQuit()
    {
        if (ros != null)
        {
            ros.Publish(cmdVelTopic, new TwistMsg(new Vector3Msg(0, 0, 0), new Vector3Msg(0, 0, 0)));
        }
    }
}
