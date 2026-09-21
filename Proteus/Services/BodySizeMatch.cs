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
/// Gear models carry a real copy of the body's skin mesh beside their cloth — that is how a top reshapes the chest it
/// exposes — so the garment usually holds the answer. Match its body mesh against each candidate and the right one
/// scores about 1.0 while every other size scores about 0, because a size change moves every vertex of the chest by
/// millimetres and an exact position match is all-or-nothing.
/// </summary>
internal static class BodySizeMatch
{
    /// <summary>At or above this share of exact hits, the guess is as good as certain.</summary>
    public const float Exact = 0.98f;

    /// <summary>Above this, the guess is worth preselecting.</summary>
    public const float Likely = 0.50f;

    /// <summary>Probe points taken from the garment. Enough to be decisive, few enough to keep 131 candidates cheap.</summary>
    private const int MaxProbes = 3000;

    /// <param name="HitRate">Share of the garment's body-mesh points sitting exactly on a vertex of this candidate.</param>
    /// <param name="Rms">Root-mean-square distance to this candidate's surface — the fallback when nothing snaps.</param>
    internal readonly record struct Score(BodyOption Option, float HitRate, float Rms);

    /// <summary>How sure the top score is, and what to say about it.</summary>
    internal enum Confidence
    {
        /// <summary>The garment has no body mesh, so there is nothing to match on.</summary>
        NoBodyMesh,
        Exact,
        Likely,
        Guess,
        Ambiguous,
    }

    internal sealed record Ranking(Confidence Confidence, IReadOnlyList<Score> Scores)
    {
        public Score? Best => Scores.Count > 0 ? Scores[0] : null;

        /// <summary>Whether the top score is worth selecting for the user rather than merely showing.</summary>
        public bool Preselect => Confidence is Confidence.Exact or Confidence.Likely;
    }

    /// <summary>
    /// Rank <paramref name="candidates"/> by how well they explain the garment's own body mesh.
    /// Candidates that cannot be read are skipped rather than scored zero, so a broken file does not win by default.
    /// </summary>
    public static Ranking Rank(ModelParts garment, IReadOnlyList<BodyOption> candidates, Func<BodyOption, string> pathOf)
    {
        var probes = Probes(garment);
        if (probes.Count == 0) return new Ranking(Confidence.NoBodyMesh, []);

        // One score per distinct BODY, not per option, and distinct means "different vertex positions". Neolithe
        // mirrors all 114 chest sizes onto the Emperor's New Robe, and those mirrors are neither the same path nor the
        // same bytes — same geometry, different undies material — so only a geometric key merges them. Left unmerged
        // they arrive as a pair of identical twins at the top of every ranking, which buries the genuine ties the
        // tie-break below exists to surface.
        var scores = new List<Score>();
        var seenContent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in candidates.GroupBy(o => o.Rel, StringComparer.OrdinalIgnoreCase))
        {
            var option = group.First();
            var body = Load(pathOf(option));
            if (body == null || !seenContent.Add(body.ContentKey)) continue;

            int hits = 0;
            double sum = 0;
            int measured = 0;

            // The candidate lives in a process-wide cache, and BodySurface reuses a scratch buffer between queries, so
            // two rankings touching the same body at once would corrupt each other's answers. Uncontended in the
            // normal case — the Studio runs one detection at a time.
            lock (body)
            {
                foreach (var p in probes)
                {
                    if (body.Snap.Contains(MeshMath.PositionKey(BodyRetarget.ToVec(p), BodyRetarget.SnapPerMetre)))
                        hits++;
                    if (!body.Surface.Nearest(p, BodyRetarget.NearBand, out var hit)) continue;
                    sum += hit.Distance * hit.Distance;
                    measured++;
                }
            }

            scores.Add(new Score(option, (float)hits / probes.Count,
                                 measured > 0 ? (float)Math.Sqrt(sum / measured) : float.MaxValue));
        }

        if (scores.Count == 0) return new Ranking(Confidence.Ambiguous, []);

        scores.Sort((x, y) => x.HitRate != y.HitRate ? y.HitRate.CompareTo(x.HitRate) : x.Rms.CompareTo(y.Rms));
        return new Ranking(Judge(scores), scores);
    }

    /// <summary>
    /// Two candidates this close are a tie, not a ranking.
    /// <para/>
    /// It happens for a real reason: a garment that only covers the chest carries no belly geometry, so Neolithe's
    /// Default and Neobelly sizes explain it equally well and differ only where the garment has nothing to say. Both
    /// answers are defensible and they give different results elsewhere, so the honest move is to show both rather
    /// than pick by sort order.
    /// </summary>
    private const float TieMargin = 0.005f;

    private static Confidence Judge(List<Score> scores)
    {
        var best = scores[0];
        bool tied = scores.Count > 1 && best.HitRate - scores[1].HitRate <= TieMargin;

        if (best.HitRate >= Likely) return tied ? Confidence.Ambiguous
                                    : best.HitRate >= Exact ? Confidence.Exact
                                    : Confidence.Likely;

        // Nothing snapped, so fall back to how much closer the surface fit is than the runner-up's. A clear winner is
        // a guess worth showing; a photo finish is not, and saying so is more useful than picking one.
        if (scores.Count == 1) return Confidence.Guess;
        float runnerUp = scores.Skip(1).Min(s => s.Rms);
        return best.Rms < runnerUp * 0.9f ? Confidence.Guess : Confidence.Ambiguous;
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
    private sealed record Candidate(BodySurface Surface, HashSet<(int, int, int)> Snap, string ContentKey);

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

                // Keyed on the VERTEX POSITIONS, not on the file's bytes. Neolithe's Emperor's New Robe mirror of a
                // size is the same body with a different undies material, so its bytes differ while its geometry does
                // not — and geometry is the only thing a retarget ever reads from a body.
                return new Candidate(surface, snap,
                                     Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes<float>(model.Positions))));
            });
        }
        catch
        {
            return null;
        }
    }
}
