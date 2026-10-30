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
/// A game top's cloth the refit carried off the new chest, brought back toward its author's standoff
/// (<see cref="BodyRetarget.KeepStandoff"/>), on <see cref="SkinTuckSheet.FloatingNeckline"/>: the bust grows while the chest
/// above it sinks, and the push carries the neckline off the upper chest with the cloth it clears out of the breast.
/// </summary>
public class NecklineFloatTests(ITestOutputHelper output)
{
    private static readonly BodyRetarget.Tuning NoPull = new(NoKeepStandoff: true);

    private BodyRetarget.Planned GameTop(byte[] top, BodyRetarget.SlotPair pair, bool across = true)
    {
        var plan = RefitCore.PlanRefit(ModelPartReader.Read(top)!, top, [pair], "_top", across: across, gameGear: true, out _);
        output.WriteLine($"pulled {plan.Report.StandoffPulled} up to {plan.Report.StandoffWorst * 1000:0.0} mm, held "
                         + $"{plan.Report.StandoffHeld}; folded {plan.Report.Folded}");
        return plan;
    }

    private BodyRetarget.Planned Unpulled(byte[] top, BodyRetarget.SlotPair pair, bool across = true)
        => BodyRetarget.WithTuning(NoPull, () => GameTop(top, pair, across));

