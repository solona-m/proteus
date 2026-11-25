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

    /// <summary>Left-out body skin at and above <see cref="LegsTop"/>, outside the crotch: the author's waist edge, its V
    /// at the front and back.</summary>
    internal static int LeftOutAboveLegs(ModelParts body, ModelParts refit)
        => LeftOut(body, refit).Count(c => !InCrotch(c) && c.Y >= LegsTop);

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

    /// <summary>
    /// The authored model's coarse cloth parts: those with a face the sag cap lifts past the old 2 mm (its longest edge
    /// past sqrt(8 r · 2 mm), 28 mm; see <see cref="BodyRetarget.ClearFaceCap(float)"/>) — the stockings, their band, the
    /// panties. Their movement out of line is read as a slope (see <see cref="OutOfLine"/>).
    /// </summary>
    internal static HashSet<string> CoarseParts(ModelParts m)
        => Cloth(m).Where(p =>
        {
            for (int t = 0; t + 2 < p.Triangles.Length; t += 3)
            {
                Vector3 a = P(m, p.Triangles[t]), b = P(m, p.Triangles[t + 1]), c = P(m, p.Triangles[t + 2]);
                float longest = MathF.Max(Vector3.Distance(a, b), MathF.Max(Vector3.Distance(b, c), Vector3.Distance(c, a)));
                if (BodyRetarget.ClearFaceCap(longest) > BodyRetarget.ClearFaceDeepest) return true;
            }
            return false;
        }).Select(p => p.Label).ToHashSet();

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

    // ── the knee standoff's pose measure ──────────────────────────────────

    /// <summary>A posed skin point standing outside its own stocking face.</summary>
    /// <param name="Facing">The face's way out at rest, z: below 0 the back of the leg (the character faces +z).</param>
    internal sealed record BentPoint(int Vertex, Vector3 RestAt, float Depth, string Part, float Facing, int A = -1, int B = -1, int C = -1);

    /// <summary>The posed measure: skin points through their own stocking face, and how many skin points were paired.</summary>
    internal sealed record Bent(IReadOnlyList<BentPoint> Through, int Paired)
    {
        public int Count => Through.Count;
        public float Percentile(float p)
            => Through.Count == 0 ? 0f : Through.Select(t => t.Depth).OrderBy(d => d).ElementAt(Math.Min(Through.Count - 1, (int)(p * Through.Count)));
    }

    /// <summary>
    /// Skin points through the stockings in a pose: each skin vertex under a stocking face at rest
    /// (<see cref="SkinThroughCloth.Covering"/>'s rules) is paired with that face; posed, it counts when it stands outside
    /// that same face, posed, by more than 0.2 mm (the face's way out as at rest). <paramref name="posed"/> is in
    /// <see cref="ModelPartReader"/>'s vertex order.
    /// </summary>
    /// <summary>
    /// <see cref="BentStockingSkinOut(ModelParts, Vector3[])"/> for the knee model against the knob model (decision 6):
    /// pairs made once, on the knob model, and reused on the knee model — the skin vertex found by its rest position, the
    /// face by its stocking part's place and the triangle's index in it (the refit never changes topology). Skin the knee
    /// model draws that the knob model does not is newly drawn: paired on the knee model at rest and counted. Answers both
    /// measures and the skin keys found in one model only.
    /// </summary>
    internal static (Bent Knob, Bent Knee, int OnlyKnob, int OnlyKnee) BentAgainstKnob(ModelParts knobRest, Vector3[] knobPosed,
                                                                                      ModelParts kneeRest, Vector3[] kneePosed)
    {
        var knob = BentStockingSkinOut(knobRest, knobPosed);
        var knobParts = knobRest.Parts.Where(p => StockingParts(knobRest).Contains(p.Label)).ToList();
        var kneeParts = kneeRest.Parts.Where(p => StockingParts(kneeRest).Contains(p.Label)).ToList();
        if (knobParts.Count != kneeParts.Count) throw new InvalidOperationException("the stocking parts differ between the models");
        static (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X / 0.0002f), (int)MathF.Round(p.Y / 0.0002f), (int)MathF.Round(p.Z / 0.0002f));
        HashSet<int> Skin(ModelParts m) => m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                              .SelectMany(p => p.Triangles).ToHashSet();
        var kneeSkinAt = new Dictionary<(int, int, int), int>();
        foreach (int v in Skin(kneeRest)) kneeSkinAt.TryAdd(Key(P(kneeRest, v)), v);
        var knobSkinKeys = Skin(knobRest).Select(v => Key(P(knobRest, v))).ToHashSet();

        var through = new List<BentPoint>();
        int paired = 0, onlyKnob = 0;
        var covers = SkinThroughCloth.Covering(knobRest).Where(c => knobParts.Any(p => p.Label == c.Label)).ToList();
        foreach (var c in covers)
        {
            if (!kneeSkinAt.TryGetValue(Key(c.At), out int sv)) { onlyKnob++; continue; }
            int pi = knobParts.FindIndex(p => p.Label == c.Label);
            var tris = knobParts[pi].Triangles;
            int t = -1;
            for (int i = 0; i + 2 < tris.Length; i += 3)
                if (tris[i] == c.A && tris[i + 1] == c.B && tris[i + 2] == c.C) { t = i; break; }
            if (t < 0) continue;
            var kt = kneeParts[pi].Triangles;
            paired++;
            if (Through(kneePosed, sv, kt[t], kt[t + 1], kt[t + 2], c) is { } by)
                through.Add(new BentPoint(sv, c.At, by, kneeParts[pi].Label, 0f, kt[t], kt[t + 1], kt[t + 2]));
        }
        // Newly drawn skin: paired on the knee model at rest, never dropped.
        var kneeLabels = kneeParts.Select(p => p.Label).ToHashSet();
        int onlyKnee = 0;
        foreach (var c in SkinThroughCloth.Covering(kneeRest).Where(c => kneeLabels.Contains(c.Label) && !knobSkinKeys.Contains(Key(c.At))))
        {
            onlyKnee++;
            paired++;
            if (Through(kneePosed, c.Vertex, c.A, c.B, c.C, c) is { } by) through.Add(new BentPoint(c.Vertex, c.At, by, c.Label, 0f, c.A, c.B, c.C));
        }
        return (knob, new Bent(through, paired), onlyKnob, onlyKnee);
    }

    /// <summary>How far, posed, skin vertex <paramref name="s"/> stands outside face abc (the cover's weights and way out),
    /// or null when not through.</summary>
    private static float? Through(Vector3[] posed, int s, int a, int b, int c, SkinThroughCloth.Cover cover)
    {
        Vector3 pa = posed[a], pb = posed[b], pc = posed[c];
        var n = Vector3.Cross(pb - pa, pc - pa);
        if (n.LengthSquared() < 1e-14f) return null;
        n = Vector3.Normalize(n);
        if (cover.Flip) n = -n;
        float by = Vector3.Dot(posed[s] - (pa * cover.U + pb * cover.V + pc * cover.W), n);
        return by > SkinThroughCloth.Tolerance ? by : null;
    }

    internal static Bent BentStockingSkinOut(ModelParts rest, Vector3[] posed)
    {
        var stockings = StockingParts(rest);
        var covers = SkinThroughCloth.Covering(rest).Where(c => stockings.Contains(c.Label)).ToList();
        var through = new List<BentPoint>();
        foreach (var c in covers)
        {
            Vector3 a = posed[c.A], b = posed[c.B], cc = posed[c.C], s = posed[c.Vertex];
            var n = Vector3.Cross(b - a, cc - a);
            if (n.LengthSquared() < 1e-14f) continue;
            n = Vector3.Normalize(n);
            if (c.Flip) n = -n;
            var q = a * c.U + b * c.V + cc * c.W;
            float by = Vector3.Dot(s - q, n);
            if (by <= SkinThroughCloth.Tolerance) continue;
            var ra = P(rest, c.A);
            var rn = Vector3.Cross(P(rest, c.B) - ra, P(rest, c.C) - ra);
            float facing = rn.LengthSquared() > 1e-14f ? Vector3.Normalize(rn).Z * (c.Flip ? -1f : 1f) : 0f;
            through.Add(new BentPoint(c.Vertex, c.At, by, c.Label, facing, c.A, c.B, c.C));
        }
        return new Bent(through, covers.Count);
    }
}
