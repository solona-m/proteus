using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Proteus.Services;

/// <summary>
/// Read-only parser for Penumbra's <c>.pmp</c> mod packs (a ZIP of the mod folder's manifest files). Both the
/// v4 single <c>meta.json</c> and the older per-group JSON layouts normalise to the same view.
/// </summary>
public static class PenumbraPackage
{
    public const string Extension = ".pmp";

    private const string ManifestEntry = "meta.json";
    private const string LegacyDefaultEntry = "default_mod.json";

    /// <summary>One option of one group.</summary>
    /// <param name="Files">Game path → the ARCHIVE ENTRY backing it, normalised to forward slashes.</param>
    /// <param name="Attributes">
    /// Model attributes this option switches on; an option with no <paramref name="Files"/> and a non-empty
    /// list here is still a real selector.
    /// </param>
    /// <param name="AttributeMask">
    /// For an option of an <c>Imc</c> group: the attribute bits this option turns off when selected; zero elsewhere.
    /// </param>
    /// <param name="Swaps">The game paths this option's <c>FileSwaps</c> replace.</param>
    /// <param name="Manipulations">Every metadata manipulation, of any type, reduced to what it touches.</param>
    public sealed record PackOption(
        string Name, string? Description, IReadOnlyDictionary<string, string> Files,
        IReadOnlyList<string> Attributes, ushort AttributeMask = 0,
        IReadOnlyList<PackEst>? Est = null,
        IReadOnlyList<string>? Swaps = null,
        IReadOnlyList<PackManipulation>? Manipulations = null)
    {
        /// <summary>Never null, so callers can enumerate without a guard.</summary>
        public IReadOnlyList<PackEst> Est { get; init; } = Est ?? [];

        /// <summary>Never null.</summary>
        public IReadOnlyList<string> Swaps { get; init; } = Swaps ?? [];

        /// <summary>Never null.</summary>
        public IReadOnlyList<PackManipulation> Manipulations { get; init; } = Manipulations ?? [];
    }

    /// <summary>
    /// One manipulation of any type, by the model set it edits: <c>SetId</c> (Eqdp, Eqp, Est, Gmp), <c>PrimaryId</c>
    /// (Imc) or <c>Id</c> (Atr, Shp). Null for one that edits no single set — racial scaling, global EQP — and so
    /// applies to every character.
    /// </summary>
    public sealed record PackManipulation(string Type, int? SetId);

    /// <summary>
    /// One <c>Est</c> manipulation: "wearing <paramref name="SetId"/> on <paramref name="Slot"/> loads extra
    /// skeleton <paramref name="Entry"/>". This is what makes a garment's <c>j_ex_*</c> bones exist; an
    /// <paramref name="Entry"/> of 0 means "no extra skeleton".
    /// </summary>
    public sealed record PackEst(string Slot, int SetId, int Entry);

    /// <summary>
    /// One option group. <paramref name="Index"/> is Penumbra's own ordinal (v4 array position or v3 filename
    /// number); lower means higher priority.
    /// </summary>
    /// <param name="Entry">The archive entry this group came from, or null when it was inline in meta.json.</param>
    /// <param name="ImcSetId">
    /// For an <c>Imc</c> group: the equipment set its entry belongs to (<c>PrimaryId</c>), or -1 otherwise.
    /// An Imc group edits the IMC attribute mask (ten bits, by position in the model's attribute table).
    /// </param>
    /// <param name="DefaultAttributeMask">
    /// For an <c>Imc</c> group: the attribute bits set when no option is selected; each selected option clears
    /// its own bits.
    /// </param>
    public sealed record PackGroup(
        string Name, string Type, int Index, IReadOnlyList<PackOption> Options, string? Entry,
        int ImcSetId = -1, string? ImcSlot = null, ushort DefaultAttributeMask = 0);

    /// <summary>A parsed pack.</summary>
    /// <param name="Entries">Every archive entry, normalised, with its uncompressed size.</param>
    /// <param name="DefaultFiles">The always-applied redirects (v4 <c>DefaultData</c> / v3 default_mod.json).</param>
    public sealed record Contents(
        string Path,
        int FileVersion,
        string Name,
        string Author,
        string? Description,
        string? Version,
        string? Website,
        IReadOnlyDictionary<string, long> Entries,
        IReadOnlyDictionary<string, string> DefaultFiles,
        IReadOnlyList<PackGroup> Groups,
        IReadOnlyList<PackEst>? DefaultEst = null)
    {
        /// <summary>The always-applied extra-skeleton entries, gated by no option. Never null.</summary>
        public IReadOnlyList<PackEst> DefaultEst { get; init; } = DefaultEst ?? [];

        /// <summary>Every redirect the pack declares anywhere, default data and every option alike.</summary>
        public IEnumerable<KeyValuePair<string, string>> AllFiles
            => DefaultFiles.Concat(Groups.SelectMany(g => g.Options).SelectMany(o => o.Files));
    }

