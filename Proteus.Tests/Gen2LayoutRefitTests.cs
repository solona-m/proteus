using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A body in a mirrored layout (gen2) refitted onto one in a doubled layout (bibo): judged by how much of its skin's
/// SURFACE lands, so a dense patch its target has no place for — Tight&amp;Firm Gen2's NSFW crotch, 60% of its legs'
/// vertices on a small share of their surface — follows the skin around it instead of sinking the pair.
/// <para/>
/// The bodies are strips: a coarse "leg" whose gen2 uvs convert onto the target's, and a dense "patch" on top of it, joined
/// at the same positions across a uv cut, whose uvs land where the target has no skin.
/// </summary>
public class Gen2LayoutRefitTests
{
    private const string Gen2 = "/mt_c0201b0001_a.mtrl";
    private const string Bibo = "/mt_c0201b0001_bibo.mtrl";

    private static readonly UVRemapService Remap = new(Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");

    /// <summary>The leg's gen2 uvs: u 0.1..0.5, v 0.1..0.95. On the bibo sheet, +X: u 0.55..0.75.</summary>
    private static readonly (float U0, float U1, float V0, float V1) LegUv = (0.1f, 0.5f, 0.1f, 0.95f);

    /// <summary>The patch's gen2 uvs, which convert to u 0.85..0.975 — where the target has no skin.</summary>
    private static readonly (float U0, float U1, float V0, float V1) PatchUv = (0.7f, 0.95f, 0f, 0.05f);

    [Fact]
    [Trait("Plan", "AC-gen2-legs-refit-2")]
    [Trait("Plan", "AC-gen2-legs-refit-4")]
    public void A_gen2_body_whose_dense_patch_has_no_place_on_bibo_is_paired_by_area()
    {
        var source = Strip(Gen2, side: 1, patchTop: 1.02f);
        var target = Leg(Bibo, side: 1, Move);

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var atlas = Assert.IsType<UvAtlasCorrespondence>(built);
        Assert.True(atlas.Measured.VertexCoverage < 0.8f, $"by count {atlas.Measured.VertexCoverage:P1}");
        Assert.True(atlas.Measured.AreaCoverage >= 0.8f, $"by area {atlas.Measured.AreaCoverage:P1}");
        Assert.Equal(source.Patch.Count, atlas.Measured.Carried);
        Assert.Equal((0, 0), (atlas.Measured.Distrusted, atlas.Measured.Unjudged));
        Assert.Contains("no place on the target's layout", atlas.Describe());
    }

    [Fact]
    [Trait("Plan", "AC-gen2-legs-refit-4")]
    public void The_patch_follows_the_skin_around_it()
    {
        // Sheared and lifted unevenly, so a carry that ignored where the patch is joined could not pass: the leg's top
        // row moves by Move(y = 1), and so must every point of the patch above it.
        var source = Strip(Gen2, side: 1, patchTop: 1.02f);
        var target = Leg(Bibo, side: 1, Move);

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var top = Move(new Vector3(0.1f, 1f, 0f)) - new Vector3(0.1f, 1f, 0f);
        foreach (int v in source.Skin)
            Assert.True(built!.Field[v].HasValue, $"vertex {v} has no displacement");
        foreach (int v in source.Patch)
            Assert.True(Vector3.Distance(built!.Field[v]!.Value, top) < 1e-5f,
                        $"patch vertex {v} moved {built.Field[v]}, the leg's top {top}");
        Assert.Equal(0, Folded(source, built!));
    }

    [Fact]
    [Trait("Plan", "AC-gen2-legs-refit-5")]
    [Trait("Plan", "EDGE-gen2-legs-refit-3")]
    public void A_gen2_body_in_an_unrelated_layout_is_still_refused()
    {
        var source = Strip(Gen2, side: 1, patchTop: 1.02f);
        for (int i = 0; i < source.Uv.Length; i += 2) source.Uv[i] += 0.37f;
        var target = Leg(Bibo, side: 1, Move);

        Assert.False(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out _,
                                                 out string refusal, Remap));
        Assert.Contains("texture layout", refusal);
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-2")]
    public void A_region_with_no_place_over_a_fifth_of_the_skin_is_refused()
    {
        // A patch 0.4 tall on a leg 1.0 tall: 29% of the surface has no place.
        var source = Strip(Gen2, side: 1, patchTop: 1.4f);
        var target = Leg(Bibo, side: 1, Move);

        Assert.False(UvAtlasCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out _,
                                                    out string refusal, out var measured, Gen2ToBibo(), null,
                                                    byArea: true));
        Assert.InRange(measured.AreaCoverage, 0.6f, 0.8f);
        Assert.Equal((0, 0, 0), (measured.Disagreed, measured.Unvouched, measured.Unjudged));   // the guard never ran
        Assert.Contains(string.Format("{0:P0}", measured.AreaCoverage), refusal);
        Assert.Contains("texture layout", refusal);
    }

