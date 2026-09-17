using System;
using System.Collections.Generic;

namespace Proteus.Services;
using static Proteus.Services.MeshMath;

public static partial class SecondSkinWriter
{
    private sealed class CapUvProjection
    {
        private readonly Source cap;
        private readonly int mesh;
        private readonly IReadOnlyList<byte[]> bodies;
        private readonly Action<string>? diag;
        private readonly CapPlacement? placed;
        private ushort vc;
        private Vec3[] cp = null!;
        private Vec3[] cn = null!;
        private List<(Vec3 A, Vec3 B, Vec3 C, (float U, float V) Ua, (float U, float V) Ub, (float U, float V) Uc, Vec3 Ctr, (string Bone, float W)[] Wa, (string Bone, float W)[] Wb, (string Bone, float W)[] Wc)> tri = null!;
        private List<int> capTri = null!;
        private HashSet<int>[] adj = null!;
        private float capEdge;
        private float[] candU = null!;
        private float[] candV = null!;
        private float[] candD = null!;
        private (string Bone, float W)[][] candW = null!;
        private int[] candN = null!;
        private float worst;
        private (float U, float V)[] outUV = null!;
        private int[] pick = null!;
        private int moved;
        private int[] patch = null!;
        private int triCount;
        private int[] faceChart = null!;
        private Dictionary<(int V, int Chart), int> copyOf = null!;
        private List<int> sourceOf = null!;
        private int[] corner = null!;
        private (float U, float V)[] finalUV = null!;
        private (string Bone, float W)[][] finalW = null!;
        private (string Bone, float W)[][] srcW = null!;
        private int reseated;
        private CapUvPlan? result;

        public CapUvProjection(Source cap, int mesh, IReadOnlyList<byte[]> bodies, Action<string>? diag, CapPlacement? placed)
        {
            this.cap = cap;
            this.mesh = mesh;
            this.bodies = bodies;
            this.diag = diag;
            this.placed = placed;
        }

        public CapUvPlan? Run()
        {
            if (!CollectSkin()) return result;
            MeasureCap();
            FindLandings();
            SettleSeams();
            SplitStretch();
            AssignCharts();
            CopyVertices();
            ChooseLandings();
            CountHops();
            return MarkRimRings();
        }

