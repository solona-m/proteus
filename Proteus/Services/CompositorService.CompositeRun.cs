using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Penumbra.Api.Enums;
using Proteus.Interop;

namespace Proteus.Services;
using static Proteus.Services.InertModDiagnosis;
using static Proteus.Services.OverlayBlend;

public partial class CompositorService
{
    private sealed partial class CompositeRun
    {
        private readonly CompositorService compositor;
        private readonly CancellationToken ct;
        private readonly long epoch;
        private readonly bool force;
        private readonly bool skinFingerprintAuthoritative;
        private readonly RefreshTimeline timeline;
        private long tRunStart;
        private string texturesDirEarly = null!;
        private List<OverlayEntry> entries = null!;
        private Dictionary<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>> byMaterial = null!;
        private IReadOnlyDictionary<string, OverlayColorOverride>? colorOverride;
        private IReadOnlyDictionary<string, OverlayGearOverride>? gearOverride;
        private IReadOnlyDictionary<string, List<string>>? stackOverride;
        private List<(OverlayEntry Entry, ResolvedOverlay Overlay)> gearOverlays = null!;
        private List<(OverlayEntry Entry, ResolvedContent Content)> contentLayers = null!;
        private List<(OverlayEntry Entry, ResolvedOverlay Overlay)> allOverlays = null!;
        private HashSet<string>? activeMtrl;
        private HashSet<string> activeBodyTypes = null!;
        private Dictionary<string, ResolutionDiagnostic> resolution = null!;
        private HashSet<string> filteredOut = null!;
        private HashSet<string>? wornCharCodes;
        private HashSet<string> toeCapMods = null!;
        private bool wearingMirroredBody;
        private HashSet<string> unmirrorMods = null!;
        private HashSet<string> asymmetricNotWorn = null!;
        private HashSet<string> capNarrowedMods = null!;
        private IReadOnlySet<string>? faceDoubledMaterials;
        private List<(OverlayEntry Entry, IReadOnlyList<(ResolvedOverlay Overlay, bool AboveGear)> Overlays)> resolvedEntries = null!;
        private FaceUvPlan facePlan = null!;
        private string texturesDir = null!;
        private string skinMaterialsDir = null!;
        private ConcurrentDictionary<string, string> redirects = null!;
        private int texturesPatched;
        private ConcurrentDictionary<(string, string?, string?), List<SkinGlowTarget>> skinGlow = null!;
        private ConcurrentDictionary<string, ChannelContribution> contributions = null!;
        private Dictionary<string, List<string>> maskPathsByMod = null!;
        private ConcurrentDictionary<(string mod, int w, int h, string bodyType), (byte[] W, byte[] T)?> combinedMaskCache = null!;
        private Dictionary<string, List<(string MaskPath, string? NormalPath, string? IndexPath)>> maskAssetsByMod = null!;
        private Dictionary<string, Dictionary<int, ColorTableRowOverride>> maskRowsByMod = null!;
        private Dictionary<string, OverlayDescriptor> maskDescByMod = null!;
        private HashSet<string> maskShellMods = null!;
        private List<string> baseKeys = null!;
        private int baseSignature;
        private string fingerprint = null!;
        private string skinFingerprint = null!;
        private SkinPublish? lastSkin;
        private bool skinReused;
        private Dictionary<string, Dictionary<int, ColorTableRowOverride>> maskFallbackRows = null!;
        private ConcurrentDictionary<string, byte> prefetchIssued = null!;
        private SemaphoreSlim prefetchGate = null!;
        private long tSetupEnd;
        private IReadOnlyList<ChannelContribution> nextChannelContributions = null!;
        private Dictionary<(string, string?, string?), List<SkinGlowTarget>> nextSkinGlowTargets = null!;
        private Dictionary<string, string> skinRedirectsThisRun = null!;
        private List<object>? manipulations;
        private bool nextNeedFullRedraw;
        private bool shellNeedsReload;
        private bool nextSecondSkinActive;
        private HashSet<string>? nextShellHostPaths;
        private HashSet<string>? nextAppendHosts;
        private Dictionary<(string ModDir, string? Group, string? Option), List<string>>? nextShellMaterials;
        private Dictionary<string, ShellLightProfile>? nextShellLight;
        private Dictionary<string, HashSet<string>>? nextContentMaterials;
        private Dictionary<string, HashSet<string>>? nextContentModels;
        private ShellDrawnProbe? nextShellDrawnCheck;
        private bool shellBuilt;
        private bool glassesPreHosted;
        private bool shellOnFacewear;
        private List<string> shellCarrierSlots = null!;
        private long tGear;
        private string reloadKind = null!;
        private bool manifestConfirmedLive;

        public CompositeRun(CompositorService compositor, CancellationToken ct, long epoch, bool force, bool skinFingerprintAuthoritative, RefreshTimeline timeline)
        {
            this.compositor = compositor;
            this.ct = ct;
            this.epoch = epoch;
            this.force = force;
            this.skinFingerprintAuthoritative = skinFingerprintAuthoritative;
            this.timeline = timeline;
        }

        public void Run()
        {
            try
            {
                if (!Discover()) return;
                PlanOverlays();
                ResolveEntries();
                PlanFaces();
                RouteOverlays();
                FilterLiveMaterials();
                if (!SynthesiseSiblings()) return;
                PrepareOutputs();
                AddBodyMaterials();
                ResolveMasks();
                SortStacks();
                if (!PrimeBases()) return;
                if (!GateUnchanged()) return;
                GateSkinReuse();
                InheritMaskColorsets();
                ColdPrefetch();
                CompositeSkin();
                CollectSkinResults();
                if (!BuildShells()) return;
                if (!CommitLocators()) return;
                Publish();
                ReconcileAndVerify();
            }
            catch (OperationCanceledException)
            {
                // Say so. This is the ordinary "a newer trigger arrived" path, but swallowing it silently left a
                // "Recomposite START" in the log with no ending of any kind, which reads like a hang — and on a slow
                // machine, where triggers can out-pace a composite indefinitely, a run of these IS the fault. Counting
                // STARTs against endings is the cheapest way to see that, so every exit from Run() now leaves a line.
                compositor.log.Debug("[Proteus] recomposite cancelled — a newer trigger superseded it ({0:F0}ms in)",
                    PhaseCounter.MsSince(tRunStart));
            }
            catch (Exception ex) when (compositor._disposed || IsLoadContextUnloading(ex))
            {
                // Plugin torn down mid-composite: the AssemblyLoadContext is unloading, nothing is recoverable, and a fresh
                // instance will composite on load. Log it as the shutdown race, not a crash.
                compositor.log.Debug("[Proteus] recomposite abandoned — plugin unloading ({0})", ex.GetBaseException().Message);
            }
            catch (Exception ex)
            {
                compositor.log.Error(ex, "[Proteus] Recomposite failed");
                timeline.Mark("failed");
                compositor.LogRefreshTimeline(timeline, "FAILED");
                compositor.LastResult = new CompositorResult { Success = false, ErrorMessage = ex.Message };
                compositor.ResultChanged?.Invoke();
                // Published output no longer matches any known inputs, so the next ambient trigger must composite.
                compositor._lastCompositeFingerprint = null;
            }
        }

