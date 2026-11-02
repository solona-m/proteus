using System;
using System.Threading;

namespace Proteus.Services;

internal static partial class ShellBodyAtr
{
    // ── one forced rebuild per target ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bounds the forced rebuilds a shape change may cause. A target (a <see cref="Canonical"/> of what a rebuild should
    /// publish) is forced once; until a run started after that publishes, it is not forced again. A publish that draws
    /// something else, while the settings still say the target, means the build and the check disagree: warned once, and
    /// that target is never forced again — until a publish draws it after all. Without this, a redraw, Glamourer
    /// re-asserting its settings, and another forced rebuild could chase each other for ever. Thread-safe.
    /// </summary>
    internal sealed class ForceLatch
    {
        private readonly object gate = new();
        private long stamps;
        private string? target;
        private long armedStamp;
        private bool pending;
        private string? exhausted;

        /// <summary>A fresh, increasing stamp: a composite run takes one as it starts.</summary>
        internal long Stamp() => Interlocked.Increment(ref stamps);

        /// <summary>May a rebuild be forced for <paramref name="t"/>? True arms it.</summary>
        internal bool TryArm(string t)
        {
            lock (gate)
            {
                if (t == exhausted) return false;
                if (pending && t == target) return false;
                target = t;
                armedStamp = Stamp();
                pending = true;
                return true;
            }
        }

        /// <summary>A run published <paramref name="published"/>.</summary>
        /// <param name="freshTarget">What the settings say now; called only on a disagreement, outside the lock.</param>
        internal void OnPublished(string published, long runStamp, Func<string?> freshTarget, Action<string> warn,
                                  Action<string>? debug = null)
        {
            string armed;
            long armedAt;
            lock (gate)
            {
                if (exhausted != null && published == exhausted)
                {
                    exhausted = null;
                    debug?.Invoke($"shell caught up with [{published}] — it may be forced again");
                }
                if (target == null || runStamp < armedStamp) return;
                if (published == target)
                {
                    target = null;
                    pending = false;
                    return;
                }
                armed = target;
                armedAt = armedStamp;
            }

            string? fresh;
            try { fresh = freshTarget(); }
            catch (Exception) { fresh = null; }

            bool disagree;
            lock (gate)
            {
                if (target != armed || armedStamp != armedAt) return;   // armed again meanwhile: that arm stands
                disagree = fresh == armed;
                if (disagree) exhausted = armed;
                target = null;
                pending = false;
            }
            if (disagree)
                warn($"forced a rebuild for the body's switched shapes [{armed}] but the build drew [{published}]; the "
                     + "check and the build disagree — not forcing again for these settings");
            else
                debug?.Invoke($"the settings moved again since [{armed}] was forced — letting the next check decide");
        }

        /// <summary>A run ended; one that did not publish releases a pending target so it may be forced again.</summary>
        internal void OnRunEnded(long runStamp, bool published, Action<string>? debug = null)
        {
            lock (gate)
            {
                if (published || !pending || runStamp < armedStamp) return;
                pending = false;
            }
            debug?.Invoke("a forced run ended without publishing — the target may be forced again");
        }

        /// <summary>
        /// A deliberate settings change on a watched mod: whatever was given up on is worth one more try. A transient read
        /// (a re-apply caught half-done) must not leave the shell stuck on the wrong shape for the session.
        /// </summary>
        internal void ClearExhausted()
        {
            lock (gate) exhausted = null;
        }

        /// <summary>Has <paramref name="t"/> been given up on?</summary>
        internal bool IsExhausted(string t)
        {
            lock (gate) return t == exhausted;
        }
    }
}
