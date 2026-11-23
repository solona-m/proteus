using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;

namespace Proteus.Tests;

/// <summary>
/// How far a model's own body skin stands in front of its cloth around the nipples — skin showing through the cloth in
/// game. Read on a written model (its cloth and the skin it draws), never on an export, and through the cloth's FACES:
/// the skin pokes between cloth vertices, where a vertex measure (Proteus.Refit --measure) sees nothing.
/// </summary>
internal static class NippleClipMeasure
{
    /// <summary>How far from each nipple tip the skin is read (35 mm).</summary>
    public const float Reach = 0.035f;

    /// <summary>How far along the skin's normal, either way, a cloth face is looked for (40 mm).</summary>
    public const float RayReach = 0.04f;

    /// <summary>A measured model: its cloth parts and how deep the skin is through each, per side (metres, positive = skin
    /// in front of the cloth), and the nipple tips it was read around.</summary>
    public sealed record Result(Vector3 TipLeft, Vector3 TipRight, IReadOnlyDictionary<string, (float Left, float Right)> Deepest,
                                int SkinVerticesRead)
    {
        /// <summary>The deeper of the two sides over the given parts.</summary>
        public float Worst(IEnumerable<string> parts) => parts.Max(p => MathF.Max(Deepest[p].Left, Deepest[p].Right));
    }

