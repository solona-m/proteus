using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Proteus.Services.Bulge;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Path A's asset: the owner's TBSE bulge (an FBX on her desktop, one mesh "bulge 0.3") made into the embedded
/// <c>Resources/tbse_bulge.json</c> — only her sculpt and the 28 points of the hole it plugs, never The Body SE's mesh.
/// </summary>
public partial class BulgeAssetLocalTests
{
    internal const string TbseBulgeFbx = "USERPROFILE:OneDrive/Desktop/tbse bulge.fbx";
    internal const string GenA = LocalData.Mods + ":The Body SE/p2/step 6- smallclothes options/gen a/chara/equipment/e0000/model/c0101e0000_dwn.mdl";

    /// <summary>How near a point must be to a hole point to be on it (0.1 mm).</summary>
    private const float OnHole = 1e-4f;

    private static Vector3 P(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>Gen A's genital hole: the open rim of its genital piece (<c>0.3</c>), welded by position, as a loop.</summary>
    internal static List<Vector3> GenAHole(byte[] genA)
    {
        var parts = ModelPartReader.Read(genA)!;
        var penis = parts.Parts.Single(p => p.Island < 0 && p.Label == "1.4");
        (long, long, long) K(Vector3 p) => ((long)MathF.Round(p.X * 1e5f), (long)MathF.Round(p.Y * 1e5f), (long)MathF.Round(p.Z * 1e5f));
        var at = new Dictionary<(long, long, long), Vector3>();
        var use = new Dictionary<((long, long, long), (long, long, long)), int>();
        for (int t = 0; t + 2 < penis.Triangles.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                var a = P(parts, penis.Triangles[t + k]);
                var b = P(parts, penis.Triangles[t + (k + 1) % 3]);
                at[K(a)] = a;
                at[K(b)] = b;
                var e = K(a).CompareTo(K(b)) < 0 ? (K(a), K(b)) : (K(b), K(a));
                use[e] = use.GetValueOrDefault(e) + 1;
            }
        var adj = new Dictionary<(long, long, long), List<(long, long, long)>>();
        foreach (var (e, n) in use.Where(kv => kv.Value == 1))
        {
            (adj.TryGetValue(e.Item1, out var l1) ? l1 : adj[e.Item1] = []).Add(e.Item2);
            (adj.TryGetValue(e.Item2, out var l2) ? l2 : adj[e.Item2] = []).Add(e.Item1);
        }
        var start = adj.Keys.OrderBy(k => k).First();
        var loop = new List<(long, long, long)> { start };
        (long, long, long)? prev = null;
        var cur = start;
        while (true)
        {
            var next = adj[cur].Where(x => !prev.HasValue || x != prev.Value).ToList();
            if (next.Count == 0 || next[0] == start) break;
            prev = cur;
            cur = next[0];
            loop.Add(cur);
        }
        return loop.Select(k => at[k]).ToList();
    }

    /// <summary>The open rim of a triangle list, as a loop of vertex indices.</summary>
    private static List<int> RimLoop(List<int[]> tris)
    {
        var use = new Dictionary<(int, int), int>();
        foreach (var t in tris)
            for (int k = 0; k < 3; k++)
            {
                int a = t[k], b = t[(k + 1) % 3];
                var e = a < b ? (a, b) : (b, a);
                use[e] = use.GetValueOrDefault(e) + 1;
            }
        var adj = new Dictionary<int, List<int>>();
        foreach (var (e, n) in use.Where(kv => kv.Value == 1))
        {
            (adj.TryGetValue(e.Item1, out var l1) ? l1 : adj[e.Item1] = []).Add(e.Item2);
            (adj.TryGetValue(e.Item2, out var l2) ? l2 : adj[e.Item2] = []).Add(e.Item1);
        }
        if (adj.Count == 0) return [];
        int start = adj.Keys.Min(), cur = start, prev = -1;
        var loop = new List<int> { start };
        while (true)
        {
            var next = adj[cur].Where(x => x != prev).ToList();
            if (next.Count == 0 || next[0] == start) break;
            prev = cur;
            cur = next[0];
            loop.Add(cur);
        }
        return loop;
    }

    /// <summary>The sculpt with its rim snapped onto the hole: rim vertices off the hole collapsed into their nearer rim
    /// neighbour; rim edges that skip a hole point split there. Each triangle keeps its corner uvs.</summary>
    internal static (List<Vector3> Pos, List<int[]> Tris, List<Vector2[]> Uv, List<Dictionary<string, float>> W, string Log)
        Repair(SculptFbx m, IReadOnlyList<Vector3> hole)
    {
        var pos = m.Positions.ToList();
        var w = m.Weights.Select(x => new Dictionary<string, float>(x, StringComparer.Ordinal)).ToList();
        var tris = Enumerable.Range(0, m.Triangles.Length / 3).Select(t => new[] { m.Triangles[t * 3], m.Triangles[t * 3 + 1], m.Triangles[t * 3 + 2] }).ToList();
        var uv = Enumerable.Range(0, m.Triangles.Length / 3).Select(t => new[] { m.CornerUv[t * 3], m.CornerUv[t * 3 + 1], m.CornerUv[t * 3 + 2] }).ToList();
        var log = new List<string>();
        float NearHole(Vector3 p) => hole.Min(h => Vector3.Distance(h, p));

        var loop = RimLoop(tris);
        foreach (int v in loop.Where(v => NearHole(pos[v]) >= OnHole).ToList())
        {
            loop = RimLoop(tris);
            int i = loop.IndexOf(v);
            if (i < 0) continue;
            int a = loop[(i - 1 + loop.Count) % loop.Count], c = loop[(i + 1) % loop.Count];
            int into = Vector3.Distance(pos[v], pos[a]) <= Vector3.Distance(pos[v], pos[c]) ? a : c;
            log.Add($"collapsed rim vertex {v} ({NearHole(pos[v]) * 1000f:0.0} mm off the hole) into {into}");
            for (int t = tris.Count - 1; t >= 0; t--)
            {
                var tri = tris[t].Select(x => x == v ? into : x).ToArray();
                if (tri.Distinct().Count() < 3) { tris.RemoveAt(t); uv.RemoveAt(t); }
                else tris[t] = tri;
            }
        }
        foreach (var h in hole)
        {
            loop = RimLoop(tris);
            if (loop.Any(v => Vector3.Distance(pos[v], h) < OnHole)) continue;
            (float D, float T, int A, int C) best = (float.MaxValue, 0, -1, -1);
            for (int i = 0; i < loop.Count; i++)
            {
                int a = loop[i], c = loop[(i + 1) % loop.Count];
                var d = pos[c] - pos[a];
                float tt = Math.Clamp(Vector3.Dot(h - pos[a], d) / MathF.Max(d.LengthSquared(), 1e-20f), 0f, 1f);
                float dist = Vector3.Distance(h, pos[a] + d * tt);
                if (dist < best.D) best = (dist, tt, a, c);
            }
            int n = pos.Count;
            pos.Add(h);
            w.Add(w[best.A].Keys.Union(w[best.C].Keys).ToDictionary(k => k, k => w[best.A].GetValueOrDefault(k) * (1 - best.T) + w[best.C].GetValueOrDefault(k) * best.T));
            log.Add($"split rim edge {best.A}-{best.C} at a hole point {best.D * 1000f:0.0} mm off it");
            for (int t = 0; t < tris.Count; t++)
            {
                var tri = tris[t];
                if (!tri.Contains(best.A) || !tri.Contains(best.C)) continue;
                int ia = Array.IndexOf(tri, best.A), ic = Array.IndexOf(tri, best.C);
                var un = Vector2.Lerp(uv[t][ia], uv[t][ic], best.T);
                var t1 = (int[])tri.Clone(); var u1 = (Vector2[])uv[t].Clone();
                t1[ic] = n; u1[ic] = un;
                var t2 = (int[])tri.Clone(); var u2 = (Vector2[])uv[t].Clone();
                t2[ia] = n; u2[ia] = un;
                tris[t] = t1; uv[t] = u1;
                tris.Add(t2); uv.Add(u2);
                break;
            }
        }
        return (pos, tris, uv, w, string.Join("; ", log));
    }


    /// <summary>Barycentric weights of <paramref name="p"/> in a 2-D triangle; false when it lies outside (a hair's tolerance).</summary>
    private static bool Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out float wa, out float wb, out float wc)
    {
        float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        wa = wb = wc = 0f;
        if (MathF.Abs(d) < 1e-14f) return false;
        wa = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / d;
        wb = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / d;
        wc = 1f - wa - wb;
        const float tol = -1e-5f;
        return wa >= tol && wb >= tol && wc >= tol;
    }

