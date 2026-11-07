using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// #86: with "Move skin too" on, an All-mode Move went about twice the drag on a real top — the guard's bound-skin check
/// measured the rider's height along the skin's blended vertex normal against a floor taken along another normal, so
/// where the two disagree (split normals, creases) it "lifted" cloth that skin and all had carried rigidly. The check now
/// judges each rider along its bound skin triangle's own face, at the operation's start and now.
/// </summary>
public partial class MeshVolumeSolveTests
{
    /// <summary>
    /// Tight cloth (1 mm, at the guard's floor) over a skin sheet whose authored vertex normals disagree with its faces,
    /// as split normals along a seam do: vertices tilted ±40° about Z in a checkerboard. Along such a normal the cloth
    /// measures cos 40° × 1 mm, under the floor — the old check's misfire on a translation.
    /// </summary>
    private static Layers SplitNormalLayers(float gap = 0.001f)
    {
        var l = Layered(11, gap, centred: true);
        var nrm = l.Model.Normals.ToArray();
        float s = MathF.Sin(0.7f), c = MathF.Cos(0.7f);
        for (int i = 0; i < 11; i++)
        for (int j = 0; j < 11; j++)
        {
            int v = l.SkinAt[i, j];
            float sign = (i + j) % 2 == 0 ? 1f : -1f;
            nrm[v * 3] = s * sign; nrm[v * 3 + 1] = c; nrm[v * 3 + 2] = 0f;
        }
        return l with { Model = WithNormals(l.Model, nrm) };
    }

    private static ModelParts WithNormals(ModelParts m, float[] normals) => new()
    {
        Positions = m.Positions, Normals = normals, MeshSpans = m.MeshSpans, Parts = m.Parts, AttributeNames = m.AttributeNames,
        Min = m.Min, Max = m.Max, ShatteredSubmeshes = m.ShatteredSubmeshes,
    };

    private static IEnumerable<int> EveryPart(ModelParts m) => m.Parts.Where(p => p.Island < 0).SelectMany(p => p.Triangles).Distinct();

    private static float OffRigid(ModelParts m, float[] after, Func<Vector3, Vector3> rigid)
        => EveryPart(m).Max(v => (At(after, v) - rigid(At(m.Positions, v))).Length());

