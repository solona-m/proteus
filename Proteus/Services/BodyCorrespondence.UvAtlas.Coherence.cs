using System;
using System.Collections.Generic;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// The landing-coherence guard: a landing the skin around it does not agree with is not trusted.
/// <para/>
/// A texture coordinate can land on the wrong place. Tight&amp;Firm Gen2's crotch midline lands 17–20 cm off on Bibo+ —
/// the mirrored sheet's seam meets the doubled one there — and a gen3 map's nearest-pixel search snaps onto a
/// neighbouring island. Counted as landed, such a point is a spike, and the carry spreads it to the skin it fills. A
/// body-to-body field changes slowly over the surface, so a landing that most of its neighbours disagree with, by more
/// than their distance allows, is set aside: it follows the skin around it like a point with no place at all.
/// </summary>
internal sealed partial class UvAtlasCorrespondence
{
    /// <summary>
    /// Who judges a landing: the skin within this many hops of it. Not a distance: Tight&amp;Firm's shin has edges up to
    /// 116 mm and its crotch 2 mm ones, so any one radius is blind on one or spans whole regions of the other. A 1-ring
    /// holds 4–6 neighbours on every body measured, so two rings hold 8 or more wherever the skin is placed.
    /// </summary>
    private const int CoherenceRings = 2;

    /// <summary>
    /// What two landings may disagree by at no distance at all (10 mm): a landing is as precise as the target's facets,
    /// and the targets' crotches are cut in 4–5 mm edges.
    /// </summary>
    private const float CoherenceFloor = 0.01f;

    /// <summary>
    /// What two landings may further disagree by, per metre between them: half. An edge turns over when its ends move
    /// apart by its own length; a body-to-body field bends nowhere near half that. The landings known wrong — 167–203 mm,
    /// two rings from skin 50 mm away — disagree by more than four times what this allows.
    /// </summary>
    private const float CoherenceStrain = 0.5f;

    /// <summary>The fewest neighbours whose majority can outvote one wrong neighbour.</summary>
    private const int MinVouchers = 3;

    /// <summary>
    /// How many times the landings are judged again, each time against what the last round kept: a pair of wrong
    /// landings that vouched for a third can be outvoted once they are gone.
    /// </summary>
    private const int CoherenceRounds = 3;

    /// <summary>What the guard set aside, and how the rest stood.</summary>
    /// <param name="Disagreed">Landings most of whose neighbours disagreed with them by more than their distance allows.</param>
    /// <param name="Unvouched">Landings with too few placed neighbours to judge, inside skin with no place: amid it, a lone
    /// landing cannot be told right from wrong.</param>
    /// <param name="Unjudged">Landings with too few placed neighbours but none unplaced beside them — a small piece of its
    /// own. Kept: skin is never set aside for being sparse.</param>
    /// <param name="P99Ratio">Of the landings judged and kept in the last round, the 99th percentile of how far they
    /// disagree with their neighbours, as a share of what is allowed (1 = the limit).</param>
    /// <param name="MaxRatio">The same, the largest.</param>
    internal readonly record struct Coherence(int Disagreed, int Unvouched, int Unjudged, float P99Ratio, float MaxRatio);

    /// <summary>
    /// Sets aside (nulls in <paramref name="field"/>) the landings of <paramref name="skin"/> that the skin around them
    /// does not agree with. Positions only. Vertices of <paramref name="offSheet"/> are never judged; with no landing,
    /// they count as skin with no place.
    /// </summary>
    internal static Coherence Distrust(ModelParts source, List<int> skin, HashSet<int> offSheet, Vector3?[] field)
    {
        var nodes = SkinNodes.Build(source, skin);
        int disagreed = 0, unvouched = 0, unjudged = 0;
        var ratios = new List<float>();
        var votes = new Vector3?[nodes.Count];
        for (int round = 0; round < CoherenceRounds; round++)
        {
            for (int n = 0; n < nodes.Count; n++) votes[n] = nodes.Vote(n, field);

            var setAside = new List<int>();
            int roundUnjudged = 0;
            ratios.Clear();
            int roundDisagreed = 0, roundUnvouched = 0;
            foreach (int v in skin)
            {
                if (offSheet.Contains(v) || field[v] is not { } d) continue;
                int node = nodes.NodeOf[v];
                var near = nodes.Near[node];
                var judged = new List<float>(near.Length);
                foreach (var (u, distance) in near)
                    if (votes[u] is { } vote)
                        judged.Add(Vector3.Distance(d, vote) / (CoherenceFloor + CoherenceStrain * distance));

                if (judged.Count < MinVouchers)
                {
                    bool amidNoPlace = false;
                    foreach (int u in nodes.Ring1[node])
                        if (votes[u] == null) { amidNoPlace = true; break; }
                    if (amidNoPlace) { setAside.Add(v); roundUnvouched++; }
                    else roundUnjudged++;
                    continue;
                }
                judged.Sort();
                float q = judged[(judged.Count - 1) / 2];
                if (q > 1f) { setAside.Add(v); roundDisagreed++; }
                else ratios.Add(q);
            }

            unjudged = roundUnjudged;
            if (setAside.Count == 0) break;
            foreach (int v in setAside) field[v] = null;
            disagreed += roundDisagreed;
            unvouched += roundUnvouched;
        }

        ratios.Sort();
        return new Coherence(disagreed, unvouched, unjudged,
                             ratios.Count > 0 ? ratios[Math.Min(ratios.Count - 1, (int)(ratios.Count * 0.99f))] : 0f,
                             ratios.Count > 0 ? ratios[^1] : 0f);
    }

