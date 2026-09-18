using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

using static Proteus.Services.BodyBridge;

using static Proteus.Services.MeshMath;

public static partial class SecondSkinWriter
{
    /// <summary>Largest boundary loop, in edges, that counts as a hole in the skin (a toenail socket) rather than an edge of the mesh.</summary>
    private const int CapHoleMaxEdges = 20;

    /// <summary>...and how far such a loop may reach in the atlas; a chart boundary spans the texture.</summary>
    private const float CapHoleMaxUvSpan = 0.08f;

    /// <summary>
    /// How far past a socket's own extent a landing is still compromised, as a multiple of its radius;
    /// the rim cannot be trusted, and just past it the surface is real.
    /// </summary>
    private const float CapSocketReach = 1.35f;

    /// <summary>Polar-decomposition iterations in <see cref="BestRotation"/>.</summary>
    private const int CapFitPolarSteps = 24;

    /// <summary>
    /// Smallest island, as a fraction of the largest, that the authored cap will take UVs from; keeps
    /// the feet and rejects the toenails, which carry their own UV island.
    /// </summary>
    private const float ProjectIslandFloor = 0.25f;

    /// <summary>
    /// Skinning of the body surface nearest a point, blended across the triangle it lands on. Returns
    /// empty when nothing is within <paramref name="reach"/>.
    /// </summary>
    private static (string Bone, float W)[] NearestWeights(Vec3 p, List<SkinTri> tris, float reach)
    {
        float best = reach * reach;
        (string Bone, float W)[] found = [];
        foreach (var t in tris)
        {
            float cx = t.Ctr.X - p.X, cy = t.Ctr.Y - p.Y, cz = t.Ctr.Z - p.Z;
            if (cx * cx + cy * cy + cz * cz > best + 0.01f) continue;
            var q = ClosestOnTriangle(p, t.A, t.B, t.C);
            float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
            float d = dx * dx + dy * dy + dz * dz;
            if (d >= best) continue;
            best = d;
            var (ba, bb, bc) = Barycentric(q, t.A, t.B, t.C);
            found = BlendWeights(t.Wa, ba, t.Wb, bb, t.Wc, bc);
        }
        return found;
    }

    /// <summary>
    /// UV of the body surface nearest a point, interpolated across the triangle it lands on. Null when
    /// nothing is within <paramref name="reach"/>. A welded vertex has moved, so everything it carries
    /// is re-read where it ended up.
    /// </summary>
    private static (float U, float V)? NearestUv(Vec3 p, List<SkinTri> tris, float reach)
    {
        float best = reach * reach;
        (float U, float V)? found = null;
        foreach (var t in tris)
        {
            float cx = t.Ctr.X - p.X, cy = t.Ctr.Y - p.Y, cz = t.Ctr.Z - p.Z;
            if (cx * cx + cy * cy + cz * cz > best + 0.01f) continue;
            var q = ClosestOnTriangle(p, t.A, t.B, t.C);
            float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
            float d = dx * dx + dy * dy + dz * dz;
            if (d >= best) continue;
            best = d;
            var (ba, bb, bc) = Barycentric(q, t.A, t.B, t.C);
            found = (t.Ua.U * ba + t.Ub.U * bb + t.Uc.U * bc,
                     t.Ua.V * ba + t.Ub.V * bb + t.Uc.V * bc);
        }
        return found;
    }

    /// <summary>Barycentric coordinate of a point against a triangle in UV space.</summary>
    private static (float A, float B, float C) Barycentric2(
        float u, float v, (float U, float V) a, (float U, float V) b, (float U, float V) c)
    {
        float v0u = b.U - a.U, v0v = b.V - a.V;
        float v1u = c.U - a.U, v1v = c.V - a.V;
        float den = v0u * v1v - v1u * v0v;
        // A triangle with no area in the atlas contains nothing; reporting it as (1,0,0) reads as
        // strictly inside and stops the search.
        if (MathF.Abs(den) < 1e-14f) return (1f, -1f, -1f);
        float v2u = u - a.U, v2v = v - a.V;
        float wb = (v2u * v1v - v1u * v2v) / den;
        float wc = (v0u * v2v - v2u * v0v) / den;
        return (1f - wb - wc, wb, wc);
    }

    /// <summary>
    /// Most extra push a vertex the bust or cleft bridge moved can get: 0.95 mm. The bridge lifts along one
    /// axis, and where the skin is square to it the lift slides a vertex across the surface rather than off
    /// it. Scaled by the vertex's own movement, so the extra clearance dies to zero at the region's edge.
    /// </summary>
    private const float BridgedSkinClearance = 0.00095f;

    /// <summary>
    /// Rings past a moved vertex its extra clearance reaches, each at <see cref="BridgedSpreadDecay"/> of
    /// the ring inside it; a face along the edge of the moved area tilts up from an unmoved corner that
    /// keeps only the base push.
    /// </summary>
    private const int BridgedSpreadRings = 3;

