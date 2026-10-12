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
[Collection(GameBodyCollection.Name)]
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

    private const string HellForLeather = LocalData.Mods + ":Hell for Leather - by Solona";

    /// <summary>
    /// Hell for Leather is a women's outfit whose "Items" group also gives Midlander men (and three other races) the
    /// game's own Anemos gambison and chaps, re-saved by TexTools. Those are not pieces: the mod is a women's one, and
    /// TBSE is not offered for it.
    /// </summary>
    [LocalDataFact(LocalData.GameData, HellForLeather)]
    public void The_game_s_own_models_a_mod_ships_are_not_its_pieces()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        string root = LocalData.Path(HellForLeather);

        Assert.Contains("0101", SizeSetRefit.PiecesOf(root).Select(p => p.Race));    // without the game, they look like men's
        // Shoes and gloves left out: the gambison and the chaps, as before.
        var bodyPieces = SizeSetRefit.PiecesOf(root, path => data.GetFile(path)?.Data, includeEnds: false);
        Assert.Equal(["0201"], bodyPieces.Select(p => p.Race).Distinct());
        Assert.Equal(2, bodyPieces.Count);

        // With them: the women's boots and gloves too. Of the men's Anemos boots and gloves, the boots are the game's
        // own, re-saved, and are not a piece; the gloves are not the game's (820 vertices to its 830) — the author's own
        // edit — and are.
        var pieces = SizeSetRefit.PiecesOf(root, path => data.GetFile(path)?.Data);
        foreach (var p in pieces) output.WriteLine($"{p.GamePath} [{p.Group}] {string.Join(", ", p.Options.Select(o => o.File))}");
        Assert.Equal(["c0201e0287_top", "c0201e0488_dwn", "c0101e0380_glv", "c0201e0380_glv", "c0201e0380_sho"],
                     pieces.Select(p => Path.GetFileNameWithoutExtension(p.GamePath)));   // [EDGE-keep-ends-studio-21]
    }

    // ── shoes and gloves (keep-ends-studio) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A mod written by hand: an outfit's chest, legs, shoe and glove in one "Size" group; legs and a shoe alone in
    /// "Bottoms"; a shoe and gloves each in a group of their own. Models absent: only the manifest is read.
    /// </summary>
    private static string EndsMod()
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), """
            { "FileVersion": 4, "Name": "Ends",
              "DefaultData": { "Files": {}, "FileSwaps": {}, "Manipulations": [] },
              "Groups": [
                { "Version": 0, "Name": "Size", "Type": "Single", "Priority": 0, "DefaultSettings": 0, "Options": [
                  { "Name": "S", "Files": { "chara/equipment/e6000/model/c0201e6000_glv.mdl": "s\\glv.mdl",
                                            "chara/equipment/e6000/model/c0201e6000_sho.mdl": "s\\sho.mdl",
                                            "chara/equipment/e6000/model/c0201e6000_dwn.mdl": "s\\dwn.mdl",
                                            "chara/equipment/e6000/model/c0201e6000_top.mdl": "s\\top.mdl" } } ] },
                { "Version": 0, "Name": "Bottoms", "Type": "Single", "Priority": 0, "DefaultSettings": 0, "Options": [
                  { "Name": "S", "Files": { "chara/equipment/e6001/model/c0201e6001_sho.mdl": "b\\sho.mdl",
                                            "chara/equipment/e6001/model/c0201e6001_dwn.mdl": "b\\dwn.mdl" } } ] },
                { "Version": 0, "Name": "Boots", "Type": "Single", "Priority": 0, "DefaultSettings": 0, "Options": [
                  { "Name": "Boots", "Files": { "chara/equipment/e6002/model/c0201e6002_sho.mdl": "boots.mdl" } } ] },
                { "Version": 0, "Name": "Gloves", "Type": "Single", "Priority": 0, "DefaultSettings": 0, "Options": [
                  { "Name": "Gloves", "Files": { "chara/equipment/e6003/model/c0201e6003_glv.mdl": "gloves.mdl" } } ] } ] }
            """);
        return root;
    }

    [Fact]
    [Trait("Plan", "AC-keep-ends-studio-6")]
    public void PiecesOf_includes_shoes_and_gloves_after_chests_and_legs()   // [AC-keep-ends-studio-6]
    {
        string root = EndsMod();
        try
        {
            var slots = SizeSetRefit.PiecesOf(root).Select(p => p.Slot).ToList();
            Assert.Equal(["_top", "_dwn", "_dwn"], slots.Take(3));
            Assert.Equal(new[] { "_glv", "_glv", "_sho", "_sho", "_sho" }, slots.Skip(3).OrderBy(s => s));
            Assert.Equal(["_top", "_dwn", "_dwn"], SizeSetRefit.PiecesOf(root, includeEnds: false).Select(p => p.Slot));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Plan", "AC-keep-ends-studio-8")]
    public void Follow_flags_for_shoes_and_gloves()   // [AC-keep-ends-studio-8]
    {
        string root = EndsMod();
        try
        {
            var pieces = SizeSetRefit.PiecesOf(root);
            SizeSetRefit.Piece Of(string id, string slot) => pieces.Single(p => p.GamePath.Contains(id) && p.Slot == slot);

            // With the outfit's chest: the shoe and the glove follow the chest, the legs too.
            Assert.True(Of("e6000", "_glv").FollowsChest);
            Assert.True(Of("e6000", "_sho").FollowsChest);
            Assert.False(Of("e6000", "_sho").FollowsLegs);
            // Legs and a shoe, no chest: the shoe follows the legs.
            Assert.True(Of("e6001", "_sho").FollowsLegs);
            Assert.False(Of("e6001", "_sho").FollowsChest);
            // Each in a group of its own: nothing to follow.
            Assert.False(Of("e6002", "_sho").FollowsChest || Of("e6002", "_sho").FollowsLegs);
            Assert.False(Of("e6003", "_glv").FollowsChest || Of("e6003", "_glv").FollowsLegs);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-22")]
    public void A_mod_with_only_shoes_has_pieces_only_with_ends()   // [EDGE-keep-ends-studio-22]
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), """
                { "FileVersion": 4, "Name": "Shoes",
                  "DefaultData": { "Files": { "chara/equipment/e6002/model/c0201e6002_sho.mdl": "boots.mdl" },
                                   "FileSwaps": {}, "Manipulations": [] }, "Groups": [] }
                """);
            Assert.Equal(["0201"], SizeSetRefit.PiecesOf(root).Select(p => p.Race));
            Assert.Empty(SizeSetRefit.PiecesOf(root, includeEnds: false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-12")]
    public void Mens_shoes_get_no_womens_bodies()   // [EDGE-keep-ends-studio-12]
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), """
                { "FileVersion": 4, "Name": "Men's shoes",
                  "DefaultData": { "Files": { "chara/equipment/e6002/model/c0101e6002_sho.mdl": "boots.mdl" },
                                   "FileSwaps": {}, "Manipulations": [] }, "Groups": [] }
                """);
            var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
            var result = SizeSetRefit.Run(new SizeSetRefit.Request(root, ["rue"], false, false, []), null, log, _ => { },
                                          CancellationToken.None);
            Assert.Equal(0, result.Made);
            Assert.Empty(result.Notes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LocalDataFact(Bibo)]
    [Trait("Plan", "AC-keep-ends-studio-9")]
    [Trait("Plan", "EDGE-keep-ends-studio-1")]
    public void Unreadable_ends_are_noted()   // [AC-keep-ends-studio-9] [EDGE-keep-ends-studio-1]
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "boots.mdl"), [1, 2, 3, 4]);
            File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), """
                { "FileVersion": 4, "Name": "Shoes",
                  "DefaultData": { "Files": { "chara/equipment/e6002/model/c0201e6002_sho.mdl": "boots.mdl" },
                                   "FileSwaps": {}, "Manipulations": [] }, "Groups": [] }
                """);
            var (result, _) = RunOn(root, ["bibo"], Bibo);
            Assert.Equal(0, result.Made);
            Assert.Contains(result.Notes, n => n.Contains("e6002", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A mod of one shoe in a "Shoes" group whose one option is <paramref name="option"/>.</summary>
    private static string ShoeMod(byte[] shoe, string option, string gamePath = "chara/equipment/e6219/model/c0201e6219_sho.mdl")
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "shoe"));
        string slot = gamePath.EndsWith("_glv.mdl", StringComparison.Ordinal) ? "glv" : "sho";
        File.WriteAllBytes(Path.Combine(root, "shoe", $"{slot}.mdl"), shoe);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile),
            "{ \"FileVersion\": 4, \"Name\": \"Shoe\", \"DefaultData\": { \"Files\": {}, \"FileSwaps\": {}, \"Manipulations\": [] },"
          + " \"Groups\": [ { \"Version\": 0, \"Name\": \"Shoes\", \"Type\": \"Single\", \"Priority\": 0, \"DefaultSettings\": 0,"
          + $" \"Options\": [ {{ \"Name\": \"{option}\", \"Files\": {{ \"{gamePath}\": \"shoe\\\\{slot}.mdl\" }} }} ] }} ] }}");
        return root;
    }

    /// <summary>The model a group's option gives a game path, read from the mod.</summary>
    private static byte[] ModelOf(string root, string group, string option, string gamePath)
    {
        var o = PenumbraModMeta.TryReadFileOptions(root, group)!.Single(x => x.Name == option);
        string file = o.Files.First(f => string.Equals(f.Key, gamePath, StringComparison.OrdinalIgnoreCase)).Value;
        return File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, file));
    }

    /// <summary>A kept end's skin positions in <paramref name="original"/> are all present, exactly, in <paramref name="saved"/>.</summary>
    private static int EndPointsMissing(byte[] original, string slot, byte[] saved)
    {
        var parts = ModelPartReader.Read(original)!;
        var reading = KeepEnds.Read(slot, parts, original)!;
        HashSet<(float, float, float)> Skin(byte[] model, Func<int, bool>? which)
        {
            var p = ModelPartReader.Read(model)!;
            return p.Parts.Where(x => x.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(x.Material))
                          .SelectMany(x => x.Triangles).Distinct().Where(v => which == null || which(v))
                          .Select(v => (p.Positions[v * 3], p.Positions[v * 3 + 1], p.Positions[v * 3 + 2])).ToHashSet();
        }
        var kept = Skin(original, reading.Ends.Contains);
        var after = Skin(saved, null);
        return kept.Count(k => !after.Contains(k));
    }

    private const string ShoePath = "chara/equipment/e6219/model/c0201e6219_sho.mdl";

    /// <summary>
    /// The body a shoe or glove is read as made on, through the very calls the size set makes: the first ticked body's
    /// mod preferred, over the body mods given.
    /// </summary>
    private static AutoRefitDecisions.Pick? EndSourceOf(byte[] model, string slot, string preferredKey, params string[] bodyMods)
    {
        string modsRoot = LocalData.Path(LocalData.Mods);
        var dirs = bodyMods.Select(n => Path.GetFileName(LocalData.Path(n))).ToDictionary(d => d, d => d);
        var bodies = new BodyModIndex().RefreshState(dirs, modsRoot).List;
        var mods = bodies.Select(b => (b.Dir, b.Catalog)).ToList();
        string sizeSlot = slot == "_glv" ? "_top" : "_dwn";
        string preferred = SupportedBodies.Find(SupportedBodies.Of(preferredKey, sizeSlot, false)!, bodies)?.Dir ?? mods[0].Dir;
        var parts = ModelPartReader.Read(model)!;
        var bones = new HashSet<string>(SecondSkinWriter.Parse(model).BoneNames, StringComparer.Ordinal);
        var reading = KeepEnds.Read(slot, parts, model)!;
        return KeepEnds.Find(reading, parts, bones, "0201",
                             (view, s) => RefitCore.DetectSource(view, bones, s, "0201", mods, preferred, _ => true, _ => { }),
                             dir => bodies.FirstOrDefault(b => b.Dir == dir)?.Catalog).Pick;
    }

    [LocalDataFact(LocalData.ShiftPack, Yab, Rue, Bibo)]
    [Trait("Plan", "AC-keep-ends-studio-6")]
    [Trait("Plan", "EDGE-keep-ends-studio-13")]
    [Trait("Plan", "EDGE-keep-ends-studio-14")]
    public void Shift_shoe_gains_legs_sizes_with_its_foot_kept()   // [AC-keep-ends-studio-6] [EDGE-keep-ends-studio-13] [EDGE-keep-ends-studio-14]
    {
        var shoe = LocalData.PackEntryEndingWith(LocalData.ShiftPack, "e6219_sho.mdl");
        string root = ShoeMod(shoe, "YAB WC");
        try
        {
            // YAB's, Rue's and Bibo's feet are one shape: which body it is read as made on is read first, and the
            // run's results are judged against that.
            var pick = EndSourceOf(shoe, "_sho", "yab", Yab, Rue, Bibo);
            output.WriteLine($"read as made on {pick?.Dir} / {pick?.Option.Label} ({pick?.Confidence})");
            Assert.NotNull(pick);
            // The body by its mod: Rue+ and YAB+ ship the very same legs files.
            var installed = new BodyModIndex().RefreshState(
                new[] { Yab, Rue, Bibo }.Select(n => Path.GetFileName(LocalData.Path(n))).ToDictionary(d => d, d => d),
                LocalData.Path(LocalData.Mods)).List;
            var made = new[] { "yab", "rue", "bibo" }.Select(k => SupportedBodies.Of(k, "_dwn", false)!)
                                                    .First(b => SupportedBodies.Find(b, installed)?.Dir == pick!.Dir);

            var groupsBefore = (PenumbraModMeta.TryReadGroups(root) ?? []).Select(g => g.Name).ToList();
            var (result, _) = RunOn(root, ["yab", "rue", "bibo"], Yab, Rue, Bibo);

            var added = Options(root, "Shoes").Skip(1).ToList();
            output.WriteLine($"added: {string.Join(", ", added)}; read as {made.Label}");
            var table = new[] { "yab", "rue", "bibo" }.Select(k => SupportedBodies.Of(k, "_dwn", false)!)
                                                     .SelectMany(b => b.Sizes.Select(s => SupportedBodies.OptionName(b, s)))
                                                     .ToHashSet();
            Assert.NotEmpty(added);
            Assert.All(added, o => Assert.Contains(o, table));
            // Not the body it was made on: its other sizes have legs of one shape with it, and give nothing. The other
            // two bodies' legs do.
            Assert.DoesNotContain(added, o => o.StartsWith(made.Label, StringComparison.Ordinal));
            Assert.Equal(2, added.Select(o => o.Split(' ')[0]).Distinct().Count());
            foreach (string option in added)
                Assert.Equal(0, EndPointsMissing(shoe, "_sho", ModelOf(root, "Shoes", option, ShoePath)));
            // Measured inside the pass: no refit left a triangle folded.
            var logged = EndRefitsLogged();
            Assert.Equal(added.Count, logged.Count);
            Assert.All(logged, l => Assert.True(l.Folded == 0, $"{l.Option}: {l.Folded} triangles left folded"));

            // Appended to the author's group, which keeps its place and its default; never a v3 file.
            Assert.Equal(groupsBefore, (PenumbraModMeta.TryReadGroups(root) ?? []).Select(g => g.Name).ToList());
            Assert.Equal("YAB WC", Options(root, "Shoes")[0]);
            Assert.Equal("YAB WC", PenumbraModMeta.SingleDefault(root, "Shoes"));
            Assert.DoesNotContain("\"FileVersion\": 3", File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)));
            output.WriteLine($"{result.Made} made; notes: {string.Join(" / ", result.Notes)}");
            Assert.Equal(added.Count, result.Made);

            // A second run finds nothing left to make.
            var (again, _) = RunOn(root, ["yab", "rue", "bibo"], Yab, Rue, Bibo);
            Assert.Equal(0, again.Made);

            // Remake: only the sizes made here are made again, in place; the author's is untouched.
            var authors = ModelOf(root, "Shoes", "YAB WC", ShoePath);
            string modsRoot = LocalData.Path(LocalData.Mods);
            var dirs = new[] { Yab, Rue, Bibo }.Select(n => Path.GetFileName(LocalData.Path(n))).ToDictionary(d => d, d => d);
            var bodies = new BodyModIndex().RefreshState(dirs, modsRoot).List;
            var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
            var remade = SizeSetRefit.Run(new SizeSetRefit.Request(root, ["yab", "rue", "bibo"], false, Remake: true, bodies),
                                          new UVRemapService(log, "."), log, _ => { }, CancellationToken.None);
            Assert.Equal(added.Count, remade.Made);
            Assert.Equal(Options(root, "Shoes").Skip(1), added);
            Assert.Equal(authors, ModelOf(root, "Shoes", "YAB WC", ShoePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LocalDataFact(LocalData.ShiftPack, Yab, Rue, Bibo)]
    [Trait("Plan", "EDGE-keep-ends-studio-15")]
    public void A_shoe_in_no_group_gets_a_group_of_ours_whose_default_is_put_back()   // [EDGE-keep-ends-studio-15]
    {
        // Given outside any group, its sizes go into a group of Proteus's own, appended after the author's, whose default
        // is "Original" again once the run is done — not the size saved last.
        string root = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "sho.mdl"), LocalData.PackEntryEndingWith(LocalData.ShiftPack, "e6219_sho.mdl"));
            File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
                { "FileVersion": 4, "Name": "Shoe",
                  "DefaultData": { "Files": { "{{ShoePath}}": "sho.mdl" }, "FileSwaps": {}, "Manipulations": [] },
                  "Groups": [ { "Version": 0, "Name": "Colour", "Type": "Single", "Priority": 0, "DefaultSettings": 0,
                                "Options": [ { "Name": "Black", "Files": {} } ] } ] }
                """);
            var (result, _) = RunOn(root, ["bibo"], Yab, Rue, Bibo);
            var groups =(PenumbraModMeta.TryReadGroups(root) ?? []).Select(g => g.Name).ToList();
            output.WriteLine($"groups: {string.Join(", ", groups)}; {result.Made} made");
            Assert.True(result.Made > 0);
            Assert.Equal("Colour", groups[0]);
            Assert.Equal(2, groups.Count);
            Assert.Equal(BodyRetargetWriter.OriginalOption, PenumbraModMeta.SingleDefault(root, groups[1]));
            Assert.DoesNotContain("\"FileVersion\": 3", File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LocalDataFact(LocalData.ShiftPack, Yab, Rue, Bibo)]
    [Trait("Plan", "EDGE-keep-ends-studio-24")]
    public void Cancelling_keeps_saved_end_sizes()   // [EDGE-keep-ends-studio-24]
    {
        // Cancelled while the third body is being made: what the first two saved stays, and nothing is half-written.
        var shoe = LocalData.PackEntryEndingWith(LocalData.ShiftPack, "e6219_sho.mdl");
        string root = ShoeMod(shoe, "YAB WC");
        try
        {
            string modsRoot = LocalData.Path(LocalData.Mods);
            var dirs = new[] { Yab, Rue, Bibo }.Select(n => Path.GetFileName(LocalData.Path(n))).ToDictionary(d => d, d => d);
            var bodies = new BodyModIndex().RefreshState(dirs, modsRoot).List;
            var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
            using var cts = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => SizeSetRefit.Run(
                new SizeSetRefit.Request(root, ["yab", "rue", "bibo"], false, false, bodies), new UVRemapService(log, "."),
                log, p => { if (p.Contains("Bibo", StringComparison.Ordinal)) cts.Cancel(); }, cts.Token));

            var kept = Options(root, "Shoes").Skip(1).ToList();
            output.WriteLine("kept: " + string.Join(", ", kept));
            Assert.NotEmpty(kept);
            Assert.DoesNotContain(kept, o => o.StartsWith("Bibo", StringComparison.Ordinal));
            foreach (string option in kept)
                Assert.Equal(0, EndPointsMissing(shoe, "_sho", ModelOf(root, "Shoes", option, ShoePath)));
            Assert.Equal("YAB WC", PenumbraModMeta.SingleDefault(root, "Shoes"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    [LocalDataFact(LocalData.ShiftPack, Yab)]
    [Trait("Plan", "EDGE-keep-ends-studio-6")]
    public void A_body_with_no_legs_in_the_table_gives_shoes_nothing()   // [EDGE-keep-ends-studio-6]
    {
        string root = ShoeMod(LocalData.PackEntryEndingWith(LocalData.ShiftPack, "e6219_sho.mdl"), "YAB WC");
        try
        {
            var (result, _) = RunOn(root, ["yab-buff"], Yab);
            Assert.Equal(0, result.Made);
            Assert.Empty(result.Notes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string SeenGloves = LocalData.Mods + ":Seen - by Solona/common/8/c0201e0670_glv.mdl";
    private const string GlovePath = "chara/equipment/e0670/model/c0201e0670_glv.mdl";

    [LocalDataFact(SeenGloves, Rue, Yab)]
    [Trait("Plan", "AC-keep-ends-studio-7")]
    [Trait("Plan", "EDGE-keep-ends-studio-5")]
    public void Gloves_in_their_own_group_get_one_option_per_body()   // [AC-keep-ends-studio-7] [EDGE-keep-ends-studio-5]
    {
        var gloves = File.ReadAllBytes(LocalData.Path(SeenGloves));
        string root = ShoeMod(gloves, "Gloves", GlovePath);
        try
        {
            var (result, _) = RunOn(root, ["rue", "yab"], Rue, Yab);
            var added = Options(root, "Shoes").Skip(1).ToList();
            output.WriteLine("added: " + string.Join(", ", added));
            // One option per body, named by it; Rue, the body they were made on, has the same arms at every size and
            // is skipped as the same shape.
            Assert.Equal(["YAB"], added);
            Assert.Equal(0, EndPointsMissing(gloves, "_glv", ModelOf(root, "Shoes", "YAB", GlovePath)));
            Assert.Equal(1, result.Made);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string Heartthrob = LocalData.Mods + ":Heartthrob - by Solona";
    private const string HeartthrobShoe = "chara/equipment/e6070/model/c0201e6070_sho.mdl";

    [LocalDataFact(Heartthrob, Bibo, Rue, Yab)]
    [Trait("Plan", "AC-keep-ends-studio-8")]
    [Trait("Plan", "EDGE-keep-ends-studio-16")]
    public void Heartthrob_shoe_joins_its_legs_sizes()   // [AC-keep-ends-studio-8] [EDGE-keep-ends-studio-16]
    {
        // The skirt and the shoe are sized together in "Bottom Size". Copied without Proteus's record of the automatic
        // refit made into it, so nothing there is taken for a size made before.
        string copy = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyModels(LocalData.Path(Heartthrob), copy, withRecord: false);
            var before = Options(copy, "Bottom Size");
            var groupsBefore = (PenumbraModMeta.TryReadGroups(copy) ?? []).Select(g => g.Name).ToList();
            var (result, _) = RunOn(copy, ["rue"], Bibo, Rue, Yab);

            var added = Options(copy, "Bottom Size").Except(before).ToList();
            output.WriteLine($"added: {string.Join(", ", added)}; {result.Made} made");
            Assert.Equal(groupsBefore, (PenumbraModMeta.TryReadGroups(copy) ?? []).Select(g => g.Name).ToList());
            Assert.NotEmpty(added);
            var small = File.ReadAllBytes(Path.Combine(copy, "bottom size", "small", "chara", "equipment", "e6070", "model",
                                                       "c0201e6070_sho.mdl"));
            foreach (string option in added)
            {
                // Each option the legs made carries a refitted shoe too, its foot kept as the author's first size has it.
                var shoe = ModelOf(copy, "Bottom Size", option, HeartthrobShoe);
                Assert.False(shoe.AsSpan().SequenceEqual(small), $"{option}: the shoe is the author's, not refitted");
                Assert.Equal(0, EndPointsMissing(small, "_sho", shoe));
            }
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    [LocalDataFact(Heartthrob, Bibo, Rue, Yab)]
    [Trait("Plan", "EDGE-keep-ends-studio-17")]
    public void Authors_own_shoe_size_is_found_by_its_legs()   // [EDGE-keep-ends-studio-17]
    {
        // Heartthrob's legs are Bibo+ Small, Medium and Large. Asked for Bibo, "Bottom Size" gains nothing: the legs find
        // the author's sizes, and the shoe, which follows the legs, has no option of theirs to join. (The top is read off
        // its chest and does gain Bibo sizes, in its own group.)
        string copy = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyModels(LocalData.Path(Heartthrob), copy, withRecord: false);
            var before = Options(copy, "Bottom Size");
            var (result, _) = RunOn(copy, ["bibo"], Bibo, Rue, Yab);
            Assert.Equal(before, Options(copy, "Bottom Size"));
            Assert.DoesNotContain(result.Notes, n => n.Contains("e6070", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    [LocalDataFact(HellForLeather, Bibo, Rue)]
    [Trait("Plan", "AC-keep-ends-studio-9")]
    public void HFL_boots_and_gloves_are_noted_not_sized()   // [AC-keep-ends-studio-9]
    {
        // Leather from toe to thigh and fingertip to shoulder: nothing to read a body off. Each gets a note, no sizes.
        string copy = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyModels(LocalData.Path(HellForLeather), copy, withRecord: false);
            var (result, _) = RunOn(copy, ["rue"], Bibo, Rue);
            Assert.Contains(result.Notes, n => n.Contains("e0380", StringComparison.Ordinal) && n.Contains("Feet"));
            Assert.Contains(result.Notes, n => n.Contains("e0380", StringComparison.Ordinal) && n.Contains("Hands"));
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    /// <summary>
    /// A shoe that reaches no leg, from real data: Bibo+'s own feet, all skin and nothing of the leg. As Bibo+ ships them
    /// they draw the women's Bibo skin every installed body draws its feet with, and there is nothing to redraw. Pointed
    /// at the men's Bibo skin instead (as Shift's heels are), every body redraws it as its own.
    /// </summary>
    private static byte[] BiboFeet(bool mensSkin)
    {
        var feet = File.ReadAllBytes(Path.Combine(LocalData.Path(Bibo), "feet", "small clothes", "chara", "equipment",
                                                  "e0000", "model", "c0201e0000_sho.mdl"));
        return mensSkin ? ModelAttributeWriter.RenameMaterial(feet, "/mt_c0201b0001_bibo.mtrl", "/mt_c0101b0001_bibo.mtrl")
                        : feet;
    }

    private static HashSet<(float, float, float)> AllPositions(byte[] model)
    {
        var p = ModelPartReader.Read(model)!;
        return Enumerable.Range(0, p.Positions.Length / 3)
                         .Select(v => (p.Positions[v * 3], p.Positions[v * 3 + 1], p.Positions[v * 3 + 2])).ToHashSet();
    }

    private static List<string> MaterialsOf(byte[] model) => SecondSkinWriter.Parse(model).MatNames.ToList();

    [LocalDataFact(Bibo, Neolithe)]
    [Trait("Plan", "AC-keep-ends-studio-9")]
    public void A_shoe_reaching_no_leg_gets_a_skin_only_option_per_body_where_the_skin_changes()   // [AC-keep-ends-studio-9]
    {
        var shoe = BiboFeet(mensSkin: true);
        Assert.Equal(KeepEnds.Route.SkinOnly, KeepEndsRouteOf(shoe, Bibo, Neolithe));
        string root = ShoeMod(shoe, "Barefoot");
        try
        {
            var (result, _) = RunOn(root, ["neolithe", "bibo"], Bibo, Neolithe);
            var added = Options(root, "Shoes").Skip(1).ToList();
            output.WriteLine($"added: {string.Join(", ", added)}; {result.Made} made");
            // In its own group: one option per body, named by it.
            Assert.Equal(["Neolithe", "Bibo"], added);
            foreach (string option in added)
            {
                var saved = ModelOf(root, "Shoes", option, ShoePath);
                Assert.Equal(AllPositions(shoe), AllPositions(saved));   // its shape, untouched
                Assert.Contains("/mt_c0201b0001_bibo.mtrl", MaterialsOf(saved));
                Assert.DoesNotContain("/mt_c0101b0001_bibo.mtrl", MaterialsOf(saved));
            }
            Assert.Equal(2, result.Made);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LocalDataFact(Bibo, Neolithe)]
    [Trait("Plan", "AC-keep-ends-studio-9")]
    public void A_shoe_reaching_no_leg_whose_skin_already_matches_gets_nothing()   // [AC-keep-ends-studio-9]
    {
        var shoe = BiboFeet(mensSkin: false);
        Assert.Equal(KeepEnds.Route.SkinOnly, KeepEndsRouteOf(shoe, Bibo, Neolithe));
        string root = ShoeMod(shoe, "Barefoot");
        try
        {
            var (result, _) = RunOn(root, ["neolithe", "bibo"], Bibo, Neolithe);
            Assert.Equal(0, result.Made);
            Assert.Equal(["Barefoot"], Options(root, "Shoes"));
            Assert.Empty(result.Notes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The route a shoe is read on, through the size set's own reading.</summary>
    private static KeepEnds.Route KeepEndsRouteOf(byte[] model, params string[] bodyMods)
    {
        string modsRoot = LocalData.Path(LocalData.Mods);
        var dirs = bodyMods.Select(n => Path.GetFileName(LocalData.Path(n))).ToDictionary(d => d, d => d);
        var bodies = new BodyModIndex().RefreshState(dirs, modsRoot).List;
        var mods = bodies.Select(b => (b.Dir, b.Catalog)).ToList();
        var parts = ModelPartReader.Read(model)!;
        var bones = new HashSet<string>(SecondSkinWriter.Parse(model).BoneNames, StringComparer.Ordinal);
        var reading = KeepEnds.Read("_sho", parts, model)!;
        return KeepEnds.Find(reading, parts, bones, "0201",
                             (view, s) => RefitCore.DetectSource(view, bones, s, "0201", mods, mods[0].Dir, _ => true, _ => { }),
                             dir => bodies.FirstOrDefault(b => b.Dir == dir)?.Catalog).Route;
    }

    [LocalDataFact(Heartthrob, Bibo, Rue, Yab)]
    [Trait("Plan", "EDGE-keep-ends-studio-23")]
    public void A_skin_only_shoe_follows_the_legs_options()   // [EDGE-keep-ends-studio-23]
    {
        // Heartthrob's Small skirt and a shoe reaching no leg, sized together in "Bottom Size": each size the skirt
        // makes carries that body's skin-only shoe, and the shoe makes no option of its own.
        string root = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string Skirt = "chara/equipment/e6071/model/c0201e6071_dwn.mdl";
            File.Copy(Path.Combine(LocalData.Path(Heartthrob), "bottom size", "small", "chara", "equipment", "e6071",
                                   "model", "c0201e6071_dwn.mdl"), Path.Combine(root, "dwn.mdl"));
            var shoe = BiboFeet(mensSkin: true);
            File.WriteAllBytes(Path.Combine(root, "sho.mdl"), shoe);
            File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
                { "FileVersion": 4, "Name": "Skirt",
                  "DefaultData": { "Files": {}, "FileSwaps": {}, "Manipulations": [] },
                  "Groups": [ { "Version": 0, "Name": "Bottom Size", "Type": "Single", "Priority": 0, "DefaultSettings": 0,
                                "Options": [ { "Name": "Small", "Files": { "{{Skirt}}": "dwn.mdl", "{{ShoePath}}": "sho.mdl" } } ] } ] }
                """);
            var (result, _) = RunOn(root, ["rue"], Bibo, Rue, Yab);
            var added = Options(root, "Bottom Size").Skip(1).ToList();
            output.WriteLine($"added: {string.Join(", ", added)}; {result.Made} made");
            Assert.NotEmpty(added);
            Assert.All(added, o => Assert.StartsWith("Rue", o));
            foreach (string option in added)
            {
                var saved = ModelOf(root, "Bottom Size", option, ShoePath);
                Assert.Equal(AllPositions(shoe), AllPositions(saved));
                Assert.DoesNotContain("/mt_c0101b0001_bibo.mtrl", MaterialsOf(saved));
            }
            // The skirt's options, and one shoe per option: twice the options made.
            Assert.Equal(2 * added.Count, result.Made);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LocalDataFact(Heartthrob, SeenGloves, LocalData.ShiftPack, Bibo, Rue, Yab)]
    [Trait("Plan", "AC-keep-ends-studio-8")]
    public void Gloves_and_a_shoe_beside_a_chest_join_its_sizes()   // [AC-keep-ends-studio-8]
    {
        // Heartthrob's Small top, Seen's gloves and Shift's shoe, all sized in one "Top Size": each size the top makes
        // carries the gloves refitted by that size's arms and the shoe by the legs that size goes with; neither makes
        // an option of its own.
        string root = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string Top = "chara/equipment/e6071/model/c0201e6071_top.mdl";
            File.Copy(Path.Combine(LocalData.Path(Heartthrob), "top size", "small", "chara", "equipment", "e6071",
                                   "model", "c0201e6071_top.mdl"), Path.Combine(root, "top.mdl"));
            var gloves = File.ReadAllBytes(LocalData.Path(SeenGloves));
            var shoe = LocalData.PackEntryEndingWith(LocalData.ShiftPack, "e6219_sho.mdl");
            File.WriteAllBytes(Path.Combine(root, "glv.mdl"), gloves);
            File.WriteAllBytes(Path.Combine(root, "sho.mdl"), shoe);
            File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
                { "FileVersion": 4, "Name": "Outfit",
                  "DefaultData": { "Files": {}, "FileSwaps": {}, "Manipulations": [] },
                  "Groups": [ { "Version": 0, "Name": "Top Size", "Type": "Single", "Priority": 0, "DefaultSettings": 0,
                                "Options": [ { "Name": "Small", "Files": { "{{Top}}": "top.mdl", "{{GlovePath}}": "glv.mdl",
                                                                           "{{ShoePath}}": "sho.mdl" } } ] } ] }
                """);
            // Onto Bibo+: the gloves are read as made on YAB+ (or Rue+, their twin), so these are refits across bodies.
            var (result, _) = RunOn(root, ["bibo"], Bibo, Rue, Yab);
            var added = Options(root, "Top Size").Skip(1).ToList();
            output.WriteLine($"added: {string.Join(", ", added)}; {result.Made} made");
            Assert.NotEmpty(added);
            Assert.Equal(["Top Size"], (PenumbraModMeta.TryReadGroups(root) ?? []).Select(g => g.Name));
            var logged = EndRefitsLogged();
            foreach (string option in added)
            {
                var glv = ModelOf(root, "Top Size", option, GlovePath);
                Assert.False(glv.AsSpan().SequenceEqual(gloves), $"{option}: the gloves are the author's, not refitted");
                Assert.Equal(0, EndPointsMissing(gloves, "_glv", glv));
                // The shoe: every Bibo+ chest size goes with legs of its own, so every one carries a refitted shoe.
                var sho = ModelOf(root, "Top Size", option, ShoePath);
                Assert.False(sho.AsSpan().SequenceEqual(shoe), $"{option}: the shoe is the author's, not refitted");
                Assert.Equal(0, EndPointsMissing(shoe, "_sho", sho));
            }
            Assert.All(logged, l => Assert.True(l.Folded == 0, $"{l.Option}: {l.Folded} triangles left folded"));
            Assert.DoesNotContain(result.Notes, n => n.Contains("e6219", StringComparison.Ordinal)
                                                     || n.Contains("e0670", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LocalDataFact(Heartthrob, LocalData.ShiftPack, Bibo, Yab, Neolithe)]
    [Trait("Plan", "AC-keep-ends-studio-8")]
    public void A_shoe_beside_a_chest_keeps_the_authors_where_the_size_has_no_legs()   // [AC-keep-ends-studio-8]
    {
        // Neolithe XS is worn with the Small hips: no legs of its own. The shoe in that size stays the author's, quietly,
        // as the legs piece does; the sizes with legs carry a refitted one.
        string root = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string Top = "chara/equipment/e6071/model/c0201e6071_top.mdl";
            File.Copy(Path.Combine(LocalData.Path(Heartthrob), "top size", "small", "chara", "equipment", "e6071",
                                   "model", "c0201e6071_top.mdl"), Path.Combine(root, "top.mdl"));
            var shoe = LocalData.PackEntryEndingWith(LocalData.ShiftPack, "e6219_sho.mdl");
            File.WriteAllBytes(Path.Combine(root, "sho.mdl"), shoe);
            File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
                { "FileVersion": 4, "Name": "Outfit",
                  "DefaultData": { "Files": {}, "FileSwaps": {}, "Manipulations": [] },
                  "Groups": [ { "Version": 0, "Name": "Top Size", "Type": "Single", "Priority": 0, "DefaultSettings": 0,
                                "Options": [ { "Name": "Small", "Files": { "{{Top}}": "top.mdl", "{{ShoePath}}": "sho.mdl" } } ] } ] }
                """);
            var (result, _) = RunOn(root, ["neolithe"], Bibo, Yab, Neolithe);
            var added = Options(root, "Top Size").Skip(1).ToList();
            output.WriteLine($"added: {string.Join(", ", added)}; notes: {string.Join(" / ", result.Notes)}");
            var noLegs = SupportedBodies.Of("neolithe", "_top", false)!.Sizes.Where(s => s.LegsRel == null)
                                        .Select(s => SupportedBodies.OptionName(SupportedBodies.Of("neolithe", "_top", false)!, s))
                                        .ToHashSet();
            Assert.Contains(added, noLegs.Contains);
            Assert.Contains(added, o => !noLegs.Contains(o));
            foreach (string option in added)
            {
                var sho = ModelOf(root, "Top Size", option, ShoePath);
                Assert.Equal(noLegs.Contains(option), sho.AsSpan().SequenceEqual(shoe));
            }
            Assert.Contains(lastLog, l => l.Template.Contains("has no legs of its own", StringComparison.Ordinal)
                                          && l.Args.Contains("Feet — e6219"));
            Assert.DoesNotContain(result.Notes, n => n.Contains("e6219", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── the game's own body as a source (sizeset-game-body-source) ───────────────────────────────────────

    private const string Lace = LocalData.Mods + ":Lace Gloves and Boots - by Solona";
    private const string LaceGlovePath = "chara/equipment/e6039/model/c0201e6039_glv.mdl";

    /// <summary>The game's own body for a race, as the panel reads it on the framework thread before a run.</summary>
    private static Func<string, BodySizeCatalog?> GameBodies()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        return race => VanillaBodyCatalog.Read(race, p => data.GetFile(p)?.Data);
    }

    /// <summary>
    /// The baseline a kept-end refit's folded triangles are judged by: the same limb and end pairs, planned across bodies
    /// with nothing held.
    /// </summary>
    private static int UnheldFolded(byte[] model, string slot, BodySizeCatalog source, BodyOption from,
                                    BodySizeCatalog target, BodyOption to)
    {
        var parts = ModelPartReader.Read(model)!;
        var reading = KeepEnds.Read(slot, parts, model)!;
        var (endFrom, endTo) = KeepEnds.EndPair(source, target, slot, BodySizeCatalog.RaceOf(to.GamePath) ?? "0201", from, to);
        var uv = new UVRemapService(Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");
        Assert.Null(KeepEnds.BuildPairs(reading, source.PathOf(from), target.PathOf(to),
                                        endFrom == null ? null : source.PathOf(endFrom),
                                        endTo == null ? null : target.PathOf(endTo), false, _ => (null, null), s => s, uv,
                                        null, out var limb, out var end));
        List<BodyRetarget.SlotPair> pairs = [limb];
        if (end is { } e) pairs.Add(e with { ShapeOnly = true });
        return BodyRetarget.Plan(parts, model, pairs, slot, held: new HashSet<int>(), replaceSkin: true, acrossBodies: true,
                                 clearBody: false, cutHidden: true, keepShape: reading.FreePieces,
                                 swapSlot: reading.Limb).Report.Folded;
    }

    /// <summary>No game body handed over, as outside the game.</summary>
    private static readonly Func<string, BodySizeCatalog?>? NoGameBody = null;

    private static byte[] LaceModel(string rel) => File.ReadAllBytes(Path.Combine(LocalData.Path(Lace), "common", rel));

    /// <summary>The game's own boots, in a mod at another item's path: made on the game's body, by definition.</summary>
    private static byte[] GameBoots(out string path)
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        path = new[]
        {
            "chara/equipment/e0100/model/c0201e0100_sho.mdl", "chara/equipment/e0005/model/c0201e0005_sho.mdl",
            "chara/equipment/e0036/model/c0201e0036_sho.mdl", "chara/equipment/e0001/model/c0201e0001_sho.mdl",
        }.First(p => data.GetFile(p) != null);
        return data.GetFile(path)!.Data;
    }

    /// <summary>The route and pick of a shoe or glove read over the body mods, then the game's own body, last.</summary>
    private static KeepEnds.Found ReadWithGameBody(byte[] model, string slot, string race, Func<string, BodySizeCatalog?> game,
                                                   params string[] bodyMods)
    {
        string modsRoot = LocalData.Path(LocalData.Mods);
        var dirs = bodyMods.Select(n => Path.GetFileName(LocalData.Path(n))).ToDictionary(d => d, d => d);
        var bodies = new BodyModIndex().RefreshState(dirs, modsRoot).List;
        var vanilla = game(race)!;
        var mods = bodies.Select(b => (b.Dir, b.Catalog)).Append((Dir: VanillaBodyCatalog.Key, Catalog: vanilla)).ToList();
        var parts = ModelPartReader.Read(model)!;
        var bones = new HashSet<string>(SecondSkinWriter.Parse(model).BoneNames, StringComparer.Ordinal);
        return KeepEnds.Find(KeepEnds.Read(slot, parts, model)!, parts, bones, race,
                             (view, s) => RefitCore.DetectSource(view, bones, s, race, mods, mods[0].Dir,
                                                                 d => d != VanillaBodyCatalog.Key, _ => { }),
                             d => d == VanillaBodyCatalog.Key ? vanilla : bodies.FirstOrDefault(b => b.Dir == d)?.Catalog);
    }

    [LocalDataFact(LocalData.GameData, Rue, Yab)]
    [Trait("Plan", "AC-sizeset-game-body-source-1")]
    [Trait("Plan", "EDGE-sizeset-game-body-source-6")]
    [Trait("Plan", "EDGE-sizeset-game-body-source-7")]
    [Trait("Plan", "EDGE-sizeset-game-body-source-8")]
    public void Game_boots_get_legs_sizes_read_off_the_game_body()   // [AC-sizeset-game-body-source-1] [EDGE-sizeset-game-body-source-6] [EDGE-sizeset-game-body-source-7] [EDGE-sizeset-game-body-source-8]
    {
        // The plan named Lace's boots; read over the game's body too, they still match nothing (5%), so they stay a
        // note. The game's own boots, made on its body by definition, stand in.
        var game = GameBodies();
        var boots = GameBoots(out string from);
        var found = ReadWithGameBody(boots, "_sho", "0201", game, Rue, Yab);
        output.WriteLine($"{from}: route {found.Route}, {found.Pick?.Dir} / {found.Pick?.Option.Label}");
        Assert.Contains(found.Route, new[] { KeepEnds.Route.Skin, KeepEnds.Route.Cloth });
        Assert.Equal(VanillaBodyCatalog.Key, found.Pick!.Dir);

        string root = ShoeMod(boots, "Boots");
        try
        {
            var (result, _) = RunOn(root, ["rue", "yab"], game, Rue, Yab);
            var added = Options(root, "Shoes").Skip(1).ToList();
            output.WriteLine($"added: {string.Join(", ", added)}; {result.Made} made");
            var table = new[] { "rue", "yab" }.Select(k => SupportedBodies.Of(k, "_dwn", false)!)
                                             .SelectMany(b => b.Sizes.Select(s => SupportedBodies.OptionName(b, s))).ToHashSet();
            Assert.NotEmpty(added);
            Assert.All(added, o => Assert.Contains(o, table));
            foreach (string option in added)
                Assert.Equal(0, EndPointsMissing(boots, "_sho", ModelOf(root, "Shoes", option, ShoePath)));
            Assert.Equal(added.Count, result.Made);
            // Recorded as made from the game's body.
            var record = BodyRetargetWriter.ReadRecord(root)!;
            Assert.All(record.Options, e => Assert.Contains(Proteus.Localization.Strings.Parts.RetargetFromVanilla, e.From));
            // The pass's own plans: nothing left folded.
            Assert.All(EndRefitsLogged(), l => Assert.True(l.Folded == 0, $"{l.Option}: {l.Folded} triangles left folded"));
            // The author's group keeps its place and default; never v3.
            Assert.Equal(["Shoes"], (PenumbraModMeta.TryReadGroups(root) ?? []).Select(g => g.Name));
            Assert.Equal("Boots", PenumbraModMeta.SingleDefault(root, "Shoes"));
            Assert.DoesNotContain("\"FileVersion\": 3", File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)));

            var (again, _) = RunOn(root, ["rue", "yab"], game, Rue, Yab);
            Assert.Equal(0, again.Made);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LocalDataFact(LocalData.GameData, Lace, Rue, Yab)]
    [Trait("Plan", "AC-sizeset-game-body-source-2")]
    public void Lace_gloves_made_on_the_game_body_get_one_option_per_body()   // [AC-sizeset-game-body-source-2]
    {
        var game = GameBodies();
        var gloves = LaceModel("4/c0201e6039_glv.mdl");
        var found = ReadWithGameBody(gloves, "_glv", "0201", game, Rue, Yab);
        Assert.Contains(found.Route, new[] { KeepEnds.Route.Skin, KeepEnds.Route.Cloth });
        Assert.Equal(VanillaBodyCatalog.Key, found.Pick!.Dir);

        string root = ShoeMod(gloves, "Lace", LaceGlovePath);
        try
        {
            var (result, _) = RunOn(root, ["rue", "yab"], game, Rue, Yab);
            var added = Options(root, "Shoes").Skip(1).ToList();
            output.WriteLine($"added: {string.Join(", ", added)}; {result.Made} made");
            Assert.Equal(["Rue", "YAB"], added);
            foreach (string option in added)
            {
                var saved = ModelOf(root, "Shoes", option, LaceGlovePath);
                Assert.Equal(0, EndPointsMissing(gloves, "_glv", saved));
                // Its kept hands' gen2 skin, drawn as the new body draws its own — or, where it cannot be converted
                // here, said so in the log.
                bool converted = !MaterialsOf(saved).Contains("/mt_c0201b0001_a.mtrl");
                bool said = lastLog.Any(l => l.Template.Contains("no way to convert", StringComparison.Ordinal));
                output.WriteLine($"{option}: {string.Join(", ", MaterialsOf(saved))}{(said ? " (not convertible here)" : "")}");
                Assert.True(converted || said, $"{option}: kept the gen2 skin without saying why");
            }
            // The pass's own plans leave no more folded than the same pairs planned with nothing held.
            var vanilla = game("0201")!;
            foreach (var (option, folded) in EndRefitsLogged())
            {
                string dir = Path.GetFileName(LocalData.Path(option == "Rue" ? Rue : Yab));
                var target = BodySizeCatalog.Read(Path.Combine(LocalData.Path(LocalData.Mods), dir));
                string middle = SupportedBodies.Of(option == "Rue" ? "rue" : "yab", "_top", false)!.Sizes.First(s => s.Name == "M").Rel;
                var to = target.Options.First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(middle));
                int unheld = UnheldFolded(gloves, "_glv", vanilla, found.Pick.Option, target, to);
                output.WriteLine($"{option}: {folded} folded, {unheld} with nothing held");
                Assert.True(folded <= unheld, $"{option}: {folded} triangles left folded, {unheld} with nothing held");
            }
            Assert.Equal(2, result.Made);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LocalDataFact(LocalData.GameData, LocalData.ShiftPack, SeenGloves, Heartthrob, Yab, Rue, Bibo)]
    [Trait("Plan", "AC-sizeset-game-body-source-3")]
    [Trait("Plan", "EDGE-sizeset-game-body-source-3")]
    public void Body_mod_shoes_and_gloves_read_the_same_with_the_game_body_offered()   // [AC-sizeset-game-body-source-3] [EDGE-sizeset-game-body-source-3]
    {
        var game = GameBodies();
        var cases = new (byte[] Model, string Option, string Path, string[] Keys, string[] Bodies)[]
        {
            (LocalData.PackEntryEndingWith(LocalData.ShiftPack, "e6219_sho.mdl"), "YAB WC", ShoePath, ["yab", "rue", "bibo"],
             [Yab, Rue, Bibo]),
            // Shift with the body it was made on not installed: still read as made on a body mod, not the game's.
            (LocalData.PackEntryEndingWith(LocalData.ShiftPack, "e6219_sho.mdl"), "YAB WC", ShoePath, ["rue", "bibo"],
             [Rue, Bibo]),
            (File.ReadAllBytes(LocalData.Path(SeenGloves)), "Gloves", GlovePath, ["rue", "yab", "bibo"], [Rue, Yab, Bibo]),
        };
        foreach (var (model, option, path, keys, bodies) in cases)
        {
            string without = ShoeMod(model, option, path);
            string with = ShoeMod(model, option, path);
            try
            {
                var (a, _) = RunOn(without, keys, NoGameBody, bodies);
                var (b, _) = RunOn(with, keys, game, bodies);
                var addedA = Options(without, "Shoes").Skip(1).ToList();
                var addedB = Options(with, "Shoes").Skip(1).ToList();
                output.WriteLine($"{path} on {string.Join("/", keys)}: {string.Join(", ", addedA)} | {string.Join(", ", addedB)}");
                Assert.Equal(addedA, addedB);
                Assert.Equal(a.Made, b.Made);
                foreach (string o in addedA) Assert.Equal(ModelOf(without, "Shoes", o, path), ModelOf(with, "Shoes", o, path));
                Assert.DoesNotContain(BodyRetargetWriter.ReadRecord(with)?.Options ?? [],
                                      e => e.From.Contains(Proteus.Localization.Strings.Parts.RetargetFromVanilla));
            }
            finally
            {
                Directory.Delete(without, recursive: true);
                Directory.Delete(with, recursive: true);
            }
        }

        // Heartthrob's shoe, following its legs in "Bottom Size": a whole mod, copied without Proteus's record.
        string modWithout = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        string modWith = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyModels(LocalData.Path(Heartthrob), modWithout, withRecord: false);
            CopyModels(LocalData.Path(Heartthrob), modWith, withRecord: false);
            var (a, _) = RunOn(modWithout, ["rue"], NoGameBody, Bibo, Rue, Yab);
            var (b, _) = RunOn(modWith, ["rue"], game, Bibo, Rue, Yab);
            var addedA = Options(modWithout, "Bottom Size");
            output.WriteLine($"Heartthrob: {string.Join(", ", addedA)}");
            Assert.Equal(addedA, Options(modWith, "Bottom Size"));
            Assert.Equal(a.Made, b.Made);
            foreach (string o in addedA)
                Assert.Equal(ModelOf(modWithout, "Bottom Size", o, HeartthrobShoe), ModelOf(modWith, "Bottom Size", o, HeartthrobShoe));
        }
        finally
        {
            if (Directory.Exists(modWithout)) Directory.Delete(modWithout, recursive: true);
            if (Directory.Exists(modWith)) Directory.Delete(modWith, recursive: true);
        }
    }

    [LocalDataFact(LocalData.GameData, Heartthrob, CasualFitShirt, TheBodySe, TheBodySeX, Bibo, Rue)]
    [Trait("Plan", "AC-sizeset-game-body-source-4")]
    public void Chest_and_legs_never_read_the_game_body()   // [AC-sizeset-game-body-source-4]
    {
        var game = GameBodies();
        // A chest made on Bibo+: the very same sizes with the game's body offered as without.
        string without = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        string with = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        string shirt = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string root in new[] { without, with })
            {
                Directory.CreateDirectory(root);
                File.Copy(Path.Combine(LocalData.Path(Heartthrob), "top size", "small", "chara", "equipment", "e6071",
                                       "model", "c0201e6071_top.mdl"), Path.Combine(root, "top.mdl"));
                File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), """
                    { "FileVersion": 4, "Name": "Top", "DefaultData": { "Files": {}, "FileSwaps": {}, "Manipulations": [] },
                      "Groups": [ { "Version": 0, "Name": "Size", "Type": "Single", "Priority": 0, "DefaultSettings": 0,
                        "Options": [ { "Name": "Small", "Files": { "chara/equipment/e6071/model/c0201e6071_top.mdl": "top.mdl" } } ] } ] }
                    """);
            }
            RunOn(without, ["rue"], NoGameBody, Bibo, Rue);
            RunOn(with, ["rue"], game, Bibo, Rue);
            var added = Options(without).Skip(1).ToList();
            Assert.NotEmpty(added);
            Assert.Equal(added, Options(with).Skip(1));
            foreach (string o in added)
                Assert.Equal(ModelOf(without, "Size", o, "chara/equipment/e6071/model/c0201e6071_top.mdl"),
                             ModelOf(with, "Size", o, "chara/equipment/e6071/model/c0201e6071_top.mdl"));

            // A men's shirt made on no installed body is still refused, not taken for one made on the game's body.
            CopyModels(LocalData.Path(CasualFitShirt), shirt);
            var (refused, _) = RunOn(shirt, ["tbse", "tbse-x"], game, TheBodySe, TheBodySeX);
            Assert.Equal(0, refused.Made);
            Assert.Contains(refused.Notes, n => n.Contains("e6146", StringComparison.Ordinal));
        }
        finally
        {
            foreach (string root in new[] { without, with, shirt })
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [LocalDataFact(Lace, Rue)]
    [Trait("Plan", "AC-sizeset-game-body-source-5")]
    [Trait("Plan", "EDGE-sizeset-game-body-source-1")]
    public void Without_the_game_body_lace_gloves_read_as_they_did()   // [AC-sizeset-game-body-source-5] [EDGE-sizeset-game-body-source-1]
    {
        // No game body handed over (outside the game), or none for the race: read only over the body mods, as before.
        // With Rue+ alone installed that is a nearness reading onto Rue+ (2.7 mm off), whose arms are one shape at every
        // size — so nothing is made, and nothing is said to come from the game's body.
        // A race the game has no body for is an empty catalog, as the panel hands it over.
        string empty = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            foreach (var gameBody in new[] { NoGameBody, _ => null, _ => new BodySizeCatalog(empty, []) })
            {
                string root = ShoeMod(LaceModel("4/c0201e6039_glv.mdl"), "Lace", LaceGlovePath);
                try
                {
                    var (result, _) = RunOn(root, ["rue"], gameBody, Rue);
                    Assert.Equal(0, result.Made);
                    Assert.Equal(["Lace"], Options(root, "Shoes"));
                    Assert.DoesNotContain(lastLog, l => l.Args.Contains(VanillaBodyCatalog.Key));
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }

                // Lace's boots match no body mod at all: today's note, and nothing made.
                string boots = ShoeMod(LaceModel("19/c0201e6039_sho.mdl"), "Lace", "chara/equipment/e6039/model/c0201e6039_sho.mdl");
                try
                {
                    var (result, _) = RunOn(boots, ["rue"], gameBody, Rue);
                    Assert.Equal(0, result.Made);
                    Assert.Equal(string.Format(Proteus.Localization.Strings.SizeSet.UnreadableFmt, "Feet — e6039"),
                                 Assert.Single(result.Notes));
                }
                finally
                {
                    Directory.Delete(boots, recursive: true);
                }
            }
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [LocalDataFact(Bibo, Neolithe)]
    [Trait("Plan", "EDGE-sizeset-game-body-source-4")]
    public void A_shoe_reaching_no_leg_made_on_the_game_body_gets_only_its_skin_redrawn()   // [EDGE-sizeset-game-body-source-4]
    {
        // No shoe of the game's own reaches no leg (its feet read by their cloth), so a body stands in for the game's:
        // Bibo+'s catalog handed over as the game's body, Bibo+ itself not among the body mods. Its own feet, pointed at
        // the men's skin, are then a shoe reaching no leg made on "the game's body".
        var bibo = BodySizeCatalog.Read(LocalData.Path(Bibo));
        Func<string, BodySizeCatalog?> game = _ => bibo;
        var shoe = BiboFeet(mensSkin: true);
        var found = ReadWithGameBody(shoe, "_sho", "0201", game, Neolithe);
        output.WriteLine($"route {found.Route}, {found.Pick?.Dir} / {found.Pick?.Option.Label}");
        Assert.Equal(KeepEnds.Route.SkinOnly, found.Route);
        Assert.Equal(VanillaBodyCatalog.Key, found.Pick!.Dir);

        string root = ShoeMod(shoe, "Barefoot");
        try
        {
            var (result, _) = RunOn(root, ["neolithe"], game, Neolithe);
            var added = Options(root, "Shoes").Skip(1).ToList();
            output.WriteLine($"added: {string.Join(", ", added)}; {result.Made} made");
            Assert.Equal(["Neolithe"], added);
            var saved = ModelOf(root, "Shoes", "Neolithe", ShoePath);
            Assert.Equal(AllPositions(shoe), AllPositions(saved));
            Assert.DoesNotContain("/mt_c0101b0001_bibo.mtrl", MaterialsOf(saved));
            Assert.All(BodyRetargetWriter.ReadRecord(root)!.Options,
                       e => Assert.Contains(Proteus.Localization.Strings.Parts.RetargetFromVanilla, e.From));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LocalDataFact(LocalData.GameData, Lace, TheBodySe, Rue)]
    [Trait("Plan", "EDGE-sizeset-game-body-source-2")]
    public void Mens_lace_gloves_read_the_mens_game_body()   // [EDGE-sizeset-game-body-source-2]
    {
        var game = GameBodies();
        var gloves = LaceModel("3/c0101e6039_glv.mdl");
        var found = ReadWithGameBody(gloves, "_glv", "0101", game, TheBodySe);
        output.WriteLine($"route {found.Route}, {found.Pick?.Dir} / {found.Pick?.Option.Label}");
        Assert.Equal(VanillaBodyCatalog.Key, found.Pick?.Dir);

        string root = ShoeMod(gloves, "Lace", "chara/equipment/e6039/model/c0101e6039_glv.mdl");
        try
        {
            var (result, _) = RunOn(root, ["tbse", "rue"], game, TheBodySe, Rue);
            var added = Options(root, "Shoes").Skip(1).ToList();
            output.WriteLine($"added: {string.Join(", ", added)}; notes: {string.Join(" / ", result.Notes)}");
            Assert.Equal(["TBSE"], added);
            Assert.Equal(0, EndPointsMissing(gloves, "_glv",
                                             ModelOf(root, "Shoes", "TBSE", "chara/equipment/e6039/model/c0101e6039_glv.mdl")));
            Assert.Empty(result.Notes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LocalDataFact(LocalData.GameData, Lace, Neolithe)]
    [Trait("Plan", "EDGE-sizeset-game-body-source-5")]
    public void Lace_gloves_onto_Neolithe_take_the_table_sizes()   // [EDGE-sizeset-game-body-source-5]
    {
        // A family body, and a source that is no family's: the table's sizes, from the installed Neolithe.
        var gloves = LaceModel("4/c0201e6039_glv.mdl");
        string root = ShoeMod(gloves, "Lace", LaceGlovePath);
        try
        {
            var (result, _) = RunOn(root, ["neolithe"], GameBodies(), Neolithe);
            var added = Options(root, "Shoes").Skip(1).ToList();
            output.WriteLine($"added: {string.Join(", ", added)}; notes: {string.Join(" / ", result.Notes)}");
            Assert.Equal(["Neolithe"], added);
            Assert.Equal(0, EndPointsMissing(gloves, "_glv", ModelOf(root, "Shoes", "Neolithe", LaceGlovePath)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LocalDataFact(LocalData.GameData, Lace, Rue)]
    [Trait("Plan", "EDGE-sizeset-game-body-source-9")]
    public void Lace_boots_beside_legs_read_off_no_body_make_nothing()   // [EDGE-sizeset-game-body-source-9]
    {
        // The boots in one group with legs the size set cannot read: the legs are never read off the game's body, so
        // they make nothing and say so, and the boots, which only join what the legs made, make nothing either. One
        // note from the legs, one from the boots — expected, not a fault.
        string root = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "sho.mdl"), LaceModel("19/c0201e6039_sho.mdl"));
            var game = GameBodies();
            File.Copy(game("0201")!.PathOf(game("0201")!.For("_dwn", "0201")[0]), Path.Combine(root, "dwn.mdl"));
            File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), """
                { "FileVersion": 4, "Name": "Outfit", "DefaultData": { "Files": {}, "FileSwaps": {}, "Manipulations": [] },
                  "Groups": [ { "Version": 0, "Name": "Bottom", "Type": "Single", "Priority": 0, "DefaultSettings": 0,
                    "Options": [ { "Name": "Lace", "Files": { "chara/equipment/e6039/model/c0201e6039_dwn.mdl": "dwn.mdl",
                                                              "chara/equipment/e6039/model/c0201e6039_sho.mdl": "sho.mdl" } } ] } ] }
                """);
            var (result, _) = RunOn(root, ["rue"], game, Rue);
            output.WriteLine("notes: " + string.Join(" / ", result.Notes));
            Assert.Equal(0, result.Made);
            Assert.Equal(["Lace"], Options(root, "Bottom"));
            Assert.Contains(result.Notes, n => n.Contains("Legs — e6039", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LocalDataFact(Heartthrob, Bibo, Rue, Yab)]
    [Trait("Plan", "AC-keep-ends-studio-10")]
    [Trait("Plan", "EDGE-keep-ends-studio-26")]
    public void Ends_off_is_todays_size_set()   // [AC-keep-ends-studio-10] [EDGE-keep-ends-studio-26]
    {
        string off = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        string on = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyModels(LocalData.Path(Heartthrob), off, withRecord: false);
            CopyModels(LocalData.Path(Heartthrob), on, withRecord: false);
            string modsRoot = LocalData.Path(LocalData.Mods);
            var mods = new[] { Bibo, Rue, Yab }.Select(n => Path.GetFileName(LocalData.Path(n))).ToDictionary(d => d, d => d);
            var bodies = new BodyModIndex().RefreshState(mods, modsRoot).List;
            var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
            SizeSetRefit.Run(new SizeSetRefit.Request(off, ["rue"], false, false, bodies, Ends: false),
                             new UVRemapService(log, "."), log, _ => { }, CancellationToken.None);
            SizeSetRefit.Run(new SizeSetRefit.Request(on, ["rue"], false, false, bodies, Ends: true),
                             new UVRemapService(log, "."), log, _ => { }, CancellationToken.None);

            const string Skirt = "chara/equipment/e6071/model/c0201e6071_dwn.mdl";
            var made = Options(off, "Bottom Size").Except(Options(LocalData.Path(Heartthrob), "Bottom Size")).ToList();
            Assert.NotEmpty(made);
            Assert.Equal(made, Options(on, "Bottom Size").Except(Options(LocalData.Path(Heartthrob), "Bottom Size")));
            foreach (string option in made)
            {
                Assert.Equal(ModelOf(off, "Bottom Size", option, Skirt), ModelOf(on, "Bottom Size", option, Skirt));
                // Off: the shoe in that option is still the author's, untouched (copied in from the option it was cut from).
                var shoeOff = ModelOf(off, "Bottom Size", option, HeartthrobShoe);
                var shoeOn = ModelOf(on, "Bottom Size", option, HeartthrobShoe);
                Assert.False(shoeOff.AsSpan().SequenceEqual(shoeOn), $"{option}: the shoe was refitted with ends off");
            }
        }
        finally
        {
            if (Directory.Exists(off)) Directory.Delete(off, recursive: true);
            if (Directory.Exists(on)) Directory.Delete(on, recursive: true);
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

    private const string Rosaline = LocalData.Mods + ":hs-Rosaline-1.1.2-hM5N";
    private const string TreYas = LocalData.Mods + ":hs-[YAS] Tre-5.5.3-8xB";

    /// <summary>
    /// Rosaline numbers its folders as YAS Tre does — files/common/5 is Rosaline's Medium chest and YAS Tre's Large —
    /// so with both installed each must still be found in its own mod, chest and legs.
    /// </summary>
    [LocalDataFact(Rosaline, TreYas)]
    public void Rosaline_and_YAS_Tre_are_told_apart()
    {
        string modsRoot = LocalData.Path(LocalData.Mods);
        var mods = new[] { Rosaline, TreYas }.Select(n => Path.GetFileName(LocalData.Path(n))).ToDictionary(d => d, d => d);
        var bodies = new BodyModIndex().RefreshState(mods, modsRoot).List;

        foreach (string slot in new[] { "_top", "_dwn" })
        {
            Assert.Equal(Path.GetFileName(LocalData.Path(Rosaline)),
                         SupportedBodies.Find(SupportedBodies.Of("rosaline", slot, false)!, bodies)?.Dir);
            Assert.Equal(Path.GetFileName(LocalData.Path(TreYas)),
                         SupportedBodies.Find(SupportedBodies.Of("tre", slot, true)!, bodies)?.Dir);
        }
        Assert.True(SupportedBodies.Installed("rosaline", bodies));

        // Its sizes are the table's: M and L tops onto the Hip Dips Vagina A legs, and that one legs size.
        var tops = SupportedBodies.Of("rosaline", "_top", false)!;
        Assert.Equal(["Rosaline M", "Rosaline L"], tops.Sizes.Select(s => SupportedBodies.OptionName(tops, s)));
        var legs = SupportedBodies.Of("rosaline", "_dwn", false)!;
        Assert.Equal(["Rosaline"], legs.Sizes.Select(s => SupportedBodies.OptionName(legs, s)));
    }

    /// <summary>This Old Thing, made on Neolithe, gains Rosaline M and L — its chest and hips pair with Neolithe's.</summary>
    [LocalDataFact(ThisOldThing, Neolithe, Rosaline)]
    public void A_Neolithe_top_gains_Rosaline_M_and_L()
    {
        string copy = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyModels(LocalData.Path(ThisOldThing), copy);
            var before = Options(copy);
            var (result, _) = RunOn(copy, ["rosaline"], Neolithe, Rosaline);
            Assert.Equal(["Rosaline M", "Rosaline L"], Options(copy).Except(before));
            Assert.Equal(2, result.Made);
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
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
    private const string TwillTrousers = LocalData.Mods + ":-Twill Trousers- [YAB, TBSE, TBSE-X]";

    /// <summary>Run a size set on a copy of a mod, printing what it said and logged.</summary>
    private (SizeSetRefit.Result Result, SizeSetRefit.Request Request) RunOn(string copy, IReadOnlyList<string> keys,
                                                                            params string[] bodyMods)
        => RunOn(copy, keys, null, bodyMods);

    /// <inheritdoc cref="RunOn(string, IReadOnlyList{string}, string[])"/>
    /// <param name="gameBody">The game's own body per race, as the panel hands it over; null offers none.</param>
    private (SizeSetRefit.Result Result, SizeSetRefit.Request Request) RunOn(string copy, IReadOnlyList<string> keys,
                                                                            Func<string, BodySizeCatalog?>? gameBody,
                                                                            params string[] bodyMods)
    {
        string modsRoot = LocalData.Path(LocalData.Mods);
        var mods = bodyMods.Select(n => Path.GetFileName(LocalData.Path(n))).ToDictionary(d => d, d => d);
        var bodies = new BodyModIndex().RefreshState(mods, modsRoot).List;
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var request = new SizeSetRefit.Request(copy, keys, Yas: false, Remake: false, bodies, GameBody: gameBody);
        var result = SizeSetRefit.Run(request, new UVRemapService(log, "."), log, p => output.WriteLine(p),
                                      CancellationToken.None);
        foreach (string note in result.Notes) output.WriteLine("note: " + note);
        lastLog.Clear();
        foreach (var call in log.ReceivedCalls())
        {
            var args = call.GetArguments();
            lastLog.Add((args.FirstOrDefault() as string ?? "", args.Skip(1).FirstOrDefault() as object[] ?? []));
            output.WriteLine("log: " + string.Join(" | ", args.Select(a => a is object[] arr
                ? string.Join(", ", arr) : a?.ToString())));
        }
        return (result, request);
    }

    /// <summary>What the last <see cref="RunOn"/> logged: each message template and its arguments.</summary>
    private readonly List<(string Template, object[] Args)> lastLog = [];

    /// <summary>
    /// Each shoe or glove refit the last run saved, and the triangles its plan left folded — read off the pass's own log
    /// line, the plan being measured where it was made, not in the saved model.
    /// </summary>
    private List<(string Option, int Folded)> EndRefitsLogged()
        => lastLog.Where(l => l.Template.Contains("triangles left folded", StringComparison.Ordinal))
                  .Select(l => ((string)l.Args[1], Convert.ToInt32(l.Args[6]))).ToList();

    /// <summary>
    /// A men's top in a "Size" group with only a TBSE option gains "TBSE-X", refitted for a man, and a second run makes
    /// nothing. None of the men's mods installed here can show it: the twill trousers are cloth with no skin to read a
    /// body from, and the casual fit shirt was made on bodies that are not installed. So the "garment" is TBSE's own
    /// chest, filed as an outfit's — all skin, and exactly a TBSE body.
    /// <para/>
    /// A top, not legs: TBSE's SFW legs and TBSE-X's are cut differently (77% of one finds its place on the other by
    /// texture coordinate) and the pair is refused, so the hem is left on the source's hips.
    /// </summary>
    [LocalDataFact(TheBodySe, TheBodySeX)]
    public void A_man_s_garment_gains_the_men_s_bodies()
    {
        string copy = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(copy, "tbse"));
            File.Copy(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(TheBodySe), "common/1/c0101e0000_top.mdl"),
                      Path.Combine(copy, "tbse", "c0101e6000_top.mdl"));
            File.WriteAllText(Path.Combine(copy, PenumbraModMeta.MetaFile), """
                { "FileVersion": 4, "Name": "Men's top",
                  "DefaultData": { "Files": {}, "FileSwaps": {}, "Manipulations": [] },
                  "Groups": [
                    { "Version": 0, "Name": "Size", "Type": "Single", "Priority": 0, "DefaultSettings": 0, "Options": [
                      { "Name": "TBSE", "Files": { "chara/equipment/e6000/model/c0101e6000_top.mdl": "tbse\\c0101e6000_top.mdl" } } ] } ] }
                """);

            var (result, request) = RunOn(copy, ["tbse", "tbse-x"], TheBodySe, TheBodySeX);
            Assert.Equal(["TBSE", "TBSE-X"], Options(copy));
            Assert.Equal(1, result.Made);

            var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
            Assert.Equal(0, SizeSetRefit.Run(request, new UVRemapService(log, "."), log, _ => { }, CancellationToken.None).Made);
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    /// <summary>Cloth with no skin under it has nothing to read a body from: the twill trousers get no sizes.</summary>
    [LocalDataFact(TwillTrousers, TheBodySe, TheBodySeX)]
    public void A_garment_with_no_skin_is_refused()
    {
        string copy = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyModels(LocalData.Path(TwillTrousers), copy);
            Assert.NotNull(PenumbraModMeta.RemoveOption(copy, "Male: Legs Style", "TBSE-X"));
            var (result, _) = RunOn(copy, ["tbse-x"], TheBodySe, TheBodySeX);
            Assert.Equal(0, result.Made);
            Assert.Equal(3, result.Notes.Count);   // e6269, e6270, e6271
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    /// <summary>
    /// A shirt made on bodies none of which is installed — "Type M", TBSE-Q, Thicclanders, TBSE-Z, each 4.5 mm or more
    /// from TBSE and TBSE-X — is refused rather than refitted from the nearest of them. The exact reading used to take
    /// TBSE-X from 13% of shared points, and refit from a body the shirt was never made on.
    /// </summary>
    [LocalDataFact(CasualFitShirt, TheBodySe, TheBodySeX)]
    public void A_garment_made_on_no_installed_body_is_refused()
    {
        string copy = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyModels(LocalData.Path(CasualFitShirt), copy);
            var before = Options(copy, "Body Fit");
            var (result, _) = RunOn(copy, ["tbse", "tbse-x"], TheBodySe, TheBodySeX);
            Assert.Equal(0, result.Made);
            Assert.Equal(before, Options(copy, "Body Fit"));
            Assert.Contains(result.Notes, n => n.Contains("e6146", StringComparison.Ordinal));
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
    /// <param name="withRecord">Copy Proteus's own record of refits made into it too. Without it, nothing in the copy is
    /// taken for a size Proteus made, whatever the player last wore.</param>
    private static void CopyModels(string from, string to, bool withRecord = true)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, file);
            bool manifest = !rel.Contains(Path.DirectorySeparatorChar) && rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            bool record = rel.StartsWith("Proteus" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (record && !withRecord) continue;
            if (!manifest && !record && !rel.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
