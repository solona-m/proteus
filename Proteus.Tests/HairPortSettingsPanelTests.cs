using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>What Settings &gt; Hair says about a check of the player's hair mods, through the panel's text helpers.</summary>
public class HairPortSettingsPanelTests
{
    private static HairPortScan.Row Row(HairPortScan.RowState state, string message = "")
        => new(7, "root", "Floof", "Genesis Floof", HairPortPlan.HairPath(201, 7), state, message);

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-5")]
    public void Status_shows_progress_and_cancel_while_running()
    {
        var running = new HairPortWatcher.ScanView(Running: true, Done: 12, Total: 140, Race: 201);
        var text = HairPortSettingsPanel.StatusText(running);
        Assert.Contains("12", text);
        Assert.Contains("140", text);
        Assert.True(HairPortSettingsPanel.ShowsCancel(running));

        var done = running with { Running = false, Rows = [Row(HairPortScan.RowState.Fitted), Row(HairPortScan.RowState.Refused, "x")] };
        Assert.False(HairPortSettingsPanel.ShowsCancel(done));
        Assert.Equal(string.Format(Strings.HairPort.CheckedFmt, 1, "Midlander ♀"), HairPortSettingsPanel.StatusText(done));
        Assert.Equal(Strings.HairPort.NoCheck, HairPortSettingsPanel.StatusText(new HairPortWatcher.ScanView()));
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-7")]
    public void Rows_show_number_mod_state_and_undo()
    {
        var s = Strings.HairPort;
        foreach (var (state, text) in new[]
                 {
                     (HairPortScan.RowState.Fitted, s.StateFitted), (HairPortScan.RowState.AlreadyFitted, s.StateAlready),
                     (HairPortScan.RowState.Remade, s.StateRemade), (HairPortScan.RowState.Undone, s.StateUndone),
                 })
        {
            var line = HairPortSettingsPanel.RowText(Row(state));
            Assert.Contains("7", line);
            Assert.Contains("Genesis Floof", line);
            Assert.EndsWith(text, line);
        }
        Assert.Contains("too big", HairPortSettingsPanel.RowText(Row(HairPortScan.RowState.Refused, "too big")));

        Assert.True(HairPortSettingsPanel.CanUndo(Row(HairPortScan.RowState.Fitted), busy: false));
        Assert.True(HairPortSettingsPanel.CanUndo(Row(HairPortScan.RowState.AlreadyFitted), busy: false));
        Assert.True(HairPortSettingsPanel.CanUndo(Row(HairPortScan.RowState.Remade), busy: false));
        Assert.False(HairPortSettingsPanel.CanUndo(Row(HairPortScan.RowState.Fitted), busy: true));
        Assert.False(HairPortSettingsPanel.CanUndo(Row(HairPortScan.RowState.Refused), busy: false));
        Assert.False(HairPortSettingsPanel.CanUndo(Row(HairPortScan.RowState.Undone), busy: false));
    }
}
