using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// Builds the "second skin": every <see cref="OverlayLayer.Gear"/> overlay becomes a shell — a copy of
/// the character's skin mesh, pushed out along its normals and drawn as gear so it can run a full gear
/// shader (color table, sphere maps, metalness, scrolling emissive), none of which skin.shpk offers.
///
/// The shells ride on The Emperor's New accessories (set a0053), which are invisible, so they survive
/// any outfit and cost no visible equipment slot. Chest rides the right ring, legs the left.
/// </summary>
public sealed class SecondSkinService
{
    private readonly PenumbraBridge penumbra;
    private readonly TextureLoader textureLoader;
    private readonly SidecarDiscoveryService discovery;
    private readonly UVRemapService uvRemap;
    private readonly Configuration config;
    private readonly IPluginLog log;

    /// <summary>Textures are authored in BODY UV (the shell inherits the body's UVs).</summary>
    // internal so the compositor can prefetch this phase's art at the right size; see PrefetchAhead.
    internal const int TexSize = 2048;

    /// <summary>Coverage only decides whether a whole triangle survives, so it can be coarse.</summary>
    private const int CoverageSize = 256;

    /// <summary>
    /// The toe-cap mask is sampled per VERTEX, not per texel, so it needs more resolution than coverage
    /// (the toes are a small, position-sensitive patch of body UV) but far less than the art.
    /// </summary>
    private const int ToeCapSize = 512;

    /// <summary>
    /// How much of the capped area a shell must actually paint before it gets a toe cap. A shell that
    /// stops at the ankle has no business rebuilding the toes.
    /// </summary>
    private const float MinToeCoverage = 0.25f;

    /// <summary>Number of single-char base-36 shell disk ids (0-9a-z) — the ceiling on placeable layers,
    /// so an id never runs past 'z'.</summary>
    private const int DiskIdSpace = 36;

    /// <summary>Encode a layer's global index as a base-36 disk id char (0-9 then a-z). Digits-first keeps it
    /// ASCII-monotonic ('0'&lt;'9'&lt;'a'&lt;'z'), so the ghost/highlighter's char comparison still orders the stack.</summary>
    private static char DiskId(int d) => (char)(d < 10 ? '0' + d : 'a' + (d - 10));

    /// <summary>The Emperor's New Ring — invisible, so a shell on it shows only our material.</summary>
    // A head/facewear "_met" model smaller than this is treated as an invisible/degenerate item (empty
    // frames) — the shell REPLACES it instead of appending, since a merge into a near-empty model won't
    // render. Real glasses/helmets are tens of KB; "The Emperor's New"-style invisibles are ~1.5 KB.
    private const int DegenerateModelBytes = 3000;

    private const int EmperorSetId = 53;
    private const string Accessory = "rir";
    private const string EqdpSlot = "RFinger";

    /// <summary>
    /// Every skin part is MERGED into the one ring model, each part contributing its own mesh groups.
    /// A part × layer group carries that layer's material, so different regions can run different
    /// shaders. Parts the character isn't drawing are simply skipped.
    /// </summary>
    private static readonly string[] Parts = ["top", "dwn", "glv", "sho"];

    /// <summary>Body ids tried by the whole-body fallback, in preference order. b0001 is the standard
    /// body; a few race/gender combos ship b0101 instead.</summary>
    private static readonly string[] WholeBodyIds = ["b0001", "b0101"];

    public SecondSkinService(
        PenumbraBridge penumbra, TextureLoader textureLoader, SidecarDiscoveryService discovery,
        UVRemapService uvRemap, Configuration config, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.textureLoader = textureLoader;
        this.discovery = discovery;
        this.uvRemap = uvRemap;
        this.config = config;
        this.log = log;
    }

    /// <summary>
    /// Files to redirect, plus the metadata edits that make the shells load.
    ///
    /// <paramref name="ShellChanged"/> is true when the model, a material OR a texture differs from what
    /// was already on disk; a run that rewrites identical bytes reports false.
    ///
    /// <paramref name="ModelChanged"/> narrows that to the .mdl alone, which is the only change that
    /// forces a full redraw. Materials do NOT need one: verified in-game — a gear colorset edit applied
    /// correctly through Glamourer's in-place reload with no redraw. (An older comment here claimed the
    /// in-place path "cannot see a new model or material"; the material half of that was never true, and
    /// it cost a character redraw and its flicker on every colour change.)
    /// </summary>
    public sealed record Result(
        Dictionary<string, string> Redirects, List<object> Manipulations, bool ShellChanged,
        Dictionary<(string ModDir, string? Group, string? Option), List<string>> ShellMaterials,
        bool ModelChanged,
        // The game model paths hosting a shell this composite (one per host that got layers). When this set
        // SHRINKS between composites — a spill host dropped as the layer count fell — the vacated accessory
        // must reload its real model, which only a full redraw does; the compositor forces one on any change.
        List<string> HostModelPaths);

