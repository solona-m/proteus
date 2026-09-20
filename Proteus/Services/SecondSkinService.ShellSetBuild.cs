using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CheapLoc;
using Dalamud.Game.Text.SeStringHandling;
using Proteus.Interop;

namespace Proteus.Services;
using static Proteus.Services.ContentPieceResolver;
using static Proteus.Services.PenumbraManipulations;

public sealed partial class SecondSkinService
{
    private sealed partial class ShellSetBuild
    {
        private readonly SecondSkinService service;
        private readonly string charCode;
        private readonly IReadOnlyList<(OverlayEntry Entry, ResolvedOverlay Overlay)> gearOverlays;
        private readonly string outputRoot;
        private string? bodyType;
        private readonly string? effectsFolder;
        private readonly IReadOnlyDictionary<string, string>? equippedPartModels;
        private readonly IReadOnlyDictionary<string, string>? equippedAccessories;
        private readonly Func<string, bool>? gen2Allowed;
        private readonly int? invisibleGlassesSet;
        private readonly IReadOnlyList<string>? metModels;
        private readonly IReadOnlyDictionary<string, HashSet<string>>? enabledBodyShapes;
        private readonly IReadOnlySet<string>? maskShellMods;
        private readonly IReadOnlyDictionary<string, string>? bareBodyModels;
        private readonly string? drawnRaceCode;
        private readonly IReadOnlySet<string>? activeMaterials;
        private readonly int? emperorRingVariant;
        private readonly int? invisibleGlassesVariant;
        private readonly IReadOnlyList<string>? humanPartModels;
        private readonly IReadOnlyList<(OverlayEntry Entry, ResolvedContent Content)>? contentLayers;
        private readonly IReadOnlyList<OverlayEntry>? allEntries;
        private readonly IReadOnlyDictionary<string, byte[]>? pristineHumanModels;
        private readonly int? shellTexSize;
        private readonly IReadOnlyList<EquippedSlotVariants.Slot>? equippedSlotVariants;
        private int contentIn;
        private int texSize;
        private Dictionary<(int, string, string), string> variantMemo = null!;
        private Dictionary<string, string> redirects = null!;
        private List<object> manipulations = null!;
        private Dictionary<(string, string?, string?), List<string>> shellMaterials = null!;
        private Dictionary<string, ShellLightProfile> shellLight = null!;
        private Dictionary<string, HashSet<string>> contentMaterials = null!;
        private Dictionary<string, HashSet<string>> contentModels = null!;
        private string modelsDir = null!;
        private string materialsDir = null!;
        private string texturesDir = null!;
        private bool anyGen2Allowed;
        private HashSet<string?> wornBodyTypes = null!;
        private bool wearsOnlyGen2;
        private string equipCode = null!;
        private List<(byte[] Bytes, HashSet<string>? Shapes, string Path, string? Uv)> bodies = null!;
        private HashSet<string> bodySettled = null!;
        private HashSet<string> smoothable = null!;
        private string? modelType;
        private int barePartsTried;
        private int barePartsMissing;
        private bool[] unmirrorPart = null!;
        private bool unmirror;
        private List<UVRemapService.UvConversion?> uvConverters = null!;
        private string cutCode = null!;
        private ResolvedSurface bodySurface = null!;
        private List<ResolvedSurface> surfaces = null!;
        private int[] layerSurface = null!;
        private string[] layerSurfaceName = null!;
        private Dictionary<ShellSurfaceKey, int> resolvedByKey = null!;
        private HashSet<int> droppedLayers = null!;
        private List<ContentUnit> contentUnits = null!;
        private Dictionary<(string Slot, int SetId), int> estClaimed = null!;
        private List<((string Mod, string? Group, string? Option) Owner, string Slot, int Entry)> estPending = null!;
        private int[] unitSurface = null!;
        private List<HostAccessory> hosts = null!;
        private List<(HostAccessory Host, string By)> claimedCarriers = null!;
        private int[] hostSurface = null!;
        private int totalCapacity;
        private bool shellChanged;
        private List<(string Material, int Density, DeferredShellNormal Normal)> pendingNormals = null!;
        private Dictionary<string, (byte[] Mask, int Size)> toeReinforceMaps = null!;
        private List<SecondSkinLayer>[] perHostLayers = null!;
        private int diskLetter;
        private int maskLayers;
        private int clothLayers;
        private int overBudget;
        private int overBudgetMask;
        private List<int> unhostedLayers = null!;
        private int[] remaining = null!;
        private int?[] hostClaim = null!;
        private int diskBudget;
        private List<(int LayerIdx, int HostIdx)> work = null!;
        private List<int> unresolvedLayers = null!;
        private List<(int Unit, int HostIdx)> contentWork = null!;
        private List<int> contentUnhosted = null!;
        private (string HostRace, bool Native, string PublishCode)[] plan = null!;
        private byte[]?[] alphaByLayer = null!;
        private List<(string ModDir, int LayerIdx, byte[] Normal)> reliefContribs = null!;
        private byte[]? sharedToeCap;
        private Dictionary<string, float> bridgeByMod = null!;
        private Dictionary<string, float> smoothByMod = null!;
        private Dictionary<string, float> cleftByMod = null!;
        private Dictionary<string, float> foldByMod = null!;
        private Dictionary<int, string> topSpanningModOnHost = null!;
        private int[] inHost = null!;
        private int contentPlaced;
        private int placed;
        private bool modelChangedAny;
        private List<string> hostModelPaths = null!;
        private List<string> appendHostModelPaths = null!;
        private Result? result;

        public ShellSetBuild(SecondSkinService service, string charCode, IReadOnlyList<(OverlayEntry Entry, ResolvedOverlay Overlay)> gearOverlays, string outputRoot, string? bodyType, string? effectsFolder, IReadOnlyDictionary<string, string>? equippedPartModels, IReadOnlyDictionary<string, string>? equippedAccessories, Func<string, bool>? gen2Allowed, int? invisibleGlassesSet, IReadOnlyList<string>? metModels, IReadOnlyDictionary<string, HashSet<string>>? enabledBodyShapes, IReadOnlySet<string>? maskShellMods, IReadOnlyDictionary<string, string>? bareBodyModels, string? drawnRaceCode, IReadOnlySet<string>? activeMaterials, int? emperorRingVariant, int? invisibleGlassesVariant, IReadOnlyList<string>? humanPartModels, IReadOnlyList<(OverlayEntry Entry, ResolvedContent Content)>? contentLayers, IReadOnlyList<OverlayEntry>? allEntries, IReadOnlyDictionary<string, byte[]>? pristineHumanModels, int? shellTexSize, IReadOnlyList<EquippedSlotVariants.Slot>? equippedSlotVariants)
        {
            this.service = service;
            this.charCode = charCode;
            this.gearOverlays = gearOverlays;
            this.outputRoot = outputRoot;
            this.bodyType = bodyType;
            this.effectsFolder = effectsFolder;
            this.equippedPartModels = equippedPartModels;
            this.equippedAccessories = equippedAccessories;
            this.gen2Allowed = gen2Allowed;
            this.invisibleGlassesSet = invisibleGlassesSet;
            this.metModels = metModels;
            this.enabledBodyShapes = enabledBodyShapes;
            this.maskShellMods = maskShellMods;
            this.bareBodyModels = bareBodyModels;
            this.drawnRaceCode = drawnRaceCode;
            this.activeMaterials = activeMaterials;
            this.emperorRingVariant = emperorRingVariant;
            this.invisibleGlassesVariant = invisibleGlassesVariant;
            this.humanPartModels = humanPartModels;
            this.contentLayers = contentLayers;
            this.allEntries = allEntries;
            this.pristineHumanModels = pristineHumanModels;
            this.shellTexSize = shellTexSize;
            this.equippedSlotVariants = equippedSlotVariants;
        }

        public Result? Run()
        {
            if (!Begin()) return result;
            ResolveHostVariants();
            CreateOutputs();
            VoteModelCode();
            HarvestParts();
            if (!WholeBodyFallback()) return result;
            ChooseUvSpace();
            UnmirrorBody();
            ConvertUvSpace();
            VoteCutCode();
            ResolveSurfaces();
            AssignLayerSurfaces();
            ResolveContent();
            ChooseHosts();
            PlanPublish();
            DistributeLayers();
            PlaceContent();
            PlanHosts();
            PlanSiblingRelief();
            BakeShellTextures();
            PlaceContentMeshes();
            if (!PublishSkeletons()) return result;
            ReportUnhosted();
            SmoothBody();
            BuildHostModels();
            return Finish();
        }

        private bool Begin()
        {
            service.ResetBuildStats();
            contentIn = contentLayers?.Count ?? 0;

            // Per-build, not per-session: only repetition within one build is worth caching.
            service.remapCache.Clear();

            // The sheet size for THIS build, chosen before any art is decoded (every load is keyed on it). Per build, never a field of the service.
            int largestArt = 0;
            texSize = shellTexSize ?? ChooseTexSize(ShellArtPaths(gearOverlays, service.discovery), out largestArt);
            // Log anything above the floor; the ordinary floor case stays quiet.
            if (texSize != TexSizeFloor)
                service.log.Information("[Proteus] second skin: shell textures at {0} (largest art {1}, floor {2}, cap {3})",
                    $"{texSize}x{texSize}",
                    largestArt == 0 ? "as given by the caller" : $"{largestArt}px",
                    TexSizeFloor, TexSizeCap);

            // Cleared first, so the field means "as of this build" even when a path below returns early.
            service.UnwearableContent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (gearOverlays.Count == 0 && contentIn == 0) { result = null; return false; }
            return true;
        }

        private void ResolveHostVariants()
        {
            // Which "v####" folder the game will ask for a host's materials under. The shell's material name is
            // variant-relative, so the game uses the EQUIPPED ITEM's variant. Sources in order: the live resource tree
            // (the slot's own loaded material, IMC meta edits included), then the item's variant through its IMC entry.
            // When both answer and disagree the tree wins, and the disagreement is logged.
            variantMemo = new Dictionary<(int, string, string), string>();
        }

        private void CreateOutputs()
        {
            redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            manipulations = new List<object>();
            // Gear overlay identity → its shell material disk names (ss_{letter}.mtrl), for the colorset editor's glow button.
            // A key can hold several: one mod/option's gear overlays all bake the same colour table.
            shellMaterials = new Dictionary<(string, string?, string?), List<string>>();
            // Shell material leaf → its light response, for the runtime applier (only materials that ask; see ShellLightProfile.Any).
            shellLight = new Dictionary<string, ShellLightProfile>(StringComparer.OrdinalIgnoreCase);
            // Per mod, the content materials backing a drawn mesh (see Result.ContentMaterials).
            contentMaterials = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            // Per mod, the model files those meshes were cut from — see Result.ContentModels.
            contentModels = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            modelsDir = Path.Combine(outputRoot, "models");
            materialsDir = Path.Combine(outputRoot, "materials");
            texturesDir = Path.Combine(outputRoot, "textures");
            Directory.CreateDirectory(modelsDir);
            Directory.CreateDirectory(materialsDir);
            Directory.CreateDirectory(texturesDir);
        }

