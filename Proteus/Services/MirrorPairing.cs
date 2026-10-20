using System;
using System.Collections.Generic;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// Finds, for a mirrored Move, Rotate or Scale, the cloth on the other side of X = 0 that matches a selection: the
/// node each selected node reflects onto, and the whole matching region however differently that side is meshed.
/// <para/>
/// Skin is never a match: tight cloth sits a millimetre over the body, and the body's own asymmetry would otherwise
/// make skin the nearest node. Among cloth, the same material facing the mirrored way is preferred, so the jacket
/// over a bra is not taken for the bra. Authored positions are searched first, so an earlier one-sided edit does not
/// break the pairing; positions as they now stand are the fallback, for a piece the user has since straightened.
/// </summary>
internal sealed class MirrorPairing
{
    private readonly Vec3[] at;
    private readonly Vec3[] normal;
    private readonly bool[] normalUsable;
    private readonly bool[] skin;
    private readonly int[] material;
    private readonly List<int>[] adj;

    /// <summary>Per node, how far its reflection may land from its match: about one local edge.</summary>
    private readonly float[] tol;

    private readonly float cell;
    private Dictionary<(int, int, int), List<int>>? authoredGrid;
    private Dictionary<(int, int, int), List<int>>? currentGrid;
    private int currentVersion = -1;

    /// <summary>
    /// How much nearer than its own tolerance a far-side node's reflection must land on a selected node to join the
    /// matching region from that side: under half the way to the next ring of the selection, so a same-density far
    /// side gets exactly the mirror image and a finer one gets every node inside it.
    /// </summary>
    private const float ReverseShare = 0.6f;

    /// <param name="at">Authored node positions.</param>
    /// <param name="normal">Authored node normals, unit length.</param>
    /// <param name="normalUsable">Whether a node's normal means anything: false where opposite normals were welded
    /// (a double-sided card), which then skips the facing test.</param>
    /// <param name="skin">Skin nodes, never a match.</param>
    /// <param name="material">A material id per node; −1 for none.</param>
    /// <param name="adj">Node adjacency.</param>
    /// <param name="meanEdge">The mesh's mean edge length.</param>
    internal MirrorPairing(Vec3[] at, Vec3[] normal, bool[] normalUsable, bool[] skin, int[] material,
                           List<int>[] adj, float meanEdge)
    {
        this.at = at;
        this.normal = normal;
        this.normalUsable = normalUsable;
        this.skin = skin;
        this.material = material;
        this.adj = adj;

        float edge = MathF.Max(meanEdge, 1e-4f);
        TolCap = 2f * edge;
        cell = TolCap;
        tol = new float[at.Length];
        for (int n = 0; n < at.Length; n++)
        {
            float longest = 0f;
            foreach (int m in adj[n]) longest = MathF.Max(longest, Distance(at[n], at[m]));
            tol[n] = Math.Clamp(longest, edge, TolCap);
        }
    }

    /// <summary>The farthest any reflection may land from its match: two mean edges, so a coarse garment cannot reach
    /// into another layer centimetres away.</summary>
    internal float TolCap { get; }

    /// <summary>What <see cref="Pair"/> found.</summary>
    /// <param name="Partner">Each selected node with a movable match, and that match (itself on the centre line).</param>
    /// <param name="Held">Selected nodes whose best match is locked: matched, but nothing there may move.</param>
    /// <param name="Reverse">Far-side nodes that reflect onto a selected node, each with that node — the matching
    /// region as the far side is meshed, kept only where it is joined to a <paramref name="Partner"/>.</param>
    internal sealed record Pairs(Dictionary<int, int> Partner, HashSet<int> Held, Dictionary<int, int> Reverse);

    /// <summary>Pair <paramref name="seeds"/> with the other side.</summary>
    /// <param name="locked">Locked nodes: a match, but held.</param>
    /// <param name="here">A node's position as it now stands.</param>
    /// <param name="editVersion">The edit <paramref name="here"/> reflects; the current-position grid is rebuilt
    /// whenever it changes.</param>
    internal Pairs Pair(IReadOnlyCollection<int> seeds, bool[] locked, Func<int, Vec3> here, int editVersion)
    {
        var partner = new Dictionary<int, int>();
        var held = new HashSet<int>();
        authoredGrid ??= BuildGrid(n => at[n]);

        foreach (int s in seeds)
        {
            int p = Best(s, Reflect(at[s]), authoredGrid, n => at[n]);
            if (p < 0)
            {
                if (currentGrid == null || currentVersion != editVersion)
                {
                    currentGrid = BuildGrid(here);
                    currentVersion = editVersion;
                }
                p = Best(s, Reflect(here(s)), currentGrid, here);
            }
            if (p < 0) continue;
            // The best match is the match: a locked one holds, it does not hand over to a worse candidate.
            if (locked[p]) held.Add(s);
            else partner[s] = p;
        }

        return new Pairs(partner, held, ReverseRegion(seeds, locked, partner));
    }

