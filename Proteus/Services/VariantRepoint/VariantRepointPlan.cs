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
    SeveralVariants, ModImc, OtherModMaterial, NoImc, MetaUnreadable, Outranked,
    LegacyMod, ProteusPack, Declined, WriteFailed,
}

/// <summary>
/// The "Gear variants" policy, with no game, Penumbra or disk writes of its own: which worn pieces would draw the wrong
/// material (or none) because their mod ships one variant's material folder and the worn variant uses another, and the
/// copies of the mod's own material redirects that fix them. <see cref="VariantRepointWriter"/> writes;
/// <see cref="VariantRepointWatcher"/> schedules.
/// <para/>
/// TexTools' metadata tab "Apply to All Variants" (<c>ImcControl.ApplyAllButton_Click</c>) copies the chosen variant's
/// whole IMC entry (<c>Mask</c> = attributes and sound, decal, VFX, material set, animation) to every variant. Proteus
/// fixes the material only, and without IMC (the owner's decision): it copies the mod's <c>…/vN/&lt;name&gt;</c>
/// redirects to <c>…/vM/&lt;name&gt;</c> in the same containers, so Penumbra resolves them at the mod's priority — the
/// ordering that picks the model. An IMC entry would apply wherever the mod is enabled, even where another mod's model
/// wins. Each variant keeps its own parts, VFX and sound.
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

    /// <summary>The material folder and file name a game path dresses for the item, or null for any other path.</summary>
    public static (int Folder, string Name)? MaterialOf(string gamePath, Item item)
    {
        var m = MaterialRe(item).Match(gamePath.Replace('\\', '/'));
        return m.Success && int.TryParse(m.Groups["v"].Value, out var v) && v > 0
            ? (v, m.Groups["name"].Value.ToLowerInvariant()) : null;
    }

    /// <summary>
    /// The material folders (the <c>NNNN</c> of <c>vNNNN</c>) a mod's redirects dress for the item, in its default data,
    /// any option or any combining container, active or not; and the material file names under them.
    /// </summary>
    public static (SortedSet<int> Folders, List<string> Names) DressedFolders(IEnumerable<string> gamePaths, Item item)
    {
        var folders = new SortedSet<int>();
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in gamePaths)
            if (MaterialOf(g, item) is { } m)
            {
                folders.Add(m.Folder);
                names.Add(m.Name);
            }
        return (folders, [.. names]);
    }

    /// <summary>A material game path moved to another folder, keeping its own spelling otherwise.</summary>
    public static string InFolder(string gamePath, int folder)
        => Regex.Replace(gamePath, @"([/\\]material[/\\]v)\d{4}([/\\])", m => m.Groups[1].Value + folder.ToString("D4") + m.Groups[2].Value,
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
    /// so our own entries and Penumbra's re-saves never change it. Proteus's own redirect copies are left out too
    /// (<paramref name="exclude"/>: each skipped where it sits, matched case- and slash-insensitively), so a copy written
    /// or removed never changes it either. "" when unreadable.
    /// </summary>
    public static string Fingerprint(string modRoot, IReadOnlyCollection<PenumbraModMeta.ContainerRedirect>? exclude = null)
    {
        try
        {
            var path = Path.Combine(modRoot, PenumbraModMeta.MetaFile);
            if (!File.Exists(path)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var sb = new StringBuilder();
            sb.Append("V:").Append(root.TryGetProperty("Version", out var ver) ? ver.ToString() : "").Append('\n');
            if (root.TryGetProperty("DefaultData", out var dd)) Files(dd, "D", new PenumbraModMeta.ContainerRef(""));
            if (root.TryGetProperty("Groups", out var gs) && gs.ValueKind == JsonValueKind.Array)
                foreach (var g in gs.EnumerateArray())
                {
                    if (g.ValueKind != JsonValueKind.Object) continue;
                    var group = Str(g, "Name");
                    sb.Append("G:").Append(group).Append('|').Append(Str(g, "Type")).Append('\n');
                    if (g.TryGetProperty("Options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                        foreach (var o in opts.EnumerateArray())
                        {
                            sb.Append("O:").Append(Str(o, "Name")).Append('\n');
                            Files(o, "F", new PenumbraModMeta.ContainerRef(group, Str(o, "Name")));
                        }
                    if (g.TryGetProperty("Containers", out var cs) && cs.ValueKind == JsonValueKind.Array)
                    {
                        int i = 0;
                        foreach (var c in cs.EnumerateArray()) Files(c, "C", new PenumbraModMeta.ContainerRef(group, null, i++));
                    }
                }
            return ShaderUpgradePlan.Hash16(Encoding.UTF8.GetBytes(sb.ToString()));

            void Files(JsonElement owner, string tag, PenumbraModMeta.ContainerRef at)
            {
                if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty("Files", out var f)
                    || f.ValueKind != JsonValueKind.Object)
                    return;
                foreach (var line in f.EnumerateObject()
                                      .Where(p => exclude == null || !exclude.Any(x => x.Container.SameAs(at)
                                          && PenumbraModMeta.SameFile(x.GamePath, p.Name)
                                          && p.Value.ValueKind == JsonValueKind.String
                                          && PenumbraModMeta.SameFile(x.File, p.Value.GetString()!)))
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

    /// <summary>
    /// A worn piece from one mod X that dresses exactly one material folder of the item. <see cref="Sources"/> are X's
    /// own redirects under that folder (Proteus's copies and released keys left out), each in its container: what is
    /// copied. <see cref="Present"/> are Proteus's recorded copies for the item that the reconcile found in X.
    /// </summary>
    public sealed record Candidate(string ModRoot, WornItem Worn, int Folder, IReadOnlyList<string> MaterialNames,
                                   IReadOnlyList<PenumbraModMeta.ContainerRedirect> Sources,
                                   IReadOnlyList<PenumbraModMeta.ContainerRedirect> AllRedirects,
                                   ImcEntry[] Vanilla, IReadOnlyDictionary<int, HashSet<int>> AuthorFolders,
                                   IReadOnlyList<VariantRepointWriter.RedirectEntry> Present, bool Declined)
    {
        public Item Item => Worn.Item;
        public string ModDir => Path.GetFileName(ModRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        public int Count => Vanilla.Length - 1;
        /// <summary>The set the game uses for the worn variant: its own, or the default set (0) for one it does not declare.</summary>
        public int WornIndex => Worn.Variant >= 1 && Worn.Variant <= Count ? Worn.Variant : 0;
        /// <summary>The folders Proteus has already copied into.</summary>
        public IEnumerable<int> CopiedFolders => Present.Select(p => p.Folder).Distinct();
        /// <summary>Whether some author claim sends a variant to this folder.</summary>
        public bool Claimed(int folder) => AuthorFolders.Values.Any(s => s.Contains(folder));
    }

    /// <summary>
    /// The copies to write for one item: X's own redirects under <see cref="Folder"/> repeated under each target folder,
    /// in the same container, pointing at the same file. <see cref="Variants"/> are the variants those folders fix.
    /// </summary>
    public sealed record ItemWrite(Item Item, int Folder, IReadOnlyList<(int Folder, PenumbraModMeta.ContainerRedirect Copy)> Copies,
                                   IReadOnlyList<int> Variants)
    {
        public IReadOnlyList<int> Folders => Copies.Select(c => c.Folder).Distinct().Order().ToList();
    }

    public sealed record ModWrites(string ModRoot, List<ItemWrite> Items);

    public sealed record Plan(List<ModWrites> PerMod, List<Left> Left);

    /// <summary>Everything planning reads, injected so it needs no game. One instance lives for one walk (its caches).</summary>
    public sealed class Inputs
    {
        public required string? ModsRoot { get; init; }
        public required string? ManagedRoot { get; init; }
        /// <summary>Reads a game path from the game's own data, or null.</summary>
        public required Func<string, byte[]?> ReadGameFile { get; init; }
        public Func<string, bool> IsProteusPack { get; init; } = ShaderUpgradePlan.IsProteusPack;
        public Func<string, VariantRepointWriter.Record?> ReadRecord { get; init; } = VariantRepointWriter.ReadRecord;
        /// <summary>Every redirect of a mod with its container: <see cref="PenumbraModMeta.ReadContainerRedirects"/>.</summary>
        public Func<string, List<PenumbraModMeta.ContainerRedirect>> ReadContainerRedirects { get; init; }
            = PenumbraModMeta.ReadContainerRedirects;

        private readonly Dictionary<string, List<PenumbraModMeta.ContainerRedirect>> redirects = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A mod's redirects, read once per walk.</summary>
        public List<PenumbraModMeta.ContainerRedirect> RedirectsOf(string modRoot)
        {
            var key = ShaderUpgradePlan.NormalPath(modRoot);
            lock (redirects)
            {
                if (!redirects.TryGetValue(key, out var r)) redirects[key] = r = ReadContainerRedirects(modRoot);
                return r;
            }
        }
    }

    /// <summary>Whether a mod redirects exactly this model game path (case- and slash-insensitive), anywhere in it.</summary>
    public static bool RedirectsPath(IEnumerable<PenumbraModMeta.ContainerRedirect> redirects, string gamePath)
        => redirects.Any(r => PenumbraModMeta.SameFile(r.GamePath, gamePath));

    /// <summary>Whether a resolved file lies in this mod folder, however Penumbra spells it (prefix, slashes, case).</summary>
    public static bool SameMod(string? resolved, string modRoot, string? modsRoot)
        => resolved != null && ModRootOf(resolved, modsRoot) is { } root
           && string.Equals(ShaderUpgradePlan.NormalPath(root), ShaderUpgradePlan.NormalPath(modRoot),
                            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The worn pieces worth a look: drawn with a model from a mod that dresses exactly one material folder of the
    /// item (Proteus's own copies and released keys not counted), with the game's entries read. Pieces left for a reason
    /// that holds whatever is worn (several folders, a v3 folder, a Proteus pack, no readable <c>.imc</c>) are listed; the
    /// rest are judged in <see cref="Build"/>, once the collection's entries are known. Read after the reconcile.
    /// </summary>
    public static (List<Candidate> Candidates, List<Left> Left) Candidates(IEnumerable<WornItem> worn, Inputs inp)
    {
        var candidates = new List<Candidate>();
        var left = new List<Left>();
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
            var record = inp.ReadRecord(modRoot);
            var entry = record?.Find(w.Item);
            var all = inp.RedirectsOf(modRoot);
            // X's own redirects: Proteus's copies and the keys it released (theirs now, but not a deliberate folder) out.
            var own = all.Where(r => !(entry?.Covers(r) ?? false)).ToList();
            var (folders, names) = DressedFolders(own.Select(r => r.GamePath), w.Item);
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

            int folder = folders.Min;
            var sources = own.Where(r => MaterialOf(r.GamePath, w.Item) is { } m && m.Folder == folder).ToList();
            var present = entry?.Redirects.ToList() ?? [];
            candidates.Add(new Candidate(modRoot, w, folder, names, sources, all, vanilla,
                                         AuthorFolders(modRoot, w.Item, vanilla, entry, folder), present,
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

    /// <summary>
    /// The variants the author deliberately sends to a material folder other than the game's: X's own IMC data — default
    /// data, any option (active or not), or a non-<c>OnlyAttributes</c> Imc group's entry — naming a MaterialId that
    /// differs from the game's entry for that variant. Proteus's legacy IMC entries do not count, and neither do edits
    /// that change only attributes or groups that change only attributes (those keep the game's folder).
    /// </summary>
    /// <returns>Variant → the folder the author sends it to. A claim on the mod's own folder is left out: the author
    /// already agrees with the fix (W1).</returns>
    internal static Dictionary<int, HashSet<int>> AuthorFolders(string modRoot, Item item, ImcEntry[] vanilla,
                                                                VariantRepointWriter.ItemEntry? ours, int ownFolder)
    {
        var set = new Dictionary<int, HashSet<int>>();
        void Claim(int v, int folder)
        {
            if (!set.TryGetValue(v, out var s)) set[v] = s = [];
            s.Add(folder);
        }
        foreach (var (edit, _, _) in ManifestImcEdits(modRoot))
        {
            if (edit.Item != item || edit.Variant < 0 || edit.Variant >= vanilla.Length) continue;
            if (ours != null && ours.Variants.Any(v => v.Variant == edit.Variant && v.Entry.ToImc() == edit.Entry)) continue;
            if (edit.Entry.MaterialId != vanilla[edit.Variant].MaterialId && edit.Entry.MaterialId != ownFolder)
                Claim(edit.Variant, edit.Entry.MaterialId);
        }
        foreach (var g in ImcEntrySource.ImcGroups(modRoot))
        {
            if (g.Group.TryGetProperty("OnlyAttributes", out var oa) && oa.ValueKind == JsonValueKind.True) continue;
            if (!GroupNames(g.Group, item, out bool allVariants, out int variant)) continue;
            if (ImcEntrySource.EntryOf(g.Group) is not { } e) continue;
            for (int v = 0; v < vanilla.Length; v++)
                if ((allVariants || v == variant) && e.MaterialId != vanilla[v].MaterialId && e.MaterialId != ownFolder)
                    Claim(v, e.MaterialId);
        }
        return set;
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

    /// <summary>X's material names under one folder, as game paths.</summary>
    private static IEnumerable<string> NamesIn(Candidate c, int folder) => c.MaterialNames.Select(n => c.Item.MaterialDir(folder) + n);

    /// <summary>
    /// The material paths to resolve before <see cref="Build"/>: X's names under its own folder, and under every other
    /// folder the item's variants use.
    /// </summary>
    public static List<string> ResolveRequests(IEnumerable<Candidate> candidates,
                                               IReadOnlyDictionary<PenumbraMetaImc.Key, ImcEntry> effective)
    {
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            foreach (var p in NamesIn(c, c.Folder)) paths.Add(p);
            for (int v = 0; v <= c.Count; v++)
            {
                int m = Effective(c, effective, v).MaterialId;
                if (m != c.Folder && m != 0) foreach (var p in NamesIn(c, m)) paths.Add(p);
            }
        }
        return [.. paths];
    }

    internal enum VariantState { Dressed, OtherMod, Wrong }

    /// <summary>
    /// What a variant folder of the item draws on X's model. Dressed: it is X's folder, or one of X's names there
    /// resolves into X (a file swap, an earlier copy). Served elsewhere: a name resolves into a mod that does not replace
    /// the model path X wins (a retexture, possibly an add-on for X), a Proteus pack, Proteus's output or outside the mods
    /// root. Wrong: every name is the game's (a vanilla file, or none at all), or comes from a competing mod that also
    /// replaces that model path and lost it to X — made for its own model, not this one.
    /// </summary>
    public static VariantState StateOf(Candidate c, int folder, IReadOnlyDictionary<string, string?> resolved, Inputs inp)
    {
        if (folder == c.Folder) return VariantState.Dressed;
        bool other = false;
        foreach (var path in NamesIn(c, folder))
        {
            resolved.TryGetValue(path, out var r);
            var src = ShaderUpgradePlan.Classify(path, r, inp.ModsRoot, inp.ManagedRoot, inp.IsProteusPack);
            if (src == ShaderUpgradePlan.Source.Game) continue;
            if (SameMod(r, c.ModRoot, inp.ModsRoot)) return VariantState.Dressed;
            if (src == ShaderUpgradePlan.Source.Mod && ModRootOf(r!, inp.ModsRoot) is { } y
                && RedirectsPath(inp.RedirectsOf(y), c.Worn.ModelGamePath!))
                continue;   // a competing replacement of the same model: wrong on ours, like the game's
            other = true;
        }
        return other ? VariantState.OtherMod : VariantState.Wrong;
    }

    /// <summary>
    /// The copies a folder still lacks: each of X's own redirects under its folder, repeated under <paramref name="folder"/>
    /// in the same container, unless that container already holds the key (ours from before, or the author's: never
    /// overwritten).
    /// </summary>
    internal static IEnumerable<PenumbraModMeta.ContainerRedirect> MissingCopies(Candidate c, int folder)
    {
        foreach (var s in c.Sources)
        {
            var path = InFolder(s.GamePath, folder);
            if (c.AllRedirects.Any(x => x.Container.SameAs(s.Container) && PenumbraModMeta.SameFile(x.GamePath, path)))
                continue;
            yield return new PenumbraModMeta.ContainerRedirect(s.Container, path, s.File);
        }
    }

    /// <summary>
    /// The mods to reload after a run: those whose manifest a write or a reconcile changed. None once disposed: there is
    /// no framework hop to make.
    /// </summary>
    public static List<string> ReloadSet(IEnumerable<string> written, IEnumerable<string> reconciled, bool disposed)
        => disposed ? [] : written.Union(reconciled, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Whether X's own folder is X's: at least one of its names there resolves into X (N2).</summary>
    public static bool ServesOwnFolder(Candidate c, IReadOnlyDictionary<string, string?> resolved, Inputs inp)
        => NamesIn(c, c.Folder).Any(p => resolved.TryGetValue(p, out var r) && SameMod(r, c.ModRoot, inp.ModsRoot));

    /// <summary>
    /// The copies for the candidates, and a reason for every worn piece left that would show the wrong material.
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
            int wornFolder = Effective(c, effective, w).MaterialId;
            // The worn variant names material 0, a folder the game never loads: no copy can change what is worn.
            if (wornFolder == 0) continue;
            var wornState = StateOf(c, wornFolder, resolved, inp);
            if (wornState == VariantState.Dressed) continue;

            RepointReason? reason = null;
            // The author sends the worn variant, or the folder it uses, somewhere on purpose (W2).
            bool wornCopied = c.CopiedFolders.Contains(wornFolder);
            if (c.AuthorFolders.ContainsKey(w) || c.Claimed(wornFolder)) reason = RepointReason.ModImc;
            else if (wornCopied && !MissingCopies(c, wornFolder).Any())
            {
                // Copied already, every copy still there, yet the worn folder still isn't ours: another mod outranks the
                // copy — unless the reload and redraw after our own write have not settled, when what resolves is not
                // to be trusted yet.
                if (!refreshPending.Contains(ShaderUpgradePlan.NormalPath(c.ModRoot)))
                    left.Add(new Left(c.ModDir, c.Item.Label, RepointReason.Outranked));
                continue;
            }
            else if ((!wornCopied && wornState == VariantState.OtherMod) || !ServesOwnFolder(c, resolved, inp))
                reason = RepointReason.OtherModMaterial;
            else if (c.Declined) reason = RepointReason.Declined;
            if (reason is { } r)
            {
                left.Add(new Left(c.ModDir, c.Item.Label, r));
                continue;
            }

            var targets = new SortedDictionary<int, List<int>>();   // folder → the variants it fixes
            for (int v = 0; v <= c.Count; v++)
            {
                if (c.AuthorFolders.ContainsKey(v)) continue;
                int m = Effective(c, effective, v).MaterialId;
                // MaterialId 0 names no folder the game loads (in-game QA: loadedFolder stays null): a v0000 copy would
                // be a dead edit to the author's manifest.
                if (m == 0 || m == c.Folder || c.Claimed(m)) continue;
                if (!targets.TryGetValue(m, out var vs))
                {
                    // A folder copied into before is judged by its missing copies alone (a source added since); any
                    // other by what it draws now.
                    if (c.CopiedFolders.Contains(m) ? !MissingCopies(c, m).Any()
                                                    : StateOf(c, m, resolved, inp) != VariantState.Wrong)
                        continue;
                    targets[m] = vs = [];
                }
                vs.Add(v);
            }
            var copies = targets.Keys.SelectMany(m => MissingCopies(c, m).Select(x => (m, x))).ToList();
            if (copies.Count == 0) continue;
            if (!perMod.TryGetValue(c.ModRoot, out var mw)) perMod[c.ModRoot] = mw = new ModWrites(c.ModRoot, []);
            if (mw.Items.Any(i => i.Item == c.Item)) continue;
            mw.Items.Add(new ItemWrite(c.Item, c.Folder, copies, targets.Values.SelectMany(v => v).Order().ToList()));
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
        RepointReason.OtherModMaterial => "other-mod-material",
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