    /// <summary>
    /// The source's skin as POSITIONS: the vertices a uv seam splits are one point of the surface, so they are one node,
    /// judged together and voting once — a welded pair can never vouch for itself.
    /// </summary>
    internal sealed class SkinNodes
    {
        /// <summary>Each vertex's node; -1 for a vertex that is not skin.</summary>
        public readonly int[] NodeOf;

        /// <summary>The skin vertices of each node.</summary>
        public readonly List<List<int>> Members = [];

        /// <summary>The nodes one hop from each node.</summary>
        public int[][] Ring1 = [];

        /// <summary>The nodes within <see cref="CoherenceRings"/> hops of each node, itself excluded, and how far each is.</summary>
        public (int Node, float Distance)[][] Near = [];

        private readonly List<Vector3> positions = [];

        public int Count => Members.Count;

        private SkinNodes(int vertexCount)
        {
            NodeOf = new int[vertexCount];
            Array.Fill(NodeOf, -1);
        }

        public static SkinNodes Build(ModelParts source, List<int> skin)
        {
            int vc = source.Positions.Length / 3;
            var nodes = new SkinNodes(vc);
            Vector3 P(int v) => new(source.Positions[v * 3], source.Positions[v * 3 + 1], source.Positions[v * 3 + 2]);

            var byKey = new Dictionary<(int, int, int), int>();
            foreach (int v in skin)
            {
                var p = P(v);
                var key = MeshMath.PositionKey(new SecondSkinWriter.Vec3(p.X, p.Y, p.Z), WeldPerMetre);
                if (!byKey.TryGetValue(key, out int node))
                {
                    byKey[key] = node = nodes.Members.Count;
                    nodes.Members.Add([]);
                    nodes.positions.Add(p);
                }
                nodes.Members[node].Add(v);
                nodes.NodeOf[v] = node;
            }

            var links = new HashSet<int>[nodes.Count];
            for (int n = 0; n < links.Length; n++) links[n] = [];
            foreach (var part in source.Parts)
            {
                if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = part.Triangles[t + k], b = part.Triangles[t + (k + 1) % 3];
                        if (a < 0 || b < 0 || a >= vc || b >= vc) continue;
                        int na = nodes.NodeOf[a], nb = nodes.NodeOf[b];
                        if (na < 0 || nb < 0 || na == nb) continue;
                        links[na].Add(nb);
                        links[nb].Add(na);
                    }
            }

            nodes.Ring1 = new int[nodes.Count][];
            nodes.Near = new (int, float)[nodes.Count][];
            for (int n = 0; n < nodes.Count; n++)
            {
                nodes.Ring1[n] = [.. links[n]];
                var within = new HashSet<int>(links[n]);
                var frontier = links[n];
                for (int hop = 1; hop < CoherenceRings; hop++)
                {
                    var next = new HashSet<int>();
                    foreach (int m in frontier)
                        foreach (int w in links[m])
                            if (w != n && within.Add(w)) next.Add(w);
                    frontier = next;
                }
                var near = new (int, float)[within.Count];
                int i = 0;
                foreach (int m in within) near[i++] = (m, Vector3.Distance(nodes.positions[n], nodes.positions[m]));
                nodes.Near[n] = near;
            }
            return nodes;
        }

        /// <summary>
        /// What a node says the field is at its position: the component-wise median of its members' landings, or null
        /// when none of them has one.
        /// </summary>
        public Vector3? Vote(int node, Vector3?[] field)
        {
            var xs = new List<float>(2);
            var ys = new List<float>(2);
            var zs = new List<float>(2);
            foreach (int v in Members[node])
                if (field[v] is { } d) { xs.Add(d.X); ys.Add(d.Y); zs.Add(d.Z); }
            if (xs.Count == 0) return null;
            return new Vector3(Median(xs), Median(ys), Median(zs));
        }

        private static float Median(List<float> values)
        {
            values.Sort();
            int mid = values.Count / 2;
            return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2f;
        }
    }
}
