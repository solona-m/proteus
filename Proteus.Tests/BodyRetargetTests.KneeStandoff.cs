using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The knee standoff's pass (<see cref="BodyRetarget.ClearFaces(BodyRetarget.FaceCheck, SecondSkinWriter.Vec3[], float[], ref float, bool[], BodyRetarget.FaceGoal, out BodyRetarget.KneeTally)"/>
/// with a goal): only the goal's faces, held a standoff off the skin, too deep only by the skin's own depth, every corner
/// within the goal's budget, counted by its own tally. Scenes: the coarse sheet over the bumped cube.
/// </summary>
public partial class BodyRetargetTests
{
    private static BodyRetarget.FaceGoal Goal(params int[] faces) => new(faces.ToHashSet(), BodyRetarget.KneeStandoff, BodyRetarget.KneeBudget);

    [Fact]
    [Trait("Plan", "AC-knee-standoff-4")]
    [Trait("Plan", "EDGE-knee-standoff-4")]
    public void A_knee_face_is_held_its_standoff_off_the_skin()
    {
        // The bump half a millimetre under the middle of a coarse face: clear, but nearer than the standoff.
        var garment = CoarseSheet();
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.0005f));
        Assert.Equal(0f, check.Need(check.Faces[0], delta, 1e-4f).Push);   // the face settle has nothing to do
        Assert.True(check.NeedOff(check.Faces[0], delta, BodyRetarget.KneeStandoff).Push > BodyRetarget.KneeStandoff - 0.001f);

        float worst = 0f;
        BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst, null, Goal(0), out var tally);
        Assert.Equal(0f, check.NeedOff(check.Faces[0], delta, BodyRetarget.KneeStandoff - 1e-4f).Push);
        Assert.True(Moved(delta) <= BodyRetarget.KneeBudget + 1e-6f, $"a corner moved {Moved(delta) * 1000:F2} mm");
        Assert.Equal(3, tally.Moved);
        Assert.Equal(Moved(delta), tally.Worst, 5);

        // Already a standoff clear: nothing moves.
        var high = CoarseSheet(0.2f + BodyRetarget.KneeStandoff + 0.001f);
        var (check2, delta2, sets2) = FaceSetup(high, Cube(0.20f, SkinMaterial), Bumped(0f));
        BodyRetarget.ClearFaces(check2, delta2, new float[sets2.NodeCount], ref worst, null, Goal(0), out var none);
        Assert.All(delta2, d => Assert.Equal(default, d));
        Assert.Equal(0, none.Moved);
    }

    [Fact]
    [Trait("Plan", "EDGE-knee-standoff-3")]
    public void Skin_deeper_than_a_knee_face_can_read_is_not_chased()
    {
        // 7.5 mm through a coarse face whose cap is 7 mm: the skin's own depth is past the cap (the standoff not added).
        var garment = CoarseSheet();
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.0085f));
        var traced = new List<string>();
        BodyRetarget.FaceTrace.Value = (_, why) => traced.Add(why);
        try
        {
            float worst = 0f;
            BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst, null, Goal(0), out var tally);
            Assert.Equal(0, tally.Moved);
        }
        finally { BodyRetarget.FaceTrace.Value = null; }
        Assert.Contains("knee-too-deep", traced);
        Assert.All(delta, d => Assert.Equal(default, d));
    }

    [Fact]
    [Trait("Plan", "EDGE-knee-standoff-9")]
    public void The_knee_pass_keeps_to_its_goal_faces()
    {
        // Strips 1 mm over the cube: every face is nearer the skin than the standoff, but only face 0 is the goal's. Its
        // corners move; no face outside the goal is pushed in any round, so every other node stays.
        // Two coarse faces 1 mm over the cube, their shared diagonal through the face's centre (the one skin point inside
        // them, which both see on their shared edge).
        float z = SheetZ;
        float[] pos = [-0.15f, -0.15f, z, 0.15f, -0.15f, z, 0.15f, 0.15f, z, -0.15f, 0.15f, z];
        float[] nrm = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1];
        var garment = Build(pos, nrm, [0, 1, 2, 0, 2, 3], ClothMaterial);
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.0005f));
        // The goal: the first face with skin under it nearer than the standoff (most are; some hold no skin point).
        int g = Enumerable.Range(0, check.Faces.Count).First(f => check.NeedOff(check.Faces[f], delta, BodyRetarget.KneeStandoff).Push > 0f);
        int others = Enumerable.Range(0, check.Faces.Count).Count(f => f != g && check.NeedOff(check.Faces[f], delta, BodyRetarget.KneeStandoff).Push > 0f);
        Assert.True(others > 0, "the scene needs faces outside the goal that would ask to move");
        var pushed = new List<(int, int, int)>();
        BodyRetarget.FaceTrace.Value = (face, why) => { if (why == "knee-pushed") pushed.Add(face); };
        try
        {
            float worst = 0f;
            BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst, null, Goal(g), out _);
        }
        finally { BodyRetarget.FaceTrace.Value = null; }
        var goalFace = check.Faces[g];
        var own = new HashSet<int> { goalFace.A, goalFace.B, goalFace.C };
        Assert.All(pushed, f => Assert.True(own.SetEquals([f.Item1, f.Item2, f.Item3])));
        for (int n = 0; n < delta.Length; n++)
            if (!own.Contains(n) && check.WallPartner[n] < 0) Assert.Equal(default, delta[n]);
        Assert.True(own.Any(n => delta[n] != default));
    }

    [Fact]
    [Trait("Plan", "AC-knee-standoff-6")]
    public void The_knee_tally_counts_what_the_pass_moved_whatever_moved_before()
    {
        // Corners an earlier pass already pushed: the face settle's "newly" skips them; the knee tally does not.
        var garment = CoarseSheet();
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.0005f));
        var pushedBy = Enumerable.Repeat(0.001f, sets.NodeCount).ToArray();
        float worst = 0f;
        int newly = BodyRetarget.ClearFaces(check, delta, pushedBy, ref worst, null, Goal(0), out var tally);
        Assert.Equal(0, newly);
        Assert.Equal(3, tally.Moved);
        Assert.True(tally.Worst > BodyRetarget.KneeStandoff - 0.001f && tally.Worst <= BodyRetarget.KneeBudget + 1e-6f, $"{tally.Worst * 1000:F2} mm");
    }

    [Fact]
    [Trait("Plan", "EDGE-knee-standoff-5")]
    public void Held_points_stay_through_the_knee_pass()
    {
        var garment = CoarseSheet();
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.0005f));
        var stay = new bool[sets.NodeCount];
        stay[check.Faces[0].A] = true;
        float worst = 0f;
        BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst, stay, Goal(0), out var tally);
        Assert.Equal(default, delta[check.Faces[0].A]);
        Assert.True(Moved(delta) <= BodyRetarget.KneeBudget + 1e-6f);
        Assert.Equal(2, tally.Moved);
    }

    [Fact]
    [Trait("Plan", "AC-knee-standoff-3")]
    [Trait("Plan", "AC-knee-standoff-5")]
    [Trait("Plan", "EDGE-knee-standoff-1")]
    [Trait("Plan", "EDGE-knee-standoff-10")]
    public void A_body_without_knee_bones_leaves_the_garment_as_it_was()
    {
        // The cube carries no knee bones: no knee band, no pass — the same deltas as with the knob, bit for bit.
        var garment = CoarseSheet();
        var now = Solve(garment, Cube(0.20f, SkinMaterial), Bumped(0.0005f));
        var knob = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoKneeStandoff: true),
                                           () => Solve(garment, Cube(0.20f, SkinMaterial), Bumped(0.0005f)));
        for (int v = 0; v < garment.Positions.Length / 3; v++) Assert.Equal(knob.Edit.DeltaAt(v), now.Edit.DeltaAt(v));
        Assert.Equal(0, now.KneeMoved);
    }

    // ── which faces the pass takes (KneeFaces) ──────────────────────────────

    /// <summary>A sheet 1 mm under the cube's bottom (-z) face, its way out down: the back of a leg, as the pass reads it.</summary>
    private static ModelParts UnderSheet(float half = 0.15f)
    {
        float z = -SheetZ;
        float[] pos = [-half, -half, z, 0f, half, z, half, -half, z];
        float[] nrm = [0, 0, -1, 0, 0, -1, 0, 0, -1];
        return Build(pos, nrm, [0, 1, 2], ClothMaterial);
    }

    [Fact]
    [Trait("Plan", "AC-knee-standoff-3")]
    [Trait("Plan", "EDGE-knee-standoff-2")]
    [Trait("Plan", "EDGE-knee-standoff-9")]
    [Trait("Plan", "EDGE-knee-standoff-17")]
    public void The_pass_takes_coarse_faces_over_the_knee_band_that_look_back()
    {
        // Looking back (its way out, the skin under it, points down -z here), coarse, over the band: taken.
        var (back, delta, _) = FaceSetup(UnderSheet(), Cube(0.20f, SkinMaterial), Cube(0.20f, SkinMaterial));
        Assert.Equal([0], BodyRetarget.KneeFaces(back, delta, () => _ => true)!.Faces);
        // Not over the band: none.
        Assert.Null(BodyRetarget.KneeFaces(back, delta, () => _ => false));
        // No body carries the bones: none.
        Assert.Null(BodyRetarget.KneeFaces(back, delta, () => null));
        // Only its centroid over the band: taken (any sample counts).
        var centroid = (back.At(back.Faces[0].A, delta) + back.At(back.Faces[0].B, delta) + back.At(back.Faces[0].C, delta)) / 3f;
        Assert.NotNull(BodyRetarget.KneeFaces(back, delta, () => p => Vector3.Distance(p, centroid) < 1e-4f));

        // Looking forward (the front of the knee, +z): none, and the band is never read.
        var (front, frontDelta, _) = FaceSetup(CoarseSheet(), Cube(0.20f, SkinMaterial), Cube(0.20f, SkinMaterial));
        int asked = 0;
        Assert.Null(BodyRetarget.KneeFaces(front, frontDelta, () => { asked++; return _ => true; }));
        Assert.Equal(0, asked);

        // A fine face (20 mm) looking back: not coarse, none.
        var (fine, fineDelta, _) = FaceSetup(UnderSheet(0.010f), Cube(0.20f, SkinMaterial), Cube(0.20f, SkinMaterial));
        Assert.Null(BodyRetarget.KneeFaces(fine, fineDelta, () => _ => true));
    }

    // ── the face settle's guards, on the goal path ──────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-knee-standoff-6")]
    public void The_knee_pass_turns_nothing_over()
    {
        // The pleat scene of Clearing_faces_never_turns_a_neighbour_over: lifting the big face's near edge by the standoff
        // tips the pleat onto its back, so those corners are put back; nothing ends turned over.
        float[] pos =
        [
            -0.15f, -0.15f, SheetZ, 0.15f, -0.15f, SheetZ, 0f, 0.15f, SheetZ,
            0f, -0.1505f, SheetZ + 0.001f,
        ];
        float[] nrm = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, -1, 0];
        var garment = Build(pos, nrm, [0, 1, 2, 1, 0, 3], ClothMaterial);
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.0005f));
        float worst = 0f;
        BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst, null, Goal(0), out _);
        Assert.Equal(0, Folded(garment, delta, sets));
    }

    [Fact]
    [Trait("Plan", "EDGE-knee-standoff-7")]
    public void The_knee_pass_never_pushes_through_the_layer_over_it()
    {
        // A lining 1 mm over the cube and a shell 1.5 mm over the lining: holding the lining 4 mm off the skin would take
        // it through the shell, so the layer guard puts it back.
        const float shellZ = SheetZ + 0.0015f;
        float[] pos =
        [
            -0.15f, -0.15f, SheetZ, 0.15f, -0.15f, SheetZ, 0f, 0.15f, SheetZ,
            -0.18f, -0.18f, shellZ, 0.18f, -0.18f, shellZ, 0f, 0.18f, shellZ,
        ];
        float[] nrm = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1];
        var garment = Build(pos, nrm, [0, 1, 2, 3, 4, 5], ClothMaterial);
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.0005f));
        float worst = 0f;
        BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst, null, Goal(0), out _);
        for (int v = 0; v < 3; v++)
        {
            float z = At(garment, v).Z + delta[sets.NodeOf[v]].Z;
            Assert.True(z < shellZ, $"lining corner {v} went through the shell, to z={z:F5}");
        }
    }
}