        private bool CollectSkin()
        {
            var s = cap.S;
            int mo = cap.MeshStart + mesh * 36;
            vc = BitConverter.ToUInt16(s, mo);
            if (vc == 0) { result = null; return false; }

            var decl = mesh < cap.Decls.Length ? cap.Decls[mesh] : [];
            VElem? pos = null, nrm = null, wgtEl = null;
            foreach (var el in decl)
            {
                if (el.Usage == UsePosition) pos ??= el;
                if (el.Usage == UseNormal) nrm ??= el;
                if (el.Usage == UseBlendWeight) wgtEl ??= el;
            }
            if (pos is not { } pe) { result = null; return false; }


            uint[] vbo = { BitConverter.ToUInt32(s, mo + 20), BitConverter.ToUInt32(s, mo + 24),
                           BitConverter.ToUInt32(s, mo + 28) };
            byte[] bs = { s[mo + 32], s[mo + 33], s[mo + 34] };
            cp = new Vec3[vc];
            cn = new Vec3[vc];
            {
                Span<float> tmp = stackalloc float[4];
                for (int i = 0; i < vc; i++)
                {
                    ReadTyped(s, cap.Vb + (int)vbo[pe.Stream] + i * bs[pe.Stream] + pe.Offset, pe.Type, tmp);
                    cp[i] = new Vec3(tmp[0], tmp[1], tmp[2]);
                    if (nrm is not { } ne) continue;
                    ReadTyped(s, cap.Vb + (int)vbo[ne.Stream] + i * bs[ne.Stream] + ne.Offset, ne.Type, tmp);
                    float nx = tmp[0], ny = tmp[1], nz = tmp[2];
                    if (ne.Type == 8) { nx = nx * 2 - 1; ny = ny * 2 - 1; nz = nz * 2 - 1; }
                    cn[i] = NormalizeOr(new Vec3(nx, ny, nz), default);
                }

                // Where the binding put it, if there is one, so everything below describes the cap as it will be emitted.
                if (placed is { } pl && pl.Pos.Length == vc)
                    for (int i = 0; i < vc; i++) { cp[i] = pl.Pos[i]; cn[i] = pl.Nrm[i]; }
            }

            // Every body's LOD0 skin geometry, in one list, with the same skin-only filter the shell builder uses.
            tri = new List<(Vec3 A, Vec3 B, Vec3 C, (float U, float V) Ua, (float U, float V) Ub,
                                (float U, float V) Uc, Vec3 Ctr,
                                (string Bone, float W)[] Wa, (string Bone, float W)[] Wb, (string Bone, float W)[] Wc)>();
            foreach (var body in bodies)
            {
                if (!TryReadLod0Geometry(body, out var bp, out var bu, out var bt, out var bw)) continue;

                // Toenails are not a projection target: they carry their own UV island, and a cap triangle with
                // one corner on a nail stretches across the gap between islands. They are separate connected
                // components, so drop the small ones and keep the feet.
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
                for (int i = 0; i < nv; i++)
                {
                    int r = Find(i);
                    size[r] = size.GetValueOrDefault(r) + 1;
                }
                int biggest = 0;
                foreach (int v in size.Values) biggest = Math.Max(biggest, v);
                // A foot is within a fraction of the other foot's size; a nail is a small fraction of either.
                int keepAbove = (int)(biggest * ProjectIslandFloor);

                for (int t = 0; t + 2 < bt.Length; t += 3)
                {
                    int a = bt[t], b = bt[t + 1], c = bt[t + 2];
                    if ((a + 1) * 3 > bp.Length || (b + 1) * 3 > bp.Length || (c + 1) * 3 > bp.Length) continue;
                    if (size.GetValueOrDefault(Find(a)) < keepAbove) continue;
                    var pa = new Vec3(bp[a * 3], bp[a * 3 + 1], bp[a * 3 + 2]);
                    var pb = new Vec3(bp[b * 3], bp[b * 3 + 1], bp[b * 3 + 2]);
                    var pc = new Vec3(bp[c * 3], bp[c * 3 + 1], bp[c * 3 + 2]);
                    tri.Add((pa, pb, pc, (bu[a * 2], bu[a * 2 + 1]), (bu[b * 2], bu[b * 2 + 1]),
                             (bu[c * 2], bu[c * 2 + 1]), new Vec3((pa.X + pb.X + pc.X) / 3f,
                                                                  (pa.Y + pb.Y + pc.Y) / 3f,
                                                                  (pa.Z + pb.Z + pc.Z) / 3f),
                             a < bw.Length ? bw[a] : [], b < bw.Length ? bw[b] : [], c < bw.Length ? bw[c] : []));
                }
            }
            if (tri.Count == 0) { diag?.Invoke("authored cap: no body geometry to project UVs from"); result = null; return false; }
            return true;
        }

        private void MeasureCap()
        {
            // The cap's own connectivity and edge length, which drive the seam pass below.
            capTri = CapTriangles(cap, mesh, vc);
            adj = new HashSet<int>[vc];
            for (int i = 0; i < vc; i++) adj[i] = [];
            var edgeLen = new List<float>();
            for (int t = 0; t + 2 < capTri.Count; t += 3)
            {
                int a = capTri[t], b = capTri[t + 1], c = capTri[t + 2];
                adj[a].Add(b); adj[b].Add(a);
                adj[b].Add(c); adj[c].Add(b);
                adj[c].Add(a); adj[a].Add(c);
                edgeLen.Add(Dist(cp[a], cp[b]));
                edgeLen.Add(Dist(cp[b], cp[c]));
                edgeLen.Add(Dist(cp[c], cp[a]));
            }
            edgeLen.Sort();
            capEdge = edgeLen.Count > 0 ? edgeLen[edgeLen.Count / 2] : 0.002f;
        }

