using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Penumbra.Api.Enums;

namespace Proteus.Services;

using static Proteus.Services.CompositorService;

internal static class OverlayBlend
{
    /// <summary>
    /// Split a per-pixel loop across cores. <paramref name="body"/> is handed a [from, to) sub-range and
    /// iterates it with the same <paramref name="step"/> the serial loop used. Every kernel using this must
    /// write only the pixel it is given and read only that index from its inputs (no carried state, no
    /// neighbour samples), so the output stays byte-identical to the serial loop.
    /// </summary>
    internal static void ParallelPixels(int start, int end, int step, Action<int, int> body)
    {
        const int MinParallelPixels = 256 * 256;
        int span = end - start;
        if (span <= 0) return;
        if (span / step < MinParallelPixels || Environment.ProcessorCount < 2)
        {
            body(start, end);
            return;
        }

        int workers = Math.Min(Environment.ProcessorCount, 16);
        // Round each chunk up to a whole number of steps so no worker can start mid-pixel.
        int chunk = ((span + workers - 1) / workers + step - 1) / step * step;
        Parallel.For(0, workers, k =>
        {
            int from = start + k * chunk;
            if (from >= end) return;
            body(from, Math.Min(from + chunk, end));
        });
    }

    /// <summary>Alpha-over union of an RGBA buffer's alpha channel into a single-channel accumulator. Shared by
    /// the higher-group claim and the paint accumulator so the two agree on "already covered".</summary>
    internal static void UnionAlphaInto(byte[] acc, byte[] rgba)
    {
        int n = Math.Min(acc.Length, rgba.Length / 4);
        ParallelPixels(0, n, 1, (from, to) =>
        {
            for (int i = from; i < to; i++)
                acc[i] = (byte)(acc[i] + (255 - acc[i]) * rgba[i * 4 + 3] / 255);
        });
    }

    /// <summary>One channel of a blend mode, on 0–1, BEFORE it is clipped to the fabric. <see cref="RowBlend.Paint"/>
    /// is not here: it is alpha-over, driven by the overlay's own coverage, and the callers apply it directly.</summary>
    internal static float BlendChannel(RowBlend mode, float dst, float src) => mode switch
    {
        RowBlend.Multiply => dst * src,
        RowBlend.Screen   => 1f - (1f - dst) * (1f - src),
        RowBlend.Overlay  => dst < 0.5f ? 2f * dst * src : 1f - 2f * (1f - dst) * (1f - src),
        RowBlend.Add      => MathF.Min(1f, dst + src),
        RowBlend.Replace  => src,
        _                 => src,
    };

    /// <summary>How strongly a print lands on one texel: its own alpha TIMES how much its mod painted there,
    /// so a print fades along antialiased edges and vanishes where its mod painted nothing.</summary>
    private static float ClipStrength(float ovA, byte[]? painted, int pixel)
        => painted == null ? 0f : ovA * (painted[pixel] / 255f);

    /// <summary>A 0–1 channel back to a byte, ROUNDED rather than truncated. The alpha-over path truncates and
    /// must keep truncating for existing mods; a blend must not, or multiply-by-white stops being identity.</summary>
    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    /// <summary>Timing shim — see the blend sub-phase counters. Body unchanged, in <c>…Core</c>.</summary>
    internal static void ApplyFlatOverlay(byte[] baseTex, byte[] ov, ColorTableSubRow row, int w, int h,
        byte[]? painted = null)
    {
        var t0 = PhaseCounter.Begin();
        try { ApplyFlatOverlayCore(baseTex, ov, row, w, h, painted); }
        finally { blendDiffuseStats.Stop(t0); }
    }

