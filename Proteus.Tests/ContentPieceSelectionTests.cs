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
}
