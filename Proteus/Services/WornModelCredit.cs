using System;
using System.Collections.Generic;
using System.IO;

namespace Proteus.Services;

/// <summary>
/// Which mods supply the models a character is drawing, as Studio's "worn" marks count them.
/// <para/>
/// Every drawn file is credited to the mod folder it lives in. One kind of file needs more: a ring (or other worn
/// accessory) that the second skin is APPENDED into is drawn out of our output mod, merged with the shell, so the file
/// the game loads lives in <see cref="SidecarDiscoveryService.ManagedModDir"/> and the mod that supplied the ring is
/// nowhere in the walk. The composite records where it read that ring from (see
/// <see cref="CompositorService.GetAppendHostSource"/>), and that mod is credited too.
/// <para/>
/// Kept out of the panel so it can be tested without Penumbra: every lookup comes in as a delegate.
/// </summary>
internal static class WornModelCredit
{
    /// <summary>
    /// Every drawn model file as (mod folder, path inside it with forward slashes) — the spelling a mod's redirects are
    /// compared in — plus, for a file our output mod draws for an append host, the mod that supplied the host's model.
    /// </summary>
    /// <param name="drawnFiles">The resolved model files the character is drawing.</param>
    /// <param name="modsRoot">Penumbra's mod folder.</param>
    /// <param name="gamePathOf">A drawn file's model game path, or null when it is not known. Asked only for files in our
    /// output mod, so a character Proteus draws nothing for costs no lookup.</param>
    /// <param name="appendHostSource">The file an append host's model was read from, by game path; null when that game
    /// path is not an append host.</param>
    internal static List<(string Mod, string Rel)> Credit(IEnumerable<string> drawnFiles, string modsRoot,
                                                          Func<string, string?> gamePathOf,
                                                          Func<string, string?> appendHostSource)
    {
        var found = new List<(string, string)>();
        foreach (var file in drawnFiles)
        {
            if (!HatCompatService.InMods(file, modsRoot, out var modRoot, out var rel)) continue;
            var mod = Path.GetFileName(modRoot);
            found.Add((mod, rel));

            if (!IsManaged(mod) || gamePathOf(file) is not { } gamePath) continue;
            if (appendHostSource(gamePath) is not { Length: > 0 } source || !Path.IsPathRooted(source)) continue;
            if (!HatCompatService.InMods(source, modsRoot, out var sourceRoot, out var sourceRel)) continue;

            // A source in our own folder is a live-preview copy the composite read while a brush was open: crediting it
            // would only name our output mod a second time.
            var sourceMod = Path.GetFileName(sourceRoot);
            if (!IsManaged(sourceMod)) found.Add((sourceMod, sourceRel));
        }
        return found;
    }

    private static bool IsManaged(string mod)
        => string.Equals(mod, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase);
}
