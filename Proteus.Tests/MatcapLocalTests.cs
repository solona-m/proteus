using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The split on real models — the game's gear, accessories, horns and an installed hair mod — measured on the writer's
/// output: every triangle where it was, the moved ones on the new material, and the passes that read a model after it
/// (refit, hat fit, the geometry reader) still finding finite, closed geometry. Owner's machine only.
/// </summary>
public class ModelMaterialWriterLocalTests(ITestOutputHelper output)
{
    internal const string Mat = "/mt_c0201e6255_top_a_mcapabcdef1.mtrl";

    internal static Lumina.GameData Game() => new(LocalData.Path(LocalData.GameData));

    internal static ModelPart FirstCloth(byte[] mdl) =>
        ModelPartReader.Read(mdl)!.Parts.First(p => p.Island < 0 && !MatcapPlan.IsSkin(p));

    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-matcaps-4")]
    public void Real_gear_hair_accessory_invariants()
    {
        var data = Game();
        foreach (var path in new[]
                 {
                     "chara/equipment/e6200/model/c0201e6200_top.mdl",          // three shapes, three LODs
                     "chara/equipment/e0010/model/c0101e0010_top.mdl",
                     "chara/accessory/a0053/model/c0201a0053_rir.mdl",
                     "chara/human/c0201/obj/hair/h0130/model/c0201h0130_hir.mdl",
                 })
        {
            var before = data.GetFile(path)?.Data;
            if (before == null) { output.WriteLine($"{path}: not in this install"); continue; }
            var part = FirstCloth(before);
            var result = ModelMaterialWriter.MoveToMaterial(before, [part], Mat);
            var cut = result.Lod0Cut ? ModelLodTrimmer.KeepLod0(before, out _)! : before;
            output.WriteLine($"{path}: {before.Length:N0} -> {result.Model.Length:N0} bytes, {result.MeshesAdded} mesh(es) added");

            var was = ModelMaterialWriterTests.Drawn(cut);
            var now = ModelMaterialWriterTests.Drawn(result.Model);
            Assert.Equal(Geo(was), Geo(now));
            Assert.Equal(part.TriangleCount, now.Count(d => d.StartsWith(Mat + "|", StringComparison.Ordinal)));
            // Every shape key still draws the same geometry.
            foreach (var shape in SecondSkinWriter.Parse(cut).Shapes.Keys)
                Assert.Equal(Geo(ModelMaterialWriterTests.Drawn(cut, shape)), Geo(ModelMaterialWriterTests.Drawn(result.Model, shape)));
        }
    }

    private static List<string> Geo(IEnumerable<string> drawn) =>
        drawn.Select(d => d[(d.IndexOf('|') + 1)..]).OrderBy(x => x, StringComparer.Ordinal).ToList();
}

/// <summary>
/// Gate G5: the game's own Au Ra faces take a matcap on their horns — the atr_hrn parts — through the LOD cut and the
/// split, with every non-horn triangle where it was and room for one material per horn material.
/// </summary>
public class MatcapFaceHornLocalTests(ITestOutputHelper output)
{
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-matcaps-11")]
    [Trait("Plan", "EDGE-matcaps-57")]
    public void Real_c1401_and_c1301_f0001_horns_take_matcap()
    {
        var data = ModelMaterialWriterLocalTests.Game();
        foreach (var race in new[] { 1301, 1401 })
        {
            var path = $"chara/human/c{race:D4}/obj/face/f0001/model/c{race:D4}f0001_fac.mdl";
            var bytes = data.GetFile(path)!.Data;
            var read = ModelPartReader.Read(bytes)!;
            var horns = read.Parts.Where(p => p.Island < 0 && MatcapPlan.Tickable(p, MatcapPlan.Kind.Face, read.AttributeNames)).ToList();
            Assert.NotEmpty(horns);
            Assert.DoesNotContain(horns, p => p.Material.Contains("_fac_a", StringComparison.Ordinal));   // never the skin
            var sources = horns.Select(p => p.Material).Distinct().ToList();
            output.WriteLine($"{path}: LODs {ModelLodTrimmer.LodCount(bytes)}, horns {string.Join(", ", horns.Select(h => $"{h.Label} {h.Material}"))}, room {ModelMaterialWriter.MaterialRoom(bytes)}");
            Assert.True(ModelMaterialWriter.MaterialRoom(bytes) >= sources.Count, "room for one matcap material per horn material");

            // One material per horn material, split in turn as Prepare does.
            var model = ModelLodTrimmer.LodCount(bytes) > 1 ? ModelLodTrimmer.KeepLod0(bytes, out var why) ?? throw new Exception(why) : bytes;
            var cut = model;
            foreach (var s in sources)
            {
                var now = ModelPartReader.Read(model)!;
                var mine = horns.Where(h => h.Material == s).Select(h => now.Parts.First(p => PartClaims.LineUp(h, p))).ToList();
                model = ModelMaterialWriter.MoveToMaterial(model, mine, MatcapPlan.NewName(s, "abcdef", SecondSkinWriter.MaterialNames(model))).Model;
            }
            var was = ModelMaterialWriterTests.Drawn(cut);
            var after = ModelMaterialWriterTests.Drawn(model);
            Assert.Equal(was.Select(d => d[(d.IndexOf('|') + 1)..]).Order(), after.Select(d => d[(d.IndexOf('|') + 1)..]).Order());
            var afterRead = ModelPartReader.Read(model)!;
            Assert.All(afterRead.Parts.Where(p => MatcapPlan.IsOnMatcap(p)), p => Assert.True(MatcapPlan.IsHorn(p, afterRead.AttributeNames)));
            // The material is published beside the face's own, flat.
            var target = MatcapPlan.TargetOf(path, out _)!;
            var paths = MatcapPlan.MaterialGamePaths(target, "/mt_x_mcapabcdef1.mtrl", [path], null);
            Assert.All(paths, p => Assert.StartsWith($"chara/human/c{race:D4}/obj/face/f0001/material/", p));
        }
    }
}

