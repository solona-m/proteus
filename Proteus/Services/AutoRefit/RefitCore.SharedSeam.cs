using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;

namespace Proteus.Services;

/// <summary>
/// Which seam a garment split at the waist shares with the other piece, and whether the refit may hold it where the author
/// put it (see <see cref="BodyRetarget.PinSharedSeam"/>).
/// <para/>
/// One answer for both pieces, whatever order they are refitted in and whatever sizes are chosen: worked out from the mod's
/// files and the bodies refitted onto alone, never from the piece being refitted. The seam top is the first of the
/// author's chest files, in the mod's order, whose open edge meets a legs file's (a split, not a shared surface), read as
/// made on the chest's target body mod from its own skin. Its split places must fit that body: outside every chest and
/// legs option of it, and the same distance from every chest (the chests share their belly). A piece then holds its side of
/// the split only when it meets at least <see cref="BodyRetarget.SeamCover"/> of it — which is what tells one mod's
/// several waists apart.
/// </summary>
internal static partial class RefitCore
{
    /// <summary>More author files than this in one slot are not read at all (32): Sarelle has 16 tops and 7 legs.</summary>
    internal const int MostSeamFiles = 32;

    /// <summary>The most a split place's distance to the target chests may differ between them (1 mm).</summary>
    internal const float BellyAgree = 1e-3f;

    /// <summary>How far from a split place a body's skin is looked for (30 mm); beyond, that body does not reach it.</summary>
    private const float SeamFitReach = 0.03f;

    /// <summary>One of the author's chest or legs files, as the mod serves it.</summary>
    internal sealed record SeamFile(string GamePath, string File, string Source);

    /// <summary>The mod's author files on either side of a waist split, each slot in the mod's order.</summary>
    internal sealed record SeamFiles(IReadOnlyList<SeamFile> Tops, IReadOnlyList<SeamFile> Legs);

    /// <summary>The seam top: the chest file whose split the refit holds, read as made on <see cref="BodyDir"/>.</summary>
    internal sealed record SeamTop(string File, string BodyDir, BodyRetarget.SeamPoints Points);

    /// <summary>The gate's answer for a mod and the bodies refitted onto: a seam top, or why there is none.</summary>
    /// <param name="NoSplit">The mod's chest and legs files share no split at all — most outfit mods.</param>
    /// <param name="Transient">The answer rests on a file that could not be read now (locked, say): not kept, so the next
    /// refit asks again.</param>
    internal sealed record SeamGate(SeamTop? Top, string? Refusal, bool NoSplit = false, bool Transient = false);

    /// <summary>Where gate answers are kept: across runs for the automatic refit, for one run in the size set.</summary>
    internal sealed class SeamCache
    {
        private readonly ConcurrentDictionary<string, SeamGate> gates = new(StringComparer.Ordinal);
        public int Count => gates.Count;
        public SeamGate GetOrAdd(string key, Func<SeamGate> make)
        {
            if (gates.TryGetValue(key, out var known)) return known;
            var made = make();
            return made.Transient ? made : gates.GetOrAdd(key, made);
        }
        public bool TryGet(string key, out SeamGate gate) => gates.TryGetValue(key, out gate!);
    }

    /// <summary>The automatic refit's gate answers, kept while the plugin runs (see <see cref="SeamCacheKey"/>).</summary>
    internal static readonly SeamCache CrossRunSeams = new();

