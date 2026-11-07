using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Penumbra.Api.Enums;

namespace Proteus.Services;

using static Proteus.Services.DrawnModelPaths;

public partial class CompositorService
{
    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnModSettingChanged(ModSettingChange change, Guid collId, string modDir, bool inherited)
    {
        if (string.Equals(modDir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return;
        // The auto refit's own write: its composites wait for the batch's end. Read now, on the writer's thread.
        bool own = IsOwnRefitWrite(modDir);
        // Inside a batch write — the scope settles up when it closes. See SuppressModSettingEvents.
        if (_modSettingEventSuppression > 0)
            return;
        var playerColl = penumbra.GetPlayerCollectionId();
        if (playerColl == null || collId != playerColl.Value)
            return;

        // Proteus reloading a mod it just wrote into (live brush, Studio, auto refit, harness…) — see ExpectOwnModEdit.
        if (change == ModSettingChange.Edited && _ownModEditUntil.TryGetValue(modDir, out var ownEdit)
            && Environment.TickCount64 < ownEdit.Until)
        {
            log.Debug(OwnEditLine(modDir, ownEdit.By));
            return;
        }

        // Setting, Priority and Edited cannot turn a mod on or off, so a disabled mod stays contributing nothing:
        // skip the handler. Ahead of the _knownDisabled gate because the change kind alone proves no transition.
        // The priority is still folded into LastDiscovered, which compares it.
        if (change is ModSettingChange.Setting or ModSettingChange.Priority or ModSettingChange.Edited
            && penumbra.GetModSettings(playerColl.Value, modDir) is { Enabled: false } off)
        {
            NoteSkippedDisabled(modDir, off.Priority);
            log.Debug("[Proteus] ModSettingChanged:{0}:{1} on a disabled mod — no recomposite", change, modDir);
            return;
        }

        // A disabled mod contributes nothing, so skip changes to it, including the upstream invalidation below.
        // Only skip mods already acted on while off (_knownDisabled), so the disable transition itself gets through.
        if (_knownDisabled.ContainsKey(modDir))
        {
            var live = penumbra.GetModSettings(playerColl.Value, modDir);
            if (live is { Enabled: false })
            {
                NoteSkippedDisabled(modDir, live.Value.Priority);
                return;
            }
            // Back on, or no longer readable: the recorded verdict no longer holds, so treat this as a real change.
            _knownDisabled.TryRemove(modDir, out _);
        }

        var sidecar = HasSidecar(modDir);

        // Before the early returns: any non-overlay mod can change which file a base path resolves to, and that must
        // be recorded even without a recomposite. Sidecar toggles and mods classified non-body are exempt, since
        // flushing costs a re-derivation that briefly unpublishes our redirects. See MayMoveOurBases.
        if (!sidecar && MayMoveOurBases(modDir))
            InvalidateUpstreamCache($"ModSettingChanged:{change}:{modDir}");

        // For enable/disable on our own overlay mods, re-check whether the active set actually changed: Glamourer
        // re-enables already-enabled mods after each redraw, which would otherwise loop RedrawPlayer() → this.
        if (sidecar && change is ModSettingChange.EnableState or ModSettingChange.MultiEnableState)
        {
            // Remapped like the list it is compared with: a discovery from before a rename is no change.
            var current = Ledgered(discovery.DiscoverAll());
            if (current.Count == 0) return;
            if (DiscoveredSetsEqual(current, LastDiscovered)) return;
            LastDiscovered = current;
        }
        else if (change == ModSettingChange.TemporarySetting)
        {
            // Glamourer re-applies temporary mod settings after our redraws; suppress triggers within 1500ms of our own
            // redraw. Applies to overlay and body mods alike.
            var msSince = unchecked(Environment.TickCount64 - Interlocked.Read(ref _lastOwnRedrawTick));
            if (msSince >= 0 && msSince < 1500)
            {
                // A Glamourer design that also picks a body's chest lands here too: look at the shapes, off this thread.
                if (ShellBodyAtr.Watches(_lastBodyAtrUses, modDir))
                    CheckBodyShapesOffThread(modDir, $"ModSettingChanged:{change}:{modDir}", own);
                return;
            }
        }

        // Everything below acts, so this is where a mod joins _knownDisabled: the set means "a composite was kicked
        // off with this mod off", which the early returns above do not guarantee.
        if (penumbra.GetModSettings(playerColl.Value, modDir) is { Enabled: false })
            _knownDisabled[modDir] = 0;

        // TemporarySetting is the only kind Glamourer re-asserts by itself after every redraw; every other kind
        // is deliberate and must always composite.
        bool ambient = change == ModSettingChange.TemporarySetting;

        if (sidecar)
        {
            // The mod's files may have changed underneath a cached decode (a reinstall/edit that kept the
            // same timestamp or byte length); drop this mod's cached textures so the composite re-reads them.
            textureLoader.EvictMod(modDir);
            TriggerRecomposite($"ModSettingChanged:{change}:{modDir}", force: !ambient, deferWhileHeld: own);
            return;
        }

        // Not an overlay mod: the only other thing we react to is a body mod, whose change can leave the cached
        // snapshot wrong. Its detection reads the mod's files, so run it off the framework thread.
        EvaluateSurfaceModOffThread(modDir, $"ModSettingChanged:{change}:{modDir}", force: !ambient,
            checkBasesMoved: true, deferWhileHeld: own);
    }

    /// <summary>
    /// Fold a skipped disabled mod's current priority and enabled state into <see cref="LastDiscovered"/>, which
    /// <see cref="DiscoveredSetsEqual"/> compares; otherwise a stale priority later fires a needless recomposite.
    /// No-op for non-overlay mods. Copy-on-write, keeping priority-ascending order.
    /// </summary>
    private void NoteSkippedDisabled(string modDir, int priority)
    {
        var snapshot = LastDiscovered;
        var idx = snapshot.FindIndex(e =>
            string.Equals(e.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        var entry = snapshot[idx];
        if (entry.Priority == priority && !entry.Enabled) return;

        var updated = new List<OverlayEntry>(snapshot);
        updated[idx] = entry with { Priority = priority, Enabled = false };
        updated.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        LastDiscovered = updated;
    }

    /// <summary>
    /// Could a settings change on this mod move a file we read as a composite base? Answers without disk I/O
    /// (framework-thread event). Unknown answers true; only a mod already classified as not affecting the
    /// composite is exempt. A stale verdict is caught by <see cref="EvaluateSurfaceModOffThread"/>.
    /// </summary>
    /// <remarks>
    /// Tests AffectsComposite, not IsSurfaceMod: the question is "could it move a base we read".
    /// </remarks>
    private bool MayMoveOurBases(string modDir)
        => !_bodyModCache.TryGetValue(modDir, out var cached) || cached.AffectsComposite;

    private void InvalidateUpstreamCache(string reason)
    {
        // Does not clear _lastCompositeFingerprint: Glamourer's zone-in re-asserts would blind the unchanged-inputs
        // gate. A base that really moved shows up in the upstream identity the fingerprint carries.
        // Retry marks and _upstreamSettled are cleared with the memo; the latter would otherwise outrank live resolves.
        _upstreamUnsettled.Clear();
        _upstreamSettled.Clear();
        _upstreamIsGameData.Clear();
        _texGameDataSeen.Clear();   // #70: a texture settles as game data only on two fresh primes after a change
        if (_upstreamByGamePath.IsEmpty) return;
        _upstreamByGamePath.Clear();
        log.Debug("[Proteus] upstream cache cleared ({0})", reason);
    }

    /// <summary>Whether a mod installed is news for the bases a composite reads: any mod but Proteus's own output.</summary>
    internal static bool ModAddedInvalidatesUpstreams(string modDir)
        => !string.Equals(modDir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase);

    private void OnModAdded(string modDir)
    {
        // A folder in use again: nothing is remapped away from it any more.
        _moveLedger.Forget(modDir);
        // A mod the auto refit just made: its composites wait for the batch's end.
        bool own = IsOwnRefitWrite(modDir);
        // A (re)install almost always rewrites the mod's files — evict any stale cached decodes for it.
        textureLoader.EvictMod(modDir);
        // Our own output mod, registered by a composite (EnsureManagedModExists): never a base, and the upstreams
        // remembered point into other mods. Clearing them here would leave a rebuild that a renamed copy of this mod
        // still masks (see AddManagedAlias) nothing but game data to stand on.
        if (!ModAddedInvalidatesUpstreams(modDir)) return;
        InvalidateUpstreamCache($"ModAdded:{modDir}");
        if (HasSidecar(modDir))
        {
            TriggerRecomposite($"ModAdded:{modDir}", deferWhileHeld: own);
            return;
        }
        // A reinstalled body mod may change files without renaming; ClassifySurfaceMod's fingerprint handles that.
        EvaluateSurfaceModOffThread(modDir, $"ModAdded:{modDir}", deferWhileHeld: own);
    }

    private void OnModDeleted(string modDir)
    {
        // Files are already gone, so use the last-known classification and then drop it. The wide verdict says
        // whether the material snapshot went stale; only the narrow one justifies a rebuild. Unknown mods answer false.
        var known       = _bodyModCache.TryGetValue(modDir, out var cached);
        var wasSurface  = known && cached.IsSurfaceMod;
        var wasComposed = known && cached.AffectsComposite;

        // Deleting a mod that contributed nothing must not cost a rebuild, redraw or upstream-cache drop.
        // Asked before the bookkeeping below, which erases the state the answer comes from.
        var contributedNothing = KnownContributingNothing(modDir, wasComposed);
        var wasDiscovered = LastDiscovered.Any(e =>
            string.Equals(e.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase));

        // Whether or not we rebuild, a deleted mod must leave the overlay list (the UI and DiscoveredSetsEqual read it).
        ForgetDiscovered(modDir);
        // A reinstall keeps the name; a remembered "was disabled" must not suppress the new install's first event.
        _knownDisabled.TryRemove(modDir, out _);

        // Drop the cached classification; the change takes the config's gate, the save goes to the framework thread.
        Task.Run(() =>
        {
            _bodyModCache.TryRemove(modDir, out _);
            bool removed;
            lock (config.Gate) removed = config.KnownBodyMods.Remove(modDir);
            if (removed) config.Save();
        });

        if (contributedNothing)
        {
            // Information, not Debug: this is the line that explains an absent recomposite.
            log.Information("[Proteus] ModDeleted:{0}: mod was contributing nothing to the current build "
                          + "— no recomposite", modDir);
            return;
        }

        if (wasSurface || wasComposed) _activeMtrlSnapshotDirty = true;
        InvalidateUpstreamCache($"ModDeleted:{modDir}");

        // Read from the snapshot taken above: ForgetDiscovered has already removed the entry from LastDiscovered.
        if (!wasDiscovered && !wasComposed)
            return;
        TriggerRecomposite($"ModDeleted:{modDir}");
    }

    /// <summary>
    /// Do we positively know this mod was contributing nothing to the build the character wears? Answered from
    /// memory, since the files are gone. Unknown answers false.
    /// </summary>
    private bool KnownContributingNothing(string modDir, bool wasComposed)
    {
        // One of our overlay mods, recorded as disabled; LastDiscovered carries the enabled flag even for a mod
        // off since before Proteus loaded.
        var discovered = LastDiscovered;   // one read: the list is replaced wholesale by other threads
        var idx = discovered.FindIndex(e =>
            string.Equals(e.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) return !discovered[idx].Enabled;

        // Last observed disabled (OnModSettingChanged removes the entry once the mod reads enabled).
        if (_knownDisabled.ContainsKey(modDir)) return true;

        // A base-providing mod that supplied none of the files the last composite read (disabled or outranked):
        // deleting it moves nothing. Mods never classified as base-providing are left to the !wasComposed path below.
        if (wasComposed && _lastBaseUpstreams is { } upstreams)
            return !upstreams.Values.Any(v =>
                string.Equals(ModFolderOf(v), modDir, StringComparison.OrdinalIgnoreCase));

        return false;
    }

    /// <summary>
    /// Remove a mod from <see cref="LastDiscovered"/>. Copy-on-write, as in <see cref="NoteSkippedDisabled"/>.
    /// </summary>
    private void ForgetDiscovered(string modDir)
    {
        var snapshot = LastDiscovered;
        var idx = snapshot.FindIndex(e =>
            string.Equals(e.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        var updated = new List<OverlayEntry>(snapshot);
        updated.RemoveAt(idx);
        LastDiscovered = updated;
    }

    /// <summary>
    /// Did a settings change on this mod move any base this composite reads? Re-resolves the paths it supplies
    /// that are also our bases and compares each against what the last composite read. Second half of
    /// <see cref="ClassifySurfaceMod"/> (could it matter → did it).
    /// Everything unknown answers true; only "resolves to the same known file" may skip. Compares paths, not
    /// contents: in-place edits arrive as ModAdded/ModDeleted.
    /// </summary>
    private bool ModSettingMovedABase(IReadOnlyCollection<string> suppliedPaths, out string detail)
    {
        var known = _lastBaseUpstreams;
        var bases = _compositeBaseKeys;
        if (known == null || bases == null) { detail = "no composite to compare against"; return true; }

        var shared = 0;
        foreach (var p in suppliedPaths)
        {
            if (!bases.Paths.Contains(p)) continue;
            shared++;

            if (!known.TryGetValue(p, out var before))
            {
                detail = p + " was not resolved by the last composite";
                return true;
            }

            string? now;
            try { now = penumbra.ResolvePlayer(p); }
            catch { detail = p + " could not be resolved"; return true; }

            if (now == null || IsOwnOutput(now))
            {
                detail = p + " resolves to our own output, so its upstream is not observable";
                return true;
            }

            var a = TryCanonicalise(now);
            var b = TryCanonicalise(before);
            if (a == null || b == null || !string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                detail = p + ": " + before + " -> " + now;
                return true;
            }
        }

        // Reached only when every shared path re-resolved to the file the last composite read.
        detail = shared + " shared base path(s) still resolve to the same file";
        return false;
    }

    /// <summary>What a settings change on a body mod comes to.</summary>
    internal enum SettingVerdict { Ignore, Recomposite }

    /// <param name="Force">The composite skips the unchanged-inputs gate.</param>
    /// <param name="InvalidateUpstreams">A base moved (or may have): drop the remembered upstreams first.</param>
    /// <param name="ScheduleRecheck">No base moved yet: look again once Penumbra has finished rebuilding.</param>
    internal readonly record struct SettingDecision(SettingVerdict Verdict, bool Force, bool InvalidateUpstreams,
                                                    bool ScheduleRecheck);

    /// <summary>
    /// The decision <see cref="EvaluateSurfaceModOffThread"/> acts on. A mod providing none of our bases, or one whose
    /// bases did not move, is ignored as before — unless the shapes the published shell's body draws by its options
    /// moved (<paramref name="shapesMoved"/>, NIRÆ's chest), which no fingerprint sees: then a forced composite. A
    /// settings change that has not moved a base yet still gets its re-check, since a file can move late.
    /// </summary>
    /// <param name="surface">Whether the mod has a surface tree; it decides nothing here, kept for the record.</param>
    internal static SettingDecision DecideSettingChange(bool surface, bool affects, bool checkBasesMoved, bool baseMoved,
                                                        bool shapesMoved, bool force)
    {
        _ = surface;
        if (!affects)
            return shapesMoved
                ? new SettingDecision(SettingVerdict.Recomposite, true, false, false)
                : new SettingDecision(SettingVerdict.Ignore, false, false, false);
        if (checkBasesMoved && !baseMoved)
            return new SettingDecision(shapesMoved ? SettingVerdict.Recomposite : SettingVerdict.Ignore, shapesMoved,
                                       false, true);
        return new SettingDecision(SettingVerdict.Recomposite, force || shapesMoved, true, false);
    }

    /// <summary>
    /// The unchanged-inputs gate: skip when nothing forced the composite and its inputs hash to what is published, and
    /// the shapes the published shell's body draws by its options have not moved. <paramref name="shapesMoved"/> is
    /// asked last, only when everything else says skip: it reads Penumbra.
    /// </summary>
    internal static bool SkipUnchanged(bool force, bool forcePending, string? lastFingerprint, string fingerprint,
                                       Func<bool> shapesMoved)
        => !force && !forcePending && lastFingerprint != null && fingerprint == lastFingerprint && !shapesMoved();

    /// <summary>
    /// Force a rebuild when the shapes the published shell's body draws by its options have changed since — of
    /// <paramref name="modDir"/>, or of any watched mod when null — and that change has not been forced already. Reads
    /// Penumbra on the framework thread and blocks: call from a pool thread only.
    /// </summary>
    private bool TryForceForShapes(string? modDir, string reason, bool deferWhileHeld = false)
    {
        if (!ShellBodyAtr.Moved(_lastBodyAtrUses, modDir, secondSkin.PlayerModSettings, out var why, out var target))
            return false;
        if (!_bodyAtrLatch.TryArm(target))
        {
            log.Debug("[Proteus] {0}: the body's switched shapes changed ({1}) but a rebuild was already forced for "
                      + "that — not again", reason, why);
            return false;
        }
        log.Information("[Proteus] {0}: the shapes this body draws by its options changed ({1}) — recompositing",
            reason, why);
        TriggerRecomposite(reason, force: true, deferWhileHeld: deferWhileHeld);
        return true;
    }

    /// <summary>
    /// A watched body mod's settings changed inside the window where its event would be dropped: compare its shapes off
    /// the event thread, counted as an evaluation so a draining auto refit batch waits for it.
    /// </summary>
    private void CheckBodyShapesOffThread(string modDir, string reason, bool deferWhileHeld)
    {
        NoteEvaluating(modDir, +1);
        Task.Run(() =>
        {
            try
            {
                TryForceForShapes(modDir, reason, deferWhileHeld);
            }
            catch (Exception ex)
            {
                log.Debug("[Proteus] {0}: body-shapes check failed: {1}", reason, ex.Message);
            }
            finally
            {
                NoteEvaluating(modDir, -1);
            }
        });
    }

    /// <param name="deferWhileHeld">The change is an auto refit batch's own write: its composite, re-check and stale
    /// snapshot wait for the batch's end (see <see cref="RedrawHold"/>).</param>
    private void EvaluateSurfaceModOffThread(string modDir, string reason, bool force = true,
        bool checkBasesMoved = false, bool deferWhileHeld = false)
    {
        // Counted before the task starts, so a draining batch that asks right after its write sees it.
        NoteEvaluating(modDir, +1);
        Task.Run(() =>
        {
            try
            {
                // A body drawing one of several shapes by its options (NIRÆ's chest): a choice that moves no file still
                // changes the shell. Compared with what the published shell left out; forced at most once per change.
                bool shapesMoved = false;
                string shapesWhy = "";
                try
                {
                    // A deliberate change (not Glamourer re-asserting) to a watched mod: anything given up on gets one more try.
                    if (force && ShellBodyAtr.Watches(_lastBodyAtrUses, modDir)) _bodyAtrLatch.ClearExhausted();
                    shapesMoved = ShellBodyAtr.Moved(_lastBodyAtrUses, modDir, secondSkin.PlayerModSettings,
                                                     out shapesWhy, out var shapesTarget)
                                  && _bodyAtrLatch.TryArm(shapesTarget);
                }
                catch (Exception ex)
                {
                    log.Debug("[Proteus] {0}: body-shapes check failed: {1}", reason, ex.Message);
                    shapesMoved = false;
                }

                var (surface, affects) = ClassifySurfaceMod(modDir);
                if (surface || affects)
                {
                    textureLoader.EvictMod(modDir);   // body textures may have changed under a cached decode
                    // Either verdict earns the snapshot re-walk: a face/hair/iris mod moves which materials are on the character,
                    // and a mod feeding us without a surface tree (e.g. an append host) can change the materials a model references.
                    MarkMaterialsDirty(owedWhileHeld: deferWhileHeld);
                }

                // Could it matter is settled; now ask whether it did. Settings changes only: ModAdded rewrites files wholesale.
                // The manifest scan is repeated because ClassifySurfaceMod answers from a fingerprint cache without paths.
                IReadOnlyCollection<string>? supplied = null;
                bool baseMoved = true;
                string why = "";
                if (affects && checkBasesMoved)
                {
                    supplied = ScanModManifests(Path.Combine(modsRoot, modDir)).Paths;
                    baseMoved = ModSettingMovedABase(supplied, out why);
                }

                var d = DecideSettingChange(surface, affects, checkBasesMoved, baseMoved, shapesMoved, force);

                // Information, not Debug: these lines explain an absent (or an unexpected) recomposite.
                if (shapesMoved)
                    log.Information("[Proteus] {0}: the shapes this body draws by its options changed ({1}) — "
                                  + "recompositing", reason, shapesWhy);
                if (surface && !affects && !shapesMoved)
                    // The gate: a mod that provides none of our base paths cannot change the output, and forcing a composite
                    // latches _forcePending, defeating the unchanged-inputs gate next time. The base-set size makes a false
                    // negative diagnosable.
                    log.Information("[Proteus] {0}: mod provides none of the {1} base path(s) this composite "
                                  + "reads — no recomposite",
                        reason, _compositeBaseKeys?.Paths.Count ?? 0);
                if (d.ScheduleRecheck)
                {
                    if (!shapesMoved)
                        log.Information("[Proteus] {0}: settings changed but no base moved ({1}) — no "
                                      + "recomposite (re-checking in {2}ms)", reason, why, MovedBaseRecheckMs);
                    ScheduleMovedBaseRecheck(modDir, supplied!, reason, force, deferWhileHeld: deferWhileHeld);
                }
                else if (affects && checkBasesMoved)
                    log.Debug("[Proteus] {0}: a base moved ({1})", reason, why);

                if (d.Verdict == SettingVerdict.Ignore) return;

                // The authoritative invalidation: MayMoveOurBases answered from a possibly stale cached verdict, and
                // ClassifySurfaceMod has just re-derived it. Idempotent.
                if (d.InvalidateUpstreams) InvalidateUpstreamCache(reason);
                // Safe to gate on an ambient re-assert: the fingerprint's upstream identity detects a moved body mod.
                TriggerRecomposite(reason, force: d.Force, deferWhileHeld: deferWhileHeld);
            }
            catch (Exception ex)
            {
                log.Error(ex, "[Proteus] Body-mod evaluation failed for {0}", modDir);
            }
            finally
            {
                // After any hand-off to the re-check, which BodyEvaluationPending also reads.
                NoteEvaluating(modDir, -1);
            }
        });
    }

    /// <summary>
    /// Ask <see cref="ModSettingMovedABase"/> once more after Penumbra has finished rebuilding the collection
    /// (see <see cref="MovedBaseRecheckMs"/>), and composite if the answer changed. One in flight per mod.
    /// </summary>
    /// <param name="deferWhileHeld">From an auto refit batch's own write: held with the batch (see <see cref="RedrawHold"/>).</param>
    private void ScheduleMovedBaseRecheck(string modDir, IReadOnlyCollection<string> supplied,
                                          string reason, bool force, bool deferWhileHeld)
    {
        if (!_pendingMoveRecheck.TryAdd(modDir, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(MovedBaseRecheckMs).ConfigureAwait(false);
                if (_disposed || !config.PluginEnabled) return;

                // The path set is still good; the baseline may have been republished by another composite, and comparing
                // against that is right since it is what the character wears.
                if (!ModSettingMovedABase(supplied, out var why)) return;

                log.Information("[Proteus] {0}: a base moved after all ({1}) — recompositing", reason, why);
                InvalidateUpstreamCache(reason);
                // The batch it came from has already let go: say so, it costs a composite of its own.
                long sinceRelease = Environment.TickCount64 - Interlocked.Read(ref _lastHoldReleaseTick);
                if (deferWhileHeld && !redrawHold.IsHeld && sinceRelease < LateRecheckMs)
                    log.Warning("{0}", RedrawLedger.LateRecheck(reason, sinceRelease));
                TriggerRecomposite(reason, force: force, deferWhileHeld: deferWhileHeld);
            }
            catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { }
            catch (Exception ex) { log.Error(ex, "[Proteus] moved-base re-check failed for {0}", modDir); }
            finally { _pendingMoveRecheck.TryRemove(modDir, out _); }
        });
    }

    /// <summary>
    /// Resolve the models the second skin cuts its shells from, so they are recorded as composite bases.
    /// SecondSkinService resolves them directly and <see cref="PrimeUpstreamCache"/> excludes gear paths, so
    /// without this <see cref="ModSettingMovedABase"/> cannot see an outfit option that moves a shell source.
    /// Runs after the gear phase against a settled collection; our manifest never publishes these paths.
    /// </summary>
    private void RecordShellSourceUpstreams()
    {
        try
        {
            // Bare-body and human models too: an empty slot is cut from the bare body, an iris shell from the face model.
            foreach (var p in ShellSourceModelPaths()) ResolveUpstream(p, bookkeeping: true);
        }
        catch (Exception ex)
        {
            // Never fatal: bookkeeping for the next settings change; losing it costs one over-eager recomposite.
            log.Debug("[Proteus] recording shell source upstreams failed: {0}", ex.Message);
        }
    }

    /// <summary>Every model game path the second skin may read as shell geometry, deduplicated.</summary>
    private IEnumerable<string> ShellSourceModelPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p) { if (!string.IsNullOrEmpty(p)) seen.Add(p!); }

        if (_equippedPartModels is { } parts) foreach (var p in parts.Values) Add(p);
        if (_bareBodyModels is { } bare) foreach (var p in bare.Values) Add(p);
        if (_humanPartModels is { } human) foreach (var p in human) Add(p);
        return seen;
    }

    private void OnPenumbraReady()
    {
        // Penumbra is back: the boot probe looks again soon rather than at the end of a long back-off.
        _boot.ResetBackoff(Environment.TickCount64);
        modsRoot      = penumbra.GetModDirectory() ?? string.Empty;
        managedModDir = Path.Combine(modsRoot, SidecarDiscoveryService.ManagedModDir);
        // Now that the mod directory is resolvable, make sure the bundled starter effects are present.
        discovery.SeedDefaultEffects();
        if (!config.PluginEnabled) return;
        // Same hold as OnBootPoll: compositing before the design-binding restore's overrides means doing it twice.
        if (BootCompositeHold) return;
        // Only trigger if discovery already sees mods: PenumbraReady can fire before settings are readable, and an
        // empty result would wipe the existing output. ModSettingChanged/ModAdded fire the first real composite.
        if (discovery.DiscoverEnabled().Count > 0)
            TriggerRecomposite("PenumbraReady", force: false);
    }

    private void OnPlayerCollectionChanged()
    {
        // The player's collection changed; everything is collection-scoped, so force one full walk.
        _boot.ResetBackoff(Environment.TickCount64);
        _activeMtrlSnapshotDirty = true;
        InvalidateUpstreamCache("collection-changed");
        // Enabled state is collection-scoped, so every remembered disabled verdict is now for the wrong collection.
        _knownDisabled.Clear();
        if (!config.PluginEnabled) return;
        TriggerRecomposite("collection-changed", force: false);
    }

    // Framework-thread redraw hook: the cheap common-case snapshot refresh. Only write a non-empty walk; a
    // null or empty result is the mid-redraw teardown and would clear a valid snapshot.
    private void OnLocalPlayerRedrawn()
    {
        log.Information("{0}", redrawLedger.Observed());
        NoteRedrawObserved();
        // One walk of the resource tree for both sets: this runs on the framework thread after every redraw. Counted beside
        // the redraws, so the idle I/O numbers show one read per redraw.
        Io?.Count(IoCounters.Redraws);
        Io?.Count(IoCounters.RedrawTreeReads);
        var (modelPaths, snapshot) = penumbra.GetActivePlayerResourcePaths();
        bool equipChanged = false;
        bool modelWalkOk = false;   // a draw object we actually read — see the carrier reconcile below
        if (snapshot is { Count: > 0 })
        {
            _activeMtrlSnapshot = snapshot;
            _activeMtrlSnapshotDirty = false;
            _activeMtrlSnapshotRewalk = false;
            // In-memory only; persisted by TriggerRecomposite's next config.Save().
            config.CachedActiveMaterialPaths = snapshot.ToList();

            // Did the gear the second skin cuts its shells from change? Equipping fires no mod or design event.
            // Guarded on its own null/empty: a walk without models must not wipe the caches and report a phantom unequip.
            // Empty is the same failure as null. A walk with no body part (face/hair loaded, body not yet) must not
            // update the maps, but the carrier reconcile below still runs: it is what puts back carriers this redraw removed.
            if (modelPaths is { Count: > 0 }) modelWalkOk = true;
            if (modelPaths is { Count: > 0 } && HasBodySlotModel(modelPaths))
            {
                var equipped = EquippedPartModelsFromModels(modelPaths);
                var accessories = EquippedAccessoryModelsFromModels(modelPaths);
                var metModels = EquippedMetModelsFromModels(modelPaths, InvisibleGlasses.FacewearModelSets(Plugin.DataManager));
                var bare = BareBodyModelsFromModels(modelPaths);
                var humanParts = HumanPartModelsFromModels(modelPaths);
                _equippedPartModels = equipped;
                _equippedAccessoryModels = accessories;
                NoteAccessoryWalk();
                _equippedMetModels = metModels;
                _bareBodyModels = bare;
                _humanPartModels = humanParts;
                NoteHairChange();
                // Framework thread (this is the redraw hook), so the owner can be read inline.
                UpdateDrawnRaceCode(modelPaths, Plugin.ObjectTable.LocalPlayer?.Name.TextValue);
                var sig = EquipSignature(equipped, accessories, metModels, bare, humanParts);
                equipChanged = _lastEquipSignature != null && !string.Equals(_lastEquipSignature, sig, StringComparison.Ordinal);
                _lastEquipSignature = sig;
            }
        }

        RefreshGlamourerCharCode();

        // Put the carriers back now: they are equipped with ApplyFlag.Once, so this redraw reverted them and the shell
        // has no host. Idempotent; off-thread (framework-thread IPC, Lumina reads). Skipped while a composite runs.
        if (config.PluginEnabled && modelWalkOk && _secondSkinActive
            && Volatile.Read(ref _compositesInFlight) == 0)
        {
            var (gearWanted, shellBuilt, onFacewear, ringSlot) =
                (_lastGearWanted, _lastShellBuilt, _lastShellOnFacewear, _lastShellCarrierSlots);
            Task.Run(() =>
            {
                try
                {
                    ReconcileInvisibleGlasses(gearWanted, shellBuilt, onFacewear, alreadyHosted: true);
                    ReconcileEmperorRing(gearWanted, shellBuilt, ringSlot);
                }
                catch (Exception ex) { log.Debug("[Proteus] carrier reconcile on redraw failed: {0}", ex.Message); }
            });
        }

        if (equipChanged && config.PluginEnabled)
            // Exempt from the manual-mode gate while a shell stands: this recomputes which item should carry it.
            TriggerRecomposite("equipment-change", force: false,
                autoRedrawExempt: _secondSkinActive);

        if (Interlocked.Exchange(ref _pendingCustomizationRecomposite, 0) == 1)
            TriggerRecomposite("glamourer-customization", force: false);
    }

    /// <summary>
    /// Glamourer moved an equipped item or a pair of glasses. Ambient: bursts collapse in the debounce and the
    /// unchanged-inputs gate decides; an equip can change the skin too (bare foot models), so it is not narrowed here.
    /// </summary>
    private void OnGlamourerEquipmentChanged()
    {
        if (!config.PluginEnabled) return;
        TriggerRecomposite("glamourer-equip", force: false);
    }

    private void OnGlamourerCustomizationChanged()
    {
        if (!config.PluginEnabled) return;
        // Read the Glamourer-displayed char code now (framework thread), before it changes again.
        RefreshGlamourerCharCode();
        // Don't recomposite yet: the snapshot still has the old race. OnLocalPlayerRedrawn fires it once fresh.
        Interlocked.Exchange(ref _pendingCustomizationRecomposite, 1);
        // Fallback: if GameObjectRedrawn never fires (e.g. redraw suppressed), trigger anyway.
        _ = Task.Delay(2000).ContinueWith(_ =>
        {
            if (Interlocked.Exchange(ref _pendingCustomizationRecomposite, 0) == 1)
                TriggerRecomposite("glamourer-customization-timeout", force: false);
        });
    }

    // Framework thread only. Caches the char code Glamourer displays so the background recomposite needs no IPC.
    private void RefreshGlamourerCharCode()
    {
        try
        {
            var state = glamourer.GetObjectState(0);
            var cust  = state?["Customize"];
            if (cust == null) { _glamourerCharCode = _glamourerFaceCode = null; return; }
            // No zero defaults: Gender 0 is male, so a failed read would produce a plausible wrong char code.
            // Unknown stays null and falls back to _lastCompositedCharCodes, read off the drawn body materials.
            var race  = cust["Race"]?["Value"]?.ToObject<byte>();
            var tribe = cust["Clan"]?["Value"]?.ToObject<byte>();
            var sex   = cust["Gender"]?["Value"]?.ToObject<byte>();
            if (race == null || tribe == null || sex == null)
            {
                Plugin.Log.Warning("[Proteus] Glamourer customize is incomplete (race={0}, clan={1}, "
                                 + "gender={2}) — treating the char code as unknown rather than guessing",
                    race?.ToString() ?? "missing", tribe?.ToString() ?? "missing", sex?.ToString() ?? "missing");
                _glamourerCharCode = _glamourerFaceCode = null;
                return;
            }
            _glamourerCharCode = BodyCodeFromCustomize(race.Value, tribe.Value, sex.Value);
            _glamourerFaceCode = FaceCodeFromCustomize(race.Value, tribe.Value, sex.Value);
        }
        catch { _glamourerCharCode = _glamourerFaceCode = null; }
    }

    private void OnGlamourerStateChanged()
    {
        // Suppress the echo of our own ReapplyState call within a short window.
        var msSinceReapply = unchecked(Environment.TickCount64 - Interlocked.Read(ref _lastOwnReapplyTick));
        if (msSinceReapply >= 0 && msSinceReapply < 250) return;
        if (Environment.TickCount64 < Interlocked.Read(ref _glamourerEchoUntil)) return;   // see ReloadGearInPlace

        // Invisible glasses need no bookkeeping here: ownership is derived from the worn set (ReconcileInvisibleGlasses).

        // Glamourer applied a design / reset / reapplied state on the local player.
        // Diff the discovered set against the last known set — only recomposite if something changed.
        var current = Ledgered(discovery.DiscoverAll());

        // Empty means Penumbra's IPC is temporarily unavailable: no information, not no mods.
        if (current.Count == 0) return;

        if (DiscoveredSetsEqual(current, LastDiscovered)) return;

        // Update LastDiscovered before triggering, or repeated events cancel the run before it updates the list and loop.
        LastDiscovered = current;
        TriggerRecomposite("glamourer-design", force: false);
    }

    // Extracts the human character code (e.g. "c0101") from a path like
    // "chara/human/c0101/obj/body/...". Returns null for non-human paths.
    internal static string? ExtractHumanCharCode(string path)
    {
        const string prefix = "chara/human/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        int slash = path.IndexOf('/', prefix.Length);
        return slash > prefix.Length ? path[prefix.Length..slash] : null;
    }

    // Set-based, order-independent (the priority sort is not stable). Compares enabled state too.
    private static bool DiscoveredSetsEqual(List<OverlayEntry> a, List<OverlayEntry> b)
    {
        if (a.Count != b.Count) return false;
        var lookup = new Dictionary<string, (int Priority, bool Enabled)>(a.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var e in a) lookup[e.ModDirectory] = (e.Priority, e.Enabled);
        foreach (var e in b)
            if (!lookup.TryGetValue(e.ModDirectory, out var v) || v.Priority != e.Priority || v.Enabled != e.Enabled)
                return false;
        return true;
    }

    private bool HasSidecar(string modDir)
    {
        var metaPath = Path.Combine(modsRoot, modDir, "Proteus", "metadata.json");
        return File.Exists(metaPath);
    }
}
