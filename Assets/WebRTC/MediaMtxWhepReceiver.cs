using System.Collections;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Nav;

/// <summary>
/// Receives the Insta360 H.264 stream from MediaMTX through WHEP.
/// Attach this to a GameObject and assign the inside-out sphere Renderer.
/// </summary>
[DefaultExecutionOrder(-100)]
public sealed class MediaMtxWhepReceiver : MonoBehaviour
{
    public enum PanoramaHeadingMode { Custom, RobotRelative, WorldStabilized }
    [Header("Husky MediaMTX")]
    [Tooltip("WHEP endpoint exposed by the Husky.")]
    public string whepUrl = "http://192.168.2.36:8889/insta360/whep";

    [Header("Video output")]
    [Tooltip("Renderer of the sphere viewed from the inside.")]
    public Renderer targetRenderer;
    [Tooltip("Optional flat preview for initial testing.")]
    public RawImage targetRawImage;
    [Tooltip("Use the unlit, double-sided material required when viewing a sphere from inside.")]
    public bool useInsideSphereMaterial = true;
    [Tooltip("Mirror the panorama texture horizontally to correct left/right reversal when viewed inside the sphere. Recalibrate forward after changing this.")]
    public bool flipVideoHorizontally = true;
    [Tooltip("Keep the panorama centered on the VR camera; headset rotation remains controlled by XR.")]
    public bool centerSphereOnMainCamera = true;

    [Header("Panorama heading alignment")]
    [Tooltip("Apply heading correction to the video sphere. Head tracking remains independent. Select the source mode after the stationary-head rotation test.")]
    public bool compensateRobotHeading = true;
    [Tooltip("RobotRelative: feed rotates with the camera/robot, use physical ROS-to-Unity yaw. WorldStabilized: camera already holds world yaw, keep the sphere fixed. Custom preserves the existing compensation sign until the source is checked.")]
    public PanoramaHeadingMode panoramaHeadingMode = PanoramaHeadingMode.Custom;
    public string headingOdometryTopic = "/odometry/filtered";
    [Tooltip("Custom mode only: sphere yaw per ROS yaw. Standard physical ROS positive-left yaw maps to negative Unity yaw. Verify video left/right before calibration.")]
    public float headingCompensationSign = 1f;
    public float headingTimeout = 1f;
    [Tooltip("Measured extra video delay relative to odometry receipt, seconds. Delays only video compensation; the robot indicator uses current heading. Zero preserves existing timing. This is an approximate manual offset, not frame timestamp synchronization.")]
    [Range(0f, 2f)] public float videoHeadingDelaySeconds = 0f;

    [Header("Corrected heading and automatic alignment")]
    [Tooltip("Add the KAT controller's SLAM heading correction to the odometry yaw. Wheel-only odometry reports false yaw while reversing straight, which used to turn the panorama.")]
    public bool useSlamCorrectedHeading = true;
    [Tooltip("Source of the correction. Empty: the one in the scene.")]
    public KatHuskyDirectAngleController headingCorrectionSource;
    [Tooltip("F6/F7 stores only the camera-to-robot mount angle. Each trigger activation (and each start) aligns the robot front with the direction the headset faces at that moment, so no per-session recalibration is needed.")]
    public bool alignViewOnActivation = true;