    /// <summary>
    /// The author's <c>_top</c> and <c>_dwn</c> files of <paramref name="race"/>, distinct by file, in the mod's order.
    /// Proteus's own refits are not the author's and never count, as <see cref="SiblingPiece.Find"/> leaves them out: every
    /// option the record lists (those inside the author's own groups too), and every file under the refit folder. Null
    /// when either slot has none, or more than <see cref="MostSeamFiles"/> (noted).
    /// </summary>
    internal static SeamFiles? SeamFilesOf(IReadOnlyList<PenumbraModMeta.Redirect> redirects,
                                           BodyRetargetWriter.Record? record, string race, Action<string>? note = null)
    {
        var ours = new HashSet<string>(record?.Options.Select(e => record.GroupOf(e) + " / " + e.Name) ?? [],
                                       StringComparer.OrdinalIgnoreCase);
        string retarget = Normal(BodyRetargetWriter.Subfolder) + "/";
        List<SeamFile> Of(string limb)
        {
            var path = new Regex($@"^chara/equipment/e\d{{4}}/model/c{Regex.Escape(race)}e\d{{4}}{Regex.Escape(limb)}\.mdl$",
                                 RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var found = new List<SeamFile>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in redirects)
            {
                if (ours.Contains(d.Source) || !path.IsMatch(d.GamePath)) continue;
                string file = Normal(d.File);
                if (file.StartsWith(retarget, StringComparison.Ordinal) || !seen.Add(file)) continue;
                found.Add(new SeamFile(d.GamePath, d.File, d.Source));
            }
            return found;
        }
        var tops = Of("_top");
        var legs = Of("_dwn");
        if (tops.Count == 0 || legs.Count == 0) return null;
        if (tops.Count > MostSeamFiles || legs.Count > MostSeamFiles)
        {
            note?.Invoke($"shared seam: {tops.Count} chest and {legs.Count} legs files — more than {MostSeamFiles}, none read");
            return null;
        }
        return new SeamFiles(tops, legs);
    }

