using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

internal sealed class ExportTab
{
    private readonly FileDialogManager fileDialog;
    private readonly Configuration config;
    private readonly CompositorService compositor;
    private readonly ModExportService modExport;

    public ExportTab(FileDialogManager fileDialog, Configuration config, CompositorService compositor, ModExportService modExport)
    {
        this.fileDialog = fileDialog;
        this.config = config;
        this.compositor = compositor;
        this.modExport = modExport;
    }

    // ── Export tab state ──
    // Selected mod by directory, not list index: discovery can reorder the list.
    private string _exportModDir = "";
    // Live only while the combo's popup is open; cleared each time it opens.
    private string _exportFilter = "";
    private string? _exportStatus;
    private bool _exportStatusOk;

    /// <summary>How far along an export is. Busy starts when the file browser opens, so a second click can't stack dialogs.
    /// Framework thread only; the pool task writes <see cref="_exportDone"/>.</summary>
    private enum ExportPhase { Idle, Choosing, Writing }
    private ExportPhase _exportPhase = ExportPhase.Idle;
    // Written by the pool task, read and cleared by DrawExportTab; no Penumbra IPC follows, so the tab itself can finish it.
    private volatile ModExportService.ExportResult? _exportDone;

    // ── Export tab ───────────────────────────────────────────────────────────

    internal void DrawExportTab()
    {
        // Adopt the pool task's result here: nothing is left to do that closing the window could strand.
        if (_exportDone is { } done)
        {
            _exportDone = null;
            _exportPhase = ExportPhase.Idle;
            _exportStatus = done.Message;
            _exportStatusOk = done.Ok;
        }

        var xs = Strings.Export;

        ImGui.TextWrapped(xs.Intro);
        ImGui.Separator();

        var mods = compositor.LastDiscovered;
        if (!config.PluginEnabled)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), Strings.ModsList.Disabled);
            return;
        }
        if (mods.Count == 0)
        {
            ImGui.TextDisabled(Strings.ModsList.NoMods);
            return;
        }

        // A stale directory falls back to the first mod.
        var selected = mods.FirstOrDefault(m =>
            string.Equals(m.ModDirectory, _exportModDir, StringComparison.OrdinalIgnoreCase)) ?? mods[0];
        _exportModDir = selected.ModDirectory;

        ImGui.SetNextItemWidth(360);
        // Explicit height cap: passing a width constraint disables BeginCombo's own row limit.
        var popupMaxH = ImGui.GetTextLineHeightWithSpacing() * 18 + ImGui.GetStyle().WindowPadding.Y * 2;
        ImGui.SetNextWindowSizeConstraints(new Vector2(360, 0), new Vector2(780, popupMaxH));
        if (ImGui.BeginCombo(xs.ModCombo, selected.ModName))
        {
            // Fresh, focused filter each open; SetKeyboardFocusHere targets the next item, so it sits right before it.
            bool appearing = ImGui.IsWindowAppearing();
            if (appearing) _exportFilter = "";
            ImGui.SetNextItemWidth(-1);
            if (appearing) ImGui.SetKeyboardFocusHere();
            ImGui.InputTextWithHint("##exportfilter", xs.FilterHint, ref _exportFilter, 64);
            ImGui.Separator();

            int shown = 0;
            foreach (var m in mods.OrderBy(m => m.ModName, StringComparer.OrdinalIgnoreCase))
            {
                if (!MatchesExportFilter(m)) continue;
                shown++;
                // ##dir: two mods can share a display name, and duplicate ids would misroute the click.
                if (ImGui.Selectable($"{m.ModName}##{m.ModDirectory}",
                        string.Equals(m.ModDirectory, _exportModDir, StringComparison.OrdinalIgnoreCase)))
                    _exportModDir = m.ModDirectory;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(m.Enabled
                        ? m.ModDirectory
                        : string.Format(xs.DisabledInPenumbraFmt, m.ModDirectory));
            }
            if (shown == 0)
                ImGui.TextDisabled(string.Format(xs.NoMatchFmt, _exportFilter));
            ImGui.EndCombo();
        }
        if (!selected.Enabled)
            ImGui.TextDisabled(xs.ModDisabledNote);

        ImGui.Spacing();
        var label = _exportPhase switch
        {
            ExportPhase.Choosing => xs.Choosing,
            ExportPhase.Writing  => xs.Exporting,
            _                    => xs.ExportBtn,
        };
        using (ImRaii.Disabled(_exportPhase != ExportPhase.Idle))
            if (ImGui.Button(label))
                BrowseForExport(selected);

        if (_exportStatus != null)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(
                _exportStatusOk ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(1f, 0.5f, 0.4f, 1f),
                _exportStatus);
            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>The export combo's filter: name or folder, nothing else.</summary>
    private bool MatchesExportFilter(OverlayEntry m)
        => StatusWindow.MatchesNameOrFolder(m, _exportFilter);

    /// <summary>Ask where to save, then zip on the pool. The dialog callback runs inline on the framework thread.</summary>
    private void BrowseForExport(OverlayEntry entry)
    {
        _exportStatus = null;
        // Claimed before the dialog opens so a second click finds the button disabled; released on cancel.
        _exportPhase = ExportPhase.Choosing;

        fileDialog.SaveFileDialog(
            Strings.Export.DialogTitle, Strings.Export.DialogFilter + "{.pmp}",
            ModExportService.SuggestedFileName(entry), ModExportService.Extension,
            (ok, path) =>
            {
                if (!ok || string.IsNullOrEmpty(path))
                {
                    _exportPhase = ExportPhase.Idle;   // cancelled — hand the button back
                    return;
                }

                // Remember the directory before the write; it is useful even if the write fails.
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !string.Equals(dir, config.LastExportDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    config.LastExportDirectory = dir;
                    config.Save();
                }

                _exportPhase = ExportPhase.Writing;
                Task.Run(() =>
                {
                    try { _exportDone = modExport.Export(entry, path); }
                    catch (Exception ex) { _exportDone = new ModExportService.ExportResult(false, $"Export failed: {ex.Message}"); }
                });
            },
            ExportStartDirectory());
    }

    /// <summary>Where the save dialog opens: last used, else the desktop, else null for the dialog's own choice.</summary>
    private string? ExportStartDirectory()
    {
        var last = config.LastExportDirectory;
        if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;

        // DesktopDirectory follows a OneDrive-redirected desktop; Desktop can come back empty, so it is the fallback.
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Desktop })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
        }
        return null;
    }
}
