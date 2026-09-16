using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Proteus.Services;

/// <summary>
/// Blurred ambient-occlusion silhouettes from recent composites, keyed on everything the blur reads.
/// <para/>
/// The contact shadow is a function of a garment's silhouette, the body's UV islands and seams, and the
/// softness radius — never of colour. Yet every composite rebuilt it: 1.3 s of blur and 0.6 s of apply on a
/// colour edit whose garments had not moved. With the silhouette hashed into the key, an unchanged garment's
/// halo is a lookup, and only a garment that really changed shape pays for a blur.
/// <para/>
/// Bounded by bytes, least-recently-used out. An entry is one byte per texel — 16 MB at 4K — so the default
/// budget holds a few dozen garments' worth, which is more materials × mods than one look has. Entries are
/// handed out SHARED: callers read them and never write, exactly as they already treated the fresh blur.
/// </summary>
internal sealed class AoBlurCache
{
    private readonly object gate = new();
    private readonly Dictionary<string, (byte[] Plane, long Access)> entries = new(StringComparer.Ordinal);
    private long clock, bytes;

    public long BudgetBytes { get; init; } = 512L << 20;

    public byte[]? TryGet(string key)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var e)) return null;
            entries[key] = (e.Plane, ++clock);
            return e.Plane;
        }
    }

    public void Put(string key, byte[] plane)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var old)) bytes -= old.Plane.Length;
            entries[key] = (plane, ++clock);
            bytes += plane.Length;
            while (bytes > BudgetBytes && entries.Count > 1)
            {
                string? victim = null;
                long oldest = long.MaxValue;
                foreach (var (k, v) in entries)
                    if (v.Access < oldest) { oldest = v.Access; victim = k; }
                if (victim == null) break;
                bytes -= entries[victim].Plane.Length;
                entries.Remove(victim);
            }
        }
    }

    public int Clear()
    {
        lock (gate)
        {
            int n = entries.Count;
            entries.Clear();
            bytes = 0;
            return n;
        }
    }
}

/// <summary>
/// The island planes of one material at half resolution, for blurring the AO silhouette there.
/// <para/>
/// A contact shadow is a halo of ~20 texels at 4K; nothing in it survives at texel scale, so blurring at half
/// size and interpolating back up loses nothing anyone can see and costs a quarter of the work. The island
/// machinery needs its labels, owners, inside plane and seam map at the same size as the plane it blurs, so
/// they are downsampled here — ONCE per material, and memoised by reference so the island blur cache (keyed
/// by reference on these arrays) keeps hitting across the mods on that material.
/// </summary>
internal sealed class HalfResIslandPlanes
{
    private int[]? labelsSrc, ownerSrc, seamSrc;
    private byte[]? insideSrc;

    public int[]? Labels, Owner, Seam;
    public byte[]? Inside;

    public void EnsureIslands(int[] labels, int[] owner, byte[] inside, int w, int h)
    {
        if (ReferenceEquals(labelsSrc, labels) && ReferenceEquals(ownerSrc, owner) && ReferenceEquals(insideSrc, inside))
            return;
        labelsSrc = labels; ownerSrc = owner; insideSrc = inside;
        Labels = DownsampleFirstNonZero(labels, w, h);
        Owner  = DownsampleFirstNonZero(owner, w, h);
        Inside = DownsampleMax(inside, w, h);
    }

    /// <summary>One seam map memoised, by reference: the radius is fixed per material, so every mod on it
    /// hands over the same array.</summary>
    public void EnsureSeam(int[]? seam, int w, int h)
    {
        if (ReferenceEquals(seamSrc, seam) && (seam == null) == (Seam == null)) return;
        seamSrc = seam;
        Seam = seam == null ? null : DownsampleSeam(seam, w, h);
    }

