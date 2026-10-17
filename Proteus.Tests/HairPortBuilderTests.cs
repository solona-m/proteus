using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>What a port of one hairstyle writes, worked out from the mod and the game, on the synthetic deformer.</summary>
public class HairPortBuilderTests
{
    private static readonly string Source = HairPortPlan.HairPath(801, Id);
    private static readonly string Target = HairPortPlan.HairPath(201, Id);
    private static readonly ushort[] Races = [101, 201, 401, 601, 801, 1001, 1101, 1201, 1401, 1601, 1801];

    private static Mod Cuts()
    {
        var mod = new Mod("[" + Group("Cut", "Single",
                              ("Long", new() { [Source] = "long.mdl" }), ("Short", new() { [Source] = "short.mdl" })) + "]");
        mod.Put("long.mdl", HairModel(801));
        mod.Put("short.mdl", HairModel(801, extra: Sphere("/mt_c0801h0130_hir_a.mtrl", Head + Offset(801), 0.105f,
                                                          [("j_kao", 1f)], minY: 0.5f)));
        return mod;
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-1")]
    [Trait("Plan", "AC-hair-race-port-4")]
    public void Every_option_serving_the_hairstyle_gets_a_port_of_its_own_file()
    {
        using var mod = Cuts();
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), Game(Races), withSharers: true);
        Assert.Empty(plan.Refused);
        Assert.Equal(2, plan.Writes.Count);
        Assert.Equal(["Long", "Short"], plan.Writes.Select(w => w.Into.Option).OrderBy(o => o));
        Assert.All(plan.Writes, w => Assert.Equal(Target, w.TargetGamePath));
        Assert.NotEqual(plan.Writes[0].Model.Length, plan.Writes[1].Model.Length);
        // Hairstyle 130's material is the Midlander one for both races: nothing to redirect.
        Assert.All(plan.Writes, w => Assert.Empty(w.Materials));
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-5")]
    public void Authored_races_and_the_other_sex_are_left_alone()
    {
        using var mod = new Mod(defaults: new Dictionary<string, string> { [Source] = "a.mdl", [Target] = "b.mdl" });
        mod.Put("a.mdl", HairModel(801));
        mod.Put("b.mdl", HairModel(201));
        var plan = HairPortBuilder.Build(mod.Root, Source, [201, 101], Pbd(), Game(Races), withSharers: false);
        Assert.Empty(plan.Writes);
        Assert.False(plan.Refused.ContainsKey(201));
        Assert.True(plan.Refused.ContainsKey(101));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-20")]
    public void A_race_without_the_hairstyle_is_refused()
    {
        using var mod = Cuts();
        var game = Game(Races.Where(r => r != 401));
        var plan = HairPortBuilder.Build(mod.Root, Source, [401], Pbd(), path => path.Contains("c0401h") ? null : game(path),
                                         withSharers: false);
        Assert.Contains("c0401", plan.Refused[401]);
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-8")]
    [Trait("Plan", "EDGE-hair-race-port-15")]
    public void A_current_port_is_left_and_a_stale_one_remade()
    {
        using var mod = Cuts();
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), Game(Races), withSharers: false);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: false).Ok);

        var again = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), Game(Races), withSharers: false);
        Assert.Empty(again.Writes);
        Assert.Contains((ushort)201, again.Current);

        // The author changes one cut: only its port is remade.
        mod.Put("long.mdl", HairModel(801, material: "/mt_c0801h0130_hir_b.mtrl"));
        var stale = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), Game(Races), withSharers: false);
        Assert.Equal(["Long"], stale.Writes.Select(w => w.Into.Option));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-8")]
    [Trait("Plan", "EDGE-hair-race-port-9")]
    public void Combining_and_legacy_mods_are_refused()
    {
        var groups = "[{\"Name\":\"Mix\",\"Type\":\"Combining\",\"Options\":[{\"Name\":\"A\"}],\"Containers\":[{\"Files\":{\""
                     + Source + "\":\"a.mdl\"}}]}]";
        using (var mod = new Mod(groups))
        {
            mod.Put("a.mdl", HairModel(801));
            var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), Game(Races), withSharers: false);
            Assert.Empty(plan.Writes);
            Assert.Contains("Mix", plan.Refused[201]);
        }
        using (var mod = new Mod(defaults: new Dictionary<string, string> { [Source] = "a.mdl" }, fileVersion: 3))
        {
            mod.Put("a.mdl", HairModel(801));
            var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), Game(Races), withSharers: false);
            Assert.Empty(plan.Writes);
            Assert.True(plan.Refused.ContainsKey(201));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-29")]
    public void Cancelled_before_the_save_writes_nothing()
    {
        using var mod = Cuts();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), Game(Races), withSharers: false, cts.Token));
        Assert.False(mod.Has(HairPortPlan.Folder));
        Assert.Empty(HairPortRecord.Read(mod.Root).Ports);
    }

    /// <summary>A Miqo'te's own hairstyle 105: every other race of her sex reads ONE Midlander material for it.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-7")]
    public void Shared_material_drags_its_sharers_along_or_refuses()
    {
        const int id = 105;
        string source = HairPortPlan.HairPath(801, id);
        string mat = "chara/human/c0801/obj/hair/h0105/material/v0001/mt_c0801h0105_hir_a.mtrl";
        using var mod = new Mod(defaults: new Dictionary<string, string> { [source] = "a.mdl", [mat] = "a.mtrl" });
        mod.Put("a.mdl", HairModel(801, material: "/mt_c0801h0105_hir_a.mtrl"));
        mod.Put("a.mtrl", [5]);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Races)
        {
            files[HairPortPlan.HeadPaths(r).First()] = HeadModel(r);
            files[HairPortPlan.HairPath(r, id)] = HairModel(r);
        }
        Func<string, byte[]?> game = p => files.GetValueOrDefault(p);

        // On its own, a Midlander port would put the Miqo'te material on the Highlander's and Lalafell's own 105.
        var alone = HairPortBuilder.Build(mod.Root, source, [201], Pbd(), game, withSharers: false);
        Assert.Empty(alone.Writes);
        Assert.Contains("c0401", alone.Refused[201]);

        // Automatic: every sharer is ported with it, and the shared material points at the author's.
        var all = HairPortBuilder.Build(mod.Root, source, [201], Pbd(), game, withSharers: true);
        Assert.Contains(all.Writes, w => w.TargetGamePath == HairPortPlan.HairPath(201, id));
        var shared = "chara/human/c0201/obj/hair/h0105/material/v0001/mt_c0201h0105_hir_a.mtrl";
        Assert.All(all.Writes, w => Assert.Contains(w.Materials, m => m.GamePath == shared && m.AuthorFile == "a.mtrl"));
        Assert.Empty(all.Refused);
        Assert.Equal(new ushort[] { 201, 401, 601, 1001, 1201, 1401, 1801 },
                     all.Writes.Select(w => HairPortPlan.Parse(w.TargetGamePath)!.Value.Race).OrderBy(r => r));
    }

    /// <summary>
    /// The automatic path never ports a race the user undid — not even as another race's material sharer, which would
    /// take the whole set with it — while a port asked for by hand still may.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-race-port-7")]
    [Trait("Plan", "EDGE-hair-race-port-23")]
    public void A_declined_sharer_refuses_the_set_on_the_automatic_path()
    {
        const int id = 105;
        string source = HairPortPlan.HairPath(801, id);
        using var mod = new Mod(defaults: new Dictionary<string, string> { [source] = "a.mdl" });
        mod.Put("a.mdl", HairModel(801, material: "/mt_c0801h0105_hir_a.mtrl"));
        mod.Put("Proteus/hairport.json", System.Text.Encoding.UTF8.GetBytes(
            "{\"Version\":1,\"Ports\":[],\"Declined\":[\"" + HairPortPlan.DeclineKey(HairPortPlan.HairPath(401, id)) + "\"]}"));
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["chara/human/c0801/obj/hair/h0105/material/v0001/mt_c0801h0105_hir_a.mtrl"] = [5],
        };
        foreach (var r in Races)
        {
            files[HairPortPlan.HeadPaths(r).First()] = HeadModel(r);
            files[HairPortPlan.HairPath(r, id)] = HairModel(r);
        }
        Func<string, byte[]?> game = p => files.GetValueOrDefault(p);

        var auto = HairPortBuilder.Build(mod.Root, source, [201], Pbd(), game, withSharers: true);
        Assert.Empty(auto.Writes);
        Assert.True(auto.Refused.ContainsKey(201));

        var byHand = HairPortBuilder.Build(mod.Root, source, [201, 401, 601, 1001, 1201, 1401, 1801], Pbd(), game,
                                           withSharers: false);
        Assert.Empty(byHand.Refused);
        Assert.Equal(7, byHand.Writes.Count);
    }

    /// <summary>A race the game never gives the hairstyle never reads its material, so it is no sharer.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-20")]
    public void A_race_that_cannot_choose_the_hairstyle_is_no_sharer()
    {
        var names = new[] { "/mt_c0801h0105_hir_a.mtrl" };
        var all = HairPortBuilder.Sharers(801, 201, 105, names).ToList();
        Assert.Contains((ushort)1801, all);
        var some = HairPortBuilder.Sharers(801, 201, 105, names, r => r != 1801).ToList();
        Assert.DoesNotContain((ushort)1801, some);
        Assert.Equal(all.Count - 1, some.Count);
    }

    /// <summary>
    /// Physics hair: the model moves with another hairstyle's extra bones, which an Est entry gives the source race. The
    /// target race has that skeleton of its own, so the port is made, and carries the entry for its race — unless the
    /// author already gave it one.
    /// </summary>
    [Theory]
    [Trait("Plan", "EDGE-hair-race-port-6")]
    [Trait("Plan", "EDGE-hair-race-port-5")]
    [InlineData(false)]
    [InlineData(true)]
    public void A_borrowed_skeleton_comes_with_the_port(bool authorGaveTheTargetOne)
    {
        var est = new List<object> { PenumbraManipulations.EstManipulation("0801", "Hair", Id, 170) };
        if (authorGaveTheTargetOne) est.Add(PenumbraManipulations.EstManipulation("0201", "Hair", Id, 150));
        var groups = "[" + System.Text.Json.JsonSerializer.Serialize(new
        {
            Name = "Cut", Description = "", Priority = 0, Type = "Single", DefaultSettings = 0,
            Options = new[] { new { Name = "Long", Description = "", Files = new Dictionary<string, string> { [Source] = "long.mdl" },
                                    FileSwaps = new Dictionary<string, string>(), Manipulations = est.ToArray() } },
        }) + "]";
        using var mod = new Mod(groups);
        mod.Put("long.mdl", HairModel(801, bones: [("j_ex_h0170_ke_b", 1f)]));
        var skeleton = System.Text.Encoding.ASCII.GetBytes("\0\0hkaSkeleton\0j_ex_h0170_ke_b\0j_ex_h0170_ke_c\0");
        var game = Game(Races, new Dictionary<string, byte[]>
        {
            [HairPortPlan.SkeletonPath(201, 170)] = skeleton,
            [HairPortPlan.SkeletonPath(201, 150)] = skeleton,
        });

        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, withSharers: false);
        Assert.Empty(plan.Refused);
        var write = Assert.Single(plan.Writes);
        Assert.Equal(authorGaveTheTargetOne ? 0 : 170, write.Est);

        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: false).Ok);
        Assert.Equal(authorGaveTheTargetOne ? 150 : 170, HairPortPlan.HairEst(mod.Owner("Cut", "Long"), 201, Id));
        Assert.Equal(170, HairPortPlan.HairEst(mod.Owner("Cut", "Long"), 801, Id));

        // Undone, the entry it added goes with it; the author's own entries stay.
        Assert.True(HairPortWriter.Undo(mod.Root, _ => true, decline: false).Ok);
        Assert.Equal(authorGaveTheTargetOne ? 150 : null, HairPortPlan.HairEst(mod.Owner("Cut", "Long"), 201, Id));
        Assert.Equal(170, HairPortPlan.HairEst(mod.Owner("Cut", "Long"), 801, Id));
    }

    /// <summary>Without a skeleton for the target race, a model on borrowed bones is still refused.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-5")]
    public void A_borrowed_skeleton_the_target_lacks_is_refused()
    {
        var groups = "[" + System.Text.Json.JsonSerializer.Serialize(new
        {
            Name = "Cut", Description = "", Priority = 0, Type = "Single", DefaultSettings = 0,
            Options = new[] { new { Name = "Long", Description = "", Files = new Dictionary<string, string> { [Source] = "long.mdl" },
                                    FileSwaps = new Dictionary<string, string>(),
                                    Manipulations = new[] { PenumbraManipulations.EstManipulation("0801", "Hair", Id, 170) } } },
        }) + "]";
        using var mod = new Mod(groups);
        mod.Put("long.mdl", HairModel(801, bones: [("j_ex_h0170_ke_b", 1f)]));
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), Game(Races), withSharers: false);
        Assert.Empty(plan.Writes);
        Assert.Contains("j_ex_h0170_ke_b", plan.Refused[201]);
    }

    private static readonly byte[] H0170Skeleton =
        System.Text.Encoding.ASCII.GetBytes("\0\0hkaSkeleton\0j_ex_h0170_ke_b\0j_ex_h0170_ke_c\0");

    /// <summary>A mod with one option publishing a Miqo'te physics hairstyle, its manipulations given.</summary>
    private static Mod PhysicsMod(object[] optionManips, object[]? defaultManips = null, string extraOptionJson = "")
    {
        var groups = "[" + System.Text.Json.JsonSerializer.Serialize(new
        {
            Name = "Cut", Description = "", Priority = 0, Type = "Single", DefaultSettings = 0,
            Options = new[] { new { Name = "Long", Description = "", Files = new Dictionary<string, string> { [Source] = "long.mdl" },
                                    FileSwaps = new Dictionary<string, string>(), Manipulations = optionManips } },
        }) + "]";
        var mod = new Mod(groups);
        if (defaultManips != null)
        {
            var meta = Path.Combine(mod.Root, "meta.json");
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(meta))!;
            node["DefaultData"]!["Manipulations"] = System.Text.Json.JsonSerializer.SerializeToNode(defaultManips);
            File.WriteAllText(meta, node.ToJsonString());
        }
        mod.Put("long.mdl", HairModel(801, bones: [("j_ex_h0170_ke_b", 1f)]));
        return mod;
    }

    private static Func<string, byte[]?> PhysicsGame(params (ushort Race, int Skeleton, byte[] Bytes)[] skeletons)
        => Game(Races, skeletons.ToDictionary(s => HairPortPlan.SkeletonPath(s.Race, s.Skeleton), s => s.Bytes));

    /// <summary>
    /// A port remade after its source changed still carries — and records — the skeleton it added: the entry it finds
    /// for its race is its own from before, not the author's, so an undo after the remake still takes it out.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-race-port-8")]
    [Trait("Plan", "EDGE-hair-race-port-6")]
    public void A_remade_port_carries_its_own_skeleton_and_undo_takes_it_out()
    {
        using var mod = PhysicsMod([PenumbraManipulations.EstManipulation("0801", "Hair", Id, 170)]);
        var game = PhysicsGame((201, 170, H0170Skeleton));
        Assert.True(HairPortWriter.Save(mod.Root, HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, false).Writes, false).Ok);

        mod.Put("long.mdl", HairModel(801, material: "/mt_c0801h0130_hir_b.mtrl", bones: [("j_ex_h0170_ke_b", 1f)]));
        var remake = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, withSharers: false);
        Assert.Equal(170, Assert.Single(remake.Writes).Est);
        Assert.True(HairPortWriter.Save(mod.Root, remake.Writes, auto: false).Ok);
        Assert.Equal(170, HairPortRecord.Read(mod.Root).Ports.Single().Est);

        Assert.True(HairPortWriter.Undo(mod.Root, _ => true, decline: false).Ok);
        Assert.Null(HairPortPlan.HairEst(mod.Owner("Cut", "Long"), 201, Id));
        Assert.Equal(170, HairPortPlan.HairEst(mod.Owner("Cut", "Long"), 801, Id));
    }

    /// <summary>
    /// When the author gave the target race a skeleton of their own, the port is checked against THAT one — and refused
    /// when it lacks the bones the model moves with.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-5")]
    public void An_authors_own_skeleton_for_the_target_is_what_the_port_is_checked_against()
    {
        using var mod = PhysicsMod([PenumbraManipulations.EstManipulation("0801", "Hair", Id, 170),
                                    PenumbraManipulations.EstManipulation("0201", "Hair", Id, 150)]);
        var other = System.Text.Encoding.ASCII.GetBytes("\0j_ex_h0150_ke_a\0");
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(),
                                         PhysicsGame((201, 170, H0170Skeleton), (201, 150, other)), withSharers: false);
        Assert.Empty(plan.Writes);
        Assert.Contains("j_ex_h0170_ke_b", plan.Refused[201]);
    }

    /// <summary>A set id written as a string — as mods do, and Penumbra reads — is the same entry.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-6")]
    public void A_skeleton_entry_spelled_with_strings_is_read_and_taken_out()
    {
        var spelled = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            "{\"Type\":\"Est\",\"Manipulation\":{\"Gender\":\"Female\",\"Race\":\"Miqote\",\"SetId\":\"" + Id
            + "\",\"Slot\":\"Hair\",\"Entry\":\"170\"}}");
        using var mod = PhysicsMod([spelled]);
        var game = PhysicsGame((201, 170, H0170Skeleton));
        Assert.Equal(170, HairPortPlan.HairEst(mod.Owner("Cut", "Long"), 801, Id));

        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), game, withSharers: false);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: false).Ok);
        // Penumbra rewrites the entry Proteus added in its own spelling.
        var meta = Path.Combine(mod.Root, "meta.json");
        File.WriteAllText(meta, File.ReadAllText(meta).Replace("\"SetId\": " + Id + ",", "\"SetId\": \"" + Id + "\","));
        Assert.True(HairPortWriter.Undo(mod.Root, _ => true, decline: false).Ok);
        Assert.Null(HairPortPlan.HairEst(mod.Owner("Cut", "Long"), 201, Id));
        Assert.Equal(170, HairPortPlan.HairEst(mod.Owner("Cut", "Long"), 801, Id));
    }

    /// <summary>A skeleton the source race borrows in the default data comes with a port published from an option.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-6")]
    public void A_skeleton_borrowed_in_the_default_data_comes_with_an_options_port()
    {
        using var mod = PhysicsMod([], [PenumbraManipulations.EstManipulation("0801", "Hair", Id, 170)]);
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), PhysicsGame((201, 170, H0170Skeleton)), false);
        Assert.Equal(170, Assert.Single(plan.Writes).Est);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: false).Ok);
        Assert.Equal(170, HairPortPlan.HairEst(mod.Owner("Cut", "Long"), 201, Id));
        Assert.True(HairPortWriter.Undo(mod.Root, _ => true, decline: false).Ok);
        Assert.Null(HairPortPlan.HairEst(mod.Owner("Cut", "Long"), 201, Id));
        Assert.Equal(170, HairPortPlan.HairEst(mod.Owners.First(o => o.IsDefault), 801, Id));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-14")]
    public void An_unreadable_record_refuses_the_build()
    {
        using var mod = Cuts();
        mod.Put("Proteus/hairport.json", System.Text.Encoding.UTF8.GetBytes("{ not json"));
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), Game(Races), withSharers: false);
        Assert.Empty(plan.Writes);
        Assert.Equal(HairPortRecord.UnreadableMessage, plan.Refused[201]);
    }
}
