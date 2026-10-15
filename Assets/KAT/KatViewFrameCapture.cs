#if UNITY_EDITOR_WIN
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

/// <summary>Read-only Editor Game view capture, including rendered overlays.
/// Bounded to one GPU readback / background JPEG write. Never drives ROS or KAT.</summary>
public sealed class KatViewFrameCapture : MonoBehaviour
{
    const float Interval = 0.2f;
    const int MaxEdge = 1280;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    string folder, failure = "", previousFile;
    Func<double> elapsed;
    StreamWriter index, concat;
    RenderTexture full, scaled;
    AsyncGPUReadbackRequest pendingRequest;
    Task writeTask;
    bool running, pending, finished;
    int requested, written, skipped;
    float nextFrameTime;
    double previousElapsed;
    string pendingRow, pendingFile;
    double pendingElapsed;
    int pendingWidth, pendingHeight;
    MediaMtxWhepReceiver panorama;
    HuskyBodyReferenceOverlay overlay;
    KatHuskyDirectAngleController controller;

    [Serializable] class Info
    {
        public string surface = "Unity Game view (desktop XR mirror), with overlays; not raw Insta360 frames or independent physical robot video";
        public float targetFps = 5;
        public int maxEdge = MaxEdge, frames, skippedBusy;
        public string state, error;
        public string timing = "End-of-frame Unity host timestamps, before asynchronous GPU readback; no camera exposure timestamp";
    }

    public static KatViewFrameCapture StartCapture(string captureFolder, Func<double> elapsedSeconds)
    {
        var go = new GameObject("KAT view capture (read only)") { hideFlags = HideFlags.HideAndDontSave };
        var capture = go.AddComponent<KatViewFrameCapture>();
        try
        {
            capture.folder = captureFolder;
            capture.elapsed = elapsedSeconds;
            if (!SystemInfo.supportsAsyncGPUReadback) throw new NotSupportedException("Async GPU readback unavailable; view recording disabled.");
            Directory.CreateDirectory(Path.Combine(captureFolder, "view_frames"));
            capture.index = new StreamWriter(Path.Combine(captureFolder, "view_frames.csv"), false, new UTF8Encoding(true)) { AutoFlush = true };
            capture.index.WriteLine("frame_index,elapsed_s,host_utc_ns,unity_receipt_s,unity_frame,file,width,height,head_world_yaw_deg,robot_world_yaw_deg,target_world_yaw_deg,odom_yaw_deg,heading_error_deg,source_mode,heading_calibrated");
            capture.concat = new StreamWriter(Path.Combine(captureFolder, "view_frames.ffconcat"), false, new UTF8Encoding(false)) { AutoFlush = true };
            capture.concat.WriteLine("ffconcat version 1.0");
            capture.panorama = FindObjectOfType<MediaMtxWhepReceiver>();
            capture.overlay = FindObjectOfType<HuskyBodyReferenceOverlay>();
            capture.controller = FindObjectOfType<KatHuskyDirectAngleController>();
            capture.running = true;
            capture.SaveInfo("recording");
            capture.StartCoroutine(capture.CaptureLoop());
            return capture;
        }
        catch (Exception ex)
        {
            capture.failure = ex.GetBaseException().Message;
            capture.StopRecording();
            Destroy(go);
            Debug.LogWarning("[KAT View Capture] " + capture.failure);
            return null;
        }
    }

    IEnumerator CaptureLoop()
    {
        var endOfFrame = new WaitForEndOfFrame();
        while (running)
        {
            // Game view must be rendering. This does not capture other desktop apps.
            yield return endOfFrame;
            if (!running || Time.realtimeSinceStartup < nextFrameTime) continue;
            nextFrameTime = Time.realtimeSinceStartup + Interval;
            if (writeTask != null && writeTask.IsFaulted) { Fail(writeTask.Exception); continue; }
            if (pending || (writeTask != null && !writeTask.IsCompleted)) { skipped++; continue; }
            try { CaptureFrame(); }
            catch (Exception ex) { Fail(ex); }
        }
    }

