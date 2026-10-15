using System;
using System.IO;
using System.Threading;

// Runs only an isolated fake Python script, which never imports sockets or ROS.
static class FeedbackProbeLifecycleCheck
{
    public static void Run(string root)
    {
        string testOutput = Path.GetFullPath("SupportPackage/KatMiniSCapture/test-output") + Path.DirectorySeparatorChar;
        root = Path.GetFullPath(root);
        if (!root.StartsWith(testOutput, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(root).StartsWith("SYNTHETIC_", StringComparison.Ordinal)
            || Path.GetFullPath(UnityEngine.Application.dataPath) != Path.Combine(root, "Assets"))
            throw new InvalidOperationException("Refusing non-synthetic lifecycle test root");
        string tools = Path.Combine(root, "Tools");
        Directory.CreateDirectory(tools);
        // overwrite=false: this cannot replace the real probe even if paths are wrong.
        File.Copy("SupportPackage/KatMiniSCapture/FakeSourceProbe.py", Path.Combine(tools, "ros_source_probe.py"), false);
        string audit = Path.Combine(root, "SupportPackage", "FeedbackTransportAudit");
        Directory.CreateDirectory(audit);
        string marker = Path.Combine(audit, "enable_next_play.flag");
        string destination = Path.Combine(root, "probe-only");
        Directory.CreateDirectory(destination);
        KatFeedbackSourceProbe.Begin(destination);
        if (Directory.GetFiles(destination).Length != 0) throw new Exception("Probe started without marker");
        File.WriteAllText(marker, "SYNTHETIC one-shot request");
        UnityEditor.EditorApplication.Play();
        string session = null;
        for (int i = 0; i < 100; i++)
        {
            foreach (string candidate in Directory.GetDirectories(Path.Combine(root, "KATCaptures")))
                if (File.Exists(Path.Combine(candidate, "source_probe.args.json"))) session = candidate;
            if (session != null) break;
            Thread.Sleep(20);
        }
        if (session == null) throw new Exception("Probe child failed to start");
        if (File.Exists(marker) || !File.Exists(Path.Combine(session, "source_probe_enable_next_play.flag")))
            throw new Exception("Marker was not preserved and consumed once");
        using (var args = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "source_probe.args.json"))))
        {
            if (args.RootElement.GetProperty("master_uri").GetString() != "http://127.0.0.1:11311/")
                throw new Exception("Probe ignored actual ROS IP");
            if (args.RootElement.GetProperty("seconds").GetString() != "60")
                throw new Exception("Probe duration not fixed to 60 seconds");
        }
        UnityEditor.EditorApplication.Stop();
        // The real Editor never waits for diagnostic cleanup; this standalone test does.
        for (int i = 0; i < 150; i++)
        {
            string lifecycle = Path.Combine(session, "source_probe_lifecycle.txt");
            if (File.Exists(lifecycle) && File.ReadAllText(lifecycle).Contains("stopped:")) break;
            Thread.Sleep(20);
        }
        if (!File.Exists(Path.Combine(session, "source_probe.json"))) throw new Exception("Stop lost summary");
        if (!File.ReadAllText(Path.Combine(session, "source_probe.stdout.txt")).Contains("SYNTHETIC STDOUT")
            || !File.ReadAllText(Path.Combine(session, "source_probe.stderr.txt")).Contains("SYNTHETIC STDERR"))
            throw new Exception("Redirected output was lost");
        KatFeedbackSourceProbe.Stop("idempotent");
        KatFeedbackSourceProbe.Begin(destination);
        if (Directory.GetFiles(destination).Length != 0) throw new Exception("One-shot probe repeated without marker");
        Console.WriteLine("Source probe lifecycle passed (offline synthetic child only).");
    }
}
