using System;
using UnityEngine;

partial class Program
{
    static void Navigation()
    {
        New();
        c.headingTrackingMode = KatHuskyDirectAngleController.HeadingTrackingMode.CurrentDirection;
        c.enableHeadingSpeedAssist = true;
        c.limitTurnByStoppingDistance = true;
        c.maxForwardSpeed = c.maxReverseSpeed = .3f;
        c.maxAngularSpeed = .5f;
        c.bodyYawFilterTime = c.turnRateSmoothingTime = 0f;
    }

    static void RunNavigationTests()
    {
        Test("defaults restore accumulated tracking without the new navigation modifiers", () => {
            var defaults = new KatHuskyDirectAngleController();
            Assert(defaults.maxForwardSpeed == .35f && defaults.maxReverseSpeed == .3f && defaults.maxAngularSpeed == .5f, "current limits changed");
            Assert(!defaults.enableHeadingSpeedAssist && !defaults.limitTurnByStoppingDistance, "navigation modifiers still enabled");
            Assert(defaults.headingTrackingMode == KatHuskyDirectAngleController.HeadingTrackingMode.AccumulatedTurns, "legacy mode not restored");
        });
        Test("navigation follows the current direction without replaying obsolete full turns", () => {
            Navigation();
            for (int i = 1; i <= 120; i++) Tick(i * 3, 0);
            Assert(Math.Abs(c.TargetTurnDeg - 360) < .01, "input angle discarded");
            Assert(Math.Abs(c.HeadingErrorDeg) < .01 && (float)Call("CalculateTurn") == 0, "obsolete revolution queued");
            c.headingTrackingMode = KatHuskyDirectAngleController.HeadingTrackingMode.AccumulatedTurns;
            Assert(Math.Abs(c.HeadingErrorDeg - 360) < .01, "experimental multi-turn target lost");
        });
        Test("opposite-direction overlap prevents chatter but allows a new shorter direction", () => {
            Navigation();
            Set("filteredBodyTurnDeg", 179.0); Assert((float)Call("CalculateTurn") > 0, "initial direction");
            foreach (double target in new[] { 181.0, 179.0, 182.0, 178.0 }) {
                Set("filteredBodyTurnDeg", target); Assert((float)Call("CalculateTurn") > 0, "180-degree chatter");
            }
            Set("filteredBodyTurnDeg", 200.0); Assert((float)Call("CalculateTurn") < 0, "latest direction ignored");
        });
        Test("mid-turn correction updates immediately even while forward motion is held", () => {
            foreach (float sign in new[] { -1f, 1f }) {
                Navigation();
                for (int i = 1; i <= 90; i++) Tick(sign * i, 0, .5f);
                Assert(c.LastPublishedLinear == 0 && c.LastPublishedAngular * sign > 0, "large turn not held");
                Tick(sign * 60, sign * 30, .5f);
                Tick(sign * 50, sign * 30, .5f);
                Assert(Math.Abs(c.HeadingErrorDeg - sign * 20) < .01, "new target not adopted");
                Tick(sign * 10, sign * 30, .5f);
                Assert(c.HeadingErrorDeg * sign < 0 && (float)Call("CalculateTurn") * sign < 0, "old target forced to completion");
                for (int i = 0; i < 30; i++) Tick(sign * 10, sign * 30, .5f);
                Assert(c.LastPublishedAngular * sign < 0 && c.LastPublishedLinear > 0, "did not reverse and resume walking");
            }
        });
        Test("small turns retain walking and assistance reduces speed smoothly as error grows", () => {
            Navigation(); float previous = 1f;
            for (int angle = 0; angle <= 74; angle++) {
                Set("filteredBodyTurnDeg", (double)angle);
                Time.realtimeSinceStartup += .02f;
                ReceiveOdometry();
                float linear = (float)Call("ApplyHeadingSpeedAssist", .3f);
                if (angle <= 20) Assert(Math.Abs(linear - .3f) < .0001, "small turn suppressed walking");
                Assert(c.ForwardSpeedMultiplier <= previous + .0001f, "nonmonotonic slowdown");
                Assert(linear > 0, "stopped before threshold"); previous = c.ForwardSpeedMultiplier;
            }
        });
        Test("heading hold has hysteresis and restoration is gradual in both drive directions", () => {
            Navigation(); Set("filteredBodyTurnDeg", 80.0);
            Assert((float)Call("ApplyHeadingSpeedAssist", -.3f) == 0, "reverse bypassed hold");
            Set("filteredBodyTurnDeg", 70.0); Time.realtimeSinceStartup += .02f;
            Assert((float)Call("ApplyHeadingSpeedAssist", .3f) == 0, "hold threshold chattered");
            Set("filteredBodyTurnDeg", 0.0); Time.realtimeSinceStartup += .02f;
            float first = (float)Call("ApplyHeadingSpeedAssist", -.3f);
            Assert(first < 0 && Math.Abs(first) < .03f, "instant full-speed restoration");
            for (int i = 0; i < 20; i++) { Time.realtimeSinceStartup += .02f; ReceiveOdometry(); Call("ApplyHeadingSpeedAssist", -.3f); }
            Assert(c.ForwardSpeedMultiplier > .99f, "speed failed to recover");
        });
        Test("alignment never replays expired footsteps", () => {
            Navigation(); for (int i = 1; i <= 30; i++) Tick(i * 3, 0, .5f);
            for (int i = 0; i < 35; i++) Tick(90, 0, 0);
            Tick(60, 0); Tick(30, 0); Tick(0, 0);
            for (int i = 0; i < 30; i++) { Tick(); Assert(c.LastPublishedLinear == 0, "queued walking executed after alignment"); }
        });
        Test("heading assistance stops translation on invalid body pose and clears held footsteps", () => {
            Navigation(); Tick(10, 0, .5f); Assert(c.LastPublishedLinear > 0, "setup");
            KATNativeSDK.Data.bodyRotationRaw = new Quaternion(); Call("FixedUpdate");
            Assert(c.LastPublishedLinear == 0 && c.LastPublishedAngular == 0 && Active, "invalid pose stop");
            Tick(10, 0, 0); Assert(c.LastPublishedLinear == 0 && Active, "stale pulse replayed");
            for (int i = 0; i < 20; i++) Tick(10, 0, .5f);
            Assert(c.LastPublishedLinear > 0, "valid new walking did not recover");
        });
        Test("expired feedback stops both channels and requires restart in navigation mode", () => {
            Navigation(); Tick(10, 0, .5f);
            Time.realtimeSinceStartup += .6f; Call("FixedUpdate");
            Assert(c.LastPublishedLinear == 0 && c.LastPublishedAngular == 0, "stale feedback motion");
            Tick(20, 0, .5f);
            Assert(!Active && c.LastPublishedLinear == 0 && c.LastPublishedAngular == 0, "fresh feedback auto-resumed old motion");
            Press(KeyCode.T); Tick(20, 0);
            Assert(Active && Math.Abs(c.TargetTurnDeg) < .01, "explicit restart failed");
        });
        Test("assist-off preserves independent walking for comparison", () => {
            Navigation(); c.enableHeadingSpeedAssist = false;
            for (int i = 1; i <= 30; i++) Tick(i * 3, 0, .5f);
            Assert(c.LastPublishedLinear > 0, "comparison mode coupled");
            KATNativeSDK.Data.bodyRotationRaw = new Quaternion(); Call("FixedUpdate");
            Assert(c.LastPublishedLinear > 0 && c.LastPublishedAngular == 0, "comparison mode changed");
        });
        Test("active body feedforward cannot bypass target braking", () => {
            foreach (float sign in new[] { -1f, 1f }) {
                Navigation(); Set("filteredBodyTurnDeg", sign * 5.0); Set("filteredYawRateDegPerSec", sign * 90f); Set("measuredRobotYawRate", sign * 30f);
                Assert((float)Call("CalculateTurn") == 0, "feedforward bypassed predictive braking");
                Set("measuredRobotYawRate", 0f);
                float command = (float)Call("CalculateTurn");
                float maximum = MathF.Sqrt(2 * c.turnDeceleration * 3 * Mathf.Deg2Rad);
                Assert(command * sign > 0 && Math.Abs(command) <= Math.Min(.5f, maximum) + .0001f, "invalid stopping envelope");
            }
        });
        Test("navigation turns settle with delayed feedback at the configured limit", () => {
            foreach (float sign in new[] { -1f, 1f }) {
                Navigation(); float robot = 0, velocity = 0, peak = 0;
                c.bodyYawFilterTime = .06f; c.turnRateSmoothingTime = .10f;
                c.bodyToRosYawSign = -1f; Call("CalibrateHeading");
                var history = new System.Collections.Generic.Queue<float>(); for (int i = 0; i < 15; i++) history.Enqueue(0);
                for (int i = 1; i <= 1800; i++) {
                    Tick(-sign * Math.Min(i * 1.8f, 90), history.Dequeue(), .5f);
                    Assert(Math.Abs(c.LastPublishedAngular) <= .5001f && Math.Abs(c.LastPublishedLinear) <= .3001f, "scene caps exceeded");
                    velocity += (c.LastPublishedAngular * .65f - velocity) * (1 - MathF.Exp(-.02f / .25f));
                    robot += velocity * .02f * Mathf.Rad2Deg; history.Enqueue(robot); peak = Math.Max(peak, robot * sign);
                }
                Assert(Math.Abs(robot - sign * 90) < 2.2f && peak < 94, "delayed navigation final=" + robot + " peak=" + peak);
                Console.WriteLine("  navigation delayed final=" + robot.ToString("F2") + ", peak=" + peak.ToString("F2"));
            }
        });
        Test("manual pause clears navigation turn and translation immediately", () => {
            Navigation(); for (int i = 1; i <= 30; i++) Tick(i, 0, .5f);
            Press(KeyCode.T); Assert(!Active && c.LastPublishedLinear == 0 && c.LastPublishedAngular == 0, "pause delayed");
            Press(KeyCode.T); Tick(30, 0); Assert(Math.Abs(c.HeadingErrorDeg) < .01 && c.LastPublishedLinear == 0, "old intent resumed");
        });
        Test("blocked navigation cannot release walking by forgetting the target", () => {
            Navigation(); Set("bodyTurnDeg", 90.0); Set("filteredBodyTurnDeg", 90.0);
            for (int i = 0; i < 300; i++) {
                Tick(0, 0, .5f);
                Assert(c.LastPublishedLinear == 0, "blocked turn released forward motion");
            }
            Assert(!Active && c.LastPublishedAngular == 0, "blocked robot did not pause");
            Assert(c.Status.Contains("NO TURN PROGRESS"), "pause reason disappeared");
            Press(KeyCode.T); Tick();
            Assert(Active && !c.Status.Contains("NO TURN PROGRESS"), "explicit restart failed");
        });
        Test("video heading interpolation crosses +/-180 without a long rotation", () => {
            var history = new PanoramaHeadingHistory(); history.Add(1, 179); history.Add(2, -179);
            Assert(history.TrySample(1.5, out float mid) && Math.Abs(Math.Abs(mid) - 180) < .001, "wrap interpolation");
            Assert(history.TrySample(3, out float latest) && Math.Abs(latest + 179) < .001, "extrapolated past last sample");
            history.Add(1.8, 40); Assert(history.TrySample(2, out latest) && Math.Abs(latest + 179) < .001, "out-of-order sample accepted");
        });
        Test("video history offset is independent of the live robot heading and resets cleanly", () => {
            var history = new PanoramaHeadingHistory(); history.Add(10, 0); history.Add(10.1, 10); history.Add(10.2, 20);
            history.TrySample(10.2 - .15, out float delayed); history.TrySample(10.2, out float live);
            Assert(Math.Abs(delayed - 5) < .001 && Math.Abs(live - 20) < .001, "offset did not separate video and live heading");
            history.Clear(); Assert(!history.TrySample(10, out _), "reset retained old origin");
            history.Add(11, -50); history.Add(11, 90); history.Add(double.NaN, 0);
            Assert(history.TrySample(11, out live) && Math.Abs(live + 50) < .001, "bad history sample");
        });
    }
}
