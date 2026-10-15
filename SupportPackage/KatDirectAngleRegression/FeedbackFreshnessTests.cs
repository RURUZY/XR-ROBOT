using System;
using UnityEngine;

partial class Program
{
    static void RunFeedbackFreshnessTests()
    {
        Test("backlog detector is independent of absolute ROS and receiver clock offsets", () => {
            foreach (double clockOffset in new[] { 1000.0, 1791940000.0 }) {
                var freshness = new RobotFeedbackFreshness();
                for (int i = 0; i < 100; i++) {
                    double now = 10 + i * .02;
                    Assert(freshness.Observe(clockOffset + now, now, .5) == RobotFeedbackFreshness.Observation.Accepted, "fresh stream rejected");
                }
                Assert(freshness.IsFresh(11.98, .5) && freshness.BacklogSeconds < .000001, "clock offset treated as latency");
            }
        });
        Test("regular delivery of queued odometry expires despite changing source timestamps", () => {
            var freshness = new RobotFeedbackFreshness();
            freshness.Observe(1000, 10, .5);
            RobotFeedbackFreshness.Observation result = RobotFeedbackFreshness.Observation.Accepted;
            // Similar to the observed queue: arrival time advances much faster
            // than source time; a receipt-only watchdog never notices this.
            for (int i = 1; i <= 100; i++) result = freshness.Observe(1000 + i * .005, 10 + i * .02, .5);
            Assert(result == RobotFeedbackFreshness.Observation.Backlogged, "queued stream trusted");
            Assert(Math.Abs(freshness.BacklogSeconds - 1.5) < .000001 && !freshness.IsFresh(12, .5), "wrong backlog age");
        });
        Test("recorded 19:58 feedback timing is rejected after it falls behind by seconds", () => {
            // Actual /odometry/filtered rows from KAT_MiniS_20261013_195802_249_d1082d.
            // Source stamps have only the constant 1791943000 removed. Receiver
            // times are Unity monotonic receipt times, not PC/ROS wall-clock ages.
            double[,] samples = {
                { 81.897807121, 5.2975058555603027 }, // row 0
                { 82.097805262, 5.2987632751464844 }, // row 10
                { 82.297791004, 5.2999653816223145 }, // row 20
                { 82.697749853, 5.3004255294799805 }, // row 40
                { 83.497759342, 6.0138959884643555 }, // row 80
                { 83.897797108, 6.4681034088134766 }, // row 100
                { 85.897743940, 9.4898099899292000 }, // row 200
                { 91.897697210, 17.157009124755859 }, // row 500
                { 101.897798538, 24.671060562133789 }, // row 1000, temporarily catches up
                { 121.897737503, 49.170146942138672 }, // row 2000
                { 141.897740602, 75.005256652832031 }, // row 3000
                { 161.897870064, 104.56089782714844 }, // row 4000
                { 177.737859249, 120.06105041503906 }  // row 4792
            };
            var freshness = new RobotFeedbackFreshness();
            for (int i = 0; i < samples.GetLength(0); i++) {
                var result = freshness.Observe(samples[i, 0], samples[i, 1], .5);
                if (i == 6 || i == 7 || i >= 9) Assert(result == RobotFeedbackFreshness.Observation.Backlogged, "known delayed packet trusted at row " + i);
                if (i == 8) Assert(result == RobotFeedbackFreshness.Observation.Accepted, "real temporary catch-up rejected");
            }
            Assert(freshness.BacklogSeconds > 19 && !freshness.IsFresh(samples[12, 1], .5), "final measured delay growth missed");
        });
        Test("silence duplicates and delayed bursts cannot erase the minimum offset", () => {
            var freshness = new RobotFeedbackFreshness(); freshness.Observe(1000, 10, .5);
            Assert(!freshness.IsFresh(14, .5), "quiet stream stayed fresh");
            Assert(freshness.Observe(1000, 14, .5) == RobotFeedbackFreshness.Observation.Duplicate, "duplicate accepted");
            for (int i = 1; i <= 20; i++) freshness.Observe(1000 + i * .02, 14, .5);
            Assert(!freshness.IsFresh(14, .5) && freshness.BacklogSeconds > 3.5, "burst rebased old feedback");
            Assert(freshness.Observe(1004, 14, .5) == RobotFeedbackFreshness.Observation.Accepted, "caught-up stream rejected");
            Assert(freshness.IsFresh(14, .5) && freshness.BacklogSeconds < .000001, "did not recover at genuine timeline catch-up");
        });
        Test("initial constant delay is unknowable but subsequent backlog growth is detected", () => {
            var freshness = new RobotFeedbackFreshness();
            // The initial 7 s delay is indistinguishable from a clock offset.
            freshness.Observe(1000, 1007, .5);
            Assert(freshness.BacklogSeconds == 0 && freshness.IsFresh(1007, .5), "claimed absolute latency without synchronized clocks");
            freshness.Observe(1001, 1009, .5);
            Assert(freshness.BacklogSeconds == 1 && !freshness.IsFresh(1009, .5), "later delay growth missed");
        });
        Test("source clock reset is explicitly reported and its first packet is untrusted", () => {
            var freshness = new RobotFeedbackFreshness(); freshness.Observe(1000, 10, .5);
            Assert(freshness.Observe(1, 10.1, .5) == RobotFeedbackFreshness.Observation.SourceReset, "reset not signaled");
            Assert(!freshness.IsFresh(10.1, .5), "reset packet fresh");
            Assert(freshness.Observe(1.1, 10.2, .5) == RobotFeedbackFreshness.Observation.Accepted && freshness.IsFresh(10.2, .5), "new clock epoch unusable");
        });
        Test("backlog pauses legacy motion in both channels and held trigger cannot resume", () => {
            New(); c.activationLatch = false; Input.Keys.Add(KeyCode.T); Call("Update");
            Tick(30, 0, .5f);
            double source = 100.0 + Time.realtimeSinceStartup;
            for (int i = 1; i <= 100; i++) Tick(30, 0, .5f, source + i * .005);
            Assert(!Active && !c.HasFreshOdometry && c.LastPublishedLinear == 0 && c.LastPublishedAngular == 0, "legacy backlog did not stop both channels");
            Assert(c.Status.Contains("ODOMETRY BACKLOG"), "backlog status absent");
            for (int i = 0; i < 10; i++) Tick(30, 0, .5f);
            Assert(c.HasFreshOdometry && !Active && c.LastPublishedLinear == 0, "held trigger restarted during catch-up");
            Input.Keys.Clear(); Call("Update"); Input.Keys.Add(KeyCode.T); Call("Update"); Tick(30, 0);
            Assert(Active && Math.Abs(c.TargetTurnDeg) < .01 && c.LastPublishedLinear == 0, "fresh restart retained old target or footsteps");
            Tick(35, 0, .5f); Assert(c.LastPublishedLinear > 0 && c.HeadingErrorDeg > 0, "fresh new input blocked");
            Input.Keys.Clear(); Call("Update");
        });
        Test("a fresh packet following a controller stall cannot silently revive old activation", () => {
            New(); Tick(30, 0, .5f); Time.realtimeSinceStartup += 4f;
            // No Update/FixedUpdate ran during the stall; callback sees the old age.
            ReceiveOdometry(20); Call("Update"); Call("FixedUpdate");
            Assert(c.HasFreshOdometry && !Active && c.LastPublishedLinear == 0 && c.LastPublishedAngular == 0, "callback erased expired interval");
        });
        Test("legacy mode never auto-resumes a backward source-clock reset", () => {
            New(); Tick(30, 0, .5f); ReceiveOdometry(0, 1);
            Assert(!Active && c.LastPublishedLinear == 0 && c.LastPublishedAngular == 0, "legacy clock reset not paused");
            Time.realtimeSinceStartup += .02f; ReceiveOdometry(0, 1.02); Call("Update"); Call("FixedUpdate");
            Assert(c.HasFreshOdometry && !Active && c.LastPublishedLinear == 0, "new clock auto-resumed");
        });
        Test("press while inactive with backlogged feedback cannot queue a same-frame restart", () => {
            foreach (bool latch in new[] { false, true }) {
                New(); Press(KeyCode.T); c.activationLatch = latch;
                double source = 100.0 + Time.realtimeSinceStartup;
                Time.realtimeSinceStartup += 1;
                ReceiveOdometry(0, source + .02);
                Assert(!Active && !c.HasFreshOdometry, "inactive backlog setup");
                Input.Keys.Add(KeyCode.T); Call("Update");
                // Fresh feedback arrives after the button event, before physics.
                ReceiveOdometry(); Call("FixedUpdate");
                Assert(c.HasFreshOdometry && !Active && c.LastPublishedLinear == 0 && c.LastPublishedAngular == 0, "stale press queued same-frame activation");
                for (int i = 0; i < 5; i++) Tick(30, 0, .5f);
                Assert(!Active && c.LastPublishedLinear == 0, "held stale press resumed later");
                Input.Keys.Clear(); Call("Update"); Press(KeyCode.T);
                if (!latch) { Input.Keys.Add(KeyCode.T); Call("Update"); }
                Tick(30, 0); Assert(Active && c.LastPublishedLinear == 0, "valid new press did not restart");
                Input.Keys.Clear(); Call("Update");
            }
        });
    }
}
