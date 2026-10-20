using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Gui;
using Proteus.Services;
using Xunit;
using Kind = Proteus.Services.MeshVolumeSolve.MoveKind;
using Mode = Proteus.Services.MeshVolumeSolve.MirrorMode;
using MoveMirror = Proteus.Services.MeshVolumeSolve.MoveMirror;

namespace Proteus.Tests;

/// <summary>
/// Move, Rotate and Scale with Mirror on: the drag reflected onto the matching cloth across X = 0, measured inside the
/// pass (<see cref="MeshVolumeSolve.Positions"/> before and after release), never on an exported model.
/// </summary>
public partial class MeshVolumeSolveTests
{
    // ── fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A model built from grids in the XZ plane, one part per material. Each cell is cut along the diagonal that mirrors
    /// across X = 0 (one way left of it, the other way right), so a symmetric layout gives a symmetric mesh.
    /// </summary>
    private sealed class MirrorMesh
    {
        public readonly List<float> Pos = [];
        public readonly List<float> Nrm = [];
        private readonly List<(string Material, List<int> Tris)> parts = [];

        private List<int> PartFor(string material)
        {
            foreach (var (m, t) in parts) if (m == material) return t;
            var tris = new List<int>();
            parts.Add((material, tris));
            return tris;
        }

        public int[,] Grid(float[] xs, float[] zs, float y = 0f, string material = "/mt_test.mtrl", float normalY = 1f,
                           Func<int, int, float>? height = null)
        {
            var index = new int[xs.Length, zs.Length];
            for (int i = 0; i < xs.Length; i++)
            for (int j = 0; j < zs.Length; j++)
            {
                index[i, j] = Pos.Count / 3;
                Pos.AddRange([xs[i], y + (height?.Invoke(i, j) ?? 0f), zs[j]]);
                Nrm.AddRange([0f, normalY, 0f]);
            }
            var tris = PartFor(material);
            for (int i = 0; i + 1 < xs.Length; i++)
            for (int j = 0; j + 1 < zs.Length; j++)
            {
                int a = index[i, j], b = index[i + 1, j], c = index[i, j + 1], d = index[i + 1, j + 1];
                bool flip = normalY < 0f;
                bool left = xs[i] + xs[i + 1] < 0f;
                int[] cell = left ? [a, b, d, a, d, c] : [a, b, c, b, d, c];
                if (flip) for (int k = 0; k < cell.Length; k += 3) (cell[k + 1], cell[k + 2]) = (cell[k + 2], cell[k + 1]);
                tris.AddRange(cell);
            }
            return index;
        }

        public ModelParts Build()
        {
            var built = new List<ModelPart>();
            for (int k = 0; k < parts.Count; k++)
                built.Add(Part(0, k, $"1.{k + 1}", parts[k].Tris.ToArray(), parts[k].Material));
            return new ModelParts
            {
                Positions = Pos.ToArray(),
                Normals = Nrm.ToArray(),
                MeshSpans = [new MeshSpan(0, 0, Pos.Count / 3)],
                Parts = built,
                AttributeNames = [],
                Min = Vector3.Zero,
                Max = Vector3.One,
                ShatteredSubmeshes = new Dictionary<string, int>(),
            };
        }
    }

    private const string SkinMaterial = "/mt_c0201b0001_bibo.mtrl";

    /// <summary><paramref name="count"/> values from <paramref name="from"/>, <paramref name="step"/> apart.</summary>
    private static float[] Steps(float from, int count, float step = Spacing)
        => Enumerable.Range(0, count).Select(i => from + i * step).ToArray();

    /// <summary>The centred 21×21 grid's columns <paramref name="i0"/>..<paramref name="i1"/>, rows j0..j1, as vertices.</summary>
    private static int[] Patch(int n, int i0, int i1, int j0, int j1)
    {
        var verts = new List<int>();
        for (int i = i0; i <= i1; i++)
        for (int j = j0; j <= j1; j++) verts.Add(i * n + j);
        return verts.ToArray();
    }

    private static int[] All(int[,] index) => index.Cast<int>().ToArray();

    private static int[] Cells(int[,] index, int i0, int i1, int j0, int j1)
    {
        var verts = new List<int>();
        for (int i = i0; i <= i1; i++)
        for (int j = j0; j <= j1; j++) verts.Add(index[i, j]);
        return verts.ToArray();
    }

