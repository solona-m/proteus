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

    /// <summary>
    /// A v4 pack with one option Proteus can take over and one it must refuse: "Bound" ships the material
    /// its mesh names, "Orphan" does not.
    /// <para/>
    /// Both options claim the SAME game path from different files — the shape the sample piercings pack
    /// uses, and the whole reason this feature exists, since Penumbra can only ever apply one of them.
    /// Giving them separate paths would leave the strip's real hazard untested: an imported option putting
    /// a path into the taken set and a refused option losing its redirect for sharing it.
    /// </summary>
    private static string MixedPack(string dir)
    {
        var bound  = SyntheticModel.Build([], new SyntheticModel.Mesh("/mt_bound.mtrl",  new SyntheticModel.Sub(0)));
        var orphan = SyntheticModel.Build([], new SyntheticModel.Mesh("/mt_orphan.mtrl", new SyntheticModel.Sub(0)));

        var manifest = """
        {
          "FileVersion": 4,
          "Name": "Mixed",
          "Author": "Someone",
          "DefaultData": { "Files": { "chara/x/mt_bound.mtrl": "common\\bound.mtrl" } },
          "Groups": [
            {
              "Name": "Pieces",
              "Type": "Multi",
              "Options": [
                { "Name": "Bound",  "Files": { "chara/equipment/e0000/model/c0201e0000_top.mdl": "bound\\model.mdl" } },
                { "Name": "Orphan", "Files": { "chara/equipment/e0000/model/c0201e0000_top.mdl": "orphan\\model.mdl" } }
              ]
            }
          ]
        }
        """;
        return WritePack(dir, manifest, new[]
        {
            ("common/bound.mtrl", new byte[64]),
            ("bound/model.mdl", bound),
            ("orphan/model.mdl", orphan),
        });
    }

    /// <summary>
    /// The strip takes over only what the sidecar names.
    /// <para/>
    /// It used to remove EVERY .mdl redirect in the pack, including those of pieces this import refused —
    /// so an option whose mesh named a material the pack does not ship stopped being published by Penumbra
    /// and was never picked up by Proteus. It rendered nothing at all after an import that reported it, in
    /// one line, as skipped.
    /// </summary>
    [Fact]
    public void An_option_the_import_refuses_keeps_its_own_model_redirect()
    {
        var dir = TempDir();
        try
        {
            var preview = ContentImportService.Inspect(MixedPack(dir));

            // Precondition: exactly one of the two is importable. Without this the assertions below could
            // pass on a pack where nothing was refused in the first place.
            var refused = Assert.Single(preview.Units, u => !u.Import);
            Assert.Equal("Orphan", refused.Option);
            // NotNull first: !Import also admits a plan with no problem and no bindings, and Assert.Contains
            // on a null string throws ArgumentNullException instead of failing as an assertion.
            var problem = Assert.Single(refused.Variants).Problem;
            Assert.NotNull(problem);
            Assert.Contains("mt_orphan.mtrl", problem);

            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Mixed", "Someone", preview);

            var options = (JsonArray)((JsonArray)((JsonObject)JsonNode.Parse(
                File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)))!)["Groups"]!)[0]!["Options"]!;

            JsonObject Files(string name) => (JsonObject)options
                .First(o => (string?)o!["Name"] == name)!["Files"]!;

            // Taken over: the sidecar names it, so Penumbra must not publish it too.
            Assert.Empty(Files("Bound"));

            // Refused: nothing else is going to publish this, so the redirect stays exactly as authored —
            // even though the option beside it was taken over under the very same game path.
            Assert.Equal("orphan\\model.mdl",
                (string?)Files("Orphan")["chara/equipment/e0000/model/c0201e0000_top.mdl"]);

            // And the sidecar carries only the piece that was taken over.
            var sidecar = JsonSerializer.Deserialize<ProteusMetadata>(
                File.ReadAllText(Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, "metadata.json")),
                ProteusJson.MetadataWrite)!;
            var piece = Assert.Single(sidecar.ContentGroups!.SelectMany(g => g.Options).SelectMany(o => o.Pieces));
            // Keyed by race, since the path carries c0201 — read it back the way the composite does.
            Assert.Contains("bound", piece.ModelFor("0201"), StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// An Imc group survives the import as something the composite can act on.
    /// <para/>
    /// Penumbra's own edit lands on the pack's equipment set, which nobody wears once Proteus has taken the
    /// models over, so the mask has to reach the sidecar or the toggle silently does nothing — which is
    /// exactly how Denim Shorts' "hide panty strap" arrived.
    /// </summary>
    [Fact]
    public void An_imc_hide_group_is_read_from_the_pack_and_recorded_in_the_sidecar()
    {
        using var built = SyntheticPack.ImcToggled("0101", "Toggles", "atr_sne", "atr_hiz");

        // Read off the pack: the group carries the identifier and masks, not the options' Files.
        var pack = PenumbraPackage.Read(built.Path);
        var g = Assert.Single(pack.Groups, x => string.Equals(x.Type, "Imc", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(6058, g.ImcSetId);
        Assert.Equal("Legs", g.ImcSlot);
        Assert.Equal(3, g.DefaultAttributeMask);       // both bits on, so nothing hides by default
        Assert.Equal([1, 2], g.Options.Select(o => (int)o.AttributeMask));
        Assert.All(g.Options, o => Assert.Empty(o.Files));

        // And into the sidecar, which is what the composite reads.
        var preview = ContentImportService.Inspect(built.Path);
        var sidecar = ContentImportService.BuildSidecar(preview, "Synthetic", "Synthetic");
        var rec = Assert.Single(sidecar.ContentAttributes!);
        Assert.Equal("Toggles", rec.Group);
        Assert.Equal(6058, rec.SetId);
        Assert.Equal(3, rec.DefaultMask);
        Assert.Equal(1, rec.Options["atr_sne Hide"]);
        Assert.Equal(2, rec.Options["atr_hiz Hide"]);

        // The masks compose the way the composite will use them: selecting an option CLEARS its bits.
        Assert.Equal(3, rec.MaskFor(null));
        Assert.Equal(2, rec.MaskFor(["atr_sne Hide"]));
        Assert.Equal(0, rec.MaskFor(["atr_sne Hide", "atr_hiz Hide"]));

        // End to end: the recorded group resolves against the model's own attribute table to the name whose
        // submeshes the writer then drops.
        Assert.Equal(["atr_sne"], SecondSkinService.HiddenAttributes(
            sidecar.ContentAttributes,
            "chara/equipment/e6058/model/c0101e6058_dwn.mdl",
            ["atr_sne", "atr_hiz"],
            new Dictionary<string, List<string>> { ["Toggles"] = ["atr_sne Hide"] })!.Order());
    }

    /// <summary>A pack with no Imc group records nothing — most packs, and the field stays absent.</summary>
    [Fact]
    public void A_pack_without_imc_toggles_records_no_attribute_groups()
    {
        using var plain = SyntheticPack.AttributeDriven("0201", "Accessories",
            new SyntheticPack.Toggle("Belly Dermals", "atrx_belly"));
        var sidecar = ContentImportService.BuildSidecar(
            ContentImportService.Inspect(plain.Path), "Synthetic", "Synthetic");
        Assert.Null(sidecar.ContentAttributes);
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
            Assert.Equal(2, preview.TotalUnits);                 // the mtrl group ships no model
            Assert.Equal(2, preview.ImportableUnits);
            Assert.All(preview.Units, u => Assert.Equal("Top", u.Group));

            // Every option ships exactly one garment, so the author's own group already selects them and
            // nothing is added: this is the shipped piercings-pack shape and it must not change.
            Assert.Null(preview.PieceGroupName);
            Assert.All(preview.Units, u => Assert.Null(u.GateOption));

            var piece = preview.Units[0].Variants.Single();
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
            var piece = preview.Units[0].Variants.Single();
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
            // Through the accessor, not the raw field: a model path names the race it was authored for, so
            // the importer records it as a per-race variant even when the pack ships only one.
            Assert.Equal("top/heart/model.mdl", piece.ModelFor("0201"));
            Assert.Equal("common/piercings.mtrl", piece.MaterialFor(leaf));
            Assert.Equal(ShellSurfaceKind.Body, piece.Surface);
            // The sidecar path must resolve against the mod root, since that is what the compositor reads.
            Assert.True(File.Exists(Path.Combine(root, piece.ModelFor("0201")!.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A model the pack's OWN checkboxes already drive gets no checkbox of ours.
    /// <para/>
    /// The synthesized "Pieces" group exists because Penumbra cannot say "apply only this model out of an
    /// always-on set". That reason evaporates when the pack holds its pieces in one model and toggles them
    /// by attribute — it already has a checkbox per piece. Adding ours on top means a box to tick before any
    /// of theirs mean anything, and ticking it equips the lot.
    /// <para/>
    /// Built rather than read off disk. This test used to name a pack at an absolute path on one machine's
    /// Desktop and return at its first line when it wasn't there — passing, silently, over behaviour that
    /// had live bugs in it. See <see cref="SyntheticPack"/>.
    /// </summary>
    [Fact]
    public void A_model_the_packs_own_options_toggle_needs_no_gate_of_ours()
    {
        using var built = SyntheticPack.AttributeDriven("0801", "Accessories",
            new SyntheticPack.Toggle("Belly Dermals", "atrx_belly"),
            new SyntheticPack.Toggle("Collarbone Top", "atrx_collar"));
        var RacePack = built.Path;

        var pack = PenumbraPackage.Read(RacePack);

        // Its options redirect no files at all — they flip named attributes, which a reader that only looks
        // at Files would see as empty options and conclude the pack selects nothing.
        var toggles = pack.Groups.SelectMany(g => g.Options).SelectMany(o => o.Attributes).ToList();
        Assert.NotEmpty(toggles);
        Assert.Contains(toggles, a => a.StartsWith("atrx_", StringComparison.Ordinal));
        Assert.All(pack.Groups.SelectMany(g => g.Options), o => Assert.Empty(o.Files));

        // So the import adds no group of its own, and nothing is gated.
        var preview = ContentImportService.Inspect(RacePack);
        Assert.True(preview.AnyImportable);
        Assert.Null(preview.PieceGroupName);
        Assert.All(preview.Units, u => Assert.Null(u.GateOption));

        // And the sidecar still carries the piece. Dropping the gate must not drop the CONTENT with it —
        // an ungated piece belongs in the always-on list, which is what the composite reads.
        var sidecar = ContentImportService.BuildSidecar(preview, "Synthetic", "Synthetic");
        Assert.Null(sidecar.PieceGroupName);
        Assert.True(sidecar.HasContent);
        var piece = Assert.Single(sidecar.Content!);
        Assert.Null(piece.GateOption);                       // nothing to tick before it applies
        Assert.Equal("0801", Assert.Single(piece.ModelCodes));
        Assert.NotEmpty(piece.Materials);

        // Each material is tied to the pack's own options, so the colour panel can show a tab per piece the
        // user is actually wearing and NAME it after the checkbox that turns it on. Without this every
        // material gets a tab whatever is ticked, labelled with a filename nobody can read.
        // NotEmpty as well as NotNull: Assert.All passes vacuously on an empty list, and the [0] below would
        // then report a raw index exception in place of the useful failure.
        Assert.NotNull(piece.MaterialGates);
        Assert.NotEmpty(piece.MaterialGates!);
        Assert.All(piece.MaterialGates!, g =>
        {
            Assert.NotEmpty(g.Material);
            Assert.Contains(pack.Groups, x => x.Name == g.Group);
            Assert.Contains(pack.Groups.Single(x => x.Name == g.Group).Options, o => o.Name == g.Option);
        });

        // Real names off the pack's own tree, not file names.
        var named = piece.MaterialGates!.Select(g => g.Option).ToList();
        Assert.Contains("Belly Dermals", named);
        Assert.Contains("Collarbone Top", named);

        // And a material really is gated — GatesFor is what the panel filters on, so it has to resolve
        // leaf-to-leaf the way the model names them.
        var gatedLeaf = piece.MaterialGates![0].Material;
        Assert.NotEmpty(piece.GatesFor(gatedLeaf));
        Assert.NotEmpty(piece.GatesFor(gatedLeaf.TrimStart('/')));   // slash-insensitive, like MaterialFor
    }

    /// <summary>
    /// A pack built for one race says so BEFORE it is imported.
    /// <para/>
    /// Proteus does not resize geometry between races, so such a pack only ever appears on that race.
    /// Finding that out afterwards means staring at an enabled mod that shows nothing — and the shared-shape
    /// case, which is nearly every pack, must stay silent or the warning means nothing.
    /// </summary>
    [Fact]
    public void A_pack_built_for_one_race_is_flagged_in_the_preview()
    {
        // A single met model at c0801 — Miqo'te F — carrying an accessory set.
        using (var racial = SyntheticPack.AttributeDriven("0801", "Accessories",
                   new SyntheticPack.Toggle("Belly Dermals", "atrx_belly")))
        {
            var preview = ContentImportService.Inspect(racial.Path);
            var warning = Assert.Single(preview.Warnings, w => w.Contains("Miqote F", StringComparison.Ordinal));
            Assert.Contains("only appear on a character of that race", warning, StringComparison.Ordinal);
        }

        // And the ordinary shape stays quiet: c0201 is what the game resizes for everyone, so there is
        // nothing to warn about. Same pack in every other respect — the race code is the only variable, which
        // is the point.
        using (var shared = SyntheticPack.AttributeDriven("0201", "Accessories",
                   new SyntheticPack.Toggle("Belly Dermals", "atrx_belly")))
        {
            var preview = ContentImportService.Inspect(shared.Path);
            Assert.DoesNotContain(preview.Warnings,
                w => w.Contains("only appear on a character of that race", StringComparison.Ordinal));
        }
    }
}
