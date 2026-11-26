using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services.Bulge;

/// <summary>
/// The bulge fitted onto a hole that does not take it point for point (plan genital-switches, part 2): "when there isn't a
/// perfect fit to the bulge on one size of a body, just use the one I have anyway". The hole is read off the body's own skin
/// (<see cref="HoleLoop"/>), not the piece's rim: TBSE-X's Flaccid L piece has three rim points off the skin round a hole of
/// the bulge's own 28, so the exact match refused it — read off the skin it fits within <see cref="HoleFit"/> (10.6 mm). A
/// hole of another count is aligned with exactly |m − n| steps of one kind — hole points merging onto a body point they share
/// (fewer body points), or body points added to the bulge's rim (more) — and the bulge drawn on that.
/// </summary>
internal static partial class BulgeSwap
{
    /// <summary>
    /// How far a hole of another point count may lie from the bulge's, by the aligned loops' worst pair (12.6 mm): Phase 0.2b
    /// measured every TBSE-X legs hole at the bulge's own 28 points — Flaccid L 10.6 mm, Flaccid XL 19.5 mm — so no installed
    /// body takes a re-stitch yet, and the reach is Flaccid L's worst and 2 mm, the plan's smaller-scope choice over a flat
    /// 25 mm. One constant for the re-stitch's acceptance and for a re-stitched bulge's recognition
    /// (<see cref="GenitalParts.ShippedBulgeBranch"/>), so widening one widens the other.
    /// </summary>
    internal const float RestitchReach = 0.0126f;

    /// <summary>The hole point counts a re-stitch takes (21–37 against the bulge's 28): beyond them, not the same hole.</summary>
    internal const int RestitchBandMin = 21, RestitchBandMax = 37;

    /// <summary>
    /// The bulge as drawn on one body's hole: per vertex its position, uv, landing uv, the body vertex whose weights and
    /// normal it takes (its rim, −1 inside) and the asset vertex it comes from (−1 for one added on the rim); the triangles
    /// over those vertices, each with the asset triangle it comes from (<see cref="Parent"/>), for the self-check.
    /// </summary>
    internal sealed record FittedBulge(Vector3[] Positions, Vector2[] Uv, float[]?[] Land, int[] RimBody, int[] Source,
                                       int[] Triangles, int[] Parent, int Merged, int Inserted, float Worst)
    {
        /// <summary>The body's hole, as drawn: the body vertices the rim lies on.</summary>
        public IReadOnlyList<int> Hole { get; init; } = [];
    }

    /// <summary>The exact fit (<see cref="MatchHole"/>): the asset as it is, warped onto the body's hole point for point.</summary>
    internal static FittedBulge Exact(TbseBulge bulge, ModelParts parts, int[] bodyAt)
    {
        int n = bulge.Positions.Length;
        var positions = Warp(bulge, bulge.Hole.Select((_, h) => At(parts, bodyAt[h]) - bulge.Hole[h]).ToArray());
        return new FittedBulge(positions, bulge.Data.uv.Select(u => new Vector2(u[0], u[1])).ToArray(), bulge.Data.land,
                               Enumerable.Range(0, n).Select(i => bulge.Data.rim[i] >= 0 ? bodyAt[bulge.Data.rim[i]] : -1).ToArray(),
                               Enumerable.Range(0, n).ToArray(), bulge.Data.triangles,
                               Enumerable.Range(0, bulge.Data.triangles.Length / 3).ToArray(), 0, 0, 0f) { Hole = bodyAt };
    }

