using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// What a saved refit option has to carry besides the model it refitted.
/// <para/>
/// Reported as a gen3 outfit swapped onto Rue+ keeping its new shape but losing its textures and materials. The solve
/// was not at fault: that pair's correspondence matches 99.7% of the body, both transfer maps load, the swapped skin
/// takes the target body's material, and a pre-Dawntrail v5 model survives the re-emit with its materials and texture
/// coordinates untouched. The save was.
/// <para/>
/// Mods split two ways. Most keep the material and textures in the default files or one required option and give a
/// size option nothing but a model — there, a refit carrying only a model is exactly right. Some give every size its
/// own material and textures, and there a size group is single-select: switching the refit ON switches the option it
/// was cut from OFF, and everything drawn on the garment goes with it.
/// </summary>
public class RefitOptionCarriesTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("proteus-refit-carry").FullName;

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* a test folder */ }
        GC.SuppressFinalize(this);
    }

    private const string Model = "chara/equipment/e0118/model/c0201e0118_top.mdl";
    private const string Material = "chara/equipment/e0118/material/v0001/mt_c0201e0118_top_a.mtrl";
    private const string Texture = "chara/equipment/e0118/texture/v01_c0201e0118_top_d.tex";
    private const string OtherRace = "chara/equipment/e0118/model/c0101e0118_top.mdl";

    // Metadata on a size option, in Penumbra's own shape. Body EQP bit 12 (0x1000) is "show bracelet"; 0x0200 is
    // "show hands" (see EqpVisibility).
    private const string ShowBracelet = """{ "Type": "Eqp", "Manipulation": { "Entry": 4096, "SetId": 118, "Slot": "Body" } }""";
    private const string HideBracelet = """{ "Type": "Eqp", "Manipulation": { "Entry": 0, "SetId": 118, "Slot": "Body" } }""";
    private const string ShowHands    = """{ "Type": "Eqp", "Manipulation": { "Entry": 4608, "SetId": 118, "Slot": "Body" } }""";
    private const string Imc = """
        { "Type": "Imc", "Manipulation": {
            "Entry": { "MaterialId": 3, "DecalId": 0, "VfxId": 0, "MaterialAnimationId": 0, "AttributeMask": 0, "SoundId": 0 },
            "PrimaryId": 118, "SecondaryId": 0, "Variant": 1, "ObjectType": "Equipment", "EquipSlot": "Body",
            "BodySlot": "Unknown" } }
        """;
    private const string ImcReordered = """
        { "Type": "Imc", "Manipulation": {
            "Entry": { "SoundId": 0, "AttributeMask": 0, "MaterialAnimationId": 0, "VfxId": 0, "DecalId": 0, "MaterialId": 3 },
            "PrimaryId": 118, "SecondaryId": 0, "Variant": 1, "ObjectType": "Equipment", "EquipSlot": "Body",
            "BodySlot": "Unknown" } }
        """;
    private const string ImcMissingZero = """
        { "Type": "Imc", "Manipulation": {
            "Entry": { "MaterialId": 3, "DecalId": 0, "VfxId": 0, "MaterialAnimationId": 0, "AttributeMask": 0 },
            "PrimaryId": 118, "SecondaryId": 0, "Variant": 1, "ObjectType": "Equipment", "EquipSlot": "Body",
            "BodySlot": "Unknown" } }
        """;

    private const string Small = "Rue+ · Small";
    private const string Legs = "chara/equipment/e0118/model/c0201e0118_dwn.mdl";

    /// <summary>An option of the size group: a name, no files, and the metadata given.</summary>
    private static string OptionJson(string name, params string[] manipulations)
        => $$"""{ "Name": "{{name}}", "Files": {}, "Manipulations": [ {{string.Join(", ", manipulations)}} ] }""";

    /// <param name="sizeCarriesEverything">
    /// true: each size ships its own material and textures. false: the size ships a model and the mod's default files
    /// ship the material and textures, which is the common shape.
    /// </param>
    /// <param name="mediumManipulations">The "Medium" option's metadata, as a JSON array's contents; null for none.</param>
    /// <param name="groupType">The size group's <c>Type</c>.</param>
    /// <param name="extraOptions">More options of the size group after "Medium", as JSON objects; null for none.</param>
    private void WriteMod(bool sizeCarriesEverything, string? mediumManipulations = null, string groupType = "Single",
                          params string[] extraOptions)
    {
        string sizeFiles = sizeCarriesEverything
            ? $$"""
                "{{Model}}": "size/medium/model.mdl",
                "{{Material}}": "size/medium/mat.mtrl",
                "{{Texture}}": "size/medium/tex.tex",
                "{{OtherRace}}": "size/medium/other.mdl"
                """
            : $$""""{{Model}}": "size/medium/model.mdl"""";
        string defaults = sizeCarriesEverything
            ? ""
            : $$"""
                "{{Material}}": "shared/mat.mtrl",
                "{{Texture}}": "shared/tex.tex"
                """;
        string mediumMeta = mediumManipulations == null ? "" : $$""", "Manipulations": [ {{mediumManipulations}} ]""";
        string more = string.Concat(extraOptions.Select(o => ", " + o));

        File.WriteAllText(Path.Combine(root, "meta.json"), $$"""
            {
              "FileVersion": 4,
              "Name": "An Outfit",
              "Author": "somebody",
              "Version": "1.0",
              "DefaultData": { "Files": { {{defaults}} }, "Manipulations": [] },
              "Groups": [
                {
                  "Name": "Size",
                  "Type": "{{groupType}}",
                  "Priority": 0,
                  "Options": [ { "Name": "Medium", "Files": { {{sizeFiles}} }{{mediumMeta}} }{{more}} ]
                }
              ]
            }
            """);

        foreach (string rel in new[] { "size/medium/model.mdl", "size/medium/mat.mtrl", "size/medium/tex.tex",
                                       "size/medium/other.mdl", "shared/mat.mtrl", "shared/tex.tex" })
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, [0]);
        }
    }

    private List<string> FilesOf(string group, string option)
    {
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "meta.json"))).RootElement;
        var g = manifest.GetProperty("Groups").EnumerateArray()
                        .First(x => x.GetProperty("Name").GetString() == group);
        var o = g.GetProperty("Options").EnumerateArray()
                 .First(x => x.GetProperty("Name").GetString() == option);
        return o.TryGetProperty("Files", out var f) && f.ValueKind == JsonValueKind.Object
            ? f.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToList()
            : [];
    }

    private JsonNode Manifest() => JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!;

    private void WriteManifest(JsonNode manifest)
        => File.WriteAllText(Path.Combine(root, "meta.json"), manifest.ToJsonString());

    private static JsonNode? GroupNode(JsonNode manifest, string group)
        => manifest["Groups"]!.AsArray().FirstOrDefault(g => (string?)g!["Name"] == group);

    private static JsonNode? OptionNode(JsonNode manifest, string group, string option)
        => GroupNode(manifest, group)?["Options"]!.AsArray().FirstOrDefault(o => (string?)o!["Name"] == option);

    private List<string> OptionNames(string group)
        => GroupNode(Manifest(), group)!["Options"]!.AsArray().Select(o => (string)o!["Name"]!).ToList();

    /// <summary>The option's metadata as written; null when it has none at all.</summary>
    private JsonNode? ManipulationsNode(string group, string option)
        => OptionNode(Manifest(), group, option)?["Manipulations"]?.DeepClone();

    private List<JsonElement> ManipulationsOf(string group, string option)
    {
        var node = ManipulationsNode(group, option);
        return node == null ? [] : JsonSerializer.Deserialize<List<JsonElement>>(node.ToJsonString())!;
    }

    private static string TypeOf(JsonElement manipulation) => manipulation.GetProperty("Type").GetString()!;

    private static string EntryOf(JsonElement manipulation)
        => manipulation.GetProperty("Manipulation").GetProperty("Entry").GetRawText();

    private List<JsonElement> EqpOf(string option)
        => ManipulationsOf("Size", option).Where(m => TypeOf(m) == "Eqp").ToList();

    /// <summary>Edit one option of the manifest in place, as a player or an author updating the mod would.</summary>
    private void EditOption(string group, string option, Action<JsonNode> edit)
    {
        var manifest = Manifest();
        edit(OptionNode(manifest, group, option)!);
        WriteManifest(manifest);
    }

    private BodyRetargetWriter.Outcome SaveAs(string option, string? fromOption, string gamePath = Model,
                                              string group = "Size", IReadOnlyList<object>? manipulations = null)
    {
        var outcome = BodyRetargetWriter.Save(root, group, gamePath, "Rue+", "The Body / A",
                                              [new BodyRetargetWriter.Refit(option, [1, 2, 3], "Small")],
                                              fromOption, manipulations);
        Assert.True(outcome.Ok, outcome.Message);
        foreach (string note in outcome.Notes ?? []) output.WriteLine($"note: {note}");
        return outcome;
    }

    private void Save(string? fromOption)
    {
        var outcome = BodyRetargetWriter.Save(root, "Size", Model, "Rue+", "The Body / A",
                                              [new BodyRetargetWriter.Refit("Rue+ · Small", [1, 2, 3], "Small")],
                                              fromOption);
        Assert.True(outcome.Ok, outcome.Message);
        output.WriteLine($"refit carries: {string.Join("  ", FilesOf("Size", "Rue+ · Small").Select(Path.GetExtension))}");
    }

    [Fact]
    public void Undoing_a_refit_never_deletes_the_author_s_files()
    {
        // The refit carries the author's material and textures so the garment keeps them when it is switched on.
        // Undo removes the option and deletes what it published — which must mean what PROTEUS wrote, and nothing
        // else. Deleting the author's files here would break their mod for every size, permanently.
        WriteMod(sizeCarriesEverything: true);
        Save(fromOption: "Medium");

        string refitModel = Path.Combine(root, JsonDocument
            .Parse(File.ReadAllText(Path.Combine(root, "meta.json"))).RootElement
            .GetProperty("Groups").EnumerateArray().First(g => g.GetProperty("Name").GetString() == "Size")
            .GetProperty("Options").EnumerateArray().First(o => o.GetProperty("Name").GetString() == "Rue+ · Small")
            .GetProperty("Files").GetProperty(Model).GetString()!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(refitModel), "the refit's own model was not written");

        var outcome = BodyRetargetWriter.Undo(root, "Size", "Rue+ · Small");
        Assert.True(outcome.Ok, outcome.Message);

        foreach (string rel in new[] { "size/medium/model.mdl", "size/medium/mat.mtrl",
                                       "size/medium/tex.tex", "size/medium/other.mdl" })
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), $"undo deleted the author's {Path.GetFileName(rel)}");
        }

        Assert.False(File.Exists(refitModel), "undo left the refit's own model behind");
    }

    [Fact]
    public void A_size_that_ships_its_own_material_and_textures_hands_them_to_the_refit()
    {
        WriteMod(sizeCarriesEverything: true);
        Save(fromOption: "Medium");

        var carried = FilesOf("Size", "Rue+ · Small");

        // The refitted model is the refit's own; everything else the size carried comes across, including the other
        // race's model, which this refit did not touch and the author still has to supply.
        Assert.Contains(Model, carried);
        Assert.Contains(Material, carried);
        Assert.Contains(Texture, carried);
        Assert.Contains(OtherRace, carried);

        // And the refitted model is NOT the author's file.
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "meta.json"))).RootElement;
        string wrote = manifest.GetProperty("Groups").EnumerateArray()
                               .First(g => g.GetProperty("Name").GetString() == "Size")
                               .GetProperty("Options").EnumerateArray()
                               .First(o => o.GetProperty("Name").GetString() == "Rue+ · Small")
                               .GetProperty("Files").GetProperty(Model).GetString()!;
        Assert.DoesNotContain("size/medium/model.mdl", wrote);
    }

    [Fact]
    public void A_size_that_ships_only_a_model_hands_over_only_a_model()
    {
        // The common shape: the material and textures are in the default files, which no option switches off.
        WriteMod(sizeCarriesEverything: false);
        Save(fromOption: "Medium");

        Assert.Equal([Model], FilesOf("Size", "Rue+ · Small"));
        // [EDGE-refit-carries-meta-2] and no metadata either, where there was none to carry
        Assert.Empty(ManipulationsOf("Size", "Rue+ · Small"));
    }

    [Fact]
    public void A_model_that_came_from_somewhere_else_hands_over_nothing()
    {
        // No option of this group was switched off by saving here, so there is nothing to replace. Copying anyway
        // would duplicate live redirects and then override the author's if the user changed size.
        WriteMod(sizeCarriesEverything: true);
        Save(fromOption: null);

        Assert.Equal([Model], FilesOf("Size", "Rue+ · Small"));
    }

    [Fact]
    public void Refitting_a_second_slot_keeps_both_the_first_slot_and_the_copied_files()
    {
        WriteMod(sizeCarriesEverything: true, ShowBracelet);
        Save(fromOption: "Medium");

        const string legs = "chara/equipment/e0118/model/c0201e0118_dwn.mdl";
        var outcome = BodyRetargetWriter.Save(root, "Size", legs, "Rue+", "The Body / A",
                                              [new BodyRetargetWriter.Refit("Rue+ · Small", [4, 5, 6], "Small")],
                                              "Medium");
        Assert.True(outcome.Ok, outcome.Message);

        var carried = FilesOf("Size", "Rue+ · Small");
        Assert.Contains(Model, carried);      // the chest refit from the first save
        Assert.Contains(legs, carried);       // and the legs from this one
        Assert.Contains(Material, carried);
        Assert.Contains(Texture, carried);

        // [EDGE-refit-carries-meta-1] the source's metadata was offered by both saves, and is there once
        Assert.Single(EqpOf("Rue+ · Small"));
    }

    // ---- Metadata ------------------------------------------------------------------------------------------------
    //
    // Reported as an outfit whose top shows its bracelet losing it once refitted. The size option the refit was cut
    // from is switched off by switching the refit on — by Penumbra in a single-choice group, by the refit itself in a
    // multi-choice one — and its metadata goes with it, exactly as its files would have before they were carried.

    [Fact]
    public void A_size_that_ships_metadata_hands_it_to_the_refit()
    {
        // [AC-refit-carries-meta-1] [EDGE-refit-carries-meta-7]
        WriteMod(sizeCarriesEverything: true, ShowBracelet + "," + Imc);
        var before = ManipulationsNode("Size", "Medium");

        var outcome = SaveAs(Small, "Medium");

        var carried = ManipulationsOf("Size", Small);
        Assert.Equal(2, carried.Count);
        Assert.Equal("4096", EntryOf(Assert.Single(carried, m => TypeOf(m) == "Eqp")));
        Assert.Single(carried, m => TypeOf(m) == "Imc");
        // Every entry as the author wrote it, value and all, in the author's order.
        Assert.True(JsonNode.DeepEquals(before, ManipulationsNode("Size", Small)), "the carried entries were changed");
        Assert.True(JsonNode.DeepEquals(before, ManipulationsNode("Size", "Medium")), "the author's size was changed");
        Assert.Null(outcome.Notes);
    }

    [Fact]
    public void This_save_s_entry_wins_over_the_source_s_for_the_same_thing()
    {
        // [AC-refit-carries-meta-2] The author's entry is built by the same helper this save's is, so the two are
        // about the same thing by construction; only their values differ.
        var ours = PenumbraManipulations.EqdpManipulation("0101", "Body", 118);
        string authors = JsonSerializer.Serialize(PenumbraManipulations.EqdpManipulation("0101", "Body", 118, hasModel: false));
        WriteMod(sizeCarriesEverything: true, authors + "," + ShowBracelet);

        SaveAs(Small, "Medium", manipulations: [ours]);

        var carried = ManipulationsOf("Size", Small);
        var eqdp = Assert.Single(carried, m => TypeOf(m) == "Eqdp");
        Assert.Equal(EntryOf(JsonSerializer.SerializeToElement(ours)), EntryOf(eqdp));
        Assert.Equal("4096", EntryOf(Assert.Single(carried, m => TypeOf(m) == "Eqp")));
    }

    [Theory]
    [InlineData("Single")]
    [InlineData("Multi")]
    public void Undoing_a_refit_leaves_the_author_s_metadata_as_it_was(string groupType)
    {
        // [AC-refit-carries-meta-3]
        WriteMod(sizeCarriesEverything: true, ShowBracelet + "," + Imc, groupType);
        var before = ManipulationsNode("Size", "Medium");

        SaveAs(Small, "Medium");
        var outcome = BodyRetargetWriter.Undo(root, "Size", Small);
        Assert.True(outcome.Ok, outcome.Message);

        Assert.Equal(["Medium"], OptionNames("Size"));
        Assert.True(JsonNode.DeepEquals(before, ManipulationsNode("Size", "Medium")), "undo changed the author's size");
        Assert.Equal(groupType, (string?)GroupNode(Manifest(), "Size")!["Type"]);
    }

    [Fact]
    public void Undoing_a_refit_cut_from_an_earlier_refit_of_ours_leaves_that_one_as_it_was()
    {
        // [AC-refit-carries-meta-3] [EDGE-refit-carries-meta-5] A group of our own: the automatic refit cutting a new
        // size from an earlier one, which carries the EQDP a racial bake needs for the model it publishes.
        const string own = "Body — Rue+";
        var bake = PenumbraManipulations.EqdpManipulation("0101", "Body", 118);
        WriteMod(sizeCarriesEverything: true);
        SaveAs("Neolithe · M", fromOption: null, group: own, manipulations: [bake]);
        var before = ManipulationsNode(own, "Neolithe · M");

        SaveAs(Small, "Neolithe · M", group: own);

        var carried = Assert.Single(ManipulationsOf(own, Small));
        Assert.Equal(BodyRetargetWriter.ManipulationKey(bake), BodyRetargetWriter.ManipulationKey(carried));

        var outcome = BodyRetargetWriter.Undo(root, own, Small);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.NotNull(GroupNode(Manifest(), own));
        Assert.True(JsonNode.DeepEquals(before, ManipulationsNode(own, "Neolithe · M")), "undo changed the earlier refit");
    }

    [Fact]
    public void A_model_that_came_from_somewhere_else_carries_no_metadata()
    {
        // [AC-refit-carries-meta-4]
        WriteMod(sizeCarriesEverything: true, ShowBracelet);
        SaveAs(Small, fromOption: null);

        Assert.Empty(ManipulationsOf("Size", Small));
    }

    [Fact]
    public void In_a_multi_choice_group_a_sibling_with_the_same_entry_is_no_conflict()
    {
        // [AC-refit-carries-meta-5] Sibling sizes repeat the garment's entry; one size must not block another's.
        WriteMod(sizeCarriesEverything: true, ShowBracelet + "," + Imc, "Multi",
                 OptionJson("Large", ShowBracelet, Imc));

        var outcome = SaveAs(Small, "Medium");

        Assert.Equal("4096", EntryOf(Assert.Single(EqpOf(Small))));
        Assert.Single(ManipulationsOf("Size", Small), m => TypeOf(m) == "Imc");
        Assert.Null(outcome.Notes);
    }

    [Fact]
    public void In_a_multi_choice_group_an_entry_a_toggle_sets_differently_is_left_to_the_toggle()
    {
        // [AC-refit-carries-meta-6]
        WriteMod(sizeCarriesEverything: true, ShowBracelet + "," + Imc, "Multi", OptionJson("Hide bracelet", HideBracelet));
        var medium = ManipulationsNode("Size", "Medium");
        var toggle = ManipulationsNode("Size", "Hide bracelet");

        var outcome = SaveAs(Small, "Medium");

        Assert.Equal("Imc", TypeOf(Assert.Single(ManipulationsOf("Size", Small))));
        string note = Assert.Single(outcome.Notes ?? []);
        Assert.Contains("left Eqp|", note);
        Assert.Contains("SetId=118", note);
        Assert.Contains("Slot=Body", note);
        Assert.Contains("\"Hide bracelet\"", note);
        Assert.True(JsonNode.DeepEquals(medium, ManipulationsNode("Size", "Medium")));
        Assert.True(JsonNode.DeepEquals(toggle, ManipulationsNode("Size", "Hide bracelet")));
    }

    [Fact]
    public void A_source_option_that_is_not_in_the_group_hands_over_nothing()
    {
        // [EDGE-refit-carries-meta-3] A stale name, or an option renamed since it was read.
        WriteMod(sizeCarriesEverything: true, ShowBracelet);
        SaveAs(Small, "Large");

        Assert.Equal([Model], FilesOf("Size", Small));
        Assert.Empty(ManipulationsOf("Size", Small));
    }

    [Fact]
    public void An_earlier_save_of_the_refit_keeps_its_value_when_the_source_changes()
    {
        // [EDGE-refit-carries-meta-4] What an earlier save carried stays above the source, as its files do.
        WriteMod(sizeCarriesEverything: true, ShowBracelet);
        SaveAs(Small, "Medium");
        EditOption("Size", "Medium", o => o["Manipulations"]![0]!["Manipulation"]!["Entry"] = 0);

        SaveAs(Small, "Medium", gamePath: Legs);

        Assert.Equal("4096", EntryOf(Assert.Single(EqpOf(Small))));
    }

    [Fact]
    public void Refitting_an_option_from_itself_writes_each_entry_once()
    {
        // [EDGE-refit-carries-meta-6]
        WriteMod(sizeCarriesEverything: true, ShowBracelet);
        SaveAs(Small, "Medium");
        SaveAs(Small, Small, gamePath: Legs);

        Assert.Single(EqpOf(Small));
    }

    [Fact]
    public void A_size_publishing_the_top_and_the_bracelet_beside_a_bracelet_group_carries_nothing_spurious()
    {
        // [EDGE-refit-carries-meta-8] The shape of the outfit that was reported ("Cardigan and Corset"): each size
        // publishes the top AND the bracelet model and no metadata, beside a group for the top's materials and one for
        // the bracelet's, whose EQDP is the only metadata in the mod.
        const string top = "chara/equipment/e6114/model/c0201e6114_top.mdl";
        const string wrists = "chara/accessory/a0085/model/c0201a0085_wrs.mdl";
        string Size(string name) => $$"""
            { "Name": "{{name}}", "Files": {
                "{{wrists}}": "size\\{{name.ToLowerInvariant()}}\\chara\\accessory\\a0085\\model\\c0201a0085_wrs.mdl",
                "{{top}}": "size\\{{name.ToLowerInvariant()}}\\chara\\equipment\\e6114\\model\\c0201e6114_top.mdl" } }
            """;
        File.WriteAllText(Path.Combine(root, "meta.json"), $$"""
            {
              "FileVersion": 4,
              "Name": "Cardigan and Corset",
              "Author": "somebody",
              "Groups": [
                { "Type": "Multi", "Name": "Dirndl's Bodice", "DefaultSettings": 1, "Options": [
                  { "Name": "Everything", "Files": {
                    "chara/equipment/e6114/material/v0001/mt_c0201e6114_top_a.mtrl": "dirndl's bodice\\mt_c0201e6114_top_a.mtrl" } } ] },
                { "Type": "Multi", "Name": "Glass Pumpkin Bracelet", "Priority": 1, "DefaultSettings": 1, "Options": [
                  { "Name": "Everything", "Files": {
                    "chara/accessory/a0085/model/c0101a0085_wrs.mdl": "glass pumpkin bracelet\\c0101a0085_wrs.mdl" },
                    "Manipulations": [ { "Type": "Eqdp", "Manipulation": {
                      "Gender": "Female", "Race": "Midlander", "SetId": 85, "Slot": "Wrists", "Entry": 48 } } ] } ] },
                { "Type": "Single", "Name": "Size", "Priority": 2, "Options": [
                  {{Size("Small")}}, {{Size("Medium")}}, {{Size("Large")}}, {{Size("XLarge")}} ] }
              ]
            }
            """);
        var bodice = GroupNode(Manifest(), "Dirndl's Bodice")!.DeepClone();
        var bracelet = GroupNode(Manifest(), "Glass Pumpkin Bracelet")!.DeepClone();

        SaveAs(Small, "Medium", gamePath: top);

        Assert.Contains(wrists, FilesOf("Size", Small));
        Assert.Empty(ManipulationsOf("Size", Small));
        Assert.True(JsonNode.DeepEquals(bodice, GroupNode(Manifest(), "Dirndl's Bodice")));
        Assert.True(JsonNode.DeepEquals(bracelet, GroupNode(Manifest(), "Glass Pumpkin Bracelet")));
        Assert.Equal(["Small", "Medium", "Large", "XLarge", Small], OptionNames("Size"));
    }

    [Fact]
    public void An_earlier_refit_of_ours_in_a_multi_choice_group_is_not_a_toggle()
    {
        // [EDGE-refit-carries-meta-9] Even holding another value — carried from the source before the author edited it
        // — an option this tool saved is ours, not the author's to be in charge of.
        WriteMod(sizeCarriesEverything: true, ShowBracelet, "Multi");
        SaveAs("Rue+ · Large", "Medium");
        EditOption("Size", "Rue+ · Large", o => o["Manipulations"]![0]!["Manipulation"]!["Entry"] = 0);

        var outcome = SaveAs(Small, "Medium");

        Assert.Equal("4096", EntryOf(Assert.Single(EqpOf(Small))));
        Assert.Null(outcome.Notes);
    }

    [Fact]
    public void In_a_single_choice_group_a_sibling_s_differing_entry_does_not_stop_the_carry()
    {
        // [EDGE-refit-carries-meta-10] Only one option of the group is on at a time: the sibling is off when the refit
        // is on, so there is nothing for the carried entry to fight.
        WriteMod(sizeCarriesEverything: true, ShowBracelet + "," + Imc, "Single", OptionJson("Large", HideBracelet));

        var outcome = SaveAs(Small, "Medium");

        Assert.Equal("4096", EntryOf(Assert.Single(EqpOf(Small))));
        Assert.Single(ManipulationsOf("Size", Small), m => TypeOf(m) == "Imc");
        Assert.Null(outcome.Notes);
    }

    [Fact]
    public void A_batch_of_sizes_in_a_multi_choice_group_each_carry_what_no_toggle_contests()
    {
        // [EDGE-refit-carries-meta-11] The sizes of one batch are not one another's toggles.
        WriteMod(sizeCarriesEverything: true, ShowBracelet + "," + Imc, "Multi", OptionJson("Hide bracelet", HideBracelet));

        var outcome = BodyRetargetWriter.Save(root, "Size", Model, "Rue+", "The Body / A",
                                              [new BodyRetargetWriter.Refit(Small, [1, 2, 3], "Small"),
                                               new BodyRetargetWriter.Refit("Rue+ · Large", [4, 5, 6], "Large")],
                                              "Medium");
        Assert.True(outcome.Ok, outcome.Message);

        Assert.Equal("Imc", TypeOf(Assert.Single(ManipulationsOf("Size", Small))));
        Assert.Equal("Imc", TypeOf(Assert.Single(ManipulationsOf("Size", "Rue+ · Large"))));
        Assert.Equal(2, outcome.Notes?.Count);
        Assert.All(outcome.Notes!, n => Assert.Contains("\"Hide bracelet\"", n));
    }

    [Fact]
    public void A_toggle_that_changes_another_flag_of_the_same_entry_still_suppresses_the_carry_known_limit()
    {
        // [EDGE-refit-carries-meta-13] A known limit: one EQP entry holds every flag of the set's body, so an option
        // that shows the hands sets the same entry as the size that shows the bracelet, to another value. Entries are
        // carried or left whole, never merged flag by flag; the note says which option took it.
        WriteMod(sizeCarriesEverything: true, ShowBracelet, "Multi", OptionJson("Show hands", ShowHands));

        var outcome = SaveAs(Small, "Medium");

        Assert.Empty(EqpOf(Small));
        Assert.Contains("\"Show hands\"", Assert.Single(outcome.Notes ?? []));
    }

    [Fact]
    public void A_toggle_added_after_the_first_refit_does_not_take_back_what_it_carried()
    {
        // [EDGE-refit-carries-meta-14] What an earlier save carried stays until the refit is undone and made again, as
        // its files do — and the note says it was KEPT, not left to the toggle, because it is still there.
        WriteMod(sizeCarriesEverything: true, ShowBracelet, "Multi");
        Assert.Null(SaveAs(Small, "Medium").Notes);

        var manifest = Manifest();
        GroupNode(manifest, "Size")!["Options"]!.AsArray().Add(JsonNode.Parse(OptionJson("Hide bracelet", HideBracelet)));
        WriteManifest(manifest);

        var outcome = SaveAs(Small, "Medium", gamePath: Legs);

        Assert.Equal("4096", EntryOf(Assert.Single(EqpOf(Small))));
        string note = Assert.Single(outcome.Notes ?? []);
        Assert.Contains("kept Eqp|", note);
        Assert.Contains("\"Hide bracelet\"", note);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_renamed_earlier_refit_of_ours_counts_as_another_option(bool sameValue)
    {
        // [EDGE-refit-carries-meta-15] Renamed in Penumbra, an earlier refit drops out of the record and reads as the
        // author's. With the same value that changes nothing; with another, the note names it.
        WriteMod(sizeCarriesEverything: true, ShowBracelet, "Multi");
        SaveAs("Rue+ · Large", "Medium");
        EditOption("Size", "Rue+ · Large", o =>
        {
            o["Name"] = "Large (mine)";
            if (!sameValue) o["Manipulations"]![0]!["Manipulation"]!["Entry"] = 0;
        });

        var outcome = SaveAs(Small, "Medium");

        if (sameValue)
        {
            Assert.Equal("4096", EntryOf(Assert.Single(EqpOf(Small))));
            Assert.Null(outcome.Notes);
        }
        else
        {
            Assert.Empty(EqpOf(Small));
            Assert.Contains("\"Large (mine)\"", Assert.Single(outcome.Notes ?? []));
        }
    }

    [Fact]
    public void An_Imc_entry_with_its_fields_reordered_is_the_same_value()
    {
        // [EDGE-refit-carries-meta-16] Penumbra writes an entry's fields in its own order; that is no difference.
        WriteMod(sizeCarriesEverything: true, Imc, "Multi", OptionJson("Large", ImcReordered));

        var outcome = SaveAs(Small, "Medium");

        Assert.Single(ManipulationsOf("Size", Small), m => TypeOf(m) == "Imc");
        Assert.Null(outcome.Notes);

        static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();
        Assert.True(BodyRetargetWriter.SameValue(El(Imc), El(ImcReordered)));
        Assert.False(BodyRetargetWriter.SameValue(El(Imc), El(Imc.Replace("\"MaterialId\": 3", "\"MaterialId\": 4"))));
        // ShiftedEntry follows from Entry and Penumbra drops it on rewrite: not a difference either.
        Assert.True(BodyRetargetWriter.SameValue(
            El("""{ "Type": "Eqdp", "Manipulation": { "Entry": 3, "Gender": "Male", "Race": "Midlander", "SetId": 118, "Slot": "Body", "ShiftedEntry": 3 } }"""),
            El("""{ "Type": "Eqdp", "Manipulation": { "Entry": 3, "Gender": "Male", "Race": "Midlander", "SetId": 118, "Slot": "Body" } }""")));
        // No Entry on either side sets the same nothing; on one side only, something else.
        Assert.True(BodyRetargetWriter.SameValue(El("""{ "Type": "Eqp", "Manipulation": { "SetId": 118, "Slot": "Body" } }"""),
                                                 El("""{ "Type": "Eqp", "Manipulation": { "SetId": 118, "Slot": "Body" } }""")));
        Assert.False(BodyRetargetWriter.SameValue(El("""{ "Type": "Eqp", "Manipulation": { "SetId": 118, "Slot": "Body" } }"""),
                                                  El(ShowBracelet)));
    }

    [Theory]
    [InlineData("multi", true)]
    [InlineData("MULTI", true)]
    [InlineData("Single", false)]
    [InlineData("Imc", false)]
    public void The_conflict_rule_reads_the_group_type_case_insensitively(string groupType, bool leftToTheToggle)
    {
        // [EDGE-refit-carries-meta-17]
        WriteMod(sizeCarriesEverything: true, ShowBracelet, groupType, OptionJson("Hide bracelet", HideBracelet));

        var outcome = SaveAs(Small, "Medium");

        if (leftToTheToggle)
        {
            Assert.Empty(EqpOf(Small));
            Assert.Single(outcome.Notes ?? []);
        }
        else
        {
            Assert.Equal("4096", EntryOf(Assert.Single(EqpOf(Small))));
            Assert.Null(outcome.Notes);
        }
    }

    [Fact]
    public void An_Imc_entry_missing_a_zero_field_reads_as_a_conflict_known_limit()
    {
        // [EDGE-refit-carries-meta-18] A known limit: values are compared as written, so an entry that leaves a zero
        // field out — hand-edited, or by another tool — reads as setting something else. It fails safe: left to the
        // other option, and said so.
        WriteMod(sizeCarriesEverything: true, Imc, "Multi", OptionJson("Large", ImcMissingZero));

        var outcome = SaveAs(Small, "Medium");

        Assert.Empty(ManipulationsOf("Size", Small));
        Assert.Contains("\"Large\"", Assert.Single(outcome.Notes ?? []));
    }
}
