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
}
