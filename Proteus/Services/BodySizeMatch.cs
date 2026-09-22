using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Proteus.Services;

/// <summary>
/// Which body option a garment was authored against.
/// <para/>
/// Gear models carry a copy of the body's skin mesh beside their cloth — that is how a top reshapes the chest it
/// exposes — so the garment usually holds the answer. The question is how to read it, and the obvious reading is wrong
/// in a way worth recording.
/// <para/>
/// Scoring every probe point against every candidate — a hit rate, or an RMS over the whole body mesh — is dominated by
/// points that cannot tell one option from another. Most of a chest model is identical across all 114 of Neolithe's
/// options: the arms, back and shoulders never change. On "This Old Thing" every candidate scored about 42% on those
/// alone, the real signal drowned, and the detector called the author's M "Macadamia L" and their L "Macadamia S" —
/// so the refit ran as a shrink when the author had made a grow, and came out worse than doing nothing.
/// <para/>
/// So only probes where the candidates actually DISAGREE are scored. That region is where the sizes differ, which is
/// also exactly where an author sculpts — a top that lifts the chest is sculpted there — so nothing matches it
/// exactly, and the score is the RMS distance over it rather than an exact hit rate. Measured on the same outfit, this
/// picks the author's size letter correctly for all four of XS, S, M and L, and finds they built on the Pushup chest;
/// refitting between the detected sizes reproduces the author's own hand-fitted L to 0.24 mm on the body mesh.
/// </summary>
internal static class BodySizeMatch
{
    /// <summary>At or above this share of exact hits over the discriminating region, the garment's body mesh IS this
    /// option, copied.</summary>
    public const float Exact = 0.98f;

    /// <summary>The best fit this much closer than the next distinct one is a clear winner.</summary>
    private const float LikelyRatio = 0.85f;

    /// <summary>Closer than the next distinct one by this much is still worth showing as a guess.</summary>
    private const float GuessRatio = 0.95f;

    /// <summary>
    /// A probe discriminates when the candidates' distances to it spread by more than this (0.5 mm): below it, every
    /// candidate is saying the same thing there.
    /// </summary>
    private const float Discriminates = 0.0005f;

    /// <summary>
    /// Two candidates this close in score are the same answer as far as this garment can tell (1%). Neolithe's Default
    /// and Neobelly chests differ only at the belly; a top that does not reach the belly scores them identically, and
    /// either gives the same refit where the garment is, so they are one answer rather than a tie to agonise over.
    /// </summary>
    private const float SameAnswer = 0.01f;

    /// <summary>How far to look for a candidate's surface from a probe; further than this is simply "far".</summary>
    private const float Reach = 0.05f;

    /// <summary>
    /// A probe only counts as evidence where the garment's body mesh actually lies ON some candidate (10 mm). A top's
    /// body mesh is the torso; against the LEGS models almost all of it is far away, and the few probes near the edge
    /// of <see cref="Reach"/> differ between candidates only because one is 49 mm off and another is capped at 50.
    /// Counting those produced a confident "SFW Small" for every size of "This Old Thing" — a wrong answer is worse
    /// than an admitted blank, so they are not counted at all.
    /// </summary>
    private const float Contact = 0.01f;

    /// <summary>Fewer evidence probes than this, and the garment cannot say which size it was made for here.</summary>
    private const int MinEvidence = 100;

    /// <summary>Probe points taken from the garment. Enough to be decisive, few enough to keep 131 candidates cheap.</summary>
    private const int MaxProbes = 3000;

    /// <param name="HitRate">Share of the discriminating probes sitting exactly on a vertex of this candidate.</param>
    /// <param name="Rms">Root-mean-square distance to this candidate's surface, over the discriminating probes.</param>
    internal readonly record struct Score(BodyOption Option, float HitRate, float Rms);

    /// <summary>How sure the top score is, and what to say about it.</summary>
    internal enum Confidence
    {
        /// <summary>The garment has no body mesh, so there is nothing to match on.</summary>
        NoBodyMesh,

        /// <summary>The garment has a body mesh, but too little of it lies on this slot's body to tell sizes apart —
        /// a top's torso against the legs models, say.</summary>
        TooLittle,
        Exact,
        Likely,
        Guess,
        Ambiguous,
    }

