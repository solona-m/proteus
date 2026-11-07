using System;
using System.Collections.Generic;

namespace Proteus.Services;

/// <summary>
/// The retry latch for output writes that could not be put in place (#91). A publish that dropped a layer must never be
/// recorded as the published output for its inputs — or the next composite is "skipped — inputs unchanged" and the layer
/// stays gone — but an unbounded retry would loop, since every publish reloads and redraws and that redraw triggers more
/// composites. So: at most <see cref="PerFingerprint"/> failing publishes per set of inputs, retried after 1.5 / 4 / 10 s,
/// and at most <see cref="GlobalCap"/> scheduled retry timers per rolling minute across all inputs. Then it gives up, says
/// so once, and goes back to skipping unchanged inputs. Thread-safe; the clock is passed in.
/// </summary>
internal sealed class OutputWriteRetry
{
    internal enum Kind { Clean, Retry, GiveUp }

    /// <summary>What to do after a publish: <see cref="DelayMs"/> and <see cref="Attempt"/> for a retry; <see cref="Warn"/>
    /// when a give-up should be said out loud (once per set of inputs).</summary>
    internal readonly record struct Decision(Kind Kind, int DelayMs, int Attempt, bool Warn);

    internal const int PerFingerprint = 3;
    internal const int GlobalCap = 3;
    internal const long WindowMs = 60_000;
    internal static readonly int[] Delays = [1500, 4000, 10000];

    private readonly object gate = new();
    private string? fingerprint;
    private int attempts;
    private string? warnedFingerprint;
    private readonly Queue<long> scheduled = new();

    /// <summary>The last decision, for the harness's /state.</summary>
    internal Kind? LastKind { get; private set; }
    internal int LastAttempt { get; private set; }

    /// <summary>
    /// Records a publish of <paramref name="fp"/> with the given write failures. <paramref name="willSchedule"/>: whether a
    /// Retry would actually schedule a timer (auto redraw on) — only then does it take a slot of the global cap.
    /// </summary>
    internal Decision OnPublished(string fp, int shellFailures, int skinFailures, long nowMs, bool willSchedule)
    {
        lock (gate)
        {
            if (shellFailures + skinFailures <= 0)
            {
                fingerprint = null;
                attempts = 0;
                return Remember(new Decision(Kind.Clean, 0, 0, false));
            }

            if (!string.Equals(fingerprint, fp, StringComparison.Ordinal))
            {
                fingerprint = fp;
                attempts = 0;
            }
            int attempt = ++attempts;
            Expire(nowMs);
            if (attempt <= PerFingerprint && (!willSchedule || scheduled.Count < GlobalCap))
            {
                if (willSchedule) scheduled.Enqueue(nowMs);
                return Remember(new Decision(Kind.Retry, Delays[attempt - 1], attempt, false));
            }

            // Once per set of inputs (AC-11): another set's give-up within the minute is still named.
            bool warn = !string.Equals(warnedFingerprint, fp, StringComparison.Ordinal);
            if (warn) warnedFingerprint = fp;
            return Remember(new Decision(Kind.GiveUp, 0, attempt, warn));
        }
    }

    /// <summary>How many retry timers were scheduled in the minute before <paramref name="nowMs"/>.</summary>
    internal int ScheduledInLast60s(long nowMs)
    {
        lock (gate)
        {
            Expire(nowMs);
            return scheduled.Count;
        }
    }

    private void Expire(long nowMs)
    {
        while (scheduled.Count > 0 && nowMs - scheduled.Peek() >= WindowMs) scheduled.Dequeue();
    }

    private Decision Remember(Decision d)
    {
        LastKind = d.Kind;
        LastAttempt = d.Attempt;
        return d;
    }

    /// <summary>
    /// What the publish records (#91): the fingerprint the skip gate compares against (null = never skip), whether the
    /// shell output is incomplete (blocks the skip while keeping skin reuse), and whether the skin publish may be reused.
    /// A shell-only retry keeps the fingerprint, so the next composite reuses the skin but cannot skip; a skin failure
    /// keeps nothing. A give-up records the inputs as published (no loop), dropping the skin publish if the skin failed.
    /// </summary>
    internal static (string? Fingerprint, bool OutputIncomplete, bool KeepSkinPublish) Record(string fp, Decision d, bool skinFailed)
        => d.Kind switch
        {
            Kind.Clean => (fp, false, true),
            Kind.Retry => skinFailed ? (null, false, false) : (fp, true, true),
            _ => (fp, false, !skinFailed),
        };

    /// <summary>When a scheduled retry timer, once its delay is up, triggers its composite.</summary>
    internal static class Fire
    {
        internal enum Step { Wait, Trigger, Drop }

        internal const long MaxWaitMs = 30_000;

        /// <summary>
        /// Drop when disposed or another publish has run since the retry was scheduled (that publish ran the latch);
        /// wait while a composite is pending or redraws are held (dropping after <see cref="MaxWaitMs"/> — safe, since
        /// the gate cannot skip while the output is incomplete); drop when auto redraw is off; otherwise trigger.
        /// </summary>
        internal static Step Decide(bool pending, bool held, long stampNow, long stampAtSchedule, bool autoRedraw,
                                    bool disposed, long waitedMs)
        {
            if (disposed || stampNow != stampAtSchedule) return Step.Drop;
            if (pending || held) return waitedMs >= MaxWaitMs ? Step.Drop : Step.Wait;
            if (!autoRedraw) return Step.Drop;
            return Step.Trigger;
        }
    }
}
