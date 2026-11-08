using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Dalamud.Plugin.Services;
using Proteus.Localization;

namespace Proteus.Services;

/// <summary>
/// A women's mod's men's versions: each women's Midlander chest and legs piece the author made no men's model of is
/// refitted for Midlander men onto TBSE and TBSE-X, through the bibo↔TBSE maps, by the very job the Body size panel's
/// "Refit for" runs (<see cref="CrossSexRefitJob"/>). Saved at the c0101 path, with the EQDP entry that makes the game
/// load it, in a group of their own (<see cref="Strings.SizeSet"/>'s "Men's body") so the women's sizes stay exactly as
/// they were.
/// <para/>
/// Ticked apart from the men's bodies a men's mod is sized onto (<see cref="AcrossSuffix"/>): a tick left on TBSE from
/// a men's mod must never write men's versions into the next women's mod opened.
/// </summary>
internal static partial class SizeSetRefit
{
    /// <summary>What a men's body's key carries when ticked for a women's mod's men's versions: "tbse@women".</summary>
    internal const string AcrossSuffix = "@women";

    /// <summary>A body the size set offers for the open mod.</summary>
    /// <param name="Key">What the tick is remembered by.</param>
    /// <param name="Body">The table's key for it (<see cref="SupportedBodies.Choices"/>): whether it is installed.</param>
    /// <param name="ForMen">A men's body offered for a women's pieces' men's versions.</param>
    /// <param name="AcrossTip">Its tooltip says what those are.</param>
    internal sealed record Choice(string Key, string Body, string Label, bool Male, bool ForMen, bool AcrossTip);

    /// <summary>
    /// The bodies offered for a mod of <paramref name="pieces"/>: the women's when it has women's pieces, the men's when it
    /// has men's, and the men's again "for men" when it has women's chest or legs pieces to cross (<see cref="AcrossPieces"/>)
    /// — or, given <paramref name="modRoot"/>, one whose author's men's model no file of can be read as a model
    /// (<see cref="UnparsableMensPaths"/>), which a run may cross after all.
    /// </summary>
    /// <param name="modRoot">The mod's folder, to read the author's men's models' files by; null offers "for men" for
    /// pieces with no men's model only. Reads files: called once per mod, never per frame.</param>
    internal static IReadOnlyList<Choice> Offered(IReadOnlyList<Piece> pieces, string? modRoot = null)
    {
        var races = pieces.Select(p => p.Race).ToHashSet(StringComparer.Ordinal);
        bool across = AcrossPieces(pieces).Count > 0 || modRoot != null && UnparsableMensPaths(pieces, modRoot).Count > 0;
        var offered = new List<Choice>();
        foreach (var (key, label, male) in SupportedBodies.Choices)
            if (races.Contains(male ? "0101" : "0201")) offered.Add(new Choice(key, key, label, male, false, false));
        if (across)
            foreach (var (key, label, _) in SupportedBodies.Choices.Where(c => c.Male))
                offered.Add(new Choice(key + AcrossSuffix, key, string.Format(Strings.SizeSet.ForMenFmt, label), true,
                                       true, true));
        return offered;
    }

