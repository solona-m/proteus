using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The writer, against a genuinely valid <c>.mdl</c>. The edit goes into somebody else's mod, so what is
/// asserted here is mostly what must NOT change: the file's length, every byte outside the vertex buffer
/// and the stored extents, and the model's ability to be parsed afterwards.
/// <para/>
/// The displacement is produced by a real <see cref="MeshVolumeSolve"/>, but over a triangle list built here
/// rather than the file's own. <see cref="SyntheticModel"/> emits isolated triangles that share corner
/// positions and no indices, which gives the solve no connected surface to work over. The positions, the
/// normals and above all the
/// <see cref="ModelParts.MeshSpans"/> come from the real reader, so the mapping under test — concatenated
/// vertex back to a mesh's own buffer — is the real one.
/// </summary>
public class MeshVolumeServiceTests
{
    /// <summary>
    /// One mesh of two triangles is six vertices, which weld by position into four distinct points: the two
    /// triangles share their hub and one edge corner. Four points is exactly enough for a closed
    /// tetrahedron, where every edge is used by two faces and therefore nothing is a hole rim.
    /// </summary>
    private static (byte[] Mdl, MeshVolumeSolve Solve, ModelParts Model) Setup(
        IReadOnlyList<string>? shapes = null)
    {
        var mdl = SyntheticModel.Build(
            [],
            [new SyntheticModel.Mesh("/mt_test.mtrl", new SyntheticModel.Sub(0, Islands: 1,
                                                                            TrianglesPerIsland: 2))],
            SyntheticModel.V6, shapes);

        var read = ModelPartReader.Read(mdl);
        Assert.NotNull(read);
        Assert.Equal(6, read!.Positions.Length / 3);

        // Vertices 0, 1, 2 and 5 land on four distinct positions; 3 and 4 are duplicates of 0 and 2.
        int[] tetra = [0, 1, 2, 0, 2, 5, 0, 5, 1, 1, 5, 2];

        // Normals along +Y so the direction a vertex inflates is known rather than inferred from winding.
        var nrm = new float[read.Positions.Length];
        for (int i = 0; i < nrm.Length / 3; i++) nrm[i * 3 + 1] = 1f;

        var model = new ModelParts
        {
            Positions = read.Positions,
            Normals = nrm,
            MeshSpans = read.MeshSpans,
            Parts =
            [
                new ModelPart
                {
                    Mesh = 0, Submesh = 0, Island = -1, Label = "1.1", Material = "/mt_test.mtrl",
                    Triangles = tetra, Ordinals = [0, 1, 2, 3], AttributeMask = 0, Toggleable = true,
                    Min = Vector3.Zero, Max = Vector3.One,
                },
            ],
            AttributeNames = read.AttributeNames,
            Min = read.Min,
            Max = read.Max,
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };

        return (mdl, new MeshVolumeSolve(model), model);
    }

