using System;
using System.Collections.Generic;

namespace Proteus.Services;
using static Proteus.Services.SecondSkinWriter;
using static Proteus.Services.MeshMath;

internal static partial class BodyBridge
{
    /// <summary>
    /// Least coverage the garment must have over a nail before it is flattened. The body is only ever changed
    /// where something covers it.
    /// </summary>
    private const byte NailCoveredFloor = 8;

    /// <summary>
    /// Flattens the NAILS out of a hand, on the body itself, so a garment over them has nothing to poke through.
    /// Each nail is its own little island of skin sitting in a socket in the finger (see
    /// <see cref="SecondSkinWriter.NailBedIslands"/>); relaxing its inside with its rim pinned replaces the nail
    /// with the surface that spans the socket — the nail is gone and the gap is bridged by its own triangles, so
    /// nothing is added or removed and no offset in the file moves. A separate nail MESH riding on the bed is
    /// carried down with it by <see cref="CarrySkinMove"/>, the same way the nipple relax carries piercings.
    /// <para/>
    /// Runs on the body BEFORE the shell is cut from it, like the other body passes, so the garment is a displaced
    /// copy of a hand that has no nails. Returns null when there is nothing to do.
    /// </summary>
    /// <param name="gate">Carries the garment's coverage; a nail no layer covers is left alone.</param>
    internal static byte[]? FlattenNails(byte[] mdl, SecondSkinLayer gate, Action<string>? log = null)
    {
        if (mdl is not { Length: > 0 }) return null;
        var plans = NailBedPlans(mdl);
        if (plans.Count == 0) return null;

        Source src;
        try { src = Parse(mdl); }
        catch { return null; }

        var s = src.S;
        var outBytes = (byte[])mdl.Clone();
        var matNames = ReadMaterialNames(s, src);
        // Every nail bed vertex as it stood BEFORE flattening: a nail MESH drawn over one is removed with it.
        var nailWas = new List<Vec3>();
        int nails = 0, uncovered = 0, moved = 0, collapsed = 0, weldWeights = 0;
        float most = 0f;

        Span<float> tmp = stackalloc float[4];
        foreach (var (m, plan) in plans)
        {
            var beds = plan.Islands;
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > s.Length) continue;
            ushort vc = BitConverter.ToUInt16(s, mo);
            if (vc == 0) continue;

            var decl = m < src.Decls.Length ? src.Decls[m] : [];
            VElem? pe = null, ne = null, ue = null;
            foreach (var el in decl)
            {
                if (el.Usage == UsePosition) pe ??= el;
                else if (el.Usage == UseNormal) ne ??= el;
                else if (el.Usage == UseUV && el.UsageIndex == 0) ue ??= el;
            }
            if (pe is not { } pel || pel.Stream > 2) continue;

            uint[] vbo = { BitConverter.ToUInt32(s, mo + 20), BitConverter.ToUInt32(s, mo + 24),
                           BitConverter.ToUInt32(s, mo + 28) };
            byte[] bs = { s[mo + 32], s[mo + 33], s[mo + 34] };
            int At(VElem el, int i) => src.Vb + (int)vbo[el.Stream] + i * bs[el.Stream] + el.Offset;

            var pos = new Vec3[vc];
            for (int i = 0; i < vc; i++)
            {
                int a = At(pel, i);
                if (a < 0 || a + 16 > s.Length) return null;
                ReadTyped(s, a, pel.Type, tmp);
                pos[i] = new Vec3(tmp[0], tmp[1], tmp[2]);
            }

            for (int k = 0; k < plan.Islands.Count && k < plan.VertsOf.Count; k++)
            {
                var (tris, verts) = (plan.Islands[k], plan.VertsOf[k]);
                // The FINGERTIP around the nail counts as well as the nail's own island: a glove's art rarely paints
                // the nail chart at all — that is why the nails showed through it in the first place.
                if (!Covered(gate, s, src, ue, At, bs, tris, tmp) && !FingertipCovered(gate, plan, verts))
                { uncovered++; continue; }

                var flat = new List<(int V, Vec3 To)>(verts.Length);
                if (k < plan.Sewn.Count && !plan.Sewn[k])
                {
                    // Nothing holds this nail to the finger, and the finger is whole underneath: collapse it to a
                    // point. Every triangle then has no area and draws nothing — a flattened patch cannot match
                    // that, because its triangles are chords and a chord cuts back out of the skin across a fold.
                    // The point is taken INSIDE the finger (the landings, which are under the skin), so there is
                    // nothing of it outside the body even before its triangles collapse.
                    float cx = 0, cy = 0, cz = 0;
                    int n = 0;
                    foreach (int v in verts)
                        if (plan.OnSkin[v] is { } q) { cx += q.X; cy += q.Y; cz += q.Z; n++; }
                    if (n == 0)
                    {
                        foreach (int v in verts) { cx += pos[v].X; cy += pos[v].Y; cz += pos[v].Z; }
                        n = verts.Length;
                    }
                    var at = new Vec3(cx / n, cy / n, cz / n);
                    foreach (int v in verts) flat.Add((v, at));
                    // ...and ONE set of bone weights for the lot. A collapsed island is only collapsed in the bind
                    // pose: leave each vertex its own weights and the game's skinning pulls them apart again the
                    // moment a finger bends, and the triangles get their area back. That is the speck at the tip.
                    weldWeights += WeldSkinning(outBytes, src, m, decl, vbo, bs, verts);
                    // ...and ONE normal, for the same reason: the second skin pushes every vertex out along its
                    // own, so a collapsed point whose vertices still face every which way fans back open into
                    // slivers — wearing the nail's texture, since they keep its UVs.
                    if (ne is { } nel2 && nel2.Stream <= 2) WeldNormals(outBytes, At, nel2, verts);
                    collapsed++;
                }
                else
                {
                    // Sewn in: removing it would open a hole, so it is laid down onto the skin AROUND it instead,
                    // rim and all. Not a surface spanning its own boundary — that boundary is the nail's outline.
                    foreach (int v in verts)
                        if (plan.OnSkin[v] is { } to) flat.Add((v, to));
                }
                if (flat.Count == 0) continue;
                nails++;

                foreach (var (v, to) in flat)
                {
                    // Where the nail WAS, so the nail mesh standing on it can be found below.
                    nailWas.Add(pos[v]);
                    float d = Dist(pos[v], to);
                    if (d <= 1e-7f) continue;
                    WriteXYZ(outBytes, At(pel, v), pel.Type, to.X, to.Y, to.Z);
                    pos[v] = to;
                    moved++;
                    most = MathF.Max(most, d);
                }
            }

            // Re-shade what was laid down onto the finger, from the surface it now is. NOT the collapsed islands:
            // those were given one shared normal above, and a per-vertex re-shade would undo it.
            if (ne is { } nel && nel.Stream <= 2 && moved > 0)
                for (int k = 0; k < plan.Islands.Count; k++)
                {
                    if (k < plan.Sewn.Count && !plan.Sewn[k]) continue;
                    foreach (var (v, n) in IslandNormals(pos, plan.Islands[k]))
                        WriteNormal(outBytes, At(nel, v), nel.Type, n.X, n.Y, n.Z);
                }
        }