    /// <summary>A hash of the candidate list in order — each file's option and path — so an option added in the mod's
    /// groups is seen and Proteus's own refits (left out of the list) change nothing.</summary>
    internal static string CandidateListHash(SeamFiles files)
    {
        var text = new StringBuilder();
        foreach (var f in files.Tops) text.Append("t|").Append(f.Source).Append('|').Append(Normal(f.File)).Append('\n');
        foreach (var f in files.Legs) text.Append("l|").Append(f.Source).Append('|').Append(Normal(f.File)).Append('\n');
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Every candidate file's length and time written (a stat, nothing read), so an edited file is read again.</summary>
    internal static string CandidateStamps(string modRoot, SeamFiles files)
    {
        var text = new StringBuilder();
        foreach (var f in files.Tops.Concat(files.Legs))
        {
            try
            {
                var info = new FileInfo(BodySizeCatalog.ResolveCaseInsensitive(modRoot, f.File));
                text.Append(info.Exists ? $"{info.Length}@{info.LastWriteTimeUtc.Ticks}" : "missing").Append(';');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                text.Append("unreadable;");
            }
        }
        return text.ToString();
    }

    /// <summary>The automatic refit's cache key for a gate answer: the mod, its candidate list and their files, the bodies
    /// refitted onto, the race, and the installed bodies (<see cref="DetectionCache.BodiesOf"/>).</summary>
    internal static string SeamCacheKey(string modRoot, SeamFiles files, string chestDir, string legsDir, string race,
                                        string bodies)
        => string.Join("|", Normal(modRoot), CandidateListHash(files), CandidateStamps(modRoot, files),
                       chestDir.ToLowerInvariant(), legsDir.ToLowerInvariant(), race, bodies);

    /// <summary>Whether a reading is one the seam top may rest on: from the garment's own skin (not its cloth), sure enough
    /// to refit by, and never "no body mesh" (which reads as whatever body was preferred).</summary>
    internal static bool IsSkinReading(AutoRefitDecisions.Pick? pick)
        => pick != null && !pick.FromCloth && pick.Confidence != Confidence.NoBodyMesh
           && AutoRefitDecisions.Proceed(pick.Confidence);

    /// <summary>
    /// The gate: the seam top among <paramref name="files"/>' chest files and whether its split fits the bodies refitted
    /// onto. Files are opened and read lazily, in the mod's order, and reading stops at the seam top.
    /// </summary>
    /// <param name="read">Reads a chest file's body (preferring <paramref name="chestDir"/> on a tie); null for none.</param>
    /// <param name="bellyCheck">Whether the target chests must agree at the split (see <see cref="BellyAgree"/>).</param>
    /// <param name="chestName">The chest's body as the player knows it: options named for it are read first (see
    /// <see cref="SeamCandidateOrder"/>). Null keeps the mod's order.</param>
    internal static SeamGate ResolveSeamGate(string modRoot, SeamFiles files, string race, string chestDir,
                                             BodySizeCatalog? chestCat, string legsDir, BodySizeCatalog? legsCat,
                                             Func<SeamFile, byte[], ModelParts, IReadOnlySet<string>, AutoRefitDecisions.Pick?> read,
                                             Action<string>? note = null, bool bellyCheck = true, string? chestName = null)
    {
        if (chestCat == null || legsCat == null)
            return new SeamGate(null, "the target body's chest or legs sizes could not be read");

        bool unread = false;
        var legs = new List<(ModelParts, XivLiveMesh.SkinnedMesh)>();
        foreach (var f in files.Legs)
            if (OpenSeamFile(modRoot, f.File, note, out bool locked) is { } opened) legs.Add((opened.Model, opened.Skin));
            else unread |= locked;
        if (legs.Count == 0) return new SeamGate(null, "no legs file could be read", Transient: unread);

        bool anySplit = false;
        int readings = 0;
        foreach (var f in SeamCandidateOrder(files.Tops, chestName ?? chestDir))
        {
            if (OpenSeamFile(modRoot, f.File, note, out bool locked) is not { } top)
            {
                unread |= locked;
                continue;
            }
            if (BodyRetarget.SplitPoints(top.Model, top.Skin, legs) is not { } points) continue;
            anySplit = true;
            if (readings++ >= SeamReadingCap)
                return new SeamGate(null, $"no chest piece sharing the seam is made on {chestDir} among the first "
                                          + $"{SeamReadingCap} read");
            var pick = read(f, top.Bytes, top.Model, top.Bones);
            if (!IsSkinReading(pick))
            {
                note?.Invoke($"shared seam: {f.File} shares a split but is not read clearly from its skin");
                continue;
            }
            if (!string.Equals(pick!.Dir, chestDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (BodyFit(points, chestCat, legsCat, race, bellyCheck) is { } misfit)
                return new SeamGate(null, $"{f.File}'s split {misfit}");
            return new SeamGate(new SeamTop(f.File, pick.Dir, points), null);
        }
        return new SeamGate(null, anySplit ? $"no chest piece sharing the seam is made on {chestDir}"
                                           : "its chest and legs files share no split",
                            NoSplit: !anySplit && !unread, Transient: unread);
    }

    /// <summary>
    /// The most chest files the gate reads (8). A first reading samples every installed body; Sarelle onto a body none of
    /// its tops was made on read all 16 in 21 s cold before it could say so — so options named for the body come first
    /// (<see cref="SeamCandidateOrder"/>), and past this many the answer is no.
    /// </summary>
    internal const int SeamReadingCap = 8;

    /// <summary>
    /// The chest files in the order the gate reads them: those whose option names a word of <paramref name="body"/>
    /// ("Tre" in "Top Size / Tre M") first, then the rest, each in the mod's order. The same order in every refit of either
    /// piece: it depends on the files and the body alone.
    /// </summary>
    internal static IEnumerable<SeamFile> SeamCandidateOrder(IReadOnlyList<SeamFile> tops, string body)
    {
        var words = Regex.Matches(body.ToLowerInvariant(), "[a-z]{3,}")
                         .Select(m => m.Value)
                         .Where(w => w is not ("yas" or "body" or "all" or "one" or "the"))
                         .ToHashSet(StringComparer.Ordinal);
        bool Named(SeamFile f)
            => Regex.Matches(f.Source.ToLowerInvariant(), "[a-z]{3,}").Any(m => words.Contains(m.Value));
        return tops.Where(Named).Concat(tops.Where(f => !Named(f)));
    }

    /// <summary>
    /// Why the split does not fit the target body, or null when it does: a place inside a chest or legs option's skin, or
    /// the chests' distances there apart by more than <see cref="BellyAgree"/> (when <paramref name="bellyCheck"/>).
    /// </summary>
    internal static string? BodyFit(BodyRetarget.SeamPoints points, BodySizeCatalog chestCat, BodySizeCatalog legsCat,
                                    string race, bool bellyCheck)
    {
        var lo = new float[points.Count];
        var hi = new float[points.Count];
        Array.Fill(lo, float.PositiveInfinity);
        Array.Fill(hi, float.NegativeInfinity);
        foreach (var (cat, slot) in new[] { (chestCat, "_top"), (legsCat, "_dwn") })
            foreach (var option in cat.For(slot, race))
            {
                ModelParts? body;
                try
                {
                    body = ModelPartReader.Read(File.ReadAllBytes(cat.PathOf(option)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    body = null;
                }
                if (body == null) continue;
                var surface = new BodySurface(body, BodySurface.CellFor(BodyRetarget.MeanEdgeOf(body)));
                if (surface.IsEmpty) continue;
                for (int k = 0; k < points.Count; k++)
                {
                    if (!surface.Nearest(points.At[k], SeamFitReach, out var hit)) continue;
                    float off = Vector3.Dot(points.At[k] - hit.Point, hit.Normal);
                    if (off < -BodyRetarget.UnfoldBuryTolerance)
                        return $"lies inside \"{option.Label}\" by {-off * 1000f:0.0} mm";
                    if (slot != "_top") continue;
                    lo[k] = MathF.Min(lo[k], hit.Distance);
                    hi[k] = MathF.Max(hi[k], hit.Distance);
                }
            }
        if (!bellyCheck) return null;
        float spread = 0f;
        for (int k = 0; k < points.Count; k++)
            if (hi[k] >= lo[k]) spread = MathF.Max(spread, hi[k] - lo[k]);
        return spread > BellyAgree ? $"is {spread * 1000f:0.0} mm nearer one chest than another" : null;
    }

    /// <summary>A candidate file's model, rig and skinning, or null (noted) when it cannot be read.</summary>
    /// <param name="locked">The file is there but could not be read now (locked, refused): an answer resting on it is not
    /// kept. A file missing from the mod, or not a model, stays so until its stamp changes, which the cache key carries.</param>
    private static (byte[] Bytes, ModelParts Model, XivLiveMesh.SkinnedMesh Skin, IReadOnlySet<string> Bones)? OpenSeamFile(
        string modRoot, string file, Action<string>? note, out bool locked)
    {
        locked = false;
        try
        {
            string path = BodySizeCatalog.ResolveCaseInsensitive(modRoot, file);
            if (File.Exists(path))
                using (File.OpenRead(path)) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            locked = true;
            note?.Invoke($"{file} could not be read now: {e.Message}");
            return null;
        }
        if (SiblingPiece.TryOpen(modRoot, file, note) is not { } opened) return null;
        if (ModelSkinReader.Read(opened.Bytes, null, null) is not { } skin || skin.VertexCount * 3 != opened.Model.Positions.Length)
        {
            note?.Invoke($"{file}'s skinning could not be read");
            return null;
        }
        return (opened.Bytes, opened.Model, skin, opened.Bones);
    }

    /// <summary>
    /// A garment's side of the gate's seam: its vertices on the seam top's split and the key its record compares, or why
    /// it holds none (no seam top, or it meets too little of the split — another waist of the same mod).
    /// </summary>
    internal static (BodyRetarget.SharedSeam? Seam, string? Key, string Why) SeamForGarment(
        SeamGate gate, ModelParts garment, byte[] bytes, string chestDir, string legsDir)
    {
        if (gate.Top is not { } top) return (null, null, gate.Refusal ?? "no seam top");
        if (ModelSkinReader.Read(bytes, null, null) is not { } skin) return (null, null, "its skinning could not be read");
        var (vertices, positions, coverage) = BodyRetarget.SeamVertices(garment, skin, top.Points);
        if (coverage < BodyRetarget.SeamCover)
            return (null, null, $"it meets {positions} of {top.Points.Count} places of {top.File}'s split ({coverage:P0}), "
                                + "another waist");
        string label = $"{top.File} on {top.BodyDir}";
        return (new BodyRetarget.SharedSeam(vertices, positions, label), SeamKey(chestDir, legsDir, top.File), "");
    }

    /// <summary>What a refit's record keeps of its seam: the bodies and the seam top.</summary>
    internal static string SeamKey(string chestDir, string legsDir, string seamTop)
        => chestDir + "|" + legsDir + "|" + Normal(seamTop);

    /// <summary>The record's value: the key when the hold stood, marked when the fallback let it go, null for none.</summary>
    internal static string? RecordedSeam(string? key, bool kept) => key == null ? null : kept ? key : key + "|dropped";

    /// <summary>Whether a recorded seam is the seam the refit would hold now (a dropped one is the same seam).</summary>
    internal static bool SameSeamKey(string? recorded, string? now)
    {
        string? was = recorded != null && recorded.EndsWith("|dropped", StringComparison.Ordinal)
            ? recorded[..^"|dropped".Length]
            : recorded;
        return string.Equals(was, now, StringComparison.OrdinalIgnoreCase);
    }
}
