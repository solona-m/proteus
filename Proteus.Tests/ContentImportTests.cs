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
        using var built = SyntheticPack.ImcToggled("0101", "Toggles", "atr_dv_a", "atr_dv_b");

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
        Assert.Equal(1, rec.Options["atr_dv_a Hide"]);
        Assert.Equal(2, rec.Options["atr_dv_b Hide"]);

        // The masks compose the way the composite will use them: selecting an option CLEARS its bits.
        Assert.Equal(3, rec.MaskFor(null));
        Assert.Equal(2, rec.MaskFor(["atr_dv_a Hide"]));
        Assert.Equal(0, rec.MaskFor(["atr_dv_a Hide", "atr_dv_b Hide"]));

        // End to end: the recorded group resolves against the model's own attribute table to the name whose
        // submeshes the writer then drops.
        Assert.Equal(["atr_dv_a"], SecondSkinService.HiddenAttributes(
            sidecar.ContentAttributes,
            "chara/equipment/e6058/model/c0101e6058_dwn.mdl",
            ["atr_dv_a", "atr_dv_b"],
            new Dictionary<string, List<string>> { ["Toggles"] = ["atr_dv_a Hide"] })!.Order());
    }

    /// <summary>
    /// A pack whose garment has "ex" bones records the extra skeleton, and which body part must ask for it.
    /// <para/>
    /// The bones are not in the model — they live in a skeleton the game loads only when the EST table
    /// points at it. The pack aims that entry at the set it replaces; Proteus moves the geometry onto a host
    /// accessory, which has no EST at all, so unless this reaches the sidecar the bones never load and the
    /// garment hangs off the root.
    /// </summary>
    [Fact]
    public void A_pack_with_ex_bones_records_its_extra_skeleton_and_the_body_part_that_loads_it()
    {
        using var built = SyntheticPack.EstBearing("Fabric", "Satin", "Body", 6085, alsoZeroEntry: true);

        // Read off the pack. A real pack repeats the entry once per race; the reader keeps slot/set/entry
        // and drops the races, because a composite dresses ONE character.
        var pack = PenumbraPackage.Read(built.Path);
        var opt = Assert.Single(pack.Groups.SelectMany(g => g.Options));
        Assert.Equal([("Body", 6085, 6085), ("Body", 6085, 0)],
            opt.Est.Select(e => (e.Slot, e.SetId, e.Entry)));

        var sidecar = ContentImportService.BuildSidecar(
            ContentImportService.Inspect(built.Path), "Synthetic", "Synthetic");

        // Only the real skeleton survives. Entry 0 means "no extra skeleton": writing it would not enable
        // anything, it would CLEAR whichever body part the composite aimed it at.
        var rec = Assert.Single(sidecar.ContentSkeletons!);
        Assert.Equal("Body", rec.Slot);
        Assert.Equal(6085, rec.Entry);
        Assert.Equal("Fabric", rec.Group);
        Assert.Equal("Satin", rec.Option);
    }

    /// <summary>
    /// Which body part's entry gets written, resolved from what the character is actually drawing.
    /// <para/>
    /// "Body" means the chest piece — the set the report named — and a BARE chest still has an answer, since
    /// the equipment walk filters e0000 out and the bare-body walk is where that case lives.
    /// </summary>
    [Fact]
    public void The_est_entry_targets_the_body_part_the_character_is_drawing()
    {
        var worn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["top"] = "chara/equipment/e0233/model/c0201e0233_top.mdl",
            ["met"] = "chara/equipment/e6112/model/c0201e6112_met.mdl",
        };
        var bare = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["top"] = "chara/equipment/e0000/model/c0201e0000_top.mdl",
        };
        string[] humanParts = ["chara/human/c0201/obj/hair/h0101/model/c0201h0101_hir.mdl"];

        // Body -> the worn chest piece. This is the number the whole feature turns on.
        Assert.Equal(233, SecondSkinService.EstSetId("Body", worn, bare, humanParts));
        Assert.Equal(6112, SecondSkinService.EstSetId("Head", worn, bare, humanParts));
        Assert.Equal(101, SecondSkinService.EstSetId("Hair", worn, bare, humanParts));

        // Bare chest: e0000 is filtered out of the equipment walk, so without the bare-body fallback this
        // would be null and a naked character's ex bones would silently not load.
        Assert.Equal(0, SecondSkinService.EstSetId("Body", null, bare, humanParts));

        // Nothing drawn there, and a slot name we don't know: null rather than a guess. The entry is written
        // onto someone else's item, so guessing moves a skeleton the user never asked about.
        Assert.Null(SecondSkinService.EstSetId("Face", worn, bare, humanParts));
        Assert.Null(SecondSkinService.EstSetId("Body", null, null, null));
        Assert.Null(SecondSkinService.EstSetId("Elbow", worn, bare, humanParts));
    }

    /// <summary>
    /// The manipulation Proteus writes is the one Penumbra reads.
    /// <para/>
    /// Pinned against a literal lifted out of a real installed mod ("Always a Bridesmaid"), because this is
    /// serialised straight into the managed mod's meta and a wrong field name would simply be ignored —
    /// the bones would not load and nothing anywhere would say why.
    /// </summary>
    [Fact]
    public void The_est_manipulation_matches_penumbras_own_shape()
    {
        // c0201 = Midlander female. Set 233 worn on the chest, loading extra skeleton 6085.
        var json = JsonSerializer.Serialize(
            SecondSkinService.EstManipulation("0201", "Body", 233, 6085));
        var m = JsonNode.Parse(json)!;

        Assert.Equal("Est", (string?)m["Type"]);
        var inner = m["Manipulation"]!;
        Assert.Equal("Female", (string?)inner["Gender"]);
        Assert.Equal("Midlander", (string?)inner["Race"]);
        Assert.Equal(233, (int?)inner["SetId"]);
        Assert.Equal("Body", (string?)inner["Slot"]);
        Assert.Equal(6085, (int?)inner["Entry"]);

        // Exactly those five fields — Penumbra matches an identifier by its whole shape.
        Assert.Equal(["Entry", "Gender", "Race", "SetId", "Slot"],
            inner.AsObject().Select(p => p.Key).Order());

        // The race half comes from the character's code, not the pack's.
        var male = JsonNode.Parse(JsonSerializer.Serialize(
            SecondSkinService.EstManipulation("0101", "Head", 1, 2)))!["Manipulation"]!;
        Assert.Equal("Male", (string?)male["Gender"]);
        Assert.Equal("Midlander", (string?)male["Race"]);
    }

    /// <summary>
    /// A material the pack ships more than once records every game path it is published under, options
    /// first — so the composite can ask Penumbra which one is live instead of using whichever the importer
    /// happened to see first.
    /// <para/>
    /// This is the dye-and-metal case every dress mod has. deadrose redirects one dress material from nine
    /// different files; with a single path baked at import, eight of its nine dye options published the
    /// wrong material.
    /// </summary>
    [Fact]
    public void A_material_shipped_under_several_paths_records_them_all_options_first()
    {
        var dir = TempDir();
        try
        {
            var model = SyntheticModel.Build([],
                new SyntheticModel.Mesh("/mt_dress.mtrl", new SyntheticModel.Sub(0)));

            // Both candidates are OPTION redirects, and the fixed one is declared FIRST — the deadrose
            // shape. So neither "options before default data" nor declaration order picks the right one;
            // only counting the files behind each path does.
            var manifest = """
            {
              "FileVersion": 4,
              "Name": "Dyeable",
              "Author": "Someone",
              "DefaultData": { "Files": {} },
              "Groups": [
                {
                  "Name": "Main", "Type": "Multi",
                  "Options": [
                    { "Name": "Install", "Files": { "chara/equipment/e0043/material/v0002/mt_dress.mtrl": "base\\mt_dress.mtrl" } }
                  ]
                },
                {
                  "Name": "Dye", "Type": "Single",
                  "Options": [
                    { "Name": "Gold",   "Files": { "chara/equipment/e0043/material/v0001/mt_dress.mtrl": "gold\\mt_dress.mtrl" } },
                    { "Name": "Silver", "Files": { "chara/equipment/e0043/material/v0001/mt_dress.mtrl": "silver\\mt_dress.mtrl" } }
                  ]
                },
                {
                  "Name": "Piece", "Type": "Multi",
                  "Options": [
                    { "Name": "Dress", "Files": { "chara/equipment/e0043/model/c0201e0043_top.mdl": "dress\\model.mdl" } }
                  ]
                }
              ]
            }
            """;
            var pmp = WritePack(dir, manifest, new[]
            {
                ("base/mt_dress.mtrl", new byte[64]),
                ("gold/mt_dress.mtrl", new byte[64]),
                ("silver/mt_dress.mtrl", new byte[64]),
                ("dress/model.mdl", model),
            });

            var sidecar = ContentImportService.BuildSidecar(
                ContentImportService.Inspect(pmp), "Dyeable", "Someone");
            var piece = Assert.Single(
                sidecar.ContentGroups!.SelectMany(g => g.Options).SelectMany(o => o.Pieces));

            // Both paths, the DYED one first: two files compete over v0001, one sits at v0002. It is
            // declared second and is not the default data, so counting is the only thing that finds it.
            Assert.Equal(
                ["chara/equipment/e0043/material/v0001/mt_dress.mtrl",
                 "chara/equipment/e0043/material/v0002/mt_dress.mtrl"],
                piece.GamePathsFor("mt_dress.mtrl"));

            // Reachable with or without the model's leading slash, like MaterialFor.
            Assert.Equal(2, piece.GamePathsFor("/mt_dress.mtrl").Count);

            // And the single baked file is still recorded as the fallback.
            Assert.NotNull(piece.MaterialFor("mt_dress.mtrl"));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A pack shipping one material under several IMC variant folders publishes the one it actually
    /// DRESSED, not whichever it declared first.
    /// <para/>
    /// [LOONY] Light the Way is the shape: nine variant folders, one file each, so the competing-files rule
    /// ties and declaration order handed it v0001. Eight of the nine are stubs pointing at a shared 2 KB
    /// placeholder set; v0007 carries 33 MB of real textures, and the nine colour tables come to only two
    /// distinct values. Which is what TexTools' "Apply to All Variants" prevents from the other end.
    /// </summary>
    [Fact]
    public void A_material_shipped_under_several_variants_publishes_the_dressed_one()
    {
        var dir = TempDir();
        try
        {
            var model = SyntheticModel.Build([],
                new SyntheticModel.Mesh("/mt_lamp.mtrl", new SyntheticModel.Sub(0)));

            // Two variant folders, one file each, so the competing-files key ties. v0001 is declared FIRST
            // and is the stub; v0007 is the dressed one, naming one more texture.
            //
            // The stub's single texture is deliberately BIGGER than the dressed one's two put together, so
            // the size key alone would pick the wrong path and only the COUNT key can decide. Without that
            // the test passed on either key and proved nothing about the one it names.
            var manifest = """
            {
              "FileVersion": 4,
              "Name": "Lantern",
              "Author": "Someone",
              "DefaultData": { "Files": {
                "chara/accessory/a0189/material/v0001/mt_lamp.mtrl": "v1\\mt_lamp.mtrl",
                "chara/accessory/a0189/material/v0001/stub_n.tex":   "shared\\small_n.tex",
                "chara/accessory/a0189/material/v0007/mt_lamp.mtrl": "v7\\mt_lamp.mtrl",
                "chara/accessory/a0189/texture/v07_norm.tex":        "v7\\big_n.tex",
                "chara/accessory/a0189/texture/v07_mask.tex":        "v7\\big_m.tex"
              } },
              "Groups": [
                {
                  "Name": "Piece", "Type": "Multi",
                  "Options": [
                    { "Name": "Lantern", "Files": { "chara/accessory/a0189/model/c0101a0189_wrs.mdl": "lamp\\model.mdl" } }
                  ]
                }
              ]
            }
            """;
            var pmp = WritePack(dir, manifest, new[]
            {
                ("v1/mt_lamp.mtrl", Mtrl("chara/accessory/a0189/material/v0001/stub_n.tex")),
                ("v7/mt_lamp.mtrl", Mtrl("chara/accessory/a0189/texture/v07_norm.tex",
                                         "chara/accessory/a0189/texture/v07_mask.tex")),
                ("shared/small_n.tex", new byte[500_000]),   // one big file…
                ("v7/big_n.tex", new byte[64_000]),          // …against two smaller ones
                ("v7/big_m.tex", new byte[64_000]),
                ("lamp/model.mdl", model),
            });

            var sidecar = ContentImportService.BuildSidecar(
                ContentImportService.Inspect(pmp), "Lantern", "Someone");
            var piece = Assert.Single(
                sidecar.ContentGroups!.SelectMany(g => g.Options).SelectMany(o => o.Pieces));

            Assert.Equal("chara/accessory/a0189/material/v0007/mt_lamp.mtrl",
                piece.GamePathsFor("mt_lamp.mtrl")[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Two variants naming the same NUMBER of textures are separated by how big those textures are.
    /// <para/>
    /// This is the case counting cannot see and the byte total exists for: a stub that references just as
    /// many files as the real material, but points them at placeholders.
    /// </summary>
    [Fact]
    public void Variants_naming_equally_many_textures_are_separated_by_their_size()
    {
        var dir = TempDir();
        try
        {
            var model = SyntheticModel.Build([],
                new SyntheticModel.Mesh("/mt_lamp.mtrl", new SyntheticModel.Sub(0)));

            // Both materials name TWO textures, so the count key ties as well. Only the size differs.
            var manifest = """
            {
              "FileVersion": 4,
              "Name": "Lantern",
              "Author": "Someone",
              "DefaultData": { "Files": {
                "chara/accessory/a0189/material/v0001/mt_lamp.mtrl": "v1\\mt_lamp.mtrl",
                "chara/accessory/a0189/texture/v01_norm.tex":        "v1\\small_n.tex",
                "chara/accessory/a0189/texture/v01_mask.tex":        "v1\\small_m.tex",
                "chara/accessory/a0189/material/v0007/mt_lamp.mtrl": "v7\\mt_lamp.mtrl",
                "chara/accessory/a0189/texture/v07_norm.tex":        "v7\\big_n.tex",
                "chara/accessory/a0189/texture/v07_mask.tex":        "v7\\big_m.tex"
              } },
              "Groups": [
                {
                  "Name": "Piece", "Type": "Multi",
                  "Options": [
                    { "Name": "Lantern", "Files": { "chara/accessory/a0189/model/c0101a0189_wrs.mdl": "lamp\\model.mdl" } }
                  ]
                }
              ]
            }
            """;
            var pmp = WritePack(dir, manifest, new[]
            {
                ("v1/mt_lamp.mtrl", Mtrl("chara/accessory/a0189/texture/v01_norm.tex",
                                         "chara/accessory/a0189/texture/v01_mask.tex")),
                ("v7/mt_lamp.mtrl", Mtrl("chara/accessory/a0189/texture/v07_norm.tex",
                                         "chara/accessory/a0189/texture/v07_mask.tex")),
                ("v1/small_n.tex", new byte[64]),
                ("v1/small_m.tex", new byte[64]),
                ("v7/big_n.tex", new byte[64_000]),
                ("v7/big_m.tex", new byte[64_000]),
                ("lamp/model.mdl", model),
            });

            var sidecar = ContentImportService.BuildSidecar(
                ContentImportService.Inspect(pmp), "Lantern", "Someone");
            var piece = Assert.Single(
                sidecar.ContentGroups!.SelectMany(g => g.Options).SelectMany(o => o.Pieces));

            Assert.Equal("chara/accessory/a0189/material/v0007/mt_lamp.mtrl",
                piece.GamePathsFor("mt_lamp.mtrl")[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A print or dye group — several options supplying ONE game path — is recorded with the option each
    /// file came from, so the composite can publish the one the user has selected.
    /// <para/>
    /// The Cerise kimono is the shape: four prints under a single material path. Ranking cannot separate
    /// them because they are not variants of one garment, they are the choice itself, and Penumbra's
    /// resolve answers the wrong question — with a second mod claiming that path, it lands outside the mod
    /// and the pack renders whichever print the import happened to bake.
    /// </summary>
    [Fact]
    public void A_material_chosen_by_an_option_group_records_which_option_supplies_each_file()
    {
        var dir = TempDir();
        try
        {
            var model = SyntheticModel.Build([],
                new SyntheticModel.Mesh("/mt_kimono.mtrl", new SyntheticModel.Sub(0)));

            var manifest = """
            {
              "FileVersion": 4,
              "Name": "Cerise",
              "Author": "Someone",
              "DefaultData": { "Files": {
                "chara/equipment/e6085/material/v0001/mt_kimono.mtrl": "print\\cranes.mtrl"
              } },
              "Groups": [
                {
                  "Name": "Piece", "Type": "Multi",
                  "Options": [
                    { "Name": "Jacket", "Files": { "chara/equipment/e6085/model/c0201e6085_met.mdl": "kimono\\model.mdl" } }
                  ]
                },
                {
                  "Name": "Print", "Type": "Single",
                  "Options": [
                    { "Name": "Cranes",      "Files": { "chara/equipment/e6085/material/v0001/mt_kimono.mtrl": "print\\cranes.mtrl" } },
                    { "Name": "Blue Rose",   "Files": { "chara/equipment/e6085/material/v0001/mt_kimono.mtrl": "print\\rose.mtrl" } },
                    { "Name": "Pink Floral", "Files": { "chara/equipment/e6085/material/v0001/mt_kimono.mtrl": "print\\floral.mtrl" } }
                  ]
                }
              ]
            }
            """;
            var pmp = WritePack(dir, manifest, new[]
            {
                ("print/cranes.mtrl", Mtrl("chara/equipment/e6085/texture/cranes_n.tex")),
                ("print/rose.mtrl",   Mtrl("chara/equipment/e6085/texture/rose_n.tex")),
                ("print/floral.mtrl", Mtrl("chara/equipment/e6085/texture/floral_n.tex")),
                ("kimono/model.mdl", model),
            });

            var sidecar = ContentImportService.BuildSidecar(
                ContentImportService.Inspect(pmp), "Cerise", "Someone");
            var piece = Assert.Single(
                sidecar.ContentGroups!.SelectMany(g => g.Options).SelectMany(o => o.Pieces));

            var sources = piece.SourcesFor("mt_kimono.mtrl");

            // Every print, each carrying the option that supplies it. The pack's own default data supplies
            // the same path too and is recorded with no group, so a pack whose group is entirely off still
            // has a file to fall back on.
            Assert.Equal(
                [("Print", "Cranes"), ("Print", "Blue Rose"), ("Print", "Pink Floral"), (null, null)],
                sources.Select(s => (s.Group, s.Option)).ToList());

            Assert.Equal(["print/cranes.mtrl", "print/rose.mtrl", "print/floral.mtrl", "print/cranes.mtrl"],
                sources.Select(s => s.File.Replace('\\', '/')).ToList());

            // Reachable with the model's leading slash, like MaterialFor and GamePathsFor.
            Assert.Equal(4, piece.SourcesFor("/mt_kimono.mtrl").Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Republishing a pack texture that has not changed does no disk work at all.
    /// <para/>
    /// The reason this needs a memo rather than WriteIfChanged: the compare reads BOTH files in full, so the
    /// steady state — nothing changed, every byte equal — is the expensive one. Cerise's four contested
    /// kimono textures are 290 MB, which made an idle composite move ~580 MB, on every gear change.
    /// <para/>
    /// The skip is deliberately blind to the destination's CONTENT, which this pins by corrupting it: only
    /// the source's stamp and the destination's existence are consulted. Proteus owns that folder, so the
    /// trade is a stat against a full read of the pack's own art.
    /// </summary>
    [Fact]
    public void An_unchanged_pack_texture_is_not_copied_again()
    {
        var dir = TempDir();
        try
        {
            var memo = new Dictionary<string, ulong>();
            var src = Path.Combine(dir, "print_d.tex");
            var dst = Path.Combine(dir, "ct_a_0.tex");
            File.WriteAllBytes(src, new byte[] { 1, 2, 3, 4 });

            // First time through: the copy happens.
            Assert.True(SecondSkinService.CopyPackFile(memo, src, dst));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(dst));

            // Unchanged source: skipped, and skipped WITHOUT reading either file — which is why the
            // corrupted destination survives. That is the trade the memo makes, stated out loud.
            File.WriteAllBytes(dst, new byte[] { 9, 9 });
            Assert.False(SecondSkinService.CopyPackFile(memo, src, dst));
            Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(dst));

            // A source edited in place — same length, new write time — is copied again.
            File.WriteAllBytes(src, new byte[] { 5, 6, 7, 8 });
            File.SetLastWriteTimeUtc(src, File.GetLastWriteTimeUtc(src).AddSeconds(5));
            Assert.True(SecondSkinService.CopyPackFile(memo, src, dst));
            Assert.Equal(new byte[] { 5, 6, 7, 8 }, File.ReadAllBytes(dst));

            // A destination that has gone away is rebuilt even though the memo still matches.
            File.Delete(dst);
            Assert.True(SecondSkinService.CopyPackFile(memo, src, dst));
            Assert.True(File.Exists(dst));

            // A DIFFERENT source into the same destination is never skipped, however the stamps line up.
            var other = Path.Combine(dir, "other_d.tex");
            File.WriteAllBytes(other, new byte[] { 7, 7, 7, 7 });
            File.SetLastWriteTimeUtc(other, File.GetLastWriteTimeUtc(src));
            Assert.True(SecondSkinService.CopyPackFile(memo, other, dst));
            Assert.Equal(new byte[] { 7, 7, 7, 7 }, File.ReadAllBytes(dst));

            // A source that is not there throws for the caller to report, and leaves no memo behind that
            // would make the next attempt skip the retry.
            var gone = Path.Combine(dir, "missing.tex");
            var before = new Dictionary<string, ulong>(memo);
            Assert.ThrowsAny<IOException>(() => SecondSkinService.CopyPackFile(memo, gone, dst));
            Assert.Equal(before, memo);   // nothing recorded, so the next attempt retries

            // The memo is SHARED with the generated writers, because all three write ss_{letter}_{slot}.tex
            // and the letter is a placement ordinal — the path a shell owns this composite belongs to a
            // content unit the next. A foreign entry for this destination must therefore force a re-copy
            // rather than read as "already ours": two memos over one path would let a shell regenerate
            // identical bytes, match its own stale entry, skip, and go on sampling what the content unit
            // had overwritten it with.
            Assert.False(SecondSkinService.CopyPackFile(memo, other, dst));   // settled on `other`

            // A generated writer takes the path: different bytes on disk, and ITS hash in the memo.
            File.WriteAllBytes(dst, new byte[] { 4, 4, 4, 4 });
            memo[dst] = 0xDEADBEEF;

            // The pack file must come back. With a memo of its own this returned false and left the
            // generated bytes in place — the stale-skip this sharing exists to prevent.
            Assert.True(SecondSkinService.CopyPackFile(memo, other, dst));
            Assert.Equal(new byte[] { 7, 7, 7, 7 }, File.ReadAllBytes(dst));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Only a piece that came out WRONG counts as a fault. Pieces arriving switched off is the design, and a
    /// body mesh left to the character's own skin is the wanted outcome — neither is a shortfall.
    /// <para/>
    /// This is what decides whether the import's result line is amber or green. Colouring it on the plain
    /// skipped count put amber on every outfit import, which taught the user to read amber as "fine" and
    /// then hid the one import that really had dropped a piece.
    /// </summary>
    [Fact]
    public void Only_a_piece_the_pack_failed_to_bind_counts_as_a_fault()
    {
        var dir = TempDir();
        try
        {
            // Three pieces: one clean, one whose meshes are all the wearer's own body, and one bound to a
            // material the pack does not ship.
            var good = SyntheticModel.Build([],
                new SyntheticModel.Mesh("/mt_ring.mtrl", new SyntheticModel.Sub(0)));
            var bodyOnly = SyntheticModel.Build([],
                new SyntheticModel.Mesh("/mt_c0201b0001_b.mtrl", new SyntheticModel.Sub(0)));
            var broken = SyntheticModel.Build([],
                new SyntheticModel.Mesh("/mt_missing.mtrl", new SyntheticModel.Sub(0)));

            string Manifest(string opts) => $$"""
            {
              "FileVersion": 4,
              "Name": "Mixed",
              "Author": "Someone",
              "DefaultData": { "Files": {
                "chara/accessory/a0031/material/v0001/mt_ring.mtrl": "ring.mtrl"
              } },
              "Groups": [
                { "Name": "Piece", "Type": "Multi", "Options": [ {{opts}} ] }
              ]
            }
            """;

            const string ringOpt = """
            { "Name": "Ring", "Files": { "chara/accessory/a0031/model/c0201a0031_rir.mdl": "ring.mdl" } }
            """;
            const string bodyOpt = """
            { "Name": "Body", "Files": { "chara/equipment/e6085/model/c0201e6085_top.mdl": "body.mdl" } }
            """;
            const string brokenOpt = """
            { "Name": "Broken", "Files": { "chara/equipment/e6086/model/c0201e6086_top.mdl": "broken.mdl" } }
            """;

            var files = new[]
            {
                ("ring.mtrl", Mtrl("chara/accessory/a0031/texture/ring_n.tex")),
                ("ring.mdl", good), ("body.mdl", bodyOnly), ("broken.mdl", broken),
            };

            // Clean plus a deliberate body drop: a skipped piece, but nothing went wrong.
            var withBody = ContentImportService.Inspect(
                WritePack(Path.Combine(dir, "a"), Manifest(ringOpt + "," + bodyOpt), files));
            Assert.Equal(1, withBody.ImportableUnits);
            Assert.Equal(2, withBody.TotalUnits);          // one was skipped…
            Assert.Equal(0, withBody.FaultyUnits);         // …and it is not a fault

            // An unbound material IS a fault — the pack has to be rebound and re-exported.
            var withBroken = ContentImportService.Inspect(
                WritePack(Path.Combine(dir, "b"), Manifest(ringOpt + "," + brokenOpt), files));
            Assert.Equal(1, withBroken.FaultyUnits);

            // A model that will not READ is a fault too, and this is the one inferring from an empty
            // Unbound list got wrong: a corrupt .mdl arrives with nothing bound AND nothing unbound, just
            // like the body-only case, so it imported as a clean success with the piece silently missing.
            const string corruptOpt = """
            { "Name": "Corrupt", "Files": { "chara/equipment/e6087/model/c0201e6087_top.mdl": "corrupt.mdl" } }
            """;
            var withCorrupt = ContentImportService.Inspect(
                WritePack(Path.Combine(dir, "d"), Manifest(ringOpt + "," + corruptOpt),
                    [.. files, ("corrupt.mdl", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]));
            Assert.Equal(1, withCorrupt.ImportableUnits);
            Assert.Equal(1, withCorrupt.FaultyUnits);

            // Nothing dropped at all.
            var clean = ContentImportService.Inspect(
                WritePack(Path.Combine(dir, "c"), Manifest(ringOpt), files));
            Assert.Equal(1, clean.ImportableUnits);
            Assert.Equal(1, clean.TotalUnits);
            Assert.Equal(0, clean.FaultyUnits);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A manifest cannot point the publish at a file outside the mod folder.
    /// <para/>
    /// A source's File is a manifest VALUE, and those are only slash-normalised on the way in — the
    /// traversal rejection in PenumbraPackage guards zip ENTRY names, which is a different list. So a rooted
    /// or climbing value reaches Path.Combine, which hands a rooted second argument straight back. Without
    /// the containment check that file is published as a material, and through the texture path it is
    /// COPIED into the mod Proteus publishes.
    /// </summary>
    [Fact]
    public void A_source_pointing_outside_the_mod_folder_is_refused()
    {
        var dir = TempDir();
        try
        {
            var modRoot = Path.Combine(dir, "mod");
            Directory.CreateDirectory(Path.Combine(modRoot, "print"));
            File.WriteAllBytes(Path.Combine(modRoot, "print", "ok.mtrl"), new byte[16]);

            // A real file outside the mod, standing in for anything the user would not want republished.
            var outside = Path.Combine(dir, "secret.key");
            File.WriteAllBytes(outside, new byte[16]);

            var on = new Dictionary<string, List<string>> { ["Print"] = ["Escape", "Climb", "Fine"] };

            // Rooted: Path.Combine returns the second argument verbatim, so nothing about modRoot survives.
            Assert.Null(SecondSkinService.SelectedMaterialFile(modRoot,
                [new() { Group = "Print", Option = "Escape", File = outside.Replace('\\', '/') }], on));

            // Climbing out with .. — the same escape by a different spelling.
            Assert.Null(SecondSkinService.SelectedMaterialFile(modRoot,
                [new() { Group = "Print", Option = "Climb", File = "../secret.key" }], on));

            // A refused source does not poison the ones after it: the honest file still publishes.
            Assert.Equal("ok.mtrl", Path.GetFileName(SecondSkinService.SelectedMaterialFile(modRoot,
                [
                    new() { Group = "Print", Option = "Escape", File = outside.Replace('\\', '/') },
                    new() { Group = "Print", Option = "Fine",   File = "print/ok.mtrl" },
                ], on)));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// An imported pack's multi-select groups arrive with nothing ticked, so the pack contributes only what
    /// the user asks for — and no two ticked options can start out fighting over one file path.
    /// <para/>
    /// Single groups keep their default: there is no "off" for one, so zeroing it would silently re-elect
    /// the first option and move a pack's chosen print.
    /// </summary>
    [Fact]
    public void Importing_leaves_every_multi_select_group_switched_off()
    {
        var dir = TempDir();
        try
        {
            var model = SyntheticModel.Build([],
                new SyntheticModel.Mesh("/mt_kimono.mtrl", new SyntheticModel.Sub(0)));

            var manifest = """
            {
              "FileVersion": 4,
              "Name": "Cerise",
              "Author": "Someone",
              "DefaultData": { "Files": {} },
              "Groups": [
                {
                  "Name": "Piece", "Type": "Multi", "DefaultSettings": 3,
                  "Options": [
                    { "Name": "Jacket", "Files": { "chara/equipment/e6085/model/c0201e6085_met.mdl": "kimono\\model.mdl" } }
                  ]
                },
                {
                  "Name": "Print", "Type": "Single", "DefaultSettings": 2,
                  "Options": [
                    { "Name": "Cranes",    "Files": { "chara/equipment/e6085/material/v0001/mt_kimono.mtrl": "print\\cranes.mtrl" } },
                    { "Name": "Blue Rose", "Files": { "chara/equipment/e6085/material/v0001/mt_kimono.mtrl": "print\\rose.mtrl" } }
                  ]
                }
              ]
            }
            """;
            var pmp = WritePack(dir, manifest, new[]
            {
                ("print/cranes.mtrl", Mtrl("chara/equipment/e6085/texture/cranes_n.tex")),
                ("print/rose.mtrl",   Mtrl("chara/equipment/e6085/texture/rose_n.tex")),
                ("kimono/model.mdl", model),
            });

            var root = Path.Combine(dir, "out");
            ContentImportService.WriteMod(root, "Cerise (Proteus)", "Someone",
                ContentImportService.Inspect(pmp));

            var written = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!.AsObject();
            var groups = written["Groups"]!.AsArray();

            var multi = groups.First(g => (string?)g!["Name"] == "Piece")!.AsObject();
            Assert.Equal(0, (int)multi["DefaultSettings"]!);

            // Untouched — Penumbra always has one option of a Single group selected.
            var single = groups.First(g => (string?)g!["Name"] == "Print")!.AsObject();
            Assert.Equal(2, (int)single["DefaultSettings"]!);

            // And the name the user chose is what the mod list will read.
            Assert.Equal("Cerise (Proteus)", (string?)written["Name"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A manifest carrying a shape the readers do not expect does not take the import down with it.
    /// <para/>
    /// A <c>Files</c> value that is a number rather than a string is enough: the strip asks it for a string.
    /// Before the guard that threw out of <c>WriteMod</c> with the archive already extracted — leaving a mod
    /// folder Penumbra loads happily, with none of the import's edits applied and no sidecar to mark it as
    /// one of ours. The malformed manifest is left alone; everything else the import writes still lands.
    /// </summary>
    [Fact]
    public void A_manifest_with_a_malformed_file_entry_does_not_abort_the_import()
    {
        var dir = TempDir();
        try
        {
            var model = SyntheticModel.Build([],
                new SyntheticModel.Mesh("/mt_kimono.mtrl", new SyntheticModel.Sub(0)));

            // The model redirect the import takes over sits beside a numeric value in the same Files map.
            var manifest = """
            {
              "FileVersion": 4,
              "Name": "Broken",
              "Author": "Someone",
              "DefaultData": { "Files": {} },
              "Groups": [
                {
                  "Name": "Piece", "Type": "Multi", "DefaultSettings": 1,
                  "Options": [
                    { "Name": "Jacket", "Files": {
                        "chara/equipment/e6085/model/c0201e6085_met.mdl": "kimono\\model.mdl",
                        "chara/equipment/e6085/material/v0001/mt_kimono.mtrl": 42 } }
                  ]
                }
              ]
            }
            """;
            var pmp = WritePack(dir, manifest, new[]
            {
                ("kimono/model.mdl", model),
            });

            var root = Path.Combine(dir, "out");
            var preview = ContentImportService.Inspect(pmp);

            // The import completes rather than throwing…
            ContentImportService.WriteMod(root, "Broken (Proteus)", "Someone", preview);

            // …and everything that does not depend on that one manifest still landed.
            Assert.True(File.Exists(Path.Combine(root, "Proteus", "metadata.json")));
            Assert.Equal("Broken (Proteus)",
                (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!["Name"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A print group whose options share ONE material and differ only in their textures is still recorded —
    /// per texture game path, with the option supplying each.
    /// <para/>
    /// This is the Cerise shape and the one the material map alone cannot see. All four prints name the same
    /// four texture paths, and Blue Rose and Pink Floral point at the identical .mtrl byte-for-byte: picking
    /// the right material tells them apart not at all. Only the file behind the diffuse does.
    /// </summary>
    [Fact]
    public void Prints_that_differ_only_in_their_textures_record_the_option_behind_each_texture()
    {
        var dir = TempDir();
        try
        {
            var model = SyntheticModel.Build([],
                new SyntheticModel.Mesh("/mt_kimono.mtrl", new SyntheticModel.Sub(0)));

            // Both prints redirect the SAME .mtrl file and the same diffuse PATH. Only the file behind that
            // path differs — which is exactly what has to survive into the sidecar.
            var manifest = """
            {
              "FileVersion": 4,
              "Name": "Cerise",
              "Author": "Someone",
              "DefaultData": { "Files": {
                "chara/equipment/e6085/material/v0001/mt_kimono.mtrl": "common\\shared.mtrl",
                "chara/equipment/e6085/texture/kimono_n.tex": "common\\shared_n.tex"
              } },
              "Groups": [
                {
                  "Name": "Piece", "Type": "Multi",
                  "Options": [
                    { "Name": "Jacket", "Files": { "chara/equipment/e6085/model/c0201e6085_met.mdl": "kimono\\model.mdl" } }
                  ]
                },
                {
                  "Name": "Print", "Type": "Single",
                  "Options": [
                    { "Name": "Blue Rose", "Files": {
                        "chara/equipment/e6085/material/v0001/mt_kimono.mtrl": "common\\shared.mtrl",
                        "chara/equipment/e6085/texture/kimono_d.tex": "print\\rose_d.tex" } },
                    { "Name": "Pink Floral", "Files": {
                        "chara/equipment/e6085/material/v0001/mt_kimono.mtrl": "common\\shared.mtrl",
                        "chara/equipment/e6085/texture/kimono_d.tex": "print\\floral_d.tex" } }
                  ]
                }
              ]
            }
            """;
            var pmp = WritePack(dir, manifest, new[]
            {
                ("common/shared.mtrl", Mtrl("chara/equipment/e6085/texture/kimono_d.tex",
                                            "chara/equipment/e6085/texture/kimono_n.tex")),
                ("common/shared_n.tex", new byte[64]),
                ("print/rose_d.tex",   new byte[64]),
                ("print/floral_d.tex", new byte[64]),
                ("kimono/model.mdl", model),
            });

            var sidecar = ContentImportService.BuildSidecar(
                ContentImportService.Inspect(pmp), "Cerise", "Someone");
            var piece = Assert.Single(
                sidecar.ContentGroups!.SelectMany(g => g.Options).SelectMany(o => o.Pieces));

            // The material map cannot separate the prints — one file serves all of them.
            Assert.Single(piece.SourcesFor("mt_kimono.mtrl")
                .Select(s => s.File).Distinct(StringComparer.OrdinalIgnoreCase));

            // The texture map can, and it is keyed by the path the MATERIAL names.
            var tex = piece.TextureSourcesFor("chara/equipment/e6085/texture/kimono_d.tex");
            Assert.Equal(
                [("Print", "Blue Rose"), ("Print", "Pink Floral")],
                tex.Select(s => (s.Group, s.Option)).ToList());
            Assert.Equal(["print/rose_d.tex", "print/floral_d.tex"],
                tex.Select(s => s.File.Replace('\\', '/')).ToList());

            // The normal map IS named by the material and IS shipped by the pack, but only one file ever
            // supplies it — no choice, nothing to take over, and republishing it would copy a 4K map on
            // every composite to change nothing.
            Assert.Empty(piece.TextureSourcesFor("chara/equipment/e6085/texture/kimono_n.tex"));

            // A texture the material does not name at all is likewise absent.
            Assert.Empty(piece.TextureSourcesFor("chara/equipment/e6085/texture/kimono_s.tex"));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The selection reaches the textures too: the same rule as the material, answered per texture path the
    /// material names. This is the call whose result decides which print is on the character.
    /// </summary>
    [Fact]
    public void The_selected_option_decides_which_texture_files_are_republished()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "print"));
            foreach (var f in new[] { "rose_d.tex", "floral_d.tex" })
                File.WriteAllBytes(Path.Combine(dir, "print", f), new byte[16]);

            const string diffuse = "chara/equipment/e6085/texture/kimono_d.tex";
            const string normal  = "chara/equipment/e6085/texture/kimono_n.tex";

            var piece = new ContentPiece
            {
                TextureOptions = new(StringComparer.OrdinalIgnoreCase)
                {
                    [diffuse] =
                    [
                        new() { Group = "Print", Option = "Blue Rose",   File = "print/rose_d.tex" },
                        new() { Group = "Print", Option = "Pink Floral", File = "print/floral_d.tex" },
                    ],
                },
            };

            // A material naming both textures. Only the diffuse is one the pack supplies.
            var mtrl = Mtrl(diffuse, normal);

            Dictionary<string, string> Pick(Dictionary<string, List<string>>? on)
                => SecondSkinService.SelectedTextureFiles(dir, piece, mtrl, on);

            var rose = Pick(new() { ["Print"] = ["Blue Rose"] });
            Assert.Equal("rose_d.tex", Path.GetFileName(Assert.Single(rose).Value));
            Assert.Equal(diffuse, rose.Keys.Single());

            Assert.Equal("floral_d.tex",
                Path.GetFileName(Assert.Single(Pick(new() { ["Print"] = ["Pink Floral"] })).Value));

            // The normal map is never claimed — the pack does not ship it, so it stays vanilla.
            Assert.DoesNotContain(normal, rose.Keys);

            // Nothing selected and no default data behind the path: Proteus republishes nothing and the
            // texture goes back to Penumbra, which is where every pack without a print group leaves it.
            Assert.Empty(Pick(null));
            Assert.Empty(Pick(new() { ["Piece"] = ["Jacket"] }));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The composite publishes the file belonging to the option the user has SELECTED, and falls back to
    /// the pack's default data when the group is off. Null when neither is on disk, so the caller's own
    /// fallbacks still run.
    /// </summary>
    [Fact]
    public void The_selected_option_decides_which_material_file_is_published()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "print"));
            foreach (var f in new[] { "cranes.mtrl", "rose.mtrl", "floral.mtrl" })
                File.WriteAllBytes(Path.Combine(dir, "print", f), new byte[16]);

            List<ContentMaterialSource> sources =
            [
                new() { Group = "Print", Option = "Cranes",      File = "print/cranes.mtrl" },
                new() { Group = "Print", Option = "Blue Rose",   File = "print/rose.mtrl" },
                new() { Group = "Print", Option = "Pink Floral", File = "print/floral.mtrl" },
                new() { File = "print/cranes.mtrl" },   // the pack's default data
            ];

            string Pick(Dictionary<string, List<string>>? on)
                => Path.GetFileName(SecondSkinService.SelectedMaterialFile(dir, sources, on) ?? "");

            // The selected print wins over the one declared first…
            Assert.Equal("rose.mtrl", Pick(new() { ["Print"] = ["Blue Rose"] }));
            Assert.Equal("floral.mtrl", Pick(new() { ["Print"] = ["Pink Floral"] }));

            // …and over one selected in some OTHER group, which says nothing about this material.
            Assert.Equal("cranes.mtrl", Pick(new() { ["Piece"] = ["Jacket"], ["Print"] = ["Cranes"] }));

            // Nothing selected in the group: the default-data file, which is what the pack ships as.
            Assert.Equal("cranes.mtrl", Pick(new() { ["Piece"] = ["Jacket"] }));
            Assert.Equal("cranes.mtrl", Pick(null));

            // A selection naming a file the mod does not have falls through rather than publishing a
            // path that isn't there.
            File.Delete(Path.Combine(dir, "print", "rose.mtrl"));
            Assert.Equal("cranes.mtrl", Pick(new() { ["Print"] = ["Blue Rose"] }));

            // And with no default data to fall back on, null — so ContentMaterialFile and the baked path
            // still get their turn.
            Assert.Null(SecondSkinService.SelectedMaterialFile(dir,
                [new() { Group = "Print", Option = "Blue Rose", File = "print/rose.mtrl" }],
                new Dictionary<string, List<string>> { ["Print"] = ["Blue Rose"] }));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A material that will not parse ranks last instead of throwing — the ordering is a hint, and
    /// the pack still publishes something.</summary>
    [Fact]
    public void An_unreadable_candidate_material_ranks_last_rather_than_throwing()
    {
        var dir = TempDir();
        try
        {
            var model = SyntheticModel.Build([],
                new SyntheticModel.Mesh("/mt_lamp.mtrl", new SyntheticModel.Sub(0)));

            // v0001 is declared first and is 8 bytes of nonsense; v0007 parses and names a texture.
            var manifest = """
            {
              "FileVersion": 4,
              "Name": "Lantern",
              "Author": "Someone",
              "DefaultData": { "Files": {
                "chara/accessory/a0189/material/v0001/mt_lamp.mtrl": "v1\\mt_lamp.mtrl",
                "chara/accessory/a0189/material/v0007/mt_lamp.mtrl": "v7\\mt_lamp.mtrl",
                "chara/accessory/a0189/texture/v07_norm.tex":        "v7\\big_n.tex"
              } },
              "Groups": [
                {
                  "Name": "Piece", "Type": "Multi",
                  "Options": [
                    { "Name": "Lantern", "Files": { "chara/accessory/a0189/model/c0101a0189_wrs.mdl": "lamp\\model.mdl" } }
                  ]
                }
              ]
            }
            """;
            var pmp = WritePack(dir, manifest, new[]
            {
                ("v1/mt_lamp.mtrl", new byte[8]),
                ("v7/mt_lamp.mtrl", Mtrl("chara/accessory/a0189/texture/v07_norm.tex")),
                ("v7/big_n.tex", new byte[64_000]),
                ("lamp/model.mdl", model),
            });

            var sidecar = ContentImportService.BuildSidecar(
                ContentImportService.Inspect(pmp), "Lantern", "Someone");
            var piece = Assert.Single(
                sidecar.ContentGroups!.SelectMany(g => g.Options).SelectMany(o => o.Pieces));

            Assert.Equal("chara/accessory/a0189/material/v0007/mt_lamp.mtrl",
                piece.GamePathsFor("mt_lamp.mtrl")[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The chosen name reaches the copied manifest, which is the list Penumbra actually shows.
    /// <para/>
    /// An import leaves the original pack installable on its own terms, so both sit in the mod list at once.
    /// Writing the name only into the sidecar and the folder left both rows reading the pack's own name.
    /// </summary>
    [Fact]
    public void The_chosen_mod_name_is_written_into_the_copied_manifest()
    {
        var dir = TempDir();
        try
        {
            var preview = ContentImportService.Inspect(MixedPack(dir));
            var root = Path.Combine(dir, "mod");
            ContentImportService.WriteMod(root, "Mixed (Proteus)", "Someone", preview);

            var manifest = (JsonObject)JsonNode.Parse(
                File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)))!;
            Assert.Equal("Mixed (Proteus)", (string?)manifest["Name"]);

            // And the sidecar agrees, so the two never drift apart.
            var sidecar = JsonSerializer.Deserialize<ProteusMetadata>(
                File.ReadAllText(Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, "metadata.json")),
                ProteusJson.MetadataWrite)!;
            Assert.Equal("Mixed (Proteus)", sidecar.Name);
        }
        finally { Directory.Delete(dir, true); }
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
        // And no ex bones either — nine of 967 packs surveyed declared an EST entry at all, so the common
        // case is that this field never appears and the composite writes no manipulation.
        Assert.Null(sidecar.ContentSkeletons);
    }

    /// <summary>
    /// A minimal <c>.mtrl</c> naming <paramref name="texturePaths"/>, laid out the way
    /// <see cref="TextureLoader.ParseMtrlBytes"/> walks one: header, texture offset table, string block,
    /// then a shader section whose samplers point back at those strings.
    /// <para/>
    /// Built rather than lifted from a real file because what is under test is the RANKING of candidate
    /// materials by the textures they name, and a fixture has to vary exactly that.
    /// </summary>
    private static byte[] Mtrl(params string[] texturePaths)
    {
        // Sampler ids, from TextureLoader: normal, mask, then index for a third.
        uint[] samplerIds = [0x0C5EC1F1u, 0x8A4E82B6u, 0x565F8FD8u];

        var strings = new MemoryStream();
        var offsets = new List<ushort>();
        foreach (var t in texturePaths)
        {
            offsets.Add((ushort)strings.Position);
            strings.Write(System.Text.Encoding.UTF8.GetBytes(t));
            strings.WriteByte(0);
        }
        var strBlock = strings.ToArray();

        var m = new MemoryStream();
        void U16(int v) { m.WriteByte((byte)v); m.WriteByte((byte)(v >> 8)); }
        void U32(uint v) { for (int i = 0; i < 4; i++) m.WriteByte((byte)(v >> (i * 8))); }

        U16(0); U16(0);                      // version
        U16(0);                              // 0x04
        U16(0);                              // 0x06 dataSetSize — no colour table
        U16(strBlock.Length);                // 0x08 stringTableSize
        U16(0);                              // 0x0A
        m.WriteByte((byte)texturePaths.Length);   // 0x0C textureCount
        m.WriteByte(0);                           // 0x0D uvSetCount
        m.WriteByte(0);                           // 0x0E colorSetCount
        m.WriteByte(0);                           // 0x0F additionalSize

        foreach (var off in offsets) { U16(off); U16(0); }   // texture table: offset + flags
        m.Write(strBlock);

        // Shader section: 12-byte header (counts at +2/+4/+6), no keys or constants, one sampler per texture.
        U16(0); U16(0); U16(0);                              // valueListSize, shaderKeyCount, constantCount
        U16(texturePaths.Length);                            // samplerCount
        U32(0);                                              // flags
        for (int i = 0; i < texturePaths.Length; i++)
        {
            U32(samplerIds[i % samplerIds.Length]);
            U32(0);
            m.WriteByte((byte)i);                            // texture index
            m.WriteByte(0); m.WriteByte(0); m.WriteByte(0);
        }
        return m.ToArray();
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
