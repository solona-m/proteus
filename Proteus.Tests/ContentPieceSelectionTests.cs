using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Picking one garment out of a pack that ships a whole outfit.
/// <para/>
/// Two things have to hold at once. A pack with no options of its own has to become selectable — which is
/// what the synthesized group is for — and a pack whose own options already select one garment each must not
/// grow a second selector it never needed. The Cerise-shaped fixture covers the first; the piercings-shaped
/// one in <see cref="ContentImportTests"/> covers the second.
/// </summary>
public class ContentPieceSelectionTests
{
    private const string SamplePack = @"E:\ModPacks\Neolithe Piercings for Proteus.pmp";

    /// <summary>A real .mdl, so the parse and the material binding are exercised rather than stubbed.</summary>
    private static byte[]? SampleModel()
    {
        if (!File.Exists(SamplePack)) return null;
        using var zip = ZipFile.OpenRead(SamplePack);
        var e = zip.GetEntry("top/belly button heart/chara/equipment/e0000/model/c0201e0000_top.mdl");
        if (e == null) return null;
        using var st = e.Open();
        using var ms = new MemoryStream();
        st.CopyTo(ms);
        return ms.ToArray();
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "proteus-pieces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// Cerise's shape: several models, no option groups at all, and most of them race variants of one
    /// garment. Eight files, three garments.
    /// </summary>
    private static string CerisePack(string dir, byte[] model, byte[] mtrl, string leaf)
    {
        // Every model declares the same material leaf, because they all came from the one real model. Real
        // packs name theirs per race; that difference is exercised by the binding, not by the collapsing.
        var files = new List<(string, byte[])> { ("common/piercings.mtrl", mtrl) };
        var redirects = new List<string> { $"\"chara/x/{leaf}\": \"common\\\\piercings.mtrl\"" };

        void Model(string race, string set, string slot)
        {
            var entry = $"{set}/{race}/model.mdl";
            files.Add((entry, model));
            redirects.Add($"\"chara/equipment/{set}/model/c{race}{set}_{slot}.mdl\": \"{entry.Replace("/", "\\\\")}\"");
        }

        Model("0201", "e6085", "met");                                  // the jacket - one race
        Model("0201", "e6010", "top"); Model("1201", "e6010", "top");   // two races
        foreach (var r in new[] { "0101", "0201", "0301", "0901", "1101" })
            Model(r, "e6025", "top");                                   // five races

        var manifest = "{\n  \"FileVersion\": 4,\n  \"Name\": \"Cerise\",\n  \"Author\": \"Solona\",\n"
                     + "  \"DefaultData\": { \"Files\": {\n    " + string.Join(",\n    ", redirects) + "\n  } },\n"
                     + "  \"Groups\": []\n}";

        var path = Path.Combine(dir, "cerise.pmp");
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        void Add(string name, byte[] data)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(data, 0, data.Length);
        }
        Add("meta.json", Encoding.UTF8.GetBytes(manifest));
        foreach (var (n, d) in files) Add(n, d);
        return path;
    }