        private bool Discover()
        {
            compositor.log.Debug("[Proteus] Recomposite START");

            // Phase instrumentation: counters are reset here and reported in one summary line after the composite loop.
            tRunStart = PhaseCounter.Begin();
            compositor.textureLoader.ResetStats();
            compositor.uvRemap.RemapStats.Reset();
            compositor.ResetBlendStats();

            compositor.EnsureManagedModExists();

            // The previous run's output stays on disk until the new manifest is live: a redirect must never point at a
            // missing file, and content-hashed names let old and new coexist.
            texturesDirEarly = Path.Combine(compositor.managedModDir, "textures");

            // Collect what the last published manifest no longer names, one composite late (see PruneSupersededOutput).
            compositor.PruneSupersededOutput();

            // Nothing is unpublished here, and nothing may be: clearing the manifest blacks out every redirect (EQDP
            // included) until the republish, across a whole trigger burst. ResolveUpstream and PrimeUpstreamCache keep
            // base reads off our own output instead.

            // Discover ALL sidecar mods (incl. disabled) so the UI can list and re-enable them;
            // composite only the ones enabled in Penumbra, in priority order.
            var allEntries = compositor.discovery.DiscoverAll();
            if (ct.IsCancellationRequested) return false;

            compositor.LastDiscovered = allEntries;

            // Enabled in Penumbra is the whole test for whether a mod composites.
            entries = allEntries
                .Where(e => e.Enabled)
                .OrderBy(e => e.Priority)
                .ToList();
            compositor.CheckManagedModHealth(entries);

            if (entries.Count == 0)
            {
                var empty = new Dictionary<string, string>();
                compositor.WriteManagedModJson(empty);
                // Recorded so the history reflects reality: once this empty manifest ages out, everything becomes collectable.
                compositor.RecordPublish(empty);

                // A shell hosted on an accessory (or a rewritten skin material) needs a full redraw to go away. This early return
                // skips the normal end-of-method reset, hence doing it explicitly.
                if (compositor._secondSkinActive || compositor._lastSkinMaterialRedirects.Count > 0) compositor._needFullRedraw = true;
                compositor._secondSkinActive = false;
                compositor._lastShellHostPaths = new(StringComparer.OrdinalIgnoreCase);
                compositor._lastSkinMaterialRedirects = new(StringComparer.OrdinalIgnoreCase);
                // ...and the UI-facing locators, which the skipped gear phase would otherwise publish.
                compositor.ClearShellLocators();
                // Same for the "contributes nothing" warnings, which the skipped ExplainInertMods normally clears.
                compositor._inertMods = new Dictionary<string, InertReason>(StringComparer.OrdinalIgnoreCase);

                // No fingerprint describes an empty manifest, and it satisfies whatever forced work was owed.
                compositor._lastCompositeFingerprint = null;
                Interlocked.Exchange(ref compositor._forcePending, 0);
                Interlocked.Exchange(ref compositor._skinForcePending, 0);
                // Nothing is hosted, so the redraw hook must not put a carrier back.
                compositor.RememberHostDecision(gearWanted: false, shellBuilt: false, onFacewear: false, carrierSlots: []);

                // Take off our glasses carrier: the empty manifest dropped the redirect that made it invisible. This early return
                // skips the end-of-method reconcile.
                compositor.ReconcileInvisibleGlasses(gearWanted: false, shellBuilt: false, hostedOnFacewear: false);
                compositor.ReconcileEmperorRing(gearWanted: false, shellBuilt: false, carrierSlots: []);

                // userRequested, as in SetEnabled(false): the output is already withdrawn, so a suppressed redraw strands the
                // character. Only reachable with auto redraw off from a forced composite.
                compositor.ReloadAndRedraw(userRequested: true);
                timeline.Mark("publish+reload");
                compositor.LogRefreshTimeline(timeline, "no enabled mods");
                compositor.LastResult = new CompositorResult { Success = true, TexturesPatched = 0, OverlayModsUsed = 0 };
                compositor.ResultChanged?.Invoke();
                return false;
            }
            return true;
        }

        private void PlanOverlays()
        {
            // Flatten: (entry, resolvedOverlay) pairs, grouped by material game path
            byMaterial = new Dictionary<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>>(
                StringComparer.OrdinalIgnoreCase);

            // Snapshot the volatile overrides for this run, with any pinned preset laid over the design binding per mod.
            colorOverride = MergeByMod(compositor._presetColorOverride, compositor._colorOverride);
            gearOverride = MergeByMod(compositor._presetGearOverride,  compositor._gearOverride);

            // Mod-wide stack position of an overlay (0 = top), from the design binding's stack override, else the global config order.
            stackOverride = MergeByMod(compositor._presetStackOverride, compositor._stackOverride);

            // Gear overlays don't composite into a skin material — each becomes its own second-skin
            // shell with its own material and shader. Collect them separately.
            gearOverlays = new List<(OverlayEntry Entry, ResolvedOverlay Overlay)>();
            // Imported content packs bring geometry, not art: they skip byMaterial and go straight to the second-skin builder.
            contentLayers = new List<(OverlayEntry Entry, ResolvedContent Content)>();
            // Every active overlay of every mod, both layers: the second-skin builder ranks groups across layers.
            allOverlays = new List<(OverlayEntry Entry, ResolvedOverlay Overlay)>();

            activeMtrl = compositor._activeMtrlSnapshot;
            // Filled ahead of the entry loop: sibling synthesis needs "type not loaded" vs "material not loaded", and promotion
            // needs to know whether the worn body is mirrored.
            activeBodyTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ── inputs to ExplainInertMods ───────────────────────────────────────────────────────────
            // Facts about a mod that contributes nothing, collected here because none survives to the end of the composite.
            // Resolved race codes, not a bool, so the message can name both sides.
            resolution = new Dictionary<string, ResolutionDiagnostic>(StringComparer.OrdinalIgnoreCase);
            // Mods the live-material filter took a material away from.
            filteredOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // The character's own race code(s) as the filter understood them; null until the filter runs, and empty
            // mid-redraw ("don't know").
            wornCharCodes = null;

            // Is a toe cap selected anywhere in the look? A cap belongs to the foot, not the mod, and must be known before
            // promotion because a cap is geometry and needs a shell.
            toeCapMods = compositor.ToeCapWanted(entries);
            if (toeCapMods.Count > 0)
                compositor.log.Debug("[Proteus] toe cap is selected in [{0}] — THAT MOD's skin overlays that can be cut "
                        + "into a shell are promoted to cloth, since the cap has to rebuild geometry the skin "
                        + "layer has none of. Other mods are left alone: a cap is a Penumbra option, and a "
                        + "mod re-exported with its options reordered re-points every saved selection.",
                    string.Join(", ", toeCapMods.OrderBy(m => m, StringComparer.OrdinalIgnoreCase)));
            if (activeMtrl != null)
                foreach (var m in activeMtrl)
                {
                    var bt = UVRemapService.InferBodyType(m);
                    if (bt != null) activeBodyTypes.Add(bt);
                }

            // Some surface on this character is a mirrored body, which folds asymmetric art in half
            // (OverlayDescriptor.AsymmetricArt); often it is skin bundled into a garment.
            wearingMirroredBody = HasMirroredBodySurface(activeBodyTypes);

            // Mods with an overlay that needs an un-mirrored shell; allowed past the gen2 gate below even with
            // "Overlay gen2/vanilla" unticked, since the shell is the only way their art renders.
            unmirrorMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Mods already told their art is asymmetric but nothing folds it; logged once per mod.
            asymmetricNotWorn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Mods already told that someone else's toe cap no longer promotes them.
            capNarrowedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Filled by the face plan below; declared here because a local function cannot capture a later variable.
            faceDoubledMaterials = null;
        }