    [Header("Heading diagnostics")]
    public bool showHeadingDiagnostics = false;
    public bool logHeadingDiagnostics = false;
    public bool HasFreshHeading => isActiveAndEnabled && hasRobotHeading && headingFeedbackUsable
        && feedbackFreshness.IsFresh(Time.realtimeSinceStartup, Mathf.Max(0.1f, headingTimeout));
    // Heading in the KAT controller's SLAM-corrected frame (odometry yaw + correction).
    public float RobotHeadingDeg => Mathf.Repeat(CurrentRobotYaw, 360f);
    public bool HasCalibratedHeading => HasFreshHeading && hasCalibrationReference && !calibratingForward;
    public bool IsHeadingCalibrated => hasCalibrationReference && !calibratingForward;
    public string HeadingAlignmentStatus => calibratingForward || !hasMountCalibration ? "CALIBRATE VIEW (F6 / F7)"
        : !hasCalibrationReference ? "LOOK FORWARD / ALIGNING VIEW"
        : !HasFreshHeading ? "HEADING UNAVAILABLE"
        : panoramaHeadingMode == PanoramaHeadingMode.Custom ? "CUSTOM VIEW / VERIFY ALIGNMENT" : "VIEW ALIGNED";
    // Compensation off = first-person view: the video, and so the robot front,
    // stays where it was aligned while the robot turns. Markers follow the same
    // rule: the robot front is fixed and other headings are drawn relative to it.
    private bool RobotFixedView => !compensateRobotHeading;
    public float RobotWorldYawDeg => RobotFixedView ? RobotViewHeading.Wrap(calibrationWorldForwardYaw)
        : WorldYawForRosHeading(CurrentRobotYaw);
    public float HeadingCorrectionDeg => useSlamCorrectedHeading && headingCorrectionSource != null
        && headingCorrectionSource.isActiveAndEnabled ? (float)headingCorrectionSource.SlamCorrectionDeg : 0f;
    private float CurrentRobotYaw => latestRobotYaw + HeadingCorrectionDeg;
    // Consumers must check HasCalibratedHeading before treating this conversion as valid.
    public float WorldYawForRosHeading(double rosYawDeg) => RobotViewHeading.WorldYaw(calibrationWorldForwardYaw,
        RobotFixedView ? CurrentRobotYaw : initialRobotYaw, rosYawDeg);
    private CmdVelPublisher keyboardPublisher;
    private float odometryForwardSpeed;
    private float odometryYawRate;
    private bool hasValidMotionForCalibration;
    private float latestYawStep;
    private float nextDiagnosticTime;
    private float nextHeadingTraceTime;
    private int calibrationRevision;
    private string diagnosticText = "Waiting for heading data...";

    [Header("Forward calibration")]
    [Tooltip("Restore the saved video/odometry alignment. Recalibrate after either source resets its heading origin.")]
    public bool rememberForwardCalibration = true;
    public KeyCode beginCalibrationKey = KeyCode.F6;
    public KeyCode saveCalibrationKey = KeyCode.F7;
    [Tooltip("Desktop calibration panel; F6 also opens it while testing with a Quest.")]
    public bool showCalibrationPanel = true;

    [System.Serializable]
    private class ForwardCalibration
    {
        public Quaternion sphereRotation;
        public float robotYaw;
        public float compensationSign;
        public bool flipVideoHorizontally;
        public string odometryFrame;
        public string odometryChildFrame;
        public PanoramaHeadingMode mode;
        public bool compensationEnabled;
        public float worldForwardYaw;
    }

    private bool calibratingForward;
    private bool hasCalibrationReference;
    // Sphere rotation relative to the headset yaw at which the robot front was
    // saved: the fixed camera mount angle, independent of odometry origin and
    // XR recentring.
    private bool hasMountCalibration;
    private Quaternion mountRelativeRotation = Quaternion.identity;
    private bool realignPending;
    private bool lastControllerActive;
    private bool previousHorizontalFlip;
    private PanoramaHeadingMode previousHeadingMode;
    private bool previousCompensationEnabled;
    private float previousCompensationSign;
    private string latestOdometryFrame;
    private string latestOdometryChildFrame;
    private string referenceOdometryFrame;
    private string referenceOdometryChildFrame;
    private float calibrationWorldForwardYaw;
    private double lastHeadingStamp = double.NegativeInfinity;
    private readonly RobotFeedbackFreshness feedbackFreshness = new RobotFeedbackFreshness();
    private bool headingFeedbackUsable;
    private readonly PanoramaHeadingHistory headingHistory = new PanoramaHeadingHistory();
    private string calibrationMessage = "Keep the robot stopped and your head facing forward. Align the image with the real robot front, then save.";
    private string CalibrationKey => "Insta360.ForwardCalibration.v2|" + whepUrl + "|" + headingOdometryTopic;

    private Quaternion initialSphereRotation;
    private float initialRobotYaw;
    private float latestRobotYaw;
    private float lastHeadingTime;
    private float headingStartTime;
    private bool hasRobotHeading;
    private bool headingWarningShown;

    [Header("Startup")]
    public bool startOnAwake = true;
    public bool autoReconnect = true;
    [Min(0.5f)] public float reconnectDelay = 2f;
    [Min(2f)] public float iceGatheringTimeout = 10f;

