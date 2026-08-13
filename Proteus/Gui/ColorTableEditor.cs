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
    /// Bottom of the colour editor: the "Glow effect" thumbnail picker (+ its scroll speed/tiling), the
    /// "Advanced" mode-pin, and the "Rendering as" badge to the right of Advanced. Kept below the rows so
    /// the eye lands on the colours first. Sets <paramref name="edited"/> = Glow when the effect changes.
    /// </summary>
    /// <param name="onReset">
    /// Restores this option's settings to the mod's recorded originals; returns true when something
    /// actually changed. Supplied by the caller because only it knows which mod/group/option is open.
    /// Null hides the button entirely.
    /// </param>
    /// <param name="resetDisabledReason">Non-null renders the reset button greyed out and explains why.</param>
    public static bool DrawGlowFooter(
        string idScope,
        IReadOnlyList<OverlayDescriptor> overlays,
        GearSettingsPreset? ovr,
        IReadOnlyList<(string Name, string Path, bool FromMod)> effects,
        out FeatureEdit edited,
        Func<bool>? onReset = null,
        string? resetDisabledReason = null,
        // The compositor promoted this auto skin overlay to a gear shell because it's stacked above gear;
        // show it as Cloth so the footer agrees with the (gear) colour panel, without persisting the change.
        bool promotedToGear = false)
    {
        edited = FeatureEdit.Neutral;
        if (overlays.Count == 0) return false;

        bool changed = false;
        var first = overlays[0];

        string? curScroll = ovr != null ? ovr.Scroll : first.Scroll;
        float? curSpeedX = ovr != null ? ovr.ScrollSpeedX : first.ScrollSpeedX;
        float? curSpeedY = ovr != null ? ovr.ScrollSpeedY : first.ScrollSpeedY;
        float? curTileX  = ovr != null ? ovr.ScrollTilingX : first.ScrollTilingX;
        float? curTileY  = ovr != null ? ovr.ScrollTilingY : first.ScrollTilingY;
        bool curLock = ovr != null ? (ovr.ManualShaderLock ?? false) : first.ManualShaderLock;
        var mode = RenderModeInference.ModeOf(ovr?.Layer ?? first.Layer, ovr != null ? ovr.Shader : first.Shader);
        if (promotedToGear && mode == RenderMode.Skin) mode = RenderMode.Cloth;   // stacked above gear → renders as a shell

        void SetScroll(string? s)       { if (ovr != null) ovr.Scroll = s;  else foreach (var d in overlays) d.Scroll = s; }
        void SetSpeed(float x, float y) { if (ovr != null) { ovr.ScrollSpeedX = x; ovr.ScrollSpeedY = y; } else foreach (var d in overlays) { d.ScrollSpeedX = x; d.ScrollSpeedY = y; } }
        void SetTile(float x, float y)  { if (ovr != null) { ovr.ScrollTilingX = x; ovr.ScrollTilingY = y; } else foreach (var d in overlays) { d.ScrollTilingX = x; d.ScrollTilingY = y; } }
        void SetLock(bool v)            { if (ovr != null) ovr.ManualShaderLock = v; else foreach (var d in overlays) d.ManualShaderLock = v; }

        // ── Glow effect: a thumbnail picker (like the sphere-map picker) — picking one switches to Animated glow ──
        DrawEffectPicker(idScope, effects, curScroll, out bool effChanged, out string? newScroll);
        if (effChanged)
        {
            SetScroll(newScroll);
            edited = FeatureEdit.Glow;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Pick a scrolling map to make this overlay GLOW and animate — that switches it to\n" +
                "Animated glow. The map IS the glow (colour, pattern, motion); the row's Glow scales it.\n" +
                "Effects come from the mod's Proteus/Effects/ folder, then your Effects folder.");
        if (effects.Count == 0)
            // Names the Settings button verbatim: this is the exact moment someone needs that folder, so
            // the message has to point at a control they can actually find on screen.
            ImGui.TextDisabled("No effects found — drop images into the folder the Glow Effect Textures\nbutton opens (Settings), or the mod's own Proteus/Effects/ folder.");

        // Scroll speed / tiling — only meaningful once glowing.
        if (mode == RenderMode.Glow)
        {
            var speed = new Vector2(curSpeedX ?? ScrollSettings.Default.SpeedX, curSpeedY ?? ScrollSettings.Default.SpeedY);
            var tile  = new Vector2(curTileX ?? ScrollSettings.Default.TilingX, curTileY ?? ScrollSettings.Default.TilingY);

            ImGui.SetNextItemWidth(150);
            if (ImGui.DragFloat2($"Scroll speed##{idScope}", ref speed, 0.002f, -1f, 1f, "%.3f"))
            {
                SetSpeed(speed.X, speed.Y);
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How fast the effect flows, X and Y. Negative reverses; 0 holds it still. ~0.01 is normal.");

            ImGui.SetNextItemWidth(150);
            if (ImGui.DragFloat2($"Tiling##{idScope}", ref tile, 0.05f, 0.1f, 20f, "%.2f"))
            {
                SetTile(tile.X, tile.Y);
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How many times the effect repeats across the surface. 1 = once.");
        }

        // ── Advanced (mode pin) at the very bottom, with the "Rendering as" badge to its right ──
        bool advOpen = ImGui.TreeNodeEx($"Advanced##{idScope}", ImGuiTreeNodeFlags.NoTreePushOnOpen);

        ImGui.SameLine(0f, 24f);
        DrawRenderingAsBadge(mode);
        ImGui.SameLine();
        ImGui.TextDisabled(curLock ? "(pinned)" : "(auto)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(curLock
                ? "You pinned this mode in Advanced — it no longer follows the features you set.\nClick \"Back to auto\" to let it adapt again."
                : "The mode follows what you use: a sphere map or metal ⇒ Cloth,\na glow effect ⇒ Animated glow, nothing special ⇒ Skin.");
        if (curLock)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Back to auto##{idScope}")) { SetLock(false); changed = true; }
        }

        if (advOpen)
        {
            ImGui.TextDisabled("Force the render mode instead of letting the features pick it:");
            foreach (var m in new[] { RenderMode.Skin, RenderMode.Cloth, RenderMode.Glow })
            {
                bool sel = curLock && mode == m;
                if (ImGui.RadioButton($"{ModeName(m)}##force_{idScope}_{m}", sel) && !sel)
                {
                    ApplyMode(overlays, ovr, m);
                    SetLock(true);
                    changed = true;
                }
                if (m != RenderMode.Glow) ImGui.SameLine();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Skin (painted) — skin.shpk.  Cloth — character.shpk (sphere, metal).\n" +
                                 "Animated glow — characterscroll.shpk. Pinning stops the auto mode-switch.");

            if (onReset != null)
            {
                ImGui.Separator();
                bool disabled = resetDisabledReason != null;
                // Ctrl-guarded: this overwrites the option's current colours/glow/mode with no undo.
                bool armed = !disabled && ImGui.GetIO().KeyCtrl;
                using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (armed ? 1f : 0.5f)))
                {
                    if (ImGui.Button($"Reset to defaults##reset_{idScope}") && armed && onReset())
                        changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(disabled
                        ? resetDisabledReason
                        : "Hold Ctrl and click to restore this option's colours, glow and mode to the\n" +
                          "settings Proteus first recorded for this mod. Cannot be undone.\n\n" +
                          "If a design is applied, this also drops that design's saved override for this\n" +
                          "option — otherwise the design would just re-impose it. Other designs keep theirs.\n\n" +
                          "Note: those originals were captured the first time Proteus saved this mod —\n" +
                          "if you had already edited it before then, that edited state is the \"default\".");
            }
        }

        return changed;
    }

    /// <summary>Thumbnail picker for the glow effect, modelled on <see cref="DrawSpherePicker"/>: the current
    /// effect's image sits beside the combo, and the list is clickable pictures. <paramref name="newScroll"/>
    /// is the chosen effect's file name (null = None) when <paramref name="changed"/> is true.</summary>
    private static void DrawEffectPicker(string id,
        IReadOnlyList<(string Name, string Path, bool FromMod)> effects,
        string? curScroll, out bool changed, out string? newScroll)
    {
        changed = false;
        newScroll = curScroll;
        const float current = 32f;
        const float thumb   = 56f;

        // Current effect's thumbnail beside the combo — the "none" icon when None, else the effect's image.
        var cur = effects.FirstOrDefault(e => string.Equals(e.Name, curScroll, StringComparison.OrdinalIgnoreCase));
        bool drewBeside = curScroll == null
            ? Spheres?.DrawNone(current) == true
            : cur.Path != null && EffectThumbs?.Draw(cur.Path, current) == true;
        if (drewBeside) ImGui.SameLine();

        var label = curScroll == null ? "None" : Path.GetFileNameWithoutExtension(curScroll);
        ImGui.SetNextItemWidth(180);
        if (ImGui.BeginCombo($"Glow effect##eff_{id}", label, ImGuiComboFlags.HeightLarge))
        {
            // None entry with the shared "none" icon (matches the sphere-map picker).
            bool noneHas = false, noneClicked = false;
            if (Spheres != null) noneClicked = Spheres.DrawNoneButton($"##effnone_{id}", thumb, out noneHas);
            if (noneClicked && curScroll != null)
            {
                newScroll = null;
                changed = true;
                ImGui.CloseCurrentPopup();
            }
            if (noneHas) ImGui.SameLine();
            if (ImGui.Selectable($"None##effnonesel_{id}", curScroll == null, ImGuiSelectableFlags.None,
                    new Vector2(0, noneHas ? thumb : 0)) && curScroll != null)
            {
                newScroll = null;
                changed = true;
            }
            foreach (var (name, path, fromMod) in effects)
            {
                bool selected = string.Equals(name, curScroll, StringComparison.OrdinalIgnoreCase);

                bool clicked = false, hasThumb = false;
                if (EffectThumbs != null)
                    clicked = EffectThumbs.DrawButton($"##effimg_{id}_{name}", path, thumb, out hasThumb);
                if (clicked && !selected)
                {
                    newScroll = name;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
                if (hasThumb) ImGui.SameLine();

                var text = fromMod ? $"{Path.GetFileNameWithoutExtension(name)}  (mod)" : Path.GetFileNameWithoutExtension(name);
                if (ImGui.Selectable($"{text}##eff_{id}_{name}", selected, ImGuiSelectableFlags.None,
                        new Vector2(0, hasThumb ? thumb : 0)) && !selected)
                {
                    newScroll = name;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
    }

    /// <summary>
    /// Effective (gear, shader) for an option's rows, resolving a live gear override first — exactly as
    /// <see cref="DrawGlowFooter"/> does — then the descriptor. Callers must pass the SAME override they hand
    /// the footer, or the row editor's gear controls (sphere map, metalness) would disagree with the mode
    /// the badge shows while a design binding is being edited.
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

    /// <summary>Friendly, mechanism-free mode name for the badge and Advanced labels.</summary>
    public static string ModeName(RenderMode m) => m switch
    {
        RenderMode.Skin  => "Skin (painted)",
        RenderMode.Cloth => "Cloth",
        _                => "Animated glow",
    };

    /// <summary>Draws the "Rendering as: &lt;mode&gt;" badge (same colours as the footer) on the current line.</summary>
    public static void DrawRenderingAsBadge(RenderMode mode)
    {
        var badgeColor = mode switch
        {
            RenderMode.Cloth => new Vector4(0.60f, 0.80f, 1.00f, 1f),   // cool blue
            RenderMode.Glow  => new Vector4(0.96f, 0.77f, 0.19f, 1f),   // #F4C430 gold
            _                => new Vector4(0.80f, 0.75f, 0.68f, 1f),   // warm skin
        };
        ImGui.TextUnformatted("Rendering as:");
        ImGui.SameLine();
        ImGui.TextColored(badgeColor, ModeName(mode));
    }

    /// <summary>Point the descriptors' (or the live design-binding override's) Layer+Shader at
    /// <paramref name="mode"/> — how the inference result and the Advanced picker are both applied.</summary>
    public static void ApplyMode(IReadOnlyList<OverlayDescriptor> overlays, GearSettingsPreset? ovr, RenderMode mode)
    {
        var (layer, shader) = mode switch
        {
            RenderMode.Skin  => (OverlayLayer.Skin, (string?)null),
            RenderMode.Cloth => (OverlayLayer.Gear, RenderModeInference.ClothShader),
            _                => (OverlayLayer.Gear, RenderModeInference.GlowShader),
        };
        if (ovr != null) { ovr.Layer = layer; ovr.Shader = shader; }
        else foreach (var d in overlays) { d.Layer = layer; d.Shader = shader; }
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
        IReadOnlyList<string>? shellMaterialLeaves,
        IReadOnlyList<Proteus.Interop.SkinGlowTarget>? skinGlowTargets,
        out FeatureEdit edited,
        ref int selectedRow,
        ref bool changed)
    {
        edited = FeatureEdit.Neutral;

        // The render mode drives which feature controls are LIVE vs dimmed; the features that are set
        // drive the mode back (inference happens in the caller). characterscroll drives its look from the
        // scroll map, so sphere/metal don't render there.
        var mode = RenderModeInference.ModeOf(gear ? OverlayLayer.Gear : OverlayLayer.Skin, shader);

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
            DrawSubRow($"{idScope}_A", rows, selectedRow, true, mode, shellMaterialLeaves, skinGlowTargets, ref edited, ref changed);

            ImGui.TableNextColumn();
            DrawSubRow($"{idScope}_B", rows, selectedRow, false, mode, shellMaterialLeaves, skinGlowTargets, ref edited, ref changed);

            ImGui.EndTable();
        }
    }

    /// <summary>Thumbnails for the sphere-map picker. Set once at startup; null just means no previews.</summary>
    public static SphereMapPreview? Spheres { get; set; }

    /// <summary>Thumbnails for the glow-effect picker. Set once at startup; null falls back to names only.</summary>
    public static EffectPreview? EffectThumbs { get; set; }

    /// <summary>Live colorset "glow / target" highlighter. Set once at startup; null disables the buttons.</summary>
    public static Proteus.Interop.ColorTableHighlighter? Highlighter { get; set; }

    /// <summary>Live skin-row glow via render-material diffuse rebind. Set once at startup; null disables the button.</summary>
    public static Proteus.Interop.SkinDiffuseGlow? SkinGlow { get; set; }

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
        string id, List<ColorTableRowPreset> rows, int row, bool isA, RenderMode mode,
        IReadOnlyList<string>? shellMaterialLeaves,
        IReadOnlyList<Proteus.Interop.SkinGlowTarget>? skinGlowTargets,
        ref FeatureEdit edited, ref bool changed)
    {
        bool gear     = mode != RenderMode.Skin;
        bool material = mode == RenderMode.Cloth;   // sphere / metal / roughness live here
        const float DimAlpha = 0.5f;                // dimmed-but-clickable: a feature the mode ignores

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

        // ── glow / target: hue-cycle this sub-row's mesh on the live character ──
        // The overlay's colour table is baked into its shell material(s) ss_{letter}.mtrl; game row for
        // this sub-row is (row-1)*2 + (A?0:1). Click-toggle: clicking again (or another sub-row) moves the
        // glow. A mod/option with several gear overlays shares one colour table, so all of them glow.
        if (gear && Highlighter != null && shellMaterialLeaves is { Count: > 0 })
        {
            int gameRow = (row - 1) * 2 + (isA ? 0 : 1);
            bool active = Highlighter.IsTarget(shellMaterialLeaves, gameRow);
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), active))
                if (ImGui.SmallButton($"{(active ? "Glowing" : "Glow")}##glow_{id}"))
                {
                    if (active) Highlighter.Clear();
                    else Highlighter.SetTarget(shellMaterialLeaves, gameRow);
                }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Make this sub-row's mesh glow on your character so you can find it\n" +
                                 "in-game. Click again to stop.");
        }
        // Skin overlays have no live colour table; SkinGlow rebinds the body diffuse instead. Static
        // bright (it can't hue-cycle — the highlight is a baked 4K texture, rebuilt only on click).
        else if (!gear && SkinGlow != null && skinGlowTargets is { Count: > 0 })
        {
            bool active = SkinGlow.IsTarget(skinGlowTargets, row, isA);
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), active))
                if (ImGui.SmallButton($"{(active ? "Glowing" : "Glow")}##glow_{id}"))
                {
                    if (active) SkinGlow.Clear();
                    else SkinGlow.SetTarget(skinGlowTargets, row, isA);
                }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Light up this sub-row's region on your character's skin so you can find\n" +
                                 "it in-game. Click again to stop. (Takes a moment to build the first time.)");
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
        if (gear && ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "The surface UNDER the glow — it multiplies the overlay's own diffuse art.\n\n" +
                "Keep it DARK for a glowing material, or the glow has nothing to stand out\n" +
                "against and looks faint however high you push it. The vanilla scrolling\n" +
                "materials pair a near-black diffuse with a bright emissive for exactly this.");

        // Specular is a Cloth feature. Dimmed-but-clickable in Skin (touch to switch to Cloth), active in
        // Cloth. HIDDEN in Animated glow — like the Physical/Sphere block below — so a stray touch on a
        // dimmed control can't silently flip the overlay out of glow.
        if (mode != RenderMode.Glow)
        {
            using var d = ImRaii.PushStyle(ImGuiStyleVar.Alpha, material ? 1f : DimAlpha);
            var spec = HexToVec3(sub?.Specular);
            ImGui.SetNextItemWidth(22);
            if (ImGui.ColorEdit3($"Specular##s_{id}", ref spec, ImGuiColorEditFlags.NoInputs))
            {
                Edit().Specular = Vec3ToHex(spec);
                edited = FeatureEdit.Cloth;
                changed = true;
            }
        }
        // Glow colour applies in any gear mode (it's the emissive colour); dimmed only on Skin.
        {
            using var d = ImRaii.PushStyle(ImGuiStyleVar.Alpha, gear ? 1f : DimAlpha);
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
            // Skin cannot emit, so asking for glow asks for a shell: classify the edit as Cloth and let
            // the inference move the overlay there, exactly as a sphere map or metalness does. The one
            // exception is Animated glow, where this slider is the scroll effect's GATE rather than a
            // feature request — treating it as Cloth there would kick the overlay out of Glow the moment
            // the author tuned the gate.
            edited = mode == RenderMode.Glow ? FeatureEdit.Neutral : FeatureEdit.Cloth;
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

        // Roughness / metalness / sphere map belong to Cloth. Dimmed-but-clickable in Skin (touch
        // one to switch to Cloth), active in Cloth. HIDDEN in Animated glow: they don't apply there, AND
        // SphereIntensity is repurposed by characterscroll as the effect's visibility — exposing it as a
        // "sphere" control let a stray 0 silently kill the glow.
        if (mode != RenderMode.Glow)
        {
            using var d = ImRaii.PushStyle(ImGuiStyleVar.Alpha, material ? 1f : DimAlpha);

            ImGui.TextDisabled("Physical");
            if (!material) { ImGui.SameLine(); ImGui.TextDisabled("— Cloth"); }

            float rough = sub?.Roughness ?? 0.5f;
            ImGui.SetNextItemWidth(70);
            if (ImGui.DragFloat($"Roughness##r_{id}", ref rough, 0.01f, 0f, 1f, "%.2f"))
            {
                Edit().Roughness = Math.Clamp(rough, 0f, 1f);
                // Roughness isn't a mode trigger (it does nothing without metal/sphere), so don't flip to Cloth.
                changed = true;
            }

            float metal = sub?.Metalness ?? 0f;
            ImGui.SetNextItemWidth(70);
            if (ImGui.DragFloat($"Metalness##m_{id}", ref metal, 0.01f, 0f, 1f, "%.2f"))
            {
                Edit().Metalness = Math.Clamp(metal, 0f, 1f);
                edited = FeatureEdit.Cloth;
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(MetalTip);

            ImGui.TextDisabled("Sphere map");

            int sphere = sub?.SphereMap ?? 0;
            DrawSpherePicker(id, ref sphere, out bool sphereChanged);
            if (sphereChanged)
            {
                var e = Edit();
                e.SphereMap = Math.Clamp(sphere, 0, SphereMapPreview.Count - 1);
                // A sphere needs BOTH index and intensity non-zero to show, so default the intensity to 3
                // when picking one for the first time (leave a user-set value alone).
                if (e.SphereMap > 0 && (e.SphereIntensity ?? 0f) <= 0f)
                    e.SphereIntensity = 3f;
                edited = FeatureEdit.Cloth;
                changed = true;
            }

            // Cap goes past 1.0 for the same reason as Glow: the value is written as a Half, so it can over-
            // drive the sphere-map contribution for a stronger effect than the vanilla 0–1 range allows.
            float sphereInt = sub?.SphereIntensity ?? 0f;
            ImGui.SetNextItemWidth(70);
            if (ImGui.DragFloat($"Intensity##si_{id}", ref sphereInt, 0.05f, 0f, 10f, "%.2f"))
            {
                Edit().SphereIntensity = Math.Clamp(sphereInt, 0f, 10f);
                edited = FeatureEdit.Cloth;
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(SphereTip);
        }
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
