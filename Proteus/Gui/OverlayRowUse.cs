using System;
using System.Collections.Generic;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>One overlay's or mask's index texture as the Colors window read it: whether it names an index at all, and
/// the scan (null when the file couldn't be read).</summary>
internal readonly record struct IndexRead(bool HasIndex, ContentIndexTexture.Scan? Scan)
{
    public static readonly IndexRead NoIndex = new(false, null);
}

/// <summary>
/// The colour-table rows and column an option's (or simple mod's) art reaches, for the row picker: rows outside it are
/// hidden (or dimmed under "Show all rows"), the other column is dimmed. Nulls mean unknown, which hides and dims nothing.
/// </summary>
internal static class OverlayRowUse
{
    /// <summary>The sub-row an overlay with no <c>_id</c> lands in: the fabricated index's green is 255, which is column
    /// A. Spelled the way <see cref="ContentIndexTexture.Scan.SubRow"/> spells it.</summary>
    public const string NoIndexColumn = "A";

    /// <summary>
    /// Every overlay counts: one with an index adds the rows its scan reports, one without samples row pair
    /// <see cref="GlowShell.Row"/> sub-row A (a gear shell's fabricated index; the skin pass's row-16 fill). A mask counts
    /// only with an index — the mask colour passes skip an index-less mask. Any index that can't be read, or reads as
    /// selecting nothing, makes the whole answer unknown: an empty scan is never a claim that a row is unused.
    /// </summary>
    public static (HashSet<int>? Rows, string? Column) Of(
        IReadOnlyList<IndexRead> overlays, IReadOnlyList<IndexRead> maskIndices)
    {
        var rows = new HashSet<int>();
        var columns = new List<string?>();

        foreach (var o in overlays)
        {
            if (!o.HasIndex)
            {
                rows.Add(GlowShell.Row);
                columns.Add(NoIndexColumn);
                continue;
            }
            if (o.Scan is not { } s || s.Rows.Count == 0) return (null, null);
            rows.UnionWith(s.Rows);
            columns.Add(s.SubRow);
        }

        foreach (var m in maskIndices)
        {
            if (!m.HasIndex) continue;
            if (m.Scan is not { } s || s.Rows.Count == 0) return (null, null);
            rows.UnionWith(s.Rows);
            columns.Add(s.SubRow);
        }

        // Nothing at all to read (no overlays, no indexed masks): what a shell samples without an index.
        if (rows.Count == 0) return ([GlowShell.Row], NoIndexColumn);
        return (rows, AgreedColumn(columns));
    }

    /// <summary>The one column every scan agrees on, or null if any scan has none or they disagree.</summary>
    public static string? AgreedColumn(IEnumerable<string?> columns)
    {
        string? one = null;
        foreach (var c in columns)
        {
            if (c == null) return null;
            if (one == null) one = c;
            else if (!string.Equals(one, c, StringComparison.Ordinal)) return null;
        }
        return one;
    }
}

/// <summary>Index-texture scans for the Colors window, each checked against its file's stamp on lookup, so an
/// <c>_id</c> edited on disk is rescanned without reopening the window.</summary>
internal sealed class StampedScanCache
{
    private readonly Dictionary<string, (FileStamp Stamp, ContentIndexTexture.Scan? Scan)> _map = new();

    public ContentIndexTexture.Scan? Get(string path, FileStamp stamp, Func<ContentIndexTexture.Scan?> scan)
    {
        if (_map.TryGetValue(path, out var e) && e.Stamp == stamp) return e.Scan;
        var s = scan();
        _map[path] = (stamp, s);
        return s;
    }

    public void RemoveWhere(Func<string, bool> pathPredicate)
    {
        foreach (var k in new List<string>(_map.Keys))
            if (pathPredicate(k)) _map.Remove(k);
    }

    public void Clear() => _map.Clear();

    public int Count => _map.Count;
}
