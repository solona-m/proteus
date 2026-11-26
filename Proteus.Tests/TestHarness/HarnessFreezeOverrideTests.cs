using System;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>
/// The harness's memory-only Studio freeze override: recorded with its Before, listed among what is left, and cleared by
/// the output put-back whether or not a Studio session is left — never written to the user's settings.
/// </summary>
[Collection(HarnessTimingCollection.Name)]
public class HarnessFreezeOverrideTests : IDisposable
{
    private readonly HarnessFixture fx = new();

    public void Dispose() => fx.Dispose();

    [Fact]
    [Trait("Plan", "AC-studio-freeze-9")]
    [Trait("Plan", "EDGE-studio-freeze-7")]
    [Trait("Plan", "EDGE-studio-freeze-37")]
    public void Override_is_recorded_listed_and_cleared_by_PutBackOutput()
    {
        fx.Game.SetFreezeOverride(false);
        var first = fx.Changes.NoteFreezeOverride(false, beforeEffective: true);
        fx.Game.SetFreezeOverride(true);
        var second = fx.Changes.NoteFreezeOverride(true, beforeEffective: false);
        Assert.True(first.BeforeEffective);
        Assert.True(second.BeforeEffective);   // the first set keeps its Before
        Assert.Contains("the freeze override", fx.Changes.Leftovers());
        Assert.False(fx.Changes.Empty);

        // No Studio session is needed: the output put-back clears it by itself.
        var output = fx.Changes.PutBackOutput();
        Assert.Equal("cleared", output.Freeze);
        Assert.Null(fx.Game.FreezeOverrideValue);
        Assert.DoesNotContain("the freeze override", fx.Changes.Leftovers());
        Assert.Equal("none", fx.Changes.PutBackOutput().Freeze);
        Assert.True(fx.Game.FreezeConfig);   // the user's setting is never touched
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-9")]
    [Trait("Plan", "EDGE-studio-freeze-37")]
    public void PutBackOutput_clears_the_overrides_before_anything_that_can_throw()
    {
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(ProteusDir(), "TestHarness", "HarnessChanges.cs"));
        int method = src.IndexOf("internal OutputPutBack PutBackOutput()", StringComparison.Ordinal);
        int clear = src.IndexOf("game.SetFreezeOverride(null);", method, StringComparison.Ordinal);
        int tryAt = src.IndexOf("try", method, StringComparison.Ordinal);
        Assert.True(method >= 0 && clear > method && clear < tryAt, "the freeze override is cleared before the try");
    }

    private static string ProteusDir([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!, "..", "..", "Proteus");

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-41")]
    public void PutBackOutput_clears_the_two_overrides_independently()
    {
        fx.Game.SetFreezeOverride(false);
        fx.Changes.NoteFreezeOverride(false, true);
        var onlyFreeze = fx.Changes.PutBackOutput();
        Assert.Equal(("cleared", "none"), (onlyFreeze.Freeze, onlyFreeze.AutoRedraw));

        fx.Game.SetAutoRedrawOverride(false);
        fx.Changes.NoteAutoRedrawOverride(false, true);
        var onlyRedraw = fx.Changes.PutBackOutput();
        Assert.Equal(("none", "cleared"), (onlyRedraw.Freeze, onlyRedraw.AutoRedraw));
    }
}
