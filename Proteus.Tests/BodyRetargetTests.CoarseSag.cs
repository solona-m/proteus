using System;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The face clearing's cap for coarse cloth (see <see cref="BodyRetarget.ClearFaceCap(float)"/>): a face may ask for the
/// sagitta of its longest edge over a 50 mm curve, never less than the old 2 mm and never more than 12 mm, and its corners
/// move no further in all than that plus 1 mm. "Starlight"'s stockings, faces 40-80 mm across, had the rounder bodies 2-6
/// mm up through their middles with every corner clear. Scenes: the 300 mm <see cref="Sheet"/> over the bumped cube (a
/// coarse face, cap 12 mm), or a small face of 20 mm (cap the old 2 mm).
/// </summary>
public partial class BodyRetargetTests
{
    /// <summary>The 300 mm sheet the other face-clearing tests use: longest edge 335 mm, cap 12 mm.</summary>
    private static ModelParts CoarseSheet(float z = SheetZ)
        => Sheet(new(-0.15f, -0.15f, z), new(0.15f, -0.15f, z), new(0f, 0.15f, z));

    /// <summary>A face of 20 mm across centred over the bump at height <paramref name="z"/>: cap the old 2 mm.</summary>
    private static ModelParts FineFace(float z)
        => Sheet(new(-0.010f, -0.00577f, z), new(0.010f, -0.00577f, z), new(0f, 0.01155f, z));

    private static Vector3 Corner(ModelParts garment, SecondSkinWriter.Vec3[] delta, BodyRetarget.Sets sets, int v)
    {
        var d = delta[sets.NodeOf[v]];
        return At(garment, v) + new Vector3(d.X, d.Y, d.Z);
    }

    private static float Moved(SecondSkinWriter.Vec3[] delta) => delta.Max(d => new Vector3(d.X, d.Y, d.Z).Length());

