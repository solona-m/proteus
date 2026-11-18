using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services.Bulge;

/// <summary>
/// Path A of plan featureless-bulge: a TBSE-family legs body made featureless by drawing the owner's bulge
/// (<see cref="TbseBulge"/>) in place of its genital piece — IN PLACE and length-neutral. The genital piece is an
/// untagged skin submesh that owns its vertex slots (Gen A <c>0.3</c>: 953 slots, 1770 triangles), and the bulge
/// needs fewer of both (153 and 254), so its vertices are written into the piece's first slots, its triangles into the
/// piece's first index-buffer triangles, and the piece's remaining triangles collapse to a point. No mesh, submesh,
/// material or buffer changes size, so no writer beyond the in-place element writes is involved.
/// <para/>
/// The bulge's rim sits on the hole's points; there it takes the body's own weights and normals, so the seam follows the
/// body exactly (TBSE-X keeps TBSE-X's weights). Inside, its own weights, any genital bone's share moved to
/// <c>j_kosi</c>. Its tangent frame is computed from its uvs in the convention the piece's own vertices carried.
/// Other genital parts the body draws (TBSE-X's <c>_dwn_c</c>) collapse too.
/// </summary>
internal static class BulgeSwap
{
    /// <summary>The featureless body.</summary>
    /// <param name="Model">The rewritten model.</param>
    /// <param name="SlotOf">Per bulge vertex, the global vertex index (part reader numbering) it was written to.</param>
    /// <param name="Collapsed">Triangles collapsed: the piece's spare ones and every other genital part's.</param>
    internal sealed record Result(byte[] Model, int[] SlotOf, int Collapsed);

    /// <summary>How near the bulge's hole and the body's must be, point for point, to be the same hole (0.1 mm).</summary>
    internal const float SameHole = 1e-4f;