        private void VoteModelCode()
        {
            // ── every skin part the character is drawing, MERGED into the one ring model ──
            // The shell is a COPY of the body geometry, so it must be cut from the models the character is drawing, resolved
            // live. gen2 (vanilla) follows each gear mod's "Overlay gen2/vanilla" checkbox, gated per PART. Content packs
            // are always allowed: they paint nothing onto the body.
            anyGen2Allowed = gen2Allowed == null || contentIn > 0
                               || gearOverlays.Any(g => gen2Allowed(g.Entry.ModDirectory));

            // Whether the character draws vanilla skin and NO other body type; the whole-body fallback needs it because it
            // reads vanilla bytes even for a modded body. Unknown is not "only vanilla".
            wornBodyTypes = (activeMaterials ?? (IEnumerable<string>)Array.Empty<string>())
                .Select(UVRemapService.InferBodyType).Where(t => t != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            wearsOnlyGen2 = wornBodyTypes.Count == 1 && wornBodyTypes.Contains("gen2");

            // Equipment is keyed to a MODEL race, not the character's race (Viera and Hrothgar wear c0201 models), so the
            // shell, cut from equipment and hosted on accessories, uses the model code; charCode stays for the body itself.
            var equippedPaths = (equippedPartModels?.Values ?? Enumerable.Empty<string>())
                .Concat(equippedAccessories?.Values ?? Enumerable.Empty<string>())
                .Concat(metModels ?? Enumerable.Empty<string>())
                // NEVER count the Emperor's ring: it is our host and resolves to our own output, so reading its race code is a
                // feedback loop.
                .Where(pth => !pth.Contains($"a{EmperorSetId:D4}", StringComparison.OrdinalIgnoreCase));

            // Resolve the winner, breaking an exact tie with evidence the main vote didn't see (a 1-1 split across two
            // accessories is ordinary, since many vanilla accessories ship at c0101 only).
            string PickCode(List<IGrouping<string, string>> votes, bool votedOnBare)
            {
                int best = votes[0].Count();
                var tied = votes.Where(g => g.Count() == best).Select(g => g.Key).ToList();
                if (tied.Count == 1) return tied[0];

                // Runoff 1: the bare-body parts the game is drawing, held out of the main vote because uncovered slots outnumber
                // worn items. Skipped when the main vote already was the bare parts.
                if (!votedOnBare)
                {
                    var bare = CodeVotes(bareBodyModels?.Values ?? Enumerable.Empty<string>())
                        .Where(g => tied.Contains(g.Key, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (bare.Count == 1 || (bare.Count > 1 && bare[0].Count() > bare[1].Count()))
                        return bare[0].Key;
                }

                // Runoff 2: the character's own body code; a shell in the space she already draws in needs no deformation.
                if (tied.Contains(charCode, StringComparer.OrdinalIgnoreCase)) return charCode;

                // Runoff 3: on a male/female coin flip, take the female. c0201 is where nearly every body mod is authored, and
                // being wrong toward c0101 shrinks the shell.
                var female = tied.FirstOrDefault(c => RaceIndex(c) is { } n && n % 2 == 0);
                if (female != null) return female;

                return tied[0];   // deterministic last resort — CodeVotes already ordered these by key
            }

            var codeVotes = CodeVotes(equippedPaths);
            var voteSource = "equipped";
            var votedOnBare = false;

            // Wearing nothing: the e0000 parts the game is drawing are the only evidence left. Counted ONLY when nothing is
            // equipped: uncovered slots outnumber worn items and would outvote real gear.
            if (codeVotes.Count == 0)
            {
                codeVotes = CodeVotes(bareBodyModels?.Values ?? Enumerable.Empty<string>());
                voteSource = "drawn bare-body";
                votedOnBare = true;
            }

            string? modelCode = null;
            if (codeVotes.Count > 0)
            {
                modelCode = PickCode(codeVotes, votedOnBare);
                if (codeVotes.Count > 1)
                    service.log.Warning("[Proteus] second skin: {0} models disagree on a model code [{1}] — using c{2}",
                        voteSource, string.Join(", ", codeVotes.Select(g => $"{g.Key}x{g.Count()}")), modelCode);
            }
            if (modelCode == null)
            {
                // Nothing equipped: probe the character's own code, then the two base codes, and take the first that has an
                // e0000 torso.
                foreach (var cand in new[] { charCode, "0201", "0101" })
                {
                    // Existence only, without reading the model. ResolvePlayer echoes the game path when nothing redirects it, so a
                    // redirect only counts when it resolves to a different path that is a real file.
                    var probe = $"chara/equipment/e0000/model/c{cand}e0000_top.mdl";
                    var probeDisk = service.penumbra.ResolvePlayer(probe);
                    bool modded = probeDisk != null
                               && !string.Equals(probeDisk, probe, StringComparison.OrdinalIgnoreCase)
                               && File.Exists(probeDisk);
                    if (!modded && !Plugin.DataManager.FileExists(probe)) continue;
                    modelCode = cand;
                    break;
                }
                modelCode ??= charCode;
            }

            // Non-null from here, pinned into its own local because flow analysis does not carry across Build.
            equipCode = modelCode;

            if (!string.Equals(modelCode, charCode, StringComparison.OrdinalIgnoreCase))
                service.log.Information("[Proteus] second skin: c{0} wears c{1} equipment models (race-deformed) — any "
                              + "bare slot the live walk missed is rebuilt in c{1}", charCode, modelCode);
        }

        private void HarvestParts()
        {
            // Each kept part carries its bytes, the shape keys enabled on that body model, and the game path it was cut from
            // (whose race code decides how the game deforms it; see cutCode).
            bodies = new List<(byte[] Bytes, HashSet<string>? Shapes, string Path, string? Uv)>();

            // Body paths whose bytes are already our own published, smoothed body. The shell is cut from them normally;
            // the smoothing pass must hold what it has rather than run again.
            bodySettled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Body paths smoothing may republish: the bare body, whatever body mod supplies it, and garments from Proteus mods.
            smoothable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            modelType = null;   // UV space of the first kept part, from its own skin material
            // Bare-body slots attempted vs. missing; the whole-body fallback fires only when every one was missing.
            barePartsTried = 0;
            barePartsMissing = 0;
            foreach (var part in Parts)
            {
                // With gear equipped in a slot, the gear model is drawn instead of the bare-body part and carries the skin it
                // exposes, posed, so the shell is cut from it (SecondSkinWriter keeps only the skin mesh). Slots without gear
                // use the e0000 model the game is drawing, rebuilt from the model code only when the live set lacks it.
                var bareBody = bareBodyModels != null && bareBodyModels.TryGetValue(part, out var drawnBare)
                    ? drawnBare
                    : $"chara/equipment/e0000/model/c{equipCode}e0000_{part}.mdl";
                var bodyGamePath = equippedPartModels != null && equippedPartModels.TryGetValue(part, out var eq)
                    ? eq
                    : bareBody;
                bool isBarePart = string.Equals(bodyGamePath, bareBody, StringComparison.Ordinal);
                if (isBarePart) barePartsTried++;

                // ResolvePlayer only yields a real file for MODDED models; a vanilla piece resolves to the game path, so read
                // game data then. A PLAIN RESOLVE, deliberately, not resolveUpstream: a body path is contested by several body
                // mods and the resolver can remember a transient answer mid-rebuild.
                var bodyDisk = service.penumbra.ResolvePlayer(bodyGamePath);
                // The body may be one of our own publications (the nipple smooth republishes it). Cutting from it or smoothing it
                // again would compound, so the upstream is remembered and used; vanilla game data is not a fallback.
                bool bodyIsOurs = bodyDisk != null && IsInsideOutputRoot(bodyDisk, outputRoot);

                // Smoothing our own output again must never happen: it compounds every composite. The part is still READ,
                // because this list is also what the shell is cut from; bodySettled marks it so the smoothing pass holds instead.
                // The upstream body is kept on disk, written whenever the path is unmasked and read back when it is masked, since
                // nothing unmasks a body path afterwards. It lives in a subfolder because PruneManagedOutput does not recurse.
                var upstreamDir = Path.Combine(modelsDir, "upstream");
                var upstreamDisk = Path.Combine(upstreamDir, CompositorService.SanitizeName(bodyGamePath) + ".mdl");

                // A SETTLED upstream outranks both remembered copies: those freeze the body at whatever it was before the first
                // publish, while the prime reflects the body the user has selected now.
                var settledDisk = bodyIsOurs ? service.settledUpstream?.Invoke(bodyGamePath) : null;
                var settledBytes = settledDisk != null ? service.textureLoader.LoadRawFile(settledDisk, bodyGamePath) : null;

                byte[]? bytes;
                // The mod file this part really comes from, when known; null for game data or when only a copy of the bytes survives.
                string? sourceDisk = null;
                if (settledBytes != null)
                {
                    bytes = settledBytes;
                    sourceDisk = settledDisk;
                    bool changed = !service._upstreamBodies.TryGetValue(bodyGamePath, out var had)
                                || !had.AsSpan().SequenceEqual(settledBytes);
                    service._upstreamBodies[bodyGamePath] = settledBytes;
                    service._upstreamBodyDisks[bodyGamePath] = settledDisk!;
                    try
                    {
                        Directory.CreateDirectory(upstreamDir);
                        WriteIfChanged(upstreamDisk, settledBytes);
                    }
                    catch (Exception ex)
                    { service.log.Warning(ex, "[Proteus] second skin: could not keep the upstream {0}", bodyGamePath); }
                    if (changed)
                        service.log.Information("[Proteus] second skin: {0} is behind our own output — using the body the "
                                      + "collection now provides, {1}", bodyGamePath, settledDisk!);
                }
                else if (bodyIsOurs && service._upstreamBodies.TryGetValue(bodyGamePath, out var remembered))
                {
                    bytes = remembered;
                    sourceDisk = service._upstreamBodyDisks.GetValueOrDefault(bodyGamePath);
                }
                else if (bodyIsOurs && File.Exists(upstreamDisk))
                {
                    bytes = File.ReadAllBytes(upstreamDisk);
                    service._upstreamBodies[bodyGamePath] = bytes;
                    service.log.Debug("[Proteus] second skin: {0} resolves to our own output — smoothing the upstream "
                            + "kept at {1}", bodyGamePath, upstreamDisk);
                }
                else if (bodyIsOurs)
                {
                    // Nothing remembered anywhere: cut the shell from what we published, but do not run the passes over it again.
                    // Never drop the part.
                    bytes = service.textureLoader.LoadRawFile(bodyDisk, bodyGamePath);
                    bodySettled.Add(bodyGamePath);
                    service.log.Debug("[Proteus] second skin: {0} still resolves to our own output ({1}) and no "
                            + "upstream is remembered — cutting the shell from it, and holding the body we "
                            + "already published rather than smoothing it again", bodyGamePath, bodyDisk ?? "(null)");
                }
                else
                {
                    bytes = service.textureLoader.LoadRawFile(bodyDisk, bodyGamePath);
                    sourceDisk = bodyDisk;
                    if (bytes != null)
                    {
                        service._upstreamBodies[bodyGamePath] = bytes;
                        if (bodyDisk != null) service._upstreamBodyDisks[bodyGamePath] = bodyDisk;
                        else service._upstreamBodyDisks.TryRemove(bodyGamePath, out _);
                        try
                        {
                            Directory.CreateDirectory(upstreamDir);
                            WriteIfChanged(upstreamDisk, bytes);
                        }
                        catch (Exception ex)
                        { service.log.Warning(ex, "[Proteus] second skin: could not keep the upstream {0}", bodyGamePath); }
                    }
                }

                if (bytes == null)
                {
                    // Only BARE-BODY misses count toward the fallback; a missing equipped model just skips the slot.
                    if (isBarePart) barePartsMissing++;
                    // Information, not Debug: this is usually why a shell fails to build.
                    service.log.Information("[Proteus] second skin: {0} not loadable, skipping part {1}", bodyGamePath, part);
                    continue;
                }

                // The part's UV space, from its own skin material suffix. A vanilla (gen2) part gets no shell when every gear
                // mod has "Overlay gen2/vanilla" unticked.
                var partType = SkinBodyType(bytes);
                if (string.Equals(partType, "gen2", StringComparison.OrdinalIgnoreCase) && !anyGen2Allowed)
                {
                    service.log.Information("[Proteus] second skin: {0} is vanilla (gen2) — every gear mod has Overlay gen2/vanilla unticked, skipping part", bodyGamePath);
                    continue;
                }
                // Each part's UV space, path and size is logged, plus a shape fingerprint of the skin geometry: it tells a wrong
                // body variant (numbers differ from a good run) from a race-deformation fault (numbers match).
                var shape = "(no skin geometry)";
                if (SecondSkinWriter.TryReadLod0Geometry(bytes, out var dbgPos, out _, out var dbgTri)
                    && dbgPos.Length >= 3)
                {
                    float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue;
                    float x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
                    for (int v = 0; v + 2 < dbgPos.Length; v += 3)
                    {
                        if (dbgPos[v]     < x0) x0 = dbgPos[v];
                        if (dbgPos[v]     > x1) x1 = dbgPos[v];
                        if (dbgPos[v + 1] < y0) y0 = dbgPos[v + 1];
                        if (dbgPos[v + 1] > y1) y1 = dbgPos[v + 1];
                        if (dbgPos[v + 2] < z0) z0 = dbgPos[v + 2];
                        if (dbgPos[v + 2] > z1) z1 = dbgPos[v + 2];
                    }
                    shape = $"{dbgPos.Length / 3}v/{dbgTri.Length / 3}t bounds=[{x0:F3}..{x1:F3}, "
                          + $"{y0:F3}..{y1:F3}, {z0:F3}..{z1:F3}]";
                }

                service.log.Information("[Proteus] second skin part {0}: uv={1} {2} ({3} KB) skin={4} <- {5}",
                    part, partType ?? "(unknown)", bodyGamePath, bytes.Length / 1024, shape,
                    bodyDisk ?? "(game data)");

                // Shape keys enabled on this exact body model (matched by file stem — see LiveModelState).
                var partShapes = LiveModelState(enabledBodyShapes, bodyGamePath, bodyDisk);

                bodies.Add((bytes, partShapes, bodyGamePath, partType));
                if (isBarePart || IsProteusModFile(sourceDisk, outputRoot)) smoothable.Add(bodyGamePath);
            }
        }

        private bool WholeBodyFallback()
        {
            // ── whole-body fallback ──────────────────────────────────────────────
            // Last resort for races that ship no e0000 parts, when the live bareBodyModels had nothing either. It reads
            // VANILLA bytes even for a modded body, so the gen2 gate below drops it unless the character draws only vanilla
            // skin; don't loosen that gate. It REPLACES every part cut above rather than stacking over them.
            // Trigger: EVERY bare-body slot attempted was missing, not any one.
            if (barePartsTried > 0 && barePartsMissing == barePartsTried)
            {
                // b0001 is standard but some race/gender combos ship b0101; prefer the one the player's mod owns, else the first
                // that exists. The file name carries the customization suffix (c1401b0001_top.mdl).
                (byte[] Bytes, string Path, string? Disk)? pick = null;
                foreach (var bodyId in WholeBodyIds)
                {
                    var wholePath = $"chara/human/c{charCode}/obj/body/{bodyId}/model/c{charCode}{bodyId}_top.mdl";
                    // ResolvePlayer echoes the game path when nothing redirects it; only a different path that is a real file is a mod.
                    var resolved = service.penumbra.ResolvePlayer(wholePath);
                    var wholeDisk = resolved != null
                                 && !string.Equals(resolved, wholePath, StringComparison.OrdinalIgnoreCase)
                                 && File.Exists(resolved) ? resolved : null;
                    var wholeBytes = service.textureLoader.LoadRawFile(wholeDisk, wholePath);
                    if (wholeBytes == null) continue;
                    if (wholeDisk != null) { pick = (wholeBytes, wholePath, wholeDisk); break; }
                    pick ??= (wholeBytes, wholePath, wholeDisk);
                }

                if (pick is { } whole)
                {
                    var wholeType = SkinBodyType(whole.Bytes);
                    if (string.Equals(wholeType, "gen2", StringComparison.OrdinalIgnoreCase)
                        && !(anyGen2Allowed && wearsOnlyGen2))
                    {
                        // Warning, not Information: the normal outcome for a modded body, and it means the shell ships short.
                        service.log.Warning("[Proteus] second skin: whole-body fallback {0} is vanilla (gen2) but the "
                                  + "character draws [{1}]{2}, leaving the {3} part(s) cut above as-is. The live "
                                  + "bare-body models were unavailable this composite; a redraw usually fixes it",
                                  whole.Path,
                                  wornBodyTypes.Count == 0 ? "unknown" : string.Join("+", wornBodyTypes.OrderBy(t => t, StringComparer.Ordinal)),
                                  anyGen2Allowed ? "" : " and every gear mod has Overlay gen2/vanilla unticked",
                                  bodies.Count);
                    }
                    else
                    {
                        // enabledBodyShapes is keyed by the stem of the model the game draws, which a borrowed-race whole body never
                        // matches, so fall back to the union of every enabled set (an undeclared shape key is a no-op).
                        HashSet<string>? wholeShapes = null;
                        if (enabledBodyShapes != null
                            && (wholeShapes = LiveModelState(enabledBodyShapes, whole.Path, whole.Disk)) == null)
                        {
                            wholeShapes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var set in enabledBodyShapes.Values) wholeShapes.UnionWith(set);
                            if (wholeShapes.Count == 0) wholeShapes = null;
                        }
                        service.log.Information("[Proteus] second skin: a bare-body e0000 part was not loadable (usual cause: "
                                      + "c{0} ships no e0000 models and the game resolves them through EQDP) — cutting "
                                      + "the whole shell from {1} instead, replacing {2} part(s) cut above",
                                      charCode, whole.Path, bodies.Count);
                        bodies.Clear();
                        bodies.Add((whole.Bytes, wholeShapes, whole.Path, wholeType));
                        smoothable.Add(whole.Path);   // the body itself, not a garment
                        modelType = wholeType;
                    }
                }
                else
                {
                    // Say why the shell is short of geometry.
                    service.log.Information("[Proteus] second skin: every bare-body e0000 part was missing and no whole-body "
                                  + "model loaded for c{0} either (tried {1}) — the shell keeps only the {2} part(s) "
                                  + "cut from equipped gear", charCode, string.Join(", ", WholeBodyIds), bodies.Count);
                }
            }

            if (bodies.Count == 0)
            {
                service.log.Warning("[Proteus] second skin: no skin models resolved for c{0} (or all parts gated out)", charCode);
                { result = null; return false; }
            }
            return true;
        }

        private void ChooseUvSpace()
        {
            // ── the shell's UV space, chosen from the parts actually kept ─────────────────────────
            // Not simply the first part's: an asymmetric space wins over gen2 whenever both are present, because gen2 -> bibo
            // places every vertex while bibo -> gen2 has nowhere for bibo's left half. Among asymmetric spaces the first wins.
            modelType = bodies.Select(b => b.Uv).FirstOrDefault(u => u != null && !IsGen2(u))
                     ?? bodies.Select(b => b.Uv).FirstOrDefault(u => u != null);

            if (modelType != null && !string.Equals(modelType, bodyType, StringComparison.OrdinalIgnoreCase))
            {
                service.log.Information("[Proteus] second skin: body UV is {0} per the model's material (was {1})",
                    modelType, bodyType ?? "unknown");
                bodyType = modelType;
            }
        }

        private void UnmirrorBody()
        {
            // ── un-mirroring a vanilla body ───────────────────────────────────────────────────────
            // gen2 is MIRRORED, so asymmetric art cannot be ported down into it; instead the vanilla geometry's two sides are
            // sent to the two halves of the art's own sheet (UvConverter's unmirror). Only for art measured asymmetric
            // (OverlayDescriptor.AsymmetricArt), and keyed on whether the shell holds ANY gen2 part, not on its bodyType.
            string? unmirrorInto = null;
            if (bodies.Any(b => IsGen2(b.Uv)))
            {
                foreach (var (_, ov) in gearOverlays)
                {
                    var d = ov.Descriptor;
                    if (d.AsymmetricArt != true) continue;
                    // Body surface only here; a face's doubled sheet is un-mirrored by ResolveHumanSurface against its own geometry.
                    if (!d.IsMaskShell && d.MaterialGamePaths.Count > 0
                        && !ShellSurface.KeysFor(d.MaterialGamePaths).Any(k => k.IsBody)) continue;
                    var src = d.SourceBodyType ?? InferOverlayBodyType(d);
                    if (src == null || UVRemapService.DoubledSpaceOf(src) != null) continue;
                    // A face sheet is not a body space; it can never be what a body shell targets.
                    if (string.Equals(src, UVRemapService.FaceSplitSpace, StringComparison.OrdinalIgnoreCase)) continue;
                    unmirrorInto = src;
                    break;
                }
            }

            // Ask the geometry, per part, not the body-type table: un-mirroring an already un-mirrored body would tear it.
            // Each part's LOD0 skin is decoded at most once, and only when asked.
            var partGeom = new (float[] Pos, float[] Uv)?[bodies.Count];
            var geomRead = new bool[bodies.Count];
            (float[] Pos, float[] Uv)? Geometry(int i)
            {
                if (!geomRead[i])
                {
                    geomRead[i] = true;
                    if (SecondSkinWriter.TryReadLod0Geometry(bodies[i].Bytes, out var gp, out var gu, out _))
                        partGeom[i] = (gp, gu);
                }
                return partGeom[i];
            }

            unmirrorPart = new bool[bodies.Count];
            if (unmirrorInto != null)
            {
                int gen2Parts = 0, notMirrored = 0, unreadable = 0, straddling = 0;
                for (int i = 0; i < bodies.Count; i++)
                {
                    if (!IsGen2(bodies[i].Uv)) continue;
                    gen2Parts++;
                    if (Geometry(i) is not { } g) { unreadable++; continue; }
                    var (mp, mu) = g;
                    if (!SurfaceMirror.LooksMirrored(mp, mu)) { notMirrored++; continue; }

                    // A mesh whose UV strays outside one integer cell is REPORTED, not refused: the strays break any conversion
                    // equally, and refusing would abandon every placeable vertex. Counted against the part's most common integer
                    // cell, since the writer shifts each mesh by its own minimum u.
                    var cellCounts = new Dictionary<int, int>();
                    float uLo = float.MaxValue, uHi = float.MinValue;
                    for (int k = 0; k < mu.Length; k += 2)
                    {
                        if (mu[k] < uLo) uLo = mu[k];
                        if (mu[k] > uHi) uHi = mu[k];
                        int cell = (int)MathF.Floor(mu[k]);
                        cellCounts[cell] = (cellCounts.TryGetValue(cell, out var had) ? had : 0) + 1;
                    }
                    int mainCell = 0, mainCount = -1;
                    foreach (var (cell, count) in cellCounts)
                        if (count > mainCount) { mainCell = cell; mainCount = count; }
                    int outside = mu.Length / 2 - Math.Max(mainCount, 0);
                    if (outside > 0)
                    {
                        straddling++;
                        service.log.Information("[Proteus] second skin: {0} has {1} of {2} vertices outside its main UV "
                                      + "cell [{3}..{4}) (u {5:F2}..{6:F2}) — those sample through the sampler's "
                                      + "wrap, with or without un-mirroring; the rest un-mirror normally",
                            bodies[i].Path, outside, mu.Length / 2, mainCell, mainCell + 1, uLo, uHi);
                    }
                    unmirrorPart[i] = true;
                }

                int usable = unmirrorPart.Count(x => x);
                if (usable == 0)
                {
                    service.log.Information("[Proteus] second skin: asymmetric {0} art over {1} gen2 part(s), but none is "
                                  + "un-mirrorable ({2} read as un-mirrored, {3} had no readable geometry) — "
                                  + "leaving the shell in {4}",
                        unmirrorInto, gen2Parts, notMirrored, unreadable, bodyType ?? "unknown");
                    unmirrorInto = null;
                }
                else
                {
                    service.log.Information("[Proteus] second skin: asymmetric {0} art over {1} of {2} gen2 part(s) ({3} total, "
                                  + "{4} with UV strays) — cutting the shell in {0} space (was {5}) and un-mirroring "
                                  + "those parts' vertices",
                        unmirrorInto, usable, gen2Parts, bodies.Count, straddling, bodyType ?? "unknown");
                    bodyType = unmirrorInto;
                }
            }
            unmirror = unmirrorInto != null;
            if (!unmirror) Array.Clear(unmirrorPart);
        }

        private void ConvertUvSpace()
        {
            // ── one shell, one UV space ───────────────────────────────────────────────────────────
            // The art is remapped into `bodyType` once, so every part's UVs must be in that space. A part in another space
            // has its VERTICES converted rather than its art, since materials are scarce; this also fixes its coverage trim.
            uvConverters = new List<UVRemapService.UvConversion?>(bodies.Count);
            foreach (var b in bodies)
            {
                var conv = service.uvRemap.UvConverter(b.Uv, bodyType, unmirror);
                uvConverters.Add(conv);
                if (b.Uv == null || string.Equals(b.Uv, bodyType, StringComparison.OrdinalIgnoreCase)) continue;
                string partUv = b.Uv, shellUv = bodyType ?? "unknown";
                if (conv != null)
                    service.log.Information("[Proteus] second skin: {0} is {1}-UV in a {2}-UV shell — converting its "
                                  + "vertices to {2}", b.Path, partUv, shellUv);
                else
                    // Not fatal, and not silent: this part renders as if the overlay had no art there.
                    service.log.Warning("[Proteus] second skin: {0} is {1}-UV in a {2}-UV shell and no transfer map "
                              + "covers that pair — the overlay will not land on this part", b.Path, partUv, shellUv);
            }
        }

        private void VoteCutCode()
        {
            // ── the space the geometry is IN ──────────────────────────────────────────────────────
            // The game race-deforms a model by the race code of the PATH it loaded from, so a shell deforms with the body only
            // when hosted under the same code. This is not the equipment code voted above. Every host loads in the
            // character's own space, so this is a diagnosis (logged, warned per host) rather than something moving the path fixes.
            // Majority vote; ties and unreadable paths fall back to the equipment code.
            var cutVotes = CodeVotes(bodies.Select(b => b.Path));
            cutCode = equipCode;
            if (cutVotes.Count == 1
                // A tie keeps the equipment code rather than letting grouping order decide.
                || (cutVotes.Count > 1 && cutVotes[0].Count() > cutVotes[1].Count()))
                cutCode = cutVotes[0].Key;
            if (cutVotes.Count > 1)
                service.log.Warning("[Proteus] second skin: the cut parts are in more than one model space [{0}] — hosting "
                          + "in c{1}; the other part(s) will be deformed differently from the body they copy",
                    string.Join(", ", cutVotes.Select(g => $"{g.Key}x{g.Count()}")), cutCode);
            // Always log the tally: a unanimous vote can still be unanimously wrong.
            service.log.Information("[Proteus] second skin: cut in c{0} space ({1} part(s), votes [{2}]) — a host that "
                          + "loads under a different code will render it a race-size wrong",
                cutCode, bodies.Count,
                cutVotes.Count > 0
                    ? string.Join(", ", cutVotes.Select(g => $"c{g.Key}x{g.Count()}"))
                    : $"no readable path codes, fell back to the equipment code c{equipCode}");
        }

        private void ResolveSurfaces()
        {
            // ── the surfaces this build cuts from ─────────────────────────────────────────────────
            // The body first: the only surface assembled from everything resolved above, and the only one that can span
            // several hosts. Human-part surfaces are appended as layers need them. Body sources take the default skin-mesh
            // filter and the redundancy pass; the writer derives the bands, this side supplies only the cached measurement.
            bool dropRedundant = service.config.HideRedundantMeshes;
            bodySurface = new ResolvedSurface(
                new ShellSurfaceKey(ShellSurfaceKind.Body, string.Empty),
                bodies.Select((b, i) => new SecondSkinWriter.SourceSpec(
                    b.Bytes,
                    KeepMaterial: null,
                    EnabledShapes: Interop.BodyShapeReader.Split(b.Shapes).Shapes,
                    // The variant this body draws when it ships more than one of a region (BodyShapeReader.ReadEnabledShapes);
                    // independent of the redundancy setting.
                    HiddenAttributes: Interop.BodyShapeReader.Split(b.Shapes).HiddenAttributes,
                    UvConv: i < uvConverters.Count ? uvConverters[i] : null,
                    DelegateKey: "keep:-|uv:" + (i < uvConverters.Count && uvConverters[i] != null
                        ? $"{b.Uv}>{bodyType}:{unmirror}" : "-"),
                    DropConnectors: dropRedundant,
                    // Decided per part above: a gen2 part whose UV reads as mirrored AND fits one integer cell.
                    UnmirrorSides: i < unmirrorPart.Length && unmirrorPart[i],
                    Profile: dropRedundant ? service.ConnectorProfileFor(b.Path, b.Bytes) : null)).ToList(),
                bodies.Select(b => b.Path).ToList(),
                cutCode,
                bodyType);
            surfaces = new List<ResolvedSurface> { bodySurface };
        }

        private void AssignLayerSurfaces()
        {
            new SurfaceAssignment(this).Run();
        }

        private void ResolveContent()
        {
            new ContentResolution(this).Run();
        }

        private void ChooseHosts()
        {
            // The surfaces those units live on, resolved after the gear layers so a shared key is the one cut from real
            // geometry. A surface introduced here has no sources; it names the race space and, through RequiresNativeHost,
            // which hosts may carry it.
            unitSurface = new int[contentUnits.Count];
            var contentByKey = new Dictionary<ShellSurfaceKey, int>();
            for (int i = 0; i < contentUnits.Count; i++)
            {
                var key = contentUnits[i].Surface;
                if (resolvedByKey.TryGetValue(key, out var known) && known >= 0) { unitSurface[i] = known; continue; }
                if (contentByKey.TryGetValue(key, out var made)) { unitSurface[i] = made; continue; }

                // A natively-authored part lives in the character's own race space, not the shared equipment cut space.
                var cut = key.IsBody ? bodySurface.CutCode : (drawnRaceCode ?? charCode);
                surfaces.Add(new ResolvedSurface(key, [], [], cut, null));
                contentByKey[key] = surfaces.Count - 1;
                unitSurface[i] = surfaces.Count - 1;
            }

            // Accessories the shell can spill across, in fill priority (glasses -> rings -> bracelet -> necklace -> Emperor
            // fallback), each holding MaxMaterials - BaseMatCount layers. An equipped host APPENDS; the Emperor REPLACES.
            // Chosen against the body's cut space.
            // The packs whose geometry this build places: a carrier slot holding one of their own leftover .mdl redirects must
            // not be vetoed as "another mod".
            var hostedPackRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cEntry, _) in contentLayers ?? [])
                if (cEntry.ModRoot is { Length: > 0 } r) hostedPackRoots.Add(r);

            hosts = service.ChooseHosts(bodySurface.CutCode, equipCode, drawnRaceCode ?? charCode,
                equippedAccessories, metModels, invisibleGlassesSet, outputRoot, hostedPackRoots,
                emperorRingVariant, invisibleGlassesVariant, out claimedCarriers);

            // Which surface each host carries: one model, one path, one EQDP entry, so only layers agreeing on a race code.
            hostSurface = new int[hosts.Count];
        }

