using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus;

/// <summary>
/// Which colour-table cell an animated glow has to be set on, and whether it is. A material renders from the ONE
/// cell its index texture selects; a value on any other row is invisible.
/// </summary>
public static class ContentGlowRow
{
    /// <summary>
    /// The cell the material actually reads, from the index scan's rows and column (null when unreadable). Falls back
    /// to the grid's row and column A, since an index's green defaults high.
    /// </summary>
    public static (int Row, bool SubRowA) Sampled(IReadOnlyCollection<int>? rows, string? subRow, int selectedRow)
        => (rows is { Count: 1 } ? rows.First() : selectedRow,
            !string.Equals(subRow, "B", StringComparison.Ordinal));

    /// <summary>Whether that cell's Glow intensity is above zero.</summary>
    public static bool Emits(IEnumerable<ColorTableRowPreset> rows, int row, bool subRowA)
    {
        var preset = rows.FirstOrDefault(r => r.Row == row);
        return (subRowA ? preset?.SubRowA : preset?.SubRowB) is { Emissive: > 0f };
    }

    /// <summary>The Glow a newly switched-on effect starts at; not full, which blows a saturated scroll map out to white.</summary>
    public const float DefaultGlow = 0.25f;

    /// <summary>The glow colour a switched-on effect seeds; a row with no colour stays dark however high its intensity.</summary>
    public const string DefaultGlowColour = "#FFFFFF";

    /// <summary>
    /// Turn the cell on at <see cref="DefaultGlow"/> if it is off, adding the row when needed; the diffuse is left
    /// alone. Returns true when it wrote something.
    /// </summary>
    public static bool Arm(List<ColorTableRowPreset> rows, int row, bool subRowA)
    {
        if (Emits(rows, row, subRowA)) return false;

        var preset = rows.FirstOrDefault(r => r.Row == row);
        if (preset == null) rows.Add(preset = new ColorTableRowPreset { Row = row });

        var cell = subRowA
            ? preset.SubRowA ??= new ColorTableSubRowPreset()
            : preset.SubRowB ??= new ColorTableSubRowPreset();
        cell.Emissive = DefaultGlow;
        cell.EmissiveColor ??= DefaultGlowColour;
        return true;
    }

    /// <summary>
    /// Take back an <see cref="Arm"/> when the effect is switched off, else the seeded Glow stays an ordinary emissive.
    /// Only an untouched seed (still <see cref="DefaultGlow"/>) is removed; a blank cell is dropped, not written as zero.
    /// Returns true when it changed something.
    /// </summary>
    public static bool Disarm(List<ColorTableRowPreset> rows, int row, bool subRowA)
    {
        var preset = rows.FirstOrDefault(r => r.Row == row);
        var cell = subRowA ? preset?.SubRowA : preset?.SubRowB;
        if (preset == null || cell == null || cell.Emissive != DefaultGlow) return false;

        cell.Emissive = 0f;
        // The seeded colour goes too, or the cell would never read as blank.
        if (string.Equals(cell.EmissiveColor, DefaultGlowColour, StringComparison.OrdinalIgnoreCase))
            cell.EmissiveColor = null;
        if (IsBlank(cell))
        {
            if (subRowA) preset.SubRowA = null; else preset.SubRowB = null;
            if (preset.SubRowA == null && preset.SubRowB == null) rows.Remove(preset);
        }
        return true;
    }

    /// <summary>Whether a sub-row now says nothing at all, so it can be dropped instead of persisted.</summary>
    private static bool IsBlank(ColorTableSubRowPreset s)
        => s.Diffuse == null && s.EmissiveColor == null && s.Specular == null
        && s.Emissive == 0f && s.Opacity == 0
        && s.SphereMap == null && s.SphereIntensity == null
        && s.Roughness == null && s.Metalness == null
        && s.Tile == null && s.TileStrength == null
        && s.TileScaleU == null && s.TileScaleV == null
        && s.LightResponse == null && !s.HideInLight;
}
