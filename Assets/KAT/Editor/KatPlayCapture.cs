#if UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEditor;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
using RosMessageTypes.Nav;

/// <summary>Read-only capture, automatically scoped to an Editor Play session.</summary>
[InitializeOnLoad]
public static class KatPlayCapture
{
    static readonly Dictionary<string, StreamWriter> files = new Dictionary<string, StreamWriter>();
    static readonly Dictionary<string, double> previous = new Dictionary<string, double>();
    static readonly List<string> serials = new List<string>();
    static readonly CultureInfo invariant = CultureInfo.InvariantCulture;
    static Stopwatch clock;
    static string folder;
    static Session session;
    static double nextSample, nextInventory, nextFlush;
    static bool active;
    static KatViewFrameCapture viewCapture;
    const string ViewCaptureNextPlayKey = "KatPlayCapture.RecordGameViewNextPlay";
    const string ViewCaptureMenu = "KAT/Record Game view on next Play";
    static readonly HashSet<string> rosSubscriptions = new HashSet<string>();
    static readonly Dictionary<int, string> configurations = new Dictionary<int, string>();
    public static bool IsCapturing => active;

    [Serializable]
    class Session
    {
        public string format = "KAT Play Capture v2";
        public string startUtc, endUtc, endReason;
        public double durationSeconds;
        public int sampleRows, connectedSampleRows, errorRows;
        public double targetPollHz = 100;
        public double actualSampleRowsPerSecond;
        public bool extraDataRecorded = false;
        public bool viewCaptureRequested;
        public int controlRows, unityPublishRows, odometryRows, chassisInputRows, chassisOutputRows;
        public string controlCaptureError;
        public string controlTiming = "Controller events, ROS receipt and Game-view frames share Unity host time; ROS source stamps retained. Camera exposure timing is not synchronized; publishing is not chassis acknowledgement.";
        public string sampling = "Editor update while Playing and not paused; no catch-up samples";
        public string[] interfaces = { "DeviceCount", "GetDevicesDesc", "GetWalkStatus", "GetLastCalibratedTimeEscaped" };
    }

    static KatPlayCapture()
    {
        EditorApplication.playModeStateChanged += PlayChanged;
        EditorApplication.update += Tick;
        AssemblyReloadEvents.beforeAssemblyReload += () => Finish("assembly_reload");
        EditorApplication.quitting += () => Finish("editor_quit");
        // Script import during Play: start on the next editor tick after reload.
        EditorApplication.delayCall += () => { if (EditorApplication.isPlaying && !active) Begin(); };
    }