    /// <summary>
    /// A disk mesh laid flat on the unit circle (Tutte): its points welded by position; the ones on <paramref name="hole"/>
    /// placed round the circle by arc length in the hole's order; every other point the mean of its neighbours, solved
    /// to rest. Per input vertex, its flat position.
    /// </summary>
    internal static Vector2[] Flatten(IReadOnlyList<Vector3> pos, int[] tris, IReadOnlyList<Vector3> hole)
    {
        var nodeOf = new int[pos.Count];
        var byPlace = new Dictionary<(long, long, long), int>();
        for (int i = 0; i < pos.Count; i++)
        {
            var key = ((long)MathF.Round(pos[i].X * 1e5f), (long)MathF.Round(pos[i].Y * 1e5f), (long)MathF.Round(pos[i].Z * 1e5f));
            if (!byPlace.TryGetValue(key, out int n)) byPlace[key] = n = byPlace.Count;
            nodeOf[i] = n;
        }
        int nc = byPlace.Count;
        var adj = Enumerable.Range(0, nc).Select(_ => new HashSet<int>()).ToArray();
        for (int t = 0; t + 2 < tris.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = nodeOf[tris[t + k]], b = nodeOf[tris[t + (k + 1) % 3]];
                if (a != b) { adj[a].Add(b); adj[b].Add(a); }
            }
        var flat = new Vector2[nc];
        var fixedNode = new bool[nc];
        float total = 0f;
        var cum = new float[hole.Count];
        for (int h = 1; h < hole.Count; h++) cum[h] = total += Vector3.Distance(hole[h - 1], hole[h]);
        total += Vector3.Distance(hole[^1], hole[0]);
        for (int i = 0; i < pos.Count; i++)
        {
            int h = -1;
            for (int k = 0; k < hole.Count; k++) if (Vector3.Distance(hole[k], pos[i]) < OnHole) { h = k; break; }
            if (h < 0) continue;
            float ang = cum[h] / total * MathF.Tau;
            flat[nodeOf[i]] = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
            fixedNode[nodeOf[i]] = true;
        }
        for (int it = 0; it < 50000; it++)
        {
            float most = 0f;
            for (int n = 0; n < nc; n++)
            {
                if (fixedNode[n] || adj[n].Count == 0) continue;
                var sum = Vector2.Zero;
                foreach (int m in adj[n]) sum += flat[m];
                var next = sum / adj[n].Count;
                most = MathF.Max(most, Vector2.DistanceSquared(next, flat[n]));
                flat[n] = next;
            }
            if (most < 1e-18f) break;
        }
        return Enumerable.Range(0, pos.Count).Select(i => flat[nodeOf[i]]).ToArray();
    }

