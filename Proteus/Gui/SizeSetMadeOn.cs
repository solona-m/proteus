using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Gui;

/// <summary>
/// The size set's "Made on" choice: the body mod the open mod's chest and legs were made on, for the sizes Proteus cannot
/// read itself (see <see cref="Services.SizeSetRefit.Request.MadeOn"/>). Kept for the open mod alone and never saved — a
/// choice left over from one mod would read the next one's sizes on the wrong body.
/// </summary>
internal sealed class SizeSetMadeOn
{
    private string? openMod;

    /// <summary>The body mod's folder, or null to find it automatically.</summary>
    internal string? Dir { get; private set; }

    /// <summary>A mod is open: another than before starts again from "find it for me".</summary>
    internal void Open(string modDir)
    {
        if (string.Equals(openMod, modDir, StringComparison.OrdinalIgnoreCase)) return;
        openMod = modDir;
        Dir = null;
    }

    internal void Choose(string? dir) => Dir = dir;

    /// <summary>The body mods installed now: one chosen that is no longer among them is let go.</summary>
    internal void Prune(IEnumerable<string> installedDirs)
    {
        if (Dir != null && !installedDirs.Contains(Dir, StringComparer.OrdinalIgnoreCase)) Dir = null;
    }

    /// <summary>The body mods to choose from, by name: every installed one but the open mod itself.</summary>
    internal static List<(string Dir, string Name)> Choices(IEnumerable<(string Dir, string Name)> bodies, string openModDir)
        => bodies.Where(b => !string.Equals(b.Dir, openModDir, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                 .ToList();
}
