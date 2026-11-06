using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Interop;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The decisions behind hovering a colour-table row to see it on the character: the row masks, the routing,
/// the hover dwell and grace, the keep-alive, the frame offer, the commit decision, the skin rebuild rule, and the two
/// paints (gear colour table, skin diffuse).</summary>
public class RowHighlightTests
{
    // ── masks ───────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-1")]
    [Trait("Plan", "EDGE-row-hover-highlight-14")]
    public void Pair_mask_covers_both_game_rows_of_the_row()
    {
        Assert.Equal(0b11u, ColorRowMask.Pair(1));
        Assert.Equal((1u << 18) | (1u << 19), ColorRowMask.Pair(10));
        Assert.Equal((1u << 30) | (1u << 31), ColorRowMask.Pair(16));
        Assert.Equal(1u << 5, ColorRowMask.Sub(3, false));
        Assert.Equal(1u << 4, ColorRowMask.Sub(3, true));
        Assert.Equal(1u << 5, ColorRowMask.Row(5));
        Assert.True(ColorRowMask.Contains(ColorRowMask.Pair(10), 18));
        Assert.True(ColorRowMask.Contains(ColorRowMask.Pair(10), 19));
        Assert.False(ColorRowMask.Contains(ColorRowMask.Pair(10), 20));
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-31")]
    public void Out_of_range_rows_give_an_empty_mask()
    {
        Assert.Equal(0u, ColorRowMask.Pair(0));
        Assert.Equal(0u, ColorRowMask.Pair(17));
        Assert.Equal(0u, ColorRowMask.Pair(-1));
        Assert.Equal(0u, ColorRowMask.Sub(0, true));
        Assert.Equal(0u, ColorRowMask.Row(-1));
        Assert.Equal(0u, ColorRowMask.Row(32));
        for (int n = -2; n < 34; n++) Assert.False(ColorRowMask.Contains(0, n));
        Assert.False(ColorRowMask.Contains(uint.MaxValue, 32));
        Assert.False(ColorRowMask.Contains(uint.MaxValue, -1));
    }

    // ── gear paint ──────────────────────────────────────────────────────────

    private static byte[] RandomTable(int seed)
    {
        var t = new byte[ColorTableInterop.TableBytes];
        new Random(seed).NextBytes(t);
        return t;
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-1")]
    public void PaintRows_paints_every_masked_row_and_nothing_else()
    {
        var baseline = RandomTable(7);
        var table = (byte[])baseline.Clone();
        ColorTableHighlighter.PaintRows(table, ColorRowMask.Pair(4), 0.25f, 0.5f, 1f);

        var expected = (byte[])baseline.Clone();
        foreach (int row in new[] { 6, 7 })
        {
            ColorTableInterop.WriteHalfColor(expected, row, ColorTableInterop.OffDiffuse, 0.25f, 0.5f, 1f);
            ColorTableInterop.WriteHalfColor(expected, row, ColorTableInterop.OffEmissive, 0.25f / 8f, 0.5f / 8f, 1f / 8f);
        }
        Assert.Equal(expected, table);

        // Every byte outside game rows 6 and 7 is the baseline's.
        for (int i = 0; i < table.Length; i++)
        {
            int row = i / ColorTableInterop.RowBytes;
            if (row is not (6 or 7)) Assert.Equal(baseline[i], table[i]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(31)]
    [Trait("Plan", "AC-row-hover-highlight-7")]
    public void PaintRows_with_one_row_matches_the_old_write(int gameRow)
    {
        var table = RandomTable(gameRow);
        var old = (byte[])table.Clone();
        // What the single-row Apply wrote before.
        ColorTableInterop.WriteHalfColor(old, gameRow, ColorTableInterop.OffDiffuse, 0.9f, 0.1f, 0.3f);
        ColorTableInterop.WriteHalfColor(old, gameRow, ColorTableInterop.OffEmissive, 0.9f / 8f, 0.1f / 8f, 0.3f / 8f);

        ColorTableHighlighter.PaintRows(table, ColorRowMask.Row(gameRow), 0.9f, 0.1f, 0.3f);
        Assert.Equal(old, table);
    }

    // ── skin paint ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-2")]
    public void Skin_hit_Both_matches_either_sub_row()
    {
        Assert.True(SkinDiffuseGlow.Hit(0xC3, 3, SubRowPick.Both));    // overlay, A, pair 3
        Assert.True(SkinDiffuseGlow.Hit(0x83, 3, SubRowPick.Both));    // overlay, B, pair 3
        Assert.False(SkinDiffuseGlow.Hit(0x84, 3, SubRowPick.Both));   // another pair
        Assert.False(SkinDiffuseGlow.Hit(0x03, 3, SubRowPick.Both));   // not an overlay pixel
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-7")]
    public void Skin_hit_A_and_B_keep_todays_rule()
    {
        for (int b = 0; b < 256; b++)
            for (int pair = 0; pair < 16; pair++)
                foreach (bool isA in new[] { true, false })
                {
                    byte m = (byte)b;
                    bool old = (m & 0x80) != 0 && (m & 0x0F) == pair && ((m & 0x40) != 0) == isA;
                    Assert.Equal(old, SkinDiffuseGlow.Hit(m, pair, isA ? SubRowPick.A : SubRowPick.B));
                }
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-2")]
    public void Skin_paint_colours_hits_gold_and_swizzles_the_rest()
    {
        const int w = 4, h = 2;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 3 + 1);
        var map = new byte[w * h];
        map[1] = 0x80 | 0x40 | 5;   // pair 5, A
        map[6] = 0x80 | 5;          // pair 5, B
        map[2] = 0x80 | 4;          // pair 4

        var bgra = SkinDiffuseGlow.Paint(rgba, w, h, [(map, w, h)], 5, SubRowPick.Both);

        for (int px = 0; px < w * h; px++)
        {
            int p = px * 4;
            if (px is 1 or 6)
                Assert.Equal(new byte[] { 0x30, 0xC4, 0xF4, 0xFF }, bgra[p..(p + 4)]);
            else
                Assert.Equal(new[] { rgba[p + 2], rgba[p + 1], rgba[p], rgba[p + 3] }, bgra[p..(p + 4)]);
        }

        // A pin on sub-row A lights only the A pixel.
        var onlyA = SkinDiffuseGlow.Paint(rgba, w, h, [(map, w, h)], 5, SubRowPick.A);
        Assert.Equal(0x30, onlyA[4]);
        Assert.NotEqual(0x30, onlyA[24]);
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-2")]
    public void Skin_paint_unions_maps_and_scales_a_smaller_map()
    {
        const int w = 4, h = 4;
        var rgba = new byte[w * h * 4];
        var full = new byte[w * h];
        full[0] = 0x80 | 2;                 // pixel (0,0)
        var half = new byte[2 * 2];
        half[3] = 0x80 | 0x40 | 2;          // half-res (1,1) → full-res (2..3, 2..3)

        var bgra = SkinDiffuseGlow.Paint(rgba, w, h, [(full, w, h), (half, 2, 2)], 2, SubRowPick.Both);

        var lit = new HashSet<(int X, int Y)>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (bgra[(y * w + x) * 4] == 0x30) lit.Add((x, y));
        Assert.Equal(new HashSet<(int, int)> { (0, 0), (2, 2), (3, 2), (2, 3), (3, 3) }, lit);
    }

    // ── skin rebuild rule ───────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-10")]
    public void Pick_is_part_of_skin_target_identity()
    {
        var list = new List<SkinGlowTarget>();
        Assert.False(SkinPreviewRule.SameTarget(list, 3, SubRowPick.A, list, 3, SubRowPick.Both));
        Assert.True(SkinPreviewRule.SameTarget(list, 3, SubRowPick.Both, list, 3, SubRowPick.Both));
        Assert.False(SkinPreviewRule.SameTarget(list, 3, SubRowPick.Both, list, 4, SubRowPick.Both));
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-18")]
    public void A_republished_list_is_a_different_target()
    {
        var t = new SkinGlowTarget("x_d.tex", new byte[1], 1, 1);
        var a = new List<SkinGlowTarget> { t };
        var b = new List<SkinGlowTarget> { t };
        Assert.False(SkinPreviewRule.SameTarget(a, 3, SubRowPick.Both, b, 3, SubRowPick.Both));
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-16")]
    [Trait("Plan", "EDGE-row-hover-highlight-17")]
    public void A_failed_target_rebuilds_rate_limited()
    {
        Assert.False(SkinPreviewRule.NeedsBuild(true, false, 1, 0, 10_000));    // same, fine: renew only
        Assert.True(SkinPreviewRule.NeedsBuild(false, false, 1, 0, 0));         // a different (or expired → new) target
        Assert.False(SkinPreviewRule.NeedsBuild(true, true, 1, 0, 499));        // failed, too soon
        Assert.True(SkinPreviewRule.NeedsBuild(true, true, 1, 0, 500));         // failed, due
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-38")]
    public void Attempts_stop_at_the_cap_until_the_target_changes()
    {
        Assert.True(SkinPreviewRule.NeedsBuild(true, true, 2, 0, 10_000));
        Assert.False(SkinPreviewRule.NeedsBuild(true, true, SkinPreviewRule.MaxAttempts, 0, 10_000));
        Assert.True(SkinPreviewRule.NeedsBuild(false, true, SkinPreviewRule.MaxAttempts, 0, 0));
    }

    // ── keep-alive ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-6")]
    public void Keep_alive_expires_after_ttl_and_two_ticks()
    {
        var k = new PreviewKeepAlive();
        k.Renew(0);
        k.Tick();
        k.Tick();
        Assert.False(k.Expired(499));
        Assert.True(k.Expired(500));
        k.Renew(600);
        Assert.False(k.Expired(1200));   // time passed, but no tick since the renewal
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-7")]
    public void One_long_frame_does_not_expire_a_renewed_preview()
    {
        var k = new PreviewKeepAlive();
        k.Renew(0);
        k.Tick();                         // the one tick after a 900 ms hitch
        Assert.False(k.Expired(900));
        k.Renew(905);                     // the UI's next frame renews
        k.Tick();
        Assert.False(k.Expired(921));
    }

    // ── dwell and grace ─────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-1")]
    public void Gear_dwell_is_immediate()
    {
        var d = new RowHoverDwell();
        Assert.Equal(("s", 3), d.Update(("s", 3), 0, RowHoverDwell.GearDwellMs));
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-2")]
    [Trait("Plan", "EDGE-row-hover-highlight-2")]
    public void Skin_dwell_waits_and_a_sweep_lights_nothing()
    {
        var d = new RowHoverDwell();
        long t = 0;
        for (int row = 1; row <= 8; row++)
            for (int f = 0; f < 50; f += 16, t += 16)
                Assert.Null(d.Update(("s", row), t, RowHoverDwell.SkinDwellMs));

        // Holding one row: nothing until the dwell has passed, then that row.
        var d2 = new RowHoverDwell();
        Assert.Null(d2.Update(("s", 8), 1000, RowHoverDwell.SkinDwellMs));
        Assert.Null(d2.Update(("s", 8), 1199, RowHoverDwell.SkinDwellMs));
        Assert.Equal(("s", 8), d2.Update(("s", 8), 1200, RowHoverDwell.SkinDwellMs));
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-3")]
    public void During_a_new_dwell_the_old_row_stays_active()
    {
        var d = new RowHoverDwell();
        d.Update(("s", 2), 0, RowHoverDwell.SkinDwellMs);
        Assert.Equal(("s", 2), d.Update(("s", 2), 200, RowHoverDwell.SkinDwellMs));
        Assert.Equal(("s", 2), d.Update(("s", 5), 210, RowHoverDwell.SkinDwellMs));
        Assert.Equal(("s", 2), d.Update(("s", 5), 400, RowHoverDwell.SkinDwellMs));
        Assert.Equal(("s", 5), d.Update(("s", 5), 410, RowHoverDwell.SkinDwellMs));
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-3")]
    [Trait("Plan", "EDGE-row-hover-highlight-1")]
    public void Grace_bridges_a_gap_and_then_ends()
    {
        var d = new RowHoverDwell();
        Assert.Equal(("s", 2), d.Update(("s", 2), 0, RowHoverDwell.GearDwellMs));
        Assert.Equal(("s", 2), d.Update(null, 50, RowHoverDwell.GearDwellMs));    // the gap between two buttons
        Assert.Equal(("s", 2), d.Update(null, 100, RowHoverDwell.GearDwellMs));
        Assert.Equal(("s", 3), d.Update(("s", 3), 110, RowHoverDwell.GearDwellMs));
        Assert.Equal(("s", 3), d.Update(("s", 3), 199, RowHoverDwell.GearDwellMs));
        Assert.Equal(("s", 3), d.Update(null, 200, RowHoverDwell.GearDwellMs));   // pointer left the picker
        Assert.Equal(("s", 3), d.Update(null, 348, RowHoverDwell.GearDwellMs));
        Assert.Null(d.Update(null, 349, RowHoverDwell.GearDwellMs));
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-5")]
    public void Reset_forgets_the_active_row()
    {
        var d = new RowHoverDwell();
        d.Update(("s", 2), 0, RowHoverDwell.GearDwellMs);
        d.Reset();
        Assert.Null(d.Update(null, 1, RowHoverDwell.GearDwellMs));
    }

    // ── frame offer ─────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-4")]
    public void First_hovered_offer_of_a_frame_wins_and_Take_clears()
    {
        var f = new RowHoverFrame();
        var first = new RowHoverOffer("a", 3, HighlightRoute.Gear, ["ss_a.mtrl"], null);
        f.Offer(first);
        f.Offer(new RowHoverOffer("b", 7, HighlightRoute.Skin, null, null));
        Assert.Same(first, f.Take());
        Assert.Null(f.Take());
    }

    // ── routing and commit ──────────────────────────────────────────────────

    [Theory]
    [Trait("Plan", "AC-row-hover-highlight-1")]
    [Trait("Plan", "AC-row-hover-highlight-2")]
    [Trait("Plan", "EDGE-row-hover-highlight-11")]
    [InlineData(RenderMode.Cloth, true, 1, true, 1, HighlightRoute.Gear)]
    [InlineData(RenderMode.Glow, true, 2, false, 0, HighlightRoute.Gear)]
    [InlineData(RenderMode.Cloth, false, 1, true, 1, HighlightRoute.None)]
    [InlineData(RenderMode.Cloth, true, 0, true, 1, HighlightRoute.None)]
    [InlineData(RenderMode.Skin, true, 1, true, 1, HighlightRoute.Skin)]
    [InlineData(RenderMode.Skin, true, 1, true, 0, HighlightRoute.None)]
    [InlineData(RenderMode.Skin, true, 1, false, 1, HighlightRoute.None)]
    [InlineData(RenderMode.Skin, true, 5, false, 0, HighlightRoute.None)]
    public void Route_picks_like_the_Glow_button(RenderMode mode, bool hasHighlighter, int leaves,
        bool hasSkin, int skinTargets, HighlightRoute expected)
        => Assert.Equal(expected, RowHighlightRoute.Pick(mode, hasHighlighter, leaves, hasSkin, skinTargets));

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-6")]
    [Trait("Plan", "EDGE-row-hover-highlight-12")]
    public void Commit_ends_everything_when_disabled_or_unroutable()
    {
        Assert.Equal(HoverAction.EndAll, RowHoverCommit.Decide(false, HighlightRoute.Gear, false, false));
        Assert.Equal(HoverAction.EndAll, RowHoverCommit.Decide(false, HighlightRoute.Skin, false, true));
        Assert.Equal(HoverAction.EndAll, RowHoverCommit.Decide(true, HighlightRoute.None, false, false));
        Assert.Equal(HoverAction.EndAll, RowHoverCommit.Decide(true, null, false, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-15")]
    public void Commit_waits_on_a_busy_compositor_for_any_skin_build()
    {
        Assert.Equal(HoverAction.WaitSkin, RowHoverCommit.Decide(true, HighlightRoute.Skin, true, true));
        Assert.Equal(HoverAction.Skin, RowHoverCommit.Decide(true, HighlightRoute.Skin, true, false));
        Assert.Equal(HoverAction.Skin, RowHoverCommit.Decide(true, HighlightRoute.Skin, false, true));
        Assert.Equal(HoverAction.Gear, RowHoverCommit.Decide(true, HighlightRoute.Gear, true, false));
    }

    // ── file stamp ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-36")]
    public void FileStamp_changes_when_a_file_is_rewritten()
    {
        var path = Path.Combine(Path.GetTempPath(), "proteus-stamp-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            Assert.Equal(default, FileStamp.Of(path));          // missing
            File.WriteAllBytes(path, new byte[10]);
            var a = FileStamp.Of(path);
            Assert.NotEqual(default, a);
            Assert.Equal(a, FileStamp.Of(path));
            File.WriteAllBytes(path, new byte[11]);
            Assert.NotEqual(a, FileStamp.Of(path));
        }
        finally { File.Delete(path); }
    }
}
