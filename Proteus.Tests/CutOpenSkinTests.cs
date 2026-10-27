using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.SkinTuckSheet;

namespace Proteus.Tests;

/// <summary>
/// The skin of a slot the automatic refit cuts only because the garment reaches onto it, drawn where the author's own
/// skin spans the new body (see <see cref="BodyRetarget.GrowSpannedOpenSkin"/>): <see cref="SkinTuckSheet.ReachingDress"/>,
/// its legs dipping under the dress's coarse skin as Muse+'s gluteal fold dips under LINXX's Codeine.
/// </summary>
public class CutOpenSkinTests
{
    private static readonly BodyRetarget.Tuning Before = new() { NoCutOpenSkin = true };

    private sealed record Swapped(byte[] Model, BodyRetarget.SwapReport Report);

    /// <summary>The dress's skin swapped for its two bodies, as the automatic refit's rebuild does it: the hidden-skin cut
    /// off, the legs cut only because the dress reaches onto them.</summary>
    private static Swapped Swap((byte[] Garment, byte[] Chest, byte[] Legs) dress, BodyRetarget.Tuning? tuning = null,
                                bool cutHidden = false, bool openSkin = true, IReadOnlySet<string>? hidden = null,
                                bool carryPiercings = false)
    {
        var pairs = new[] { Resized(dress.Chest, "_top"), Resized(dress.Legs, "_dwn", hidden) };
        BodyRetarget.SwapReport report = default;
        var model = BodyRetarget.WithTuning(tuning ?? new BodyRetarget.Tuning(), () => BodyRetarget.Rebuild(
            dress.Garment, pairs, swapSkin: true, weights: null, out report, cutHidden: cutHidden,
            carryPiercings: carryPiercings, openSkin: openSkin));
        Assert.NotNull(model);
        return new Swapped(model!, report);
    }

    /// <summary>The swap ran and the plain cut took something: the legs were claimed and cut, so a test of the pass is a
    /// test of something.</summary>
    private static void SwappedAndCut(Swapped before)
    {
        Assert.True(before.Report.Removed > 0, $"nothing was swapped: {before.Report}");
        Assert.True(before.Report.Cut > 0, "the plain cut took nothing: the legs were not cut");
        Assert.Contains(DrawnRows(before.Model).Keys, r => r < Waist);   // the legs' skin went in
    }

    private static Dictionary<int, int> Below(byte[] model, int row)
        => DrawnRows(model).Where(e => e.Key < row).ToDictionary(e => e.Key, e => e.Value);

    [Fact]
    [Trait("Plan", "AC-cut-open-skin-1")]
    public void A_dress_reaching_onto_the_legs_draws_a_dip_its_skin_spans()
    {
        var dress = ReachingDress();
        var before = Swap(dress, Before);
        var now = Swap(dress);
        SwappedAndCut(before);
        // The reported hole, synthetically: the plain cut leaves the dip out, though the author drew skin across it.
        Assert.False(DrawsDip(before.Model));
        Assert.True(DrawsDip(now.Model));
        Assert.True(now.Report.Spanned > 0);
        Assert.Equal(0, before.Report.Spanned);
        Assert.Equal(before.Report.Cut - now.Report.Spanned, now.Report.Cut);
    }

    [Fact]
    [Trait("Plan", "AC-cut-open-skin-4")]
    [Trait("Plan", "EDGE-cut-open-skin-3")]
    public void A_dip_just_inside_the_authors_edge_draws_no_ring_past_it()
    {
        var dress = ReachingDress(dipRows: (Hem + 1, Hem + 2));
        var before = Swap(dress, Before);
        var now = Swap(dress);
        SwappedAndCut(before);
        Assert.True(now.Report.Spanned > 0, "the dip inside the edge is drawn");
        // Nothing past the author's edge: the rows below the hem draw what the plain cut drew there.
        Assert.Equal(Below(before.Model, Hem), Below(now.Model, Hem));
    }

