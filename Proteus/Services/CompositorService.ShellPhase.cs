using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Penumbra.Api.Enums;
using Proteus.Interop;

namespace Proteus.Services;

public partial class CompositorService
{
    private sealed partial class CompositeRun
    {
        private sealed class ShellPhase
        {
            private readonly CompositeRun run;
            private string? bodyType;
            private IReadOnlyDictionary<string, string> equippedModels = null!;
            private IReadOnlyDictionary<string, string> equippedAccessories = null!;
            private IReadOnlyList<string>? metModels;
            private IReadOnlyDictionary<string, string> bareBodyModels = null!;
            private int? invisibleGlassesSet;
            private IReadOnlyDictionary<string, HashSet<string>>? bodyShapes;
            private HashSet<string>? hostMtrl;
            private List<EquippedSlotVariants.Slot>? slotVariants;
            private SecondSkinService.Result? shells;

            public ShellPhase(CompositeRun run)
            {
                this.run = run;
            }

            public bool Run()
            {
                // ── Superseded: stop before the shell build ───────────────────────
                // A run superseded during the blend must not build a second skin and republish over its replacement. Here, before
                // shell state stops describing the published manifest, and before SecondSkinService.Build clears state shared with
                // the winner. The UI locators and every latch are set only at the publish below, so the previous values stand.
                if (run.compositor.Superseded(run.epoch))
                {
                    run.compositor.log.Information("[Proteus] recomposite superseded — a newer composite is running; dropping "
                                  + "this one before the shell build ({0:F0}ms in)",
                        PhaseCounter.MsSince(run.tRunStart));
                    return false;
                }

                run.compositor._channelContributions = run.nextChannelContributions;
                run.compositor._skinGlowTargets      = run.nextSkinGlowTargets;

                // ── Second skin: one gear shell per Layer:Gear overlay ────────────
                // Built from the body model the character is currently drawing (resolved live through Penumbra).
                run.manipulations = null;
                // Built into locals and published below the supersede check. _appendHostModelPaths is written through to config,
                // so a stale value would not self-correct.
                run.nextNeedFullRedraw = false;
                // The shell half of nextNeedFullRedraw: its model changed, or a forced run is redrawing to unstick it
                // (see SchedulePostRedrawShellCheck's reloadWithheld).
                run.shellNeedsReload = false;
                run.nextSecondSkinActive = false;
                run.nextShellHostPaths = null;
                run.nextAppendHosts = null;
                // The three UI-facing locators are built into locals and published in one step after the gear phase, never cleared
                // mid-composite: the editor's Glow locator reads them every frame and would fire a warmup recomposite on an empty map.
                run.nextShellMaterials = null;
                run.nextShellLight = null;
                run.nextContentMaterials = null;
                run.nextContentModels = null;
                run.nextShellDrawnCheck = null;
                run.shellBuilt = false;   // a gear shell was produced this composite (drives glasses reconcile)
                // The shell was built for invisible glasses not yet equipped (ChooseHost's pending branch); the equip's redraw lands on it.
                run.glassesPreHosted = false;
                // Which host the shell was published onto; each reconcile below is driven by where the shell went.
                run.shellOnFacewear = false;
                    // The accessory slots the shell published a carrier onto (possibly several).
                run.shellCarrierSlots = [];
                run.tGear = PhaseCounter.Begin();
                // maskShellMods and content packs too: a mask promoted to Cloth/Glow on an all-skin mod has no gear overlay, and its
                // skin passes already skipped it. Build no-ops on an empty list.
                if (run.gearOverlays.Count > 0 || run.maskShellMods.Count > 0 || run.contentLayers.Count > 0)
                {
                    // Same stacking rules as the skin composite; SecondSkinService assigns shell letters in this order.
                    run.gearOverlays = run.gearOverlays
                        .OrderBy(p => p.Entry.Priority)
                        .ThenByDescending(p => run.ModStackIndexFor(p.Entry.ModDirectory, p.Overlay.OptionGroup ?? "", p.Overlay.Option ?? ""))
                        .ThenByDescending(p => p.Overlay.GroupOrder)
                        .ThenByDescending(p => run.compositor.config.StackIndexOf(p.Entry.ModDirectory, p.Overlay.OptionGroup ?? "", p.Overlay.Option ?? ""))
                        .ToList();

                    // ── Top mask shell ────────────────────────────────────────────
                    // Each mask-shell mod gets a dedicated shell, appended after the sort so it takes the highest letter (on top).
                    // Coverage, _id and relief come from the mod's active masks (IsMaskShell); its other shells skip the mask merge.
                    foreach (var mod in run.maskShellMods)
                    {
                        // Seed from a sibling gear overlay, else any of the mod's overlays; only SourceBodyType and GroupOrder are read off it.
                        var seed = run.gearOverlays.FirstOrDefault(g => g.Entry.ModDirectory == mod);
                        // Only a gear seed's colorset is worth inheriting; a skin overlay's rows would paint the mask in body tone.
                        bool seededFromGear = seed.Entry != null;
                        if (seed.Entry == null) seed = run.allOverlays.FirstOrDefault(g => g.Entry.ModDirectory == mod);
                        if (seed.Entry == null) continue;   // no overlay to source a body type from

                        // The mask's own render mode: Cloth by default, or its shader/scroll (Glow ⇒ characterscroll.shpk). Same instance
                        // as the promotion loop and fingerprint (maskDescByMod).
                        run.maskDescByMod.TryGetValue(mod, out var md);
                        var maskDesc = new OverlayDescriptor
                        {
                            Layer          = OverlayLayer.Gear,
                            IsMaskShell    = true,
                            SourceBodyType = seed.Overlay.Descriptor.SourceBodyType,
                            Shader         = md?.Shader,
                            Scroll         = md?.Scroll,
                            ScrollSpeedX   = md?.ScrollSpeedX,
                            ScrollSpeedY   = md?.ScrollSpeedY,
                            ScrollTilingX  = md?.ScrollTilingX,
                            ScrollTilingY  = md?.ScrollTilingY,
                        };
                        var maskResolved = seed.Overlay with
                        {
                            Descriptor     = maskDesc,
                            // Its own Masks colorset, else the gear fabric's; with neither, null gives the neutral-white baseline. (The skin
                            // fallback inherits from skin overlays too, which is correct on skin.)
                            ColorTableRows = run.MaskRowsFor(seed.Entry)
                                          ?? (seededFromGear ? seed.Overlay.ColorTableRows : null),
                            OptionGroup    = SidecarDiscoveryService.MaskGroupName,
                            Option         = "Masks",
                        };
                        run.gearOverlays.Add((seed.Entry, maskResolved));
                    }

                    // ── Gear-shell prefetch ──────────────────────────────────────
                    // This phase is decode-bound: fire its decodes now, next to their consumer (earlier warms get evicted by the skin
                    // composite). The size must be the one Build will ask for, so it is chosen once here and passed as shellTexSize.
                    int gs = SecondSkinService.ChooseTexSize(
                        SecondSkinService.ShellArtPaths(run.gearOverlays, run.compositor.discovery));
                    foreach (var (gEntry, gOverlay) in run.gearOverlays)
                    {
                        var gd = gOverlay.Descriptor;
                        if (gd.Diffuse != null) run.WarmBg(Path.Combine(gEntry.SidecarRoot, gd.Diffuse), gs, gs);
                        if (gd.Normal  != null) run.WarmBg(Path.Combine(gEntry.SidecarRoot, gd.Normal),  gs, gs);
                        if (gd.Index   != null) run.WarmBg(Path.Combine(gEntry.SidecarRoot, gd.Index),   gs, gs,
                                                       ResampleFilter.Nearest);
                        if (run.maskPathsByMod.TryGetValue(gEntry.ModDirectory, out var gMasks))
                            foreach (var mp in gMasks) run.WarmBg(mp, gs, gs);
                        if (run.maskAssetsByMod.TryGetValue(gEntry.ModDirectory, out var gAssets))
                            foreach (var a in gAssets)
                            {
                                if (a.NormalPath != null) run.WarmBg(a.NormalPath, gs, gs);
                                if (a.IndexPath  != null) run.WarmBg(a.IndexPath,  gs, gs, ResampleFilter.Nearest);
                            }
                    }

                    var charCode = (run.compositor._glamourerCharCode ?? run.compositor._lastCompositedCharCodes?.Split(',').FirstOrDefault())
                        ?.TrimStart('c', 'C');
                    if (string.IsNullOrEmpty(charCode))
                        run.compositor.log.Warning("[Proteus] {0} gear overlay(s) and {1} content piece(s) skipped: "
                                  + "no character code yet", run.gearOverlays.Count, run.contentLayers.Count);
                    else
                        try
                        {
                            GatherInputs(charCode);
                            BuildAndApply(gs, charCode);
                            ApplyShells();
                        }
                        catch (Exception ex) { run.compositor.log.Error(ex, "[Proteus] second skin build failed"); }
                }
                return true;
            }

