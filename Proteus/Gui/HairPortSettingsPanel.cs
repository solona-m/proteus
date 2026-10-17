using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The Hair section: the switch for fitting hair mods made for another race, and what Proteus did with the hairstyle
/// being worn. A view only: <see cref="HairPortWatcher"/> does the work, with this window shut or open.
/// </summary>
internal sealed class HairPortSettingsPanel(HairPortWatcher watcher, Configuration config)
{
    public void Draw()
    {
        var s = Strings.HairPort;

        var auto = config.AutoHairPort;
        if (ImGui.Checkbox(s.Auto, ref auto))
        {
            config.AutoHairPort = auto;
            config.Save();
            // On acts on what is worn now, not at the next hairstyle change. Off leaves every port where it is.
            if (auto) watcher.Refresh(force: true);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.AutoTip);
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(s.AutoTip);

        ImGui.Spacing();
        var view = watcher.Current;
        if (view.Busy) { ImGui.TextDisabled(s.Working); return; }

        if (view.Ported && view.ModRoot != null && view.Worn != null)
        {
            ImGui.TextWrapped(string.Format(s.PortedFmt, HairPortPlan.Parse(view.Worn) is { } at ? $"h{at.Id:D4}" : "?",
                                            System.IO.Path.GetFileName(view.ModRoot)));
            if (ImGui.Button(s.Undo)) watcher.Undo();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.UndoTip);
            if (view.PortsInMod > 1)
            {
                ImGui.SameLine();
                if (ImGui.Button(string.Format(s.UndoAllFmt, view.PortsInMod))) watcher.UndoAll();
            }
        }
        else ImGui.TextWrapped(s.None);

        if (view.Message.Length > 0)
        {
            ImGui.Spacing();
            if (view.Failed) ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), view.Message);
            else ImGui.TextWrapped(view.Message);
        }
    }
}
