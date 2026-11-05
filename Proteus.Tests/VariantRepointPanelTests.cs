using System;
using System.Linq;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The Gear variants section's status line, tooltip and result (<see cref="VariantRepointPanel"/>).</summary>
public class VariantRepointPanelTests
{
    private static readonly string[] Reasons =
    [
        "several-variants", "mod-imc", "other-mod-material", "no-imc", "meta-unreadable", "outranked", "legacy-mod",
        "proteus-pack", "declined", "write-failed",
    ];

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-3")]
    public void Tooltip_lists_each_reason_localized()
    {
        var texts = Reasons.Select(VariantRepointPanel.ReasonText).ToList();
        Assert.Equal(texts.Count, texts.Distinct().Count());
        Assert.All(Reasons, r => Assert.Equal(r, VariantRepointPlan.ReasonName(Enum.Parse<RepointReason>(
            string.Concat(r.Split('-').Select(w => char.ToUpperInvariant(w[0]) + w[1..]))))));
        var view = new VariantRepointWatcher.View(Enabled: true,
            Left: [new("A", "e6255 Body", "mod-imc"), new("B", "e6255 Legs", "mod-imc"), new("C", "a0053 Earrings", "outranked")]);
        var tip = VariantRepointPanel.StatusTooltip(view)!;
        Assert.Contains("2 × " + Strings.Settings.GearVariantsReasonModImc, tip);
        Assert.Contains("1 × " + Strings.Settings.GearVariantsReasonOutranked, tip);
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-4")]
    public void Status_hidden_when_off()
    {
        Assert.Null(VariantRepointPanel.StatusText(new VariantRepointWatcher.View(Enabled: false)));
        Assert.Equal(string.Format(Strings.Settings.GearVariantsStatusFmt, 0, 0),
                     VariantRepointPanel.StatusText(new VariantRepointWatcher.View(Enabled: true)));
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-8")]
    public void Status_tooltip_lists_repointed_mods()
    {
        var view = new VariantRepointWatcher.View(Enabled: true, Repointed: [new("moonfire lace", "e0317 Body", 1, [2, 3], [2, 3])]);
        Assert.Contains("moonfire lace: e0317 Body v0001 → v0002, v0003", VariantRepointPanel.StatusTooltip(view));
        Assert.Null(VariantRepointPanel.StatusTooltip(new VariantRepointWatcher.View(Enabled: true)));
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-8")]
    public void Chat_and_tip_describe_copied_material_paths()
    {
        var s = Strings.Settings;
        Assert.Contains("Copied the material paths", s.GearVariantsChatFmt);
        Assert.Contains("wherever it wins", s.GearVariantsChatFmt);
        Assert.Contains("/proteus > Settings > Gear variants", s.GearVariantsChatFmt);
        Assert.Contains("copies the mod's material paths", s.GearVariantsAutoTip);
        Assert.Contains("parts, VFX and sound stay their own", s.GearVariantsAutoTip);
        Assert.Contains("Apply to All Variants", s.GearVariantsAutoTip);
        Assert.Contains("Undo for what I'm wearing", s.GearVariantsAutoTip);
        Assert.DoesNotContain("IMC", s.GearVariantsAutoTip);
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-6")]
    public void Undo_result_line_and_nothing_recorded()
    {
        var run = new VariantRepointWatcher.RunInfo("undo-worn", 1, 0, 2, 1, 0, true, true, DateTime.UtcNow);
        Assert.Equal(string.Format(Strings.Settings.GearVariantsResultFmt, 2, 1, 0),
                     VariantRepointPanel.ResultText(new VariantRepointWatcher.View(LastRun: run)));
        Assert.Equal(Strings.Settings.GearVariantsNothingRecorded,
                     VariantRepointPanel.ResultText(new VariantRepointWatcher.View(LastRun: run with { NothingRecorded = true })));
        Assert.Null(VariantRepointPanel.ResultText(new VariantRepointWatcher.View(LastRun: run with { Kind = "repoint" })));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-24")]
    public void Waiting_status_text()
        => Assert.Equal(Strings.Settings.GearVariantsWaiting,
                        VariantRepointPanel.StatusText(new VariantRepointWatcher.View(Enabled: true, Waiting: "penumbra")));

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-25")]
    public void Next_redraw_status_when_awaiting()
        => Assert.Contains(Strings.Settings.GearVariantsNextRedraw,
                           VariantRepointPanel.StatusText(new VariantRepointWatcher.View(Enabled: true, AwaitingRedraw: true)));
}
