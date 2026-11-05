using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The Gear shaders section: the switch for moving old gear to Dawntrail's shader inside its mod, what is worn upgraded
/// or left, and the two buttons. A view only: <see cref="ShaderUpgradeWatcher"/> does the work, with this window shut
/// or open.
/// </summary>
internal sealed class ShaderUpgradePanel(ShaderUpgradeWatcher watcher, Configuration config)
{
    public void Draw()
    {
        var s = Strings.Settings;
        var on = config.AutoUpgradeLegacyShaders;
        if (ImGui.Checkbox(s.GearShadersAutoUpgrade, ref on))
        {
            // The watcher sees the change on its next frame: on upgrades what is worn, off only stops.
            lock (config.Gate) config.AutoUpgradeLegacyShaders = on;
            config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.GearShadersAutoUpgradeTip);
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(s.GearShadersAutoUpgradeTip);

        var view = watcher.Current;
        if (StatusText(view) is { } status)
        {
            ImGui.TextDisabled(status);
            if (LeftTooltip(view) is { } tip && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        }

        using (ImRaii.Disabled(view.Busy))
        {
            if (ImGui.Button(s.GearShadersUpgradeNow)) watcher.UpgradeWornNow();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(view.Busy ? s.GearShadersBusy : s.GearShadersUpgradeNowTip);
            ImGui.SameLine();
            if (ImGui.Button(s.GearShadersUndoWorn)) watcher.UndoWorn();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(view.Busy ? s.GearShadersBusy : s.GearShadersUndoWornTip);
        }
        if (ResultText(view) is { } result) ImGui.TextWrapped(result);
    }

    /// <summary>The status line under the switch, or null when there is nothing to say (off).</summary>
    internal static string? StatusText(ShaderUpgradeWatcher.View view)
    {
        var s = Strings.Settings;
        if (!view.Enabled) return null;
        if (view.Waiting != "none") return s.GearShadersWaiting;
        var counts = string.Format(s.GearShadersStatusFmt, view.UpgradedList.Count, view.LeftList.Count);
        return view.AwaitingRedraw ? counts + "\n" + s.GearShadersNextRedraw : counts;
    }

    /// <summary>The reasons materials were left on the old shader, one per line with a count; null when none were.</summary>
    internal static string? LeftTooltip(ShaderUpgradeWatcher.View view)
    {
        if (view.LeftList.Count == 0) return null;
        var lines = view.LeftList.GroupBy(l => l.Reason)
                                 .OrderByDescending(g => g.Count())
                                 .ThenBy(g => g.Key, System.StringComparer.Ordinal)
                                 .Select(g => $"{g.Count()} × {ReasonText(g.Key)}");
        return string.Format(Strings.Settings.GearShadersLeftTipFmt, string.Join("\n", lines));
    }

    /// <summary>What the last "Undo for what I'm wearing" did; null after any other run.</summary>
    internal static string? ResultText(ShaderUpgradeWatcher.View view)
    {
        if (view.LastRun is not { Kind: "undo-worn" } r) return null;
        var line = string.Format(Strings.Settings.GearShadersResultFmt, r.Restored, r.Kept, r.Refused);
        return r.Message.Length > 0 ? line + "\n" + r.Message : line;
    }

    internal static string ReasonText(string reason)
    {
        var s = Strings.Settings;
        return reason switch
        {
            "old-table" => s.GearShadersReasonOldTable,
            "unsupported-form" => s.GearShadersReasonUnsupportedForm,
            "unreadable" => s.GearShadersReasonUnreadable,
            "compositor" => s.GearShadersReasonCompositor,
            "legacy-mod" => s.GearShadersReasonLegacyMod,
            "mixed-specular" => s.GearShadersReasonMixedSpecular,
            "specular-from-other-mod" => s.GearShadersReasonSpecularFromOtherMod,
            "unsupported-placement" => s.GearShadersReasonUnsupportedPlacement,
            "proteus-pack" => s.GearShadersReasonProteusPack,
            "declined" => s.GearShadersReasonDeclined,
            _ => s.GearShadersReasonWriteFailed,
        };
    }
}