        private void ResolveEntries()
        {
            // Resolved once, then walked twice: the face-doubling plan must settle before promotion, and re-resolving would
            // repeat every mod's IPC and meta.json read. Each overlay carries its above-gear rank.
            resolvedEntries = new List<(OverlayEntry Entry, IReadOnlyList<(ResolvedOverlay Overlay, bool AboveGear)> Overlays)>();

            foreach (var entry in entries)
            {
                // Both halves of the pack's selection state (overlays and geometry), so an empty mod is explained by both.
                var contentDiag = ResolutionDiagnostic.None;
                if (entry.Metadata.HasContent)
                {
                    var content = compositor.discovery.ResolveActiveContent(entry, out contentDiag);
                    // A design binding overrides the pack's colours in memory; metadata.json is never written.
                    // Per material as well as per option: the colour panel edits a pack one material at a time, and
                    // the mod's own per-material rows would otherwise shadow the binding (ContentSettingLevels).
                    if (colorOverride != null && colorOverride.TryGetValue(entry.ModDirectory, out var cOvr))
                        content = content
                            .Select(c => c with
                            {
                                ColorTableRows = cOvr.Resolve(c.OptionGroup, c.Option) ?? c.ColorTableRows,
                                MaterialRows   = cOvr.Materials,
                            })
                            .ToList();
                    // And its animated glow, resolved the same way, or edits saved to the binding would do nothing.
                    if (gearOverride != null && gearOverride.TryGetValue(entry.ModDirectory, out var cGear))
                        content = content
                            .Select(c => c with
                            {
                                Glow         = cGear.ResolveContent(c.OptionGroup, c.Option) ?? c.Glow,
                                MaterialGlow = cGear.Materials,
                            })
                            .ToList();
                    foreach (var c in content) contentLayers.Add((entry, c));
                }

                var overlays = compositor.discovery.ResolveActiveOverlays(entry, out var diag);
                resolution[entry.ModDirectory] = diag.Merge(contentDiag);

                // Asymmetry is declared by the author, never measured: real skin is never symmetric (OverlayDescriptor.AsymmetricArt).

                // A design binding overrides metadata colours in memory, falling back to the live metadata per overlay.
                if (colorOverride != null && colorOverride.TryGetValue(entry.ModDirectory, out var ovr))
                    overlays = overlays
                        .Select(o => o with { ColorTableRows = ovr.Resolve(o.OptionGroup, o.Option) ?? o.ColorTableRows })
                        .ToList();

                // Same for the gear settings, onto a copy of the descriptor.
                if (gearOverride != null && gearOverride.TryGetValue(entry.ModDirectory, out var gOvr))
                    overlays = overlays
                        .Select(o =>
                        {
                            var gs = gOvr.Resolve(o.OptionGroup, o.Option);
                            if (gs == null) return o;
                            var copy = CloneDescriptor(o.Descriptor);
                            gs.ApplyTo(copy);
                            return o with { Descriptor = copy };
                        })
                        .ToList();

                // Stack rank of an overlay, top-first, matching the tab strip (ModStackIndexOf, GroupOrder, StackIndexOf).
                // Lower tuple = higher in the stack.
                var modDir = entry.ModDirectory;
                (int, int, int) Rank(ResolvedOverlay o) => (
                    ModStackIndexFor(modDir, o.OptionGroup ?? "", o.Option ?? ""),
                    o.GroupOrder,
                    compositor.config.StackIndexOf(modDir, o.OptionGroup ?? "", o.Option ?? ""));

                // The deepest gear overlay in this mod. An unpinned skin overlay stacked above it is promoted to a gear shell;
                // reverts once dragged back below all gear. Pinned skin stays skin.
                (int, int, int)? lowestGear = null;
                foreach (var o in overlays)
                    if (o.Descriptor.Layer == OverlayLayer.Gear)
                    {
                        var r = Rank(o);
                        if (lowestGear == null || r.CompareTo(lowestGear.Value) > 0) lowestGear = r;
                    }

                resolvedEntries.Add((entry, overlays
                    .Select(o => (Overlay: o,
                                  AboveGear: lowestGear.HasValue && Rank(o).CompareTo(lowestGear.Value) < 0))
                    .ToList()));
            }
        }

        private void PlanFaces()
        {
            // ── un-mirroring a face, in place ────────────────────────────────────────────────
            // Asymmetric face art moves the face's own UVs into the doubled sheet rather than cutting a shell (a shell has no
            // shape keys, so cannot blink). Runs before promotion and the material walk, which all read this one plan.
            facePlan = compositor.faceUv.Plan(resolvedEntries, compositor._humanPartModels, compositor.penumbra.ResolvePlayer,
                                       compositor.IsOwnOutput, compositor.managedModDir, compositor.config.FaceUvInPlace);
            faceDoubledMaterials = facePlan.Materials;   // what NeedsUnmirroredShell reads, below
        }

        private void RouteOverlays()
        {
            foreach (var (entry, entryOverlays) in resolvedEntries)
            {
                foreach (var (overlay, aboveGear) in entryOverlays)
                {
                    var ov = overlay;
                    // Only a body-UV overlay can move onto a shell (CanRenderAsShell); a face overlay stays on its material whatever its
                    // stored layer says, or the shell builder would paste face art across the body.
                    bool canShell = CanRenderAsShell(overlay.Descriptor);
                    bool unmirrors = NeedsUnmirroredShell(overlay.Descriptor);
                    // Asymmetric art that is not being un-mirrored: log why, once per mod.
                    if (!unmirrors && overlay.Descriptor.AsymmetricArt == true
                        && !wearingMirroredBody && asymmetricNotWorn.Add(entry.ModDirectory))
                        compositor.log.Information("[Proteus] {0}: art is asymmetric, but the body being worn is {1} — "
                                      + "nothing folds it, so it paints normally. Un-mirroring is only for a "
                                      + "character actually wearing vanilla/gen2 (and only then is vanilla the "
                                      + "single active body type)",
                            entry.ModDirectory,
                            activeBodyTypes.Count == 0
                                ? "not known yet"
                                : string.Join("+", activeBodyTypes.OrderBy(x => x, StringComparer.Ordinal)));
                    if (unmirrors)
                    {
                        if (canShell) unmirrorMods.Add(entry.ModDirectory);
                        else
                            // The fallback is the fold: the vanilla crop keeps the +X side and mirrors it.
                            compositor.log.Information("[Proteus] {0}: asymmetric art on a vanilla body, but no shell can "
                                          + "be cut for the surface it paints — it falls back to the mirrored "
                                          + "half-sheet and one side of the art is lost", entry.ModDirectory);
                    }
                    if (!canShell && overlay.Descriptor.Layer == OverlayLayer.Gear)
                    {
                        var demoted = CloneDescriptor(overlay.Descriptor);
                        demoted.Layer = OverlayLayer.Skin;   // ShaderPackage → skin.shpk; Scroll goes unread
                        ov = overlay with { Descriptor = demoted };
                        compositor.NotifyNoShellSurface(entry, overlay.ColorTableRows, overlay.Descriptor);
                    }
                    // Same surface, other direction: the auto-promotion is vetoed, so tell the user why a glow they set is silent.
                    else if (!canShell && !overlay.Descriptor.ManualShaderLock
                        && (aboveGear || RenderModeInference.HasCloth(overlay.ColorTableRows ?? [])))
                        compositor.NotifyNoShellSurface(entry, overlay.ColorTableRows, overlay.Descriptor);
                    // A cap only promotes the overlays of the mod that ships it (see ToeCapWanted). Another mod's skin stocking over the
                    // same toes stays skin; logged once per mod so the trade is findable.
                    else if (toeCapMods.Count > 0 && !toeCapMods.Contains(entry.ModDirectory)
                        // The bust bridge is held equal across the pair: this asks what the cap alone decides.
                        && !RenderModeInference.ShouldPromoteToGear(overlay.Descriptor.Layer,
                                overlay.Descriptor.ManualShaderLock, overlay.ColorTableRows, aboveGear,
                                canShell, unmirrors, false, RenderModeInference.WantsGeometry(entry.Metadata))
                        && RenderModeInference.ShouldPromoteToGear(overlay.Descriptor.Layer,
                                overlay.Descriptor.ManualShaderLock, overlay.ColorTableRows, aboveGear,
                                canShell, unmirrors, true, RenderModeInference.WantsGeometry(entry.Metadata))
                        && capNarrowedMods.Add(entry.ModDirectory))
                    {
                        compositor.log.Information("[Proteus] {0}: a toe cap is selected in [{1}], not here, so this "
                                      + "mod's skin overlays stay on the skin — the cap still shapes any "
                                      + "shell over the toes, but this mod has no shell to shape. Tick the "
                                      + "cap in this mod too if its art should follow the rebuilt toes.",
                            entry.ModDirectory,
                            string.Join(", ", toeCapMods.OrderBy(m => m, StringComparer.OrdinalIgnoreCase)));
                    }
                    else if (RenderModeInference.ShouldPromoteToGear(overlay.Descriptor.Layer,
                            overlay.Descriptor.ManualShaderLock, overlay.ColorTableRows, aboveGear, canShell,
                            unmirrors, toeCapMods.Contains(entry.ModDirectory),
                            RenderModeInference.WantsGeometry(entry.Metadata)))
                    {
                        var promoted = CloneDescriptor(overlay.Descriptor);
                        promoted.Layer = OverlayLayer.Gear;   // ShaderPackage → character.shpk

                        // Same predicate the editor asks (RenderModeInference.PromotedShader), so the two cannot disagree.
                        promoted.Shader = RenderModeInference.PromotedShader(promoted, overlay.ColorTableRows);

                        ov = overlay with { Descriptor = promoted };

                        // Only when glow is what moved it; an overlay promoted for sitting above gear already rendered through a shell.
                        if (!aboveGear) compositor.NotifyGlowPromoted(entry, overlay.ColorTableRows);
                    }

                    allOverlays.Add((entry, ov));
                    if (ov.Descriptor.Layer == OverlayLayer.Gear)
                    {
                        gearOverlays.Add((entry, ov));
                        continue;
                    }

                    foreach (var mtrlPath in ov.Descriptor.MaterialGamePaths)
                    {
                        if (string.IsNullOrEmpty(mtrlPath)) continue;
                        if (!byMaterial.TryGetValue(mtrlPath, out var list))
                            byMaterial[mtrlPath] = list = new();
                        list.Add((entry, ov));
                    }
                }
            }
        }