    /// <summary>
    /// The hole <paramref name="piece"/> plugs, as the body's OTHER skin draws it: that skin's open edges, welded by
    /// position, both of whose ends are points of the piece's own open rim — walked as one loop (as body vertex indices);
    /// null when it is not one loop of points each on two such edges. The skin's, not the piece's: TBSE-X Flaccid L's piece
    /// has a 28-point rim round a 25-point hole (three of its points lie along the hole's edges), and the bulge must close
    /// the hole the skin leaves, or a crack shows.
    /// </summary>
    internal static List<int>? HoleLoop(ModelParts parts, ModelPart piece)
    {
        (long, long, long) K(Vector3 p) => ((long)MathF.Round(p.X * 1e5f), (long)MathF.Round(p.Y * 1e5f), (long)MathF.Round(p.Z * 1e5f));
        ((long, long, long), (long, long, long)) E((long, long, long) a, (long, long, long) b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);
        var pieceUse = new Dictionary<((long, long, long), (long, long, long)), int>();
        for (int t = 0; t + 2 < piece.Triangles.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                var e = E(K(At(parts, piece.Triangles[t + k])), K(At(parts, piece.Triangles[t + (k + 1) % 3])));
                pieceUse[e] = pieceUse.GetValueOrDefault(e) + 1;
            }
        var rim = pieceUse.Where(kv => kv.Value == 1).SelectMany(kv => new[] { kv.Key.Item1, kv.Key.Item2 }).ToHashSet();
        var otherSkin = new Dictionary<(long, long, long), int>();
        var use = new Dictionary<((long, long, long), (long, long, long)), int>();
        foreach (var part in parts.Parts.Where(p => p.Island < 0 && p != piece && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int va = part.Triangles[t + k], vb = part.Triangles[t + (k + 1) % 3];
                    var ka = K(At(parts, va));
                    otherSkin.TryAdd(ka, va);
                    var e = E(ka, K(At(parts, vb)));
                    use[e] = use.GetValueOrDefault(e) + 1;
                }
        // Every open edge of the other skin (the body's whole boundary: the hole, the waist, the ankles), and the loop of it
        // that runs through the piece's rim, walked from the lowest rim point it holds.
        var adj = new Dictionary<(long, long, long), List<(long, long, long)>>();
        foreach (var (e, _) in use.Where(kv => kv.Value == 1))
        {
            (adj.TryGetValue(e.Item1, out var l1) ? l1 : adj[e.Item1] = []).Add(e.Item2);
            (adj.TryGetValue(e.Item2, out var l2) ? l2 : adj[e.Item2] = []).Add(e.Item1);
        }
        var starts = rim.Where(adj.ContainsKey).ToList();
        if (starts.Count == 0) return null;
        var start = starts.Min();
        if (adj[start].Count != 2) return null;
        var loop = new List<int> { otherSkin[start] };
        var seen = new HashSet<(long, long, long)> { start };
        var prev = start;
        var cur = adj[start][0];
        while (cur != start)
        {
            if (adj[cur].Count != 2 || !seen.Add(cur) || loop.Count > RestitchBandMax) return null;
            loop.Add(otherSkin[cur]);
            var next = adj[cur][0] == prev ? adj[cur][1] : adj[cur][0];
            prev = cur;
            cur = next;
        }
        // The hole: most of its points are the piece's own (a loop elsewhere on the body never is).
        return seen.Count(rim.Contains) * 2 > loop.Count ? loop : null;
    }

    /// <summary>
    /// The alignment of the bulge's hole (<paramref name="hole"/>, n points) with the body's loop (m ≠ n points): over both
    /// directions and every rotation of the body's loop, a monotone pairing in which every step advances the longer side and
    /// exactly |m − n| of them leave the shorter where it was — merges only when the body has fewer points, inserts only when
    /// it has more, so no corner step anywhere (the wrap from the last point to the first is always a diagonal). Among those:
    /// the lowest worst pair, then the lowest summed distance, then the lowest rotation, direction +1 first. <c>Map</c> sends
    /// each point of the longer side to its point on the shorter (indices into the rotated body loop or the hole).
    /// </summary>
    internal static (int Shift, int Dir, int[] Map, float Worst)? Align(IReadOnlyList<Vector3> hole, IReadOnlyList<Vector3> loop)
    {
        int n = hole.Count, m = loop.Count;
        if (n < 2 || m < 2) return null;
        if (n == m)
        {
            // Point for point: the rotation and direction with the lowest worst pair, then the lowest sum.
            (int Shift, int Dir, float Worst, double Sum)? one = null;
            foreach (int dir in new[] { 1, -1 })
                for (int shift = 0; shift < m; shift++)
                {
                    float worst = 0f;
                    double sum = 0;
                    for (int h = 0; h < n; h++)
                    {
                        float d = Vector3.Distance(hole[h], loop[((shift + dir * h) % m + m) % m]);
                        worst = MathF.Max(worst, d);
                        sum += d;
                    }
                    if (one is not { } o || worst < o.Worst || (worst == o.Worst && sum < o.Sum)) one = (shift, dir, worst, sum);
                }
            return one is { } r1 ? (r1.Shift, r1.Dir, Enumerable.Range(0, n).ToArray(), r1.Worst) : null;
        }
        bool fewer = m < n;   // the body has fewer points: the hole is the longer side
        int nl = fewer ? n : m, ns = fewer ? m : n;
        (int Shift, int Dir, int[] Map, float Worst, double Sum)? best = null;
        var bn = new float[nl, ns];
        var sm = new double[nl, ns];
        foreach (int dir in new[] { 1, -1 })
            for (int shift = 0; shift < m; shift++)
            {
                Vector3 B(int j) => loop[((shift + dir * j) % m + m) % m];
                float D(int l, int s) => fewer ? Vector3.Distance(hole[l], B(s)) : Vector3.Distance(B(l), hole[s]);
                bool Ok(int l, int s) => s <= l && ns - 1 - s <= nl - 1 - l;
                // The lowest worst pair.
                for (int l = 0; l < nl; l++)
                    for (int s = 0; s < ns; s++)
                    {
                        bn[l, s] = float.PositiveInfinity;
                        if (!Ok(l, s)) continue;
                        float d = D(l, s);
                        if (l == 0) { if (s == 0) bn[l, s] = d; continue; }
                        float pred = MathF.Min(bn[l - 1, s], s > 0 ? bn[l - 1, s - 1] : float.PositiveInfinity);
                        bn[l, s] = MathF.Max(pred, d);
                    }
                float worst = bn[nl - 1, ns - 1];
                if (!float.IsFinite(worst) || (best is { } b0 && worst > b0.Worst)) continue;
                // The lowest summed distance among the paths no pair of which is worse than that.
                for (int l = 0; l < nl; l++)
                    for (int s = 0; s < ns; s++)
                    {
                        sm[l, s] = double.PositiveInfinity;
                        if (!Ok(l, s)) continue;
                        float d = D(l, s);
                        if (d > worst) continue;
                        if (l == 0) { if (s == 0) sm[l, s] = d; continue; }
                        double pred = Math.Min(sm[l - 1, s], s > 0 ? sm[l - 1, s - 1] : double.PositiveInfinity);
                        sm[l, s] = pred + d;
                    }
                double sum = sm[nl - 1, ns - 1];
                if (best is { } b1 && worst == b1.Worst && sum >= b1.Sum) continue;
                // Back along it, a diagonal first wherever both steps cost the same.
                var map = new int[nl];
                int at = ns - 1;
                for (int l = nl - 1; l >= 0; l--)
                {
                    map[l] = at;
                    if (l == 0) break;
                    double diag = at > 0 ? sm[l - 1, at - 1] : double.PositiveInfinity, stay = sm[l - 1, at];
                    if (diag <= stay) at--;
                }
                best = (shift, dir, map, worst, sum);
            }
        return best is { } r ? (r.Shift, r.Dir, r.Map, r.Worst) : null;
    }

    /// <summary>
    /// The bulge re-stitched onto <paramref name="piece"/>'s hole when its point count differs from the bulge's and lies in
    /// the band, or null with the reason: not one loop or an equal count or outside the band ("no sculpt for this body"),
    /// the aligned rim farther than <see cref="RestitchReach"/> (the distance), a bulge rim edge that is not one triangle.
    /// </summary>
    internal static FittedBulge? FitHole(ModelParts parts, ModelPart piece, TbseBulge bulge, out string reason)
    {
        reason = "no sculpt for this body";
        int n = bulge.Hole.Length;
        if (HoleLoop(parts, piece) is not { } loop) return null;
        int m = loop.Count;
        if (m < RestitchBandMin || m > RestitchBandMax) return null;
        if (Align(bulge.Hole, loop.Select(v => At(parts, v)).ToList()) is not { } al) return null;
        // A hole of the bulge's own count, read off the skin (the piece's rim did not lie on it), is held to the exact
        // reach; another count to the re-stitch's.
        if (m == n ? al.Worst > HoleFit : al.Worst > RestitchReach)
        {
            if (m != n) reason = $"the body's hole is {al.Worst * 1000f:0.0} mm from the bulge's";
            return null;
        }
        var rot = Enumerable.Range(0, m).Select(j => loop[((al.Shift + al.Dir * j) % m + m) % m]).ToArray();
        // Each hole point's partner: its first body point.
        var partner = new int[n];
        if (m <= n) for (int h = 0; h < n; h++) partner[h] = rot[al.Map[h]];
        else
        {
            for (int h = 0; h < n; h++) partner[h] = -1;
            for (int j = 0; j < m; j++) if (partner[al.Map[j]] < 0) partner[al.Map[j]] = rot[j];
        }
        var positions = Warp(bulge, Enumerable.Range(0, n).Select(h => At(parts, partner[h]) - bulge.Hole[h]).ToArray()).ToList();
        int bn = bulge.Positions.Length;
        var uv = bulge.Data.uv.Select(u => new Vector2(u[0], u[1])).ToList();
        var land = bulge.Data.land.ToList();
        var rimBody = Enumerable.Range(0, bn).Select(i => bulge.Data.rim[i] >= 0 ? partner[bulge.Data.rim[i]] : -1).ToList();
        var source = Enumerable.Range(0, bn).ToList();
        var bt = bulge.Data.triangles;
        var tris = new List<int>();
        var parent = new List<int>();
        int merged = 0, inserted = 0;
        if (m < n)
        {
            // Merges: the rim points sharing a body point lie on it together; a triangle two of whose corners do is dropped.
            merged = n - m;
            for (int t = 0; t < bt.Length / 3; t++)
            {
                int a = bt[t * 3], b = bt[t * 3 + 1], c = bt[t * 3 + 2];
                // Two hole points merged onto one body point (never one point's two seam copies, which stay apart).
                bool Same(int x, int y) => rimBody[x] >= 0 && rimBody[x] == rimBody[y] && bulge.Data.rim[x] != bulge.Data.rim[y];
                if (Same(a, b) || Same(b, c) || Same(a, c)) continue;
                tris.AddRange([a, b, c]);
                parent.Add(t);
            }
        }
        else
        {
            // Inserts: the body points after a hole point's partner, up to the next hole point's, split that rim edge.
            inserted = m - n;
            var ins = new List<int>[n];
            for (int h = 0; h < n; h++) ins[h] = [];
            for (int j = 0; j < m; j++) if (rot[j] != partner[al.Map[j]]) ins[al.Map[j]].Add(rot[j]);
            var split = new Dictionary<int, (int A, int B, int C, List<int> Added)>();
            for (int h = 0; h < n; h++)
            {
                if (ins[h].Count == 0) continue;
                int h1 = (h + 1) % n;
                // The one boundary triangle on the rim edge (h, h + 1).
                var on = new List<(int T, int K, bool Forward)>();
                for (int t = 0; t < bt.Length / 3; t++)
                    for (int k = 0; k < 3; k++)
                    {
                        int x = bulge.Data.rim[bt[t * 3 + k]], y = bulge.Data.rim[bt[t * 3 + (k + 1) % 3]];
                        if (x == h && y == h1) on.Add((t, k, true));
                        else if (x == h1 && y == h) on.Add((t, k, false));
                    }
                if (on.Count != 1 || split.ContainsKey(on[0].T))
                {
                    reason = "a bulge rim edge is not one triangle";
                    return null;
                }
                var (tt, kk, forward) = on[0];
                int a = bt[tt * 3 + kk], b = bt[tt * 3 + (kk + 1) % 3], c = bt[tt * 3 + (kk + 2) % 3];
                // The added rim vertices, from a to b, each lerped along the body's run from h's partner to h + 1's.
                var run = new List<int> { partner[h] };
                run.AddRange(ins[h]);
                run.Add(partner[h1]);
                var cum = new float[run.Count];
                for (int q = 1; q < run.Count; q++) cum[q] = cum[q - 1] + Vector3.Distance(At(parts, run[q - 1]), At(parts, run[q]));
                float total = MathF.Max(cum[^1], 1e-9f);
                var added = new List<int>();
                for (int q = 1; q < run.Count - 1; q++)
                {
                    float f = cum[q] / total;               // from h toward h + 1
                    float fa = forward ? f : 1f - f;       // from a toward b
                    positions.Add(At(parts, run[q]));
                    uv.Add(Vector2.Lerp(uv[a], uv[b], fa));
                    land.Add(land[a] is { } la && land[b] is { } lb ? [la[0] + (lb[0] - la[0]) * fa, la[1] + (lb[1] - la[1]) * fa] : land[a]);
                    rimBody.Add(run[q]);
                    source.Add(-1);
                    added.Add(positions.Count - 1);
                }
                if (!forward) added.Reverse();
                split[tt] = (a, b, c, added);
            }
            for (int t = 0; t < bt.Length / 3; t++)
            {
                if (!split.TryGetValue(t, out var s)) { tris.AddRange([bt[t * 3], bt[t * 3 + 1], bt[t * 3 + 2]]); parent.Add(t); continue; }
                // A fan through the added points, each sub-triangle wound as the one it replaces: a → b, then c.
                var chain = new List<int> { s.A };
                chain.AddRange(s.Added);
                chain.Add(s.B);
                for (int q = 0; q + 1 < chain.Count; q++) { tris.AddRange([chain[q], chain[q + 1], s.C]); parent.Add(t); }
            }
        }
        return new FittedBulge([.. positions], [.. uv], [.. land], [.. rimBody], [.. source], [.. tris], [.. parent], merged, inserted, al.Worst)
        { Hole = loop };
    }
}
