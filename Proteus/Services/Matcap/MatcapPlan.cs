using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Proteus.Services.Matcap;

/// <summary>
/// The matcap tool's decisions, pure: which models take a matcap, which parts may be ticked, what the new material is
/// called and every game path it must be published at, and which of a model's sibling files line up.
/// </summary>
internal static partial class MatcapPlan
{
    public enum Kind { Gear, Hair, Ear, Face, Tail }

    /// <summary>The attribute that marks a face model's horn submeshes (Au Ra). Only these face parts take a matcap.</summary>
    public const string HornAttribute = "atr_hrn";

    /// <summary>A model the tool can edit: its kind, and for gear the item.</summary>
    public sealed record Target(Kind Kind, string GamePath, VariantRepointPlan.Item? Item, ushort Race, int Id);

    /// <summary>The marker every matcap material name carries: <c>&lt;stem&gt;_mcap&lt;token&gt;&lt;N&gt;.mtrl</c>.</summary>
    public const string Marker = "_mcap";

    /// <summary>What the model at <paramref name="gamePath"/> is, or null with the reason code (face, tail, other).</summary>
    public static Target? TargetOf(string gamePath, out string refusal)
    {
        refusal = "";
        var p = gamePath.Replace('\\', '/').ToLowerInvariant();
        if (CustomizationMaterialPath.Parse(p) is { } cust)
            return new Target(cust.Slot switch
            {
                CustomizationMaterialPath.Slot.Face => Kind.Face,
                CustomizationMaterialPath.Slot.Tail => Kind.Tail,
                _ => Kind.Ear,
            }, p, null, cust.Race, cust.Id);
        if (HairPortPlan.Parse(p) is { } hair) return new Target(Kind.Hair, p, null, hair.Race, hair.Id);
        if (ContentSlot.Parse(p) is { } slot && ContentSlot.SetIdOf(slot.SetTag) is { } set
            && (p.StartsWith("chara/equipment/", StringComparison.Ordinal) || p.StartsWith("chara/accessory/", StringComparison.Ordinal)))
        {
            var suffix = Path.GetFileNameWithoutExtension(p);
            suffix = suffix[(suffix.LastIndexOf('_') + 1)..];
            if (VariantRepointPlan.ItemFor(suffix, set) is { } item)
                return new Target(Kind.Gear, p, item, ushort.TryParse(slot.RaceCode, out var r) ? r : (ushort)0, set);
        }
        refusal = "not-target";
        return null;
    }

    // ── parts ───────────────────────────────────────────────────────────────

    /// <summary>A part drawn with body skin (the toggles' and brush's own test), which a matcap never takes.</summary>
    public static bool IsSkin(ModelPart part) => SecondSkinWriter.SkinMaterialBodyType(part.Material) != null;

    /// <summary>A part already drawn by one of Proteus's matcap materials (an Apply on it changes that matcap).</summary>
    public static bool IsOnMatcap(ModelPart part) => IsMatcapName(part.Material);

    public static bool IsMatcapName(string material) => McapRe().IsMatch(material);

    /// <summary>Whether a part may be ticked under the Matcaps tool: anything but skin (a matcap part to change it).</summary>
    public static bool Tickable(ModelPart part) => !IsSkin(part);

    /// <summary>What Apply does with the ticked parts.</summary>
    public enum ApplyKind
    {
        /// <summary>Nothing ticked.</summary>
        None,
        /// <summary>No ticked part has a matcap: a new matcap material.</summary>
        New,
        /// <summary>Every ticked part has one: those matcaps change in place.</summary>
        Change,
        /// <summary>Some have one and some don't: refused (two writes in one click are not atomic).</summary>
        Mixed,
    }

    public static ApplyKind KindOfApply(IReadOnlyList<ModelPart> ticked)
    {
        if (ticked.Count == 0) return ApplyKind.None;
        int on = ticked.Count(IsOnMatcap);
        return on == 0 ? ApplyKind.New : on == ticked.Count ? ApplyKind.Change : ApplyKind.Mixed;
    }

    /// <summary>The matcap materials among the ticked parts, each once (case-insensitive), in the order first seen.</summary>
    public static IReadOnlyList<string> ChangeNames(IReadOnlyList<ModelPart> ticked) =>
        ticked.Where(IsOnMatcap).Select(p => p.Material).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// <see cref="Tickable(ModelPart)"/>, and on a face model only a horn part (its submesh carries <see cref="HornAttribute"/>,
    /// looked up by name): the face's skin, eyes and the rest never take a matcap.
    /// </summary>
    public static bool Tickable(ModelPart part, Kind? kind, IReadOnlyList<string> attributeNames) =>
        Tickable(part) && (kind != Kind.Face || IsHorn(part, attributeNames));

