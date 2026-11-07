using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Text.SeStringHandling;

namespace Proteus.Services;

/// <summary>
/// Recovery from a shell that did not make it onto the character (#91 and the owner's report on its message):
/// <list type="bullet">
/// <item>an output file that could not be put in place is never recorded as published — a retry composite runs on its
/// own timer, bounded by <see cref="OutputWriteRetry"/>, and a give-up names the accessory once;</item>
/// <item>a published shell the game does not draw gets one automatic full redraw, then one message naming the accessory
/// (<see cref="ShellNotDrawnNotice"/>).</item>
/// </list>
/// </summary>
public partial class CompositorService
{
    /// <summary>The retry latch for output writes (#91).</summary>
    private readonly OutputWriteRetry _writeRetry = new();

    /// <summary>The last publish dropped a shell layer whose write failed and a retry is owed: the unchanged-inputs gate
    /// must not skip, though the skin publish may still be reused (see <see cref="OutputWriteRetry.Record"/>).</summary>
    private volatile bool _shellOutputIncomplete;

    /// <summary>Bumped by every publish, so a retry timer can tell another publish has run since it was scheduled.</summary>
    private long _publishStamp;

    /// <summary>Drawn checks and re-checks still to finish, and write-retry timers still to fire (Copy Logs waits on both).</summary>
    private readonly InFlightCounter _shellChecksInFlight = new();
    private readonly InFlightCounter _writeRetryTimers = new();

    /// <summary>The shell (probe key) Proteus already redrew the character for once, and the one already told about.</summary>
    private volatile string? _shellAutoRedrawKey;
    private volatile string? _shellNotDrawnNoticeKey;

    /// <summary>When the automatic shell redraws happened (TickCount64), for the 2-per-5-minutes limit.</summary>
    private readonly Queue<long> _ownShellRedrawTicks = new();

    /// <summary>The last step the drawn check took about an undrawn shell, for the harness's /state.</summary>
    private volatile string? _shellDrawnLastStep;

    private Func<int, int, string?>? _itemNames;
    private Func<int, string?>? _facewearNames;

    /// <summary>
    /// Whether Proteus redraws on its own: the setting, or the test harness's session-only override (ADR 0002). Every
    /// compositor read goes through here; the shader-upgrade and variant watchers keep reading the setting.
    /// </summary>
    internal bool AutoRedrawOn => HarnessAutoRedrawOverride ?? config.AutoRedraw;

#if !PROTEUS_TEST_HARNESS
    /// <summary>No harness in this build: never overridden.</summary>
    private static bool? HarnessAutoRedrawOverride => null;
#endif

    /// <summary>Something Copy Logs should wait for before it reads the log: a composite pending or running, a
    /// write-retry timer, or a drawn check still to finish.</summary>
    internal bool DiagnosticsPending
        => CompositeBusy || _writeRetryTimers.Count > 0 || _shellChecksInFlight.Count > 0;

    /// <summary>Automatic shell redraws in the last five minutes.</summary>
    private int AutoRedrawsInWindow()
    {
        long now = Environment.TickCount64;
        lock (_ownShellRedrawTicks)
        {
            while (_ownShellRedrawTicks.Count > 0 && now - _ownShellRedrawTicks.Peek() >= ShellNotDrawnNotice.AutoRedrawWindowMs)
                _ownShellRedrawTicks.Dequeue();
            return _ownShellRedrawTicks.Count;
        }
    }

    private void NoteAutoRedraw()
    {
        lock (_ownShellRedrawTicks) _ownShellRedrawTicks.Enqueue(Environment.TickCount64);
    }

    /// <summary>
    /// Applies the latch to a publish (#91), from <c>CompositeRun.Publish</c>: what the skip gate and skin reuse may rely
    /// on, and the retry timer or the give-up message. Returns the decision for the log.
    /// </summary>
    private OutputWriteRetry.Decision RecordPublishOutcome(string fingerprint, int shellFailures, int skinFailures,
                                                          IReadOnlyList<string> failedHosts, long stamp,
                                                          Action<bool> keepSkinPublish)
    {
        bool autoRedraw = AutoRedrawOn;
        var d = _writeRetry.OnPublished(fingerprint, shellFailures, skinFailures, Environment.TickCount64, willSchedule: autoRedraw);
        var (fp, incomplete, keepSkin) = OutputWriteRetry.Record(fingerprint, d, skinFailed: skinFailures > 0);
        _lastCompositeFingerprint = fp;
        _shellOutputIncomplete = incomplete;
        keepSkinPublish(keepSkin);

        int failures = shellFailures + skinFailures;
        switch (d.Kind)
        {
            case OutputWriteRetry.Kind.Retry when autoRedraw:
                log.Information("[Proteus] output write failed for {0} file(s) — retrying in {1}ms (attempt {2}/{3})",
                    failures, d.DelayMs, d.Attempt, OutputWriteRetry.PerFingerprint);
                ScheduleWriteRetry(d.DelayMs, stamp);
                break;
            case OutputWriteRetry.Kind.Retry:
                log.Information("[Proteus] output write failed for {0} file(s) — auto redraw is off, so not retrying; the "
                              + "next recomposite rebuilds it", failures);
                break;
            case OutputWriteRetry.Kind.GiveUp:
                if (!d.Warn)
                {
                    log.Information("[Proteus] output write failed for {0} file(s) again — retries already given up for "
                                  + "these inputs", failures);
                    break;
                }
                log.Warning("[Proteus] output write failed for {0} file(s) after {1} attempt(s) — giving up until the "
                          + "inputs change. The files are held by something else (the game, a sync tool, an antivirus)",
                    failures, d.Attempt);
                if (failedHosts.Count > 0) PrintShellNotice(failedHosts, partly: true);
                break;
        }
        return d;
    }

