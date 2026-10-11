using System;
using System.IO;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// What under the managed mod's folder is our published output. The one exception is <see cref="LivePreviewSubdir"/>:
/// the live brush's shared copies of a mod's saved model, which sit there only so sync plugins (which upload files
/// under the Penumbra root) can see them. They are the mod's own file under another name, never a composite, so every
/// "is this our output" test must read them as an outside file.
/// </summary>
internal static class ManagedModPaths
{
    public const string LivePreviewSubdir = "LivePreview";

    public static string LivePreviewDir(string managedRoot) => Path.Combine(managedRoot, LivePreviewSubdir);

    /// <summary>A live brush preview shared under <paramref name="managedRoot"/>.</summary>
    public static bool IsLivePreview(string? path, string? managedRoot)
        => !string.IsNullOrWhiteSpace(managedRoot) && Under(path, LivePreviewDir(managedRoot));

    /// <summary>A file the compositor published into the managed mod (the folder itself counts), not a live preview.</summary>
    public static bool IsPublishedOutput(string? path, string? managedRoot)
        => Under(path, managedRoot) && !IsLivePreview(path, managedRoot);

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
        catch { /* not a path Windows can resolve; the plain key still compares */ }
        return key.TrimEnd('/');
    }
}
