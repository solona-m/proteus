using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The gen2 arm of <see cref="UVRemapService.UvConverter"/> — the one pairing that needs no transfer map,
/// since vanilla space is a plain crop of bibo, so these run on any machine.
/// </summary>
public class UvConverterTests
{
    private static UVRemapService Service() => new(Substitute.For<IPluginLog>(), ".");

    [Fact]
    public void Same_space_and_unknown_space_need_no_conversion()
    {
        var uv = Service();
        Assert.Null(uv.UvConverter("bibo", "bibo"));
        Assert.Null(uv.UvConverter("BIBO", "bibo"));   // the comparison is case-insensitive
        Assert.Null(uv.UvConverter(null, "gen3"));
        Assert.Null(uv.UvConverter("gen3", null));
    }

    [Fact]
    public void Gen2_into_bibo_maps_onto_the_right_half()
    {
        var conv = Service().UvConverter("gen2", "bibo");
        Assert.NotNull(conv);

        Assert.Equal((0.5f, 0.25f), conv!(0f, 0.25f));
        Assert.Equal((0.75f, 0.5f), conv(0.5f, 0.5f));
        Assert.Equal((1f, 0.9f), conv(1f, 0.9f));
    }

    [Fact]
    public void Bibo_into_gen2_unpacks_the_right_half()
    {
        var conv = Service().UvConverter("bibo", "gen2");
        Assert.NotNull(conv);

        Assert.Equal((0f, 0.25f), conv!(0.5f, 0.25f));   // the seam itself is in vanilla space
        Assert.Equal((0.5f, 0.5f), conv(0.75f, 0.5f));
        Assert.Equal((1f, 0.9f), conv(1f, 0.9f));
    }

    /// <summary>
    /// bibo's LEFT half is its own added detail area and has no vanilla equivalent. The affine would hand
    /// such a point a negative u, which the sampler wraps to the far edge — so the triangle spans the whole
    /// sheet and renders as a smeared band. It must come back unmapped instead, leaving the vertex as
    /// authored.
    /// </summary>
    [Fact]
    public void Bibo_left_half_has_no_vanilla_home()
    {
        var conv = Service().UvConverter("bibo", "gen2");
        Assert.NotNull(conv);

        Assert.Null(conv!(0.0f, 0.5f));
        Assert.Null(conv(0.2f, 0.5f));
        Assert.Null(conv(0.49999f, 0.5f));
    }

    [Fact]
    public void Gen2_survives_a_round_trip_through_bibo()
    {
        var uv = Service();
        var out2 = uv.UvConverter("gen2", "bibo")!;
        var back = uv.UvConverter("bibo", "gen2")!;

        foreach (var u in new[] { 0f, 0.1f, 0.5f, 0.837f, 1f })
        {
            var mid = out2(u, 0.3f);
            Assert.NotNull(mid);
            var round = back(mid!.Value.U, mid.Value.V);
            Assert.NotNull(round);
            // Halving and doubling a float doesn't always land back on the same bits (0.1 comes back as
            // 0.100000024), so compare to a tolerance far below one texel of a 4096 map.
            Assert.Equal(u, round!.Value.U, 5);
            Assert.Equal(0.3f, round.Value.V, 5);
        }
    }
}