    private static Vector3 Pos(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    private static float Row(ModelParts m, int v) => (m.Positions[v * 3 + 1] - Bottom) / Step;

    private static IEnumerable<int> ClothVerts(ModelParts m)
        => m.Parts.Where(p => p.Island < 0 && p.Material == Cloth).SelectMany(p => p.Triangles).Distinct().Order();

    /// <summary>Each cloth vertex: its row as authored, its standoff off the source chest as authored, and off the new chest
    /// as laid — by this test's own nearest point.</summary>
    private static List<(int V, float Row, float Own, float Now)> Standoffs(byte[] top, BodyRetarget.SlotPair pair,
                                                                           BodyRetarget.Planned plan)
    {
        var garment = ModelPartReader.Read(top)!;
        var laid = Laid(top, plan);
        var source = new BodySurface(ModelPartReader.Read(pair.SourceModel!)!, 0.01f);
        var target = new BodySurface(ModelPartReader.Read(pair.TargetModel!)!, 0.01f);
        return [.. ClothVerts(garment).Select(v => (v, Row(garment, v), Standoff(Pos(garment, v), source), Standoff(Pos(laid, v), target)))];
    }

    /// <summary>The fixture floats: some cloth above the breast stands more than 5 mm further off than authored.</summary>
    private void AssertFloats(byte[] top, BodyRetarget.SlotPair pair)
    {
        var floating = Standoffs(top, pair, Unpulled(top, pair)).Where(s => s.Row >= 9 && s.Now - s.Own > 0.005f).ToList();
        output.WriteLine($"floating without the pass: {floating.Count}, up to {(floating.Count > 0 ? floating.Max(s => s.Now - s.Own) * 1000 : 0):0.0} mm past");
        Assert.NotEmpty(floating);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-float-1")]
    [Trait("Plan", "AC-neckline-float-3")]
    [Trait("Speed", "Slow")]
    public void A_neckline_lifted_off_a_shrunk_upper_chest_comes_back_to_its_standoff()
    {
        var (top, pair) = FloatingNeckline();
        AssertFloats(top, pair);
        // And it floats for the game's reasons: the push stage carries the neckline itself (rows 9 and up) off — see
        // neckline-float Step 0 — not merely the breast cloth it clears.
        var stages = new List<(string, SecondSkinWriter.Vec3[])>();
        var unpulled = BodyRetarget.WithTuning(NoPull, () => RefitCore.PlanRefit(ModelPartReader.Read(top)!, top, [pair], "_top",
            across: true, gameGear: true, out _, trace: (s, d) => stages.Add((s, (SecondSkinWriter.Vec3[])d.Clone()))));
        var garment = ModelPartReader.Read(top)!;
        var laidUnpulled = Laid(top, unpulled);
        var last = stages[^1].Item2;
        int pushIndex = stages.FindIndex(s => s.Item1 == "push");
        float neckLift = 0f;
        foreach (int v in ClothVerts(garment).Where(v => Row(garment, v) >= 9))
        {
            var d = Pos(laidUnpulled, v) - Pos(garment, v);
            int n = Enumerable.Range(0, last.Length).OrderBy(k => (new Vector3(last[k].X, last[k].Y, last[k].Z) - d).LengthSquared()).First();
            neckLift = MathF.Max(neckLift, stages[pushIndex].Item2[n].Z - stages[pushIndex - 1].Item2[n].Z);
        }
        output.WriteLine($"the push lifts the neckline up to {neckLift * 1000:0.0} mm");
        Assert.True(neckLift > 0.002f, "the push does not carry the neckline off");

        // Two rows or more from the cloth the clear holds at the clearance (row 8): back to the author's standoff. Row 9,
        // beside it, comes back only as far as the face between them keeps clear of the skin.
        var plan = GameTop(top, pair);
        Assert.True(plan.Report.StandoffPulled > 0);
        var after = Standoffs(top, pair, plan);
        foreach (var s in after.Where(s => s.Row >= 9.5f))
        {
            Assert.True(s.Now <= s.Own + BodyRetarget.FloatSlack + 0.0005f, $"row {s.Row}: {s.Now * 1000:0.0} mm off, own {s.Own * 1000:0.0}");
            Assert.True(s.Now >= BodyRetarget.FloatFloor - 0.0002f, $"row {s.Row}: {s.Now * 1000:0.0} mm off, under the floor");
        }
        var beforePull = Standoffs(top, pair, Unpulled(top, pair));
        Assert.True(after.Where(s => s.Row is > 8.5f and < 9.5f).Sum(s => s.Now)
                    < beforePull.Where(s => s.Row is > 8.5f and < 9.5f).Sum(s => s.Now), "row 9 does not come back at all");
    }

    [Fact]
    [Trait("Plan", "AC-neckline-float-1")]
    public void A_coarse_neckline_floats_less()
    {
        // The game's coarse cloth: its faces span the rounding chest, and the guards hold some of it back — less floating.
        var (top, pair) = FloatingNeckline(coarse: true);
        AssertFloats(top, pair);
        float Past(BodyRetarget.Planned plan) => Standoffs(top, pair, plan).Where(s => s.Row >= 9).Sum(s => MathF.Max(0f, s.Now - s.Own));
        float with = Past(GameTop(top, pair)), without = Past(Unpulled(top, pair));
        output.WriteLine($"past their standoff: {with * 1000:0} mm, without the pass {without * 1000:0}");
        Assert.True(with < 0.5f * without);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-float-1")]
    [Trait("Plan", "EDGE-neckline-float-3")]
    [Trait("Plan", "AC-neckline-float-6")]
    public void Buried_cloth_and_the_drape_below_the_bust_are_left_where_the_refit_put_them()
    {
        // Rows 2-8 were authored inside the body (own -6 mm), and row 2, below the breast, is carried 15 mm off — a drape.
        var (top, pair) = FloatingNeckline(coarse: true);
        var full = Laid(top, GameTop(top, pair));
        var without = Laid(top, Unpulled(top, pair));
        var garment = ModelPartReader.Read(top)!;
        foreach (int v in ClothVerts(garment).Where(v => Row(garment, v) <= 8))
            Assert.True(Vector3.Distance(Pos(full, v), Pos(without, v)) <= 0.0005f, $"row {Row(garment, v)} moved");
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-float-2")]
    [Trait("Plan", "EDGE-neckline-float-21")]
    public void Cloth_within_its_standoff_stays()
    {
        // The same body before and after, and nothing buried for the push to lift its neighbours with: nothing was carried
        // off, and the refit is what it was without the pass.
        var top = Top(_ => 0.006f);
        var pair = Grown(Bust(0f), Bust(0f));
        var plan = GameTop(top, pair);
        Assert.Equal(0, plan.Report.StandoffPulled);
        Assert.Equal(Unpulled(top, pair).Model, plan.Model);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-float-4")]
    [Trait("Plan", "AC-neckline-float-6")]
    public void Where_the_body_grew_nothing_is_pulled()
    {
        // The upper chest grows 8 mm instead of sinking: cloth standing off it there is the body's, not the refit's.
        var (top, pair) = FloatingNeckline(shrink: -0.008f);
        var full = Laid(top, GameTop(top, pair));
        var without = Laid(top, Unpulled(top, pair));
        var garment = ModelPartReader.Read(top)!;
        foreach (int v in ClothVerts(garment).Where(v => Row(garment, v) >= 9))
            Assert.True(Vector3.Distance(Pos(full, v), Pos(without, v)) <= 0.0005f, $"row {Row(garment, v)} moved");
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-float-6")]
    public void The_pull_stops_at_the_floor()
    {
        // Authored half a millimetre off the chest above the bust: brought back no nearer than the floor.
        var (_, pair) = FloatingNeckline(coarse: true);
        var top = CoarseNeckline(-0.006f, 0.0005f);
        var plan = GameTop(top, pair);
        Assert.True(plan.Report.StandoffPulled > 0);
        foreach (var s in Standoffs(top, pair, plan).Where(s => s.Row >= 9))
            Assert.True(s.Now >= BodyRetarget.FloatFloor - 0.0002f, $"row {s.Row}: {s.Now * 1000:0.0} mm off");
    }

    [Theory]
    [Trait("Plan", "EDGE-neckline-float-7")]
    [Trait("Plan", "EDGE-neckline-float-10")]
    [Trait("Plan", "AC-neckline-float-5")]
    [InlineData(false)]
    [InlineData(true)]
    public void The_pull_turns_nothing_over_and_stretches_nothing(bool coarse)
    {
        var (top, pair) = FloatingNeckline(coarse: coarse);
        var authored = ModelPartReader.Read(top)!;
        var full = Laid(top, GameTop(top, pair));
        var without = Laid(top, Unpulled(top, pair));
        Assert.True(FoldedAgainstAuthored(authored, full) <= FoldedAgainstAuthored(authored, without));
        Assert.Equal(0, ClothStretch(without, full));
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-float-8")]
    public void A_pulled_face_keeps_the_skin_out_of_it_between_its_corners()
    {
        // The coarse neckline's faces span 20-40 mm of a chest that rounds under them. Every face, across a grid of this
        // test's own: the tuck's room kept where the face had its 3.5 mm, and no nearer the skin than the clearance (or than
        // it was, nearer already) where it did not.
        var (top, pair) = FloatingNeckline(coarse: true);
        var plan = GameTop(top, pair);
        Assert.True(plan.Report.StandoffPulled > 0);
        var laid = Laid(top, plan);
        var without = Laid(top, Unpulled(top, pair));
        var target = new BodySurface(ModelPartReader.Read(pair.TargetModel!)!, 0.01f);
        float Least(ModelParts m, int ia, int ib, int ic)
        {
            Vector3 a = Pos(m, ia), b = Pos(m, ib), c = Pos(m, ic);
            float least = float.MaxValue;
            for (int i = 0; i <= 8; i++)
                for (int j = 0; i + j <= 8; j++)
                    least = MathF.Min(least, Standoff(a + (b - a) * (i / 8f) + (c - a) * (j / 8f), target));
            return least;
        }
        foreach (var part in laid.Parts.Where(p => p.Island < 0 && p.Material == Cloth))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                float now = Least(laid, a, b, c), was = Least(without, a, b, c);
                // This grid is finer than the pass's: a quarter millimetre of its sampling allowed.
                float least = was >= BodyRetarget.TuckClear + 0.0005f ? BodyRetarget.TuckClear - 0.00025f
                            : MathF.Min(BodyRetarget.Clearance, was) - 1e-4f;
                Assert.True(now >= least, $"skin within {now * 1000:0.0} mm of a face (without the pass {was * 1000:0.0})");
            }
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-float-9")]
    [Trait("Plan", "AC-neckline-float-3")]
    public void A_shell_is_not_pulled_through_its_lining()
    {
        // A shell 8 mm off over rows 9-13 and a lining 4 mm off under it: straight out from every lining vertex, the shell.
        var (top, pair) = FloatingNeckline(lining: 0.004f);
        var plan = GameTop(top, pair);
        var laid = Laid(top, plan);
        var meshes = laid.Parts.Where(p => p.Island < 0 && p.Material == Cloth).GroupBy(p => p.Mesh).OrderBy(g => g.Key).ToList();
        var shell = meshes[0].SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                                        .Select(t => (Pos(laid, p.Triangles[t * 3]), Pos(laid, p.Triangles[t * 3 + 1]),
                                                                      Pos(laid, p.Triangles[t * 3 + 2])))).ToList();
        foreach (int v in meshes[1].SelectMany(p => p.Triangles).Distinct())
        {
            var p = Pos(laid, v);
            if (Row(laid, v) < 9.5f || Row(laid, v) > 12.5f || MathF.Abs(p.X) > X(Columns - 1) - 0.001f) continue;
            Assert.True(shell.Any(t => Ahead(p, t)), $"lining vertex {v} is not behind the shell");
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-float-11")]
    public void A_hard_piece_is_not_pulled()
    {
        var (top, pair) = FloatingNeckline(coarse: true);
        var garment = ModelPartReader.Read(top)!;
        var piece = ClothVerts(garment).Where(v => Row(garment, v) >= 9).ToList();
        BodyRetarget.Planned Kept() => BodyRetarget.Plan(garment, top, [pair], "_top", held: new HashSet<int>(), replaceSkin: true,
                                                          acrossBodies: true, clearBody: true, cutHidden: true, keepShape: [piece],
                                                          tuckSlot: "_top", deepClear: true, keepStandoff: true);
        var full = Laid(top, Kept());
        var without = Laid(top, BodyRetarget.WithTuning(NoPull, Kept));
        foreach (int v in piece) Assert.Equal(Pos(without, v), Pos(full, v));
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-float-12")]
    [Trait("Plan", "EDGE-neckline-float-18")]
    [Trait("Plan", "AC-neckline-float-5")]
    public void The_tops_own_skin_and_every_normal_are_left_alone()
    {
        // The top's skin here is not welded to its cloth: it stays. (Skin welded to a rim the pass brings back moves with
        // it — Dirndl's chemise on the game's turned-in lip — judged by the local tests' folds and stretch with the skin in.)
        var (top, pair) = FloatingNeckline();
        var plan = GameTop(top, pair);
        Assert.True(plan.Report.StandoffPulled > 0);
        var full = Laid(top, plan);
        var without = Laid(top, Unpulled(top, pair));
        var garment = ModelPartReader.Read(top)!;
        foreach (int v in garment.Parts.Where(p => p.Island < 0 && p.Material == Skin).SelectMany(p => p.Triangles).Distinct())
            Assert.Equal(Pos(without, v), Pos(full, v));
        Assert.Equal(without.Normals, full.Normals);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-float-13")]
    public void No_pull_when_the_chests_weights_cannot_be_read()
    {
        var (_, pair) = FloatingNeckline();
        Assert.NotNull(BodyRetarget.StandoffTest([pair], "_top"));
        Assert.Null(BodyRetarget.StandoffTest([pair with { TargetModel = new byte[64] }], "_top"));
        Assert.Null(BodyRetarget.StandoffTest([pair], "_dwn"));
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-float-14")]
    public void No_pull_within_one_body()
    {
        var (top, pair) = FloatingNeckline(coarse: true);
        Assert.Equal(0, GameTop(top, pair, across: false).Report.StandoffPulled);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-float-15")]
    public void The_tuck_judges_the_pulled_cloth()
    {
        // Every corner of every skin triangle the written model draws over the neckline rows keeps the tuck's room under
        // the pulled cloth, straight out from it.
        var (top, pair) = FloatingNeckline();
        var plan = GameTop(top, pair);
        Assert.True(plan.Report.StandoffPulled > 0);
        var model = ModelPartReader.Read(plan.Model)!;
        var cloth = model.Parts.Where(p => p.Island < 0 && p.Material == Cloth)
                         .SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                                    .Select(t => (Pos(model, p.Triangles[t * 3]), Pos(model, p.Triangles[t * 3 + 1]),
                                                                  Pos(model, p.Triangles[t * 3 + 2])))).ToList();
        foreach (int v in model.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                               .SelectMany(p => p.Triangles).Distinct())
        {
            var p = Pos(model, v);
            float? over = cloth.Select(t => Along(p, t)).Where(d => d != null).Select(d => d!.Value).DefaultIfEmpty().Min();
            if (over is float d && d > 0f)
                Assert.True(d >= BodyRetarget.TuckClear - 1e-4f || d > 0.02f, $"skin at row {Row(model, v):0.0} {d * 1000:0.0} mm under cloth");
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-float-16")]
    public void Deep_cleared_cloth_is_not_pulled_back()
    {
        var top = CoarseBuriedTop(0.012f);
        var pair = Grown(Bust(0f), Bust(0.02f));
        var plan = GameTop(top, pair);
        Assert.True(plan.Report.DeepCleared > 0);
        var full = Laid(top, plan);
        var without = Laid(top, Unpulled(top, pair));
        foreach (int v in plan.Report.DeepAt!) Assert.Equal(Pos(without, v), Pos(full, v));
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-float-17")]
    public void The_pull_is_deterministic()
    {
        var (top, pair) = FloatingNeckline(coarse: true);
        Assert.Equal(GameTop(top, pair).Model, GameTop(top, pair).Model);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-float-3")]
    public void The_floor_leaves_the_tuck_its_room()
        => Assert.True(BodyRetarget.FloatFloor >= BodyRetarget.TuckClear + BodyRetarget.Clearance);

    [Fact]
    [Trait("Plan", "AC-neckline-float-6")]
    public void The_floating_neckline_is_where_a_game_top_keeps_its_standoff_and_the_Studio_does_not()
    {
        var (top, pair) = FloatingNeckline(coarse: true);
        var garment = ModelPartReader.Read(top)!;
        Assert.True(GameTop(top, pair).Report.StandoffPulled > 0);
        var studio = BodyRetarget.Plan(garment, top, [pair], "_top", held: new HashSet<int>(), replaceSkin: true, acrossBodies: true,
                                       clearBody: true, cutHidden: true,
                                       keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)),
                                       tuckSlot: RefitCore.TuckSlot(true, "_top"));
        Assert.Equal(0, studio.Report.StandoffPulled);
    }

    /// <summary>How far straight out (+Z) from <paramref name="p"/> the triangle lies, or null if the ray misses it.</summary>
    private static float? Along(Vector3 p, (Vector3 A, Vector3 B, Vector3 C) t)
    {
        var e1 = t.B - t.A; var e2 = t.C - t.A;
        var h = Vector3.Cross(Vector3.UnitZ, e2);
        float det = Vector3.Dot(e1, h);
        if (MathF.Abs(det) < 1e-12f) return null;
        var s = p - t.A;
        float u = Vector3.Dot(s, h) / det;
        if (u < -1e-4f || u > 1f + 1e-4f) return null;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(Vector3.UnitZ, q) / det;
        if (v < -1e-4f || u + v > 1f + 1e-4f) return null;
        return Vector3.Dot(e2, q) / det;
    }

    private static bool Ahead(Vector3 p, (Vector3 A, Vector3 B, Vector3 C) t) => Along(p, t) is > 0f;
}
