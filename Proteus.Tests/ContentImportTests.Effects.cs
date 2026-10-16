using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Item-bound effects: a pack (Frost Veil) whose accessory is an invisible placeholder and whose whole look is an
/// animated effect, spawned by an IMC <c>VfxId</c> on the item it replaces.
/// </summary>
public partial class ContentImportTests
{
    private const string FrostVa = "chara/accessory/a0002/vfx/eff/va0001.avfx";

    /// <summary>Every manifest of a written mod, both layouts, as (owner, file) pairs: default data and each option.</summary>
    private static List<JsonObject> Owners(string root)
    {
        var owners = new List<JsonObject>();
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!.AsObject();
        void Group(JsonObject g)
        {
            if (g["Options"] is JsonArray opts)
                foreach (var o in opts) owners.Add(o!.AsObject());
        }
        if (meta["DefaultData"] is JsonObject dd) owners.Add(dd);
        if (meta["Groups"] is JsonArray groups) foreach (var g in groups) Group(g!.AsObject());
        var legacy = Path.Combine(root, "default_mod.json");
        if (File.Exists(legacy)) owners.Add(JsonNode.Parse(File.ReadAllText(legacy))!.AsObject());
        foreach (var f in Directory.GetFiles(root, "group_*.json"))
            Group(JsonNode.Parse(File.ReadAllText(f))!.AsObject());
        return owners;
    }

    private static JsonObject OptionNamed(string root, string name)
        => Owners(root).First(o => (string?)o["Name"] == name);

    private static IEnumerable<string> FileKeys(JsonObject owner)
        => owner["Files"] is JsonObject f ? f.Select(p => p.Key) : [];

    private static IEnumerable<JsonObject> Manips(JsonObject owner, string type)
        => owner["Manipulations"] is JsonArray m
            ? m.Select(x => x!.AsObject()).Where(x => (string?)x["Type"] == type)
            : [];

    [Fact]
    public void An_effect_only_piece_is_importable_and_marked_effect_only()   // [AC-item-vfx-host-1]
    {
        using var pack = SyntheticPack.EffectBearing();
        var preview = ContentImportService.Inspect(pack.Path);

        var unit = Assert.Single(preview.Units);
        Assert.True(unit.Import);
        Assert.True(unit.EffectOnly);
        Assert.True(Assert.Single(unit.Variants).Degenerate);
        Assert.Equal(15, unit.Effect!.Variant);
        Assert.Equal(1, unit.Effect.VfxId);
        Assert.Equal(FrostVa, unit.Effect.GamePath);
        Assert.Equal(1, preview.ImportableUnits);

        var piece = Assert.Single(Assert.Single(
            ContentImportService.BuildSidecar(preview, "Veil", "Someone").ContentGroups!).Options.Single().Pieces);
        Assert.True(piece.EffectOnly);
        Assert.Equal(FrostVa, piece.Effect!.GamePath);
    }

