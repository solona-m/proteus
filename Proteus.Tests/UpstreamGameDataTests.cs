using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Proteus.Services;
using Xunit;
using K = Proteus.Services.SecondSkinService.BodySourceKind;

namespace Proteus.Tests;

/// <summary>
/// #63: vanilla hands we republish smoothed never settled (a game-data answer was counted as missed), so they were
/// re-primed every composite, warned "ResolvePlayer returned our own managed file", and could be cut from our own
/// smoothed output. A republished body with no mod file behind it now settles as game data — and only that.
/// </summary>
public class UpstreamGameDataTests
{
    private const string Glv = "chara/equipment/e0000/model/c0201e0000_glv.mdl";
    private static readonly Dictionary<string, string> None = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-6")]
    [Trait("Plan", "EDGE-issue-sweep-b-15")]
    [Trait("Plan", "EDGE-issue-sweep-b-16")]
    [Trait("Plan", "EDGE-issue-sweep-b-17")]
    [Trait("Plan", "EDGE-issue-sweep-b-30")]
    public void Prime_settles_game_data_only_for_unnamed_republished_bodies()
    {
        var settled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Glv] = @"E:\mods\Hands\glv.mdl" };
        Assert.Equal((@"E:\mods\Hands\glv.mdl", true), CompositorService.PrimeResult(Glv, settled, false, true, false));
        Assert.Equal((Glv, true), CompositorService.PrimeResult(Glv, None, settledAsGameData: true, gameDataMaySettle: true, namesModFile: false));
        Assert.Equal((null, false), CompositorService.PrimeResult(Glv, None, true, true, namesModFile: true));   // a false echo
        const string mtrl = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl";
        Assert.Equal((null, false), CompositorService.PrimeResult(mtrl, None, true, CompositorService.GameDataMaySettle(mtrl, false), false));   // as today
        Assert.Equal((null, false), CompositorService.PrimeResult(Glv, None, settledAsGameData: false, true, false));   // timed out
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-6")]
    [Trait("Plan", "EDGE-issue-sweep-b-30")]
    [Trait("Plan", "EDGE-issue-sweep-b-33")]
    public void Names_mod_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "proteus-names-" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(root, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "Hands"));
        try
        {
            var inside = Path.Combine(mods, "Hands", "glv.mdl");
            var outside = Path.Combine(root, "elsewhere.mdl");
            File.WriteAllBytes(inside, [1]);
            File.WriteAllBytes(outside, [1]);
            Assert.True(CompositorService.NamesModFile(Glv, inside, null, mods, File.Exists));
            Assert.True(CompositorService.NamesModFile(Glv, null, inside, mods, File.Exists));
            Assert.False(CompositorService.NamesModFile(Glv, null, null, mods, File.Exists));
            Assert.False(CompositorService.NamesModFile(Glv, Glv, null, mods, _ => true));   // the shell's echoed game path
            Assert.False(CompositorService.NamesModFile(Glv, outside, null, mods, File.Exists));
            Assert.False(CompositorService.NamesModFile(Glv, inside, null, null, File.Exists));
            File.Delete(inside);
            Assert.False(CompositorService.NamesModFile(Glv, inside, null, mods, File.Exists));   // removed since
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-6")]
    [Trait("Plan", "EDGE-issue-sweep-b-33")]
    public void Vanilla_part_with_echoed_remembered_disk_settles_as_game_data()
    {
        bool named = CompositorService.NamesModFile(Glv, Glv, null, @"E:\mods", _ => true);
        Assert.False(named);
        Assert.Equal((Glv, true), CompositorService.PrimeResult(Glv, None, true, true, named));
        Assert.Equal(Glv, CompositorService.SettledAnswer(Glv, true, Glv, _ => false, _ => false));
        Assert.Equal(K.Settled, SecondSkinService.ChooseBodySource(true, hasSettled: true, true, true));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-6")]
    [Trait("Plan", "EDGE-issue-sweep-b-20")]
    public void Masked_answer()
    {
        Assert.Equal((@"E:\mods\Hands\glv.mdl", false), CompositorService.MaskedAnswer(Glv, @"E:\mods\Hands\glv.mdl", _ => true));
        Assert.Equal(((string?)null, false), CompositorService.MaskedAnswer(Glv, Glv, _ => true));   // game data, quietly
        Assert.Equal(((string?)null, true), CompositorService.MaskedAnswer(Glv, null, _ => true));
        Assert.Equal(((string?)null, true), CompositorService.MaskedAnswer(Glv, @"E:\mods\gone.mdl", _ => false));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-6")]
    [Trait("Plan", "EDGE-issue-sweep-b-18")]
    public void Settled_answer()
    {
        const string disk = @"E:\mods\Hands\glv.mdl";
        Assert.Equal(disk, CompositorService.SettledAnswer(Glv, true, disk, _ => true, _ => false));
        Assert.Equal(Glv, CompositorService.SettledAnswer(Glv, true, Glv, _ => false, _ => false));
        Assert.Null(CompositorService.SettledAnswer(Glv, false, disk, _ => true, _ => false));   // cleared by InvalidateUpstreamCache
        Assert.Null(CompositorService.SettledAnswer(Glv, true, disk, _ => true, _ => true));   // our own output
        Assert.Null(CompositorService.SettledAnswer(Glv, true, disk, _ => false, _ => false));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-6")]
    [Trait("Plan", "EDGE-issue-sweep-b-16")]
    [Trait("Plan", "EDGE-issue-sweep-b-20")]
    public void Upstream_wiring()
    {
        var up = Source("Services/CompositorService.Upstream.cs");
        Assert.Contains("PrimeResult(p, settled, gameData, GameDataMaySettle(p, republishedBodies.Contains(p)),", up);
        Assert.Contains("NamesModFile(p, remembered, keptSource, modsRoot, File.Exists)", up);
        Assert.Contains("&& ModFolderOf(c, modsRoot) != null", up);
        Assert.Contains("else _upstreamIsGameData[p] = 0;", up);   // MaterialComposite's game-data flag unchanged
        Assert.Contains("MaskedAnswer(gamePath,", up);
        Assert.Contains("SettledAnswer(gamePath, _upstreamSettled.ContainsKey(gamePath)", up);
        int miss = up.IndexOf("missed++;", StringComparison.Ordinal);
        Assert.True(miss > up.IndexOf("if (isSettled && memo is { } disk)", StringComparison.Ordinal), "missed only on the not-settled branch");
        Assert.Contains("ResolveUpstream(p, bookkeeping: true)", Source("Services/CompositorService.Events.cs"));
    }

    // ── #70: vanilla skin textures we composite from settle as game data on the second game-data prime ──────────────

    private const string Base = "chara/human/c0201/obj/body/b0001/texture/c0201b0001_base.tex";

    [Fact]
    [Trait("Plan", "AC-issue-sweep-c-11")]
    public void Prime_settles_vanilla_textures_on_the_second_game_data_prime()
    {
        Assert.True(CompositorService.GameDataMaySettle(Base, false));
        // First game-data prime since the change: missed, and marked.
        Assert.Equal((null, false), CompositorService.PrimeResult(Base, None, true, true, false, sawGameDataBefore: false));
        Assert.True(CompositorService.TexGameDataMarkAfter(Base, settledNow: false, true, false));
        // Second: settled as game data (the memo is the game path), the mark consumed — and the answer is quiet.
        Assert.Equal((Base, true), CompositorService.PrimeResult(Base, None, true, true, false, sawGameDataBefore: true));
        Assert.False(CompositorService.TexGameDataMarkAfter(Base, settledNow: true, true, false));
        Assert.Equal(((string?)null, false), CompositorService.MaskedAnswer(Base, Base, _ => true));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-31")]
    public void Prime_does_not_settle_a_texture_named_by_a_mod_file()
    {
        Assert.Equal((null, false), CompositorService.PrimeResult(Base, None, true, true, namesModFile: true, sawGameDataBefore: true));
        Assert.False(CompositorService.TexGameDataMarkAfter(Base, false, true, namesModFile: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-32")]
    public void Materials_keep_missing_and_the_status_window_hides_game_data()
    {
        const string mtrl = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl";
        Assert.False(CompositorService.GameDataMaySettle(mtrl, false));
        Assert.Equal((null, false), CompositorService.PrimeResult(mtrl, None, true, false, false, sawGameDataBefore: true));
        Assert.False(CompositorService.TexGameDataMarkAfter(mtrl, false, true, false));
        var up = Source("Services/CompositorService.Upstream.cs");
        Assert.Contains("else _upstreamIsGameData[p] = 0;", up);
        Assert.Contains("&& !string.Equals(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase))   // game data: no mod behind it", up);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-33")]
    public void A_timed_out_texture_prime_is_missed_and_drops_the_mark()
    {
        Assert.Equal((null, false), CompositorService.PrimeResult(Base, None, settledAsGameData: false, true, false, sawGameDataBefore: true));
        Assert.False(CompositorService.TexGameDataMarkAfter(Base, false, settledAsGameData: false, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-34")]
    public void Texture_named_by_last_composite_is_not_settled_after_invalidation()
    {
        var root = Path.Combine(Path.GetTempPath(), "proteus-tex-" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(root, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "Skin"));
        try
        {
            // The memo was cleared by the mod change; the last composite read the texture from a mod that is still there.
            var lastRead = Path.Combine(mods, "Skin", "base.tex");
            File.WriteAllBytes(lastRead, [1]);
            bool named = CompositorService.NamesModFile(Base, lastRead, null, mods, File.Exists);
            Assert.True(named);
            Assert.Equal((null, false), CompositorService.PrimeResult(Base, None, true, true, named, sawGameDataBefore: true));
        }
        finally { Directory.Delete(root, true); }
        var up = Source("Services/CompositorService.Upstream.cs");
        Assert.Contains("lastUpstreams.TryGetValue(p, out var lr) ? lr : null;", up);
        Assert.Contains("named = NamesModFile(p, lastRead, null, modsRoot, File.Exists);", up);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-30")]
    [Trait("Plan", "EDGE-issue-sweep-c-48")]
    public void Invalidation_clears_the_texture_marks()
    {
        // Vanilla settled; a texture mod enabled (invalidate: every mark cleared); one game-data echo → not settled.
        var ev = Source("Services/CompositorService.Events.cs");
        int inv = ev.IndexOf("private void InvalidateUpstreamCache(", StringComparison.Ordinal);
        Assert.True(inv > 0);
        var body = ev[inv..ev.IndexOf("}", ev.IndexOf("_upstreamByGamePath.Clear();", inv, StringComparison.Ordinal), StringComparison.Ordinal)];
        Assert.Contains("_texGameDataSeen.Clear();", body);
        Assert.Contains("_upstreamByGamePath.Clear();", body);
        bool sawBefore = false;   // cleared
        Assert.Equal((null, false), CompositorService.PrimeResult(Base, None, true, true, false, sawBefore));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-47")]
    public void Disabled_texture_mod_settles_on_the_composite_after_next()
    {
        // The mod is switched off (invalidate). Prime 1: game data, marked. Prime 2: game data again, settled.
        bool mark = false;
        var (_, first) = CompositorService.PrimeResult(Base, None, true, true, false, mark);
        mark = CompositorService.TexGameDataMarkAfter(Base, first, true, false);
        Assert.False(first);
        Assert.True(mark);
        var (memo, second) = CompositorService.PrimeResult(Base, None, true, true, false, mark);
        Assert.True(second);
        Assert.Equal(Base, memo);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-47")]
    public void A_mod_file_prime_clears_the_mark()
    {
        var settled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Base] = @"E:\mods\Skinase.tex" };
        var (memo, ok) = CompositorService.PrimeResult(Base, settled, false, true, false, sawGameDataBefore: true);
        Assert.True(ok);
        Assert.Equal(@"E:\mods\Skinase.tex", memo);
        Assert.False(CompositorService.TexGameDataMarkAfter(Base, ok, false, false));
        // Bodies still settle on their first game-data prime (#63 unchanged).
        Assert.Equal((Glv, true), CompositorService.PrimeResult(Glv, None, true, true, false, sawGameDataBefore: false));
    }

    private static string Source(string relative, [CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", relative));
}
