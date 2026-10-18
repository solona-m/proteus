using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using static Proteus.Tests.SkinTuckSheet;

namespace Proteus.Tests;

/// <summary>
/// The new body's skin carried on past the author's edge in under the cloth (<see cref="BodyRetarget.TuckUnderCloth"/>),
/// on <see cref="SkinTuckSheet"/>: the top's own skin from row <see cref="SkinTuckSheet.Edge"/> up, the cut keeping the
/// body there and one row of triangles past it, the cloth over the rows below at the height each case sets.
/// </summary>
public class SkinTuckTests(ITestOutputHelper output)
{
    private (byte[] Model, BodyRetarget.SwapReport Report) Swap(byte[] top, byte[] body, bool tuck = true,
                                                               string slot = "_top", IReadOnlySet<string>? hidden = null,
                                                               bool carryPiercings = false)
    {
        var model = BodyRetarget.SwapSkin(top, [Resized(body, slot, hidden)], out var report,
                                          tuckSlot: tuck ? "_top" : null, carryPiercings: carryPiercings)!;
        Assert.NotNull(model);
        output.WriteLine($"{(tuck ? "tuck" : "no tuck")}: {report}; rows {string.Join(", ", DrawnRows(model).Select(r => $"{r.Key}:{r.Value}"))}");
        return (model, report);
    }

    /// <summary>Two triangles per column gap: a whole row band of the sheet.</summary>
    private const int Band = (Columns - 1) * 2;

