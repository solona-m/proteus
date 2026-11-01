using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Interop;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>One drawn model as <c>/state</c> reports it.</summary>
/// <param name="Slot">The renderer's slot, or -1 for a model Penumbra resolves that the renderer has not drawn.</param>
/// <param name="Loaded">The file the renderer loaded, Penumbra's <c>|…|</c> prefix stripped; null without a handle.</param>
/// <param name="GamePath">The game path it stands in for, when Penumbra knows it.</param>
/// <param name="Resolved">The file Penumbra resolved it to.</param>
/// <param name="ModDir">The mod supplying it, when the file lies under Penumbra's mods root.</param>
internal sealed record ModelEntry(int Slot, string? Loaded, string? GamePath, string? Resolved, string? ModDir);

/// <summary>
/// Joins what the renderer drew to what Penumbra resolved. The renderer names a modded file
/// <c>|prefix|E:\mods\X\a.mdl</c>; Penumbra's resource map names it <c>E:\mods\X\a.mdl</c>; so the join strips the
/// prefix (<see cref="LiveCharacter.FilePath"/>) and compares case-insensitively with either separator.
/// </summary>
internal static class ModelJoin
{
    internal static string Normalize(string path) => LiveCharacter.FilePath(path).Replace('\\', '/').Trim();

    /// <param name="loaded">The renderer's models (slot, raw file name).</param>
    /// <param name="resourceMap">Penumbra's map, resolved file → game paths; null when unavailable.</param>
    /// <param name="modsRoot">Penumbra's mods folder; null when unknown.</param>
    internal static List<ModelEntry> Join(IReadOnlyList<(int Slot, string? File)> loaded,
                                          IReadOnlyDictionary<string, HashSet<string>>? resourceMap, string? modsRoot)
    {
        // Models only: the map carries every resource type.
        var models = new Dictionary<string, (string Resolved, string GamePath)>(StringComparer.OrdinalIgnoreCase);
        var byGamePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (resourceMap != null)
            foreach (var (resolved, gamePaths) in resourceMap)
            {
                var gamePath = gamePaths.FirstOrDefault(g => g.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase));
                if (gamePath == null && !resolved.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) continue;
                gamePath ??= gamePaths.FirstOrDefault() ?? resolved;
                models[Normalize(resolved)] = (resolved, gamePath);
                byGamePath[gamePath.Replace('\\', '/')] = resolved;
            }

        var result = new List<ModelEntry>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, file) in loaded)
        {
            string? norm = file == null ? null : Normalize(file);
            string? clean = file == null ? null : LiveCharacter.FilePath(file);
            if (norm != null && models.TryGetValue(norm, out var hit))
            {
                matched.Add(norm);
                result.Add(new ModelEntry(slot, clean, hit.GamePath, hit.Resolved, ModDirOf(hit.Resolved, modsRoot)));
            }
            else if (norm != null && byGamePath.TryGetValue(norm, out var resolvedForGame))
            {
                // The game's own file, drawn by game path: Penumbra lists it resolved to itself or to a mod's file.
                matched.Add(Normalize(resolvedForGame));
                result.Add(new ModelEntry(slot, clean, norm, resolvedForGame, ModDirOf(resolvedForGame, modsRoot)));
            }
            else
                result.Add(new ModelEntry(slot, clean, null, null, null));
        }
        foreach (var (norm, (resolved, gamePath)) in models)
            if (!matched.Contains(norm))
                result.Add(new ModelEntry(-1, null, gamePath, resolved, ModDirOf(resolved, modsRoot)));
        return result;
    }

    /// <summary>The first folder under the mods root a file lies in; null outside it.</summary>
    internal static string? ModDirOf(string? file, string? modsRoot)
    {
        if (file == null || string.IsNullOrEmpty(modsRoot)) return null;
        string f = file.Replace('\\', '/');
        string root = modsRoot.Replace('\\', '/').TrimEnd('/') + "/";
        if (!f.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        string rest = f[root.Length..];
        int slash = rest.IndexOf('/');
        return slash <= 0 ? null : rest[..slash];
    }
}