            private void GatherInputs(string? charCode)
            {
                // The shell inherits the body's UVs, so remap into this character's body material's space (not the accessory's,
                // and not whichever of several _lastCompositedBodyType entries sorts first).
                bodyType = run.activeMtrl?
                    .Where(m => m.Contains($"/c{charCode}/obj/body/", StringComparison.OrdinalIgnoreCase))
                    .Select(UVRemapService.InferBodyType)
                    .FirstOrDefault(t => t != null)
                    ?? run.compositor._lastCompositedBodyType?.Split(',').FirstOrDefault();

                // Each equipped slot's shell is cut from the model the character draws there (gear poses the skin it exposes).
                // Retry the walk if the trigger-time one found nothing, and pass maps through without coalescing null: null means
                // "unknown" to the host chooser, not "empty". Wrapped: a throw here must not cost the whole shell.
                if (run.compositor._equippedPartModels == null)
                {
                    var known = false;
                    try { known = run.compositor.RefreshEquippedModels(); }
                    catch (Exception ex)
                    {
                        run.compositor.log.Debug("[Proteus] second skin: equipped-model retry could not run ({0})",
                            ex.GetType().Name);
                    }
                    if (!known)
                        run.compositor.log.Warning("[Proteus] second skin: no draw-object walk has succeeded yet — "
                                  + "host choice will avoid anything that replaces worn gear");
                }

                equippedModels = run.compositor._equippedPartModels
                    ?? new Dictionary<string, string>();
                equippedAccessories = run.compositor._equippedAccessoryModels
                    ?? new Dictionary<string, string>();
                metModels = run.compositor._equippedMetModels;
                // Bare slots come from the same walk; a slot missing from both lists has no geometry for the shell.
                bareBodyModels = run.compositor._bareBodyModels ?? new Dictionary<string, string>();
                run.compositor.log.Information("[Proteus] second skin: equipped part models [{0}], accessories [{1}], head/met [{2}], bare [{3}] ({4})",
                    string.Join(", ", equippedModels.Select(kv => $"{kv.Key}={kv.Value}")),
                    string.Join(", ", equippedAccessories.Select(kv => $"{kv.Key}={kv.Value}")),
                    metModels == null ? "unknown" : string.Join(", ", metModels),
                    string.Join(", ", bareBodyModels.Select(kv => $"{kv.Key}={kv.Value}")),
                    run.compositor._equippedPartModels == null ? "cache null" : "cached");
                // Shells are cut from these (SecondSkinService.ResolveHumanSurface); logged to show whether the walk reports them.
                run.compositor.log.Information("[Proteus] second skin: human part models [{0}]",
                    string.Join(", ", run.compositor._humanPartModels ?? []));

                // Our injected invisible-glasses set, so the shell replaces its model rather than appending (see ChooseHost).
                invisibleGlassesSet = run.compositor.config.AutoInvisibleGlasses
                    ? InvisibleGlasses.Resolve(Plugin.DataManager, run.compositor.log)?.ModelSet : null;
                // The chooser knows our pair only by model set, which real spectacles share: hide the set when the worn pair is
                // not one we equipped. Our carrier item worn by the player's choice is theirs too, and is never rewritten.
                if (invisibleGlassesSet is int ourSet && run.compositor.IsOurGlassesWorn(ourSet)
                    && InvisibleGlasses.Resolve(Plugin.DataManager, run.compositor.log) is { } ourGlasses
                    && !(run.compositor._injectedGlasses && run.compositor.IsOurGlassesItemWorn(ourGlasses)))
                    invisibleGlassesSet = null;

                // Snapshot the volatile shape set once, so the bake and its signature see the same value.
                bodyShapes = run.compositor._bodyShapeSnapshot;

                // The host material folder, read now: activeMtrl is stamped before a freshly worn accessory's materials load.
                // Used only for the folder; the rest of the composite keeps keying off activeMtrl.
                hostMtrl = run.activeMtrl;
                slotVariants = null;
                try
                {
                    var live = Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        var player = Plugin.ObjectTable.LocalPlayer;
                        return (Materials: run.compositor.penumbra.GetActivePlayerMaterialPaths(),
                                Slots: Interop.EquippedSlotVariants.Read(player?.Address ?? 0));
                    }).GetAwaiter().GetResult();
                    // Empty is a mid-teardown walk; the snapshot is the better answer then.
                    if (live.Materials is { Count: > 0 }) hostMtrl = live.Materials;
                    slotVariants = live.Slots;
                }
                // Everything, cancellation included: this read has a fallback.
                catch (Exception ex)
                {
                    run.compositor.log.Debug("[Proteus] second skin: live host-variant read could not run ({0})",
                        ex.GetType().Name);
                }
            }

