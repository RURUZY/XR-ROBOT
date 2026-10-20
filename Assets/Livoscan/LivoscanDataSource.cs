// LivoscanDataSource.cs - receive side of the livoscan XR bridge (protocol livoscan.xr.v0).
// Owns the HTTP map download and the WebSocket (objects + pose). Views (LivoscanWorldClient for the
// 1:1 map, LivoscanWristMinimap for the hand-held map) read from one shared instance; this component
// draws nothing and never sends anything to the Jetson.
//
// Frame conversion (same convention as Assets/OdometrySubscriber.cs):
//   livoscan_map (x forward, y left, z up, metres)  ->  Unity (x right, y up, z forward)
//   unity = (-y, z, x);  unity yaw (deg, about +y) = -map_yaw (rad) * Rad2Deg
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

[DefaultExecutionOrder(-100)]   // update the smoothed pose before any view reads it
[DisallowMultipleComponent]
public class LivoscanDataSource : MonoBehaviour, ISlamHeadingSource, IGyroHeadingSource
{
    [Tooltip("Jetson running scripts/xr_bridge_server.py")]
    public string server = "http://192.168.2.22:8770";
    public bool loadMap = true;
    public float reconnectInterval = 2f;
    public float mapRetryInterval = 5f;
    public float connectTimeout = 3f;
    [Tooltip("Pose arrives at ~10 Hz; this low-pass hides the steps. 0 = raw.")]
    public float poseSmoothingTime = 0.1f;
    [Tooltip("No pose for this long counts as 'waiting for pose'.")]
    public float poseTimeout = 2f;

    [Serializable] class Envelope { public string type; }
    [Serializable] class PoseMsg { public string type; public double t; public float[] position; public float yaw; public bool localized; public float age_s;
        public double yaw_odom; public double yaw_rate; public float heading_age_s; }
    [Serializable] public class ObjectMsg { public int id; public string label; public float[] center; public float[] size; public float yaw; public float confidence; public int observations; }
    [Serializable] class ObjectsMsg { public string type; public int seq; public ObjectMsg[] objects; }

    // Map in Unity map-frame coordinates. Counters let views poll for changes.
    public Vector3[] MapPoints { get; private set; }
    public Color32[] MapColors { get; private set; }
    public int MapVersion { get; private set; }
    public ObjectMsg[] Objects { get; private set; } = new ObjectMsg[0];
    public int ObjectsVersion { get; private set; }

    public bool Connected { get; private set; }
    public bool HasPose { get; private set; }
    public bool PoseFresh => HasPose && Time.realtimeSinceStartup - lastPoseTime < poseTimeout;
    // 2026-10-11 pose fields (xr-interface.md). Localized: pose shares the map/object frame; false while the live
    // relocaliser has not (re)acquired the map, so the robot may be metres off. Servers without the field count as
    // localized, as replay always is.
    public bool Localized { get; private set; } = true;
    // Seconds since the last SLAM pose: the server's age_s at send time plus how long ago it arrived here, so a
    // stopped SLAM (age_s grows) and a stalled connection (nothing arrives) both show up.
    public float PoseAgeSeconds => HasPose ? lastPoseAgeS + (Time.realtimeSinceStartup - lastPoseTime) : float.PositiveInfinity;
    public bool MapLoaded => MapVersion > 0;
    // Smoothed lidar-IMU pose in the Unity map frame (not the Husky base centre).
    public Vector3 RobotPosition { get; private set; }
    public float RobotYawDeg { get; private set; }

    public static Vector3 MapToUnity(float x, float y, float z) => new Vector3(-y, z, x);

    readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
    CancellationTokenSource cancel;
    Vector3 targetPosition;
    float targetYawDeg, lastPoseTime = -999f, lastPoseAgeS;
    // Unsmoothed map yaw (ROS convention, CCW positive) for the KAT heading correction.
    double rawMapYawDeg;
    int poseSequence;

    public bool TryGetLatestYaw(out int sequence, out double yawDeg, out float receiveTime, out float sourceAgeSeconds)
    {
        sequence = poseSequence; yawDeg = rawMapYawDeg; receiveTime = lastPoseTime; sourceAgeSeconds = lastPoseAgeS;
        return HasPose && isActiveAndEnabled;
    }

    // Gyro turn heading (yaw_odom / yaw_rate / heading_age_s), sent only when the
    // server runs with FAST_POSE=1. Not relocalised: control and panorama only;
    // the minimap keeps using the map-frame yaw above.
    public bool HasGyroHeading { get; private set; }
    int gyroSequence;
    double gyroYawDeg, gyroRateDegPerSec, gyroSourceTime = double.NaN, lastGyroT = double.NaN, lastGyroYawRad = double.NaN;
    float gyroHeadingAgeS, gyroReceiveTime;

    public bool TryGetGyroHeading(out int sequence, out double yawOdomDeg, out double yawRateDegPerSec,
        out float headingAgeSeconds, out float receiveTime, out double sourceTime)
    {
        sequence = gyroSequence; yawOdomDeg = gyroYawDeg; yawRateDegPerSec = gyroRateDegPerSec;
        headingAgeSeconds = gyroHeadingAgeS; receiveTime = gyroReceiveTime; sourceTime = gyroSourceTime;
        return HasGyroHeading && isActiveAndEnabled;
    }
    bool loggedSocketError, loggedMapError, loggedParseError;

    void Start()
    {
        cancel = new CancellationTokenSource();
        if (loadMap) StartCoroutine(LoadMapLoop());
        _ = ReceiveLoop(cancel.Token);
    }

    void OnDestroy() => cancel?.Cancel();

