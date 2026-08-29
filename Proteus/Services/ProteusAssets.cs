using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Where the plugin's out-of-band assets are served from.
/// <para/>
/// Nothing large ships inside the plugin zip: Dalamud re-downloads that on every update, and with a
/// testing tag going out most days that multiplies by every tester. The UV maps and the starter effect
/// library are therefore release assets fetched once into the config directory, which survives updates.
/// </summary>
public static class ProteusAssets
{
    /// <summary>
    /// Edge cache in front of the GitHub release assets, or empty while none is deployed — in which
    /// case <see cref="BaseUrls"/> is simply GitHub alone and everything behaves as it always did.
    /// The mirror is a preference, never a dependency: GitHub always stays in the list behind it.
    /// <para/>
    /// Must end in a slash, and must serve <c>&lt;tag&gt;/&lt;file&gt;</c> exactly as the GitHub base
    /// does, so the two are interchangeable — see <c>worker/src/index.js</c>, which is deliberately
    /// shaped to mirror the origin's path layout rather than invent its own.
    /// Example: <c>https://dl.example.com/</c>
    /// </summary>
    public const string MirrorBase = "https://dl.solona.info/";

    public const string GitHubBase = "https://github.com/solona-m/proteus/releases/download/";

    /// <summary>
    /// Directories an older install may have left <paramref name="subdir"/> in, most-likely first.
    /// <para/>
    /// Dalamud installs every plugin VERSION into its own folder — <c>installedPlugins/Proteus/
    /// 2608.309.0.0/</c>, <c>.../2608.312.0.0/</c> — and keeps the previous one around for a while.
    /// So immediately after an update the assets are in a SIBLING directory, and the plugin's own
    /// <c>AssemblyLocation</c> is a freshly-unpacked folder that has none of them.
    /// <para/>
    /// Looking only at the assembly directory therefore finds nothing on exactly the update that
    /// matters, and every existing user re-downloads a quarter of a gigabyte — the precise waste this
    /// relocation exists to stop. A dev install hides the bug completely, because it loads from one
    /// stable path that never moves.
    /// <para/>
    /// Newest sibling first: version folders sort lexicographically close enough to newest-last for
    /// this purpose, and any complete copy is as good as any other, so ordering is a preference rather
    /// than a correctness requirement.
    /// </summary>
    public static List<string> LegacyAssetDirs(string? assemblyDir, string subdir)
    {
        var dirs = new List<string>();
        if (string.IsNullOrEmpty(assemblyDir)) return dirs;

        dirs.Add(Path.Combine(assemblyDir, subdir));

        try
        {
            var parent = Directory.GetParent(assemblyDir);
            if (parent is { Exists: true })
            {
                var self = Path.GetFullPath(assemblyDir);
                var siblings = parent.GetDirectories()
                    .Where(d => !string.Equals(Path.GetFullPath(d.FullName), self,
                                               StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase);

                foreach (var s in siblings)
                    dirs.Add(Path.Combine(s.FullName, subdir));
            }
        }
        catch
        {
            // An unreadable plugin folder is not worth failing a load over: the worst case is that the
            // assets are re-downloaded, which is what happened before this existed anyway.
        }

        return dirs;
    }

    /// <summary>Sources for one release tag, most-preferred first.</summary>
    public static string[] BaseUrls(string tag) =>
        MirrorBase.Length > 0
            ? [MirrorBase + tag + "/", GitHubBase + tag + "/"]
            : [GitHubBase + tag + "/"];
}
