using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The Gear variants section: the switch for pointing every variant of worn gear at its mod's material, what is worn
/// repointed or left, and the two buttons. A view only: <see cref="VariantRepointWatcher"/> does the work, with this
/// window shut or open.
/// </summary>
internal sealed class VariantRepointPanel(VariantRepointWatcher watcher, Configuration config)
{
    public void Draw()
    {
        var s = Strings.Settings;
        var on = config.AutoRepointVariants;
        if (ImGui.Checkbox(s.GearVariantsAuto, ref on))
        {
            // The watcher sees the change on its next frame: on looks at what is worn, off only stops.
            lock (config.Gate) config.AutoRepointVariants = on;
            config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.GearVariantsAutoTip);
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(s.GearVariantsAutoTip);

        var view = watcher.Current;
        if (StatusText(view) is { } status)
        {
            ImGui.TextDisabled(status);
            if (StatusTooltip(view) is { } tip && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        }

        using (ImRaii.Disabled(view.Busy))
        {
            if (ImGui.Button(s.GearVariantsRepointNow)) watcher.RepointWornNow();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(view.Busy ? s.GearVariantsBusy : s.GearVariantsRepointNowTip);
            ImGui.SameLine();
            if (ImGui.Button(s.GearVariantsUndoWorn)) watcher.UndoWorn();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(view.Busy ? s.GearVariantsBusy : s.GearVariantsUndoWornTip);
        }
        if (ResultText(view) is { } result) ImGui.TextWrapped(result);
    }

    /// <summary>The status line under the switch, or null when there is nothing to say (off).</summary>
    internal static string? StatusText(VariantRepointWatcher.View view)
    {
        var s = Strings.Settings;
        if (!view.Enabled) return null;
        if (view.Waiting != "none") return s.GearVariantsWaiting;
        var counts = string.Format(s.GearVariantsStatusFmt, view.RepointedList.Count, view.LeftList.Count);
        return view.AwaitingRedraw ? counts + "\n" + s.GearVariantsNextRedraw : counts;
    }

    /// <summary>The repointed pieces by mod, then the reasons pieces were left, one per line with a count; null when neither.</summary>
    internal static string? StatusTooltip(VariantRepointWatcher.View view)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (view.RepointedList.Count > 0)
            parts.Add(string.Format(Strings.Settings.GearVariantsRepointedTipFmt,
                string.Join("\n", view.RepointedList.Select(r => $"{r.ModDir}: {r.Item} → v{r.MaterialId:D4}"))));
        if (view.LeftList.Count > 0)
        {
            var lines = view.LeftList.GroupBy(l => l.Reason)
                                     .OrderByDescending(g => g.Count())
                                     .ThenBy(g => g.Key, System.StringComparer.Ordinal)
                                     .Select(g => $"{g.Count()} × {ReasonText(g.Key)}");
            parts.Add(string.Format(Strings.Settings.GearVariantsLeftTipFmt, string.Join("\n", lines)));
        }
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>What the last "Undo for what I'm wearing" did; null after any other run.</summary>
    internal static string? ResultText(VariantRepointWatcher.View view)
    {
        if (view.LastRun is not { Kind: "undo-worn" } r) return null;
        if (r.NothingRecorded) return Strings.Settings.GearVariantsNothingRecorded;
        var line = string.Format(Strings.Settings.GearVariantsResultFmt, r.Removed, r.Kept, r.Refused);
        return r.Message.Length > 0 ? line + "\n" + r.Message : line;
    }

    internal static string ReasonText(string reason)
    {
        var s = Strings.Settings;
        return reason switch
        {
            "several-variants" => s.GearVariantsReasonSeveralVariants,
            "mod-imc" => s.GearVariantsReasonModImc,
            "imc-group" => s.GearVariantsReasonImcGroup,
            "proteus-toggles" => s.GearVariantsReasonProteusToggles,
            "other-mod-material" => s.GearVariantsReasonOtherModMaterial,
            "folder-unused" => s.GearVariantsReasonFolderUnused,
            "no-imc" => s.GearVariantsReasonNoImc,
            "meta-unreadable" => s.GearVariantsReasonMetaUnreadable,
            "outranked" => s.GearVariantsReasonOutranked,
            "legacy-mod" => s.GearVariantsReasonLegacyMod,
            "proteus-pack" => s.GearVariantsReasonProteusPack,
            "declined" => s.GearVariantsReasonDeclined,
            _ => s.GearVariantsReasonWriteFailed,
        };
    }
}