    /// <param name="FromCloth">The answer was read from how the garment's CLOTH sits on each body, because its body mesh
    /// did not reach this slot — see <see cref="RankByCloth"/>. <see cref="Score.Rms"/> is then the mean gap.</param>
    internal sealed record Ranking(Confidence Confidence, IReadOnlyList<Score> Scores, bool FromCloth = false)
    {
        public Score? Best => Scores.Count > 0 ? Scores[0] : null;

        /// <summary>Whether the top score is worth selecting for the user rather than merely showing.</summary>
        public bool Preselect => Confidence is Confidence.Exact or Confidence.Likely;
    }

    /// <summary>
    /// The share of nearby cloth that may sit inside a body and still count as clearing it (0.5%). Not zero: authors
    /// tuck the odd waistband vertex under the skin on purpose.
    /// </summary>
    private const float ClearsBelow = 0.005f;

    /// <summary>How close cloth has to be to a body to count as sitting on it, for the cloth reading (30 mm).</summary>
    private const float ClothReach = 0.03f;

    /// <summary>
    /// Rank <paramref name="candidates"/> by how well they explain the garment's own body mesh.
    /// Candidates that cannot be read are skipped rather than scored zero, so a broken file does not win by default.
    /// </summary>
    /// <param name="garmentBones">The bones the garment is rigged to. Among bodies that fit it equally well — Rue's plain
    /// and Yiggle sizes are one mesh rigged two ways — the one whose rig covers the most of these leads, so a garment
    /// made for Yiggle reads as Yiggle. Null orders equals by the author's listing alone.</param>
    public static Ranking Rank(ModelParts garment, IReadOnlyList<BodyOption> candidates, Func<BodyOption, string> pathOf,
                               IReadOnlySet<string>? garmentBones = null)
    {
        var probes = Probes(garment);
        if (probes.Count == 0) return new Ranking(Confidence.NoBodyMesh, []);

        // One entry per distinct BODY, in the author's order. Distinct means different vertex positions: Neolithe's
        // Emperor's New Robe mirrors are neither the same path nor the same bytes as the SmallClothes sizes — same
        // geometry, different undies material — so only a geometric key merges them.
        var bodies = new List<(BodyOption Option, float[] Distance, bool[] Hit)>();
        var rigOf = new Dictionary<BodyOption, IReadOnlySet<string>>();
        var seenGeometry = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in candidates.GroupBy(o => o.Rel, StringComparer.OrdinalIgnoreCase))
        {
            var option = group.First();
            var body = Load(pathOf(option));
            if (body == null || !seenGeometry.Add(body.ContentKey)) continue;

            var distance = new float[probes.Count];
            var hit = new bool[probes.Count];

            // The candidate lives in a process-wide cache, and BodySurface reuses a scratch buffer between queries, so
            // two rankings touching the same body at once would corrupt each other. Uncontended in the normal case.
            lock (body)
            {
                for (int i = 0; i < probes.Count; i++)
                {
                    hit[i] = body.Snap.Contains(MeshMath.PositionKey(BodyRetarget.ToVec(probes[i]),
                                                                     BodyRetarget.SnapPerMetre));
                    distance[i] = body.Surface.Nearest(probes[i], Reach, out var h) ? h.Distance : Reach;
                }
            }
            bodies.Add((option, distance, hit));
            rigOf[option] = body.Bones;
        }

        if (bodies.Count == 0) return new Ranking(Confidence.Ambiguous, []);

        var distances = bodies.Select(b => b.Distance).ToList();
        var touching = Touching(distances, probes.Count);
        var region = DiscriminatingRegion(distances, touching);

        // Where the candidates are all the same shape, the garment agrees with every one of them: score over everywhere
        // it touches, so the scores stay comparable and the ties are reported as ties.
        var scoredOver = region.Count > 0 ? region : touching;

        var order = bodies.Select(b => b.Option).ToList();
        Func<BodyOption, int>? rigMatch = garmentBones is { Count: > 0 }
            ? o => rigOf.TryGetValue(o, out var rig) ? garmentBones.Count(rig.Contains) : 0
            : null;
        var scores = scoredOver.Count == 0
                         ? bodies.Select(b => new Score(b.Option, 0f, Reach)).ToList()
                         : FirstListedAmongEquals(bodies.Select(b => ScoreOver(b.Option, b.Distance, b.Hit, scoredOver))
                                                        .OrderBy(s => s.Rms)
                                                        .ThenByDescending(s => s.HitRate)
                                                        .ToList(),
                                                  order, rigMatch);

