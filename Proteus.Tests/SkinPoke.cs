using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;

namespace Proteus.Tests;

/// <summary>
/// Skin an edit pushed further through the cloth over it than its author had it: what a sculpt with "Move skin too"
/// must not make worse. Judged along the SKIN's way out (its authored normal), so a lining — whose own normal faces the
/// body — is read the same as the shell over it; <see cref="SkinThroughCloth"/> reads by the face's own normal and so
/// counts every skin point under a lining as through it, sculpted or not.
/// </summary>
internal static class SkinPoke
{
    private const float Reach = SkinThroughCloth.Reach, Tolerance = SkinThroughCloth.Tolerance, OnEdge = 0.02f;

    /// <summary>Further outside a face than this (2 mm) as authored, the skin covers it — the guard's own line.</summary>
    private const float DeepClip = 0.002f;

    /// <summary>How many skin vertices stand more than <see cref="Tolerance"/> further outside the cloth nearest them in
    /// <paramref name="after"/> than in the author's model.</summary>
    public static int MadeWorse(ModelParts model, float[] after, List<(int Vertex, float Was, float Now)>? which = null)
    {
        var whole = model.Parts.Where(p => p.Island < 0).ToList();
        var tris = whole.Where(p => !SecondSkinWriter.IsBodySkinMaterial(p.Material))
                        .SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                                   .Select(t => (p.Triangles[t * 3], p.Triangles[t * 3 + 1], p.Triangles[t * 3 + 2])))
                        .Where(t => t.Item1 != t.Item2 && t.Item2 != t.Item3 && t.Item1 != t.Item3)
                        .ToList();
        var skin = whole.Where(p => SecondSkinWriter.IsBodySkinMaterial(p.Material)).SelectMany(p => p.Triangles).Distinct().ToList();
        if (tris.Count == 0 || skin.Count == 0) return 0;

        // Hems by authored position, as SkinThroughCloth finds them.
        var key = new Dictionary<(long, long, long), int>();
        int Weld(int v)
        {
            var p = At(model.Positions, v);
            var k = ((long)MathF.Round(p.X * 1e5f), (long)MathF.Round(p.Y * 1e5f), (long)MathF.Round(p.Z * 1e5f));
            if (!key.TryGetValue(k, out int id)) key[k] = id = key.Count;
            return id;
        }
        var uses = new Dictionary<(int, int), int>();
        foreach (var (a, b, c) in tris)
            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
            {
                int wx = Weld(x), wy = Weld(y);
                var e = wx < wy ? (wx, wy) : (wy, wx);
                uses[e] = uses.GetValueOrDefault(e) + 1;
            }
        bool Hem(int x, int y)
        {
            int wx = Weld(x), wy = Weld(y);
            return uses.GetValueOrDefault(wx < wy ? (wx, wy) : (wy, wx)) < 2;
        }

