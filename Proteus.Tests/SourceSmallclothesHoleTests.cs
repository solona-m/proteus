using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The old body read as worn where its skin has a hole under its own underwear — the game's chest, whose bust is its bra
/// and nothing else. Cloth over the bra followed the hole's rim and kept the game body's breasts on a flat chest
/// (No.2 Type B Dress onto Muse+). Here at body scale with sheets: an old body with its skin cut from a band and a "bra"
/// plate standing in front of the cut, onto new bodies flat, fuller, or with underwear of their own.
/// </summary>
public class SourceSmallclothesHoleTests
{
    private const string Skin = SkinlessTargetTests.Skin;
    private const string Bra = "/mt_c0201e0000_top_a.mtrl";
    private const string Cloth = "/mt_c0201e6000_top_a.mtrl";

    /// <summary>Where the old body's skin is cut (y), and how far in front of it its bra stands.</summary>
    private static readonly (float Y0, float Y1) Hole = (0.4f, 0.6f);
    private const float BraOut = 0.025f;

    /// <summary>The garment's cloth, this far over the bra as authored.</summary>
    private const float Gap = 0.01f;

    private const int SkinNx = 9, SkinNy = 50;

    /// <summary>A grid of triangles, x and y across the given ranges, z as given; facing +z.</summary>
    private static (List<float> Pos, List<int> Tris) Grid(float x0, float x1, float y0, float y1, int nx, int ny,
                                                         Func<float, float, float> z, int first = 0,
                                                         Func<float, bool>? keep = null)
    {
        var pos = new List<float>();
        var tris = new List<int>();
        for (int j = 0; j <= ny; j++)
            for (int i = 0; i <= nx; i++)
            {
                float x = x0 + (x1 - x0) * i / nx, y = y0 + (y1 - y0) * j / ny;
                pos.AddRange([x, y, z(x, y)]);
            }
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int a = first + j * (nx + 1) + i, b = a + 1, d = a + nx + 1, e = d + 1;
                float cy = y0 + (y1 - y0) * (j + 0.5f) / ny;
                if (keep != null && !keep(cy)) continue;
                tris.AddRange([a, b, e, a, e, d]);
            }
        return (pos, tris);
    }

    private static (List<float> Pos, List<int> Tris) SkinSheet(Func<float, float, float> z, Func<float, bool>? keep = null)
        => Grid(0.02f, 0.2f, 0f, 1f, SkinNx, SkinNy, z, 0, keep);

    private static bool OutsideHole(float y) => y < Hole.Y0 || y > Hole.Y1;

    /// <summary>The old body: skin cut from <see cref="Hole"/>, a bra plate <paramref name="braOut"/> in front of it over
    /// x <paramref name="braX"/>, reaching 20 mm past the cut either way.</summary>
    private static ModelParts OldBody(bool cut = true, bool bra = true, float braOut = BraOut,
                                      (float X0, float X1)? braX = null, (float Y0, float Y1)? hole = null)
    {
        var (y0, y1) = hole ?? Hole;
        var (pos, tris) = SkinSheet((_, _) => 0f, cut ? y => y < y0 || y > y1 : null);
        var parts = new List<(string, int[], uint)> { (Skin, tris.ToArray(), 0u) };
        if (bra)
        {
            var (bx0, bx1) = braX ?? (0.02f, 0.2f);
            var plate = Grid(bx0, bx1, y0 - 0.02f, y1 + 0.02f, 9, 12, (_, _) => braOut, pos.Count / 3);
            pos.AddRange(plate.Pos);
            parts.Add((Bra, plate.Tris.ToArray(), 0u));
        }
        return SkinlessTargetTests.Model(pos.ToArray(), parts, []);
    }

    /// <summary>A new body: the skin sheet, its front at <paramref name="z"/>; no hole, no underwear.</summary>
    private static ModelParts NewBody(Func<float, float, float> z)
    {
        var (pos, tris) = SkinSheet(z);
        var model = SkinlessTargetTests.Model(pos.ToArray(), [(Skin, tris.ToArray(), 0u)], []);
        // The surface's own normals, as a body's are: across a breast they face up, out and down, not all straight out.
        const float h = 1e-3f;
        for (int v = 0; v < pos.Count / 3; v++)
        {
            float x = pos[v * 3], y = pos[v * 3 + 1];
            var n = Vector3.Normalize(new Vector3(-(z(x + h, y) - z(x - h, y)) / (2 * h), -(z(x, y + h) - z(x, y - h)) / (2 * h), 1f));
            model.Normals[v * 3] = n.X;
            model.Normals[v * 3 + 1] = n.Y;
            model.Normals[v * 3 + 2] = n.Z;
        }
        return model;
    }

    /// <summary>A breast 45 mm out at the middle of the hole, smoothly to nothing 50 mm outside it.</summary>
    private static float Fuller(float x, float y)
    {
        float t = Math.Clamp((y - (Hole.Y0 - 0.05f)) / (Hole.Y1 - Hole.Y0 + 0.1f), 0f, 1f);
        return 0.045f * MathF.Sin(MathF.PI * t) * MathF.Sin(MathF.PI * t);
    }

    /// <summary>A correspondence that knows the old body's skin vertex for vertex — the new body's skin sheet is numbered
    /// as the old one's — and nothing of its underwear, as one by texture coordinate knows nothing of a bra.</summary>
    private sealed class SkinField(ModelParts source, ModelParts target) : IBodyCorrespondence
    {
        public ModelParts Source { get; } = source;

        public IReadOnlyList<Vector3?> Field { get; } = Build(source, target);

        public string Describe() => "skin vertex for vertex";

        private static Vector3?[] Build(ModelParts source, ModelParts target)
        {
            int skin = (SkinNx + 1) * (SkinNy + 1);
            var field = new Vector3?[source.Positions.Length / 3];
            for (int v = 0; v < skin; v++) field[v] = At(target, v) - At(source, v);
            return field;
        }
    }

    private static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>The garment: cloth <see cref="Gap"/> over the bra across the hole's middle.</summary>
    private static ModelParts Garment(float over = BraOut + Gap, bool skinPatch = false)
    {
        var (pos, tris) = Grid(0.05f, 0.17f, Hole.Y0 + 0.02f, Hole.Y1 - 0.02f, 12, 16, (_, _) => over);
        var parts = new List<(string, int[], uint)> { (Cloth, tris.ToArray(), 0u) };
        if (skinPatch)
        {
            var patch = Grid(0.06f, 0.16f, Hole.Y0 + 0.03f, Hole.Y1 - 0.03f, 6, 6, (_, _) => BraOut, pos.Count / 3);
            pos.AddRange(patch.Pos);
            parts.Add((Skin, patch.Tris.ToArray(), 0u));
        }
        return SkinlessTargetTests.Model(pos.ToArray(), parts, []);
    }

    /// <param name="game">Whether the old body is the game's chest — the only body read this way.</param>
    private static BodyRetarget.Solved Solve(ModelParts garment, ModelParts source, ModelParts target, bool old = false,
                                             bool replaceSkin = false, IReadOnlySet<int>? held = null, bool game = true)
        => BodyRetarget.WithTuning(new BodyRetarget.Tuning { NoSourceWorn = old },
                                   () => BodyRetarget.Solve(garment, [new BodyRetarget.SlotPair("_top",
                                                                         new SkinField(source, target), target,
                                                                         SourceIsGameChest: game)],
                                                            "_top", replaceSkin: replaceSkin, held: held));

    /// <summary>Where each of the garment's vertices of <paramref name="material"/> ended up.</summary>
    private static List<Vector3> Final(ModelParts garment, BodyRetarget.Solved solved, string material = Cloth)
    {
        var verts = garment.Parts.Where(p => p.Material == material).SelectMany(p => p.Triangles).Distinct();
        return verts.Select(v =>
        {
            var d = solved.Edit.DeltaAt(v);
            return At(garment, v) + new Vector3(d.X, d.Y, d.Z);
        }).ToList();
    }

    /// <summary>The cloth over the hole's middle third, where nothing but the bra is near.</summary>
    private static List<Vector3> Middle(List<Vector3> points)
        => points.Where(p => MathF.Abs(p.Y - 0.5f) < 0.04f && p.X > 0.08f && p.X < 0.14f).ToList();

    private static void SameMoves(BodyRetarget.Solved a, BodyRetarget.Solved b, int count)
    {
        for (int v = 0; v < count; v++)
        {
            var da = a.Edit.DeltaAt(v);
            var db = b.Edit.DeltaAt(v);
            Assert.True(da.X == db.X && da.Y == db.Y && da.Z == db.Z, $"vertex {v} moved differently");
        }
    }

    [Fact]
    public void Cloth_over_a_bra_with_no_skin_under_it_follows_a_flatter_body_in()
    {
        // [AC-bra-hole-transfer-1] The bra stands 25 mm out, the new body is flat: the cloth comes in to its own gap.
        var source = OldBody();
        var target = NewBody((_, _) => 0f);
        var garment = Garment();

        var fixedUp = Middle(Final(garment, Solve(garment, source, target)));
        var before = Middle(Final(garment, Solve(garment, source, target, old: true)));

        Assert.NotEmpty(fixedUp);
        foreach (var p in fixedUp) Assert.InRange(p.Z, Gap - 0.001f, Gap + 0.001f);
        // The old reading: the hole's rim did not move, so neither did the cloth over the bust.
        foreach (var p in before) Assert.True(p.Z >= 0.035f - 1e-4f, $"old reading at {p.Z * 1000:0.0} mm");
    }

    [Fact]
    public void Cloth_over_a_bra_onto_a_fuller_body_stays_about_where_it_was()
    {
        // [AC-bra-hole-transfer-2] The new body's breast stands 45 mm out where the bra stood 25: the bra only ever brings
        // cloth in, and what does not land is left out of the old body, so over the dome the cloth reads the hole's rim as
        // it always has. (Toward the rim, where this dome is lower than the flat bra, it does come in.)
        var source = OldBody();
        var target = NewBody(Fuller);
        var garment = Garment();

        var now = Solve(garment, source, target);
        var old = Solve(garment, source, target, old: true);
        var middle = Enumerable.Range(0, garment.Positions.Length / 3)
                               .Where(v => Middle([At(garment, v)]).Count == 1).ToList();
        Assert.NotEmpty(middle);
        // Over the dome, where the old reading left it. Not exactly: the flat plate's edges stand proud of the dome's low
        // rim, land there, and are nearer the middle cloth than the rim skin was (1.7 mm at most here). On a real fuller
        // chest — LavaBod+'s Teardrop — the refit comes out as before (SourceSmallclothesHoleLocalTests).
        foreach (int v in middle) Assert.InRange(now.Edit.DeltaAt(v).Z - old.Edit.DeltaAt(v).Z, -0.002f, 0.002f);
    }

    [Fact]
    public void A_body_mod_with_underwear_over_a_hole_reads_as_before()
    {
        // [AC-bra-hole-transfer-3] Bibo+'s SFW bras and Muse+'s binders stand their underwear over a hole too, and were
        // never measured: only the game's own chest is read with its bra.
        var source = OldBody();
        var target = NewBody((_, _) => 0f);
        var garment = Garment();

        var now = Solve(garment, source, target, game: false);
        SameMoves(now, Solve(garment, source, target, old: true, game: false), garment.Positions.Length / 3);
        Assert.Equal(0, now.SourceSmallclothes);
    }

    [Fact]
    public void A_bra_over_intact_skin_changes_nothing()
    {
        // [AC-bra-hole-transfer-3][EDGE-bra-hole-transfer-1] Skin under the bra: the old body reads as it always did.
        var source = OldBody(cut: false, braOut: 0.002f);
        var target = NewBody((_, _) => 0f);
        var garment = Garment(0.012f);

        Assert.Null(BodyRetarget.SmallclothesOverHoles(source, new BodySurface(source, 0.02f)));
        var now = Solve(garment, source, target);
        SameMoves(now, Solve(garment, source, target, old: true), garment.Positions.Length / 3);
        Assert.Equal(0, now.SourceSmallclothes);
    }

    [Fact]
    public void A_body_without_underwear_changes_nothing()
    {
        // [AC-bra-hole-transfer-3][EDGE-bra-hole-transfer-2]
        var source = OldBody(bra: false);
        var target = NewBody((_, _) => 0f);
        var garment = Garment();

        Assert.Null(BodyRetarget.SmallclothesOverHoles(source, new BodySurface(source, 0.02f)));
        SameMoves(Solve(garment, source, target), Solve(garment, source, target, old: true), garment.Positions.Length / 3);
    }

    [Fact]
    public void The_report_counts_the_underwear_points_landed()
    {
        // [AC-bra-hole-transfer-5] Counted when there are any, and said in the log line; nothing said when there are none.
        var target = NewBody((_, _) => 0f);
        var garment = Garment();
        var holed = Solve(garment, OldBody(), target);
        var covered = Solve(garment, OldBody(cut: false, braOut: 0.002f), target);

        Assert.True(holed.SourceSmallclothes > 0);
        Assert.Equal(0, covered.SourceSmallclothes);

        var report = new BodyRetarget.Report(1, 0, 1, 0, 0, 0.01f, 0f, 0, false, SourceSmallclothes: 117);
        Assert.Contains("117 old underwear points follow the new body", Proteus.Gui.BodyRetargetPanel.Describe(report, []));
        Assert.DoesNotContain("underwear points", Proteus.Gui.BodyRetargetPanel.Describe(report with { SourceSmallclothes = 0 }, []));
    }

    [Fact]
    public void A_same_mesh_pair_keeps_the_bras_own_move()
    {
        // [EDGE-bra-hole-transfer-5] The same mesh, its bra 5 mm further out: the correspondence knows the bra's move,
        // and the landing never replaces it.
        var source = OldBody();
        var target = OldBody(braOut: BraOut + 0.005f);
        Assert.True(IdentityCorrespondence.TryBuild(source, target, "chest", out var identity, out string why), why);
        var garment = Garment();

        var solved = BodyRetarget.Solve(garment, [new BodyRetarget.SlotPair("_top", identity!, target, SourceIsGameChest: true)],
                                        "_top");
        foreach (var p in Middle(Final(garment, solved)))
            Assert.InRange(p.Z, BraOut + Gap + 0.005f - 1e-4f, BraOut + Gap + 0.005f + 1e-4f);
    }

    [Fact]
    public void A_worn_new_body_takes_the_bra_onto_its_own_underwear()
    {
        // [EDGE-bra-hole-transfer-6] The new body has no skin there either, only its own underwear 5 mm out: the old
        // bra lands on that, not on the rim of the new hole.
        var source = OldBody();
        var target = OldBody(braOut: 0.005f);
        var garment = Garment();

        foreach (var p in Middle(Final(garment, Solve(garment, source, target))))
            Assert.InRange(p.Z, 0.005f + Gap - 0.001f, 0.005f + Gap + 0.001f);
    }

    [Fact]
    public void Out_of_reach_keeps_the_carried_move()
    {
        // [EDGE-bra-hole-transfer-7] The new body has a hole far wider than the bra and nothing in it: the bra's middle
        // finds nothing within reach and keeps the move the skin around it gives — the old reading's.
        var source = OldBody();
        var target = OldBody(bra: false, hole: (0.2f, 0.8f));
        var garment = Garment();

        var now = Middle(Final(garment, Solve(garment, source, target)));
        var old = Middle(Final(garment, Solve(garment, source, target, old: true)));
        Assert.Equal(old.Count, now.Count);
        for (int i = 0; i < now.Count; i++) Assert.InRange(now[i].Z - old[i].Z, -1e-4f, 1e-4f);
    }

    [Fact]
    public void Cups_never_land_across_the_middle()
    {
        // [EDGE-bra-hole-transfer-8] The other side's skin is nearer than this side's: no landing crosses the middle.
        var source = OldBody();
        var near = Grid(-0.2f, -0.02f, 0f, 1f, SkinNx, SkinNy, (_, _) => 0f);
        var far = Grid(0.02f, 0.2f, 0f, 1f, SkinNx, SkinNy, (_, _) => -0.05f, near.Pos.Count / 3);
        var target = SkinlessTargetTests.Model([.. near.Pos, .. far.Pos], [(Skin, [.. near.Tris, .. far.Tris], 0u)], []);
        var skin = new BodySurface(source, 0.02f);
        var over = BodyRetarget.SmallclothesOverHoles(source, skin);
        Assert.NotNull(over);

        var whole = new Vector3?[source.Positions.Length / 3];
        for (int v = 0; v < (SkinNx + 1) * (SkinNy + 1); v++) whole[v] = Vector3.Zero;
        Assert.True(BodyRetarget.LandSmallclothes(source, skin, whole, over!, target) > 0);
        for (int v = 0; v < whole.Length; v++)
            if (over![v]) Assert.True(At(source, v).X + whole[v]!.Value.X > 0f, $"bra vertex {v} landed across the middle");
    }

    [Fact]
    public void A_landing_that_would_fold_is_relaxed()
    {
        // [EDGE-bra-hole-transfer-9] A new body ridged sharply across the bra: no bra triangle is left turned over, and
        // the garment folds no more than it did.
        var source = OldBody();
        var target = NewBody((x, y) => MathF.Abs(y - 0.5f) < 0.08f ? 0.02f * MathF.Sin(x * 400f) : 0f);
        var skin = new BodySurface(source, 0.02f);
        var over = BodyRetarget.SmallclothesOverHoles(source, skin)!;
        var whole = new Vector3?[source.Positions.Length / 3];
        for (int v = 0; v < (SkinNx + 1) * (SkinNy + 1); v++) whole[v] = At(target, v) - At(source, v);
        BodyRetarget.LandSmallclothes(source, skin, whole, over, target);

        var bra = source.Parts.Single(p => p.Material == Bra).Triangles;
        int folded = 0;
        for (int t = 0; t + 2 < bra.Length; t += 3)
        {
            int a = bra[t], b = bra[t + 1], c = bra[t + 2];
            if (!over[a] || !over[b] || !over[c]) continue;
            var n0 = Vector3.Cross(At(source, b) - At(source, a), At(source, c) - At(source, a));
            Vector3 pa = At(source, a) + whole[a]!.Value, pb = At(source, b) + whole[b]!.Value, pc = At(source, c) + whole[c]!.Value;
            if (Vector3.Dot(n0, Vector3.Cross(pb - pa, pc - pa)) <= 0f) folded++;
        }
        Assert.Equal(0, folded);

        var garment = Garment();
        Assert.True(Solve(garment, source, target).Folded <= Solve(garment, source, target, old: true).Folded);
    }

    [Fact]
    public void Garment_skin_over_the_hole_lands_on_the_new_chest()
    {
        // [EDGE-bra-hole-transfer-10] The garment's own skin, laid on the bra as authored, is laid on the new chest —
        // not on the hole's rim.
        var source = OldBody();
        var target = NewBody((_, _) => 0f);
        var garment = Garment(skinPatch: true);

        var laid = Final(garment, Solve(garment, source, target, replaceSkin: true), Skin)
            .Where(p => MathF.Abs(p.Y - 0.5f) < 0.04f).ToList();
        Assert.NotEmpty(laid);
        foreach (var p in laid) Assert.InRange(p.Z, -0.001f, 0.001f);
    }

    [Fact]
    public void Held_parts_stay()
    {
        // [EDGE-bra-hole-transfer-11] Cloth the user holds does not move, the bra read or not.
        var source = OldBody();
        var target = NewBody((_, _) => 0f);
        var garment = Garment();
        var held = garment.Parts.SelectMany(p => p.Triangles).ToHashSet();

        var solved = Solve(garment, source, target, held: held);
        foreach (int v in held)
        {
            var d = solved.Edit.DeltaAt(v);
            Assert.True(d.X == 0f && d.Y == 0f && d.Z == 0f, $"held vertex {v} moved");
        }
    }

    [Fact]
    public void A_sliver_under_the_minimum_changes_nothing()
    {
        // [EDGE-bra-hole-transfer-12] A strap's width over a narrow cut is not a bust the cloth was fitted over.
        var source = OldBody(braX: (0.10f, 0.105f), hole: (0.49f, 0.51f));
        var target = NewBody((_, _) => 0f);
        var garment = Garment();

        Assert.Null(BodyRetarget.SmallclothesOverHoles(source, new BodySurface(source, 0.02f)));
        SameMoves(Solve(garment, source, target), Solve(garment, source, target, old: true), garment.Positions.Length / 3);
    }

    [Fact]
    public void A_vertex_on_the_bra_is_not_snapped()
    {
        // [EDGE-bra-hole-transfer-13] Cloth lying exactly on a bra vertex is not snapped to it: snapped cloth is never
        // pushed out, and would sit exactly on the new skin.
        var source = OldBody();
        var target = NewBody((_, _) => 0f);
        var garment = Garment(over: BraOut);   // its rows lie on the bra plate's own vertices where the grids meet

        Assert.Equal(Solve(garment, source, target, old: true).Snapped, Solve(garment, source, target).Snapped);
    }

    [Fact]
    public void The_old_reading_is_kept_behind_its_switch()
    {
        // [EDGE-bra-hole-transfer-14] NoSourceWorn reads the old body as its skin alone: nothing landed, the cloth over
        // the bust left where the rim put it.
        var source = OldBody();
        var target = NewBody((_, _) => 0f);
        var garment = Garment();

        var old = Solve(garment, source, target, old: true);
        Assert.Equal(0, old.SourceSmallclothes);
        foreach (var p in Middle(Final(garment, old))) Assert.True(p.Z >= 0.035f - 1e-4f);
    }

    [Fact]
    public void Two_slots_blend_as_before_away_from_the_hole()
    {
        // [EDGE-bra-hole-transfer-16] A legs body below the chest: cloth on the legs, far from the bra, moves as before.
        var source = OldBody();
        var target = NewBody((_, _) => 0f);
        var legsFrom = SkinlessTargetTests.Model(Grid(0.02f, 0.2f, -1f, 0f, SkinNx, SkinNy, (_, _) => 0f).Pos.ToArray(),
                                                 [(Skin, Grid(0.02f, 0.2f, -1f, 0f, SkinNx, SkinNy, (_, _) => 0f).Tris.ToArray(), 0u)], []);
        var legsTo = SkinlessTargetTests.Model(Grid(0.02f, 0.2f, -1f, 0f, SkinNx, SkinNy, (_, _) => 0.01f).Pos.ToArray(),
                                               [(Skin, Grid(0.02f, 0.2f, -1f, 0f, SkinNx, SkinNy, (_, _) => 0.01f).Tris.ToArray(), 0u)], []);
        var (pos, tris) = Grid(0.05f, 0.17f, -0.8f, 0.7f, 6, 30, (_, _) => 0.04f);
        var garment = SkinlessTargetTests.Model(pos.ToArray(), [(Cloth, tris.ToArray(), 0u)], []);
        List<BodyRetarget.SlotPair> pairs =
        [
            new("_top", new SkinField(source, target), target, SourceIsGameChest: true),
            new("_dwn", new SkinField(legsFrom, legsTo), legsTo),
        ];

        var now = BodyRetarget.Solve(garment, pairs, "_top");
        var old = BodyRetarget.WithTuning(new BodyRetarget.Tuning { NoSourceWorn = true },
                                          () => BodyRetarget.Solve(garment, pairs, "_top"));
        for (int v = 0; v < garment.Positions.Length / 3; v++)
        {
            if (At(garment, v).Y > -0.3f) continue;
            var a = now.Edit.DeltaAt(v);
            var b = old.Edit.DeltaAt(v);
            Assert.True(a.X == b.X && a.Y == b.Y && a.Z == b.Z, $"vertex {v} at y {At(garment, v).Y:0.00} moved differently");
        }
    }
}
