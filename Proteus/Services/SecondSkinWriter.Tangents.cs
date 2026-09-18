using System;
using System.Collections.Generic;

namespace Proteus.Services;

public static partial class SecondSkinWriter
{
    /// <summary>
    /// Re-fit a converted mesh's tangent frame to its NEW UVs: a tangent basis is defined by the UV
    /// parameterization, and <see cref="BuildVerbatim"/> copies the frame byte-for-byte, so a mesh moved into
    /// another layout lights from the wrong direction and a mirrored island reads as an inverted normal map.
    /// The convention is READ off the source rather than authored: per vertex, the sign the stored vector had
    /// against the OLD derivative is written against the NEW one, and the .w handedness lane flips only when
    /// the two frames' handedness disagrees. Only surviving triangles contribute. True when at least one
    /// vertex was re-fitted.
    /// </summary>
    private static bool RetangentMesh(
        byte[][] outStreams, byte[] outStrides, VElem[] decl, ushort vc,
        (float U, float V)[] uvOld, (float U, float V)[] uvNew, List<ushort[]> keptPerSub)
    {
        VElem? pos = null, norm = null, tanEl = null, binEl = null;
        foreach (var el in decl)
            switch (el.Usage)
            {
                case UsePosition: pos ??= el; break;
                case UseNormal:   norm ??= el; break;
                case UseTangent2: tanEl ??= el; break;   // usage 5 — tracks dP/du
                case UseTangent1: binEl ??= el; break;   // usage 6 — tracks dP/dv (the one bodies carry)
            }
        if (pos is not { } pe || norm is not { } ne) return false;
        if (tanEl == null && binEl == null) return false;   // nothing to re-fit

        // Positions are the PUSHED ones the shell ships; the push is along the normal and identical for both UV sets.
        var px = new float[vc * 3];
        var nrm = new float[vc * 3];
        Span<float> tmp = stackalloc float[4];
        for (int i = 0; i < vc; i++)
        {
            ReadTyped(outStreams[pe.Stream], i * outStrides[pe.Stream] + pe.Offset, pe.Type, tmp);
            px[i * 3] = tmp[0]; px[i * 3 + 1] = tmp[1]; px[i * 3 + 2] = tmp[2];
            ReadTyped(outStreams[ne.Stream], i * outStrides[ne.Stream] + ne.Offset, ne.Type, tmp);
            float a = tmp[0], b = tmp[1], c = tmp[2];
            if (ne.Type == 8) { a = a * 2 - 1; b = b * 2 - 1; c = c * 2 - 1; }
            nrm[i * 3] = a; nrm[i * 3 + 1] = b; nrm[i * 3 + 2] = c;
        }

        var tOld = new float[vc * 3]; var bOld = new float[vc * 3];
        var tNew = new float[vc * 3]; var bNew = new float[vc * 3];
        AccumulateFrames(px, uvOld, keptPerSub, tOld, bOld);
        AccumulateFrames(px, uvNew, keptPerSub, tNew, bNew);

        int fixedUp = 0;
        for (int i = 0; i < vc; i++)
        {
            int o = i * 3;
            float nx = nrm[o], ny = nrm[o + 1], nz = nrm[o + 2];
            float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl < 1e-8f) continue;
            nx /= nl; ny /= nl; nz /= nl;

            // All four directions must be well-defined; guessing a frame is worse than keeping what it had.
            if (!InTangentPlane(tOld, o, nx, ny, nz, out var tox, out var toy, out var toz)) continue;
            if (!InTangentPlane(bOld, o, nx, ny, nz, out var box, out var boy, out var boz)) continue;
            if (!InTangentPlane(tNew, o, nx, ny, nz, out var tnx, out var tny, out var tnz)) continue;
            if (!InTangentPlane(bNew, o, nx, ny, nz, out var bnx, out var bny, out var bnz)) continue;

            // (N x B) . T — positive or negative tells the two frames apart; disagreement means the new
            // island is mirrored relative to the old one.
            float hOld = (ny * boz - nz * boy) * tox + (nz * box - nx * boz) * toy + (nx * boy - ny * box) * toz;
            float hNew = (ny * bnz - nz * bny) * tnx + (nz * bnx - nx * bnz) * tny + (nx * bny - ny * bnx) * tnz;
            bool mirrored = hOld * hNew < 0;

            bool any = false;
            if (binEl is { } be)
                any |= Refit(outStreams[be.Stream], i * outStrides[be.Stream] + be.Offset, be.Type,
                             box, boy, boz, bnx, bny, bnz, mirrored);
            if (tanEl is { } te)
                any |= Refit(outStreams[te.Stream], i * outStrides[te.Stream] + te.Offset, te.Type,
                             tox, toy, toz, tnx, tny, tnz, mirrored);
            if (any) fixedUp++;
        }
        return fixedUp > 0;
    }

    /// <summary>Area-weighted tangent accumulation: sum each triangle's dP/du and dP/dv onto its vertices.
    /// UV-degenerate triangles are skipped rather than dividing by ~0.</summary>
    private static void AccumulateFrames(float[] p, (float U, float V)[] uv, List<ushort[]> keptPerSub,
                                         float[] tAcc, float[] bAcc)
    {
        foreach (var keep in keptPerSub)
            for (int k = 0; k + 2 < keep.Length; k += 3)
            {
                int ia = keep[k], ib = keep[k + 1], ic = keep[k + 2];
                int a = ia * 3, b = ib * 3, c = ic * 3;
                float e1x = p[b] - p[a], e1y = p[b + 1] - p[a + 1], e1z = p[b + 2] - p[a + 2];
                float e2x = p[c] - p[a], e2y = p[c + 1] - p[a + 1], e2z = p[c + 2] - p[a + 2];
                float du1 = uv[ib].U - uv[ia].U, dv1 = uv[ib].V - uv[ia].V;
                float du2 = uv[ic].U - uv[ia].U, dv2 = uv[ic].V - uv[ia].V;
                float det = du1 * dv2 - du2 * dv1;
                if (MathF.Abs(det) < 1e-12f) continue;
                float r = 1f / det;
                float tx = (e1x * dv2 - e2x * dv1) * r, ty = (e1y * dv2 - e2y * dv1) * r, tz = (e1z * dv2 - e2z * dv1) * r;
                float bx = (e2x * du1 - e1x * du2) * r, by = (e2y * du1 - e1y * du2) * r, bz = (e2z * du1 - e1z * du2) * r;
                foreach (var v in (ReadOnlySpan<int>)[a, b, c])
                {
                    tAcc[v] += tx; tAcc[v + 1] += ty; tAcc[v + 2] += tz;
                    bAcc[v] += bx; bAcc[v + 1] += by; bAcc[v + 2] += bz;
                }
            }
    }

    /// <summary>Gram-Schmidt an accumulated derivative into the plane of the normal and normalize it.
    /// False when nothing measurable survives (a vertex with no non-degenerate triangle).</summary>
    private static bool InTangentPlane(float[] acc, int o, float nx, float ny, float nz,
                                       out float x, out float y, out float z)
    {
        x = acc[o]; y = acc[o + 1]; z = acc[o + 2];
        float d = x * nx + y * ny + z * nz;
        x -= nx * d; y -= ny * d; z -= nz * d;
        float len = MathF.Sqrt(x * x + y * y + z * z);
        if (len < 1e-8f) return false;
        x /= len; y /= len; z /= len;
        return true;
    }

    /// <summary>
    /// Rewrite one stored frame vector to point along <c>new*</c>, keeping the sign it had relative to
    /// <c>old*</c> (that sign IS the source's convention). <paramref name="mirrored"/> flips the .w lane. False
    /// for a type with no encoder, leaving it byte-identical.
    /// </summary>
    private static bool Refit(byte[] a, int off, byte type,
                              float ox, float oy, float oz, float nx, float ny, float nz, bool mirrored)
    {
        Span<float> cur = stackalloc float[4];
        ReadTyped(a, off, type, cur);
        bool byteNorm = type == 8;
        float sx = cur[0], sy = cur[1], sz = cur[2], sw = cur[3];
        if (byteNorm) { sx = sx * 2 - 1; sy = sy * 2 - 1; sz = sz * 2 - 1; sw = sw * 2 - 1; }
        float sign = sx * ox + sy * oy + sz * oz >= 0f ? 1f : -1f;
        float w = mirrored ? -sw : sw;
        return WriteVec4Typed(a, off, type, sign * nx, sign * ny, sign * nz, w);
    }

    /// <summary>Encode a signed 4-vector into a vertex element. False for a type this can't write.</summary>
    private static bool WriteVec4Typed(byte[] a, int off, byte type, float x, float y, float z, float w)
    {
        static byte B(float v) => (byte)Math.Clamp((int)MathF.Round((v * 0.5f + 0.5f) * 255f), 0, 255);
        switch (type)
        {
            case 8:            // Ubyte4n — what character models actually use for tangent/binormal
                a[off] = B(x); a[off + 1] = B(y); a[off + 2] = B(z); a[off + 3] = B(w);
                return true;
            case 10:           // Short4n
                W16(a, off,     (ushort)(short)Math.Clamp((int)MathF.Round(x * 32767f), -32767, 32767));
                W16(a, off + 2, (ushort)(short)Math.Clamp((int)MathF.Round(y * 32767f), -32767, 32767));
                W16(a, off + 4, (ushort)(short)Math.Clamp((int)MathF.Round(z * 32767f), -32767, 32767));
                W16(a, off + 6, (ushort)(short)Math.Clamp((int)MathF.Round(w * 32767f), -32767, 32767));
                return true;
            case 14:           // Half4
                W16(a, off, Half(x)); W16(a, off + 2, Half(y));
                W16(a, off + 4, Half(z)); W16(a, off + 6, Half(w));
                return true;
            case 3:            // Float4
                W32(a, off,      (uint)BitConverter.SingleToInt32Bits(x));
                W32(a, off + 4,  (uint)BitConverter.SingleToInt32Bits(y));
                W32(a, off + 8,  (uint)BitConverter.SingleToInt32Bits(z));
                W32(a, off + 12, (uint)BitConverter.SingleToInt32Bits(w));
                return true;
            case 2:            // Float3 (no handedness lane to keep)
                W32(a, off,     (uint)BitConverter.SingleToInt32Bits(x));
                W32(a, off + 4, (uint)BitConverter.SingleToInt32Bits(y));
                W32(a, off + 8, (uint)BitConverter.SingleToInt32Bits(z));
                return true;
            default:
                return false;
        }
    }
}