            private void BuildAndApply(int gs, string? charCode)
            {
                // gen2 shell parts follow the per-mod gen2 checkbox, except for a mod whose art must be un-mirrored (vanilla is the worn body).
                shells = run.compositor.secondSkin.Build(charCode!, run.gearOverlays, run.compositor.managedModDir, bodyType,
                    run.compositor.discovery.EffectsLibraryPath(), equippedModels, equippedAccessories,
                    modDir => run.unmirrorMods.Contains(modDir) || run.compositor.config.OverlaysVanillaFor(modDir),
                    invisibleGlassesSet, metModels, bodyShapes, run.maskShellMods, bareBodyModels,
                    run.compositor._drawnRaceCode, hostMtrl,
                    InvisibleRing.Resolve(Plugin.DataManager, run.compositor.log)?.Variant,
                    InvisibleGlasses.Resolve(Plugin.DataManager, run.compositor.log)?.Variant,
                    run.compositor._humanPartModels, run.contentLayers,
                    // Skin-layer mods count too: a toe cap map is about the foot.
                    run.entries.Concat(run.gearOverlays.Select(g => g.Entry))
                           .GroupBy(e => e.ModDirectory, StringComparer.OrdinalIgnoreCase)
                           .Select(g => g.First()).ToList(),
                    // The faces we rewrote, as installed, so a shell cut from the same face doesn't read our doubled model.
                    run.facePlan.UpstreamModels,
                    // The size the prefetch above warmed at — see `gs`.
                    shellTexSize: gs,
                    equippedSlotVariants: slotVariants);
            }

