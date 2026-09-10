using System;
using System.Collections.Generic;
using System.IO;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The shell used to bake every texture at a fixed 2048 and reach it by NEAREST-NEIGHBOUR decimation, while
/// the skin path ran at 4096 and, for 4K art, never resampled at all. So the same tattoo looked clean on a
/// skin layer and speckled on a cloth one — which reads as block-compression damage and is not: it survives
/// turning compression off, because it happens long before the encoder.
/// <para/>
/// These cover the two halves of the fix — <see cref="SecondSkinService.ChooseTexSize"/> sizing the sheet from
/// the art, and <see cref="ResampleFilter"/> deciding how anything that still has to be resized gets there.
/// </summary>
public class ShellTexResolutionTests
{
    // ── ProbeSize ────────────────────────────────────────────────────────────

    /// <summary>A minimal PNG: signature plus an IHDR chunk. Only the header is ever read.</summary>
    private static void WritePngHeader(string path, int w, int h)
    {
        var bytes = new byte[64];
        byte[] sig = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        sig.CopyTo(bytes, 0);
        bytes[8] = 0; bytes[9] = 0; bytes[10] = 0; bytes[11] = 13;      // IHDR length
        bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
        // Width and height are BIG-endian in a PNG, unlike everything else this codebase reads.
        bytes[16] = (byte)(w >> 24); bytes[17] = (byte)(w >> 16); bytes[18] = (byte)(w >> 8); bytes[19] = (byte)w;
        bytes[20] = (byte)(h >> 24); bytes[21] = (byte)(h >> 16); bytes[22] = (byte)(h >> 8); bytes[23] = (byte)h;
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>A .tex header in the layout <see cref="TextureLoader.WriteTex"/> emits.</summary>
    private static void WriteTexHeader(string path, int w, int h)
    {
        var bytes = new byte[80];
        BitConverter.TryWriteBytes(bytes.AsSpan(8),  (ushort)w);
        BitConverter.TryWriteBytes(bytes.AsSpan(10), (ushort)h);
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void ProbeSize_reads_a_png_header_big_endian()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var p = Path.Combine(dir, "diffuse.png");
            WritePngHeader(p, 4096, 4096);
            Assert.Equal((4096, 4096), TextureLoader.ProbeSize(p));

            // Non-square, and small enough that only the low byte carries — catches a byte-order slip that a
            // 4096x4096 square (which is symmetric in both) would hide.
            var q = Path.Combine(dir, "mask.png");
            WritePngHeader(q, 512, 1024);
            Assert.Equal((512, 1024), TextureLoader.ProbeSize(q));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ProbeSize_reads_a_tex_header_little_endian()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var p = Path.Combine(dir, "art.tex");
            WriteTexHeader(p, 2048, 1024);
            Assert.Equal((2048, 1024), TextureLoader.ProbeSize(p));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A mod may name diffuse.dds in its metadata and ship diffuse.png. The loader already tolerates that, so
    /// the probe must resolve the SAME file — otherwise the sheet is sized off one image and filled from
    /// another.
    /// </summary>
    [Fact]
    public void ProbeSize_follows_the_same_sibling_extension_fallback_as_the_loader()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            WritePngHeader(Path.Combine(dir, "diffuse.png"), 4096, 4096);
            Assert.Equal((4096, 4096), TextureLoader.ProbeSize(Path.Combine(dir, "diffuse.dds")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ProbeSize_returns_null_rather_than_throwing_on_junk()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var missing = Path.Combine(dir, "nope.png");
            Assert.Null(TextureLoader.ProbeSize(missing));

            var truncated = Path.Combine(dir, "short.png");
            File.WriteAllBytes(truncated, [0x89, (byte)'P', (byte)'N', (byte)'G']);
            Assert.Null(TextureLoader.ProbeSize(truncated));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── ChooseTexSize ────────────────────────────────────────────────────────

    /// <summary>
    /// The no-regression case, and the one that matters most: everything shipped before shells could grow was
    /// baked at 2048, so art at or below that must still choose exactly 2048 — not a smaller sheet sized to
    /// the art, which would make an existing 1K-authored shell blurrier than it was.
    /// </summary>
    [Theory]
    [InlineData(256, 2048)]
    [InlineData(1024, 2048)]
    [InlineData(2048, 2048)]
    public void Art_at_or_below_the_floor_stays_at_the_floor(int art, int expected)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var p = Path.Combine(dir, "a.png");
            WritePngHeader(p, art, art);
            Assert.Equal(expected, SecondSkinService.ChooseTexSize([p]));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The reported defect: 4K art must bake at 4K, the same size the skin path gives it.</summary>
    [Fact]
    public void Four_k_art_chooses_the_cap()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var p = Path.Combine(dir, "a.png");
            WritePngHeader(p, 4096, 4096);
            Assert.Equal(4096, SecondSkinService.ChooseTexSize([p], out var largest));
            Assert.Equal(4096, largest);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Art_above_the_cap_is_clamped_to_it()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var p = Path.Combine(dir, "a.png");
            WritePngHeader(p, 8192, 8192);
            Assert.Equal(4096, SecondSkinService.ChooseTexSize([p]));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// One size serves the whole build, so the LARGEST layer has to win: sibling relief and the mask merges
    /// combine these buffers element-wise, and a layer sized below the one it is folded into would index it
    /// out of step.
    /// </summary>
    [Fact]
    public void The_largest_art_in_the_build_decides()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var small = Path.Combine(dir, "small.png"); WritePngHeader(small, 1024, 1024);
            var big   = Path.Combine(dir, "big.png");   WritePngHeader(big, 4096, 4096);
            Assert.Equal(4096, SecondSkinService.ChooseTexSize([small, big]));
            Assert.Equal(4096, SecondSkinService.ChooseTexSize([big, small]));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A non-power-of-two rounds UP — block compression and the coverage grid both want 4-alignment.</summary>
    [Fact]
    public void A_non_power_of_two_rounds_up()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var p = Path.Combine(dir, "a.png");
            WritePngHeader(p, 3000, 3000);
            Assert.Equal(4096, SecondSkinService.ChooseTexSize([p]));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Nulls, blanks and unprobeable paths get no vote; the floor is the answer when nobody votes.</summary>
    [Fact]
    public void Unprobeable_paths_fall_back_to_the_floor()
    {
        Assert.Equal(2048, SecondSkinService.ChooseTexSize([null, "", @"Z:\does\not\exist.png"], out var largest));
        Assert.Equal(0, largest);
    }

    // ── Resampling ───────────────────────────────────────────────────────────

    /// <summary>A 2x2 checker of pure black and white, tiled to (w, h).</summary>
    private static byte[] Checker(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)(((x + y) & 1) == 0 ? 0 : 255);
                int o = (y * w + x) * 4;
                px[o] = v; px[o + 1] = v; px[o + 2] = v; px[o + 3] = 255;
            }
        return px;
    }

    /// <summary>
    /// The defect itself, in one assertion. Halving a checkerboard by point sampling lands on the same parity
    /// every time, so the whole image collapses to one flat colour — every other pixel simply thrown away.
    /// An area average sees both and returns the mean. This is what a tattoo's filigree was being put through.
    /// </summary>
    [Fact]
    public void Box_averages_a_checker_where_nearest_annihilates_it()
    {
        var src = Checker(64, 64);

        var box = UVRemapService.ResizeBox(src, 64, 64, 32, 32);
        for (int i = 0; i < box.Length; i += 4)
            Assert.InRange(box[i], 127, 128);   // the mean of 0 and 255, either rounding

        // Nearest, for contrast: every destination texel samples the same parity, so the checker is gone.
        var near = new TextureLoaderProbe().Scale(src, 64, 64, 32, 32);
        for (int i = 0; i < near.Length; i += 4)
            Assert.Equal(near[0], near[i]);
    }

    /// <summary>Alpha is averaged like any other channel — a coverage edge softens rather than stair-steps.</summary>
    [Fact]
    public void Box_averages_the_alpha_lane_too()
    {
        // Left half fully opaque, right half fully transparent.
        var src = new byte[4 * 1 * 4];
        for (int x = 0; x < 4; x++)
        {
            src[x * 4 + 3] = (byte)(x < 2 ? 255 : 0);
        }

        var box = UVRemapService.ResizeBox(src, 4, 1, 2, 1);
        Assert.Equal(255, box[3]);   // both source texels opaque
        Assert.Equal(0, box[7]);     // both transparent
    }

    /// <summary>
    /// Growing an axis while another stays equal must NOT take the box path: its span collapses to one texel
    /// on the axis that grows, which is point sampling again. This is the gen2 remap's shape — it hands a
    /// 2048x4096 half sheet up to a square one — and getting it wrong would reintroduce the defect on exactly
    /// the path that already resamples the most.
    /// </summary>
    [Fact]
    public void A_half_sheet_grown_to_square_is_interpolated_not_point_sampled()
    {
        // Two columns, black then white. A real interpolation puts intermediate values between them.
        var src = new byte[2 * 2 * 4];
        for (int y = 0; y < 2; y++)
        {
            int o = (y * 2 + 1) * 4;
            src[o] = src[o + 1] = src[o + 2] = 255;
            src[(y * 2) * 4 + 3] = 255; src[o + 3] = 255;
        }

        var grown = TextureLoader.Resample(src, 2, 2, 8, 2, ResampleFilter.Auto);
        var reds = new HashSet<byte>();
        for (int x = 0; x < 8; x++) reds.Add(grown[x * 4]);
        Assert.True(reds.Count > 2, "an upscale must interpolate, not repeat two source texels");
    }

    /// <summary>
    /// Index maps opt OUT. Red encodes a row id (red / 17 + 1), so an averaged texel names a row nobody
    /// assigned — the wrong colour, not a softer edge. Nearest must return only values that were in the
    /// source.
    /// </summary>
    [Fact]
    public void Nearest_never_invents_a_value_that_was_not_in_the_source()
    {
        // Alternating row selectors: pair 1 (red 0) and pair 16 (red 255). Averaging gives ~127 — pair 8,
        // which is a row the author never touched.
        var src = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int o = (y * 8 + x) * 4;
                src[o] = (byte)(((x + y) & 1) == 0 ? 0 : 255);
                src[o + 3] = 255;
            }

        var near = TextureLoader.Resample(src, 8, 8, 4, 4, ResampleFilter.Nearest);
        for (int i = 0; i < near.Length; i += 4)
            Assert.True(near[i] == 0 || near[i] == 255,
                $"nearest produced {near[i]}, a row selector that was never in the source");

        // ...and the Auto filter is what it has to be protected FROM.
        var auto = TextureLoader.Resample(src, 8, 8, 4, 4, ResampleFilter.Auto);
        Assert.Contains(auto[0], (byte[])[127, 128]);
    }

    [Fact]
    public void Resample_returns_the_same_buffer_when_nothing_has_to_change()
    {
        var src = Checker(8, 8);
        Assert.Same(src, TextureLoader.Resample(src, 8, 8, 8, 8, ResampleFilter.Auto));
        Assert.Same(src, TextureLoader.Resample(src, 8, 8, 8, 8, ResampleFilter.Nearest));
    }

    /// <summary>Reaches the loader's public nearest-neighbour wrapper without needing Dalamud services.</summary>
    private sealed class TextureLoaderProbe
    {
        public byte[] Scale(byte[] src, int sw, int sh, int dw, int dh)
        {
            // ScaleRgba is an instance method purely by convention; it touches no state.
            var loader = (TextureLoader)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(TextureLoader));
            return loader.ScaleRgba(src, sw, sh, dw, dh);
        }
    }
}
