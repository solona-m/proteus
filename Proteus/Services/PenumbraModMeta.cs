using System;
using System.Collections.Generic;
using System.IO;
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

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

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
    public static string NewMetaJson(string name, string author, string description)
        => JsonSerializer.Serialize(new
        {
            FileVersion = LegacyFileVersion,
            Name        = name,
            Author      = author,
            Description = description,
            Version     = "",
            Website     = "",
            ModTags     = Array.Empty<string>(),
        }, WriteOptions);

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
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
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
    /// </summary>
    public static void AtomicWrite(string target, string contents)
    {
        var tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, contents);
        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, target, overwrite: true); return; }
            catch (Exception) when (i < 5) { Thread.Sleep(50 << i); } // 50 100 200 400 800ms
            catch { try { File.Delete(tmp); } catch { } throw; }      // don't leave the temp behind
        }
    }

    /// <summary>
    /// Removes a v3 <c>default_mod.json</c> now superseded by <c>DefaultData</c>, plus any orphaned
    /// <c>.tmp</c> siblings left by an interrupted <see cref="AtomicWrite"/> before this cleanup existed.
    /// </summary>
    public static void CleanLegacyFiles(string modRoot)
    {
        try
        {
            var legacy = Path.Combine(modRoot, LegacyDefaultMod);
            if (File.Exists(legacy)) File.Delete(legacy);
            foreach (var tmp in Directory.EnumerateFiles(modRoot, LegacyDefaultMod + ".*.tmp"))
                try { File.Delete(tmp); } catch { }
        }
        catch { /* best effort — leftovers are inert */ }
    }
}
