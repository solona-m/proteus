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
    /// Tight cloth (3 mm) over a skin sheet whose authored vertex normals disagree with its faces, as split normals along
    /// a seam do: alternate columns tilted ±40° about Z. Cloth is bound in vertex, edge and face regions alike.
    /// </summary>
    private static Layers SplitNormalLayers()
    {
        var l = Layered(11, 0.003f, centred: true);
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

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!, "..");
}
