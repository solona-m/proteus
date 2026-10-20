using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Gui;
using Proteus.Services;
using Xunit;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// The Scale tool's one-axis stretch, through the solve: where it moves the part, and the exact normals it leaves —
/// the inverse-transpose a stretch really gives, which no per-node turn of the author's normals can.
/// </summary>
public partial class MeshVolumeSolveTests
{
    private static readonly Vector3 BoxCentre = new(0.5f, 0.1f, 0.5f);

    /// <summary>
    /// A hard-edged 40 mm box — 24 vertices, each face its own four with its own flat normal, so every corner is
    /// one welded node carrying three different normals — as part 1.1, and a flat 5×5 sheet half a metre away as 1.2.
    /// </summary>
    private static (ModelParts Model, int BoxVertices) HardBox()
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();
        const float h = 0.02f;
        (Vector3 N, Vector3 U, Vector3 V)[] faces =
        [
            (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
            (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX),
            (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
            (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY),
            (-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX),
        ];
        foreach (var (n, u, v) in faces)
        {
            int start = pos.Count / 3;
            foreach (var (su, sv) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                var p = BoxCentre + (n + u * su + v * sv) * h;
                pos.AddRange([p.X, p.Y, p.Z]);
                nrm.AddRange([n.X, n.Y, n.Z]);
            }
            tris.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
        int boxVertices = pos.Count / 3;
        var far = new List<int>();
        Sheet(5, (_, _) => 0f, 1f, pos, nrm, far);
        return (Assemble(pos, nrm, tris, far.ToArray()), boxVertices);
    }

    private static readonly Vector3 Tilted = Vector3.Normalize(new Vector3(1f, 1f, 0f));

    /// <summary>
    /// A 7×7 sheet leaning 45° in XY, its normals <see cref="Tilted"/> — the faces a stretch along Y must lean
    /// further over — as part 1.1, and a flat 5×5 sheet half a metre away as 1.2.
    /// </summary>
    private static (ModelParts Model, int SheetVertices, Vector3 Pivot) TiltedSheet()
    {
        const int n = 7;
        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();
        var along = Vector3.Normalize(new Vector3(1f, -1f, 0f));
        var origin = new Vector3(0.5f, 0.2f, 0.5f);
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            var p = origin + along * (i * Spacing) + Vector3.UnitZ * (j * Spacing);
            pos.AddRange([p.X, p.Y, p.Z]);
            nrm.AddRange([Tilted.X, Tilted.Y, Tilted.Z]);
        }
        for (int i = 0; i + 1 < n; i++)
        for (int j = 0; j + 1 < n; j++)
        {
            int a = i * n + j, b = (i + 1) * n + j, c = i * n + j + 1, d = (i + 1) * n + j + 1;
            tris.AddRange([a, b, d, a, d, c]);
        }
        var far = new List<int>();
        Sheet(5, (_, _) => 0f, 1f, pos, nrm, far);
        var pivot = origin + along * (3 * Spacing) + Vector3.UnitZ * (3 * Spacing);
        return (Assemble(pos, nrm, tris, far.ToArray()), n * n, pivot);
    }

    private static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);

    private static void Drag(MeshVolumeSolve solve, int[] partTriangles, Matrix4x4 transform,
                             bool adjacent = false, float falloff = 0f)
    {
        Assert.True(solve.BeginMove(partTriangles, adjacent, falloff) > 0);
        solve.TransformTo(Matrix4x4.Identity);
        solve.TransformTo(transform);
        solve.EndMove();
    }

    private static void AssertNormal(Vector3 expected, Vec3 actual, float tolerance, string what)
        => Assert.True(Vector3.Distance(expected, V(actual)) < tolerance, $"{what}: expected {expected}, got {V(actual)}");