    IEnumerator LoadMapLoop()
    {
        while (!MapLoaded)
        {
            using (var req = UnityWebRequest.Get(server + "/map/points.bin"))
            {
                req.timeout = 30;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success && ParseMap(req.downloadHandler.data)) yield break;
                if (!loggedMapError)
                {
                    loggedMapError = true;   // once, then retry quietly
                    Debug.LogWarning($"[livoscan] map not loaded ({req.error ?? "bad data"}); retrying every {mapRetryInterval:0}s");
                }
            }
            yield return new WaitForSecondsRealtime(Mathf.Max(mapRetryInterval, 1f));
        }
    }

    bool ParseMap(byte[] data)
    {
        try
        {
            var r = new BinaryReader(new MemoryStream(data));
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "LVXP") return false;
            r.ReadUInt32();                                  // format version
            int n = (int)r.ReadUInt32();
            var verts = new Vector3[n]; var cols = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
                verts[i] = MapToUnity(x, y, z);
                cols[i] = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
            }
            MapPoints = verts; MapColors = cols; MapVersion++;
            Debug.Log($"[livoscan] map loaded: {n} points");
            return true;
        }
        catch (Exception) { return false; }   // truncated or corrupt download: retried by the caller
    }

    async Task ReceiveLoop(CancellationToken token)
    {
        var uri = new Uri(server.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws");
        var buffer = new byte[1 << 16];
        while (!token.IsCancellationRequested)
        {
            using (var ws = new ClientWebSocket())
            {
                try
                {
                    using (var connectCancel = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        connectCancel.CancelAfter(TimeSpan.FromSeconds(Mathf.Max(connectTimeout, 0.5f)));
                        await ws.ConnectAsync(uri, connectCancel.Token);
                    }
                    Connected = true;
                    loggedSocketError = false;
                    Debug.Log("[livoscan] connected " + uri);
                    var sb = new StringBuilder();
                    while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        var res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (res.MessageType == WebSocketMessageType.Close) break;
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                        if (res.EndOfMessage)
                        {
                            if (inbox.Count > 200) inbox.TryDequeue(out _);   // not being drained (component disabled)
                            inbox.Enqueue(sb.ToString());
                            sb.Clear();
                        }
                    }
                }
                catch (Exception e)
                {
                    if (token.IsCancellationRequested) break;
                    if (!loggedSocketError)
                    {
                        loggedSocketError = true;   // once per outage, not every reconnect attempt
                        Debug.LogWarning($"[livoscan] not connected ({e.Message}); retrying every {reconnectInterval:0}s");
                    }
                }
                finally { Connected = false; }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Mathf.Max(reconnectInterval, 0.5f)), token); }
            catch (OperationCanceledException) { }
        }
    }

    void Update()
    {
        while (inbox.TryDequeue(out var text))
        {
            try
            {
                var type = JsonUtility.FromJson<Envelope>(text).type;
                if (type == "pose") ApplyPose(JsonUtility.FromJson<PoseMsg>(text), text);
                else if (type == "objects")
                {
                    Objects = JsonUtility.FromJson<ObjectsMsg>(text).objects ?? new ObjectMsg[0];
                    ObjectsVersion++;
                }
            }
            catch (Exception e)
            {
                if (!loggedParseError) { loggedParseError = true; Debug.LogWarning("[livoscan] bad message: " + e.Message); }
            }
        }

        float blend = poseSmoothingTime <= 0f ? 1f : 1f - Mathf.Exp(-Time.deltaTime / poseSmoothingTime);
        RobotPosition = Vector3.Lerp(RobotPosition, targetPosition, blend);
        RobotYawDeg += Mathf.DeltaAngle(RobotYawDeg, targetYawDeg) * blend;
    }

    void ApplyPose(PoseMsg p, string json)
    {
        if (p.position == null || p.position.Length < 3) return;
        // JsonUtility leaves missing fields at default (false / 0); only trust them when the server sent them.
        Localized = !json.Contains("\"localized\"") || p.localized;
        lastPoseAgeS = json.Contains("\"age_s\"") && !float.IsNaN(p.age_s) ? Mathf.Max(p.age_s, 0f) : 0f;
        targetPosition = MapToUnity(p.position[0], p.position[1], p.position[2]);
        targetYawDeg = -p.yaw * Mathf.Rad2Deg;
        rawMapYawDeg = p.yaw * Mathf.Rad2Deg;
        poseSequence++;
        // All three gyro fields or none (old server / FAST_POSE off). The server
        // resends the last pose, so a repeated (t, yaw_odom) is not a new heading.
        if (json.Contains("\"yaw_odom\"") && json.Contains("\"yaw_rate\"") && json.Contains("\"heading_age_s\"")
            && !(p.t == lastGyroT && p.yaw_odom == lastGyroYawRad))
        {
            lastGyroT = p.t; lastGyroYawRad = p.yaw_odom;
            gyroYawDeg = p.yaw_odom * Mathf.Rad2Deg;
            gyroRateDegPerSec = p.yaw_rate * Mathf.Rad2Deg;
            gyroHeadingAgeS = float.IsNaN(p.heading_age_s) ? float.PositiveInfinity : Mathf.Max(p.heading_age_s, 0f);
            gyroSourceTime = p.t;
            gyroReceiveTime = Time.realtimeSinceStartup;
            gyroSequence++;
            HasGyroHeading = true;
        }
        // Snap on the first pose or a relocalisation jump instead of sliding across the map.
        if (!HasPose || (targetPosition - RobotPosition).sqrMagnitude > 4f)
        {
            RobotPosition = targetPosition;
            RobotYawDeg = targetYawDeg;
        }
        HasPose = true;
        lastPoseTime = Time.realtimeSinceStartup;
    }
}
