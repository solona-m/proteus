using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Selecting single polygons for the Move tools, and the soft selection that falls off along the surface.
/// <para/>
/// Over <see cref="SyntheticModel"/>, whose islands are the case that matters: the triangles of one island are joined
/// (they share a corner position, never a vertex index, as a real model's uv seams make them), and two islands are
/// separate pieces however they are placed.
/// </summary>
public class PolygonSelectionTests
{
    private const string Cloth = "/mt_c0201e6255_top_a.mtrl";
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";

    /// <summary>Two islands of three triangles each in one cloth submesh, and a skin mesh.</summary>
    private static ModelParts Model(float secondIslandZ = 0f)
    {
        var mdl = SyntheticModel.Build([],
            new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 3),
                                           new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetZ: secondIslandZ,
                                                                  OffsetY: 0.5f)),
            new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 100f)));
        return ModelPartReader.Read(mdl)!;
    }

    private static List<PolygonSelection.Key> Keys(ModelParts m, string material, int sub)
    {
        var part = m.Parts.Where(p => p.Island < 0 && p.Material == material).ElementAt(sub);
        var keys = new List<PolygonSelection.Key>();
        for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            keys.Add(PolygonSelection.Key.Of(part.Triangles[t], part.Triangles[t + 1], part.Triangles[t + 2]));
        return keys;
    }

    [Fact]
    public void A_polygon_is_the_same_whichever_order_its_corners_come_in()
    {
        Assert.Equal(PolygonSelection.Key.Of(7, 2, 5), PolygonSelection.Key.Of(5, 7, 2));
        Assert.Equal(new PolygonSelection.Key(2, 5, 7), PolygonSelection.Key.Of(2, 7, 5));
    }

    [Fact]
    public void Skin_is_never_selectable()
    {
        var m = Model();
        var polys = new PolygonSelection(m);
        Assert.All(Keys(m, Skin, 0), k => Assert.False(polys.Contains(k)));
        Assert.All(Keys(m, Cloth, 0), k => Assert.True(polys.Contains(k)));
    }

    [Fact]
    public void Growing_spreads_along_joined_polygons_and_not_to_a_separate_piece()
    {
        var m = Model();
        var polys = new PolygonSelection(m);
        var first = Keys(m, Cloth, 0);
        var second = Keys(m, Cloth, 1);

        var grown = polys.Grow(new HashSet<PolygonSelection.Key> { first[0] });

        Assert.True(first.All(grown.Contains), "the rest of the joined piece joins the selection");
        Assert.False(second.Any(grown.Contains), "the separate piece does not");
    }

    [Fact]
    public void Shrinking_drops_the_outer_ring_and_keeps_a_whole_piece()
    {
        var m = Model();
        var polys = new PolygonSelection(m);
        var first = Keys(m, Cloth, 0);

        // One polygon of a joined piece is all edge.
        Assert.Empty(polys.Shrink(new HashSet<PolygonSelection.Key> { first[0] }));

        // The whole piece touches nothing unselected, so it is all interior.
        var whole = first.ToHashSet();
        Assert.Equal(whole, polys.Shrink(whole));
    }

    [Fact]
    public void A_ray_picks_the_polygon_it_passes_through()
    {
        var m = Model();
        var polys = new PolygonSelection(m);
        var target = Keys(m, Cloth, 0)[1];
        var centre = (PolygonSelection.At(m.Positions, target.A) + PolygonSelection.At(m.Positions, target.B)
                      + PolygonSelection.At(m.Positions, target.C)) / 3f;

        var picked = polys.Pick(centre + new Vector3(0f, 0f, 5f), new Vector3(0f, 0f, -1f), m.Positions);

        Assert.Equal(target, picked);
        Assert.Null(polys.Pick(new Vector3(500f, 500f, 5f), new Vector3(0f, 0f, -1f), m.Positions));
    }

    [Fact]
    public void The_soft_selection_follows_the_surface_and_not_space()
    {
        // The second piece sits a millimetre in front of the first: nearer in space than anything, joined to nothing.
        var m = Model(secondIslandZ: 0.001f);
        var seed = Keys(m, Cloth, 0)[0];
        var secondNodes = Keys(m, Cloth, 1).SelectMany(k => k.Corners).ToHashSet();

        var alongSurface = new MeshVolumeSolve(m);
        alongSurface.BeginMove(seed.Corners, adjacent: true, falloffRadius: 5f, alongSurface: true);
        alongSurface.MoveTo(new Vector3(0f, 0f, 1f));

        var throughSpace = new MeshVolumeSolve(m);
        throughSpace.BeginMove(seed.Corners, adjacent: true, falloffRadius: 5f, alongSurface: false);
        throughSpace.MoveTo(new Vector3(0f, 0f, 1f));

        Assert.All(secondNodes, v => Assert.Equal(0f, alongSurface.DeltaAt(v).Z));
        Assert.Contains(secondNodes, v => throughSpace.DeltaAt(v).Z > 0f);

        // The rest of the seed's own piece does follow, fading with the walk from the seed.
        var firstOthers = Keys(m, Cloth, 0).Skip(1).SelectMany(k => k.Corners).Except(seed.Corners).ToList();
        Assert.Contains(firstOthers, v => alongSurface.DeltaAt(v).Z is > 0f and < 1f);
    }
}
