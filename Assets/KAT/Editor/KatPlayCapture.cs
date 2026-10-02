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

    [Serializable]
    class Session
    {
        public string format = "KAT Play Capture v1";
        public string startUtc, endUtc, endReason;
        public double durationSeconds;
        public int sampleRows, connectedSampleRows, errorRows;
        public double targetPollHz = 100;
        public double actualSampleRowsPerSecond;
        public bool extraDataRecorded = false;
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
        if (state == PlayModeStateChange.ExitingPlayMode) Finish("play_stopped");
    }

    static void Begin()
    {
        if (active) return;
        try
        {
            folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "KATCaptures",
                "KAT_MiniS_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            string readme = Path.Combine(Application.dataPath, "KAT/Editor/KatPlayCapture.README.md");
            File.Copy(readme, Path.Combine(folder, "README.md"));
            session = new Session { startUtc = DateTime.UtcNow.ToString("O") };
            clock = Stopwatch.StartNew();
            previous.Clear(); serials.Clear();
            nextSample = nextInventory = nextFlush = 0;
            active = true;
            SaveSession();
            UnityEngine.Debug.Log("[KAT Capture] 开始采集（不含 extra）：" + folder);
        }
        catch (Exception e)
        {
            active = false;
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
        if (!active) return;
        active = false;
        try
        {
            foreach (var stream in files.Values) stream.Dispose();
            session.endUtc = DateTime.UtcNow.ToString("O");
            session.endReason = reason;
            SaveSession();
            string zip = folder + ".zip";
            ZipFile.CreateFromDirectory(folder, zip, System.IO.Compression.CompressionLevel.Optimal, false);
            UnityEngine.Debug.Log("[KAT Capture] 已停止并打包：" + zip
                + " | 样本=" + session.sampleRows + "，连接有效样本=" + session.connectedSampleRows
                + "，异常=" + session.errorRows);
            if (session.connectedSampleRows == 0)
                UnityEngine.Debug.LogWarning("[KAT Capture] 本次没有设备连接有效的样本，包仅用于诊断。");
        }
        catch (Exception e) { UnityEngine.Debug.LogError("[KAT Capture] 打包失败，原始 CSV 保留于 " + folder + "：" + e); }
        finally { files.Clear(); previous.Clear(); serials.Clear(); }
    }

    [MenuItem("KAT/打开采集目录")]
    static void OpenCaptures()
    {
        string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "KATCaptures");
        Directory.CreateDirectory(path);
        EditorUtility.RevealInFinder(path);
    }
}
#endif
