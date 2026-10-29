using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Proteus.Localization;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;

namespace Proteus.Services;

/// <summary>
/// Which of the author's sizes each new size is cut from: the one whose body lies nearest the new size's body. Cut from
/// the author's first size instead, a bulk Rue Large came from YAB Small — the garment's cups carried 57–62 mm where
/// from YAB Large they move 19–22 — and the skin the author hid under the cups showed past their rims as holes.
/// </summary>
internal static partial class SizeSetRefit
{
    /// <summary>How far apart two candidates' 90th percentiles may be and still tie: the step below then decides.</summary>
    internal const float TieMm = 0.5f;

    /// <summary>
    /// How far a point counts at most when one size's body is measured against another's. The 20 mm the body-family
    /// reading caps at (<see cref="BodySizeMatch.NearCap"/>) would read YAB Small, Medium and Large all alike against Rue
    /// Large; their busts lie 57, 33 and 19 mm off it.
    /// </summary>
    internal const float SourceCap = 0.08f;

    /// <summary>How many of the author's sizes are read besides the first readable one; the rest are left unread.</summary>
    internal const int MostCandidates = 24;

    /// <summary>
    /// How much further, in millimetres, one of the author's sizes' skin may lie from the body it was read as made on than
    /// the best-fitting size's does, and still be a source. Luxe's "Uranus", read as Lithe Almond Large on a weak share of
    /// its skin, lies far off that body while its sisters sit on theirs; cut from it, Rue M came out a Rue L.
    /// </summary>
    internal const float MisfitMm = 12f;

    /// <summary>
    /// How far one of the author's sizes lies from a new size, in metres, by the keys <see cref="Choose{T}"/> takes in
    /// turn. Body: between the body the size was made on and the new size's, both ways (one way reads a bust that slid
    /// along the other's surface as near). Skin: the garment's own skin to the new body, all its points. Own skin: the
    /// garment's skin to the body it was read as made on (<see cref="OwnSkinFit"/>); null when unknown.
    /// </summary>
    /// <param name="ByMadeOn">Read on the body chosen under "Made on" (<see cref="Request.MadeOn"/>), not by the
    /// automatic reading: judged for misfits among those read so alone.</param>
    /// <param name="MisfitMm">How much further than the best of its kind its own skin may lie (<see cref="Misfits{T}"/>).</param>
    internal readonly record struct SourceDistance(float BodyP90, float BodyMean, float SkinP90, float SkinMean, int Order,
                                                   float? OwnSkinP90 = null, bool ByMadeOn = false,
                                                   float MisfitMm = SizeSetRefit.MisfitMm);

