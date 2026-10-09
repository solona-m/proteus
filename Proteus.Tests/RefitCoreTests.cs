using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Redirect = Proteus.Services.PenumbraModMeta.Redirect;

namespace Proteus.Tests;

/// <summary>
/// What a Body size refit IS, shared between the Studio panel and the automatic refit: what its option is called,
/// which group it joins, and when a garment is baked for another race first.
/// </summary>
public class RefitCoreTests
{
    private const string Top = "chara/equipment/e6255/model/c0201e6255_top.mdl";

    [Fact]
    public void An_option_names_the_body_and_every_size_in_slot_order()
    {
        // Golden strings: these are option names already saved into people's mods, which the automatic refit has to
        // find again to switch a refit back on rather than making a second one.
        Assert.Equal("Rue — M", RefitCore.OptionName("Rue", ["M"]));
        Assert.Equal("Rue — M + Default · L", RefitCore.OptionName("Rue", ["M", "Default · L"]));
        Assert.Equal("S + M", RefitCore.LabelFrom(null, ["S", "M"]));
        Assert.Equal("Neolithe — S + M", RefitCore.LabelFrom("Neolithe", ["S", "M"]));
    }

    [Fact]
    public void A_new_size_joins_the_authors_size_group()
    {
        var redirects = new List<Redirect>
        {
            new(Top, "default/top.mdl", ""),                         // the default files: no group
            new(Top, "multi/top.mdl", "Extras / Lace"),              // a multi-select group
            new(Top, "size/m.mdl", "Size / M"),
            new("chara/equipment/e6255/model/c0201e6255_dwn.mdl", "dwn.mdl", "Legs / M"),
        };

        Assert.Equal("Size", RefitCore.SwitchingGroup(redirects, Top, null, ["Size", "Legs"]));
        // A group a refit made is never the author's size group.
        Assert.Null(RefitCore.SwitchingGroup(redirects, Top, ["size"], ["Size", "Legs"]));
        // Only single-choice groups take a size.
        Assert.Null(RefitCore.SwitchingGroup(redirects, Top, null, ["Legs"]));
    }

    private const string Skirt = "chara/equipment/e0041/model/c0201e0041_dwn.mdl";

    [Fact]
    public void A_piece_ticked_in_a_multi_choice_list_gets_its_new_size_in_that_list()
    {
        // Farfalla, as it was: the skirt's model lives in the "Items" list beside the choker and the jacket, and no size
        // group switches it. The new size belongs beside "Skirt", not in a group of its own.
        var redirects = new List<Redirect>
        {
            new(Skirt, @"items\skirt\chara\equipment\e0041\model\c0201e0041_dwn.mdl", "Items / Skirt"),
            new("chara/equipment/e0041/model/c0201e0041_top.mdl", @"items\shirt\top.mdl", "Items / Shirt"),
        };
        const string rel = "items/skirt/chara/equipment/e0041/model/c0201e0041_dwn.mdl";

        Assert.Equal("Items", RefitCore.SwitchingGroup(redirects, Skirt, null, [], rel, ["Items"]));
        // A size group still comes first.
        var sized = redirects.Append(new(Skirt, @"size\m.mdl", "Skirt Size / M")).ToList();
        Assert.Equal("Skirt Size", RefitCore.SwitchingGroup(sized, Skirt, null, ["Skirt Size"], rel, ["Items"]));
        // A list with no room left (Penumbra's 32), or a group a refit made, gets a group of its own.
        Assert.Null(RefitCore.SwitchingGroup(redirects, Skirt, null, [], rel, []));
        Assert.Null(RefitCore.SwitchingGroup(redirects, Skirt, ["Items"], [], rel, ["Items"]));
        // Only the list the drawn file is ticked in: another model of the same path elsewhere says nothing.
        Assert.Null(RefitCore.SwitchingGroup(redirects, Skirt, null, [], "other/skirt.mdl", ["Items"]));
    }

    [Fact]
    public void Wearing_a_size_in_a_multi_choice_list_swaps_it_for_the_piece_and_keeps_the_rest()
    {
        const string size = "Neolithe — S";

        Assert.Equal(["Choker", "Jacket", size],
                     RefitCore.SelectionFor(true, size, ["Choker", "Skirt", "Jacket"], cutFrom: "skirt"));
        Assert.Equal([size], RefitCore.SelectionFor(true, size, null, "Skirt"));
        // Picked again: once, not twice.
        Assert.Equal(["Choker", size], RefitCore.SelectionFor(true, size, ["Choker", size], "Skirt"));
        // A single-choice group holds one answer.
        Assert.Equal([size], RefitCore.SelectionFor(false, size, ["M"], "M"));
    }