    private static void ApplyFlatOverlayCore(byte[] baseTex, byte[] ov, ColorTableSubRow row, int w, int h,
                                          byte[]? painted = null)
    {
        float cr = row.DiffuseR, cg = row.DiffuseG, cb = row.DiffuseB;
        var mode = row.Blend;

        // The path every overlay took before blend modes existed, kept bit for bit. Not an optimisation:
        // it is the guarantee that adding the field changed nothing for the mods that came before it.
        if (mode == RowBlend.Paint)
        {
            ParallelPixels(0, w * h * 4, 4, (from, to) =>
            {
                for (int i = from; i < to; i += 4)
                {
                    float a = ov[i + 3] / 255f;
                    if (a <= 0f) continue;
                    float ia = 1f - a;
                    baseTex[i]     = (byte)(ov[i]     / 255f * cr * a * 255f + baseTex[i]     * ia);
                    baseTex[i + 1] = (byte)(ov[i + 1] / 255f * cg * a * 255f + baseTex[i + 1] * ia);
                    baseTex[i + 2] = (byte)(ov[i + 2] / 255f * cb * a * 255f + baseTex[i + 2] * ia);
                }
            });
            return;
        }

        // A print with nothing beneath it paints nothing; a clip that does not cover the sheet cannot say what
        // was painted, and guessing would put colour on bare skin.
        if (painted == null || painted.Length < w * h) return;

        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float m = ClipStrength(ov[i + 3] / 255f, painted, i >> 2);
                if (m <= 0f) continue;
                float im = 1f - m;
                float d0 = baseTex[i] / 255f, d1 = baseTex[i + 1] / 255f, d2 = baseTex[i + 2] / 255f;
                baseTex[i]     = ToByte(d0 * im + BlendChannel(mode, d0, ov[i]     / 255f * cr) * m);
                baseTex[i + 1] = ToByte(d1 * im + BlendChannel(mode, d1, ov[i + 1] / 255f * cg) * m);
                baseTex[i + 2] = ToByte(d2 * im + BlendChannel(mode, d2, ov[i + 2] / 255f * cb) * m);
            }
        });
    }

    // Per-pixel color and emissive driven by index texture.
    // isNormal = false: tint+composite diffuse; isNormal = true: write emissive to normal alpha.
    internal static void ApplyIndexedOverlay(
        byte[] baseTex, byte[] ov, byte[] idx,
        Dictionary<int, ColorTableRowOverride> rows,
        bool isNormal, int w, int h, byte[]? painted = null)
    {
        var t0 = PhaseCounter.Begin();
        // isNormal routes emissive into the normal's alpha, so it belongs with the normal recombine
        // rather than with the diffuse composites, even though it is the same kernel.
        try { ApplyIndexedOverlayCore(baseTex, ov, idx, rows, isNormal, w, h, painted); }
        finally { (isNormal ? blendNormalStats : blendDiffuseStats).Stop(t0); }
    }

    /// <summary>Body of <see cref="ApplyIndexedOverlay"/>, split out only so the call can be timed.</summary>
    private static void ApplyIndexedOverlayCore(
        byte[] baseTex, byte[] ov, byte[] idx,
        Dictionary<int, ColorTableRowOverride> rows,
        bool isNormal, int w, int h, byte[]? painted = null)
    {
        // The row pair is `red / 17`, so there are only SIXTEEN distinct answers, resolved once into flat arrays
        // rather than a dictionary lookup and an allocation per texel.
        const int Pairs = 16;
        float[] aR = new float[Pairs], aG = new float[Pairs], aB = new float[Pairs], aE = new float[Pairs];
        float[] bR = new float[Pairs], bG = new float[Pairs], bB = new float[Pairs], bE = new float[Pairs];
        var aBl = new RowBlend[Pairs];
        var bBl = new RowBlend[Pairs];
        bool anyBlend = false;
        for (int p = 0; p < Pairs; p++)
        {
            // An absent row keeps the default-constructed values.
            var pair = rows.TryGetValue(p, out var r) ? r : new ColorTableRowOverride();
            aR[p] = pair.A.DiffuseR; aG[p] = pair.A.DiffuseG; aB[p] = pair.A.DiffuseB; aE[p] = pair.A.Emissive;
            bR[p] = pair.B.DiffuseR; bG[p] = pair.B.DiffuseG; bB[p] = pair.B.DiffuseB; bE[p] = pair.B.Emissive;
            aBl[p] = pair.A.Blend;   bBl[p] = pair.B.Blend;
            if (aBl[p] != RowBlend.Paint || bBl[p] != RowBlend.Paint) anyBlend = true;
        }

        // Nothing here prints, so run the original loop bit for bit (lerping colours then compositing is not
        // float-identical to compositing twice and lerping). The normal path never prints: it carries emissive.
        bool plain = isNormal || !anyBlend;
        var clip = painted != null && painted.Length >= w * h ? painted : null;

        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float ovA = ov[i + 3] / 255f;
                if (ovA <= 0f) continue;

                int   pairIdx = idx[i]     / 17;        // red → pair 0–15
                float blendA  = idx[i + 1] / 255f;      // green → lerp B→A (1 = full A, 0 = full B)

                // Also the original path when THIS pair paints on both sides: the print path rounds where this
                // one truncates, and a per-row edit must not shift another row's output.
                if (plain || (aBl[pairIdx] == RowBlend.Paint && bBl[pairIdx] == RowBlend.Paint))
                {
                    float dr = bR[pairIdx] + (aR[pairIdx] - bR[pairIdx]) * blendA;
                    float dg = bG[pairIdx] + (aG[pairIdx] - bG[pairIdx]) * blendA;
                    float db = bB[pairIdx] + (aB[pairIdx] - bB[pairIdx]) * blendA;
                    float em = bE[pairIdx] + (aE[pairIdx] - bE[pairIdx]) * blendA;

                    if (!isNormal)
                    {
                        float ia = 1f - ovA;
                        baseTex[i]     = (byte)(ov[i]     / 255f * dr * ovA * 255f + baseTex[i]     * ia);
                        baseTex[i + 1] = (byte)(ov[i + 1] / 255f * dg * ovA * 255f + baseTex[i + 1] * ia);
                        baseTex[i + 2] = (byte)(ov[i + 2] / 255f * db * ovA * 255f + baseTex[i + 2] * ia);
                    }
                    else
                    {
                        baseTex[i + 3] = Math.Max(baseTex[i + 3], (byte)(em * 255f));
                    }
                    continue;
                }

                // The two sub-rows may composite by different rules, and a rule cannot be interpolated: each is
                // resolved to the value it would leave in the base ON ITS OWN, and THOSE are lerped by green.
                float mBlend = ClipStrength(ovA, clip, i >> 2);
                for (int c = 0; c < 3; c++)
                {
                    float dst = baseTex[i + c] / 255f;
                    float art = ov[i + c] / 255f;
                    float ca  = c == 0 ? aR[pairIdx] : c == 1 ? aG[pairIdx] : aB[pairIdx];
                    float cb  = c == 0 ? bR[pairIdx] : c == 1 ? bG[pairIdx] : bB[pairIdx];
                    float outA = SubRowResult(aBl[pairIdx], dst, art * ca, ovA, mBlend);
                    float outB = SubRowResult(bBl[pairIdx], dst, art * cb, ovA, mBlend);
                    baseTex[i + c] = ToByte(outB + (outA - outB) * blendA);
                }
            }
        });
    }

    /// <summary>What one sub-row alone would leave in the base at this texel, on 0–1. <see cref="RowBlend.Paint"/>
    /// composites by the overlay's own alpha (it brings its own colour); every other mode by the clip.</summary>
    private static float SubRowResult(RowBlend mode, float dst, float src, float ovA, float clipped)
    {
        if (mode == RowBlend.Paint) return dst * (1f - ovA) + src * ovA;
        if (clipped <= 0f) return dst;
        return dst * (1f - clipped) + BlendChannel(mode, dst, src) * clipped;
    }

    // Partial-derivative add for normal maps: XY are decoded to signed space, the overlay's contribution (scaled
    // by alpha) is added, then re-encoded. Blue (skin colour influence) and alpha (emissive) have their own passes.
    /// <summary>Overwrite the base normal with <paramref name="src"/> where it applies, instead of adding to it:
    /// a Masks option's relief normal IS the surface there, and compounding two reliefs reads as noise.</summary>
    internal static void ReplaceNormal(byte[] dst, byte[] src, int w, int h, byte[]? mask = null)
    {
        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float a = src[i + 3] / 255f;
                if (mask != null) a = Math.Min(a, mask[i + 3] / 255f);
                if (a <= 0f) continue;

                dst[i]     = (byte)Math.Clamp(dst[i]     + (src[i]     - dst[i])     * a, 0, 255);
                dst[i + 1] = (byte)Math.Clamp(dst[i + 1] + (src[i + 1] - dst[i + 1]) * a, 0, 255);
            }
        });
    }

    // ── Mask trim convention ──────────────────────────────────────────────────
    // A Proteus mask replaces the surface beneath it only on its TRIM: a texel is the trim if its relief normal
    // deviates from flat OR its coverage LUMINANCE is near-white. Neither alone covers the whole band. The mask
    // PNGs' alpha channel is not a usable shape signal.
    internal const int MaskReliefDeadzone = 8;    // |R-128|+|G-128| below this = flat (no bump)
    internal const int MaskTrimLuma       = 190;  // coverage luminance at/above this = bright trim band

    /// <summary>True if texel <paramref name="o"/> is a mask's trim (see the convention above). RGBA arrays.</summary>
    internal static bool IsMaskTrim(byte[] relief, byte[] coverage, int o)
    {
        if (Math.Abs(relief[o] - 128) + Math.Abs(relief[o + 1] - 128) >= MaskReliefDeadzone) return true;
        int luma = (coverage[o] * 77 + coverage[o + 1] * 150 + coverage[o + 2] * 29) >> 8;
        return luma >= MaskTrimLuma;
    }

    /// <summary>
    /// Fold several masks' relief into <paramref name="baseNormal"/> (RGBA), TOP-FIRST with a per-texel claim: a
    /// higher mask's TRIM owns the texel (R/G written, blue left alone) and a lower mask's trim never draws
    /// there. A mask's plain fill does NOT claim. Shared by the skin normal and the gear mask-shell.
    /// </summary>
    internal static void CombineMaskReliefs(byte[] baseNormal, int w, int h,
        IReadOnlyList<(byte[] Relief, byte[] Coverage)> masksTopFirst)
    {
        if (masksTopFirst.Count == 0) return;
        var claimed = new bool[w * h];
        foreach (var (relief, coverage) in masksTopFirst)
        {
            if (relief == null || coverage == null) continue;
            ParallelPixels(0, w * h, 1, (from, to) =>
            {
                for (int p = from; p < to; p++)
                {
                    if (claimed[p]) continue;               // a higher mask's trim already owns this texel
                    int o = p * 4;
                    if (!IsMaskTrim(relief, coverage, o)) continue;
                    baseNormal[o]     = relief[o];          // R/G only — blue is unused for masks
                    baseNormal[o + 1] = relief[o + 1];
                    claimed[p] = true;
                }
            });
        }
    }

    /// <summary>Timing shim — see the blend sub-phase counters. Body unchanged, in <c>…Core</c>.</summary>
    internal static void CompoundNormal(byte[] dst, byte[] src, int w, int h, byte[]? mask = null)
    {
        var t0 = PhaseCounter.Begin();
        try { CompoundNormalCore(dst, src, w, h, mask); }
        finally { blendNormalStats.Stop(t0); }
    }

    private static void CompoundNormalCore(byte[] dst, byte[] src, int w, int h, byte[]? mask = null)
    {
        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float a = src[i + 3] / 255f;
                if (mask != null) a = Math.Min(a, mask[i + 3] / 255f);
                if (a <= 0f) continue;

                float bx = dst[i]     / 127.5f - 1f;
                float by = dst[i + 1] / 127.5f - 1f;
                float ox = src[i]     / 127.5f - 1f;
                float oy = src[i + 1] / 127.5f - 1f;

                dst[i]     = (byte)Math.Clamp((bx + ox * a + 1f) * 127.5f, 0, 255);
                dst[i + 1] = (byte)Math.Clamp((by + oy * a + 1f) * 127.5f, 0, 255);
            }
        });
    }

    // Standard alpha-over: dst = src * src.a + dst * (1 - src.a); dst alpha unchanged. mask, if given, gates
    // the effective alpha to min(src alpha, mask alpha).
    /// <summary>Timing shim — see the blend sub-phase counters. Body unchanged, in <c>…Core</c>.</summary>
    internal static void AlphaComposite(byte[] dst, byte[] src, int w, int h, byte[]? mask = null)
    {
        var t0 = PhaseCounter.Begin();
        try { AlphaCompositeCore(dst, src, w, h, mask); }
        finally { blendNormalStats.Stop(t0); }
    }

    private static void AlphaCompositeCore(byte[] dst, byte[] src, int w, int h, byte[]? mask = null)
    {
        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float a = src[i + 3] / 255f;
                if (mask != null) a = Math.Min(a, mask[i + 3] / 255f);
                if (a <= 0f) continue;
                float ia = 1f - a;
                dst[i]     = (byte)(src[i]     * a + dst[i]     * ia);
                dst[i + 1] = (byte)(src[i + 1] * a + dst[i + 1] * ia);
                dst[i + 2] = (byte)(src[i + 2] * a + dst[i + 2] * ia);
            }
        });
    }

    // Fade the normal map's BLUE channel (skin.shpk "skin colour influence") toward black under the overlay,
    // weighted by the composited diffuse's luminance (null diffuse = luminance 1) and by cov.alpha; `strength`
    // is the global user multiplier.
    /// <summary>Timing shim — see the blend sub-phase counters. Body unchanged, in <c>…Core</c>.</summary>
    internal static void SuppressSkinColorInfluence(byte[] baseN, byte[] cov, byte[]? diffuse, int w, int h, float strength = 1f)
    {
        var t0 = PhaseCounter.Begin();
        try { SuppressSkinColorInfluenceCore(baseN, cov, diffuse, w, h, strength); }
        finally { blendNormalStats.Stop(t0); }
    }

    private static void SuppressSkinColorInfluenceCore(byte[] baseN, byte[] cov, byte[]? diffuse, int w, int h, float strength = 1f)
    {
        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float a = cov[i + 3] / 255f * strength;
                if (a <= 0f) continue;
                if (diffuse != null)
                {
                    float lum = (0.299f * diffuse[i] + 0.587f * diffuse[i + 1] + 0.114f * diffuse[i + 2]) / 255f;
                    a *= lum;
                    if (a <= 0f) continue;
                }
                baseN[i + 2] = (byte)(baseN[i + 2] * (1f - a));
            }
        });
    }

    /// <summary>
    /// Separable maximum filter of a single-channel plane: each texel takes the largest value within
    /// <paramref name="radius"/>. A maximum, not a blur: "is any cloth NEAR here lifted" is a max over the
    /// neighbourhood. Van Herk / Gil-Werman, O(1) per texel regardless of radius; the window is clamped at
    /// the edges, which can only overstate the result there.
    /// </summary>
    internal static byte[] MaxFilter(byte[] src, int w, int h, int radius)
    {
        if (radius < 1 || w <= 0 || h <= 0 || src.Length < w * h) return (byte[])src.Clone();
        var mid = new byte[w * h];
        var dst = new byte[w * h];
        int k = 2 * radius + 1;
        var pre = new byte[Math.Max(w, h)];
        var suf = new byte[Math.Max(w, h)];

        void Line(byte[] s, int sOff, int sStep, byte[] d, int dOff, int dStep, int n)
        {
            for (int i = 0; i < n; i++)
            {
                byte v = s[sOff + i * sStep];
                pre[i] = i % k == 0 ? v : Math.Max(pre[i - 1], v);
            }
            for (int i = n - 1; i >= 0; i--)
            {
                byte v = s[sOff + i * sStep];
                suf[i] = i == n - 1 || (i + 1) % k == 0 ? v : Math.Max(suf[i + 1], v);
            }
            for (int i = 0; i < n; i++)
            {
                byte a = suf[i - radius >= 0 ? i - radius : 0];
                byte b = pre[i + radius < n ? i + radius : n - 1];
                d[dOff + i * dStep] = Math.Max(a, b);
            }
        }

        for (int y = 0; y < h; y++) Line(src, y * w, 1, mid, y * w, 1, w);
        for (int x = 0; x < w; x++) Line(mid, x, w, dst, x, w, h);
        return dst;
    }

    /// <summary>Separable box blur of a single-channel plane; two box passes per iteration approximate a
    /// Gaussian. Reads neighbours, so it parallelises over independent rows/columns, not ParallelPixels.</summary>
    /// <summary>Box passes <see cref="BlurCoverage"/> makes by default, and so the multiple of its radius a
    /// blurred coverage reaches; the standoff mask must be grown by exactly that far.</summary>
    internal const int BlurCoveragePasses = 2;

    internal static byte[] BlurCoverage(byte[] src, int w, int h, int radius, int iterations = BlurCoveragePasses)
    {
        if (radius < 1 || w <= 0 || h <= 0 || src.Length < w * h) return (byte[])src.Clone();
        var a = (byte[])src.Clone();
        var b = new byte[a.Length];
        for (int it = 0; it < iterations; it++)
        {
            BoxBlurH(a, b, w, h, radius);   // a -> b (rows)
            BoxBlurV(b, a, w, h, radius);   // b -> a (columns)
        }
        return a;
    }

    // Horizontal running-sum box blur, one row per worker (rows are independent, so this is safe to
    // parallelise even though it reads neighbours — which ParallelPixels forbids).
    private static void BoxBlurH(byte[] src, byte[] dst, int w, int h, int radius)
    {
        int window = radius * 2 + 1;
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            int sum = 0;
            // Seed the window at x = 0: clamp samples off the left edge to column 0.
            for (int k = -radius; k <= radius; k++)
                sum += src[row + Math.Clamp(k, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = (byte)(sum / window);
                int add = Math.Clamp(x + radius + 1, 0, w - 1);
                int sub = Math.Clamp(x - radius, 0, w - 1);
                sum += src[row + add] - src[row + sub];
            }
        });
    }

    // Vertical running-sum box blur, one column per worker.
    private static void BoxBlurV(byte[] src, byte[] dst, int w, int h, int radius)
    {
        int window = radius * 2 + 1;
        Parallel.For(0, w, x =>
        {
            int sum = 0;
            for (int k = -radius; k <= radius; k++)
                sum += src[Math.Clamp(k, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = (byte)(sum / window);
                int add = Math.Clamp(y + radius + 1, 0, h - 1);
                int sub = Math.Clamp(y - radius, 0, h - 1);
                sum += src[add * w + x] - src[sub * w + x];
            }
        });
    }

    /// <summary>
    /// Bake a soft contact shadow onto the skin diffuse hugging the OUTSIDE edge of each strap: halo =
    /// blurred·(1−strap), RGB multiplied by (1 − strength·halo), alpha untouched. Per-pixel (the neighbour work
    /// happened in the blur). <c>coveredAbove</c> suppresses the shadow under an opaque higher garment.
    /// </summary>
    internal static void ApplyAmbientOcclusion(byte[] baseD, byte[] strap, byte[] blurred, int w, int h, float strength,
        byte[]? coveredAbove = null, byte[]? liftedOff = null)
    {
        if (strength <= 0f) return;
        ParallelPixels(0, w * h, 1, (from, to) =>
        {
            for (int p = from; p < to; p++)
            {
                float s = strap[p] / 255f;
                float halo = (blurred[p] / 255f) * (1f - s);
                if (coveredAbove != null) halo *= 1f - coveredAbove[p] / 255f;   // hidden under a higher layer
                // ...and lifted clear of the skin: a CONTACT shadow has nothing to shade there. Gated exactly
                // like the indent it accompanies.
                if (liftedOff != null) halo *= 1f - liftedOff[p] / 255f;
                if (halo <= 0f) continue;
                float k = 1f - strength * halo;
                if (k >= 1f) continue;
                if (k < 0f) k = 0f;
                int o = p * 4;
                baseD[o]     = (byte)(baseD[o]     * k);
                baseD[o + 1] = (byte)(baseD[o + 1] * k);
                baseD[o + 2] = (byte)(baseD[o + 2] * k);
            }
        });
    }

    /// <summary>
    /// Perturb the skin normal at strap edges so the skin reads as pressed IN: the tilt is the gradient of the
    /// blurred coverage, gated to the skin OUTSIDE the strap (the band the AO shadow darkens), leaning toward
    /// the strap. FFXIV tangent normals are OpenGL-style (green = +Y up) while rows increase downward, so the
    /// green offset negates gy; flip that one sign if vertical edges ever bulge. Writes R/G only. The tilt is
    /// clamped by VECTOR LENGTH (x² + y² past 1 makes z imaginary and the edge blows out white), and the
    /// gradient is normalised by the blur <paramref name="radius"/> so depth means the same slope at any resolution.
    /// </summary>
    /// <param name="radius">The blur radius used to build <paramref name="blurred"/>; defaults to
    /// <see cref="IndentRefRadius"/>.</param>
    internal static void ApplyNormalIndent(byte[] baseN, byte[] blurred, byte[] strap, int w, int h, float strength,
        byte[]? coveredAbove = null, int radius = IndentRefRadius, byte[]? inside = null,
        byte[]? liftedOff = null)
    {
        if (strength <= 0f) return;
        if (inside != null && inside.Length < w * h) inside = null;
        // Depth was tuned on a 2048-wide skin map at the 0.003 default softness ⇒ radius 6. Scaling by
        // radius/6 makes the setting mean the same slope everywhere, and a no-op at that reference.
        float gScale = radius > 0 ? radius / (float)IndentRefRadius : 1f;
        Parallel.For(0, h, y =>
        {
            int row     = y * w;
            int rowUp   = (y > 0 ? y - 1 : 0) * w;
            int rowDown = (y < h - 1 ? y + 1 : h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                float edge = 1f - strap[row + x] / 255f;   // skin side of the edge only
                if (coveredAbove != null) edge *= 1f - coveredAbove[row + x] / 255f;   // hidden under a higher layer
                // ...and not pressing on the skin at all: where a bridged shell has lifted clear of the bust
                // there is no contact to mark.
                if (liftedOff != null) edge *= 1f - liftedOff[row + x] / 255f;
                if (edge <= 0f) continue;
                int xm = x > 0 ? x - 1 : 0;
                int xp = x < w - 1 ? x + 1 : w - 1;

                // Never differentiate ACROSS a UV-island border: padding is not surface, and a central difference
                // straddling it reports a phantom step that carves a crease along the seam. Drop to a one-sided
                // difference there, scaled against the span this texel WOULD have had (baseSpan), so the
                // correction applies only where the island mask actually shortened the difference.
                int rUp = rowUp, rDn = rowDown;                 // per-texel copies: the row bases must not move
                int baseSpanX = xp - xm, baseSpanY = (rDn - rUp) / w;
                int spanX = baseSpanX, spanY = baseSpanY;
                if (inside != null)
                {
                    if (inside[row + xm] == 0) xm = x;
                    if (inside[row + xp] == 0) xp = x;
                    if (inside[rUp + x] == 0) rUp = row;
                    if (inside[rDn + x] == 0) rDn = row;
                    spanX = xp - xm;
                    spanY = (rDn - rUp) / w;
                }
                float gx = spanX == 0 ? 0f : (blurred[row + xp] - blurred[row + xm]) / 255f * ((float)baseSpanX / spanX);
                float gy = spanY == 0 ? 0f : (blurred[rDn + x] - blurred[rUp + x]) / 255f * ((float)baseSpanY / spanY);
                if (gx == 0f && gy == 0f) continue;
                int i = (row + x) * 4;
                float bx = baseN[i]     / 127.5f - 1f;
                float by = baseN[i + 1] / 127.5f - 1f;
                bx += strength * gx * edge * gScale;        // lean X toward the strap
                by -= strength * gy * edge * gScale;        // green = +Y up (OpenGL); rows go down → negate

                // Keep (x, y) inside the unit disc so the implied z stays real. Scaling both by the same
                // factor preserves the tilt DIRECTION (the groove still points into the strap) and only
                // caps how steep it gets.
                float len2 = bx * bx + by * by;
                if (len2 > MaxIndentTilt * MaxIndentTilt)
                {
                    float k = MaxIndentTilt / MathF.Sqrt(len2);
                    bx *= k;
                    by *= k;
                }

                baseN[i]     = (byte)Math.Clamp((bx + 1f) * 127.5f, 0, 255);
                baseN[i + 1] = (byte)Math.Clamp((by + 1f) * 127.5f, 0, 255);
            }
        });
    }

    /// <summary>Blur radius the Skindenting depth default was tuned against (2048-wide skin map × the 0.003
    /// default softness). <see cref="ApplyNormalIndent"/> normalises its gradient against this.</summary>
    private const int IndentRefRadius = 6;

    /// <summary>Steepest tangent-space tilt <see cref="ApplyNormalIndent"/> will produce, as the length of
    /// (x, y). Below 1 by a real margin: at exactly 1 the implied z is 0 (a wall seen edge-on, which is what
    /// blows out to white), so 0.9 leaves z ≈ 0.44 — a deep groove that still shades like a surface.</summary>
    private const float MaxIndentTilt = 0.9f;

    /// <summary>
    /// Repair an index texture's RED channel (the colour-table row selector, <c>red / 17 + 1</c>) so it only
    /// ever names a row that HAS a preset. An antialiased edge ramps through rows nobody configured, and a
    /// gear shell hands the texture straight to the shader, which paints the template's colours as a fringe.
    /// The repair is SPATIAL: an invalid texel takes the row of a nearby valid one. Texels already naming a
    /// configured row, GREEN (a genuine blend) and alpha are left alone. Texels the spread never reaches stay
    /// EXACTLY as they are: repainting the background would paint its row just outside the garment through the
    /// transparency gate's bleed; <paramref name="dilate"/> is sized to cover that bleed band instead.
    /// </summary>
    /// <param name="index">RGBA index texture, modified in place.</param>
    /// <param name="definedRows">1-based rows that have presets. Empty ⇒ no-op (nothing to snap to).</param>
    /// <param name="dilate">How many texels a valid row may spread outward: the antialiased band (1–2 texels)
    /// PLUS how far the transparency gate can bleed (a BC7 block is 4 texels, mips widen it).</param>
    /// <param name="authored">Optional per-texel flag: false marks a texel whose row selector was never written,
    /// so it is repaired even when its value names a configured row (a SYNTHESIZED index background is a real
    /// selector). Null ⇒ every texel counts as authored.</param>
    internal static void SnapIndexRowsToDefined(byte[] index, int w, int h,
        IReadOnlyCollection<int> definedRows, int dilate = 8, bool[]? authored = null)
    {
        if (index.Length < w * h * 4 || definedRows.Count == 0 || w <= 0 || h <= 0) return;
        if (authored != null && authored.Length < w * h)
        {
            // Ignore it rather than throw (a half-applied repair mid-composite is worse than none), but say so.
            // Null-conditional because the tests call this with no Dalamud behind it.
            Plugin.Log?.Error("[Proteus] SnapIndexRowsToDefined: authored flags cover {0} texels, need {1} — "
                + "ignoring them, so unwritten texels naming a configured row will NOT be repaired",
                authored.Length, w * h);
            authored = null;
        }

        var isDefined = new bool[17];
        foreach (var r in definedRows)
            if (r >= 1 && r <= 16) isDefined[r] = true;

        int n = w * h;
        var valid = new bool[n];
        ParallelPixels(0, n, 1, (from, to) =>
        { for (int p = from; p < to; p++) valid[p] = (authored?[p] ?? true) && isDefined[index[p * 4] / 17 + 1]; });

        // Only an INVALID texel with a VALID neighbour can ever be filled, so the passes walk that frontier
        // instead of rescanning the map. The frontier is a boundary, so its worst case is no worse than a scan.
        var queued = new bool[n];   // already on a frontier. Never cleared: once a texel fills it is valid
                                    // for good, and the !valid test below is what keeps it from returning.

        // Row-major, matching the old scan order, so the frontier is walked in the same sequence and the
        // "first valid neighbour wins" tie-breaks land identically.
        var rowSeeds = new List<int>?[h];
        Parallel.For(0, h, y =>
        {
            List<int>? seeds = null;
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int p = row + x;
                if (valid[p]) continue;
                if ((x > 0 && valid[p - 1]) || (x < w - 1 && valid[p + 1])
                 || (y > 0 && valid[p - w]) || (y < h - 1 && valid[p + w]))
                    (seeds ??= new List<int>()).Add(p);
            }
            rowSeeds[y] = seeds;
        });
        var frontier = new List<int>();
        foreach (var seeds in rowSeeds)
        {
            if (seeds == null) continue;
            foreach (var p in seeds) { frontier.Add(p); queued[p] = true; }
        }

        // Spread valid rows outward one texel per pass.
        var filled = new List<int>();
        for (int it = 0; it < dilate && frontier.Count > 0; it++)
        {
            filled.Clear();
            // `valid` is not written until the whole frontier has been resolved, so a texel's source can't be
            // something this pass just filled; no snapshot of `index` is needed since every frontier texel is invalid.
            foreach (var p in frontier)
            {
                int x = p % w, y = p / w;
                // 4-neighbourhood; first valid neighbour wins. Diagonals add nothing here — the band is
                // contiguous, so an extra pass reaches anything a diagonal would.
                int src = -1;
                if (x > 0     && valid[p - 1]) src = p - 1;
                else if (x < w - 1 && valid[p + 1]) src = p + 1;
                else if (y > 0     && valid[p - w]) src = p - w;
                else if (y < h - 1 && valid[p + w]) src = p + w;
                if (src < 0) continue;   // can't happen for a real frontier entry; cheap to not depend on it
                index[p * 4]     = index[src * 4];       // row selector
                index[p * 4 + 1] = index[src * 4 + 1];   // and its sub-row weight, so the pair stays coherent
                filled.Add(p);
            }
            foreach (var p in filled) valid[p] = true;

            // Anything still invalid that touches a texel this pass filled. It cannot be adjacent to an
            // OLDER valid texel — that would have put it on this frontier, and every frontier entry fills.
            var next = new List<int>();
            foreach (var p in filled)
            {
                int x = p % w, y = p / w;
                if (x > 0)     Consider(p - 1);
                if (x < w - 1) Consider(p + 1);
                if (y > 0)     Consider(p - w);
                if (y < h - 1) Consider(p + w);
            }
            frontier = next;

            void Consider(int q)
            {
                if (valid[q] || queued[q]) return;
                queued[q] = true;
                next.Add(q);
            }
        }
    }

    /// <summary>Any sub-row in this option asks for glow. Narrower than
    /// <see cref="RenderModeInference.HasCloth"/>, which also answers true for sphere/metal — used only to
    /// decide whether the promotion notice is about glow specifically.</summary>
    internal static bool HasEmissiveRow(List<ColorTableRowPreset>? rows)
        => rows?.Any(r => r.SubRowA?.Emissive > 0f || r.SubRowB?.Emissive > 0f) == true;

    /// <summary>Does any row here composite as a print rather than painting?</summary>
    internal static bool AnyBlendRow(Dictionary<int, ColorTableRowOverride> rows)
        => rows.Values.Any(r => r.A.Blend != RowBlend.Paint || r.B.Blend != RowBlend.Paint);

    /// <summary>The same question asked of the presets, for the composite sort, which runs before any row
    /// dictionary is built.</summary>
    internal static bool AnyBlendRow(List<ColorTableRowPreset>? presets)
        => presets != null && presets.Any(p => (p.SubRowA?.Blend ?? RowBlend.Paint) != RowBlend.Paint
                                            || (p.SubRowB?.Blend ?? RowBlend.Paint) != RowBlend.Paint);

    /// <summary>
    /// Does EVERY cell this overlay can resolve to print? A pure print lays down no surface, so relief, ambient
    /// occlusion and skin-tone suppression are skipped. An unconfigured index cell paints, so one is enough to
    /// make this false (the safe direction).
    /// </summary>
    internal static bool AllRowsPrint(Dictionary<int, ColorTableRowOverride> rows, bool hasIndex)
    {
        if (!hasIndex)
        {
            rows.TryGetValue(15, out var r16);
            return (r16?.A.Blend ?? RowBlend.Paint) != RowBlend.Paint;
        }
        for (int p = 0; p < 16; p++)
        {
            if (!rows.TryGetValue(p, out var r)) return false;
            if (r.A.Blend == RowBlend.Paint || r.B.Blend == RowBlend.Paint) return false;
        }
        return true;
    }

    /// <summary>
    /// PAINT COVERAGE: the part of a coverage buffer that actually puts colour on the material, with the print
    /// rows taken out. A print must not claim territory, cast an ambient-occlusion shadow, bleach skin tone or
    /// register a glow map, and each of those reads this coverage. Fractional where an index cell lerps between
    /// a painting and a printing sub-row. Returns the input untouched when nothing prints; never mutates it.
    /// <paramref name="hasIndex"/> says whether the overlay DECLARES an index: if it does but
    /// <paramref name="idx"/> is null, the coverage is returned unchanged and the caller warns.
    /// </summary>
    internal static byte[] PaintCoverage(byte[] cov, byte[]? idx,
        Dictionary<int, ColorTableRowOverride> rows, int w, int h, bool hasIndex = false)
    {
        var t0 = PhaseCounter.Begin();
        try { return PaintCoverageCore(cov, idx, rows, w, h, hasIndex); }
        finally { blendCovStats.Stop(t0); }
    }

    /// <summary>Body of <see cref="PaintCoverage"/>, split out only so the call can be timed.</summary>
    private static byte[] PaintCoverageCore(byte[] cov, byte[]? idx,
        Dictionary<int, ColorTableRowOverride> rows, int w, int h, bool hasIndex = false)
    {
        if (!AnyBlendRow(rows)) return cov;

        if (idx == null)
        {
            // Declared an index but has none: all-paint is the pre-blend behaviour, so a broken _id degrades to
            // "the print does not print" rather than claiming territory on texels nobody could route.
            if (hasIndex) return cov;

            // Genuinely flat: every texel resolves to row 16 sub-row A, so it is all or nothing.
            rows.TryGetValue(15, out var r16);
            if ((r16?.A.Blend ?? RowBlend.Paint) == RowBlend.Paint) return cov;
            return new byte[cov.Length];   // a pure print paints nowhere
        }

        const int Pairs = 16;
        var fA = new float[Pairs];
        var fB = new float[Pairs];
        for (int p = 0; p < Pairs; p++)
        {
            var pair = rows.TryGetValue(p, out var r) ? r : new ColorTableRowOverride();
            fA[p] = pair.A.Blend == RowBlend.Paint ? 1f : 0f;
            fB[p] = pair.B.Blend == RowBlend.Paint ? 1f : 0f;
        }

        var dst = (byte[])cov.Clone();
        // Floored to a whole number of texels: the body reads idx[i + 1], so a buffer that is not a multiple
        // of four would let the last iteration read one past the end.
        int n = Math.Min(Math.Min(dst.Length, idx.Length), w * h * 4) / 4 * 4;
        ParallelPixels(0, n, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                int   pairIdx = idx[i] / 17;
                float blendA  = idx[i + 1] / 255f;
                float frac    = fB[pairIdx] + (fA[pairIdx] - fB[pairIdx]) * blendA;
                dst[i + 3] = (byte)(dst[i + 3] * frac);
            }
        });
        return dst;
    }

    /// <summary>
    /// Does this coverage buffer cover anything at all? <see cref="AllRowsPrint"/> is rarely true for an indexed
    /// overlay, so this is how the surface phases ask <see cref="PaintCoverage"/>'s result whether any surface
    /// is left. Serial and short-circuiting on the first covered texel.
    /// </summary>
    internal static bool AnyCoverage(byte[]? cov)
    {
        if (cov == null) return false;
        for (int a = 3; a < cov.Length; a += 4)
            if (cov[a] != 0) return true;
        return false;
    }

    internal static Dictionary<int, ColorTableRowOverride> BuildRowDict(List<ColorTableRowPreset>? presets)
    {
        var dict = new Dictionary<int, ColorTableRowOverride>();
        if (presets == null) return dict;
        foreach (var p in presets)
        {
            var row = new ColorTableRowOverride();
            if (p.SubRowA is { } a)
            {
                if (a.Diffuse != null) (row.A.DiffuseR, row.A.DiffuseG, row.A.DiffuseB) = ParseHex(a.Diffuse);
                row.A.Emissive = a.Emissive;
                row.A.Opacity  = a.Opacity;
                row.A.Blend    = a.Blend;
            }
            if (p.SubRowB is { } b)
            {
                if (b.Diffuse != null) (row.B.DiffuseR, row.B.DiffuseG, row.B.DiffuseB) = ParseHex(b.Diffuse);
                row.B.Emissive = b.Emissive;
                row.B.Opacity  = b.Opacity;
                row.B.Blend    = b.Blend;
            }
            dict[p.Row - 1] = row; // 1-based JSON → 0-based internal
        }
        return dict;
    }

    internal static (float r, float g, float b) ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        int v = Convert.ToInt32(hex, 16);
        return ((v >> 16 & 0xFF) / 255f, (v >> 8 & 0xFF) / 255f, (v & 0xFF) / 255f);
    }

    // Apply per-pixel opacity from the index texture, blending sub-row A/B values just
    // like diffuse color and emissive. Returns a new array; src and pngCache are not mutated.
    /// <summary>Timing shim — see the blend sub-phase counters. Body unchanged, in <c>…Core</c>.</summary>
    internal static byte[] ApplyIndexedOpacity(byte[] src, byte[] idx, Dictionary<int, ColorTableRowOverride> rows)
    {
        var t0 = PhaseCounter.Begin();
        try { return ApplyIndexedOpacityCore(src, idx, rows); }
        finally { blendCovStats.Stop(t0); }
    }

    private static byte[] ApplyIndexedOpacityCore(byte[] src, byte[] idx, Dictionary<int, ColorTableRowOverride> rows)
    {
        var dst = (byte[])src.Clone();

        // Sixteen possible row pairs, resolved once rather than per texel. A separate `present` flag, not a NaN
        // sentinel: a row whose Opacity genuinely IS NaN would otherwise be silently skipped.
        const int Pairs = 16;
        var present = new bool[Pairs];
        var opA = new float[Pairs];
        var opB = new float[Pairs];
        for (int p = 0; p < Pairs; p++)
            if (rows.TryGetValue(p, out var r)) { present[p] = true; opA[p] = r.A.Opacity; opB[p] = r.B.Opacity; }

        ParallelPixels(0, dst.Length, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float a = dst[i + 3] / 255f;
                if (a <= 0f) continue;
                int pairIdx = idx[i] / 17;
                if (!present[pairIdx]) continue;              // no row configured for this pair
                float blendA = idx[i + 1] / 255f;
                float op = opB[pairIdx] + (opA[pairIdx] - opB[pairIdx]) * blendA;
                if (op == 0f) continue;
                float newA = op < 0f
                    ? a * (100f + op) / 100f
                    : a + (1f - a) * op / 100f;
                dst[i + 3] = (byte)(newA * 255f + 0.5f);
            }
        });
        return dst;
    }

    /// <summary>Timing shim — see the blend sub-phase counters. Body unchanged, in <c>…Core</c>.</summary>
    internal static byte[] ScaleOverlayAlpha(byte[] src, int opacity)
    {
        var t0 = PhaseCounter.Begin();
        try { return ScaleOverlayAlphaCore(src, opacity); }
        finally { blendCovStats.Stop(t0); }
    }

    private static byte[] ScaleOverlayAlphaCore(byte[] src, int opacity)
    {
        var dst = (byte[])src.Clone();
        ParallelPixels(3, dst.Length, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                int a = dst[i];
                if (opacity < 0)
                    dst[i] = (byte)(a * (100 + opacity) / 100);
                else if (a > 0)
                    dst[i] = (byte)Math.Min(255, a + (255 - a) * opacity / 100);
            }
        });
        return dst;
    }

    // Apply a per-mod "Masks" map to a coverage RGBA buffer: cov' = cov*W + T, where W (how much of the
    // overlay's own coverage survives, Π(1-aᵢ)) and T (the mask's target opacity, Σ gray·a) come from
    // CombinedMaskAt. The additive term is gated by the base coverage, so a mask can never paint opacity onto
    // bare skin. Returns the input unchanged when the map is null, otherwise a clone (the coverage may be a
    // shared cached array). `additive` withholds T: only the mod's HIGHEST-priority group gets the forced
    // opacity; lower groups see W alone, which erases them from the mask's territory.
    /// <summary>Timing shim — see the blend sub-phase counters. Body unchanged, in <c>…Core</c>.</summary>
    internal static byte[] ApplyCoverageMask(byte[] coverageRgba, byte[]? w, byte[]? t, bool additive = true)
    {
        var t0 = PhaseCounter.Begin();
        try { return ApplyCoverageMaskCore(coverageRgba, w, t, additive); }
        finally { blendCovStats.Stop(t0); }
    }

    private static byte[] ApplyCoverageMaskCore(byte[] coverageRgba, byte[]? w, byte[]? t, bool additive = true)
    {
        if (w == null || t == null) return coverageRgba;
        var dst = (byte[])coverageRgba.Clone();
        int n = Math.Min(w.Length, dst.Length / 4);
        ParallelPixels(0, n, 1, (from, to) =>
        {
            for (int pi = from; pi < to; pi++)
            {
                int baseA = dst[pi * 4 + 3];
                if (baseA == 0) continue;                    // no base coverage → mask has no say (stays 0)
                int v = baseA * w[pi] / 255 + (additive ? t[pi] : 0);
                dst[pi * 4 + 3] = (byte)(v > 255 ? 255 : v);
            }
        });
        return dst;
    }
}
