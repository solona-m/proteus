using System;
using System.Collections.Generic;

namespace Proteus.Services;

using static Proteus.Services.SecondSkinWriter;

using static Proteus.Services.MeshMath;

internal static partial class BodyBridge
{
    /// <summary>
    /// Pulls every LOD0 vertex of the genital mesh (<see cref="IsGenitalMaterial"/>) that stands in front of the fold's
    /// smoothed, garment-covered skin — or less than
    /// <see cref="PullMargin"/> behind it — back to that margin behind, along the skin's normal, capped at
    /// <see cref="PullMax"/>. The pull is then spread over each mesh's own edges so the mesh does not crease.
    /// </summary>
    private static int PullBehindSkin(byte[] outBytes, Source src, List<string> matNames,
                                      List<(Vec3 A, Vec3 B, Vec3 C, Vec3 N)> skin, out float deepest)
    {
        deepest = 0f;
        var s = outBytes;
        const float cell = 0.008f;
        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Key(Vec3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
        for (int t = 0; t < skin.Count; t++)
        {
            var c = new Vec3((skin[t].A.X + skin[t].B.X + skin[t].C.X) / 3f, (skin[t].A.Y + skin[t].B.Y + skin[t].C.Y) / 3f,
                             (skin[t].A.Z + skin[t].B.Z + skin[t].C.Z) / 3f);
            var k = Key(c);
            if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>();
            list.Add(t);
        }

        Span<float> tmp = stackalloc float[4];
        int pulledTotal = 0;
        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > src.S.Length) break;
            ushort vc = BitConverter.ToUInt16(src.S, mo);
            if (vc == 0) continue;
            ushort matIdx = BitConverter.ToUInt16(src.S, mo + 8);
            // The genital mesh only. Everything else a body carries at the crotch — smallclothes it draws in front of
            // its skin, piercings — is meant to stand proud of the skin, and pushed behind it would vanish.
            if (matIdx >= matNames.Count || !IsGenitalMaterial(matNames[matIdx])) continue;
            var decl = m < src.Decls.Length ? src.Decls[m] : [];
            VElem? pe = null;
            foreach (var el in decl) if (el.Usage == UsePosition) { pe = el; break; }
            if (pe is not { } pos || pos.Stream > 2) continue;
            uint vbo = BitConverter.ToUInt32(src.S, mo + 20 + pos.Stream * 4);
            byte stride = src.S[mo + 32 + pos.Stream];
            if (stride == 0) continue;

            var at = new Vec3[vc];
            var pull = new Vec3[vc];
            bool any = false;
            for (int i = 0; i < vc; i++)
            {
                int o = src.Vb + (int)vbo + i * stride + pos.Offset;
                if (o < 0 || o + 12 > s.Length) break;
                ReadTyped(s, o, pos.Type, tmp);
                var p = at[i] = new Vec3(tmp[0], tmp[1], tmp[2]);
                // Against the skin AVERAGED over everything within PullSurround, not the single nearest triangle. The
                // mesh plugs into a socket where the skin has no triangles at all — the nearest one is the rim, facing
                // into the slit, and pulling along it moved the socket's contents sideways. Averaged, the surface spans
                // the slit the way the garment's own closing patch does.
                var (cx, cy, cz) = Key(p);
                float best = float.MaxValue, wSum = 0f, aheadSum = 0f, nx = 0f, ny = 0f, nz = 0f;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                    foreach (int t in list)
                    {
                        var c = ClosestOnTriangle(p, skin[t].A, skin[t].B, skin[t].C);
                        float d = Dist(p, c);
                        best = MathF.Min(best, d);
                        if (d > PullSurround) continue;
                        float w = 1f / (d * d + 1e-6f);
                        var tn = skin[t].N;
                        wSum += w;
                        nx += tn.X * w; ny += tn.Y * w; nz += tn.Z * w;
                        aheadSum += ((p.X - c.X) * tn.X + (p.Y - c.Y) * tn.Y + (p.Z - c.Z) * tn.Z) * w;
                    }
                }
                if (best > PullReach || wSum <= 0f) continue;
                var n = Normalize(new Vec3(nx, ny, nz)) ?? default;
                if (n.X == 0f && n.Y == 0f && n.Z == 0f) continue;
                float ahead = aheadSum / wSum;
                if (ahead <= -PullMargin) continue;
                float by = MathF.Min(PullMax, ahead + PullMargin);
                pull[i] = new Vec3(-n.X * by, -n.Y * by, -n.Z * by);
                deepest = MathF.Max(deepest, by);
                any = true;
            }
            if (!any) continue;

