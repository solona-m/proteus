using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;

namespace Proteus.Tests;

/// <summary>
/// Measures for "Starlight"'s Valentione Skirt refits (see <see cref="StarlightBottomsLocalTests"/>): skin through its
/// coarse stockings, new-body skin the cut left out where the author's skin was whole, and cloth movement out of line with
/// its neighbours.
/// </summary>
internal static class StarlightMeasure
{
    /// <summary>The stockings' material: the skirt's legs parts share it with the panties, the band and the skirt.</summary>
    internal const string StockingMaterial = "mt_c0201e0145_dwn_d.mtrl";

    /// <summary>The stockings' own parts: thigh to knee (<c>atr_hiz</c>) and shin (<c>atr_sne</c>).</summary>
    internal static readonly string[] StockingAttributes = ["atr_hiz", "atr_sne"];

    /// <summary>The labels of a model's stocking parts: the material and an attribute, never a label number (the swap
    /// moves the meshes).</summary>
    internal static HashSet<string> StockingParts(ModelParts m)
    {
        var bits = StockingAttributes.Select(a => m.AttributeNames.ToList().IndexOf(a)).Where(i => i >= 0).ToList();
        return m.Parts.Where(p => p.Island < 0 && p.Material.EndsWith(StockingMaterial, StringComparison.Ordinal)
                                  && bits.Any(b => (p.AttributeMask & (1u << b)) != 0))
                      .Select(p => p.Label).ToHashSet();
    }

    /// <summary>The garment's skin points standing outside its stocking parts (<see cref="SkinThroughCloth"/>).</summary>
    internal static int StockingSkinOut(ModelParts m)
    {
        var stockings = StockingParts(m);
        return SkinThroughCloth.Find(m).Outside.Count(o => stockings.Contains(o.Over));
    }

    /// <summary>The garment's skin points standing outside any cloth but the stockings: the panties, the band, the skirt.</summary>
    internal static int SkinOutOffStockings(ModelParts m)
    {
        var stockings = StockingParts(m);
        return SkinThroughCloth.Find(m).Outside.Count(o => !stockings.Contains(o.Over));
    }

    /// <summary>The author's crotch cut: inside |x| &lt; 40 mm between y 0.78 and 0.95 m (measured from the skirt's own skin's
    /// open edges: the crotch boundary loop runs x ±0-20 mm, y 0.857-0.891 m; the panties band covers the rest).</summary>
    internal static bool InCrotch(Vector3 p) => MathF.Abs(p.X) < 0.04f && p.Y is > 0.78f and < 0.95f;

    /// <summary>The author's own crotch hole: the skirt's skin's crotch boundary loop, |x| &lt; 20 mm, y 0.857-0.891 m.</summary>
    internal static bool InAuthorsHole(Vector3 p) => MathF.Abs(p.X) < 0.02f && p.Y is > 0.857f and < 0.891f;

    /// <summary>The legs below the panties (0.90 m): above it the author's skin ends in a V at the front and back, cut
    /// under the panties band and the skirt's waist.</summary>
    internal const float LegsTop = 0.90f;

    /// <summary>
    /// Skin triangles of the new body (<paramref name="body"/>) the refit did not draw, where the author's skin was whole:
    /// on the legs below <see cref="LegsTop"/>, outside <see cref="InCrotch"/>. A triangle counts as drawn when the
    /// refit's skin has a triangle on the same three corners (to 0.2 mm).
    /// </summary>
    internal static List<Vector3> LeftOutBodySkin(ModelParts body, ModelParts refit)
        => LeftOut(body, refit).Where(c => !InCrotch(c) && c.Y < LegsTop).ToList();

    /// <summary>Left-out body skin inside the author's own crotch hole (see <see cref="InAuthorsHole"/>).</summary>
    internal static int LeftOutInAuthorsHole(ModelParts body, ModelParts refit) => LeftOut(body, refit).Count(InAuthorsHole);