    /// <summary>The normals the solve would give with no exact records — the turned normals of its own edit.</summary>
    private static Vec3[] TurnedNormalsOf(MeshVolumeSolve solve, ModelParts model)
    {
        int vc = model.Positions.Length / 3;
        var basePos = new Vec3[vc];
        var baseNrm = new Vec3[vc];
        var delta = new Vec3[vc];
        for (int i = 0; i < vc; i++)
        {
            basePos[i] = solve.AuthoredPositionAt(i);
            baseNrm[i] = solve.AuthoredNormalAt(i);
            delta[i] = solve.DeltaAt(i);
        }
        var nodeOf = MeshMath.WeldByPosition(basePos, out int nodeCount);
        var weight = new float[nodeCount];
        for (int i = 0; i < vc; i++) weight[nodeOf[i]] = solve.WeightAt(i);
        var tris = model.Parts.Where(p => p.Island < 0).SelectMany(p => p.Triangles).ToArray();
        return SecondSkinWriter.TurnedNormals(basePos, baseNrm, delta, nodeOf, weight, tris);
    }

    // ── positions ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-axis-scale-2")]
    [Trait("Plan", "AC-axis-scale-5")]
    public void StretchAlongOneAxisMovesOnlyThatComponent()
    {
        var (model, n, second) = TwoParts();
        var solve = new MeshVolumeSolve(model);
        var pivot = new Vector3(0.15f, 0.01f, 0.05f);
        Drag(solve, model.Parts[1].Triangles, PartScaleDrag.Build(pivot, new Vector3(1f, 2f, 1f)));

        var after = solve.Positions();
        for (int v = second; v < second + n * n; v++)
        {
            var d = At(model.Positions, v) - pivot;
            var expected = pivot + new Vector3(d.X, 2f * d.Y, d.Z);
            Assert.True(Vector3.Distance(expected, At(after, v)) < 1e-6f, $"vertex {v}");
        }
        Assert.Equal(At(model.Positions, 5), At(after, 5));
        Assert.True(solve.Dirty);
    }

    [Fact]
    [Trait("Plan", "AC-axis-scale-5")]
    public void OneUndoTakesBackAnAxisStretch()
    {
        var (model, _, _) = TwoParts();
        var solve = new MeshVolumeSolve(model);
        Drag(solve, model.Parts[1].Triangles, PartScaleDrag.Build(new Vector3(0.15f, 0.01f, 0.05f), new Vector3(1f, 2f, 1f)));
        solve.Undo();
        var back = solve.Positions();
        for (int v = 0; v < model.Positions.Length / 3; v++)
            Assert.True(Vector3.Distance(At(model.Positions, v), At(back, v)) < 1e-6f, $"vertex {v}");
    }

    [Fact]
    [Trait("Plan", "AC-axis-scale-5")]
    [Trait("Plan", "EDGE-axis-scale-12")]
    public void AxisStretchCarriesAdjacentClothByFalloff()
    {
        var (model, n, _) = TwoParts();
        var solve = new MeshVolumeSolve(model);
        var pivot = new Vector3(0.15f, 0f, 0.05f);
        var stretch = PartScaleDrag.Build(pivot, new Vector3(2f, 1f, 1f));
        Drag(solve, model.Parts[1].Triangles, stretch, adjacent: true, falloff: 0.05f);

        var after = solve.Positions();
        int inBand = 8 * n + 5, beyond = 0 * n + 5;            // 20 mm and 100 mm from the part
        var start = At(model.Positions, inBand);
        float full = Vector3.Transform(start, stretch).X - start.X;
        float share = (At(after, inBand).X - start.X) / full;
        Assert.InRange(share, 0.01f, 0.99f);
        Assert.Equal(At(model.Positions, beyond), At(after, beyond));
    }

    // ── exact normals ────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-axis-scale-7")]
    [Trait("Plan", "EDGE-axis-scale-13")]
    public void StretchKeepsHardEdgedBoxFacesFlat()
    {
        var (model, box) = HardBox();
        var solve = new MeshVolumeSolve(model);
        Drag(solve, model.Parts[0].Triangles, PartScaleDrag.Build(BoxCentre, new Vector3(1f, 2f, 1f)));

        for (int v = 0; v < box; v++)
            AssertNormal(V(solve.AuthoredNormalAt(v)), solve.NormalAt(v), 1e-5f, $"box vertex {v}");

        // The control: the turned normals tilt the side faces, which is the skew this fixes.
        var turned = TurnedNormalsOf(solve, model);
        float worst = 0f;
        for (int v = 0; v < box; v++)
            if (MathF.Abs(solve.AuthoredNormalAt(v).X) > 0.5f)
                worst = MathF.Max(worst, MathF.Acos(Math.Clamp(Vector3.Dot(V(turned[v]), V(solve.AuthoredNormalAt(v))), -1f, 1f)));
        Assert.True(worst > MathF.PI / 180f, $"the turned normals tilted the ±X sides only {worst * 180f / MathF.PI:0.00}°");
    }

