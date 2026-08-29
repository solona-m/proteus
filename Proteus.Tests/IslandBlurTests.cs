using System;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests for the UV-island-restricted blur that keeps AO coming from the mask rather than from the UV
/// layout: <see cref="CompositorService.LabelIslands"/> and
/// <see cref="CompositorService.BlurCoverageWithinIslands"/>.
/// </summary>
public class IslandBlurTests
{
    /// <summary>Builds a plane by painting rectangles: (x0, y0, x1, y1) inclusive, all set to <paramref name="v"/>.</summary>
    private static byte[] Plane(int w, int h, byte v, params (int X0, int Y0, int X1, int Y1)[] rects)
    {
        var p = new byte[w * h];
        foreach (var r in rects)
            for (int y = r.Y0; y <= r.Y1; y++)
                for (int x = r.X0; x <= r.X1; x++)
                    p[y * w + x] = v;
        return p;
    }

    // ── LabelIslands ─────────────────────────────────────────────────────────

    [Fact]
    public void LabelIslands_SeparatedBlobs_GetDistinctLabels()
    {
        const int w = 16, h = 8;
        // two 4x4 blobs with a 4px gutter between them
        var inside = Plane(w, h, 255, (1, 1, 4, 4), (9, 1, 12, 4));
        var labels = CompositorService.LabelIslands(inside, w, h, out int count);

        Assert.Equal(2, count);
        Assert.Equal(0, labels[0]);                        // padding stays 0
        int a = labels[1 * w + 1], b = labels[1 * w + 9];
        Assert.NotEqual(0, a);
        Assert.NotEqual(0, b);
        Assert.NotEqual(a, b);
        // every texel of a blob carries that blob's label
        for (int y = 1; y <= 4; y++)
            for (int x = 1; x <= 4; x++)
                Assert.Equal(a, labels[y * w + x]);
    }

    [Fact]
    public void LabelIslands_UShape_MergesTheTwoArms()
    {
        // A U: the arms are separate on every row until the bottom joins them. This is the case that only
        // passes if the union-find actually merges provisional labels rather than just propagating them.
        const int w = 9, h = 6;
        var inside = Plane(w, h, 255, (1, 0, 2, 5), (6, 0, 7, 5), (1, 4, 7, 5));
        var labels = CompositorService.LabelIslands(inside, w, h, out int count);

        Assert.Equal(1, count);
        Assert.Equal(labels[0 * w + 1], labels[0 * w + 6]);   // top of each arm, same island
        Assert.Equal(1, labels[0 * w + 1]);
    }

    [Fact]
    public void LabelIslands_EmptyMask_HasNoIslands()
    {
        var labels = CompositorService.LabelIslands(new byte[64], 8, 8, out int count);
        Assert.Equal(0, count);
        Assert.All(labels, l => Assert.Equal(0, l));
    }

    // ── BlurCoverageWithinIslands ────────────────────────────────────────────

    [Fact]
    public void BlurWithinIslands_DoesNotBleedAcrossAGutter()
    {
        // The property the whole change exists for: a fully covered island next to an empty one, separated
        // by a gutter far narrower than the blur radius. A plain blur reaches across; this must not.
        const int w = 64, h = 16, radius = 8;
        var inside = Plane(w, h, 255, (0, 0, 27, 15), (32, 0, 63, 15));   // 4px gutter at x=28..31
        var labels = CompositorService.LabelIslands(inside, w, h, out int count);
        var owner = CompositorService.NearestIslandOwner(labels, w, h);
        Assert.Equal(2, count);

        var cover = Plane(w, h, 255, (0, 0, 27, 15));                     // left island fully covered
        var plain = CompositorService.BlurCoverage(cover, w, h, radius);
        var within = CompositorService.BlurCoverageWithinIslands(cover, labels, owner, count, inside, null, w, h, radius);

        // A plain blur pulls the left island's coverage well into the right one.
        Assert.True(plain[8 * w + 33] > 40, $"expected bleed from a plain blur, got {plain[8 * w + 33]}");
        // The island-restricted blur leaves the empty island empty.
        for (int y = 0; y < h; y++)
            for (int x = 32; x < w; x++)
                Assert.Equal(0, within[y * w + x]);
    }

