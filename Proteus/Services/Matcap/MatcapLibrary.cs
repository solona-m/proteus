using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Proteus.Services.Matcap;

/// <summary>One option of a library slot: its name, and the file it switches the slot's texture to.</summary>
public sealed record MatcapOption(string Name, string File);

/// <summary>
/// One matcap slot of an installed library mod: a Single or Multi group whose options switch one shared texture path,
/// which the mod's own <c>characterreflection</c> material samples as its matcap. A material elsewhere that names
/// <see cref="Texture"/> shows whichever option the collection has on — so a library is referenced, never copied.
/// </summary>
/// <param name="Mod">The library's folder under Penumbra's mods root.</param>
/// <param name="Name">What the library calls itself.</param>
/// <param name="Website">The library's own link, for the share note (its terms ask users to link it).</param>
/// <param name="Group">The group that switches the slot.</param>
/// <param name="Texture">The shared game path the options switch.</param>
/// <param name="Template">The library's material reading the slot (mod-relative), for reference.</param>
/// <param name="AlsoRedirects">What else the library publishes when switched on with its defaults (game paths), so
/// switching it on is never a surprise.</param>
public sealed record MatcapLibrary(
    string Mod, string Name, string Website, string Group, string Texture, string Template,
    IReadOnlyList<MatcapOption> Options, IReadOnlyList<string> AlsoRedirects)
{
    /// <summary>"Library — group", as the panel lists it.</summary>
    public string Label => $"{Name} — {Group}";

    /// <summary>
    /// Game-path roots a library slot may NOT sit under: a texture inside an item, character or monster folder is that
    /// item's own, switched by its own mod, not a shared library.
    /// </summary>
    internal static readonly string[] ItemFolders =
        ["chara/equipment/", "chara/accessory/", "chara/weapon/", "chara/human/", "chara/monster/", "chara/demihuman/"];

    /// <summary>
    /// Every matcap slot one mod folder holds, by the generic rule (any layout, enabled or not). Empty for a mod that has
    /// none or can't be read. Reads manifests, then the headers of the mod's .mtrl files only when a candidate switch
    /// exists.
    /// </summary>
    public static List<MatcapLibrary> Detect(string modRoot, string dir, string name)
    {
        var found = new List<MatcapLibrary>();
        try
        {
            var groups = ReadGroups(modRoot);
            if (groups.Count == 0) return found;

            // Pass 1: switches — a .tex path that two or more options of one group send to different files.
            var candidates = new List<(string Group, string Texture, List<MatcapOption> Options)>();
            foreach (var g in groups)
            {
                var type = g.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";
                if (!type.Equals("Single", StringComparison.OrdinalIgnoreCase)
                 && !type.Equals("Multi", StringComparison.OrdinalIgnoreCase)) continue;
                var gname = g.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                if (!g.TryGetProperty("Options", out var opts) || opts.ValueKind != JsonValueKind.Array) continue;

                var byPath = new Dictionary<string, List<MatcapOption>>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in opts.EnumerateArray())
                {
                    var oname = o.TryGetProperty("Name", out var on) ? on.GetString() ?? "" : "";
                    if (!o.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Object) continue;
                    foreach (var p in files.EnumerateObject())
                    {
                        if (!p.Name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) continue;
                        if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() is not { Length: > 0 } rel) continue;
                        var path = p.Name.ToLowerInvariant().Replace('\\', '/');
                        if (ItemFolders.Any(f => path.StartsWith(f, StringComparison.Ordinal))) continue;
                        if (!byPath.TryGetValue(path, out var list)) byPath[path] = list = [];
                        list.Add(new MatcapOption(oname, rel));
                    }
                }
                foreach (var (path, list) in byPath)
                    if (list.Count >= 2 && list.Select(x => x.File.Replace('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2)
                        candidates.Add((gname, path, list));
            }
            if (candidates.Count == 0) return found;

            // Pass 2: which of those a reflection material of this mod samples as its matcap (any option, any state).
            var redirects = PenumbraModMeta.ReadAllRedirects(modRoot);
            var sampled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // texture → material rel
            foreach (var r in redirects)
            {
                if (!r.GamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)) continue;
                var full = Path.Combine(modRoot, r.File.Replace('\\', Path.DirectorySeparatorChar));
                byte[] bytes;
                try { if (!File.Exists(full) || new FileInfo(full).Length > 1 << 20) continue; bytes = File.ReadAllBytes(full); }
                catch { continue; }
                var h = MatcapMaterial.ReadHeader(bytes);
                if (h is not { IsReflection: true }) continue;
                foreach (var (id, tex) in h.Samplers)
                {
                    if (id is MatcapMaterial.SamplerNormal or MatcapMaterial.SamplerMask) continue;
                    if (tex < 0 || tex >= h.Textures.Count) continue;
                    sampled.TryAdd(h.Textures[tex].ToLowerInvariant(), r.File);
                }
            }

            var (libName, website) = ReadMeta(modRoot, name);
            var defaults = DefaultRedirects(modRoot, groups);
            var slotPaths = candidates.Select(c => c.Texture).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var also = defaults.Where(p => !slotPaths.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase)
                               .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var (group, texture, options) in candidates)
                if (sampled.TryGetValue(texture, out var template))
                    found.Add(new MatcapLibrary(dir, libName, website, group, texture, template, options, also));
        }
        catch
        {
            // An unreadable mod is not a library.
        }
        return found;
    }

    /// <summary>
    /// The texture paths a mod publishes outside item folders, in any option — what makes it a PROVIDER of a slot path,
    /// library or not (a mod that bundled a library's textures would be one).
    /// </summary>
    public static List<string> CommonTextures(string modRoot) =>
        PenumbraModMeta.ReadAllRedirects(modRoot)
            .Select(r => r.GamePath.ToLowerInvariant().Replace('\\', '/'))
            .Where(p => p.EndsWith(".tex", StringComparison.Ordinal) && !ItemFolders.Any(f => p.StartsWith(f, StringComparison.Ordinal)))
            .Distinct().ToList();

    /// <summary>The mod's groups in either layout: v4's <c>Groups</c> array, else v3's group files by name.</summary>
    internal static List<JsonElement> ReadGroups(string modRoot)
    {
        if (PenumbraModMeta.TryReadRawGroups(modRoot) is { } v4) return v4;
        var list = new List<JsonElement>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(modRoot, "group_*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                list.Add(doc.RootElement.Clone());
            }
        }
        catch { }
        return list;
    }

    /// <summary>The mod's own name and website from meta.json, falling back to <paramref name="fallback"/>.</summary>
    internal static (string Name, string Website) ReadMeta(string modRoot, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modRoot, PenumbraModMeta.MetaFile)));
            var root = doc.RootElement;
            var n = root.TryGetProperty("Name", out var nn) ? nn.GetString() : null;
            var w = root.TryGetProperty("Website", out var ww) ? ww.GetString() : null;
            return (string.IsNullOrWhiteSpace(n) ? fallback : n!, w ?? "");
        }
        catch { return (fallback, ""); }
    }

    /// <summary>What switching the mod on with its default settings publishes: default data plus each group's defaults.</summary>
    private static List<string> DefaultRedirects(string modRoot, List<JsonElement> groups)
    {
        var paths = new List<string>();
        if (PenumbraModMeta.TryReadDefaultData(modRoot) is { } dd) paths.AddRange(dd.Files.Keys);
        foreach (var g in groups)
        {
            var type = g.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";
            long def = g.TryGetProperty("DefaultSettings", out var d) && d.TryGetInt64(out var v) ? v : 0;
            if (!g.TryGetProperty("Options", out var opts) || opts.ValueKind != JsonValueKind.Array) continue;
            int i = 0;
            foreach (var o in opts.EnumerateArray())
            {
                bool on = type.Equals("Single", StringComparison.OrdinalIgnoreCase) ? i == def
                        : type.Equals("Multi", StringComparison.OrdinalIgnoreCase) && (def & (1L << i)) != 0;
                if (on && o.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Object)
                    paths.AddRange(files.EnumerateObject().Select(p => p.Name));
                i++;
            }
        }
        return paths.Select(p => p.ToLowerInvariant().Replace('\\', '/')).ToList();
    }
}
