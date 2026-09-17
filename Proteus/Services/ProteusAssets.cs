using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Where the plugin's out-of-band assets are served from: large files are release assets fetched once into
/// the config directory, never shipped in the plugin zip.
/// </summary>
public static class ProteusAssets
{
    /// <summary>
    /// Edge cache in front of the GitHub release assets, or empty for GitHub alone; GitHub always stays behind it.
    /// Must end in a slash and serve <c>&lt;tag&gt;/&lt;file&gt;</c> exactly as the GitHub base does
    /// (see <c>worker/src/index.js</c>).
    /// </summary>
    public const string MirrorBase = "https://dl.solona.info/";

    public const string GitHubBase = "https://github.com/solona-m/proteus/releases/download/";

    /// <summary>
    /// Directories an older install may have left <paramref name="subdir"/> in, most-likely first. Includes
    /// sibling version folders, since Dalamud installs each plugin version into its own folder.
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
            // An unreadable plugin folder is not worth failing a load over; the assets are re-downloaded.
        }

        return dirs;
    }

    /// <summary>Sources for one release tag, most-preferred first.</summary>
    public static string[] BaseUrls(string tag) =>
        MirrorBase.Length > 0
            ? [MirrorBase + tag + "/", GitHubBase + tag + "/"]
            : [GitHubBase + tag + "/"];
}
