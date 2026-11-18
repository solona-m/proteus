using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Row matcaps, the model writer and the harder end-to-end cases: vertex alpha on the cut mesh (raw bytes), the triangle
/// filter's validity, a 3-LOD game model, and Prepare/Apply/Undo cases the main file doesn't cover (Sensitive writers:
/// these stay in the quick suite, except the game-data one).
/// </summary>
public class MatcapRowWriterTests
{
    /// <summary>
    /// The row fixture's four triangles as a <see cref="SyntheticModel.BuildSurface"/> model (normals, UVs) whose blend
    /// weights are re-declared as vertex colour (usage index 0, UByte4: the n_root weight bytes 255,0,0,0 → alpha 0), and
    /// optionally a second colour element (usage index 1) over the blend indices.
    /// </summary>
    internal static byte[] ColourModel(bool secondColour = false, byte colourType = 5)
    {
        var c = RowFixture.Corners();
        var pos = c.Select(p => new Vector3(p.Item1, p.Item2, p.Item3)).ToArray();
        var uv = c.Select(p => new Vector2((p.Item1 + 0.1f) / 0.2f, (p.Item2 - 0.9f) / 0.5f)).ToArray();
        var nrm = Enumerable.Repeat(new Vector3(0, 0, 1), c.Length).ToArray();
        var mdl = SyntheticModel.BuildSurface([], new SyntheticModel.SurfaceMesh(RowFixture.Mat, pos, nrm, uv,
            new (string, float)[]?[c.Length], [(0u, Enumerable.Range(0, c.Length).ToArray())]));
        int decl = 0x44;
        for (int e = 0; e < 17; e++)
        {
            int at = decl + e * 8;
            if (mdl[at] == 0xFF)
            {
                if (secondColour)
                {
                    // Append usage index 1 over the blend indices (stream 0, offset 28), then the terminator after it.
                    Array.Copy(mdl, at, mdl, at + 8, 8);
                    mdl[at] = 0; mdl[at + 1] = 28; mdl[at + 2] = 5; mdl[at + 3] = SecondSkinWriter.UseColor; mdl[at + 4] = 1;
                }
                break;
            }
            if (mdl[at + 3] == SecondSkinWriter.UseBlendWeight) { mdl[at + 3] = SecondSkinWriter.UseColor; mdl[at + 4] = 0; mdl[at + 2] = colourType; }
        }
        return mdl;
    }

    private static byte AlphaOf(byte[] mdl, int mesh, int vertex, byte usageIndex = 0)
    {
        var src = SecondSkinWriter.Parse(mdl);
        var el = src.Decls[mesh].First(x => x.Usage == SecondSkinWriter.UseColor && x.UsageIndex == usageIndex);
        int mo = src.MeshStart + mesh * 36;
        int vbo = (int)BitConverter.ToUInt32(mdl, mo + 20 + el.Stream * 4);
        return mdl[src.Vb + vbo + vertex * mdl[mo + 32 + el.Stream] + el.Offset + 3];
    }

