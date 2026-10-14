using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

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
    /// How far one of the author's sizes lies from a new size, in metres, by the keys <see cref="Choose{T}"/> takes in
    /// turn. Body: between the body the size was made on and the new size's, both ways (one way reads a bust that slid
    /// along the other's surface as near). Skin: the garment's own skin to the new body, all its points.
    /// </summary>
    internal readonly record struct SourceDistance(float BodyP90, float BodyMean, float SkinP90, float SkinMean, int Order);

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
    /// The nearest of <paramref name="items"/>: at each key in turn — body 90th percentile, body mean, skin 90th
    /// percentile, skin mean, then the author's order — the nearest is found and only those within reach of it go on,
    /// <see cref="TieMm"/> on a percentile and nothing on a mean. A filter at each step, never a sort: "within half a
    /// millimetre" does not chain, and a comparer built on it is inconsistent.
    /// </summary>
    /// <returns>The one chosen, the key that decided it, and by how much it beat the nearest of the rest there (metres;
    /// 0 when the author's order decided).</returns>
    internal static (T Chosen, string Step, float Gap) Choose<T>(IReadOnlyList<(T Item, SourceDistance D)> items)
    {
        if (items.Count == 0) throw new ArgumentException("nothing to choose from", nameof(items));
        var left = Enumerable.Range(0, items.Count).ToList();
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
                return (items[won].Item, name, gap);
            }
            left = near;
        }
        int first = left.MinBy(i => items[i].D.Order);
        return (items[first].Item, "author order", 0f);
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

        private Vector3[]? skinProbes;
        public Vector3[] SkinProbes => skinProbes ??= BodySizeMatch.NearProbePoints(BodySizeMatch.SkinPoints(Garment));

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
        private readonly Action<string> note;
        private readonly Dictionary<string, Candidate?> byFile = new(StringComparer.Ordinal);

        /// <summary>The ones a size chooses among, in the author's order.</summary>
        public List<Candidate> Choices { get; } = [];
        public Candidate? Primary { get; }

        public Sources(Request request, Piece piece, string preferred,
                       IReadOnlyList<(string Dir, BodySizeCatalog Catalog)> mods,
                       Dictionary<string, AutoRefitDecisions.Pick?> readings, Action<string> note, CancellationToken ct)
        {
            this.request = request;
            root = request.ModRoot;
            slot = piece.Slot;
            race = piece.Race;
            this.preferred = preferred;
            this.mods = mods;
            this.readings = readings;
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
                // author's order. Rigged otherwise, it is kept: a target rigged like it chooses among those alone.
                if (Choices.Any(o => o.Yas == c.Yas && string.Equals(o.Pick!.Dir, c.Pick!.Dir, StringComparison.OrdinalIgnoreCase)
                                     && RefitCore.Normal(o.Pick.Option.Rel) == RefitCore.Normal(c.Pick.Option.Rel)))
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
                        if (pick == null || !AutoRefitDecisions.Proceed(pick.Confidence)) c = null;
                        else
                        {
                            c.Pick = pick;
                            c.Entry = request.Bodies.First(b => string.Equals(b.Dir, pick.Dir, StringComparison.OrdinalIgnoreCase));
                            c.SourcePath = c.Entry.Catalog.PathOf(pick.Option);
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
    }

    /// <summary>How a candidate's distance reads in the log, in millimetres.</summary>
    internal static string Describe(SourceDistance d)
        => $"body {d.BodyP90 * 1000f:F1} mm (mean {d.BodyMean * 1000f:F1}), skin {d.SkinP90 * 1000f:F1} mm (mean {d.SkinMean * 1000f:F1})";
}
