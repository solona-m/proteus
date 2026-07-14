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

        ImGui.SetNextItemWidth(110);
        if (ImGui.BeginCombo($"Layer##{idScope}", gear ? "Gear" : "Skin"))
        {
            foreach (var layer in new[] { OverlayLayer.Skin, OverlayLayer.Gear })
            {
                bool selected = layer == first.Layer;
                if (ImGui.Selectable(layer == OverlayLayer.Gear ? "Gear" : "Skin", selected) && !selected)
                {
                    foreach (var d in overlays) d.Layer = layer;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Skin — composited into your skin's own textures (the default).\n\n" +
                "Gear — rendered on a \"second skin\": a copy of your skin drawn as gear, so it can use a\n" +
                "full gear shader (sphere maps, metalness, animated emissive), none of which skin.shpk\n" +
                "offers. Rides an invisible ring, so it survives any outfit.");

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
        // Each button previews its row: three columns (diffuse, specular, glow), each split top = A,
        // bottom = B — so the whole table is readable at a glance without clicking through it.
        // Left-align the label, or it centres itself under the swatches and disappears.
        using var align = ImRaii.PushStyle(ImGuiStyleVar.ButtonTextAlign, new Vector2(0f, 0.5f));

        // Wrap to whatever width the window actually has, rather than a fixed count that falls off the
        // right edge when the window is narrow.
        var btn = new Vector2(70, 30);
        float avail = ImGui.GetContentRegionAvail().X;
        int perLine = Math.Max(1, (int)((avail + ImGui.GetStyle().ItemSpacing.X) / (btn.X + ImGui.GetStyle().ItemSpacing.X)));

        for (int row = 1; row <= 16; row++)
        {
            if ((row - 1) % perLine != 0) ImGui.SameLine();

            bool used = InUse(row);
            using (ImRaii.Disabled(!used))
            using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), row == selectedRow))
            {
                if (ImGui.Button($"#{row:D2}##row_{idScope}_{row}", btn))
                    selectedRow = row;
            }
            if (!used && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("This overlay's index texture never selects this row,\nso editing it would have no effect.");

            DrawRowSwatches(rows, row, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), used);
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

    /// <summary>Thumbnails for the sphere-map picker. Set once at startup; null just means no previews.</summary>
    public static SphereMapPreview? Spheres { get; set; }

    /// <summary>Sphere map index, as a dropdown of thumbnails — an index alone tells you nothing.</summary>
    private static void DrawSpherePicker(string id, ref int index, out bool changed)
    {
        changed = false;
        const float current = 32f;   // the one in use, beside the combo
        const float thumb = 56f;     // the pictures in the list — click one to pick it

        Spheres?.Draw(index, current);
        if (Spheres != null) ImGui.SameLine();

        ImGui.SetNextItemWidth(70);
        if (ImGui.BeginCombo($"Index##sp_{id}", index.ToString(), ImGuiComboFlags.HeightLarge))
        {
            for (int i = 0; i < SphereMapPreview.Count; i++)
            {
                // The picture is the button — clicking it selects that index and closes the list.
                if (Spheres?.DrawButton($"##sphimg_{id}_{i}", i, thumb) == true && i != index)
                {
                    index = i;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
                if (Spheres != null) ImGui.SameLine();

                if (ImGui.Selectable($"{i}##sph_{id}_{i}", i == index, ImGuiSelectableFlags.None,
                        new Vector2(0, thumb)) && i != index)
                {
                    index = i;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(SphereTip);
    }

    /// <summary>
    /// Paint a row's colours onto its picker button: three columns — diffuse, specular, glow — each
    /// split top = sub-row A, bottom = sub-row B. The glow swatch is the emissive colour scaled by its
    /// intensity, i.e. what actually lands in the colour table, so a row with no glow reads as black.
    /// </summary>
    private static void DrawRowSwatches(
        List<ColorTableRowPreset> rows, int row, Vector2 min, Vector2 max, bool used)
    {
        var preset = rows.FirstOrDefault(r => r.Row == row);
        var draw = ImGui.GetWindowDrawList();

        // The label sits on the left; swatches fill the right half of the button.
        float x0 = min.X + 34f, x1 = max.X - 3f;
        float y0 = min.Y + 3f, y1 = max.Y - 3f;
        if (x1 <= x0) return;

        float colW = (x1 - x0) / 3f;
        float midY = (y0 + y1) * 0.5f;

        // Rows the index texture never selects are dimmed, so the ones actually in play stand out.
        // ImGui's Disabled only fades the widget itself — these swatches are hand-drawn, so dim them too.
        float dim = used ? 1f : 0.25f;

        Vector3 Swatch(ColorTableSubRowPreset? s, int column) => dim * (column switch
        {
            0 => HexToVec3(s?.Diffuse),
            1 => HexToVec3(s?.Specular),
            _ => HexToVec3(s?.EmissiveColor ?? s?.Diffuse) * (s?.Emissive ?? 0f),
        });

        for (int c = 0; c < 3; c++)
        {
            float cx0 = x0 + c * colW, cx1 = cx0 + colW - 1f;
            foreach (var (sub, ry0, ry1) in new[]
                     {
                         (preset?.SubRowA, y0, midY),
                         (preset?.SubRowB, midY, y1),
                     })
            {
                var v = Swatch(sub, c);
                uint col = ImGui.GetColorU32(new Vector4(v.X, v.Y, v.Z, 1f));
                draw.AddRectFilled(new Vector2(cx0, ry0), new Vector2(cx1, ry1), col);
            }
        }

        draw.AddRect(new Vector2(x0, y0), new Vector2(x1, y1), ImGui.GetColorU32(ImGuiCol.Border));
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

        // Stored 0–1, shown as a percentage. Fine steps matter: characterscroll's glow gate sits at
        // about 2.5%, and anything much larger washes the scroll map's colour out.
        float emPct = (sub?.Emissive ?? 0f) * 100f;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragFloat($"Glow##e_{id}", ref emPct, 0.25f, 0f, 100f, "%.1f%%"))
        {
            Edit().Emissive = Math.Clamp(emPct / 100f, 0f, 1f);
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
        DrawSpherePicker(id, ref sphere, out bool sphereChanged);
        if (sphereChanged)
        {
            Edit().SphereMap = Math.Clamp(sphere, 0, SphereMapPreview.Count - 1);
            changed = true;
        }

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