    [Fact]
    public void BlurWithinIslands_InteriorMatchesAPlainBlur()
    {
        // More than a blur reach from any border the window is entirely same-island, so the two must agree.
        const int w = 96, h = 96, radius = 6;
        var inside = Plane(w, h, 255, (0, 0, 95, 95));                    // one island, whole plane
        var labels = CompositorService.LabelIslands(inside, w, h, out int count);
        var owner = CompositorService.NearestIslandOwner(labels, w, h);
        var cover = Plane(w, h, 255, (30, 30, 65, 65));

        var plain = CompositorService.BlurCoverage(cover, w, h, radius);
        var within = CompositorService.BlurCoverageWithinIslands(cover, labels, owner, count, inside, null, w, h, radius);

        for (int y = 4 * radius; y < h - 4 * radius; y++)
            for (int x = 4 * radius; x < w - 4 * radius; x++)
                Assert.Equal(plain[y * w + x], within[y * w + x]);
    }

    [Fact]
    public void NearestIslandOwner_SplitsTheGutterBetweenItsNeighbours()
    {
        // Each padding texel must be claimed by the island it is closest to — that is what lets an island
        // read its own dilation without ever reading the one across the gutter.
        const int w = 16, h = 4;
        var inside = Plane(w, h, 255, (0, 0, 5, 3), (10, 0, 15, 3));   // gutter at x = 6..9
        var labels = CompositorService.LabelIslands(inside, w, h, out int count);
        var owner = CompositorService.NearestIslandOwner(labels, w, h);
        Assert.Equal(2, count);

        int left = labels[0], right = labels[10];
        Assert.Equal(left,  owner[1 * w + 6]);   // nearer the left island
        Assert.Equal(left,  owner[1 * w + 7]);
        Assert.Equal(right, owner[1 * w + 8]);   // nearer the right island
        Assert.Equal(right, owner[1 * w + 9]);
        Assert.Equal(0, owner[1 * w + 0]);       // on an island, not padding — unclaimed
    }

    [Fact]
    public void BlurWithinIslands_CoverageMeetingABorder_IsNotTruncatedThere()
    {
        // A strap that runs off the edge of an island continues on the body across the seam. The island's
        // own padding stands in for that, so a fully covered island must stay fully covered right up to its
        // border rather than dipping — while the island across the gutter still sees none of it.
        const int w = 64, h = 16, radius = 8;
        var inside = Plane(w, h, 255, (0, 0, 27, 15), (32, 0, 63, 15));
        var labels = CompositorService.LabelIslands(inside, w, h, out int count);
        var owner = CompositorService.NearestIslandOwner(labels, w, h);
        var cover = Plane(w, h, 255, (0, 0, 27, 15));
        var within = CompositorService.BlurCoverageWithinIslands(cover, labels, owner, count, inside, null, w, h, radius);

        for (int y = 0; y < h; y++)
        {
            Assert.Equal(255, within[y * w + 27]);   // the border texel itself
            Assert.Equal(255, within[y * w + 26]);
            Assert.Equal(0, within[y * w + 32]);     // and nothing crossed the gutter
        }
    }

    [Fact]
    public void BlurWithinIslands_SeamSourceCarriesCoverageAcrossTheGutter()
    {
        // The point of the seam map: two islands that are far apart in UV but touching on the body. Given
        // the correspondence, a strap on the left island must cast its halo onto the right one — which no
        // amount of texture-space reasoning could ever produce, since nothing near the right island is lit.
        const int w = 64, h = 16, radius = 8;
        var inside = Plane(w, h, 255, (0, 0, 27, 15), (32, 0, 63, 15));
        var labels = CompositorService.LabelIslands(inside, w, h, out int count);
        var owner = CompositorService.NearestIslandOwner(labels, w, h);
        var cover = Plane(w, h, 255, (0, 0, 27, 15));       // only the LEFT island is covered

        // The right island's own gutter (x = 30, 31) continues onto the left island's edge, mirrored.
        var seam = new int[w * h];
        System.Array.Fill(seam, -1);
        for (int y = 0; y < h; y++)
        {
            seam[y * w + 30] = y * w + 27;
            seam[y * w + 31] = y * w + 26;
        }

        var blind = CompositorService.BlurCoverageWithinIslands(cover, labels, owner, count, inside, null, w, h, radius);
        var seamed = CompositorService.BlurCoverageWithinIslands(cover, labels, owner, count, inside, seam, w, h, radius);

        for (int y = 0; y < h; y++)
        {
            Assert.Equal(0, blind[y * w + 32]);                 // without the seam map: nothing crosses
            Assert.True(seamed[y * w + 32] > 0,
                $"expected the halo to cross the seam, got {seamed[y * w + 32]}");
        }
        // ...and it must still be a falloff, not a flood: deep inside the right island stays dark.
        Assert.Equal(0, seamed[8 * w + 60]);
    }

