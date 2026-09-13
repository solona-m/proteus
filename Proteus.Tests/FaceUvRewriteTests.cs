using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <see cref="SecondSkinWriter.RewriteFaceUv0"/> — moving a FACE model's uv0 into the doubled sheet layout,
/// in place, so the character's own face samples an un-mirrored texture and asymmetric makeup survives.
/// <para/>
/// The alternative it replaces is a second-skin shell, which cannot carry a face: a shell is emitted with
/// its shape block zeroed, so it is a frozen duplicate of the head riding a millimetre proud of a face that
/// is still blinking underneath.
/// <para/>
/// These assert the BYTE behaviour, which is the half no in-game look can check: that nothing but uv0 moves,
/// that every LOD is converted or none is, and that anything the walk cannot vouch for refuses outright
/// rather than leaving a half-converted face.
/// </summary>
public class FaceUvRewriteTests
{
    private const string FaceMat = "mt_c1401f0001_fac_a.mtrl";
    private const string OtherMat = "mt_c1401f0001_etc_a.mtrl";

    /// <summary>SyntheticModel's vertex layout: position float3 at 0, uv0 float2 at 12.</summary>
    private const int Stride = 20, UvAt = 12;

    private static UVRemapService.UvConversion Convert()
        => new UVRemapService(Substitute.For<IPluginLog>(), ".")
            .UvConverter(UVRemapService.FaceSpace, UVRemapService.FaceSplitSpace, unmirror: true)!;

    private static Func<string, bool> KeepFace() => n => n.TrimStart('/').Equals(FaceMat, StringComparison.OrdinalIgnoreCase);

    private static int VertexBase(byte[] mdl) => (int)BitConverter.ToUInt32(mdl, 16);
    private static int VertexCount(byte[] mdl) => (int)BitConverter.ToUInt32(mdl, 40) / Stride;
    private static float X(byte[] mdl, int i) => BitConverter.ToSingle(mdl, VertexBase(mdl) + i * Stride);
    private static float U(byte[] mdl, int i) => BitConverter.ToSingle(mdl, VertexBase(mdl) + i * Stride + UvAt);
    private static float V(byte[] mdl, int i) => BitConverter.ToSingle(mdl, VertexBase(mdl) + i * Stride + UvAt + 4);

    private static void SetX(byte[] mdl, int i, float x)
        => BitConverter.GetBytes(x).CopyTo(mdl, VertexBase(mdl) + i * Stride);
    private static void SetUv(byte[] mdl, int i, float u, float v)
    {
        BitConverter.GetBytes(u).CopyTo(mdl, VertexBase(mdl) + i * Stride + UvAt);
        BitConverter.GetBytes(v).CopyTo(mdl, VertexBase(mdl) + i * Stride + UvAt + 4);
    }

    /// <summary>
    /// One face mesh of two triangles: the first on the +X side, the second mirrored onto -X, both painted
    /// at the same UV — which is exactly what a mirrored face layout means and what the rewrite has to undo.
    /// </summary>
    private static byte[] MirroredFace(float u = 0.3f, float v = 0.6f)
    {
        var mdl = SyntheticModel.Build([], new SyntheticModel.Mesh(FaceMat, new SyntheticModel.Sub(0, Islands: 2)));
        for (int i = 0; i < 6; i++)
        {
            // Triangle 0 (vertices 0-2) keeps its +X positions; triangle 1 (3-5) is its mirror.
            if (i >= 3) SetX(mdl, i, -X(mdl, i - 3));
            SetUv(mdl, i, u, v);
        }
        return mdl;
    }

    [Fact]
    public void The_two_sides_land_in_opposite_halves()
    {
        var mdl = MirroredFace();
        var got = SecondSkinWriter.RewriteFaceUv0(mdl, KeepFace(), Convert(), out var stats);

        Assert.NotNull(got);
        Assert.Equal(6, stats.VerticesWritten);
        Assert.Equal(1, stats.MeshesTouched);
        Assert.Equal(1, stats.LodsTouched);
        Assert.Equal(0, stats.Unsided);

        for (int i = 0; i < 3; i++)
        {
            Assert.True(U(got!, i) >= 0.5f, $"+X vertex {i} landed at {U(got!, i)}");
            Assert.True(U(got!, i + 3) <= 0.5f, $"-X vertex {i + 3} landed at {U(got!, i + 3)}");
            // Reflections about the middle of the sheet — the property that gives a one-sided mark
            // somewhere to live.
            Assert.Equal(1f, U(got!, i) + U(got!, i + 3), 5);
            Assert.Equal(0.6f, V(got!, i), 5);   // v is never touched
        }
    }