    private RTCPeerConnection peerConnection;
    private VideoStreamTrack receivedTrack;
    private Coroutine webRtcUpdateCoroutine;
    private Coroutine connectCoroutine;
    private bool stopping;
    private Material runtimeVideoMaterial;

    private void Start()
    {
        previousHorizontalFlip = flipVideoHorizontally;
        previousHeadingMode = panoramaHeadingMode;
        previousCompensationEnabled = compensateRobotHeading;
        previousCompensationSign = headingCompensationSign;
        keyboardPublisher = FindObjectOfType<CmdVelPublisher>();
        if (headingCorrectionSource == null) headingCorrectionSource = FindObjectOfType<KatHuskyDirectAngleController>();
        if (targetRenderer != null)
            initialSphereRotation = targetRenderer.transform.rotation;
        LoadForwardCalibration();
        if (!hasMountCalibration) BeginForwardCalibration();
        else realignPending = true;
        headingStartTime = Time.realtimeSinceStartup;
        // Even a world-stabilized video needs heading for the physical robot marker.
        ROSConnection.GetOrCreateInstance().Subscribe<OdometryMsg>(headingOdometryTopic, OnHeadingOdometry);
        if (centerSphereOnMainCamera && targetRenderer != null &&
            Camera.main != null)
        {
            targetRenderer.transform.position = Camera.main.transform.position;
        }

        if (startOnAwake)
            Connect();
    }

    private void OnHeadingOdometry(OdometryMsg message)
    {
        // The connector shares topic subscribers. Do not unsubscribe the entire
        // topic on destruction: the KAT controller may also be listening to it.
        if (this == null || !isActiveAndEnabled) return;
        if (message == null || message.header == null || message.header.stamp == null
            || message.pose == null || message.pose.pose == null || message.pose.pose.orientation == null) return;
        var q = message.pose.pose.orientation;
        double norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        if (double.IsNaN(norm) || double.IsInfinity(norm) || norm < 1e-12) return;
        double stamp = message.header.stamp.sec + message.header.stamp.nanosec * 1e-9;
        if (stamp <= 0 || message.header.stamp.nanosec >= 1000000000) return;
        var freshness = feedbackFreshness.Observe(stamp, Time.realtimeSinceStartup, Mathf.Max(0.1f, headingTimeout));
        if (freshness == RobotFeedbackFreshness.Observation.Duplicate) return;
        if (freshness == RobotFeedbackFreshness.Observation.Invalid
            || freshness == RobotFeedbackFreshness.Observation.Backlogged)
        {
            // Receiving an old packet is not evidence of the robot's current pose.
            // Keep the last accepted yaw, but immediately invalidate its markers.
            headingFeedbackUsable = false;
            return;
        }
        bool sourceReset = freshness == RobotFeedbackFreshness.Observation.SourceReset;
        headingFeedbackUsable = true;
        lastHeadingStamp = stamp;
        float newYaw = (float)(System.Math.Atan2(
            2.0 * (q.w * q.z + q.x * q.y),
            norm - 2.0 * (q.y * q.y + q.z * q.z)) * Mathf.Rad2Deg);
        latestYawStep = hasRobotHeading ? Mathf.DeltaAngle(latestRobotYaw, newYaw) : 0f;
        sourceReset |= hasRobotHeading && (latestOdometryFrame != message.header.frame_id
            || latestOdometryChildFrame != message.child_frame_id || Mathf.Abs(latestYawStep) > 45f);
        if (sourceReset) headingHistory.Clear();
        latestRobotYaw = newYaw;
        hasValidMotionForCalibration = false;
        if (message.twist != null && message.twist.twist != null
            && message.twist.twist.linear != null && message.twist.twist.angular != null)
        {
            odometryForwardSpeed = (float)message.twist.twist.linear.x;
            odometryYawRate = (float)message.twist.twist.angular.z * Mathf.Rad2Deg;
            hasValidMotionForCalibration = !float.IsNaN(odometryForwardSpeed) && !float.IsInfinity(odometryForwardSpeed)
                && !float.IsNaN(odometryYawRate) && !float.IsInfinity(odometryYawRate);
        }
        latestOdometryFrame = message.header.frame_id;
        latestOdometryChildFrame = message.child_frame_id;
        if (hasCalibrationReference && (sourceReset || latestOdometryFrame != referenceOdometryFrame
            || latestOdometryChildFrame != referenceOdometryChildFrame))
        {
            hasCalibrationReference = false;
            if (hasMountCalibration && alignViewOnActivation)
            {
                // The mount angle is still valid; only the session reference moved.
                realignPending = true;
            }
            else
            {
                BeginForwardCalibration();
                calibrationMessage = "Heading source reset or jumped. Stop the robot, align its real front again and save.";
            }
        }
        if (!hasRobotHeading)
        {
            hasRobotHeading = true;
        }
        lastHeadingTime = Time.realtimeSinceStartup;
        headingHistory.Add(lastHeadingTime, latestRobotYaw);
        headingWarningShown = false;
    }

