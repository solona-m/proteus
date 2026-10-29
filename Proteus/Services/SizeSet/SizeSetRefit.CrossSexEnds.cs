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
/// Women's shoes and gloves made men's in the size set: refitted for Midlander men through their leg or arm
/// (<see cref="CrossSexRefitJob.RunEnd"/>), saved into the men's group's option beside the chest and legs.
/// </summary>
internal static partial class SizeSetRefit
{
    /// <summary>
    /// How far, on average, a skinless shoe's or glove's cloth may sit off the leg or arm it was read on before Check in
    /// game says the body may not be the one it was made on (5 mm). Measured as the mean gap (<see
    /// cref="BodySizeMatch.ClothAgainst"/>), which runs under the worst points: the test project's
    /// <c>RefitGate.StandoffSlack</c>, the 8 mm it allows a refit's standoff against the made-for body's, taken down for
    /// a mean.
    /// </summary>
    internal const float EndClothGapWarn = 0.005f;

    /// <summary>Which body a women's shoe or glove is made a men's from — see <see cref="EndMadeFor"/>.</summary>
    internal enum EndMadeForChoice
    {
        /// <summary>Its leg's or arm's own reading, skin or cloth, on a body whose layout the maps join to a man's.</summary>
        Read,
        /// <summary>No skin to read: the body the user says it was made on, its cloth sitting there.</summary>
        MadeOn,
        /// <summary>No skin to read: the installed women's body its cloth sits on.</summary>
        Cloth,
        /// <summary>No skin to read and no body its cloth sits on: the game's own women's body.</summary>
        GameBody,
        /// <summary>Read on a body no map joins to a man's (gen3), or nothing to make it from.</summary>
        NoSource,
        /// <summary>Its reading was too weak to make it from.</summary>
        Unreadable,
    }

    /// <summary>
    /// Which body a women's shoe or glove is made a men's from. One read off its own skin crosses when that body's layout
    /// pairs with a man's. One with no leg or arm skin to read (a skinless boot, or a shoe whose foot alone is skin) is
    /// read, in turn, on the "Made on" body, on whichever installed women's body its cloth sits on, and on the game's own
    /// women's body.
    /// </summary>
    /// <param name="route">How <see cref="KeepEnds.Find"/> read it.</param>
    /// <param name="layout">The layout of the body its skin was read on; null for none.</param>
    /// <param name="madeOnFits">The "Made on" body is set, installed, pairs with a man's, and its cloth sits there.</param>
    /// <param name="clothFits">An installed women's body that pairs with a man's has its cloth sitting on it.</param>
    /// <param name="gameBody">The game's own women's body can be read.</param>
    internal static EndMadeForChoice EndMadeFor(KeepEnds.Route route, string? layout, bool madeOnFits, bool clothFits,
                                                bool gameBody)
        => route switch
        {
            KeepEnds.Route.Skin or KeepEnds.Route.Cloth or KeepEnds.Route.Sibling
                => AcrossEligible(layout) ? EndMadeForChoice.Read : EndMadeForChoice.NoSource,
            KeepEnds.Route.SkinOnly when !AcrossEligible(layout) => EndMadeForChoice.NoSource,
            KeepEnds.Route.SkinOnly or KeepEnds.Route.NoSkin
                => madeOnFits ? EndMadeForChoice.MadeOn
                 : clothFits ? EndMadeForChoice.Cloth
                 : gameBody ? EndMadeForChoice.GameBody
                 : EndMadeForChoice.NoSource,
            _ => EndMadeForChoice.Unreadable,
        };

    /// <summary>What Check in game says of a men's shoe or glove for the body it was made from: that it was refit as made
    /// on the game's own body, or that its cloth sits far off the leg or arm it was read on.</summary>
    /// <param name="gap">How far its cloth sits off that leg or arm, on average; null when not measured.</param>
    internal static IReadOnlyList<string> EndWarnings(string piece, string option, EndMadeForChoice choice, float? gap,
                                                      string? bodyName)
    {
        var warnings = new List<string>();
        if (choice == EndMadeForChoice.GameBody)
            warnings.Add(string.Format(Strings.SizeSet.EndFromGameBodyFmt, piece, option));
        else if (choice is EndMadeForChoice.MadeOn or EndMadeForChoice.Cloth && gap > EndClothGapWarn)
            warnings.Add(string.Format(Strings.SizeSet.EndClothGapFmt, piece, option, gap!.Value * 1000f, bodyName));
        return warnings;
    }

    /// <summary>The men's body and size a men's shoe or glove goes onto: its leg's or arm's (<see cref="AcrossTarget"/>).</summary>
    internal static (SupportedBodies.Body Body, SupportedBodies.Size Size, string Rel)? AcrossEndTarget(string key, string slot)
        => AutoRefitDecisions.LimbOf(slot) is { } limb ? AcrossTarget(key, limb) : null;