    /// <summary>
    /// The strongest single guarantee here: the file is the same length and every byte that moved is inside
    /// a uv0 field. Positions, normals, indices, every table and the whole header are untouched, which is
    /// what makes this safe to publish as the character's own face.
    /// </summary>
    [Fact]
    public void Only_uv0_bytes_move_and_the_length_is_unchanged()
    {
        var mdl = MirroredFace();
        var got = SecondSkinWriter.RewriteFaceUv0(mdl, KeepFace(), Convert(), out _)!;

        Assert.Equal(mdl.Length, got.Length);

        int vb = VertexBase(mdl), vc = VertexCount(mdl);
        for (int i = 0; i < mdl.Length; i++)
        {
            if (mdl[i] == got[i]) continue;
            int rel = i - vb;
            Assert.True(rel >= 0 && rel < vc * Stride, $"byte {i} changed outside the vertex buffer");
            int within = rel % Stride;
            Assert.True(within >= UvAt && within < UvAt + 8,
                $"byte {i} changed at offset {within} in its vertex — outside uv0");
        }
    }

    /// <summary>
    /// One model can carry more than one face material, and a rewrite converts only the meshes its filter
    /// names. So when two of them are doubled the filter has to cover BOTH — handling the second by reusing
    /// the first's rewrite would mark it doubled while its meshes still held vanilla UVs, and those meshes
    /// would then sample the wrong half of their own sheet.
    /// </summary>
    [Fact]
    public void A_filter_naming_two_materials_converts_both()
    {
        var mdl = SyntheticModel.Build([],
            new SyntheticModel.Mesh(FaceMat, new SyntheticModel.Sub(0)),
            new SyntheticModel.Mesh(OtherMat, new SyntheticModel.Sub(0)));
        for (int i = 0; i < 6; i++) SetUv(mdl, i, 0.4f, 0.2f);

        var leaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FaceMat, OtherMat };
        var got = SecondSkinWriter.RewriteFaceUv0(mdl, SecondSkinWriter.KeepByLeaf(leaves), Convert(),
                                                  out var stats)!;

