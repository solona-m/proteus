using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// How far along the cloth the hold on a shared seam fades into the rest of the piece (40 mm) — see
    /// <see cref="PinSharedSeam"/>.
    /// </summary>
    internal const float SeamBlendReach = 0.04f;

    /// <summary>Rounds of neighbour averaging the band's correction gets, so it fades rather than steps.</summary>
    internal const int SeamBlendRounds = 4;

    /// <summary>
    /// The share of the seam top's split a piece has to meet, at its own open edge, for its side to be held (0.9). It is
    /// what tells one mod's waists apart: Sarelle's Rue top meets the main waist at 73 of 133 places and its Gen 3 top at
    /// 42 — open edges both — while every main-waist piece meets 127 to 133.
    /// </summary>
    internal const float SeamCover = 0.9f;

    /// <summary>The fewest split places that make a split (16): fewer is two pieces touching, not a garment cut in two.</summary>
    internal const int LeastSeamPoints = 16;

    /// <summary>The garment's vertices on a shared seam, which the refit leaves where the author put them.</summary>
    /// <param name="Vertices">In the part reader's vertex order, front and back sheets alike.</param>
    /// <param name="Positions">How many split places they make (welded).</param>
    /// <param name="Label">Which top file and bodies the seam follows, for the log.</param>
    internal sealed record SharedSeam(int[] Vertices, int Positions, string Label);

    /// <summary>
    /// The split places of a seam top: where its open edge meets a legs file's, weighted alike, off any surface the two
    /// files share. Each place carries every weighting the top gives it.
    /// </summary>
    internal sealed record SeamPoints(Vector3[] At, List<(string Bone, float W)>[][] Weights)
    {
        public int Count => At.Length;
    }

    /// <summary>
    /// A model's cloth welded at <see cref="SnapEps"/>, with its triangles merged by their welded corners — the front and
    /// back of a double-sided sheet weld into one node and their faces into one, so a double-sided hem is still an open
    /// edge — and which nodes lie on an edge only one merged face uses.
    /// </summary>
    internal sealed class OpenEdges
    {
        public required int[] NodeOf { get; init; }
        public required Vector3[] NodeAt { get; init; }
        public required bool[] Open { get; init; }
        public required List<int>[] VertsOf { get; init; }
        public required HashSet<(int, int, int)> Faces { get; init; }
        public required List<(int, int, int)>[] FacesAt { get; init; }
        private readonly Dictionary<(int, int, int), List<int>> grid = [];

        public int Count => NodeAt.Length;

        public void Index()
        {
            for (int n = 0; n < NodeAt.Length; n++)
            {
                var key = Cell(NodeAt[n]);
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
                list.Add(n);
            }
        }

        /// <summary>Nodes within <see cref="SnapEps"/> of <paramref name="p"/>.</summary>
        public IEnumerable<int> Near(Vector3 p)
        {
            var (cx, cy, cz) = Cell(p);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                            foreach (int n in list)
                                if (Vector3.DistanceSquared(NodeAt[n], p) <= SnapEps * SnapEps) yield return n;
        }

        private static (int, int, int) Cell(Vector3 p)
            => ((int)MathF.Floor(p.X / SnapEps), (int)MathF.Floor(p.Y / SnapEps), (int)MathF.Floor(p.Z / SnapEps));
    }

    /// <summary>
    /// The open edges of <paramref name="model"/>'s cloth: welded at <see cref="SnapEps"/>, its faces merged by their
    /// three welded corners (winding ignored), and an edge open when exactly one merged face uses it. Without the merge
    /// Sarelle's double-sided dress has no open split place at all (each split edge is used by its front and back face).
    /// </summary>
    /// <param name="mergeSheets">False counts every face apart (tests only: the double-sided case without the merge).</param>
    internal static OpenEdges OpenEdgesOf(ModelParts model, bool mergeSheets = true)
    {
        int vc = model.Positions.Length / 3;
        var cloth = new bool[vc];
        var tris = new List<int>();
        foreach (var part in model.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
                if (v >= 0 && v < vc) cloth[v] = true;
            tris.AddRange(part.Triangles);
        }

        Vector3 At(int v) => new(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);

        // Weld the cloth's vertices within SnapEps of each other (a grid at the tolerance, union-find over it).
        var parent = new int[vc];
        for (int v = 0; v < vc; v++) parent[v] = v;
        int Find(int v)
        {
            while (parent[v] != v) v = parent[v] = parent[parent[v]];
            return v;
        }
        var cells = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) CellOf(Vector3 p)
            => ((int)MathF.Floor(p.X / SnapEps), (int)MathF.Floor(p.Y / SnapEps), (int)MathF.Floor(p.Z / SnapEps));
        for (int v = 0; v < vc; v++)
        {
            if (!cloth[v]) continue;
            var key = CellOf(At(v));
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
            list.Add(v);
        }
        for (int v = 0; v < vc; v++)
        {
            if (!cloth[v]) continue;
            var p = At(v);
            var (cx, cy, cz) = CellOf(p);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                        foreach (int u in list)
                            if (u > v && Vector3.DistanceSquared(p, At(u)) <= SnapEps * SnapEps)
                            {
                                int a = Find(v), b = Find(u);
                                if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                            }
                    }
        }

        var nodeOf = new int[vc];
        Array.Fill(nodeOf, -1);
        var rootNode = new Dictionary<int, int>();
        var at = new List<Vector3>();
        var verts = new List<List<int>>();
        for (int v = 0; v < vc; v++)
        {
            if (!cloth[v]) continue;
            int r = Find(v);
            if (!rootNode.TryGetValue(r, out int n))
            {
                rootNode[r] = n = at.Count;
                at.Add(At(r));
                verts.Add([]);
            }
            nodeOf[v] = n;
            verts[n].Add(v);
        }

        int nodes = at.Count;
        var faces = new HashSet<(int, int, int)>();
        var facesAt = new List<(int, int, int)>[nodes];
        for (int n = 0; n < nodes; n++) facesAt[n] = [];
        var faceList = new List<(int, int, int)>();
        for (int t = 0; t + 2 < tris.Count; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            if (a < 0 || b < 0 || c < 0 || a >= vc || b >= vc || c >= vc) continue;
            int na = nodeOf[a], nb = nodeOf[b], nc = nodeOf[c];
            if (na < 0 || nb < 0 || nc < 0 || na == nb || nb == nc || na == nc) continue;
            var key = Sorted(na, nb, nc);
            if (faces.Add(key) || !mergeSheets)
            {
                faceList.Add(key);
                facesAt[na].Add(key);
                facesAt[nb].Add(key);
                facesAt[nc].Add(key);
            }
        }

        var uses = new Dictionary<(int, int), int>();
        foreach (var (a, b, c) in faceList)
        {
            uses[(a, b)] = uses.GetValueOrDefault((a, b)) + 1;
            uses[(b, c)] = uses.GetValueOrDefault((b, c)) + 1;
            uses[(a, c)] = uses.GetValueOrDefault((a, c)) + 1;
        }
        var open = new bool[nodes];
        foreach (var ((a, b), count) in uses)
            if (count == 1) open[a] = open[b] = true;

        var edges = new OpenEdges
        {
            NodeOf = nodeOf,
            NodeAt = [.. at],
            Open = open,
            VertsOf = [.. verts],
            Faces = faces,
            FacesAt = facesAt,
        };
        edges.Index();
        return edges;
    }

    /// <summary>The open edges of the models <see cref="SplitPoints"/> has read, kept while the model is.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ModelParts, OpenEdges> OpenEdgesCache = new();

    /// <summary>
    /// The split places of a top file against a set of legs files: each a node on the top's open edge that lies within
    /// <see cref="SnapEps"/> of a legs file's open-edge node with a vertex pair there weighted alike
    /// (<see cref="LayerSameWeights"/>), and on no face the two files share corner for corner — a belt copied into both
    /// files is a shared surface, not a split. Null when fewer than <see cref="LeastSeamPoints"/>.
    /// </summary>
    internal static SeamPoints? SplitPoints(ModelParts top, XivLiveMesh.SkinnedMesh topSkin,
                                            IEnumerable<(ModelParts Model, XivLiveMesh.SkinnedMesh Skin)> legs)
    {
        if (topSkin.VertexCount * 3 != top.Positions.Length) return null;
        var legsEdges = legs.Where(l => l.Skin.VertexCount * 3 == l.Model.Positions.Length)
                            .Select(l => OpenEdgesCache.GetValue(l.Model, m => OpenEdgesOf(m))).ToList();
        // Most tops share no place with any legs file at all: counted on the raw cloth positions first, before the top's
        // own edges are built.
        if (!CoincidesAtLeast(top, legsEdges, LeastSeamPoints)) return null;
        var t = OpenEdgesOf(top);
        var split = new bool[t.Count];
        foreach (var (model, skin) in legs)
        {
            if (skin.VertexCount * 3 != model.Positions.Length) continue;
            // Each legs file's edges once, however many tops are tried against it: built per top, a mod with 39 tops and
            // 21 legs files (Farfalla) took 32 s and 16 GB to find it had no split.
            var l = OpenEdgesCache.GetValue(model, m => OpenEdgesOf(m));

            // A face of the top lying on a face of the legs file corner for corner is a surface both carry.
            var copied = new bool[t.Count];
            int Match(int n)
            {
                foreach (int m in l.Near(t.NodeAt[n])) return m;
                return -1;
            }
            var mapped = new int[t.Count];
            for (int n = 0; n < t.Count; n++) mapped[n] = Match(n);
            foreach (var (a, b, c) in t.Faces)
            {
                int ma = mapped[a], mb = mapped[b], mc = mapped[c];
                if (ma < 0 || mb < 0 || mc < 0 || ma == mb || mb == mc || ma == mc) continue;
                if (l.Faces.Contains(Sorted(ma, mb, mc))) copied[a] = copied[b] = copied[c] = true;
            }

            for (int n = 0; n < t.Count; n++)
            {
                if (split[n] || !t.Open[n] || copied[n]) continue;
                foreach (int m in l.Near(t.NodeAt[n]))
                    if (l.Open[m] && WeightedAlike(t.VertsOf[n], topSkin, l.VertsOf[m], skin))
                    {
                        split[n] = true;
                        break;
                    }
            }
        }

        var at = new List<Vector3>();
        var weights = new List<List<(string Bone, float W)>[]>();
        for (int n = 0; n < t.Count; n++)
        {
            if (!split[n]) continue;
            at.Add(t.NodeAt[n]);
            weights.Add([.. t.VertsOf[n].Select(v => Influences(topSkin, v))]);
        }
        return at.Count >= LeastSeamPoints ? new SeamPoints([.. at], [.. weights]) : null;
    }

    /// <summary>Whether at least <paramref name="least"/> of the model's cloth vertices lie within <see cref="SnapEps"/> of a
    /// node of any of <paramref name="others"/>.</summary>
    private static bool CoincidesAtLeast(ModelParts model, IReadOnlyList<OpenEdges> others, int least)
    {
        if (others.Count == 0) return false;
        int vc = model.Positions.Length / 3, found = 0;
        var seen = new bool[vc];
        foreach (var part in model.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
            {
                if (v < 0 || v >= vc || seen[v]) continue;
                seen[v] = true;
                var p = new Vector3(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);
                if (others.Any(o => o.Near(p).Any()) && ++found >= least) return true;
            }
        }
        return false;
    }

    private static bool WeightedAlike(List<int> a, XivLiveMesh.SkinnedMesh aSkin, List<int> b, XivLiveMesh.SkinnedMesh bSkin)
    {
        foreach (int i in a)
        {
            var wi = Influences(aSkin, i);
            foreach (int j in b)
                if (Difference(wi, Influences(bSkin, j)) <= LayerSameWeights) return true;
        }
        return false;
    }

    /// <summary>
    /// The vertices of <paramref name="garment"/> on the seam top's split <paramref name="points"/>: every raw vertex,
    /// front and back sheet, of a garment open-edge node within <see cref="SnapEps"/> of a split place and weighted like
    /// it. <c>Coverage</c> is the share of the split places met so.
    /// </summary>
    internal static (int[] Vertices, int Positions, float Coverage) SeamVertices(ModelParts garment,
                                                                               XivLiveMesh.SkinnedMesh skin,
                                                                               SeamPoints points)
    {
        if (points.Count == 0 || skin.VertexCount * 3 != garment.Positions.Length) return ([], 0, 0f);
        var g = OpenEdgesOf(garment);
        var verts = new SortedSet<int>();
        int met = 0;
        for (int k = 0; k < points.Count; k++)
        {
            bool hit = false;
            foreach (int n in g.Near(points.At[k]))
            {
                if (!g.Open[n]) continue;
                bool alike = false;
                foreach (int v in g.VertsOf[n])
                {
                    var wv = Influences(skin, v);
                    if (points.Weights[k].Any(w => Difference(wv, w) <= LayerSameWeights)) { alike = true; break; }
                }
                if (!alike) continue;
                hit = true;
                foreach (int v in g.VertsOf[n]) verts.Add(v);
            }
            if (hit) met++;
        }
        return ([.. verts], met, (float)met / points.Count);
    }

    /// <summary>What <see cref="PinSharedSeam"/> did.</summary>
    /// <param name="Kept">The hold stands; false when nothing was held or the fallback dropped it.</param>
    /// <param name="Dropped">Why the fallback let the hold go; null when it did not.</param>
    /// <param name="SeamNodes">Per node, held at the seam (all false when not kept).</param>
    /// <param name="VertexFade">Per vertex, how much of the refit's weight change it takes: 0 at the seam, 1 past the
    /// band. Null when not kept.</param>
    internal sealed record SeamPin(bool Kept, string? Dropped, int PinnedNodes, int PinnedVertices, int Skipped,
                                   int BandHeld, int BandNear, bool[] SeamNodes, float[]? VertexFade);

    /// <summary>
    /// Put a shared seam's vertices back where the author put them, and fade that correction into the rest of the
    /// piece.
    /// <para/>
    /// A mod split at the waist — top and legs two models whose edges coincide, weighted alike — is refitted one piece at
    /// a time, each from the body it reads as made on. Sarelle's legs read as Bibo+ and its top as Tre: the legs moved
    /// their waist by Bibo+'s difference from Tre (up to 10 mm) while the top's stayed, and a seam opened that the
    /// originals never show. Held to where the author put them (D = 0) in both pieces, the two edges meet again whatever
    /// sizes are chosen and in whatever order — the gate (<c>RefitCore.ResolveSeam</c>) only lets it happen where the
    /// seam's own top was made on the body refitted onto.
    /// <para/>
    /// The correction (the refit's own move, undone) spreads from the seam over <see cref="SeamBlendReach"/> along the
    /// cloth's edges, its layer partners and the pieces covering it, smoothed by <see cref="SeamBlendRounds"/>. A band
    /// node may not end nearer the body than <c>min(where it was, </c><see cref="Clearance"/><c>)</c>: its correction is
    /// halved until it does not. If the hold adds a folded triangle or buries a cloth node, the whole hold is let go.
    /// </summary>
    /// <param name="guard">How far outside every target body in play a point is (see <see cref="SignedOff"/>).</param>
    internal static SeamPin PinSharedSeam(Sets sets, SharedSeam seam, Vec3[] nodeDelta, bool[]? stay,
                                          List<(int Node, float W)>?[]? layers, IReadOnlyList<Covered>? covered,
                                          Func<Vector3, float>? guard, int vertexCount)
    {
        var cloth = new bool[sets.NodeCount];
        foreach (int n in sets.ClothNodes) cloth[n] = true;
        bool Free(int n) => cloth[n] && (stay == null || !stay[n]);

        var seed = new bool[sets.NodeCount];
        var skippedNodes = new HashSet<int>();
        foreach (int v in seam.Vertices)
        {
            if (v < 0 || v >= sets.NodeOf.Length) continue;
            int n = sets.NodeOf[v];
            if (Free(n)) seed[n] = true;
            else skippedNodes.Add(n);
        }
        int seeds = seed.Count(s => s);
        if (seeds == 0)
            return new SeamPin(false, null, 0, 0, skippedNodes.Count, 0, 0, new bool[sets.NodeCount], null);

        var snapshot = (Vec3[])nodeDelta.Clone();
        int foldsBefore = FoldedTriangles(sets, nodeDelta);
        int buriedBefore = Buried(sets, nodeDelta, guard);

        // The links the band travels: the cloth's own edges, its layer partners, and the faces covering it.
        var extra = new List<int>[sets.NodeCount];
        void Link(int a, int b)
        {
            if (a == b) return;
            (extra[a] ??= []).Add(b);
            (extra[b] ??= []).Add(a);
        }
        if (layers != null)
            for (int n = 0; n < layers.Length; n++)
                if (layers[n] is { } partners)
                    foreach (var (m, _) in partners) Link(n, m);
        if (covered != null)
            foreach (var c in covered)
            {
                Link(c.Node, c.A);
                Link(c.Node, c.B);
                Link(c.Node, c.C);
            }

        // Nearest seam along those links, and its correction.
        var dist = new float[sets.NodeCount];
        Array.Fill(dist, float.PositiveInfinity);
        var corr = new Vec3[sets.NodeCount];
        var queue = new PriorityQueue<int, float>();
        for (int n = 0; n < sets.NodeCount; n++)
            if (seed[n])
            {
                dist[n] = 0f;
                corr[n] = new Vec3(-nodeDelta[n].X, -nodeDelta[n].Y, -nodeDelta[n].Z);
                queue.Enqueue(n, 0f);
            }
        while (queue.TryDequeue(out int n, out float d))
        {
            if (d > dist[n]) continue;
            void Visit(int m)
            {
                if (!Free(m)) return;
                float nd = d + Vector3.Distance(ToVector(sets.NodeAt[n]), ToVector(sets.NodeAt[m]));
                if (nd >= SeamBlendReach || nd >= dist[m]) return;
                dist[m] = nd;
                corr[m] = corr[n];
                queue.Enqueue(m, nd);
            }
            foreach (int m in sets.Adj[n]) Visit(m);
            if (extra[n] != null) foreach (int m in extra[n]) Visit(m);
        }

        var weight = new float[sets.NodeCount];
        for (int n = 0; n < sets.NodeCount; n++)
            weight[n] = seed[n] ? 1f : float.IsFinite(dist[n]) ? 1f - MeshMath.Smoothstep(dist[n] / SeamBlendReach) : 0f;
        var band = new Vec3[sets.NodeCount];
        for (int n = 0; n < sets.NodeCount; n++)
            band[n] = new Vec3(corr[n].X * weight[n], corr[n].Y * weight[n], corr[n].Z * weight[n]);

        for (int round = 0; round < SeamBlendRounds; round++)
        {
            var next = (Vec3[])band.Clone();
            for (int n = 0; n < sets.NodeCount; n++)
            {
                if (seed[n] || weight[n] <= 0f) continue;
                float x = band[n].X, y = band[n].Y, z = band[n].Z;
                int k = 1;
                foreach (int m in sets.Adj[n])
                {
                    if (!Free(m)) continue;
                    x += band[m].X; y += band[m].Y; z += band[m].Z;
                    k++;
                }
                next[n] = new Vec3(x / k, y / k, z / k);
            }
            band = next;
        }

        int bandHeld = 0, bandNear = 0;
        for (int n = 0; n < sets.NodeCount; n++)
        {
            if (seed[n])
            {
                nodeDelta[n] = default;
                continue;
            }
            if (weight[n] <= 0f || !Free(n)) continue;
            var c = band[n];
            if (c.X == 0f && c.Y == 0f && c.Z == 0f) continue;
            var start = ToVector(sets.NodeAt[n]) + ToVector(nodeDelta[n]);
            float scale = 1f;
            if (guard != null)
            {
                float s0 = guard(start);
                float floor = MathF.Min(s0, Clearance);
                int halvings = 0;
                while (guard(start + ToVector(c) * scale) < floor - UnfoldBuryTolerance && halvings < 16)
                {
                    scale *= 0.5f;
                    halvings++;
                }
                if (halvings == 16) scale = 0f;
                if (halvings > 0) bandHeld++;
                if (guard(start + ToVector(c) * scale) < Clearance) bandNear++;
            }
            nodeDelta[n] = new Vec3(nodeDelta[n].X + c.X * scale, nodeDelta[n].Y + c.Y * scale,
                                    nodeDelta[n].Z + c.Z * scale);
        }

        int foldsAfter = FoldedTriangles(sets, nodeDelta);
        int buriedAfter = Buried(sets, nodeDelta, guard);
        int pinnedVertices = seam.Vertices.Count(v => v >= 0 && v < sets.NodeOf.Length && seed[sets.NodeOf[v]]);
        if (foldsAfter > foldsBefore || buriedAfter > buriedBefore)
        {
            Array.Copy(snapshot, nodeDelta, nodeDelta.Length);
            string why = foldsAfter > foldsBefore
                ? $"holding it folded {foldsAfter - foldsBefore} more triangle(s)"
                : $"holding it put {buriedAfter - buriedBefore} more cloth point(s) inside the body";
            return new SeamPin(false, why, seeds, pinnedVertices, skippedNodes.Count, bandHeld, bandNear,
                               new bool[sets.NodeCount], null);
        }

        var fade = new float[vertexCount];
        for (int v = 0; v < vertexCount && v < sets.NodeOf.Length; v++) fade[v] = 1f - weight[sets.NodeOf[v]];
        return new SeamPin(true, null, seeds, pinnedVertices, skippedNodes.Count, bandHeld, bandNear, seed, fade);
    }

    /// <summary>Cloth nodes deeper than <see cref="UnfoldBuryTolerance"/> inside the guard surface; 0 without one.</summary>
    private static int Buried(Sets sets, Vec3[] nodeDelta, Func<Vector3, float>? guard)
    {
        if (guard == null) return 0;
        int buried = 0;
        foreach (int n in sets.ClothNodes)
            if (guard(ToVector(sets.NodeAt[n]) + ToVector(nodeDelta[n])) < -UnfoldBuryTolerance) buried++;
        return buried;
    }

    /// <summary>
    /// The weights the refit gave each vertex, faded back toward the author's by <paramref name="fade"/> (0 the author's,
    /// 1 the refit's): the seam keeps the author's weights in both pieces, so it stays closed when the body moves. A
    /// vertex sharing a place with a seam vertex (<paramref name="classOf"/>) keeps the author's too.
    /// </summary>
    internal static void FadeSeamWeights(XivLiveMesh.SkinnedMesh own, (string Bone, float W)[]?[] result, float[] fade,
                                         int[]? classOf)
    {
        int vc = Math.Min(result.Length, fade.Length);
        var f = new float[vc];
        Array.Copy(fade, f, vc);
        if (classOf != null)
        {
            var authoredClass = new HashSet<int>();
            for (int v = 0; v < vc && v < classOf.Length; v++)
                if (f[v] <= 0f && classOf[v] >= 0) authoredClass.Add(classOf[v]);
            for (int v = 0; v < vc && v < classOf.Length; v++)
                if (classOf[v] >= 0 && authoredClass.Contains(classOf[v])) f[v] = 0f;
        }
        for (int v = 0; v < vc; v++)
        {
            if (f[v] >= 1f || result[v] == null) continue;
            if (f[v] <= 0f)
            {
                result[v] = null;
                continue;
            }
            result[v] = MeshMath.BlendWeights([.. Influences(own, v)], 1f - f[v], result[v]!, f[v], [], 0f, MaxInfluences);
        }
    }
}
