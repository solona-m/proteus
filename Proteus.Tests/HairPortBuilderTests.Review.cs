using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using XivLiveMesh;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>
/// Old carries taken through the paths a player meets them on — worn, checked, stripped — and the edges the review of
/// the game-table work named: hat fits kept, kinematics slots skipped, the author's own skeleton files judged by their
/// bones, ports of other hairstyles never shared with.
/// </summary>
public partial class HairPortBuilderTests
{
    /// <summary>The watcher's view of the plugin, on one mod and the game given.</summary>
    private sealed class PortHost(Mod mod, Func<string, byte[]?> game) : HairPortWatcher.IHost
    {
        public readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Calls = [];
        public bool Enabled => true;
        public string? ModsRoot => Path.GetDirectoryName(mod.Root);
        public string? Resolve(string gamePath) => Files.GetValueOrDefault(gamePath);
        public PbdFile? Pbd() => HairPortFixtures.Pbd();
        public byte[]? ReadGame(string gamePath) => game(gamePath);
        public void Log(string message) { }
        public void Announce() => Calls.Add("announce");
        public void Reload(string modDir) => Calls.Add("reload");
        public string? WornNow() => Target;
        public void Redraw() => Calls.Add("redraw");

        /// <summary>The player wears the port of the target, the source race's model resolving to the author's.</summary>
        public PortHost Wearing()
        {
            Files[Target] = Path.Combine(mod.Root, HairPortRecord.Read(mod.Root).Ports.Single().File);
            Files[Source] = Path.Combine(mod.Root, "long.mdl");
            return this;
        }
    }

    /// <summary>
    /// An old carry at the hairstyle's own number that another hairstyle of the race loads, and that cannot be made again:
    /// the source race's skeleton is gone from the game.
    /// </summary>
    private static (Mod Mod, Func<string, byte[]?> Game) StrippableOldCarry()
    {
        var mod = BorrowMod();
        Assert.True(HairPortWriter.Save(mod.Root, [BuildOne(mod, TableGame([(801, Id, 170), (201, Id, Id)]))], false).Ok);
        MakeOld(mod);
        return (mod, Game(Races, new Dictionary<string, byte[]>
        {
            [HairSkeletonTable.GamePath] = EstBytes((801, Id, 170), (201, Id, Id), (201, 131, Id)),
        }));
    }

    /// <summary>Worn, an old carry the race does not load is remade — with an entry to the slot it now goes to.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-8")]
    [Trait("Plan", "AC-hair-skeleton-infer-5")]
    public void A_worn_old_carry_is_remade_with_an_entry()
    {
        using var mod = BorrowMod();
        Assert.True(HairPortWriter.Save(mod.Root, [BuildOne(mod, TableGame([(801, Id, 170), (201, Id, Id)]))], false).Ok);
        MakeOld(mod);
        var game = TableGame([(801, Id, 170), (201, Id, 170)], new() { [HairPortPlan.SkeletonPath(201, 170)] = Lacking });

        var host = new PortHost(mod, game).Wearing();
        string? key = null;
        var view = HairPortWatcher.Run(host, Target, ref key);
        Assert.True(view.Ported, view.Message);
        var entry = HairPortRecord.Read(mod.Root).Ports.Single(e => e.TargetGamePath == Target);
        Assert.Equal((170, 170), (entry.Est, entry.SkeletonFrom));
        Assert.Contains("reload", host.Calls);
    }

    /// <summary>Only mods with an old carry that is out of date are checked at start: one that works is left.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-8")]
    public void Mods_holding_an_old_carry_are_the_ones_checked_at_start()
    {
        var made = TableGame([(801, Id, 170), (201, Id, Id)]);
        using var stale = BorrowMod();
        using var works = BorrowMod();
        using var fresh = BorrowMod();
        foreach (var m in new[] { stale, works, fresh })
            Assert.True(HairPortWriter.Save(m.Root, [BuildOne(m, made)], false).Ok);
        MakeOld(stale);
        MakeOld(works);

        // The race loads slot 170 for the hairstyle now: the old carry at 130 is wrong.
        var game = TableGame([(801, Id, 170), (201, Id, 170)], new() { [HairPortPlan.SkeletonPath(201, 170)] = Lacking });
        Assert.Equal([stale.Root], HairPortWatcher.Decide.ModsWithOldCarries([stale.Root, fresh.Root], game));
        Assert.Empty(HairPortWatcher.Decide.ModsWithOldCarries([works.Root, fresh.Root], made));
    }