    /// <summary>Write only if the content differs; reports whether it did.</summary>
    private static bool WriteIfChanged(string path, byte[] data)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(data))
                return false;
        }
        catch { /* unreadable — fall through and rewrite */ }

        File.WriteAllBytes(path, data);
        return true;
    }

    /// <summary>
    /// Content hash of each shell texture we last wrote, so we can tell a real change from a rewrite of
    /// identical bytes. The shell's TEXTURES matter as much as its model: an opacity or mask edit only
    /// moves coverage, which lands in the normal map — and the game won't pick that up on an in-place
    /// reload either, because the texture belongs to an accessory rather than the body.
    /// </summary>
    private readonly Dictionary<string, ulong> _texHashes = new(StringComparer.OrdinalIgnoreCase);

    // Layer count last warned about as over the host's material budget — so the chat guidance prints once
    // per changed situation, not every composite. -1 = not currently over budget.
    private int _lastOverBudgetLayers = -1;

    private static ulong Hash(byte[] data)
    {
        ulong h = 14695981039346656037;   // FNV-1a
        foreach (var b in data) { h ^= b; h *= 1099511628211; }
        return h;
    }

    /// <summary>True when the blue channel (byte 2 of each RGBA quad) is 255 across the whole buffer — i.e.
    /// the normal carries no transparency gate, so BC5 (which drops blue) is lossless for it.</summary>
    private static bool IsBlueAllWhite(byte[] rgba)
    {
        for (int i = 2; i < rgba.Length; i += 4)
            if (rgba[i] != 255) return false;
        return true;
    }

    /// <summary>
    /// Build every gear shell for the character. <paramref name="charCode"/> is the human model code
    /// ("0201" = Midlander female). <paramref name="outputRoot"/> is the managed mod directory.
    /// Returns null when there is nothing to build.
    /// </summary>
    /// <summary>
    /// The UV space an overlay's art is painted in, inferred from the body materials it targets — a mod
    /// listing only <c>*_bibo.mtrl</c> is bibo art. Returns null when the materials disagree or name no
    /// body type, in which case the art is assumed to already be in the body's space.
    /// </summary>
    /// <summary>The UV space of a body model, read from its own skin material's suffix, or null.</summary>
    private static string? SkinBodyType(byte[] model)
    {
        try
        {
            return SecondSkinWriter.MaterialNames(model)
                .Select(SecondSkinWriter.SkinMaterialBodyType)
                .FirstOrDefault(t => t != null);
        }
        catch { return null; }
    }

    private static string? InferOverlayBodyType(OverlayDescriptor d)
    {
        string? found = null;
        foreach (var p in d.MaterialGamePaths)
        {
            var t = UVRemapService.InferBodyType(p);
            if (t == null) continue;
            if (found == null) found = t;
            else if (!string.Equals(found, t, StringComparison.OrdinalIgnoreCase)) return null;   // mixed
        }
        return found;
    }

    /// <summary>
    /// Load an overlay image and, if it was painted for a different body's UV layout, remap it into the
    /// body's UV space. The shell INHERITS the body's UVs, so the destination is the character's body UV
    /// type — not the accessory material's. Mirrors CompositorService.RemapIfNeeded; keep them in step.
    /// </summary>
    private byte[]? LoadRemapped(string? rel, string sidecarRoot, string? srcType, string? dstType, int w, int h)
    {
        if (rel == null) return null;
        // Extension tolerance (metadata says diffuse.dds but the file is diffuse.png, etc.) is handled
        // centrally in TextureLoader.LoadPngAsRgba, so skin and gear resolve identically.
        var path = Path.Combine(sidecarRoot, rel);
        return RemapPath(path, srcType, dstType, w, h);
    }

    private byte[]? RemapPath(string path, string? srcType, string? dstType, int w, int h)
    {
        var png = textureLoader.LoadPngAsRgba(path, w, h);
        if (png == null || srcType == null || dstType == null) return png;
        if (string.Equals(srcType, dstType, StringComparison.OrdinalIgnoreCase)) return png;

        // Any source -> gen2 (vanilla): vanilla UV is the RIGHT HALF of bibo UV space, so convert to
        // bibo first (via transfer map when needed), crop, then resize.
        if (string.Equals(dstType, "gen2", StringComparison.OrdinalIgnoreCase))
        {
            var native = textureLoader.LoadPngAsRgba(path, 4096, 4096);
            if (native == null) return png;
            byte[] biboSpace;
            if (string.Equals(srcType, "bibo", StringComparison.OrdinalIgnoreCase))
            {
                biboSpace = native;
            }
            else
            {
                var converted = uvRemap.Remap(native, 4096, 4096, srcType, "bibo");
                if (ReferenceEquals(converted, native)) return png;   // no transfer map — leave it alone
                biboSpace = converted;
            }
            var rightHalf = UVRemapService.CropRightHalf(biboSpace, 4096, 4096);
            return UVRemapService.ResizeBilinear(rightHalf, 2048, 4096, w, h);
        }

        // Transfer maps operate at 4096x4096; our textures are smaller, so remap at full res then resize.
        if (w != 4096 || h != 4096)
        {
            var native4k = textureLoader.LoadPngAsRgba(path, 4096, 4096);
            if (native4k == null) return png;
            var remapped = uvRemap.Remap(native4k, 4096, 4096, srcType, dstType);
            if (ReferenceEquals(remapped, native4k)) return png;
            return UVRemapService.ResizeBilinear(remapped, 4096, 4096, w, h);
        }
        return uvRemap.Remap(png, w, h, srcType, dstType);
    }

    public Result? Build(
        string charCode,
        IReadOnlyList<(OverlayEntry Entry, ResolvedOverlay Overlay)> gearOverlays,
        string outputRoot,
        string? bodyType,
        string? effectsFolder,
        IReadOnlyDictionary<string, string>? equippedPartModels = null,
        IReadOnlyDictionary<string, string>? equippedAccessories = null,
        Func<string, bool>? gen2Allowed = null,
        int? invisibleGlassesSet = null,
        IReadOnlyList<string>? metModels = null,
        // Shape keys the game currently has enabled per body-model stem (see BodyShapeReader). Used to bake
        // body morphs (e.g. "Remove Hip Dips" = shpx_yam_softbutt) into the shell so it follows the body.
        IReadOnlyDictionary<string, HashSet<string>>? enabledBodyShapes = null,
        // Mods that carry a dedicated top mask shell (OverlayDescriptor.IsMaskShell) this build. For these,
        // the mod's OTHER shells must NOT merge the masks' _id/relief — the mask shell owns them, so merging
        // would colour the mask twice. The mask shell itself always merges (it IS the mask).
        IReadOnlySet<string>? maskShellMods = null,
        // Every mod in the look, not just those contributing a shell. A toe cap belongs to the foot, so
        // the mod that ships the map need not be the one wearing anything over the toes.
        IReadOnlyList<OverlayEntry>? allEntries = null)
    {
        if (gearOverlays.Count == 0) return null;

        // A mask OCCLUDES everything beneath it (matches CompositorService.MaskAdds): in a mask's territory
        // every gear overlay — top group included — is erased (its coverage drops to cov·W, and W=0 where the
        // mask is opaque), so the fabric shells go transparent to skin under the mask and only the mask shell
        // renders there. A mask no longer hands its coverage to a lower shell, which would otherwise draw its
        // art/relief/colour straight over the mask.
        static bool MaskAdds(OverlayEntry e, ResolvedOverlay o) => false;

        var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manipulations = new List<object>();
        // Maps each gear overlay's identity to its shell material disk file names (ss_{letter}.mtrl) — what
        // the live resource handle reports — so the colorset editor's "glow" button can target them. A key
        // can hold SEVERAL: a mod/option may carry more than one gear overlay, all baking the same shared
        // colour table, so a row's glow must reach every one of their shell materials.
        var shellMaterials = new Dictionary<(string, string?, string?), List<string>>();

        var modelsDir = Path.Combine(outputRoot, "models");
        var materialsDir = Path.Combine(outputRoot, "materials");
        var texturesDir = Path.Combine(outputRoot, "textures");
        Directory.CreateDirectory(modelsDir);
        Directory.CreateDirectory(materialsDir);
        Directory.CreateDirectory(texturesDir);

        // ── every skin part the character is drawing, MERGED into the one ring model ──
        // The shell is a COPY of the body geometry, so it must be cut from the models the character is
        // actually drawing. A shell built from any other body/size is a different shape and the body
        // pokes through it at any push distance. Resolve them live, every time.
        // gen2 (vanilla) is opt-in per the gear mode, exactly like the skin layer's gen2 sibling — but the
        // gate is per-PART, not per-character: a bibo torso plus a vanilla skirt's exposed legs is ONE
        // shell, and only the vanilla legs must be withheld unless a gear overlay opted into "All bodies".
        bool anyGen2Allowed = gen2Allowed == null || gearOverlays.Any(g => gen2Allowed(g.Entry.ModDirectory));

        // FFXIV keys EQUIPMENT to a model race, not the character's race. Viera and Hrothgar wear Midlander
        // models, race-deformed onto their own skeleton, so a c1801 character's gear, accessories AND e0000
        // parts all live at c0201 paths — the c1801 equivalents were never shipped. Skin is the opposite:
        // keyed to the real race (mt_c1801b0001_bibo.mtrl). The shell is cut from equipment models and
        // hosted on accessories, so everything in that space must use the MODEL code; charCode stays for
        // the body itself. Read it off whatever the game already resolved rather than hardcoding a race
        // table — and it is simply charCode for races that ship their own models.
        var equippedPaths = (equippedPartModels?.Values ?? Enumerable.Empty<string>())
            .Concat(equippedAccessories?.Values ?? Enumerable.Empty<string>())
            .Concat(metModels ?? Enumerable.Empty<string>())
            // NEVER count the Emperor's ring. It is OUR host: last composite redirected it, so Penumbra
            // resolves it straight back to our own output and it reports whatever code WE published it at.
            // Reading the model race off it is a feedback loop — observed live as c0101 -> build shell ->
            // publish at c1801 -> next composite reads 1801 -> every c1801e0000 part missing -> shell torn
            // down -> redraw restores c0101 -> rebuild, forever. (ChooseHosts guards the same hazard when
            // it picks a base model; this is the same trap one layer up.) Vanilla only ships a0053 at
            // c0101 anyway, so it is never evidence of anything.
            .Where(pth => !pth.Contains($"a{EmperorSetId:D4}", StringComparison.OrdinalIgnoreCase));

        // Group rather than take-the-first: a real disagreement is decided by weight of evidence, not by
        // dictionary enumeration order. On the character that exposed this, the honest gear (ril a0031 and
        // met e5501) both say 0201 while only the discounted Emperor said 0101 — and picking wrong costs
        // the ENTIRE shell, not the one stray redraw a wrong guess costs elsewhere.
        var codeVotes = equippedPaths.Select(PathCharCode).Where(c => !string.IsNullOrEmpty(c)).Select(c => c!)
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codeVotes.Count > 1)
            log.Warning("[Proteus] second skin: equipped models disagree on a model code [{0}] — using c{1}",
                string.Join(", ", codeVotes.Select(g => $"{g.Key}x{g.Count()}")), codeVotes[0].Key);

        var modelCode = codeVotes.Count > 0 ? codeVotes[0].Key : null;
        if (modelCode == null)
        {
            // Nothing equipped, so there is no resolved path to read it off — and defaulting to charCode
            // strands exactly the races this exists for: a bare Viera would ask for c1801e0000_top.mdl,
            // which was never shipped, and end up with no shell at all. Probe instead — the character's own
            // code first, then the two bases everything else deforms from — and take the first that
            // actually has an e0000 torso.
            foreach (var cand in new[] { charCode, "0201", "0101" })
            {
                // Existence only — no need to read the model, which would pull megabytes just to discard
                // them. A mod redirect counts (ResolvePlayer yields a file only for modded paths), else ask
                // the game index directly.
                var probe = $"chara/equipment/e0000/model/c{cand}e0000_top.mdl";
                if (penumbra.ResolvePlayer(probe) == null && !Plugin.DataManager.FileExists(probe)) continue;
                modelCode = cand;
                break;
            }
            modelCode ??= charCode;
        }

        if (!string.Equals(modelCode, charCode, StringComparison.OrdinalIgnoreCase))
            log.Information("[Proteus] second skin: c{0} wears c{1} equipment models (race-deformed) — cutting "
                          + "and hosting the shell in c{1} space", charCode, modelCode);

        // Each kept part carries its bytes and the shape keys enabled on THAT body model (by stem), so the
        // writer bakes only the morphs the game is actually applying to that part.
        var bodies = new List<(byte[] Bytes, HashSet<string>? Shapes)>();
        string? modelType = null;   // UV space of the first kept part, from its own skin material
        // Bare-body slots attempted vs. missing — the whole-body fallback below fires only when EVERY one
        // of them came back missing (see there for why "any one missing" is the wrong trigger).
        int barePartsTried = 0, barePartsMissing = 0;
        foreach (var part in Parts)
        {
            // When gear is equipped in a slot, the bare-body part for that slot ISN'T drawn — the gear
            // model is, and it carries the skin it exposes posed to fit (a high heel tiptoes the foot,
            // a bikini bottom reshapes the hip, etc.), as an mt_c….b….skin mesh beside its cloth meshes.
            // Cut the shell from that equipped model so it deforms WITH the gear AND covers only the skin
            // the gear actually exposes (the hidden skin under cloth isn't in the model, so nothing pokes
            // through it); the flat bare-body e0000 would shell the whole body and float off the posed
            // skin. The skin-material filter in SecondSkinWriter keeps only the skin mesh. Slots with no
            // gear (or gear that exposes no skin) fall back to the bare body e0000.
            var bareBody = $"chara/equipment/e0000/model/c{modelCode}e0000_{part}.mdl";
            var bodyGamePath = equippedPartModels != null && equippedPartModels.TryGetValue(part, out var eq)
                ? eq
                : bareBody;
            bool isBarePart = string.Equals(bodyGamePath, bareBody, StringComparison.Ordinal);
            if (isBarePart) barePartsTried++;

            // ResolvePlayer only yields a real file for MODDED models; a vanilla piece resolves to the
            // game path unchanged, so read from the game data in that case. The transcoder reads each
            // model's own vertex declaration, so vanilla and modded models both skin correctly.
            var bodyDisk = penumbra.ResolvePlayer(bodyGamePath);
            var bytes = textureLoader.LoadRawFile(bodyDisk, bodyGamePath);

            if (bytes == null)
            {
                // Only BARE-BODY misses count toward the fallback. A missing EQUIPPED model doesn't: the
                // gear is still drawn in that slot, and shelling the bare skin under it is precisely the
                // poke-through the comment above says to avoid — just skip the slot.
                if (isBarePart) barePartsMissing++;
                // Information, not Debug: when a shell fails to build this is usually the reason, and at
                // Debug it is invisible in the log level people actually run at — which has already cost
                // one round of "why did this fail?" that the log couldn't answer. Says only what it knows:
                // a corrupt mod file and a path the race doesn't ship both land here.
                log.Information("[Proteus] second skin: {0} not loadable, skipping part {1}", bodyGamePath, part);
                continue;
            }

            // The part's UV space names itself in its skin material's suffix. A vanilla (gen2) part gets
            // no shell unless a gear overlay is set to All bodies — otherwise the overlay would wear on
            // vanilla whether or not the author opted in. Ambiguity (a vanilla _a material alongside a
            // gen3 body) is avoided by reading THIS part's own model rather than the loaded-material soup.
            var partType = SkinBodyType(bytes);
            if (string.Equals(partType, "gen2", StringComparison.OrdinalIgnoreCase) && !anyGen2Allowed)
            {
                log.Information("[Proteus] second skin: {0} is vanilla (gen2) — no gear overlay opted into All bodies, skipping part", bodyGamePath);
                continue;
            }
            // Each part's own UV space, resolved path and size — the shell takes ONE uv space (the first
            // kept part's, below) and maps every part's art with it, so a part whose space differs here is
            // rendered with the wrong UVs. Logged per part because the fallback paths resolve through
            // Penumbra to whatever body mod owns them, which can differ slot to slot.
            // A shape FINGERPRINT of the skin geometry we are about to cut from. The shell only conforms if
            // this is the same mesh the game draws, and the two ways that fails look identical in game —
            // the body pokes through the fabric either way:
            //   - wrong variant: a body mod ships several chest sizes and ResolvePlayer handed us a
            //     different one than the character renders. A different size is a different SHAPE, so the
            //     vertex count and/or bounds differ from a known-good run of the same option.
            //   - race deformation: the cut mesh is right, but the game deforms host and body differently.
            //     Then these numbers MATCH a known-good run and the fault is in hosting, not geometry.
            // Cheap enough to always emit: the writer parses this same geometry every composite anyway.
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

            log.Information("[Proteus] second skin part {0}: uv={1} {2} ({3} KB) skin={4} <- {5}",
                part, partType ?? "(unknown)", bodyGamePath, bytes.Length / 1024, shape,
                bodyDisk ?? "(game data)");

            // Shape keys enabled on this exact body model (matched by file stem, e.g. c0201e0000_dwn).
            HashSet<string>? partShapes = null;
            enabledBodyShapes?.TryGetValue(Interop.BodyShapeReader.Stem(bodyGamePath), out partShapes);

            bodies.Add((bytes, partShapes));
            modelType ??= partType;
        }

        // ── whole-body fallback ──────────────────────────────────────────────
        // Not every race ships e0000 parts. Viera and Hrothgar have none, so the game resolves those paths
        // through EQDP to another race's model and the direct path never loads. Left alone that silently
        // drops the torso and hands from the shell, leaving a fabric that renders only where some equipped
        // gear model happened to carry a skin mesh — 2 meshes where a Midlander gets 6. Fall back to the
        // race's own body model: it always exists, is by definition the right race, and is what a body mod
        // replaces, so it carries the correct UV space too.
        //
        // It is the WHOLE body and cannot be split per slot, so it REPLACES everything cut above rather
        // than stacking a second shell over skin it already covers (coincident geometry that z-fights and
        // spends the host's mesh budget twice). The cost is the gear-posed parts — a heel's tiptoed foot —
        // and one consistent shell is the better trade. Decided here rather than mid-loop so the result
        // can't depend on which slot happened to fail first.
        //
        // Trigger: EVERY bare-body slot attempted was missing, which is what "this race ships no e0000
        // models" actually looks like. Firing on any ONE missing slot would mean a single corrupt file on
        // a race that does ship them wipes the gear-posed parts that loaded perfectly well and shells bare
        // skin underneath gear the game is still drawing.
        if (barePartsTried > 0 && barePartsMissing == barePartsTried)
        {
            // b0001 is the standard body, but a few race/gender combos ship b0101, and cutting the shell
            // from the wrong one yields a plausible-looking shell of the wrong shape — worse than failing.
            // Prefer whichever body the player's MOD owns (ResolvePlayer yields a real file only for modded
            // models), since that is the one they are actually wearing; else take the first that exists.
            (byte[] Bytes, string Path, string? Disk)? pick = null;
            foreach (var bodyId in WholeBodyIds)
            {
                var wholePath = $"chara/human/c{charCode}/obj/body/{bodyId}/model/c{charCode}{bodyId}.mdl";
                var wholeDisk = penumbra.ResolvePlayer(wholePath);
                var wholeBytes = textureLoader.LoadRawFile(wholeDisk, wholePath);
                if (wholeBytes == null) continue;
                if (wholeDisk != null) { pick = (wholeBytes, wholePath, wholeDisk); break; }
                pick ??= (wholeBytes, wholePath, wholeDisk);
            }

            if (pick is { } whole)
            {
                var wholeType = SkinBodyType(whole.Bytes);
                if (string.Equals(wholeType, "gen2", StringComparison.OrdinalIgnoreCase) && !anyGen2Allowed)
                {
                    log.Information("[Proteus] second skin: whole-body fallback {0} is vanilla (gen2) — no gear "
                                  + "overlay opted into All bodies, leaving the {1} part(s) cut above as-is",
                                  whole.Path, bodies.Count);
                }
                else
                {
                    // enabledBodyShapes is keyed by the stem of the model the GAME is drawing (e.g.
                    // c0201e0000_dwn). A race with no e0000 models of its own draws ANOTHER race's, so the
                    // whole body's stem (c1801b0001) never appears there and an exact lookup quietly bakes
                    // no morphs at all — the shell would sit off a body with "Remove Hip Dips" enabled,
                    // which is the very thing the shape-key baking exists to prevent. Fall back to the
                    // union of every enabled set: baking a shape key a model doesn't declare is a no-op,
                    // so folding in the face and other stems costs nothing.
                    HashSet<string>? wholeShapes = null;
                    if (enabledBodyShapes != null
                        && !enabledBodyShapes.TryGetValue(Interop.BodyShapeReader.Stem(whole.Path), out wholeShapes))
                    {
                        wholeShapes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var set in enabledBodyShapes.Values) wholeShapes.UnionWith(set);
                        if (wholeShapes.Count == 0) wholeShapes = null;
                    }
                    log.Information("[Proteus] second skin: a bare-body e0000 part was not loadable (usual cause: "
                                  + "c{0} ships no e0000 models and the game resolves them through EQDP) — cutting "
                                  + "the whole shell from {1} instead, replacing {2} part(s) cut above",
                                  charCode, whole.Path, bodies.Count);
                    bodies.Clear();
                    bodies.Add((whole.Bytes, wholeShapes));
                    modelType = wholeType;
                }
            }
            else
            {
                // Silence here once cost a debugging round: the trigger fired, nothing loaded, and the log
                // said nothing at all — leaving a shell short of geometry with no line explaining why.
                log.Information("[Proteus] second skin: every bare-body e0000 part was missing and no whole-body "
                              + "model loaded for c{0} either (tried {1}) — the shell keeps only the {2} part(s) "
                              + "cut from equipped gear", charCode, string.Join(", ", WholeBodyIds), bodies.Count);
            }
        }

        if (bodies.Count == 0)
        {
            log.Warning("[Proteus] second skin: no skin models resolved for c{0} (or all parts gated out)", charCode);
            return null;
        }

        if (modelType != null && !string.Equals(modelType, bodyType, StringComparison.OrdinalIgnoreCase))
        {
            log.Information("[Proteus] second skin: body UV is {0} per the model's material (was {1})",
                modelType, bodyType ?? "unknown");
            bodyType = modelType;
        }

        // Accessories the shell can spill across, in fill priority (glasses -> rings -> bracelet -> necklace
        // -> Emperor fallback). Each holds MaxMaterials - BaseMatCount layers; layers are distributed across
        // them so a big look can span several items. An already-equipped host APPENDS; the Emperor REPLACES.
        var hosts = ChooseHosts(modelCode, equippedAccessories, metModels, invisibleGlassesSet, outputRoot);
        // Cap total placeable layers at the single-char base-36 disk-id space (0-9a-z = 36). Any excess
        // folds into the over-budget drop path below, so a disk id can never run past 'z' into filesystem-
        // reserved chars. 36 is far beyond the practical geometric limit (~15 stacked shells).
        int totalCapacity = Math.Min(hosts.Sum(h => SecondSkinWriter.MaxMaterials - h.BaseMatCount), DiskIdSpace);

        // Only a shell whose bytes actually differ from what's on disk needs a full redraw.
        bool shellChanged = false;

        // Layers assigned to each host, filled in order. Two letters per layer: the in-model MATERIAL INDEX
        // (host base + position within that host) so appended names don't collide with the host's own, and a
        // globally-unique DISK letter so two hosts never overwrite the same ss_<letter> file on disk (the
        // ghost/highlighter also parse that single letter — see ShellNormalGhost).
        var perHostLayers = new List<SecondSkinLayer>[hosts.Count];
        for (int h = 0; h < hosts.Count; h++) perHostLayers[h] = new List<SecondSkinLayer>();

        int diskLetter = 0;
        int maskLayers = 0, clothLayers = 0;    // successfully placed
        int overBudget = 0, overBudgetMask = 0; // real layers that ran out of accessory capacity

        // ── Layer → host distribution ──────────────────────────────────────────
        // Layers arrive bottom-first with the mask LAST (it must render on top). Accessory hosts draw in the
        // order ChooseHosts returns them, the FIRST drawing IN FRONT. So the TOP layers (including the mask)
        // fill the first host and lower layers spill to the hosts behind it — otherwise the mask, being last,
        // would spill onto the rearmost host (e.g. the Emperor fallback ring) and render BEHIND the fabric.
        // Within a host the layers stay in stack order so the topmost gets the highest material index (= drawn
        // last = on top). If the look exceeds total capacity the BOTTOM layers drop, never the mask. A look
        // that fits on ONE host is unchanged (same order as before).
        int layerCount = gearOverlays.Count;
        int placeable   = Math.Min(layerCount, totalCapacity);
        int dropCount   = layerCount - placeable;   // bottom layers with no room

        var work = new List<(int LayerIdx, int HostIdx)>(placeable);
        int cursor = layerCount - 1;                  // the TOP layer (mask)
        for (int h = 0; h < hosts.Count && cursor >= dropCount; h++)
        {
            int cap  = SecondSkinWriter.MaxMaterials - hosts[h].BaseMatCount;
            int take = Math.Min(cap, cursor - dropCount + 1);
            for (int k = cursor - take + 1; k <= cursor; k++)   // ascending → topmost lands last (highest idx)
                work.Add((k, h));
            cursor -= take;
        }
        for (int k = 0; k < dropCount; k++)          // the dropped bottom layers = over budget
        {
            overBudget++;
            if (gearOverlays[k].Overlay.Descriptor.IsMaskShell) overBudgetMask++;
        }

        // ── Sibling-relief pre-pass ──────────────────────────────────────────────
        // Each cloth overlay keeps its own shell, but two opaque shells at the same body position OCCLUDE
        // rather than blend — so a ribbing/relief hidden behind a sibling fabric never shows. Fix: additively
        // compound every overlay's normal into its SAME-MOD sibling shells, gated by that overlay's own
        // coverage (baked into the normal's alpha lane so CompoundNormal's src-alpha gate masks it). Whichever
        // shell wins the depth test then carries the combined relief. Only R/G is written, so blue (each
        // shell's own coverage gate) is untouched — the diffuse and index are never affected.
        //
        // Coverage (BuildAlpha) is computed here ONCE per non-mask overlay and reused as the shell's own alpha
        // below, so it isn't computed — or logged — twice.
        byte[]?[] alphaByLayer = new byte[gearOverlays.Count][];
        var reliefContribs = new List<(string ModDir, int LayerIdx, byte[] Normal)>();
        for (int i = 0; i < gearOverlays.Count; i++)
        {
            var (rEntry, rOv) = gearOverlays[i];
            var rd = rOv.Descriptor;
            if (rd.IsMaskShell) continue;   // mask coverage/relief is handled by BuildMaskCoverage
            var rSrc = rd.SourceBodyType ?? InferOverlayBodyType(rd);
            var rAlpha = BuildAlpha(rd, rEntry, rSrc, bodyType, TexSize, TexSize, MaskAdds(rEntry, rOv));
            alphaByLayer[i] = rAlpha;
            if (rd.Normal == null || rAlpha == null) continue;
            var rNormal = LoadRemapped(rd.Normal, rEntry.SidecarRoot, rSrc, bodyType, TexSize, TexSize);
            if (rNormal == null) continue;
            rNormal = (byte[])rNormal.Clone();   // LoadRemapped may hand back a shared cached buffer
            int nn = Math.Min(rAlpha.Length, rNormal.Length / 4);
            for (int p = 0; p < nn; p++) rNormal[p * 4 + 3] = rAlpha[p];   // coverage → alpha lane (the gate)
            reliefContribs.Add((rEntry.ModDirectory, i, rNormal));
        }

        // A toe cap belongs to the FOOT, not to the mod that happens to ship the map. One mod paints it
        // and every shell over those toes is rebuilt with it — otherwise a wardrobe of stockings needs the
        // same map copied into each one, and any shell missing it sleeves the toes while its neighbour
        // caps them. The map is remapped into the body's UV using its own mod's source type, so it is
        // shared as body-UV pixels that any shell can use.
        byte[]? sharedToeCap = null;
        var capCandidates = (allEntries ?? gearOverlays.Select(g => g.Entry).ToList())
            .GroupBy(e => e.ModDirectory, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());
        foreach (var tEntry in capCandidates)
        {
            var tPath = discovery.ResolveActiveToeCap(tEntry);
            if (tPath == null) continue;

            // Remapped with the OWNING mod's UV space; after that it is body-UV pixels anyone can use.
            var tDesc = gearOverlays.FirstOrDefault(g =>
                string.Equals(g.Entry.ModDirectory, tEntry.ModDirectory, StringComparison.OrdinalIgnoreCase)).Overlay?.Descriptor;
            var tSrc = tDesc != null ? tDesc.SourceBodyType ?? InferOverlayBodyType(tDesc) : bodyType;
            sharedToeCap = ReadToeCap(tPath, tSrc, bodyType);
            if (sharedToeCap != null)
            {
                log.Information("[Proteus] second skin: toe cap {0} from \"{1}\" applies to every shell over the toes",
                    Path.GetFileName(tPath), tEntry.ModDirectory);
                break;
            }
        }

        var inHost = new int[hosts.Count];
        foreach (var (i, hIdx) in work)
        {
            var (entry, ov) = gearOverlays[i];
            bool isMaskShell = ov.Descriptor.IsMaskShell;
            var host = hosts[hIdx];

            string shader = ov.Descriptor.ShaderPackage;
            char matLetter = (char)('a' + host.BaseMatCount + inHost[hIdx]);   // in-model material index (per-host, <= 'j')
            char diskChar  = DiskId(diskLetter);                               // globally-unique disk id (base-36, 0-9a-z)
            // Materials live INSIDE the host's own model, so name them with the code that model is loaded
            // under — the equipped host's real resolved path, or charCode for the Emperor rebuild (see
            // mdlGamePath below). On an append host this also keeps our added letters matching the base's
            // own material names instead of mixing two codes inside one model.
            var hostCode = host.ModelPath != null ? PathCharCode(host.ModelPath) ?? modelCode : charCode;
            string matName = $"mt_c{hostCode}{host.Prefix}{host.SetId:D4}_{host.Slot}_{matLetter}.mtrl";
            string matGamePath = $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/material/v0001/{matName}";
            string texPrefix   = $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/texture/ss_{diskChar}_";

            // Which UV space is this art painted in? A mod listing only *_bibo.mtrl is bibo art; the gear
            // layer has no material-match gate like the skin layer, so remap into the body's UV explicitly.
            var srcType = ov.Descriptor.SourceBodyType ?? InferOverlayBodyType(ov.Descriptor);
            log.Information("[Proteus] gear layer mat={0}/disk={1} -> host {2}{3:D4}/{4}: shader={5} UV {6}->{7}{8}{9}",
                matLetter, diskChar, host.Prefix, host.SetId, host.Slot, shader, srcType ?? "(unknown)", bodyType ?? "(unknown)",
                srcType != null && bodyType != null && !string.Equals(srcType, bodyType, StringComparison.OrdinalIgnoreCase) ? " [REMAP]" : "",
                isMaskShell ? " [MASK SHELL]" : "");

            // The mask shell's coverage IS the mask; other shells' coverage is the overlay's art shaped by masks.
            bool mergeMasks = isMaskShell || !(maskShellMods?.Contains(entry.ModDirectory) ?? false);
            var alpha = isMaskShell
                ? BuildMaskCoverage(entry, srcType, bodyType, TexSize, TexSize)
                : alphaByLayer[i];   // computed once in the sibling-relief pre-pass above

            // Error-drops (below) don't consume a host slot — inHost/diskLetter only advance on a full success.
            // A null coverage means the art failed to load or the overlay is empty (BuildAlpha logged why).
            // Drop the shell rather than render it fully opaque — a fabric with no coverage gate covers the
            // WHOLE body and the masks never carve it (this masked a diffuse.dds/.png extension mismatch).
            if (alpha == null) continue;
            var coverage = Downsample(alpha, TexSize, TexSize, CoverageSize);

            // Same-mod siblings' relief compounds into this fabric shell (never into a mask shell — its normal
            // IS the mask relief). Self is excluded so a shell doesn't double-stamp its own normal.
            var siblingReliefs = isMaskShell
                ? null
                : reliefContribs.Where(c => c.LayerIdx != i &&
                        string.Equals(c.ModDir, entry.ModDirectory, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Normal).ToList();

            var texPaths = WriteTextures(entry, ov.Descriptor, shader, texPrefix, texturesDir, redirects, diskChar,
                alpha, srcType, bodyType, ov.ColorTableRows, effectsFolder, ref shellChanged, mergeMasks, siblingReliefs);
            if (texPaths == null) continue;

            var template = textureLoader.LoadRawMtrl(null, GearMaterialWriter.TemplateFor(shader));
            if (template == null) { log.Error("[Proteus] second skin: missing template material for {0}", shader); continue; }

            var scroll = new ScrollSettings(
                ov.Descriptor.ScrollSpeedX ?? ScrollSettings.Default.SpeedX,
                ov.Descriptor.ScrollSpeedY ?? ScrollSettings.Default.SpeedY,
                ov.Descriptor.ScrollTilingX ?? ScrollSettings.Default.TilingX,
                ov.Descriptor.ScrollTilingY ?? ScrollSettings.Default.TilingY);

            byte[] mtrl;
            // A mask shell's colour lives in the colorset over a WHITE base (no diffuse of its own), so the
            // colorset diffuse must be linearised to render at the authored (sRGB) value — matching the skin
            // bake. Fabric shells carry colour in their base texture with a white colorset, so they don't.
            try { mtrl = GearMaterialWriter.Build(template, texPaths, BuildRows(ov.ColorTableRows), scroll, config.GearCutoutAlpha, linearizeDiffuse: isMaskShell); }
            catch (Exception ex) { log.Error(ex, "[Proteus] second skin: material build failed for {0}", shader); continue; }

            var matDisk = Path.Combine(materialsDir, $"ss_{diskChar}.mtrl");
            shellChanged |= WriteIfChanged(matDisk, mtrl);
            redirects[matGamePath] = Rel(outputRoot, matDisk);
            var shellKey = (entry.ModDirectory, ov.OptionGroup, ov.Option);
            if (!shellMaterials.TryGetValue(shellKey, out var shellList))
                shellMaterials[shellKey] = shellList = new List<string>();
            shellList.Add($"ss_{diskChar}.mtrl");

            // A shell follows every body contour, so hosiery sleeves each toe unless the toe area is
            // marked — then the writer cuts that region out and rebuilds it as one rounded cap.
            var toeCap = ToeCapFor(ov.Descriptor, entry, srcType, bodyType, sharedToeCap, alpha);
            perHostLayers[hIdx].Add(new SecondSkinLayer
            {
                MaterialName = "/" + matName,   // the model stores material names with a leading slash
                Coverage = coverage,
                CoverageWidth = coverage == null ? 0 : CoverageSize,
                CoverageHeight = coverage == null ? 0 : CoverageSize,
                ToeCap = toeCap,
                ToeCapWidth = toeCap == null ? 0 : ToeCapSize,
                ToeCapHeight = toeCap == null ? 0 : ToeCapSize,
                ToeCapStrength = Math.Clamp(ov.Descriptor.ToeCapStrength ?? 1f, 0f, 1f),
            });
            inHost[hIdx]++; diskLetter++;       // slot consumed
            if (isMaskShell) maskLayers++; else clothLayers++;
        }

        int placed = maskLayers + clothLayers;
        if (placed == 0) return null;

        // Guidance when even all equipped accessories can't hold the look (deduped by total layer count).
        if (overBudget > 0)
        {
            int totalLayers = placed + overBudget;
            int totalMask = maskLayers + overBudgetMask;
            if (_lastOverBudgetLayers != totalLayers)
            {
                _lastOverBudgetLayers = totalLayers;
                var msg =
                    $"[Proteus] This look has {totalLayers} layers ({totalMask} Mask, {totalLayers - totalMask} Cloth), "
                  + $"but only {totalCapacity} fit across your accessories (Proteus's invisible fallback ring already "
                  + $"included). Turn off some layers, or equip another pair of glasses / ring / bracelet / necklace so "
                  + $"the rest fit.";
                Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 25).Build());   // 25 = yellow
            }
            log.Warning("[Proteus] second skin: {0} layers exceed total accessory capacity {1} — {2} dropped",
                placed + overBudget, totalCapacity, overBudget);
        }
        else _lastOverBudgetLayers = -1;

        // Build one shell model per host that got layers; fold each into the single Result.
        bool skipConnectors = config.HideConnectorMeshes == ConnectorMeshMode.Neolithe;
        var bodyBytes  = bodies.Select(b => b.Bytes).ToList();
        var bodyShapes = bodies.Select(b => b.Shapes).ToList();
        bool modelChangedAny = false;
        var hostModelPaths = new List<string>();
        for (int h = 0; h < hosts.Count; h++)
        {
            if (perHostLayers[h].Count == 0) continue;
            var host = hosts[h];

            byte[] shell;
            SecondSkinWriter.Stats stats;
            DumpShellInputs(h, bodyBytes, perHostLayers[h], host.BaseModel, skipConnectors, bodyShapes);
            try
            {
                shell = SecondSkinWriter.Build(bodyBytes, perHostLayers[h], host.BaseModel, skipConnectors,
                    out stats, bodyShapes, msg => log.Debug("[Proteus] second skin: {0}", msg));
            }
            catch (Exception ex)
            {
                log.Error(ex, "[Proteus] second skin: model build failed for host {0}{1:D4}/{2}", host.Prefix, host.SetId, host.Slot);
                continue;   // this host fails; the others still build
            }

            // Redirect the path the game ACTUALLY loads (host.ModelPath) for an equipped host. The Emperor
            // fallback has no resolved path to copy (ModelPath null), so its path is rebuilt here — from
            // charCode, matching the EQDP entry written below, which is keyed to the CHARACTER's race
            // (EqdpManipulation parses charCode) and declares that race to have its own model for the slot.
            //
            // charCode is also what the game actually asks for, confirmed in a live log: with charCode
            // c1801 and modelCode c0201 it requested chara/accessory/a0053/model/c1801a0053_rir.mdl and
            // got our shell. So EQDP wins over the model-race chain here, and ONE published path is right.
            // An earlier version hedged by publishing at c{modelCode} too; that alias returned as an
            // equipped accessory next composite and poisoned the model-race vote above. Don't re-add it.
            //
            // The open consequence is fit, not lookup: hosted at c{charCode} the game treats the model as
            // native and does NOT race-deform it, while the shell is cut from c{modelCode} gear that IS
            // deformed, so this host can render at the wrong proportions. Equipped hosts avoid that (they
            // keep their real, deformed path), which is why the Emperor is last in fill order.
            var mdlGamePath = host.ModelPath
                ?? $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/model/c{charCode}{host.Prefix}{host.SetId:D4}_{host.Slot}.mdl";
            var mdlDisk = Path.Combine(modelsDir, $"secondskin_{h}.mdl");
            var modelChanged = WriteIfChanged(mdlDisk, shell);
            shellChanged   |= modelChanged;
            modelChangedAny |= modelChanged;
            redirects[mdlGamePath] = Rel(outputRoot, mdlDisk);
            hostModelPaths.Add(mdlGamePath);

            // NOTE: we deliberately publish the shell at ONE path only. An earlier version also published
            // it at c{modelCode} to cover either resolution order; that extra path came straight back as an
            // equipped accessory on the next composite and poisoned the model-race vote above. One path,
            // paired with the EQDP entry below, is the only self-consistent choice.

            // Only the invisible Emperor's New Ring needs an EQDP entry to load a model at all; real equipped
            // hosts (rings/bracelet/necklace/worn glasses) and the auto-glasses load natively.
            if (host.BaseModel == null && host.Prefix == 'a')
            {
                manipulations.Add(EqdpManipulation(charCode, host.EqdpSlot));
                log.Information("[Proteus] second skin: EQDP added for {0} (Emperor fallback host)", host.EqdpSlot);
            }
            log.Information("[Proteus] second skin: host {0}{1:D4}/{2} <- {3} layer(s) -> {4} meshes, {5} KB (append={6})",
                host.Prefix, host.SetId, host.Slot, perHostLayers[h].Count, stats.Meshes, shell.Length / 1024, host.BaseModel != null);
        }
        if (hostModelPaths.Count == 0) return null;

        return new Result(redirects, manipulations, shellChanged, shellMaterials, modelChangedAny, hostModelPaths);
    }

    private static string Rel(string root, string full) => Path.GetRelativePath(root, full).Replace('/', '\\');

    /// <summary>
    /// The overlay's coverage, exactly as the skin layer computes it: the art's own alpha, then shaped
    /// by the mod's selected "Masks" options. Returns one byte per texel, or null when nothing bounds it
    /// (in which case the shell covers the whole body).
    ///
    /// Each mask contributes W *= (1-a) and T += gray*a, and the result is baseAlpha*W/255 + T — so a
    /// mask can both carve coverage away and force it on. Mirrors CompositorService.CombinedMaskAt /
    /// ApplyCoverageMask; keep the two in step.
    /// </summary>
    private byte[]? BuildAlpha(
        OverlayDescriptor d, OverlayEntry entry, string? srcType, string? dstType, int w, int h,
        bool maskAdds = true)
    {
        var artPath = d.Diffuse ?? d.Normal ?? d.Mask;
        var masks = discovery.ResolveActiveMasks(entry);
        if (artPath == null && masks.Count == 0)
            return null;   // empty overlay (no art, no masks) — caller drops the shell

        int n = w * h;
        var alpha = new byte[n];

        if (artPath != null)
        {
            var art = LoadRemapped(artPath, entry.SidecarRoot, srcType, dstType, w, h);
            if (art == null)
            {
                log.Warning("[Proteus] gear art failed to load: {0} (mod {1}) — dropping this shell",
                    artPath, entry.ModDirectory);
                return null;
            }
            for (int i = 0; i < n; i++) alpha[i] = art[i * 4 + 3];
        }
        else
        {
            Array.Fill(alpha, (byte)255);
        }

        // combine the selected masks into weight/target, then apply
        byte[]? wArr = null, tArr = null;
        for (int p = masks.Count - 1; p >= 0; p--)
        {
            var m = RemapPath(masks[p], srcType, dstType, w, h);   // masks share the overlay's UV space
            if (m == null) continue;
            if (wArr == null)
            {
                wArr = new byte[n];
                tArr = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    int o = i * 4, a = m[o + 3];
                    int g = (m[o] * 77 + m[o + 1] * 150 + m[o + 2] * 29) >> 8;   // luminance
                    wArr[i] = (byte)(255 - a);
                    tArr[i] = (byte)(g * a / 255);
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    int o = i * 4, a = m[o + 3];
                    int g = (m[o] * 77 + m[o + 1] * 150 + m[o + 2] * 29) >> 8;
                    int inv = 255 - a;
                    tArr![i] = (byte)(tArr[i] * inv / 255 + g * a / 255);
                    wArr[i] = (byte)(wArr[i] * inv / 255);
                }
            }
        }

        if (wArr != null)
            for (int i = 0; i < n; i++)
            {
                if (alpha[i] == 0) continue;                       // no base coverage -> mask has no say
                int v = alpha[i] * wArr[i] / 255 + (maskAdds ? tArr![i] : 0);
                alpha[i] = (byte)(v > 255 ? 255 : v);
            }

        long opaque = 0, clear = 0;
        foreach (var a in alpha) { if (a == 0) clear++; else if (a == 255) opaque++; }
        log.Information(
            "[Proteus] gear coverage: art={0} masks={1} [{2}] -> {3:F1}% clear, {4:F1}% opaque, {5:F1}% partial",
            artPath ?? "none", masks.Count,
            string.Join(", ", masks.Select(Path.GetFileNameWithoutExtension)),
            clear * 100.0 / n, opaque * 100.0 / n, (n - clear - opaque) * 100.0 / n);

        return alpha;
    }

    /// <summary>
    /// Coverage for a dedicated mask shell: the union (max alpha) of the mod's active masks, remapped into
    /// the body's UV space. Unlike <see cref="BuildAlpha"/> — which SHAPES an overlay's coverage by the masks
    /// (absent mask ⇒ overlay stays) — here the mask IS the shape (absent mask ⇒ nothing renders). Returns
    /// null when no mask resolves, in which case the shell would cover the whole body (so callers gate on
    /// there being mask assets first).
    /// </summary>
    private byte[]? BuildMaskCoverage(OverlayEntry entry, string? srcType, string? dstType, int w, int h)
    {
        int n = w * h;
        byte[]? cov = null;
        // Combine TOP-TERRITORY-WINS, matching CombinedMaskAt (which carves the other layers the same way):
        // at each pixel the topmost mask with territory (alpha) there decides the coverage — its grayscale.
        // Process bottom masks first so the top one (assets are highest-priority-first) lands last and
        // overrides. A mask that is BLACK in its territory (a=255, g=0) drives coverage to 0 — a hole — even
        // where a LOWER mask is white. Alpha alone (a union) would instead display the black regions opaque.
        var assets = discovery.ResolveActiveMaskAssets(entry);
        for (int mi = assets.Count - 1; mi >= 0; mi--)
        {
            var m = RemapPath(assets[mi].MaskPath, srcType, dstType, w, h);   // masks share the overlay's UV space
            if (m == null) continue;
            cov ??= new byte[n];
            for (int i = 0; i < n; i++)
            {
                int o = i * 4, a = m[o + 3];
                if (a == 0) continue;                                          // outside this mask's territory
                int g = (m[o] * 77 + m[o + 1] * 150 + m[o + 2] * 29) >> 8;     // luminance
                cov[i] = (byte)(cov[i] * (255 - a) / 255 + g * a / 255);       // territory alpha-over
            }
        }
        return cov;
    }

    /// <summary>
    /// Load this shell's toe-cap map as a single-channel mask in the BODY's UV space (the shell inherits
    /// the body's UVs, so the same remap the art takes applies here). Greyscale, so the red channel is the
    /// value.
    /// <para/>
    /// Two ways to ask for one, checked in this order: the option's own <c>ToeCap</c> path — per-option, so
    /// "Sheer" can be capped and "Fishnet" not — or the reserved <c>Toe Cap</c> entry in the mod's Masks
    /// group, which the wearer toggles like any other mask and which applies to all of that mod's shells.
    /// <para/>
    /// Null — and no cap — when neither asks, strength is zero, the file won't load, or the map is all black.
    /// </summary>
    /// <summary>
    /// Write out exactly what the writer is about to be handed, so the offline harness can rebuild the
    /// same shell instead of approximating its inputs. Enabled by CREATING the folder — it does nothing
    /// until %TEMP%\proteus-shell-dump exists, and there is nothing to turn off afterwards but deleting
    /// it again.
    /// <para/>
    /// This exists because approximating those inputs cost several rounds of chasing defects that the
    /// harness could not reproduce: it was pointed at a different foot model than the one equipped, and
    /// then at one body where the game passes several, no shape keys, and no connector-mesh mode.
    /// </summary>
    private void DumpShellInputs(int host, IReadOnlyList<byte[]> bodies, IReadOnlyList<SecondSkinLayer> layers,
                                 byte[]? baseModel, bool skipConnectors, IReadOnlyList<HashSet<string>>? shapes)
    {
        var dir = Path.Combine(Path.GetTempPath(), "proteus-shell-dump");
        if (!Directory.Exists(dir)) return;
        try
        {
            var pre = Path.Combine(dir, $"host{host}_");
            for (int i = 0; i < bodies.Count; i++) File.WriteAllBytes($"{pre}body{i}.mdl", bodies[i]);
            if (baseModel != null) File.WriteAllBytes($"{pre}base.mdl", baseModel);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"skipConnectors={skipConnectors}");
            sb.AppendLine($"bodies={bodies.Count}");
            // A body with no shape keys enabled contributes a null entry, not an empty set.
            for (int i = 0; i < (shapes?.Count ?? 0); i++)
                sb.AppendLine($"shapes[{i}]={(shapes![i] is { } sk ? string.Join(',', sk) : "")}");
            for (int i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                sb.AppendLine($"layer[{i}] material={l.MaterialName} "
                            + $"coverage={(l.Coverage == null ? "none" : $"{l.CoverageWidth}x{l.CoverageHeight}")} "
                            + $"toeCap={(l.ToeCap == null ? "none" : $"{l.ToeCapWidth}x{l.ToeCapHeight}")} strength={l.ToeCapStrength}");
                if (l.ToeCap != null) File.WriteAllBytes($"{pre}layer{i}_toecap.raw", l.ToeCap);
                if (l.Coverage != null) File.WriteAllBytes($"{pre}layer{i}_coverage.raw", l.Coverage);
            }
            File.WriteAllText($"{pre}inputs.txt", sb.ToString());
            log.Information("[Proteus] second skin: dumped build inputs for host {0} to {1}", host, dir);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] second skin: could not dump build inputs");
        }
    }

    private byte[]? ReadToeCap(string path, string? srcType, string? dstType)
    {
        var rgba = RemapPath(path, srcType, dstType, ToeCapSize, ToeCapSize);
        if (rgba == null)
        {
            log.Warning("[Proteus] second skin: toe cap map {0} failed to load — shells built without a cap", path);
            return null;
        }

        var mask = new byte[ToeCapSize * ToeCapSize];
        bool any = false;
        for (int p = 0; p < mask.Length; p++)
        {
            mask[p] = rgba[p * 4];
            if (mask[p] != 0) any = true;
        }
        return any ? mask : null;   // all black = untouched everywhere; keep the build byte-identical
    }

    /// <summary>
    /// The toe cap for one shell: its option's own map if it names one, otherwise the shared map any mod
    /// in the look supplied. Returned only when this shell's art actually reaches the toes — a cap cuts
    /// the toe box out and rebuilds it, so handing one to a shell that stops at the ankle would carve a
    /// hole in the body and fill it with fabric nobody asked for.
    /// </summary>
    private byte[]? ToeCapFor(OverlayDescriptor d, OverlayEntry entry, string? srcType, string? dstType,
                              byte[]? shared, byte[]? alpha)
    {
        if ((d.ToeCapStrength ?? 1f) <= 0f) return null;

        var mask = d.ToeCap != null
            ? ReadToeCap(Path.Combine(entry.SidecarRoot, d.ToeCap), srcType, dstType)
            : shared;
        if (mask == null || alpha == null) return null;

        // How much of the capped area this shell actually paints, sampling the coverage under the map.
        int over = 0, painted = 0;
        int step = TexSize / ToeCapSize;
        for (int y = 0; y < ToeCapSize; y++)
            for (int x = 0; x < ToeCapSize; x++)
            {
                if (mask[y * ToeCapSize + x] < 128) continue;
                over++;
                if (alpha[(y * step) * TexSize + x * step] >= 32) painted++;
            }
        float share = over == 0 ? 0f : (float)painted / over;
        if (share < MinToeCoverage)
        {
            log.Debug("[Proteus] second skin: shell covers {0:P0} of the toe cap area — below {1:P0}, left uncapped",
                share, MinToeCoverage);
            return null;
        }

        log.Information("[Proteus] second skin: toe cap at strength {0:0.##}, shell covers {1:P0} of it",
            d.ToeCapStrength ?? 1f, share);
        return mask;
    }

    /// <summary>Box-downsample the coverage for triangle trimming; it only decides keep/drop.</summary>
    private static byte[]? Downsample(byte[]? src, int w, int h, int size)
    {
        if (src == null) return null;
        var dst = new byte[size * size];
        int sx = w / size, sy = h / size;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int max = 0;   // keep a texel if ANY source texel under it is visible
                for (int j = 0; j < sy; j++)
                    for (int i = 0; i < sx; i++)
                        max = Math.Max(max, src[(y * sy + j) * w + x * sx + i]);
                dst[y * size + x] = (byte)max;
            }
        return dst;
    }

    /// <summary>
    /// Author the shell's textures. Returns the game paths in the shader's slot order, or null on
    /// failure. The overlay's alpha is written into the NORMAL's BLUE channel — that is what gates
    /// transparency for gear, and therefore what lets stacked shells composite instead of occlude.
    /// </summary>
    private List<string>? WriteTextures(
        OverlayEntry entry, OverlayDescriptor d, string shader, string texPrefix,
        string texturesDir, Dictionary<string, string> redirects, char letter, byte[]? alpha,
        string? srcType, string? dstType, List<ColorTableRowPreset>? rows, string? effectsFolder,
        ref bool texturesChanged, bool mergeMasks = true,
        IReadOnlyList<byte[]>? siblingReliefs = null)   // each: a normal RGBA with coverage in its alpha lane
    {
        var sidecarRoot = entry.SidecarRoot;
        var outputRoot = Directory.GetParent(texturesDir)!.FullName;

        byte[]? Png(string? rel) => LoadRemapped(rel, sidecarRoot, srcType, dstType, TexSize, TexSize);

        var diffuse = Png(d.Diffuse);
        var normal = Png(d.Normal);
        var mask = Png(d.Mask);
        var index = Png(d.Index);

        // The scroll map is NOT body-UV art — it's a tiling pattern the shader samples with uv1, so it
        // must NOT be UV-remapped (that would tear the pattern apart). It also lives in an effects
        // folder, not the sidecar tree, so resolve it separately.
        byte[]? scroll = null;
        if (d.Scroll != null)
        {
            var effectPath = SidecarDiscoveryService.ResolveEffectPath(entry, effectsFolder, d.Scroll);
            if (effectPath != null)
                scroll = textureLoader.LoadPngAsRgba(effectPath, TexSize, TexSize);
            else
                log.Warning("[Proteus] second skin: effect \"{0}\" not found", d.Scroll);
        }

        byte[] Solid(byte r, byte g, byte b, byte a)
        {
            var t = new byte[TexSize * TexSize * 4];
            for (int i = 0; i < t.Length; i += 4) { t[i] = r; t[i + 1] = g; t[i + 2] = b; t[i + 3] = a; }
            return t;
        }

        // ── Proteus "Masks" options ──────────────────────────────────────────
        // A mask isn't only coverage: its export can also ship its OWN row assignment (Masks/<x>_id.png)
        // and relief normal (Masks/<x>_n.dds). The skin layer merges both — LoadIndexMerged and the
        // masks-driven relief pass in CompositorService — so the gear layer must too, or a mask silently
        // loses its rows and its bump. (Coverage itself is already folded in by BuildAlpha.)
        //
        // Skipped when this mod carries a dedicated top mask shell for its OTHER overlays (mergeMasks=false):
        // the mask shell owns the _id/relief, so merging here too would colour the mask twice. The mask
        // shell itself passes mergeMasks=true, so its own _id/relief still land.
        //
        // _id is merged bottom-first (assets are highest-priority-first, so reverse) — each mask overwrites
        // the _id where it is present, so the TOP mask wins on overlap. The RELIEF is folded in afterwards by
        // the shared CombineMaskReliefs (top-first claim), the SAME combine the skin body normal uses.
        var mergeTopFirst = mergeMasks
            ? discovery.ResolveActiveMaskAssets(entry)
            : new List<(string MaskPath, string? NormalPath, string? IndexPath)>();
        foreach (var (maskPath, maskNormalPath, maskIndexPath) in Enumerable.Reverse(mergeTopFirst))
        {
            if (maskIndexPath == null) continue;
            var maskPng = RemapPath(maskPath, srcType, dstType, TexSize, TexSize);
            var maskIdx = RemapPath(maskIndexPath, srcType, dstType, TexSize, TexSize);
            if (maskPng == null || maskIdx == null) continue;
            // LoadPngAsRgba hands back a shared cached array — clone before writing into it.
            index = index != null ? (byte[])index.Clone() : Solid(0, 0, 0, 255);
            for (int i = 0; i < index.Length; i += 4)
            {
                if (maskPng[i + 3] < 128) continue;   // only where the mask is actually present
                index[i]     = maskIdx[i];            // red   → row pair
                index[i + 1] = maskIdx[i + 1];        // green → sub-row
            }
        }

        // Mask relief: same top-first claim-combine as the skin body normal (CombineMaskReliefs), so the two
        // paths can't drift. A higher mask's trim wins over a lower one's; plain fill leaves the base normal.
        var reliefMasks = new List<(byte[] Relief, byte[] Coverage)>();
        foreach (var (maskPath, maskNormalPath, _) in mergeTopFirst)
        {
            if (maskNormalPath == null) continue;
            var maskPng    = RemapPath(maskPath, srcType, dstType, TexSize, TexSize);
            var maskNormal = RemapPath(maskNormalPath, srcType, dstType, TexSize, TexSize);
            if (maskPng != null && maskNormal != null)
                reliefMasks.Add((maskNormal, maskPng));
        }
        if (reliefMasks.Count > 0)
        {
            normal = normal != null ? (byte[])normal.Clone() : Solid(128, 128, 255, 255);
            CompositorService.CombineMaskReliefs(normal, TexSize, TexSize, reliefMasks);
        }

        // Sibling relief: additively fold each same-mod sibling overlay's normal into this shell's normal so a
        // relief hidden behind this fabric (occluded shell) still shows here. ADDITIVE (CompoundNormal), not
        // claim-replace — ribbing bumps stack ON the fabric weave rather than flattening it. Each sibling
        // carries its own coverage in its alpha lane, so CompoundNormal's src-alpha gate lands it only where
        // that sibling is visible. R/G only — blue stays this shell's coverage gate, so it rides this fabric.
        if (siblingReliefs is { Count: > 0 })
        {
            normal = normal != null ? (byte[])normal.Clone() : Solid(128, 128, 255, 255);
            foreach (var sib in siblingReliefs)
                CompositorService.CompoundNormal(normal, sib, TexSize, TexSize);
        }

        // ── row-selector repair ──────────────────────────────────────────────
        // Runs after every mask _id merge, so it sees the final row assignment.
        //
        // An exported _id is antialiased art, but red/17+1 is a discrete row lookup: each edge texel ramps
        // down through rows nobody configured, and the shell's shader resolves those against the TEMPLATE
        // colorset, painting a one-texel template-coloured fringe just inside every edge. The skin layer
        // never showed this because it skips rows with no preset; a shell can't skip — it hands the texture
        // to the GPU. See CompositorService.SnapIndexRowsToDefined.
        //
        // The repair goes to a SEPARATE buffer that only the shader's "id" slot uses. The opacity pass below
        // deliberately keeps reading the unrepaired index: it skips texels whose row has no preset, so
        // repairing them first would newly apply the row's Opacity across the whole antialiased band and
        // push every edge toward opaque (or transparent), visibly fattening or thinning the garment. The
        // fringe is a shader-side problem; opacity behaviour has no reason to move with it.
        var shaderIndex = index;
        if (index != null && rows is { Count: > 0 })
        {
            // LoadPngAsRgba hands back a shared cached array; the mask merge above clones only when it
            // actually merged, so clone here too rather than writing through to the cache.
            shaderIndex = (byte[])index.Clone();
            CompositorService.SnapIndexRowsToDefined(shaderIndex, TexSize, TexSize, rows.Select(p => p.Row).ToList());
        }

        // ── per-row opacity ──────────────────────────────────────────────────
        // Each color table row carries an Opacity (-100..100), and the index texture says which row a
        // pixel uses — so opacity is per-region, not global. Same blend the skin layer applies
        // (CompositorService.ApplyIndexedOpacity): negative fades toward transparent, positive pushes
        // toward opaque, interpolated between sub-rows A and B by the index's green channel.
        if (alpha != null && index != null && rows is { Count: > 0 })
        {
            alpha = (byte[])alpha.Clone();
            for (int i = 0; i < alpha.Length; i++)
            {
                float a = alpha[i] / 255f;
                if (a <= 0f) continue;

                int pair = index[i * 4] / 17 + 1;                       // red → 1-based row pair
                var preset = rows.FirstOrDefault(p => p.Row == pair);
                if (preset == null) continue;

                float blendA = index[i * 4 + 1] / 255f;                 // green → sub-row A weight
                float opA = preset.SubRowA?.Opacity ?? 0;
                float opB = preset.SubRowB?.Opacity ?? 0;
                float op = opB + (opA - opB) * blendA;
                if (op == 0f) continue;

                float newA = op < 0f ? a * (100f + op) / 100f : a + (1f - a) * op / 100f;
                alpha[i] = (byte)(Math.Clamp(newA, 0f, 1f) * 255f + 0.5f);
            }
        }

        // norm: RG = the normal itself, B = TRANSPARENCY (the gear alpha gate), A = unused.
        // This is the whole trick: a Proteus overlay is gated by opacity, and on the gear layer that
        // opacity has to be translated into the normal map's BLUE channel or the shell renders solid.
        // (It also needs the material's transparency flag on — see GearMaterialWriter.)
        var norm = normal != null ? (byte[])normal.Clone() : Solid(128, 128, 255, 255);
        for (int i = 0; i < TexSize * TexSize; i++)
            norm[i * 4 + 2] = alpha?[i] ?? 255;   // blue is the gate; alpha is not used

        var slots = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["norm"] = norm,
            // A fabricated mask must be WHITE, not mid-grey. The gear shaders read occlusion/gloss out
            // of it, so a 50% grey mask halves the lighting everywhere and a white surface renders grey.
            ["mask"] = mask ?? Solid(255, 255, 255, 255),
            // No index texture → select Row 16 sub-row A everywhere, matching the SKIN layer's fallback
            // (it applies row16A as a flat tint when desc.Index == null). red 255 → row pair 16, green 255
            // → sub-row A. Defaulting to black (row 1) instead picked up the template's default row — which
            // renders the shell a flat red — and ignored the Row 16 tint the overlay actually carries.
            ["id"]   = shaderIndex ?? Solid(255, 255, 0, 255),

            ["base"] = diffuse ?? Solid(255, 255, 255, 255),  // tint also comes from the color table
            ["catc"] = scroll ?? Solid(0, 0, 0, 255),         // black = no glow
        };

        var order = GearMaterialWriter.TextureOrder(shader);
        var paths = new List<string>(order.Count);
        bool compress = config.EnableCompression;
        foreach (var slot in order)
        {
            var gamePath = texPrefix + slot + ".tex";
            var disk = Path.Combine(texturesDir, $"ss_{letter}_{slot}.tex");

            // Compression (opt-in). The "id" (index) slot is NEVER compressed: its red/green encode discrete
            // colour-table row selectors (red / 17 + 1), and any lossy error crosses a bucket boundary and
            // picks the wrong row (wrong colour/glow, seams). The normal's BLUE channel is the gear
            // transparency gate (see WriteTextures above), which BC5 (2-channel) drops — so it only uses BC5
            // when its blue is uniformly opaque (255 ⇒ nothing to lose), else BC7 preserves the gate.
            // Everything else (base/mask/catc) is continuous → BC7.
            var encoding = TexEncoding.Uncompressed;
            if (compress && !string.Equals(slot, "id", StringComparison.OrdinalIgnoreCase))
                encoding = string.Equals(slot, "norm", StringComparison.OrdinalIgnoreCase)
                    ? (IsBlueAllWhite(slots[slot]) ? TexEncoding.Bc5 : TexEncoding.Bc7)
                    : TexEncoding.Bc7;

            // Skip the write when the content AND its encoding match what we last wrote — otherwise every
            // recomposite would look like a change and force a redraw. The encoding is folded into the hash
            // so toggling compression forces a rewrite instead of a stale skip.
            var hash = Hash(slots[slot]) ^ ((ulong)((int)encoding + 1) * 0x9E3779B97F4A7C15ul);
            bool same = _texHashes.TryGetValue(disk, out var prev) && prev == hash && File.Exists(disk);
            if (!same)
            {
                if (!textureLoader.WriteTex(slots[slot], TexSize, TexSize, disk, encoding))
                {
                    log.Error("[Proteus] second skin: failed to write {0}", disk);
                    return null;
                }
                _texHashes[disk] = hash;
                texturesChanged = true;
            }

            redirects[gamePath] = Rel(outputRoot, disk);
            paths.Add(gamePath);
        }
        return paths;
    }

    /// <summary>Map the metadata's 1-based row/sub-row presets onto 0-based color table rows.</summary>
    private static Dictionary<int, GearColorRow>? BuildRows(List<ColorTableRowPreset>? presets)
    {
        if (presets == null || presets.Count == 0) return null;
        var rows = new Dictionary<int, GearColorRow>();

        // Initialize EVERY color-table row to neutral white first, so no row keeps the gear template's default
        // (often dark) colour. The index can select ANY pair — one the colorset never defined, or the
        // undefined sub-row of a pair that set only the other — and it must show the base diffuse there
        // (base × white = base), exactly as the skin layer defaults its rows to white. The presets below
        // overwrite the rows they define. (16 colour-table pairs = 32 sub-rows.)
        for (int r = 0; r < 32; r++)
            rows[r] = new GearColorRow { Diffuse = (1f, 1f, 1f), Emissive = (0f, 0f, 0f) };

        foreach (var p in presets)
        {
            if (p.Row is < 1 or > 16) continue;
            Add((p.Row - 1) * 2, p.SubRowA);
            Add((p.Row - 1) * 2 + 1, p.SubRowB);
        }
        return rows;

        void Add(int rowIndex, ColorTableSubRowPreset? sub)
        {
            if (sub == null) return;   // leaves the neutral-white row from the init above
            var rgb = ParseHex(sub.Diffuse);
            // Glow colour is INDEPENDENT of the diffuse (a scrolling material wants a near-black diffuse
            // with a white emissive), falling back to the diffuse when not given.
            var emis = ParseHex(sub.EmissiveColor) ?? rgb;
            rows[rowIndex] = new GearColorRow
            {
                Diffuse = rgb,
                Specular = ParseHex(sub.Specular),
                // Always write emissive — a template's own emissive must be CLEARED, not inherited.
                // Vanilla characterscroll rows carry a warm non-zero emissive that renders as a flat
                // white glow and drowns out the scroll map entirely.
                Emissive = sub.Emissive > 0f && emis is { } c
                    ? (c.R * sub.Emissive, c.G * sub.Emissive, c.B * sub.Emissive)
                    : (0f, 0f, 0f),
                SphereMapIndex = sub.SphereMap,
                SphereMapMask = sub.SphereIntensity,
                Roughness = sub.Roughness,
                Metalness = sub.Metalness,
            };
        }
    }

    private static (float R, float G, float B)? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return (((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
    }

    // ── EQDP ─────────────────────────────────────────────────────────────────

    private static readonly string[] RaceNames =
    [
        "Midlander", "Highlander", "Elezen", "Miqote", "Roegadyn",
        "Lalafell", "AuRa", "Hrothgar", "Viera",
    ];

    /// <summary>
    /// The accessory a second skin rides on. For an already-equipped ring/bracelet <see cref="BaseModel"/>
    /// holds its model bytes (the shell is appended into it) and <see cref="BaseMatCount"/> its material
    /// count; for the Emperor's New Ring fallback both are 0/null and an EQDP edit forces the model.
    /// </summary>
    // ModelPath is the ACTUAL loaded game path for an equipped host (rings/bracelet/worn or injected
    // glasses) — used as the redirect key so we match the char code the game really requested. Invisible
    // items (Emperor accessories, invisible glasses) have no model for many races and load a fallback race
    // (e.g. c0101 male on a female), so a path rebuilt from the player's char code would miss. Null for the
    // Emperor fallback, whose EQDP edit forces the player-race model, making the rebuilt path correct.
    private readonly record struct HostAccessory(
        int SetId, string Slot, string EqdpSlot, byte[]? BaseModel, int BaseMatCount, string Tree, char Prefix,
        string? ModelPath = null);

    /// <summary>
    /// Pick the model to host the shell. Prefer equipped glasses (the head "_met" model), then a ring the
    /// player already wears (the one with the FEWEST materials, right ring winning a tie), then a bracelet,
    /// else the invisible Emperor's New Ring. A candidate is skipped — with a warning to chat AND log —
    /// when it has no room to append this many overlays: a model carries at most
    /// <see cref="SecondSkinWriter.MaxMaterials"/> materials, so the host's own count plus the layer count
    /// must not exceed it. <paramref name="glassesModel"/> is the head "_met" model path (glasses or a real
    /// helmet), captured separately because it lives in the equipment tree, not the accessory tree.
    /// </summary>
    /// <summary>
    /// All accessories the shell can spill across, in FILL priority: glasses/head, then rings, bracelet,
    /// necklace, and finally the invisible Emperor's New Ring (replace + EQDP). Each carries its base material
    /// count; a host holds up to <c>MaxMaterials - BaseMatCount</c> layers. The layers are distributed across
    /// this list in order (see Build), so more than one accessory's worth of materials can host a big look.
    /// </summary>
    private List<HostAccessory> ChooseHosts(string modelCode, IReadOnlyDictionary<string, string>? equipped,
        IReadOnlyList<string>? metModels, int? invisibleGlassesSet, string outputRoot)
    {
        log.Information("[Proteus] host: choosing from equipped accessories [{0}], head/glasses [{1}]",
            equipped == null ? "(null)" : string.Join(", ", equipped.Select(kv => $"{kv.Key}={kv.Value}")),
            metModels == null || metModels.Count == 0 ? "(none)" : string.Join(", ", metModels));

        // Load a candidate host model: resolve through Penumbra, read its bytes, and count its materials.
        // Returns null (having warned) when the path is unparseable, unloadable, or its material table
        // won't parse — a host we can't understand must be SKIPPED, never guessed at, because an
        // understated material count makes the appended material letters collide with the base's own.
        (int SetId, byte[] Bytes, int Mats)? LoadCandidate(string slot, string gamePath, char prefix)
        {
            if (ParseSetId(gamePath, prefix) is not int setId)
            {
                log.Warning("[Proteus] host: {0} — cannot parse a '{1}' set id from {2}, skipping", slot, prefix, gamePath);
                return null;
            }

            // The shell is cut from equipment models, so it may only be redirected onto a host the game
            // loads under the SAME model code. An invisible item with no model at that code loads a
            // different one instead (e.g. c0101 male under a c0201 female); the game then applies
            // race-conversion deformation to whatever sits at that path, which shrinks and warps our shell
            // — it ends up inside the skin with only its edges poking through, correctly shaped and
            // animated but visibly the wrong size. Skip it and let the next candidate host instead.
            //
            // Compared against modelCode, NOT the character's race: a Viera wears c0201 equipment by
            // design, and the shell is cut from those same c0201 models, so a c0201 host matches it
            // exactly. Comparing against c1801 rejected every accessory the character actually had and
            // dropped the whole look onto the Emperor fallback alone.
            if (PathCharCode(gamePath) is { } pathCc
                && !string.Equals(pathCc, modelCode, StringComparison.OrdinalIgnoreCase))
            {
                log.Information(
                    "[Proteus] host: {0} ({1}{2:D4}) loads as c{3}, not the shell's c{4} — the game would "
                  + "race-deform the shell, skipping", slot, prefix, setId, pathCc, modelCode);
                return null;
            }

            // A host's model path is one WE redirect to the shell, so Penumbra can resolve it straight back
            // to our own previous output. Taking that as the "base" is a feedback loop: on the append path
            // it would merge the shell into the shell again every composite, doubling the model each run.
            // The composite clears redirects and reloads before getting here, but that's async and races —
            // observed in the wild resolving to an 875 KB "glasses" model (our 854 KB shell). Ignore any
            // resolved file inside our own mod directory and read the game's original instead.
            var disk = penumbra.ResolvePlayer(gamePath);
            if (disk != null && IsInsideOutputRoot(disk, outputRoot))
            {
                log.Debug("[Proteus] host: {0} resolved to our own output ({1}) — reading the game's original instead", slot, disk);
                disk = null;
            }

            var bytes = textureLoader.LoadRawFile(disk, gamePath);
            if (bytes == null)
            {
                log.Warning("[Proteus] host: {0} ({1}{2:D4}) model {3} not loadable (disk={4}) — skipping", slot, prefix, setId, gamePath, disk ?? "(null)");
                return null;
            }

            try { return (setId, bytes, SecondSkinWriter.MaterialNames(bytes).Count); }
            catch (Exception ex)
            {
                log.Warning(ex, "[Proteus] host: {0} ({1}{2:D4}) material parse failed — skipping", slot, prefix, setId);
                return null;
            }
        }

        // True when the model has no real geometry to append onto — an invisible item ("The Emperor's
        // New …"-style empty frames). A shell merged into one of those never renders, so it must be
        // REPLACED with a standalone shell instead.
        bool IsDegenerate(byte[] bytes) => bytes.Length < DegenerateModelBytes;

        // Load an equipped model (accessory or head-equipment glasses) as a host candidate, or null if absent,
        // unloadable, or already FULL (its own materials leave no room to append even one layer). Tree is
        // "accessory" (prefix a — rings/bracelet/necklace) or "equipment" (prefix e — glasses/head); the
        // shell's redirect + material game-paths are built from these so both trees resolve correctly.
        HostAccessory? ConsiderPath(string slot, string? gamePath, string tree, char prefix, string eqdpSlot)
        {
            if (gamePath == null)
            {
                log.Information("[Proteus] host: {0} — none equipped", slot);
                return null;
            }

            // The Emperor's New Ring is invisible and only loads a model via our own EQDP edit — it is the
            // FALLBACK, never an append host. Appending to it skips that EQDP, so its model never loads and
            // nothing renders. Skip it here so it drops through to the replace+EQDP path below. (Only the
            // accessory tree has an Emperor set; the equipment/glasses tree never does.)
            if (prefix == 'a' && ParseSetId(gamePath, prefix) == EmperorSetId)
            {
                log.Information("[Proteus] host: {0} is the Emperor's ring (a{1:D4}) — reserved for fallback, skipping", slot, EmperorSetId);
                return null;
            }

            if (LoadCandidate(slot, gamePath, prefix) is not { } c) return null;

            if (c.Mats >= SecondSkinWriter.MaxMaterials)
            {
                log.Debug("[Proteus] host: {0} ({1}{2:D4}) already carries {3}/{4} materials — no room to append, skipping",
                    slot, prefix, c.SetId, c.Mats, SecondSkinWriter.MaxMaterials);
                return null;
            }
            log.Information("[Proteus] host: {0} ({1}{2:D4}) candidate — {3} base material(s), capacity {4}",
                slot, prefix, c.SetId, c.Mats, SecondSkinWriter.MaxMaterials - c.Mats);
            return new HostAccessory(c.SetId, slot, eqdpSlot, c.Bytes, c.Mats, tree, prefix, gamePath);
        }

        // Accessory host (ring/bracelet): look the slot up in the equipped-accessory map.
        HostAccessory? Consider(string slot, string eqdpSlot)
            => ConsiderPath(slot,
                equipped != null && equipped.TryGetValue(slot, out var gp) ? gp : null,
                "accessory", 'a', eqdpSlot);

        var hosts = new List<HostAccessory>();

        // 1. Facewear (glasses) / head "_met" model — filled FIRST so rings stay free. Head equipment and the
        // facewear bonus slot BOTH render through "_met", so several candidates can be loaded at once (a helmet
        // AND glasses); they arrive pre-sorted so the pick is deterministic, and we prefer the pair we injected
        // ourselves. Take the first usable candidate as the single head host:
        //  • A DEGENERATE base (invisible item — empty frames) or OUR injected pair is REPLACED with a
        //    standalone shell (redirect the ACTUAL loaded path; no EQDP — really equipped).
        //  • A REAL model (worn glasses with frames) is APPENDED so its frames stay visible beside the shell.
        foreach (var metPath in OrderMetCandidates(metModels, invisibleGlassesSet))
        {
            if (LoadCandidate("met", metPath, 'e') is not { } c) continue;

            bool ours = invisibleGlassesSet is int inv && inv == c.SetId;
            if (ours || IsDegenerate(c.Bytes))
            {
                log.Information("[Proteus] host: glasses/head e{0:D4} (met, REPLACE — {1}, base {2} B)",
                    c.SetId, ours ? "our injected pair" : "degenerate base", c.Bytes.Length);
                hosts.Add(new HostAccessory(c.SetId, "met", "Head", null, 0, "equipment", 'e', metPath));
                break;
            }
            if (c.Mats < SecondSkinWriter.MaxMaterials)
            {
                log.Information("[Proteus] host: glasses/head e{0:D4} (met, append — {1} material(s), {2} B)",
                    c.SetId, c.Mats, c.Bytes.Length);
                hosts.Add(new HostAccessory(c.SetId, "met", "Head", c.Bytes, c.Mats, "equipment", 'e', metPath));
                break;
            }
            log.Debug("[Proteus] host: glasses/head e{0:D4} full ({1} materials) — skipping", c.SetId, c.Mats);
        }

        // Nothing occupies the head "_met" slot yet, but the invisible-glasses feature is on — so the
        // compositor is about to equip our pair. Host on it NOW: the injected model only loads after the
        // equip's redraw, and OUR pair always takes the REPLACE path (no base bytes needed), its path fully
        // determined by our set id plus this character.
        if (hosts.Count == 0 && (metModels == null || metModels.Count == 0) && invisibleGlassesSet is int pending)
        {
            var pendingPath = $"chara/equipment/e{pending:D4}/model/c{modelCode}e{pending:D4}_met.mdl";
            log.Information("[Proteus] host: invisible glasses e{0:D4} (met, REPLACE — pending injection)", pending);
            hosts.Add(new HostAccessory(pending, "met", "Head", null, 0, "equipment", 'e', pendingPath));
        }
        // (No invisible-glasses-from-nothing route for a slot we don't fill ourselves: an empty head/facewear
        // slot loads NO model, so there's nothing to redirect.)

        // 2. Rings (right then left), 3. bracelet, 4. necklace — each an append host if worn and not full.
        foreach (var (slot, eqdp) in new[] { ("rir", "RFinger"), ("ril", "LFinger"), ("wrs", "Wrists"), ("nek", "Neck") })
            if (Consider(slot, eqdp) is { } acc) hosts.Add(acc);

        // 5. Fallback last: the invisible Emperor's New Ring (replace + EQDP). Only actually built if the real
        // hosts above overflow (it gets no layers otherwise), so its EQDP is added only when it hosts.
        hosts.Add(new HostAccessory(EmperorSetId, Accessory, EqdpSlot, null, 0, "accessory", 'a'));

        log.Information("[Proteus] host: {0} host(s) in fill order: {1}", hosts.Count,
            string.Join(" -> ", hosts.Select(h => $"{h.Prefix}{h.SetId:D4}/{h.Slot}(cap {SecondSkinWriter.MaxMaterials - h.BaseMatCount})")));
        return hosts;
    }

    /// <summary>The set id from a model path for the given tree prefix, e.g. ("…/a0114/model/…", 'a') → 114
    /// or ("…/equipment/e5524/model/…", 'e') → 5524. Null when the path carries no such id — callers must
    /// SKIP the candidate rather than substitute a default: guessing a set builds redirects for an item the
    /// player isn't wearing, which silently never renders.</summary>
    private static int? ParseSetId(string gamePath, char prefix)
    {
        var m = System.Text.RegularExpressions.Regex.Match(gamePath, $@"/{prefix}(\d+)/");
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>
    /// The race/gender code a model path is loaded under, e.g. "…/model/c0101e0279_met.mdl" → "0101".
    /// Null when the path carries none. This is NOT always the wearer's own code: an item with no model
    /// for their race falls back to another (commonly c0101), and the game race-deforms whatever it finds
    /// there — so a shell built for the wearer must never be redirected onto a foreign-race path.
    /// </summary>
    private static string? PathCharCode(string gamePath)
    {
        var m = System.Text.RegularExpressions.Regex.Match(gamePath, @"/c(\d+)[ae]\d+_");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Head equipment and the facewear/glasses bonus slot both render through "_met", so more than one
    /// candidate can be loaded at once. Order them deterministically — our own injected pair first (its
    /// frames must never show), then by set id — so the chosen host can't flip between composites and
    /// churn a shell rebuild + full redraw.
    /// </summary>
    /// <summary>Is this resolved disk path one of OUR managed-mod files (i.e. our own composite output)?</summary>
    private static bool IsInsideOutputRoot(string diskPath, string outputRoot)
    {
        try
        {
            var full = Path.GetFullPath(diskPath);
            var root = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // unparseable path — treat as external, the old behaviour
    }

    private static IEnumerable<string> OrderMetCandidates(IReadOnlyList<string>? metModels, int? invisibleGlassesSet)
        => metModels == null
            ? []
            : metModels
                .OrderByDescending(p => invisibleGlassesSet is int inv && ParseSetId(p, 'e') == inv)
                .ThenBy(p => ParseSetId(p, 'e') ?? int.MaxValue)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tell the game this accessory has a model for the character's own race/gender. Char codes run
    /// c0101 = Midlander male, c0201 = Midlander female, c0301 = Highlander male, and so on.
    /// </summary>
    private static object EqdpManipulation(string charCode, string slot)
    {
        int n = int.TryParse(charCode.AsSpan(0, 2), out var parsed) ? parsed : 2;
        string race = RaceNames[Math.Clamp((n - 1) / 2, 0, RaceNames.Length - 1)];
        string gender = n % 2 == 1 ? "Male" : "Female";

        return new
        {
            Type = "Eqdp",
            Manipulation = new
            {
                Entry = 192,
                Gender = gender,
                Race = race,
                SetId = EmperorSetId,
                Slot = slot,
                ShiftedEntry = 3,
            },
        };
    }
}