    [Fact]
    public void NormalIndent_DoesNotDifferentiateAcrossAnIslandBorder()
    {
        // The blurred plane steps at an island border because the padding beyond it is filled by a
        // different computation. A central difference straddling that border reports a phantom slope and
        // the indent carves it into a crease following the seam. With the island mask, the border texel
        // must read the same slope as its neighbour one step inside.
        const int w = 32, h = 16;
        var inside = Plane(w, h, 255, (0, 0, 15, 15));      // island is x 0..15; x >= 16 is padding
        var blurred = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x <= 15; x++) blurred[y * w + x] = (byte)(100 + x);   // a gentle real ramp
            for (int x = 16; x < w; x++) blurred[y * w + x] = 240;                // the padding's step
        }
        var strap = new byte[w * h];                        // gate wide open everywhere

        byte[] Flat() { var n = new byte[w * h * 4]; for (int i = 0; i < w * h; i++) { n[i * 4] = 128; n[i * 4 + 1] = 128; } return n; }

        var blind = Flat();
        var aware = Flat();
        CompositorService.ApplyNormalIndent(blind, blurred, strap, w, h, 1f, null, 6, null);
        CompositorService.ApplyNormalIndent(aware, blurred, strap, w, h, 1f, null, 6, inside);

        // One step inside the border both agree — the interior is untouched.
        Assert.Equal(blind[(8 * w + 14) * 4], aware[(8 * w + 14) * 4]);
        // At the border the blind version sees the padding step and tilts much harder.
        int bBlind = Math.Abs(blind[(8 * w + 15) * 4] - 128);
        int bAware = Math.Abs(aware[(8 * w + 15) * 4] - 128);
        Assert.True(bBlind > bAware, $"expected the border-aware tilt to be smaller, got {bAware} vs {bBlind}");
        // and it should match the slope just inside, not the step
        int interior = Math.Abs(aware[(8 * w + 14) * 4] - 128);
        Assert.True(Math.Abs(bAware - interior) <= 1, $"border {bAware} should match interior {interior}");
    }

    [Fact]
    public void BlurWithinIslands_IgnoresASeamTargetThatLandsInPadding()
    {
        // A seam edge near an island corner can map outward into the NEIGHBOUR's padding. Reading that
        // would feed the art's dilated gutter ink back into the blur — the very thing this path exists to
        // keep out — so an off-island target must be refused and the extrapolation used instead.
        const int w = 64, h = 16, radius = 8;
        var inside = Plane(w, h, 255, (0, 0, 27, 15), (32, 0, 63, 15));
        var labels = CompositorService.LabelIslands(inside, w, h, out int count);
        var owner = CompositorService.NearestIslandOwner(labels, w, h);
        var cover = Plane(w, h, 255, (0, 0, 27, 15));

        // Poison the gutter with "dilated ink" and point the right island's own padding straight at it.
        for (int y = 0; y < h; y++)
            for (int x = 28; x <= 31; x++)
                cover[y * w + x] = 255;
        var seam = new int[w * h];
        System.Array.Fill(seam, -1);
        for (int y = 0; y < h; y++)
        {
            seam[y * w + 30] = y * w + 29;      // a PADDING texel — must be refused
            seam[y * w + 31] = y * w + 28;      // ditto
        }

        var got = CompositorService.BlurCoverageWithinIslands(cover, labels, owner, count, inside, seam, w, h, radius);
        var noSeam = CompositorService.BlurCoverageWithinIslands(cover, labels, owner, count, inside, null, w, h, radius);

        for (int y = 0; y < h; y++)
            for (int x = 32; x < w; x++)
                Assert.Equal(noSeam[y * w + x], got[y * w + x]);
    }

    [Fact]
    public void NormalIndent_LeavesTheTextureOuterEdgeAsItWas()
    {
        // The border-aware difference must not change the OUTERMOST row/column, where the difference was
        // already one-sided before the island mask existed. Scaling those up would silently double the
        // indent on a path this fix isn't about — including for bodies with no island mask at all.
        const int w = 32, h = 32;
        var blurred = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) blurred[y * w + x] = (byte)(4 * x + 2 * y);
        var strap = new byte[w * h];
        var inside = Plane(w, h, 255, (0, 0, w - 1, h - 1));    // everything is island — mask can't shorten

        byte[] Flat() { var n = new byte[w * h * 4]; for (int i = 0; i < w * h; i++) { n[i * 4] = 128; n[i * 4 + 1] = 128; } return n; }
        var before = Flat();
        var after = Flat();
        CompositorService.ApplyNormalIndent(before, blurred, strap, w, h, 1f, null, 6, null);
        CompositorService.ApplyNormalIndent(after, blurred, strap, w, h, 1f, null, 6, inside);

        Assert.Equal(before, after);     // identical everywhere, outer edge included
    }

    [Fact]
    public void BlurWithinIslands_TheCacheIsInvisible()
    {
        // The cache holds the plane-INDEPENDENT blurs so they're computed once per material instead of once
        // per mod. It is only safe if it changes nothing: a second call with a DIFFERENT silhouette must
        // give exactly what an uncached call would. That's the whole contract.
        const int w = 64, h = 32, radius = 6;
        var inside = Plane(w, h, 255, (0, 0, 27, 31), (32, 0, 63, 31));
        var labels = CompositorService.LabelIslands(inside, w, h, out int count);
        var owner = CompositorService.NearestIslandOwner(labels, w, h);
        var seam = new int[w * h];
        System.Array.Fill(seam, -1);
        for (int y = 0; y < h; y++) { seam[y * w + 30] = y * w + 27; seam[y * w + 31] = y * w + 26; }

        // Two different mods' silhouettes on the same material.
        var planeA = Plane(w, h, 255, (2, 2, 20, 20));
        var planeB = Plane(w, h, 255, (36, 6, 60, 26), (4, 24, 10, 30));

        var wantA = CompositorService.BlurCoverageWithinIslands(planeA, labels, owner, count, inside, seam, w, h, radius);
        var wantB = CompositorService.BlurCoverageWithinIslands(planeB, labels, owner, count, inside, seam, w, h, radius);

        var cache = new CompositorService.IslandBlurCache();
        var gotA = CompositorService.BlurCoverageWithinIslands(planeA, labels, owner, count, inside, seam, w, h, radius, cache);
        var gotB = CompositorService.BlurCoverageWithinIslands(planeB, labels, owner, count, inside, seam, w, h, radius, cache);
        var gotA2 = CompositorService.BlurCoverageWithinIslands(planeA, labels, owner, count, inside, seam, w, h, radius, cache);

        Assert.Equal(wantA, gotA);    // first call populates
        Assert.Equal(wantB, gotB);    // second reuses the masks with a different plane
        Assert.Equal(wantA, gotA2);   // and reuse doesn't drift
    }

    [Fact]
    public void BlurWithinIslands_TheCacheMissesWhenTheRadiusChanges()
    {
        // Softness drives the radius, and every cached blur is radius-specific. Serving a radius-6 mask
        // blur to a radius-3 pass would silently widen the halo, so the key must include it.
        const int w = 48, h = 24;
        var inside = Plane(w, h, 255, (0, 0, 47, 23));
        var labels = CompositorService.LabelIslands(inside, w, h, out int count);
        var owner = CompositorService.NearestIslandOwner(labels, w, h);
        var plane = Plane(w, h, 255, (10, 6, 30, 18));

        var cache = new CompositorService.IslandBlurCache();
        var r6 = CompositorService.BlurCoverageWithinIslands(plane, labels, owner, count, inside, null, w, h, 6, cache);
        var r3 = CompositorService.BlurCoverageWithinIslands(plane, labels, owner, count, inside, null, w, h, 3, cache);

        Assert.Equal(CompositorService.BlurCoverageWithinIslands(plane, labels, owner, count, inside, null, w, h, 6), r6);
        Assert.Equal(CompositorService.BlurCoverageWithinIslands(plane, labels, owner, count, inside, null, w, h, 3), r3);
        Assert.NotEqual(r6, r3);
    }

    [Fact]
    public void ExtendIntoPadding_IgnoresACacheBuiltForSomethingElse()
    {
        // The cached denominator only divides, so a stale one yields a plausible-but-wrong extension rather
        // than a crash. ExtendIntoPadding must therefore check the cache belongs to ITS mask and radius
        // instead of trusting the caller to have validated it.
        const int w = 48, h = 24, radius = 5;
        var insideA = Plane(w, h, 255, (0, 0, 23, 23));
        var insideB = Plane(w, h, 255, (0, 0, 47, 11));      // a different mask entirely
        var plane = Plane(w, h, 255, (4, 4, 18, 18));

        // Populate the cache against insideA by running the real path.
        var labels = CompositorService.LabelIslands(insideA, w, h, out int count);
        var owner = CompositorService.NearestIslandOwner(labels, w, h);
        var cache = new CompositorService.IslandBlurCache();
        CompositorService.BlurCoverageWithinIslands(plane, labels, owner, count, insideA, null, w, h, radius, cache);

        // Now hand that cache to a call for a DIFFERENT mask. It must not reuse insideA's denominator.
        var want = CompositorService.ExtendIntoPadding(plane, insideB, w, h, radius);
        var got = CompositorService.ExtendIntoPadding(plane, insideB, w, h, radius, cache);
        Assert.Equal(want, got);

        // ...and the legitimate reuse still works.
        var wantA = CompositorService.ExtendIntoPadding(plane, insideA, w, h, radius);
        Assert.Equal(wantA, CompositorService.ExtendIntoPadding(plane, insideA, w, h, radius, cache));
    }

    [Fact]
    public void IslandBlurCache_IsUnusableUntilTheProducerSaysItIsFinished()
    {
        // The fill runs in stages: allocate, store bounding boxes, then blur island by island. A reader that
        // keyed on any of those intermediate fields would take the cached branch while Bd entries are still
        // null. So a half-built entry — bounding boxes present, blurs absent — must NOT match.
        const int w = 32, h = 16, radius = 4;
        var inside = Plane(w, h, 255, (0, 0, 31, 15));
        var labels = CompositorService.LabelIslands(inside, w, h, out int count);
        var owner = CompositorService.NearestIslandOwner(labels, w, h);

        var cache = new CompositorService.IslandBlurCache();
        cache.Reset(labels, owner, inside, null, radius, count);
        Assert.False(cache.Matches(labels, owner, inside, null, radius, count), "fresh Reset must not match");

        // Stand in for the producer having stored the boxes and then failed before filling any blur.
        cache.Bx0 = new int[count + 1]; cache.By0 = new int[count + 1];
        cache.Bx1 = new int[count + 1]; cache.By1 = new int[count + 1];
        Assert.False(cache.Matches(labels, owner, inside, null, radius, count),
            "bounding boxes alone must not advertise the entry as usable");

        // A real, completed pass publishes it.
        var plane = Plane(w, h, 255, (6, 4, 20, 12));
        CompositorService.BlurCoverageWithinIslands(plane, labels, owner, count, inside, null, w, h, radius, cache);
        Assert.True(cache.Matches(labels, owner, inside, null, radius, count));
    }

    [Fact]
    public void BlurWithinIslands_NoLabels_FallsBackToAPlainBlur()
    {
        // gen2 has no transfer map to derive islands from; the silhouette must still blur as it always did.
        const int w = 32, h = 32, radius = 4;
        var cover = Plane(w, h, 255, (8, 8, 23, 23));
        var expected = CompositorService.BlurCoverage(cover, w, h, radius);
        var actual = CompositorService.BlurCoverageWithinIslands(cover, new int[w * h], new int[w * h], 0, new byte[w * h], null, w, h, radius);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The per-island loop runs in parallel, so its output must not depend on scheduling.
    /// <para/>
    /// This is not a general "races are bad" test — it guards a specific consequence. Proteus names every
    /// composited texture by a hash of its content, so a single byte that varies between runs would rename
    /// the output, re-bake it and re-upload it through the sync plugins, intermittently and for no visible
    /// reason. Islands write disjoint texels and cache at their own index, so the result is order-
    /// independent by construction; this is the check that it stays that way.
    /// <para/>
    /// Enough islands to occupy several workers, and enough repeats to make an ordering-dependent bug
    /// show up rather than hide behind a lucky schedule.
    /// </summary>
    [Fact]
    public void BlurWithinIslands_ManyIslands_IsDeterministicUnderParallelism()
    {
        const int w = 256, h = 256, radius = 5;

        // A 6x6 grid of islands separated by gutters, each with its own coverage pattern so no two
        // islands can be confused for one another.
        var rects = new System.Collections.Generic.List<(int, int, int, int)>();
        var covRects = new System.Collections.Generic.List<(int, int, int, int)>();
        for (int gy = 0; gy < 6; gy++)
            for (int gx = 0; gx < 6; gx++)
            {
                int x0 = gx * 42 + 2, y0 = gy * 42 + 2;
                rects.Add((x0, y0, x0 + 33, y0 + 33));
                // Coverage offset per island, so each produces a distinct blur.
                covRects.Add((x0 + (gx % 3), y0 + (gy % 3), x0 + 20 + (gx % 4), y0 + 20 + (gy % 4)));
            }

        var inside = Plane(w, h, 255, rects.ToArray());
        var labels = CompositorService.LabelIslands(inside, w, h, out int count);
        var owner = CompositorService.NearestIslandOwner(labels, w, h);
        Assert.Equal(36, count);

        var cover = Plane(w, h, 255, covRects.ToArray());

        var first = CompositorService.BlurCoverageWithinIslands(cover, labels, owner, count, inside, null, w, h, radius);
        for (int i = 0; i < 24; i++)
        {
            var again = CompositorService.BlurCoverageWithinIslands(cover, labels, owner, count, inside, null, w, h, radius);
            Assert.Equal(first, again);
        }
    }

    /// <summary>
    /// The same, with the shared <see cref="CompositorService.IslandBlurCache"/> in play — the parallel
    /// loop fills Bd/Cd at its own index per island, and the SECOND call reads them back. A cache filled
    /// out of order must produce exactly what an uncached pass does.
    /// </summary>
    [Fact]
    public void BlurWithinIslands_CacheFilledInParallel_MatchesTheUncachedResult()
    {
        const int w = 192, h = 192, radius = 4;

        var rects = new System.Collections.Generic.List<(int, int, int, int)>();
        for (int gy = 0; gy < 4; gy++)
            for (int gx = 0; gx < 4; gx++)
            {
                int x0 = gx * 47 + 3, y0 = gy * 47 + 3;
                rects.Add((x0, y0, x0 + 38, y0 + 38));
            }

        var inside = Plane(w, h, 255, rects.ToArray());
        var labels = CompositorService.LabelIslands(inside, w, h, out int count);
        var owner = CompositorService.NearestIslandOwner(labels, w, h);
        var cover = Plane(w, h, 255, (20, 20, 170, 170));

        var uncached = CompositorService.BlurCoverageWithinIslands(cover, labels, owner, count, inside, null, w, h, radius);

        var cache = new CompositorService.IslandBlurCache();
        var fill = CompositorService.BlurCoverageWithinIslands(cover, labels, owner, count, inside, null, w, h, radius, cache);
        Assert.True(cache.Matches(labels, owner, inside, null, radius, count));
        var read = CompositorService.BlurCoverageWithinIslands(cover, labels, owner, count, inside, null, w, h, radius, cache);

        Assert.Equal(uncached, fill);
        Assert.Equal(uncached, read);
    }
}
