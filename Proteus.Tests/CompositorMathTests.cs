using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests for the pure math / pixel-operation static methods on CompositorService.
/// These methods are internal so access is granted via InternalsVisibleTo("Proteus.Tests").
/// No Dalamud, Penumbra, or game data is needed; all inputs are raw byte arrays.
/// </summary>
public class CompositorMathTests
{
    // ── ParseHex ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("#FF0000", 1.000f, 0.000f, 0.000f)]
    [InlineData("#00FF00", 0.000f, 1.000f, 0.000f)]
    [InlineData("#0000FF", 0.000f, 0.000f, 1.000f)]
    [InlineData("#000000", 0.000f, 0.000f, 0.000f)]
    [InlineData("#FFFFFF", 1.000f, 1.000f, 1.000f)]
    [InlineData("#ff0000", 1.000f, 0.000f, 0.000f)] // lowercase
    [InlineData("FF0000",  1.000f, 0.000f, 0.000f)] // no leading #
    [InlineData("#F00",    1.000f, 0.000f, 0.000f)] // 3-digit shorthand
    [InlineData("F00",     1.000f, 0.000f, 0.000f)] // 3-digit without #
    [InlineData("#080808", 0.031f, 0.031f, 0.031f)] // near-black
    public void ParseHex_VariousFormats_ReturnsCorrectFloats(
        string hex, float expectedR, float expectedG, float expectedB)
    {
        var (r, g, b) = CompositorService.ParseHex(hex);
        Assert.Equal(expectedR, r, precision: 3);
        Assert.Equal(expectedG, g, precision: 3);
        Assert.Equal(expectedB, b, precision: 3);
    }

    [Fact]
    public void ParseHex_MixedHex_CorrectComponents()
    {
        // #AABBCC → R=0xAA/255, G=0xBB/255, B=0xCC/255
        var (r, g, b) = CompositorService.ParseHex("#AABBCC");
        Assert.Equal(0xAA / 255f, r, precision: 4);
        Assert.Equal(0xBB / 255f, g, precision: 4);
        Assert.Equal(0xCC / 255f, b, precision: 4);
    }

    // ── ApplyFlatOverlay ──────────────────────────────────────────────────────

    [Fact]
    public void ApplyFlatOverlay_FullyOpaqueWhiteOverlay_ReplacesBase()
    {
        var baseTex = RGBA(255, 0, 0, 255);         // red
        var overlay  = RGBA(0,   0, 255, 255);       // blue, full alpha
        var row      = Row(1f, 1f, 1f);              // white tint (no tint)

        CompositorService.ApplyFlatOverlay(baseTex, overlay, row, 1, 1);

        Assert.Equal(0,   baseTex[0]); // R → 0
        Assert.Equal(0,   baseTex[1]); // G → 0
        Assert.Equal(255, baseTex[2]); // B → 255
    }

    [Fact]
    public void ApplyFlatOverlay_ZeroAlphaOverlay_LeavesBaseUnchanged()
    {
        var baseTex = RGBA(100, 150, 200, 255);
        var overlay  = RGBA(255, 255, 255, 0);       // fully transparent
        var original = (byte[])baseTex.Clone();

        CompositorService.ApplyFlatOverlay(baseTex, overlay, Row(1f, 1f, 1f), 1, 1);

        Assert.Equal(original, baseTex);
    }

    [Fact]
    public void ApplyFlatOverlay_HalfAlpha_BlendsCorrectly()
    {
        // base=200, overlay=0, alpha=128 → 0*(128/255) + 200*(1-128/255) ≈ 100
        var baseTex = RGBA(200, 0, 0, 255);
        var overlay  = RGBA(0, 200, 0, 128);         // green at 50% alpha

        CompositorService.ApplyFlatOverlay(baseTex, overlay, Row(1f, 1f, 1f), 1, 1);

        Assert.InRange(baseTex[0], 94, 106);  // ≈100 (red fades)
        Assert.InRange(baseTex[1], 94, 106);  // ≈100 (green appears)
    }

    [Fact]
    public void ApplyFlatOverlay_RedTint_ZeroesGreenAndBlue()
    {
        var baseTex = RGBA(0, 0, 0, 255);
        var overlay  = RGBA(255, 255, 255, 255);     // white, full alpha
        var row      = Row(1f, 0f, 0f);              // red tint only

        CompositorService.ApplyFlatOverlay(baseTex, overlay, row, 1, 1);

        Assert.Equal(255, baseTex[0]); // R = full (white * red tint)
        Assert.Equal(0,   baseTex[1]); // G = 0
        Assert.Equal(0,   baseTex[2]); // B = 0
    }

    [Fact]
    public void ApplyFlatOverlay_MultiplePixels_ProcessesAll()
    {
        // 2×1 image: two pixels
        var baseTex = new byte[] { 255, 0, 0, 255,  0, 255, 0, 255 }; // red | green
        var overlay  = new byte[] { 0,   0, 0, 255,  0, 0,   0, 0   }; // black opaque | transparent
        var row      = Row(1f, 1f, 1f);

        CompositorService.ApplyFlatOverlay(baseTex, overlay, row, 2, 1);

        // First pixel: fully replaced by black overlay
        Assert.Equal(0, baseTex[0]);
        Assert.Equal(0, baseTex[1]);
        Assert.Equal(0, baseTex[2]);
        // Second pixel: unchanged (transparent overlay)
        Assert.Equal(0,   baseTex[4]);
        Assert.Equal(255, baseTex[5]);
        Assert.Equal(0,   baseTex[6]);
    }

    // ── AlphaComposite ────────────────────────────────────────────────────────

    [Fact]
    public void AlphaComposite_FullyOpaqueSrc_ReplacesDst()
    {
        var dst = RGBA(255, 0, 0, 255);  // red
        var src = RGBA(0,   0, 255, 255); // blue, full alpha

        CompositorService.AlphaComposite(dst, src, 1, 1);

        Assert.Equal(0,   dst[0]);
        Assert.Equal(0,   dst[1]);
        Assert.Equal(255, dst[2]);
    }

    [Fact]
    public void AlphaComposite_ZeroAlphaSrc_LeavesDstUnchanged()
    {
        var dst      = RGBA(100, 150, 200, 255);
        var original = (byte[])dst.Clone();

        CompositorService.AlphaComposite(dst, RGBA(255, 0, 0, 0), 1, 1);

        Assert.Equal(original, dst);
    }

