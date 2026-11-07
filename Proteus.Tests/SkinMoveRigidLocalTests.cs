using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// #86 on the reported garment: Tight&amp;Firm-RebelGirl's top, model view, Move skin too on. An All-mode Move of 15 mm
/// answered "Furthest moved" 32.45 mm; a Whole-parts Move of skin row 1.1 by 15 mm, 29.36 mm. Measured inside the solve,
/// before the guard and after it.
/// </summary>
public class SkinMoveRigidLocalTests(ITestOutputHelper output)
{
    private const string Top = LocalData.Mods + ":Tight&Firm-RebelGirl (2)/default/chara/equipment/e6096/model/c0201e6096_top.mdl";
    private const float Drag = 0.015f;
    /// <summary>The Studio's defaults for a Whole-parts drag (PartsPanel: moveAdjacent = true, moveFalloffMm = 50), which
    /// the QA run that found #86 left as they were.</summary>
    private const bool Adjacent = true;
    private const float FalloffMm = 50f;

    private static Vector3 At(float[] p, int v) => new(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);

    private static (ModelParts Model, MeshVolumeSolve Solve) Open()
    {
        var model = ModelPartReader.Read(File.ReadAllBytes(LocalData.Path(Top))) ?? throw new InvalidDataException("unreadable");
        var solve = new MeshVolumeSolve(model) { MoveSkin = true };
        Assert.True(solve.AttachFollow(SkinFollow.Bind(solve.FollowInputs)));
        return (model, solve);
    }

    /// <summary>Every whole part's corners: what All moves with every row ticked (skin included, nothing locked).</summary>
    private static List<int> AllSeeds(ModelParts model)
        => model.Parts.Where(p => p.Island < 0).SelectMany(p => p.Triangles).Distinct().ToList();

    /// <summary>How far each vertex went from <paramref name="from"/> to <paramref name="to"/>, the furthest, in mm.</summary>
    private static float WorstMm(float[] from, float[] to)
    {
        float worst = 0f;
        for (int v = 0; v < from.Length / 3; v++) worst = MathF.Max(worst, (At(to, v) - At(from, v)).Length());
        return worst * 1000f;
    }

    /// <summary>How far any of <paramref name="moved"/> ended from where <paramref name="rigid"/> puts it, in mm.</summary>
    private static float OffRigidMm(float[] from, float[] to, IEnumerable<int> moved, Func<Vector3, Vector3> rigid)
    {
        float worst = 0f;
        foreach (int v in moved) worst = MathF.Max(worst, (At(to, v) - rigid(At(from, v))).Length());
        return worst * 1000f;
    }