    private void Update()
    {
        if (previousHorizontalFlip != flipVideoHorizontally || previousHeadingMode != panoramaHeadingMode
            || previousCompensationEnabled != compensateRobotHeading || previousCompensationSign != headingCompensationSign)
        {
            previousHorizontalFlip = flipVideoHorizontally;
            previousHeadingMode = panoramaHeadingMode;
            previousCompensationEnabled = compensateRobotHeading;
            previousCompensationSign = headingCompensationSign;
            hasCalibrationReference = false;
            hasMountCalibration = false;   // the saved mount angle belongs to the old mapping
            ApplyVideoOrientation();
            BeginForwardCalibration();
        }
        if (Input.GetKeyDown(beginCalibrationKey)) BeginForwardCalibration();
        if (calibratingForward && Input.GetKeyDown(saveCalibrationKey)) SaveForwardCalibration();

        bool controllerActive = headingCorrectionSource != null && headingCorrectionSource.IsControlActive;
        if (alignViewOnActivation && controllerActive && !lastControllerActive) realignPending = true;
        lastControllerActive = controllerActive;
        if (realignPending && !calibratingForward && hasMountCalibration && HasFreshHeading) RealignView();
    }

    /// <summary>
    /// The direction the headset faces now becomes the robot front. Pressing the
    /// trigger is the same moment the KAT controller takes the body direction as
    /// the robot's heading, so the operator should face forward then.
    /// </summary>
    private void RealignView()
    {
        if (targetRenderer == null || Camera.main == null) return;
        float headYaw = Camera.main.transform.eulerAngles.y;
        calibrationWorldForwardYaw = headYaw;
        initialSphereRotation = Quaternion.Euler(0f, headYaw, 0f) * mountRelativeRotation;
        initialRobotYaw = CurrentRobotYaw;
        referenceOdometryFrame = latestOdometryFrame;
        referenceOdometryChildFrame = latestOdometryChildFrame;
        hasCalibrationReference = true;
        realignPending = false;
        calibrationRevision++;
        Debug.Log($"Panorama view aligned: robot front = headset yaw {headYaw:F1}, heading {initialRobotYaw:F1} (correction {HeadingCorrectionDeg:F1}).");
    }

    private void LateUpdate()
    {
        if (targetRenderer == null) { RecordHeadingTrace(); return; }
        if (centerSphereOnMainCamera && Camera.main != null)
            targetRenderer.transform.position = Camera.main.transform.position;

        double age = hasRobotHeading ? feedbackFreshness.AgeSeconds(Time.realtimeSinceStartup)
            : Time.realtimeSinceStartup - headingStartTime;
        if (age > Mathf.Max(0.1f, headingTimeout) && !headingWarningShown)
        {
            Debug.LogWarning($"Panorama heading: no fresh {headingOdometryTopic}; holding the last view correction.");
            headingWarningShown = true;
        }
        if (HasCalibratedHeading)
        {
            // An empty history (just reset) samples as 0; use the latest yaw then.
            if (!headingHistory.TrySample(Time.realtimeSinceStartup - Mathf.Clamp(videoHeadingDelaySeconds, 0f, 2f),
                    out float displayedYaw))
                displayedYaw = latestRobotYaw;
            displayedYaw += HeadingCorrectionDeg;
            float delta = Mathf.DeltaAngle(initialRobotYaw, displayedYaw);
            float sign = !compensateRobotHeading || panoramaHeadingMode == PanoramaHeadingMode.WorldStabilized ? 0f
                : panoramaHeadingMode == PanoramaHeadingMode.RobotRelative ? -1f : headingCompensationSign;
            targetRenderer.transform.rotation =
                Quaternion.AngleAxis(sign * delta, Vector3.up) * initialSphereRotation;
        }
        UpdateHeadingDiagnostics();
        RecordHeadingTrace();
    }