        private void PlanPublish()
        {
            // ── per-host publish decision, resolved once for BOTH loops below ──────────────────────────────
            // The material loop and the host loop must agree on the race code, because a material is looked up under the code
            // its model loads at. hostRace is the CHARACTER's real race (drawnRaceCode), never the code of the path the model
            // loads from now: our own redirects move that, which made the shell alternate on and off. The path code is a
            // fallback before any walk has returned; charCode is the last resort.
            // Native: cut space is not on the wearer's fall-through chain, so publish at hostRace and declare that race has
            // the model, loading it with no deform. Carriers only; an APPEND host keeps WarnForeignAppendHost.
            // Cap total placeable layers at the single-char base-36 disk-id space; the excess folds into the over-budget drop.
            totalCapacity = Math.Min(hosts.Sum(h => SecondSkinWriter.MaxMaterials - h.BaseMatCount), DiskIdSpace);

            // Only a shell whose bytes actually differ from what's on disk needs a full redraw.
            shellChanged = false;

            // The reinforced toe spans the model writer: its normals are held back from WriteTextures, and the placed-cap
            // footprints come back from every host's writer. Keyed by in-model material name.
            pendingNormals = new List<(string Material, int Density, DeferredShellNormal Normal)>();
            toeReinforceMaps = new Dictionary<string, (byte[] Mask, int Size)>(StringComparer.Ordinal);

            // Layers assigned to each host. Two letters per layer: the in-model MATERIAL INDEX (host base + position), and a
            // globally-unique DISK letter so hosts never overwrite one ss_<letter> file (see ShellNormalGhost).
            perHostLayers = new List<SecondSkinLayer>[hosts.Count];
            for (int h = 0; h < hosts.Count; h++) perHostLayers[h] = new List<SecondSkinLayer>();

            diskLetter = 0;
            maskLayers = 0;
            clothLayers = 0;    // successfully placed
            overBudget = 0;
            overBudgetMask = 0; // real layers that ran out of accessory capacity
            // Layers with no host that could carry their SURFACE: a carrier-only surface that was offered hosts and none could
            // take it. Distinct from a capacity overflow, and from `droppedLayers` (never resolved), because the remedy differs.
            unhostedLayers = new List<int>();
        }

