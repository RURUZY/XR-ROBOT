using System;
using System.Collections.Generic;
using UnityEngine;

// Test double for LivoscanDataSource with FAST_POSE=1: gyro yaw_odom / yaw_rate /
// heading_age_s, plus the map-frame SLAM yaw and its age_s.
class GyroStub : MonoBehaviour, IGyroHeadingSource, ISlamHeadingSource
{
    public bool SendGyroFields = true;
    public int Seq; public double YawDeg, RateDegPerSec, T; public float HeadingAge = .02f, Received;
    public int SlamSeq; public double SlamYaw = 0; public float SlamAge = .07f, SlamReceived = 0; public bool HasSlam;
    public void PushGyro(double yawDeg, double rateDegPerSec)
    {
        Seq++; YawDeg = ((yawDeg + 180) % 360 + 360) % 360 - 180; RateDegPerSec = rateDegPerSec;
        T = 1000 + Time.realtimeSinceStartup; Received = Time.realtimeSinceStartup;
    }
    public bool TryGetGyroHeading(out int sequence, out double yawOdomDeg, out double yawRateDegPerSec,
        out float headingAgeSeconds, out float receiveTime, out double sourceTime)
    {
        sequence = Seq; yawOdomDeg = YawDeg; yawRateDegPerSec = RateDegPerSec; headingAgeSeconds = HeadingAge;
        receiveTime = Received; sourceTime = T; return SendGyroFields && Seq > 0;
    }
    public bool TryGetLatestYaw(out int sequence, out double yawDeg, out float receiveTime, out float sourceAgeSeconds)
    { sequence = SlamSeq; yawDeg = SlamYaw; receiveTime = SlamReceived; sourceAgeSeconds = SlamAge; return HasSlam; }
}

partial class Program
{
    // Chassis: 0.9 x command, 0.25 s lag. Wheel odometry is wrong by odomScale and
    // a phantom drift (deg/s); the gyro reports the true yaw 40 ms late at 50 Hz.
    static float RunGyroLoop(Func<int, float> body, float odomScale, float phantomDegPerSec, int ticks,
        Func<int, bool> gyroSends, out GyroStub gyro, float yawOffset = 0, Action<int> perTick = null,
        Func<int, float> walk = null)
    {
        New();
        c.maxAngularSpeed = 1f; c.nearHeadingKp = 1f;
        c.headingSource = KatHuskyDirectAngleController.HeadingSource.JetsonGyro;
        gyro = new GyroStub(); c.slamHeadingSource = gyro;
        float actual = 0, velocity = 0, phantom = 0, previous = 0;
        var gyroDelay = new Queue<float>(); gyroDelay.Enqueue(0); gyroDelay.Enqueue(0);
        for (int i = 1; i <= ticks; i++)
        {
            float delayed = gyroDelay.Dequeue();
            if (gyroSends(i)) gyro.PushGyro(delayed + yawOffset, (delayed - previous) / .02f);
            previous = delayed;
            Tick(body(i), actual * odomScale + phantom, walk?.Invoke(i) ?? 0f);
            perTick?.Invoke(i);
            velocity += (c.LastPublishedAngular * .9f - velocity) * (1 - MathF.Exp(-.02f / .25f));
            actual += velocity * .02f * Mathf.Rad2Deg;
            phantom += phantomDegPerSec * .02f;
            gyroDelay.Enqueue(actual);
        }
        return actual;
    }

