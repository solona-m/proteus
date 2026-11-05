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
        Assert.Equal((Glv, true), CompositorService.PrimeResult(Glv, None, settledAsGameData: true, republishedBody: true, namesModFile: false));
        Assert.Equal((null, false), CompositorService.PrimeResult(Glv, None, true, true, namesModFile: true));   // a false echo
        const string tex = "chara/common/texture/skin_m.tex";
        Assert.Equal((null, false), CompositorService.PrimeResult(tex, None, true, republishedBody: false, false));   // as today
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
        Assert.Contains("PrimeResult(p, settled, gameData, republishedBodies.Contains(p), named)", up);
        Assert.Contains("NamesModFile(p, remembered, keptSource, modsRoot, File.Exists)", up);
        Assert.Contains("&& ModFolderOf(c, modsRoot) != null", up);
        Assert.Contains("else _upstreamIsGameData[p] = 0;", up);   // MaterialComposite's game-data flag unchanged
        Assert.Contains("MaskedAnswer(gamePath,", up);
        Assert.Contains("SettledAnswer(gamePath, _upstreamSettled.ContainsKey(gamePath)", up);
        int miss = up.IndexOf("missed++;", StringComparison.Ordinal);
        Assert.True(miss > up.IndexOf("if (isSettled && memo is { } disk)", StringComparison.Ordinal), "missed only on the not-settled branch");
        Assert.Contains("ResolveUpstream(p, bookkeeping: true)", Source("Services/CompositorService.Events.cs"));
    }

    private static string Source(string relative, [CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", relative));
}