    /// <summary>
    /// The 90th percentile (nearest rank) of a reading; <see cref="SourceCap"/> when there is none to take — a body
    /// that could not be read, or no skin to read it by — never <see cref="BodySizeMatch.NearP90"/>'s 20 mm.
    /// </summary>
    internal static float P90(float[]? distances, float cap = SourceCap)
    {
        if (distances is not { Length: > 0 }) return cap;
        var sorted = (float[])distances.Clone();
        Array.Sort(sorted);
        int rank = (int)MathF.Ceiling(0.9f * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    /// <summary>The mean of a reading; <see cref="SourceCap"/> when there is none.</summary>
    internal static float Mean(float[]? distances, float cap = SourceCap)
        => distances is { Length: > 0 } ? distances.Average() : cap;

    /// <summary>
    /// How far the body at <paramref name="sourcePath"/> lies from the one at <paramref name="targetPath"/>, both ways:
    /// the larger of the two 90th percentiles, and of the two means.
    /// </summary>
    /// <param name="probesOf">A body's probe points, read once per body by the caller.</param>
    internal static (float P90, float Mean) BodyDistance(string sourcePath, string targetPath,
                                                         Func<string, Vector3[]> probesOf)
    {
        var there = BodySizeMatch.Near(probesOf(sourcePath), targetPath, out _, out _, SourceCap);
        var back = BodySizeMatch.Near(probesOf(targetPath), sourcePath, out _, out _, SourceCap);
        return (MathF.Max(P90(there), P90(back)), MathF.Max(Mean(there), Mean(back)));
    }

    /// <summary>How far a garment's own skin points lie from the body at <paramref name="targetPath"/>.</summary>
    internal static (float P90, float Mean) SkinDistance(Vector3[] skinProbes, string targetPath)
    {
        var d = skinProbes.Length == 0 ? null : BodySizeMatch.Near(skinProbes, targetPath, out _, out _, SourceCap);
        return (P90(d), Mean(d));
    }

    /// <summary>
    /// How far a garment's own skin points lie from the body it was read as made on, at the 90th percentile; null when
    /// there is nothing to tell by — no skin, or a body that could not be read — never <see cref="SourceCap"/>, so a size
    /// is not passed over for what could not be measured.
    /// </summary>
    internal static float? OwnSkinFit(Vector3[] skinProbes, string sourcePath)
        => skinProbes.Length > 0 && BodySizeMatch.Near(skinProbes, sourcePath, out _, out _, SourceCap) is { Length: > 0 } d
            ? P90(d)
            : null;

    /// <summary>
    /// A body's probe points: <see cref="BodySizeMatch.NearProbePoints"/> of its skin. Empty when unreadable — every
    /// distance to it then counts as <see cref="SourceCap"/> — said through <paramref name="note"/>.
    /// </summary>
    internal static Vector3[] BodyProbes(string path, Action<string>? note = null)
    {
        try
        {
            if (System.IO.File.Exists(path) && ModelPartReader.Read(System.IO.File.ReadAllBytes(path)) is { } body)
                return BodySizeMatch.NearProbePoints(BodySizeMatch.SkinPoints(body));
            note?.Invoke($"body {System.IO.Path.GetFileName(path)} could not be read; sizes measured against it count as "
                         + $"{SourceCap * 1000f:F0} mm off");
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException
                                       or ArgumentException or IndexOutOfRangeException)
        {
            note?.Invoke($"body {System.IO.Path.GetFileName(path)} could not be read ({e.GetType().Name}); sizes measured "
                         + $"against it count as {SourceCap * 1000f:F0} mm off");
        }
        return [];
    }

    /// <summary>
    /// The model a piece goes onto for one of the table's sizes, when cut from a size made on <paramref name="sourceRel"/>.
    /// Its own slot's size; legs following a chest onto the legs that size's chest is worn with — the table's across
    /// body mods, the source's own family's within one. Null when there are none: Neolithe XS keeps the Small hips.
    /// </summary>
    internal static string? TargetRel(bool followsChest, bool across, SupportedBodies.Size size, string sourceRel)
        => !followsChest ? size.Rel
         : across ? size.LegsRel
         : SupportedBodies.FamilyLegs(sourceRel, size.Name);

    /// <summary>
    /// The nearest of <paramref name="items"/>. First, the sizes whose own skin lies more than <see cref="MisfitMm"/>
    /// further from the body they were read as than the best-fitting one's (<see cref="Misfits{T}"/>) are passed over:
    /// read as the wrong body, their body distance says nothing. Then at each key in turn — body 90th percentile, body
    /// mean, skin 90th percentile, skin mean, then the author's order — the nearest is found and only those within reach of
    /// it go on, <see cref="TieMm"/> on a percentile and nothing on a mean. A filter at each step, never a sort: "within
    /// half a millimetre" does not chain, and a comparer built on it is inconsistent.
    /// </summary>
    /// <returns>The one chosen, the key that decided it, and by how much it beat the nearest of the rest there (metres;
    /// 0 when the author's order decided). "skin fit" only when passing the misfits over changed the choice; its gap is
    /// how much better the chosen one's own skin sits than the nearest misfit's.</returns>
    internal static (T Chosen, string Step, float Gap) Choose<T>(IReadOnlyList<(T Item, SourceDistance D)> items)
    {
        var (chosen, step, gap, _) = ChooseWithMisfits(items);
        return (chosen, step, gap);
    }

    /// <summary>
    /// <see cref="Choose{T}"/>, with the misfits it passed over: what the log says of each size, so the log and the choice
    /// are one reading.
    /// </summary>
    internal static (T Chosen, string Step, float Gap, HashSet<int> Misfits) ChooseWithMisfits<T>(
        IReadOnlyList<(T Item, SourceDistance D)> items)
    {
        if (items.Count == 0) throw new ArgumentException("nothing to choose from", nameof(items));
        var all = Enumerable.Range(0, items.Count).ToList();
        var asBefore = ChooseAmong(items, all);
        var misfits = Misfits(items);
        if (misfits.Count == 0) return Result(asBefore);
        var kept = ChooseAmong(items, all.Where(i => !misfits.Contains(i)).ToList());
        if (kept.Index == asBefore.Index) return Result(asBefore);
        // One whose own fit is unknown is kept, never judged: chosen, it is said by the key that chose it.
        if (items[kept.Index].D.OwnSkinP90 == null) return Result(kept);
        float gap = misfits.Min(i => items[i].D.OwnSkinP90!.Value) - items[kept.Index].D.OwnSkinP90!.Value;
        return (items[kept.Index].Item, "skin fit", gap, misfits);

        (T, string, float, HashSet<int>) Result((int Index, string Step, float Gap) r)
            => (items[r.Index].Item, r.Step, r.Gap, misfits);
    }

    /// <summary>
    /// The indices of <paramref name="items"/> whose own skin lies more than their <see cref="SourceDistance.MisfitMm"/>
    /// further from the body they were read as than the best-fitting one's of their kind. Measured against the best, never
    /// chained; one whose fit is unknown is neither judged nor the best; none of a kind with fewer than two known.
    /// <para/>
    /// Of their kind: a size read on the body chosen under "Made on" is judged among those alone. They sit a few
    /// millimetres off a body they are an older release of, where a size read exactly sits on its own — measured against
    /// that, every one of them would be passed over.
    /// </summary>
    internal static HashSet<int> Misfits<T>(IReadOnlyList<(T Item, SourceDistance D)> items)
    {
        var misfits = new HashSet<int>();
        foreach (bool byMadeOn in new[] { false, true })
        {
            var known = Enumerable.Range(0, items.Count)
                                  .Where(i => items[i].D.ByMadeOn == byMadeOn && items[i].D.OwnSkinP90 != null).ToList();
            if (known.Count < 2) continue;
            float best = known.Min(i => items[i].D.OwnSkinP90!.Value);
            misfits.UnionWith(known.Where(i => items[i].D.OwnSkinP90!.Value > best + items[i].D.MisfitMm / 1000f));
        }
        return misfits;
    }

    /// <summary>
    /// What the log says when the size chosen is the only one of <paramref name="items"/> read on the "Made on" body: no
    /// other of its kind to judge it against, so it had no misfit check. Null otherwise.
    /// </summary>
    internal static string? LoneMadeOn<T>(IReadOnlyList<(T Item, SourceDistance D)> items, T chosen)
    {
        var madeOn = items.Where(i => i.D.ByMadeOn).ToList();
        return madeOn.Count == 1 && EqualityComparer<T>.Default.Equals(madeOn[0].Item, chosen)
            ? "the only size read on the Made on body: no misfit check"
            : null;
    }

    /// <summary>
    /// Whether an option read as the same size as an earlier one is kept beside it: when the earlier one's own skin lies
    /// more than <paramref name="misfitMm"/> (the earlier one's threshold) further from that body than the later one's —
    /// the earlier is passed over, and the later one is the size it should have been.
    /// </summary>
    internal static bool KeepsBoth(float? earlier, float? later, float misfitMm = MisfitMm)
        => earlier is { } e && later is { } l && e > l + misfitMm / 1000f;

    private static (int Index, string Step, float Gap) ChooseAmong<T>(IReadOnlyList<(T Item, SourceDistance D)> items,
                                                                     List<int> left)
    {
        var steps = new (string Name, Func<SourceDistance, float> Key, float Tie)[]
        {
            ("body", d => d.BodyP90, TieMm / 1000f),
            ("body mean", d => d.BodyMean, 0f),
            ("skin", d => d.SkinP90, TieMm / 1000f),
            ("skin mean", d => d.SkinMean, 0f),
        };
        foreach (var (name, key, tie) in steps)
        {
            float min = left.Min(i => key(items[i].D));
            var near = left.Where(i => key(items[i].D) <= min + tie).ToList();
            if (near.Count == 1)
            {
                int won = near[0];
                float gap = left.Count > 1 ? left.Where(i => i != won).Min(i => key(items[i].D)) - key(items[won].D) : 0f;
                return (won, name, gap);
            }
            left = near;
        }
        return (left.MinBy(i => items[i].D.Order), "author order", 0f);
    }

    // ── "Made on": the body the author says the mod was made on, for the sizes the automatic reading cannot place ──
    //
    // Oblique, made on Bibo+ before Dawntrail, shares 1.2% of its skin points exactly with today's Bibo+ and at most 4.7%
    // with any body (YAS Tre): below the 5% a body family needs, the automatic reading places none of its sizes, and
    // rightly guesses nothing. Told the body, each size is ranked among that body's own sizes, as the Studio ranks them.

    /// <summary>
    /// How far, in metres, the best of an Ambiguous ranking on the "Made on" body may lie and still be taken: a tie
    /// between shapes of one size, not between sizes. Oblique's top Medium tied Bibo+'s Nude and Perky Medium at 2.67 and
    /// 2.70 mm, its Large Perky and Nude Large at 2.68 and 2.77, its legs' Medium Tummy Type C, B and A Medium at
    /// 0.77–0.81; its top Medium Tummy, a size apart (Nude X-Large 5.96, Large 6.04), is refused.
    /// </summary>
    internal const float MadeOnTieRms = 0.004f;

    /// <summary>
    /// How far, in metres, a size's skin may lie off the size of the "Made on" body it was read as (the 90th percentile
    /// over all its skin points, <see cref="OwnSkinFit"/>) and still be read on it: a coarse guard against a plainly wrong
    /// body, not a measure of which size. Oblique's plain sizes lie 2.7–3.1 mm off Bibo+'s, their legs 0.5; its XLarge
    /// Tummy, 12.5 mm off, is refused. It catches no body of a woman's shape: Oblique on Neolithe lies 2.7–4.2 mm off
    /// (8 of 10 tops taken), on Rosaline 5.8–9.1 (3 tops; its legs are all unclear). The author's choice is taken as said.
    /// </summary>
    internal const float MadeOnMaxOff = 0.010f;

    /// <summary>
    /// <see cref="MisfitMm"/> for a chest size read on the "Made on" body, judged among those alone: tight enough that a
    /// "Tummy" variant, read there as a larger plain size, is passed over beside the plain sizes. Oblique's plain tops lie
    /// 2.7–3.1 mm off the Bibo+ sizes they read as, its Tummy ones 5.3–6.4 mm (Medium Tummy unclear, XLarge Tummy 12.5 mm
    /// off): 1.5 mm over the best keeps every plain size by 1.1 mm and passes every Tummy one over by 1.1 mm. At 12 mm,
    /// Bibo M, Rue M and L, YAB M and L were all cut from a Tummy size.
    /// </summary>
    internal const float MadeOnMisfitTopMm = 1.5f;

    /// <summary>
    /// <see cref="MadeOnMisfitTopMm"/> for legs. Oblique's Tummy legs read as the very Type C sizes its plain ones do
    /// (0.6 mm off against 0.5) and are dropped as the same size, so no threshold tells them apart, and none needs to; the
    /// chest's serves.
    /// </summary>
    internal const float MadeOnMisfitLegsMm = MadeOnMisfitTopMm;

    /// <summary>The misfit threshold of a size read on the "Made on" body, by its piece's slot.</summary>
    internal static float MadeOnMisfitMm(string slot) => slot == "_dwn" ? MadeOnMisfitLegsMm : MadeOnMisfitTopMm;

    /// <summary>What reading a size on the "Made on" body came to.</summary>
    internal enum MadeOnFit
    {
        /// <summary>Read as one of its sizes.</summary>
        Taken,
        /// <summary>The body has no size of the piece's slot and race.</summary>
        NoSizes,
        /// <summary>Too little of the garment's skin to tell by.</summary>
        NoEvidence,
        /// <summary>Its sizes could not be told apart: an Ambiguous ranking past <see cref="MadeOnTieRms"/>, or one read
        /// off the cloth.</summary>
        Unclear,
        /// <summary>Its skin lies further than <see cref="MadeOnMaxOff"/> off the size it was read as.</summary>
        TooFar,
    }

    /// <summary>
    /// Whether a ranking of one of the author's sizes among the "Made on" body's sizes is taken, and if not, why.
    /// </summary>
    /// <param name="sizes">How many sizes of the piece's slot and race the body has.</param>
    /// <param name="ownFit">The size's skin against the body's best size (<see cref="OwnSkinFit"/>); null when unknown.</param>
    internal static MadeOnFit JudgeMadeOn(int sizes, BodySizeMatch.Ranking ranking, float? ownFit)
    {
        if (sizes == 0) return MadeOnFit.NoSizes;
        if (ranking.Best is not { } best || ranking.Confidence is Confidence.NoBodyMesh or Confidence.TooLittle)
            return MadeOnFit.NoEvidence;
        if (ranking.Confidence == Confidence.Ambiguous && (ranking.FromCloth || best.Rms > MadeOnTieRms))
            return MadeOnFit.Unclear;
        if (ownFit > MadeOnMaxOff) return MadeOnFit.TooFar;
        return MadeOnFit.Taken;
    }

    /// <summary>
    /// What one of the author's files was read as when the automatic reading could not place it: on the "Made on" body
    /// (<see cref="ReadByMadeOn"/>), with that reading's verdict. Kept for the run like an automatic reading, so a file
    /// read twice is answered alike.
    /// </summary>
    /// <param name="Pick">The size it was read as; null when the reading was not taken.</param>
    /// <param name="Fit">The verdict; null when the "Made on" body is not installed.</param>
    internal sealed record SourceReading(AutoRefitDecisions.Pick? Pick, bool ReadByMadeOn, MadeOnFit? Fit);

    /// <summary>
    /// Whether a chest or legs size read automatically is said under "Check in game" once a size is cut from it: with
    /// "Made on" set, read below Exact on another body mod than the one chosen. It stays read so — "Made on" only places
    /// what the automatic reading cannot — but the author said otherwise. Never for shoes and gloves, always read
    /// automatically.
    /// </summary>
    internal static bool WarnsOtherBody(string? madeOn, string slot, SourceReading reading)
        => madeOn != null && !IsEnd(slot) && !reading.ReadByMadeOn && reading.Pick is { } pick
           && pick.Confidence != Confidence.Exact
           && !string.Equals(pick.Dir, madeOn, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The note for a chest or legs piece none of whose sizes could be read: without "Made on", that the body could not be
    /// told and may be chosen; with it, why it could not be read on that body.
    /// </summary>
    /// <param name="madeOnName">What the "Made on" body is called; its folder when not installed.</param>
    /// <param name="missing">The "Made on" body is not installed.</param>
    /// <param name="fits">The verdicts of the sizes read on it.</param>
    internal static (string Note, string Code) UnreadableNote(string piece, string? madeOn, string? madeOnName, bool missing,
                                                              IReadOnlyCollection<MadeOnFit> fits)
    {
        var s = Strings.SizeSet;
        if (madeOn == null) return (string.Format(s.UnreadableMadeOnFmt, piece), Codes.Unreadable);
        string name = madeOnName ?? madeOn;
        if (missing) return (string.Format(s.MadeOnMissingFmt, piece, name), Codes.NotMadeOn);
        if (fits.Count > 0 && fits.All(f => f == MadeOnFit.Unclear))
            return (string.Format(s.MadeOnUnclearFmt, piece, name), Codes.NotMadeOn);
        return (string.Format(s.NotMadeOnFmt, piece, name), Codes.NotMadeOn);
    }

    /// <summary>
    /// One of the author's sizes ranked among the "Made on" body's sizes of its slot and race, and judged
    /// (<see cref="JudgeMadeOn"/>). Every reading is logged, with the runner-up of a taken Ambiguous one.
    /// </summary>
    private static SourceReading ReadOn(ModelParts garment, IReadOnlySet<string> bones, string slot, string race,
                                        BodyModIndex.Entry body, Vector3[] skinProbes, string name, Action<string> note)
    {
        var options = body.Catalog.For(slot, race);
        if (options.Count == 0) return new SourceReading(null, true, MadeOnFit.NoSizes);
        var ranking = BodySizeMatch.Rank(garment, options, body.Catalog.PathOf, bones);
        float? own = ranking.Best is { } top ? OwnSkinFit(skinProbes, body.Catalog.PathOf(top.Option)) : null;
        var fit = JudgeMadeOn(options.Count, ranking, own);
        string said = ranking.Best is { } b
            ? $"{b.Option.Label} ({ranking.Confidence}{(ranking.FromCloth ? ", from the cloth" : "")}, rms {b.Rms * 1000f:F2} mm"
              + (own is { } o ? $", own body {o * 1000f:F1} mm" : "") + ")"
            : $"nothing ({ranking.Confidence})";
        if (fit == MadeOnFit.Taken && ranking.Confidence == Confidence.Ambiguous && ranking.Scores.Count > 1)
            said += $", runner-up {ranking.Scores[1].Option.Label} {ranking.Scores[1].Rms * 1000f:F2} mm";
        note($"{name} read on {body.Name}: {said}" + (fit == MadeOnFit.Taken ? "" : $"; not taken ({fit})"));
        return fit == MadeOnFit.Taken
            ? new SourceReading(new AutoRefitDecisions.Pick(body.Dir, ranking.Best!.Value.Option, ranking.Confidence,
                                                            ranking.FromCloth), true, fit)
            : new SourceReading(null, true, fit);
    }

    /// <summary>The key followers find their lead's choice by: an option is one size per race of its group.</summary>
    internal static string LeadKey(string group, string race, string option) => group + "|" + race + "|" + option;

    /// <summary>
    /// The file an option of <paramref name="group"/> gives <paramref name="gamePath"/>, from the mod's manifest — not
    /// from <see cref="Piece.Options"/>, which lists a file shared by several sizes once, under the first. Null when
    /// that option gives the path nothing.
    /// </summary>
    internal static string? ResolveOptionFile(IReadOnlyList<PenumbraModMeta.Redirect> redirects, string gamePath,
                                              string group, string option)
    {
        string source = group + " / " + option;
        return redirects.FirstOrDefault(r => string.Equals(r.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)
                                             && string.Equals(r.Source, source, StringComparison.OrdinalIgnoreCase)).File;
    }

    /// <summary>Why a candidate is no source for a size: its target is the very size it was made on.</summary>
    internal const string MadeOnIt = "the size it was made on";

    /// <summary>
    /// Why one of the author's sizes may not be refitted onto <paramref name="to"/>, or null when it may: no size to go
    /// onto; made on that very size; or made on another family of the target's own body mod (Neolithe's neobelly is not
    /// its almond, and a refit between them within the mod skips what a refit between bodies does).
    /// </summary>
    internal static string? Ineligible(string sourceDir, BodyOption source, bool family, string targetDir, BodyOption? to)
    {
        if (to == null) return "no size of its own to go onto";
        if (AutoRefitDecisions.SameFile(sourceDir, source, targetDir, to)) return MadeOnIt;
        if (!string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase)) return null;
        if (family && SupportedBodies.Template(source.Rel)?.Pattern != SupportedBodies.Template(to.Rel)?.Pattern)
            return "another family of its body";
        return null;
    }

    /// <summary>
    /// Of the sizes that may go onto a target, the ones rigged as the target body is — YAS or not — when there are any:
    /// a mod listing Neolithe's sizes (no YAS) before its YAB ones (YAS) still cuts Rue's YAS Large from YAB Large. None
    /// rigged alike, all of them, as the only source always was.
    /// </summary>
    internal static List<T> RiggedAlike<T>(IReadOnlyList<T> eligible, Func<T, bool> yas, bool targetYas)
    {
        var alike = eligible.Where(c => yas(c) == targetYas).ToList();
        return alike.Count > 0 ? alike : eligible.ToList();
    }

    /// <summary>One of the author's sizes of a piece, read: what a new size may be cut from.</summary>
    private sealed class Candidate(string? option, string file, byte[] bytes, ModelParts garment, HashSet<string> bones,
                                   int order)
    {
        public string? Option { get; } = option;
        public string File { get; } = file;
        public byte[] Bytes { get; } = bytes;
        public ModelParts Garment { get; } = garment;
        public HashSet<string> Bones { get; } = bones;
        public int Order { get; } = order;
        public bool Yas { get; } = SupportedBodies.IsYas(bones);

        /// <summary>What it was made on; null until read, or when it could not be.</summary>
        public AutoRefitDecisions.Pick? Pick { get; set; }
        public BodyModIndex.Entry Entry { get; set; } = null!;
        public string SourcePath { get; set; } = "";

        /// <summary>Read on the body chosen under "Made on", the automatic reading having placed it nowhere.</summary>
        public bool ReadByMadeOn { get; set; }

        /// <summary>How much further than the best of its kind its own skin may lie (<see cref="Misfits{T}"/>).</summary>
        public float MisfitMm { get; set; } = SizeSetRefit.MisfitMm;

        /// <summary>Read automatically, below Exact, on another body than the one chosen under "Made on"
        /// (<see cref="WarnsOtherBody"/>): a size cut from it is said under "Check in game".</summary>
        public bool OtherBody { get; set; }

        /// <summary>Its distances, with its kind and threshold.</summary>
        public SourceDistance Distance(float bodyP90, float bodyMean, float skinP90, float skinMean)
            => new(bodyP90, bodyMean, skinP90, skinMean, Order, OwnFit, ReadByMadeOn, MisfitMm);

        private Vector3[]? skinProbes;
        public Vector3[] SkinProbes => skinProbes ??= BodySizeMatch.NearProbePoints(BodySizeMatch.SkinPoints(Garment));

        private bool ownFitRead;
        private float? ownFit;
        /// <summary>How far its skin lies from the body it was read as made on (<see cref="OwnSkinFit"/>), read once.</summary>
        public float? OwnFit
        {
            get
            {
                if (!ownFitRead) (ownFit, ownFitRead) = (OwnSkinFit(SkinProbes, SourcePath), true);
                return ownFit;
            }
        }

        private List<IReadOnlyCollection<int>>? shape;
        private List<string> shapeNames = [];
        public List<IReadOnlyCollection<int>> Shape => shape ??= RefitCore.ShapePieces(Garment, RefitCore.DefaultKeepShape(Garment),
                                                                                       out shapeNames);
        public List<string> ShapeNames { get { _ = Shape; return shapeNames; } }

        // Read once, the first time a size cut from it asks (see RunPiece).
        public bool LegsRead, BeneathRead;
        public BodyOption? SourceLegs;
        public RefitCore.ChestBeneath? Beneath;

        public string Name => Option ?? File;
    }

    /// <summary>
    /// The author's sizes of one piece, read and placed: the first one whose body reads — the primary, which picks the
    /// bodies' YAS or plain sizes and the family's size list, as the only source did — and up to
    /// <see cref="MostCandidates"/> more. Another option is read when a follower asks for it by file.
    /// </summary>
    private sealed class Sources
    {
        private readonly string root, slot, race, preferred;
        private readonly Request request;
        private readonly IReadOnlyList<(string Dir, BodySizeCatalog Catalog)> mods;
        private readonly Dictionary<string, AutoRefitDecisions.Pick?> readings;
        private readonly Dictionary<string, SourceReading> madeOnReadings;
        private readonly BodyModIndex.Entry? madeOn;
        private readonly Action<string> note;
        private readonly Dictionary<string, Candidate?> byFile = new(StringComparer.Ordinal);

        /// <summary>The ones a size chooses among, in the author's order.</summary>
        public List<Candidate> Choices { get; } = [];
        public Candidate? Primary { get; }

        /// <summary>The verdicts of the sizes read on the "Made on" body: what a piece with none read is told.</summary>
        public List<MadeOnFit> Fits { get; } = [];

        /// <summary>A size needed "Made on", and the body chosen there is not installed.</summary>
        public bool MadeOnMissing { get; private set; }

        /// <summary>What the body chosen under "Made on" is called, or null.</summary>
        public string? MadeOnName => madeOn?.Name;

        /// <param name="madeOnReadings">What sizes were read as on the "Made on" body this run (see
        /// <see cref="SourceReading"/>), beside the automatic <paramref name="readings"/>.</param>
        public Sources(Request request, Piece piece, string preferred,
                       IReadOnlyList<(string Dir, BodySizeCatalog Catalog)> mods,
                       Dictionary<string, AutoRefitDecisions.Pick?> readings,
                       Dictionary<string, SourceReading> madeOnReadings, Action<string> note, CancellationToken ct)
        {
            this.request = request;
            root = request.ModRoot;
            slot = piece.Slot;
            race = piece.Race;
            this.preferred = preferred;
            this.mods = mods;
            this.readings = readings;
            this.madeOnReadings = madeOnReadings;
            madeOn = request.MadeOn is { } dir
                ? request.Bodies.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase))
                : null;
            this.note = note;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            int order = 0, extras = 0, unread = 0;
            // Every option until one reads, not the first few: a men's shirt lists "Type M", "TBSE-Q" and two
            // Thicclanders fits — none on an installed body — before its TBSE ones.
            foreach (var (option, file) in piece.Options)
            {
                ct.ThrowIfCancellationRequested();
                if (Primary != null && extras >= MostCandidates)
                {
                    unread++;
                    continue;
                }
                // Past the first readable one, every option tried counts, read or not: a mod of many unreadable fits
                // must not detect them all.
                if (Primary != null) extras++;
                var c = Read(option, file, order++);
                if (c == null) continue;
                if (Primary == null)
                {
                    Primary = c;
                    Choices.Add(c);
                    continue;
                }
                // Read as the very size another already was, rigged alike: it would only tie with it, and lose on the
                // author's order. Rigged otherwise, it is kept: a target rigged like it chooses among those alone. So is
                // one that sits on that body where the other does not (KeepsBoth): the other is passed over.
                if (Choices.FirstOrDefault(o => o.Yas == c.Yas
                                                && string.Equals(o.Pick!.Dir, c.Pick!.Dir, StringComparison.OrdinalIgnoreCase)
                                                && RefitCore.Normal(o.Pick.Option.Rel) == RefitCore.Normal(c.Pick.Option.Rel))
                        is { } same && !KeepsBoth(same.OwnFit, c.OwnFit, same.MisfitMm))
                {
                    note($"{c.Name} reads as the same size as another of the author's; not measured apart");
                    continue;
                }
                Choices.Add(c);
            }
            note($"{Choices.Count} of the author's {piece.Options.Count} sizes read as sources in {clock.ElapsedMilliseconds} ms"
                 + (unread > 0 ? $"; {unread} more left unread" : ""));
        }

        /// <summary>The option giving <paramref name="file"/>, read now if it was not: a follower's lead chose it.</summary>
        public Candidate? ByFile(string? option, string file)
            => byFile.TryGetValue(RefitCore.Normal(file), out var c) ? c : Read(option, file, int.MaxValue);

        private Candidate? Read(string? option, string file, int order)
        {
            string key = RefitCore.Normal(file);
            if (byFile.TryGetValue(key, out var known)) return known;
            Candidate? c = null;
            try
            {
                string path = BodySizeCatalog.ResolveCaseInsensitive(root, file);
                if (System.IO.File.Exists(path))
                {
                    byte[] bytes = System.IO.File.ReadAllBytes(path);
                    if (ModelPartReader.Read(bytes) is { } garment)
                    {
                        var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
                        c = new Candidate(option, file, bytes, garment, bones, order);
                        string reading = ReadingKey(file, slot, race, preferred);
                        if (readings.TryGetValue(reading, out var pick))
                            note($"{file} was read before in this run");
                        else
                            readings[reading] = pick = RefitCore.DetectSource(garment, bones, slot, race, mods, preferred,
                                                                              _ => true, note);
                        var read = new SourceReading(pick, false, null);
                        // Only what the automatic reading cannot place is read on the "Made on" body: a size it places
                        // is read the same whatever the author chose.
                        if (request.MadeOn != null && (pick == null || !AutoRefitDecisions.Proceed(pick.Confidence)))
                            read = ReadMadeOn(c, file);
                        if (read.Fit is { } fit) Fits.Add(fit);
                        if (read.Pick is not { } took || (!read.ReadByMadeOn && !AutoRefitDecisions.Proceed(took.Confidence)))
                            c = null;
                        else
                        {
                            c.Pick = took;
                            c.Entry = request.Bodies.First(b => string.Equals(b.Dir, took.Dir, StringComparison.OrdinalIgnoreCase));
                            c.SourcePath = c.Entry.Catalog.PathOf(took.Option);
                            c.ReadByMadeOn = read.ReadByMadeOn;
                            c.MisfitMm = read.ReadByMadeOn ? MadeOnMisfitMm(slot) : MisfitMm;
                            c.OtherBody = WarnsOtherBody(request.MadeOn, slot, read);
                            if (c.OtherBody)
                                note($"{c.Name}: read as made on {c.Entry.Name} ({took.Confidence}), not on the body chosen "
                                     + "under Made on; sized from it as read");
                        }
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                note($"{option ?? file} could not be read: {e.Message}");
                c = null;
            }
            return byFile[key] = c;
        }

        /// <summary>A file the automatic reading could not place, read on the "Made on" body — once a run.</summary>
        private SourceReading ReadMadeOn(Candidate c, string file)
        {
            string key = ReadingKey(file, slot, race, preferred, request.MadeOn);
            if (madeOnReadings.TryGetValue(key, out var known))
            {
                note($"{file} was read on the Made on body before in this run");
                if (known.Fit == null) MadeOnMissing = true;
                return known;
            }
            if (madeOn == null)
            {
                MadeOnMissing = true;
                note($"{c.Name}: \"{request.MadeOn}\", chosen under Made on, is not installed");
                return madeOnReadings[key] = new SourceReading(null, true, null);
            }
            return madeOnReadings[key] = ReadOn(c.Garment, c.Bones, slot, race, madeOn, c.SkinProbes, c.Name, note);
        }
    }

    /// <summary>How a candidate's distance reads in the log, in millimetres.</summary>
    internal static string Describe(SourceDistance d, bool passedOver = false)
        => $"body {d.BodyP90 * 1000f:F1} mm (mean {d.BodyMean * 1000f:F1}), skin {d.SkinP90 * 1000f:F1} mm (mean {d.SkinMean * 1000f:F1})"
           + (d.OwnSkinP90 is { } own ? $", own body {own * 1000f:F1} mm" : "")
           + (passedOver ? ", passed over: its skin is off the body it was read as" : "");
}
