using System;
using System.Collections.Generic;

namespace Proteus.Services;
using static Proteus.Services.MeshMath;

public static partial class SecondSkinWriter
{
    private sealed partial class JoinCutPlan
    {
        private sealed class MeshJoinCut
        {
            private readonly JoinCutPlan plan;
            private readonly int src;
            private readonly ConnectorProfile profile;
            private readonly ConnectorProfile.MeshProfile mesh;
            private int nv;
            private int nt;
            private bool[] ring = null!;
            private bool[] partRing = null!;
            private int[] comp = null!;
            private int comps;

            public MeshJoinCut(JoinCutPlan plan, int src, ConnectorProfile profile, ConnectorProfile.MeshProfile mesh)
            {
                this.plan = plan;
                this.src = src;
                this.profile = profile;
                this.mesh = mesh;
            }

            public void Run(ref int flapVerts)
            {
                if (!Begin()) return;
                FindRings();
                if (!SplitAtRings()) return;
                JudgeComponents(ref flapVerts);
            }

            private bool Begin()
            {
                nv = mesh.Pos.Length / 3;
                nt = mesh.Tris.Length / 3;
                if (nt == 0) return false;
                return true;
            }

            private void FindRings()
            {
                // ── 1. the rings ───────────────────────────────────────────────────────────────────
                // Two kinds, judged differently below: a PART ring joins this part to another (waist, wrist,
                // ankle); an INNER ring joins two regions of this part (thigh to knee), lapped the same way.
                ring = new bool[nv];
                partRing = new bool[nv];

                // Which submeshes each vertex belongs to, so a shared position can be told from a UV seam:
                // a seam duplicates a vertex WITHIN one submesh, a stitch is between two.
                var subsOf = new Dictionary<ushort, HashSet<int>>();
                for (int k = 0; k < mesh.SubCount; k++)
                {
                    var sb = profile.Subs[mesh.SubFirst + k];
                    for (int i = sb.VertFirst; i < sb.VertFirst + sb.VertCount; i++)
                    {
                        if (!subsOf.TryGetValue(mesh.SubVerts[i], out var set))
                            subsOf[mesh.SubVerts[i]] = set = [];
                        set.Add(sb.Index);
                    }
                }

                var here = new Dictionary<(long, long, long), List<ushort>>();
                for (ushort v = 0; v < nv; v++)
                {
                    var key = plan.VCell(new Vec3(mesh.Pos[v * 3], mesh.Pos[v * 3 + 1], mesh.Pos[v * 3 + 2]));
                    if (!here.TryGetValue(key, out var list)) here[key] = list = [];
                    list.Add(v);
                }

                for (ushort v = 0; v < nv; v++)
                {
                    var q = new Vec3(mesh.Pos[v * 3], mesh.Pos[v * 3 + 1], mesh.Pos[v * 3 + 2]);
                    var (cx, cy, cz) = plan.VCell(q);
                    for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        var key = (cx + dx, cy + dy, cz + dz);
                        if (plan.vgrid.TryGetValue(key, out var others))
                            foreach (var (osrc, op) in others)
                                if (osrc != src && Dist(q, op) <= JoinWeld)
                                { ring[v] = partRing[v] = true; break; }

                        if (!here.TryGetValue(key, out var mine)) continue;
                        foreach (ushort w in mine)
                        {
                            if (w == v || Dist(q, new Vec3(mesh.Pos[w * 3], mesh.Pos[w * 3 + 1],
                                                           mesh.Pos[w * 3 + 2])) > JoinWeld) continue;
                            if (subsOf.TryGetValue(v, out var sv) && subsOf.TryGetValue(w, out var sw)
                                && !sv.Overlaps(sw))
                                ring[v] = true;
                        }
                    }
                }
            }