        // Too little of the garment's body mesh lies on this slot's body to be evidence either way. The cloth may still
        // say — a top's torso never reaches the legs models, but its hem hangs right over them.
        if (touching.Count < MinEvidence)
            return RankByCloth(garment, bodies.Select(b => b.Option).ToList(), pathOf) is { } byCloth
                       ? byCloth
                       : new Ranking(Confidence.TooLittle, scores);

        return new Ranking(Judge(scores), scores);
    }

    /// <summary>
    /// Read the size from how the garment's CLOTH sits on each candidate, for a slot its body mesh never reaches.
    /// <para/>
    /// An author fits cloth to sit just outside the body it was made for. Against a larger body the cloth passes into
    /// it; against a smaller one it floats clear. So the authored size is the TIGHTEST body the cloth still clears.
    /// Measured on "This Old Thing", whose top's torso body mesh never touches the legs: its M's hem clears Small and
    /// Medium and passes 75 vertices into Large, and its L's clears all three — Medium and Large, exactly the hips its
    /// author fitted, which the refit had needed to make the cloth six times closer to the author's own sizes.
    /// </summary>
    /// <returns>Null when the cloth does not reach this slot's bodies either, so there is nothing to read.</returns>
    private static Ranking? RankByCloth(ModelParts garment, IReadOnlyList<BodyOption> options,
                                        Func<BodyOption, string> pathOf)
    {
        var cloth = ClothProbes(garment);
        if (cloth.Count == 0) return null;

        var readings = new List<(BodyOption Option, int Near, int Inside, float Gap, string Family)>();
        foreach (var option in options)
        {
            var body = Load(pathOf(option));
            if (body == null) continue;

            int near = 0, inside = 0, outside = 0;
            double gap = 0;
            lock (body)
            {
                foreach (var p in cloth)
                {
                    if (!body.Surface.Nearest(p, ClothReach, out var hit)) continue;
                    near++;
                    float s = Vector3.Dot(p - hit.Point, hit.Normal);
                    if (s < 0f) inside++;
                    else { gap += s; outside++; }
                }
            }
            readings.Add((option, near, inside, outside > 0 ? (float)(gap / outside) : ClothReach, body.TopologyKey));
        }

        var evidence = readings.Where(r => r.Near >= MinEvidence).ToList();
        if (evidence.Count == 0) return null;

        // The size is read within ONE family, and the family is the author's first-listed one.
        //
        // "Tightest body the cloth clears" is the right signal along a size axis and the wrong one across families.
        // Across them it rewards any body that is fatter wherever the garment happens to have room: Neolithe's Neobelly
        // legs fill a top's hem with their belly, so they read tighter than the plain legs without being what the top
        // was made for. A hem cannot tell belly from no belly, so the family is not read from it at all — the plain,
        // first-listed family is taken, the size is read within it, and the other families follow for the user to
        // choose.
        string primary = evidence.OrderBy(r => IndexIn(options, r.Option)).First().Family;
        var scores = new List<Score>();
        Confidence confidence = Confidence.Ambiguous;
        foreach (var family in evidence.GroupBy(r => r.Family)
                                       .OrderBy(g => g.Key == primary ? 0 : 1)
                                       .ThenBy(g => g.Min(r => IndexIn(options, r.Option))))
        {
            var members = family.ToList();
            var clearing = members.Where(Clears).OrderBy(r => r.Gap).ToList();
            var head = FirstListedAmongEquals(clearing.Select(r => new Score(r.Option, 0f, r.Gap)).ToList(), options);
            scores.AddRange(head);
            scores.AddRange(members.Where(r => !Clears(r)).OrderBy(r => r.Gap).Select(r => new Score(r.Option, 0f, r.Gap)));

            if (family.Key != primary) continue;

            // The cloth passes into every body of the family: nothing it was plainly fitted to. Otherwise a size is only
            // pinned down when a larger one exists that the cloth passes into; without one, "the tightest it clears"
            // might equally be "loose over all of them" — worth showing, not worth choosing for the user.
            confidence = clearing.Count == 0 ? Confidence.Ambiguous
                       : members.Any(r => !Clears(r)) ? Confidence.Likely
                       : Confidence.Guess;
        }

        return new Ranking(confidence, scores, FromCloth: true);

        static bool Clears((BodyOption, int Near, int Inside, float, string) r) => r.Inside <= r.Near * ClearsBelow;
    }

    private static int IndexIn(IReadOnlyList<BodyOption> options, BodyOption option)
    {
        for (int i = 0; i < options.Count; i++)
            if (options[i] == option) return i;
        return int.MaxValue;
    }

    /// <summary>
    /// Of the leading scores that are the SAME answer (within <see cref="SameAnswer"/> of the best), put the one the
    /// author listed first at the front.
    /// <para/>
    /// Which of several equally good candidates leads is not cosmetic. Neolithe's legs group lists the plain SFW sizes
    /// alongside Bulge, Gen A/B/C and Puffy variants that share the hips and differ only at the crotch, which no hem
    /// reaches — so they read identically from a top, give or take a hundredth of a millimetre. Leaving the pick to
    /// that hundredth chose "Gen C Small" for one size and "SFW L" for another; those are different meshes, so the pair
    /// was refused and the refit lost its legs. The author's own ordering puts the plain family first, and it is the
    /// one a user would reach for.
    /// </summary>
    /// <param name="rigMatch">How much of the garment's rig a candidate covers; among equals, more leads. Null to skip.</param>
    private static List<Score> FirstListedAmongEquals(List<Score> sorted, IReadOnlyList<BodyOption> order,
                                                      Func<BodyOption, int>? rigMatch = null)
    {
        if (sorted.Count < 2) return sorted;

        float best = MathF.Max(sorted[0].Rms, 1e-6f);
        int same = sorted.TakeWhile(s => s.Rms <= best * (1f + SameAnswer)).Count();
        if (same < 2) return sorted;

        var rank = new Dictionary<BodyOption, int>();
        for (int i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);

        return sorted.Take(same)
                     .OrderByDescending(s => rigMatch?.Invoke(s.Option) ?? 0)
                     .ThenBy(s => rank.TryGetValue(s.Option, out int i) ? i : int.MaxValue)
                     .Concat(sorted.Skip(same))
                     .ToList();
    }

    /// <summary>The garment's cloth points, welded and thinned the same way as <see cref="Probes"/>.</summary>
    private static List<Vector3> ClothProbes(ModelParts garment)
    {
        int vc = garment.Positions.Length / 3;
        var seen = new HashSet<(int, int, int)>();
        var all = new List<Vector3>();

        foreach (var part in garment.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
            {
                if (v < 0 || v >= vc) continue;
                var p = new Vector3(garment.Positions[v * 3], garment.Positions[v * 3 + 1],
                                    garment.Positions[v * 3 + 2]);
                if (seen.Add(MeshMath.PositionKey(BodyRetarget.ToVec(p), BodyRetarget.SnapPerMetre))) all.Add(p);
            }
        }

        if (all.Count <= MaxProbes) return all;
        int stride = (all.Count + MaxProbes - 1) / MaxProbes;
        var thinned = new List<Vector3>(MaxProbes);
        for (int i = 0; i < all.Count; i += stride) thinned.Add(all[i]);
        return thinned;
    }

    /// <summary>The probes lying on at least one candidate — the only ones that are evidence. See <see cref="Contact"/>.</summary>
    private static List<int> Touching(IReadOnlyList<float[]> distances, int probeCount)
    {
        var touching = new List<int>();
        for (int i = 0; i < probeCount; i++)
        {
            float lo = float.MaxValue;
            foreach (var d in distances) lo = MathF.Min(lo, d[i]);
            if (lo <= Contact) touching.Add(i);
        }
        return touching;
    }

    /// <summary>Of the touching probes, the ones where the candidates disagree — the region that decides it.</summary>
    private static List<int> DiscriminatingRegion(IReadOnlyList<float[]> distances, List<int> touching)
    {
        var region = new List<int>();
        foreach (int i in touching)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var d in distances)
            {
                lo = MathF.Min(lo, d[i]);
                hi = MathF.Max(hi, d[i]);
            }
            if (hi - lo > Discriminates) region.Add(i);
        }
        return region;
    }

    private static Score ScoreOver(BodyOption option, float[] distance, bool[] hit, List<int> region)
    {
        double sum = 0;
        int hits = 0;
        foreach (int i in region)
        {
            sum += distance[i] * distance[i];
            if (hit[i]) hits++;
        }
        return new Score(option, (float)hits / region.Count, (float)Math.Sqrt(sum / region.Count));
    }

    private static Confidence Judge(List<Score> scores)
    {
        var best = scores[0];
        if (best.HitRate >= Exact) return Confidence.Exact;
        if (scores.Count == 1) return Confidence.Guess;

        // Measured against the first candidate that is a genuinely DIFFERENT answer, skipping the ones that score the
        // same as the best because the garment cannot tell them apart. See SameAnswer.
        float floor = MathF.Max(best.Rms, 1e-6f);
        var runnerUp = scores.Skip(1).FirstOrDefault(s => s.Rms > floor * (1f + SameAnswer));
        if (runnerUp.Option == null) return Confidence.Likely;   // everything left is the same answer

        float ratio = best.Rms / MathF.Max(runnerUp.Rms, 1e-6f);
        return ratio < LikelyRatio ? Confidence.Likely
             : ratio < GuessRatio ? Confidence.Guess
             : Confidence.Ambiguous;
    }

    /// <summary>
    /// The garment's own body-mesh points, welded and thinned. Taken from whole submeshes drawing with a body skin
    /// material — the cloth is no use here, because cloth is exactly the part that does not match the body.
    /// </summary>
    private static List<Vector3> Probes(ModelParts garment)
    {
        int vc = garment.Positions.Length / 3;
        var seen = new HashSet<(int, int, int)>();
        var all = new List<Vector3>();

        foreach (var part in garment.Parts)
        {
            if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
            {
                if (v < 0 || v >= vc) continue;
                var p = new Vector3(garment.Positions[v * 3], garment.Positions[v * 3 + 1],
                                    garment.Positions[v * 3 + 2]);
                if (seen.Add(MeshMath.PositionKey(BodyRetarget.ToVec(p), BodyRetarget.SnapPerMetre))) all.Add(p);
            }
        }

        if (all.Count <= MaxProbes) return all;

        // Every k-th, not a random sample: the answer must not change between two runs on the same files.
        int stride = (all.Count + MaxProbes - 1) / MaxProbes;
        var thinned = new List<Vector3>(MaxProbes);
        for (int i = 0; i < all.Count; i += stride) thinned.Add(all[i]);
        return thinned;
    }

    /// <param name="ContentKey">Identifies the MODEL rather than the file, so two copies of one body deduplicate.</param>
    /// <param name="TopologyKey">Identifies the MESH — vertex count and mesh layout — so two sizes of one body share it
    /// and a different family (Neobelly, Gen C) does not.</param>
    /// <param name="Bones">The bones the body is rigged to — which of two identical meshes a garment was made for.</param>
    private sealed record Candidate(BodySurface Surface, HashSet<(int, int, int)> Snap, string ContentKey,
                                    string TopologyKey, IReadOnlySet<string> Bones);

    /// <summary>
    /// Read and index one candidate body, remembering it for the session.
    /// <para/>
    /// Neolithe's chest group alone has 131 options, and a detection pass reads all of them. Keyed on length and write
    /// time as well as path, so editing a body mod during a session is picked up.
    /// </summary>
    private static readonly ConcurrentDictionary<(string Path, long Length, long Ticks), Candidate?> Cache = new();

    private static Candidate? Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;

            return Cache.GetOrAdd((path, info.Length, info.LastWriteTimeUtc.Ticks), key =>
            {
                var bytes = File.ReadAllBytes(key.Path);
                var model = ModelPartReader.Read(bytes);
                if (model == null) return null;

                var surface = new BodySurface(model, BodySurface.CellFor(0.01f));
                var snap = new HashSet<(int, int, int)>();
                foreach (int v in surface.SkinVertices)
                    snap.Add(MeshMath.PositionKey(BodyRetarget.ToVec(surface.PositionOf(v)),
                                                  BodyRetarget.SnapPerMetre));

                // Keyed on the VERTEX POSITIONS, not on the file's bytes: see the dedupe in Rank. And on the RIG: one
                // mesh rigged two ways (Rue's plain and Yiggle sizes) is two bodies, and which one the garment was made
                // for decides which bones it keeps when it is refitted.
                var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
                string content = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes<float>(model.Positions)))
                               + "|" + string.Join(",", bones.OrderBy(b => b, StringComparer.Ordinal));
                string topology = model.Positions.Length + ":" +
                                  string.Join(",", model.MeshSpans.Select(s => $"{s.Mesh}/{s.Count}"));
                return new Candidate(surface, snap, content, topology, bones);
            });
        }
        catch
        {
            return null;
        }
    }
}
