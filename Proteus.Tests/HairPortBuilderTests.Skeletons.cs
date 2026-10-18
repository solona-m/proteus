using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>
/// Ports moving with the skeleton the game's table gives each race for the hairstyle (most races' hairstyle 8 loads
/// skeleton 9), given one that has the bones where it does not — at a slot no other hairstyle of the race loads. The
/// source c0801 h0130 moves with <c>j_ex_h0170_ke_b</c>; the target is c0201.
/// </summary>
public partial class HairPortBuilderTests
{
    /// <summary>A skeleton without the hairstyle's bone.</summary>
    private static readonly byte[] Lacking = System.Text.Encoding.ASCII.GetBytes("\0j_ex_h0170_ke_c\0");

    /// <summary>The source on hairstyle 170's bones, published from the option with no entries of the mod's.</summary>
    private static Mod BorrowMod() => PhysicsMod([]);

    /// <summary>The game: the table given, the Miqo'te's skeleton 170 and its physics, and any other files.</summary>
    private static Func<string, byte[]?> TableGame((ushort Race, int Set, int Skeleton)[] table,
                                                   Dictionary<string, byte[]>? more = null)
    {
        var files = new Dictionary<string, byte[]>(more ?? [])
        {
            [HairSkeletonTable.GamePath] = EstBytes(table),
        };
        files.TryAdd(HairPortPlan.SkeletonPath(801, 170), H0170Skeleton);
        files.TryAdd(HairPortPlan.PhysicsPath(801, 170), H0170Physics);
        return Game(Races, files);
    }