    [Theory]   // [AC-item-vfx-host-3] [EDGE-item-vfx-host-13] [EDGE-item-vfx-host-14] [EDGE-item-vfx-host-9]
    [InlineData(false)]
    [InlineData(true)]
    public void The_leftover_mod_stops_swapping_the_vanilla_item(bool legacy)
    {
        var dir = TempDir();
        try
        {
            using var pack = SyntheticPack.EffectBearing(legacy: legacy);
            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Veil", "Someone", ContentImportService.Inspect(pack.Path));

            var owners = Owners(root);
            var keys = owners.SelectMany(FileKeys).ToList();

            // Nothing of the item is left for Penumbra: model, materials, textures, the effect.
            Assert.DoesNotContain(keys, k => k.StartsWith("chara/accessory/a0002/", StringComparison.OrdinalIgnoreCase));
            // Its IMC edit (otherwise vanilla only with no game data to tell) stays with the effect zeroed, every other
            // field as the pack wrote it.
            var imc = Assert.Single(owners.SelectMany(o => Manips(o, "Imc")));
            var entry = imc["Manipulation"]!["Entry"]!.AsObject();
            Assert.Equal(0, (int)entry["VfxId"]!);
            Assert.Equal(15, (int)entry["MaterialId"]!);
            Assert.Equal(1023, (int)entry["AttributeAndSound"]!);
            Assert.Equal(1023, (int)entry["AttributeMask"]!);
            Assert.Equal(0, (int)entry["DecalId"]!);
            Assert.Equal(0, (int)entry["SoundId"]!);
            Assert.Equal(0, (int)entry["MaterialAnimationId"]!);
            Assert.Equal(15, (int)imc["Manipulation"]!["Variant"]!);
            Assert.Equal(2, (int)imc["Manipulation"]!["PrimaryId"]!);
            // Its race routing ("SetId": "2", a string) is gone.
            Assert.Empty(owners.SelectMany(o => Manips(o, "Eqdp")));

            // Everything outside the item is the pack's still.
            Assert.Single(owners.SelectMany(o => Manips(o, "GlobalEqp")));
            Assert.Contains("vfx/FroVeil/FroVeilDustT.atex", keys);
            Assert.Contains("vfx/FrostVeilVariant1.avfx", keys);
            Assert.Equal(["vfx/FroVeil/FroVeilDustT.atex"], FileKeys(OptionNamed(root, "Off")));

            if (legacy)
            {
                // A v3 folder is only edited in the files it already has, never authored in the new layout.
                var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!.AsObject();
                Assert.Null(meta["Groups"]);
                Assert.Null(meta["DefaultData"]);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Effect_sources_follow_the_packs_option_order()   // [AC-item-vfx-host-4] [EDGE-item-vfx-host-7]
    {
        var dir = TempDir();
        try
        {
            using var pack = SyntheticPack.EffectBearing();
            var preview = ContentImportService.Inspect(pack.Path);
            var effect = Assert.Single(preview.Units).Effect!;

            Assert.Equal(
                [(null, null, "vfx/va0001.avfx"), ("Style", "Arcane Infusion", "vfx/variant1.avfx"),
                 ("Style", "Ice Candies", "vfx/variant2.avfx")],
                effect.Sources.Select(s => (s.Group, s.Option, s.File)).ToList());

            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Veil", "Someone", preview);

            var ice = ContentPieceResolver.SelectedMaterialFile(root, effect.Sources,
                new Dictionary<string, List<string>> { ["Style"] = ["Ice Candies"] });
            var plain = ContentPieceResolver.SelectedMaterialFile(root, effect.Sources, null);
            Assert.Equal(Path.Combine(root, "vfx", "variant2.avfx"), ice);
            Assert.Equal(Path.Combine(root, "vfx", "va0001.avfx"), plain);
            Assert.NotEqual(ice, plain);   // a different style is a different file, which is what redraws
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_geometry_piece_with_an_effect_keeps_its_geometry()   // [EDGE-item-vfx-host-1]
    {
        var dir = TempDir();
        try
        {
            using var pack = SyntheticPack.EffectBearing(geometry: true);
            var preview = ContentImportService.Inspect(pack.Path);
            var unit = Assert.Single(preview.Units);
            Assert.False(unit.EffectOnly);
            Assert.NotNull(unit.Effect);
            Assert.False(Assert.Single(unit.Variants).Degenerate);
            Assert.True(unit.Variants[0].Vertices > 0);

            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Veil", "Someone", preview);
            var keys = Owners(root).SelectMany(FileKeys).ToList();
            // Only the model and the effect leave; its materials and textures stay the pack's, as for any import.
            Assert.DoesNotContain("chara/accessory/a0002/model/c0101a0002_wrs.mdl", keys);
            Assert.DoesNotContain(FrostVa, keys);
            Assert.Contains("chara/accessory/a0002/material/v0015/mt_c0101a0002_wrs_a.mtrl", keys);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_degenerate_model_without_an_effect_imports_as_before()   // [EDGE-item-vfx-host-2]
    {
        var dir = TempDir();
        try
        {
            using var pack = SyntheticPack.EffectBearing(effect: false);
            var preview = ContentImportService.Inspect(pack.Path);
            var unit = Assert.Single(preview.Units);
            Assert.Null(unit.Effect);
            Assert.False(unit.EffectOnly);

            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Veil", "Someone", preview);
            var owners = Owners(root);
            Assert.Equal(2, owners.SelectMany(o => Manips(o, "Eqdp")).Count());
            Assert.Contains("chara/accessory/a0002/material/v0015/mt_c0101a0002_wrs_a.mtrl",
                owners.SelectMany(FileKeys));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_missing_effect_file_is_warned_not_fatal()   // [EDGE-item-vfx-host-10]
    {
        using var pack = SyntheticPack.EffectBearing(avfxPresent: false);
        var preview = ContentImportService.Inspect(pack.Path);
        Assert.True(Assert.Single(preview.Units).Import);
        Assert.Contains(preview.Warnings, w => w.Contains("vfx/va0001.avfx", StringComparison.Ordinal));
    }

    [Fact]
    public void An_effect_only_piece_with_an_unbound_material_is_left_to_the_pack()   // [EDGE-item-vfx-host-18]
    {
        var dir = TempDir();
        try
        {
            using var pack = SyntheticPack.EffectBearing(materialPresent: false);
            var preview = ContentImportService.Inspect(pack.Path);
            var unit = Assert.Single(preview.Units);
            Assert.False(unit.Import);

            // Nothing can be worn, so nothing of the effect is taken away: the pack keeps working as built.
            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Veil", "Someone", preview);
            var owners = Owners(root);
            Assert.Contains(FrostVa, owners.SelectMany(FileKeys));
            Assert.Equal(1, (int)owners.SelectMany(o => Manips(o, "Imc")).Single()["Manipulation"]!["Entry"]!["VfxId"]!);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_effect_declared_in_default_data_is_found()   // [EDGE-item-vfx-host-20]
    {
        var dir = TempDir();
        try
        {
            using var pack = SyntheticPack.EffectBearing(imcInDefault: true);
            var preview = ContentImportService.Inspect(pack.Path);
            Assert.Equal(1, Assert.Single(preview.Units).Effect!.VfxId);

            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Veil", "Someone", preview);
            var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!.AsObject();
            var imc = Assert.Single(Manips(meta["DefaultData"]!.AsObject(), "Imc"));
            Assert.Equal(0, (int)imc["Manipulation"]!["Entry"]!["VfxId"]!);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_effect_with_no_file_of_its_own_is_left_on_the_item()   // [EDGE-item-vfx-host-10]
    {
        var dir = TempDir();
        try
        {
            // The pack switches on one of the game's effects for the item and ships no .avfx: nothing to carry, so the
            // item keeps it exactly as the pack set it.
            using var pack = SyntheticPack.EffectBearing(avfxRedirected: false);
            var preview = ContentImportService.Inspect(pack.Path);
            var unit = Assert.Single(preview.Units);
            Assert.Null(unit.Effect);
            Assert.False(unit.EffectOnly);

            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Veil", "Someone", preview);
            var owners = Owners(root);
            Assert.Equal(1, (int)owners.SelectMany(o => Manips(o, "Imc")).Single()["Manipulation"]!["Entry"]!["VfxId"]!);
            Assert.Equal(2, owners.SelectMany(o => Manips(o, "Eqdp")).Count());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void One_race_failing_does_not_stop_a_piece_being_an_effect()   // [AC-item-vfx-host-6]
    {
        var empty = new Dictionary<string, string>();
        var bound = new Dictionary<string, string> { ["mt_c0101a0002_wrs_a.mtrl"] = "items/v0001.mtrl" };
        var slot = ContentSlot.Parse("chara/accessory/a0002/model/c0101a0002_wrs.mdl")!.Value;
        var placeholder = new ContentImportService.PiecePlan("p", "e", bound, [], 1, 3, null, "0101", Degenerate: true);
        var unbound = new ContentImportService.PiecePlan("p", "e2", empty, ["mt_x.mtrl"], 0, 0, "unbound", "1401");
        var effect = new ContentEffect { VfxId = 1 };

        Assert.True(new ContentImportService.PieceUnit(null, null, slot, null, null, [placeholder, unbound], effect).EffectOnly);
        Assert.False(new ContentImportService.PieceUnit(null, null, slot, null, null, [unbound], effect).EffectOnly);
        var real = placeholder with { Degenerate = false };
        Assert.False(new ContentImportService.PieceUnit(null, null, slot, null, null, [placeholder, real], effect).EffectOnly);
    }

    [Fact]
    public void Only_the_pieces_own_item_and_effect_leave_the_pack()   // [EDGE-item-vfx-host-14] [AC-item-vfx-host-3]
    {
        var dir = TempDir();
        try
        {
            using var pack = SyntheticPack.EffectBearing(neighbours: true);
            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Veil", "Someone", ContentImportService.Inspect(pack.Path));
            var owners = Owners(root);
            var keys = owners.SelectMany(FileKeys).ToList();

            // The bracelet's own files go…
            Assert.DoesNotContain("chara/accessory/a0002/material/v0015/mt_c0101a0002_wrs_a.mtrl", keys);
            Assert.DoesNotContain("chara/accessory/a0002/texture/v01_c0101a0002_wrs_d.tex", keys);
            // …but one set folder holds every slot: the necklace's material, and a texture the effect may load by path, stay.
            Assert.Contains("chara/accessory/a0002/material/v0001/mt_c0101a0002_nek_a.mtrl", keys);
            Assert.Contains("chara/accessory/a0002/vfx/texture/sparkle.atex", keys);

            // The recorded effect is neutralised; a second effect on the same item is left as the pack wrote it.
            var imcs = owners.SelectMany(o => Manips(o, "Imc")).Select(m => m["Manipulation"]!).ToList();
            Assert.Equal(0, (int)imcs.Single(m => (int)m["Variant"]! == 15)["Entry"]!["VfxId"]!);
            Assert.Equal(2, (int)imcs.Single(m => (int)m["Variant"]! == 3)["Entry"]!["VfxId"]!);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_imc_edit_that_differs_from_vanilla_keeps_all_but_its_effect()   // [EDGE-item-vfx-host-13]
    {
        var dir = TempDir();
        try
        {
            using var pack = SyntheticPack.EffectBearing();
            // The game's v15 entry uses material 14, not the pack's 15: the pack's edit is more than the effect, so it stays.
            var imcFile = new byte[4 + 16 * 5 * 6];
            BitConverter.GetBytes((ushort)15).CopyTo(imcFile, 0);
            BitConverter.GetBytes((ushort)31).CopyTo(imcFile, 2);
            int at = 4 + (15 * 5 + 2) * 6;
            imcFile[at] = 14;
            BitConverter.GetBytes((ushort)0x3FF).CopyTo(imcFile, at + 2);

            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Veil", "Someone", ContentImportService.Inspect(pack.Path),
                readGameFile: p => p == "chara/accessory/a0002/a0002.imc" ? imcFile : null);
            var entry = Owners(root).SelectMany(o => Manips(o, "Imc")).Single()["Manipulation"]!["Entry"]!;
            Assert.Equal(0, (int)entry["VfxId"]!);
            Assert.Equal(15, (int)entry["MaterialId"]!);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_imc_edit_that_is_vanilla_but_for_its_effect_is_removed()   // [AC-item-vfx-host-3] [EDGE-item-vfx-host-13]
    {
        var dir = TempDir();
        try
        {
            using var pack = SyntheticPack.EffectBearing();
            // The game's a0002.imc with variant 15 exactly as the pack's entry but VfxId 0: material 15, all ten bits.
            var imcFile = new byte[4 + 16 * 5 * 6];
            BitConverter.GetBytes((ushort)15).CopyTo(imcFile, 0);
            BitConverter.GetBytes((ushort)31).CopyTo(imcFile, 2);
            int at = 4 + (15 * 5 + 2) * 6;
            imcFile[at] = 15;
            BitConverter.GetBytes((ushort)0x3FF).CopyTo(imcFile, at + 2);

            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Veil", "Someone", ContentImportService.Inspect(pack.Path),
                readGameFile: p => p == "chara/accessory/a0002/a0002.imc" ? imcFile : null);
            Assert.Empty(Owners(root).SelectMany(o => Manips(o, "Imc")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
