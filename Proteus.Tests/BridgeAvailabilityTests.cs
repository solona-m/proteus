using System;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Interop;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The bridges with Penumbra or Glamourer absent, from <c>docs/ipc-contract.md</c> §4 (A3, A4, A10, A11, A15). The
/// Dalamud interface is the call-gate proxy from <see cref="ContribSpecTests"/>: every invoke throws, as Dalamud's do
/// when the owning plugin is not loaded, while subscribing works (A3, A14).
/// </summary>
public class BridgeAvailabilityTests
{
    private const string Plan10 = "AC-contrib-clean-room-10";

    [Fact]
    [Trait("Plan", Plan10)]
    [Trait("Plan", "EDGE-contrib-clean-room-17")]
    public void Penumbra_absent_is_unavailable_and_never_throws()   // [AC-contrib-clean-room-10] [EDGE-contrib-clean-room-17]
    {
        var (pi, proxy) = ContribSpecTests.PluginInterfaceProxy.Create(throwing: true);
        PenumbraBridge? bridge = null;
        Assert.Null(Record.Exception(() => bridge = new PenumbraBridge(pi, Substitute.For<IPluginLog>())));   // A3
        Assert.False(bridge!.IsAvailable);                                                                     // A10
        Assert.Null(Record.Exception(() => bridge.OpenToMod("Some Mod")));                                     // A10
        Assert.Null(Record.Exception(bridge.Dispose));                                                         // A15
    }

    [Fact]
    [Trait("Plan", Plan10)]
    [Trait("Plan", "EDGE-contrib-clean-room-17")]
    public void Glamourer_absent_constructs_and_refuses_a_reapply()   // [AC-contrib-clean-room-10] [EDGE-contrib-clean-room-17]
    {
        var (pi, _) = ContribSpecTests.PluginInterfaceProxy.Create(throwing: true);
        GlamourerBridge? bridge = null;
        Assert.Null(Record.Exception(() => bridge = new GlamourerBridge(pi, Substitute.For<IObjectTable>(), Substitute.For<IPluginLog>())));
        Assert.True(bridge!.IsAvailable);                                // A11: subscriptions never throw
        bool ok = true;
        Assert.Null(Record.Exception(() => ok = bridge.ReapplyPlayerState()));   // A4 throws inside; the bridge catches it
        Assert.False(ok);
        Assert.Null(Record.Exception(bridge.Dispose));                   // A15
    }
}
