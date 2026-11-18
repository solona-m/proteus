using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using NSubstitute;
using Penumbra.Api.Enums;
using Proteus.Interop.Ipc;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Proteus's own IPC gates (plan D24) against <c>docs/ipc-contract.md</c> §4 and §1: what each gate does when Dalamud
/// refuses it, when the owner is absent, when a handler throws, on a second Dispose, and the argument order each gate
/// hands to Dalamud for the rows whose wrapper order differs from the gate's.
/// </summary>
[Collection("IpcLog")]   // IpcLog is process-wide
public class IpcGatesTests : IDisposable
{
    private const string Plan10 = "AC-contrib-clean-room-10";
    private readonly IPluginLog log = Substitute.For<IPluginLog>();

    public IpcGatesTests() => IpcLog.Attach(log);
    public void Dispose() => IpcLog.Detach();

    /// <summary>A call gate that records every call, optionally throwing on invoke or on subscribe.</summary>
    public class RecordingGate : DispatchProxy
    {
        public bool ThrowOnInvoke, ThrowOnSubscribe;
        public readonly List<object?[]> Invokes = [];
        public readonly List<Delegate> Subscribed = [];
        public int Unsubscribes;

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            switch (m!.Name)
            {
                case "Subscribe":
                    if (ThrowOnSubscribe) throw new InvalidOperationException("subscribe refused");
                    Subscribed.Add((Delegate)args![0]!);
                    return null;
                case "Unsubscribe":
                    Unsubscribes++;
                    return null;
            }
            if (m.Name.StartsWith("Invoke", StringComparison.Ordinal))
            {
                Invokes.Add(args ?? []);
                if (ThrowOnInvoke) throw new IpcNotReadyError("test");
            }
            var ret = m.ReturnType;
            return ret == typeof(void) || !ret.IsValueType ? null : Activator.CreateInstance(ret);
        }
    }

    public class RecordingPi : DispatchProxy
    {
        public bool RefuseGates, ThrowOnInvoke, ThrowOnSubscribe;
        public readonly Dictionary<string, RecordingGate> Gates = [];

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            var ret = m!.ReturnType;
            if (m.Name == "GetIpcSubscriber")
            {
                if (RefuseGates) throw new InvalidOperationException("no gate");
                var g = (RecordingGate)DispatchProxy.Create(ret, typeof(RecordingGate));
                g.ThrowOnInvoke = ThrowOnInvoke;
                g.ThrowOnSubscribe = ThrowOnSubscribe;
                Gates[(string)args![0]!] = g;
                return g;
            }
            return ret == typeof(void) || !ret.IsValueType ? null : Activator.CreateInstance(ret);
        }

        public static (IDalamudPluginInterface Pi, RecordingPi Proxy) Create()
        {
            var pi = DispatchProxy.Create<IDalamudPluginInterface, RecordingPi>();
            return (pi, (RecordingPi)(object)pi);
        }
    }

    private int Errors() => log.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Error");

    /// <summary>Every string argument of every log call, params arrays unpacked.</summary>
    private IEnumerable<string> LoggedStrings() => log.ReceivedCalls().SelectMany(c => c.GetArguments())
        .SelectMany(a => a is object?[] arr ? arr : [a]).OfType<string>();

    // ── §4 availability and errors ──────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", Plan10)]
    public void A1_A4_a_refused_gate_is_logged_and_a_func_then_throws_not_ready()   // [AC-contrib-clean-room-10]
    {
        var (pi, proxy) = RecordingPi.Create();
        proxy.RefuseGates = true;
        var gate = new PenumbraApiVersion(pi);              // A1: no throw from the constructor
        Assert.Equal(1, Errors());                           // A1: logged at Error, with the label
        Assert.Contains(PenumbraApiVersion.Label, LoggedStrings());
        Assert.Throws<IpcNotReadyError>(() => gate.Invoke());   // A4
    }

    [Fact]
    [Trait("Plan", Plan10)]
    public void A5_a_refused_action_gate_does_nothing()   // [AC-contrib-clean-room-10]
    {
        var (pi, proxy) = RecordingPi.Create();
        proxy.RefuseGates = true;
        var gate = new PenumbraRedrawObject(pi);
        Assert.Null(Record.Exception(() => gate.Invoke(0, RedrawType.Redraw)));
    }

    [Fact]
    [Trait("Plan", Plan10)]
    public void A4_an_absent_owner_throws_through_a_func_gate()   // [AC-contrib-clean-room-10]
    {
        var (pi, proxy) = RecordingPi.Create();
        proxy.ThrowOnInvoke = true;
        Assert.Throws<IpcNotReadyError>(() => new PenumbraApiVersion(pi).Invoke());
    }

    [Fact]
    [Trait("Plan", Plan10)]
    public void A2_a_refused_event_gate_subscribes_nothing_and_never_throws()   // [AC-contrib-clean-room-10]
    {
        var (pi, proxy) = RecordingPi.Create();
        proxy.RefuseGates = true;
        PenumbraInitialized? ev = null;
        Assert.Null(Record.Exception(() => ev = new PenumbraInitialized(pi, () => { })));
        Assert.Equal(1, Errors());
        Assert.Null(Record.Exception(ev!.Dispose));
    }

    [Fact]
    [Trait("Plan", Plan10)]
    public void A2_a_failed_subscribe_is_logged_and_never_throws()   // [AC-contrib-clean-room-10]
    {
        var (pi, proxy) = RecordingPi.Create();
        proxy.ThrowOnSubscribe = true;
        Assert.Null(Record.Exception(() => new PenumbraInitialized(pi, () => { })));
        Assert.Equal(1, Errors());
    }

    [Fact]
    [Trait("Plan", Plan10)]
    [Trait("Plan", "EDGE-contrib-clean-room-17")]
    public void A12_A13_handlers_run_inline_and_a_throwing_handler_is_logged_not_raised()   // [AC-contrib-clean-room-10] [EDGE-contrib-clean-room-17]
    {
        var (pi, proxy) = RecordingPi.Create();
        int thread = -1;
        _ = new PenumbraInitialized(pi, () => { thread = Environment.CurrentManagedThreadId; throw new InvalidOperationException("boom"); });
        var relay = (Action)Assert.Single(proxy.Gates[PenumbraInitialized.Label].Subscribed);
        Assert.Null(Record.Exception(relay));                                // A13: never reaches the owner
        Assert.Equal(Environment.CurrentManagedThreadId, thread);             // A12: inline, on the raising thread
        Assert.Equal(1, Errors());
        Assert.Contains(PenumbraInitialized.Label, LoggedStrings());
    }

    [Fact]
    [Trait("Plan", Plan10)]
    public void A15_dispose_unsubscribes_once()   // [AC-contrib-clean-room-10]
    {
        var (pi, proxy) = RecordingPi.Create();
        var ev = new PenumbraDisposed(pi, () => { });
        ev.Dispose();
        ev.Dispose();
        Assert.Equal(1, proxy.Gates[PenumbraDisposed.Label].Unsubscribes);
    }

    [Fact]
    [Trait("Plan", Plan10)]
    public void IpcLog_detached_logs_nothing()   // [AC-contrib-clean-room-10]
    {
        IpcLog.Detach();
        var (pi, proxy) = RecordingPi.Create();
        proxy.RefuseGates = true;
        Assert.Null(Record.Exception(() => new PenumbraApiVersion(pi)));
        Assert.Equal(0, Errors());
    }

    // ── §1 argument order at the gate (the rows whose wrapper order differs) ───────────────────

    private static object?[] OnlyCall(RecordingPi proxy, string label) => Assert.Single(proxy.Gates[label].Invokes);

    [Fact]
    [Trait("Plan", Plan10)]
    public void Gates_pass_the_contracts_argument_order_and_defaults()   // [AC-contrib-clean-room-10]
    {
        var (pi, proxy) = RecordingPi.Create();
        var coll = Guid.NewGuid();

        new PenumbraSetModPath(pi).Invoke("dir", "new/path");                       // P17: (dir, modName, newPath)
        Assert.Equal(new object?[] { "dir", "", "new/path" }, OnlyCall(proxy, PenumbraSetModPath.Label));

        new PenumbraRemoveTemporaryModSettings(pi).Invoke(coll, "dir", 7);         // P8: modName before key
        Assert.Equal(new object?[] { coll, "dir", "", 7 }, OnlyCall(proxy, PenumbraRemoveTemporaryModSettings.Label));

        new PenumbraTrySetModSetting(pi).Invoke(coll, "dir", "Group", "Option");   // P21: modName third
        Assert.Equal(new object?[] { coll, "dir", "", "Group", "Option" }, OnlyCall(proxy, PenumbraTrySetModSetting.Label));

        new PenumbraTrySetMod(pi).Invoke(coll, "dir", true);                        // P18: the bool is the enabled state, last
        Assert.Equal(new object?[] { coll, "dir", "", true }, OnlyCall(proxy, PenumbraTrySetMod.Label));

        new PenumbraOpenMainWindow(pi).Invoke(TabType.Mods, "dir");                // P24: tab as int, then dir, then modName
        Assert.Equal(new object?[] { (int)TabType.Mods, "dir", "" }, OnlyCall(proxy, PenumbraOpenMainWindow.Label));

        new GlamourerSetBonusItem(pi).Invoke(0, ApiBonusSlot.Glasses, 4242UL, 9u, ApplyFlag.Once);   // G5: itemId before key, flags last
        Assert.Equal(new object?[] { 0, (byte)ApiBonusSlot.Glasses, 4242UL, 9u, (ulong)ApplyFlag.Once },
                     OnlyCall(proxy, GlamourerSetBonusItem.Label));
    }
}

[CollectionDefinition("IpcLog", DisableParallelization = true)]
public class IpcLogCollection;
