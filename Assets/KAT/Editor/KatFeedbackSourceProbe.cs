#if UNITY_EDITOR_WIN
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;

/// <summary>One-shot, read-only source timing diagnostic. Never publishes robot commands.</summary>
public static class KatFeedbackSourceProbe
{
    static Process process;
    static Task stdoutCopy, stderrCopy;
    static FileStream stdoutFile, stderrFile;
    static string stopFile, statusFile;
    const string DefaultPython = @"D:\PYTHON312\python.exe";

    public static void Begin(string sessionFolder)
    {
        if (process != null) return;
        string root = Path.GetDirectoryName(Application.dataPath);
        string marker = Path.Combine(root, "SupportPackage", "FeedbackTransportAudit", "enable_next_play.flag");
        if (!File.Exists(marker)) return;
        try
        {
            var ros = UnityEngine.Object.FindObjectOfType<ROSConnection>();
            if (ros == null || !IPAddress.TryParse(ros.RosIPAddress, out IPAddress address))
                throw new InvalidOperationException("No existing ROSConnection with a valid IP; one-shot marker retained.");
            string script = Path.Combine(root, "Tools", "ros_source_probe.py");
            string python = Environment.GetEnvironmentVariable("KAT_SOURCE_PROBE_PYTHON");
            if (string.IsNullOrWhiteSpace(python)) python = DefaultPython;
            if (!Path.IsPathRooted(python) || !File.Exists(python) || !python.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(script))
                throw new FileNotFoundException("Source probe requires its script and an absolute Python .exe path; marker retained.");
            string prefix = Path.Combine(sessionFolder, "source_probe");
            stopFile = Path.Combine(sessionFolder, "source_probe.stop");
            statusFile = Path.Combine(sessionFolder, "source_probe_lifecycle.txt");
            string master = new UriBuilder("http", address.ToString(), 11311).Uri.AbsoluteUri;
            var start = new ProcessStartInfo
            {
                FileName = python,
                Arguments = Quote(script) + " --master-uri " + Quote(master)
                    + " --publisher-node /ekf_localization --topic /odometry/filtered --seconds 60"
                    + " --output-prefix " + Quote(prefix) + " --stop-file " + Quote(stopFile),
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            stdoutFile = new FileStream(prefix + ".stdout.txt", FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stderrFile = new FileStream(prefix + ".stderr.txt", FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            process = new Process { StartInfo = start };
            if (!process.Start()) throw new InvalidOperationException("Python process did not start.");
            stdoutCopy = process.StandardOutput.BaseStream.CopyToAsync(stdoutFile);
            stderrCopy = process.StandardError.BaseStream.CopyToAsync(stderrFile);
            // Preserve the exact opt-in file; moving it consumes only this next-Play request.
            File.Move(marker, Path.Combine(sessionFolder, "source_probe_enable_next_play.flag"));
            File.AppendAllText(statusFile, DateTime.UtcNow.ToString("O") + " started pid=" + process.Id
                + " master=" + master + " duration_limit=60s read_only=true\n");
            UnityEngine.Debug.Log("[KAT Source Probe] One-shot read-only timing capture started: " + prefix);
        }
        catch (Exception error)
        {
            Stop("start_failed");
            UnityEngine.Debug.LogWarning("[KAT Source Probe] Diagnostic not started: " + error.Message);
        }
    }

    public static void RequestStop(string reason)
    {
        if (process == null || stopFile == null) return;
        try { File.WriteAllText(stopFile, reason + "\n"); }
        catch (Exception error) { UnityEngine.Debug.LogWarning("[KAT Source Probe] Stop request: " + error.Message); }
    }

    public static void Stop(string reason)
    {
        if (process == null && stdoutFile == null && stderrFile == null) return;
        RequestStop(reason);
        // Detach this session before cleanup: a later Begin must never share its process/files.
        Process ownedProcess = process;
        Task ownedOutputCopy = stdoutCopy, ownedErrorCopy = stderrCopy;
        FileStream ownedOutput = stdoutFile, ownedError = stderrFile;
        string ownedStatus = statusFile;
        stdoutFile = stderrFile = null;
        process = null; stdoutCopy = stderrCopy = null;
        stopFile = statusFile = null;
        if (reason == "assembly_reload" || reason == "editor_quit")
        {
            // Domain teardown may discard background tasks. Signal only our child now,
            // without waiting and delaying controller cleanup. Summary can be incomplete.
            TryKill(ownedProcess);
            AppendStatus(ownedStatus, reason + " / immediate_process_stop; Python summary may be absent");
        }
        Task.Run(() =>
        {
            try
            {
                if (ownedProcess != null)
                {
                    if (!ownedProcess.HasExited && !ownedProcess.WaitForExit(2500))
                    {
                        TryKill(ownedProcess);
                        ownedProcess.WaitForExit(1000);
                        reason += " / forced_process_stop; Python summary may be absent";
                    }
                    if (ownedOutputCopy != null && ownedErrorCopy != null)
                        Task.WaitAll(new[] { ownedOutputCopy, ownedErrorCopy }, 1000);
                    AppendStatus(ownedStatus, "stopped: " + reason
                        + " exit=" + (ownedProcess.HasExited ? ownedProcess.ExitCode.ToString() : "pending"));
                }
            }
            catch (Exception error) { AppendStatus(ownedStatus, "cleanup: " + error.Message); }
            finally
            {
                TryKill(ownedProcess);
                ownedOutput?.Dispose(); ownedError?.Dispose(); ownedProcess?.Dispose();
            }
        });
    }

    static void TryKill(Process ownedProcess)
    {
        try { if (ownedProcess != null && !ownedProcess.HasExited) ownedProcess.Kill(); }
        catch (Exception) { /* Process.Start may have failed before a child existed. */ }
    }

    static void AppendStatus(string path, string text)
    {
        try { if (path != null) File.AppendAllText(path, DateTime.UtcNow.ToString("O") + " " + text + "\n"); }
        catch (Exception) { /* Diagnostics must never interfere with robot/controller cleanup. */ }
    }

    // Windows command-line quoting; arguments never pass through a shell.
    static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character); slashes = 0;
        }
        result.Append('\\', slashes * 2); result.Append('"');
        return result.ToString();
    }
}
#endif
