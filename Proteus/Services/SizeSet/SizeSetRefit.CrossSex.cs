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
    /// has men's, and the men's again "for men" when it has women's chest or legs pieces to cross (<see cref="AcrossPieces"/>).
    /// </summary>
    internal static IReadOnlyList<Choice> Offered(IReadOnlyList<Piece> pieces)
    {
        var races = pieces.Select(p => p.Race).ToHashSet(StringComparer.Ordinal);
        bool across = AcrossPieces(pieces).Count > 0;
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
    /// The women's chest and legs pieces that get men's versions: Midlander women's (<see cref="CrossSexRefit.Supported"/>),
    /// and only those the author made no men's model of — that one is sized onto the men's bodies as it is.
    /// </summary>
    internal static IReadOnlyList<Piece> AcrossPieces(IReadOnlyList<Piece> pieces)
    {
        var paths = pieces.Select(p => p.GamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return pieces.Where(p => p.Race == "0201" && CrossSexRefit.Supported(CrossSexRefit.Women, CrossSexRefit.Men, p.Slot)
                                 && !paths.Contains(RacialModelBake.WithRace(p.GamePath, CrossSexRefit.Men)))
                     .ToList();
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
        if (!CrossSexRefit.CoverageAllowed(coverage))
            return (false, Refused(string.Format(Strings.Parts.RetargetRefuseCrossSexCoverageFmt,
                                                 coverage.ToString("P0", System.Globalization.CultureInfo.CurrentCulture),
                                                 CrossSexRefit.MinCoverage.ToString("P0",
                                                     System.Globalization.CultureInfo.CurrentCulture))), []);

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
    /// </summary>
    internal static string MensGroupName(string modRoot, IReadOnlyList<Piece> across)
    {
        var saves = across.Select(p => RacialModelBake.WithRace(p.GamePath, CrossSexRefit.Men))
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var record = BodyRetargetWriter.ReadRecord(modRoot);
        var groups = PenumbraModMeta.TryReadGroups(modRoot)?.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (record?.Options ?? []).Where(e => !e.InAuthorGroup && saves.Contains(e.GamePath))
                                      .Select(e => record!.GroupOf(e))
                                      .FirstOrDefault(g => g.Length > 0 && (groups?.Contains(g) ?? false))
               ?? Strings.SizeSet.MensGroup;
    }

    /// <summary>
    /// One of the author's sizes made ready to cross: its wearer built, and the body it was made on bent for a man the
    /// way the garment is. What the nearest is chosen among, and what its job is given.
    /// </summary>
    private sealed record Ready(Candidate Candidate, OtherRaceWearer.Inputs Wearer, string? SkeletonFrom,
                                BodySizeCatalog MadeFor, string BentPath);

    private static int RunAcross(Request request, IReadOnlyList<Piece> pieces, UVRemapService? uvRemap, IPluginLog log,
                                 Action<string> progress, List<string> notes, List<string> warnings, CancellationToken ct)
    {
        var mensKeys = MensKeys(request.Keys);
        if (request.Across is not { } inputs || mensKeys.Count == 0) return 0;
        if (request.Ends && pieces.Any(p => p.Race == "0201" && IsEnd(p.Slot))) notes.Add(Strings.SizeSet.EndsNotForMen);
        var across = AcrossPieces(pieces);
        if (across.Count == 0) return 0;

        string groupName = MensGroupName(request.ModRoot, across);
        if (BodyRetargetWriter.IsAuthorGroup(request.ModRoot, groupName))
        {
            notes.Add(string.Format(Strings.SizeSet.MensGroupTakenFmt, groupName));
            return 0;
        }
        // Without the maps no body is matched across the sexes: one note, nothing bent or written.
        if (uvRemap == null || !uvRemap.MenMapsPresent())
        {
            uvRemap?.RequestMenMaps();
            notes.Add(Strings.SizeSet.MenMapsMissing);
            log.Information("[Proteus] size set: no men's versions: the body maps are not here yet");
            return 0;
        }
        if (request.ReadGameFile == null)
        {
            log.Information("[Proteus] size set: no men's versions: the game's data is not readable here");
            return 0;
        }

        int made = 0;
        var readings = new Dictionary<string, AutoRefitDecisions.Pick?>(StringComparer.Ordinal);
        var leadSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in across)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                made += RunAcrossPiece(request, inputs, piece, groupName, mensKeys, uvRemap, log, progress, notes, warnings,
                                       readings, leadSources, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                log.Error(e, "[Proteus] size set: {0} for men failed", piece.GamePath);
                notes.Add(string.Format(Strings.SizeSet.PieceFailedFmt, piece.Label, e.Message));
            }
        }
        return made;
    }

    private static int RunAcrossPiece(Request request, AcrossInputs inputs, Piece piece, string groupName,
                                      IReadOnlyList<string> mensKeys, UVRemapService uvRemap, IPluginLog log,
                                      Action<string> progress, List<string> notes, List<string> warnings,
                                      Dictionary<string, AutoRefitDecisions.Pick?> readings,
                                      Dictionary<string, string> leadSources, CancellationToken ct)
    {
        string root = request.ModRoot;
        void Note(string note) => log.Information("[Proteus] size set: {0} for men: {1}", piece.Label, note);
        progress(string.Format(Strings.SizeSet.ReadingFmt, piece.Label));
        var mods = request.Bodies.Select(b => (b.Dir, b.Catalog)).ToList();
        if (mods.Count == 0)
        {
            notes.Add(Strings.SizeSet.NoBodies);
            return 0;
        }
        var clock = Stopwatch.StartNew();
        using var reading = BodySizeMatch.BeginReading();
        string preferred = mods[0].Dir;
        var sources = new Sources(request, piece, preferred, mods, readings, Note, ct);
        if (sources.Primary == null)
        {
            notes.Add(string.Format(Strings.SizeSet.UnreadableFmt, piece.Label));
            return 0;
        }

        // ── which of the author's sizes may be crossed: made on a skin the maps join, its body bent for a man ──
        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        var groups = PenumbraModMeta.TryReadGroups(root);
        var defaultManipulations = PenumbraModMeta.TryReadDefaultData(root)?.Manipulations;
        string save = RacialModelBake.WithRace(piece.GamePath, CrossSexRefit.Men);
        var layouts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var ready = new Dictionary<Candidate, Ready?>();
        int bent = 0;
        byte[]? ReadGame(string path)
        {
            try { return request.ReadGameFile!(path); }
            catch (Exception e)
            {
                // Most paths asked for are only maybe there (an extra skeleton per entry).
                log.Debug("[Proteus] size set: the game gave nothing for {0}: {1}", path, e.Message);
                return null;
            }
        }
        string? LayoutOf(Candidate c)
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
                Note($"{Path.GetFileName(c.SourcePath)} could not be read ({e.GetType().Name})");
            }
            return layouts[c.SourcePath] = layout;
        }
        // Each size its own wearer: what the option it is cut from carries (an extra skeleton, Est entries) is what the
        // men's version loads, and the bend of its body is made with the same wearer the job bends the garment with.
        Ready? Prepare(Candidate c)
        {
            if (ready.TryGetValue(c, out var known)) return known;
            ready[c] = null;
            if (!AcrossEligible(LayoutOf(c)))
            {
                Note($"{c.Name}: made on a {LayoutOf(c) ?? "unreadable"} skin, which no map joins to a man's");
                return null;
            }
            string? cutFrom = piece.Group != null && c.Option != null ? $"{piece.Group} / {c.Option}" : null;
            var wearer = OtherRaceWearer.Gather(CrossSexRefit.Men, piece.GamePath, ReadGame, inputs.ResolveCollection,
                path => OtherRaceRefit.CarriedFile(redirects, cutFrom, path) is { } rel ? ReadModFile(root, rel) : null,
                OtherRaceRefit.EstEntriesFor(groups, defaultManipulations, cutFrom),
                (file, e) => log.Warning(e, "[Proteus] size set: could not read {0}", file));
            if (ModelSkinReader.Read(c.Bytes, null, null) is not { } skin)
            {
                Note($"{c.Name}: its rig could not be read");
                return null;
            }
            var made = OtherRaceWearer.Build(wearer, skin);
            if (made.Wearer is not { } who)
            {
                Note($"{c.Name}: no man's wearer: {made.Refusal}");
                return null;
            }
            if (made.FellBack) Note("the collection's men's skeleton could not be read; the game's was used");
            var madeFor = c.Entry.Catalog with { Options = [c.Pick!.Option] };
            var bentCat = BakedBodyCatalog.Lowered(madeFor, CrossSexRefit.Women, CrossSexRefit.Men, who, [piece.Slot],
                                                   out var failed, byAlias: true, made.Planner, Note);
            if (bentCat.For(piece.Slot).FirstOrDefault() is not { } bentBody)
            {
                Note($"{c.Name}: its body could not be bent for a man: {string.Join("; ", failed)}");
                return null;
            }
            bent++;
            return ready[c] = new Ready(c, wearer, made.FellBack ? null : wearer.ModdedFrom, madeFor, bentCat.PathOf(bentBody));
        }

        // The nearest is read between bodies alone: the garment's own skin is a women's, and no man's skin is near it.
        var probes = new Dictionary<string, System.Numerics.Vector3[]>(StringComparer.OrdinalIgnoreCase);
        System.Numerics.Vector3[] ProbesOf(string path) => probes.TryGetValue(path, out var p) ? p : probes[path] = BodyProbes(path, Note);
        (Ready Chosen, string Why)? Nearest(string targetPath, string option)
        {
            var said = new List<string>();
            var may = sources.Choices.Select(c => (c, r: Prepare(c))).Where(x => x.r != null).Select(x => x.r!).ToList();
            var alike = RiggedAlike(may, r => r.Candidate.Yas, targetYas: false);
            foreach (var r in may.Except(alike)) said.Add($"{r.Candidate.Name}: rigged for YAS");
            var eligible = new List<(Ready, SourceDistance)>();
            foreach (var r in alike)
            {
                var (p90, mean) = BodyDistance(r.BentPath, targetPath, ProbesOf);
                var d = new SourceDistance(p90, mean, 0f, 0f, r.Candidate.Order);
                eligible.Add((r, d));
                said.Add($"{r.Candidate.Name}: {Describe(d)}");
            }
            if (eligible.Count == 0) return null;
            var (chosen, step, gap) = Choose(eligible);
            return (chosen, $"{option} from {chosen.Candidate.Name} (by {step}, {gap * 1000f:F1} mm ahead; {string.Join("; ", said)})");
        }

        BodySizeMatch.NeedNear(sources.Choices.Count + 2 * mensKeys.Count);
        using var group = new SizeGroup(root, piece, groupName, save);
        int ownSaved = 0;
        bool anySource = true;
        foreach (string key in mensKeys)
        {
            ct.ThrowIfCancellationRequested();
            if (AcrossTarget(key, piece.Slot) is not { } at) continue;
            var (body, size, rel) = at;
            string option = SupportedBodies.OptionName(body, size);
            if (SupportedBodies.Find(body, request.Bodies) is not { } target)
            {
                notes.Add(string.Format(Strings.SizeSet.NotInstalledFmt, piece.Label, body.Label));
                continue;
            }
            if (group.AlreadyThere(option, request.Remake))
            {
                log.Information("[Proteus] size set: {0} for men: \"{1}\" already there", piece.Label, option);
                continue;
            }
            var men = OtherRaceRefit.OnlyRace(target.Catalog, CrossSexRefit.Men);
            if (OptionByFile(men.For(piece.Slot), rel) is not { } onto)
            {
                notes.Add(string.Format(Strings.SizeSet.NoSuchSizeFmt, piece.Label, option, target.Name));
                continue;
            }
            string targetPath = men.PathOf(onto);

            // ── which of the author's sizes: the one the chest's men's version was, else the nearest ──
            string leadKey = LeadKey(groupName, "0101", option);
            Ready? chosen = null;
            if (piece.FollowsChest && leadSources.TryGetValue(leadKey, out var leadOption) && piece.Group != null)
            {
                var c = ResolveOptionFile(redirects, piece.GamePath, piece.Group, leadOption) is { } file
                    ? sources.ByFile(leadOption, file)
                    : null;
                chosen = c != null ? Prepare(c) : null;
                Note(chosen != null ? $"{option} from \"{leadOption}\", as the chest's was"
                                    : $"{option}: not cut from \"{leadOption}\" as the chest's was; the nearest instead");
            }
            else if (piece.FollowsChest)
                Note($"{option}: the chest got no men's version from this run to follow; the nearest instead");
            if (chosen == null)
            {
                if (Nearest(targetPath, option) is not { } nearest)
                {
                    anySource = false;
                    break;
                }
                chosen = nearest.Chosen;
                Note(nearest.Why);
            }

            progress(string.Format(Strings.SizeSet.WorkingFmt, piece.Label, option));
            var started = clock.ElapsedMilliseconds;
            var c0 = chosen.Candidate;
            var job = CrossSexRefitJob.Run(new CrossSexRefitJob.Inputs(
                c0.Bytes, piece.GamePath, CrossSexRefit.Men, chosen.MadeFor, null, men,
                o => RefitCore.Normal(o.Rel) == RefitCore.Normal(rel), chosen.Wearer, uvRemap, log,
                CarryPiercings: request.Piercings));
            var made = job.Sizes.FirstOrDefault();
            var (ok, refusal, said) = Judge(piece.Label, option, job, made, chosen.SkeletonFrom);
            log.Information("[Proteus] size set: {0} for men: {1} in {2} ms (source in {3} ms, {4} bodies bent): {5}",
                            piece.Label, option, clock.ElapsedMilliseconds - started, started, bent,
                            ok ? $"{made!.Folds} folded, {made.Landed?.VertexCoverage:P1} matched" : refusal ?? "");
            if (!ok)
            {
                notes.Add(refusal!);
                continue;
            }

            ct.ThrowIfCancellationRequested();
            if (!group.Save(target.Dir, RefitCore.LabelFrom(c0.Entry.Name, [c0.Pick!.Option.Label]),
                            [new BodyRetargetWriter.Refit(option, made!.Saved!.Model, onto.Label, Materials: made.Saved.Materials)],
                            sourceOption: null, notes, manipulations: MensManipulations(job.Route, job.Est, save)))
                continue;
            ownSaved++;
            warnings.AddRange(said);
            if (!piece.FollowsChest && c0.Option != null) leadSources[leadKey] = c0.Option;
            if (OtherVariants(piece, c0.File, c0.Pick, preferred, readings) is { Count: > 0 })
                warnings.Add(string.Format(Strings.SizeSet.OneVariantFmt, piece.Label, c0.Name));
        }
        if (!anySource) notes.Add(string.Format(Strings.SizeSet.NoSourceForMenFmt, piece.Label));
        group.RestoreDefault();
        return ownSaved;
    }

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
    internal static List<string> OtherVariants(Piece piece, string chosenFile, AutoRefitDecisions.Pick chosen,
                                               string preferred, IReadOnlyDictionary<string, AutoRefitDecisions.Pick?> readings)
        => piece.Options.Where(o => o.Option != null && RefitCore.Normal(o.File) != RefitCore.Normal(chosenFile)
                                    && readings.TryGetValue(ReadingKey(o.File, piece.Slot, piece.Race, preferred), out var p)
                                    && p != null
                                    && string.Equals(p.Dir, chosen.Dir, StringComparison.OrdinalIgnoreCase)
                                    && RefitCore.Normal(p.Option.Rel) == RefitCore.Normal(chosen.Option.Rel))
                        .Select(o => o.Option!).ToList();

    private static byte[]? ReadModFile(string root, string rel)
    {
        try { return File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, rel)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
