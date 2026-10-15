// LivoscanWristMinimap.cs - watch-style minimap on the left controller (XR_MINIMAP_REQUIREMENTS.md, plan A).
// Put it on an empty GameObject anywhere in the scene; at Start it parents itself to the left controller.
// It builds a scaled LivoscanWorldClient as its content and recentres that content on the robot every
// frame. Only the circle of mapRadius metres around the robot is drawn.
// Read-only: it uses the shared LivoscanDataSource (HTTP + WebSocket) and publishes nothing.
//
// Orientation:
//  - AlignWithVideo (default): the map uses the receiver's CURRENT calibrated robot world heading,
//    shared with the physical footprint. Robot forward changes when the robot turns.
//    The dial follows the hand position, but its yaw faces away from the head instead of following the wrist.
//    There is no separate saved head direction or PlayerPrefs calibration for the map.
//    This aligns the display using odometry; it is not independent validation of SLAM yaw or extrinsics.
//  - FollowWrist: robot heading points along the dial's forward (+Z) on the controller.
//  - NorthUp: map +x (mapping start heading) is the dial's forward.
using UnityEngine;
using UnityEngine.XR;

[DefaultExecutionOrder(100)]
public class LivoscanWristMinimap : MonoBehaviour
{
    public enum Orientation { AlignWithVideo, FollowWrist, NorthUp }

    public LivoscanDataSource source;

    [Header("Placement")]
    [Tooltip("Empty = find the GameObject named 'Left Controller' (XR Origin).")]
    public Transform hand;
    [Tooltip("Dial centre in the controller's local space (metres). Applied every frame so it can be tuned in Play mode.")]
    public Vector3 handPositionOffset = new Vector3(0f, 0.06f, 0.10f);
    [Tooltip("FollowWrist/NorthUp: dial tilt in the controller's local space. Negative X tilts the face toward the user.")]
    public Vector3 handRotationOffset = new Vector3(-30f, 0f, 0f);
    [Tooltip("AlignWithVideo: dial tilt toward the head, degrees from horizontal.")]
    public float videoDialTilt = 40f;

    [Header("Map")]
    public Orientation orientation = Orientation.AlignWithVideo;
    [Tooltip("Map metres -> dial metres. 0.03 shows a 6 m radius as an 18 cm dial radius.")]
    public float mapScale = 0.03f;
    [Tooltip("Radius around the robot that is shown (map metres).")]
    public float mapRadius = 6f;
    [Tooltip("Keep every Nth map point. 6 keeps ~53k of the current 320k points.")]
    [Min(1)] public int pointStride = 6;
    [Tooltip("Drop points above this height (map metres) so the ceiling does not cover the room.")]
    public float maxPointHeight = 1.8f;
    [Tooltip("Robot arrow length in map metres.")]
    public float robotArrowLength = 1.0f;
    public Color robotColor = new Color(1f, 0.55f, 0.1f, 1f);
    [Tooltip("Robot arrow colour while the pose is not localized in the map or is stale.")]
    public Color untrustedRobotColor = new Color(0.55f, 0.55f, 0.55f, 0.8f);
    [Tooltip("Show 'pose stale' when the last SLAM pose is older than this (seconds).")]
    public float poseStaleAfter = 1f;
    public Color dialColor = new Color(0f, 0f, 0f, 0.45f);
    public Color ringColor = new Color(0.3f, 0.9f, 1f, 0.9f);

    [Header("Video alignment")]
    [Tooltip("Empty = find MediaMtxWhepReceiver in the scene (also when disabled).")]
    public MediaMtxWhepReceiver videoReceiver;

    [Header("Labels")]
    [Tooltip("World scale of the label TextMesh; 0.05 gives roughly 1 cm high text.")]
    public float labelWorldScale = 0.05f;
    public bool showConfidence = false;

    [Header("Toggle")]
    public bool visibleOnStart = true;
    public XRNode toggleHand = XRNode.LeftHand;   // primaryButton on the left hand = X
    public bool enableKeyboardFallback = false;
    public KeyCode toggleKey = KeyCode.M;

    [Header("Shaders (auto-filled)")]
    public Shader overlayShader;
    public Shader overlayTextShader;

    const int QueueDial = 3100, QueuePoints = 3101, QueueBoxes = 3102, QueueRobot = 3103, QueueText = 3104;

    LivoscanWorldClient view;
    Transform trackedHand;
    Transform dial;
    TextMesh status;
    Mesh dialMesh;
    Material dialMat, pointMat, boxMat, robotMat, textMat;
    Font font;
    float builtDialRadius = -1f;
    bool visible, lastToggle;

