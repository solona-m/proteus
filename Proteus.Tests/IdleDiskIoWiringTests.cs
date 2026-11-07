using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The idle-disk-io wiring that lives in classes a test cannot build (Plugin, CompositorService, the watchers), pinned
/// by reading the source like <see cref="AutoRedrawWiringTests"/>. Textual on purpose: change these with the code.
/// </summary>
public class IdleDiskIoWiringTests
{
    private static string ProteusDir([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus");

    private static string Source(string relative) => File.ReadAllText(Path.Combine(ProteusDir(), relative));

    /// <summary>The body of the first method whose signature contains <paramref name="signature"/>, braces matched.</summary>
    private static string Method(string source, string signature)
    {
        int at = source.IndexOf(signature, System.StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {signature}");
        int open = source.IndexOf('{', at), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail($"unbalanced {signature}");
        return "";
    }

    private static int Count(string body, string needle) => Regex.Matches(body, Regex.Escape(needle)).Count;

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-7")]
    [Trait("Plan", "EDGE-idle-disk-io-29")]
    public void RedrawHook_ReadsResourceTreeOnce()
    {
        // [AC-idle-disk-io-7] [EDGE-idle-disk-io-29]
        var hook = Method(Source("Services/CompositorService.Events.cs"), "private void OnLocalPlayerRedrawn()");
        Assert.Equal(1, Count(hook, "GetActivePlayerResourcePaths("));
        Assert.DoesNotContain("GetActivePlayerMaterialPaths(", hook);
        Assert.DoesNotContain("GetActivePlayerModelPaths(", hook);
        // The guards that keep a mid-teardown walk from wiping the snapshot stay.
        Assert.Contains("if (snapshot is { Count: > 0 })", hook);
        Assert.Contains("if (modelPaths is { Count: > 0 }) modelWalkOk = true;", hook);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-4")]
    [Trait("Plan", "EDGE-idle-disk-io-17")]
    public void Discovery_AttachedBeforeCompositorAndModMoves()
    {
        // [AC-idle-disk-io-4] [EDGE-idle-disk-io-17]
        var plugin = Source("Plugin.cs");
        int attach = plugin.IndexOf("discovery.AttachTo(penumbra);", System.StringComparison.Ordinal);
        int bridge = plugin.IndexOf("penumbra = new PenumbraBridge(", System.StringComparison.Ordinal);
        Assert.True(attach > bridge && bridge > 0, "discovery attaches after the bridge exists");
        foreach (var later in new[] { "new CompositorService(", "new ModMoveService(", "new ShaderUpgradeWatcher(",
                                      "new VariantRepointWatcher(", "new AutoRefitWatcher(", "new HairPortWatcher(",
                                      "new HatCompatWatcher(", "new DesignBindingService(", "PlayerSettingsFilter.Attach(" })
        {
            int at = plugin.IndexOf(later, System.StringComparison.Ordinal);
            Assert.True(at > attach, $"{later} subscribes after discovery");
        }

        var attachTo = Method(Source("Services/SidecarDiscoveryService.cs"), "public void AttachTo(PenumbraBridge bridge)");
        foreach (var ev in new[] { "ModAdded", "ModDeleted", "ModMoved", "ModSettingChanged", "PenumbraReady" })
            Assert.Contains($"bridge.{ev}", attachTo);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-4")]
    public void ManualRefresh_ForgetsAll()
    {
        // [AC-idle-disk-io-4]
        var refresh = Method(Source("Services/CompositorService.Trigger.cs"), "public void RefreshAndRecomposite(");
        Assert.Contains("discovery.ForgetAll()", refresh);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-4")]
    public void OwnWrites_ForgetPath()
    {
        // [AC-idle-disk-io-4]
        var src = Source("Services/SidecarDiscoveryService.cs");
        foreach (var sig in new[] { "public bool SaveMetadata(OverlayEntry entry)", "private void WriteStaged(" })
        {
            var body = Method(src, sig);
            int write = body.IndexOf("PenumbraModMeta.AtomicWrite(", System.StringComparison.Ordinal);
            int forget = body.IndexOf("Cache.ForgetPath(path)", System.StringComparison.Ordinal);
            Assert.True(write >= 0 && forget > write, $"{sig} forgets the path after writing it");
        }
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-5")]
    public void Watchers_SubscribeThroughFilter()
    {
        // [AC-idle-disk-io-5]
        foreach (var file in new[] { "Services/VariantRepoint/VariantRepointWatcher.cs",
                                     "Services/ShaderUpgrade/ShaderUpgradeWatcher.cs" })
        {
            var src = Source(file);
            Assert.DoesNotContain("penumbra.ModSettingChanged +=", src);
            Assert.Contains("settings.Passed += OnModSettingChanged;", src);
            Assert.Contains("settings.Passed -= OnModSettingChanged;", src);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-31")]
    public void AttachHandlers_WrapTryCatch()
    {
        // [EDGE-idle-disk-io-31]
        var src = Source("Services/SidecarDiscoveryService.cs");
        // Every handler funnels through ForgetQuietly or guards itself.
        foreach (var sig in new[] { "internal void OnPenumbraModAdded(", "internal void OnPenumbraModDeleted(",
                                    "internal void OnPenumbraModMoved(", "internal void OnPenumbraModSettingChanged(" })
        {
            int at = src.IndexOf(sig, System.StringComparison.Ordinal);
            Assert.True(at >= 0, sig);
            var tail = src[at..src.IndexOf('\n', src.IndexOf("ForgetQuietly", at, System.StringComparison.Ordinal))];
            Assert.Contains("ForgetQuietly(", tail);
        }
        var quiet = Method(src, "private void ForgetQuietly(");
        Assert.Contains("string.IsNullOrEmpty(modDir)", quiet);
        Assert.Contains("try", quiet);
        Assert.Contains("catch", quiet);
        var ready = Method(src, "internal void OnPenumbraReady()");
        Assert.Contains("try", ready);
        Assert.Contains("catch", ready);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-1")]
    public void BootPoll_BacksOffThroughTheGate()
    {
        // [AC-idle-disk-io-1]
        var poll = Method(Source("Services/CompositorService.cs"), "private void OnBootPoll(IFramework fw)");
        Assert.Contains("_boot.Rearm(", poll);
        Assert.Contains("_boot.Due(now)", poll);
        Assert.Contains("DiscoverEnabled(out bool complete)", poll);
        Assert.Contains("_boot.Report(outcome", poll);
        // The latch still comes after the trigger.
        Assert.True(poll.IndexOf("TriggerRecomposite(\"boot-ready\"", System.StringComparison.Ordinal)
                  < poll.IndexOf("_boot.Latch()", System.StringComparison.Ordinal));
        var events = Source("Services/CompositorService.Events.cs");
        Assert.Contains("_boot.ResetBackoff(", Method(events, "private void OnPenumbraReady()"));
        Assert.Contains("_boot.ResetBackoff(", Method(events, "private void OnPlayerCollectionChanged()"));
    }
}
