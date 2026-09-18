using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

using static Proteus.Services.BodyBridge;

using static Proteus.Services.ToeCapSolver;

using static Proteus.Services.MeshMath;

public static partial class SecondSkinWriter
{
    /// <summary>Fraction of its original area a capped triangle must keep to survive; relative, so a dense body is not culled for small triangles.</summary>
    private const float DegenerateAreaFraction = 0.02f;

    /// <summary>Movement below which a vertex counts as untouched, matching the cap's own reporting.</summary>
    private const float DegenerateMoveEpsilon = 1e-7f;

    /// <summary>Distance at which two capped corners count as the same point — the weld's own grid.</summary>
    private const float DegenerateWeldDistance = 1e-5f;

    /// <summary>
    /// One segment of a join, carrying everything both sides have to agree on: where it is, which way it
    /// faces, and what it is skinned to. The weights travel with it because a weld without them holds
    /// only in bind pose.
    /// </summary>
    /// <param name="UA">The cap's own UV at this end, so a lip vertex landing anywhere along the segment
    /// can take the coordinate the cap will sample there.</param>
    private readonly record struct RimSeg(
        Vec3 PA, Vec3 NA, (string Bone, float W)[] WA,
        Vec3 PB, Vec3 NB, (string Bone, float W)[] WB,
        (float U, float V) UA = default, (float U, float V) UB = default,
        bool HasUv = false);

    /// <summary>
    /// Put a vertex on the cap's boundary wherever a shell vertex was welded onto it, so the two
    /// boundaries share positions. A boundary edge belongs to one triangle, so a split is a fan; nothing moves.
    /// </summary>
    /// <returns>How many vertices were inserted.</returns>
    /// <param name="atVertex">Landings skipped because the boundary already has a vertex there.</param>
    /// <param name="offBoundary">Landings skipped because no boundary edge was within reach.</param>
    /// <summary>
    /// Split every open-boundary edge at every given position lying on it, so two runs of boundary that
    /// share vertices end up sharing edges as well. The inverse of <see cref="SplitCapRim"/>: per edge,
    /// which positions lie on me. Nothing moves.
    /// </summary>
    /// <summary>How many of the last stitch's split points reused a vertex already in the mesh.</summary>
    private static int StitchShared;

    private static int StitchBoundaryAt(IReadOnlyList<Vec3> at, VElem[] decl, ref byte[][] streams,
                                        byte[] strides, ref ushort vc, List<ushort[]> keptPerSub,
                                        ref bool[] used)
    {
        var edgeUse = new Dictionary<(ushort A, ushort B), int>();
        foreach (var sub in keptPerSub)
            for (int t = 0; t + 2 < sub.Length; t += 3)
                foreach (var (x, y) in new[] { (sub[t], sub[t + 1]), (sub[t + 1], sub[t + 2]), (sub[t + 2], sub[t]) })
                {
                    var e = x < y ? (x, y) : (y, x);
                    edgeUse[e] = edgeUse.GetValueOrDefault(e) + 1;
                }
        var boundary = edgeUse.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
        if (boundary.Count == 0 || at.Count == 0) return 0;

        VElem? pEl = null;
        foreach (var el in decl) if (el.Usage == UsePosition) { pEl = el; break; }
        if (pEl is not { } pe) return 0;

        var posStream = streams[pe.Stream];
        int posStride = strides[pe.Stream];
        Vec3 PosOf(int v)
        {
            Span<float> tmp = stackalloc float[4];
            ReadTyped(posStream, v * posStride + pe.Offset, pe.Type, tmp);
            return new Vec3(tmp[0], tmp[1], tmp[2]);
        }

        var cuts = new Dictionary<(ushort A, ushort B), List<(float T, Vec3 P)>>();
        foreach (var e in boundary)
        {
            var a = PosOf(e.A);
            var b = PosOf(e.B);
            float ex = b.X - a.X, ey = b.Y - a.Y, ez = b.Z - a.Z;
            float len2 = ex * ex + ey * ey + ez * ez;
            if (len2 < 1e-20f) continue;
            float edgeLen = MathF.Sqrt(len2);
            float margin = CapRimSplitMargin / edgeLen;
            foreach (var p in at)
            {
                float t = ((p.X - a.X) * ex + (p.Y - a.Y) * ey + (p.Z - a.Z) * ez) / len2;
                if (t <= margin || t >= 1f - margin) continue;   // an endpoint already, or too close to one
                float qx = a.X + ex * t, qy = a.Y + ey * t, qz = a.Z + ez * t;
                float d2 = (p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy) + (p.Z - qz) * (p.Z - qz);
                if (d2 > CapRimSplitMargin * CapRimSplitMargin) continue;   // not on this edge
                (cuts.TryGetValue(e, out var l) ? l : cuts[e] = new List<(float, Vec3)>()).Add((t, p));
            }
        }
        if (cuts.Count == 0) return 0;

        int newCount = cuts.Sum(kv => kv.Value.Count);
        int baseV = vc;
        for (int st = 0; st < streams.Length; st++)
        {
            if (streams[st] == null || strides[st] == 0) continue;
            var g = new byte[(vc + newCount) * strides[st]];
            Buffer.BlockCopy(streams[st], 0, g, 0, vc * strides[st]);
            streams[st] = g;
        }
        var grownUsed = new bool[vc + newCount];
        Array.Copy(used, grownUsed, vc);
        used = grownUsed;

        // Reuse the vertex that is already there: inserting a new vertex at a position the mesh already
        // occupies leaves two indices on one point, and the two runs still do not share the edge.
        var already = new Dictionary<(int, int, int), ushort>();
        for (ushort i = 0; i < baseV; i++)
        {
            if (!used[i]) continue;
            var q = PosOf(i);
            already[QuantPos(q.X, q.Y, q.Z)] = i;
        }

        int next = baseV;
        int shared = 0;
        var inserted = new Dictionary<(ushort A, ushort B), List<(float T, ushort V)>>();
        foreach (var (e, list) in cuts)
        {
            list.Sort((x, y) => x.T.CompareTo(y.T));
            var made = new List<(float, ushort)>();
            foreach (var (t, p) in list)
            {
                if (already.TryGetValue(QuantPos(p.X, p.Y, p.Z), out ushort reuseAt))
                { made.Add((t, reuseAt)); shared++; continue; }

                ushort nv = (ushort)next++;
                LerpVertex(decl, streams, strides, e.A, e.B, t, nv);
                WriteXYZ(streams[pe.Stream], nv * strides[pe.Stream] + pe.Offset, pe.Type, p.X, p.Y, p.Z);
                used[nv] = true;
                already[QuantPos(p.X, p.Y, p.Z)] = nv;
                made.Add((t, nv));
            }
            inserted[e] = made;
        }
        vc = (ushort)next;
        StitchShared = shared;

        for (int su = 0; su < keptPerSub.Count; su++)
        {
            var sub = keptPerSub[su];
            var outp = new List<ushort>(sub.Length);
            for (int t = 0; t + 2 < sub.Length; t += 3)
            {
                ushort a = sub[t], b = sub[t + 1], c = sub[t + 2];
                bool done = false;
                for (int k = 0; k < 3 && !done; k++)
                {
                    (ushort x, ushort y, ushort opp) = k switch
                    {
                        0 => (a, b, c),
                        1 => (b, c, a),
                        _ => (c, a, b),
                    };
                    var e = x < y ? (A: x, B: y) : (A: y, B: x);
                    if (!inserted.TryGetValue(e, out var pts)) continue;
                    var seq = new List<ushort> { x };
                    if (x == e.A) seq.AddRange(pts.Select(q => q.V));
                    else for (int i = pts.Count - 1; i >= 0; i--) seq.Add(pts[i].V);
                    seq.Add(y);
                    for (int i = 0; i + 1 < seq.Count; i++)
                    { outp.Add(seq[i]); outp.Add(seq[i + 1]); outp.Add(opp); }
                    done = true;
                }
                if (!done) { outp.Add(a); outp.Add(b); outp.Add(c); }
            }
            keptPerSub[su] = outp.ToArray();
        }
        return newCount;
    }