    [Fact]
    [Trait("Plan", "AC-cut-open-skin-4")]
    public void The_legs_below_the_authors_edge_are_still_cut()
    {
        var dress = ReachingDress(dip: 0f);
        var before = Swap(dress, Before);
        var now = Swap(dress);
        SwappedAndCut(before);
        Assert.Equal(0, now.Report.Spanned);
        Assert.Equal(before.Model, now.Model);
        // Below the hem only the rim row the cut draws through its corners on the hem: row 0 is not drawn.
        Assert.DoesNotContain(0, DrawnRows(now.Model).Keys);
    }

    [Fact]
    [Trait("Plan", "AC-cut-open-skin-4")]
    [Trait("Plan", "EDGE-cut-open-skin-4")]
    public void A_facing_sheet_brings_nothing_into_a_deleted_region()
    {
        var dress = ReachingDress(dip: 0f, facingSheet: true);
        var before = Swap(dress, Before);
        var now = Swap(dress);
        var anyFacing = Swap(dress, new BodyRetarget.Tuning { CutOpenSkinAnyFacing = true });
        SwappedAndCut(before);
        Assert.Equal(before.Model, now.Model);
        // Not for want of reach: without the facing guard the other thigh's skin lets the deleted rows in.
        Assert.True(anyFacing.Report.Spanned > 0, "the facing sheet's landings are not spanned: the test checks nothing");
        Assert.True(Below(anyFacing.Model, Hem).Values.Sum() > Below(now.Model, Hem).Values.Sum());
    }

    [Fact]
    [Trait("Plan", "EDGE-cut-open-skin-4")]
    public void A_spanned_island_off_the_kept_skin_is_not_drawn()
    {
        var dress = ReachingDress(island: true);
        var before = Swap(dress, Before);
        var now = Swap(dress);
        SwappedAndCut(before);
        // The island's own vertices are spanned (15 mm under a 54 mm chord), but the ring round it (45 mm) is not.
        Assert.Equal(before.Model, now.Model);
        Assert.Equal(0, now.Report.Spanned);
    }

    [Fact]
    [Trait("Plan", "EDGE-cut-open-skin-1")]
    public void A_dip_past_half_the_chord_is_still_cut()
    {
        var dress = ReachingDress(dip: 0.045f);
        var before = Swap(dress, Before);
        var now = Swap(dress);
        SwappedAndCut(before);
        Assert.False(DrawsDip(now.Model));
        Assert.Equal(0, now.Report.Spanned);
    }

