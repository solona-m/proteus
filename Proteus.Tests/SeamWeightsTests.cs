using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// A seam's split vertices through a refit's weights: one place of the cloth, split between two submeshes (or two
/// meshes) and weighted alike by the author, comes out on one weighting — the blouse of voidblood's "Dei Gratia" parted at
/// the waist when its two sides were smoothed apart (see <see cref="BodyRetarget.KeepSeamsTogether"/>).
/// </summary>
public class SeamWeightsTests(ITestOutputHelper output)
{
    private const string Cloth = "/mt_c0201e0600_top_a.mtrl";
    private const string Layer = "/mt_c0201e0600_top_b.mtrl";

    private static readonly (string Bone, float W)[] Kosi = [("j_kosi", 1f)];

    // ── small models, for calling the pass directly ────────────────────────────────────────────────────────

    /// <summary>One mesh of single triangles, one per submesh: each point given is the corner 0 of a triangle of its
    /// own, its other two corners a centimetre away, so no two given points share an edge.</summary>
    private static SyntheticModel.SurfaceMesh Points(string material, Vector3[] at, (string Bone, float W)[][] weights)
    {
        var pos = new List<Vector3>();
        var w = new List<(string Bone, float W)[]?>();
        var subs = new List<(uint, int[])>();
        for (int i = 0; i < at.Length; i++)
        {
            int b = pos.Count;
            float side = i % 2 == 0 ? -1f : 1f;
            pos.AddRange([at[i], at[i] + new Vector3(0.01f * side, 0f, 0f), at[i] + new Vector3(0.01f * side, 0.01f, 0f)]);
            w.AddRange([weights[i], weights[i], weights[i]]);
            subs.Add((0u, [b, b + 1, b + 2]));
        }
        return Surface(material, [.. pos], [.. w], [.. subs]);
    }

    private static SyntheticModel.SurfaceMesh Surface(string material, Vector3[] pos, (string Bone, float W)[]?[] w,
                                                      (uint, int[])[] subs)
        => new(material, pos, [.. pos.Select(_ => Vector3.UnitZ)], [.. pos.Select(_ => new Vector2(0.5f, 0.5f))], w, subs);

    /// <summary>The given points' vertex indices in a <see cref="Points"/> mesh that comes first in its model.</summary>
    private static int At(int point) => point * 3;

    private static (ModelParts Model, XivLiveMesh.SkinnedMesh Skin) Read(byte[] mdl)
        => (ModelPartReader.Read(mdl)!, ModelSkinReader.Read(mdl, null, null)!);

    private static BodyRetarget.SeamOutcome Run(byte[] mdl, (string Bone, float W)[]?[] result, IReadOnlySet<int>? held = null,
                                                IReadOnlyList<IReadOnlyCollection<int>>? keepShape = null)
    {
        var (model, skin) = Read(mdl);
        return BodyRetarget.KeepSeamsTogether(model, skin, model.Positions, result, held, keepShape);
    }

    private static (string Bone, float W)[]?[] Empty(byte[] mdl) => new (string Bone, float W)[]?[ModelPartReader.Read(mdl)!.Positions.Length / 3];

