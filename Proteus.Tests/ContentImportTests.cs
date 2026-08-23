using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The .pmp content importer: reading both Penumbra manifest layouts, deciding which options ship
/// appendable geometry, and the two things the write does — strip every model redirect, and name those
/// models in the Proteus sidecar instead.
/// </summary>
public class ContentImportTests
{
    private const string SamplePack = @"E:\ModPacks\Neolithe Piercings for Proteus.pmp";

    // ── synthetic packs ──────────────────────────────────────────────────────

    /// <summary>
    /// A model whose meshes are bound to <paramref name="materialName"/>. Lifted out of the sample pack
    /// when it is present so the parse is exercised against a real .mdl, and null otherwise — the tests
    /// that need geometry skip, like every other model-backed test in this suite.
    /// </summary>
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

    private static string WritePack(string dir, string manifestJson, IEnumerable<(string Entry, byte[] Data)> files)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "pack.pmp");
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        void Add(string name, byte[] data)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(data, 0, data.Length);
        }
        Add("meta.json", System.Text.Encoding.UTF8.GetBytes(manifestJson));
        foreach (var (entry, data) in files) Add(entry, data);
        return path;
    }

    /// <summary>A v4 pack: one always-on material group, one multi-select group with two mesh options.</summary>
    private static string V4Pack(string dir, byte[] model, byte[] mtrl, string boundMaterialLeaf)
    {
        var manifest = $$"""
        {
          "FileVersion": 4,
          "Name": "Sample Piercings",
          "Author": "Someone",
          "Version": "1.0.0",
          "Website": "https://example.invalid",
          "DefaultData": { "Version": 0 },
          "Groups": [
            {
              "Name": "BASE INSTALL",
              "Type": "Single",
              "Options": [
                { "Name": "Install", "Files": { "chara/x/{{boundMaterialLeaf}}": "common\\piercings.mtrl" } }
              ]
            },
            {
              "Name": "Top",
              "Type": "Multi",
              "Options": [
                { "Name": "Basic", "Files": { "chara/equipment/e0000/model/c0201e0000_top.mdl": "top\\basic\\model.mdl" } },
                { "Name": "Heart", "Files": { "chara/equipment/e0000/model/c0201e0000_top.mdl": "top\\heart\\model.mdl" } }
              ]
            }
          ]
        }
        """;
        return WritePack(dir, manifest, new[]
        {
            ("common/piercings.mtrl", mtrl),
            ("top/basic/model.mdl", model),
            ("top/heart/model.mdl", model),
        });
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "proteus-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // ── reading ──────────────────────────────────────────────────────────────

    [Fact]
    public void Reads_a_v4_pack_and_binds_meshes_to_the_packs_own_material()
    {
        var model = SampleModel();
        if (model == null) return;

        var dir = TempDir();
        try
        {
            // The pack ships the very material the model's drawn mesh names, so both mesh options bind.
            var leaf = SecondSkinService
                .UsedMaterialNames(model, SecondSkinWriter.MaterialNames(model))[0].TrimStart('/');
            var pmp = V4Pack(dir, model, new byte[64], leaf);

            var preview = ContentImportService.Inspect(pmp);

            Assert.Equal(4, preview.Pack.FileVersion);
            Assert.Equal("Sample Piercings", preview.Name);
            Assert.Equal(2, preview.Options.Count);              // the mtrl group ships no model
            Assert.Equal(2, preview.ImportableOptions);
            Assert.All(preview.Options, o => Assert.Equal("Top", o.Group));

            var piece = preview.Options[0].Pieces.Single();
            Assert.Null(piece.Problem);
            Assert.Empty(piece.Unbound);
            Assert.Equal("common/piercings.mtrl", piece.Bindings[leaf]);
            Assert.True(piece.Vertices > 0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_mesh_naming_a_material_the_pack_does_not_ship_is_reported_not_guessed()
    {
        var model = SampleModel();
        if (model == null) return;

        var dir = TempDir();
        try
        {
            // The pack ships a material under a DIFFERENT name than the mesh declares. Binding is by name,
            // so the piece is refused rather than bound to the only material lying around.
            var pmp = V4Pack(dir, model, new byte[64], "mt_something_else.mtrl");

            var preview = ContentImportService.Inspect(pmp);

            Assert.False(preview.AnyImportable);
            var piece = preview.Options[0].Pieces.Single();
            Assert.NotNull(piece.Problem);
            Assert.Empty(piece.Bindings);
            Assert.NotEmpty(piece.Unbound);
            // The message has to name the material the MESH declares — that is what the author must rebind.
            Assert.Contains(piece.Unbound[0], piece.Problem);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reads_a_legacy_v3_pack_from_its_group_files()
    {
        var dir = TempDir();
        try
        {
            var manifest = """
            { "FileVersion": 3, "Name": "Legacy", "Author": "A" }
            """;
            byte[] Utf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);
            var pmp = WritePack(dir, manifest, new[]
            {
                ("default_mod.json", Utf8("""{ "Files": { "chara/a.tex": "a.tex" } }""")),
                ("group_002_second.json", Utf8("""
                 { "Name": "Second", "Type": "Multi",
                   "Options": [ { "Name": "B", "Files": { "chara/b.mdl": "b.mdl" } } ] }
                 """)),
                ("group_001_first.json", Utf8("""
                 { "Name": "First", "Type": "Single",
                   "Options": [ { "Name": "A", "Files": { "chara/c.mtrl": "c.mtrl" } } ] }
                 """)),
                ("a.tex", new byte[4]), ("b.mdl", new byte[4]), ("c.mtrl", new byte[4]),
            });

            var pack = PenumbraPackage.Read(pmp);

            Assert.Equal(3, pack.FileVersion);
            Assert.Single(pack.DefaultFiles);
            // Ordered by the NUMBER in the filename, not by the archive's entry order.
            Assert.Equal(new[] { "First", "Second" }, pack.Groups.Select(g => g.Name).ToArray());
            Assert.Equal(new[] { 1, 2 }, pack.Groups.Select(g => g.Index).ToArray());
            Assert.Equal("group_001_first.json", pack.Groups[0].Entry);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_unsafe_entry_path_is_refused_outright()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "evil.pmp");
            using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            {
                using (var s = zip.CreateEntry("meta.json").Open())
                {
                    var b = System.Text.Encoding.UTF8.GetBytes("{ \"FileVersion\": 4, \"Name\": \"x\" }");
                    s.Write(b, 0, b.Length);
                }
                using (zip.CreateEntry("../escape.txt").Open()) { }
            }

            Assert.Throws<InvalidDataException>(() => PenumbraPackage.Read(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── writing ──────────────────────────────────────────────────────────────

    [Fact]
    public void Writing_strips_every_model_redirect_and_mirrors_the_groups_into_the_sidecar()
    {
        var model = SampleModel();
        if (model == null) return;

        var dir = TempDir();
        try
        {
            var leaf = SecondSkinService
                .UsedMaterialNames(model, SecondSkinWriter.MaterialNames(model))[0].TrimStart('/');
            var pmp = V4Pack(dir, model, new byte[64], leaf);
            var preview = ContentImportService.Inspect(pmp);

            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Sample Piercings", "Someone", preview);

            // Every file the pack shipped is still on disk, in the pack's own layout.
            Assert.True(File.Exists(Path.Combine(root, "top", "heart", "model.mdl")));
            Assert.True(File.Exists(Path.Combine(root, "common", "piercings.mtrl")));

            // …but Penumbra no longer publishes the models. If it did, the two Multi options would fight
            // over one game path and only one could ever apply.
            var manifest = (JsonObject)JsonNode.Parse(
                File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)))!;
            var groups = (JsonArray)manifest["Groups"]!;
            foreach (var g in groups)
                foreach (var o in (JsonArray)g!["Options"]!)
                    if (o!["Files"] is JsonObject files)
                        Assert.DoesNotContain(files, f => f.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase));

            // The material redirect is untouched — the pack still serves its own textures and materials.
            var install = (JsonObject)((JsonArray)groups[0]!["Options"]!)[0]!;
            Assert.Single((JsonObject)install["Files"]!);

            // The sidecar names the models instead, one option per Penumbra option.
            var sidecar = JsonSerializer.Deserialize<ProteusMetadata>(
                File.ReadAllText(Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, "metadata.json")),
                ProteusJson.MetadataRead)!;

            var group = Assert.Single(sidecar.ContentGroups!);
            Assert.Equal("Top", group.PenumbraGroupName);
            Assert.Equal(new[] { "Basic", "Heart" }, group.Options.Select(o => o.Name).ToArray());

            var piece = group.Options[1].Pieces.Single();
            Assert.Equal("top/heart/model.mdl", piece.Model);
            Assert.Equal("common/piercings.mtrl", piece.MaterialFor(leaf));
            Assert.Equal(ShellSurfaceKind.Body, piece.Surface);
            // The sidecar path must resolve against the mod root, since that is what the compositor reads.
            Assert.True(File.Exists(Path.Combine(root, piece.Model.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally { Directory.Delete(dir, true); }
    }
}