    [Fact]
    [Trait("Plan", "AC-axis-scale-7")]
    [Trait("Plan", "EDGE-axis-scale-13")]
    public void TiltedFaceNormalsGetTheInverseTranspose()
    {
        var (model, sheet, pivot) = TiltedSheet();
        var solve = new MeshVolumeSolve(model);
        Drag(solve, model.Parts[0].Triangles, PartScaleDrag.Build(pivot, new Vector3(1f, 2f, 1f)));

        var expected = Vector3.Normalize(new Vector3(1f, 0.5f, 0f));
        for (int v = 0; v < sheet; v++) AssertNormal(expected, solve.NormalAt(v), 1e-5f, $"vertex {v}");
    }

    [Fact]
    [Trait("Plan", "AC-axis-scale-7")]
    [Trait("Plan", "EDGE-axis-scale-17")]
    public void ExactNormalsSurviveLaterEdits()
    {
        var (model, box) = HardBox();
        var solve = new MeshVolumeSolve(model);
        Drag(solve, model.Parts[0].Triangles, PartScaleDrag.Build(BoxCentre, new Vector3(1f, 2f, 1f)));

        void BoxExact(string after)
        {
            for (int v = 0; v < box; v++)
                AssertNormal(V(solve.AuthoredNormalAt(v)), solve.NormalAt(v), 1e-5f, $"{after}: box vertex {v}");
        }

        Assert.True(solve.BeginMove(model.Parts[1].Triangles, adjacent: false, falloffRadius: 0f) > 0);
        solve.MoveTo(new Vector3(0f, 0.01f, 0f));
        solve.EndMove();
        BoxExact("a move elsewhere");

        Assert.True(solve.Paint(new Vector3(0.02f, 0.01f, 0.02f), 0.02f, 0.002f) > 0);
        solve.EndStroke();
        BoxExact("a brush elsewhere");

        solve.Undo();
        BoxExact("undoing that brush");
    }

    [Fact]
    [Trait("Plan", "AC-axis-scale-7")]
    [Trait("Plan", "EDGE-axis-scale-17")]
    public void ExactNormalsAreBitIdenticalAcrossAnUnrelatedSettle()
    {
        var (model, sheet, pivot) = TiltedSheet();
        var solve = new MeshVolumeSolve(model);
        Drag(solve, model.Parts[0].Triangles, PartScaleDrag.Build(pivot, new Vector3(1f, 2f, 1f)));
        var before = Enumerable.Range(0, sheet).Select(solve.NormalAt).ToArray();

        Assert.True(solve.BeginMove(model.Parts[1].Triangles, adjacent: false, falloffRadius: 0f) > 0);
        solve.MoveTo(new Vector3(0f, 0.01f, 0f));
        solve.EndMove();

        for (int v = 0; v < sheet; v++) Assert.Equal(before[v], solve.NormalAt(v));
    }

