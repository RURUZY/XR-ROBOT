// LivoscanWorldClient.cs - display side of the livoscan XR bridge (protocol livoscan.xr.v0).
// Drop on an empty GameObject ("LivoscanMapRoot"); everything it creates is parented under it, so the
// whole map can be moved/scaled. Data comes from a LivoscanDataSource (assigned, found in the scene,
// or added to this GameObject), so the 1:1 map and LivoscanWristMinimap share one connection.
// Needs only UnityEngine + .NET; no ROS, no extra packages. Unity 2022.3.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[DefaultExecutionOrder(200)]
public class LivoscanWorldClient : MonoBehaviour
{
    public LivoscanDataSource source;
    [Tooltip("Material for the point cloud; must use vertex colours (e.g. Livoscan/VertexColor).")]
    public Material pointMaterial;
    [Tooltip("Material for object boxes; a transparent unlit material works best. Colour is set per label.")]
    public Material objectMaterial;
    [Tooltip("Optional label material. Empty = the font's default material.")]
    public Material labelMaterial;
    [Tooltip("Optional label font (must match labelMaterial's texture). Empty = Unity's built-in font.")]
    public Font labelFont;
    [Tooltip("Optional robot marker material (arrow or default capsule).")]
    public Material robotMaterial;

    [Header("Points")]
    [Tooltip("Keep every Nth point (1 = all).")]
    [Min(1)] public int pointStride = 1;
    [Tooltip("Drop points higher than this above the ground (map metres; 0 = keep all). Removes the ceiling in a top-down view.")]
    public float maxPointHeight = 0f;

    [Header("Labels")]
    public float labelHeight = 0.15f;
    public bool showConfidence = true;
    [Tooltip("0 = labels scale with this map. >0 = labels keep this world scale however small the map is.")]
    public float labelWorldScale = 0f;
    [Tooltip("Hide labels of objects farther than this from the robot (map metres; 0 = show all).")]
    public float labelMaxDistance = 0f;

    [Header("Robot marker")]
    public GameObject robotMarkerPrefab;
    [Tooltip("Flat arrow showing the heading instead of the default capsule.")]
    public bool arrowRobotMarker = false;
    public float arrowLength = 0.8f;

    public Transform RobotMarker => robot;

    // Kept for existing callers.
    public static Vector3 MapToUnity(float x, float y, float z) => LivoscanDataSource.MapToUnity(x, y, z);

    readonly Dictionary<int, GameObject> objectViews = new Dictionary<int, GameObject>();
    readonly Dictionary<int, TextMesh> labels = new Dictionary<int, TextMesh>();
    MaterialPropertyBlock block;
    Transform robot;
    Font font;
    int builtMapVersion, appliedObjectsVersion;

    void Start()
    {
        if (source == null) source = FindObjectOfType<LivoscanDataSource>();
        if (source == null) source = gameObject.AddComponent<LivoscanDataSource>();
        block = new MaterialPropertyBlock();
        font = labelFont != null ? labelFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        if (robotMarkerPrefab != null) robot = Instantiate(robotMarkerPrefab, transform).transform;
        else if (arrowRobotMarker) robot = CreateArrow(arrowLength).transform;
        else
        {
            robot = GameObject.CreatePrimitive(PrimitiveType.Capsule).transform;
            Destroy(robot.GetComponent<Collider>());
            robot.localScale = new Vector3(0.3f, 0.25f, 0.3f);
        }
        robot.SetParent(transform, false);
        if (robotMarkerPrefab == null && robotMaterial != null) robot.GetComponent<Renderer>().sharedMaterial = robotMaterial;
        robot.name = "Robot (livox imu)";
        robot.gameObject.SetActive(false);
    }

    GameObject CreateArrow(float length)
    {
        var mesh = new Mesh { name = "Robot arrow" };
        mesh.vertices = new[]
        {
            new Vector3(0f, 0f, 0.6f * length), new Vector3(0.35f * length, 0f, -0.4f * length),
            new Vector3(0f, 0f, -0.15f * length), new Vector3(-0.35f * length, 0f, -0.4f * length),
        };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        var go = new GameObject();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>();
        return go;
    }

    void Update()
    {
        if (source == null) return;
        if (source.MapVersion != builtMapVersion && source.MapLoaded) BuildPoints();
        if (source.ObjectsVersion != appliedObjectsVersion)
        {
            appliedObjectsVersion = source.ObjectsVersion;
            ApplyObjects(source.Objects);
        }
        robot.gameObject.SetActive(source.HasPose);
        if (source.HasPose)
        {
            robot.localPosition = source.RobotPosition;
            robot.localRotation = Quaternion.Euler(0f, source.RobotYawDeg, 0f);
        }
    }

