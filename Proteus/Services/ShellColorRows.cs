using System;
using System.Collections.Generic;

namespace Proteus.Services;

internal static class ShellColorRows
{
    /// <summary>
    /// A neutral colour-table row, so no row an author never chose keeps the vanilla template's values. Sets EVERY
    /// field <see cref="GearMaterialWriter.PatchColorTable"/> writes, since it skips nulls.
    /// </summary>
    internal static readonly GearColorRow NeutralRow = new()
    {
        Diffuse        = (1f, 1f, 1f),   // a no-op multiply over art that carries its own colour
        Emissive       = (0f, 0f, 0f),   // nothing glows unless the author says so
        Specular       = (1f, 1f, 1f),   // full, undimmed highlight response
        Roughness      = 0.5f,
        Metalness      = 0f,
        SphereMapIndex = 0,              // an index with a zero mask does nothing; both halves are pinned
        SphereMapMask  = 0f,
    };

    /// <inheritdoc cref="NeutralRow"/>
    // One shared instance for all 32 entries is safe: GearColorRow is init-only.
    internal static Dictionary<int, GearColorRow> NeutralRows()
    {
        var rows = new Dictionary<int, GearColorRow>();
        for (int r = 0; r < 32; r++) rows[r] = NeutralRow;
        return rows;
    }

    /// <summary>Map the metadata's 1-based row/sub-row presets onto 0-based color table rows.</summary>
    /// <param name="isMaskShell">
    /// A MASK shell, where the colorset IS the colour: no presets returns the white baseline instead of null (keep the
    /// template), and a half-authored row pair mirrors.
    /// </param>
    /// <param name="neutralWhenEmpty">
    /// Take the white baseline for an empty list without the mirroring; defaults to <paramref name="isMaskShell"/>.
    /// </param>
    internal static Dictionary<int, GearColorRow>? BuildRows(List<ColorTableRowPreset>? presets,
                                                             bool isMaskShell = false,
                                                             bool? neutralWhenEmpty = null)
    {
        if (presets == null || presets.Count == 0)
            return (neutralWhenEmpty ?? isMaskShell) ? NeutralRows() : null;
        var rows = NeutralRows();

        foreach (var p in presets)
        {
            if (p.Row is < 1 or > 16) continue;
            // On a mask shell the unset half of a pair MIRRORS the set half: the shader lerps B→A by the index's
            // green, and a white half would paint white. On an ordinary shell white is a no-op multiply, so
            // mirroring there would tint the art.
            Add((p.Row - 1) * 2, p.SubRowA ?? (isMaskShell ? p.SubRowB : null));
            Add((p.Row - 1) * 2 + 1, p.SubRowB ?? (isMaskShell ? p.SubRowA : null));
        }
        return rows;

        void Add(int rowIndex, ColorTableSubRowPreset? sub)
        {
            if (sub == null) return;   // neither sub-row set — leaves the neutral row from the init above
            // MERGED over the neutral row: RowFrom leaves unfilled fields null, and PatchColorTable would let the
            // template's values through. `with` keeps any field not named here (the weave) as authored.
            var authored = RowFrom(sub, diffuseWhenUnset: NeutralRow.Diffuse);
            rows[rowIndex] = authored with
            {
                Diffuse        = authored.Diffuse        ?? NeutralRow.Diffuse,
                Emissive       = authored.Emissive       ?? NeutralRow.Emissive,
                Specular       = authored.Specular       ?? NeutralRow.Specular,
                Roughness      = authored.Roughness      ?? NeutralRow.Roughness,
                Metalness      = authored.Metalness      ?? NeutralRow.Metalness,
                SphereMapIndex = authored.SphereMapIndex ?? NeutralRow.SphereMapIndex,
                SphereMapMask  = authored.SphereMapMask  ?? NeutralRow.SphereMapMask,
                // EmissiveStrength is not defaulted: NeutralRow carries none.
            };
        }
    }

