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
/// wrote and never migrated. Writes are v4 only.
/// </summary>
internal static class PenumbraModMeta
{
    public const string MetaFile        = "meta.json";
    public const string LegacyDefaultMod = "default_mod.json";
    public const int    FileVersion     = 4;

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

    /// <summary>A fresh v4 manifest with an empty <c>DefaultData</c> and no groups.</summary>
    public static string NewMetaJson(string name, string author, string description)
        => JsonSerializer.Serialize(new
        {
            FileVersion,
            Identifier  = Guid.NewGuid().ToString(),
            Name        = name,
            Author      = author,
            Description = description,
            Version     = "",
            Website     = "",
            ModTags     = Array.Empty<string>(),
            DefaultData = new
            {
                Files         = new Dictionary<string, string>(),
                FileSwaps     = new Dictionary<string, string>(),
                Manipulations = Array.Empty<object>(),
            },
            Groups = Array.Empty<object>(),
        }, WriteOptions);

    /// <summary>
    /// Replaces the manifest's <c>DefaultData</c>, preserving every other key — critically
    /// <c>Identifier</c>, which is how Penumbra keys the mod, and any <c>Groups</c>/<c>ModTags</c>/
    /// <c>Image</c> the user set in Penumbra's UI. Creates the manifest if it is missing.
    /// </summary>
    public static void WriteDefaultData(
        string modRoot, string modName,
        IDictionary<string, string> files,
        IDictionary<string, string>? swaps = null,
        IReadOnlyList<object>? manipulations = null)
    {
        var path = Path.Combine(modRoot, MetaFile);

        // Round-trip the existing manifest key-by-key so nothing outside DefaultData is lost.
        var preserved = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in doc.RootElement.EnumerateObject())
                        preserved[p.Name] = p.Value.Clone();
            }
        }
        catch { /* unparseable manifest — rebuild it from scratch below */ }

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("FileVersion", FileVersion);
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
