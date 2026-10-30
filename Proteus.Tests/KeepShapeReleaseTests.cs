using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Keep shape lets go of a piece that moving whole would sink into the new body (<see cref="BodyRetarget.ReleaseSink"/>).
/// <para/>
/// A box of half 0.20 m, each face a fine grid, whose +Z face grows a smooth dome at its centre; flat cloth plates 1 mm
/// over that face. Moved point by point, a plate follows the dome. Moved whole, it can only shift, turn and scale, so
/// its middle stays under the dome's top. Real body scale throughout, as <see cref="BodyRetargetTests"/> explains.
/// </summary>
public class KeepShapeReleaseTests
{
    private const string SkinMaterial = "/mt_c0201b0001_bibo.mtrl";
    private const string ClothMaterial = "/mt_c0201e6255_top_a.mtrl";

    internal const float Half = 0.20f;
    internal const float DomeRadius = 0.04f;

    /// <summary>A dome tall enough that a 6 cm plate moved whole sinks well past the threshold.</summary>
    internal const float TallDome = 0.020f;

    /// <summary>A dome low enough that the same plate, moved whole, stays within the threshold.</summary>
    internal const float LowDome = 0.004f;

    // ── the pieces that sink are let go ───────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-keep-shape-release-4")]
    [Trait("Plan", "EDGE-keep-shape-release-14")]
    [Trait("Speed", "Slow")]
    public void Plate_over_a_growing_bump_is_released_and_bends()
    {
        var plate = Plate(Vector3.Zero, 0.06f, 0.001f);
        var keep = new[] { AllVertices(plate) };

        var released = Solve(plate, TallDome, keep);
        var unticked = Solve(plate, TallDome, []);
        var rigid = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoRelease: true), () => Solve(plate, TallDome, keep));

        var outcome = Assert.Single(released.Shapes!);
        Assert.True(outcome.Judged);
        Assert.True(outcome.Released, $"a plate sinking {outcome.Sink * 1000:F2} mm should have been let go");
        Assert.True(outcome.Sink > BodyRetarget.ReleaseSink);

        // Let go, it comes out exactly as if it had never been ticked — every later pass treats it as cloth.
        AssertSameDeltas(unticked, released, plate);

        // And it no longer sits in the dome, where moved whole it did.
        var dome = Box(TallDome);
        Assert.True(DeepestInside(plate, released, dome) < 0.0005f,
                    $"released, the plate is still {DeepestInside(plate, released, dome) * 1000:F2} mm inside the dome");
        Assert.True(DeepestInside(plate, rigid, dome) > BodyRetarget.ReleaseSink,
                    "the fixture should sink the plate when it is moved whole, or the test proves nothing");
    }

    [Fact]
    [Trait("Plan", "AC-keep-shape-release-4")]
    [Trait("Speed", "Slow")]
    public void A_user_ticked_large_panel_is_released_too()
    {
        // 20 cm across: HardPieces would never pick it, so only a user ticking it keeps it. The rule is the same.
        var panel = Plate(Vector3.Zero, 0.20f, 0.001f, step: 0.01f);

        var solved = Solve(panel, TallDome, [AllVertices(panel)]);

        var outcome = Assert.Single(solved.Shapes!);
        Assert.True(outcome.Released, $"the panel sinks {outcome.Sink * 1000:F2} mm moved whole and should bend instead");
    }

    [Fact]
    [Trait("Plan", "AC-keep-shape-release-4")]
    [Trait("Plan", "AC-keep-shape-release-9")]
    [Trait("Speed", "Slow")]
    public void Plate_over_a_small_bump_stays_kept()
    {
        var plate = Plate(Vector3.Zero, 0.06f, 0.001f);
        var keep = new[] { AllVertices(plate) };

        var solved = Solve(plate, LowDome, keep);
        var old = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoRelease: true), () => Solve(plate, LowDome, keep));

        var outcome = Assert.Single(solved.Shapes!);
        Assert.True(outcome.Judged);
        Assert.False(outcome.Released, $"a plate sinking only {outcome.Sink * 1000:F2} mm should stay whole");
        Assert.True(outcome.Sink > 0f, "the low dome should still sink the plate a little, or this proves nothing");
        AssertSameDeltas(old, solved, plate);
        AssertMovedWhole(plate, solved, AllVertices(plate));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-release-1")]
    public void Buries_is_strict_at_the_threshold()
    {
        Assert.False(BodyRetarget.Buries(BodyRetarget.ReleaseSink));
        Assert.True(BodyRetarget.Buries(BodyRetarget.ReleaseSink + 1e-6f));
        Assert.False(BodyRetarget.Buries(0f));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-release-2")]
    public void Nothing_is_released_without_push_out()
    {
        var plate = Plate(Vector3.Zero, 0.06f, 0.001f);
        var keep = new[] { AllVertices(plate) };

        var solved = Solve(plate, TallDome, keep, pushOut: false);
        var old = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoRelease: true),
                                          () => Solve(plate, TallDome, keep, pushOut: false));

        var outcome = Assert.Single(solved.Shapes!);
        Assert.False(outcome.Judged);
        Assert.False(outcome.Released);
        AssertSameDeltas(old, solved, plate);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-release-3")]
    [Trait("Speed", "Slow")]
    public void No_kept_pieces_reports_nothing()
    {
        var plate = Plate(Vector3.Zero, 0.06f, 0.001f);

        var none = Solve(plate, TallDome, null);
        var empty = Solve(plate, TallDome, []);
        var old = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoRelease: true), () => Solve(plate, TallDome, null));

        Assert.Null(none.Shapes);
        Assert.Null(empty.Shapes);
        AssertSameDeltas(old, none, plate);
        AssertSameDeltas(old, empty, plate);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-release-4")]
    [Trait("Speed", "Slow")]
    public void All_released_equals_no_keep_shape()
    {
        // Two plates, each over a dome of its own and both sinking: with both let go the refit is the one with none kept.
        var a = Plate(Vector3.Zero, 0.06f, 0.001f);
        var b = Plate(new Vector3(0f, SecondDome, 0f), 0.06f, 0.001f);
        var garment = Merge(a, b);
        int na = a.Positions.Length / 3;
        var keep = new IReadOnlyCollection<int>[]
        {
            Enumerable.Range(0, na).ToArray(),
            Enumerable.Range(na, b.Positions.Length / 3).ToArray(),
        };

        var solved = Solve(garment, TallDome, keep);

        Assert.All(solved.Shapes!, o => Assert.True(o.Released, $"sink {o.Sink * 1000:F2} mm"));
        AssertSameDeltas(Solve(garment, TallDome, null), solved, garment);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-release-5")]
    [Trait("Speed", "Slow")]
    public void An_unplaced_piece_is_not_judged()
    {
        var plate = Plate(Vector3.Zero, 0.06f, 0.001f);
        var all = AllVertices(plate);

        // Two vertices: too few for KeepShape to fit a turn to.
        var twoPoints = Solve(plate, TallDome, [new[] { 0, 1 }]);
        var two = Assert.Single(twoPoints.Shapes!);
        Assert.False(two.Judged);
        Assert.False(two.Released);

        // Every vertex held: nothing of the piece may move, so it is never placed.
        var heldAll = Solve(plate, TallDome, [all], held: new HashSet<int>(all));
        var held = Assert.Single(heldAll.Shapes!);
        Assert.False(held.Judged);
        Assert.False(held.Released);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-release-6")]
    public void A_piece_tucked_into_the_skin_as_authored_stays_kept()
    {
        // A plate the author pressed 3 mm into the skin, on a body that does not change: the push-out leaves an
        // authored tuck where it is, so moved whole the plate sinks no deeper than bent, and stays whole.
        var plate = Plate(Vector3.Zero, 0.06f, -0.003f);
        var keep = new[] { AllVertices(plate) };

        var solved = Solve(plate, 0f, keep);

        var outcome = Assert.Single(solved.Shapes!);
        Assert.True(outcome.Judged, $"{outcome}");
        Assert.False(outcome.Released, $"an authored tuck read as {outcome.Sink * 1000:F2} mm of sinking");
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-release-6")]
    public void PieceSink_does_not_charge_a_tuck_the_push_out_cleared()
    {
        // Clear body on: the push-out lifted a point the author put 3 mm into the skin, and the whole move keeps it
        // there. That depth was the author's before the refit, so it is not sinking. (A solid tucked whole is
        // flattened by that push and KeepShape cannot place it again — the solve-level case never reaches here.)
        static float SignedOff(Vector3 p) => p.Z;   // the same flat skin at z = 0 before and after

        Vector3[] at = [new(0, 0, -0.003f)];
        Vector3[] kept = [Vector3.Zero];
        Vector3[] pushed = [new(0, 0, 0.004f)];

        Assert.Equal(0f, BodyRetarget.PieceSink([0], at, kept, pushed, SignedOff, SignedOff));
        // Without the authored skin the same point reads as sunk 3 mm.
        Assert.Equal(0.003f, BodyRetarget.PieceSink([0], at, kept, pushed, SignedOff, null), 5);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-release-7")]
    public void A_piece_far_off_the_body_is_never_buried()
    {
        var plate = Plate(Vector3.Zero, 0.06f, 0.04f);

        var solved = Solve(plate, TallDome, [AllVertices(plate)]);

        var outcome = Assert.Single(solved.Shapes!);
        Assert.Equal(0f, outcome.Sink);
        Assert.False(outcome.Released);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-release-8")]
    [Trait("Speed", "Slow")]
    public void A_node_shared_with_a_kept_piece_follows_the_kept_piece()
    {
        // Two plates sharing an edge (welded by position): one over the dome, which sinks, one well off it, which
        // does not. The sinking one is let go, and the refit is the one that only ever kept the other.
        var sinking = Plate(Vector3.Zero, 0.08f, 0.001f);
        var steady = Plate(new Vector3(0.07f, 0f, 0f), 0.06f, 0.001f);
        var garment = Merge(sinking, steady);
        int ns = sinking.Positions.Length / 3;
        var first = Enumerable.Range(0, ns).ToArray();
        var second = Enumerable.Range(ns, steady.Positions.Length / 3).ToArray();

        var solved = Solve(garment, TallDome, [first, second]);

        Assert.True(solved.Shapes![0].Released, $"sink {solved.Shapes[0].Sink * 1000:F2} mm");
        Assert.False(solved.Shapes[1].Released, $"sink {solved.Shapes[1].Sink * 1000:F2} mm");
        AssertSameDeltas(Solve(garment, TallDome, [second]), solved, garment);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-release-11")]
    [Trait("Speed", "Slow")]
    public void Each_target_is_judged_on_its_own()
    {
        var plate = Plate(Vector3.Zero, 0.06f, 0.001f);
        var keep = new[] { AllVertices(plate) };

        Assert.True(Assert.Single(Solve(plate, TallDome, keep).Shapes!).Released);
        Assert.False(Assert.Single(Solve(plate, 0f, keep).Shapes!).Released);
    }

    [Fact]
    [Trait("Plan", "AC-keep-shape-release-4")]
    public void A_piece_under_the_smallest_size_stays_whole_however_deep()
    {
        // A stud, a pearl, a little rose sinks deeper than its own size (La Vie en Rose's 1.7 cm roses 16.5 mm), and
        // bent point by point it would come out squashed flat on the skin: under ReleaseMinSize it is never let go.
        Assert.False(BodyRetarget.Releases(0.0165f, 0.017f));
        Assert.False(BodyRetarget.Releases(0.0165f, BodyRetarget.ReleaseMinSize - 1e-4f));
        Assert.True(BodyRetarget.Releases(0.0064f, 0.113f));               // Ash's cup cover
        Assert.True(BodyRetarget.Releases(0.0064f, BodyRetarget.ReleaseMinSize));
        Assert.False(BodyRetarget.Releases(0.0036f, 0.122f));              // pop's arm band
        // A piece's size is its widest spread along an axis.
        Assert.Equal(0.02f, BodyRetarget.PieceSize([0, 1, 2, 3],
                                                   [new(0, 0, 0), new(0.02f, 0, 0), new(0, 0.01f, 0), new(0, 0, 0.005f)]), 5);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-release-13")]
    public void PieceSink_ignores_non_finite_points()
    {
        // A flat skin at z = 0 facing +Z: inside is z < 0.
        static float SignedOff(Vector3 p) => p.Z;

        Vector3[] at = [new(0, 0, 0.001f), new(0.01f, 0, 0.001f), new(0, 0.01f, 0.001f)];
        Vector3[] kept = [new(0, 0, -0.004f), new(float.NaN, 0, 0), new(0, float.PositiveInfinity, 0)];
        Vector3[] pushed = [new(0, 0, 0f), new(0, 0, 0f), new(0, 0, 0f)];

        float sink = BodyRetarget.PieceSink([0, 1, 2], at, kept, pushed, SignedOff, null);

        Assert.True(float.IsFinite(sink));
        Assert.Equal(0.003f, sink, 5);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-release-15")]
    [Trait("Speed", "Slow")]
    public void Report_carries_the_first_round()
    {
        // The steady plate first this time, so the released one is at index 1.
        var steady = Plate(new Vector3(0.09f, 0f, 0f), 0.04f, 0.001f);
        var sinking = Plate(Vector3.Zero, 0.06f, 0.001f);
        var garment = Merge(steady, sinking);
        int ns = steady.Positions.Length / 3;
        var keep = new IReadOnlyCollection<int>[]
        {
            Enumerable.Range(0, ns).ToArray(),
            Enumerable.Range(ns, sinking.Positions.Length / 3).ToArray(),
        };

        var solved = Solve(garment, TallDome, keep);

        Assert.Equal(2, solved.Shapes!.Count);
        Assert.False(solved.Shapes[0].Released);
        Assert.True(solved.Shapes[1].Released);
        AssertSameDeltas(Solve(garment, TallDome, [keep[0]]), solved, garment);
    }

    // ── the fixture ──────────────────────────────────────────────────────────────────────────────────────

    internal static BodyRetarget.Solved Solve(ModelParts garment, float dome, IReadOnlyList<IReadOnlyCollection<int>>? keep,
                                             bool pushOut = true, bool clearBody = false, IReadOnlySet<int>? held = null,
                                             float radius = DomeRadius)
    {
        var from = Box(0f);
        var to = Box(dome, radius);
        Assert.True(IdentityCorrespondence.TryBuild(from, to, "chest", out var built, out string refusal), refusal);
        return BodyRetarget.Solve(garment, [new BodyRetarget.SlotPair("_top", built!, to)], pushOut: pushOut,
                                  held: held, clearBody: clearBody, keepShape: keep);
    }

    internal static int[] AllVertices(ModelParts m) => Enumerable.Range(0, m.Positions.Length / 3).ToArray();

    internal static void AssertSameDeltas(BodyRetarget.Solved expected, BodyRetarget.Solved actual, ModelParts garment)
    {
        for (int v = 0; v < garment.Positions.Length / 3; v++)
        {
            var e = expected.Edit.DeltaAt(v);
            var a = actual.Edit.DeltaAt(v);
            Assert.True(e.X == a.X && e.Y == a.Y && e.Z == a.Z,
                        $"vertex {v} moved ({a.X}, {a.Y}, {a.Z}), expected ({e.X}, {e.Y}, {e.Z})");
        }
    }

    /// <summary>Moved whole: every pairwise distance scaled by the one factor.</summary>
    internal static void AssertMovedWhole(ModelParts garment, BodyRetarget.Solved solved, int[] piece)
    {
        Vector3 Before(int v) => new(garment.Positions[v * 3], garment.Positions[v * 3 + 1], garment.Positions[v * 3 + 2]);
        Vector3 After(int v)
        {
            var d = solved.Edit.DeltaAt(v);
            return Before(v) + new Vector3(d.X, d.Y, d.Z);
        }

        int anchor = piece[0], far = piece[^1];
        float scale = Vector3.Distance(After(anchor), After(far)) / Vector3.Distance(Before(anchor), Before(far));
        foreach (int v in piece.Skip(1))
        {
            float was = Vector3.Distance(Before(anchor), Before(v));
            float now = Vector3.Distance(After(anchor), After(v));
            Assert.True(MathF.Abs(now - was * scale) < 1e-4f, $"vertex {v} bent: {now} vs {was * scale}");
        }
    }

    /// <summary>The deepest any moved vertex lies inside <paramref name="body"/>'s +Z face, measured straight down.</summary>
    private static float DeepestInside(ModelParts garment, BodyRetarget.Solved solved, ModelParts body)
    {
        float deepest = 0f;
        for (int v = 0; v < garment.Positions.Length / 3; v++)
        {
            var d = solved.Edit.DeltaAt(v);
            float x = garment.Positions[v * 3] + d.X, y = garment.Positions[v * 3 + 1] + d.Y;
            float z = garment.Positions[v * 3 + 2] + d.Z;
            deepest = MathF.Max(deepest, Half + DomeAt(x, y, DomeHeight(body)) - z);
        }
        return deepest;
    }

    private static float DomeHeight(ModelParts body)
    {
        float top = float.MinValue;
        for (int v = 0; v < body.Positions.Length / 3; v++) top = MathF.Max(top, body.Positions[v * 3 + 2]);
        return top - Half;
    }

    /// <summary>Where the box's second dome stands, along y: well clear of every other plate.</summary>
    private const float SecondDome = 0.10f;

    /// <summary>The domes' height at (x, y): one at the face's centre, one at y = <see cref="SecondDome"/>.</summary>
    private static float DomeAt(float x, float y, float height, float radius = DomeRadius)
    {
        float One(float x, float y)
        {
            float r2 = (x * x + y * y) / (radius * radius);
            return r2 >= 1f ? 0f : height * (1f - r2) * (1f - r2);
        }
        return One(x, y) + One(x, y - SecondDome);
    }

    /// <summary>
    /// A closed box of half <see cref="Half"/>, each face a grid of 5 mm quads, whose +Z face stands out in a smooth dome
    /// of <paramref name="dome"/> at its centre. The same vertex order whatever the dome, so an identity correspondence
    /// carries one onto another.
    /// </summary>
    internal static ModelParts Box(float dome, float radius = DomeRadius)
    {
        const int n = 80;
        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();

        Span<Vector3> axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        for (int a = 0; a < 3; a++)
        for (int sign = -1; sign <= 1; sign += 2)
        {
            var normal = axes[a] * sign;
            var u = axes[(a + 1) % 3];
            var w = axes[(a + 2) % 3];
            if (sign < 0) (u, w) = (w, u);

            int baseVertex = pos.Count / 3;
            for (int iu = 0; iu <= n; iu++)
            for (int iw = 0; iw <= n; iw++)
            {
                var p = normal * Half + u * ((iu * 2f / n - 1f) * Half) + w * ((iw * 2f / n - 1f) * Half);
                if (a == 2 && sign > 0) p.Z += DomeAt(p.X, p.Y, dome, radius);
                pos.Add(p.X); pos.Add(p.Y); pos.Add(p.Z);
                nrm.Add(normal.X); nrm.Add(normal.Y); nrm.Add(normal.Z);
            }

            for (int iu = 0; iu < n; iu++)
            for (int iw = 0; iw < n; iw++)
            {
                int p00 = baseVertex + iu * (n + 1) + iw;
                int p10 = baseVertex + (iu + 1) * (n + 1) + iw;
                int p01 = baseVertex + iu * (n + 1) + iw + 1;
                int p11 = baseVertex + (iu + 1) * (n + 1) + iw + 1;
                tris.AddRange([p00, p10, p11]);
                tris.AddRange([p00, p11, p01]);
            }
        }

        return Build(pos.ToArray(), nrm.ToArray(), tris.ToArray(), SkinMaterial);
    }

    /// <summary>The thickness of a <see cref="Plate"/>: a solid piece, not a sheet — KeepShape cannot tell how a piece
    /// with no depth at all turned, and leaves it alone.</summary>
    private const float Thick = 0.0015f;

    /// <summary>
    /// A square cloth slab <paramref name="size"/> across, centred on <paramref name="centre"/> (x, y): its underside
    /// <paramref name="off"/> over the box's flat +Z face facing down, its top <see cref="Thick"/> above that facing up.
    /// </summary>
    internal static ModelParts Plate(Vector3 centre, float size, float off, float step = 0.005f)
    {
        int n = (int)MathF.Round(size / step);
        int layer = (n + 1) * (n + 1);
        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();
        foreach (var (z, up) in new[] { (Half + off, false), (Half + off + Thick, true) })
            for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                pos.Add(centre.X - size / 2 + i * step);
                pos.Add(centre.Y - size / 2 + j * step);
                pos.Add(z);
                nrm.Add(0); nrm.Add(0); nrm.Add(up ? 1 : -1);
            }
        for (int l = 0; l < 2; l++)
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            int b = l * layer;
            int p00 = b + i * (n + 1) + j, p10 = b + (i + 1) * (n + 1) + j, p01 = p00 + 1, p11 = p10 + 1;
            if (l == 1) tris.AddRange([p00, p10, p11, p00, p11, p01]);   // facing up
            else tris.AddRange([p00, p11, p10, p00, p01, p11]);           // facing down
        }
        return Build(pos.ToArray(), nrm.ToArray(), tris.ToArray(), ClothMaterial);
    }

    /// <summary>Two cloth models as one, the second's vertices after the first's.</summary>
    internal static ModelParts Merge(ModelParts a, ModelParts b)
    {
        int na = a.Positions.Length / 3;
        return Build([.. a.Positions, .. b.Positions], [.. a.Normals, .. b.Normals],
                     [.. a.Parts[0].Triangles, .. b.Parts[0].Triangles.Select(t => t + na)], ClothMaterial);
    }

    internal static ModelParts Build(float[] pos, float[] nrm, int[] tris, string material)
    {
        int vc = pos.Length / 3;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int v = 0; v < vc; v++)
        {
            var p = new Vector3(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        var part = new ModelPart
        {
            Mesh = 0,
            Submesh = 0,
            Island = -1,
            Label = "0.0",
            Material = material,
            Triangles = tris,
            Ordinals = Enumerable.Range(0, tris.Length / 3).ToArray(),
            AttributeMask = 0,
            Min = min,
            Max = max,
            Toggleable = true,
        };

        return new ModelParts
        {
            Positions = pos,
            Normals = nrm,
            MeshSpans = [new MeshSpan(0, 0, vc)],
            Parts = [part],
            AttributeNames = [],
            Min = min,
            Max = max,
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
    }
}
