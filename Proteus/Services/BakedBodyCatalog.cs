using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CheapLoc;

namespace Proteus.Services;

/// <summary>
/// The bodies a garment was made on, bent into another race the way the garment itself is (see
/// <see cref="OtherRaceRefit"/>): the "made for" side of a refit for a picked race.
/// <para/>
/// A Midlander man's shirt was cut against a Midlander man's body. Baked for a Highlander, it is that shirt as the game
/// would draw it on him; the body it was cut against, bent by the very same deformer and skeleton, is still the body
/// under it, every gap and overlap between the two kept exactly. A body mod's own Highlander sculpt is another shape —
/// close or not — and reading the baked shirt against it would take that difference for the author's intent.
/// <para/>
/// Written to files, like <see cref="VanillaBodyCatalog"/>, because everything downstream speaks paths. Each is named
/// by what went into it, so a bake already made is never made again, and a changed skeleton or deformer makes another.
/// </summary>
internal static class BakedBodyCatalog
{
    /// <summary>Where the bent bodies live — out of Penumbra's mods root for the same reasons as the vanilla extracts.</summary>
    /// <remarks>Settable for the tests alone, which keep their own.</remarks>
    internal static string Dir { get; set; } = Path.Combine(Path.GetTempPath(), "proteus-baked-body");

    private static readonly object Gate = new();

    /// <summary>Each bent body whose bones were moved, by full path: the bones its file was rigged to before.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlySet<string>> Rigged =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The bones a bent body was rigged to before its bones were moved for the wearer (<see cref="SkeletonRemap"/>);
    /// null when none were. Which of Rue's plain and Yiggle sizes — one mesh rigged two ways — a garment was made for is
    /// read from these, not from the moved names, which can be the same for both.
    /// </summary>
    internal static IReadOnlySet<string>? RiggedBefore(string path)
        => Rigged.TryGetValue(Path.GetFullPath(path), out var bones) ? bones : null;

