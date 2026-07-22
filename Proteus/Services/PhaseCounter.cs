using System.Diagnostics;
using System.Threading;

namespace Proteus.Services;

/// <summary>
/// Thread-safe accumulator for one phase of a recomposite: elapsed time, call count and an optional
/// byte total. Instrumentation only — <see cref="CompositorService"/> resets every counter at the
/// start of a run and logs the totals at the end, so a slow recomposite can be attributed to a
/// specific stage (decode / remap / blend / write / reload) instead of guessed at.
/// </summary>
public sealed class PhaseCounter
{
    private long ticks;
    private long calls;
    private long bytes;

    /// <summary>Timestamp to hand back to <see cref="Stop"/>. No allocation, unlike a Stopwatch instance.</summary>
    public static long Begin() => Stopwatch.GetTimestamp();

    /// <summary>Milliseconds elapsed since a <see cref="Begin"/> timestamp, for one-off spans.</summary>
    public static double MsSince(long start)
        => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Record one completed call that started at <paramref name="start"/>.</summary>
    public void Stop(long start, long byteCount = 0)
    {
        Interlocked.Add(ref ticks, Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref calls);
        if (byteCount != 0) Interlocked.Add(ref bytes, byteCount);
    }

    /// <summary>Record an occurrence with no duration (cache hits, skipped work).</summary>
    public void Count() => Interlocked.Increment(ref calls);

    public void Reset()
    {
        Interlocked.Exchange(ref ticks, 0);
        Interlocked.Exchange(ref calls, 0);
        Interlocked.Exchange(ref bytes, 0);
    }

    public double Ms    => Interlocked.Read(ref ticks) * 1000.0 / Stopwatch.Frequency;
    public long   Calls => Interlocked.Read(ref calls);
    public long   Bytes => Interlocked.Read(ref bytes);
}
