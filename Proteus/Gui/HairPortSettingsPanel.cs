using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The Hair section: the switch for fitting hair mods made for another race, what Proteus did with the hairstyle being
/// worn, and what the last check of the player's hair mods fitted. A view only: <see cref="HairPortWatcher"/> does the
/// work, with this window shut or open.
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
            // On acts on what is worn now and looks through every hair mod. Off leaves every port where it is.
            if (auto)
            {
                watcher.Refresh(force: true);
                watcher.CheckNow();
            }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.AutoTip);
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(s.AutoTip);

        ImGui.Spacing();
        var view = watcher.Current;
        var scan = watcher.Scan;
        if (view.Busy && !scan.Running) ImGui.TextDisabled(s.Working);
        else if (view.Ported && view.ModRoot != null && view.Worn != null)
        {
            ImGui.TextWrapped(string.Format(s.PortedFmt, HairPortPlan.Parse(view.Worn) is { } at ? $"h{at.Id:D4}" : "?",
                                            System.IO.Path.GetFileName(view.ModRoot)));
            using (ImRaii.Disabled(view.Busy))
            {
                if (ImGui.Button(s.Undo)) watcher.Undo();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(s.UndoTip);
                if (view.PortsInMod > 1)
                {
                    ImGui.SameLine();
                    if (ImGui.Button(string.Format(s.UndoAllFmt, view.PortsInMod))) watcher.UndoAll();
                }
            }
        }
        else ImGui.TextWrapped(s.None);

        if (view.Message.Length > 0)
        {
            ImGui.Spacing();
            if (view.Failed) ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), view.Message);
            else ImGui.TextWrapped(view.Message);
        }

        if (config.AutoHairPort) DrawCheck(scan, view.Busy);
    }

    /// <summary>The check of every hair mod: how far it has got or what it found, and each hairstyle it fitted.</summary>
    private void DrawCheck(HairPortWatcher.ScanView scan, bool busy)
    {
        var s = Strings.HairPort;
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextWrapped(StatusText(scan));
        if (ShowsCancel(scan))
        {
            if (ImGui.Button(s.Cancel)) watcher.CancelCheck();
        }
        else
        {
            using (ImRaii.Disabled(busy))
                if (ImGui.Button(s.CheckNow)) watcher.CheckNow();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(s.CheckNowTip);
        }
        if (scan.Message.Length > 0) ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), scan.Message);
        if (scan.List.Count == 0) return;

        ImGui.TextDisabled(s.WearHint);
        if (!ImGui.CollapsingHeader(string.Format(s.ListFmt, scan.List.Count) + "###hairPortList")) return;
        bool canUndoAny = false;
        foreach (var row in scan.List)
        {
            ImGui.PushID($"{row.ModDir}|{row.Id}");
            bool can = CanUndo(row, busy || scan.Running);
            canUndoAny |= can && row.State is HairPortScan.RowState.Fitted or HairPortScan.RowState.Remade;
            if (row.State == HairPortScan.RowState.Refused) ImGui.TextColored(new Vector4(1f, 0.7f, 0.4f, 1f), RowText(row));
            else ImGui.TextWrapped(RowText(row));
            if (row.State is not (HairPortScan.RowState.Refused or HairPortScan.RowState.Undone))
            {
                ImGui.SameLine();
                using (ImRaii.Disabled(!can))
                    if (ImGui.SmallButton(s.Undo)) watcher.UndoRow(row);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(s.UndoTip);
            }
            ImGui.PopID();
        }
        if (canUndoAny && ImGui.Button(s.UndoChecked)) watcher.UndoChecked();
    }

    /// <summary>The check's one line: how far it has got, or what the last one found, or that none has run.</summary>
    internal static string StatusText(HairPortWatcher.ScanView scan)
    {
        var s = Strings.HairPort;
        if (scan.Running) return scan.Total > 0 ? string.Format(s.CheckingFmt, scan.Done, scan.Total) : s.CheckingStart;
        if (scan.Race == 0) return s.NoCheck;
        int fitted = 0;
        foreach (var r in scan.List)
            if (r.State is HairPortScan.RowState.Fitted or HairPortScan.RowState.Remade or HairPortScan.RowState.AlreadyFitted)
                fitted++;
        return string.Format(s.CheckedFmt, fitted, RaceName(scan.Race));
    }

    /// <summary>Whether Cancel stands in for "Check now": only while a check runs.</summary>
    internal static bool ShowsCancel(HairPortWatcher.ScanView scan) => scan.Running;

    /// <summary>One hairstyle's line: its number, its mod, and what became of it.</summary>
    internal static string RowText(HairPortScan.Row row)
    {
        var s = Strings.HairPort;
        string state = row.State switch
        {
            HairPortScan.RowState.Fitted => s.StateFitted,
            HairPortScan.RowState.AlreadyFitted => s.StateAlready,
            HairPortScan.RowState.Remade => s.StateRemade,
            HairPortScan.RowState.Undone => s.StateUndone,
            _ => row.Message.Length > 0 ? string.Format(s.StateRefusedFmt, row.Message) : s.StateRefused,
        };
        return string.Format(s.RowFmt, row.Id, row.ModName, state);
    }

    /// <summary>Whether a row's port can be undone: one there is, while nothing else runs.</summary>
    internal static bool CanUndo(HairPortScan.Row row, bool busy)
        => !busy && row.State is HairPortScan.RowState.Fitted or HairPortScan.RowState.AlreadyFitted
                                 or HairPortScan.RowState.Remade;

    /// <summary>"Midlander ♀" for c0201.</summary>
    internal static string RaceName(ushort race)
    {
        int n = race / 100;
        if (n is < 1 or > 18) return $"c{race:D4}";
        return $"{ModelRace.Names[(n - 1) / 2]} {(n % 2 == 1 ? "♂" : "♀")}";
    }
}