    [Fact]
    public void AlphaComposite_HalfAlpha_BlendsEvenly()
    {
        var dst = RGBA(200, 200, 200, 255);
        var src = RGBA(0,   0,   0,   128); // black at 50%

        CompositorService.AlphaComposite(dst, src, 1, 1);

        // ≈ 0*(128/255) + 200*(1-128/255) ≈ 100
        Assert.InRange(dst[0], 94, 106);
        Assert.InRange(dst[1], 94, 106);
        Assert.InRange(dst[2], 94, 106);
    }

    [Fact]
    public void AlphaComposite_WithMask_MaskAlphaZeroBlocksComposite()
    {
        var dst  = RGBA(0, 0, 0, 255);
        var src  = RGBA(255, 255, 255, 255); // fully opaque white
        var mask = RGBA(0, 0, 0, 0);         // mask blocks everything

        CompositorService.AlphaComposite(dst, src, 1, 1, mask);

        // dst should be unchanged (mask blocked the composite)
        Assert.Equal(0, dst[0]);
        Assert.Equal(0, dst[1]);
        Assert.Equal(0, dst[2]);
    }

    [Fact]
    public void AlphaComposite_WithMask_MaskTakesMinAlpha()
    {
        var dst  = RGBA(0,   0,   0,   255);
        var src  = RGBA(255, 255, 255, 255); // full alpha
        var mask = RGBA(0,   0,   0,   128); // half alpha — limits effective alpha

        CompositorService.AlphaComposite(dst, src, 1, 1, mask);

        // effective alpha = min(1, 0.5) = 0.5 → ≈128 for white on black
        Assert.InRange(dst[0], 120, 136);
    }

    [Fact]
    public void AlphaComposite_DstAlphaNotModified()
    {
        var dst = RGBA(0, 0, 0, 255);
        CompositorService.AlphaComposite(dst, RGBA(255, 255, 255, 128), 1, 1);
        Assert.Equal(255, dst[3]); // dst alpha is never touched
    }

    // ── CompoundNormal ────────────────────────────────────────────────────────

    [Fact]
    public void CompoundNormal_AddsXYDetail()
    {
        // Base: tilted right (X > 128), flat Y — decoded X ≈ +0.25
        byte[] dst = [160, 128, 200, 0];
        // Overlay: same tilt, fully opaque
        byte[] src = [160, 128, 180, 255];

        CompositorService.CompoundNormal(dst, src, 1, 1);

        // XY compounds: result X must exceed either input alone
        Assert.True(dst[0] > 160);
        // Blue and alpha untouched
        Assert.Equal(200, dst[2]);
        Assert.Equal(0,   dst[3]);
    }

    [Fact]
    public void CompoundNormal_FlatOverlay_LeavesBaseXYUnchanged()
    {
        // Flat overlay (128,128) = zero deviation — adding zero changes nothing
        byte[] dst = [180, 90, 200, 0];
        CompositorService.CompoundNormal(dst, [128, 128, 255, 255], 1, 1);
        Assert.Equal(180, dst[0]);
        Assert.Equal(90,  dst[1]);
    }

    [Fact]
    public void CompoundNormal_ZeroAlpha_LeavesBaseUnchanged()
    {
        byte[] dst      = [160, 128, 200, 0];
        byte[] original = (byte[])dst.Clone();
        CompositorService.CompoundNormal(dst, [255, 0, 180, 0], 1, 1);
        Assert.Equal(original, dst);
    }

    /// <summary>
    /// The whole-skin defect, pinned at the arithmetic. A converted skin mod's normal is composited over a
    /// base that is already that same map — either the mod's own textures or the body mod it was derived
    /// from — so compounding applies every slope twice. Measured in game on one such mod: mean deviation
    /// from flat went 5.95 → 11.83 (R) and 4.73 → 9.39 (G), which is what this reproduces at one texel.
    /// <see cref="NormalMode.Replace"/> is the way out, and it composites through AlphaComposite.
    /// </summary>
    [Fact]
    public void CompoundNormal_OverItself_DoublesTheDeviationFromFlat()
    {
        // A texel tilted +32 in X and −20 in Y away from flat (128,128).
        byte[] skin = [160, 108, 250, 255];

        byte[] compounded = (byte[])skin.Clone();
        CompositorService.CompoundNormal(compounded, skin, 1, 1);
        Assert.Equal(192, compounded[0]);   // 128 + 32 + 32
        Assert.Equal(88,  compounded[1]);   // 128 − 20 − 20

        // Replace mode hands back the map the author painted, exactly.
        byte[] replaced = [200, 60, 180, 255];
        CompositorService.AlphaComposite(replaced, skin, 1, 1);
        Assert.Equal(skin[0], replaced[0]);
        Assert.Equal(skin[1], replaced[1]);
        // And the BLUE channel comes across too — skin.shpk reads it as skin-colour influence, and
        // CompoundNormal never writes it, so a whole-skin normal's blue was silently inherited from
        // whatever body mod happened to sit underneath.
        Assert.Equal(skin[2], replaced[2]);
    }

    [Fact]
    public void CompoundNormal_WithMask_MaskZeroBlocksComposite()
    {
        byte[] dst  = [160, 128, 200, 0];
        byte[] src  = [200, 200, 180, 255];
        byte[] mask = [0,   0,   0,   0];
        byte[] orig = (byte[])dst.Clone();
        CompositorService.CompoundNormal(dst, src, 1, 1, mask);
        Assert.Equal(orig, dst);
    }

    // ── BuildRowDict ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildRowDict_Null_ReturnsEmptyDict()
    {
        Assert.Empty(CompositorService.BuildRowDict(null));
    }

    [Fact]
    public void BuildRowDict_EmptyList_ReturnsEmptyDict()
    {
        Assert.Empty(CompositorService.BuildRowDict([]));
    }

    [Fact]
    public void BuildRowDict_Row1_MapsToIndex0()
    {
        var presets = Presets(row: 1, diffuseA: "#FF0000");
        var dict    = CompositorService.BuildRowDict(presets);

        Assert.True(dict.ContainsKey(0));  // 1-based → 0-based
        Assert.Equal(1f, dict[0].A.DiffuseR, precision: 3);
        Assert.Equal(0f, dict[0].A.DiffuseG, precision: 3);
        Assert.Equal(0f, dict[0].A.DiffuseB, precision: 3);
    }

    [Fact]
    public void BuildRowDict_Row16_MapsToIndex15()
    {
        var presets = Presets(row: 16, diffuseA: "#0000FF");
        var dict    = CompositorService.BuildRowDict(presets);

        Assert.True(dict.ContainsKey(15));
        Assert.Equal(1f, dict[15].A.DiffuseB, precision: 3);
    }

