using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Penumbra.Api.Enums;

namespace Proteus.Services;

using static Proteus.Services.DrawnModelPaths;

public partial class CompositorService
{
    // ── Trigger ──────────────────────────────────────────────────────────────

    /// <summary>Colorset "glow" highlighter, cleared on recomposite (the shell may rebuild with a new letter).</summary>
    public Proteus.Interop.ColorTableHighlighter? Highlighter { get; set; }

    /// <summary>
    /// The Refresh button: re-resolves every base path and composites at once. The skin may be reused when its
    /// fingerprint is unchanged. <paramref name="full"/> (shift-click) forgets the published fingerprint so
    /// everything rebuilds.
    /// </summary>
    public void RefreshAndRecomposite(bool full = false)
    {
        InvalidateUpstreamCache(full ? "manual-full" : "manual");
        if (full)
        {
            _lastCompositeFingerprint = null;
            TriggerRecomposite("manual-full", 0);
        }
        else
            TriggerRecomposite("manual", 0, skinFingerprintAuthoritative: true, drawStateStable: true);
    }

    /// <summary>
    /// A shift-click Refresh the caller can await, for Copy Logs. Completes with the first result from a run that
    /// started after the trigger, or a null result on timeout. A cancelled run posts nothing, so the one that
    /// superseded it answers. <c>NotRun</c> says why no composite could start.
    /// </summary>
    public async Task<(CompositorResult? Result, string? NotRun)> FullRefreshAsync(TimeSpan timeout)
    {
        // The same early-outs as TriggerRecomposite, which would otherwise leave this waiting out the timeout.
        if (_disposed) return (null, "plugin unloading");
        if (!config.PluginEnabled) return (null, "Proteus is disabled");
        if (!penumbra.IsAvailable) return (null, "Penumbra is not available");

        // Any run already in Recomposite holds an epoch at or below this one; ours, and anything after it, is above.
        var startEpoch = Volatile.Read(ref _recompositeEpoch);
        var done = new TaskCompletionSource<CompositorResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnResult()
        {
            if (LastResult is { } r && r.Epoch > startEpoch) done.TrySetResult(r);
        }
        ResultChanged += OnResult;
        // Cancelled on the way out, so a refresh that finishes early doesn't leave the timeout's timer running.
        using var timeoutCts = new CancellationTokenSource();
        try
        {
            RefreshAndRecomposite(full: true);
            var finished = await Task.WhenAny(done.Task, Task.Delay(timeout, timeoutCts.Token)).ConfigureAwait(false);
            return (finished == done.Task ? done.Task.Result : null, null);
        }
        finally
        {
            timeoutCts.Cancel();
            ResultChanged -= OnResult;
        }
    }

    /// <summary>
    /// Manual escape hatch: drop every cached decoded texture, then recomposite immediately, for an edit that kept
    /// timestamp and byte length. Returns the number of cache entries dropped.
    /// </summary>
    public int ClearTextureCacheAndRecomposite()
    {
        int dropped = textureLoader.ClearCache();
        dropped += aoBlurCache.Clear();
        log.Information("[Proteus] Texture cache cleared manually ({0} entries) — recompositing.", dropped);
        // The fingerprint cannot see such a change either, so drop it too.
        _lastCompositeFingerprint = null;
        // Re-derive which file each base path resolves to, not just re-decode the remembered one.
        InvalidateUpstreamCache("clear-texture-cache");
        TriggerRecomposite("clear-texture-cache", 0);
        return dropped;
    }

    /// <summary>
    /// Re-walk the draw object and refresh the five equipped-model maps the second skin builds from. Returns
    /// whether the maps are populated afterwards (false only when no walk has ever succeeded).
    /// </summary>
    /// <remarks>
    /// Safe from a background thread: the draw-object IPC runs inside RunOnFrameworkThread.
    /// </remarks>
    private bool RefreshEquippedModels()
    {
        // Capture who the walk saw inside the framework call, with the paths, so the owner matches the models.
        var walk = Plugin.Framework.RunOnFrameworkThread(() =>
            (Paths: penumbra.GetActivePlayerModelPaths(),
             Owner: Plugin.ObjectTable.LocalPlayer?.Name.TextValue)).GetAwaiter().GetResult();
        return ApplyEquippedModels(walk.Paths, walk.Owner);
    }

