using System.Collections.Generic;
using System.Text;

namespace Proteus.Services;

/// <summary>
/// One refresh as the user waits through it: from the FIRST trigger of a burst to the character drawing
/// the result. Instrumentation only.
/// <para/>
/// The <c>recomposite phases</c> lines each cover one stage from START, and START is already seconds in:
/// a colour drag waits out its debounce, every trigger waits for the draw object to settle, and after the
/// skin blend come the shell build, the publish and the reload. Measured on a colour edit, the phases
/// lines accounted for 1.5 s of a 10.3 s refresh. This is the line that sums the whole thing.
/// <para/>
/// Spans are consecutive: each one runs from the end of the previous span, so they add up to the total
/// and nothing between two marks can go unattributed.
/// </summary>
internal sealed class RefreshTimeline
{
    private readonly List<(string Name, double Ms)> spans = [];
    private long mark;

    /// <summary>Timestamp of the earliest trigger this refresh serves.</summary>
    public long Start { get; private set; }
    public int Triggers { get; private set; }
    public string Reasons { get; private set; }

    /// <summary>Time charged from runs this one superseded, which started before its own first trigger.
    /// Shown as the first span, so the spans still add up to the total.</summary>
    private double supersededMs;

    /// <summary>
    /// Take on a run that was superseded while this one was already in flight. Its triggers were never
    /// answered by anything but this run, so the wait they started belongs here.
    /// </summary>
    public void Absorb(RefreshTimeline earlier)
    {
        lock (spans)
        {
            if (earlier.Start < Start)
            {
                supersededMs += (Start - earlier.Start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Start = earlier.Start;
            }
            Triggers += earlier.Triggers;
            foreach (var r in earlier.Reasons.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
                if (!("," + Reasons + ",").Contains("," + r + ",")) Reasons = Reasons.Length == 0 ? r : Reasons + "," + r;
        }
    }

    /// <summary>Set once the run has an outcome (published, skipped or failed). A run that ends without
    /// one — cancelled or superseded — hands its start back so the run replacing it is charged for the
    /// whole wait.</summary>
    public bool Completed { get; set; }

    public RefreshTimeline(long start, int triggers, string reasons)
    {
        Start = start;
        Triggers = triggers;
        Reasons = reasons;
        mark = start;
    }

    /// <summary>Close the span that began at the previous mark (or the first trigger).</summary>
    public void Mark(string name)
    {
        var now = PhaseCounter.Begin();
        lock (spans)
        {
            spans.Add((name, (now - mark) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
            mark = now;
        }
    }

    /// <summary>Close a span that ended at a timestamp already taken, e.g. the moment the last trigger
    /// arrived. A timestamp before the current mark closes a zero-length span rather than a negative one.</summary>
    public void MarkAt(string name, long at)
    {
        lock (spans)
        {
            if (at < mark) at = mark;
            spans.Add((name, (at - mark) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
            mark = at;
        }
    }

    public double TotalMs => PhaseCounter.MsSince(Start);

    /// <summary>"burst 133 | debounce 1128 | …" — spans under 1 ms are left out so the line stays readable.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        lock (spans)
        {
            if (supersededMs >= 1) sb.Append("superseded run(s) ").Append(supersededMs.ToString("F0"));
            foreach (var (name, ms) in spans)
            {
                if (ms < 1) continue;
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(name).Append(' ').Append(ms.ToString("F0"));
            }
        }
        return sb.Length == 0 ? "-" : sb.ToString();
    }
}
