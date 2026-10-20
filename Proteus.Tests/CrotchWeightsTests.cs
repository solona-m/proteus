using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.SyntheticCrotch;

namespace Proteus.Tests;

/// <summary>
/// Refitting cloth that passes between the legs across body mods: its leg weights eased almost away about the new body's
/// crotch, so spreading the legs no longer pulls it apart with them — and everything that goes on down a leg, or never
/// came near the crotch, as before. Each refit is compared with the same refit without the ease.
/// </summary>
public class CrotchWeightsTests
{
    private const float Tol = 0.01f;   // a byte of weight, and a little

    private static readonly IReadOnlySet<string> Bones =
        new HashSet<string> { "j_kosi", "j_asi_a_l", "j_asi_a_r", "ya_daitai_phys_l", "ya_daitai_phys_r", "iv_shiri_l" };

    private static int[] Where(byte[] garment, Func<Vector3, bool> keep)
        => [.. Positions(garment).Select((p, i) => (p, i)).Where(t => keep(t.p)).Select(t => t.i)];

    /// <summary>Cloth on the perineum in front of the crotch point, where the ease is whole.</summary>
    private static bool Gusset(Vector3 p) => MathF.Abs(p.Y - Crotch) < 0.005f && MathF.Abs(p.X) <= 0.03f && p.Z >= 0f;

    private static float Bone(List<(string Bone, float W)> w, string bone) => w.Where(i => i.Bone == bone).Sum(i => i.W);

    private static void Same(List<(string Bone, float W)>[] a, List<(string Bone, float W)>[] b, IEnumerable<int> vertices)
    {
        foreach (int v in vertices)
            Assert.True(a[v].OrderBy(i => i.Bone).SequenceEqual(b[v].OrderBy(i => i.Bone)),
                        $"vertex {v}: {string.Join(",", a[v])} against {string.Join(",", b[v])}");
    }

    private static void AllSame(byte[] garment, (BodyRetarget.WeightPlan, byte[], List<(string Bone, float W)>[] W) a,
                                (BodyRetarget.WeightPlan, byte[], List<(string Bone, float W)>[] W) b)
        => Same(a.W, b.W, Enumerable.Range(0, Positions(garment).Length));

    // ── the rule ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Panties_between_the_legs_keep_a_tenth_of_their_leg_weight()
    {
        // [AC-crotch-leg-weights-1]
        var garment = Garment(Briefs);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);

