using System;

/// <summary>
/// Detects growing source-message backlog without assuming synchronized ROS/PC clocks.
/// The smallest observed receiver-minus-source offset is the reference. Silence and
/// repeated timestamps age the stream; queued messages cannot renew it merely by arriving.
/// A constant delay already present at the first sample cannot be measured this way.
/// A backward source clock starts a new epoch and reports SourceReset: motion callers
/// must latch a stop and require an explicit restart even when subsequent samples recover.
/// </summary>
public sealed class RobotFeedbackFreshness
{
    public enum Observation { Accepted, Duplicate, Invalid, Backlogged, SourceReset }

    public bool HasSample { get; private set; }
    public double BacklogSeconds { get; private set; }
    private double lastSource, lastReceipt, bestOffset;
    private bool epochUsable;

    public void Reset()
    {
        HasSample = epochUsable = false;
        BacklogSeconds = 0;
        lastSource = lastReceipt = bestOffset = 0;
    }

    public Observation Observe(double sourceSeconds, double receiverMonotonicSeconds, double timeoutSeconds)
    {
        if (!Finite(sourceSeconds) || sourceSeconds <= 0 || !Finite(receiverMonotonicSeconds)
            || receiverMonotonicSeconds < 0 || !Finite(timeoutSeconds) || timeoutSeconds <= 0)
            return Observation.Invalid;
        if (HasSample && receiverMonotonicSeconds < lastReceipt) return Observation.Invalid;
        if (HasSample && sourceSeconds == lastSource) return Observation.Duplicate;

        bool sourceReset = HasSample && sourceSeconds < lastSource;
        double offset = receiverMonotonicSeconds - sourceSeconds;
        if (!HasSample || sourceReset) bestOffset = offset;
        else bestOffset = Math.Min(bestOffset, offset);
        lastSource = sourceSeconds;
        lastReceipt = receiverMonotonicSeconds;
        HasSample = true;
        BacklogSeconds = Math.Max(0, offset - bestOffset);
        epochUsable = !sourceReset;
        if (sourceReset) return Observation.SourceReset;
        return BacklogSeconds > timeoutSeconds ? Observation.Backlogged : Observation.Accepted;
    }

    public double AgeSeconds(double receiverMonotonicSeconds)
    {
        if (!HasSample || !epochUsable || !Finite(receiverMonotonicSeconds)
            || receiverMonotonicSeconds < lastReceipt) return double.PositiveInfinity;
        return BacklogSeconds + receiverMonotonicSeconds - lastReceipt;
    }

    public bool IsFresh(double receiverMonotonicSeconds, double timeoutSeconds)
        => Finite(timeoutSeconds) && timeoutSeconds > 0 && AgeSeconds(receiverMonotonicSeconds) <= timeoutSeconds;

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
