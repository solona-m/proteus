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

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-1")]
    public void Earless_label_has_a_stable_id()
    {
        Assert.EndsWith("###hairEarless", Strings.HairPort.Earless);
        Assert.Contains("every collection", Strings.HairPort.EarlessTip);
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-9")]
    public void Worn_text_names_the_earless_hairstyle_mod_and_reach()
    {
        var s = Strings.HairPort;
        string worn = HairPortPlan.HairPath(801, 130), root = System.IO.Path.Combine("mods", "Miqo Hair");
        Assert.Equal(string.Format(s.EarlessFmt, "h0130", "Miqo Hair"),
                     HairPortSettingsPanel.WornText(new HairPortWatcher.View(worn, root, Earless: true)));
        Assert.Contains("every collection", HairPortSettingsPanel.WornText(new HairPortWatcher.View(worn, root, Earless: true)));
        Assert.Equal(string.Format(s.PortedFmt, "h0130", "Miqo Hair"),
                     HairPortSettingsPanel.WornText(new HairPortWatcher.View(worn, root, Ported: true)));
        Assert.Equal(s.None, HairPortSettingsPanel.WornText(new HairPortWatcher.View(worn)));
        Assert.Equal(s.None, HairPortSettingsPanel.WornText(new HairPortWatcher.View()));
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-9")]
    [Trait("Plan", "EDGE-earless-miqote-hair-25")]
    public void Hint_text_names_the_mods_earless_options()
    {
        Assert.Null(HairPortSettingsPanel.HintText(new HairPortWatcher.View()));
        Assert.Null(HairPortSettingsPanel.HintText(new HairPortWatcher.View(EarlessOptions: [])));
        Assert.Equal(string.Format(Strings.HairPort.EarlessOptionHintFmt, "Races / Miqote Earless, Extras / earless"),
                     HairPortSettingsPanel.HintText(new HairPortWatcher.View(EarlessOptions: ["Races / Miqote Earless", "Extras / earless"])));
    }
}
