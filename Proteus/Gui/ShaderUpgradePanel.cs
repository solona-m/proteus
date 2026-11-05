using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The Gear shaders section: the switch for drawing old gear with Dawntrail's shader, and what is worn upgraded or left.
/// A view only: <see cref="ShaderUpgradeWatcher"/> does the work, with this window shut or open.
/// </summary>
internal sealed class ShaderUpgradePanel(ShaderUpgradeWatcher watcher, Configuration config)
{
    public void Draw()
    {
        var s = Strings.Settings;
        var on = config.AutoUpgradeLegacyShaders;
        if (ImGui.Checkbox(s.GearShadersAutoUpgrade, ref on))
        {
            // The watcher sees the change on its next frame: on upgrades what is worn, off takes it down and reloads.
            lock (config.Gate) config.AutoUpgradeLegacyShaders = on;
            config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.GearShadersAutoUpgradeTip);
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(s.GearShadersAutoUpgradeTip);

        var view = watcher.Current;
        if (StatusText(view) is not { } status) return;
        ImGui.TextDisabled(status);
        if (LeftTooltip(view) is { } tip && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
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

    internal static string ReasonText(string reason)
    {
        var s = Strings.Settings;
        return reason switch
        {
            "old-table" => s.GearShadersReasonOldTable,
            "unsupported-form" => s.GearShadersReasonUnsupportedForm,
            "unreadable" => s.GearShadersReasonUnreadable,
            "outranked" => s.GearShadersReasonOutranked,
            "unresolved" => s.GearShadersReasonUnresolved,
            "compositor" => s.GearShadersReasonCompositor,
            _ => s.GearShadersReasonWriteFailed,
        };
    }
}
