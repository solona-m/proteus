using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Interop;
using Proteus.Services;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

[Collection(HarnessTimingCollection.Name)]
public class HarnessChangesTests : IDisposable
{
    private readonly HarnessFixture fx = new();
    private FakeGame Game => fx.Game;
    private HarnessChanges C => fx.Changes;
    private Guid Coll => Game.Collection!.Value;

    public void Dispose() => fx.Dispose();

    private ChangeResult Equip(string slot, ulong id, (int, int)? requested = null) => fx.Dispatcher.Run(() => C.Equip(slot, id, requested));
    private ChangeResult Hold(string dir, bool? enabled, int? priority = null) => fx.Dispatcher.Run(() => C.Hold(Coll, dir, enabled, priority));
    private PutBackReport PutBack() => fx.Dispatcher.Run(C.PutBack);
    private GearRecord Record(string slot) => fx.Dispatcher.Run(() => RecordOf(slot));
    private GearRecord RecordOf(string slot)
    {
        var field = typeof(HarnessChanges).GetField("gear", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return ((Dictionary<string, GearRecord>)field.GetValue(C)!)[slot];
    }

    private static PenumbraBridge.SettingsRead Ok(bool enabled, int priority, Dictionary<string, List<string>>? options = null, bool temporary = false)
        => new(PenumbraBridge.SettingsReadKind.Ok,
               new PenumbraBridge.ModSettingsSnapshot(enabled, priority, options ?? new(), false, temporary));

    // [EDGE-harness-equip-10]
    [Fact]
    public void EquipTwice_PutBackBringsFirstOriginal()
    {
        Game.Gear["Body"] = new Worn(111, 3, 0);
        Assert.Equal(200, Equip("Body", 500).Status);
        Assert.Equal(200, Equip("Body", 600).Status);
        Assert.Equal(new Worn(111, 3, 0), Record("Body").Original);
        var r = PutBack();
        Assert.Equal("restored", Assert.Single(r.Gear).Result);
        Assert.Equal(new Worn(111, 3, 0), Game.Gear["Body"]);
    }

    // [AC-harness-equip-6] [EDGE-harness-equip-11]
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SlotChangedSince_Changed_NotWritten(bool itemChanged)
    {
        Equip("Body", 500);
        Game.Gear["Body"] = itemChanged ? new Worn(777, 0, 0) : new Worn(500, 4, 0);
        int sets = Game.GearSets.Count;
        var r = PutBack();
        Assert.Equal("changed", Assert.Single(r.Gear).Result);
        Assert.Equal(sets, Game.GearSets.Count);
        Assert.Empty(PutBack().Gear);
    }

    // [EDGE-harness-equip-13]
    [Fact]
    public void PutBack_GlamourerDown_FailedKept_RetrySucceeds()
    {
        Game.Gear["Legs"] = new Worn(42, 1, 2);
        Equip("Legs", 500);
        Game.GlamourerUp = false;
        var first = PutBack();
        Assert.Equal(new UndoEntry("Legs", "failed", "glamourer-unavailable"), Assert.Single(first.Gear));
        Game.GlamourerUp = true;
        Assert.Equal("restored", Assert.Single(PutBack().Gear).Result);
        Assert.Equal(new Worn(42, 1, 2), Game.Gear["Legs"]);
    }

    // [EDGE-harness-equip-16]
    [Fact]
    public void NothingId_RestoredVerbatim()
    {
        Game.Gear["Head"] = new Worn(4294967164, 0, 0);
        Equip("Head", 500);
        PutBack();
        Assert.Equal(("Head", 4294967164UL, (byte)0, (byte)0), Game.GearSets.Last());
    }

    // [EDGE-harness-equip-17]
    [Fact]
    public void Dyes_EquipZero_PutBackExact()
    {
        Game.Gear["Feet"] = new Worn(77, 3, 7);
        Equip("Feet", 500);
        Assert.Equal(("Feet", 500UL, (byte)0, (byte)0), Game.GearSets[0]);
        PutBack();
        Assert.Equal(("Feet", 77UL, (byte)3, (byte)7), Game.GearSets[1]);
    }

    // [EDGE-harness-equip-40]
    [Fact]
    public void Applied_IsReadBack_NormalisedIdNotChanged()
    {
        Game.Normalise = (_, _) => 40000;
        var id = HarnessGear.CustomId("Body", 317, 2);
        Equip("Body", id, (317, 2));
        Assert.Equal(40000UL, Record("Body").Applied.Item);
        Assert.True(Record("Body").Verified);
        Assert.Equal("restored", Assert.Single(PutBack().Gear).Result);

        Game.Normalise = (_, i) => i;
        Game.GearReadsBeforeFailure = 1;   // the gate read works, the read-back fails
        var r = Equip("Hands", 900);
        Game.GearReadsBeforeFailure = -1;
        var body = (Dictionary<string, object?>)r.Body!;
        Assert.Equal(false, body["readBack"]);
        Assert.False(Record("Hands").Verified);
        Assert.Equal(new Worn(900, 0, 0), Record("Hands").Applied);
    }

    // [EDGE-harness-equip-48]
    [Fact]
    public void Unverified_PutBackRestores_NotChanged()
    {
        Game.Gear["Body"] = new Worn(111, 3, 0);
        Game.GearReadsBeforeFailure = 1;
        Equip("Body", 500);
        Game.GearReadsBeforeFailure = -1;
        Assert.Equal(new Worn(500, 0, 0), Record("Body").Applied);
        Game.Gear["Body"] = new Worn(888, 5, 6);
        Assert.Equal("restored", Assert.Single(PutBack().Gear).Result);
        Assert.Equal(new Worn(111, 3, 0), Game.Gear["Body"]);
    }

    // [EDGE-harness-equip-48] approval guard: an unverified slot now holding a carrier is never overwritten.
    [Fact]
    public void Unverified_CarrierSinceEquip_Changed()
    {
        Game.GearReadsBeforeFailure = 1;
        Equip("RFinger", 500);
        Game.GearReadsBeforeFailure = -1;
        Game.Carriers.Add("RFinger");
        Game.Gear["RFinger"] = new Worn(9999, 0, 0);
        int sets = Game.GearSets.Count;
        Assert.Equal("changed", Assert.Single(PutBack().Gear).Result);
        Assert.Equal(sets, Game.GearSets.Count);
    }

    // [EDGE-harness-equip-49]
    [Fact]
    public void WornDecodesToRequested_Restored_OtherModel_Changed()
    {
        Game.Normalise = (_, _) => 40000;
        Equip("Body", HarnessGear.CustomId("Body", 317, 2), (317, 2));
        Game.Gear["Body"] = new Worn(0x102020000013D, 0, 0);
        Assert.Equal("restored", Assert.Single(PutBack().Gear).Result);

        Equip("Body", HarnessGear.CustomId("Body", 317, 2), (317, 2));
        Game.Gear["Body"] = new Worn(HarnessGear.CustomId("Body", 317, 3), 0, 0);
        int sets = Game.GearSets.Count;
        Assert.Equal("changed", Assert.Single(PutBack().Gear).Result);
        Assert.Equal(sets, Game.GearSets.Count);
    }

    // [EDGE-harness-equip-49] approval guard: the model matches but a dye changed: still changed.
    [Fact]
    public void WornDecodesToRequested_DyeChanged_Changed()
    {
        Game.Normalise = (_, _) => 40000;
        Equip("Body", HarnessGear.CustomId("Body", 317, 2), (317, 2));
        Game.Gear["Body"] = new Worn(0x102020000013D, 9, 0);
        Assert.Equal("changed", Assert.Single(PutBack().Gear).Result);
    }

    // [AC-harness-equip-9]
    [Fact]
    public void Equip_RecordKeepsRequested()
    {
        Equip("Body", HarnessGear.CustomId("Body", 317, 2), (317, 2));
        Equip("Legs", 12345);
        Assert.Equal((317, 2), Record("Body").Requested);
        Assert.Null(Record("Legs").Requested);
    }

    // [AC-harness-equip-4] [EDGE-harness-equip-20]
    [Fact]
    public void Hold_NoSettings_Base_PutBackClears()
    {
        var r = Hold("Equinox", null, 1000);
        Assert.Equal(200, r.Status);
        var set = Assert.Single(Game.HoldSets);
        Assert.False(set.Enabled);
        Assert.Equal(1000, set.Priority);
        Assert.Empty(set.Options);
        var put = PutBack();
        Assert.Equal("cleared", Assert.Single(put.Holds).Result);
        Assert.Empty(Game.Temporary);
        Assert.True(put.RedrawRequested);
    }

    // [AC-harness-equip-4]
    [Fact]
    public void Hold_KeepsOptions()
    {
        var options = new Dictionary<string, List<string>> { ["Colour"] = ["Red"], ["Parts"] = ["Top", "Skirt"] };
        Game.Settings["Equinox"] = Ok(true, 530, options);
        Hold("Equinox", false);
        var set = Assert.Single(Game.HoldSets);
        Assert.False(set.Enabled);
        Assert.Equal(530, set.Priority);
        Assert.Equal(options, set.Options);
        Assert.Equal(HarnessChanges.HoldSource, set.Source);
        Assert.Equal(HarnessChanges.HoldKey, set.Key);
        Assert.Equal(1, Game.RedrawRequests);
    }

    // [AC-harness-equip-5] [EDGE-harness-equip-18]
    [Fact]
    public void HeldByDesignBinding_Refused_SweepUsesOnlyHoldKey()
    {
        Game.Temporary[(Coll, "Equinox")] = new PenumbraBridge.TemporaryHold(true, false, DesignBindingService.TemporarySource, true, 5);
        Game.TemporaryKeys[(Coll, "Equinox")] = DesignBindingService.TemporaryKey;
        var r = Hold("Equinox", false);
        Assert.Equal((409, "held-elsewhere"), (r.Status, r.Code));
        Assert.Empty(Game.HoldSets);
        PutBack();
        Assert.All(Game.Sweeps, s => Assert.Equal(HarnessChanges.HoldKey, s.Key));
        Assert.True(Game.Temporary.ContainsKey((Coll, "Equinox")));
    }

    // [EDGE-harness-equip-19]
    [Fact]
    public void PositiveLock_HeldElsewhere_RealSource()
    {
        Game.Temporary[(Coll, "Equinox")] = new PenumbraBridge.TemporaryHold(true, true, "OtherPlugin", false, 0);
        var r = Hold("Equinox", false);
        Assert.Equal((409, "held-elsewhere"), (r.Status, r.Code));
        Assert.Contains("OtherPlugin", r.Message);
    }

    // [EDGE-harness-equip-21]
    [Fact]
    public void SecondHold_Merges_BeforeKept()
    {
        Game.Settings["Equinox"] = Ok(true, 530);
        Hold("Equinox", false);
        Game.Settings["Equinox"] = Ok(false, 530, temporary: true);
        Hold("Equinox", null, 2000);
        var last = Game.HoldSets.Last();
        Assert.False(last.Enabled);
        Assert.Equal(2000, last.Priority);
        var before = (Dictionary<string, object?>)Hold("Equinox", null, 2001).Body!;
        Assert.Contains("enabled = True", before["before"]!.ToString());
        Assert.Contains("priority = 530", before["before"]!.ToString());
    }

    // [EDGE-harness-equip-22]
    [Fact]
    public void HoldReplaced_Changed_NoClear()
    {
        Hold("Equinox", false);
        Game.Temporary[(Coll, "Equinox")] = new PenumbraBridge.TemporaryHold(true, false, "Glamourer", true, 1);
        Game.TemporaryKeys[(Coll, "Equinox")] = 12;   // Glamourer's own key: our sweep never reaches it
        var r = PutBack();
        Assert.Equal("changed", Assert.Single(r.Holds).Result);
        Assert.Equal("Glamourer", Game.Temporary[(Coll, "Equinox")].Source);
        Assert.DoesNotContain(Game.Calls, c => c.Member == nameof(IHarnessGame.ClearHold));
    }

    // [EDGE-harness-equip-23]
    [Fact]
    public void HoldRemoved_Gone()
    {
        Hold("Equinox", false);
        Game.Temporary.Clear();
        Assert.Equal("gone", Assert.Single(PutBack().Holds).Result);

        Hold("Other", false);
        Game.ClearHoldAnswer = "ModMissing";
        Assert.Equal("gone", Assert.Single(PutBack().Holds).Result);
    }

    // [EDGE-harness-equip-24]
    [Fact]
    public void CollectionSwitched_ClearRecorded_SweepBoth()
    {
        var first = Coll;
        Hold("Equinox", false);
        Game.Collection = Guid.Parse("99999999-2222-3333-4444-555555555555");
        var r = PutBack();
        Assert.Equal("cleared", Assert.Single(r.Holds).Result);
        Assert.Contains(Game.Sweeps, s => s.Collection == first);
        Assert.Contains(Game.Sweeps, s => s.Collection == Game.Collection);
    }

    // [AC-harness-equip-6] [EDGE-harness-equip-28]
    [Fact]
    public void PutBack_PartialFailure_RetriesOnlyFailed()
    {
        Equip("Body", 500);
        Equip("Legs", 600);
        Game.SetGearAnswer = (slot, _) => slot == "Legs" ? "ActorNotFound" : null;
        var r = PutBack();
        Assert.Equal(["restored", "failed"], r.Gear.Select(g => g.Result));
        Assert.Equal("ActorNotFound", r.Gear[1].Code);
        Game.SetGearAnswer = (_, _) => null;
        var again = PutBack();
        Assert.Equal(new UndoEntry("Legs", "restored"), Assert.Single(again.Gear));
        Assert.Empty(PutBack().Gear);
    }

    // [EDGE-harness-equip-29]
    [Fact]
    public void PutBack_EntryThrows_OthersUndone()
    {
        Equip("Body", 500);
        Equip("Legs", 600);
        Game.OnSetGear = slot => { if (slot == "Body") throw new InvalidOperationException("boom"); };
        var r = PutBack();
        Assert.Equal(new UndoEntry("Body", "failed", "internal"), r.Gear[0]);
        Assert.Equal("restored", r.Gear[1].Result);
    }

    // [EDGE-harness-equip-30]
    [Fact]
    public void LeftoverOwnHold_Ours_Swept()
    {
        Game.Temporary[(Coll, "Equinox")] = new PenumbraBridge.TemporaryHold(true, false, HarnessChanges.HoldSource, false, 0);
        Game.TemporaryKeys[(Coll, "Equinox")] = HarnessChanges.HoldKey;
        Game.Temporary[(Coll, "Leftover")] = new PenumbraBridge.TemporaryHold(true, false, HarnessChanges.HoldSource, false, 0);
        Game.TemporaryKeys[(Coll, "Leftover")] = HarnessChanges.HoldKey;
        var r = Hold("Equinox", true);
        Assert.Equal(200, r.Status);
        Assert.Contains("temporary = True", ((Dictionary<string, object?>)r.Body!)["before"]!.ToString());
        PutBack();
        Assert.Empty(Game.Temporary);
    }

    // [EDGE-harness-equip-44]
    [Fact]
    public void Sweep_Success_RequestsRedraw_NothingChanged_DoesNot()
    {
        Game.TemporaryKeys[(Coll, "Leftover")] = HarnessChanges.HoldKey;
        Game.Temporary[(Coll, "Leftover")] = new PenumbraBridge.TemporaryHold(true, false, HarnessChanges.HoldSource, false, 0);
        var r = PutBack();
        Assert.Equal("ok", r.Sweep);
        Assert.True(r.RedrawRequested);
        var quiet = PutBack();
        Assert.Equal("ok", quiet.Sweep);
        Assert.False(quiet.RedrawRequested);
        Game.PenumbraUp = false;
        Assert.Equal("skipped", PutBack().Sweep);
    }

    // [EDGE-harness-equip-36]
    [Fact]
    public void HoldKey_DistinctFromDesignBinding()
    {
        Assert.True(HarnessChanges.HoldKey < 0);
        Assert.NotEqual(DesignBindingService.TemporaryKey, HarnessChanges.HoldKey);
        Assert.NotEqual(DesignBindingService.LegacyTemporaryKey, HarnessChanges.HoldKey);
        Assert.NotEqual(DesignBindingService.TemporarySource, HarnessChanges.HoldSource);
    }

    // [AC-harness-equip-1]
    [Fact]
    public void ChangeMembers_RunOnFrameworkThread()
    {
        Equip("Body", 500);
        Hold("Equinox", false);
        PutBack();
        string[] members = [nameof(IHarnessGame.GearNow), nameof(IHarnessGame.SetGear), nameof(IHarnessGame.QueryHold),
                            nameof(IHarnessGame.ReadSettings), nameof(IHarnessGame.SetHold), nameof(IHarnessGame.ClearHold),
                            nameof(IHarnessGame.SweepHolds), nameof(IHarnessGame.RequestRedraw)];
        var calls = Game.Calls.Where(c => members.Contains(c.Member)).ToList();
        Assert.Equal(members.Length, calls.Select(c => c.Member).Distinct().Count());
        Assert.All(calls, c => Assert.Equal(fx.Dispatcher.ThreadId, c.Thread));
    }
}