public class MatcapVieraEarLocalTests(ITestOutputHelper output)
{
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-matcaps-11")]
    public void Real_viera_ear_model_takes_matcap()
    {
        var data = ModelMaterialWriterLocalTests.Game();
        int tested = 0;
        foreach (var race in new[] { 1701, 1801 })
            for (int id = 1; id <= 4; id++)
            {
                var path = $"chara/human/c{race:D4}/obj/zear/z{id:D4}/model/c{race:D4}z{id:D4}_zer.mdl";
                var bytes = data.GetFile(path)?.Data;
                if (bytes == null) continue;
                var parts = ModelPartReader.Read(bytes)!.Parts.Where(p => p.Island < 0 && MatcapPlan.Tickable(p)).ToList();
                if (parts.Count == 0) continue;
                var target = MatcapPlan.TargetOf(path, out _)!;
                Assert.Equal(MatcapPlan.Kind.Ear, target.Kind);
                var name = MatcapPlan.NewName(parts[0].Material, "abcdef", SecondSkinWriter.MaterialNames(bytes));
                var result = ModelMaterialWriter.MoveToMaterial(bytes, [parts[0]], name);
                Assert.Contains(name, SecondSkinWriter.MaterialNames(result.Model));
                var paths = MatcapPlan.MaterialGamePaths(target, name, [path], null);
                // Published beside the game's own material for that ear (the rule's folder exists in game data).
                var vanillaPaths = CustomizationMaterialPath.GamePaths(path, parts[0].Material);
                Assert.True(data.FileExists(vanillaPaths[0]), vanillaPaths[0]);
                Assert.Equal(Path.GetDirectoryName(vanillaPaths[0]), Path.GetDirectoryName(paths[0]));
                output.WriteLine($"{path}: {parts[0].Label} -> {paths[0]}");
                tested++;
            }
        Assert.True(tested > 0, "no Viera ear model in game data");
    }
}

public class MatcapTailLocalTests(ITestOutputHelper output)
{
    [LocalDataFact(LocalData.GameData)]
    public void Real_tail_models_take_matcap_published_beside_the_games_material()
    {
        var data = ModelMaterialWriterLocalTests.Game();
        int tested = 0;
        foreach (var (race, id) in new[] { (801, 1), (1401, 2), (1501, 1), (1601, 3) })
        {
            var path = $"chara/human/c{race:D4}/obj/tail/t{id:D4}/model/c{race:D4}t{id:D4}_til.mdl";
            var bytes = data.GetFile(path)?.Data;
            if (bytes == null) continue;
            var parts = ModelPartReader.Read(bytes)!.Parts.Where(p => p.Island < 0 && MatcapPlan.Tickable(p)).ToList();
            Assert.NotEmpty(parts);
            var target = MatcapPlan.TargetOf(path, out _)!;
            Assert.Equal(MatcapPlan.Kind.Tail, target.Kind);
            var name = MatcapPlan.NewName(parts[0].Material, "abcdef", SecondSkinWriter.MaterialNames(bytes));
            var result = ModelMaterialWriter.MoveToMaterial(bytes, [parts[0]], name);
            Assert.Contains(name, SecondSkinWriter.MaterialNames(result.Model));
            var paths = MatcapPlan.MaterialGamePaths(target, name, [path], null);
            // Every folder the game ships the tail's own material in gets the new one.
            var vanilla = CustomizationMaterialPath.GamePaths(path, parts[0].Material).Where(data.FileExists).ToList();
            Assert.NotEmpty(vanilla);
            foreach (var v in vanilla)
                Assert.Contains(paths, p => Path.GetDirectoryName(p) == Path.GetDirectoryName(v));
            output.WriteLine($"{path}: {parts[0].Label} -> {string.Join(", ", paths)}");
            tested++;
        }
        Assert.True(tested > 0, "no tail model in game data");
    }
}

