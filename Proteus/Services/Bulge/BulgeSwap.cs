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

    /// <summary>How far, at most, a body's hole point may lie from the bulge's and the bulge still be fitted to it, warped
    /// (12 mm): TBSE-X's hole is The Body SE's 1.85 mm off (Flaccid M), 10.6 mm (Flaccid L A).</summary>
    internal const float HoleFit = 0.012f;

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

        // The genital piece the bulge plugs: an untagged skin submesh whose rim on the body's other skin is the bulge's
        // hole, point for point within HoleFit.
        ModelPart? piece = null;
        int[]? bodyAt = null;
        foreach (var g in reading.Genitals.Where(g => g.Rule == "c" && g.Part.AttributeMask == 0))
            if (MatchHole(parts, g.Part, bulge.Hole) is { } at) { piece = g.Part; bodyAt = at; break; }
        if (piece == null || bodyAt == null) { reason = "no sculpt for this body"; return null; }
        // The bulge warped onto the body's hole: each rim point onto its partner, the inside by inverse-distance weights.
        var positions = Warp(bulge, bulge.Hole.Select((_, h) => At(parts, bodyAt[h]) - bulge.Hole[h]).ToArray());

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


        // Normals: area-weighted over the bulge's triangles, welded by position; the rim's from the body.
        var tris = bulge.Data.triangles;
        var normal = new Vector3[n];
        var byPlace = new Dictionary<(long, long, long), List<int>>();
        for (int i = 0; i < n; i++)
        {
            var p = positions[i];
            var key = ((long)MathF.Round(p.X * 1e5f), (long)MathF.Round(p.Y * 1e5f), (long)MathF.Round(p.Z * 1e5f));
            (byPlace.TryGetValue(key, out var l) ? l : byPlace[key] = []).Add(i);
        }
        var acc = new Vector3[n];
        for (int t = 0; t < tCount; t++)
        {
            var a = positions[tris[t * 3]];
            var face = Vector3.Cross(positions[tris[t * 3 + 1]] - a, positions[tris[t * 3 + 2]] - a);
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
        var (dPdu, dPdv) = Derivatives(positions, bulgeUv, tris);

        var o = (byte[])body.Clone();
        var slotOf = new int[n];
        for (int i = 0; i < n; i++)
        {
            int slot = owned[i], local = slot - span.BaseVertex;
            slotOf[i] = slot;
            var p = positions[i];
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
            // A genital bone's share, and a bone the body's skin does not carry (the sculpt's rig may name one), go to j_kosi.
            foreach (var b in w.Keys.Where(b => GenitalParts.IsPenisBone(b) || (LocalBone(b) < 0 && b != "j_kosi")).ToList())
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

        // Triangles: the bulge's into the piece's first, the rest collapsed onto the bulge's first vertex — not onto their
        // own corners, which would leave the old genitals' vertices referenced (by zero-area triangles, at their old places
        // and uvs) for a landing by uv to find.
        int collapsed = 0;
        ushort onto = (ushort)(slotOf[0] - span.BaseVertex);
        for (uint t = 0; t < sc / 3; t++)
        {
            int at = src.Ib + (int)(so + t * 3) * 2;
            if (t < tCount)
                for (int k = 0; k < 3; k++)
                    BitConverter.TryWriteBytes(o.AsSpan(at + k * 2), (ushort)(slotOf[tris[t * 3 + k]] - span.BaseVertex));
            else
            {
                for (int k = 0; k < 3; k++) BitConverter.TryWriteBytes(o.AsSpan(at + k * 2), onto);
                collapsed++;
            }
        }

        // No penis physics anywhere on the body's skin: a skin vertex's genital-bone share goes to j_kosi, so the skin at the
        // bulge's rim moves as the rim does (TBSE-X weights its crotch skin a little to the penis).
        foreach (var part in parts.Parts.Where(p => p.Island < 0 && p.Mesh == piece.Mesh && p != piece && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            foreach (int v in part.Triangles.Distinct())
            {
                if (v < span.BaseVertex || v >= span.BaseVertex + span.Count) continue;
                var w = new Dictionary<string, float>(StringComparer.Ordinal);
                bool onPenis = false;
                for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
                {
                    float x = skin.BoneWeights[v * 8 + k];
                    if (x <= 0f) continue;
                    string b = skin.BoneNames[skin.BoneIndices[v * 8 + k]];
                    if (GenitalParts.IsPenisBone(b)) { onPenis = true; b = "j_kosi"; }
                    w[b] = w.GetValueOrDefault(b) + x;
                }
                if (!onPenis) continue;
                int local = v - span.BaseVertex;
                if (!WriteWeights(o, Addr(we.Value, local), we.Value.Type, Addr(ie.Value, local), ie.Value.Type, w, LocalBone, out string why))
                { reason = why; return null; }
            }

        // Every other genital part goes: TBSE-X's _dwn_c meshes, a merged penis's triangles.
        var hide = new HashSet<PolygonSelection.Key>();
        foreach (var g in reading.Genitals.Where(g => g.Part != piece))
            foreach (int t in g.Triangles)
                hide.Add(PolygonSelection.Key.Of(g.Part.Triangles[t * 3], g.Part.Triangles[t * 3 + 1], g.Part.Triangles[t * 3 + 2]));
        collapsed += ModelPartHider.Hide(o, parts.MeshSpans, hide);
        return new Result(o, slotOf, collapsed);
    }

    /// <summary>
    /// The body's vertex partnering each of the bulge's hole points, or null: <paramref name="piece"/>'s open rim, welded by
    /// position, where it lies on the body's OTHER skin (openings of the piece's own, which touch no skin, aside), must be
    /// as many points as the hole, each the nearest of a distinct hole point and within <see cref="HoleFit"/> of it.
    /// </summary>
    internal static int[]? MatchHole(ModelParts parts, ModelPart piece, IReadOnlyList<Vector3> hole)
    {
        (long, long, long) K(Vector3 p) => ((long)MathF.Round(p.X * 1e5f), (long)MathF.Round(p.Y * 1e5f), (long)MathF.Round(p.Z * 1e5f));
        var otherSkin = new Dictionary<(long, long, long), int>();
        foreach (var part in parts.Parts.Where(p => p.Island < 0 && p != piece && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            foreach (int v in part.Triangles) otherSkin.TryAdd(K(At(parts, v)), v);
        var use = new Dictionary<((long, long, long), (long, long, long)), int>();
        for (int t = 0; t + 2 < piece.Triangles.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                var a = K(At(parts, piece.Triangles[t + k]));
                var b = K(At(parts, piece.Triangles[t + (k + 1) % 3]));
                var e = a.CompareTo(b) < 0 ? (a, b) : (b, a);
                use[e] = use.GetValueOrDefault(e) + 1;
            }
        // The rim on the other skin, walked as a loop.
        var adj = new Dictionary<(long, long, long), List<(long, long, long)>>();
        foreach (var (e, _) in use.Where(kv => kv.Value == 1 && otherSkin.ContainsKey(kv.Key.Item1) && otherSkin.ContainsKey(kv.Key.Item2)))
        {
            (adj.TryGetValue(e.Item1, out var l1) ? l1 : adj[e.Item1] = []).Add(e.Item2);
            (adj.TryGetValue(e.Item2, out var l2) ? l2 : adj[e.Item2] = []).Add(e.Item1);
        }
        if (adj.Count != hole.Count || adj.Values.Any(l => l.Count != 2)) return null;
        var start = adj.Keys.Min();
        var loop = new List<int> { otherSkin[start] };
        var prev = start;
        var cur = adj[start][0];
        while (cur != start && loop.Count <= hole.Count)
        {
            loop.Add(otherSkin[cur]);
            var next = adj[cur][0] == prev ? adj[cur][1] : adj[cur][0];
            prev = cur;
            cur = next;
        }
        if (loop.Count != hole.Count) return null;
        // The rotation and direction that put the two loops nearest, judged by their worst pair.
        int n = hole.Count;
        (float Worst, int Shift, int Dir) best = (float.MaxValue, 0, 1);
        foreach (int dir in new[] { 1, -1 })
            for (int shift = 0; shift < n; shift++)
            {
                float worst = 0f;
                for (int h = 0; h < n && worst < best.Worst; h++)
                    worst = MathF.Max(worst, Vector3.Distance(At(parts, loop[((shift + dir * h) % n + n) % n]), hole[h]));
                if (worst < best.Worst) best = (worst, shift, dir);
            }
        if (best.Worst > HoleFit) return null;
        var at = new int[n];
        for (int h = 0; h < n; h++) at[h] = loop[((best.Shift + best.Dir * h) % n + n) % n];
        return at;
    }

    /// <summary>The bulge's points moved by its hole's offsets: a rim point onto its partner exactly, every other by the
    /// offsets weighted by inverse squared distance to the hole points.</summary>
    internal static Vector3[] Warp(TbseBulge bulge, Vector3[] offset)
    {
        var o = new Vector3[bulge.Positions.Length];
        for (int i = 0; i < o.Length; i++)
        {
            var p = bulge.Positions[i];
            // A rim point onto its hole point's partner EXACTLY (the sculpt's rim sits within 0.1 mm of the hole).
            if (bulge.Data.rim[i] >= 0) { o[i] = bulge.Hole[bulge.Data.rim[i]] + offset[bulge.Data.rim[i]]; continue; }
            float wsum = 0f;
            var d = Vector3.Zero;
            for (int h = 0; h < bulge.Hole.Length; h++)
            {
                float w = 1f / MathF.Max(Vector3.DistanceSquared(p, bulge.Hole[h]), 1e-10f);
                wsum += w;
                d += offset[h] * w;
            }
            o[i] = p + d / wsum;
        }
        return o;
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
