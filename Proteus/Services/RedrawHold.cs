using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Holds the composites and redraws an auto refit batch causes itself until the batch is done, so an outfit change
/// redraws once at the end instead of once per slot. Measured on 2026-10-15 (build 1279): a three-slot batch cost a
/// redraw per "switched on" and an in-place reload per option switch, eight redraws and five reloads in all.
/// <para/>
/// Keyed: each batch holds under its own key (<see cref="Hold"/>), renewed per job, and releases it when it drains.
/// While any key is live, a trigger offered through <see cref="TryDefer"/> and a model redraw offered through
/// <see cref="TryAbsorbModelRedraw"/> are recorded instead of run; <see cref="Poll"/> hands them back, merged, once
/// the last key has gone. Every hold ends: a key whose lease runs out, a key past its cap, and a hold live longer than
/// the global cap are let go by <see cref="Poll"/> itself, marked forced.
/// <para/>
/// Pure: the clock is injected, one lock guards everything, and nothing is called out from under it. The decisions
/// the compositor and the watcher act on are the static members, so they are tested here and not in services that
/// cannot be built in a test.
/// </summary>
internal sealed class RedrawHold
{
    /// <summary>A hold live this long is let go however many keys keep it.</summary>
    internal const int GlobalCapMs = 120_000;

    /// <summary>How long the token handed to a release's composite stays good (see <see cref="IssueReleaseToken"/>).</summary>
    internal const int ReleaseTokenMs = 10_000;

    /// <summary>A trigger held for later: the arguments it is composited with when the hold ends.</summary>
    internal readonly record struct Deferred(string Reason, bool Force, bool SkinAuth, bool AutoRedrawExempt);

    /// <summary>One key as it ended.</summary>
    internal readonly record struct KeyReport(string Key, long HeldMs, bool TimedOut, bool Capped);

    /// <summary>
    /// What a hold kept back, handed out once when its last key has gone. <see cref="Force"/> is any trigger's,
    /// <see cref="SkinAuth"/> every forced one's (true when none was forced), <see cref="AutoRedrawExempt"/> any one's.
    /// </summary>
    internal sealed record Released(IReadOnlyList<string> Reasons, bool AnyDeferred, bool Force, bool SkinAuth,
        bool AutoRedrawExempt, IReadOnlyList<string> ModelRedrawCauses, bool MaterialsOwed,
        IReadOnlyList<KeyReport> Keys, long HeldMs)
    {
        /// <summary>A key timed out or hit a cap rather than being let go by its batch.</summary>
        public bool Forced => Keys.Any(k => k.TimedOut || k.Capped);
    }

    /// <summary>
    /// What to do with a release: redraw first when a model change is owed, then composite what waited. That composite
    /// is never held again: a batch starting during its debounce would otherwise hold back whatever composite it had
    /// already replaced (see <see cref="CompositorService.TriggerRecomposite"/>'s debounce gate).
    /// </summary>
    internal readonly record struct ReleasePlan(bool RedrawFirst, bool Composite);

    /// <summary>What <see cref="Admit"/> decided for a trigger.</summary>
    internal enum Admission { Run, Deferred, AutoRedrawOff }

    private sealed class Key
    {
        public long Acquired;
        public long Deadline;
        public int Cap;
        public bool Released;
    }

    private readonly Func<long> nowMs;
    private readonly object gate = new();
    private readonly Dictionary<string, Key> keys = new(StringComparer.Ordinal);
    // Keys whose lease ran out mid-batch, by first take: a later take keeps the same cap.
    private readonly Dictionary<string, long> lapsed = new(StringComparer.Ordinal);
    // Keys refused after their cap, until their batch lets them go.
    private readonly HashSet<string> refused = new(StringComparer.Ordinal);
    private readonly List<Deferred> deferred = [];
    private readonly List<string> causes = [];
    private readonly List<KeyReport> reports = [];
    private bool materialsOwed;
    private long spanStart = -1;
    private int token;
    private long tokenIssued;
    private bool tokenConsumed = true;

    internal RedrawHold(Func<long> nowMs) => this.nowMs = nowMs;

    /// <summary>Whether any key is live.</summary>
    internal bool IsHeld { get { lock (gate) return keys.Count > 0; } }