        int worse = 0;
        foreach (int s in skin)
        {
            var n = At(model.Normals, s);
            if (n.LengthSquared() < 1e-12f) continue;
            n = Vector3.Normalize(n);
            float now = Outside(after, tris, At(after, s), n, Hem, out int face);
            if (float.IsNaN(now) || now <= 2f * Tolerance) continue;
            // As the author had it, against the SAME face: skin already outside it covers cloth left inside the body,
            // and moving that skin further out shows nothing more.
            float was = Against(model.Positions, tris[face], At(model.Positions, s), n);
            // Cloth left inside the body (skin more than DeepClip outside it as authored) is covered by that skin; a
            // slighter clip is the author's to keep, and only its growth counts.
            if (was > DeepClip || now - MathF.Max(was, 0f) <= Tolerance) continue;
            worse++;
            which?.Add((s, was, now));
        }
        return worse;
    }

    /// <summary>
    /// A second reading that does not share <see cref="MadeWorse"/>'s rule (the nearest face, deep clips skipped): skin
    /// vertices that ANY cloth face covered as authored — the skin at or under it, within reach — and now stands more
    /// than <paramref name="by"/> outside of.
    /// </summary>
    public static int Uncovered(ModelParts model, float[] after, float by)
    {
        var whole = model.Parts.Where(p => p.Island < 0).ToList();
        var tris = whole.Where(p => !SecondSkinWriter.IsBodySkinMaterial(p.Material))
                        .SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                                   .Select(t => (p.Triangles[t * 3], p.Triangles[t * 3 + 1], p.Triangles[t * 3 + 2])))
                        .Where(t => t.Item1 != t.Item2 && t.Item2 != t.Item3 && t.Item1 != t.Item3)
                        .ToList();
        var skin = whole.Where(p => SecondSkinWriter.IsBodySkinMaterial(p.Material)).SelectMany(p => p.Triangles).Distinct().ToList();
        int count = 0;
        foreach (int s in skin)
        {
            var n = At(model.Normals, s);
            if (n.LengthSquared() < 1e-12f) continue;
            n = Vector3.Normalize(n);
            var was = At(model.Positions, s);
            foreach (var t in tris)
            {
                var pa = At(model.Positions, t.Item1);
                if (MathF.Abs(pa.X - was.X) > 0.05f || MathF.Abs(pa.Y - was.Y) > 0.05f || MathF.Abs(pa.Z - was.Z) > 0.05f) continue;
                var q = BrushTransfer.ClosestOnTriangle(was, pa, At(model.Positions, t.Item2), At(model.Positions, t.Item3),
                                                        out float u, out float v, out float w);
                // Covered: under the face proper, not beside its edge.
                if (Vector3.Distance(was, q) > Reach || u < OnEdge || v < OnEdge || w < OnEdge) continue;
                if (Against(model.Positions, t, was, n) is not (<= 0f)) continue;
                // Covering faces lie across the skin's way out; one turned more than 60° from it — a band's wall, a seam
                // standing up — flips which side is "outside" with the slightest turn, and says nothing about cover.
                if (MathF.Abs(Vector3.Dot(FaceNormal(model.Positions, t), n)) < 0.5f
                    || MathF.Abs(Vector3.Dot(FaceNormal(after, t), n)) < 0.5f) continue;
                if (Against(after, t, At(after, s), n) > by) { count++; break; }
            }
        }
        return count;
    }

    /// <summary>How far <paramref name="s"/> stands outside the cloth face nearest it, along <paramref name="skinOut"/>;
    /// NaN when no face is within reach or the nearest point is past a hem.</summary>
    private static float Outside(float[] pos, List<(int, int, int)> tris, Vector3 s, Vector3 skinOut, Func<int, int, bool> hem,
                                 out int face)
    {
        float best = Reach;
        int bt = -1;
        face = -1;
        Vector3 bq = default;
        float bu = 0, bv = 0, bw = 0;
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            var pa = At(pos, a);
            if (MathF.Abs(pa.X - s.X) > 0.1f || MathF.Abs(pa.Y - s.Y) > 0.1f || MathF.Abs(pa.Z - s.Z) > 0.1f) continue;
            var q = BrushTransfer.ClosestOnTriangle(s, pa, At(pos, b), At(pos, c), out float u, out float v, out float w);
            float d = Vector3.Distance(s, q);
            if (d >= best) continue;
            best = d; bt = i; bq = q; bu = u; bv = v; bw = w;
        }
        if (bt < 0) return float.NaN;
        face = bt;
        var (ta, tb, tc) = tris[bt];
        if ((bu < OnEdge && hem(tb, tc)) || (bv < OnEdge && hem(tc, ta)) || (bw < OnEdge && hem(ta, tb))) return float.NaN;
        return Against(pos, tris[bt], s, skinOut);
    }

    /// <summary>How far <paramref name="s"/> stands outside one face, along <paramref name="skinOut"/>.</summary>
    private static float Against(float[] pos, (int A, int B, int C) t, Vector3 s, Vector3 skinOut)
    {
        var q = BrushTransfer.ClosestOnTriangle(s, At(pos, t.A), At(pos, t.B), At(pos, t.C), out _, out _, out _);
        var f = Vector3.Cross(At(pos, t.B) - At(pos, t.A), At(pos, t.C) - At(pos, t.A));
        if (f.LengthSquared() < 1e-14f) return float.NaN;
        f = Vector3.Normalize(f);
        if (Vector3.Dot(f, skinOut) < 0f) f = -f;
        return Vector3.Dot(s - q, f);
    }

    private static Vector3 FaceNormal(float[] pos, (int A, int B, int C) t)
    {
        var f = Vector3.Cross(At(pos, t.B) - At(pos, t.A), At(pos, t.C) - At(pos, t.A));
        return f.LengthSquared() < 1e-14f ? Vector3.Zero : Vector3.Normalize(f);
    }

    private static Vector3 At(float[] p, int v) => new(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);
}