    private static int Folded(ModelParts model, float[] after)
    {
        int folded = 0;
        foreach (var part in model.Parts.Where(p => p.Island < 0))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                var was = Vector3.Cross(At(model.Positions, b) - At(model.Positions, a), At(model.Positions, c) - At(model.Positions, a));
                var now = Vector3.Cross(At(after, b) - At(after, a), At(after, c) - At(after, a));
                if (was.LengthSquared() > 1e-16f && Vector3.Dot(was, now) <= 0f) folded++;
            }
        return folded;
    }

    private void Report(string name, MeshVolumeSolve solve, float[] start, float[] beforeGuard, float[] after, Stopwatch sw)
    {
        var g = solve.LastGuard;
        output.WriteLine($"{name}: drag only {WorstMm(start, beforeGuard):F2} mm, after the guard {WorstMm(start, after):F2} mm "
                       + $"(Worst {solve.Worst * 1000f:F2}); guard lifted {g.Bound} bound / {g.Face} face, worst lift {g.WorstMm:F2} mm, "
                       + $"{g.Inward} inward; {sw.ElapsedMilliseconds} ms");
    }

    [LocalDataFact(Top)]
    [Trait("Plan", "AC-issue-sweep-c-2")]
    public void RebelGirlTopAllMoveRotateWithSkinAreRigid()
    {
        var sw = Stopwatch.StartNew();
        var (model, solve) = Open();
        var start = solve.Positions().ToArray();
        var seeds = AllSeeds(model);

        // (i) All, Move 15 mm along Z.
        Assert.True(solve.BeginMove(seeds, adjacent: false, falloffRadius: 0f) > 0);
        solve.MoveTo(new Vector3(0f, 0f, Drag));
        var dragged = solve.Positions().ToArray();
        Assert.True(solve.EndMove());
        var moved = solve.Positions().ToArray();
        Report("All Move 15 mm", solve, start, dragged, moved, sw);
        Assert.Equal(15f, WorstMm(start, moved), 0.1f);
        float offMove = OffRigidMm(start, moved, seeds, p => p + new Vector3(0f, 0f, Drag));
        Assert.True(offMove <= 0.1f, $"a vertex ended {offMove:F3} mm off the drag");
        Assert.Equal(0, solve.LastGuard.Bound + solve.LastGuard.Face);
        Assert.Equal(0, Folded(model, moved));

        // (ii) All, Rotate 30° about Y around the bounds centre — from where (i) left it.
        var (open, s2) = Open();
        var centre = (open.Min + open.Max) / 2f;
        var turn = Matrix4x4.CreateTranslation(-centre) * Matrix4x4.CreateRotationY(MathF.PI / 6f) * Matrix4x4.CreateTranslation(centre);
        Assert.True(s2.BeginMove(AllSeeds(open), adjacent: false, falloffRadius: 0f) > 0);
        s2.TransformTo(turn);
        var turnedDrag = s2.Positions().ToArray();
        Assert.True(s2.EndMove());
        var turned = s2.Positions().ToArray();
        Report("All Rotate 30°", s2, start, turnedDrag, turned, sw);
        float offDrag = OffRigidMm(start, turnedDrag, AllSeeds(open), p => Vector3.Transform(p, turn));
        float offTurn = OffRigidMm(start, turned, AllSeeds(open), p => Vector3.Transform(p, turn));
        output.WriteLine($"rotate: the drag {offDrag:F3} mm off the turn, after the guard {offTurn:F3} mm");
        Assert.True(offTurn <= 0.1f, $"a vertex ended {offTurn:F3} mm off the turn (the drag alone {offDrag:F3} mm)");
        Assert.Equal(0, Folded(open, turned));

        // (iii) All, Scale 1.2 — not rigid (heights scale with it), measured for the record only.
        var (sc, s3) = Open();
        var centre3 = (sc.Min + sc.Max) / 2f;
        var grow = Matrix4x4.CreateTranslation(-centre3) * Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateTranslation(centre3);
        Assert.True(s3.BeginMove(AllSeeds(sc), adjacent: false, falloffRadius: 0f) > 0);
        s3.TransformTo(grow);
        var grownDrag = s3.Positions().ToArray();
        Assert.True(s3.EndMove());
        Report("All Scale 1.2 (information)", s3, start, grownDrag, s3.Positions().ToArray(), sw);
    }

    [LocalDataFact(Top)]
    [Trait("Plan", "AC-issue-sweep-c-3")]
    [Trait("Plan", "EDGE-issue-sweep-c-43")]
    public void RebelGirlTopSkinRow11MoveIsTheDragOrAPoke()
    {
        var sw = Stopwatch.StartNew();
        var (model, solve) = Open();
        var start = solve.Positions().ToArray();
        var row = model.Parts.Single(p => p.Island < 0 && p.Label == "1.1");
        output.WriteLine($"row 1.1: {row.Material}, skin {SecondSkinWriter.IsBodySkinMaterial(row.Material)}");

        Assert.True(solve.BeginMove(row.Triangles, Adjacent, FalloffMm / 1000f) > 0);
        solve.MoveTo(new Vector3(0f, 0f, Drag));
        var dragged = solve.Positions().ToArray();
        Assert.True(solve.EndMove());
        var after = solve.Positions().ToArray();
        Report("Whole parts row 1.1 Move 15 mm", solve, start, dragged, after, sw);
        Assert.True(WorstMm(start, dragged) <= 15.01f, "the drag itself never goes further than the drag");
        output.WriteLine($"row 1.1: folded {Folded(model, dragged)} by the drag, {Folded(model, after)} after the guard");

        // Beyond the drag only where the drag itself pushed cloth under the skin it rides: measured independently of the
        // guard, along each such vertex's bound skin triangle, before the guard ran (#86, AC-3's "proven poke").
        var inputs = solve.FollowInputs;
        var binding = SkinFollow.Bind(inputs);
        var vertexOf = new Dictionary<int, int>();
        for (int v = 0; v < inputs.NodeOf.Length; v++) vertexOf.TryAdd(inputs.NodeOf[v], v);
        float Height(float[] p, SkinFollow.BoundTriangle t, int v)
        {
            var a = At(p, vertexOf[t.A]); var b = At(p, vertexOf[t.B]); var c = At(p, vertexOf[t.C]);
            var f = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            var authored = Vector3.Cross(At(model.Positions, vertexOf[t.B]) - At(model.Positions, vertexOf[t.A]),
                                         At(model.Positions, vertexOf[t.C]) - At(model.Positions, vertexOf[t.A]));
            var nrm = At(model.Normals, vertexOf[t.A]) + At(model.Normals, vertexOf[t.B]) + At(model.Normals, vertexOf[t.C]);
            if (Vector3.Dot(authored, nrm) < 0f) f = -f;
            return Vector3.Dot(At(p, v) - (a * t.U + b * t.V + c * t.W), f);
        }
        int beyond = 0, proven = 0, face = 0;
        var unexplained = new List<int>();
        for (int v = 0; v < start.Length / 3; v++)
        {
            if ((At(after, v) - At(start, v)).Length() <= Drag + 1e-5f) continue;
            beyond++;
            int node = inputs.NodeOf[v];
            var t = binding.TriangleOf(node);
            bool boundPoke = false;
            if (t.IsSet)
            {
                float floor = MathF.Min(MathF.Min(0.001f, binding.AuthoredHeight(node)), Height(start, t, v));
                boundPoke = Height(dragged, t, v) < floor - 1e-6f;
            }
            if (boundPoke && solve.LastBoundLifted.Contains(node)) proven++;
            else if (solve.LastFaceLifted.Contains(node)) face++;
            else unexplained.Add(v);
        }
        output.WriteLine($"row 1.1: {beyond} vertices beyond the drag: {proven} pushed under their skin by the drag itself, "
                       + $"{face} lifted by the face check over skin the move pushed through it");
        // Every vertex beyond the drag is a lift the guard answered to a poke this move made: a bound rider the drag sank
        // under its skin (measured here, independently), or a corner of a face the moved skin came through (the face check
        // fires only where the skin stands further outside the face than both the author and the move's start had it).
        Assert.True(unexplained.Count == 0, $"beyond the drag with no poke behind it: {string.Join(", ", unexplained.Take(10))}");
        // Measured (build 1455): before the fix 29.77 mm, 420 vertices beyond the drag, 54 folded, 553 bound lifts; after,
        // 15.41 mm, 8 beyond (all face-check corners), 10 folded. The face check's corner lift folding a few triangles on
        // a real poke is a pre-existing limit of its rule (#90): pinned here so it can only get better.
        Assert.True(WorstMm(start, after) <= Drag * 1000f + 0.5f, $"after the guard {WorstMm(start, after):F2} mm");
        Assert.True(Folded(model, after) <= 10, $"{Folded(model, after)} folded (#90)");
    }
}
