using System;
using System.Collections.Generic;
using System.IO;

namespace Proteus.Services;

/// <summary>
/// The body maps a refit across bodies reads (<see cref="UVRemapService"/>'s <c>uvmaps</c> folder), checked before a
/// headless run: without them every pair across body families is refused "no map" and the run comes back with nothing.
/// A map must open as a TIFF — a Git LFS pointer checked out in its place has the right name and a length, and is not.
/// </summary>
internal static class UvMapFiles
{
    /// <summary>The maps every women's body refit across families needs: Neolithe's skin to bibo's and back.</summary>
    internal static readonly string[] WomenMaps = ["gen3_to_bibo_transfer.tif", "bibo_to_gen3_transfer.tif"];

    /// <summary>The maps a men's version needs (see <see cref="UVRemapService.MenMapsPresent"/>).</summary>
    internal static readonly string[] MenMaps = [UVRemapService.BiboToTbseFile, UVRemapService.TbseToBiboFile];

    /// <summary>Whether a file opens as a TIFF ("II*\0" or "MM\0*").</summary>
    internal static bool IsMap(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (s.Read(magic) < 4) return false;
            return (magic[0] == (byte)'I' && magic[1] == (byte)'I' && magic[2] == 42 && magic[3] == 0)
                || (magic[0] == (byte)'M' && magic[1] == (byte)'M' && magic[2] == 0 && magic[3] == 42);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The maps a run needs that are missing or not TIFFs, by file name; empty when all are there.</summary>
    /// <param name="pluginDir">The folder holding <c>uvmaps</c>, as <see cref="UVRemapService"/> is given it.</param>
    /// <param name="men">Men's versions are asked for too.</param>
    internal static List<string> Missing(string pluginDir, bool men)
    {
        var missing = new List<string>();
        foreach (string name in men ? [.. WomenMaps, .. MenMaps] : WomenMaps)
            if (!IsMap(Path.Combine(pluginDir, "uvmaps", name))) missing.Add(name);
        return missing;
    }
}
