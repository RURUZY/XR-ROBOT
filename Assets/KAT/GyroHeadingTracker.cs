using System;

/// <summary>
/// Turn-control heading from the Jetson's lidar (Avia) gyro: the pose message's
/// yaw_odom / yaw_rate / heading_age_s (livoscan.xr.v0, only with FAST_POSE=1).
/// Yaw follows the ROS convention (degrees, counter-clockwise positive).
/// </summary>
public interface IGyroHeadingSource
{
    /// <param name="sequence">Increments once per new heading (new heading_t; every message on a server without it).</param>
    /// <param name="headingAgeSeconds">heading_age_s at send time (not age_s).</param>
    /// <param name="receiveTime">Time.realtimeSinceStartup when the message was taken in.</param>
    /// <param name="sourceTime">The message's t, for link-latency logging.</param>
    /// <returns>false while the server has never sent the gyro fields.</returns>
    bool TryGetGyroHeading(out int sequence, out double yawOdomDeg, out double yawRateDegPerSec,
        out float headingAgeSeconds, out float receiveTime, out double sourceTime);
}

/// <summary>
/// Whether a pose message carries a new gyro heading. Dedupe on heading_t (the
/// IMU stamp of that heading, same clock as t), never on (t, yaw_odom): with SLAM
/// stopped t freezes and with the robot standing still yaw_odom freezes, so that
/// pair stops changing, every message looks like a repeat, the heading goes stale
/// after 0.2 s and turning locks (10-20 Jetson replay: 1 distinct pair in 1138
/// messages). A server without heading_t: every message counts as new and only
/// heading_age_s decides freshness (it grows when the server's heading stalls).
/// </summary>
public static class GyroHeadingDedupe
{
    public static bool IsNewHeading(bool hasHeadingT, double headingT, ref double lastHeadingT)
    {
        if (!hasHeadingT) return true;
        if (double.IsNaN(headingT) || double.IsInfinity(headingT)) return true;
        if (headingT == lastHeadingT) return false;
        lastHeadingT = headingT;
        return true;
    }
}

/// <summary>
/// yaw_odom is gyro-only (zero bias re-estimated while the robot stands still),
/// continuous and never relocalised, so it is used as is: no SLAM correction on
/// top. It is unwrapped here and extrapolated as
/// yaw_odom + yaw_rate × (heading_age_s + time since local receipt), because
/// updates sometimes arrive in bursts (up to ~4° apart when turning fast).
/// Freshness is heading_age_s and receipt age only; age_s grows whenever SLAM
/// stops while this heading stays valid.
/// </summary>
public sealed class GyroHeadingTracker
{
    public float MaxAgeSeconds = 0.2f;
    /// <summary>A pause longer than this means the turn the robot made meanwhile is unknown.</summary>
    public float ResyncGapSeconds = 1f;

    public bool HasData { get; private set; }
    public double UnwrappedYawDeg { get; private set; }
    public double YawRateDegPerSec { get; private set; }
    public float HeadingAgeSeconds { get; private set; }
    public float LastReceiveTime { get; private set; } = float.NegativeInfinity;
    public double LastSourceTime { get; private set; } = double.NaN;
    public int GapCount { get; private set; }

    private int lastSequence = int.MinValue;
    private double lastRawYawDeg;

    public void Reset()
    {
        HasData = false;
        UnwrappedYawDeg = YawRateDegPerSec = 0;
        HeadingAgeSeconds = 0;
        LastReceiveTime = float.NegativeInfinity;
        LastSourceTime = double.NaN;
        GapCount = 0;
        lastSequence = int.MinValue;
    }

    /// <returns>true when this is a new sample. longGap: the stream had paused for
    /// longer than ResyncGapSeconds before it (callers re-anchor the target).</returns>
    public bool Update(int sequence, double yawOdomDeg, double yawRateDegPerSec, float headingAgeSeconds,
        float receiveTime, double sourceTime, out bool longGap)
    {
        longGap = false;
        if (sequence == lastSequence) return false;
        if (double.IsNaN(yawOdomDeg) || double.IsInfinity(yawOdomDeg)
            || double.IsNaN(yawRateDegPerSec) || double.IsInfinity(yawRateDegPerSec)) return false;
        float age = float.IsNaN(headingAgeSeconds) ? float.PositiveInfinity : Math.Max(headingAgeSeconds, 0f);
        if (HasData)
        {
            longGap = receiveTime - LastReceiveTime > ResyncGapSeconds;
            UnwrappedYawDeg += DeltaAngle(lastRawYawDeg, yawOdomDeg);
            if (longGap) GapCount++;
        }
        else UnwrappedYawDeg = yawOdomDeg;
        lastSequence = sequence;
        lastRawYawDeg = yawOdomDeg;
        YawRateDegPerSec = yawRateDegPerSec;
        HeadingAgeSeconds = age;
        LastReceiveTime = receiveTime;
        LastSourceTime = sourceTime;
        HasData = true;
        return true;
    }

    public bool IsFresh(float now) => HasData && now - LastReceiveTime <= MaxAgeSeconds
        && HeadingAgeSeconds <= MaxAgeSeconds;

    /// <summary>Seconds between the heading's measurement and now.</summary>
    public float TotalAgeSeconds(float now) => HasData ? HeadingAgeSeconds + Math.Max(now - LastReceiveTime, 0f)
        : float.PositiveInfinity;

    /// <summary>Extrapolated heading. Never further than 2 × MaxAgeSeconds ahead: past that
    /// IsFresh is false and turning is stopped anyway.</summary>
    public double HeadingAt(float now)
    {
        if (!HasData) return 0;
        double horizon = Math.Min(TotalAgeSeconds(now), 2.0 * Math.Max(MaxAgeSeconds, 0f));
        return UnwrappedYawDeg + YawRateDegPerSec * horizon;
    }

    private static double DeltaAngle(double from, double to)
    {
        double d = (to - from) % 360.0;
        if (d > 180) d -= 360; else if (d < -180) d += 360;
        return d;
    }
}
