using System.Collections.Generic;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The push sweep's ladder. An instrument that reads wrong is worse than none: a band landing on the wrong
/// height looks, in game, exactly like a body that clips there.
/// </summary>
public class PushSweepTests
{
    [Fact]
    public void Default_ladder_parses_with_no_problems()
    {
        var problems = new List<string>();
        var sweep = PushSweep.Parse(PushSweep.DefaultLadder, problems);
        Assert.NotNull(sweep);
        Assert.Empty(problems);
        Assert.Equal(8, sweep!.BandCount);
    }

    /// <summary>The feet are the control; the toe cap's own height logic must see today's push.</summary>
    [Fact]
    public void Default_ladder_leaves_the_feet_at_full_push()
    {
        var sweep = PushSweep.Parse(PushSweep.DefaultLadder)!;
        Assert.Equal(1f, sweep.MultiplierAt(0.00f));
        Assert.Equal(1f, sweep.MultiplierAt(0.11f));
    }

    [Theory]
    [InlineData(-0.5f, 1.0f)]   // below the first line: the first band
    [InlineData(0.0f, 1.0f)]
    [InlineData(0.5f, 1.0f)]
    [InlineData(1.0f, 0.5f)]    // a band starts AT its height
    [InlineData(1.5f, 0.5f)]
    [InlineData(2.0f, 0.1f)]
    [InlineData(9.0f, 0.1f)]
    public void A_band_runs_from_its_height_to_the_next(float y, float expected)
    {
        var sweep = PushSweep.Parse("0 1\n1 0.5\n2 0.1")!;
        Assert.Equal(expected, sweep.MultiplierAt(y));
    }

    /// <summary>Reversing the ladder should mean swapping numbers, not reordering lines.</summary>
    [Fact]
    public void Lines_may_be_written_in_any_order()
    {
        var sweep = PushSweep.Parse("2 0.1\n0 1\n1 0.5")!;
        Assert.Equal(1f, sweep.MultiplierAt(0.5f));
        Assert.Equal(0.5f, sweep.MultiplierAt(1.5f));
        Assert.Equal(0.1f, sweep.MultiplierAt(2.5f));
    }

    [Fact]
    public void A_bad_line_is_reported_and_skipped_not_fatal()
    {
        var problems = new List<string>();
        var sweep = PushSweep.Parse("0 1\nnonsense\n1 -0.5\n2 0.25  # comment", problems);
        Assert.NotNull(sweep);
        Assert.Equal(2, sweep!.BandCount);
        Assert.Equal(2, problems.Count);
        Assert.Equal(0.25f, sweep.MultiplierAt(3f));
    }

    [Theory]
    [InlineData("")]
    [InlineData("# only comments\n\n   ")]
    public void No_bands_means_no_sweep(string text) => Assert.Null(PushSweep.Parse(text));

    /// <summary>Comma decimals must not parse as something else on a non-English locale.</summary>
    [Fact]
    public void Parsing_is_culture_invariant()
    {
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var sweep = PushSweep.Parse("0 1.0\n1 0.35")!;
            Assert.Equal(0.35f, sweep.MultiplierAt(1.2f));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void Report_counts_each_band_and_resets()
    {
        var sweep = PushSweep.Parse("0 1\n1 0.5")!;
        sweep.Take(0.2f);
        sweep.Take(0.4f);
        sweep.Take(1.3f);
        var first = sweep.TakeReport();
        Assert.Contains("[0.00]=2", first);
        Assert.Contains("[1.00]=1", first);
        Assert.Equal("no shell vertices were pushed", sweep.TakeReport());
    }
}