    /// <summary>
    /// Parse the manifest(s) and entry table of <paramref name="pmpPath"/>. Throws
    /// <see cref="InvalidDataException"/> when the file isn't a readable pack.
    /// </summary>
    public static Contents Read(string pmpPath)
    {
        using var zip = OpenSource(pmpPath);

        var entries = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (raw, length) in zip.List())
        {
            var name = Normalize(raw);
            // Entries are extracted by name, so a traversal entry rejects the whole pack.
            if (System.IO.Path.IsPathRooted(name) || name.Split('/').Any(IsTraversal))
                throw new InvalidDataException($"The pack contains an unsafe entry path: {raw}");
            entries[name] = length;
        }

        var manifestNode = ReadNode(zip, ManifestEntry)
            ?? throw new InvalidDataException("Not a Penumbra pack — it has no meta.json.");
        if (manifestNode is not JsonObject manifest)
            throw new InvalidDataException("The pack's meta.json is not an object.");

        int fileVersion = Int(manifest, "FileVersion") ?? PenumbraModMeta.LegacyFileVersion;

        var defaultFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defaultEst = new List<PackEst>();
        var groups = new List<PackGroup>();

        if (fileVersion >= PenumbraModMeta.SingleFileVersion)
        {
            if (manifest["DefaultData"] is JsonObject dd) { ReadFiles(dd, defaultFiles); ReadEst(dd, defaultEst); }
            if (manifest["Groups"] is JsonArray arr)
                for (int i = 0; i < arr.Count; i++)
                    if (arr[i] is JsonObject g)
                        groups.Add(ReadGroup(g, i, null));
        }
        else
        {
            if (ReadNode(zip, LegacyDefaultEntry) is JsonObject legacy)
            {
                ReadFiles(legacy, defaultFiles);
                ReadEst(legacy, defaultEst);
            }

            // v3 group files, ordered by the number in their name: that number is the group's priority.
            foreach (var entry in entries.Keys.Where(IsLegacyGroupFile)
                         .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                if (ReadNode(zip, entry) is not JsonObject g) continue;
                groups.Add(ReadGroup(g, LegacyGroupNumber(entry) ?? groups.Count, entry));
            }
            groups = groups.OrderBy(g => g.Index).ToList();
        }