    /// <summary>
    /// Publish one model walk into the five caches the second skin builds from. Returns whether the maps are
    /// populated afterwards. Separate so <see cref="WaitForDrawStateToSettle"/> can apply the sample its settle
    /// decision was made on.
    /// </summary>
    private bool ApplyEquippedModels(HashSet<string>? equipped, string? owner)
    {
        // Empty counts as failure, not "wearing nothing": a drawn character always has models, so empty means
        // mid-teardown and would wipe all five maps. Likewise a walk with no body part (HasBodySlotModel).
        if (equipped is { Count: > 0 } && HasBodySlotModel(equipped))
        {
            _equippedPartModels = EquippedPartModelsFromModels(equipped);
            _equippedAccessoryModels = EquippedAccessoryModelsFromModels(equipped);
            _equippedMetModels = EquippedMetModelsFromModels(equipped, InvisibleGlasses.FacewearModelSets(Plugin.DataManager));
            _bareBodyModels = BareBodyModelsFromModels(equipped);
            _humanPartModels = HumanPartModelsFromModels(equipped);
            NoteHairChange();
            // Keep the last known race on a walk with no human model; the owner check keeps it from crossing characters.
            UpdateDrawnRaceCode(equipped, owner);
        }
        return _equippedPartModels != null;
    }

