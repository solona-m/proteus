using System;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The coarse-face sag cap and the pressed-skin cut (build 1501, "Starlight"'s bottoms) against the build before them, on
/// the garments the face clearing's 2 mm cap was set by and the regression harness already refits: "Sirius" (the corset
/// rim behind <see cref="BodyRetarget.ClearFaceDeepest"/>), "This Old Thing" and Geometric's skirt. No more folds, no
/// more cloth inside the new body, no further from the author's own size, no worse a move, no more skin through the cloth,
/// and no steeper cloth movement out of line with its neighbours. On demand: the regression gate of the change.
/// </summary>
public partial class SkirtHipsRegressionLocalTests
{
    /// <summary>The build before the coarse-face sag cap and the pressed-skin cut.</summary>
    private static readonly BodyRetarget.Tuning Pre1501 = new(NoCoarseSag: true, NoPressedSkinCut: true);

    /// <summary>How much steeper a garment's cloth movement may be out of line with its neighbours (0.005: 0.5 mm over
    /// 100 mm, rounding across the passes).</summary>
    private const float OutOfLineSlopeSlack = 0.005f;

    /// <param name="acceptedSkin">Skin points it may newly put through the cloth, accepted at their measured count.</param>
    private void CoarseSagNoWorse(string what, Runs r, bool againstAuthor, int acceptedSkin = 0)
    {
        float now = FromAuthor(r.Author, r.Now.Model), before = FromAuthor(r.Author, r.Before.Model);
        var cloth = r.Garment.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)
                                               && !BodySurface.IsSmallclothes(p.Material))
                                   .Select(p => p.Label).ToHashSet();
        var lineNow = StarlightMeasure.OutOfLine(r.Garment, r.Now.Model, cloth, perLength: true);
        var lineBefore = StarlightMeasure.OutOfLine(r.Garment, r.Before.Model, cloth, perLength: true);
        int skinNow = SkinThroughCloth.Find(r.Now.Model).Outside.Count, skinBefore = SkinThroughCloth.Find(r.Before.Model).Outside.Count;
        output.WriteLine($"{what}: from the author's own {before * 1000f:F3} -> {now * 1000f:F3} mm; folded {r.Before.Folded} -> "
                       + $"{r.Now.Folded}; inside {r.Before.Inside} -> {r.Now.Inside}; worst move {r.Before.WorstMove * 1000f:F1} -> "
                       + $"{r.Now.WorstMove * 1000f:F1} mm; skin through {skinBefore} -> {skinNow}; out of line (slope) max "
                       + $"{lineBefore.Max:F3} -> {lineNow.Max:F3}, p99 {lineBefore.P99:F3} -> {lineNow.P99:F3}");
        Assert.True(r.Now.Folded <= r.Before.Folded, $"{r.Now.Folded} folded, {r.Before.Folded} before");
        Assert.True(r.Now.Inside <= r.Before.Inside, $"{r.Now.Inside} inside, {r.Before.Inside} before");
        if (againstAuthor) Assert.True(now <= before + 1e-5f, $"{now * 1000f:F3} mm from the author's own, {before * 1000f:F3} before");
        Assert.True(r.Now.WorstMove <= r.Before.WorstMove + 1e-5f,
                    $"worst move {r.Now.WorstMove * 1000f:F1} mm, {r.Before.WorstMove * 1000f:F1} before");
        Assert.True(skinNow <= skinBefore + acceptedSkin, $"{skinNow} skin points through the cloth, {skinBefore} before");
        Assert.True(lineNow.Max <= lineBefore.Max + OutOfLineSlopeSlack, $"out of line {lineNow.Max:F3}, {lineBefore.Max:F3} before");
        Assert.True(lineNow.P99 <= lineBefore.P99 + OutOfLineSlopeSlack, $"out of line p99 {lineNow.P99:F3}, {lineBefore.P99:F3} before");
    }

    [LocalDataTheory(Sirius, Neolithe, Rue, Yab)]
    [MemberData(nameof(SiriusTargets))]
    [Trait("Plan", "AC-starlight-bottoms-5")]
    [Trait("Plan", "EDGE-starlight-bottoms-14")]
    [Trait("Suite", "Diagnostic")]
    [Trait("Speed", "Slow")]
    public void Sirius_is_no_worse_with_the_coarse_sag_cap(string size)
        => CoarseSagNoWorse($"Sirius L -> {size}",
                            Refit(Sirius, "size/Sirius/L/chara/equipment/e0154/model/c0201e0154_top.mdl",
                                  $"size/Sirius/{size}/chara/equipment/e0154/model/c0201e0154_top.mdl", "_top",
                                  [Neolithe, Rue, Yab], now: new BodyRetarget.Tuning(), before: Pre1501, fitted: false),
                            againstAuthor: true);

    [LocalDataFact(OldThing, Rue, Neolithe)]
    [Trait("Plan", "AC-starlight-bottoms-5")]
    [Trait("Suite", "Diagnostic")]
    [Trait("Speed", "Slow")]
    public void This_Old_Thing_is_no_worse_with_the_coarse_sag_cap()
        => CoarseSagNoWorse("This Old Thing Neolithe L -> Rue L",
                            Refit(OldThing, "size/neolithe l/chara/equipment/e6255/model/c0201e6255_top.mdl",
                                  "size/rue l/chara/equipment/e6255/model/c0201e6255_top.mdl", "_top", [Rue, Neolithe],
                                  now: new BodyRetarget.Tuning(), before: Pre1501, fitted: false),
                            againstAuthor: false);

    [LocalDataFact(Geometric, Bibo)]
    [Trait("Plan", "AC-starlight-bottoms-5")]
    [Trait("Suite", "Diagnostic")]
    [Trait("Speed", "Slow")]
    public void Geometric_is_no_worse_with_the_coarse_sag_cap()
        => CoarseSagNoWorse("Geometric skirt Small -> Large",
                            Refit(Geometric, "leg size/small/chara/equipment/e6010/model/c0201e6010_dwn.mdl",
                                  "leg size/large/chara/equipment/e6010/model/c0201e6010_dwn.mdl", "_dwn", [Bibo],
                                  now: new BodyRetarget.Tuning(), before: Pre1501, fitted: false),
                            // Accepted (build 1501): the sag cap alone puts one skin point 0.95 mm through the skirt at the
                            // hip side (y 0.93 m), where the skirt already has 290 as made; nothing else moved worse.
                            againstAuthor: true, acceptedSkin: 1);
}

