using System;
using System.Collections.Generic;
using UnityEngine;

// Test double for LivoscanDataSource: publishes a delayed "true" yaw at 10 Hz.
class SlamStub : MonoBehaviour, ISlamHeadingSource
{
    public bool Has; public int Seq; public double Yaw; public float Received; public float Age = .07f;
    public void Push(double yaw) { Has = true; Seq++; Yaw = yaw; Received = Time.realtimeSinceStartup; }
    public bool TryGetLatestYaw(out int sequence, out double yawDeg, out float receiveTime, out float sourceAgeSeconds)
    { sequence = Seq; yawDeg = Yaw; receiveTime = Received; sourceAgeSeconds = Age; return Has; }
}

partial class Program
{
    // Closed loop: the chassis turns at 0.9 x command with a 0.25 s lag. Wheel
    // odometry under-reports every turn by `odomScale` (skid slip, no IMU) and
    // arrives 0.1 s late; SLAM reports the true yaw 0.37 s late (0.27 s behind
    // odometry receipt, as measured on 14 Oct) at 10 Hz.
    static float RunSlamLoop(bool useSlam, float odomScale, Func<int, float> body, int ticks, out float maxActual, out float displayMismatch)
    {
        New();
        c.maxAngularSpeed = 1f; c.nearHeadingKp = 1f; c.headingSource = useSlam ? KatHuskyDirectAngleController.HeadingSource.WheelPlusSlam : KatHuskyDirectAngleController.HeadingSource.WheelOdometry;
        var slam = new SlamStub(); c.slamHeadingSource = slam;
        float actual = 0, velocity = 0; maxActual = 0;
        var odomDelay = new Queue<float>(); for (int i = 0; i < 5; i++) odomDelay.Enqueue(0);
        var slamDelay = new Queue<float>(); for (int i = 0; i < 18; i++) slamDelay.Enqueue(0);
        for (int i = 1; i <= ticks; i++)
        {
            Tick(body(i), odomDelay.Dequeue());
            if (i % 5 == 0) slam.Push(slamDelay.Peek());
            velocity += (c.LastPublishedAngular * .9f - velocity) * (1 - MathF.Exp(-.02f / .25f));
            actual += velocity * .02f * Mathf.Rad2Deg;
            maxActual = Math.Max(maxActual, Math.Abs(actual));
            odomDelay.Enqueue(actual * odomScale);
            slamDelay.Dequeue(); slamDelay.Enqueue(actual);
        }
        // Displays get both the target and the heading in the corrected frame.
        displayMismatch = (float)Math.Abs(c.DesiredRobotYawDeg - c.CorrectedRobotYawDeg);
        return actual;
    }

