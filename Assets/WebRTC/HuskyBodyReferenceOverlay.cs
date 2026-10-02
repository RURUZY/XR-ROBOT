using UnityEngine;

/// <summary>
/// Draws a flat, semi-transparent footprint rectangle (with a distance grid
/// and a border) positioned directly "under" the Insta360 X5's mount point,
/// so the operator has a scale/clearance reference instead of judging
/// distance from the raw 360 video alone. Addresses the reported problem:
/// "I can't tell whether the robot's current position is safe" -- the
/// equirectangular 360 feed alone has no depth/scale cue, and the robot's
/// own body never appears in frame.
///
/// Geometry uses a robot-relative viewing frame, independent of the video
/// sphere rotation and scale. World-fixed video heading compensation is
/// handled separately by MediaMtxWhepReceiver; XR controls head rotation.
///
/// Attach directly to the "Insta360 360 Sphere" and leave anchor empty to
/// follow its center without inheriting its compensated video rotation.
/// An explicit anchor must be a separate robot-view reference, not the sphere.
///
/// Standard A200 external dimensions are 0.99 x 0.67 m. Camera height is
/// approximately 0.8 m on this rig; measure the forward and lateral offsets.
/// </summary>
public class HuskyBodyReferenceOverlay : MonoBehaviour
{
    [Header("Anchor")]
    [Tooltip("Optional robot-view reference: position and rotation only. Leave empty to follow the sphere center with independent robotForwardYaw.")]
    public Transform anchor;
    [Tooltip("Robot forward in Unity world degrees when Anchor is empty, independent of video yaw and headset rotation.")]
    public float robotForwardYaw = 0f;

    [Header("Camera mount offset (Insta360 X5 relative to robot footprint center)")]
    [Tooltip("Lens center above ground in meters; approximately 0.8 m on the current rig.")]
    public float cameraHeightAboveGround = 0.8f;
    [Tooltip("Forward/back offset of the camera from the robot's footprint center, in meters. Positive = camera sits ahead of center.")]
    public float mountForwardOffset = 0f;
    [Tooltip("Left/right offset of the camera from the robot's footprint center, in meters. Positive = camera sits to the right of center.")]
    public float mountLateralOffset = 0f;

    [Header("Footprint size (standard Husky A200 external dimensions, meters)")]
    public float footprintLength = 0.99f;
    public float footprintWidth = 0.67f;

    [Header("Appearance")]
    public Color fillColor = new Color(0f, 1f, 1f, 0.18f);
    public Color lineColor = new Color(0f, 1f, 1f, 0.85f);
    [Tooltip("Distance between grid lines, in meters -- doubles as a rough ruler for judging clearance to nearby obstacles.")]
    public float gridSpacing = 0.25f;
    public float lineWidth = 0.015f;
    public float borderWidth = 0.03f;

    [Header("Forward-direction marker")]
    [Tooltip("Highlights robot forward (+Z of the independent robot-view reference).")]
    public Color frontColor = new Color(0.3f, 1f, 0.2f, 1f);
    public float frontBorderWidth = 0.05f;

    private GameObject quad;
    private Material material;

    private void Awake()
    {
        Build();
    }

    [ContextMenu("Rebuild Now")]
    public void Build()
    {
        if (quad == null)
        {
            quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Husky Footprint Overlay";

            Collider quadCollider = quad.GetComponent<Collider>();
            if (quadCollider != null)
            {
                Destroy(quadCollider);
            }

            Shader shader = Shader.Find("Insta360/Robot Footprint Overlay");
            if (shader != null)
            {
                material = new Material(shader);
                quad.GetComponent<MeshRenderer>().sharedMaterial = material;
            }
            else
            {
                Debug.LogError(
                    "HuskyBodyReferenceOverlay: shader 'Insta360/Robot Footprint " +
                    "Overlay' not found -- footprint will not be visible."
                );
            }
        }

        UpdatePose();
        ApplyMaterialProperties();
    }

    private void LateUpdate()
    {
        if (quad == null) return;
        UpdatePose();
        ApplyMaterialProperties();
    }

    private void UpdatePose()
    {
        // Unparented geometry stays in meters and never inherits video yaw.
        Quaternion heading = anchor != null ? anchor.rotation : Quaternion.Euler(0f, robotForwardYaw, 0f);
        Vector3 center = anchor != null ? anchor.position : transform.position;
        quad.transform.SetPositionAndRotation(
            center + heading * new Vector3(-mountLateralOffset, -cameraHeightAboveGround, -mountForwardOffset),
            heading * Quaternion.Euler(90f, 0f, 0f));
        quad.transform.localScale = new Vector3(footprintWidth, footprintLength, 1f);
    }

    private void OnEnable()
    {
        if (quad != null) quad.SetActive(true);
    }

    private void OnDisable()
    {
        if (quad != null) quad.SetActive(false);
    }

    private void ApplyMaterialProperties()
    {
        if (material == null) return;

        material.SetColor("_Color", fillColor);
        material.SetColor("_LineColor", lineColor);
        material.SetColor("_FrontColor", frontColor);
        material.SetVector("_WorldSize", new Vector4(footprintWidth, footprintLength, 0f, 0f));
        material.SetFloat("_GridSpacing", Mathf.Max(gridSpacing, 0.01f));
        material.SetFloat("_LineWidth", Mathf.Max(lineWidth, 0.001f));
        material.SetFloat("_BorderWidth", Mathf.Max(borderWidth, 0.001f));
        material.SetFloat("_FrontBorderWidth", Mathf.Max(frontBorderWidth, 0.001f));
    }

    private void OnDestroy()
    {
        if (quad != null)
        {
            Destroy(quad);
            quad = null;
        }
        if (material != null)
        {
            Destroy(material);
            material = null;
        }
    }
}
