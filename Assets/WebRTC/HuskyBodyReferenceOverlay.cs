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
/// Geometry uses the physical robot heading in the calibrated Unity world,
/// independent of video sphere rotation, video delay, and headset rotation.
///
/// Attach directly to the "Insta360 360 Sphere" and leave anchor empty to
/// follow its center and the receiver's calibrated robot heading.
/// An explicit anchor must be a separate robot-view reference, not the sphere.
///
/// Standard A200 external dimensions are 0.99 x 0.67 m. Camera height is
/// approximately 0.8 m on this rig; measure the forward and lateral offsets.
/// </summary>
[DefaultExecutionOrder(100)]
public class HuskyBodyReferenceOverlay : MonoBehaviour
{
    [Header("Anchor")]
    [Tooltip("Optional robot-view reference: position and rotation only. Leave empty to follow the sphere center with independent robotForwardYaw.")]
    public Transform anchor;
    [Tooltip("Robot forward in Unity world degrees when Anchor is empty, independent of video yaw and headset rotation.")]
    public float robotForwardYaw = 0f;
    [Tooltip("Provides the shared real-robot/Unity heading calibration. Required for a live heading when Anchor is empty.")]
    public MediaMtxWhepReceiver headingSource;
    public KatHuskyDirectAngleController angleController;
    public bool showTargetDirection = true;
    public Color targetColor = new Color(0.3f, 0.65f, 1f, 1f);
    public Color waitingColor = new Color(1f, 0.35f, 0.1f, 1f);

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
    private LineRenderer targetArrow;
    private Material targetMaterial;
    private float lastWorldYaw;
    private Color currentFrontColor;
    private float nextTraceTime;
    public float DisplayedRobotWorldYawDeg { get; private set; } = float.NaN;
    public float DisplayedTargetWorldYawDeg { get; private set; } = float.NaN;

