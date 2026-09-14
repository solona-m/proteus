using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The reinforced toe: pushing the capped area toward opaque so a sheer stocking gets the denser toe box
/// real hosiery is knitted with. The value produced here becomes the shell normal's BLUE channel, which is
/// the gear transparency gate, so these are the numbers that decide how the toe reads.
/// </summary>
public class ReinforcedToeTests
{
    private static byte[] Fill(int n, byte v)
    {
        var a = new byte[n];
        for (int i = 0; i < n; i++) a[i] = v;
        return a;
    }

    /// <summary>
    /// The invariant the whole feature rests on: reinforcement can only take away transparency that is
    /// already there. A texel the stocking does not paint stays unpainted, so this can never put fabric on
    /// a bare toe — the same rule the per-row opacity pass follows.
    /// </summary>
    [Fact]
    public void ReinforceToeCap_WhereTheShellPaintsNothing_StaysEmpty()
    {
        var alpha = Fill(4, 0);
        var cap   = Fill(4, 255);

        var got = SecondSkinService.ReinforceToeCap(alpha, cap, 2, 2, density: 100, feather: 0);

        Assert.Equal(new byte[] { 0, 0, 0, 0 }, got);
    }

    [Fact]
    public void ReinforceToeCap_ZeroDensity_IsIdentity()
    {
        var alpha = Fill(4, 100);
        var cap   = Fill(4, 255);

        var got = SecondSkinService.ReinforceToeCap(alpha, cap, 2, 2, density: 0, feather: 0);

        Assert.Equal(alpha, got);
    }

    [Fact]
    public void ReinforceToeCap_FullDensityInsideTheCap_IsOpaque()
    {
        var alpha = Fill(4, 100);
        var cap   = Fill(4, 255);

        var got = SecondSkinService.ReinforceToeCap(alpha, cap, 2, 2, density: 100, feather: 0);

        Assert.Equal(255, got[0]);
    }

    [Fact]
    public void ReinforceToeCap_OutsideTheCap_IsUnchanged()
    {
        var alpha = Fill(4, 100);
        var cap   = Fill(4, 0);

        var got = SecondSkinService.ReinforceToeCap(alpha, cap, 2, 2, density: 100, feather: 0);

        Assert.Equal(alpha, got);
    }

    /// <summary>
    /// The boost is the PRODUCT of the density and the cap weight, so a grey cap map is honoured rather
    /// than thresholded — which is half of why the rim fades instead of stepping.
    /// </summary>
    [Fact]
    public void ReinforceToeCap_GreyCapMask_ScalesTheBoost()
    {
        var alpha = Fill(4, 100);

        var full = SecondSkinService.ReinforceToeCap(alpha, Fill(4, 255), 2, 2, density: 100, feather: 0);
        var half = SecondSkinService.ReinforceToeCap(alpha, Fill(4, 128), 2, 2, density: 100, feather: 0);

        Assert.Equal(255, full[0]);
        Assert.InRange(half[0], 101, 254);          // strictly between untouched and opaque
        Assert.True(half[0] < full[0]);
    }

    /// <summary>Half the density is half the boost, on the same curve a positive row Opacity uses.</summary>
    [Fact]
    public void ReinforceToeCap_HalfDensity_LandsBetween()
    {
        var alpha = Fill(4, 100);
        var cap   = Fill(4, 255);

        var half = SecondSkinService.ReinforceToeCap(alpha, cap, 2, 2, density: 50, feather: 0);

        // a = 100/255 ≈ 0.392; newA = a + (1-a)·0.5 ≈ 0.696 → ≈178
        Assert.InRange(half[0], 173, 183);
    }

    /// <summary>
    /// The cap map is 512² and the sheet is the build's size, so the two are strided rather than resampled.
    /// A 4×4 sheet against a 2×2 map must send each sheet quadrant to one map texel.
    /// </summary>
    [Fact]
    public void ReinforceToeCap_StridesTheCapMaskAgainstTheSheet()
    {
        var alpha = Fill(16, 100);
        var cap   = new byte[] { 255, 0,
                                 0,   0 };          // only the top-left quarter is capped

        var got = SecondSkinService.ReinforceToeCap(alpha, cap, texSize: 4, capSize: 2,
                                                    density: 100, feather: 0);

        Assert.Equal(255, got[0]);                  // (0,0) → cap (0,0)
        Assert.Equal(255, got[1]);                  // (1,0) → cap (0,0)
        Assert.Equal(255, got[4]);                  // (0,1) → cap (0,0)
        Assert.Equal(255, got[5]);                  // (1,1) → cap (0,0)
        Assert.Equal(100, got[2]);                  // (2,0) → cap (1,0) = 0
        Assert.Equal(100, got[8]);                  // (0,2) → cap (0,1) = 0
        Assert.Equal(100, got[15]);                 // (3,3) → cap (1,1) = 0
    }

