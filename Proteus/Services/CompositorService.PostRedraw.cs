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

        _ = Task.Run(() => CheckShellDrawnAsync(expected, reloadWithheld, timeline));
    }

    /// <summary>Waits for the redraw to settle, then reports shell materials the game did not draw.</summary>
    private async Task CheckShellDrawnAsync(ShellDrawnProbe expected, bool reloadWithheld, RefreshTimeline? timeline)
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

            if (!anyRead) return;   // never got an answer at all — not in game / IPC down

            // The host was seen, but the window ended on a read that could not judge it: no verdict either way.
            if (hostEverDrawn && !lastJudged)
            {
                log.Information("[Proteus] second skin drawn check inconclusive — the character was not "
                        + "drawable, or its host not back, on the last read of the sampling window (not a failure)");
                return;
            }

            if (!hostEverDrawn)
            {
                log.Information("[Proteus] second skin drawn check inconclusive — the host accessory was not back "
                        + "in the draw object within the sampling window, so whether our mesh loaded is "
                        + "unknown (not a failure)");
                return;
            }

            // Everything whose host is drawn loaded; the rest rides a host that left after the build. Not a failure.
            if (missing.Count == 0)
            {
                log.Information("[Proteus] second skin drawn check inconclusive — {0} shell material(s) ride a "
                              + "host that is not drawn right now ({1}); everything on a drawn host loaded",
                    hostGone.Count, string.Join(", ", hostGone));
                return;
            }

            // A carrier equipped during this composite is still settling (its model can precede its materials); the next
            // composite re-runs this check.
            var sinceCarrier = unchecked(Environment.TickCount64
                - Math.Max(_lastRingInjectTick, _lastGlassesInjectTick));
            if (sinceCarrier >= 0 && sinceCarrier < RingInjectCooldownMs)
            {
                log.Information("[Proteus] second skin drawn check deferred — a carrier was equipped {0}ms ago "
                        + "and is still loading; the next composite re-checks", sinceCarrier);
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

                var needsRedraw = Loc.Localize("Chat.ShellNeedsRedraw",
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

                var failed = Loc.Localize("Chat.ShellMaterialFailed",
                    "[Proteus] Part of your second skin isn't being drawn. The rest of it is on your character, so "
                    + "either a texture that piece needs is missing, or the game is still holding the previous "
                    + "version. Try \"Recomposite now\" first, then unequipping and re-equipping the accessory it "
                    + "rides on.");
                _ = Plugin.Framework.RunOnFrameworkThread(
                    () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(failed, 17).Build()));
                return;
            }

            log.Warning("[Proteus] second skin built and published but is NOT being drawn — {0} of {1} "
                      + "shell material(s) never appeared on the character: {2}. The host accessory it "
                      + "was appended into is drawn, but not our copy of it. The host's OWN materials in "
                      + "the draw object are: {3}",
                missing.Count, expected.Materials.Count, string.Join(", ", missing),
                siblings.Count == 0 ? "(none — the host loads no material of its own)" : string.Join(", ", siblings));

            var msg = Loc.Localize("Chat.ShellNotDrawing",
                "[Proteus] Your second skin was built but the game isn't drawing it. The accessory "
                + "it rides on is equipped, yet the character isn't loading Proteus' version. Try "
                + "unequipping and re-equipping that accessory, or pick a different one.");
            _ = Plugin.Framework.RunOnFrameworkThread(
                () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 17).Build()));
        }
        catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { }
        catch (Exception ex) { log.Error(ex, "[Proteus] post-redraw shell check failed"); }
    }

    /// <summary>
    /// The body-side facts this backstop judges, as one comparable key. Narrower than <see cref="DrawStateSignature"/>:
    /// equipment changes belong to the redraw hook.
    /// </summary>
    private static string PostSettleKey(in DrawSample s)
        => string.Join('\n',
            BodyTypeKey(s.Materials!)              ?? "-",
            CharCodeKey(CharCodeSet(s.Materials!)) ?? "-",
            BodyShapeSignature(s.Shapes));

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

                // Confirm before deciding: sample until two consecutive readings agree, since a single mid-reload read looks like
                // a body-type change.
                string? prevKey = null;
                DrawSample latest = default;
                bool confirmed = false;
                for (int i = 0; i < PostSettleMaxPolls; i++)
                {
                    DrawSample s;
                    // GetResult (not await) inside SampleDrawState keeps the continuation on this pool thread.
                    try { s = SampleDrawState(); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { return; }

                    // Empty as well as null is a blank walk; blanks must never agree with each other.
                    if (s.Materials is { Count: > 0 })
                    {
                        latest = s;
                        var key = PostSettleKey(s);
                        if (string.Equals(key, prevKey, StringComparison.Ordinal)) { confirmed = true; break; }
                        prevKey = key;
                    }
                    else prevKey = null;

                    // Not after the last sample: nothing left to wait for.
                    if (i < PostSettleMaxPolls - 1)
                        await Task.Delay(PostSettlePollMs).ConfigureAwait(false);
                }
                if (prevKey == null) return;   // never got a usable reading at all

                // Same builders WaitForRaceToSettle and Recomposite use (see CharCodeKey).
                var snapshot       = latest.Materials!;
                var newBodyTypeKey = BodyTypeKey(snapshot);
                var newCharCodeKey = CharCodeKey(CharCodeSet(snapshot));

                // A body type beside a null char code is a mid-reload walk: veto acting on the materials. A veto, not an early
                // return, because the body-shape branch below reads shape keys, not materials.
                bool materialsIncoherent = newBodyTypeKey != null && newCharCodeKey == null;
                if (materialsIncoherent)
                    log.Debug("[Proteus] post-settle materials are incoherent (bodyType={0}, charCode=none) — "
                            + "judging shapes only", newBodyTypeKey ?? "none");

                bool bodyTypeShrank  = IsStrictSubsetKey(newBodyTypeKey, _lastCompositedBodyType);
                bool bodyTypeChanged = !materialsIncoherent && newBodyTypeKey != null && !bodyTypeShrank &&
                    !string.Equals(newBodyTypeKey, _lastCompositedBodyType, StringComparison.OrdinalIgnoreCase);
                bool charCodeChanged = !materialsIncoherent && newCharCodeKey != null &&
                    !string.Equals(newCharCodeKey, _lastCompositedCharCodes, StringComparison.OrdinalIgnoreCase);

                // Enabled body shapes can settle after the composite too. Independent of the material vetoes; only a failed (null)
                // read is untrustworthy, an empty map means "nothing enabled".
                var settledShapes = latest.Shapes;
                bool shapesChanged = settledShapes != null && !string.Equals(
                    BodyShapeSignature(settledShapes), _lastCompositedBodyShapeSig, StringComparison.Ordinal);

                if (bodyTypeChanged || charCodeChanged || shapesChanged)
                {
                    log.Debug("[Proteus] Post-settle correction: bodyType={0}→{1} charCode={2}→{3} "
                            + "shapesChanged={4} confirmed={5} incoherent={6}",
                        _lastCompositedBodyType ?? "none", newBodyTypeKey ?? "none",
                        _lastCompositedCharCodes ?? "none", newCharCodeKey ?? "none",
                        shapesChanged, confirmed, materialsIncoherent);

                    // Published per source: shapes may be settled while the material walk is mid-reload.
                    if (confirmed && !materialsIncoherent)
                    {
                        // Two agreeing readings: publish, so the corrective recomposite uses them (dirty stays false).
                        _activeMtrlSnapshot = snapshot;
                        _activeMtrlSnapshotDirty = false;
                        config.CachedActiveMaterialPaths = snapshot.ToList();
                    }
                    else
                    {
                        // Still moving at the cap, or incoherent: still worth a corrective composite, but mark dirty rather than publish an
                        // unverified read, and let the preamble's settle establish the truth.
                        _activeMtrlSnapshotDirty = true;
                    }
                    if (confirmed && settledShapes != null) _bodyShapeSnapshot = settledShapes;
                    TriggerRecomposite("post-settle-correction", force: false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
            catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { }
            finally { Volatile.Write(ref _postSettleCheckRunning, 0); }
        });
    }
}