    void BuildPoints()
    {
        builtMapVersion = source.MapVersion;
        var src = source.MapPoints; var srcCols = source.MapColors;
        int stride = Mathf.Max(pointStride, 1);
        var verts = new List<Vector3>(src.Length / stride + 1);
        var cols = new List<Color32>(src.Length / stride + 1);
        for (int i = 0; i < src.Length; i += stride)
        {
            if (maxPointHeight > 0f && src[i].y > maxPointHeight) continue;   // Unity y = map z
            verts.Add(src[i]); cols.Add(srcCols[i]);
        }
        var idx = new int[verts.Count];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(verts); mesh.SetColors(cols);
        mesh.SetIndices(idx, MeshTopology.Points, 0);

        var existing = transform.Find("Map points");
        if (existing != null) Destroy(existing.gameObject);
        var go = new GameObject("Map points");
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = pointMaterial;
    }

    void ApplyObjects(LivoscanDataSource.ObjectMsg[] objects)
    {
        var seen = new HashSet<int>();
        foreach (var o in objects)
        {
            if (o.center == null || o.center.Length < 3 || o.size == null || o.size.Length < 3) continue;
            seen.Add(o.id);
            if (!objectViews.TryGetValue(o.id, out var view))
            {
                view = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(view.GetComponent<Collider>());
                view.transform.SetParent(transform, false);
                var rend = view.GetComponent<Renderer>();
                if (objectMaterial != null) rend.sharedMaterial = objectMaterial;
                var c = Color.HSVToRGB(Mathf.Abs((o.label ?? "").GetHashCode() % 1000) / 1000f, 0.7f, 1f); c.a = 0.35f;
                rend.GetPropertyBlock(block);
                block.SetColor("_Color", c);
                rend.SetPropertyBlock(block);
                objectViews[o.id] = view;

                var label = new GameObject("label " + o.id).AddComponent<TextMesh>();
                label.transform.SetParent(transform, false);
                label.font = font;
                label.GetComponent<MeshRenderer>().sharedMaterial = labelMaterial != null ? labelMaterial : font.material;
                label.characterSize = 0.05f; label.fontSize = 48; label.anchor = TextAnchor.LowerCenter;
                labels[o.id] = label;
            }
            view.name = $"{o.id} {o.label}";
            view.transform.localPosition = MapToUnity(o.center[0], o.center[1], o.center[2]);
            view.transform.localRotation = Quaternion.Euler(0f, -o.yaw * Mathf.Rad2Deg, 0f);
            view.transform.localScale = new Vector3(o.size[1], o.size[2], o.size[0]);   // (left, up, forward) extents
            var tm = labels[o.id];
            var shown = o.ShownLabel;   // null: name hidden, box still drawn
            tm.text = shown == null ? "" : showConfidence ? $"{shown} ({o.confidence:0.00})" : shown;
            tm.transform.localPosition = view.transform.localPosition + Vector3.up * (o.size[2] / 2f + labelHeight);
        }
        foreach (var id in new List<int>(objectViews.Keys))
        {
            if (seen.Contains(id)) continue;
            Destroy(objectViews[id]); objectViews.Remove(id);
            Destroy(labels[id].gameObject); labels.Remove(id);
        }
    }

    void LateUpdate()
    {
        // Billboard parallel to the view plane. Pointing each label from the camera position (the old
        // LookRotation(pos - cam)) reads mirrored whenever a different camera, e.g. the Scene view, looks at it.
        var cam = Camera.main;
        if (cam == null) return;
        var rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
        float scale = labelWorldScale > 0f ? labelWorldScale / Mathf.Max(transform.lossyScale.x, 1e-6f) : 1f;
        foreach (var pair in labels)
        {
            var tm = pair.Value;
            bool visible = labelMaxDistance <= 0f || !source.HasPose
                || HorizontalDistance(objectViews[pair.Key].transform.localPosition, source.RobotPosition) <= labelMaxDistance;
            if (tm.gameObject.activeSelf != visible) tm.gameObject.SetActive(visible);
            if (!visible) continue;
            tm.transform.rotation = rotation;
            tm.transform.localScale = Vector3.one * scale;
        }
    }

    static float HorizontalDistance(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;
}
