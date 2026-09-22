using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Proteus.Services;

/// <summary>
/// Finds the picture a mod's author shipped with it, for the Mods tab's hover preview. Resolved once per mod folder
/// on a worker, never on the draw thread, and cached; <see cref="Invalidate"/> marks every answer stale, and a stale
/// answer keeps being served while its replacement is looked up, so an open preview never blinks out.
/// </summary>
public sealed class ModPreviewService
{
    /// <summary>Formats Dalamud's texture provider decodes from a plain file (WIC): no DDS/TEX, which are game textures.</summary>
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    /// <summary>File names (without extension) an author uses for a cover picture, most specific first.</summary>
    private static readonly string[] CoverNames = ["preview", "cover", "thumbnail", "thumb", "banner", "icon"];

    /// <summary>Folders beside meta.json that commonly hold the pack's pictures.</summary>
    private static readonly string[] ImageFolders = ["", "images", "Images", "preview", "previews", "Preview"];

    private readonly ConcurrentDictionary<string, (string? Path, int Generation)> resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.OrdinalIgnoreCase);
    private int generation;

    /// <summary>
    /// The absolute path of <paramref name="modRoot"/>'s picture, or null when it has none or is still being looked
    /// up. Cheap enough to call every frame.
    /// </summary>
    public string? ImagePathFor(string? modRoot)
    {
        if (string.IsNullOrEmpty(modRoot)) return null;

        var have = resolved.TryGetValue(modRoot, out var r);
        if ((!have || r.Generation != Volatile.Read(ref generation)) && pending.TryAdd(modRoot, 0))
        {
            var gen = Volatile.Read(ref generation);
            Task.Run(() =>
            {
                string? path = null;
                try { path = Resolve(modRoot); }
                catch (Exception ex) { Plugin.Log.Debug(ex, "[Proteus] preview lookup failed for {0}", modRoot); }
                finally
                {
                    resolved[modRoot] = (path, gen);
                    pending.TryRemove(modRoot, out _);
                }
            });
        }
        return have ? r.Path : null;
    }

    /// <summary>Look every mod up again on its next request, serving the old answer meanwhile.</summary>
    public void Invalidate() => Interlocked.Increment(ref generation);

    /// <summary>
    /// The picture for the mod in <paramref name="modRoot"/>: meta.json's <c>Image</c>, then the first image an option
    /// group names, then a conventionally named file beside meta.json or in an images folder. Null for none.
    /// </summary>
    internal static string? Resolve(string modRoot)
    {
        if (!Directory.Exists(modRoot)) return null;

        // 1. meta.json: the mod's own Image, then (v4 layout) images named inside its Groups.
        var meta = Path.Combine(modRoot, "meta.json");
        if (File.Exists(meta))
        {
            foreach (var candidate in ImagesIn(meta))
                if (Accept(modRoot, candidate) is { } hit)
                    return hit;
        }

        // 2. The legacy per-group files, in Penumbra's order (their names carry the ordinal).
        foreach (var group in Directory.EnumerateFiles(modRoot, "group_*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            foreach (var candidate in ImagesIn(group))
                if (Accept(modRoot, candidate) is { } hit)
                    return hit;

        // 3. Convention: a cover-named file, then any picture in a folder that exists only to hold pictures.
        foreach (var folder in ImageFolders)
        {
            var dir = folder.Length == 0 ? modRoot : Path.Combine(modRoot, folder);
            if (!Directory.Exists(dir)) continue;

            var files = Directory.EnumerateFiles(dir)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var name in CoverNames)
                if (files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).StartsWith(name, StringComparison.OrdinalIgnoreCase)) is { } named)
                    return named;

            // The mod root itself holds more than pictures (a texture export, a readme image), so only a picture
            // folder gives up its first file.
            if (folder.Length > 0 && files.Count > 0)
                return files[0];
        }

        return null;
    }

    /// <summary>Every non-empty <c>Image</c> string in a Penumbra manifest: the file's own, then its options'.</summary>
    private static IEnumerable<string> ImagesIn(string jsonPath)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllBytes(jsonPath),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch
        {
            yield break;
        }

        using (doc)
        {
            var found = new List<string>();
            Collect(doc.RootElement, found, depth: 0);
            foreach (var s in found)
                yield return s;
        }
    }

    /// <summary>Walk objects and arrays for <c>Image</c> properties, shallowly: manifests nest at most a few levels.</summary>
    private static void Collect(JsonElement e, List<string> into, int depth)
    {
        if (depth > 4) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (string.Equals(p.Name, "Image", StringComparison.OrdinalIgnoreCase)
                        && p.Value.ValueKind == JsonValueKind.String
                        && p.Value.GetString() is { Length: > 0 } s)
                        into.Add(s);
                    else if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        Collect(p.Value, into, depth + 1);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    Collect(item, into, depth + 1);
                break;
        }
    }

    /// <summary>
    /// <paramref name="candidate"/> as an absolute path when it names a readable picture inside the mod folder;
    /// null for a URL, a path that escapes the folder, a missing file or an unsupported format.
    /// </summary>
    private static string? Accept(string modRoot, string candidate)
    {
        if (candidate.Contains("://", StringComparison.Ordinal)) return null;
        try
        {
            var root = Path.GetFullPath(modRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, candidate.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            if (!Extensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase)) return null;
            return File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }
}