    static void RunGyroHeadingTests()
    {
        Func<int, float> turn90 = i => MathF.Round(Math.Min(i * .02f * 30f, 90f));
        Func<int, bool> always = i => true;

        Test("gyro heading lands a 90 deg turn within 2 deg while the wheels slip or drift", () =>
        {
            float slip = RunGyroLoop(turn90, .88f, 0, 1500, always, out var g1);
            Assert(c.ActiveHeadingSource == KatHuskyDirectAngleController.HeadingSource.JetsonGyro, "gyro not active");
            float phantom = RunGyroLoop(turn90, 1f, 6f, 1500, always, out var g2);
            Console.WriteLine($"  90 deg turn with gyro heading: wheels 12% short -> {slip:F1} deg, wheels +6 deg/s phantom -> {phantom:F1} deg");
            Assert(Math.Abs(slip - 90) <= 2, "gyro turn with slipping wheels=" + slip);
            Assert(Math.Abs(phantom - 90) <= 2, "gyro turn with phantom wheel yaw=" + phantom);
        });

        Test("a 0.3 s Jetson gap stops turning, keeps walking and does not move the target", () =>
        {
            double targetBefore = double.NaN, targetAfter = double.NaN;
            bool turnStoppedInGap = true, walkedInGap = true;
            // Walk while turning to 60 deg; the gyro goes silent between ticks 101 and 115.
            Func<int, float> body = i => MathF.Round(Math.Min(i * .02f * 30f, 60f));
            RunGyroLoop(body, 1f, 0, 400, i => i <= 100 || i > 115, out var g, 0, i =>
            {
                if (i == 100) targetBefore = c.DesiredRobotYawDeg - c.TargetTurnDeg;
                if (i >= 112 && i <= 115)
                {
                    turnStoppedInGap &= c.LastPublishedAngular == 0;
                    walkedInGap &= c.LastPublishedLinear > 0;
                }
                if (i == 120) targetAfter = c.DesiredRobotYawDeg - c.TargetTurnDeg;
            }, i => .5f);
            Assert(turnStoppedInGap, "turning continued on a stale gyro heading");
            Assert(walkedInGap, "walking stopped with the gyro gap");
            Assert(Math.Abs(targetAfter - targetBefore) < .01, $"target reference jumped {targetBefore:F2} -> {targetAfter:F2}");
        });

        Test("a growing SLAM age_s does not stop gyro turning", () =>
        {
            float final = RunGyroLoop(turn90, .88f, 0, 1500, always, out var g, 0, i =>
            {
                if (i == 1) { var s = (GyroStub)c.slamHeadingSource; s.HasSlam = true; s.SlamSeq = 1; s.SlamAge = 30f; }
            });
            Assert(Math.Abs(final - 90) <= 2, "gyro turn with stale SLAM=" + final);
        });

        Test("yaw_odom crossing +/-180 unwraps without a false full turn", () =>
        {
            var k = new GyroHeadingTracker();
            double[] raw = { 170, 178, -176, -170, -178, 175 };
            int seq = 0; float t = 0;
            foreach (double y in raw) { t += .02f; k.Update(++seq, y, 0, 0, t, t, out _); }
            Assert(Math.Abs(k.UnwrappedYawDeg - 175) < 1e-6, "unwrap=" + k.UnwrappedYawDeg);
            float final = RunGyroLoop(turn90, 1f, 0, 1500, always, out var g, 150f);
            Assert(Math.Abs(final - 90) <= 2, "turn across +/-180 = " + final);
        });

        Test("extrapolation uses yaw_rate x (heading_age_s + local age) and freshness ignores age_s", () =>
        {
            var k = new GyroHeadingTracker { MaxAgeSeconds = .2f };
            k.Update(1, 10, 20, .05f, 1f, 0, out _);
            Assert(Math.Abs(k.HeadingAt(1.05f) - (10 + 20 * .1)) < 1e-4, "extrapolated=" + k.HeadingAt(1.05f));
            Assert(k.IsFresh(1.1f) && !k.IsFresh(1.25f), "receipt freshness");
            k.Update(2, 10, 20, .3f, 2f, 0, out _);
            Assert(!k.IsFresh(2f), "heading_age_s above the limit accepted");
            Assert(!k.Update(2, 99, 0, 0, 3f, 0, out _), "duplicate sequence taken as new");
        });

        Test("without gyro fields the controller runs WheelPlusSlam", () =>
        {
            // An old server / FAST_POSE off: pose messages never carry the gyro fields.
            float final = RunGyroLoop(turn90, 1f, 0, 1500, i => false, out var g);
            Assert(c.ActiveHeadingSource == KatHuskyDirectAngleController.HeadingSource.WheelPlusSlam, "did not fall back");
            Assert(c.HeadingSourceStatus.StartsWith("NO GYRO FIELDS"), "status=" + c.HeadingSourceStatus);
            Assert(Math.Abs(final - 90) <= 2.5f, "fallback turn=" + final);
        });
    }
}
