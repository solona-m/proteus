using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Proteus.Services;

/// <summary>
/// Reads and writes Penumbra's root <c>meta.json</c> mod manifest.
///
/// Penumbra's FileVersion 4 folded the whole mod layout into this one file: option groups moved out of
/// per-group <c>group_NNN_name.json</c> files into a <c>Groups</c> array, and <c>default_mod.json</c>
/// became the <c>DefaultData</c> object. Group ORDER is now the array index — the old filename number is
/// gone — but the meaning is unchanged: lower = higher priority.
///
/// Reads are two-tier everywhere: v4 first, falling back to the v3 layout for folders an older Penumbra
/// wrote and never migrated.
///
/// Writes follow whatever format the mod folder is ALREADY in, because the two are not mutually legible:
/// a Penumbra new enough to read <c>DefaultData</c> migrates a v3 folder up on load, but an older one has
/// never heard of <c>DefaultData</c> and silently applies no redirects at all. Writing v4 unconditionally
/// therefore breaks users on an older Penumbra with a completely clean log. New folders are created as v3
/// for the same reason: it is the format both understand, and a newer Penumbra upgrades it on first load.
/// </summary>
internal static class PenumbraModMeta
{
    public const string MetaFile         = "meta.json";
    public const string LegacyDefaultMod = "default_mod.json";

    /// <summary>The version that moved groups and the default option into meta.json.</summary>
    public const int SingleFileVersion = 4;
    /// <summary>What we create new folders as — readable by every Penumbra, upgraded in place by new ones.</summary>
    public const int LegacyFileVersion = 3;

    // Encoder: these are Penumbra's own files, and Penumbra writes non-ASCII names as themselves. Without
    // it a rewrite here turns a mod's 正常 into "正常" in its manifest. See ProteusJson.
    private static readonly JsonSerializerOptions WriteOptions =
        new() { WriteIndented = true, Encoder = ProteusJson.Encoder };

