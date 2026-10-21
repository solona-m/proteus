using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

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
    internal const float SameAnswer = 0.01f;

    /// <summary>How far to look for a candidate's surface from a probe; further than this is simply "far".</summary>
    internal const float Reach = 0.05f;

    /// <summary>
    /// A probe only counts as evidence where the garment's body mesh actually lies ON some candidate (10 mm). A top's
    /// body mesh is the torso; against the LEGS models almost all of it is far away, and the few probes near the edge
    /// of <see cref="Reach"/> differ between candidates only because one is 49 mm off and another is capped at 50.
    /// Counting those produced a confident "SFW Small" for every size of "This Old Thing" — a wrong answer is worse
    /// than an admitted blank, so they are not counted at all.
    /// </summary>
    private const float Contact = 0.01f;

    /// <summary>Fewer evidence probes than this, and the garment cannot say which size it was made for here.</summary>
    internal const int MinEvidence = 100;

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
    internal const float ClearsBelow = 0.005f;

    /// <summary>How close cloth has to be to a body to count as sitting on it, for the cloth reading (30 mm).</summary>
    internal const float ClothReach = 0.03f;

    /// <summary>
    /// Rank <paramref name="candidates"/> by how well they explain the garment's own body mesh.
    /// Candidates that cannot be read are skipped rather than scored zero, so a broken file does not win by default.
    /// </summary>
    /// <param name="garmentBones">The bones the garment is rigged to. Among bodies that fit it equally well — Rue's plain
    /// and Yiggle sizes are one mesh rigged two ways — the one whose rig covers the most of these leads, so a garment
    /// made for Yiggle reads as Yiggle. Null orders equals by the author's listing alone.</param>
    /// <param name="skinPoints">The garment's <see cref="SkinPoints"/>, when the caller has them already.</param>
    /// <param name="rigByPath">The bones a body file was rigged to before its bones were moved for another race
    /// (<see cref="BakedBodyCatalog.RiggedBefore"/>); null, or null for a file, reads the file's own.</param>
    public static Ranking Rank(ModelParts garment, IReadOnlyList<BodyOption> candidates, Func<BodyOption, string> pathOf,
                               IReadOnlySet<string>? garmentBones = null, List<Vector3>? skinPoints = null,
                               Func<string, IReadOnlySet<string>?>? rigByPath = null)
    {
        var probes = skinPoints ?? SkinPoints(garment);
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
            string path = pathOf(option);
            var body = Load(path);
            // Two bodies one mesh rigged two ways stay two even once their bones were moved onto the same ones.
            var rigged = rigByPath?.Invoke(path);
            string distinct = rigged == null ? "" : "|" + string.Join(",", rigged.Order(StringComparer.Ordinal));
            if (body == null || !seenGeometry.Add(body.ContentKey + distinct)) continue;

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
            rigOf[option] = rigged ?? body.Bones;
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
    /// <remarks>Called directly for a garment with no skin there at all — a boot's cloth shaft, read with its kept foot
    /// taken out — which <see cref="Rank"/> would give up on as having no body mesh before reaching the cloth.</remarks>
    internal static Ranking? RankByCloth(ModelParts garment, IReadOnlyList<BodyOption> options,
                                        Func<BodyOption, string> pathOf)
    {
        var cloth = ClothProbes(garment);
        if (cloth.Count == 0) return null;

        var readings = new List<(BodyOption Option, int Near, int Inside, float Gap, string Family)>();
        foreach (var option in options)
        {
            var body = Load(pathOf(option));
            if (body == null) continue;
            var (near, inside, gap) = ClothOn(cloth, body);
            readings.Add((option, near, inside, gap, body.TopologyKey));
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

    /// <summary>
    /// How the garment's cloth sits on one body: how many cloth points lie within <see cref="ClothReach"/> of it, how many
    /// of those are inside it, and the mean gap of the rest — what <see cref="RankByCloth"/> reads per size. Null when the
    /// body cannot be read or the garment has no cloth.
    /// </summary>
    internal static (int Near, int Inside, float Gap)? ClothAgainst(ModelParts garment, string bodyPath)
    {
        var cloth = ClothProbes(garment);
        if (cloth.Count == 0 || Load(bodyPath) is not { } body) return null;
        return ClothOn(cloth, body);
    }

    /// <summary>
    /// Whether the garment's cloth clears the body at <paramref name="bodyPath"/> as an author fits it: no more than
    /// <see cref="ClearsBelow"/> of the cloth near it inside it. True when too little cloth is near to say — nothing then
    /// sits inside it either.
    /// </summary>
    internal static bool ClothClears(ModelParts garment, string bodyPath)
        => ClothAgainst(garment, bodyPath) is not { } on || on.Near < MinEvidence || on.Inside <= on.Near * ClearsBelow;

    /// <summary>Points of the garment at which two bodies must part (by more than <see cref="Discriminates"/>) before
    /// they are two shapes where it is: a few stray ones are a seam vertex nudged between exports, not a size.</summary>
    internal const int DifferAtLeast = 10;

    /// <summary>
    /// Whether the bodies at <paramref name="pathA"/> and <paramref name="pathB"/> are two SHAPES where the garment is:
    /// at <see cref="DifferAtLeast"/> or more of its points, within <see cref="ClothReach"/> of either, their surfaces
    /// lie more than <see cref="Discriminates"/> apart. False when either cannot be read.
    /// <para/>
    /// Asked of the bodies themselves, not of how the garment scores against each: a score is a mean, and two sizes
    /// can score within <see cref="SameAnswer"/> of each other while one is fuller where the garment reaches. YAB's
    /// Watermelon and Skull Crushers legs are one mesh and differ at the thighs; a thigh-high's cloth gap, read only
    /// over the cloth outside each body, came out the same for both, and the refit up a size was refused as already
    /// fitting.
    /// </summary>
    internal static bool ShapesDiffer(ModelParts garment, string pathA, string pathB)
    {
        if (Load(pathA) is not { } a || Load(pathB) is not { } b) return false;
        if (a.ContentKey == b.ContentKey) return false;

        // Only what the garment still draws: a limb view (RefitCore.LimbView) keeps the hand's or foot's vertices,
        // numbered as before, and drops only their triangles.
        int vc = garment.Positions.Length / 3;
        var seen = new HashSet<(int, int, int)>();
        var all = new List<Vector3>();
        foreach (var part in garment.Parts)
        {
            if (part.Island >= 0) continue;
            foreach (int v in part.Triangles)
            {
                if (v < 0 || v >= vc) continue;
                var p = new Vector3(garment.Positions[v * 3], garment.Positions[v * 3 + 1],
                                    garment.Positions[v * 3 + 2]);
                if (seen.Add(MeshMath.PositionKey(BodyRetarget.ToVec(p), BodyRetarget.SnapPerMetre))) all.Add(p);
            }
        }
        var probes = Thin(all, MaxProbes);

        var da = DistancesTo(a, probes);
        var db = DistancesTo(b, probes);
        int differ = 0;
        for (int i = 0; i < probes.Count; i++)
        {
            if (MathF.Min(da[i], db[i]) >= ClothReach) continue;
            if (MathF.Abs(da[i] - db[i]) > Discriminates && ++differ >= DifferAtLeast) return true;
        }
        return false;

        static float[] DistancesTo(Candidate body, List<Vector3> probes)
        {
            var d = new float[probes.Count];
            // See Rank: the cached body's surface reuses a scratch buffer between queries.
            lock (body)
            {
                for (int i = 0; i < probes.Count; i++)
                    d[i] = body.Surface.Nearest(probes[i], ClothReach, out var hit) ? hit.Distance : ClothReach;
            }
            return d;
        }
    }

    private static (int Near, int Inside, float Gap) ClothOn(List<Vector3> cloth, Candidate body)
    {
        int near = 0, inside = 0, outside = 0;
        double gap = 0;
        // See Rank: the cached body's surface reuses a scratch buffer between queries.
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
        return (near, inside, outside > 0 ? (float)(gap / outside) : ClothReach);
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

    /// <summary>The garment's cloth points, welded and thinned the same way as <see cref="SkinPoints"/>.</summary>
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

        return Thin(all, MaxProbes);
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
    internal static List<Vector3> SkinPoints(ModelParts garment)
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

        return Thin(all, MaxProbes);
    }

    /// <summary>
    /// At most <paramref name="max"/> of <paramref name="all"/>: every k-th, not a random sample, so the answer does not
    /// change between two runs on the same files. The list itself when it is short enough.
    /// </summary>
    private static List<Vector3> Thin(List<Vector3> all, int max)
    {
        if (all.Count <= max) return all;
        int stride = (all.Count + max - 1) / max;
        var thinned = new List<Vector3>(max);
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

    // ── which body FAMILY: the cheap reading ────────────────────────────────

    /// <summary>
    /// The garment's body-skin points as packed position keys, sorted — the input to <see cref="Shared"/>. Empty when
    /// the garment has no body mesh.
    /// </summary>
    internal static long[] ProbeKeys(ModelParts garment) => ProbeKeys(SkinPoints(garment));

    /// <summary>
    /// <see cref="Shared"/>, read off the body as <see cref="Rank"/> indexes it rather than off its bare keys. Costs a
    /// little more per body than <see cref="Shared"/>, and is for a mod that is about to be ranked anyway: the body is
    /// read once, and the ranking that follows finds it cached instead of reading every size a second time.
    /// </summary>
    internal static float SharedIndexed(long[] probeKeys, string path)
    {
        if (probeKeys.Length == 0 || Load(path) is not { } body) return 0f;
        int hits = 0;
        foreach (long k in probeKeys)
            if (body.Snap.Contains(Unpack(k))) hits++;
        return (float)hits / probeKeys.Length;
    }

    /// <summary>
    /// The share of a garment's body-skin points lying EXACTLY on a vertex of the body at <paramref name="path"/>.
    /// <para/>
    /// A different question from <see cref="Rank"/>'s, and far cheaper. Rank tells sizes of ONE body apart, which
    /// needs every size indexed for distance queries — several megabytes each, and Neolithe has 114 chests. This tells
    /// body FAMILIES apart: an author copies the body mesh into the garment, and every size of a body shares most of
    /// its mesh (arms, back, shoulders), so any one size of the family the garment was cut from shares a large part of
    /// its points exactly, while another body mod — another mesh altogether — shares next to none. Only a sorted array
    /// of the body's vertex keys is kept, so a few sizes of every installed body cost little.
    /// </summary>
    /// <param name="content">Identifies the body's skin geometry, so two copies of one body (a mod and its fork)
    /// can be recognised as one.</param>
    internal static float Shared(long[] probeKeys, string path, out string? content)
    {
        content = null;
        if (probeKeys.Length == 0 || LoadKeys(path) is not { } body) return 0f;
        content = body.Content;
        int hits = 0;
        foreach (long k in probeKeys)
            if (Array.BinarySearch(body.Keys, k) >= 0) hits++;
        return (float)hits / probeKeys.Length;
    }

    // ── which body FAMILY when nothing matches exactly ──────────────────────

    /// <summary>How many of the garment's skin points the distance reading measures.</summary>
    private const int NearProbes = 600;

    /// <summary>Further than this from every body vertex, a point counts as this far: an arm the body lacks must not
    /// outweigh the whole of the rest.</summary>
    internal const float NearCap = 0.02f;

    /// <inheritdoc cref="ProbeKeys(ModelParts)"/>
    /// <param name="skinPoints">The garment's <see cref="SkinPoints"/>, taken once and handed to both readings.</param>
    internal static long[] ProbeKeys(List<Vector3> skinPoints)
    {
        var keys = skinPoints.Select(p => Pack(MeshMath.PositionKey(BodyRetarget.ToVec(p), BodyRetarget.SnapPerMetre)))
                             .ToArray();
        Array.Sort(keys);
        return keys;
    }

    /// <summary>
    /// Of the garment's <see cref="SkinPoints"/>, the ones the distance reading measures, thinned as they were (every
    /// k-th, so the answer is the same each run).
    /// </summary>
    internal static Vector3[] NearProbePoints(List<Vector3> skinPoints) => Thin(skinPoints, NearProbes).ToArray();

    /// <summary>
    /// How far, in metres, each of the garment's skin points lies from the skin SURFACE of the body at
    /// <paramref name="path"/>, capped at <see cref="NearCap"/>, in the order of <paramref name="probes"/>. Null when the
    /// body cannot be read. A reading compares bodies by the 90th percentile of these (<see cref="NearP90"/>).
    /// <para/>
    /// For a garment <see cref="Shared"/> cannot place: one made on an earlier release of its body mod, re-meshed since,
    /// shares next to nothing exactly with any installed body — and a sliver of shared vertices with an ancestor body
    /// then outscored it (a 2023 YAB top shared 1.2% with YAB+ 4.2 and 12.2% with Bibo+). Its skin still lies on its own
    /// body's surface. The surface, not the nearest vertex: to a vertex every body measured the same 3.4 mm, which is
    /// only how far apart vertices are. The 90th percentile, not the median: most of a chest body (back, arms,
    /// shoulders) is the same in every body descended from one another, and the median of that top came out 0.24 to
    /// 0.6 mm on all of them. Where they differ — the bust — is the tail: 0.8 mm on an old YAB S, 2.8 on YAB+ 4.2, 4.3
    /// on Bibo+.
    /// <para/>
    /// The body is read again for this — its triangles are not kept with <see cref="Shared"/>'s keys, which every
    /// detection reads and most never need more of — and indexed once per body (<see cref="NearIndexes"/>). Not through
    /// <see cref="BodySurface"/>, which <see cref="Rank"/> uses: that carries every vertex's normal and a per-query buffer
    /// that makes it unsafe to share between threads, several megabytes a body, and a weak reading measures a hundred.
    /// This holds only the skin's positions and triangles, and is read-only once built.
    /// </summary>
    /// <param name="file">Which file the body is (its bytes' hash), or null when it could not be read: two mods shipping
    /// the same file are measured once.</param>
    /// <param name="body">What the body's skin is (its geometry), or null: two mods whose sizes are the same skin are one
    /// body, whatever else their files differ by.</param>
    /// <param name="cap">How far a point counts at most. <see cref="NearCap"/> tells body families apart; how far one size
    /// of a body lies from another's (the size set's reading) needs the bust's whole offset, several centimetres.</param>
    internal static float[]? Near(Vector3[] probes, string path, out string? file, out string? body, float cap = NearCap)
    {
        file = body = null;
        if (probes.Length == 0 || NearIndexOf(path, out file, out body) is not { Cells.Length: > 0 } index) return null;
        var distances = new float[probes.Length];
        // Rings searched at most: every triangle within the cap, a small one's centre up to Reach further.
        int rings = (int)MathF.Ceiling((cap + index.Reach) / NearCellSize) + 1;
        for (int i = 0; i < probes.Length; i++)
        {
            var q = probes[i];
            var (cx, cy, cz) = NearCell(q);
            float best = cap * cap;
            // Ring by ring outward. A small triangle whose nearest point lies within d of this point has its centre
            // within d + Reach, so in a cell no more than that many cells' width away; a large one is filed in the cell
            // holding that nearest point. Once the best is nearer than the rings searched, less Reach, no ring further
            // out can hold a nearer one. Skin lies a millimetre or two from the garment's, so most points stop early.
            for (int r = 0; r <= rings; r++)
            {
                // Only the ring's shell: the cells r away along some axis, never the cube inside it again.
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        bool onFace = Math.Abs(dx) == r || Math.Abs(dy) == r;
                        for (int dz = -r; dz <= r; dz += onFace || r == 0 ? 1 : 2 * r)
                        {
                            long key = Pack((cx + dx, cy + dy, cz + dz));
                            int at = Array.BinarySearch(index.Cells, key);
                            if (at < 0) continue;
                            while (at > 0 && index.Cells[at - 1] == key) at--;
                            for (; at < index.Cells.Length && index.Cells[at] == key; at++)
                            {
                                int t = index.Order[at] * 3;
                                var on = BrushTransfer.ClosestOnTriangle(q, index.Points[index.Triangles[t]],
                                                                         index.Points[index.Triangles[t + 1]],
                                                                         index.Points[index.Triangles[t + 2]],
                                                                         out _, out _, out _);
                                best = MathF.Min(best, Vector3.DistanceSquared(on, q));
                            }
                        }
                    }
                float covered = r * NearCellSize - index.Reach;
                if (covered > 0f && best <= covered * covered) break;
            }
            distances[i] = MathF.Sqrt(best);
        }
        return distances;
    }

    /// <summary>The nearness index's cell: most points find their skin within a few millimetres.</summary>
    private const float NearCellSize = 0.01f;

    /// <summary>
    /// A body's skin triangles in a cell index sorted by cell: a point's neighbourhood is a few binary searches per ring
    /// of cells (see <see cref="Near"/>). A small triangle — reaching no further than a cell from its centre — is filed
    /// ONCE, under its centre's cell; filing every triangle under every cell it touched came to 17 entries a triangle and
    /// 2.3 MB a body. A larger one is filed under every cell it touches instead, so one coarse triangle does not widen
    /// every point's search to its size. Empty — no skin to measure — is a reading too, and kept as one.
    /// </summary>
    /// <param name="Cells">The index's cells, ascending; <paramref name="Order"/> the triangle filed under each.</param>
    /// <param name="Reach">How far a small triangle's corners lie from its centre, at most.</param>
    private sealed record NearIndex(Vector3[] Points, int[] Triangles, long[] Cells, int[] Order, float Reach)
    {
        public long Bytes => Points.Length * 12L + Triangles.Length * 4L + Cells.Length * 8L + Order.Length * 4L;
    }

    /// <summary>The nearness indexes held, their size in bytes, and what makes it up — for measuring.</summary>
    internal static (int Bodies, long Bytes, long Points, long Triangles, long Entries) NearCacheSize()
    {
        var built = NearIndexes.Values.Where(e => e.Value.IsValueCreated).Select(e => e.Value.Value).OfType<NearIndex>().ToList();
        return (built.Count, built.Sum(i => i.Bytes), built.Sum(i => (long)i.Points.Length),
                built.Sum(i => (long)i.Triangles.Length / 3), built.Sum(i => (long)i.Cells.Length));
    }

    /// <summary>
    /// Per body FILE (its bytes' hash — two forks ship the same file, and a re-triangulated copy is another file), its
    /// nearness index: built only when a weak reading asks — once, however many threads ask at the same moment — and the
    /// most recently used kept, so the next garment read that way does not build its family's again. A build that failed
    /// is not kept: a file Penumbra was rewriting reads again next time. Trimmed as readings start and end
    /// (<see cref="BeginReading"/>).
    /// </summary>
    private static readonly ConcurrentDictionary<string, Stamped<Lazy<NearIndex?>>> NearIndexes = new(StringComparer.Ordinal);

    /// <summary>
    /// Bodies' nearness indexes kept: about one weak reading's worth (three sizes of every installed body mod, twelve of a
    /// few), so the next garment read that way finds its bodies indexed rather than parsing them all again. Skin
    /// positions, triangles and the cell index measured 570 KB a body (90 bodies, 50 MB), so this stays near 55 MB.
    /// </summary>
    private const int MaxNearBodies = 96;

    /// <summary>The most bodies one reading has measured: the cache holds at least that many, so a large install's next
    /// weak garment still finds its family indexed (see <see cref="NeedNear"/>).</summary>
    private static int nearDemand;

    /// <summary>The most the nearness cache grows to fit one reading: about 90 MB.</summary>
    private const int MaxNearDemand = 160;

    /// <summary>
    /// A reading measured <paramref name="bodies"/> bodies: the nearness cache keeps room for that many — up to
    /// <see cref="MaxNearDemand"/>, so one reading on a very large install does not hold its hundreds for the session.
    /// </summary>
    internal static void NeedNear(int bodies)
    {
        bodies = Math.Min(bodies, MaxNearDemand);
        int seen;
        while (bodies > (seen = Volatile.Read(ref nearDemand)) && Interlocked.CompareExchange(ref nearDemand, bodies, seen) != seen)
        {
        }
    }

    /// <summary>
    /// The body's nearness index, and which file it is. A body the exact reading has not read yet is parsed once for both
    /// its keys and its index; one whose keys are cached is parsed again, as its triangles were not kept (see
    /// <see cref="Near"/>). A file that cannot be read or parsed is unreadable — null — never an exception, and is not
    /// remembered as such: Penumbra may have been halfway through writing it.
    /// </summary>
    /// <param name="body">What the body's skin IS — its geometry (<see cref="Shared"/>'s content) — or null.</param>
    private static NearIndex? NearIndexOf(string path, out string? file, out string? body)
    {
        file = body = null;
        try
        {
            // Read once here when not read yet, hashed before anything is parsed: a fork's copy, or a body whose keys were
            // trimmed, is often still known by its hash, and then nothing is parsed at all. Parsed at most once, for its
            // keys and its index together, and only by whichever of the two caches misses.
            if (LoadKeysParsing(path, out var parse, out var stamp) is not { } keys) return null;
            file = keys.File;
            body = keys.Content;
            var entry = NearIndexes.GetOrAdd(keys.File, _ => new Stamped<Lazy<NearIndex?>>(new Lazy<NearIndex?>(() =>
            {
                try
                {
                    // Keys cached from an earlier read: read the file again, and build only if it is still the file those
                    // keys came from — read unchanged at the stamp they were filed under. A rewrite since would put
                    // another mesh under this file's name.
                    var model = parse?.Value;
                    if (parse == null && ReadVerified(path) is { } again && again.Stamp == stamp)
                        model = ModelPartReader.Read(again.Bytes);
                    return model == null ? null : BuildNearIndex(model);
                }
                catch
                {
                    return null;
                }
            })));
            entry.Touch();
            var index = entry.Value.Value;
            // Not read (rather than read and found to have no skin, which is kept): Penumbra may have been rewriting it,
            // so it is read again next time.
            if (index == null) NearIndexes.TryRemove(new KeyValuePair<string, Stamped<Lazy<NearIndex?>>>(keys.File, entry));
            return index;
        }
        catch
        {
            return null;
        }
    }

    private static (long Length, long Ticks)? Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : null;
    }

    /// <summary>
    /// The file's bytes and the stamp they were read at, the stamp taken on both sides of the read. Rewritten while it was
    /// read, the bytes are not the file the stamp names, and it is read afresh — a few times at most: a file rewritten
    /// without pause is unreadable for now. Null when it cannot be read.
    /// </summary>
    private static ((long Length, long Ticks) Stamp, byte[] Bytes)? ReadVerified(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Stamp(path) is not { } stamp) return null;
            var bytes = File.ReadAllBytes(path);
            if (Stamp(path) == stamp) return (stamp, bytes);
        }
        return null;
    }

    private static readonly object TrimGate = new();

    /// <summary>When each running reading began, on the caches' clock (see <see cref="Clock"/>).</summary>
    private static readonly List<long> Running = [];

    /// <summary>
    /// Mark a detection running until the result is disposed, and trim both caches — at its start and at its end — of
    /// their least recently used bodies once over their caps. Never of one used since the oldest running reading began:
    /// that one may still be reading it, and would read it from disk again. Readings that overlap without a gap still
    /// trim at each start and end, so a long run of them holds the caches near their caps; while several run at once,
    /// what they are reading may hold them above.
    /// </summary>
    internal static IDisposable BeginReading()
    {
        var scope = new ReadingScope(Clock.Next());
        lock (TrimGate)
        {
            Running.Add(scope.Started);
            TrimLocked();
        }
        return scope;
    }

    private static void TrimLocked()
    {
        long spare = Running.Count == 0 ? long.MaxValue : Running.Min();
        TrimKeys(spare);
        TrimOldest(NearIndexes, Math.Max(MaxNearBodies, Volatile.Read(ref nearDemand)), spare);
    }

    private sealed class ReadingScope(long started) : IDisposable
    {
        private int disposed;
        public long Started { get; } = started;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1) return;
            lock (TrimGate)
            {
                Running.Remove(Started);
                TrimLocked();
            }
        }
    }

    private static NearIndex? BuildNearIndex(ModelParts model)
    {
        try
        {
            int vc = model.Positions.Length / 3;
            // Only the skin's vertices are kept, renumbered: a body model also carries undies, nails and piercings.
            var skinIndex = new Dictionary<int, int>();
            var skin = new List<Vector3>();
            int Skin(int v)
            {
                if (!skinIndex.TryGetValue(v, out int i))
                {
                    skinIndex[v] = i = skin.Count;
                    skin.Add(new Vector3(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]));
                }
                return i;
            }

            // A body's own triangles are millimetres across, a centimetre or two where the mesh is coarse. A small one
            // (reaching no further than a cell from its centre) is filed once, under its centre's cell; a larger one under
            // every cell it touches. One reaching further than MaxReach is a stray vertex's sliver — one at the origin is
            // a metre long, and would be filed under a million cells — and is left out.
            const float MaxReach = 0.03f;
            var tris = new List<int>();
            var entries = new List<(long Cell, int Triangle)>();
            float reach = 0f;
            foreach (var part in model.Parts)
            {
                if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= vc || b >= vc || c >= vc) continue;
                    var pa = At(a);
                    var pb = At(b);
                    var pc = At(c);
                    var centre = (pa + pb + pc) / 3f;
                    float r = MathF.Sqrt(MathF.Max(Vector3.DistanceSquared(centre, pa),
                                                   MathF.Max(Vector3.DistanceSquared(centre, pb),
                                                             Vector3.DistanceSquared(centre, pc))));
                    // Written so a NaN corner (a malformed export) is left out too: every comparison with NaN is false.
                    if (!(r <= MaxReach)) continue;
                    int index = tris.Count / 3;
                    tris.Add(Skin(a));
                    tris.Add(Skin(b));
                    tris.Add(Skin(c));
                    if (r <= NearCellSize)
                    {
                        reach = MathF.Max(reach, r);
                        entries.Add((Pack(NearCell(centre)), index));
                        continue;
                    }
                    var lo = NearCell(Vector3.Min(pa, Vector3.Min(pb, pc)));
                    var hi = NearCell(Vector3.Max(pa, Vector3.Max(pb, pc)));
                    for (int x = lo.X; x <= hi.X; x++)
                        for (int y = lo.Y; y <= hi.Y; y++)
                            for (int z = lo.Z; z <= hi.Z; z++)
                                entries.Add((Pack((x, y, z)), index));
                }
            }

            entries.Sort((x, y) => x.Cell.CompareTo(y.Cell));
            return new NearIndex(skin.ToArray(), tris.ToArray(), entries.Select(e => e.Cell).ToArray(),
                                 entries.Select(e => e.Triangle).ToArray(), reach);

            Vector3 At(int v) => new(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);
        }
        catch
        {
            return null;
        }
    }

    private static (int X, int Y, int Z) NearCell(Vector3 p)
        => ((int)MathF.Floor(p.X / NearCellSize), (int)MathF.Floor(p.Y / NearCellSize), (int)MathF.Floor(p.Z / NearCellSize));

    /// <summary>
    /// The 90th percentile of <paramref name="distances"/> (see <see cref="Near"/>) over the points
    /// <paramref name="use"/> marks; <see cref="NearCap"/> when it marks none.
    /// </summary>
    internal static float NearP90(float[] distances, bool[] use)
    {
        var kept = distances.Where((_, i) => use[i]).ToArray();
        if (kept.Length == 0) return NearCap;
        Array.Sort(kept);
        // Nearest rank: the smallest distance at least 90% of the points are within. Ten points give the ninth, not the
        // tenth, so one stray point is never the answer.
        return kept[(int)MathF.Ceiling(kept.Length * 0.9f) - 1];
    }

    /// <summary>Which FILE the body at <paramref name="path"/> is — its bytes' hash — and what its skin is, when its keys
    /// are cached already; nulls otherwise, without reading it.</summary>
    internal static (string File, string Body)? CachedFile(string path)
        => TryCached(path, out var keys) && keys != null ? (keys.File, keys.Content) : null;

    /// <summary>
    /// Let the least recently used cached keys go once more than <see cref="MaxKeyBodies"/> bodies are held, sparing any
    /// used since <paramref name="spare"/> (see <see cref="BeginReading"/>).
    /// </summary>
    private static void TrimKeys(long spare)
    {
        if (!TrimOldest(KeyCache, MaxKeyBodies, spare)) return;
        // The content index only saves parsing a fork's copy of a file already read: rebuilt from what is still held. A
        // file found unreadable stays known as such — a few bytes — so it is never parsed again under another name; up to
        // a few hundred of them, past which they are let go too.
        const int MaxUnreadable = 256;
        var held = KeyCache.Values.Select(e => e.Value).Where(k => k != null).ToHashSet();
        bool tooManyUnreadable = KeysByContent.Count(kv => kv.Value == null) > MaxUnreadable;
        foreach (var (content, keys) in KeysByContent)
            if (keys == null ? tooManyUnreadable : !held.Contains(keys))
                KeysByContent.TryRemove(content, out _);
    }

    /// <summary>
    /// Drop the least recently used entries of <paramref name="cache"/> down to three quarters of <paramref name="max"/>
    /// once it holds more than that — none used since <paramref name="spare"/>. True when anything went.
    /// </summary>
    private static bool TrimOldest<TKey, T>(ConcurrentDictionary<TKey, Stamped<T>> cache, int max, long spare)
        where TKey : notnull
    {
        if (cache.Count <= max) return false;
        var old = cache.Where(e => e.Value.Used < spare).OrderBy(e => e.Value.Used)
                       .Take(cache.Count - max * 3 / 4).Select(e => e.Key).ToList();
        foreach (var key in old) cache.TryRemove(key, out _);
        return old.Count > 0;
    }

    /// <summary>The caches' clock: a counter rather than the time, as only the order matters. One for all of them, so a
    /// reading's start can be compared with when any entry was last used.</summary>
    private static class Clock
    {
        private static long now;
        public static long Next() => Interlocked.Increment(ref now);
    }

    /// <summary>A cached value and when it was last used (<see cref="Clock"/>).</summary>
    private sealed class Stamped<T>(T value)
    {
        private long used = Clock.Next();
        public T Value { get; } = value;
        public long Used => Interlocked.Read(ref used);
        public void Touch() => Interlocked.Exchange(ref used, Clock.Next());
    }

    /// <param name="Keys">The skin's vertex keys, sorted and distinct.</param>
    /// <param name="Content">The keys' hash: what the skin's geometry is.</param>
    /// <param name="File">The file's bytes' hash: which file it is.</param>
    private sealed record SkinKeys(long[] Keys, string Content, string File);

    /// <summary>
    /// Per body file, its skin vertex keys: a hundred-odd KB a body. Kept for the session up to
    /// <see cref="MaxKeyBodies"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<(string Path, long Length, long Ticks), Stamped<SkinKeys?>> KeyCache = new();

    /// <summary>
    /// Bodies' keys kept (<see cref="TrimKeys"/>). One detection reads about a hundred — three sizes of every installed
    /// body mod, twelve of a few — so this holds several detections' worth, near 50 MB at most.
    /// </summary>
    private const int MaxKeyBodies = 400;

    /// <summary>
    /// The same, by the file's CONTENT: a body mod and its fork ship the very same files under other names, and parsing
    /// a model is what costs — hashing its bytes is a small fraction of that.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SkinKeys?> KeysByContent = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the body's keys are cached already — <paramref name="keys"/> null when it was read and found unreadable —
    /// without reading the file.
    /// </summary>
    private static bool TryCached(string path, out SkinKeys? keys) => TryCached(path, out keys, out _);

    /// <param name="stamp">The stamp the keys are filed under — the file as it is now.</param>
    private static bool TryCached(string path, out SkinKeys? keys, out (long Length, long Ticks) stamp)
    {
        keys = null;
        stamp = default;
        try
        {
            if (Stamp(path) is not { } now || !KeyCache.TryGetValue((path, now.Length, now.Ticks), out var entry))
                return false;
            entry.Touch();
            keys = entry.Value;
            stamp = now;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static SkinKeys? LoadKeys(string path) => LoadKeysParsing(path, out _, out _);

    /// <summary>
    /// The body's keys, from the cache or read for them: the one way both readings fill the cache. A file not cached is
    /// read with its stamp verified around the read (<see cref="ReadVerified"/>) and filed under that stamp; it is hashed
    /// first, and parsed only when its hash is not known already.
    /// </summary>
    /// <param name="parse">The file just read, parsed on first use — so a caller needing the model too (the nearness
    /// index) parses the file once — or null when the keys came from the cache and nothing was read.</param>
    /// <param name="stamp">The stamp the keys are filed under: a later read at the same stamp is the same file.</param>
    private static SkinKeys? LoadKeysParsing(string path, out Lazy<ModelParts?>? parse, out (long Length, long Ticks) stamp)
    {
        parse = null;
        stamp = default;
        try
        {
            if (TryCached(path, out var cached, out stamp)) return cached;
            if (ReadVerified(path) is not { } read) return null;
            var bytes = read.Bytes;
            stamp = read.Stamp;
            parse = new Lazy<ModelParts?>(() => ModelPartReader.Read(bytes));
            return LoadKeys(path, read.Stamp, Convert.ToHexString(SHA256.HashData(bytes)), parse);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// File keys for bytes read at <paramref name="stamp"/> — under that stamp, not one taken afterwards, which would file
    /// these bytes under a rewrite's name.
    /// </summary>
    /// <param name="file">The bytes' hash.</param>
    /// <param name="parse">The bytes parsed, on first use: never at all when the file's keys are known by its hash.</param>
    private static SkinKeys? LoadKeys(string path, (long Length, long Ticks) stamp, string file, Lazy<ModelParts?> parse)
    {
        try
        {
            var entry = KeyCache.GetOrAdd((path, stamp.Length, stamp.Ticks),
                                          _ => new Stamped<SkinKeys?>(KeysByContent.GetOrAdd(file, _ => ReadKeys(() => parse.Value, file))));
            entry.Touch();
            // Read outside any detection too (the Studio's Body size tool): trimmed here once well past the cap, sparing
            // whatever a running reading uses, so the cap holds when no detection runs for a long while.
            if (KeyCache.Count > MaxKeyBodies * 2)
                lock (TrimGate)
                    TrimLocked();
            return entry.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <param name="parse">Parses the file — inside the catch, so a malformed body is cached as unreadable rather than
    /// parsed again on every call.</param>
    private static SkinKeys? ReadKeys(Func<ModelParts?> parse, string file)
    {
        try
        {
            if (parse() is not { } model) return null;
            int vc = model.Positions.Length / 3;
            var keys = new HashSet<long>();
            foreach (var part in model.Parts)
            {
                if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
                foreach (int v in part.Triangles)
                {
                    if (v < 0 || v >= vc) continue;
                    var p = new Vector3(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);
                    keys.Add(Pack(MeshMath.PositionKey(BodyRetarget.ToVec(p), BodyRetarget.SnapPerMetre)));
                }
            }
            var sorted = keys.ToArray();
            Array.Sort(sorted);
            string content = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(sorted.AsSpan())));
            return new SkinKeys(sorted, content, file);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A position key in one long: 21 bits an axis, which at 0.1 mm covers ±100 m.</summary>
    private static long Pack((int X, int Y, int Z) k)
        => ((long)(k.X & 0x1FFFFF) << 42) | ((long)(k.Y & 0x1FFFFF) << 21) | (long)(k.Z & 0x1FFFFF);

    /// <summary>The key <see cref="Pack"/> packed, sign restored.</summary>
    private static (int X, int Y, int Z) Unpack(long k)
    {
        static int Axis(long v) => (int)((v & 0x1FFFFF) << 43 >> 43);
        return (Axis(k >> 42), Axis(k >> 21), Axis(k));
    }

    /// <summary>
    /// Forget every cached body once more than <paramref name="max"/> are held.
    /// <para/>
    /// An indexed body is several megabytes, and the automatic refit (<see cref="AutoRefitWatcher"/>) ranks the sizes of
    /// whichever body mod each garment was made on, so over a long session the cache could hold several packs. Once it
    /// holds more than <paramref name="max"/>, it is let go. All or nothing: the next ranking reads what it needs again.
    /// </summary>
    internal static void ForgetCandidatesBeyond(int max)
    {
        if (Cache.Count > max) Cache.Clear();
    }

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