public class MatcapDownstreamLocalTests(ITestOutputHelper output)
{
    /// <summary>Positions of one mesh's vertices straight from the file (its position element).</summary>
    private static List<(float X, float Y, float Z)> MeshPositions(byte[] mdl, int mesh)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int mo = src.MeshStart + mesh * 36;
        ushort vc = BitConverter.ToUInt16(mdl, mo);
        var pe = src.Decls[mesh].First(e => e.Usage == SecondSkinWriter.UsePosition);
        uint vbo = BitConverter.ToUInt32(mdl, mo + 20 + pe.Stream * 4);
        byte stride = mdl[mo + 32 + pe.Stream];
        Span<float> tmp = stackalloc float[4];
        var list = new List<(float, float, float)>(vc);
        for (int v = 0; v < vc; v++)
        {
            SecondSkinWriter.ReadTyped(mdl, src.Vb + (int)vbo + v * stride + pe.Offset, pe.Type, tmp);
            list.Add((tmp[0], tmp[1], tmp[2]));
        }
        return list;
    }

    /// <summary>The mesh-local vertices its non-collapsed triangles name.</summary>
    private static HashSet<int> LiveVertices(byte[] mdl, int mesh)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int mo = src.MeshStart + mesh * 36;
        uint ic = BitConverter.ToUInt32(mdl, mo + 4), start = BitConverter.ToUInt32(mdl, mo + 16);
        var live = new HashSet<int>();
        for (uint t = 0; t + 3 <= ic; t += 3)
        {
            int at = src.Ib + (int)(start + t) * 2;
            int a = BitConverter.ToUInt16(mdl, at), b = BitConverter.ToUInt16(mdl, at + 2), c = BitConverter.ToUInt16(mdl, at + 4);
            if (a == b && b == c) continue;
            live.Add(a); live.Add(b); live.Add(c);
        }
        return live;
    }

    /// <summary>The vertices both the source mesh and its copy draw (the seam between them): every one must sit in
    /// the same place in both after a pass, or the matcap's edge opens a crack.</summary>
    private static void AssertSeamClosed(byte[] mdl, int source, int copy)
    {
        var a = MeshPositions(mdl, source);
        var b = MeshPositions(mdl, copy);
        Assert.Equal(a.Count, b.Count);
        // Only the vertices BOTH meshes draw are the seam; a copy no live triangle names is never moved by anyone.
        var seam = LiveVertices(mdl, source);
        seam.IntersectWith(LiveVertices(mdl, copy));
        foreach (var k in seam)
        {
            Assert.True(float.IsFinite(a[k].X) && float.IsFinite(b[k].X), $"vertex {k} is not finite");
            Assert.True(MathF.Abs(a[k].X - b[k].X) < 1e-5f && MathF.Abs(a[k].Y - b[k].Y) < 1e-5f && MathF.Abs(a[k].Z - b[k].Z) < 1e-5f,
                $"vertex {k} moved apart: {a[k]} vs {b[k]}");
        }
    }

    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-matcaps-53")]
    public void Refit_sizeset_hatcompat_lod0geometry_brush_no_nan_boundary_coincident()
    {
        var data = ModelMaterialWriterLocalTests.Game();
        var garment = data.GetFile("chara/equipment/e0010/model/c0101e0010_top.mdl")!.Data;
        var bodyBytes = data.GetFile("chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl")!.Data;
        var cut = ModelLodTrimmer.LodCount(garment) > 1 ? ModelLodTrimmer.KeepLod0(garment, out _)! : garment;
        var part = ModelMaterialWriterLocalTests.FirstCloth(cut);
        var split = ModelMaterialWriter.MoveToMaterial(cut, [part], "/mt_c0101e0010_top_a_mcapabcdef1.mtrl");
        var (src, copy) = split.Meshes.First(m => m.Source != m.Drawn);

        // The geometry reader: finite, and the collapsed triangles read as nothing harmful.
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(split.Model, out var pos, out _, out var tris, out _, out var nrm, skinOnly: false));
        Assert.All(pos, f => Assert.True(float.IsFinite(f)));
        Assert.All(nrm, f => Assert.True(float.IsFinite(f)));

        // A refit in place onto the same body, as the size set and Studio run it.
        var body = ModelPartReader.Read(bodyBytes)!;
        var uv = TestMeshes.Uv(bodyBytes);
        Assert.True(BodyCorrespondence.TryBuild(body, uv, body, uv, "_top", out var built, out string refusal, male: true), refusal);
        var planned = BodyRetarget.Plan(ModelPartReader.Read(split.Model)!, split.Model,
                                        [new BodyRetarget.SlotPair("_top", built!, body)], "_top");
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(planned.Model, out var rpos, out _, out _, out _, out var rnrm, skinOnly: false));
        Assert.All(rpos, f => Assert.True(float.IsFinite(f)));
        Assert.All(rnrm, f => Assert.True(float.IsFinite(f)));
        if (planned.Report.Swap == null) AssertSeamClosed(planned.Model, src, copy);
        Assert.Contains("/mt_c0101e0010_top_a_mcapabcdef1.mtrl", SecondSkinWriter.MaterialNames(planned.Model));
        output.WriteLine($"refit: swap={planned.Report.Swap != null}");
    }

    [LocalDataFact(LocalData.StryxMod)]
    [Trait("Plan", "EDGE-matcaps-29")]
    public void Auto_hat_compat_keeps_matcap_mesh_and_shapes()
    {
        var file = Path.Combine(LocalData.Path(LocalData.StryxMod), "default group", "chara", "human", "c0201", "obj", "hair", "h0168", "model", "c0201h0168_hir.mdl");
        var hair = File.ReadAllBytes(file);
        var cut = ModelLodTrimmer.LodCount(hair) > 1 ? ModelLodTrimmer.KeepLod0(hair, out _)! : hair;
        var part = ModelPartReader.Read(cut)!.Parts.Where(p => p.Island < 0 && MatcapPlan.Tickable(p)).OrderBy(p => p.TriangleCount).First();
        var split = ModelMaterialWriter.MoveToMaterial(cut, [part], "/mt_c0201h0168_hir_b_mcapabcdef1.mtrl");

        // The hat fit, as the automatic one runs it on a worn hairstyle.
        var parts = ModelPartReader.Read(split.Model)!;
        var fit = HatCompatSolve.Solve(split.Model, parts, raceCode: "0201");
        output.WriteLine($"hat fit moved vertices in {fit.Moved.Count} mesh(es), dropped {fit.Dropped}");
        if (fit.Moved.Count > 0)
        {
            const string Shape = "shp_matcap_test";
            var fitted = ModelAttributeWriter.AddShape(split.Model, Shape, fit.Moved);
            Assert.True(ModelAttributeWriter.DeclaresShape(fitted, Shape));
            Assert.Contains("/mt_c0201h0168_hir_b_mcapabcdef1.mtrl", SecondSkinWriter.MaterialNames(fitted));
            Assert.NotNull(ModelPartReader.Read(fitted));
            // Every shape still draws finite geometry.
            foreach (var shape in SecondSkinWriter.Parse(fitted).Shapes.Keys)
                Assert.All(ModelMaterialWriterTests.Drawn(fitted, shape), d => Assert.DoesNotContain("NaN", d));
        }
    }
}

