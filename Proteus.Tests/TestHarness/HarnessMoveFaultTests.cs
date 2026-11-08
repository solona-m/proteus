using System;
using System.IO;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>The harness-only injected move failures that make #91's write retry and latch deterministic in game.
/// Serialised with the other harness tests: the fault is process-wide.</summary>
[Collection(HarnessTimingCollection.Name)]
public class HarnessMoveFaultTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "proteus-move-fault", Guid.NewGuid().ToString("N"), "textures");

    private string Root => Path.GetDirectoryName(dir)!;

    public HarnessMoveFaultTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        HarnessMoveFault.Disarm();
        try { Directory.Delete(Path.GetDirectoryName(dir)!, true); } catch { }
    }

    private string Tmp(string path)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, [5, 5]);
        return tmp;
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-4")]
    [Trait("Plan", "EDGE-issue-sweep-d-42")]
    public void Two_injected_failures_are_two_retries_then_a_write()
    {
        // [AC-issue-sweep-d-4] [EDGE-issue-sweep-d-42]
        HarnessMoveFault.Arm(Root, ["textures/zz_mf_*.tex"], 2);
        var path = Path.Combine(dir, "zz_mf_1_norm.tex");
        Assert.Equal(2, TextureLoader.MoveIntoPlace(Tmp(path), path, 5, 1));
        Assert.Equal(new byte[] { 5, 5 }, File.ReadAllBytes(path));
        Assert.Null(HarnessMoveFault.State);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-42")]
    public void Six_injected_failures_outlast_the_retries()
    {
        // [EDGE-issue-sweep-d-42]
        HarnessMoveFault.Arm(Root, ["zz_mf_*.tex"], 6);
        var path = Path.Combine(dir, "zz_mf_2_norm.tex");
        Assert.Throws<OutputWriteException>(() => TextureLoader.MoveIntoPlace(Tmp(path), path, 5, 1));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.Null(HarnessMoveFault.State);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-42")]
    public void A_non_matching_file_is_untouched_and_disarm_clears()
    {
        // [EDGE-issue-sweep-d-42]
        HarnessMoveFault.Arm(Root, ["textures/zz_mf_*.tex"], 3);
        var other = Path.Combine(dir, "zz_other.tex");
        Assert.Equal(0, TextureLoader.MoveIntoPlace(Tmp(other), other, 5, 1));
        Assert.Equal(3, HarnessMoveFault.State!.Value.MovesLeft);
        HarnessMoveFault.Disarm();
        Assert.Null(HarnessMoveFault.State);
        var path = Path.Combine(dir, "zz_mf_3_norm.tex");
        Assert.Equal(0, TextureLoader.MoveIntoPlace(Tmp(path), path, 5, 1));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-42")]
    public void A_file_outside_the_output_root_is_never_failed()
    {
        // [EDGE-issue-sweep-d-42] Review ND2: only Proteus's own output folder, never a user's mod that has a textures/ too.
        var elsewhere = Path.Combine(Path.GetTempPath(), "proteus-move-fault", Guid.NewGuid().ToString("N"), "textures");
        Directory.CreateDirectory(elsewhere);
        try
        {
            HarnessMoveFault.Arm(Root, ["textures/zz_mf_*.tex"], 2);
            var path = Path.Combine(elsewhere, "zz_mf_9_norm.tex");
            Assert.Equal(0, TextureLoader.MoveIntoPlace(Tmp(path), path, 5, 1));
            Assert.Equal(2, HarnessMoveFault.State!.Value.MovesLeft);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(elsewhere)!, true); } catch { } }
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-4")]
    public void TryWriteTex_tells_a_held_file_from_other_failures_and_logs_the_retries()
    {
        // [AC-issue-sweep-d-4] Two injected failures: written, with the retry line. Six: OutputWriteFailed. A path that cannot
        // exist: Failed (never retried).
        var log = NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var loader = new TextureLoader(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IDataManager>(), log);
        var px = new byte[16 * 16 * 4];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)(i * 7);
        var path = Path.Combine(dir, "zz_mf_t_norm.tex");

        HarnessMoveFault.Arm(Root, ["textures/zz_mf_t_*.tex"], 2);
        Assert.Equal(WriteOutcome.Written, loader.TryWriteTex(px, 16, 16, path, TexEncoding.Uncompressed));
        log.Received().Information("[Proteus] {0} was locked; written after {1} retr(ies)", path, 2);

        HarnessMoveFault.Arm(Root, ["textures/zz_mf_t_*.tex"], 6);
        Assert.Equal(WriteOutcome.OutputWriteFailed, loader.TryWriteTex(px, 16, 16, path, TexEncoding.Uncompressed));
        HarnessMoveFault.Disarm();

        var bad = dir + Path.DirectorySeparatorChar + "bad" + (char)0 + "name.tex";   // a NUL: invalid on every OS
        Assert.Equal(WriteOutcome.Failed, loader.TryWriteTex(px, 16, 16, bad, TexEncoding.Uncompressed));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-42")]
    public void WriteIfChanged_honours_the_fault()
    {
        // [EDGE-issue-sweep-d-42] The .mtrl / .mdl writer moves through the same fault point.
        HarnessMoveFault.Arm(Root, ["zz_mf_*.mtrl"], 1);
        var path = Path.Combine(dir, "zz_mf_1.mtrl");
        Assert.True(SecondSkinService.WriteIfChanged(path, [1, 2, 3]));
        Assert.Null(HarnessMoveFault.State);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }
}