    /// <summary>
    /// The women's pieces that get men's versions: Midlander women's chest and legs (<see cref="CrossSexRefit.Supported"/>)
    /// and shoes and gloves (<see cref="CrossSexRefit.EndsCross"/>), and only those the author made no men's model of —
    /// that one is sized onto the men's bodies as it is. A men's model that is only the game's own is no piece at all
    /// (<see cref="PiecesOf"/>), so it stops nothing — nor a copy of it TexTools packed beside the author's women's design,
    /// an older version of the game's model as often as not.
    /// </summary>
    /// <param name="crossedInstead">Men's paths whose author's model no file of can be read as a model at all
    /// (<see cref="MensModelsUnparsable"/>): the women's piece there is crossed after all.</param>
    internal static IReadOnlyList<Piece> AcrossPieces(IReadOnlyList<Piece> pieces, IReadOnlySet<string>? crossedInstead = null)
    {
        var paths = pieces.Select(p => p.GamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return pieces.Where(p => Crossable(p)
                                 && (!paths.Contains(MensPathOf(p)) || crossedInstead?.Contains(MensPathOf(p)) == true))
                     .ToList();
    }

    /// <summary>A women's piece of a kind that gets men's versions, whatever the author made for men.</summary>
    internal static bool Crossable(Piece p)
        => p.Race == "0201"
           && (CrossSexRefit.Supported(CrossSexRefit.Women, CrossSexRefit.Men, p.Slot)
               || CrossSexRefit.EndsCross(CrossSexRefit.Women, CrossSexRefit.Men, p.Slot));

    /// <summary>Where a women's piece's men's version is saved: its c0101 path.</summary>
    internal static string MensPathOf(Piece p) => RacialModelBake.WithRace(p.GamePath, CrossSexRefit.Men);

    /// <summary>
    /// Whether the author's men's model at <paramref name="mensPath"/> came to anything this run: a size made, or one
    /// that leaves nothing to do (<see cref="NothingToDo"/>) — already there, the author's own, the size it was made on.
    /// Never "no body of its race ticked": that model was not tried at all.
    /// </summary>
    internal static bool MensModelUsable(IEnumerable<SizeOutcome> outcomes)
        => outcomes.Any(s => s.Status == SizeStatus.Made
                             || s.Status == SizeStatus.Skipped && s.ReasonCode is { } code && NothingToDo.Contains(code)
                                && code != Codes.NoBodyForRace);

    /// <summary>
    /// Whether every file the author gives <paramref name="mensPath"/> can't be read as a model at all: missing, zero
    /// bytes, or rejected by the model parser. Every redirect at that path counts — default files and any option of any
    /// group, ours left out as <see cref="PiecesOf"/> leaves them out — and none at all is false: there is nothing of the
    /// author's to stand in for. A model that parses is never counted, however the size set read it; nor one that could
    /// not be opened for another reason (locked, refused), which may well be a good model. Author FileSwaps are not read.
    /// </summary>
    internal static bool AuthorMensRedirectsUnparsable(string modRoot, string mensPath, BodyRetargetWriter.Record? record,
                                                       IReadOnlyList<PenumbraModMeta.Redirect>? redirects = null)
    {
        var ours = OurFiles(record);
        var authors = (redirects ?? PenumbraModMeta.ReadAllRedirects(modRoot))
            .Where(r => string.Equals(r.GamePath, mensPath, StringComparison.OrdinalIgnoreCase) && !IsOurFile(ours, r.File))
            .ToList();
        return authors.Count > 0 && authors.All(r => Unparsable(modRoot, r.File));
    }

    /// <summary>A mod file that is missing, zero bytes, or one the model parser rejects. Found as the size set finds the
    /// author's files (<see cref="BodySizeCatalog.ResolveCaseInsensitive"/>); one that cannot be opened is not counted.</summary>
    internal static bool Unparsable(string modRoot, string file)
    {
        byte[] bytes;
        try
        {
            string path = BodySizeCatalog.ResolveCaseInsensitive(modRoot, file);
            if (!File.Exists(path)) return true;
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return false;
        }
        if (bytes.Length == 0) return true;
        // The parser itself, not ModelPartReader.Read, which turns a parse failure into "no drawable part". Only what a
        // malformed file raises counts: any other exception is a bug of ours, and must never replace the author's model.
        try
        {
            SecondSkinWriter.Parse(bytes);
            return false;
        }
        catch (Exception e) when (MalformedModel(e))
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>What reading a truncated or damaged model raises: reads past its end, sizes that don't fit.</summary>
    internal static bool MalformedModel(Exception e)
        => e is IndexOutOfRangeException or ArgumentException or OverflowException or EndOfStreamException
                or InvalidDataException;

    /// <summary>
    /// The men's paths whose author's model can't be used at all, so the women's piece there is crossed after all: the
    /// men's model came to nothing this run (<see cref="MensModelUsable"/>) — tried or not — and no file the author gives
    /// that path can be read as a model (<see cref="AuthorMensRedirectsUnparsable"/>). None with a "Made on"
    /// body chosen: the author said where the mod was made, and the size set never falls back past that.
    /// </summary>
    internal static IReadOnlySet<string> MensModelsUnparsable(IReadOnlyList<Piece> pieces, RunLog runLog, string modRoot,
                                                              string? madeOn,
                                                              IReadOnlyList<PenumbraModMeta.Redirect>? redirects = null)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (madeOn != null) return set;
        // The files decide: none reads as a model, so whatever the men's bodies ticked, there is nothing of the author's to
        // size — tried this run (unreadable) or not (only "for men" ticked). A size that came of it all the same keeps it.
        foreach (string mens in UnparsableMensPaths(pieces, modRoot, redirects))
            if (!MensModelUsable(runLog.Sizes.Where(s => string.Equals(s.GamePath, mens, StringComparison.OrdinalIgnoreCase))))
                set.Add(mens);
        return set;
    }

    /// <summary>
    /// The c0101 paths of the crossable women's pieces whose author's men's piece beside them has no file that reads as a
    /// model (<see cref="AuthorMensRedirectsUnparsable"/>): what the files alone say, before any run.
    /// </summary>
    internal static IReadOnlyList<string> UnparsableMensPaths(IReadOnlyList<Piece> pieces, string modRoot,
                                                              IReadOnlyList<PenumbraModMeta.Redirect>? redirects = null)
    {
        var paths = pieces.Select(p => p.GamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mensPaths = pieces.Where(Crossable).Select(MensPathOf).Where(paths.Contains)
                              .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (mensPaths.Count == 0) return [];
        var record = BodyRetargetWriter.ReadRecord(modRoot);
        redirects ??= PenumbraModMeta.ReadAllRedirects(modRoot);
        return mensPaths.Where(m => AuthorMensRedirectsUnparsable(modRoot, m, record, redirects)).ToList();
    }

    /// <summary>The men's bodies ticked for men's versions, by the table's keys ("tbse"), in the table's order.</summary>
    internal static IReadOnlyList<string> MensKeys(IReadOnlyList<string> keys)
        => SupportedBodies.Choices.Where(c => c.Male && keys.Contains(c.Key + AcrossSuffix, StringComparer.Ordinal))
                          .Select(c => c.Key).ToList();

    /// <summary>The men's body and size a men's version of a <paramref name="slot"/> piece goes onto, and its file; null
    /// when the table has none.</summary>
    internal static (SupportedBodies.Body Body, SupportedBodies.Size Size, string Rel)? AcrossTarget(string key, string slot)
        => SupportedBodies.Of(key, slot, false) is { Race: "0101", Sizes: [var size, ..] } body ? (body, size, size.Rel) : null;

    /// <summary>
    /// Whether one of the author's sizes made on a body of <paramref name="madeForLayout"/> may be a men's version's
    /// source: a skin the maps join to a man's (bibo or the game's), never gen3's, which has no map of its own to TBSE.
    /// </summary>
    internal static bool AcrossEligible(string? madeForLayout)
        => madeForLayout != null && CrossSexRefit.LayoutsPair(madeForLayout, UVRemapService.TbseSpace);

    /// <summary>
    /// Whether one men's version may be saved, and what is said about it: refused with the reason under "Left out", or
    /// saved with its folds, moved bones and custom-skeleton bones under "Check in game". Never refused for its folds.
    /// </summary>
    /// <param name="piece">The piece, as notes call it.</param>
    /// <param name="option">The option it would be saved as ("TBSE").</param>
    /// <param name="skeletonFrom">Where the men's skeleton came from, for the custom-skeleton line.</param>
    internal static (bool Save, string? Note, IReadOnlyList<string> Warnings) Judge(
        string piece, string option, CrossSexRefitJob.Result result, CrossSexRefitJob.Size? size, string? skeletonFrom = null)
        => Judge(piece, option, result.Refusal, size?.Refusal, size?.Saved != null, size?.Landed?.VertexCoverage ?? 1.0,
                 size?.Folds ?? 0, result.Moved, result.NeedsSkeleton, skeletonFrom);

    /// <inheritdoc cref="Judge(string, string, CrossSexRefitJob.Result, CrossSexRefitJob.Size?, string?)"/>
    /// <param name="refusal">The job's refusal ("" when it ran).</param>
    /// <param name="sizeRefusal">The size's refusal ("" when made); null when no size was made at all.</param>
    /// <param name="made">Whether the size has a model to save.</param>
    internal static (bool Save, string? Note, IReadOnlyList<string> Warnings) Judge(
        string piece, string option, string refusal, string? sizeRefusal, bool made, double coverage, int folds,
        IReadOnlyList<SkeletonRemap.Move>? moved, IReadOnlyList<string>? needsSkeleton, string? skeletonFrom = null)
    {
        var s = Strings.SizeSet;
        string Refused(string why) => string.Format(s.RefusedFmt, piece, option, why);
        if (refusal.Length > 0) return (false, Refused(refusal), []);
        if (sizeRefusal == null) return (false, Refused(s.NotMade), []);
        if (sizeRefusal.Length > 0) return (false, Refused(sizeRefusal), []);
        if (!made) return (false, Refused(s.NotMade), []);
        if (CrossSexRefit.Judged(coverage, folds).Refusal is { } low) return (false, Refused(low), []);

        var warnings = new List<string>();
        string Warn(string what) => string.Format(s.WarningFmt, piece, option, what);
        string men = ModelRace.Describe($"{CrossSexRefit.Men:D4}");
        if (folds > 0) warnings.Add(Warn(string.Format(Strings.Parts.RetargetRaceCrossSexFoldedFmt, folds)));
        if (moved is { Count: > 0 })
            warnings.Add(Warn(string.Format(Strings.Parts.RetargetRaceBonesMovedFmt, men, moved.Count,
                                            SkeletonRemap.Describe(moved))));
        if (needsSkeleton is { Count: > 0 } needs)
            warnings.Add(Warn(string.Format(Strings.Parts.RetargetRaceNeedsSkeletonFmt, men,
                                            Gui.BodyRetargetPanel.SkeletonMod(skeletonFrom), needs.Count,
                                            string.Join(", ", needs.Take(12)))));
        return (true, null, warnings);
    }

    /// <summary>
    /// The group a women's mod's men's versions go into: the one an earlier run saved them into — found by the record,
    /// whatever language named it — else a new one named in the current language.
    /// <para/>
    /// Only a group of ours that holds nothing but men's versions counts: every entry of it a men's body's option
    /// ("TBSE", "TBSE-X") at the c0101 path of one of these pieces. A Body size panel "Refit for" save keeps its own
    /// group, beside the women's refits there, and the size set never writes into it.
    /// </summary>
    /// <param name="crossable">Women's pieces whose men's version an earlier run may have made though the author has a
    /// men's model there (<see cref="MensModelsUnparsable"/>): their c0101 path counts too where the record holds an
    /// entry of ours at it under a men's option name — so the group stays recognised whatever this run crosses.</param>
    /// <param name="ownSizeGroups">The size groups of the mod's own men's pieces (<see cref="OwnSizeGroups"/>): never the
    /// men's versions' group, though such a group holds nothing but men's options at those very paths.</param>
    internal static string MensGroupName(string modRoot, IReadOnlyList<Piece> across, IReadOnlyList<Piece>? crossable = null,
                                         IReadOnlySet<string>? ownSizeGroups = null)
    {
        var saves = across.Select(MensPathOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = MensOptionNames();
        var record = BodyRetargetWriter.ReadRecord(modRoot);
        if (record == null) return Strings.SizeSet.MensGroup;
        foreach (var piece in crossable ?? [])
        {
            string mens = MensPathOf(piece);
            if (record.Options.Any(e => !e.InAuthorGroup && names.Contains(e.Name)
                                        && string.Equals(e.GamePath, mens, StringComparison.OrdinalIgnoreCase)))
                saves.Add(mens);
        }
        var groups = PenumbraModMeta.TryReadGroups(modRoot)?.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool OnlyMens(string group)
            => record.Options.Where(e => string.Equals(record.GroupOf(e), group, StringComparison.OrdinalIgnoreCase))
                             .All(e => !e.InAuthorGroup && saves.Contains(e.GamePath) && names.Contains(e.Name));
        return record.Options.Where(e => !e.InAuthorGroup && saves.Contains(e.GamePath))
                             .Select(e => record.GroupOf(e))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .FirstOrDefault(g => g.Length > 0 && (groups?.Contains(g) ?? false)
                                                  && ownSizeGroups?.Contains(g) != true && OnlyMens(g))
               ?? Strings.SizeSet.MensGroup;
    }

    /// <summary>The options a men's version is saved as: one per men's body ("TBSE", "TBSE-X").</summary>
    internal static HashSet<string> MensOptionNames()
        => SupportedBodies.Choices.Where(c => c.Male)
                          .Select(c => AcrossTarget(c.Key, "_top") is { } at ? SupportedBodies.OptionName(at.Body, at.Size) : null)
                          .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The groups the mod's own men's pieces are sized in: their author's size group, or the one made for them — named in
    /// whichever language Proteus spoke when it was made, so in every language it ships.
    /// </summary>
    internal static HashSet<string> OwnSizeGroups(IReadOnlyList<Piece> all)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in all.Where(p => p.Race == "0101"))
        {
            if (p.Group != null)
            {
                groups.Add(p.Group);
                continue;
            }
            groups.Add(p.GroupName);
            foreach (string format in GroupFormats.Value)
                foreach (string model in p.Models ?? [])
                    try { groups.Add(string.Format(format, model)); }
                    catch (FormatException) { }
        }
        return groups;
    }

    /// <summary>The name format of a group made for a piece's sizes ("Body — {0}"), in every language Proteus ships.</summary>
    private static readonly Lazy<IReadOnlyList<string>> GroupFormats
        = new(() => InEveryLanguage("Parts.Retarget.Group.Fmt", Strings.Parts.RetargetGroupFmt));

    /// <summary>A string's message in each language file embedded in the plugin, and <paramref name="current"/>.</summary>
    internal static IReadOnlyList<string> InEveryLanguage(string key, string current)
    {
        var found = new List<string> { current };
        var assembly = typeof(SizeSetRefit).Assembly;
        foreach (string name in assembly.GetManifestResourceNames()
                                        .Where(n => n.StartsWith("Proteus.Localization.", StringComparison.Ordinal)
                                                    && n.EndsWith(".json", StringComparison.Ordinal)))
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream == null) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty(key, out var entry) && entry.TryGetProperty("message", out var message)
                    && message.GetString() is { Length: > 0 } text)
                    found.Add(text);
            }
            catch (System.Text.Json.JsonException) { }
        return found.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The women's pieces whose author's men's model a men's version of ours still stands over, though this run makes none
    /// there: the author has a men's piece at that path, and the record holds an entry of ours at it under a men's option
    /// name in <paramref name="mensGroup"/> — made from the women's piece by an earlier run, when that model could not be
    /// read. Never deleted: said, with how to show the author's model again.
    /// </summary>
    /// <param name="skip">Men's paths left out: those crossed this run, and those whose author's files can't be read as
    /// a model — there a men's version of ours is the men's model, not one standing over the author's.</param>
    internal static IReadOnlyList<Piece> StillReplaced(IReadOnlyList<Piece> all, BodyRetargetWriter.Record? record,
                                                       string mensGroup, IReadOnlySet<string> skip)
    {
        if (record == null) return [];
        var paths = all.Select(p => p.GamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = MensOptionNames();
        return all.Where(p => Crossable(p) && paths.Contains(MensPathOf(p)) && !skip.Contains(MensPathOf(p))
                              && record.Options.Any(e => !e.InAuthorGroup && names.Contains(e.Name)
                                                         && string.Equals(e.GamePath, MensPathOf(p), StringComparison.OrdinalIgnoreCase)
                                                         && string.Equals(record.GroupOf(e), mensGroup, StringComparison.OrdinalIgnoreCase)))
                  .ToList();
    }

    /// <summary>What Check in game says of each piece <see cref="StillReplaced"/> found.</summary>
    internal static IEnumerable<string> StillReplacedLines(IEnumerable<Piece> pieces, string mensGroup)
        => pieces.Select(p => string.Format(Strings.SizeSet.AuthorsMensStillReplacedFmt, p.Label, mensGroup));

    /// <summary>
    /// What Check in game says of each women's piece crossed because its author's men's model could not be read
    /// (<see cref="MensModelsUnparsable"/>) and made a men's version this run: that it now replaces that model wherever a
    /// men's body is picked in the group. Nothing for one whose crossing made nothing — its refusals say why.
    /// </summary>
    internal static IEnumerable<string> CrossedLines(IReadOnlySet<string> crossedInstead, IReadOnlyList<Piece> pieces,
                                                     RunLog runLog, string mensGroup)
        => pieces.Where(p => Crossable(p) && crossedInstead.Contains(MensPathOf(p))
                             && runLog.Sizes.Any(s => s.Men && s.Status == SizeStatus.Made
                                                      && string.Equals(s.GamePath, MensPathOf(p), StringComparison.OrdinalIgnoreCase)))
                 .Select(p => string.Format(Strings.SizeSet.AuthorsMensCrossedFmt, p.Label, mensGroup));

    /// <summary>
    /// The key a men's option's lead is kept by: the piece's own size group — legs following a chest share it by
    /// definition — so two outfits of one mod, each sized in its own group, never take each other's chest's size. A
    /// piece in no group keys by the men's group.
    /// </summary>
    internal static string AcrossLeadKey(Piece piece, string mensGroup, string option)
        => LeadKey(piece.Group ?? mensGroup, "0101", option);

    /// <summary>How a legs piece's men's version picks its source (<see cref="FromLead{T}"/>).</summary>
    internal enum LeadOutcome
    {
        /// <summary>Not a follower: its own nearest.</summary>
        Own,
        /// <summary>Cut from the option the chest's men's version was.</summary>
        FromLead,
        /// <summary>The chest's option could not be read or crossed for it: its own nearest.</summary>
        LeadUnusable,
        /// <summary>The chest made no men's version this run to follow: its own nearest.</summary>
        NoLead,
    }

    /// <summary>
    /// Which of the author's sizes legs sized with a chest are cut from for a men's option: the one the chest's men's
    /// version there was, when it made one and that size can be crossed for the legs too; otherwise none, and the legs
    /// take their own nearest.
    /// </summary>
    /// <param name="leadKey">The option's key (<see cref="LeadKey"/>).</param>
    /// <param name="prepare">The legs' own size in the chest's option, ready to cross; null when there is none.</param>
    internal static (LeadOutcome Outcome, string? Option, T? Chosen) FromLead<T>(
        bool followsChest, IReadOnlyDictionary<string, string> leadSources, string leadKey, Func<string, T?> prepare)
        where T : class
    {
        if (!followsChest) return (LeadOutcome.Own, null, null);
        if (!leadSources.TryGetValue(leadKey, out var option)) return (LeadOutcome.NoLead, null, null);
        return prepare(option) is { } chosen ? (LeadOutcome.FromLead, option, chosen) : (LeadOutcome.LeadUnusable, option, null);
    }

    /// <summary>
    /// What is said when none of the author's sizes could be crossed: why the men's wearer could not be built, when
    /// that is what stopped one; else that none was made on a body the maps join to a man's.
    /// </summary>
    internal static string NoSourceNote(string piece, string option, string? wearerRefusal)
        => wearerRefusal != null
            ? string.Format(Strings.SizeSet.RefusedFmt, piece, option, wearerRefusal)
            : string.Format(Strings.SizeSet.NoSourceForMenFmt, piece);

    /// <summary>
    /// One of the author's sizes made ready to cross: its wearer built, and the body it was made on bent for a man the
    /// way the garment is. What the nearest is chosen among, and what its job is given.
    /// </summary>
    private sealed record Ready(Candidate Candidate, OtherRaceWearer.Inputs Wearer, string? SkeletonFrom,
                                BodySizeCatalog MadeFor, string BentPath);

    /// <summary>
    /// The author's sizes of one women's piece as men's versions' sources: which may be crossed (made on a skin the maps
    /// join to a man's), each made ready once (<see cref="Prepare"/>) — its own wearer, its made-for body bent — and the
    /// nearest of them to a men's body (<see cref="Nearest"/>).
    /// </summary>
    private sealed class MensSources
    {
        private readonly Request request;
        private readonly AcrossInputs inputs;
        private readonly Piece piece;
        private readonly Sources sources;
        private readonly IPluginLog log;
        private readonly Action<string> note;
        private readonly IReadOnlyList<PenumbraModMeta.Redirect> redirects;
        private readonly List<(string Name, System.Text.Json.JsonElement Group)>? groups;
        private readonly List<object>? defaultManipulations;
        private readonly Dictionary<string, string?> layouts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Candidate, Ready?> ready = [];
        private readonly Dictionary<string, System.Numerics.Vector3[]> probes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Made-for bodies bent so far, for the log.</summary>
        public int Bent { get; private set; }

        /// <summary>Why the last size refused for its wearer could not be crossed: its rig unreadable, or no men's
        /// wearer for it. What the author is told when no size could be crossed for that reason.</summary>
        public string? WearerRefusal { get; private set; }

        public MensSources(Request request, AcrossInputs inputs, Piece piece, Sources sources,
                           IReadOnlyList<PenumbraModMeta.Redirect> redirects, IPluginLog log, Action<string> note)
        {
            this.request = request;
            this.inputs = inputs;
            this.piece = piece;
            this.sources = sources;
            this.redirects = redirects;
            this.log = log;
            this.note = note;
            groups = PenumbraModMeta.TryReadGroups(request.ModRoot);
            defaultManipulations = PenumbraModMeta.TryReadDefaultData(request.ModRoot)?.Manipulations;
        }

        private byte[]? ReadGame(string path)
        {
            try { return request.ReadGameFile!(path); }
            catch (Exception e)
            {
                // Most paths asked for are only maybe there (an extra skeleton per entry).
                log.Debug("[Proteus] size set: the game gave nothing for {0}: {1}", path, e.Message);
                return null;
            }
        }

        private string? LayoutOf(Candidate c)
        {
            if (layouts.TryGetValue(c.SourcePath, out var known)) return known;
            string? layout = null;
            try
            {
                if (File.Exists(c.SourcePath) && ModelPartReader.Read(File.ReadAllBytes(c.SourcePath)) is { } body)
                    layout = BodyCorrespondence.LayoutOf(body);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or ArgumentException or IndexOutOfRangeException)
            {
                note($"{Path.GetFileName(c.SourcePath)} could not be read ({e.GetType().Name})");
            }
            return layouts[c.SourcePath] = layout;
        }

        /// <summary>
        /// A size made ready to cross, once: its own wearer — what the option it is cut from carries (an extra skeleton,
        /// Est entries) is what the men's version loads — and its made-for body bent with that same wearer, as the job
        /// bends the garment. Null, said in the log, when it may not be crossed.
        /// </summary>
        public Ready? Prepare(Candidate c)
        {
            if (ready.TryGetValue(c, out var known)) return known;
            ready[c] = null;
            if (!AcrossEligible(LayoutOf(c)))
            {
                note($"{c.Name}: made on a {LayoutOf(c) ?? "unreadable"} skin, which no map joins to a man's");
                return null;
            }
            string root = request.ModRoot;
            string? cutFrom = piece.Group != null && c.Option != null ? $"{piece.Group} / {c.Option}" : null;
            var wearer = OtherRaceWearer.Gather(CrossSexRefit.Men, piece.GamePath, ReadGame, inputs.ResolveCollection,
                path => OtherRaceRefit.CarriedFile(redirects, cutFrom, path) is { } rel ? ReadModFile(root, rel) : null,
                OtherRaceRefit.EstEntriesFor(groups, defaultManipulations, cutFrom),
                (file, e) => log.Warning(e, "[Proteus] size set: could not read {0}", file));
            if (ModelSkinReader.Read(c.Bytes, null, null) is not { } skin)
            {
                WearerRefusal = Strings.SizeSet.RigUnreadable;
                note($"{c.Name}: its rig could not be read");
                return null;
            }
            var made = OtherRaceWearer.Build(wearer, skin);
            if (made.Wearer is not { } who)
            {
                WearerRefusal = made.Refusal;
                note($"{c.Name}: no man's wearer: {made.Refusal}");
                return null;
            }
            if (made.FellBack) note("the collection's men's skeleton could not be read; the game's was used");
            var madeFor = c.Entry.Catalog with { Options = [c.Pick!.Option] };
            var bentCat = BakedBodyCatalog.Lowered(madeFor, CrossSexRefit.Women, CrossSexRefit.Men, who, [piece.Slot],
                                                   out var failed, byAlias: true, made.Planner, note);
            if (bentCat.For(piece.Slot).FirstOrDefault() is not { } bentBody)
            {
                note($"{c.Name}: its body could not be bent for a man: {string.Join("; ", failed)}");
                return null;
            }
            Bent++;
            return ready[c] = new Ready(c, wearer, made.FellBack ? null : wearer.ModdedFrom, madeFor, bentCat.PathOf(bentBody));
        }

        /// <summary>The legs' own size in the chest's option <paramref name="option"/>, ready to cross; null when it
        /// gives the legs nothing, or that size may not be crossed.</summary>
        public Ready? InOption(string option)
            => piece.Group != null && ResolveOptionFile(redirects, piece.GamePath, piece.Group, option) is { } file
               && sources.ByFile(option, file) is { } c
                ? Prepare(c)
                : null;

        /// <summary>
        /// The nearest of the sizes that may be crossed to the men's body at <paramref name="targetPath"/>, rigged alike
        /// (not YAS) first, and the log line saying why; null when none may be. Read between bodies alone: the garment's
        /// own skin is a women's, and no man's skin is near it.
        /// </summary>
        public (Ready Chosen, string Why)? Nearest(string targetPath, string option)
        {
            var said = new List<string>();
            var may = sources.Choices.Select(Prepare).OfType<Ready>().ToList();
            var alike = RiggedAlike(may, r => r.Candidate.Yas, targetYas: false);
            foreach (var r in may.Except(alike)) said.Add($"{r.Candidate.Name}: rigged for YAS");
            var eligible = new List<(Ready, SourceDistance)>();
            foreach (var r in alike)
            {
                var (p90, mean) = BodyDistance(r.BentPath, targetPath, ProbesOf);
                eligible.Add((r, r.Candidate.Distance(p90, mean, 0f, 0f)));
            }
            if (eligible.Count == 0) return null;
            var (chosen, step, gap, misfits) = ChooseWithMisfits(eligible);
            for (int i = 0; i < eligible.Count; i++)
                said.Add($"{eligible[i].Item1.Candidate.Name}: {Describe(eligible[i].Item2, misfits.Contains(i))}");
            if (LoneMadeOn(eligible, chosen) is { } lone) said.Add(lone);
            return (chosen, $"{option} from {chosen.Candidate.Name} (by {step}, {gap * 1000f:F1} mm ahead; {string.Join("; ", said)})");
        }

        private System.Numerics.Vector3[] ProbesOf(string path)
            => probes.TryGetValue(path, out var p) ? p : probes[path] = BodyProbes(path, note);
    }

    /// <summary>
    /// The women's pieces a men's version is not made of because the author made the men's model of it themselves
    /// (<see cref="AcrossPieces"/>): one <see cref="Codes.AuthorsMens"/> outcome per men's body ticked, so none is silent.
    /// A note only when there is something to do about it, read off what became of that men's model this run: it was
    /// refused, failed or unreadable and came to nothing (<see cref="MensModelUsable"/>); or only plain men's bodies were
    /// missing for it — then which to tick.
    /// </summary>
    /// <param name="crossedInstead">Men's paths whose women's piece is crossed after all (<see cref="MensModelsUnparsable"/>):
    /// not stated here — the crossing says what became of it.</param>
    /// <param name="stillReplaced">Pieces a men's version of ours still stands over (<see cref="StillReplaced"/>): their
    /// reason is the line saying so, and no note says "no men's version was made from it".</param>
    /// <param name="standsIn">Men's paths where a men's version of ours from an earlier run is the men's model, the
    /// author's files there still unreadable: no note says "no men's version was made from it" of those.</param>
    internal static void StateAuthorsMens(IReadOnlyList<Piece> pieces, IReadOnlyList<string> mensKeys, RunLog runLog,
                                          IReadOnlySet<string>? crossedInstead = null,
                                          IReadOnlyDictionary<Piece, string>? stillReplaced = null,
                                          IReadOnlySet<string>? standsIn = null)
    {
        var paths = pieces.Select(p => p.GamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in pieces)
        {
            if (!Crossable(piece)) continue;
            string mens = MensPathOf(piece);
            if (!paths.Contains(mens) || crossedInstead?.Contains(mens) == true) continue;

            var outcomes = runLog.Sizes.Where(s => string.Equals(s.GamePath, mens, StringComparison.OrdinalIgnoreCase))
                                       .ToList();
            bool wrong = outcomes.Any(s => s.Status is SizeStatus.Refused or SizeStatus.Failed
                                           || s.Status == SizeStatus.Skipped && s.ReasonCode is Codes.Unreadable
                                                                                  or Codes.EndsUnreadable);
            bool unticked = outcomes.Count > 0 && outcomes.All(s => s.ReasonCode == Codes.NoBodyForRace);
            string? stale = stillReplaced != null && stillReplaced.TryGetValue(piece, out var line) ? line : null;
            string? note = stale != null || standsIn?.Contains(mens) == true ? null
                         : !MensModelUsable(outcomes) && wrong ? string.Format(Strings.SizeSet.AuthorsMensFmt, piece.Label)
                         : unticked ? string.Format(Strings.SizeSet.AuthorsMensUntickedFmt, piece.Label,
                                                    SupportedBodies.Choices.First(c => c.Key == mensKeys[0]).Label)
                         : null;
            if (note != null) runLog.Notes.Add(note);
            foreach (string key in mensKeys)
                runLog.Add(piece, SizeStatus.Skipped, Codes.AuthorsMens,
                           stale ?? note ?? $"{mens} is the author's own men's model of it", piece.Group, body: key,
                           men: true, gamePath: mens);
        }
    }

    internal static int RunAcross(Request request, IReadOnlyList<Piece> pieces, IReadOnlyList<Piece> all,
                                  UVRemapService? uvRemap, IPluginLog log, Action<string> progress, RunLog runLog,
                                  CancellationToken ct)
    {
        var mensKeys = MensKeys(request.Keys);
        // The women's pieces crossed after all, their author's men's model being no model at all.
        var crossedInstead = request.Across != null && mensKeys.Count > 0
            ? MensModelsUnparsable(pieces, runLog, request.ModRoot, request.MadeOn)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Every women's piece the group may hold, shoes and gloves too whether or not they are made this time: a group an
        // earlier run put men's shoes in is still ours when they are left out now.
        var mayHold = request.Ends || request.ReadGameFile == null
            ? AcrossPieces(pieces, crossedInstead)
            : AcrossPieces(all, crossedInstead);
        var crossable = all.Where(Crossable).ToList();
        string groupName = MensGroupName(request.ModRoot, mayHold, crossable, OwnSizeGroups(all));

        // A men's version an earlier run made over an author's men's model that could not be read then still stands over
        // it — said whatever this run makes, before anything else returns.
        // Never where the author's files still can't be read: there ours is the men's model, not one standing over it.
        var record = BodyRetargetWriter.ReadRecord(request.ModRoot);
        var unparsable = UnparsableMensPaths(all, request.ModRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
        unparsable.UnionWith(crossedInstead);
        var stale = StillReplaced(all, record, groupName, unparsable);
        var staleLines = stale.Zip(StillReplacedLines(stale, groupName)).ToDictionary(z => z.First, z => z.Second);
        runLog.Warnings.AddRange(staleLines.Values);
        if (mensKeys.Count == 0) return 0;
        // The author's files still unreadable and an earlier men's version of ours there: that is the men's model.
        var names = MensOptionNames();
        var standsIn = unparsable.Where(m => record?.Options.Any(e => !e.InAuthorGroup && names.Contains(e.Name)
                                                                     && string.Equals(e.GamePath, m, StringComparison.OrdinalIgnoreCase))
                                             == true)
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);
        StateAuthorsMens(pieces, mensKeys, runLog, crossedInstead, staleLines, standsIn);
        if (request.Across is not { } inputs) return 0;
        var across = AcrossPieces(pieces, crossedInstead);
        if (across.Count == 0) return 0;
        try
        {
            return RunAcrossPieces(request, inputs, pieces, across, groupName, mensKeys, uvRemap, log, progress, runLog, ct);
        }
        finally
        {
            // The fallback pieces: the line saying what their men's versions now stand in for, however the run ended.
            runLog.Warnings.AddRange(CrossedLines(crossedInstead, pieces, runLog, groupName));
        }
    }

    private static int RunAcrossPieces(Request request, AcrossInputs inputs, IReadOnlyList<Piece> pieces,
                                       IReadOnlyList<Piece> across, string groupName, IReadOnlyList<string> mensKeys,
                                       UVRemapService? uvRemap, IPluginLog log, Action<string> progress, RunLog runLog,
                                       CancellationToken ct)
    {
        var notes = runLog.Notes;

        // Every piece and men's body, when none is made at all.
        void NoneMade(string code, string reason, string? group)
        {
            foreach (var piece in across)
                foreach (string key in mensKeys)
                    runLog.Add(piece, SizeStatus.Skipped, code, reason, group,
                               (IsEnd(piece.Slot) ? AcrossEndTarget(key, piece.Slot) : AcrossTarget(key, piece.Slot)) is { } at
                                   ? SupportedBodies.OptionName(at.Body, at.Size) : null,
                               key, men: true, gamePath: RacialModelBake.WithRace(piece.GamePath, CrossSexRefit.Men));
        }
        if (BodyRetargetWriter.IsAuthorGroup(request.ModRoot, groupName))
        {
            string said = string.Format(Strings.SizeSet.MensGroupTakenFmt, groupName);
            notes.Add(said);
            NoneMade(Codes.MenGroupTaken, said, groupName);
            return 0;
        }
        // Without the maps no body is matched across the sexes: one note, nothing bent or written.
        if (uvRemap == null || !uvRemap.MenMapsPresent())
        {
            uvRemap?.RequestMenMaps();
            notes.Add(Strings.SizeSet.MenMapsMissing);
            NoneMade(Codes.MenMapsMissing, Strings.SizeSet.MenMapsMissing, groupName);
            log.Information("[Proteus] size set: no men's versions: the body maps are not here yet");
            return 0;
        }
        if (request.ReadGameFile == null)
        {
            log.Information("[Proteus] size set: no men's versions: the game's data is not readable here");
            NoneMade(Codes.NoGameData, "the game's data is not readable here", groupName);
            return 0;
        }

        int made = 0;
        var readings = new Dictionary<string, AutoRefitDecisions.Pick?>(StringComparer.Ordinal);
        var madeOnReadings = new Dictionary<string, SourceReading>(StringComparer.Ordinal);
        var leadSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in across)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                made += IsEnd(piece.Slot)
                    ? RunAcrossEnd(request, inputs, piece, groupName, mensKeys, uvRemap, log, progress, runLog,
                                   leadSources, pieces, readings, ct)
                    : RunAcrossPiece(request, inputs, piece, groupName, mensKeys, uvRemap, log, progress, runLog,
                                     readings, madeOnReadings, leadSources, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                log.Error(e, "[Proteus] size set: {0} for men failed", piece.GamePath);
                runLog.Note(string.Format(Strings.SizeSet.PieceFailedFmt, piece.Label, e.Message), piece, SizeStatus.Failed,
                            Codes.PieceFailed, groupName, men: true,
                            gamePath: RacialModelBake.WithRace(piece.GamePath, CrossSexRefit.Men));
            }
        }
        return made;
    }

    private static int RunAcrossPiece(Request request, AcrossInputs inputs, Piece piece, string groupName,
                                      IReadOnlyList<string> mensKeys, UVRemapService uvRemap, IPluginLog log,
                                      Action<string> progress, RunLog runLog,
                                      Dictionary<string, AutoRefitDecisions.Pick?> readings,
                                      Dictionary<string, SourceReading> madeOnReadings,
                                      Dictionary<string, string> leadSources, CancellationToken ct)
    {
        var notes = runLog.Notes;
        var warnings = runLog.Warnings;
        string root = request.ModRoot;
        string save = RacialModelBake.WithRace(piece.GamePath, CrossSexRefit.Men);
        void Note(string note) => log.Information("[Proteus] size set: {0} for men: {1}", piece.Label, note);
        progress(string.Format(Strings.SizeSet.ReadingFmt, piece.Label));
        var mods = request.Bodies.Select(b => (b.Dir, b.Catalog)).ToList();
        if (mods.Count == 0)
        {
            runLog.Note(Strings.SizeSet.NoBodies, piece, SizeStatus.Skipped, Codes.NoBodies, groupName, men: true,
                        gamePath: save);
            return 0;
        }
        var clock = Stopwatch.StartNew();
        using var reading = BodySizeMatch.BeginReading();
        string preferred = mods[0].Dir;
        var sources = new Sources(request, piece, preferred, mods, readings, madeOnReadings, Note, ct);
        if (sources.Primary == null)
        {
            var (unread, code) = UnreadableNote(piece.Label, request.MadeOn, sources.MadeOnName, sources.MadeOnMissing,
                                                sources.Fits,
                                                noneParsed: request.MadeOn == null && PieceFilesUnparsable(root, piece));
            runLog.Note(unread, piece, SizeStatus.Skipped, code, groupName, men: true, gamePath: save);
            return 0;
        }
        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        var mens = new MensSources(request, inputs, piece, sources, redirects, log, Note);

        BodySizeMatch.NeedNear(sources.Choices.Count + 2 * mensKeys.Count);
        using var group = new SizeGroup(root, piece, groupName, save);
        int saved = 0;
        for (int k = 0; k < mensKeys.Count; k++)
        {
            string key = mensKeys[k];
            ct.ThrowIfCancellationRequested();
            if (AcrossTarget(key, piece.Slot) is not { } at) continue;
            var (body, size, rel) = at;
            string option = SupportedBodies.OptionName(body, size);
            var sizeClock = Stopwatch.StartNew();
            void Out(SizeStatus status, string? code, string? reason, string? bodyMod, string? from = null,
                     Measures? measures = null, string? cutFrom = null)
                => runLog.Add(piece, status, code, reason, group.Name, option, key, bodyMod, yas: false, men: true,
                              from: from, cutFrom: cutFrom, measures: measures,
                              seconds: status == SizeStatus.Made ? sizeClock.Elapsed.TotalSeconds : null, gamePath: save);
            if (SupportedBodies.Find(body, request.Bodies) is not { } target)
            {
                string left = string.Format(Strings.SizeSet.NotInstalledFmt, piece.Label, body.Label);
                notes.Add(left);
                Out(SizeStatus.Skipped, Codes.NotInstalled, left, null);
                continue;
            }
            if (group.AlreadyThere(option, request.Remake))
            {
                log.Information("[Proteus] size set: {0} for men: \"{1}\" already there", piece.Label, option);
                AlreadyOut(group, option, (code, why) => Out(SizeStatus.Skipped, code, why, target.Dir));
                continue;
            }
            var men = OtherRaceRefit.OnlyRace(target.Catalog, CrossSexRefit.Men);
            if (OptionByFile(men.For(piece.Slot), rel) is not { } onto)
            {
                string left = string.Format(Strings.SizeSet.NoSuchSizeFmt, piece.Label, option, target.Name);
                notes.Add(left);
                Out(SizeStatus.Skipped, Codes.NoSuchSize, left, target.Dir);
                continue;
            }
            string targetPath = men.PathOf(onto);

            // ── which of the author's sizes: the one the chest's men's version was, else the nearest ──
            string leadKey = AcrossLeadKey(piece, groupName, option);
            var (outcome, leadOption, chosen) = FromLead(piece.FollowsChest, leadSources, leadKey, mens.InOption);
            switch (outcome)
            {
                case LeadOutcome.FromLead: Note($"{option} from \"{leadOption}\", as the chest's was"); break;
                case LeadOutcome.LeadUnusable: Note($"{option}: not cut from \"{leadOption}\" as the chest's was; the nearest instead"); break;
                case LeadOutcome.NoLead: Note($"{option}: the chest got no men's version from this run to follow; the nearest instead"); break;
            }
            if (chosen == null)
            {
                if (mens.Nearest(targetPath, option) is not { } nearest)
                {
                    // None may be crossed: no other men's body would find one either.
                    string left = NoSourceNote(piece.Label, option, mens.WearerRefusal);
                    notes.Add(left);
                    Out(SizeStatus.Skipped, Codes.NoSourceMen, left, target.Dir);
                    // Nor for the men's bodies after it: each is said so.
                    foreach (string rest in mensKeys.Skip(k + 1))
                        if (AcrossTarget(rest, piece.Slot) is { } restAt)
                            runLog.Add(piece, SizeStatus.Skipped, Codes.NoSourceMen, left, group.Name,
                                       SupportedBodies.OptionName(restAt.Body, restAt.Size), rest,
                                       SupportedBodies.Find(restAt.Body, request.Bodies)?.Dir, men: true, gamePath: save);
                    break;
                }
                chosen = nearest.Chosen;
                Note(nearest.Why);
            }

            progress(string.Format(Strings.SizeSet.WorkingFmt, piece.Label, option));
            long started = clock.ElapsedMilliseconds;
            var c = chosen.Candidate;
            var job = CrossSexRefitJob.Run(new CrossSexRefitJob.Inputs(
                c.Bytes, piece.GamePath, CrossSexRefit.Men, chosen.MadeFor, null, men,
                o => RefitCore.Normal(o.Rel) == RefitCore.Normal(rel), chosen.Wearer, uvRemap, log,
                CutHidden: !request.KeepHiddenSkin, CarryPiercings: request.Piercings,
                Nude: NudeFor(men.For(piece.Slot), size, log)));
            var made = job.Sizes.FirstOrDefault();
            var (ok, refusal, said) = Judge(piece.Label, option, job, made, chosen.SkeletonFrom);
            log.Information("[Proteus] size set: {0} for men: {1} in {2} ms (source in {3} ms, {4} bodies bent): {5}",
                            piece.Label, option, clock.ElapsedMilliseconds - started, started, mens.Bent,
                            ok ? $"{made!.Folds} folded, {made.Landed?.VertexCoverage:P1} matched, skin "
                               + $"{made.Plan?.Report.Swap?.Added ?? 0} added, {made.Plan?.Report.Swap?.Cut ?? 0} cut"
                               + NudeSaid(made, targetPath)
                               : refusal ?? "");
            if (!ok)
            {
                notes.Add(refusal!);
                Out(SizeStatus.Refused, Codes.RefusedMen, refusal, target.Dir);
                continue;
            }

            ct.ThrowIfCancellationRequested();
            string fromLabel = RefitCore.LabelFrom(c.Entry.Name, [c.Pick!.Option.Label]);
            if (!group.Save(target.Dir, fromLabel,
                            [new BodyRetargetWriter.Refit(option, made!.Saved!.Model, made.Target.Label,
                                                          Materials: made.Saved.Materials)],
                            sourceOption: null, notes, manipulations: MensManipulations(job.Route, job.Est, save)))
            {
                Out(SizeStatus.Failed, Codes.SaveFailed, notes[^1], target.Dir);
                continue;
            }
            Out(SizeStatus.Made, null, null, target.Dir, fromLabel,
                made.Plan is { } p ? Measures.Of(p, made.Folds, made.Landed?.VertexCoverage)
                                   : new Measures(made.Folds, null, null, null, null, null, null, made.Landed?.VertexCoverage),
                c.Option);
            saved++;
            warnings.AddRange(said);
            if (c.OtherBody)
                warnings.Add(string.Format(Strings.SizeSet.MadeOnOtherBodyFmt, piece.Label, c.Name, c.Entry.Name,
                                           sources.MadeOnName ?? request.MadeOn));
            // The first piece of an outfit to make an option leads it; legs following the chest never do.
            if (!piece.FollowsChest && c.Option != null) leadSources.TryAdd(leadKey, c.Option);
            if (OtherVariants(piece, c.File, c.Pick, preferred, readings, madeOnReadings, request.MadeOn) is { Count: > 0 })
                warnings.Add(string.Format(Strings.SizeSet.OneVariantFmt, piece.Label, c.Name));
        }
        group.RestoreDefault();
        return saved;
    }

    /// <summary>
    /// For a men's size, its body's nude smallclothes among <paramref name="men"/> (<see cref="SupportedBodies.Size.NudeRel"/>),
    /// tried by the job when the SFW ones match too little; null for a size that has none. A body installed without them
    /// gives nothing, and says so when asked.
    /// </summary>
    internal static Func<BodyOption, BodyOption?>? NudeFor(IReadOnlyList<BodyOption> men, SupportedBodies.Size size,
                                                            IPluginLog log)
    {
        if (size.NudeRel is not { } rel) return null;
        return asked =>
        {
            var nude = OptionByFile(men, rel);
            if (nude == null)
                log.Information("[Proteus] size set: {0} has no nude smallclothes legs at {1}; its SFW ones only",
                                asked.Label, rel);
            return nude;
        };
    }

    /// <summary>What the log says of a men's version fitted to its body's nude smallclothes instead: onto which, what the
    /// SFW ones matched, and how many of the nude ones' own skin triangles the garment shows. "" for any other.</summary>
    private static string NudeSaid(CrossSexRefitJob.Size made, string askedPath)
        => made.Asked == null ? ""
         : $" on {made.Target.Label} (the SFW legs matched {made.AskedLanded?.VertexCoverage:P1}; " +
           $"{CrossSexRefit.NudeSkinShown(made.Saved?.Model, askedPath)?.ToString() ?? "unknown"} of its own skin triangles shown)";

    /// <summary>
    /// What a men's version saved at <paramref name="save"/> carries: the EQDP entry that makes the game load a model
    /// there, and — when the men's wearer needs the extra skeleton the item loads for the women — that Est entry for
    /// the men too, as the Body size panel's "Refit for" saves it.
    /// </summary>
    internal static List<object> MensManipulations(RacialModelBake.Route route, EstPlan? est, string save)
    {
        var manipulations = new List<object>();
        if (route.Eqdp(save) is { } eqdp) manipulations.Add(eqdp);
        if (est is { Outcome: EstOutcome.Carry } carry && EstTable.EstSlot(BodySizeCatalog.SlotOf(save) ?? "") is { } slot
            && PenumbraManipulations.ParseSetId(save, 'e') is { } set)
            manipulations.Add(PenumbraManipulations.EstManipulation($"{route.To:D4}", slot, set, carry.Entry));
        return manipulations;
    }

    /// <summary>
    /// The author's other options of a piece read as made on the very body <paramref name="chosen"/> was — variants of
    /// one size (Sheer Elegance's "SFW" and "NSFW"), where a men's body has one size: men get the one it was cut from.
    /// </summary>
    /// <param name="readings">What each of the author's files was read as this run (see <see cref="ReadingKey"/>).</param>
    /// <param name="madeOnReadings">What the ones those could not place were read as on the <paramref name="madeOn"/>
    /// body.</param>
    internal static List<string> OtherVariants(Piece piece, string chosenFile, AutoRefitDecisions.Pick chosen,
                                               string preferred, IReadOnlyDictionary<string, AutoRefitDecisions.Pick?> readings,
                                               IReadOnlyDictionary<string, SourceReading>? madeOnReadings = null,
                                               string? madeOn = null)
    {
        AutoRefitDecisions.Pick? ReadAs(string file)
        {
            readings.TryGetValue(ReadingKey(file, piece.Slot, piece.Race, preferred), out var p);
            if (p != null && AutoRefitDecisions.Proceed(p.Confidence)) return p;
            return madeOn != null && madeOnReadings != null
                   && madeOnReadings.TryGetValue(ReadingKey(file, piece.Slot, piece.Race, preferred, madeOn), out var r)
                ? r.Pick
                : p;
        }
        return piece.Options.Where(o => o.Option != null && RefitCore.Normal(o.File) != RefitCore.Normal(chosenFile)
                                        && ReadAs(o.File) is { } p
                                        && string.Equals(p.Dir, chosen.Dir, StringComparison.OrdinalIgnoreCase)
                                        && RefitCore.Normal(p.Option.Rel) == RefitCore.Normal(chosen.Option.Rel))
                            .Select(o => o.Option!).ToList();
    }

    private static byte[]? ReadModFile(string root, string rel)
    {
        try { return File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, rel)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