    private static float PositionY(byte[] mdl, int vertex)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int mo = src.MeshStart + 0 * 36;
        var pe = src.Decls[0].First(e => e.Usage == SecondSkinWriter.UsePosition);
        uint vbo = BitConverter.ToUInt32(mdl, mo + 20 + pe.Stream * 4);
        byte stride = mdl[mo + 32 + pe.Stream];
        Span<float> tmp = stackalloc float[4];
        SecondSkinWriter.ReadTyped(mdl, src.Vb + (int)vbo + vertex * stride + pe.Offset, pe.Type, tmp);
        return tmp[1];
    }

    /// <summary>
    /// The file keeps its length and its shape, every brushed vertex moves by the displacement, and NOTHING
    /// outside the vertex buffer and the stored extents is touched.
    /// <para/>
    /// That last assertion is the one that makes this edit defensible on a mod that is not ours. Positions in
    /// place is only safe because nothing changes the file's length — no offset in the header moves, and
    /// every table, string, bone map and opaque tail stays exactly where it was.
    /// </summary>
    /// <summary>
    /// The level count is ONE byte of the model header; the next is Flags1. Read as a u16 they called a one-level model
    /// with a flag set multi-level (Neolithe's chests: 1025), and every refit of one warned it would look like the old
    /// size from a distance.
    /// </summary>
    [Fact]
    public void A_one_level_model_with_flags_set_is_one_level()
    {
        var (mdl, solve, model) = Setup();
        int mh = SecondSkinWriter.Parse(mdl).Mh;
        Assert.Equal(1, mdl[mh + 22]);
        mdl[mh + 23] = 0x04;

        var centre = new Vector3(model.Positions[0], model.Positions[1], model.Positions[2]);
        solve.Paint(centre, 10f, 0.002f);
        solve.EndStroke();
        Assert.False(MeshVolumeService.Inflate(mdl, solve).HasOtherLods);
    }

    [Fact]
    public void WritesPositionsInPlaceAndTouchesNothingElse()
    {
        var (mdl, solve, model) = Setup();

        // Centred on vertex 0 with a radius that swallows the whole tetrahedron.
        var centre = new Vector3(model.Positions[0], model.Positions[1], model.Positions[2]);
        Assert.True(solve.Paint(centre, 10f, 0.002f) > 0);
        solve.EndStroke();
        Assert.True(solve.Worst > 0f);

        var written = MeshVolumeService.Inflate(mdl, solve);

        Assert.Equal(mdl.Length, written.Model.Length);
        Assert.False(written.HasOtherLods);
        Assert.Equal(0, written.UnmappedSpares);

        // Still a model.
        var src = SecondSkinWriter.Parse(written.Model);
        Assert.Equal(1, src.MeshCount);

        // Every vertex moved by its own node's displacement, duplicates included — vertex 3 is a welded copy
        // of 0 and vertex 4 of 2, and a copy left behind is a crack down a UV seam.
        for (int v = 0; v < 6; v++)
        {
            float expected = PositionY(mdl, v) + solve.DeltaAt(v).Y;
            Assert.Equal(expected, PositionY(written.Model, v), 1e-6f);
        }
        Assert.Equal(PositionY(written.Model, 0), PositionY(written.Model, 3), 1e-9f);
        Assert.Equal(PositionY(written.Model, 2), PositionY(written.Model, 4), 1e-9f);

        // Nothing outside the ranges this is allowed to touch.
        int mo = src.MeshStart;
        ushort vc = BitConverter.ToUInt16(written.Model, mo);
        byte stride = written.Model[mo + 32];
        uint vbo = BitConverter.ToUInt32(written.Model, mo + 20);
        int vbLo = src.Vb + (int)vbo, vbHi = vbLo + vc * stride;

        var stray = new List<int>();
        for (int i = 0; i < mdl.Length; i++)
        {
            if (mdl[i] == written.Model[i]) continue;
            bool allowed = (i >= vbLo && i < vbHi)
                        || (i >= src.ModelBBoxAt && i < src.ModelBBoxAt + 4 * 32)
                        || (i >= src.BoneBBoxAt && i < src.BoneBBoxAt + src.BoneCount * 32)
                        || (i >= src.Mh && i < src.Mh + 4)          // Radius
                        || (i >= src.Mh + 28 && i < src.Mh + 36);   // model and shadow clip distances
            if (!allowed) stray.Add(i);
        }
        Assert.Empty(stray);
    }

    /// <summary>
    /// A moved part is written as far as it was moved — past the brushes' ceiling — and the extents grow by all of it.
    /// </summary>
    [Fact]
    public void WritesAMovedPartBeyondTheBrushCap()
    {
        var (mdl, solve, model) = Setup();

        Assert.True(solve.BeginMove(model.Parts[0].Triangles, adjacent: false, falloffRadius: 0f) > 0);
        solve.MoveTo(new Vector3(0f, 0.3f, 0f));
        solve.EndMove();
        Assert.Equal(0.3f, solve.Worst, 1e-6f);

        var before = SecondSkinWriter.Parse(mdl);
        BitConverter.GetBytes(2f).CopyTo(mdl, before.Mh);   // Radius, so there is something to grow

        var written = MeshVolumeService.Inflate(mdl, solve).Model;
        Assert.Equal(mdl.Length, written.Length);
        for (int v = 0; v < 6; v++)
            Assert.Equal(PositionY(mdl, v) + 0.3f, PositionY(written, v), 1e-5f);
        Assert.Equal(2.3f, BitConverter.ToSingle(written, SecondSkinWriter.Parse(written).Mh), 1e-5f);
    }

    /// <summary>
    /// The stored extents grow with the geometry.
    /// <para/>
    /// Nothing in this project ever recomputed a bounding box, and an inflate is the edit that makes it
    /// matter: it moves geometry monotonically outward, which is exactly the direction that understates a
    /// box. Understating one makes the game cull the model while the body it belongs to is still on screen,
    /// with nothing in any log to say why.
    /// </summary>
    [Fact]
    public void GrowsTheStoredExtentsSoTheModelIsNotCulled()
    {
        var (mdl, solve, model) = Setup();

        // A bounding box the writer can widen, rather than the zeroed one the fixture ships. An all-zero box
        // is treated as unused, which is the right reading for the water and fog boxes on a character.
        var before = SecondSkinWriter.Parse(mdl);
        for (int box = 0; box < 2; box++)
        for (int c = 0; c < 3; c++)
        {
            BitConverter.GetBytes(-1f).CopyTo(mdl, before.ModelBBoxAt + box * 32 + c * 4);
            BitConverter.GetBytes(1f).CopyTo(mdl, before.ModelBBoxAt + box * 32 + 16 + c * 4);
        }
        BitConverter.GetBytes(2f).CopyTo(mdl, before.Mh);          // Radius
        BitConverter.GetBytes(50f).CopyTo(mdl, before.Mh + 28);    // ModelClipOutDistance
        BitConverter.GetBytes(30f).CopyTo(mdl, before.Mh + 32);    // ShadowClipOutDistance

        var centre = new Vector3(model.Positions[0], model.Positions[1], model.Positions[2]);
        solve.Paint(centre, 10f, 0.002f);
        solve.EndStroke();

        var o = MeshVolumeService.Inflate(mdl, solve).Model;
        var src = SecondSkinWriter.Parse(o);
        float by = solve.Worst;
        Assert.True(by > 0f);

        for (int box = 0; box < 2; box++)
        for (int c = 0; c < 3; c++)
        {
            Assert.Equal(-1f - by, BitConverter.ToSingle(o, src.ModelBBoxAt + box * 32 + c * 4), 1e-6f);
            Assert.Equal(1f + by, BitConverter.ToSingle(o, src.ModelBBoxAt + box * 32 + 16 + c * 4), 1e-6f);
        }
        Assert.Equal(2f + by, BitConverter.ToSingle(o, src.Mh), 1e-6f);
        Assert.Equal(50f + by, BitConverter.ToSingle(o, src.Mh + 28), 1e-6f);
        Assert.Equal(30f + by, BitConverter.ToSingle(o, src.Mh + 32), 1e-6f);

        // The boxes the fixture left at zero stay at zero: turning an unused box into a tiny one around the
        // origin is inventing a volume rather than widening one.
        for (int box = 2; box < 4; box++)
        for (int c = 0; c < 8; c++)
            Assert.Equal(0f, BitConverter.ToSingle(o, src.ModelBBoxAt + box * 32 + c * 4));
    }

    /// <summary>
    /// A shape key's replacement vertex moves exactly once, and by its BASE vertex's displacement.
    /// <para/>
    /// A shape does not store offsets — each value rewires one index slot to a whole spare vertex carrying
    /// the shape's target pose. Left behind, enabling that shape puts the slots it rewires back where the
    /// author had them, so turning on a body slider undoes the clipping fix along whatever it touches.
    /// <para/>
    /// This fixture names vertex 0 as both the base and the replacement, which is the case that catches a
    /// double write: the main pass and the spare pass would each add the displacement and move it twice as
    /// far as everything around it. Hence spares being excluded from the main pass rather than merely
    /// handled afterwards.
    /// </summary>
    [Fact]
    public void CarriesAShapesSpareVertexExactlyOnce()   // [AC-spare-carry-append-worn-1] [EDGE-spare-carry-append-worn-4]
    {
        var (mdl, solve, model) = Setup(["shp_test"]);

        var centre = new Vector3(model.Positions[0], model.Positions[1], model.Positions[2]);
        solve.Paint(centre, 10f, 0.002f);
        solve.EndStroke();

        var written = MeshVolumeService.Inflate(mdl, solve);
        Assert.Equal(0, written.UnmappedSpares);

        // Once, not twice: the fixture's shape names vertex 0 for both roles.
        float expected = PositionY(mdl, 0) + solve.DeltaAt(0).Y;
        Assert.Equal(expected, PositionY(written.Model, 0), 1e-6f);
    }

    // ── shape spares named by several index slots ───────────────────────────────────────────────────────────────
    //
    // Real exporters make one spare per base vertex and name it from EVERY index slot that vertex fills, so a spare
    // is named as many times as its vertex has triangles. Each spare must still move once, by its base's delta.

    /// <summary>
    /// An edit whose every vertex moves by a function of where it IS. Coincident vertices therefore share a delta,
    /// as they do under the real welded edits, while a spare sitting at its shape pose would get a value of its own —
    /// which is what tells "moved by its base" apart from "moved by itself".
    /// </summary>
    private sealed class FieldEdit : IMeshEdit
    {
        private readonly float[] positions;
        private readonly Func<Vector3, Vector3> field;

        public FieldEdit(IReadOnlyList<MeshSpan> spans, float[] positions, Func<Vector3, Vector3> field)
        {
            Spans = spans;
            this.positions = positions;
            this.field = field;
            for (int v = 0; v < positions.Length / 3; v++) Worst = MathF.Max(Worst, Field(v).Length());
        }

        private Vector3 Field(int v) => field(new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]));

        public IReadOnlyList<MeshSpan> Spans { get; }
        public float Worst { get; }
        public bool Dirty => true;
        public bool WindEdited => false;
        public SecondSkinWriter.Vec3 DeltaAt(int vertex) { var d = Field(vertex); return new(d.X, d.Y, d.Z); }
        public SecondSkinWriter.Vec3 NormalAt(int vertex) => new(0f, 0f, 0f);
        public float WindAt(int vertex) => 0f;
    }

    /// <summary>Mesh <paramref name="mesh"/>'s vertex <paramref name="vertex"/>, as stored.</summary>
    private static Vector3 PositionAt(byte[] mdl, int mesh, int vertex)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int mo = src.MeshStart + mesh * 36;
        var pe = src.Decls[mesh].First(e => e.Usage == SecondSkinWriter.UsePosition);
        uint vbo = BitConverter.ToUInt32(mdl, mo + 20 + pe.Stream * 4);
        byte stride = mdl[mo + 32 + pe.Stream];
        Span<float> tmp = stackalloc float[4];
        SecondSkinWriter.ReadTyped(mdl, src.Vb + (int)vbo + vertex * stride + pe.Offset, pe.Type, tmp);
        return new Vector3(tmp[0], tmp[1], tmp[2]);
    }

    /// <summary>
    /// Two triangles (six vertices; 0 and 3 share the hub position) plus one spare, vertex 6, raised above vertex 0.
    /// Slots 0 and 3 index vertices 0 and 3 — the fixture numbers every corner afresh.
    /// </summary>
    private static (byte[] Mdl, ModelParts Read) SpareModel(params SyntheticModel.Shape[] shapes)
    {
        var mdl = SyntheticModel.Build(
            [],
            [new SyntheticModel.Mesh("/mt_test.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 2)) { Spares = 1 }],
            SyntheticModel.V6, shapes);
        var read = ModelPartReader.Read(mdl);
        Assert.NotNull(read);
        Assert.Equal(7, read!.Positions.Length / 3);
        return (mdl, read);
    }

    /// <summary>A field that rises with height, so a spare above its base would be moved further by its own value.</summary>
    private static Vector3 Rising(Vector3 p) => new(0f, 0.01f + 0.1f * p.Y, 0f);

    /// <summary>
    /// A spare named from two slots moves once, by its base's delta — not twice, and not by its own.
    /// </summary>
    [Fact]
    public void A_spare_named_by_several_slots_moves_once_by_its_base()   // [AC-spare-carry-append-worn-1]
    {
        var (mdl, read) = SpareModel(new SyntheticModel.Shape("shp_a", 0, [(0, 6), (3, 6)]));
        var edit = new FieldEdit(read.MeshSpans, read.Positions, Rising);

        var written = MeshVolumeService.Inflate(mdl, edit);
        Assert.Equal(0, written.UnmappedSpares);

        var base0 = PositionAt(mdl, 0, 0);
        var spare0 = PositionAt(mdl, 0, 6);
        Assert.Equal(spare0.Y + edit.DeltaAt(0).Y, PositionAt(written.Model, 0, 6).Y, 1e-6f);

        // The shape's own offset survives: turning it on puts the slot where the author's shape put it, plus the edit.
        var offsetAfter = PositionAt(written.Model, 0, 6) - PositionAt(written.Model, 0, 0);
        Assert.Equal((spare0 - base0).Y, offsetAfter.Y, 1e-6f);
    }

    /// <summary>The refit's edit goes through the same writer, and carries each spare once.</summary>
    [Fact]
    public void A_retarget_carries_each_spare_once()   // [AC-spare-carry-append-worn-2]
    {
        var (mdl, read) = SpareModel(new SyntheticModel.Shape("shp_a", 0, [(0, 6), (3, 6), (0, 6)]));
        int n = read.Positions.Length / 3;
        var delta = new SecondSkinWriter.Vec3[n];
        for (int v = 0; v < n; v++)
        {
            var d = Rising(new Vector3(read.Positions[v * 3], read.Positions[v * 3 + 1], read.Positions[v * 3 + 2]));
            delta[v] = new(d.X, d.Y, d.Z);
        }
        var edit = new RetargetEdit(read.MeshSpans, delta, new SecondSkinWriter.Vec3[n]);

        var written = MeshVolumeService.Inflate(mdl, edit);
        Assert.Equal(PositionAt(mdl, 0, 6).Y + delta[0].Y, PositionAt(written.Model, 0, 6).Y, 1e-6f);
        Assert.Equal(mdl.Length, written.Model.Length);
    }

    /// <summary>The "points could not be moved" count is points, not the slots that name them.</summary>
    [Fact]
    public void Unmapped_counts_points_not_slots()   // [AC-spare-carry-append-worn-3]
    {
        var (mdl, read) = SpareModel(new SyntheticModel.Shape("shp_a", 0, [(0, 999), (1, 999), (2, 999)]));
        var written = MeshVolumeService.Inflate(mdl, new FieldEdit(read.MeshSpans, read.Positions, Rising));
        Assert.Equal(1, written.UnmappedSpares);
    }

    /// <summary>Two meshes, each with spare 6 named from slots 0 and 3, mesh 1 placed apart in X.</summary>
    private static (byte[] Mdl, ModelParts Read) TwoSpareMeshes(params SyntheticModel.Shape[] shapes)
    {
        var mdl = SyntheticModel.Build(
            [],
            [
                new SyntheticModel.Mesh("/mt_a.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 2)) { Spares = 1 },
                new SyntheticModel.Mesh("/mt_b.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 5f)) { Spares = 1 },
            ],
            SyntheticModel.V6, shapes);
        return (mdl, ModelPartReader.Read(mdl)!);
    }

    /// <summary>
    /// A shaped mesh the edit does not cover cannot be carried: its spare is counted once, however many slots name it.
    /// </summary>
    [Fact]
    public void A_spare_in_a_mesh_the_edit_leaves_out_counts_once()   // [AC-spare-carry-append-worn-3]
    {
        var (mdl, read) = TwoSpareMeshes(new SyntheticModel.Shape("shp_b", 1, [(0, 6), (3, 6)]));
        var edit = new FieldEdit([read.MeshSpans[0]], read.Positions, Rising);
        Assert.Equal(1, MeshVolumeService.Inflate(mdl, edit).UnmappedSpares);
    }

    /// <summary>
    /// The same local spare index missed in one mesh and carried in another: the carried one moves once, and the missed
    /// one is still counted — the key keeps the meshes apart.
    /// </summary>
    [Fact]
    public void A_missed_spare_is_not_hidden_by_the_same_index_carried_in_another_mesh()   // [AC-spare-carry-append-worn-3]
    {
        var (mdl, read) = TwoSpareMeshes(new SyntheticModel.Shape("shp_a", 0, [(0, 6), (3, 6)]),
                                         new SyntheticModel.Shape("shp_b", 1, [(0, 6), (3, 6)]));
        var edit = new FieldEdit([read.MeshSpans[0]], read.Positions, Rising);

        var written = MeshVolumeService.Inflate(mdl, edit);
        Assert.Equal(1, written.UnmappedSpares);
        Assert.Equal(PositionAt(mdl, 0, 6).Y + edit.DeltaAt(0).Y, PositionAt(written.Model, 0, 6).Y, 1e-6f);
    }

    /// <summary>An entry no mesh owns: its values name one spare, so one point is lost, not two.</summary>
    [Fact]
    public void An_entry_no_mesh_owns_counts_its_spares_once()   // [AC-spare-carry-append-worn-3]
    {
        var (mdl, read) = SpareModel(new SyntheticModel.Shape("shp_a", 0, [(0, 6), (3, 6)], IndexOffset: 100000));
        Assert.Equal(1, MeshVolumeService.Inflate(mdl, new FieldEdit(read.MeshSpans, read.Positions, Rising)).UnmappedSpares);
    }

    /// <summary>One spare named by two different shapes still moves once.</summary>
    [Fact]
    public void A_spare_shared_by_two_shapes_moves_once()   // [EDGE-spare-carry-append-worn-1]
    {
        var (mdl, read) = SpareModel(new SyntheticModel.Shape("shp_a", 0, [(0, 6)]),
                                     new SyntheticModel.Shape("shp_b", 0, [(3, 6)]));
        var edit = new FieldEdit(read.MeshSpans, read.Positions, Rising);
        var written = MeshVolumeService.Inflate(mdl, edit);
        Assert.Equal(PositionAt(mdl, 0, 6).Y + edit.DeltaAt(0).Y, PositionAt(written.Model, 0, 6).Y, 1e-6f);
    }

    /// <summary>The same local spare index in two meshes is two spares, each carried by its own mesh's base.</summary>
    [Fact]
    public void Same_spare_index_in_two_meshes_each_carried_by_its_own_base()   // [EDGE-spare-carry-append-worn-2]
    {
        var mdl = SyntheticModel.Build(
            [],
            [
                new SyntheticModel.Mesh("/mt_a.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 2)) { Spares = 1 },
                new SyntheticModel.Mesh("/mt_b.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 5f)) { Spares = 1 },
            ],
            SyntheticModel.V6,
            [new SyntheticModel.Shape("shp_a", 0, [(0, 6), (3, 6)]), new SyntheticModel.Shape("shp_b", 1, [(0, 6), (3, 6)])]);
        var read = ModelPartReader.Read(mdl)!;
        var edit = new FieldEdit(read.MeshSpans, read.Positions, p => new Vector3(0f, 0.01f + 0.01f * p.X + 0.1f * p.Y, 0f));

        var written = MeshVolumeService.Inflate(mdl, edit);
        Assert.Equal(0, written.UnmappedSpares);
        foreach (var span in read.MeshSpans)
            Assert.Equal(PositionAt(mdl, span.Mesh, 6).Y + edit.DeltaAt(span.BaseVertex).Y,
                         PositionAt(written.Model, span.Mesh, 6).Y, 1e-6f);
        Assert.NotEqual(edit.DeltaAt(read.MeshSpans[0].BaseVertex).Y, edit.DeltaAt(read.MeshSpans[1].BaseVertex).Y);
    }

    /// <summary>Bases that are seam duplicates of one point: once, by the first, which is also what the others say.</summary>
    [Fact]
    public void Spare_of_coincident_bases_moves_by_the_first()   // [EDGE-spare-carry-append-worn-3]
    {
        var (mdl, read) = SpareModel(new SyntheticModel.Shape("shp_a", 0, [(3, 6), (0, 6)]));
        var edit = new FieldEdit(read.MeshSpans, read.Positions, Rising);
        Assert.Equal(edit.DeltaAt(0).Y, edit.DeltaAt(3).Y, 1e-9f);

        var written = MeshVolumeService.Inflate(mdl, edit);
        Assert.Equal(PositionAt(mdl, 0, 6).Y + edit.DeltaAt(3).Y, PositionAt(written.Model, 0, 6).Y, 1e-6f);
    }

    /// <summary>A slot that cannot be resolved does not stop another slot carrying the same spare, nor count it lost.</summary>
    [Fact]
    public void A_spare_one_slot_misses_is_still_carried()   // [EDGE-spare-carry-append-worn-5]
    {
        var (mdl, read) = SpareModel(new SyntheticModel.Shape("shp_a", 0, [(60000, 6), (0, 6)]));
        var edit = new FieldEdit(read.MeshSpans, read.Positions, Rising);

        var written = MeshVolumeService.Inflate(mdl, edit);
        Assert.Equal(0, written.UnmappedSpares);
        Assert.Equal(PositionAt(mdl, 0, 6).Y + edit.DeltaAt(0).Y, PositionAt(written.Model, 0, 6).Y, 1e-6f);
    }

    /// <summary>
    /// A spare whose first slot's base did not move stays where it is — and is not then moved by a later slot whose
    /// base did. Vertex 0 sits at the origin, where this field is zero; vertex 1 does not.
    /// </summary>
    [Fact]
    public void A_spare_whose_base_did_not_move_stays_put()   // [EDGE-spare-carry-append-worn-6]
    {
        var (mdl, read) = SpareModel(new SyntheticModel.Shape("shp_a", 0, [(0, 6), (1, 6)]));
        var edit = new FieldEdit(read.MeshSpans, read.Positions, p => new Vector3(0f, 0.01f * p.X + 0.1f * p.Y, 0f));
        Assert.Equal(0f, edit.DeltaAt(0).Y);
        Assert.NotEqual(0f, edit.DeltaAt(1).Y);

        var written = MeshVolumeService.Inflate(mdl, edit);
        Assert.Equal(PositionAt(mdl, 0, 6), PositionAt(written.Model, 0, 6));
    }

    /// <summary>A mesh with no shape of its own, in a model that has shapes, moves every vertex by its own delta.</summary>
    [Fact]
    public void A_shape_free_mesh_moves_by_its_own_delta()   // [EDGE-spare-carry-append-worn-7]
    {
        var mdl = SyntheticModel.Build(
            [],
            [
                new SyntheticModel.Mesh("/mt_a.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 2)) { Spares = 1 },
                new SyntheticModel.Mesh("/mt_b.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 5f)),
            ],
            SyntheticModel.V6,
            [new SyntheticModel.Shape("shp_a", 0, [(0, 6), (3, 6)])]);
        var read = ModelPartReader.Read(mdl)!;
        var written = MeshVolumeService.Inflate(mdl, new FieldEdit(read.MeshSpans, read.Positions, _ => new Vector3(0f, 0.01f, 0f)));

        var mesh1 = read.MeshSpans[1];
        for (int v = 0; v < mesh1.Count; v++)
            Assert.Equal(PositionAt(mdl, 1, v).Y + 0.01f, PositionAt(written.Model, 1, v).Y, 1e-6f);
    }

    /// <summary>The racial bake moves every vertex by its own delta, spares too — and leaves nothing "unmapped".</summary>
    [Fact]
    public void Without_carrying_every_vertex_moves_by_its_own_delta()   // [EDGE-spare-carry-append-worn-8]
    {
        var (mdl, read) = SpareModel(new SyntheticModel.Shape("shp_a", 0, [(0, 6), (3, 6)]));
        var edit = new FieldEdit(read.MeshSpans, read.Positions, Rising);

        var written = MeshVolumeService.Inflate(mdl, edit, carrySpares: false);
        Assert.Equal(0, written.UnmappedSpares);
        Assert.Equal(PositionAt(mdl, 0, 6).Y + edit.DeltaAt(6).Y, PositionAt(written.Model, 0, 6).Y, 1e-6f);
    }

    private const string PlugModel = LocalData.Mods + ":Butt Plug - True Colors/Plog/sized/default_fit.mdl";

    /// <summary>
    /// The model the tearing was found on: 16 size shapes over its three meshes, written by an exporter that
    /// names each spare from every slot its base fills. A uniform move must move every vertex by exactly that much,
    /// so every shape keeps its own offset from the base.
    /// </summary>
    [LocalDataFact(PlugModel)]
    public void Real_shape_keyed_model_keeps_every_shape_offset()   // [EDGE-spare-carry-append-worn-9]
    {
        var mdl = System.IO.File.ReadAllBytes(LocalData.Path(PlugModel));
        var src = SecondSkinWriter.Parse(mdl);
        var read = ModelPartReader.Read(mdl)!;
        Assert.Equal(3, read.MeshSpans.Count);

        // Which spare each slot names, per mesh — and the precondition that makes this test able to fail: a spare
        // named more than once.
        var meshes = read.MeshSpans
            .Select(s => (s.Mesh, Start: BitConverter.ToUInt32(mdl, src.MeshStart + s.Mesh * 36 + 16),
                          Count: BitConverter.ToUInt32(mdl, src.MeshStart + s.Mesh * 36 + 4)))
            .ToList();
        var named = new List<(int Mesh, int Base, int Spare)>();
        foreach (var entry in src.Shapes.Values.SelectMany(e => e))
        {
            var owner = meshes.First(m => entry.MeshIndexOffset >= m.Start && entry.MeshIndexOffset < m.Start + m.Count);
            foreach (var (b, replace) in entry.Values)
            {
                int baseVertex = BitConverter.ToUInt16(mdl, src.Ib + (int)(entry.MeshIndexOffset + b) * 2);
                named.Add((owner.Mesh, baseVertex, replace));
            }
        }
        Assert.Contains(named.GroupBy(x => (x.Mesh, x.Spare)), g => g.Count() >= 2);

        // The assumption "first slot wins" rests on: every slot naming a spare stands on the same point.
        foreach (var g in named.GroupBy(x => (x.Mesh, x.Spare)))
        {
            var first = PositionAt(mdl, g.Key.Mesh, g.First().Base);
            foreach (var x in g)
                Assert.True(Vector3.Distance(first, PositionAt(mdl, x.Mesh, x.Base)) < 1e-5f,
                            $"spare {x.Spare} of mesh {x.Mesh} is named from bases at different points");
        }

        var lift = new Vector3(0f, 0.01f, 0f);
        var written = MeshVolumeService.Inflate(mdl, new FieldEdit(read.MeshSpans, read.Positions, _ => lift));

        foreach (var span in read.MeshSpans)
        for (int v = 0; v < span.Count; v++)
        {
            float moved = PositionAt(written.Model, span.Mesh, v).Y - PositionAt(mdl, span.Mesh, v).Y;
            Assert.True(MathF.Abs(moved - 0.01f) < 1e-4f, $"mesh {span.Mesh} vertex {v} moved {moved * 1000f:F2} mm, not 10");
        }
        foreach (var (mesh, b, spare) in named)
        {
            var before = PositionAt(mdl, mesh, spare) - PositionAt(mdl, mesh, b);
            var after = PositionAt(written.Model, mesh, spare) - PositionAt(written.Model, mesh, b);
            Assert.True(Vector3.Distance(before, after) < 1e-4f, $"mesh {mesh} spare {spare} lost its shape offset");
        }
    }

    /// <summary>
    /// A position format the writer cannot express is refused rather than silently doing nothing.
    /// <para/>
    /// <c>WriteXYZ</c> has no default arm: handed a type it does not know it writes nothing and returns, so
    /// the edit would be structurally perfect and move the model nowhere — which is indistinguishable from
    /// the brush having decided the geometry was fine.
    /// </summary>
    [Fact]
    public void RefusesAPositionFormatItCannotWrite()
    {
        var (mdl, solve, model) = Setup();

        var centre = new Vector3(model.Positions[0], model.Positions[1], model.Positions[2]);
        solve.Paint(centre, 10f, 0.002f);
        solve.EndStroke();

        // Retype mesh 0's position element to Half2, which WriteXYZ knows but cannot hold three components
        // of — and which ModelAttributeWriter.PositionTypes therefore excludes.
        // The declaration blocks run from 0x44 to DeclEnd, eight bytes an entry:
        // stream, offset, type, usage, usageIndex, then padding.
        var src = SecondSkinWriter.Parse(mdl);
        bool retyped = false;
        for (int i = 0x44; i + 5 < src.DeclEnd && !retyped; i += 8)
            if (mdl[i] != 0xFF && mdl[i + 3] == SecondSkinWriter.UsePosition)
            { mdl[i + 2] = 13; retyped = true; }
        Assert.True(retyped, "the fixture's position element was not found");

        var ex = Assert.Throws<ModelAttributeWriter.ModelEditException>(
            () => MeshVolumeService.Inflate(mdl, solve));
        Assert.Contains("format", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Painted wind is saved into the model: the file gains the wind channel it did not have, reading it back
    /// gives the painted amount on every vertex, and the geometry is exactly what it was.
    /// </summary>
    [Fact]
    public void PaintedWindIsSavedIntoAnAddedChannel()
    {
        var (mdl, solve, model) = Setup();
        Assert.False(ModelPartReader.Read(mdl)!.HasWindChannel);

        var centre = new Vector3(model.Positions[0], model.Positions[1], model.Positions[2]);
        Assert.True(solve.PaintWind(centre, 10f, 0.8f, 1f) > 0);
        solve.EndStroke(wind: true);
        Assert.True(solve.WindEdited);

        var written = MeshVolumeService.Inflate(mdl, solve);
        Assert.NotNull(written.WindChannel);
        Assert.Equal(1, written.WindChannel!.Value.MeshesAdded);

        var back = ModelPartReader.Read(written.Model)!;
        Assert.True(back.HasWindChannel);
        Assert.Equal(ModelPartReader.Read(mdl)!.Positions, back.Positions);
        for (int v = 0; v < back.Wind.Length; v++)
            Assert.Equal(solve.WindAt(v), back.Wind[v], 1f / 255f);

        // Opened again, the painted wind is what the brush starts from.
        var reopened = new MeshVolumeSolve(back);
        Assert.False(reopened.WindEdited);
        Assert.True(reopened.HasWindChannel);
        Assert.Equal(solve.WindAt(0), reopened.WindAt(0), 1f / 255f);
    }

    /// <summary>A model only pulled — no wind painted — keeps its vertex layout exactly as the author shipped it.</summary>
    [Fact]
    public void UnpaintedWindAddsNoChannel()
    {
        var (mdl, solve, model) = Setup();
        var centre = new Vector3(model.Positions[0], model.Positions[1], model.Positions[2]);
        solve.Paint(centre, 10f, 0.002f);
        solve.EndStroke();

        var written = MeshVolumeService.Inflate(mdl, solve);
        Assert.Null(written.WindChannel);
        Assert.Equal(mdl.Length, written.Model.Length);
    }

    /// <summary>Nothing painted, nothing saved — and the refusal is not an error the user has to clear.</summary>
    [Fact]
    public void RefusesToSaveAnUntouchedModel()
    {
        var (_, solve, _) = Setup();
        var outcome = MeshVolumeService.Apply("no-such-root", "a.mdl", [], solve);
        Assert.False(outcome.Ok);
        Assert.Equal(0, outcome.FilesWritten);
    }
}
