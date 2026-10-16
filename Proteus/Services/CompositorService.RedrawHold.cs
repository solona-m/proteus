using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>
/// The compositor's side of <see cref="RedrawHold"/>: an auto refit batch holds the composites and redraws its own
/// writes cause, and the frame poll here lets them go as one redraw and one composite when the batch is done. The
/// decisions are <see cref="RedrawHold"/>'s; this is the glue.
/// </summary>
public partial class CompositorService
{
    /// <summary>How long after the auto refit marks a mod its setting events count as the batch's own.</summary>
    internal const int OwnRefitWriteMs = 2000;

    /// <summary>How long after a batch let go its summary is written, so the release's redraw, composite and any
    /// correction are counted.</summary>
    private const int BatchSummaryTailMs = 10_000;

    /// <summary>A re-check from a batch's own write landing this soon after the release is logged as late.</summary>
    private const int LateRecheckMs = 5000;

    private readonly RedrawHold redrawHold = new(() => Environment.TickCount64);

    // Mods the auto refit is writing: their setting events are the batch's own (ExpectOwnRefitWrite).
    private readonly OwnWriteMarks _ownRefitWrites = new(() => Environment.TickCount64, OwnRefitWriteMs);

    // Body-mod evaluations under way, by mod: a draining batch waits for its own (BodyEvaluationPending).
    private readonly ConcurrentDictionary<string, int> _evaluatingMods = new(StringComparer.OrdinalIgnoreCase);

    // A redraw a release asked for and no character redraw has answered yet (1/0), since when, and how often asked.
    private int _redrawOwed;
    private long _owedSince;
    private int _owedAsks;
    private long _owedLastAsk;
    private bool _owedGaveUp;

    // When the last release asked for its redraw (0 for none), and when a character redraw was last seen.
    private long _releaseRedrawTick;
    private long _lastObservedRedrawTick;

    // Debounced composites not yet finished: a pending one will pay off an owed redraw with its publish.
    private int _recompositesPending;

    private long _lastHoldReleaseTick;
    private long _batchSummaryDue;

    /// <summary>
    /// Hold the composites and redraws <paramref name="key"/>'s batch causes, for <paramref name="leaseMs"/> (renew to
    /// keep it) and at most <paramref name="capMs"/> from the first take. False when refused after its cap: the batch's
    /// later work then runs as it did before holds. Any thread.
    /// </summary>
    public bool HoldRedraws(string key, int leaseMs, int capMs)
    {
        bool fresh = !redrawHold.IsHeld;
        bool ok = redrawHold.Hold(key, leaseMs, capMs);
        if (ok && fresh)
        {
            var line = redrawLedger.HoldStarted(key, out var previous);
            // The last batch's summary, ended by this one: its tail is not waited out.
            if (previous != null) log.Information("{0}", previous);
            log.Information("{0}", line);
        }
        return ok;
    }

    /// <summary>Let <paramref name="key"/>'s hold go after <paramref name="graceMs"/>. Idempotent; any thread.</summary>
    public void ReleaseRedraws(string key, int graceMs) => redrawHold.Release(key, graceMs);

    /// <summary>
    /// The auto refit is about to write <paramref name="modDir"/>'s settings or register it: the setting events that
    /// follow, and the composites they set off, are the batch's own and are held with it. Any thread.
    /// </summary>
    public void ExpectOwnRefitWrite(string modDir) => _ownRefitWrites.Mark(modDir);

    /// <summary>Whether a setting event on <paramref name="modDir"/> now is one of the auto refit's own writes.</summary>
    private bool IsOwnRefitWrite(string modDir) => _ownRefitWrites.IsMarked(modDir);

    /// <summary>Whether a body-mod evaluation or a moved-base re-check is still running for any of <paramref name="modDirs"/>.</summary>
    public bool BodyEvaluationPending(IEnumerable<string> modDirs)
        => modDirs.Any(d => _evaluatingMods.ContainsKey(d) || _pendingMoveRecheck.ContainsKey(d));

    /// <summary>
    /// The material snapshot went stale. Held by an auto refit batch when the batch's own write caused it, and applied
    /// when the batch lets go, so a composite meanwhile pays no material reload the batch caused.
    /// </summary>
    private void MarkMaterialsDirty(bool owedWhileHeld)
    {
        if (owedWhileHeld && redrawHold.TryOweMaterials()) return;
        _activeMtrlSnapshotDirty = true;
    }

