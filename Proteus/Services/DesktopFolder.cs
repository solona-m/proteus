using System;
using System.IO;

namespace Proteus.Services;

internal static class DesktopFolder
{
    /// <summary>The user's desktop, or null when neither desktop folder resolves.</summary>
    public static string? Path()
    {
        // DesktopDirectory follows a OneDrive-redirected desktop; Desktop can come back empty, so it is the fallback.
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Desktop })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
        }
        return null;
    }
}
