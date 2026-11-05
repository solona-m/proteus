using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Proteus.Services;

/// <summary>Why a worn piece's variants were not pointed at its mod's material.</summary>
internal enum RepointReason
{
    SeveralVariants, ModImc, ImcGroup, ProteusToggles, OtherModMaterial, FolderUnused, NoImc, MetaUnreadable, Outranked,
    LegacyMod, ProteusPack, Declined, WriteFailed,
}

/// <summary>
/// The "Gear variants" policy, with no game, Penumbra or disk writes of its own: which worn pieces would draw the wrong
/// material (or none) because their mod ships one variant's material folder and the worn variant uses another, and the
/// per-variant IMC entries that point them at the mod's folder. <see cref="VariantRepointWriter"/> writes;
/// <see cref="VariantRepointWatcher"/> schedules.
/// <para/>
/// What is written matches TexTools' metadata tab "Apply to All Variants" (<c>ImcControl.ApplyAllButton_Click</c>), which
/// copies the chosen variant's <c>Mask</c> (attributes and sound, one <c>u16</c>), decal, VFX, material set and animation
/// to every entry of the slot — all six fields. Proteus copies the same six, but on purpose only to the variants that
/// miss the mod's folder and that the mod's own IMC data does not name, and as Penumbra default-data entries (undoable,
/// and outranked by every author option) rather than a rewritten <c>.imc</c>.
/// </summary>
internal static class VariantRepointPlan
{
    // ── the item ────────────────────────────────────────────────────────────

    /// <summary>One gear item: an equipment ('e') or accessory ('a') set and the slot suffix its model loads under.</summary>
    public readonly record struct Item(char Kind, int SetId, string Suffix)
    {
        public string SetTag => $"{Kind}{SetId:D4}";
        public string SlotLabel => ContentSlot.LabelForSuffix(Suffix) ?? Suffix;
        public string ObjectType => Kind == 'a' ? "Accessory" : "Equipment";
        public string EquipSlot => ContentSlot.EquipSlotForLabel(SlotLabel) ?? SlotLabel;
        /// <summary>How the record and its declines name the item: "Equipment/6255/Body".</summary>
        public string Key => $"{ObjectType}/{SetId}/{EquipSlot}";
        /// <summary>For people: "e6255 Body".</summary>
        public string Label => $"{SetTag} {SlotLabel}";
        public string MaterialDir(int materialId)
            => $"chara/{(Kind == 'a' ? "accessory" : "equipment")}/{SetTag}/material/v{materialId:D4}/";
    }

    private static readonly HashSet<string> EquipmentSuffixes = new(StringComparer.OrdinalIgnoreCase)
        { "met", "top", "glv", "dwn", "sho" };
    private static readonly HashSet<string> AccessorySuffixes = new(StringComparer.OrdinalIgnoreCase)
        { "ear", "nek", "wrs", "rir", "ril" };

    /// <summary>The item a worn slot is, or null for anything that is not one of the ten gear slots.</summary>
    public static Item? ItemFor(string suffix, int setId)
    {
        if (setId <= 0) return null;
        if (EquipmentSuffixes.Contains(suffix)) return new Item('e', setId, suffix.ToLowerInvariant());
        if (AccessorySuffixes.Contains(suffix)) return new Item('a', setId, suffix.ToLowerInvariant());
        return null;
    }

    /// <summary>
    /// One worn piece: the item, its variant, and the model it is drawn with (game path and the file it resolved to).
    /// <paramref name="LoadedFolder"/> is the material folder the game actually loaded for it, for QA's cross-check.
    /// </summary>
    public sealed record WornItem(Item Item, int Variant, string? ModelGamePath, string? ModelResolved,
                                  bool Glasses = false, int? LoadedFolder = null);

    // ── which folders a mod dresses ─────────────────────────────────────────

