using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Interop;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>How a rename is sequenced and dispatched: <see cref="ModMoveService"/>'s and the bridge's seams.</summary>
public class ModMoveServiceTests
{
    private static Dictionary<string, Action> Steps(List<string> ran, string? throws = null)
        => ModMoveService.Order.ToDictionary(n => n, n => (Action)(() =>
        {
            ran.Add(n);
            if (n == throws) throw new InvalidOperationException(n);
        }));

    [Fact]
    [Trait("Plan", "AC-mod-rename-1")]
    [Trait("Plan", "EDGE-mod-rename-27")]
    public void BuildSteps_follows_order()
    {
        var ran = new List<string>();
        ModMoveService.RunSteps(ModMoveService.BuildSteps(ModMoveService.Order, Steps(ran)), (_, _) => { });

        Assert.Equal(["config", "presets", "bindings", "compositor", "autoRefit", "hatCompat", "hairPort", "ui"], ran);
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-1")]
    public void BuildSteps_throws_on_missing_or_extra_step()
    {
        var map = Steps([]);
        map.Remove("presets");
        Assert.Throws<ArgumentException>(() => ModMoveService.BuildSteps(ModMoveService.Order, map));

        map = Steps([]);
        map["extra"] = () => { };
        Assert.Throws<ArgumentException>(() => ModMoveService.BuildSteps(ModMoveService.Order, map));
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-4")]
    [Trait("Plan", "AC-mod-rename-5")]
    public void RunSteps_runs_in_order_and_continues_after_a_failing_step()
    {
        var ran = new List<string>();
        var failed = new List<string>();
        ModMoveService.RunSteps(ModMoveService.BuildSteps(ModMoveService.Order, Steps(ran, throws: "presets")),
                                (step, _) => failed.Add(step));

        Assert.Equal(["presets"], failed);
        Assert.Equal(ModMoveService.Order, ran);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-1")]
    public void Dispatch_never_runs_inline()
    {
        int runs = 0;
        Action? posted = null;
        PenumbraBridge.Dispatch(() => runs++, a => posted = a);

        Assert.Equal(0, runs);
        posted!();
        Assert.Equal(1, runs);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-31")]
    public void GetAllModsQuiet_logs_failures_at_debug()
    {
        var quiet = Substitute.For<IPluginLog>();
        PenumbraBridge.LogModListFailure(quiet, new Exception("x"), quiet: true);
        quiet.ReceivedWithAnyArgs().Debug(default(string)!, default!);
        quiet.DidNotReceiveWithAnyArgs().Error(default(Exception)!, default(string)!, default!);

        var loud = Substitute.For<IPluginLog>();
        PenumbraBridge.LogModListFailure(loud, new Exception("x"), quiet: false);
        loud.ReceivedWithAnyArgs().Error(default(Exception)!, default(string)!, default!);
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-9")]
    [Trait("Plan", "EDGE-mod-rename-18")]
    public void Managed_copy_is_switched_off_only_after_a_successful_newer_composite()
    {
        Assert.True(ModMoveService.SwitchOffManagedCopy(new CompositorResult { Success = true, Epoch = 5 }, 4, managedEnabled: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-29")]
    public void Managed_copy_stays_on_after_failed_or_stale_composite()
    {
        Assert.False(ModMoveService.SwitchOffManagedCopy(null, 4, true));
        Assert.False(ModMoveService.SwitchOffManagedCopy(new CompositorResult { Success = false, Epoch = 5 }, 4, true));
        Assert.False(ModMoveService.SwitchOffManagedCopy(new CompositorResult { Success = true, Epoch = 4 }, 4, true));
        Assert.False(ModMoveService.SwitchOffManagedCopy(new CompositorResult { Success = true, Epoch = 5 }, 4, false));
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-9")]
    public void CollectionsToSwitchOff_returns_every_enabled_collection()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var off = ModMoveService.CollectionsToSwitchOff(new Dictionary<Guid, bool?> { [a] = true, [b] = false, [c] = null });
        Assert.Equal([a], off);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-28")]
    public void Managed_rename_while_disabled_takes_no_action()
    {
        Assert.Equal(ModMoveService.ManagedAction.WarnOnly, ModMoveService.PlanManaged(false, true));
        Assert.Equal(ModMoveService.ManagedAction.WarnOnly, ModMoveService.PlanManaged(true, false));
        Assert.Equal(ModMoveService.ManagedAction.RebuildThenSwitchOff, ModMoveService.PlanManaged(true, true));
    }
}
