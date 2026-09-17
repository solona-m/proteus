using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The colour-table editor, shared by both overlay layers: skin and gear overlays store their colours in the same
/// <see cref="ColorTableSubRowPreset"/>, so one editor shows or hides fields per layer.
/// </summary>
public static partial class ColorTableEditor
{
    /// <summary>How close to 1.0 counts as "the default" for skin-tint suppression; shared by the store decision and
    /// the collapsed-state readout so the two never disagree.</summary>
    private const float SkinTintEpsilon = 0.0005f;

    /// <summary>Where the reinforced-toe slider starts when the box is first ticked: the common density, not the maximum.</summary>
    private const int ReinforcedToeDefault = 45;

    private static readonly string[] GearShaders = ["character.shpk", "characterscroll.shpk"];

    /// <summary>What an unset glow colour looks like in the swatch. Shared with <see cref="ContentGlowRow"/>.</summary>
    internal const string WhiteHex = "#FFFFFF";

    // Localized, so not const; resolved once per language (see Strings).
    private static string SphereTip    => Strings.Colors.SphereTip;
    private static string MetalTip     => Strings.Colors.MetalTip;
    private static string TileTip      => Strings.Colors.TileTip;
    private static string TileScaleTip => Strings.Colors.TileScaleTip;

    /// <summary>
    /// The "Effects" disclosure: the glow effect and Skindent. Keyed per MOD so the state holds across option tabs;
    /// "###" so a language switch doesn't reset it.
    /// </summary>
    public static bool EffectsHeader(string modScope)
        => ImGui.CollapsingHeader($"{Strings.Colors.EffectsSection}###effects_{modScope}",
            ImGuiTreeNodeFlags.DefaultOpen);

    /// <summary>
    /// Bottom of the colour editor: the glow effect picker, the Advanced mode-pin and the "Rendering as" badge.
    /// Sets <paramref name="edited"/> = Glow when the effect changes.
    /// </summary>
    /// <param name="onReset">Restores this option's settings to the mod's recorded originals; returns true when
    /// something changed. Null hides the button.</param>
    /// <param name="resetDisabledReason">Non-null renders the reset button greyed out and explains why.</param>
    /// <param name="drawExtraAdvanced">Extra per-MOD settings drawn inside the Advanced disclosure, above the reset
    /// button. Commits and recomposites for itself rather than through this method's return.</param>
    /// <param name="advancedScope">ImGui id for the Advanced disclosure alone; callers pass the mod, so its open
    /// state holds across option tabs.</param>
    public static bool DrawGlowFooter(
        string idScope,
        string advancedScope,
        IReadOnlyList<OverlayDescriptor> overlays,
        GearSettingsPreset? ovr,
        IReadOnlyList<(string Name, string Path, bool FromMod)> effects,
        out FeatureEdit edited,
        Func<bool>? onReset = null,
        string? resetDisabledReason = null,
        Action? drawExtraAdvanced = null,
        // Mod-wide sections drawn between the glow controls and Advanced; commits for itself. Null hides it.
        Action? drawBelowGlow = null,
        // Mod-wide controls drawn INSIDE the Effects section, after the glow effect; commits for itself.
        Action? drawInEffects = null,
        // Non-null when this option's render mode is decided elsewhere: a short marker such as "(forced)" beside the badge,
        // replacing (auto)/(pinned) and suppressing "Back to auto".
        string? modeForced = null,
        // The full explanation behind modeForced: the badge's tooltip and the text in place of the force-mode radios.
        string? modeForcedTip = null,
        // The compositor promoted this auto skin overlay to a gear shell; show it as Cloth without persisting the change.
        bool promotedToGear = false,
        // Non-null when this option can't be a cloth/glow layer (it paints gear, not skin). Disables those controls and is
        // shown beneath the picker: a DISABLED item never reports hover, so a tooltip could not carry it.
        string? noShellReason = null,
        // False where the skin-tint pass never reads these descriptors (the Masks tab), so the slider would change nothing.
        bool skinTintApplies = true,
        // True while a design binding is driving this mod: edits preview into the binding and metadata.json is not written,
        // so sidecar-only controls are hidden. Not inferable from `ovr != null`: that is the binding's GEAR entry, not its colour.
        bool overrideActive = false,
        // True when a toe cap is active in this mod's look AND this option renders on a shell that can carry one
        // (see StatusWindow.CanReinforceToe). Hidden rather than disabled when false.
        bool toeCapActive = false)
    {
        return new GlowFooter(idScope, advancedScope, overlays, ovr, effects, onReset, resetDisabledReason, drawExtraAdvanced, drawBelowGlow, drawInEffects, modeForced, modeForcedTip, promotedToGear, noShellReason, skinTintApplies, overrideActive, toeCapActive).Run(out edited);
    }