    [Fact]
    [Trait("Plan", "AC-gen2-legs-refit-6")]
    [Trait("Plan", "EDGE-gen2-legs-refit-13")]
    public void A_pair_today_s_rule_accepts_is_matched_as_before()
    {
        // No patch; three of the leg's points drawn where the target has nothing. Enough lands by count, so the three are
        // left for the refit's own fill, exactly as before, and the description says nothing new.
        var source = Strip(Gen2, side: 1, patchTop: null);
        int[] stray = [source.Skin[1], source.Skin[2], source.Skin[3]];
        foreach (int v in stray) source.Uv[v * 2] = 0.9f;
        var target = Leg(Bibo, side: 1, Move);

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var atlas = Assert.IsType<UvAtlasCorrespondence>(built);
        foreach (int v in stray) Assert.Null(atlas.Field[v]);
        Assert.Equal(0, atlas.Measured.Carried);
        int placeable = source.Skin.Count;
        Assert.Equal($"legs: matched by texture coordinate, {placeable - 3:N0} of {placeable:N0} skin vertices " +
                     $"({(float)(placeable - 3) / placeable:P1})", atlas.Describe());
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-1")]
    public void The_same_patch_on_one_layout_is_still_judged_by_vertex()
    {
        // Both bodies bibo: no conversion, so no judging by surface — the dense patch sinks the pair as it always did.
        var source = Strip(Bibo, side: 1, patchTop: 1.02f, converted: true);
        var target = Leg(Bibo, side: 1, Move);

        Assert.False(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out _,
                                                 out string refusal, Remap));
        Assert.Contains("texture layout", refusal);
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-4")]
    public void A_patch_on_the_mirrored_side_follows_its_own_side()
    {
        // Both sides share one gen2 sheet; the target moves them apart. Each patch takes its own side's move.
        var source = Join(Strip(Gen2, side: 1, patchTop: 1.02f), Strip(Gen2, side: -1, patchTop: 1.02f));
        var lift = new Vector3(0f, 0.03f, 0f);
        var target = Join(Leg(Bibo, side: 1, p => p + new Vector3(0.01f, 0f, 0f)),
                          Leg(Bibo, side: -1, p => p + lift));

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        foreach (int v in source.Patch)
        {
            var want = At(source.Model, v).X > 0 ? new Vector3(0.01f, 0f, 0f) : lift;
            Assert.True(Vector3.Distance(built!.Field[v]!.Value, want) < 1e-5f,
                        $"patch vertex {v} at {At(source.Model, v)} moved {built.Field[v]}, its side {want}");
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-5")]
    public void An_unplaced_island_joined_to_nothing_takes_its_nearest_placed_point()
    {
        // The patch floats 0.1 above the leg, joined to it by nothing.
        var source = Strip(Gen2, side: 1, patchTop: 1.12f, patchGap: 0.1f);
        var target = Leg(Bibo, side: 1, Move);

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var placed = source.Skin.Except(source.Patch).ToList();
        foreach (int v in source.Patch)
        {
            int nearest = placed.MinBy(m => Vector3.DistanceSquared(At(source.Model, m), At(source.Model, v)));
            Assert.True(Vector3.Distance(built!.Field[nearest]!.Value, built.Field[v]!.Value) < 1e-6f,
                        $"vertex {v} moved {built.Field[v]}, its nearest placed point {built.Field[nearest]}");
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-6")]
    public void Off_sheet_islands_and_unplaced_misses_are_carried_together()
    {
        // A third island, drawn off the sheet (u > 0.96 here), beside the patch: kept out of both coverages, and carried.
        var source = Strip(Gen2, side: 1, patchTop: 1.02f, offSheetTop: -0.02f);
        var target = Leg(Bibo, side: 1, Move);
        UVRemapService.UvConversion onSheet = (u, v, side) => u > 0.96f ? null : (u, v);

        Assert.True(UvAtlasCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs",
                                                   out var built, out string refusal, out var measured, Gen2ToBibo(),
                                                   onSheet, byArea: true), refusal);
        Assert.Equal(source.OffSheet.Count, measured.OffSheet);
        Assert.Equal(0, measured.Distrusted);
        Assert.Equal(source.Skin.Count - source.OffSheet.Count, measured.Placeable);
        foreach (int v in source.Patch.Concat(source.OffSheet))
            Assert.True(built!.Field[v].HasValue, $"vertex {v} was not carried");
        Assert.Contains("drawn off the texture layout", built!.Describe());
        Assert.Contains("no place on the target's layout", built.Describe());

        // Mostly off its sheet, it is still refused.
        var mostly = Strip(Gen2, side: 1, patchTop: 1.02f, offSheetTop: -0.3f, offSheetCells: 200);
        Assert.False(UvAtlasCorrespondence.TryBuild(mostly.Model, mostly.Uv, target.Model, target.Uv, "legs", out _,
                                                    out refusal, out _, Gen2ToBibo(), onSheet, byArea: true));
        Assert.Contains("outside its texture layout", refusal);
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-7")]
    public void A_man_s_vanilla_body_onto_TBSE_carries_its_patch_too()
    {
        var source = Strip("/mt_c0101b0001_a.mtrl", side: 1, patchTop: 1.02f);
        var target = Leg("/mt_c0101b0001_b.mtrl", side: 1, Move);

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap, male: true), refusal);
        Assert.Equal(source.Patch.Count, Assert.IsType<UvAtlasCorrespondence>(built).Measured.Carried);
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-8")]
    public void Without_the_uv_service_a_gen2_pair_is_refused_as_before()
    {
        var source = Strip(Gen2, side: 1, patchTop: 1.02f);
        var target = Leg(Bibo, side: 1, Move);

        Assert.False(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out _,
                                                 out string refusal, uvRemap: null));
        Assert.Contains("no map", refusal);
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-9")]
    public void A_body_whose_skin_has_no_area_is_refused()
    {
        // Three points on a line, drawn where the target has nothing: nothing lands, and there is no surface to weigh.
        float[] pos = [0.05f, 0.5f, 0f, 0.1f, 0.5f, 0f, 0.15f, 0.5f, 0f];
        float[] uv = [0.9f, 0.5f, 0.92f, 0.5f, 0.94f, 0.5f];
        var source = Model(pos, [0, 1, 2], Gen2);
        var target = Leg(Bibo, side: 1, Move);

        Assert.False(BodyCorrespondence.TryBuild(source, uv, target.Model, target.Uv, "legs", out _, out string refusal,
                                                 Remap));
        Assert.Contains("texture layout", refusal);
    }

    // ── the coherence guard ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-gen2-legs-refit-8")]
    public void A_landing_far_from_its_neighbours_is_not_trusted()
    {
        var source = Strip(Gen2, side: 1, patchTop: 1.02f);
        int v = LegVertex(2, 5);
        Lift(source, v, 0.15f);
        var target = Leg(Bibo, side: 1, Move);

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var atlas = Assert.IsType<UvAtlasCorrespondence>(built);
        Assert.Equal((1, 0), (atlas.Measured.Disagreed, atlas.Measured.Unvouched));
        var p = At(source.Model, v);
        Assert.True(Vector3.Distance(atlas.Field[v]!.Value, Move(p) - p) < 0.002f, $"it moved {atlas.Field[v]}");
        Assert.Contains("whose landing disagreed with the skin around them", atlas.Describe());
        Assert.Equal(0, Folded(source, atlas));
    }

    [Fact]
    [Trait("Plan", "AC-gen2-legs-refit-9")]
    [Trait("Plan", "EDGE-gen2-legs-refit-27")]
    public void Landings_the_guard_sets_aside_count_against_the_surface()
    {
        // A patch tall enough that 82% of the surface lands; three wrong landings, three rows apart, worth more than
        // the two points above the bar.
        var source = Strip(Gen2, side: 1, patchTop: 1.22f);
        int[] wrong = [LegVertex(2, 2), LegVertex(2, 5), LegVertex(2, 8)];
        foreach (int v in wrong) Lift(source, v, 0.15f);
        var target = Leg(Bibo, side: 1, Move);

        Assert.False(UvAtlasCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out _,
                                                    out string refusal, out var measured, Gen2ToBibo(), null,
                                                    byArea: true));
        Assert.InRange(measured.RawAreaCoverage, 0.81f, 0.83f);
        var weight = AreaWeights(source.Model);
        float placeable = source.Skin.Sum(v => weight[v]);
        Assert.True(wrong.Sum(v => weight[v]) / placeable >= measured.RawAreaCoverage - 0.78f);
        Assert.True(measured.RawAreaCoverage >= 0.8f && measured.AreaCoverage < 0.8f,
                    $"{measured.RawAreaCoverage:P1} -> {measured.AreaCoverage:P1}");
        Assert.Equal(3, measured.Disagreed);
        Assert.Contains(string.Format("{0:P0}", measured.AreaCoverage), refusal);
        Assert.Contains("texture layout", refusal);
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-14")]
    public void Wrong_landings_that_vouch_for_each_other_are_not_trusted()
    {
        // A uv seam down the leg: its two sides are welded twins. Both twins at mid-height land 0.15 m off, and so does
        // the point beside them: the twins are one position and never vouch for themselves, so all three are outvoted.
        var source = SeamStrip(Gen2);
        int[] wrong = [SeamA(5), SeamB(0, 5), SeamB(1, 5)];
        foreach (int v in wrong) Lift(source, v, 0.15f);
        var target = Leg(Bibo, side: 1, Move);

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var atlas = Assert.IsType<UvAtlasCorrespondence>(built);
        Assert.Equal(3, atlas.Measured.Disagreed);
        foreach (int v in wrong)
        {
            var p = At(source.Model, v);
            Assert.True(Vector3.Distance(atlas.Field[v]!.Value, Move(p) - p) < 0.002f, $"vertex {v} moved {atlas.Field[v]}");
        }
        Assert.Equal(0, Folded(source, atlas));
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-15")]
    public void A_wrong_landing_on_the_midline_is_not_trusted()
    {
        // Both sides, built out from the midline and welded there; the -X side's midline is at x = -0, which the
        // conversion takes for +X — where T&F's crotch midline landed wrong on Bibo+.
        var source = Join(Strip(Gen2, side: 1, patchTop: 1.02f, x0: 0f), Strip(Gen2, side: -1, patchTop: 1.02f, x0: 0f));
        int legs = source.Model.Positions.Length / 6;
        int v = legs + LegVertex(0, 5);
        Assert.True(source.Model.Positions[v * 3] == 0f && float.IsNegative(source.Model.Positions[v * 3]));
        Lift(source, v, 0.15f);
        var target = Join(Leg(Bibo, side: 1, Move, x0: 0f), Leg(Bibo, side: -1, Move, x0: 0f));

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var atlas = Assert.IsType<UvAtlasCorrespondence>(built);
        Assert.True(atlas.Measured.Disagreed >= 1);
        var p = At(source.Model, v);
        Assert.True(Vector3.Distance(atlas.Field[v]!.Value, Move(p) - p) < 0.002f, $"it moved {atlas.Field[v]}");
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-16")]
    public void A_landing_amid_skin_with_no_place_is_carried()
    {
        // One point of the patch, five rows up, lands — on the leg's top row, where its uv says. Nothing around it
        // landed, so nothing can vouch for it: it follows the patch.
        var source = Strip(Gen2, side: 1, patchTop: 1.02f);
        int v = source.Patch[5 * 31 + 15];
        int top = LegVertex(2, 10);
        source.Uv[v * 2] = source.Uv[top * 2];
        source.Uv[v * 2 + 1] = source.Uv[top * 2 + 1];
        var target = Leg(Bibo, side: 1, Move);

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var atlas = Assert.IsType<UvAtlasCorrespondence>(built);
        Assert.Equal((0, 1), (atlas.Measured.Disagreed, atlas.Measured.Unvouched));
        Assert.Contains("that landed amid skin with no place", atlas.Describe());
        Assert.True(Vector3.Distance(atlas.Field[v]!.Value, TopMove) < 1e-5f, $"it moved {atlas.Field[v]}");
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-17")]
    public void A_distrusted_landing_never_feeds_the_carry()
    {
        // The leg's top row, where the patch is joined, lands 0.15 m low at one point: the patch takes the skin's move
        // around it, nowhere near the spike.
        var source = Strip(Gen2, side: 1, patchTop: 1.02f);
        int v = LegVertex(2, 10);
        Lift(source, v, -0.15f);
        var target = Leg(Bibo, side: 1, Move);

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var atlas = Assert.IsType<UvAtlasCorrespondence>(built);
        Assert.True(atlas.Measured.Disagreed >= 1);
        foreach (int w in source.Patch)
            Assert.True(Vector3.Distance(atlas.Field[w]!.Value, TopMove) < 0.002f, $"patch vertex {w} moved {atlas.Field[w]}");
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-18")]
    public void A_large_smooth_difference_is_trusted()
    {
        var source = Strip(Gen2, side: 1, patchTop: 1.02f);
        var target = Leg(Bibo, side: 1, p => p + new Vector3(0.06f * p.Y, 0.01f, 0.02f * p.Y));

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var m = Assert.IsType<UvAtlasCorrespondence>(built).Measured;
        Assert.Equal((0, 0), (m.Distrusted, m.Unjudged));
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-23")]
    public void Sparse_skin_is_never_distrusted_for_being_sparse()
    {
        // Cells 60 × 125 mm, as T&F's shin: few neighbours, none within 40 mm, every one of them still a voucher.
        var source = Strip(Gen2, side: 1, patchTop: 1.02f, nx: 3, ny: 8);
        var target = Leg(Bibo, side: 1, p => new Vector3(0.11f + (p.X - 0.11f) * 1.15f, p.Y * 1.05f + 0.01f, p.Z));

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var m = Assert.IsType<UvAtlasCorrespondence>(built).Measured;
        Assert.Equal((0, 0, 0), (m.Disagreed, m.Unvouched, m.Unjudged));
        Assert.Equal(m.RawAreaCoverage, m.AreaCoverage);
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-24")]
    public void Welded_twins_vote_once()
    {
        var source = SeamStrip(Gen2);
        var nodes = UvAtlasCorrespondence.SkinNodes.Build(source.Model, source.Skin);
        int twins = nodes.NodeOf[SeamA(5)];
        Assert.Equal(twins, nodes.NodeOf[SeamB(0, 5)]);
        Assert.Equal(2, nodes.Members[twins].Count);
        var beside = nodes.Near[nodes.NodeOf[SeamB(1, 5)]];
        Assert.Equal(1, beside.Count(n => n.Node == twins));

        // Both twins land off: they are set aside, and the point beside them — one wrong voice among its vouchers — is not.
        Lift(source, SeamA(5), 0.15f);
        Lift(source, SeamB(0, 5), 0.15f);
        var target = Leg(Bibo, side: 1, Move);
        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var atlas = Assert.IsType<UvAtlasCorrespondence>(built);
        Assert.Equal(2, atlas.Measured.Disagreed);
        int b = SeamB(1, 5);
        var p = At(source.Model, b);
        Assert.True(Vector3.Distance(atlas.Field[b]!.Value, Move(p) - p) < 1e-5f, $"vertex {b} moved {atlas.Field[b]}");
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-25")]
    public void A_lone_landed_island_is_kept()
    {
        // A triangle of skin of its own, a millimetre in front of the leg, landing where it should: too small to judge,
        // and kept.
        var strip = Strip(Gen2, side: 1, patchTop: 1.02f);
        Vector3[] corners = [new(0.05f, 0.45f, 0.001f), new(0.08f, 0.45f, 0.001f), new(0.05f, 0.52f, 0.001f)];
        float U(Vector3 c) => LegUv.U0 + (LegUv.U1 - LegUv.U0) * (c.X - 0.02f) / 0.18f;
        float V(Vector3 c) => LegUv.V0 + (LegUv.V1 - LegUv.V0) * c.Y;
        int first = strip.Model.Positions.Length / 3;
        var source = Join(strip, new Body(Model(corners.SelectMany(c => new[] { c.X, c.Y, c.Z }).ToArray(), [0, 1, 2], Gen2),
                                          corners.SelectMany(c => new[] { U(c), V(c) }).ToArray(), [0, 1, 2], [], []));
        var target = Leg(Bibo, side: 1, Move);

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var atlas = Assert.IsType<UvAtlasCorrespondence>(built);
        Assert.Equal((3, 0), (atlas.Measured.Unjudged, atlas.Measured.Distrusted));
        for (int k = 0; k < 3; k++)
        {
            var c = corners[k];
            var want = Move(c with { Z = 0f }) - c;
            // Within the target's facets' flattening of the curved move.
            Assert.True(Vector3.Distance(atlas.Field[first + k]!.Value, want) < 1e-4f, $"corner {k} moved {atlas.Field[first + k]}");
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-26")]
    public void Mirrored_moves_across_the_midline_are_trusted()
    {
        var source = Join(Strip(Gen2, side: 1, patchTop: 1.02f, x0: 0f), Strip(Gen2, side: -1, patchTop: 1.02f, x0: 0f));
        Func<Vector3, Vector3> widen = p => new Vector3(p.X * 1.2f, p.Y + 0.01f, p.Z);
        var target = Join(Leg(Bibo, side: 1, widen, x0: 0f), Leg(Bibo, side: -1, widen, x0: 0f));

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        Assert.Equal(0, Assert.IsType<UvAtlasCorrespondence>(built).Measured.Distrusted);
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-19")]
    [Trait("Plan", "AC-gen2-legs-refit-6")]
    public void The_guard_leaves_pairs_today_s_rule_accepts_alone()
    {
        // No patch: enough lands by count, so the pair is matched as it always was — a wrong landing included. A leg cut
        // in three columns, not the target's four, so the two are matched by uv rather than vertex for vertex.
        foreach (var (material, converted) in new[] { (Gen2, false), (Bibo, true) })
        {
            var source = Strip(material, side: 1, patchTop: null, converted: converted, nx: 3);
            int v = LegVertex(1, 5, nx: 3);
            Lift(source, v, 0.15f);
            var target = Leg(Bibo, side: 1, Move);

            Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                    out string refusal, Remap), refusal);
            var atlas = Assert.IsType<UvAtlasCorrespondence>(built);
            var m = atlas.Measured;
            Assert.Equal((0, 0, 0, 0), (m.Disagreed, m.Unvouched, m.Unjudged, m.Carried));
            var p = At(source.Model, v);
            var spike = Move(p + new Vector3(0f, 0.15f, 0f)) - p;
            Assert.True(Vector3.Distance(atlas.Field[v]!.Value, spike) < 1e-4f, $"{material}: it moved {atlas.Field[v]}");
            Assert.DoesNotContain("disagreed", atlas.Describe());
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-22")]
    public void The_guard_does_not_depend_on_vertex_order()
    {
        var lifted = Strip(Gen2, side: 1, patchTop: 1.02f);
        Lift(lifted, LegVertex(2, 5), 0.15f);
        var seam = SeamStrip(Gen2);
        foreach (int v in new[] { SeamA(5), SeamB(0, 5), SeamB(1, 5) }) Lift(seam, v, 0.15f);
        var target = Leg(Bibo, side: 1, Move);

        foreach (var source in new[] { lifted, seam })
        {
            var reversed = Reversed(source, out var newIndexOf);
            Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var a,
                                                    out string ra, Remap), ra);
            Assert.True(BodyCorrespondence.TryBuild(reversed.Model, reversed.Uv, target.Model, target.Uv, "legs", out var b,
                                                    out string rb, Remap), rb);
            var ma = Assert.IsType<UvAtlasCorrespondence>(a).Measured;
            var mb = Assert.IsType<UvAtlasCorrespondence>(b).Measured;
            Assert.Equal((ma.Disagreed, ma.Unvouched, ma.Unjudged), (mb.Disagreed, mb.Unvouched, mb.Unjudged));
            for (int v = 0; v < newIndexOf.Length; v++)
                Assert.True(Vector3.Distance(a!.Field[v]!.Value, b!.Field[newIndexOf[v]]!.Value) < 1e-6f, $"vertex {v} differs");
        }
    }

    /// <summary>What the target does to the leg's top row (y = 1), where the patch is joined.</summary>
    private static Vector3 TopMove => Move(new Vector3(0.1f, 1f, 0f)) - new Vector3(0.1f, 1f, 0f);

    /// <summary>The leg's vertex at column <paramref name="i"/>, row <paramref name="j"/> (the leg is built first).</summary>
    private static int LegVertex(int i, int j, int nx = 4) => j * (nx + 1) + i;

    /// <summary>Gives a vertex a uv that lands <paramref name="dy"/> higher up the target's leg than it belongs.</summary>
    private static void Lift(Body body, int v, float dy) => body.Uv[v * 2 + 1] += dy * (LegUv.V1 - LegUv.V0);

    /// <summary>
    /// The leg as two grids with a uv seam between them at x = 0.11: the column there is a pair of welded twins, one on
    /// each side. The patch on top, as <see cref="Strip"/>'s.
    /// </summary>
    private static Body SeamStrip(string material)
    {
        var b = new Builder(1);
        b.Grid(0f, 1f, 2, 10, (0.1f, 0.3f, LegUv.V0, LegUv.V1), converted: false, xa: 0.02f, xb: 0.11f);
        b.Grid(0f, 1f, 2, 10, (0.3f, 0.5f, LegUv.V0, LegUv.V1), converted: false, xa: 0.11f, xb: 0.2f);
        var patch = b.Grid(1f, 1.02f, 30, 10, PatchUv, converted: false);
        var model = Model(b.Positions.ToArray(), b.Triangles.ToArray(), material);
        return new Body(model, b.Uvs.ToArray(), Enumerable.Range(0, b.Positions.Count / 3).ToList(), patch, []);
    }

    /// <summary>The seam's twin on the first grid's side, row <paramref name="j"/>.</summary>
    private static int SeamA(int j) => j * 3 + 2;

    /// <summary>The second grid's vertex at column <paramref name="i"/> (0 = the seam), row <paramref name="j"/>.</summary>
    private static int SeamB(int i, int j) => 33 + j * 3 + i;

    /// <summary>Each skin vertex's third of the area of the triangles it is a corner of.</summary>
    private static float[] AreaWeights(ModelParts m)
    {
        var w = new float[m.Positions.Length / 3];
        var t = m.Parts[0].Triangles;
        for (int k = 0; k + 2 < t.Length; k += 3)
        {
            float third = Vector3.Cross(At(m, t[k + 1]) - At(m, t[k]), At(m, t[k + 2]) - At(m, t[k])).Length() / 6f;
            w[t[k]] += third;
            w[t[k + 1]] += third;
            w[t[k + 2]] += third;
        }
        return w;
    }

    /// <summary>The same body with its vertices numbered backwards; <paramref name="newIndexOf"/> maps old to new.</summary>
    private static Body Reversed(Body body, out int[] newIndexOf)
    {
        int vc = body.Model.Positions.Length / 3;
        newIndexOf = Enumerable.Range(0, vc).Select(v => vc - 1 - v).ToArray();
        var pos = new float[vc * 3];
        var uv = new float[vc * 2];
        for (int v = 0; v < vc; v++)
        {
            Array.Copy(body.Model.Positions, v * 3, pos, newIndexOf[v] * 3, 3);
            Array.Copy(body.Uv, v * 2, uv, newIndexOf[v] * 2, 2);
        }
        var map = newIndexOf;
        var tris = body.Model.Parts[0].Triangles.Select(i => map[i]).ToArray();
        return new Body(Model(pos, tris, body.Model.Parts[0].Material), uv, body.Skin.Select(i => map[i]).ToList(),
                        body.Patch.Select(i => map[i]).ToList(), body.OffSheet.Select(i => map[i]).ToList());
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>How the target body differs from the source: sheared by height and lifted.</summary>
    private static Vector3 Move(Vector3 p) => p + new Vector3(0.02f * p.Y, 0.01f + 0.005f * p.Y * p.Y, 0f);

    private static UVRemapService.UvConversion Gen2ToBibo()
        => (u, v, side) => (side < 0 ? 0.5f - u * 0.5f : 0.5f + u * 0.5f, v);

    private sealed record Body(ModelParts Model, float[] Uv, List<int> Skin, List<int> Patch, List<int> OffSheet);

    /// <summary>
    /// A leg 1.0 tall on one side of the body, its gen2 uvs converting onto the target's, with a dense patch on top
    /// (to <paramref name="patchTop"/>; none when null) whose uvs have no place there.
    /// </summary>
    /// <param name="converted">The uvs are already in the bibo layout, for a source in that layout.</param>
    /// <param name="patchGap">How far above the leg the patch starts; 0 joins it to the leg's top row by position.</param>
    /// <param name="offSheetTop">Adds an off-sheet island below the leg, down to this height; null for none.</param>
    private static Body Strip(string material, int side, float? patchTop, bool converted = false, float patchGap = 0f,
                              float? offSheetTop = null, int offSheetCells = 4, int nx = 4, int ny = 10, float x0 = 0.02f)
    {
        var b = new Builder(side, x0: x0);
        b.Grid(0f, 1f, nx, ny, LegUv, converted);
        var patch = patchTop is { } t ? b.Grid(1f + patchGap, t, 30, 10, PatchUv, converted) : [];
        var off = offSheetTop is { } o ? b.Grid(o, 0f, offSheetCells, 2, (0.97f, 0.99f, 0f, 0.05f), converted: true,
                                                rawU: true) : [];
        var model = Model(b.Positions.ToArray(), b.Triangles.ToArray(), material);
        return new Body(model, b.Uvs.ToArray(), Enumerable.Range(0, b.Positions.Count / 3).ToList(), patch, off);
    }

    /// <summary>The target: the leg alone, moved by <paramref name="move"/>, its uvs on the doubled sheet.</summary>
    private static Body Leg(string material, int side, Func<Vector3, Vector3> move, float x0 = 0.02f)
    {
        var b = new Builder(side, move, x0);
        b.Grid(0f, 1f, 4, 10, LegUv, converted: true);
        var model = Model(b.Positions.ToArray(), b.Triangles.ToArray(), material);
        return new Body(model, b.Uvs.ToArray(), Enumerable.Range(0, b.Positions.Count / 3).ToList(), [], []);
    }

    private static Body Join(Body a, Body b)
    {
        int offset = a.Model.Positions.Length / 3;
        var model = Model(a.Model.Positions.Concat(b.Model.Positions).ToArray(),
                          a.Model.Parts[0].Triangles.Concat(b.Model.Parts[0].Triangles.Select(i => i + offset)).ToArray(),
                          a.Model.Parts[0].Material);
        return new Body(model, a.Uv.Concat(b.Uv).ToArray(), a.Skin.Concat(b.Skin.Select(i => i + offset)).ToList(),
                        a.Patch.Concat(b.Patch.Select(i => i + offset)).ToList(),
                        a.OffSheet.Concat(b.OffSheet.Select(i => i + offset)).ToList());
    }

    private sealed class Builder(int side, Func<Vector3, Vector3>? move = null, float x0 = 0.02f)
    {
        public readonly List<float> Positions = [];
        public readonly List<float> Uvs = [];
        public readonly List<int> Triangles = [];

        /// <summary>A grid of quads across x0..0.2 (mirrored for -X) and the given heights; its vertex indices.</summary>
        /// <param name="xa">Where this grid starts across, instead of the builder's x0.</param>
        /// <param name="xb">Where it ends, instead of 0.2.</param>
        public List<int> Grid(float y0, float y1, int nx, int ny, (float U0, float U1, float V0, float V1) uv,
                              bool converted, bool rawU = false, float? xa = null, float? xb = null)
        {
            int first = Positions.Count / 3;
            var made = new List<int>();
            float from = xa ?? x0, to = xb ?? 0.2f;
            for (int j = 0; j <= ny; j++)
            for (int i = 0; i <= nx; i++)
            {
                var p = new Vector3(side * (from + (to - from) * i / nx), y0 + (y1 - y0) * j / ny, 0f);
                if (move != null) p = move(p);
                Positions.AddRange([p.X, p.Y, p.Z]);
                float u = uv.U0 + (uv.U1 - uv.U0) * i / nx, v = uv.V0 + (uv.V1 - uv.V0) * j / ny;
                // On the doubled sheet the +X side is the right half, the -X side its mirror.
                if (converted && !rawU) u = side < 0 ? 0.5f - u * 0.5f : 0.5f + u * 0.5f;
                Uvs.AddRange([u, v]);
                made.Add(Positions.Count / 3 - 1);
            }
            for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int a = first + j * (nx + 1) + i, b = a + 1, c = a + nx + 1, d = c + 1;
                if (side > 0) Triangles.AddRange([a, b, d, a, d, c]);
                else Triangles.AddRange([a, d, b, a, c, d]);
            }
            return made;
        }
    }

    private static ModelParts Model(float[] pos, int[] tris, string material)
    {
        int vc = pos.Length / 3;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int v = 0; v < vc; v++)
        {
            min = Vector3.Min(min, At(pos, v));
            max = Vector3.Max(max, At(pos, v));
        }
        var nrm = new float[pos.Length];
        for (int v = 0; v < vc; v++) nrm[v * 3 + 2] = 1f;

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

    private static Vector3 At(float[] pos, int v) => new(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);

    private static Vector3 At(ModelParts m, int v) => At(m.Positions, v);

    /// <summary>The source's skin triangles that turn over once the field is applied.</summary>
    private static int Folded(Body source, IBodyCorrespondence built)
    {
        Vector3 Moved(int v) => At(source.Model, v) + (built.Field[v] ?? Vector3.Zero);
        var tris = source.Model.Parts[0].Triangles;
        int folded = 0;
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            var before = Vector3.Cross(At(source.Model, b) - At(source.Model, a), At(source.Model, c) - At(source.Model, a));
            var after = Vector3.Cross(Moved(b) - Moved(a), Moved(c) - Moved(a));
            if (before.LengthSquared() > 0f && Vector3.Dot(before, after) <= 0f) folded++;
        }
        return folded;
    }
}
