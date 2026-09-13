using System;
using System.Collections.Generic;
using System.IO;
using CheapLoc;

namespace Proteus.Services;

/// <summary>
/// Which of Proteus's in-place model edits has to be undone before another, so they are undone in the reverse
/// of the order they were made.
/// <para/>
/// Three features edit a mod's model files in place — part switches, hat fitting and the brush — and each
/// keeps its OWN backup of a file, taken the first time it edits it. So each feature's idea of "the original"
/// is whatever the feature before it left. Undoing out of order restores bytes from before a change that has
/// already been undone: reverting part switches under a later brush edit throws the brush edit away while its
/// record still claims the file, and undoing the brush afterwards puts the removed switch attributes back with
/// no Penumbra group left to drive them.
/// <para/>
/// Order is read off backup timestamps. A backup is <c>File.Copy</c>'d, which keeps the source's last-write
/// time, and the source is whatever the previous feature last wrote — so a later feature's backup always
/// carries a later time than an earlier one's. Every feature's revert asks this before restoring anything.
/// </summary>
internal static class ModelBackupOrder
{
    /// <summary>Every feature that backs a model up before editing it, by backup folder and display name.</summary>
    private static IEnumerable<(string Subdir, string Name)> Features()
    {
        yield return (MeshToggleService.BackupSubdir, Loc.Localize("MeshVolume.Feature.Toggles", "part switches"));
        yield return (HatCompatService.BackupSubdir, Loc.Localize("MeshVolume.Feature.HatCompat", "hat fitting"));
        yield return (MeshVolumeService.BackupSubdir, Loc.Localize("MeshVolume.Feature.Brush", "the brush"));
    }

    /// <summary>
    /// The feature that edited <paramref name="rel"/> AFTER the one owning <paramref name="ownSubdir"/>, and so
    /// must be undone first — or null when nothing stands in the way.
    /// </summary>
    public static string? LaterFeature(string modRoot, string ownSubdir, string rel)
    {
        var mine = BackupTime(modRoot, ownSubdir, rel);
        if (mine == null) return null;

        foreach (var (subdir, name) in Features())
        {
            if (string.Equals(subdir, ownSubdir, StringComparison.OrdinalIgnoreCase)) continue;
            if (BackupTime(modRoot, subdir, rel) > mine) return name;
        }
        return null;
    }

    /// <summary>The refusal a revert returns when <see cref="LaterFeature"/> names something.</summary>
    public static string BlockedMessage(string later)
        => string.Format(Loc.Localize("ModelBackups.Blocked.Fmt",
            "Undo {0} first: it changed the same model after this did, so undoing this now would also undo it."),
            later);

    private static DateTime? BackupTime(string modRoot, string subdir, string rel)
    {
        var at = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, subdir,
                              rel.Replace('/', Path.DirectorySeparatorChar));
        try { return File.Exists(at) ? File.GetLastWriteTimeUtc(at) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
