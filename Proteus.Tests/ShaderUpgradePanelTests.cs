using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The Gear shaders section's status line and tooltip (<see cref="ShaderUpgradePanel"/>).</summary>
public class ShaderUpgradePanelTests
{
    private static ShaderUpgradeWatcher.UpgradedEntry Up(string g)
        => new(g, "Dress/a.mtrl", "1-a.mtrl", "index-mask", null, null, 0, 0, true);

    private static ShaderUpgradeWatcher.LeftEntry Left(string reason) => new("p", "Dress/a.mtrl", reason);

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-8")]
    public void Status_counts_upgraded_and_left()
    {
        var view = new ShaderUpgradeWatcher.View(Enabled: true, Upgraded: [Up("a"), Up("b"), Up("c")],
                                                 Left: [Left("old-table"), Left("old-table"), Left("unsupported-form")]);
        Assert.Equal(string.Format(Strings.Settings.GearShadersStatusFmt, 3, 3), ShaderUpgradePanel.StatusText(view));
        var tip = ShaderUpgradePanel.LeftTooltip(view)!;
        Assert.Contains("2 × " + Strings.Settings.GearShadersReasonOldTable, tip);
        Assert.Contains("1 × " + Strings.Settings.GearShadersReasonUnsupportedForm, tip);
        Assert.Null(ShaderUpgradePanel.LeftTooltip(view with { Left = [] }));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-8")]
    [Trait("Plan", "EDGE-dt-shader-upgrade-14")]
    public void Status_says_next_redraw_or_waiting()
    {
        var on = new ShaderUpgradeWatcher.View(Enabled: true, Upgraded: [Up("a")]);
        Assert.Contains(Strings.Settings.GearShadersNextRedraw, ShaderUpgradePanel.StatusText(on with { AwaitingRedraw = true }));
        Assert.DoesNotContain(Strings.Settings.GearShadersNextRedraw, ShaderUpgradePanel.StatusText(on));
        Assert.Equal(Strings.Settings.GearShadersWaiting, ShaderUpgradePanel.StatusText(on with { Waiting = "managed-mod" }));
        Assert.Null(ShaderUpgradePanel.StatusText(on with { Enabled = false }));
    }
}