    /// <summary>Fraction of a ring's extra clearance the next ring out keeps. See <see cref="BridgedSpreadRings"/>.</summary>
    private const float BridgedSpreadDecay = 0.5f;

    /// <summary>
    /// Per-vertex extra push for a bridged mesh: each node's own movement capped at
    /// <see cref="BridgedSkinClearance"/>, then spread <see cref="BridgedSpreadRings"/> rings outward with
    /// <see cref="BridgedSpreadDecay"/> per ring, never reducing a node's own value. Over welded nodes,
    /// Jacobi, and zero wherever no moved node is within reach, so the rest of the shell is byte-identical.
    /// </summary>
    internal static float[] BridgedClearance(BustBridgePlan plan, ushort[] tris)
    {
        int vc = plan.Delta.Length, nodes = plan.NodeWeight.Length;
        var e = new float[nodes];
        for (int i = 0; i < vc; i++)
        {
            int n = plan.NodeOf[i];
            e[n] = MathF.Max(e[n], MathF.Min(Len(plan.Delta[i]), BridgedSkinClearance));
        }

        var nbr = new List<int>?[nodes];
        void Link(int a, int b)
        {
            if (a == b) return;
            (nbr[a] ??= []).Add(b);
            (nbr[b] ??= []).Add(a);
        }
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) continue;
            int a = plan.NodeOf[tris[t]], b = plan.NodeOf[tris[t + 1]], c = plan.NodeOf[tris[t + 2]];
            Link(a, b); Link(b, c); Link(c, a);
        }

        for (int ring = 0; ring < BridgedSpreadRings; ring++)
        {
            var next = (float[])e.Clone();
            for (int n = 0; n < nodes; n++)
            {
                // A part join stays exactly where the part across it is: no clearance spreads onto it.
                if (nbr[n] is not { } ns || (plan.NodePinned is { } pin && n < pin.Length && pin[n])) continue;
                foreach (int k in ns)
                    next[n] = MathF.Max(next[n], e[k] * BridgedSpreadDecay);
            }
            e = next;
        }

        var extra = new float[vc];
        for (int i = 0; i < vc; i++) extra[i] = e[plan.NodeOf[i]];
        return extra;
    }

    /// <summary>
    /// How close to the skin a vertex the bridge moved has to land before it takes that skin's weights: fully
    /// at zero distance, fading to its own weights by this far. The gap under a breast closes only when the
    /// breast moves, so the vertex must follow the skin's bones; past this it keeps its weights, or the span
    /// across the cleavage would swing with a breast.
    /// </summary>
    private const float BridgedReskinReach = 0.003f;

    /// <summary>
    /// Rewrites the blend weights of every vertex the bridge moved to within <see cref="BridgedReskinReach"/>
    /// of this mesh's own skin, blended toward the skinning at the nearest point on that skin by how close it
    /// landed. Returns how many vertices changed. This mesh's own skin and bone table, so nothing is remapped;
    /// every value is read from a snapshot, so one rewritten vertex never feeds its neighbour's answer.
    /// </summary>
    internal static int ReskinBridged(Vec3[] basePos, BustBridgePlan plan, ushort[] tris, VElem[] decl,
                                      byte[][] outStreams, byte[] outStrides)
    {
        VElem? wEl = null, iEl = null;
        foreach (var el in decl)
        {
            if (el.Usage == UseBlendWeight) wEl ??= el;
            else if (el.Usage == UseBlendIndices) iEl ??= el;
        }
        if (wEl is not { } we || iEl is not { } ie) return 0;

        int vc = basePos.Length;
        int nInf = Math.Min(BlendCount(we.Type), BlendCount(ie.Type));
        var ow = new byte[vc * nInf];
        var oi = new byte[vc * nInf];
        for (int i = 0; i < vc; i++)
        {
            Buffer.BlockCopy(outStreams[we.Stream], i * outStrides[we.Stream] + we.Offset, ow, i * nInf, nInf);
            Buffer.BlockCopy(outStreams[ie.Stream], i * outStrides[ie.Stream] + ie.Offset, oi, i * nInf, nInf);
        }

        const float cell = 0.01f;
        (int, int, int) Cell(Vec3 q) => ((int)MathF.Floor(q.X / cell), (int)MathF.Floor(q.Y / cell),
                                         (int)MathF.Floor(q.Z / cell));
        var hash = new Dictionary<(int, int, int), List<int>>();
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) continue;
            Vec3 a = basePos[tris[t]], b = basePos[tris[t + 1]], c = basePos[tris[t + 2]];
            var lo = Cell(new Vec3(MathF.Min(a.X, MathF.Min(b.X, c.X)) - BridgedReskinReach,
                                   MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - BridgedReskinReach,
                                   MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - BridgedReskinReach));
            var hi = Cell(new Vec3(MathF.Max(a.X, MathF.Max(b.X, c.X)) + BridgedReskinReach,
                                   MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) + BridgedReskinReach,
                                   MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) + BridgedReskinReach));
            for (int x = lo.Item1; x <= hi.Item1; x++)
            for (int y = lo.Item2; y <= hi.Item2; y++)
            for (int z = lo.Item3; z <= hi.Item3; z++)
                (hash.TryGetValue((x, y, z), out var l) ? l : hash[(x, y, z)] = []).Add(t);
        }

        static float Area(Vec3 x, Vec3 y, Vec3 z)
        {
            float ux = y.X - x.X, uy = y.Y - x.Y, uz = y.Z - x.Z;
            float vx = z.X - x.X, vy = z.Y - x.Y, vz = z.Z - x.Z;
            float cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            return MathF.Sqrt(cx * cx + cy * cy + cz * cz);
        }

        int changed = 0;
        var mix = new Dictionary<byte, float>();
        Span<byte> wb = stackalloc byte[8], ib = stackalloc byte[8];
        for (int i = 0; i < vc; i++)
        {
            var d = plan.Delta[i];
            if (Len(d) <= BustBridgeEpsilon) continue;
            var q = new Vec3(basePos[i].X + d.X, basePos[i].Y + d.Y, basePos[i].Z + d.Z);
            if (!hash.TryGetValue(Cell(q), out var near)) continue;

            float best = float.MaxValue;
            int bestT = -1;
            Vec3 bestP = default;
            foreach (int t in near)
            {
                var cp = ClosestOnTriangle(q, basePos[tris[t]], basePos[tris[t + 1]], basePos[tris[t + 2]]);
                float dist = Dist(q, cp);
                if (dist < best) { best = dist; bestT = t; bestP = cp; }
            }
            if (bestT < 0 || best >= BridgedReskinReach) continue;
            float toSkin = 1f - Smoothstep(best / BridgedReskinReach);
            if (toSkin <= 0f) continue;

            Vec3 ta = basePos[tris[bestT]], tb = basePos[tris[bestT + 1]], tc = basePos[tris[bestT + 2]];
            float total = Area(ta, tb, tc);
            if (total < 1e-12f) continue;
            float fa = Area(bestP, tb, tc) / total, fb = Area(ta, bestP, tc) / total;
            float fc = MathF.Max(0f, 1f - fa - fb);

            mix.Clear();
            void Add(int v, float f)
            {
                if (f <= 0f) return;
                for (int k = 0; k < nInf; k++)
                {
                    byte w = ow[v * nInf + k];
                    if (w == 0) continue;
                    byte bone = oi[v * nInf + k];
                    mix[bone] = mix.GetValueOrDefault(bone) + f * (w / 255f);
                }
            }
            Add(i, 1f - toSkin);
            Add(tris[bestT], toSkin * fa);
            Add(tris[bestT + 1], toSkin * fb);
            Add(tris[bestT + 2], toSkin * fc);

            // Strongest influences first, as many as the element holds, renormalised to exactly 255 — bytes that
            // do not sum to 255 shrink the vertex toward the origin.
            var top = mix.OrderByDescending(kv => kv.Value).Take(nInf).ToList();
            float sum = top.Sum(kv => kv.Value);
            if (sum <= 0f) continue;
            wb.Clear(); ib.Clear();
            int used = 0, bytes = 0;
            foreach (var (bone, f) in top)
            {
                byte qb = (byte)Math.Clamp((int)MathF.Round(f / sum * 255f), 0, 255);
                if (qb == 0) continue;
                ib[used] = bone; wb[used] = qb; bytes += qb; used++;
            }
            if (used == 0) continue;
            wb[0] = (byte)Math.Clamp(wb[0] + (255 - bytes), 0, 255);

            bool differs = false;
            for (int k = 0; k < nInf; k++)
                if (wb[k] != ow[i * nInf + k] || (wb[k] != 0 && ib[k] != oi[i * nInf + k])) { differs = true; break; }
            if (!differs) continue;

            int wo = i * outStrides[we.Stream] + we.Offset, io = i * outStrides[ie.Stream] + ie.Offset;
            for (int k = 0; k < nInf; k++)
            {
                outStreams[we.Stream][wo + k] = wb[k];
                outStreams[ie.Stream][io + k] = ib[k];
            }
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// The grafted cap's skinning against the cap file it came from, vertex by vertex: per-bone totals
    /// cannot see a left/right swap, which is perfect in bind pose and ruinous once the toes move.
    /// Matched authored to shipped by nearest position.
    /// </summary>
    /// <param name="shell">The built shell.</param>
    /// <param name="capMdl">The authored cap the graft was taken from.</param>
    internal static List<string> DiffCapSkinning(byte[] shell, byte[] capMdl)
    {
        var outp = new List<string>();
        Source sh, cp;
        try { sh = Parse(shell); cp = Parse(capMdl); }
        catch (Exception ex) { outp.Add($"cap skinning diff: cannot parse ({ex.Message})"); return outp; }

        // The authored side: every LOD0 mesh the cap has that carries skinning.
        var authored = new List<(Vec3 P, (string Bone, float W)[] W)>();
        for (int m = cp.Lod0MeshIndex; m < cp.Lod0MeshIndex + cp.Lod0MeshCount && m < cp.MeshCount; m++)
            authored.AddRange(ReadMeshSkinning(cp, m));
        if (authored.Count == 0) { outp.Add("cap skinning diff: authored cap has no skinning"); return outp; }

        // The shipped side: the shell's cap meshes are identified by bone-table content, since the seam
        // split changes their vertex count.
        var capBones = new HashSet<string>(authored.SelectMany(a => a.W).Select(w => w.Bone), StringComparer.Ordinal);
        for (int m = sh.Lod0MeshIndex; m < sh.Lod0MeshIndex + sh.Lod0MeshCount && m < sh.MeshCount; m++)
        {
            var got = ReadMeshSkinning(sh, m);
            if (got.Length == 0) continue;
            var mine = new HashSet<string>(got.SelectMany(g => g.W).Select(w => w.Bone), StringComparer.Ordinal);
            // A cap mesh draws its bones from the cap's set and essentially nothing else. A body mesh
            // that happens to share the toe bones still brings ankle and leg bones with it.
            int shared = mine.Count(b => capBones.Contains(b));
            if (mine.Count == 0 || shared < mine.Count * 0.8) continue;

            int domDiff = 0, setDiff = 0, sideFlip = 0, unmatched = 0;
            float worstMove = 0f;
            var examples = new List<string>();
            foreach (var (ap, aw) in authored)
            {
                if (aw.Length == 0) continue;
                int best = -1;
                float bestD = float.MaxValue;
                for (int v = 0; v < got.Length; v++)
                {
                    float dx = got[v].P.X - ap.X, dy = got[v].P.Y - ap.Y, dz = got[v].P.Z - ap.Z;
                    float d = dx * dx + dy * dy + dz * dz;
                    if (d < bestD) { bestD = d; best = v; }
                }
                if (best < 0 || bestD > CapDiffMatchRadius * CapDiffMatchRadius) { unmatched++; continue; }
                worstMove = MathF.Max(worstMove, MathF.Sqrt(bestD));

                var bw = got[best].W;
                if (bw.Length == 0) { setDiff++; continue; }
                string a0 = aw[0].Bone, b0 = bw[0].Bone;
                if (a0 != b0)
                {
                    domDiff++;
                    // The one that matters: same bone, opposite foot.
                    if (a0.Length > 2 && b0.Length > 2 && a0[..^1] == b0[..^1]
                        && (a0[^1], b0[^1]) is ('l', 'r') or ('r', 'l'))
                        sideFlip++;
                    if (examples.Count < 6)
                        examples.Add($"      ({ap.X:F4},{ap.Y:F4},{ap.Z:F4}) {a0} {aw[0].W:P0} -> {b0} {bw[0].W:P0}");
                }
                var aset = aw.Where(x => x.W > 0.02f).Select(x => x.Bone).OrderBy(x => x, StringComparer.Ordinal);
                var bset = bw.Where(x => x.W > 0.02f).Select(x => x.Bone).OrderBy(x => x, StringComparer.Ordinal);
                if (!aset.SequenceEqual(bset, StringComparer.Ordinal)) setDiff++;
            }

            outp.Add($"cap skinning diff, shell mesh {m}: {authored.Count} authored vertices, "
                   + $"{got.Length} shipped, furthest match {worstMove:F4}");
            outp.Add($"   dominant bone differs: {domDiff}   of those, LEFT/RIGHT FLIPPED: {sideFlip}");
            outp.Add($"   influence set differs: {setDiff}   unmatched beyond {CapDiffMatchRadius:F3}: {unmatched}");
            outp.AddRange(examples);
        }
        if (outp.Count == 0) outp.Add("cap skinning diff: no cap mesh found in the shell");
        return outp;
    }

    /// <summary>How far a shipped cap vertex may sit from its authored one and still be the same vertex.
    /// The graft moves them by the layer push plus the weld, both well under this.</summary>
    private const float CapDiffMatchRadius = 0.02f;

    internal static List<string> DescribeBones(byte[] mdl)
    {
        var src = Parse(mdl);
        var outp = new List<string>
        {
            $"model bones {src.BoneCount}, tables {src.BoneTables.Length}, "
          + $"submesh bone map {src.SubmeshBoneMap.Length}",
        };
        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            ushort vc = BitConverter.ToUInt16(src.S, mo);
            ushort subIdx = BitConverter.ToUInt16(src.S, mo + 10);
            ushort subCnt = BitConverter.ToUInt16(src.S, mo + 12);
            ushort tbl = BitConverter.ToUInt16(src.S, mo + 14);
            var names = tbl < src.BoneTables.Length
                ? string.Join(",", src.BoneTables[tbl].Select(b => b < src.BoneNames.Length
                                                                 ? src.BoneNames[b] : $"?{b}"))
                : "(no table)";
            outp.Add($"mesh {m}: {vc} verts, table {tbl} [{(tbl < src.BoneTables.Length ? src.BoneTables[tbl].Length : 0)}] = {names}");

            // Where this mesh's weight actually goes, by bone name: the table can name the right bones and
            // the indices still point at the wrong ones.
            var decl = m < src.Decls.Length ? src.Decls[m] : [];
            VElem? wEl = null, iEl = null;
            foreach (var el in decl)
            {
                if (el.Usage == UseBlendWeight) wEl ??= el;
                if (el.Usage == UseBlendIndices) iEl ??= el;
            }
            if (wEl is { } we && iEl is { } ie && tbl < src.BoneTables.Length)
            {
                var table = src.BoneTables[tbl];
                int nInf = BlendCount(we.Type);
                uint[] vOff = { BitConverter.ToUInt32(src.S, mo + 20), BitConverter.ToUInt32(src.S, mo + 24),
                                BitConverter.ToUInt32(src.S, mo + 28) };
                byte[] strides = { src.S[mo + 32], src.S[mo + 33], src.S[mo + 34] };
                var acc = new Dictionary<string, float>(StringComparer.Ordinal);
                for (int v = 0; v < vc; v++)
                {
                    int wa = (int)(src.Vb + vOff[we.Stream]) + v * strides[we.Stream] + we.Offset;
                    int ia = (int)(src.Vb + vOff[ie.Stream]) + v * strides[ie.Stream] + ie.Offset;
                    if (wa + nInf > src.S.Length || ia + nInf > src.S.Length) break;
                    for (int k = 0; k < nInf; k++)
                    {
                        float f = src.S[wa + k] / 255f;
                        if (f <= 0f) continue;
                        int local = src.S[ia + k];
                        string nm = local < table.Length && table[local] < src.BoneNames.Length
                            ? src.BoneNames[table[local]] : $"?{local}";
                        acc[nm] = acc.GetValueOrDefault(nm) + f;
                    }
                }
                float tot = acc.Values.Sum();
                if (tot > 0)
                    outp.Add("      weight: " + string.Join("  ", acc.OrderByDescending(k => k.Value).Take(8)
                        .Select(k => $"{k.Key} {100 * k.Value / tot:0.0}%")));
            }
            for (int s = subIdx; s < subIdx + subCnt; s++)
            {
                int so = src.SubmeshStart + s * 16;
                if (so + 16 > src.S.Length) break;
                ushort bStart = BitConverter.ToUInt16(src.S, so + 12);
                ushort bCount = BitConverter.ToUInt16(src.S, so + 14);
                var win = new List<string>();
                for (int k = bStart; k < bStart + bCount && k < src.SubmeshBoneMap.Length; k++)
                {
                    ushort b = src.SubmeshBoneMap[k];
                    win.Add(b < src.BoneNames.Length ? src.BoneNames[b] : $"?{b}");
                }
                bool overrun = bStart + bCount > src.SubmeshBoneMap.Length;
                outp.Add($"   sub {s}: bone window {bStart}+{bCount}"
                       + (overrun ? "  *** RUNS PAST THE MAP ***" : "")
                       + $" = {string.Join(",", win)}");
            }
        }
        return outp;
    }

    /// <summary>
    /// One triangle of body skin, with everything a cap vertex needs to be placed against it or read off
    /// it: geometry, atlas coordinate, normal and skinning at each corner.
    /// </summary>
    /// <summary>
    /// The surface the cap is bound to. Skin only, and it has to stay that way: a binding is an atlas
    /// coordinate, and the nails carry their own UV island, so a coordinate measured on a nail is looked up
    /// somewhere unrelated on the body. The version 2 residual covers the hole under a nail instead.
    /// </summary>
    private static List<SkinTri> BindSurface(IReadOnlyList<byte[]> bodies)
        => CollectSkinTriangles(bodies);

    /// <summary>
    /// Look one baked atlas coordinate back up on a body: which point of which triangle it names, the
    /// normal there, the skinning there, and a tangent frame for the surface. Shared by
    /// <see cref="BakeCapBind"/> and <see cref="TryPlaceCapFromBind"/>: the residual the bake stores is
    /// only a correction if both sides agree to the last bit about where the vertex lands.
    /// </summary>
    /// <returns>How far outside the winning triangle the coordinate fell; 0 means inside it.</returns>
    private static float ResolveBindLanding(IReadOnlyList<SkinTri> tris, float u, float v, int side,
                                            Vec3 face, out Vec3 at, out Vec3 nrm,
                                            out (string Bone, float W)[] w, out Vec3 tan, out Vec3 bit)
    {
        at = default; nrm = default; w = []; tan = default; bit = default;
        float best = float.MaxValue, bestFacing = -2f;
        foreach (var t in tris)
        {
            // Left and right carry their own coordinates, but a body may also mirror them onto each
            // other; the side recorded at bake time keeps the two feet apart either way.
            if (MathF.Sign(t.Ctr.X) != side && t.Ctr.X != 0f) continue;
            var tile = TileOf(t);
            var (ba, bb, bc) = Barycentric2(u + tile.U, v + tile.V, t.Ua, t.Ub, t.Uc);
            // Least-outside wins; among candidates that all contain the coordinate, the one facing the
            // way the skin faced at bake time wins. No early exit: the first triangle to contain a
            // coordinate is not necessarily the right one.
            float outside = MathF.Max(0f, -ba) + MathF.Max(0f, -bb) + MathF.Max(0f, -bc);
            if (outside > best + 1e-6f) continue;

            var n = NormalizeOr(new Vec3(t.Na.X * ba + t.Nb.X * bb + t.Nc.X * bc,
                                         t.Na.Y * ba + t.Nb.Y * bb + t.Nc.Y * bc,
                                         t.Na.Z * ba + t.Nb.Z * bb + t.Nc.Z * bc), default);
            float facing = n.X * face.X + n.Y * face.Y + n.Z * face.Z;
            if (outside > best - 1e-6f && facing <= bestFacing) continue;   // tie: keep the better facing

            best = MathF.Min(best, outside);
            bestFacing = facing;
            at = new Vec3(t.A.X * ba + t.B.X * bb + t.C.X * bc,
                          t.A.Y * ba + t.B.Y * bb + t.C.Y * bc,
                          t.A.Z * ba + t.B.Z * bb + t.C.Z * bc);
            nrm = n;
            w = BlendWeights(t.Wa, ba, t.Wb, bb, t.Wc, bc);
            (tan, bit) = UvFrame(t, n);
        }
        return best;
    }

    /// <summary>
    /// Where the body's skin has a small hole (a toenail carried on its own mesh), expressed as a disc in
    /// the atlas. Landings there are measured off the rim, whose normals fan out over the hole.
    /// </summary>
    private static List<(float U, float V, float R)> NailSocketDiscs(IReadOnlyList<SkinTri> tris)
    {
        (long, long, long) Key(Vec3 q) => ((long)MathF.Round(q.X * 1e5f), (long)MathF.Round(q.Y * 1e5f),
                                           (long)MathF.Round(q.Z * 1e5f));
        var count = new Dictionary<((long, long, long), (long, long, long)), int>();
        var uvOf = new Dictionary<(long, long, long), (float U, float V)>();
        void Edge(Vec3 x, Vec3 y)
        {
            var (kx, ky) = (Key(x), Key(y));
            var k = kx.CompareTo(ky) <= 0 ? (kx, ky) : (ky, kx);
            count[k] = count.GetValueOrDefault(k) + 1;
        }
        foreach (var t in tris)
        {
            // Tile-normalised, the same way a landing is resolved, or a raw coordinate silently never matches
            // a baked one.
            var tile = TileOf(t);
            uvOf[Key(t.A)] = (t.Ua.U - tile.U, t.Ua.V - tile.V);
            uvOf[Key(t.B)] = (t.Ub.U - tile.U, t.Ub.V - tile.V);
            uvOf[Key(t.C)] = (t.Uc.U - tile.U, t.Uc.V - tile.V);
            Edge(t.A, t.B); Edge(t.B, t.C); Edge(t.C, t.A);
        }

        var side = new Dictionary<(long, long, long), List<(long, long, long)>>();
        foreach (var (k, n) in count)
        {
            if (n != 1) continue;
            (side.TryGetValue(k.Item1, out var l1) ? l1 : side[k.Item1] = []).Add(k.Item2);
            (side.TryGetValue(k.Item2, out var l2) ? l2 : side[k.Item2] = []).Add(k.Item1);
        }

        var discs = new List<(float, float, float)>();
        var seen = new HashSet<(long, long, long)>();
        foreach (var start in side.Keys)
        {
            if (!seen.Add(start)) continue;
            var loop = new List<(long, long, long)> { start };
            var at = start;
            var from = (long.MinValue, 0L, 0L);
            while (true)
            {
                var next = (long.MinValue, 0L, 0L);
                bool got = false;
                foreach (var cand in side[at])
                    if (!cand.Equals(from) && !seen.Contains(cand)) { next = cand; got = true; break; }
                if (!got) break;
                seen.Add(next); loop.Add(next);
                from = at; at = next;
                if (loop.Count > CapHoleMaxEdges) break;
            }
            if (loop.Count < 3 || loop.Count > CapHoleMaxEdges) continue;

            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            bool all = true;
            foreach (var k in loop)
            {
                if (!uvOf.TryGetValue(k, out var q)) { all = false; break; }
                u0 = MathF.Min(u0, q.U); u1 = MathF.Max(u1, q.U);
                v0 = MathF.Min(v0, q.V); v1 = MathF.Max(v1, q.V);
            }
            if (!all || u1 - u0 > CapHoleMaxUvSpan || v1 - v0 > CapHoleMaxUvSpan) continue;
            discs.Add(((u0 + u1) * 0.5f, (v0 + v1) * 0.5f,
                       MathF.Max(u1 - u0, v1 - v0) * 0.5f * CapSocketReach));
        }
        return discs;
    }

    /// <summary>
    /// The rotation carrying one set of points onto another, by Kabsch. Solved as a polar decomposition
    /// rather than an SVD - iterating M -> (M + M^-T)/2 converges on the orthogonal factor in a handful
    /// of steps and needs nothing but a 3x3 inverse.
    /// </summary>
    private static float[] BestRotation(IReadOnlyList<Vec3> from, IReadOnlyList<Vec3> to,
                                        Vec3 cFrom, Vec3 cTo)
    {
        var h = new float[9];
        for (int i = 0; i < from.Count && i < to.Count; i++)
        {
            float ax = from[i].X - cFrom.X, ay = from[i].Y - cFrom.Y, az = from[i].Z - cFrom.Z;
            float bx = to[i].X - cTo.X, by = to[i].Y - cTo.Y, bz = to[i].Z - cTo.Z;
            h[0] += ax * bx; h[1] += ax * by; h[2] += ax * bz;
            h[3] += ay * bx; h[4] += ay * by; h[5] += ay * bz;
            h[6] += az * bx; h[7] += az * by; h[8] += az * bz;
        }
        float Det(float[] m) => m[0] * (m[4] * m[8] - m[5] * m[7])
                              - m[1] * (m[3] * m[8] - m[5] * m[6])
                              + m[2] * (m[3] * m[7] - m[4] * m[6]);
        var r = (float[])h.Clone();
        for (int it = 0; it < CapFitPolarSteps; it++)
        {
            float d = Det(r);
            if (MathF.Abs(d) < 1e-20f) return [1, 0, 0, 0, 1, 0, 0, 0, 1];
            // inverse-transpose of r
            var inv = new float[9];
            inv[0] = (r[4] * r[8] - r[5] * r[7]) / d;
            inv[1] = (r[2] * r[7] - r[1] * r[8]) / d;
            inv[2] = (r[1] * r[5] - r[2] * r[4]) / d;
            inv[3] = (r[5] * r[6] - r[3] * r[8]) / d;
            inv[4] = (r[0] * r[8] - r[2] * r[6]) / d;
            inv[5] = (r[2] * r[3] - r[0] * r[5]) / d;
            inv[6] = (r[3] * r[7] - r[4] * r[6]) / d;
            inv[7] = (r[1] * r[6] - r[0] * r[7]) / d;
            inv[8] = (r[0] * r[4] - r[1] * r[3]) / d;
            var next = new float[9];
            for (int k = 0; k < 3; k++)
                for (int j = 0; j < 3; j++)
                    next[k * 3 + j] = 0.5f * (r[k * 3 + j] + inv[j * 3 + k]);
            float move = 0f;
            for (int k = 0; k < 9; k++) move += MathF.Abs(next[k] - r[k]);
            r = next;
            if (move < 1e-7f) break;
        }
        // H is built as (from)^T(to), so the rotation that carries `from` onto `to` is its transpose.
        return [r[0], r[3], r[6], r[1], r[4], r[7], r[2], r[5], r[8]];
    }

    /// <summary>
    /// A tangent frame for a triangle, taken from its UV parameterisation rather than its edges, so a
    /// residual stored in it means the same thing on any body laid out in the same atlas, however posed.
    /// </summary>
    private static (Vec3 T, Vec3 B) UvFrame(SkinTri t, Vec3 n)
    {
        float du1 = t.Ub.U - t.Ua.U, dv1 = t.Ub.V - t.Ua.V;
        float du2 = t.Uc.U - t.Ua.U, dv2 = t.Uc.V - t.Ua.V;
        var e1 = new Vec3(t.B.X - t.A.X, t.B.Y - t.A.Y, t.B.Z - t.A.Z);
        var e2 = new Vec3(t.C.X - t.A.X, t.C.Y - t.A.Y, t.C.Z - t.A.Z);
        float det = du1 * dv2 - du2 * dv1;
        Vec3 tan;
        if (MathF.Abs(det) < 1e-12f)
            tan = NormalizeOr(e1, new Vec3(1, 0, 0));
        else
        {
            float rr = 1f / det;
            tan = NormalizeOr(new Vec3((e1.X * dv2 - e2.X * dv1) * rr,
                                       (e1.Y * dv2 - e2.Y * dv1) * rr,
                                       (e1.Z * dv2 - e2.Z * dv1) * rr), e1);
        }
        float d = tan.X * n.X + tan.Y * n.Y + tan.Z * n.Z;      // Gram-Schmidt against the normal
        tan = NormalizeOr(new Vec3(tan.X - n.X * d, tan.Y - n.Y * d, tan.Z - n.Z * d), tan);
        var bitan = new Vec3(n.Y * tan.Z - n.Z * tan.Y,
                             n.Z * tan.X - n.X * tan.Z,
                             n.X * tan.Y - n.Y * tan.X);
        return (tan, bitan);
    }

    private readonly record struct SkinTri(
        Vec3 A, Vec3 B, Vec3 C,
        (float U, float V) Ua, (float U, float V) Ub, (float U, float V) Uc,
        Vec3 Na, Vec3 Nb, Vec3 Nc,
        (string Bone, float W)[] Wa, (string Bone, float W)[] Wb, (string Bone, float W)[] Wc,
        Vec3 Ctr);

    /// <summary>
    /// Every body's LOD0 skin triangles in one list, with the toenail islands dropped: they carry their own
    /// UV island, and a cap triangle with one corner on a nail stretches across the gap between islands.
    /// </summary>
    /// <param name="skinOnly">See <see cref="TryReadLod0Geometry"/>.</param>
    /// <param name="dropIslands">
    /// False keeps the small connected components: dropped when collecting a surface to take UV from, kept
    /// when collecting a surface to take skinning from, since a nail the cap covers is what it must follow.
    /// </param>
    private static List<SkinTri> CollectSkinTriangles(IReadOnlyList<byte[]> bodies, bool skinOnly = true,
                                                      bool dropIslands = true, bool nonSkin = false)
    {
        var tri = new List<SkinTri>();
        foreach (var body in bodies)
        {
            if (!TryReadLod0Geometry(body, out var bp, out var bu, out var bt, out var bw, out var bn,
                                     skinOnly, nonSkin))
                continue;

            int nv = bp.Length / 3;
            var parent = new int[nv];
            for (int i = 0; i < nv; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int x, int y) { int rx = Find(x), ry = Find(y); if (rx != ry) parent[rx] = ry; }
            for (int t = 0; t + 2 < bt.Length; t += 3)
            {
                if (bt[t] >= nv || bt[t + 1] >= nv || bt[t + 2] >= nv) continue;
                Union(bt[t], bt[t + 1]); Union(bt[t + 1], bt[t + 2]);
            }
            var size = new Dictionary<int, int>();
            for (int i = 0; i < nv; i++) { int r = Find(i); size[r] = size.GetValueOrDefault(r) + 1; }
            int biggest = 0;
            foreach (int v in size.Values) biggest = Math.Max(biggest, v);
            // A foot is within a fraction of the other foot's size; a nail is a small fraction of either.
            int keepAbove = dropIslands ? (int)(biggest * ProjectIslandFloor) : 0;

            Vec3 P(int i) => new(bp[i * 3], bp[i * 3 + 1], bp[i * 3 + 2]);
            Vec3 N(int i) => i * 3 + 2 < bn.Length ? new Vec3(bn[i * 3], bn[i * 3 + 1], bn[i * 3 + 2]) : default;
            (float, float) U(int i) => (bu[i * 2], bu[i * 2 + 1]);
            (string, float)[] W(int i) => i < bw.Length ? bw[i] : [];

            for (int t = 0; t + 2 < bt.Length; t += 3)
            {
                int a = bt[t], b = bt[t + 1], c = bt[t + 2];
                if ((a + 1) * 3 > bp.Length || (b + 1) * 3 > bp.Length || (c + 1) * 3 > bp.Length) continue;
                if (size.GetValueOrDefault(Find(a)) < keepAbove) continue;
                var (pa, pb, pc) = (P(a), P(b), P(c));
                tri.Add(new SkinTri(pa, pb, pc, U(a), U(b), U(c), N(a), N(b), N(c), W(a), W(b), W(c),
                                    new Vec3((pa.X + pb.X + pc.X) / 3f, (pa.Y + pb.Y + pc.Y) / 3f,
                                             (pa.Z + pb.Z + pc.Z) / 3f)));
            }
        }
        return tri;
    }

    /// <summary>Barycentric coordinate of <paramref name="q"/> in the plane of a triangle.</summary>
    private static (float A, float B, float C) Barycentric(Vec3 q, Vec3 a, Vec3 b, Vec3 c)
    {
        float v0x = b.X - a.X, v0y = b.Y - a.Y, v0z = b.Z - a.Z;
        float v1x = c.X - a.X, v1y = c.Y - a.Y, v1z = c.Z - a.Z;
        float v2x = q.X - a.X, v2y = q.Y - a.Y, v2z = q.Z - a.Z;
        float d00 = v0x * v0x + v0y * v0y + v0z * v0z;
        float d01 = v0x * v1x + v0y * v1y + v0z * v1z;
        float d11 = v1x * v1x + v1y * v1y + v1z * v1z;
        float d20 = v2x * v0x + v2y * v0y + v2z * v0z;
        float d21 = v2x * v1x + v2y * v1y + v2z * v1z;
        float den = d00 * d11 - d01 * d01;
        if (MathF.Abs(den) < 1e-20f) return (1f, 0f, 0f);
        float wb = (d11 * d20 - d01 * d21) / den;
        float wc = (d00 * d21 - d01 * d20) / den;
        return (1f - wb - wc, wb, wc);
    }
}