    /// <summary>The one port of the Midlander target — with sharers, the races reading its material are ported too.</summary>
    private static HairPortWriter.PortWrite BuildOne(Mod mod, Func<string, byte[]?> game, bool withSharers = false)
    {
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, withSharers);
        Assert.False(plan.Refused.ContainsKey(201), plan.Refused.GetValueOrDefault((ushort)201));
        return Assert.Single(plan.Writes, w => w.TargetGamePath == Target);
    }

    private static bool OutOfDate(Mod mod, Func<string, byte[]?> game)
    {
        var record = HairPortRecord.Read(mod.Root);
        var entry = record.Ports.Single();
        return HairPortBuilder.IsOutOfDate(mod.Root, entry, mod.Get(entry.SourceFile), mod.Owners, record.Ports, game);
    }

    /// <summary>
    /// With the port's place on, the target race loads — by the game's table, or the entry the port adds — a skeleton
    /// with every extra bone the model moves with; and a skeleton the port publishes is at a slot only this hairstyle of
    /// the race loads.
    /// </summary>
    internal static void AssertLoadsEveryBone(HairPortWriter.PortWrite write, byte[] model, Func<string, byte[]?> game,
                                              Func<string, byte[]?>? readMod = null)
    {
        var (to, id) = HairPortPlan.Parse(write.TargetGamePath)!.Value;
        var table = HairSkeletonTable.Read(game)!;
        int k = write.Est > 0 ? write.Est : table.Loaded(to, id);
        Assert.True(k > 0, $"c{to:D4} h{id:D4} loads no skeleton");
        var path = HairPortPlan.SkeletonPath(to, k);
        var published = write.Materials.FirstOrDefault(m => m.GamePath == path);
        var bytes = published == null ? game(path)
                  : published.Bytes ?? readMod?.Invoke(published.AuthorFile!);
        var bones = HairPortPlan.SkeletonBones(bytes);
        var extra = HairPortSkeleton.ExtraBones(model);
        Assert.True(extra.All(bones.Contains),
                    $"c{to:D4} h{id:D4} loads skeleton {k}, which lacks {string.Join(", ", extra.Where(b => !bones.Contains(b)))}");
        if (published == null) return;
        Assert.All(table.SetsLoading(to, k), s => Assert.Equal(id, s));
        Assert.Null(game(HairPortPlan.KinematicsPath(to, k)));
    }

    /// <summary>Rewrite a saved carry as ports made before the game's table was read wrote it: no entry, the skeleton at
    /// the hairstyle's own number.</summary>
    private static void MakeOld(Mod mod)
    {
        var record = HairPortRecord.Read(mod.Root);
        foreach (var e in record.Ports) e.Est = 0;
        HairPortRecord.Write(mod.Root, record);
        EditMeta(mod, n =>
        {
            var manips = n["Groups"]![0]!["Options"]![0]!["Manipulations"]!.AsArray();
            for (int i = manips.Count - 1; i >= 0; i--)
                if (HairPortPlan.HairEstEntry(System.Text.Json.JsonSerializer.SerializeToElement(manips[i]), 201, Id) != null)
                    manips.RemoveAt(i);
        });
    }

    [Theory]
    [Trait("Plan", "AC-hair-skeleton-infer-1")]
    [Trait("Plan", "AC-hair-skeleton-infer-3")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-20")]
    [InlineData(false)]
    [InlineData(true)]
    public void A_hairstyle_on_the_skeleton_its_race_loads_ports_with_that_skeleton_copied_to_a_free_slot(bool withSharers)
    {
        using var mod = BorrowMod();
        var game = TableGame([(801, Id, 170), (201, Id, 170)], new() { [HairPortPlan.SkeletonPath(201, 170)] = Lacking });
        var write = BuildOne(mod, game, withSharers);
        Assert.Equal((170, 170), (write.Est, write.SkeletonFrom));
        Assert.Equal(H0170Skeleton, write.Materials.Single(m => m.GamePath == HairPortPlan.SkeletonPath(201, 170)).Bytes);
        Assert.Equal(H0170Physics, write.Materials.Single(m => m.GamePath == HairPortPlan.PhysicsPath(201, 170)).Bytes);
        AssertLoadsEveryBone(write, mod.Get("long.mdl"), game);
    }

    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-2")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-2")]
    public void The_mods_source_entry_overrides_the_games_table()
    {
        using var mod = PhysicsMod([PenumbraManipulations.EstManipulation("0801", "Hair", Id, 170)]);
        var write = BuildOne(mod, TableGame([(801, Id, 171), (201, Id, Id)],
                                            new() { [HairPortPlan.SkeletonPath(201, Id)] = Lacking }));
        Assert.Equal(170, write.SkeletonFrom);
    }

    /// <summary>What the target loads by the table decides — not the file at the hairstyle's own number.</summary>
    [Theory]
    [Trait("Plan", "AC-hair-skeleton-infer-2")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-5")]
    [InlineData(true)]
    [InlineData(false)]
    public void A_target_whose_loaded_skeleton_has_the_bones_gets_nothing_added(bool loadedHasThem)
    {
        using var mod = BorrowMod();
        var game = TableGame([(801, Id, 170), (201, Id, 150)], new()
        {
            [HairPortPlan.SkeletonPath(201, 150)] = loadedHasThem ? H0170Skeleton : Lacking,
            [HairPortPlan.SkeletonPath(201, Id)] = H0170Skeleton,
            [HairPortPlan.SkeletonPath(201, 170)] = Lacking,
        });
        var write = BuildOne(mod, game);
        if (loadedHasThem) Assert.Equal((0, 0), (write.Est, write.SkeletonFrom));
        else Assert.Equal(170, write.SkeletonFrom);
        AssertLoadsEveryBone(write, mod.Get("long.mdl"), game);
    }

    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-3")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-6")]
    public void A_target_with_the_sources_skeleton_of_its_own_gets_an_entry_to_it()
    {
        using var mod = BorrowMod();
        var game = TableGame([(801, Id, 170), (201, Id, 150)], new()
        {
            [HairPortPlan.SkeletonPath(201, 150)] = Lacking,
            [HairPortPlan.SkeletonPath(201, 170)] = H0170Skeleton,
        });
        var write = BuildOne(mod, game);
        Assert.Equal((170, 0), (write.Est, write.SkeletonFrom));
        Assert.DoesNotContain(write.Materials, m => m.GamePath.Contains("/skeleton/", StringComparison.Ordinal));
        AssertLoadsEveryBone(write, mod.Get("long.mdl"), game);
        Assert.True(HairPortWriter.Save(mod.Root, [write], auto: false).Ok);
        Assert.False(OutOfDate(mod, game));
        Assert.True(HairPortWriter.Undo(mod.Root, _ => true, decline: false).Ok);
        Assert.Empty(EstsOf(mod, 201));
    }

    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-3")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-7")]
    public void A_slot_another_hairstyle_loads_is_never_used()
    {
        using var mod = BorrowMod();
        var game = TableGame([(801, Id, 170), (201, Id, 170), (201, 171, 170)],
                             new() { [HairPortPlan.SkeletonPath(201, 170)] = Lacking });
        var write = BuildOne(mod, game);
        Assert.Equal((Id, 170), (write.Est, write.SkeletonFrom));
        Assert.DoesNotContain(write.Materials, m => m.GamePath == HairPortPlan.SkeletonPath(201, 170));
        AssertLoadsEveryBone(write, mod.Get("long.mdl"), game);
    }

    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-3")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-8")]
    public void A_taken_L_and_id_falls_to_the_lowest_free_slot()
    {
        using var mod = BorrowMod();
        var game = TableGame([(801, Id, 170), (201, Id, 170), (201, 171, 170), (201, 129, Id), (201, 1, 1)],
                             new() { [HairPortPlan.SkeletonPath(201, 170)] = Lacking });
        var write = BuildOne(mod, game);
        Assert.Equal((2, 170), (write.Est, write.SkeletonFrom));
        AssertLoadsEveryBone(write, mod.Get("long.mdl"), game);
    }

    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-4")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-3")]
    public void A_source_entry_of_0_loads_no_skeleton_so_nothing_is_carried()
    {
        using var mod = PhysicsMod([PenumbraManipulations.EstManipulation("0801", "Hair", Id, 0)]);
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), TableGame([(801, Id, 170), (201, Id, Id)]), false);
        Assert.Empty(plan.Writes);
        Assert.Contains("j_ex_h0170_ke_b", plan.Refused[201]);
    }

    /// <summary>The author's own entry for the target is checked alone: the target's model naming the bone does not
    /// make a skeleton that lacks it fit.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-4")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-4")]
    public void An_authors_target_entry_is_checked_alone()
    {
        using var mod = PhysicsMod([PenumbraManipulations.EstManipulation("0201", "Hair", Id, 150)]);
        var game = TableGame([(801, Id, 170), (201, Id, Id)], new()
        {
            [HairPortPlan.HairPath(201, Id)] = HairModel(201, bones: [("j_ex_h0170_ke_b", 1f)]),
            [HairPortPlan.SkeletonPath(201, 150)] = Lacking,
        });
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, withSharers: false);
        Assert.Empty(plan.Writes);
        Assert.Contains("j_ex_h0170_ke_b", plan.Refused[201]);
    }

    /// <summary>A slot an entry of the mod points another hairstyle of the race at is that hairstyle's.</summary>
    [Theory]
    [Trait("Plan", "AC-hair-skeleton-infer-4")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-10")]
    [InlineData(false)]
    [InlineData(true)]
    public void A_slot_another_entry_of_the_mod_names_is_skipped(bool inTheDefaultData)
    {
        var other = PenumbraManipulations.EstManipulation("0201", "Hair", 140, 170);
        using var mod = inTheDefaultData ? PhysicsMod([], [other]) : PhysicsMod([other]);
        var game = TableGame([(801, Id, 170), (201, Id, 170)], new() { [HairPortPlan.SkeletonPath(201, 170)] = Lacking });
        Assert.Equal((Id, 170), (BuildOne(mod, game) is var w ? (w.Est, w.SkeletonFrom) : default));
    }

    /// <summary>A slot the mod publishes a skeleton at is the author's; the port's own files from before are not.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-4")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-11")]
    public void A_slot_the_mod_publishes_is_skipped_but_the_ports_own_is_not()
    {
        using var mod = BorrowMod();
        EditMeta(mod, n => n["DefaultData"]!["Files"]![HairPortPlan.SkeletonPath(201, 170)] = "author.sklb");
        mod.Put("author.sklb", H0170Skeleton);
        var game = TableGame([(801, Id, 170), (201, Id, 170)], new() { [HairPortPlan.SkeletonPath(201, 170)] = Lacking });
        var write = BuildOne(mod, game);
        Assert.Equal((Id, 170), (write.Est, write.SkeletonFrom));
        Assert.True(HairPortWriter.Save(mod.Root, [write], auto: false).Ok);

        // Remade, it keeps the slot its own files are at, with one entry.
        mod.Put("long.mdl", HairModel(801, material: "/mt_c0801h0130_hir_b.mtrl", bones: [("j_ex_h0170_ke_b", 1f)]));
        var remake = BuildOne(mod, game);
        Assert.Equal((Id, 170), (remake.Est, remake.SkeletonFrom));
        Assert.True(HairPortWriter.Save(mod.Root, [remake], auto: false).Ok);
        Assert.Equal([Id], EstsOf(mod, 201));
    }

    /// <summary>The mod came to publish the slot between the plan and the save: nothing is saved.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-4")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-13")]
    public void A_carried_skeleton_at_another_slot_taken_meanwhile_refuses_the_save()
    {
        using var mod = BorrowMod();
        var game = TableGame([(801, Id, 170), (201, Id, 170)], new() { [HairPortPlan.SkeletonPath(201, 170)] = Lacking });
        var write = BuildOne(mod, game);
        Assert.Equal(170, write.Est);
        EditMeta(mod, n => n["DefaultData"]!["Files"]![HairPortPlan.SkeletonPath(201, 170)] = "author.sklb");
        mod.Put("author.sklb", H0170Skeleton);

        Assert.False(HairPortWriter.Save(mod.Root, [write], auto: false).Ok);
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target));
        Assert.Empty(EstsOf(mod, 201));
        Assert.Empty(HairPortRecord.Read(mod.Root).Ports);
    }

    /// <summary>Served from the default data and an option — in one build, or the option's ported later — both ports
    /// carry to the same slot and are current.</summary>
    [Theory]
    [Trait("Plan", "AC-hair-skeleton-infer-7")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-14")]
    [InlineData(false)]
    [InlineData(true)]
    public void Carried_ports_in_the_default_data_and_an_option_share_their_slot(bool optionLater)
    {
        using var mod = BorrowMod();
        // The default data's port goes to the hairstyle's own number while slot 170 is the author's …
        EditMeta(mod, n =>
        {
            n["DefaultData"]!["Files"]![Source] = "default.mdl";
            n["DefaultData"]!["Files"]![HairPortPlan.SkeletonPath(201, 170)] = "author.sklb";
            if (optionLater) n["Groups"]![0]!["Options"]![0]!["Files"]!.AsObject().Remove(Source);
        });
        mod.Put("author.sklb", H0170Skeleton);
        mod.Put("default.mdl", HairModel(801, material: "/mt_c0801h0130_hir_b.mtrl", bones: [("j_ex_h0170_ke_b", 1f)]));
        var game = TableGame([(801, Id, 170), (201, Id, 170)], new() { [HairPortPlan.SkeletonPath(201, 170)] = Lacking });

        var first = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, withSharers: false);
        Assert.True(HairPortWriter.Save(mod.Root, first.Writes, auto: false).Ok);
        if (optionLater)
        {
            // … and once the author's skeleton is gone and the option serves the hairstyle too, the option's port joins
            // its sibling's slot rather than take the now free 170.
            EditMeta(mod, n =>
            {
                n["DefaultData"]!["Files"]!.AsObject().Remove(HairPortPlan.SkeletonPath(201, 170));
                n["Groups"]![0]!["Options"]![0]!["Files"]![Source] = "long.mdl";
            });
            var second = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, withSharers: false);
            Assert.Equal((Id, 170), (Assert.Single(second.Writes).Est, second.Writes[0].SkeletonFrom));
            Assert.True(HairPortWriter.Save(mod.Root, second.Writes, auto: false).Ok);
        }
        else Assert.All(first.Writes, w => Assert.Equal((Id, 170), (w.Est, w.SkeletonFrom)));

        var record = HairPortRecord.Read(mod.Root);
        Assert.Equal(2, record.Ports.Count);
        Assert.All(record.Ports, e => Assert.False(
            HairPortBuilder.IsOutOfDate(mod.Root, e, mod.Get(e.SourceFile), mod.Owners, record.Ports, game), $"{e.Group}/{e.Option}"));
        Assert.Contains((ushort)201, HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, withSharers: false).Current);
    }

    /// <summary>
    /// A carry made before the game's table was read — at the hairstyle's own number, no entry — where the race loads
    /// another skeleton for the hairstyle: out of date to the builder, Studio and the automatic path; remade at the slot
    /// the race loads, with one entry, the old files taken out. Where the race loads exactly that slot and nothing else
    /// does, the old carry works and is left.
    /// </summary>
    [Theory]
    [Trait("Plan", "AC-hair-skeleton-infer-5")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-15")]
    [InlineData(true)]
    [InlineData(false)]
    public void An_old_carry_is_out_of_date_everywhere_where_the_race_does_not_load_it(bool racesLoadAnother)
    {
        using var mod = BorrowMod();
        var made = TableGame([(801, Id, 170), (201, Id, Id)]);
        Assert.True(HairPortWriter.Save(mod.Root, [BuildOne(mod, made)], auto: false).Ok);
        MakeOld(mod);
        var oldSkl = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), HairPortPlan.SkeletonPath(201, Id))!;

        var game = racesLoadAnother
            ? TableGame([(801, Id, 170), (201, Id, 170)], new() { [HairPortPlan.SkeletonPath(201, 170)] = Lacking })
            : made;
        Assert.Equal(racesLoadAnother, OutOfDate(mod, game));
        var record = HairPortRecord.Read(mod.Root);
        var rows = Gui.HairPortPanel.Rows(Source, mod.Owners, record, _ => true,
                                          Gui.HairPortPanel.OutOfDate(mod.Root, mod.Owners, record, game));
        Assert.Equal(racesLoadAnother ? Gui.HairPortPanel.State.Stale : Gui.HairPortPanel.State.Ported,
                     rows.Single(r => r.Race == 201).State);
        var worn = HairPortPlan.HairPath(201, Id);
        var decision = HairPortWatcher.Decide.What(worn,
            new Dictionary<string, string?> { [worn] = Path.Combine(mod.Root, record.Ports.Single().File) }, [],
            Path.GetDirectoryName(mod.Root)!, HairPortRecord.Read, game);
        Assert.Equal(racesLoadAnother ? HairPortWatcher.Decide.Kind.Port : HairPortWatcher.Decide.Kind.Ported, decision.Kind);
        if (!racesLoadAnother) return;
        Assert.True(decision.Stale);

        var remake = BuildOne(mod, game);
        Assert.Equal((170, 170), (remake.Est, remake.SkeletonFrom));
        Assert.True(HairPortWriter.Save(mod.Root, [remake], auto: false).Ok);
        Assert.Equal([170], EstsOf(mod, 201));
        var owner = mod.Owner("Cut", "Long");
        Assert.Null(HairPortPlan.FileFor(owner, HairPortPlan.SkeletonPath(201, Id)));
        Assert.Null(HairPortPlan.FileFor(owner, HairPortPlan.PhysicsPath(201, Id)));
        Assert.False(mod.Has(oldSkl));
        Assert.False(OutOfDate(mod, game));
    }

    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-5")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-16")]
    public void An_old_carry_undone_takes_its_skeleton_out()
    {
        using var mod = PhysicsMod([PenumbraManipulations.EstManipulation("0801", "Hair", Id, 170)]);
        Assert.True(HairPortWriter.Save(mod.Root, [BuildOne(mod, MiqoteOnlyBones())], auto: false).Ok);
        MakeOld(mod);
        var skl = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), HairPortPlan.SkeletonPath(201, Id))!;

        Assert.True(HairPortWriter.Undo(mod.Root, _ => true, decline: false).Ok);
        var owner = mod.Owner("Cut", "Long");
        Assert.Null(HairPortPlan.FileFor(owner, Target));
        Assert.Null(HairPortPlan.FileFor(owner, HairPortPlan.SkeletonPath(201, Id)));
        Assert.Null(HairPortPlan.FileFor(owner, HairPortPlan.PhysicsPath(201, Id)));
        Assert.False(mod.Has(skl));
        Assert.Equal(170, HairPortPlan.HairEst(owner, 801, Id));
    }

    /// <summary>An entry the mod gives the source that the target has a fitting skeleton of: an entry to it, as before
    /// the table was read — even though what the target loads fits as well.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-6")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-24")]
    public void A_source_entry_mirrored_today_is_still_current_when_the_loaded_skeleton_fits()
    {
        using var mod = PhysicsMod([PenumbraManipulations.EstManipulation("0801", "Hair", Id, 170)]);
        var game = TableGame([(801, Id, 170), (201, Id, 150)], new()
        {
            [HairPortPlan.SkeletonPath(201, 150)] = H0170Skeleton,
            [HairPortPlan.SkeletonPath(201, 170)] = H0170Skeleton,
        });
        var write = BuildOne(mod, game);
        Assert.Equal((170, 0), (write.Est, write.SkeletonFrom));
        Assert.True(HairPortWriter.Save(mod.Root, [write], auto: false).Ok);
        Assert.False(OutOfDate(mod, game));
    }

    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-6")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-1")]
    public void A_model_without_extra_bones_never_reads_the_table()
    {
        using var mod = Cuts();
        var game = Game(Races);
        Func<string, byte[]?> guarded = p => p == HairSkeletonTable.GamePath ? throw new InvalidOperationException("read") : game(p);
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), guarded, withSharers: false);
        Assert.Empty(plan.Refused);
        Assert.Equal(2, plan.Writes.Count);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: false).Ok);
        var record = HairPortRecord.Read(mod.Root);
        Assert.All(record.Ports, e => Assert.False(HairPortBuilder.IsOutOfDate(mod.Root, e, mod.Get(e.SourceFile), mod.Owners, record.Ports, guarded)));
    }

    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-7")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-23")]
    public void A_carried_port_stays_current_everywhere_and_undo_takes_all_it_added()
    {
        using var mod = BorrowMod();
        var game = TableGame([(801, Id, 170), (201, Id, 170)], new() { [HairPortPlan.SkeletonPath(201, 170)] = Lacking });
        Assert.True(HairPortWriter.Save(mod.Root, [BuildOne(mod, game)], auto: false).Ok);
        Assert.False(OutOfDate(mod, game));
        Assert.Contains((ushort)201, HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, withSharers: false).Current);
        var record = HairPortRecord.Read(mod.Root);
        var rows = Gui.HairPortPanel.Rows(Source, mod.Owners, record, _ => true,
                                          Gui.HairPortPanel.OutOfDate(mod.Root, mod.Owners, record, game));
        Assert.Equal(Gui.HairPortPanel.State.Ported, rows.Single(r => r.Race == 201).State);
        var worn = HairPortPlan.HairPath(201, Id);
        Assert.Equal(HairPortWatcher.Decide.Kind.Ported, HairPortWatcher.Decide.What(worn,
            new Dictionary<string, string?> { [worn] = Path.Combine(mod.Root, record.Ports.Single().File) }, [],
            Path.GetDirectoryName(mod.Root)!, HairPortRecord.Read, game).Kind);

        mod.Put("long.mdl", HairModel(801, material: "/mt_c0801h0130_hir_b.mtrl", bones: [("j_ex_h0170_ke_b", 1f)]));
        Assert.True(OutOfDate(mod, game));
        var remake = BuildOne(mod, game);
        Assert.Equal((170, 170), (remake.Est, remake.SkeletonFrom));
        Assert.True(HairPortWriter.Save(mod.Root, [remake], auto: false).Ok);
        Assert.Equal([170], EstsOf(mod, 201));
        var skl = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), HairPortPlan.SkeletonPath(201, 170))!;

        Assert.True(HairPortWriter.Undo(mod.Root, _ => true, decline: false).Ok);
        var owner = mod.Owner("Cut", "Long");
        Assert.Null(HairPortPlan.FileFor(owner, Target));
        Assert.Null(HairPortPlan.FileFor(owner, HairPortPlan.SkeletonPath(201, 170)));
        Assert.Null(HairPortPlan.FileFor(owner, HairPortPlan.PhysicsPath(201, 170)));
        Assert.False(mod.Has(skl));
        Assert.Empty(EstsOf(mod, 201));
    }

    /// <summary>Studio (no sharers) and the automatic path write the same skeleton, by either route.</summary>
    [Theory]
    [Trait("Plan", "AC-hair-skeleton-infer-7")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-20")]
    [InlineData(true)]
    [InlineData(false)]
    public void Studio_and_the_automatic_path_write_the_same_skeleton(bool entryRoute)
    {
        using var mod = BorrowMod();
        var game = TableGame([(801, Id, 170), (201, Id, 170)],
                             new() { [HairPortPlan.SkeletonPath(201, 170)] = entryRoute ? H0170Skeleton : Lacking });
        var studio = BuildOne(mod, game, withSharers: false);
        var auto = BuildOne(mod, game, withSharers: true);
        Assert.Equal((studio.Est, studio.SkeletonFrom), (auto.Est, auto.SkeletonFrom));
        Assert.Equal(studio.Materials.Select(m => m.GamePath).Order(), auto.Materials.Select(m => m.GamePath).Order());
    }

    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-7")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-22")]
    public void An_authors_own_source_skeleton_is_named_at_the_slot_not_copied()
    {
        using var mod = BorrowMod();
        EditMeta(mod, n => n["Groups"]![0]!["Options"]![0]!["Files"]![HairPortPlan.SkeletonPath(801, 170)] = "skl.sklb");
        mod.Put("skl.sklb", H0170Skeleton);
        var write = BuildOne(mod, TableGame([(801, Id, 170), (201, Id, Id)]));
        var skl = Assert.Single(write.Materials, m => m.GamePath == HairPortPlan.SkeletonPath(201, Id));
        Assert.Equal(("skl.sklb", null), (skl.AuthorFile, skl.Bytes));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-skeleton-infer-17")]
    public void A_slot_the_game_gives_another_hairstyle_since_makes_the_port_stale()
    {
        using var mod = BorrowMod();
        var before = TableGame([(801, Id, 170), (201, Id, Id)]);
        Assert.True(HairPortWriter.Save(mod.Root, [BuildOne(mod, before)], auto: false).Ok);
        var after = TableGame([(801, Id, 170), (201, Id, Id), (201, 160, Id)]);
        Assert.False(OutOfDate(mod, before));
        Assert.True(OutOfDate(mod, after));
        Assert.Equal(1, BuildOne(mod, after).Est);
    }

    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-9")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-19")]
    public void An_unreadable_table_refuses_only_ports_that_need_it()
    {
        var noTable = Game(Races, new Dictionary<string, byte[]>
        {
            [HairPortPlan.SkeletonPath(801, 170)] = H0170Skeleton, [HairPortPlan.PhysicsPath(801, 170)] = H0170Physics,
        });
        using (var mod = BorrowMod())
        {
            var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), noTable, withSharers: false);
            Assert.Empty(plan.Writes);
            Assert.Equal(HairPortBuilder.NoSkeletonTable, plan.Refused[201]);
            Assert.True(plan.Unreadable);

            // A carry made with the table is left as it is without it; one from before the table was read is not.
            Assert.True(HairPortWriter.Save(mod.Root, [BuildOne(mod, TableGame([(801, Id, 170), (201, Id, Id)]))], false).Ok);
            Assert.False(OutOfDate(mod, noTable));
            MakeOld(mod);
            Assert.True(OutOfDate(mod, noTable));
        }
        using (var cuts = Cuts())
            Assert.Equal(2, HairPortBuilder.Build(cuts.Root, Source, [201], Pbd(), noTable, withSharers: false).Writes.Count);
    }

    /// <summary>A port made with nothing added, on bones its race turns out not to load: remade with a carry.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-skeleton-infer-27")]
    public void A_port_made_on_bones_its_race_does_not_load_is_remade_with_a_carry()
    {
        using var mod = BorrowMod();
        var fits = TableGame([(801, Id, 170), (201, Id, Id)], new() { [HairPortPlan.SkeletonPath(201, Id)] = H0170Skeleton });
        var write = BuildOne(mod, fits);
        Assert.Equal((0, 0), (write.Est, write.SkeletonFrom));
        Assert.True(HairPortWriter.Save(mod.Root, [write], auto: false).Ok);

        var game = TableGame([(801, Id, 170), (201, Id, 150)], new()
        {
            [HairPortPlan.SkeletonPath(201, Id)] = H0170Skeleton, [HairPortPlan.SkeletonPath(201, 150)] = Lacking,
            [HairPortPlan.SkeletonPath(201, 170)] = Lacking,
        });
        Assert.True(OutOfDate(mod, game));
        Assert.Equal(170, BuildOne(mod, game).SkeletonFrom);
    }

    /// <summary>
    /// An old carry that cannot be made again, whose copy another hairstyle of the race loads: saved again without that
    /// skeleton and its physics — the port itself kept — and said why it was not ported.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-skeleton-infer-28")]
    public void An_old_carry_that_cannot_be_remade_is_stripped_of_the_skeleton_another_hairstyle_loads()
    {
        using var mod = BorrowMod();
        Assert.True(HairPortWriter.Save(mod.Root, [BuildOne(mod, TableGame([(801, Id, 170), (201, Id, Id)]))], false).Ok);
        MakeOld(mod);
        var skl = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), HairPortPlan.SkeletonPath(201, Id))!;

        // Another hairstyle of the race loads slot 130 now, and the source's skeleton is gone: no remake.
        var game = Game(Races, new Dictionary<string, byte[]>
        {
            [HairSkeletonTable.GamePath] = EstBytes((801, Id, 170), (201, Id, Id), (201, 131, Id)),
        });
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, withSharers: false);
        Assert.Contains("j_ex_h0170_ke_b", plan.Refused[201]);
        var strip = Assert.Single(plan.Writes);
        Assert.True(strip.Stripped);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: true).Ok);

        var owner = mod.Owner("Cut", "Long");
        Assert.NotNull(HairPortPlan.FileFor(owner, Target));
        Assert.Null(HairPortPlan.FileFor(owner, HairPortPlan.SkeletonPath(201, Id)));
        Assert.Null(HairPortPlan.FileFor(owner, HairPortPlan.PhysicsPath(201, Id)));
        Assert.False(mod.Has(skl));
        Assert.Equal((0, 0), HairPortRecord.Read(mod.Root).Ports.Single() is var e ? (e.Est, e.SkeletonFrom) : default);
    }

    /// <summary>An old carry no other hairstyle loads the copy of, that cannot be made again, is left as it is.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-skeleton-infer-29")]
    public void A_harmless_old_carry_that_cannot_be_remade_is_left()
    {
        using var mod = BorrowMod();
        Assert.True(HairPortWriter.Save(mod.Root, [BuildOne(mod, TableGame([(801, Id, 170), (201, Id, Id)]))], false).Ok);
        MakeOld(mod);
        mod.Put("long.mdl", HairModel(801, material: "/mt_c0801h0130_hir_b.mtrl", bones: [("j_ex_h0170_ke_b", 1f)]));
        var game = Game(Races, new Dictionary<string, byte[]> { [HairSkeletonTable.GamePath] = EstBytes((801, Id, 170), (201, Id, Id)) });
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, withSharers: false);
        Assert.Contains("j_ex_h0170_ke_b", plan.Refused[201]);
        Assert.Empty(plan.Writes);
    }

    /// <summary>The default data publishes its own skeleton where the game's has the bones: that file is what loads, and
    /// it lacks them — no entry points there.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-6")]
    public void An_entry_is_judged_by_the_default_datas_own_skeleton_not_the_games()
    {
        using var mod = BorrowMod();
        EditMeta(mod, n => n["DefaultData"]!["Files"]![HairPortPlan.SkeletonPath(201, 170)] = "other.sklb");
        mod.Put("other.sklb", Lacking);
        var write = BuildOne(mod, TableGame([(801, Id, 170), (201, Id, Id)],
                                            new() { [HairPortPlan.SkeletonPath(201, 170)] = H0170Skeleton }));
        Assert.Equal((Id, 170), (write.Est, write.SkeletonFrom));
    }

    /// <summary>A port whose entry Penumbra dropped as a default (an export round trip) stays current, and undo
    /// still works.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-7")]
    public void A_port_whose_entry_was_dropped_stays_current_and_undoes()
    {
        using var mod = BorrowMod();
        var game = TableGame([(801, Id, 170), (201, Id, Id)]);
        Assert.True(HairPortWriter.Save(mod.Root, [BuildOne(mod, game)], false).Ok);
        Assert.Equal([Id], EstsOf(mod, 201));
        SetOptionManipulations(mod);
        Assert.False(OutOfDate(mod, game));
        Assert.True(HairPortWriter.Undo(mod.Root, _ => true, decline: false).Ok);
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), HairPortPlan.SkeletonPath(201, Id)));
    }
}
