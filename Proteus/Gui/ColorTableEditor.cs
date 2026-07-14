using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Proteus.Gui;

/// <summary>
/// The colour-table editor, shared by both overlay layers.
///
/// Skin and gear overlays store their colours in the SAME place — <see cref="ColorTableSubRowPreset"/>
/// in metadata.json. Only the consumer differs: the compositor bakes them into the skin's textures,
/// while a gear overlay's are written into a real .mtrl colour table by GearMaterialWriter. So this is
/// one editor with fields shown or hidden per layer, not two.
///
/// Layout is modelled on Penumbra's advanced material editor (row picker, then A/B detail panels), but
/// written from scratch: Penumbra ships no licence, and it edits a live MtrlFile.ColorTable rather than
/// our presets, so its code would not fit here anyway.
/// </summary>
public static class ColorTableEditor
{
    private static readonly string[] GearShaders = ["character.shpk", "characterscroll.shpk"];

    private const string SphereTip =
        "Reflects a slice of the game's shared sphere map array.\n" +
        "Index AND intensity must both be non-zero, or nothing happens.\n" +
        "Does NOT work under characterscroll.shpk — use character.shpk.";

    private const string MetalTip =
        "Metal has no diffuse colour of its own — it shows what it reflects.\n" +
        "With no sphere map to reflect, a metallic surface just goes dark.";

    /// <summary>
    /// Layer / shader / surface controls for one option. These live on the descriptors rather than the
    /// colour rows, so they always write to metadata.json — a design binding only carries rows.
    /// Returns true when something changed.
    /// </summary>
    public static bool DrawLayerHeader(string idScope, IReadOnlyList<OverlayDescriptor> overlays)
    {
        if (overlays.Count == 0) return false;

        bool changed = false;
        var first = overlays[0];

        bool gear = first.Layer == OverlayLayer.Gear;
        if (ImGui.Checkbox($"Gear Layer##{idScope}", ref gear))
        {
            foreach (var d in overlays)
                d.Layer = gear ? OverlayLayer.Gear : OverlayLayer.Skin;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Render this option on a \"second skin\" — a copy of your skin drawn as gear, so it can\n" +
                "use a full gear shader: sphere maps, metalness, and animated emissive, none of which\n" +
                "skin.shpk offers. Rides an invisible ring, so it survives any outfit.");

        if (!gear) return changed;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(170);
        var shader = first.ShaderPackage;
        if (ImGui.BeginCombo($"Shader##{idScope}", shader))
        {
            foreach (var s in GearShaders)
            {
                if (ImGui.Selectable(s, s == shader) && s != shader)
                {
                    foreach (var d in overlays) d.Shader = s;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "character.shpk — sphere maps, metalness (default).\n" +
                "characterscroll.shpk — adds an animated scrolling glow driven by the Scroll map,\n" +
                "but sphere maps do NOT work under it.");

        if (string.Equals(shader, "characterscroll.shpk", StringComparison.OrdinalIgnoreCase))
        {
            ImGui.TextDisabled(first.Scroll is { } s
                ? $"Scroll map: {s}"
                : "Scroll map: none — set \"Scroll\" in metadata.json for an animated glow.");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The scrolling map IS the glow: its colour, its pattern, and its animation.\n" +
                                 "The row's emissive is only a small gate that switches the glow on.");
        }

        return changed;
    }

    /// <summary>
    /// Row picker plus the A/B detail panels for the selected row.
    /// <paramref name="usedRows"/> (when non-null) limits the picker to rows the index texture uses.
    /// </summary>
    public static void DrawRows(
        string idScope,
        List<ColorTableRowPreset> rows,
        HashSet<int>? usedRows,
        bool gear,
        ref int selectedRow,
        ref bool changed)
    {
        // Every row is shown; rows the index texture never selects are disabled rather than hidden, so
        // the table's shape stays constant and it's obvious WHICH rows the overlay actually uses.
        bool InUse(int r) => usedRows == null || usedRows.Contains(r);

        int sel = selectedRow;                       // a ref param can't be captured by a lambda
        if (!InUse(sel))
        {
            int firstUsed = Enumerable.Range(1, 16).FirstOrDefault(InUse);
            sel = firstUsed == 0 ? 1 : firstUsed;    // land on a row that actually renders
        }
        selectedRow = sel;

        // ── row picker ───────────────────────────────────────────────────────
        const int perLine = 8;
        for (int row = 1; row <= 16; row++)
        {
            if ((row - 1) % perLine != 0) ImGui.SameLine();

            bool used = InUse(row);
            using (ImRaii.Disabled(!used))
            using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), row == selectedRow))
            {
                if (ImGui.Button($"#{row:D2}##row_{idScope}_{row}", new Vector2(38, 0)))
                    selectedRow = row;
            }
            if (!used && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("This overlay's index texture never selects this row,\nso editing it would have no effect.");
        }

        ImGui.Separator();

        if (ImGui.BeginTable($"##ab_{idScope}", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn($"Row {selectedRow}A");
            ImGui.TableSetupColumn($"Row {selectedRow}B");
            ImGui.TableHeadersRow();
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawSubRow($"{idScope}_A", rows, selectedRow, true, gear, ref changed);

            ImGui.TableNextColumn();
            DrawSubRow($"{idScope}_B", rows, selectedRow, false, gear, ref changed);

            ImGui.EndTable();
        }
    }