    /// <summary>Summed difference between two weightings, by bone.</summary>
    private static float D(IEnumerable<(string Bone, float W)> a, IEnumerable<(string Bone, float W)> b)
    {
        var d = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (bone, w) in a) d[bone] = d.GetValueOrDefault(bone) + w;
        foreach (var (bone, w) in b) d[bone] = d.GetValueOrDefault(bone) - w;
        return d.Values.Sum(MathF.Abs);
    }

    private static byte[] Pair(float gap, (string Bone, float W)[] a, (string Bone, float W)[] b)
        => SyntheticModel.BuildSurface([], Points(Cloth, [new Vector3(0f, 1f, 0.1f), new Vector3(gap, 1f, 0.1f)], [a, b]));

    [Fact]
    [Trait("Plan", "EDGE-seam-weights-2")]
    public void A_seam_already_weighted_as_one_is_left_bit_for_bit()
    {
        var mdl = Pair(1e-5f, Kosi, Kosi);
        var result = Empty(mdl);
        (string Bone, float W)[] a = [("j_kosi", 0.7f), ("j_sebo_a", 0.3f)], b = [("j_kosi", 0.7f), ("j_sebo_a", 0.3f)];
        result[At(0)] = a;
        result[At(1)] = b;
        var outcome = Run(mdl, result);
        Assert.Same(a, result[At(0)]);
        Assert.Same(b, result[At(1)]);
        Assert.Equal(0, outcome.Averaged);
        Assert.Equal(BodyRetarget.SeamKind.Agreeing, outcome.Kinds[outcome.ClassOf[At(0)]]);
    }

    [Fact]
    [Trait("Plan", "EDGE-seam-weights-3")]
    public void Same_place_vertices_in_two_meshes_share_one_weighting_each_mesh_counting_once()
    {
        // Mesh 0 carries the place twice (two submeshes), mesh 1 once: the mean is of the two meshes, not of three vertices.
        var p = new Vector3(0f, 1f, 0.1f);
        var mdl = SyntheticModel.BuildSurface([], Points(Cloth, [p, p + new Vector3(1e-5f, 0f, 0f)], [Kosi, Kosi]),
                                              Points(Layer, [p + new Vector3(0f, 1e-5f, 0f)], [Kosi]));
        var result = Empty(mdl);
        result[At(0)] = [("j_kosi", 1f)];
        result[At(1)] = [("j_kosi", 1f)];
        result[6] = [("j_sebo_a", 1f)];
        Run(mdl, result);
        foreach (int v in new[] { At(0), At(1), 6 })
        {
            Assert.Equal(0.5f, result[v]!.Single(i => i.Bone == "j_kosi").W, 4);
            Assert.Equal(0.5f, result[v]!.Single(i => i.Bone == "j_sebo_a").W, 4);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-seam-weights-4")]
    public void A_seam_beyond_the_bodies_stays_the_authors()
    {
        var mdl = Pair(1e-5f, Kosi, Kosi);
        var result = Empty(mdl);
        var outcome = Run(mdl, result);
        Assert.Null(result[At(0)]);
        Assert.Null(result[At(1)]);
        Assert.Equal(0, outcome.Net);
    }

    [Fact]
    [Trait("Plan", "EDGE-seam-weights-5")]
    public void A_null_member_reads_its_authored_weights_and_takes_the_shared_result()
    {
        var mdl = Pair(1e-5f, Kosi, Kosi);
        var result = Empty(mdl);
        result[At(1)] = [("j_sebo_a", 1f)];
        var outcome = Run(mdl, result);
        Assert.Equal(1, outcome.Added);
        Assert.Equal(1, outcome.Net);
        Assert.Equal(0f, D(result[At(0)]!, result[At(1)]!));
        Assert.Equal(0.5f, result[At(0)]!.Single(i => i.Bone == "j_kosi").W, 4);
    }

    [Fact]
    [Trait("Plan", "AC-seam-weights-6")]
    [Trait("Plan", "EDGE-seam-weights-6")]
    public void A_seam_touching_a_held_part_keeps_the_authors_weights()
    {
        var mdl = Pair(1e-5f, Kosi, Kosi);
        var result = Empty(mdl);
        result[At(0)] = [("j_sebo_a", 1f)];
        var outcome = Run(mdl, result, held: new HashSet<int> { At(1) });
        Assert.Null(result[At(0)]);
        Assert.Null(result[At(1)]);
        Assert.Equal(1, outcome.Reverted);
        Assert.Equal(-1, outcome.Net);
        Assert.Equal(BodyRetarget.SeamKind.Reverted, outcome.Kinds[outcome.ClassOf[At(0)]]);
    }

    [Fact]
    [Trait("Plan", "AC-seam-weights-4")]
    [Trait("Plan", "EDGE-seam-weights-7")]
    public void Same_place_vertices_weighted_apart_by_the_author_stay_apart()
    {
        var mdl = Pair(1e-5f, [("j_kosi", 1f)], [("j_kosi", 0.95f), ("j_sebo_a", 0.05f)]);
        var result = Empty(mdl);
        result[At(0)] = [("j_kosi", 1f)];
        result[At(1)] = [("j_sebo_a", 1f)];
        var outcome = Run(mdl, result);
        Assert.Equal(-1, outcome.ClassOf[At(0)]);
        Assert.Equal(1f, result[At(0)]!.Single().W);
        Assert.Equal("j_sebo_a", result[At(1)]!.Single().Bone);
    }

    [Fact]
    [Trait("Plan", "AC-seam-weights-4")]
    [Trait("Plan", "EDGE-seam-weights-21")]
    public void Neighbours_a_millimetre_apart_are_not_averaged()
    {
        var mdl = Pair(0.001f, Kosi, Kosi);
        var result = Empty(mdl);
        result[At(0)] = [("j_kosi", 1f)];
        result[At(1)] = [("j_sebo_a", 1f)];
        var outcome = Run(mdl, result);
        Assert.Equal(-1, outcome.ClassOf[At(0)]);
        Assert.Equal("j_kosi", result[At(0)]!.Single().Bone);
    }

    [Theory]
    [InlineData(2e-4f)]
    [InlineData(0f)]
    [Trait("Plan", "AC-seam-weights-4")]
    [Trait("Plan", "EDGE-seam-weights-23")]
    public void Vertices_joined_by_an_edge_are_never_one_seam(float gap)
    {
        // One triangle whose first two corners are `gap` apart (zero: a degenerate edge): neighbours along the surface.
        var pos = new[] { new Vector3(0f, 1f, 0.1f), new Vector3(gap, 1f, 0.1f), new Vector3(0f, 1.01f, 0.1f) };
        var mdl = SyntheticModel.BuildSurface([], Surface(Cloth, pos, [Kosi, Kosi, Kosi], [(0u, [0, 1, 2])]));
        var result = Empty(mdl);
        result[0] = [("j_kosi", 1f)];
        result[1] = [("j_sebo_a", 1f)];
        var outcome = Run(mdl, result);
        Assert.Equal(-1, outcome.ClassOf[0]);
        Assert.Equal("j_kosi", result[0]!.Single().Bone);
        Assert.Equal("j_sebo_a", result[1]!.Single().Bone);
    }

    private static byte[] Row(float[] xs, (string Bone, float W)[][] weights)
        => SyntheticModel.BuildSurface([], Points(Cloth, [.. xs.Select(x => new Vector3(x, 1f, 0.1f))], weights));

    [Fact]
    [Trait("Plan", "EDGE-seam-weights-8")]
    public void Near_points_within_reach_of_each_other_are_one_seam_and_a_far_end_is_not()
    {
        var mdl = Row([0f, 2e-4f, 4e-4f, 8e-4f], [Kosi, Kosi, Kosi, Kosi]);
        var result = Empty(mdl);
        for (int i = 0; i < 4; i++) result[At(i)] = [("j_kosi", 1f - 0.2f * i), ("j_sebo_a", 0.2f * i)];
        var outcome = Run(mdl, result);
        int c = outcome.ClassOf[At(0)];
        Assert.True(c >= 0);
        Assert.Equal(c, outcome.ClassOf[At(1)]);
        Assert.Equal(c, outcome.ClassOf[At(2)]);
        Assert.NotEqual(c, outcome.ClassOf[At(3)]);
        Assert.Equal(0f, D(result[At(0)]!, result[At(2)]!));
        Assert.Equal(3, outcome.LargestClass);
        Assert.True(Widest(mdl, outcome) <= BodyRetarget.SeamReach + 1e-7f, $"a class {Widest(mdl, outcome) * 1e6:0} µm wide");
    }

    [Fact]
    [Trait("Plan", "EDGE-seam-weights-9")]
    public void A_class_never_holds_weights_further_apart_than_the_cap()
    {
        (string, float)[] W(float s) => [("j_kosi", 1f - s), ("j_sebo_a", s)];
        // Each a little over 0.01 from the last (summed 0.03 per step of 0.015): the second is 0.03 from the root.
        var mdl = Row([0f, 2e-4f, 4e-4f], [W(0f), W(0.0075f), W(0.015f)]);
        var result = Empty(mdl);
        for (int i = 0; i < 3; i++) result[At(i)] = [("j_kosi", 1f - 0.2f * i), ("j_sebo_a", 0.2f * i)];
        var outcome = Run(mdl, result);
        Assert.Equal(outcome.ClassOf[At(0)], outcome.ClassOf[At(1)]);
        Assert.NotEqual(outcome.ClassOf[At(0)], outcome.ClassOf[At(2)]);
    }

    [Fact]
    [Trait("Plan", "EDGE-seam-weights-10")]
    public void A_seam_carrying_more_than_eight_bones_keeps_eight_none_new()
    {
        var mdl = Pair(1e-5f, Kosi, Kosi);
        var result = Empty(mdl);
        string[] bones = [.. Enumerable.Range(0, 10).Select(i => $"j_bone_{i:D2}")];
        result[At(0)] = [.. bones.Take(5).Select((b, i) => (b, 0.1f + 0.02f * i))];
        result[At(1)] = [.. bones.Skip(5).Select((b, i) => (b, 0.1f + 0.02f * i))];
        Run(mdl, result);
        var shared = result[At(0)]!;
        Assert.True(shared.Length <= BodyRetarget.MaxInfluences);
        Assert.Equal(1f, shared.Sum(i => i.W), 3);
        Assert.All(shared, i => Assert.Contains(i.Bone, bones));
        Assert.Equal(0f, D(shared, result[At(1)]!));
    }

    [Fact]
    [Trait("Plan", "AC-seam-weights-5")]
    [Trait("Plan", "EDGE-seam-weights-11")]
    public void A_seam_never_carries_more_to_the_other_side_than_its_author_gave()
    {
        // Rigged 60/40 and 60.4/39.6 (a byte apart each: 0.008, within 0.02): one place. The lookups gave them 30/70 and
        // 70/30; averaged 50/50, the first would carry 0.5 to its right where its author gave 0.4 — the side limit, run
        // after, takes it back.
        (string, float)[] a = [("j_mune_l", 0.60f), ("j_mune_r", 0.40f)], b = [("j_mune_l", 0.604f), ("j_mune_r", 0.396f)];
        var mdl = Pair(1e-5f, a, b);
        var (model, skin) = Read(mdl);
        var result = Empty(mdl);
        result[At(0)] = [("j_mune_l", 0.3f), ("j_mune_r", 0.7f)];
        result[At(1)] = [("j_mune_l", 0.7f), ("j_mune_r", 0.3f)];
        var outcome = BodyRetarget.KeepSeamsTogether(model, skin, model.Positions, result, null, null);
        Assert.Equal(1, outcome.Averaged);
        var averaged = result[At(0)];
        BodyRetarget.KeepToSide(skin, result, new HashSet<string> { "j_mune_l", "j_mune_r" });
        foreach (var (v, authored) in new[] { (At(0), a), (At(1), b) })
        {
            float right = result[v]!.Where(i => i.Bone == "j_mune_r").Sum(i => i.W);
            Assert.True(right <= authored.Single(i => i.Item1 == "j_mune_r").Item2 + 1e-4f, $"vertex {v} carries {right:F3} right");
        }
        Assert.NotSame(averaged, result[At(0)]);   // clamped: what the trace's "seam-clamped" flags
    }

    [Fact]
    [Trait("Plan", "EDGE-seam-weights-12")]
    public void Skin_coincident_with_cloth_keeps_the_bodys_weights()
    {
        var p = new Vector3(0f, 1f, 0.1f);
        var mdl = SyntheticModel.BuildSurface([], Points(Cloth, [p, p + new Vector3(1e-5f, 0f, 0f)], [Kosi, Kosi]),
                                              Points(SyntheticCrotch.Skin, [p], [Kosi]));
        var result = Empty(mdl);
        result[At(0)] = [("j_kosi", 1f)];
        result[At(1)] = [("j_sebo_a", 1f)];
        (string Bone, float W)[] body = [("j_sebo_b", 1f)];
        result[6] = body;
        var outcome = Run(mdl, result);
        Assert.Equal(-1, outcome.ClassOf[6]);
        Assert.Same(body, result[6]);
        Assert.Equal(0f, D(result[At(0)]!, result[At(1)]!));
    }

    [Fact]
    [Trait("Plan", "EDGE-seam-weights-17")]
    public void A_seam_on_a_kept_whole_piece_is_left_to_the_crotch_ease()
    {
        var mdl = Pair(1e-5f, Kosi, Kosi);
        var result = Empty(mdl);
        (string Bone, float W)[] a = [("j_kosi", 1f)], b = [("j_sebo_a", 1f)];
        result[At(0)] = a;
        result[At(1)] = b;
        var outcome = Run(mdl, result, keepShape: [new[] { At(0), At(0) + 1, At(0) + 2 }]);
        Assert.Same(a, result[At(0)]);
        Assert.Same(b, result[At(1)]);
        Assert.Equal(1, outcome.SkippedKeepShape);
        Assert.Equal(BodyRetarget.SeamKind.KeepShape, outcome.Kinds[outcome.ClassOf[At(1)]]);
    }

    [Fact]
    [Trait("Plan", "AC-seam-weights-4")]
    [Trait("Plan", "EDGE-seam-weights-22")]
    public void A_dense_evenly_weighted_strip_is_not_one_seam()
    {
        // A strip 10 mm long, two rows 0.3 mm apart, columns every 0.3 mm, triangulated: every vertex edge-joined to its
        // neighbours. Weighted evenly by its author; the lookups grade it along its length.
        const int cols = 34;
        const float step = 3e-4f;
        var pos = new List<Vector3>();
        for (int r = 0; r < 2; r++)
            for (int c = 0; c < cols; c++) pos.Add(new Vector3(c * step, 1f + r * step, 0.1f));
        var tris = new List<int>();
        for (int c = 0; c + 1 < cols; c++) tris.AddRange([c, c + 1, cols + c + 1, c, cols + c + 1, cols + c]);
        // A split copy of the strip's first column, in a submesh of its own: the one seam there is.
        int seam = pos.Count;
        pos.AddRange([new Vector3(1e-5f, 1f, 0.1f), new Vector3(1e-5f, 1f + step, 0.1f), new Vector3(-0.01f, 1f, 0.1f)]);
        var mdl = SyntheticModel.BuildSurface([], Surface(Cloth, [.. pos], [.. pos.Select(_ => Kosi)],
                                                          [(0u, [.. tris]), (0u, [seam, seam + 2, seam + 1])]));
        var result = Empty(mdl);
        for (int v = 0; v < pos.Count; v++) result[v] = [("j_kosi", 1f - v % cols * 0.02f), ("j_sebo_a", v % cols * 0.02f)];
        result[seam] = [("j_sebo_a", 1f)];
        result[seam + 1] = [("j_sebo_a", 1f)];
        var before = ((string Bone, float W)[]?[])result.Clone();

        var outcome = Run(mdl, result);

        Assert.True(Widest(mdl, outcome) <= BodyRetarget.SeamReach + 1e-7f, $"a class {Widest(mdl, outcome) * 1e6:0} µm wide");
        Assert.True(outcome.LargestClass <= 2, $"a class of {outcome.LargestClass}");
        for (int t = 0; t < tris.Count; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = tris[t + k], b = tris[t + (k + 1) % 3];
                Assert.True(outcome.ClassOf[a] < 0 || outcome.ClassOf[a] != outcome.ClassOf[b], $"{a} and {b} share a class");
            }
        // The strip away from its seam keeps its gradient, untouched.
        for (int v = 0; v < 2 * cols; v++)
            if (v % cols != 0) Assert.Same(before[v], result[v]);
        Assert.Equal(0f, D(result[0]!, result[seam]!));
        output.WriteLine($"classes averaged {outcome.Averaged}, largest {outcome.LargestClass}, {outcome.LargestDiameter * 1e6:0} µm");
    }

    // ── through the weight plan: the hips, a seam ring across a weld rounding ─────────────────────────────────

    private const int Around = 48;
    private const float Out = 0.010f;     // past the skin hug, which would rig both sides as the skin alone
    private const float RingY = 0.85f;

    /// <summary>
    /// Cloth over the synthetic hips (<see cref="SyntheticCrotch"/>), 1 cm out, in one mesh of two submeshes meeting at a
    /// ring at <see cref="RingY"/>: A below (from 0.82, clear of the crotch ease), B above (to 0.95). Each has its own
    /// copy of the ring, weighted as the skin — identically — and placed so the two lie across a 10 µm weld rounding, one
    /// pair 104 µm apart, as Dei Gratia's centre front. The hips' leg share falls across the ring, so each side's
    /// neighbours pull its copy a different way.
    /// </summary>
    internal static (byte[] Garment, List<(int A, int B)> Ring) HipSeam(bool bFirst = false)
    {
        var s = SyntheticCrotch.Build();
        int Row(float y) => (int)MathF.Round((y - SyntheticCrotch.Crotch) / 0.01f);
        int rA0 = Row(0.82f), rRing = Row(RingY), rB1 = Row(0.95f);
        Vector3 P(int r, int c) => s.Pos[r * Around + c] + s.Nrm[r * Around + c] * Out;
        Vector3 Snap(Vector3 p) => p with { X = MathF.Round(p.X * 1e5f) / 1e5f + 4e-6f };

        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var w = new List<(string Bone, float W)[]?>();
        var ring = new List<(int A, int B)>();
        int[] Sheet(int r0, int r1, bool isB, Dictionary<int, int> ringIndex)
        {
            var at = new Dictionary<(int, int), int>();
            for (int r = r0; r <= r1; r++)
                for (int c = 0; c < Around; c++)
                {
                    var p = P(r, c);
                    if (r == rRing)
                    {
                        p = Snap(p);
                        if (isB) p += new Vector3(c == 0 ? 1.04e-4f : 1e-5f, 0f, 0f);
                    }
                    at[(r, c)] = pos.Count;
                    if (r == rRing) ringIndex[c] = pos.Count;
                    pos.Add(p);
                    nrm.Add(s.Nrm[r * Around + c]);
                    w.Add(s.Weights[r * Around + c]);
                }
            var tris = new List<int>();
            for (int r = r0; r < r1; r++)
                for (int c = 0; c < Around; c++)
                {
                    int a = at[(r, c)], b = at[(r, (c + 1) % Around)], a2 = at[(r + 1, c)], b2 = at[(r + 1, (c + 1) % Around)];
                    tris.AddRange([a, b2, b, a, a2, b2]);
                }
            return [.. tris];
        }
        var ringA = new Dictionary<int, int>();
        var ringB = new Dictionary<int, int>();
        int[] trisA, trisB;
        if (bFirst) { trisB = Sheet(rRing, rB1, true, ringB); trisA = Sheet(rA0, rRing, false, ringA); }
        else { trisA = Sheet(rA0, rRing, false, ringA); trisB = Sheet(rRing, rB1, true, ringB); }
        for (int c = 0; c < Around; c++) ring.Add((ringA[c], ringB[c]));
        var pa = pos.ToArray();
        var mesh = new SyntheticModel.SurfaceMesh(Cloth, pa, [.. nrm], [.. pa.Select(_ => new Vector2(0.5f, 0.5f))], [.. w],
                                                  [(0u, trisA), (0u, trisB)]);
        return (SyntheticModel.BuildSurface([], mesh), ring);
    }

    private static BodyRetarget.WeightPlan Plan(byte[] garment, bool seams = true, bool acrossBodies = true,
                                               bool before = true, IReadOnlySet<int>? held = null,
                                               Action<string, float[]>? trace = null)
    {
        var pairs = new[] { SyntheticCrotch.Pair(SyntheticCrotch.Body(), SyntheticCrotch.Body(physics: true)) };
        BodyRetarget.PushTrace.Value = trace;
        try
        {
            return BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoSeamWeights: !seams),
                () => BodyRetarget.PlanWeights(garment, pairs, acrossBodies, before: before ? garment : null, held: held))!;
        }
        finally { BodyRetarget.PushTrace.Value = null; }
    }

    private static List<(string Bone, float W)> Of(BodyRetarget.WeightPlan plan, byte[] garment, int v)
    {
        var list = plan.For(0)?[v];
        if (list != null) return [.. list];
        return BodyRetarget.Influences(ModelSkinReader.Read(garment, null, null)!, v);
    }

    private static byte[] Rebuilt(byte[] garment, BodyRetarget.WeightPlan plan)
    {
        var pairs = new[] { SyntheticCrotch.Pair(SyntheticCrotch.Body(), SyntheticCrotch.Body(physics: true)) };
        var rebuilt = BodyRetarget.Rebuild(garment, pairs, swapSkin: false, plan, out _);
        Assert.NotNull(rebuilt);
        return rebuilt!;
    }

    [Fact]
    [Trait("Plan", "AC-seam-weights-1a")]
    [Trait("Plan", "EDGE-seam-weights-1")]
    public void A_seam_split_across_the_weld_rounding_comes_out_weighted_as_one()
    {
        var (garment, ring) = HipSeam();
        // The positions the smoothing welds: each ring pair rounds to two different 10 µm keys, and shares no edge.
        var at = SyntheticCrotch.Positions(garment);
        static (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 1e5f), (int)MathF.Round(p.Y * 1e5f), (int)MathF.Round(p.Z * 1e5f));
        Assert.All(ring, r => Assert.NotEqual(Key(at[r.A]), Key(at[r.B])));

        var without = Plan(garment, seams: false);
        var split = ring.Select(r => D(Of(without, garment, r.A), Of(without, garment, r.B))).ToList();
        output.WriteLine($"without the rule: {split.Count(d => d > 0.01f)} of {ring.Count} ring pairs apart, worst {split.Max():F3}");
        // Measured at build 1382: 48 of 48 pairs apart, worst 0.154.
        Assert.True(split.Count(d => d > 0.01f) >= 40, "the fixture no longer splits the ring");

        var with = Plan(garment);
        Assert.All(ring, r => Assert.Equal(0f, D(Of(with, garment, r.A), Of(with, garment, r.B))));
        var written = SyntheticCrotch.Weights(Rebuilt(garment, with));
        Assert.All(ring, r => Assert.Equal(0f, D(written[r.A], written[r.B])));
    }

    [Fact]
    [Trait("Plan", "AC-seam-weights-3")]
    public void The_seam_rule_changes_weights_only()
    {
        var (garment, ring) = HipSeam();
        byte[] with = Rebuilt(garment, Plan(garment)), without = Rebuilt(garment, Plan(garment, seams: false));
        var a = ModelPartReader.Read(with)!;
        var b = ModelPartReader.Read(without)!;
        Assert.Equal(b.Positions, a.Positions);
        Assert.Equal(b.Normals, a.Normals);
        var wa = SyntheticCrotch.Weights(with);
        var wb = SyntheticCrotch.Weights(without);
        var onRing = ring.SelectMany(r => new[] { r.A, r.B }).ToHashSet();
        for (int v = 0; v < wa.Length; v++)
            if (!onRing.Contains(v)) Assert.Equal(0f, D(wa[v], wb[v]));
    }

    [Fact]
    [Trait("Plan", "AC-seam-weights-6")]
    [Trait("Plan", "EDGE-seam-weights-6")]
    public void A_seam_against_a_held_part_through_the_plan_keeps_the_authors_weights()
    {
        var (garment, ring) = HipSeam();
        var model = ModelPartReader.Read(garment)!;
        var held = model.Parts.Where(p => p.Island < 0).ElementAt(1).Triangles.ToHashSet();   // B, unticked
        float[]? stats = null;
        var without = Plan(garment, seams: false, held: held);
        var with = Plan(garment, held: held, trace: (k, v) => { if (k == "seam-stats") stats = v; });
        Assert.NotNull(stats);
        int reverted = (int)stats![1];
        Assert.Equal(ring.Count(r => without.For(0)?[r.A] != null), reverted);
        Assert.Equal(without.Reweighted - reverted, with.Reweighted);
        var authored = SyntheticCrotch.Weights(garment);
        var written = SyntheticCrotch.Weights(Rebuilt(garment, with));
        Assert.All(ring, r => Assert.Equal(D(authored[r.A], authored[r.B]), D(written[r.A], written[r.B]), 3));
    }

    [Fact]
    [Trait("Plan", "EDGE-seam-weights-13")]
    public void A_seam_closes_without_the_authored_garment()
    {
        var (garment, ring) = HipSeam();
        var plan = Plan(garment, before: false);
        Assert.All(ring, r => Assert.Equal(0f, D(Of(plan, garment, r.A), Of(plan, garment, r.B))));
    }

    [Fact]
    [Trait("Plan", "AC-seam-weights-7")]
    [Trait("Plan", "EDGE-seam-weights-14")]
    public void A_seam_is_deterministic_for_a_given_file()
    {
        var (garment, ring) = HipSeam();
        var a = Plan(garment);
        var b = Plan(garment);
        foreach (var (k, list) in a.PerMesh)
            for (int v = 0; v < list.Length; v++)
                Assert.Equal(list[v] is null ? "" : string.Join(",", list[v]!), b.PerMesh[k][v] is null ? "" : string.Join(",", b.PerMesh[k][v]!));

        // Built the other way round, B's sheet first: the ring's shared weights, place by place, the same.
        var (swapped, ringSwapped) = HipSeam(bFirst: true);
        var c = Plan(swapped);
        for (int i = 0; i < ring.Count; i++)
        {
            var x = Of(a, garment, ring[i].A).ToDictionary(t => t.Bone, t => t.W);
            var y = Of(c, swapped, ringSwapped[i].A).ToDictionary(t => t.Bone, t => t.W);
            foreach (var bone in x.Keys.Union(y.Keys))
                Assert.Equal(x.GetValueOrDefault(bone), y.GetValueOrDefault(bone), 3);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-seam-weights-16")]
    public void Within_one_body_mod_a_split_seam_is_closed_too()
    {
        // Within one body mod, two slots whose rigs both change, a seam between them: each copy nearest a different body
        // reads a different new bone.
        var left = FlatPair("_top", Flat(-0.03f, -0.001f, Kosi), Flat(-0.03f, -0.001f, [("j_sebo_a", 1f)]));
        var right = FlatPair("_glv", Flat(0.001f, 0.03f, Kosi), Flat(0.001f, 0.03f, [("j_sebo_b", 1f)]));
        var garment = SeamBetween(Kosi, Kosi);
        BodyRetarget.WeightPlan Run(bool seams) => BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoSeamWeights: !seams),
            () => BodyRetarget.PlanWeights(garment, [left, right], acrossBodies: false, before: garment))!;

        var without = Run(false);
        float apart = D(Of(without, garment, At(0)), Of(without, garment, At(1)));
        Assert.True(apart > 0.5f, $"the copies read only {apart:F3} apart without the rule");
        var with = Run(true);
        Assert.Equal(0f, D(Of(with, garment, At(0)), Of(with, garment, At(1))));
    }

    /// <summary>A seam's two copies 0.4 mm apart across x = 0, 5 mm out from <see cref="Flat"/> bodies either side.</summary>
    private static byte[] SeamBetween((string Bone, float W)[] left, (string Bone, float W)[] right)
    {
        var p = new Vector3(-2e-4f, 1f, 0.005f);
        return SyntheticModel.BuildSurface([], Points(Cloth, [p, p + new Vector3(4e-4f, 0f, 0f)], [left, right]));
    }

    [Fact]
    [Trait("Plan", "AC-seam-weights-5")]
    [Trait("Plan", "EDGE-seam-weights-11")]
    public void Through_the_plan_a_seam_never_carries_more_to_the_other_side_than_its_author_gave()
    {
        // Rigged 60/40 and 60.4/39.6 (a byte apart): one place. The body on the left turns to the right breast, the body on
        // the right to the left; averaged, the left copy would carry half to its right where its author gave 0.4.
        (string, float)[] a = [("j_mune_l", 0.60f), ("j_mune_r", 0.40f)], b = [("j_mune_l", 0.604f), ("j_mune_r", 0.396f)];
        var left = FlatPair("_top", Flat(-0.03f, -0.001f, a), Flat(-0.03f, -0.001f, [("j_mune_l", 0.3f), ("j_mune_r", 0.7f)]));
        var right = FlatPair("_glv", Flat(0.001f, 0.03f, b), Flat(0.001f, 0.03f, [("j_mune_l", 0.7f), ("j_mune_r", 0.3f)]));
        var garment = SeamBetween(a, b);
        float[]? clamped = null;
        BodyRetarget.PushTrace.Value = (k, v) => { if (k == "seam-clamped") clamped = v; };
        BodyRetarget.WeightPlan plan;
        try { plan = BodyRetarget.PlanWeights(garment, [left, right], acrossBodies: true, before: garment)!; }
        finally { BodyRetarget.PushTrace.Value = null; }
        var written = SyntheticCrotch.Weights(Rebuild(garment, [left, right], plan));
        foreach (var (v, authored) in new[] { (At(0), a), (At(1), b) })
        {
            float right0 = written[v].Where(i => i.Bone == "j_mune_r").Sum(i => i.W);
            Assert.True(right0 <= authored.Single(i => i.Item1 == "j_mune_r").Item2 + 1f / 255f,
                        $"vertex {v} carries {right0:F3} to the right");
        }
        Assert.NotNull(clamped);
        Assert.Equal(1f, clamped![At(0)]);
    }

    [Fact]
    [Trait("Plan", "AC-seam-weights-5")]
    [Trait("Plan", "EDGE-seam-weights-11")]
    public void Through_the_plan_a_seam_never_carries_more_leg_than_its_author_gave()
    {
        // Rigged 9.8% and 10.2% to the thigh (a byte apart): one place, but only the second counts as rigged to the legs
        // (LegShare), so only the first is held off them. The body on the right gives its copy most of the thigh.
        (string, float)[] a = [("j_kosi", 0.902f), ("j_asi_a_l", 0.098f)], b = [("j_kosi", 0.898f), ("j_asi_a_l", 0.102f)];
        var left = FlatPair("_top", Flat(-0.03f, -0.001f, a), Flat(-0.03f, -0.001f, Kosi));
        var right = FlatPair("_glv", Flat(0.001f, 0.03f, b), Flat(0.001f, 0.03f, [("j_kosi", 0.4f), ("j_asi_a_l", 0.6f)]));
        var garment = SeamBetween(a, b);
        var plan = BodyRetarget.PlanWeights(garment, [left, right], acrossBodies: true, before: garment)!;
        var without = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoSeamWeights: true),
            () => BodyRetarget.PlanWeights(garment, [left, right], acrossBodies: true, before: garment))!;
        float Legs(BodyRetarget.WeightPlan p, int v) => SyntheticCrotch.Legs(Of(p, garment, v));
        Assert.True(Legs(without, At(1)) > 0.2f, $"the right copy read only {Legs(without, At(1)):F3} of the thigh");
        var written = SyntheticCrotch.Weights(Rebuild(garment, [left, right], plan));
        float legs = SyntheticCrotch.Legs(written[At(0)]);
        Assert.True(legs <= 0.098f + 1f / 255f, $"the copy held off the legs carries {legs:F3} of the thigh");
    }

    private static byte[] Rebuild(byte[] garment, BodyRetarget.SlotPair[] pairs, BodyRetarget.WeightPlan plan)
    {
        var rebuilt = BodyRetarget.Rebuild(garment, pairs, swapSkin: false, plan, out _);
        Assert.NotNull(rebuilt);
        return rebuilt!;
    }

    /// <summary>
    /// [EDGE-seam-weights-15] A refit pinned as it was before the seam weights, against the same refit with them: every
    /// position and normal the same, so the rule moved weights alone.
    /// </summary>
    internal static void SameGeometry(byte[] pinned, byte[] now)
    {
        var a = ModelPartReader.Read(pinned)!;
        var b = ModelPartReader.Read(now)!;
        Assert.Equal(a.Positions, b.Positions);
        Assert.Equal(a.Normals, b.Normals);
    }

    /// <summary>The widest class: the largest distance between two members of one, as authored.</summary>
    private static float Widest(byte[] mdl, BodyRetarget.SeamOutcome outcome)
    {
        var at = SyntheticCrotch.Positions(mdl);
        float widest = 0f;
        foreach (var members in Enumerable.Range(0, outcome.ClassOf.Length).Where(v => outcome.ClassOf[v] >= 0)
                                          .GroupBy(v => outcome.ClassOf[v]))
            foreach (int a in members)
                foreach (int b in members)
                    widest = MathF.Max(widest, Vector3.Distance(at[a], at[b]));
        return widest;
    }

    // ── a rig-kept slot beside one whose rig changes ─────────────────────────────────────────────────────────

    /// <summary>A flat body patch at z = 0 from <paramref name="x0"/> to <paramref name="x1"/>, 1 m up, on one weighting.</summary>
    private static byte[] Flat(float x0, float x1, (string Bone, float W)[] weights)
    {
        var pos = new List<Vector3>();
        var tris = new List<int>();
        const int n = 5;
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++) pos.Add(new Vector3(x0 + (x1 - x0) * c / (n - 1), 0.98f + 0.04f * r / (n - 1), 0f));
        for (int r = 0; r + 1 < n; r++)
            for (int c = 0; c + 1 < n; c++)
            {
                int a = r * n + c;
                tris.AddRange([a, a + 1, a + n + 1, a, a + n + 1, a + n]);
            }
        return SyntheticModel.BuildSurface([], Surface(SyntheticCrotch.Skin, [.. pos], [.. pos.Select(_ => weights)],
                                                       [(0u, [.. tris])]));
    }

    private static BodyRetarget.SlotPair FlatPair(string slot, byte[] source, byte[] target)
    {
        var t = ModelPartReader.Read(target)!;
        Assert.True(IdentityCorrespondence.TryBuild(t, t, "chest", out var built, out string why), why);
        return new BodyRetarget.SlotPair(slot, built!, t, target, source);
    }

    [Fact]
    [Trait("Plan", "AC-seam-weights-6")]
    [Trait("Plan", "EDGE-seam-weights-18")]
    public void A_seam_at_a_rig_kept_slot_boundary_keeps_the_authors_weights()
    {
        // Two slots within one body mod: on the left two sizes of one rig (kept), on the right a rig that changes. The
        // seam lies between them, its left copy nearer the left body, its right copy nearer the right.
        var kept = FlatPair("_top", Flat(-0.03f, -0.001f, Kosi), Flat(-0.03f, -0.001f, Kosi));
        var changed = FlatPair("_glv", Flat(0.001f, 0.03f, Kosi), Flat(0.001f, 0.03f, [("j_sebo_a", 1f)]));
        var p = new Vector3(-2e-4f, 1f, 0.005f);
        var garment = SyntheticModel.BuildSurface([], Points(Cloth, [p, p + new Vector3(4e-4f, 0f, 0f)], [Kosi, Kosi]));
        BodyRetarget.WeightPlan Run(bool seams) => BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoSeamWeights: !seams),
            () => BodyRetarget.PlanWeights(garment, [kept, changed], acrossBodies: false, before: garment))!;

        var without = Run(false);
        Assert.Null(without.For(0)?[At(0)]);        // the rig-kept side: the author's
        Assert.NotNull(without.For(0)?[At(1)]);     // the other: rewritten
        var with = Run(true);
        Assert.Null(with.For(0)?[At(0)]);
        Assert.Null(with.For(0)?[At(1)]);
    }
}
