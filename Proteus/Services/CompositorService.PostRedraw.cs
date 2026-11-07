using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CheapLoc;
using Dalamud.Game.Text.SeStringHandling;
using Penumbra.Api.Enums;

namespace Proteus.Services;

public partial class CompositorService
{
    /// <summary>
    /// Whether a shell material was published for this host model: same item folder and the model's slot in the material
    /// name (<c>…_wrs.mdl</c> ↔ <c>mt_…_wrs_b.mtrl</c>). The race code is ignored (a carrier's EQDP edit can change it).
    /// </summary>
    internal static bool SameShellHost(string materialPath, string modelPath)
    {
        int mat = materialPath.IndexOf("/material/", StringComparison.OrdinalIgnoreCase);
        int mdl = modelPath.IndexOf("/model/", StringComparison.OrdinalIgnoreCase);
        if (mat <= 0 || mdl <= 0 || mat != mdl
            || string.Compare(materialPath, 0, modelPath, 0, mat, StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        var stem = Path.GetFileNameWithoutExtension(modelPath);
        int us = stem.LastIndexOf('_');
        if (us < 0) return false;
        var slotTag = stem[us..] + "_";                                    // "_wrs_"
        return Path.GetFileName(materialPath).Contains(slotTag, StringComparison.OrdinalIgnoreCase);
    }

    // Poll interval within the 2.4 s check window.
    private const int ShellCheckPollMs   = 200;
    private const int ShellCheckAttempts = 12;

    // The earliest a read may confirm the shell drawn: shell material paths repeat across composites, so an earlier read
    // may see the previous shell and wrongly veto the unstick redraw.
    private const int ShellCheckMinConfirmMs = 800;

    private void SchedulePostRedrawShellCheck(bool reloadWithheld, RefreshTimeline? timeline = null)
    {
        var expected = _shellDrawnCheck;
        if (expected == null || expected.Materials.Count == 0) return;

        // Counted from scheduling to the end (Copy Logs waits on it), on every way out.
        _ = InFlight.ScheduleAsync(_shellChecksInFlight, () => Task.CompletedTask, () => true,
            () => CheckShellDrawnAsync(expected, reloadWithheld, timeline));
    }

    /// <summary>Waits for the redraw to settle, then reports shell materials the game did not draw.</summary>
    /// <param name="recheck">0 for the check after a publish; 1 and 2 for the drawn check's own re-checks of a shell
    /// whose carrier host was not drawn (see <see cref="CarrierRecheck"/>).</param>
    /// <param name="afterOwnRedraw">This check follows Proteus's own automatic redraw for this shell: it ends in a
    /// confirmation or the message, never silently (see <see cref="EndAfterOwnRedraw"/>).</param>
    /// <param name="extra">How many deferral or after-redraw re-checks this shell has had.</param>
    private async Task CheckShellDrawnAsync(ShellDrawnProbe expected, bool reloadWithheld, RefreshTimeline? timeline,
                                            int recheck = 0, bool afterOwnRedraw = false, int extra = 0)
    {
        try
        {
            List<string> missing = [];
            // Materials whose own host was not drawn on the last read; not judged.
            List<string> hostGone = [];
            bool hostEverDrawn = false;
            bool anyRead = false;
            // Whether the last read is the one `missing` describes: a verdict must come from the final state of the window.
            bool lastJudged = false;
            // When reads started finding every material in place; the timeline is stamped here, not at the confirming read.
            long presentSince = 0;
            HashSet<string>? hostMaterials = null;
            for (int attempt = 0; attempt < ShellCheckAttempts; attempt++)
            {
                await Task.Delay(ShellCheckPollMs).ConfigureAwait(false);
                if (_disposed) return;

                // Superseded by a newer build — that composite runs its own check.
                if (!ReferenceEquals(_shellDrawnCheck, expected)) return;

                HashSet<string>? materials, models;
                try
                {
                    materials = Plugin.Framework.RunOnFrameworkThread(penumbra.GetActivePlayerMaterialPaths).GetAwaiter().GetResult();
                    models    = Plugin.Framework.RunOnFrameworkThread(penumbra.GetActivePlayerModelPaths).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { return; }
                // No answer (not in game, IPC down, or mid-redraw): wait the window out before concluding.
                var readAt = PhaseCounter.Begin();
                if (materials == null || models == null) { lastJudged = false; presentSince = 0; continue; }
                anyRead = true;

                // The anchor: until a material's own host model is drawn, a missing material means "not loaded yet". Judged per host;
                // a material whose host cannot be told from its path is judged on the whole shell.
                if (!expected.Models.Any(models.Contains)) { lastJudged = false; presentSince = 0; continue; }
                hostEverDrawn = true;

                bool OwnHostDrawn(string mtrl)
                {
                    var own = expected.Models.Where(m => SameShellHost(mtrl, m)).ToList();
                    return own.Count == 0 || own.Any(models.Contains);
                }
                hostGone = expected.Materials.Where(p => !OwnHostDrawn(p)).ToList();
                missing  = expected.Materials.Where(p => OwnHostDrawn(p) && !materials.Contains(p)).ToList();
                lastJudged = true;
                if (missing.Count == 0 && hostGone.Count == 0) { if (presentSince == 0) presentSince = readAt; }
                else presentSince = 0;
                if (missing.Count == 0 && hostGone.Count > 0) continue;   // not a verdict either way yet
                // Too early to tell this shell from the previous one (see ShellCheckMinConfirmMs).
                if (missing.Count == 0 && (attempt + 1) * ShellCheckPollMs < ShellCheckMinConfirmMs) continue;
                if (missing.Count == 0)
                {
                    // The one place this is set; everything below is a failure, inconclusive read or deferral.
                    _shellConfirmedDrawnKey = ShellProbeKey(expected);
                    ClearUndrawnKeys(expected);
                    // Stamped with the key: a carrier ask or a game redraw after this makes it stale (ShellConfirmedSince).
                    Interlocked.Exchange(ref _shellConfirmedDrawnAtTick, Environment.TickCount64);
                    if (timeline != null)
                        log.Information("[Proteus] refresh drawn: {0:F0}ms from first trigger to the shell on the "
                                      + "character (to within {1}ms)",
                            (presentSince - timeline.Start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
                            ShellCheckPollMs);
                    // Information, not Debug: this separates "on the character but rendering wrong" from "never loaded" in user logs.
                    // Paths only when they change.
                    var drawn = string.Join(", ", expected.Materials);
                    if (string.Equals(_lastDrawnMaterials, drawn, StringComparison.Ordinal))
                        log.Information("[Proteus] second skin is drawn: all {0} shell material(s) are on "
                                      + "the character", expected.Materials.Count);
                    else
                    {
                        _lastDrawnMaterials = drawn;
                        log.Information("[Proteus] second skin is drawn: all {0} shell material(s) are on "
                                      + "the character [{1}]", expected.Materials.Count, drawn);
                    }
                    return;
                }
                hostMaterials = materials;
            }

            // Never got an answer at all — not in game / IPC down. After our own redraw, once more, then the message.
            if (!anyRead) { EndAfterOwnRedraw(expected, reloadWithheld, afterOwnRedraw, extra); return; }

            // The host was seen, but the window ended on a read that could not judge it: no verdict either way.
            if (hostEverDrawn && !lastJudged)
            {
                log.Information("[Proteus] second skin drawn check inconclusive — the character was not "
                        + "drawable, or its host not back, on the last read of the sampling window (not a failure)");
                EndAfterOwnRedraw(expected, reloadWithheld, afterOwnRedraw, extra);
                return;
            }

            if (!hostEverDrawn)
            {
                // Every host undrawn: when they are our ring carriers, re-check on our own as below (no echo composite).
                if (CarrierRecheck(expected, reloadWithheld, timeline, recheck, expected.Materials, afterOwnRedraw, extra)) return;
                log.Information("[Proteus] second skin drawn check inconclusive — the host accessory was not back "
                        + "in the draw object within the sampling window, so whether our mesh loaded is "
                        + "unknown (not a failure)");
                EndAfterOwnRedraw(expected, reloadWithheld, afterOwnRedraw, extra);
                return;
            }

            // Everything whose host is drawn loaded; the rest rides a host that left after the build. Not a failure.
            if (missing.Count == 0)
            {
                // A host that is one of our carriers: no echo composite re-checks it any more, so this check does.
                if (CarrierRecheck(expected, reloadWithheld, timeline, recheck, hostGone, afterOwnRedraw, extra)) return;
                log.Information("[Proteus] second skin drawn check inconclusive — {0} shell material(s) ride a "
                              + "host that is not drawn right now ({1}); everything on a drawn host loaded",
                    hostGone.Count, string.Join(", ", hostGone));
                EndAfterOwnRedraw(expected, reloadWithheld, afterOwnRedraw, extra);
                return;
            }

            // A carrier equipped during this composite is still settling (its model can precede its materials). No echo
            // composite re-checks any more, so this check schedules its own, timed from that ask (two at most).
            long lastCarrierAsk = Math.Max(LastRingAskTick, _lastGlassesInjectTick);
            var sinceCarrier = unchecked(Environment.TickCount64 - lastCarrierAsk);
            if (sinceCarrier >= 0 && sinceCarrier < RingInjectCooldownMs)
            {
                if (recheck < 2)
                {
                    log.Information("[Proteus] second skin drawn check deferred — a carrier was equipped {0}ms ago "
                            + "and is still loading; re-checking", sinceCarrier);
                    ScheduleShellRecheck(expected, reloadWithheld, timeline, recheck + 1,
                        ShellRecheck.DelayMs(Environment.TickCount64, lastCarrierAsk, RingInjectCooldownMs, CarrierDecisions.SettleMs),
                        afterOwnRedraw, extra);
                }
                else
                {
                    log.Information("[Proteus] second skin drawn check still deferred — a carrier was equipped {0}ms ago "
                            + "and is still loading; no further re-check", sinceCarrier);
                    EndAfterOwnRedraw(expected, reloadWithheld, afterOwnRedraw, extra);
                }
                return;
            }

            // What the host did load, so a wrong v#### folder is visible from this line alone.
            var hostDir = missing
                .Select(p => p[..(p.LastIndexOf('/') + 1)])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var siblings = hostMaterials == null ? [] : hostMaterials
                .Where(m => hostDir.Any(d => m.StartsWith(
                    d[..(d.IndexOf("/material/", StringComparison.OrdinalIgnoreCase) + 10)],
                    StringComparison.OrdinalIgnoreCase)))
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // A failure makes the next success a transition, so print its paths in full when it comes.
            _lastDrawnMaterials = null;
            _shellConfirmedDrawnKey = null;

            if (reloadWithheld)
            {
                log.Information("[Proteus] second skin not drawn yet — {0} of {1} shell material(s) are not on "
                              + "the character ({2}), but auto redraw is off, so the character was not reloaded "
                              + "and is still drawing the previous shell. Expected until something redraws it",
                    missing.Count, expected.Materials.Count, string.Join(", ", missing));

                // Once per shell, so repeated edits with auto redraw off don't repeat the notice.
                var probeKey = ShellProbeKey(expected);
                if (string.Equals(_redrawWithheldNoticeKey, probeKey, StringComparison.Ordinal)) return;
                _redrawWithheldNoticeKey = probeKey;

                var needsRedraw = ProteusLoc.Localize("Chat.ShellNeedsRedraw",
                    "[Proteus] Your second skin changed, but auto redraw is off, so your character is still "
                    + "wearing the previous version. Zone or use Penumbra's Redraw button to see it.");
                _ = Plugin.Framework.RunOnFrameworkThread(
                    () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(needsRedraw, 17).Build()));
                return;
            }

            // A shell material drawn on the same host means a shell of ours is loaded there (nothing else declares it). Then a
            // missing neighbour either failed on its own (a material does not load when one of its textures does not) or
            // belongs to a newer shell than the one drawn. The two look alike from here; Penumbra's log tells them apart.
            var failedAlone = hostMaterials == null ? [] : missing
                .Where(p => expected.Models.Where(m => SameShellHost(p, m)).ToList() is { Count: > 0 } own
                         && expected.Materials.Any(q => hostMaterials.Contains(q) && own.Any(m => SameShellHost(q, m))))
                .ToList();
            if (failedAlone.Count == missing.Count)
            {
                log.Warning("[Proteus] second skin is PARTLY drawn — {0} of {1} shell material(s) never appeared on the "
                          + "character: {2}. Other shell materials on the same host did, so a shell of ours is loaded "
                          + "there. Either these materials failed to load on their own (look just above for Penumbra's "
                          + "\"Failed to … load resource … FailedSubResource\" naming them, and the texture lines "
                          + "before it), or the game is still drawing the previous shell model",
                    missing.Count, expected.Materials.Count, string.Join(", ", missing));

                // One automatic redraw before any chat; the message names the accessory if it is still missing after.
                DecideUndrawn(expected, reloadWithheld, timeline, missing, partly: true, afterOwnRedraw, extra);
                return;
            }

            log.Warning("[Proteus] second skin built and published but is NOT being drawn — {0} of {1} "
                      + "shell material(s) never appeared on the character: {2}. The host accessory it "
                      + "was appended into is drawn, but not our copy of it. The host's OWN materials in "
                      + "the draw object are: {3}",
                missing.Count, expected.Materials.Count, string.Join(", ", missing),
                siblings.Count == 0 ? "(none — the host loads no material of its own)" : string.Join(", ", siblings));

            DecideUndrawn(expected, reloadWithheld, timeline, missing, partly: false, afterOwnRedraw, extra);
        }
        catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { }
        catch (Exception ex) { log.Error(ex, "[Proteus] post-redraw shell check failed"); }
    }

    /// <summary>Set while a post-redraw body-type check is in flight, so overlapping composites cannot run two at once
    /// off different half-settled readings.</summary>
    private int _postSettleCheckRunning;

    private const int PostSettleInitialMs = 600;
    private const int PostSettlePollMs    = 200;
    private const int PostSettleMaxPolls  = 6;   // ~1.6s all told

    private void SchedulePostRedrawBodyTypeCheck()
    {
        if (Interlocked.Exchange(ref _postSettleCheckRunning, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PostSettleInitialMs).ConfigureAwait(false);

                // The loop only samples; DecidePostSettle judges the readings so far (#57). A body type beside no char
                // code is a mid-reload walk, and its shapes come from that same walk: counted as a blank.
                var samples = new List<DrawSample>();
                var readings = new List<PostSettleReading?>();
                PostSettleVerdict verdict = default;
                for (int i = 0; i < PostSettleMaxPolls; i++)
                {
                    DrawSample s;
                    // GetResult (not await) inside SampleDrawState keeps the continuation on this pool thread.
                    try { s = SampleDrawState(); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { return; }

                    samples.Add(s);
                    // Empty as well as null is a blank walk; blanks must never agree with each other.
                    readings.Add(s.Materials is { Count: > 0 } m
                        ? new PostSettleReading(BodyTypeKey(m), CharCodeKey(CharCodeSet(m)),
                                                s.Shapes == null ? null : BodyShapeSignature(s.Shapes))
                        : null);
                    verdict = DecidePostSettle(readings, final: i == PostSettleMaxPolls - 1, _lastCompositedBodyType,
                                               _lastCompositedCharCodes, _lastCompositedBodyShapeSig);
                    if (verdict.Action != PostSettleAction.KeepSampling) break;
                    await Task.Delay(PostSettlePollMs).ConfigureAwait(false);
                }

                if (verdict.Action is PostSettleAction.None or PostSettleAction.KeepSampling)
                {
                    if (verdict.Dropped != null)
                        log.Debug("[Proteus] post-settle: dropped an incoherent reading; it would have triggered: {0}",
                                  verdict.Dropped);
                    return;
                }

                var latest = samples[verdict.Latest];
                var reading = readings[verdict.Latest]!.Value;
                log.Debug("[Proteus] Post-settle correction: bodyType={0}→{1} charCode={2}→{3} "
                        + "shapesChanged={4} confirmed={5}",
                    _lastCompositedBodyType ?? "none", reading.BodyType ?? "none",
                    _lastCompositedCharCodes ?? "none", reading.CharCodes ?? "none",
                    verdict.ShapesChanged, verdict.Confirmed);

                if (verdict.Action == PostSettleAction.Correct)
                {
                    // Two agreeing readings: publish, so the corrective recomposite uses them (dirty stays false).
                    var snapshot = latest.Materials!;
                    _activeMtrlSnapshot = snapshot;
                    _activeMtrlSnapshotDirty = false;
                    _activeMtrlSnapshotRewalk = false;
                    config.CachedActiveMaterialPaths = snapshot.ToList();
                    if (verdict.PublishShapes) _bodyShapeSnapshot = latest.Shapes;
                }
                else
                {
                    // Still moving at the cap: still worth a corrective composite, but ask for a fresh walk rather than
                    // publish an unverified read; the trigger's settle walk re-reads it. Not dirty: no body mod changed,
                    // and this publish already reloaded the character, so a preamble reload is waste.
                    _activeMtrlSnapshotRewalk = true;
                }
                TriggerRecomposite("post-settle-correction", force: false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
            catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { }
            finally { Volatile.Write(ref _postSettleCheckRunning, 0); }
        });
    }

    /// <summary>One post-redraw reading: null elements as the walk gave them; a null <see cref="ShapeSig"/> means the
    /// shapes were not read (an empty signature is "nothing enabled").</summary>
    internal readonly record struct PostSettleReading(string? BodyType, string? CharCodes, string? ShapeSig);

    internal enum PostSettleAction { KeepSampling, None, Correct, CorrectWithRewalk }

    /// <summary>What <see cref="DecidePostSettle"/> concluded. <see cref="Latest"/> indexes the trusted reading acted on
    /// (-1 when none); <see cref="Dropped"/> names, for a dropped incoherent reading, the facts it would have triggered.</summary>
    internal readonly record struct PostSettleVerdict(PostSettleAction Action, int Latest, bool Confirmed, bool PublishShapes,
                                                      bool BodyTypeChanged, bool CharCodeChanged, bool ShapesChanged,
                                                      string? Dropped);

    /// <summary>
    /// The whole post-redraw backstop decision over the readings so far (null = a blank walk), so the loop only samples
    /// (#57). A reading with a body type but no char code is a mid-reload walk — its shapes come from that same walk —
    /// so it counts as a blank: it resets confirmation and is never acted on. Two consecutive trusted readings with
    /// equal keys confirm; otherwise sampling goes on until <paramref name="final"/>, when a coherent last reading is
    /// still judged (a correction with a fresh walk). A body type shrunk to a strict subset is no change; shapes change
    /// only from a read, different signature.
    /// </summary>
    internal static PostSettleVerdict DecidePostSettle(IReadOnlyList<PostSettleReading?> readings, bool final,
                                                       string? lastBodyType, string? lastCharCodes, string? lastShapeSig)
    {
        static bool Trusted(PostSettleReading r) => !(r.BodyType != null && r.CharCodes == null);
        static string Key(PostSettleReading r) => string.Join('\n', r.BodyType ?? "-", r.CharCodes ?? "-", r.ShapeSig ?? "");

        (bool Body, bool Char, bool Shapes) Changes(PostSettleReading r)
            => (r.BodyType != null && !IsStrictSubsetKey(r.BodyType, lastBodyType)
                    && !string.Equals(r.BodyType, lastBodyType, StringComparison.OrdinalIgnoreCase),
                r.CharCodes != null && !string.Equals(r.CharCodes, lastCharCodes, StringComparison.OrdinalIgnoreCase),
                r.ShapeSig != null && !string.Equals(r.ShapeSig, lastShapeSig, StringComparison.Ordinal));

        // Every fact any dropped reading would have triggered (the union), for the Debug line.
        bool dBody = false, dChar = false, dShapes = false;
        foreach (var r in readings)
            if (r is { } u && !Trusted(u))
            {
                var (b, c, sh) = Changes(u);
                dBody |= b; dChar |= c; dShapes |= sh;
            }
        var names = new List<string>();
        if (dBody) names.Add("body type");
        if (dChar) names.Add("char code");
        if (dShapes) names.Add("shapes");
        string? dropped = names.Count > 0 ? string.Join(", ", names) : null;

        int n = readings.Count;
        bool TrustedAt(int i) => i >= 0 && readings[i] is { } r && Trusted(r);
        bool confirmed = n >= 2 && TrustedAt(n - 1) && TrustedAt(n - 2)
                         && Key(readings[n - 1]!.Value) == Key(readings[n - 2]!.Value);
        if (!confirmed && !final) return new(PostSettleAction.KeepSampling, -1, false, false, false, false, false, dropped);
        if (!TrustedAt(n - 1)) return new(PostSettleAction.None, -1, false, false, false, false, false, dropped);

        var latest = readings[n - 1]!.Value;
        var (body, chr, shapes) = Changes(latest);
        if (!(body || chr || shapes))
            return new(PostSettleAction.None, n - 1, confirmed, false, false, false, false, dropped);
        return new(confirmed ? PostSettleAction.Correct : PostSettleAction.CorrectWithRewalk, n - 1, confirmed,
                   confirmed && latest.ShapeSig != null, body, chr, shapes, dropped);
    }

    /// <summary>When the drawn check last confirmed <see cref="_shellConfirmedDrawnKey"/> (TickCount64).</summary>
    private long _shellConfirmedDrawnAtTick;

    /// <summary>The "carrier equipped but not drawn" notice was shown for this shell (content key).</summary>
    private volatile string? _carrierUndrawnNoticeKey;

    /// <summary>Run the drawn check again for the same probe after <paramref name="delayMs"/>, if it is still current.</summary>
    private void ScheduleShellRecheck(ShellDrawnProbe expected, bool reloadWithheld, RefreshTimeline? timeline,
                                      int recheck, long delayMs, bool afterOwnRedraw = false, int extra = 0)
    {
        // Counted from scheduling to the end, released on every way out — superseded included (Copy Logs waits on it).
        _ = InFlight.ScheduleAsync(_shellChecksInFlight,
            () => Task.Delay((int)Math.Clamp(delayMs, 0, 60_000) + 250),
            () => !_disposed && ReferenceEquals(_shellDrawnCheck, expected),   // superseded: the newer check owns it
            () => CheckShellDrawnAsync(expected, reloadWithheld, timeline, recheck, afterOwnRedraw, extra),
            ex => log.Debug("[Proteus] shell re-check failed: {0}", ex.Message));
    }

    /// <summary>
    /// The drawn check ended with shell materials on a host that is not drawn. When that host is one of our ring
    /// carriers, re-check on our own (no echo composite does any more), timed from the slot's last ask, and decide the
    /// deferral before any warning: a retry still pending or owed, or an ask still settling, is never a failure. Only a
    /// carrier asked for up to the cap and still undrawn after settling is reported — once per shell. Returns false
    /// when no carrier host is involved (today's inconclusive line applies).
    /// </summary>
    private bool CarrierRecheck(ShellDrawnProbe expected, bool reloadWithheld, RefreshTimeline? timeline, int recheck,
                                IReadOnlyList<string> hostGone, bool afterOwnRedraw = false, int extra = 0)
    {
        var slots = InvisibleRing.CarrierSlots
            .Select(c => c.Slot)
            .Where(slot => hostGone.Any(m => m.Contains("_" + slot + "_", StringComparison.OrdinalIgnoreCase)))
            .Where(slot => _injectedCarrierSlots.Contains(slot, StringComparer.Ordinal) || _carrierAsks.LastAskTick(slot) != 0)
            .ToList();
        if (slots.Count == 0) return false;

        // A host slot the build hosts on, gone from the drawn check's own walk, with no ask this epoch and none
        // pending: the shared walk that decided "no ask" is stale (QA: a re-added ring layer was never asked for and
        // stayed undrawn for minutes). Refresh it and deliver the owed ask, once per check.
        long epochNow = Interlocked.Read(ref _hostDecisionEpoch);
        var owed = slots.Where(slot => _lastShellCarrierSlots.Contains(slot, StringComparer.Ordinal)
                                    && _carrierAsks.Count(slot, epochNow) == 0 && !_carrierRetry.PendingFor(slot))
                        .ToList();
        if (owed.Count > 0 && recheck < 2)
        {
            bool refreshed = false;
            try { refreshed = Plugin.Framework.RunOnFrameworkThread(RefreshEquippedModels).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.Debug("[Proteus] second skin drawn check: walk refresh failed ({0})", ex.GetType().Name);
            }
            log.Information("[Proteus] second skin drawn check: carrier {0} is hosted but was never asked for — "
                          + "asking now{1}", string.Join(",", owed), refreshed ? "" : " (walk not refreshed)");
            foreach (var slot in owed) ScheduleCarrierRetry(0, slot);
        }

        long now = Environment.TickCount64;
        if (recheck == 0)
        {
            long delay = slots.Max(slot => ShellRecheck.DelayMs(now, _carrierAsks.LastAskTick(slot), RingInjectCooldownMs,
                                                                CarrierDecisions.SettleMs));
            log.Information("[Proteus] second skin drawn check: carrier {0} is not drawn yet — re-checking in {1}ms",
                string.Join(",", slots), delay + 250);
            ScheduleShellRecheck(expected, reloadWithheld, timeline, 1, delay, afterOwnRedraw, extra);
            return true;
        }

        long epoch = Interlocked.Read(ref _hostDecisionEpoch);
        var verdicts = slots.Select(slot =>
        {
            // Read now, at re-check time: a retry that went out since the re-check was scheduled moves the clock.
            long lastAsk = _carrierAsks.LastAskTick(slot);
            bool pending = _carrierRetry.PendingFor(slot);
            bool belowCap = _carrierAsks.Count(slot, epoch) < CarrierDecisions.AskCap;
            return (Slot: slot, Verdict: ShellRecheck.Next(recheck, now - lastAsk, pending, belowCap, hostGone: true,
                RingInjectCooldownMs, CarrierDecisions.SettleMs), LastAsk: lastAsk, OwedNotPending: belowCap && !pending);
        }).ToList();

        if (verdicts.Any(v => v.Verdict == ShellRecheck.Verdict.Schedule))
        {
            long retryAt = _carrierRetry.Deadline;
            long delay = verdicts.Max(v => ShellRecheck.NextDelayMs(now, v.LastAsk, retryAt, v.OwedNotPending,
                                                                    RingInjectCooldownMs, CarrierDecisions.SettleMs));
            ScheduleShellRecheck(expected, reloadWithheld, timeline, recheck + 1, delay, afterOwnRedraw, extra);
            return true;
        }
        if (verdicts.Any(v => v.Verdict == ShellRecheck.Verdict.Info))
        {
            log.Information("[Proteus] second skin drawn check inconclusive — carrier {0} is not drawn, but no retry "
                          + "was owed or it was still pending when the last re-check ran (not reported as a failure)",
                string.Join(",", verdicts.Where(v => v.Verdict == ShellRecheck.Verdict.Info).Select(v => v.Slot)));
            EndAfterOwnRedraw(expected, reloadWithheld, afterOwnRedraw, extra);
            return true;
        }

        var failed = verdicts.Where(v => v.Verdict == ShellRecheck.Verdict.Warn).Select(v => v.Slot).ToList();
        log.Warning("[Proteus] second skin is NOT drawn — carrier {0} is equipped but the game draws nothing there "
                  + "({1})", string.Join(",", failed), string.Join(", ", hostGone));
        var probeKey = ShellProbeKey(expected);
        if (string.Equals(_carrierUndrawnNoticeKey, probeKey, StringComparison.Ordinal)) return true;
        _carrierUndrawnNoticeKey = probeKey;
        // Named: the carrier is Proteus's own invisible accessory, so it gets the carrier wording (#91 / owner's report).
        var carrierHosts = expected.Models
            .Where(m => failed.Any(slot => m.EndsWith($"_{slot}.mdl", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        PrintShellNotice(carrierHosts.Count > 0 ? carrierHosts : HostsOf(expected, hostGone), partly: true);
        return true;
    }
}