        if (moved == 0)
        {
            if (uncovered > 0)
                log?.Invoke($"nails: {uncovered} left alone, nothing covers them");
            return null;
        }

        // The nail MESH goes the same way, and is not CARRIED: a nail laid flat on the finger is still a nail,
        // drawn in nail colour over the skin. Each of its islands collapses to a point, so every triangle of it
        // has no area and nothing is rasterised — the same in-place edit, no index or submesh surgery.
        var (removedIslands, removedVerts) = RemoveNailMeshes(outBytes, src, matNames, nailWas);

        log?.Invoke($"nails: {collapsed} of {nails} collapsed to nothing ({weldWeights} vertices given one "
                  + $"skinning so they stay collapsed when the hand moves), the rest laid down onto the finger "
                  + $"({moved} vertices, furthest {most:F4}), "
                  + $"removed {removedIslands} nail mesh island(s) ({removedVerts} vertices), "
                  + $"{uncovered} left uncovered");
        return outBytes;
    }

    /// <summary>
    /// Give every vertex of a collapsed island the SAME normal, copied from the first of them: anything that
    /// offsets a surface along its normals — the second skin does — would otherwise push the collapsed point apart
    /// again, one sliver per direction.
    /// </summary>
    private static void WeldNormals(byte[] outBytes, Func<VElem, int, int> at, VElem ne, int[] verts)
    {
        if (verts.Length < 2) return;
        int from = at(ne, verts[0]);
        int width = ne.Type switch { 8 => 4, 13 or 14 => 8, _ => 16 };   // as ReadTyped/WriteNormal see it
        if (from < 0 || from + width > outBytes.Length) return;
        foreach (int v in verts)
        {
            int to = at(ne, v);
            if (v == verts[0] || to < 0 || to + width > outBytes.Length) continue;
            Array.Copy(outBytes, from, outBytes, to, width);
        }
    }

    /// <summary>
    /// Give every vertex of a collapsed island the SAME bone weights, copied from the first of them, so the game's
    /// skinning moves them all alike and they stay in one place whatever the hand does. Weights and indices are
    /// overwritten where they sit — same bytes, same length, and the indices address this mesh's own bone table,
    /// so copying them between vertices of that mesh is sound. Returns how many vertices were rewritten.
    /// </summary>
    private static int WeldSkinning(byte[] outBytes, Source src, int mesh, VElem[] decl, uint[] vbo, byte[] bs,
                                    int[] verts)
    {
        VElem? wEl = null, iEl = null;
        foreach (var el in decl)
        {
            if (el.Usage == UseBlendWeight) wEl ??= el;
            else if (el.Usage == UseBlendIndices) iEl ??= el;
        }
        if (wEl is not { } we || iEl is not { } ie || we.Stream > 2 || ie.Stream > 2 || verts.Length < 2) return 0;

        // Eight influences on Dawntrail's wide format, four on the old one — see BlendCount.
        int wn = we.Type == 17 ? 8 : 4, inn = ie.Type == 17 ? 8 : 4;
        int WAt(int v) => src.Vb + (int)vbo[we.Stream] + v * bs[we.Stream] + we.Offset;
        int IAt(int v) => src.Vb + (int)vbo[ie.Stream] + v * bs[ie.Stream] + ie.Offset;

        int from = verts[0];
        if (WAt(from) + wn > outBytes.Length || IAt(from) + inn > outBytes.Length) return 0;
        var w = new byte[wn];
        var idx = new byte[inn];
        Array.Copy(outBytes, WAt(from), w, 0, wn);
        Array.Copy(outBytes, IAt(from), idx, 0, inn);

        int n = 0;
        foreach (int v in verts)
        {
            if (v == from) continue;
            if (WAt(v) + wn > outBytes.Length || IAt(v) + inn > outBytes.Length) continue;
            Array.Copy(w, 0, outBytes, WAt(v), wn);
            Array.Copy(idx, 0, outBytes, IAt(v), inn);
            n++;
        }
        _ = mesh;
        return n;
    }

    /// <summary>How near a flattened nail bed a non-skin island must sit to BE that nail, and go with it.</summary>
    private const float NailMeshOnBed = 0.003f;

    /// <summary>
    /// What share of an island's vertices must sit on a nail bed before the island counts as a nail. Not all: a
    /// nail's root tucks under the fold and its tip can overhang the finger.
    /// </summary>
    private const float NailMeshShare = 0.6f;

    /// <summary>
    /// Takes the nail MESHES off the hand: every island of a non-skin mesh sitting on a nail bed collapses to its
    /// own centroid, so each of its triangles has two corners in the same place, no area, and draws nothing. The
    /// vertex count, the indices and every offset in the file stay exactly as they were.
    /// </summary>
    /// <param name="nailWas">The nail beds as they stood before they were flattened.</param>
    private static (int Islands, int Verts) RemoveNailMeshes(byte[] outBytes, Source src, List<string> matNames,
                                                             List<Vec3> nailWas)
    {
        if (nailWas.Count == 0) return (0, 0);
        var s = src.S;
        var onNail = new HashSet<(int, int, int)>();
        (int, int, int) Cell(Vec3 p) => ((int)MathF.Floor(p.X / NailMeshOnBed),
                                         (int)MathF.Floor(p.Y / NailMeshOnBed),
                                         (int)MathF.Floor(p.Z / NailMeshOnBed));
        foreach (var q in nailWas) onNail.Add(Cell(q));
        bool WasNail(Vec3 q)
        {
            var (cx, cy, cz) = Cell(q);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
                if (onNail.Contains((cx + dx, cy + dy, cz + dz))) return true;
            return false;
        }

        int islands = 0, verts = 0;
        Span<float> tmp = stackalloc float[4];
        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > s.Length) break;
            ushort vc = BitConverter.ToUInt16(s, mo);
            if (vc == 0) continue;
            ushort mat = BitConverter.ToUInt16(s, mo + 8);
            // Skin stays: the bed is what bridges the socket now.
            if (mat < matNames.Count && SkinMaterialBodyType(matNames[mat]) != null) continue;

            var decl = m < src.Decls.Length ? src.Decls[m] : [];
            VElem? pe = null;
            foreach (var el in decl)
                if (el.Usage == UsePosition) { pe = el; break; }
            if (pe is not { } pel || pel.Stream > 2) continue;

            uint vbo = BitConverter.ToUInt32(s, mo + 20 + pel.Stream * 4);
            int stride = s[mo + 32 + pel.Stream];
            if (stride == 0) continue;
            int At(int i) => src.Vb + (int)vbo + i * stride + pel.Offset;

            var pos = new Vec3[vc];
            bool ok = true;
            for (int i = 0; i < vc; i++)
            {
                int a = At(i);
                if (a < 0 || a + 16 > s.Length) { ok = false; break; }
                ReadTyped(s, a, pel.Type, tmp);
                pos[i] = new Vec3(tmp[0], tmp[1], tmp[2]);
            }
            if (!ok) continue;

            // Islands by shared POSITION: one nail is one piece, whatever the UV seams do to its vertices.
            var parent = new int[vc];
            for (int i = 0; i < vc; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int x, int y) { int rx = Find(x), ry = Find(y); if (rx != ry) parent[rx] = ry; }
            var at = new Dictionary<(int, int, int), int>();
            for (int i = 0; i < vc; i++)
            {
                var k = ((int)MathF.Round(pos[i].X * 1e6f), (int)MathF.Round(pos[i].Y * 1e6f),
                         (int)MathF.Round(pos[i].Z * 1e6f));
                if (at.TryGetValue(k, out int j)) Union(i, j); else at[k] = i;
            }
            var meshTris = MeshTriangles(src, BitConverter.ToUInt16(s, mo + 10), BitConverter.ToUInt16(s, mo + 12));
            for (int t = 0; t + 2 < meshTris.Length; t += 3)
            {
                if (meshTris[t] >= vc || meshTris[t + 1] >= vc || meshTris[t + 2] >= vc) continue;
                Union(meshTris[t], meshTris[t + 1]); Union(meshTris[t + 1], meshTris[t + 2]);
            }

            var members = new Dictionary<int, List<int>>();
            for (int i = 0; i < vc; i++)
                (members.TryGetValue(Find(i), out var l) ? l : members[Find(i)] = []).Add(i);

            foreach (var group in members.Values)
            {
                int on = 0;
                foreach (int i in group) if (WasNail(pos[i])) on++;
                if (on < group.Count * NailMeshShare) continue;   // a piercing or a strap, not this nail

                float cx = 0, cy = 0, cz = 0;
                foreach (int i in group) { cx += pos[i].X; cy += pos[i].Y; cz += pos[i].Z; }
                cx /= group.Count; cy /= group.Count; cz /= group.Count;
                foreach (int i in group) WriteXYZ(outBytes, At(i), pel.Type, cx, cy, cz);
                // One skinning and one normal for the lot, or the game's skinning and the second skin's push each
                // pull the collapsed point apart again — see WeldSkinning and WeldNormals.
                uint[] vbo3 = { BitConverter.ToUInt32(s, mo + 20), BitConverter.ToUInt32(s, mo + 24),
                                BitConverter.ToUInt32(s, mo + 28) };
                byte[] bs3 = { s[mo + 32], s[mo + 33], s[mo + 34] };
                var decl3 = m < src.Decls.Length ? src.Decls[m] : [];
                WeldSkinning(outBytes, src, m, decl3, vbo3, bs3, [.. group]);
                foreach (var el in decl3)
                    if (el.Usage == UseNormal && el.Stream <= 2)
                    {
                        WeldNormals(outBytes, (e, i) => src.Vb + (int)vbo3[e.Stream] + i * bs3[e.Stream] + e.Offset,
                                    el, [.. group]);
                        break;
                    }
                islands++;
                verts += group.Count;
            }
        }
        return (islands, verts);
    }

    /// <summary>Does the garment cover the fingertip around this nail? Asked at the UV of the skin each nail vertex
    /// lands on (<see cref="NailBedPlan.FingertipUv"/>), the same question <c>RescueNailBeds</c> asks.</summary>
    private static bool FingertipCovered(SecondSkinLayer gate, NailBedPlan plan, int[] verts)
    {
        if (gate.Coverage is not { } mask || gate.CoverageWidth <= 0 || gate.CoverageHeight <= 0) return true;
        int w = gate.CoverageWidth, h = gate.CoverageHeight;
        foreach (int v in verts)
        {
            if (v >= plan.FingertipUv.Length || plan.FingertipUv[v] is not { } t) continue;
            int x0 = (int)MathF.Floor(t.U * w), y0 = (int)MathF.Floor(t.V * h);
            // A texel either way: a landing sits on the EDGE of the finger's island, and the coarse map can round it
            // just off the paint.
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = ((x0 + dx) % w + w) % w, y = ((y0 + dy) % h + h) % h;
                    if (mask[y * w + x] >= NailCoveredFloor) return true;
                }
        }
        return false;
    }

    /// <summary>Does the garment cover this nail? Asked of the island's own UVs, at its coarse coverage map.</summary>
    private static bool Covered(SecondSkinLayer gate, byte[] s, Source src, VElem? uv,
                                Func<VElem, int, int> at, byte[] bs, int[] tris, Span<float> tmp)
    {
        if (gate.Coverage is not { } mask || gate.CoverageWidth <= 0 || gate.CoverageHeight <= 0) return true;
        if (uv is not { } ue || ue.Stream > 2) return true;
        int w = gate.CoverageWidth, h = gate.CoverageHeight;
        foreach (int v in tris)
        {
            int a = at(ue, v);
            if (a < 0 || a + 16 > s.Length) continue;
            ReadTyped(s, a, ue.Type, tmp);
            int x = (((int)MathF.Floor(tmp[0] * w) % w) + w) % w;
            int y = (((int)MathF.Floor(tmp[1] * h) % h) + h) % h;
            if (mask[y * w + x] >= NailCoveredFloor) return true;
        }
        return false;
    }

    /// <summary>Area-weighted vertex normals over one island, for the vertices it moved.</summary>
    private static List<(int V, Vec3 N)> IslandNormals(Vec3[] pos, int[] tris)
    {
        var sum = new Dictionary<int, Vec3>();
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            var (a, b, c) = (pos[tris[t]], pos[tris[t + 1]], pos[tris[t + 2]]);
            var n = new Vec3((b.Y - a.Y) * (c.Z - a.Z) - (b.Z - a.Z) * (c.Y - a.Y),
                             (b.Z - a.Z) * (c.X - a.X) - (b.X - a.X) * (c.Z - a.Z),
                             (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));
            for (int e = 0; e < 3; e++)
            {
                int v = tris[t + e];
                sum[v] = sum.TryGetValue(v, out var acc)
                    ? new Vec3(acc.X + n.X, acc.Y + n.Y, acc.Z + n.Z) : n;
            }
        }
        var outp = new List<(int V, Vec3 N)>(sum.Count);
        foreach (var (v, n) in sum)
        {
            var u = NormalizeOr(n, default);
            if (u is { X: 0f, Y: 0f, Z: 0f }) continue;
            outp.Add((v, u));
        }
        return outp;
    }
}