    /// <summary>
    /// The rows a content pack's own material should have overwritten: a SPARSE map of only the edited rows, so the
    /// author's other rows survive (unlike <see cref="BuildRows"/>).
    /// </summary>
    internal static Dictionary<int, GearColorRow>? BuildSparseRows(List<ColorTableRowPreset>? presets)
    {
        if (presets == null || presets.Count == 0) return null;
        var rows = new Dictionary<int, GearColorRow>();
        foreach (var p in presets)
        {
            if (p.Row is < 1 or > 16) continue;
            if (p.SubRowA is { } a) rows[(p.Row - 1) * 2] = RowFrom(a);
            if (p.SubRowB is { } b) rows[(p.Row - 1) * 2 + 1] = RowFrom(b);
        }
        return rows.Count > 0 ? rows : null;
    }

    /// <summary>
    /// What a shell's rows want from the scene light, or null. Uses <see cref="BuildRows"/>'s exact row mapping and
    /// mask-shell mirroring, so the response lands on the rows whose emissive it scales.
    /// </summary>
    internal static ShellLightProfile? BuildLightProfile(List<ColorTableRowPreset>? presets, bool isMaskShell,
                                                        ShellSurfaceKind kind, bool isScroll = false)
    {
        if (presets == null || presets.Count == 0) return null;

        var response = new float[ShellLightProfile.RowCount];
        var hide     = new float[ShellLightProfile.RowCount];
        foreach (var p in presets)
        {
            if (p.Row is < 1 or > 16) continue;
            Add((p.Row - 1) * 2, p.SubRowA ?? (isMaskShell ? p.SubRowB : null));
            Add((p.Row - 1) * 2 + 1, p.SubRowB ?? (isMaskShell ? p.SubRowA : null));
        }

        var profile = new ShellLightProfile(response, hide, ProbeHeightFor(kind), isScroll);
        return profile.Any ? profile : null;

        void Add(int rowIndex, ColorTableSubRowPreset? sub)
        {
            if (sub == null) return;
            float r = Math.Clamp(sub.LightResponse ?? 0f, 0f, 1f);
            response[rowIndex] = r;
            // A bare Hide with no response follows the glow all the way.
            if (sub.HideInLight) hide[rowIndex] = r > 0f ? r : 1f;
        }
    }

    /// <summary>Roughly how far up the wearer a surface's art sits (metres, estimated), so the light is sampled near it.</summary>
    private static float ProbeHeightFor(ShellSurfaceKind kind) => kind switch
    {
        ShellSurfaceKind.Face or ShellSurfaceKind.Iris or ShellSurfaceKind.Hair or ShellSurfaceKind.Ear => 1.45f,
        ShellSurfaceKind.Tail => 0.7f,
        _ => 0.9f,   // the body, and any piece a pack brought its own geometry for
    };

    /// <summary>One sub-row preset as the material writer's row, shared by both row builders.</summary>
    /// <param name="diffuseWhenUnset">Diffuse for a preset with no colour: white for a shell, null for a content
    /// material. Applied after the emissive colour resolves, so a colourless glow stays dark.</param>
    private static GearColorRow RowFrom(ColorTableSubRowPreset sub,
                                        (float R, float G, float B)? diffuseWhenUnset = null)
    {
        var rgb = ParseHex(sub.Diffuse);
        // Glow colour is independent of the diffuse, falling back to it. A row with an intensity but no colour
        // anywhere stays dark; the editor stores white when the Glow slider is raised.
        var emis = ParseHex(sub.EmissiveColor) ?? rgb;
        return new GearColorRow
        {
            Diffuse = rgb ?? diffuseWhenUnset,
            Specular = ParseHex(sub.Specular),
            // Always write emissive: a template's own emissive must be cleared, not inherited.
            Emissive = sub.Emissive > 0f && emis is { } c
                ? (c.R * sub.Emissive, c.G * sub.Emissive, c.B * sub.Emissive)
                : (0f, 0f, 0f),
            // The dial itself, for characterscroll — see GearColorRow.EmissiveStrength.
            EmissiveStrength = sub.Emissive,
            SphereMapIndex = sub.SphereMap,
            SphereMapMask = sub.SphereIntensity,
            Roughness = sub.Roughness,
            Metalness = sub.Metalness,
            TileIndex = sub.Tile,
            TileStrength = sub.TileStrength,
            TileScaleU = sub.TileScaleU,
            TileScaleV = sub.TileScaleV,
        };
    }

    private static (float R, float G, float B)? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return (((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
    }

    // ── EQDP ─────────────────────────────────────────────────────────────────

    internal static readonly string[] RaceNames = ModelRace.Names;
}