    private static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>The body made featureless, or null with the reason: no genitals, no piece the bulge fits, too few slots,
    /// a shape key on the piece, an element this cannot write.</summary>
    internal static Result? Featureless(byte[] body, TbseBulge bulge, out string reason)
    {
        reason = "";
        var parts = ModelPartReader.Read(body);
        var skin = ModelSkinReader.Read(body, null, null);
        if (parts == null || skin == null) { reason = "the body could not be read"; return null; }
        var reading = GenitalParts.Read(parts, skin);
        if (reading is not { Penis: true }) { reason = "no penis"; return null; }

        // The genital piece the bulge plugs: an untagged skin submesh whose open rim is the bulge's hole.
        ModelPart? piece = null;
        foreach (var g in reading.Genitals.Where(g => g.Rule == "c" && g.Part.AttributeMask == 0))
            if (SameRim(parts, g.Part, bulge.Hole)) { piece = g.Part; break; }
        if (piece == null) { reason = "no sculpt for this body"; return null; }

        var others = parts.Parts.Where(p => p.Island < 0 && p != piece).SelectMany(p => p.Triangles).ToHashSet();
        var owned = piece.Triangles.Distinct().Where(v => !others.Contains(v)).OrderBy(v => v).ToArray();
        int n = bulge.Positions.Length, tCount = bulge.Data.triangles.Length / 3;
        if (owned.Length < n || piece.Triangles.Length / 3 < tCount)
        {
            reason = $"the genital piece has {owned.Length} vertices and {piece.Triangles.Length / 3} triangles, fewer than the bulge's {n} and {tCount}";
            return null;
        }

        var src = SecondSkinWriter.Parse(body);
        var span = parts.MeshSpans.Single(s => s.Mesh == piece.Mesh);
        int mo = src.MeshStart + piece.Mesh * 36;
        ushort subIdx = BitConverter.ToUInt16(body, mo + 10);
        uint meshStart = BitConverter.ToUInt32(body, mo + 16);
        int ss = src.SubmeshStart + (subIdx + piece.Submesh) * 16;
        uint so = BitConverter.ToUInt32(body, ss), sc = BitConverter.ToUInt32(body, ss + 4);
        foreach (var entries in src.Shapes.Values)
            foreach (var e in entries)
                if (e.MeshIndexOffset == meshStart && e.Values.Any(x => x.Base >= so - meshStart && x.Base < so - meshStart + sc))
                {
                    reason = "a shape key moves the genital piece";
                    return null;
                }

        var decl = piece.Mesh < src.Decls.Length ? src.Decls[piece.Mesh] : [];
        SecondSkinWriter.VElem? pe = null, ne = null, ue = null, we = null, ie = null, be = null;
        foreach (var el in decl)
            switch (el.Usage)
            {
                case SecondSkinWriter.UsePosition: pe ??= el; break;
                case SecondSkinWriter.UseNormal: ne ??= el; break;
                case SecondSkinWriter.UseUV: if (el.UsageIndex == 0) ue ??= el; break;
                case SecondSkinWriter.UseBlendWeight: we ??= el; break;
                case SecondSkinWriter.UseBlendIndices: ie ??= el; break;
                case SecondSkinWriter.UseTangent1: be ??= el; break;
            }
        if (pe == null || ne == null || ue == null || we == null || ie == null) { reason = "the body's skin lacks an element the bulge needs"; return null; }

        uint[] vbo = [BitConverter.ToUInt32(body, mo + 20), BitConverter.ToUInt32(body, mo + 24), BitConverter.ToUInt32(body, mo + 28)];
        byte[] stride = [body[mo + 32], body[mo + 33], body[mo + 34]];
        int Addr(SecondSkinWriter.VElem el, int local) => (int)(src.Vb + vbo[el.Stream]) + local * stride[el.Stream] + el.Offset;
        ushort tbl = BitConverter.ToUInt16(body, mo + 14);
        var boneTable = tbl < src.BoneTables.Length ? src.BoneTables[tbl] : [];
        int LocalBone(string name)
        {
            for (int i = 0; i < boneTable.Length; i++)
                if (boneTable[i] < src.BoneNames.Length && src.BoneNames[boneTable[i]] == name) return i;
            return -1;
        }

        // The body's own vertex at each hole point, off the piece: whose weights and normal the rim takes.
        var bodyAt = new int[bulge.Hole.Length];
        var skinOff = parts.Parts.Where(p => p.Island < 0 && p != piece && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                           .SelectMany(p => p.Triangles).Distinct().ToList();
        for (int h = 0; h < bulge.Hole.Length; h++)
        {
            int best = skinOff.MinBy(v => Vector3.DistanceSquared(At(parts, v), bulge.Hole[h]));
            if (Vector3.Distance(At(parts, best), bulge.Hole[h]) > SameHole) { reason = "the hole is not the body's"; return null; }
            bodyAt[h] = best;
        }

        // Normals: area-weighted over the bulge's triangles, welded by position; the rim's from the body.
        var tris = bulge.Data.triangles;
        var normal = new Vector3[n];
        var byPlace = new Dictionary<(long, long, long), List<int>>();
        for (int i = 0; i < n; i++)
        {
            var p = bulge.Positions[i];
            var key = ((long)MathF.Round(p.X * 1e5f), (long)MathF.Round(p.Y * 1e5f), (long)MathF.Round(p.Z * 1e5f));
            (byPlace.TryGetValue(key, out var l) ? l : byPlace[key] = []).Add(i);
        }
        var acc = new Vector3[n];
        for (int t = 0; t < tCount; t++)
        {
            var a = bulge.Positions[tris[t * 3]];
            var face = Vector3.Cross(bulge.Positions[tris[t * 3 + 1]] - a, bulge.Positions[tris[t * 3 + 2]] - a);
            for (int k = 0; k < 3; k++) acc[tris[t * 3 + k]] += face;
        }
        foreach (var group in byPlace.Values)
        {
            var sum = group.Aggregate(Vector3.Zero, (s, i) => s + acc[i]);
            foreach (int i in group) normal[i] = sum.LengthSquared() > 0 ? Vector3.Normalize(sum) : Vector3.UnitZ;
        }
        for (int i = 0; i < n; i++)
            if (bulge.Data.rim[i] >= 0)
            {
                int v = bodyAt[bulge.Data.rim[i]];
                var bn = new Vector3(parts.Normals[v * 3], parts.Normals[v * 3 + 1], parts.Normals[v * 3 + 2]);
                if (bn.LengthSquared() > 1e-12f) normal[i] = Vector3.Normalize(bn);
            }

        // The tangent convention: read off the piece's own vertices — the stored dP/dv's sign against the computed one.
        Span<float> tmp = stackalloc float[4];
        float sign = 1f, wRef = 1f, hRef = 1f;
        if (be is { } bel)
        {
            var (signRead, wRead, hRead) = PieceConvention(body, src, parts, piece, span, bel, ne.Value, Addr);
            sign = signRead; wRef = wRead; hRef = hRead;
        }
        var bulgeUv = bulge.Data.uv.Select(u => new Vector2(u[0], u[1])).ToArray();
        var (dPdu, dPdv) = Derivatives(bulge.Positions, bulgeUv, tris);

        var o = (byte[])body.Clone();
        var slotOf = new int[n];
        for (int i = 0; i < n; i++)
        {
            int slot = owned[i], local = slot - span.BaseVertex;
            slotOf[i] = slot;
            var p = bulge.Positions[i];
            SecondSkinWriter.WriteXYZ(o, Addr(pe.Value, local), pe.Value.Type, p.X, p.Y, p.Z);
            if (!SecondSkinWriter.WriteNormal(o, Addr(ne.Value, local), ne.Value.Type, normal[i].X, normal[i].Y, normal[i].Z))
            { reason = "the body's normals are in a format this cannot write"; return null; }
            if (!SecondSkinWriter.WriteUv0(o, Addr(ue.Value, local), ue.Value.Type, bulgeUv[i].X, bulgeUv[i].Y))
            { reason = "the body's uvs are in a format this cannot write"; return null; }

            // Weights: the body's own on the rim, the bulge's inside; genital bones' share to j_kosi.
            var w = new Dictionary<string, float>(StringComparer.Ordinal);
            if (bulge.Data.rim[i] >= 0)
            {
                int v = bodyAt[bulge.Data.rim[i]];
                for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
                {
                    float x = skin.BoneWeights[v * 8 + k];
                    if (x > 0f) { string b = skin.BoneNames[skin.BoneIndices[v * 8 + k]]; w[b] = w.GetValueOrDefault(b) + x; }
                }
            }
            else foreach (var (b, x) in bulge.Data.weights[i]) w[b] = w.GetValueOrDefault(b) + x;
            foreach (var b in w.Keys.Where(GenitalParts.IsPenisBone).ToList())
            {
                w["j_kosi"] = w.GetValueOrDefault("j_kosi") + w[b];
                w.Remove(b);
            }
            if (!WriteWeights(o, Addr(we.Value, local), we.Value.Type, Addr(ie.Value, local), ie.Value.Type, w, LocalBone, out string why))
            { reason = why; return null; }

            if (be is { } bt)
            {
                var nn = normal[i];
                var bv = dPdv[i] - nn * Vector3.Dot(dPdv[i], nn);
                var tv = dPdu[i] - nn * Vector3.Dot(dPdu[i], nn);
                if (bv.LengthSquared() > 1e-16f)
                {
                    bv = Vector3.Normalize(bv) * sign;
                    float h = MathF.Sign(Vector3.Dot(Vector3.Cross(nn, Vector3.Normalize(dPdv[i] - nn * Vector3.Dot(dPdv[i], nn))), tv));
                    float wv = h * hRef >= 0 ? wRef : -wRef;
                    WriteFrame(o, Addr(bt, local), bt.Type, bv, wv);
                }
            }
        }

        // Triangles: the bulge's into the piece's first, the rest collapsed onto their first corner.
        int collapsed = 0;
        for (uint t = 0; t < sc / 3; t++)
        {
            int at = src.Ib + (int)(so + t * 3) * 2;
            if (t < tCount)
                for (int k = 0; k < 3; k++)
                    BitConverter.TryWriteBytes(o.AsSpan(at + k * 2), (ushort)(slotOf[tris[t * 3 + k]] - span.BaseVertex));
            else
            {
                ushort a = BitConverter.ToUInt16(o, at);
                BitConverter.TryWriteBytes(o.AsSpan(at + 2), a);
                BitConverter.TryWriteBytes(o.AsSpan(at + 4), a);
                collapsed++;
            }
        }

        // Every other genital part goes: TBSE-X's _dwn_c meshes, a merged penis's triangles.
        var hide = new HashSet<PolygonSelection.Key>();
        foreach (var g in reading.Genitals.Where(g => g.Part != piece))
            foreach (int t in g.Triangles)
                hide.Add(PolygonSelection.Key.Of(g.Part.Triangles[t * 3], g.Part.Triangles[t * 3 + 1], g.Part.Triangles[t * 3 + 2]));
        collapsed += ModelPartHider.Hide(o, parts.MeshSpans, hide);
        return new Result(o, slotOf, collapsed);
    }

    /// <summary>Whether <paramref name="piece"/>'s open rim, welded by position, is <paramref name="hole"/> point for point.</summary>
    internal static bool SameRim(ModelParts parts, ModelPart piece, IReadOnlyList<Vector3> hole)
    {
        (long, long, long) K(Vector3 p) => ((long)MathF.Round(p.X * 1e5f), (long)MathF.Round(p.Y * 1e5f), (long)MathF.Round(p.Z * 1e5f));
        var at = new Dictionary<(long, long, long), Vector3>();
        var use = new Dictionary<((long, long, long), (long, long, long)), int>();
        for (int t = 0; t + 2 < piece.Triangles.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                var a = At(parts, piece.Triangles[t + k]);
                var b = At(parts, piece.Triangles[t + (k + 1) % 3]);
                at[K(a)] = a;
                at[K(b)] = b;
                var e = K(a).CompareTo(K(b)) < 0 ? (K(a), K(b)) : (K(b), K(a));
                use[e] = use.GetValueOrDefault(e) + 1;
            }
        var rim = use.Where(kv => kv.Value == 1).SelectMany(kv => new[] { kv.Key.Item1, kv.Key.Item2 }).Distinct().Select(k => at[k]).ToList();
        return rim.Count == hole.Count && hole.All(h => rim.Any(r => Vector3.Distance(r, h) < SameHole));
    }

    /// <summary>Per vertex, the area-weighted dP/du and dP/dv over its triangles.</summary>
    private static (Vector3[] Du, Vector3[] Dv) Derivatives(Vector3[] p, Vector2[] uv, int[] tris)
    {
        var du = new Vector3[p.Length];
        var dv = new Vector3[p.Length];
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            var e1 = p[b] - p[a];
            var e2 = p[c] - p[a];
            float du1 = uv[b].X - uv[a].X, dv1 = uv[b].Y - uv[a].Y, du2 = uv[c].X - uv[a].X, dv2 = uv[c].Y - uv[a].Y;
            float det = du1 * dv2 - du2 * dv1;
            if (MathF.Abs(det) < 1e-12f) continue;
            var tu = (e1 * dv2 - e2 * dv1) / det;
            var tv = (e2 * du1 - e1 * du2) / det;
            foreach (int v in (ReadOnlySpan<int>)[a, b, c]) { du[v] += tu; dv[v] += tv; }
        }
        return (du, dv);
    }

