using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Proteus.Services;
using static Proteus.Services.OverlayBlend;

internal static class IslandBlur
{
    /// <summary>
    /// Replace an AO silhouette's values OUTSIDE the UV islands with a smooth extension of the values just
    /// inside, so the blur that follows sees no step at an island border. Art tools dilate padding with
    /// whatever suits the DIFFUSE, and a plain blur drags that plateau inward into a crease along the seam.
    /// The extension is a normalised (masked) blur; deep padding where too little of the window is in-island
    /// falls to 0. Valid texels are copied through EXACTLY; only padding is rewritten.
    /// </summary>
    internal static byte[] ExtendIntoPadding(byte[] plane, byte[] inside, int w, int h, int radius,
                                             IslandBlurCache? cache = null)
    {
        if (plane.Length < w * h || inside.Length < w * h || w <= 0 || h <= 0) return plane;

        // Masked sums: numerator over in-island values, denominator over in-island weight. The denominator
        // is the blurred island mask — no dependence on `plane` — so it is reused across this material's mods.
        var masked = new byte[w * h];
        ParallelPixels(0, w * h, 1, (from, to) =>
        {
            for (int p = from; p < to; p++) masked[p] = inside[p] != 0 ? plane[p] : (byte)0;
        });
        var num = BlurCoverage(masked, w, h, radius);
        var den = cache != null
            ? cache.PaddingDenominator(inside, radius, () => BlurCoverage(inside, w, h, radius))
            : BlurCoverage(inside, w, h, radius);

        // Below this share of the window being in-island the average is noise, not an extension.
        const int MinWeight = 8;   // ~3% of a full window
        var outp = (byte[])plane.Clone();
        ParallelPixels(0, w * h, 1, (from, to) =>
        {
            for (int p = from; p < to; p++)
            {
                if (inside[p] != 0) continue;                       // on the body — leave exactly as authored
                outp[p] = den[p] >= MinWeight
                    ? (byte)Math.Clamp(num[p] * 255 / den[p], 0, 255)
                    : (byte)0;
            }
        });
        return outp;
    }

