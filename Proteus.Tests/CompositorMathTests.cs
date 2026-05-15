using System;
using System.Collections.Generic;
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

    // ── ApplyFlatEmissive ─────────────────────────────────────────────────────

    [Fact]
    public void ApplyFlatEmissive_ZeroEmissive_LeavesAlphaUnchanged()
    {
        var baseN   = RGBA(128, 128, 255, 0);
        var overlay  = RGBA(0, 0, 0, 255);           // covered
        var row      = Row(1f, 1f, 1f);              // Emissive defaults to 0

        CompositorService.ApplyFlatEmissive(baseN, overlay, row, 1, 1);

        Assert.Equal(0, baseN[3]); // alpha unchanged
    }

    [Fact]
    public void ApplyFlatEmissive_HalfEmissive_WritesIntensityToCoveredPixels()
    {
        var baseN   = RGBA(128, 128, 255, 0);
        var overlay  = RGBA(0, 0, 0, 255);           // covered
        var row      = Row(1f, 1f, 1f, emissive: 0.5f);

        CompositorService.ApplyFlatEmissive(baseN, overlay, row, 1, 1);

        var expected = (byte)(0.5f * 255f);
        Assert.Equal(expected, baseN[3]);
    }

    [Fact]
    public void ApplyFlatEmissive_ExistingHigherIntensity_KeepsMax()
    {
        var baseN   = RGBA(128, 128, 255, 200);      // alpha already 200
        var overlay  = RGBA(0, 0, 0, 255);
        var row      = Row(1f, 1f, 1f, emissive: 0.5f); // 0.5*255 ≈ 127 < 200

        CompositorService.ApplyFlatEmissive(baseN, overlay, row, 1, 1);

        Assert.Equal(200, baseN[3]); // max(200, 127) = 200
    }

    [Fact]
    public void ApplyFlatEmissive_UncoveredPixel_Unchanged()
    {
        var baseN   = RGBA(128, 128, 255, 0);
        var overlay  = RGBA(0, 0, 0, 0);             // NOT covered (alpha=0)
        var row      = Row(1f, 1f, 1f, emissive: 1f);

        CompositorService.ApplyFlatEmissive(baseN, overlay, row, 1, 1);

        Assert.Equal(0, baseN[3]); // unchanged
    }

    [Fact]
    public void ApplyFlatEmissive_FullEmissive_WritesMaxIntensity()
    {
        var baseN   = RGBA(128, 128, 255, 0);
        var overlay  = RGBA(0, 0, 0, 255);
        var row      = Row(1f, 1f, 1f, emissive: 1f);

        CompositorService.ApplyFlatEmissive(baseN, overlay, row, 1, 1);

        Assert.Equal(255, baseN[3]);
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

    // ── ApplyIndexedEmissive ──────────────────────────────────────────────────

    [Fact]
    public void ApplyIndexedEmissive_CoveredPixelWithEmissiveRow_WritesIntensity()
    {
        var baseN = RGBA(128, 128, 255, 0);       // normal with alpha=0
        var idx   = RGBA(0,   255, 0,   255);     // pair0, 100% A
        var cov   = RGBA(255, 255, 255, 255);     // covered
        var rows  = new Dictionary<int, ColorTableRowOverride>
        {
            [0] = new() { A = new() { Emissive = 0.5f }, B = new() }
        };

        CompositorService.ApplyIndexedEmissive(baseN, idx, cov, rows, 1, 1);

        // blendA=1 → em = B.Em + (A.Em - B.Em)*1 = 0 + 0.5 = 0.5
        Assert.Equal((byte)(0.5f * 255f), baseN[3]);
    }

    [Fact]
    public void ApplyIndexedEmissive_UncoveredPixel_Unchanged()
    {
        var baseN = RGBA(128, 128, 255, 0);
        var idx   = RGBA(0,   255, 0,   255);
        var cov   = RGBA(255, 255, 255, 0);       // NOT covered (alpha=0)
        var rows  = new Dictionary<int, ColorTableRowOverride>
        {
            [0] = new() { A = new() { Emissive = 1f }, B = new() }
        };

        CompositorService.ApplyIndexedEmissive(baseN, idx, cov, rows, 1, 1);

        Assert.Equal(0, baseN[3]); // unchanged
    }

    [Fact]
    public void ApplyIndexedEmissive_TakesMaxOfExistingAndNew()
    {
        var baseN = RGBA(128, 128, 255, 200);     // already has emissive 200
        var idx   = RGBA(0,   255, 0,   255);
        var cov   = RGBA(255, 255, 255, 255);
        var rows  = new Dictionary<int, ColorTableRowOverride>
        {
            [0] = new() { A = new() { Emissive = 0.3f }, B = new() } // 0.3*255 ≈ 76 < 200
        };

        CompositorService.ApplyIndexedEmissive(baseN, idx, cov, rows, 1, 1);

        Assert.Equal(200, baseN[3]); // max(200, 76) = 200
    }

    [Fact]
    public void ApplyIndexedEmissive_BlendsBetweenAandBEmissive()
    {
        var baseN = RGBA(128, 128, 255, 0);
        var idx   = RGBA(0,   0,   0,   255);  // G=0 → blendA=0 → 100% B
        var cov   = RGBA(255, 255, 255, 255);
        var rows  = new Dictionary<int, ColorTableRowOverride>
        {
            [0] = new() { A = new() { Emissive = 1f }, B = new() { Emissive = 0.25f } }
        };

        CompositorService.ApplyIndexedEmissive(baseN, idx, cov, rows, 1, 1);

        // blendA=0 → em = B.Em + (A.Em - B.Em)*0 = 0.25
        Assert.Equal((byte)(0.25f * 255f), baseN[3]);
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
}