    private void Awake()
    {
        if (headingSource == null) headingSource = FindObjectOfType<MediaMtxWhepReceiver>();
        if (angleController == null) angleController = FindObjectOfType<KatHuskyDirectAngleController>();
        lastWorldYaw = robotForwardYaw;
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

        if (targetArrow == null)
        {
            // The video sphere can be smaller than the floor distance. Reuse
            // the stereo-aware ZTest Always overlay shader so the arrow is not
            // hidden behind the panorama mesh.
            Shader shader = Shader.Find("Insta360/Robot Footprint Overlay");
            if (shader != null)
            {
                var arrowObject = new GameObject("Husky Target Direction");
                arrowObject.layer = gameObject.layer;
                targetArrow = arrowObject.AddComponent<LineRenderer>();
                targetMaterial = new Material(shader) { renderQueue = 3100 };
                targetArrow.sharedMaterial = targetMaterial;
                targetArrow.useWorldSpace = true;
                targetArrow.positionCount = 5;
                targetArrow.startWidth = targetArrow.endWidth = 0.025f;
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
        // Hold the last known heading in gray when feedback is stale, rather
        // than snapping the footprint back to zero and implying fresh data.
        bool fresh = anchor != null || (headingSource != null && headingSource.HasCalibratedHeading);
        if (anchor == null && fresh) lastWorldYaw = headingSource.RobotWorldYawDeg + robotForwardYaw;
        Quaternion heading = anchor != null ? anchor.rotation : Quaternion.Euler(0f, lastWorldYaw, 0f);
        DisplayedRobotWorldYawDeg = fresh ? heading.eulerAngles.y : float.NaN;
        DisplayedTargetWorldYawDeg = float.NaN;
        Vector3 center = anchor != null ? anchor.position : transform.position;
        Vector3 floorCenter = center + heading * new Vector3(-mountLateralOffset, -cameraHeightAboveGround, -mountForwardOffset);
        quad.transform.SetPositionAndRotation(
            floorCenter,
            heading * Quaternion.Euler(90f, 0f, 0f));
        quad.transform.localScale = new Vector3(footprintWidth, footprintLength, 1f);
        bool targetValid = fresh && angleController != null && angleController.IsControlActive && angleController.HasUsableHeading
            && (headingSource == null || headingSource.headingOdometryTopic == angleController.odometryTopic);
        float error = targetValid ? (float)angleController.HeadingErrorDeg : 0f;
        currentFrontColor = !fresh ? Color.gray : targetValid
            ? Color.Lerp(frontColor, waitingColor, Mathf.InverseLerp(20f, 75f, Mathf.Abs(error))) : frontColor;
        if (targetArrow != null)
        {
            targetArrow.gameObject.SetActive(isActiveAndEnabled && showTargetDirection && targetValid);
            if (targetValid)
            {
                // Transform the absolute target with exactly the same calibration
                // as the physical footprint. Do not combine heading samples from
                // two independently scheduled odometry subscribers.
                Quaternion target = anchor != null ? heading * Quaternion.Euler(0f, -error, 0f)
                    : Quaternion.Euler(0f, headingSource.WorldYawForRosHeading(angleController.DesiredRobotYawDeg) + robotForwardYaw, 0f);
                DisplayedTargetWorldYawDeg = target.eulerAngles.y;
                Vector3 origin = floorCenter + Vector3.up * 0.02f;
                float length = Mathf.Max(footprintLength * 0.85f, 0.4f);
                Vector3 tip = origin + target * new Vector3(0f, 0f, length);
                targetArrow.startColor = targetArrow.endColor = targetColor;
                targetMaterial.SetColor("_Color", targetColor);
                targetMaterial.SetColor("_LineColor", targetColor);
                targetMaterial.SetColor("_FrontColor", targetColor);
                targetArrow.SetPosition(0, origin);
                targetArrow.SetPosition(1, tip);
                targetArrow.SetPosition(2, tip + target * new Vector3(-0.10f, 0f, -0.16f));
                targetArrow.SetPosition(3, tip);
                targetArrow.SetPosition(4, tip + target * new Vector3(0.10f, 0f, -0.16f));
            }
        }
        if (HuskyControlTrace.IsRecording && Time.realtimeSinceStartup >= nextTraceTime)
        {
            nextTraceTime = Time.realtimeSinceStartup + 0.1f;
            HuskyControlTrace.Record("view_overlay", "Husky overlay", headingSource != null ? headingSource.headingOdometryTopic : "", 0,
                fresh, targetValid && showTargetDirection, DisplayedRobotWorldYawDeg,
                targetValid && showTargetDirection ? DisplayedTargetWorldYawDeg : float.NaN,
                targetValid ? angleController.DesiredRobotYawDeg : double.NaN,
                angleController != null ? angleController.RobotYawDeg : float.NaN,
                targetValid ? error : float.NaN,
                Camera.main != null ? Camera.main.transform.eulerAngles.y : float.NaN,
                anchor != null, headingSource != null && angleController != null && headingSource.headingOdometryTopic == angleController.odometryTopic);
        }
    }

    private void OnEnable()
    {
        if (quad != null) quad.SetActive(true);
    }

    private void OnDisable()
    {
        if (quad != null) quad.SetActive(false);
        if (targetArrow != null) targetArrow.gameObject.SetActive(false);
    }

    private void ApplyMaterialProperties()
    {
        if (material == null) return;

        material.SetColor("_Color", fillColor);
        material.SetColor("_LineColor", lineColor);
        material.SetColor("_FrontColor", currentFrontColor);
        material.SetVector("_WorldSize", new Vector4(footprintWidth, footprintLength, 0f, 0f));
        material.SetFloat("_GridSpacing", Mathf.Max(gridSpacing, 0.01f));
        material.SetFloat("_LineWidth", Mathf.Max(lineWidth, 0.001f));
        material.SetFloat("_BorderWidth", Mathf.Max(borderWidth, 0.001f));
        material.SetFloat("_FrontBorderWidth", Mathf.Max(frontBorderWidth, 0.001f));
    }

    private void OnDestroy()
    {
        if (targetArrow != null) Destroy(targetArrow.gameObject);
        if (targetMaterial != null) Destroy(targetMaterial);
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