    static void RunSlamHeadingTests()
    {
        Test("SLAM correction makes a slipping 90 deg pivot land on the real angle", () =>
        {
            Func<int, float> turn = i => MathF.Round(Math.Min(i * .02f * 30f, 90f));
            float wheels = RunSlamLoop(false, .88f, turn, 1500, out _, out _);
            float slam = RunSlamLoop(true, .88f, turn, 1500, out float peak, out float mismatch);
            Console.WriteLine($"  90 deg body turn, odometry 12% short: wheels only -> {wheels:F1} deg, with SLAM -> {slam:F1} deg (peak {peak:F1})");
            Assert(wheels > 99, "test did not reproduce the slip: " + wheels);
            Assert(Math.Abs(slam - 90) < 3, "SLAM-corrected final yaw=" + slam);
            Assert(peak < 97, "SLAM correction overshoot=" + peak);
            Assert(mismatch < 3, "display target and odometry heading disagree when aligned: " + mismatch);
        });

        Test("drift no longer accumulates over repeated left and right turns", () =>
        {
            // +90, -90, +180, -90 in sequence: the accumulated body target ends at +90.
            float[] legs = { 90, 0, 180, 90 };
            Func<int, float> seq = i =>
            {
                int leg = Math.Min((i - 1) / 600, legs.Length - 1);
                float from = leg == 0 ? 0 : legs[leg - 1], to = legs[leg];
                float t = ((i - 1) % 600) * .02f * 30f;
                return MathF.Round(from + Math.Sign(to - from) * Math.Min(t, Math.Abs(to - from)));
            };
            float wheels = RunSlamLoop(false, .88f, seq, 2700, out _, out _);
            float slam = RunSlamLoop(true, .88f, seq, 2700, out _, out _);
            Console.WriteLine($"  after +90/-90/+180/-90 (target +90): wheels only -> {wheels:F1} deg, with SLAM -> {slam:F1} deg");
            Assert(Math.Abs(wheels - 90) > 8, "wheel-only drift not reproduced: " + wheels);
            Assert(Math.Abs(slam - 90) < 3, "SLAM-corrected final yaw=" + slam);
        });

        Test("false encoder yaw while reversing straight no longer steers the robot", () =>
        {
            // Sept bags: reversing with zero angular command, the wheel odometry
            // reported about +6 deg/s of yaw that never happened.
            float Run(bool useSlam)
            {
                New(); c.maxAngularSpeed = 1f; c.nearHeadingKp = .7f; c.headingSource = useSlam ? KatHuskyDirectAngleController.HeadingSource.WheelPlusSlam : KatHuskyDirectAngleController.HeadingSource.WheelOdometry;
                var slam = new SlamStub(); c.slamHeadingSource = slam;
                float actual = 0, velocity = 0, phantom = 0;
                var odomDelay = new Queue<float>(); for (int i = 0; i < 5; i++) odomDelay.Enqueue(0);
                var slamDelay = new Queue<float>(); for (int i = 0; i < 18; i++) slamDelay.Enqueue(0);
                for (int i = 1; i <= 750; i++)
                {
                    Tick(0, odomDelay.Dequeue());
                    if (i % 5 == 0) slam.Push(slamDelay.Peek());
                    velocity += (c.LastPublishedAngular * .9f - velocity) * (1 - MathF.Exp(-.02f / .25f));
                    actual += velocity * .02f * Mathf.Rad2Deg;
                    if (i > 100) phantom += 6f * .02f;   // reversing from t = 2 s
                    odomDelay.Enqueue(actual + phantom);
                    slamDelay.Dequeue(); slamDelay.Enqueue(actual);
                }
                return actual;
            }
            float wheels = Run(false), slam = Run(true);
            Console.WriteLine($"  13 s straight reverse with 6 deg/s false odometry yaw: real heading change wheels only {wheels:F1} deg, with SLAM {slam:F1} deg");
            Assert(Math.Abs(wheels) > 40, "false yaw did not steer the wheel-only robot: " + wheels);
            Assert(Math.Abs(slam) < 6, "SLAM-corrected robot still turned: " + slam);
        });

        Test("accurate odometry is left alone and missing SLAM falls back to odometry", () =>
        {
            Func<int, float> turn = i => MathF.Round(Math.Min(i * .02f * 30f, 60f));
            float withSlam = RunSlamLoop(true, 1f, turn, 1200, out _, out _);
            Assert(Math.Abs(withSlam - 60) < 2.5f, "correction disturbed accurate odometry: " + withSlam);
            New(); c.maxAngularSpeed = 1f;
            for (int i = 0; i < 50; i++) Tick();
            Assert(c.SlamHeadingStatus.StartsWith("NO SLAM") && c.SlamCorrectionDeg == 0, "no source: " + c.SlamHeadingStatus);
        });

        Test("SLAM latency during a steady turn does not create a false correction", () =>
        {
            var k = new SlamHeadingCorrector();
            float t = 0; double yaw = 0, maxAbs = 0;
            var history = new List<(float, double)>();
            for (int i = 0; i < 500; i++)
            {
                t += .02f; yaw += 30 * .02; k.AddOdometry(t, yaw); history.Add((t, yaw));
                if (i % 5 == 4 && t > .5f)
                {
                    float meas = t - .27f; double truth = 0;
                    foreach (var h in history) if (h.Item1 <= meas) truth = h.Item2;
                    k.AddSlam(meas, ((truth + 180) % 360) - 180, 30f);
                    maxAbs = Math.Max(maxAbs, Math.Abs(k.CorrectionDeg));
                }
            }
            Assert(maxAbs < 1, "latency leaked into correction: " + maxAbs);
        });

        Test("relocalisation jumps and stream gaps resync without moving the correction", () =>
        {
            var k = new SlamHeadingCorrector();
            float t = 0;
            for (int i = 0; i < 200; i++) { t += .02f; k.AddOdometry(t, 0); if (i % 5 == 4) k.AddSlam(t, 10, 0); }
            double before = k.CorrectionDeg;
            Assert(Math.Abs(before) < .5, "a constant frame offset was treated as drift: " + before);
            for (int i = 0; i < 20; i++) { t += .02f; k.AddOdometry(t, 0); if (i % 5 == 4) k.AddSlam(t, 100, 0); }
            Assert(Math.Abs(k.CorrectionDeg - before) < .5 && k.SyncCount >= 2, "relocalisation moved the heading: " + k.CorrectionDeg);
            for (int i = 0; i < 100; i++) { t += .02f; k.AddOdometry(t, 0); }
            k.AddSlam(t, -40, 0);
            Assert(Math.Abs(k.CorrectionDeg - before) < .5 && k.Status == "SLAM SYNCED", "gap resync moved the heading");
        });

        Test("real drift converges at a bounded rate and is held during fast turns", () =>
        {
            var k = new SlamHeadingCorrector();
            float t = 0;
            for (int i = 0; i < 25; i++) { t += .02f; k.AddOdometry(t, 0); if (i % 5 == 4) k.AddSlam(t, 0, 0); }
            double prev = k.CorrectionDeg;
            for (int i = 0; i < 400; i++)
            {
                t += .02f; k.AddOdometry(t, 0);
                if (i % 5 == 4)
                {
                    k.AddSlam(t, 10, 0);
                    Assert(k.CorrectionDeg - prev <= 20 * .1 + 1e-6, "correction faster than its rate limit");
                    prev = k.CorrectionDeg;
                }
            }
            Assert(Math.Abs(k.CorrectionDeg - 10) < .3, "drift not removed: " + k.CorrectionDeg);
            double held = k.CorrectionDeg;
            for (int i = 0; i < 50; i++) { t += .02f; k.AddOdometry(t, 0); if (i % 5 == 4) k.AddSlam(t, 0, 60f); }
            Assert(k.CorrectionDeg == held && k.Status.Contains("HOLD"), "fast turn was not held");
        });
    }
}
