using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Proteus.Services;

/// <summary>
/// Reader for Penumbra's <c>.pmp</c> mod packs — a plain ZIP holding the same manifest files a mod folder
/// does, at the archive root.
/// <para/>
/// Two layouts exist and both are read, exactly as <see cref="PenumbraModMeta"/> reads them on disk:
/// FileVersion 4 and above put everything in <c>meta.json</c> (a <c>Groups</c> array and a
/// <c>DefaultData</c> object), while older packs ship <c>default_mod.json</c> plus one
/// <c>group_NNN_name.json</c> per group. What the caller gets back is the same normalised view either way,
/// so nothing downstream has to know which it was.
/// <para/>
/// Nothing here writes or extracts. <see cref="ContentImportService"/> decides what to copy and what the
/// resulting Proteus sidecar says.
/// </summary>
public static class PenumbraPackage
{
    public const string Extension = ".pmp";

    private const string ManifestEntry = "meta.json";
    private const string LegacyDefaultEntry = "default_mod.json";

    /// <summary>One option of one group.</summary>
    /// <param name="Files">Game path → the ARCHIVE ENTRY backing it, normalised to forward slashes.</param>
    /// <param name="Attributes">
    /// Model attributes this option switches on, if any. A pack whose pieces live in one model toggles them
    /// by name rather than redirecting files, so an option with no <paramref name="Files"/> and a non-empty
    /// list here is still a real selector — see <see cref="ReadAttributes"/>.
    /// </param>
    public sealed record PackOption(
        string Name, string? Description, IReadOnlyDictionary<string, string> Files,
        IReadOnlyList<string> Attributes);

