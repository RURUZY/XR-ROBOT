using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

// Body-yaw jumps confirmed by the headset, and reverse mode cleared on pause.
partial class Program
{
    static void Head(float yaw) { if (Camera.main == null) Camera.main = new Camera(); Camera.main.transform.eulerAngles = new Vector3 { y = yaw }; }

    /// <summary>Head turns to headTo over lagTicks while the KAT reading stays frozen
    /// at 0, then the reading jumps to bodyTo and stays there.</summary>
    static void RunLaggedTurn(float headTo, float bodyTo, int lagTicks = 25)
    {
        New(); c.bodyYawFilterTime = 0f;
        Head(0);
        for (int i = 0; i < 60; i++) Tick(0);
        for (int i = 1; i <= lagTicks; i++) { Head(headTo * i / lagTicks); Tick(0); }
        for (int i = 0; i < 80; i++) Tick(bodyTo);
    }

    static void RunBodyJumpTests()
    {
        Test("KAT jump that catches up a real fast turn is accepted (headset turned too)", () =>
        {
            RunLaggedTurn(120, 110);
            Assert(c.ConfirmedBodyJumps == 1, $"confirmed {c.ConfirmedBodyJumps}");
            Assert(Math.Abs(c.BodyTurnDeg - 110) < .01, $"turn {c.BodyTurnDeg:F1}, expected 110");
        });
        Test("KAT glitch with the headset still is rejected as before", () =>
        {
            RunLaggedTurn(0, 123);
            Assert(c.ConfirmedBodyJumps == 0, "glitch confirmed");
            Assert(Math.Abs(c.BodyTurnDeg) < .01, $"glitch leaked {c.BodyTurnDeg:F1}");
        });
        Test("headset turning the other way does not confirm", () =>
        {
            RunLaggedTurn(-90, 100);
            Assert(c.ConfirmedBodyJumps == 0 && Math.Abs(c.BodyTurnDeg) < .01, $"confirmed {c.BodyTurnDeg:F1}");
        });
        Test("headset turn far larger than the jump does not confirm", () =>
        {
            RunLaggedTurn(170, 100);
            Assert(c.ConfirmedBodyJumps == 0, "40+ deg mismatch confirmed");
        });
        Test("turning only the head never turns the robot", () =>
        {
            RunLaggedTurn(120, 0);
            Assert(c.ConfirmedBodyJumps == 0 && Math.Abs(c.BodyTurnDeg) < .01, $"head-only turn moved {c.BodyTurnDeg:F1}");
        });
        Test("confirmed turn past 180 keeps its full angle", () =>
        {
            // 190 deg reads as a -170 step; the headset says which way it went.
            RunLaggedTurn(200, 190, 35);
            Assert(c.ConfirmedBodyJumps == 1, $"confirmed {c.ConfirmedBodyJumps}");
            Assert(Math.Abs(c.BodyTurnDeg - 190) < .01, $"turn {c.BodyTurnDeg:F1}, expected 190");
        });
        Test("without a headset camera the jump stays rejected", () =>
        {
            Camera.main = null;
            New(); c.bodyYawFilterTime = 0f;
            for (int i = 0; i < 60; i++) Tick(0);
            for (int i = 0; i < 80; i++) Tick(110);
            Assert(c.ConfirmedBodyJumps == 0 && Math.Abs(c.BodyTurnDeg) < .01, "confirmed without headset");
        });
        Test("reverse is kept across a pause (Y or trigger) and toggles while paused", () =>
        {
            New();
            Press(KeyCode.R);
            Assert(c.ReverseModeEnabled, "R did not enter reverse");
            Press(KeyCode.C);
            Assert(!Active && c.ReverseModeEnabled, "Y cleared reverse");
            Press(KeyCode.T);
            Assert(Active && c.ReverseModeEnabled, "reactivation lost reverse");
            Press(KeyCode.T);
            Assert(!Active && c.ReverseModeEnabled, "trigger off cleared reverse");
            Press(KeyCode.R);
            Assert(!Active && !c.ReverseModeEnabled, "R while paused did not leave reverse");
            Press(KeyCode.R);
            Assert(c.ReverseModeEnabled, "R while paused did not enter reverse");
        });
        Test("emergency stop clears reverse", () =>
        {
            New();
            Press(KeyCode.R);
            Press(KeyCode.E);
            Assert(!Active && !c.ReverseModeEnabled, "e-stop left reverse on");
        });
        ReplayCapture();
        Camera.main = null;
    }

    /// <summary>10-21 field capture (KATCaptures is not in git; skipped when absent).
    /// Expected: only the 137 s turn is confirmed; the six glitches stay rejected.</summary>
    static void ReplayCapture()
    {
        string dir = Environment.GetEnvironmentVariable("KAT_REPLAY_CAPTURE")
            ?? Path.Combine(AppContext.BaseDirectory, "../../../../../KATCaptures/KAT_MiniS_20261021_213032_203_8c2aa0");
        if (!File.Exists(Path.Combine(dir, "control_input.csv")) || !File.Exists(Path.Combine(dir, "control_panorama.csv")))
        {
            Console.WriteLine("SKIP 10-21 capture replay (capture not found)");
            return;
        }
        var input = ReadCsv(Path.Combine(dir, "control_input.csv"));
        var pano = ReadCsv(Path.Combine(dir, "control_panorama.csv"));
        var headT = pano.Select(r => F(r["elapsed_s"])).ToArray();
        var headY = pano.Select(r => F(r["head_world_yaw_deg"])).ToArray();
        Test("10-21 capture replay: 137 s turn confirmed, glitches at 49/52/65/66/218/223 s rejected", () =>
        {
            New(); c.enableDebugLog = false;
            var confirmed = new List<float>();
            float baseTime = Time.realtimeSinceStartup + 1f - F(input[0]["elapsed_s"]);
            int h = 0, lastCount = 0;
            foreach (var r in input)
            {
                if (r["connected"] != "True") continue;
                float t = F(r["elapsed_s"]);
                while (h + 1 < headT.Length && headT[h + 1] <= t) h++;
                Head(headY[h]);
                Time.realtimeSinceStartup = baseTime + t;
                KATNativeSDK.Data = new KATNativeSDK.TreadMillData { connected = true, deviceName = "MiniS", extraData = new byte[128],
                    bodyRotationRaw = new Quaternion(F(r["quat_x"]), F(r["quat_y"]), F(r["quat_z"]), F(r["quat_w"])) };
                seq++; ReceiveOdometry(); Call("Update"); Call("FixedUpdate");
                if (!Active) Press(KeyCode.T);
                if (c.ConfirmedBodyJumps != lastCount) { lastCount = c.ConfirmedBodyJumps; confirmed.Add(t); }
            }
            string list = string.Join(", ", confirmed.Select(x => x.ToString("F1")));
            Assert(confirmed.Count(x => x > 136.5f && x < 138f) == 1, $"137 s turn not confirmed (confirmed at: {list})");
            Assert(confirmed.Count == 1, $"glitches confirmed (confirmed at: {list})");
        });
    }

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    static List<Dictionary<string, string>> ReadCsv(string path)
    {
        // Unity may still hold the capture open while Playing.
        var lines = new List<string>();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
            for (string line; (line = reader.ReadLine()) != null;) lines.Add(line);
        var header = lines[0].TrimStart('﻿').Split(',');
        var rows = new List<Dictionary<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            if (cells.Length < header.Length) continue;
            var row = new Dictionary<string, string>();
            for (int i = 0; i < header.Length; i++) row[header[i]] = cells[i].Trim('"');
            rows.Add(row);
        }
        return rows;
    }
}