    private static Vector3 P(ModelParts m, int i) => new(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]);

    /// <summary>The cloth parts of a model: whole parts, not body skin, not what a body mod draws on it.</summary>
    public static IEnumerable<ModelPart> Cloth(ModelParts m)
        => m.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)
                              && !System.Text.RegularExpressions.Regex.IsMatch(p.Material, @"(^|/)mt_c\d{4}b\d{4}_"));

    /// <summary>The nipple tips: the model's body skin's most forward point either side, at chest height.</summary>
    public static (Vector3 Left, Vector3 Right, HashSet<int> Skin) Tips(ModelParts m)
    {
        var skin = m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                          .SelectMany(p => p.Triangles).ToHashSet();
        Vector3 left = default, right = default;
        foreach (int v in skin)
        {
            var p = P(m, v);
            if (p.Y < 1.12f || p.Y > 1.32f) continue;
            if (p.X > 0 && p.Z > left.Z) left = p;
            if (p.X < 0 && p.Z > right.Z) right = p;
        }
        return (left, right, skin);
    }

    /// <summary>
    /// Per cloth part and side, the deepest any skin vertex within <see cref="Reach"/> of a tip stands in front of that
    /// part's nearest face along the skin's own normal (cast both ways, <see cref="RayReach"/>).
    /// </summary>
    public static Result Measure(ModelParts m)
    {
        var (left, right, skin) = Tips(m);
        if (left == default || right == default) throw new InvalidOperationException("the model draws no chest skin to read");
        var probes = skin.Where(v => Vector3.Distance(P(m, v), left) < Reach || Vector3.Distance(P(m, v), right) < Reach).ToList();
        var deepest = new Dictionary<string, (float Left, float Right)>();
        foreach (var part in Cloth(m))
        {
            float l = 0f, r = 0f;
            foreach (int v in probes)
            {
                var o = P(m, v);
                var n = new Vector3(m.Normals[v * 3], m.Normals[v * 3 + 1], m.Normals[v * 3 + 2]);
                if (n.LengthSquared() < 1e-12f) continue;
                float t = NearestHit(m, part.Triangles, o, Vector3.Normalize(n), -RayReach, RayReach);
                if (float.IsNaN(t) || t >= 0f) continue;
                if (o.X > 0) l = MathF.Max(l, -t);
                else r = MathF.Max(r, -t);
            }
            deepest[part.Label] = (l, r);
        }
        return new Result(left, right, deepest, probes.Count);
    }

    /// <summary>The ray's nearest hit on the triangles, by |t| within [from, to], or NaN.</summary>
    public static float NearestHit(ModelParts m, int[] triangles, Vector3 o, Vector3 d, float from, float to)
        => NearestHit(m, triangles, o, d, from, to, out _, out _, out _);

    public static float NearestHit(ModelParts m, int[] triangles, Vector3 o, Vector3 d, float from, float to,
                                   out int triangle, out float u, out float w)
    {
        float best = float.NaN;
        triangle = -1;
        u = w = 0f;
        for (int k = 0; k + 2 < triangles.Length; k += 3)
        {
            var a = P(m, triangles[k]);
            var e1 = P(m, triangles[k + 1]) - a;
            var e2 = P(m, triangles[k + 2]) - a;
            var h = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, h);
            if (MathF.Abs(det) < 1e-12f) continue;
            float f = 1f / det;
            var s = o - a;
            float bu = f * Vector3.Dot(s, h);
            if (bu < 0f || bu > 1f) continue;
            var q = Vector3.Cross(s, e1);
            float bw = f * Vector3.Dot(d, q);
            if (bw < 0f || bu + bw > 1f) continue;
            float t = f * Vector3.Dot(e2, q);
            if (t < from || t > to) continue;
            if (float.IsNaN(best) || MathF.Abs(t) < MathF.Abs(best)) { best = t; triangle = k; u = bu; w = bw; }
        }
        return best;
    }

    /// <summary>
    /// Cloth triangles the refit turned over: per cloth part the two models share, triangles whose face normal points
    /// against the authored one. The refit moves positions only, so the authored model's indices name the same vertices.
    /// </summary>
    public static int TurnedOver(ModelParts authored, ModelParts refit) => TurnedOverByPart(authored, refit).Values.Sum();

    /// <summary>
    /// Where each authored cloth vertex is in the refit's model: the refit moves positions only, so a cloth part keeps its
    /// label and its triangles, though the meshes around it (the swapped skin) can shift every index. -1 where a part
    /// did not come through unchanged in shape.
    /// </summary>
    public static int[] VertexMap(ModelParts authored, ModelParts refit)
    {
        var map = new int[authored.Positions.Length / 3];
        Array.Fill(map, -1);
        var theirs = Cloth(refit).ToDictionary(p => p.Label);
        foreach (var part in Cloth(authored))
        {
            if (!theirs.TryGetValue(part.Label, out var other) || other.Triangles.Length != part.Triangles.Length) continue;
            for (int k = 0; k < part.Triangles.Length; k++) map[part.Triangles[k]] = other.Triangles[k];
        }
        return map;
    }

    /// <summary><see cref="TurnedOver"/> per cloth part (the parts the refit carried through).</summary>
    public static Dictionary<string, int> TurnedOverByPart(ModelParts authored, ModelParts refit)
    {
        var byPart = new Dictionary<string, int>();
        var theirs = Cloth(refit).ToDictionary(p => p.Label);
        foreach (var part in Cloth(authored))
        {
            if (!theirs.TryGetValue(part.Label, out var other) || other.Triangles.Length != part.Triangles.Length) continue;
            int turned = 0;
            var t = part.Triangles;
            var u = other.Triangles;
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                var n0 = Vector3.Cross(P(authored, t[k + 1]) - P(authored, t[k]), P(authored, t[k + 2]) - P(authored, t[k]));
                var n1 = Vector3.Cross(P(refit, u[k + 1]) - P(refit, u[k]), P(refit, u[k + 2]) - P(refit, u[k]));
                if (n0.LengthSquared() < 1e-16f) continue;
                if (n1.LengthSquared() < 1e-16f || Vector3.Dot(n0, n1) <= 0f) turned++;
            }
            byPart[part.Label] = turned;
        }
        return byPart;
    }

    /// <summary>A cloth vertex the author covered with a fur card: the card's triangle and where on it.</summary>
    public readonly record struct CardPair(int Vertex, int Triangle, float U, float W);

    /// <summary>
    /// Cloth vertices of <paramref name="clothParts"/> within <see cref="Reach"/> of the nipple tips that the author
    /// covered with a face of <paramref name="cardPart"/>: along the vertex's own normal, 0.2-10 mm in front.
    /// </summary>
    public static List<CardPair> CardPairs(ModelParts authored, IEnumerable<string> clothParts, string cardPart)
    {
        var (left, right, _) = Tips(authored);
        var cards = authored.Parts.Single(p => p.Island < 0 && p.Label == cardPart).Triangles;
        var pairs = new List<CardPair>();
        foreach (var part in Cloth(authored).Where(p => clothParts.Contains(p.Label)))
            foreach (int v in part.Triangles.Distinct())
            {
                var o = P(authored, v);
                if (Vector3.Distance(o, left) >= Reach && Vector3.Distance(o, right) >= Reach) continue;
                var n = new Vector3(authored.Normals[v * 3], authored.Normals[v * 3 + 1], authored.Normals[v * 3 + 2]);
                if (n.LengthSquared() < 1e-12f) continue;
                float t = NearestHit(authored, cards, o, Vector3.Normalize(n), 0.0002f, 0.01f, out int tri, out float u, out float w);
                if (!float.IsNaN(t)) pairs.Add(new CardPair(v, tri, u, w));
            }
        return pairs;
    }

    /// <summary>
    /// How far behind the cloth vertex each pair's card point now lies (metres; positive = the card behind the cloth it
    /// covered), along the cloth's normal as the refit left it — computed from its faces, the refit writing no normals.
    /// </summary>
    public static float[] CardsBehind(ModelParts authored, ModelParts refit, IReadOnlyList<CardPair> pairs, string cardPart)
    {
        var map = VertexMap(authored, refit);
        var cards = refit.Parts.Single(p => p.Island < 0 && p.Label == cardPart).Triangles;
        var normals = new Vector3[refit.Positions.Length / 3];
        foreach (var part in Cloth(refit))
        {
            var t = part.Triangles;
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                var n = Vector3.Cross(P(refit, t[k + 1]) - P(refit, t[k]), P(refit, t[k + 2]) - P(refit, t[k]));
                normals[t[k]] += n;
                normals[t[k + 1]] += n;
                normals[t[k + 2]] += n;
            }
        }
        return pairs.Select(pr =>
        {
            var a = P(refit, cards[pr.Triangle]);
            var b = P(refit, cards[pr.Triangle + 1]);
            var c = P(refit, cards[pr.Triangle + 2]);
            var at = a * (1f - pr.U - pr.W) + b * pr.U + c * pr.W;
            int v = map[pr.Vertex];
            if (v < 0) throw new InvalidOperationException($"cloth vertex {pr.Vertex} did not come through the refit");
            var n = normals[v];
            if (n.LengthSquared() < 1e-16f) return 0f;
            return -Vector3.Dot(at - P(refit, v), Vector3.Normalize(n));
        }).ToArray();
    }
}