    private static Regex MaterialRe(Item item) => new(
        $@"^chara/{(item.Kind == 'a' ? "accessory" : "equipment")}/{item.SetTag}/material/v(?<v>\d{{4}})/"
      + $@"(?<name>mt_c\d{{4}}{item.SetTag}_{item.Suffix}_[^/]*\.mtrl)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The material folders (the <c>NNNN</c> of <c>vNNNN</c>) a mod's redirects dress for the item, in its default data,
    /// any option or any combining container, active or not; and the material file names under them.
    /// </summary>
    public static (SortedSet<int> Folders, List<string> Names) DressedFolders(IEnumerable<PenumbraModMeta.Redirect> redirects,
                                                                               Item item)
    {
        var re = MaterialRe(item);
        var folders = new SortedSet<int>();
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in redirects)
        {
            var m = re.Match(r.GamePath.Replace('\\', '/'));
            if (!m.Success || !int.TryParse(m.Groups["v"].Value, out var v) || v <= 0) continue;
            folders.Add(v);
            names.Add(m.Groups["name"].Value.ToLowerInvariant());
        }
        return (folders, [.. names]);
    }

    // ── reading IMC edits ───────────────────────────────────────────────────

    /// <summary>One <c>Imc</c> manipulation, read whatever way it is spelled.</summary>
    public readonly record struct ImcEdit(Item Item, int Variant, ImcEntry Entry);