    /// <summary>
    /// Whether the folder has a manifest that actually parses. False both when there is none and when
    /// it is corrupt — a caller repairing a manifest needs those two separated from "readable", which
    /// <see cref="ReadFileVersion"/> cannot give it: that collapses missing and unparseable into the
    /// same <see cref="LegacyFileVersion"/>. The distinction matters because from
    /// <see cref="SingleFileVersion"/> on, the manifest is also where the redirects live, so
    /// overwriting a READABLE one throws away live published state.
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
    /// The mod's option groups in <c>Groups</c> array order, as (name, raw element) pairs. Null — not an
    /// empty list — when there is no v4 <c>Groups</c> array to read, which is the caller's signal to fall
    /// back to the v3 <c>group_*.json</c> layout. An empty list means "v4, and it genuinely has no groups".
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
    /// The mod's always-applied redirects and metadata edits as they are ON DISK right now — the inverse
    /// of <see cref="WriteRedirects"/>, reading whichever format the folder is in. Null when there is no
    /// manifest or it can't be parsed, which the caller must treat as "unknown", never as "empty".
    ///
    /// Manipulations come back as boxed <see cref="JsonElement"/>s rather than a typed model on purpose:
    /// the only thing that consumes them is <see cref="WriteRedirects"/>, which serialises each entry by
    /// its runtime type, and a JsonElement round-trips through that verbatim. So a read→write cycle
    /// preserves EQDP rows (and any future manipulation kind) without this file having to understand them.
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
    /// Whether the mod puts anything of its OWN into the game — a file redirect, a metadata manipulation,
    /// an IMC group, or a file swap that actually goes somewhere — in its default data or in any option of
    /// any group. Reads whichever format the folder is in.
    ///
    /// This separates the two kinds of folder a Proteus sidecar can sit in: a pure overlay pack, whose
    /// entire visible effect is what Proteus composites for it, and a mod that ALSO ships gear, a body or
    /// textures. Switching the first off in Penumbra costs nothing but its overlays; switching the second
    /// off takes the author's actual mod down with them. <c>DesignBindingService.Restore</c> is the caller
    /// that has to tell them apart.
    ///
    /// An identity swap (A -> A) does not count. Several overlay packs carry exactly one, purely so
    /// Penumbra doesn't see an empty mod, and it redirects nothing.
    ///
    /// True — "has content", so leave it alone — when the manifest is missing or unreadable. The only
    /// caller uses a false to justify DISABLING the mod, and a folder we couldn't read is not one to
    /// disable on a guess.
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
    /// One group, in either format. Three shapes, because Penumbra's group kinds carry their redirects in
    /// three different places: an <c>Imc</c> group edits the game by existing at all; a <c>Combining</c>
    /// group's options are bare flag labels and every redirect sits in a parallel <c>Containers</c> array,
    /// one per combination; every other kind carries them on the options themselves.
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
    /// The manifest's top-level keys, cloned so they outlive the parse. Empty when there is no manifest
    /// or it can't be read. Read once per write and threaded through, so a single recomposite doesn't
    /// parse meta.json twice.
    /// </summary>
    private static Dictionary<string, JsonElement> ReadManifest(string modRoot)
    {
        var preserved = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(modRoot, MetaFile);
            if (!File.Exists(path)) return preserved;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    preserved[p.Name] = p.Value.Clone();
        }
        catch { /* unreadable — caller falls back to the older, universally-legible format */ }
        return preserved;
    }

    /// <summary>The <c>FileVersion</c> in an already-read manifest, defaulting to <see cref="LegacyFileVersion"/>.</summary>
    private static int FileVersionOf(Dictionary<string, JsonElement> manifest)
        => manifest.TryGetValue("FileVersion", out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out var n) ? n : LegacyFileVersion;

    /// <summary>
    /// The folder's declared <c>FileVersion</c>, or <see cref="LegacyFileVersion"/> when there is no
    /// manifest or it can't be read. Penumbra rewrites this field when it migrates a mod on load, so it
    /// is a reliable statement of which format the INSTALLED Penumbra speaks — no version table needed.
    /// </summary>
    public static int ReadFileVersion(string modRoot)
        => FileVersionOf(ReadManifest(modRoot));

    /// <summary>A fresh manifest. See the type remarks for why this is v3 and not v4.</summary>
    /// <param name="version">The mod's own version string, when the source carries one (an imported pack).</param>
    /// <param name="website">The mod's home page, when the source carries one.</param>
    public static string NewMetaJson(string name, string author, string description,
        string? version = null, string? website = null)
        => JsonSerializer.Serialize(new
        {
            FileVersion = LegacyFileVersion,
            Name        = name,
            Author      = author,
            Description = description,
            Version     = version ?? "",
            Website     = website ?? "",
            ModTags     = Array.Empty<string>(),
        }, WriteOptions);

    /// <summary>
    /// Writes one single-select option group, in whichever format the folder is already in — the group
    /// counterpart to <see cref="WriteRedirects"/>, and the only group writer. Every option is empty of
    /// redirects: the group exists purely so Penumbra shows a selector, and Proteus reads the SELECTION
    /// back through <c>OverlayOptionGroup.PenumbraGroupName</c> to decide which overlays to composite.
    /// <para/>
    /// <paramref name="index"/> is the group's ordinal (0-based); LOWER means higher priority.
    /// <c>SidecarDiscoveryService.ReadGroupOrder</c> reads it from the ARRAY POSITION in v4 and from the
    /// <c>group_NNN_</c> FILENAME in v3, and the two formats can only honour it to different degrees:
    /// <list type="bullet">
    /// <item><b>v4</b> splices at exactly <paramref name="index"/>, shifting the groups after it. Past the
    /// end appends.</item>
    /// <item><b>v3</b> takes file number <c>index + 1</c> if it is free, else the next free number above
    /// it. It CANNOT insert between two existing groups, because that would mean renumbering files this
    /// mod's author owns and Proteus didn't write. So on a populated v3 folder a colliding ordinal lands
    /// AFTER the group already holding it, not before.</item>
    /// </list>
    /// On a folder with no other groups — the importer's case, and the only one Proteus creates — both
    /// formats give the same answer.
    /// </summary>
    public static void WriteSingleSelectGroup(
        string modRoot, int index, string name, IReadOnlyList<string> optionNames, int defaultIndex)
    {
        if (optionNames.Count == 0) return;
        if (defaultIndex < 0 || defaultIndex >= optionNames.Count) defaultIndex = 0;
        if (index < 0) index = 0;

        var manifest = ReadManifest(modRoot);
        if (FileVersionOf(manifest) >= SingleFileVersion)
            WriteGroupIntoManifest(modRoot, manifest, index, name, optionNames, defaultIndex);
        else
            WriteLegacyGroupFile(modRoot, index, name, optionNames, defaultIndex);
    }

    /// <summary>
    /// The pre-v4 form: one <c>group_NNN_name.json</c> per group. Two things this has to get right:
    /// <list type="bullet">
    /// <item>Any earlier file for the SAME group name is deleted first. Writing at a different ordinal
    /// changes the filename, and leaving the old file behind would give Penumbra two groups of the same
    /// name and make the ordinal <c>ReadGroupOrder</c> derives depend on directory enumeration order. The
    /// v4 branch replaces by name for the same reason.</item>
    /// <item>The number must not collide with a group Proteus didn't write, for exactly the same reason —
    /// two files numbered 001 make <c>ReadGroupOrder</c> report both at ordinal 1. The requested number is
    /// therefore taken only if free, and otherwise the search walks UP. Walking up rather than renumbering
    /// the folder is deliberate: renaming another author's group files to make room is not this method's
    /// to do, and a half-completed renumber would leave their mod broken.</item>
    /// </list>
    /// </summary>
    private static void WriteLegacyGroupFile(
        string modRoot, int index, string name, IReadOnlyList<string> optionNames, int defaultIndex)
    {
        var taken = new HashSet<int>();
        try
        {
            // ToList: the enumeration is being deleted from. Every file carrying this group's name goes,
            // the one we're about to write included — AtomicWrite recreates it.
            foreach (var file in Directory.EnumerateFiles(modRoot, "group_*.json").ToList())
            {
                if (string.Equals(GroupNameOf(file), name, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { /* AtomicWrite still overwrites a same-named file */ }
                    continue;
                }
                // Same parse ReadGroupOrder uses: group_002_fabric.json -> 2. A file we can't read a number
                // out of can't be collided with either, so it simply doesn't reserve one.
                var parts = Path.GetFileNameWithoutExtension(file).Split('_');
                if (parts.Length >= 2 && int.TryParse(parts[1], out var n)) taken.Add(n);
            }
        }
        catch { /* modRoot missing or unreadable — the write below creates what it needs */ }

        var number = index + 1;
        while (taken.Contains(number)) number++;

        AtomicWrite(
            Path.Combine(modRoot, LegacyGroupFileName(number - 1, name)),
            JsonSerializer.Serialize(BuildGroup(number - 1, name, optionNames, defaultIndex), WriteOptions));
    }

    /// <summary>The <c>Name</c> inside a v3 group file, or null when it can't be read.</summary>
    private static string? GroupNameOf(string groupFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(groupFile));
            return doc.RootElement.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;
        }
        catch { return null; /* malformed — leave it alone rather than delete something unread */ }
    }

    /// <summary>
    /// The v3 group filename: <c>group_001_stocking pattern.json</c>. Penumbra reads the name from the
    /// file's <c>Name</c> field, not the filename, so the sanitisation here only has to produce something
    /// the filesystem accepts.
    /// </summary>
    private static string LegacyGroupFileName(int index, string name)
    {
        var safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return $"group_{index + 1:000}_{safe.ToLowerInvariant()}.json";
    }

    /// <summary>
    /// The shape both formats share. <c>DefaultSettings</c> is the selected option's INDEX for a Single
    /// group (it is a bitmask only for Multi), and each option carries no redirects of its own.
    /// </summary>
    private static object BuildGroup(int index, string name, IReadOnlyList<string> optionNames, int defaultIndex)
        => new
        {
            Version         = 0,
            Name            = name,
            Description     = "",
            Image           = "",
            Page            = 0,
            Priority        = index,
            Type            = "Single",
            DefaultSettings = defaultIndex,
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
    /// Splices the group into a v4 manifest's <c>Groups</c> array AT <paramref name="index"/>, replacing any
    /// group of the same name and preserving every other key — the same care <see cref="WriteDefaultData"/>
    /// takes, and for the same reason: <c>Identifier</c> is how Penumbra keys the mod.
    /// </summary>
    private static void WriteGroupIntoManifest(
        string modRoot, Dictionary<string, JsonElement> preserved,
        int index, string name, IReadOnlyList<string> optionNames, int defaultIndex)
    {
        // The surviving groups in order. Any group of the same name is DROPPED rather than kept alongside
        // the new one — a duplicate name is something Penumbra would have to disambiguate, and it would
        // also make the ordinal this method promises ambiguous.
        var others = new List<JsonElement>();
        if (preserved.TryGetValue("Groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            foreach (var g in groups.EnumerateArray())
                if (!(g.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String
                      && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase)))
                    others.Add(g);

        // Clamped, not rejected: an index past the end is the ordinary "put it last" request, and a caller
        // that removed a group since computing the index shouldn't fail over an off-by-one.
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
                if (i == slot) JsonSerializer.Serialize(w, BuildGroup(slot, name, optionNames, defaultIndex));
                others[i].WriteTo(w);
            }
            if (slot >= others.Count)
                JsonSerializer.Serialize(w, BuildGroup(slot, name, optionNames, defaultIndex));
            w.WriteEndArray();

            w.WriteEndObject();
        }

        AtomicWrite(Path.Combine(modRoot, MetaFile), System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>
    /// Writes the mod's always-applied redirects in whatever format the folder is already in: the
    /// <c>DefaultData</c> object for v4+, a separate <c>default_mod.json</c> for older Penumbra.
    ///
    /// This is the ONLY entry point for writing redirects. The format-specific writers are private on
    /// purpose — calling one directly skips this dispatch, which is exactly how overlays silently stop
    /// applying for anyone on the other format.
    /// </summary>
    public static void WriteRedirects(
        string modRoot, string modName,
        IDictionary<string, string> files,
        IDictionary<string, string>? swaps = null,
        IReadOnlyList<object>? manipulations = null)
    {
        var manifest = ReadManifest(modRoot);
        if (FileVersionOf(manifest) >= SingleFileVersion)
        {
            WriteDefaultData(modRoot, modName, manifest, files, swaps, manipulations);
            // Penumbra migrated this folder itself; drop the default_mod.json it left behind so a stale
            // copy can't be mistaken for the live redirect set.
            CleanLegacyFiles(modRoot);
        }
        else
        {
            WriteLegacyDefaultMod(modRoot, files, swaps, manipulations);
        }
    }

    /// <summary>
    /// The pre-v4 <c>default_mod.json</c>: <c>{ "Files": {…}, "Swaps": {…}, "Manipulations": [] }</c>.
    /// Note the key is <c>Swaps</c> here; v4 renamed it to <c>FileSwaps</c> inside <c>DefaultData</c>.
    /// </summary>
    private static void WriteLegacyDefaultMod(
        string modRoot,
        IDictionary<string, string> files,
        IDictionary<string, string>? swaps = null,
        IReadOnlyList<object>? manipulations = null)
    {
        var obj = new
        {
            Files         = files,
            Swaps         = swaps ?? new Dictionary<string, string>(),
            Manipulations = manipulations ?? Array.Empty<object>(),
        };
        AtomicWrite(Path.Combine(modRoot, LegacyDefaultMod), JsonSerializer.Serialize(obj, WriteOptions));
    }

    /// <summary>
    /// Replaces the manifest's <c>DefaultData</c>, preserving every other key — critically
    /// <c>Identifier</c>, which is how Penumbra keys the mod, and any <c>Groups</c>/<c>ModTags</c>/
    /// <c>Image</c> the user set in Penumbra's UI. Creates the manifest if it is missing.
    /// <paramref name="preserved"/> is the already-read manifest, so this doesn't re-parse it.
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
    /// Writes via a sibling temp file + atomic move, retrying with backoff: Penumbra's own file watcher
    /// can hold the target open for a moment right after a reload.
    /// <para/>
    /// The temp file is flushed to the DEVICE before the move, not merely handed to the OS cache. A
    /// rename is ordered, the data behind it is not: NTFS can commit the directory entry while the
    /// file's blocks are still unwritten, so a crash or power loss in that window leaves a file of the
    /// right length filled with zeros. Penumbra reports reading one as
    /// <c>'0x00' is an invalid start of a value. LineNumber: 0 | BytePositionInLine: 0</c> and drops
    /// the mod — after which Proteus publishes redirects into a mod Penumbra has never heard of, and
    /// every path we own silently resolves elsewhere. Cheap insurance: these files are a few KB, and
    /// they are written once per composite at most.
    /// </summary>
    /// <param name="maxRetries">
    /// How many times to re-attempt the MOVE, sleeping 50 ms and doubling between tries. The default
    /// spends up to ~1.55 s outlasting Penumbra's watcher, which is the right trade for a background
    /// write — nobody is waiting on it.
    /// <para/>
    /// A caller on the ImGui draw thread or the framework thread must pass a small budget instead: those
    /// sleeps are frozen frames, and the editor provokes exactly the contention this loop waits out (it
    /// saves and then immediately recomposites, so Penumbra reloads and grabs the file just as the next
    /// edit lands). Losing that race costs one save that the next edit rewrites anyway; spending a second
    /// and a half of someone's colour-slider drag to win it does not pay.
    /// </param>
    public static void AtomicWrite(string target, string contents, int maxRetries = 5)
    {
        var tmp = target + "." + Guid.NewGuid().ToString("N") + TempSuffix;

        // UTF-8 without a BOM, matching what File.WriteAllText would have produced.
        var bytes = new System.Text.UTF8Encoding(false).GetBytes(contents);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                   bufferSize: 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, target, overwrite: true); return; }
            // Shift clamped at 5 so an oversized budget backs off to 1.6 s a try, never overflows into one.
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

        // EVERY AtomicWrite target, not just default_mod.json's: the sweep used to name that one file, so
        // once meta.json and metadata.json started going through AtomicWrite their temps were stranded for
        // good. meta.json sits in the mod root, metadata.json one level down in the sidecar folder.
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