    /// <summary>
    /// One option group. <paramref name="Index"/> is Penumbra's own ordinal — the position in the v4
    /// <c>Groups</c> array, or the number in a v3 <c>group_NNN_*.json</c> filename — and lower means higher
    /// priority, which is the order <c>SidecarDiscoveryService.ReadGroupOrder</c> reads back once the pack
    /// is on disk.
    /// </summary>
    /// <param name="Entry">The archive entry this group came from, or null when it was inline in meta.json.</param>
    public sealed record PackGroup(
        string Name, string Type, int Index, IReadOnlyList<PackOption> Options, string? Entry);

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
        IReadOnlyList<PackGroup> Groups)
    {
        /// <summary>Every redirect the pack declares anywhere, default data and every option alike.</summary>
        public IEnumerable<KeyValuePair<string, string>> AllFiles
            => DefaultFiles.Concat(Groups.SelectMany(g => g.Options).SelectMany(o => o.Files));
    }

    /// <summary>
    /// Parse the manifest(s) and entry table of <paramref name="pmpPath"/>. Throws
    /// <see cref="InvalidDataException"/> when the file isn't a readable pack — the caller turns that into
    /// a user-facing message.
    /// </summary>
    public static Contents Read(string pmpPath)
    {
        using var zip = ZipFile.OpenRead(pmpPath);

        var entries = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;   // directory marker
            var name = Normalize(e.FullName);
            // This importer DOES extract by entry name — a pack's own folder layout is preserved in the mod
            // folder, because its manifest refers to files by that layout. So a traversal entry could
            // genuinely escape, and the whole pack is rejected rather than half-imported.
            if (System.IO.Path.IsPathRooted(name) || name.Split('/').Any(IsTraversal))
                throw new InvalidDataException($"The pack contains an unsafe entry path: {e.FullName}");
            entries[name] = e.Length;
        }

        var manifestNode = ReadNode(zip, ManifestEntry)
            ?? throw new InvalidDataException("Not a Penumbra pack — it has no meta.json.");
        if (manifestNode is not JsonObject manifest)
            throw new InvalidDataException("The pack's meta.json is not an object.");

        int fileVersion = Int(manifest, "FileVersion") ?? PenumbraModMeta.LegacyFileVersion;

        var defaultFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<PackGroup>();

        if (fileVersion >= PenumbraModMeta.SingleFileVersion)
        {
            if (manifest["DefaultData"] is JsonObject dd) ReadFiles(dd, defaultFiles);
            if (manifest["Groups"] is JsonArray arr)
                for (int i = 0; i < arr.Count; i++)
                    if (arr[i] is JsonObject g)
                        groups.Add(ReadGroup(g, i, null));
        }
        else
        {
            if (ReadNode(zip, LegacyDefaultEntry) is JsonObject legacy) ReadFiles(legacy, defaultFiles);

            // v3 group files, ordered by the number in their name — that number IS the group's priority,
            // and a zip's entry order is not guaranteed to follow it.
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
            Str(manifest, "Name") ?? System.IO.Path.GetFileNameWithoutExtension(pmpPath),
            Str(manifest, "Author") ?? string.Empty,
            Str(manifest, "Description"),
            Str(manifest, "Version"),
            Str(manifest, "Website"),
            entries,
            defaultFiles,
            groups);
    }

    /// <summary>
    /// Several entries in ONE pass over the archive. Opening a zip per file is fine for a handful; a pack
    /// with dozens of mesh options is read entirely on the frame the user picked it, and there each open
    /// re-reads the central directory. Entries the archive doesn't carry are simply absent from the result.
    /// </summary>
    public static Dictionary<string, byte[]> ReadEntries(string pmpPath, IEnumerable<string> entryNames)
    {
        var wanted = new HashSet<string>(entryNames.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return result;

        using var zip = ZipFile.OpenRead(pmpPath);
        foreach (var e in zip.Entries)
        {
            var name = Normalize(e.FullName);
            if (wanted.Contains(name)) result[name] = ReadAll(e);
        }
        return result;
    }

    /// <summary>One archive entry parsed as JSON, or null when it isn't there or isn't JSON.</summary>
    public static JsonNode? ReadJson(string pmpPath, string entryName)
    {
        using var zip = ZipFile.OpenRead(pmpPath);
        return ReadNode(zip, entryName);
    }

    /// <summary>Archive paths use forward slashes; a manifest's file values use backslashes.</summary>
    public static string Normalize(string p) => p.Replace('\\', '/').TrimStart('/');

    // ── internals ────────────────────────────────────────────────────────────

    private static bool IsTraversal(string segment) => segment is ".." or ".";

    private static bool IsLegacyGroupFile(string entry)
        => !entry.Contains('/')
        && entry.StartsWith("group_", StringComparison.OrdinalIgnoreCase)
        && entry.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static ZipArchiveEntry? Find(ZipArchive zip, string entryName)
        => zip.GetEntry(entryName)
        ?? zip.Entries.FirstOrDefault(e =>
               string.Equals(Normalize(e.FullName), Normalize(entryName), StringComparison.OrdinalIgnoreCase));

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var src = entry.Open();
        using var mem = new MemoryStream(entry.Length > 0 && entry.Length < int.MaxValue ? (int)entry.Length : 0);
        src.CopyTo(mem);
        return mem.ToArray();
    }

    private static JsonNode? ReadNode(ZipArchive zip, string entryName)
    {
        var entry = Find(zip, entryName);
        if (entry == null) return null;
        try
        {
            // Through a StreamReader rather than straight off the stream: Penumbra writes these with a
            // UTF-8 BOM often enough that a raw parse fails on the very first character, and the reader
            // strips it. The files are kilobytes, so materialising them costs nothing.
            using var src = entry.Open();
            using var rd = new StreamReader(src, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return JsonNode.Parse(rd.ReadToEnd());
        }
        catch (JsonException) { return null; }
    }

    private static PackGroup ReadGroup(JsonObject g, int index, string? entry)
    {
        var options = new List<PackOption>();

        // A "Combining" group keeps its redirects in a parallel Containers array rather than on the
        // options, which are bare flag labels there. Nothing in this importer can place those — a container
        // describes one COMBINATION of flags, not a selectable option — so its options are read for their
        // names and come back with no files, which reports as "nothing importable" rather than a wrong guess.
        if (g["Options"] is JsonArray opts)
            foreach (var o in opts)
            {
                if (o is not JsonObject oo) continue;
                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ReadFiles(oo, files);
                var attrs = new List<string>();
                ReadAttributes(oo, attrs);
                options.Add(new PackOption(
                    Str(oo, "Name") ?? string.Empty, Str(oo, "Description"), files, attrs));
            }

        return new PackGroup(
            Str(g, "Name") ?? string.Empty,
            Str(g, "Type") ?? "Single",
            index,
            options,
            entry);
    }

    private static void ReadFiles(JsonObject owner, Dictionary<string, string> into)
    {
        if (owner["Files"] is not JsonObject files) return;
        foreach (var p in files)
            if (p.Value is JsonValue v && v.TryGetValue<string>(out var rel) && !string.IsNullOrWhiteSpace(rel))
                into[p.Key] = Normalize(rel);
    }

    /// <summary>
    /// The model attributes an option switches ON — Penumbra's <c>Atr</c> manipulation, by name.
    /// <para/>
    /// This is how a pack ships one model holding a dozen accessories and a checkbox for each: every piece's
    /// submeshes are tagged with an attribute, the pack's default turns them all off, and each option turns
    /// one back on. Such an option redirects no FILES at all, so a reader that only looks at <c>Files</c>
    /// sees an empty option and concludes the pack selects nothing.
    /// <para/>
    /// Only entries turning an attribute ON are collected. An option that switches one off is not what makes
    /// the piece selectable.
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