    [Fact]
    [Trait("Plan", "AC-starlight-bottoms-1")]
    public void Clearing_faces_lifts_a_coarse_face_sagging_deeper_than_two_mm()
    {
        // The bump 3 mm through the middle of a coarse face, every corner clear: the stockings' sag, which the old 2 mm
        // cap left standing through them.
        var garment = CoarseSheet();
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.004f));
        float need = check.Need(check.Faces[0], delta).Push;
        float cap = BodyRetarget.ClearFaceCap(check.LongestEdge(check.Faces[0], delta));
        Assert.True(need > BodyRetarget.ClearFaceDeepest && need <= cap, $"the face asks {need * 1000:F2} mm, cap {cap * 1000:F2}");

        float worst = 0f;
        Assert.Equal(3, BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst));

        Assert.Equal(0f, check.Need(check.Faces[0], delta, 1e-4f).Push);
        Assert.True(Moved(delta) <= BodyRetarget.ClearFaceBudget(cap) + 1e-6f, $"a corner moved {Moved(delta) * 1000:F2} mm");
    }

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-1")]
    [Trait("Plan", "AC-starlight-bottoms-4")]
    public void A_face_of_twenty_millimetres_keeps_the_two_millimetre_cap()
    {
        // The deep-through rule for fine cloth (a corset's rim, Sirius): a 20 mm face the skin is 3 mm through is not
        // sagging, and is left — with or without the coarse cap, bit for bit.
        var garment = FineFace(0.2015f);
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.0045f));
        Assert.True(check.Need(check.Faces[0], delta).Push > BodyRetarget.ClearFaceDeepest + 0.001f);

        float worst = 0f;
        Assert.Equal(0, BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst));
        Assert.All(delta, d => Assert.Equal(default, d));

        var (check2, delta2, sets2) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.0045f));
        BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoCoarseSag: true),
            () => BodyRetarget.ClearFaces(check2, delta2, new float[sets2.NodeCount], ref worst));
        Assert.Equal(delta, delta2);

        Assert.Equal(BodyRetarget.ClearFaceDeepest, BodyRetarget.ClearFaceCap(0.020f));
        Assert.Equal(BodyRetarget.ClearFaceDeepest, BodyRetarget.ClearFaceCap(0.020f, 0.0255f));
        Assert.Equal(BodyRetarget.ClearFaceMost, BodyRetarget.ClearFaceBudget(BodyRetarget.ClearFaceDeepest));
    }

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-29")]
    [Trait("Plan", "AC-starlight-bottoms-4")]
    public void The_exact_floor_branch_is_float_safe()
    {
        static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
        int floor = Bits(BodyRetarget.ClearFaceDeepest), most = Bits(BodyRetarget.ClearFaceMost);
        // The edge whose sag at the floor radius is the 2 mm floor itself, to the float.
        float atFloor = MathF.Sqrt(8f * 0.0255f * BodyRetarget.ClearFaceDeepest);
        foreach (float edge in new[] { 0.0199f, 0.020f, atFloor, 0f })
        {
            float cap = BodyRetarget.ClearFaceCap(edge, 0.0255f);
            Assert.Equal(floor, Bits(cap));
            Assert.Equal(most, Bits(BodyRetarget.ClearFaceBudget(cap)));
            Assert.Equal(floor, Bits(BodyRetarget.ClearFaceCap(edge)));
        }

        // A face of 20 mm the skin is 0.9 mm through is cleared exactly as the old rule clears it.
        var garment = FineFace(0.2040f);
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.0045f));
        float need = check.Need(check.Faces[0], delta).Push;
        Assert.True(need > 0f && need <= BodyRetarget.ClearFaceDeepest, $"the face asks {need * 1000:F2} mm");
        var (check2, delta2, sets2) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.0045f));
        float worst = 0f;
        BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst);
        BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoCoarseSag: true),
            () => BodyRetarget.ClearFaces(check2, delta2, new float[sets2.NodeCount], ref worst));
        Assert.Contains(delta, d => d.Z > 0f);
        Assert.Equal(delta, delta2);
    }

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-2")]
    public void Clearing_faces_leaves_a_coarse_face_deeper_than_its_sag()
    {
        // A face of 40 mm (cap 4 mm) the bump is 6 mm through, every corner clear: more than it can sag, so it is left,
        // as the old rule left 3 mm. (The face test reads skin no deeper than ClearDepth, 8 mm, under any face.)
        var garment = Sheet(new(-0.020f, -0.01155f, 0.2536f), new(0.020f, -0.01155f, 0.2536f), new(0f, 0.0231f, 0.2536f));
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.06f));
        float need = check.Need(check.Faces[0], delta).Push;
        float cap = BodyRetarget.ClearFaceCap(check.LongestEdge(check.Faces[0], delta));
        Assert.True(need > cap, $"the face asks {need * 1000:F2} mm, cap {cap * 1000:F2}");

        float worst = 0f;
        Assert.Equal(0, BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst));
        Assert.All(delta, d => Assert.Equal(default, d));
    }

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-28")]
    public void The_sag_cap_follows_the_sagitta()
    {
        Assert.True(BodyRetarget.ClearFaceSagRadius >= 0.0255f);
        foreach (float edge in new[] { 0.020f, 0.040f, 0.060f, 0.080f, 0.120f })
        {
            float expected = MathF.Max(BodyRetarget.ClearFaceDeepest,
                MathF.Min(BodyRetarget.ClearFaceSagMax, edge * edge / (8f * BodyRetarget.ClearFaceSagRadius)));
            Assert.Equal(expected, BodyRetarget.ClearFaceCap(edge), 6);
        }
        Assert.Equal(0.004f, BodyRetarget.ClearFaceCap(0.040f), 6);
        Assert.Equal(BodyRetarget.ClearFaceSagMax, BodyRetarget.ClearFaceCap(0.120f));
        Assert.Equal(BodyRetarget.ClearFaceCap(0.060f) + 0.001f, BodyRetarget.ClearFaceBudget(BodyRetarget.ClearFaceCap(0.060f)), 6);
    }

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-3")]
    public void A_fine_neighbour_never_carries_a_node_past_three_millimetres()
    {
        // A fine face over the bump's top sharing a corner with a coarse face far clear of the skin: the fine face asks
        // for its sag, and its corners — the shared one too — move no further than the old 3 mm in all.
        const float z = 0.2040f;
        float[] pos =
        [
            -0.010f, -0.00577f, z, 0.010f, -0.00577f, z, 0f, 0.01155f, z,
            0.15f, -0.15f, z + 0.02f, 0.15f, 0.15f, z + 0.02f,
        ];
        float[] nrm = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1];
        var garment = Build(pos, nrm, [0, 1, 2, 1, 3, 4], ClothMaterial);
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.0045f));

        float worst = 0f;
        BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst);

        Assert.Contains(delta, d => d.Z > 0f);
        Assert.True(Moved(delta) <= BodyRetarget.ClearFaceMost + 1e-6f, $"a corner moved {Moved(delta) * 1000:F2} mm");
    }

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-4")]
    public void Clearing_a_coarse_face_never_pushes_a_lining_through_its_shell()
    {
        // The lining 3 mm through, the shell 3.5 mm over it and clear of the bump: lifting the lining's face the 4 mm
        // it asks would take it through the shell. The layer guard puts it back, as at 2 mm.
        const float shellZ = SheetZ + 0.0035f;
        float[] pos =
        [
            -0.15f, -0.15f, SheetZ, 0.15f, -0.15f, SheetZ, 0f, 0.15f, SheetZ,
            -0.18f, -0.18f, shellZ, 0.18f, -0.18f, shellZ, 0f, 0.18f, shellZ,
        ];
        float[] nrm = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1];
        var garment = Build(pos, nrm, [0, 1, 2, 3, 4, 5], ClothMaterial);
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.004f));

        float worst = 0f;
        BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst);

        for (int v = 3; v < 6; v++) Assert.Equal(default, delta[sets.NodeOf[v]]);
        for (int v = 0; v < 3; v++)
            Assert.True(Corner(garment, delta, sets, v).Z < shellZ, $"lining corner {v} went through the shell");

        var (check2, delta2, sets2) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.004f));
        BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoLayerGuard: true),
            () => BodyRetarget.ClearFaces(check2, delta2, new float[sets2.NodeCount], ref worst));
        Assert.Contains(Enumerable.Range(0, 3), v => Corner(garment, delta2, sets2, v).Z > shellZ);
    }

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-5")]
    public void Clearing_a_coarse_face_never_turns_a_neighbour_over()
    {
        // The pleat of Clearing_faces_never_turns_a_neighbour_over, the bump 3 mm through the coarse face: lifting the
        // edge's corners 4 mm tips the pleat onto its back, so they stay.
        float[] pos =
        [
            -0.15f, -0.15f, SheetZ, 0.15f, -0.15f, SheetZ, 0f, 0.15f, SheetZ,
            0f, -0.1505f, SheetZ + 0.001f,
        ];
        float[] nrm = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, -1, 0];
        var garment = Build(pos, nrm, [0, 1, 2, 1, 0, 3], ClothMaterial);
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.004f));

        float worst = 0f;
        BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst);

        Assert.Equal(0, Folded(garment, delta, sets));
        Assert.Equal(default, delta[sets.NodeOf[0]]);
        Assert.Equal(default, delta[sets.NodeOf[1]]);
    }

    // EDGE-starlight-bottoms-6, a thick shell's walls: WallPairTests.A_coarse_shell_clears_a_deeper_sag_with_both_walls_within_its_budget.

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-7")]
    public void Clearing_a_coarse_face_leaves_what_it_is_told_to_keep()
    {
        var garment = CoarseSheet();
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.004f));
        var stay = new bool[sets.NodeCount];
        stay[sets.NodeOf[2]] = true;

        float worst = 0f;
        BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst, stay);

        Assert.Equal(default, delta[sets.NodeOf[2]]);
        Assert.True(delta[sets.NodeOf[0]].Z > BodyRetarget.ClearFaceDeepest, "the free corners should go past 2 mm");
    }

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-13")]
    public void No_coarse_sag_restores_the_old_cap()
    {
        var garment = CoarseSheet();
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.004f));
        float worst = 0f;
        int pushed = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoCoarseSag: true),
            () => BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst));
        Assert.Equal(0, pushed);
        Assert.All(delta, d => Assert.Equal(default, d));
    }
}