        private void DistributeLayers()
        {
            // ── Layer → host distribution ──────────────────────────────────────────
            // Layers arrive bottom-first with the mask LAST. The FIRST host draws in front, so the TOP layers fill it and lower
            // layers spill to the hosts behind. Within a host, stack order is kept so the topmost has the highest material
            // index. Over capacity, the BOTTOM layers drop, never the mask.
            // Done per SURFACE, body first: a host has one EQDP entry, so its layers must agree on a race code, and a
            // natively-authored surface needs a CARRIER host, whose metadata is ours to change.
            remaining = new int[hosts.Count];
            for (int i = 0; i < hosts.Count; i++)
                remaining[i] = SecondSkinWriter.MaxMaterials - hosts[i].BaseMatCount;
            hostClaim = new int?[hosts.Count];     // surface index that has taken this host
            diskBudget = DiskIdSpace;              // the base-36 cap, now enforced across all surfaces

            work = new List<(int LayerIdx, int HostIdx)>();
            // Surface order: body, then the rest as resolved. Body never yields a host to a human part.
            foreach (var surfIdx in Enumerable.Range(0, surfaces.Count))
            {
                var surf = surfaces[surfIdx];
                var layerIdxs = new List<int>();
                for (int i = 0; i < gearOverlays.Count; i++)
                    if (layerSurface[i] == surfIdx) layerIdxs.Add(i);
                if (layerIdxs.Count == 0) continue;

                bool carrierOnly = surf.Key.RequiresNativeHost;
                var eligible = new List<int>();
                for (int i = 0; i < hosts.Count; i++)
                {
                    if (remaining[i] <= 0) continue;
                    // Only a carrier's EQDP may be rewritten, so only a carrier can publish a native surface undeformed.
                    if (carrierOnly && hosts[i].BaseModel != null) continue;
                    // Already taken by ANOTHER SURFACE. Identity, not cut-code equality: a host is built from exactly one surface's
                    // sources, so sharing it would replace the first surface's geometry.
                    if (hostClaim[i] is { } claimed && claimed != surfIdx) continue;
                    eligible.Add(i);
                }

                int capacity = Math.Min(eligible.Sum(i => remaining[i]), diskBudget);
                if (capacity == 0 && carrierOnly)
                {
                    // Skipped, not squeezed: a native surface on a deforming host renders visibly wrong. Reported apart from a capacity
                    // overflow because the remedy is a free ring or facewear slot.
                    unhostedLayers.AddRange(layerIdxs);
                    service.log.Warning("[Proteus] second skin: {0} — {1} layer(s) skipped, no host can carry it. It must "
                              + "not be race-deformed, so it needs a slot Proteus can replace outright (a free "
                              + "ring, or the facewear slot); the {2} host(s) available are all append hosts or "
                              + "already full",
                        surf.Key, layerIdxs.Count, hosts.Count);
                    continue;
                }

                int placeable = Math.Min(layerIdxs.Count, capacity);
                int dropCount = layerIdxs.Count - placeable;
                int cursor = layerIdxs.Count - 1;              // the TOP layer of THIS surface (its mask)
                foreach (var h in eligible)
                {
                    if (cursor < dropCount) break;
                    int take = Math.Min(remaining[h], cursor - dropCount + 1);
                    take = Math.Min(take, diskBudget);
                    if (take <= 0) break;
                    for (int k = cursor - take + 1; k <= cursor; k++)   // ascending → topmost lands last (highest idx)
                        work.Add((layerIdxs[k], h));
                    remaining[h] -= take;
                    diskBudget -= take;
                    hostClaim[h] = surfIdx;
                    hostSurface[h] = surfIdx;
                    cursor -= take;
                }
                for (int k = 0; k < dropCount; k++)            // the dropped bottom layers = over budget
                {
                    overBudget++;
                    if (gearOverlays[layerIdxs[k]].Overlay.Descriptor.IsMaskShell) overBudgetMask++;
                }
            }
            // Layers whose surface could not be resolved at all (already logged by the resolver). NOT folded into
            // unhostedLayers: a different failure with a different remedy. Split by whether the character is drawing anything,
            // which tells a transient mid-redraw composite from a real mismatch.
            unresolvedLayers = new List<int>(droppedLayers);
        }

