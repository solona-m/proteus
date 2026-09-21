using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

using static Proteus.Services.PenumbraManipulations;

internal static class ContentPieceResolver
{
    /// <summary>
    /// The model of <paramref name="piece"/> for equipment code <paramref name="modelCode"/>: the exact variant, else
    /// the NEAREST one the fall-through chain reaches (same gender at every hop), else null.
    /// </summary>
    /// <remarks>
    /// Returns the code too, since cut space and race-authored models publish differently. An un-keyed piece
    /// reports a NULL code: it never named a race.
    /// </remarks>
    internal static (string? Code, string Path)? ResolveVariant(ContentPiece piece, string? modelCode)
    {
        // ModelCodes is empty in exactly the case ModelFor ignored the code it was given.
        if (piece.ModelFor(modelCode) is { } exact)
            return (piece.ModelCodes.Any() ? modelCode : null, exact);
        if (modelCode == null || RaceIndex(modelCode) is not { } from) return null;

        // An accessory may take the chain's cross-gender hops (the game hands them across genders); a fitted
        // garment may not.
        bool crossGenderOk = IsAccessoryPiece(piece);

        for (int i = 0, cur = from; i < 8; i++)
        {
            cur = EqdpFallbackIndex(cur);
            if (cur == 0) break;
            if (!crossGenderOk && cur % 2 != from % 2) continue;
            foreach (var code in piece.ModelCodes)
                if (RaceIndex(code) == cur)
                    return (code, piece.ModelFor(code)!);
        }
        return null;
    }

    /// <summary>
    /// Is every model this piece ships an accessory? Read off the FILENAME (<c>cNNNNaNNNN</c>), not the folder: the
    /// sidecar stores archive entries, not game paths. Unknown means "not an accessory".
    /// </summary>
    private static bool IsAccessoryPiece(ContentPiece piece)
    {
        var paths = piece.ModelCodes.Select(piece.ModelFor)
            .Concat([piece.Model])
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
        return paths.Count > 0 && paths.All(p => ModelCategory(p) == 'a');
    }

    /// <summary>
    /// The category letter (<c>a</c> accessory, <c>e</c> equipment) of a <c>cNNNNxNNNN_slot.mdl</c> leaf name, or
    /// null when the name is not that shape.
    /// </summary>
    private static char? ModelCategory(string? modelPath)
    {
        if (string.IsNullOrEmpty(modelPath)) return null;

        var leaf = modelPath.Replace('\\', '/');
        var slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];

