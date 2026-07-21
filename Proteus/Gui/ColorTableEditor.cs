using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Proteus.Services;

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
    public static bool DrawLayerHeader(
        string idScope,
        IReadOnlyList<OverlayDescriptor> overlays,
        GearSettingsPreset? ovr,
        IReadOnlyList<(string Name, string Path, bool FromMod)> effects)
    {
        if (overlays.Count == 0) return false;

        bool changed = false;
        var first = overlays[0];

        // When a design binding is being edited, every layer/shader/scroll edit goes into its gear
        // OVERRIDE (live preview, folded in on "Update binding") instead of metadata.json — mirrors the
        // colour editor. Effective values read the override first, then fall back to the descriptor.
        OverlayLayer curLayer  = ovr?.Layer ?? first.Layer;
        string? curShaderField = ovr != null ? ovr.Shader : first.Shader;
        string? curScroll      = ovr != null ? ovr.Scroll : first.Scroll;
        float? curSpeedX = ovr != null ? ovr.ScrollSpeedX : first.ScrollSpeedX;
        float? curSpeedY = ovr != null ? ovr.ScrollSpeedY : first.ScrollSpeedY;
        float? curTileX  = ovr != null ? ovr.ScrollTilingX : first.ScrollTilingX;
        float? curTileY  = ovr != null ? ovr.ScrollTilingY : first.ScrollTilingY;

        void SetLayer(OverlayLayer l)  { if (ovr != null) ovr.Layer = l;   else foreach (var d in overlays) d.Layer = l; }
        void SetShader(string? s)      { if (ovr != null) ovr.Shader = s;  else foreach (var d in overlays) d.Shader = s; }
        void SetScroll(string? s)      { if (ovr != null) ovr.Scroll = s;  else foreach (var d in overlays) d.Scroll = s; }
        void SetSpeed(float x, float y) { if (ovr != null) { ovr.ScrollSpeedX = x; ovr.ScrollSpeedY = y; } else foreach (var d in overlays) { d.ScrollSpeedX = x; d.ScrollSpeedY = y; } }
        void SetTile(float x, float y)  { if (ovr != null) { ovr.ScrollTilingX = x; ovr.ScrollTilingY = y; } else foreach (var d in overlays) { d.ScrollTilingX = x; d.ScrollTilingY = y; } }
        bool curNylon = ovr != null ? (ovr.EnhancedNylon ?? false) : first.EnhancedNylon;
        void SetNylon(bool v) { if (ovr != null) ovr.EnhancedNylon = v; else foreach (var d in overlays) d.EnhancedNylon = v; }

        bool gear = curLayer == OverlayLayer.Gear;
        var shader = curLayer == OverlayLayer.Skin
            ? OverlayDescriptor.SkinShader
            : (curShaderField ?? OverlayDescriptor.DefaultGearShader);

        ImGui.SetNextItemWidth(110);
        if (ImGui.BeginCombo($"Layer##{idScope}", gear ? "Gear" : "Skin"))
        {
            foreach (var layer in new[] { OverlayLayer.Skin, OverlayLayer.Gear })
            {
                bool selected = layer == curLayer;
                if (ImGui.Selectable(layer == OverlayLayer.Gear ? "Gear" : "Skin", selected) && !selected)
                {
                    SetLayer(layer);
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
        if (ImGui.BeginCombo($"Shader##{idScope}", shader))
        {
            foreach (var s in GearShaders)
            {
                if (ImGui.Selectable(s, s == shader) && s != shader)
                {
                    SetShader(s);
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
            ImGui.SetNextItemWidth(220);
            var currentEffect = curScroll;
            var label = currentEffect == null ? "None" : Path.GetFileNameWithoutExtension(currentEffect);

            if (ImGui.BeginCombo($"Effect##{idScope}", label))
            {
                if (ImGui.Selectable("None", currentEffect == null) && currentEffect != null)
                {
                    SetScroll(null);
                    changed = true;
                }

                foreach (var (name, _, fromMod) in effects)
                {
                    bool selected = string.Equals(name, currentEffect, StringComparison.OrdinalIgnoreCase);
                    var text = fromMod
                        ? $"{Path.GetFileNameWithoutExtension(name)}  (mod)"
                        : Path.GetFileNameWithoutExtension(name);

                    if (ImGui.Selectable(text, selected) && !selected)
                    {
                        SetScroll(name);
                        changed = true;
                    }
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "The scrolling map IS the glow: its colour, its pattern, and its animation.\n" +
                    "The row's emissive is only a small gate that switches it on (~2.5%).\n\n" +
                    "Effects come from the mod's own Proteus/Effects/ folder, then your\n" +
                    "Effects folder in Settings.");

            if (effects.Count == 0)
                ImGui.TextDisabled("No effects found — drop image files into the Effects library\n" +
                                   "(see Settings), or the mod's own Proteus/Effects/ folder.");

            // Scroll speed / tiling. These are material constants, and vanilla ships the speeds at zero,
            // so without them the pattern would sit still.
            var speed = new Vector2(
                curSpeedX ?? ScrollSettings.Default.SpeedX,
                curSpeedY ?? ScrollSettings.Default.SpeedY);
            var tile = new Vector2(
                curTileX ?? ScrollSettings.Default.TilingX,
                curTileY ?? ScrollSettings.Default.TilingY);

            ImGui.SetNextItemWidth(150);
            if (ImGui.DragFloat2($"Scroll speed##{idScope}", ref speed, 0.002f, -1f, 1f, "%.3f"))
            {
                SetSpeed(speed.X, speed.Y);
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How fast the effect flows, X and Y. Negative reverses the direction;\n" +
                                 "0 holds it still. About 0.01 is a normal rate.");

            ImGui.SetNextItemWidth(150);
            if (ImGui.DragFloat2($"Tiling##{idScope}", ref tile, 0.05f, 0.1f, 20f, "%.2f"))
            {
                SetTile(tile.X, tile.Y);
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How many times the effect repeats across the surface. 1 = once.");
        }
        else   // character.shpk (non-scroll gear)
        {
            bool nylon = curNylon;
            if (ImGui.Checkbox($"Enhanced Nylon##{idScope}", ref nylon))
            {
                SetNylon(nylon);
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Raises the shader's g_AlphaOffset to 1 for a sheerer alpha falloff\n" +
                                 "(nylon/stocking look). character.shpk only; off by default.");
        }

        return changed;
    }

    /// <summary>
    /// Effective (gear, shader) for an option's rows, resolving a live gear override first — exactly
    /// as <see cref="DrawLayerHeader"/> does — then the descriptor. Callers must pass the SAME override
    /// they hand the header, or the row editor's gear controls (sphere map, metalness) would disagree
    /// with the "Gear" the header shows while a design binding is being edited.
    /// </summary>
    public static (bool Gear, string? Shader) EffectiveLayerShader(
        IReadOnlyList<OverlayDescriptor> overlays, GearSettingsPreset? ovr)
    {
        if (overlays.Count == 0) return (false, null);
        var first = overlays[0];
        var layer = ovr?.Layer ?? first.Layer;
        if (layer == OverlayLayer.Skin) return (false, OverlayDescriptor.SkinShader);
        var shader = (ovr != null ? ovr.Shader : first.Shader) ?? OverlayDescriptor.DefaultGearShader;
        return (true, shader);
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
        string? shader,
        ref int selectedRow,
        ref bool changed)
    {
        // characterscroll drives its look from the scroll map; the material fields below do nothing on
        // it (sphere maps provably don't render there), so don't offer knobs that can't turn.
        bool material = gear && !string.Equals(shader, "characterscroll.shpk", StringComparison.OrdinalIgnoreCase);

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

        // ── copy / paste the whole selected row-pair (both sub-rows) ─────────
        // Mirrors Penumbra's advanced-material row copy: grab one row's colours and stamp them onto
        // another. The per-sub-row buttons below (in each A/B panel) do the same for a single column,
        // and share their clipboard, so you can copy sub-row A and paste it into B.
        int curRow = selectedRow;   // a ref param can't be captured by a lambda
        if (ImGui.SmallButton($"Copy row##copyrow_{idScope}"))
            _rowClip = CloneRow(rows.FirstOrDefault(r => r.Row == curRow));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy both sub-rows (A and B) of this row.");

        ImGui.SameLine();
        using (ImRaii.Disabled(_rowClip == null))
        {
            if (ImGui.SmallButton($"Paste row##pasterow_{idScope}"))
            {
                var p = EnsurePreset(rows, selectedRow);
                p.SubRowA = _rowClip!.SubRowA is { } a ? Clone(a) : null;
                p.SubRowB = _rowClip!.SubRowB is { } b ? Clone(b) : null;
                changed = true;
            }
        }
        if (_rowClip == null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Copy a row first.");
        else if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Overwrite row {selectedRow} (both sub-rows) with the copied row.");

        if (ImGui.BeginTable($"##ab_{idScope}", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn($"Row {selectedRow}A");
            ImGui.TableSetupColumn($"Row {selectedRow}B");
            ImGui.TableHeadersRow();
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawSubRow($"{idScope}_A", rows, selectedRow, true, gear, material, ref changed);

            ImGui.TableNextColumn();
            DrawSubRow($"{idScope}_B", rows, selectedRow, false, gear, material, ref changed);

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
        string id, List<ColorTableRowPreset> rows, int row, bool isA, bool gear, bool material,
        ref bool changed)
    {
        var preset = rows.FirstOrDefault(r => r.Row == row);
        var sub = isA ? preset?.SubRowA : preset?.SubRowB;

        ColorTableSubRowPreset Edit()
        {
            var p = EnsurePreset(rows, row);
            if (isA) return p.SubRowA ??= new ColorTableSubRowPreset();
            return p.SubRowB ??= new ColorTableSubRowPreset();
        }

        // ── copy / paste this single sub-row (the "column") ──────────────────
        // The clipboard is shared with the other panel and with "Copy row", so copying sub-row A and
        // pasting into B works, and a row copied whole can seed a single sub-row here.
        if (ImGui.SmallButton($"Copy##copysub_{id}"))
            _subClip = Clone(sub ?? new ColorTableSubRowPreset());
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy this sub-row's values.");

        ImGui.SameLine();
        using (ImRaii.Disabled(_subClip == null))
        {
            if (ImGui.SmallButton($"Paste##pastesub_{id}"))
            {
                var p = EnsurePreset(rows, row);
                if (isA) p.SubRowA = Clone(_subClip!);
                else p.SubRowB = Clone(_subClip!);
                changed = true;
            }
        }
        if (_subClip == null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Copy a sub-row first.");
        else if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Overwrite this sub-row with the copied values.");

        // ── Colours ──────────────────────────────────────────────────────────
        ImGui.TextDisabled("Colours");

        var diffuse = HexToVec3(sub?.Diffuse);
        ImGui.SetNextItemWidth(22);
        if (ImGui.ColorEdit3($"Diffuse##d_{id}", ref diffuse, ImGuiColorEditFlags.NoInputs))
        {
            Edit().Diffuse = Vec3ToHex(diffuse);
            changed = true;
        }
        if (gear && ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "The surface UNDER the glow — it multiplies the overlay's own diffuse art.\n\n" +
                "Keep it DARK for a glowing material, or the glow has nothing to stand out\n" +
                "against and looks faint however high you push it. The vanilla scrolling\n" +
                "materials pair a near-black diffuse with a bright emissive for exactly this.");

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

        // Shown as a percentage. Fine steps matter: characterscroll's glow gate sits at about 2.5%. The
        // cap goes past 100% because the emissive is written as a Half — vanilla scrolling materials push
        // it above 1.0 for a brighter bloom, which a graphics-only (black-background) effect needs to make
        // up for the surface it no longer glows across.
        // Drag is coarse for a quick sweep of the wide range; ctrl+click to type the fine gate (~2.5%).
        float emPct = (sub?.Emissive ?? 0f) * 100f;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragFloat($"Glow##e_{id}", ref emPct, 2.5f, 0f, 1000f, "%.1f%%"))
        {
            Edit().Emissive = Math.Clamp(emPct / 100f, 0f, 10f);
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

        // Roughness / metalness / sphere map are inert under characterscroll.
        if (!material) return;

        // ── Gear, non-scroll shaders only ────────────────────────────────────
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

        // Cap goes past 1.0 for the same reason as Glow: the value is written as a Half, so it can over-
        // drive the sphere-map contribution (and, on characterscroll, the effect's visibility) for a
        // stronger effect than the vanilla 0–1 range allows.
        float sphereInt = sub?.SphereIntensity ?? 0f;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragFloat($"Intensity##si_{id}", ref sphereInt, 0.05f, 0f, 10f, "%.2f"))
        {
            Edit().SphereIntensity = Math.Clamp(sphereInt, 0f, 10f);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(SphereTip);
    }

    // ── copy / paste clipboard ────────────────────────────────────────────────
    // Static so it persists across options and windows for the session — copy from one overlay,
    // paste into another. Both hold deep copies, so later edits to the source don't mutate them.
    private static ColorTableSubRowPreset? _subClip;
    private static ColorTableRowPreset? _rowClip;

    /// <summary>Deep copy of a sub-row. All fields are value types or immutable strings.</summary>
    private static ColorTableSubRowPreset Clone(ColorTableSubRowPreset s) => new()
    {
        Diffuse         = s.Diffuse,
        Emissive        = s.Emissive,
        EmissiveColor   = s.EmissiveColor,
        Opacity         = s.Opacity,
        SphereMap       = s.SphereMap,
        SphereIntensity = s.SphereIntensity,
        Specular        = s.Specular,
        Roughness       = s.Roughness,
        Metalness       = s.Metalness,
    };

    private static ColorTableRowPreset CloneRow(ColorTableRowPreset? p) => new()
    {
        SubRowA = p?.SubRowA is { } a ? Clone(a) : null,
        SubRowB = p?.SubRowB is { } b ? Clone(b) : null,
    };

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