    private static Vec3 Reflect(Vec3 v) => new(-v.X, v.Y, v.Z);

    private static float Distance(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private (int, int, int) CellOf(Vec3 p)
        => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));

    private Dictionary<(int, int, int), List<int>> BuildGrid(Func<int, Vec3> pos)
    {
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int n = 0; n < at.Length; n++)
        {
            if (skin[n]) continue;
            var key = CellOf(pos(n));
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(n);
        }
        return grid;
    }

    /// <summary>
    /// How well <paramref name="candidate"/> matches <paramref name="seed"/>: 1 for the same material facing the
    /// mirrored way, 2 for another material facing the mirrored way, 3 for any other cloth.
    /// </summary>
    private int Tier(int seed, int candidate)
    {
        bool facing = !normalUsable[seed] || !normalUsable[candidate]
                      || -normal[seed].X * normal[candidate].X + normal[seed].Y * normal[candidate].Y
                         + normal[seed].Z * normal[candidate].Z > 0.5f;
        if (!facing) return 3;
        return material[seed] == material[candidate] ? 1 : 2;
    }

    /// <summary>The best cloth node within the seed's tolerance of <paramref name="q"/>, by tier and then distance;
    /// −1 when there is none.</summary>
    private int Best(int seed, Vec3 q, Dictionary<(int, int, int), List<int>> grid, Func<int, Vec3> pos)
    {
        var (cx, cy, cz) = CellOf(q);
        int best = -1, bestTier = int.MaxValue;
        float bestD = float.MaxValue, reach = tol[seed];
        for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++)
        for (int z = -1; z <= 1; z++)
        {
            if (!grid.TryGetValue((cx + x, cy + y, cz + z), out var list)) continue;
            foreach (int c in list)
            {
                float d = Distance(pos(c), q);
                if (d > reach) continue;
                int tier = Tier(seed, c);
                if (tier < bestTier || (tier == bestTier && d < bestD) || (tier == bestTier && d == bestD && c < best))
                {
                    best = c;
                    bestTier = tier;
                    bestD = d;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// The matching region as the far side is meshed: every movable cloth node whose reflection lands near a selected
    /// node of its own material and facing. Kept only where it is joined, through itself and the partners, to a
    /// partner — a frill row or a pleat that merely lies within reach of the reflection is not the match.
    /// </summary>
    private Dictionary<int, int> ReverseRegion(IReadOnlyCollection<int> seeds, bool[] locked, Dictionary<int, int> partner)
    {
        var found = new Dictionary<int, int>();
        if (partner.Count == 0) return found;

        // The selection's reflections, bucketed, and their bounds.
        var grid = new Dictionary<(int, int, int), List<int>>();
        var lo = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var hi = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (int s in seeds)
        {
            var r = Reflect(at[s]);
            var key = CellOf(r);
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(s);
            lo = new Vec3(MathF.Min(lo.X, r.X), MathF.Min(lo.Y, r.Y), MathF.Min(lo.Z, r.Z));
            hi = new Vec3(MathF.Max(hi.X, r.X), MathF.Max(hi.Y, r.Y), MathF.Max(hi.Z, r.Z));
        }

        float pad = TolCap;
        for (int m = 0; m < at.Length; m++)
        {
            if (skin[m] || locked[m]) continue;
            var p = at[m];
            if (p.X < lo.X - pad || p.Y < lo.Y - pad || p.Z < lo.Z - pad
                || p.X > hi.X + pad || p.Y > hi.Y + pad || p.Z > hi.Z + pad)
                continue;

            var (cx, cy, cz) = CellOf(p);
            int best = -1;
            float bestD = float.MaxValue;
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
            {
                if (!grid.TryGetValue((cx + x, cy + y, cz + z), out var list)) continue;
                foreach (int s in list)
                {
                    float d = Distance(Reflect(at[s]), p);
                    if (d > ReverseShare * tol[s] || d >= bestD || Tier(s, m) != 1) continue;
                    best = s;
                    bestD = d;
                }
            }
            if (best >= 0) found[m] = best;
        }

        // Keep the parts of the region joined to a partner.
        var region = new HashSet<int>(found.Keys);
        region.UnionWith(partner.Values);
        var kept = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (int p in partner.Values)
            if (kept.Add(p)) queue.Enqueue(p);
        while (queue.Count > 0)
        {
            int n = queue.Dequeue();
            foreach (int m in adj[n])
                if (region.Contains(m) && kept.Add(m)) queue.Enqueue(m);
        }
        var joined = new Dictionary<int, int>();
        foreach (var (m, s) in found)
            if (kept.Contains(m)) joined[m] = s;
        return joined;
    }
}
