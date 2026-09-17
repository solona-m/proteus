using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>
/// Where a UV island's edge continues on the body rather than in the texture, so a blur can read across a seam.
/// Two triangles sharing an edge in 3D but not in UV are the two sides of a seam; for each gutter texel just
/// outside an island edge this rasterises the texel on the far side that the surface continues into.
/// </summary>
public sealed class UvSeamMapService(IPluginLog log)
{
    /// <summary>
    /// Cached maps, newest last. Capped because each is a full <c>int[w*h]</c> and the reach follows a
    /// continuous slider.
    /// </summary>
    private readonly List<((string Id, int W, int H, int Reach) Key, int[]? Map)> cache = new();
    private const int MaxCachedMaps = 2;
    private readonly object cacheLock = new();

    /// <summary>One body part for the seam map: how to tell whether it changed, and how to read it if it did.</summary>
    /// <param name="Id">Identity — path plus size and write time, or the game path for an sqpack asset.</param>
    /// <param name="Load">Reads the bytes. Called ONLY on a cache miss.</param>
    public readonly record struct SeamModel(string Id, Func<byte[]?> Load);

    /// <summary>
    /// For every texel, the texel its surface continues into across a UV seam, or -1 where there is none
    /// (the texel is on-island, too far from a seam, or the far side falls outside the map). Null when the
    /// models carry no usable geometry.
    /// <para/>
    /// Takes all the body's part models together: seams such as torso-to-leg lie between two part files.
    /// Cached by identity, so a hit reads no model bytes.
    /// </summary>
    public int[]? SeamSource(IReadOnlyList<SeamModel> models, int w, int h, int reach)
    {
        if (models == null || models.Count == 0 || w <= 0 || h <= 0 || reach <= 0) return null;

        var sb = new System.Text.StringBuilder();
        foreach (var m in models) sb.Append(m.Id).Append('');
        var key = (sb.ToString(), w, h, reach);

        lock (cacheLock)
        {
            for (int i = 0; i < cache.Count; i++)
                if (cache[i].Key == key) return cache[i].Map;
        }

        var loaded = new List<byte[]?>(models.Count);
        foreach (var m in models) loaded.Add(m.Load());

        // Built outside the lock; a concurrent duplicate build is wasted work, never a wrong answer.
        // A null result is cached too, so unreadable parts are not re-read every composite.
        var map = loaded.TrueForAll(b => b == null || b.Length < 0x44)
            ? null
            : Build(loaded, w, h, reach);
        lock (cacheLock)
        {
            for (int i = 0; i < cache.Count; i++)
                if (cache[i].Key == key) return cache[i].Map;   // someone else finished first — keep theirs
            if (cache.Count >= MaxCachedMaps) cache.RemoveAt(0);
            cache.Add((key, map));
        }
        return map;
    }

    /// <summary>
    /// Shift a part's UVs by whole tiles so they lie in [0,1), uniformly across the part and per axis. An axis
    /// spanning more than one tile is left alone and false is returned.
    /// </summary>
    private static bool ShiftIntoUnitTile(float[] uv)
    {
        if (uv.Length == 0) return true;
        float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
        for (int i = 0; i < uv.Length; i += 2)
        {
            if (uv[i] < minU) minU = uv[i];
            if (uv[i] > maxU) maxU = uv[i];
            if (uv[i + 1] < minV) minV = uv[i + 1];
            if (uv[i + 1] > maxV) maxV = uv[i + 1];
        }
        bool okU = maxU - minU < 1f, okV = maxV - minV < 1f;
        float su = okU ? -MathF.Floor(minU) : 0f;
        float sv = okV ? -MathF.Floor(minV) : 0f;
        if (su != 0f || sv != 0f)
            for (int i = 0; i < uv.Length; i += 2) { uv[i] += su; uv[i + 1] += sv; }
        return okU && okV;
    }