    /// <summary>
    /// Triangulate any open boundary loop of at most <see cref="SmallHoleEdges"/> edges; a small stray hole
    /// along the join shows in game as a bright polygon of bare skin. Bounded deliberately: toenail sockets
    /// must stay open. Winding is taken from the triangle that owns each boundary edge and reversed.
    /// </summary>
    private static int FillSmallHoles(List<ushort[]> keptPerSub, ushort vc, ref bool[] used, int maxEdges)
    {
        var dir = new Dictionary<(ushort, ushort), int>();
        foreach (var sub in keptPerSub)
            for (int t = 0; t + 2 < sub.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    ushort x = sub[t + k], y = sub[t + (k + 1) % 3];
                    var e = (Math.Min(x, y), Math.Max(x, y));
                    dir[e] = dir.GetValueOrDefault(e) + 1;
                }
        var open = new HashSet<(ushort, ushort)>();
        foreach (var (e, n) in dir) if (n == 1) open.Add(e);
        if (open.Count == 0) return 0;

        // The direction each boundary edge is traversed by the triangle that owns it.
        var next = new Dictionary<ushort, ushort>();
        foreach (var sub in keptPerSub)
            for (int t = 0; t + 2 < sub.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    ushort x = sub[t + k], y = sub[t + (k + 1) % 3];
                    if (open.Contains((Math.Min(x, y), Math.Max(x, y)))) next[x] = y;
                }

        var seen = new HashSet<ushort>();
        var fill = new List<ushort>();
        int closed = 0;
        foreach (var start in next.Keys.ToList())
        {
            if (seen.Contains(start)) continue;
            var loop = new List<ushort>();
            ushort at = start;
            while (loop.Count <= maxEdges + 1)
            {
                if (!next.TryGetValue(at, out ushort nx)) { loop.Clear(); break; }
                loop.Add(at);
                if (nx == start) break;
                at = nx;
            }
            if (loop.Count < 3 || loop.Count > maxEdges) continue;
            if (loop.Any(seen.Contains)) continue;
            foreach (var v in loop) seen.Add(v);
            // Reversed against the owning triangles, so the patch faces outward like its neighbours.
            for (int i = 1; i + 1 < loop.Count; i++)
            { fill.Add(loop[0]); fill.Add(loop[i + 1]); fill.Add(loop[i]); }
            closed++;
        }
        if (fill.Count == 0) return 0;

        int host = 0;
        for (int su = 1; su < keptPerSub.Count; su++)
            if (keptPerSub[su].Length > keptPerSub[host].Length) host = su;
        var grown = new List<ushort>(keptPerSub[host]);
        grown.AddRange(fill);
        keptPerSub[host] = grown.ToArray();
        foreach (var v in fill) if (v < used.Length) used[v] = true;
        return closed;
    }

