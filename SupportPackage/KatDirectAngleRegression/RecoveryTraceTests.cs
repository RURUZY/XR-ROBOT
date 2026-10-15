using System;
using System.Collections.Generic;
using UnityEngine;
using RosMessageTypes.Nav;

partial class Program
{
    static void RunRecoveryTraceTests()
    {
        Test("long feedback loss pauses before recovery and cannot release held walking", () => {
            Navigation(); Tick(40, 0, .5f);
            Time.realtimeSinceStartup += 1.1f; Call("FixedUpdate");
            Assert(!Active && c.LastPublishedLinear == 0 && c.LastPublishedAngular == 0, "long outage not latched");
            for (int i = 0; i < 30; i++) Tick(40, 0, .5f);
            Assert(!Active && c.Status.Contains("ODOMETRY GAP") && c.LastPublishedLinear == 0, "feedback auto-resumed");
            Press(KeyCode.T); Tick(40, 0);
            Assert(Active && Math.Abs(c.HeadingErrorDeg) < .01 && c.LastPublishedLinear == 0, "restart retained stale target/step");
            Tick(45, 0, .5f); Assert(c.HeadingErrorDeg > 0 && c.LastPublishedLinear > 0, "new input failed");
        });
        Test("long feedback gap is detected even when there were no intervening controller ticks", () => {
            Navigation(); Tick(40, 0, .5f);
            Time.realtimeSinceStartup += 1.1f; Tick(40, 20, .5f);
            Assert(!Active && c.LastPublishedLinear == 0 && c.LastPublishedAngular == 0, "callback silently reanchored");
        });
        Test("navigation pauses on odometry clock frame or large heading resets", () => {
            foreach (string fault in new[] { "clock", "frame", "yaw" }) {
                Navigation(); Tick(40, 0, .5f);
                var o = new OdometryMsg(); o.header.stamp.sec = fault == "clock" ? 1u : 200u;
                if (fault == "frame") o.header.frame_id = "new_odom";
                if (fault == "yaw") { o.pose.pose.orientation.z = Math.Sin(Math.PI / 4); o.pose.pose.orientation.w = Math.Cos(Math.PI / 4); }
                Call("OnOdometry", o);
                Assert(!Active && c.LastPublishedLinear == 0 && c.LastPublishedAngular == 0, "reset moved robot: " + fault);
            }
        });
        Test("press during lost feedback is not queued for later restart", () => {
            Navigation(); Tick(30, 0, .5f);
            Time.realtimeSinceStartup += 1.1f; Call("FixedUpdate");
            Input.Keys.Add(KeyCode.T); Call("Update");
            Tick(30, 0, .5f); Assert(!Active, "stale press resumed when odometry returned");
            Input.Keys.Clear(); Call("Update"); Press(KeyCode.T); Tick(30, 0);
            Assert(Active && c.LastPublishedLinear == 0, "fresh restart failed");
        });
        Test("hold-to-run requires a physical release and new press after feedback loss", () => {
            Navigation(); c.activationLatch = false; Input.Keys.Add(KeyCode.T); Call("Update"); Tick(30, 0, .5f);
            Time.realtimeSinceStartup += 1.1f; Call("FixedUpdate");
            for (int i = 0; i < 5; i++) Tick(30, 0, .5f);
            Assert(!Active && c.LastPublishedLinear == 0, "held trigger bypassed fault latch");
            Input.Keys.Clear(); Call("Update"); Input.Keys.Add(KeyCode.T); Call("Update"); Tick(30, 0);
            Assert(Active && Math.Abs(c.HeadingErrorDeg) < .01, "new hold did not re-align");
            Input.Keys.Clear(); Call("Update");
        });
        Test("persistent invalid body pose needs explicit restart rather than automatic rebase", () => {
            Navigation(); Tick(20, 0, .5f);
            for (int i = 0; i < 70; i++) Tick(149, 0, .5f);
            Assert(!Active && c.LastPublishedLinear == 0 && c.LastPublishedAngular == 0, "persistent body fault resumed");
            Assert(c.Status.Contains("BODY HEADING GAP"), "body fault reason lost");
            Press(KeyCode.T); Tick(149, 0);
            Assert(Active && Math.Abs(c.HeadingErrorDeg) < .01 && c.LastPublishedLinear == 0, "explicit body reference failed");
        });
        Test("long KAT disconnect cannot be bypassed with a held activation trigger", () => {
            Navigation(); c.activationLatch = false; Input.Keys.Add(KeyCode.T); Call("Update");
            KATNativeSDK.Data.connected = false; Call("FixedUpdate");
            Time.realtimeSinceStartup += 1.1f; Call("FixedUpdate");
            for (int i = 0; i < 5; i++) Tick(0, 0, .5f);
            Assert(!Active && c.LastPublishedLinear == 0, "KAT disconnect resumed with held trigger");
            Input.Keys.Clear(); Call("Update");
        });
        Test("trace connects raw input request and actual limited publication", () => {
            Navigation(); var entries = new List<HuskyControlTrace.Entry>();
            Action<HuskyControlTrace.Entry> collect = entry => entries.Add(entry);
            HuskyControlTrace.Recorded += collect;
            try {
                Tick(40, 0, .5f);
                var input = entries.Find(e => e.stage == "input");
                var request = entries.Find(e => e.stage == "request");
                var published = entries.Find(e => e.stage == "publish");
                Assert(input != null && request != null && published != null, "missing causal stage");
                Assert(input.sequence < request.sequence && request.sequence < published.sequence, "event ordering");
                Assert(input.inputSequence == request.inputSequence && request.inputSequence == published.inputSequence, "input linkage");
                Assert((float)published.values[0] == c.LastPublishedLinear && (float)published.values[1] == c.LastPublishedAngular, "logged request instead of actual publish");
                Assert(Math.Abs((float)request.values[2]) > Math.Abs((float)published.values[1]), "test did not exercise command ramp");
                Assert((float)request.values[0] > (float)request.values[1], "test did not exercise walking assistance");
                foreach (var entry in entries) Assert(HuskyControlTrace.Columns(entry.stage).Split(',').Length == entry.values.Length, "schema mismatch");
                Press(KeyCode.T);
                var stop = entries[entries.Count - 1];
                Assert(stop.stage == "publish" && (float)stop.values[0] == 0 && (float)stop.values[1] == 0, "pause zero absent");
            } finally { HuskyControlTrace.Recorded -= collect; }
        });
        Test("a broken trace subscriber cannot change control output or block other observers", () => {
            Navigation(); Tick(20, 0, .5f); float expected = c.LastPublishedLinear;
            Navigation(); int failures = 0, observed = 0;
            Action<HuskyControlTrace.Entry> broken = entry => { failures++; throw new Exception("disk unavailable"); };
            Action<HuskyControlTrace.Entry> healthy = entry => observed++;
            HuskyControlTrace.Recorded += broken; HuskyControlTrace.Recorded += healthy;
            try {
                Tick(20, 0, .5f);
                Assert(c.LastPublishedLinear == expected && Active, "logging failure interrupted control");
                Assert(failures == 1 && observed >= 3, "failed observer not isolated");
            } finally { HuskyControlTrace.Recorded -= broken; HuskyControlTrace.Recorded -= healthy; }
        });
    }
}
