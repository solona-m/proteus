using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// The reinforced toe's edge: a plane through the cap's rim, drawn at sheet resolution. The line in game
/// used to stair-step and wander because it traced the cap's UV outline; these pin that it is now the
/// plane's own — straight, on the correct side, and fitted to the rim rather than to any hole in the cap.
/// </summary>
public class ToeLineTests
{
    private static List<Vec3> Ring(Vec3 centre, Vec3 u, Vec3 v, float r, int n)
    {
        var pts = new List<Vec3>();
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            float cu = (float)(Math.Cos(a) * r), cv = (float)(Math.Sin(a) * r);
            pts.Add(new Vec3(centre.X + u.X * cu + v.X * cv,
                             centre.Y + u.Y * cu + v.Y * cv,
                             centre.Z + u.Z * cu + v.Z * cv));
        }
        return pts;
    }

    [Fact]
    public void FitPlane_RecoversAnAxisPlane_AndPointsTowardTheToes()
    {
        var rim = Ring(new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), 0.05f, 24);

        Assert.True(ToeLine.FitPlane(rim, toward: new Vec3(0, 0, 0.1f), out var o, out var n));
        Assert.Equal(0f, o.Z, 4);
        Assert.Equal(1f, n.Z, 3);                                   // +Z, toward the toes

        Assert.True(ToeLine.FitPlane(rim, toward: new Vec3(0, 0, -0.1f), out _, out var flipped));
        Assert.Equal(-1f, flipped.Z, 3);                            // and flips when the toes are the other way
    }

    [Fact]
    public void FitPlane_RecoversATiltedPlane()
    {
        float s = 1f / MathF.Sqrt(2f);
        var u = new Vec3(s, -s, 0);
        var v = new Vec3(0, 0, 1);
        // Normal of that plane is (s, s, 0).
        var rim = Ring(new Vec3(0.2f, 0.1f, 0.3f), u, v, 0.04f, 30);

        Assert.True(ToeLine.FitPlane(rim, toward: new Vec3(1.2f, 1.1f, 0.3f), out _, out var n));
        Assert.Equal(s, n.X, 3);
        Assert.Equal(s, n.Y, 3);
        Assert.Equal(0f, n.Z, 3);
    }

    [Fact]
    public void FitPlane_CollinearPoints_AreRefused()
    {
        var line = Enumerable.Range(0, 10).Select(i => new Vec3(i * 0.01f, 0, 0)).ToList();
        Assert.False(ToeLine.FitPlane(line, new Vec3(0, 1, 0), out _, out _));
    }

    /// <summary>
    /// A cap with an opening in it — around a toenail, say — has two boundaries. The plane must be fitted to
    /// the OUTER one, or the opening's points, well forward of the rim, would tilt the line.
    /// </summary>
    [Fact]
    public void OuterRim_IgnoresAnInnerHole()
    {
        // A square annulus: outer ring 0..7 (side 2), inner ring 8..15 (side 1), joined by triangles.
        var pos = new List<Vec3>();
        foreach (var (x, y) in new[] { (-1f, -1f), (0f, -1f), (1f, -1f), (1f, 0f), (1f, 1f), (0f, 1f), (-1f, 1f), (-1f, 0f) })
            pos.Add(new Vec3(x, y, 0));
        foreach (var (x, y) in new[] { (-.5f, -.5f), (0f, -.5f), (.5f, -.5f), (.5f, 0f), (.5f, .5f), (0f, .5f), (-.5f, .5f), (-.5f, 0f) })
            pos.Add(new Vec3(x, y, 0));

        var tri = new List<int>();
        for (int i = 0; i < 8; i++)
        {
            int o0 = i, o1 = (i + 1) % 8, i0 = 8 + i, i1 = 8 + (i + 1) % 8;
            tri.AddRange([o0, o1, i1]);
            tri.AddRange([o0, i1, i0]);
        }

        var rim = ToeLine.OuterRim(tri, pos);

        Assert.Equal(8, rim.Count);
        Assert.All(rim, i => Assert.InRange(i, 0, 7));
    }

    /// <summary>
    /// The one-foot bug. A cap covering both feet can arrive as ONE mesh; each foot is then a separate
    /// connected piece with its own rim, and each must be found — one plane fitted to "the" rim reinforced one
    /// foot in game and left the other with slivers.
    /// </summary>
    [Fact]
    public void Components_SplitsTwoFeetInOneMesh()
    {
        // Two triangles sharing no vertex (left foot 0..2, right foot 3..5) and one unused vertex (6).
        int[] tri = [0, 1, 2, 3, 4, 5];

        var (label, count) = ToeLine.Components(tri, 7);

        Assert.Equal(2, count);
        Assert.Equal(label[0], label[1]);
        Assert.Equal(label[1], label[2]);
        Assert.Equal(label[3], label[4]);
        Assert.Equal(label[4], label[5]);
        Assert.NotEqual(label[0], label[3]);
        Assert.Equal(-1, label[6]);
    }

    /// <summary>
    /// And why it matters: of two feet's rims in one mesh, OuterRim alone returns only one of them, so each
    /// piece has to be asked on its own.
    /// </summary>
    [Fact]
    public void OuterRim_OfTwoSeparateRims_ReturnsOnlyOne_WhichIsWhyEachPieceIsAskedAlone()
    {
        var pos = new List<Vec3>
        {
            new(-1.0f, 0, 0), new(-0.8f, 0, 0), new(-0.9f, 0.1f, 0),          // left: a smaller triangle
            new( 1.0f, 0, 0), new( 1.5f, 0, 0), new( 1.25f, 0.4f, 0),         // right: a larger one
        };
        int[] both = [0, 1, 2, 3, 4, 5];

        Assert.Equal(3, ToeLine.OuterRim(both, pos).Count);                    // just one foot
        Assert.Equal([0, 1, 2], ToeLine.OuterRim(new[] { 0, 1, 2 }, pos).OrderBy(i => i));
        Assert.Equal([3, 4, 5], ToeLine.OuterRim(new[] { 3, 4, 5 }, pos).OrderBy(i => i));
    }

    [Fact]
    public void Weight_IsFullOnTheToeSide_AndFadesBehindTheLine()
    {
        const float band = 0.003f;
        Assert.Equal(1f, ToeLine.Weight(0.01f, band));
        Assert.Equal(1f, ToeLine.Weight(0f, band));                 // the line itself is fully reinforced
        Assert.Equal(0f, ToeLine.Weight(-band, band));
        Assert.Equal(0f, ToeLine.Weight(-1f, band));
        float mid = ToeLine.Weight(-band / 2, band);
        Assert.InRange(mid, 0.4f, 0.6f);
        Assert.True(ToeLine.Weight(-band * 0.25f, band) > mid);      // monotonic toward the line
    }

    /// <summary>
    /// The whole point. A square face whose signed distance runs linearly across U puts the line at one
    /// fixed U: every row must switch at exactly the same column. The footprint version stepped here.
    /// </summary>
    [Fact]
    public void Rasterize_DrawsAStraightLine_WithNoStairSteps()
    {
        const int size = 256;
        var map = new byte[size * size];
        // Distance 0 at u = 0.5, positive to the left, crossing zero partway along each row.
        float DistAt(float u) => (0.5f - u) * 0.02f;
        const float band = 0.003f;

        (float U, float V) a = (0.2f, 0.2f), b = (0.8f, 0.2f), c = (0.8f, 0.8f), d = (0.2f, 0.8f);
        ToeLine.Rasterize(map, size, a, b, c, DistAt(a.U), DistAt(b.U), DistAt(c.U), band);
        ToeLine.Rasterize(map, size, a, c, d, DistAt(a.U), DistAt(c.U), DistAt(d.U), band);

        int? edge = null;
        for (int y = 70; y < 190; y++)
        {
            // The first column, left to right, that is no longer at full weight.
            int x = 60;
            while (x < 200 && map[y * size + x] == 255) x++;
            edge ??= x;
            Assert.Equal(edge, x);                                   // identical on every row
        }
        Assert.InRange(edge!.Value, 126, 130);                       // at u = 0.5

        // And it fades rather than stops.
        int row = 128 * size;
        Assert.InRange(map[row + edge.Value + 5], 1, 254);
        Assert.Equal(0, map[row + 190]);
    }

    /// <summary>A body's UVs can sit a whole tile away (a heel's foot model); the sheet wraps, so must this.</summary>
    [Fact]
    public void Rasterize_WrapsUvsOutsideTheUnitTile()
    {
        const int size = 64;
        var map = new byte[size * size];
        ToeLine.Rasterize(map, size, (0.2f, -0.8f), (0.4f, -0.8f), (0.3f, -0.6f), 0.01f, 0.01f, 0.01f, 0.003f);

        // v = -0.7 wraps to 0.3.
        Assert.Equal(255, map[(int)(0.25f * size) * size + (int)(0.3f * size)]);
    }

    [Fact]
    public void Rasterize_KeepsTheLargerWeightWhereFacesOverlap()
    {
        const int size = 32;
        var map = new byte[size * size];
        (float, float) a = (0.1f, 0.1f), b = (0.9f, 0.1f), c = (0.5f, 0.9f);
        ToeLine.Rasterize(map, size, a, b, c, 0.01f, 0.01f, 0.01f, 0.003f);       // full
        ToeLine.Rasterize(map, size, a, b, c, -0.002f, -0.002f, -0.002f, 0.003f); // faint, drawn after

        Assert.Equal(255, map[(int)(0.4f * size) * size + (int)(0.5f * size)]);
    }
}
