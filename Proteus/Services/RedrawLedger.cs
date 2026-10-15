using System;

namespace Proteus.Services;

/// <summary>
/// The dalamud.log lines that count redraws: one per redraw or in-place reload Proteus asks for, with what caused it,
/// and one per redraw the character goes through, saying whether it was Proteus's. An outfit change can then be read
/// back as "these redraws, for these reasons", and a redraw nobody in Proteus asked for shows as not ours.
/// <para/>
/// Lines carry the cause strings and counters only, never a character's name (see LogAnonymizer). Thread-safe:
/// redraws are asked for from the framework thread and from workers.
/// </summary>
internal sealed class RedrawLedger
{
    /// <summary>How long after we ask for a redraw the character's own redraw event still counts as its answer.</summary>
    internal const long EchoWindowMs = 2000;

    private readonly Func<long> nowMs;
    private readonly object gate = new();
    private int redraws, reloads;

    // The latest redraw of ours that reached Penumbra, until a character redraw is matched to it.
    private int lastNumber;
    private long lastAt = -1;
    private bool lastMatched;

    internal RedrawLedger(Func<long> nowMs) => this.nowMs = nowMs;

    /// <summary>A full redraw Proteus asked for. <paramref name="reached"/> is false when Penumbra refused it.</summary>
    internal string Redrew(string cause, bool reached)
    {
        lock (gate)
        {
            int n = ++redraws;
            if (!reached) return $"[Proteus] redraw #{n}: {cause} (not drawn: Penumbra refused it)";
            lastNumber = n;
            lastAt = nowMs();
            lastMatched = false;
            return $"[Proteus] redraw #{n}: {cause}";
        }
    }

    /// <summary>A reload through Glamourer, in place: no redraw event follows it.</summary>
    internal string Reloaded(string cause)
    {
        lock (gate) return $"[Proteus] reload #{++reloads} (in place): {cause}";
    }

    /// <summary>The character was redrawn, by whoever: ours when it follows our latest redraw within the window, once.</summary>
    internal string Observed()
    {
        lock (gate)
        {
            if (lastAt < 0) return "[Proteus] character redrawn (not Proteus's — none of ours yet)";
            long since = nowMs() - lastAt;
            if (!lastMatched && since >= 0 && since < EchoWindowMs)
            {
                lastMatched = true;
                return $"[Proteus] character redrawn (ours: redraw #{lastNumber}, {since} ms after it was asked for)";
            }
            return $"[Proteus] character redrawn (not Proteus's — {since} ms since our last redraw)";
        }
    }
}