    /// <summary>Whether a part's submesh carries the horn attribute.</summary>
    public static bool IsHorn(ModelPart part, IReadOnlyList<string> attributeNames)
    {
        int bit = -1;
        for (int i = 0; i < attributeNames.Count; i++)
            if (string.Equals(attributeNames[i], HornAttribute, StringComparison.Ordinal)) { bit = i; break; }
        return bit is >= 0 and < 32 && (part.AttributeMask & (1u << bit)) != 0;
    }

    /// <summary>Whether any part of the model is a horn.</summary>
    public static bool HasHorns(IReadOnlyList<ModelPart> parts, IReadOnlyList<string> attributeNames) =>
        parts.Any(p => IsHorn(p, attributeNames));

    // ── names ───────────────────────────────────────────────────────────────

    /// <summary>The per-mod token in material names: 6 hex characters of SHA-256 of the lowercased mod folder name.</summary>
    public static string TokenFor(string modFolder) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modFolder.ToLowerInvariant())))[..6].ToLowerInvariant();

    /// <summary>
    /// The new material's name for parts drawn with <paramref name="sourceMaterial"/>: its stem plus
    /// <c>_mcap&lt;token&gt;&lt;N&gt;</c>, N the smallest number not in <paramref name="taken"/> (names in the model,
    /// the record and the mod's redirects, compared by file name). An absolute source name keeps its folder.
    /// </summary>
    /// <param name="clashes">A name whose game paths are already taken (another material published there): skipped
    /// too, so two models reading one folder never end up with one material redirect.</param>
    public static string NewName(string sourceMaterial, string token, IEnumerable<string> taken, Func<string, bool>? clashes = null)
    {
        var used = new HashSet<string>(taken.Select(FileNameOf), StringComparer.OrdinalIgnoreCase);
        var src = sourceMaterial.Replace('\\', '/');
        // A name already carrying a matcap marker takes the stem before it.
        int mark = src.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        var stem = src.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) ? src[..^5] : src;
        if (mark > 0 && IsMatcapName(src)) stem = src[..mark];
        for (int n = 1; ; n++)
        {
            var name = $"{stem}{Marker}{token}{n}.mtrl";
            if (!used.Contains(FileNameOf(name)) && clashes?.Invoke(name) != true) return name;
        }
    }

    private static string FileNameOf(string nameOrPath)
    {
        var p = nameOrPath.Replace('\\', '/');
        return p[(p.LastIndexOf('/') + 1)..];
    }

    // ── material game paths ─────────────────────────────────────────────────

    /// <summary>
    /// Every game path the new material must be published at, so the model finds it wherever it is drawn.
    /// <list type="bullet">
    /// <item>Gear: <c>…/material/vNNNN/&lt;name&gt;</c> for every material folder the item's game IMC, the mod's IMC
    /// edits and groups, and the mod's own material redirects use, plus the worn variant's — an unredirected sister
    /// variant would otherwise ask for a file that does not exist and draw the parts invisible.</item>
    /// <item>Hair: the folder <see cref="HairMaterialPath"/> says each race the model is published for reads.</item>
    /// <item>Ears, tails and faces (horns): <see cref="CustomizationMaterialPath"/>, for each published path.</item>
    /// </list>
    /// An absolute name is published at exactly that path.
    /// </summary>
    /// <param name="publishedAt">Every game path the model file is published at in the mod.</param>
    /// <param name="gearFolders">The material folders for gear (see <see cref="GearFolders"/>).</param>
    public static List<string> MaterialGamePaths(Target target, string name, IEnumerable<string> publishedAt,
                                                 IEnumerable<int>? gearFolders)
    {
        var paths = new List<string>();
        void Add(string? p)
        {
            if (p == null) return;
            p = p.Replace('\\', '/').ToLowerInvariant();
            if (!paths.Contains(p)) paths.Add(p);
        }
        if (!name.StartsWith('/'))
        {
            Add(name);
            return paths;
        }
        switch (target.Kind)
        {
            case Kind.Gear:
                foreach (var f in (gearFolders ?? []).Where(f => f > 0).Distinct().Order())
                    Add($"{target.Item!.Value.MaterialDir(f).TrimEnd('/')}{name}");
                break;
            case Kind.Hair:
                foreach (var gp in publishedAt.Prepend(target.GamePath))
                    if (HairPortPlan.Parse(gp) is { } h) Add(HairMaterialPath.GamePath(h.Race, h.Id, name));
                break;
            case Kind.Ear:
            case Kind.Tail:
            case Kind.Face:
                foreach (var gp in publishedAt.Prepend(target.GamePath))
                    foreach (var p in CustomizationMaterialPath.GamePaths(gp, name)) Add(p);
                break;
        }
        return paths;
    }

    /// <summary>
    /// The material folders a gear item may be drawn from: every variant's in the game's IMC, every folder the mod's IMC
    /// edits or IMC groups name for the item, every folder the mod's own material redirects already dress, and the worn
    /// variant's.
    /// </summary>
    public static SortedSet<int> GearFolders(VariantRepointPlan.Item item, ImcEntry[]? game,
                                             IEnumerable<ImcEntry> modImc, IEnumerable<string> modGamePaths, int? wornFolder)
    {
        var folders = new SortedSet<int>();
        foreach (var e in game ?? []) if (e.MaterialId > 0) folders.Add(e.MaterialId);
        foreach (var e in modImc) if (e.MaterialId > 0) folders.Add(e.MaterialId);
        foreach (var f in VariantRepointPlan.DressedFolders(modGamePaths, item).Folders) folders.Add(f);
        if (wornFolder is > 0) folders.Add(wornFolder.Value);
        if (folders.Count == 0) folders.Add(1);
        return folders;
    }

    /// <summary>The mod's own IMC entries for the item: manipulations anywhere and IMC groups.</summary>
    public static List<ImcEntry> ModImcEntries(string modRoot, VariantRepointPlan.Item item)
    {
        var list = VariantRepointPlan.ManifestImcEdits(modRoot).Where(e => e.Edit.Item == item).Select(e => e.Edit.Entry).ToList();
        foreach (var g in ImcEntrySource.ImcGroups(modRoot))
            if (g.Group.TryGetProperty("Identifier", out var id) && id.TryGetProperty("PrimaryId", out var pid)
                && pid.TryGetInt32(out var set) && set == item.SetId && ImcEntrySource.EntryOf(g.Group) is { } e)
                list.Add(e);
        return list;
    }

    // ── regenerated models ──────────────────────────────────────────────────

    /// <summary>
    /// Which of Proteus's regenerated outputs the model file is (a size-set or refit output, a hair port, an earless
    /// copy), or null. A remake writes the file again from its source, dropping a matcap put on it.
    /// </summary>
    public static string? Regenerated(string modRoot, string rel)
    {
        var norm = rel.Replace('\\', '/').ToLowerInvariant();
        if (norm.StartsWith(HairPortPlan.Folder.ToLowerInvariant() + "/", StringComparison.Ordinal)) return HairPortKind;
        if (norm.StartsWith(EarlessHairPlan.Folder.ToLowerInvariant() + "/", StringComparison.Ordinal)) return "earless";
        if (SizeSetRefit.IsOurFile(SizeSetRefit.OurFiles(BodyRetargetWriter.ReadRecord(modRoot)), rel)) return RefitKind;
        return null;
    }

    /// <summary><see cref="Regenerated"/>'s kind for a size-set or refit output.</summary>
    public const string RefitKind = "refit";

    /// <summary><see cref="Regenerated"/>'s kind for one of Proteus's hair ports.</summary>
    public const string HairPortKind = "hair-port";

    /// <summary>
    /// Why the model file can't take a matcap, or null. Refits take one: they are seldom remade, and a remake writes the
    /// file again from its source (Undo then forgets the remade file; the next Apply there retires the old backup). Hair
    /// ports take one too: a matcapped port counts as edited (<see cref="HairPortWriter.EditedBy"/>), so Proteus no
    /// longer remakes or undoes it until the matcap is undone. Earless copies are still refused: they are shared across
    /// places and remade from the author's file, which is where their matcap belongs.
    /// </summary>
    public static string? RefusedRegenerated(string modRoot, string rel) =>
        Regenerated(modRoot, rel) is { } kind && kind is not (RefitKind or HairPortKind) ? kind : null;

    // ── siblings ────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether <paramref name="chosen"/> (read from the open file) line up in <paramref name="sibling"/>'s parts: the
    /// same mesh, submesh, island, ordinals, material and mask — the toggles' rule, so the same split means the same
    /// geometry there.
    /// </summary>
    public static bool LinesUp(IReadOnlyList<ModelPart> chosen, IReadOnlyList<ModelPart> sibling) =>
        chosen.All(c => sibling.Any(s => PartClaims.LineUp(c, s)));

    [GeneratedRegex(@"_mcap[0-9a-f]{6}\d+\.mtrl$", RegexOptions.IgnoreCase)]
    private static partial Regex McapRe();
}