    /// <summary>
    /// After <paramref name="delayMs"/>, one retry composite for a failed write (#91) — on its own timer, never taking the
    /// debounce token of a trigger already pending: it waits while a composite is pending or redraws are held, and does
    /// nothing if another publish has run since (that publish ran the latch).
    /// </summary>
    private void ScheduleWriteRetry(int delayMs, long stamp)
    {
        long waited = 0;
        _ = InFlight.ScheduleAsync(_writeRetryTimers,
            () => Task.Delay(delayMs),
            () => true,
            async () =>
            {
                while (true)
                {
                    var step = OutputWriteRetry.Fire.Decide(Volatile.Read(ref _recompositesPending) > 0, redrawHold.IsHeld,
                        Interlocked.Read(ref _publishStamp), stamp, AutoRedrawOn, _disposed, waited);
                    if (step == OutputWriteRetry.Fire.Step.Drop) return;
                    if (step == OutputWriteRetry.Fire.Step.Trigger)
                    {
                        TriggerRecomposite("output-write-retry", force: false, deferWhileHeld: true, keepHighlight: true);
                        return;
                    }
                    await Task.Delay(250).ConfigureAwait(false);
                    waited += 250;
                }
            },
            ex => log.Debug("[Proteus] output write retry timer failed: {0}", ex.Message));
    }

    /// <summary>The host models the missing materials ride on (all the probe's models when none can be told).</summary>
    private static List<string> HostsOf(ShellDrawnProbe expected, IEnumerable<string> missing)
    {
        var hosts = expected.Models.Where(m => missing.Any(p => SameShellHost(p, m))).ToList();
        return hosts.Count > 0 ? hosts : expected.Models.ToList();
    }

