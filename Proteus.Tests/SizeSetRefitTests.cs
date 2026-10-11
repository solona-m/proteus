using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// "Every size for other bodies": the body table carried over from the mod packer, and a whole mod given its sizes.
/// </summary>
public class SizeSetRefitTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("extra chest - smallclothes/sfw almond xs.mdl", "extra chest - smallclothes/sfw almond {0}.mdl", false)]
    [InlineData("default legs - smallclothes\\sfw small.mdl", "default legs - smallclothes/sfw {0}.mdl", true)]
    [InlineData("files/common/17/c0201e0000_top.mdl", null, false)]
    [InlineData("chest/nsfw almond.mdl", null, false)]
    public void Template_reads_the_size_off_the_end_of_the_name(string rel, string? pattern, bool word)
    {
        var template = SupportedBodies.Template(rel);
        Assert.Equal(pattern, template?.Pattern);
        if (pattern != null) Assert.Equal(word, template!.Value.Word);
    }

    [Fact]
    public void A_garment_made_on_Neolithe_gets_its_own_family_s_sizes()
    {
        var neolithe = SupportedBodies.Of("neolithe", "_top", false)!;
        var sizes = SupportedBodies.SizesFor(neolithe, "extra chest - smallclothes/sfw almond xs.mdl");
        Assert.Equal(["XS", "S", "M", "L"], sizes.Select(s => s.Name));
        Assert.Equal("extra chest - smallclothes/sfw almond m.mdl", sizes[2].Rel);

        // Made somewhere else, it gets the table's own.
        Assert.Equal("default chest - smallclothes/nsfw m.mdl",
                     SupportedBodies.SizesFor(neolithe, null).Single(s => s.Name == "M").Rel);
    }

    [Fact]
    public void A_body_with_no_family_keeps_the_table_s_sizes()
    {
        var rue = SupportedBodies.Of("rue", "_top", false)!;
        Assert.Same(rue.Sizes, SupportedBodies.SizesFor(rue, "files/chest - smallclothes/small/chara/x.mdl"));
    }

    [Theory]
    [InlineData("default legs - smallclothes/sfw small.mdl", "L", "default legs - smallclothes/sfw large.mdl")]
    [InlineData("default legs - smallclothes/sfw small.mdl", "XS", null)]
    [InlineData("files/common/28/c0201e0000_dwn.mdl", "M", null)]
    public void Family_legs_move_along_their_size_word(string legs, string size, string? want)
        => Assert.Equal(want, SupportedBodies.FamilyLegs(legs, size));

    [Fact]
    public void Every_choice_has_sizes_for_a_chest()
    {
        foreach (var (key, _, male) in SupportedBodies.Choices)
            Assert.Equal(male ? "0101" : "0201", SupportedBodies.Of(key, "_top", false)!.Race);
    }

    /// <summary>A mod whose one "Size" group switches a top and its legs, and whose "Items" group switches a hat-less
    /// skirt with no size group: written by hand, models absent (only the manifest is read).</summary>
    private static string OutfitMod()
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), """
            { "FileVersion": 4, "Name": "Outfit",
              "DefaultData": { "Files": {}, "FileSwaps": {}, "Manipulations": [] },
              "Groups": [
                { "Version": 0, "Name": "Size", "Type": "Single", "Priority": 0, "DefaultSettings": 1, "Options": [
                  { "Name": "S", "Files": { "chara/equipment/e6000/model/c0201e6000_dwn.mdl": "s\\dwn.mdl",
                                            "chara/equipment/e6000/model/c0201e6000_top.mdl": "s\\top.mdl" } },
                  { "Name": "M", "Files": { "chara/equipment/e6000/model/c0201e6000_top.mdl": "m\\top.mdl",
                                            "chara/equipment/e6000/model/c0201e6000_dwn.mdl": "m\\dwn.mdl" } } ] },
                { "Version": 0, "Name": "Items", "Type": "Multi", "Priority": 0, "DefaultSettings": 1, "Options": [
                  { "Name": "Skirt", "Files": { "chara/equipment/e6001/model/c0201e6001_dwn.mdl": "skirt.mdl" } } ] } ] }
            """);
        return root;
    }

    [Fact]
    public void Legs_sized_with_their_chest_follow_it_and_come_after_it()
    {
        string root = OutfitMod();
        try
        {
            var pieces = SizeSetRefit.PiecesOf(root);
            Assert.Equal(["_top", "_dwn", "_dwn"], pieces.Select(p => p.Slot));

            var top = pieces[0];
            Assert.Equal("Size", top.Group);
            Assert.Equal(["S", "M"], top.Options.Select(o => o.Option));
            Assert.False(top.FollowsChest);

            var legs = pieces.Single(p => p.Slot == "_dwn" && p.GamePath.Contains("e6000"));
            Assert.True(legs.FollowsChest);

            // Only in a Multi group: a group of its own, named after the author's option, and no chest to follow.
            var skirt = pieces.Single(p => p.GamePath.Contains("e6001"));
            Assert.Null(skirt.Group);
            Assert.False(skirt.FollowsChest);
            Assert.Equal(string.Format(Proteus.Localization.Strings.Parts.RetargetGroupFmt, "Items / Skirt"), skirt.GroupName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_single_group_s_default_is_read_and_put_back_by_name()
    {
        string root = OutfitMod();
        try
        {
            Assert.Equal("M", PenumbraModMeta.SingleDefault(root, "Size"));
            PenumbraModMeta.SetSingleDefault(root, "Size", "S");
            Assert.Equal("S", PenumbraModMeta.SingleDefault(root, "Size"));
            PenumbraModMeta.SetSingleDefault(root, "Size", "No such option");
            Assert.Equal("S", PenumbraModMeta.SingleDefault(root, "Size"));   // index 0
            Assert.Null(PenumbraModMeta.SingleDefault(root, "No such group"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_body_of_one_size_is_named_alone()
    {
        var tbse = SupportedBodies.Of("tbse", "_top", false)!;
        Assert.Equal("TBSE", SupportedBodies.OptionName(tbse, tbse.Sizes[0]));
        var rue = SupportedBodies.Of("rue", "_top", false)!;
        Assert.Equal("Rue S", SupportedBodies.OptionName(rue, rue.Sizes[0]));
    }

    private const string TheBodySe = LocalData.Mods + ":The Body SE";
    private const string TheBodySeX = LocalData.Mods + ":The Body SE-X IVCS2 V1_1_0";
    private const string NeolitheYas = LocalData.Mods + ":Neolithe YAS AIO";

    /// <summary>
    /// Files alone cannot tell Rue+ from Yet Another Body+ (the same paths), nor TBSE from a mod sharing its plain
    /// <c>common/1</c> chest path, nor Neolithe from its YAS release: each body goes to its own mod.
    /// </summary>
    [LocalDataFact(Neolithe, NeolitheYas, Rue, Yab, TheBodySe, TheBodySeX)]
    public void Each_body_is_found_in_its_own_mod()
    {
        string modsRoot = LocalData.Path(LocalData.Mods);
        var mods = new[] { Neolithe, NeolitheYas, Rue, Yab, TheBodySe, TheBodySeX }
                   .Select(n => Path.GetFileName(LocalData.Path(n))).ToDictionary(d => d, d => d);
        var bodies = new BodyModIndex().RefreshState(mods, modsRoot).List;

        string? Dir(string key, bool yas = false) => SupportedBodies.Find(SupportedBodies.Of(key, "_top", yas)!, bodies)?.Dir;
        Assert.Equal(Path.GetFileName(LocalData.Path(Rue)), Dir("rue"));
        Assert.Equal(Path.GetFileName(LocalData.Path(Rue)), Dir("rue", yas: true));
        Assert.Equal(Path.GetFileName(LocalData.Path(Yab)), Dir("yab"));
        Assert.Equal(Path.GetFileName(LocalData.Path(Neolithe)), Dir("neolithe"));
        Assert.Equal(Path.GetFileName(LocalData.Path(TheBodySe)), Dir("tbse"));
        Assert.Equal(Path.GetFileName(LocalData.Path(TheBodySeX)), Dir("tbse-x"));
    }

    [Fact]
    public void Yas_is_read_off_the_bones()
    {
        Assert.True(SupportedBodies.IsYas(["j_kosi", "iv_kougan_l"]));
        Assert.False(SupportedBodies.IsYas(["j_kosi", "j_ya_a_l"]));
    }

    private const string ThisOldThing = LocalData.Mods + ":This Old Thing - by Solona";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";

    /// <summary>
    /// This Old Thing ships Neolithe XS to L and its own Rue M and L. Asked for Neolithe, Rue and Bibo, it gains Bibo
    /// S, M and L and Rue S — and no second Neolithe or Rue M/L, which it already has.
    /// </summary>
    [LocalDataFact(ThisOldThing, Neolithe, Bibo, Rue, Yab)]
    public void A_whole_mod_gains_the_sizes_it_lacks()
    {
        string copy = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyModels(LocalData.Path(ThisOldThing), copy);
            string modsRoot = LocalData.Path(LocalData.Mods);
            var mods = new[] { Neolithe, Bibo, Rue, Yab }.Select(n => Path.GetFileName(LocalData.Path(n)))
                                                       .ToDictionary(d => d, d => d);
            var bodies = new BodyModIndex().RefreshState(mods, modsRoot).List;

            var before = Options(copy);
            var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
            var result = SizeSetRefit.Run(
                new SizeSetRefit.Request(copy, ["neolithe", "rue", "bibo"], Yas: false, Remake: false, bodies),
                new UVRemapService(log, "."), log, p => output.WriteLine(p), CancellationToken.None);
            foreach (string note in result.Notes) output.WriteLine("note: " + note);

            var added = Options(copy).Except(before).ToList();
            output.WriteLine("added: " + string.Join(", ", added));
            // In the order the bodies were asked for.
            Assert.Equal(["Rue S", "Bibo S", "Bibo M", "Bibo L"], added);
            Assert.Equal(4, result.Made);

            // A second run finds nothing left to make.
            var again = SizeSetRefit.Run(
                new SizeSetRefit.Request(copy, ["neolithe", "rue", "bibo"], Yas: false, Remake: false, bodies),
                new UVRemapService(log, "."), log, _ => { }, CancellationToken.None);
            Assert.Equal(0, again.Made);
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    private const string CasualFitShirt = LocalData.Mods + ":raykie's casual fit shirt (tbse+)";

    /// <summary>
    /// A man's shirt, its sizes in a "Body Fit" group of TBSE variants, gains a TBSE-X size; its one woman's model (a
    /// Neolithe option in the same group) is left to the women's bodies, none of which is asked for.
    /// </summary>
    [LocalDataFact(CasualFitShirt, TheBodySe, TheBodySeX, Neolithe)]
    public void A_man_s_garment_gains_the_men_s_bodies()
    {
        string copy = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyModels(LocalData.Path(CasualFitShirt), copy);
            string modsRoot = LocalData.Path(LocalData.Mods);
            var mods = new[] { TheBodySe, TheBodySeX, Neolithe }.Select(n => Path.GetFileName(LocalData.Path(n)))
                                                                 .ToDictionary(d => d, d => d);
            var bodies = new BodyModIndex().RefreshState(mods, modsRoot).List;

            var before = Options(copy, "Body Fit");
            var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
            var request = new SizeSetRefit.Request(copy, ["tbse", "tbse-x"], Yas: false, Remake: false, bodies);
            var result = SizeSetRefit.Run(request, new UVRemapService(log, "."), log, p => output.WriteLine(p),
                                          CancellationToken.None);
            foreach (string note in result.Notes) output.WriteLine("note: " + note);
            var added = Options(copy, "Body Fit").Except(before).ToList();
            output.WriteLine("added: " + string.Join(", ", added));

            Assert.NotEmpty(added);
            Assert.All(added, a => Assert.Contains(a, new[] { "TBSE", "TBSE-X" }));
            Assert.Equal(added.Count, result.Made);
            Assert.Equal(0, SizeSetRefit.Run(request, new UVRemapService(log, "."), log, _ => { }, CancellationToken.None).Made);
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    /// <summary>A group's options, in order.</summary>
    private static List<string> Options(string modRoot, string group = "Size")
        => (PenumbraModMeta.TryReadFileOptions(modRoot, group) ?? []).Select(o => o.Name).ToList();

    /// <summary>A mod's manifests and models — what a refit reads and writes — without its textures.</summary>
    private static void CopyModels(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, file);
            bool manifest = !rel.Contains(Path.DirectorySeparatorChar) && rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            bool record = rel.StartsWith("Proteus" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!manifest && !record && !rel.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