        private void FilterLiveMaterials()
        {
            // Drop materials the player doesn't currently have loaded. Equipment/accessory materials: exact path (authoritative).
            // Body-type materials: the snapshot may predate a body-type switch, so:
            //   - type in the snapshot but exact path not → filter (active for a different race/body code);
            //   - type not in the snapshot at all → keep (mid-switch; the post-redraw check cleans up).
            {
                compositor._lastCompositedBodyType = activeBodyTypes.Count > 0
                    ? string.Join(",", activeBodyTypes.OrderBy(x => x))
                    : null;

                if (activeMtrl != null)
                {
                    // The active character codes from body materials in the snapshot. Built with CharCodeSet so the post-redraw check's
                    // CharCodeKey agrees.
                    var activeCharCodes = CharCodeSet(activeMtrl);
                    compositor._lastCompositedCharCodes = CharCodeKey(activeCharCodes);

                    // Glamourer may display a different race than the draw object uses; if so, its char code is the sole effective one.
                    string? glamCode = compositor._glamourerCharCode;
                    bool glamOverride = glamCode != null && !activeCharCodes.Contains(glamCode);
                    var effectiveCharCodes = glamOverride
                        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { glamCode! }
                        : activeCharCodes;
                    if (glamOverride)
                        compositor.log.Debug("[Proteus] Glamourer race override: snapshot={0} → displayed={1}",
                            compositor._lastCompositedCharCodes ?? "none", glamCode!);

                    // What the wearer is (the effective set, as Glamourer displays it), for explaining a pack that painted nothing.
                    wornCharCodes = effectiveCharCodes;

                    // Noted before the removal, which destroys the evidence.
                    void NoteDropped(string key)
                    {
                        if (!byMaterial.TryGetValue(key, out var doomed)) return;
                        foreach (var (e, _) in doomed) filteredOut.Add(e.ModDirectory);
                    }

                    foreach (var key in byMaterial.Keys.Where(k => !activeMtrl.Contains(k)).ToList())
                    {
                        var keyBodyType = UVRemapService.InferBodyType(key);
                        if (keyBodyType != null)
                        {
                            var keyCharCode = ExtractHumanCharCode(key);
                            if (activeBodyTypes.Contains(keyBodyType))
                            {
                                // Body type is active: keep if the char code matches the effective race (covers Glamourer's display override).
                                if (keyCharCode != null && effectiveCharCodes.Count > 0
                                    && effectiveCharCodes.Contains(keyCharCode))
                                    continue; // keep
                                compositor.log.Debug("[Proteus] Skipping body material (active types={0}): {1}",
                                    compositor._lastCompositedBodyType ?? "none", key);
                                NoteDropped(key);
                                byMaterial.Remove(key);
                            }
                            else
                            {
                                // Body type absent — could be mid-switch to a new body type.
                                // Only keep the mid-switch heuristic for the effective race.
                                if (keyCharCode != null && effectiveCharCodes.Count > 0
                                    && !effectiveCharCodes.Contains(keyCharCode))
                                {
                                    compositor.log.Debug("[Proteus] Skipping body material (wrong race): {0}", key);
                                    NoteDropped(key);
                                    byMaterial.Remove(key);
                                }
                                // else: same race, body type absent → keep (mid body-type switch)
                            }
                        }
                        else
                        {
                            // The character's own non-body surfaces (face, hair, tail, ears) get the same mid-switch tolerance as the body:
                            // the snapshot's silence is not evidence the surface is absent. Equipment keeps the strict exact-path rule.
                            var keyRace = ExtractHumanCharCode(key);
                            if (keyRace != null
                                && (effectiveCharCodes.Count == 0 || effectiveCharCodes.Contains(keyRace)))
                                continue;   // keep — ours, and the snapshot simply hasn't caught up

                            compositor.log.Debug("[Proteus] Skipping non-equipped material: {0}", key);
                            NoteDropped(key);
                            byMaterial.Remove(key);
                        }
                    }
                }
            }
        }

        private bool SynthesiseSiblings()
        {
            // Sibling synthesis: a mod's overlays are applied to the other loaded body-type materials that have no direct entry,
            // with UV remap from the descriptor's source body type. bibo↔gen3/Eve always; vanilla (gen2) unless the mod's
            // "Overlay gen2/vanilla" is unticked. gen2 is never a source.
            if (activeMtrl != null)
            {
                var siblings = new Dictionary<string, List<(OverlayEntry, ResolvedOverlay)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (srcPath, pairs) in byMaterial)
                {
                    var srcType = UVRemapService.InferBodyType(srcPath);
                    if (srcType is null or "gen2") continue;

                    var srcSuffix = BodySuffixes.First(s => srcPath.EndsWith(s.Suffix, StringComparison.OrdinalIgnoreCase)).Suffix;
                    var stem = srcPath[..^srcSuffix.Length];

                    foreach (var (suffix, bodyType) in BodySuffixes)
                    {
                        if (suffix == srcSuffix) continue;
                        var dstPath = stem + suffix;
                        // A sibling is a target only if loaded on the character; needs a settled snapshot (SchedulePostRedrawBodyTypeCheck re-verifies).
                        if (!activeMtrl.Contains(dstPath))
                        {
                            // Log only when the body type is loaded but this material is not (the type came from another race or body id).
                            if (activeBodyTypes.Contains(bodyType))
                                compositor.log.Debug("[Proteus] No sibling synthesized ({0} is loaded, but not this material): {1}",
                                    bodyType, dstPath);
                            continue;
                        }

                        bool vanilla = bodyType == "gen2";
                        var dstPairs = vanilla
                            ? pairs.Where(p => compositor.config.OverlaysVanillaFor(p.Entry.ModDirectory)).ToList()
                            : pairs.ToList();
                        if (dstPairs.Count == 0) continue;

                        // Name the mod/option(s) driving this sibling, tagged with the destination body type, so it can be traced.
                        var contributors = string.Join(", ", dstPairs
                            .Select(p => p.Overlay.Option != null
                                ? $"\"{p.Entry.ModName}\"/{p.Overlay.OptionGroup}:{p.Overlay.Option}"
                                : $"\"{p.Entry.ModName}\"")
                            .Distinct());
                        compositor.log.Debug("[Proteus] Sibling synthesis ({0}): {1} → {2} (from {3})",
                            vanilla ? "gen2/vanilla" : bodyType, srcPath, dstPath, contributors);
                        if (siblings.TryGetValue(dstPath, out var existSiblings))
                            existSiblings.AddRange(dstPairs);
                        else
                            siblings[dstPath] = dstPairs;
                    }
                }
                foreach (var (path, pairs) in siblings)
                {
                    if (byMaterial.TryGetValue(path, out var existing))
                        existing.AddRange(pairs);
                    else
                        byMaterial[path] = pairs;
                }
            }

            if (ct.IsCancellationRequested) return false;