            private bool SplitAtRings()
            {
                // ── 2. the split ───────────────────────────────────────────────────────────────────
                // Triangle adjacency, refusing any edge that lies along a ring. An edge with both ends on
                // the ring IS the join, and the two triangles sharing it are on opposite sides of it.
                var byEdge = new Dictionary<(ushort, ushort), List<int>>();
                for (int t = 0; t < nt; t++)
                {
                    ushort a = mesh.Tris[t * 3], b = mesh.Tris[t * 3 + 1], c = mesh.Tris[t * 3 + 2];
                    Edge(a, b, t); Edge(b, c, t); Edge(c, a, t);
                }
                void Edge(ushort x, ushort y, int t)
                {
                    if (ring[x] && ring[y]) return;
                    var e = x < y ? (x, y) : (y, x);
                    if (!byEdge.TryGetValue(e, out var list)) byEdge[e] = list = [];
                    list.Add(t);
                }

                comp = new int[nt];
                Array.Fill(comp, -1);
                comps = 0;
                var stack = new Stack<int>();
                for (int t0 = 0; t0 < nt; t0++)
                {
                    if (comp[t0] >= 0) continue;
                    int id = comps++;
                    stack.Push(t0);
                    comp[t0] = id;
                    while (stack.Count > 0)
                    {
                        int t = stack.Pop();
                        ushort a = mesh.Tris[t * 3], b = mesh.Tris[t * 3 + 1], c = mesh.Tris[t * 3 + 2];
                        Walk(a, b); Walk(b, c); Walk(c, a);

                        void Walk(ushort x, ushort y)
                        {
                            if (ring[x] && ring[y]) return;
                            var e = x < y ? (x, y) : (y, x);
                            if (!byEdge.TryGetValue(e, out var list)) return;
                            foreach (int u in list)
                                if (comp[u] < 0) { comp[u] = id; stack.Push(u); }
                        }
                    }
                }
                if (comps < 2) return false;   // nothing was split off, so there is no flap
                return true;
            }

