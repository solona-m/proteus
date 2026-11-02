using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Proteus.Services;

internal static partial class ShellBodyAtr
{
    // ── which mod an upstream copy came from, kept beside it across a restart ─────────────────────────────

    private const string SourceSuffix = ".src";

    /// <summary>
    /// Record beside a kept upstream copy which mod file it is, mod-relative. A source unknown or outside the mods root
    /// deletes any record, so it can never name a body the copy no longer is. May throw (I/O); the caller logs.
    /// </summary>
    internal static void WriteSource(string upstreamDisk, string? sourceDisk, string? modsRoot)
    {
        var record = upstreamDisk + SourceSuffix;
        string? rel = null;
        if (!string.IsNullOrEmpty(sourceDisk) && !string.IsNullOrEmpty(modsRoot)
            && CompositorService.ModFolderOf(sourceDisk, modsRoot) != null)
            rel = sourceDisk[modsRoot.Length..].TrimStart('/', '\\').Replace('\\', '/');
        if (string.IsNullOrEmpty(rel))
        {
            if (File.Exists(record)) File.Delete(record);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(rel);
        if (File.Exists(record) && File.ReadAllBytes(record).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(record, bytes);
    }

    /// <summary>
    /// The mod file a kept upstream copy came from, or null: no record, a record leaving the mods root, or a file that is
    /// gone or no longer the copy's length. Never throws.
    /// </summary>
    internal static string? ReadSource(string upstreamDisk, string? modsRoot, long expectedLength)
    {
        try
        {
            if (string.IsNullOrEmpty(modsRoot)) return null;
            var record = upstreamDisk + SourceSuffix;
            if (!File.Exists(record)) return null;
            var rel = File.ReadAllText(record, Encoding.UTF8).Trim();
            if (rel.Length == 0 || Path.IsPathRooted(rel)
                || rel.Split('/', '\\').Any(s => s == "..")) return null;
            var full = Path.Combine(modsRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(full);
            return info.Exists && info.Length == expectedLength ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