            texturesDir = texturesDirEarly;
            Directory.CreateDirectory(texturesDir);
            return true;
        }

        private void PrepareOutputs()
        {
            // Skin materials rewritten to stop naming a shared texture (IsSharedTexturePath); the directory is created on first use.
            skinMaterialsDir = Path.Combine(compositor.managedModDir, "materials");

            redirects = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // The rewritten face models go into the same manifest as their doubled textures, so the pair publishes atomically.
            foreach (var (gamePath, relPath) in facePlan.ModelRedirects) redirects[gamePath] = relPath;
            texturesPatched = 0;
            // Accumulated across the parallel per-material loop; published to _skinGlowTargets after it.
            skinGlow = new ConcurrentDictionary<(string, string?, string?), List<Proteus.Interop.SkinGlowTarget>>();
            // Same, for ChannelContributions: what each material's channels actually received. Touched carries non-overlay passes
            // (AO, skin-tint suppression). Keyed by material so the two Add sites stay one entry per material.
            contributions = new ConcurrentDictionary<string, ChannelContribution>(StringComparer.OrdinalIgnoreCase);

            // Output filenames carry a content hash (see ContentTag), so unchanged bytes keep their path.

            // Per-mod transparency masks: a multi-select "Masks" group whose options load grayscale PNGs from Proteus/Masks/,
            // reducing the coverage of every overlay in the same mod. Resolved once per mod, keep-maps cached per (mod, size).
            maskPathsByMod = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var masks = compositor.discovery.ResolveActiveMasks(entry);
                if (masks.Count > 0) maskPathsByMod[entry.ModDirectory] = masks;
            }
            combinedMaskCache = new ConcurrentDictionary<(string mod, int w, int h, string bodyType), (byte[] W, byte[] T)?>();