    /// <summary>
    /// An <c>Imc</c> manipulation as Proteus or Penumbra spells it (numbers possibly as strings, the entry with or without
    /// <c>AttributeAndSound</c>), for a gear item; null for anything else.
    /// </summary>
    public static ImcEdit? ParseImc(JsonElement m)
    {
        try
        {
            if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("Type", out var t)
                || !string.Equals(t.GetString(), "Imc", StringComparison.OrdinalIgnoreCase)
                || !m.TryGetProperty("Manipulation", out var x) || x.ValueKind != JsonValueKind.Object)
                return null;

            char kind = ObjectKind(x);
            if (kind == '\0' || Num(x, "PrimaryId") is not { } set || Num(x, "Variant") is not { } variant) return null;
            if (!x.TryGetProperty("EquipSlot", out var es)) return null;
            string? slotName = es.ValueKind == JsonValueKind.String ? es.GetString()
                             : es.ValueKind == JsonValueKind.Number ? EquipSlotName(es.GetInt32()) : null;
            if (slotName == null || ContentSlot.LabelForEquipSlot(slotName) is not { } label) return null;
            if (SuffixForLabel(label) is not { } suffix) return null;

            if (!x.TryGetProperty("Entry", out var e) || e.ValueKind != JsonValueKind.Object) return null;
            int? both = Num(e, "AttributeAndSound");
            int mask = Num(e, "AttributeMask") ?? (both ?? 0) & 0x3FF;
            int sound = Num(e, "SoundId") ?? (both ?? 0) >> 10;
            var entry = new ImcEntry((byte)(Num(e, "MaterialId") ?? 0), (byte)(Num(e, "DecalId") ?? 0),
                                     (ushort)(mask & 0x3FF), (byte)(sound & 0x3F), (byte)(Num(e, "VfxId") ?? 0),
                                     (byte)(Num(e, "MaterialAnimationId") ?? 0));
            return new ImcEdit(new Item(kind, set, suffix), variant, entry);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return null; }
    }

    private static char ObjectKind(JsonElement x)
    {
        if (!x.TryGetProperty("ObjectType", out var o)) return '\0';
        if (o.ValueKind == JsonValueKind.String)
            return o.GetString() switch
            {
                { } s when s.Equals("Equipment", StringComparison.OrdinalIgnoreCase) => 'e',
                { } s when s.Equals("Accessory", StringComparison.OrdinalIgnoreCase) => 'a',
                _ => '\0',
            };
        if (o.ValueKind == JsonValueKind.Number) return o.GetInt32() switch { 11 => 'e', 3 => 'a', _ => '\0' };
        return '\0';
    }

    private static string? EquipSlotName(int n) => n switch
    {
        3 => "Head", 4 => "Body", 5 => "Hands", 7 => "Legs", 8 => "Feet",
        9 => "Ears", 10 => "Neck", 11 => "Wrists", 12 => "RFinger", 14 => "LFinger", _ => null,
    };

    private static string? SuffixForLabel(string label) => label switch
    {
        "Head" => "met", "Body" => "top", "Hands" => "glv", "Legs" => "dwn", "Feet" => "sho",
        "Earrings" => "ear", "Necklace" => "nek", "Bracelets" => "wrs", "Right ring" => "rir", "Left ring" => "ril",
        _ => null,
    };

    private static int? Num(JsonElement o, string key)
    {
        if (!o.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    /// <summary>
    /// Every <c>Imc</c> manipulation in a v4 manifest, with where it sits: default data (<c>IsDefault</c>), an option or a
    /// combining container. Empty when the manifest is unreadable.
    /// </summary>
    public static List<(ImcEdit Edit, bool IsDefault, JsonElement Raw)> ManifestImcEdits(string modRoot)
    {
        var found = new List<(ImcEdit, bool, JsonElement)>();
        try
        {
            var path = Path.Combine(modRoot, PenumbraModMeta.MetaFile);
            if (!File.Exists(path)) return found;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("DefaultData", out var dd)) Add(dd, true);
            if (root.TryGetProperty("Groups", out var gs) && gs.ValueKind == JsonValueKind.Array)
                foreach (var g in gs.EnumerateArray())
                {
                    if (g.ValueKind != JsonValueKind.Object) continue;
                    foreach (var list in new[] { "Options", "Containers" })
                        if (g.TryGetProperty(list, out var opts) && opts.ValueKind == JsonValueKind.Array)
                            foreach (var o in opts.EnumerateArray()) Add(o, false);
                }

            void Add(JsonElement owner, bool isDefault)
            {
                if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty("Manipulations", out var ms)
                    || ms.ValueKind != JsonValueKind.Array)
                    return;
                foreach (var m in ms.EnumerateArray())
                    if (ParseImc(m) is { } edit) found.Add((edit, isDefault, m.Clone()));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return found;
    }

    // ── the manifest fingerprint ────────────────────────────────────────────

    /// <summary>
    /// The mod's content as far as an author update goes: the manifest's version, default-data files and every group's
    /// name, type and options' (and containers') files, canonically spelled. Manipulations and layout keys are left out,
    /// so our own entries and Penumbra's re-saves never change it. "" when unreadable.
    /// </summary>
    public static string Fingerprint(string modRoot)
    {
        try
        {
            var path = Path.Combine(modRoot, PenumbraModMeta.MetaFile);
            if (!File.Exists(path)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var sb = new StringBuilder();
            sb.Append("V:").Append(root.TryGetProperty("Version", out var ver) ? ver.ToString() : "").Append('\n');
            if (root.TryGetProperty("DefaultData", out var dd)) Files(dd, "D");
            if (root.TryGetProperty("Groups", out var gs) && gs.ValueKind == JsonValueKind.Array)
                foreach (var g in gs.EnumerateArray())
                {
                    if (g.ValueKind != JsonValueKind.Object) continue;
                    sb.Append("G:").Append(Str(g, "Name")).Append('|').Append(Str(g, "Type")).Append('\n');
                    if (g.TryGetProperty("Options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                        foreach (var o in opts.EnumerateArray())
                        {
                            sb.Append("O:").Append(Str(o, "Name")).Append('\n');
                            Files(o, "F");
                        }
                    if (g.TryGetProperty("Containers", out var cs) && cs.ValueKind == JsonValueKind.Array)
                        foreach (var c in cs.EnumerateArray()) Files(c, "C");
                }
            return ShaderUpgradePlan.Hash16(Encoding.UTF8.GetBytes(sb.ToString()));

            void Files(JsonElement owner, string tag)
            {
                if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty("Files", out var f)
                    || f.ValueKind != JsonValueKind.Object)
                    return;
                foreach (var line in f.EnumerateObject()
                                      .Select(p => Canon(p.Name) + "=" + (p.Value.ValueKind == JsonValueKind.String
                                                                              ? Canon(p.Value.GetString()!) : ""))
                                      .OrderBy(s => s, StringComparer.Ordinal))
                    sb.Append(tag).Append(':').Append(line).Append('\n');
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return ""; }

        static string Canon(string s) => s.Replace('\\', '/').ToLowerInvariant();
        static string Str(JsonElement o, string k)
            => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()! : "";
    }

    // ── planning ────────────────────────────────────────────────────────────

    public readonly record struct Left(string ModDir, string Item, RepointReason Reason);

    /// <summary>How a group in the mod covers the item: not at all, a Proteus part-toggle group, or the author's.</summary>
    internal enum Cover { None, ProteusToggles, Author }

    /// <summary>
    /// What the mod itself says about the item's IMC, minus Proteus's own recorded entries: the variants it names
    /// anywhere, its default-data entry per variant, a group covering the item, and variants only a pinned group names.
    /// </summary>
    public sealed record Claims(IReadOnlySet<int> Named, IReadOnlyDictionary<int, ImcEntry> OwnDefault, Cover Cover,
                                IReadOnlySet<int> Pinned);

    /// <summary>A worn piece from one mod that dresses exactly one folder of the item.</summary>
    public sealed record Candidate(string ModRoot, WornItem Worn, int Folder, IReadOnlyList<string> MaterialNames,
                                   ImcEntry[] Vanilla, Claims Claims, IReadOnlySet<int> Ours, bool Declined)
    {
        public Item Item => Worn.Item;
        public string ModDir => Path.GetFileName(ModRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        public int Count => Vanilla.Length - 1;
        /// <summary>The set the game uses for the worn variant: its own, or the default set (0) for one it does not declare.</summary>
        public int WornIndex => Worn.Variant >= 1 && Worn.Variant <= Count ? Worn.Variant : 0;
    }

    /// <summary>The entries to write for one item.</summary>
    public sealed record ItemWrite(Item Item, int Folder, IReadOnlyList<(int Variant, ImcEntry Entry)> Add);

    public sealed record ModWrites(string ModRoot, List<ItemWrite> Items);

    public sealed record Plan(List<ModWrites> PerMod, List<Left> Left);

    /// <summary>Everything planning reads, injected so it needs no game.</summary>
    public sealed class Inputs
    {
        public required string? ModsRoot { get; init; }
        public required string? ManagedRoot { get; init; }
        /// <summary>Reads a game path from the game's own data, or null.</summary>
        public required Func<string, byte[]?> ReadGameFile { get; init; }
        public Func<string, bool> IsProteusPack { get; init; } = ShaderUpgradePlan.IsProteusPack;
        public Func<string, MeshToggleRecord?> ReadToggleRecord { get; init; } = MeshToggleService.ReadRecord;
        public Func<string, VariantRepointWriter.Record?> ReadRecord { get; init; } = VariantRepointWriter.ReadRecord;
    }

    /// <summary>
    /// The worn pieces worth a look: drawn with a model from a mod that dresses exactly one material folder of the
    /// item, with the game's entries and the mod's own IMC claims read. Pieces left for a reason that holds whatever is
    /// worn (several folders, a v3 folder, a Proteus pack, no readable <c>.imc</c>) are listed; the rest are judged in
    /// <see cref="Build"/>, once the collection's entries are known.
    /// </summary>
    public static (List<Candidate> Candidates, List<Left> Left) Candidates(IEnumerable<WornItem> worn, Inputs inp)
    {
        var candidates = new List<Candidate>();
        var left = new List<Left>();
        var redirectsCache = new Dictionary<string, List<PenumbraModMeta.Redirect>>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in worn)
        {
            if (w.Glasses || w.ModelGamePath == null) continue;
            var src = ShaderUpgradePlan.Classify(w.ModelGamePath, w.ModelResolved, inp.ModsRoot, inp.ManagedRoot,
                                                 inp.IsProteusPack);
            if (src == ShaderUpgradePlan.Source.ProteusPack)
            {
                if (ModRootOf(w.ModelResolved!, inp.ModsRoot) is { } packRoot)
                    left.Add(new Left(DirOf(packRoot), w.Item.Label, RepointReason.ProteusPack));
                continue;
            }
            if (src != ShaderUpgradePlan.Source.Mod || ModRootOf(w.ModelResolved!, inp.ModsRoot) is not { } modRoot)
                continue;

            if (PenumbraModMeta.IsLegacyFolder(modRoot))
            {
                left.Add(new Left(DirOf(modRoot), w.Item.Label, RepointReason.LegacyMod));
                continue;
            }
            if (!redirectsCache.TryGetValue(modRoot, out var redirects))
                redirectsCache[modRoot] = redirects = PenumbraModMeta.ReadAllRedirects(modRoot);
            var (folders, names) = DressedFolders(redirects, w.Item);
            if (folders.Count == 0) continue;   // a model-only mod dresses whatever variant is worn
            if (folders.Count > 1)
            {
                left.Add(new Left(DirOf(modRoot), w.Item.Label, RepointReason.SeveralVariants));
                continue;
            }
            if (ImcEntrySource.GameEntries(inp.ReadGameFile, w.Item.SetTag, w.Item.SlotLabel) is not { } vanilla)
            {
                left.Add(new Left(DirOf(modRoot), w.Item.Label, RepointReason.NoImc));
                continue;
            }

            var record = inp.ReadRecord(modRoot);
            var ours = OursOf(record, w.Item);
            candidates.Add(new Candidate(modRoot, w, folders.Min, names, vanilla,
                                         ClaimsOf(modRoot, w.Item, w.Variant, record, inp), ours,
                                         record?.Declined.Contains(w.Item.Key, StringComparer.OrdinalIgnoreCase) == true));
        }
        return (candidates, left);
    }

    private static string DirOf(string modRoot)
        => Path.GetFileName(modRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static string? ModRootOf(string resolved, string? modsRoot)
        => !string.IsNullOrEmpty(modsRoot)
           && HatCompatService.InMods(ShaderUpgradePlan.StripPenumbraPrefix(resolved), modsRoot, out var root, out _)
            ? root : null;

    /// <summary>The variants of the item Proteus's record says it wrote (the record as reconciled: present and equal).</summary>
    private static HashSet<int> OursOf(VariantRepointWriter.Record? record, Item item)
        => record?.Find(item) is { } e ? e.Variants.Select(v => v.Variant).ToHashSet() : [];

    /// <summary>The mod's own IMC claims on the item, with Proteus's recorded entries taken out.</summary>
    internal static Claims ClaimsOf(string modRoot, Item item, int wornVariant, VariantRepointWriter.Record? record,
                                    Inputs inp)
    {
        var mine = record?.Find(item);
        var named = new HashSet<int>();
        var own = new Dictionary<int, ImcEntry>();
        foreach (var (edit, isDefault, _) in ManifestImcEdits(modRoot))
        {
            if (edit.Item != item) continue;
            if (mine != null && mine.Variants.Any(v => v.Variant == edit.Variant && v.Entry.ToImc() == edit.Entry))
                continue;   // ours, not the author's
            named.Add(edit.Variant);
            if (isDefault) own[edit.Variant] = edit.Entry;
        }

        var cover = Cover.None;
        var pinned = new HashSet<int>();
        var toggles = inp.ReadToggleRecord(modRoot);
        foreach (var g in ImcEntrySource.ImcGroups(modRoot))
        {
            if (!GroupNames(g.Group, item, out bool allVariants, out int variant)) continue;
            bool covers = allVariants || variant == wornVariant;
            if (!covers)
            {
                pinned.Add(variant);
                continue;
            }
            bool isToggles = toggles?.Items.Any(t => t.AdoptedGroup != true && t.SetId == item.SetId
                                                     && string.Equals(t.Slot, item.EquipSlot, StringComparison.OrdinalIgnoreCase)
                                                     && string.Equals(t.GroupName, g.Name, StringComparison.OrdinalIgnoreCase))
                             == true;
            if (isToggles) { if (cover == Cover.None) cover = Cover.ProteusToggles; }
            else cover = Cover.Author;
        }
        return new Claims(named, own, cover, pinned);
    }

    /// <summary>Whether an <c>Imc</c> group edits this item; and whether for every variant or one.</summary>
    private static bool GroupNames(JsonElement group, Item item, out bool allVariants, out int variant)
    {
        allVariants = group.TryGetProperty("AllVariants", out var av) && av.ValueKind == JsonValueKind.True;
        variant = -1;
        if (!group.TryGetProperty("Identifier", out var id) || id.ValueKind != JsonValueKind.Object) return false;
        if (Num(id, "PrimaryId") != item.SetId) return false;
        if (id.TryGetProperty("ObjectType", out _) && ObjectKind(id) != item.Kind) return false;
        if (id.TryGetProperty("EquipSlot", out var es))
        {
            // Penumbra writes the name; a hand-edited number is read too, and anything else never matches.
            string? slot = es.ValueKind == JsonValueKind.String ? es.GetString()
                         : es.ValueKind == JsonValueKind.Number && es.TryGetInt32(out var n) ? EquipSlotName(n) : null;
            if (!string.Equals(slot, item.EquipSlot, StringComparison.OrdinalIgnoreCase)) return false;
        }
        variant = Num(id, "Variant") ?? 0;
        return true;
    }

    /// <summary>The effective entry of one variant: the collection's, else the game's own.</summary>
    public static ImcEntry Effective(Candidate c, IReadOnlyDictionary<PenumbraMetaImc.Key, ImcEntry> effective, int variant)
        => effective.TryGetValue(new PenumbraMetaImc.Key(c.Item.Kind, c.Item.SetId, c.Item.SlotLabel, variant), out var e)
            ? e : c.Vanilla[variant];

    /// <summary>The material paths the candidates' variants would load, for Penumbra to resolve before <see cref="Build"/>.</summary>
    public static List<string> ResolveRequests(IEnumerable<Candidate> candidates,
                                               IReadOnlyDictionary<PenumbraMetaImc.Key, ImcEntry> effective)
    {
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
            for (int v = 0; v <= c.Count; v++)
            {
                int m = Effective(c, effective, v).MaterialId;
                if (m == c.Folder) continue;
                foreach (var name in c.MaterialNames) paths.Add(c.Item.MaterialDir(m) + name);
            }
        return [.. paths];
    }

    internal enum VariantState { Dressed, OtherMod, Wrong }

    /// <summary>
    /// Whether a variant draws the mod's material: dressed when it uses the mod's folder, or when its own folder's
    /// material resolves into the mod itself (a file swap); served elsewhere when any of the mod's material names under
    /// its folder resolves into another mod, Proteus's output or outside the mods root; otherwise wrong — the game's own
    /// file, or no file at all (a custom-named material the game does not have).
    /// </summary>
    public static VariantState StateOf(Candidate c, ImcEntry entry, IReadOnlyDictionary<string, string?> resolved,
                                       Inputs inp)
    {
        if (entry.MaterialId == c.Folder) return VariantState.Dressed;
        bool other = false;
        foreach (var name in c.MaterialNames)
        {
            var path = c.Item.MaterialDir(entry.MaterialId) + name;
            resolved.TryGetValue(path, out var r);
            var src = ShaderUpgradePlan.Classify(path, r, inp.ModsRoot, inp.ManagedRoot, inp.IsProteusPack);
            if (src == ShaderUpgradePlan.Source.Game) continue;
            if (src is ShaderUpgradePlan.Source.Mod or ShaderUpgradePlan.Source.ProteusPack
                && ModRootOf(r!, inp.ModsRoot) is { } root
                && string.Equals(ShaderUpgradePlan.NormalPath(root), ShaderUpgradePlan.NormalPath(c.ModRoot),
                                 StringComparison.OrdinalIgnoreCase))
                return VariantState.Dressed;
            other = true;
        }
        return other ? VariantState.OtherMod : VariantState.Wrong;
    }

    /// <summary>
    /// The variant the mod dresses: the lowest variant (1 up) whose entry — the mod's own default-data entry, else the
    /// game's — uses the mod's folder; null when none does.
    /// </summary>
    public static int? DressedVariant(Candidate c)
    {
        for (int v = 1; v <= c.Count; v++)
            if (DressedEntryAt(c, v).MaterialId == c.Folder) return v;
        return null;
    }

    private static ImcEntry DressedEntryAt(Candidate c, int v)
        => c.Claims.OwnDefault.TryGetValue(v, out var own) ? own : c.Vanilla[v];

    /// <summary>
    /// The entry copied to every variant written: the dressed variant's whole entry (all six fields, as TexTools copies
    /// them), taken from the mod's own default data or the game — never another mod's edit. Null when no variant uses
    /// the mod's folder.
    /// </summary>
    public static ImcEntry? DressedEntry(Candidate c) => DressedVariant(c) is { } v ? DressedEntryAt(c, v) : null;

    /// <summary>
    /// The writes for the candidates, and a reason for every worn piece left that would show the wrong material.
    /// <paramref name="effective"/> is the collection's IMC (null when unreadable: nothing is written);
    /// <paramref name="resolved"/> what each requested material path resolves to; <paramref name="refreshPending"/> the
    /// mods written whose reload and redraw have not settled, which get no "outranked" verdict yet.
    /// </summary>
    public static Plan Build(IEnumerable<Candidate> candidates,
                             IReadOnlyDictionary<PenumbraMetaImc.Key, ImcEntry>? effective,
                             IReadOnlyDictionary<string, string?> resolved, ISet<string> refreshPending, Inputs inp,
                             List<Left>? leftSoFar = null)
    {
        var left = leftSoFar ?? [];
        var perMod = new Dictionary<string, ModWrites>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (effective == null)
            {
                left.Add(new Left(c.ModDir, c.Item.Label, RepointReason.MetaUnreadable));
                continue;
            }
            int w = c.WornIndex;
            var wornState = StateOf(c, Effective(c, effective, w), resolved, inp);
            if (wornState == VariantState.Dressed) continue;

            RepointReason? reason = c.Claims.Cover switch
            {
                Cover.ProteusToggles => RepointReason.ProteusToggles,
                Cover.Author => RepointReason.ImcGroup,
                _ => null,
            };
            reason ??= c.Claims.Named.Contains(w) ? RepointReason.ModImc
                     : wornState == VariantState.OtherMod ? RepointReason.OtherModMaterial
                     : c.Declined ? RepointReason.Declined
                     : null;
            if (reason == null && DressedEntry(c) == null) reason = RepointReason.FolderUnused;
            if (reason is { } r)
            {
                left.Add(new Left(c.ModDir, c.Item.Label, r));
                continue;
            }

            if (c.Ours.Contains(w))
            {
                // Written already, yet the worn variant still misses: another mod outranks it — unless the reload and
                // redraw after our own write have not settled, when the collection's entries are not to be trusted yet.
                if (!refreshPending.Contains(ShaderUpgradePlan.NormalPath(c.ModRoot)))
                    left.Add(new Left(c.ModDir, c.Item.Label, RepointReason.Outranked));
                continue;
            }

            var entry = DressedEntry(c)!.Value;
            var add = new List<(int, ImcEntry)>();
            for (int v = 0; v <= c.Count; v++)
            {
                if (c.Claims.Named.Contains(v) || c.Claims.Pinned.Contains(v) || c.Ours.Contains(v)) continue;
                if (StateOf(c, Effective(c, effective, v), resolved, inp) != VariantState.Wrong) continue;
                add.Add((v, entry));
            }
            if (add.Count == 0) continue;
            if (!perMod.TryGetValue(c.ModRoot, out var mw)) perMod[c.ModRoot] = mw = new ModWrites(c.ModRoot, []);
            if (mw.Items.Any(i => i.Item == c.Item)) continue;   // the same piece worn twice (both rings of one set are two items)
            mw.Items.Add(new ItemWrite(c.Item, c.Folder, add));
        }
        return new Plan([.. perMod.Values], left);
    }

    // ── signals, timing ─────────────────────────────────────────────────────

    /// <summary>A walk waits while auto refit has anything going, or the gear shader upgrade is running: all three write into worn mods.</summary>
    public static bool MayWalk(bool refitRunning, int refitQueued, bool refitWalkDue, bool refitUndoing, bool shaderBusy)
        => ShaderUpgradePlan.MayWalk(refitRunning, refitQueued, refitWalkDue, refitUndoing) && !shaderBusy;

    internal enum RunKind { Repoint, RepointNow, UndoWorn }

    /// <summary>An automatic repoint follows auto redraw; the user's buttons always redraw.</summary>
    public static bool RedrawAfter(RunKind kind, bool autoRedraw, bool changedAny)
        => changedAny && (kind != RunKind.Repoint || autoRedraw);

    internal static string ReasonName(RepointReason r) => r switch
    {
        RepointReason.SeveralVariants => "several-variants",
        RepointReason.ModImc => "mod-imc",
        RepointReason.ImcGroup => "imc-group",
        RepointReason.ProteusToggles => "proteus-toggles",
        RepointReason.OtherModMaterial => "other-mod-material",
        RepointReason.FolderUnused => "folder-unused",
        RepointReason.NoImc => "no-imc",
        RepointReason.MetaUnreadable => "meta-unreadable",
        RepointReason.Outranked => "outranked",
        RepointReason.LegacyMod => "legacy-mod",
        RepointReason.ProteusPack => "proteus-pack",
        RepointReason.Declined => "declined",
        _ => "write-failed",
    };

    internal static string KindName(RunKind k) => k switch
    {
        RunKind.UndoWorn => "undo-worn",
        RunKind.RepointNow => "repoint-now",
        _ => "repoint",
    };
}
