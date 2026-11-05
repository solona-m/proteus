using System;
using System.Linq;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The Gear shaders section's status line, tooltip and result (<see cref="ShaderUpgradePanel"/>).</summary>
public class ShaderUpgradePanelTests
{
    private static ShaderUpgradeWatcher.UpgradedEntry Up(string g) => new(g, "Dress", "a.mtrl", "index-mask");
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
    public void Status_says_next_redraw_or_waiting()
    {
        var on = new ShaderUpgradeWatcher.View(Enabled: true, Upgraded: [Up("a")]);
        Assert.Contains(Strings.Settings.GearShadersNextRedraw, ShaderUpgradePanel.StatusText(on with { AwaitingRedraw = true }));
        Assert.DoesNotContain(Strings.Settings.GearShadersNextRedraw, ShaderUpgradePanel.StatusText(on));
        Assert.Equal(Strings.Settings.GearShadersWaiting, ShaderUpgradePanel.StatusText(on with { Waiting = "penumbra" }));
        Assert.Null(ShaderUpgradePanel.StatusText(on with { Enabled = false }));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-8")]
    public void Undo_worn_and_upgrade_now_results_are_formatted()
    {
        var undo = new ShaderUpgradeWatcher.RunInfo("undo-worn", 1, 3, 2, 1, 1, true, true, DateTime.UtcNow, "Undo the brush first.");
        var text = ShaderUpgradePanel.ResultText(new ShaderUpgradeWatcher.View(LastRun: undo))!;
        Assert.Contains(string.Format(Strings.Settings.GearShadersResultFmt, 2, 1, 1), text);
        Assert.Contains("Undo the brush first.", text);
        Assert.Null(ShaderUpgradePanel.ResultText(new ShaderUpgradeWatcher.View(LastRun: undo with { Kind = "upgrade-now" })));
        // No undo-all anywhere.
        Assert.DoesNotContain("all mods", Strings.Settings.GearShadersUndoWorn + Strings.Settings.GearShadersUndoWornTip,
                              StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-71")]
    public void Left_tooltip_names_every_reason()
    {
        string[] reasons = ["old-table", "unsupported-form", "unreadable", "compositor", "write-failed", "legacy-mod",
                            "mixed-specular", "specular-from-other-mod", "unsupported-placement", "proteus-pack", "declined"];
        var texts = reasons.Select(ShaderUpgradePanel.ReasonText).ToList();
        Assert.Equal(reasons.Length, texts.Distinct().Count());
        Assert.Equal(reasons.Order(), Enum.GetValues<LeftReason>().Select(ShaderUpgradeWatcher.ReasonName).Order());
    }
}