            // Masks with a companion relief normal and/or colour-row index, resolved once per mod.
            maskAssetsByMod = new Dictionary<string, List<(string MaskPath, string? NormalPath, string? IndexPath)>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var assets = compositor.discovery.ResolveActiveMaskAssets(entry)
                    .Where(a => a.NormalPath != null || a.IndexPath != null).ToList();
                if (assets.Count > 0) maskAssetsByMod[entry.ModDirectory] = assets;
            }
        }

        private void AddBodyMaterials()
        {
            // ── Body materials for a gear-only / masks-only look ─────────────
            // AO and the indent are cast by a garment onto the skin, so add the character's own body materials with an empty
            // overlay list even when no skin overlay targets them. Gated on the effect being on and opted in by a contributing
            // mod; a body that is not the caster's own obeys that mod's sibling-synthesis setting.
            if ((compositor.config.AmbientOcclusionStrength > 0f || compositor.config.AmbientOcclusionNormalDepth > 0f)
                && activeMtrl != null)
            {
                // Each caster with the body type(s) it is authored for, to judge siblings.
                var casters = entries
                    .Where(e => (gearOverlays.Any(g => string.Equals(g.Entry.ModDirectory, e.ModDirectory, StringComparison.OrdinalIgnoreCase))
                                 || maskPathsByMod.ContainsKey(e.ModDirectory))
                                && compositor.config.AmbientOcclusionEnabledFor(e.ModDirectory, e.Metadata?.AmbientOcclusion))
                    .Select(e => (
                        Mod: e.ModDirectory,
                        Types: allOverlays
                            .Where(o => string.Equals(o.Entry.ModDirectory, e.ModDirectory, StringComparison.OrdinalIgnoreCase))
                            .Select(o => o.Overlay.Descriptor.SourceBodyType
                                         ?? o.Overlay.Descriptor.MaterialGamePaths
                                              .Select(UVRemapService.InferBodyType).FirstOrDefault(t => t != null))
                            .Where(t => t != null)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase)))
                    .ToList();

                if (casters.Count > 0)
                {
                    int added = 0;
                    foreach (var m in activeMtrl)
                    {
                        // Both predicates: InferBodyType alone also matches weapon paths. Never overwrite a real list.
                        if (ExtractHumanCharCode(m) == null) continue;
                        var dstType = UVRemapService.InferBodyType(m);
                        if (dstType == null || byMaterial.ContainsKey(m)) continue;

                        // A caster's own body always qualifies; any other is a sibling and follows the sibling-synthesis gate.
                        bool vanilla = string.Equals(dstType, "gen2", StringComparison.OrdinalIgnoreCase);
                        if (!casters.Any(c => c.Types.Contains(dstType) || !vanilla || compositor.config.OverlaysVanillaFor(c.Mod)))
                            continue;

                        byMaterial[m] = new();
                        added++;
                    }
                    if (added > 0)
                        compositor.log.Debug("[Proteus] AO: added {0} body material(s) with no skin overlay so the "
                                + "shadow/indent from gear or masks has somewhere to land", added);
                }
            }
        }

        private void ResolveMasks()
        {
            // The mod's shared "Masks" colorset. When present, active masks are coloured by these rows via their combined _id
            // in a top diffuse layer, and the per-overlay _id merge is skipped (LoadIndexMerged). Absent ⇒ legacy merge.
            maskRowsByMod = new Dictionary<string, Dictionary<int, ColorTableRowOverride>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                if (MaskRowsFor(entry) is { Count: > 0 } mr)
                    maskRowsByMod[entry.ModDirectory] = BuildRowDict(mr);

            // The Masks tab's effective render mode per mod, resolved once through the design binding: promotion, shell
            // synthesis and the fingerprint must not disagree.
            maskDescByMod = new Dictionary<string, OverlayDescriptor>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                if (MaskDescriptorFor(entry) is { } md)
                    maskDescByMod[entry.ModDirectory] = md;

            // Mods that get a dedicated top mask shell: gear shells plus mask _id/relief assets. For these the mask lives
            // entirely on the shell, so the skin diffuse/relief passes and LoadIndexMerged must skip them.
            maskShellMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in gearOverlays.Select(g => g.Entry.ModDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
                if (maskAssetsByMod.TryGetValue(mod, out var mA)
                    && mA.Any(a => a.IndexPath != null || a.NormalPath != null))
                    maskShellMods.Add(mod);

            // Also promote an all-skin mod whose Masks tab was given Cloth/Glow (MaskDescriptor.Layer == Gear). A recorded mode,
            // never inferred from the mask colorset: moving a mask to a shell changes how it renders.
            foreach (var entry in entries)
                if (maskAssetsByMod.TryGetValue(entry.ModDirectory, out var mA2)
                    && mA2.Any(a => a.IndexPath != null || a.NormalPath != null)
                    && maskDescByMod.TryGetValue(entry.ModDirectory, out var md2)
                    && md2.Layer == OverlayLayer.Gear)
                    maskShellMods.Add(entry.ModDirectory);

            // First point at which "contributed nothing" is settled and true (after the sibling pass). Placement is load-bearing.
            compositor.ExplainInertMods(entries, byMaterial, gearOverlays, contentLayers, maskShellMods,
                             maskDescByMod, resolution, filteredOut, wornCharCodes, activeBodyTypes,
                             allOverlays);
        }

        private void SortStacks()
        {
            // Composite order = list order; last lands on top. Across mods, Penumbra priority. Within a mod, the tab strip's
            // mod-wide stack outranks GroupOrder; GroupOrder and the per-group stack are tiebreaks. Masks apply on top separately.
            foreach (var list in byMaterial.Values)
            {
                var sorted = list
                    .Select((p, i) => (p, i))
                    .OrderBy(x => x.p.Entry.Priority)
                    // A print recolours what was painted, so it lands after everything it can reach: below Penumbra priority, above the tab strip.
                    .ThenBy(x => AnyBlendRow(x.p.Overlay.ColorTableRows) ? 1 : 0)
                    .ThenByDescending(x => ModStackIndexFor(x.p.Entry.ModDirectory, x.p.Overlay.OptionGroup ?? "", x.p.Overlay.Option ?? ""))
                    .ThenByDescending(x => x.p.Overlay.GroupOrder)
                    .ThenByDescending(x => compositor.config.StackIndexOf(x.p.Entry.ModDirectory, x.p.Overlay.OptionGroup ?? "", x.p.Overlay.Option ?? ""))
                    // Same-group options on an unrestacked mod tie on every key: reverse the index so "leftmost tab = on top" holds.
                    .ThenByDescending(x => x.i)
                    .Select(x => x.p)
                    .ToList();
                list.Clear();
                list.AddRange(sorted);

                // Bottom-to-top composite order with sort keys (mod= tab-stack index, grp= group ordinal), at Debug.
                if (sorted.Count > 1)
                {
                    var parts = sorted.Select(p =>
                    {
                        var g = p.Overlay.OptionGroup ?? "";
                        var o = p.Overlay.Option ?? "";
                        var mi = FmtIdx(ModStackIndexFor(p.Entry.ModDirectory, g, o));
                        var bl = AnyBlendRow(p.Overlay.ColorTableRows) ? ",print" : "";
                        return $"{g}/{o}[mod={mi},grp={p.Overlay.GroupOrder}{bl}]";
                    });
                    compositor.log.Debug("[Proteus] skin stack (bottom->top): {0}", string.Join("  ->  ", parts));
                }
            }
        }

        private bool PrimeBases()
        {
            // Every base path is known and nothing expensive has started: give each a remembered upstream before anything
            // resolves through our live redirects. Before the gate, whose fingerprint includes the resolved files.
            baseKeys = compositor.PrimeUpstreamCache(byMaterial.Keys);
            if (ct.IsCancellationRequested) return false;

            // The shape of this composite (targeted materials), computed from byMaterial so a cold cache cannot perturb it.
            baseSignature = ComputeBaseKeysHash(byMaterial.Keys);

            // Contribute the half of the base set knowable before the gate, so the first settings event need not fail open.
            // Not authoritative: it may add paths but never retire any.
            compositor.RecordCompositeBaseKeys(baseKeys, baseSignature, authoritative: false);
            return true;
        }

        private bool GateUnchanged()
        {
            // ── Unchanged-inputs gate ────────────────────────────────────────
            // An ambient trigger can stop here when the inputs hash to what is already published. Placed after resolution and
            // the design-binding overrides so the fingerprint covers them; nothing persistent is mutated before the return.
            fingerprint = compositor.BuildCompositeFingerprint(
                byMaterial, gearOverlays, maskPathsByMod, maskAssetsByMod, maskRowsByMod, maskDescByMod,
                maskShellMods, baseKeys, contentLayers, toeCapMods);

            // The same inputs minus those only the shell reads (see the skin-reuse gate below).
            skinFingerprint = compositor.BuildCompositeFingerprint(
                byMaterial, gearOverlays, maskPathsByMod, maskAssetsByMod, maskRowsByMod, maskDescByMod,
                maskShellMods, baseKeys, contentLayers, toeCapMods, skinOnly: true);

            if (!force && Volatile.Read(ref compositor._forcePending) == 0
                && compositor._lastCompositeFingerprint != null && fingerprint == compositor._lastCompositeFingerprint)
            {
                // Still reconcile the carriers: whatever redraw led here may have reverted them (ApplyFlag.Once). Idempotent.
                compositor.ReconcileInvisibleGlasses(compositor._lastGearWanted, compositor._lastShellBuilt, compositor._lastShellOnFacewear,
                                          alreadyHosted: true);
                compositor.ReconcileEmperorRing(compositor._lastGearWanted, compositor._lastShellBuilt, compositor._lastShellCarrierSlots);

                // Nothing published, so nothing to record: RecordPublish would let PruneSupersededOutput collect live files.
                // No write, reload or redraw.
                compositor.log.Information("[Proteus] recomposite skipped — inputs unchanged ({0:F0}ms)",
                    PhaseCounter.MsSince(tRunStart));
                timeline.Mark("setup");
                compositor.LogRefreshTimeline(timeline, "skipped, inputs unchanged");
                return false;
            }
            return true;
        }

        private void GateSkinReuse()
        {
            // ── Skin reuse ───────────────────────────────────────────────────
            // Something moved, but maybe nothing the skin depends on (typically an outfit change). When the skin fingerprint
            // matches, the published skin textures are already what this run would compute. Also gated on
            // _lastCompositeFingerprint, so invalidating the full gate invalidates this. Anything unexpected falls through to
            // the full composite. The group is read through one reference (SkinPublish). _skinForcePending, not force, vetoes
            // (see skinFingerprintAuthoritative).
            lastSkin = compositor._lastSkinPublish;
            skinReused = (!force || skinFingerprintAuthoritative)
                && Volatile.Read(ref compositor._skinForcePending) == 0
                && compositor._lastCompositeFingerprint != null
                && lastSkin != null && skinFingerprint == lastSkin.Fingerprint
                && lastSkin.Redirects.Count > 0
                && compositor.SkinOutputStillOnDisk(lastSkin.Redirects);

            if (skinReused)
            {
                foreach (var kv in lastSkin!.Redirects) redirects[kv.Key] = kv.Value;
                compositor.log.Information("[Proteus] skin unchanged — reusing {0} published texture(s), "
                              + "compositing the shell only", lastSkin.Redirects.Count);
            }
            else
            {
                // Which condition declined it: the fingerprint mismatch and the latch look identical from outside.
                compositor.log.Debug("[Proteus] skin reuse declined: {0}",
                    force && !skinFingerprintAuthoritative ? "forced trigger whose skin effect is not hashed"
                    : Volatile.Read(ref compositor._skinForcePending) != 0 ? "an earlier forced skin change is still owed"
                    : compositor._lastCompositeFingerprint == null ? "no published composite fingerprint"
                    : lastSkin == null ? "no remembered skin publish"
                    : skinFingerprint != lastSkin.Fingerprint
                        ? "skin fingerprint changed at " + FirstDifferingBlock(lastSkin.Fingerprint, skinFingerprint)
                    : lastSkin.Redirects.Count == 0 ? "last publish had no skin redirects"
                    : "published skin output missing on disk");
            }
        }

        private void InheritMaskColorsets()
        {
            // ── Inherited mask colorsets ─────────────────────────────────────
            // A mod whose masks carry an _id but whose Masks tab has no colorset paints them from the topmost overlay it draws
            // on that material, keyed per (material, mod). Built before the Parallel.ForEach so nothing is mutated inside it.
            maskFallbackRows = new Dictionary<string, Dictionary<int, ColorTableRowOverride>>(
                StringComparer.OrdinalIgnoreCase);
            {
                var loggedInherit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (mtrl, list) in byMaterial)
                    // `list` is in composite order (bottom→top), so Last() is the overlay the mask sits on.
                    foreach (var modGroup in list.GroupBy(p => p.Entry.ModDirectory, StringComparer.OrdinalIgnoreCase))
                    {
                        var modDir = modGroup.Key;
                        if (maskRowsByMod.ContainsKey(modDir) || maskShellMods.Contains(modDir)) continue;
                        if (!maskAssetsByMod.TryGetValue(modDir, out var mA) || !mA.Any(a => a.IndexPath != null))
                            continue;

                        // A print is not a surface a mask sits on, and always sorts last.
                        var painters = modGroup.Where(p => !AnyBlendRow(p.Overlay.ColorTableRows)).ToList();
                        var top = painters.Count > 0 ? painters[^1] : modGroup.Last();
                        // An empty colorset leaves the dictionary empty, which ApplyIndexedOverlay reads as neutral white.
                        var inherited = BuildRowDict(top.Overlay.ColorTableRows);
                        maskFallbackRows[MaskFallbackKey(mtrl, modDir)] = inherited;
                        if (loggedInherit.Add(modDir))
                            compositor.log.Debug("[Proteus] Masks on \"{0}\": no Masks colorset — inheriting \"{1}\"'s "
                                    + "{2} row(s) so the mask still paints on skin",
                                modDir, top.Overlay.Option ?? "(default)", inherited.Count);
                    }
            }
        }

        private void ColdPrefetch()
        {
            // Decode a file in the background purely to warm the cache; callers never await it (time excluded from DecodeWaitStats).
            // Issued once while in flight, retired on completion so an evicted file can be warmed again. Bounded to half the
            // cores so the prefetch cannot starve the blend loop it runs ahead of.
            prefetchIssued = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            prefetchGate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));

            // ── Cold-start prefetch ──────────────────────────────────────────
            // The blend loop cannot prefetch its own first overlays (sizes follow the base texture). LoadBaseTexture upscales
            // to BaseTargetSize, so warm at that; a wrong guess wastes a decode, never a pixel.
            const int coldSize = TextureLoader.BaseTargetSize;
            foreach (var list in byMaterial.Values)
                foreach (var (cEntry, cOverlay) in list.Take(PrefetchDepth))
                {
                    var cd = cOverlay.Descriptor;
                    if (cd.Diffuse != null) WarmBg(Path.Combine(cEntry.SidecarRoot, cd.Diffuse), coldSize, coldSize);
                    if (cd.Normal  != null) WarmBg(Path.Combine(cEntry.SidecarRoot, cd.Normal),  coldSize, coldSize);
                    if (cd.Index   != null) WarmBg(Path.Combine(cEntry.SidecarRoot, cd.Index),   coldSize, coldSize,
                                                   ResampleFilter.Nearest);
                }

            tSetupEnd = PhaseCounter.Begin();
            timeline.MarkAt("setup", tSetupEnd);
        }

        private void CompositeSkin()
        {
            // Drop the previous run's answers first, so a cancelled composite never leaves a stale panel. Empty = "no answer yet".
            compositor._channelContributions = [];

            // Compression (opt-in): BC7 for every skin channel; the skin normal uses B/A, so BC5 would corrupt it.
            // The machine gets a vote: on hardware too slow to encode, compressing costs more refresh time than the
            // gap between the triggers that restart a composite, so honouring the setting would publish nothing at all.
            bool compress = compositor.config.EnableCompression && compositor.textureLoader.CompressionAffordable();

            // Salted with dimensions and encoding (0 = uncompressed, 1 = native BC7, 2 = managed BC7; the encoders differ
            // byte-for-byte), since both change the file without changing the RGBA. Loop-invariant: the base diffuse is
            // fingerprinted at load time with the same salts.
            int encSalt = compress ? (TextureLoader.NativeEncoderAvailable ? 1 : 2) : 0;

            // Skipped wholesale when the skin is reused; its redirects are seeded above and other outputs restored below.
            // Braced: the guarded statement runs over a thousand lines.
            if (!skinReused)
            {
            Parallel.ForEach(byMaterial, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
                kvp => CompositeMaterial(kvp, compress, encSalt));
            }   // end: if (!skinReused)
        }

        /// <summary>Blends every overlay targeting one skin material and publishes its textures; runs in parallel per material.</summary>
        private void CompositeMaterial(KeyValuePair<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>> kvp, bool compress, int encSalt)
        {
            new MaterialComposite(this, kvp, compress, encSalt).Run();
        }

        private void CollectSkinResults()
        {
            // Filtered and sorted once per composite for the status window; rows nothing reached or meant to reach are dropped.
            // On skin reuse, restore the last real skin composite's outputs. Into locals, published below the supersede check,
            // so a losing run never leaves the locators describing itself.
            nextChannelContributions = skinReused
                ? lastSkin!.Contributions
                : contributions.Values
                    .Where(c => c.Touched || c.DiffuseWanted || c.Diffuse + c.Normal + c.Mask > 0)
                    .OrderBy(c => c.Material, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            compositor.LogPhaseBreakdown(tRunStart, tSetupEnd, skinReused ? 0 : byMaterial.Count);
            timeline.Mark(skinReused ? "skin (reused)" : "skin");

            // The glow recipes gathered above (empty if no indexed skin overlays).
            nextSkinGlowTargets = skinReused
                ? new Dictionary<(string, string?, string?), List<Proteus.Interop.SkinGlowTarget>>(lastSkin!.GlowTargets)
                : new Dictionary<(string, string?, string?), List<Proteus.Interop.SkinGlowTarget>>(skinGlow);

            // Everything in `redirects` so far is the skin's (the shell adds its own below): the snapshot a later run reuses.
            skinRedirectsThisRun = new Dictionary<string, string>(redirects, StringComparer.OrdinalIgnoreCase);
        }

        private bool BuildShells()
        {
            return new ShellPhase(this).Run();
        }

        private bool CommitLocators()
        {
            // ── Superseded: stop before the publish ───────────────────────────
            // Protects publish integrity: WriteManagedModJson replaces the manifest wholesale, so a stale run finishing second
            // would reinstate old output and clear _forcePending. Ahead of every publish below; the gear phase writes only next* locals.
            if (compositor.Superseded(epoch))
            {
                compositor.log.Information("[Proteus] recomposite superseded — a newer composite is running; not "
                              + "publishing ({0:F0}ms in)", PhaseCounter.MsSince(tRunStart));
                return false;
            }

            // A rewritten face model needs a full redraw, both when added and when removed (a .mdl is never re-fetched in place).
            var faceUvPaths = new HashSet<string>(facePlan.ModelRedirects.Keys, StringComparer.OrdinalIgnoreCase);
            if (facePlan.AnyModelChanged || !faceUvPaths.SetEquals(compositor._lastFaceUvModelPaths))
            {
                nextNeedFullRedraw = true;
                compositor.log.Debug("[Proteus] face uv: the set of rewritten face models changed ({0} now, {1} before) "
                        + "— full redraw", faceUvPaths.Count, compositor._lastFaceUvModelPaths.Count);
            }
            compositor._lastFaceUvModelPaths = faceUvPaths;

            // Same for skin materials rewritten off a shared texture: a withdrawn redirect would leave the loaded material naming
            // a path nothing serves. Compared by value too: a changed upstream is a new copy.
            var skinMaterials = skinRedirectsThisRun
                .Where(kv => kv.Key.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            if (skinMaterials.Count != compositor._lastSkinMaterialRedirects.Count
             || skinMaterials.Any(kv => !compositor._lastSkinMaterialRedirects.TryGetValue(kv.Key, out var was)
                                     || !string.Equals(was, kv.Value, StringComparison.OrdinalIgnoreCase)))
            {
                nextNeedFullRedraw = true;
                compositor.log.Debug("[Proteus] skin materials: the rewritten set changed ({0} now, {1} before) — full redraw",
                    skinMaterials.Count, compositor._lastSkinMaterialRedirects.Count);
            }
            compositor._lastSkinMaterialRedirects = skinMaterials;
            // Shared with the editor, so its "Rendering as" badge cannot disagree with what was composited.
            compositor._faceDoubledMaterials = facePlan.Materials;

            compositor._needFullRedraw    = nextNeedFullRedraw;
            compositor._secondSkinActive  = nextSecondSkinActive;
            if (nextShellHostPaths != null) compositor._lastShellHostPaths = nextShellHostPaths;

            // Only on a real change (disk I/O), under _bodyModConfigLock like every other off-thread save.
            if (nextAppendHosts != null && !nextAppendHosts.SetEquals(compositor._appendHostModelPaths))
            {
                compositor._appendHostModelPaths = nextAppendHosts;
                lock (compositor._bodyModConfigLock)
                {
                    compositor.config.AppendHostModelPaths = [.. nextAppendHosts];
                    compositor.config.Save();
                }
            }

            // Publish the locators in one step. Null ⇒ no shell built, when the editor should see them empty. Early-return
            // tear-downs use ClearShellLocators instead.
            compositor._shellMaterials   = nextShellMaterials ?? new();
            compositor._contentMaterials = nextContentMaterials ?? new(StringComparer.OrdinalIgnoreCase);
            compositor._contentModels    = nextContentModels ?? new(StringComparer.OrdinalIgnoreCase);
            compositor._shellLight       = nextShellLight ?? new(StringComparer.OrdinalIgnoreCase);
            compositor._shellDrawnCheck  = nextShellDrawnCheck;

            // No shell built but hosts were redirected last time: force a full redraw to reload the vacated accessories.
            if (!shellBuilt && compositor._lastShellHostPaths.Count > 0)
            {
                compositor._needFullRedraw = true;
                compositor._lastShellHostPaths = new(StringComparer.OrdinalIgnoreCase);
                compositor.log.Debug("[Proteus] second skin removed — forcing a full redraw to restore host accessories");
            }

            // Record the enabled-shape signature on every composite (the only write on the publishing path), or
            // SchedulePostRedrawBodyTypeCheck sees a permanent mismatch.
            compositor._lastCompositedBodyShapeSig = BodyShapeSignature(compositor._bodyShapeSnapshot);

            // Runs entirely after the composite, adding to the user-visible delay.
            if (gearOverlays.Count > 0 || contentLayers.Count > 0)
            {
                var gearMs = PhaseCounter.MsSince(tGear);
                compositor.log.Information("[Proteus] recomposite phases: second skin {0:F0}ms ({1} gear layer(s), "
                              + "{2} content piece(s)) — {3}",
                    gearMs, gearOverlays.Count, contentLayers.Count, compositor.secondSkin.DescribeBuildStats(gearMs));
            }
            timeline.Mark("second skin");

            compositor.WriteManagedModJson(redirects, manipulations);
            return true;
        }

        private void Publish()
        {
            // Read before the reload: ReloadAndRedraw consumes the flag.
            reloadKind = !compositor.config.AutoRedraw ? "withheld" : compositor._needFullRedraw ? "full redraw" : "in-place";

            // Publish only: nothing is deleted in this composite; superseded output is collected at the top of the next one
            // (see PruneSupersededOutput).
            manifestConfirmedLive = compositor.ReloadAndRedrawWhenReady(redirects, compositor.RecordPublish(redirects));
            timeline.Mark("publish+reload");

            // Published: the manifest is now exactly these inputs' output, so set the fingerprint and settle the forced-work
            // latch. Only on this path, so the gate never claims unproduced output.
            compositor._lastCompositeFingerprint = fingerprint;
            // The skin half, in one reference swap, only on publish (assigned even on reuse).
            compositor._lastSkinPublish = new SkinPublish(
                skinFingerprint, skinRedirectsThisRun, compositor._channelContributions, compositor._skinGlowTargets);
            Interlocked.Exchange(ref compositor._forcePending, 0);
            Interlocked.Exchange(ref compositor._skinForcePending, 0);

            // Widen the recorded base set to what this run actually resolved (_upstreamByGamePath holds every base resolved,
            // including unpublished texture bases). Authoritative only when the skin was not reused, since the blend loop
            // resolves those extra bases.
            // The models the shell is cut from, which nothing else puts in the memo.
            compositor.RecordShellSourceUpstreams();

            compositor.RecordCompositeBaseKeys(baseKeys.Concat(compositor._upstreamByGamePath.Keys), baseSignature,
                authoritative: !skinReused);
            // What those base paths resolved to, so the next settings change is judged against a composite that happened.
            compositor._lastBaseUpstreams = new Dictionary<string, string>(compositor._upstreamByGamePath, StringComparer.OrdinalIgnoreCase);
        }

        private void ReconcileAndVerify()
        {
            // Reconcile the injected host items after the redirect mod is live, so the equip's redraw lands on the shell. Each
            // reconcile is told whether the shell went to its host. "Wanted" includes content packs: it separates a transient
            // failed build (keep the carrier) from nothing wanting a host (remove it).
            bool hostWanted = gearOverlays.Count > 0 || contentLayers.Count > 0;
            compositor.RememberHostDecision(hostWanted, shellBuilt, shellOnFacewear,
                                 shellBuilt ? shellCarrierSlots : []);
            compositor.ReconcileInvisibleGlasses(hostWanted, shellBuilt, shellOnFacewear, glassesPreHosted);
            compositor.ReconcileEmperorRing(hostWanted, shellBuilt, shellBuilt ? shellCarrierSlots : []);
            timeline.Mark("carriers");

            // Every path, not just the shell's: the skin textures are held only by priority against the body mod that invented
            // them. Below the reconciles (it can block on Penumbra). Takes ct: a newer composite may republish meanwhile, and a
            // superseded expectation would produce false accusations.
            compositor.VerifyRedirectsLive(redirects, manifestConfirmedLive, ct);
            timeline.Mark("verify");

            // Winning the path is not the game having drawn it; this check runs after the redraw. Withheld only when the shell
            // needed the reload and auto redraw is off.
            compositor.SchedulePostRedrawShellCheck(reloadWithheld: !compositor.config.AutoRedraw && shellNeedsReload, timeline);

            compositor.LastResult = new CompositorResult
            {
                Success = true,
                // On skin reuse the redirects were carried forward: report what the manifest carries.
                TexturesPatched = skinReused ? skinRedirectsThisRun.Count : texturesPatched,
                OverlayModsUsed = entries.Count,
            };
            compositor.ResultChanged?.Invoke();

            // Wall clock from start to everything done, which no single phase line sums to.
            compositor.log.Information("[Proteus] recomposite DONE — {0:F0}ms total", PhaseCounter.MsSince(tRunStart));
            compositor.LogRefreshTimeline(timeline, reloadKind);
        }

        // The mod's shared "Masks" colorset, with the design binding's override applied when it has one
        // (OverlayColorOverride.Mask); otherwise the live metadata mask rows.
        private List<ColorTableRowPreset>? MaskRowsFor(OverlayEntry e)
        {
            if (colorOverride != null && colorOverride.TryGetValue(e.ModDirectory, out var ov)
                && ov.Mask is { Count: > 0 } m)
                return m;
            return e.Metadata.MaskColorTableRows;
        }

        // The Masks tab's effective render-mode descriptor (with the binding's mask gear override), or null when the mask
        // is plain Skin. Mirrors MaskRowsFor.
        private OverlayDescriptor? MaskDescriptorFor(OverlayEntry e)
        {
            var baseDesc = e.Metadata.MaskDescriptor;
            var ovr = gearOverride != null && gearOverride.TryGetValue(e.ModDirectory, out var g)
                ? g.Mask : null;
            if (baseDesc == null && ovr == null) return null;
            var desc = baseDesc != null ? CloneDescriptor(baseDesc) : new OverlayDescriptor();
            ovr?.ApplyTo(desc);
            return desc;
        }

        private int ModStackIndexFor(string modDir, string group, string option)
            => stackOverride != null && stackOverride.TryGetValue(modDir, out var order)
                ? Configuration.ModStackIndexIn(order, group, option)
                : compositor.config.ModStackIndexOf(modDir, group, option);

        // Shared with the editor (static NeedsUnmirroredShell); every overlay is judged against one reading of the worn body.
        private bool NeedsUnmirroredShell(OverlayDescriptor d)
            => CompositorService.NeedsUnmirroredShell(d, wearingMirroredBody, faceDoubledMaterials);

        // A mask occludes everything beneath it: in its territory every group is erased to skin, and lowering its opacity
        // fades toward bare skin. Masks never add coverage.
        private static bool MaskAdds(OverlayEntry e, ResolvedOverlay o) => false;

        // The filter is part of the decode-cache key, so it must be warmed with the one the build will request.
        private void WarmBg(string path, int w, int h, ResampleFilter filter = ResampleFilter.Auto)
        {
            var warmKey = $"{path}|{w}x{h}|{filter}";
            if (!prefetchIssued.TryAdd(warmKey, 0)) return;
            _ = Task.Run(async () =>
            {
                try { await prefetchGate.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { prefetchIssued.TryRemove(warmKey, out _); return; }
                catch (ObjectDisposedException) { prefetchIssued.TryRemove(warmKey, out _); return; }
                try
                {
                    // Set after the await: the flag is [ThreadStatic] and must be on the thread that runs the decode.
                    TextureLoader.BackgroundPrefetch = true;
                    compositor.textureLoader.LoadPngAsRgba(path, w, h, filter);
                }
                catch { }
                finally
                {
                    TextureLoader.BackgroundPrefetch = false;
                    prefetchGate.Release();
                    // Retired here, not held for the composite (see above).
                    prefetchIssued.TryRemove(warmKey, out _);
                }
            }, ct);
        }
    }
}
