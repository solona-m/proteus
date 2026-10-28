using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using static Proteus.Tests.RefitNippleSmoothPinTests;
using static Proteus.Tests.SyntheticChest;

namespace Proteus.Tests;

/// <summary>
/// Cloth the transfer snapped onto a body vertex — a skin COPY, as Laurel's tights are of the Neolithe legs — keeps its
/// source body's tessellation, and lands on a body tessellated otherwise only at its own vertices. The new skin then
/// bulges through between them. Here the source is the synthetic chest on a coarse grid (10 mm cells) and the target the
/// same shape on the fine one (5 mm): every fine vertex between two coarse ones stands up to ~0.7 mm proud of the coarse
/// cloth's flat faces near the lobes' peaks. The fine grid holds every coarse vertex, so the transfer's field is ~zero
/// and only the push decides the answer.
/// </summary>
public class SnapPushTests(ITestOutputHelper output)
{
    private static readonly BodyRetarget.Tuning Before = new(NoSnapPush: true);

    /// <summary>The cloth panel 0.05 mm off its skin: inside the snap's reach, so every cloth vertex snaps.</summary>
    private const float Copy = 0.00005f;

    private sealed record Run(byte[] Model, BodyRetarget.Report Report, int Folded, int Outside);

    private static Run Refit(byte[] garmentBytes, BodyRetarget.SlotPair pair, BodyRetarget.Tuning tuning, bool clearBody = false,
                             bool replaceSkin = true)
    {
        var garment = ModelPartReader.Read(garmentBytes)!;
        var sets = BodyRetarget.Sets.From(garment, new HashSet<int>(),
                                          BodyRetarget.PartedSkinMeshes(garment, [pair], "_top", replaceSkin, null));
        SecondSkinWriter.Vec3[]? final = null;
        var plan = BodyRetarget.WithTuning(tuning, () => BodyRetarget.Plan(
            garment, garmentBytes, [pair], "_top", held: new HashSet<int>(), replaceSkin: replaceSkin, acrossBodies: true,
            clearBody: clearBody, cutHidden: false, trace: (s, d) => { if (s == "layer-order") final = d; }));
        Assert.NotNull(final);
        var (_, outside) = SkinThroughCloth.Find(ModelPartReader.Read(plan.Model)!);
        return new Run(plan.Model, plan.Report, BodyRetarget.FoldedTriangles(sets, final!), outside.Count);
    }

    private static (byte[] Garment, BodyRetarget.SlotPair Pair) CoarseToFine(float off = Copy)
    {
        var source = Body(0f, 0f, step: 2);
        var target = Body(0f, 0f, step: 1);
        return (Garment(0f, 0f, BothCups, off: off, step: 2), Pair(source, target, atlas: true));
    }

    [Fact]
    [Trait("Plan", "AC-snap-push-1")]
    [Trait("Speed", "Slow")]
    public void Skin_copy_cloth_onto_a_finer_body_has_no_skin_through_it()
    {
        var (garment, pair) = CoarseToFine();

        var before = Refit(garment, pair, Before);
        var now = Refit(garment, pair, new BodyRetarget.Tuning());
        output.WriteLine($"skin through the cloth {before.Outside} -> {now.Outside}; folded {before.Folded} -> {now.Folded}; "
                       + $"snapped {before.Report.Snapped} -> {now.Report.Snapped}");

        Assert.True(before.Outside > 0, "the fixture must show the bulge the rule is for");
        Assert.Equal(0, now.Outside);
        Assert.Equal(0, now.Folded);
    }

    [Fact]
    [Trait("Plan", "AC-snap-push-1")]
    [Trait("Speed", "Slow")]
    public void The_snap_itself_is_untouched()
    {
        var (garment, pair) = CoarseToFine();

        Assert.Equal(Refit(garment, pair, Before).Report.Snapped, Refit(garment, pair, new BodyRetarget.Tuning()).Report.Snapped);
    }