    private static bool Left(ModelMaterialWriter.TriangleKey k) => k.Ordinal < 2;   // the fixture's two left triangles

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-32")]
    [Trait("Plan", "EDGE-matcap-row-43")]
    public void Cut_mesh_alpha_set_to_one_only_on_usage_0_and_only_on_the_cut_mesh()
    {
        var mdl = ColourModel(secondColour: true);
        var parts = ModelPartReader.Read(mdl)!.Parts.Where(p => p.Island < 0).ToList();
        Assert.Equal(0f, ModelMaterialWriter.VertexAlphaMin(mdl, parts, Left));

        var r = ModelMaterialWriter.MoveToMaterial(mdl, parts, "/mt_cut.mtrl", 0f, Left, cutAlphaOne: true);
        var (source, drawn) = Assert.Single(r.Meshes);
        Assert.NotEqual(source, drawn);                                       // a split: the cut mesh is a copy
        int vc = BitConverter.ToUInt16(r.Model, SecondSkinWriter.Parse(r.Model).MeshStart + drawn * 36);
        for (int v = 0; v < vc; v++)
        {
            Assert.Equal(255, AlphaOf(r.Model, drawn, v));                    // the cut mesh: alpha 1
            Assert.Equal(0, AlphaOf(r.Model, drawn, v, usageIndex: 1));       // its second colour: untouched
            Assert.Equal(0, AlphaOf(r.Model, source, v));                     // the author's mesh: untouched
        }
        Assert.NotNull(ModelPartReader.Read(r.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-32")]
    public void Unwritable_colour_type_refuses_vertex_alpha()
    {
        var mdl = ColourModel(colourType: 2);   // Float3: no alpha to write
        var parts = ModelPartReader.Read(mdl)!.Parts.Where(p => p.Island < 0).ToList();
        var ex = Assert.Throws<ModelMaterialWriter.Refused>(() =>
            ModelMaterialWriter.MoveToMaterial(mdl, parts, "/mt_cut.mtrl", 0f, Left, cutAlphaOne: true));
        Assert.Equal(ModelMaterialWriter.CodeVertexAlpha, ex.Code);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-row-11")]
    public void KeepTriangle_copy_then_move_keeps_the_model_valid()
    {
        var mdl = ColourModel();
        var parts = ModelPartReader.Read(mdl)!.Parts.Where(p => p.Island < 0).ToList();
        var copy = ModelMaterialWriter.MoveToMaterial(mdl, parts, "/mt_m.mtrl", -0.0006f, Left);
        var after = ModelPartReader.Read(copy.Model)!.Parts;
        var mine = parts.Select(p => after.First(c => PartClaims.LineUp(p, c))).ToList();
        var moved = ModelMaterialWriter.MoveToMaterial(copy.Model, mine, "/mt_cut.mtrl", 0f, Left);

        var read = ModelPartReader.Read(moved.Model)!;
        var byMat = read.Parts.Where(p => p.Island < 0).GroupBy(p => p.Material).ToDictionary(g => g.Key, g => g.Sum(p => p.TriangleCount));
        Assert.Equal(2, byMat["/mt_m.mtrl"]);
        Assert.Equal(2, byMat["/mt_cut.mtrl"]);
        Assert.Equal(2, byMat[RowFixture.Mat]);
        // No degenerate (zero-area) or out-of-range triangle anywhere.
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(moved.Model, out var pos, out _, out var tris, out _, out _, skinOnly: false));
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            Assert.All(new[] { tris[t], tris[t + 1], tris[t + 2] }, i => Assert.InRange(i, 0, pos.Length / 3 - 1));
            if (tris[t] == tris[t + 1] && tris[t + 1] == tris[t + 2]) continue;   // collapsed on purpose: draws nothing
            var a = new Vector3(pos[tris[t] * 3], pos[tris[t] * 3 + 1], pos[tris[t] * 3 + 2]);
            var b = new Vector3(pos[tris[t + 1] * 3], pos[tris[t + 1] * 3 + 1], pos[tris[t + 1] * 3 + 2]);
            var c = new Vector3(pos[tris[t + 2] * 3], pos[tris[t + 2] * 3 + 1], pos[tris[t + 2] * 3 + 2]);
            Assert.True(Vector3.Cross(b - a, c - a).Length() > 1e-9f, "a degenerate triangle");
        }
    }

    [LocalDataFact]
    [Trait("Plan", "EDGE-matcap-row-41")]
    public void Three_lod_model_copies_and_moves_exactly_the_row_triangles()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var mdl = data.GetFile("chara/equipment/e6200/model/c0201e6200_top.mdl")!.Data;
        Assert.Equal(3, ModelLodTrimmer.LodCount(mdl));
        var part = ModelPartReader.Read(mdl)!.Parts.First(p => p.Island < 0 && p.TriangleCount > 4);
        bool Keep(ModelMaterialWriter.TriangleKey k) => k.Ordinal % 2 == 0;
        int kept = part.Ordinals.Count(o => o % 2 == 0);

        var copy = ModelMaterialWriter.MoveToMaterial(mdl, [part], "/mt_m.mtrl", -0.0006f, Keep);
        Assert.True(copy.Lod0Cut);
        var mine = ModelPartReader.Read(copy.Model)!.Parts.First(c => PartClaims.LineUp(part, c));
        var moved = ModelMaterialWriter.MoveToMaterial(copy.Model, [mine], "/mt_cut.mtrl", 0f, Keep);
        var parts = ModelPartReader.Read(moved.Model)!.Parts.Where(p => p.Island < 0).ToList();
        Assert.Equal(kept, parts.Where(p => p.Material == "/mt_m.mtrl").Sum(p => p.TriangleCount));
        Assert.Equal(kept, parts.Where(p => p.Material == "/mt_cut.mtrl").Sum(p => p.TriangleCount));
    }

    // ── end to end ────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-47")]
    public void Mixed_thresholds_with_low_vertex_alpha_refuse_and_all_threshold0_sets_alpha()
    {
        using (var all0 = new RowFixture(model: ColourModel()))
        {
            var p = all0.Prepare();
            Assert.True(p.Ok, p.Message);
            Assert.True(p.CutAlphaSet);
        }
        using var mixed = new RowFixture(materialInOption: true, model: ColourModel());
        mixed.ColourOptions();
        File.WriteAllBytes(MatcapWriter.Full(mixed.ModRoot, "mat/blue.mtrl"), RowFixture.Material(threshold: 0.3f, tint: 2));
        var refused = mixed.Prepare();
        Assert.Equal(MatcapStudio.CodeRowMixedThreshold, refused.Code);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-27")]
    public void Identical_container_files_share_one_cut_file_and_are_not_stale()
    {
        using var f = new RowFixture(materialInOption: true);
        f.ColourOptions();
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "mat/blue.mtrl"), File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, "mat/red.mtrl")));
        Assert.True(f.Apply().Ok);
        var entry = MatcapRecord.Read(f.ModRoot).Materials.Single();
        var file = Assert.Single(entry.Row!.Files);
        var redirects = PenumbraModMeta.ReadContainerRedirects(f.ModRoot).Where(r => PenumbraModMeta.SameFile(r.File, file.File)).ToList();
        Assert.Contains(redirects, r => r.Container.Option == "Red");
        Assert.Contains(redirects, r => r.Container.Option == "Blue");
        Assert.Equal(MatcapWriter.State.Ok, MatcapWriter.Detect(f.ModRoot, p => f.Game.GetValueOrDefault(p))[entry.Name]);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-6")]
    public void Unrelated_group_swapping_only_id_tex_refuses_and_a_rival_single_option_does_not()
    {
        using var f = new RowFixture();
        // An unrelated Multi group that swaps only the index map: one cut can't fit both choices.
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(f.ModRoot, "meta.json")))!.AsObject();
        File.WriteAllBytes(Path.Combine(f.ModRoot, "tex", "id2.tex"), f.Tex(RowFixture.IndexRgba(leftRow: 3)));
        meta["Groups"] = new JsonArray(new JsonObject
        {
            ["Name"] = "Pattern", ["Type"] = "Multi", ["DefaultSettings"] = 0,
            ["Options"] = new JsonArray(new JsonObject { ["Name"] = "Alt", ["Files"] = new JsonObject { [RowFixture.IndexPath] = "tex\\id2.tex" } }),
        });
        File.WriteAllText(Path.Combine(f.ModRoot, "meta.json"), meta.ToJsonString());
        Assert.Equal(MatcapRowCut.CodeTextureVaries, f.Prepare().Code);

        // Bakes are keyed by content: the same bytes at another path are no variation.
        File.WriteAllBytes(Path.Combine(f.ModRoot, "tex", "id2.tex"), File.ReadAllBytes(Path.Combine(f.ModRoot, "tex", "id.tex")));
        Assert.True(f.Prepare().Ok);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-5")]
    public void No_index_map_cuts_by_normal_alpha_row()
    {
        using var f = new RowFixture();
        // The material without an index map; the normal's alpha picks the row (left half row 15).
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "mat/top_a.mtrl"),
            SyntheticMatcapMod.Material(MatcapRowCut.CutShader, ["chara/x/base.tex", RowFixture.NormalPath],
                [(0x115306BE, 0), (MatcapMaterial.SamplerNormal, 1)], colorTableBytes: 2048));
        var n = RowFixture.NormalRgba();
        for (int y = 0; y < RowFixture.Size; y++)
            for (int x = 0; x < RowFixture.Size; x++)
                n[(y * RowFixture.Size + x) * 4 + 3] = (byte)(x < RowFixture.Size / 2 ? 14 * 17 : 0);
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "tex/n.tex"), f.Tex(n));
        var p = f.Prepare(f.Env(sub: SubRowPick.Both));
        Assert.True(p.Ok, p.Message);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-4")]
    public void Unreadable_index_map_refuses_row_bake()
    {
        using var f = new RowFixture();
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "tex/id.tex"), [1, 2, 3, 4]);
        Assert.Equal(MatcapRowCut.CodeBake, f.Prepare().Code);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-15")]
    public void Model_changed_during_prepare_writes_nothing()
    {
        using var f = new RowFixture();
        var p = f.Prepare();
        Assert.True(p.Ok);
        var before = f.Snapshot();
        File.AppendAllText(MatcapWriter.Full(f.ModRoot, RowFixture.ModelRel), "x");
        before[Path.Combine("model", "top.mdl")] = HairPortPlan.Sha8(File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, RowFixture.ModelRel)));
        Assert.Equal(MatcapWriter.CodeEdited, MatcapWriter.Apply(f.ModRoot, p.Request!).Code);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-28")]
    public void Imported_piece_and_legacy_mod_refused_in_row_mode()
    {
        using var f = new RowFixture();
        File.WriteAllText(Path.Combine(f.ModRoot, "default_mod.json"), "{\"Files\":{}}");
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(f.ModRoot, "meta.json")))!.AsObject();
        meta["FileVersion"] = 3;
        File.WriteAllText(Path.Combine(f.ModRoot, "meta.json"), meta.ToJsonString());
        Assert.Equal(MatcapWriter.CodeLegacy, f.Prepare().Code);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-17")]
    public void Pending_row_entry_is_cleaned_by_undo()
    {
        using var f = new RowFixture(materialInOption: true);
        f.ColourOptions();
        var before = f.Snapshot();
        var p = f.Prepare();
        Assert.True(p.Ok);
        // A crash after the redirects (the model not yet written): the entry stays pending.
        MatcapWriter.AfterStep = s => { if (s == "redirects") throw new InvalidOperationException("crash"); };
        try { MatcapWriter.Apply(f.ModRoot, p.Request!); }
        finally { MatcapWriter.AfterStep = null; }
        Assert.DoesNotContain(PenumbraModMeta.ReadContainerRedirects(f.ModRoot), r => r.GamePath.Contains("_mcut"));
        Assert.Equal(before.Keys.Order(), f.Snapshot().Keys.Order());
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-19")]
    public void Undo_finds_cut_redirects_in_a_renamed_option()
    {
        using var f = new RowFixture(materialInOption: true);
        f.ColourOptions();
        Assert.True(f.Apply().Ok);
        var path = Path.Combine(f.ModRoot, "meta.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Blue\"", "\"Navy\""));
        Assert.True(MatcapWriter.Undo(f.ModRoot, RowFixture.ModelRel).Ok);
        Assert.DoesNotContain(PenumbraModMeta.ReadContainerRedirects(f.ModRoot), r => r.GamePath.Contains("_mcut"));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-34")]
    public void Undo_removes_copies_of_our_cut_redirects_in_other_folders_and_never_an_author_key()
    {
        using var f = new RowFixture(materialInOption: true);
        f.ColourOptions();
        Assert.True(f.Apply().Ok);
        var row = MatcapRecord.Read(f.ModRoot).Materials.Single().Row!;
        var redCut = PenumbraModMeta.ReadContainerRedirects(f.ModRoot).First(r => r.Container.Option == "Red" && r.GamePath.Contains("_mcut"));
        // Gear variants copies the Red option's redirects (the author's and ours) to folder 2.
        var cut2 = redCut.GamePath.Replace("/v0001/", "/v0002/");
        PenumbraModMeta.EditContainers(f.ModRoot, [new PenumbraModMeta.ContainerEdit(new PenumbraModMeta.ContainerRef("Colour", "Red"),
            new Dictionary<string, string> { [cut2] = redCut.File, [RowFixture.MatPath(2)] = "mat\\red.mtrl" }, new Dictionary<string, string>())]);
        Assert.Equal(MatcapWriter.State.Ok, MatcapWriter.Detect(f.ModRoot)[MatcapRecord.Read(f.ModRoot).Materials.Single().Name]);

        Assert.True(MatcapWriter.Undo(f.ModRoot, RowFixture.ModelRel).Ok);
        var after = PenumbraModMeta.ReadContainerRedirects(f.ModRoot);
        Assert.DoesNotContain(after, r => row.Files.Any(x => PenumbraModMeta.SameFile(x.File, r.File)));
        Assert.Contains(after, r => r.Container.Option == "Red" && PenumbraModMeta.SameFile(r.GamePath, RowFixture.MatPath(2)));   // the author's copy stays
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-18")]
    public void Hand_edited_cut_file_is_kept_named_and_survives_a_later_sweep()
    {
        using var f = new RowFixture();
        Assert.True(f.Apply().Ok);
        var file = MatcapRecord.Read(f.ModRoot).Materials.Single().Row!.Files.Single().File;
        File.AppendAllText(MatcapWriter.Full(f.ModRoot, file), "edited");
        var undo = MatcapWriter.Undo(f.ModRoot, RowFixture.ModelRel);
        Assert.True(undo.Ok, undo.Message);
        Assert.Contains(Path.GetFileName(file), undo.Message);
        Assert.True(File.Exists(MatcapWriter.Full(f.ModRoot, file)));
        // Another apply and undo later: the sweep never deletes files.
        Assert.True(f.Apply().Ok);
        Assert.True(MatcapWriter.Undo(f.ModRoot, RowFixture.ModelRel).Ok);
        Assert.True(File.Exists(MatcapWriter.Full(f.ModRoot, file)));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-14")]
    public void Folder_nothing_serves_gets_no_cut_redirect()
    {
        using var f = new RowFixture();
        var target = MatcapPlan.TargetOf(RowFixture.ModelPath, out _)!;
        var (plan, refused) = MatcapRowCut.Make(new MatcapRowCut.Inputs(f.ModRoot, target, RowFixture.Mat, [1, 3], 1, f.Env(), 15,
            SubRowPick.B, "abc123", "/mt_c0201e6255_top_a_mcutabc1231.mtrl"));
        Assert.Null(refused);
        // Folder 3: no container, no game file, and the worn material is in default data → the worn cut in default data.
        Assert.Contains(plan!.Folders, x => x.Folder == 3 && x.Case == MatcapRecord.RowFingerprint.CaseWorn);
        // With the worn material from the game too (nothing in the mod), there is nothing to follow: none.
        using var g = new RowFixture(materialInOption: true);
        g.Game[RowFixture.MatPath(1)] = RowFixture.Material();
        var (plan2, refused2) = MatcapRowCut.Make(new MatcapRowCut.Inputs(g.ModRoot, target, RowFixture.Mat, [1, 3], 1, g.Env(), 15,
            SubRowPick.B, "abc123", "/mt_c0201e6255_top_a_mcutabc1231.mtrl"));
        Assert.Null(refused2);
        Assert.Contains(plan2!.Folders, x => x.Folder == 3 && x.Case == MatcapRecord.RowFingerprint.CaseNone);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-23")]
    public void Library_choice_in_row_mode_plans_the_cut()
    {
        using var f = new RowFixture();
        var lib = MatcapLibrary.Detect(SyntheticMatcapMod.Library(f.Mods, "Lib", "Test Library", "https://example.test",
            ["chara/common/texture/testlib_a.tex"], ["Gem Red", "Pearl"]), "Lib", "Lib").Single();
        var p = f.Prepare(choice: MatcapChoice.OfLibrary(lib, "Gem Red"));
        Assert.True(p.Ok, p.Message);
        Assert.NotNull(p.Request!.Materials.Single().Row);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-45")]
    public void File_swap_inside_an_option_refuses()
    {
        using var f = new RowFixture(materialInOption: true);
        f.ColourOptions();
        var path = Path.Combine(f.ModRoot, "meta.json");
        var meta = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        meta["Groups"]![0]!["Options"]![1]!.AsObject()["FileSwaps"] = new JsonObject { [RowFixture.MatPath()] = "chara/x/other.mtrl" };
        File.WriteAllText(path, meta.ToJsonString());
        Assert.Contains(PenumbraModMeta.TryReadContainerFileSwaps(f.ModRoot)!, s => s.Container.Option == "Blue");
        Assert.Equal(MatcapRowCut.CodeFileSwap, f.Prepare().Code);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-20")]
    public void Refit_naming_the_cut_keeps_its_files()
    {
        using var f = new RowFixture();
        Assert.True(f.Apply().Ok);
        var row = MatcapRecord.Read(f.ModRoot).Materials.Single().Row!;
        // A refit remade from the row-matcapped model draws the cut too.
        var refit = Path.Combine(f.ModRoot, BodyRetargetWriter.Subfolder, "size", "top.mdl");
        Directory.CreateDirectory(Path.GetDirectoryName(refit)!);
        File.WriteAllBytes(refit, f.Current());
        var undo = MatcapWriter.Undo(f.ModRoot, RowFixture.ModelRel);
        Assert.True(undo.Ok, undo.Message);
        Assert.True(File.Exists(MatcapWriter.Full(f.ModRoot, row.Files.Single().File)));
        Assert.Contains(PenumbraModMeta.ReadContainerRedirects(f.ModRoot), r => PenumbraModMeta.SameFile(r.File, row.Files.Single().File));
        Assert.NotNull(MatcapRecord.Read(f.ModRoot).Materials.SingleOrDefault()?.Row);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-row-12")]
    public void Follow_warning_when_the_material_file_has_colours_in_proteus()
    {
        using var f = new RowFixture();
        Assert.True(MatcapRowCut.FollowWarn(f.ModRoot, RowFixture.Mat, file => PenumbraModMeta.SameFile(file, "mat/top_a.mtrl")));
        Assert.False(MatcapRowCut.FollowWarn(f.ModRoot, RowFixture.Mat, _ => false));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-row-7")]
    public void Siblings_lining_up_share_the_cut_name_and_others_are_skipped()
    {
        using var f = new RowFixture();
        // A sibling at the same game path in an option (same model bytes: lines up), and one that doesn't line up.
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(f.ModRoot, "meta.json")))!.AsObject();
        Directory.CreateDirectory(Path.Combine(f.ModRoot, "alt"));
        File.WriteAllBytes(Path.Combine(f.ModRoot, "alt", "top.mdl"), f.Model);
        File.WriteAllBytes(Path.Combine(f.ModRoot, "alt", "odd.mdl"), SyntheticModel.Build([],
            [new SyntheticModel.Mesh("/mt_other.mtrl", new SyntheticModel.Sub(0))], SyntheticModel.V6, (IReadOnlyList<string>?)null));
        meta["Groups"] = new JsonArray(new JsonObject
        {
            ["Name"] = "Fit", ["Type"] = "Single", ["DefaultSettings"] = 0,
            ["Options"] = new JsonArray(
                new JsonObject { ["Name"] = "Alt", ["Files"] = new JsonObject { [RowFixture.ModelPath] = "alt\\top.mdl" } },
                new JsonObject { ["Name"] = "Odd", ["Files"] = new JsonObject { [RowFixture.ModelPath] = "alt\\odd.mdl" } }),
        });
        File.WriteAllText(Path.Combine(f.ModRoot, "meta.json"), meta.ToJsonString());

        var p = f.Prepare();
        Assert.True(p.Ok, p.Message);
        Assert.Contains(p.Request!.Models, m => PenumbraModMeta.SameFile(m.Rel, "alt/top.mdl"));
        Assert.Contains(p.SiblingsSkipped, s => PenumbraModMeta.SameFile(s, "alt/odd.mdl"));
        var cut = p.Request.Materials.Single().Row!.CutName;
        foreach (var m in p.Request.Models)
            Assert.Contains(cut, SecondSkinWriter.MaterialNames(m.NewBytes), StringComparer.OrdinalIgnoreCase);
    }
}
