using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Proteus.Services;

/// <summary>
/// Reads and writes Penumbra's root <c>meta.json</c> mod manifest. From FileVersion 4 it holds the <c>Groups</c>
/// array (array order = priority, lower = higher) and the <c>DefaultData</c> object.
/// Reads fall back to the v3 layout (per-group files, <c>default_mod.json</c>). Writes are v4 only: Proteus never
/// authors v3 and never edits a v3 folder (see <see cref="IsLegacyFolder"/> and <see cref="LegacyFolderException"/>);
/// a current Penumbra migrates such a folder on load.
/// </summary>
internal static class PenumbraModMeta
{
    public const string MetaFile         = "meta.json";
    public const string LegacyDefaultMod = "default_mod.json";

    /// <summary>The version that moved groups and the default option into meta.json.</summary>
    public const int SingleFileVersion = 4;
    /// <summary>
    /// The format Proteus reads but will not write, and what <see cref="FileVersionOf"/> reports for a manifest with
    /// no version; hence <see cref="IsLegacyFolder"/> asks <see cref="HasReadableManifest"/> first.
    /// </summary>
    public const int LegacyFileVersion = 3;

    // Encoder: Penumbra writes non-ASCII names as themselves, so a rewrite here must not escape them. See ProteusJson.
    private static readonly JsonSerializerOptions WriteOptions =
        new() { WriteIndented = true, Encoder = ProteusJson.Encoder };