    /// <summary>
    /// Take or renew <paramref name="key"/> for <paramref name="leaseMs"/>. False once the key has reached its cap
    /// (<paramref name="capMs"/> from its first take) or the hold the global cap; it is refused until the batch lets
    /// it go with <see cref="Release"/>, so the batch's later work runs unheld, as before holds.
    /// </summary>
    internal bool Hold(string key, int leaseMs, int capMs)
    {
        lock (gate)
        {
            if (refused.Contains(key)) return false;
            long now = nowMs();
            if (!keys.TryGetValue(key, out var k))
            {
                long first = lapsed.TryGetValue(key, out long t) ? t : now;
                lapsed.Remove(key);
                if (now - first >= capMs) { refused.Add(key); return false; }
                if (keys.Count == 0 && spanStart < 0) spanStart = now;
                keys[key] = k = new Key { Acquired = first, Cap = capMs };
            }
            if (now - k.Acquired >= k.Cap) return false;   // Poll lets it go and records the cap
            k.Deadline = Math.Max(k.Deadline, now + leaseMs);
            k.Released = false;
            return true;
        }
    }

    /// <summary>
    /// Let <paramref name="key"/> go after <paramref name="graceMs"/>: its deadline only ever comes closer. Also lifts
    /// a refusal after its cap. False when the key was not live. Idempotent.
    /// </summary>
    internal bool Release(string key, int graceMs)
    {
        lock (gate)
        {
            refused.Remove(key);
            lapsed.Remove(key);
            if (!keys.TryGetValue(key, out var k)) return false;
            k.Deadline = Math.Min(k.Deadline, nowMs() + graceMs);
            k.Released = true;
            return true;
        }
    }

    /// <summary>When <see cref="Poll"/> next has something to do; <see cref="long.MaxValue"/> with no key live.</summary>
    internal long NextDueMs
    {
        get
        {
            lock (gate)
            {
                if (keys.Count == 0) return spanStart >= 0 ? 0 : long.MaxValue;
                long due = spanStart + GlobalCapMs;
                foreach (var k in keys.Values) due = Math.Min(due, Math.Min(k.Deadline, k.Acquired + k.Cap));
                return due;
            }
        }
    }

    /// <summary>Record a trigger while held. False with no key live: the caller runs it now.</summary>
    internal bool TryDefer(in Deferred d)
    {
        lock (gate)
        {
            if (keys.Count == 0) return false;
            deferred.Add(d);
            return true;
        }
    }

    /// <summary>Record a model redraw while held, owed at release. False with no key live: the caller redraws now.</summary>
    internal bool TryAbsorbModelRedraw(string cause)
    {
        lock (gate)
        {
            if (keys.Count == 0) return false;
            causes.Add(cause);
            return true;
        }
    }

    /// <summary>
    /// Record that the material snapshot went stale while held: applied at release, before its redraw, so a composite
    /// that runs meanwhile pays no reload the batch caused. False with no key live: the caller marks it now.
    /// </summary>
    internal bool TryOweMaterials()
    {
        lock (gate)
        {
            if (keys.Count == 0) return false;
            materialsOwed = true;
            return true;
        }
    }

    /// <summary>
    /// Let go the keys that are due — released, out of lease, past their cap, or all of them past the global cap — and,
    /// once none is left, hand back everything the hold kept, merged. Null while a key is live or nothing was held.
    /// </summary>
    internal Released? Poll()
    {
        lock (gate)
        {
            if (spanStart < 0) return null;
            long now = nowMs();
            bool global = now - spanStart >= GlobalCapMs;
            foreach (var (name, k) in keys.ToList())
            {
                // A key past its cap is let go however its lease stands, and refused until its batch lets it go.
                bool capped = global || !k.Released && now - k.Acquired >= k.Cap;
                if (!capped && now < k.Deadline) continue;
                keys.Remove(name);
                reports.Add(new KeyReport(name, now - k.Acquired, TimedOut: !k.Released && !capped, Capped: capped));
                if (capped && !k.Released) refused.Add(name);
                else if (!k.Released) lapsed[name] = k.Acquired;
            }
            if (keys.Count > 0) return null;

            var forced = deferred.Where(d => d.Force).ToList();
            var r = new Released(
                deferred.Select(d => d.Reason).Distinct().ToList(), deferred.Count > 0,
                deferred.Any(d => d.Force), forced.All(d => d.SkinAuth), deferred.Any(d => d.AutoRedrawExempt),
                causes.ToList(), materialsOwed,
                reports.ToList(), now - spanStart);
            deferred.Clear();
            causes.Clear();
            reports.Clear();
            materialsOwed = false;
            spanStart = -1;
            return r;
        }
    }