    [Fact]
    [Trait("Plan", "AC-neckline-skin-tuck-4")]
    public void Skin_tucks_under_loose_cloth_below_the_authors_edge()
    {
        // Cloth 15 mm off over rows 7 and up, 1 mm off below. The cut keeps the band under the author's edge (row 9 to
        // 10); the tuck carries the skin down through the loose rows and stops where the cloth comes down onto it.
        var top = LooseTop(looseFrom: 7);
        var (untucked, before) = Swap(top, Body(), tuck: false);
        var (model, report) = Swap(top, Body());

        var rows = DrawnRows(model);
        Assert.Equal(Band, rows.GetValueOrDefault(8));
        Assert.Equal(Band, rows.GetValueOrDefault(7));
        Assert.DoesNotContain(rows.Keys, r => r < 7);
        Assert.DoesNotContain(DrawnRows(untucked).Keys, r => r < 9);
        Assert.Equal(2 * Band, report.Tucked);
        Assert.Equal(before.Added + report.Tucked, report.Added);
        Assert.Equal(before.Cut - report.Tucked, report.Cut);

        // Every corner of every triangle the tuck drew lies 3 mm or more under the cloth, by a ray of this test's own.
        var cloth = ClothOf(top);
        foreach (var corner in NewCorners(untucked, model))
        {
            float over = cloth.Select(t => Up(corner, t)).Where(d => d != null).Select(d => d!.Value).DefaultIfEmpty(float.MaxValue).Min();
            Assert.True(over >= BodyRetarget.TuckClear - 1e-5f, $"corner {corner} has cloth {over * 1000:0.0} mm over it");
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-1")]
    public void Nothing_tucks_where_the_cloth_crowds_or_is_buried()
    {
        Assert.Equal(0, Swap(Top(_ => 0.001f), Body()).Report.Tucked);
        Assert.Equal(0, Swap(Top(r => r >= Edge ? 0.015f : -0.003f), Body()).Report.Tucked);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-15")]
    public void Nothing_tucks_under_a_rim_close_to_the_skin()
    {
        // The cloth ends at row 7, its open edge 1 mm off the skin: the rim's faces, which the pull lets skin run under
        // at a quarter millimetre. The tuck does not; 5 mm off, it does.
        var tight = Top(r => r == 7 ? 0.001f : 0.015f, clothFrom: 7);
        var cloth = ModelPartReader.Read(tight)!;
        var faces = cloth.Parts.Where(p => p.Island < 0 && p.Material == Cloth)
                         .SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                                    .Select(t => (p.Triangles[t * 3], p.Triangles[t * 3 + 1], p.Triangles[t * 3 + 2])))
                         .ToList();
        var rims = BodyRetarget.RimFaces(faces, v => new Vector3(cloth.Positions[v * 3], cloth.Positions[v * 3 + 1], cloth.Positions[v * 3 + 2]));
        Assert.Contains(rims, f => new[] { faces[f].Item1, faces[f].Item2, faces[f].Item3 }
                                       .Any(v => MathF.Abs(cloth.Positions[v * 3 + 1] - Y(7)) < 1e-4f));

        var underTight = DrawnRows(Swap(tight, Body()).Model);
        Assert.DoesNotContain(underTight.Keys, r => r < 8);

        var underLoose = DrawnRows(Swap(Top(r => r == 7 ? 0.005f : 0.015f, clothFrom: 7), Body()).Model);
        Assert.Equal(Band, underLoose.GetValueOrDefault(7));
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-16")]
    public void Nothing_tucks_over_deeply_buried_cloth()
    {
        // A second layer 15 mm inside the body under the loose rows — past the clear-body push and the pull's 10 mm.
        var top = Top(_ => 0.015f, extra: (3, 9, -0.015f));
        Assert.Equal(0, Swap(top, Body()).Report.Tucked);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-2")]
    public void Nothing_tucks_where_no_cloth_of_the_garment_covers_the_body()
        => Assert.Equal(0, Swap(Top(_ => 0.015f, clothFrom: Edge), Body()).Report.Tucked);

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-3")]
    public void Tucking_stops_at_the_band()
    {
        // Loose cloth over the whole sheet: the skin goes as far as the band from the author's edge, no further.
        var (model, report) = Swap(Top(_ => 0.015f, clothFrom: 0), Body());
        Assert.True(report.Tucked > 0);
        var read = ModelPartReader.Read(model)!;
        float lowest = read.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                           .SelectMany(p => p.Triangles).Min(v => read.Positions[v * 3 + 1]);
        Assert.True(lowest >= Y(Edge) - BodyRetarget.TuckBand - 1e-4f, $"skin drawn down to y {lowest}");
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-4")]
    public void Unconnected_skin_under_cloth_is_not_tucked()
    {
        // Row 6 has the cloth down on it; the loose rows below it are not reached.
        var rows = DrawnRows(Swap(Top(r => r == 6 ? 0.001f : 0.015f, clothFrom: 0), Body()).Model);
        Assert.Equal(Band, rows.GetValueOrDefault(7));
        Assert.DoesNotContain(rows.Keys, r => r < 7);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-5")]
    public void Pulled_corners_are_left_to_the_pull()
    {
        // The left half's cloth lies 1 mm on the skin over the author's edge, so the pull draws the corners past it back;
        // the right half's stands 15 mm off. The body is split into two meshes along row 9 — the kept corners — so each
        // of those corners has a twin at the same position in the other mesh.
        var top = Top((r, c) => c <= 3 ? 0.001f : 0.015f, clothFrom: 5);
        var body = Body(splitAt: 9);
        var (untucked, _) = Swap(top, body, tuck: false);
        var (model, report) = Swap(top, body);
        Assert.True(report.Tucked > 0);

        var bodyAt = KeysOf(ModelPartReader.Read(body)!, all: true);
        var before = KeysOf(ModelPartReader.Read(untucked)!);
        var after = KeysOf(ModelPartReader.Read(model)!);
        var moved = before.Where(k => !bodyAt.Contains(k)).ToHashSet();
        Assert.NotEmpty(moved);                       // the pull ran
        Assert.Superset(before, after);               // every corner drawn untucked is where it was
        foreach (var tri in NewTriangles(untucked, model))
            Assert.DoesNotContain(tri, moved.Contains);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-6")]
    public void Variant_cloth_does_not_count_as_cover()
        => Assert.Equal(0, Swap(Top(r => r >= 7 ? 0.015f : 0.001f, clothAttr: "atr_tv_a"), Body()).Report.Tucked);

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-7")]
    public void Hidden_body_variants_are_never_tucked()
    {
        var rows = DrawnRows(Swap(LooseTop(looseFrom: 4), Body(hiddenBelow: 8), hidden: new HashSet<string> { "atr_tv_b" }).Model);
        Assert.DoesNotContain(rows.Keys, r => r < 8);
        Assert.Equal(Band, rows.GetValueOrDefault(8));
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-8")]
    public void No_tuck_when_nothing_was_cut()
    {
        var top = Top(_ => 0.015f, skinFrom: 0);
        var with = BodyRetarget.SwapSkin(top, [Resized(Body())], out var report, tuckSlot: "_top");
        var without = BodyRetarget.SwapSkin(top, [Resized(Body())], out _);
        Assert.Equal(0, report.Tucked);
        Assert.Equal(without, with);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-9")]
    public void Tucked_skin_brings_no_piercing_the_author_did_not_show()
    {
        // A ring on row 7, which the tuck draws: whether it comes in is the author's cut's to say. With the piercings
        // carried, or the two would agree on none for nothing.
        var (_, tucked) = Swap(LooseTop(looseFrom: 5), Body(piercing: true), carryPiercings: true);
        var (_, untucked) = Swap(LooseTop(looseFrom: 5), Body(piercing: true), tuck: false, carryPiercings: true);
        Assert.True(tucked.Tucked > 0);
        Assert.Equal(untucked.Adorned, tucked.Adorned);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-10")]
    public void Tucked_vertices_land_in_their_own_mesh()
    {
        // Rows 0-8 are a second mesh, every vertex of it cut — the tuck draws into it all the same.
        var (model, report) = Swap(LooseTop(looseFrom: 5), Body(splitAt: 8));
        var rows = DrawnRows(model);
        Assert.Equal(Band, rows.GetValueOrDefault(7));
        Assert.Equal(Band, rows.GetValueOrDefault(6));
        Assert.True(report.Tucked >= 3 * Band);
        Assert.NotNull(ModelPartReader.Read(model));
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-11")]
    public void A_face_sagging_into_the_cloth_is_not_tucked()
    {
        // The cloth is twice as fine as the skin: 4 mm off at every point over a skin vertex, 1 mm off between them. Each
        // corner has room; the middle of each skin face has the cloth down on it.
        var meshes = new List<SyntheticModel.SurfaceMesh>();
        var pos = new List<Vector3>();
        var tris = new List<int>();
        int cols = Columns * 2 - 1, rows = Rows * 2 - 1;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                pos.Add(new Vector3(X(0) + c * Step / 2, Y(0) + r * Step / 2, r % 2 == 0 && c % 2 == 0 ? 0.004f : 0.001f));
        for (int r = 0; r + 1 < rows; r++)
            for (int c = 0; c + 1 < cols; c++)
            {
                int a = r * cols + c, b = a + 1, d = a + cols, e = d + 1;
                tris.AddRange([a, b, e, a, e, d]);
            }
        var skin = new List<Vector3>();
        var skinTris = new List<int>();
        for (int r = Edge; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
                skin.Add(new Vector3(X(c), Y(r), 0f));
        for (int r = 0; r + 1 < Rows - Edge; r++)
            for (int c = 0; c + 1 < Columns; c++)
            {
                int a = r * Columns + c, b = a + 1, d = a + Columns, e = d + 1;
                skinTris.AddRange([a, b, e, a, e, d]);
            }
        meshes.Add(new SyntheticModel.SurfaceMesh(Skin, [.. skin], [.. skin.Select(_ => Vector3.UnitZ)],
                                                  [.. skin.Select(_ => Vector2.Zero)],
                                                  [.. skin.Select(_ => ((string, float)[]?)[("j_sebo_c", 1f)])],
                                                  [(0u, [.. skinTris])]));
        meshes.Add(new SyntheticModel.SurfaceMesh(Cloth, [.. pos], [.. pos.Select(_ => Vector3.UnitZ)],
                                                  [.. pos.Select(_ => Vector2.Zero)],
                                                  [.. pos.Select(_ => ((string, float)[]?)[("j_sebo_c", 1f)])],
                                                  [(0u, [.. tris])]));
        Assert.Equal(0, Swap(SyntheticModel.BuildSurface([], [.. meshes]), Body()).Report.Tucked);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-12")]
    public void Tucking_moves_nothing()
    {
        var bytes = LooseTop(looseFrom: 5);
        var garment = ModelPartReader.Read(bytes)!;
        var tucked = RefitCore.PlanRefit(garment, bytes, [Resized(Body())], "_top", across: true, gameGear: true, out _);
        var untucked = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoTuck: true, NoDeepClear: true),
            () => RefitCore.PlanRefit(garment, bytes, [Resized(Body())], "_top", across: true, gameGear: true, out _));
        var log = new List<string>();
        RefitCore.PlanRefit(garment, bytes, [Resized(Body())], "_top", across: true, gameGear: true, out _, log: log.Add);
        Assert.DoesNotContain(log, l => l.StartsWith("skin tuck: ", StringComparison.Ordinal));

        Assert.True(tucked.Report.Swap?.Tucked > 0, $"{tucked.Report.Swap}");
        var body = ModelPartReader.Read(Body())!;
        var model = ModelPartReader.Read(tucked.Model)!;
        Assert.Subset(KeysOf(body, all: true), KeysOf(model));
        // On the written model: every triangle the tuck drew is one of the body's own, wound as the body winds it.
        var (fresh, wrong) = NewSkinAgainstBody(ModelPartReader.Read(untucked.Model)!, model, body);
        Assert.Equal(tucked.Report.Swap!.Value.Tucked, fresh);
        Assert.Equal(0, wrong);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-13")]
    public void Tucking_is_deterministic()
    {
        var top = LooseTop(looseFrom: 5);
        Assert.Equal(Swap(top, Body()).Model, Swap(top, Body()).Model);
        Assert.Equal(DrawnRows(Swap(top, Body(halvesAt: 8)).Model), DrawnRows(Swap(top, Body(halvesAt: 8, reversed: true)).Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-17")]
    public void A_game_legs_piece_and_a_long_tops_legs_are_not_tucked()
    {
        var bytes = LooseTop(looseFrom: 5);
        var garment = ModelPartReader.Read(bytes)!;
        var legs = RefitCore.PlanRefit(garment, bytes, [Resized(Body(), "_dwn")], "_dwn", across: true, gameGear: true, out _);
        Assert.True(legs.Report.Swap is { Tucked: 0 }, $"{legs.Report.Swap}");

        // A top's own pair for the legs is not its chest.
        Assert.Equal(0, Swap(bytes, Body(), slot: "_dwn").Report.Tucked);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-18")]
    [Trait("Plan", "AC-neckline-skin-tuck-4")]
    public void Arm_skin_under_a_sleeve_is_not_tucked()
    {
        // Rows 0-7 rigged to the upper arm: the tuck reaches row 8 and stops, where the torso version goes on.
        var arm = DrawnRows(Swap(LooseTop(looseFrom: 4), Body(arm: r => r <= 7)).Model);
        var torso = DrawnRows(Swap(LooseTop(looseFrom: 4), Body()).Model);
        Assert.Equal(Band, arm.GetValueOrDefault(8));
        Assert.DoesNotContain(arm.Keys, r => r < 8);
        Assert.Equal(Band, torso.GetValueOrDefault(6));
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-skin-tuck-19")]
    public void No_tuck_when_the_bodys_weights_cannot_be_read()
    {
        // The loose sheet's own cut — covered vertices and kept triangles as the cut makes them — grows with the body's
        // file, and grows nothing, saying why, with a file whose weights cannot be read.
        var top = ModelPartReader.Read(LooseTop(looseFrom: 5))!;
        byte[] bodyBytes = Body();
        var body = ModelPartReader.Read(bodyBytes)!;
        var drawn = new BodySurface(top, BodySurface.CellFor(BodyRetarget.MeanEdgeOf(top)));
        var edges = BodyRetarget.SkinEdges.Of(top);
        var cloth = new BodyRetarget.ClothCrossings(top);
        var covered = Enumerable.Range(0, body.Positions.Length / 3)
                                .Select(v => BodyRetarget.Covers(drawn, new Vector3(body.Positions[v * 3], body.Positions[v * 3 + 1],
                                                                                    body.Positions[v * 3 + 2]), edges))
                                .ToArray();
        var kept = body.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                       .SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                                  .Select(t => (p.Triangles[t * 3], p.Triangles[t * 3 + 1], p.Triangles[t * 3 + 2])))
                       .Where(t => covered[t.Item1] || covered[t.Item2] || covered[t.Item3]).ToList();
        var baseOf = body.MeshSpans.ToDictionary(s => s.Mesh, s => s.BaseVertex);

        var readable = BodyRetarget.TuckUnderCloth(drawn, cloth, bodyBytes, body, covered, kept, new HashSet<Vector3>(), baseOf,
                                                   edges);
        Assert.True(readable.Triangles > 0, "the fixture does not grow with readable weights");
        Assert.False(readable.Unread);

        var unread = BodyRetarget.TuckUnderCloth(drawn, cloth, [], body, covered, kept, new HashSet<Vector3>(), baseOf, edges);
        Assert.Empty(unread.Grown);
        Assert.Equal(0, unread.Triangles);
        Assert.True(unread.Unread);
    }

    [Theory]
    [Trait("Plan", "EDGE-neckline-skin-tuck-18")]
    [InlineData("j_ude_a_l", true)]
    [InlineData("j_ude_b_r", true)]
    [InlineData("j_kata_l", true)]
    [InlineData("n_hkata_r", true)]
    [InlineData("n_hhiji_l", true)]
    [InlineData("n_hte_r", true)]
    [InlineData("j_te_l", true)]
    [InlineData("j_oya_a_r", true)]
    [InlineData("j_sebo_c", false)]
    [InlineData("j_mune_l", false)]
    [InlineData("j_sako_r", false)]
    [InlineData("j_kubi", false)]
    public void The_arm_bones_are_the_arm_and_hand(string bone, bool arm)
        => Assert.Equal(arm, BodyRetarget.IsArmBone(bone));

    [Theory]
    [Trait("Plan", "EDGE-neckline-skin-tuck-18")]
    [InlineData(0.20f, true)]    // a fifth on the arm is torso still
    [InlineData(0.24f, true)]
    [InlineData(0.26f, false)]   // past the quarter, the arm's
    [InlineData(0.50f, false)]
    public void A_vertex_is_the_torso_below_a_quarter_of_its_weight_on_the_arm(float share, bool tucked)
    {
        // Rows 0-8 carry that share on the shoulder, the rest on the spine.
        var rows = DrawnRows(Swap(LooseTop(looseFrom: 4), Body(arm: r => r <= 8, armShare: share, armBone: "j_kata_l")).Model);
        Assert.Equal(tucked, rows.Keys.Any(r => r < 8));
    }

    // ── helpers ──

    private static List<(Vector3 A, Vector3 B, Vector3 C)> ClothOf(byte[] top)
    {
        var read = ModelPartReader.Read(top)!;
        Vector3 At(int v) => new(read.Positions[v * 3], read.Positions[v * 3 + 1], read.Positions[v * 3 + 2]);
        return read.Parts.Where(p => p.Island < 0 && p.Material == Cloth)
                   .SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                              .Select(t => (At(p.Triangles[t * 3]), At(p.Triangles[t * 3 + 1]), At(p.Triangles[t * 3 + 2]))))
                   .ToList();
    }

    /// <summary>How far straight up (+Z) from <paramref name="p"/> the triangle lies, or null if the line misses it.</summary>
    private static float? Up(Vector3 p, (Vector3 A, Vector3 B, Vector3 C) t)
    {
        var e1 = t.B - t.A; var e2 = t.C - t.A;
        var h = Vector3.Cross(Vector3.UnitZ, e2);
        float det = Vector3.Dot(e1, h);
        if (MathF.Abs(det) < 1e-12f) return null;
        var s = p - t.A;
        float u = Vector3.Dot(s, h) / det;
        if (u < -1e-5f || u > 1f + 1e-5f) return null;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(Vector3.UnitZ, q) / det;
        if (v < -1e-5f || u + v > 1f + 1e-5f) return null;
        float d = Vector3.Dot(e2, q) / det;
        return d >= 0f ? d : null;
    }

    private static (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 1e5f), (int)MathF.Round(p.Y * 1e5f), (int)MathF.Round(p.Z * 1e5f));

    /// <summary>The positions of a model's drawn body skin vertices — or of all its vertices.</summary>
    private static HashSet<(int, int, int)> KeysOf(ModelParts m, bool all = false)
    {
        var verts = all ? Enumerable.Range(0, m.Positions.Length / 3)
                        : m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).SelectMany(p => p.Triangles).Distinct();
        return [.. verts.Select(v => Key(new Vector3(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2])))];
    }

    private static List<(Vector3, Vector3, Vector3)> SkinTris(byte[] model)
    {
        var read = ModelPartReader.Read(model)!;
        Vector3 At(int v) => new(read.Positions[v * 3], read.Positions[v * 3 + 1], read.Positions[v * 3 + 2]);
        return read.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                   .SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                              .Select(t => (At(p.Triangles[t * 3]), At(p.Triangles[t * 3 + 1]), At(p.Triangles[t * 3 + 2]))))
                   .ToList();
    }

    /// <summary>The skin triangles <paramref name="after"/> draws that <paramref name="before"/> does not, by their
    /// corners' positions.</summary>
    private static List<(int, int, int)[]> NewTriangles(byte[] before, byte[] after)
    {
        var had = SkinTris(before).Select(t => string.Join(";", new[] { Key(t.Item1), Key(t.Item2), Key(t.Item3) }.Order())).ToHashSet();
        return SkinTris(after).Select(t => new[] { Key(t.Item1), Key(t.Item2), Key(t.Item3) })
                              .Where(k => !had.Contains(string.Join(";", k.Order()))).ToList();
    }

    private static IEnumerable<Vector3> NewCorners(byte[] before, byte[] after)
    {
        var had = SkinTris(before).Select(t => string.Join(";", new[] { Key(t.Item1), Key(t.Item2), Key(t.Item3) }.Order())).ToHashSet();
        return SkinTris(after).Where(t => !had.Contains(string.Join(";", new[] { Key(t.Item1), Key(t.Item2), Key(t.Item3) }.Order())))
                              .SelectMany(t => new[] { t.Item1, t.Item2, t.Item3 }).Distinct();
    }
}