    /// <summary>The embedded file's shape: see <see cref="TbseBulge"/>.</summary>
    [LocalDataFact(TbseBulgeFbx, GenA, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-featureless-bulge-10")]
    public void Generate_tbse_bulge()
    {
        var fbxPath = LocalData.Path(TbseBulgeFbx);
        var genA = File.ReadAllBytes(LocalData.Path(GenA));
        var hole = GenAHole(genA);
        var mesh = SculptFbx.Read(fbxPath);
        var (pos, tris, cornerUv, weights, log) = Repair(mesh, hole);
        output.WriteLine($"sculpt {mesh.Positions.Length} vertices, {mesh.Triangles.Length / 3} triangles; hole {hole.Count} points; repair: {log}");

        var loop = RimLoop(tris);
        int onHole = loop.Count(v => hole.Any(h => Vector3.Distance(h, pos[v]) < OnHole));
        Assert.True(loop.Count == hole.Count && onHole == hole.Count, $"after the repair the rim has {loop.Count} points, {onHole} on the {hole.Count}-point hole");

        // One vertex per (point, uv): the mdl stores a uv per vertex.
        var outPos = new List<Vector3>();
        var outUv = new List<Vector2>();
        var outW = new List<Dictionary<string, float>>();
        var outOf = new Dictionary<(int, long, long), int>();
        var outTris = new List<int>();
        for (int t = 0; t < tris.Count; t++)
            for (int k = 0; k < 3; k++)
            {
                int v = tris[t][k];
                var u = cornerUv[t][k];
                var key = (v, (long)MathF.Round(u.X * 1e6f), (long)MathF.Round(u.Y * 1e6f));
                if (!outOf.TryGetValue(key, out int o))
                {
                    outOf[key] = o = outPos.Count;
                    outPos.Add(pos[v]);
                    outUv.Add(u);
                    outW.Add(weights[v]);
                }
                outTris.Add(o);
            }
        var rim = outPos.Select(p => hole.FindIndex(h => Vector3.Distance(h, p) < OnHole)).ToArray();

        // Where each vertex should land for a garment made on a bibo body: the bibo->TBSE maps send a bibo crotch onto
        // Gen A's genital piece, so each bulge vertex takes the uv of the MATCHING point of that piece. Both are disks on
        // the same 28-point hole, so each is laid flat on a circle (Tutte: the hole by arc length, the inside by
        // uniform averaging — bijective, so fold-free) and a bulge vertex reads the piece's uv where it falls.
        var genAParts = ModelPartReader.Read(genA)!;
        var penis = genAParts.Parts.Single(q => q.Island < 0 && q.Label == "1.4");
        var genAUv = BodyRetarget.Uv(genA);
        var penisVerts = penis.Triangles.Distinct().ToList();
        var penisFlat = Flatten(penisVerts.Select(v => P(genAParts, v)).ToList(),
                                penis.Triangles.Select(v => penisVerts.IndexOf(v)).ToArray(), hole);
        var bulgeFlat = Flatten(outPos, outTris.ToArray(), hole);
        var land = new float[]?[outPos.Count];
        int unlanded = 0;
        for (int i = 0; i < outPos.Count; i++)
        {
            var d = bulgeFlat[i];
            bool found = false;
            for (int t = 0; t + 2 < penis.Triangles.Length && !found; t += 3)
            {
                int a = penisVerts.IndexOf(penis.Triangles[t]), b = penisVerts.IndexOf(penis.Triangles[t + 1]), c = penisVerts.IndexOf(penis.Triangles[t + 2]);
                if (!Barycentric(d, penisFlat[a], penisFlat[b], penisFlat[c], out float wa, out float wb, out float wc)) continue;
                int va = penis.Triangles[t], vb = penis.Triangles[t + 1], vc = penis.Triangles[t + 2];
                land[i] = [genAUv[va * 2] * wa + genAUv[vb * 2] * wb + genAUv[vc * 2] * wc,
                           genAUv[va * 2 + 1] * wa + genAUv[vb * 2 + 1] * wb + genAUv[vc * 2 + 1] * wc];
                found = true;
            }
            if (!found) unlanded++;
        }
        output.WriteLine($"asset: {outPos.Count} vertices, {outTris.Count / 3} triangles, rim {rim.Count(r => r >= 0)}; {unlanded} without a landing uv");

        var file = new TbseBulge.File(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fbxPath))).ToLowerInvariant(),
            hole.Select(h => new[] { h.X, h.Y, h.Z }).ToArray(),
            outPos.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
            outUv.Select(u => new[] { u.X, u.Y }).ToArray(),
            land,
            outW.Select(x => x.Where(kv => kv.Value > 0f).ToDictionary(kv => kv.Key, kv => kv.Value)).ToArray(),
            rim,
            outTris.ToArray());
        var json = JsonSerializer.Serialize(file);
        var path = RepoFile("Proteus/Resources/tbse_bulge.json");
        if (File.Exists(path) && File.ReadAllText(path).Trim() != "{}")
            Assert.True(File.ReadAllText(path).Trim() == json, "the committed tbse_bulge.json differs from what the sculpt gives — regenerate it on purpose");
        else
            File.WriteAllText(path, json + "\n");
    }
}
