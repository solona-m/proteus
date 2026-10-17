using Penumbra.Api.Enums;
using Proteus.Services;
using Xunit;
using static Proteus.Services.HairPortScanSchedule;

namespace Proteus.Tests;

/// <summary>When a check of the player's hair mods is owed, and of what: <see cref="HairPortScanSchedule"/> fed signals
/// at chosen times.</summary>
public class HairPortScanScheduleTests
{
    /// <summary>A schedule whose race (c0201) has held since t = 0, so work is due by its own debounce alone.</summary>
    private static HairPortScanSchedule Settled()
    {
        var s = new HairPortScanSchedule();
        s.Walked(201, 0);
        return s;
    }

    private const long T = RaceSettleMs + 1000;

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-1")]
    public void Enabled_edge_owes_full_check_once_race_known()
    {
        var s = new HairPortScanSchedule();
        s.OweFull(0);
        Assert.Null(s.Take(10_000));                       // no race yet
        s.Walked(201, 10_000);
        Assert.Null(s.Take(10_000 + RaceSettleMs - 1));    // not held long enough
        var work = s.Take(10_000 + RaceSettleMs);
        Assert.NotNull(work);
        Assert.Equal((201, true), (work!.Race, work.Full));
        Assert.Null(s.Take(10_000 + RaceSettleMs + 1));    // taken once
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-2")]
    public void Turning_setting_on_owes_full_check()
    {
        var s = Settled();
        s.OweFull(T, forced: true);
        var work = s.Take(T + SettleMs)!;
        Assert.True(work.Full);
        Assert.True(work.Force);
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-3")]
    [Trait("Plan", "EDGE-hair-port-scan-4")]
    public void Mod_added_and_enabled_owe_that_mod_only()
    {
        var s = Settled();
        s.ModAdded("Floof", T);
        s.ModEvent(ModSettingChange.EnableState, "Floof", mine: true, T);
        Assert.Null(s.Take(T + SettleMs - 1));
        var work = s.Take(T + SettleMs)!;
        Assert.False(work.Full);
        Assert.False(work.Force);
        Assert.Equal(["Floof"], work.Mods);
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-4")]
    [Trait("Plan", "EDGE-hair-port-scan-12")]
    public void Race_change_stops_the_check_and_waits_for_the_new_race_to_hold()
    {
        var s = Settled();
        Assert.False(s.Walked(201, T));        // the same race: nothing to stop
        s.ModAdded("Floof", T);
        Assert.True(s.Walked(401, T));         // another race: the check under way stops
        Assert.Null(s.Take(T + SettleMs));     // and what is owed waits for it to hold
        Assert.Equal(401, s.Take(T + RaceSettleMs)!.Race);
        Assert.False(s.Walked(0, T));          // a walk that saw nothing changes nothing
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-13")]
    public void Changes_in_other_collections_owe_nothing()
    {
        var s = Settled();
        s.ModEvent(ModSettingChange.EnableState, "Floof", mine: false, T);
        s.ModEvent(ModSettingChange.MultiEnableState, "", mine: false, T);
        Assert.False(s.Pending);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-11")]
    public void No_race_no_work()
    {
        var s = new HairPortScanSchedule();
        s.ModAdded("Floof", 0);
        Assert.Null(s.Take(1_000_000));
        Assert.True(s.Pending);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-16")]
    public void Burst_debounces_and_over_25_becomes_full()
    {
        var s = Settled();
        for (int i = 0; i <= FullOver; i++) s.ModAdded($"Mod {i}", T + i * 100);
        long last = T + FullOver * 100;
        Assert.Null(s.Take(last + SettleMs - 1));   // every signal reset the wait
        Assert.True(s.Take(last + SettleMs)!.Full);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-17")]
    public void Own_reload_echo_and_managed_mod_ignored()
    {
        var s = Settled();
        s.OwnReload("Floof", T);
        s.ModEvent(ModSettingChange.Edited, "Floof", mine: true, T + 500);
        s.ModEvent(ModSettingChange.Edited, SidecarDiscoveryService.ManagedModDir, mine: true, T + 500);
        s.ModEvent(ModSettingChange.TemporaryMod, "", mine: true, T + 500);
        Assert.False(s.Pending);

        s.ModEvent(ModSettingChange.Edited, "Floof", mine: true, T + OwnEchoMs + 1);
        Assert.Equal(["Floof"], s.Take(T + OwnEchoMs + 1 + SettleMs)!.Mods);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-18")]
    public void Multi_changes_owe_full()
    {
        var s = Settled();
        s.ModEvent(ModSettingChange.MultiEnableState, "", mine: true, T);
        Assert.True(s.Take(T + SettleMs)!.Full);
        s.ModEvent(ModSettingChange.MultiInheritance, "", mine: true, T);
        Assert.True(s.Take(T + SettleMs)!.Full);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-24")]
    public void Disabled_owes_nothing()
    {
        // The watcher feeds nothing while the setting or Proteus is off; a schedule fed nothing owes nothing.
        var s = Settled();
        Assert.False(s.Pending);
        Assert.Null(s.Take(long.MaxValue / 2));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-26")]
    public void Mod_deleted_drops_pending_and_rows()
    {
        var s = Settled();
        s.ModAdded("Floof", T);
        s.ModDeleted("floof");
        Assert.False(s.Pending);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-27")]
    public void Edited_by_user_owes_mod_check()
    {
        var s = Settled();
        foreach (var change in new[] { ModSettingChange.Edited, ModSettingChange.Setting, ModSettingChange.Priority,
                                       ModSettingChange.Inheritance, ModSettingChange.TemporarySetting })
        {
            s.ModEvent(change, "Floof", mine: true, T);
            Assert.Equal(["Floof"], s.Take(T + SettleMs)!.Mods);
        }
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-1")]
    public void A_full_check_owed_takes_in_the_mods_owed()
    {
        var s = Settled();
        s.ModAdded("Floof", T);
        s.OweFull(T);
        s.ModAdded("Other", T);
        var work = s.Take(T + SettleMs)!;
        Assert.True(work.Full);
        Assert.Empty(work.Mods);
        Assert.False(s.Pending);
    }
}
