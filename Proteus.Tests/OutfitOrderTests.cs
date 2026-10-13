using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// <see cref="BodyRetarget.KeepOutfitOrder"/> on flat sheets over a flat body: a "jacket" authored 3 mm off the skin over
/// a "bra" authored 0.5 mm off it, the bra's refit standing 5 mm off — where the jacket must go back outside it.
/// </summary>
public class OutfitOrderTests
{
    private const string SkinMaterial = "/mt_c0201b0001_bibo.mtrl";
    private const string ClothMaterial = "/mt_c0201e6255_top_a.mtrl";

    /// <summary>Sheet spacing (5 mm), as fine as the cells the relation is read in.</summary>
    private const float Step = 0.005f;

    /// <summary>Square grids of <paramref name="count"/> × <paramref name="count"/> points <see cref="Step"/> apart, one
    /// per height, facing +Z, each its own sheet.</summary>
    private static ModelParts Sheets(string material, int count, params float[] heights)
        => Grid(material, count, Step, 0f, heights);

    /// <inheritdoc cref="Sheets(string, int, float[])"/>
    /// <param name="step">How far apart the points are.</param>
    /// <param name="offsetX">Where along X the grid is centred.</param>
    private static ModelParts Grid(string material, int count, float step, float offsetX, params float[] heights)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();
        float half = (count - 1) * step / 2f;
        foreach (float z in heights)
        {
            int start = pos.Count / 3;
            for (int i = 0; i < count; i++)
            for (int k = 0; k < count; k++)
            {
                pos.AddRange([i * step - half + offsetX, k * step - half, z]);
                nrm.AddRange([0f, 0f, 1f]);
            }
            for (int i = 0; i + 1 < count; i++)
            for (int k = 0; k + 1 < count; k++)
            {
                int a = start + i * count + k, b = a + count, c = a + 1, d = b + 1;
                tris.AddRange([a, b, d, a, d, c]);
            }
        }
        return Model(material, pos.ToArray(), nrm.ToArray(), tris.ToArray());
    }

    private static ModelParts Model(string material, float[] p, float[] normals, int[] t)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int v = 0; v < p.Length / 3; v++)
        {
            var at = new Vector3(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }
        return new ModelParts
        {
            Positions = p,
            Normals = normals,
            MeshSpans = [new MeshSpan(0, 0, p.Length / 3)],
            Parts =
            [
                new ModelPart
                {
                    Mesh = 0, Submesh = 0, Island = -1, Label = "0.0", Material = material, Triangles = t,
                    Ordinals = Enumerable.Range(0, t.Length / 3).ToArray(), AttributeMask = 0, Min = min, Max = max,
                    Toggleable = true,
                },
            ],
            AttributeNames = [],
            Min = min,
            Max = max,
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
    }

    /// <summary>The body: a flat sheet of skin under everything, the same on both sides of the refit.</summary>
    private static BodyRetarget.SlotPair Body(bool shapeOnly = false)
    {
        var skin = Sheets(SkinMaterial, 81, 0f);
        Assert.True(IdentityCorrespondence.TryBuild(skin, skin, "chest", out var same, out string why), why);
        return new BodyRetarget.SlotPair("_top", same!, skin, ShapeOnly: shapeOnly);
    }

    private sealed record Run(BodyRetarget.Sets Sets, Vec3[] Delta, int Moved, float Residual);

    /// <summary>The pass alone, on a piece whose refit moved nothing: what it lifts is all its own doing.</summary>
    private static Run Keep(ModelParts piece, BodyRetarget.Outfit outfit, BodyRetarget.SlotPair? pair = null,
                            IReadOnlySet<int>? held = null, bool[]? stay = null, bool covered = false,
                            float gap = BodyRetarget.OutfitOrderGapDefault,
                            float fade = BodyRetarget.OutfitFadeDefault)
    {
        var sets = BodyRetarget.Sets.From(piece, held);
        var delta = new Vec3[sets.NodeCount];
        var layered = BodyRetarget.GarmentCloth(piece, sets);
        var cover = covered ? BodyRetarget.CoveredPoints(sets, layered, BodyRetarget.FacesOneWay(piece, sets)) : [];
        int moved = BodyRetarget.KeepOutfitOrder(sets, [pair ?? Body()], outfit, delta, stay, layered, cover,
                                                 out float residual, gap, fade);
        return new Run(sets, delta, moved, residual);
    }

    private static float HeightOf(ModelParts piece, Run run, int vertex)
        => piece.Positions[vertex * 3 + 2] + run.Delta[run.Sets.NodeOf[vertex]].Z;

    /// <summary>The vertex nearest the middle of the first sheet.</summary>
    private static int Middle(int count) => count / 2 * count + count / 2;

    [Fact]
    [Trait("Plan", "AC-outfit-fit-1")]
    public void A_piece_authored_outside_its_sibling_is_lifted_back_outside()
    {
        var jacket = Sheets(ClothMaterial, 21, 0.003f);
        var outfit = new BodyRetarget.Outfit(Sheets(ClothMaterial, 21, 0.0005f), Sheets(ClothMaterial, 21, 0.005f));

        var run = Keep(jacket, outfit);

        Assert.True(run.Moved > 0);
        // Outside the bra's refit by the gap kept: the author's margin was 2.5 mm, the kept gap is 1 mm.
        float middle = HeightOf(jacket, run, Middle(21));
        Assert.True(middle >= 0.005f + BodyRetarget.OutfitOrderGapDefault - 1e-4f, $"the middle is at {middle * 1000f:F2} mm");
        Assert.True(run.Residual <= 1e-4f, $"{run.Residual * 1000f:F2} mm short");
    }

    [Fact]
    [Trait("Plan", "AC-outfit-fit-1")]
    public void The_lift_is_capped_at_the_kept_gap()
    {
        // The author's margin is 2.5 mm; the lift goes to 1 mm outside the bra's refit, not 2.5.
        var jacket = Sheets(ClothMaterial, 21, 0.003f);
        var outfit = new BodyRetarget.Outfit(Sheets(ClothMaterial, 21, 0.0005f), Sheets(ClothMaterial, 21, 0.005f));

        var run = Keep(jacket, outfit);

        float middle = HeightOf(jacket, run, Middle(21));
        Assert.InRange(middle, 0.006f - 1e-4f, 0.006f + 1e-4f);
    }

    [Fact]
    [Trait("Plan", "EDGE-outfit-fit-1")]
    public void Cloth_the_author_had_touching_its_sibling_asks_nothing()
    {
        // 0.1 mm over the bra as authored: touching, not covering — nothing moves, however far the bra's refit rose.
        var jacket = Sheets(ClothMaterial, 21, 0.0006f);
        var outfit = new BodyRetarget.Outfit(Sheets(ClothMaterial, 21, 0.0005f), Sheets(ClothMaterial, 21, 0.005f));

        var run = Keep(jacket, outfit);

        Assert.Equal(0, run.Moved);
        Assert.All(run.Delta, d => Assert.Equal(default, d));
    }

    [Fact]
    [Trait("Plan", "EDGE-outfit-fit-2")]
    public void Held_and_kept_shape_nodes_never_move()
    {
        var jacket = Sheets(ClothMaterial, 21, 0.003f);
        var outfit = new BodyRetarget.Outfit(Sheets(ClothMaterial, 21, 0.0005f), Sheets(ClothMaterial, 21, 0.005f));
        int middle = Middle(21);
        var held = new HashSet<int> { middle };
        var sets = BodyRetarget.Sets.From(jacket, held);
        var stay = new bool[sets.NodeCount];
        stay[sets.NodeOf[middle + 1]] = true;

        var run = Keep(jacket, outfit, held: held, stay: stay);

        Assert.True(run.Moved > 0);
        Assert.Equal(default, run.Delta[run.Sets.NodeOf[middle]]);
        Assert.Equal(default, run.Delta[run.Sets.NodeOf[middle + 1]]);
        Assert.NotEqual(default, run.Delta[run.Sets.NodeOf[middle + 2]]);
    }

    [Fact]
    [Trait("Plan", "EDGE-outfit-fit-3")]
    public void Cloth_far_from_its_sibling_asks_nothing()
    {
        // The bra covers a 5 cm square in the middle of a 20 cm jacket: the jacket's corners, far past the fade, stay.
        var jacket = Sheets(ClothMaterial, 41, 0.003f);
        var outfit = new BodyRetarget.Outfit(Sheets(ClothMaterial, 11, 0.0005f), Sheets(ClothMaterial, 11, 0.005f));

        var run = Keep(jacket, outfit);

        Assert.True(run.Moved > 0);
        Assert.Equal(default, run.Delta[run.Sets.NodeOf[0]]);
        Assert.Equal(default, run.Delta[run.Sets.NodeOf[41 * 41 - 1]]);
        Assert.True(HeightOf(jacket, run, Middle(41)) >= 0.006f - 1e-4f);
    }

    [Fact]
    [Trait("Plan", "EDGE-outfit-fit-5")]
    [Trait("Plan", "AC-outfit-fit-2")]
    public void A_shape_only_pair_is_never_a_surface()
    {
        // A jacket worn as gloves carries the hands only for their shape; the bra is never read against them.
        var jacket = Sheets(ClothMaterial, 21, 0.003f);
        var outfit = new BodyRetarget.Outfit(Sheets(ClothMaterial, 21, 0.0005f), Sheets(ClothMaterial, 21, 0.005f));

        var shapeOnly = Keep(jacket, outfit, Body(shapeOnly: true));
        var body = Keep(jacket, outfit, Body());

        Assert.Equal(0, shapeOnly.Moved);
        Assert.All(shapeOnly.Delta, d => Assert.Equal(default, d));
        Assert.True(body.Moved > 0);
    }

    [Fact]
    [Trait("Plan", "EDGE-outfit-fit-6")]
    [Trait("Plan", "AC-outfit-fit-2")]
    public void No_outfit_or_NoOutfitOrder_leaves_the_refit_as_it_was()
    {
        var jacket = Sheets(ClothMaterial, 21, 0.003f);
        var outfit = new BodyRetarget.Outfit(Sheets(ClothMaterial, 21, 0.0005f), Sheets(ClothMaterial, 21, 0.005f));
        List<BodyRetarget.SlotPair> pairs = [Body()];

        var plain = BodyRetarget.Solve(jacket, pairs, "_glv");
        var off = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoOutfitOrder: true),
                                          () => BodyRetarget.Solve(jacket, pairs, "_glv", outfit: outfit));
        var on = BodyRetarget.Solve(jacket, pairs, "_glv", outfit: outfit);

        for (int v = 0; v < jacket.Positions.Length / 3; v++)
            Assert.Equal(plain.Edit.DeltaAt(v), off.Edit.DeltaAt(v));
        Assert.Equal(0, plain.Layered);
        Assert.Equal(0, off.Layered);
        Assert.True(on.Layered > 0);
    }

    [Fact]
    [Trait("Plan", "EDGE-outfit-fit-7")]
    public void Two_layers_of_the_piece_keep_their_order_under_the_lift()
    {
        // A lining 3 mm and a shell 4 mm off the skin, both over the bra: lifted, the shell stays 1 mm outside the lining.
        var jacket = Sheets(ClothMaterial, 21, 0.003f, 0.004f);
        var outfit = new BodyRetarget.Outfit(Sheets(ClothMaterial, 21, 0.0005f), Sheets(ClothMaterial, 21, 0.005f));

        var run = Keep(jacket, outfit, covered: true);

        int n = 21 * 21;
        for (int v = 0; v < n; v++)
        {
            float lining = HeightOf(jacket, run, v), shell = HeightOf(jacket, run, n + v);
            Assert.True(shell - lining >= 0.001f - 1e-4f,
                        $"vertex {v}: lining {lining * 1000f:F2} mm, shell {shell * 1000f:F2} mm");
        }
        Assert.True(HeightOf(jacket, run, Middle(21)) >= 0.006f - 1e-4f);
    }

    /// <summary>A sliver standing edge-on to the skin at a jacket's front edge, as on Geometric's: two corners 2 mm apart
    /// over the bra, the third 24 mm off, past it. Lifted unevenly it turns over.</summary>
    private static ModelParts Sliver()
        => Model(ClothMaterial, [0f, 0f, 0.003f, 0.024f, 0f, 0.003f, 0.002f, 0f, 0.0028f],
                 [0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f], [0, 1, 2]);

    /// <summary>A bra over x &lt; 5 mm only, authored 0.5 mm off the skin, refitted to stand 5 mm off.</summary>
    private static BodyRetarget.Outfit BraToTheLeft()
        => new(Grid(ClothMaterial, 11, Step, -0.02f, 0.0005f), Grid(ClothMaterial, 11, Step, -0.02f, 0.005f));

    [Fact]
    [Trait("Plan", "EDGE-outfit-fit-4")]
    public void A_triangle_the_lift_would_turn_over_has_its_low_corner_raised()
    {
        var sliver = Sliver();
        var run = Keep(sliver, BraToTheLeft());

        Assert.True(run.Moved > 0);
        Assert.Equal(0, BodyRetarget.FoldedTriangles(run.Sets, run.Delta));
        // The far corner, past the bra and the fade, rose with the others rather than holding them down.
        Assert.True(HeightOf(sliver, run, 1) >= HeightOf(sliver, run, 0) - 1e-4f,
                    $"far corner {HeightOf(sliver, run, 1) * 1000f:F2} mm, near {HeightOf(sliver, run, 0) * 1000f:F2} mm");
        Assert.True(run.Residual <= 1e-4f, $"{run.Residual * 1000f:F2} mm short");
    }

    [Fact]
    [Trait("Plan", "EDGE-outfit-fit-4")]
    public void A_fold_the_low_corner_cannot_be_raised_out_of_is_backed_off_and_reported()
    {
        // The far corner is a kept-shape piece: it never moves, so the fold guard holds the near corners back.
        var sliver = Sliver();
        var sets = BodyRetarget.Sets.From(sliver);
        var stay = new bool[sets.NodeCount];
        stay[sets.NodeOf[1]] = true;

        var run = Keep(sliver, BraToTheLeft(), stay: stay);

        Assert.Equal(0, BodyRetarget.FoldedTriangles(run.Sets, run.Delta));
        Assert.Equal(default, run.Delta[run.Sets.NodeOf[1]]);
        Assert.True(run.Residual > 0f, "what the guard held back is reported");
    }

    [Fact]
    [Trait("Plan", "EDGE-outfit-fit-8")]
    public void A_face_wider_than_a_cell_is_lifted_where_the_bra_comes_through_its_middle()
    {
        // A coarse jacket, its points 20 mm apart, over a bra strip 5 mm wide between them: no point of the jacket is over
        // the bra, but the middles of its edges are.
        var jacket = Grid(ClothMaterial, 5, 0.02f, 0.01f, 0.003f);
        var outfit = new BodyRetarget.Outfit(Grid(ClothMaterial, 2, Step, 0f, 0.0005f),
                                             Grid(ClothMaterial, 2, Step, 0f, 0.005f));

        var run = Keep(jacket, outfit);

        Assert.True(run.Moved > 0, "the edges over the bra should have asked for a lift");
    }

    [Fact]
    [Trait("Plan", "EDGE-outfit-fit-7")]
    public void Keeping_layer_order_lift_only_moves_the_cover_never_the_covered_point()
    {
        // A lining 3.0 mm and a shell 4.5 mm off the skin; the lining has come up 2 mm, through the shell.
        var jacket = Sheets(ClothMaterial, 11, 0.003f, 0.0045f);
        var sets = BodyRetarget.Sets.From(jacket);
        var layered = BodyRetarget.GarmentCloth(jacket, sets);
        var covered = BodyRetarget.CoveredPoints(sets, layered, BodyRetarget.FacesOneWay(jacket, sets));
        Assert.NotEmpty(covered);
        var delta = new Vec3[sets.NodeCount];
        for (int v = 0; v < 121; v++) delta[sets.NodeOf[v]] = new Vec3(0f, 0f, 0.002f);
        var lining = (Vec3[])delta.Clone();

        BodyRetarget.KeepLayerOrder(sets, covered, delta, null, layered, drawn: null, liftOnly: true);

        for (int v = 0; v < 121; v++) Assert.Equal(lining[sets.NodeOf[v]], delta[sets.NodeOf[v]]);
        float middleShell = 0.0045f + delta[sets.NodeOf[121 + 60]].Z, middleLining = 0.003f + delta[sets.NodeOf[60]].Z;
        Assert.True(middleShell - middleLining >= BodyRetarget.LayerOrderGap - 1e-4f,
                    $"shell {middleShell * 1000f:F2} mm over a lining at {middleLining * 1000f:F2} mm");
    }

    [Fact]
    [Trait("Plan", "EDGE-outfit-fit-7")]
    public void Lining_and_shell_lifted_unevenly_across_a_cell_boundary_stay_in_order()
    {
        // The shell's points sit half a cell along from the lining's, 1.5 mm above it, at the bra's edge where a short
        // fade (3 mm) lifts each by a very different share: the lining would come up through the shell.
        var lining = Grid(ClothMaterial, 21, Step, 0f, 0.003f);
        var shell = Grid(ClothMaterial, 21, Step, Step / 2f, 0.0045f);
        var jacket = Model(ClothMaterial, [.. lining.Positions, .. shell.Positions], [.. lining.Normals, .. shell.Normals],
                           [.. lining.Parts[0].Triangles, .. shell.Parts[0].Triangles.Select(i => i + 441)]);
        var run = Keep(jacket, BraToTheLeft(), covered: true, fade: 0.003f);
        Assert.True(run.Moved > 0);

        var layered = BodyRetarget.GarmentCloth(jacket, run.Sets);
        foreach (var c in BodyRetarget.CoveredPoints(run.Sets, layered, BodyRetarget.FacesOneWay(jacket, run.Sets)))
        {
            Vector3 Now(int n) => new Vector3(run.Sets.NodeAt[n].X, run.Sets.NodeAt[n].Y, run.Sets.NodeAt[n].Z)
                                  + new Vector3(run.Delta[n].X, run.Delta[n].Y, run.Delta[n].Z);
            var a = Now(c.A);
            var at = a * (1f - c.U - c.V) + Now(c.B) * c.U + Now(c.C) * c.V;
            float gap = (at - Now(c.Node)).Z;
            Assert.True(gap >= MathF.Min(c.Gap, BodyRetarget.LayerOrderGap) - 1e-4f,
                        $"node {c.Node}: {gap * 1000f:F2} mm under its cover, authored {c.Gap * 1000f:F2}");
        }
    }

    [Fact]
    [Trait("Plan", "AC-outfit-fit-1")]
    public void The_lift_fades_with_no_step()
    {
        // Past the bra's edge the lift fades over the fade distance: neighbours never differ by more than its slope.
        var jacket = Sheets(ClothMaterial, 41, 0.003f);
        var outfit = new BodyRetarget.Outfit(Sheets(ClothMaterial, 11, 0.0005f), Sheets(ClothMaterial, 11, 0.005f));

        var run = Keep(jacket, outfit);

        float need = 0.006f - 0.003f;
        float slope = need * 1.5f / BodyRetarget.OutfitFadeDefault;   // a smoothstep's steepest, per metre
        float worst = 0f;
        for (int v = 0; v < 41 * 41; v++)
        {
            if (v % 41 == 40) continue;
            worst = MathF.Max(worst, MathF.Abs(HeightOf(jacket, run, v) - HeightOf(jacket, run, v + 1)));
        }
        Assert.True(worst <= slope * Step + 1e-5f, $"neighbours {worst * 1000f:F2} mm apart, at most {slope * Step * 1000f:F2}");
    }
}