    static void PlayChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode) Begin();
        // Do not delay controller OnDisable/zero commands while Play is exiting.
        if (state == PlayModeStateChange.ExitingPlayMode) KatFeedbackSourceProbe.RequestStop("play_exiting");
        // Include controller OnDisable zero commands before closing the streams.
        if (state == PlayModeStateChange.EnteredEditMode) Finish("play_stopped");
    }

    static void Begin()
    {
        if (active) return;
        // SessionState survives the Play domain reload. Consume once so later
        // Play sessions (or a script reload during Play) do not start recording.
        bool recordView = SessionState.GetBool(ViewCaptureNextPlayKey, false);
        SessionState.SetBool(ViewCaptureNextPlayKey, false);
        try
        {
            folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "KATCaptures",
                "KAT_MiniS_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            string readme = Path.Combine(Application.dataPath, "KAT/Editor/KatPlayCapture.README.md");
            File.Copy(readme, Path.Combine(folder, "README.md"));
            session = new Session { startUtc = DateTime.UtcNow.ToString("O"), viewCaptureRequested = recordView };
            clock = Stopwatch.StartNew();
            previous.Clear(); serials.Clear();
            configurations.Clear();
            nextSample = nextInventory = nextFlush = 0;
            active = true;
            HuskyControlTrace.Recorded += ControlRecord;
            CaptureConfigurations();
            EnsureRosCapture();
            SaveSession();
            KatFeedbackSourceProbe.Begin(folder);
            if (recordView)
                viewCapture = KatViewFrameCapture.StartCapture(folder, () => clock.Elapsed.TotalSeconds);
            UnityEngine.Debug.Log("[KAT Capture] 开始采集（不含 extra）：" + folder);
        }
        catch (Exception e)
        {
            KatFeedbackSourceProbe.Stop("capture_start_failed");
            active = false;
            HuskyControlTrace.Recorded -= ControlRecord;
            foreach (var stream in files.Values) stream.Dispose();
            files.Clear();
            UnityEngine.Debug.LogError("[KAT Capture] 无法开始采集：" + e);
        }
    }

    static void Tick()
    {
        if (!active || !EditorApplication.isPlaying || EditorApplication.isPaused) return;
        double now = clock.Elapsed.TotalSeconds;
        if (now < nextSample) return;
        nextSample = now + .01;
        try
        {
            if (now >= nextInventory)
            {
                nextInventory = now + 1;
                CaptureConfigurations();
                EnsureRosCapture();
                try
                {
                    int count = KATNativeSDK.DeviceCount();
                    Write("device_count", "elapsed_s,host_utc_ns,device_count", Stamp(), count);
                    serials.Clear();
                    if (count < 0 || count > 128) throw new InvalidOperationException("Invalid device count: " + count);
                    for (uint i = 0; i < count; i++)
                    {
                        try
                        {
                            var d = KATNativeSDK.GetDevicesDesc(i);
                            Write("devices", "elapsed_s,host_utc_ns,index,device,isBusy,serialNumber,pid,vid,deviceType,deviceSource",
                                Stamp(), i, d.device, d.isBusy, d.serialNumber, d.pid, d.vid, d.deviceType, d.deviceSource);
                            if (!serials.Contains(d.serialNumber ?? "")) serials.Add(d.serialNumber ?? "");
                        }
                        catch (Exception e) { Error("GetDevicesDesc", e); }
                    }
                }
                catch (Exception e) { Error("DeviceCount", e); }
            }
            try
            {
                double value = KATNativeSDK.GetLastCalibratedTimeEscaped();
                Write("calibration", "elapsed_s,host_utc_ns,last_calibrated_time_escaped_raw", Stamp(), value);
            }
            catch (Exception e) { Error("GetLastCalibratedTimeEscaped", e); }

            if (serials.Count == 0) Sample("");
            else foreach (string serial in serials) Sample(serial);
            if (now >= nextFlush)
            {
                nextFlush = now + 1;
                foreach (var stream in files.Values) stream.Flush();
                SaveSession();
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[KAT Capture] 采集写入失败：" + e);
            Finish("capture_error");
        }
    }

    static void Sample(string serial)
    {
        KATNativeSDK.TreadMillData data;
        double started = clock.Elapsed.TotalSeconds;
        try { data = KATNativeSDK.GetWalkStatus(serial); }
        catch (Exception e) { Error("GetWalkStatus:" + serial, e); return; }
        double callMs = (clock.Elapsed.TotalSeconds - started) * 1000;
        object[] stamp = Stamp();
        double old;
        bool hasPrevious = previous.TryGetValue(serial, out old);
        previous[serial] = data.lastUpdateTimePoint;
        Vector3 v = data.moveSpeed;
        Quaternion q = data.bodyRotationRaw;
        int seq = session.sampleRows;
        Write("samples", "elapsed_s,host_utc_ns,sample_index,serial_number,device_name,connected,sdk_call_ms,sdk_last_update_raw,sdk_stamp_changed,sdk_stamp_delta_raw,move_x,move_y,move_z,horizontal_speed,quat_x,quat_y,quat_z,quat_w",
            stamp, seq, serial, data.deviceName, data.connected, callMs, data.lastUpdateTimePoint,
            hasPrevious ? (object)(old != data.lastUpdateTimePoint) : null,
            hasPrevious ? (object)(data.lastUpdateTimePoint - old) : null,
            v.x, v.y, v.z, Math.Sqrt((double)v.x * v.x + (double)v.z * v.z), q.x, q.y, q.z, q.w);
        if (data.deviceDatas != null)
            for (int i = 0; i < data.deviceDatas.Length; i++)
            {
                var s = data.deviceDatas[i];
                Write("device_status", "elapsed_s,host_utc_ns,sample_index,serial_number,slot_index,btnPressed,isBatteryCharging,batteryLevel_raw,firmwareVersion_raw",
                    stamp, seq, serial, i, s.btnPressed, s.isBatteryCharging, s.batteryLevel, s.firmwareVersion);
            }
        session.sampleRows++;
        if (data.connected) session.connectedSampleRows++;
    }

    static void ControlRecord(HuskyControlTrace.Entry entry)
    {
        if (!active) return;
        try
        {
            string columns = HuskyControlTrace.Columns(entry.stage);
            if (columns.Split(',').Length != entry.values.Length)
                throw new InvalidDataException("Control column count mismatch: " + entry.stage);
            var values = new List<object> { entry.sequence, entry.unityReceiptTime, entry.source, entry.topic, entry.inputSequence };
            values.AddRange(entry.values);
            Write("control_" + entry.stage,
                "elapsed_s,host_utc_ns,event_seq,unity_receipt_s,source,topic,input_seq," + columns,
                new object[] { clock.Elapsed.TotalSeconds, entry.hostUtcNs }, values.ToArray());
            session.controlRows++;
            if (entry.stage == "publish") session.unityPublishRows++;
            if (entry.stage == "odometry") session.odometryRows++;
            if (entry.stage == "chassis_command")
            {
                if (entry.topic.EndsWith("/cmd_vel_out")) session.chassisOutputRows++;
                else session.chassisInputRows++;
            }
        }
        catch (Exception ex)
        {
            session.controlCaptureError = ex.Message;
            HuskyControlTrace.Recorded -= ControlRecord;
            UnityEngine.Debug.LogError("[KAT Capture] 控制链记录中断（控制器继续运行）：" + ex.Message);
        }
    }

    static void CaptureConfigurations()
    {
        foreach (var controller in UnityEngine.Object.FindObjectsOfType<KatHuskyDirectAngleController>())
            Configuration(controller, "KAT DirectAngle", controller.cmdVelTopic);
        foreach (var controller in UnityEngine.Object.FindObjectsOfType<CmdVelPublisher>())
            Configuration(controller, "Keyboard", controller.topicName);
    }

    static void Configuration(MonoBehaviour component, string source, string topic)
    {
        string json = JsonUtility.ToJson(component);
        string fingerprint = component.isActiveAndEnabled + json;
        int id = component.GetInstanceID();
        if (configurations.TryGetValue(id, out string old) && old == fingerprint) return;
        configurations[id] = fingerprint;
        HuskyControlTrace.Record("configuration", source, topic, 0, component.isActiveAndEnabled, json);
    }

    static void EnsureRosCapture()
    {
        // Use the existing connection; capture never creates a publisher or a
        // connection. Subscribe once per connection/topic (also with domain reload off).
        var ros = UnityEngine.Object.FindObjectOfType<ROSConnection>();
        if (ros == null) return;
        var odometryTopics = new HashSet<string> { "/odometry/filtered" };
        foreach (var controller in UnityEngine.Object.FindObjectsOfType<KatHuskyDirectAngleController>())
            odometryTopics.Add(controller.odometryTopic);
        foreach (string topic in odometryTopics)
        {
            if (!rosSubscriptions.Add(ros.GetInstanceID() + "|" + topic)) continue;
            ros.Subscribe<OdometryMsg>(topic, message => {
                if (!active || message?.header?.stamp == null || message.pose?.pose == null || message.twist?.twist == null) return;
                var p = message.pose.pose.position; var q = message.pose.pose.orientation;
                if (p == null || q == null) return;
                HuskyControlTrace.Record("odometry", "ROS feedback", topic, 0,
                    message.header.stamp.sec, message.header.stamp.nanosec, message.header.frame_id, message.child_frame_id,
                    p.x, p.y, p.z, q.x, q.y, q.z, q.w, message.twist.twist.linear.x, message.twist.twist.angular.z);
            });
        }
        const string inputTopic = "/husky_velocity_controller/cmd_vel";
        if (rosSubscriptions.Add(ros.GetInstanceID() + "|" + inputTopic))
            ros.Subscribe<TwistMsg>(inputTopic, message => ChassisCommand(inputTopic, message, null, null, ""));
        // Verified against the project's September ROS 1 bag: cmd_vel_out is
        // TwistStamped, whereas the controller input is Twist.
        const string outputTopic = "/husky_velocity_controller/cmd_vel_out";
        if (rosSubscriptions.Add(ros.GetInstanceID() + "|" + outputTopic))
            ros.Subscribe<TwistStampedMsg>(outputTopic, message => {
                if (message?.header?.stamp == null) return;
                ChassisCommand(outputTopic, message.twist, message.header.stamp.sec,
                    message.header.stamp.nanosec, message.header.frame_id);
            });
        // ROSConnection.Unsubscribe removes ALL callbacks for a topic, including
        // controller feedback. Leave these read-only callbacks inert outside capture.
    }

    static void ChassisCommand(string topic, TwistMsg message, object sec, object nsec, string frame)
    {
        if (!active || message?.linear == null || message.angular == null) return;
        HuskyControlTrace.Record("chassis_command", "ROS chassis command", topic, 0, sec, nsec, frame,
            message.linear.x, message.linear.y, message.linear.z, message.angular.x, message.angular.y, message.angular.z);
    }

    public static void AddControlMarker(string label)
    {
        if (active && !string.IsNullOrWhiteSpace(label))
            HuskyControlTrace.Record("marker", "Operator", "", 0, label.Trim());
    }

    static object[] Stamp()
    {
        long ns = (DateTime.UtcNow.Ticks - 621355968000000000L) * 100L;
        return new object[] { clock.Elapsed.TotalSeconds, ns };
    }

    static string Cell(object value)
    {
        if (value == null) return "";
        if (value is double d && (double.IsNaN(d) || double.IsInfinity(d))) return "";
        if (value is float f && (float.IsNaN(f) || float.IsInfinity(f))) return "";
        string s = value is double ? ((double)value).ToString("R", invariant)
            : value is float ? ((float)value).ToString("R", invariant) : Convert.ToString(value, invariant);
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    static void Write(string name, string header, object[] stamp, params object[] values)
    {
        StreamWriter stream;
        if (!files.TryGetValue(name, out stream))
        {
            stream = new StreamWriter(Path.Combine(folder, name + ".csv"), false, new UTF8Encoding(true));
            files.Add(name, stream);
            stream.WriteLine(header);
        }
        var cells = new List<string>();
        foreach (object v in stamp) cells.Add(Cell(v));
        foreach (object v in values) cells.Add(Cell(v));
        stream.WriteLine(string.Join(",", cells));
    }

    static void Error(string api, Exception e)
    {
        session.errorRows++;
        Write("errors", "elapsed_s,host_utc_ns,interface,error", Stamp(), api, e.GetBaseException().Message);
        if (session.errorRows == 1)
            UnityEngine.Debug.LogWarning("[KAT Capture] KAT 查询出现异常，详情将保存在 errors.csv：" + e.GetBaseException().Message);
    }

    static void SaveSession()
    {
        session.durationSeconds = clock.Elapsed.TotalSeconds;
        session.actualSampleRowsPerSecond = session.durationSeconds > 0 ? session.sampleRows / session.durationSeconds : 0;
        File.WriteAllText(Path.Combine(folder, "session.json"), JsonUtility.ToJson(session, true), new UTF8Encoding(false));
    }

    static void Finish(string reason)
    {
        KatFeedbackSourceProbe.Stop(reason);
        if (!active) return;
        if (viewCapture != null)
        {
            viewCapture.StopRecording();
            UnityEngine.Object.DestroyImmediate(viewCapture.gameObject);
            viewCapture = null;
        }
        active = false;
        HuskyControlTrace.Recorded -= ControlRecord;
        try
        {
            foreach (var stream in files.Values) stream.Dispose();
            session.endUtc = DateTime.UtcNow.ToString("O");
            session.endReason = reason;
            SaveSession();
            string zip = folder + ".zip";
            // The optional source probe finishes independently; never wait for it or
            // open its live output files while packaging the main capture.
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetDirectoryName(path) == folder
                        && Path.GetFileName(path).StartsWith("source_probe", StringComparison.Ordinal)) continue;
                    string entry = path.Substring(folder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace('\\', '/');
                    archive.CreateEntryFromFile(path, entry, System.IO.Compression.CompressionLevel.Optimal);
                }
            }
            UnityEngine.Debug.Log("[KAT Capture] 已停止并打包：" + zip
                + " | 样本=" + session.sampleRows + "，连接有效样本=" + session.connectedSampleRows
                + "，异常=" + session.errorRows);
            if (session.connectedSampleRows == 0)
                UnityEngine.Debug.LogWarning("[KAT Capture] 本次没有设备连接有效的 KAT 样本，不能用于 KAT 步行分析；键盘和 ROS 记录需单独检查。");
            if (session.chassisOutputRows == 0 || session.odometryRows == 0)
                UnityEngine.Debug.LogWarning("[KAT Capture] 控制链不完整：底盘输出或里程计未收到，不能当作完整实车响应记录。");
        }
        catch (Exception e) { UnityEngine.Debug.LogError("[KAT Capture] 打包失败，原始 CSV 保留于 " + folder + "：" + e); }
        finally { files.Clear(); previous.Clear(); serials.Clear(); }
    }

    [MenuItem(ViewCaptureMenu)]
    static void ToggleViewRecordingForNextPlay()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        SessionState.SetBool(ViewCaptureNextPlayKey, !SessionState.GetBool(ViewCaptureNextPlayKey, false));
    }

    [MenuItem(ViewCaptureMenu, true)]
    static bool ValidateViewRecordingForNextPlay()
    {
        Menu.SetChecked(ViewCaptureMenu, SessionState.GetBool(ViewCaptureNextPlayKey, false));
        return !EditorApplication.isPlayingOrWillChangePlaymode;
    }

    [MenuItem("KAT/打开采集目录")]
    static void OpenCaptures()
    {
        string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "KATCaptures");
        Directory.CreateDirectory(path);
        EditorUtility.RevealInFinder(path);
    }
}

public class HuskyControlMarkerWindow : EditorWindow
{
    string label = "straight_forward_01_start";
    string lastMarker = "";
    [MenuItem("KAT/控制测试标记")]
    static void Open() { GetWindow<HuskyControlMarkerWindow>("Control marker"); }
    void OnGUI()
    {
        GUILayout.Label(KatPlayCapture.IsCapturing ? "Recording" : "Start Play to record");
        label = EditorGUILayout.TextField("Trial / event", label);
        if (GUILayout.Button("Add marker") && KatPlayCapture.IsCapturing)
        {
            KatPlayCapture.AddControlMarker(label);
            lastMarker = DateTime.UtcNow.ToString("O") + " " + label;
        }
        GUILayout.Label(lastMarker);
        GUILayout.Label("Game view recorded during Play. Camera exposure timing still needs a measured sync event.");
    }
}
#endif
