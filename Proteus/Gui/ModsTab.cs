using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Proteus.Interop;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

internal sealed class ModsTab
{
    private readonly PenumbraBridge penumbra;
    private readonly CompositorService compositor;
    private readonly StatusWindow window;
    private readonly PresetService presets;
    private readonly DesignBindingService designBindings;
    private readonly Configuration config;
    private readonly ModPreviewService previews;

    public ModsTab(PenumbraBridge penumbra, CompositorService compositor, StatusWindow window, PresetService presets, DesignBindingService designBindings, Configuration config, ModPreviewService previews)
    {
        this.penumbra = penumbra;
        this.compositor = compositor;
        this.window = window;
        this.presets = presets;
        this.designBindings = designBindings;
        this.config = config;
        this.previews = previews;
    }

    // The list the preview cache was last validated against: a new list (a composite, an import) may carry new pictures.
    private IReadOnlyList<OverlayEntry>? _lastMods;

    // Key: modDir → priority value being dragged; committed to Penumbra on edit-end.
    private readonly Dictionary<string, int> _priorityEdits = new();

    // Mods-tab column sort (display only; the compositor orders by priority independently).
    private enum ModSort { Enabled, Name, Priority }
    private ModSort _modSort = ModSort.Priority;
    private bool _modSortDesc = true;   // Priority descending = default
    // Search box, kept across tab switches: a tab body has no appearing event, and the no-match line quotes a stale filter back.
    private string _modFilter = "";
    // Width the table last occupied, held in the no-match state so the auto-resizing window doesn't snap narrow while typing.
    private float _modsTableWidth;

    /// <summary>The Mods tab's search: name, folder or author. Metadata is null-conditional: an entry's sidecar may have failed to parse.</summary>
    private bool MatchesModFilter(OverlayEntry m)
        => StatusWindow.MatchesNameOrFolder(m, _modFilter)
        || m.Metadata?.Author?.Contains(_modFilter, StringComparison.OrdinalIgnoreCase) == true;

