using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <see cref="ModelAttributeWriter.AddShape"/> — adding a morph target to a model that already exists.
/// <para/>
/// Almost every test here is about arithmetic that has no visible symptom when it is wrong. A shape whose
/// tables are a few bytes out does not throw and does not fail to load; it deforms the wrong triangles, or
/// skins a later mesh to the wrong bones, and the first report is somebody's hair going through the floor.
/// So the assertions are on the BYTES — which offset moved by which delta, and which bytes did not move at
/// all — rather than on the result parsing cleanly, which it does either way.
/// </summary>
public class ModelShapeWriterTests
{
    private const string Mat = "/mt_c0201b0001_a.mtrl";
    private const int Stride = 20;   // SyntheticModel: position float3 @0, uv float2 @12

    private static byte[] TwoMesh(uint version = SyntheticModel.V6, IReadOnlyList<string>? shapes = null)
        => SyntheticModel.Build(
            ["atr_top"],
            [new SyntheticModel.Mesh(Mat, new SyntheticModel.Sub(0)),
             new SyntheticModel.Mesh(Mat, new SyntheticModel.Sub(0))],
            version, shapes);

    private static Dictionary<int, IReadOnlyDictionary<int, Vector3>> Move(int mesh, params (int V, Vector3 P)[] m)
        => new() { [mesh] = m.ToDictionary(x => x.V, x => x.P) };