    private int[]? Build(IReadOnlyList<byte[]?> models, int w, int h, int reach)
    {
        // Concatenate every part into one vertex/triangle space so the weld below can join them.
        var posL = new List<float>(); var uvL = new List<float>(); var triL = new List<int>();
        foreach (var mdl in models)
        {
            if (mdl == null || !SecondSkinWriter.TryReadLod0Geometry(mdl, out var p, out var u, out var t))
                continue;

            // Body parts may store UVs shifted by whole tiles and disagree with each other; normalise each to [0,1).
            if (!ShiftIntoUnitTile(u))
                log.Warning("[Proteus] seam map: a body part's UVs span more than one tile on some axis — "
                          + "it can't be normalised, so seams involving it may be missing");

            int b = posL.Count / 3;
            posL.AddRange(p); uvL.AddRange(u);
            foreach (int idx in t) triL.Add(b + idx);
        }
        if (triL.Count == 0)
        {
            log.Debug("[Proteus] seam map: no readable LOD0 geometry in {0} part model(s)", models.Count);
            return null;
        }
        var pos = posL.ToArray(); var uv = uvL.ToArray(); var tri = triL.ToArray();

        // ── 1. Weld vertices by POSITION ────────────────────────────────────
        // A seam's two sides are separate vertices; welding on quantised position recovers the 3D topology.
        // The grid absorbs half-float rounding while keeping distinct anatomy apart.
        const float Quant = 4096f;
        var weld = new Dictionary<(int, int, int), int>(pos.Length / 3);
        var posId = new int[pos.Length / 3];
        for (int v = 0; v < posId.Length; v++)
        {
            var key = ((int)MathF.Round(pos[v * 3] * Quant),
                       (int)MathF.Round(pos[v * 3 + 1] * Quant),
                       (int)MathF.Round(pos[v * 3 + 2] * Quant));
            if (!weld.TryGetValue(key, out int id)) { id = weld.Count; weld[key] = id; }
            posId[v] = id;
        }

        // ── 2. Collect every triangle edge under its 3D identity ────────────
        // Side = the UV segment plus the UV of the opposite corner, which is what tells us which way is
        // "outward" (away from the triangle) later.
        var edges = new Dictionary<(int, int), List<(int A, int B, int C)>>(tri.Length / 2);
        for (int t = 0; t + 2 < tri.Length; t += 3)
        {
            int v0 = tri[t], v1 = tri[t + 1], v2 = tri[t + 2];
            AddEdge(v0, v1, v2); AddEdge(v1, v2, v0); AddEdge(v2, v0, v1);

            void AddEdge(int a, int b, int c)
            {
                int pa = posId[a], pb = posId[b];
                if (pa == pb) return;                       // degenerate
                // Order by 3D identity so both sides of a seam agree on which end is which.
                var key = pa < pb ? (pa, pb) : (pb, pa);
                var side = pa < pb ? (a, b, c) : (b, a, c);
                if (!edges.TryGetValue(key, out var list)) edges[key] = list = new List<(int, int, int)>(2);
                if (list.Count < 8) list.Add(side);
            }
        }

        // ── 3. Rasterise the seams ──────────────────────────────────────────
        var seam = new int[w * h];
        Array.Fill(seam, -1);
        var depth = new int[w * h];
        Array.Fill(depth, int.MaxValue);
        int seamEdges = 0;

        foreach (var (_, sides) in edges)
        {
            if (sides.Count < 2) continue;                  // an open boundary has nothing on the far side
            for (int i = 0; i < sides.Count; i++)
                for (int j = i + 1; j < sides.Count; j++)
                {
                    var s1 = sides[i];
                    var s2 = sides[j];
                    // Same 3D edge AND same UV edge — an ordinary interior edge, not a seam.
                    if (SameUv(s1.A, s2.A) && SameUv(s1.B, s2.B)) continue;
                    seamEdges++;
                    Paint(s1, s2);
                    Paint(s2, s1);
                }
        }

        log.Information("[Proteus] seam map: {0} verts, {1} tris, {2} seam edges, {3} texels mapped ({4}x{5}, reach {6})",
                        pos.Length / 3, tri.Length / 3, seamEdges, CountMapped(), w, h, reach);
        return seamEdges == 0 ? null : seam;

        bool SameUv(int a, int b)
            => MathF.Abs(uv[a * 2] - uv[b * 2]) < 1e-6f && MathF.Abs(uv[a * 2 + 1] - uv[b * 2 + 1]) < 1e-6f;

        int CountMapped()
        {
            int c = 0;
            foreach (int v in seam) if (v >= 0) c++;
            return c;
        }

        // Fill the gutter just outside DST's UV edge with the texels SRC's surface continues into.
        void Paint((int A, int B, int C) dst, (int A, int B, int C) src)
        {
            // Texel space, so a step of `d` below is a texel and matches the blur's radius directly.
            float d0x = uv[dst.A * 2] * w, d0y = uv[dst.A * 2 + 1] * h;
            float d1x = uv[dst.B * 2] * w, d1y = uv[dst.B * 2 + 1] * h;
            float dcx = uv[dst.C * 2] * w, dcy = uv[dst.C * 2 + 1] * h;
            float s0x = uv[src.A * 2] * w, s0y = uv[src.A * 2 + 1] * h;
            float s1x = uv[src.B * 2] * w, s1y = uv[src.B * 2 + 1] * h;
            float scx = uv[src.C * 2] * w, scy = uv[src.C * 2 + 1] * h;

            float dLen = MathF.Sqrt((d1x - d0x) * (d1x - d0x) + (d1y - d0y) * (d1y - d0y));
            float sLen = MathF.Sqrt((s1x - s0x) * (s1x - s0x) + (s1y - s0y) * (s1y - s0y));
            if (dLen < 0.01f || sLen < 0.01f) return;
            // A UV-wrapped triangle spans the atlas; its "edge" is meaningless here.
            if (dLen > w || sLen > w) return;

            // Outward normal: perpendicular to the edge, pointing AWAY from the triangle's third corner.
            if (!Outward(d0x, d0y, d1x, d1y, dcx, dcy, out float dnx, out float dny)) return;
            if (!Outward(s0x, s0y, s1x, s1y, scx, scy, out float snx, out float sny)) return;

            // The two UV edges are one 3D edge, so their length ratio is the local texel-density ratio; clamped
            // against sliver triangles.
            float densityScale = Math.Clamp(sLen / dLen, 0.25f, 4f);

            // Sweep texels within reach of the segment, clamping the projection to [0,1] so the wedges where
            // two seam edges meet fill from the nearest endpoint.
            float ex = d1x - d0x, ey = d1y - d0y;
            float e2 = ex * ex + ey * ey;
            int x0 = (int)MathF.Floor(MathF.Min(d0x, d1x)) - reach, x1 = (int)MathF.Ceiling(MathF.Max(d0x, d1x)) + reach;
            int y0 = (int)MathF.Floor(MathF.Min(d0y, d1y)) - reach, y1 = (int)MathF.Ceiling(MathF.Max(d0y, d1y)) + reach;
            x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0); x1 = Math.Min(x1, w - 1); y1 = Math.Min(y1, h - 1);

            for (int py = y0; py <= y1; py++)
            for (int px = x0; px <= x1; px++)
            {
                float cx = px + 0.5f, cy = py + 0.5f;
                float t = ((cx - d0x) * ex + (cy - d0y) * ey) / e2;
                t = t < 0f ? 0f : t > 1f ? 1f : t;
                float vx = cx - (d0x + ex * t), vy = cy - (d0y + ey * t);
                float dist = MathF.Sqrt(vx * vx + vy * vy);
                if (dist < 0.5f || dist > reach) continue;
                if (vx * dnx + vy * dny <= 0f) continue;    // the inward side is the island itself

                int k = (int)MathF.Ceiling(dist);
                int p = py * w + px;
                if (k >= depth[p]) continue;                // a nearer seam already owns this texel

                // The mirrored distance inward from the source edge, at the source island's texel density.
                float sd = dist * densityScale;
                int qx = (int)MathF.Floor(s0x + (s1x - s0x) * t - snx * sd);
                int qy = (int)MathF.Floor(s0y + (s1y - s0y) * t - sny * sd);
                if ((uint)qx >= (uint)w || (uint)qy >= (uint)h) continue;
                depth[p] = k;
                seam[p] = qy * w + qx;
            }
        }

        static bool Outward(float ax, float ay, float bx, float by, float cx, float cy, out float nx, out float ny)
        {
            nx = ny = 0f;
            float ex = bx - ax, ey = by - ay;
            float len = MathF.Sqrt(ex * ex + ey * ey);
            if (len < 1e-6f) return false;              // degenerate edge — the callers' guard, made real
            nx = -ey / len; ny = ex / len;
            // Flip so the normal points away from the opposite corner.
            float mx = (ax + bx) * 0.5f, my = (ay + by) * 0.5f;
            if ((cx - mx) * nx + (cy - my) * ny > 0) { nx = -nx; ny = -ny; }
            return true;
        }
    }
}
