using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Localization;

namespace Proteus.Services;
using static Proteus.Services.ContentPieceResolver;
using static Proteus.Services.ShellColorRows;

public sealed partial class SecondSkinService
{
    private sealed partial class ShellSetBuild
    {
        private sealed class ContentResolution
        {
            private readonly ShellSetBuild build;
            private Dictionary<string, ContentUnit> unitByKey = null!;
            private HashSet<string> unitGeometry = null!;
            private Dictionary<string, (byte[]? Model, List<string> Used, List<string> Attrs)> modelCache = null!;
            private Dictionary<string, string?> mtrlFileCache = null!;
            private Dictionary<string, ILookup<string, string>> shippedCache = null!;
            private Dictionary<string, string> unwearable = null!;
            private List<(string ModDir, string Group, string Option, string Reason)> refusals = null!;
            private Dictionary<string, IReadOnlyDictionary<string, List<string>>?> selectionCache = null!;
            private Dictionary<string, ContentImcEdits?> imcEditsCache = null!;
            private Dictionary<(ImcItem Item, int Variant), ushort?> vanillaMaskCache = null!;

            public ContentResolution(ShellSetBuild build)
            {
                this.build = build;
            }

            public void Run()
            {
                PrepareCaches();
                ResolveUnits();
                PublishUnwearable();
            }

            private void PrepareCaches()
            {
                // ── imported content, resolved into units before anything is allocated ────────────────
                // A UNIT is one published MATERIAL and every mesh drawn with it, since a material costs a host slot. Resolved
                // before allocation so a piece that cannot be built never consumes capacity, and its reason is reported once.
                build.contentUnits = new List<ContentUnit>();
                unitByKey = new Dictionary<string, ContentUnit>(StringComparer.OrdinalIgnoreCase);
                unitGeometry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Model path → its bytes, the materials its LOD0 meshes draw with, and its attributes. A null Model is an
                // unreadable file, cached so the warning prints once.
                modelCache = new Dictionary<string, (byte[]? Model, List<string> Used, List<string> Attrs)>(
                        StringComparer.OrdinalIgnoreCase);

                // Mod directory → why none of its pieces can be worn, for the panel. Per mod; first reason wins.
                // (EST slot, set id) → the skeleton already claimed this build, so a second contradictory claim is reported.
                // The slot is lower-cased into the key, because every consumer is case-insensitive.
                build.estClaimed = new Dictionary<(string Slot, int SetId), int>();

                // Game path → the pack file Penumbra resolves it to, for this build only (see ContentMaterialFile).
                mtrlFileCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                // Mod root → game path → every file its manifest puts behind it, read on first need (see ShippedFiles).
                shippedCache = new Dictionary<string, ILookup<string, string>>(StringComparer.OrdinalIgnoreCase);

                // Extra-skeleton claims noted while resolving content, written only for options that reached a host: the entry
                // lands on an item that is not the pack's.
                build.estPending = new List<((string Mod, string? Group, string? Option) Owner, string Slot, int Entry)>();

                unwearable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                // Refusals are held and logged only when the pack got nothing through; a refused piece alongside accepted ones is normal.
                refusals = new List<(string ModDir, string Group, string Option, string Reason)>();

                // A mod's live Penumbra selection, fetched once per mod per composite, for the IMC hide-toggles. Null leaves every
                // toggle at the pack's default. Marshalled onto the framework thread, since it reads Penumbra's collection state.
                selectionCache = build.modSelections = new Dictionary<string, IReadOnlyDictionary<string, List<string>>?>(
                    StringComparer.OrdinalIgnoreCase);

                // A mod's option/default IMC edits, read off its manifest once per mod per build (see ImcEdits), and the
                // game's own IMC masks they fall back to. Nothing is kept across builds, so an edited manifest is seen next time.
                imcEditsCache = new Dictionary<string, ContentImcEdits?>(StringComparer.OrdinalIgnoreCase);
                vanillaMaskCache = new Dictionary<(ImcItem Item, int Variant), ushort?>();
            }

            private void ResolveUnits()
            {
                for (int i = 0; i < build.contentIn; i++)
                {
                    ResolveUnit(i);
                }
            }

