using System;
using System.Linq;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>Which Studio tools hold the character still, and what the line under the snowflake toggle says.</summary>
public class PartsPanelFreezeTests
{
    [Fact]
    [Trait("Plan", "AC-studio-freeze-1")]
    [Trait("Plan", "AC-studio-freeze-2")]
    [Trait("Plan", "EDGE-studio-freeze-27")]
    public void FreezesCharacter_is_exactly_the_brushes_and_part_tools()
    {
        var expected = new[]
        {
            PartsPanel.Tool.Inflate, PartsPanel.Tool.Deflate, PartsPanel.Tool.Relax, PartsPanel.Tool.Smooth,
            PartsPanel.Tool.Bridge, PartsPanel.Tool.Wind, PartsPanel.Tool.Grab, PartsPanel.Tool.Move,
            PartsPanel.Tool.Rotate, PartsPanel.Tool.Scale,
        };
        var notFreezing = new[] { PartsPanel.Tool.Navigate, PartsPanel.Tool.Retarget, PartsPanel.Tool.Matcap };
        // Every tool is classified on purpose: a new one fails here until it is.
        Assert.Equal(Enum.GetValues<PartsPanel.Tool>().OrderBy(t => t), expected.Concat(notFreezing).OrderBy(t => t));
        foreach (var t in Enum.GetValues<PartsPanel.Tool>())
            Assert.Equal(expected.Contains(t), PartsPanel.FreezesCharacter(t));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-7")]
    [Trait("Plan", "AC-studio-freeze-10")]
    public void FreezeStatusLine_maps_each_state()
    {
        Assert.Equal(Strings.Parts.FreezeMoving, PartsPanel.FreezeStatusLine(FreezeState.Settling));
        Assert.Equal(Strings.Parts.FreezeBusy, PartsPanel.FreezeStatusLine(FreezeState.Busy));
        Assert.Equal(Strings.Parts.FreezeGpose, PartsPanel.FreezeStatusLine(FreezeState.GPose));
        Assert.Equal(Strings.Parts.FreezeUnavailable, PartsPanel.FreezeStatusLine(FreezeState.Unavailable));
        foreach (var s in new[] { FreezeState.Frozen, FreezeState.Idle, FreezeState.Off, FreezeState.NoCharacter })
            Assert.Null(PartsPanel.FreezeStatusLine(s));
    }
}