    private static ushort U16(byte[] b, int o) => BitConverter.ToUInt16(b, o);
    private static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);

    /// <summary>Mesh record fields, read straight out of a file rather than through the parser.</summary>
    private static (ushort Vc, uint Ic, uint Start, uint Vbo) MeshAt(byte[] mdl, int mesh)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int mo = src.MeshStart + mesh * 36;
        return (U16(mdl, mo), U32(mdl, mo + 4), U32(mdl, mo + 16), U32(mdl, mo + 20));
    }

    /// <summary>The shape block's three arrays, located the way the format locates them.</summary>
    private static (int Shape, int ShapeMesh, int Value, int Count, int MeshCount, int ValueCount)
        Tables(byte[] mdl)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int sc = U16(mdl, src.Mh + 16), smc = U16(mdl, src.Mh + 18), svc = U16(mdl, src.Mh + 20);
        return (src.ShapeBlock, src.ShapeBlock + sc * 16, src.ShapeBlock + sc * 16 + smc * 12, sc, smc, svc);
    }

    // ── the shape does what a shape is ──────────────────────────────────────

    /// <summary>
    /// The spares go at the END of the moved mesh's own vertex block, and every later mesh's offset follows.
    /// This is the property the whole design rests on: appending there is what leaves every existing vertex
    /// index and byte address alone, which is in turn what lets an existing shape survive untouched.
    /// </summary>
    [Fact]
    public void AppendsSparesAtTheEndOfTheMeshsOwnBlock()
    {
        var before = TwoMesh();
        var (vc0, _, _, vbo0) = MeshAt(before, 0);
        var (_, _, _, vbo1) = MeshAt(before, 1);
        var srcVb = SecondSkinWriter.Parse(before).Vb;

        var after = ModelAttributeWriter.AddShape(before, "shp_hib", Move(0, (1, new Vector3(5, 6, 7))));

        var (nvc0, _, _, nvbo0) = MeshAt(after, 0);
        var (_, _, _, nvbo1) = MeshAt(after, 1);
        Assert.Equal(vc0 + 1, nvc0);
        Assert.Equal(vbo0, nvbo0);                    // the mesh that grew starts where it started
        Assert.Equal(vbo1 + Stride, nvbo1);           // the one after it moved by exactly one vertex

        // The spare is a byte-for-byte copy of its source except the position, which is what was asked for.
        int newVb = SecondSkinWriter.Parse(after).Vb;
        int spare = newVb + (int)nvbo0 + (nvc0 - 1) * Stride;
        int source = srcVb + (int)vbo0 + 1 * Stride;
        Assert.Equal(5f, BitConverter.ToSingle(after, spare));
        Assert.Equal(6f, BitConverter.ToSingle(after, spare + 4));
        Assert.Equal(7f, BitConverter.ToSingle(after, spare + 8));
        Assert.Equal(before[(source + 12)..(source + 20)], after[(spare + 12)..(spare + 20)]);   // the UV
    }

    /// <summary>
    /// One value per INDEX SLOT, not per vertex, and the slot is mesh-relative.
    /// <para/>
    /// Both halves have a wrong answer that looks right. A vertex drawn by six triangles is named by six
    /// slots, and rewiring only the first leaves the shape tearing the mesh along the other five. And mesh 1
    /// starts at index 3 here, so an implementation storing absolute slots would write 3 and 5 — a file that
    /// loads, and deforms whichever triangles happen to sit at those positions in mesh 0.
    /// </summary>
    [Fact]
    public void EmitsOneValuePerIndexSlotMeshRelative()
    {
        var before = TwoMesh();
        var src = SecondSkinWriter.Parse(before);
        var (_, _, start1, _) = MeshAt(before, 1);
        Assert.True(start1 > 0, "the fixture's second mesh must not start at index 0 for this to prove anything");

        // The fixture's triangle names vertices 0, 1, 2 at slots 0, 1, 2. Point slot 2 at vertex 0 as well,
        // so vertex 0 is drawn twice and owes two values — at slots 0 and 2, with slot 1 left out.
        BitConverter.TryWriteBytes(before.AsSpan(src.Ib + (int)(start1 + 2) * 2), (ushort)0);

        var after = ModelAttributeWriter.AddShape(before, "shp_hib", Move(1, (0, new Vector3(0, 0, 0))));

        var t = Tables(after);
        Assert.Equal(1, t.Count);
        Assert.Equal(1, t.MeshCount);
        Assert.Equal(2, t.ValueCount);
        Assert.Equal(start1, U32(after, t.ShapeMesh));           // ShapeMesh names the mesh by its StartIndex
        Assert.Equal(2u, U32(after, t.ShapeMesh + 4));

        var slots = new[] { U16(after, t.Value), U16(after, t.Value + 4) };
        Assert.Equal([0, 2], slots);                             // NOT 3 and 5
        var (vc1, _, _, _) = MeshAt(before, 1);
        Assert.Equal(vc1, U16(after, t.Value + 2));              // both select the one appended spare
        Assert.Equal(vc1, U16(after, t.Value + 6));
    }

    /// <summary>The position asked for is the position written, and nothing else about the vertex changes.</summary>
    [Fact]
    public void WritesThePositionItWasGiven()
    {
        var before = TwoMesh();
        var after = ModelAttributeWriter.AddShape(
            before, "shp_hib", Move(0, (0, new Vector3(-1.5f, 2.25f, 0.125f))));

        var src = SecondSkinWriter.Parse(after);
        var (vc, _, _, vbo) = MeshAt(after, 0);
        var pos = Array.Find(src.Decls[0], e => e.Usage == SecondSkinWriter.UsePosition);
        Span<float> t = stackalloc float[4];
        SecondSkinWriter.ReadTyped(after, src.Vb + (int)vbo + (vc - 1) * Stride + pos.Offset, pos.Type, t);
        Assert.Equal(-1.5f, t[0]);
        Assert.Equal(2.25f, t[1]);
        Assert.Equal(0.125f, t[2]);
    }

    // ── the tables ──────────────────────────────────────────────────────────

    /// <summary>
    /// An existing shape comes through byte-identical, and the new one is appended after it.
    /// <para/>
    /// The claim being tested is that NO existing field needs renumbering — which is true only because
    /// <c>shapeMeshStart</c> and <c>valueStart</c> are global indices into arrays we only ever append to.
    /// If that were wrong, this is where it shows.
    /// </summary>
    [Fact]
    public void AppendsToTheTablesWithoutDisturbingAnExistingShape()
    {
        var before = TwoMesh(shapes: ["shp_base"]);
        var b = Tables(before);
        var oldShape = before[b.Shape..(b.Shape + 16)];
        var oldMesh = before[b.ShapeMesh..(b.ShapeMesh + 12)];
        var oldValue = before[b.Value..(b.Value + 4)];

        var after = ModelAttributeWriter.AddShape(before, "shp_hib", Move(0, (1, new Vector3(1, 1, 1))));

        var a = Tables(after);
        Assert.Equal(2, a.Count);
        Assert.Equal(2, a.MeshCount);
        Assert.Equal(oldShape, after[a.Shape..(a.Shape + 16)]);
        Assert.Equal(oldMesh, after[a.ShapeMesh..(a.ShapeMesh + 12)]);
        Assert.Equal(oldValue, after[a.Value..(a.Value + 4)]);

        // The new Shape points past the existing records, in both arrays.
        Assert.Equal(1, U16(after, a.Shape + 16 + 4));               // LOD0 shapeMeshStart
        Assert.Equal(1, U16(after, a.Shape + 16 + 10));              // LOD0 shapeMeshCount
        Assert.Equal(1u, U32(after, a.ShapeMesh + 12 + 8));          // valueStart

        var parsed = SecondSkinWriter.Parse(after).Shapes;
        Assert.Contains("shp_base", parsed.Keys);
        Assert.Contains("shp_hib", parsed.Keys);
    }

    /// <summary>
    /// Into a model with no shape block at all — where all three arrays are empty and therefore start at the
    /// same offset, so the only thing putting them in the right order is that <c>Splice</c> sorts stably.
    /// </summary>
    [Fact]
    public void InsertsIntoAModelWithNoShapeBlock()
    {
        var before = TwoMesh();
        Assert.Empty(SecondSkinWriter.Parse(before).Shapes);

        var after = ModelAttributeWriter.AddShape(before, "shp_hib", Move(0, (1, new Vector3(1, 2, 3))));

        var t = Tables(after);
        Assert.Equal(t.Shape + 16, t.ShapeMesh);       // Shape first,
        Assert.Equal(t.ShapeMesh + 12, t.Value);       // then ShapeMesh, then ShapeValue
        var shapes = SecondSkinWriter.Parse(after).Shapes;
        Assert.Equal(["shp_hib"], shapes.Keys.ToArray());
        Assert.Single(shapes["shp_hib"]);
    }

    // ── the arithmetic ──────────────────────────────────────────────────────

    /// <summary>
    /// The centrepiece. Inserts land on BOTH sides of the vertex buffer, so the eight absolute offsets do
    /// not all move by the same amount, and the three that must not follow the vertex growth are the ones
    /// naming where that buffer starts.
    /// <para/>
    /// Getting this wrong by reusing the unconditional shift moves <c>VertexOffset[0]</c> by the vertex
    /// delta as well, sliding the entire buffer one stride out of register against every mesh's offset. The
    /// file still loads. Every vertex in it is somebody else's.
    /// </summary>
    [Fact]
    public void MovesOnlyTheOffsetsPastTheVertexBuffer()
    {
        var before = TwoMesh();
        // The fixture leaves LOD1/2's data offsets at zero, where a real file parks them at EOF. Zero is
        // exempted by the "leave a zero alone" rule, so it would hide a threshold that never fires.
        var lod = SecondSkinWriter.Parse(before).LodStart;
        for (int l = 1; l < 3; l++)
        {
            BitConverter.TryWriteBytes(before.AsSpan(lod + l * 60 + 52), (uint)before.Length);
            BitConverter.TryWriteBytes(before.AsSpan(lod + l * 60 + 56), (uint)before.Length);
        }

        var (rt, v0, i0, vSize, lodV, lodI, lod0V, lod0I, lod1V) = Snapshot(before, lod);
        var after = ModelAttributeWriter.AddShape(before, "shp_hib", Move(0, (1, new Vector3(9, 9, 9))));

        int dV = Stride;                                   // one spare vertex, one stream
        int dMeta = after.Length - before.Length - dV;
        Assert.True(dMeta > 0);

        int lodN = SecondSkinWriter.Parse(after).LodStart;
        var a = Snapshot(after, lodN);

        // Naming where the vertex buffer STARTS — metadata only.
        Assert.Equal(rt + (uint)dMeta, a.Rt);
        Assert.Equal(v0 + (uint)dMeta, a.V0);
        Assert.Equal(lod0V + (uint)dMeta, a.Lod0V);
        // Sitting after it — both deltas.
        Assert.Equal(i0 + (uint)(dMeta + dV), a.I0);
        Assert.Equal(lod0I + (uint)(dMeta + dV), a.Lod0I);
        Assert.Equal(lod1V + (uint)(dMeta + dV), a.Lod1V);
        // Sizes: the vertex buffer grew, the index buffer did not.
        Assert.Equal(vSize + (uint)dV, a.VSize);
        Assert.Equal(lodV + (uint)dV, a.LodV);
        Assert.Equal(lodI, a.LodI);

        // And the geometry itself came through untouched: same indices, same original vertices.
        Assert.Equal(before[(int)i0..(int)(i0 + lodI)], after[(int)a.I0..(int)(a.I0 + a.LodI)]);
        var (vc, _, _, vbo) = MeshAt(before, 0);
        Assert.Equal(before[((int)v0 + (int)vbo)..((int)v0 + (int)vbo + vc * Stride)],
                     after[((int)a.V0 + (int)vbo)..((int)a.V0 + (int)vbo + vc * Stride)]);
    }

    private static (uint Rt, uint V0, uint I0, uint VSize, uint LodV, uint LodI, uint Lod0V, uint Lod0I, uint Lod1V)
        Snapshot(byte[] m, int lod)
        => (U32(m, 8), U32(m, 16), U32(m, 28), U32(m, 40),
            U32(m, lod + 44), U32(m, lod + 48), U32(m, lod + 52), U32(m, lod + 56), U32(m, lod + 60 + 52));

    /// <summary>Two meshes growing at once: each spare block lands in its own mesh, and the values do not overlap.</summary>
    [Fact]
    public void TwoMeshesGrowIndependently()
    {
        var before = TwoMesh();
        var (_, _, start0, vbo0) = MeshAt(before, 0);
        var (_, _, start1, vbo1) = MeshAt(before, 1);

        var moved = new Dictionary<int, IReadOnlyDictionary<int, Vector3>>
        {
            [0] = new Dictionary<int, Vector3> { [0] = new(1, 1, 1) },
            [1] = new Dictionary<int, Vector3> { [1] = new(2, 2, 2), [2] = new(3, 3, 3) },
        };
        var after = ModelAttributeWriter.AddShape(before, "shp_hib", moved);

        Assert.Equal(vbo0, MeshAt(after, 0).Vbo);                    // first mesh's block starts where it did
        Assert.Equal(vbo1 + Stride, MeshAt(after, 1).Vbo);           // moved by mesh 0's one spare only

        var t = Tables(after);
        Assert.Equal(1, t.Count);
        Assert.Equal(2, t.MeshCount);
        Assert.Equal(0, U16(after, t.Shape + 4));                    // LOD0 shapeMeshStart
        Assert.Equal(2, U16(after, t.Shape + 10));                   // one Shape over two ShapeMeshes
        Assert.Equal(start0, U32(after, t.ShapeMesh));
        Assert.Equal(start1, U32(after, t.ShapeMesh + 12));
        // Consecutive, non-overlapping value ranges.
        Assert.Equal(0u, U32(after, t.ShapeMesh + 8));
        Assert.Equal(U32(after, t.ShapeMesh + 4), U32(after, t.ShapeMesh + 12 + 8));
        Assert.Equal(t.ValueCount, (int)(U32(after, t.ShapeMesh + 4) + U32(after, t.ShapeMesh + 12 + 4)));
    }

    /// <summary>Nothing else in the model is rewritten — in particular no submesh record, which addresses an
    /// index buffer that did not move internally.</summary>
    [Fact]
    public void LeavesEveryOtherRecordAlone()
    {
        var before = TwoMesh();
        var b = SecondSkinWriter.Parse(before);
        var subs = before[b.SubmeshStart..(b.SubmeshStart + b.SubmeshCount * 16)];
        var mesh1 = before[(b.MeshStart + 36)..(b.MeshStart + 72)];

        var after = ModelAttributeWriter.AddShape(before, "shp_hib", Move(0, (1, new Vector3(1, 1, 1))));

        var a = SecondSkinWriter.Parse(after);
        Assert.Equal(subs, after[a.SubmeshStart..(a.SubmeshStart + a.SubmeshCount * 16)]);
        // Mesh 1 differs in exactly one field: its vertex buffer offset.
        var mesh1After = after[(a.MeshStart + 36)..(a.MeshStart + 72)];
        Assert.Equal(mesh1[..20], mesh1After[..20]);
        Assert.Equal(mesh1[24..], mesh1After[24..]);
        Assert.NotEqual(mesh1[20..24], mesh1After[20..24]);
    }

    // ── both format versions ────────────────────────────────────────────────

    /// <summary>
    /// v5 and v6 differ only in the bone table — which is the block immediately before the shape block, so a
    /// version the writer walks wrongly puts the whole insert inside a bone table. The file would still be
    /// the right length and still parse partway.
    /// </summary>
    [Theory]
    [InlineData(SyntheticModel.V5)]
    [InlineData(SyntheticModel.V6)]
    public void KeepsTheModelReadableOnBothVersions(uint version)
    {
        var before = TwoMesh(version, ["shp_base"]);
        var after = ModelAttributeWriter.AddShape(before, "shp_hib", Move(0, (1, new Vector3(4, 5, 6))));

        var src = SecondSkinWriter.Parse(after);
        Assert.Equal(["shp_base", "shp_hib"], src.Shapes.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(["atr_top"], src.AttrNames);
        Assert.Equal([0], src.BoneTables[0]);
        Assert.Empty(src.SubmeshBoneMap);

        Assert.True(SecondSkinWriter.TryReadLod0Geometry(after, out var pos, out _, out var tris));
        Assert.NotEmpty(tris);
        Assert.NotEmpty(pos);
        Assert.NotNull(ModelPartReader.Read(after));
    }

    /// <summary>An attribute and a shape on the same model, in the order the service applies them.</summary>
    [Fact]
    public void ComposesWithAddAttribute()
    {
        var before = TwoMesh();
        var tagged = ModelAttributeWriter.AddAttribute(before, "atr_kam", [(1, 0)]);
        var after = ModelAttributeWriter.AddShape(tagged, "shp_hib", Move(0, (1, new Vector3(1, 1, 1))));

        var src = SecondSkinWriter.Parse(after);
        Assert.Equal(["atr_top", "atr_kam"], src.AttrNames);
        Assert.Contains("shp_hib", src.Shapes.Keys);
        // The attribute still lands on the submesh it was aimed at.
        int mo = src.MeshStart + 36;
        int ss = src.SubmeshStart + U16(after, mo + 10) * 16;
        Assert.Equal(2u, U32(after, ss + 8));
    }

    // ── refusals ────────────────────────────────────────────────────────────

    [Fact]
    public void RefusesAShapeItAlreadyHas()
        => Assert.Throws<ModelAttributeWriter.ModelEditException>(() =>
            ModelAttributeWriter.AddShape(TwoMesh(shapes: ["shp_hib"]), "shp_hib",
                                          Move(0, (0, Vector3.Zero))));

    [Fact]
    public void RefusesAMeshOutsideLod0()
        => Assert.Throws<ModelAttributeWriter.ModelEditException>(() =>
            ModelAttributeWriter.AddShape(TwoMesh(), "shp_hib", Move(7, (0, Vector3.Zero))));

    [Fact]
    public void RefusesAVertexTheMeshDoesNotHave()
        => Assert.Throws<ModelAttributeWriter.ModelEditException>(() =>
            ModelAttributeWriter.AddShape(TwoMesh(), "shp_hib", Move(0, (9999, Vector3.Zero))));

    /// <summary>
    /// A mesh already at the vertex ceiling. The count is a u16 and so is the replacement index, so the
    /// limit bites twice; passing it would wrap silently and reinterpret every index in the mesh.
    /// </summary>
    [Fact]
    public void RefusesToOverflowTheVertexCount()
    {
        var before = TwoMesh();
        var src = SecondSkinWriter.Parse(before);
        BitConverter.TryWriteBytes(before.AsSpan(src.MeshStart), ushort.MaxValue);
        Assert.Throws<ModelAttributeWriter.ModelEditException>(() =>
            ModelAttributeWriter.AddShape(before, "shp_hib", Move(0, (0, Vector3.Zero))));
    }

    /// <summary>A vertex no triangle names would grow the file and deform nothing, so it is not a shape.</summary>
    [Fact]
    public void RefusesVerticesNoTriangleUses()
    {
        var before = TwoMesh();
        var src = SecondSkinWriter.Parse(before);
        // Point every one of mesh 0's index slots at vertex 0, so vertex 1 is drawn by nothing.
        var (_, ic, start, _) = MeshAt(before, 0);
        for (uint s = 0; s < ic; s++)
            BitConverter.TryWriteBytes(before.AsSpan(src.Ib + (int)(start + s) * 2), (ushort)0);
        Assert.Throws<ModelAttributeWriter.ModelEditException>(() =>
            ModelAttributeWriter.AddShape(before, "shp_hib", Move(0, (1, Vector3.Zero))));
    }

    /// <summary>
    /// A model whose tables do not add up. The probe reads the submesh bone map's length prefix from just
    /// past the shape block — on a mis-walk that is arbitrary bytes, and refusing there is the difference
    /// between a clean error and splicing new tables into the middle of a bone table.
    /// <para/>
    /// The corruption is deliberately PLAUSIBLE: an odd byte count, which the format can never produce for
    /// an array of shorts but which is exactly what landing three bytes into something else looks like. An
    /// absurd value would not test this — it dies inside the parse instead, long before the probe.
    /// </summary>
    [Fact]
    public void RefusesWhenTheShapeBlockIsNotWhereTheFormatSaysItIs()
    {
        var before = TwoMesh();
        var src = SecondSkinWriter.Parse(before);
        BitConverter.TryWriteBytes(before.AsSpan(src.ShapeBlock), 3u);   // an odd number of bytes of shorts
        Assert.Throws<ModelAttributeWriter.ModelEditException>(() =>
            ModelAttributeWriter.AddShape(before, "shp_hib", Move(0, (0, Vector3.Zero))));
    }
}