            /// <summary>Resolves one content layer into wearable units, recording why a piece cannot be worn.</summary>
            private void ResolveUnit(int i)
            {
                var (cEntry, rc) = build.contentLayers![i];
                var piece = rc.Piece;
                var modRoot = cEntry.ModRoot;
                // Two codes: equipCode is what this character's gear loads at (usually shared c0201/c0101); the character's own
                // race code finds a pack built for one race. Exact matches first, shared shape before own race.
                var ownCode = build.drawnRaceCode ?? build.charCode;
                var variant = ResolveForWearerOrEffect(piece, build.equipCode, ownCode);
                if (modRoot == null || variant is not { } v)
                {
                    // Named by race, not by code, since this message has to explain an enabled pack showing nothing.
                    var reason = string.Format(Strings.Content.NotForYourRaceFmt,
                        ModelRace.DescribeAll(piece.ModelCodes), ModelRace.Describe(ownCode));
                    Unwearable(cEntry.ModDirectory, reason, rc.OptionGroup, rc.Option);
                    return;
                }
                var modelRel = v.Path;

                // Cut space or native (see ContentSurface). Null means the model is authored for neither and would publish at the wrong size.
                var surfaceKey = piece.EffectOnly
                    ? piece.SurfaceKey
                    : ContentSurface(piece.SurfaceKey, v.Code, ownCode, build.bodySurface.CutCode);
                if (surfaceKey is not { } pieceSurface)
                {
                    var reason = string.Format(Strings.Content.NoRaceFitFmt,
                        ModelRace.Describe(v.Code), ModelRace.Describe(ownCode));
                    Unwearable(cEntry.ModDirectory, reason, rc.OptionGroup, rc.Option);
                    return;
                }

                // Read and inspected once per file, however many options name it; the same byte[] also hits the writer's parse cache.
                var modelPath = Path.Combine(modRoot, modelRel);
                if (!modelCache.TryGetValue(modelPath, out var parsedModel))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(modelPath);
                        // The attribute table fails on its own terms: losing it costs the pack's hide toggles, not the piece.
                        List<string> attrs;
                        try { attrs = [.. SecondSkinWriter.AttributeNames(bytes)]; }
                        catch (Exception ex)
                        {
                            attrs = [];
                            build.service.log.Warning("[Proteus] content: {0} — could not read {1}'s attribute table, so its "
                                      + "hide toggles do nothing ({2})", cEntry.ModDirectory, modelRel, ex.Message);
                        }
                        parsedModel = (bytes, UsedMaterialNames(bytes, SecondSkinWriter.MaterialNames(bytes)), attrs);
                    }
                    catch (Exception ex)
                    {
                        build.service.log.Warning("[Proteus] content: {0} \"{1}/{2}\" — {3} could not be read as a model ({4})",
                            cEntry.ModDirectory, rc.OptionGroup ?? "", rc.Option ?? "", modelRel, ex.Message);
                        parsedModel = (null, [], []);
                    }
                    modelCache[modelPath] = parsedModel;
                }
                if (parsedModel.Model == null) return;   // unreadable, and already reported
                var model = parsedModel.Model;

                // Which of the model's materials actually carry geometry; materials on emptied (0-vertex) meshes need no binding.
                var used = parsedModel.Used;
                if (used.Count == 0)
                {
                    build.service.log.Warning("[Proteus] content: {0} \"{1}/{2}\" — {3} has no LOD0 geometry at all, skipping",
                        cEntry.ModDirectory, rc.OptionGroup ?? "", rc.Option ?? "", modelRel);
                    return;
                }

