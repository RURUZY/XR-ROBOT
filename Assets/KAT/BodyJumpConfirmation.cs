using System;
using System.Collections.Generic;

/// <summary>
/// Tells a real fast turn from a KAT sensor glitch when the body yaw jumps.
/// On a fast turn the KAT reading can lag (or freeze) for up to about a second
/// and then catch up in one step; a glitch jumps while the operator stands still.
/// The headset tells them apart: over the same interval it turned the same way
/// by a similar amount only in the first case. 10-21 capture: the 137 s turn
/// (body +114°, headset +101° over 1 s) is real; the 218/223 s jumps (+123°/-131°
/// with the headset within 10°) and four others are glitches.
/// Turning only the head never reaches this: without a body jump nothing is asked.
/// Yaws are degrees in Unity's convention (clockwise positive seen from above),
/// as both the KAT body forward and the headset eulerAngles.y are.
/// </summary>
public sealed class BodyJumpConfirmation
{
    /// <summary>Both turns are measured from this long before the jump.</summary>
    public float LookbackSeconds = 1f;
    /// <summary>The headset must confirm within this time after the jump started.</summary>
    public float WindowSeconds = 0.3f;
    public float ToleranceDeg = 30f;
    /// <summary>A headset sample older than this means the headset is not tracking.</summary>
    public float MaxHeadSampleAgeSeconds = 0.1f;

    private const float HistorySeconds = 4f;
    private readonly List<(float t, double yaw)> head = new List<(float, double)>();
    private readonly List<(float t, double yaw)> body = new List<(float, double)>();
    private double lastRawHeadDeg;

    public void Clear() { head.Clear(); body.Clear(); }

    /// <summary>The accepted body turn restarts here (new session or sensor rebase).</summary>
    public void ResetBody(float t)
    {
        body.Clear();
        body.Add((t, 0.0));
    }

    public void AddHead(float t, float yawDeg)
    {
        if (float.IsNaN(yawDeg) || float.IsInfinity(yawDeg)) return;
        double unwrapped = head.Count == 0 ? yawDeg : head[head.Count - 1].yaw + DeltaAngle(lastRawHeadDeg, yawDeg);
        lastRawHeadDeg = yawDeg;
        if (head.Count > 0 && t <= head[head.Count - 1].t) head[head.Count - 1] = (head[head.Count - 1].t, unwrapped);
        else head.Add((t, unwrapped));
        Prune(head, t);
    }

    /// <summary>An accepted body step (degrees).</summary>
    public void AddBody(float t, double stepDeg)
    {
        if (body.Count == 0) body.Add((t, 0.0));
        body.Add((t, body[body.Count - 1].yaw + stepDeg));
        Prune(body, t);
    }

    /// <param name="rawStepDeg">Jump from the last accepted reading, in (-180, 180].</param>
    /// <param name="confirmedStepDeg">The jump to accept, ±360 chosen to match the headset
    /// (a 200° turn reads as a -160° step).</param>
    public bool TryConfirm(float rejectStart, float now, double rawStepDeg,
        out double confirmedStepDeg, out double headTurnDeg, out double bodyTurnDeg)
    {
        confirmedStepDeg = rawStepDeg;
        headTurnDeg = bodyTurnDeg = double.NaN;
        if (now - rejectStart > WindowSeconds || head.Count == 0 || body.Count == 0) return false;
        if (now - head[head.Count - 1].t > MaxHeadSampleAgeSeconds) return false;
        float from = rejectStart - LookbackSeconds;
        if (head[0].t > from) return false;   // headset history does not reach back that far
        headTurnDeg = head[head.Count - 1].yaw - At(head, from);
        double bodyBefore = body[body.Count - 1].yaw - At(body, from);
        double best = double.PositiveInfinity;
        for (int k = -1; k <= 1; k++)
        {
            double step = rawStepDeg + 360.0 * k;
            double turn = bodyBefore + step;
            if (Math.Abs(turn - headTurnDeg) < best) { best = Math.Abs(turn - headTurnDeg); confirmedStepDeg = step; bodyTurnDeg = turn; }
        }
        return best <= ToleranceDeg && Math.Sign(confirmedStepDeg) == Math.Sign(headTurnDeg)
            && Math.Sign(bodyTurnDeg) == Math.Sign(headTurnDeg);
    }

    /// <summary>Latest value at or before t (the first one if t is earlier).</summary>
    private static double At(List<(float t, double yaw)> samples, float t)
    {
        double value = samples[0].yaw;
        foreach (var s in samples)
        {
            if (s.t > t) break;
            value = s.yaw;
        }
        return value;
    }

    private static void Prune(List<(float t, double yaw)> samples, float now)
    {
        // Keep one sample older than the horizon as the anchor for At().
        int drop = 0;
        while (drop + 1 < samples.Count && samples[drop + 1].t < now - HistorySeconds) drop++;
        if (drop > 0) samples.RemoveRange(0, drop);
    }

    private static double DeltaAngle(double from, double to)
    {
        double d = (to - from) % 360.0;
        if (d > 180) d -= 360; else if (d <= -180) d += 360;
        return d;
    }
}
