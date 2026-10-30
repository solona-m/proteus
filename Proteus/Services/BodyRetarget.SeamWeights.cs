using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// How near two cloth vertices must have sat, as authored, to be one place split in two (0.5 mm) — see
    /// <see cref="KeepSeamsTogether"/>.
    /// <para/>
    /// Measured on voidblood's "Dei Gratia" refitted onto Bibo+: the blouse's waist seam is split 9.8-11.3 µm apart, and
    /// its centre front 104 µm apart; nothing split again until 0.82 mm, where the pairs are neighbours along the
    /// surface, not a seam. 0.1 mm (<see cref="ModelPartReader"/>'s weld) missed the centre front, the worst of them.
    /// </summary>
    internal const float SeamReach = 5e-4f;

    /// <summary>What <see cref="KeepSeamsTogether"/> did with one class of a seam's split vertices.</summary>
    internal enum SeamKind
    {
        /// <summary>Its vertices already carried the same weights, or none were rewritten: left as it was.</summary>
        Agreeing,
        /// <summary>Given one averaged weighting.</summary>
        Averaged,
        /// <summary>A member is held: every member keeps the author's weights.</summary>
        Reverted,
        /// <summary>A member is in a piece kept whole: left as the earlier passes made it.</summary>
        KeepShape,
    }

    /// <summary>
    /// The outcome of <see cref="KeepSeamsTogether"/>. <see cref="ClassOf"/> is, per vertex, its class's index in
    /// <see cref="Kinds"/>, or -1 for a vertex no other shares a place with.
    /// </summary>
    internal sealed record SeamOutcome(int Added, int Reverted, int Averaged, int SkippedKeepShape, int LargestClass,
                                       float LargestDiameter, int[] ClassOf, SeamKind[] Kinds)
    {
        /// <summary>The change to the count of vertices given new weights: added, less those handed back to the author.</summary>
        public int Net => Added - Reverted;
    }

    /// <summary>
    /// Give the vertices one place of the cloth is split into — at a seam between two submeshes, or a texture seam —
    /// one weighting, where the author weighted them alike.
    /// <para/>
    /// Each vertex looks the bodies up for itself, and the passes after the lookup weld a seam's copies by rounding their
    /// positions to 10 µm (<see cref="SmoothWeights"/>, <see cref="BlendShiri"/>, <see cref="EaseCrotch"/>): copies lying
    /// across a rounding boundary, or a little further apart than it, are smoothed each over its own side's neighbours.
    /// Dei Gratia's blouse meets its skirt panel at the waist in two submeshes of one mesh, its copies 10 µm apart and
    /// weighted identically; refitted onto Bibo+ the two sides came out up to 0.33 apart (j_kosi 0.22 against 0.42) and
    /// parted at the waist as soon as the hips moved. <see cref="KeepLayersTogether"/> leaves the same mesh alone.
    /// <para/>
    /// Two vertices are partners when they lay within <see cref="SeamReach"/> of each other, weighted within
    /// <see cref="LayerSameWeights"/>, and share neither a triangle edge nor a neighbour — a seam's copies never do, and
    /// neighbours along a surface always do. Classes are built from partner pairs nearest first, and two join only when
    /// every member of one is a partner of every member of the other: so a class is never wider than the reach, however
    /// dense the cloth, nor drifts along a chain, and a seam's copies pair with each other before either can with the
    /// neighbour of the other.
    /// <para/>
    /// Each mesh counts once, as in <see cref="KeepLayersTogether"/>. A class already weighted as one is left bit for bit;
    /// one touching a held vertex keeps the author's weights throughout, and one touching a piece kept whole is left to
    /// the crotch ease, which eases the piece as one. Runs before <see cref="KeepToSide"/> and <see cref="KeepOffLegs"/>,
    /// which then hold every member to its own limits: averaging, cut back to eight bones, can raise a share.
    /// </summary>
    /// <param name="result">New weights per vertex, null where the author's stand. Updated in place.</param>
    internal static SeamOutcome KeepSeamsTogether(ModelParts model, XivLiveMesh.SkinnedMesh own, float[] wasAt,
                                                  (string Bone, float W)[]?[] result, IReadOnlySet<int>? held,
                                                  IReadOnlyList<IReadOnlyCollection<int>>? keepShape)
    {
        int vc = model.Positions.Length / 3;
        var classOf = new int[vc];
        Array.Fill(classOf, -1);
        var kinds = new List<SeamKind>();

        var cloth = ClothVertices(model);
        cloth.Sort();

        // Each cloth vertex's neighbours by edge: what tells a neighbour along the surface from a seam's other copy. Two
        // vertices sharing an edge, or a neighbour — a quad's opposite corners, 0.42 mm apart on a 0.3 mm strip, share
        // two — lie on one surface; a seam's copies sit on fans of their own and share neither.
        var next = new Dictionary<int, HashSet<int>>();
        void Link(int a, int b)
        {
            if (a == b) return;
            if (!next.TryGetValue(a, out var na)) next[a] = na = [];
            if (!next.TryGetValue(b, out var nb)) next[b] = nb = [];
            na.Add(b);
            nb.Add(a);
        }
        foreach (var part in model.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                Link(a, b);
                Link(b, c);
                Link(a, c);
            }
        }
        bool Joined(int a, int b)
            => next.TryGetValue(a, out var na) && next.TryGetValue(b, out var nb) && (na.Contains(b) || na.Overlaps(nb));

        var kept = new HashSet<int>();
        if (keepShape != null)
            foreach (var piece in keepShape) kept.UnionWith(piece);

        Vector3 At(int v) => new(wasAt[v * 3], wasAt[v * 3 + 1], wasAt[v * 3 + 2]);
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / SeamReach), (int)MathF.Floor(p.Y / SeamReach),
                                            (int)MathF.Floor(p.Z / SeamReach));
        var grid = new Dictionary<(int, int, int), List<int>>();
        foreach (int v in cloth)
        {
            var c = Cell(At(v));
            if (!grid.TryGetValue(c, out var bucket)) grid[c] = bucket = [];
            bucket.Add(v);
        }

        var authored = new Dictionary<int, List<(string Bone, float W)>>();
        List<(string Bone, float W)> Authored(int v)
            => authored.TryGetValue(v, out var a) ? a : authored[v] = Influences(own, v);

        var meshOf = new int[vc];
        Array.Fill(meshOf, -1);
        foreach (var span in model.MeshSpans)
            for (int i = 0; i < span.Count && span.BaseVertex + i < vc; i++) meshOf[span.BaseVertex + i] = span.Mesh;

        bool Partners(int a, int b) => Vector3.DistanceSquared(At(a), At(b)) <= SeamReach * SeamReach
                                       && Difference(Authored(a), Authored(b)) <= LayerSameWeights && !Joined(a, b);

        // Every pair that could be one place, nearest first, so a seam's own copies (10 µm) pair before a copy and the
        // neighbour of its partner (0.4 mm) can.
        var pairs = new List<(float D, int A, int B)>();
        foreach (int v in cloth)
        {
            var (cx, cy, cz) = Cell(At(v));
            for (int x = cx - 1; x <= cx + 1; x++)
            for (int y = cy - 1; y <= cy + 1; y++)
            for (int z = cz - 1; z <= cz + 1; z++)
                if (grid.TryGetValue((x, y, z), out var bucket))
                    foreach (int u in bucket)
                        if (u > v && Partners(v, u)) pairs.Add((Vector3.Distance(At(v), At(u)), v, u));
        }
        pairs.Sort();
        var partnered = new HashSet<(int, int)>(pairs.Select(p => (p.A, p.B)));
        bool Paired(int a, int b) => partnered.Contains((Math.Min(a, b), Math.Max(a, b)));

        // Two classes join only when every vertex of one is a partner of every vertex of the other: no class is wider than
        // the reach, drifts in weight, or holds two neighbours along a surface.
        var groupOf = new Dictionary<int, List<int>>();
        foreach (var (_, a, b) in pairs)
        {
            var ga = groupOf.TryGetValue(a, out var x0) ? x0 : [a];
            var gb = groupOf.TryGetValue(b, out var y0) ? y0 : [b];
            if (ReferenceEquals(ga, gb) || !ga.All(m => gb.All(n => Paired(m, n)))) continue;
            ga.AddRange(gb);
            foreach (int m in ga) groupOf[m] = ga;
        }

        int added = 0, reverted = 0, averaged = 0, skipped = 0, largest = 0;
        float diameter = 0f;
        foreach (var members in groupOf.Values.Distinct().Select(g => g.OrderBy(m => m).ToList()).OrderBy(g => g[0]))
        {
            int id = kinds.Count;
            foreach (int m in members) classOf[m] = id;
            largest = Math.Max(largest, members.Count);
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (int m in members) { lo = Vector3.Min(lo, At(m)); hi = Vector3.Max(hi, At(m)); }
            diameter = MathF.Max(diameter, Vector3.Distance(lo, hi));

            if (members.Any(kept.Contains))
            {
                kinds.Add(SeamKind.KeepShape);
                skipped++;
                continue;
            }
            if (held != null && members.Any(held.Contains))
            {
                foreach (int m in members)
                    if (result[m] != null) { result[m] = null; reverted++; }
                kinds.Add(SeamKind.Reverted);
                continue;
            }
            if (members.All(m => result[m] == null)) { kinds.Add(SeamKind.Agreeing); continue; }

            IEnumerable<(string Bone, float W)> Now(int v) => result[v] ?? (IEnumerable<(string Bone, float W)>)Authored(v);
            var first = Totals(Now(members[0]));
            if (members.Skip(1).All(m => SameMap(first, Totals(Now(m))))) { kinds.Add(SeamKind.Agreeing); continue; }

            var sum = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var layer in members.GroupBy(m => meshOf[m]))
            {
                float share = 1f / layer.Count();
                foreach (int m in layer)
                    foreach (var (bone, w) in Now(m))
                        sum[bone] = sum.GetValueOrDefault(bone) + w * share;
            }
            var shared = Normalised(sum.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                                       .Take(MaxInfluences).Select(kv => (kv.Key, kv.Value)).ToList(), 1f);
            foreach (int m in members)
            {
                if (result[m] == null) added++;
                result[m] = [.. shared];
            }
            averaged++;
            kinds.Add(SeamKind.Averaged);
        }

        return new SeamOutcome(added, reverted, averaged, skipped, largest, diameter, classOf, [.. kinds]);
    }

    /// <summary>A vertex's influences summed by bone.</summary>
    private static Dictionary<string, float> Totals(IEnumerable<(string Bone, float W)> list)
    {
        var d = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (bone, w) in list) d[bone] = d.GetValueOrDefault(bone) + w;
        return d;
    }

    /// <summary>Whether two bone maps are the same exactly: averaging equal weights can still move the last bit.</summary>
    private static bool SameMap(Dictionary<string, float> a, Dictionary<string, float> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out float w) && w == kv.Value);
}