                // The pack's own hide-toggles, applied by DROPPING geometry: these meshes move onto a host accessory, so the
                // game would read the wrong item's IMC mask. From its Imc groups (ContentAttributeGroup) or, failing those,
                // the IMC edits inside its ordinary options and default data (ContentImcEdits).
                // The manifest is only read for a model that has variant parts to toggle.
                var imcEdits = parsedModel.Attrs.Any(SecondSkinWriter.IsVariantAttribute)
                    ? ImcEdits(cEntry.ModDirectory, modRoot) : null;
                var vis = ContentImcVisibility.For(cEntry.Metadata.ContentAttributes, imcEdits,
                    modelRel, parsedModel.Attrs,
                    cEntry.Metadata.ContentAttributes is { Count: > 0 } || imcEdits != null
                        ? ModSelection(cEntry.ModDirectory) : null,
                    VanillaMask);
                var hidden = vis.Hidden;
                if (hidden is { Count: > 0 })
                {
                    build.service.log.Information("[Proteus] content: {0} — {1} hides [{2}] ({3})",
                        cEntry.ModDirectory, modelRel, string.Join(", ", hidden), vis.Why ?? "");
                    // A mod imported before option toggles were honoured arrived with them switched off; say which to tick.
                    if (vis.Source == ImcMaskSource.GameEntry && ContentImcEdits.ItemOf(modelRel) is { } hiddenItem
                        && ImcEdits(cEntry.ModDirectory, modRoot) is { } shown
                        && shown.OptionsThatShow(hiddenItem, hidden) is { Count: > 0 } ticks
                        && ContentImcNotices.FirstGameEntryHint(modRoot))
                        build.service.log.Information("[Proteus] content: {0} — {1}: [{2}] are hidden by the game's own "
                                      + "entry; ticking {3} in Penumbra shows them again",
                            cEntry.ModDirectory, modelRel, string.Join(", ", hidden), string.Join(" or ", ticks));
                }