        private void FindLandings()
        {
            // Every landing worth considering, nearest first: a UV seam is a cut in texture space only, so a
            // vertex on one is nearly equidistant from body triangles carrying different coordinates.
            candU = new float[vc * ProjectCandidates];
            candV = new float[vc * ProjectCandidates];
            candD = new float[vc * ProjectCandidates];
            // The skinning that goes with each landing, blended by the same barycentric coordinate as the UV,
            // so the cap deforms exactly as the skin beneath it does.
            candW = new (string Bone, float W)[vc * ProjectCandidates][];
            candN = new int[vc];
            worst = 0f;
            for (int i = 0; i < vc; i++)
            {
                var p = cp[i];
                int b0 = i * ProjectCandidates;
                int n = 0;
                float cull = float.MaxValue;   // squared distance the K-th best already achieves
                foreach (var (a, b, c, ua, ub, uc, ctr, wga, wgb, wgc) in tri)
                {
                    float cx = ctr.X - p.X, cy = ctr.Y - p.Y, cz = ctr.Z - p.Z;
                    if (cx * cx + cy * cy + cz * cz > cull + 0.01f) continue;
                    var q = ClosestOnTriangle(p, a, b, c);
                    float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                    float d = dx * dx + dy * dy + dz * dz;
                    if (n == ProjectCandidates && d >= candD[b0 + n - 1]) continue;

                    float v0x = b.X - a.X, v0y = b.Y - a.Y, v0z = b.Z - a.Z;
                    float v1x = c.X - a.X, v1y = c.Y - a.Y, v1z = c.Z - a.Z;
                    float v2x = q.X - a.X, v2y = q.Y - a.Y, v2z = q.Z - a.Z;
                    float d00 = v0x * v0x + v0y * v0y + v0z * v0z;
                    float d01 = v0x * v1x + v0y * v1y + v0z * v1z;
                    float d11 = v1x * v1x + v1y * v1y + v1z * v1z;
                    float d20 = v2x * v0x + v2y * v0y + v2z * v0z;
                    float d21 = v2x * v1x + v2y * v1y + v2z * v1z;
                    float den = d00 * d11 - d01 * d01;
                    float hu, hv;
                    (string Bone, float W)[] hw;
                    if (MathF.Abs(den) < 1e-20f) { hu = ua.U; hv = ua.V; hw = wga; }
                    else
                    {
                        float wb = (d11 * d20 - d01 * d21) / den;
                        float wc = (d00 * d21 - d01 * d20) / den;
                        float wa = 1f - wb - wc;
                        hu = ua.U * wa + ub.U * wb + uc.U * wc;
                        hv = ua.V * wa + ub.V * wb + uc.V * wc;
                        hw = BlendWeights(wga, wa, wgb, wb, wgc, wc);
                    }

                    // One landing per distinct patch of the atlas, or one side of the seam would crowd the other out.
                    int dup = -1;
                    for (int k = 0; k < n; k++)
                    {
                        float du = candU[b0 + k] - hu, dv = candV[b0 + k] - hv;
                        if (du * du + dv * dv < ProjectMergeUV * ProjectMergeUV) { dup = k; break; }
                    }
                    if (dup >= 0)
                    {
                        if (d >= candD[b0 + dup]) continue;
                        for (int k = dup; k + 1 < n; k++)
                        {
                            candU[b0 + k] = candU[b0 + k + 1]; candV[b0 + k] = candV[b0 + k + 1];
                            candD[b0 + k] = candD[b0 + k + 1]; candW[b0 + k] = candW[b0 + k + 1];
                        }
                        n--;
                    }
                    else if (n == ProjectCandidates) n--;

                    int ins = n;
                    while (ins > 0 && candD[b0 + ins - 1] > d)
                    {
                        candU[b0 + ins] = candU[b0 + ins - 1]; candV[b0 + ins] = candV[b0 + ins - 1];
                        candD[b0 + ins] = candD[b0 + ins - 1]; candW[b0 + ins] = candW[b0 + ins - 1];
                        ins--;
                    }
                    candU[b0 + ins] = hu; candV[b0 + ins] = hv; candD[b0 + ins] = d; candW[b0 + ins] = hw;
                    n++;
                    if (n == ProjectCandidates) cull = candD[b0 + n - 1];
                }
                candN[i] = n;
                if (n > 0) worst = MathF.Max(worst, candD[b0]);
            }

            outUV = new (float U, float V)[vc];
            pick = new int[vc];
            for (int i = 0; i < vc; i++) outUV[i] = candN[i] > 0 ? (candU[i * ProjectCandidates], candV[i * ProjectCandidates]) : default;
        }