            private void ApplyShells()
            {
                if (shells != null)
                {
                    run.shellBuilt = true;
                    // Where the shell landed, read off its published paths: this decides which item must be equipped for it to render.
                    run.shellOnFacewear = shells.HostModelPaths.Any(
                        p => p.EndsWith("_met.mdl", StringComparison.OrdinalIgnoreCase));
                    // ...and in which slots: a carrier only loads from the slot it published for.
                    run.shellCarrierSlots = InvisibleRing.CarrierSlots
                        .Where(c => shells.HostModelPaths.Any(p =>
                            p.Contains($"a{InvisibleRing.EmperorSetId:D4}", StringComparison.OrdinalIgnoreCase)
                         && p.EndsWith($"_{c.Slot}.mdl", StringComparison.OrdinalIgnoreCase)))
                        .Select(c => c.Slot).ToList();
                    // Mirrors ChooseHost's pending-injection branch: feature on and "_met" known empty. Unknown (null) is not empty.
                    run.glassesPreHosted = invisibleGlassesSet is int && metModels is { Count: 0 };
                    // Verified after publishing along with everything else (see VerifyRedirectsLive).
                    foreach (var (gamePath, relPath) in shells.Redirects)
                        run.redirects[gamePath] = relPath;
                    run.manipulations = shells.Manipulations;
                    run.nextSecondSkinActive = true;   // an accessory model was redirected — disable must full-redraw

                    // Only new geometry forces the heavy path: an in-place reload applies a colorset-only .mtrl change. An enabled
                    // shape-key change is a redraw trigger in its own right (ModelChanged can miss it).
                    var shapeSig = BodyShapeSignature(bodyShapes);
                    // Compared, not stored: the publish below records the signature.
                    bool shapesChanged = !string.Equals(shapeSig, run.compositor._lastCompositedBodyShapeSig, StringComparison.Ordinal);

                    // A host dropped from the set needs a full redraw so the vacated accessory reloads its real model.
                    var hostPaths = new HashSet<string>(shells.HostModelPaths, StringComparer.OrdinalIgnoreCase);
                    bool hostsChanged = !hostPaths.SetEquals(run.compositor._lastShellHostPaths);
                    run.nextShellHostPaths = hostPaths;

                    // A forced trigger producing no change: the in-place reload cannot repair a shell whose host the game never
                    // reloaded, so redraw. Gated on "nothing changed" (colour edits are forced too), and skipped once the drawn check
                    // has confirmed this shell is on the character.
                    bool nothingChanged = !shells.ModelChanged && !shapesChanged && !hostsChanged
                                       && !shells.ShellChanged;
                    // Read against the probe still standing (the shell worn now); a changed shell no longer matches its key.
                    var standing = run.compositor._shellDrawnCheck;
                    bool confirmedDrawn = standing != null
                        && string.Equals(run.compositor._shellConfirmedDrawnKey, ShellProbeKey(standing), StringComparison.Ordinal);
                    bool unstickShell = run.force && nothingChanged && !confirmedDrawn;
                    run.nextNeedFullRedraw = shells.ModelChanged || shapesChanged || hostsChanged
                                      || unstickShell;
                    run.shellNeedsReload = run.nextNeedFullRedraw;
                    if (unstickShell)
                        run.compositor.log.Debug("[Proteus] second skin unchanged on a forced composite and not yet "
                                + "confirmed drawn — redrawing anyway, since an in-place reload "
                                + "can't reload a host accessory");
                    else if (run.force && nothingChanged)
                        run.compositor.log.Debug("[Proteus] second skin unchanged on a forced composite and already "
                                + "confirmed drawn — no redraw needed");
                    if (shells.ModelChanged)
                        run.compositor.log.Debug("[Proteus] second skin model changed — forcing a full redraw");
                    else if (shapesChanged)
                        run.compositor.log.Debug("[Proteus] second skin body shapes changed — forcing a full redraw");
                    else if (hostsChanged)
                        run.compositor.log.Debug("[Proteus] second skin host set changed — forcing a full redraw");
                    else if (shells.ShellChanged)
                        run.compositor.log.Debug("[Proteus] second skin material/textures changed — in-place reload");
                    run.nextShellMaterials = shells.ShellMaterials;
                    run.nextContentMaterials = shells.ContentMaterials;
                    run.nextContentModels = shells.ContentModels;
                    run.nextShellLight = shells.ShellLight;

                    // Materials to test, models to anchor the test against (see ShellDrawnProbe).
                    run.nextShellDrawnCheck = new ShellDrawnProbe(
                        [.. shells.Redirects.Keys.Where(k => k.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))],
                        [.. shells.Redirects.Keys.Where(k => k.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))]);

                    // Which hosts we appended into, for PrimeUpstreamCache. Persisted, since the manifest masks them after a restart.
                    // Captured here, compared and saved below the supersede check.
                    run.nextAppendHosts =
                        new HashSet<string>(shells.AppendHostModelPaths, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
    }
}