                // The extra skeleton this piece's "ex" bones live in, NOTED here and written once the piece is known to have
                // reached a host (see ContentSkeleton and EstManipulation). Tagged with this layer's option; default-data records
                // are noted against every layer.
                foreach (var skel in cEntry.Metadata.ContentSkeletons ?? [])
                    if (skel.Group == null
                        || (string.Equals(skel.Group, rc.OptionGroup, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(skel.Option, rc.Option, StringComparison.OrdinalIgnoreCase)))
                        build.estPending.Add(((cEntry.ModDirectory, rc.OptionGroup, rc.Option), skel.Slot, skel.Entry));

                foreach (var leaf in used)
                {
                    // Binding is by NAME and never guessed (see ContentPiece.Materials); a mesh whose material the pack does not ship
                    // is dropped, loudly.
                    var rel = piece.MaterialFor(leaf);
                    if (rel == null)
                    {
                        // A mesh bound to the BODY's own material is dropped quietly: it is the body the outfit was fitted to, and the
                        // character already has one.
                        if (SecondSkinWriter.IsBodySkinMaterial(leaf))
                            build.service.log.Information("[Proteus] content: {0} \"{1}/{2}\" — {3} is the body's own material, "
                                          + "so those meshes are left to the character's own skin",
                                cEntry.ModDirectory, rc.OptionGroup ?? "", rc.Option ?? "", leaf);
                        else
                            build.service.log.Warning("[Proteus] content: {0} \"{1}/{2}\" — mesh material {3} is not bound to any "
                                      + "material this pack ships, so those meshes are dropped. Rebind the mesh to "
                                      + "one of [{4}] and re-export",
                                cEntry.ModDirectory, rc.OptionGroup ?? "", rc.Option ?? "", leaf,
                                string.Join(", ", piece.Materials.Values));
                        continue;
                    }

                    // The file to publish, in order: this pack's own selected option, then Penumbra, then the file the importer
                    // froze. The pack's options come first because Penumbra answers across every installed mod.
                    var mtrlDisk = SelectedMaterialFile(modRoot, piece.SourcesFor(leaf),
                                       ModSelection(cEntry.ModDirectory))
                                ?? build.service.ContentMaterialFile(modRoot, piece.GamePathsFor(leaf), mtrlFileCache)
                                ?? Path.Combine(modRoot, rel);

                    byte[] mtrl;
                    try { mtrl = File.ReadAllBytes(mtrlDisk); }
                    catch (Exception ex)
                    {
                        build.service.log.Warning("[Proteus] content: {0} \"{1}/{2}\" — material {3} could not be read ({4})",
                            cEntry.ModDirectory, rc.OptionGroup ?? "", rc.Option ?? "", mtrlDisk, ex.Message);
                        continue;
                    }

                    // Per-material settings win over the option's (the colour panel writes per material), and a live
                    // override's material entry wins over the mod's own — see ContentSettingLevels.
                    var matSettings = cEntry.Metadata.PeekMaterialSettings(rel);
                    var rowPresets = ContentSettingLevels.RowsFor(
                        rc.MaterialRows != null && rc.MaterialRows.TryGetValue(rel, out var ovrRows) ? ovrRows : null,
                        matSettings, rc.ColorTableRows);
                    var rows = BuildSparseRows(rowPresets);

                    // Only a glow that names an effect counts; a preset with numbers but no scroll map would split a slot for nothing.
                    var glowSource = ContentSettingLevels.GlowFor(
                        rc.MaterialGlow != null && rc.MaterialGlow.TryGetValue(rel, out var ovrGlow) ? ovrGlow : null,
                        matSettings, rc.Glow);
                    var glow = glowSource?.GlowKey() != null ? glowSource : null;

                    // The textures the selection puts behind this material; in the unit key, since sharing a material but not its
                    // textures is two materials to publish.
                    var texFiles = SelectedTextureFiles(modRoot, piece, mtrl, ModSelection(cEntry.ModDirectory),
                        tex => ShippedFiles(modRoot, tex), Plugin.DataManager.FileExists);

                    var key = ContentUnitKey(cEntry.ModDirectory, pieceSurface, rel,
                        rows == null ? null : JsonSerializer.Serialize(rowPresets), glow?.GlowKey(),
                        TextureKey(texFiles));

                    if (!unitByKey.TryGetValue(key, out var unit))
                    {
                        unitByKey[key] = unit =
                            new ContentUnit(mtrl, rel, rows, glow, pieceSurface, [], [], texFiles);
                        build.contentUnits.Add(unit);
                    }

                    // Recorded here, where the material is known to back a drawn mesh, not at the emit loop: a piece that spilled
                    // past the material budget still needs its colour grid. An invisible placeholder has nothing to colour.
                    if (!piece.EffectOnly)
                    {
                        if (!build.contentMaterials.TryGetValue(cEntry.ModDirectory, out var modMats))
                            build.contentMaterials[cEntry.ModDirectory] =
                                modMats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        modMats.Add(rel);
                    }

                    // And the model this mesh came from, which is the only way the Studio tab knows a content piece is worn.
                    if (!build.contentModels.TryGetValue(cEntry.ModDirectory, out var modModels))
                        build.contentModels[cEntry.ModDirectory] =
                            modModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    modModels.Add(modelRel.Replace('\\', '/'));

                    // The same mesh of the same model twice is still drawn once.
                    if (unitGeometry.Add(key + '\u0000' + ContentGeometryKey(modelRel, leaf)))
                        unit.Geometries.Add(new ContentGeometry(model,
                            SecondSkinWriter.KeepByLeaf(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                { leaf.TrimStart('/') }),
                            // characterscroll samples its scroll map with uv1, so a glowing mesh is not copied byte-for-byte. Per geometry,
                            // because the glow belongs to the unit.
                            MirrorUv1: glow != null,
                            // The pack's own hide toggles, baked in — see ContentGeometry.HiddenAttributes.
                            HiddenAttributes: hidden,
                            OwnAttributes: vis.OwnAttributes,
                            DropVariantAttributes: vis.DropVariantAttributes));

                    // Every option this material serves, compared case-insensitively.
                    if (!unit.Owners.Any(o =>
                            string.Equals(o.Content.OptionGroup, rc.OptionGroup, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(o.Content.Option, rc.Option, StringComparison.OrdinalIgnoreCase)))
                        unit.Owners.Add((cEntry, rc));
                }
            }

            private void PublishUnwearable()
            {
                // A mod that got SOMETHING through is not unwearable: the field means "why NONE of its pieces can be worn".
                var wore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var unit in build.contentUnits)
                {
                    if (unit.Geometries.Count == 0) continue;
                    foreach (var (owner, _) in unit.Owners) wore.Add(owner.ModDirectory);
                }
                foreach (var mod in wore) unwearable.Remove(mod);

                // The held refusals, with the same predicate as the panel so log and panel agree.
                foreach (var (modDir, group, option, reason) in refusals)
                {
                    if (wore.Contains(modDir))
                        build.service.log.Debug("[Proteus] content: {0} \"{1}/{2}\" — {3} (other pieces of it are worn)",
                            modDir, group, option, reason);
                    else
                        build.service.log.Warning("[Proteus] content: {0} \"{1}/{2}\" — {3}", modDir, group, option, reason);
                }

                // Published as soon as it is filled: the build can return at the `placed == 0` guard before host allocation.
                build.service.UnwearableContent = unwearable;
            }

