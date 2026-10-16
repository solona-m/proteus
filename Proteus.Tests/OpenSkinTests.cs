using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using static Proteus.Tests.SkinTuckSheet;

namespace Proteus.Tests;

/// <summary>
/// The new body's skin drawn in the open where the garment's own skin spans it (see <see cref="BodyRetarget.TuckSpanShare"/>),
/// on <see cref="SkinTuckSheet.CoarseSkinTop"/> — the game's coarse chest skin, two triangles over rows 10-13 — over a body
/// whose chest dips under it at rows 11-12 (<see cref="SkinTuckSheet.Dipped"/>), with no cloth over the dip: the new
/// body's cleavage under Dirndl's Bodice, which the cut left open in game.
/// </summary>
public class OpenSkinTests(ITestOutputHelper output)
{
    private (byte[] Model, BodyRetarget.SwapReport Report) Swap(byte[] top, byte[] body, bool tuck = true,
                                                               IReadOnlySet<string>? hidden = null)
    {
        var model = BodyRetarget.SwapSkin(top, [Resized(body, hidden: hidden)], out var report, tuckSlot: tuck ? "_top" : null)!;
        Assert.NotNull(model);
        output.WriteLine($"{(tuck ? "tuck" : "no tuck")}: {report}");
        return (model, report);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-large-bust-7")]
    [Trait("Plan", "AC-neckline-large-bust-4")]
    public void Open_skin_the_games_skin_spans_is_kept_past_the_cuts_sag()
    {
        // 15 mm under an 85 mm chord: past the cut's 12 mm sag, within half the chord.
        var top = CoarseSkinTop();
        var (untucked, _) = Swap(top, Dipped(0.015f), tuck: false);
        Assert.False(DrawsDip(untucked));   // the cut leaves it open

        var (model, report) = Swap(top, Dipped(0.015f));
        Assert.True(DrawsDip(model));
        Assert.True(report.Opened > 0);
        Assert.True(report.Opened <= report.Tucked);
        Assert.Equal(0, SkinTuckSheet.NewSkinAgainstBody(ModelPartReader.Read(untucked)!, ModelPartReader.Read(model)!,
                                                         ModelPartReader.Read(Dipped(0.015f))!).NotTheBodys);

        var off = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoOpenSkin: true), () => Swap(top, Dipped(0.015f)));
        Assert.False(DrawsDip(off.Model));
        Assert.Equal(0, off.Report.Opened);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-10")]
    public void Open_skin_past_the_authors_edge_is_not_kept()
    {
        // The cloth stops at row 5: rows 6-9, below the author's skin and with nothing over them, land past its open edge.
        var (model, report) = Swap(CoarseSkinTop(clothTo: 5), Body());
        Assert.Equal(0, report.Opened);
        Assert.DoesNotContain(DrawnRows(model).Keys, r => r < 9);
    }

    [Theory]
    [Trait("Plan", "EDGE-neckline-large-bust-11")]
    [InlineData(-0.014f)]   // 1 mm in front of the dipped skin
    [InlineData(-0.025f)]   // 10 mm behind it
    public void Open_skin_with_cloth_crowding_it_is_not_kept(float patch)
        => Assert.False(DrawsDip(Swap(CoarseSkinTop(patch: patch), Dipped(0.015f)).Model));

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-12")]
    public void Open_arm_skin_is_not_kept()
        => Assert.False(DrawsDip(Swap(CoarseSkinTop(), Dipped(0.015f, arm: r => r >= 10)).Model));

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-13")]
    public void Open_skin_beyond_the_span_allowance_is_not_kept()
    {
        // 45 mm under an 85 mm chord: past half of it.
        var (model, report) = Swap(CoarseSkinTop(), Dipped(0.045f));
        Assert.False(DrawsDip(model));
        Assert.Equal(0, report.Opened);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-15")]
    public void A_bulge_over_the_games_skin_is_kept_like_a_dip()
        => Assert.True(DrawsDip(Swap(CoarseSkinTop(), Dipped(-0.015f)).Model));

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-17")]
    public void Hidden_variants_are_never_opened()
    {
        // The dip and everything below row 12 is a variant the body mod does not draw: the author's skin over it no longer
        // lies on a body that is drawn, so nothing is swapped at all — and nothing of the variant comes in.
        var model = BodyRetarget.SwapSkin(CoarseSkinTop(), [Resized(Dipped(0.015f, hiddenBelow: 12),
                                                                    hidden: new HashSet<string> { "atr_tv_b" })],
                                          out var report, tuckSlot: "_top");
        Assert.True(model == null || !DrawsDip(model));
        Assert.Equal(0, report.Opened);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-18")]
    public void Open_skin_brings_no_piercing_the_author_did_not_show()
    {
        // The body carries a ring; whether it comes in is the author's cut's to say, not the open skin's.
        var (_, opened) = Swap(CoarseSkinTop(), Dipped(0.015f, piercing: true));
        var closed = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoOpenSkin: true),
                                             () => Swap(CoarseSkinTop(), Dipped(0.015f, piercing: true)));
        Assert.True(opened.Opened > 0);
        Assert.Equal(closed.Report.Adorned, opened.Adorned);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-19")]
    public void Open_skin_is_deterministic()
        => Assert.Equal(Swap(CoarseSkinTop(), Dipped(0.015f)).Model, Swap(CoarseSkinTop(), Dipped(0.015f)).Model);

    [Fact]
    [Trait("Plan", "AC-neckline-large-bust-8")]
    public void A_game_tops_open_skin_is_counted_in_the_log()
    {
        var top = CoarseSkinTop();
        var garment = ModelPartReader.Read(top)!;
        var lines = new List<string>();
        var plan = RefitCore.PlanRefit(garment, top, [Resized(Dipped(0.015f))], "_top", across: true, gameGear: true, out _,
                                       log: lines.Add);
        var swap = plan.Report.Swap!.Value;
        Assert.True(swap.Opened > 0);
        Assert.Contains(lines, l => l.StartsWith($"skin tucked: {swap.Tucked} triangles", System.StringComparison.Ordinal)
                                    && l.Contains($"({swap.Opened} in the open", System.StringComparison.Ordinal));
    }
}