    [Fact]
    [Trait("Plan", "AC-snap-push-2")]
    [Trait("Plan", "EDGE-snap-push-1")]
    [Trait("Speed", "Slow")]
    public void Between_sizes_of_one_body_the_model_is_byte_identical()
    {
        // Prouder nipples on the target: the nipple smoothing runs and wraps the identity correspondence, which the gate
        // must still see through.
        var pair = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        var garment = Garment(0f, 0f, BothCups, off: Copy);

        var before = Refit(garment, pair, Before);
        var now = Refit(garment, pair, new BodyRetarget.Tuning());

        Assert.True(now.Report.Smoothed > 0, "the fixture must wrap the correspondence");
        Assert.Equal(before.Model, now.Model);
        Assert.Equal(before.Report.Pushed, now.Report.Pushed);
    }

    [Fact]
    [Trait("Plan", "EDGE-snap-push-2")]
    public void Only_a_swapped_pair_of_another_tessellation_lets_snapped_cloth_be_pushed()
    {
        var coarse = Body(0f, 0f, step: 2);
        var fine = Body(0f, 0f);
        var identity = Pair(fine, Body(Nipple, Nipple));
        var atlas = Pair(coarse, fine, atlas: true);
        var smoothed = identity with
        {
            Correspondence = new SmoothedCorrespondence(identity.Correspondence, identity.Target,
                                                        new System.Numerics.Vector3[identity.Target.Positions.Length / 3]),
        };

        Assert.False(BodyRetarget.SnappedLandsElsewhere([identity], replaceSkin: true));
        Assert.False(BodyRetarget.SnappedLandsElsewhere([smoothed], replaceSkin: true));
        Assert.True(BodyRetarget.SnappedLandsElsewhere([atlas], replaceSkin: true));
        Assert.True(BodyRetarget.SnappedLandsElsewhere([identity, atlas with { Slot = "_dwn" }], replaceSkin: true));
        // Nothing swapped: the skin under the cloth is the garment's own, moved by the same field — no bulge.
        Assert.False(BodyRetarget.SnappedLandsElsewhere([atlas], replaceSkin: false));
        Assert.False(BodyRetarget.SnappedLandsElsewhere([atlas with { TargetModel = null }], replaceSkin: true));
        Assert.False(BodyRetarget.SnappedLandsElsewhere([atlas with { ShapeOnly = true }], replaceSkin: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-snap-push-3")]
    [Trait("Speed", "Slow")]
    public void Clearing_the_body_leaves_snapped_cloth_as_before()
    {
        var (garment, pair) = CoarseToFine();

        Assert.Equal(Refit(garment, pair, Before, clearBody: true).Model,
                     Refit(garment, pair, new BodyRetarget.Tuning(), clearBody: true).Model);
    }

    [Fact]
    [Trait("Plan", "EDGE-snap-push-4")]
    [Trait("Speed", "Slow")]
    public void Snapped_cloth_the_author_tucked_under_the_skin_stays_and_pins_nothing()
    {
        // One panel: its left half a hair INSIDE its own skin (still snapping), its right half 0.3 mm off it — not
        // snapped, and pushed where the finer skin bulges through, as before this rule. The tucked half moves only as
        // the transfer moves it, and does not hold the other half back where they meet.
        var source = Body(0f, 0f, step: 2);
        var target = Body(0f, 0f, step: 1);
        var tucked = Garment(0f, 0f, BothCups, step: 2, offAt: x => x < 0f ? -Copy : 0.0003f);
        var pair = Pair(source, target, atlas: true);

        var before = Refit(tucked, pair, Before);
        var now = Refit(tucked, pair, new BodyRetarget.Tuning());

        Assert.True(before.Report.Pushed > 0, "the right half must need the push");
        Assert.Equal(before.Model, now.Model);
    }

    [Fact]
    [Trait("Plan", "EDGE-snap-push-8")]
    public void The_experiments_knob_name_is_gone()
    {
        var names = typeof(BodyRetarget.Tuning).GetProperties().Select(p => p.Name).ToList();

        Assert.Contains("NoSnapPush", names);
        Assert.DoesNotContain("SnapFaces", names);
    }
}