    /// <summary>
    /// One chat line per wording present among <paramref name="hostModels"/>: Proteus's own carriers, and accessories the
    /// wearer put on. Callers own the once-key. No hosts, no chat.
    /// </summary>
    private void PrintShellNotice(IReadOnlyList<string> hostModels, bool partly)
    {
        if (hostModels.Count == 0) return;
        int? glassesSet = null;
        try { glassesSet = InvisibleGlasses.Resolve(Plugin.DataManager, log)?.ModelSet; }
        catch (Exception ex) { log.Debug("[Proteus] invisible glasses unknown — no carrier wording for them ({0})", ex.Message); }
        bool redrawOff = !AutoRedrawOn;
        foreach (var group in hostModels.GroupBy(m => ShellNotDrawnNotice.IsCarrierHost(m, glassesSet)).OrderBy(g => g.Key))
        {
            var label = HostLabelFor(group);
            var line = string.Format(ShellNotDrawnNotice.Localized(partly, carrier: group.Key, redrawOff), label);
            log.Information("[Proteus] second skin notice: {0}", line);
            _ = Plugin.Framework.RunOnFrameworkThread(
                () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(line, 17).Build()));
        }
    }

    private string HostLabelFor(IEnumerable<string> hostModels)
    {
        try
        {
            _itemNames ??= ItemNames.Lookup(Plugin.DataManager, log);
            _facewearNames ??= ItemNames.FacewearLookup(Plugin.DataManager, log);
            var facewearSets = InvisibleGlasses.FacewearModelSets(Plugin.DataManager);
            return ShellNotDrawnNotice.HostLabel(hostModels, _itemNames, _facewearNames,
                set => facewearSets?.Contains(set) ?? false, ContentSlot.DisplayLabel);
        }
        catch (Exception ex)
        {
            log.Debug("[Proteus] host names unavailable ({0})", ex.Message);
            return ShellNotDrawnNotice.HostLabel(hostModels, static (_, _) => null, static _ => null, static _ => false,
                static s => s);
        }
    }

    /// <summary>
    /// A published shell is partly or not drawn while its host is: one automatic redraw (no chat), or a deferral, or the
    /// message naming the accessory — see <see cref="ShellNotDrawnNotice.Next"/>.
    /// </summary>
    private void DecideUndrawn(ShellDrawnProbe expected, bool reloadWithheld, RefreshTimeline? timeline,
                               IReadOnlyList<string> missing, bool partly, bool afterOwnRedraw, int extra)
    {
        var key = ShellProbeKey(expected);
        var step = ShellNotDrawnNotice.Next(key, _shellAutoRedrawKey, _shellNotDrawnNoticeKey, AutoRedrawOn, afterOwnRedraw,
            ReferenceEquals(_shellDrawnCheck, expected), CompositeBusy, redrawHold.IsHeld,
            expected.PublishedWithWriteFailures, AutoRedrawsInWindow(), extra);

        switch (step)
        {
            case ShellNotDrawnNotice.Step.AutoRedraw:
                _shellDrawnLastStep = "auto-redraw";
                _shellAutoRedrawKey = key;
                NoteAutoRedraw();
                log.Information("[Proteus] second skin not drawn — redrawing the character once before saying anything");
#if PROTEUS_TEST_HARNESS
                HarnessBeforeOwnShellRedraw(expected);
#endif
                bool reached;
                try
                {
                    reached = RedrawPlayerLogged("second skin not drawn — one automatic redraw",
                        () => Plugin.Framework.RunOnFrameworkThread(penumbra.RedrawPlayer).GetAwaiter().GetResult());
                }
                catch (OperationCanceledException) { return; }
                if (reached)
                    ScheduleShellRecheck(expected, reloadWithheld, null, 0, 0, afterOwnRedraw: true, extra: 0);
                else
                    NotifyUndrawn(expected, missing, partly);
                return;

            case ShellNotDrawnNotice.Step.Defer:
                _shellDrawnLastStep = "defer";
                log.Information("[Proteus] second skin not drawn — {0}; checking once more before saying anything",
                    expected.PublishedWithWriteFailures ? "its publish had write failures, which the retry composite owns"
                    : redrawHold.IsHeld ? "redraws are held for an auto refit batch" : "a composite is pending");
                ScheduleShellRecheck(expected, reloadWithheld, timeline, 0, 3000, afterOwnRedraw, extra + 1);
                return;

            case ShellNotDrawnNotice.Step.Notify:
            case ShellNotDrawnNotice.Step.NotifyRedrawOff:
                NotifyUndrawn(expected, missing, partly);
                return;

            default:
                _shellDrawnLastStep = "silent";
                log.Debug("[Proteus] second skin not drawn — superseded or already reported; nothing said");
                return;
        }
    }

    /// <summary>The message naming the accessory, once per shell.</summary>
    private void NotifyUndrawn(ShellDrawnProbe expected, IEnumerable<string> missing, bool partly)
    {
        var key = ShellProbeKey(expected);
        if (!ReferenceEquals(_shellDrawnCheck, expected)
            || string.Equals(_shellNotDrawnNoticeKey, key, StringComparison.Ordinal)) return;
        _shellNotDrawnNoticeKey = key;
        _shellDrawnLastStep = AutoRedrawOn ? "notify" : "notify-redraw-off";
        PrintShellNotice(HostsOf(expected, missing), partly);
    }

    /// <summary>
    /// A drawn check after Proteus's own redraw that ended without confirming the shell (inconclusive, host gone, no read,
    /// a carrier deferral): one more re-check, then the message. Never silent. False when this was not after our redraw.
    /// </summary>
    private bool EndAfterOwnRedraw(ShellDrawnProbe expected, bool reloadWithheld, bool afterOwnRedraw, int extra)
    {
        if (!afterOwnRedraw) return false;
        if (extra < 1)
        {
            log.Information("[Proteus] second skin drawn check after the automatic redraw was inconclusive — once more");
            ScheduleShellRecheck(expected, reloadWithheld, null, 0, 3000, afterOwnRedraw: true, extra: extra + 1);
        }
        else
            NotifyUndrawn(expected, expected.Materials, partly: true);
        return true;
    }

    /// <summary>A conclusive draw: the shell's once-keys are spent no more.</summary>
    private void ClearUndrawnKeys(ShellDrawnProbe expected)
    {
        var key = ShellProbeKey(expected);
        if (string.Equals(_shellAutoRedrawKey, key, StringComparison.Ordinal)) _shellAutoRedrawKey = null;
        if (string.Equals(_shellNotDrawnNoticeKey, key, StringComparison.Ordinal)) _shellNotDrawnNoticeKey = null;
        _shellDrawnLastStep = "confirmed";
    }
}
