using UnityEngine;

public class Insta360QuestViewSync : MonoBehaviour
{
    [Header("Reference")]
    [Tooltip("The camera that represents the Insta360 X5 view.")]
    public Camera insta360Camera;

    [Tooltip("The VR camera / headset camera that should follow the Insta360 view.")]
    public Camera vrCamera;

    [Tooltip("The vehicle or robot transform to show as an overlay marker.")]
    public Transform vehicleTransform;

    [Header("Sync Settings")]
    public bool enableSync = true;
    public bool syncPosition = true;
    public bool syncRotation = true;
    public bool syncFieldOfView = true;
    public bool syncNearClip = true;
    public bool syncFarClip = true;

    [Header("Vehicle Overlay")]
    public bool showVehicleOverlay = true;
    public Vector3 vehicleOverlayOffset = new Vector3(0f, 0.2f, 0f);
    public float overlayBoxSize = 28f;
    public bool showDistanceText = true;

    [Header("Hotkeys")]
    public KeyCode toggleSyncKey = KeyCode.F;
    public KeyCode toggleOverlayKey = KeyCode.O;

    private bool syncEnabled = true;
    private bool overlayVisible = true;

    private void Awake()
    {
        if (vrCamera == null)
        {
            vrCamera = Camera.main;
        }

        if (insta360Camera == null)
        {
            insta360Camera = GetComponent<Camera>();
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleSyncKey))
        {
            syncEnabled = !syncEnabled;
            Debug.Log($"Insta360 view sync {(syncEnabled ? "enabled" : "disabled")}");
        }

        if (Input.GetKeyDown(toggleOverlayKey))
        {
            overlayVisible = !overlayVisible;
            Debug.Log($"Vehicle overlay {(overlayVisible ? "shown" : "hidden")}");
        }

        if (!syncEnabled || !enableSync || vrCamera == null || insta360Camera == null)
        {
            return;
        }

        if (syncPosition)
        {
            vrCamera.transform.position = insta360Camera.transform.position;
        }

        if (syncRotation)
        {
            vrCamera.transform.rotation = insta360Camera.transform.rotation;
        }

        if (syncFieldOfView)
        {
            vrCamera.fieldOfView = insta360Camera.fieldOfView;
        }

        if (syncNearClip)
        {
            vrCamera.nearClipPlane = insta360Camera.nearClipPlane;
        }

        if (syncFarClip)
        {
            vrCamera.farClipPlane = insta360Camera.farClipPlane;
        }
    }

    private void OnGUI()
    {
        if (!showVehicleOverlay || !overlayVisible || vrCamera == null || vehicleTransform == null)
        {
            return;
        }

        Vector3 vehicleWorldPos = vehicleTransform.position + vehicleOverlayOffset;
        Vector3 screenPoint = vrCamera.WorldToScreenPoint(vehicleWorldPos);

        if (screenPoint.z > 0f)
        {
            float x = screenPoint.x - overlayBoxSize * 0.5f;
            float y = Screen.height - screenPoint.y - overlayBoxSize * 0.5f;
            Rect boxRect = new Rect(x, y, overlayBoxSize, overlayBoxSize);
            GUI.Box(boxRect, "车");
        }

        if (showDistanceText)
        {
            Vector3 toVehicle = vehicleWorldPos - vrCamera.transform.position;
            float distance = toVehicle.magnitude;
            GUI.Label(
                new Rect(20f, 20f, 500f, 30f),
                $"[Insta360 View] 车距离: {distance:F2} m"
            );
        }
    }
}