    [Fact]
    public void BuildRowDict_MissingDiffuse_DefaultsToWhite()
    {
        var presets = new List<ColorTableRowPreset>
        {
            new() { Row = 1, SubRowA = new() { Diffuse = null, Emissive = 0.5f } }
        };
        var dict = CompositorService.BuildRowDict(presets);

        Assert.Equal(1f, dict[0].A.DiffuseR, precision: 3);
        Assert.Equal(1f, dict[0].A.DiffuseG, precision: 3);
        Assert.Equal(1f, dict[0].A.DiffuseB, precision: 3);
        Assert.Equal(0.5f, dict[0].A.Emissive, precision: 5);
    }

    [Fact]
    public void BuildRowDict_SubRowBSeparateFromA()
    {
        var presets = new List<ColorTableRowPreset>
        {
            new()
            {
                Row    = 1,
                SubRowA = new() { Diffuse = "#FF0000", Emissive = 0.5f, Opacity = 10 },
                SubRowB = new() { Diffuse = "#0000FF", Emissive = 0.2f, Opacity = -5 }
            }
        };
        var dict = CompositorService.BuildRowDict(presets);
        var row  = dict[0];

        Assert.Equal(1f,   row.A.DiffuseR, precision: 3);
        Assert.Equal(0.5f, row.A.Emissive,  precision: 5);
        Assert.Equal(10,   row.A.Opacity);
        Assert.Equal(1f,   row.B.DiffuseB, precision: 3);
        Assert.Equal(0.2f, row.B.Emissive,  precision: 5);
        Assert.Equal(-5,   row.B.Opacity);
    }

    [Fact]
    public void BuildRowDict_MultipleRows()
    {
        var presets = new List<ColorTableRowPreset>
        {
            new() { Row = 1,  SubRowA = new() { Diffuse = "#FF0000" } },
            new() { Row = 16, SubRowA = new() { Diffuse = "#0000FF" } }
        };
        var dict = CompositorService.BuildRowDict(presets);

        Assert.Equal(2, dict.Count);
        Assert.True(dict.ContainsKey(0));
        Assert.True(dict.ContainsKey(15));
    }

    // ── ScaleOverlayAlpha ─────────────────────────────────────────────────────

    [Fact]
    public void ScaleOverlayAlpha_ZeroOpacity_ReturnsSameAlphas()
    {
        var src    = new byte[] { 255, 255, 255, 128, 255, 255, 255, 200 };
        var result = CompositorService.ScaleOverlayAlpha(src, 0);
        Assert.Equal(128, result[3]);
        Assert.Equal(200, result[7]);
    }

    [Fact]
    public void ScaleOverlayAlpha_PositiveOpacity_IncreasesAlpha()
    {
        // alpha=128, opacity=+50: newA = 128 + (255-128)*50/100 = 128+63 = 191
        var result = CompositorService.ScaleOverlayAlpha(RGBA(255, 255, 255, 128), 50);
        Assert.Equal(191, result[3]);
    }

    [Fact]
    public void ScaleOverlayAlpha_NegativeOpacity_DecreasesAlpha()
    {
        // alpha=200, opacity=-50: newA = 200 * 50/100 = 100
        var result = CompositorService.ScaleOverlayAlpha(RGBA(255, 255, 255, 200), -50);
        Assert.Equal(100, result[3]);
    }

    [Fact]
    public void ScaleOverlayAlpha_PositiveOpacity_ZeroAlphaPixelStaysZero()
    {
        // fully-transparent pixel stays transparent even with positive opacity
        var result = CompositorService.ScaleOverlayAlpha(RGBA(255, 255, 255, 0), 100);
        Assert.Equal(0, result[3]);
    }

    [Fact]
    public void ScaleOverlayAlpha_NegativeOpacity_ZeroAlphaStaysZero()
    {
        var result = CompositorService.ScaleOverlayAlpha(RGBA(255, 255, 255, 0), -50);
        Assert.Equal(0, result[3]);
    }

    [Fact]
    public void ScaleOverlayAlpha_FullPositive_ClampsAt255()
    {
        // alpha=200, opacity=+100: newA = 200 + (255-200)*100/100 = 200+55 = 255
        var result = CompositorService.ScaleOverlayAlpha(RGBA(255, 255, 255, 200), 100);
        Assert.Equal(255, result[3]);
    }

    [Fact]
    public void ScaleOverlayAlpha_DoesNotMutateSrc()
    {
        var src = RGBA(255, 255, 255, 128);
        CompositorService.ScaleOverlayAlpha(src, 50);
        Assert.Equal(128, src[3]);
    }

    // ── ApplyCoverageMask (cov' = cov*W + T, gated by base alpha > 0) ──────────
    // A mask SETS the overlay's opacity explicitly within its alpha region (additive): it can force
    // opacity over a SHEER area, not only reduce it. W = original-coverage survival (Π(1-aᵢ)); T =
    // accumulated gray·a target. cov' = cov*W + T (clamped to 255) — but ONLY where the base overlay
    // already has coverage (alpha > 0). Where base alpha = 0 the pixel stays 0, so a mask can never
    // paint onto bare skin. Mask alpha black → W=255, T=0 → cov' = cov (no effect); alpha white →
    // W=0, cov' = gray target (gated by base > 0).

    [Fact]
    public void ApplyCoverageMask_NullMap_ReturnsSameReference()
    {
        var cov = RGBA(255, 255, 255, 200);
        Assert.Same(cov, CompositorService.ApplyCoverageMask(cov, null, null));
    }

    [Fact]
    public void ApplyCoverageMask_BaseTransparent_StaysTransparent_EvenUnderWhite()
    {
        // THE GATE: base coverage 0 (bare skin above the stocking, or a fishnet hole) stays 0 even
        // when the mask is fully applied white (W=0, T=255). A mask can boost a sheer area to opaque
        // but can NEVER create coverage where the overlay is absent.
        var cov    = RGBA(255, 255, 255, 0);
        var result = CompositorService.ApplyCoverageMask(cov, new byte[] { 0 }, new byte[] { 255 });
        Assert.Equal(0, result[3]);
    }

    [Fact]
    public void ApplyCoverageMask_BaseSheer_WhiteForcesOpaque()
    {
        // Additive over a SHEER base: alpha=40 (visible but sheer), mask white (W=0, T=255) → forced
        // to 255. This is what paints the opaque bands at the bottom of a sheer stocking.
        var cov    = RGBA(255, 255, 255, 40);
        var result = CompositorService.ApplyCoverageMask(cov, new byte[] { 0 }, new byte[] { 255 });
        Assert.Equal(255, result[3]);
    }