    [Theory]
    [InlineData(-0.014f)]   // 1 mm in front of the dipped skin
    [InlineData(-0.025f)]   // 10 mm behind it, buried
    [Trait("Plan", "AC-cut-open-skin-4")]
    [Trait("Plan", "EDGE-cut-open-skin-2")]
    public void A_dip_with_cloth_crowding_it_is_still_cut(float patch)
    {
        var dress = ReachingDress(patch: patch);
        var before = Swap(dress, Before);
        var now = Swap(dress);
        SwappedAndCut(before);
        Assert.False(DrawsDip(now.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-cut-open-skin-5")]
    public void A_hidden_variant_dip_does_not_come_in()
    {
        var dress = ReachingDress(hideDip: true);
        var hidden = new HashSet<string> { "atr_tv_b" };
        var before = Swap(dress, Before, hidden: hidden);
        var now = Swap(dress, hidden: hidden);
        SwappedAndCut(before);
        Assert.False(DrawsDip(now.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-cut-open-skin-6")]
    public void An_arm_dip_on_a_reached_onto_slot_is_kept()
    {
        // No arm guard, so no weights are read: a dip is drawn however the body is rigged. The dress's legs are a
        // stand-in for an arm a garment reaches onto; the pass never asks which bone a vertex is on.
        var dress = ReachingDress();
        Assert.True(DrawsDip(Swap(dress).Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-cut-open-skin-7")]
    public void A_new_triangle_with_cloth_over_its_middle_is_not_drawn()
    {
        var clear = Swap(ReachingDress());
        var dress = ReachingDress(middlePatch: true);
        var before = Swap(dress, Before);
        var now = Swap(dress);
        SwappedAndCut(before);
        Assert.True(now.Report.Spanned < clear.Report.Spanned,
                    $"{now.Report.Spanned} triangles drawn with cloth over one's middle, {clear.Report.Spanned} without");
    }

    [Fact]
    [Trait("Plan", "EDGE-cut-open-skin-8")]
    public void A_body_vertex_with_no_normal_of_its_own_is_judged_on_its_faces()
    {
        // The file's normals all zero: each vertex is judged on the sum of its triangles' faces, as the tuck judges it —
        // the same as a file whose normals are those faces' (the sheet's shaded ones, area-weighted the same way).
        var dress = ReachingDress(flatNormals: true);
        var before = Swap(dress, Before);
        var now = Swap(dress);
        var shaded = Swap(ReachingDress(shadedNormals: true));
        SwappedAndCut(before);
        Assert.Equal(DrawnRows(shaded.Model), DrawnRows(now.Model));
        Assert.Equal(shaded.Report.Spanned, now.Report.Spanned);
        // And no normal is not refused outright: past the facing guard (the coarse sheet's walls are steeper than it
        // allows, see ReachingDress's shadedNormals) the faces give it a direction and the dip is drawn.
        var anyFacing = Swap(dress, new BodyRetarget.Tuning { CutOpenSkinAnyFacing = true });
        Assert.True(DrawsDip(anyFacing.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-cut-open-skin-9")]
    public void A_bulge_off_the_laid_skin_is_kept_like_a_dip()
    {
        var dress = ReachingDress(dip: -0.015f);
        var before = Swap(dress, Before);
        var now = Swap(dress);
        SwappedAndCut(before);
        Assert.False(DrawsDip(before.Model));
        Assert.True(DrawsDip(now.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-cut-open-skin-10")]
    public void The_plain_cut_is_kept_exactly_and_only_added_to()
    {
        var dress = ReachingDress();
        var before = Swap(dress, Before);
        var now = Swap(dress);
        var was = SkinTriangles(before.Model);
        var is_ = SkinTriangles(now.Model);
        Assert.Empty(was.Except(is_));
        Assert.Equal(now.Report.Spanned, is_.Count - was.Count);

        // A legs body the dress's skin covers to the hem, nothing dipping and nothing below: nothing cut, the body whole.
        var flat = ReachingDress(dip: 0f);
        var whole = Swap((flat.Garment, flat.Chest, BodyFromRows(Hem)));
        Assert.Equal(0, whole.Report.Cut);
        Assert.Equal(0, whole.Report.Spanned);
    }

    [Fact]
    [Trait("Plan", "EDGE-cut-open-skin-11")]
    public void A_ring_in_a_kept_dip_is_judged_on_the_authors_cut()
    {
        var dress = ReachingDress(ringInDip: true);
        var before = Swap(dress, Before, carryPiercings: true);
        var now = Swap(dress, carryPiercings: true);
        SwappedAndCut(before);
        Assert.True(now.Report.Spanned > 0);
        Assert.Equal(before.Report.Adorned, now.Report.Adorned);
    }

    [Fact]
    [Trait("Plan", "AC-cut-open-skin-5")]
    [Trait("Plan", "EDGE-cut-open-skin-12")]
    public void Every_refit_but_the_automatic_one_is_unchanged()
    {
        var dress = ReachingDress();
        var knob = Swap(dress, Before);
        // The hidden-skin cut on (the Studio's box ticked, the size set, game gear): the author's cut is carried as before.
        Assert.Equal(Swap(dress, Before, cutHidden: true).Model, Swap(dress, cutHidden: true).Model);
        // The Studio with the box unticked: the legs are cut all the same, but the Studio never asks for the pass.
        Assert.Equal(knob.Model, Swap(dress, openSkin: false).Model);
        // A tucked slot: the tuck draws open skin by its own rule.
        var top = CoarseSkinTop();
        byte[] Tucked(BodyRetarget.Tuning t) => BodyRetarget.WithTuning(t, () => BodyRetarget.Rebuild(
            top, [Resized(Dipped(0.015f))], swapSkin: true, weights: null, out _, cutHidden: true, tuckSlot: "_top",
            openSkin: true))!;
        Assert.Equal(Tucked(Before), Tucked(new BodyRetarget.Tuning()));

        // Through the whole plan, as the Studio and the automatic refit call it.
        var garment = ModelPartReader.Read(dress.Garment)!;
        var pairs = new[] { Resized(dress.Chest, "_top"), Resized(dress.Legs, "_dwn") };
        BodyRetarget.Planned Plan(bool openSkin, BodyRetarget.Tuning t) => BodyRetarget.WithTuning(t, () => BodyRetarget.Plan(
            garment, dress.Garment, pairs, "_top", replaceSkin: true, acrossBodies: false, cutHidden: false, openSkin: openSkin));
        Assert.Equal(Plan(false, Before).Model, Plan(false, new BodyRetarget.Tuning()).Model);
        Assert.Equal(Plan(true, Before).Model, Plan(false, new BodyRetarget.Tuning()).Model);
        Assert.True(Plan(true, new BodyRetarget.Tuning()).Report.Swap!.Value.Spanned > 0, "the automatic refit's plan draws the dip");
    }

    [Fact]
    [Trait("Plan", "EDGE-cut-open-skin-13")]
    public void The_open_skin_is_deterministic()
    {
        var dress = ReachingDress(ringInDip: true);
        Assert.Equal(Swap(dress, carryPiercings: true).Model, Swap(dress, carryPiercings: true).Model);
    }

    /// <summary>A flat legs body over rows <paramref name="firstRow"/> to <see cref="Waist"/>: with
    /// <see cref="Hem"/>, the rows the dress's skin covers and nothing below.</summary>
    private static byte[] BodyFromRows(int firstRow)
    {
        var pos = new List<Vector3>();
        for (int r = firstRow; r <= Waist; r++)
            for (int c = 0; c < Columns; c++) pos.Add(new Vector3(X(c), Y(r), 0f));
        var tris = new List<int>();
        for (int r = 0; r < Waist - firstRow; r++)
            for (int c = 0; c + 1 < Columns; c++)
            {
                int a = r * Columns + c, b = a + 1, d = a + Columns, e = d + 1;
                tris.AddRange([a, b, e, a, e, d]);
            }
        var mesh = new SyntheticModel.SurfaceMesh(Skin, [.. pos], [.. pos.Select(_ => Vector3.UnitZ)],
                                                  [.. pos.Select(_ => Vector2.Zero)],
                                                  [.. pos.Select(_ => ((string, float)[]?)[("j_sebo_c", 1f)])],
                                                  [(0u, [.. tris])]);
        return SyntheticModel.BuildSurface([], mesh);
    }

    /// <summary>The model's skin triangles, as their corners' positions to a tenth of a millimetre.</summary>
    private static HashSet<string> SkinTriangles(byte[] model)
    {
        var m = ModelPartReader.Read(model)!;
        string P(int v) => $"{MathF.Round(m.Positions[v * 3] * 1e4f)},{MathF.Round(m.Positions[v * 3 + 1] * 1e4f)},{MathF.Round(m.Positions[v * 3 + 2] * 1e4f)}";
        var set = new HashSet<string>();
        foreach (var part in m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                set.Add(string.Join("|", new[] { P(part.Triangles[t]), P(part.Triangles[t + 1]), P(part.Triangles[t + 2]) }.Order()));
        return set;
    }
}
