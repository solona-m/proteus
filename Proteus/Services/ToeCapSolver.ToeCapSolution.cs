using System;
using System.Collections.Generic;

namespace Proteus.Services;
using static Proteus.Services.SecondSkinWriter;
using static Proteus.Services.MeshMath;

internal static partial class ToeCapSolver
{
    private sealed partial class ToeCapSolution
    {
        private readonly Vec3[] pos;
        private readonly Vec3[] nrm;
        private readonly (float U, float V)[] uv;
        private readonly ushort[] tris;
        private readonly byte[] mask;
        private readonly int mw;
        private readonly int mh;
        private readonly float strength;
        private readonly Action<string>? capLogSink;
        private readonly bool buildGeometry;
        private int vc;
        private int[] nodeOf = null!;
        private int nodeCount;
        private Vec3[] start = null!;
        private Vec3[] nNorm = null!;
        private float[] nW = null!;
        private List<int>[] adj = null!;
        private int[] comp = null!;
        private int compCount;
        private List<int>[] maskedByComp = null!;
        private Vec3[] target = null!;
        private bool[] hasTarget = null!;
        private bool[] dropNode = null!;
        private int[] fromRing = null!;
        private int[] fromSlot = null!;
        private float[] nodeFill = null!;
        private bool[] cutNode = null!;
        private List<(ushort A, ushort B, ushort C)> newTris = null!;
        private bool capped;
        private ushort[] repOf = null!;
        private int[] islandSize = null!;
        private (float U, float V)[]? nodeUV;
        private ToeCapPlan? result;

        public ToeCapSolution(Vec3[] pos, Vec3[] nrm, (float U, float V)[] uv, ushort[] tris, byte[] mask, int mw, int mh, float strength, Action<string>? capLogSink, bool buildGeometry)
        {
            this.pos = pos;
            this.nrm = nrm;
            this.uv = uv;
            this.tris = tris;
            this.mask = mask;
            this.mw = mw;
            this.mh = mh;
            this.strength = strength;
            this.capLogSink = capLogSink;
            this.buildGeometry = buildGeometry;
        }

        public ToeCapPlan? Run()
        {
            if (!WeightVertices()) return result;
            BuildAdjacency();
            FindComponents();
            PrepareTracking();
            ChooseDroppedIslands();
            CapComponents();
            StopSkinBulging();
            EvenPinchedCells();
            LogCap();
            AssignUvs();
            return BuildPlan();
        }

        private bool WeightVertices()
        {
            vc = pos.Length;
            if (vc == 0 || mw <= 0 || mh <= 0 || strength <= 0f || mask.Length < mw * mh) { result = null; return false; }

            // Mask weight per vertex, sampled nearest at the vertex's (already normalized) UV.
            var w = new float[vc];
            bool any = false;
            for (int i = 0; i < vc; i++)
            {
                int x = ((int)MathF.Floor(uv[i].U * mw) % mw + mw) % mw;
                int y = ((int)MathF.Floor(uv[i].V * mh) % mh + mh) % mh;
                float m = mask[y * mw + x] / 255f * strength;
                if (m <= 0f) continue;
                w[i] = MathF.Min(1f, m);
                any = true;
            }
            if (!any) { result = null; return false; }

            nodeOf = WeldByPosition(pos, out nodeCount);

            start = new Vec3[nodeCount];
            nNorm = new Vec3[nodeCount];
            nW = new float[nodeCount];
            var members = new int[nodeCount];
            for (int i = 0; i < vc; i++)
            {
                int n = nodeOf[i];
                start[n] = new Vec3(start[n].X + pos[i].X, start[n].Y + pos[i].Y, start[n].Z + pos[i].Z);
                nNorm[n] = new Vec3(nNorm[n].X + nrm[i].X, nNorm[n].Y + nrm[i].Y, nNorm[n].Z + nrm[i].Z);
                nW[n] = MathF.Max(nW[n], w[i]);
                members[n]++;
            }
            for (int n = 0; n < nodeCount; n++)
            {
                float inv = 1f / members[n];
                start[n] = new Vec3(start[n].X * inv, start[n].Y * inv, start[n].Z * inv);
                var q = nNorm[n];
                float len = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
                nNorm[n] = len > 1e-6f ? new Vec3(q.X / len, q.Y / len, q.Z / len) : default;
            }
            return true;
        }