            // Spread over the mesh's own edges, never shrinking a vertex's own pull, so the pulled patch eases into
            // the rest of the mesh instead of stepping away from it.
            ushort subIdx = BitConverter.ToUInt16(src.S, mo + 10), subCount = BitConverter.ToUInt16(src.S, mo + 12);
            var tris = MeshTriangles(src, subIdx, subCount);
            var nb = new List<int>[vc];
            for (int t = 0; t + 2 < tris.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = tris[t + k], b = tris[t + (k + 1) % 3];
                    if (a >= vc || b >= vc) continue;
                    (nb[a] ??= new List<int>()).Add(b);
                    (nb[b] ??= new List<int>()).Add(a);
                }
            for (int pass = 0; pass < PullSpreadPasses; pass++)
            {
                var nextPull = (Vec3[])pull.Clone();
                for (int i = 0; i < vc; i++)
                {
                    if (nb[i] is not { Count: > 0 } near) continue;
                    float sx = 0, sy = 0, sz = 0;
                    foreach (int k in near) { sx += pull[k].X; sy += pull[k].Y; sz += pull[k].Z; }
                    var avg = new Vec3(sx / near.Count * 0.5f, sy / near.Count * 0.5f, sz / near.Count * 0.5f);
                    if (Len(avg) > Len(pull[i])) nextPull[i] = avg;
                }
                pull = nextPull;
            }
            for (int i = 0; i < vc; i++)
            {
                if (Len(pull[i]) <= BustBridgeEpsilon) continue;
                WriteXYZ(outBytes, src.Vb + (int)vbo + i * stride + pos.Offset, pos.Type,
                         at[i].X + pull[i].X, at[i].Y + pull[i].Y, at[i].Z + pull[i].Z);
                pulledTotal++;
            }
        }
        return pulledTotal;
    }

    /// <summary>
    /// Whether a body material is the genital mesh plugged into the skin's socket at the crotch — Rue+'s "_betterpube",
    /// Bibo+'s "_bibopube" and the like — rather than skin, smallclothes, nails or piercings. Never skin.
    /// </summary>
    private static bool IsGenitalMaterial(string material)
    {
        if (SkinMaterialBodyType(material) != null) return false;
        foreach (var token in GenitalMaterialTokens)
            if (material.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly string[] GenitalMaterialTokens = ["pube", "genital", "vagina", "vulva", "penis"];

    /// <summary>How far behind the smoothed skin a pulled vertex ends — clear of the garment's push and a little more.</summary>
    private const float PullMargin = 0.0015f;

    /// <summary>The most any vertex is pulled back.</summary>
    private const float PullMax = 0.008f;

    /// <summary>Radius of skin averaged into the surface a vertex is pulled behind — wider than a socket's slit.</summary>
    private const float PullSurround = 0.006f;

    /// <summary>Passes spreading the pull over each mesh's own edges.</summary>
    private const int PullSpreadPasses = 3;

    /// <summary>
    /// Moves every LOD0 vertex of the model's non-skin meshes by the skin's movement around it — see the call in
    /// <see cref="SmoothBodyNipples"/>. Returns how many vertices moved.
    /// </summary>
    private static int CarrySkinMove(byte[] outBytes, Source src, List<string> matNames, List<Vec3> skinAt,
                                     List<Vec3> skinMove)
    {
        if (skinAt.Count == 0) return 0;
        var s = src.S;
        float cell = CarryReach;
        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Key(Vec3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
        bool anyMove = false;
        for (int i = 0; i < skinAt.Count; i++)
        {
            if (skinMove[i].X != 0f || skinMove[i].Y != 0f || skinMove[i].Z != 0f) anyMove = true;
            var k = Key(skinAt[i]);
            if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>();
            list.Add(i);
        }
        if (!anyMove) return 0;

        Span<float> tmp = stackalloc float[4];
        int carried = 0;
        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > s.Length) break;
            ushort vc = BitConverter.ToUInt16(s, mo);
            if (vc == 0) continue;
            ushort matIdx = BitConverter.ToUInt16(s, mo + 8);
            if (matIdx >= matNames.Count || SkinMaterialBodyType(matNames[matIdx]) != null) continue;

            var decl = m < src.Decls.Length ? src.Decls[m] : [];
            VElem? pe = null;
            foreach (var el in decl) if (el.Usage == UsePosition) { pe = el; break; }
            if (pe is not { } pos || pos.Stream > 2) continue;
            uint vbo = BitConverter.ToUInt32(s, mo + 20 + pos.Stream * 4);
            byte stride = s[mo + 32 + pos.Stream];
            if (stride == 0) continue;

            for (int i = 0; i < vc; i++)
            {
                int at = src.Vb + (int)vbo + i * stride + pos.Offset;
                if (at < 0 || at + 12 > s.Length) break;
                ReadTyped(s, at, pos.Type, tmp);
                var p = new Vec3(tmp[0], tmp[1], tmp[2]);
                var (cx, cy, cz) = Key(p);
                float wSum = 0f, nearest = float.MaxValue, mx = 0f, my = 0f, mz = 0f;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                    foreach (int j in list)
                    {
                        float d = Dist(skinAt[j], p);
                        if (d > CarryReach) continue;
                        nearest = MathF.Min(nearest, d);
                        float w = 1f / (d * d + 1e-8f);
                        wSum += w;
                        mx += skinMove[j].X * w; my += skinMove[j].Y * w; mz += skinMove[j].Z * w;
                    }
                }
                if (wSum <= 0f) continue;
                float fade = 1f - Smoothstep(Math.Clamp((nearest - CarryFull) / (CarryReach - CarryFull), 0f, 1f));
                float k = fade / wSum;
                var move = new Vec3(mx * k, my * k, mz * k);
                if (Len(move) <= BustBridgeEpsilon) continue;
                WriteXYZ(outBytes, at, pos.Type, p.X + move.X, p.Y + move.Y, p.Z + move.Z);
                carried++;
            }
        }
        return carried;
    }

    /// <summary>How far from the skin a non-skin vertex may sit and still move with it, fading to nothing there.</summary>
    private const float CarryReach = 0.015f;

    /// <summary>Within this of the skin a non-skin vertex takes the skin's whole movement — the genital mesh on Rue+
    /// sits up to about 4mm off the skin vertices around it and is still resting on the body.</summary>
    private const float CarryFull = 0.006f;
}