    /// <summary>The piece's own tangent convention: the majority sign of its stored dP/dv against the computed one, its
    /// majority handedness lane, and the handedness that lane was stored against.</summary>
    private static (float Sign, float W, float H) PieceConvention(byte[] body, SecondSkinWriter.Source src, ModelParts parts,
        ModelPart piece, MeshSpan span, SecondSkinWriter.VElem be, SecondSkinWriter.VElem ne, Func<SecondSkinWriter.VElem, int, int> addr)
    {
        var uv = BodyRetarget.Uv(body);
        var verts = piece.Triangles.Distinct().ToArray();
        var index = verts.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
        var p = verts.Select(v => At(parts, v)).ToArray();
        var u = verts.Select(v => new Vector2(uv[v * 2], uv[v * 2 + 1])).ToArray();
        var tris = piece.Triangles.Select(v => index[v]).ToArray();
        var (du, dv) = Derivatives(p, u, tris);
        Span<float> t = stackalloc float[4];
        int pos = 0, neg = 0, wPos = 0, wNeg = 0, hAgree = 0, hDisagree = 0;
        for (int i = 0; i < verts.Length; i++)
        {
            int local = verts[i] - span.BaseVertex;
            SecondSkinWriter.ReadTyped(body, addr(ne, local), ne.Type, t);
            var nn = ne.Type == 8 ? new Vector3(t[0] * 2 - 1, t[1] * 2 - 1, t[2] * 2 - 1) : new Vector3(t[0], t[1], t[2]);
            SecondSkinWriter.ReadTyped(body, addr(be, local), be.Type, t);
            var stored = be.Type == 8 ? new Vector3(t[0] * 2 - 1, t[1] * 2 - 1, t[2] * 2 - 1) : new Vector3(t[0], t[1], t[2]);
            float w = be.Type == 8 ? t[3] * 2 - 1 : t[3];
            if (nn.LengthSquared() < 1e-8f || dv[i].LengthSquared() < 1e-16f) continue;
            nn = Vector3.Normalize(nn);
            var bv = dv[i] - nn * Vector3.Dot(dv[i], nn);
            if (Vector3.Dot(stored, bv) >= 0) pos++; else neg++;
            if (w >= 0) wPos++; else wNeg++;
            float h = MathF.Sign(Vector3.Dot(Vector3.Cross(nn, Vector3.Normalize(bv)), du[i] - nn * Vector3.Dot(du[i], nn)));
            if (h * MathF.Sign(w) >= 0) hAgree++; else hDisagree++;
        }
        float wRef = wPos >= wNeg ? 1f : -1f;
        return (pos >= neg ? 1f : -1f, wRef, hAgree >= hDisagree ? wRef : -wRef);
    }

