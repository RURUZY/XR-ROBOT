using System;
using System.Collections.Generic;

/// <summary>
/// A heading source that is independent of the wheels, e.g. the lidar-inertial
/// SLAM pose streamed by LivoscanDataSource. Yaw follows the ROS convention
/// (degrees, counter-clockwise positive, same sign as /odometry/filtered).
/// </summary>
public interface ISlamHeadingSource
{
    /// <param name="sequence">Increments once per new pose.</param>
    /// <param name="receiveTime">Time.realtimeSinceStartup when the pose was taken in.</param>
    /// <param name="sourceAgeSeconds">Age of the pose reported by the sender at send time.</param>
    bool TryGetLatestYaw(out int sequence, out double yawDeg, out float receiveTime, out float sourceAgeSeconds);
}

/// <summary>
/// Wheel odometry stays the fast (50 Hz) heading feedback; SLAM yaw slowly
/// removes the drift that skid-steer slip adds on every pivot. No IMU is fused
/// on this Husky, so /odometry/filtered is wheels only: a 14 Oct capture drifted
/// 58 deg from SLAM in 210 s, with single turns off by up to 12 deg.
///
/// Each SLAM sample is compared with odometry interpolated at the SLAM
/// measurement time, so pose latency does not show up as heading error. Only
/// the change since the last sync is used: the SLAM map frame and the odometry
/// frame never need to agree, and a relocalisation jump, a pause in the stream
/// or an odometry reset re-syncs without moving the correction. The correction
/// is rate-limited and is held while the robot turns fast, where any latency
/// mismatch would be largest.
/// </summary>
public sealed class SlamHeadingCorrector
{
    public float CorrectionTime = 1.5f;
    public float MaxCorrectionRateDegPerSec = 20f;
    public float JumpThresholdDeg = 15f;
    public float ResyncGapSeconds = 1f;
    public float MaxRobotYawRateForUpdateDegPerSec = 40f;
    public float OdometryHistorySeconds = 3f;

    /// <summary>Degrees to add to the unwrapped odometry yaw.</summary>
    public double CorrectionDeg { get; private set; }
    public string Status { get; private set; } = "NO SLAM";
    public int SyncCount { get; private set; }
    public float LastUpdateTime { get; private set; } = float.NegativeInfinity;

    private readonly List<(float t, double yaw)> odometry = new List<(float, double)>();
    private bool synced;
    private double reference, lastSlamUnwrapped, lastSlamRaw, lastOdomAtSlam;
    private float lastSlamTime;

    public void Reset()
    {
        odometry.Clear();
        synced = false;
        CorrectionDeg = 0;
        SyncCount = 0;
        LastUpdateTime = float.NegativeInfinity;
        Status = "NO SLAM";
    }

    /// <summary>Wheel odometry, unwrapped, at its Unity receipt time.</summary>
    public void AddOdometry(float receiptTime, double unwrappedYawDeg)
    {
        if (odometry.Count > 0 && receiptTime < odometry[odometry.Count - 1].t) odometry.Clear();
        odometry.Add((receiptTime, unwrappedYawDeg));
        int drop = 0;
        while (drop < odometry.Count - 2 && receiptTime - odometry[drop].t > OdometryHistorySeconds) drop++;
        if (drop > 0) odometry.RemoveRange(0, drop);
    }

    /// <summary>The odometry unwrap restarted (frame/clock reset or long gap). Keeps the correction.</summary>
    public void OnOdometryDiscontinuity()
    {
        odometry.Clear();
        synced = false;
    }

    public void AddSlam(float measurementTime, double slamYawDeg, float robotYawRateDegPerSec)
    {
        if (double.IsNaN(slamYawDeg) || double.IsInfinity(slamYawDeg)) { Status = "SLAM INVALID"; return; }
        if (!TryOdometryAt(measurementTime, out double odomAt)) { Status = "SLAM WAITING FOR ODOMETRY"; return; }

        double slamUnwrapped = synced ? lastSlamUnwrapped + DeltaAngle(lastSlamRaw, slamYawDeg) : slamYawDeg;
        bool gap = !synced || measurementTime - lastSlamTime > ResyncGapSeconds || measurementTime <= lastSlamTime;
        bool jump = !gap && Math.Abs((slamUnwrapped - lastSlamUnwrapped) - (odomAt - lastOdomAtSlam)) > JumpThresholdDeg;
        if (gap || jump)
        {
            // Continue from the current correction; the new frame offset is absorbed into the reference.
            reference = slamUnwrapped - odomAt - CorrectionDeg;
            synced = true;
            SyncCount++;
            Status = jump ? "SLAM JUMP / RESYNCED" : "SLAM SYNCED";
            Remember(measurementTime, slamYawDeg, slamUnwrapped, odomAt);
            return;
        }

        double innovation = (slamUnwrapped - odomAt - reference) - CorrectionDeg;
        float dt = measurementTime - lastSlamTime;
        if (Math.Abs(robotYawRateDegPerSec) > MaxRobotYawRateForUpdateDegPerSec)
        {
            Status = "SLAM HOLD (FAST TURN)";
        }
        else
        {
            double step = innovation * (CorrectionTime <= 0 ? 1 : 1 - Math.Exp(-dt / CorrectionTime));
            double limit = Math.Max(MaxCorrectionRateDegPerSec, 0) * dt;
            CorrectionDeg += Math.Max(-limit, Math.Min(limit, step));
            LastUpdateTime = measurementTime;
            Status = Math.Abs(innovation) > 2 ? "SLAM CORRECTING" : "SLAM OK";
        }
        Remember(measurementTime, slamYawDeg, slamUnwrapped, odomAt);
    }

    private void Remember(float t, double raw, double unwrapped, double odomAt)
    {
        lastSlamTime = t; lastSlamRaw = raw; lastSlamUnwrapped = unwrapped; lastOdomAtSlam = odomAt;
    }

    private bool TryOdometryAt(float t, out double yaw)
    {
        yaw = 0;
        int n = odometry.Count;
        if (n == 0 || t < odometry[0].t || t > odometry[n - 1].t + 0.1f) return false;
        if (t >= odometry[n - 1].t) { yaw = odometry[n - 1].yaw; return true; }
        int hi = 1;
        while (odometry[hi].t < t) hi++;
        var a = odometry[hi - 1]; var b = odometry[hi];
        double f = b.t > a.t ? (t - a.t) / (b.t - a.t) : 0;
        yaw = a.yaw + f * (b.yaw - a.yaw);
        return true;
    }

    private static double DeltaAngle(double from, double to)
    {
        double d = (to - from) % 360.0;
        if (d > 180) d -= 360; else if (d < -180) d += 360;
        return d;
    }
}
