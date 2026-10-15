using System;

namespace Proteus.Services;

/// <summary>
/// The dalamud.log lines that count redraws: one per redraw or in-place reload Proteus asks for, with what caused it,
/// and one per redraw the character goes through, saying whether it was Proteus's. An outfit change can then be read
/// back as "these redraws, for these reasons", and a redraw nobody in Proteus asked for shows as not ours.
/// <para/>
/// A character redraw is matched to the LATEST thing of ours that could have caused it, once, within
/// <see cref="EchoWindowMs"/>. Two of ours asked for before the first's redraw arrives therefore read as one ours and
/// one not: rare (Proteus's own redraws come seconds apart), and the other way round — matching each in order — would
/// let a pair Penumbra merged into one redraw claim someone else's next.
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

    // The latest thing of ours that may redraw the character, until a character redraw is matched to it; and the one
    // before it, for a refusal to fall back to. Label is "redraw #N", or the cause of an indirect one.
    private (int Number, string Label, long At, bool Matched) last = (0, "", -1, false), previous = (0, "", -1, false);

    internal RedrawLedger(Func<long> nowMs) => this.nowMs = nowMs;

    /// <summary>
    /// A full redraw Proteus is about to ask for: numbered and timed BEFORE the call, since the character's redraw event
    /// can arrive before the call returns. Follow with <see cref="Answered"/>.
    /// </summary>
    internal int Asking()
    {
        lock (gate)
        {
            int n = ++redraws;
            previous = last;
            last = (n, $"redraw #{n}", nowMs(), false);
            return n;
        }
    }

    /// <summary>The line for redraw <paramref name="n"/>. <paramref name="reached"/> is false when Penumbra refused it,
    /// which takes back its claim on the character's next redraw.</summary>
    internal string Answered(int n, string cause, bool reached)
    {
        lock (gate)
        {
            if (reached) return $"[Proteus] redraw #{n}: {cause}";
            if (last.Number == n) last = previous;
            return $"[Proteus] redraw #{n}: {cause} (not drawn: Penumbra refused it)";
        }
    }

    /// <summary><see cref="Asking"/> and <see cref="Answered"/> together, for a redraw already done.</summary>
    internal string Redrew(string cause, bool reached) => Answered(Asking(), cause, reached);

    /// <summary>
    /// Something Proteus asks another plugin to do that may redraw the character (Glamourer equipping a carrier, a design
    /// restore taking a slot off). A redraw that follows within the window is counted as ours, under
    /// <paramref name="cause"/>. Asked before the call, for the same reason as <see cref="Asking"/>.
    /// </summary>
    internal string Expecting(string cause)
    {
        lock (gate)
        {
            previous = last;
            last = (0, cause, nowMs(), false);
            return $"[Proteus] asked: {cause} (may redraw the character)";
        }
    }

    /// <summary>A reload through Glamourer, in place: no redraw event follows it.</summary>
    internal string Reloaded(string cause)
    {
        lock (gate) return $"[Proteus] reload #{++reloads} (in place): {cause}";
    }

    /// <summary>The character was redrawn, by whoever: ours when it follows our latest within the window, once.</summary>
    internal string Observed()
    {
        lock (gate)
        {
            if (last.At < 0) return "[Proteus] character redrawn (not Proteus's — none of ours yet)";
            long since = nowMs() - last.At;
            if (!last.Matched && since >= 0 && since < EchoWindowMs)
            {
                last.Matched = true;
                return $"[Proteus] character redrawn (ours: {last.Label}, {since} ms after it was asked for)";
            }
            return $"[Proteus] character redrawn (not Proteus's — {since} ms since our last redraw)";
        }
    }
}