        private void PlaceContent()
        {
            // ── content units take what the shells left ───────────────────────────
            // After the shells, from the same remaining[]/hostClaim[]/diskBudget state. One slot per unit, first host with
            // room that may carry its surface; a unit that finds none is reported, never squeezed in over a shell.
            contentWork = new List<(int Unit, int HostIdx)>();
            contentUnhosted = new List<int>();
            for (int u = 0; u < contentUnits.Count; u++)
            {
                int surfIdx = unitSurface[u];
                bool carrierOnly = surfaces[surfIdx].Key.RequiresNativeHost;
                int chosen = -1;
                for (int h = 0; h < hosts.Count && diskBudget > 0; h++)
                {
                    if (remaining[h] <= 0) continue;
                    // Only a carrier's EQDP may be rewritten, so only a carrier can publish a natively-authored piece undeformed.
                    if (carrierOnly && hosts[h].BaseModel != null) continue;
                    if (hostClaim[h] is { } claimed && claimed != surfIdx) continue;
                    chosen = h;
                    break;
                }
                if (chosen < 0) { contentUnhosted.Add(u); continue; }

                remaining[chosen]--;
                diskBudget--;
                hostClaim[chosen] = surfIdx;
                hostSurface[chosen] = surfIdx;
                contentWork.Add((u, chosen));
            }
        }

        private void PlanHosts()
        {
            // Per host, reading that host's surface's cut code, computed after allocation decides which surface it carries.
            plan = new (string HostRace, bool Native, string PublishCode)[hosts.Count];
            for (int i = 0; i < hosts.Count; i++)
            {
                var h0 = hosts[i];
                var hSurf = surfaces[hostSurface[i]];
                var hCut = hSurf.CutCode;
                var race = drawnRaceCode
                        ?? (h0.ModelPath != null ? PathCharCode(h0.ModelPath) : null)
                        ?? charCode;
                // A natively-authored surface is already the right shape, so never fall through, whatever the codes say.
                bool native = h0.BaseModel == null
                           && (hSurf.Key.RequiresNativeHost
                            || (!string.Equals(race, hCut, StringComparison.OrdinalIgnoreCase)
                                && !CanFallThrough(race, hCut)));
                if (native && !hSurf.Key.RequiresNativeHost)
                    service.log.Warning("[Proteus] second skin: host {0}{1:D4}/{2} — the shell claims to be cut in c{3}, "
                              + "which is not on c{4}'s fall-through chain. Publishing NATIVELY at c{4} instead "
                              + "(no deform); one of the two codes is wrong and c{3} is the suspect",
                        h0.Prefix, h0.SetId, h0.Slot, hCut, race);
                plan[i] = (race, native, native ? race : hCut);
            }
        }

