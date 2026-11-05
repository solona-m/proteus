using System;
using System.IO;
using System.Runtime.CompilerServices;
using Proteus.Services;
using Xunit;
using K = Proteus.Services.SecondSkinService.BodySourceKind;

namespace Proteus.Tests;

/// <summary>
/// One body source for both phases of a composite (#56): the skin's seam map and the second-skin shell read a part that
/// resolves to our own smoothed publication from the same upstream, chosen by the same rule — so a body mod's priority
/// change never leaves the seam map on the previous body while the shell is cut from the new one.
/// </summary>
public class BodySourceTests
{
    [Fact]
    [Trait("Plan", "AC-issue-sweep-a-8")]
    [Trait("Plan", "EDGE-issue-sweep-a-20")]
    public void Both_phases_choose_the_same_body_source()
    {
        // Not ours: the resolved file, whatever is remembered.
        Assert.Equal(K.Resolved, SecondSkinService.ChooseBodySource(false, true, true, true));
        // Ours: settled first, then remembered, then kept, then our own file held.
        Assert.Equal(K.Settled, SecondSkinService.ChooseBodySource(true, true, true, true));
        Assert.Equal(K.Remembered, SecondSkinService.ChooseBodySource(true, false, true, true));   // the prime missed
        Assert.Equal(K.Kept, SecondSkinService.ChooseBodySource(true, false, false, true));
        Assert.Equal(K.OwnHeld, SecondSkinService.ChooseBodySource(true, false, false, false));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-a-8")]
    [Trait("Plan", "EDGE-issue-sweep-a-28")]
    public void Settled_that_fails_to_load_falls_through_like_the_shell()
    {
        // "Settled" means it LOADED, for both callers: a settled file that failed to load is chosen as if absent.
        Assert.Equal(K.Remembered, SecondSkinService.ChooseBodySource(true, hasSettled: false, true, false));
        Assert.Equal(K.Kept, SecondSkinService.ChooseBodySource(true, hasSettled: false, false, true));
        Assert.Equal(K.OwnHeld, SecondSkinService.ChooseBodySource(true, hasSettled: false, false, false));
        // The seam map's copy of remembered bytes is its own: the shell's array is never shared.
        var remembered = new byte[] { 1, 2, 3 };
        var copy = SecondSkinService.RememberedLoad(remembered);
        Assert.NotSame(remembered, copy);
        Assert.Equal(remembered, copy);
        copy[0] = 9;
        Assert.Equal(1, remembered[0]);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-a-21")]
    public void Seam_id_follows_the_chosen_source()
    {
        const string gp = "chara/equipment/e0000/model/c0201e0000_top.mdl";
        var dir = Path.Combine(Path.GetTempPath(), "proteus-bodysource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var upstream = Path.Combine(dir, "nirae_top.mdl");
            var ours = Path.Combine(dir, "smoothed_top.mdl");
            File.WriteAllBytes(upstream, [1, 2, 3]);
            File.WriteAllBytes(ours, [4, 5, 6, 7]);
            var a = SecondSkinService.BodySourceId(K.Settled, upstream, null, gp);
            var b = SecondSkinService.BodySourceId(K.OwnHeld, ours, null, gp);
            Assert.StartsWith(upstream + "|3|", a);
            Assert.NotEqual(a, b);
            // Our smoothed file rewritten while the upstream is chosen: the id doesn't move.
            File.WriteAllBytes(ours, [8, 8, 8, 8, 8]);
            Assert.Equal(a, SecondSkinService.BodySourceId(K.Settled, upstream, null, gp));
            // Remembered bytes: by size and hash; a missing file or game data: the game path.
            var mem = SecondSkinService.BodySourceId(K.Remembered, null, [1, 2, 3], gp);
            Assert.StartsWith($"mem|{gp}|3|", mem);
            Assert.NotEqual(mem, SecondSkinService.BodySourceId(K.Remembered, null, [1, 2, 4], gp));
            Assert.Equal(gp, SecondSkinService.BodySourceId(K.Kept, Path.Combine(dir, "gone.mdl"), null, gp));
            Assert.Equal(gp, SecondSkinService.BodySourceId(K.Resolved, null, null, gp));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-a-8")]
    public void Shell_and_seam_map_share_the_rule()
    {
        var shell = Source("Services/SecondSkinService.ShellSetBuild.cs");
        Assert.Contains("ChooseBodySource(bodyIsOurs, settledBytes != null", shell);   // "settled" = it loaded, as the seam map
        Assert.Contains("UpstreamKeptPath(", shell);
        Assert.DoesNotContain("Path.Combine(modelsDir, \"upstream\")", shell);
        var seams = Source("Services/CompositorService.OcclusionPass.cs");
        Assert.Contains("BodySourceFor(", seams);
        Assert.Contains("source.Kind", seams);   // the per-part Debug line names the kind
        Assert.DoesNotContain("penumbra.ResolvePlayer(", seams);
        var rule = Source("Services/SecondSkinService.BodySource.cs");
        Assert.Contains("ChooseBodySource(ours, settledBytes != null", rule);
        Assert.Contains("RememberedLoad(", rule);
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-6")]
    [Trait("Plan", "EDGE-issue-sweep-b-30")]
    public void Hands_mod_echo_keeps_the_remembered_source()
    {
        const string glv = "chara/equipment/e0000/model/c0201e0000_glv.mdl";
        var mods = Path.Combine(Path.GetTempPath(), "proteus-handsmod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(mods, "Hands"));
        try
        {
            var remembered = Path.Combine(mods, "Hands", "glv.mdl");
            File.WriteAllBytes(remembered, [1]);
            bool named = CompositorService.NamesModFile(glv, remembered, null, mods, File.Exists);
            var (memo, settled) = CompositorService.PrimeResult(glv, new System.Collections.Generic.Dictionary<string, string>(),
                                                                settledAsGameData: true, republishedBody: true, named);
            Assert.False(settled);
            Assert.Null(CompositorService.SettledAnswer(glv, settled, memo, File.Exists, _ => false));
            Assert.Equal(K.Remembered, SecondSkinService.ChooseBodySource(true, hasSettled: false, hasRemembered: true, false));
        }
        finally { Directory.Delete(mods, true); }
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-6")]
    [Trait("Plan", "EDGE-issue-sweep-b-19")]
    [Trait("Plan", "EDGE-issue-sweep-b-21")]
    public void Game_data_settle_is_settled_with_the_game_path_id()
    {
        const string gp = "chara/equipment/e0000/model/c0201e0000_glv.mdl";
        Assert.Equal(gp, SecondSkinService.BodySourceId(K.Settled, gp, [1, 2], gp));
        var rule = Source("Services/SecondSkinService.BodySource.cs");
        int check = rule.IndexOf("string.Equals(disk, gamePath, StringComparison.OrdinalIgnoreCase)) return gamePath;", StringComparison.Ordinal);
        Assert.True(check >= 0 && check < rule.IndexOf("new FileInfo(disk)", StringComparison.Ordinal));
        Assert.Equal(K.Remembered, SecondSkinService.ChooseBodySource(true, hasSettled: false, true, true));   // failed to load
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-7")]
    public void Part_log_names_the_source_read()
    {
        const string gp = "chara/equipment/e0000/model/c0201e0000_glv.mdl";
        const string ours = @"E:\mods\Proteus\models\smoothed_glv.mdl";
        Assert.Equal(@"Settled: E:\mods\Hands\glv.mdl", SecondSkinService.SourceLabel(K.Settled, @"E:\mods\Hands\glv.mdl", ours, gp));
        Assert.Equal("Settled: (game data)", SecondSkinService.SourceLabel(K.Settled, gp, ours, gp));
        Assert.Equal("Remembered: (remembered copy)", SecondSkinService.SourceLabel(K.Remembered, null, ours, gp));
        Assert.Equal("Resolved: (game data)", SecondSkinService.SourceLabel(K.Resolved, null, null, gp));
        Assert.Equal($"OwnHeld: {ours} (our own output, held)", SecondSkinService.SourceLabel(K.OwnHeld, null, ours, gp));
        Assert.Contains("SourceLabel(source, sourceDisk, bodyDisk, bodyGamePath)", Source("Services/SecondSkinService.ShellSetBuild.cs"));
    }

    private static string Source(string relative, [CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", relative));
}