    /// <param name="skinFingerprintAuthoritative">
    /// This trigger's skin effect is fully described by the skin fingerprint, so a forced run may still reuse the
    /// published skin when it matches. Set only where BuildCompositeFingerprint can see the change.
    /// </param>
    /// <param name="autoRedrawExempt">
    /// This ambient trigger runs even with auto redraw off (manual-mode gate only; RefreshPlayerTextures stays gated).
    /// </param>
    /// <param name="drawStateStable">
    /// The trigger came from Proteus's own UI, not the world, so the settle wait may accept a first reading that
    /// matches the last settled state. Never set after an equipment change, design apply or redraw.
    /// </param>
    public void TriggerRecomposite(string reason, int delayMs = 200, bool force = true,
        bool skinFingerprintAuthoritative = false, bool autoRedrawExempt = false, bool drawStateStable = false)
    {
        if (_disposed || !config.PluginEnabled || !penumbra.IsAvailable) return;

        // Auto redraw off: Proteus does nothing on its own initiative, so ambient (!force) triggers stop here. The
        // previous composite stays published. autoRedrawExempt covers a stale action (the carrier host decision),
        // not a stale look.
        if (!force && !config.AutoRedraw && !autoRedrawExempt)
        {
            log.Debug("[Proteus] Recomposite skipped ({0}) — auto redraw is off.", reason);
            return;
        }

        Highlighter?.Clear();

        // A forced composite cancelled by an ambient one must not be lost; cleared only when a composite publishes.
        if (force)
        {
            Interlocked.Exchange(ref _forcePending, 1);
            // The skin half of the latch: set only by a forced trigger whose skin effect the fingerprint cannot see.
            if (!skinFingerprintAuthoritative) Interlocked.Exchange(ref _skinForcePending, 1);
        }

        // Cancels whatever composite was pending and gives us the token for this one (see DebounceGate).
        var token = recompositeGate.Next();

        log.Debug("[Proteus] Recomposite triggered: {0} (delay {1}ms)", reason, delayMs);
        NoteRefreshTrigger(reason);
        var tTriggered = PhaseCounter.Begin();
        Task.Run(async () =>
        {
            try { await Task.Delay(delayMs, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            var tDebounced = PhaseCounter.Begin();

            // Read the Glamourer char code if still unknown, or the first composites after a load fingerprint differently
            // and cannot skip or reuse the skin.
            if (_glamourerCharCode == null)
            {
                try { Plugin.Framework.RunOnFrameworkThread(RefreshGlamourerCharCode).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { return; }
                catch (Exception ex) { log.Debug("[Proteus] early Glamourer char code read failed: {0}", ex.Message); }
            }

            // First, before anything reads the draw object: everything gathered must describe the same character, not a
            // mid-race-change mix. await is safe here: this task only completes on a pool thread.
            if (!await WaitForRaceToSettle(token).ConfigureAwait(false)) return;
            var tRaceSettled = PhaseCounter.Begin();

            // ...then wait for the draw object as a whole to stop moving (a design apply lands over several frames).
            // Its final sample supplies the model, shape and material reads, all from one frame.
            var settled = await WaitForDrawStateToSettle(token, drawStateStable).ConfigureAwait(false);
            if (settled is not { } state) return;
            var tDrawSettled = PhaseCounter.Begin();

            // Publish the equipped gear models every composite (equipping fires no event), from the settled sample.
            ApplyEquippedModels(state.Models, state.Owner);

            // Shape keys are read every composite (a mod toggle fires no redraw with the in-place reload). Only from a
            // usable sample: BodyShapeReader returns an empty map, not null, when the player isn't drawable.
            if (state.IsUsable && state.Shapes != null)
            {
                _bodyShapeSnapshot = state.Shapes;
                foreach (var (path, names) in state.Shapes)
                    log.Debug("[Proteus] body shapes enabled: {0} -> [{1}]",
                        SanitizeName(path), string.Join(", ", names));
            }

            // Some mods change active materials without a redraw, so refresh the snapshot when cold or flagged dirty
            // (not on every mask/colour toggle).
            if (_activeMtrlSnapshot == null || _activeMtrlSnapshotDirty)
            {
                bool wasDirty = _activeMtrlSnapshotDirty;
                log.Debug("[Proteus] Refreshing active-material snapshot (cold={0}, dirty={1})",
                    _activeMtrlSnapshot == null, wasDirty);

                // The settle loop already walked this. Framework calls below use GetResult(), not await: an await continuation
                // would run inline on the framework thread and drag the composite onto a frame.
                HashSet<string>? fresh = state.Materials;

                // A body mod changed but its materials load only on reload: force the reload up front and wait for the body
                // types to change, so we composite once. Skipped with auto redraw off (it is a redraw);
                // SchedulePostRedrawBodyTypeCheck is the backstop.
                if (wasDirty && fresh != null && config.AutoRedraw
                    && string.Equals(BodyTypeKey(fresh), _lastCompositedBodyType, StringComparison.OrdinalIgnoreCase))
                {
                    var beforeKey = _lastCompositedBodyType;
                    RefreshPlayerTextures(); // reload the character so the new body's materials load
                    for (int i = 0; i < 12; i++) // up to ~3s, then compose anyway (backstop covers misses)
                    {
                        try { await Task.Delay(250, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        HashSet<string>? next;
                        try { next = Plugin.Framework.RunOnFrameworkThread(penumbra.GetActivePlayerMaterialPaths).GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { return; }
                        if (next == null) break;
                        fresh = next;
                        if (!string.Equals(BodyTypeKey(next), beforeKey, StringComparison.OrdinalIgnoreCase))
                            break; // the reload landed the new body materials → settled
                    }
                }

                if (token.IsCancellationRequested) return;
                // Empty is a mid-teardown walk; keep the previous snapshot and the dirty flag so the next composite retries.
                if (fresh is { Count: > 0 })
                {
                    _activeMtrlSnapshot = fresh;
                    _activeMtrlSnapshotDirty = false;
                    // Under the lock like every other off-thread save; an unsynchronized Save can throw and silently drop the composite.
                    lock (_bodyModConfigLock)
                    {
                        config.CachedActiveMaterialPaths = fresh.ToList();
                        config.Save();
                    }
                }
            }
            Recomposite(token, force, skinFingerprintAuthoritative,
                new RefreshPreamble(tTriggered, tDebounced, tRaceSettled, tDrawSettled, PhaseCounter.Begin()));
        });
    }

    /// <summary>
    /// Wait for the snapshot to show the race Glamourer declares, so a race change costs one composite instead of
    /// two. The snapshot can be clean and still the old race's. Runs first in the caller;
    /// SchedulePostRedrawBodyTypeCheck is the backstop.
    /// </summary>
    /// <returns>False only if the wait was cancelled; the caller must not composite on a dead token.</returns>
    private async Task<bool> WaitForRaceToSettle(CancellationToken token)
    {
        var glamCode = _glamourerCharCode;
        var snapshot = _activeMtrlSnapshot;
        if (glamCode == null || snapshot == null) return true;

        var codes = CharCodeSet(snapshot);
        // No body materials at all means there is nothing to disagree with — not a stale snapshot.
        if (codes.Count == 0 || codes.Contains(glamCode)) { _unsettledRace = null; return true; }

        // Glamourer can display a race the draw object never adopts; trust an already waited-out pair until the memo expires.
        var codeKey = CharCodeKey(codes)!;   // non-null: the empty case returned above
        var pair = $"{glamCode}|{codeKey}";
        var memo = _unsettledRace;
        if (memo != null && memo.Pair == pair && Environment.TickCount64 < memo.ExpiresAtTick) return true;

        log.Debug("[Proteus] snapshot is mid-race-change (snapshot={0}, Glamourer displays {1}) — "
                + "waiting for the new race's materials before compositing", codeKey, glamCode);

        // Read before the wait: a dirty mark that arrives during it concerns materials this walk knows nothing about.
        bool wasDirty = _activeMtrlSnapshotDirty;

        int consecutiveNulls = 0;
        for (int i = 0; i < 12; i++) // up to ~3s, then composite anyway (the post-settle check covers misses)
        {
            // Treat teardown like cancellation: unload mid-poll raises ObjectDisposedException/InvalidOperationException,
            // which would fault an unobserved task.
            try { await Task.Delay(250, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { return false; }

            HashSet<string>? next;
            try { next = Plugin.Framework.RunOnFrameworkThread(penumbra.GetActivePlayerMaterialPaths).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { return false; }

            // A null walk is ambiguous: a race change destroys the draw object, but so does a loading screen. Tolerate a
            // redraw-sized gap (~1s) and give up once persistent. No memo either way.
            if (next == null)
            {
                if (++consecutiveNulls >= 4) return true;
                continue;
            }
            consecutiveNulls = 0;

            // Scanned rather than building a set per poll.
            if (!next.Any(m => UVRemapService.InferBodyType(m) != null
                            && glamCode.Equals(ExtractHumanCharCode(m), StringComparison.OrdinalIgnoreCase)))
                continue;

            // Settled: publish, unless a superseding trigger cancelled us (it would trust a stale snapshot).
            if (token.IsCancellationRequested) return false;
            _activeMtrlSnapshot = next;
            // Only clear dirty if it was clear at the start; a mid-wait mark's materials may still be loading.
            if (!wasDirty) _activeMtrlSnapshotDirty = false;
            // No lock: a bare reference swap with no Save.
            config.CachedActiveMaterialPaths = next.ToList();
            _unsettledRace = null;
            log.Debug("[Proteus] race settled to {0} after {1}ms — compositing once", glamCode, (i + 1) * 250);
            return true;
        }

        // Full window elapsed: most likely a display override rather than a slow load, hence the memo expiry.
        _unsettledRace = new UnsettledRace(pair, Environment.TickCount64 + UnsettledRaceMemoMs);
        log.Debug("[Proteus] race never settled to {0} within 3s — compositing on the snapshot as-is", glamCode);
        return true;
    }

    /// <summary>
    /// One framework-thread reading of every draw-object fact a composite is built from, in one visit so they
    /// describe the same frame.
    /// </summary>
    private readonly record struct DrawSample(
        HashSet<string>? Models,     // .mdl game paths  — the second skin's shell sources
        HashSet<string>? Materials,  // .mtrl game paths — body type and char code
        string? Owner,               // captured in the same visit; see ApplyEquippedModels
        IReadOnlyDictionary<string, HashSet<string>>? Shapes)
    {
        /// <summary>Null or empty is a teardown / loading-screen walk, not a character wearing nothing.</summary>
        public bool IsUsable => Models is { Count: > 0 } && Materials is { Count: > 0 } && HasBodySlotModel(Models);
    }

    private const int SettlePollMs        = 100;
    private const int SettleStableSamples = 2;      // two consecutive identical readings
    private const int SettleCapMs         = 1500;
    private const int SettleMaxBlankPolls = 5;      // ~500ms of blank walks → loading screen, stop waiting

    /// <summary>
    /// One framework visit for all three draw-object reads. A body-shape failure degrades to "shapes unknown".
    /// </summary>
    private DrawSample SampleDrawState()
        => Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            var (models, materials) = penumbra.GetActivePlayerResourcePaths();
            IReadOnlyDictionary<string, HashSet<string>>? shapes = null;
            try { shapes = Interop.BodyShapeReader.ReadEnabledShapes(player?.Address ?? 0); }
            catch (Exception ex) { log.Warning("[Proteus] body-shape read failed: {0}", ex.Message); }
            return new DrawSample(models, materials, player?.Name.TextValue, shapes);
        }).GetAwaiter().GetResult();

    /// <summary>
    /// Order-independent signature of a composite's shape from a sample in hand (no IPC). Uses
    /// <see cref="EquipSignature"/>, which excludes our own injected carriers so their flapping cannot block settling.
    /// </summary>
    private string DrawStateSignature(in DrawSample s)
        => string.Join('\n',
            EquipSignature(
                EquippedPartModelsFromModels(s.Models!),
                EquippedAccessoryModelsFromModels(s.Models!),
                EquippedMetModelsFromModels(s.Models!, InvisibleGlasses.FacewearModelSets(Plugin.DataManager)),
                BareBodyModelsFromModels(s.Models!),
                HumanPartModelsFromModels(s.Models!)),
            BodyTypeKey(s.Materials!)              ?? "-",
            CharCodeKey(CharCodeSet(s.Materials!)) ?? "-",
            BodyShapeSignature(s.Shapes));

    /// <summary>The reading the last composite settled on, so a trigger that expects nothing to have moved can
    /// accept a first reading that matches it.</summary>
    private string? _lastSettledDrawSig, _lastSettledDrawOwner;

    private async Task<DrawSample?> WaitForDrawStateToSettle(CancellationToken token, bool expectStable = false)
    {
        var started = Environment.TickCount64;
        DrawSample sample = default;
        string? prevSig = null, prevOwner = null;
        int agreements = 0, blanks = 0, polls = 0;

        while (true)
        {
            // Teardown gets the same treatment as cancellation (see WaitForRaceToSettle).
            try { sample = SampleDrawState(); }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { return null; }
            if (token.IsCancellationRequested) return null;
            polls++;

            if (!sample.IsUsable)
            {
                // Two blank walks must never AGREE with each other and settle the composite onto nothing.
                agreements = 0;
                prevSig = null;
                // A redraw-sized gap is normal; a persistent blank returns the blank sample, which every consumer ignores.
                if (++blanks >= SettleMaxBlankPolls) return sample;
            }
            else
            {
                blanks = 0;
                var sig = DrawStateSignature(sample);
                // Owner too: a walk that landed on a different character is not evidence about this one.
                bool sameOwner = string.Equals(sample.Owner, prevOwner, StringComparison.Ordinal);
                agreements = sameOwner && string.Equals(sig, prevSig, StringComparison.Ordinal) ? agreements + 1 : 1;
                prevSig = sig;
                prevOwner = sample.Owner;
                // A UI-driven trigger whose first reading matches the last settled state needs no second poll.
                if (expectStable && polls == 1
                    && string.Equals(sig, _lastSettledDrawSig, StringComparison.Ordinal)
                    && string.Equals(sample.Owner, _lastSettledDrawOwner, StringComparison.Ordinal))
                    return sample;
                if (agreements >= SettleStableSamples)
                {
                    _lastSettledDrawSig = sig;
                    _lastSettledDrawOwner = sample.Owner;
                    // Only log when something was actually in flight.
                    if (polls > SettleStableSamples)
                        log.Debug("[Proteus] draw state settled after {0}ms ({1} polls) — compositing once",
                                  Environment.TickCount64 - started, polls);
                    return sample;
                }
            }

            if (Environment.TickCount64 - started >= SettleCapMs)
            {
                log.Debug("[Proteus] draw state still moving after {0}ms — compositing on the latest reading "
                        + "(the post-redraw check remains the backstop)", Environment.TickCount64 - started);
                return sample;
            }

            try { await Task.Delay(SettlePollMs, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { return null; }
        }
    }
}
