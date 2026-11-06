using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The harness's design-binding seams, pinned by reading the source: the adapter talks to the game and cannot run headless.
/// Always compiled (reads files only). Textual and deliberately so: change these with the code.
/// </summary>
public class HarnessBindingsWiringTests
{
    private static string Repo([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "..");
    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Repo(), .. path]));

    private static string Member(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {signature}");
        int open = source.IndexOf('{', at), semi = source.IndexOf(';', at);
        if (semi >= 0 && (open < 0 || semi < open)) return source[at..(semi + 1)];   // expression-bodied
        for (int i = open, depth = 0; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[at..(i + 1)];
        }
        Assert.Fail($"unbalanced {signature}");
        return "";
    }

    private static string Adapter() => Read("Proteus", "TestHarness", "GameHarnessAdapter.cs");

    [Fact]
    [Trait("Plan", "AC-binding-reapply-9")]
    public void Apply_goes_through_Glamourers_own_ApplyDesign_ipc()
    {
        var apply = Member(Adapter(), "public string? ApplyDesign(Guid id)");
        Assert.Contains("applyDesign.Invoke(id, 0, 0, ApplyFlagEx.DesignDefault)", apply);
        Assert.Contains("new Glamourer.Api.IpcSubscribers.ApplyDesign(pluginInterface)", Adapter());
        // Never in the shipped bridge.
        var bridge = Read("Proteus", "Interop", "GlamourerBridge.cs");
        Assert.DoesNotContain("IpcSubscribers.ApplyDesign", bridge);
        Assert.DoesNotContain("IpcSubscribers.ApplyState", bridge);
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-9")]
    [Trait("Plan", "EDGE-binding-reapply-25")]
    public void Restore_suppresses_apply_signals_first_and_never_locks()
    {
        var restore = Member(Adapter(), "public string? RestoreGlamourerState(");
        int suppress = restore.IndexOf("HarnessSuppressApplySignals()", StringComparison.Ordinal);
        int apply = restore.IndexOf("applyState.Invoke(", StringComparison.Ordinal);
        Assert.True(suppress >= 0 && apply > suppress, "the echo window opens before the state is applied");
        Assert.Contains("ApplyFlag.Once", restore);
        Assert.DoesNotContain("Lock", restore);
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-11")]
    public void Live_edits_reach_only_the_edit_router()
    {
        foreach (var sig in new[] { "public bool SetLiveRows(", "public bool ClearLiveOption(" })
        {
            var body = Member(Adapter(), sig);
            Assert.Contains("editRouter.", body);
            Assert.DoesNotContain("StageMetadata", body);
            Assert.DoesNotContain("SaveMetadata", body);
            Assert.DoesNotContain("Metadata.", body);
        }
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-13")]
    public void Partial_and_plugin_wiring_are_harness_only()
    {
        var partial = Read("Proteus", "Services", "DesignBindingService.Harness.cs").Replace("\r\n", "\n").Trim();
        Assert.StartsWith("#if PROTEUS_TEST_HARNESS", partial);
        Assert.EndsWith("#endif", partial);

        var plugin = Read("Proteus", "Plugin.cs");
        int call = plugin.IndexOf("new TestHarness.GameHarnessAdapter(", StringComparison.Ordinal);
        int ifAt = plugin.LastIndexOf("#if PROTEUS_TEST_HARNESS", call, StringComparison.Ordinal);
        int endAt = plugin.IndexOf("#endif", call, StringComparison.Ordinal);
        Assert.True(ifAt >= 0 && endAt > call, "the adapter is built inside #if PROTEUS_TEST_HARNESS");
        Assert.Contains("pluginInterface, designBindings, editRouter, presets)", plugin[call..endAt]);
    }
}