    /// <summary>
    /// Fill every open boundary loop of the shell that is short (under <see cref="SocketMaxPerimeter"/> round) and lies
    /// on hip-owned skin. Loops are traced over vertices welded by position, because a socket's rim crosses UV seams.
    /// Each patch is a fan wound against the triangles that own the rim. Returns loops closed.
    /// Only holes the body has: <paramref name="bodyTris"/> is the whole drawn list before coverage trimming, and a
    /// loop bordering a trimmed triangle is the garment's own cut and is left open.
    /// </summary>
    private static int CloseHipSockets(List<ushort[]> keptPerSub, ushort[] bodyTris, VElem[] decl, byte[][] streams,
                                       byte[] strides, ushort vc, float[] hipW, ref bool[] used, out int trisAdded)
    {
        trisAdded = 0;
        VElem? pEl = null;
        foreach (var el in decl) if (el.Usage == UsePosition) { pEl = el; break; }
        if (pEl is not { } pe) return 0;

        Span<float> tmp = stackalloc float[4];
        var pos = new Vec3[vc];
        var weldOf = new int[vc];
        var repOf = new List<ushort>();
        var weldKey = new Dictionary<(long, long, long), int>();
        for (int v = 0; v < vc; v++)
        {
            ReadTyped(streams[pe.Stream], v * strides[pe.Stream] + pe.Offset, pe.Type, tmp);
            pos[v] = new Vec3(tmp[0], tmp[1], tmp[2]);
            var k = ((long)MathF.Round(tmp[0] / JoinWeld), (long)MathF.Round(tmp[1] / JoinWeld), (long)MathF.Round(tmp[2] / JoinWeld));
            if (!weldKey.TryGetValue(k, out int id)) { weldKey[k] = id = repOf.Count; repOf.Add((ushort)v); }
            weldOf[v] = id;
            if (used.Length > v && used[v] && !(used.Length > repOf[id] && used[repOf[id]])) repOf[id] = (ushort)v;
        }

        var uses = new Dictionary<(int, int), int>();
        foreach (var sub in keptPerSub)
            for (int t = 0; t + 2 < sub.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = weldOf[sub[t + k]], b = weldOf[sub[t + (k + 1) % 3]];
                    if (a == b) continue;
                    var e = (Math.Min(a, b), Math.Max(a, b));
                    uses[e] = uses.GetValueOrDefault(e) + 1;
                }
        // Open edges as the owning triangle walks them, several per vertex where two holes share a rim vertex.
        var outgoing = new Dictionary<int, List<int>>();
        foreach (var sub in keptPerSub)
            for (int t = 0; t + 2 < sub.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = weldOf[sub[t + k]], b = weldOf[sub[t + (k + 1) % 3]];
                    if (a == b || uses.GetValueOrDefault((Math.Min(a, b), Math.Max(a, b))) != 1) continue;
                    if (!outgoing.TryGetValue(a, out var list)) outgoing[a] = list = new List<int>();
                    if (!list.Contains(b)) list.Add(b);
                }
        if (outgoing.Count == 0) return 0;

        // Edges the coverage trim opened: an edge of a body triangle that is not among the kept ones.
        (int, int, int) Tri(int a, int b, int c)
        {
            int lo = Math.Min(a, Math.Min(b, c)), hi = Math.Max(a, Math.Max(b, c));
            return (lo, a + b + c - lo - hi, hi);
        }
        var keptTris = new HashSet<(int, int, int)>();
        foreach (var sub in keptPerSub)
            for (int t = 0; t + 2 < sub.Length; t += 3)
                keptTris.Add(Tri(weldOf[sub[t]], weldOf[sub[t + 1]], weldOf[sub[t + 2]]));
        var trimmedEdge = new HashSet<(int, int)>();
        for (int t = 0; t + 2 < bodyTris.Length; t += 3)
        {
            if (bodyTris[t] >= vc || bodyTris[t + 1] >= vc || bodyTris[t + 2] >= vc) continue;
            int a = weldOf[bodyTris[t]], b = weldOf[bodyTris[t + 1]], c = weldOf[bodyTris[t + 2]];
            if (a == b || b == c || a == c || keptTris.Contains(Tri(a, b, c))) continue;
            trimmedEdge.Add((Math.Min(a, b), Math.Max(a, b)));
            trimmedEdge.Add((Math.Min(b, c), Math.Max(b, c)));
            trimmedEdge.Add((Math.Min(a, c), Math.Max(a, c)));
        }

        // Hip weight per welded vertex: the most any copy carries.
        var hipOf = new float[repOf.Count];
        for (int v = 0; v < vc && v < hipW.Length; v++) hipOf[weldOf[v]] = MathF.Max(hipOf[weldOf[v]], hipW[v]);

        var walked = new HashSet<(int, int)>();
        var fill = new List<ushort>();
        int closed = 0;
        int added = 0;
        void FanLoop(List<int> simple)
        {
            for (int i = 1; i + 1 < simple.Count; i++)
            {
                fill.Add(repOf[simple[0]]); fill.Add(repOf[simple[i + 1]]); fill.Add(repOf[simple[i]]);
                added++;
            }
        }
        foreach (var (first, targets) in outgoing.ToList())
        foreach (int firstTo in targets)
        {
            if (walked.Contains((first, firstTo))) continue;
            var loop = new List<int>();
            var loopEdges = new List<(int, int)>();
            int at = first, to = firstTo;
            float perimeter = 0f;
            bool ok = true;
            while (true)
            {
                loop.Add(at);
                loopEdges.Add((at, to));
                perimeter += Dist(pos[repOf[at]], pos[repOf[to]]);
                if (perimeter > SocketMaxPerimeter || loop.Count > SocketMaxEdges) { ok = false; break; }
                if (to == first) break;
                if (!outgoing.TryGetValue(to, out var outs)) { ok = false; break; }
                int pick = -1;
                foreach (int o in outs)
                    if (!walked.Contains((to, o)) && !loopEdges.Contains((to, o))) { pick = o; break; }
                if (pick < 0) { ok = false; break; }
                at = to;
                to = pick;
            }
            foreach (var e in loopEdges) walked.Add(e);
            if (!ok || loop.Count < 3) continue;
            if (loop.Average(v => hipOf[v]) < SocketMinHip) continue;
            // The garment's own cut, not the body's socket.
            if (loopEdges.Any(e => trimmedEdge.Contains((Math.Min(e.Item1, e.Item2), Math.Max(e.Item1, e.Item2))))) continue;
            // A loop that passes through a vertex twice is two holes touching, and one fan across it overlaps itself:
            // split at each repeat into simple loops, and fan each.
            var stack = new List<int>();
            foreach (int v in loop)
            {
                int j = stack.IndexOf(v);
                if (j < 0) { stack.Add(v); continue; }
                FanLoop(stack.GetRange(j, stack.Count - j));
                stack.RemoveRange(j + 1, stack.Count - j - 1);
            }
            FanLoop(stack);
            closed++;
        }
        trisAdded = added;   // an out parameter cannot be written from the local function
        if (fill.Count == 0) return 0;

        int host = 0;
        for (int su = 1; su < keptPerSub.Count; su++)
            if (keptPerSub[su].Length > keptPerSub[host].Length) host = su;
        var grown = new List<ushort>(keptPerSub[host]);
        grown.AddRange(fill);
        keptPerSub[host] = grown.ToArray();
        foreach (var v in fill) if (v < used.Length) used[v] = true;
        return closed;
    }

