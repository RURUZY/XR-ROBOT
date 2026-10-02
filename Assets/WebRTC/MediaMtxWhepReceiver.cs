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

    [Header("World-fixed video heading compensation")]
    [Tooltip("Enable only when the video holds its world heading instead of turning with the robot.")]
    public bool compensateRobotHeading = true;
    public string headingOdometryTopic = "/odometry/filtered";
    [Tooltip("Inverse robot rotation: standard ROS positive-left yaw needs positive Unity sphere yaw. Use -1 only for a reversed video mapping.")]
    public float headingCompensationSign = 1f;
    public float headingTimeout = 1f;

    [Header("Heading diagnostics")]
    public bool showHeadingDiagnostics = false;
    public bool logHeadingDiagnostics = false;
    public bool HasFreshHeading => isActiveAndEnabled && hasRobotHeading
        && Time.realtimeSinceStartup - lastHeadingTime <= Mathf.Max(0.1f, headingTimeout);
    public float RobotHeadingDeg => Mathf.Repeat(latestRobotYaw, 360f);
    private CmdVelPublisher keyboardPublisher;
    private float odometryForwardSpeed;
    private float odometryYawRate;
    private float latestYawStep;
    private float nextDiagnosticTime;
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
    }

    private bool calibratingForward;
    private bool hasCalibrationReference;
    private bool previousHorizontalFlip;
    private string latestOdometryFrame;
    private string referenceOdometryFrame;
    private string calibrationMessage = "Keep the robot stopped and your head facing forward. Align the image with the real robot front, then save.";
    private string CalibrationKey => "Insta360.ForwardCalibration.v1|" + whepUrl + "|" + headingOdometryTopic;

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
        keyboardPublisher = FindObjectOfType<CmdVelPublisher>();
        if (targetRenderer != null)
            initialSphereRotation = targetRenderer.transform.rotation;
        if (compensateRobotHeading)
        {
            LoadForwardCalibration();
            if (!hasCalibrationReference) BeginForwardCalibration();
        }
        headingStartTime = Time.realtimeSinceStartup;
        if (compensateRobotHeading)
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
        if (this == null) return;
        var q = message.pose.pose.orientation;
        double norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        if (double.IsNaN(norm) || double.IsInfinity(norm) || norm < 1e-12) return;
        float newYaw = (float)(System.Math.Atan2(
            2.0 * (q.w * q.z + q.x * q.y),
            norm - 2.0 * (q.y * q.y + q.z * q.z)) * Mathf.Rad2Deg);
        latestYawStep = hasRobotHeading ? Mathf.DeltaAngle(latestRobotYaw, newYaw) : 0f;
        latestRobotYaw = newYaw;
        odometryForwardSpeed = (float)message.twist.twist.linear.x;
        odometryYawRate = (float)message.twist.twist.angular.z * Mathf.Rad2Deg;
        latestOdometryFrame = message.header.frame_id;
        if (hasCalibrationReference && latestOdometryFrame != referenceOdometryFrame)
        {
            hasCalibrationReference = false;
            BeginForwardCalibration();
            calibrationMessage = "Odometry frame changed. Align the real robot front again and save.";
        }
        if (!hasRobotHeading)
        {
            hasRobotHeading = true;
        }
        lastHeadingTime = Time.realtimeSinceStartup;
        headingWarningShown = false;
    }

    private void Update()
    {
        if (previousHorizontalFlip != flipVideoHorizontally)
        {
            previousHorizontalFlip = flipVideoHorizontally;
            hasCalibrationReference = false;
            ApplyVideoOrientation();
            if (compensateRobotHeading) BeginForwardCalibration();
        }
        if (!compensateRobotHeading) return;
        if (Input.GetKeyDown(beginCalibrationKey)) BeginForwardCalibration();
        if (calibratingForward && Input.GetKeyDown(saveCalibrationKey)) SaveForwardCalibration();
    }

    private void LateUpdate()
    {
        UpdateHeadingDiagnostics();
        if (targetRenderer == null) return;
        if (centerSphereOnMainCamera && Camera.main != null)
            targetRenderer.transform.position = Camera.main.transform.position;
        if (!compensateRobotHeading) return;

        float age = Time.realtimeSinceStartup - (hasRobotHeading ? lastHeadingTime : headingStartTime);
        if (age > Mathf.Max(0.1f, headingTimeout) && !headingWarningShown)
        {
            Debug.LogWarning($"Panorama heading: no fresh {headingOdometryTopic}; holding the last view correction.");
            headingWarningShown = true;
        }
        if (!hasRobotHeading || calibratingForward || !hasCalibrationReference) return;

        // Rotate world-fixed imagery opposite to the robot's Unity rotation.
        // Preserve the calibrated sphere rotation (currently Y=250 degrees).
        float delta = Mathf.DeltaAngle(initialRobotYaw, latestRobotYaw);
        targetRenderer.transform.rotation =
            Quaternion.AngleAxis(headingCompensationSign * delta, Vector3.up) * initialSphereRotation;
    }

    [ContextMenu("Begin Forward Calibration")]
    public void BeginForwardCalibration()
    {
        if (!Application.isPlaying || targetRenderer == null) return;
        calibratingForward = true;
        calibrationMessage = "Keep the robot stopped and your head facing forward. Align the image with the real robot front, then save.";
    }

    [ContextMenu("Save Forward Calibration")]
    public void SaveForwardCalibration()
    {
        if (!Application.isPlaying || !calibratingForward || targetRenderer == null) return;
        if (!hasRobotHeading || Time.realtimeSinceStartup - lastHeadingTime > Mathf.Max(0.1f, headingTimeout))
        {
            calibrationMessage = "Cannot save: waiting for fresh odometry on " + headingOdometryTopic;
            Debug.LogWarning(calibrationMessage);
            return;
        }
        initialSphereRotation = targetRenderer.transform.rotation;
        initialRobotYaw = latestRobotYaw;
        referenceOdometryFrame = latestOdometryFrame;
        hasCalibrationReference = true;
        calibratingForward = false;
        if (rememberForwardCalibration)
        {
            var calibration = new ForwardCalibration
            {
                sphereRotation = initialSphereRotation,
                robotYaw = initialRobotYaw,
                compensationSign = headingCompensationSign,
                flipVideoHorizontally = flipVideoHorizontally,
                odometryFrame = referenceOdometryFrame
            };
            PlayerPrefs.SetString(CalibrationKey, JsonUtility.ToJson(calibration));
            PlayerPrefs.Save();
        }
        Debug.Log($"Panorama forward calibrated: sphere Y={initialSphereRotation.eulerAngles.y:F1}, ROS yaw={initialRobotYaw:F1}. Recalibrate if video or odometry heading origin resets.");
    }

    private void LoadForwardCalibration()
    {
        if (!rememberForwardCalibration || !PlayerPrefs.HasKey(CalibrationKey)) return;
        try
        {
            var saved = JsonUtility.FromJson<ForwardCalibration>(PlayerPrefs.GetString(CalibrationKey));
            if (saved == null || !Mathf.Approximately(saved.compensationSign, headingCompensationSign)) return;
            if (saved.flipVideoHorizontally != flipVideoHorizontally) return;
            Quaternion q = saved.sphereRotation;
            float norm = Quaternion.Dot(q, q);
            if (float.IsNaN(norm) || float.IsInfinity(norm) || norm < 0.0001f ||
                float.IsNaN(saved.robotYaw) || float.IsInfinity(saved.robotYaw)) return;
            initialSphereRotation = q.normalized;
            initialRobotYaw = saved.robotYaw;
            referenceOdometryFrame = saved.odometryFrame;
            hasCalibrationReference = true;
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
        if (!compensateRobotHeading || !showCalibrationPanel || !calibratingForward || targetRenderer == null) return;
        GUILayout.BeginArea(new Rect(20, 60, 510, 315), GUI.skin.box);
        GUILayout.Label("Panorama: calibrate robot forward");
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
        GUILayout.Label("Fresh odometry: " + (hasRobotHeading && Time.realtimeSinceStartup - lastHeadingTime <= Mathf.Max(0.1f, headingTimeout)));
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

    private void UpdateHeadingDiagnostics()
    {
        float now = Time.realtimeSinceStartup;
        if (!showHeadingDiagnostics || now < nextDiagnosticTime) return;
        nextDiagnosticTime = now + 0.25f;
        string command = keyboardPublisher != null && keyboardPublisher.isActiveAndEnabled &&
            now - keyboardPublisher.LastPublishedTime < 0.5f && keyboardPublisher.LastPublishedTime >= 0f
            ? $"Keyboard cmd: v={keyboardPublisher.LastPublishedLinear:F3} m/s, turn={keyboardPublisher.LastPublishedAngular:F3} rad/s"
            : "Keyboard cmd: unavailable (other command sources are not measured here)";
        string sphere = targetRenderer != null ? targetRenderer.transform.eulerAngles.y.ToString("F2") : "N/A";
        string view = Camera.main != null ? Camera.main.transform.eulerAngles.y.ToString("F2") : "N/A";
        diagnosticText = $"Heading diagnostics | t={now:F2}s | compensate={compensateRobotHeading}, calibrating={calibratingForward}\n" +
            command + $"\nOdom yaw={latestRobotYaw:F2} deg, sample step={latestYawStep:F3} deg, age={(hasRobotHeading ? now - lastHeadingTime : -1f):F2}s\n" +
            $"Odom v={odometryForwardSpeed:F3} m/s, yaw rate={odometryYawRate:F2} deg/s\nSphere Y={sphere} deg | View Y={view} deg";
        if (logHeadingDiagnostics)
            Debug.Log("[PanoramaHeading] " + diagnosticText.Replace('\n', ' '));
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
