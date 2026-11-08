using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The matcap split (<see cref="ModelMaterialWriter"/>), on synthetic v6 and v5 models. What it must hold: the moved
/// triangles draw with the new material and nothing else changes — every triangle's corners, under every shape key —
/// and the source mesh keeps its submesh records and ordinals.
/// </summary>
public class ModelMaterialWriterTests
{
    private const string NewMat = "/mt_c0201e6255_top_a_mcap3fa21c1.mtrl";

    /// <summary>
    /// Every drawn triangle, as "material|corners", corners in index order and with <paramref name="shape"/>'s values
    /// applied. Collapsed triangles draw nothing and are left out. Sorted, so two models compare as multisets. With
    /// <paramref name="allElements"/>, each corner also carries its vertex's raw bytes in every stream the mesh declares
    /// (normals, UVs, weights, colours…), so a wrong stream offset in a copy shows.
    /// </summary>
    internal static List<string> Drawn(byte[] mdl, string? shape = null, bool allElements = false)
    {
        var src = SecondSkinWriter.Parse(mdl);
        var s = src.S;
        var repl = new Dictionary<long, ushort>();
        if (shape != null && src.Shapes.TryGetValue(shape, out var entries))
            foreach (var e in entries)
                foreach (var (b, r) in e.Values) repl[e.MeshIndexOffset + b] = r;

        var list = new List<string>();
        Span<float> tmp = stackalloc float[4];
        for (int m = 0; m < src.MeshCount; m++)
        {
            int mo = src.MeshStart + m * 36;
            ushort vc = BitConverter.ToUInt16(s, mo);
            uint ic = BitConverter.ToUInt32(s, mo + 4), start = BitConverter.ToUInt32(s, mo + 16);
            var mat = src.MatNames[BitConverter.ToUInt16(s, mo + 8)];
            var pe = src.Decls[m].First(e => e.Usage == SecondSkinWriter.UsePosition);
            uint vbo = BitConverter.ToUInt32(s, mo + 20 + pe.Stream * 4);
            byte stride = s[mo + 32 + pe.Stream];
            for (uint t = 0; t + 3 <= ic; t += 3)
            {
                var idx = new int[3];
                for (int k = 0; k < 3; k++)
                {
                    long slot = start + t + k;
                    idx[k] = repl.TryGetValue(slot, out var r) ? r : BitConverter.ToUInt16(s, src.Ib + (int)slot * 2);
                }
                if (idx[0] == idx[1] && idx[1] == idx[2]) continue;
                var corners = new List<string>();
                foreach (var v in idx)
                {
                    Assert.True(v < vc, $"mesh {m} index {v} past its {vc} vertices");
                    SecondSkinWriter.ReadTyped(s, src.Vb + (int)vbo + v * stride + pe.Offset, pe.Type, tmp);
                    var corner = string.Join(",", tmp[..3].ToArray().Select(f => f.ToString("F4", CultureInfo.InvariantCulture)));
                    if (allElements)
                        foreach (var st in src.Decls[m].Select(e => e.Stream).Distinct().Order())
                        {
                            uint so = BitConverter.ToUInt32(s, mo + 20 + st * 4);
                            byte sw = s[mo + 32 + st];
                            corner += "/" + Convert.ToHexString(s, src.Vb + (int)so + v * sw, sw);
                        }
                    corners.Add(corner);
                }
                list.Add($"{mat}|{string.Join(";", corners)}");
            }
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static List<string> Geometry(IEnumerable<string> drawn) =>
        drawn.Select(d => d[(d.IndexOf('|') + 1)..]).OrderBy(x => x, StringComparer.Ordinal).ToList();

    private static List<string> WithMaterial(IEnumerable<string> drawn, string mat) =>
        Geometry(drawn.Where(d => d.StartsWith(mat + "|", StringComparison.Ordinal)));

    /// <summary>The corner strings of the given parts' triangles, before the split.</summary>
    private static List<string> GeometryOf(byte[] mdl, IEnumerable<ModelPart> chosen)
    {
        var parts = ModelPartReader.Read(mdl)!;
        var result = new List<string>();
        foreach (var p in chosen)
            for (int k = 0; k < p.Triangles.Length; k += 3)
                result.Add(string.Join(";", Enumerable.Range(0, 3).Select(c =>
                {
                    int v = p.Triangles[k + c];
                    return string.Join(",", new[] { parts.Positions[v * 3], parts.Positions[v * 3 + 1], parts.Positions[v * 3 + 2] }
                        .Select(f => f.ToString("F4", CultureInfo.InvariantCulture)));
                })));
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static ModelPart Part(byte[] mdl, string label) =>
        ModelPartReader.Read(mdl)!.Parts.Single(p => p.Label == label);

    private static byte[] TwoSubmeshModel(uint version = SyntheticModel.V6) => SyntheticModel.Build([],
        [new SyntheticModel.Mesh("/mt_c0201e6255_top_a.mtrl",
            new SyntheticModel.Sub(0, TrianglesPerIsland: 3),
            new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetY: 10f)),
         new SyntheticModel.Mesh("/mt_c0201e6255_top_b.mtrl",
            new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 50f))], version, (IReadOnlyList<string>?)null);

    [Theory]
    [Trait("Plan", "AC-matcaps-4")]
    [InlineData(SyntheticModel.V6)]
    [InlineData(SyntheticModel.V5)]
    public void Moved_triangles_draw_new_material_rest_unchanged(uint version)
    {
        var before = TwoSubmeshModel(version);
        var chosen = Part(before, "1.2");
        var result = ModelMaterialWriter.MoveToMaterial(before, [chosen], NewMat);

        var was = Drawn(before);
        var now = Drawn(result.Model);
        // The same triangles, corner for corner.
        Assert.Equal(Geometry(was), Geometry(now));
        // The moved ones on the new material, the rest where they were.
        Assert.Equal(GeometryOf(before, [chosen]), WithMaterial(now, NewMat));
        Assert.Equal(WithMaterial(was, "/mt_c0201e6255_top_b.mtrl"), WithMaterial(now, "/mt_c0201e6255_top_b.mtrl"));
        Assert.Equal(3, WithMaterial(now, "/mt_c0201e6255_top_a.mtrl").Count);
        Assert.Equal(1, result.MeshesAdded);
        Assert.Equal(2, result.MaterialIndex);
        Assert.False(result.Lod0Cut);
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-4")]
    public void Readback_parses_with_parser_part_reader_and_geometry_reader()
    {
        var before = TwoSubmeshModel();
        var after = ModelMaterialWriter.MoveToMaterial(before, [Part(before, "1.2")], NewMat).Model;

        var src = SecondSkinWriter.Parse(after);
        Assert.Equal(["/mt_c0201e6255_top_a.mtrl", "/mt_c0201e6255_top_b.mtrl", NewMat], src.MatNames);
        Assert.Equal(3, src.MeshCount);
        Assert.Equal(src.MeshCount, BitConverter.ToUInt16(after, 12));         // one declaration per mesh
        Assert.Equal(3, BitConverter.ToUInt16(after, 14));                      // file header's material count
        Assert.Equal(SecondSkinWriter.Parse(before).BoneNames, src.BoneNames);

        var parts = ModelPartReader.Read(after)!;
        // Labels of the source meshes are stable; the copy is the last mesh.
        Assert.Contains(parts.Parts, p => p.Label == "1.1" && p.Material == "/mt_c0201e6255_top_a.mtrl");
        Assert.Contains(parts.Parts, p => p.Label == "2.1" && p.Material == "/mt_c0201e6255_top_b.mtrl");
        Assert.Contains(parts.Parts, p => p.Label == "3.2" && p.Material == NewMat);
        Assert.DoesNotContain(parts.Parts, p => p.Label == "1.2");             // moved out: nothing left to draw
        Assert.DoesNotContain(parts.Parts, p => p.Label == "3.1");             // collapsed in the copy

        Assert.True(SecondSkinWriter.TryReadLod0Geometry(after, out var pos, out _, out var tris, out _, out _,
            skinOnly: false));
        Assert.All(pos, f => Assert.True(float.IsFinite(f)));
        Assert.NotEmpty(tris);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-1")]
    public void Parts_on_two_meshes_two_new_meshes_one_material()
    {
        var before = SyntheticModel.Build([],
            new SyntheticModel.Mesh("/mt_a.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 2),
                                                  new SyntheticModel.Sub(0, OffsetY: 5f)),
            new SyntheticModel.Mesh("/mt_b.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 30f),
                                                  new SyntheticModel.Sub(0, OffsetX: 30f, OffsetY: 5f)));
        var chosen = new[] { Part(before, "1.2"), Part(before, "2.1") };
        var result = ModelMaterialWriter.MoveToMaterial(before, chosen, NewMat);

        Assert.Equal(2, result.MeshesAdded);
        Assert.Equal([(0, 2), (1, 3)], result.Meshes);
        var now = Drawn(result.Model);
        Assert.Equal(Geometry(Drawn(before)), Geometry(now));
        Assert.Equal(GeometryOf(before, chosen), WithMaterial(now, NewMat));
        Assert.Equal(3, SecondSkinWriter.Parse(result.Model).MatNames.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-2")]
    public void Whole_mesh_switches_material_in_place()
    {
        var before = TwoSubmeshModel();
        var chosen = new[] { Part(before, "2.1") };
        var result = ModelMaterialWriter.MoveToMaterial(before, chosen, NewMat);

        Assert.Equal(0, result.MeshesAdded);
        Assert.Equal([(1, 1)], result.Meshes);
        var src = SecondSkinWriter.Parse(result.Model);
        Assert.Equal(2, src.MeshCount);
        Assert.Equal(2, BitConverter.ToUInt16(result.Model, src.MeshStart + 36 + 8));
        Assert.Equal(GeometryOf(before, chosen), WithMaterial(Drawn(result.Model), NewMat));
        Assert.Equal(Geometry(Drawn(before)), Geometry(Drawn(result.Model)));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-3")]
    public void Island_move_keeps_submesh_records_and_ordinals()
    {
        var before = SyntheticModel.Build([],
            new SyntheticModel.Mesh("/mt_a.mtrl", new SyntheticModel.Sub(0, Islands: 2, TrianglesPerIsland: 2),
                                                  new SyntheticModel.Sub(0, OffsetY: 9f)));
        var parts = ModelPartReader.Read(before)!.Parts;
        var island = parts.First(p => p.Mesh == 0 && p.Submesh == 0 && p.Island >= 0);
        var other = parts.Single(p => p.Mesh == 0 && p.Submesh == 0 && p.Island >= 0 && p != island);

        var after = ModelMaterialWriter.MoveToMaterial(before, [island], NewMat).Model;
        var was = SecondSkinWriter.Parse(before);
        var now = SecondSkinWriter.Parse(after);
        // The source's submesh records, byte for byte.
        Assert.Equal(before.AsSpan(was.SubmeshStart, 2 * 16).ToArray(), after.AsSpan(now.SubmeshStart, 2 * 16).ToArray());

        var read = ModelPartReader.Read(after)!.Parts;
        var left = read.Single(p => p.Mesh == 0 && p.Submesh == 0 && p.Island < 0);
        Assert.Equal(other.Ordinals.OrderBy(x => x), left.Ordinals.OrderBy(x => x));
        var moved = read.Single(p => p.Mesh == 1 && p.Submesh == 0 && p.Island < 0);
        Assert.Equal(island.Ordinals.OrderBy(x => x), moved.Ordinals.OrderBy(x => x));
        Assert.Equal(NewMat, moved.Material);
    }

    /// <summary>Mesh 0: two submeshes and two spares; a shape rewires a slot in each submesh to a spare.</summary>
    private static byte[] ShapedModel(uint version = SyntheticModel.V6) => SyntheticModel.Build([],
        [new SyntheticModel.Mesh("/mt_a.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 2),
                                                new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetY: 7f)) { Spares = 2 }],
        version, [new SyntheticModel.Shape("shp_lift", 0, [(0, 12), (7, 13)])]);

    [Theory]
    [Trait("Plan", "EDGE-matcaps-4")]
    [InlineData(SyntheticModel.V6)]
    [InlineData(SyntheticModel.V5)]
    public void Shapes_on_moved_triangles_carried(uint version)
    {
        var before = ShapedModel(version);
        var after = ModelMaterialWriter.MoveToMaterial(before, [Part(before, "1.2")], NewMat).Model;

        // Under the shape, every triangle is where it was; the shaped one now draws with the new material.
        var was = Drawn(before, "shp_lift");
        var now = Drawn(after, "shp_lift");
        Assert.Equal(Geometry(was), Geometry(now));
        Assert.Equal(2, WithMaterial(now, NewMat).Count);
        Assert.NotEqual(Geometry(Drawn(after)), Geometry(now));    // the shape still does something
        Assert.Equal(Geometry(Drawn(before)), Geometry(Drawn(after)));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-4")]
    public void Several_shapes_several_meshes_M_not_last_rebased()
    {
        var before = SyntheticModel.Build([],
            [new SyntheticModel.Mesh("/mt_a.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 2),
                                                    new SyntheticModel.Sub(0, OffsetY: 4f)) { Spares = 1 },
             new SyntheticModel.Mesh("/mt_b.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 20f),
                                                    new SyntheticModel.Sub(0, OffsetX: 20f, OffsetY: 4f)) { Spares = 1 },
             new SyntheticModel.Mesh("/mt_c.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 40f)) { Spares = 1 }],
            SyntheticModel.V6,
            [new SyntheticModel.Shape("shp_a", 0, [(6, 9)]),
             new SyntheticModel.Shape("shp_b", 1, [(1, 9), (6, 9)]),
             new SyntheticModel.Shape("shp_c", 2, [(0, 6)])]);
        var chosen = new[] { Part(before, "1.2"), Part(before, "2.1") };
        var result = ModelMaterialWriter.MoveToMaterial(before, chosen, NewMat);
        var after = result.Model;
        Assert.Equal(2, result.MeshesAdded);

        foreach (var shape in new[] { "shp_a", "shp_b", "shp_c" })
            Assert.Equal(Geometry(Drawn(before, shape)), Geometry(Drawn(after, shape)));

        // Each shape's entries: its own first, then the copies'. Each copy's window sits on the copy's run.
        var src = SecondSkinWriter.Parse(after);
        uint StartOf(int mesh) => BitConverter.ToUInt32(after, src.MeshStart + mesh * 36 + 16);
        Assert.Equal([StartOf(0), StartOf(3)], src.Shapes["shp_a"].Select(e => e.MeshIndexOffset));
        Assert.Equal([StartOf(1), StartOf(4)], src.Shapes["shp_b"].Select(e => e.MeshIndexOffset));
        Assert.Equal([StartOf(2)], src.Shapes["shp_c"].Select(e => e.MeshIndexOffset));

        // The copies' submesh records are re-based onto their runs.
        for (int copy = 3; copy <= 4; copy++)
        {
            int mo = src.MeshStart + copy * 36;
            ushort subIdx = BitConverter.ToUInt16(after, mo + 10);
            Assert.Equal(StartOf(copy), BitConverter.ToUInt32(after, src.SubmeshStart + subIdx * 16));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-5")]
    public void Shapes_cannot_reopen_collapsed_triangles()
    {
        var before = ShapedModel();
        var after = ModelMaterialWriter.MoveToMaterial(before, [Part(before, "1.2")], NewMat).Model;
        var src = SecondSkinWriter.Parse(after);

        // Every shape value on a collapsed slot points at that slot's collapsed corner.
        foreach (var e in src.Shapes["shp_lift"])
            foreach (var (b, r) in e.Values)
            {
                long slot = e.MeshIndexOffset + b;
                long tri = slot - slot % 3;
                int at = src.Ib + (int)tri * 2;
                ushort a = BitConverter.ToUInt16(after, at);
                if (a == BitConverter.ToUInt16(after, at + 2) && a == BitConverter.ToUInt16(after, at + 4))
                    Assert.Equal(a, r);
            }
        // Under the shape the source draws exactly its unmoved submesh.
        Assert.Equal(2, WithMaterial(Drawn(after, "shp_lift"), "/mt_a.mtrl").Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-6")]
    public void Trimmer_refusal_propagates()
    {
        var junk = new byte[256];
        junk[64] = 3;
        var ex = Assert.Throws<ModelMaterialWriter.Refused>(() =>
            ModelMaterialWriter.MoveToMaterial(junk, [Part(TwoSubmeshModel(), "1.1")], NewMat));
        Assert.Equal(ModelMaterialWriter.CodeLayout, ex.Code);
    }

    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-matcaps-6")]
    public void MultiLod_cut_and_flagged()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var before = data.GetFile("chara/equipment/e6200/model/c0201e6200_top.mdl")!.Data;
        Assert.Equal(3, ModelLodTrimmer.LodCount(before));
        var part = ModelPartReader.Read(before)!.Parts.First(p => p.Island < 0);
        var result = ModelMaterialWriter.MoveToMaterial(before, [part], NewMat);
        Assert.True(result.Lod0Cut);
        Assert.Equal(1, ModelLodTrimmer.LodCount(result.Model));
        Assert.Equal(GeometryOf(before, [part]), WithMaterial(Drawn(result.Model), NewMat));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-7")]
    public void Unsupported_layouts_and_overflow_refused()
    {
        var good = TwoSubmeshModel();
        var part = Part(good, "1.2");
        var src = SecondSkinWriter.Parse(good);

        void Refused(Action<byte[]> breakIt)
        {
            var bad = (byte[])good.Clone();
            breakIt(bad);
            var copy = (byte[])bad.Clone();
            var ex = Assert.Throws<ModelMaterialWriter.Refused>(() => ModelMaterialWriter.MoveToMaterial(bad, [part], NewMat));
            Assert.Equal(ModelMaterialWriter.CodeLayout, ex.Code);
            Assert.Equal(copy, bad);                                             // input untouched
        }

        Refused(b => b[src.Mh + 26] = 1);                                      // terrain shadow meshes
        Refused(b => b[src.Mh + 27] |= 0x10);                                  // extra LODs
        Refused(b => BitConverter.TryWriteBytes(b.AsSpan(src.LodStart + 14), (ushort)1));   // water meshes
        Refused(b => BitConverter.TryWriteBytes(b.AsSpan(src.LodStart + 28), 4u));         // edge geometry

        // A part naming a submesh the model lacks.
        var stray = new ModelPart
        {
            Mesh = 0, Submesh = 9, Island = -1, Label = "1.10", Material = "/mt_c0201e6255_top_a.mtrl",
            Triangles = [], Ordinals = [0], AttributeMask = 0, Min = default, Max = default, Toggleable = true,
        };
        Assert.Equal(ModelMaterialWriter.CodeUnknownPart,
            Assert.Throws<ModelMaterialWriter.Refused>(() => ModelMaterialWriter.MoveToMaterial(good, [stray], NewMat)).Code);
        // A name the model already has.
        Assert.Throws<ModelMaterialWriter.Refused>(() =>
            ModelMaterialWriter.MoveToMaterial(good, [part], "/mt_c0201e6255_top_a.mtrl"));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-8")]
    public void Material_cap_refused()
    {
        var meshes = Enumerable.Range(0, SecondSkinWriter.MaxMaterials)
            .Select(i => new SyntheticModel.Mesh($"/mt_m{i}.mtrl", new SyntheticModel.Sub(0, OffsetX: i * 20f)))
            .ToArray();
        var full = SyntheticModel.Build([], meshes);
        Assert.Equal(0, ModelMaterialWriter.MaterialRoom(full));
        var ex = Assert.Throws<ModelMaterialWriter.Refused>(() =>
            ModelMaterialWriter.MoveToMaterial(full, [Part(full, "1.1")], NewMat));
        Assert.Equal(ModelMaterialWriter.CodeMaterialCap, ex.Code);
        Assert.Equal(SecondSkinWriter.MaxMaterials - 2, ModelMaterialWriter.MaterialRoom(TwoSubmeshModel()));
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-4")]
    public void A_second_split_on_the_result_adds_another_material()
    {
        var before = TwoSubmeshModel();
        var once = ModelMaterialWriter.MoveToMaterial(before, [Part(before, "1.2")], NewMat).Model;
        var twice = ModelMaterialWriter.MoveToMaterial(once, [Part(once, "1.1")], "/mt_c0201e6255_top_a_mcap3fa21c2.mtrl");
        var now = Drawn(twice.Model);
        Assert.Equal(Geometry(Drawn(before)), Geometry(now));
        Assert.Equal(2, WithMaterial(now, NewMat).Count);
        Assert.Equal(3, WithMaterial(now, "/mt_c0201e6255_top_a_mcap3fa21c2.mtrl").Count);
        Assert.Empty(WithMaterial(now, "/mt_c0201e6255_top_a.mtrl"));
    }
}