    /// <summary>Longest boundary loop, round, that <see cref="CloseHipSockets"/> treats as a socket rather than a
    /// garment edge.</summary>
    private const float SocketMaxPerimeter = 0.08f;

    /// <summary>Most edges a socket loop may have.</summary>
    private const int SocketMaxEdges = 96;

    /// <summary>Mean hip-bone weight a loop's rim needs to count as a socket on the hips.</summary>
    private const float SocketMinHip = 0.3f;

    /// <summary>Nearest point on the body's skin, and how far away it is. Null when nothing is in reach.</summary>
    private static bool NearestOnSkin(Vec3 p, List<SkinTri> tris, float reach, out Vec3 at)
    {
        float best = reach * reach;
        at = default;
        bool got = false;
        foreach (var t in tris)
        {
            float cx = t.Ctr.X - p.X, cy = t.Ctr.Y - p.Y, cz = t.Ctr.Z - p.Z;
            if (cx * cx + cy * cy + cz * cz > best + 0.01f) continue;
            var q = ClosestOnTriangle(p, t.A, t.B, t.C);
            float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
            float d = dx * dx + dy * dy + dz * dz;
            if (d >= best) continue;
            best = d; at = q; got = true;
        }
        return got;
    }

    /// <summary>
    /// A position as an exact-match key, at 1e-6 of a metre; two vertices with the same key are the same
    /// point and the graft gives them one index.
    /// </summary>
    private static (int, int, int) QuantPos(float x, float y, float z)
        => ((int)MathF.Round(x * 1e6f), (int)MathF.Round(y * 1e6f), (int)MathF.Round(z * 1e6f));

    /// <summary>
    /// Append one vertex to every stream and return its index, copied byte-for-byte from
    /// <paramref name="template"/>: a zeroed vertex has colour 0, which the gear shaders gate on, and zero
    /// bone weights, which collapse it onto the model origin.
    /// </summary>
    private static ushort GrowOne(ref byte[][] streams, byte[] strides, ref ushort vc, ref bool[] used,
                                  ushort template)
    {
        for (int st = 0; st < streams.Length; st++)
        {
            if (streams[st] == null || strides[st] == 0) continue;
            var g = new byte[(vc + 1) * strides[st]];
            Buffer.BlockCopy(streams[st], 0, g, 0, vc * strides[st]);
            if (template < vc)
                Buffer.BlockCopy(g, template * strides[st], g, vc * strides[st], strides[st]);
            streams[st] = g;
        }
        var u = new bool[vc + 1];
        Array.Copy(used, u, vc);
        used = u;
        used[vc] = true;
        return vc++;
    }

    /// <summary>
    /// Write one vertex's skinning from bone NAMES into a mesh's own table, growing the table on demand.
    /// The same route the welded lip takes, so a merged mesh is served by a single table.
    /// </summary>
    private static void WriteSkinNamed(byte[][] streams, byte[] strides, VElem wEl, VElem iEl, ushort v,
                                       (string Bone, float W)[] w, Dictionary<string, int> slot,
                                       List<ushort> table, Dictionary<string, ushort> boneIndex)
    {
        if (w.Length == 0) return;
        int nInf = BlendCount(wEl.Type);
        Span<byte> wb = stackalloc byte[8], ib = stackalloc byte[8];
        wb.Clear(); ib.Clear();
        int used = 0, total = 0;
        foreach (var (bone, f) in w)
        {
            if (used == nInf) break;
            if (!slot.TryGetValue(bone, out int at))
            {
                if (!boneIndex.TryGetValue(bone, out var ui)) continue;
                if (table.Count >= 255) continue;
                slot[bone] = at = table.Count;
                table.Add(ui);
            }
            byte q = (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);
            if (q == 0) continue;
            ib[used] = (byte)at; wb[used] = q; total += q;
            used++;
        }
        if (used == 0) return;
        // The bytes must come to 255 or the vertex shrinks toward the origin.
        wb[0] = (byte)Math.Clamp(wb[0] + (255 - total), 0, 255);
        int wo = v * strides[wEl.Stream] + wEl.Offset;
        int io = v * strides[iEl.Stream] + iEl.Offset;
        for (int q2 = 0; q2 < nInf; q2++)
        {
            streams[wEl.Stream][wo + q2] = wb[q2];
            streams[iEl.Stream][io + q2] = ib[q2];
        }
    }

