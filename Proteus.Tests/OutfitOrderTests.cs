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
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();
        float half = (count - 1) * Step / 2f;
        foreach (float z in heights)
        {
            int start = pos.Count / 3;
            for (int i = 0; i < count; i++)
            for (int k = 0; k < count; k++)
            {
                pos.AddRange([i * Step - half, k * Step - half, z]);
                nrm.AddRange([0f, 0f, 1f]);
            }
            for (int i = 0; i + 1 < count; i++)
            for (int k = 0; k + 1 < count; k++)
            {
                int a = start + i * count + k, b = a + count, c = a + 1, d = b + 1;
                tris.AddRange([a, b, d, a, d, c]);
            }
        }
        var p = pos.ToArray();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int v = 0; v < p.Length / 3; v++)
        {
            var at = new Vector3(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }
        var t = tris.ToArray();
        return new ModelParts
        {
            Positions = p,
            Normals = nrm.ToArray(),
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
                            float gap = BodyRetarget.OutfitOrderGapDefault)
    {
        var sets = BodyRetarget.Sets.From(piece, held);
        var delta = new Vec3[sets.NodeCount];
        var layered = BodyRetarget.GarmentCloth(piece, sets);
        var cover = covered ? BodyRetarget.CoveredPoints(sets, layered, BodyRetarget.FacesOneWay(piece, sets)) : [];
        int moved = BodyRetarget.KeepOutfitOrder(sets, [pair ?? Body()], outfit, delta, stay, layered, cover,
                                                 out float residual, gap);
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
