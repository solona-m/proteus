using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The shipped foot band. <c>BaseOffset</c> went 1 mm -> 0.05 mm on a sweep that pinned the feet at 1.00x as its
/// control, so the feet were never measured below 1 mm; at 0.05 mm the shell z-fights the skin it was cut from.
/// These pin the value the foot was swept back up to, and the heights the band covers.
/// </summary>
public class FootPushTests
{
    /// <summary>1.00 mm: what the whole body had before the 20x change, and what the re-sweep found clean.</summary>
    [Fact]
    public void The_foot_gets_the_offset_the_body_gave_up()
        => Assert.Equal(1e-3f, SecondSkinWriter.BaseOffset * SecondSkinWriter.FootPushAt(0f), 6);

    /// <summary>0.50 mm and 0.75 mm were both measured short, so the band must not be scaled back to them.</summary>
    [Fact]
    public void The_foot_band_clears_the_values_that_were_measured_short()
        => Assert.True(SecondSkinWriter.BaseOffset * SecondSkinWriter.FootPushScale > 7.5e-4f);

    /// <summary>A heeled foot is modelled below the origin — the sole of the shoes this was measured against
    /// sits at y -0.032 — so the foot band has no bottom.</summary>
    [Theory]
    [InlineData(-0.05f)]
    [InlineData(-0.032f)]
    [InlineData(0f)]
    [InlineData(0.15f)]
    public void A_heeled_foot_below_the_origin_is_still_in_the_foot_band(float y)
        => Assert.Equal(SecondSkinWriter.FootPushScale, SecondSkinWriter.FootPushAt(y));

    /// <summary>
    /// THE property that matters, not the shape: no step anywhere. The shell is one mesh, so a jump between two
    /// heights is a fold in its surface — and a gap wherever a part join straddles it, which the crack weld only
    /// closes within 1 mm. Walked finely enough to catch a discontinuity at either band edge.
    /// </summary>
    [Fact]
    public void The_band_is_continuous_so_no_edge_can_straddle_a_step()
    {
        const float step = 0.0005f;                    // 0.5 mm, far finer than any body mesh's edge length
        float worst = 0f;
        for (float y = -0.10f; y < 0.45f; y += step)
        {
            float jump = System.MathF.Abs(SecondSkinWriter.FootPushAt(y + step) - SecondSkinWriter.FootPushAt(y));
            if (jump > worst) worst = jump;
        }
        // In millimetres of shell height across one 0.5 mm step of body.
        float worstMm = worst * SecondSkinWriter.BaseOffset * 1000f;
        Assert.True(worstMm < 0.01f, $"the band steps {worstMm:F4} mm within 0.5 mm of travel");
    }

    /// <summary>Monotone: the shell may not rise again on the way up, or it would tent over the shin.</summary>
    [Fact]
    public void The_taper_only_ever_descends()
    {
        float previous = SecondSkinWriter.FootPushAt(-0.10f);
        for (float y = -0.10f; y < 0.45f; y += 0.001f)
        {
            float here = SecondSkinWriter.FootPushAt(y);
            Assert.True(here <= previous + 1e-4f, $"the band rises again at y {y:F3}");
            previous = here;
        }
    }

    /// <summary>The taper spans the whole band and reaches both ends exactly.</summary>
    [Fact]
    public void The_taper_runs_from_the_foot_value_to_the_shipped_one()
    {
        Assert.Equal(SecondSkinWriter.FootPushScale, SecondSkinWriter.FootPushAt(SecondSkinWriter.FootBandTop));
        Assert.Equal(1f, SecondSkinWriter.FootPushAt(SecondSkinWriter.FootRampTop));
        var middle = SecondSkinWriter.FootPushAt((SecondSkinWriter.FootBandTop + SecondSkinWriter.FootRampTop) / 2f);
        Assert.True(middle < SecondSkinWriter.FootPushScale && middle > 1f);
    }

    /// <summary>
    /// Set from the TALLEST race, not the measured one. A male Roegadyn is about 1.22x c0201, so the ankle the
    /// band has to cover sits near y 0.184; reaching past it only returns that region to at most 1 mm, which
    /// every race shipped with before the 20x change.
    /// </summary>
    [Fact]
    public void The_band_covers_the_tallest_race_s_ankle()
        => Assert.Equal(SecondSkinWriter.FootPushScale, SecondSkinWriter.FootPushAt(0.151f * 1.22f));

    /// <summary>Nothing anywhere may exceed the offset the whole body shipped with before the 20x change.</summary>
    [Theory]
    [InlineData(-0.05f)]
    [InlineData(0.2f)]
    [InlineData(0.26f)]
    [InlineData(1.19f)]
    public void The_band_never_pushes_past_the_historic_offset(float y)
        => Assert.True(SecondSkinWriter.BaseOffset * SecondSkinWriter.FootPushAt(y) <= 1e-3f + 1e-9f);

    /// <summary>Above the taper nothing is scaled: that is the region the 20x change WAS measured on.</summary>
    [Theory]
    [InlineData(0.32f)]     // the taper ends AT its height
    [InlineData(0.5f)]      // thigh
    [InlineData(1.19f)]     // bust apex
    [InlineData(1.45f)]     // shoulders
    public void Above_the_ramp_the_shipped_offset_stands(float y)
        => Assert.Equal(1f, SecondSkinWriter.FootPushAt(y));

    /// <summary>The feet-slot skin mesh this was diagnosed on reaches y 0.151 on c0201; the band must cover it.</summary>
    [Fact]
    public void The_band_covers_the_feet_slot_skin_mesh()
        => Assert.True(SecondSkinWriter.FootBandTop > 0.151f);

    [Fact]
    public void The_bands_are_ordered()
        => Assert.True(SecondSkinWriter.FootBandTop < SecondSkinWriter.FootRampTop);
}