            private void JudgeComponents(ref int flapVerts)
            {
                // ── 3. the judgement ───────────────────────────────────────────────────────────────
                var members = new List<int>[comps];
                var verts = new HashSet<ushort>[comps];
                for (int i = 0; i < comps; i++) { members[i] = []; verts[i] = []; }
                for (int t = 0; t < nt; t++)
                {
                    members[comp[t]].Add(t);
                    for (int k = 0; k < 3; k++) verts[comp[t]].Add(mesh.Tris[t * 3 + k]);
                }

                // Which components meet at each ring POSITION. Position, not vertex index: each side of a join
                // carries its own copy of every ring vertex, because a seam runs along the join.
                var atRing = new Dictionary<(long, long, long), HashSet<int>>();
                for (int t = 0; t < nt; t++)
                    for (int k = 0; k < 3; k++)
                    {
                        ushort v = mesh.Tris[t * 3 + k];
                        if (!ring[v]) continue;
                        var key = plan.VCell(new Vec3(mesh.Pos[v * 3], mesh.Pos[v * 3 + 1], mesh.Pos[v * 3 + 2]));
                        if (!atRing.TryGetValue(key, out var set)) atRing[key] = set = [];
                        set.Add(comp[t]);
                    }

                // This mesh's own surface, tagged by component, so a component can ask what the REST of the
                // mesh draws. The size test alone would delete a neck, the only thing drawing its band.
                var own = new CoverGrid(plan.coverEps);
                for (int t = 0; t < nt; t++)
                {
                    ushort ia = mesh.Tris[t * 3], ib = mesh.Tris[t * 3 + 1], ic = mesh.Tris[t * 3 + 2];
                    own.Add(new Vec3(mesh.Pos[ia * 3], mesh.Pos[ia * 3 + 1], mesh.Pos[ia * 3 + 2]),
                            new Vec3(mesh.Pos[ib * 3], mesh.Pos[ib * 3 + 1], mesh.Pos[ib * 3 + 2]),
                            new Vec3(mesh.Pos[ic * 3], mesh.Pos[ic * 3 + 1], mesh.Pos[ic * 3 + 2]), comp[t]);
                }

                for (int i = 0; i < comps; i++)
                {
                    // The biggest thing this component shares a ring with. Of the two surfaces meeting at a
                    // join, the one that carries on into the body is the part's own; the one that stops is the margin.
                    int rival = 0, rivalIdx = -1;
                    bool onPartRing = false;
                    foreach (ushort v in verts[i])
                    {
                        if (!ring[v]) continue;
                        if (partRing[v]) onPartRing = true;
                        var (cx, cy, cz) = plan.VCell(new Vec3(mesh.Pos[v * 3], mesh.Pos[v * 3 + 1],
                                                          mesh.Pos[v * 3 + 2]));
                        // The neighbourhood, because two coincident vertices can still land either side of
                        // a cell edge.
                        for (long dx = -1; dx <= 1; dx++)
                        for (long dy = -1; dy <= 1; dy++)
                        for (long dz = -1; dz <= 1; dz++)
                        {
                            if (!atRing.TryGetValue((cx + dx, cy + dy, cz + dz), out var set)) continue;
                            foreach (int j in set)
                                if (j != i && members[j].Count > rival)
                                { rival = members[j].Count; rivalIdx = j; }
                        }
                    }
                    if (rival == 0) continue;                               // meets no ring

                    // How much of this component another part, and another region of this mesh, already draws;
                    // for the second, also WHICH region draws most of it (the one-way rule below).
                    int covered = 0, tested = 0, byOwn = 0;
                    var ownCredit = new Dictionary<int, int>();
                    foreach (ushort v in verts[i])
                    {
                        if (ring[v]) continue;
                        tested++;
                        var q = new Vec3(mesh.Pos[v * 3], mesh.Pos[v * 3 + 1], mesh.Pos[v * 3 + 2]);
                        if (plan.surface.CoveredBy(q, exclude: src) >= 0) covered++;
                        if (own.CoveredBy(q, exclude: i) is var ownTag and >= 0)
                        {
                            byOwn++;
                            ownCredit[ownTag] = ownCredit.TryGetValue(ownTag, out var n) ? n + 1 : 1;
                        }
                    }
                    if (tested == 0) continue;
                    int ownBy = -1;
                    foreach (var (tag, n) in ownCredit)
                        if (ownBy < 0 || n > ownCredit[ownBy]) ownBy = tag;

                    // A PART join is settled by size, not coverage: the margin tucks INSIDE the neighbour rather
                    // than lying on its surface. An INNER join has two shapes: a band lying ON its neighbour's
                    // surface (coverage finds it) and a lap tucked UNDER it (the sign of the offset from the
                    // nearest FACE finds it; see BehindFraction), the latter held to a high bar and a size
                    // condition. The coverage rule runs ONE WAY: a region is cut only in favour of a BIGGER one,
                    // or the lower index between equals, so of two regions redrawing one surface exactly one survives.
                    bool isFlap;
                    if (onPartRing)
                        isFlap = members[i].Count < rival * FlapShare
                              && plan.InsideJoinedParts(mesh, verts[i], ring, src) >= tested * FlapInJoinedPart;
                    else if (byOwn >= tested * FlapCovered && ownBy >= 0
                             && (members[i].Count < members[ownBy].Count
                                 || (members[i].Count == members[ownBy].Count && i > ownBy)))
                        isFlap = true;
                    else if (rivalIdx >= 0 && members[i].Count < rival * FlapShare)
                    {
                        var (behind, of) = BehindFraction(mesh, verts[i], ring, members[rivalIdx]);
                        isFlap = of > 0 && behind >= of * FlapBehind;
                    }
                    else
                        isFlap = false;
                    if (!isFlap) continue;

                    if (!plan.del[src].TryGetValue(mesh.Index, out var flap))
                        plan.del[src][mesh.Index] = flap = [];
                    float lo = float.MaxValue, hi = float.MinValue;
                    foreach (ushort v in verts[i])
                    {
                        if (ring[v]) continue;
                        flap.Add(v);
                        flapVerts++;
                        float y = mesh.Pos[v * 3 + 1];
                        if (y < lo) lo = y;
                        if (y > hi) hi = y;
                    }
                    plan.diag?.Invoke($"join cut: source {src} mesh {mesh.Index} — a {members[i].Count}-triangle "
                               + $"flap past {(onPartRing ? "a part join" : "an inner join")} it shares "
                               + $"with {rival} triangles, over y {lo:F3}..{hi:F3} "
                               + $"({covered}/{tested} drawn by another part, {byOwn}/{tested} by this "
                               + "mesh's own other regions)");
                }
            }
        }
    }
}
