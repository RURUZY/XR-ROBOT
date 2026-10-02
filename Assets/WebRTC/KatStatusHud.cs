using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// VR-safe replacement for KatHuskyDirectController's OnGUI() debug panel.
///
/// OnGUI is legacy IMGUI and is not stereo-aware: in a Quest 3 stereo
/// render it ends up drawn once per eye at a fixed screen-space offset,
/// which reads as doubled/ghosted, scrambled-looking text (this is exactly
/// what was reported during real-headset testing -- not a scene
/// misconfiguration). This component instead builds an actual World Space
/// Canvas at runtime, parented to the main camera so it stays in view like
/// a HUD, and renders through the normal stereo camera pipeline like any
/// other 3D object -- so it does not have that problem.
///
/// Builds its own Canvas/Panel/Text at runtime (uses Unity's built-in
/// legacy UI Text + the built-in font, not TextMeshPro, specifically so
/// this works with zero setup -- no "Import TMP Essential Resources" step
/// required). Attach to any GameObject; if `controller` is left empty it
/// looks for a KatHuskyDirectController on the same GameObject first.
/// </summary>
public class KatStatusHud : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("Left empty, this will look for a KatHuskyDirectController on the same GameObject.")]
    public KatHuskyDirectController controller;
    [Tooltip("Optional Direct + angle correction version. Auto-detected on this object or in the scene.")]
    public KatHuskyDirectAngleController angleController;
    public MediaMtxWhepReceiver headingSource;
    public CmdVelPublisher keyboardController;

    [Header("Placement (relative to the camera it's parented to)")]
    public float distanceFromCamera = 1.2f;
    public float verticalOffset = -0.25f;
    public float panelWidth = 0.6f;
    public float panelHeight = 0.22f;
    [Tooltip("World-space units per pixel. Controls how large the text reads at distanceFromCamera -- lower = bigger text.")]
    public float pixelsPerUnit = 800f;

    [Header("Update rate")]
    public float refreshInterval = 0.1f;

    private Text statusText;
    private GameObject hudRoot;
    private float nextRefreshTime;

    private void Awake()
    {
        if (controller == null)
        {
            controller = GetComponent<KatHuskyDirectController>();
        }
    }

    private void Start()
    {
        if (angleController == null) angleController = GetComponent<KatHuskyDirectAngleController>();
        if (angleController == null) angleController = FindObjectOfType<KatHuskyDirectAngleController>();
        if (headingSource == null) headingSource = FindObjectOfType<MediaMtxWhepReceiver>();
        if (keyboardController == null) keyboardController = FindObjectOfType<CmdVelPublisher>();
        BuildHud();
    }

    private void BuildHud()
    {
        Transform cam = Camera.main != null ? Camera.main.transform : null;
        if (cam == null)
        {
            Debug.LogWarning("KatStatusHud: no Camera.main found, HUD will not be placed.");
            return;
        }

        GameObject canvasGO = new GameObject("KAT Status HUD");
        hudRoot = canvasGO;
        canvasGO.transform.SetParent(cam, false);
        canvasGO.transform.localPosition = new Vector3(0f, verticalOffset, distanceFromCamera);
        canvasGO.transform.localRotation = Quaternion.identity;

        Canvas canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGO.AddComponent<CanvasScaler>();

        RectTransform canvasRect = canvasGO.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(panelWidth * pixelsPerUnit, panelHeight * pixelsPerUnit);
        canvasGO.transform.localScale = Vector3.one / Mathf.Max(pixelsPerUnit, 1f);

        GameObject panelGO = new GameObject("Panel");
        panelGO.transform.SetParent(canvasGO.transform, false);
        Image panelImage = panelGO.AddComponent<Image>();
        panelImage.color = new Color(0f, 0f, 0f, 0.6f);
        panelImage.raycastTarget = false;
        RectTransform panelRect = panelGO.GetComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;

        GameObject textGO = new GameObject("Status Text");
        textGO.transform.SetParent(panelGO.transform, false);
        statusText = textGO.AddComponent<Text>();
        statusText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        statusText.fontSize = 28;
        statusText.resizeTextForBestFit = true;
        statusText.resizeTextMinSize = 16;
        statusText.resizeTextMaxSize = 28;
        statusText.raycastTarget = false;
        statusText.color = Color.white;
        statusText.alignment = TextAnchor.UpperLeft;
        statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
        statusText.verticalOverflow = VerticalWrapMode.Overflow;
        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(12f, 12f);
        textRect.offsetMax = new Vector2(-12f, -12f);
    }

    private void Update()
    {
        if (statusText == null) return;
        if (Time.unscaledTime < nextRefreshTime) return;
        nextRefreshTime = Time.unscaledTime + Mathf.Max(refreshInterval, 0.02f);

        bool katActive = controller != null && controller.isActiveAndEnabled;
        bool angleActive = angleController != null && angleController.isActiveAndEnabled;
        bool keyboardActive = keyboardController != null && keyboardController.isActiveAndEnabled;
        string mode = "NO ACTIVE CONTROL";
        int activeCount = (katActive ? 1 : 0) + (angleActive ? 1 : 0) + (keyboardActive ? 1 : 0);
        if (activeCount > 1) mode = "MULTIPLE CONTROLLERS";
        else if (angleActive) mode = "KAT ANGLE | " + angleController.Status;
        else if (katActive) mode = "KAT | " + controller.Status;
        else if (keyboardActive)
        {
            bool fresh = keyboardController.LastPublishedTime >= 0f
                && Time.realtimeSinceStartup - keyboardController.LastPublishedTime < 0.5f;
            float linear = keyboardController.LastPublishedLinear;
            float angular = keyboardController.LastPublishedAngular;
            mode = !fresh ? "KEYBOARD | WAITING"
                : "KEYBOARD | " + (linear < -0.001f ? "REVERSE"
                : linear > 0.001f ? "FORWARD" : Mathf.Abs(angular) > 0.001f ? "TURNING" : "IDLE");
        }
        string heading = angleActive
            ? (angleController.HasFreshOdometry ? $"{angleController.RobotYawDeg:F1}°" : "UNAVAILABLE")
            : headingSource != null && headingSource.HasFreshHeading
            ? $"{headingSource.RobotHeadingDeg:F1}°" : "UNAVAILABLE";
        statusText.text = $"Robot heading (odom): {heading}\nMode: {mode}";
        if (angleActive)
            statusText.text += angleController.HeadingCalibrated && angleController.HasFreshOdometry
                ? $"\nTurn (ROS): target {angleController.TargetTurnDeg:F1}° / robot {angleController.RobotTurnDeg:F1}°\nError: {angleController.HeadingErrorDeg:F1}°\nTurn demand: input {angleController.LiveTurnDemand:F2} / correction {angleController.CorrectionTurnDemand:F2} rad/s"
                : "\nTurn: " + angleController.TurnStatus;
    }

    private void OnEnable() { if (hudRoot != null) hudRoot.SetActive(true); }
    private void OnDisable() { if (hudRoot != null) hudRoot.SetActive(false); }
    private void OnDestroy() { if (hudRoot != null) Destroy(hudRoot); }
}