        private void PlanSiblingRelief()
        {
            alphaByLayer = new byte[gearOverlays.Count][];
            reliefContribs = new List<(string ModDir, int LayerIdx, byte[] Normal)>();
            for (int i = 0; i < gearOverlays.Count; i++)
            {
                var (rEntry, rOv) = gearOverlays[i];
                var rd = rOv.Descriptor;
                if (rd.IsMaskShell) continue;   // mask coverage/relief is handled by BuildMaskCoverage
                if (layerSurface[i] < 0) continue;   // surface unresolved — the layer is not being built
                var tCov = PhaseCounter.Begin();
                var (rSrc, rDst) = UvFor(i, rd);
                var rAlpha = service.BuildAlpha(rd, rEntry, rSrc, rDst, texSize, texSize, MaskAdds(rEntry, rOv));
                alphaByLayer[i] = rAlpha;
                if (rd.Normal == null || rAlpha == null) { service.statsCoverage.Stop(tCov); continue; }
                var rNormal = service.LoadRemapped(rd.Normal, rEntry.SidecarRoot, rSrc, rDst, texSize, texSize);
                if (rNormal == null) { service.statsCoverage.Stop(tCov); continue; }
                rNormal = (byte[])rNormal.Clone();   // LoadRemapped may hand back a shared cached buffer
                int nn = Math.Min(rAlpha.Length, rNormal.Length / 4);
                for (int p = 0; p < nn; p++) rNormal[p * 4 + 3] = rAlpha[p];   // coverage → alpha lane (the gate)
                reliefContribs.Add((rEntry.ModDirectory, i, rNormal));
                service.statsCoverage.Stop(tCov);
            }

            // A toe cap belongs to the FOOT, not the mod that ships the map: one mod paints it and every shell over the toes
            // is rebuilt with it. The map is remapped into body UV with its own mod's source type.
            sharedToeCap = null;
            var capCandidates = (allEntries ?? gearOverlays.Select(g => g.Entry).ToList())
                .GroupBy(e => e.ModDirectory, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First());
            foreach (var tEntry in capCandidates)
            {
                var tPath = service.discovery.ResolveActiveToeCap(tEntry);
                if (tPath == null) continue;

                // Remapped with the OWNING mod's UV space; after that it is body-UV pixels anyone can use.
                var tDesc = gearOverlays.FirstOrDefault(g =>
                    string.Equals(g.Entry.ModDirectory, tEntry.ModDirectory, StringComparison.OrdinalIgnoreCase)).Overlay?.Descriptor;
                var tSrc = tDesc != null ? tDesc.SourceBodyType ?? InferOverlayBodyType(tDesc) : bodyType;
                sharedToeCap = service.ReadToeCap(tPath, tSrc, bodyType);
                if (sharedToeCap != null)
                {
                    service.log.Information("[Proteus] second skin: toe cap {0} from \"{1}\" applies to every shell over the toes",
                        Path.GetFileName(tPath), tEntry.ModDirectory);
                    break;
                }
            }

            // A bust bridge belongs to the GARMENT: any of a mod's shells asking for it spans all of them, at the strongest
            // setting, or the stacked shells cross. Per mod, not global like the toe cap. The nipple smooth follows the same rule.
            bridgeByMod = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            smoothByMod = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            cleftByMod = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foldByMod = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var bEntry in (allEntries ?? gearOverlays.Select(g => g.Entry).ToList())
                         .GroupBy(e => e.ModDirectory, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                if (bEntry.Metadata.BustBridge == true)
                {
                    float s = Math.Clamp(bEntry.Metadata.BustBridgeStrength ?? 1f, 0f, 1f);
                    if (s > 0f) bridgeByMod[bEntry.ModDirectory] = s;
                }
                if (bEntry.Metadata.SmoothNipples == true)
                {
                    float s = Math.Clamp(bEntry.Metadata.SmoothNipplesStrength ?? 1f, 0f, 1f);
                    if (s > 0f) smoothByMod[bEntry.ModDirectory] = s;
                }
                if (bEntry.Metadata.CleftBridge == true)
                {
                    float s = Math.Clamp(bEntry.Metadata.CleftBridgeStrength ?? 1f, 0f, 1f);
                    if (s > 0f) cleftByMod[bEntry.ModDirectory] = s;
                }
                if (bEntry.Metadata.SmoothFold == true)
                {
                    float s = Math.Clamp(bEntry.Metadata.SmoothFoldStrength ?? 1f, 0f, 1f);
                    if (s > 0f) foldByMod[bEntry.ModDirectory] = s;
                }
            }
            foreach (var (cMod, cStrength) in cleftByMod)
                service.log.Information("[Proteus] second skin: cleft bridge at {0:0.##} applies to every shell of \"{1}\"",
                    cStrength, cMod);
            foreach (var (fMod, fStrength) in foldByMod)
                service.log.Information("[Proteus] second skin: fold smoothing at {0:0.##} applies to every shell of \"{1}\"",
                    fStrength, fMod);
            foreach (var (sMod, sStrength) in smoothByMod)
                service.log.Information("[Proteus] second skin: nipple smoothing at {0:0.##} applies to every shell of \"{1}\"",
                    sStrength, sMod);
            foreach (var (bMod, bStrength) in bridgeByMod)
                service.log.Information("[Proteus] second skin: bust bridge at {0:0.##} applies to every shell of \"{1}\"",
                    bStrength, bMod);

            // Which MOD is outermost on each host: every one of its shells gets backfaces drawn, and no one else's. Not every
            // spanning shell (inner undersides blend through), and not the single outermost shell (that is the mask alone).
            topSpanningModOnHost = new Dictionary<int, string>();
            foreach (var (wi, wh) in work)
                if (bridgeByMod.ContainsKey(gearOverlays[wi].Entry.ModDirectory))
                    topSpanningModOnHost[wh] = gearOverlays[wi].Entry.ModDirectory;   // work is in stack order
        }

        private void BakeShellTextures()
        {
            new ShellTextureBake(this).Run();
        }

        private void PlaceContentMeshes()
        {
            // ── imported content: the pack's own meshes and its own material ──────────────────────
            // Host naming, variant folder and material letter follow the shells' convention; a content unit is one more
            // material on the accessory. The .mtrl is the PACK'S, published byte-for-byte (colour rows aside).
            contentPlaced = 0;
            // Texture game path → the pack file already published there. Units are told apart by SOURCE: each copies to a
            // name of its own, so two that share one texture would never compare equal by destination.
            var texSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (u, hIdx) in contentWork)
            {
                var unit = contentUnits[u];
                var host = hosts[hIdx];
                char matLetter = (char)('a' + host.BaseMatCount + inHost[hIdx]);
                char diskChar  = DiskId(diskLetter);

                var hostCode = host.ModelPath != null
                    ? PathCharCode(host.ModelPath) ?? plan[hIdx].PublishCode
                    : plan[hIdx].PublishCode;
                string matName     = $"mt_c{hostCode}{host.Prefix}{host.SetId:D4}_{host.Slot}_{matLetter}.mtrl";
                string matVariant  = VariantFolderFor(host);
                string matGamePath = $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/material/{matVariant}/{matName}";
                string texPrefix   = $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/texture/ss_{diskChar}_";

                byte[]? mtrl;
                bool glowBuilt = false;
                if (unit.Glow != null)
                {
                    mtrl = service.BuildContentGlowMaterial(unit, texPrefix, texturesDir, diskChar, effectsFolder,
                        texSize, redirects, ref shellChanged);
                    glowBuilt = mtrl != null;
                    // The pack's own material rather than nothing, so a missing effect or template never removes the piece.
                    mtrl ??= GearMaterialWriter.PatchColorTable(unit.Mtrl, unit.Rows);
                }
                else
                {
                    // Only the colour rows the user edited are stamped in. PatchColorTable no-ops on a material with no colour set.
                    mtrl = GearMaterialWriter.PatchColorTable(unit.Mtrl, unit.Rows);
                }

                var matDisk = Path.Combine(materialsDir, $"ss_{diskChar}.mtrl");
                shellChanged |= WriteIfChanged(matDisk, mtrl);
                redirects[matGamePath] = Rel(outputRoot, matDisk);

                // ── the pack's own textures, republished ──────────────────────────────
                // The material keeps its texture paths, but Proteus serves them from the files the selection chose, copied (a
                // Penumbra redirect cannot reach into another mod's folder). Only textures the pack ships (see
                // SelectedTextureFiles). Skipped for a material the glow builder rebuilt, which names paths it published itself.
                int texIdx = 0;
                var republish = glowBuilt
                    ? Enumerable.Empty<KeyValuePair<string, string>>()
                    : unit.TexFiles.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase);
                foreach (var (texGamePath, srcDisk) in republish)
                {
                    // The number is spent either way, so a shared texture does not rename the ones after it.
                    var dstDisk = Path.Combine(texturesDir, $"ct_{diskChar}_{texIdx++}.tex");

                    // An earlier unit already serves this path from this very file: nothing to copy, nothing to say.
                    bool claimed = texSource.TryGetValue(texGamePath, out var from);
                    if (claimed && string.Equals(from, srcDisk, StringComparison.OrdinalIgnoreCase)) continue;

                    // Re-encoded rather than byte-copied when the author left it uncompressed and we are compressing,
                    // so a viewer's sync client has nothing left to convert — see RepublishPackTexture.
                    try { shellChanged |= service.RepublishPackTexture(unit.Entry.ModRoot, texGamePath, srcDisk, dstDisk); }
                    catch (Exception ex)
                    {
                        // Left to Penumbra: a texture that will not copy is not worth dropping the piece over.
                        service.log.Warning("[Proteus] content: {0} — could not republish {1} from {2} ({3}); that "
                                  + "texture falls back to whichever mod Penumbra resolves it to",
                            unit.Entry.ModDirectory, texGamePath, srcDisk, ex.Message);
                        continue;
                    }

                    // Two units claiming one texture path with DIFFERENT files cannot both win; say so rather than let the last win silently.
                    if (claimed)
                        service.log.Warning("[Proteus] content: {0} — two materials want different files at {1}; the "
                                  + "later one wins and the earlier piece may show the wrong texture",
                            unit.Entry.ModDirectory, texGamePath);

                    texSource[texGamePath] = srcDisk;
                    redirects[texGamePath] = Rel(outputRoot, dstDisk);
                }

                // Same "ss_" naming as a shell: ShellColorsetApplier and ColorTableHighlighter key on that prefix and disk char.
                // Registered under EVERY option this material serves, or the glow button loses its target for the others.
                foreach (var (oEntry, oContent) in unit.Owners)
                {
                    var cKey = (oEntry.ModDirectory, oContent.OptionGroup, oContent.Option);
                    if (!shellMaterials.TryGetValue(cKey, out var cList))
                        shellMaterials[cKey] = cList = new List<string>();
                    cList.Add($"ss_{diskChar}.mtrl");
                }

                perHostLayers[hIdx].Add(new SecondSkinLayer
                {
                    MaterialName = "/" + matName,   // the model stores material names with a leading slash
                    Geometry = unit.Geometries,
                });
                inHost[hIdx]++; diskLetter++;
                contentPlaced++;