    private static IEnumerable<Vector3> LeftOut(ModelParts body, ModelParts refit)
    {
        static (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X / 0.0002f), (int)MathF.Round(p.Y / 0.0002f),
                                                  (int)MathF.Round(p.Z / 0.0002f));
        var drawn = new HashSet<(int, int, int)[]>(new TriangleKeys());
        foreach (var part in Skin(refit))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                drawn.Add([Key(P(refit, part.Triangles[t])), Key(P(refit, part.Triangles[t + 1])), Key(P(refit, part.Triangles[t + 2]))]);
        foreach (var part in Skin(body))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                Vector3 a = P(body, part.Triangles[t]), b = P(body, part.Triangles[t + 1]), c = P(body, part.Triangles[t + 2]);
                if (!drawn.Contains([Key(a), Key(b), Key(c)])) yield return (a + b + c) / 3f;
            }
    }

    /// <summary>Three corners as a set, whichever order a triangle lists them in.</summary>
    private sealed class TriangleKeys : IEqualityComparer<(int, int, int)[]>
    {
        public bool Equals((int, int, int)[]? x, (int, int, int)[]? y)
            => x != null && y != null && x.OrderBy(k => k).SequenceEqual(y.OrderBy(k => k));

        public int GetHashCode((int, int, int)[] k)
            => k.Select(e => e.GetHashCode()).Aggregate(0, (h, e) => h ^ e);
    }

    /// <summary>
    /// Cloth movement out of line with its neighbours, per authored cloth vertex of <paramref name="parts"/> (labels in
    /// <paramref name="authored"/>): |move − mean of its welded neighbours' moves|, move = refit position − authored
    /// position. The cloth parts of the two models pair by material and size (see <see cref="Paired"/>); the max and the 99th
    /// percentile, in metres.
    /// </summary>
    /// <param name="perLength">Divide each vertex's figure by its mean authored distance to those neighbours: a slope, for
    /// coarse cloth whose neighbours are 40-80 mm apart, where a millimetre out of line is nothing to see.</param>
    internal static (float Max, float P99) OutOfLine(ModelParts authored, ModelParts refit, IReadOnlyCollection<string> parts,
                                                     bool perLength = false)
    {
        var move = new Dictionary<int, Vector3>();
        var neighbours = new Dictionary<int, HashSet<int>>();
        foreach (var (mine, theirs) in Paired(authored, refit))
        {
            if (!parts.Contains(mine.Label)) continue;
            var t = mine.Triangles;
            var u = theirs.Triangles;
            for (int i = 0; i < t.Length; i++) move[t[i]] = P(refit, u[i]) - P(authored, t[i]);
            for (int i = 0; i + 2 < t.Length; i += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = t[i + e], b = t[i + (e + 1) % 3];
                    (neighbours.TryGetValue(a, out var na) ? na : neighbours[a] = []).Add(b);
                    (neighbours.TryGetValue(b, out var nb) ? nb : neighbours[b] = []).Add(a);
                }
        }
        var off = neighbours.Where(kv => kv.Value.Count > 0)
                            .Select(kv =>
                            {
                                float d = (move[kv.Key] - kv.Value.Aggregate(Vector3.Zero, (s, n) => s + move[n]) / kv.Value.Count).Length();
                                if (!perLength) return d;
                                float span = kv.Value.Average(n => Vector3.Distance(P(authored, kv.Key), P(authored, n)));
                                return span > 1e-6f ? d / span : 0f;
                            })
                            .OrderBy(x => x).ToList();
        return off.Count == 0 ? (0f, 0f) : (off[^1], off[(int)MathF.Floor(0.99f * (off.Count - 1))]);
    }

    /// <summary>The labels of the authored model's stocking, skirt and panty parts (mesh 1, <see cref="StockingMaterial"/>).</summary>
    internal static HashSet<string> LegsParts(ModelParts authored)
        => Cloth(authored).Where(p => p.Material.EndsWith(StockingMaterial, StringComparison.Ordinal)).Select(p => p.Label).ToHashSet();

    /// <summary>Turned-over triangles per authored cloth part (labels of <paramref name="authored"/>; parts as <see cref="Paired"/>).</summary>
    internal static Dictionary<string, int> TurnedOver(ModelParts authored, ModelParts refit)
    {
        var byPart = new Dictionary<string, int>();
        foreach (var (mine, theirs) in Paired(authored, refit))
        {
            var t = mine.Triangles;
            var u = theirs.Triangles;
            int turned = 0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                var n0 = Vector3.Cross(P(authored, t[i + 1]) - P(authored, t[i]), P(authored, t[i + 2]) - P(authored, t[i]));
                var n1 = Vector3.Cross(P(refit, u[i + 1]) - P(refit, u[i]), P(refit, u[i + 2]) - P(refit, u[i]));
                if (n0.LengthSquared() < 1e-16f) continue;
                if (n1.LengthSquared() < 1e-16f || Vector3.Dot(n0, n1) <= 0f) turned++;
            }
            byPart[mine.Label] = turned;
        }
        return byPart;
    }

    /// <summary>The authored model's cloth parts, each with the refit's part it became: the same material and triangle
    /// count, taken in order (the refit moves positions only, though the swap renumbers the meshes).</summary>
    private static IEnumerable<(ModelPart Mine, ModelPart Theirs)> Paired(ModelParts authored, ModelParts refit)
    {
        var left = Cloth(refit).ToList();
        foreach (var part in Cloth(authored))
        {
            int k = left.FindIndex(o => o.Material == part.Material && o.Triangles.Length == part.Triangles.Length);
            if (k < 0) continue;
            yield return (part, left[k]);
            left.RemoveAt(k);
        }
    }

    private static IEnumerable<ModelPart> Skin(ModelParts m)
        => m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material));

    private static IEnumerable<ModelPart> Cloth(ModelParts m)
        => m.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)
                              && !BodySurface.IsSmallclothes(p.Material));

    private static Vector3 P(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
}