    [Fact]
    public void ApplyCoverageMask_AlphaBlack_LeavesCoverageUnchanged()
    {
        // alpha=0 → W=255, T=0 → keep=255 → cov' = cov (mask does nothing)
        var cov    = RGBA(255, 255, 255, 200);
        var result = CompositorService.ApplyCoverageMask(cov, new byte[] { 255 }, new byte[] { 0 });
        Assert.Equal(200, result[3]);
    }

    [Fact]
    public void ApplyCoverageMask_AlphaWhiteGrayBlack_RemovesCoverage()
    {
        // alpha=255, gray=0 → W=0, T=0 → keep=0 → cov' = 0 (rip / hole)
        var cov    = RGBA(255, 255, 255, 200);
        var result = CompositorService.ApplyCoverageMask(cov, new byte[] { 0 }, new byte[] { 0 });
        Assert.Equal(0, result[3]);
    }

    [Fact]
    public void ApplyCoverageMask_AlphaWhiteGrayWhite_ForcesOpaque()
    {
        // alpha=255, gray=255 → W=0, T=255 → cov' = 255 (forced opaque, above the sheer base of 40)
        var cov    = RGBA(255, 255, 255, 40);
        var result = CompositorService.ApplyCoverageMask(cov, new byte[] { 0 }, new byte[] { 255 });
        Assert.Equal(255, result[3]);
    }

    [Fact]
    public void ApplyCoverageMask_AlphaWhiteGrayMid_SetsToTarget()
    {
        // alpha=255, gray=128 → W=0, T=128 → cov' = 128 (set to the gray target, regardless of base)
        var cov    = RGBA(255, 255, 255, 200);
        var result = CompositorService.ApplyCoverageMask(cov, new byte[] { 0 }, new byte[] { 128 });
        Assert.Equal(128, result[3]);
    }

    [Fact]
    public void ApplyCoverageMask_AlphaHalfGrayBlack_HalfwayRemoval()
    {
        // a=128 → W=127, T=0 → keep=127 → cov' = 200*127/255 ≈ 99
        var cov    = RGBA(255, 255, 255, 200);
        var result = CompositorService.ApplyCoverageMask(cov, new byte[] { 127 }, new byte[] { 0 });
        Assert.Equal(200 * 127 / 255, result[3]);
    }

    [Fact]
    public void ApplyCoverageMask_OnlyTouchesAlpha_NotColor()
    {
        var cov    = RGBA(10, 20, 30, 200);
        var result = CompositorService.ApplyCoverageMask(cov, new byte[] { 0 }, new byte[] { 0 });
        Assert.Equal(10, result[0]);
        Assert.Equal(20, result[1]);
        Assert.Equal(30, result[2]);
    }

    [Fact]
    public void ApplyCoverageMask_DoesNotMutateSource()
    {
        var cov = RGBA(255, 255, 255, 200);
        CompositorService.ApplyCoverageMask(cov, new byte[] { 0 }, new byte[] { 0 });
        Assert.Equal(200, cov[3]); // original untouched (clone returned)
    }

    [Fact]
    public void ApplyCoverageMask_MultiplePixels_AppliesPerPixel()
    {
        // pixel 0: alpha black (keep=255) → unchanged; pixel 1: alpha white gray black (keep=0) → 0
        var cov    = new byte[] { 255, 0, 0, 255,  0, 255, 0, 255 };
        var result = CompositorService.ApplyCoverageMask(cov, new byte[] { 255, 0 }, new byte[] { 0, 0 });
        Assert.Equal(255, result[3]);
        Assert.Equal(0,   result[7]);
    }

    // ── ApplyIndexedOpacity ───────────────────────────────────────────────────

    [Fact]
    public void ApplyIndexedOpacity_PositiveOpacity_IncreasesAlpha()
    {
        var src  = RGBA(255, 255, 255, 128);
        var idx  = RGBA(0,   255, 0,   255); // R=0→pair0, G=255→100% A row
        var rows = RowDict(pairIdx: 0, opA: 50, opB: 0);

        var result = CompositorService.ApplyIndexedOpacity(src, idx, rows);

        float a    = 128f / 255f;
        float newA = a + (1f - a) * 50f / 100f;
        Assert.Equal((byte)(newA * 255f + 0.5f), result[3]);
    }

    [Fact]
    public void ApplyIndexedOpacity_NegativeOpacity_DecreasesAlpha()
    {
        var src  = RGBA(255, 255, 255, 200);
        var idx  = RGBA(0,   255, 0,   255);
        var rows = RowDict(pairIdx: 0, opA: -50, opB: 0);

        var result = CompositorService.ApplyIndexedOpacity(src, idx, rows);

        float a    = 200f / 255f;
        float newA = a * (100f - 50f) / 100f;
        Assert.Equal((byte)(newA * 255f + 0.5f), result[3]);
    }

    [Fact]
    public void ApplyIndexedOpacity_ZeroAlphaPixel_Skipped()
    {
        var src  = RGBA(255, 255, 255, 0);    // transparent
        var idx  = RGBA(0,   255, 0,   255);
        var rows = RowDict(pairIdx: 0, opA: 50, opB: 0);

        var result = CompositorService.ApplyIndexedOpacity(src, idx, rows);
        Assert.Equal(0, result[3]);
    }

    [Fact]
    public void ApplyIndexedOpacity_UnmappedPair_Unchanged()
    {
        // idx.R = 85 → pairIdx = 85/17 = 5, but no row 5 in dict
        var src  = RGBA(255, 255, 255, 200);
        var idx  = RGBA(85,  255, 0,   255);
        var rows = RowDict(pairIdx: 0, opA: 100, opB: 0); // only pair 0 exists

        var result = CompositorService.ApplyIndexedOpacity(src, idx, rows);
        Assert.Equal(200, result[3]); // unchanged
    }

    [Fact]
    public void ApplyIndexedOpacity_BlendsBetweenAandB()
    {
        // G=0 → blendA=0 → use B row opacity
        var src  = RGBA(255, 255, 255, 200);
        var idx  = RGBA(0,   0,   0,   255); // R=0→pair0, G=0→100% B
        var rows = new Dictionary<int, ColorTableRowOverride>
        {
            [0] = new() { A = new() { Opacity = 100 }, B = new() { Opacity = -50 } }
        };

        var result = CompositorService.ApplyIndexedOpacity(src, idx, rows);

        // blendA = 0 → op = B.Opacity = -50 → newA = (200/255)*(50/100)
        float a    = 200f / 255f;
        float newA = a * 50f / 100f;
        Assert.Equal((byte)(newA * 255f + 0.5f), result[3]);
    }

    [Fact]
    public void ApplyIndexedOpacity_DoesNotMutateSrc()
    {
        var src  = RGBA(255, 255, 255, 200);
        var orig = (byte[])src.Clone();
        CompositorService.ApplyIndexedOpacity(src, RGBA(0, 255, 0, 255), RowDict(0, 50, 0));
        Assert.Equal(orig, src);
    }

