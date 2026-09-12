using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <see cref="UVRemapService.ExpandMirrored"/> — the art-side counterpart of un-mirroring, and the inverse of
/// <see cref="UVRemapService.CropRightHalf"/>.
/// <para/>
/// A mirrored sheet describes BOTH sides of the character with one layout, so moving it into a doubled space
/// means putting it in the right half and its reflection in the left. That has to agree exactly with where
/// the geometry conversion sends vertices: <c>+X → 0.5 + u/2</c> reads the right half, and its mirror reads
/// the left. If the two disagree the art lands on the wrong side of the body.
/// </summary>
public class ExpandMirroredTests
{
    /// <summary>A row of distinct columns, so where each one ends up is visible.</summary>
    private static byte[] Ramp(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                px[o] = (byte)(x * 255 / (w - 1));
                px[o + 1] = 128;
                px[o + 2] = 0;
                px[o + 3] = 255;
            }
        return px;
    }

    private static byte R(byte[] px, int w, int x, int y) => px[(y * w + x) * 4];

    /// <summary>
    /// The two halves are reflections of one another about the middle of the sheet — which is what gives a
    /// mirrored layout's single side somewhere to live on both sides of a doubled one.
    /// </summary>
    [Fact]
    public void The_output_halves_mirror_each_other()
    {
        const int N = 64;
        var outp = UVRemapService.ExpandMirrored(Ramp(N, N), N, N, N, N);

        for (int x = 0; x < N; x++)
            Assert.Equal(R(outp, N, x, 32), R(outp, N, N - 1 - x, 32));
    }

    /// <summary>
    /// EXACT, byte for byte, in every channel and every row — and that is load-bearing rather than tidy.
    /// <para/>
    /// It is the reason <see cref="SecondSkinWriter.RewriteFaceUv0"/> leaves tangents alone. The game shades
    /// with the tangent frame stored in the model; a -X vertex's frame is already the mirror of its +X
    /// partner's and today both sample the same texel. After the rewrite that vertex reads the mirrored
    /// half instead — so as long as the mirrored half holds identical VALUES, it reads the same numbers
    /// through the same frame and symmetric art renders exactly as it does now. If this ever drifts to
    /// "within a tolerance", that argument goes with it and the -X side starts shading differently.
    /// </summary>
    [Fact]
    public void The_left_half_is_an_exact_mirror_of_the_right()
    {
        const int W = 128, H = 64;
        var outp = UVRemapService.ExpandMirrored(Ramp(W / 2, H), W / 2, H, W, H);

        for (int y = 0; y < H; y++)
            for (int x = 0; x < W / 2; x++)
                for (int c = 0; c < 4; c++)
                    Assert.Equal(outp[(y * W + x) * 4 + c], outp[(y * W + (W - 1 - x)) * 4 + c]);
    }

    /// <summary>
    /// Expanding and folding back is the identity. The fold is what a doubled sheet falls back to when its
    /// face model cannot be rewritten, so this is the guarantee that a mod's +X side does not MOVE when it
    /// switches between the two paths — only its second side appears or disappears.
    /// </summary>
    [Fact]
    public void Expand_then_fold_round_trips()
    {
        const int W = 64, H = 64;
        var src = Ramp(W, H);
        var folded = UVRemapService.CropRightHalf(UVRemapService.ExpandMirrored(src, W, H, W * 2, H), W * 2, H);

        Assert.Equal(src.Length, folded.Length);
        for (int i = 0; i < src.Length; i++) Assert.Equal(src[i], folded[i]);
    }

    /// <summary>
    /// The RIGHT half carries the source as authored: the un-mirror affine sends a +X vertex at u to
    /// 0.5 + u/2, so the right half must hold the source read left-to-right.
    /// </summary>
    [Fact]
    public void The_right_half_holds_the_source_in_order()
    {
        const int N = 64;
        var outp = UVRemapService.ExpandMirrored(Ramp(N, N), N, N, N, N);

        // Across the right half the ramp must rise, and it must span the source's full range.
        Assert.True(R(outp, N, N / 2 + 1, 32) < R(outp, N, N - 2, 32));
        Assert.True(R(outp, N, N - 1, 32) > 200);   // the source's bright end
        Assert.True(R(outp, N, N / 2, 32) < 60);    // the source's dark end, at the seam
    }

    /// <summary>
    /// Expanding straight to the requested size and going via a larger intermediate must agree — this is what
    /// lets the no-transfer-map leg skip the 4096 detour. Compared with tolerance because the detour resamples
    /// twice and this does it once; the LAYOUT has to match, not the last bit of every texel.
    /// </summary>
    [Fact]
    public void Direct_and_via_a_larger_intermediate_agree()
    {
        const int N = 64;
        var src = Ramp(N, N);

        var direct = UVRemapService.ExpandMirrored(src, N, N, N, N);
        var viaBig = UVRemapService.ResizeBilinear(
            UVRemapService.ExpandMirrored(src, N, N, N * 4, N * 4), N * 4, N * 4, N, N);

        // Away from the seam and the edges, where a two-step resample and a one-step one differ most.
        for (int x = 4; x < N - 4; x++)
        {
            if (x is >= N / 2 - 2 and <= N / 2 + 1) continue;
            Assert.True(System.Math.Abs(R(direct, N, x, 32) - R(viaBig, N, x, 32)) <= 12,
                $"column {x}: direct {R(direct, N, x, 32)} vs via-4x {R(viaBig, N, x, 32)}");
        }
    }

    /// <summary>Alpha survives — a sheer overlay's coverage is carried in it, and losing it renders the art
    /// solid.</summary>
    [Fact]
    public void Alpha_is_carried_through()
    {
        const int N = 32;
        var src = Ramp(N, N);
        for (int i = 3; i < src.Length; i += 4) src[i] = 77;

        var outp = UVRemapService.ExpandMirrored(src, N, N, N, N);
        for (int i = 3; i < outp.Length; i += 4) Assert.Equal(77, outp[i]);
    }
}