    private static Vector3 BoundsCentre(float[] positions, IEnumerable<int> verts)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (int v in verts)
        {
            var p = At(positions, v);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min + max) * 0.5f;
    }

    private static MoveMirror Mirrored(Kind kind, float[] positions, IEnumerable<int> verts)
        => new(kind, BoundsCentre(positions, verts));

    private static Vector3 Reflect(Vector3 v) => new(-v.X, v.Y, v.Z);

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance, string what)
        => Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"{what}: expected {expected}, got {actual}");

    private static void AssertUnmoved(float[] before, float[] after, IEnumerable<int> verts, string what)
    {
        foreach (int v in verts) Assert.True(At(before, v) == At(after, v), $"{what}: vertex {v} moved to {At(after, v)}");
    }

    private static void AssertNoFolds(ModelParts model, float[] before, float[] after, string what)
    {
        var (folded, _) = FoldedTriangles(model, before, after);
        Assert.True(folded == 0, $"{what}: {folded} triangle(s) folded");
    }

    // ── the other side follows ──────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-1")]
    public void MirroredMoveMovesTheOtherSideWithXFlipped()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var solve = new MeshVolumeSolve(model);
        var patch = Patch(n, 14, 16, 8, 12);
        var mirror = Patch(n, 4, 6, 8, 12);
        var o = new Vector3(0.01f, 0.02f, -0.005f);

        Assert.True(solve.BeginMove(patch, adjacent: false, falloffRadius: 0f,
                                    mirror: Mirrored(Kind.Translate, model.Positions, patch)) > 0);
        solve.MoveTo(o * 0.5f);
        solve.MoveTo(o);

        var during = solve.Positions();
        foreach (int v in patch) AssertNear(At(model.Positions, v) + o, At(during, v), 1e-7f, "selected");
        foreach (int v in mirror) AssertNear(At(model.Positions, v) + Reflect(o), At(during, v), 1e-7f, "mirrored");
        AssertUnmoved(model.Positions, during, Enumerable.Range(0, n * n).Except(patch).Except(mirror), "elsewhere");
        AssertSymmetric(during, n, 1e-7f, "during");

        Assert.Equal(new MeshVolumeSolve.MirrorReport(patch.Length, patch.Length, 0, Mode.Sides), solve.DragMirror);
        Assert.True(solve.EndMove());
        AssertSymmetric(solve.Positions(), n, 1e-6f, "released");
    }

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-1")]
    public void MirroredRotateTurnsTheOtherSideTheOppositeWay()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var solve = new MeshVolumeSolve(model);
        var patch = Patch(n, 14, 16, 8, 12);
        var mirror = Patch(n, 4, 6, 8, 12);
        var how = Mirrored(Kind.Rotate, model.Positions, patch);
        var t = RotateGizmo.About(how.Pivot, Vector3.UnitY, 0.4f);

        Assert.True(solve.BeginMove(patch, adjacent: false, falloffRadius: 0f, mirror: how) > 0);
        solve.TransformTo(t);
        var during = solve.Positions();
        foreach (int v in patch) AssertNear(Vector3.Transform(At(model.Positions, v), t), At(during, v), 1e-6f, "selected");
        var other = MirrorMotion.Reflect(t);
        foreach (int v in mirror) AssertNear(Vector3.Transform(At(model.Positions, v), other), At(during, v), 1e-6f, "mirrored");
        AssertSymmetric(during, n, 1e-6f, "turned");
        solve.EndMove();
        AssertNoFolds(model, model.Positions, solve.Positions(), "turned");
    }

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-1")]
    public void MirroredScaleStretchesTheOtherSideAboutTheMirroredPivot()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var solve = new MeshVolumeSolve(model);
        var patch = Patch(n, 14, 16, 8, 12);
        var mirror = Patch(n, 4, 6, 8, 12);
        var how = Mirrored(Kind.Scale, model.Positions, patch);
        var factors = new Vector3(1.5f, 1f, 1.2f);

        Assert.True(solve.BeginMove(patch, adjacent: false, falloffRadius: 0f, mirror: how) > 0);
        solve.TransformTo(PartScaleDrag.Build(how.Pivot, factors));
        var during = solve.Positions();
        var other = PartScaleDrag.Build(Reflect(how.Pivot), factors);
        foreach (int v in mirror) AssertNear(Vector3.Transform(At(model.Positions, v), other), At(during, v), 1e-6f, "mirrored");
        AssertSymmetric(during, n, 1e-6f, "stretched");
        solve.EndMove();
    }

    /// <summary>A part's corners and a polygon selection's both mirror; in polygon mode a loose sheet lying just over
    /// the other side is neither paired nor reached by the falloff.</summary>
    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-1")]
    [Trait("Plan", "EDGE-mirror-move-tools-13")]
    public void PartAndPolygonSelectionsBothMirror()
    {
        var mesh = new MirrorMesh();
        var grid = mesh.Grid(Steps(-0.1f, 21), Steps(0f, 21));
        var loose = mesh.Grid(Steps(-0.06f, 3), Steps(0.08f, 5), y: 0.004f);
        var model = mesh.Build();
        var patch = Cells(grid, 14, 16, 8, 12);

        foreach (bool polygons in new[] { false, true })
        {
            var solve = new MeshVolumeSolve(model);
            Assert.True(solve.BeginMove(patch, adjacent: true, falloffRadius: 0.02f, alongSurface: polygons,
                                        mirror: Mirrored(Kind.Translate, model.Positions, patch)) > 0);
            solve.MoveTo(new Vector3(0.004f, 0.01f, 0f));
            var during = solve.Positions();
            for (int i = 0; i < 21; i++)
            for (int j = 0; j < 21; j++)
            {
                var p = At(during, grid[i, j]);
                var q = At(during, grid[20 - i, j]);
                Assert.True(MathF.Abs(p.X + q.X) < 1e-6f && MathF.Abs(p.Y - q.Y) < 1e-6f,
                            $"polygons={polygons}: column {i} row {j} at {p} does not mirror {q}");
            }
            if (polygons) AssertUnmoved(model.Positions, during, All(loose), "loose sheet");
            solve.EndMove();
        }
    }

    // ── a piece that is its own mirror ──────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-3")]
    [Trait("Plan", "EDGE-mirror-move-tools-1")]
    public void SelfPairedPieceMovesAsOneAndStaysCentred()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var whole = model.Parts[0].Triangles;

        var preview = new MeshVolumeSolve(model);
        Assert.NotNull(preview.MoveFalloff(whole, 0f, alongSurface: false, mirror: true));
        Assert.Equal(Mode.Centred, preview.PreviewMirror?.Mode);

        // A move keeps only its up/down and forward/back.
        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(whole, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, whole));
        Assert.Equal(Mode.Centred, solve.DragMirror?.Mode);
        solve.MoveTo(new Vector3(0.02f, 0.01f, 0f));
        var moved = solve.Positions();
        for (int v = 0; v < n * n; v++) AssertNear(At(model.Positions, v) + new Vector3(0f, 0.01f, 0f), At(moved, v), 1e-7f, "move");
        solve.EndMove();

        // A turn about X is kept whole, about a pivot off the centre too.
        var pivot = new Vector3(0.03f, 0f, 0.1f);
        var xTurn = RotateGizmo.About(pivot, Vector3.UnitX, 0.3f);
        solve = new MeshVolumeSolve(model);
        solve.BeginMove(whole, false, 0f, mirror: new MoveMirror(Kind.Rotate, pivot));
        solve.TransformTo(xTurn);
        var turned = solve.Positions();
        for (int v = 0; v < n * n; v++) AssertNear(Vector3.Transform(At(model.Positions, v), xTurn), At(turned, v), 1e-6f, "X turn");
        solve.EndMove();

        // A view-ring turn keeps only its turn about X.
        var axis = Vector3.Normalize(new Vector3(1f, 0.5f, 1f));
        var oblique = RotateGizmo.About(pivot, axis, 0.5f);
        solve = new MeshVolumeSolve(model);
        solve.BeginMove(whole, false, 0f, mirror: new MoveMirror(Kind.Rotate, pivot));
        solve.TransformTo(oblique);
        AssertSymmetric(solve.Positions(), n, 1e-6f, "view turn");
        solve.EndMove();

        // A stretch grows from the centre plane, wherever the gizmo sits.
        solve = new MeshVolumeSolve(model);
        solve.BeginMove(whole, false, 0f, mirror: new MoveMirror(Kind.Scale, pivot));
        solve.TransformTo(PartScaleDrag.Build(pivot, new Vector3(1.5f, 1f, 1f)));
        var stretched = solve.Positions();
        var aboutCentre = PartScaleDrag.Build(new Vector3(0f, pivot.Y, pivot.Z), new Vector3(1.5f, 1f, 1f));
        for (int v = 0; v < n * n; v++)
            AssertNear(Vector3.Transform(At(model.Positions, v), aboutCentre), At(stretched, v), 1e-6f, "stretch");
        solve.EndMove();
        AssertNoFolds(model, model.Positions, solve.Positions(), "stretch");
    }

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-3")]
    [Trait("Plan", "AC-mirror-move-tools-6")]
    [Trait("Plan", "EDGE-mirror-move-tools-24")]
    public void CentredSidewaysTurnIsATap()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var whole = model.Parts[0].Triangles;
        var pivot = new Vector3(0.002f, 0f, 0.1f);

        void Tap(Action<MeshVolumeSolve> drag, Kind kind, string what)
        {
            var solve = new MeshVolumeSolve(model);
            Assert.True(solve.BeginMove(whole, false, 0f, mirror: new MoveMirror(kind, pivot)) > 0);
            drag(solve);
            Assert.False(solve.EndMove(), $"{what} counted as a change");
            Assert.False(solve.CanUndo, what);
            Assert.False(solve.Dirty, what);
            var after = solve.Positions();
            for (int v = 0; v < n * n; v++)
            {
                Assert.True(At(model.Positions, v) == At(after, v), $"{what}: vertex {v} moved");
                Assert.Equal(solve.AuthoredNormalAt(v), solve.NormalAt(v));
                Assert.Equal(0f, solve.WeightAt(v));
            }
        }

        Tap(s => s.MoveTo(new Vector3(0.03f, 0f, 0f)), Kind.Translate, "X arrow");
        foreach (float degrees in new[] { 30f, 120f, -45f })
            Tap(s => s.TransformTo(RotateGizmo.About(pivot, Vector3.UnitY, degrees * MathF.PI / 180f)), Kind.Rotate, $"Y ring {degrees}");
        Tap(s => s.TransformTo(RotateGizmo.About(pivot, Vector3.UnitZ, 0.7f)), Kind.Rotate, "Z ring");
        Tap(s => s.TransformTo(RotateGizmo.About(pivot, Vector3.Normalize(new Vector3(0f, 1f, 1f)), 0.6f)), Kind.Rotate, "view ring");
    }

    /// <summary>Two cups that only meet at the centre — not welded, a gap of up to an edge between them — are one
    /// piece that is its own mirror, as a welded bra is.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(Spacing)]
    [Trait("Plan", "EDGE-mirror-move-tools-27")]
    public void IslandBraMeetingAtTheCentreIsCentredLikeAWeldedOne(float gap)
    {
        var mesh = new MirrorMesh();
        var left = mesh.Grid(Steps(-0.1f - gap / 2f, 11), Steps(0f, 11));
        var right = mesh.Grid(Steps(gap / 2f, 11), Steps(0f, 11));
        var model = mesh.Build();
        var both = All(left).Concat(All(right)).ToArray();

        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(both, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, both));
        Assert.Equal(Mode.Centred, solve.DragMirror?.Mode);
        solve.MoveTo(new Vector3(0.02f, 0.01f, 0f));
        var after = solve.Positions();
        foreach (int v in both) AssertNear(At(model.Positions, v) + new Vector3(0f, 0.01f, 0f), At(after, v), 1e-7f, "cup");
    }

    // ── sides and the centre strip ──────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-4")]
    [Trait("Plan", "EDGE-mirror-move-tools-2")]
    public void PartlyStraddlingSelectionBlendsWithoutTearing()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var solve = new MeshVolumeSolve(model);
        var sel = Patch(n, 9, 18, 0, n - 1);             // x from −10 mm to 80 mm
        var o = new Vector3(0.02f, 0f, 0f);

        solve.BeginMove(sel, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, sel));
        Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);
        solve.MoveTo(o);
        var after = solve.Positions();
        for (int j = 0; j < n; j++)
        {
            Assert.Equal(0f, At(after, 10 * n + j).X, 1e-7f);
            for (int i = 12; i <= 18; i++) AssertNear(At(model.Positions, i * n + j) + o, At(after, i * n + j), 1e-7f, "beyond the strip");
            for (int i = 2; i <= 8; i++) AssertNear(At(model.Positions, i * n + j) + Reflect(o), At(after, i * n + j), 1e-7f, "mirror side");
            // Across the selection, its match and the strip between (the edges beyond them meet unmoved cloth, as with
            // Mirror off).
            for (int i = 2; i < 18; i++)
                Assert.True(At(after, (i + 1) * n + j).X > At(after, i * n + j).X, $"row {j}: columns {i} and {i + 1} crossed");
        }
        AssertSymmetric(after, n, 1e-7f, "straddling");
        solve.EndMove();
        static bool Inner(int v) => v / n is >= 2 and <= 18;
        var (folded, _) = FoldedTriangles(model, model.Positions, solve.Positions(), (a, b, c) => Inner(a) && Inner(b) && Inner(c));
        Assert.Equal(0, folded);
    }

    public static TheoryData<string> CupDrags => new()
    {
        "turn +30", "turn -30", "turn +90", "turn -90", "squash", "stretch", "move in", "move out",
    };

    /// <summary>One cup of a pair welded at the gore, turned, squashed, stretched or moved toward and away from the
    /// centre: the gore stays on the centre, the result mirrors, and no triangle turns over.</summary>
    [Theory]
    [MemberData(nameof(CupDrags))]
    [Trait("Plan", "AC-mirror-move-tools-4")]
    [Trait("Plan", "EDGE-mirror-move-tools-3")]
    [Trait("Plan", "EDGE-mirror-move-tools-25")]
    public void SidesFoldTests(string drag)
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var cup = Patch(n, 10, 20, 0, n - 1);
        var pivot = BoundsCentre(model.Positions, cup);
        var solve = new MeshVolumeSolve(model);
        var kind = drag.StartsWith("turn") ? Kind.Rotate : drag.StartsWith("move") ? Kind.Translate : Kind.Scale;
        solve.BeginMove(cup, false, 0f, mirror: new MoveMirror(kind, pivot));
        Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);

        // Stepped, so a band that jumped mid-drag would show as a jump.
        const int steps = 30;
        var last = solve.Positions();
        for (int k = 1; k <= steps; k++)
        {
            float f = k / (float)steps;
            switch (drag)
            {
                case "turn +30": solve.TransformTo(RotateGizmo.About(pivot, Vector3.UnitY, f * MathF.PI / 6f)); break;
                case "turn -30": solve.TransformTo(RotateGizmo.About(pivot, Vector3.UnitY, -f * MathF.PI / 6f)); break;
                case "turn +90": solve.TransformTo(RotateGizmo.About(pivot, Vector3.UnitY, f * MathF.PI / 2f)); break;
                case "turn -90": solve.TransformTo(RotateGizmo.About(pivot, Vector3.UnitY, -f * MathF.PI / 2f)); break;
                case "squash": solve.TransformTo(PartScaleDrag.Build(pivot, new Vector3(1f - 0.5f * f, 1f, 1f))); break;
                case "stretch": solve.TransformTo(PartScaleDrag.Build(pivot, new Vector3(1f + 0.5f * f, 1f, 1f))); break;
                case "move in": solve.MoveTo(new Vector3(-0.02f * f, 0f, 0f)); break;
                default: solve.MoveTo(new Vector3(0.02f * f, 0f, 0f)); break;
            }
            var now = solve.Positions();
            float jump = 0f;
            for (int v = 0; v < n * n; v++) jump = MathF.Max(jump, Vector3.Distance(At(last, v), At(now, v)));
            Assert.True(jump < 0.012f, $"{drag}: a vertex jumped {jump * 1000f:F2} mm at step {k}");
            last = now;
        }
        for (int j = 0; j < n; j++) Assert.Equal(0f, At(last, 10 * n + j).X, 1e-6f);
        AssertSymmetric(last, n, 1e-6f, drag);
        if (drag.EndsWith("90"))
        {
            // A cup welded to its twin and turned a quarter turn must fold at the gore — Mirror off folds it too.
            // Mirrored, no worse than the same turn on both sides with Mirror off.
            var plain = new MeshVolumeSolve(model);
            plain.BeginMove(cup, false, 0f);
            plain.TransformTo(RotateGizmo.About(pivot, Vector3.UnitY, (drag[5] == '+' ? 1f : -1f) * MathF.PI / 2f));
            var (plainFolded, _) = FoldedTriangles(model, model.Positions, plain.Positions());
            var (folded, _) = FoldedTriangles(model, model.Positions, last);
            Assert.True(plainFolded > 0, $"{drag}: the fixture no longer folds with Mirror off");
            Assert.True(folded <= 2 * plainFolded, $"{drag}: {folded} folded, Mirror off folds {plainFolded} on one side");
        }
        else AssertNoFolds(model, model.Positions, last, drag);
        solve.EndMove();
    }

    /// <summary>A piece well off the centre turned a long way: the band never widens, so all of it follows the ring
    /// exactly, every step, with no jump.</summary>
    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-4")]
    public void OffCentreTurnFollowsTheRingExactlyAllTheWay()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var strap = Patch(n, 17, 19, 8, 12);
        var pivot = BoundsCentre(model.Positions, strap);
        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(strap, false, 0f, mirror: new MoveMirror(Kind.Rotate, pivot));
        for (int deg = 1; deg <= 90; deg++)
        {
            var t = RotateGizmo.About(pivot, Vector3.UnitY, deg * MathF.PI / 180f);
            solve.TransformTo(t);
            var now = solve.Positions();
            foreach (int v in strap) AssertNear(Vector3.Transform(At(model.Positions, v), t), At(now, v), 1e-6f, $"{deg}°");
        }
        solve.EndMove();
    }

    /// <summary>A move away from the centre leaves the band as it was: the user's side moves exactly by the offset.</summary>
    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-4")]
    public void MoveAwayFromTheCentreKeepsTheUserSideExact()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var sel = Patch(n, 12, 14, 8, 12);
        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(sel, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, sel));
        var o = new Vector3(0.05f, 0f, 0f);
        solve.MoveTo(o);
        foreach (int v in sel) AssertNear(At(model.Positions, v) + o, At(solve.Positions(), v), 1e-7f, "selected");
        solve.EndMove();
    }

    /// <summary>A mirrored move stretches the strip at the centre, so the strip is relit like a falloff band.</summary>
    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-4")]
    public void SidesMoveRelightsTheCentreStrip()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var cup = Patch(n, 10, 20, 0, n - 1);
        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(cup, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, cup));
        solve.MoveTo(new Vector3(-0.02f, 0f, 0f));
        Assert.True(solve.EndMove());
        Assert.True(solve.WeightAt(11 * n + 10) > 0f, "the centre strip was not relit");
        Assert.True(solve.WeightAt(9 * n + 10) > 0f, "the mirrored centre strip was not relit");
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-move-tools-4")]
    public void SeparateMirroredPiecesSpread()
    {
        var mesh = new MirrorMesh();
        var right = mesh.Grid(Steps(0.06f, 3), Steps(0f, 11));
        var left = mesh.Grid(Steps(-0.08f, 3), Steps(0f, 11));
        var model = mesh.Build();
        var both = All(right).Concat(All(left)).ToArray();
        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(both, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, both));
        Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);
        var o = new Vector3(0.01f, 0.005f, 0f);
        solve.MoveTo(o);
        var after = solve.Positions();
        foreach (int v in All(right)) AssertNear(At(model.Positions, v) + o, At(after, v), 1e-7f, "+X strap");
        foreach (int v in All(left)) AssertNear(At(model.Positions, v) + Reflect(o), At(after, v), 1e-7f, "−X strap");
    }

    /// <summary>A sash across the centre is not its own mirror and has nothing to mirror: it moves exactly as with
    /// Mirror off, sideways included.</summary>
    [Fact]
    [Trait("Plan", "EDGE-mirror-move-tools-26")]
    [Trait("Plan", "AC-mirror-move-tools-5")]
    public void CrossingPieceThatIsNotItsOwnMirrorMovesAsMirrorOff()
    {
        var mesh = new MirrorMesh();
        var sash = mesh.Grid(Steps(-0.03f, 16), Steps(0f, 11));
        var model = mesh.Build();
        var all = All(sash);
        var o = new Vector3(0.02f, 0.01f, 0f);

        var plain = new MeshVolumeSolve(model);
        plain.BeginMove(all, false, 0f);
        plain.MoveTo(o);
        var mirrored = new MeshVolumeSolve(model);
        mirrored.BeginMove(all, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, all));
        mirrored.MoveTo(o);

        Assert.Equal(Mode.Off, mirrored.DragMirror?.Mode);
        Assert.Equal(plain.Positions(), mirrored.Positions());
    }

    // ── never refused ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-5")]
    [Trait("Plan", "EDGE-mirror-move-tools-5")]
    public void UnpairedNodesStillMoveOnTheirOwnSide()
    {
        var mesh = new MirrorMesh();
        var grid = mesh.Grid(Steps(-0.1f, 21), Steps(0f, 21));
        var extra = mesh.Grid(Steps(0.04f, 3), Steps(0.3f, 3));          // a tab on the right only
        var model = mesh.Build();
        var patch = Cells(grid, 14, 16, 8, 12);
        var sel = patch.Concat(All(extra)).ToArray();
        var o = new Vector3(0.01f, 0.01f, 0f);

        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(sel, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, sel));
        solve.MoveTo(o);
        var after = solve.Positions();
        foreach (int v in All(extra)) AssertNear(At(model.Positions, v) + o, At(after, v), 1e-7f, "unpaired tab");
        foreach (int v in Cells(grid, 4, 6, 8, 12)) AssertNear(At(model.Positions, v) + Reflect(o), At(after, v), 1e-7f, "mirror");
        var report = solve.DragMirror!.Value;
        Assert.Equal(sel.Length, report.Selected);
        Assert.Equal(patch.Length, report.Paired);
        Assert.Equal(Mode.Sides, report.Mode);
    }

    /// <summary>
    /// A piece with no mirror image, selected on the far side from the gizmo alongside one that has: with the falloff on,
    /// the cloth it carries goes the way it goes — not the mirrored way its side would — so nothing tears between them.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-5")]
    [Trait("Plan", "EDGE-mirror-move-tools-5")]
    public void ClothAroundAnUnmatchedPieceGoesItsWay()
    {
        var mesh = new MirrorMesh();
        var grid = mesh.Grid(Steps(-0.1f, 21), Steps(0f, 21));
        var flap = mesh.Grid(Steps(-0.1f, 9), Steps(0.3f, 11));          // left only: nothing mirrors it
        var model = mesh.Build();
        var patch = Cells(grid, 14, 16, 8, 12);
        var tab = Cells(flap, 2, 4, 2, 4);
        var sel = patch.Concat(tab).ToArray();
        var how = Mirrored(Kind.Translate, model.Positions, sel);
        Assert.True(MathF.Abs(how.Pivot.X) < 0.0114f || how.Pivot.X > 0f, "the fixture's gizmo is meant on the +X side");

        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(sel, true, 0.03f, mirror: how);
        Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);
        var o = new Vector3(0.02f, 0f, 0f);
        solve.MoveTo(o);
        var after = solve.Positions();
        foreach (int v in tab) AssertNear(At(model.Positions, v) + o, At(after, v), 1e-7f, "tab");
        int carried = 0;
        foreach (int v in All(flap))
        {
            float dx = At(after, v).X - At(model.Positions, v).X;
            Assert.True(dx >= 0f, $"flap vertex {v} went {dx * 1000f:F2} mm the other way");
            if (dx > 0f && !tab.Contains(v)) carried++;
        }
        Assert.True(carried > 0, "the falloff carried nothing around the tab");
        var (folded, _) = FoldedTriangles(model, model.Positions, after);
        Assert.Equal(0, folded);
    }

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-5")]
    [Trait("Plan", "EDGE-mirror-move-tools-6")]
    public void NoMatchMovesAndPreviewsExactlyAsMirrorOff()
    {
        var mesh = new MirrorMesh();
        var sheet = mesh.Grid(Steps(0.2f, 6), Steps(0f, 6));
        var model = mesh.Build();
        var all = All(sheet);

        var plain = new MeshVolumeSolve(model);
        plain.BeginMove(all, true, 0.02f);
        plain.TransformTo(RotateGizmo.About(new Vector3(0.22f, 0f, 0.02f), Vector3.UnitY, 0.3f));
        var mirrored = new MeshVolumeSolve(model);
        mirrored.BeginMove(all, true, 0.02f, mirror: new MoveMirror(Kind.Rotate, new Vector3(0.22f, 0f, 0.02f)));
        mirrored.TransformTo(RotateGizmo.About(new Vector3(0.22f, 0f, 0.02f), Vector3.UnitY, 0.3f));
        Assert.Equal(plain.Positions(), mirrored.Positions());
        Assert.Equal(new MeshVolumeSolve.MirrorReport(all.Length, 0, 0, Mode.Off), mirrored.DragMirror);

        var preview = new MeshVolumeSolve(model);
        var shown = preview.MoveFalloff(all, 0f, false, mirror: true);
        Assert.Equal(Mode.Off, preview.PreviewMirror?.Mode);
        Assert.False(PartsPanel.ShowsMoveFalloff(true, adjacent: false, falloffMm: 20f, mirror: true, preview.PreviewMirror?.Mode));
        Assert.Equal(new MeshVolumeSolve(model).MoveFalloff(all, 0f, false), shown);
    }

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-5")]
    [Trait("Plan", "EDGE-mirror-move-tools-7")]
    public void LockedPartnersStayPutAndCountAsHeld()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var patch = Patch(n, 14, 16, 8, 12);
        var mirror = Patch(n, 4, 6, 8, 12);
        var solve = new MeshVolumeSolve(model);
        solve.SetLocked(mirror);
        solve.BeginMove(patch, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        solve.MoveTo(new Vector3(0.01f, 0.01f, 0f));
        AssertUnmoved(model.Positions, solve.Positions(), mirror, "locked mirror");
        var report = solve.DragMirror!.Value;
        Assert.Equal(patch.Length, report.Paired);
        Assert.Equal(patch.Length, report.Held);
    }

    // ── what counts as the match ────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-10")]
    [Trait("Plan", "EDGE-mirror-move-tools-21")]
    public void SkinIsNeverAPartner()
    {
        var mesh = new MirrorMesh();
        var skin = mesh.Grid(Steps(-0.097f, 21), Steps(0f, 21), y: 0f, material: SkinMaterial);   // a body 3 mm off centre
        var cloth = mesh.Grid(Steps(-0.1f, 21), Steps(0f, 21), y: 0.001f);
        var model = mesh.Build();
        var patch = Cells(cloth, 14, 16, 8, 12);
        var o = new Vector3(0.005f, 0.01f, 0f);

        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(patch, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        solve.MoveTo(o);
        var after = solve.Positions();
        AssertUnmoved(model.Positions, after, All(skin), "skin");
        foreach (int v in Cells(cloth, 4, 6, 8, 12)) AssertNear(At(model.Positions, v) + Reflect(o), At(after, v), 1e-7f, "mirrored cloth");
        Assert.Equal(0, solve.DragMirror!.Value.Held);
        Assert.Equal(patch.Length, solve.DragMirror!.Value.Paired);
    }

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-10")]
    [Trait("Plan", "EDGE-mirror-move-tools-22")]
    public void MatchingLayerIsChosenOverAnother()
    {
        var mesh = new MirrorMesh();
        var bra = mesh.Grid(Steps(-0.1f, 21), Steps(0f, 21), y: 0f, material: "/mt_bra.mtrl");
        var jacket = mesh.Grid(Steps(-0.1f, 21), Steps(0f, 21), y: 0.003f, material: "/mt_jacket.mtrl");
        var model = mesh.Build();
        var patch = Cells(bra, 14, 16, 8, 12);
        var o = new Vector3(0.003f, -0.002f, 0f);

        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(patch, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        solve.MoveTo(o);
        var after = solve.Positions();
        AssertUnmoved(model.Positions, after, All(jacket), "the other layer");
        foreach (int v in Cells(bra, 4, 6, 8, 12)) AssertNear(At(model.Positions, v) + Reflect(o), At(after, v), 1e-7f, "the same layer");
    }

    /// <summary>The other side meshed twice as finely, with the falloff off: all of its matching region moves whole, so
    /// nothing is left behind as a spike, and nothing folds.</summary>
    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-10")]
    [Trait("Plan", "EDGE-mirror-move-tools-23")]
    [Trait("Plan", "EDGE-mirror-move-tools-18")]
    public void MirrorWithoutAdjacentLeavesNoSpikes()
    {
        var mesh = new MirrorMesh();
        var right = mesh.Grid(Steps(0.01f, 10), Steps(0f, 11));
        var left = mesh.Grid(Steps(-0.1f, 19, Spacing / 2f), Steps(0f, 21, Spacing / 2f));
        var model = mesh.Build();
        var patch = Cells(right, 3, 5, 4, 6);                       // x 40..60 mm, z 40..60 mm
        var o = new Vector3(0.002f, 0.01f, 0f);

        foreach (var kind in new[] { Kind.Translate, Kind.Rotate })
        {
            var solve = new MeshVolumeSolve(model);
            var how = Mirrored(kind, model.Positions, patch);
            var turn = RotateGizmo.About(how.Pivot, Vector3.UnitY, 0.2f);
            Func<Vector3, Vector3> mirrored = kind == Kind.Translate
                ? p => p + Reflect(o)
                : p => Vector3.Transform(p, MirrorMotion.Reflect(turn));
            solve.BeginMove(patch, false, 0f, mirror: how);
            if (kind == Kind.Translate) solve.MoveTo(o);
            else solve.TransformTo(turn);
            var after = solve.Positions();

            foreach (int v in All(left))
            {
                var p = At(model.Positions, v);
                bool inside = p.X is >= -0.0601f and <= -0.0399f && p.Z is >= 0.0399f and <= 0.0601f;
                if (inside) AssertNear(mirrored(p), At(after, v), 1e-6f, $"{kind}: inside the mirrored region, left behind");
                else if (At(after, v) != p)
                {
                    Assert.True(p.X is >= -0.066f and <= -0.034f && p.Z is >= 0.034f and <= 0.066f,
                                $"{kind}: vertex {v} at {p} moved, more than half an edge outside the mirrored region");
                    AssertNear(mirrored(p), At(after, v), 1e-6f, $"{kind}: the ring around the mirrored region");
                }
            }
            AssertNoFolds(model, model.Positions, after, kind.ToString());
        }
    }

    /// <summary>A double-sided card welds opposite normals into nothing; it still pairs.</summary>
    [Fact]
    [Trait("Plan", "EDGE-mirror-move-tools-29")]
    public void ZeroNormalNodesStillPair()
    {
        var mesh = new MirrorMesh();
        var front = mesh.Grid(Steps(-0.1f, 21), Steps(0f, 21));
        mesh.Grid(Steps(-0.1f, 21), Steps(0f, 21), normalY: -1f);
        var model = mesh.Build();
        var patch = Cells(front, 14, 16, 8, 12);
        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(patch, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        solve.MoveTo(new Vector3(0.01f, 0.01f, 0f));
        Assert.Equal(patch.Length, solve.DragMirror!.Value.Paired);
        foreach (int v in Cells(front, 4, 6, 8, 12))
            AssertNear(At(model.Positions, v) + new Vector3(-0.01f, 0.01f, 0f), At(solve.Positions(), v), 1e-7f, "mirrored");
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-move-tools-8")]
    public void PairsByAuthoredPositionAfterAnUnmirroredMove()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var right = Patch(n, 14, 16, 8, 12);
        var left = Patch(n, 4, 6, 8, 12);
        var solve = new MeshVolumeSolve(model);

        var away = new Vector3(0f, 0.03f, 0f);
        solve.BeginMove(left, false, 0f);
        solve.MoveTo(away);
        solve.EndMove();

        var o = new Vector3(0.01f, 0.005f, 0f);
        solve.BeginMove(right, false, 0f, mirror: Mirrored(Kind.Translate, solve.Positions(), right));
        solve.MoveTo(o);
        foreach (int v in left) AssertNear(At(model.Positions, v) + away + Reflect(o), At(solve.Positions(), v), 1e-6f, "left");
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-move-tools-9")]
    public void FallsBackToCurrentPositionsAndRebuildsAfterEdits()
    {
        var mesh = new MirrorMesh();
        var right = mesh.Grid(Steps(0.06f, 3), Steps(0.3f, 11));      // authored 300 mm further forward than the left
        var left = mesh.Grid(Steps(-0.08f, 3), Steps(0f, 11));
        var model = mesh.Build();
        var solve = new MeshVolumeSolve(model);

        // Straighten the right strap by hand: now it mirrors the left one where it stands.
        solve.BeginMove(All(right), false, 0f);
        solve.MoveTo(new Vector3(0f, 0f, -0.3f));
        solve.EndMove();

        solve.BeginMove(All(left), false, 0f, mirror: Mirrored(Kind.Translate, solve.Positions(), All(left)));
        Assert.Equal(All(left).Length, solve.DragMirror!.Value.Paired);
        solve.MoveTo(new Vector3(0f, 0.002f, 0f));
        solve.EndMove();

        // Lift both straps together: they match again, at their new place — found only by a search of where the right
        // strap stands now, not where it stood when last searched.
        solve.BeginMove(All(left).Concat(All(right)), false, 0f);
        solve.MoveTo(new Vector3(0f, 0.05f, 0f));
        solve.EndMove();
        solve.BeginMove(All(left), false, 0f, mirror: Mirrored(Kind.Translate, solve.Positions(), All(left)));
        Assert.Equal(All(left).Length, solve.DragMirror!.Value.Paired);
        solve.EndMove();

        // Move the right strap well away: it no longer matches, whatever was found before.
        solve.BeginMove(All(right), false, 0f);
        solve.MoveTo(new Vector3(0f, 0.08f, 0f));
        solve.EndMove();
        solve.BeginMove(All(left), false, 0f, mirror: Mirrored(Kind.Translate, solve.Positions(), All(left)));
        Assert.Equal(0, solve.DragMirror!.Value.Paired);
        Assert.Equal(Mode.Off, solve.DragMirror!.Value.Mode);
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-move-tools-10")]
    public void OverlappingFalloffsAtTheMidlineNeverDoubleMove()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var patch = Patch(n, 12, 12, 9, 11);                      // 20 mm from the centre
        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(patch, true, 0.04f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        solve.MoveTo(new Vector3(0f, 0.01f, 0f));
        var after = solve.Positions();
        for (int v = 0; v < n * n; v++)
            Assert.True(At(after, v).Y <= 0.01f + 1e-7f, $"vertex {v} moved {At(after, v).Y * 1000f:F3} mm");
        AssertSymmetric(after, n, 1e-7f, "overlap");
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-move-tools-11")]
    public void MidlineNodePairsWithItselfAndStaysOnTheMidline()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var sel = Patch(n, 10, 14, 8, 12);
        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(sel, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, sel));
        solve.MoveTo(new Vector3(0.02f, 0f, 0f));
        Assert.Equal(sel.Length, solve.DragMirror!.Value.Paired);
        Assert.Equal(sel.Length, solve.DragMirror!.Value.Selected);
        for (int j = 8; j <= 12; j++) Assert.Equal(0f, At(solve.Positions(), 10 * n + j).X, 1e-7f);
        AssertSymmetric(solve.Positions(), n, 1e-7f, "midline");
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-move-tools-12")]
    public void MixedSelectionIsCentred()
    {
        var mesh = new MirrorMesh();
        var bra = mesh.Grid(Steps(-0.1f, 21), Steps(0f, 11));
        var strap = mesh.Grid(Steps(0.15f, 3), Steps(0f, 5));
        var model = mesh.Build();
        var sel = All(bra).Concat(All(strap)).ToArray();
        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(sel, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, sel));
        Assert.Equal(Mode.Centred, solve.DragMirror?.Mode);
        solve.MoveTo(new Vector3(0.02f, 0.01f, 0.003f));
        foreach (int v in All(strap))
            AssertNear(At(model.Positions, v) + new Vector3(0f, 0.01f, 0.003f), At(solve.Positions(), v), 1e-7f, "strap");
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-move-tools-20")]
    public void SharedPartnerTakesOneFullWeight()
    {
        var mesh = new MirrorMesh();
        var right = mesh.Grid(Steps(0.01f, 19, Spacing / 2f), Steps(0f, 21, Spacing / 2f));
        var left = mesh.Grid(Steps(-0.1f, 10), Steps(0f, 11));
        var model = mesh.Build();
        var patch = Cells(right, 6, 10, 6, 10);                      // x 40..60 mm, z 30..50 mm
        var o = new Vector3(0.01f, 0.01f, 0f);
        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(patch, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        solve.MoveTo(o);
        var after = solve.Positions();
        int moved = 0;
        foreach (int v in All(left))
        {
            if (At(after, v) == At(model.Positions, v)) continue;
            moved++;
            AssertNear(At(model.Positions, v) + Reflect(o), At(after, v), 1e-7f, "partner");
        }
        Assert.True(moved >= 9, $"only {moved} partner(s) moved");
    }

    // ── undo and normals ────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-6")]
    [Trait("Plan", "EDGE-mirror-move-tools-14")]
    public void MirroredTapLeavesNothing()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var patch = Patch(n, 14, 16, 8, 12);
        foreach (var kind in new[] { Kind.Translate, Kind.Rotate, Kind.Scale })
        {
            var solve = new MeshVolumeSolve(model);
            solve.BeginMove(patch, true, 0.02f, mirror: Mirrored(kind, model.Positions, patch));
            if (kind == Kind.Translate) solve.MoveTo(Vector3.Zero);
            else solve.TransformTo(Matrix4x4.Identity);
            Assert.False(solve.EndMove(), $"{kind}: a tap counted as a change");
            Assert.False(solve.CanUndo);
            Assert.False(solve.Dirty);
            for (int v = 0; v < n * n; v++)
            {
                Assert.Equal(0f, solve.WeightAt(v));
                Assert.True(solve.MovedAt(v).Equals(default(SecondSkinWriter.Vec3)), $"{kind}: vertex {v} kept a move");
            }
        }
    }

    /// <summary>
    /// Two hard 40 mm boxes, one each side of the centre, the left the mirror image of the right vertex for vertex
    /// (vertex k of the left box is vertex k of the right reflected) — as part 1.1 — and a far sheet as 1.2.
    /// </summary>
    private static (ModelParts Model, int PerBox, Vector3 RightCentre) MirroredBoxes()
    {
        var (box, perBox) = HardBox();
        var shift = new Vector3(0.1f, 0.1f, 0.5f) - BoxCentre;
        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();
        for (int v = 0; v < perBox; v++)
        {
            var p = At(box.Positions, v) + shift;
            pos.AddRange([p.X, p.Y, p.Z]);
            nrm.AddRange([box.Normals[v * 3], box.Normals[v * 3 + 1], box.Normals[v * 3 + 2]]);
        }
        for (int v = 0; v < perBox; v++)
        {
            var p = Reflect(At(box.Positions, v) + shift);
            pos.AddRange([p.X, p.Y, p.Z]);
            nrm.AddRange([-box.Normals[v * 3], box.Normals[v * 3 + 1], box.Normals[v * 3 + 2]]);
        }
        var boxTris = box.Parts[0].Triangles;
        tris.AddRange(boxTris);
        for (int i = 0; i + 2 < boxTris.Length; i += 3)
            tris.AddRange([boxTris[i] + perBox, boxTris[i + 2] + perBox, boxTris[i + 1] + perBox]);   // reflected winding
        var far = new List<int>();
        Sheet(5, (_, _) => 0f, 1f, pos, nrm, far);
        for (int v = 2 * perBox; v < pos.Count / 3; v++) pos[v * 3 + 2] += 2f;
        return (Assemble(pos, nrm, tris, far.ToArray()), perBox, new Vector3(0.1f, 0.1f, 0.5f));
    }

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-8")]
    [Trait("Plan", "EDGE-mirror-move-tools-16")]
    public void MirroredRotateGivesPartnersTheMirrorMapsNormals()
    {
        var (model, perBox, centre) = MirroredBoxes();
        var rightBox = Enumerable.Range(0, perBox).ToArray();
        foreach (var (kind, t) in new[]
                 {
                     (Kind.Rotate, RotateGizmo.About(centre, Vector3.Normalize(new Vector3(0.3f, 0.2f, 1f)), 0.6f)),
                     (Kind.Scale, PartScaleDrag.Build(centre, new Vector3(1f, 1.6f, 1f))),
                 })
        {
            var solve = new MeshVolumeSolve(model);
            solve.BeginMove(rightBox, false, 0f, mirror: new MoveMirror(kind, centre));
            solve.TransformTo(t);
            Assert.True(solve.EndMove());

            var map = LinearNormals.NormalMap(t)!.Value;
            for (int v = 0; v < perBox; v++)
            {
                var expected = Vector3.Normalize(Vector3.TransformNormal(V(solve.AuthoredNormalAt(v)), map));
                AssertNormal(expected, solve.NormalAt(v), 1e-5f, $"{kind}: right box vertex {v}");
                AssertNormal(Reflect(V(solve.NormalAt(v))), solve.NormalAt(v + perBox), 1e-5f, $"{kind}: left box vertex {v}");
            }

            // A later brush stroke elsewhere settles the model again: the exact normals stand, still mirrored.
            solve.Paint(At(model.Positions, 2 * perBox + 12), 0.02f, 0.001f);
            solve.EndStroke();
            for (int v = 0; v < perBox; v++)
                AssertNormal(Reflect(V(solve.NormalAt(v))), solve.NormalAt(v + perBox), 1e-5f, $"{kind}: after a stroke, vertex {v}");
        }
    }

    /// <summary>
    /// A hard box on the centre line is its own mirror; a centred stretch gives it the stretch's exact normals — its flat
    /// sides stay flat-shaded — where turned normals would tilt its corners' split normals.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-8")]
    [Trait("Plan", "EDGE-mirror-move-tools-16")]
    public void CentredStretchGivesTheStretchsNormals()
    {
        var (box, perBox) = HardBox();
        var positions = (float[])box.Positions.Clone();
        for (int v = 0; v < perBox; v++) positions[v * 3] -= BoxCentre.X;          // onto the centre line
        var model = new ModelParts
        {
            Positions = positions, Normals = box.Normals, MeshSpans = box.MeshSpans, Parts = box.Parts,
            AttributeNames = box.AttributeNames, Min = box.Min, Max = box.Max, ShatteredSubmeshes = box.ShatteredSubmeshes,
        };
        var pivot = new Vector3(0.01f, BoxCentre.Y, BoxCentre.Z);                   // the gizmo a little off the centre
        var t = PartScaleDrag.Build(pivot, new Vector3(1f, 1.6f, 1f));
        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(Enumerable.Range(0, perBox), false, 0f, mirror: new MoveMirror(Kind.Scale, pivot));
        Assert.Equal(Mode.Centred, solve.DragMirror?.Mode);
        solve.TransformTo(t);
        Assert.True(solve.EndMove());

        var map = LinearNormals.NormalMap(Matrix4x4.Identity + MirrorMotion.CentredLinear(t, Kind.Scale))!.Value;
        var turned = TurnedNormalsOf(solve, model);
        bool turnedDiffers = false;
        for (int v = 0; v < perBox; v++)
        {
            var expected = Vector3.Normalize(Vector3.TransformNormal(V(solve.AuthoredNormalAt(v)), map));
            AssertNormal(expected, solve.NormalAt(v), 1e-5f, $"box vertex {v}");
            turnedDiffers |= Vector3.Dot(expected, Vector3.Normalize(V(turned[v]))) < MathF.Cos(MathF.PI / 180f);
        }
        Assert.True(turnedDiffers, "the fixture does not tell exact normals from turned ones");
    }

    /// <summary>A centre seam left a hair off X = 0 is on the centre plane: an outward drag sends none of its points
    /// sideways, so the seam does not zigzag.</summary>
    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-4")]
    [Trait("Plan", "EDGE-mirror-move-tools-11")]
    public void ACentreSeamAHairOffZeroStaysOnTheCentre()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var positions = (float[])model.Positions.Clone();
        const float hair = 4e-6f;                                                 // under 1e-3 of an edge
        for (int j = 0; j < n; j++) positions[(10 * n + j) * 3] = j % 2 == 0 ? hair : -hair;
        model = new ModelParts
        {
            Positions = positions, Normals = model.Normals, MeshSpans = model.MeshSpans, Parts = model.Parts,
            AttributeNames = model.AttributeNames, Min = model.Min, Max = model.Max, ShatteredSubmeshes = model.ShatteredSubmeshes,
        };
        var cup = Patch(n, 10, 20, 0, n - 1);
        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(cup, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, cup));
        Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);
        solve.MoveTo(new Vector3(0.02f, 0f, 0f));
        var after = solve.Positions();
        for (int j = 0; j < n; j++)
            Assert.True(MathF.Abs(At(after, 10 * n + j).X - positions[(10 * n + j) * 3]) <= 1e-7f,
                        $"seam row {j} went {At(after, 10 * n + j).X * 1000f:F3} mm");
    }

    /// <summary>
    /// The status line counts only matches that move: a piece selected alongside the one being mirrored, with a few of
    /// its points matching but too few to have a mirror image, adds nothing to it — matched or held.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Plan", "AC-mirror-move-tools-5")]
    [Trait("Plan", "EDGE-mirror-move-tools-7")]
    public void ReportCountsOnlyMatchesThatMove(bool lockTheStub)
    {
        var mesh = new MirrorMesh();
        var grid = mesh.Grid(Steps(-0.1f, 21), Steps(0f, 21));
        var sash = mesh.Grid(Steps(0.02f, 19), Steps(0.3f, 3));                   // x 20..200 mm
        var stub = mesh.Grid(Steps(-0.03f, 2), Steps(0.3f, 3));                   // mirrors the sash's first two columns only
        var model = mesh.Build();
        var patch = Cells(grid, 14, 16, 8, 12);
        var sel = patch.Concat(All(sash)).ToArray();
        var solve = new MeshVolumeSolve(model);
        if (lockTheStub) solve.SetLocked(All(stub));
        solve.BeginMove(sel, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, sel));
        Assert.Equal(new MeshVolumeSolve.MirrorReport(sel.Length, patch.Length, 0, Mode.Sides), solve.DragMirror);
        solve.MoveTo(new Vector3(0.01f, 0f, 0f));
        AssertUnmoved(model.Positions, solve.Positions(), All(stub), "the stub");
    }

    /// <summary>
    /// Cloth a turn gathers up at the centre was not carried by the turn itself, so it is given no exact normals of the
    /// turn: it keeps the normals its own new shape gives it, as a falloff band does.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-8")]
    [Trait("Plan", "EDGE-mirror-move-tools-16")]
    public void ClothGatheredAtTheCentreKeepsItsShapesNormals()
    {
        const int n = 21;
        var model = Centred(n, (i, j) => 0.004f * MathF.Sin(MathF.Abs(i - 10) * 0.9f + j * 0.7f));
        var cup = Patch(n, 10, 20, 0, n - 1);
        var pivot = BoundsCentre(model.Positions, cup);
        var t = RotateGizmo.About(pivot, Vector3.UnitY, MathF.PI / 6f);
        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(cup, false, 0f, mirror: new MoveMirror(Kind.Rotate, pivot));
        Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);
        solve.TransformTo(t);
        var after = solve.Positions();
        var gathered = cup.Where(v => v / n > 10
                                      && MathF.Abs(At(after, v).X - Vector3.Transform(At(model.Positions, v), t).X) > 1e-6f)
                          .ToArray();
        Assert.NotEmpty(gathered);
        Assert.True(solve.EndMove());

        var turned = TurnedNormalsOf(solve, model);
        var mapped = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, LinearNormals.NormalMap(t)!.Value));
        bool differs = false;
        foreach (int v in gathered)
        {
            AssertNormal(V(turned[v]), solve.NormalAt(v), 1e-5f, $"gathered vertex {v}");
            differs |= Vector3.Distance(mapped, V(solve.NormalAt(v))) > 1e-3f;
        }
        Assert.True(differs, "the gathered cloth's normals do not tell a record from none");
    }

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-6")]
    [Trait("Plan", "EDGE-mirror-move-tools-15")]
    public void OneUndoTakesBackBothSidesAndTheirNormals()
    {
        var (model, perBox, centre) = MirroredBoxes();
        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(Enumerable.Range(0, perBox), false, 0f, mirror: new MoveMirror(Kind.Rotate, centre));
        solve.TransformTo(RotateGizmo.About(centre, Vector3.UnitZ, 0.5f));
        Assert.True(solve.EndMove());
        Assert.NotEqual(solve.AuthoredNormalAt(perBox + 4), solve.NormalAt(perBox + 4));

        solve.Undo();
        var back = solve.Positions();
        for (int v = 0; v < model.Positions.Length / 3; v++)
        {
            AssertNear(At(model.Positions, v), At(back, v), 1e-7f, $"vertex {v}");
            Assert.Equal(solve.AuthoredNormalAt(v), solve.NormalAt(v));
        }
        Assert.False(solve.CanUndo);
    }

    // ── what the views are shown ────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-7")]
    public void DragFalloffAndPreviewCoverBothSides()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var patch = Patch(n, 14, 16, 8, 12);
        var solve = new MeshVolumeSolve(model);
        var preview = solve.MoveFalloff(patch, 0.03f, false, mirror: true)!.ToArray();
        solve.BeginMove(patch, true, 0.03f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        var drag = solve.DragFalloff()!;
        Assert.Equal(preview, drag);
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            Assert.Equal(drag[i * n + j], drag[(n - 1 - i) * n + j], 1e-6f);
        Assert.Equal(1f, drag[5 * n + 10]);
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-move-tools-28")]
    public void DragReportIsKeptApartFromThePreviews()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var patch = Patch(n, 14, 16, 8, 12);
        var solve = new MeshVolumeSolve(model);

        solve.BeginMove(patch, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);
        solve.MoveFalloff(model.Parts[0].Triangles, 0f, false, mirror: true);
        Assert.Equal(Mode.Centred, solve.PreviewMirror?.Mode);
        Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);

        solve.MoveFalloff(patch, 0f, false, mirror: false);
        Assert.Null(solve.PreviewMirror);
        solve.MoveFalloff(patch, 0f, false, mirror: true);
        Assert.NotNull(solve.PreviewMirror);
        solve.MoveFalloff([], 0f, false, mirror: true);
        Assert.Null(solve.PreviewMirror);

        solve.EndMove();
        solve.BeginMove(patch, false, 0f);
        Assert.Null(solve.DragMirror);
    }
}