    /// <summary>
    /// After a split: how many landings still have no boundary vertex exactly on them. The target is zero
    /// on both sides of the join; a T-junction leaves a sliver.
    /// </summary>
    private static void JoinAudit(string what, List<Vec3> landings, List<ushort[]> keptPerSub,
                                  VElem[] decl, byte[][] streams, byte[] strides, ushort vc,
                                  Action<string>? diag)
    {
        if (diag == null || landings.Count == 0) return;
        VElem? pEl = null;
        foreach (var el in decl) if (el.Usage == UsePosition) { pEl = el; break; }
        if (pEl is not { } pe) return;

        var uses = new Dictionary<(ushort, ushort), int>();
        foreach (var sub in keptPerSub)
            for (int t = 0; t + 2 < sub.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    ushort x = sub[t + k], y = sub[t + (k + 1) % 3];
                    var e = (Math.Min(x, y), Math.Max(x, y));
                    uses[e] = uses.GetValueOrDefault(e) + 1;
                }
        var onEdge = new HashSet<ushort>();
        foreach (var (e, n) in uses)
            if (n == 1) { onEdge.Add(e.Item1); onEdge.Add(e.Item2); }

        Span<float> tmp = stackalloc float[4];
        var pts = new List<Vec3>(onEdge.Count);
        foreach (var v in onEdge)
        {
            if (v >= vc) continue;
            ReadTyped(streams[pe.Stream], v * strides[pe.Stream] + pe.Offset, pe.Type, tmp);
            pts.Add(new Vec3(tmp[0], tmp[1], tmp[2]));
        }

        int missing = 0;
        float worst = 0f;
        foreach (var land in landings)
        {
            float best = float.MaxValue;
            foreach (var p in pts) best = MathF.Min(best, Dist(land, p));
            if (best <= 1e-6f) continue;
            // Past the reach it was never this boundary's landing to match.
            if (best > CapRimSplitReach) continue;
            missing++;
            worst = MathF.Max(worst, best);
        }
        diag?.Invoke(missing == 0
            ? $"join audit [{what}]: every one of {landings.Count} landing(s) has a vertex on it"
            : $"join audit [{what}]: {missing} of {landings.Count} landing(s) have NO vertex on them "
              + $"(worst {worst:F6}) — each is a T-junction");
    }

    private static int SplitCapRim(List<Vec3> landings, VElem[] decl, ref byte[][] streams,
                                   byte[] strides, ref ushort vc, List<ushort[]> keptPerSub,
                                   ref bool[] used, out int atVertex, out int offBoundary)
    {
        atVertex = 0; offBoundary = 0;
        // Boundary edges of what this mesh is actually emitting, with the triangle each belongs to.
        var edgeUse = new Dictionary<(ushort A, ushort B), int>();
        foreach (var sub in keptPerSub)
            for (int t = 0; t + 2 < sub.Length; t += 3)
                foreach (var (x, y) in new[] { (sub[t], sub[t + 1]), (sub[t + 1], sub[t + 2]), (sub[t + 2], sub[t]) })
                {
                    var e = x < y ? (x, y) : (y, x);
                    edgeUse[e] = edgeUse.GetValueOrDefault(e) + 1;
                }
        var boundary = edgeUse.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
        if (boundary.Count == 0) return 0;

        VElem? pEl = null;
        foreach (var el in decl) if (el.Usage == UsePosition) { pEl = el; break; }
        if (pEl is not { } pe) return 0;

        var posStream = streams[pe.Stream];
        int posStride = strides[pe.Stream];
        Vec3 PosOf(int v)
        {
            Span<float> tmp = stackalloc float[4];
            ReadTyped(posStream, v * posStride + pe.Offset, pe.Type, tmp);
            return new Vec3(tmp[0], tmp[1], tmp[2]);
        }

        // Each landing against the boundary edge it sits on, as a parameter along that edge. A landing at
        // an endpoint is not split at — the vertex is MOVED onto it instead; see the snap below.
        var cuts = new Dictionary<(ushort A, ushort B), List<(float T, Vec3 P)>>();
        // Boundary vertices already pulled onto a landing. First landing wins: a second one arriving at
        // the same vertex must not drag it somewhere else, so it falls through to a real split.
        var snapped = new HashSet<ushort>();
        foreach (var land in landings)
        {
            (ushort A, ushort B) bestE = default;
            float bestD2 = float.MaxValue, bestT = 0f;
            foreach (var e in boundary)
            {
                var a = PosOf(e.A);
                var b = PosOf(e.B);
                float ex = b.X - a.X, ey = b.Y - a.Y, ez = b.Z - a.Z;
                float len = ex * ex + ey * ey + ez * ez;
                if (len < 1e-20f) continue;
                float t = Math.Clamp(((land.X - a.X) * ex + (land.Y - a.Y) * ey + (land.Z - a.Z) * ez) / len, 0f, 1f);
                float qx = a.X + ex * t, qy = a.Y + ey * t, qz = a.Z + ez * t;
                float d2 = (land.X - qx) * (land.X - qx) + (land.Y - qy) * (land.Y - qy) + (land.Z - qz) * (land.Z - qz);
                if (d2 >= bestD2) continue;
                bestD2 = d2; bestE = e; bestT = t;
            }
            if (bestD2 > CapRimSplitReach * CapRimSplitReach) { offBoundary++; continue; }
            // Already a shared vertex, or close enough to one that a split would make a sliver.
            float edgeLen = Dist(PosOf(bestE.A), PosOf(bestE.B));
            if (edgeLen < 1e-6f) { offBoundary++; continue; }
            float margin = CapRimSplitMargin / edgeLen;
            if (bestT <= margin || bestT >= 1f - margin)
            {
                // Snap rather than skip: a landing within CapRimSplitMargin of a vertex is a T-junction if left alone.
                // Bounded by the margin, so nothing travels further than the error it removes.
                ushort at = bestT <= margin ? bestE.A : bestE.B;
                if (snapped.Add(at))
                {
                    WriteXYZ(streams[pe.Stream], at * strides[pe.Stream] + pe.Offset, pe.Type,
                             land.X, land.Y, land.Z);
                    atVertex++;
                    continue;
                }
                // That vertex is already serving another landing — fall through and split properly.
            }
            (cuts.TryGetValue(bestE, out var l) ? l : cuts[bestE] = new List<(float, Vec3)>()).Add((bestT, land));
        }
        if (cuts.Count == 0) return 0;

        // Grow every stream by the number of vertices to insert, then fill each by interpolating its edge's
        // endpoints. Blend indices and weights come from the nearer endpoint: averaging index bytes produces
        // a bone nobody asked for.
        int newCount = cuts.Sum(kv => kv.Value.Count);
        int baseV = vc;
        for (int st = 0; st < streams.Length; st++)
        {
            if (streams[st] == null || strides[st] == 0) continue;
            var g = new byte[(vc + newCount) * strides[st]];
            Buffer.BlockCopy(streams[st], 0, g, 0, vc * strides[st]);
            streams[st] = g;
        }
        var grownUsed = new bool[vc + newCount];
        Array.Copy(used, grownUsed, vc);
        used = grownUsed;

        int next = baseV;
        var inserted = new Dictionary<(ushort A, ushort B), List<(float T, ushort V)>>();
        foreach (var (e, list) in cuts)
        {
            list.Sort((x, y) => x.T.CompareTo(y.T));
            var made = new List<(float, ushort)>();
            foreach (var (t, p) in list)
            {
                ushort nv = (ushort)next++;
                LerpVertex(decl, streams, strides, e.A, e.B, t, nv);
                // The position is the shell's landing exactly, not the interpolation.
                WriteXYZ(streams[pe.Stream], nv * strides[pe.Stream] + pe.Offset, pe.Type, p.X, p.Y, p.Z);
                used[nv] = true;
                made.Add((t, nv));
            }
            inserted[e] = made;
        }
        vc = (ushort)(baseV + newCount);

        // Re-fan every triangle that owns a split edge.
        for (int su = 0; su < keptPerSub.Count; su++)
        {
            var sub = keptPerSub[su];
            var outp = new List<ushort>(sub.Length);
            for (int t = 0; t + 2 < sub.Length; t += 3)
            {
                ushort a = sub[t], b = sub[t + 1], c = sub[t + 2];
                bool done = false;
                for (int k = 0; k < 3 && !done; k++)
                {
                    (ushort x, ushort y, ushort opp) = k switch
                    {
                        0 => (a, b, c),
                        1 => (b, c, a),
                        _ => (c, a, b),
                    };
                    var e = x < y ? (A: x, B: y) : (A: y, B: x);
                    if (!inserted.TryGetValue(e, out var pts)) continue;
                    // Walk the edge in the triangle's own winding so the fan keeps its facing.
                    var seq = new List<ushort> { x };
                    if (x == e.A) seq.AddRange(pts.Select(p => p.V));
                    else for (int i = pts.Count - 1; i >= 0; i--) seq.Add(pts[i].V);
                    seq.Add(y);
                    for (int i = 0; i + 1 < seq.Count; i++)
                    { outp.Add(seq[i]); outp.Add(seq[i + 1]); outp.Add(opp); }
                    done = true;
                }
                if (!done) { outp.Add(a); outp.Add(b); outp.Add(c); }
            }
            keptPerSub[su] = outp.ToArray();
        }
        return newCount;
    }