    // ── ApplyIndexedOverlay ───────────────────────────────────────────────────

    [Fact]
    public void ApplyIndexedOverlay_Diffuse_TintsWithRowAColor()
    {
        // R=0 → pair0; G=255 → blendA=1 → full A row; row A is pure red
        var baseTex = RGBA(0,   0, 0, 255);
        var ov      = RGBA(255, 255, 255, 255);  // white overlay, full alpha
        var idx     = RGBA(0,   255, 0,  255);   // pair0, 100% A
        var rows    = new Dictionary<int, ColorTableRowOverride>
        {
            [0] = new()
            {
                A = new() { DiffuseR = 1f, DiffuseG = 0f, DiffuseB = 0f }, // red
                B = new() { DiffuseR = 0f, DiffuseG = 1f, DiffuseB = 0f }  // green
            }
        };

        CompositorService.ApplyIndexedOverlay(baseTex, ov, idx, rows, isNormal: false, 1, 1);

        Assert.Equal(255, baseTex[0]); // R = full (red row A)
        Assert.Equal(0,   baseTex[1]); // G = 0
        Assert.Equal(0,   baseTex[2]); // B = 0
    }

    [Fact]
    public void ApplyIndexedOverlay_Diffuse_BlendsAandB()
    {
        // G=128 → blendA ≈ 0.502 → lerp between B(green) and A(red)
        var baseTex = RGBA(0,   0, 0, 255);
        var ov      = RGBA(255, 255, 255, 255);
        var idx     = RGBA(0,   128, 0, 255);   // pair0, ~50% blend
        var rows    = new Dictionary<int, ColorTableRowOverride>
        {
            [0] = new()
            {
                A = new() { DiffuseR = 1f, DiffuseG = 0f, DiffuseB = 0f }, // red
                B = new() { DiffuseR = 0f, DiffuseG = 0f, DiffuseB = 1f }  // blue
            }
        };

        CompositorService.ApplyIndexedOverlay(baseTex, ov, idx, rows, isNormal: false, 1, 1);

        // blendA ≈ 0.5 → R ≈ 0.5, B ≈ 0.5 → ~127 each
        Assert.InRange(baseTex[0], 120, 135);
        Assert.InRange(baseTex[2], 120, 135);
    }

    [Fact]
    public void ApplyIndexedOverlay_MissingRow_UsesDefaultWhite()
    {
        // idx.R = 17 → pairIdx = 1, but only pair 0 exists → defaults to white
        var baseTex = RGBA(0,   0, 0, 255);
        var ov      = RGBA(255, 255, 255, 255);
        var idx     = RGBA(17,  255, 0,  255);  // pair1, not in dict
        var rows    = new Dictionary<int, ColorTableRowOverride>
        {
            [0] = new() { A = new() { DiffuseR = 1f, DiffuseG = 0f, DiffuseB = 0f } }
        };

        CompositorService.ApplyIndexedOverlay(baseTex, ov, idx, rows, isNormal: false, 1, 1);

        // Default ColorTableRowOverride has white (1,1,1) sub-rows
        Assert.Equal(255, baseTex[0]);
        Assert.Equal(255, baseTex[1]);
        Assert.Equal(255, baseTex[2]);
    }

    [Fact]
    public void ApplyIndexedOverlay_ZeroAlphaPixel_Skipped()
    {
        var baseTex  = RGBA(100, 100, 100, 255);
        var original = (byte[])baseTex.Clone();
        var ov       = RGBA(255, 255, 255, 0);   // transparent
        var idx      = RGBA(0,   255, 0,   255);
        var rows     = new Dictionary<int, ColorTableRowOverride>
        {
            [0] = new() { A = new() { DiffuseR = 0f, DiffuseG = 0f, DiffuseB = 0f } }
        };

        CompositorService.ApplyIndexedOverlay(baseTex, ov, idx, rows, isNormal: false, 1, 1);

        Assert.Equal(original, baseTex);
    }

    // ── SanitizeName ─────────────────────────────────────────────────────────

    [Fact]
    public void SanitizeName_ExtractsFilenameWithoutExtension()
    {
        var result = CompositorService.SanitizeName("chara/human/c0101/mt_c0101b0001_b.mtrl");
        Assert.Equal("mt_c0101b0001_b", result);
    }

    [Fact]
    public void SanitizeName_InvalidPathChars_ReplacedWithUnderscore()
    {
        // Simulate a game path that contains characters disallowed in Windows file names.
        // We use a game path that contains a colon-like character (not common but let's use < >).
        var result = CompositorService.SanitizeName("chara/test<body>mat.mtrl");
        Assert.DoesNotContain("<", result);
        Assert.DoesNotContain(">", result);
    }

