using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.SyntheticPack;

namespace Proteus.Tests;

/// <summary>
/// The import side of a pack's option IMC toggles: which default ticks survive, what an installed mod's own choices
/// carry over, and that the leftover manifest still holds the edits the composite reads.
/// </summary>
public partial class ContentImportTests
{
    private static readonly ImcItem ImcBody = new('e', ImcSet, "Body");

    private const string OtherSetEqp =
        "{\"Type\":\"Eqp\",\"Manipulation\":{\"Entry\":0,\"SetId\":42,\"Slot\":\"Body\"}}";
    private const string GlobalRsp =
        "{\"Type\":\"Rsp\",\"Manipulation\":{\"Entry\":1.0,\"SubRace\":\"Midlander\",\"Attribute\":\"BustMaxX\"}}";
    private const string OtherItemImc =
        "{\"Type\":\"Imc\",\"Manipulation\":{\"Entry\":{\"MaterialId\":1,\"AttributeMask\":0},\"ObjectType\":\"Equipment\","
      + "\"PrimaryId\":42,\"Variant\":1,\"EquipSlot\":\"Body\"}}";

    private static JsonArray WrittenGroups(string root)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!["Groups"]!.AsArray();

    private static int WrittenDefault(string root, string group)
        => (int)WrittenGroups(root).First(g => (string?)g!["Name"] == group)!["DefaultSettings"]!;

    private static Dictionary<string, List<string>> Source(params (string Group, string[] Options)[] groups)
        => groups.ToDictionary(g => g.Group, g => g.Options.ToList(), StringComparer.Ordinal);