    /// <summary>A shoe or glove read for its men's versions: its bytes, its reading, and the body it is made from.</summary>
    private sealed record MensEndSource(string? Option, byte[] Bytes, KeepEnds.Reading Reading, BodySizeCatalog MadeFor,
                                        Func<BodyOption, bool>? FromPick, EndMadeForChoice Choice, string BodyName,
                                        string FromLabel, float? Gap);

    private static int RunAcrossEnd(Request request, AcrossInputs inputs, Piece piece, string groupName,
                                    IReadOnlyList<string> mensKeys, UVRemapService uvRemap, IPluginLog log,
                                    Action<string> progress, RunLog runLog, Dictionary<string, string> leadSources,
                                    CancellationToken ct)
    {
        var notes = runLog.Notes;
        var warnings = runLog.Warnings;
        string root = request.ModRoot;
        string slot = piece.Slot;
        string limb = AutoRefitDecisions.LimbOf(slot)!;
        string save = RacialModelBake.WithRace(piece.GamePath, CrossSexRefit.Men);
        void Note(string note) => log.Information("[Proteus] size set: {0} for men: {1}", piece.Label, note);
        progress(string.Format(Strings.SizeSet.ReadingFmt, piece.Label));

        var women = request.Bodies.Where(b => b.Catalog.For(limb, "0201").Count > 0).ToList();
        var game = request.GameBody?.Invoke("0201") is { IsBody: true } g ? g : null;
        using var nearness = BodySizeMatch.BeginReading();

        // The author's file for an option, read: by the lead's option when it follows one, else the first that reads.
        var sources = new Dictionary<string, MensEndSource?>(StringComparer.Ordinal);
        string? unreadable = null;
        MensEndSource? Read(string? option, string file)
        {
            string key = RefitCore.Normal(file);
            if (sources.TryGetValue(key, out var known)) return known;
            sources[key] = null;
            string path = BodySizeCatalog.ResolveCaseInsensitive(root, file);
            if (!File.Exists(path) || File.ReadAllBytes(path) is not { } bytes || ModelPartReader.Read(bytes) is not { } garment
                || KeepEnds.Read(slot, garment, bytes) is not { } reading)
                return null;
            return sources[key] = MadeFrom(request, piece, option, bytes, garment, reading, women, game, Note,
                                           out unreadable);
        }

        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        var groups = PenumbraModMeta.TryReadGroups(root);
        var defaults = PenumbraModMeta.TryReadDefaultData(root)?.Manipulations;
        byte[]? ReadGame(string p)
        {
            try { return request.ReadGameFile!(p); }
            catch (Exception e)
            {
                log.Debug("[Proteus] size set: the game gave nothing for {0}: {1}", p, e.Message);
                return null;
            }
        }

        var clock = Stopwatch.StartNew();
        using var group = new SizeGroup(root, piece, groupName, save);
        int saved = 0;
        foreach (string key in mensKeys)
        {
            ct.ThrowIfCancellationRequested();
            if (AcrossEndTarget(key, slot) is not { } at) continue;
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
                Note($"\"{option}\" already there");
                AlreadyOut(group, option, (code, why) => Out(SizeStatus.Skipped, code, why, target.Dir));
                continue;
            }
            var men = OtherRaceRefit.OnlyRace(target.Catalog, CrossSexRefit.Men);
            if (OptionByFile(men.For(limb), rel) is null)
            {
                string left = string.Format(Strings.SizeSet.NoSuchSizeFmt, piece.Label, option, target.Name);
                notes.Add(left);
                Out(SizeStatus.Skipped, Codes.NoSuchSize, left, target.Dir);
                continue;
            }

            // Cut from the lead's option when it follows a piece that made this option, else the first that reads.
            string leadKey = AcrossLeadKey(piece, groupName, option);
            MensEndSource? src = null;
            if ((piece.FollowsChest || piece.FollowsLegs) && leadSources.TryGetValue(leadKey, out var leadOption)
                && piece.Options.FirstOrDefault(o => o.Option == leadOption) is { File: { } leadFile })
            {
                src = Read(leadOption, leadFile);
                Note(src != null ? $"{option} from \"{leadOption}\", as its lead's was"
                                 : $"{option}: not cut from \"{leadOption}\" as its lead's was; the first that reads instead");
            }
            foreach (var (o, file) in piece.Options)
            {
                if (src != null) break;
                ct.ThrowIfCancellationRequested();
                src = Read(o, file);
            }
            if (src == null)
            {
                string left = unreadable ?? string.Format(Strings.SizeSet.EndsUnreadableFmt, piece.Label);
                notes.Add(left);
                Out(SizeStatus.Skipped, unreadable != null ? Codes.NoSourceMen : Codes.EndsUnreadable, left, target.Dir);
                continue;
            }

            string? cutFrom = piece.Group != null && src.Option != null ? $"{piece.Group} / {src.Option}" : null;
            var wearer = OtherRaceWearer.Gather(CrossSexRefit.Men, piece.GamePath, ReadGame, inputs.ResolveCollection,
                p => OtherRaceRefit.CarriedFile(redirects, cutFrom, p) is { } carried ? ReadModFile(root, carried) : null,
                OtherRaceRefit.EstEntriesFor(groups, defaults, cutFrom),
                (file, e) => log.Warning(e, "[Proteus] size set: could not read {0}", file));

            progress(string.Format(Strings.SizeSet.WorkingFmt, piece.Label, option));
            long started = clock.ElapsedMilliseconds;
            var job = CrossSexRefitJob.RunEnd(new CrossSexRefitJob.Inputs(
                src.Bytes, piece.GamePath, CrossSexRefit.Men, src.MadeFor, src.FromPick, men,
                o => RefitCore.Normal(o.Rel) == RefitCore.Normal(rel), wearer, uvRemap, log,
                CarryPiercings: request.Piercings, Nude: slot == "_sho" ? NudeFor(men.For(limb), size, log) : null));
            var made = job.Sizes.FirstOrDefault();
            var (ok, refusal, said) = Judge(piece.Label, option, job, made, wearer.ModdedFrom);
            log.Information("[Proteus] size set: {0} for men: {1} in {2} ms from {3}: {4}", piece.Label, option,
                            clock.ElapsedMilliseconds - started, src.FromLabel,
                            ok ? $"{made!.Folds} folded, {made.Landed?.VertexCoverage:P1} matched" : refusal ?? "");
            if (!ok)
            {
                notes.Add(refusal!);
                Out(SizeStatus.Refused, Codes.RefusedMen, refusal, target.Dir);
                continue;
            }

            ct.ThrowIfCancellationRequested();
            if (!group.Save(target.Dir, src.FromLabel,
                            [new BodyRetargetWriter.Refit(option, made!.Saved!.Model, made.Target.Label,
                                                          Materials: made.Saved.Materials)],
                            sourceOption: null, notes, manipulations: MensManipulations(job.Route, job.Est, save)))
            {
                Out(SizeStatus.Failed, Codes.SaveFailed, notes[^1], target.Dir);
                continue;
            }
            Out(SizeStatus.Made, null, null, target.Dir, src.FromLabel,
                made.Plan is { } p ? Measures.Of(p, made.Folds, made.Landed?.VertexCoverage)
                                   : new Measures(made.Folds, null, null, null, null, null, null, made.Landed?.VertexCoverage),
                src.Option);
            saved++;
            warnings.AddRange(said);
            warnings.AddRange(EndWarnings(piece.Label, option, src.Choice, src.Gap, src.BodyName));
        }
        group.RestoreDefault();
        return saved;
    }

    /// <summary>
    /// The body one of the author's shoes or gloves is made a men's from (<see cref="EndMadeFor"/>): its own reading's,
    /// or for one with no leg or arm skin, the "Made on" body's, the installed body's its cloth sits on, or the game's.
    /// Null, with <paramref name="refused"/> said, when it may not be made a men's.
    /// </summary>
    private static MensEndSource? MadeFrom(Request request, Piece piece, string? option, byte[] bytes, ModelParts garment,
                                          KeepEnds.Reading reading, IReadOnlyList<BodyModIndex.Entry> women,
                                          BodySizeCatalog? game, Action<string> note, out string? refused)
    {
        refused = null;
        string limb = reading.Limb;
        var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
        var mods = women.Select(b => (b.Dir, b.Catalog)).ToList();
        if (game != null) mods.Add((VanillaBodyCatalog.Key, game));
        BodySizeCatalog? CatalogOf(string dir)
            => dir == VanillaBodyCatalog.Key ? game
             : women.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase))?.Catalog;
        string NameOf(string dir)
            => dir == VanillaBodyCatalog.Key ? Strings.Parts.RetargetFromVanilla
             : women.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase))?.Name ?? dir;
        string preferred = mods.Count > 0 ? mods[0].Dir : VanillaBodyCatalog.Key;
        var found = KeepEnds.Find(reading, garment, bones, "0201",
                                  (view, s) => RefitCore.DetectSource(view, bones, s, "0201", mods, preferred,
                                                                      dir => dir != VanillaBodyCatalog.Key, note),
                                  CatalogOf);
        string? LayoutOf(BodySizeCatalog catalog, BodyOption o)
        {
            try
            {
                string path = catalog.PathOf(o);
                return File.Exists(path) && ModelPartReader.Read(File.ReadAllBytes(path)) is { } body
                    ? BodyCorrespondence.LayoutOf(body) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or ArgumentException or IndexOutOfRangeException)
            {
                return null;
            }
        }
        // The best size of a body for the leg's or arm's cloth, with how far it sits off it; null when it does not sit on
        // it — too little cloth near it, or too much inside — or the body pairs with no man's.
        (BodyOption Option, float Gap)? ClothOn(BodySizeCatalog catalog)
        {
            var options = catalog.For(limb, "0201");
            if (options.Count == 0 || BodySizeMatch.RankByCloth(reading.LimbView, options, catalog.PathOf) is not
                { Best: { } best }) return null;
            if (!AcrossEligible(LayoutOf(catalog, best.Option))) return null;
            if (BodySizeMatch.ClothAgainst(reading.LimbView, catalog.PathOf(best.Option)) is not { } on
                || on.Near < BodySizeMatch.MinEvidence || on.Inside > on.Near * KeepEnds.SiblingInsideAtMost) return null;
            return (best.Option, on.Gap);
        }

        string? readLayout = null;
        if (found.Route is KeepEnds.Route.Skin or KeepEnds.Route.Cloth or KeepEnds.Route.Sibling or KeepEnds.Route.SkinOnly
            && found.Pick is { } pick && CatalogOf(pick.Dir) is { } readCatalog)
            readLayout = LayoutOf(readCatalog, pick.Option);
        var madeOn = request.MadeOn is { } m
            ? women.FirstOrDefault(b => string.Equals(b.Dir, m, StringComparison.OrdinalIgnoreCase)) : null;
        var madeOnCloth = madeOn != null ? ClothOn(madeOn.Catalog) : null;
        (BodyModIndex.Entry Entry, BodyOption Option, float Gap)? byCloth = null;
        if (found.Route is KeepEnds.Route.NoSkin or KeepEnds.Route.SkinOnly && madeOnCloth == null)
            foreach (var mod in women)
                if (ClothOn(mod.Catalog) is { } on && (byCloth == null || on.Gap < byCloth.Value.Gap))
                    byCloth = (mod, on.Option, on.Gap);

        var choice = EndMadeFor(found.Route, readLayout, madeOnCloth != null, byCloth != null, game != null);
        note($"{option ?? "its file"}: route {KeepEnds.Letter(found.Route)}, made a men's from {choice}");
        // The made-for body narrowed to the size read and its own hand or foot: the job bends what it is given, and a
        // bent size is known by its new file, not the author's (as the legs' sources are handed over, MensSources).
        BodySizeCatalog Narrow(BodySizeCatalog catalog, BodyOption o)
            => catalog with
            {
                Options = AutoRefitDecisions.Companion(catalog.For(reading.Slot, "0201"), o, null) is { } end ? [o, end] : [o],
            };
        switch (choice)
        {
            case EndMadeForChoice.Read:
                var p = found.Pick!;
                return new MensEndSource(option, bytes, reading, Narrow(CatalogOf(p.Dir)!, p.Option), null, choice, NameOf(p.Dir),
                                         RefitCore.LabelFrom(NameOf(p.Dir), [p.Option.Label]), null);
            case EndMadeForChoice.MadeOn:
                var (mo, mg) = madeOnCloth!.Value;
                return new MensEndSource(option, bytes, reading, Narrow(madeOn!.Catalog, mo), null, choice, madeOn.Name,
                                         RefitCore.LabelFrom(madeOn.Name, [mo.Label]), mg);
            case EndMadeForChoice.Cloth:
                var (entry, co, cg) = byCloth!.Value;
                return new MensEndSource(option, bytes, reading, Narrow(entry.Catalog, co), null, choice, entry.Name,
                                         RefitCore.LabelFrom(entry.Name, [co.Label]), cg);
            case EndMadeForChoice.GameBody:
                string gameName = Strings.Parts.RetargetFromVanilla;
                var gameLimb = game!.For(limb, "0201").FirstOrDefault();
                return new MensEndSource(option, bytes, reading, game, null, choice, gameName,
                                         gameLimb != null ? RefitCore.LabelFrom(gameName, [gameLimb.Label]) : gameName, null);
            case EndMadeForChoice.NoSource:
                refused = string.Format(Strings.SizeSet.NoSourceForMenFmt, piece.Label);
                return null;
            default:
                refused = string.Format(Strings.SizeSet.UnreadableFmt, piece.Label);
                return null;
        }
    }
}