    /// <summary>Write vertex <paramref name="dst"/> as the interpolation of <paramref name="va"/> and
    /// <paramref name="vb"/>, attribute by attribute as the declaration describes them.</summary>
    private static void LerpVertex(VElem[] decl, byte[][] streams, byte[] strides,
                                   ushort va, ushort vb, float t, ushort dst)
    {
        // Start from the nearer endpoint, so anything not explicitly interpolated below — blend indices,
        // blend weights, colour — arrives as a coherent set rather than a mix of two.
        ushort near = t < 0.5f ? va : vb;
        for (int st = 0; st < streams.Length; st++)
        {
            if (streams[st] == null || strides[st] == 0) continue;
            Buffer.BlockCopy(streams[st], near * strides[st], streams[st], dst * strides[st], strides[st]);
        }

        Span<float> A = stackalloc float[4], B = stackalloc float[4];
        foreach (var el in decl)
        {
            if (el.Usage is not (UsePosition or UseNormal or UseUV)) continue;
            var s = streams[el.Stream];
            if (s == null) continue;
            ReadTyped(s, va * strides[el.Stream] + el.Offset, el.Type, A);
            ReadTyped(s, vb * strides[el.Stream] + el.Offset, el.Type, B);
            float x = A[0] + (B[0] - A[0]) * t, y = A[1] + (B[1] - A[1]) * t, z = A[2] + (B[2] - A[2]) * t;
            int off = dst * strides[el.Stream] + el.Offset;
            switch (el.Usage)
            {
                case UsePosition: WriteXYZ(s, off, el.Type, x, y, z); break;
                case UseNormal:
                {
                    var n = NormalizeOr(new Vec3(x, y, z), new Vec3(A[0], A[1], A[2]));
                    WriteNormal(s, off, el.Type, n.X, n.Y, n.Z);
                    break;
                }
                case UseUV:
                    WriteUV2(s, off, el.Type is 13 or 14, x, y);
                    break;
            }
        }
    }

    /// <summary>How far a welded landing may sit from a cap boundary edge and still be treated as on it.</summary>
    private const float CapRimSplitReach = 0.004f;

    /// <summary>How close to an existing rim vertex a landing must be before splitting is pointless — a
    /// split there would only make a sliver, and the vertex it would share is already there.</summary>
    private const float CapRimSplitMargin = 2e-4f;

    /// <summary>
    /// Nearest point on a rim, with the normal and the skinning interpolated along the segment it lands
    /// on. False when nothing is within <paramref name="radius"/>.
    /// </summary>
    private static bool NearestOnRim(Vec3 p, RimSeg[] rim, float radius,
                                     out Vec3 at, out Vec3 normal, out (string Bone, float W)[] weights,
                                     out float dist)
        => NearestOnRim(p, rim, radius, out at, out normal, out weights, out dist, out _);

