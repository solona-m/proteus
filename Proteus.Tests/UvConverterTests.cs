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

    /// <summary>The side a vertex was on before sides existed, and what an unknown one still gets.</summary>
    private const int NoSide = 0;

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

        Assert.Equal((0.5f, 0.25f), conv!(0f, 0.25f, NoSide));
        Assert.Equal((0.75f, 0.5f), conv(0.5f, 0.5f, NoSide));
        Assert.Equal((1f, 0.9f), conv(1f, 0.9f, NoSide));
    }

    [Fact]
    public void Bibo_into_gen2_unpacks_the_right_half()
    {
        var conv = Service().UvConverter("bibo", "gen2");
        Assert.NotNull(conv);

        Assert.Equal((0f, 0.25f), conv!(0.5f, 0.25f, NoSide));   // the seam itself is in vanilla space
        Assert.Equal((0.5f, 0.5f), conv(0.75f, 0.5f, NoSide));
        Assert.Equal((1f, 0.9f), conv(1f, 0.9f, NoSide));
    }

    /// <summary>
    /// bibo's LEFT half is the character's other side, and vanilla has no coordinate for it — vanilla puts
    /// both sides on the right half. The affine would hand such a point a negative u, which the sampler wraps
    /// to the far edge — so the triangle spans the whole sheet and renders as a smeared band. It must come
    /// back unmapped instead, leaving the vertex as authored.
    /// </summary>
    [Fact]
    public void Bibo_left_half_has_no_vanilla_home()
    {
        var conv = Service().UvConverter("bibo", "gen2");
        Assert.NotNull(conv);

        Assert.Null(conv!(0.0f, 0.5f, NoSide));
        Assert.Null(conv(0.2f, 0.5f, NoSide));
        Assert.Null(conv(0.49999f, 0.5f, NoSide));
    }

    [Fact]
    public void Gen2_survives_a_round_trip_through_bibo()
    {
        var uv = Service();
        var out2 = uv.UvConverter("gen2", "bibo")!;
        var back = uv.UvConverter("bibo", "gen2")!;

        foreach (var u in new[] { 0f, 0.1f, 0.5f, 0.837f, 1f })
        {
            var mid = out2(u, 0.3f, NoSide);
            Assert.NotNull(mid);
            var round = back(mid!.Value.U, mid.Value.V, NoSide);
            Assert.NotNull(round);
            // Halving and doubling a float doesn't always land back on the same bits (0.1 comes back as
            // 0.100000024), so compare to a tolerance far below one texel of a 4096 map.
            Assert.Equal(u, round!.Value.U, 5);
            Assert.Equal(0.3f, round.Value.V, 5);
        }
    }

    // ── un-mirroring ─────────────────────────────────────────────────────────

    /// <summary>
    /// Vanilla gives both sides of the body the same UV. Un-mirrored, the +X side keeps the right half (the
    /// half vanilla space IS a crop of) and the -X side takes its reflection, so the two stop sharing texels.
    /// </summary>
    [Fact]
    public void Unmirrored_gen2_sends_the_two_sides_to_opposite_halves()
    {
        var conv = Service().UvConverter("gen2", "bibo", unmirror: true);
        Assert.NotNull(conv);

        // u = 0.5 in vanilla -> 0.75 on the right half, and its reflection 0.25 on the left.
        Assert.Equal((0.75f, 0.4f), conv!(0.5f, 0.4f, 1));
        Assert.Equal((0.25f, 0.4f), conv(0.5f, 0.4f, -1));

        // Every pair reflects about the middle of the sheet, and V never moves.
        foreach (var u in new[] { 0f, 0.2f, 0.5f, 0.9f, 1f })
        {
            var right = conv(u, 0.6f, 1)!.Value;
            var left = conv(u, 0.6f, -1)!.Value;
            Assert.Equal(1f, right.U + left.U, 5);
            Assert.Equal(0.6f, right.V, 5);
            Assert.Equal(0.6f, left.V, 5);
            Assert.True(right.U >= 0.5f && left.U <= 0.5f);
        }
    }

    /// <summary>
    /// A vertex the geometry couldn't place — one on the midline whose triangles disagreed — must not be
    /// guessed onto the far half. It takes the +X branch, which is exactly the behaviour it had before
    /// un-mirroring existed.
    /// </summary>
    [Fact]
    public void An_unplaced_vertex_keeps_the_old_behaviour()
    {
        var uv = Service();
        var plain = uv.UvConverter("gen2", "bibo")!;
        var conv = uv.UvConverter("gen2", "bibo", unmirror: true)!;

        Assert.Equal(plain(0.3f, 0.7f, NoSide), conv(0.3f, 0.7f, NoSide));
        Assert.Equal(conv(0.3f, 0.7f, 1), conv(0.3f, 0.7f, NoSide));
    }

    /// <summary>
    /// Without the flag, sides are ignored entirely — otherwise every existing shell would start splitting
    /// its body in half the moment a caller passed a side through.
    /// </summary>
    [Fact]
    public void Sides_do_nothing_unless_unmirroring()
    {
        var conv = Service().UvConverter("gen2", "bibo")!;
        Assert.Equal(conv(0.3f, 0.7f, 1), conv(0.3f, 0.7f, -1));
    }

    /// <summary>
    /// The memo behind the converter must key on the side as well as the UV. A mirrored sheet hands the two
    /// sides the SAME (u,v), so a UV-only key would answer the second vertex with the first one's half and
    /// quietly re-fold the body — the exact bug this feature exists to remove.
    /// </summary>
    [Fact]
    public void The_same_uv_still_answers_per_side()
    {
        var conv = Service().UvConverter("gen2", "bibo", unmirror: true)!;
        var first = conv(0.42f, 0.13f, 1);
        var second = conv(0.42f, 0.13f, -1);
        var firstAgain = conv(0.42f, 0.13f, 1);

        Assert.NotEqual(first, second);
        Assert.Equal(first, firstAgain);
    }
}
