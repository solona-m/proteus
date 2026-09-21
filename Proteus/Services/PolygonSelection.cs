using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// Polygons of a garment, for moving something smaller than a whole part: which ones there are, which touch which,
/// and which one is under a ray.
/// <para/>
/// A polygon is named by its three corners, sorted — <see cref="Key"/> — rather than by its place in a triangle list.
/// The Studio's model view and the live character both hand back a hit triangle as corners in the part reader's vertex
/// numbering, but not in the same triangle order, so the corners are the one name both agree on.
/// <para/>
/// "Touching" is through WELDED corners: two polygons are neighbours when they share a corner position, which is how a
/// model's surface stays joined across the vertex splits every uv seam makes. Two pieces that merely sit close together
/// share no corner and are not neighbours — the reason a selection grows along the garment and not across a gap.
/// Skin is never offered: nothing in the Move tools may move it.
/// </summary>
internal sealed class PolygonSelection
{
    /// <summary>One polygon, by its corners in ascending order.</summary>
    internal readonly record struct Key(int A, int B, int C)
    {
        public static Key Of(int a, int b, int c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            return new Key(a, b, c);
        }

        public IEnumerable<int> Corners { get { yield return A; yield return B; yield return C; } }
    }

    private readonly Key[] polygons;
    private readonly int[] nodeOf;
    private readonly Dictionary<int, List<int>> byNode = [];   // welded corner -> polygons touching it
    private readonly Dictionary<Key, int> index = [];

    /// <param name="model">The garment. Whole submeshes only — islands repeat their submesh's triangles — and never
    /// skin.</param>
    /// <param name="movable">Optional further filter on parts, for the locked ones.</param>
    public PolygonSelection(ModelParts model, Func<ModelPart, bool>? movable = null)
    {
        int vc = model.Positions.Length / 3;
        var at = new SecondSkinWriter.Vec3[vc];
        for (int i = 0; i < vc; i++)
            at[i] = new SecondSkinWriter.Vec3(model.Positions[i * 3], model.Positions[i * 3 + 1], model.Positions[i * 3 + 2]);
        nodeOf = MeshMath.WeldByPosition(at, out _);

        var list = new List<Key>();
        foreach (var part in model.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            if (movable != null && !movable(part)) continue;
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vc || b >= vc || c >= vc) continue;
                var key = Key.Of(a, b, c);
                if (index.ContainsKey(key)) continue;
                index[key] = list.Count;
                list.Add(key);
            }
        }
        polygons = [.. list];

        for (int p = 0; p < polygons.Length; p++)
            foreach (int v in polygons[p].Corners)
            {
                int n = nodeOf[v];
                if (!byNode.TryGetValue(n, out var touching)) byNode[n] = touching = [];
                touching.Add(p);
            }
    }

    /// <summary>How many polygons may be selected.</summary>
    public int Count => polygons.Length;

    /// <summary>Whether <paramref name="key"/> is a polygon that may be selected — cloth, not skin, not locked.</summary>
    public bool Contains(Key key) => index.ContainsKey(key);

    /// <summary>Every corner of the selected polygons, for the move to seed from.</summary>
    public static IEnumerable<int> CornersOf(IEnumerable<Key> selected) => selected.SelectMany(k => k.Corners).Distinct();

    /// <summary>
    /// The selection and every polygon touching it: one ring wider, along the surface.
    /// </summary>
    public HashSet<Key> Grow(IReadOnlySet<Key> selected)
    {
        var grown = new HashSet<Key>(selected.Where(Contains));
        foreach (var key in selected)
            foreach (int v in key.Corners)
                if (v < nodeOf.Length && byNode.TryGetValue(nodeOf[v], out var touching))
                    foreach (int p in touching) grown.Add(polygons[p]);
        return grown;
    }

    /// <summary>
    /// The selection without its outer ring: every selected polygon that touches an unselected one goes.
    /// </summary>
    public HashSet<Key> Shrink(IReadOnlySet<Key> selected)
    {
        var kept = new HashSet<Key>();
        foreach (var key in selected)
        {
            if (!Contains(key)) continue;
            bool edge = false;
            foreach (int v in key.Corners)
            {
                if (v >= nodeOf.Length || !byNode.TryGetValue(nodeOf[v], out var touching)) continue;
                if (touching.Any(p => !selected.Contains(polygons[p]))) { edge = true; break; }
            }
            if (!edge) kept.Add(key);
        }
        return kept;
    }

    /// <summary>
    /// The nearest selectable polygon a ray passes through, against <paramref name="positions"/> (the surface as it now
    /// stands, three floats per vertex); null when it misses everything.
    /// </summary>
    public Key? Pick(Vector3 origin, Vector3 direction, float[] positions)
    {
        Key? best = null;
        float bestT = float.MaxValue;
        foreach (var key in polygons)
        {
            if (key.C * 3 + 2 >= positions.Length) continue;
            if (RayHits(origin, direction, At(positions, key.A), At(positions, key.B), At(positions, key.C), out float t)
                && t < bestT)
            {
                bestT = t;
                best = key;
            }
        }
        return best;
    }

    internal static Vector3 At(float[] p, int v) => new(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);

    /// <summary>Möller–Trumbore, both faces: a garment's inside is as selectable as its outside.</summary>
    private static bool RayHits(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        float inv = 1f / det;
        var s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > 0f;
    }
}