public class MatcapGuardTests
{
    [Fact]
    [Trait("Plan", "EDGE-matcaps-29")]
    public void Characterreflection_never_planned_by_shader_upgrade()
    {
        // The shader upgrade rewrites characterlegacy materials only; a matcap material is characterreflection.
        var built = MatcapMaterial.Build(SyntheticMatcapMod.Reflection(), "n.tex", "m.tex", "c.tex");
        Assert.Equal(MatcapMaterial.Shader, CharacterShaderUpgrade.ShaderOf(built));
        Assert.NotEqual(CharacterShaderUpgrade.Legacy, CharacterShaderUpgrade.ShaderOf(built));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-29")]
    public void Matcap_defaultdata_keys_cover_every_folder_gear_variants_would_copy_to()
    {
        // Gear variants copies a worn mod's material redirects to the variant folders it does not dress. A matcap is
        // published at every folder the item's IMC uses, so it reads as dressed everywhere and nothing is copied over it.
        var item = new VariantRepointPlan.Item('e', 6255, "top");
        var game = new[] { new ImcEntry(1, 0, 0, 0, 0, 0), new ImcEntry(1, 0, 0, 0, 0, 0), new ImcEntry(4, 0, 0, 0, 0, 0) };
        var folders = MatcapPlan.GearFolders(item, game, [], [], 1);
        var target = MatcapPlan.TargetOf("chara/equipment/e6255/model/c0201e6255_top.mdl", out _)!;
        var paths = MatcapPlan.MaterialGamePaths(target, "/mt_c0201e6255_top_a_mcapabcdef1.mtrl", [], folders);
        var dressed = VariantRepointPlan.DressedFolders(paths, item).Folders;
        Assert.Equal(game.Select(e => (int)e.MaterialId).Distinct().Order(), dressed);
    }
}