    /// <summary>
    /// The Cyr shape: ONE garment shipped twice — a base model per race in default data, plus a size group
    /// that overrides the one race it was fitted to. Three races unconditionally, two size options over
    /// <c>c0201</c>.
    /// <para/>
    /// Penumbra resolves one file per game path and an option outranks the default, so its <c>c0201</c>
    /// default copy never rendered. Reading it as a piece of its own put the shorts on the character twice
    /// and left the size copy ungated, which is what made the piece checkbox inert.
    /// </summary>
    /// <param name="groupType">
    /// "Single" for the size-selector shape. "Multi" makes the override OPTIONAL, which is the case the
    /// default copy must survive — a multi-select option can be off, and the import turns every one of them
    /// off, so there the default is the copy that renders.
    /// </param>
    /// <param name="breakOneOption">
    /// Give the second option an unreadable model, so Proteus refuses it. The default copy must then survive
    /// too: with it gone, selecting that size would wear nothing at all.
    /// </param>
    private static string SizeGroupPack(
        string dir, byte[] model, byte[] mtrl, string leaf,
        string groupType = "Single", bool breakOneOption = false)
    {
        var files = new List<(string, byte[])> { ("common/shorts.mtrl", mtrl) };
        var defaults = new List<string> { $"\"chara/x/{leaf}\": \"common\\\\shorts.mtrl\"" };

        const string Path0201 = "chara/equipment/e6058/model/c0201e6058_dwn.mdl";

        foreach (var r in new[] { "0101", "0201", "1101" })
        {
            var entry = $"default group/{r}.mdl";
            files.Add((entry, model));
            defaults.Add($"\"chara/equipment/e6058/model/c{r}e6058_dwn.mdl\": \"{entry.Replace("/", "\\\\")}\"");
        }

        var options = new List<string>();
        foreach (var size in new[] { "WC", "Mini" })
        {
            var entry = $"shorts size/{size}/model.mdl";
            files.Add((entry, breakOneOption && size == "Mini" ? new byte[8] : model));
            options.Add("{ \"Name\": \"" + size + "\", \"Files\": { \"" + Path0201 + "\": \""
                      + entry.Replace("/", "\\\\") + "\" } }");
        }

        var manifest = "{\n  \"FileVersion\": 4,\n  \"Name\": \"Sized\",\n  \"Author\": \"Cyr\",\n"
                     + "  \"DefaultData\": { \"Files\": {\n    " + string.Join(",\n    ", defaults) + "\n  } },\n"
                     + "  \"Groups\": [ { \"Name\": \"Shorts Size\", \"Type\": \"" + groupType
                     + "\", \"Options\": [ " + string.Join(", ", options) + " ] } ]\n}";

        var path = Path.Combine(dir, "sized.pmp");
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        void Add(string name, byte[] data)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(data, 0, data.Length);
        }
        Add("meta.json", Encoding.UTF8.GetBytes(manifest));
        foreach (var (n, d) in files) Add(n, d);
        return path;
    }

    // ── collapsing and gating ────────────────────────────────────────────────

    [Fact]
    public void Race_variants_collapse_into_one_pickable_piece()
    {
        var model = SampleModel();
        if (model == null) return;

        var dir = TempDir();
        try
        {
            var leaf = SecondSkinService
                .UsedMaterialNames(model, SecondSkinWriter.MaterialNames(model))[0].TrimStart('/');
            var preview = ContentImportService.Inspect(CerisePack(dir, model, new byte[64], leaf));

            // Eight models, three garments. Listing the models would put five duplicates of one shirt in
            // front of the user, four of them wrong for whoever is wearing it.
            Assert.Equal(3, preview.TotalUnits);
            Assert.Equal(3, preview.ImportableUnits);
            Assert.Equal(new[] { 1, 2, 5 }, preview.Units.Select(u => u.Variants.Count).OrderBy(n => n).ToArray());

            // Nothing else selects them, so every one gets an entry in a group the import adds.
            Assert.NotNull(preview.PieceGroupName);
            Assert.Equal(ContentImportService.PieceGroup, preview.PieceGroupName);
            Assert.All(preview.Units, u => Assert.NotNull(u.GateOption));
            Assert.Equal(3, preview.GateOptions.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Pieces_are_named_by_slot_and_the_item_they_replace()
    {
        var model = SampleModel();
        if (model == null) return;

        var dir = TempDir();
        try
        {
            var leaf = SecondSkinService
                .UsedMaterialNames(model, SecondSkinWriter.MaterialNames(model))[0].TrimStart('/');
            var pack = CerisePack(dir, model, new byte[64], leaf);

            // With the game's Item sheet available, a piece is named after the item it replaces.
            string? Names(int category, int set) => (category, set) switch
            {
                (3, 6085) => "Far Eastern Schoolgirl's Hair Ribbon",
                (4, 6010) => "Extreme Survival Shirt",
                (4, 6025) => "Gryphonskin Breastguard",
                _         => null,
            };
            var named = ContentImportService.Inspect(pack, itemName: Names);
            Assert.Equal(
                new[] { "Body — Extreme Survival Shirt",
                        "Body — Gryphonskin Breastguard",
                        "Head — Far Eastern Schoolgirl's Hair Ribbon" },
                named.Units.Select(u => u.Label).OrderBy(x => x, StringComparer.Ordinal).ToArray());

            // Without it, the set id carries the label instead — never a blank one.
            var unnamed = ContentImportService.Inspect(pack);
            Assert.Equal(
                new[] { "Body — e6010", "Body — e6025", "Head — e6085" },
                unnamed.Units.Select(u => u.Label).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_synthesized_group_is_written_multi_select_with_nothing_on()
    {
        var model = SampleModel();
        if (model == null) return;

        var dir = TempDir();
        try
        {
            var leaf = SecondSkinService
                .UsedMaterialNames(model, SecondSkinWriter.MaterialNames(model))[0].TrimStart('/');
            var preview = ContentImportService.Inspect(CerisePack(dir, model, new byte[64], leaf));

            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Cerise", "Solona", preview);

            var manifest = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!;
            var group = ((JsonArray)manifest["Groups"]!)
                .Cast<JsonObject>()
                .Single(g => (string?)g["Name"] == ContentImportService.PieceGroup);

            Assert.Equal("Multi", (string?)group["Type"]);
            // Zero, not a bitmask: an imported outfit contributes nothing until the user asks for a piece.
            Assert.Equal(0, (long?)group["DefaultSettings"]);
            Assert.Equal(3, ((JsonArray)group["Options"]!).Count);

            // And the sidecar's pieces each name the option that switches them on.
            var meta = JsonSerializer.Deserialize<ProteusMetadata>(
                File.ReadAllText(Path.Combine(root, "Proteus", "metadata.json")), ProteusJson.MetadataRead)!;
            Assert.Equal(ContentImportService.PieceGroup, meta.PieceGroupName);
            Assert.Equal(3, meta.Content!.Count);
            Assert.All(meta.Content, p => Assert.NotNull(p.GateOption));

            // The five-race shirt kept every variant, keyed by the code that picks between them.
            var shirt = meta.Content.Single(p => p.Models is { Count: 5 });
            Assert.Equal(new[] { "0101", "0201", "0301", "0901", "1101" },
                shirt.Models!.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.Equal("Body", shirt.Slot);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_group_name_the_author_already_used_is_not_clobbered()
    {
        var dir = TempDir();
        try
        {
            // Writing a group REPLACES any of the same name, so colliding would destroy the author's.
            var manifest = "{\n  \"FileVersion\": 4,\n  \"Name\": \"P\",\n  \"DefaultData\": { \"Files\": {} },\n"
                         + "  \"Groups\": [ { \"Name\": \"" + ContentImportService.PieceGroup
                         + "\", \"Type\": \"Single\", \"Options\": [] } ]\n}";
            var path = Path.Combine(dir, "p.pmp");
            using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            using (var s = zip.CreateEntry("meta.json").Open())
            {
                var b = Encoding.UTF8.GetBytes(manifest);
                s.Write(b, 0, b.Length);
            }

            var pack = PenumbraPackage.Read(path);
            Assert.Single(pack.Groups);
            Assert.Equal(ContentImportService.PieceGroup, pack.Groups[0].Name);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── a garment shipped both unconditionally and per size ──────────────────

    [Fact]
    public void A_default_model_an_option_replaces_is_dropped()
    {
        var model = SampleModel();
        if (model == null) return;

        var dir = TempDir();
        try
        {
            var leaf = SecondSkinService
                .UsedMaterialNames(model, SecondSkinWriter.MaterialNames(model))[0].TrimStart('/');
            var preview = ContentImportService.Inspect(SizeGroupPack(dir, model, new byte[64], leaf));

            // One unconditional unit and one per size option — never a fourth for the c0201 default, which
            // Penumbra would never have loaded.
            var free = preview.Units.Single(u => u.Group == null);
            Assert.Equal(new[] { "0101", "1101" },
                free.Variants.Select(v => v.RaceCode).OrderBy(x => x, StringComparer.Ordinal).ToArray());

            // The size options are untouched: each still supplies the race it was fitted to.
            var sized = preview.Units.Where(u => u.Group == "Shorts Size").ToList();
            Assert.Equal(2, sized.Count);
            Assert.All(sized, u => Assert.Equal("0201", Assert.Single(u.Variants).RaceCode));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_optional_multi_select_override_does_not_shadow_the_default_copy()
    {
        var model = SampleModel();
        if (model == null) return;

        var dir = TempDir();
        try
        {
            var leaf = SecondSkinService
                .UsedMaterialNames(model, SecondSkinWriter.MaterialNames(model))[0].TrimStart('/');
            var preview = ContentImportService.Inspect(
                SizeGroupPack(dir, model, new byte[64], leaf, groupType: "Multi"));

            // A Multi option can be OFF, and ClearMultiSelectDefaults turns every one of them off at import,
            // so the default copy is the one that renders. Dropping it would leave a c0201 wearer with no
            // shorts at all until they found the pack's own checkbox.
            var free = preview.Units.Single(u => u.Group == null);
            Assert.Contains("0201", free.Variants.Select(v => v.RaceCode));
            Assert.Equal(new[] { "0101", "0201", "1101" },
                free.Variants.Select(v => v.RaceCode).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_option_proteus_refuses_does_not_shadow_the_default_copy()
    {
        var model = SampleModel();
        if (model == null) return;

        var dir = TempDir();
        try
        {
            var leaf = SecondSkinService
                .UsedMaterialNames(model, SecondSkinWriter.MaterialNames(model))[0].TrimStart('/');
            var preview = ContentImportService.Inspect(
                SizeGroupPack(dir, model, new byte[64], leaf, breakOneOption: true));

            // One size will not read, so its redirect stays with Penumbra and Proteus places nothing for it.
            var broken = preview.Units.Single(u => u.Option == "Mini");
            Assert.False(broken.Import);

            // The default copy therefore has to stay: selecting "Mini" with it gone would wear nothing at
            // all. Showing the garment twice is the better failure of the two.
            var free = preview.Units.Single(u => u.Group == null);
            Assert.Contains("0201", free.Variants.Select(v => v.RaceCode));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void One_checkbox_governs_a_garment_its_size_group_also_ships()
    {
        var model = SampleModel();
        if (model == null) return;

        var dir = TempDir();
        try
        {
            var leaf = SecondSkinService
                .UsedMaterialNames(model, SecondSkinWriter.MaterialNames(model))[0].TrimStart('/');
            var preview = ContentImportService.Inspect(SizeGroupPack(dir, model, new byte[64], leaf));

            // ONE option, not one for the default copy and none for the sizes. Gating only the unconditional
            // copy left the size copy ungated, and a Single group always has an option selected — so the
            // shorts were worn whatever the box said.
            Assert.Equal(ContentImportService.PieceGroup, preview.PieceGroupName);
            var gate = Assert.Single(preview.GateOptions);
            Assert.All(preview.Units, u => Assert.Equal(gate, u.GateOption));

            // …and it reaches the sidecar on both, so the runtime gate governs the whole garment.
            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Sized", "Cyr", preview);
            var meta = JsonSerializer.Deserialize<ProteusMetadata>(
                File.ReadAllText(Path.Combine(root, "Proteus", "metadata.json")), ProteusJson.MetadataRead)!;

            var sized = meta.ContentGroups!.Single().Options.SelectMany(o => o.Pieces).ToList();
            var every = meta.Content!.Concat(sized).ToList();
            Assert.Equal(3, every.Count);
            Assert.All(every, p => Assert.Equal(gate, p.GateOption));

            // Off: nothing of the garment is worn, Single-group selection notwithstanding.
            Assert.All(every, p => Assert.False(SidecarDiscoveryService.PieceIsOn(p, [])));
            // On: worn — and for the wearer's own race exactly once, because the default copy no longer
            // carries c0201 at all.
            Assert.All(every, p => Assert.True(SidecarDiscoveryService.PieceIsOn(p, [gate])));
            Assert.Equal(1, every.Count(p => p.ModelFor("0201") != null));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_shadowed_default_redirect_is_stripped_from_the_copied_manifest()
    {
        var model = SampleModel();
        if (model == null) return;

        var dir = TempDir();
        try
        {
            var leaf = SecondSkinService
                .UsedMaterialNames(model, SecondSkinWriter.MaterialNames(model))[0].TrimStart('/');
            var preview = ContentImportService.Inspect(SizeGroupPack(dir, model, new byte[64], leaf));

            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Sized", "Cyr", preview);

            var manifest = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!;
            var defaults = (JsonObject)((JsonObject)manifest["DefaultData"]!)["Files"]!;

            // The option's redirect is stripped because Proteus took that model over. Leaving the default's
            // behind would promote it to winner and republish the geometry Proteus now appends itself.
            Assert.DoesNotContain("chara/equipment/e6058/model/c0201e6058_dwn.mdl", defaults.Select(p => p.Key));

            // The races no option claimed are Proteus's too, and go the same way.
            Assert.DoesNotContain("chara/equipment/e6058/model/c0101e6058_dwn.mdl", defaults.Select(p => p.Key));
            Assert.DoesNotContain("chara/equipment/e6058/model/c1101e6058_dwn.mdl", defaults.Select(p => p.Key));

            // The material is not a model and was never taken — it must still publish.
            Assert.Contains($"chara/x/{leaf}", defaults.Select(p => p.Key));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_garment_that_lives_only_in_a_group_still_gets_no_checkbox_of_ours()
    {
        var model = SampleModel();
        if (model == null) return;

        var dir = TempDir();
        try
        {
            var leaf = SecondSkinService
                .UsedMaterialNames(model, SecondSkinWriter.MaterialNames(model))[0].TrimStart('/');

            // Same pack minus the default-data models: the author's own option already selects the garment,
            // so a switch of ours would be a box to tick before any of theirs meant anything.
            var files = new List<(string, byte[])> { ("common/shorts.mtrl", new byte[64]) };
            var options = new List<string>();
            foreach (var size in new[] { "WC", "Mini" })
            {
                var entry = $"shorts size/{size}/model.mdl";
                files.Add((entry, model));
                options.Add("{ \"Name\": \"" + size + "\", \"Files\": { "
                          + "\"chara/equipment/e6058/model/c0201e6058_dwn.mdl\": \""
                          + entry.Replace("/", "\\\\") + "\" } }");
            }
            var manifest = "{\n  \"FileVersion\": 4,\n  \"Name\": \"Sized\",\n"
                         + "  \"DefaultData\": { \"Files\": { \"chara/x/" + leaf + "\": \"common\\\\shorts.mtrl\" } },\n"
                         + "  \"Groups\": [ { \"Name\": \"Shorts Size\", \"Type\": \"Single\", \"Options\": [ "
                         + string.Join(", ", options) + " ] } ]\n}";

            var path = Path.Combine(dir, "grouponly.pmp");
            using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            {
                void Add(string name, byte[] data)
                {
                    using var s = zip.CreateEntry(name).Open();
                    s.Write(data, 0, data.Length);
                }
                Add("meta.json", Encoding.UTF8.GetBytes(manifest));
                foreach (var (n, d) in files) Add(n, d);
            }

            var preview = ContentImportService.Inspect(path);
            Assert.Equal(2, preview.ImportableUnits);
            Assert.All(preview.Units, u => Assert.Null(u.GateOption));
            Assert.Null(preview.PieceGroupName);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── the runtime gate ─────────────────────────────────────────────────────

    [Fact]
    public void An_ungated_piece_needs_no_penumbra_at_all()
    {
        // The service is built with a null bridge here: a pack of plain unconditional pieces must resolve
        // without one IPC hop, which is what keeps the common case cheap.
        var piece = new ContentPiece { Model = "m.mdl" };
        var meta = new ProteusMetadata { Content = [piece] };
        var entry = new OverlayEntry("mod1", "Mod 1", 10, true, meta, "/tmp/mod1/Proteus");

        var resolved = new SidecarDiscoveryService(null!, NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>())
            .ResolveActiveContent(entry);

        Assert.Same(piece, Assert.Single(resolved).Piece);
    }

    [Fact]
    public void A_gated_piece_is_worn_only_when_its_option_is_ticked()
    {
        var gated = new ContentPiece { Model = "m.mdl", GateOption = "Head — Hair Ribbon" };
        var free  = new ContentPiece { Model = "m.mdl" };

        Assert.True(SidecarDiscoveryService.PieceIsOn(gated, ["Head — Hair Ribbon"]));
        Assert.True(SidecarDiscoveryService.PieceIsOn(gated, ["Body — Shirt", "Head — Hair Ribbon"]));
        Assert.False(SidecarDiscoveryService.PieceIsOn(gated, ["Body — Shirt"]));
        Assert.False(SidecarDiscoveryService.PieceIsOn(gated, []));

        // Unreadable selection: everything gated stays off rather than being worn unasked.
        Assert.False(SidecarDiscoveryService.PieceIsOn(gated, null));

        // A piece with no gate is never gated, whatever the group says.
        Assert.True(SidecarDiscoveryService.PieceIsOn(free, null));
        Assert.True(SidecarDiscoveryService.PieceIsOn(free, []));
    }

    [Fact]
    public void Two_garments_sharing_one_material_are_not_collapsed_into_one()
    {
        // A pack whose pieces all bind the SAME material — the piercings pack is exactly that — publishes
        // ONE material for them and spends one of the host's ten slots, not one each.
        const string Mtrl = "common/1/piercings.mtrl";
        const string Leaf = "/mt_c0201b0001_neolithe_piercings.mtrl";
        var body = new ShellSurfaceKey(ShellSurfaceKind.Body, string.Empty);

        var belly = SecondSkinService.ContentUnitKey("mod", body, Mtrl, null);
        var hip   = SecondSkinService.ContentUnitKey("mod", body, Mtrl, null);
        Assert.Equal(belly, hip);

        // …but they are still two MESHES inside it, which is what the earlier regression got wrong: keying
        // the geometry on ContentPiece.Model collapsed them, because an imported piece's path lives in
        // Models (it names a race) and leaves Model blank.
        var imported = new ContentPiece { Models = new Dictionary<string, string> { ["0201"] = "top/heart/model.mdl" } };
        Assert.Equal(string.Empty, imported.Model);
        Assert.Equal("top/heart/model.mdl", imported.ModelFor("0201"));

        Assert.NotEqual(
            SecondSkinService.ContentGeometryKey("top/heart/model.mdl", Leaf),
            SecondSkinService.ContentGeometryKey("bottom/hip/model.mdl", Leaf));
        // The same mesh named twice really is one mesh.
        Assert.Equal(
            SecondSkinService.ContentGeometryKey("top/heart/model.mdl", Leaf),
            SecondSkinService.ContentGeometryKey("top/heart/model.mdl", Leaf));
    }

    [Fact]
    public void A_material_is_shared_only_by_pieces_that_would_publish_it_identically()
    {
        const string Mtrl = "common/1/piercings.mtrl";
        var body = new ShellSurfaceKey(ShellSurfaceKind.Body, string.Empty);
        var baseline = SecondSkinService.ContentUnitKey("mod", body, Mtrl, null);

        // Different colours really are a different material, and legitimately cost two slots.
        Assert.NotEqual(baseline, SecondSkinService.ContentUnitKey("mod", body, Mtrl, "[{\"Row\":1}]"));

        // A different .mtrl is a different material.
        Assert.NotEqual(baseline, SecondSkinService.ContentUnitKey("mod", body, "common/1/other.mtrl", null));

        // Another mod's identical file is still its own — a shared slot across mods would make one mod's
        // colour edit reach into another's.
        Assert.NotEqual(baseline, SecondSkinService.ContentUnitKey("other", body, Mtrl, null));

        // And a face piece cannot share with a body piece however identical the material: they are allocated
        // to different hosts, because a natively-authored face must not be race-deformed.
        Assert.NotEqual(baseline,
            SecondSkinService.ContentUnitKey("mod", new ShellSurfaceKey(ShellSurfaceKind.Face, "f0001"), Mtrl, null));
    }

    [Fact]
    public void An_animated_glow_splits_a_shared_material_and_its_numbers_are_part_of_that()
    {
        const string Mtrl = "common/1/piercings.mtrl";
        var body = new ShellSurfaceKey(ShellSurfaceKind.Body, string.Empty);
        var plain = SecondSkinService.ContentUnitKey("mod", body, Mtrl, null);

        GearSettingsPreset Glow(string scroll, float? speedX = null, float? tileX = null) => new()
        {
            Scroll = scroll, ScrollSpeedX = speedX, ScrollTilingX = tileX,
        };

        // A glow rebuilds the material onto characterscroll — a different file, so a different slot.
        var glowing = SecondSkinService.ContentUnitKey("mod", body, Mtrl, null, Glow("rainbow.png").GlowKey());
        Assert.NotEqual(plain, glowing);

        // Two options that agree on the effect AND every number still share one.
        Assert.Equal(glowing,
            SecondSkinService.ContentUnitKey("mod", body, Mtrl, null, Glow("rainbow.png").GlowKey()));

        // Differing on any of them does not. Merging on the effect name alone would silently hand one
        // option the other's speed, because only one material gets published.
        Assert.NotEqual(glowing,
            SecondSkinService.ContentUnitKey("mod", body, Mtrl, null, Glow("stars.png").GlowKey()));
        Assert.NotEqual(glowing,
            SecondSkinService.ContentUnitKey("mod", body, Mtrl, null, Glow("rainbow.png", speedX: 0.4f).GlowKey()));
        Assert.NotEqual(glowing,
            SecondSkinService.ContentUnitKey("mod", body, Mtrl, null, Glow("rainbow.png", tileX: 8f).GlowKey()));

        // A preset carrying numbers but NO effect is not a glow, and must not split a slot for nothing.
        Assert.Null(new GearSettingsPreset { ScrollSpeedX = 0.4f, ScrollTilingX = 8f }.GlowKey());
        Assert.Null(new GearSettingsPreset().GlowKey());

        // Layer, Shader and the mode pin describe an overlay descriptor, not a content material — a stray
        // value in one must not cost a second slot.
        Assert.Equal(
            new GearSettingsPreset { Scroll = "rainbow.png" }.GlowKey(),
            new GearSettingsPreset
            {
                Scroll = "rainbow.png", Layer = OverlayLayer.Gear,
                Shader = "characterscroll.shpk", ManualShaderLock = true,
            }.GlowKey());
    }

    [Fact]
    public void A_piece_is_named_by_the_switch_that_turned_it_on()
    {
        // The regression: an imported pack with no options of its own puts every model in the unconditional
        // list, so the OPTION is null and they are gated through the synthesized piece group instead.
        // Naming them after the option captioned the whole panel "always on" — untrue of all of them, and
        // identical however many were worn.
        var jacket = new ContentPiece { GateOption = "Head — Far Eastern Schoolgirl's Hair Ribbon" };
        var shirt  = new ContentPiece { GateOption = "Body — Extreme Survival Shirt" };

        Assert.Equal(
            new[] { "Head — Far Eastern Schoolgirl's Hair Ribbon", "Body — Extreme Survival Shirt" },
            ContentLabels.For([(null, new[] { jacket, shirt })], "always on").ToArray());

        // A pack whose own options already select one garment each has no gate, so the option names it.
        var plain = new ContentPiece();
        Assert.Equal(
            new[] { "Belly Button Heart", "Hip Dermals" },
            ContentLabels.For(
                [("Belly Button Heart", new[] { plain }), ("Hip Dermals", new[] { plain })],
                "always on").ToArray());

        // Only a piece with neither — a hand-authored sidecar — is genuinely always applied.
        Assert.Equal(new[] { "always on" }, ContentLabels.For([(null, new[] { plain })], "always on").ToArray());

        // One option contributing several pieces under one gate is named once, in encounter order.
        Assert.Equal(
            new[] { "Body — Extreme Survival Shirt" },
            ContentLabels.For([(null, new[] { shirt, shirt })], "always on").ToArray());
    }

    [Fact]
    public void An_index_texture_says_which_colour_cell_a_material_samples()
    {
        static byte[] Flat(byte r, byte g, int texels = 16)
        {
            var b = new byte[texels * 4];
            for (int i = 0; i < texels; i++) { b[i * 4] = r; b[i * 4 + 1] = g; b[i * 4 + 3] = 255; }
            return b;
        }

        // The shipped piercings pack: red 0, green 0 — row 1, column B. Colouring row 16 (which is what the
        // grid invited before it was filtered) changes a cell nothing reads, and the glow highlight, which
        // drives the same row, does nothing either.
        var piercings = ContentIndexTexture.Read(Flat(0, 0));
        Assert.Equal(new[] { 1 }, piercings.Rows.ToArray());
        Assert.Equal("B", piercings.SubRow);

        // Red is the row PAIR selector: 255/17 = 15, so row 16. Green 255 is column A.
        var silver = ContentIndexTexture.Read(Flat(255, 255));
        Assert.Equal(new[] { 16 }, silver.Rows.ToArray());
        Assert.Equal("A", silver.SubRow);
        Assert.Equal(new[] { 15 }, ContentIndexTexture.Read(Flat(238, 0)).Rows.ToArray());

        // Several rows in one texture are all reported, and a texture using both columns names neither —
        // a gradient genuinely reads both, and narrowing it away would be a lie.
        var mixed = new byte[Flat(0, 0).Length + Flat(255, 255).Length];
        Flat(0, 0).CopyTo(mixed, 0);
        Flat(255, 255).CopyTo(mixed, Flat(0, 0).Length);
        var scan = ContentIndexTexture.Read(mixed);
        Assert.Equal(new[] { 1, 16 }, scan.Rows.OrderBy(r => r).ToArray());
        Assert.Null(scan.SubRow);

        // Fully transparent texels select nothing at all.
        Assert.Empty(ContentIndexTexture.Read(new byte[64]).Rows);
    }

    /// <summary>
    /// A 16×16 sheet — 256 texels, so <c>ReadOverlay</c>'s floor is the flat 64 rather than 0.1% — filled
    /// from a list of (red, green, count) runs in row-major order.
    private static byte[] Sheet(params (byte R, byte G, int Count)[] runs)
    {
        var b = new byte[16 * 16 * 4];
        int t = 0;
        foreach (var (r, g, count) in runs)
            for (int i = 0; i < count; i++, t++)
            {
                b[t * 4] = r; b[t * 4 + 1] = g; b[t * 4 + 3] = 255;
            }
        return b;
    }

    /// <summary>
    /// The two readings of a red channel that Proteus holds at once, pinned against each other on real
    /// pixels.
    /// <para/>
    /// They are allowed to differ, and must: <c>RowOf</c> ROUNDS, for a pack's index where the number states
    /// its author's intent, while <c>ReadOverlay</c> TRUNCATES, for an overlay's own <c>_id</c>, because
    /// that is how <c>CompositorService.ApplyIndexedOverlay</c> bins the very same file. Tidying either into
    /// the other is a one-character edit that this has to catch — the editor DIMS the rows a scan does not
    /// name, so a binning one row out does not merely mislabel a row, it puts the working one behind a
    /// dimmed button.
    /// <para/>
    /// What keeps the divergence harmless is the round trip below: on a multiple of 17 both readings agree,
    /// and a multiple of 17 is all any importer writes.
    /// </summary>
    [Fact]
    public void AnOverlayIndexIsBinnedTheWayTheCompositorBinsIt()
    {
        // 254 is a value the two part company on. ApplyIndexedOverlay paints row 15 there; RowOf says 16.
        var edge = ContentIndexTexture.ReadOverlay(Sheet((254, 255, 256)), 16, 16);
        Assert.Equal([15], edge.Rows.ToArray());
        Assert.Equal(16, ContentIndexTexture.RowOf(254));   // the other reading, on the same byte
        Assert.Equal(15, ContentIndexTexture.OverlayRowOf(254));

        // Rounding is what RowOf is FOR, so pin the step either side of its midpoint too.
        Assert.Equal(1, ContentIndexTexture.RowOf(8));
        Assert.Equal(2, ContentIndexTexture.RowOf(9));

        // Every row an importer writes round-trips under BOTH readings — through the real scan, not just
        // the binning function. This is the invariant that makes the divergence unreachable in practice.
        for (int row = 1; row <= 16; row++)
        {
            byte red = (byte)((row - 1) * 17);
            Assert.Equal([row], ContentIndexTexture.ReadOverlay(Sheet((red, 255, 256)), 16, 16).Rows.ToArray());
            Assert.Equal(row, ContentIndexTexture.RowOf(red));
        }

        // Green picks the column; a sheet that uses both narrows to neither, because that is a gradient.
        Assert.Equal("A", ContentIndexTexture.ReadOverlay(Sheet((0, 255, 256)), 16, 16).SubRow);
        Assert.Equal("B", ContentIndexTexture.ReadOverlay(Sheet((0, 0, 256)), 16, 16).SubRow);
        Assert.Null(ContentIndexTexture.ReadOverlay(Sheet((0, 255, 128), (0, 0, 128)), 16, 16).SubRow);

        // A run too small to be art earns no row of its own. Without the floor, one bled texel from an art
        // tool's padding would light a row up in the picker that nothing renders.
        var stray = ContentIndexTexture.ReadOverlay(Sheet((0, 255, 200), (255, 255, 56)), 16, 16);
        Assert.Equal([1], stray.Rows.ToArray());

        // And the island mask is applied, so padding outside the UV islands is not coverage: the top half
        // of the sheet is row 1, the bottom is row 16, and only the top half is inside an island.
        var island = new bool[16 * 16];
        for (int i = 0; i < island.Length / 2; i++) island[i] = true;
        var masked = ContentIndexTexture.ReadOverlay(
            Sheet((0, 255, 128), (255, 255, 128)), 16, 16, island, 16, 16);
        Assert.Equal([1], masked.Rows.ToArray());

        // Nothing readable at all stays UNKNOWN rather than becoming a claim — OverlayRowFilter turns an
        // empty scan into a null filter, which dims nothing.
        Assert.Empty(ContentIndexTexture.ReadOverlay([], 16, 16).Rows);
        Assert.Empty(ContentIndexTexture.ReadOverlay(Sheet((0, 255, 256)), 0, 0).Rows);
    }

    /// <summary>Writer and reader end to end: the index an importer writes, read back through the reading
    /// that the shell it is written for actually uses.</summary>
    [Fact]
    public void TheIndexAnImporterWritesReadsBackAsTheRowsItMeant()
    {
        byte[] intensity = [255, 128, 255, 0];
        var bands = GlowShell.Bands(intensity);
        Assert.Equal(2, bands.Count);

        // Column A because GlowShell.Index writes green 255 — the same cell a shell with no index at all
        // lands on, which is what makes row 16 sub-row A the right default when there is no _id.
        var back = ContentIndexTexture.Read(GlowShell.Index(intensity, bands));
        Assert.Equal([1, 2], back.Rows.OrderBy(r => r).ToArray());
        Assert.Equal("A", back.SubRow);
    }

    /// <summary>
    /// An animated glow has to be armed on the cell the material SAMPLES, not on whichever row happens to
    /// carry a value.
    /// <para/>
    /// The regression this pins, in full: the piercings pack's index selects row 1 sub-row B, but an older
    /// colour edit had left values on row 16. Asking "does any row emit" saw those, concluded the glow was
    /// already on, and armed nothing — so the effect switched on at row 16, which the shader never reads.
    /// The material was correct in every other respect and the piece rendered as plain metal.
    /// </summary>
    [Fact]
    public void A_glow_is_armed_on_the_cell_the_material_samples_not_on_any_row_that_has_a_value()
    {
        // The piercings pack: index selects row 1, column B.
        Assert.Equal((1, false), ContentGlowRow.Sampled([1], "B", selectedRow: 16));
        Assert.Equal((16, true), ContentGlowRow.Sampled([16], "A", selectedRow: 3));

        // An unreadable index falls back to the row the grid is showing, in column A.
        Assert.Equal((3, true), ContentGlowRow.Sampled(null, null, selectedRow: 3));
        // An index naming several rows falls back for the ROW — none of them is "the" cell — but keeps the
        // column, which the scan did establish. Half an answer is still an answer.
        Assert.Equal((3, false), ContentGlowRow.Sampled([1, 16], "B", selectedRow: 3));

        // A leftover value on row 16 is NOT this effect being on.
        var rows = new List<ColorTableRowPreset>
        {
            new()
            {
                Row = 16,
                SubRowA = new ColorTableSubRowPreset { Diffuse = "#FF5000" },
                SubRowB = new ColorTableSubRowPreset { Emissive = 0.25f },
            },
        };
        Assert.False(ContentGlowRow.Emits(rows, 1, subRowA: false));
        Assert.True(ContentGlowRow.Emits(rows, 16, subRowA: false));

        // Arming adds the sampled row and leaves the leftovers alone.
        Assert.True(ContentGlowRow.Arm(rows, 1, subRowA: false));
        Assert.True(ContentGlowRow.Emits(rows, 1, subRowA: false));
        Assert.Equal(ContentGlowRow.DefaultGlow, rows.Single(r => r.Row == 1).SubRowB!.Emissive);
        Assert.Null(rows.Single(r => r.Row == 1).SubRowA);              // the other column is untouched
        Assert.Equal(0.25f, rows.Single(r => r.Row == 16).SubRowB!.Emissive);

        // The diffuse is never written — a piece keeps the surface its author gave it.
        Assert.Null(rows.Single(r => r.Row == 1).SubRowB!.Diffuse);
        // The glow COLOUR is written, and has to be: the writer resolves it EmissiveColor → Diffuse, so a
        // cell with neither stays dark however high the intensity.
        Assert.Equal(ContentGlowRow.DefaultGlowColour, rows.Single(r => r.Row == 1).SubRowB!.EmissiveColor);

        // Arming again is a no-op — it seeds a starting point, it does not overrule a choice.
        rows.Single(r => r.Row == 1).SubRowB!.Emissive = 0.4f;
        Assert.False(ContentGlowRow.Arm(rows, 1, subRowA: false));
        Assert.Equal(0.4f, rows.Single(r => r.Row == 1).SubRowB!.Emissive);

        // And column matters: the same row's other half is a different cell.
        Assert.False(ContentGlowRow.Emits(rows, 1, subRowA: true));
        Assert.True(ContentGlowRow.Arm(rows, 1, subRowA: true));
    }

    /// <summary>
    /// Switching an effect off leaves nothing behind.
    /// <para/>
    /// Clearing the effect only changes the SHADER: the pack's own material goes back to character.shpk,
    /// where a Glow value is an ordinary emissive rather than an animation gate. So a value arming left in
    /// the rows kept the piece glowing with the animation switched off — and broke the promise that clearing
    /// an effect republishes the author's material exactly.
    /// </summary>
    [Fact]
    public void Switching_an_effect_off_takes_back_the_glow_it_switched_on()
    {
        var rows = new List<ColorTableRowPreset>();

        // On, then off: back to nothing at all. Not a row carrying an explicit zero — the row writer writes
        // every emissive it is given, so a zero would overwrite whatever the author had there.
        Assert.True(ContentGlowRow.Arm(rows, 1, subRowA: false));
        Assert.True(ContentGlowRow.Disarm(rows, 1, subRowA: false));
        Assert.Empty(rows);

        // Disarming something that was never armed changes nothing.
        Assert.False(ContentGlowRow.Disarm(rows, 1, subRowA: false));

        // A value the user MOVED is theirs and survives — only an untouched seed is taken back.
        ContentGlowRow.Arm(rows, 1, subRowA: false);
        rows.Single().SubRowB!.Emissive = 0.6f;
        Assert.False(ContentGlowRow.Disarm(rows, 1, subRowA: false));
        Assert.Equal(0.6f, rows.Single().SubRowB!.Emissive);

        // So does a cell that carries anything else, even at the seeded value — the row itself is still
        // wanted, so only the Glow is cleared and the rest stays.
        var tinted = new List<ColorTableRowPreset>();
        ContentGlowRow.Arm(tinted, 4, subRowA: true);
        tinted.Single().SubRowA!.Diffuse = "#FF5000";
        Assert.True(ContentGlowRow.Disarm(tinted, 4, subRowA: true));
        Assert.Equal(0f, tinted.Single().SubRowA!.Emissive);
        Assert.Equal("#FF5000", tinted.Single().SubRowA!.Diffuse);

        // And a sibling column keeps the row alive when only one half is taken back.
        var both = new List<ColorTableRowPreset>();
        ContentGlowRow.Arm(both, 2, subRowA: true);
        both.Single().SubRowB = new ColorTableSubRowPreset { Diffuse = "#00FF00" };
        Assert.True(ContentGlowRow.Disarm(both, 2, subRowA: true));
        Assert.Null(both.Single().SubRowA);
        Assert.Equal("#00FF00", both.Single().SubRowB!.Diffuse);

        // A weave is "anything else" too. IsBlank is what decides whether the cell is dropped, so a field it
        // does not know about is a field switching an effect off silently deletes — the user's tile gone
        // because they toggled an unrelated setting.
        var woven = new List<ColorTableRowPreset>();
        ContentGlowRow.Arm(woven, 3, subRowA: true);
        woven.Single().SubRowA!.Tile = 11;
        Assert.True(ContentGlowRow.Disarm(woven, 3, subRowA: true));
        Assert.Equal(0f, woven.Single().SubRowA!.Emissive);
        Assert.Equal(11, woven.Single().SubRowA!.Tile);
    }

    /// <summary>
    /// The row a texture names is what the GAME reads there, which is not always what the pack shipped.
    /// <para/>
    /// The piercings pack names <c>chara/neolithe/neolithe_piercings_index.tex</c> — a namespace its author
    /// invented and does not own. Neolithe [ALL IN ONE] claims the same path and wins, and its file reads
    /// <c>R=255</c>: row 16, where that material happens to keep a silver, fully metallic, non-emitting row.
    /// So a glow armed on the pack's own row 1 rendered as nothing while the piece drew as plain metal, and
    /// nothing said another mod had taken the texture.
    /// <para/>
    /// Both files are decoded here, because the two readings are the whole story and neither is wrong on its
    /// own terms — the fix is to stop the collision, by republishing the pack's texture under a path Proteus
    /// owns, not to prefer one reading over the other.
    /// </summary>
    [Fact]
    public void Two_mods_can_claim_one_texture_path_and_they_select_different_rows()
    {
        static byte[] Flat(byte r, byte g)
        {
            var b = new byte[16 * 4];
            for (int i = 0; i < 16; i++) { b[i * 4] = r; b[i * 4 + 1] = g; b[i * 4 + 3] = 255; }
            return b;
        }

        // What the pack ships: black, so row 1 column B.
        var pack = ContentIndexTexture.Read(Flat(0, 0));
        Assert.Equal(new[] { 1 }, pack.Rows.ToArray());
        Assert.Equal("B", pack.SubRow);

        // What actually resolved at that path: another mod's silver index, red at full, so row 16 column B.
        var foreign = ContentIndexTexture.Read(Flat(255, 0));
        Assert.Equal(new[] { 16 }, foreign.Rows.ToArray());
        Assert.Equal("B", foreign.SubRow);

        // Same column, different row — which is why editing row 16 B was the only thing that ever showed.
        Assert.NotEqual(pack.Rows.Single(), foreign.Rows.Single());
        Assert.Equal(pack.SubRow, foreign.SubRow);
    }

    /// <summary>
    /// How a resolved content model has to be published, as a table.
    /// <para/>
    /// The game deforms a model by the race code of the path it loaded from, so a model in the shared cut
    /// space is resized onto whoever wears it — which every content piece has relied on — while a model
    /// already built at the wearer's race must not be touched. Getting this backwards is not subtle: a
    /// Miqo'te-authored pack placed in cut space is deformed a second time and lands in the wrong place.
    /// </summary>
    [Fact]
    public void A_content_model_is_cut_space_or_native_or_it_cannot_be_worn()
    {
        var body = new ShellSurfaceKey(ShellSurfaceKind.Body, string.Empty);

        // The ordinary pack: built in the shared shape, worn by the Midlander F it was cut for. Stays Body,
        // which is what lets it ride an appended ring instead of demanding a carrier — and all three codes
        // agreeing here is exactly why cut space has to be tested BEFORE "matches the wearer".
        Assert.Equal(body, SecondSkinService.ContentSurface(body, "0201", "0201", "0201"));

        // Same pack on a Miqo'te: still cut space, still deformed onto her by the game.
        Assert.Equal(body, SecondSkinService.ContentSurface(body, "0201", "0801", "0201"));

        // A Miqo'te-authored pack on a Miqo'te: native, at her own race, and the race rides in the surface
        // id so two races' pieces can never share a host or a published material.
        Assert.Equal(new ShellSurfaceKey(ShellSurfaceKind.Native, "0801"),
            SecondSkinService.ContentSurface(body, "0801", "0801", "0201"));

        // A model for neither: a Hrothgar reaching a Roegadyn model down the fall-through chain would need a
        // deform between two races Proteus does not do. Refused rather than shown at the wrong size.
        Assert.Null(SecondSkinService.ContentSurface(body, "0901", "1501", "0101"));
        Assert.Null(SecondSkinService.ContentSurface(body, "0801", "0201", "0201"));

        // The shared shape is decided on the CODE, not by matching this character's cut code. Those are
        // different questions and conflating them refused packs that work: the cut code is voted off the
        // paths the body was cut from, and a character whose skin comes from a WHOLE-BODY model votes their
        // own race. An Au Ra F in an ordinary c0201 pack then matched neither arm and lost every piece.
        Assert.Equal(body, SecondSkinService.ContentSurface(body, "0201", "1401", "1401"));
        Assert.Equal(body, SecondSkinService.ContentSurface(body, "0101", "1501", "1501"));

        // A pack that ships ONE model for everyone names no race at all, so there is none to disagree with.
        // It reports a null code precisely so it is never judged against a code it did not claim.
        Assert.Equal(body, SecondSkinService.ContentSurface(body, null, "1401", "1401"));

        // A sidecar that names a surface by hand means it; this only decides for the default.
        var face = new ShellSurfaceKey(ShellSurfaceKind.Face, "f0001");
        Assert.Equal(face, SecondSkinService.ContentSurface(face, "0801", "0201", "0201"));
    }

    [Fact]
    public void A_pack_shipping_one_model_for_everyone_claims_no_race()
    {
        // ModelFor falls back to Model for ANY code, so the exact-match arm always succeeds for an un-keyed
        // piece. Reporting the code it was asked about would attribute a race the pack never named, and the
        // surface decision would then refuse it on a character whose cut code differs from their gear's.
        var universal = new ContentPiece { Model = "models/thing.mdl" };
        Assert.Equal((null, "models/thing.mdl"), SecondSkinService.ResolveVariantForTest(universal, "0201"));
        Assert.Equal((null, "models/thing.mdl"), SecondSkinService.ResolveVariantForTest(universal, "1401"));
        Assert.Equal((null, "models/thing.mdl"), SecondSkinService.ResolveVariantForTest(universal, null));

        // A keyed piece still reports the code that matched, which is the whole point of returning one.
        var keyed = new ContentPiece { Models = new() { ["0801"] = "models/miqo.mdl" } };
        Assert.Equal(("0801", "models/miqo.mdl"), SecondSkinService.ResolveVariantForTest(keyed, "0801"));
        Assert.Null(SecondSkinService.ResolveVariantForTest(keyed, "0201"));
    }

    [Fact]
    public void A_native_surface_needs_a_carrier_and_names_itself()
    {
        var native = new ShellSurfaceKey(ShellSurfaceKind.Native, "0801");

        // Only a carrier can promise no deform — an append host's EQDP belongs to the player's own item.
        Assert.True(native.RequiresNativeHost);
        Assert.False(native.IsBody);
        Assert.False(new ShellSurfaceKey(ShellSurfaceKind.Body, string.Empty).RequiresNativeHost);

        // Named, not swept into the catch-all that would have called it "Ear".
        Assert.Equal("Native", ShellSurface.Label(ShellSurfaceKind.Native));

        // And two races never merge into one surface, so they can never share a published material.
        Assert.NotEqual(native, new ShellSurfaceKey(ShellSurfaceKind.Native, "1401"));
        Assert.NotEqual(
            SecondSkinService.ContentUnitKey("mod", native, "m.mtrl", null),
            SecondSkinService.ContentUnitKey("mod", new ShellSurfaceKey(ShellSurfaceKind.Native, "1401"),
                "m.mtrl", null));
    }

    /// <summary>
    /// Race codes, the fall-through table, and how a code is said out loud — shared by the composite, the
    /// importer and the panel, which must not disagree about what "0801" means.
    /// </summary>
    [Fact]
    public void Race_codes_are_read_named_and_fall_through_the_way_the_game_does()
    {
        Assert.Equal(8, ModelRace.Index("0801"));
        Assert.Equal(2, ModelRace.Index("0201"));
        Assert.Null(ModelRace.Index("9101"));      // past the playable range
        Assert.Null(ModelRace.Index(null));

        Assert.Equal("Miqote F", ModelRace.Describe("0801"));
        Assert.Equal("Midlander M", ModelRace.Describe("0101"));
        Assert.Equal("nonsense", ModelRace.Describe("nonsense"));   // never renames what it can't read

        // Only Midlander is the shape the game resizes for everyone.
        Assert.True(ModelRace.IsSharedShape("0101"));
        Assert.True(ModelRace.IsSharedShape("0201"));
        Assert.False(ModelRace.IsSharedShape("0801"));
        Assert.False(ModelRace.IsSharedShape("1201"));              // Lalafell ship their own, and are not it

        // The game's own table, exceptions included.
        Assert.Equal(0, ModelRace.Fallback(1));     // Midlander male is the root
        Assert.Equal(1, ModelRace.Fallback(2));
        Assert.Equal(11, ModelRace.Fallback(12));   // Lalafell female -> Lalafell male
        Assert.Equal(9, ModelRace.Fallback(15));    // Hrothgar male   -> Roegadyn male
        Assert.Equal(2, ModelRace.Fallback(8));     // Miqo'te female  -> Midlander female

        Assert.Equal("Miqote F, Au Ra F".Replace("Au Ra", "AuRa"),
            ModelRace.DescribeAll(["0801", "1401", "0801"]));       // deduped, in the order given
    }

    /// <summary>
    /// Colours and glow belong to a MATERIAL, because that is what one of the panel's tabs governs.
    /// <para/>
    /// They used to be stored per option, which cannot express a pack holding nine accessories in one
    /// always-on model: that is ONE option and nine materials, so every tab read and wrote the same
    /// settings. Switching on a glow for the ear rings and finding the shin laces already glowing is the
    /// same storage answering two different questions.
    /// </summary>
    [Fact]
    public void Content_colours_and_glow_are_stored_per_material_not_per_option()
    {
        var meta = new ProteusMetadata();
        const string EarRings = "common/2/mt_c0801e5505_met_a.mtrl";
        const string ShinLaces = "common/9/mt_c0801e5505_met_e.mtrl";

        // Reading never creates an entry — merely opening a panel must not change the mod.
        Assert.Null(meta.PeekMaterialSettings(EarRings));
        Assert.Null(meta.ContentMaterials);

        meta.MaterialSettings(EarRings).Glow = new GearSettingsPreset { Scroll = "geometric.jpeg" };
        meta.MaterialSettings(EarRings).ColorTableRows =
            [new ColorTableRowPreset { Row = 16, SubRowA = new ColorTableSubRowPreset { Diffuse = "#FF0000" } }];

        // The other material is untouched — the whole point.
        Assert.Null(meta.PeekMaterialSettings(ShinLaces));
        Assert.Equal("geometric.jpeg", meta.PeekMaterialSettings(EarRings)!.Glow!.Scroll);

        // And it keeps its own answer once it has one.
        meta.MaterialSettings(ShinLaces).Glow = new GearSettingsPreset { Scroll = "flames.jpeg" };
        Assert.Equal("geometric.jpeg", meta.PeekMaterialSettings(EarRings)!.Glow!.Scroll);
        Assert.Equal("flames.jpeg", meta.PeekMaterialSettings(ShinLaces)!.Glow!.Scroll);

        // An entry SURVIVES being emptied, and a cleared glow is stored as a preset naming no effect rather
        // than as null. Both say "cleared here", which is a different fact from "never set here": dropping
        // either would fall back through to the older per-option value, so clearing an effect would make the
        // pack's previous one reappear on the next composite.
        meta.MaterialSettings(EarRings).Glow = new GearSettingsPreset();
        meta.MaterialSettings(EarRings).ColorTableRows = [];
        Assert.NotNull(meta.PeekMaterialSettings(EarRings));
        Assert.NotNull(meta.PeekMaterialSettings(EarRings)!.Glow);
        Assert.Null(meta.PeekMaterialSettings(EarRings)!.Glow!.GlowKey());
        Assert.Empty(meta.PeekMaterialSettings(EarRings)!.ColorTableRows!);

        // Two materials differing only in their settings publish different materials, so they cannot share
        // a host slot — which is exactly why the settings must not be shared either.
        var body = new ShellSurfaceKey(ShellSurfaceKind.Body, string.Empty);
        Assert.NotEqual(
            SecondSkinService.ContentUnitKey("mod", body, EarRings, null, "geometric.jpeg 0.15 0.15 5 5"),
            SecondSkinService.ContentUnitKey("mod", body, ShinLaces, null, "geometric.jpeg 0.15 0.15 5 5"));
    }

    [Fact]
    public void The_row_decode_rounds_to_the_nearest_pair_rather_than_truncating()
    {
        // Every exact multiple of 17 lands on its own pair — the authored case, and the one plain division
        // already got right.
        for (int pair = 0; pair < 16; pair++)
            Assert.Equal(pair + 1, ContentIndexTexture.RowOf((byte)(pair * 17)));

        // A value one short of the multiple is what truncation gets wrong: 254 is plainly pair 15 (row 16),
        // but 254/17 = 14. That is not cosmetic — the editor DISABLES every row the scan doesn't name, so a
        // one-off decode puts the only working row out of reach.
        Assert.Equal(16, ContentIndexTexture.RowOf(254));
        Assert.Equal(16, ContentIndexTexture.RowOf(250));
        Assert.Equal(2,  ContentIndexTexture.RowOf(16));
        Assert.Equal(2,  ContentIndexTexture.RowOf(18));

        // Halfway rounds up, and neither end can escape the table.
        Assert.Equal(1,  ContentIndexTexture.RowOf(0));
        Assert.Equal(2,  ContentIndexTexture.RowOf(9));
        Assert.Equal(1,  ContentIndexTexture.RowOf(8));
        Assert.Equal(16, ContentIndexTexture.RowOf(255));
    }

    [Fact]
    public void ModelFor_picks_the_wearers_race()
    {
        var piece = new ContentPiece
        {
            Models = new Dictionary<string, string>
            {
                ["0101"] = "male.mdl",
                ["0201"] = "female.mdl",
            },
        };

        Assert.Equal("female.mdl", piece.ModelFor("0201"));
        Assert.Equal("male.mdl", piece.ModelFor("0101"));
        // No variant for this race: an EXACT lookup only, because choosing a fallback is a decision about
        // race fall-through and belongs to the code that owns that chain.
        Assert.Null(piece.ModelFor("1801"));
        Assert.Null(piece.ModelFor(null));

        // A pack that ships one model for everyone keeps working through the same accessor.
        Assert.Equal("m.mdl", new ContentPiece { Model = "m.mdl" }.ModelFor("1801"));
    }

    [Fact]
    public void Slot_and_set_come_out_of_the_model_path()
    {
        var jacket = ContentSlot.Parse("chara/equipment/e6085/model/c0201e6085_met.mdl");
        Assert.NotNull(jacket);
        Assert.Equal("Head", jacket!.Value.Label);
        Assert.Equal("e6085", jacket.Value.SetTag);
        Assert.Equal("0201", jacket.Value.RaceCode);
        Assert.Equal(3, jacket.Value.Category);
        Assert.Equal(6085, ContentSlot.SetIdOf(jacket.Value.SetTag));

        var ring = ContentSlot.Parse("chara/accessory/a0053/model/c0201a0053_rir.mdl");
        Assert.Equal("Right ring", ring!.Value.Label);
        // Anchored by InvisibleRing.CarrierSlots, which resolves real items with these very numbers.
        Assert.Equal(12, ring.Value.Category);

        // A character part has no Item row, so it is labelled but never named.
        var hair = ContentSlot.Parse("chara/human/c0201/obj/hair/h0001/model/c0201h0001_hir.mdl");
        Assert.Equal("Hair", hair!.Value.Label);
        Assert.Equal(0, hair.Value.Category);

        // An unknown suffix labels itself rather than coming out blank.
        Assert.Equal("kao", ContentSlot.Parse("chara/equipment/e0001/model/c0201e0001_kao.mdl")!.Value.Label);

        Assert.Null(ContentSlot.Parse("chara/equipment/e0001/texture/v01_c0201e0001_top_d.tex"));
    }

    [Fact]
    public void Item_names_are_deterministic_when_several_items_share_a_set()
    {
        // Dyes and variants share a model set. The lowest RowId wins so the label does not change run to run.
        var lookup = ContentSlot.NameLookup(
            [(50u, 4u, 6025UL), (10u, 4u, 6025UL), (99u, 3u, 6085UL)],
            id => id switch { 50 => "Dyed", 10 => "Gryphonskin Breastguard", 99 => "Hair Ribbon", _ => "" });

        Assert.Equal("Gryphonskin Breastguard", lookup(4, 6025));
        Assert.Equal("Hair Ribbon", lookup(3, 6085));
        Assert.Null(lookup(4, 9999));
    }

    /// <summary>
    /// An accessory may take the fall-through chain's cross-gender hop; a garment may not.
    /// <para/>
    /// The game itself hands accessories across genders — a Midlander female wears
    /// <c>c0101a0002_wrs.mdl</c>, a male-coded model, and the live equipment walk reports exactly that. So
    /// modders ship one c0101 ring or lantern for everyone, and refusing it leaves the piece invisible with
    /// a message about body shape that does not apply to a prop hung off a bone.
    /// <para/>
    /// A fitted garment keeps the guard: c0101 and c0201 are different bodies, and the male cut of a top on
    /// a female is exactly what the refusal is for.
    /// </summary>
    [Fact]
    public void An_accessory_may_fall_through_to_the_other_gender_but_a_garment_may_not()
    {
        // The paths an IMPORT writes, not tidy game paths: the sidecar stores the pack's ARCHIVE ENTRY, and
        // the real lantern is recorded as "base install/chara/accessory/a0189/model/c0101a0189_wrs.mdl".
        // Written that way deliberately — a test using clean game paths would keep passing if the check
        // regressed to matching a folder segment, which is exactly what it must not do.
        const string lanternMdl = "base install/chara/accessory/a0189/model/c0101a0189_wrs.mdl";
        const string topMdl     = "undershirt/cropped tee/chara/equipment/e0043/model/c0101e0043_top.mdl";

        var lantern = new ContentPiece { Models = new() { ["0101"] = lanternMdl } };
        var top     = new ContentPiece { Models = new() { ["0101"] = topMdl } };

        // Midlander female. The chain's first hop is 2 -> 1, which IS the game's own fallback.
        Assert.Equal(("0101", lanternMdl), SecondSkinService.ResolveVariantForTest(lantern, "0201"));
        Assert.Null(SecondSkinService.ResolveVariantForTest(top, "0201"));

        // Further out: Miqo'te female reaches c0101 as 8 -> 2 -> 1, so the hop is the last step rather than
        // the first, and an accessory still gets there.
        Assert.Equal(("0101", lanternMdl), SecondSkinService.ResolveVariantForTest(lantern, "0801"));
        Assert.Null(SecondSkinService.ResolveVariantForTest(top, "0801"));

        // An exact match never needed the chain, and a male character reaches c0101 without one either.
        Assert.Equal(("0101", topMdl), SecondSkinService.ResolveVariantForTest(top, "0101"));

        // A pack that does NOT mirror the game path under its option folder — nothing forbids it, and the
        // folder-segment check this replaced would refuse this one and leave the lantern invisible.
        var flat = new ContentPiece { Models = new() { ["0101"] = "a0189/model/c0101a0189_wrs.mdl" } };
        Assert.Equal(("0101", "a0189/model/c0101a0189_wrs.mdl"),
            SecondSkinService.ResolveVariantForTest(flat, "0201"));

        // Backslashes, which a manifest may well use, and a bare filename with no folder at all.
        var backslashed = new ContentPiece
        {
            Models = new() { ["0101"] = @"base install\chara\accessory\a0189\model\c0101a0189_wrs.mdl" },
        };
        Assert.NotNull(SecondSkinService.ResolveVariantForTest(backslashed, "0201"));
        Assert.NotNull(SecondSkinService.ResolveVariantForTest(
            new ContentPiece { Models = new() { ["0101"] = "c0101a0189_wrs.mdl" } }, "0201"));

        // A garment sitting under an "accessory" FOLDER is still a garment — the name decides, and the
        // folder-segment check got this one backwards.
        var mislabelled = new ContentPiece
        {
            Models = new() { ["0101"] = "my accessory pack/chara/equipment/e0043/model/c0101e0043_top.mdl" },
        };
        Assert.Null(SecondSkinService.ResolveVariantForTest(mislabelled, "0201"));

        // A name that is not the cNNNNxNNNN shape at all reads as unknown, and unknown takes the strict rule.
        Assert.Null(SecondSkinService.ResolveVariantForTest(
            new ContentPiece { Models = new() { ["0101"] = "models/lantern.mdl" } }, "0201"));

        // A piece mixing an accessory and a garment is judged as the garment — unknown or mixed means the
        // stricter rule, since the risk is one-sided.
        var mixed = new ContentPiece
        {
            Models = new()
            {
                ["0101"] = lanternMdl,
                ["0301"] = "undershirt/cropped tee/chara/equipment/e0043/model/c0301e0043_top.mdl",
            },
        };
        Assert.Null(SecondSkinService.ResolveVariantForTest(mixed, "0201"));

        // And a c0101 accessory is still cut space, so it is deformed onto the wearer rather than
        // demanding a carrier of its own.
        var body = new ShellSurfaceKey(ShellSurfaceKind.Body, string.Empty);
        Assert.Equal(body, SecondSkinService.ContentSurface(body, "0101", "0201", "0201"));
    }

    /// <summary>
    /// A selected IMC option TOGGLES its bits against the default — it does not simply clear or set them.
    /// <para/>
    /// All three shapes here are real, taken from the pack library, and no one-directional rule serves them
    /// all: 63 groups put their option bits inside the default mask, 47 put them outside, and 27 overlap it.
    /// The overlapping ones settle the question, because only xor turns a "swap one variant for another"
    /// into exactly that.
    /// <para/>
    /// Getting it wrong is silent and destructive in one direction: clearing on a pack that meant "set"
    /// leaves every attribute off, and every submesh those attributes gate is then dropped from the model.
    /// </summary>
    [Fact]
    public void An_imc_option_toggles_its_bits_rather_than_only_clearing_them()
    {
        static ContentAttributeGroup G(int def, params (string Name, int Mask)[] opts) => new()
        {
            Group = "Toggles",
            SetId = 43,
            DefaultMask = def,
            Options = opts.ToDictionary(o => o.Name, o => o.Mask, StringComparer.Ordinal),
        };

        // CONTAINED — Denim Shorts. Default has the bits, so a selection has to clear to mean anything.
        var hide = G(3, ("Panty Strap Hide", 1), ("Pockets Hide", 2));
        Assert.Equal(3, hide.MaskFor(null));
        Assert.Equal(2, hide.MaskFor(["Panty Strap Hide"]));
        Assert.Equal(0, hide.MaskFor(["Panty Strap Hide", "Pockets Hide"]));

        // OUTSIDE — deadrose. Default is 0, so a selection has to SET. Under a clear-only rule every one of
        // these reads 0, every attribute counts as off, and the whole dress loses its toggleable parts.
        var add = G(0, ("+ top of the harness", 1), ("+ garter with the dagger", 2));
        Assert.Equal(0, add.MaskFor(null));
        Assert.Equal(1, add.MaskFor(["+ top of the harness"]));
        Assert.Equal(3, add.MaskFor(["+ top of the harness", "+ garter with the dagger"]));

        // OVERLAPPING — "Nails", default 16 and an option mask of 48. Bit 4 off, bit 5 on: one style swapped
        // for another. OR would draw both meshes, clear would leave none.
        Assert.Equal(32, G(16, ("Nails", 48)).MaskFor(["Nails"]));
        Assert.Equal(2, G(1, ("Toenails", 3)).MaskFor(["Toenails"]));

        // Still clamped to the ten bits the field holds, and an option nobody selected changes nothing.
        Assert.Equal(0x3FF, G(0, ("All", ~0)).MaskFor(["All"]));
        Assert.Equal(16, G(16, ("Nails", 48)).MaskFor(["something else entirely"]));
    }

    /// <summary>
    /// Several IMC groups on ONE set are separated by their slot, so each judges only its own garment.
    /// <para/>
    /// deadrose is the shape: dress, bottoms and shoes all sit on set 43 and differ only by EquipSlot. With
    /// the slot ignored every group matched every model, so each group's unselected bits hid the OTHER
    /// garments' geometry — selecting a bottoms option changed nothing, because the dress and shoes groups
    /// were still hiding it.
    /// </summary>
    [Fact]
    public void Imc_groups_on_one_set_are_told_apart_by_their_slot()
    {
        static ContentAttributeGroup G(string slot, int def, params (string Name, int Mask)[] opts) => new()
        {
            Group = slot + " toggles",
            SetId = 43,
            Slot = slot,
            DefaultMask = def,
            Options = opts.ToDictionary(o => o.Name, o => o.Mask, StringComparer.Ordinal),
        };

        List<ContentAttributeGroup> groups =
        [
            G("Body", 16, ("+ skirt", 25)),
            G("Legs", 0, ("+ harness", 1)),
            G("Feet", 0, ("+ ruffles", 2)),
        ];
        string[] attrs = ["atr_a", "atr_b", "atr_c"];   // bits 0, 1, 2

        const string Top = "chara/equipment/e0043/model/c0201e0043_top.mdl";
        const string Legs = "chara/equipment/e0043/model/c0201e0043_dwn.mdl";

        Dictionary<string, List<string>> Sel(string group, params string[] on) =>
            new(StringComparer.Ordinal) { [group] = [.. on] };

        // Selecting a LEGS option shows its attribute on the legs model. The Body and Feet groups have
        // nothing to say about this model and must not reach it — they are the ones that used to hide it.
        var onLegs = SecondSkinService.HiddenAttributes(groups, Legs, attrs, Sel("Legs toggles", "+ harness"));
        Assert.DoesNotContain("atr_a", onLegs ?? new HashSet<string>());

        // And the same selection changes nothing on the TOP model, which the Legs group does not govern.
        // Body defaults to 16 — bit 4, past this three-name table — so every named attribute is off there.
        var onTop = SecondSkinService.HiddenAttributes(groups, Top, attrs, Sel("Legs toggles", "+ harness"));
        Assert.Equal(["atr_a", "atr_b", "atr_c"], onTop!.Order());

        // A group naming no slot still matches anything, so sidecars written before the slot was recorded
        // keep working rather than silently doing nothing.
        var legacy = G("Legs", 0, ("+ harness", 1));
        legacy.Slot = null;
        Assert.DoesNotContain("atr_a",
            SecondSkinService.HiddenAttributes([legacy], Top, attrs, Sel("Legs toggles", "+ harness"))
            ?? new HashSet<string>());
    }

    /// <summary>
    /// A pack's IMC hide-toggles resolve to attribute NAMES through each model's own table.
    /// <para/>
    /// Denim Shorts is the shape under test: an Imc group on set 6058 whose default mask is 3 — both bits
    /// on — with "Panty Strap Hide" carrying bit 0 and "Pockets Hide" bit 1. Selecting an option CLEARS its
    /// bits, which is what makes them read as "hide".
    /// </summary>
    [Fact]
    public void An_imc_hide_toggle_resolves_to_the_attribute_names_that_models_own_table_uses()
    {
        var group = new ContentAttributeGroup
        {
            Group = "Toggles",
            SetId = 6058,
            DefaultMask = 3,
            Options = new(StringComparer.Ordinal)
            {
                ["Panty Strap Hide"] = 1,
                ["Pockets Hide"] = 2,
            },
        };
        List<ContentAttributeGroup> groups = [group];
        const string Mid = "items/chara/equipment/e6058/model/c0101e6058_dwn.mdl";

        // A bit is matched to a name by the LETTER the name ends in, so the table's order is irrelevant —
        // the same two names in either order give the same answer. Denim Shorts really does ship them both
        // ways round (atr_sne/atr_hiz swap between its Midlander and Lalafell models), and under the old
        // positional reading that alone changed which piece a toggle hid.
        string[] forward = ["atr_dv_a", "atr_dv_b"];
        string[] reversed = ["atr_dv_b", "atr_dv_a"];

        Dictionary<string, List<string>> Sel(params string[] on) => new(StringComparer.Ordinal)
            { ["Toggles"] = [.. on] };

        // Nothing selected: the default has both bits, so nothing is hidden at all.
        Assert.Null(SecondSkinService.HiddenAttributes(groups, Mid, forward, Sel()));
        Assert.Null(SecondSkinService.HiddenAttributes(groups, Mid, forward, null));

        // Bit 0 is whichever name ends in "a" — in either table order.
        Assert.Equal(["atr_dv_a"],
            SecondSkinService.HiddenAttributes(groups, Mid, forward, Sel("Panty Strap Hide"))!.Order());
        Assert.Equal(["atr_dv_a"],
            SecondSkinService.HiddenAttributes(groups, Mid, reversed, Sel("Panty Strap Hide"))!.Order());

        // Both selected: the mask empties and every part attribute goes.
        Assert.Equal(["atr_dv_a", "atr_dv_b"],
            SecondSkinService.HiddenAttributes(groups, Mid, forward,
                Sel("Panty Strap Hide", "Pockets Hide"))!.Order());

        // A group for a different set leaves this model alone — a pack can ship several items.
        Assert.Null(SecondSkinService.HiddenAttributes(groups,
            "items/chara/equipment/e9999/model/c0101e9999_dwn.mdl", forward, Sel("Panty Strap Hide")));

        // Names that are not part attributes are never touched. atr_hij, atr_nek, atr_ude, atr_hiz and
        // atr_sne suppress body geometry and the game drives them from EQP; reading them as parts is what
        // made deadrose's "+ arm belts" toggle atr_nek and therefore do nothing visible at all.
        Assert.Null(SecondSkinService.HiddenAttributes(
            [new ContentAttributeGroup { Group = "Toggles", SetId = 6058, DefaultMask = 0 }],
            Mid, ["atr_hij", "atr_nek", "atr_ude", "atr_hiz", "atr_sne"], null));

        // Part J is the last bit an IMC mask has; anything beyond it is not addressable and stays put.
        Assert.Equal(["atr_tv_j"], SecondSkinService.HiddenAttributes(
            [new ContentAttributeGroup { Group = "Toggles", SetId = 6058, DefaultMask = 0x1FF }],
            Mid, ["atr_tv_j", "atr_tv_k"], null)!.Order());
    }

    /// <summary>
    /// An attribute's bit is the letter it ends in — <c>atr_tv_a</c> is part A, <c>atr_tv_i</c> is part I.
    /// Everything else answers to no bit at all.
    /// </summary>
    [Fact]
    public void A_part_attributes_bit_is_the_letter_it_ends_in()
    {
        Assert.Equal(0, SecondSkinService.PartAttributeBit("atr_tv_a"));
        Assert.Equal(1, SecondSkinService.PartAttributeBit("atr_tv_b"));
        Assert.Equal(8, SecondSkinService.PartAttributeBit("atr_tv_i"));
        Assert.Equal(9, SecondSkinService.PartAttributeBit("atr_tv_j"));

        // The prefix does not matter — bottoms and shoes name their parts the same way.
        Assert.Equal(0, SecondSkinService.PartAttributeBit("atr_dv_a"));
        Assert.Equal(1, SecondSkinService.PartAttributeBit("atr_sv_b"));

        // Body-suppression attributes: EQP's, not the mask's.
        foreach (var n in new[] { "atr_hij", "atr_nek", "atr_ude", "atr_hiz", "atr_sne", "atr_leg" })
            Assert.Null(SecondSkinService.PartAttributeBit(n));

        // A mask holds ten bits, so a letter past J answers to none of them.
        Assert.Null(SecondSkinService.PartAttributeBit("atr_tv_k"));

        // And nothing shaped like a part attribute at all.
        foreach (var n in new[] { "a", "_a", "atr_", "heels_offset=0.0361", "" })
            Assert.Null(SecondSkinService.PartAttributeBit(n));
    }
}
