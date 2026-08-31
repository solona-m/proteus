using System;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// When the connector heuristic drops a submesh, and when it must not.
/// <para/>
/// It exists to remove the thin seam RINGS a body mod puts at its joints, which are redundant because the
/// neighbouring part draws the same stretch of body. Two things made it eat real skin instead:
/// <list type="number">
/// <item>The size test was absolute ("under 200 triangles"), read off a body whose real parts run 800+.
/// Gear that ships its own skin cuts it far coarser — a garment's whole exposed torso can be 500 triangles,
/// so its neck (20) and elbow (144) both looked like rings.</item>
/// <item>Redundancy was assumed rather than checked. A ring at the top of a hand model IS covered by the
/// part above it; a neck ring at the top of a torso has nothing above it and is the only thing that paints
/// that band of the character.</item>
/// </list>
/// Neither alone is enough: the elbow is saved by the relative size, but the neck is shape-identical to a
/// wrist ring — 20 triangles in a thin band at its part's own top edge — and only the coverage test tells
/// them apart.
/// </summary>
public class ConnectorRedundancyTests
{
    private const string BodyMaterial = "/mt_c0201b0001_a.mtrl";

    private const int MainTris = 400;
    private const int TailTris = 8;

    /// <summary>
    /// A mesh shaped like the real thing: a big main part, the submesh under test in the MIDDLE, and a small
    /// trailing one. The subject must not be last, because the heuristic also drops a mesh's final submesh
    /// outright (a duplicate variant) — conflating the two is what made the first version of this test
    /// mislead. Rinoa's elbow is submesh 1 of 4 for exactly this reason.
    /// </summary>
    private static byte[] Model(int subjectTris) => SyntheticModel.Build(
        ["atr_top"],
        new SyntheticModel.Mesh(BodyMaterial,
            new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: MainTris),
            new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: subjectTris),
            new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: TailTris)));

    /// <summary>Triangles the connector pass removed, for a given subject size and neighbour layout.</summary>
    private static int Removed(int subjectTris, (float Lo, float Hi)[]? otherBands)
    {
        var model = Model(subjectTris);
        return Build(model, drop: false, otherBands).TrianglesOut
             - Build(model, drop: true, otherBands).TrianglesOut;
    }

    private static SecondSkinWriter.Stats Build(byte[] model, bool drop,
                                                (float Lo, float Hi)[]? otherBands)
    {
        var layers = new[] { new SecondSkinLayer { MaterialName = "/ss_0.mtrl", Coverage = null } };
        SecondSkinWriter.Build(
            [new SecondSkinWriter.SourceSpec(model, DropConnectors: drop, OtherPartBands: otherBands)],
            layers, null, out var stats);
        return stats;
    }

    /// <summary>The synthetic model spans a known Y range; a band that contains it stands for a neighbour
    /// covering this part, and one far away stands for having no neighbour there.</summary>
    private static readonly (float Lo, float Hi)[] Covering = [(-1000f, 1000f)];
    private static readonly (float Lo, float Hi)[] Elsewhere = [(500f, 600f)];

    /// <summary>
    /// The case the heuristic is FOR: a small submesh whose band another part already draws.
    /// </summary>
    [Fact]
    public void A_small_submesh_another_part_covers_is_dropped()
    {
        // Both the subject and the trailing submesh are redundant here, so both go.
        Assert.Equal(TailTris + TailTris, Removed(subjectTris: TailTris, Covering));
    }

    /// <summary>
    /// The neck: identical in shape and size to a seam ring, but nothing else in the shell covers it. It is
    /// the only geometry painting that band, so it stays — and so does the trailing submesh, for the same
    /// reason.
    /// </summary>
    [Fact]
    public void A_small_submesh_nothing_covers_is_kept()
    {
        Assert.Equal(0, Removed(subjectTris: TailTris, Elsewhere));
    }

    /// <summary>
    /// A part alone in the shell has no neighbour, so none of its geometry can be redundant — an empty band
    /// list is a real answer, not missing information.
    /// </summary>
    [Fact]
    public void A_lone_part_keeps_everything()
    {
        Assert.Equal(0, Removed(subjectTris: TailTris, []));
    }

    /// <summary>
    /// The elbow: big RELATIVE to its own mesh, so it is not ring-shaped however few triangles it has in
    /// absolute terms. This is what the old flat "under 200" test got wrong on a low-poly source — only the
    /// trailing submesh should go, leaving the subject's triangles untouched.
    /// </summary>
    [Fact]
    public void A_submesh_large_relative_to_its_mesh_is_kept_even_where_covered()
    {
        // 120 against a 400-triangle main part is 30% of the mesh — a body region, not a seam.
        Assert.Equal(TailTris, Removed(subjectTris: 120, Covering));
    }

    /// <summary>
    /// A null band list means the caller described no part layout at all, so there is nothing to test
    /// redundancy against and the shape-only judgement stands. Kept so the older Build overloads, which
    /// never had this information, behave as they always did.
    /// </summary>
    [Fact]
    public void No_band_information_falls_back_to_shape_alone()
    {
        Assert.Equal(TailTris + TailTris, Removed(subjectTris: TailTris, null));
    }
}
