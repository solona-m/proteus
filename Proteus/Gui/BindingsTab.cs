using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Proteus.Interop;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

internal sealed class BindingsTab
{
    private readonly Configuration config;
    private readonly DesignBindingService designBindings;
    private readonly PenumbraBridge penumbra;

    public BindingsTab(Configuration config, DesignBindingService designBindings, PenumbraBridge penumbra)
    {
        this.config = config;
        this.designBindings = designBindings;
        this.penumbra = penumbra;
    }

    // Column sort (display only); defaults to newest-first, the order DesignBindingService.Bindings returns.
    private enum BindingSort { Design, Captured }
    private BindingSort _bindingSort = BindingSort.Captured;
    private bool _bindingSortDesc = true;
    // Search box, kept across tab switches: a tab body has no appearing event, and the no-match line quotes a stale filter back.
    private string _bindingFilter = "";
    // Width the table last occupied, held in the no-match state so the auto-resizing window doesn't snap narrow while typing.
    private float _bindingsTableWidth;

    // Enough to recognise a look without the tooltip outgrowing the screen.
    private const int BindingModsTooltipRows = 30;

    internal void DrawBindingsTab()
    {
        var bs = Strings.Bindings;
        DrawSettings(bs);

        var bindings = designBindings.Bindings;
        var activeId = designBindings.ActiveDesignId;
        if (activeId.HasValue)
        {
            var act = bindings.FirstOrDefault(b => b.DesignId == activeId.Value);
            ImGui.TextDisabled(string.Format(bs.ActiveFmt, act?.DesignName ?? activeId.Value.ToString()[..8]));
        }

        if (bindings.Count == 0)
        {
            ImGui.TextDisabled(bs.NoBindings);
            return;
        }

        var visible = DrawFilter(bindings);
        if (visible.Count == 0) return;

        // EndTable is only legal when BeginTable returned true; returning also skips the deferred dispatch, as no row was drawn.
        if (!BeginTable()) return;
        var (toApply, toRemove, toUpdate) = DrawRows(Sorted(visible, activeId), activeId, bs);
        ImGui.EndTable();
        _bindingsTableWidth = ImGui.GetItemRectSize().X;

        // All deferred past the loop: each mutates the binding state the rows are drawn from.
        if (toUpdate)
            designBindings.UpdateActiveBindingFromCurrentState();
        if (toApply.HasValue)
            designBindings.Restore(toApply.Value);
        if (toRemove.HasValue)
            designBindings.RemoveBinding(toRemove.Value);
    }

