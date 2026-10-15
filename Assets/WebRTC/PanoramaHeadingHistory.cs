using System;
using System.Collections.Generic;

/// <summary>
/// Receipt-time heading history for a manually measured video/odometry offset.
/// This is not video-frame timestamp synchronization. No extrapolation is used.
/// </summary>
public sealed class PanoramaHeadingHistory
{
    private struct Sample { public double time, yaw; }
    private readonly List<Sample> samples = new List<Sample>();

    public void Clear() => samples.Clear();

    public void Add(double receiptTime, double yawDeg)
    {
        if (double.IsNaN(receiptTime) || double.IsInfinity(receiptTime)
            || double.IsNaN(yawDeg) || double.IsInfinity(yawDeg)) return;
        if (samples.Count > 0)
        {
            Sample previous = samples[samples.Count - 1];
            if (receiptTime <= previous.time) return;
            double delta = ((yawDeg - previous.yaw) % 360.0 + 540.0) % 360.0 - 180.0;
            yawDeg = previous.yaw + delta;
        }
        samples.Add(new Sample { time = receiptTime, yaw = yawDeg });
        while (samples.Count > 2 && (samples.Count > 512 || samples[1].time < receiptTime - 5.0))
            samples.RemoveAt(0);
    }

    public bool TrySample(double receiptTime, out float yawDeg)
    {
        yawDeg = 0f;
        if (samples.Count == 0 || double.IsNaN(receiptTime) || double.IsInfinity(receiptTime)) return false;
        Sample previous = samples[0];
        if (receiptTime <= previous.time) { yawDeg = Wrap(previous.yaw); return true; }
        for (int i = 1; i < samples.Count; i++)
        {
            Sample next = samples[i];
            if (receiptTime <= next.time)
            {
                double fraction = (receiptTime - previous.time) / (next.time - previous.time);
                yawDeg = Wrap(previous.yaw + fraction * (next.yaw - previous.yaw));
                return true;
            }
            previous = next;
        }
        yawDeg = Wrap(previous.yaw);
        return true;
    }

    private static float Wrap(double yaw) => (float)((yaw % 360.0 + 540.0) % 360.0 - 180.0);
}
