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

    /// <summary>How near the nearest a body's cloth reading may be and still win for being listed first (0.5 mm) — see
    /// <see cref="NearestByCloth"/>.</summary>
    internal const float EndClothTie = 0.0005f;

    /// <summary>
    /// Which body a shoe's or glove's cloth sits on, by its gap to each (null for one it does not sit on): the first listed
    /// — the body preferred comes first — whose gap is within <paramref name="tie"/> of the nearest. -1 when it sits on
    /// none.
    /// </summary>
    internal static int NearestByCloth(IReadOnlyList<float?> gaps, float tie)
    {
        float nearest = float.PositiveInfinity;
        foreach (float? g in gaps)
            if (g is { } v && v < nearest) nearest = v;
        for (int i = 0; i < gaps.Count; i++)
            if (gaps[i] is { } v && v <= nearest + tie) return i;
        return -1;
    }

    /// <summary>Which body a women's shoe or glove is made a men's from — see <see cref="EndMadeFor"/>.</summary>
    internal enum EndMadeForChoice
    {
        /// <summary>Its leg's or arm's own reading, skin or cloth, on a body whose layout the maps join to a man's.</summary>
        Read,
        /// <summary>No skin to read: the body the user says it was made on, its cloth sitting there.</summary>
        MadeOn,
        /// <summary>No skin to read: the installed women's body its cloth sits on.</summary>
        Cloth,
        /// <summary>A shoe with no skin to read and no body its cloth sits on closely: the body the outfit's own legs were
        /// made on (<see cref="OutfitLegsOf"/>), read off the legs' skin.</summary>
        Outfit,
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
    /// <param name="layout">The layout of the body its skin was read on; null for none (a foot or hand alone may read
    /// none, and is then taken as skinless).</param>
    /// <param name="madeOnFits">The "Made on" body is set, installed, pairs with a man's, and its cloth sits there.</param>
    /// <param name="clothFits">An installed women's body that pairs with a man's has its cloth sitting on it.</param>
    /// <param name="gameBody">The game's own women's body can be read.</param>
    /// <param name="outfitFits">A skinless shoe's outfit legs read as a body that pairs with a man's (<see cref="OutfitLegsOf"/>):
    /// taken after the cloth and before the game's body, for <see cref="KeepEnds.Route.NoSkin"/> only.</param>
    internal static EndMadeForChoice EndMadeFor(KeepEnds.Route route, string? layout, bool madeOnFits, bool clothFits,
                                                bool gameBody, bool outfitFits = false)
        => route switch
        {
            KeepEnds.Route.Skin or KeepEnds.Route.Cloth or KeepEnds.Route.Sibling
                => AcrossEligible(layout) ? EndMadeForChoice.Read : EndMadeForChoice.NoSource,
            KeepEnds.Route.SkinOnly when layout != null && !AcrossEligible(layout) => EndMadeForChoice.NoSource,
            KeepEnds.Route.SkinOnly or KeepEnds.Route.NoSkin
                => madeOnFits ? EndMadeForChoice.MadeOn
                 : clothFits ? EndMadeForChoice.Cloth
                 : outfitFits && route == KeepEnds.Route.NoSkin ? EndMadeForChoice.Outfit
                 : gameBody ? EndMadeForChoice.GameBody
                 : EndMadeForChoice.NoSource,
            _ => EndMadeForChoice.Unreadable,
        };

    /// <summary>
    /// A women's shoe's outfit legs: the mod's own c0201 legs piece of the same item (the shoe's path with <c>_sho</c> as
    /// <c>_dwn</c>). One of <paramref name="pieces"/>, so always the author's — never a refit of ours or the game's own
    /// model. Null for anything but a c0201 shoe, or a mod with no such legs.
    /// </summary>
    internal static Piece? OutfitLegsOf(IReadOnlyList<Piece> pieces, Piece shoe)
    {
        if (shoe.Slot != "_sho" || shoe.Race != "0201") return null;
        string legs = shoe.GamePath[..^"_sho.mdl".Length] + "_dwn.mdl";
        return pieces.FirstOrDefault(p => p.Slot == "_dwn" && p.Race == "0201"
                                          && string.Equals(p.GamePath, legs, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Which of an outfit legs piece's options a skinless shoe takes its body from: read in order, the first read
    /// <see cref="BodySizeMatch.Confidence.Exact"/> on a body that pairs with a man's — reading stops there — else the first
    /// read on such a body at all; null when none is.
    /// </summary>
    /// <param name="read">Reads one option's file (cached by the caller); null when it reads as nothing.</param>
    /// <param name="eligible">Whether a reading's body pairs with a man's.</param>
    internal static ((string? Option, string File) Legs, AutoRefitDecisions.Pick Pick)? PickOutfitLegs(
        IReadOnlyList<(string? Option, string File)> options, Func<string, AutoRefitDecisions.Pick?> read,
        Func<AutoRefitDecisions.Pick, bool> eligible)
    {
        ((string?, string), AutoRefitDecisions.Pick)? first = null;
        foreach (var option in options)
        {
            if (read(option.File) is not { } pick || !eligible(pick)) continue;
            if (pick.Confidence == BodySizeMatch.Confidence.Exact) return (option, pick);
            first ??= (option, pick);
        }
        return first;
    }

    /// <summary>The key an outfit legs file's reading is kept by: exactly the one a crossed legs piece's own reading of the
    /// file is kept by (<see cref="RunAcrossPiece"/>: the first body mod preferred), so neither reads it twice.</summary>
    internal static string OutfitLegsKey(Request request, string legsFile)
        => ReadingKey(legsFile, "_dwn", "0201", request.Bodies.Count > 0 ? request.Bodies[0].Dir : "");

    /// <summary>
    /// An outfit legs file read as the size set reads a crossed legs piece (<see cref="RunAcrossPiece"/>): over every body
    /// mod given, the first preferred, cached in the run's <paramref name="readings"/> by the same key — read once a run.
    /// </summary>
    internal static AutoRefitDecisions.Pick? ReadOutfitLegs(Request request, string file,
                                                           Dictionary<string, AutoRefitDecisions.Pick?> readings,
                                                           Action<string> note)
    {
        var mods = request.Bodies.Select(b => (b.Dir, b.Catalog)).ToList();
        if (mods.Count == 0) return null;
        string preferred = mods[0].Dir;
        string key = OutfitLegsKey(request, file);
        if (readings.TryGetValue(key, out var known))
        {
            note($"its outfit's legs {file} were read before in this run");
            return known;
        }
        AutoRefitDecisions.Pick? pick = null;
        try
        {
            string path = BodySizeCatalog.ResolveCaseInsensitive(request.ModRoot, file);
            if (File.Exists(path) && File.ReadAllBytes(path) is { } bytes && ModelPartReader.Read(bytes) is { } legs)
                pick = RefitCore.DetectSource(legs, new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal),
                                              "_dwn", "0201", mods, preferred, _ => true, note);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                       or InvalidOperationException or ArgumentException)
        {
            note($"its outfit's legs {file} could not be read: {e.Message}");
        }
        return readings[key] = pick;
    }

    /// <summary>What Check in game says of a men's shoe or glove for the body it was made from: that it was refit as made
    /// on the game's own body, or that its cloth sits far off the leg or arm it was read on.</summary>
    /// <param name="gap">How far its cloth sits off that leg or arm, on average; null when not measured.</param>
    /// <param name="outfitLegs">With <see cref="EndMadeForChoice.Outfit"/>, the body and size read off the outfit's legs and
    /// the legs option read ("Bibo+ Type C - Small", "Small").</param>
    internal static IReadOnlyList<string> EndWarnings(string piece, string option, EndMadeForChoice choice, float? gap,
                                                      string? bodyName, (string From, string LegsOption)? outfitLegs = null)
    {
        var warnings = new List<string>();
        if (choice == EndMadeForChoice.GameBody)
            warnings.Add(string.Format(Strings.SizeSet.EndFromGameBodyFmt, piece, option));
        else if (choice == EndMadeForChoice.Outfit && outfitLegs is { } legs)
            warnings.Add(string.Format(Strings.SizeSet.EndFromOutfitLegsFmt, piece, option, legs.From, legs.LegsOption));
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
                                        string FromLabel, float? Gap, string? OutfitLegsOption = null);

    /// <param name="pieces">The mod's pieces this run: where a skinless shoe's outfit legs are found.</param>
    /// <param name="readings">The run's readings, shared with the crossed legs (<see cref="ReadOutfitLegs"/>).</param>
    private static int RunAcrossEnd(Request request, AcrossInputs inputs, Piece piece, string groupName,
                                    IReadOnlyList<string> mensKeys, UVRemapService uvRemap, IPluginLog log,
                                    Action<string> progress, RunLog runLog, Dictionary<string, string> leadSources,
                                    IReadOnlyList<Piece> pieces, Dictionary<string, AutoRefitDecisions.Pick?> readings,
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
        (string Reason, string Code)? unreadable = null;
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
                                           pieces, readings, out unreadable);
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
                var (left, code) = unreadable ?? (string.Format(Strings.SizeSet.EndsUnreadableFmt, piece.Label),
                                                  Codes.EndsUnreadable);
                notes.Add(left);
                Out(SizeStatus.Skipped, code, left, target.Dir);
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
            warnings.AddRange(EndWarnings(piece.Label, option, src.Choice, src.Gap, src.BodyName,
                                          src.OutfitLegsOption is { } legsOption ? (src.FromLabel, legsOption) : null));
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
                                          BodySizeCatalog? game, Action<string> note,
                                          IReadOnlyList<Piece> pieces, Dictionary<string, AutoRefitDecisions.Pick?> readings,
                                          out (string Reason, string Code)? refused)
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
            => KeepEnds.ClothSitsOn(reading, catalog.For(limb, "0201"), catalog.PathOf) is { } sits
               && AcrossEligible(LayoutOf(catalog, sits.Option))
                ? (sits.Option, sits.Gap) : null;

        string? readLayout = null;
        if (found.Route is KeepEnds.Route.Skin or KeepEnds.Route.Cloth or KeepEnds.Route.Sibling or KeepEnds.Route.SkinOnly
            && found.Pick is { } pick && CatalogOf(pick.Dir) is { } readCatalog)
            readLayout = LayoutOf(readCatalog, pick.Option);
        var madeOn = request.MadeOn is { } m
            ? women.FirstOrDefault(b => string.Equals(b.Dir, m, StringComparison.OrdinalIgnoreCase)) : null;
        var madeOnCloth = madeOn != null ? ClothOn(madeOn.Catalog) : null;
        (BodyModIndex.Entry Entry, BodyOption Option, float Gap)? byCloth = null;
        if (found.Route is KeepEnds.Route.NoSkin or KeepEnds.Route.SkinOnly && madeOnCloth == null)
        {
            var clothReadings = women.Select(mod => ClothOn(mod.Catalog)).ToList();
            if (NearestByCloth(clothReadings.Select(r => r?.Gap).ToList(), EndClothTie) is var best and >= 0)
                byCloth = (women[best], clothReadings[best]!.Value.Option, clothReadings[best]!.Value.Gap);
        }

        // A skinless shoe its cloth places on no body closely: the body its outfit's own legs were made on, read off the
        // legs' skin, before the game's body — which is a body the author may never have used.
        (BodyModIndex.Entry Entry, BodyOption Option, string LegsOption)? byOutfit = null;
        if (found.Route == KeepEnds.Route.NoSkin && piece.Slot == "_sho" && madeOnCloth == null && byCloth == null
            && women.Count > 0 && OutfitLegsOf(pieces, piece) is { } outfitLegs)
        {
            BodyModIndex.Entry? WomenOf(string dir)
                => women.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase));
            var legsPick = PickOutfitLegs(outfitLegs.Options, file => ReadOutfitLegs(request, file, readings, note),
                                          p => WomenOf(p.Dir) is { } w && AcrossEligible(LayoutOf(w.Catalog, p.Option)));
            if (legsPick is { } lp)
            {
                byOutfit = (WomenOf(lp.Pick.Dir)!, lp.Pick.Option, lp.Legs.Option ?? Path.GetFileName(lp.Legs.File));
                note($"{option ?? "its file"}: its outfit's legs \"{byOutfit.Value.LegsOption}\" read as {lp.Pick.Dir} / "
                     + $"{lp.Pick.Option.Label} ({lp.Pick.Confidence})");
            }
        }

        var choice = EndMadeFor(found.Route, readLayout, madeOnCloth != null, byCloth != null, game != null, byOutfit != null);
        note($"{option ?? "its file"}: route {KeepEnds.Letter(found.Route)}, made a men's from {choice}");
        // The made-for body narrowed to the size read and its own hand or foot: the job bends what it is given, and a
        // bent size is known by its new file, not the author's (as the legs' sources are handed over, MensSources).
        BodySizeCatalog Narrow(BodySizeCatalog catalog, BodyOption o)
            => CrossSexRefitJob.NarrowMadeFor(catalog, o, reading.Slot, "0201");
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
            case EndMadeForChoice.Outfit:
                var (outfitEntry, oo, legsOption) = byOutfit!.Value;
                return new MensEndSource(option, bytes, reading, Narrow(outfitEntry.Catalog, oo), null, choice, outfitEntry.Name,
                                         RefitCore.LabelFrom(outfitEntry.Name, [oo.Label]), null, legsOption);
            case EndMadeForChoice.GameBody:
                string gameName = Strings.Parts.RetargetFromVanilla;
                var gameLimb = game!.For(limb, "0201").FirstOrDefault();
                return new MensEndSource(option, bytes, reading, game, null, choice, gameName,
                                         gameLimb != null ? RefitCore.LabelFrom(gameName, [gameLimb.Label]) : gameName, null);
            case EndMadeForChoice.NoSource:
                refused = (string.Format(Strings.SizeSet.NoSourceForMenFmt, piece.Label), Codes.NoSourceMen);
                return null;
            default:
                refused = (string.Format(Strings.SizeSet.UnreadableFmt, piece.Label), Codes.Unreadable);
                return null;
        }
    }
}