    /// <inheritdoc cref="NearestOnRim(Vec3, RimSeg[], float, out Vec3, out Vec3, out (string, float)[], out float)"/>
    /// <param name="uv">The cap's UV at the landing, interpolated along the same segment; null on a segment
    /// whose ends sit on the body's atlas seam.</param>
    private static bool NearestOnRim(Vec3 p, RimSeg[] rim, float radius,
                                     out Vec3 at, out Vec3 normal, out (string Bone, float W)[] weights,
                                     out float dist, out (float U, float V)? uv)
    {
        at = default; normal = default; weights = []; uv = null;
        float best = float.MaxValue;

        // Always the nearest point of a segment, never the nearest rim vertex: several vertices can land on
        // the same vertex and collapse rim triangles to zero area.
        int win = -1;
        float winT = 0f;
        for (int i = 0; i < rim.Length; i++)
        {
            var q = ClosestOnSegment(p, rim[i].PA, rim[i].PB, out float t);
            float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
            float d = dx * dx + dy * dy + dz * dz;
            if (d >= best) continue;
            best = d; win = i; winT = t; at = q;
        }
        if (win < 0) { dist = float.MaxValue; return false; }

        // ...but a landing already within CapRimSplitMargin of an end of its segment takes the end exactly;
        // otherwise it is too close to split at and too far to share a position with, which is a T-junction.
        var seg2 = rim[win];
        if (Dist(at, seg2.PA) <= CapRimSplitMargin) { at = seg2.PA; winT = 0f; }
        else if (Dist(at, seg2.PB) <= CapRimSplitMargin) { at = seg2.PB; winT = 1f; }

        var (na, nb) = (seg2.NA, seg2.NB);
        normal = NormalizeOr(new Vec3(na.X + (nb.X - na.X) * winT, na.Y + (nb.Y - na.Y) * winT,
                                      na.Z + (nb.Z - na.Z) * winT), na);
        weights = BlendWeights(seg2.WA, 1f - winT, seg2.WB, winT, [], 0f);
        if (seg2.HasUv)
            uv = (seg2.UA.U + (seg2.UB.U - seg2.UA.U) * winT,
                  seg2.UA.V + (seg2.UB.V - seg2.UA.V) * winT);
        dist = Dist(p, at);
        return dist <= radius;
    }