    /// <summary>Write a tangent-frame vector and its handedness lane.</summary>
    private static void WriteFrame(byte[] a, int off, byte type, Vector3 v, float w)
    {
        if (type == 8)
        {
            a[off] = (byte)Math.Clamp(MathF.Round((v.X * 0.5f + 0.5f) * 255f), 0f, 255f);
            a[off + 1] = (byte)Math.Clamp(MathF.Round((v.Y * 0.5f + 0.5f) * 255f), 0f, 255f);
            a[off + 2] = (byte)Math.Clamp(MathF.Round((v.Z * 0.5f + 0.5f) * 255f), 0f, 255f);
            a[off + 3] = (byte)Math.Clamp(MathF.Round((w * 0.5f + 0.5f) * 255f), 0f, 255f);
        }
        else SecondSkinWriter.WriteNormal(a, off, type, v.X, v.Y, v.Z);
    }

    /// <summary>Encode bone weights (bytes summing to 255, largest first) and their bone-table indices.</summary>
    private static bool WriteWeights(byte[] o, int wAt, byte wType, int iAt, byte iType, Dictionary<string, float> w,
                                     Func<string, int> localBone, out string reason)
    {
        reason = "";
        int slots = SecondSkinWriter.BlendCount(wType);
        if (SecondSkinWriter.BlendCount(iType) != slots) { reason = "the body's weight and index formats disagree"; return false; }
        var list = w.Where(kv => kv.Value > 0f).OrderByDescending(kv => kv.Value).Take(slots).ToList();
        foreach (var (b, _) in list)
            if (localBone(b) < 0) { reason = $"the body's skin has no {b} bone"; return false; }
        float sum = list.Sum(kv => kv.Value);
        if (sum <= 0f) { reason = "a bulge vertex has no weight"; return false; }
        var bytes = list.Select(kv => (int)MathF.Round(kv.Value / sum * 255f)).ToArray();
        if (bytes.Length > 0) bytes[0] += 255 - bytes.Sum();
        for (int k = 0; k < slots; k++)
        {
            o[wAt + k] = k < bytes.Length ? (byte)Math.Clamp(bytes[k], 0, 255) : (byte)0;
            o[iAt + k] = k < list.Count ? (byte)localBone(list[k].Key) : (byte)0;
        }
        return true;
    }
}