    /// <summary>
    /// A token for the one composite a release fires: it alone may skip the preamble's material reload, once, within
    /// <see cref="ReleaseTokenMs"/>. A newer release's token supersedes it.
    /// </summary>
    internal int IssueReleaseToken()
    {
        lock (gate)
        {
            token++;
            tokenIssued = nowMs();
            tokenConsumed = false;
            return token;
        }
    }

    /// <summary>True once for the latest token, unexpired; false for any other.</summary>
    internal bool ConsumeReleaseToken(int t)
    {
        lock (gate)
        {
            if (t == 0 || t != token || tokenConsumed || nowMs() - tokenIssued >= ReleaseTokenMs) return false;
            tokenConsumed = true;
            return true;
        }
    }

    // ── decisions ───────────────────────────────────────────────────────────

    /// <summary>
    /// What a trigger does, in the order that keeps a held trigger harmless: an ambient one with auto redraw off stops
    /// first; a forced one latches its force (which must not be lost) whether or not it is held; then it is held, or
    /// runs. The caller takes its debounce token only on <see cref="Admission.Run"/>, so a held trigger never cancels
    /// the composite in flight.
    /// </summary>
    internal static Admission Admit(bool force, bool autoRedraw, bool autoRedrawExempt, bool deferWhileHeld,
                                    Action latchForce, Func<bool> tryDefer)
    {
        if (!force && !autoRedraw && !autoRedrawExempt) return Admission.AutoRedrawOff;
        if (force) latchForce();
        return deferWhileHeld && tryDefer() ? Admission.Deferred : Admission.Run;
    }

    /// <summary>
    /// A release redraws first when a model change is owed, so the composite after it samples the refits; then
    /// composites what waited, if it can — unheld, forced release or not.
    /// </summary>
    internal static ReleasePlan PlanRelease(Released r, bool disposed, bool canComposite)
        => disposed ? default : new ReleasePlan(r.ModelRedrawCauses.Count > 0, r.AnyDeferred && canComposite);

    /// <summary>
    /// Whether to ask again for an owed redraw no redraw has answered: not while held or while a composite (whose
    /// publish pays it off) is under way, every 2 s, and only with the character drawn. <paramref name="asks"/> counts
    /// the release's own redraw: three asks in all, that one and two more.
    /// </summary>
    internal static bool OwedRedrawDue(bool owed, bool held, bool compositeBusy, long msSinceAsk, int asks,
                                       bool playerDrawn)
        => owed && !held && !compositeBusy && msSinceAsk >= 2000 && asks < 3 && playerDrawn;

    /// <summary>A publish redraws in full when the model changed, the in-place reload is off, or a redraw is owed.</summary>
    internal static bool FullRedrawFor(bool needFullRedraw, bool useInPlace, bool owed)
        => needFullRedraw || !useInPlace || owed;

    /// <summary>
    /// Whether a reload of the character may happen: one Proteus starts itself only with Auto redraw on; one the user
    /// asked for (switching Proteus off) always.
    /// </summary>
    internal static bool AutoRedrawAllows(bool autoRedraw, bool userRequested) => autoRedraw || userRequested;

    /// <summary>
    /// Whether a composite reloads the character before sampling, to load a body mod's materials: as before, except
    /// in a release's own composite once its redraw has been seen — that redraw already loaded them.
    /// </summary>
    internal static bool PreambleReload(bool wasDirty, bool autoRedraw, bool bodyKeyUnchanged, bool releaseTokenValid)
        => wasDirty && autoRedraw && bodyKeyUnchanged && !releaseTokenValid;

    /// <summary>A draining batch lets its key go once its own evaluations are done, or after <paramref name="capMs"/>.</summary>
    internal static bool ReleaseAfterWait(bool evaluationsPending, long waitedMs, int capMs)
        => !evaluationsPending || waitedMs >= capMs;
}

/// <summary>
/// The mods an auto refit batch is writing, each for a short window from its mark: a setting event on one within it
/// is the batch's own, and what it sets off is held with the batch. A player's change to the same mod inside the window
/// is held too — accepted: it is rare, and only waits for the batch's end. Thread-safe.
/// </summary>
internal sealed class OwnWriteMarks(Func<long> nowMs, int windowMs)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> until =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The batch is about to write <paramref name="modDir"/>.</summary>
    internal void Mark(string modDir) => until[modDir] = nowMs() + windowMs;

    /// <summary>Whether an event on <paramref name="modDir"/> now falls in a mark's window.</summary>
    internal bool IsMarked(string modDir) => until.TryGetValue(modDir, out long u) && nowMs() < u;
}