    /// <summary>
    /// The other half of the soft rim, and the half the art cannot supply: the cap map is authored for a
    /// geometry cut so it is usually painted hard-edged, and a hard density step across the foot reads as a
    /// decal rather than as knitting. Feathering must produce a ramp across a hard edge while leaving the
    /// deep interior and the far exterior alone.
    /// </summary>
    [Fact]
    public void ReinforceToeCap_Feather_RampsAcrossAHardEdge()
    {
        const int n = 16;
        var alpha = Fill(n * n, 100);
        var cap   = new byte[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                cap[y * n + x] = (byte)(x < n / 2 ? 255 : 0);   // hard edge down the middle

        var got = SecondSkinService.ReinforceToeCap(alpha, cap, n, n, density: 100, feather: 2);

        int row = 8 * n;
        Assert.Equal(255, got[row + 0]);                        // deep inside: still fully reinforced
        Assert.Equal(100, got[row + 15]);                       // far outside: untouched
        Assert.InRange(got[row + 8], 101, 254);                 // just past the edge: part way
        Assert.True(got[row + 7] > got[row + 8], "density must fall off outward across the rim");
    }

    /// <summary>
    /// The feather fades OUTWARD only. The cap's footprint is the toe box, with the toe tips on its
    /// boundary, so a blur that also ate inward left the most visible part of the toe at about half weight —
    /// "only slightly more opaque" at 100% in game. Every texel inside the footprint, right up to its edge,
    /// must reach full strength.
    /// </summary>
    [Fact]
    public void ReinforceToeCap_Feather_KeepsFullDensityRightToTheCapsEdge()
    {
        const int n = 16;
        var alpha = Fill(n * n, 100);
        var cap   = new byte[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                cap[y * n + x] = (byte)(x < n / 2 ? 255 : 0);

        var got = SecondSkinService.ReinforceToeCap(alpha, cap, n, n, density: 100, feather: 3);

        int row = 8 * n;
        for (int x = 0; x < n / 2; x++)
            Assert.Equal(255, got[row + x]);                    // the whole footprint, edge included
        Assert.InRange(got[row + 8], 101, 254);                 // and a soft ramp just outside it
    }

    /// <summary>
    /// The real proportions: a 2K sheet against the 512 cap map, with the cap covering a small patch the
    /// way a toe box does. Only that patch's footprint may change — the reported symptom was the whole
    /// garment going denser, which is exactly what a striding mistake here would look like.
    /// </summary>
    [Fact]
    public void ReinforceToeCap_AtSheetProportions_TouchesOnlyTheCappedPatch()
    {
        const int tex = 2048, capN = 512;
        var alpha = Fill(tex * tex, 100);
        var cap   = new byte[capN * capN];
        for (int y = 100; y < 140; y++)                 // a 40×40 patch, ~0.6% of the map
            for (int x = 100; x < 140; x++)
                cap[y * capN + x] = 255;

        var got = SecondSkinService.ReinforceToeCap(alpha, cap, tex, capN, density: 100, feather: 0);

        int changed = 0;
        for (int i = 0; i < got.Length; i++)
            if (got[i] != alpha[i]) changed++;

        int step = tex / capN;                           // 4
        Assert.Equal(40 * 40 * step * step, changed);    // exactly the patch's footprint, nothing else
        Assert.Equal(100, got[0]);                       // origin is far from the patch
        Assert.Equal(255, got[(100 * step) * tex + 100 * step]);
    }

    /// <summary>
    /// A sheet SMALLER than the cap map. An integer stride floors to 1 here and reads the map's top-left
    /// quarter stretched across the whole atlas — which paints the reinforcement over the entire garment,
    /// the exact symptom this guards against. Only the patch's proportional footprint may move.
    /// </summary>
    [Fact]
    public void ReinforceToeCap_SheetSmallerThanTheCapMap_StillOnlyTouchesTheCappedPatch()
    {
        const int tex = 256, capN = 512;                 // sheet is half the map
        var alpha = Fill(tex * tex, 100);
        var cap   = new byte[capN * capN];
        for (int y = 400; y < 464; y++)                  // a patch in the BOTTOM-RIGHT of the map,
            for (int x = 400; x < 464; x++)              // which a floored stride can never reach
                cap[y * capN + x] = 255;

        var got = SecondSkinService.ReinforceToeCap(alpha, cap, tex, capN, density: 100, feather: 0);

        int changed = 0;
        for (int i = 0; i < got.Length; i++)
            if (got[i] != alpha[i]) changed++;

        Assert.Equal(32 * 32, changed);                  // 64 map texels → 32 sheet texels per axis
        Assert.Equal(100, got[0]);                       // top-left untouched
        Assert.Equal(255, got[210 * tex + 210]);         // inside the patch's footprint
    }

    /// <summary>A cap map smaller than it claims must be refused rather than read past the end.</summary>
    [Fact]
    public void ReinforceToeCap_ShortCapMask_IsIgnored()
    {
        var alpha = Fill(16, 100);
        var cap   = Fill(2, 255);                               // claims 2×2 but is one texel short

        var got = SecondSkinService.ReinforceToeCap(alpha, cap, texSize: 4, capSize: 2,
                                                    density: 100, feather: 0);

        Assert.Equal(alpha, got);
    }
}