    /// <summary>Connected-component labels of a UV-island mask: 0 for padding, 1..<paramref name="count"/>
    /// for islands. Two-pass union-find over 4-connectivity — one linear scan, then a root resolve, which
    /// matters because this runs on a 4096² plane.</summary>
    internal static int[] LabelIslands(byte[] inside, int w, int h, out int count)
    {
        var labels = new int[w * h];
        // parent[i] is the provisional label i's union-find parent. Unions always point the higher index at
        // the lower one, so a root is exactly an i with parent[i] == i, and roots are found in ascending
        // order — which is what lets the remap below resolve in a single forward pass.
        var parent = new List<int> { 0 };
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int p = row + x;
                if (inside[p] == 0) continue;
                int west  = x > 0 ? labels[p - 1] : 0;
                int north = y > 0 ? labels[p - w] : 0;
                if (west == 0 && north == 0)
                {
                    parent.Add(parent.Count);
                    labels[p] = parent.Count - 1;
                }
                else if (west != 0 && north != 0)
                {
                    labels[p] = Math.Min(west, north);
                    int a = Find(west), b = Find(north);
                    if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                }
                else labels[p] = west != 0 ? west : north;
            }
        }

        var remap = new int[parent.Count];
        count = 0;
        for (int i = 1; i < parent.Count; i++)
        {
            int r = Find(i);
            remap[i] = r == i ? ++count : remap[r];   // r < i, so remap[r] is already resolved
        }
        ParallelPixels(0, labels.Length, 1, (from, to) =>
        {
            for (int p = from; p < to; p++) if (labels[p] != 0) labels[p] = remap[Find(labels[p])];
        });
        return labels;
    }

    /// <summary>How far past an island border the seam map must reach to cover the blur's window.
    /// <see cref="BlurCoverage"/> is SEPARABLE, so its window is a SQUARE reaching 2*radius per axis and
    /// 2*radius*sqrt(2) diagonally; a disc of 2*radius leaves the corners unmapped.</summary>
    internal static int SeamReach(int radius) => (int)Math.Ceiling(2 * radius * 1.4143);

    /// <summary>Below this share of the blur window being on the SAME island, the renormalised average is
    /// noise rather than a mean, and the texel keeps its authored value instead.</summary>
    private const int MinIslandWeight = 8;   // ~3% of a full window

    /// <summary>For every padding texel, the label of the CLOSEST island — which island's dilation that bit
    /// of gutter is standing in for. Multi-source breadth-first expansion from the island borders outward, so
    /// each texel is reached first along its shortest path. 0 where nothing is reachable.</summary>
    internal static int[] NearestIslandOwner(int[] labels, int w, int h)
    {
        var owner = new int[w * h];
        var frontier = new List<int>();

        // Seed: padding texels touching an island. Seeding from the islands themselves would enqueue
        // millions of interior texels that can never own anything.
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int p = row + x;
                if (labels[p] != 0 || owner[p] != 0) continue;
                int L = 0;
                if (x > 0 && labels[p - 1] != 0) L = labels[p - 1];
                else if (x < w - 1 && labels[p + 1] != 0) L = labels[p + 1];
                else if (y > 0 && labels[p - w] != 0) L = labels[p - w];
                else if (y < h - 1 && labels[p + w] != 0) L = labels[p + w];
                if (L == 0) continue;
                owner[p] = L;
                frontier.Add(p);
            }
        }

        var next = new List<int>();
        while (frontier.Count > 0)
        {
            next.Clear();
            foreach (int p in frontier)
            {
                int L = owner[p], x = p % w, y = p / w;
                if (x > 0)     Push(p - 1);
                if (x < w - 1) Push(p + 1);
                if (y > 0)     Push(p - w);
                if (y < h - 1) Push(p + w);

                void Push(int q)
                {
                    if (labels[q] != 0 || owner[q] != 0) return;
                    owner[q] = L;
                    next.Add(q);
                }
            }
            (frontier, next) = (next, frontier);
        }
        return owner;
    }

    /// <summary>
    /// Blur an AO silhouette without ever sampling across a UV-island border: each texel averages only texels
    /// of its OWN island (plus the gutter it owns, filled with its own edge continued outward), renormalised
    /// by how much of the window that was. The gutters are narrow, so a plain blur samples straight across
    /// into an unrelated island; the island's own padding keeps a strap's AO intact where it crosses a seam.
    /// </summary>
    /// <summary>
    /// Scratch for <see cref="BlurCoverageWithinIslands"/>: the parts of its work that do NOT depend on the
    /// silhouette being blurred (the island-mask and gutter-mask blurs), computed once and reused for every
    /// mod on a material. Deliberately NOT shared between materials: materials composite in parallel and the
    /// mod loop inside one is sequential, so a per-material instance needs no locking.
    /// </summary>
    internal sealed class IslandBlurCache
    {
        private int[]? labels, owner, seam;
        private byte[]? inside;
        private int radius = -1;
        private int count = -1;
        private bool ready;

        internal int[]? Bx0, By0, Bx1, By1;   // island bounding boxes — a function of labels alone
        internal byte[]?[]? Bd, Cd;           // per-island plane-independent blurs
        private byte[]? padDen;               // ExtendIntoPadding's denominator — a function of inside alone

        /// <summary>
        /// True when everything held was derived from exactly these inputs AND is fully populated. Reference
        /// equality, not content. Only the producer sets <see cref="ready"/>, via <see cref="MarkReady"/> after
        /// the last island: the fill runs in stages, so no field is a safe proxy for "populated".
        /// <paramref name="count"/> is in the key because it sizes Bd/Cd.
        /// </summary>
        internal bool Matches(int[] labels, int[] owner, byte[] inside, int[]? seam, int radius, int count)
            => ready && this.count == count
            && ReferenceEquals(this.labels, labels) && ReferenceEquals(this.owner, owner)
            && ReferenceEquals(this.inside, inside) && ReferenceEquals(this.seam, seam)
            && this.radius == radius;

        internal void Reset(int[] labels, int[] owner, byte[] inside, int[]? seam, int radius, int count)
        {
            ready = false;
            this.labels = labels; this.owner = owner; this.inside = inside; this.seam = seam;
            this.radius = radius; this.count = count;
            Bx0 = By0 = Bx1 = By1 = null;
            Bd = new byte[count + 1][];
            Cd = new byte[count + 1][];
            padDen = null;
        }

        /// <summary>Publish the entry: every island's blurs are now stored. Anything that throws before this
        /// leaves the cache unusable rather than half-filled, so the next call rebuilds instead of reading a
        /// null.</summary>
        internal void MarkReady() => ready = true;

        /// <summary><see cref="ExtendIntoPadding"/>'s blurred island mask, built on first use. Self-validating:
        /// a cache built for a different mask or radius is ignored, since a stale denominator yields a plausible
        /// wrong answer instead of a crash.</summary>
        internal byte[] PaddingDenominator(byte[] inside, int radius, Func<byte[]> build)
        {
            bool mine = ReferenceEquals(this.inside, inside) && this.radius == radius;
            if (mine && padDen != null) return padDen;
            var den = build();
            if (mine) padDen = den;
            return den;
        }
    }

    internal static byte[] BlurCoverageWithinIslands(byte[] plane, int[] labels, int[] owner, int islandCount,
                                                     byte[] inside, int[]? seamSource, int w, int h, int radius,
                                                     IslandBlurCache? cache = null)
    {
        if (w <= 0 || h <= 0 || islandCount <= 0 ||
            plane.Length < w * h || labels.Length < w * h || owner.Length < w * h || inside.Length < w * h)
            return BlurCoverage(plane, w, h, radius);
        if (seamSource != null && seamSource.Length < w * h) seamSource = null;

        int n = islandCount + 1;
        // Everything below that doesn't depend on `plane` is reused across the mods on this material.
        bool cached = cache != null && cache.Matches(labels, owner, inside, seamSource, radius, islandCount);
        if (cache != null && !cached) cache.Reset(labels, owner, inside, seamSource, radius, islandCount);

        // Per-island bounding boxes, so the cost is the sum of island areas rather than islands × map.
        // A function of `labels` alone, and a full-map scan, so it rides the cache too.
        int[] bx0, by0, bx1, by1;
        if (cached)
        {
            bx0 = cache!.Bx0!; by0 = cache.By0!; bx1 = cache.Bx1!; by1 = cache.By1!;
        }
        else
        {
            bx0 = new int[n]; by0 = new int[n]; bx1 = new int[n]; by1 = new int[n];
            for (int i = 0; i < n; i++) { bx0[i] = int.MaxValue; by0[i] = int.MaxValue; bx1[i] = -1; by1[i] = -1; }
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int L = labels[row + x];
                    if (L <= 0 || L >= n) continue;
                    if (x < bx0[L]) bx0[L] = x;
                    if (x > bx1[L]) bx1[L] = x;
                    if (y < by0[L]) by0[L] = y;
                    if (y > by1[L]) by1[L] = y;
                }
            }
            if (cache != null) { cache.Bx0 = bx0; cache.By0 = by0; cache.Bx1 = bx1; cache.By1 = by1; }
        }

        var outp = new byte[w * h];

        // ISLANDS IN PARALLEL: every iteration reads shared inputs read-only, writes only texels carrying its
        // OWN label, and stores its cached blurs at its own index of Bd/Cd, so the result is byte-identical to
        // the serial version (the output filenames are content hashes). Held to half the cores: each island
        // allocates eight crop buffers, and the box blurs inside are themselves parallel.
        var islandOpts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) };
        Parallel.For(1, n, islandOpts, L =>
        {
            if (bx1[L] < 0) return;
            // The window has to be able to hang off the island by its own reach — twice, since the first
            // pass builds the values the second one then reads — or edge texels would be renormalised
            // against a window the crop had already truncated.
            int pad = 3 * radius;
            int ax0 = Math.Max(0, bx0[L] - pad), ax1 = Math.Min(w - 1, bx1[L] + pad);
            int ay0 = Math.Max(0, by0[L] - pad), ay1 = Math.Min(h - 1, by1[L] + pad);
            int cw = ax1 - ax0 + 1, ch = ay1 - ay0 + 1;

            // Pass 1 — the island alone, to learn what its edge values are. The numerator carries the plane
            // and must be rebuilt per mod; the denominator is the island mask, so it is cached.
            var num = new byte[cw * ch];
            byte[]? den = cached ? null : new byte[cw * ch];
            for (int y = 0; y < ch; y++)
            {
                int src = (ay0 + y) * w + ax0, dst = y * cw;
                for (int x = 0; x < cw; x++)
                {
                    if (labels[src + x] != L) continue;
                    num[dst + x] = plane[src + x];
                    if (den != null) den[dst + x] = 255;
                }
            }
            var bn = BlurCoverage(num, cw, ch, radius);
            var bd = cached ? cache!.Bd![L]! : BlurCoverage(den!, cw, ch, radius);
            if (!cached && cache != null) cache.Bd![L] = bd;

            // Pass 2 — the island plus the gutter it owns, that gutter carrying pass 1's continuation of the
            // island's own edge. This is what keeps a strap's AO intact where the strap crosses a UV seam.
            var plane2 = new byte[cw * ch];
            byte[]? mask2 = cached ? null : new byte[cw * ch];
            for (int y = 0; y < ch; y++)
            {
                int src = (ay0 + y) * w + ax0, dst = y * cw;
                for (int x = 0; x < cw; x++)
                {
                    int lp = labels[src + x];
                    if (lp == L)
                    {
                        plane2[dst + x] = plane[src + x];
                        if (mask2 != null) mask2[dst + x] = 255;
                    }
                    else if (lp == 0 && owner[src + x] == L)
                    {
                        // Best case: the mesh says which texel the surface continues into across the seam, so
                        // the gutter carries the REAL neighbouring coverage; with no seam data, continue this
                        // island's own edge outward. The target must itself be ON an island, or the art's
                        // dilated ink feeds straight back into the blur.
                        int q = seamSource != null ? seamSource[src + x] : -1;
                        if (q >= 0 && labels[q] != 0)
                        {
                            plane2[dst + x] = plane[q];
                            if (mask2 != null) mask2[dst + x] = 255;
                            continue;
                        }
                        int d0 = bd[dst + x];
                        if (d0 < MinIslandWeight) continue;      // too far out to have a meaningful value
                        plane2[dst + x] = (byte)Math.Clamp(bn[dst + x] * 255 / d0, 0, 255);
                        if (mask2 != null) mask2[dst + x] = 255;
                    }
                }
            }
            var cn = BlurCoverage(plane2, cw, ch, radius);
            var cd = cached ? cache!.Cd![L]! : BlurCoverage(mask2!, cw, ch, radius);
            if (!cached && cache != null) cache.Cd![L] = cd;
            for (int y = 0; y < ch; y++)
            {
                int src = (ay0 + y) * w + ax0, dst = y * cw;
                for (int x = 0; x < cw; x++)
                {
                    if (labels[src + x] != L) continue;
                    int d = cd[dst + x];
                    outp[src + x] = d >= MinIslandWeight
                        ? (byte)Math.Clamp(cn[dst + x] * 255 / d, 0, 255)
                        : plane[src + x];
                }
            }
        });

        // Every island's blurs are stored, so the entry is now safe for the next mod to read. Set here and
        // not earlier: anything that throws above must leave the cache unusable, not half-filled.
        cache?.MarkReady();

        // The padding still has to carry values that agree with the island edge: the sampler's bilinear tap
        // reaches about a texel past the border, so leaving it at 0 would draw a thin light fringe along
        // every island outline — the very thing this is here to avoid.
        return ExtendIntoPadding(outp, inside, w, h, radius, cache);
    }
}