        Assert.Equal(2, stats.MeshesTouched);
        Assert.Equal(6, stats.VerticesWritten);
        for (int i = 0; i < 6; i++) Assert.NotEqual(0.4f, U(got, i));
    }

    /// <summary>A face model draws more than the face — eyes, lashes, brows. Only the targeted material's
    /// meshes may move, or the eyes would sample the doubled sheet with the face's coordinates.</summary>
    [Fact]
    public void Meshes_of_other_materials_are_left_alone()
    {
        var mdl = SyntheticModel.Build([],
            new SyntheticModel.Mesh(FaceMat, new SyntheticModel.Sub(0)),
            new SyntheticModel.Mesh(OtherMat, new SyntheticModel.Sub(0)));
        for (int i = 0; i < 6; i++) SetUv(mdl, i, 0.4f, 0.2f);

        var got = SecondSkinWriter.RewriteFaceUv0(mdl, KeepFace(), Convert(), out var stats)!;

        Assert.Equal(1, stats.MeshesTouched);
        Assert.Equal(3, stats.VerticesWritten);
        for (int i = 0; i < 3; i++) Assert.NotEqual(0.4f, U(got, i));          // the face mesh moved
        for (int i = 3; i < 6; i++) Assert.Equal(0.4f, U(got, i), 5);          // the other one did not
    }

    /// <summary>
    /// Every LOD, or none. A face that keeps vanilla UVs at LOD1 samples the doubled sheet with the wrong
    /// coordinates the moment the camera pulls back — and that is invisible in the mirror, where a character
    /// is always at LOD0.
    /// </summary>
    [Fact]
    public void Every_lod_is_converted()
    {
        var mdl = SyntheticModel.Build([],
            new SyntheticModel.Mesh(FaceMat, new SyntheticModel.Sub(0)),
            new SyntheticModel.Mesh(FaceMat, new SyntheticModel.Sub(0)));
        for (int i = 0; i < 6; i++) SetUv(mdl, i, 0.4f, 0.2f);

        // Hand mesh 1 to LOD1, sharing LOD0's buffers: mesh offsets are relative to their LOD's own base,
        // and both LODs' bases are the same buffer here, so every offset still resolves.
        int lodStart = LodStart(mdl);
        Write16(mdl, lodStart + 2, 1);                                   // LOD0: mesh 0 only
        Write16(mdl, lodStart + 60, 1);                                  // LOD1: mesh index 1
        Write16(mdl, lodStart + 62, 1);                                  // LOD1: one mesh
        Write32(mdl, 16 + 4, BitConverter.ToUInt32(mdl, 16));            // vertexOffset[1] = [0]
        Write32(mdl, 28 + 4, BitConverter.ToUInt32(mdl, 28));            // indexOffset[1]  = [0]
        Write32(mdl, 40 + 4, BitConverter.ToUInt32(mdl, 40));            // vertexBufferSize[1]

        var got = SecondSkinWriter.RewriteFaceUv0(mdl, KeepFace(), Convert(), out var stats)!;

        Assert.Equal(2, stats.LodsTouched);
        Assert.Equal(2, stats.MeshesTouched);
        Assert.Equal(6, stats.VerticesWritten);
        for (int i = 0; i < 6; i++) Assert.NotEqual(0.4f, U(got, i));
    }

    /// <summary>No matching mesh is not a failure — it is a model this overlay does not paint. The caller
    /// falls back to folding the sheet, so it must be able to tell that from a rewrite.</summary>
    [Fact]
    public void No_matching_mesh_returns_null()
    {
        var mdl = SyntheticModel.Build([], new SyntheticModel.Mesh(OtherMat, new SyntheticModel.Sub(0)));
        Assert.Null(SecondSkinWriter.RewriteFaceUv0(mdl, KeepFace(), Convert(), out var stats));
        Assert.Equal(0, stats.VerticesWritten);
    }

    /// <summary>
    /// A uv0 type the writer cannot encode refuses the WHOLE model, not just that mesh. Half a converted
    /// face is worse than none: the converted half would sample the doubled sheet while the rest reads it
    /// at vanilla coordinates.
    /// </summary>
    [Fact]
    public void An_unwritable_uv_type_refuses_the_model()
    {
        var mdl = MirroredFace();
        // Declaration slot 1 is uv0 (see SyntheticModel); byte 2 of an element is its type. 9 = Short2n,
        // which ReadTyped decodes and WriteUv0 will not write.
        mdl[0x44 + 1 * 8 + 2] = 9;
        Assert.Null(SecondSkinWriter.RewriteFaceUv0(mdl, KeepFace(), Convert(), out _));
    }

    /// <summary>
    /// A vertex buffer too short for the elements it declares refuses the whole model. The bound is the
    /// element's OWN width — a blanket would refuse an ordinary tightly packed buffer, whose last element
    /// ends exactly where the buffer does, and the fixture below is exactly that shape one byte short.
    /// </summary>
    [Fact]
    public void A_vertex_buffer_too_short_for_its_elements_refuses_the_model()
    {
        var whole = MirroredFace();
        Assert.NotNull(SecondSkinWriter.RewriteFaceUv0(whole, KeepFace(), Convert(), out _));

        var truncated = MirroredFace();
        // vertexBufferSize[0] at 0x28: one byte less than the last uv0 needs.
        Write32(truncated, 40, (uint)(VertexCount(truncated) * Stride - 1));
        Assert.Null(SecondSkinWriter.RewriteFaceUv0(truncated, KeepFace(), Convert(), out _));
    }

    /// <summary>
    /// The affine reads u as a position in the [0,1] sheet, so a mesh tiled outside it would be sent
    /// somewhere this has no business sending the player's own face. Refused, and the sheet is folded
    /// instead.
    /// </summary>
    [Fact]
    public void A_mesh_outside_the_uv_tile_refuses_the_model()
    {
        var mdl = MirroredFace();
        SetUv(mdl, 2, 3.25f, 0.5f);
        Assert.Null(SecondSkinWriter.RewriteFaceUv0(mdl, KeepFace(), Convert(), out _));
    }

    /// <summary>
    /// A vertex whose triangles disagree about its side keeps the documented +X default rather than being
    /// overruled, and a vertex NO triangle names — a morph replacement, which is how every facial expression
    /// is delivered — is placed by its own X instead of silently taking that default.
    /// </summary>
    [Fact]
    public void A_vertex_no_triangle_names_is_placed_by_its_own_x()
    {
        var mdl = MirroredFace();
        // Vertices 3-5 are the -X triangle. Move the LAST one somewhere no triangle references it: the
        // index buffer still names it, so instead make a NEW unreferenced vertex by pointing the triangle's
        // third slot at vertex 3 and leaving 5 orphaned on the -X side.
        int ib = (int)BitConverter.ToUInt32(mdl, 28);
        Write16(mdl, ib + 5 * 2, 3);

        var got = SecondSkinWriter.RewriteFaceUv0(mdl, KeepFace(), Convert(), out var stats)!;

        Assert.Equal(0, stats.Unsided);
        Assert.True(X(mdl, 5) < 0f);
        Assert.True(U(got, 5) <= 0.5f, $"an orphaned -X vertex landed at {U(got, 5)}");
    }

    private static int LodStart(byte[] mdl)
    {
        // Mirrors Parse: declarations, then the string block, then the 56-byte model header, then the LODs.
        int declCount = BitConverter.ToUInt16(mdl, 12);
        int declEnd = 0x44 + declCount * 17 * 8;
        uint strSize = BitConverter.ToUInt32(mdl, declEnd + 4);
        int mh = declEnd + 8 + (int)strSize;
        return mh + 56 + BitConverter.ToUInt16(mdl, mh + 24) * 32;   // elemCount * 32
    }

    private static void Write16(byte[] a, int at, ushort v) => BitConverter.GetBytes(v).CopyTo(a, at);
    private static void Write32(byte[] a, int at, uint v) => BitConverter.GetBytes(v).CopyTo(a, at);
}
