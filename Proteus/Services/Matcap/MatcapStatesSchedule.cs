using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services.Matcap;

/// <summary>
/// When the Matcaps pane re-reads its entries' states (<see cref="MatcapWriter.Detect"/>, which reads files and hashes
/// changed row sources): always on a worker — on every mod open and after every Proteus write; and, while the pane is
/// shown, at most every <see cref="IntervalMs"/>, when the worker finds a manifest file changed. Nothing here runs on the
/// framework thread but <see cref="Due"/>, a tick comparison; nothing runs at all while the pane is hidden (it is only
/// asked from the pane's draw).
/// </summary>
internal static class MatcapStatesSchedule
{
    public const long IntervalMs = 2000;

    /// <summary>Whether to start the worker's manifest check now: idle (no check in flight) and the interval passed.</summary>
    public static bool Due(long nowMs, long lastCheckMs, bool idle) => idle && nowMs - lastCheckMs >= IntervalMs;

    /// <summary>The manifest files' write times (v4 meta.json, and a v3 folder's default_mod.json and group files). Worker only.</summary>
    public static Dictionary<string, long> ManifestStamps(string modRoot)
    {
        var stamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in new[] { PenumbraModMeta.MetaFile, "default_mod.json" }
                         .Select(n => Path.Combine(modRoot, n))
                         .Concat(Directory.Exists(modRoot) ? Directory.EnumerateFiles(modRoot, "group_*.json") : []))
                if (File.Exists(f)) stamps[Path.GetFileName(f)] = File.GetLastWriteTimeUtc(f).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return stamps;
    }

    /// <summary>Whether two stamp sets differ (a file added, removed or written).</summary>
    public static bool Changed(IReadOnlyDictionary<string, long>? before, IReadOnlyDictionary<string, long> after) =>
        before == null || before.Count != after.Count
        || after.Any(kv => !before.TryGetValue(kv.Key, out var t) || t != kv.Value);
}