    float lastRobotWorldYaw, nextTraceTime;
    bool AligningWithVideo => orientation == Orientation.AlignWithVideo;

    void Reset()
    {
        overlayShader = Shader.Find("Livoscan/Overlay");
        overlayTextShader = Shader.Find("Livoscan/OverlayText");
    }

    void Start()
    {
        if (source == null) source = FindObjectOfType<LivoscanDataSource>();
        if (source == null) source = new GameObject("Livoscan Data Source").AddComponent<LivoscanDataSource>();
        if (hand == null) hand = GameObject.Find("Left Controller")?.transform;
        // MainScene keeps the XRI 'Left Controller' inactive (GameObject.Find cannot see it, and children of an
        // inactive object never render), so fall back to an anchor driven straight from the XR left-hand pose.
        if (hand == null || !hand.gameObject.activeInHierarchy) hand = CreateTrackedHandAnchor();
        if (hand != null) transform.SetParent(hand, false);
        else Debug.LogWarning("[livoscan] minimap: no left hand anchor (no Camera.main); staying where it is.");
        if (videoReceiver == null) videoReceiver = FindObjectOfType<MediaMtxWhepReceiver>(true);
        if (overlayShader == null) overlayShader = Shader.Find("Livoscan/Overlay");
        if (overlayTextShader == null) overlayTextShader = Shader.Find("Livoscan/OverlayText");

        dialMat = MakeOverlay(QueueDial, true, Color.white, false);
        pointMat = MakeOverlay(QueuePoints, true, Color.white, false);
        boxMat = MakeOverlay(QueueBoxes, false, Color.white, true);
        robotMat = MakeOverlay(QueueRobot, false, robotColor, false);
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        textMat = new Material(overlayTextShader) { renderQueue = QueueText, mainTexture = font.material.mainTexture };
        Font.textureRebuilt += OnFontTextureRebuilt;

        dial = new GameObject("Dial").transform;
        dial.SetParent(transform, false);
        dialMesh = new Mesh { name = "Minimap dial" };
        dial.gameObject.AddComponent<MeshFilter>().sharedMesh = dialMesh;
        dial.gameObject.AddComponent<MeshRenderer>().sharedMaterial = dialMat;

        var contentGo = new GameObject("Minimap Content");
        contentGo.transform.SetParent(transform, false);
        view = contentGo.AddComponent<LivoscanWorldClient>();   // its Start runs later, after these fields are set
        view.source = source;
        view.pointMaterial = pointMat;
        view.objectMaterial = boxMat;
        view.labelMaterial = textMat;
        view.labelFont = font;
        view.robotMaterial = robotMat;
        view.pointStride = pointStride;
        view.maxPointHeight = maxPointHeight;
        view.showConfidence = showConfidence;
        view.arrowRobotMarker = true;
        view.arrowLength = robotArrowLength;

        status = new GameObject("Status").AddComponent<TextMesh>();
        status.transform.SetParent(transform, false);
        status.font = font;
        status.GetComponent<MeshRenderer>().sharedMaterial = textMat;
        status.characterSize = 0.05f; status.fontSize = 48; status.anchor = TextAnchor.LowerCenter;
        status.color = new Color(1f, 0.85f, 0.4f);

        SetVisible(visibleOnStart);
    }

    // Tracked device poses are in tracking space, i.e. the local space of the camera's parent (XR Origin's
    // 'Camera Offset'), so an anchor there with the raw left-hand pose sits exactly on the controller.
    Transform CreateTrackedHandAnchor()
    {
        var cam = Camera.main;
        if (cam == null) return null;
        trackedHand = new GameObject("Left Hand Anchor (livoscan)").transform;
        trackedHand.SetParent(cam.transform.parent, false);
        UpdateTrackedHand();
        Application.onBeforeRender += UpdateTrackedHand;
        Debug.Log("[livoscan] minimap: following the XR left-hand pose under " + (cam.transform.parent != null ? cam.transform.parent.name : "scene root"));
        return trackedHand;
    }