    /// <summary>
    /// A brush on the stretched part turns its exact normals, not the author's: the nodes the stroke moved turn by
    /// how far their faces turned since the stretch, and everything clear of the stroke's ring keeps them as they were.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-axis-scale-7")]
    [Trait("Plan", "EDGE-axis-scale-17")]
    public void BrushOnStretchedPartTurnsFromExactNormals()
    {
        var (model, sheet, pivot) = TiltedSheet();
        var solve = new MeshVolumeSolve(model);
        Drag(solve, model.Parts[0].Triangles, PartScaleDrag.Build(pivot, new Vector3(1f, 2f, 1f)));
        var exact = Vector3.Normalize(new Vector3(1f, 0.5f, 0f));

        int vc = model.Positions.Length / 3;
        var basePos = Enumerable.Range(0, vc).Select(solve.AuthoredPositionAt).ToArray();
        var nodeOf = MeshMath.WeldByPosition(basePos, out int nodeCount);
        var tris = model.Parts.Where(p => p.Island < 0).SelectMany(p => p.Triangles).ToArray();
        var faces = LinearNormals.UniqueFaces(tris, nodeOf);
        var faceOf = LinearNormals.FacesOfNode(faces, nodeOf, nodeCount);
        var deltaWas = Enumerable.Range(0, vc).Select(solve.DeltaAt).ToArray();

        int middle = 3 * 7 + 3;
        var at = solve.Positions();
        Assert.True(solve.Paint(At(at, middle), 0.012f, 0.002f) > 0);
        solve.EndStroke();
        var deltaNow = Enumerable.Range(0, vc).Select(solve.DeltaAt).ToArray();

        var stroke = new HashSet<int>();
        for (int v = 0; v < sheet; v++) if (deltaNow[v] != deltaWas[v]) stroke.Add(nodeOf[v]);
        Assert.Contains(nodeOf[middle], stroke);
        var ring = new HashSet<int>(stroke);
        foreach (int n in stroke)
            foreach (int f in faceOf[n])
                for (int k = 0; k < 3; k++) ring.Add(nodeOf[faces[f + k]]);

        int clear = 0;
        for (int v = 0; v < sheet; v++)
        {
            if (ring.Contains(nodeOf[v])) continue;
            AssertNormal(exact, solve.NormalAt(v), 1e-5f, $"vertex {v} clear of the stroke");
            clear++;
        }
        Assert.True(clear > 0);

        // Inside the ring: the exact normal, turned by however far the node's faces turned since the stretch.
        int turnedSome = 0;
        for (int v = 0; v < sheet; v++)
        {
            int n = nodeOf[v];
            if (!ring.Contains(n)) continue;
            var refFace = LinearNormals.FaceSum(faces, faceOf[n], basePos, deltaWas);
            var nowFace = LinearNormals.FaceSum(faces, faceOf[n], basePos, deltaNow);
            var expected = Vector3.Normalize(V(LinearNormals.Turn(refFace, nowFace, new Vec3(exact.X, exact.Y, exact.Z))));
            AssertNormal(expected, solve.NormalAt(v), 1e-4f, $"vertex {v} in the stroke's ring");
            if (Vector3.Distance(exact, expected) > 1e-3f) turnedSome++;
        }
        Assert.True(turnedSome > 0, "the dent turned no normal; the fixture proves nothing");
    }

    [Fact]
    [Trait("Plan", "AC-axis-scale-7")]
    [Trait("Plan", "AC-axis-scale-5")]
    public void UndoRestoresAuthoredNormalsAfterAStretch()
    {
        var (model, _) = HardBox();
        var solve = new MeshVolumeSolve(model);
        Drag(solve, model.Parts[0].Triangles, PartScaleDrag.Build(BoxCentre, new Vector3(1f, 2f, 1f)));
        solve.Undo();
        for (int v = 0; v < model.Positions.Length / 3; v++) Assert.Equal(solve.AuthoredNormalAt(v), solve.NormalAt(v));
    }