    /// <summary>Mean of each 2×2 block, rounded.</summary>
    public static byte[] DownsampleAverage(byte[] src, int w, int h)
    {
        int hw = w / 2, hh = h / 2;
        var dst = new byte[hw * hh];
        Parallel.For(0, hh, y =>
        {
            int r0 = (2 * y) * w, r1 = r0 + w, o = y * hw;
            for (int x = 0; x < hw; x++)
            {
                int i = 2 * x;
                dst[o + x] = (byte)((src[r0 + i] + src[r0 + i + 1] + src[r1 + i] + src[r1 + i + 1] + 2) >> 2);
            }
        });
        return dst;
    }

    /// <summary>Max of each 2×2 block — a half texel on the body is on the body.</summary>
    public static byte[] DownsampleMax(byte[] src, int w, int h)
    {
        int hw = w / 2, hh = h / 2;
        var dst = new byte[hw * hh];
        Parallel.For(0, hh, y =>
        {
            int r0 = (2 * y) * w, r1 = r0 + w, o = y * hw;
            for (int x = 0; x < hw; x++)
            {
                int i = 2 * x;
                dst[o + x] = Math.Max(Math.Max(src[r0 + i], src[r0 + i + 1]), Math.Max(src[r1 + i], src[r1 + i + 1]));
            }
        });
        return dst;
    }

    /// <summary>The first non-zero id in each 2×2 block. Ids are names, not magnitudes, so a max would be
    /// meaningless; "any island present" keeps a one-texel-wide island from vanishing.</summary>
    public static int[] DownsampleFirstNonZero(int[] src, int w, int h)
    {
        int hw = w / 2, hh = h / 2;
        var dst = new int[hw * hh];
        Parallel.For(0, hh, y =>
        {
            int r0 = (2 * y) * w, r1 = r0 + w, o = y * hw;
            for (int x = 0; x < hw; x++)
            {
                int i = 2 * x;
                int v = src[r0 + i];
                if (v == 0) v = src[r0 + i + 1];
                if (v == 0) v = src[r1 + i];
                if (v == 0) v = src[r1 + i + 1];
                dst[o + x] = v;
            }
        });
        return dst;
    }

    /// <summary>A seam map's targets are full-res texel indices; each is moved to the half texel holding it.</summary>
    public static int[] DownsampleSeam(int[] src, int w, int h)
    {
        int hw = w / 2, hh = h / 2;
        var dst = new int[hw * hh];
        Parallel.For(0, hh, y =>
        {
            int r0 = (2 * y) * w, r1 = r0 + w, o = y * hw;
            for (int x = 0; x < hw; x++)
            {
                int i = 2 * x;
                int q = src[r0 + i];
                if (q < 0) q = src[r0 + i + 1];
                if (q < 0) q = src[r1 + i];
                if (q < 0) q = src[r1 + i + 1];
                dst[o + x] = q < 0 ? -1 : (q / w / 2) * hw + (q % w) / 2;
            }
        });
        return dst;
    }

    /// <summary>Bilinear, texel centres aligned, for a single-channel plane.</summary>
    public static byte[] UpsampleBilinear(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh];
        float sx = (float)sw / dw, sy = (float)sh / dh;
        Parallel.For(0, dh, y =>
        {
            float fy = (y + 0.5f) * sy - 0.5f;
            int y0 = (int)MathF.Floor(fy);
            float ty = fy - y0;
            int ya = Math.Clamp(y0, 0, sh - 1), yb = Math.Clamp(y0 + 1, 0, sh - 1);
            int ra = ya * sw, rb = yb * sw, o = y * dw;
            for (int x = 0; x < dw; x++)
            {
                float fx = (x + 0.5f) * sx - 0.5f;
                int x0 = (int)MathF.Floor(fx);
                float tx = fx - x0;
                int xa = Math.Clamp(x0, 0, sw - 1), xb = Math.Clamp(x0 + 1, 0, sw - 1);
                float top = src[ra + xa] + (src[ra + xb] - src[ra + xa]) * tx;
                float bot = src[rb + xa] + (src[rb + xb] - src[rb + xa]) * tx;
                dst[o + x] = (byte)Math.Clamp(top + (bot - top) * ty + 0.5f, 0f, 255f);
            }
        });
        return dst;
    }
}