        // c + four digits + the letter; anything shorter cannot carry a second id and a slot.
        if (leaf.Length < 11 || char.ToLowerInvariant(leaf[0]) != 'c') return null;
        for (int i = 1; i <= 4; i++)
            if (!char.IsAsciiDigit(leaf[i])) return null;
        return char.ToLowerInvariant(leaf[5]);
    }

    /// <summary>
    /// Which IMC attribute bit an attribute NAME answers to, or null. The bit is the part letter (<c>atr_tv_a</c> =
    /// bit 0); other names are body-suppression attributes driven from EQP, never by an IMC mask.
    /// </summary>
    internal static int? PartAttributeBit(string attributeName)
    {
        // at > 0: "_a" on its own is not an attribute.
        int at = attributeName.LastIndexOf('_');
        if (at <= 0 || at != attributeName.Length - 2) return null;
        char letter = char.ToLowerInvariant(attributeName[^1]);
        return letter is >= 'a' and <= 'j' ? letter - 'a' : null;
    }

    /// <summary>
    /// Does this toggle group speak for that model? Set AND slot: packs put several garments on one set. A group
    /// naming no slot, or an unknown one, matches anything.
    /// </summary>
    private static bool Governs(ContentAttributeGroup g, string modelRel)
    {
        var parsed = ContentSlot.Parse(modelRel);
        if (g.SetId >= 0 && (parsed is { } p ? ContentSlot.SetIdOf(p.SetTag) : null) is { } s
            && g.SetId != s)
            return false;

        return g.Slot is not { Length: > 0 } gs
            || ContentSlot.LabelForEquipSlot(gs) is not { } wantLabel
            || parsed is not { } mp
            || string.Equals(mp.Label, wantLabel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether any toggle group governs the model, so Proteus strips its attribute tags
    /// (<see cref="ContentGeometry.OwnAttributes"/>), even when nothing is currently hidden.
    /// </summary>
    internal static bool GovernsModel(IReadOnlyList<ContentAttributeGroup>? groups, string modelRel)
        => groups is { Count: > 0 } && groups.Any(g => Governs(g, modelRel));

    /// <summary>
    /// What the game has toggled on one drawn model (<see cref="Interop.BodyShapeReader.ReadEnabledShapes"/>). The walk
    /// keys by the file LOADED, so: full disk path, full game path, then the unique file-name stem.
    /// </summary>
    internal static HashSet<string>? LiveModelState(
        IReadOnlyDictionary<string, HashSet<string>>? live, string gamePath, string? diskPath)
    {
        if (live == null) return null;
        if (diskPath != null && live.TryGetValue(Interop.BodyShapeReader.PathKey(diskPath), out var byDisk))
            return byDisk;
        if (live.TryGetValue(Interop.BodyShapeReader.PathKey(gamePath), out var byGame))
            return byGame;
        if (diskPath != null && live.TryGetValue(Interop.BodyShapeReader.Stem(diskPath), out var byDiskStem))
            return byDiskStem;
        return live.TryGetValue(Interop.BodyShapeReader.Stem(gamePath), out var byGameStem) ? byGameStem : null;
    }

    /// <summary>
    /// The attribute names a pack's own hide-toggles currently switch off for one of its models, or null when
    /// nothing is hidden. The composite drops the submeshes tagged with them.
    /// </summary>
    /// <param name="selected">The mod's live Penumbra selection: group name → chosen options.</param>
    internal static IReadOnlySet<string>? HiddenAttributes(
        IReadOnlyList<ContentAttributeGroup>? groups, string modelRel, IReadOnlyList<string> attrNames,
        IReadOnlyDictionary<string, List<string>>? selected)
    {
        if (groups is not { Count: > 0 } || attrNames.Count == 0) return null;

        HashSet<string>? hidden = null;
        foreach (var g in groups)
        {
            if (!Governs(g, modelRel)) continue;

            int mask = g.MaskFor(
                selected != null && selected.TryGetValue(g.Group, out var sel) ? sel : null);

            // Matched to its bit BY NAME (PartAttributeBit); a name with no bit is not the mask's to switch.
            foreach (var name in attrNames)
                if (PartAttributeBit(name) is { } bit && (mask & (1 << bit)) == 0)
                    (hidden ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
        }
        return hidden;
    }

    /// <summary>Test seam for <see cref="ResolveVariant"/>.</summary>
    internal static (string? Code, string Path)? ResolveVariantForTest(ContentPiece piece, string? modelCode)
        => ResolveVariant(piece, modelCode);

    /// <summary>
    /// How a resolved content model is published, or null when it cannot be for this wearer: cut space keeps the
    /// declared surface (tested FIRST, so it stays on the shared host), a model at the wearer's own race goes
    /// Native, and any other race is refused.
    /// </summary>
    internal static ShellSurfaceKey? ContentSurface(
        ShellSurfaceKey declared, string? resolvedCode, string? wearerCode, string cutCode)
    {
        // A sidecar that names a surface by hand means it; this only decides for the default.
        if (!declared.IsBody) return declared;

        // One model for everyone: no race to disagree with.
        if (resolvedCode == null) return declared;

        // c0101/c0201 IS cut space, by definition — the game deforms it onto whoever wears it.
        if (ModelRace.IsSharedShape(resolvedCode)) return declared;
        if (string.Equals(resolvedCode, cutCode, StringComparison.OrdinalIgnoreCase)) return declared;
        if (wearerCode != null && string.Equals(resolvedCode, wearerCode, StringComparison.OrdinalIgnoreCase))
            return new ShellSurfaceKey(ShellSurfaceKind.Native, resolvedCode);
        return null;
    }

    /// <summary>
    /// What makes two pieces share one published material (one host slot): everything that decides its bytes, plus
    /// the surface, since different surfaces go to different hosts. Not the model path; see <see cref="ContentGeometryKey"/>.
    /// </summary>
    internal static string ContentUnitKey(
        string modDir, ShellSurfaceKey surface, string mtrlRel, string? rowsJson, string? glowKey = null,
        string? texKey = null)
        => string.Join('\u0000', modDir, surface.ToString(), mtrlRel, rowsJson ?? "-", glowKey ?? "-", texKey ?? "-");

    /// <summary>
    /// What makes two meshes the same within a unit: the RESOLVED model path (never <see cref="ContentPiece.Model"/>,
    /// which the importer leaves empty) and the material.
    /// </summary>
    internal static string ContentGeometryKey(string modelRel, string materialLeaf)
        => modelRel + '\u0000' + materialLeaf;

    /// <summary>
    /// The material names of a model's LOD0 meshes that actually have vertices, in declaration order. Packs often
    /// leave zero-vertex meshes bound to vanilla materials.
    /// </summary>
    internal static List<string> UsedMaterialNames(byte[] model, List<string> declared)
    {
        var used = new List<string>();
        foreach (var name in declared)
        {
            if (used.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var leaf = name;
            if (SecondSkinWriter.TryReadLod0Geometry(model, out var pos, out _, out _,
                    SecondSkinWriter.KeepByLeaf(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { leaf.TrimStart('/') }))
                && pos.Length >= 3)
                used.Add(name);
        }
        return used;
    }

    /// <summary>
    /// The file this pack's own selection supplies for a material, or null when its options say nothing. The LAST
    /// selected group wins (Penumbra's rule); default data (no group) always counts as ticked but ranks below every group.
    /// <para/>
    /// <paramref name="sources"/> is best-ranked first (<c>ContentPiece.SourcesFor</c>), which only matters among the
    /// defaults: several of them can name one leaf under different IMC variant folders, and they DO disagree — a pack
    /// that ships its dressed material at <c>v0002</c> and the vanilla one at <c>v0001</c> is the TexTools
    /// "apply to all variants" shape. The best-ranked default is the one the author actually dressed.
    /// </summary>
    internal static string? SelectedMaterialFile(
        string modRoot, IReadOnlyList<ContentMaterialSource> sources,
        IReadOnlyDictionary<string, List<string>>? selected)
    {
        // Backwards, so the last selected group is found first and the scan can stop there.
        string? fromDefault = null;  // the pack's default data, used only when no group supplies the path

        for (int i = sources.Count - 1; i >= 0; i--)
        {
            var s = sources[i];
            if (s.File.Length == 0) continue;
            bool grouped = s.Group != null;
            if (grouped
             && (selected == null
              || !selected.TryGetValue(s.Group!, out var on)
              || !on.Any(x => string.Equals(x, s.Option, StringComparison.OrdinalIgnoreCase))))
                continue;

            // Constrained to the mod's folder: a manifest value is never traversal-checked, and a rooted one
            // would copy an arbitrary file into the published mod.
            var disk = Path.Combine(modRoot, s.File.Replace('/', Path.DirectorySeparatorChar));
            if (!IsUnder(modRoot, disk) || !File.Exists(disk)) continue;

            if (grouped) return disk;   // the last selected group, reached first — nothing earlier can beat it
            fromDefault = disk;         // overwritten while walking back, so the BEST-ranked default survives
        }

        return fromDefault;
    }

    /// <summary>
    /// The texture files this pack's selection supplies for a material, keyed by game path, by the same rule as
    /// <see cref="SelectedMaterialFile"/>. A path the pack does not ship is absent, so a vanilla texture stays vanilla.
    /// </summary>
    /// <param name="shippedFor">
    /// Every file the pack's manifest puts behind a game path, whatever option holds it. A piece's gate is not the option
    /// that carries its textures, so a piece can be worn with that option unticked: its material is then the importer's
    /// frozen file, and its textures must come from the pack the same way or the material fails to load outright.
    /// </param>
    /// <param name="gameHasFile">
    /// Whether the game's own data holds a file at a path. An unticked texture is only taken over where it does not: a
    /// texture is published at the path the material names, for the whole collection, so taking over a vanilla path would
    /// repaint the real item the user left that option off to keep. There the game's file loads and so does the material.
    /// </param>
    internal static Dictionary<string, string> SelectedTextureFiles(
        string modRoot, ContentPiece piece, byte[] mtrl,
        IReadOnlyDictionary<string, List<string>>? selected,
        Func<string, IReadOnlyList<string>>? shippedFor = null,
        Func<string, bool>? gameHasFile = null)
    {
        var picked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (piece.TextureOptions is not { Count: > 0 } && shippedFor == null) return picked;

        MtrlTexturePaths slots;
        try { slots = TextureLoader.ParseMtrlBytes(mtrl); }
        catch { return picked; }
        if (!slots.Parsed) return picked;

        foreach (var tex in new[] { slots.Diffuse, slots.Normal, slots.Mask, slots.Index })
        {
            if (tex is not { Length: > 0 }) continue;
            var sources = piece.TextureSourcesFor(tex);
            if (sources.Count > 0 && SelectedMaterialFile(modRoot, sources, selected) is { } disk)
            {
                picked[tex] = disk;
                continue;
            }

            // Nothing ticked supplies it: the pack's own file, recorded sources first, then the manifest's. Only for a path
            // the pack invented (see gameHasFile).
            if (gameHasFile?.Invoke(tex) == true) continue;
            var unselected = sources.Select(s => s.File).Concat(shippedFor?.Invoke(tex) ?? [])
                .Where(f => f.Length > 0)
                .Select(f => Path.Combine(modRoot, f.Replace('/', Path.DirectorySeparatorChar)))
                .FirstOrDefault(f => IsUnder(modRoot, f) && File.Exists(f));
            if (unselected != null) picked[tex] = unselected;
        }
        return picked;
    }

    /// <summary>A stable digest of a texture selection for the unit key: different textures are different materials.</summary>
    internal static string TextureKey(Dictionary<string, string> picked)
        => picked.Count == 0
            ? ""
            : string.Join(" ", picked.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                                          .Select(p => p.Key + "=" + p.Value));

    /// <summary>Whether <paramref name="path"/> sits inside <paramref name="root"/>, immune to <c>..</c> and mixed separators.</summary>
    internal static bool IsUnder(string root, string path)
    {
        try
        {
            var rel = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            return !Path.IsPathRooted(rel)
                && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && rel != "..";
        }
        catch { return false; }
    }
}
