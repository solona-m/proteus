using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

    /// <summary>
    /// Resolve a game path to the file we should read as a BASE — never our own previous output, and
    /// never past a mod the player actually installed. See CompositorService.ResolveUpstream: it
    /// remembers the file that was behind a path BEFORE our redirect started masking it, which is the
    /// only way an append host that the player has modded keeps its own appearance across composites.
    /// Null (tests, or no upstream known) falls back to a plain resolve plus the own-output guard.
    /// </summary>
    private readonly Func<string, string?>? resolveUpstream;

    /// <summary>Textures are authored in BODY UV (the shell inherits the body's UVs).</summary>
    // internal so the compositor can prefetch this phase's art at the right size; see PrefetchAhead.
    internal const int TexSize = 2048;

    /// <summary>Coverage only decides whether a whole triangle survives, so it can be coarse.</summary>
    private const int CoverageSize = 256;

    /// <summary>Number of single-char base-36 shell disk ids (0-9a-z) — the ceiling on placeable layers,
    /// so an id never runs past 'z'.</summary>
    private const int DiskIdSpace = 36;

    /// <summary>Encode a layer's global index as a base-36 disk id char (0-9 then a-z). Digits-first keeps it
    /// ASCII-monotonic ('0'&lt;'9'&lt;'a'&lt;'z'), so the ghost/highlighter's char comparison still orders the stack.</summary>
    private static char DiskId(int d) => (char)(d < 10 ? '0' + d : 'a' + (d - 10));

    // A head/facewear "_met" model smaller than this is treated as an invisible/degenerate item (empty
    // frames) — the shell REPLACES it instead of appending, since a merge into a near-empty model won't
    // render. Real glasses/helmets are tens of KB; "The Emperor's New"-style invisibles are ~1.5 KB.
    private const int DegenerateModelBytes = 3000;

    /// <summary>
    /// The Emperor's New Ring — invisible, so a shell on it shows only our material. One source of truth
    /// with the resolver that finds the item to equip: this set id is what ties the published path, the
    /// EQDP entry and the Glamourer item together, and two copies of 53 could drift apart.
    /// </summary>
    private const int EmperorSetId = InvisibleRing.EmperorSetId;

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
        UVRemapService uvRemap, Configuration config, IPluginLog log,
        Func<string, string?>? resolveUpstream = null)
    {
        this.penumbra = penumbra;
        this.textureLoader = textureLoader;
        this.discovery = discovery;
        this.uvRemap = uvRemap;
        this.config = config;
        this.log = log;
        this.resolveUpstream = resolveUpstream;
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
        List<string> HostModelPaths,
        // The subset of HostModelPaths we APPEND into — an item the player is wearing, whose own model we
        // read back as the base for the merge. These are the only host paths that have an upstream worth
        // recovering, so they are the only ones PrimeUpstreamCache should ever unpublish to go looking (see
        // there). A CARRIER host is replaced outright and its base is never read, so priming it would blank
        // the shell for the width of the prime and learn nothing.
        List<string> AppendHostModelPaths);

    /// <summary>
    /// Write only if the content differs; reports whether it did.
    ///
    /// Via a temp file and an atomic move, NOT WriteAllBytes. Shell output names are stable
    /// (models/secondskin_{hash}.mdl, materials/ss_{char}.mtrl — the hash is over the SOURCE, so the same
    /// source rewrites the same name), which means a rewrite truncates a file a LIVE redirect points at.
    /// That was survivable only while the composite unpublished every redirect up front; now that it
    /// doesn't, a redraw landing mid-write would read a half-written model. Same reasoning and same shape
    /// as TextureLoader.WriteWithRetry and PenumbraModMeta.AtomicWrite.
    /// </summary>
    private static bool WriteIfChanged(string path, byte[] data)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(data))
                return false;
        }
        catch { /* unreadable — fall through and rewrite */ }

        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, data);
        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, path, overwrite: true); return true; }
            catch (Exception) when (i < 5) { Thread.Sleep(50 << i); }  // the game may hold it open mid-load
            catch { try { File.Delete(tmp); } catch { } throw; }       // don't leave the temp behind
        }
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
        // The bare-body e0000 models the game is CURRENTLY DRAWING, per slot (see
        // CompositorService.BareBodyModelsFromModels). Ground truth for both the model code and the path a
        // bare slot is cut from: a race with no e0000 models of its own draws another race's, and only the
        // live resource says which. Null/absent slots fall back to a path built from the model code.
        IReadOnlyDictionary<string, string>? bareBodyModels = null,
        // The character's REAL race code, off a drawn chara/human/… model. charCode is the shared BODY
        // code — c0201 for every "Midlander-bodied" female — so it cannot name the race whose metadata
        // entry has to be emptied for a carrier host. Null falls back to charCode, which is right for the
        // races that ship their own body (Au Ra, Viera, Hrothgar) and merely does nothing for the rest.
        string? drawnRaceCode = null,
        // The .mtrl game paths the character is CURRENTLY drawing. Used for one thing: reading the material
        // VARIANT folder a host actually loads under. See VariantFolderFor — the model references our
        // material variant-relatively, so publishing it under the wrong variant means the game never asks
        // for it and the appended mesh renders with no material at all.
        IReadOnlySet<string>? activeMaterials = null,
        // The two CARRIERS' material variants, off their sheets. See HostAccessory.KnownVariant: a carrier
        // is equipped after the shell is built, so the live tree cannot answer for it.
        int? emperorRingVariant = null,
        int? invisibleGlassesVariant = null)
    {
        if (gearOverlays.Count == 0) return null;

        // Which "v####" folder the game will ask for a host's materials under.
        //
        // The shell's material is stored in the model with a LEADING SLASH (see SecondSkinLayer.MaterialName
        // below), which is the variant-relative form: the game builds
        // chara/{tree}/{set}/material/v{variant}/{name} using the EQUIPPED ITEM's variant, not anything in
        // the model. This was hardcoded to v0001, so a host on any other variant had our material published
        // at a path the game never requests — and the failure was invisible from every angle we could see:
        // the redirect resolved perfectly (nothing else claims that path), the model was ours and drew, and
        // only the appended meshes silently had no material.
        //
        // Read from the live resource tree rather than guessed, so it is whatever the game actually asked
        // for. A CARRIER is the case the tree cannot answer — it is equipped after the shell is built — so
        // those pass their variant in from their item sheet; see HostAccessory.KnownVariant.
        string VariantFolderFor(HostAccessory h)
        {
            var dir = $"chara/{h.Tree}/{h.Prefix}{h.SetId:D4}/material/v";

            // The variant belongs to the EQUIPPED ITEM IN THIS SLOT, not to the set, so only a material the
            // game loaded for this slot answers the question outright. An accessory set is a jewellery set —
            // one id covers _nek, _ear, _wrs and _rir — and each worn piece carries its own variant, so
            // matching the set alone would take whichever piece the (unordered) set enumerated first and
            // publish the shell under a neighbour's variant, differently from run to run. The slot is in the
            // material's own name, so the exact answer is cheap to insist on.
            //
            // Set-wide is kept only as a recovery for the host's materials being briefly absent — a stale
            // snapshot — and ONLY when every piece of the set agrees on one variant, which is the single
            // shape in which a neighbour cannot be lying about this slot. Disagreement means the set spans
            // variants and no neighbour can speak for us, so fall through rather than flip a coin.
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
                    if (m.Contains(slotTag, StringComparison.OrdinalIgnoreCase)) return folder;
                    if (setVariant == null) setVariant = folder;
                    else if (!string.Equals(setVariant, folder, StringComparison.OrdinalIgnoreCase))
                        setDisagrees = true;
                }

            if (setVariant != null && !setDisagrees) return setVariant;
            return h.KnownVariant is { } v ? $"v{v:D4}" : "v0001";
        }

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
        static List<IGrouping<string, string>> CodeVotes(IEnumerable<string> paths)
            => paths.Select(PathCharCode).Where(c => !string.IsNullOrEmpty(c)).Select(c => c!)
                .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Resolve the winner, breaking an exact tie with evidence the main vote didn't get to see.
        //
        // The old tie-break was <see cref="CodeVotes"/>'s lexicographic ThenBy, which is arbitrary: on a
        // Miqo'te wearing one c0201 necklace and one c0101 ring it picked c0101 purely because "0101"
        // sorts first, then reported her own c0201 equipment as race-deformed. That is not an exotic
        // input — many vanilla accessories ship at c0101 ONLY, whatever the wearer's race, so a 1-1 split
        // across two worn accessories is the ordinary shape of this vote on a character wearing no gear.
        string PickCode(List<IGrouping<string, string>> votes, bool votedOnBare)
        {
            int best = votes[0].Count();
            var tied = votes.Where(g => g.Count() == best).Select(g => g.Key).ToList();
            if (tied.Count == 1) return tied[0];

            // Runoff 1 — the bare-body parts the game is DRAWING. Same kind of evidence as the main vote
            // (the race the game resolved this character's equipment to), held out of it because
            // uncovered slots outnumber worn items and would drown real gear. When the worn items split
            // exactly, that objection is gone and this is the only tiebreaker that is about equipment at
            // all. Skipped when the main vote already WAS the bare parts — it would just restage the tie.
            if (!votedOnBare)
            {
                var bare = CodeVotes(bareBodyModels?.Values ?? Enumerable.Empty<string>())
                    .Where(g => tied.Contains(g.Key, StringComparer.OrdinalIgnoreCase)).ToList();
                if (bare.Count == 1 || (bare.Count > 1 && bare[0].Count() > bare[1].Count()))
                    return bare[0].Key;
            }

            // Runoff 2 — the character's own body code, when the evidence is still balanced. Home beats
            // foreign: a shell hosted in the space the character already draws in needs no deformation,
            // so on a genuine coin-flip it is the choice with the smaller failure.
            if (tied.Contains(charCode, StringComparer.OrdinalIgnoreCase)) return charCode;

            // Runoff 3 — on a male/female coin flip, take the female. The alternative is CodeVotes'
            // lexicographic order, which always hands it to the male code because "0101" sorts before
            // "0201", and that is the losing side of the bet twice over: c0201 is the space nearly every
            // body mod is authored in, and being wrong toward c0101 is the failure that SHRINKS a shell
            // (male->female is a downward deform, applied to geometry already cut female — the reported
            // bug). Being wrong toward c0201 on an actually-male character is the same magnitude in the
            // other direction, but it is by far the rarer input.
            var female = tied.FirstOrDefault(c => RaceIndex(c) is { } n && n % 2 == 0);
            if (female != null) return female;

            return tied[0];   // deterministic last resort — CodeVotes already ordered these by key
        }

        var codeVotes = CodeVotes(equippedPaths);
        var voteSource = "equipped";
        var votedOnBare = false;

        // Wearing nothing: the e0000 parts the game is DRAWING are the only evidence left, and they are
        // evidence of the same kind — the race the game resolved this character's equipment to. Without
        // them a naked Au Ra fell through to the probe below, which picked her own c1401 (no e0000 models
        // exist there), so every part missed and the shell came out empty.
        //
        // Counted ONLY when nothing is equipped, never alongside gear: uncovered slots OUTNUMBER worn
        // items, and EQDP is per-item, so a character whose gear ships at her own race while her bare
        // slots fall back to Midlander would see her gear outvoted 3-to-1. modelCode gates hosting —
        // LoadCandidate skips any accessory whose path code differs — so a flipped vote would reject every
        // ring she actually wears and dump the whole look onto the undeformed Emperor fallback.
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
                log.Warning("[Proteus] second skin: {0} models disagree on a model code [{1}] — using c{2}",
                    voteSource, string.Join(", ", codeVotes.Select(g => $"{g.Key}x{g.Count()}")), modelCode);
        }
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
                // them. A mod redirect counts, else ask the game index directly.
                //
                // ResolvePlayer is NOT an existence test: Penumbra ECHOES the game path back when no mod
                // redirects it, so "!= null" was true for every candidate and the loop always stopped on the
                // first one, charCode. That silently un-did the whole point of probing — an Au Ra female
                // (c1401 ships no e0000 models; she draws Midlander c0201) asked for c1401e0000_*.mdl, all
                // four parts missed, and she got no shell at all. A redirect only counts when it resolves to
                // something OTHER than the path we asked for, and that something is a real file on disk.
                var probe = $"chara/equipment/e0000/model/c{cand}e0000_top.mdl";
                var probeDisk = penumbra.ResolvePlayer(probe);
                bool modded = probeDisk != null
                           && !string.Equals(probeDisk, probe, StringComparison.OrdinalIgnoreCase)
                           && File.Exists(probeDisk);
                if (!modded && !Plugin.DataManager.FileExists(probe)) continue;
                modelCode = cand;
                break;
            }
            modelCode ??= charCode;
        }

        if (!string.Equals(modelCode, charCode, StringComparison.OrdinalIgnoreCase))
            log.Information("[Proteus] second skin: c{0} wears c{1} equipment models (race-deformed) — any "
                          + "bare slot the live walk missed is rebuilt in c{1}", charCode, modelCode);

        // Each kept part carries its bytes, the shape keys enabled on THAT body model (by stem) so the
        // writer bakes only the morphs the game is actually applying to that part, and the game path it
        // was cut from — the path's race code is what decides how the game deforms this geometry, and the
        // shell has to be hosted in that same space (see cutCode below).
        var bodies = new List<(byte[] Bytes, HashSet<string>? Shapes, string Path)>();
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
            // Prefer the e0000 model the game is ACTUALLY drawing in this slot over one rebuilt from the
            // model code: EQDP can send a slot to a different race than the vote settled on, and the live
            // resource is the only thing that knows. Rebuild only for a slot that isn't in the live set
            // (nothing drawn there, or the walk came back empty) — same path as before.
            var bareBody = bareBodyModels != null && bareBodyModels.TryGetValue(part, out var drawnBare)
                ? drawnBare
                : $"chara/equipment/e0000/model/c{modelCode}e0000_{part}.mdl";
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

            bodies.Add((bytes, partShapes, bodyGamePath));
            modelType ??= partType;
        }

        // ── whole-body fallback ──────────────────────────────────────────────
        // Not every race ships e0000 parts. Viera, Hrothgar and Au Ra F have none, so the game resolves
        // those paths through EQDP to another race's model and the direct path never loads. Left alone that
        // silently drops the torso and hands from the shell, leaving a fabric that renders only where some
        // equipped gear model happened to carry a skin mesh — 2 meshes where a Midlander gets 6.
        //
        // LAST resort, and a poor one. The primary answer is bareBodyModels: the e0000 model the game is
        // actually drawing in each slot, whatever race it resolved to, which IS the modded body. This fires
        // only when that live set had nothing for a slot (an empty/stale draw-object walk) AND the path
        // rebuilt from the model code missed — i.e. we know nothing about what the character draws.
        //
        // Its weakness is UV space, not shape: a body mod replaces the e0000 EQUIPMENT models (Bibo+ ships
        // c0201e0000_top/dwn/glv/sho and nothing under obj/body/…/model/), so this reads VANILLA bytes in
        // vanilla UV even for a modded character. SkinBodyType then reports gen2 and the gate below drops it
        // unless a gear overlay opted into All bodies — which is the honest outcome: a vanilla-UV shell over
        // a Bibo+ body is art in the wrong place, not a rescue. Don't "fix" that by loosening the gate.
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
            // Prefer whichever body the player's MOD owns, since that is the one they are actually wearing;
            // else take the first that exists.
            // The file name carries the customization-type suffix — c1401b0001_TOP.mdl, the same "top" the
            // e0000 torso uses (Penumbra.GameData GamePaths.Mdl.Customization). Without it this asked for
            // c1401b0001.mdl, which exists for no race, so the fallback loaded nothing for ANYONE and the
            // race it exists to rescue got an empty shell with only "no whole-body model loaded" in the log.
            (byte[] Bytes, string Path, string? Disk)? pick = null;
            foreach (var bodyId in WholeBodyIds)
            {
                var wholePath = $"chara/human/c{charCode}/obj/body/{bodyId}/model/c{charCode}{bodyId}_top.mdl";
                // ResolvePlayer ECHOES the game path when nothing redirects it, so a non-null result is not
                // evidence of a mod — only a resolved path that DIFFERS and is a real file on disk is.
                var resolved = penumbra.ResolvePlayer(wholePath);
                var wholeDisk = resolved != null
                             && !string.Equals(resolved, wholePath, StringComparison.OrdinalIgnoreCase)
                             && File.Exists(resolved) ? resolved : null;
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
                    // Warning, not Information: this is the normal outcome for a modded body (no body mod
                    // replaces the human body model, so it always reads vanilla), and it means the shell
                    // ships SHORT — with 0 parts cut above, not at all. Whoever reads the log after "my
                    // glow didn't appear" needs to see it at the level they actually run at.
                    log.Warning("[Proteus] second skin: whole-body fallback {0} is vanilla (gen2) — no gear "
                              + "overlay opted into All bodies, leaving the {1} part(s) cut above as-is. The "
                              + "live bare-body models were unavailable this composite; a redraw usually fixes it",
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
                    bodies.Add((whole.Bytes, wholeShapes, whole.Path));
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

        // ── the space the geometry is IN ──────────────────────────────────────────────────────
        // The game race-deforms a model according to the race code of the PATH it loaded it from. The body
        // this shell copies is drawn from these exact paths, so a shell hosted under the same code deforms
        // with it, and a shell hosted under any other code does not.
        //
        // This is NOT the equipment code voted above. Those are different EQDP chains and they disagree in
        // ordinary cases: a Miqo'te female draws c0201 e0000 body parts (Midlander, deformed 0201->0801)
        // while her facewear ships native at c0801. That mismatch is why her shell renders Midlander-sized.
        //
        // Knowing it does NOT let us fix it by moving the path, which build #294 tried and had to undo:
        // every host a character can offer — worn accessory, facewear, or the Emperor ring the EQDP entry
        // conjures — loads in that character's own space, so requiring cut space leaves no host at all. It
        // is kept because it is the honest diagnosis (logged per composite, warned on per host) and because
        // the eventual fix reads it: deform the geometry from cut space into the host's space ourselves,
        // the way TexTools race-converts, before writing the shell.
        //
        // Majority, because one host serves the whole shell: a race-native gear top cut beside bare c0201
        // legs is genuinely two spaces at once. Ties and unreadable paths fall back to the equipment code.
        var cutVotes = CodeVotes(bodies.Select(b => b.Path));
        var cutCode = modelCode;
        if (cutVotes.Count == 1
            // A tie means the shell is half in each space and neither is more right than the other, so
            // keep the equipment code rather than letting grouping order decide it.
            || (cutVotes.Count > 1 && cutVotes[0].Count() > cutVotes[1].Count()))
            cutCode = cutVotes[0].Key;
        if (cutVotes.Count > 1)
            log.Warning("[Proteus] second skin: the cut parts are in more than one model space [{0}] — hosting "
                      + "in c{1}; the other part(s) will be deformed differently from the body they copy",
                string.Join(", ", cutVotes.Select(g => $"{g.Key}x{g.Count()}")), cutCode);
        // The tally, ALWAYS — not just on the multi-space warning above. A unanimous vote can still be
        // unanimously wrong (every bare part rebuilt from a bad modelCode votes the same bad code, which
        // is how a c0101 cutCode reached a Midlander female), and without the breakdown the log said only
        // WHICH code won, never on what evidence. That is the difference between a report that identifies
        // the bug and one that just confirms the symptom.
        log.Information("[Proteus] second skin: cut in c{0} space ({1} part(s), votes [{2}]) — a host that "
                      + "loads under a different code will render it a race-size wrong",
            cutCode, bodies.Count,
            cutVotes.Count > 0
                ? string.Join(", ", cutVotes.Select(g => $"c{g.Key}x{g.Count()}"))
                : $"no readable path codes, fell back to the equipment code c{modelCode}");

        // Accessories the shell can spill across, in fill priority (glasses -> rings -> bracelet -> necklace
        // -> Emperor fallback). Each holds MaxMaterials - BaseMatCount layers; layers are distributed across
        // them so a big look can span several items. An already-equipped host APPENDS; the Emperor REPLACES.
        var hosts = ChooseHosts(cutCode, modelCode, equippedAccessories, metModels, invisibleGlassesSet, outputRoot,
            emperorRingVariant, invisibleGlassesVariant);

        // ── per-host publish decision, resolved once for BOTH loops below ──────────────────────────────
        // The material loop names the materials baked into each shell, and the host loop publishes the
        // model — and the two MUST agree on the race code, because a material is looked up under the code
        // its model loads at. Deciding this inside the host loop (where it started) left the material loop
        // still naming everything at cutCode while the model went out at hostRace, which is a shell with
        // materials the game will never find. One computation, both consumers.
        //
        // hostRace: the race whose EQDP entry the game consults — the CHARACTER's real one, never the code
        // of the path the model happens to load from right now. Reading it off host.ModelPath was a feedback
        // loop against our own fix, and it alternated in the wild on a Miqo'te wearing the injected glasses:
        //
        //   composite A  the walk sees c0801e5501_met (her native facewear). 0801 != cutCode 0201, so the
        //                pair fires: c0801 is emptied, the shell is published at both codes, and the game
        //                duly falls through to the c0201 twin. Suit on.
        //   composite B  the walk now sees c0201e5501_met — because of A. 0201 == cutCode, so the test goes
        //                FALSE, no manipulation is emitted, and only the c0201 path is published. With
        //                c0801's entry no longer emptied the game asks for c0801, where we publish nothing.
        //                Suit off.
        //   composite C  the walk sees c0801 again… and round it goes.
        //
        // That is "the suit disappears when I drag a slider, and comes back when I refresh". drawnRaceCode
        // is read off the drawn chara/human model, which our redirects cannot move, so it is stable. The
        // path code stays as a FALLBACK for the first composite of a login, before any walk has returned a
        // human model; charCode is the last resort.
        //
        // Native: cut space is NOT on the wearer's fall-through chain, so the usual trick — empty the
        // wearer's entry and let the game walk to cut space, inheriting the deform — would land it on the
        // wrong body. Put a c0101 shell in front of a Midlander female and vanilla fall-through (c0201 ->
        // c0101 is the game's own hop) applies the male->female deform to geometry already cut female: it
        // renders, shrunk and low, which is exactly what two Midlanders reported. cutCode is only a VOTE
        // over the paths the parts came from — a label — while the geometry was copied from what this
        // character actually draws, so when the label is incoherent with the wearer, trust the wearer:
        // publish at hostRace, declare THAT race has the model, and the game loads it with no deform.
        //
        // Carriers only. An APPEND host redirects host.ModelPath — the path the game already resolved for
        // the player's own item — so a mismatch there renders a race-size wrong rather than wrong-bodied,
        // which build #294 established beats no shell. Those keep WarnForeignAppendHost and are untouched.
        var plan = new (string HostRace, bool Native, string PublishCode)[hosts.Count];
        for (int i = 0; i < hosts.Count; i++)
        {
            var h0 = hosts[i];
            var race = drawnRaceCode
                    ?? (h0.ModelPath != null ? PathCharCode(h0.ModelPath) : null)
                    ?? charCode;
            bool native = h0.BaseModel == null
                       && !string.Equals(race, cutCode, StringComparison.OrdinalIgnoreCase)
                       && !CanFallThrough(race, cutCode);
            if (native)
                log.Warning("[Proteus] second skin: host {0}{1:D4}/{2} — the shell claims to be cut in c{3}, "
                          + "which is not on c{4}'s fall-through chain. Publishing NATIVELY at c{4} instead "
                          + "(no deform); one of the two codes is wrong and c{3} is the suspect",
                    h0.Prefix, h0.SetId, h0.Slot, cutCode, race);
            plan[i] = (race, native, native ? race : cutCode);
        }
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
            // under — the equipped host's real resolved path, or the rebuild's publish code for a carrier
            // (see mdlGamePath below). On an append host this also keeps our added letters matching the
            // base's own material names instead of mixing two codes inside one model.
            //
            // plan[hIdx].PublishCode, NOT cutCode: the two are the same except on the native-publish path,
            // and hardcoding cutCode there names materials the game would look for under a different code
            // and never find. That is why the plan is computed before this loop rather than inside the
            // host loop below.
            var hostCode = host.ModelPath != null
                ? PathCharCode(host.ModelPath) ?? plan[hIdx].PublishCode
                : plan[hIdx].PublishCode;
            string matName = $"mt_c{hostCode}{host.Prefix}{host.SetId:D4}_{host.Slot}_{matLetter}.mtrl";
            string matVariant = VariantFolderFor(host);
            string matGamePath = $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/material/{matVariant}/{matName}";
            string texPrefix   = $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/texture/ss_{diskChar}_";

            // Which UV space is this art painted in? A mod listing only *_bibo.mtrl is bibo art; the gear
            // layer has no material-match gate like the skin layer, so remap into the body's UV explicitly.
            var srcType = ov.Descriptor.SourceBodyType ?? InferOverlayBodyType(ov.Descriptor);
            log.Information("[Proteus] gear layer mat={0}/{10}/disk={1} -> host {2}{3:D4}/{4}: shader={5} UV {6}->{7}{8}{9}",
                matLetter, diskChar, host.Prefix, host.SetId, host.Slot, shader, srcType ?? "(unknown)", bodyType ?? "(unknown)",
                srcType != null && bodyType != null && !string.Equals(srcType, bodyType, StringComparison.OrdinalIgnoreCase) ? " [REMAP]" : "",
                isMaskShell ? " [MASK SHELL]" : "", matVariant);

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
            try { mtrl = GearMaterialWriter.Build(template, texPaths, BuildRows(ov.ColorTableRows, neutralWhenEmpty: isMaskShell), scroll, config.GearCutoutAlpha, linearizeDiffuse: isMaskShell); }
            catch (Exception ex) { log.Error(ex, "[Proteus] second skin: material build failed for {0}", shader); continue; }

            var matDisk = Path.Combine(materialsDir, $"ss_{diskChar}.mtrl");
            shellChanged |= WriteIfChanged(matDisk, mtrl);
            redirects[matGamePath] = Rel(outputRoot, matDisk);
            var shellKey = (entry.ModDirectory, ov.OptionGroup, ov.Option);
            if (!shellMaterials.TryGetValue(shellKey, out var shellList))
                shellMaterials[shellKey] = shellList = new List<string>();
            shellList.Add($"ss_{diskChar}.mtrl");

            perHostLayers[hIdx].Add(new SecondSkinLayer
            {
                MaterialName = "/" + matName,   // the model stores material names with a leading slash
                Coverage = coverage,
                CoverageWidth = coverage == null ? 0 : CoverageSize,
                CoverageHeight = coverage == null ? 0 : CoverageSize,
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
                  + $"but only {totalCapacity} fit across your accessories (Proteus' invisible fallback ring already "
                  + $"included). Turn off some layers, or equip another pair of glasses / ring / bracelet / necklace so "
                  + $"the rest fit.";
                // Marshalled: the shell build runs off the framework thread, and ChatGui's queue is not
                // safe to enqueue into concurrently with the tick that drains it.
                _ = Plugin.Framework.RunOnFrameworkThread(
                    () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 25).Build()));   // 25 = yellow
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
        var appendHostModelPaths = new List<string>();
        for (int h = 0; h < hosts.Count; h++)
        {
            if (perHostLayers[h].Count == 0) continue;
            var host = hosts[h];

            // All three resolved above the material loop, so the materials baked into this shell and the
            // path it is published at agree on a race code. See the plan block after ChooseHosts.
            bool carrier = host.BaseModel == null;
            var (hostRace, nativeAtHostRace, publishCode) = plan[h];
            bool differs = !string.Equals(hostRace, cutCode, StringComparison.OrdinalIgnoreCase);

            byte[] shell;
            SecondSkinWriter.Stats stats;
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
            // fallback has no resolved path to copy (ModelPath null), so its path is rebuilt here — in
            // cutCode space, matching the EQDP entry written below, which declares THAT race/gender to have
            // its own model for the slot.
            //
            // CUT space, paired with the EQDP entries written below — the same route a Midlander-only gear
            // mod takes onto every other race: its entry for the wearer's race is empty, so the game walks
            // to the parent race's model and race-deforms it on the way. Our shell is a copy of c0201 body
            // parts that get exactly that deform, so inheriting it is what makes the shell fit.
            //
            // Verified in game on a Miqo'te female wearing the Emperor's New Ring: published at c0201, EQDP
            // for Midlander Female, and the shell rendered at the body's size. (Build #294 published here
            // too and rendered nothing — but her ring slot was EMPTY that whole time, so the game never
            // asked for any ring model. That was a dead test, not evidence against this.)
            //
            // One published path per host, except the invisible-carrier case below. An earlier version
            // hedged by publishing every shell at a second code; that alias came back as an equipped
            // accessory next composite and poisoned the model-race vote above. Don't re-add it generally.
            //
            // The carrier exception below DOES publish two codes, and that includes the Emperor's ring —
            // which looks like exactly the re-add this warns against. It is safe for one specific reason:
            // a{EmperorSetId} is filtered out of the model-race vote at its source (see the .Where on
            // equippedPaths), so no alias of it can reach the vote whatever code it loads under. Nothing
            // else enjoys that exemption, so the warning still stands for every other path.
            var mdlGamePath = host.ModelPath
                ?? $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/model/c{publishCode}{host.Prefix}{host.SetId:D4}_{host.Slot}.mdl";
            var mdlDisk = Path.Combine(modelsDir, $"secondskin_{h}.mdl");
            var modelChanged = WriteIfChanged(mdlDisk, shell);

            // What the model ON DISK asks the game for — read back from the FILE, not from the bytes we just
            // built, because those are different questions and only the file is what the game loads.
            //
            // A carrier was observed drawing with no material at all, and Penumbra named the request:
            // chara/equipment/e5501/material/v0001/mt_c0201a0053_rir_a.mtrl — the GLASSES directory with the
            // EMPEROR RING's material name. Shell files are keyed by host INDEX (secondskin_{h}.mdl) and the
            // host list changes between composites, so index 0 is the ring one composite and the glasses the
            // next. Reading the built bytes would have agreed with itself and proved nothing; reading the
            // file catches a write that did not land.
            try
            {
                // Re-read ONLY after a write. When WriteIfChanged reports unchanged it has already read the
                // file and proven it equals `shell`, so `shell` IS the disk contents and a second read would
                // buy nothing at a few megabytes a composite. When it reports changed we have just written,
                // and re-reading is the one thing that can catch a write reporting success without landing —
                // which is the anomaly this exists for.
                var onDisk = shell;
                if (modelChanged)
                {
                    onDisk = File.ReadAllBytes(mdlDisk);
                    if (!onDisk.AsSpan().SequenceEqual(shell))
                        log.Warning("[Proteus] shell file {0} does NOT match the bytes just built for host "
                                  + "{1}{2:D4}/{3} — the write did not land, so the game is loading a stale shell",
                            Path.GetFileName(mdlDisk), host.Prefix, host.SetId, host.Slot);
                }

                var declared = SecondSkinWriter.MaterialNames(onDisk);
                // Slot as well as set: one accessory set covers _nek, _ear, _wrs and _rir, so matching the set
                // alone would list a sibling piece's material under this host — the same conflation the
                // variant lookup had to be narrowed for.
                var published = redirects.Keys.Where(k =>
                    k.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)
                 && k.Contains($"{host.Prefix}{host.SetId:D4}/", StringComparison.OrdinalIgnoreCase)
                 && k.Contains($"_{host.Slot}_", StringComparison.OrdinalIgnoreCase));

                log.Information("[Proteus] second skin host {0}{1:D4}/{2}: model declares {3} material(s) [{4}] "
                              + "— published [{5}]",
                    host.Prefix, host.SetId, host.Slot, declared.Count, string.Join(", ", declared),
                    string.Join(", ", published));
            }
            catch (System.Exception ex)
            {
                log.Warning("[Proteus] could not read back the shell model's material names: {0}", ex.Message);
            }
            shellChanged   |= modelChanged;
            modelChangedAny |= modelChanged;
            redirects[mdlGamePath] = Rel(outputRoot, mdlDisk);
            hostModelPaths.Add(mdlGamePath);
            // Append hosts only: BaseModel non-null means we merged into the player's own item, so this
            // path has a real upstream (their necklace/ring mod) that later composites must read back.
            if (host.BaseModel != null) appendHostModelPaths.Add(mdlGamePath);

            // ── carrier hosts: make the game load our copy from CUT space ─────────────────────────
            // A host whose model we REPLACE has no appearance of its own to protect — the Emperor's ring is
            // invisible, and our injected glasses are only ever seen AS the shell. So its per-race metadata
            // is ours to rewrite, and rewriting it is what makes non-Midlander races fit: empty the entry
            // for the race the game would load it natively from, and the lookup falls through to the parent
            // — the c0201 space the shell was cut in — picking up the same deform the body already gets.
            //
            // NOT done for an APPEND host (real glasses, a worn ring): there we merge into the player's own
            // item, and emptying its entry would swap their frames for a deformed Midlander pair. Those
            // keep the size warning from LoadCandidate instead.
            //
            // carrier/hostRace/differs/nativeAtHostRace are computed at the top of the loop — a fall-through
            // pair is only ever emitted for codes the guard there has already proven reach each other.
            if (carrier && nativeAtHostRace)
            {
                // Cut space was rejected, so there is no fall-through to arrange: the shell is published at
                // the wearer's own code and this entry is what makes the game load it from there. Nothing
                // is emptied — an empty entry is a request to be deformed, and the whole point here is to
                // take the geometry at face value and skip the deform.
                manipulations.Add(EqdpManipulation(hostRace, host.EqdpSlot, host.SetId));

                // Publish at c{hostRace} EXPLICITLY rather than trusting mdlGamePath. For the Emperor ring
                // (ModelPath null) they are already the same path and this is a no-op. For a facewear
                // carrier they are NOT: mdlGamePath is the resolved — or, for a pending injection, the
                // predicted — path, which is built from equipCode and can name a different race than the
                // entry we just wrote. Declaring hostRace has a model while publishing only at equipCode
                // is a redirect the game never asks for, so derive the path from the same code as the
                // manipulation. Whatever mdlGamePath already registered stays, on the same reasoning the
                // pair branch keeps its native copy: if this slot turns out not to be EQDP-driven, the
                // resolved path is still there and we are no worse off.
                var nativePath = $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/model/"
                               + $"c{hostRace}{host.Prefix}{host.SetId:D4}_{host.Slot}.mdl";
                if (!redirects.ContainsKey(nativePath))
                {
                    redirects[nativePath] = Rel(outputRoot, mdlDisk);
                    hostModelPaths.Add(nativePath);
                }
                log.Information("[Proteus] second skin: EQDP for {0} {1}{2:D4} — c{3} has the model, loaded "
                              + "natively with no fall-through -> {4}",
                    host.EqdpSlot, host.Prefix, host.SetId, hostRace, nativePath);
            }
            else if (carrier && differs)
            {
                manipulations.Add(EqdpManipulation(cutCode,  host.EqdpSlot, host.SetId));
                manipulations.Add(EqdpManipulation(hostRace, host.EqdpSlot, host.SetId, hasModel: false));

                // Publish at BOTH codes, derived from hostRace/cutCode rather than from whatever the walk
                // resolved. If the emptied entry takes, the game loads the cut-space copy and deforms it
                // (right size); if this slot turns out not to be EQDP-driven — facewear is a bonus item and
                // may not be — it loads the native copy and we are no worse off than before. Deriving the
                // pair instead of reusing host.ModelPath is what stops the published set from shrinking on
                // the composites where the walk has already been steered onto the twin.
                string PathFor(string code)
                    => $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/model/"
                     + $"c{code}{host.Prefix}{host.SetId:D4}_{host.Slot}.mdl";

                foreach (var code in new[] { cutCode, hostRace })
                {
                    var p = PathFor(code);
                    if (redirects.ContainsKey(p)) continue;
                    redirects[p] = Rel(outputRoot, mdlDisk);
                    hostModelPaths.Add(p);
                }
                log.Information("[Proteus] second skin: EQDP for {0} {1}{2:D4} — c{3} has the model, c{4} "
                              + "emptied so the game falls through to it -> {5} (native copy kept at {6})",
                    host.EqdpSlot, host.Prefix, host.SetId, cutCode, hostRace, PathFor(cutCode), PathFor(hostRace));
            }
            else if (carrier && host.Prefix == 'a')
            {
                // Emperor's ring in a race that is already cut-space (Midlander/Miqo'te/Elezen/Roegadyn
                // females all read c0201 here): it loads no model at all without an entry saying it has one.
                manipulations.Add(EqdpManipulation(cutCode, host.EqdpSlot, host.SetId));
                log.Information("[Proteus] second skin: EQDP for {0} {1}{2:D4} — c{3} has the model -> {4}",
                    host.EqdpSlot, host.Prefix, host.SetId, cutCode, mdlGamePath);
            }
            log.Information("[Proteus] second skin: host {0}{1:D4}/{2} <- {3} layer(s) -> {4} meshes, {5} KB (append={6})",
                host.Prefix, host.SetId, host.Slot, perHostLayers[h].Count, stats.Meshes, shell.Length / 1024, host.BaseModel != null);
        }
        if (hostModelPaths.Count == 0) return null;

        return new Result(redirects, manipulations, shellChanged, shellMaterials, modelChangedAny,
                          hostModelPaths, appendHostModelPaths);
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

    /// <summary>
    /// Every colour-table row neutral white, so no row keeps the gear template's default (often dark)
    /// colour. The index texture can select ANY pair — one the colorset never defined, or the undefined
    /// sub-row of a pair that set only the other — and that row must not paint something the author never
    /// chose, exactly as the skin layer defaults its rows to white. (16 pairs = 32 sub-rows.)
    /// </summary>
    private static Dictionary<int, GearColorRow> NeutralRows()
    {
        var rows = new Dictionary<int, GearColorRow>();
        for (int r = 0; r < 32; r++)
            rows[r] = new GearColorRow { Diffuse = (1f, 1f, 1f), Emissive = (0f, 0f, 0f) };
        return rows;
    }

    /// <summary>Map the metadata's 1-based row/sub-row presets onto 0-based color table rows.</summary>
    /// <param name="neutralWhenEmpty">
    /// With no presets, return the all-white baseline instead of null. Null means "keep the gear template's
    /// own colour table", which is right for an ordinary shell — its template belongs to the look being
    /// worn — and wrong for a MASK shell, which has no look of its own. A mask shell is a WHITE base plus
    /// whatever the colorset says (see the Build call), so white rows are its blank canvas: it starts
    /// uncoloured and the Masks colorset dyes it from there. Inheriting the template's table instead would
    /// start it at some arbitrary dark colour the author never picked. Only the mask-shell caller passes true.
    /// </param>
    private static Dictionary<int, GearColorRow>? BuildRows(List<ColorTableRowPreset>? presets,
                                                            bool neutralWhenEmpty = false)
    {
        if (presets == null || presets.Count == 0) return neutralWhenEmpty ? NeutralRows() : null;
        var rows = NeutralRows();

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
        string? ModelPath = null,
        // The material variant to publish under, when the live resource tree cannot answer. Only a CARRIER
        // needs this: it is equipped after the shell is built, so its materials are not in the tree yet and
        // VariantFolderFor would fall back to v0001 — wrong for any carrier item that is not variant 1, and
        // wrong in a way that renders nothing at all. Null for worn hosts, which the tree does answer for.
        int? KnownVariant = null);

    /// <summary>
    /// Every model the shell can be hosted on, in FILL priority — the policy is written out at the top of
    /// the method body. Each carries its base material count; a host holds up to
    /// <c>MaxMaterials - BaseMatCount</c> layers, and layers are distributed across this list in order (see
    /// Build), so a look too big for one host spills into the next. A candidate with no room to append even
    /// one layer is skipped.
    /// <para/>
    /// <paramref name="cutCode"/> is the race code the shell GEOMETRY is in (see Build). A CARRIER host —
    /// one whose model we replace — may load under a different code, because Build then rewrites its EQDP
    /// entry to pull our copy into cut space; a worn item may not, because its metadata is the player's.
    /// <paramref name="equipCode"/> is the code the character's EQUIPMENT loads at, used only to predict
    /// where our not-yet-loaded injected glasses will appear.
    /// </summary>
    private List<HostAccessory> ChooseHosts(string cutCode, string equipCode,
        IReadOnlyDictionary<string, string>? equipped,
        IReadOnlyList<string>? metModels, int? invisibleGlassesSet, string outputRoot,
        int? emperorRingVariant, int? invisibleGlassesVariant)
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
            // A host that loads under a different code than the shell was cut in renders it at the wrong
            // SIZE: the code in the path is what decides whether the game race-deforms a model, so a
            // c0201-cut shell hosted at c0801 gets no deform while the body it copies does.
            //
            // Never rejected for loading under a foreign code. Build #294 rejected, and on a Miqo'te
            // wearing only our carrier glasses that removed the last host: no shell at all, and her glasses
            // frames came back, since hiding them is a side effect of hosting on them. The size warning
            // belongs to the APPEND path only (see WarnForeignAppendHost) — a carrier host is redirected
            // into cut space by Build instead, so warning here would cry wolf every composite.

            // A host's model path is one WE redirect to the shell, so Penumbra can resolve it straight back
            // to our own previous output. Taking that as the "base" is a feedback loop: on the append path
            // it would merge the shell into the shell again every composite, doubling the model each run.
            // The composite clears redirects and reloads before getting here, but that's async and races —
            // observed in the wild resolving to an 875 KB "glasses" model (our 854 KB shell).
            //
            // Go through the upstream resolver, NOT a plain resolve. An append host is an item the player
            // chose, and they may well have modded it: this path has to come back as THEIR necklace/ring
            // file even on the composites where our own redirect masks it. Reading the game's original
            // instead — what this did before — silently reverted a modded host to vanilla from the second
            // composite onward, and took the appended shell with it.
            var disk = resolveUpstream?.Invoke(gamePath) ?? penumbra.ResolvePlayer(gamePath);
            // Defence in depth: ResolveUpstream already biases toward "ours" and returns null rather than
            // hand back our output, so this should not fire — but a null resolver (tests) still needs it,
            // and appending the shell to itself is the one failure that compounds silently every run.
            if (disk != null && IsInsideOutputRoot(disk, outputRoot))
            {
                log.Debug("[Proteus] host: {0} resolved to our own output ({1}) and no upstream is known — "
                        + "reading the game's original instead", slot, disk);
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

        // An APPEND host merges into an item the player chose, so its metadata is not ours to rewrite —
        // which means a host loading under a code other than the shell's stays at the wrong size and the
        // player needs to be told. Carrier hosts (replaced, invisible) are fixed silently by Build.
        void WarnForeignAppendHost(string slot, char prefix, int setId, string gamePath)
        {
            if (PathCharCode(gamePath) is not { } pathCc
                || string.Equals(pathCc, cutCode, StringComparison.OrdinalIgnoreCase))
                return;
            log.Warning(
                "[Proteus] host: {0} ({1}{2:D4}) loads as c{3} but the shell was cut in c{4} — the game "
              + "deforms the body and not this worn item, so the shell will render a race-size wrong. Free "
              + "a ring slot (either hand) or your facewear slot to let Proteus host it in c{4} instead",
                slot, prefix, setId, pathCc, cutCode);
        }

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
        // Worn accessories that would host, but load under a code the shell was not cut in: kept aside
        // for the last resort below rather than used, since we cannot move a worn item into cut space.
        var foreignAccessories = new List<HostAccessory>();

        // ── the host policy, in order, and the same on every race ──────────────────────────────────
        // Prefer a host we can move into cut space; never rewrite an item the player chose; never end up
        // with no host at all.
        //
        // 1. Facewear CARRIER — our injected pair or a degenerate (invisible, empty-frames) item. Its model
        //    is REPLACED, so nothing of it is ever seen and Build may rewrite its EQDP to pull the shell
        //    into cut space. The player's own visible pair is deliberately NOT a candidate: appending would
        //    merge into their item, whose metadata is not ours to move, so it could only ever host at the
        //    wrong size. Leave it alone and let the accessories below take it.
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
            log.Information("[Proteus] host: glasses/head e{0:D4} is the player's own pair ({1} material(s), "
                          + "{2} B) — not ours to redirect into c{3}, leaving it alone",
                c.SetId, c.Mats, c.Bytes.Length, cutCode);
        }

        // Nothing occupies the head "_met" slot yet, but the invisible-glasses feature is on — so the
        // compositor is about to equip our pair. Host on it NOW: the injected model only loads after the
        // equip's redraw, and OUR pair always takes the REPLACE path (no base bytes needed), its path fully
        // determined by our set id plus this character.
        if (hosts.Count == 0 && (metModels == null || metModels.Count == 0) && invisibleGlassesSet is int pending)
        {
            // Predicted with the EQUIPMENT code, not the cut code: this is a guess at the path the game
            // will load our pair from once it is equipped, and equipment loads in the character's own
            // space (a Miqo'te's facewear ships native at c0801 even though her body parts are c0201).
            var pendingPath = $"chara/equipment/e{pending:D4}/model/c{equipCode}e{pending:D4}_met.mdl";
            log.Information("[Proteus] host: invisible glasses e{0:D4} (met, REPLACE — pending injection)", pending);
            hosts.Add(new HostAccessory(pending, "met", "Head", null, 0, "equipment", 'e', pendingPath,
                KnownVariant: invisibleGlassesVariant));
        }
        // (No invisible-glasses-from-nothing route for a slot we don't fill ourselves: an empty head/facewear
        // slot loads NO model, so there's nothing to redirect.)

        // 2. Worn accessories — rings (right then left), bracelet, necklace — appended so they stay
        //    visible, but ONLY when they already load in the shell's own space. One that doesn't would
        //    render the shell a race-size wrong, and unlike a carrier we cannot fix it, so it is held back
        //    for step 4 and the Emperor's ring gets first refusal.
        //
        //    Build #320 briefly moved the Emperor's ring AHEAD of these, on the theory that appending into
        //    a worn item was what stopped the shell rendering. That was wrong: a REPLACE carrier host
        //    (e5501/met, append=False) reproduces the same symptom, so the append/carrier split explains
        //    nothing and the reorder was reverted rather than left in on a dead rationale.
        foreach (var (slot, eqdp) in new[] { ("rir", "RFinger"), ("ril", "LFinger"), ("wrs", "Wrists"), ("nek", "Neck") })
        {
            if (Consider(slot, eqdp) is not { } acc) continue;
            if (acc.ModelPath != null && PathCharCode(acc.ModelPath) is { } accCc
                && !string.Equals(accCc, cutCode, StringComparison.OrdinalIgnoreCase))
            {
                log.Information("[Proteus] host: {0} ({1}{2:D4}) loads as c{3}, not the shell's c{4} — held back, "
                              + "the Emperor's ring hosts in c{4} instead", slot, acc.Prefix, acc.SetId, accCc, cutCode);
                foreignAccessories.Add(acc);
                continue;
            }
            hosts.Add(acc);
        }

        // 3. The invisible Emperor's New Ring (replace + EQDP), in the first ring slot that is FREE — right,
        //    then left. A slot holding the player's own ring is not ours to take. Offered even when step 2
        //    found hosts, since it is also the spill capacity for a look too big for them.
        var freeRing = new[] { (Slot: "rir", Eqdp: "RFinger"), (Slot: "ril", Eqdp: "LFinger") }
            .Select(r => (r.Slot, r.Eqdp,
                Worn: equipped != null && equipped.TryGetValue(r.Slot, out var wp) ? wp : null))
            // Ours counts as free: it is the ring we equipped for exactly this on an earlier composite.
            .FirstOrDefault(r => r.Worn == null || ParseSetId(r.Worn, 'a') == EmperorSetId);
        if (freeRing.Slot != null)
            hosts.Add(new HostAccessory(EmperorSetId, freeRing.Slot, freeRing.Eqdp, null, 0, "accessory", 'a',
                KnownVariant: emperorRingVariant));
        else
            log.Information("[Proteus] host: both ring slots hold the player's own rings — no free slot for "
                          + "the Emperor's ring");

        // 4. Last resort: every ring slot full AND nothing above usable. Host on a held-back accessory
        //    anyway — a shell a race-size wrong beats no shell at all, which is what rejecting outright
        //    produced in build #294 (and it took the carrier glasses' invisibility with it).
        if (hosts.Count == 0 && foreignAccessories.Count > 0)
        {
            var fallback = foreignAccessories[0];
            WarnForeignAppendHost(fallback.Slot, fallback.Prefix, fallback.SetId, fallback.ModelPath!);
            hosts.Add(fallback);
        }

        if (hosts.Count == 0)
            log.Warning("[Proteus] host: nothing can host the shell — no free facewear or ring slot, and no "
                      + "worn accessory to append to");

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
        // 'b' as well as 'a'/'e': the whole-body fallback cuts from chara/human/…/c1401b0001_top.mdl, and
        // that path's code is just as much "the space this geometry is in" as an equipment path's is —
        // there it happens to be the character's own race, which is why that geometry hosts natively.
        var m = System.Text.RegularExpressions.Regex.Match(gamePath, @"/c(\d+)[abe]\d+_");
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
    /// Slot's position in an EQDP entry. Each slot owns TWO bits (model, material), so its mask is
    /// <c>3 &lt;&lt; 2*index</c>. Equipment and accessories are separate tables with their own numbering —
    /// Penumbra picks the table from the slot name, so the two runs of indices don't collide.
    /// </summary>
    private static int EqdpSlotIndex(string slot) => slot switch
    {
        "Head" => 0, "Body" => 1, "Hands" => 2, "Legs" => 3, "Feet" => 4,        // equipment
        "Ears" => 0, "Neck" => 1, "Wrists" => 2, "RFinger" => 3, "LFinger" => 4, // accessory
        _ => 0,
    };

    /// <summary>The leading race/gender index of a char code ("0801" → 8), or null when it carries none.
    /// Two digits, matching how the game numbers them: odd = male, even = female, and (n-1)/2 indexes
    /// <see cref="RaceNames"/>.
    /// <para/>
    /// Bounded to the playable range 1..18. Out of it there is no race, so callers get null rather than a
    /// number: unbounded, <see cref="EqdpFallbackIndex"/>'s catch-all arm made every unknown index look
    /// like a child of Midlander, so <see cref="CanFallThrough"/> waved through pairs like c9101 -> c0101.
    /// A guard that decides how a shell is published should not accept a race that cannot exist.</summary>
    private static int? RaceIndex(string? code)
        => code is { Length: >= 2 } && int.TryParse(code.AsSpan(0, 2), out var n)
        && n > 0 && n <= RaceNames.Length * 2 ? n : null;

    /// <summary>
    /// The race the game falls through to when a set declares no model for <paramref name="n"/>, or 0 at
    /// the root. Mirrors the game's own table (the same one Penumbra.GameData's <c>GenderRace.Fallback</c>
    /// encodes): most races fall to their own gender's Midlander, with three exceptions — Hrothgar males
    /// go to Roegadyn males, Lalafell females to Lalafell males, and Midlander females to Midlander males.
    /// </summary>
    private static int EqdpFallbackIndex(int n) => n switch
    {
        1  => 0,   // Midlander male — the root, nothing below it
        2  => 1,   // Midlander female -> Midlander male
        11 => 1,   // Lalafell male    -> Midlander male
        12 => 11,  // Lalafell female  -> Lalafell male
        15 => 9,   // Hrothgar male    -> Roegadyn male
        _  => n % 2 == 1 ? 1 : 2,
    };

    /// <summary>
    /// Would emptying <paramref name="from"/>'s entry actually land the game on <paramref name="to"/>?
    /// <para/>
    /// The carrier branch in Build empties the WEARER's own EQDP entry to pull the shell into cut space.
    /// That is only sound when cut space is somewhere on the wearer's fall-through chain — otherwise the
    /// empty sends the game somewhere we publish nothing, or (worse) somewhere we publish a shell cut for
    /// a different body, which arrives race-deformed. Two Midlanders reported exactly that: a shell sitting
    /// too low, as if scaled, cleared by a plugin reload. Nothing validated the pair before this.
    /// <para/>
    /// Same gender is required on top of reachability. The chain does contain two cross-gender hops
    /// (Midlander female -> Midlander male, Lalafell female -> Lalafell male), but arriving at one means
    /// the shell was cut from the other gender's body parts — a cutCode vote that is wrong at its source,
    /// not a deform worth inheriting. Emptying c0201 on a Midlander female to land her on c0101 is the
    /// single worst case the guard rejects, and the one the reports match.
    /// </summary>
    internal static bool CanFallThrough(string? from, string? to)
    {
        if (RaceIndex(from) is not { } f || RaceIndex(to) is not { } t) return false;
        if (f % 2 != t % 2) return false;
        // The chain is at most a few hops and always shrinks toward the root; the bound is a guard against
        // a malformed index steering it into a cycle, not a real depth.
        for (int i = 0, cur = f; i < 8; i++)
        {
            cur = EqdpFallbackIndex(cur);
            if (cur == 0) return false;
            if (cur == t) return true;
        }
        return false;
    }

    /// <summary>
    /// Declare whether a set has a model for a given race/gender. Char codes run c0101 = Midlander male,
    /// c0201 = Midlander female, c0301 = Highlander male, and so on.
    /// <para/>
    /// <paramref name="hasModel"/> false is the interesting one: it is how a Midlander-only gear mod reaches
    /// every other race. With a race's entry empty the game walks to that race's PARENT, loads the parent's
    /// model, and applies the racial deform on the way — so declaring "no model for c1401" while publishing
    /// ours at c0201 hands our shell the same deform the character's c0201 body parts already get. Setting
    /// it instead makes the game load the model natively at that race, with no deform at all.
    /// <para/>
    /// The entry used to be a hardcoded 192, which is <c>3 &lt;&lt; 6</c> — right for RFinger and wrong for
    /// every other slot, harmless only while the Emperor's ring was the sole target.
    /// </summary>
    private static object EqdpManipulation(string charCode, string slot, int setId, bool hasModel = true)
    {
        int n = RaceIndex(charCode) ?? 2;
        string race = RaceNames[Math.Clamp((n - 1) / 2, 0, RaceNames.Length - 1)];
        string gender = n % 2 == 1 ? "Male" : "Female";

        return new
        {
            Type = "Eqdp",
            Manipulation = new
            {
                Entry = hasModel ? 3 << (2 * EqdpSlotIndex(slot)) : 0,
                Gender = gender,
                Race = race,
                SetId = setId,
                Slot = slot,
                ShiftedEntry = hasModel ? 3 : 0,
            },
        };
    }
}