    /// <summary>
    /// Where the stretch meets cloth it did not carry whole — the seam it shares, the band the falloff reaches — the
    /// surface did not change by the matrix, and the normals are the turned ones, as any other edit's.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-axis-scale-16")]
    public void SeamAndBandKeepTurnedNormals()
    {
        var (model, n, second) = TwoParts();
        var solve = new MeshVolumeSolve(model);
        // Lifted off the sheets' plane, so the stretch drops the part and the seam and band tilt.
        var pivot = new Vector3(0.15f, 0.02f, 0.05f);
        Drag(solve, model.Parts[1].Triangles, PartScaleDrag.Build(pivot, new Vector3(1f, 2f, 1f)),
             adjacent: true, falloff: 0.02f);

        var turned = TurnedNormalsOf(solve, model);
        int checkedTilted = 0;
        for (int v = 0; v < model.Positions.Length / 3; v++)
        {
            bool seam = MathF.Abs(At(model.Positions, v).X - 0.1f) < 1e-6f;
            float w = solve.WeightAt(v);
            bool band = w > 0f && w < 1f;
            if (!seam && !band) continue;
            Assert.Equal(turned[v], solve.NormalAt(v));
            if (MathF.Abs(turned[v].Y) < 0.999f) checkedTilted++;
        }
        Assert.True(checkedTilted > 0, "no seam or band normal tilted; the fixture proves nothing");
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-18")]
    public void TwoStretchesCompose()
    {
        var (model, sheet, pivot) = TiltedSheet();
        var solve = new MeshVolumeSolve(model);
        Drag(solve, model.Parts[0].Triangles, PartScaleDrag.Build(pivot, new Vector3(1f, 2f, 1f)));
        Drag(solve, model.Parts[0].Triangles, PartScaleDrag.Build(pivot, new Vector3(3f, 1f, 1f)));

        var expected = Vector3.Normalize(new Vector3(Tilted.X / 3f, Tilted.Y / 2f, 0f));
        for (int v = 0; v < sheet; v++) AssertNormal(expected, solve.NormalAt(v), 1e-5f, $"vertex {v}");
    }

    /// <summary>
    /// A selection slid first leaves its edge nodes' faces sheared but their normals the author's (a slide gives no
    /// weight); a stretch over it afterwards starts from those authored normals, not from a turn that never happened.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-axis-scale-18")]
    public void TranslateThenStretchComposesExactly()
    {
        var (model, sheet, pivot) = TiltedSheet();
        var solve = new MeshVolumeSolve(model);
        var firstRows = model.Parts[0].Triangles.Take(2 * 6 * 6).ToArray();     // the first two rows of quads
        Assert.True(solve.BeginMove(firstRows, adjacent: false, falloffRadius: 0f) > 0);
        solve.MoveTo(new Vector3(0f, 0.005f, 0f));
        solve.EndMove();

        Drag(solve, model.Parts[0].Triangles, PartScaleDrag.Build(pivot, new Vector3(1f, 2f, 1f)));
        var expected = Vector3.Normalize(new Vector3(1f, 0.5f, 0f));
        for (int v = 0; v < sheet; v++) AssertNormal(expected, solve.NormalAt(v), 1e-5f, $"vertex {v}");
    }

    /// <summary>
    /// A stretch after a turn applies on top of it, in that order: the turned normals through the stretch's
    /// inverse-transpose. The two do not commute, so the order the maps compose in shows.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-axis-scale-18")]
    public void RotateThenStretchComposesExactly()
    {
        var (model, box) = HardBox();
        var solve = new MeshVolumeSolve(model);
        var turn = RotateGizmo.About(BoxCentre, Vector3.UnitZ, MathF.PI / 6f);
        var stretch = PartScaleDrag.Build(BoxCentre, new Vector3(1f, 2f, 1f));
        Drag(solve, model.Parts[0].Triangles, turn);
        Drag(solve, model.Parts[0].Triangles, stretch);

        var map = LinearNormals.NormalMap(stretch)!.Value;
        for (int v = 0; v < box; v++)
        {
            var expected = Vector3.Normalize(Vector3.TransformNormal(
                Vector3.TransformNormal(V(solve.AuthoredNormalAt(v)), turn), map));
            AssertNormal(expected, solve.NormalAt(v), 1e-5f, $"box vertex {v}");
        }
    }

    /// <summary>
    /// A stretch over cloth a brush already relit starts from the brush's turned normals, not from the author's.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-axis-scale-18")]
    public void StretchAfterABrushStartsFromTheTurnedNormals()
    {
        var (model, sheet, pivot) = TiltedSheet();
        var solve = new MeshVolumeSolve(model);
        Assert.True(solve.Paint(At(model.Positions, 3 * 7 + 3), 0.025f, 0.003f) > 0);
        solve.EndStroke();
        var before = Enumerable.Range(0, sheet).Select(v => V(solve.NormalAt(v))).ToArray();
        Assert.Contains(before, n => Vector3.Distance(n, Tilted) > 1e-3f);

        Drag(solve, model.Parts[0].Triangles, PartScaleDrag.Build(pivot, new Vector3(1f, 2f, 1f)));
        for (int v = 0; v < sheet; v++)
        {
            var expected = Vector3.Normalize(new Vector3(before[v].X, before[v].Y * 0.5f, before[v].Z));
            AssertNormal(expected, solve.NormalAt(v), 1e-4f, $"vertex {v}");
        }
    }

    /// <summary>
    /// A stretch, a brush on the stretched part, then a second stretch: the second starts from the normals the brush
    /// turned the exact ones to — the record's map first, then the turn its faces have made since.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-axis-scale-18")]
    public void StretchBrushStretchComposesExactly()
    {
        var (model, sheet, pivot) = TiltedSheet();
        var solve = new MeshVolumeSolve(model);
        Drag(solve, model.Parts[0].Triangles, PartScaleDrag.Build(pivot, new Vector3(1f, 2f, 1f)));
        Assert.True(solve.Paint(At(solve.Positions(), 3 * 7 + 3), 0.025f, 0.003f) > 0);
        solve.EndStroke();
        var before = Enumerable.Range(0, sheet).Select(v => V(solve.NormalAt(v))).ToArray();
        var exact = Vector3.Normalize(new Vector3(1f, 0.5f, 0f));
        Assert.Contains(before, n => Vector3.Distance(n, exact) > 1e-3f);

        Drag(solve, model.Parts[0].Triangles, PartScaleDrag.Build(pivot, new Vector3(3f, 1f, 1f)));
        for (int v = 0; v < sheet; v++)
        {
            var expected = Vector3.Normalize(new Vector3(before[v].X / 3f, before[v].Y, before[v].Z));
            AssertNormal(expected, solve.NormalAt(v), 1e-4f, $"vertex {v}");
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-19")]
    public void ResetAndImportDropExactNormals()
    {
        var (model, _) = HardBox();
        var solve = new MeshVolumeSolve(model);
        Drag(solve, model.Parts[0].Triangles, PartScaleDrag.Build(BoxCentre, new Vector3(1f, 2f, 1f)));

        var carried = BrushTransfer.Transfer(solve, model, model);
        var turned = TurnedNormalsOf(carried, model);
        for (int v = 0; v < model.Positions.Length / 3; v++) Assert.Equal(turned[v], carried.NormalAt(v));

        solve.Reset();
        for (int v = 0; v < model.Positions.Length / 3; v++) Assert.Equal(solve.AuthoredNormalAt(v), solve.NormalAt(v));
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-23")]
    public void MoveAndEvenScaleKeepTodaysNormals()
    {
        var (model, _, pivot) = TiltedSheet();

        var slid = new MeshVolumeSolve(model);
        Assert.True(slid.BeginMove(model.Parts[0].Triangles.Take(36).ToArray(), adjacent: false, falloffRadius: 0f) > 0);
        slid.MoveTo(new Vector3(0f, 0.005f, 0f));
        slid.EndMove();
        var turned = TurnedNormalsOf(slid, model);
        for (int v = 0; v < model.Positions.Length / 3; v++) Assert.Equal(turned[v], slid.NormalAt(v));

        var grown = new MeshVolumeSolve(model);
        Drag(grown, model.Parts[0].Triangles.Take(36).ToArray(), PartScaleDrag.Build(pivot, new Vector3(2f)));
        turned = TurnedNormalsOf(grown, model);
        for (int v = 0; v < model.Positions.Length / 3; v++) Assert.Equal(turned[v], grown.NormalAt(v));
    }

    [Fact]
    [Trait("Plan", "AC-axis-scale-7")]
    public void RotateTurnsBoxNormalsExactly()
    {
        var (model, box) = HardBox();
        var solve = new MeshVolumeSolve(model);
        var turn = RotateGizmo.About(BoxCentre, Vector3.UnitY, MathF.PI / 2f);
        Drag(solve, model.Parts[0].Triangles, turn);

        for (int v = 0; v < box; v++)
        {
            var expected = Vector3.Normalize(Vector3.TransformNormal(V(solve.AuthoredNormalAt(v)), turn));
            AssertNormal(expected, solve.NormalAt(v), 1e-5f, $"box vertex {v}");
        }
    }

    // ── folds ────────────────────────────────────────────────────────────────

    private static Vector3 FaceNormal(float[] positions, int a, int b, int c)
        => Vector3.Cross(At(positions, b) - At(positions, a), At(positions, c) - At(positions, a));

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-2")]
    public void SquashToMinScaleFoldsNoPartTriangle()
    {
        var (model, _, pivot) = TiltedSheet();
        var solve = new MeshVolumeSolve(model);
        Drag(solve, model.Parts[0].Triangles, PartScaleDrag.Build(pivot, new Vector3(PartScaleDrag.MinScale, 1f, 1f)));

        var after = solve.Positions();
        var t = model.Parts[0].Triangles;
        for (int i = 0; i + 2 < t.Length; i += 3)
            Assert.True(Vector3.Dot(FaceNormal(model.Positions, t[i], t[i + 1], t[i + 2]),
                                    FaceNormal(after, t[i], t[i + 1], t[i + 2])) > 0f, $"triangle {i / 3} folded");
    }

    /// <summary>
    /// The model's triangles that turned over between <paramref name="before"/> and <paramref name="after"/> — a fold, a
    /// hole in game — counting only those <paramref name="only"/> picks (every triangle when null). Islands are left
    /// out: their triangles are already counted in their submesh.
    /// </summary>
    private static (int Folded, int Counted) FoldedTriangles(ModelParts model, float[] before, float[] after,
                                                             Func<int, int, int, bool>? only = null)
    {
        int folded = 0, counted = 0;
        foreach (var part in model.Parts)
        {
            if (part.Island >= 0) continue;
            var t = part.Triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                if (only != null && !only(t[i], t[i + 1], t[i + 2])) continue;
                counted++;
                if (Vector3.Dot(FaceNormal(before, t[i], t[i + 1], t[i + 2]),
                                FaceNormal(after, t[i], t[i + 1], t[i + 2])) <= 0f) folded++;
            }
        }
        return (folded, counted);
    }

    /// <summary>Band triangles (any corner part-way carried) that turned over against how they faced before the drag.</summary>
    private static int FoldedBand(Matrix4x4 transform)
    {
        var (model, _, _) = TwoParts();
        var solve = new MeshVolumeSolve(model);
        Drag(solve, model.Parts[1].Triangles, transform, adjacent: true, falloff: 0.02f);
        var (folded, band) = FoldedTriangles(model, model.Positions, solve.Positions(),
            (a, b, c) => new[] { a, b, c }.Any(v => solve.WeightAt(v) is > 0f and < 1f));
        Assert.True(band > 0, "the fixture has no band");
        return folded;
    }

    /// <summary>
    /// A squash cannot fold a band that recedes from the part — the band here lies beside the part, further from the
    /// pivot the further it is from the part. Not every band does: cloth inside a ring-shaped part can (decision 12).
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-axis-scale-21")]
    public void SquashWithShortFalloffFoldsNoBandTriangle()
        => Assert.Equal(0, FoldedBand(PartScaleDrag.Build(new Vector3(0.15f, 0f, 0.05f), new Vector3(0.05f, 1f, 1f))));

    /// <summary>
    /// Pins known behaviour, not a wish: a strong stretch with a short falloff folds the band, one-axis exactly as even
    /// scaling always has. No guard by decision (plan decision 12) — a guard would claw back the user's drag. A future
    /// guard updates this on purpose.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-axis-scale-21")]
    public void LargeStretchWithShortFalloffFoldsTheBandAsEvenScaleDoes()
    {
        var pivot = new Vector3(0.15f, 0f, 0.05f);
        Assert.True(FoldedBand(PartScaleDrag.Build(pivot, new Vector3(20f, 1f, 1f))) > 0);
        Assert.True(FoldedBand(PartScaleDrag.Build(pivot, new Vector3(20f))) > 0);
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-14")]
    public void TapOnHandleChangesNothing()
    {
        var (model, _) = HardBox();
        var solve = new MeshVolumeSolve(model);
        Assert.True(solve.BeginMove(model.Parts[0].Triangles, adjacent: false, falloffRadius: 0f) > 0);
        solve.TransformTo(Matrix4x4.Identity);
        Assert.False(solve.EndMove(), "a tap reported it moved something, so the caller would save");
        Assert.False(solve.CanUndo);
        Assert.False(solve.Dirty);
        Assert.Equal(0f, solve.WeightAt(0));
        for (int v = 0; v < model.Positions.Length / 3; v++)
        {
            Assert.Equal(solve.AuthoredNormalAt(v), solve.NormalAt(v));
            Assert.Equal(At(model.Positions, v), At(solve.Positions(), v));
        }
    }
}