    /// <summary>Throw last session's bodies away. Called once at start-up, beside <see cref="VanillaBodyCatalog.CleanUp"/>.</summary>
    internal static void CleanUp()
    {
        lock (Gate)
        {
            try
            {
                if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
            }
            catch (IOException)
            {
                // Someone has a file open; a body is rewritten when it is needed anyway.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// <paramref name="source"/>'s bodies of the drawn race, bent to <paramref name="to"/>: the same options under the same
    /// headings, filed under the new race so its lists find them. Bodies that cannot be bent are left out, and say why
    /// in <paramref name="failed"/>.
    /// </summary>
    /// <param name="drawn">The race the garment is drawn as, whose bodies the source is read for.</param>
    /// <param name="slots">The slots to bend; the others are never refitted between and cost nothing.</param>
    /// <param name="byAlias">Bend body mods' own bones as the standard bones they stand in for: a refit for the other
    /// sex (see <see cref="RacialModelBake.Route.ByAlias"/>), whose garment is bent the same way.</param>
    /// <param name="planner">Moves a body's bones the wearer lacks onto ones it has, as the garment's were
    /// (<see cref="SkeletonRemap"/>): a body rigged to a women's custom skeleton bent for a man.</param>
    /// <param name="log">Hears each body's moves.</param>
    internal static BodySizeCatalog Of(BodySizeCatalog source, string drawn, ushort to, RacialModelBake.Wearer who,
                                       IEnumerable<string> slots, out List<string> failed, bool byAlias = false,
                                       SkeletonRemap.Planner? planner = null, Action<string>? log = null)
    {
        failed = [];
        if (!ushort.TryParse(drawn, out ushort from)) return new BodySizeCatalog(Dir, []);

        var how = new BendHow(Key(who) + (byAlias ? "-alias" : ""), byAlias, planner, log);
        var options = new List<BodyOption>();
        var made = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);   // source file -> baked rel
        foreach (string slot in slots)
            foreach (var option in source.For(slot, drawn).Where(o => BodySizeCatalog.RaceOf(o.GamePath) == drawn))
                Add(options, made, failed, source, option, from, to, who, how);
        return new BodySizeCatalog(Dir, options);
    }

    /// <summary>How each body is bent: the wearer's key for its file name, by alias or not, and its bones moved or not.</summary>
    private sealed record BendHow(string WearerKey, bool ByAlias, SkeletonRemap.Planner? Planner, Action<string>? Log);

    /// <summary>
    /// <paramref name="source"/>'s bodies bent DOWN into <paramref name="to"/>, a race the drawn one falls through to —
    /// the "made for" side of a Highlander model refitted for Midlanders. Per slot, the bodies of the first race on the
    /// line from the drawn race down to <paramref name="to"/> (<see cref="OtherRaceRefit.LineDown"/>) the source has
    /// bodies of exactly: that is the body the garment's part was cut against, as the game draws it on the drawn race —
    /// TBSE-X's Highlander torso, but its Midlander legs, which a Highlander draws bent. Bent down as the garment is, or
    /// taken as they are when they already are <paramref name="to"/>'s.
    /// </summary>
    /// <remarks>Across the sexes the line is the drawn race alone (<see cref="CrossSexRefit.MadeForLine"/>): a made-for
    /// mod with none of its bodies gives nothing, never the target race's own.</remarks>
    internal static BodySizeCatalog Lowered(BodySizeCatalog source, ushort drawn, ushort to, RacialModelBake.Wearer who,
                                            IEnumerable<string> slots, out List<string> failed, bool byAlias = false,
                                            SkeletonRemap.Planner? planner = null, Action<string>? log = null)
    {
        failed = [];
        var line = CrossSexRefit.MadeForLine(drawn, to);
        var how = new BendHow(Key(who) + (byAlias ? "-alias" : ""), byAlias, planner, log);
        var options = new List<BodyOption>();
        var made = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string slot in slots)
        {
            var all = source.For(slot);
            foreach (ushort race in line)
            {
                string code = $"{race:D4}";
                var own = all.Where(o => BodySizeCatalog.RaceOf(o.GamePath) == code).ToList();
                if (own.Count == 0) continue;
                foreach (var option in own) Add(options, made, failed, source, option, race, to, who, how);
                break;
            }
        }
        return new BodySizeCatalog(Dir, options);
    }

    private static void Add(List<BodyOption> options, Dictionary<string, string?> made, List<string> failed,
                            BodySizeCatalog source, BodyOption option, ushort from, ushort to, RacialModelBake.Wearer who,
                            BendHow how)
    {
        string why = "";
        if (Baked(made, source.PathOf(option), option, to, p => Bend(p, from, to, who, how, out why), out bool bent) is { } baked)
            options.Add(baked);
        else if (bent) failed.Add($"{option.FullLabel}: {why}");
    }

    /// <summary>
    /// <paramref name="option"/> on its bent model: each model file bent once (<paramref name="made"/>, by path), and a
    /// chest entry keeping its chest (<see cref="BodySizeCatalog.IsShape"/>) — the bent model is shared, the chests stay
    /// apart. Null when the model could not be bent.
    /// </summary>
    /// <param name="bent">Whether this call bent the model, rather than finding it bent before.</param>
    internal static BodyOption? Baked(Dictionary<string, string?> made, string path, BodyOption option, ushort to,
                                      Func<string, string?> bend, out bool bent)
    {
        bent = !made.TryGetValue(path, out string? rel);
        if (bent) made[path] = rel = bend(path);
        return rel == null ? null
            : option with { Rel = rel + BodySizeCatalog.ShapeSuffix(option.Rel), GamePath = RacialModelBake.WithRace(option.GamePath, to) };
    }

    /// <summary>
    /// One body bent from <paramref name="from"/> to <paramref name="to"/>: baked up when <paramref name="to"/> falls
    /// through to it, bent down when it falls through to <paramref name="to"/>, copied when the two are one. The
    /// direction is in the file's name, so a body bent one way is never taken for the same body bent the other.
    /// </summary>
    private static string? Bend(string path, ushort from, ushort to, RacialModelBake.Wearer who, BendHow how,
                                out string why)
    {
        why = "";
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = ex.Message;
            return null;
        }

        // The body's bones the wearer lacks, moved as the garment's were — bent or not, so the garment and the body it
        // was made on always name the same bones. The moves are in the file's name.
        string moved = "";
        IReadOnlySet<string>? riggedBefore = null;
        if (how.Planner != null && ModelSkinReader.Read(bytes, null, null) is { } rigged
            && how.Planner.MovesFor(OtherRaceRefit.Weighted(rigged)) is { Count: > 0 } moves)
        {
            if (SkeletonRemap.Rename(bytes, moves, out string refusal) is not { } renamed)
            {
                why = refusal;
                return null;
            }
            riggedBefore = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
            bytes = renamed;
            string said = SkeletonRemap.Describe(moves);
            moved = "-r" + Hash(System.Text.Encoding.UTF8.GetBytes(said))[..8];
            how.Log?.Invoke($"made-for body bones moved for c{to:D4} ({Path.GetFileName(path)}): {said}");
        }

        bool down = CrossSexRefit.BendsDown(from, to);
        Func<string, string>? alias = how.ByAlias ? UvBakeBones.Alias : null;
        string rel = from == to ? $"{Hash(bytes)}{moved}-c{to:D4}.mdl"
                   : $"{Hash(bytes)}{moved}-{how.WearerKey}-c{from:D4}-{(down ? "down" : "up")}-c{to:D4}.mdl";
        string full = Path.Combine(Dir, rel);
        if (riggedBefore != null) Rigged[Path.GetFullPath(full)] = riggedBefore;
        lock (Gate)
            if (File.Exists(full)) return rel;

        byte[]? baked = bytes;
        if (from != to)
        {
            // As the garment is: a body bone with nothing to follow would stay put while the rest of the body bent, and
            // the garment would be read against a body half in one race's shape and half in another's. Left out, saying
            // why.
            if (ModelSkinReader.Read(bytes, null, null) is { } skin
                && OtherRaceRefit.Unbendable(skin, who.Pbd, who.ParentOf, from, to) is { Count: > 0 } stuck)
            {
                why = string.Format(ProteusLoc.Localize("Parts.Retarget.Race.BodyBones.Fmt",
                                                 "its bones {0} would not move with {1}'s skeleton"),
                                    string.Join(", ", stuck.Take(8)), ModelRace.Describe($"{to:D4}"));
                return null;
            }
            baked = down ? RacialModelBake.Unbake(bytes, to, from, who.Pbd, who.ParentOf, out why, alias)
                         : RacialModelBake.Bake(bytes, from, to, who.Pbd, who.ParentOf, out why, alias);
            if (baked == null) return null;
        }
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                PenumbraModMeta.AtomicWrite(full, baked);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                why = ex.Message;
                return null;
            }
        }
        return rel;
    }

    /// <summary>What a wearer bends by, short: its skeleton's parents. The deformer is the game's own, fixed for the session.</summary>
    private static string Key(RacialModelBake.Wearer who)
        => Hash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",
               who.Parents.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ">" + p.Value))))[..8];

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
}