            private void Unwearable(string modDir, string reason, string? group, string? option)
            {
                if (!unwearable.ContainsKey(modDir)) unwearable[modDir] = reason;
                refusals.Add((modDir, group ?? "", option ?? "", reason));
            }

            /// <summary>
            /// Every file the pack's manifest puts behind <paramref name="gamePath"/>, in declaration order. The manifest is
            /// read once per mod per build, and only when a texture has no ticked supplier.
            /// </summary>
            private IReadOnlyList<string> ShippedFiles(string modRoot, string gamePath)
            {
                if (!shippedCache.TryGetValue(modRoot, out var shipped))
                    shippedCache[modRoot] = shipped = PenumbraModMeta.ReadAllRedirects(modRoot)
                        .ToLookup(r => PenumbraPackage.Normalize(r.GamePath), r => r.File,
                                  StringComparer.OrdinalIgnoreCase);
                return [.. shipped[PenumbraPackage.Normalize(gamePath)]];
            }

            /// <summary>
            /// The IMC part toggles <paramref name="modRoot"/>'s ordinary options and default data carry (see
            /// <see cref="ContentImcEdits"/>), read once per mod per build. An unreadable manifest is warned about once a
            /// session and leaves the mod as it was before these were honoured.
            /// </summary>
            private ContentImcEdits? ImcEdits(string modDir, string modRoot)
            {
                if (imcEditsCache.TryGetValue(modRoot, out var known)) return known;
                long started = PhaseCounter.Begin();
                var (edits, error) = ContentImcEdits.ReadInstalled(modRoot);
                if (error != null)
                {
                    // The file's own fault reads as one line; anything else keeps its stack, since it is a bug here.
                    bool badFile = error is System.IO.IOException or System.IO.InvalidDataException
                        or System.Text.Json.JsonException or UnauthorizedAccessException;
                    if (ContentImcNotices.FirstReadFailure(modRoot))
                    {
                        if (badFile)
                            build.service.log.Warning("[Proteus] content: {0} — its manifest could not be read ({1}), so "
                                        + "its option part toggles are not applied", modDir, error.Message);
                        else
                            build.service.log.Warning(error, "[Proteus] content: {0} — reading its option part toggles "
                                        + "failed, so they are not applied", modDir);
                    }
                    else
                        build.service.log.Debug("[Proteus] content: {0} — option part toggles still unreadable ({1})",
                            modDir, error.Message);
                }
                else
                    build.service.log.Debug("[Proteus] content: {0} — option part toggles read in {1:F1} ms ({2})",
                        modDir, PhaseCounter.MsSince(started), edits == null ? "none" : "some");
                return imcEditsCache[modRoot] = edits;
            }

            /// <summary>The game's own IMC attribute mask for one variant of an item, or null when it cannot be read.</summary>
            private ushort? VanillaMask(ImcItem item, int variant)
            {
                if (vanillaMaskCache.TryGetValue((item, variant), out var known)) return known;
                ushort? mask = null;
                try
                {
                    mask = ImcEntrySource.FromGame(p => Plugin.DataManager.GetFile(p)?.Data, item.SetTag, item.SlotLabel,
                                                   variant)?.AttributeMask;
                }
                catch (Exception ex)
                {
                    build.service.log.Debug("[Proteus] content: the game's IMC entry for {0} {1} could not be read ({2})",
                        item.SetTag, item.SlotLabel, ex.Message);
                }
                return vanillaMaskCache[(item, variant)] = mask;
            }

            private IReadOnlyDictionary<string, List<string>>? ModSelection(string modDir)
            {
                if (selectionCache.TryGetValue(modDir, out var known)) return known;
                IReadOnlyDictionary<string, List<string>>? found = null;
                try
                {
                    found = Plugin.Framework.RunOnFrameworkThread(() =>
                            build.service.penumbra.GetPlayerCollectionId() is { } id
                                ? build.service.penumbra.GetModSettings(id, modDir)?.Options
                                : null)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    build.service.log.Warning("[Proteus] content: could not read {0}'s Penumbra settings ({1}) — its hide "
                              + "toggles fall back to the pack's defaults", modDir, ex.Message);
                }
                return selectionCache[modDir] = found;
            }
        }
    }
}