        var gusset = Where(garment, Gusset);
        Assert.NotEmpty(gusset);
        foreach (int v in gusset)
        {
            float was = Legs(before.Weights[v]), now = Legs(eased.Weights[v]);
            Assert.True(was > 0.3f, $"vertex {v} had only {was} of the legs to begin with");
            Assert.True(now <= 0.1f * was + Tol, $"vertex {v} kept {now} of {was}");
            Assert.Equal(Bone(before.Weights[v], "j_kosi") + (was - now), Bone(eased.Weights[v], "j_kosi"), 2);
            Assert.Equal(1f, eased.Weights[v].Sum(i => i.W), 2);
            Assert.True(eased.Weights[v].Count <= 4);
        }
        Assert.True(eased.Plan.CrotchEased > 0);
    }

    [Fact]
    public void Spread_legs_barely_stretch_the_gusset()
    {
        // [AC-crotch-leg-weights-1] Measured across the perineum, edge to edge, on the row under the crotch point.
        var garment = Garment(Briefs);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);
        var at = Positions(garment);
        int left = Where(garment, p => Gusset(p) && MathF.Abs(p.Z) < 0.005f).MaxBy(v => at[v].X);
        int right = Where(garment, p => Gusset(p) && MathF.Abs(p.Z) < 0.005f).MinBy(v => at[v].X);
        float bind = Vector3.Distance(at[left], at[right]);

        float Excess(byte[] model)
        {
            var posed = Posed(model, Pose(Spread));
            return Vector3.Distance(posed[left], posed[right]) / bind - 1f;
        }
        float was = Excess(before.Rebuilt), now = Excess(eased.Rebuilt);
        Assert.True(was > 0.15f, $"the gusset only stretched {was:P0} without the ease: the test does not bite");
        Assert.True(now <= 0.25f * was, $"eased, the gusset still stretched {now:P0} against {was:P0}");

        // No new folds, spread or sitting.
        foreach (var pose in new[] { Pose(Spread), Pose(Sit) })
            Assert.True(Folded(eased.Rebuilt, Posed(eased.Rebuilt, pose)) <= Folded(before.Rebuilt, Posed(before.Rebuilt, pose)));
    }

    [Fact]
    public void CrotchStrength_is_the_box_described()
    {
        // [AC-crotch-leg-weights-2]
        var a = new BodyRetarget.CrotchAnchor(0.8f, 0f, 1f, new Vector3(0f, 0.8f, 0f));
        float S(float dx, float dy, float dz) => BodyRetarget.CrotchStrength(new Vector3(dx, 0.8f + dy, dz), a);

        Assert.Equal(1f, S(0, 0, 0), 4);
        Assert.Equal(1f, S(0.03f, 0.02f, 0.10f), 4);       // whole out to the full extents, and anywhere in front
        Assert.Equal(0.5f, S(0, 0.045f, 0), 3);             // half way up the fade above
        Assert.Equal(0f, S(0, 0.07f, 0), 4);
        Assert.Equal(0.5f, S(0, -0.035f, 0), 3);            // below
        Assert.Equal(0f, S(0, -0.05f, 0), 4);
        Assert.Equal(0.5f, S(0.045f, 0, 0), 3);             // to the side
        Assert.Equal(0f, S(-0.06f, 0, 0), 4);
        Assert.Equal(0.5f, S(0, 0, -0.02f), 3);             // behind: the seat
        Assert.Equal(0f, S(0, 0, -0.04f), 4);

        // Half the body, half the box.
        var small = new BodyRetarget.CrotchAnchor(0.4f, 0f, 0.5f, new Vector3(0f, 0.4f, 0f));
        Assert.Equal(0.5f, BodyRetarget.CrotchStrength(new Vector3(0f, 0.4f + 0.0225f, 0f), small), 3);
    }

    [Fact]
    public void FindCrotch_is_the_lowest_midline_skin_on_both_thighs()
    {
        // [AC-crotch-leg-weights-2]
        var body = Body();
        var a = BodyRetarget.FindCrotch([(ModelPartReader.Read(body)!, ModelSkinReader.Read(body, null, null)!)])!.Value;
        Assert.Equal(Crotch, a.Y, 3);
        Assert.Equal(0f, a.MidX, 3);
        Assert.True(MathF.Abs(a.At.Z) < 0.02f, $"crotch point at z {a.At.Z}");
        Assert.Equal(1f, a.Scale, 2);
    }

    [Fact]
    public void A_crotch_split_down_the_middle_is_found_and_eased()
    {
        // [AC-crotch-leg-weights-2] [AC-crotch-leg-weights-1] The Body SE's way: no skin on both thighs, each side on its
        // own. The crotch is where the two sides meet, and panties made on that body still ease.
        var body = Body(split: true);
        var a = BodyRetarget.FindCrotch([(ModelPartReader.Read(body)!, ModelSkinReader.Read(body, null, null)!)])!.Value;
        Assert.Equal(Crotch, a.Y, 3);
        Assert.True(MathF.Abs(a.MidX) <= 0.006f, $"midline at {a.MidX}");

        var garment = Garment(Briefs, split: true);
        var eased = Refit(garment, source: body);
        var before = Refit(garment, ease: false, source: body);
        // Within 2 cm of the midline: where the sides meet on a 1 cm grid, the midline falls between two columns.
        var gusset = Where(garment, p => Gusset(p) && MathF.Abs(p.X) <= 0.02f).Where(v => Legs(before.Weights[v]) > 0.3f).ToList();
        Assert.NotEmpty(gusset);
        var at = Positions(garment);
        Assert.All(gusset, v => Assert.True(Legs(eased.Weights[v]) <= 0.1f * Legs(before.Weights[v]) + Tol,
                                            $"{at[v]}: {string.Join(",", eased.Weights[v])} of {string.Join(",", before.Weights[v])}; "
                                            + $"crotch at {a.At}"));
    }

    [Fact]
    public void The_ease_fades_out_and_matches_baseline_beyond_it()
    {
        // [AC-crotch-leg-weights-2] A leotard: hips to the waist and the perineum.
        var garment = Garment([new(Hips(Waist)), new(Perineum)]);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);
        var at = Positions(garment);

        Same(eased.Weights, before.Weights, Where(garment, p => p.Y >= Crotch + 0.07f || MathF.Abs(p.X) >= 0.06f
                                                                || p.Z <= -0.04f));

        // Smooth: the share of its legs each vertex kept differs between neighbours no more than the steepest ramp allows.
        var parts = ModelPartReader.Read(garment)!;
        float Kept(int v) => Legs(eased.Weights[v]) / Legs(before.Weights[v]);
        foreach (var part in parts.Parts.Where(p => p.Island < 0))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int u = part.Triangles[t + e], w = part.Triangles[t + (e + 1) % 3];
                    if (Legs(before.Weights[u]) < 0.05f || Legs(before.Weights[w]) < 0.05f) continue;
                    float slope = (1f - BodyRetarget.CrotchLegKept) / (BodyRetarget.CrotchFade - BodyRetarget.CrotchFull);
                    slope = MathF.Max(slope, (1f - BodyRetarget.CrotchLegKept) / (BodyRetarget.CrotchSideFade - BodyRetarget.CrotchSideFull));
                    Assert.True(MathF.Abs(Kept(u) - Kept(w)) <= slope * Vector3.Distance(at[u], at[w]) + 0.05f,
                                $"edge {at[u]}-{at[w]}: kept {Kept(u)} against {Kept(w)}");
                }
    }

    // ── what goes on down a leg ─────────────────────────────────────────────────────────────────────────

    private static void LeftAsBefore(byte[] garment, string why)
    {
        var eased = Refit(garment);
        AllSame(garment, eased, Refit(garment, ease: false));
        Assert.True(eased.Plan.CrotchVetoed >= 1);
        Assert.Contains(eased.Plan.CrotchNotes!, n => n.Contains(why, StringComparison.Ordinal));
    }

    [Fact]
    public void Pants_come_out_as_before()
    {
        // [AC-crotch-leg-weights-3]
        LeftAsBefore(Garment([new(Hips(0.90f)), new(Legs(Knee)), new(Perineum)], stitch: _ => true), "left on the legs");
    }

    [Fact]
    public void Long_shorts_come_out_as_before()
    {
        // [AC-crotch-leg-weights-3] Ten centimetres down the thigh.
        LeftAsBefore(Garment([new(Hips(0.90f)), new(Legs(Crotch - 0.10f)), new(Perineum)], stitch: _ => true),
                     "down the thigh");
    }

    [Fact]
    public void A_catsuit_comes_out_as_before()
    {
        // [AC-crotch-leg-weights-3]
        LeftAsBefore(Garment([new(Hips(Waist)), new(Legs(Knee)), new(Perineum)], stitch: _ => true), "left on the legs");
    }

    [Fact]
    public void Shorts_with_short_legs_stay_on_the_legs()
    {
        // [AC-crotch-leg-weights-3] Five centimetres of leg: not past the drop, but round the thigh all the same.
        LeftAsBefore(Garment([new(Hips(0.90f)), new(Legs(Crotch - 0.05f)), new(Perineum)], stitch: _ => true),
                     "wrapped round a thigh");
    }

    [Fact]
    public void A_stocking_is_untouched()
    {
        // [AC-crotch-leg-weights-4]
        var garment = Garment([new(Leg(Part.LegL, 0.45f, 0.78f))]);
        var eased = Refit(garment);
        AllSame(garment, eased, Refit(garment, ease: false));
        Assert.Equal(0, eased.Plan.CrotchEased);
    }

    [Fact]
    public void A_garter_ring_is_untouched()
    {
        // [AC-crotch-leg-weights-4] [EDGE-crotch-leg-weights-23] Worn with briefs, so the garment does pass between the
        // legs; the ring sits in the field below the crotch, and keeps its thigh.
        var garment = Garment([.. Briefs, new(Leg(Part.LegL, 0.74f, 0.77f))]);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);
        var ring = Where(garment, p => p.Y < 0.775f);
        Assert.NotEmpty(ring);
        Same(eased.Weights, before.Weights, ring);
        Assert.Contains(eased.Plan.CrotchNotes!, n => n.Contains("left on the legs", StringComparison.Ordinal));
        Assert.All(Where(garment, Gusset), v => Assert.True(Legs(eased.Weights[v]) < 0.5f * Legs(before.Weights[v])));
    }

    [Fact]
    public void A_thong_whose_chain_touches_a_garter_ring_is_still_eased()
    {
        // [EDGE-crotch-leg-weights-23] One hop only: the link touching the ring stays with it, the strip touching the
        // link does not.
        Vector3[] link = [new(0.012f, 0.772f, 0f), new(0.02f, 0.797f, 0.003f), new(0.02f, 0.797f, -0.003f)];
        var chain = new SyntheticModel.SurfaceMesh(Cloth, link, [.. link.Select(_ => Vector3.UnitZ)],
            [.. link.Select(_ => Vector2.Zero)], [.. link.Select(_ => ((string, float)[]?)[("j_kosi", 0.5f), ("j_asi_a_l", 0.5f)])],
            [(0u, [0, 1, 2])]);
        var garment = Garment([new(Perineum), new(Leg(Part.LegL, 0.74f, 0.77f))], extra: [chain]);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);
        var at = Positions(garment);
        Assert.All(Where(garment, p => Gusset(p) && p.Y > Crotch - 0.002f),
                   v => Assert.True(Legs(eased.Weights[v]) <= 0.1f * Legs(before.Weights[v]) + Tol,
                                    $"{at[v]}: {string.Join(",", eased.Weights[v])} of {string.Join(",", before.Weights[v])}"));
        Assert.Contains(eased.Plan.CrotchNotes!, n => n.Contains("touches a piece", StringComparison.Ordinal));
    }

    [Fact]
    public void A_skirt_over_panties_is_untouched_and_the_panties_eased()
    {
        // [AC-crotch-leg-weights-5] The skirt rigged to the hips and its own chain, 2 cm out.
        var skirt = new Piece(Hips(0.95f), 0.02f, (_, _) => [("j_kosi", 0.6f), ("j_sk_f_a_l", 0.4f)]);
        var garment = Garment([.. Briefs, skirt]);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);
        Same(eased.Weights, before.Weights, Where(garment, p => new Vector2(p.X / HipRx, p.Z / HipRz).Length() > 1.1f));
        Assert.All(Where(garment, Gusset), v => Assert.True(Legs(eased.Weights[v]) <= 0.1f * Legs(before.Weights[v]) + Tol));
    }

    [Fact]
    public void A_leotard_crotch_is_eased_and_its_body_is_not()
    {
        // [AC-crotch-leg-weights-6]
        var garment = Garment([new(Hips(Waist)), new(Perineum)]);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);
        Assert.All(Where(garment, Gusset), v => Assert.True(Legs(eased.Weights[v]) <= 0.1f * Legs(before.Weights[v]) + Tol));
        Same(eased.Weights, before.Weights, Where(garment, p => p.Y >= Crotch + 0.07f));
    }

    // ── where it never runs ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Within_one_body_mod_nothing_changes()
    {
        // [AC-crotch-leg-weights-7] A rig change within one body mod (thigh physics added) still reweights; never eases.
        var garment = Garment(Briefs);
        var source = Body();
        var target = Body(physics: true);
        var eased = Refit(garment, source: source, target: target, acrossBodies: false);
        AllSame(garment, eased, Refit(garment, ease: false, source: source, target: target, acrossBodies: false));
        Assert.Equal(0, eased.Plan.CrotchEased);
    }

    [Fact]
    public void CrossBodyPassesWithinBody_does_not_turn_the_ease_on()
    {
        // [AC-crotch-leg-weights-7]
        var garment = Garment(Briefs);
        var pairs = new[] { Pair(Body(), Body(physics: true)) };
        var plan = BodyRetarget.WithTuning(new BodyRetarget.Tuning(CrossBodyPassesWithinBody: true),
            () => BodyRetarget.PlanWeights(garment, pairs, acrossBodies: false, before: garment))!;
        Assert.Equal(0, plan.CrotchEased);
        Assert.Null(plan.CrotchNotes);
    }

    [Fact]
    public void Held_parts_keep_their_weights()
    {
        // [AC-crotch-leg-weights-7]
        var garment = Garment(Briefs);
        var held = new HashSet<int>(Where(garment, p => MathF.Abs(p.Y - Crotch) < 0.005f && MathF.Abs(p.X) <= 0.03f));
        var authored = Weights(garment);
        var eased = Refit(garment, held: held);
        Same(eased.Weights, authored, held);
    }

    [Fact]
    public void Positions_are_untouched()
    {
        // [AC-crotch-leg-weights-7]
        var garment = Garment(Briefs);
        Assert.Equal(Positions(Refit(garment, ease: false).Rebuilt), Positions(Refit(garment).Rebuilt));
    }

    [Fact]
    public void The_garment_s_skin_is_never_eased()
    {
        // [EDGE-crotch-leg-weights-13] A garment carrying its own body mesh over the crotch.
        var garment = Garment(Briefs, skin: (part, p) => part == Part.Perineum || part == Part.Hips && p.Y <= 0.85f);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);
        var parts = ModelPartReader.Read(garment)!;
        var skin = parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                        .SelectMany(p => p.Triangles).Distinct().ToList();
        Assert.NotEmpty(skin);
        Same(eased.Weights, before.Weights, skin);
    }

    // ── through the refit ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_logs_the_crotch_anchor_eased_and_vetoed()
    {
        // [AC-crotch-leg-weights-8]
        var garment = Garment([.. Briefs, new(Leg(Part.LegL, 0.74f, 0.77f))]);
        var body = Body();
        var log = new List<string>();
        BodyRetarget.Plan(ModelPartReader.Read(garment)!, garment, [Pair(body, body)], "_dwn", acrossBodies: true,
                          log: log.Add);
        Assert.Contains(log, l => l.StartsWith("crotch: anchored at 800 mm", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("crotch: ", StringComparison.Ordinal) && l.EndsWith("eased off them", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("left on the legs: ", StringComparison.Ordinal) && l.Contains(" mm sideways", StringComparison.Ordinal));
    }

    [Fact]
    public void Auto_refit_eases_the_crotch()
    {
        // [AC-crotch-leg-weights-8] Through the automatic refit, keep-shape pieces and all.
        var garment = Garment(Briefs);
        var body = Body();
        BodyRetarget.Planned Run(bool ease) => BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoCrotchEase: !ease),
            () => RefitCore.PlanRefit(ModelPartReader.Read(garment)!, garment, [Pair(body, body)], "_dwn", across: true,
                                      gameGear: false, out _));
        var eased = Run(true).Model;
        var before = Run(false).Model;
        var we = Weights(eased);
        var wb = Weights(before);
        var gusset = Where(eased, Gusset);
        Assert.NotEmpty(gusset);
        Assert.All(gusset, v => Assert.True(Legs(we[v]) <= 0.1f * Legs(wb[v]) + Tol, $"vertex {v}: {Legs(we[v])} of {Legs(wb[v])}"));
    }

    // ── edge cases ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_garment_s_own_bones_are_kept_exactly()
    {
        // [EDGE-crotch-leg-weights-1] A gusset 1 cm off the skin, past the skin hug, carrying a flap's own bone. The bone
        // is on the midline: KeepToSide, after the ease, already scales a sided own bone as the other leg's, with or
        // without it.
        var gusset = new Piece(Perineum, 0.01f, (part, p) =>
            [.. Bones(part, p).Select(i => (i.Bone, i.W * 0.8f)), ("j_sk_f_a", 0.2f)]);
        var garment = Garment([gusset]);
        var authored = Weights(garment);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);
        // As planned, before the writer trims a vertex to its four slots.
        List<(string Bone, float W)> Planned(BodyRetarget.WeightPlan plan, int v) => plan.For(0)?[v]?.ToList() ?? authored[v];
        var flap = Where(garment, p => MathF.Abs(p.Y - (Crotch - 0.01f)) < 0.002f && p.Z >= 0f && MathF.Abs(p.X) <= 0.03f);
        Assert.NotEmpty(flap);
        foreach (int v in flap)
        {
            Assert.Equal(Bone(Planned(before.Plan, v), "j_sk_f_a"), Bone(Planned(eased.Plan, v), "j_sk_f_a"), 5);
            Assert.True(Legs(eased.Weights[v]) <= 0.1f * Legs(before.Weights[v]) + Tol);
        }
    }

    [Fact]
    public void A_thong_strip_eases_at_the_crotch_and_fades_up_the_back()
    {
        // [EDGE-crotch-leg-weights-2] Front at the crotch: eased; up the back, behind the seat's fade: as before.
        bool Strip(Part part, Vector3 p) => MathF.Abs(p.X) <= 0.012f;
        var garment = Garment([new((part, p) => part == Part.Perineum && Strip(part, p)),
                               new((part, p) => part == Part.Hips && Strip(part, p) && (p.Z > 0 ? p.Y <= 0.86f : p.Y <= 0.95f))]);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);
        Assert.All(Where(garment, Gusset), v => Assert.True(Legs(eased.Weights[v]) <= 0.1f * Legs(before.Weights[v]) + Tol));
        Same(eased.Weights, before.Weights, Where(garment, p => p.Z < -0.05f));
        var back = Where(garment, p => MathF.Abs(p.Y - Crotch) < 0.005f && p.Z < -0.025f && p.Z > -0.035f);
        Assert.All(back, v => Assert.True(Legs(eased.Weights[v]) < Legs(before.Weights[v]) - Tol
                                          && Legs(eased.Weights[v]) > 0.1f * Legs(before.Weights[v]) + Tol));
    }

    [Fact]
    public void Panties_with_a_welded_garter_are_left_on_the_legs()
    {
        // [EDGE-crotch-leg-weights-3] A strap down the outside of the left thigh, sewn on.
        var strap = new Piece((part, p) => part == Part.LegL && p.Y >= 0.60f && p.X > LegX + 0.03f);
        LeftAsBefore(Garment([.. Briefs, strap], stitch: part => part is Part.LegL or Part.Perineum), "down the thigh");
    }

    [Fact]
    public void Two_layers_stay_identical()
    {
        // [EDGE-crotch-leg-weights-4] A lace layer 2 mm over the gusset, weighted as it.
        var garment = Garment([.. Briefs, new(Perineum, 0.003f, OwnMesh: true)]);
        var eased = Refit(garment);
        var at = Positions(garment);
        var outer = Where(garment, p => MathF.Abs(p.Y - (Crotch - 0.003f)) < 0.0005f);
        Assert.NotEmpty(outer);
        foreach (int v in outer)
        {
            int inner = Where(garment, p => MathF.Abs(p.Y - (Crotch - 0.001f)) < 0.0005f
                                            && MathF.Abs(p.X - at[v].X) < 1e-4f && MathF.Abs(p.Z - at[v].Z) < 1e-4f).Single();
            Same(eased.Weights, [.. eased.Weights.Select((_, i) => i == v ? eased.Weights[inner] : eased.Weights[i])], [v]);
        }
    }

    [Fact]
    public void Seam_split_vertices_stay_identical()
    {
        // [EDGE-crotch-leg-weights-5] The gusset in two halves meeting on the midline, its vertices split there.
        var garment = Garment([new((part, p) => part == Part.Perineum && p.X <= 1e-4f),
                               new((part, p) => part == Part.Perineum && p.X >= -1e-4f), new(Hips(0.87f))]);
        var eased = Refit(garment);
        var at = Positions(garment);
        foreach (var twins in Enumerable.Range(0, at.Length).GroupBy(v => (MathF.Round(at[v].X * 1e4f), MathF.Round(at[v].Y * 1e4f),
                                                                             MathF.Round(at[v].Z * 1e4f))).Where(g => g.Count() > 1))
        {
            var first = eased.Weights[twins.First()].OrderBy(i => i.Bone).ToList();
            Assert.All(twins, v => Assert.True(eased.Weights[v].OrderBy(i => i.Bone).SequenceEqual(first)));
        }
    }

    [Fact]
    public void No_crotch_skin_changes_nothing()
    {
        // [EDGE-crotch-leg-weights-6] The new body has no skin where the thighs meet — its crotch cut away, the hips on
        // the hips alone: nothing to anchor on, and it says so.
        var garment = Garment(Briefs);
        var body = Body(crotch: false, perineum: false);
        var eased = Refit(garment, source: body);
        AllSame(garment, eased, Refit(garment, ease: false, source: body));
        Assert.Contains("no crotch to anchor on", eased.Plan.CrotchNotes!);
    }

    [Fact]
    public void A_gown_rigged_to_both_thighs_is_untouched()
    {
        // [EDGE-crotch-leg-weights-7] A front panel hanging between the knees, 5 cm off the legs.
        var p = new List<Vector3>();
        var t = new List<int>();
        for (int r = 0; r <= 6; r++)
            for (int c = 0; c <= 6; c++) p.Add(new Vector3(-0.15f + c * 0.05f, 0.20f + r * 0.10f, 0.12f));
        for (int r = 0; r < 6; r++)
            for (int c = 0; c < 6; c++) { int a = r * 7 + c; t.AddRange([a, a + 1, a + 8, a, a + 8, a + 7]); }
        var pa = p.ToArray();
        var gown = new SyntheticModel.SurfaceMesh(Cloth, pa, [.. pa.Select(_ => Vector3.UnitZ)], [.. pa.Select(_ => Vector2.Zero)],
            [.. pa.Select(_ => ((string, float)[]?)[("j_asi_a_l", 0.5f), ("j_asi_a_r", 0.5f)])], [(0u, t.ToArray())]);
        var garment = Garment([], extra: [gown]);
        var eased = Refit(garment);
        AllSame(garment, eased, Refit(garment, ease: false));
        Assert.Equal(0, eased.Plan.CrotchEased);
    }

    [Fact]
    public void EaseLegs_trims_a_body_bone_never_an_own_bone_at_eight()
    {
        // [EDGE-crotch-leg-weights-8] Eight already, no j_kosi: the hips' share needs a slot, and a tied left/right pair
        // goes together.
        (string, float)[] now = [("own_a", 0.05f), ("own_b", 0.05f), ("own_c", 0.05f), ("own_d", 0.05f),
                                 ("j_asi_a_l", 0.3f), ("j_asi_a_r", 0.3f), ("ya_daitai_phys_l", 0.1f), ("ya_daitai_phys_r", 0.1f)];
        var w = BodyRetarget.EaseLegs(now, 1f, Bones)!;
        Assert.True(w.Length <= 8);
        foreach (var own in new[] { "own_a", "own_b", "own_c", "own_d" }) Assert.Equal(0.05f, w.Single(i => i.Bone == own).W, 5);
        Assert.Equal(1f, w.Sum(i => i.W), 4);
        Assert.Equal(w.Count(i => i.Bone == "ya_daitai_phys_l"), w.Count(i => i.Bone == "ya_daitai_phys_r"));
        Assert.Contains(w, i => i.Bone == "j_kosi");
    }

    [Fact]
    public void EaseLegs_without_kosi_shares_to_other_body_bones()
    {
        // [EDGE-crotch-leg-weights-9]
        var bones = new HashSet<string> { "j_sebo_a", "j_asi_a_l", "j_asi_a_r" };
        var w = BodyRetarget.EaseLegs([("j_sebo_a", 0.4f), ("j_asi_a_l", 0.3f), ("j_asi_a_r", 0.3f)], 1f, bones)!;
        Assert.Equal(0.94f, w.Single(i => i.Bone == "j_sebo_a").W, 3);
        Assert.Equal(0.06f, w.Where(i => i.Bone.StartsWith("j_asi", StringComparison.Ordinal)).Sum(i => i.W), 3);
    }

    [Fact]
    public void EaseLegs_with_nothing_to_give_to_leaves_the_vertex()
    {
        // [EDGE-crotch-leg-weights-9]
        var bones = new HashSet<string> { "j_asi_a_l", "j_asi_a_r" };
        Assert.Null(BodyRetarget.EaseLegs([("j_asi_a_l", 0.5f), ("j_asi_a_r", 0.5f)], 1f, bones));
    }

    [Fact]
    public void A_half_scale_crotch_eases_the_same_vertices()
    {
        // [EDGE-crotch-leg-weights-10] A Lalafell-sized body: every extent halves with it.
        HashSet<int> EasedAt(float scale)
        {
            var garment = Garment(Briefs, scale: scale);
            var body = Body(scale: scale);
            var eased = Refit(garment, source: body);
            var before = Refit(garment, ease: false, source: body);
            return [.. Enumerable.Range(0, eased.Weights.Length)
                                 .Where(v => Legs(eased.Weights[v]) < 0.5f * Legs(before.Weights[v]))];
        }
        var full = EasedAt(1f);
        Assert.NotEmpty(full);
        Assert.Equal(full.OrderBy(v => v), EasedAt(0.5f).OrderBy(v => v));
    }

    [Fact]
    public void Thigh_physics_is_eased_with_the_thigh()
    {
        // [EDGE-crotch-leg-weights-11] On the hips' bottom edge just off the midline, rigged to one thigh and its physics.
        var garment = Garment(Briefs, physics: true);
        var body = Body(physics: true);
        var eased = Refit(garment, source: body);
        var before = Refit(garment, ease: false, source: body);
        var edge = Where(garment, p => MathF.Abs(p.Y - Crotch) < 0.001f && p.Z > 0.05f && MathF.Abs(p.X) > 0.031f
                                       && MathF.Abs(p.X) < 0.045f);
        Assert.NotEmpty(edge);
        Assert.All(edge, v =>
        {
            float was = Bone(before.Weights[v], "ya_daitai_phys_l") + Bone(before.Weights[v], "ya_daitai_phys_r");
            Assert.True(was > 0.05f);
            Assert.True(Bone(eased.Weights[v], "ya_daitai_phys_l") + Bone(eased.Weights[v], "ya_daitai_phys_r") < 0.6f * was);
        });
    }

    [Fact]
    public void Hot_pants_cuffs_keep_the_thigh()
    {
        // [EDGE-crotch-leg-weights-12] A centimetre of cuff, sewn on: the gusset eased, the cuffs out on the thigh not.
        var garment = Garment([new(Hips(0.85f)), new(Legs(Crotch - 0.01f)), new(Perineum)], stitch: _ => true);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);
        Assert.Equal(0, eased.Plan.CrotchVetoed);
        Assert.All(Where(garment, Gusset), v => Assert.True(Legs(eased.Weights[v]) <= 0.1f * Legs(before.Weights[v]) + Tol));
        Same(eased.Weights, before.Weights, Where(garment, p => p.Y < Crotch - 0.001f && MathF.Abs(p.X) >= 0.06f));
    }

    [Fact]
    public void Cloth_beyond_reach_inside_the_box_is_eased_too()
    {
        // [EDGE-crotch-leg-weights-14] A flap 4.5 cm out in front of the crotch, past the bodies' reach.
        var flap = new Piece((part, p) => part == Part.Hips && p.Y <= 0.82f && p.Z > 0.1f && MathF.Abs(p.X) <= 0.03f, 0.045f);
        var garment = Garment([.. Briefs, flap]);
        var authored = Weights(garment);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);
        var out_ = Where(garment, p => p.Z > 0.15f);
        Assert.NotEmpty(out_);
        Same(before.Weights, authored, out_);
        Assert.All(out_, v => Assert.True(Legs(eased.Weights[v]) <= 0.1f * Legs(authored[v]) + Tol));
    }

    [Fact]
    public void A_leaning_gusset_edge_keeps_its_lean()
    {
        // [EDGE-crotch-leg-weights-15]
        var garment = Garment(Briefs);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);
        var edge = Where(garment, p => Gusset(p) && MathF.Abs(p.X - 0.01f) < 0.001f);
        Assert.NotEmpty(edge);
        Assert.All(edge, v =>
        {
            float Lean(List<(string Bone, float W)> w) => Bone(w, "j_asi_a_l") / (Bone(w, "j_asi_a_l") + Bone(w, "j_asi_a_r"));
            Assert.Equal(Lean(before.Weights[v]), Lean(eased.Weights[v]), 1);
        });
    }

    [Fact]
    public void Buttock_and_genital_bones_gain_nothing()
    {
        // [EDGE-crotch-leg-weights-16]
        var bones = new HashSet<string>(Bones) { "iv_kintama" };
        (string, float)[] now = [("j_kosi", 0.2f), ("j_asi_a_l", 0.3f), ("j_asi_a_r", 0.3f), ("iv_shiri_l", 0.1f), ("iv_kintama", 0.1f)];
        var w = BodyRetarget.EaseLegs(now, 1f, bones)!;
        Assert.Equal(0.1f, w.Single(i => i.Bone == "iv_shiri_l").W, 4);
        Assert.Equal(0.1f, w.Single(i => i.Bone == "iv_kintama").W, 4);
        Assert.Equal(0.74f, w.Single(i => i.Bone == "j_kosi").W, 4);
    }

    [Fact]
    public void Two_crotch_pieces_share_one_field()
    {
        // [EDGE-crotch-leg-weights-17] A bikini bottom 6 mm over panties, weighted to the hips more: not one layer.
        var bikini = new Piece(Perineum, 0.006f, (part, p) => [.. Bones(part, p).Select(i => (i.Bone, i.Bone == "j_kosi" ? i.W + 0.2f : i.W * 0.67f))]);
        var garment = Garment([.. Briefs, bikini]);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);
        var both = Where(garment, p => (MathF.Abs(p.Y - (Crotch - 0.001f)) < 0.0005f || MathF.Abs(p.Y - (Crotch - 0.006f)) < 0.0005f)
                                       && p.Z >= 0f && MathF.Abs(p.X) <= 0.03f);
        Assert.Contains(both, v => Positions(garment)[v].Y < Crotch - 0.005f);
        Assert.All(both, v => Assert.True(Legs(eased.Weights[v]) <= 0.1f * Legs(before.Weights[v]) + Tol));
    }

    [Fact]
    public void A_top_without_a_crotch_is_identical()
    {
        // [EDGE-crotch-leg-weights-18]
        var garment = Garment([new((part, p) => part == Part.Hips && p.Y >= 0.90f)]);
        var eased = Refit(garment);
        AllSame(garment, eased, Refit(garment, ease: false));
        Assert.Equal(0, eased.Plan.CrotchEased);
    }

    [Fact]
    public void Thong_plates_and_links_ease_with_the_strip()
    {
        // [EDGE-crotch-leg-weights-19] Plates beside the strip and loose links, none welded to it.
        bool Strip(Part part, Vector3 p) => part == Part.Perineum && MathF.Abs(p.X) <= 0.012f;
        bool Plates(Part part, Vector3 p) => part == Part.Perineum && MathF.Abs(p.X) >= 0.008f;
        var links = new List<SyntheticModel.SurfaceMesh>();
        for (int k = 0; k < 4; k++)
        {
            Vector3[] l = [new(-0.02f + k * 0.012f, Crotch - 0.004f, 0.02f), new(-0.015f + k * 0.012f, Crotch - 0.004f, 0.02f),
                           new(-0.0175f + k * 0.012f, Crotch - 0.004f, 0.025f)];
            links.Add(new(Cloth, l, [.. l.Select(_ => -Vector3.UnitY)], [.. l.Select(_ => Vector2.Zero)],
                          [.. l.Select(p => ((string, float)[]?)Bones(Part.Perineum, p))], [(0u, [0, 1, 2])]));
        }
        var garment = Garment([new(Strip), new(Plates, 0.003f)], extra: links);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);
        var all = Where(garment, p => p.Y < Crotch && p.Y > Crotch - 0.005f && p.Z >= 0f && MathF.Abs(p.X) <= 0.03f);
        Assert.Contains(all, v => Positions(garment)[v].Y < Crotch - 0.0035f);   // the links are in it
        Assert.All(all, v => Assert.True(Legs(eased.Weights[v]) <= 0.1f * Legs(before.Weights[v]) + Tol,
                                         $"vertex {v}: {Legs(eased.Weights[v])} of {Legs(before.Weights[v])}"));
    }

    [Fact]
    public void A_keep_shape_plate_takes_one_strength()
    {
        // [EDGE-crotch-leg-weights-20] A plate on the front of the hips, reaching up through the fade: it eases whole.
        var plate = new Piece((part, p) => part == Part.Hips && p.Z > 0.1f && MathF.Abs(p.X) <= 0.03f && p.Y <= 0.87f, 0.003f);
        var garment = Garment([.. Briefs, plate]);
        var kept = Where(garment, p => p.Z > 0.1115f && MathF.Abs(p.X) <= 0.031f && p.Y <= 0.871f);
        var eased = Refit(garment, keepShape: [kept]);
        var before = Refit(garment, ease: false, keepShape: [kept]);
        var ratios = kept.Where(v => Legs(before.Weights[v]) > 0.1f)
                         .Select(v => Legs(eased.Weights[v]) / Legs(before.Weights[v])).ToList();
        Assert.True(ratios.Count > 3, $"{ratios.Count} of {kept.Length} kept vertices on the legs");
        Assert.True(ratios.Max() - ratios.Min() < 0.05f, $"ratios {ratios.Min()}..{ratios.Max()}");
        Assert.True(ratios.Max() < 0.95f);
    }

    [Fact]
    public void A_pants_crotch_insert_stays_with_its_legs()
    {
        // [EDGE-crotch-leg-weights-21] The insert is its own piece, touching the pant legs at the crotch.
        var garment = Garment([new(Hips(0.90f)), new(Legs(Knee)), new(Perineum)],
                              stitch: part => part is Part.LegL or Part.LegR);
        var eased = Refit(garment);
        AllSame(garment, eased, Refit(garment, ease: false));
        Assert.Contains(eased.Plan.CrotchNotes!, n => n.Contains("touches a piece", StringComparison.Ordinal));
    }

    [Fact]
    public void Full_back_briefs_are_not_vetoed_and_their_seat_is_untouched()
    {
        // [EDGE-crotch-leg-weights-22] The briefs reach 4 cm down the back of the thighs, the underside of the buttocks.
        var underside = new Piece((part, p) => part is Part.LegL or Part.LegR && p.Z < -0.02f && p.Y >= Crotch - 0.04f);
        var garment = Garment([.. Briefs, underside], stitch: part => part is Part.LegL or Part.LegR or Part.Perineum);
        var eased = Refit(garment);
        var before = Refit(garment, ease: false);
        Assert.Equal(0, eased.Plan.CrotchVetoed);
        Same(eased.Weights, before.Weights, Where(garment, p => p.Z <= -0.04f));
        Assert.All(Where(garment, Gusset), v => Assert.True(Legs(eased.Weights[v]) <= 0.1f * Legs(before.Weights[v]) + Tol));
    }

    [Fact]
    public void A_crop_top_hem_with_thigh_traces_is_untouched()
    {
        // [EDGE-crotch-leg-weights-24] Blender's transfer left 5% of each thigh along the hem, 15 cm above the crotch.
        var hem = new Piece((part, p) => part == Part.Hips && p.Y >= 0.95f, Weights: (_, _) =>
            [("j_kosi", 0.9f), ("j_asi_a_l", 0.05f), ("j_asi_a_r", 0.05f)]);
        var garment = Garment([hem]);
        var eased = Refit(garment);
        AllSame(garment, eased, Refit(garment, ease: false));
        Assert.Equal(0, eased.Plan.CrotchEased);
    }

    [Fact]
    public void The_pouch_is_not_the_crotch()
    {
        // [EDGE-crotch-leg-weights-25] A pouch below the crotch, on both thighs, on a material that is not skin.
        Assert.False(SecondSkinWriter.IsBodySkinMaterial(Pouch));
        var body = Body(pouch: true);
        var parts = ModelPartReader.Read(body)!;
        var a = BodyRetarget.FindCrotch([(parts, ModelSkinReader.Read(body, null, null)!)])!.Value;
        Assert.Equal(Crotch, a.Y, 3);
    }
}