        private void BuildAdjacency()
        {
            // Edge adjacency over the welded nodes, deduped (a shared edge would otherwise weight twice).
            adj = new List<int>[nodeCount];
            var seen = new HashSet<long>();
            void Link(int a, int b)
            {
                if (a == b) return;
                long key = a < b ? (long)a * nodeCount + b : (long)b * nodeCount + a;
                if (!seen.Add(key)) return;
                (adj[a] ??= new List<int>()).Add(b);
                (adj[b] ??= new List<int>()).Add(a);
            }
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) continue;   // never fault on a bad index
                int a = nodeOf[tris[t]], b = nodeOf[tris[t + 1]], c = nodeOf[tris[t + 2]];
                Link(a, b); Link(b, c); Link(c, a);
            }
        }

        private void FindComponents()
        {
            // Connected components: the two feet are separate islands and must never share a centre, or the
            // envelope would bridge the gap BETWEEN them.
            comp = new int[nodeCount];
            Array.Fill(comp, -1);
            compCount = 0;
            var stack = new Stack<int>();
            for (int n = 0; n < nodeCount; n++)
            {
                if (comp[n] >= 0) continue;
                comp[n] = compCount;
                stack.Push(n);
                while (stack.Count > 0)
                {
                    int q = stack.Pop();
                    if (adj[q] == null) continue;
                    foreach (int k in adj[q])
                        if (comp[k] < 0) { comp[k] = compCount; stack.Push(k); }
                }
                compCount++;
            }

            maskedByComp = new List<int>[compCount];
            for (int n = 0; n < nodeCount; n++)
                if (nW[n] > 0f) (maskedByComp[comp[n]] ??= new List<int>()).Add(n);

            target = new Vec3[nodeCount];
            hasTarget = new bool[nodeCount];
            dropNode = new bool[nodeCount];
        }

        private void PrepareTracking()
        {
            // Which ring and slot placed each cap vertex, so a defect can be traced back to its construction.
            fromRing = new int[nodeCount];
            fromSlot = new int[nodeCount];
            Array.Fill(fromRing, -1);
            Array.Fill(fromSlot, -1);
            // How much of this node's placement bridged empty space; 1 means nothing is under it, so nothing may pull it down.
            nodeFill = new float[nodeCount];
            Array.Fill(nodeFill, 1f);
            cutNode = new bool[nodeCount];
            newTris = new List<(ushort A, ushort B, ushort C)>();
            capped = false;

            // One representative vertex per node — the cap's triangles are written in vertex indices.
            repOf = new ushort[nodeCount];
            var haveRep = new bool[nodeCount];
            for (int i = 0; i < vc; i++)
            {
                int n = nodeOf[i];
                if (!haveRep[n]) { repOf[n] = (ushort)i; haveRep[n] = true; }
            }

            islandSize = new int[compCount];
            for (int n = 0; n < nodeCount; n++) islandSize[comp[n]]++;
        }

        private void ChooseDroppedIslands()
        {
            // Which islands the cap swallows whole (the toenails), settled before anything is capped: on some
            // bodies the nails are separate islands inside the foot mesh itself.
            for (int c = 0; c < compCount; c++)
            {
                var m2 = maskedByComp[c];
                if (m2 is not { Count: >= MinToeCapNodes }) continue;
                int core2 = 0;
                foreach (int n in m2) if (nW[n] >= ToeCapCoreWeight) core2++;
                if (core2 < MinToeCapNodes) continue;
                if (core2 > MaxCoreFraction * islandSize[c] && islandSize[c] <= nodeCount * SmallIslandFraction)
                    foreach (int n in m2) dropNode[n] = true;
            }
        }

        private void CapComponents()
        {
            for (int c = 0; c < compCount; c++)
            {
                CapComponent(c);
            }
        }

        /// <summary>Cuts one foot's toe box out and rebuilds it as a smooth cap.</summary>
        private void CapComponent(int c)
        {
            new FootCap(this, c).Run();
        }

        private void StopSkinBulging()
        {
            // ── stop the skin bulging through the cap ──────────────────────────────────────────────────
            // Clearance was enforced one way only, and a convex toe pad comes through the middle of a flat
            // cap triangle whose corners are clear. Walk the skin and lift the cap where it passes under a
            // vertex; each corner gets the largest lift asked of it, never the sum, and capped.
            if (capped && newTris.Count > 0)
            {
                var capNodes = new int[newTris.Count * 3];
                for (int t = 0; t < newTris.Count; t++)
                {
                    var (ta, tb, tc) = newTris[t];
                    capNodes[t * 3] = nodeOf[ta];
                    capNodes[t * 3 + 1] = nodeOf[tb];
                    capNodes[t * 3 + 2] = nodeOf[tc];
                }

                Vec3 At2(int n) => new(start[n].X + target[n].X, start[n].Y + target[n].Y, start[n].Z + target[n].Z);

                var skinPts = new List<int>();
                var capped2 = new List<int>();
                for (int n = 0; n < nodeCount; n++)
                {
                    if (cutNode[n]) skinPts.Add(n);
                    if (hasTarget[n]) capped2.Add(n);
                }
                float edge2 = MeanEdgeLength(start, adj, skinPts.Count > 0 ? skinPts : capped2);
                float wantClear = edge2 * SkinClearance;
                float maxLift = edge2 * MaxPokeLift;

                var want = new float[nodeCount];      // largest lift any skin vertex asks of this node
                var dir = new Vec3[nodeCount];        // and the direction that asked for it
                var used = new float[nodeCount];      // total already applied, against the cap
                float biggest = 0f;

                for (int pass = 0; pass < PokePasses; pass++)
                {
                    Array.Clear(want);
                    int asked = 0;

                    foreach (int m in skinPts)
                    {
                        var pm = start[m];
                        var nm = nNorm[m];

                        int bestT = -1;
                        float bestD = float.MaxValue;
                        Vec3 bestQ = default;
                        for (int t = 0; t < newTris.Count; t++)
                        {
                            Vec3 a = At2(capNodes[t * 3]), b = At2(capNodes[t * 3 + 1]), c = At2(capNodes[t * 3 + 2]);
                            var q = ClosestOnTriangle(pm, a, b, c);
                            float dx = pm.X - q.X, dy = pm.Y - q.Y, dz = pm.Z - q.Z;
                            float d = dx * dx + dy * dy + dz * dz;
                            if (d < bestD) { bestD = d; bestT = t; bestQ = q; }
                        }
                        if (bestT < 0) continue;

                        // Only where the cap passes over this vertex: on a toe's inner flank the nearest cap face is
                        // the bridge, and lifting it along this normal drives it into the toe opposite.
                        float dqx = bestQ.X - pm.X, dqy = bestQ.Y - pm.Y, dqz = bestQ.Z - pm.Z;
                        float off = dqx * nm.X + dqy * nm.Y + dqz * nm.Z;
                        float latx = dqx - nm.X * off, laty = dqy - nm.Y * off, latz = dqz - nm.Z * off;
                        if (latx * latx + laty * laty + latz * latz > (edge2 * PokeReach) * (edge2 * PokeReach))
                            continue;
                        if (off >= wantClear) continue;
                        float deficit = wantClear - off;
                        asked++;

                        int na = capNodes[bestT * 3], nb = capNodes[bestT * 3 + 1], nc = capNodes[bestT * 3 + 2];
                        Vec3 pa = At2(na), pb = At2(nb), pc = At2(nc);

                        // Barycentric coordinates of the landing point, so the lift stays local to the bulge.
                        float v0x = pb.X - pa.X, v0y = pb.Y - pa.Y, v0z = pb.Z - pa.Z;
                        float v1x = pc.X - pa.X, v1y = pc.Y - pa.Y, v1z = pc.Z - pa.Z;
                        float v2x = bestQ.X - pa.X, v2y = bestQ.Y - pa.Y, v2z = bestQ.Z - pa.Z;
                        float e00 = v0x * v0x + v0y * v0y + v0z * v0z;
                        float e01 = v0x * v1x + v0y * v1y + v0z * v1z;
                        float e11 = v1x * v1x + v1y * v1y + v1z * v1z;
                        float e20 = v2x * v0x + v2y * v0y + v2z * v0z;
                        float e21 = v2x * v1x + v2y * v1y + v2z * v1z;
                        float den = e00 * e11 - e01 * e01;
                        float wa = 1f / 3f, wb = 1f / 3f, wc = 1f / 3f;
                        if (MathF.Abs(den) > 1e-20f)
                        {
                            wb = Math.Clamp((e11 * e20 - e01 * e21) / den, 0f, 1f);
                            wc = Math.Clamp((e00 * e21 - e01 * e20) / den, 0f, 1f);
                            wa = Math.Clamp(1f - wb - wc, 0f, 1f);
                            float sum = wa + wb + wc;
                            if (sum > 1e-6f) { wa /= sum; wb /= sum; wc /= sum; }
                        }

                        // The MAXIMUM asked of each corner this pass, never the sum.
                        void Ask(int n, float w)
                        {
                            if (n < 0 || !hasTarget[n]) return;      // rim vertices are shared: moving one tears the seam
                            float need = deficit * w;
                            if (need <= want[n]) return;
                            want[n] = need;
                            dir[n] = nm;
                        }
                        Ask(na, wa); Ask(nb, wb); Ask(nc, wc);
                    }

                    if (asked == 0) break;

                    foreach (int n in capped2)
                    {
                        if (want[n] <= 0f) continue;
                        float step = MathF.Min(want[n], maxLift - used[n]);
                        if (step <= 0f) continue;
                        used[n] += step;
                        biggest = MathF.Max(biggest, used[n]);
                        target[n] = new Vec3(target[n].X + dir[n].X * step,
                                             target[n].Y + dir[n].Y * step,
                                             target[n].Z + dir[n].Z * step);
                    }
                }

                capLogSink?.Invoke($"poke: lifted the cap off the skin, most-moved vertex {biggest:F5} "
                                 + $"(ceiling {maxLift:F5}, clearance {wantClear:F5})");
            }
        }

        private void EvenPinchedCells()
        {
            // ── even out the pinched cells ─────────────────────────────────────────────────────────────
            // Pinched cells: two ring slots that came to rest almost on top of each other. Positional
            // smoothing pulls both the same way, so slide them along the surface instead (the Laplacian
            // with its normal component removed); the silhouette does not move.
            if (capped && newTris.Count > 0)
            {
                var tanAdj = new Dictionary<int, HashSet<int>>();
                var tanFaces = new Dictionary<int, List<(int A, int B, int C)>>();
                void TanEdge(int a, int b)
                {
                    if (a == b) return;
                    (tanAdj.TryGetValue(a, out var la) ? la : tanAdj[a] = new HashSet<int>()).Add(b);
                    (tanAdj.TryGetValue(b, out var lb) ? lb : tanAdj[b] = new HashSet<int>()).Add(a);
                }
                // A local function per corner rather than a stackalloc in the loops below: a stackalloc is only
                // freed when the method returns, so one inside a loop grows the frame every iteration.
                void TanFace(int n, int a, int b, int c)
                    => (tanFaces.TryGetValue(n, out var lf) ? lf : tanFaces[n] = new List<(int, int, int)>())
                        .Add((a, b, c));
                var capTri = new List<(int A, int B, int C)>(newTris.Count);
                foreach (var (ta, tb, tc) in newTris)
                {
                    int na = nodeOf[ta], nb = nodeOf[tb], nc = nodeOf[tc];
                    capTri.Add((na, nb, nc));
                    TanEdge(na, nb); TanEdge(nb, nc); TanEdge(nc, na);
                    TanFace(na, na, nb, nc); TanFace(nb, na, nb, nc); TanFace(nc, na, nb, nc);
                }

                // The surviving shell around the cap joins the graph too, or a rim vertex only sees its cap-side
                // neighbours and is dragged inward.
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) continue;
                    int na = nodeOf[tris[t]], nb = nodeOf[tris[t + 1]], nc = nodeOf[tris[t + 2]];
                    if (cutNode[na] || cutNode[nb] || cutNode[nc]) continue;
                    TanEdge(na, nb); TanEdge(nb, nc); TanEdge(nc, na);
                    TanFace(na, na, nb, nc); TanFace(nb, na, nb, nc); TanFace(nc, na, nb, nc);
                }

                Vec3 Now(int n) => new(start[n].X + target[n].X, start[n].Y + target[n].Y, start[n].Z + target[n].Z);

                var allCap = new List<int>();
                foreach (var kv in tanAdj) if (hasTarget[kv.Key]) allCap.Add(kv.Key);
                float tanEdge = MeanEdgeLength(start, adj, allCap);
                float tanLimit = tanEdge * TangentClamp;

                // Only the vertices of faces that are actually badly shaped; everything else stays put.
                var tanMove = new HashSet<int>();
                foreach (var (a, b, c) in capTri)
                {
                    Vec3 pa = Now(a), pb = Now(b), pc = Now(c);
                    float e0 = Dist(pa, pb), e1 = Dist(pb, pc), e2 = Dist(pc, pa);
                    float lo2 = MathF.Min(e0, MathF.Min(e1, e2)), hi2 = MathF.Max(e0, MathF.Max(e1, e2));
                    if (lo2 <= 1e-9f || hi2 / lo2 <= TangentTrigger) continue;
                    if (hasTarget[a]) tanMove.Add(a);
                    if (hasTarget[b]) tanMove.Add(b);
                    if (hasTarget[c]) tanMove.Add(c);
                }

                if (tanMove.Count > 0)
                {
                    var from = new Dictionary<int, Vec3>();
                    foreach (int n in tanMove) from[n] = Now(n);

                    for (int pass = 0; pass < TangentPasses; pass++)
                    {
                        var next = new List<(int Node, Vec3 To)>(tanMove.Count);
                        foreach (int n in tanMove)
                        {
                            if (!tanAdj.TryGetValue(n, out var nb) || nb.Count == 0) continue;
                            var p2 = Now(n);
                            float sx = 0, sy = 0, sz = 0;
                            foreach (int k in nb) { var q = Now(k); sx += q.X; sy += q.Y; sz += q.Z; }
                            float dx = sx / nb.Count - p2.X, dy = sy / nb.Count - p2.Y, dz = sz / nb.Count - p2.Z;

                            // The surface normal here, area weighted over the faces this vertex belongs to.
                            float ax = 0, ay = 0, az = 0;
                            if (tanFaces.TryGetValue(n, out var fl))
                                foreach (var (a, b, c) in fl)
                                {
                                    Vec3 pa = Now(a), pb = Now(b), pc = Now(c);
                                    float ux = pb.X - pa.X, uy = pb.Y - pa.Y, uz = pb.Z - pa.Z;
                                    float wx = pc.X - pa.X, wy = pc.Y - pa.Y, wz = pc.Z - pa.Z;
                                    ax += uy * wz - uz * wy; ay += uz * wx - ux * wz; az += ux * wy - uy * wx;
                                }
                            if (Normalize(new Vec3(ax, ay, az)) is { } nn)
                            {
                                float along = dx * nn.X + dy * nn.Y + dz * nn.Z;
                                dx -= nn.X * along; dy -= nn.Y * along; dz -= nn.Z * along;   // tangential only
                            }

                            var cand = new Vec3(p2.X + dx * RelaxRate, p2.Y + dy * RelaxRate, p2.Z + dz * RelaxRate);

                            // ...and never on top of a neighbour: sliding vertices together closes pairs as readily as it
                            // evens the shape.
                            float keep2 = tanEdge * SlotMinGap;
                            foreach (int k in nb)
                            {
                                var np3 = Now(k);
                                float gx = cand.X - np3.X, gy = cand.Y - np3.Y, gz = cand.Z - np3.Z;
                                float gd = MathF.Sqrt(gx * gx + gy * gy + gz * gz);
                                if (gd >= keep2 || gd <= 1e-9f) continue;
                                float grow2 = (keep2 - gd) / gd;
                                cand = new Vec3(cand.X + gx * grow2, cand.Y + gy * grow2, cand.Z + gz * grow2);
                            }

                            // Never far from where it started, however many passes run.
                            var o = from[n];
                            float tx = cand.X - o.X, ty = cand.Y - o.Y, tz = cand.Z - o.Z;
                            float travel = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
                            if (travel > tanLimit)
                            {
                                float k2 = tanLimit / travel;
                                cand = new Vec3(o.X + tx * k2, o.Y + ty * k2, o.Z + tz * k2);
                            }
                            next.Add((n, cand));
                        }
                        foreach (var (n, to) in next)
                            target[n] = new Vec3(to.X - start[n].X, to.Y - start[n].Y, to.Z - start[n].Z);
                    }
                }

            }
        }

        private void LogCap()
        {
            if (capLogSink != null && capped)
            {
                var placed = new List<int>();
                for (int n = 0; n < nodeCount; n++) if (hasTarget[n]) placed.Add(n);
                var all = new List<int>();
                for (int n = 0; n < nodeCount; n++) if (cutNode[n] || hasTarget[n]) all.Add(n);
                float near = MeanEdgeLength(start, adj, all) * 0.15f;
                Vec3 Fin(int n) => new(start[n].X + target[n].X, start[n].Y + target[n].Y, start[n].Z + target[n].Z);
                string Where(int n) => fromRing[n] switch
                {
                    -1 => "rim",
                    2000 => $"patch[{fromSlot[n] / 1000},{fromSlot[n] % 1000}]",
                    >= 1000 => $"dome{fromRing[n] - 1000} slot {fromSlot[n]}",
                    _ => $"ring {fromRing[n]} slot {fromSlot[n]}",
                };
                int reported = 0;
                for (int a = 0; a < placed.Count && reported < 12; a++)
                    for (int b = a + 1; b < placed.Count && reported < 12; b++)
                    {
                        var pa = Fin(placed[a]);
                        var pb = Fin(placed[b]);
                        float dx = pa.X - pb.X, dy = pa.Y - pb.Y, dz = pa.Z - pb.Z;
                        float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (d >= near) continue;
                        capLogSink($"collapsed pair {d:F6} apart at ({pa.X:F4},{pa.Y:F4},{pa.Z:F4}): "
                                 + $"{Where(placed[a])} <-> {Where(placed[b])}");
                        reported++;
                    }
            }
        }

        private void AssignUvs()
        {
            // ── UVs for the rebuilt surface ────────────────────────────────────────────────────────────
            // Every cap vertex is reused from elsewhere in the toe box and still carries that donor's UV,
            // which reads as a smeared texture. Shrink-wrap instead: drop each moved vertex onto the surface
            // the cap replaced and interpolate the UV where it lands.
            nodeUV = null;
            if (capped)
            {
                var srcTri = new List<(Vec3 A, Vec3 B, Vec3 C, int NA, int NB, int NC)>();
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) continue;
                    int na = nodeOf[tris[t]], nb = nodeOf[tris[t + 1]], nc = nodeOf[tris[t + 2]];
                    // The replaced region plus a rim of what survives, so the join stays continuous.
                    if (!cutNode[na] && !cutNode[nb] && !cutNode[nc]) continue;
                    srcTri.Add((start[na], start[nb], start[nc], na, nb, nc));
                }

                if (srcTri.Count > 0)
                {
                    // One UV per welded node; coincident duplicates across a UV seam collapse to one node and one UV wins.
                    var uvOf = new (float U, float V)[nodeCount];
                    for (int i = 0; i < vc; i++) uvOf[nodeOf[i]] = uv[i];

                    nodeUV = new (float U, float V)[nodeCount];
                    for (int n = 0; n < nodeCount; n++)
                    {
                        if (!hasTarget[n]) continue;
                        var p2 = new Vec3(start[n].X + target[n].X, start[n].Y + target[n].Y, start[n].Z + target[n].Z);

                        float bestD = float.MaxValue;
                        (float U, float V) best = uvOf[n];
                        foreach (var (a, b, c, na, nb, nc) in srcTri)
                        {
                            var q = ClosestOnTriangle(p2, a, b, c);
                            float dx = p2.X - q.X, dy = p2.Y - q.Y, dz = p2.Z - q.Z;
                            float d = dx * dx + dy * dy + dz * dz;
                            if (d >= bestD) continue;
                            bestD = d;

                            // Barycentric coordinates of the landing point, by area.
                            float v0x = b.X - a.X, v0y = b.Y - a.Y, v0z = b.Z - a.Z;
                            float v1x = c.X - a.X, v1y = c.Y - a.Y, v1z = c.Z - a.Z;
                            float v2x = q.X - a.X, v2y = q.Y - a.Y, v2z = q.Z - a.Z;
                            float d00 = v0x * v0x + v0y * v0y + v0z * v0z;
                            float d01 = v0x * v1x + v0y * v1y + v0z * v1z;
                            float d11 = v1x * v1x + v1y * v1y + v1z * v1z;
                            float d20 = v2x * v0x + v2y * v0y + v2z * v0z;
                            float d21 = v2x * v1x + v2y * v1y + v2z * v1z;
                            float den = d00 * d11 - d01 * d01;
                            if (MathF.Abs(den) < 1e-20f) { best = uvOf[na]; continue; }
                            float wb = (d11 * d20 - d01 * d21) / den;
                            float wc = (d00 * d21 - d01 * d20) / den;
                            float wa = 1f - wb - wc;
                            best = (uvOf[na].U * wa + uvOf[nb].U * wb + uvOf[nc].U * wc,
                                    uvOf[na].V * wa + uvOf[nb].V * wb + uvOf[nc].V * wc);
                        }
                        nodeUV[n] = best;
                    }
                }
            }
        }

        private ToeCapPlan? BuildPlan()
        {
            // A mesh may have nothing to cap and still have islands to drop — the toenail mesh is exactly
            // that: every island on it is swallowed whole, so no cap is ever built for it.
            bool anyDropped = false;
            foreach (bool d in dropNode) if (d) { anyDropped = true; break; }
            // An empty NewTriangles is a failure only when geometry was meant to be built; on the authored
            // path the cut alone is the contribution.
            if ((!capped || (buildGeometry && newTris.Count == 0)) && !anyDropped) return null;

            var delta = new Vec3[vc];
            for (int i = 0; i < vc; i++)
            {
                int n = nodeOf[i];
                if (hasTarget[n]) delta[i] = target[n];
            }

            // Nodes the cap never moved must report zero weight, so the normal pass leaves their bytes
            // exactly as they were — that is what keeps an untouched shell byte-identical.
            for (int n = 0; n < nodeCount; n++)
                if (!hasTarget[n]) nW[n] = 0f;

            return new ToeCapPlan
            {
                Delta = delta, NodeOf = nodeOf, NodeWeight = nW, NodeNormal = nNorm, DropNode = dropNode,
                NodeUV = nodeUV,
                CutNode = cutNode, NewTriangles = newTris,
            };
        }

        private ushort Rep(int n) => repOf[n];
    }
}