    void CaptureFrame()
    {
        int width = Screen.width, height = Screen.height;
        if (width < 2 || height < 2) return;
        if (full == null || full.width != width || full.height != height)
        {
            ReleaseTextures();
            full = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            float factor = Mathf.Min(1f, (float)MaxEdge / Mathf.Max(width, height));
            scaled = new RenderTexture(Mathf.Max(2, Mathf.RoundToInt(width * factor)), Mathf.Max(2, Mathf.RoundToInt(height * factor)), 0, RenderTextureFormat.ARGB32);
            full.Create(); scaled.Create();
        }
        pendingElapsed = elapsed();
        long utcNs = (DateTime.UtcNow.Ticks - 621355968000000000L) * 100L;
        pendingFile = "view_frames/frame_" + (++requested).ToString("D6", Inv) + ".jpg";
        pendingWidth = scaled.width; pendingHeight = scaled.height;
        bool headingValid = panorama != null && panorama.HasCalibratedHeading;
        bool targetValid = controller != null && controller.IsControlActive && controller.HasUsableHeading;
        pendingRow = string.Join(",", requested.ToString(Inv), pendingElapsed.ToString("R", Inv), utcNs.ToString(Inv),
            Time.realtimeSinceStartup.ToString("R", Inv), Time.frameCount.ToString(Inv), pendingFile,
            pendingWidth.ToString(Inv), pendingHeight.ToString(Inv),
            Number(Camera.main != null ? Camera.main.transform.eulerAngles.y : float.NaN),
            Number(overlay != null && overlay.isActiveAndEnabled ? overlay.DisplayedRobotWorldYawDeg : float.NaN),
            Number(overlay != null && overlay.isActiveAndEnabled && overlay.showTargetDirection ? overlay.DisplayedTargetWorldYawDeg : float.NaN),
            Number(headingValid ? panorama.RobotHeadingDeg : float.NaN),
            Number(targetValid ? controller.HeadingErrorDeg : double.NaN),
            panorama != null ? panorama.panoramaHeadingMode.ToString() : "", headingValid.ToString());
        ScreenCapture.CaptureScreenshotIntoRenderTexture(full);
        Graphics.Blit(full, scaled);
        pending = true;
        try { pendingRequest = AsyncGPUReadback.Request(scaled, 0, TextureFormat.RGBA32, ReadbackComplete); }
        catch { pending = false; throw; }
    }

    void ReadbackComplete(AsyncGPUReadbackRequest request)
    {
        if (!pending) return; // StopRecording can drain the final request explicitly.
        pending = false;
        try
        {
            if (request.hasError) throw new IOException("GPU frame readback failed.");
            byte[] pixels = request.GetData<byte>().ToArray();
            int width = pendingWidth, height = pendingHeight;
            string relativeFile = pendingFile, row = pendingRow;
            double frameElapsed = pendingElapsed;
            // EncodeArrayToJPG is thread safe. Only copied CPU pixels and file I/O
            // go to the worker; no scene objects or robot APIs are accessed there.
            writeTask = Task.Run(() => {
                byte[] jpg = ImageConversion.EncodeArrayToJPG(pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height, 0, 85);
                File.WriteAllBytes(Path.Combine(folder, relativeFile), jpg);
                index.WriteLine(row); // Rows only refer to successfully saved images.
                if (previousFile != null)
                {
                    concat.WriteLine("file '" + previousFile + "'");
                    concat.WriteLine("duration " + Math.Max(0.001, frameElapsed - previousElapsed).ToString("R", Inv));
                }
                previousFile = relativeFile;
                previousElapsed = frameElapsed;
                System.Threading.Interlocked.Increment(ref written);
            });
        }
        catch (Exception ex) { Fail(ex); }
    }

    static string Number(double value) => double.IsNaN(value) || double.IsInfinity(value) ? "" : value.ToString("R", Inv);

    void Fail(Exception ex)
    {
        failure = ex.GetBaseException().Message;
        running = false;
        Debug.LogWarning("[KAT View Capture] Recording stopped: " + failure);
    }

    public void StopRecording()
    {
        if (finished) return;
        finished = true;
        running = false;
        StopAllCoroutines();
        try
        {
            // Only on capture shutdown: finish the single outstanding frame
            // before closing its files or allowing the session zip to be built.
            if (pending)
            {
                pendingRequest.WaitForCompletion();
                if (pending) ReadbackComplete(pendingRequest);
            }
            if (writeTask != null) writeTask.GetAwaiter().GetResult();
            if (previousFile != null)
            {
                concat.WriteLine("file '" + previousFile + "'");
                concat.WriteLine("duration " + Interval.ToString("R", Inv));
                concat.WriteLine("file '" + previousFile + "'");
            }
        }
        catch (Exception ex) { failure = ex.GetBaseException().Message; }
        finally
        {
            index?.Dispose(); concat?.Dispose();
            ReleaseTextures();
            SaveInfo(failure.Length > 0 ? "failed" : written == 0 ? "no_frames" : "stopped");
        }
        if (written == 0 || failure.Length > 0)
            Debug.LogWarning("[KAT View Capture] Saved " + written + " frames. Keep the Game view rendering. " + failure);
    }

    void SaveInfo(string state)
    {
        if (string.IsNullOrEmpty(folder)) return;
        try { File.WriteAllText(Path.Combine(folder, "view_capture.json"), JsonUtility.ToJson(new Info {
            frames = written, skippedBusy = skipped, state = state, error = failure
        }, true), new UTF8Encoding(false)); }
        catch (Exception ex) { Debug.LogWarning("[KAT View Capture] Cannot save capture status: " + ex.Message); }
    }

    void ReleaseTextures()
    {
        if (full != null) { full.Release(); Destroy(full); full = null; }
        if (scaled != null) { scaled.Release(); Destroy(scaled); scaled = null; }
    }

    void OnGUI()
    {
        GUI.Box(new Rect(Mathf.Max(0, Screen.width - 280), Mathf.Max(0, Screen.height - 32), 270, 26),
            failure.Length > 0 ? "VIEW RECORDING ERROR (see Console)" : "VIEW REC: " + written + " frames / target 5 fps");
    }

    void OnDisable() { StopRecording(); }
    void OnDestroy() { StopRecording(); }
}
#endif
