using System.Collections.Generic;
using System.Text.Json;
using Proteus.Gui;
using Proteus.Localization;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The rules behind every destructive one-click button (an Undo that throws away an edit saved into a mod): armed only
/// while Ctrl or Shift is held, never while blocked, and a tooltip that keeps the button's own text and ends with the
/// shared arm tip — or says why it is blocked.
/// </summary>
public class ArmedActionTests
{
    [Theory]
    [Trait("Plan", "AC-ctrl-click-undo-6")]
    [Trait("Plan", "EDGE-ctrl-click-undo-1")]
    [Trait("Plan", "EDGE-ctrl-click-undo-2")]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Armed_only_while_ctrl_or_shift_is_held(bool ctrl, bool shift, bool armed)
        => Assert.Equal(armed, ArmedAction.Armed(ctrl, shift));

    [Theory]
    [Trait("Plan", "EDGE-ctrl-click-undo-1")]
    [Trait("Plan", "EDGE-ctrl-click-undo-3")]
    [Trait("Plan", "EDGE-ctrl-click-undo-12")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void A_press_that_is_unarmed_or_blocked_never_counts(bool pressed, bool armed, bool blocked)
        => Assert.False(ArmedAction.Clicked(pressed, armed, blocked));

    [Fact]
    [Trait("Plan", "EDGE-ctrl-click-undo-1")]
    [Trait("Plan", "EDGE-ctrl-click-undo-3")]
    public void Only_an_armed_unblocked_press_counts()
        => Assert.True(ArmedAction.Clicked(pressed: true, armed: true, blocked: false));

    [Fact]
    [Trait("Plan", "AC-ctrl-click-undo-6")]
    [Trait("Plan", "EDGE-ctrl-click-undo-7")]
    public void An_existing_tip_keeps_its_text_and_gains_the_arm_tip()
        => Assert.Equal("X\n\nA", ArmedAction.Tooltip("X", null, "A"));

    [Fact]
    [Trait("Plan", "AC-ctrl-click-undo-6")]
    [Trait("Plan", "EDGE-ctrl-click-undo-6")]
    [Trait("Plan", "EDGE-ctrl-click-undo-8")]
    public void No_tip_shows_the_arm_tip_alone()
    {
        Assert.Equal("A", ArmedAction.Tooltip(null, null, "A"));
        Assert.Equal("A", ArmedAction.Tooltip("", null, "A"));
    }

    [Fact]
    [Trait("Plan", "AC-ctrl-click-undo-5")]
    [Trait("Plan", "EDGE-ctrl-click-undo-4")]
    public void A_busy_reason_replaces_the_tip()
    {
        Assert.Equal("Busy", ArmedAction.Tooltip("X", "Busy", "A"));
        Assert.Equal("Busy", ArmedAction.Tooltip(null, "Busy", "A"));
    }

    [Fact]
    [Trait("Plan", "EDGE-ctrl-click-undo-5")]
    public void Blocked_without_a_reason_keeps_tip_and_arm_tip()
        => Assert.Equal("X\n\nA", ArmedAction.Tooltip("X", null, "A"));

    private sealed class LocEntry
    {
        public string Message { get; set; } = "";
    }

    private static Dictionary<string, LocEntry> Load(string lang)
    {
        using var s = typeof(Plugin).Assembly.GetManifestResourceStream($"Proteus.Localization.{lang}.json");
        Assert.NotNull(s);
        return JsonSerializer.Deserialize<Dictionary<string, LocEntry>>(s!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    [Trait("Plan", "AC-ctrl-click-undo-5")]
    [Trait("Plan", "AC-ctrl-click-undo-6")]
    [Trait("Plan", "EDGE-ctrl-click-undo-11")]
    public void The_arm_and_busy_tips_are_existing_translated_strings()
    {
        Assert.Equal("Hold Ctrl or Shift and click.", Strings.Parts.BrushRevertArmTip);
        string[] keys = ["Parts.Brush.Revert.Arm.Tip", "HairPort.Working", "Parts.Matcap.Working", "Parts.Retarget.Working"];
        var en = Load("en");
        foreach (var k in keys) Assert.True(en.ContainsKey(k), k);
        foreach (var lang in new[] { "ja", "de", "fr", "zh", "ko", "es", "ru" })
        {
            var loc = Load(lang);
            foreach (var k in keys) Assert.True(loc.ContainsKey(k), $"{lang}: {k}");
            Assert.NotEqual(en["Parts.Brush.Revert.Arm.Tip"].Message, loc["Parts.Brush.Revert.Arm.Tip"].Message);
        }
    }
}
