using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Which UV space a shell should take when its parts disagree, expressed through the converter that does the
/// work. The shell takes ONE space and rewrites every other part's vertices into it, so the choice decides
/// how much geometry survives.
/// <para/>
/// The two directions are NOT equally lossy, and that asymmetry is the whole rule: gen2 IS bibo's right half,
/// so gen2 -> bibo places every vertex, while bibo -> gen2 has nowhere to put bibo's left half and reports
/// those vertices unmapped. A shell holding both must therefore land in bibo.
/// <para/>
/// This bit in practice because gear ships its own skin: a top whose exposed torso is vanilla geometry is the
/// first part collected, and picking the first part's space sent three bibo parts (13k vertices, 6.6k of them
/// unmappable) through the fold to accommodate one 529-vertex gen2 part.
/// </summary>
public class ShellUvSpaceTests
{
    private static UVRemapService Service() => new(Substitute.For<IPluginLog>(), ".");

    /// <summary>A grid of UVs spanning the whole sheet, as a part's vertices would.</summary>
    private static (float U, float V)[] Sheet()
    {
        var pts = new (float, float)[121];
        int i = 0;
        for (int a = 0; a <= 10; a++)
            for (int b = 0; b <= 10; b++)
                pts[i++] = (a / 10f, b / 10f);
        return pts;
    }

    /// <summary>
    /// Every gen2 vertex has a home in bibo — the affine is total, so nothing is dropped.
    /// </summary>
    [Fact]
    public void Gen2_into_bibo_places_every_vertex()
    {
        var conv = Service().UvConverter("gen2", "bibo")!;
        foreach (var (u, v) in Sheet())
            Assert.NotNull(conv(u, v, 0));
    }

    /// <summary>
    /// The other direction drops bibo's whole left half. This is the loss the space choice exists to avoid,
    /// and it is not a rounding-error minority — it is half the sheet.
    /// </summary>
    [Fact]
    public void Bibo_into_gen2_drops_the_left_half()
    {
        var conv = Service().UvConverter("bibo", "gen2")!;
        int placed = 0, dropped = 0;
        foreach (var (u, v) in Sheet())
            if (conv(u, v, 0) == null) dropped++; else placed++;

        Assert.True(dropped > 0, "bibo's left half should have no vanilla home");
        // The seam sits at u = 0.5, so an 11-wide grid drops the five columns below it.
        Assert.Equal(5 * 11, dropped);
        Assert.Equal(6 * 11, placed);
    }

    /// <summary>
    /// Stated as the rule the shell follows: converting INTO the asymmetric space is total, converting into
    /// the mirrored one is not. A shell mixing the two must pick the direction that keeps every vertex.
    /// </summary>
    [Fact]
    public void The_asymmetric_space_is_the_lossless_destination()
    {
        var uv = Service();
        var intoBibo = uv.UvConverter("gen2", "bibo")!;
        var intoGen2 = uv.UvConverter("bibo", "gen2")!;

        int lostGoingToBibo = 0, lostGoingToGen2 = 0;
        foreach (var (u, v) in Sheet())
        {
            if (intoBibo(u, v, 0) == null) lostGoingToBibo++;
            if (intoGen2(u, v, 0) == null) lostGoingToGen2++;
        }

        Assert.Equal(0, lostGoingToBibo);
        Assert.True(lostGoingToGen2 > lostGoingToBibo);
    }
}