        private void SettleSeams()
        {
            // Sweep the cap's own graph and let agreement, not proximity, settle the ties: a vertex switches
            // to a rival landing only when its neighbours say so and the rival is within about one cap edge.
            float slack = capEdge * ProjectSeamSlack;
            moved = 0;
            for (int pass = 0; pass < ProjectSeamPasses; pass++)
            {
                int changed = 0;
                for (int i = 0; i < vc; i++)
                {
                    int n = candN[i];
                    if (n < 2 || adj[i].Count == 0) continue;
                    int b0 = i * ProjectCandidates;
                    float near = MathF.Sqrt(candD[b0]);

                    int bestK = pick[i];
                    float bestCost = float.MaxValue;
                    for (int k = 0; k < n; k++)
                    {
                        float far = MathF.Sqrt(candD[b0 + k]);
                        if (far > near + slack) continue;
                        float sum = 0f;
                        foreach (int j in adj[i])
                        {
                            float du = candU[b0 + k] - outUV[j].U, dv = candV[b0 + k] - outUV[j].V;
                            sum += MathF.Sqrt(du * du + dv * dv);
                        }
                        // The distance term is only a tie-break, converted into UV units at the cap's own
                        // scale so the two halves of the cost are comparable.
                        float cost = sum / adj[i].Count + (far - near) * ProjectNearBias / MathF.Max(capEdge, 1e-6f) * ProjectMergeUV;
                        if (cost < bestCost) { bestCost = cost; bestK = k; }
                    }
                    if (bestK == pick[i]) continue;
                    pick[i] = bestK;
                    outUV[i] = (candU[b0 + bestK], candV[b0 + bestK]);
                    changed++;
                }
                moved += changed;
                if (changed == 0) break;
            }
        }

        private void SplitStretch()
        {
            // Agreement alone cannot finish the job: both sides of a cut are locally self-consistent. So the
            // cut is reproduced: label every cap face with its chart and give each vertex one copy per chart
            // its faces use, so no face has corners in two charts. An edge crosses the cut when its stretch
            // (UV per unit of 3D) is far above the median; an absolute UV distance has a blind spot where
            // two charts pass close in the atlas.
            var stretch = new List<float>();
            for (int t = 0; t + 2 < capTri.Count; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = capTri[t + k], b = capTri[t + (k + 1) % 3];
                    float d3 = Dist(cp[a], cp[b]);
                    if (d3 < 1e-7f) continue;
                    float du = outUV[a].U - outUV[b].U, dv = outUV[a].V - outUV[b].V;
                    stretch.Add(MathF.Sqrt(du * du + dv * dv) / d3);
                }
            stretch.Sort();
            float seamCut = (stretch.Count > 0 ? stretch[stretch.Count / 2] : 1f) * ProjectSeamStretch;

            var patchAdj = new HashSet<int>[vc];
            for (int i = 0; i < vc; i++) patchAdj[i] = [];
            for (int i = 0; i < vc; i++)
                foreach (int j in adj[i])
                {
                    float d3 = Dist(cp[i], cp[j]);
                    float du = outUV[i].U - outUV[j].U, dv = outUV[i].V - outUV[j].V;
                    if (d3 < 1e-7f || MathF.Sqrt(du * du + dv * dv) / d3 <= seamCut) patchAdj[i].Add(j);
                }
            patch = ConnectedComponents(patchAdj, vc);
        }

        private void AssignCharts()
        {
            // A face belongs to whichever chart most of its corners are in; a three-way split goes to the
            // corner whose landing is nearest.
            triCount = capTri.Count / 3;
            faceChart = new int[triCount];
            for (int f = 0; f < triCount; f++)
            {
                int a = capTri[f * 3], b = capTri[f * 3 + 1], c = capTri[f * 3 + 2];
                faceChart[f] = patch[a] == patch[b] || patch[a] == patch[c] ? patch[a]
                             : patch[b] == patch[c] ? patch[b]
                             : candD[a * ProjectCandidates] <= candD[b * ProjectCandidates]
                               && candD[a * ProjectCandidates] <= candD[c * ProjectCandidates] ? patch[a]
                             : candD[b * ProjectCandidates] <= candD[c * ProjectCandidates] ? patch[b]
                             : patch[c];
            }
        }

        private void CopyVertices()
        {
            // One output vertex per (vertex, chart) actually used. Everything away from the cut keeps a
            // single copy, so the cap grows by the width of the seam and nothing else.
            copyOf = new Dictionary<(int V, int Chart), int>();
            sourceOf = new List<int>();
            corner = new int[capTri.Count];
            for (int f = 0; f < triCount; f++)
                for (int k = 0; k < 3; k++)
                {
                    var key = (capTri[f * 3 + k], faceChart[f]);
                    if (!copyOf.TryGetValue(key, out int outIdx))
                    {
                        copyOf[key] = outIdx = sourceOf.Count;
                        sourceOf.Add(key.Item1);
                    }
                    corner[f * 3 + k] = outIdx;
                }
        }