    void UpdateTrackedHand()
    {
        if (trackedHand == null) return;
        var device = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        if (!device.isValid) return;   // keep the last pose while the controller is asleep
        if (device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 p)) trackedHand.localPosition = p;
        if (device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion q)) trackedHand.localRotation = q;
    }

    Material MakeOverlay(int queue, bool vertexColor, Color color, bool cullBack)
    {
        var m = new Material(overlayShader) { renderQueue = queue };
        m.SetFloat("_UseVertexColor", vertexColor ? 1f : 0f);
        m.SetFloat("_Cull", cullBack ? 2f : 0f);
        m.SetColor("_Color", color);
        return m;
    }

    void OnFontTextureRebuilt(Font f)
    {
        if (f == font && textMat != null) textMat.mainTexture = f.material.mainTexture;
    }

    // ---- per frame -------------------------------------------------------------------------------

    void Update()
    {
        UpdateTrackedHand();
        bool pressed = ReadButton(toggleHand, CommonUsages.primaryButton) || (enableKeyboardFallback && Input.GetKey(toggleKey));
        if (pressed && !lastToggle) SetVisible(!visible);
        lastToggle = pressed;
    }

    void SetVisible(bool on)
    {
        visible = on;
        if (view != null) view.gameObject.SetActive(on);
        if (dial != null) dial.gameObject.SetActive(on);
        if (status != null) status.gameObject.SetActive(on);
    }

    void LateUpdate()
    {
        float dialYaw = PlaceDial();
        if (!visible || view == null) return;

        float s = Mathf.Max(mapScale, 1e-4f);
        float dialRadius = Mathf.Max(mapRadius, 0.1f) * s;
        if (!Mathf.Approximately(dialRadius, builtDialRadius)) BuildDial(dialRadius);

        // Content yaw about the dial's up axis. Robot (map yaw psi) then shows at content + psi.
        float contentYaw = 0f;
        bool headingValid = videoReceiver != null && videoReceiver.HasCalibratedHeading;
        if (headingValid) lastRobotWorldYaw = videoReceiver.RobotWorldYawDeg;
        if (source.HasPose)
        {
            if (AligningWithVideo) contentYaw = RobotViewHeading.DialLocalYaw(lastRobotWorldYaw, dialYaw,
                Mathf.DeltaAngle(0f, transform.eulerAngles.x)) - source.RobotYawDeg;
            else if (orientation != Orientation.NorthUp) contentYaw = -source.RobotYawDeg;          // heading -> dial +Z
        }
        // Centre the robot on the dial; height is kept so the ground (map z = 0) stays on the dial plane.
        Vector3 robot = source.HasPose ? source.RobotPosition : Vector3.zero;
        Quaternion rotation = Quaternion.Euler(0f, contentYaw, 0f);
        var content = view.transform;
        content.localScale = Vector3.one * s;
        content.localRotation = rotation;
        content.localPosition = -(rotation * new Vector3(robot.x, 0f, robot.z)) * s;

        view.labelMaxDistance = mapRadius;
        view.labelWorldScale = labelWorldScale;
        // Not localized: position may be metres off the map. Stale: SLAM stopped or the link stalled.
        bool stale = source.HasPose && source.PoseAgeSeconds > Mathf.Max(poseStaleAfter, 0.1f);
        bool untrusted = source.HasPose && (!source.Localized || stale || (AligningWithVideo && !headingValid));
        robotMat.SetColor("_Color", untrusted ? untrustedRobotColor : robotColor);
        var clip = transform.worldToLocalMatrix;
        foreach (var m in new[] { pointMat, boxMat })
        {
            m.SetMatrix("_LivoscanClipMatrix", clip);
            m.SetFloat("_LivoscanClipRadius", dialRadius);
        }

        string text = !source.Connected ? "Livoscan: not connected"
            : !source.MapLoaded ? "Livoscan: loading map"
            : !source.HasPose ? "Livoscan: waiting for pose" : "";
        if (text.Length == 0)
        {
            var lines = new System.Collections.Generic.List<string>();
            if (stale) lines.Add($"POSE STALE ({source.PoseAgeSeconds:0.0} s)");
            if (!source.Localized) lines.Add("NOT LOCALIZED");
            if (AligningWithVideo && !headingValid) lines.Add(videoReceiver != null
                ? videoReceiver.HeadingAlignmentStatus : "PANORAMA HEADING UNAVAILABLE");
            else if (AligningWithVideo && videoReceiver.panoramaHeadingMode == MediaMtxWhepReceiver.PanoramaHeadingMode.Custom)
                lines.Add("CUSTOM VIEW / VERIFY ALIGNMENT");
            text = string.Join("\n", lines);
        }
        status.text = text;
        if (HuskyControlTrace.IsRecording && Time.realtimeSinceStartup >= nextTraceTime)
        {
            nextTraceTime = Time.realtimeSinceStartup + 0.1f;
            Vector3 arrow = transform.TransformDirection(rotation * Quaternion.Euler(0f, source.RobotYawDeg, 0f) * Vector3.forward);
            HuskyControlTrace.Record("minimap", "Livoscan wrist map", "", 0,
                orientation.ToString(), headingValid, source.PoseFresh && !stale, source.Localized, source.PoseAgeSeconds,
                source.HasPose ? source.RobotYawDeg : float.NaN,
                headingValid ? lastRobotWorldYaw : float.NaN, dialYaw, contentYaw,
                source.HasPose ? Mathf.Atan2(arrow.x, arrow.z) * Mathf.Rad2Deg : float.NaN, robot.x, robot.y, robot.z);
        }
        if (text.Length > 0)
        {
            status.transform.localPosition = new Vector3(0f, 0.01f, dialRadius + 0.01f);
            var cam = Camera.main;
            if (cam != null) status.transform.rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
            status.transform.localScale = Vector3.one * (labelWorldScale / Mathf.Max(transform.lossyScale.x, 1e-6f));
        }
    }

    // Returns the dial's world yaw. AlignWithVideo: follow the hand position only, face away from the head
    // and tilt toward it. Otherwise: rigidly attached to the controller.
    float PlaceDial()
    {
        if (hand == null) return transform.eulerAngles.y;
        if (!AligningWithVideo)
        {
            transform.localPosition = handPositionOffset;
            transform.localRotation = Quaternion.Euler(handRotationOffset);
            return transform.eulerAngles.y;
        }
        Vector3 position = hand.TransformPoint(handPositionOffset);
        var cam = Camera.main;
        Vector3 away = cam != null ? position - cam.transform.position : hand.forward;
        away.y = 0f;
        if (away.sqrMagnitude < 1e-6f) away = hand.forward;
        float yaw = Mathf.Atan2(away.x, away.z) * Mathf.Rad2Deg;
        transform.SetPositionAndRotation(position, Quaternion.Euler(-Mathf.Clamp(videoDialTilt, -80f, 80f), yaw, 0f));
        return yaw;
    }

    void BuildDial(float radius)
    {
        // Dark disc, a ring at the clip edge and a tick marking dial forward.
        builtDialRadius = radius;
        const int segments = 64;
        float ringWidth = Mathf.Max(radius * 0.025f, 0.002f);
        var verts = new System.Collections.Generic.List<Vector3>();
        var cols = new System.Collections.Generic.List<Color>();
        var tris = new System.Collections.Generic.List<int>();
        verts.Add(Vector3.zero); cols.Add(dialColor);
        for (int i = 0; i < segments; i++)
        {
            float a = i * Mathf.PI * 2f / segments;
            verts.Add(new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * radius); cols.Add(dialColor);
            tris.AddRange(new[] { 0, 1 + i, 1 + (i + 1) % segments });
        }
        int ring = verts.Count;
        for (int i = 0; i < segments; i++)
        {
            float a = i * Mathf.PI * 2f / segments;
            var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            verts.Add(dir * radius); cols.Add(ringColor);
            verts.Add(dir * (radius + ringWidth)); cols.Add(ringColor);
            int a0 = ring + 2 * i, a1 = a0 + 1, b0 = ring + 2 * ((i + 1) % segments), b1 = b0 + 1;
            tris.AddRange(new[] { a0, a1, b1, a0, b1, b0 });
        }
        int tick = verts.Count;
        verts.Add(new Vector3(0f, 0f, radius + ringWidth * 4f)); cols.Add(ringColor);
        verts.Add(new Vector3(-ringWidth * 2.5f, 0f, radius + ringWidth)); cols.Add(ringColor);
        verts.Add(new Vector3(ringWidth * 2.5f, 0f, radius + ringWidth)); cols.Add(ringColor);
        tris.AddRange(new[] { tick, tick + 1, tick + 2 });

        dialMesh.Clear();
        dialMesh.SetVertices(verts);
        dialMesh.SetColors(cols);
        dialMesh.SetTriangles(tris, 0);
        dialMesh.RecalculateBounds();
    }

    static bool ReadButton(XRNode node, InputFeatureUsage<bool> usage)
    {
        var device = InputDevices.GetDeviceAtXRNode(node);
        return device.isValid && device.TryGetFeatureValue(usage, out bool pressed) && pressed;
    }

    void OnDestroy()
    {
        Font.textureRebuilt -= OnFontTextureRebuilt;
        Application.onBeforeRender -= UpdateTrackedHand;
        if (trackedHand != null) Destroy(trackedHand.gameObject);
        foreach (var m in new[] { dialMat, pointMat, boxMat, robotMat, textMat }) if (m != null) Destroy(m);
        if (dialMesh != null) Destroy(dialMesh);
    }
}