                // The glow state is logged because a unit with no effect and one whose effect failed look identical otherwise.
                service.log.Information("[Proteus] content mat={0}/{7}/disk={1} -> host {2}{3:D4}/{4}: {5} — {8} mesh(es) "
                              + "for [{6}] glow={9}",
                    matLetter, diskChar, host.Prefix, host.SetId, host.Slot,
                    unit.Entry.ModDirectory,
                    string.Join(", ", unit.Owners.Select(o => o.Content.Option ?? "(default)")),
                    matVariant, unit.Geometries.Count,
                    unit.Glow?.Scroll is { Length: > 0 } s
                        ? (glowBuilt ? s : s + " (FAILED — published the pack's own material)")
                        : "(none)");
            }
        }

        private bool PublishSkeletons()
        {
            // ── extra skeletons, now that we know what published ──────────────────
            // shellMaterials holds a key per option whose material reached a host; a claim whose option is not in it is dropped.
            // One skeleton offered to several body parts is ALTERNATIVES: an unresolvable slot warns only if no part took it.
            var estMissed = new List<(string Mod, string Slot, int Entry)>();
            var estLanded = new HashSet<(string Mod, int Entry)>();

            foreach (var (owner, slot, entry) in estPending)
            {
                if (!shellMaterials.ContainsKey(owner)) continue;

                if (EstSetId(slot, equippedPartModels, bareBodyModels, humanPartModels) is not { } estSet)
                {
                    estMissed.Add((owner.Mod, slot, entry));
                    continue;
                }

                // Deduplicated on the TARGET: the same skeleton twice is one entry; different skeletons conflict, and first wins.
                var key = (slot.ToLowerInvariant(), estSet);
                if (!estClaimed.TryAdd(key, entry))
                {
                    if (estClaimed[key] != entry)
                        service.log.Warning("[Proteus] content: {0} wants extra skeleton {1} on the {2} (set {3}), which "
                                  + "is already claimed for {4} — EST holds one entry per body part",
                            owner.Mod, entry, slot, estSet, estClaimed[key]);
                    continue;
                }

                manipulations.Add(EstManipulation(drawnRaceCode ?? charCode, slot, estSet, entry));
                estLanded.Add((owner.Mod, entry));
                service.log.Information("[Proteus] content: {0} — extra skeleton {1} claimed on the {2}, set {3}. "
                              + "That replaces whatever entry that item had",
                    owner.Mod, entry, slot, estSet);
            }

            // Deduplicated, so several options offering the same alternative say it once.
            foreach (var (mod, slot, entry) in estMissed.Distinct())
            {
                if (estLanded.Contains((mod, entry))) continue;
                service.log.Warning("[Proteus] content: {0} needs extra skeleton {1} on the {2}, but this character "
                          + "is drawing nothing there — its ex bones will not load", mod, entry, slot);
            }

            if (contentUnhosted.Count > 0)
            {
                // In chat as well as the log, deduped by count: a piece that silently does not appear has no other visible cause.
                if (service._lastUnhostedContent != contentUnhosted.Count)
                {
                    service._lastUnhostedContent = contentUnhosted.Count;
                    var msg = string.Format(Loc.Localize("Chat.ContentUnplaced.Fmt",
                        "[Proteus] {0} mesh piece(s) from your mods could not be placed — your accessories are "
                      + "out of material slots. Equip another ring / bracelet / necklace, or turn off a layer."),
                        contentUnhosted.Count);
                    _ = Plugin.Framework.RunOnFrameworkThread(
                        () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 25).Build()));
                }
                service.log.Warning("[Proteus] content: {0} piece material(s) could not be placed — every host is full or "
                          + "cannot carry their surface. Free an accessory slot, or turn off a layer",
                    contentUnhosted.Count);
            }
            else service._lastUnhostedContent = -1;

            placed = maskLayers + clothLayers + contentPlaced;
            if (placed == 0) { result = null; return false; }
            return true;
        }

        private void ReportUnhosted()
        {
            // Layers whose SURFACE could not be hosted, reported apart from capacity overflow because they need a slot Proteus
            // can replace outright (a free ring or the facewear slot). Deduped on the SET of surfaces, not a count.
            if (unhostedLayers.Count > 0)
            {
                // Names taken from the SAME layers the count came from — see unhostedLayers.
                var keys = string.Join(", ", unhostedLayers
                    .Select(i => layerSurfaceName[i])
                    .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
                if (!string.Equals(service._lastUnhostedSurfaces, keys, StringComparison.Ordinal))
                {
                    service._lastUnhostedSurfaces = keys;
                    var msg = string.Format(Loc.Localize("Chat.UnhostedLayers.Fmt",
                        "[Proteus] Some layers on your {1} could not be placed (layers: {0}). Those must not be "
                      + "race-deformed, so they need a slot Proteus can replace outright: free a ring slot "
                      + "(either hand) or your facewear slot and they will appear."), unhostedLayers.Count, keys);
                    _ = Plugin.Framework.RunOnFrameworkThread(
                        () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 25).Build()));
                }
                service.log.Warning("[Proteus] second skin: {0} layer(s) unhosted on surface(s) [{1}]",
                    unhostedLayers.Count, keys);
            }
            else service._lastUnhostedSurfaces = null;

            // Layers whose surface was never resolved: its own sentence, said only when the character is drawn (a mid-redraw
            // composite sees nothing). Deduped on the set.
            if (unresolvedLayers.Count > 0)
            {
                var keys = string.Join(", ", unresolvedLayers
                    .Select(i => layerSurfaceName[i])
                    .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
                bool characterDrawn = humanPartModels is { Count: > 0 };
                if (!characterDrawn)
                {
                    // Deliberately leaves _lastUnresolvedSurfaces alone: an undrawn composite is no evidence, and clearing here would
                    // re-arm the notice on every redraw. Re-arming happens in the `else` below.
                    service.log.Information("[Proteus] second skin: {0} layer(s) on surface(s) [{1}] had nothing to cut "
                                  + "— the character is not drawing any human part yet, so this is a redraw in "
                                  + "progress rather than anything to report",
                        unresolvedLayers.Count, keys);
                }
                else
                {
                    if (!string.Equals(service._lastUnresolvedSurfaces, keys, StringComparison.Ordinal))
                    {
                        service._lastUnresolvedSurfaces = keys;
                        var msg = string.Format(Loc.Localize("Chat.UnresolvedSurfaces.Fmt",
                            "[Proteus] Some layers on your {1} were skipped (layers: {0}) — your character isn't "
                          + "drawing that part, so there was nothing to cut them from. Check the Surface set on "
                          + "those overlays matches the face or part you are actually wearing."),
                            unresolvedLayers.Count, keys);
                        _ = Plugin.Framework.RunOnFrameworkThread(
                            () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 25).Build()));
                    }
                    service.log.Warning("[Proteus] second skin: {0} layer(s) skipped — nothing drawn for surface(s) [{1}]",
                        unresolvedLayers.Count, keys);
                }
            }
            else service._lastUnresolvedSurfaces = null;

            // Guidance when even all equipped accessories can't hold the look (deduped by total layer count).
            if (overBudget > 0)
            {
                int totalLayers = maskLayers + clothLayers + overBudget;
                int totalMask = maskLayers + overBudgetMask;
                if (service._lastOverBudgetLayers != totalLayers)
                {
                    service._lastOverBudgetLayers = totalLayers;
                    var msg = string.Format(Loc.Localize("Chat.OverBudget.Fmt",
                        "[Proteus] This look has {0} layers ({1} Mask, {2} Cloth), but only {3} fit across your "
                      + "accessories (Proteus' invisible fallback ring already included). Turn off some layers, "
                      + "or equip another pair of glasses / ring / bracelet / necklace so the rest fit."),
                        totalLayers, totalMask, totalLayers - totalMask, totalCapacity);
                    // Marshalled: the shell build runs off the framework thread, and ChatGui's queue is not thread-safe.
                    _ = Plugin.Framework.RunOnFrameworkThread(
                        () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 25).Build()));   // 25 = yellow
                }
                service.log.Warning("[Proteus] second skin: {0} layers exceed total accessory capacity {1} — {2} dropped",
                    placed + overBudget, totalCapacity, overBudget);
            }
            else service._lastOverBudgetLayers = -1;

            // Only now can a carrier slot left to another mod be called a problem: only when something overflowed or went unhosted.
            if (claimedCarriers.Count > 0 && (overBudget > 0 || unhostedLayers.Count > 0))
                service.NotifyCarriersClaimed(claimedCarriers);
            else
                service._lastClaimedCarriers = null;   // re-arm: the same mod mattering later is news again

            modelChangedAny = false;
        }

        private void SmoothBody()
        {
            // ── the body under the garment, BEFORE anything is cut from it ────────────────────────────
            // Only ONE relax: the body is smoothed here and the shell is cut from the smoothed body, so a shell stays a displaced
            // copy of what is beneath it and cannot be pierced. Two independent relaxes leave the shell inside the body.
            // Only where a garment that asked for it COVERS, so an uncovered breast keeps its shape.
            if (smoothByMod.Count > 0 || foldByMod.Count > 0)
            {
                float smoothMax = smoothByMod.Count == 0 ? 0f : smoothByMod.Values.Max();
                float foldMax = foldByMod.Count == 0 ? 0f : foldByMod.Values.Max();
                // One coverage union for both: two regions of one body, gated by the same question.
                byte[]? union = null;
                int uw = 0, uh = 0;
                foreach (var l in perHostLayers.SelectMany(x => x))
                {
                    if ((l.NippleSmoothStrength <= 0f && l.FoldSmoothStrength <= 0f) || l.Coverage == null
                     || l.CoverageWidth <= 0 || l.CoverageHeight <= 0) continue;
                    if (union == null) { uw = l.CoverageWidth; uh = l.CoverageHeight; union = (byte[])l.Coverage.Clone(); }
                    else if (l.CoverageWidth == uw && l.CoverageHeight == uh && l.Coverage.Length >= uw * uh)
                        for (int p = 0; p < union.Length; p++) if (l.Coverage[p] > union[p]) union[p] = l.Coverage[p];
                }
                var gate = new SecondSkinLayer
                {
                    MaterialName = "/bodysmooth.mtrl",   // never emitted; carries the coverage only
                    Coverage = union,
                    CoverageWidth = union == null ? 0 : uw,
                    CoverageHeight = union == null ? 0 : uh,
                };

                var smoothedBody = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var (bBytes, _, bPath, _) in bodies)
                {
                    // NEVER a regular mod's garment: it is someone else's file, and republishing it replaces it on the character.
                    // Only the bare body and Proteus mods' garments are ours to relax. Checked before the hold below.
                    if (!smoothable.Contains(bPath))
                    {
                        if (service._smoothSkippedLogged.TryAdd(bPath, 0))
                            service.log.Information("[Proteus] second skin: not smoothing {0} — the garment is not from a "
                                          + "Proteus mod", bPath);
                        continue;
                    }

                    // Already ours: these bytes ARE the body we published. Re-running would compound and dropping the redirect would
                    // show the untouched body, so keep the file as it stands.
                    if (bodySettled.Contains(bPath))
                    {
                        var held = SmoothedBodyPath(modelsDir, bPath, bBytes);
                        if (File.Exists(held))
                        {
                            redirects[bPath] = Rel(outputRoot, held);
                            smoothedBody[bPath] = bBytes;
                        }
                        continue;
                    }

                    byte[]? smoothed;
                    var tSmooth = PhaseCounter.Begin();
                    try
                    {
                        smoothed = BodyBridge.SmoothBodyNipples(bBytes, gate, smoothMax,
                            msg => service.log.Debug("[Proteus] second skin: {0}", msg), foldMax);
                    }
                    catch (Exception ex)
                    {
                        service.statsBodySmooth.Stop(tSmooth);
                        service.log.Warning(ex, "[Proteus] second skin: could not smooth {0}", bPath);
                        continue;
                    }
                    if (smoothed == null) { service.statsBodySmooth.Stop(tSmooth); continue; }   // no bust bones, or nothing covered — most parts

                    var disk = SmoothedBodyPath(modelsDir, bPath, smoothed);
                    bool changed = WriteIfChanged(disk, smoothed);
                    service.statsBodySmooth.Stop(tSmooth);
                    redirects[bPath] = Rel(outputRoot, disk);
                    modelChangedAny |= changed;
                    smoothedBody[bPath] = smoothed;
                    if (changed)
                        service.log.Information("[Proteus] second skin: republished {0} with the chest relaxed -> {1}",
                                        bPath, Path.GetFileName(disk));
                }

                if (smoothedBody.Count > 0)
                {
                    // Re-point every surface at the bodies just published. SourcePaths is index-aligned with Sources.
                    for (int i = 0; i < surfaces.Count; i++)
                    {
                        var s = surfaces[i];
                        var swapped = s.Sources.Select((src, k) =>
                            k < s.SourcePaths.Count && smoothedBody.TryGetValue(s.SourcePaths[k], out var nb)
                                ? src with { Model = nb }
                                : src).ToList();
                        surfaces[i] = s with { Sources = swapped };
                    }

                }
            }
        }

        private void BuildHostModels()
        {
            // A height-banded push for measuring how close a shell can sit; off unless its file exists (see PushSweep).
            // Loaded once per composite so every host is measured on the same ladder.
            var pushSweep = service.LoadPushSweep();

            // Build one shell model per host that got layers; fold each into the single Result.
            hostModelPaths = new List<string>();
            appendHostModelPaths = new List<string>();
            for (int h = 0; h < hosts.Count; h++)
            {
                BuildHostModel(pushSweep, h);
            }
        }

        /// <summary>Builds one host's shell model (or reuses a memoised one) and publishes it with its EQDP and variant redirects.</summary>
        private void BuildHostModel(PushSweep? pushSweep, int h)
        {
            new HostModelBuild(this, pushSweep, h).Run();
        }

        private Result? Finish()
        {
            // Now the caps are placed, write the held-back normals. Before the early return: their redirects already went out.
            if (pendingNormals.Count > 0)
            {
                var tNormals = PhaseCounter.Begin();
                service.WriteDeferredNormals(pendingNormals, toeReinforceMaps, redirects, ref shellChanged);
                service.statsDeferredNormals.Stop(tNormals);
            }

            if (hostModelPaths.Count == 0) return null;

            return new Result(redirects, manipulations, shellChanged, shellMaterials, modelChangedAny,
                              hostModelPaths, appendHostModelPaths, contentMaterials, shellLight, contentModels);
        }

        private string VariantFolderFor(HostAccessory h)
        {
            var key = (h.SetId, h.Slot, h.Tree);
            lock (variantMemo)
            {
                if (variantMemo.TryGetValue(key, out var memo)) return memo;
                var folder = ResolveVariantFolder(h);
                variantMemo[key] = folder;
                return folder;
            }
        }

        private string ResolveVariantFolder(HostAccessory h)
        {
            var fromItem = ItemVariantFolder(h);
            var fromTree = TreeVariantFolder(h, out bool exact);

            if (exact && fromTree is { } drawn)
            {
                if (fromItem is { } f && !string.Equals(f.Folder, drawn, StringComparison.OrdinalIgnoreCase))
                    service.log.Information("[Proteus] host {0}{1:D4}/{2}: material folder {3} from the drawn materials, "
                                  + "but item variant {4} maps to {5} through its IMC file — an IMC edit, going "
                                  + "with the drawn one", h.Prefix, h.SetId, h.Slot, drawn, f.Variant, f.Folder);
                return drawn;
            }
            if (fromItem is { } item)
            {
                service.log.Information("[Proteus] host {0}{1:D4}/{2}: material folder {3} from item variant {4} "
                              + "({5}) — none of its materials are drawn yet", h.Prefix, h.SetId, h.Slot,
                    item.Folder, item.Variant, item.Source);
                return item.Folder;
            }
            if (fromTree != null) return fromTree;

            service.log.Warning("[Proteus] host {0}{1:D4}/{2}: no drawn material and no item variant to read its "
                      + "material folder from — publishing under v0001, which only renders if the item is "
                      + "variant 1", h.Prefix, h.SetId, h.Slot);
            return "v0001";
        }

        // The item variant, and the folder its IMC entry names. Null when no variant is known for this host.
        private (string Folder, int Variant, string Source)? ItemVariantFolder(HostAccessory h)
        {
            int variant;
            string source;
            if (h.KnownVariant is { } known)
            {
                variant = known;
                source = "item sheet";
            }
            else if (equippedSlotVariants?.FirstOrDefault(s => s.SetId == h.SetId
                         && string.Equals(s.Suffix, h.Slot, StringComparison.OrdinalIgnoreCase)) is { SetId: > 0 } worn)
            {
                variant = worn.Variant;
                source = "drawn slot";
            }
            else return null;

            // Facewear keeps the variant as the folder; a met host is only ever a facewear carrier.
            if (h.Tree == "equipment" && h.Slot == "met")
                return ($"v{variant:D4}", variant, source);

            var modelPath = h.ModelPath ?? $"chara/{h.Tree}/{h.Prefix}{h.SetId:D4}/model/c0101{h.Prefix}{h.SetId:D4}_{h.Slot}.mdl";
            var entry = ImcEntrySource.FromGame(p => service.textureLoader.LoadRawFile(service.penumbra.ResolvePlayer(p), p),
                modelPath, variant);
            // No readable IMC: the variant number itself is the folder for any item whose entry was not repointed.
            int materialId = entry is { MaterialId: > 0 } e ? e.MaterialId : variant;
            return ($"v{materialId:D4}", variant, entry == null ? $"{source}, no IMC" : $"{source}, IMC");
        }

        // What the drawn materials say. `exact` is set only when a material of THIS slot answered.
        private string? TreeVariantFolder(HostAccessory h, out bool exact)
        {
            exact = false;
            var dir = $"chara/{h.Tree}/{h.Prefix}{h.SetId:D4}/material/v";

            // The variant belongs to the item in THIS slot, not to the set (one accessory set id covers _nek, _ear, _wrs,
            // _rir), so only a material of this slot answers outright. Set-wide is a fallback only when every piece agrees.
            var slotTag = $"_{h.Slot}_";
            string? setVariant = null;
            bool setDisagrees = false;
            if (activeMaterials != null)
                foreach (var m in activeMaterials)
                {
                    if (!m.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                    int end = m.IndexOf('/', dir.Length);
                    if (end <= dir.Length) continue;
                    var folder = m[(dir.Length - 1)..end];
                    if (m.Contains(slotTag, StringComparison.OrdinalIgnoreCase))
                    {
                        exact = true;
                        return folder;
                    }
                    if (setVariant == null) setVariant = folder;
                    else if (!string.Equals(setVariant, folder, StringComparison.OrdinalIgnoreCase))
                        setDisagrees = true;
                }

            return setVariant != null && !setDisagrees ? setVariant : null;
        }

        // A mask OCCLUDES everything beneath it (matches CompositorService.MaskAdds): gear overlays are erased under an
        // opaque mask and only the mask shell renders there, so a mask never hands coverage to a lower shell.
        private static bool MaskAdds(OverlayEntry e, ResolvedOverlay o) => false;

        // Group rather than take the first: a disagreement is decided by weight of evidence, not enumeration order.
        private static List<IGrouping<string, string>> CodeVotes(IEnumerable<string> paths)
            => paths.Select(PathCharCode).Where(c => !string.IsNullOrEmpty(c)).Select(c => c!)
                .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // ── Sibling-relief pre-pass ──────────────────────────────────────────────
        // Opaque shells at one position occlude rather than blend, so each cloth overlay's normal is compounded into its
        // SAME-MOD sibling shells, gated by its own coverage; only R/G is written, so each shell's blue gate is untouched.
        // Coverage (BuildAlpha) is computed here once per non-mask overlay and reused as the shell's own alpha.
        // The UV space a layer's art must end up in: its surface's. Human parts are NATIVE at both ends (null), so a stray
        // SourceBodyType cannot run a body remap across face art.
        private (string? Src, string? Dst) UvFor(int layerIdx, OverlayDescriptor d)
        {
            var s = surfaces[layerSurface[layerIdx] >= 0 ? layerSurface[layerIdx] : 0];
            if (s.Key.IsBody) return (d.SourceBodyType ?? InferOverlayBodyType(d), s.UvSpace);

            // The one human-part exception: a face shell cut into the DOUBLED sheet needs ordinary face-layout art spread
            // across both halves; art already declaring the doubled sheet passes through. Only FACE spaces are honoured.
            if (string.Equals(s.UvSpace, UVRemapService.FaceSplitSpace, StringComparison.OrdinalIgnoreCase))
            {
                bool declaredFace =
                    string.Equals(d.SourceBodyType, UVRemapService.FaceSpace, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(d.SourceBodyType, UVRemapService.FaceSplitSpace, StringComparison.OrdinalIgnoreCase);
                return (declaredFace ? d.SourceBodyType : UVRemapService.FaceSpace, s.UvSpace);
            }
            return (null, null);
        }

        /// <summary>How far this layer's surface wants its shell pushed off the skin (see
        /// <see cref="ShellSurfaceKey.PushScale"/>).</summary>
        private float PushFor(int layerIdx)
            => surfaces[layerSurface[layerIdx] >= 0 ? layerSurface[layerIdx] : 0].Key.PushScale;

        // Most specific first, and a FACE never falls back to a body material; the body template is the last resort for a
        // body surface. The template follows the surface and the wearer's race (skin-type shader key, face material).
        private byte[]? LoadTemplate(ResolvedSurface layerSurf, string shader, bool report)
        {
            var faceId = layerSurf.Key.Kind == ShellSurfaceKind.Face ? layerSurf.Key.Id : null;
            var candidates = new List<string> { GearMaterialWriter.TemplateFor(shader, layerSurf.CutCode, faceId) };
            if (faceId != null) candidates.Add(GearMaterialWriter.SkinTemplate(null, faceId));
            candidates.Add(GearMaterialWriter.TemplateFor(shader));
            foreach (var cand in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var t = service.textureLoader.LoadRawMtrl(null, cand);
                if (t == null) continue;
                if (report && !string.Equals(cand, candidates[0], StringComparison.OrdinalIgnoreCase))
                    service.log.Information("[Proteus] second skin: no {0} template at {1} — using {2}",
                        shader, candidates[0], cand);
                return t;
            }
            return null;
        }
    }
}