        private void ChooseLandings()
        {
            // Each copy takes the landing that best suits its chart.
            finalUV = new (float U, float V)[sourceOf.Count];
            finalW = new (string Bone, float W)[sourceOf.Count][];
            srcW = new (string Bone, float W)[vc][];
            for (int i = 0; i < vc; i++) srcW[i] = [];
            reseated = 0;
            foreach (var ((v, chart), outIdx) in copyOf)
            {
                int b0 = v * ProjectCandidates;
                int bestK = pick[v];
                if (patch[v] != chart)
                {
                    float bestCost = float.MaxValue;
                    for (int k = 0; k < candN[v]; k++)
                    {
                        float sum = 0f;
                        int c = 0;
                        foreach (int j in adj[v])
                        {
                            if (patch[j] != chart) continue;
                            float du = candU[b0 + k] - outUV[j].U, dv = candV[b0 + k] - outUV[j].V;
                            sum += MathF.Sqrt(du * du + dv * dv);
                            c++;
                        }
                        if (c > 0 && sum / c < bestCost) { bestCost = sum / c; bestK = k; }
                    }
                    if (bestK != pick[v]) reseated++;
                }
                finalUV[outIdx] = candN[v] > 0 ? (candU[b0 + bestK], candV[b0 + bestK]) : default;
                finalW[outIdx] = candN[v] > 0 ? candW[b0 + bestK] ?? [] : [];
                // The rim is expressed in pre-split indices, so it needs a weight per source vertex; split copies
                // agree, so which one answers is immaterial.
                srcW[v] = finalW[outIdx];
            }
        }

        private void CountHops()
        {
            // Faces that still straddle the atlas, reported so a regression shows in the build log.
            int hops = 0;
            for (int f = 0; f < triCount; f++)
            {
                var (ua, ub, uc) = (finalUV[corner[f * 3]], finalUV[corner[f * 3 + 1]], finalUV[corner[f * 3 + 2]]);
                float span = MathF.Max(
                    MathF.Max(MathF.Abs(ua.U - ub.U), MathF.Max(MathF.Abs(ub.U - uc.U), MathF.Abs(uc.U - ua.U))),
                    MathF.Max(MathF.Abs(ua.V - ub.V), MathF.Max(MathF.Abs(ub.V - uc.V), MathF.Abs(uc.V - ua.V))));
                if (span > ProjectSeamSpan) hops++;
            }
            int charts = 0;
            {
                var seen = new HashSet<int>();
                foreach (int p in patch) seen.Add(p);
                charts = seen.Count;
            }
            diag?.Invoke($"authored cap: projected {vc} uvs from the body, furthest landing {MathF.Sqrt(worst):F4}, "
                       + $"{moved} settled by agreement; {charts} chart patch(es) split into {sourceOf.Count} "
                       + $"vertices ({reseated} copies reprojected), {hops} face(s) spanning >{ProjectSeamSpan:F2} uv");
        }

        private CapUvPlan? MarkRimRings()
        {
            // Rings in from the cap's open boundary; only a band this deep at the back seam is blended toward
            // the body's skinning.
            var rimRing = new int[vc];
            Array.Fill(rimRing, int.MaxValue);
            {
                var edgeSeen = new Dictionary<(int A, int B), int>();
                for (int t = 0; t + 2 < capTri.Count; t += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = capTri[t + k], b = capTri[t + (k + 1) % 3];
                        var e = (Math.Min(a, b), Math.Max(a, b));
                        edgeSeen[e] = edgeSeen.GetValueOrDefault(e) + 1;
                    }
                var queue = new Queue<int>();
                foreach (var (e, n) in edgeSeen)
                    if (n == 1)
                    {
                        foreach (int x in new[] { e.A, e.B })
                            if (rimRing[x] != 0) { rimRing[x] = 0; queue.Enqueue(x); }
                    }
                while (queue.Count > 0)
                {
                    int x = queue.Dequeue();
                    foreach (int y in adj[x])
                        if (rimRing[y] > rimRing[x] + 1) { rimRing[y] = rimRing[x] + 1; queue.Enqueue(y); }
                }
            }

            return new CapUvPlan
            {
                Uv = finalUV, Weights = finalW, SourceOf = sourceOf.ToArray(), Corner = corner,
                Tri = capTri.ToArray(), SrcPos = cp, SrcNrm = cn, SrcW = srcW, RimRing = rimRing,
            };
        }
    }
}