    /// <summary>
    /// The mesh's triangle list as the cap leaves it — the source triangles it did not cut out, plus the
    /// ones it built. Vertex normals must be averaged over THIS, not the source list, or the cap is
    /// shaded by the toes it replaced.
    /// </summary>
    private static ushort[] CappedTopology(ToeCapPlan plan, ushort[] tris)
    {
        var kept = new List<ushort>(tris.Length);
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            ushort a = tris[t], b = tris[t + 1], c = tris[t + 2];
            if (a >= plan.NodeOf.Length || b >= plan.NodeOf.Length || c >= plan.NodeOf.Length) continue;
            if (plan.IsCut(a, b, c)) continue;
            kept.Add(a); kept.Add(b); kept.Add(c);
        }
        foreach (var (a, b, c) in plan.NewTriangles) { kept.Add(a); kept.Add(b); kept.Add(c); }
        return kept.ToArray();
    }

    /// <summary>
    /// Did the cap collapse this triangle? Only triangles it actually moved are eligible, so an
    /// uncapped shell can never lose geometry to this test.
    /// </summary>
    private static bool CapDegenerate(Vec3[] src, Vec3[] def, ushort a, ushort b, ushort c)
    {
        if (a >= def.Length || b >= def.Length || c >= def.Length) return false;

        static float Dist2(Vec3 p, Vec3 q)
        {
            float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
            return dx * dx + dy * dy + dz * dz;
        }
        static float Cross2(Vec3 p, Vec3 q, Vec3 r)
        {
            float ux = q.X - p.X, uy = q.Y - p.Y, uz = q.Z - p.Z;
            float wx = r.X - p.X, wy = r.Y - p.Y, wz = r.Z - p.Z;
            float x = uy * wz - uz * wy, y = uz * wx - ux * wz, z = ux * wy - uy * wx;
            return x * x + y * y + z * z;   // = (2*area)^2
        }

        float eps2 = DegenerateMoveEpsilon * DegenerateMoveEpsilon;
        if (Dist2(src[a], def[a]) <= eps2 && Dist2(src[b], def[b]) <= eps2 && Dist2(src[c], def[c]) <= eps2)
            return false;   // the cap never touched this one

        float weld2 = DegenerateWeldDistance * DegenerateWeldDistance;
        if (Dist2(def[a], def[b]) <= weld2 || Dist2(def[b], def[c]) <= weld2 || Dist2(def[a], def[c]) <= weld2)
            return true;

        return Cross2(def[a], def[b], def[c])
             <= DegenerateAreaFraction * DegenerateAreaFraction * Cross2(src[a], src[b], src[c]);
    }

    /// <summary>
    /// Vertex normals recomputed from the capped surface, blended back to the original by mask weight;
    /// without this every normal still describes the toe it was cut from. Faces contribute their
    /// unnormalized cross product, so area weights itself, and normals are accumulated per welded node so
    /// UV seams inside the cap do not crack.
    /// </summary>
    private static Vec3[] CapNormals(Vec3[] basePos, Vec3[] baseNrm, ToeCapPlan plan, ushort[] tris)
        => RelaxedNormals(basePos, baseNrm, plan.Delta, plan.NodeOf, plan.NodeWeight, plan.NodeNormal, tris);

    /// <inheritdoc cref="CapNormals"/>
    /// <remarks>
    /// The plan-free form, shared by every pass that moves vertices without changing which vertices exist.
    /// The toe cap hands it a rebuilt topology; the bust bridge hands it the mesh's own, unchanged.
    /// </remarks>
    internal static Vec3[] RelaxedNormals(Vec3[] basePos, Vec3[] baseNrm, Vec3[] delta, int[] nodeOf,
                                          float[] nodeWeight, Vec3[] nodeNormal, ushort[] tris)
        => RelaxedNormals(basePos, baseNrm, delta, nodeOf, nodeWeight, nodeNormal,
                          Array.ConvertAll(tris, v => (int)v));

    /// <inheritdoc cref="CapNormals"/>
    /// <remarks>The wide-index form: a pass over a whole model's LOD0 meshes concatenated runs past 65535
    /// vertices, and a truncated index accumulates a face onto the wrong node.</remarks>
    internal static Vec3[] RelaxedNormals(Vec3[] basePos, Vec3[] baseNrm, Vec3[] delta, int[] nodeOf,
                                          float[] nodeWeight, Vec3[] nodeNormal, int[] tris)
    {
        int vc = basePos.Length;
        int nodeCount = nodeWeight.Length;

        var def = new Vec3[vc];
        for (int i = 0; i < vc; i++)
            def[i] = new Vec3(basePos[i].X + delta[i].X, basePos[i].Y + delta[i].Y, basePos[i].Z + delta[i].Z);

        // Deduped by node triple: capTris spans every submesh of the mesh, including the duplicate
        // variant the connector filter drops later, and a doubled face would skew the average.
        var accum = new Vec3[nodeCount];
        var seenFace = new HashSet<(int, int, int)>();
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
            if (ia < 0 || ib < 0 || ic < 0 || ia >= vc || ib >= vc || ic >= vc) continue;
            int na = nodeOf[ia], nb = nodeOf[ib], nc = nodeOf[ic];
            if (na == nb || nb == nc || na == nc) continue;

            int s0 = Math.Min(na, Math.Min(nb, nc)), s2 = Math.Max(na, Math.Max(nb, nc));
            if (!seenFace.Add((s0, na + nb + nc - s0 - s2, s2))) continue;

            float ux = def[ib].X - def[ia].X, uy = def[ib].Y - def[ia].Y, uz = def[ib].Z - def[ia].Z;
            float wx = def[ic].X - def[ia].X, wy = def[ic].Y - def[ia].Y, wz = def[ic].Z - def[ia].Z;
            float cxp = uy * wz - uz * wy, cyp = uz * wx - ux * wz, czp = ux * wy - uy * wx;
            if (cxp * cxp + cyp * cyp + czp * czp <= 1e-24f) continue;   // collapsed: no direction to give

            // Unrolled rather than a foreach over a stackalloc: a stackalloc is only freed when the method returns,
            // so inside this loop it grows the frame per face, and a whole model overflows the render-thread stack.
            accum[na] = new Vec3(accum[na].X + cxp, accum[na].Y + cyp, accum[na].Z + czp);
            accum[nb] = new Vec3(accum[nb].X + cxp, accum[nb].Y + cyp, accum[nb].Z + czp);
            accum[nc] = new Vec3(accum[nc].X + cxp, accum[nc].Y + cyp, accum[nc].Z + czp);
        }

        // Smooth the normal field: the area-weighted sums above are faceted wherever the triangles are, and
        // a shell is built as position + normal * BaseOffset, so scattered normals become scattered position.
        // Jacobi over the welded-node graph, so the answer does not depend on node order. Only nodes the pass
        // touched are smoothed, but neighbours are read regardless of weight so an edge node averages against
        // real normals rather than zero. Normalized once after the last pass, to keep the area weighting.
        if (NormalSmoothPasses > 0)
        {
            var nbr = new List<int>[nodeCount];
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
                if (ia < 0 || ib < 0 || ic < 0 || ia >= vc || ib >= vc || ic >= vc) continue;
                int na = nodeOf[ia], nb = nodeOf[ib], nc = nodeOf[ic];
                if (na == nb || nb == nc || na == nc) continue;
                void Link(int a, int b)
                {
                    (nbr[a] ??= new List<int>()).Add(b);
                    (nbr[b] ??= new List<int>()).Add(a);
                }
                Link(na, nb); Link(nb, nc); Link(nc, na);
            }

            var swap = new Vec3[nodeCount];
            for (int pass = 0; pass < NormalSmoothPasses; pass++)
            {
                Array.Copy(accum, swap, nodeCount);
                for (int n = 0; n < nodeCount; n++)
                {
                    if (nodeWeight[n] <= 0f || nbr[n] is not { Count: > 0 } near) continue;
                    float sx = accum[n].X, sy = accum[n].Y, sz = accum[n].Z;
                    foreach (int k in near) { sx += accum[k].X; sy += accum[k].Y; sz += accum[k].Z; }
                    float inv = 1f / (near.Count + 1);
                    swap[n] = new Vec3(sx * inv, sy * inv, sz * inv);
                }
                (accum, swap) = (swap, accum);
            }
        }

        // Winding is not guaranteed here. Getting it backwards shades the cap inside out AND makes the
        // push drive the shell into the body, so decide it once from the source normals we trust.
        float agree = 0;
        for (int n = 0; n < nodeCount; n++)
            if (nodeWeight[n] > 0f)
                agree += accum[n].X * nodeNormal[n].X + accum[n].Y * nodeNormal[n].Y + accum[n].Z * nodeNormal[n].Z;
        float sign = agree < 0f ? -1f : 1f;

        var outN = new Vec3[vc];
        for (int i = 0; i < vc; i++)
        {
            int n = nodeOf[i];
            float w = nodeWeight[n];
            if (w <= 0f) { outN[i] = baseNrm[i]; continue; }   // untouched: original bytes must survive

            var a = accum[n];
            var fresh = Normalize(new Vec3(a.X * sign, a.Y * sign, a.Z * sign)) ?? nodeNormal[n];
            var src = nodeNormal[n];

            // Blend against the NODE-averaged source normal, not this vertex's own, so welded copies
            // land on identical bytes; the weight fade rejoins the untouched shell without a crease.
            var blended = Normalize(new Vec3(
                src.X + (fresh.X - src.X) * w,
                src.Y + (fresh.Y - src.Y) * w,
                src.Z + (fresh.Z - src.Z) * w)) ?? baseNrm[i];

            // Facing kept per vertex: a double-sided surface welds its front and back copies into one node, and
            // a vertex whose own normal disagrees with the result takes it reversed.
            var own = baseNrm[i];
            if (own.X * blended.X + own.Y * blended.Y + own.Z * blended.Z < 0f)
                blended = new Vec3(-blended.X, -blended.Y, -blended.Z);
            outN[i] = blended;
        }
        return outN;
    }
}
