using System;
using System.IO;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// What under the managed mod's folder is our published output. Two subfolders are exceptions, both kept there only so
/// sync plugins (which upload files under the Penumbra root) can see them:
/// <list type="bullet">
/// <item><see cref="LivePreviewSubdir"/>: the live brush's shared copies of a mod's saved model;</item>
/// <item><see cref="ShaderUpgradeSubdir"/>: the gear shader upgrade's copies of a worn mod's legacy materials, moved to
/// Dawntrail's shader, and the masks derived for them.</item>
/// </list>
/// Each is the mod's own file under another name (or derived from it), never a composite, so every "is this our output"
/// test must read them as outside files.
/// </summary>
internal static class ManagedModPaths
{
    public const string LivePreviewSubdir = "LivePreview";

    /// <summary>Where <see cref="ShaderUpgrade.ShaderUpgradeCopies"/> writes its copies.</summary>
    public const string ShaderUpgradeSubdir = "ShaderUpgrade";

    public static string LivePreviewDir(string managedRoot) => Path.Combine(managedRoot, LivePreviewSubdir);

    public static string ShaderUpgradeDir(string managedRoot) => Path.Combine(managedRoot, ShaderUpgradeSubdir);

    /// <summary>A live brush preview shared under <paramref name="managedRoot"/>.</summary>
    public static bool IsLivePreview(string? path, string? managedRoot)
        => !string.IsNullOrWhiteSpace(managedRoot) && Under(path, LivePreviewDir(managedRoot));

    /// <summary>A shader-upgrade copy (material or derived mask) under <paramref name="managedRoot"/>.</summary>
    public static bool IsShaderUpgradeCopy(string? path, string? managedRoot)
        => !string.IsNullOrWhiteSpace(managedRoot) && Under(path, ShaderUpgradeDir(managedRoot));

    /// <summary>
    /// A file the compositor published into the managed mod (the folder itself counts), not a live preview or a
    /// shader-upgrade copy.
    /// </summary>
    public static bool IsPublishedOutput(string? path, string? managedRoot)
        => Under(path, managedRoot) && !IsLivePreview(path, managedRoot) && !IsShaderUpgradeCopy(path, managedRoot);

    /// <summary>
    /// Compared on <see cref="BodyShapeReader.PathKey"/> of the full path: Penumbra spells resource names with forward
    /// slashes, a <c>|…|</c> prefix and its own casing, and our own paths are built with backslashes.
    /// </summary>
    private static bool Under(string? path, string? root)
    {
        var p = Key(path);
        var r = Key(root);
        if (p == null || r == null) return false;
        return p == r || p.StartsWith(r + "/", StringComparison.Ordinal);
    }

    private static string? Key(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var key = BodyShapeReader.PathKey(path);
        try { key = BodyShapeReader.PathKey(Path.GetFullPath(key)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            /* not a path Windows can resolve; the plain key still compares */
        }
        return key.TrimEnd('/');
    }
}
