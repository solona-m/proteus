using System.Collections.Generic;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <see cref="BodySurface.Ray"/>: the first point of a body along a ray, which tells underwear standing over a hole in a
/// body's skin from underwear over skin (<see cref="BodyRetarget.SmallclothesOverHoles"/>) and lands the game chest's bra
/// on a new body where the nearest point faces away.
/// </summary>
public class BodySurfaceRayTests
{
    private const string Skin = SkinlessTargetTests.Skin;

    /// <summary>A square of skin 0.2 m across at z = <paramref name="z"/>, facing +z, in two triangles; x from
    /// <paramref name="x0"/>.</summary>
    private static (List<float> Pos, int[] Tris) Square(float z, float x0 = -0.1f, int first = 0)
        => ([x0, -0.1f, z, x0 + 0.2f, -0.1f, z, x0 + 0.2f, 0.1f, z, x0, 0.1f, z],
            [first, first + 1, first + 2, first, first + 2, first + 3]);

    private static BodySurface Surface(params (List<float> Pos, int[] Tris)[] squares)
    {
        var pos = new List<float>();
        var tris = new List<int>();
        foreach (var (p, t) in squares)
        {
            pos.AddRange(p);
            tris.AddRange(t);
        }
        return new BodySurface(SkinlessTargetTests.Model(pos.ToArray(), [(Skin, tris.ToArray(), 0u)], []), 0.01f);
    }

    [Fact]
    public void A_ray_hits_the_nearest_face_along_it_front_or_back()
    {
        // Two sheets behind the origin, the nearer 10 mm away: the nearer is hit — from behind it as from in front.
        var body = Surface(Square(-0.01f), Square(-0.03f, first: 4));
        Assert.True(body.Ray(Vector3.Zero, -Vector3.UnitZ, 0.05f, out var hit));
        Assert.Equal(0.01f, hit.Distance, 4);
        Assert.Equal(-0.01f, hit.Point.Z, 4);
        Assert.Equal(1f, hit.U + hit.V + hit.W, 4);

        Assert.True(body.Ray(new Vector3(0f, 0f, -0.05f), Vector3.UnitZ, 0.05f, out var back));
        Assert.Equal(-0.03f, back.Point.Z, 4);
    }

    [Fact]
    public void A_ray_reaches_exactly_as_far_as_asked()
    {
        var body = Surface(Square(-0.02f));
        Assert.True(body.Ray(Vector3.Zero, -Vector3.UnitZ, 0.02f, out var hit));   // at the reach itself
        Assert.Equal(0.02f, hit.Distance, 4);
        Assert.False(body.Ray(Vector3.Zero, -Vector3.UnitZ, 0.019f, out _));       // just short of it
        Assert.False(body.Ray(Vector3.Zero, Vector3.UnitZ, 0.05f, out _));          // the other way: nothing there
    }

    [Fact]
    public void A_ray_from_on_the_surface_hits_it_where_it_starts()
    {
        // Underwear vertices sharing the skin's own position at its rim must read as over skin, not over a hole.
        var body = Surface(Square(0f));
        Assert.True(body.Ray(new Vector3(0.03f, 0.02f, 0f), -Vector3.UnitZ, 0.03f, out var hit));
        Assert.Equal(0f, hit.Distance, 5);
    }

    [Fact]
    public void A_ray_stays_off_the_side_it_is_told_to()
    {
        // The nearer sheet's corners all on the -1 side: avoiding that side, the ray goes on to the far one.
        var body = Surface(Square(-0.01f), Square(-0.03f, first: 4));
        var sides = new sbyte[] { -1, -1, -1, -1, 1, 1, 1, 1 };
        Assert.True(body.Ray(Vector3.Zero, -Vector3.UnitZ, 0.05f, out var hit, sides, avoid: -1));
        Assert.Equal(-0.03f, hit.Point.Z, 4);
    }

    [Fact]
    public void A_ray_slanting_across_cells_finds_a_face_it_only_clips()
    {
        // A small triangle the ray crosses near a corner of the grid's cells, on a slant: the cells either side of the
        // ray's samples are searched too.
        var pos = new List<float> { 0.0095f, 0.0095f, -0.0205f, 0.0105f, 0.0095f, -0.0205f, 0.0095f, 0.0105f, -0.0205f };
        var body = new BodySurface(SkinlessTargetTests.Model(pos.ToArray(), [(Skin, new[] { 0, 1, 2 }, 0u)], []), 0.01f);
        var dir = Vector3.Normalize(new Vector3(0.0097f, 0.0097f, -0.0205f));
        Assert.True(body.Ray(Vector3.Zero, dir, 0.05f, out var hit));
        Assert.Equal(-0.0205f, hit.Point.Z, 4);
    }
}
