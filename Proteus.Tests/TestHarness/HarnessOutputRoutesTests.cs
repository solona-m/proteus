using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Proteus.Services;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>
/// The shell output routes (issue sweep D) over a real loopback server and a fake game: output-lock (held files and
/// injected move failures), shell-fault and the auto-redraw override, and what put-back does with each.
/// </summary>
[Collection(HarnessTimingCollection.Name)]
public class HarnessOutputRoutesTests : IDisposable
{
    private readonly HarnessFixture fx = new();
    private readonly string output;

    public HarnessOutputRoutesTests()
    {
        output = fx.Temp.Sub("output");
        fx.Game.OutputDir = output;
        Directory.CreateDirectory(Path.Combine(output, "textures"));
        File.WriteAllBytes(Path.Combine(output, "textures", "ss_1_norm.tex"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(output, "textures", "ss_1_mask.tex"), [1, 2]);
    }

    public void Dispose()
    {
        fx.Changes.OutputLocks.ReleaseAll();
        HarnessMoveFault.Disarm();
        fx.Dispose();
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-13")]
    [Trait("Plan", "EDGE-issue-sweep-d-33")]
    public async Task Lock_holds_the_files_until_release()
    {
        // [AC-issue-sweep-d-13] [EDGE-issue-sweep-d-33]
        var (status, body) = await fx.Post("/actions/output-lock", "{\"files\":[\"textures/ss_1_*.tex\"],\"ms\":60000}");
        Assert.Equal(200, status);
        Assert.Equal("lock", body.GetProperty("mode").GetString());
        Assert.Equal(2, body.GetProperty("held").GetArrayLength());
        Assert.True(fx.Changes.OutputLocks.Active);

        var target = Path.Combine(output, "textures", "ss_1_norm.tex");
        if (OperatingSystem.IsWindows())
        {
            var tmp = target + ".x.tmp";
            File.WriteAllBytes(tmp, [9]);
            // The game holding a file answers a replace with access denied or a sharing violation.
            var ex = Record.Exception(() => File.Move(tmp, target, overwrite: true));
            Assert.True(ex is IOException or UnauthorizedAccessException, ex?.GetType().Name);
            File.Delete(tmp);
        }

        var (second, err) = await fx.Post("/actions/output-lock", "{\"files\":[\"textures/ss_1_*.tex\"],\"failMoves\":2}");
        Assert.Equal(409, second);
        Assert.Equal("locks-held", err.GetProperty("error").GetProperty("code").GetString());

        var (rel, released) = await fx.Post("/actions/output-lock", "{\"release\":true}");
        Assert.Equal(200, rel);
        Assert.Equal(2, released.GetProperty("released").GetArrayLength());
        Assert.False(fx.Changes.OutputLocks.Active);
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-13")]
    [Trait("Plan", "EDGE-issue-sweep-d-42")]
    public async Task FailMoves_arms_the_move_fault()
    {
        // [AC-issue-sweep-d-13] [EDGE-issue-sweep-d-42]
        var (status, body) = await fx.Post("/actions/output-lock", "{\"files\":[\"textures/ss_9_*.tex\"],\"failMoves\":2}");
        Assert.Equal(200, status);
        Assert.Equal("fail-moves", body.GetProperty("mode").GetString());
        Assert.Equal(2, HarnessMoveFault.State!.Value.MovesLeft);
        var (_, released) = await fx.Post("/actions/output-lock", "{\"release\":true}");
        Assert.Equal(2, released.GetProperty("movesLeft").GetInt32());
        Assert.Null(HarnessMoveFault.State);
    }

    [Theory]
    [Trait("Plan", "EDGE-issue-sweep-d-33")]
    [InlineData("{\"files\":[\"../x.tex\"],\"ms\":10}")]
    [InlineData("{\"files\":[\"C:/x.tex\"],\"ms\":10}")]
    [InlineData("{\"files\":[\"other/x.tex\"],\"ms\":10}")]
    [InlineData("{\"files\":[\"textures/a/b.tex\"],\"ms\":10}")]
    [InlineData("{\"files\":[\"textures\\\\x.tex\"],\"ms\":10}")]
    [InlineData("{\"files\":[\"textures/x.tex\"],\"ms\":10,\"failMoves\":2}")]
    [InlineData("{\"files\":[\"textures/x.tex\"]}")]
    [InlineData("{\"files\":[\"textures/x.tex\"],\"ms\":200000}")]
    [InlineData("{\"files\":[\"textures/x.tex\"],\"failMoves\":21}")]
    [InlineData("{\"files\":[]}")]
    [InlineData("{\"release\":true,\"ms\":5}")]
    public async Task Bad_lock_requests_are_400(string json)
    {
        // [EDGE-issue-sweep-d-33]
        var (status, body) = await fx.Post("/actions/output-lock", json);
        Assert.Equal(400, status);
        Assert.Equal("invalid-field", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-33")]
    public async Task Lock_with_no_match_is_404()
    {
        // [EDGE-issue-sweep-d-33]
        var (status, body) = await fx.Post("/actions/output-lock", "{\"files\":[\"models/nothing*.mdl\"],\"ms\":100}");
        Assert.Equal(404, status);
        Assert.Equal("no-match", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-33")]
    public async Task Lock_expires_by_itself()
    {
        // [EDGE-issue-sweep-d-33]
        var (status, _) = await fx.Post("/actions/output-lock", "{\"files\":[\"textures/ss_1_norm.tex\"],\"ms\":50}");
        Assert.Equal(200, status);
        for (int i = 0; i < 40 && fx.Changes.OutputLocks.Active; i++) await Task.Delay(25);
        Assert.False(fx.Changes.OutputLocks.Active);
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-13")]
    [Trait("Plan", "AC-issue-sweep-d-14")]
    [Trait("Plan", "AC-issue-sweep-d-15")]
    [Trait("Plan", "EDGE-issue-sweep-d-33")]
    [Trait("Plan", "EDGE-issue-sweep-d-34")]
    [Trait("Plan", "EDGE-issue-sweep-d-35")]
    public async Task Put_back_releases_locks_clears_the_fault_and_the_override()
    {
        // [AC-issue-sweep-d-13] [AC-issue-sweep-d-14] [AC-issue-sweep-d-15] [EDGE-issue-sweep-d-33] [EDGE-issue-sweep-d-34] [EDGE-issue-sweep-d-35]
        await fx.Post("/actions/output-lock", "{\"files\":[\"textures/ss_1_norm.tex\"],\"ms\":60000}");
        var (armed, _) = await fx.Post("/actions/shell-fault", "{\"shell\":\"any\",\"until\":\"put-back\"}");
        Assert.Equal(200, armed);
        await fx.Post("/actions/auto-redraw", "{\"enabled\":false}");
        Assert.False(fx.Game.AutoRedrawEnabled().Effective);

        var (_, report) = await fx.Post("/actions/put-back", "{}");
        var o = report.GetProperty("output");
        Assert.Equal("released", o.GetProperty("locks")[0].GetProperty("result").GetString());
        Assert.Equal("cleared", o.GetProperty("fault").GetProperty("result").GetString());
        Assert.Equal("cleared", o.GetProperty("autoRedraw").GetProperty("result").GetString());
        Assert.Equal("none", o.GetProperty("moveFault").GetProperty("result").GetString());
        Assert.False(fx.Changes.OutputLocks.Active);
        Assert.Null(fx.Game.ShellFaultValue);
        Assert.Null(fx.Game.AutoRedrawOverrideValue);
        Assert.Contains("test harness: shell fault cleared", fx.Game.Recomposites);
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-14")]
    [Trait("Plan", "EDGE-issue-sweep-d-34")]
    public async Task Shell_fault_arm_clear_putback()
    {
        // [AC-issue-sweep-d-14] [EDGE-issue-sweep-d-34]
        var (status, body) = await fx.Post("/actions/shell-fault", "{\"shell\":\"ss_1\",\"slot\":\"mask\",\"until\":\"auto-redraw\"}");
        Assert.Equal(200, status);
        Assert.Equal("mask", body.GetProperty("slot").GetString());
        var (again, err) = await fx.Post("/actions/shell-fault", "{\"shell\":\"any\",\"until\":\"put-back\"}");
        Assert.Equal(409, again);
        Assert.Equal("fault-armed", err.GetProperty("error").GetProperty("code").GetString());

        var (cleared, c) = await fx.Post("/actions/shell-fault", "{\"clear\":true}");
        Assert.Equal(200, cleared);
        Assert.True(c.GetProperty("recomposite").GetBoolean());
        Assert.Null(fx.Game.ShellFaultValue);

        foreach (var bad in new[] { "{\"shell\":\"x\",\"until\":\"put-back\"}", "{\"shell\":\"any\",\"until\":\"never\"}",
                                    "{\"shell\":\"any\",\"slot\":\"diffuse\",\"until\":\"put-back\"}", "{\"clear\":true,\"shell\":\"any\"}" })
            Assert.Equal(400, (await fx.Post("/actions/shell-fault", bad)).Item1);
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-15")]
    [Trait("Plan", "EDGE-issue-sweep-d-35")]
    public async Task Auto_redraw_override_is_session_only()
    {
        // [AC-issue-sweep-d-15] [EDGE-issue-sweep-d-35] Memory only: the fake's config never changes.
        fx.Game.AutoRedrawConfig = true;
        var (status, body) = await fx.Post("/actions/auto-redraw", "{\"enabled\":false}");
        Assert.Equal(200, status);
        Assert.True(body.GetProperty("config").GetBoolean());
        Assert.False(body.GetProperty("effective").GetBoolean());
        Assert.True(body.GetProperty("before").GetBoolean());
        Assert.True(fx.Game.AutoRedrawConfig);
        Assert.Equal(400, (await fx.Post("/actions/auto-redraw", "{}")).Item1);
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-14")]
    public async Task State_carries_the_recovery_block()
    {
        // [AC-issue-sweep-d-14] /state.compositor.recovery is the game's RecoveryState (the shell fault included).
        await fx.Post("/actions/shell-fault", "{\"shell\":\"any\",\"until\":\"put-back\"}");
        var (_, state) = await fx.Get("/state");
        var recovery = state.GetProperty("compositor").GetProperty("recovery");
        Assert.Equal("any", recovery.GetProperty("shellFault").GetProperty("shell").GetString());
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-34")]
    public void Withhold_filter()
    {
        // [EDGE-issue-sweep-d-34] Exact keys, what was removed is returned, "any" takes the lowest letter.
        var redirects = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["chara/accessory/a0053/texture/ss_2_norm.tex"] = "textures\\ss_2_norm.tex",
            ["chara/accessory/a0053/texture/ss_1_norm.tex"] = "textures\\ss_1_norm.tex",
            ["chara/accessory/a0053/texture/ss_1_mask.tex"] = "textures\\ss_1_mask.tex",
            ["chara/accessory/a0053/texture/ss_a_norm.tex"] = "textures\\ss_a_norm.tex",
            ["chara/accessory/a0053/material/v0001/mt_c0101a0053_rir_b.mtrl"] = "materials\\ss_1.mtrl",
        };
        var removed = CompositorService.WithholdShellTexture(redirects, "any", "norm");
        var (gp, rel) = Assert.Single(removed);
        Assert.Equal("chara/accessory/a0053/texture/ss_1_norm.tex", gp);
        Assert.Equal("textures\\ss_1_norm.tex", rel);
        Assert.False(redirects.ContainsKey(gp));
        Assert.True(redirects.ContainsKey("chara/accessory/a0053/texture/ss_1_mask.tex"));

        Assert.Single(CompositorService.WithholdShellTexture(redirects, "ss_a", "norm"));
        Assert.Empty(CompositorService.WithholdShellTexture(redirects, "ss_7", "norm"));
    }
}