    [ContextMenu("Begin Forward Calibration")]
    public void BeginForwardCalibration()
    {
        if (!Application.isPlaying || targetRenderer == null) return;
        calibratingForward = true;
        calibrationRevision++;
        calibrationMessage = "Keep the robot stopped and your head facing forward. Align the image with the real robot front, then save.";
    }

    [ContextMenu("Save Forward Calibration")]
    public void SaveForwardCalibration()
    {
        if (!Application.isPlaying || !calibratingForward || targetRenderer == null) return;
        if (!HasFreshHeading)
        {
            calibrationMessage = "Cannot save: waiting for fresh odometry on " + headingOdometryTopic;
            Debug.LogWarning(calibrationMessage);
            return;
        }
        if (!hasValidMotionForCalibration)
        {
            calibrationMessage = "Cannot save: waiting for valid robot motion feedback.";
            return;
        }
        if (Mathf.Abs(odometryForwardSpeed) > 0.02f || Mathf.Abs(odometryYawRate) > 2f)
        {
            calibrationMessage = "Stop the robot before saving the forward alignment.";
            return;
        }
        initialSphereRotation = targetRenderer.transform.rotation;
        initialRobotYaw = CurrentRobotYaw;
        calibrationWorldForwardYaw = Camera.main != null ? Camera.main.transform.eulerAngles.y : 0f;
        mountRelativeRotation = Quaternion.Euler(0f, -calibrationWorldForwardYaw, 0f) * initialSphereRotation;
        hasMountCalibration = true;
        realignPending = false;
        referenceOdometryFrame = latestOdometryFrame;
        referenceOdometryChildFrame = latestOdometryChildFrame;
        hasCalibrationReference = true;
        calibratingForward = false;
        calibrationRevision++;
        if (rememberForwardCalibration)
        {
            var calibration = new ForwardCalibration
            {
                sphereRotation = initialSphereRotation,
                robotYaw = initialRobotYaw,
                compensationSign = headingCompensationSign,
                flipVideoHorizontally = flipVideoHorizontally,
                odometryFrame = referenceOdometryFrame,
                odometryChildFrame = referenceOdometryChildFrame,
                mode = panoramaHeadingMode,
                compensationEnabled = compensateRobotHeading,
                worldForwardYaw = calibrationWorldForwardYaw
            };
            PlayerPrefs.SetString(CalibrationKey, JsonUtility.ToJson(calibration));
            PlayerPrefs.Save();
        }
        Debug.Log($"Panorama forward calibrated: sphere Y={initialSphereRotation.eulerAngles.y:F1}, ROS yaw={initialRobotYaw:F1}, mount angle {mountRelativeRotation.eulerAngles.y:F1}. Later sessions align automatically; recalibrate only if the camera is remounted.");
    }

    private void LoadForwardCalibration()
    {
        if (!rememberForwardCalibration || !PlayerPrefs.HasKey(CalibrationKey)) return;
        try
        {
            var saved = JsonUtility.FromJson<ForwardCalibration>(PlayerPrefs.GetString(CalibrationKey));
            if (saved == null || !Mathf.Approximately(saved.compensationSign, headingCompensationSign)) return;
            if (saved.flipVideoHorizontally != flipVideoHorizontally) return;
            if (saved.mode != panoramaHeadingMode || saved.compensationEnabled != compensateRobotHeading) return;
            Quaternion q = saved.sphereRotation;
            float norm = Quaternion.Dot(q, q);
            if (float.IsNaN(norm) || float.IsInfinity(norm) || norm < 0.0001f ||
                float.IsNaN(saved.robotYaw) || float.IsInfinity(saved.robotYaw)
                || float.IsNaN(saved.worldForwardYaw) || float.IsInfinity(saved.worldForwardYaw)) return;
            // Keep only the mount angle. The saved odometry yaw and headset yaw
            // belonged to that session's origins (they change on every robot or
            // XR restart), so the session reference is re-taken by RealignView.
            mountRelativeRotation = Quaternion.Euler(0f, -saved.worldForwardYaw, 0f) * q.normalized;
            hasMountCalibration = true;
            if (!alignViewOnActivation)
            {
                initialSphereRotation = q.normalized;
                initialRobotYaw = saved.robotYaw;
                referenceOdometryFrame = saved.odometryFrame;
                referenceOdometryChildFrame = saved.odometryChildFrame;
                calibrationWorldForwardYaw = saved.worldForwardYaw;
                hasCalibrationReference = true;
            }
            calibrationRevision++;
        }
        catch (System.ArgumentException)
        {
            Debug.LogWarning("Saved panorama calibration is invalid; please calibrate again.");
        }
    }