    internal void DrawModsTab()
    {
        // ── Overlay mod list ─────────────────────────────────────────────────
        // The list comes from the last composite, so while the plugin is off say so rather than claim there are no mods.
        var mods = compositor.LastDiscovered;
        if (!ReferenceEquals(mods, _lastMods))
        {
            _lastMods = mods;
            previews.Invalidate();
        }
        ImGui.Spacing();
        if (!config.PluginEnabled)
        {
            ImGui.TextColored(ProteusStyle.Warn, Strings.ModsList.Disabled);
        }
        else if (mods.Count == 0)
        {
            ImGui.TextDisabled(Strings.ModsList.NoMods);
        }
        else
        {
            // Fixed scaled width: a fill-width item in an auto-resizing window feeds the auto-fit loop.
            ImGui.SetNextItemWidth(ProteusStyle.S(240f));
            ImGui.InputTextWithHint("##modFilter", Strings.Export.FilterHint, ref _modFilter, 64);

            // Filter before sorting. Rows are keyed by ModDirectory, never index, so dropping rows can't misroute a click or drag.
            var visible = _modFilter.Length == 0 ? mods : mods.Where(MatchesModFilter).ToList();
            if (visible.Count == 0)
            {
                // Only reachable with something typed: say so rather than draw a header-only table.
                ProteusStyle.DisabledWrapped(string.Format(Strings.Export.NoMatchFmt, _modFilter));
                StatusWindow.HoldWindowWidth(_modsTableWidth);
                return;
            }

            // EndTable is only legal when BeginTable returned true.
            // "##mods3": ImGui keys column widths and sort state off the table id, so a column-count change needs a new id.
            if (!ImGui.BeginTable("##mods3", 5,
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
                return;
            // Scaled widths with headroom for translated headers. Column names are ids, never shown, and stay English so a
            // language change doesn't reset ImGui's per-column state.
            ImGui.TableSetupColumn("On",     ImGuiTableColumnFlags.WidthFixed, ProteusStyle.S(32f));
            ImGui.TableSetupColumn("Mod",    ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pri",    ImGuiTableColumnFlags.WidthFixed, ProteusStyle.S(78f));
            ImGui.TableSetupColumn("Preset", ImGuiTableColumnFlags.WidthFixed, ProteusStyle.S(120f));
            // Wider than the header needs: the button carries an icon ahead of the translated label.
            ImGui.TableSetupColumn("Colors", ImGuiTableColumnFlags.WidthFixed, ProteusStyle.S(100f));

            // Clickable sort headers: Name defaults to ascending, the others to descending.
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            var ms = Strings.Mods;
            ProteusStyle.SortableHeader(ms.ColOn,       "modOn",   ModSort.Enabled,  ref _modSort, ref _modSortDesc, defaultDesc: true);
            ProteusStyle.SortableHeader(ms.ColMod,      "modName", ModSort.Name,     ref _modSort, ref _modSortDesc, defaultDesc: false);
            ProteusStyle.SortableHeader(ms.ColPriority, "modPri",  ModSort.Priority, ref _modSort, ref _modSortDesc, defaultDesc: true);
            ImGui.TableNextColumn(); ImGui.TableHeader(Strings.Presets.ColumnHeader);
            ImGui.TableNextColumn(); ImGui.TableHeader(ms.ColColors);

            // Enable/priority controls write straight through to Penumbra (Proteus keeps no
            // override state of its own); both reflect the mod's live Penumbra values.
            var collId = penumbra.GetPlayerCollectionId();

            // Display-only sort of a copy, by committed priority so a row won't jump mid-drag. Active mods are pinned on top for
            // every column except "On", which honours its own direction.
            var byActive = visible.OrderByDescending(e => e.Enabled);
            IOrderedEnumerable<OverlayEntry> ordered = _modSort switch
            {
                ModSort.Enabled => _modSortDesc ? byActive : visible.OrderBy(e => e.Enabled),
                ModSort.Name    => _modSortDesc ? byActive.ThenByDescending(e => e.ModName, StringComparer.OrdinalIgnoreCase)
                                                : byActive.ThenBy(e => e.ModName, StringComparer.OrdinalIgnoreCase),
                _               => _modSortDesc ? byActive.ThenByDescending(e => e.Priority)
                                                : byActive.ThenBy(e => e.Priority),
            };
            var displayMods = ordered.ThenBy(e => e.ModName, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var entry in displayMods)
            {
                ImGui.TableNextRow();

                // Enable checkbox — toggles the mod in Penumbra.
                ImGui.TableNextColumn();
                bool active = entry.Enabled;
                if (ImGui.Checkbox($"##en_{entry.ModDirectory}", ref active) && collId.HasValue)
                {
                    penumbra.SetModEnabled(collId.Value, entry.ModDirectory, active);
                    // Live edit only; folding into the active binding happens via "Update binding".
                    compositor.TriggerRecomposite("penumbra-enable");
                }

                // Mod name, dimmed only when disabled.
                ImGui.TableNextColumn();

                // An amber "!" for an enabled mod that paints nothing. The tip is formatted once and shared by both tooltips.
                var inertTip = active && compositor.GetInertReason(entry.ModDirectory) is { } why
                    ? StatusWindow.DescribeInert(why)
                    : null;
                if (inertTip != null)
                {
                    ImGui.TextColored(ProteusStyle.Warn, "!");
                    ProteusStyle.ReasonTooltip(inertTip);
                    ImGui.SameLine(0f, ProteusStyle.S(4f));
                }

                using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled), !active))
                {
                    if (ImGui.Selectable($"{entry.ModName}##{entry.ModDirectory}"))
                    {
                        penumbra.OpenToMod(entry.ModDirectory);
                    }
                }
                // Repeated on the name, which is what people hover, under the mod's own picture when it ships one.
                if (ImGui.IsItemHovered())
                    DrawNameTooltip(entry, inertTip);

                // Priority (drag to edit, Ctrl+click to type) — writes to Penumbra on edit-end.
                ImGui.TableNextColumn();
                int pri = _priorityEdits.TryGetValue(entry.ModDirectory, out var pe) ? pe : entry.Priority;
                ImGui.SetNextItemWidth(ProteusStyle.S(55f));
                if (ImGui.DragInt($"##pri_{entry.ModDirectory}", ref pri, 0.1f))
                    _priorityEdits[entry.ModDirectory] = pri;
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    _priorityEdits.Remove(entry.ModDirectory);
                    if (collId.HasValue)
                    {
                        // Read live rather than trusting entry.Priority, which is a snapshot from the last
                        // composite: if Penumbra has moved on, a stale "from" would both misreport the change
                        // and silence the line for a drag that really did move the mod. One IPC hop, on a
                        // gesture, not a frame.
                        var before = penumbra.GetModSettings(collId.Value, entry.ModDirectory)?.Priority;
                        penumbra.SetModPriority(collId.Value, entry.ModDirectory, pri);
                        // Logged like every other priority write: "what set this mod to N?" must be answerable
                        // from dalamud.log alone, and this is the one that came from a drag. Unreadable settings
                        // still log, since a write we cannot verify is the more interesting case, not the less.
                        if (before != pri)
                            Plugin.Log.Information("[Proteus] priority set by hand: {0} {1} -> {2}",
                                entry.ModDirectory, before?.ToString() ?? "(unreadable)", pri);
                    }
                    // Live edit only (see enable toggle above); folded into the binding via the button.
                    window.RecompositeForOverlay(entry, "penumbra-priority");
                }

                // Preset: switch this mod's saved look without opening the colour editor.
                ImGui.TableNextColumn();
                DrawPresetCell(entry, collId);

                // Opens a window, not a popup, so it survives clicks into the game; tinted when a binding drives the colours.
                ImGui.TableNextColumn();
                bool bindingDriven = designBindings.IsOverrideActiveFor(entry.ModDirectory);
                // IconButtonWithText derives its id from the label, so scope each row's id or only the first button responds.
                using (ImRaii.PushId($"colors_{entry.ModDirectory}"))
                using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ProteusStyle.Binding with { W = 0.45f }), bindingDriven))
                {
                    if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Palette, ms.ColorsBtn))
                        window.ToggleColorWindow(entry.ModDirectory);
                }
                if (bindingDriven && ImGui.IsItemHovered())
                    ImGui.SetTooltip(ms.ColorsBindingDrivenTip);
            }

            ImGui.EndTable();
            // EndTable submits the table's rect as an item: the width the rows took.
            _modsTableWidth = ImGui.GetItemRectSize().X;
        }
    }

    /// <summary>
    /// The name's hover tooltip: the mod's picture, fitted into a fixed box, then the inert warning if there is one.
    /// No picture (or one still loading) falls back to the plain warning, or no tooltip at all.
    /// </summary>
    private void DrawNameTooltip(OverlayEntry entry, string? inertTip)
    {
        IDalamudTextureWrap? wrap = null;
        if (previews.ImagePathFor(entry.ModRoot) is { } path)
        {
            try { wrap = Plugin.TextureProvider.GetFromFileAbsolute(path).GetWrapOrDefault(); }
            catch { /* undecodable: no picture */ }
        }

        if (wrap == null)
        {
            if (inertTip != null) ImGui.SetTooltip(inertTip);
            return;
        }

        // Fit (not crop) into the box, never enlarged past the image's own size.
        var box   = ProteusStyle.S(320f, 240f);
        var scale = MathF.Min(1f, MathF.Min(box.X / wrap.Width, box.Y / wrap.Height));
        var size  = new Vector2(wrap.Width, wrap.Height) * scale;

        using var tip = ImRaii.Tooltip();
        ImGui.Image(wrap.Handle, size);
        if (inertTip != null)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + MathF.Max(size.X, ProteusStyle.S(240f)));
            ImGui.TextColored(ProteusStyle.Warn, inertTip);
            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>The Mods row's preset picker: the worn preset and a one-click switch; saving and sharing live in the colour editor.
    /// A mod with no presets shows a dash.</summary>
    private void DrawPresetCell(OverlayEntry entry, Guid? collId)
    {
        var ps  = Strings.Presets;
        var all = presets.ListFor(entry);
        if (all.Count == 0)
        {
            ImGui.TextDisabled(ps.ColumnNone);
            ProteusStyle.ReasonTooltip(ps.ColumnTip);
            return;
        }

        var appliedId = presets.AppliedIdFor(entry.ModDirectory);
        var current   = appliedId is { } id ? all.FirstOrDefault(p => p.Id == id) : null;
        var label     = current == null ? ps.NoPreset : current.Name;

        // Applied after the combo closes: both calls republish the overrides and kick a composite.
        Guid? toApply = null;
        bool  toClear = false;

        // Disabled without a collection: wearing a preset writes Penumbra options.
        using (ImRaii.Disabled(collId == null))
        {
            ImGui.SetNextItemWidth(ProteusStyle.S(112f));
            if (ImGui.BeginCombo($"##preset_{entry.ModDirectory}", ProteusStyle.Ellipsize(label, ProteusStyle.S(96f))))
            {
                if (ImGui.Selectable(ps.NoPreset, current == null)) toClear = true;
                ImGui.Separator();

                for (var i = 0; i < all.Count; i++)
                {
                    var p = all[i];
                    using var _ = ImRaii.PushId(i);
                    var name = (p.Source == PresetSource.Pack ? ps.PackMarker : string.Empty) + p.Name;
                    if (ImGui.Selectable(name, p.Id == appliedId)) toApply = p.Id;
                }
                ImGui.EndCombo();
            }
        }
        // ReasonTooltip allows hover while disabled.
        ProteusStyle.ReasonTooltip(collId == null ? ps.NoCollection : ps.ColumnTip);

        if (toClear) presets.ClearApplied(entry.ModDirectory);
        else if (toApply is { } pick && collId is { } c && presets.Get(entry, pick) is { } preset)
            presets.Apply(entry, c, preset);
    }
}
