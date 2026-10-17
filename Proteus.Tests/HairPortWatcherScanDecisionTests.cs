using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Services.HairPortWatcher;

namespace Proteus.Tests;

/// <summary>The watcher's own decisions around a check: whose race a walk saw, and what the panel keeps of each check.</summary>
public class HairPortWatcherScanDecisionTests
{
    private static HairPortScan.Row Row(int id, string mod = "Floof", HairPortScan.RowState state = HairPortScan.RowState.Fitted)
        => new(id, "root/" + mod, mod, mod, HairPortPlan.HairPath(201, id), state);

    private static HairPortScan.Result Result(int[] examined, params HairPortScan.Row[] rows) => new(examined, rows, false, []);

    [Theory]
    [Trait("Plan", "AC-hair-port-scan-4")]
    [InlineData(new[] { "chara/human/c0201/obj/hair/h0007/model/c0201h0007_hir.mdl",
                        "chara/human/c0201/obj/face/f0001/model/c0201f0001_fac.mdl" }, 201)]
    // A hat hides the hair: the face says the race.
    [InlineData(new[] { "chara/equipment/e0001/model/c0201e0001_met.mdl",
                        "chara/human/c0801/obj/face/f0002/model/c0801f0002_fac.mdl" }, 801)]
    [InlineData(new[] { @"chara\human\c0401\obj\face\f0001\model\c0401f0001_fac.mdl" }, 401)]
    // A face file filed under another race's folder is not trusted.
    [InlineData(new[] { "chara/human/c0201/obj/face/f0001/model/c0801f0001_fac.mdl" }, 0)]
    [InlineData(new[] { "chara/equipment/e0001/model/c0201e0001_top.mdl" }, 0)]
    public void Race_from_the_hair_else_the_face(string[] parts, int race)
        => Assert.Equal(race, Decide.RaceOf(parts));

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-4")]
    public void Nothing_drawn_has_no_race() => Assert.Equal(0, Decide.RaceOf(null));

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-3")]
    [Trait("Plan", "AC-hair-port-scan-7")]
    public void A_check_replaces_only_the_hairstyles_it_looked_at()
    {
        var view = new ScanView(Race: 201, Rows: [Row(5, "Other"), Row(7), Row(9, "Kept")]);
        // A check of one mod: hairstyle 7 refused now, 9 skipped as unchanged, 12 new.
        var merged = Decide.Merge(view, 201, Result([7, 12], Row(7, state: HairPortScan.RowState.Refused), Row(12)), stale: false);
        Assert.Equal([5, 7, 9, 12], merged.List.Select(r => r.Id));
        Assert.Equal(HairPortScan.RowState.Refused, merged.List.Single(r => r.Id == 7).State);

        // Another race's rows are not this race's.
        var other = Decide.Merge(view, 401, Result([12], Row(12)), stale: false);
        Assert.Equal([12], other.List.Select(r => r.Id));
        Assert.Equal(401, other.Race);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-13")]
    public void A_check_across_a_collection_change_brings_nothing_back()
    {
        var cleared = new ScanView(Race: 201, Rows: []);
        var after = Decide.Merge(cleared, 201, Result([7], Row(7)), stale: true);
        Assert.Empty(after.List);
    }

    [Theory]
    [Trait("Plan", "EDGE-hair-port-scan-12")]
    [Trait("Plan", "EDGE-hair-port-scan-13")]
    [InlineData(true, false, false, true, true)]    // the race or collection changed: owed again
    [InlineData(true, true, false, true, false)]    // the player's Cancel
    [InlineData(true, false, true, true, false)]    // unloaded
    [InlineData(true, false, false, false, false)]  // the setting switched off
    [InlineData(false, false, false, true, false)]  // it finished
    public void A_check_stopped_by_a_change_is_owed_again(bool cancelled, bool byPlayer, bool disposed, bool enabled, bool owed)
        => Assert.Equal(owed, Decide.OweAgain(cancelled, byPlayer, disposed, enabled));

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-7")]
    public void Undo_everything_takes_back_only_the_last_checks_fits()
    {
        var view = new ScanView(Race: 201, Rows: [Row(5)]);
        var after = Decide.Merge(view, 201, Result([7, 9], Row(7), Row(9, state: HairPortScan.RowState.Refused)), stale: false);
        Assert.Equal([7], after.Last.Select(r => r.Id));
        Assert.Equal(1, Proteus.Gui.HairPortSettingsPanel.UndoableByLastCheck(after));
        var jobs = Decide.CheckedUndos(after.Last);
        var (root, which) = Assert.Single(jobs);
        Assert.Equal("root/Floof", root);
        Assert.True(which(new HairPortRecord.Entry { TargetGamePath = HairPortPlan.HairPath(201, 7) }));
        Assert.False(which(new HairPortRecord.Entry { TargetGamePath = HairPortPlan.HairPath(201, 5) }));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-24")]
    public void Penumbra_that_never_answers_is_asked_less_and_less_often()
    {
        int wait = 0;
        var waits = new List<int>();
        for (int i = 0; i < 10; i++) waits.Add(wait = Decide.NextRetry(wait));
        Assert.Equal(5000, waits[0]);
        Assert.Equal(10_000, waits[1]);
        Assert.Equal(300_000, waits[^1]);
        Assert.All(waits.Zip(waits.Skip(1)), p => Assert.True(p.Second >= p.First));
    }
}