    private void OnGUI()
    {
        if (showHeadingDiagnostics)
            GUI.Box(new Rect(20, Screen.height - 140, 620, 130), diagnosticText);
        if (!showCalibrationPanel || !calibratingForward || targetRenderer == null) return;
        GUILayout.BeginArea(new Rect(20, 60, 550, 440), GUI.skin.box);
        GUILayout.Label("Panorama: calibrate robot forward");
        GUILayout.Label("Source mode: " + panoramaHeadingMode);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Camera turns with robot")) SetHeadingMode(PanoramaHeadingMode.RobotRelative);
        if (GUILayout.Button("Video already holds world yaw")) SetHeadingMode(PanoramaHeadingMode.WorldStabilized);
        GUILayout.EndHorizontal();
        GUILayout.Label("Check left/right first. Extra video delay relative to heading: " + videoHeadingDelaySeconds.ToString("F2") + " s");
        videoHeadingDelaySeconds = GUILayout.HorizontalSlider(videoHeadingDelaySeconds, 0f, 2f);
        bool requestedFlip = GUILayout.Toggle(flipVideoHorizontally, "Flip horizontal (swap video left / right)");
        if (requestedFlip != flipVideoHorizontally)
        {
            flipVideoHorizontally = requestedFlip;
            previousHorizontalFlip = requestedFlip;
            hasCalibrationReference = false;
            ApplyVideoOrientation();
        }
        Material shownMaterial = targetRenderer.sharedMaterial;
        bool supportsFlip = shownMaterial != null && shownMaterial.HasProperty("_FlipHorizontal");
        GUILayout.Label(supportsFlip
            ? "Video flip applied: " + (shownMaterial.GetFloat("_FlipHorizontal") > 0.5f ? "ON" : "OFF")
            : "Waiting for video material with flip support...");
        GUILayout.Label(calibrationMessage);
        GUILayout.Label("Fresh odometry: " + HasFreshHeading);
        Vector3 angles = targetRenderer.transform.eulerAngles;
        GUILayout.Label($"Sphere yaw: {angles.y:F1} degrees");
        float yaw = GUILayout.HorizontalSlider(angles.y, 0f, 359.9f);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("-90")) yaw -= 90f;
        if (GUILayout.Button("-1")) yaw -= 1f;
        if (GUILayout.Button("+1")) yaw += 1f;
        if (GUILayout.Button("+90")) yaw += 90f;
        GUILayout.EndHorizontal();
        if (!Mathf.Approximately(yaw, angles.y))
            targetRenderer.transform.rotation = Quaternion.Euler(angles.x, yaw, angles.z);
        if (GUILayout.Button($"Save forward ({saveCalibrationKey})")) SaveForwardCalibration();
        GUILayout.Label($"Reopen with {beginCalibrationKey}. Recalibrate after video / odometry origin resets.");
        GUILayout.EndArea();
    }

    private void SetHeadingMode(PanoramaHeadingMode mode)
    {
        panoramaHeadingMode = previousHeadingMode = mode;
        compensateRobotHeading = previousCompensationEnabled = true;
        hasCalibrationReference = false;
        BeginForwardCalibration();
    }

    private void UpdateHeadingDiagnostics()
    {
        float now = Time.realtimeSinceStartup;
        if ((!showHeadingDiagnostics && !logHeadingDiagnostics) || now < nextDiagnosticTime) return;
        nextDiagnosticTime = now + 0.25f;
        string command = keyboardPublisher != null && keyboardPublisher.isActiveAndEnabled &&
            now - keyboardPublisher.LastPublishedTime < 0.5f && keyboardPublisher.LastPublishedTime >= 0f
            ? $"Keyboard cmd: v={keyboardPublisher.LastPublishedLinear:F3} m/s, turn={keyboardPublisher.LastPublishedAngular:F3} rad/s"
            : "Keyboard cmd: unavailable (other command sources are not measured here)";
        string sphere = targetRenderer != null ? targetRenderer.transform.eulerAngles.y.ToString("F2") : "N/A";
        string view = Camera.main != null ? Camera.main.transform.eulerAngles.y.ToString("F2") : "N/A";
        diagnosticText = $"Heading diagnostics | t={now:F2}s | source={panoramaHeadingMode}, delay={videoHeadingDelaySeconds:F2}s, compensate={compensateRobotHeading}, calibrating={calibratingForward}\n" +
            command + $"\nOdom yaw={latestRobotYaw:F2} deg, sample step={latestYawStep:F3} deg, effective age={(hasRobotHeading ? feedbackFreshness.AgeSeconds(now) : -1):F2}s, backlog={feedbackFreshness.BacklogSeconds:F2}s\n" +
            $"Odom v={odometryForwardSpeed:F3} m/s, yaw rate={odometryYawRate:F2} deg/s\nSphere Y={sphere} deg | View Y={view} deg";
        if (logHeadingDiagnostics)
            Debug.Log("[PanoramaHeading] " + diagnosticText.Replace('\n', ' '));
    }

    private void RecordHeadingTrace()
    {
        float now = Time.realtimeSinceStartup;
        if (!HuskyControlTrace.IsRecording || now < nextHeadingTraceTime) return;
        nextHeadingTraceTime = now + 0.1f;
        HuskyControlTrace.Record("panorama", "Panorama", headingOdometryTopic, 0,
            HasFreshHeading, HasCalibratedHeading, calibrationRevision, panoramaHeadingMode.ToString(),
            compensateRobotHeading, headingCompensationSign, flipVideoHorizontally, videoHeadingDelaySeconds,
            lastHeadingStamp, hasRobotHeading ? latestRobotYaw : float.NaN,
            hasRobotHeading ? feedbackFreshness.AgeSeconds(now) : double.NaN, initialRobotYaw, calibrationWorldForwardYaw,
            HasCalibratedHeading ? RobotWorldYawDeg : float.NaN,
            targetRenderer != null ? targetRenderer.transform.eulerAngles.y : float.NaN,
            Camera.main != null ? Camera.main.transform.eulerAngles.y : float.NaN);
    }

    public void Connect()
    {
        if (peerConnection != null)
            return;

        stopping = false;
        connectCoroutine = StartCoroutine(ConnectRoutine());
    }

    private IEnumerator ConnectRoutine()
    {
        RTCConfiguration configuration = default;
        configuration.iceServers = new RTCIceServer[0];

        peerConnection = new RTCPeerConnection(ref configuration);
        peerConnection.OnIceConnectionChange = state =>
            Debug.Log($"Insta360 WHEP ICE: {state}");
        peerConnection.OnConnectionStateChange = state =>
            Debug.Log($"Insta360 WHEP connection: {state}");
        peerConnection.OnTrack = OnRemoteTrack;

        peerConnection.AddTransceiver(
            TrackKind.Video,
            new RTCRtpTransceiverInit
            {
                direction = RTCRtpTransceiverDirection.RecvOnly
            });

        webRtcUpdateCoroutine = StartCoroutine(WebRTC.Update());

        RTCSessionDescriptionAsyncOperation offerOperation =
            peerConnection.CreateOffer();
        yield return offerOperation;
        if (offerOperation.IsError)
        {
            Fail($"CreateOffer failed: {offerOperation.Error.message}");
            yield break;
        }

        RTCSessionDescription offer = offerOperation.Desc;
        RTCSetSessionDescriptionAsyncOperation localOperation =
            peerConnection.SetLocalDescription(ref offer);
        yield return localOperation;
        if (localOperation.IsError)
        {
            Fail($"SetLocalDescription failed: {localOperation.Error.message}");
            yield break;
        }

        float deadline = Time.realtimeSinceStartup + iceGatheringTimeout;
        while (!stopping &&
               peerConnection.GatheringState != RTCIceGatheringState.Complete &&
               Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        if (stopping || peerConnection == null)
            yield break;

        string localSdp = peerConnection.LocalDescription.sdp;
        using (UnityWebRequest request =
               new UnityWebRequest(whepUrl, UnityWebRequest.kHttpVerbPOST))
        {
            request.uploadHandler =
                new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(localSdp));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/sdp");
            request.SetRequestHeader("Accept", "application/sdp");
            request.timeout = 15;

            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Fail(
                    $"WHEP POST failed ({request.responseCode}): " +
                    $"{request.error}\n{request.downloadHandler.text}");
                yield break;
            }

            RTCSessionDescription answer = new RTCSessionDescription
            {
                type = RTCSdpType.Answer,
                sdp = request.downloadHandler.text
            };
            RTCSetSessionDescriptionAsyncOperation remoteOperation =
                peerConnection.SetRemoteDescription(ref answer);
            yield return remoteOperation;
            if (remoteOperation.IsError)
            {
                Fail(
                    $"SetRemoteDescription failed: " +
                    remoteOperation.Error.message);
                yield break;
            }
        }

        Debug.Log($"Insta360 WHEP connected to {whepUrl}");
        connectCoroutine = null;
    }

    private void OnRemoteTrack(RTCTrackEvent trackEvent)
    {
        if (!(trackEvent.Track is VideoStreamTrack videoTrack))
            return;

        receivedTrack = videoTrack;
        receivedTrack.OnVideoReceived += OnVideoReceived;
        Debug.Log("Insta360 WebRTC video track received.");
    }

    private void OnVideoReceived(Texture texture)
    {
        if (targetRenderer != null)
        {
            if (useInsideSphereMaterial && runtimeVideoMaterial == null)
            {
                Shader shader = Shader.Find("Insta360/Inside Sphere");
                if (shader != null)
                {
                    runtimeVideoMaterial = new Material(shader);
                    targetRenderer.sharedMaterial = runtimeVideoMaterial;
                }
                else
                {
                    Debug.LogError("Insta360 inside-sphere shader was not found.");
                }
            }

            Material displayedMaterial = runtimeVideoMaterial != null ? runtimeVideoMaterial : targetRenderer.material;
            // Keep the texture and orientation on exactly the material being rendered.
            targetRenderer.sharedMaterial = displayedMaterial;
            displayedMaterial.mainTexture = texture;
            ApplyVideoOrientation();
        }
        if (targetRawImage != null)
            targetRawImage.texture = texture;
    }

    private void ApplyVideoOrientation()
    {
        // Use an explicit shader switch on the displayed material. Clear the
        // older negative tiling so the two mechanisms cannot cancel each other.
        if (targetRenderer == null) return;
        Material videoMaterial = targetRenderer.sharedMaterial;
        if (videoMaterial == null) return;
        if (videoMaterial.HasProperty("_FlipHorizontal"))
        {
            videoMaterial.mainTextureScale = Vector2.one;
            videoMaterial.mainTextureOffset = Vector2.zero;
            videoMaterial.SetFloat("_FlipHorizontal", flipVideoHorizontally ? 1f : 0f);
        }
        else
        {
            videoMaterial.mainTextureScale = new Vector2(flipVideoHorizontally ? -1f : 1f, 1f);
            videoMaterial.mainTextureOffset = new Vector2(flipVideoHorizontally ? 1f : 0f, 0f);
        }
    }

    private void Fail(string message)
    {
        Debug.LogError(message);
        connectCoroutine = null;
        CleanupPeerConnection();

        if (autoReconnect && !stopping)
            connectCoroutine = StartCoroutine(ReconnectAfterDelay());
    }

    private IEnumerator ReconnectAfterDelay()
    {
        yield return new WaitForSecondsRealtime(reconnectDelay);
        connectCoroutine = null;
        if (!stopping)
            Connect();
    }

    private void CleanupPeerConnection()
    {
        if (webRtcUpdateCoroutine != null)
            StopCoroutine(webRtcUpdateCoroutine);
        webRtcUpdateCoroutine = null;

        if (receivedTrack != null)
        {
            receivedTrack.OnVideoReceived -= OnVideoReceived;
            receivedTrack.Dispose();
            receivedTrack = null;
        }

        if (peerConnection != null)
        {
            peerConnection.Close();
            peerConnection.Dispose();
            peerConnection = null;
        }
    }

    public void Disconnect()
    {
        stopping = true;

        if (connectCoroutine != null)
            StopCoroutine(connectCoroutine);
        connectCoroutine = null;

        CleanupPeerConnection();

        if (runtimeVideoMaterial != null)
        {
            Destroy(runtimeVideoMaterial);
            runtimeVideoMaterial = null;
        }
    }

    private void OnDestroy()
    {
        Disconnect();
    }
}