    /// <summary>Stripped on equip: saved and reloaded, and said why it was not ported.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-skeleton-infer-28")]
    public void A_worn_old_carry_that_cannot_be_remade_is_stripped_and_reloaded()
    {
        var (mod, game) = StrippableOldCarry();
        using var _ = mod;
        var host = new PortHost(mod, game).Wearing();
        string? key = null;
        var view = HairPortWatcher.Run(host, Target, ref key);
        Assert.False(view.Ported);
        Assert.True(view.Failed);
        Assert.Contains("j_ex_h0170_ke_b", view.Message);
        Assert.Contains("reload", host.Calls);
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), HairPortPlan.SkeletonPath(201, Id)));
    }

    /// <summary>Stripped by a check: a refused row with why, and the mod reloaded.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-skeleton-infer-28")]
    public void A_checked_old_carry_that_cannot_be_remade_is_stripped_and_shown_refused()
    {
        var (mod, game) = StrippableOldCarry();
        using var _ = mod;
        var host = new PortHost(mod, game).Wearing();
        var result = HairPortScan.Run(host, 201, [Id], null, new HairPortScan.Memo(), force: true, "test",
                                      System.Threading.CancellationToken.None);
        var row = Assert.Single(result.Rows);
        Assert.Equal(HairPortScan.RowState.Refused, row.State);
        Assert.Contains("j_ex_h0170_ke_b", row.Message);
        Assert.Contains("reload", host.Calls);
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), HairPortPlan.SkeletonPath(201, Id)));
    }

    /// <summary>A strip changes only redirects: a hat fit of the port keeps its backup and record, and the port's record
    /// keeps how and when it was made.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-skeleton-infer-28")]
    public void A_strip_keeps_the_ports_hat_fit_and_its_record()
    {
        var (mod, game) = StrippableOldCarry();
        using var _ = mod;
        var before = HairPortRecord.Read(mod.Root).Ports.Single();
        before.PortVersion = HairPortBaker.Version - 1;
        HairPortRecord.Write(mod.Root, new HairPortRecord { Ports = [before] });
        var fitted = mod.Get(before.File);
        var backup = Path.Combine(SidecarDiscoveryService.SidecarSubdir, HatCompatService.BackupSubdir, before.File);
        mod.Put(backup, [1, 2, 3]);
        mod.Put(Path.Combine(SidecarDiscoveryService.SidecarSubdir, HatCompatService.RecordFile),
                System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { Files = new[] { before.File } })));

        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, withSharers: false);
        Assert.True(Assert.Single(plan.Writes).Stripped);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: true).Ok);

        Assert.True(mod.Has(backup));
        Assert.Equal(1, HatCompatService.PatchedCount(mod.Root));
        Assert.Equal(fitted, mod.Get(before.File));
        var after = HairPortRecord.Read(mod.Root).Ports.Single();
        Assert.Equal((before.PortVersion, before.Auto, 0, 0), (after.PortVersion, after.Auto, after.Est, after.SkeletonFrom));
    }

    /// <summary>A carry made with the table whose slot the game gave another hairstyle since, and that cannot be made
    /// again, loses its skeleton and its entry too.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-skeleton-infer-28")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-17")]
    public void A_carry_whose_slot_another_hairstyle_loads_since_is_stripped_when_it_cannot_be_remade()
    {
        using var mod = BorrowMod();
        Assert.True(HairPortWriter.Save(mod.Root, [BuildOne(mod, TableGame([(801, Id, 170), (201, Id, 170)],
            new() { [HairPortPlan.SkeletonPath(201, 170)] = Lacking }))], false).Ok);
        Assert.Equal([170], EstsOf(mod, 201));

        var game = Game(Races, new Dictionary<string, byte[]>
        {
            [HairSkeletonTable.GamePath] = EstBytes((801, Id, 170), (201, Id, 170), (201, 171, 170)),
        });
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, withSharers: false);
        Assert.True(Assert.Single(plan.Writes).Stripped);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: true).Ok);
        Assert.Empty(EstsOf(mod, 201));
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), HairPortPlan.SkeletonPath(201, 170)));
    }

    /// <summary>An old carry the mod's own entry points another hairstyle of the race at is harmful too.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-skeleton-infer-28")]
    public void An_old_carry_a_mod_entry_shares_out_is_stripped_when_it_cannot_be_remade()
    {
        using var mod = BorrowMod();
        Assert.True(HairPortWriter.Save(mod.Root, [BuildOne(mod, TableGame([(801, Id, 170), (201, Id, Id)]))], false).Ok);
        MakeOld(mod);
        SetOptionManipulations(mod, PenumbraManipulations.EstManipulation("0201", "Hair", 140, Id));
        var game = Game(Races, new Dictionary<string, byte[]> { [HairSkeletonTable.GamePath] = EstBytes((801, Id, 170), (201, Id, Id)) });
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, withSharers: false);
        Assert.True(Assert.Single(plan.Writes).Stripped);
    }

    /// <summary>A slot the game ships kinematics at is not paired with a foreign skeleton.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-4")]
    public void A_slot_with_kinematics_is_skipped()
    {
        using var mod = BorrowMod();
        var game = TableGame([(801, Id, 170), (201, Id, 170)], new()
        {
            [HairPortPlan.SkeletonPath(201, 170)] = Lacking,
            [HairPortPlan.KinematicsPath(201, 170)] = [1, 2, 3],
        });
        Assert.Equal((Id, 170), BuildOne(mod, game) is var w ? (w.Est, w.SkeletonFrom) : default);
    }

    /// <summary>An old carry the race does load there, remade (its model changed): at the same slot, overwritten in
    /// place, with one entry.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-5")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-15")]
    public void An_old_carry_remade_at_its_own_slot_is_overwritten_in_place_with_one_entry()
    {
        using var mod = BorrowMod();
        var game = TableGame([(801, Id, 170), (201, Id, Id)]);
        Assert.True(HairPortWriter.Save(mod.Root, [BuildOne(mod, game)], false).Ok);
        MakeOld(mod);
        var skl = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), HairPortPlan.SkeletonPath(201, Id))!;

        mod.Put("long.mdl", HairModel(801, material: "/mt_c0801h0130_hir_b.mtrl", bones: [("j_ex_h0170_ke_b", 1f)]));
        var remake = BuildOne(mod, game);
        Assert.Equal((Id, 170), (remake.Est, remake.SkeletonFrom));
        Assert.True(HairPortWriter.Save(mod.Root, [remake], false).Ok);
        Assert.Equal(skl, HairPortPlan.FileFor(mod.Owner("Cut", "Long"), HairPortPlan.SkeletonPath(201, Id)));
        Assert.True(mod.Has(skl));
        Assert.Equal([Id], EstsOf(mod, 201));
        Assert.False(OutOfDate(mod, game));
    }

    /// <summary>A slot one of our ports of another hairstyle carries its skeleton to is that port's.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-4")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-11")]
    public void A_slot_our_port_of_another_hairstyle_uses_is_skipped()
    {
        using var mod = BorrowMod();
        string other = HairPortPlan.HairPath(201, 140), otherModel = $"{HairPortPlan.Folder}/x/other.mdl",
               otherSkl = $"{HairPortPlan.Folder}/x/other.sklb";
        EditMeta(mod, n =>
        {
            n["Groups"]![0]!["Options"]![0]!["Files"]![other] = otherModel;
            n["Groups"]![0]!["Options"]![0]!["Files"]![HairPortPlan.SkeletonPath(201, 170)] = otherSkl;
        });
        mod.Put(otherModel, HairModel(201));
        mod.Put(otherSkl, H0170Skeleton);
        HairPortRecord.Write(mod.Root, new HairPortRecord
        {
            Ports = [new HairPortRecord.Entry
            {
                Group = "Cut", Option = "Long", SourceGamePath = HairPortPlan.HairPath(801, 140), SourceFile = "long.mdl",
                TargetGamePath = other, File = otherModel, PortVersion = HairPortBaker.Version, SkeletonFrom = 170,
                Materials = new(StringComparer.OrdinalIgnoreCase) { [HairPortPlan.SkeletonPath(201, 170)] = otherSkl },
            }],
        });
        var game = TableGame([(801, Id, 170), (201, Id, 170)], new() { [HairPortPlan.SkeletonPath(201, 170)] = Lacking });
        Assert.Equal((Id, 170), BuildOne(mod, game) is var w ? (w.Est, w.SkeletonFrom) : default);
    }

    /// <summary>
    /// The author ships the target race's skeleton for the borrow themselves: an entry to it is judged by their file —
    /// the one the game loads with the port on — so a port made today stays current, and one that lacks the bones is
    /// not pointed at.
    /// </summary>
    [Theory]
    [Trait("Plan", "AC-hair-skeleton-infer-6")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-24")]
    [InlineData(true)]
    [InlineData(false)]
    public void An_entry_to_the_authors_own_skeleton_is_judged_by_that_file(bool authorsHasTheBones)
    {
        using var mod = PhysicsMod([PenumbraManipulations.EstManipulation("0801", "Hair", Id, 170)]);
        EditMeta(mod, n => n["Groups"]![0]!["Options"]![0]!["Files"]![HairPortPlan.SkeletonPath(201, 170)] = "author.sklb");
        mod.Put("author.sklb", authorsHasTheBones ? H0170Skeleton : Lacking);
        var game = TableGame([(801, Id, 170), (201, Id, Id)],
                             new() { [HairPortPlan.SkeletonPath(201, 170)] = authorsHasTheBones ? Lacking : H0170Skeleton });
        var write = BuildOne(mod, game);
        if (!authorsHasTheBones)
        {
            Assert.Equal((Id, 170), (write.Est, write.SkeletonFrom));
            return;
        }
        Assert.Equal((170, 0), (write.Est, write.SkeletonFrom));
        Assert.True(HairPortWriter.Save(mod.Root, [write], false).Ok);
        Assert.False(OutOfDate(mod, game));
        Assert.Contains((ushort)201, HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, withSharers: false).Current);
    }
}