    /// <summary>The feature toggle and its three options.</summary>
    private void DrawSettings(BindingsStrings bs)
    {
        bool bindEnabled = config.DesignBindingEnabled;
        if (ImGui.Checkbox(bs.Enable, ref bindEnabled))
        {
            config.DesignBindingEnabled = bindEnabled;
            config.Save();
            // Turning the feature off drops any active override immediately.
            if (!bindEnabled)
                designBindings.ClearColorOverride();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(bs.EnableTip);

        ImGui.Indent();
        using (ImRaii.Disabled(!bindEnabled))
        {
            bool followAutomation = config.DesignBindingFollowsAutomation;
            if (ImGui.Checkbox(bs.FollowAutomation, ref followAutomation))
            {
                config.DesignBindingFollowsAutomation = followAutomation;
                config.Save();
                // No ClearColorOverride: this path only ever restores a binding.
            }
            // AllowWhenDisabled, so the tooltip is reachable while the parent toggle greys this out.
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(bs.FollowAutomationTip);

            bool restoreCharacter = config.DesignBindingRestoresCharacterMods;
            if (ImGui.Checkbox(bs.RestoreCharacter, ref restoreCharacter))
            {
                config.DesignBindingRestoresCharacterMods = restoreCharacter;
                config.Save();
                designBindings.OnRestoreCharacterModsToggled(restoreCharacter);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(bs.RestoreCharacterTip);

            bool unequipUnset = config.DesignBindingUnequipUnsetSlots;
            if (ImGui.Checkbox(bs.UnequipUnset, ref unequipUnset))
            {
                config.DesignBindingUnequipUnsetSlots = unequipUnset;
                config.Save();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(bs.UnequipUnsetTip);
        }
        ImGui.Unindent();
    }

    /// <summary>The search box, and the no-match line when it hides everything. Returns the bindings that pass.</summary>
    private IReadOnlyList<DesignBinding> DrawFilter(IReadOnlyList<DesignBinding> bindings)
    {
        // Below the empty guard and the "Active:" line, at a fixed scaled width.
        ImGui.SetNextItemWidth(ProteusStyle.S(240f));
        ImGui.InputTextWithHint("##bindingFilter", Strings.Export.FilterHint, ref _bindingFilter, 64);

        // An empty filter uses the original list.
        IReadOnlyList<DesignBinding> visible =
            _bindingFilter.Length == 0 ? bindings : bindings.Where(MatchesBindingFilter).ToList();
        if (visible.Count == 0)
        {
            ProteusStyle.DisabledWrapped(string.Format(Strings.Export.NoMatchFmt, _bindingFilter));
            StatusWindow.HoldWindowWidth(_bindingsTableWidth);
        }
        return visible;
    }

    /// <summary>The search box's test: the design name, or the id (in full or in part) shown when the name is missing.</summary>
    private bool MatchesBindingFilter(DesignBinding b)
        => _bindingFilter.Length == 0
        || b.DesignName?.Contains(_bindingFilter, StringComparison.OrdinalIgnoreCase) == true
        || b.DesignId.ToString().Contains(_bindingFilter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens the table and draws its sortable header row. False when ImGui did not open it.</summary>
    private bool BeginTable()
    {
        if (!ImGui.BeginTable("##bindings", 3,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
            return false;
        // Column ids, not display text, so they stay English.
        ImGui.TableSetupColumn("Design",   ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Captured", ImGuiTableColumnFlags.WidthFixed, ProteusStyle.S(112f));
        // Wide enough for three translated icon+text buttons.
        ImGui.TableSetupColumn("##act",    ImGuiTableColumnFlags.WidthFixed, ProteusStyle.S(300f));

        // Clickable sort headers: the name defaults to ascending, dates to descending.
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ProteusStyle.SortableHeader(Strings.Bindings.ColDesign, "bindDesign",
            BindingSort.Design, ref _bindingSort, ref _bindingSortDesc, defaultDesc: false);
        ProteusStyle.SortableHeader(Strings.Bindings.ColCaptured, "bindCaptured",
            BindingSort.Captured, ref _bindingSort, ref _bindingSortDesc, defaultDesc: true);
        ImGui.TableNextColumn(); ImGui.TableHeader("##act");
        return true;
    }

    /// <summary>A sorted copy, tie-broken by label so equal timestamps keep a stable order, with the active binding first.</summary>
    private List<DesignBinding> Sorted(IReadOnlyList<DesignBinding> visible, Guid? activeId)
    {
        static string Label(DesignBinding x) => x.DesignName ?? x.DesignId.ToString();
        var ordered = _bindingSort switch
        {
            BindingSort.Design => _bindingSortDesc
                ? visible.OrderByDescending(Label, StringComparer.OrdinalIgnoreCase)
                : visible.OrderBy(Label, StringComparer.OrdinalIgnoreCase),
            _ => _bindingSortDesc
                ? visible.OrderByDescending(x => x.CapturedUtc)
                : visible.OrderBy(x => x.CapturedUtc),
        };
        var shown = ordered.ThenBy(Label, StringComparer.OrdinalIgnoreCase).ToList();

        // The active binding always leads, whatever the sort: it is the only one Update can write to.
        if (activeId is { } active && shown.FindIndex(x => x.DesignId == active) is > 0 and var at)
        {
            var lead = shown[at];
            shown.RemoveAt(at);
            shown.Insert(0, lead);
        }
        return shown;
    }

    /// <summary>One row per binding. Returns the button clicked, for the caller to act on after the table closes.</summary>
    private (Guid? Apply, Guid? Remove, bool Update) DrawRows(List<DesignBinding> shown, Guid? activeId, BindingsStrings bs)
    {
        Guid? toApply = null, toRemove = null;
        bool toUpdate = false;
        foreach (var b in shown)
        {
            ImGui.TableNextRow();

            bool isActive = activeId == b.DesignId;
            if (isActive)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                    ImGui.GetColorU32(ProteusStyle.Binding with { W = 0.20f }));

            ImGui.TableNextColumn();
            var label = b.DesignName ?? b.DesignId.ToString()[..8];
            if (isActive)
            {
                ProteusStyle.Pill(bs.PillActive, ProteusStyle.Binding);
                ImGui.SameLine();
                ImGui.TextColored(ProteusStyle.Binding, label);
            }
            else
                ImGui.TextUnformatted(label);

            // How many mods Apply restores, or that an older binding is Proteus-only; only while whole-character restore is on.
            if (config.DesignBindingRestoresCharacterMods)
            {
                ImGui.SameLine();
                if (b.HasCharacterSnapshot)
                {
                    ImGui.TextDisabled(string.Format(bs.ModCountFmt, b.CharacterMods.Count));
                    if (ImGui.IsItemHovered())
                        DrawBindingModsTooltip(b);
                }
                else
                {
                    ImGui.TextDisabled(bs.ProteusOnly);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(bs.ProteusOnlyTip);
                }
            }

            ImGui.TableNextColumn();
            var ago = DateTime.UtcNow - b.CapturedUtc;
            ImGui.TextDisabled(
                ago.TotalSeconds  < 60 ? string.Format(bs.SecondsAgoFmt, ago.TotalSeconds.ToString("F0"))
                : ago.TotalMinutes < 60 ? string.Format(bs.MinutesAgoFmt, ago.TotalMinutes.ToString("F0"))
                :                         string.Format(bs.HoursAgoFmt, ago.TotalHours.ToString("F0")));

            ImGui.TableNextColumn();
            // IconButtonWithText derives its id from the label, so scope each row's id or only the first row's buttons respond.
            using (ImRaii.PushId(b.DesignId.ToString()))
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.PlayCircle, bs.Apply))
                    toApply = b.DesignId;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(config.DesignBindingRestoresCharacterMods ? bs.ApplyCharacterTip : bs.ApplyTip);
                ImGui.SameLine();
                // Only the active binding can be re-captured, since the snapshot comes from live state. Disabled, not hidden, so Unbind keeps its position.
                using (ImRaii.Disabled(!isActive))
                {
                    if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, bs.Update))
                        toUpdate = true;
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(isActive ? bs.UpdateTip : bs.UpdateInactiveTip);
                ImGui.SameLine();
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Unlink, bs.Unbind))
                    toRemove = b.DesignId;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(bs.UnbindTip);
            }
        }
        return (toApply, toRemove, toUpdate);
    }