    // ── BodyCodeFromCustomize ─────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 1, 0, "c0101")]  // Hyur Midlander male
    [InlineData(1, 1, 1, "c0201")]  // Hyur Midlander female
    [InlineData(1, 2, 0, "c0301")]  // Hyur Highlander male
    [InlineData(1, 2, 1, "c0401")]  // Hyur Highlander female
    [InlineData(2, 1, 0, "c0101")]  // Elezen male → shares mid body
    [InlineData(2, 1, 1, "c0201")]  // Elezen female
    [InlineData(3, 1, 0, "c0101")]  // Lalafell male
    [InlineData(3, 1, 1, "c0201")]  // Lalafell female
    [InlineData(4, 1, 0, "c0101")]  // Miqo'te male
    [InlineData(4, 1, 1, "c0201")]  // Miqo'te female
    [InlineData(5, 1, 0, "c0101")]  // Roegadyn male
    [InlineData(5, 1, 1, "c0201")]  // Roegadyn female
    [InlineData(6, 1, 0, "c1301")]  // Au Ra male
    [InlineData(6, 1, 1, "c1401")]  // Au Ra female
    [InlineData(7, 1, 0, "c1501")]  // Hrothgar male
    [InlineData(7, 1, 1, "c1601")]  // Hrothgar female
    [InlineData(8, 1, 0, "c1701")]  // Viera male
    [InlineData(8, 1, 1, "c1801")]  // Viera female
    public void BodyCodeFromCustomize_KnownRaces_ReturnCorrectCode(
        byte race, byte tribe, byte sex, string expected)
    {
        var result = CompositorService.BodyCodeFromCustomize(race, tribe, sex);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BodyCodeFromCustomize_UnknownRace_ReturnsNull()
    {
        Assert.Null(CompositorService.BodyCodeFromCustomize(99, 1, 0));
        Assert.Null(CompositorService.BodyCodeFromCustomize(0,  1, 0));
        Assert.Null(CompositorService.BodyCodeFromCustomize(9,  1, 0));
    }

    // ── BlurCoverage ──────────────────────────────────────────────────────────

    [Fact]
    public void BlurCoverage_FlatPlane_StaysFlat()
    {
        // A uniform plane must be unchanged by a box blur (edge clamping preserves the constant).
        int w = 64, h = 64;
        var src = new byte[w * h];
        Array.Fill(src, (byte)200);

        var blurred = CompositorService.BlurCoverage(src, w, h, radius: 4);

        Assert.All(blurred, v => Assert.Equal(200, v));
    }

    [Fact]
    public void BlurCoverage_SingleWhiteSquare_BleedsIntoSurroundingRing()
    {
        // A small white square on black: after the blur, pixels just outside the square (previously 0)
        // must become non-zero (the coverage bled outward), while a far-away corner stays black.
        int w = 64, h = 64;
        var src = new byte[w * h];
        for (int y = 28; y < 36; y++)
            for (int x = 28; x < 36; x++)
                src[y * w + x] = 255;

        var blurred = CompositorService.BlurCoverage(src, w, h, radius: 4);

        Assert.True(blurred[27 * w + 30] > 0, "pixel just outside the square should receive bled coverage");
        Assert.True(blurred[30 * w + 27] > 0, "pixel just left of the square should receive bled coverage");
        Assert.Equal(0, blurred[0]);                  // far corner untouched
        Assert.True(blurred[31 * w + 31] > 0);        // interior still lit
    }

    // ── ApplyAmbientOcclusion ───────────────────────────────────────────────────

    [Fact]
    public void ApplyAmbientOcclusion_DarkensOutsideStrapOnly_AlphaUntouched()
    {
        // 3 pixels: [0] under the strap interior, [1] just outside (blurred spread present),
        // [2] far away (no spread). Only [1] should darken; alpha never changes.
        int w = 3, h = 1;
        var baseD = new byte[]
        {
            200, 200, 200, 255,   // p0: under strap
            200, 200, 200, 255,   // p1: outside, in the halo
            200, 200, 200, 255,   // p2: far away
        };
        var strap   = new byte[] { 255, 0,   0   };  // p0 is the strap; p1/p2 are skin
        var blurred = new byte[] { 255, 200, 0   };  // spread reaches p1 but not p2

        CompositorService.ApplyAmbientOcclusion(baseD, strap, blurred, w, h, strength: 0.5f);

        Assert.Equal(200, baseD[0]);   // under strap (s=1 → halo 0): unchanged
        Assert.True(baseD[4] < 200);   // outside, in halo: darkened
        Assert.Equal(200, baseD[8]);   // far away (blur 0): unchanged
        Assert.Equal(255, baseD[3]);   // alpha untouched
        Assert.Equal(255, baseD[7]);
        Assert.Equal(255, baseD[11]);
    }

    [Fact]
    public void ApplyAmbientOcclusion_ZeroStrength_NoChange()
    {
        var baseD   = RGBA(200, 150, 100, 255);
        var strap   = new byte[] { 0 };
        var blurred = new byte[] { 255 };

        CompositorService.ApplyAmbientOcclusion(baseD, strap, blurred, 1, 1, strength: 0f);

        Assert.Equal(new byte[] { 200, 150, 100, 255 }, baseD);
    }

    // ── ApplyNormalIndent ───────────────────────────────────────────────────────

    [Fact]
    public void ApplyNormalIndent_LeansNormalTowardStrap_OnSkinSideOnly()
    {
        // 4×1 row: strap on the left two texels, skin on the right two. The blurred coverage ramps down
        // left→right. On the skin side the surface should lean toward the strap (−X → R below neutral 128);
        // texels under the strap (edge==0) are untouched. Blue (skin-color) and G stay neutral (h=1 → gy=0).
        int w = 4, h = 1;
        var baseN = new byte[]
        {
            128, 128, 255, 128,   // x0 under strap
            128, 128, 255, 128,   // x1 under strap
            128, 128, 255, 128,   // x2 skin
            128, 128, 255, 128,   // x3 skin
        };
        var strap   = new byte[] { 255, 255, 0, 0 };
        var blurred = new byte[] { 255, 192, 64, 0 };

        CompositorService.ApplyNormalIndent(baseN, blurred, strap, w, h, strength: 0.5f);

        Assert.Equal(128, baseN[0]);        // x0: under strap, untouched
        Assert.Equal(128, baseN[4]);        // x1: under strap, untouched
        Assert.True(baseN[8]  < 128);       // x2: skin leans −X toward the strap
        Assert.True(baseN[12] < 128);       // x3: skin leans −X toward the strap
        Assert.Equal(255, baseN[10]);       // blue untouched
        Assert.Equal(255, baseN[14]);
        Assert.Equal(128, baseN[9]);        // green untouched (single row → no vertical gradient)
        Assert.Equal(128, baseN[13]);
    }

    [Fact]
    public void ApplyNormalIndent_FlatCoverage_NoChange()
    {
        // No edges (flat blurred plane, no strap) → zero gradient → normal unchanged.
        int w = 4, h = 4;
        var baseN = new byte[w * h * 4];
        for (int p = 0; p < w * h; p++) { baseN[p * 4] = 128; baseN[p * 4 + 1] = 128; baseN[p * 4 + 2] = 255; }
        var expected = (byte[])baseN.Clone();
        var strap   = new byte[w * h];               // all skin
        var blurred = new byte[w * h];
        Array.Fill(blurred, (byte)128);              // flat

        CompositorService.ApplyNormalIndent(baseN, blurred, strap, w, h, strength: 1f);

        Assert.Equal(expected, baseN);
    }

    [Fact]
    public void ApplyNormalIndent_ZeroDepth_NoChange()
    {
        var baseN   = RGBA(128, 128, 255, 128);
        var strap   = new byte[] { 0 };
        var blurred = new byte[] { 200 };

        CompositorService.ApplyNormalIndent(baseN, blurred, strap, 1, 1, strength: 0f);

        Assert.Equal(new byte[] { 128, 128, 255, 128 }, baseN);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] RGBA(byte r, byte g, byte b, byte a) => [r, g, b, a];

    private static ColorTableSubRow Row(
        float r, float g, float b, float emissive = 0f, int opacity = 0) =>
        new() { DiffuseR = r, DiffuseG = g, DiffuseB = b, Emissive = emissive, Opacity = opacity };

    private static List<ColorTableRowPreset> Presets(int row, string? diffuseA = null, float emissiveA = 0f)
    {
        return
        [
            new ColorTableRowPreset
            {
                Row    = row,
                SubRowA = new ColorTableSubRowPreset { Diffuse = diffuseA, Emissive = emissiveA }
            }
        ];
    }

    private static Dictionary<int, ColorTableRowOverride> RowDict(int pairIdx, int opA, int opB)
    {
        return new Dictionary<int, ColorTableRowOverride>
        {
            [pairIdx] = new() { A = new() { Opacity = opA }, B = new() { Opacity = opB } }
        };
    }

    // ── ContentTag ───────────────────────────────────────────────────────────
    // The output filename suffix. Stability is the whole point: a sync plugin keys transfers on content,
    // so identical bytes must produce an identical name (no re-upload), and any difference that reaches
    // the written file must produce a different one (or the game keeps rendering its cached texture).

    [Fact]
    public void ContentTag_IdenticalInput_IsStable()
    {
        var a = new byte[1000]; var b = new byte[1000];
        for (int i = 0; i < a.Length; i++) { a[i] = (byte)(i * 7); b[i] = (byte)(i * 7); }

        Assert.Equal(CompositorService.ContentTag(a, 512, 512, 1),
                     CompositorService.ContentTag(b, 512, 512, 1));
    }

    [Fact]
    public void ContentTag_IsEightHexChars()
    {
        var tag = CompositorService.ContentTag(new byte[64], 4, 4);
        Assert.Equal(8, tag.Length);
        Assert.All(tag, c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public void ContentTag_OneChangedByte_ChangesTag()
    {
        var a = new byte[1000];
        var b = (byte[])a.Clone();
        b[997] = 1;   // in the ragged tail past the last whole 8-byte word

        Assert.NotEqual(CompositorService.ContentTag(a), CompositorService.ContentTag(b));
    }

    [Fact]
    public void ContentTag_TailBytesAreNotIgnored()
    {
        // 1003 bytes = 125 whole words + 3 loose. A word-at-a-time hash that forgets the remainder would
        // give these two the same name and the peer would never see the edit.
        var a = new byte[1003];
        var b = (byte[])a.Clone();
        b[1002] = 0xFF;

        Assert.NotEqual(CompositorService.ContentTag(a), CompositorService.ContentTag(b));
    }

    [Fact]
    public void ContentTag_SaltDistinguishesDimensionsAndEncoding()
    {
        var data = new byte[512];

        // Same pixels, different declared size — a resize writes different bytes.
        Assert.NotEqual(CompositorService.ContentTag(data, 128, 128, 0),
                        CompositorService.ContentTag(data, 256, 64, 0));

        // Same pixels, compression toggled — BC7 vs B8G8R8A8 must not share a filename.
        Assert.NotEqual(CompositorService.ContentTag(data, 128, 128, 0),
                        CompositorService.ContentTag(data, 128, 128, 1));
    }

    // ── CanRenderAsShell — which overlays a shell can be cut for ───────────────
    // Every surface the shell builder knows: the character's own body, face, hair, tail and ears. Nothing
    // else, ever — gear, accessories and weapons have no shell path.
    //
    // Face was FALSE here until the multi-surface work landed, and the reason is worth keeping: while the
    // builder could only cut from the body, promoting a face overlay cut a BODY shell and pasted the face
    // art across the whole character. The veto was the correct answer to "there is nowhere to put this",
    // not a statement that faces can't glow.

    private static OverlayDescriptor Target(params string[] mtrl)
        => new() { MaterialGamePaths = [.. mtrl] };

    [Theory]
    [InlineData("chara/human/c1401/obj/body/b0001/material/v0001/mt_c1401b0001_bibo.mtrl")]
    [InlineData("chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_fac_a.mtrl")]
    [InlineData("chara/human/c1401/obj/hair/h0133/material/v0001/mt_c1401h0133_hir_a.mtrl")]
    [InlineData("chara/human/c1401/obj/tail/t0001/material/v0001/mt_c1401t0001_etc_a.mtrl")]
    [InlineData("chara/human/c1801/obj/zear/z0001/material/mt_c1801z0001_zer_a.mtrl")]
    public void CanRenderAsShell_OwnSkinSurfaces_Yes(string mtrl)
        => Assert.True(CompositorService.CanRenderAsShell(Target(mtrl)));

    [Fact]
    public void CanRenderAsShell_BodyUvThroughAnEquipmentSlot_Yes()   // body mods route skin through gear paths
        => Assert.True(CompositorService.CanRenderAsShell(
            Target("chara/equipment/e0001/material/v0001/mt_c0201e0001_top_bibo.mtrl")));

    [Theory]
    [InlineData("chara/equipment/e6039/material/v0001/mt_c0201e6039_sho_a.mtrl")]
    [InlineData("chara/accessory/a0053/material/v0001/mt_c0101a0053_rir_a.mtrl")]
    [InlineData("chara/weapon/w0801/obj/body/b0006/material/v0001/mt_w0801b0006_a.mtrl")]
    [InlineData("chara/monster/m0361/obj/body/b0001/material/v0001/mt_m0361b0001_a.mtrl")]
    public void CanRenderAsShell_NotOurSkin_No(string mtrl)
        => Assert.False(CompositorService.CanRenderAsShell(Target(mtrl)));

    [Fact]
    public void CanRenderAsShell_MixedFaceAndBody_Yes()   // both halves have somewhere to go now
        => Assert.True(CompositorService.CanRenderAsShell(
            Target("chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_fac_a.mtrl",
                   "chara/human/c1401/obj/body/b0001/material/v0001/mt_c1401b0001_bibo.mtrl")));

    [Fact]
    public void CanRenderAsShell_NoMaterialNamed_Yes()   // can't place it — keep the prior behaviour
        => Assert.True(CompositorService.CanRenderAsShell(Target()));

    // ── SnapIndexRowsToDefined ───────────────────────────────────────────────

    /// <summary>An index texture with `red` per texel, green pinned to 255, opaque.</summary>
    private static byte[] IndexOf(params byte[] reds)
    {
        var t = new byte[reds.Length * 4];
        for (int i = 0; i < reds.Length; i++)
        { t[i * 4] = reds[i]; t[i * 4 + 1] = 255; t[i * 4 + 3] = 255; }
        return t;
    }

    [Fact]
    public void SnapIndexRows_UndefinedRow_TakesNeighboursRow()
    {
        // red 255 → pair 16 (defined), red 0 → pair 1 (not defined) → dilated over from the left.
        var index = IndexOf(255, 0, 0);
        CompositorService.SnapIndexRowsToDefined(index, 3, 1, new[] { 16 });
        Assert.Equal(255, index[4]);
        Assert.Equal(255, index[8]);
    }

    [Fact]
    public void SnapIndexRows_DefinedRow_LeftAlone()
    {
        // Both pairs are configured, so the boundary between them is a real one — not a repair target.
        var index = IndexOf(255, 0, 0);
        CompositorService.SnapIndexRowsToDefined(index, 3, 1, new[] { 1, 16 });
        Assert.Equal(0, index[4]);
        Assert.Equal(0, index[8]);
    }

    [Fact]
    public void SnapIndexRows_UnauthoredTexel_RepairedEvenWhenItsRowIsDefined()
    {
        // The regression this exists for: a mask shell has no _id, so the merge SYNTHESIZES one and paints
        // over only the texels a mask claims. The unclaimed remainder is a real selector, and once the
        // colorset happens to configure that row the numeric test alone calls it authored and the fringe
        // survives — as white, when the seeded pair's other sub-row was never set.
        var index = IndexOf(255, 0, 0);
        var authored = new[] { true, false, false };
        CompositorService.SnapIndexRowsToDefined(index, 3, 1, new[] { 1, 16 }, authored: authored);
        Assert.Equal(255, index[4]);
        Assert.Equal(255, index[8]);
    }

    [Fact]
    public void SnapIndexRows_NullAuthored_KeepsPriorBehaviour()
    {
        var withNull = IndexOf(255, 0, 0);
        var withAllTrue = IndexOf(255, 0, 0);
        CompositorService.SnapIndexRowsToDefined(withNull, 3, 1, new[] { 1, 16 });
        CompositorService.SnapIndexRowsToDefined(withAllTrue, 3, 1, new[] { 1, 16 },
            authored: new[] { true, true, true });
        Assert.Equal(withAllTrue, withNull);
    }

    [Fact]
    public void SnapIndexRows_ShortAuthored_Ignored()   // a mismatched array must not throw or half-apply
    {
        var index = IndexOf(255, 0, 0);
        CompositorService.SnapIndexRowsToDefined(index, 3, 1, new[] { 1, 16 }, authored: new[] { false });
        Assert.Equal(0, index[4]);
    }

    [Fact]
    public void SnapIndexRows_DilateBudget_StopsSpreading()
    {
        var index = IndexOf(255, 0, 0, 0, 0);
        CompositorService.SnapIndexRowsToDefined(index, 5, 1, new[] { 16 }, dilate: 2);
        Assert.Equal(255, index[4]);
        Assert.Equal(255, index[8]);
        Assert.Equal(0, index[12]);    // past the budget — left unmapped, and never drawn
    }

    /// <summary>
    /// The straightforward version of the dilation: rescan the whole map every pass, filling any invalid
    /// texel that has a valid neighbour in the pass-start snapshot. The shipping code walks a frontier
    /// instead — the same fill, minus the texels that provably cannot be filled — because the full scan
    /// stopped exiting early once `authored` marked a mask shell's whole background invalid. This is the
    /// reference the optimisation has to stay byte-identical to.
    /// </summary>
    private static void NaiveSnap(byte[] index, int w, int h, int[] definedRows, int dilate, bool[]? authored)
    {
        var isDefined = new bool[17];
        foreach (var r in definedRows)
            if (r >= 1 && r <= 16) isDefined[r] = true;

        int n = w * h;
        var valid = new bool[n];
        for (int p = 0; p < n; p++)
            valid[p] = (authored?[p] ?? true) && isDefined[index[p * 4] / 17 + 1];

        var snap = new bool[n];
        for (int it = 0; it < dilate; it++)
        {
            Array.Copy(valid, snap, n);
            int filled = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = y * w + x;
                    if (snap[p]) continue;
                    int src = -1;
                    if (x > 0 && snap[p - 1]) src = p - 1;
                    else if (x < w - 1 && snap[p + 1]) src = p + 1;
                    else if (y > 0 && snap[p - w]) src = p - w;
                    else if (y < h - 1 && snap[p + w]) src = p + w;
                    if (src < 0) continue;
                    index[p * 4] = index[src * 4];
                    index[p * 4 + 1] = index[src * 4 + 1];
                    valid[p] = true;
                    filled++;
                }
            if (filled == 0) break;
        }
    }

    [Fact]
    public void SnapIndexRows_FrontierWalk_MatchesTheNaiveFullScan()
    {
        // Randomised rather than hand-picked: what the frontier has to get right is an invariant about
        // which texels are reachable, and the cases that break such a thing are odd shapes — islands,
        // single-texel rows, a region touching an edge — not the tidy ones anybody thinks to write down.
        var rng = new Random(20260831);
        for (int trial = 0; trial < 400; trial++)
        {
            int w = 1 + rng.Next(40), h = 1 + rng.Next(40);
            int n = w * h;

            var index = new byte[n * 4];
            for (int p = 0; p < n; p++)
            {
                index[p * 4]     = (byte)rng.Next(256);   // row selector
                index[p * 4 + 1] = (byte)rng.Next(256);   // sub-row weight
                index[p * 4 + 2] = (byte)rng.Next(256);   // must survive untouched
                index[p * 4 + 3] = 255;
            }

            var defined = Enumerable.Range(1, 16).Where(_ => rng.Next(3) == 0).ToArray();
            if (defined.Length == 0) defined = new[] { 1 + rng.Next(16) };

            bool[]? authored = null;
            if (rng.Next(2) == 0)
            {
                authored = new bool[n];
                // Biased toward contiguous blobs, which is what mask coverage actually looks like, with
                // enough noise to produce islands and ragged edges.
                for (int p = 0; p < n; p++) authored[p] = rng.Next(100) < 70;
            }

            int dilate = rng.Next(4);

            var mine = (byte[])index.Clone();
            var reference = (byte[])index.Clone();
            CompositorService.SnapIndexRowsToDefined(mine, w, h, defined, dilate, authored);
            NaiveSnap(reference, w, h, defined, dilate, authored);

            Assert.True(reference.AsSpan().SequenceEqual(mine),
                $"trial {trial}: w={w} h={h} dilate={dilate} authored={(authored == null ? "null" : "set")} "
                + $"rows=[{string.Join(",", defined)}]");
        }
    }

    [Fact]
    public void SnapIndexRows_CarriesGreenWithTheRow()
    {
        // Green is the A/B weight; a repaired texel has to take the pair AND the weight, or it lands on the
        // sub-row the author never set.
        var index = IndexOf(255, 0);
        index[1] = 40;                 // the valid texel leans toward sub-row B
        CompositorService.SnapIndexRowsToDefined(index, 2, 1, new[] { 16 });
        Assert.Equal(255, index[4]);
        Assert.Equal(40, index[5]);
    }
}
