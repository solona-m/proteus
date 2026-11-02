using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;

namespace Proteus.Tests;

/// <summary>
/// Where two models' body skin cuts through each other: the line a player sees where two pieces each draw a skin and
/// one comes through the other (cross-sex-wrist-band). Pairs of triangles that lie in one plane (the same triangle drawn
/// twice) or share a corner (neighbours of one surface, touching along an edge) do not cut, and are not counted.
/// </summary>
internal static class SkinIntersections
{
    private static Vector3 At(ModelParts m, int v) => new(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]);

    private static List<(Vector3 A, Vector3 B, Vector3 C)> Triangles(ModelParts m, Func<Vector3, bool>? where)
        => m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                  .SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                             .Select(t => (At(m, p.Triangles[3 * t]), At(m, p.Triangles[3 * t + 1]),
                                                           At(m, p.Triangles[3 * t + 2]))))
                  .Where(t => where == null || where(t.Item1) || where(t.Item2) || where(t.Item3))
                  .ToList();

    /// <summary>The segments along which <paramref name="a"/>'s skin and <paramref name="b"/>'s cut each other, among
    /// triangles with a corner where <paramref name="where"/> holds (all when null).</summary>
    internal static List<(Vector3 P, Vector3 Q)> Segments(ModelParts a, ModelParts b, Func<Vector3, bool>? where = null)
    {
        var ta = Triangles(a, where);
        var tb = Triangles(b, where);
        var segments = new List<(Vector3, Vector3)>();
        foreach (var x in ta)
        {
            var xmin = Vector3.Min(x.A, Vector3.Min(x.B, x.C));
            var xmax = Vector3.Max(x.A, Vector3.Max(x.B, x.C));
            var nx = Vector3.Normalize(Vector3.Cross(x.B - x.A, x.C - x.A));
            foreach (var y in tb)
            {
                var ymin = Vector3.Min(y.A, Vector3.Min(y.B, y.C));
                var ymax = Vector3.Max(y.A, Vector3.Max(y.B, y.C));
                if (xmax.X < ymin.X || ymax.X < xmin.X || xmax.Y < ymin.Y || ymax.Y < xmin.Y
                    || xmax.Z < ymin.Z || ymax.Z < xmin.Z) continue;
                var ny = Vector3.Normalize(Vector3.Cross(y.B - y.A, y.C - y.A));
                if (MathF.Abs(Vector3.Dot(nx, ny)) > 0.9999f && MathF.Abs(Vector3.Dot(y.A - x.A, nx)) < 2e-5f) continue;
                if (SharesCorner(x, y)) continue;
                var points = new List<Vector3>();
                EdgesThrough(x, y, points);
                EdgesThrough(y, x, points);
                if (points.Count >= 2) segments.Add((points[0], points[^1]));
            }
        }
        return segments;
    }

    /// <summary>Their summed length, in metres.</summary>
    internal static float Length(IEnumerable<(Vector3 P, Vector3 Q)> segments) => segments.Sum(s => Vector3.Distance(s.P, s.Q));

    private static bool SharesCorner((Vector3 A, Vector3 B, Vector3 C) x, (Vector3 A, Vector3 B, Vector3 C) y)
    {
        foreach (var p in new[] { x.A, x.B, x.C })
            foreach (var q in new[] { y.A, y.B, y.C })
                if (Vector3.DistanceSquared(p, q) < 1e-10f) return true;
        return false;
    }

    private static void EdgesThrough((Vector3 A, Vector3 B, Vector3 C) e, (Vector3 A, Vector3 B, Vector3 C) t, List<Vector3> into)
    {
        foreach (var (p0, p1) in new[] { (e.A, e.B), (e.B, e.C), (e.C, e.A) })
            if (SegmentHits(p0, p1, t.A, t.B, t.C, out var q)) into.Add(q);
    }

    /// <summary>Möller–Trumbore, the segment p0→p1 against the triangle.</summary>
    private static bool SegmentHits(Vector3 p0, Vector3 p1, Vector3 a, Vector3 b, Vector3 c, out Vector3 hit)
    {
        hit = default;
        var d = p1 - p0;
        var e1 = b - a;
        var e2 = c - a;
        var h = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, h);
        if (MathF.Abs(det) < 1e-12f) return false;
        float f = 1f / det;
        var s = p0 - a;
        float u = f * Vector3.Dot(s, h);
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(s, e1);
        float v = f * Vector3.Dot(d, q);
        if (v < 0 || u + v > 1) return false;
        float t = f * Vector3.Dot(e2, q);
        if (t < 0 || t > 1) return false;
        hit = p0 + t * d;
        return true;
    }
}
