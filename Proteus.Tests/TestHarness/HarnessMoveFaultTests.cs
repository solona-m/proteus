using System;
using System.IO;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>The harness-only injected move failures that make #91's write retry and latch deterministic in game.
/// Serialised with the other harness tests: the fault is process-wide.</summary>
[Collection(HarnessTimingCollection.Name)]
public class HarnessMoveFaultTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "proteus-move-fault", Guid.NewGuid().ToString("N"), "textures");

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
        HarnessMoveFault.Arm(["textures/zz_mf_*.tex"], 2);
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
        HarnessMoveFault.Arm(["zz_mf_*.tex"], 6);
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
        HarnessMoveFault.Arm(["textures/zz_mf_*.tex"], 3);
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
    public void WriteIfChanged_honours_the_fault()
    {
        // [EDGE-issue-sweep-d-42] The .mtrl / .mdl writer moves through the same fault point.
        HarnessMoveFault.Arm(["zz_mf_*.mtrl"], 1);
        var path = Path.Combine(dir, "zz_mf_1.mtrl");
        Assert.True(SecondSkinService.WriteIfChanged(path, [1, 2, 3]));
        Assert.Null(HarnessMoveFault.State);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }
}