    [Theory]
    [Trait("Plan", "AC-issue-sweep-c-2")]
    [Trait("Plan", "EDGE-issue-sweep-c-5")]
    [Trait("Plan", "EDGE-issue-sweep-c-8")]
    [Trait("Plan", "EDGE-issue-sweep-c-41")]
    [InlineData("move")]
    [InlineData("rotate")]
    public void AllMoveRotateWithSkinAreRigid(string op)
    {
        var l = SplitNormalLayers();
        var solve = SkinSolve(l.Model);
        var pivot = At(l.Model.Positions, l.SkinAt[5, 5]);
        Func<Vector3, Vector3> rigid = op == "move"
            ? p => p + new Vector3(0.003f, 0.015f, -0.004f)
            : p => Vector3.Transform(p, Matrix4x4.CreateRotationX(MathF.PI * 0.6f, pivot));   // past 90°: the sign holds

        Assert.True(solve.BeginMove(EveryPart(l.Model), adjacent: false, falloffRadius: 0f) > 0);
        if (op == "move") solve.MoveTo(new Vector3(0.003f, 0.015f, -0.004f));
        else solve.TransformTo(Matrix4x4.CreateRotationX(MathF.PI * 0.6f, pivot));
        Assert.True(solve.EndMove());

        var after = solve.Positions();
        Assert.True(OffRigid(l.Model, after, rigid) < 1e-5f, $"{op}: {OffRigid(l.Model, after, rigid) * 1000f:F3} mm off");
        Assert.True(solve.LastGuard == default, $"{op}: guard {solve.LastGuard}");
        // (A turn past 90° faces every triangle away from the author's, which Folded counts; exact to 10 µm, it folds none.)
        if (op == "move") Assert.Equal(0, Folded(l.Model, after));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-42")]
    public void BoundCheckSignFixedAtStartSurvivesAHalfTurn()
    {
        // Turned over completely, skin and cloth together: the face's way out turns with them, nothing is lifted.
        var l = SplitNormalLayers();
        var solve = SkinSolve(l.Model);
        var pivot = At(l.Model.Positions, l.SkinAt[5, 5]);
        var flip = Matrix4x4.CreateRotationZ(MathF.PI, pivot);
        solve.BeginMove(EveryPart(l.Model), adjacent: false, falloffRadius: 0f);
        solve.TransformTo(flip);
        Assert.True(solve.EndMove());
        Assert.Equal(default, solve.LastGuard);
        Assert.True(OffRigid(l.Model, solve.Positions(), p => Vector3.Transform(p, flip)) < 1e-5f);
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-c-3")]
    public void SkinPartMoveCarriesItsRidersNoFurtherThanTheDrag()
    {
        // Whole parts: only the skin row moved; its riders go with it, never further.
        var l = SplitNormalLayers();
        var solve = SkinSolve(l.Model);
        var drag = new Vector3(0f, 0.015f, 0f);
        Assert.True(solve.BeginMove(l.Model.Parts[1].Triangles, adjacent: false, falloffRadius: 0f) > 0);
        solve.MoveTo(drag);
        Assert.True(solve.EndMove());
        var after = solve.Positions();
        Assert.True(EveryPart(l.Model).Max(v => Moved(l.Model, after, v).Length()) <= drag.Length() + 1e-5f);
        Assert.Equal(0, Folded(l.Model, after));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-6")]
    [Trait("Plan", "EDGE-issue-sweep-c-8")]
    public void AMoveThatPokesSkinThroughStillLifts()
    {
        // Cloth 1 mm up with a vertex over every other skin vertex, each riding the skin straight under it; the skin
        // vertex between four of them moved up 4 mm through the face. The move made a poke — the guard lifts the face.
        var l = Layered(21, 0.001f, clothStride: 2);
        var solve = SkinSolve(l.Model);
        var middle = new List<int> { l.SkinAt[11, 11] };
        float before = l.ClothAt.Cast<int>().Max(v => At(l.Model.Positions, v).Y);
        Assert.True(solve.BeginMove(middle, adjacent: false, falloffRadius: 0f) > 0);
        solve.MoveTo(new Vector3(0f, 0.004f, 0f));
        Assert.True(solve.EndMove());
        Assert.True(solve.LastGuard.Face > 0, $"the poke was lifted: {solve.LastGuard}");
        Assert.True(l.ClothAt.Cast<int>().Max(v => At(solve.Positions(), v).Y) > before, "the cloth over the moved skin rose");
        Assert.Equal(0, Folded(l.Model, solve.Positions()));
        Assert.True(SkinThrough(l.Model, solve) == 0,
            $"{SkinThrough(l.Model, solve)} through after the guard {solve.LastGuard}, {solve.SkinStillOutside} left by it");
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-9")]
    public void TransferGuardKeepsTodaysFloor()
    {
        // A size transfer's guard is judged against the author's model, as before (relativeToStart: false).
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "Proteus", "Services", "MeshVolumeSolve.SkinMoves.cs"));
        Assert.Contains("try { KeepRidersAboveSkin(start, relativeToStart: false); }", src);
        Assert.Contains("if (KeepAboveBoundSkin(n, nodeDelta[n], out var kept))", src);
        Assert.Contains("float since = relativeToStart ? OutsideAtStart(s, ca, cb, cc, face, start) : float.NegativeInfinity;", src);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-41")]
    public void Bind_stores_the_triangle_and_keeps_heights()
    {
        // Cloth straight over skin vertices: bound in a vertex region, one corner with a share — the triangle is kept all
        // the same, its barycentrics summing to 1, and the height is the authored 3 mm.
        var l = Layered(11, 0.003f);
        var solve = new MeshVolumeSolve(l.Model) { MoveSkin = true };
        var inputs = solve.FollowInputs;
        var binding = SkinFollow.Bind(inputs);
        int rider = inputs.NodeOf[l.ClothAt[5, 5]];
        Assert.True(binding.CornersOf(rider).Count < 3);
        var t = binding.TriangleOf(rider);
        Assert.True(t.IsSet);
        Assert.Equal(1f, t.U + t.V + t.W, 1e-5f);
        Assert.Equal(0.003f, binding.AuthoredHeight(rider), 1e-5f);
        Assert.False(binding.TriangleOf(inputs.NodeOf[l.SkinAt[5, 5]]).IsSet);   // skin binds nothing
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-6")]
    [Trait("Plan", "EDGE-issue-sweep-c-41")]
    public void A_skin_move_that_sinks_a_loose_rider_lifts_it_to_its_floor()
    {
        // Cloth 15 mm up follows the skin only 84 % (FollowWeight): a 120 mm move of the skin sinks it 19 mm, through the
        // skin. The bound-skin check lifts it back to its floor (1 mm) above the triangle it rides.
        var l = Layered(11, 0.015f);
        var solve = SkinSolve(l.Model);
        Assert.True(solve.BeginMove(l.Model.Parts[1].Triangles, adjacent: false, falloffRadius: 0f) > 0);
        solve.MoveTo(new Vector3(0f, 0.12f, 0f));
        Assert.True(solve.EndMove());
        Assert.True(solve.LastGuard.Bound > 0, $"{solve.LastGuard}");
        int cloth = l.ClothAt[5, 5];
        Assert.Contains(solve.FollowInputs.NodeOf[cloth], solve.LastBoundLifted);
        var after = solve.Positions();
        Assert.True(At(after, cloth).Y - At(after, l.SkinAt[5, 5]).Y >= 0.001f - 1e-5f,
                    $"{(At(after, cloth).Y - At(after, l.SkinAt[5, 5]).Y) * 1000f:F2} mm above the skin");
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-49")]
    public void AllScaleWithSkinLiftsNoMoreThanItsHeightChange()
    {
        // A uniform scale is not rigid: heights grow with it. Cloth 3 mm up, scaled 1.2 about the middle with the skin:
        // anything the guard lifts, it lifts no more than the scale's own change of height (0.2 × 3 mm).
        var l = SplitNormalLayers(gap: 0.003f);
        var solve = SkinSolve(l.Model);
        var pivot = At(l.Model.Positions, l.SkinAt[5, 5]);
        solve.BeginMove(EveryPart(l.Model), adjacent: false, falloffRadius: 0f);
        solve.TransformTo(Matrix4x4.CreateScale(1.2f, pivot));
        Assert.True(solve.EndMove());
        Assert.True(solve.LastGuard.WorstMm <= 0.2f * 3f + 1e-3f, $"{solve.LastGuard}");
        Assert.Equal(0, Folded(l.Model, solve.Positions()));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-52")]
    public void DegenerateBoundTriangleSkipsBoundCheckAndRigidMoveLiftsNothing()
    {
        // A skin vertex welded onto its neighbour leaves zero-area triangles there; riders bound to one keep no triangle
        // (the bound check skips them) and a rigid move of everything lifts nothing.
        var l = Layered(11, 0.003f);
        var pos = l.Model.Positions.ToArray();
        int from = l.SkinAt[5, 6], onto = l.SkinAt[5, 5];
        for (int k = 0; k < 3; k++) pos[from * 3 + k] = pos[onto * 3 + k];
        var model = new ModelParts
        {
            Positions = pos, Normals = l.Model.Normals, MeshSpans = l.Model.MeshSpans, Parts = l.Model.Parts,
            AttributeNames = l.Model.AttributeNames, Min = l.Model.Min, Max = l.Model.Max, ShatteredSubmeshes = l.Model.ShatteredSubmeshes,
        };
        var solve = SkinSolve(model);
        solve.BeginMove(EveryPart(model), adjacent: false, falloffRadius: 0f);
        solve.MoveTo(new Vector3(0.002f, 0.01f, 0.003f));
        Assert.True(solve.EndMove());
        Assert.Equal(default, solve.LastGuard);
        Assert.True(EveryPart(model).Max(v => (At(solve.Positions(), v) - At(pos, v) - new Vector3(0.002f, 0.01f, 0.003f)).Length()) < 1e-5f);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!, "..");
}