    /// <summary>[AC-imported-imc-toggles-1] The leftover manifest still carries the option edits, read as the pack's were.</summary>
    [Fact]
    public void An_imported_packs_installed_manifest_still_carries_its_option_imc_edits()
    {
        var dir = TempDir();
        try
        {
            using var built = ImcInOptions([new("Belt", "Single", 0, 0, O("On", 0x3FF), O("Off", 0x3FD))]);
            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Belted", "Tests", ContentImportService.Inspect(built.Path));

            var fromPack = ContentImcEdits.From(PenumbraPackage.Read(built.Path))!;
            var (installed, error) = ContentImcEdits.ReadInstalled(root);
            Assert.Null(error);
            foreach (var choice in new[] { "On", "Off" })
            {
                var sel = new Dictionary<string, List<string>> { ["Belt"] = [choice] };
                Assert.Equal(fromPack.Active(ImcBody, sel)!.Mask, installed!.Active(ImcBody, sel)!.Mask);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// [AC-imported-imc-toggles-5] [EDGE-imported-imc-toggles-35] A Multi option that only switches an imported piece's
    /// parts keeps the pack's tick, in the manifest and in the selection — a same-set Eqp beside it notwithstanding.
    /// </summary>
    [Fact]
    public void An_imc_only_multi_option_keeps_its_default_tick()
    {
        var dir = TempDir();
        try
        {
            using var built = ImcInOptions([new("Parts", "Multi", 0, 0b1, O("Belt", 0x3FD))]);
            var preview = ContentImportService.Inspect(built.Path);
            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Parts", "Tests", preview);

            Assert.Equal(1, WrittenDefault(root, "Parts"));
            Assert.Equal(["Belt"], ContentImportService.ImportSelection(preview)["Parts"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>[AC-imported-imc-toggles-5] An option that does anything beyond the imported pieces still arrives off.</summary>
    [Fact]
    public void Piece_and_other_multi_options_still_arrive_off()
    {
        var dir = TempDir();
        try
        {
            using var built = ImcInOptions([new("Body", "Multi", 0, 0b1, O("Scale", null, Extra: GlobalRsp))]);
            var preview = ContentImportService.Inspect(built.Path);
            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Scaled", "Tests", preview);

            Assert.Equal(0, WrittenDefault(root, "Body"));
            Assert.Empty(ContentImportService.ImportSelection(preview)["Body"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// [AC-imported-imc-toggles-8] [EDGE-imported-imc-toggles-35] An installed mod's own choices for its toggle options
    /// replace the pack's defaults; a group the source does not mention keeps them.
    /// </summary>
    [Fact]
    public void An_installed_import_copies_the_sources_imc_toggle_choices()
    {
        using var built = ImcInOptions(
        [
            new("Parts", "Multi", 0, 0b01, O("Belt", 0x3FD), O("Hood", 0x3FE)),
            new("Extras", "Multi", 0, 0b1, O("Strap", 0x3FB)),
        ]);
        var preview = ContentImportService.Inspect(built.Path);

        var selection = ContentImportService.ImportSelection(preview, Source(("Parts", ["Hood"])));
        Assert.Equal(["Hood"], selection["Parts"]);
        Assert.Equal(["Strap"], selection["Extras"]);
    }

    /// <summary>[AC-imported-imc-toggles-8] A Single group made only of toggles takes the source's choice.</summary>
    [Fact]
    public void A_single_group_of_only_imc_toggles_takes_the_sources_choice()
    {
        using var built = ImcInOptions([new("Belt", "Single", 0, 0, O("On", 0x3FF), O("Off", 0x3FD))]);
        var preview = ContentImportService.Inspect(built.Path);

        Assert.Equal(["Off"], ContentImportService.ImportSelection(preview, Source(("Belt", ["Off"])))["Belt"]);
        Assert.False(ContentImportService.ImportSelection(preview).ContainsKey("Belt"));
    }

    /// <summary>
    /// [AC-imported-imc-toggles-8] [EDGE-imported-imc-toggles-29] [EDGE-imported-imc-toggles-33] Only a folder directly
    /// in Penumbra's mod directory has settings to copy.
    /// </summary>
    [Fact]
    public void InstalledSourceDir_names_only_folders_directly_under_the_mod_directory()
    {
        var mods = Path.Combine(Path.GetTempPath(), "Mods");
        Assert.Equal("Belted", ContentImportService.InstalledSourceDir(Path.Combine(mods, "Belted"), true, mods));
        Assert.Equal("Belted", ContentImportService.InstalledSourceDir(
            Path.Combine(mods.ToUpperInvariant(), "Belted") + Path.DirectorySeparatorChar, true, mods + Path.DirectorySeparatorChar));
        Assert.Null(ContentImportService.InstalledSourceDir(Path.Combine(mods, "Nested", "Belted"), true, mods));
        Assert.Null(ContentImportService.InstalledSourceDir(Path.Combine(mods, "Belted.pmp"), false, mods));
        Assert.Null(ContentImportService.InstalledSourceDir(Path.Combine(mods, "Belted"), true, null));
    }

    /// <summary>
    /// [EDGE-imported-imc-toggles-11] An effect-bearing IMC edit the import strips leaves the same mask: kept with its
    /// effect zeroed, or deleted where it equals the game's entry, which the composite then falls back to.
    /// </summary>
    [Fact]
    public void An_effects_stripped_imc_edit_still_yields_the_same_mask()
    {
        var dir = TempDir();
        try
        {
            var wrists = new ImcItem('a', 2, "Bracelets");
            using var pack = SyntheticPack.EffectBearing();
            var preview = ContentImportService.Inspect(pack.Path);
            var original = ContentImcEdits.From(PenumbraPackage.Read(pack.Path))!.Active(wrists, null)!.Mask;

            var kept = Path.Combine(dir, "kept");
            ContentImportService.WriteMod(kept, "Veil", "A", preview);
            Assert.Equal(original, ContentImcEdits.ReadInstalled(kept).Edits!.Active(wrists, null)!.Mask);

            // The game's a0002 entry for variant 15 equals the pack's in everything but the effect.
            var imc = new byte[4 + 16 * 5 * 6];
            BitConverter.GetBytes((ushort)15).CopyTo(imc, 0);
            BitConverter.GetBytes((ushort)31).CopyTo(imc, 2);
            int at = 4 + (15 * 5 + 2) * 6;
            imc[at] = 15;
            BitConverter.GetBytes((ushort)1023).CopyTo(imc, at + 2);
            byte[]? Game(string p) => p == "chara/accessory/a0002/a0002.imc" ? imc : null;

            var deleted = Path.Combine(dir, "deleted");
            ContentImportService.WriteMod(deleted, "Veil", "A", preview, readGameFile: Game);
            var after = ContentImcEdits.ReadInstalled(deleted).Edits;
            Assert.Null(after?.Active(wrists, null));
            Assert.Equal(original, ImcEntrySource.FromGame(Game, "a0002", "Bracelets", 15)!.Value.AttributeMask);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>[EDGE-imported-imc-toggles-12] An IMC option for an item nobody imported is cleared, and not copied.</summary>
    [Fact]
    public void An_imc_option_for_a_vanilla_item_is_cleared_and_not_copied()
    {
        var dir = TempDir();
        try
        {
            using var built = ImcInOptions([new("Other", "Multi", 0, 0b1, O("Vanilla top", null, Extra: OtherItemImc))]);
            var preview = ContentImportService.Inspect(built.Path);
            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Other", "Tests", preview);

            Assert.Equal(0, WrittenDefault(root, "Other"));
            Assert.Empty(ContentImportService.ImportSelection(preview, Source(("Other", ["Vanilla top"])))["Other"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>[EDGE-imported-imc-toggles-13] A toggle that also touches another set, or everyone, is not a toggle.</summary>
    [Theory]
    [InlineData(OtherSetEqp)]
    [InlineData(GlobalRsp)]
    [InlineData(OtherItemImc)]
    public void An_imc_option_with_other_set_or_global_content_is_cleared_and_not_copied(string extra)
    {
        var dir = TempDir();
        try
        {
            using var built = ImcInOptions([new("Parts", "Multi", 0, 0b1, O("Belt", 0x3FD, Extra: extra))]);
            var preview = ContentImportService.Inspect(built.Path);
            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Parts", "Tests", preview);

            Assert.Equal(0, WrittenDefault(root, "Parts"));
            Assert.Empty(ContentImportService.ImportSelection(preview, Source(("Parts", ["Belt"])))["Parts"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>[EDGE-imported-imc-toggles-14] A gated piece option and a toggle in one group keep both ticks.</summary>
    [Fact]
    public void A_gated_piece_option_and_an_imc_toggle_share_one_group_default()
    {
        var dir = TempDir();
        try
        {
            byte[] Model(string mtrl) => SyntheticModel.Build(["atr_tv_a"],
                new SyntheticModel.Mesh("/" + mtrl, new SyntheticModel.Sub(1)));
            var manifest = """
            {
              "FileVersion": 4, "Name": "Brume", "Author": "A",
              "DefaultData": { "Files": {
                "chara/accessory/a0103/material/v0001/mt_wrs.mtrl": "wrs.mtrl",
                "chara/equipment/e6025/material/v0001/mt_top.mtrl": "top.mtrl"
              } },
              "Groups": [
                { "Name": "Bracelet", "Type": "Multi", "DefaultSettings": 0, "Options": [
                  { "Name": "Everything", "Files": { "chara/accessory/a0103/model/c0201a0103_wrs.mdl": "wrs.mdl" } } ] },
                { "Name": "Shirt", "Type": "Multi", "DefaultSettings": 3, "Options": [
                  { "Name": "Everything", "Files": { "chara/equipment/e6025/model/c0101e6025_top.mdl": "top0101.mdl" } },
                  { "Name": "Belt", "Manipulations": [ { "Type": "Imc", "Manipulation": {
                      "Entry": { "MaterialId": 1, "AttributeMask": 1022 }, "ObjectType": "Equipment",
                      "PrimaryId": 6025, "Variant": 1, "EquipSlot": "Body" } } ] } ] },
                { "Name": "Size", "Type": "Single", "Options": [
                  { "Name": "Small", "Files": { "chara/equipment/e6025/model/c0201e6025_top.mdl": "small.mdl" } },
                  { "Name": "Large", "Files": { "chara/equipment/e6025/model/c0201e6025_top.mdl": "large.mdl" } } ] }
              ]
            }
            """;
            var pmp = WritePack(dir, manifest, new[]
            {
                ("wrs.mtrl", new byte[64]), ("wrs.mdl", Model("mt_wrs.mtrl")),
                ("top.mtrl", new byte[64]), ("top0101.mdl", Model("mt_top.mtrl")),
                ("small.mdl", Model("mt_top.mtrl")), ("large.mdl", Model("mt_top.mtrl")),
            });

            var preview = ContentImportService.Inspect(pmp);
            Assert.Equal(1, ContentImportService.GatedGroupDefaults(preview)["Shirt"]);
            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Brume (Proteus)", "A", preview);

            Assert.Equal(3, WrittenDefault(root, "Shirt"));
            Assert.Equal(["Everything", "Belt"], ContentImportService.ImportSelection(preview)["Shirt"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>[EDGE-imported-imc-toggles-15] A v3 pack keeps the toggle's tick in its group file, and reads back in Read's order.</summary>
    [Fact]
    public void A_v3_packs_imc_toggle_default_survives_in_its_group_file()
    {
        var dir = TempDir();
        try
        {
            using var built = ImcInOptions([new("Parts", "Multi", 0, 0b1, O("Belt", 0x3FD))], legacy: true);
            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Parts", "Tests", ContentImportService.Inspect(built.Path));

            var file = Directory.GetFiles(root, "group_*.json").Single();
            Assert.Equal(1, (int)JsonNode.Parse(File.ReadAllText(file))!["DefaultSettings"]!);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>[EDGE-imported-imc-toggles-15] An installed v3 folder reads in the order Read gives it.</summary>
    [Fact]
    public void ReadInstalledManifest_reads_a_v3_folder_in_reads_order()
    {
        var dir = TempDir();
        try
        {
            using var built = ImcInOptions(
            [
                new("Alpha", "Single", 2, 0, O("a", 0x3FD)),
                new("Beta", "Multi", 0, 0b1, O("b", 0x3FE)),
                new("Gamma", "Single", 0, 0, O("c", 0x3FF)),
            ], legacy: true);
            var folder = Path.Combine(dir, "installed");
            PenumbraPackage.ExtractTo(built.Path, folder);

            var read = PenumbraPackage.Read(built.Path);
            var installed = PenumbraPackage.ReadInstalledManifest(folder);
            Assert.Empty(installed.Entries);
            Assert.Equal(read.Groups.Select(g => (g.Name, g.Index, g.Priority, g.DefaultSettings)),
                         installed.Groups.Select(g => (g.Name, g.Index, g.Priority, g.DefaultSettings)));
            Assert.Equal(2, installed.Groups.Single(g => g.Name == "Alpha").Priority);
            Assert.Equal(1ul, installed.Groups.Single(g => g.Name == "Beta").DefaultSettings);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>[EDGE-imported-imc-toggles-10] A malformed installed manifest throws, as Read does.</summary>
    [Fact]
    public void ReadInstalledManifest_throws_on_a_malformed_manifest()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "meta.json"), "{ not json");
            Assert.Throws<InvalidDataException>(() => PenumbraPackage.ReadInstalledManifest(dir));
            Assert.Throws<InvalidDataException>(() => PenumbraPackage.ReadInstalledManifest(Path.Combine(dir, "gone")));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>[EDGE-imported-imc-toggles-16] A Multi option past bit 31 is never kept or copied.</summary>
    [Fact]
    public void A_multi_option_past_bit_31_is_never_kept_or_copied()
    {
        var options = Enumerable.Range(0, 33).Select(i => new ImcOption($"o{i}", 0x3FD)).ToArray();
        using var built = ImcInOptions([new("Many", "Multi", 0, 1ul << 32, options)]);
        var preview = ContentImportService.Inspect(built.Path);

        Assert.False(ContentImportService.ImcToggleDefaults(preview).ContainsKey("Many"));
        Assert.Empty(ContentImportService.ImportSelection(preview, Source(("Many", ["o32"])))["Many"]);
    }

    /// <summary>[EDGE-imported-imc-toggles-29] Without the source's settings, an installed import gets the pack's defaults.</summary>
    [Fact]
    public void Without_source_settings_the_pack_defaults_apply()
    {
        using var built = ImcInOptions([new("Parts", "Multi", 0, 0b1, O("Belt", 0x3FD))]);
        var preview = ContentImportService.Inspect(built.Path);
        var pmp = ContentImportService.ImportSelection(preview);

        Assert.Equal(pmp, ContentImportService.ImportSelection(preview, null));
        Assert.Equal(pmp, ContentImportService.ImportSelection(preview, Source(("Unrelated", ["x"]))));
    }

    /// <summary>[EDGE-imported-imc-toggles-30] A Single group that also carries other content is not copied.</summary>
    [Fact]
    public void A_mixed_single_group_is_not_copied()
    {
        using var built = ImcInOptions(
            [new("Belt", "Single", 0, 0, O("On", 0x3FF), O("Big", null, Extra: GlobalRsp))]);
        var preview = ContentImportService.Inspect(built.Path);
        Assert.False(ContentImportService.ImportSelection(preview, Source(("Belt", ["Big"]))).ContainsKey("Belt"));
    }

    /// <summary>[EDGE-imported-imc-toggles-31] Names come out as the pack spells them; unknown ones are dropped.</summary>
    [Fact]
    public void Source_names_are_emitted_in_the_packs_spelling_and_unknown_ones_dropped()
    {
        using var built = ImcInOptions(
        [
            new("Stocking Toggle", "Single", 0, 0, O("On", 0x3FF), O("Off", 0x3FD)),
            new("Parts", "Multi", 0, 0, O("Belt", 0x3FD), O("Hood", 0x3FE)),
            new("Cuffs", "Single", 0, 0, O("On", 0x3FF), O("Off", 0x3FD)),
        ]);
        var preview = ContentImportService.Inspect(built.Path);

        var selection = ContentImportService.ImportSelection(preview,
            Source(("stocking toggle", ["off"]), ("Parts", ["belt", "Gone"]), ("Cuffs", ["Sideways"])));
        Assert.Equal(["Off"], selection["Stocking Toggle"]);
        Assert.Equal(["Belt"], selection["Parts"]);
        Assert.False(selection.ContainsKey("Cuffs"));
    }

    /// <summary>[EDGE-imported-imc-toggles-32] The source's enabled state plays no part: only its options are read.</summary>
    [Fact]
    public void Source_settings_are_copied_whatever_the_sources_enabled_state()
    {
        using var built = ImcInOptions([new("Belt", "Single", 0, 0, O("On", 0x3FF), O("Off", 0x3FD))]);
        var preview = ContentImportService.Inspect(built.Path);
        // ImportSelection takes the options alone; Finish reads them without looking at Enabled.
        Assert.Equal(["Off"], ContentImportService.ImportSelection(preview, Source(("Belt", ["Off"])))["Belt"]);
        Assert.DoesNotContain(".Enabled",
            SourceText.Method("Proteus/Services/ContentImportService.cs", "private ImportResult Finish("));
    }

    /// <summary>[EDGE-imported-imc-toggles-34] Empty "Show" plus a toggle "Hide" is a toggle group; empty options alone are not.</summary>
    [Fact]
    public void An_empty_show_plus_imc_hide_single_group_is_copied()
    {
        using var built = ImcInOptions(
        [
            new("Belt", "Single", 0, 0, O("Show", null), O("Hide", 0x3FD)),
            new("Nothing", "Single", 0, 0, O("A", null), O("B", null)),
        ]);
        var preview = ContentImportService.Inspect(built.Path);

        var selection = ContentImportService.ImportSelection(preview, Source(("Belt", ["Hide"]), ("Nothing", ["B"])));
        Assert.Equal(["Hide"], selection["Belt"]);
        Assert.False(selection.ContainsKey("Nothing"));
    }
}