    private static void DrawSubRow(
        string id, List<ColorTableRowPreset> rows, int row, bool isA, bool gear, ref bool changed)
    {
        var preset = rows.FirstOrDefault(r => r.Row == row);
        var sub = isA ? preset?.SubRowA : preset?.SubRowB;

        ColorTableSubRowPreset Edit()
        {
            var p = EnsurePreset(rows, row);
            if (isA) return p.SubRowA ??= new ColorTableSubRowPreset();
            return p.SubRowB ??= new ColorTableSubRowPreset();
        }

        // ── Colours ──────────────────────────────────────────────────────────
        ImGui.TextDisabled("Colours");

        var diffuse = HexToVec3(sub?.Diffuse);
        ImGui.SetNextItemWidth(22);
        if (ImGui.ColorEdit3($"Diffuse##d_{id}", ref diffuse, ImGuiColorEditFlags.NoInputs))
        {
            Edit().Diffuse = Vec3ToHex(diffuse);
            changed = true;
        }

        if (gear)
        {
            var spec = HexToVec3(sub?.Specular);
            ImGui.SetNextItemWidth(22);
            if (ImGui.ColorEdit3($"Specular##s_{id}", ref spec, ImGuiColorEditFlags.NoInputs))
            {
                Edit().Specular = Vec3ToHex(spec);
                changed = true;
            }

            var emCol = HexToVec3(sub?.EmissiveColor ?? sub?.Diffuse);
            ImGui.SetNextItemWidth(22);
            if (ImGui.ColorEdit3($"Glow colour##ec_{id}", ref emCol, ImGuiColorEditFlags.NoInputs))
            {
                Edit().EmissiveColor = Vec3ToHex(emCol);
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Glow colour, independent of the diffuse — a glowing material usually\n" +
                                 "wants a DARK surface with a bright glow. Defaults to the diffuse.");
        }

        float em = sub?.Emissive ?? 0f;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragFloat($"Glow##e_{id}", ref em, 0.005f, 0f, 1f, "%.3f"))
        {
            Edit().Emissive = Math.Clamp(em, 0f, 1f);
            changed = true;
        }
        if (gear && ImGui.IsItemHovered())
            ImGui.SetTooltip("Under characterscroll this is a small GATE, not the brightness:\n" +
                             "0 = no glow at all; a large value washes the scroll map's colour out.\n" +
                             "The vanilla animated materials use ~0.025.");

        // Opacity applies to both layers: on skin it scales the overlay's alpha, on gear it scales the
        // coverage that becomes the normal map's blue channel (the transparency gate).
        int op = sub?.Opacity ?? 0;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragInt($"Opacity##o_{id}", ref op, 1f, -100, 100, "%d%%"))
        {
            Edit().Opacity = Math.Clamp(op, -100, 100);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Negative fades this row toward transparent; positive pushes it toward opaque.");

        if (!gear) return;

        // ── Gear only ────────────────────────────────────────────────────────
        ImGui.TextDisabled("Physical");

        float rough = sub?.Roughness ?? 0.5f;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragFloat($"Roughness##r_{id}", ref rough, 0.01f, 0f, 1f, "%.2f"))
        {
            Edit().Roughness = Math.Clamp(rough, 0f, 1f);
            changed = true;
        }

        float metal = sub?.Metalness ?? 0f;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragFloat($"Metalness##m_{id}", ref metal, 0.01f, 0f, 1f, "%.2f"))
        {
            Edit().Metalness = Math.Clamp(metal, 0f, 1f);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(MetalTip);

        ImGui.TextDisabled("Sphere map");

        int sphere = sub?.SphereMap ?? 0;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragInt($"Index##sp_{id}", ref sphere, 0.2f, 0, 31))
        {
            Edit().SphereMap = Math.Clamp(sphere, 0, 31);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(SphereTip);

        float sphereInt = sub?.SphereIntensity ?? 0f;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragFloat($"Intensity##si_{id}", ref sphereInt, 0.01f, 0f, 1f, "%.2f"))
        {
            Edit().SphereIntensity = Math.Clamp(sphereInt, 0f, 1f);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(SphereTip);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    internal static ColorTableRowPreset EnsurePreset(List<ColorTableRowPreset> rows, int row)
    {
        var p = rows.FirstOrDefault(r => r.Row == row);
        if (p == null) { p = new ColorTableRowPreset { Row = row }; rows.Add(p); }
        return p;
    }

    internal static Vector3 HexToVec3(string? hex)
    {
        if (hex == null) return Vector3.One;
        hex = hex.TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        try
        {
            int v = Convert.ToInt32(hex, 16);
            return new Vector3((v >> 16 & 0xFF) / 255f, (v >> 8 & 0xFF) / 255f, (v & 0xFF) / 255f);
        }
        catch { return Vector3.One; }
    }

    internal static string Vec3ToHex(Vector3 c)
    {
        int r = Math.Clamp((int)(c.X * 255), 0, 255);
        int g = Math.Clamp((int)(c.Y * 255), 0, 255);
        int b = Math.Clamp((int)(c.Z * 255), 0, 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
