using System;
using System.Collections.Generic;

namespace Proteus.Services;

using static Proteus.Services.SecondSkinWriter;

internal static partial class FaceUvRewriter
{
    /// <summary>What <see cref="RewriteFaceUv0"/> actually did, for the log line. <paramref name="Unsided"/> is
    /// the one to watch: on a real face it is 0.</summary>
    internal readonly record struct FaceUvStats(int MeshesTouched, int VerticesWritten, int LodsTouched,
                                                int Conflicted, int Straddling, int MorphSided, int Unsided);

    /// <summary>
    /// Rewrite a face model's uv0 into the doubled (<see cref="UVRemapService.FaceSplitSpace"/>) layout, in
    /// place, so the character's own face samples an un-mirrored sheet: the +X side into the right half,
    /// the -X side into the left. Returns a modified copy, or null when nothing qualified.
    /// A shell cannot carry a whole face: it has no shape keys, so it cannot blink or talk.
    /// In place and length-neutral: uv0 is overwritten where it sits, in the declared type; uv1 shares the
    /// packed lanes and is never touched. Tangents are left alone: the doubled sheet's left half is a pixel
    /// mirror of its right, so an unchanged frame renders symmetric art bit-for-bit as before.
    /// All-or-nothing: every LOD is rewritten or none is, and anything this cannot vouch for returns null,
    /// and the caller falls back to folding the sheet.
    /// </summary>
    /// <param name="keepMaterial">Which meshes to convert, by material name: the face material only.</param>
    /// <param name="convert">
    /// <c>UvConverter(FaceSpace, FaceSplitSpace, unmirror: true)</c>, taken rather than built so the one
    /// affine the shell path uses is the one applied here.
    /// </param>
    internal static byte[]? RewriteFaceUv0(byte[] mdl, Func<string, bool> keepMaterial,
                                           UVRemapService.UvConversion convert,
                                           out FaceUvStats stats, Action<string>? log = null)
    {
        stats = default;
        if (mdl is not { Length: > 0 }) return null;

        Source src;
        try { src = Parse(mdl); }
        catch { return null; }

        var s = src.S;
        uint U32(int o) => BitConverter.ToUInt32(s, o);
        ushort U16(int o) => BitConverter.ToUInt16(s, o);

        // Each LOD's buffers come from the file header's own per-LOD arrays (vertexOffset[3] at 0x10,
        // indexOffset[3] at 0x1C); Parse reads only [0] as Vb/Ib.
        int VertexBase(int l) => (int)U32(16 + l * 4);
        int IndexBase(int l) => (int)U32(28 + l * 4);
        long VertexBytes(int l) => U32(0x28 + l * 4);

        // Cross-checked against the one value Parse derived independently; if they disagree every write
        // below lands in someone else's bytes.
        if (mdl.Length < 0x44 || VertexBase(0) != src.Vb || IndexBase(0) != src.Ib) return null;

        var matNames = ReadMaterialNames(s, src);
        var outBytes = (byte[])mdl.Clone();
        // A malformed LOD range that repeats LOD0's meshes would otherwise convert the same vertices twice.
        var done = new HashSet<int>();
        int meshes = 0, written = 0, lods = 0, conflicted = 0, straddled = 0, morphSided = 0, unsided = 0;
        Span<float> tmp = stackalloc float[4];

        for (int lod = 0; lod < 3; lod++)
        {
            int ls = src.LodStart + lod * 60;
            if (ls + 60 > s.Length) break;
            ushort mi = U16(ls), mc = U16(ls + 2);
            if (mc == 0) continue;
            int vbBase = VertexBase(lod), ibBase = IndexBase(lod);
            if (vbBase <= 0 || ibBase <= 0) continue;      // a LOD the file does not actually carry
            long vbBytes = VertexBytes(lod);
            bool touchedLod = false;

            int end = Math.Min(mi + mc, src.MeshCount);
            for (int m = mi; m < end; m++)
            {
                if (!done.Add(m)) continue;
                int mo = src.MeshStart + m * 36;
                if (mo + 36 > s.Length) break;
                ushort vc = U16(mo);
                if (vc == 0) continue;
                ushort matIdx = U16(mo + 8);
                if (matIdx >= matNames.Count || !keepMaterial(matNames[matIdx])) continue;

                var decl = m < src.Decls.Length ? src.Decls[m] : [];
                VElem? pe = null, ue = null;
                foreach (var el in decl)
                {
                    if (el.Usage == UsePosition) pe ??= el;
                    else if (el.Usage == UseUV && el.UsageIndex == 0) ue ??= el;
                }
                if (pe is not { } pos || ue is not { } uvE) continue;
                if (pos.Stream > 2 || uvE.Stream > 2) continue;

                uint[] vbo = { U32(mo + 20), U32(mo + 24), U32(mo + 28) };
                byte[] bs = { s[mo + 32], s[mo + 33], s[mo + 34] };
                if (bs[pos.Stream] == 0 || bs[uvE.Stream] == 0) continue;

                // Both spans must fit inside the file and inside this LOD's declared vertex buffer, measured by what
                // ReadTyped will actually read for each declared type. A refusal is all-or-nothing.
                bool Fits(VElem el)
                {
                    long rel = vbo[el.Stream] + (long)(vc - 1) * bs[el.Stream] + el.Offset + TypeWidth(el.Type);
                    return vbBase + rel <= s.Length && (vbBytes <= 0 || rel <= vbBytes);
                }
                if (!Fits(pos) || !Fits(uvE)) return null;

                int posAt = vbBase + (int)vbo[pos.Stream] + pos.Offset, posStride = bs[pos.Stream];
                int uvAt = vbBase + (int)vbo[uvE.Stream] + uvE.Offset, uvStride = bs[uvE.Stream];

                var x = new float[vc];
                var uv = new (float U, float V)[vc];
                float uLo = float.MaxValue, uHi = float.MinValue;
                for (int i = 0; i < vc; i++)
                {
                    ReadTyped(s, posAt + i * posStride, pos.Type, tmp);
                    x[i] = tmp[0];
                    ReadTyped(s, uvAt + i * uvStride, uvE.Type, tmp);
                    uv[i] = (tmp[0], tmp[1]);
                    if (tmp[0] < uLo) uLo = tmp[0];
                    if (tmp[0] > uHi) uHi = tmp[0];
                }

                // The affine reads u as a position in the [0,1] sheet; a mesh tiled outside it is refused whole.
                if (uLo < -UvTileSlack || uHi > 1f + UvTileSlack)
                {
                    log?.Invoke($"face uv: mesh {m} sits outside the [0,1] tile (u {uLo:F3}..{uHi:F3}) — "
                              + "refusing to rewrite this model");
                    return null;
                }

                ushort subIdx = U16(mo + 10), subCount = U16(mo + 12);
                var tris = MeshTrianglesAt(src, ibBase, subIdx, subCount);
                var sides = SurfaceMirror.AssignSides(x, tris, out int conf, out int strad);
                conflicted += conf;
                straddled += strad;

                // Which vertices a triangle actually names. The rest are morph replacements no authored triangle
                // references, so AssignSides leaves them at 0, the +X branch.
                var claimed = new bool[vc];
                foreach (var t in tris)
                    if (t < vc) claimed[t] = true;

                // So take the side of the vertex each one replaces. LOD0 only, the LOD Parse reads shapes for; a
                // LOD1/LOD2 morph vertex falls through to its own X below.
                if (lod == 0 && src.Shapes.Count > 0)
                {
                    uint meshStartIndex = U32(mo + 16);
                    foreach (var entries in src.Shapes.Values)
                        foreach (var e in entries)
                        {
                            if (e.MeshIndexOffset != meshStartIndex) continue;
                            foreach (var (bIdx, rep) in e.Values)
                            {
                                if (rep >= vc || claimed[rep] || sides[rep] != 0) continue;
                                int slot = ibBase + (int)(meshStartIndex + bIdx) * 2;
                                if (slot < 0 || slot + 2 > s.Length) continue;
                                ushort bv = U16(slot);
                                if (bv >= vc || sides[bv] == 0) continue;
                                sides[rep] = sides[bv];
                                morphSided++;
                            }
                        }
                }

                // Anything still unplaced and named by no triangle answers from its own X. A vertex a
                // triangle DID claim and that still reads 0 is genuinely disputed — two triangles on
                // opposite sides — and keeps the documented +X default rather than being overruled here.
                for (int i = 0; i < vc; i++)
                {
                    if (claimed[i] || sides[i] != 0) continue;
                    sides[i] = x[i] > SurfaceMirror.Midline ? (sbyte)1
                             : x[i] < -SurfaceMirror.Midline ? (sbyte)-1 : (sbyte)0;
                    if (sides[i] == 0) unsided++;
                }

                for (int i = 0; i < vc; i++)
                {
                    if (convert(uv[i].U, uv[i].V, sides[i]) is not { } r) continue;   // unmapped: as authored
                    // Clamped because the affine's ends land a rounding step outside the sheet, and a u of
                    // 1.0005 does not clip — it WRAPS to the far edge and drags the triangle across the face.
                    if (!WriteUv0(outBytes, uvAt + i * uvStride, uvE.Type,
                                  Math.Clamp(r.U, 0f, 1f), r.V))
                    {
                        log?.Invoke($"face uv: mesh {m} declares a uv0 type ({uvE.Type}) this cannot write "
                                  + "— refusing to rewrite this model");
                        return null;
                    }
                    written++;
                }
                meshes++;
                touchedLod = true;
            }
            if (touchedLod) lods++;
        }

        if (written == 0) return null;
        stats = new FaceUvStats(meshes, written, lods, conflicted, straddled, morphSided, unsided);
        log?.Invoke($"face uv: {written} vertex(es) across {meshes} mesh(es) in {lods} LOD(s) "
                  + $"(conflicted {conflicted}, straddling {straddled}, morph-sided {morphSided}, "
                  + $"unsided {unsided})");
        return outBytes;
    }

    /// <summary>
    /// How many bytes <see cref="ReadTyped"/> consumes for a vertex element of this type, exactly; 16 (the
    /// widest) for a type it does not know.
    /// </summary>
    private static int TypeWidth(byte type) => type switch
    {
        0 => 4,                      // Float1
        1 => 8,                      // Float2
        2 => 12,                     // Float3
        3 => 16,                     // Float4
        5 or 8 => 4,                 // Ubyte4 / Ubyte4n
        6 or 9 or 13 or 16 => 4,     // Short2 / Short2n / Half2 / Ushort2
        7 or 10 or 14 or 17 => 8,    // Short4 / Short4n / Half4 / Ushort4
        _ => 16,
    };

    /// <summary>How far outside the [0,1] tile a face mesh's u may stray before <see cref="RewriteFaceUv0"/>
    /// refuses it: for the rounding at an island's edge, not for a tiled mesh.</summary>
    private const float UvTileSlack = 0.01f;
}
