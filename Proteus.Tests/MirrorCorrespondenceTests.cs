using System;
using System.IO;
using BitMiracle.LibTiff.Classic;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Locks <see cref="UVRemapService.MirrorU"/> to the data it was measured from.
/// <para/>
/// The whole un-mirroring feature rests on one fact: bibo lays the character's two sides out as mirror
/// images about u = 0.5, so a -X vertex reads <c>1 - u</c>. The alternative — the two halves being a plain
/// translate, <c>u - 0.5</c> — would put every un-mirrored vertex somewhere else entirely, and the failure
/// looks plausible in a screenshot (art on both sides, just wrong). It is not something a reviewer can spot.
/// <para/>
/// So the scorer that decided it runs here against the SHIPPED transfer map rather than being recorded only
/// as a comment. If a future map swap changes bibo's island layout, this fails instead of the feature
/// quietly inverting.
/// </summary>
public class MirrorCorrespondenceTests
{
    /// <summary>
    /// The shipped map, found by walking up from the test binary, or null when this machine does not have
    /// the real thing. Its <c>Valid</c> mask IS bibo's island layout (see
    /// <see cref="UVRemapService.IslandMask"/>), which is what makes it the right thing to score.
    /// <para/>
    /// Existing is not enough, and that distinction is the whole reason this returns null rather than a path.
    /// The maps are 128 MB each and tracked in Git LFS, which <c>actions/checkout</c> does NOT fetch unless
    /// asked — so on CI this file is present and is a 130-byte text pointer. Handing that to
    /// <see cref="Tiff.Open"/> returns null, and the tests died on the resulting Assert.NotNull with a
    /// message about a TIFF handle: an opaque failure, in the one place a maintainer would read it as the
    /// mirror assumption having broken. Reading the magic tells the two apart.
    /// </summary>
    private static string? FindMap()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var p = Path.Combine(dir.FullName, "Proteus", "uvmaps", "gen3_to_bibo_transfer.tif");
            if (File.Exists(p)) return IsTiff(p) ? p : null;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Whether the file opens with a TIFF byte-order mark — "II" 42 little-endian, or "MM" 42 big.
    /// An LFS pointer begins "version https://…", so four bytes settle it.</summary>
    private static bool IsTiff(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (s.Read(magic) < 4) return false;
            return (magic[0] == (byte)'I' && magic[1] == (byte)'I' && magic[2] == 42 && magic[3] == 0)
                || (magic[0] == (byte)'M' && magic[1] == (byte)'M' && magic[2] == 0 && magic[3] == 42);
        }
        catch { return false; }
    }

    /// <summary>The map's Valid mask, as a bibo-space island bitmap.</summary>
    private static bool[] LoadIslands(string path, out int w, out int h)
    {
        using var tiff = Tiff.Open(path, "r");
        Assert.NotNull(tiff);
        w = tiff!.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        h = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();

        var valid = new bool[w * h];
        var scan = new byte[tiff.ScanlineSize()];
        for (int row = 0; row < h; row++)
        {
            tiff.ReadScanline(scan, row);
            // 4 x u16 per pixel: srcX, srcY, (unused), valid.
            for (int x = 0; x < w; x++)
                valid[row * w + x] = BitConverter.ToUInt16(scan, x * 8 + 6) > 0;
        }
        return valid;
    }

    /// <summary>
    /// IoU of the LEFT half's island mask against the right half pulled back through
    /// <paramref name="rightOfLeft"/>. The candidate that describes the real correspondence scores near 1;
    /// a wrong one cannot, because a body unwrap is far too irregular to overlap itself by accident.
    /// </summary>
    private static double Iou(bool[] valid, int w, int h, Func<int, int> rightOfLeft)
    {
        int half = w / 2, inter = 0, union = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < half; x++)
            {
                int rx = rightOfLeft(x);
                if (rx < half || rx >= w) continue;
                bool a = valid[y * w + x], b = valid[y * w + rx];
                if (a && b) inter++;
                if (a || b) union++;
            }
        return union == 0 ? 0 : (double)inter / union;
    }

    [Fact]
    public void Bibos_two_halves_are_a_reflection_not_a_translate()
    {
        var path = FindMap();
        if (path == null) return;   // no real map here — like every other fixture-backed test in this suite

        var valid = LoadIslands(path, out int w, out int h);
        int half = w / 2;

        double mirror = Iou(valid, w, h, x => w - 1 - x);
        double translate = Iou(valid, w, h, x => x + half);

        // Measured 0.998 vs 0.681. The gap is the point: these are not close, so a threshold anywhere
        // between them pins the answer without being brittle about the exact score.
        Assert.True(mirror > 0.95, $"mirror IoU {mirror:F4} — bibo's halves no longer read as a reflection");
        Assert.True(translate < 0.80, $"translate IoU {translate:F4} — unexpectedly high");
        Assert.True(mirror > translate + 0.2, $"mirror {mirror:F4} vs translate {translate:F4} — no longer decisive");

        // And the reflection is about the sheet's exact centre, not a few texels off it: shifting either way
        // makes it worse. Without this the test would still pass on a layout offset by a small margin, which
        // would smear the art sideways by that margin on every un-mirrored vertex.
        foreach (int d in new[] { -8, -4, 4, 8 })
            Assert.True(Iou(valid, w, h, x => w - 1 - x + d) < mirror,
                $"mirror at offset {d} scores at least as well as at 0 — the centre is not where it is assumed");
    }

    [Fact]
    public void The_two_halves_carry_the_same_island_area()
    {
        var path = FindMap();
        if (path == null) return;   // no real map here — like every other fixture-backed test in this suite

        var valid = LoadIslands(path, out int w, out int h);
        int half = w / 2, left = 0, right = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (valid[y * w + x]) { if (x < half) left++; else right++; }

        // A mirrored pair of halves must hold the same area. Measured within 0.02%; 2% is loose enough to
        // survive a re-baked map and tight enough to catch a half that stopped being a mirror of the other.
        Assert.True(left > 0 && right > 0);
        double ratio = (double)left / right;
        Assert.True(Math.Abs(ratio - 1.0) < 0.02, $"left/right island area = {ratio:F4}");
    }

    /// <summary>
    /// <see cref="UVRemapService.MirrorU"/> is the reflection the scores above chose, and it is its own
    /// inverse — which is what lets one function serve both directions of the un-mirroring.
    /// </summary>
    [Fact]
    public void MirrorU_reflects_about_the_middle_and_is_its_own_inverse()
    {
        Assert.Equal(0.5f, UVRemapService.MirrorU(0.5f));
        Assert.Equal(1f, UVRemapService.MirrorU(0f));
        Assert.Equal(0f, UVRemapService.MirrorU(1f));

        foreach (var u in new[] { 0f, 0.13f, 0.5f, 0.87f, 1f })
            Assert.Equal(u, UVRemapService.MirrorU(UVRemapService.MirrorU(u)), 6);
    }
}
