using System;

namespace Proteus.Services;

/// <summary>
/// The "blend" figure of the recomposite phases line is what is left of the wall time after the stage counters. Those
/// counters add up parallel workers and are shared, so they can exceed the wall time (a user's log showed blend -791ms).
/// </summary>
internal static class PhaseResidual
{
    public static (long BlendMs, bool Overlapped) Blend(long compositeMs, long stagesMs)
    {
        long residual = compositeMs - stagesMs;
        return residual < 0 ? (0, true) : (residual, false);
    }
}

/// <summary>The GC pause time over a span, read from an injected clock (default <see cref="GC.GetTotalPauseDuration"/>).</summary>
internal sealed class GcPauseProbe
{
    private readonly Func<TimeSpan> clock;
    private readonly TimeSpan start;

    public GcPauseProbe(Func<TimeSpan>? clock = null)
    {
        this.clock = clock ?? GC.GetTotalPauseDuration;
        start = this.clock();
    }

    public long ElapsedPauseMs => (long)Math.Max(0, (clock() - start).TotalMilliseconds);
}
