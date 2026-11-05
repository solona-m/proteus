using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The group the size set reads a piece's sizes from and saves them into (<see cref="SizeSetRefit.SizeGroupOf"/>): the one
/// Penumbra draws the piece from. Equinox gave its legs in "Top Size" (only the Yab and Rue sizes) and in "Bottom Size"
/// (every option); the game always draws Bottom Size's, but the size set read Top Size's six and made no bottoms.
/// </summary>
public class SizeSetSwitchingGroupTests : IDisposable
{
    private const string Top = "chara/equipment/e0317/model/c0201e0317_top.mdl";
    private const string Dwn = "chara/equipment/e0317/model/c0201e0317_dwn.mdl";
    private const string Glv = "chara/equipment/e0408/model/c0201e0408_glv.mdl";

    private readonly string mods = Path.Combine(Path.GetTempPath(), "proteus-sizegroup-" + Guid.NewGuid().ToString("N"));

    public SizeSetSwitchingGroupTests() => Directory.CreateDirectory(mods);

    public void Dispose()
    {
        try { Directory.Delete(mods, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static PenumbraModMeta.FileOption Option(string name, params (string Path, string File)[] files)
        => new(name, files.ToDictionary(f => f.Path, f => f.File, StringComparer.OrdinalIgnoreCase));

    /// <summary>A mod with the given single-choice groups, written in order: (name, priority, options).</summary>
    private string Mod(params (string Name, int Priority, PenumbraModMeta.FileOption[] Options)[] groups)
    {
        string root = Path.Combine(mods, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), PenumbraModMeta.NewMetaJson("Mod", "Author", ""));
        for (int i = 0; i < groups.Length; i++)
            PenumbraModMeta.WriteFileOptionGroup(root, i, groups[i].Name, groups[i].Priority, groups[i].Options, 0);
        return root;
    }

    private static void WriteRecord(string root, BodyRetargetWriter.Record record)
    {
        string dir = Path.Combine(root, SidecarDiscoveryService.SidecarSubdir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, BodyRetargetWriter.RecordFile), JsonSerializer.Serialize(record));
    }

    private static string? GroupOf(string root, string gamePath)
    {
        var record = BodyRetargetWriter.ReadRecord(root);
        return SizeSetRefit.SizeGroupOf(PenumbraModMeta.ReadAllRedirects(root), gamePath, record,
                                        RefitCore.SingleGroups(root), PenumbraModMeta.TryReadRawGroups(root),
                                        SizeSetRefit.OurFiles(record));
    }

    /// <summary>Equinox's shape: Top Size with 19 options, 6 of which (Yab/Rue) also give the legs; Bottom Size with 9,
    /// every one giving the legs.</summary>
    private string Equinox(int topPriority = 0)
    {
        var top = new List<PenumbraModMeta.FileOption>();
        foreach (string s in new[] { "XSmall", "Small", "Medium", "Large", "XLarge", "XSmall Tummy", "Small Tummy",
                                     "Medium Tummy", "Large Tummy", "XLarge Tummy", "Yab Buff Small", "Yab Buff Medium",
                                     "Yab Buff Large" })
            top.Add(Option(s, (Top, $"top/{s}/top.mdl")));
        foreach (string s in new[] { "Yab Small", "Yab Medium", "Yab Large", "Rue Small", "Rue Medium", "Rue Large" })
            top.Add(Option(s, (Top, $"top/{s}/top.mdl"), (Dwn, $"top/{s}/dwn.mdl")));
        var bottom = new[] { "Small", "Medium", "Large", "Small Tummy", "Medium Tummy", "Large Tummy", "Yab WC", "Yab SC", "Rue WC" }
            .Select(s => Option(s, (Dwn, $"bottom/{s}/dwn.mdl"))).ToArray();
        return Mod(("Top Size", topPriority, top.ToArray()), ("Bottom Size", 0, bottom));
    }

    [Fact]
    [Trait("Plan", "AC-equinox-legs-slits-3")]
    [Trait("Plan", "EDGE-equinox-legs-slits-1")]
    public void A_piece_switched_in_every_option_of_the_drawn_group_is_sized_there()
    {
        string root = Equinox();
        var regroups = new List<(string Path, string? Was, string Now, IReadOnlyList<string> Rivals)>();

        var pieces = SizeSetRefit.PiecesOf(root, regrouped: (p, was, now, rivals) => regroups.Add((p, was, now, rivals)));

        var legs = Assert.Single(pieces, p => p.Slot == "_dwn");
        Assert.Equal("Bottom Size", legs.Group);
        Assert.Equal(["Small", "Medium", "Large", "Small Tummy", "Medium Tummy", "Large Tummy", "Yab WC", "Yab SC", "Rue WC"],
                     legs.Options.Select(o => o.Option));
        Assert.False(legs.FollowsChest);
        Assert.Equal("Top Size", Assert.Single(pieces, p => p.Slot == "_top").Group);
        var regroup = Assert.Single(regroups);
        Assert.Equal((Dwn, (string?)"Top Size", "Bottom Size"), (regroup.Path, regroup.Was, regroup.Now));
        Assert.Empty(regroup.Rivals);   // Bottom Size is applied after Top Size's options at the tie: nothing outranks it
    }

    [Fact]
    [Trait("Plan", "AC-equinox-legs-slits-3")]
    [Trait("Plan", "EDGE-equinox-legs-slits-2")]
    public void Of_two_every_option_groups_the_later_wins_a_tie()
    {
        // Precious: "Default" (one option) and "Size" (14), both priority 0, both giving the top in every option.
        string root = Mod(("Default", 0, [Option("Install", (Top, "default/top.mdl"))]),
                          ("Size", 0, Enumerable.Range(0, 14).Select(i => Option($"S{i}", (Top, $"size/{i}.mdl"))).ToArray()));

        Assert.Equal("Default", RefitCore.SwitchingGroup(PenumbraModMeta.ReadAllRedirects(root), Top, null, RefitCore.SingleGroups(root)));
        Assert.Equal("Size", GroupOf(root, Top));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-19")]
    public void Priority_beats_position_among_every_option_groups()
    {
        string earlierHigher = Mod(("A", 2, [Option("a", (Top, "a.mdl"))]), ("B", 0, [Option("b", (Top, "b.mdl"))]));
        string laterHigher = Mod(("A", 0, [Option("a", (Top, "a.mdl"))]), ("B", 1, [Option("b", (Top, "b.mdl"))]));
        string negative = Mod(("A", -1, [Option("a", (Top, "a.mdl"))]), ("B", -2, [Option("b", (Top, "b.mdl"))]));

        Assert.Equal("A", GroupOf(earlierHigher, Top));
        Assert.Equal("B", GroupOf(laterHigher, Top));
        Assert.Equal("A", GroupOf(negative, Top));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-3")]
    public void No_every_option_group_keeps_todays_group()
    {
        string root = Mod(("A", 0, [Option("a1", (Top, "a1.mdl")), Option("a2", (Top, "a2.mdl")), Option("a3")]),
                          ("B", 0, [Option("b1", (Top, "b1.mdl")), Option("b2")]));

        Assert.Equal("A", GroupOf(root, Top));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-4")]
    public void Our_options_and_files_are_not_counted()
    {
        // "A" gives the top in its author's options; an option of ours there gives only a top too — and a legs file of
        // ours sits in an author's option of "A". Our own group "Ours" is later and every-option.
        string root = Mod(("A", 0, [Option("a1", (Dwn, "a1.mdl")), Option("a2", (Dwn, "a2.mdl")), Option("Neolithe S", (Top, "body retarget/t.mdl"))]),
                          ("B", 0, [Option("b1", (Dwn, "b1.mdl")), Option("b2")]),
                          ("Ours", 5, [Option("x", (Dwn, "body retarget/x.mdl"))]));
        WriteRecord(root, new BodyRetargetWriter.Record
        {
            Options =
            [
                new BodyRetargetWriter.Entry { Group = "A", InAuthorGroup = true, Name = "Neolithe S", GamePath = Top, File = "body retarget/t.mdl" },
                new BodyRetargetWriter.Entry { Group = "Ours", Name = "x", GamePath = Dwn, File = "body retarget/x.mdl" },
            ],
        });

        // "A" is every-option once our "Neolithe S" is left out; "Ours" is never a candidate.
        Assert.Equal("A", GroupOf(root, Dwn));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-5")]
    public void Paths_compare_normalised_and_empty_options_do_not_switch()
    {
        string upper = Top.ToUpperInvariant().Replace('/', '\\');
        string root = Mod(("A", 0, [Option("a", (Top, "a.mdl"))]),
                          ("B", 0, [Option("b1", (upper, "b1.mdl")), Option("b2", (Top, "b2.mdl"))]),
                          ("C", 0, [Option("c1", (Top, "c1.mdl")), Option("None")]));

        // B switches it in both options (one written in upper case with backslashes); C's fileless "None" makes it partial.
        Assert.Equal("B", GroupOf(root, Top));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-6")]
    public void One_or_no_switching_group_is_unchanged()
    {
        string one = Mod(("A", 0, [Option("a1", (Top, "a1.mdl")), Option("a2")]), ("B", 3, [Option("b", (Dwn, "b.mdl"))]));
        string none = Mod(("B", 0, [Option("b", (Dwn, "b.mdl"))]));

        Assert.Equal(RefitCore.SwitchingGroup(PenumbraModMeta.ReadAllRedirects(one), Top, null, RefitCore.SingleGroups(one)),
                     GroupOf(one, Top));
        Assert.Null(GroupOf(none, Top));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-7")]
    public void A_v3_or_unreadable_mod_is_unchanged()
    {
        string v3 = Path.Combine(mods, "v3");
        Directory.CreateDirectory(v3);
        File.WriteAllText(Path.Combine(v3, PenumbraModMeta.MetaFile), "{\"FileVersion\":3,\"Name\":\"Old\"}");
        File.WriteAllText(Path.Combine(v3, "default_mod.json"), "{\"Files\":{\"" + Top + "\":\"top.mdl\"}}");
        string broken = Path.Combine(mods, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, PenumbraModMeta.MetaFile), "{ not json");

        foreach (string root in new[] { v3, broken })
        {
            var redirects = PenumbraModMeta.ReadAllRedirects(root);
            Assert.Equal(RefitCore.SwitchingGroup(redirects, Top, null, RefitCore.SingleGroups(root)), GroupOf(root, Top));
            Assert.All(SizeSetRefit.PiecesOf(root), p => Assert.Null(p.Group));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-24")]
    public void Today_chosen_only_by_our_entry_is_replaced_by_an_author_candidate()
    {
        var ours = new BodyRetargetWriter.Record
        {
            Options = [new BodyRetargetWriter.Entry { Group = "A", InAuthorGroup = true, Name = "a1", GamePath = Dwn, File = "body retarget/a1.mdl" }],
        };
        // "A" gives the legs only through our file: one partial author candidate "B" takes them.
        string one = Mod(("A", 0, [Option("a1", (Dwn, "body retarget/a1.mdl")), Option("a2", (Top, "a2.mdl"))]),
                         ("B", 0, [Option("b1", (Dwn, "b1.mdl")), Option("b2")]));
        WriteRecord(one, ours);
        // Two partial author candidates: the one Penumbra applies first (the later, at a tie).
        string two = Mod(("A", 0, [Option("a1", (Dwn, "body retarget/a1.mdl")), Option("a2", (Top, "a2.mdl"))]),
                         ("B", 0, [Option("b1", (Dwn, "b1.mdl")), Option("b2")]),
                         ("C", 0, [Option("c1", (Dwn, "c1.mdl")), Option("c2")]));
        WriteRecord(two, ours);
        // No author candidate: today's stands.
        string none = Mod(("A", 0, [Option("a1", (Dwn, "body retarget/a1.mdl")), Option("a2", (Top, "a2.mdl"))]));
        WriteRecord(none, ours);

        Assert.Equal("B", GroupOf(one, Dwn));
        Assert.Equal("C", GroupOf(two, Dwn));
        Assert.Equal("A", GroupOf(none, Dwn));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-22")]
    public void An_end_moves_to_the_always_on_group()
    {
        // Cowgirls and Assassins: the glove in "Fabric" (1 of 2) and "Required" (its one option, priority 2).
        string root = Mod(("Fabric", 0, [Option("Leather", (Glv, "leather/glv.mdl")), Option("Silk")]),
                          ("Required", 2, [Option("Install", (Glv, "req/glv.mdl"))]));

        Assert.Equal("Required", GroupOf(root, Glv));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-25")]
    public void A_style_switch_that_carries_the_model_in_every_option_takes_the_sizes()
    {
        // Codeine: "Base Files" (one 18-file option) and "✦ HARNESS" (On/Off, both giving the top).
        string root = Mod(("Base Files", 0, [Option("Install", (Top, "base/top.mdl"))]),
                          ("✦ HARNESS", 0, [Option("On", (Top, "on/top.mdl")), Option("Off", (Top, "off/top.mdl"))]));

        Assert.Equal("✦ HARNESS", GroupOf(root, Top));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-26")]
    public void Raw_positions_and_duplicate_names_rank_as_Penumbra_reads_them()
    {
        string root = Mod(("A", 0, [Option("a", (Top, "a.mdl"))]), ("B", 0, [Option("b", (Top, "b.mdl"))]));
        // A nameless group first, and a second "A" last: positions count the nameless one, the duplicate takes the first.
        string meta = Path.Combine(root, PenumbraModMeta.MetaFile);
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(meta))!;
        var groups = doc["Groups"]!.AsArray();
        groups.Insert(0, new System.Text.Json.Nodes.JsonObject { ["Type"] = "Single", ["Priority"] = 9, ["Options"] = new System.Text.Json.Nodes.JsonArray() });
        var duplicate = groups[1]!.DeepClone();
        groups.Add(duplicate);
        File.WriteAllText(meta, doc.ToJsonString());

        // B (raw position 2) still comes after the first A (raw position 1): B wins the tie.
        Assert.Equal("B", GroupOf(root, Top));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-28")]
    public void A_partial_group_applied_first_is_named_as_a_rival()
    {
        // "Style" (priority 5) gives the top in one option of two; "Size" (priority 0) in every option.
        string root = Mod(("Style", 5, [Option("Cropped", (Top, "style/top.mdl")), Option("Plain")]),
                          ("Size", 0, [Option("S", (Top, "s/top.mdl")), Option("M", (Top, "m/top.mdl"))]));
        var regroups = new List<IReadOnlyList<string>>();

        var pieces = SizeSetRefit.PiecesOf(root, regrouped: (_, _, _, rivals) => regroups.Add(rivals));

        // The every-option group is still chosen — "every option" is not "always drawn" — and the run names what wins
        // the path over it while on, just as the Studio's warning reads it.
        Assert.Equal("Size", Assert.Single(pieces).Group);
        var rivals = Assert.Single(regroups);
        Assert.Contains("Style", rivals);
        Assert.Equal(BodyRetargetWriter.Outranking(PenumbraModMeta.TryReadRawGroups(root), Top, "Size", null)
                                       .Select(r => r.Group), rivals);
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-27")]
    public void The_studio_and_auto_refit_still_take_the_first_switching_group()
    {
        // A known issue, not yet moved over: the Studio and the automatic refit save where SwitchingGroup says.
        string root = Equinox();

        Assert.Equal("Top Size", RefitCore.SwitchingGroup(PenumbraModMeta.ReadAllRedirects(root), Dwn, null, RefitCore.SingleGroups(root)));
        Assert.Equal("Bottom Size", GroupOf(root, Dwn));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-8")]
    public void Earlier_refits_in_the_losing_group_are_left_alone()
    {
        string root = Equinox();
        // An earlier refit of ours: a legs file inside an option we added to Top Size.
        string meta = Path.Combine(root, PenumbraModMeta.MetaFile);
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(meta))!;
        var topSize = doc["Groups"]!.AsArray().First(g => (string?)g!["Name"] == "Top Size")!;
        topSize["Options"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["Name"] = "Neolithe S",
            ["Files"] = new System.Text.Json.Nodes.JsonObject
            {
                [Top] = "body retarget/neo s/top.mdl",
                [Dwn] = "body retarget/neo s/dwn.mdl",
            },
        });
        File.WriteAllText(meta, doc.ToJsonString());
        WriteRecord(root, new BodyRetargetWriter.Record
        {
            Options =
            [
                new BodyRetargetWriter.Entry { Group = "Top Size", InAuthorGroup = true, Name = "Neolithe S", GamePath = Top, File = "body retarget/neo s/top.mdl" },
                new BodyRetargetWriter.Entry { Group = "Top Size", InAuthorGroup = true, Name = "Neolithe S", GamePath = Dwn, File = "body retarget/neo s/dwn.mdl" },
            ],
        });
        string before = File.ReadAllText(meta);

        var legs = Assert.Single(SizeSetRefit.PiecesOf(root), p => p.Slot == "_dwn");

        Assert.Equal("Bottom Size", legs.Group);
        Assert.DoesNotContain(legs.Options, o => o.File.Contains("body retarget", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before, File.ReadAllText(meta));   // reading never touches the author's groups or our old entries
    }
}