    /// <summary>
    /// Whether the folder has a manifest that actually parses; false when missing or corrupt. Unlike
    /// <see cref="ReadFileVersion"/>, separates those from readable, which matters because from
    /// <see cref="SingleFileVersion"/> on the manifest also holds the live redirects.
    /// </summary>
    public static bool HasReadableManifest(string modRoot)
    {
        try
        {
            var path = Path.Combine(modRoot, MetaFile);
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch { return false; }
    }

    /// <summary>
    /// Thrown when a write is asked for against a mod folder still in Penumbra's pre-v4 layout. An exception rather
    /// than a no-op, so a write that cannot happen is never mistaken for success.
    /// </summary>
    public sealed class LegacyFolderException(string modRoot)
        : InvalidOperationException(
            $"{modRoot} is a pre-v{SingleFileVersion} Penumbra mod folder, which Proteus does not write to.");

    /// <summary>
    /// Whether this folder is one Proteus will read but not edit. <see cref="HasReadableManifest"/> is checked
    /// first, so a folder still being created (no manifest yet) doesn't refuse its own first write.
    /// </summary>
    /// <param name="waitIfHeld">Wait out a manifest someone else holds open, as the writers do. False for display-only
    /// callers on the draw or framework thread.</param>
    public static bool IsLegacyFolder(string modRoot, bool waitIfHeld = true)
    {
        var manifest = ReadManifest(modRoot, out bool readable, waitIfHeld: waitIfHeld);
        return readable && FileVersionOf(manifest) < SingleFileVersion;
    }

    /// <summary>
    /// Read the manifest in one parse, throwing <see cref="LegacyFolderException"/> if Proteus will not write to the
    /// folder; the manifest is returned so the write can preserve keys it does not own. Call it where a writer commits
    /// to a format, so a write of nothing stays a no-op.
    /// </summary>
    private static Dictionary<string, JsonElement> ReadManifestForWrite(string modRoot)
    {
        // throwIfHeld: a manifest held open must not read as absent, or the write would replace it. See ManifestInUseException.
        var manifest = ReadManifest(modRoot, out bool readable, waitIfHeld: true, throwIfHeld: true);
        // Readable first: FileVersionOf reports missing and version-less manifests alike.
        if (readable && FileVersionOf(manifest) < SingleFileVersion)
            throw new LegacyFolderException(modRoot);
        return manifest;
    }

    /// <summary>
    /// Bring a folder Proteus owns up to <see cref="SingleFileVersion"/>, folding its <c>default_mod.json</c> into
    /// <c>DefaultData</c> and preserving every other key. A no-op on a v4 folder or one with no manifest. Never used on
    /// someone else's mod (Penumbra migrates those on load). Groups are not folded: Proteus-owned v3 folders have none.
    /// </summary>
    public static void MigrateToCurrent(string modRoot)
    {
        if (!IsLegacyFolder(modRoot)) return;

        // Rewrites the whole manifest from what it read, so a held-open one must not read as empty.
        var manifest = ReadManifest(modRoot, out _, waitIfHeld: true, throwIfHeld: true);
        var (files, manips) = TryReadDefaultData(modRoot)
                           ?? (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []);

        // Swaps, under its v3 name; WriteDefaultData writes it back as FileSwaps.
        var swaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var legacy = Path.Combine(modRoot, LegacyDefaultMod);
            if (File.Exists(legacy))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("Swaps", out var s)
                    && s.ValueKind == JsonValueKind.Object)
                    foreach (var p in s.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } to)
                            swaps[p.Name] = to;
            }
        }
        catch { /* unreadable — the redirects above are still worth carrying over */ }

        var name = manifest.TryGetValue("Name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? Path.GetFileName(modRoot)
            : Path.GetFileName(modRoot);

        // WriteDefaultData stamps SingleFileVersion and preserves unowned keys; CleanLegacyFiles drops default_mod.json.
        WriteDefaultData(modRoot, name, manifest, files, swaps, manips);
        CleanLegacyFiles(modRoot);
    }

    /// <summary>
    /// The mod's option groups in <c>Groups</c> array order, as (name, raw element) pairs. Null when there is no v4
    /// <c>Groups</c> array (fall back to v3); empty means v4 with no groups.
    /// </summary>
    public static List<(string Name, JsonElement Group)>? TryReadGroups(string modRoot)
    {
        try
        {
            var path = Path.Combine(modRoot, MetaFile);
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Groups", out var groups)
                || groups.ValueKind != JsonValueKind.Array)
                return null;

            var result = new List<(string, JsonElement)>();
            foreach (var g in groups.EnumerateArray())
                if (g.TryGetProperty("Name", out var n) && n.GetString() is { Length: > 0 } name)
                    // Clone: the JsonDocument is disposed when this method returns.
                    result.Add((name, g.Clone()));
            return result;
        }
        catch { return null; /* missing or malformed — fall back to v3 */ }
    }

    /// <summary>
    /// The mod's always-applied redirects and metadata edits as on disk, in either format — the inverse of
    /// <see cref="WriteRedirects"/>. Null when missing or unparseable, which means "unknown", never "empty".
    /// Manipulations are boxed <see cref="JsonElement"/>s so a read→write cycle preserves them verbatim.
    /// </summary>
    public static (Dictionary<string, string> Files, List<object> Manipulations)? TryReadDefaultData(string modRoot)
    {
        try
        {
            var manifest = ReadManifest(modRoot);
            if (FileVersionOf(manifest) >= SingleFileVersion)
            {
                if (!manifest.TryGetValue("DefaultData", out var dd) || dd.ValueKind != JsonValueKind.Object)
                    return null;
                return ReadFilesAndManipulations(dd);
            }

            var legacy = Path.Combine(modRoot, LegacyDefaultMod);
            if (!File.Exists(legacy)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return ReadFilesAndManipulations(doc.RootElement);
        }
        catch { return null; /* missing or malformed — "unknown", and the caller must not guess */ }
    }

    /// <summary>Shared shape of the v3 root object and the v4 <c>DefaultData</c> object.</summary>
    private static (Dictionary<string, string> Files, List<object> Manipulations) ReadFilesAndManipulations(
        JsonElement root)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("Files", out var f) && f.ValueKind == JsonValueKind.Object)
            foreach (var p in f.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } rel)
                    files[p.Name] = rel;

        var manips = new List<object>();
        if (root.TryGetProperty("Manipulations", out var m) && m.ValueKind == JsonValueKind.Array)
            foreach (var e in m.EnumerateArray())
                manips.Add(e.Clone());   // the JsonDocument is disposed when the caller returns

        return (files, manips);
    }

    /// <summary>
    /// Whether the mod puts anything of its own into the game (a redirect, manipulation, IMC group, or non-identity
    /// file swap) in its default data or any option, in either format. Separates pure overlay packs from mods that
    /// also ship content. True when the manifest is missing or unreadable, since false justifies disabling the mod.
    /// </summary>
    public static bool PublishesGameContent(string modRoot)
    {
        try
        {
            var manifest = ReadManifest(modRoot);
            if (manifest.Count == 0) return true;   // unreadable — see the remarks

            if (FileVersionOf(manifest) >= SingleFileVersion)
            {
                if (manifest.TryGetValue("DefaultData", out var dd) && HasGameContent(dd)) return true;
                if (manifest.TryGetValue("Groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                    foreach (var g in groups.EnumerateArray())
                        if (GroupHasGameContent(g))
                            return true;
                return false;
            }

            var legacy = Path.Combine(modRoot, LegacyDefaultMod);
            if (File.Exists(legacy))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
                if (HasGameContent(doc.RootElement)) return true;
            }

            foreach (var file in Directory.EnumerateFiles(modRoot, "group_*.json"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (GroupHasGameContent(doc.RootElement)) return true;
            }

            return false;
        }
        catch { return true; /* see the remarks — an unreadable folder is not one to disable */ }
    }

    /// <summary>
    /// One group, in either format: an <c>Imc</c> group edits the game by existing; a <c>Combining</c> group keeps its
    /// redirects in a parallel <c>Containers</c> array; every other kind keeps them on the options.
    /// </summary>
    private static bool GroupHasGameContent(JsonElement group)
    {
        if (group.ValueKind != JsonValueKind.Object) return false;

        if (group.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String
            && string.Equals(t.GetString(), "Imc", StringComparison.OrdinalIgnoreCase))
            return true;

        if (group.TryGetProperty("Containers", out var containers) && containers.ValueKind == JsonValueKind.Array)
            foreach (var c in containers.EnumerateArray())
                if (HasGameContent(c))
                    return true;

        if (!group.TryGetProperty("Options", out var opts) || opts.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var o in opts.EnumerateArray())
            if (HasGameContent(o))
                return true;
        return false;
    }

    /// <summary>Shared shape of one option, the v3 root object and the v4 <c>DefaultData</c> object.</summary>
    private static bool HasGameContent(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) return false;

        if (o.TryGetProperty("Files", out var f) && f.ValueKind == JsonValueKind.Object
            && f.EnumerateObject().Any())
            return true;

        if (o.TryGetProperty("Manipulations", out var m) && m.ValueKind == JsonValueKind.Array
            && m.EnumerateArray().Any())
            return true;

        if (o.TryGetProperty("FileSwaps", out var s) && s.ValueKind == JsonValueKind.Object)
            foreach (var p in s.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String
                    && !string.Equals(p.Value.GetString(), p.Name, StringComparison.OrdinalIgnoreCase))
                    return true;

        return false;
    }

    /// <summary>
    /// One file the mod publishes: the game path it claims, the file backing it (relative to the mod root),
    /// and where in the mod that claim is made.
    /// </summary>
    /// <param name="Source">"" for the mod's default data, else "Group" or "Group / Option"; display only.</param>
    public readonly record struct Redirect(string GamePath, string File, string Source);

    /// <summary>
    /// Every file redirect in the mod — default data, every option and Combining container — in either format.
    /// Not deduplicated by game path: an edit must reach every option's file. Empty (not null) when unreadable.
    /// </summary>
    public static List<Redirect> ReadAllRedirects(string modRoot)
    {
        var found = new List<Redirect>();
        try
        {
            var manifest = ReadManifest(modRoot);
            if (FileVersionOf(manifest) >= SingleFileVersion)
            {
                if (manifest.TryGetValue("DefaultData", out var dd)) AddFiles(dd, "", found);
                if (manifest.TryGetValue("Groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                    foreach (var g in groups.EnumerateArray())
                        AddGroup(g, found);
                return found;
            }

            var legacy = Path.Combine(modRoot, LegacyDefaultMod);
            if (File.Exists(legacy))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
                AddFiles(doc.RootElement, "", found);
            }
            foreach (var file in Directory.EnumerateFiles(modRoot, "group_*.json"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                AddGroup(doc.RootElement, found);
            }
        }
        catch { /* missing or malformed — the caller shows an empty list */ }
        return found;
    }

    private static void AddGroup(JsonElement group, List<Redirect> into)
    {
        if (group.ValueKind != JsonValueKind.Object) return;
        var name = group.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";

        // A Combining group's files hang off a parallel Containers array, one per flag combination, named by ordinal.
        if (group.TryGetProperty("Containers", out var containers)
            && containers.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var c in containers.EnumerateArray())
                AddFiles(c, $"{name} / #{++i}", into);
        }

        if (!group.TryGetProperty("Options", out var opts) || opts.ValueKind != JsonValueKind.Array) return;
        foreach (var o in opts.EnumerateArray())
        {
            var option = o.ValueKind == JsonValueKind.Object && o.TryGetProperty("Name", out var on)
                ? on.GetString() ?? "" : "";
            AddFiles(o, option.Length > 0 ? $"{name} / {option}" : name, into);
        }
    }

    private static void AddFiles(JsonElement owner, string source, List<Redirect> into)
    {
        if (owner.ValueKind != JsonValueKind.Object) return;
        if (!owner.TryGetProperty("Files", out var f) || f.ValueKind != JsonValueKind.Object) return;
        foreach (var p in f.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } rel)
                into.Add(new Redirect(p.Name, rel, source));
    }

    /// <summary>The <c>Options[].Name</c> values of <paramref name="group"/>, in order.</summary>
    public static List<string> ReadOptionNames(JsonElement group)
    {
        var names = new List<string>();
        if (!group.TryGetProperty("Options", out var opts) || opts.ValueKind != JsonValueKind.Array)
            return names;
        foreach (var o in opts.EnumerateArray())
            if (o.TryGetProperty("Name", out var n) && n.GetString() is { } s)
                names.Add(s);
        return names;
    }

    /// <summary>
    /// The manifest's top-level keys, cloned so they outlive the parse. Empty when there is no manifest or it can't
    /// be read.
    /// </summary>
    private static Dictionary<string, JsonElement> ReadManifest(string modRoot)
        => ReadManifest(modRoot, out _);

    /// <inheritdoc cref="ReadManifest(string)"/>
    /// <param name="readable">Whether a manifest was there and parsed as an object; an empty dictionary alone can't
    /// tell no manifest from <c>{}</c>.</param>
    /// <param name="waitIfHeld">Retry for a moment while the manifest is held open by someone else.</param>
    /// <param name="throwIfHeld">Throw <see cref="ManifestInUseException"/> when the manifest exists but stays held
    /// open, instead of answering "no manifest". Every writer passes it.</param>
    private static Dictionary<string, JsonElement> ReadManifest(
        string modRoot, out bool readable, bool waitIfHeld = false, bool throwIfHeld = false)
    {
        readable = false;
        var preserved = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var path = Path.Combine(modRoot, MetaFile);

        string text;
        try
        {
            if (!File.Exists(path)) return preserved;
            text = ReadSharedText(path, waitIfHeld ? ReadRetries : 0);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return preserved;   // gone between the check and the read: genuinely no manifest
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (throwIfHeld) throw new ManifestInUseException(path, ex);
            return preserved;
        }
        catch { return preserved; }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return preserved;

            readable = true;
            foreach (var p in doc.RootElement.EnumerateObject())
                preserved[p.Name] = p.Value.Clone();
        }
        // Read but unparseable: treated as no manifest, so the managed mod's next write rebuilds it.
        catch { }
        return preserved;
    }

    /// <summary>
    /// Thrown when a writer finds <c>meta.json</c> present but held open past every retry. Refusing costs one write;
    /// overwriting with a fresh manifest would lose the mod.
    /// </summary>
    public sealed class ManifestInUseException(string path, Exception inner)
        : IOException($"{path} is in use by another program and could not be read, so it was not rewritten.", inner);

    /// <summary>How many times a held manifest is re-read, backing off from 50 ms (about 1.5 s, matching
    /// <see cref="AtomicWrite"/>).</summary>
    private const int ReadRetries = 5;

    /// <summary>
    /// The file's text, retrying while another program holds it. Shared read and write/delete, so Penumbra's
    /// own saves and a replace can proceed while this reads.
    /// </summary>
    private static string ReadSharedText(string path, int retries)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { throw; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < retries)
            {
                Thread.Sleep(50 << i);
            }
        }
    }

    /// <summary>The <c>FileVersion</c> in an already-read manifest, defaulting to <see cref="LegacyFileVersion"/>.</summary>
    private static int FileVersionOf(Dictionary<string, JsonElement> manifest)
        => manifest.TryGetValue("FileVersion", out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out var n) ? n : LegacyFileVersion;

    /// <summary>
    /// The folder's declared <c>FileVersion</c>, or <see cref="LegacyFileVersion"/> when there is no manifest or it
    /// can't be read. Penumbra rewrites this field when it migrates a mod on load.
    /// </summary>
    public static int ReadFileVersion(string modRoot)
        => FileVersionOf(ReadManifest(modRoot));

    /// <summary>
    /// A fresh manifest at <see cref="SingleFileVersion"/>, written by every importer before its first
    /// <see cref="WriteRedirects"/>, so those folders never read as <see cref="IsLegacyFolder"/>.
    /// </summary>
    /// <param name="version">The mod's own version string, when the source carries one (an imported pack).</param>
    /// <param name="website">The mod's home page, when the source carries one.</param>
    public static string NewMetaJson(string name, string author, string description,
        string? version = null, string? website = null)
        => JsonSerializer.Serialize(new
        {
            FileVersion = SingleFileVersion,
            Name        = name,
            Author      = author,
            Description = description,
            Version     = version ?? "",
            Website     = website ?? "",
            ModTags     = Array.Empty<string>(),
        }, WriteOptions);

    /// <summary>
    /// Writes one single-select option group whose options carry no redirects: it exists so Penumbra shows a
    /// selector, which Proteus reads back through <c>OverlayOptionGroup.PenumbraGroupName</c>.
    /// <paramref name="index"/> is the group's 0-based ordinal (lower = higher priority); it is spliced at exactly
    /// that array position, and past the end appends. A same-named group is replaced.
    /// </summary>
    public static void WriteSingleSelectGroup(
        string modRoot, int index, string name, IReadOnlyList<string> optionNames, int defaultIndex)
    {
        if (optionNames.Count == 0) return;
        if (defaultIndex < 0 || defaultIndex >= optionNames.Count) defaultIndex = 0;
        Write(modRoot, index, name, optionNames, "Single", defaultIndex);
    }

    /// <summary>
    /// The multi-select counterpart: <paramref name="defaultSettings"/> is a bitmask over the options (bit 0 = first),
    /// not an index. Ordinals and replacement work as in <see cref="WriteSingleSelectGroup"/>.
    /// </summary>
    public static void WriteMultiSelectGroup(
        string modRoot, int index, string name, IReadOnlyList<string> optionNames, ulong defaultSettings = 0)
    {
        if (optionNames.Count == 0) return;
        Write(modRoot, index, name, optionNames, "Multi", (long)defaultSettings);
    }

    /// <summary>
    /// Writes an <c>Imc</c> group: checkboxes over one item's attribute bits, editing the game directly (it works with
    /// Proteus off). <paramref name="entry"/> must be the item's real entry (see <see cref="ImcEntrySource"/>) with the
    /// new bits cleared, since Penumbra replaces the whole entry. Each option's bit must lie outside the entry's mask,
    /// so the meaning is the same whether Penumbra combines by OR or XOR.
    /// </summary>
    /// <param name="defaultSettings">Bitmask over the options (bit 0 = first). Ship with every bit set so the mod looks
    /// unchanged until one is unticked.</param>
    /// <param name="priority">
    /// Must beat any other <c>Imc</c> group in the mod for the same identifier: Penumbra applies only the highest.
    /// See <see cref="ImcEntrySource.MaxPriorityFor"/>.
    /// </param>
    public static void WriteImcGroup(
        string modRoot, int index, string name, ImcIdentifier identifier, ImcEntry entry,
        IReadOnlyList<(string Name, ushort Mask)> options, ulong defaultSettings, int priority = 0)
    {
        if (options.Count == 0) return;

        var group = new Dictionary<string, object>
        {
            ["Type"] = "Imc",
            ["Name"] = name,
            ["Description"] = "",
            ["Priority"] = priority,
            ["DefaultSettings"] = defaultSettings,

            // AllVariants: the worn variant can't be known from a mod folder. OnlyAttributes: Penumbra then sources
            // each variant's own entry and replaces only the attribute mask. DefaultEntry is the fallback for a
            // variant the game has no entry for.
            ["AllVariants"] = true,
            ["OnlyAttributes"] = true,
            ["Identifier"] = new Dictionary<string, object>
            {
                ["ObjectType"] = identifier.ObjectType,
                ["PrimaryId"] = identifier.PrimaryId,
                ["Variant"] = identifier.Variant,
                ["EquipSlot"] = identifier.EquipSlot,
            },
            ["DefaultEntry"] = new Dictionary<string, object>
            {
                ["MaterialId"] = entry.MaterialId,
                ["DecalId"] = entry.DecalId,
                ["VfxId"] = entry.VfxId,
                ["MaterialAnimationId"] = entry.MaterialAnimationId,
                ["AttributeMask"] = entry.AttributeMask,
                ["SoundId"] = entry.SoundId,
            },
            ["Options"] = options
                .Select(o => new Dictionary<string, object> { ["Name"] = o.Name, ["AttributeMask"] = o.Mask })
                .ToList(),
        };

        if (index < 0) index = 0;

        WriteGroupIntoManifest(modRoot, ReadManifestForWrite(modRoot), index, name, _ => group);
    }

    /// <summary>
    /// Put <paramref name="ours"/> into an IMC group the mod already has, since Penumbra keeps only one group per IMC
    /// identifier (see <see cref="ImcEntrySource.AppliedGroupFor"/>). Every property is carried over raw; only
    /// <c>Options</c>, <c>DefaultSettings</c> and <c>DefaultEntry.AttributeMask</c> are rewritten.
    /// </summary>
    /// <param name="target">The group as read. Rewritten in place, at its own ordinal and under its own name.</param>
    /// <param name="ours">Our options, in the order they should appear, each carrying a single bit.</param>
    /// <param name="ownedNames">Option names Proteus owns, dropped before <paramref name="ours"/> is appended, so a
    /// rewrite replaces rather than duplicates.</param>
    /// <param name="entryMask">What <c>DefaultEntry</c>'s <c>AttributeMask</c> becomes: our bits cleared on
    /// a write, the author's original restored on a revert. Null leaves it as it is.</param>
    /// <param name="entryIfAbsent">Used only when the group has no <c>DefaultEntry</c>; one must exist so our bit sits
    /// outside the default mask (see <see cref="WriteImcGroup"/>).</param>
    /// <returns>False when the merge would leave the group with no options; nothing is written and the caller deletes
    /// the group.</returns>
    internal static bool MergeImcGroup(
        string modRoot, GroupRef target,
        IReadOnlyList<(string Name, ushort Mask)> ours,
        IReadOnlySet<string> ownedNames,
        ushort? entryMask,
        ImcEntry entryIfAbsent)
    {
        // Read and refuse up front, before any option work, then reuse the same manifest for the write.
        var manifest = ReadManifestForWrite(modRoot);

        // Kept options, each with the index it USED to sit at, so DefaultSettings can follow them.
        var kept = new List<(JsonElement Option, int OldIndex)>();
        if (target.Group.TryGetProperty("Options", out var opts) && opts.ValueKind == JsonValueKind.Array)
        {
            int at = 0;
            foreach (var o in opts.EnumerateArray())
            {
                int old = at++;
                var name = o.TryGetProperty("Name", out var n) ? n.GetString() : null;
                if (name != null && ownedNames.Contains(name)) continue;
                kept.Add((o, old));
            }
        }
        if (kept.Count == 0 && ours.Count == 0) return false;

        // DefaultSettings is a bitmask over option index: rebuilt so each survivor keeps its bit at its new position,
        // and every option of ours ships ticked.
        ulong oldDefaults = target.Group.TryGetProperty("DefaultSettings", out var ds)
                         && ds.ValueKind == JsonValueKind.Number && ds.TryGetUInt64(out var v) ? v : 0;
        ulong defaults = 0;
        for (int i = 0; i < kept.Count; i++)
            if (kept[i].OldIndex < 64 && (oldDefaults & (1UL << kept[i].OldIndex)) != 0)
                defaults |= 1UL << i;
        for (int i = 0; i < ours.Count; i++)
        {
            int at = kept.Count + i;
            if (at < 64) defaults |= 1UL << at;
        }

        var options = new List<object>(kept.Count + ours.Count);
        foreach (var (o, _) in kept) options.Add(o);
        foreach (var (name, mask) in ours)
            options.Add(new Dictionary<string, object> { ["Name"] = name, ["AttributeMask"] = mask });

        var merged = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var p in target.Group.EnumerateObject())
            if (p.Name is not ("Options" or "DefaultSettings" or "DefaultEntry"))
                merged[p.Name] = p.Value;

        merged["DefaultSettings"] = defaults;
        merged["Options"] = options;
        if (MergedEntry(target.Group, entryMask, entryIfAbsent) is { } defaultEntry)
            merged["DefaultEntry"] = defaultEntry;

        // Replaced by name and spliced at its own ordinal, so the group keeps its position.
        WriteGroupIntoManifest(modRoot, manifest, target.Index, target.Name, _ => merged);
        return true;
    }

    /// <summary>
    /// The group's own <c>DefaultEntry</c> with its attribute mask replaced, or a fresh one from
    /// <paramref name="fallback"/> when it has none. Other fields are carried over untouched.
    /// </summary>
    private static object? MergedEntry(JsonElement group, ushort? mask, ImcEntry fallback)
    {
        // No entry and no mask (a revert): synthesising one would hand the item MaterialId 0.
        if (mask is null
            && (!group.TryGetProperty("DefaultEntry", out var none) || none.ValueKind != JsonValueKind.Object))
            return null;

        if (!group.TryGetProperty("DefaultEntry", out var e) || e.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object>
            {
                ["MaterialId"] = fallback.MaterialId,
                ["DecalId"] = fallback.DecalId,
                ["VfxId"] = fallback.VfxId,
                ["MaterialAnimationId"] = fallback.MaterialAnimationId,
                ["AttributeMask"] = (ushort)((mask ?? fallback.AttributeMask) & 0x3FF),
                ["SoundId"] = fallback.SoundId,
            };

        var entry = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
            if (p.Name != "AttributeMask")
                entry[p.Name] = p.Value;

        entry["AttributeMask"] = mask is { } m
            ? (ushort)(m & 0x3FF)
            : (ushort)((e.TryGetProperty("AttributeMask", out var a) && a.TryGetInt32(out var cur) ? cur : 0)
                       & 0x3FF);
        return entry;
    }

    /// <summary>
    /// How many option groups the mod has: the ordinal that appends one at the end. Zero when there is no readable
    /// <c>Groups</c> array.
    /// </summary>
    public static int GroupCount(string modRoot) => TryReadGroups(modRoot)?.Count ?? 0;

    /// <summary>Which item an IMC edit names. Equipment and accessories only — see <see cref="ImcEntrySource.ImcPathFor"/>.</summary>
    public readonly record struct ImcIdentifier(string ObjectType, int PrimaryId, int Variant, string EquipSlot);

    /// <summary>
    /// One option group as it sits in the manifest, with what a rewrite needs to put it back where it was.
    /// </summary>
    /// <param name="Index">Its position in the whole <c>Groups</c> array, not among groups of its kind.</param>
    /// <param name="Group">The raw element, so a rewrite can preserve fields it doesn't own — see <see cref="MergeImcGroup"/>.</param>
    public readonly record struct GroupRef(string Name, int Index, JsonElement Group);

    /// <summary>
    /// Remove the group of this name; a missing name is not an error.
    /// </summary>
    public static void DeleteGroup(string modRoot, string name)
    {
        var manifest = ReadManifestForWrite(modRoot);
        if (!manifest.TryGetValue("Groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return;
        var others = groups.EnumerateArray()
            .Where(g => !(g.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String
                          && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, ProteusJson.WriterOptions))
        {
            w.WriteStartObject();
            foreach (var (key, value) in manifest)
            {
                if (key == "Groups") continue;
                w.WritePropertyName(key);
                value.WriteTo(w);
            }
            w.WritePropertyName("Groups");
            w.WriteStartArray();
            foreach (var g in others) g.WriteTo(w);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        AtomicWrite(Path.Combine(modRoot, MetaFile), System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void Write(
        string modRoot, int index, string name, IReadOnlyList<string> optionNames, string type, long defaultSettings)
    {
        if (index < 0) index = 0;

        WriteGroupIntoManifest(modRoot, ReadManifestForWrite(modRoot), index, name,
            slot => BuildGroup(slot, name, optionNames, type, defaultSettings));
    }

    /// <summary>One option of a group that publishes files.</summary>
    /// <param name="Files">Game path to mod-root-relative file. Empty means "this option contributes nothing", which
    /// is a useful thing for an option to do: it lets the group be switched off without deleting it.</param>
    public readonly record struct FileOption(string Name, IReadOnlyDictionary<string, string> Files);

    /// <summary>
    /// Write one single-select group whose options CARRY redirects — which
    /// <see cref="WriteSingleSelectGroup"/> deliberately does not do, its options existing only to make Penumbra show
    /// a selector.
    /// <para/>
    /// The whole option list is passed every time: a caller adding one option reads the group back, appends to it and
    /// rewrites. A same-named group is replaced, so that is also how an option is removed.
    /// </summary>
    /// <param name="priority">
    /// Which group wins a game path two groups both claim. Passed rather than derived from
    /// <paramref name="index"/>, because a group that has to beat the author's own needs to say so explicitly.
    /// </param>
    public static void WriteFileOptionGroup(string modRoot, int index, string name, int priority,
                                            IReadOnlyList<FileOption> options, int defaultIndex)
    {
        if (options.Count == 0) return;
        if (index < 0) index = 0;
        if (defaultIndex < 0 || defaultIndex >= options.Count) defaultIndex = 0;

        WriteGroupIntoManifest(modRoot, ReadManifestForWrite(modRoot), index, name,
            _ => BuildFileGroup(name, priority, options, defaultIndex));
    }

    /// <inheritdoc cref="BuildGroup"/>
    /// <remarks>The same shape, with files per option and an explicit priority.</remarks>
    private static object BuildFileGroup(string name, int priority, IReadOnlyList<FileOption> options, int defaultIndex)
        => new
        {
            Version         = 0,
            Name            = name,
            Description     = "",
            Image           = "",
            Page            = 0,
            Priority        = priority,
            Type            = "Single",
            DefaultSettings = (long)defaultIndex,
            Options         = options.Select(o => new
            {
                Name          = o.Name,
                Description   = "",
                Files         = o.Files.ToDictionary(p => p.Key, p => p.Value),
                FileSwaps     = new Dictionary<string, string>(),
                Manipulations = Array.Empty<object>(),
            }).ToArray(),
        };

    /// <summary>A group's <c>Type</c> ("Single", "Multi", "Imc", …), or "" when it has none.</summary>
    public static string TypeOf(JsonElement group)
        => group.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";

    /// <summary>
    /// Add options that carry files to a group somebody else wrote, or replace same-named ones, leaving every other
    /// field of the group and every other option exactly as it was — its description, priority, default, and the
    /// options' own swaps and manipulations. New options go at the END, so no option the author wrote changes index:
    /// Penumbra keeps a Single group's selection as an index and a Multi group's as a bitmask, and either would
    /// otherwise land on the wrong option.
    /// </summary>
    public static void AddFileOptions(string modRoot, string groupName, IReadOnlyList<FileOption> options)
        => EditGroup(modRoot, groupName, (group, list) =>
        {
            foreach (var option in options)
            {
                var built = JsonSerializer.SerializeToNode(new
                {
                    Name          = option.Name,
                    Description   = "",
                    Files         = option.Files.ToDictionary(p => p.Key, p => p.Value),
                    FileSwaps     = new Dictionary<string, string>(),
                    Manipulations = Array.Empty<object>(),
                });
                int at = IndexOfOption(list, option.Name);
                if (at >= 0) list[at] = built;
                else list.Add(built);
            }
        });

    /// <summary>
    /// Take one option out of a group somebody else wrote, leaving the rest as it was. The group's default is
    /// corrected for the options that move up a place. Returns the files the option published, or null when there was
    /// no such option.
    /// </summary>
    public static Dictionary<string, string>? RemoveOption(string modRoot, string groupName, string optionName)
    {
        Dictionary<string, string>? removed = null;
        EditGroup(modRoot, groupName, (group, list) =>
        {
            int at = IndexOfOption(list, optionName);
            if (at < 0) return;

            removed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (list[at]?["Files"] is System.Text.Json.Nodes.JsonObject files)
                foreach (var (path, rel) in files)
                    if (rel?.GetValueKind() == JsonValueKind.String) removed[path] = rel.GetValue<string>();
            list.RemoveAt(at);

            long settings = group["DefaultSettings"]?.GetValueKind() == JsonValueKind.Number
                                ? group["DefaultSettings"]!.GetValue<long>()
                                : 0;
            bool multi = string.Equals(group["Type"]?.GetValue<string>(), "Multi", StringComparison.OrdinalIgnoreCase);
            settings = multi
                ? (settings & ((1L << at) - 1)) | ((settings >> (at + 1)) << at)   // the bit goes, the higher ones drop
                : settings > at ? settings - 1 : settings == at ? 0 : settings;
            group["DefaultSettings"] = settings;
        });
        return removed;
    }

    private static int IndexOfOption(System.Text.Json.Nodes.JsonArray list, string name)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i]?["Name"] is { } n && n.GetValueKind() == JsonValueKind.String
                && string.Equals(n.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>Rewrite one existing group in place, keeping its position. Throws when there is no such group.</summary>
    private static void EditGroup(string modRoot, string groupName,
                                  Action<System.Text.Json.Nodes.JsonObject, System.Text.Json.Nodes.JsonArray> edit)
    {
        var manifest = ReadManifestForWrite(modRoot);
        int index = -1, i = 0;
        JsonElement found = default;
        if (manifest.TryGetValue("Groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            foreach (var g in groups.EnumerateArray())
            {
                if (g.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String
                    && string.Equals(n.GetString(), groupName, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    found = g;
                    break;
                }
                i++;
            }
        if (index < 0) throw new InvalidOperationException($"This mod has no group called \"{groupName}\".");

        var group = System.Text.Json.Nodes.JsonNode.Parse(found.GetRawText())!.AsObject();
        if (group["Options"] is not System.Text.Json.Nodes.JsonArray list)
            group["Options"] = list = [];
        edit(group, list);

        // Written back under its own name, so the splice drops the old copy and puts this one in the same slot.
        string name = group["Name"]?.GetValue<string>() ?? groupName;
        WriteGroupIntoManifest(modRoot, manifest, index, name, _ => group);
    }

    /// <summary>
    /// The highest <c>Priority</c> any of the mod's groups declares, or -1 when it has none. What a group has to beat
    /// to win a game path the mod already claims elsewhere.
    /// </summary>
    public static int MaxGroupPriority(string modRoot)
    {
        int max = -1;
        foreach (var (_, group) in TryReadGroups(modRoot) ?? [])
            if (group.TryGetProperty("Priority", out var p) && p.ValueKind == JsonValueKind.Number
                && p.TryGetInt32(out int value) && value > max)
                max = value;
        return max;
    }

    /// <summary>
    /// The named group's options, each with the files it publishes, or null when there is no such group. What a caller
    /// appending an option to its own group reads first.
    /// </summary>
    public static List<FileOption>? TryReadFileOptions(string modRoot, string name)
    {
        var group = (TryReadGroups(modRoot) ?? [])
            .FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        return group.Name == null ? null : FileOptionsOf(group.Group);
    }

    /// <summary>
    /// One group's options and the files each publishes, from a group already read. Reading a group at a time through
    /// <see cref="TryReadFileOptions"/> re-parses the whole manifest for each, and a manifest here can be 400 KB.
    /// </summary>
    public static List<FileOption>? FileOptionsOf(JsonElement group)
    {
        if (!group.TryGetProperty("Options", out var options) || options.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<FileOption>();
        foreach (var o in options.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object) continue;
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (o.TryGetProperty("Files", out var f) && f.ValueKind == JsonValueKind.Object)
                foreach (var p in f.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } rel)
                        files[p.Name] = rel;

            result.Add(new FileOption(o.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "", files));
        }
        return result;
    }

    /// <summary>
    /// The shape a group has on disk. <c>DefaultSettings</c> is the selected INDEX for Single and a bitmask for Multi;
    /// options carry no redirects.
    /// </summary>
    private static object BuildGroup(
        int index, string name, IReadOnlyList<string> optionNames, string type, long defaultSettings)
        => new
        {
            Version         = 0,
            Name            = name,
            Description     = "",
            Image           = "",
            Page            = 0,
            Priority        = index,
            Type            = type,
            DefaultSettings = defaultSettings,
            Options         = optionNames.Select(o => new
            {
                Name          = o,
                Description   = "",
                Files         = new Dictionary<string, string>(),
                FileSwaps     = new Dictionary<string, string>(),
                Manipulations = Array.Empty<object>(),
            }).ToArray(),
        };

    /// <summary>
    /// Splices the group into a v4 manifest's <c>Groups</c> array at <paramref name="index"/>, replacing any group of
    /// the same name and preserving every other key (<c>Identifier</c> is how Penumbra keys the mod).
    /// </summary>
    /// <param name="build">Builds the group object from its final array slot.</param>
    private static void WriteGroupIntoManifest(
        string modRoot, Dictionary<string, JsonElement> preserved,
        int index, string name, Func<int, object> build)
    {
        // Surviving groups in order; a same-named group is dropped, so names and ordinals stay unambiguous.
        var others = new List<JsonElement>();
        if (preserved.TryGetValue("Groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            foreach (var g in groups.EnumerateArray())
                if (!(g.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String
                      && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase)))
                    others.Add(g);

        // Clamped, not rejected: past the end means "put it last".
        var slot = Math.Clamp(index, 0, others.Count);

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, ProteusJson.WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("FileVersion", SingleFileVersion);
            w.WriteString("Identifier",
                preserved.TryGetValue("Identifier", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()!
                    : Guid.NewGuid().ToString());

            foreach (var (key, value) in preserved)
            {
                if (key is "FileVersion" or "Identifier" or "Groups") continue;
                w.WritePropertyName(key);
                value.WriteTo(w);
            }

            w.WritePropertyName("Groups");
            w.WriteStartArray();
            for (int i = 0; i < others.Count; i++)
            {
                if (i == slot) JsonSerializer.Serialize(w, build(slot));
                others[i].WriteTo(w);
            }
            if (slot >= others.Count)
                JsonSerializer.Serialize(w, build(slot));
            w.WriteEndArray();

            w.WriteEndObject();
        }

        AtomicWrite(Path.Combine(modRoot, MetaFile), System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>
    /// Writes the mod's always-applied redirects into the manifest's <c>DefaultData</c> object. The only entry point
    /// for redirects: <see cref="WriteDefaultData"/> is private because it skips the legacy refusal.
    /// </summary>
    public static void WriteRedirects(
        string modRoot, string modName,
        IDictionary<string, string> files,
        IDictionary<string, string>? swaps = null,
        IReadOnlyList<object>? manipulations = null)
    {
        WriteDefaultData(modRoot, modName, ReadManifestForWrite(modRoot), files, swaps, manipulations);
        // A folder Penumbra migrated keeps its old default_mod.json; drop it so a stale copy isn't mistaken for live.
        CleanLegacyFiles(modRoot);
    }

    /// <summary>
    /// Replaces the manifest's <c>DefaultData</c>, preserving every other key (notably <c>Identifier</c>). Creates the
    /// manifest if missing. <paramref name="preserved"/> is the already-read manifest.
    /// </summary>
    private static void WriteDefaultData(
        string modRoot, string modName,
        Dictionary<string, JsonElement> preserved,
        IDictionary<string, string> files,
        IDictionary<string, string>? swaps = null,
        IReadOnlyList<object>? manipulations = null)
    {
        var path = Path.Combine(modRoot, MetaFile);

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, ProteusJson.WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("FileVersion", SingleFileVersion);
            w.WriteString("Identifier",
                preserved.TryGetValue("Identifier", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()!
                    : Guid.NewGuid().ToString());

            foreach (var (key, value) in preserved)
            {
                // Rewritten below or above; everything else passes through untouched.
                if (key is "FileVersion" or "Identifier" or "DefaultData") continue;
                w.WritePropertyName(key);
                value.WriteTo(w);
            }
            if (!preserved.ContainsKey("Name")) w.WriteString("Name", modName);

            w.WritePropertyName("DefaultData");
            w.WriteStartObject();
            w.WritePropertyName("Files");
            w.WriteStartObject();
            foreach (var (gamePath, relPath) in files) w.WriteString(gamePath, relPath);
            w.WriteEndObject();
            w.WritePropertyName("FileSwaps");
            w.WriteStartObject();
            if (swaps != null) foreach (var (from, to) in swaps) w.WriteString(from, to);
            w.WriteEndObject();
            w.WritePropertyName("Manipulations");
            w.WriteStartArray();
            if (manipulations != null)
                foreach (var m in manipulations)
                    JsonSerializer.Serialize(w, m, m.GetType());
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteEndObject();
        }

        AtomicWrite(path, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>
    /// Writes via a sibling temp file + atomic move, retrying with backoff while Penumbra's watcher holds the target.
    /// The temp file is flushed to the device before the move, or a crash can leave a zero-filled file.
    /// </summary>
    /// <param name="maxRetries">
    /// How many times to retry the move, sleeping from 50 ms and doubling (default ≈1.55 s). Callers on the draw or
    /// framework thread must pass a small budget: those sleeps are frozen frames.
    /// </param>
    public static void AtomicWrite(string target, string contents, int maxRetries = 5)
        // UTF-8 without a BOM, matching what File.WriteAllText would have produced.
        => AtomicWrite(target, new System.Text.UTF8Encoding(false).GetBytes(contents), maxRetries);

    /// <summary>
    /// The binary form, for a mod's own files rewritten while Penumbra may be serving them.
    /// </summary>
    public static void AtomicWrite(string target, byte[] bytes, int maxRetries = 5)
    {
        var tmp = target + "." + Guid.NewGuid().ToString("N") + TempSuffix;

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                   bufferSize: 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, target, overwrite: true); return; }
            // Shift clamped at 5 so an oversized budget never overflows.
            catch (Exception) when (i < maxRetries) { Thread.Sleep(50 << Math.Min(i, 5)); }
            catch { try { File.Delete(tmp); } catch { } throw; }      // don't leave the temp behind
        }
    }

    /// <summary>Extension <see cref="AtomicWrite"/> gives its sibling temp file, which is named
    /// <c>&lt;target&gt;.&lt;32 hex&gt;.tmp</c>. Shared with the sweep so the two can't drift.</summary>
    private const string TempSuffix = ".tmp";

    /// <summary>
    /// Removes a v3 <c>default_mod.json</c> now superseded by <c>DefaultData</c>, plus any orphaned temp
    /// files left by an interrupted <see cref="AtomicWrite"/>.
    /// </summary>
    public static void CleanLegacyFiles(string modRoot)
    {
        try
        {
            var legacy = Path.Combine(modRoot, LegacyDefaultMod);
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch { /* best effort — a locked legacy file must not skip the sweep below */ }

        // Every AtomicWrite target: meta.json in the mod root, metadata.json in the sidecar folder.
        SweepAtomicTemps(modRoot);
        SweepAtomicTemps(Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir));
    }

    /// <summary>
    /// Deletes <see cref="AtomicWrite"/> temp files orphaned in <paramref name="dir"/> (non-recursive).
    /// Matched on the full <c>&lt;name&gt;.&lt;32 hex&gt;.tmp</c> shape rather than a bare <c>*.tmp</c>,
    /// so a mod that ships a .tmp of its own is never touched.
    /// </summary>
    private static void SweepAtomicTemps(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var tmp in Directory.EnumerateFiles(dir, "*" + TempSuffix))
            {
                var stem = Path.GetFileNameWithoutExtension(tmp);   // drops ".tmp"
                int dot = stem.LastIndexOf('.');
                if (dot < 1 || stem.Length - dot - 1 != 32) continue;
                bool hex = true;
                for (int i = dot + 1; i < stem.Length && hex; i++) hex = Uri.IsHexDigit(stem[i]);
                if (!hex) continue;
                try { File.Delete(tmp); } catch { }
            }
        }
        catch { /* best effort — leftovers are inert */ }
    }
}