    /// <summary>The mods a binding restores, enabled ones first. Only drawn while hovered, which is also the
    /// only time it asks Penumbra which mods are still installed.</summary>
    private void DrawBindingModsTooltip(DesignBinding b)
    {
        var bs        = Strings.Bindings;
        var installed = penumbra.GetAllMods();

        using var tip = ImRaii.Tooltip();
        ImGui.TextUnformatted(bs.ModListHeader);

        var rows = b.CharacterMods
            .OrderByDescending(m => m.Enabled)
            .ThenByDescending(m => m.Priority)
            .ThenBy(m => m.ModName ?? m.ModDirectory, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var m in rows.Take(BindingModsTooltipRows))
        {
            var name    = installed?.GetValueOrDefault(m.ModDirectory) ?? m.ModName ?? m.ModDirectory;
            var missing = installed != null && !installed.ContainsKey(m.ModDirectory);
            var detail  = missing   ? bs.ModListMissing
                        : m.Enabled ? string.Format(bs.ModListPriorityFmt, m.Priority)
                        :             bs.ModListOff;

            if (missing || !m.Enabled) ImGui.TextDisabled($"{name}  ({detail})");
            else                       ImGui.TextUnformatted($"{name}  ({detail})");
        }

        if (rows.Count > BindingModsTooltipRows)
            ImGui.TextDisabled(string.Format(bs.ModListMoreFmt, rows.Count - BindingModsTooltipRows));
    }
}