        return new Contents(
            pmpPath,
            fileVersion,
            // A folder's name is the mod's, dots and all.
            Str(manifest, "Name") ?? (zip is FolderSource
                ? System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(pmpPath))
                : System.IO.Path.GetFileNameWithoutExtension(pmpPath)),
            Str(manifest, "Author") ?? string.Empty,
            Str(manifest, "Description"),
            Str(manifest, "Version"),
            Str(manifest, "Website"),
            entries,
            defaultFiles,
            groups,
            defaultEst);
    }

    /// <summary>
    /// Several entries in one pass over the archive. Entries the archive doesn't carry are absent from the result.
    /// </summary>
    public static Dictionary<string, byte[]> ReadEntries(string pmpPath, IEnumerable<string> entryNames)
    {
        var wanted = new HashSet<string>(entryNames.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return result;

        using var zip = OpenSource(pmpPath);
        foreach (var (raw, length) in zip.List())
        {
            var name = Normalize(raw);
            if (!wanted.Contains(name)) continue;
            using var src = zip.Open(raw);
            using var mem = new MemoryStream(length > 0 && length < int.MaxValue ? (int)length : 0);
            src.CopyTo(mem);
            result[name] = mem.ToArray();
        }
        return result;
    }

    /// <summary>One archive entry parsed as JSON, or null when it isn't there or isn't JSON.</summary>
    public static JsonNode? ReadJson(string pmpPath, string entryName)
    {
        using var zip = OpenSource(pmpPath);
        return ReadNode(zip, entryName);
    }

    /// <summary>
    /// Whether <paramref name="packPath"/> is still there: a <c>.pmp</c> file, or an installed mod's folder.
    /// </summary>
    public static bool Exists(string packPath) => File.Exists(packPath) || Directory.Exists(packPath);

    /// <summary>Whether <paramref name="packPath"/> names an installed mod's folder rather than a <c>.pmp</c>.</summary>
    public static bool IsFolder(string packPath) => Directory.Exists(packPath);

    /// <summary>
    /// Every entry of the pack written under <paramref name="root"/> in the pack's own layout. An installed mod's
    /// folder is copied, never moved or edited: the import is a new mod beside the original.
    /// </summary>
    public static void ExtractTo(string packPath, string root)
    {
        using var src = OpenSource(packPath);
        foreach (var (raw, _) in src.List())
        {
            var rel = Normalize(raw);
            if (System.IO.Path.IsPathRooted(rel) || rel.Split('/').Any(IsTraversal))
                throw new InvalidDataException($"The pack contains an unsafe entry path: {raw}");
            var dest = System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            src.CopyTo(raw, dest);
        }
    }

    /// <summary>Archive paths use forward slashes; a manifest's file values use backslashes.</summary>
    public static string Normalize(string p) => p.Replace('\\', '/').TrimStart('/');

    // ── internals ────────────────────────────────────────────────────────────

    private static bool IsTraversal(string segment) => segment is ".." or ".";

    private static bool IsLegacyGroupFile(string entry)
        => !entry.Contains('/')
        && entry.StartsWith("group_", StringComparison.OrdinalIgnoreCase)
        && entry.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where a pack's entries come from: a <c>.pmp</c> archive, or an installed mod's folder read in place. Entry
    /// names are as the source spells them; callers normalise.
    /// </summary>
    private abstract class Source : IDisposable
    {
        public abstract IEnumerable<(string Name, long Length)> List();
        public abstract Stream Open(string name);
        public abstract void CopyTo(string name, string dest);
        public virtual void Dispose() { }

        /// <summary>The raw name of <paramref name="entryName"/>, matched case- and slash-insensitively.</summary>
        public virtual string? Find(string entryName)
        {
            var want = Normalize(entryName);
            foreach (var (name, _) in List())
                if (string.Equals(Normalize(name), want, StringComparison.OrdinalIgnoreCase))
                    return name;
            return null;
        }
    }

    private sealed class ZipSource(string path) : Source
    {
        private readonly ZipArchive zip = ZipFile.OpenRead(path);

        public override IEnumerable<(string, long)> List()
            => zip.Entries.Where(e => !e.FullName.EndsWith('/'))   // directory markers
                          .Select(e => (e.FullName, e.Length));

        public override string? Find(string entryName)
            => zip.GetEntry(entryName) is { } e ? e.FullName : base.Find(entryName);

        public override Stream Open(string name) => zip.GetEntry(name)!.Open();
        public override void CopyTo(string name, string dest) => zip.GetEntry(name)!.ExtractToFile(dest, overwrite: true);
        public override void Dispose() => zip.Dispose();
    }

    /// <summary>An installed mod's folder. Only ever read: an import copies it.</summary>
    private sealed class FolderSource(string root) : Source
    {
        public override IEnumerable<(string, long)> List()
            => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Select(f => (System.IO.Path.GetRelativePath(root, f), new FileInfo(f).Length));

        // The filesystem answers directly, case-insensitively on Windows; no walk of a large mod per lookup.
        public override string? Find(string entryName)
        {
            var rel = Normalize(entryName).Replace('/', System.IO.Path.DirectorySeparatorChar);
            return File.Exists(System.IO.Path.Combine(root, rel)) ? rel : null;
        }

        public override Stream Open(string name) => File.OpenRead(System.IO.Path.Combine(root, name));
        public override void CopyTo(string name, string dest)
            => File.Copy(System.IO.Path.Combine(root, name), dest, overwrite: true);
    }

    private static Source OpenSource(string packPath)
        => Directory.Exists(packPath) ? new FolderSource(packPath) : new ZipSource(packPath);

    private static JsonNode? ReadNode(Source zip, string entryName)
    {
        var entry = zip.Find(entryName);
        if (entry == null) return null;
        try
        {
            // Through a StreamReader so a UTF-8 BOM is stripped before parsing.
            using var src = zip.Open(entry);
            using var rd = new StreamReader(src, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return JsonNode.Parse(rd.ReadToEnd());
        }
        catch (JsonException) { return null; }
    }

    private static PackGroup ReadGroup(JsonObject g, int index, string? entry)
    {
        var options = new List<PackOption>();

        // A "Combining" group keeps its redirects in a Containers array, which this importer cannot place; its
        // options come back with no files.
        if (g["Options"] is JsonArray opts)
            foreach (var o in opts)
            {
                if (o is not JsonObject oo) continue;
                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ReadFiles(oo, files);
                var attrs = new List<string>();
                ReadAttributes(oo, attrs);
                var est = new List<PackEst>();
                ReadEst(oo, est);
                options.Add(new PackOption(
                    Str(oo, "Name") ?? string.Empty, Str(oo, "Description"), files, attrs,
                    Mask(oo, "AttributeMask"), est, ReadSwaps(oo), ReadManipulations(oo)));
            }

        // An Imc group's edit lives on the group; read only for that kind.
        bool imc = string.Equals(Str(g, "Type"), "Imc", StringComparison.OrdinalIgnoreCase);
        int setId = -1;
        string? slot = null;
        ushort defaultMask = 0;
        if (imc)
        {
            if (g["Identifier"] is JsonObject id)
            {
                setId = Int(id, "PrimaryId") ?? -1;
                slot = Str(id, "EquipSlot");
            }
            if (g["DefaultEntry"] is JsonObject de) defaultMask = Mask(de, "AttributeMask");
        }

        return new PackGroup(
            Str(g, "Name") ?? string.Empty,
            Str(g, "Type") ?? "Single",
            index,
            options,
            entry,
            setId,
            slot,
            defaultMask);
    }

    /// <summary>One IMC attribute mask field, clamped to the ten bits the format actually carries.</summary>
    private static ushort Mask(JsonObject owner, string key)
        => (ushort)((Int(owner, key) ?? 0) & 0x3FF);

    /// <summary>
    /// The extra-skeleton entries an owner declares (<c>Est</c> manipulations), deduplicated by slot, set and
    /// entry with race dropped. Entries of 0 are kept; the importer drops them.
    /// </summary>
    private static void ReadEst(JsonObject owner, List<PackEst> into)
    {
        if (owner["Manipulations"] is not JsonArray manips) return;
        foreach (var m in manips)
        {
            if (m is not JsonObject mo
                || !string.Equals(Str(mo, "Type"), "Est", StringComparison.OrdinalIgnoreCase)
                || mo["Manipulation"] is not JsonObject inner) continue;

            if (Str(inner, "Slot") is not { Length: > 0 } slot) continue;
            var rec = new PackEst(slot, Int(inner, "SetId") ?? -1, Int(inner, "Entry") ?? 0);
            if (rec.SetId < 0 || into.Contains(rec)) continue;
            into.Add(rec);
        }
    }

    private static List<string> ReadSwaps(JsonObject owner)
        => owner["FileSwaps"] is JsonObject swaps ? [.. swaps.Select(p => Normalize(p.Key))] : [];

    private static List<PackManipulation> ReadManipulations(JsonObject owner)
    {
        var result = new List<PackManipulation>();
        if (owner["Manipulations"] is not JsonArray manips) return result;
        foreach (var m in manips)
        {
            if (m is not JsonObject mo) continue;
            var inner = mo["Manipulation"] as JsonObject;
            // Imc names its set PrimaryId; Atr and Shp name it Id, and leave it null to mean every set.
            int? set = inner == null ? null
                : Int(inner, "SetId") ?? Int(inner, "PrimaryId") ?? Int(inner, "Id");
            result.Add(new PackManipulation(Str(mo, "Type") ?? string.Empty, set));
        }
        return result;
    }

    private static void ReadFiles(JsonObject owner, Dictionary<string, string> into)
    {
        if (owner["Files"] is not JsonObject files) return;
        foreach (var p in files)
            if (p.Value is JsonValue v && v.TryGetValue<string>(out var rel) && !string.IsNullOrWhiteSpace(rel))
                into[p.Key] = Normalize(rel);
    }

    /// <summary>
    /// The model attributes an option switches on — Penumbra's <c>Atr</c> manipulation, by name. Entries
    /// switching an attribute off are not collected.
    /// </summary>
    private static void ReadAttributes(JsonObject owner, List<string> into)
    {
        if (owner["Manipulations"] is not JsonArray manips) return;
        foreach (var m in manips)
        {
            if (m is not JsonObject mo
                || !string.Equals(Str(mo, "Type"), "Atr", StringComparison.OrdinalIgnoreCase)
                || mo["Manipulation"] is not JsonObject inner) continue;

            if (inner["Entry"] is JsonValue e && e.TryGetValue<bool>(out var on) && !on) continue;
            if (Str(inner, "Attribute") is { Length: > 0 } name && !into.Contains(name, StringComparer.Ordinal))
                into.Add(name);
        }
    }

    /// <summary>The NNN out of <c>group_007_fabric.json</c>, or null when the name doesn't carry one.</summary>
    private static int? LegacyGroupNumber(string entryName)
    {
        var name = System.IO.Path.GetFileName(entryName);
        if (!name.StartsWith("group_", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = name["group_".Length..];
        int end = rest.IndexOf('_');
        if (end <= 0) return null;
        return int.TryParse(rest[..end], out var n) ? n : null;
    }

    private static string? Str(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
}