    [Fact]
    public void A_mans_garment_is_baked_for_a_woman_unless_the_author_made_hers()
    {
        const string mans = "chara/equipment/e6255/model/c0101e6255_top.mdl";
        static bool Bodies(ushort code) => code == 201;

        Assert.Equal(201, RefitCore.BakeTarget(mans, 201, Bodies, [], null));

        // The author ships the woman's model: hers stands, nothing is baked over it.
        var hers = new List<Redirect> { new(Top, "top/f.mdl", "Size / M") };
        Assert.Equal(0, RefitCore.BakeTarget(mans, 201, Bodies, hers, null));

        // ...unless that model at her path is one a refit saved — then it is not the author's.
        var record = new BodyRetargetWriter.Record();
        record.Options.Add(new BodyRetargetWriter.Entry { File = "top/f.mdl", GamePath = Top });
        Assert.Equal(201, RefitCore.BakeTarget(mans, 201, Bodies, hers, record));

        // Already the wearer's own race: nothing to bake.
        Assert.Equal(0, RefitCore.BakeTarget(Top, 201, Bodies, [], null));
    }

    [Fact]
    public void The_races_a_body_mod_covers()
    {
        var catalog = new BodySizeCatalog("x", [
            new BodyOption("Chest", "", "M", "m.mdl", "chara/equipment/e0000/model/c0201e0000_top.mdl", "_top"),
            new BodyOption("Chest", "", "M", "v.mdl", "chara/equipment/e0000/model/c1801e0000_top.mdl", "_top"),
        ]);
        Assert.Equal(new HashSet<string> { "0201", "1801" }, RefitCore.RacesOf(catalog));
        Assert.Empty(RefitCore.RacesOf(null));
    }
}

/// <summary>The installed body mods, read once and again only when a manifest changes.</summary>
public class BodyModIndexTests : IDisposable
{
    private readonly string modsRoot = Directory.CreateTempSubdirectory("proteus-body-index").FullName;

    public void Dispose()
    {
        try { Directory.Delete(modsRoot, recursive: true); } catch { /* a test folder, not worth failing over */ }
        GC.SuppressFinalize(this);
    }

    private void WriteMod(string dir, string defaultFiles)
    {
        string root = Path.Combine(modsRoot, dir);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "meta.json"), $$"""
            {
              "FileVersion": 4,
              "Name": "{{dir}}",
              "Author": "somebody",
              "Version": "1.0",
              "DefaultData": { "Files": {{{defaultFiles}}}, "Manipulations": [] },
              "Groups": []
            }
            """);
    }

    private const string Body = "\"chara/equipment/e0000/model/c0201e0000_top.mdl\": \"body/top.mdl\"";

    [Fact]
    public void Only_body_mods_are_listed_by_name()
    {
        WriteMod("rue", Body);
        WriteMod("coat", "\"chara/equipment/e6255/model/c0201e6255_top.mdl\": \"coat.mdl\"");
        var index = new BodyModIndex();

        var bodies = index.Refresh(new Dictionary<string, string> { ["rue"] = "Rue", ["coat"] = "A Coat" }, modsRoot);

        var only = Assert.Single(bodies);
        Assert.Equal("Rue", only.Name);
        Assert.Same(only, index.Find("RUE"));
        Assert.Null(index.Find("coat"));
    }

    [Fact]
    public void A_mod_is_read_again_when_its_manifest_changes_and_forgotten_when_it_goes()
    {
        WriteMod("rue", "\"chara/equipment/e6255/model/c0201e6255_top.mdl\": \"coat.mdl\"");
        var index = new BodyModIndex();
        var mods = new Dictionary<string, string> { ["rue"] = "Rue" };
        Assert.Empty(index.Refresh(mods, modsRoot));

        // Now it ships a body — a different length, so the fingerprint moves whatever the clock does.
        WriteMod("rue", Body + ", \"chara/equipment/e0000/model/c0201e0000_dwn.mdl\": \"body/dwn.mdl\"");
        Assert.Single(index.Refresh(mods, modsRoot));

        Assert.Empty(index.Refresh(new Dictionary<string, string>(), modsRoot));
        Assert.Null(index.Find("rue"));
    }

    [Fact]
    public void A_rename_in_Penumbra_is_picked_up_without_a_reread()
    {
        WriteMod("rue", Body);
        var index = new BodyModIndex();
        index.Refresh(new Dictionary<string, string> { ["rue"] = "Rue" }, modsRoot);

        var renamed = index.Refresh(new Dictionary<string, string> { ["rue"] = "Rue+ 2.0" }, modsRoot);
        Assert.Equal("Rue+ 2.0", Assert.Single(renamed).Name);
    }
}