    private void NoteEvaluating(string modDir, int delta)
    {
        if (delta > 0) _evaluatingMods.AddOrUpdate(modDir, 1, (_, n) => n + 1);
        else if (_evaluatingMods.AddOrUpdate(modDir, 0, (_, n) => n - 1) <= 0)
            _evaluatingMods.TryRemove(new KeyValuePair<string, int>(modDir, 0));
    }

    /// <summary>A character redraw: it answers any redraw a release owed.</summary>
    private void NoteRedrawObserved()
    {
        long now = Environment.TickCount64;
        Interlocked.Exchange(ref _lastObservedRedrawTick, now);
        if (Volatile.Read(ref _redrawOwed) == 1 && now >= Interlocked.Read(ref _owedSince))
            Interlocked.Exchange(ref _redrawOwed, 0);
    }

    /// <summary>
    /// Whether the release's composite may skip its material reload: its token is good and the release's redraw has
    /// been seen, which already loaded them.
    /// </summary>
    private bool ReleaseTokenValid(int releaseToken)
    {
        if (releaseToken == 0 || !redrawHold.ConsumeReleaseToken(releaseToken)) return false;
        long asked = Interlocked.Read(ref _releaseRedrawTick);
        return asked != 0 && Interlocked.Read(ref _lastObservedRedrawTick) >= asked;
    }

    /// <summary>Framework thread, every frame: ask again for an owed redraw, write a batch's summary, and let a hold go.</summary>
    private void OnRedrawHoldPoll(IFramework _)
    {
        if (_disposed) return;
        long now = Environment.TickCount64;

        WatchOwedRedraw(now);

        if (_batchSummaryDue != 0 && now >= _batchSummaryDue)
        {
            _batchSummaryDue = 0;
            if (redrawLedger.BatchSummary() is { } summary) log.Information("{0}", summary);
        }

        if (now < redrawHold.NextDueMs || redrawHold.Poll() is not { } r) return;

        if (r.Forced) log.Warning("{0}", redrawLedger.HoldReleased(r));
        else log.Information("{0}", redrawLedger.HoldReleased(r));
        Interlocked.Exchange(ref _lastHoldReleaseTick, now);
        _batchSummaryDue = now + BatchSummaryTailMs;

        if (r.MaterialsOwed) _activeMtrlSnapshotDirty = true;

        var plan = RedrawHold.PlanRelease(r, _disposed, config.PluginEnabled && penumbra.IsAvailable);
        Interlocked.Exchange(ref _releaseRedrawTick, 0);
        if (plan.RedrawFirst)
        {
            // Owed until a character redraw is seen: Penumbra accepting the request is not that.
            Interlocked.Exchange(ref _owedSince, now);
            _owedAsks = 1;
            _owedLastAsk = now;
            _owedGaveUp = false;
            Interlocked.Exchange(ref _redrawOwed, 1);
            Interlocked.Exchange(ref _releaseRedrawTick, now);
            RedrawPlayerLogged("auto refit batch: " + string.Join(", ", r.ModelRedrawCauses), penumbra.RedrawPlayer);
        }
        // Unheld (see RedrawHold.ReleasePlan): nothing it replaces in the debounce gate may wait on a later batch.
        if (plan.Composite)
            TriggerRecomposite("held: " + string.Join(",", r.Reasons), 200, r.Force, r.SkinAuth, r.AutoRedrawExempt,
                               releaseToken: redrawHold.IssueReleaseToken());
    }

    /// <summary>
    /// An owed redraw nothing has answered: asked again while nothing else will pay it off (no hold, no composite whose
    /// publish would), at most three times; after that a publish while it is owed is a full redraw.
    /// </summary>
    private void WatchOwedRedraw(long now)
    {
        if (Volatile.Read(ref _redrawOwed) == 0) return;
        bool busy = Volatile.Read(ref _recompositesPending) > 0 || Volatile.Read(ref _compositesInFlight) > 0;
        if (RedrawHold.OwedRedrawDue(true, redrawHold.IsHeld, busy, now - _owedLastAsk, _owedAsks,
                                     Plugin.ObjectTable.LocalPlayer != null))
        {
            _owedAsks++;
            _owedLastAsk = now;
            RedrawPlayerLogged("auto refit batch: no redraw seen yet, asking again", penumbra.RedrawPlayer);
        }
        else if (_owedAsks >= 3 && !_owedGaveUp && now - _owedLastAsk >= 2000)
        {
            _owedGaveUp = true;
            log.Warning("[Proteus] auto refit batch: no redraw seen after {0} asks — the next composite redraws in full",
                        _owedAsks);
        }
    }

    /// <summary>Runs <see cref="Action"/> once on dispose: counts a debounced composite out however its task ends.</summary>
    private sealed class OnExit(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
