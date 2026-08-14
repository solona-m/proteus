using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Proteus;
using Proteus.Services;
using StbImageSharp;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Offline coverage for the Onion <c>.omp</c> importer: the pack reader, the layer classifier (what is
/// imported and what is refused, and why), and the pure filesystem writer. No Penumbra, no game data —
/// material paths are supplied by the test rather than probed.
/// </summary>
public class OnionImportTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    private sealed record Layer(
        string File, string Layout, string Map, string Mode = "Normal",
        int Order = 0, double Opacity = 1.0, string? Races = null, string? GeneratedFrom = null);

    /// <summary>A .omp on disk: meta.json plus one solid-colour PNG per layer, keyed by its File name.</summary>
    private static string MakePack(
        string dir, IEnumerable<Layer> layers,
        string name = "Ven", string author = "Almaden", int formatVersion = 2,
        string? groups = null, IReadOnlyDictionary<string, byte[]>? images = null,
        string? extraEntry = null)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "pack.omp");

        var ls = layers.ToList();
        var meta = new StringBuilder();
        meta.Append('{')
            .Append($"\"FormatVersion\":{formatVersion},")
            .Append($"\"Name\":{JsonSerializer.Serialize(name)},")
            .Append($"\"Author\":{JsonSerializer.Serialize(author)},")
            .Append("\"Description\":\"a description\",\"Version\":\"1.0.0\",")
            .Append("\"Website\":\"https://example.invalid/x\",")
            .Append($"\"Groups\":{groups ?? "[]"},")
            .Append($"\"TotalLayerCount\":{ls.Count},")
            .Append("\"Layers\":[");
        meta.Append(string.Join(",", ls.Select(l => "{"
            + $"\"File\":{JsonSerializer.Serialize(l.File)},"
            + $"\"Layout\":{JsonSerializer.Serialize(l.Layout)},"
            + $"\"Map\":{JsonSerializer.Serialize(l.Map)},"
            + $"\"Mode\":{JsonSerializer.Serialize(l.Mode)},"
            + $"\"Order\":{l.Order},"
            + $"\"Opacity\":{l.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
            + $"\"Races\":{l.Races ?? "[]"},"
            + $"\"GeneratedFrom\":{(l.GeneratedFrom == null ? "null" : JsonSerializer.Serialize(l.GeneratedFrom))},"
            + "\"SourceHash\":null}")));
        meta.Append("]}");

        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("meta.json").Open()))
            w.Write(meta.ToString());

        foreach (var l in ls.Select(l => l.File).Distinct())
        {
            var bytes = images != null && images.TryGetValue(l, out var b) ? b : SolidPng(8, 8, 200, 100, 50, 255);
            using var s = zip.CreateEntry(l).Open();
            s.Write(bytes, 0, bytes.Length);
        }

        if (extraEntry != null)
            using (var s = zip.CreateEntry(extraEntry).Open())
                s.WriteByte(0);

        return path;
    }

    private static byte[] SolidPng(int w, int h, byte r, byte g, byte b, byte a)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = a;
        }
        using var mem = new MemoryStream();
        new StbImageWriteSharp.ImageWriter().WritePng(
            rgba, w, h, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, mem);
        return mem.ToArray();
    }

    /// <summary>Two stand-in body materials per layout — the shape <see cref="BodyMaterialCatalog"/> produces.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Materials(
        OnionImportService.ImportPreview preview)
        => preview.Layers.Where(l => l.Import)
            .GroupBy(l => l.LayoutToken, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)
                [
                    "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001" + g.First().Suffix,
                    "chara/human/c1801/obj/body/b0001/material/v0001/mt_c1801b0001" + g.First().Suffix,
                ],
                StringComparer.OrdinalIgnoreCase);

    private static OnionImportService.ImportPreview Preview(string omp, string? body = null)
        => OnionImportService.BuildPreview(omp, OnionPackage.Read(omp), body);

    private static ProteusMetadata ReadSidecar(string root)
        => JsonSerializer.Deserialize<ProteusMetadata>(
               File.ReadAllText(Path.Combine(root, "Proteus", "metadata.json")),
               new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "proteus_omp_" + Path.GetRandomFileName());

    private static void With(Action<string> body)
    {
        var dir = TempDir();
        try { Directory.CreateDirectory(dir); body(dir); }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ── Multi-layout packs ───────────────────────────────────────────────────

    [Fact]
    public void Three_layouts_become_one_single_select_group()
    {
        With(dir =>
        {
            var omp = MakePack(dir,
            [
                new Layer("layers/bibo.png", "bibo", "base", Order: 0),
                new Layer("layers/gen3.png", "gen3", "base", Order: 1),
                new Layer("layers/van.png", "vanilla", "base", Order: 2),
            ]);

            // The wearer is on gen3, so that option must be the one pre-selected — and reported as a real
            // match rather than the preference-order fallback.
            var preview = Preview(omp, "gen3");
            Assert.Equal(3, preview.Layers.Count);
            Assert.All(preview.Layers, l => Assert.True(l.Import));
            Assert.Equal("gen3", preview.DefaultLayout);
            Assert.True(preview.DefaultLayoutMatchedBody);

            var root = Path.Combine(dir, "mod");
            OnionImportService.WriteMod(root, "Ven", "Almaden", preview, Materials(preview), texLoader: null);

            // Sidecar: one group, one option per layout, each carrying its own UV space and materials.
            var meta = ReadSidecar(root);
            Assert.Null(meta.Overlays);
            var group = Assert.Single(meta.OptionGroups!);
            Assert.Equal(OnionImportService.LayoutGroupName, group.PenumbraGroupName);
            Assert.Equal(["bibo", "gen3", "vanilla"], group.Options.Select(o => o.Name));

            var expected = new (string Option, string BodyType, string Suffix)[]
            {
                ("bibo", "bibo", "_bibo.mtrl"), ("gen3", "gen3", "_b.mtrl"), ("vanilla", "gen2", "_a.mtrl"),
            };
            foreach (var (option, bodyType, suffix) in expected)
            {
                var ov = Assert.Single(group.Options.Single(o => o.Name == option).Overlays);
                Assert.Equal(bodyType, ov.SourceBodyType);
                Assert.Equal(OverlayLayer.Skin, ov.Layer);
                Assert.NotNull(ov.Diffuse);
                Assert.All(ov.MaterialGamePaths, p => Assert.EndsWith(suffix, p));
                Assert.Equal(2, ov.MaterialGamePaths.Count);
                Assert.True(File.Exists(Path.Combine(root, "Proteus", ov.Diffuse!.Replace('/', Path.DirectorySeparatorChar))));
            }

            // Penumbra side: a v3 group file with the detected layout as DefaultSettings.
            var groupFile = Path.Combine(root, "group_001_body uv.json");
            Assert.True(File.Exists(groupFile));
            var gj = JsonDocument.Parse(File.ReadAllText(groupFile)).RootElement;
            Assert.Equal("Body UV", gj.GetProperty("Name").GetString());
            Assert.Equal("Single", gj.GetProperty("Type").GetString());
            Assert.Equal(1, gj.GetProperty("DefaultSettings").GetInt32());   // index of "gen3"
            Assert.Equal(["bibo", "gen3", "vanilla"],
                gj.GetProperty("Options").EnumerateArray().Select(o => o.GetProperty("Name").GetString()));
        });
    }

    [Fact]
    public void Default_layout_falls_back_to_bibo_when_the_body_is_unknown()
    {
        With(dir =>
        {
            var omp = MakePack(dir,
            [
                new Layer("layers/van.png", "vanilla", "base", Order: 0),
                new Layer("layers/bibo.png", "bibo", "base", Order: 1),
            ]);

            // No detected body: the preference order picks bibo, and the preview must NOT claim it matched.
            var blind = Preview(omp);
            Assert.Equal("bibo", blind.DefaultLayout);
            Assert.False(blind.DefaultLayoutMatchedBody);

            // A body the pack has nothing for is the same story — a guess, not a match.
            var gen3 = Preview(omp, "gen3");
            Assert.Equal("bibo", gen3.DefaultLayout);
            Assert.False(gen3.DefaultLayoutMatchedBody);

            // A body the pack DOES carry is a real match.
            var vanilla = Preview(omp, "gen2");
            Assert.Equal("vanilla", vanilla.DefaultLayout);
            Assert.True(vanilla.DefaultLayoutMatchedBody);

            // Only the gen2 fallback needs the mod's sibling mode raised — bibo↔gen3 is baked by default.
            Assert.False(blind.NeedsAllBodies);      // body unknown: can't say, so don't
            Assert.False(gen3.NeedsAllBodies);       // bibo -> gen3 remaps with no action
            Assert.False(vanilla.NeedsAllBodies);    // matched outright
        });
    }

    [Fact]
    public void A_vanilla_wearer_with_no_vanilla_layout_is_flagged_as_needing_all_bodies()
    {
        With(dir =>
        {
            // Proteus only bakes into gen2 UV space when a mod's sibling mode is AllBodies, and the default
            // is bibo+gen3 — so without this flag the import would be inert for the person making it.
            var omp = MakePack(dir,
            [
                new Layer("layers/bibo.png", "bibo", "base", Order: 0),
                new Layer("layers/gen3.png", "gen3", "base", Order: 1),
            ]);

            var preview = Preview(omp, "gen2");
            Assert.Equal("bibo", preview.DefaultLayout);
            Assert.False(preview.DefaultLayoutMatchedBody);
            Assert.Equal("gen2", preview.WearerBodyType);
            Assert.True(preview.NeedsAllBodies);
        });
    }

    [Fact]
    public void A_single_layout_pack_still_reports_the_body_fit()
    {
        With(dir =>
        {
            // The case with no option group to hang the note off — and the one that most needs saying.
            var omp = MakePack(dir, [new Layer("layers/bibo.png", "bibo", "base")]);

            var preview = Preview(omp, "gen2");
            Assert.Single(preview.Layouts);
            Assert.False(preview.DefaultLayoutMatchedBody);
            Assert.True(preview.NeedsAllBodies);
        });
    }

    [Fact]
    public void One_layout_writes_a_flat_overlay_list_and_no_group()
    {
        With(dir =>
        {
            var omp = MakePack(dir, [new Layer("layers/bibo.png", "bibo", "base")]);
            var preview = Preview(omp);

            var root = Path.Combine(dir, "mod");
            OnionImportService.WriteMod(root, "Ven", "Almaden", preview, Materials(preview), texLoader: null);

            var meta = ReadSidecar(root);
            Assert.Null(meta.OptionGroups);
            var ov = Assert.Single(meta.Overlays!);
            Assert.Equal("bibo", ov.SourceBodyType);
            Assert.Empty(Directory.GetFiles(root, "group_*.json"));

            // Penumbra manifest + a default option carrying the harmless self-swap, as ModCreationService does.
            var pmeta = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "meta.json"))).RootElement;
            Assert.Equal(3, pmeta.GetProperty("FileVersion").GetInt32());
            Assert.Equal("1.0.0", pmeta.GetProperty("Version").GetString());
            Assert.Equal("https://example.invalid/x", pmeta.GetProperty("Website").GetString());
            var def = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "default_mod.json"))).RootElement;
            Assert.Single(def.GetProperty("Swaps").EnumerateObject());
        });
    }

    [Fact]
    public void Layers_are_ordered_by_the_packs_own_Order()
    {
        With(dir =>
        {
            // Same layout, three stacked images, declared out of order in the manifest.
            var omp = MakePack(dir,
            [
                new Layer("layers/c.png", "bibo", "base", Order: 2),
                new Layer("layers/a.png", "bibo", "base", Order: 0),
                new Layer("layers/b.png", "bibo", "base", Order: 1),
            ]);
            var preview = Preview(omp);
            Assert.Equal(["layers/a.png", "layers/b.png", "layers/c.png"], preview.Layers.Select(l => l.File));

            var root = Path.Combine(dir, "mod");
            OnionImportService.WriteMod(root, "Ven", "Almaden", preview, Materials(preview), texLoader: null);
            var meta = ReadSidecar(root);
            Assert.Equal(3, meta.Overlays!.Count);
            Assert.Equal(["overlays/bibo_diffuse_0.png", "overlays/bibo_diffuse_1.png", "overlays/bibo_diffuse_2.png"],
                meta.Overlays.Select(o => o.Diffuse));
        });
    }

    // ── Classification ───────────────────────────────────────────────────────

    [Fact]
    public void A_blend_mode_other_than_Normal_is_skipped_and_the_rest_still_import()
    {
        With(dir =>
        {
            var omp = MakePack(dir,
            [
                new Layer("layers/a.png", "bibo", "base", Mode: "Normal", Order: 0),
                new Layer("layers/b.png", "bibo", "base", Mode: "Multiply", Order: 1),
            ]);
            var preview = Preview(omp);

            var kept = Assert.Single(preview.Layers, l => l.Import);
            Assert.Equal("layers/a.png", kept.File);
            var dropped = Assert.Single(preview.Layers, l => !l.Import);
            Assert.Contains("Multiply", dropped.SkipReason);

            var root = Path.Combine(dir, "mod");
            OnionImportService.WriteMod(root, "Ven", "Almaden", preview, Materials(preview), texLoader: null);
            Assert.Single(ReadSidecar(root).Overlays!);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "Proteus", "overlays")));
        });
    }

    [Theory]
    [InlineData("bibo", "sparkle", "unsupported texture map")]
    [InlineData("teapot", "base", "unsupported UV layout")]
    public void Unknown_layouts_and_maps_are_skipped_with_a_reason(string layout, string map, string reason)
    {
        With(dir =>
        {
            var omp = MakePack(dir, [new Layer("layers/a.png", layout, map)]);
            var preview = Preview(omp);
            var l = Assert.Single(preview.Layers);
            Assert.False(l.Import);
            Assert.Contains(reason, l.SkipReason);
            Assert.False(preview.AnyImportable);
            Assert.Contains(preview.Warnings, w => w.Contains("No layer in this pack can be imported"));
        });
    }

    [Fact]
    public void A_fully_transparent_layer_is_skipped_rather_than_written_as_a_no_op()
    {
        With(dir =>
        {
            var omp = MakePack(dir,
            [
                new Layer("layers/hidden.png", "bibo", "base", Order: 0, Opacity: 0.0),
                new Layer("layers/shown.png", "bibo", "base", Order: 1, Opacity: 1.0),
            ]);
            var preview = Preview(omp);

            var dropped = Assert.Single(preview.Layers, l => !l.Import);
            Assert.Equal("layers/hidden.png", dropped.File);
            Assert.Contains("fully transparent", dropped.SkipReason);

            // Nothing of it reaches disk — no image, no descriptor.
            var root = Path.Combine(dir, "mod");
            OnionImportService.WriteMod(root, "Ven", "Almaden", preview, Materials(preview), texLoader: null);
            Assert.Single(ReadSidecar(root).Overlays!);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "Proteus", "overlays")));
        });
    }

    [Fact]
    public void The_default_layout_uses_the_spelling_the_group_option_is_named_with()
    {
        With(dir =>
        {
            // A pack that spells one layout two ways. The option is named from the FIRST layer, and the
            // default has to match it exactly — Register feeds this string straight to SetModOption, and a
            // name Penumbra doesn't know would leave nothing selected and nothing painting.
            var omp = MakePack(dir,
            [
                new Layer("layers/a.png", "bibo", "base", Order: 0),
                new Layer("layers/b.png", "BIBO", "base", Order: 1),
                new Layer("layers/c.png", "gen3", "base", Order: 2),
            ]);

            var preview = Preview(omp, "bibo");
            Assert.Equal("bibo", preview.DefaultLayout);
            Assert.Equal(["bibo", "gen3"], preview.Layouts);

            var root = Path.Combine(dir, "mod");
            OnionImportService.WriteMod(root, "Ven", "Almaden", preview, Materials(preview), texLoader: null);

            var group = Assert.Single(ReadSidecar(root).OptionGroups!);
            Assert.Contains(preview.DefaultLayout, group.Options.Select(o => o.Name));
            // Both bibo-spelled layers land in the one option, in paint order.
            Assert.Equal(2, group.Options.Single(o => o.Name == "bibo").Overlays.Count);
        });
    }

    [Fact]
    public void A_layer_whose_image_is_missing_from_the_archive_is_skipped()
    {
        With(dir =>
        {
            // Build a valid pack, then rewrite its manifest to name an image it doesn't carry.
            var omp = MakePack(dir, [new Layer("layers/a.png", "bibo", "base")]);
            var pack = OnionPackage.Read(omp);
            pack.Manifest.Layers![0].File = "layers/gone.png";

            var preview = OnionImportService.BuildPreview(omp, pack, null);
            var l = Assert.Single(preview.Layers);
            Assert.False(l.Import);
            Assert.Contains("doesn't contain", l.SkipReason);
        });
    }

    [Fact]
    public void A_non_diffuse_map_lands_in_its_own_slot_and_does_not_generate_a_diffuse()
    {
        With(dir =>
        {
            var omp = MakePack(dir,
            [
                new Layer("layers/n.png", "bibo", "normal", Order: 0),
                new Layer("layers/m.png", "bibo", "multi", Order: 1),
            ]);
            var preview = Preview(omp);

            var root = Path.Combine(dir, "mod");
            OnionImportService.WriteMod(root, "Ven", "Almaden", preview, Materials(preview), texLoader: null);

            var overlays = ReadSidecar(root).Overlays!;
            Assert.Equal(2, overlays.Count);

            var normal = overlays[0];
            Assert.Equal("overlays/bibo_normal_0.png", normal.Normal);
            Assert.Null(normal.Diffuse);
            Assert.False(normal.GenerateDiffuse);

            var mask = overlays[1];
            Assert.Equal("overlays/bibo_mask_1.png", mask.Mask);
            Assert.False(mask.GenerateDiffuse);
        });
    }

    // ── Layer images ─────────────────────────────────────────────────────────

    [Fact]
    public void A_full_opacity_layer_is_copied_byte_for_byte()
    {
        With(dir =>
        {
            var png = SolidPng(8, 8, 10, 20, 30, 255);
            var omp = MakePack(dir, [new Layer("layers/a.png", "bibo", "base", Opacity: 1.0)],
                images: new Dictionary<string, byte[]> { ["layers/a.png"] = png });

            var preview = Preview(omp);
            var root = Path.Combine(dir, "mod");
            OnionImportService.WriteMod(root, "Ven", "Almaden", preview, Materials(preview), texLoader: null);

            var written = File.ReadAllBytes(Path.Combine(root, "Proteus", "overlays", "bibo_diffuse_0.png"));
            Assert.Equal(png, written);
        });
    }

    [Fact]
    public void A_partial_opacity_layer_has_it_baked_into_the_alpha()
    {
        With(dir =>
        {
            var omp = MakePack(dir, [new Layer("layers/a.png", "bibo", "base", Opacity: 0.5)],
                images: new Dictionary<string, byte[]> { ["layers/a.png"] = SolidPng(8, 8, 10, 20, 30, 255) });

            var preview = Preview(omp);
            Assert.Equal(0.5f, Assert.Single(preview.Layers).Opacity, 3);

            var root = Path.Combine(dir, "mod");
            OnionImportService.WriteMod(root, "Ven", "Almaden", preview, Materials(preview), texLoader: null);

            var img = ImageResult.FromMemory(
                File.ReadAllBytes(Path.Combine(root, "Proteus", "overlays", "bibo_diffuse_0.png")),
                ColorComponents.RedGreenBlueAlpha);
            Assert.Equal(8, img.Width);
            // Colour untouched, alpha halved.
            Assert.Equal(10, img.Data[0]);
            Assert.Equal(128, img.Data[3]);
            Assert.All(Enumerable.Range(0, 64), i => Assert.Equal(128, img.Data[i * 4 + 3]));
        });
    }

    // ── Pack reader ──────────────────────────────────────────────────────────

    [Fact]
    public void A_traversal_entry_is_refused_outright()
    {
        With(dir =>
        {
            var omp = MakePack(dir, [new Layer("layers/a.png", "bibo", "base")], extraEntry: "../evil.png");
            var ex = Assert.Throws<InvalidDataException>(() => OnionPackage.Read(omp));
            Assert.Contains("unsafe entry path", ex.Message);
            Assert.False(File.Exists(Path.Combine(dir, "..", "evil.png")));
        });
    }

    [Fact]
    public void A_zip_without_a_manifest_is_not_a_pack()
    {
        With(dir =>
        {
            var path = Path.Combine(dir, "notapack.omp");
            using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
                zip.CreateEntry("layers/a.png").Open().Dispose();

            var ex = Assert.Throws<InvalidDataException>(() => OnionPackage.Read(path));
            Assert.Contains("no meta.json", ex.Message);
        });
    }

    [Fact]
    public void Unsupported_pack_features_are_reported_but_do_not_block_the_import()
    {
        With(dir =>
        {
            var omp = MakePack(dir,
                [new Layer("layers/a.png", "bibo", "base", Races: "[\"Viera\"]", GeneratedFrom: "bibo")],
                formatVersion: 99, groups: "[{\"Name\":\"Colour\"}]");

            var preview = Preview(omp);
            Assert.True(preview.AnyImportable);
            Assert.Contains(preview.Warnings, w => w.Contains("format version 99"));
            Assert.Contains(preview.Warnings, w => w.Contains("Onion option group"));
            Assert.Contains(preview.Warnings, w => w.Contains("restricted to specific races"));
            Assert.Contains(preview.Warnings, w => w.Contains("generated by Onion"));

            var root = Path.Combine(dir, "mod");
            OnionImportService.WriteMod(root, "Ven", "Almaden", preview, Materials(preview), texLoader: null);
            Assert.Single(ReadSidecar(root).Overlays!);
        });
    }

    [Fact]
    public void Name_and_author_come_from_the_manifest()
    {
        With(dir =>
        {
            var omp = MakePack(dir, [new Layer("layers/a.png", "bibo", "base")], name: "  Ven  ", author: "Almaden");
            var preview = Preview(omp);
            Assert.Equal("Ven", preview.Name);
            Assert.Equal("Almaden", preview.Author);
            Assert.Equal("a description", preview.Description);
            Assert.Equal("1.0.0", preview.Version);
        });
    }

    // ── Body catalogue ───────────────────────────────────────────────────────

    [Fact]
    public void The_body_catalogue_probes_vanilla_materials_and_swaps_the_suffix()
    {
        var probed = new List<string>();
        var catalog = new BodyMaterialCatalog(p =>
        {
            probed.Add(p);
            return p is "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl"
                     or "chara/human/c1801/obj/body/b0001/material/v0001/mt_c1801b0001_a.mtrl";
        });

        Assert.Equal(
        [
            "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl",
            "chara/human/c1801/obj/body/b0001/material/v0001/mt_c1801b0001_bibo.mtrl",
        ], catalog.ForSuffix("_bibo.mtrl"));

        // Only the vanilla suffix is ever probed — it's the one that actually ships in the game data.
        Assert.All(probed, p => Assert.EndsWith("_a.mtrl", p));

        // The probe runs once; a second suffix reuses the discovered stems.
        var before = probed.Count;
        Assert.Equal(2, catalog.ForSuffix("_b.mtrl").Count);
        Assert.Equal(before, probed.Count);
    }

    [Fact]
    public void The_body_catalogue_falls_back_when_the_game_data_answers_nothing()
    {
        var catalog = new BodyMaterialCatalog(_ => false);
        var paths = catalog.ForSuffix("_bibo.mtrl");
        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.EndsWith("_bibo.mtrl", p));
        Assert.Contains("chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl", paths);
        Assert.False(catalog.FromGameData);
    }

    [Fact]
    public void The_body_catalogue_re_probes_after_a_miss_instead_of_pinning_the_fallback()
    {
        // The fallback names only female bodies. A session that fell back once and then remembered it
        // would write every later import so it can never apply to a male character.
        var live = false;
        var catalog = new BodyMaterialCatalog(p => live &&
            p == "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl");

        var cold = catalog.ForSuffix("_b.mtrl");
        Assert.False(catalog.FromGameData);
        Assert.DoesNotContain("chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_b.mtrl", cold);

        live = true;
        var warm = catalog.ForSuffix("_b.mtrl");
        Assert.True(catalog.FromGameData);
        Assert.Equal(["chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_b.mtrl"], warm);

        // And once it HAS a real answer it stops probing — the game's body list can't change mid-session.
        live = false;
        Assert.Single(catalog.ForSuffix("_b.mtrl"));
        Assert.True(catalog.FromGameData);
    }
}