    /// <summary>
    /// The glow footer for an imported content pack's material: pick an effect, then set its speed and tiling.
    /// Returns true when something changed and the caller should persist and recomposite. <paramref name="glow"/> is
    /// mutated in place; the caller owns whether that is the sidecar's or a binding's copy.
    /// </summary>
    public static bool DrawContentGlowFooter(
        string idScope,
        IReadOnlyList<(string Name, string Path, bool FromMod)> effects,
        GearSettingsPreset glow)
    {
        bool changed = false;
        var cs = Strings.Colors;

        DrawEffectPicker(idScope, effects, glow.Scroll, out bool effChanged, out string? newScroll);
        if (effChanged)
        {
            glow.Scroll = newScroll;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.GlowEffectTip);
        if (effects.Count == 0)
            // Names the Settings button verbatim: this is the exact moment someone needs that folder.
            ImGui.TextDisabled(cs.NoEffects);

        // Speed and tiling mean nothing until there is a pattern to move, so they appear with one.
        if (string.IsNullOrEmpty(glow.Scroll)) return changed;

        // A stored effect the picker can no longer offer: the composite falls back to the pack's own material, so say so.
        if (!effects.Any(e => string.Equals(e.Name, glow.Scroll, StringComparison.OrdinalIgnoreCase)))
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ProteusStyle.Warn, string.Format(Strings.Content.GlowEffectMissingFmt, glow.Scroll));
            ImGui.PopTextWrapPos();
        }

        var speed = new Vector2(glow.ScrollSpeedX ?? ScrollSettings.Default.SpeedX,
                                glow.ScrollSpeedY ?? ScrollSettings.Default.SpeedY);
        var tile  = new Vector2(glow.ScrollTilingX ?? ScrollSettings.Default.TilingX,
                                glow.ScrollTilingY ?? ScrollSettings.Default.TilingY);

        ImGui.SetNextItemWidth(150);
        if (ImGui.DragFloat2($"{cs.ScrollSpeed}##content_{idScope}", ref speed, 0.002f, -1f, 1f, "%.3f"))
        {
            glow.ScrollSpeedX = speed.X;
            glow.ScrollSpeedY = speed.Y;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.ScrollSpeedTip);

        ImGui.SetNextItemWidth(150);
        if (ImGui.DragFloat2($"{cs.Tiling}##content_{idScope}", ref tile, 0.05f, 0.1f, 20f, "%.2f"))
        {
            glow.ScrollTilingX = tile.X;
            glow.ScrollTilingY = tile.Y;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.TilingTip);

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

        var cs = Strings.Colors;
        var label = curScroll == null ? cs.None : Path.GetFileNameWithoutExtension(curScroll);
        ImGui.SetNextItemWidth(180);
        if (ImGui.BeginCombo($"{cs.GlowEffect}##eff_{id}", label, ImGuiComboFlags.HeightLarge))
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
            if (ImGui.Selectable($"{cs.None}##effnonesel_{id}", curScroll == null, ImGuiSelectableFlags.None,
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

                var text = fromMod
                    ? string.Format(cs.EffectFromModFmt, Path.GetFileNameWithoutExtension(name))
                    : Path.GetFileNameWithoutExtension(name);
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
    /// Effective (gear, shader) for an option's rows, resolving a live gear override first, then the descriptor.
    /// Callers must pass the SAME override they hand <see cref="DrawGlowFooter"/>.
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
        RenderMode.Skin  => Strings.Colors.ModeSkin,
        RenderMode.Cloth => Strings.Colors.ModeCloth,
        _                => Strings.Colors.ModeGlow,
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
        ImGui.TextUnformatted(Strings.Colors.RenderingAs);

        // Where ImGui actually put that text: after a framed item the line's text baseline is FramePadding.y below the raw
        // top. Pill paints straight to the draw list, so it needs the real top.
        var textTop = ImGui.GetItemRectMin().Y;
        ImGui.SameLine();
        ProteusStyle.Pill(ModeName(mode), badgeColor, textTop - ImGui.GetCursorScreenPos().Y);
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

    /// <summary>Set (or release) the manual mode pin, on the same target <see cref="ApplyMode"/> writes to, so the
    /// pin and the mode it pins never end up on different objects.</summary>
    public static void SetManualShaderLock(IReadOnlyList<OverlayDescriptor> overlays,
        GearSettingsPreset? ovr, bool locked)
    {
        if (ovr != null) ovr.ManualShaderLock = locked;
        else foreach (var d in overlays) d.ManualShaderLock = locked;
    }

    /// <summary>
    /// Row picker plus the A/B detail panels for the selected row. <paramref name="usedRows"/> (when non-null) marks
    /// rows the index texture uses. <paramref name="authoredPhysical"/> says the roughness and metalness came from
    /// the material's own author rather than a Proteus template.
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
        ref bool changed,
        bool authoredPhysical = false,
        IReadOnlyList<(float Roughness, float Metalness)>? physicalBaseline = null,
        // The Masks tab: an unset sub-row renders MIRRORED from its partner (ShellColorRows.BuildRows), so display follows.
        bool mirrorUnsetSubRows = false,
        // Whether the light response can reach this table: true for a Proteus shell, false for an imported pack's
        // verbatim material, where the control would do nothing.
        bool lightResponseApplies = true,
        // The sub-row column the index actually lands in: "A", "B", or null for both or unknown. The other column is
        // dimmed like an unsampled row.
        string? usedSubRow = null)
    {
        edited = FeatureEdit.Neutral;

        // Resolved once rather than per swatch. Never while mirroring: an unset column still reaches the screen through its partner.
        bool onlyA = !mirrorUnsetSubRows && string.Equals(usedSubRow, "A", StringComparison.Ordinal);
        bool onlyB = !mirrorUnsetSubRows && string.Equals(usedSubRow, "B", StringComparison.Ordinal);

        // The render mode drives which feature controls are live vs dimmed; characterscroll does not render sphere/metal.
        var mode = RenderModeInference.ModeOf(gear ? OverlayLayer.Gear : OverlayLayer.Skin, shader);

        // Rows the index texture never selects are DIMMED, not hidden or disabled: the reading can be wrong, and must not
        // block editing.
        bool InUse(int r) => usedRows == null || usedRows.Contains(r);

        // Land on a live row only when nothing has been chosen yet (0 = unset), so a dimmed row can be picked on purpose.
        if (selectedRow is <= 0 or > 16)
        {
            int firstUsed = Enumerable.Range(1, 16).FirstOrDefault(InUse);
            selectedRow = firstUsed == 0 ? 1 : firstUsed;
        }
        int sel = selectedRow;                       // a ref param can't be captured by a lambda

        // ── row picker ───────────────────────────────────────────────────────
        // Each button previews its row: diffuse, specular, glow columns, each split top = A, bottom = B.
        // Left-align the label, or it centres itself under the swatches and disappears.
        using var align = ImRaii.PushStyle(ImGuiStyleVar.ButtonTextAlign, new Vector2(0f, 0.5f));

        // Wraps to the window's width. Scaled together with the swatch insets in DrawRowSwatches, which are measured off
        // this button's rect.
        var btn = ProteusStyle.S(70f, 30f);
        float avail = ImGui.GetContentRegionAvail().X;
        var cs = Strings.Colors;
        int perLine = Math.Max(1, (int)((avail + ImGui.GetStyle().ItemSpacing.X) / (btn.X + ImGui.GetStyle().ItemSpacing.X)));

        for (int row = 1; row <= 16; row++)
        {
            if ((row - 1) % perLine != 0) ImGui.SameLine();

            bool used = InUse(row);
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, used ? 1f : 0.5f))
            using (ProteusStyle.Selected(row == selectedRow))
            {
                if (ImGui.Button($"#{row:D2}##row_{idScope}_{row}", btn))
                    selectedRow = row;
            }
            if (!used && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(cs.RowUnusedTip);

            DrawRowSwatches(rows, row, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), used,
                mirrorUnsetSubRows, onlyA, onlyB);
        }

        ImGui.Separator();

        // ── copy / paste the whole selected row-pair (both sub-rows) ─────────
        // Shares its clipboard with the per-sub-row buttons, so sub-row A can be pasted into B.
        int curRow = selectedRow;   // a ref param can't be captured by a lambda
        if (ImGui.SmallButton($"{cs.CopyRow}##copyrow_{idScope}"))
            _rowClip = CloneRow(rows.FirstOrDefault(r => r.Row == curRow));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.CopyRowTip);

        ImGui.SameLine();
        using (ImRaii.Disabled(_rowClip == null))
        {
            if (ImGui.SmallButton($"{cs.PasteRow}##pasterow_{idScope}"))
            {
                var p = EnsurePreset(rows, selectedRow);
                p.SubRowA = _rowClip!.SubRowA is { } a ? Clone(a) : null;
                p.SubRowB = _rowClip!.SubRowB is { } b ? Clone(b) : null;
                changed = true;
            }
        }
        if (_rowClip == null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(cs.NeedRowCopy);
        else if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Format(cs.PasteRowTipFmt, selectedRow));

        using (ImRaii.PushColor(ImGuiCol.TableHeaderBg, ImGui.GetColorU32(ProteusStyle.AccentSoft)))
        if (ImGui.BeginTable($"##ab_{idScope}", 2,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn(string.Format(cs.SubRowAFmt, selectedRow));
            ImGui.TableSetupColumn(string.Format(cs.SubRowBFmt, selectedRow));
            ImGui.TableHeadersRow();
            ImGui.TableNextRow();

            // Said in the dead column rather than dimmed: ImGui's Alpha style var REPLACES rather than multiplies, and the
            // controls below push their own.
            ImGui.TableNextColumn();
            if (onlyB) ProteusStyle.DisabledWrapped(cs.SubRowUnused);
            DrawSubRow($"{idScope}_A", rows, selectedRow, true, mode, shellMaterialLeaves, skinGlowTargets,
                authoredPhysical, physicalBaseline, ref edited, ref changed, mirrorUnsetSubRows,
                lightResponseApplies);

            ImGui.TableNextColumn();
            if (onlyA) ProteusStyle.DisabledWrapped(cs.SubRowUnused);
            DrawSubRow($"{idScope}_B", rows, selectedRow, false, mode, shellMaterialLeaves, skinGlowTargets,
                authoredPhysical, physicalBaseline, ref edited, ref changed, mirrorUnsetSubRows,
                lightResponseApplies);

            ImGui.EndTable();
        }
    }

    /// <summary>Thumbnails for the sphere-map picker. Set once at startup; null just means no previews.</summary>
    public static SphereMapPreview? Spheres { get; set; }

    /// <summary>Thumbnails for the tile picker. Set once at startup; null just means no previews.</summary>
    public static TilePreview? Tiles { get; set; }

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
        if (ImGui.BeginCombo($"{Strings.Colors.SphereIndex}##sp_{id}", index.ToString(), ImGuiComboFlags.HeightLarge))
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
    /// Fabric weave, as a dropdown of thumbnails. Like <see cref="DrawSpherePicker"/> plus a None entry
    /// (<paramref name="index"/> below zero), since tile 0 is a real weave.
    /// </summary>
    private static void DrawTilePicker(string id, ref int index, out bool changed)
    {
        changed = false;
        const float current = 32f;   // the one in use, beside the combo
        const float thumb = 56f;     // the pictures in the list — click one to pick it

        var cs = Strings.Colors;
        if (index < 0) Spheres?.DrawNone(current);
        else Tiles?.Draw(index, current);
        if (Tiles != null || Spheres != null) ImGui.SameLine();

        ImGui.SetNextItemWidth(70);
        var label = index < 0 ? cs.TileNone : index.ToString();
        if (ImGui.BeginCombo($"{cs.TilePattern}##tl_{id}", label, ImGuiComboFlags.HeightLarge))
        {
            // "No weave" first, so switching it off doesn't mean hunting through sixty-four pictures.
            bool noneAvailable = false;
            if (Spheres?.DrawNoneButton($"##tlnone_{id}", thumb, out noneAvailable) == true && index >= 0)
            {
                index = -1;
                changed = true;
                ImGui.CloseCurrentPopup();
            }
            if (noneAvailable) ImGui.SameLine();

            if (ImGui.Selectable($"{cs.TileNone}##tln_{id}", index < 0, ImGuiSelectableFlags.None,
                    new Vector2(0, thumb)) && index >= 0)
            {
                index = -1;
                changed = true;
            }

            for (int i = 0; i < TilePreview.Count; i++)
            {
                // The picture is the button — clicking it selects that index and closes the list.
                if (Tiles?.DrawButton($"##tlimg_{id}_{i}", i, thumb) == true && i != index)
                {
                    index = i;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
                if (Tiles != null) ImGui.SameLine();

                if (ImGui.Selectable($"{i}##tl_{id}_{i}", i == index, ImGuiSelectableFlags.None,
                        new Vector2(0, thumb)) && i != index)
                {
                    index = i;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(TileTip);
    }

    /// <summary>
    /// Paint a row's colours onto its picker button: diffuse, specular, glow columns, each split top = sub-row A,
    /// bottom = sub-row B. The glow swatch is the emissive colour scaled by its intensity.
    /// </summary>
    /// <param name="onlyA">The index lands in column A everywhere, so the B half of this strip is dead.</param>
    /// <param name="onlyB">The mirror of that. Both false = shared or unknown, and neither half is dimmed.</param>
    private static void DrawRowSwatches(
        List<ColorTableRowPreset> rows, int row, Vector2 min, Vector2 max, bool used,
        bool mirrorUnsetSubRows = false, bool onlyA = false, bool onlyB = false)
    {
        var preset = rows.FirstOrDefault(r => r.Row == row);
        var draw = ImGui.GetWindowDrawList();

        // The label sits on the left; swatches fill the right half. Scaled to match the button size in DrawRows.
        float x0 = min.X + ProteusStyle.S(34f), x1 = max.X - ProteusStyle.S(3f);
        float y0 = min.Y + ProteusStyle.S(3f), y1 = max.Y - ProteusStyle.S(3f);
        if (x1 <= x0) return;

        float colW = (x1 - x0) / 3f;
        float midY = (y0 + y1) * 0.5f;

        // Unused rows dimmed by hand, since these swatches are hand-drawn; per HALF, since the column is a second axis.
        float dim = used ? 1f : 0.25f;
        float dimA = onlyB ? 0.25f : 1f;
        float dimB = onlyA ? 0.25f : 1f;

        Vector3 Swatch(ColorTableSubRowPreset? s, int column, float half) => dim * half * (column switch
        {
            0 => HexToVec3(s?.Diffuse),
            1 => HexToVec3(s?.Specular),
            _ => HexToVec3(s?.EmissiveColor ?? s?.Diffuse) * (s?.Emissive ?? 0f),
        });

        // Same mirror the detail panels and the renderer use.
        var subA = preset?.SubRowA ?? (mirrorUnsetSubRows ? preset?.SubRowB : null);
        var subB = preset?.SubRowB ?? (mirrorUnsetSubRows ? preset?.SubRowA : null);

        for (int c = 0; c < 3; c++)
        {
            float cx0 = x0 + c * colW, cx1 = cx0 + colW - 1f;
            foreach (var (sub, ry0, ry1, half) in new[]
                     {
                         (subA, y0, midY, dimA),
                         (subB, midY, y1, dimB),
                     })
            {
                var v = Swatch(sub, c, half);
                uint col = ImGui.GetColorU32(new Vector4(v.X, v.Y, v.Z, 1f));
                draw.AddRectFilled(new Vector2(cx0, ry0), new Vector2(cx1, ry1), col);

                // A light-sensitive glow shows its dark-light colour, marked with a notch in the glow swatch's top-right corner.
                if (c == 2 && (sub?.LightResponse ?? 0f) > 0f)
                {
                    float n = MathF.Min(ProteusStyle.S(4f), MathF.Min(cx1 - cx0, ry1 - ry0));
                    draw.AddTriangleFilled(
                        new Vector2(cx1 - n, ry0), new Vector2(cx1, ry0), new Vector2(cx1, ry0 + n),
                        ImGui.GetColorU32(ImGuiCol.Text));
                }
            }
        }

        draw.AddRect(new Vector2(x0, y0), new Vector2(x1, y1), ImGui.GetColorU32(ImGuiCol.Border));
    }

    private static void DrawSubRow(
        string id, List<ColorTableRowPreset> rows, int row, bool isA, RenderMode mode,
        IReadOnlyList<string>? shellMaterialLeaves,
        IReadOnlyList<Proteus.Interop.SkinGlowTarget>? skinGlowTargets,
        bool authoredPhysical,
        IReadOnlyList<(float Roughness, float Metalness)>? physicalBaseline,
        ref FeatureEdit edited, ref bool changed,
        bool mirrorUnsetSubRows = false,
        bool lightResponseApplies = true)
    {
        new SubRowPanel(id, rows, row, isA, mode, shellMaterialLeaves, skinGlowTargets, authoredPhysical, physicalBaseline, mirrorUnsetSubRows, lightResponseApplies).Run(ref edited, ref changed);
    }

    // ── copy / paste clipboard ────────────────────────────────────────────────
    // Static so it persists across options and windows for the session. Both hold deep copies.
    private static ColorTableSubRowPreset? _subClip;
    private static ColorTableRowPreset? _rowClip;

    /// <summary>Deep copy of a sub-row; delegates so a newly added field is never missed.</summary>
    private static ColorTableSubRowPreset Clone(ColorTableSubRowPreset s) => s.Clone();

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
