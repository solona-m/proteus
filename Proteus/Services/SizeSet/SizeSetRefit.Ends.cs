using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Dalamud.Plugin.Services;
using Proteus.Localization;

namespace Proteus.Services;

/// <summary>
/// A mod's shoes and gloves, sized as the automatic refit sizes them (<see cref="KeepEnds"/>): the foot or hand kept
/// exactly, and the leg or arm refitted onto each size — by the legs table for a shoe ("Rue WC", "YAB SC"), one option per
/// body for gloves in their own group, since every chest size of a body has the same arms. A shoe or glove sized in the
/// same group as the outfit's chest or legs joins the options that piece made, so one choice dresses the whole outfit.
/// One that reaches no leg or arm has only its skin redrawn, where the new body draws skin another way. A shoe with no
/// skin at all is read only with "Made on" set: its sizes placed on that body's legs (<c>SizeSetRefit.MadeOnEnds.cs</c>).
/// </summary>
internal static partial class SizeSetRefit
{
    /// <summary>
    /// What a glove's or shoe's sibling (see <see cref="SiblingPiece"/>) was made on, as the size set reads it: off its own
    /// skin, in the slot and race its own path names, over the body mods given — never the game's own body, which the size
    /// set reads no chest or legs off — and kept in <paramref name="readings"/> for the run, so the sibling's own sizes and
    /// the glove's are cut from one reading. Null, noted, when it cannot be read: a sibling never fails the glove.
    /// </summary>
    /// <param name="request">The run: its mod, and its body mods — <see cref="Request.Bodies"/> only, whatever
    /// <see cref="Request.GameBody"/> offers.</param>
    /// <param name="preferred">The body preferred on a tie for a slot and race (see <see cref="Preferred"/>).</param>
    internal static AutoRefitDecisions.Pick? SiblingReading(Request request, SiblingPiece.Candidate c, string limb,
                                                           Func<string, string, string> preferred,
                                                           Dictionary<string, AutoRefitDecisions.Pick?> readings,
                                                           Action<string> note)
    {
        string root = request.ModRoot;
        var bodies = request.Bodies.Select(b => (b.Dir, b.Catalog)).ToList();
        var (readSlot, race) = AutoRefitWatcher.ReadAs(c.GamePath);
        string slot = readSlot ?? limb, prefer = preferred(slot, race);
        string key = ReadingKey(c.File, slot, race, prefer);
        if (readings.TryGetValue(key, out var known))
        {
            note($"its {(limb == "_top" ? "chest" : "legs")} piece {c.File} was read before in this run");
            return known;
        }
        AutoRefitDecisions.Pick? pick = null;
        if (SiblingPiece.TryOpen(root, c.File, note) is { } opened)
            try
            {
                pick = RefitCore.DetectSource(opened.Model, opened.Bones, slot, race, bodies, prefer, request.KnownBody, note);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                note($"{c.File} could not be read: {ex.Message}");
            }
        return readings[key] = pick;
    }

    /// <summary>One of the author's sizes of a shoe or glove, read: what its sizes may be cut from.</summary>
    /// <param name="MadeOn">How a skinless shoe's size was placed on the "Made on" body (<c>SizeSetRefit.MadeOnEnds.cs</c>);
    /// null for one read off the shoe itself.</param>
    private sealed record EndSource(string? Option, byte[] Bytes, ModelParts Garment, HashSet<string> Bones,
                                    KeepEnds.Reading Reading, KeepEnds.Found Found, SiblingPiece.Candidate? Sibling,
                                    EndMadeOnRead? MadeOn = null)
    {
        public bool SkinOnly => Found.Route == KeepEnds.Route.SkinOnly;
    }

    /// <summary>One of the author's shoe or glove files, read once a run whichever options give it.</summary>
    /// <param name="Cloth">For a skinless shoe with "Made on" set, how its shaft's cloth sits on that body's legs
    /// (<see cref="KeepEnds.ClothSitsOn"/>); else null.</param>
    /// <param name="Yas">The file is rigged for YAS, or the run asks for YAS sizes.</param>
    private sealed record EndFile(byte[] Bytes, ModelParts Garment, HashSet<string> Bones, KeepEnds.Reading Reading,
                                  KeepEnds.Found Found, IReadOnlyList<SiblingPiece.Candidate> Siblings,
                                  (BodyOption Option, float Gap, BodySizeMatch.Ranking Ranking)? Cloth, bool Yas);

    private static int RunEndsPiece(Request request, Piece piece, UVRemapService? uvRemap, IPluginLog log,
                                    Action<string> progress, RunLog runLog,
                                    Dictionary<string, AutoRefitDecisions.Pick?> readings,
                                    Dictionary<string, string> leadSources, CancellationToken ct)
    {
        var notes = runLog.Notes;
        string root = request.ModRoot;
        string race = piece.Race;
        bool male = BodySizeCatalog.IsMaleRace(race);
        string limb = AutoRefitDecisions.LimbOf(piece.Slot)!;
        bool gloves = piece.Slot == "_glv";
        // The table the sizes come from: the chest's for gloves and for anything following a chest, else the legs'.
        string sizeSlot = gloves || piece.FollowsChest ? "_top" : "_dwn";
        bool follows = piece.FollowsChest || piece.FollowsLegs;

        if (!request.Keys.Any(k => ForRace(k, sizeSlot, race)))
        {
            runLog.Add(piece, SizeStatus.Skipped, Codes.NoBodyForRace, "none of the bodies asked for is of its race");
            return 0;
        }
        if (piece.Unpaired)
        {
            runLog.Note(string.Format(Strings.SizeSet.UnpairedFmt, piece.Label), piece, SizeStatus.Skipped, Codes.Unpaired,
                        piece.Group);
            return 0;
        }

        // ── what it was made on: the author's first size the foot and leg can be read on ──
        progress(string.Format(Strings.SizeSet.ReadingFmt, piece.Label));
        var mods = request.Bodies.Select(b => (b.Dir, b.Catalog)).ToList();
        if (mods.Count == 0)
        {
            runLog.Note(Strings.SizeSet.NoBodies, piece, SizeStatus.Skipped, Codes.NoBodies);
            return 0;
        }
        using var nearness = BodySizeMatch.BeginReading();
        string preferred = Preferred(request, sizeSlot, race, mods[0].Dir);
        // The chest or legs piece the author ships beside it in each option, read off its own skin (see SiblingReading):
        // over the body mods alone, as that piece's own sizes are read (RunPiece), so it is the reading they were cut
        // from — kept for the run, and taken from there when that piece was read first. The mod's manifest and record as
        // they stand NOW: the options a top made earlier in this run carry the glove's file too, and are Proteus's.
        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        var record = BodyRetargetWriter.ReadRecord(root);
        void Note(string note) => log.Information("[Proteus] size set: {0}: {1}", piece.Label, note);
        AutoRefitDecisions.Pick? ReadSibling(SiblingPiece.Candidate c)
            => SiblingReading(request, c, limb, (slot, r) => Preferred(request, slot, r, mods[0].Dir), readings, Note);
        // The game's own body, last, as the automatic refit searches: a shoe or glove made on it shares nothing with any
        // body mod. Never "enabled" — no collection turns it on — so it wins no tie a body mod is in, as there.
        var game = request.GameBody?.Invoke(race) is { IsBody: true } g ? g : null;
        if (game != null) mods.Add((VanillaBodyCatalog.Key, game));
        BodySizeCatalog? CatalogOf(string dir)
            => dir == VanillaBodyCatalog.Key ? game
             : request.Bodies.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase))?.Catalog;

        // A shoe with no skin at all is placed on the body chosen under "Made on", when one is (SizeSetRefit.MadeOnEnds.cs):
        // by where its shaft's cloth sits on that body's legs, else by its option name. Null keeps today's reading.
        var madeOnBody = EndMadeOnBody(request, piece.Slot, race, KeepEnds.Route.NoSkin);
        var madeOnFits = new Dictionary<string, MadeOnFit>(StringComparer.Ordinal);
        bool clothKept = true;

        // Each of the author's files read once: the first size that reads sizes an end that follows nothing, and one
        // that follows is cut, option by option, from the size its lead piece there was.
        bool anyRead = false;
        var fileReads = new Dictionary<string, EndFile?>(StringComparer.Ordinal);
        EndFile? ReadFile(string? option, string file)
        {
            string fileKey = RefitCore.Normal(file);
            if (fileReads.TryGetValue(fileKey, out var known)) return known;
            EndFile? read = null;
            try
            {
                string path = BodySizeCatalog.ResolveCaseInsensitive(root, file);
                if (File.Exists(path) && File.ReadAllBytes(path) is var bytes && ModelPartReader.Read(bytes) is { } garment
                    && KeepEnds.Read(piece.Slot, garment, bytes) is { } reading)
                {
                    anyRead = true;
                    var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
                    // The chest or legs piece this very option serves beside it.
                    var siblings = SiblingPiece.Find(redirects, record, file, limb, race, Note);
                    var sibling = siblings.Count > 0 ? SiblingPiece.Agree(siblings, ReadSibling, Note) : null;
                    var found = KeepEnds.Find(reading, garment, bones, race,
                                              (view, slot) => RefitCore.DetectSource(view, bones, slot, race, mods, preferred,
                                                  dir => dir != VanillaBodyCatalog.Key, Note),
                                              CatalogOf, sibling: sibling,
                                              note: why => Note($"its {(limb == "_top" ? "chest" : "legs")} piece in the same option "
                                                              + $"was not taken: {why}; read as before"));
                    if (found.Route == KeepEnds.Route.Sibling)
                        log.Information("[Proteus] size set: {0} sized by its {1} piece in the same option: {2} / {3} ({4})",
                                        piece.Label, limb == "_top" ? "chest" : "legs", found.Pick!.Dir, found.Pick.Option.Label,
                                        found.Pick.Confidence);
                    log.Information("[Proteus] size set: {0} from {1}: route {2}, {3} / {4}", piece.Label, option ?? file,
                                    KeepEnds.Letter(found.Route), found.Pick?.Dir ?? "-", found.Pick?.Option.Label ?? "-");
                    bool yasFile = request.Yas || SupportedBodies.IsYas(bones);
                    (BodyOption Option, float Gap, BodySizeMatch.Ranking Ranking)? cloth = null;
                    if (found.Route == KeepEnds.Route.NoSkin && madeOnBody != null)
                        cloth = KeepEnds.ClothSitsOn(reading, MadeOnLegs(madeOnBody, race, yasFile, piece.FollowsChest),
                                                     madeOnBody.Catalog.PathOf);
                    read = new EndFile(bytes, garment, bones, reading, found, siblings, cloth, yasFile);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Note($"{option ?? file} could not be read: {e.Message}");
            }
            return fileReads[fileKey] = read;
        }

        // What a size may be cut from, kept by file — or by file and option for a skinless shoe placed by its name.
        var endSources = new Dictionary<string, EndSource?>(StringComparer.Ordinal);
        EndSource? ReadEndSource(string? option, string file)
        {
            if (ReadFile(option, file) is not { } f) return null;
            var found = f.Found;
            if (found.Route is KeepEnds.Route.Skin or KeepEnds.Route.Cloth or KeepEnds.Route.SkinOnly or KeepEnds.Route.Sibling)
            {
                string fileKey = EndSourceKey(file, option, byName: false);
                if (endSources.TryGetValue(fileKey, out var had)) return had;
                return endSources[fileKey] = new EndSource(option, f.Bytes, f.Garment, f.Bones, f.Reading, found,
                                                           found.Route == KeepEnds.Route.Sibling ? f.Siblings[0] : null);
            }
            if (found.Route != KeepEnds.Route.NoSkin || madeOnBody == null) return null;
            bool byCloth = clothKept && ClothClear(f.Cloth);
            string key = EndSourceKey(file, option, byName: !byCloth);
            if (endSources.TryGetValue(key, out var known)) return known;
            var placed = PlaceEndOnMadeOn(byCloth ? f.Cloth : null, option, madeOnBody, race, f.Yas, piece.FollowsChest,
                                          on => BodySizeMatch.ClothAgainst(f.Reading.LimbView, madeOnBody.Catalog.PathOf(on)));
            string shaft = byCloth ? "" : f.Cloth is { } c ? $"shaft unclear ({c.Ranking.Confidence}); " : "shaft unclear; ";
            Note($"{option ?? file}: no skin; {shaft}{placed.Why}");
            if (placed.How == EndMadeOnHow.None)
            {
                madeOnFits[key] = placed.Fit;
                return endSources[key] = null;
            }
            var pick = new AutoRefitDecisions.Pick(madeOnBody.Dir, placed.On!, placed.Confidence, true);
            return endSources[key] = new EndSource(option, f.Bytes, f.Garment, f.Bones, f.Reading,
                                                   new KeepEnds.Found(KeepEnds.Route.Cloth, pick), null, placed);
        }

        // Every size of a skinless shoe is read on the "Made on" body before any is placed: when the shaft's cloth puts two
        // of them on one size, it cannot tell them apart there, and every size is placed by its name instead.
        if (madeOnBody != null)
        {
            var reads = new List<(string? Option, BodyOption? Cloth, BodyOption? ByName)>();
            foreach (var (option, file) in piece.Options)
            {
                ct.ThrowIfCancellationRequested();
                if (ReadFile(option, file) is not { Found.Route: KeepEnds.Route.NoSkin } f) continue;
                var table = SupportedBodies.MadeOnTable(madeOnBody, piece.FollowsChest ? "_top" : "_dwn", race, f.Yas);
                reads.Add((option, ClothClear(f.Cloth) ? f.Cloth!.Value.Option : null,
                           TrustOn(option, table, madeOnBody.Catalog, "_dwn", race, piece.FollowsChest).Trusted?.On));
            }
            if (ClothCollides(reads, out string collides))
            {
                clothKept = false;
                Note(collides);
            }
        }

        int primaryAt = PrimaryOf(piece.Options.Count, i =>
        {
            ct.ThrowIfCancellationRequested();
            return ReadEndSource(piece.Options[i].Option, piece.Options[i].File) switch
            {
                null => EndReadKind.None,
                { MadeOn.How: EndMadeOnHow.Name } => EndReadKind.Name,
                _ => EndReadKind.Read,
            };
        });
        if (primaryAt < 0 || ReadEndSource(piece.Options[primaryAt].Option, piece.Options[primaryAt].File) is not { } src)
        {
            var (said, code) = EndsUnplaced(piece.Label, anyRead, madeOnBody == null ? null : request.MadeOn, madeOnBody?.Name,
                                            madeOnFits.Values);
            runLog.Note(said, piece, SizeStatus.Skipped, code, piece.Group ?? piece.GroupName);
            return 0;
        }

        var pick = src.Found.Pick!;
        bool skinOnly = src.SkinOnly;
        // The body mod it was made on — or none, for the game's own body: every refit from that is across bodies.
        var sourceEntry = request.Bodies.FirstOrDefault(b => string.Equals(b.Dir, pick.Dir, StringComparison.OrdinalIgnoreCase));
        if (sourceEntry == null)
            log.Information("[Proteus] size set: {0} was made on {1}", piece.Label, Strings.Parts.RetargetFromVanilla);

        // ── where the sizes go: the same bookkeeping as a chest or legs piece's ──
        using var group = new SizeGroup(root, piece);
        // A shoe's author sizes are compared by its legs, the foot left out; a glove's never (every chest has its arms).
        var authorSizes = !skinOnly && KeepEnds.ComparesAuthorSizes(piece.Slot) ? AuthorLimbSizes(root, piece, group.Ours) : [];

        bool yas = request.Yas || SupportedBodies.IsYas(src.Bones);
        // A skinless shoe placed on the "Made on" body: each of the author's sizes placed there is that size of the body,
        // never remade (as a skinless chest or legs piece's are, KAN-6).
        var madeOnOwn = new List<(string? Option, TrustedSize? Trusted)>();
        if (src.MadeOn != null)
            madeOnOwn.AddRange(piece.Options.Select(o => (o.Option, ReadEndSource(o.Option, o.File)?.MadeOn?.Own)));

        foreach (string key in request.Keys)
        {
            ct.ThrowIfCancellationRequested();
            if (!ForRace(key, sizeSlot, race)) continue;
            var (body, target, missing) = BodyFor(key, sizeSlot, yas, request.Bodies, sourceEntry, notes, piece.Label);
            if (missing != null)
                runLog.Add(piece, SizeStatus.Skipped, Codes.NotInstalled, missing, group.Name, body: key, yas: yas);
            if (body == null || target == null) continue;
            var targetCat = target.Catalog;
            // Read once per slot for the body; the size refitted onto is taken as chosen per target below.
            var targetMasks = new Dictionary<string, BodyRetarget.BodyDraw?>(StringComparer.Ordinal);
            BodyRetarget.BodyDraw? targetMask(string slot)
                => targetMasks.TryGetValue(slot, out var have) ? have
                 : targetMasks[slot] = RefitCore.MaskOf(target.Dir, targetCat, slot, null, RefitCore.RaceNumber(race));
            bool primaryAcross = !string.Equals(target.Dir, pick.Dir, StringComparison.OrdinalIgnoreCase);

            foreach (var (option, size, fixedRel) in EndTargets(body, piece, gloves, skinOnly, follows, primaryAcross, pick))
            {
                ct.ThrowIfCancellationRequested();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                void Out(SizeStatus status, string? code, string? reason, string? from = null, Measures? measures = null,
                         string? cutFrom = null)
                    => runLog.Add(piece, status, code, reason, group.Name, option, key, target!.Dir, body!.Yas, from: from, cutFrom: cutFrom,
                                  measures: measures, seconds: status == SizeStatus.Made ? clock.Elapsed.TotalSeconds : null);
                if (group.AlreadyThere(option, request.Remake))
                {
                    log.Information("[Proteus] size set: {0}: \"{1}\" already there", piece.Label, option);
                    AlreadyOut(group, option, (code, why) => Out(SizeStatus.Skipped, code, why));
                    continue;
                }
                // A follower only joins a size what it follows made: one made here alone would wear the author's
                // unrefitted chest or legs beside a refitted shoe.
                if (follows && !group.Ours.Contains(option))
                {
                    log.Information("[Proteus] size set: {0}: no \"{1}\" to join", piece.Label, option);
                    Out(SizeStatus.Skipped, Codes.NoChestOption, $"no \"{option}\" to join");
                    continue;
                }

                // ── which of the author's sizes it is cut from: for a follower, the one its lead piece in this option
                // was. Cut from any other, a jacket is laid over a top it was never made against; so it is left out. ──
                var from = src;
                string? sourceOption = src.Option;
                if (follows)
                {
                    EndSource? led = null;
                    string? why;
                    if (!leadSources.TryGetValue(LeadKey(group.Name, race, option), out var leadOption))
                        why = "this run did not cut the option from any size";
                    else if (ResolveOptionFile(redirects, piece.GamePath, group.Name, leadOption) is not { } file)
                        why = $"\"{leadOption}\" gives it no model";
                    else if ((led = ReadEndSource(leadOption, file)) == null)
                        why = $"its model in \"{leadOption}\" could not be read";
                    else if (led.SkinOnly != skinOnly)
                        why = $"its model in \"{leadOption}\" reads another way than its first size";
                    else
                        why = null;
                    if (why != null)
                    {
                        Note($"{option} left out: {why}");
                        // "Left as it was" only of a refit an earlier run made there; one never made says to make it.
                        string said = string.Format(request.Remake && group.Carries(option) ? Strings.SizeSet.EndNoLeadSourceRemakeFmt
                                                                                            : Strings.SizeSet.EndNoLeadSourceFmt,
                                                    piece.Label, option);
                        notes.Add(said);
                        Out(SizeStatus.Skipped, Codes.EndNoLead, said);
                        continue;
                    }
                    (from, sourceOption) = (led!, leadOption);
                    Note($"{option} from \"{leadOption}\", as its {(limb == "_top" ? "chest" : "legs")} piece there was");
                }

                var fromPick = from.Found.Pick!;
                var fromEntry = request.Bodies.FirstOrDefault(b => string.Equals(b.Dir, fromPick.Dir, StringComparison.OrdinalIgnoreCase));
                var sourceCat = fromEntry?.Catalog ?? game!;
                string sourceName = fromEntry?.Name ?? Strings.Parts.RetargetFromVanilla;
                string sourcePath = sourceCat.PathOf(fromPick.Option);
                bool across = !string.Equals(target.Dir, fromPick.Dir, StringComparison.OrdinalIgnoreCase);
                BodyRetarget.BodyDraw? sourceMask(string slot) => RefitCore.MaskOf(fromPick.Dir, sourceCat, slot, null);
                // A shoe following a chest goes onto the legs that size's chest is worn with: the table's across body
                // mods, the source's own family's within one.
                string? limbRel = size == null ? fixedRel
                                : piece.FollowsChest && !gloves
                                    ? across ? size.LegsRel : SupportedBodies.FamilyLegs(fromPick.Option.Rel, size.Name)
                                    : size.Rel;

                // A shoe following a chest size with no legs of its own (Neolithe XS keeps the Small hips) keeps the
                // author's, as the legs piece does.
                if (limbRel == null && !skinOnly)
                {
                    log.Information("[Proteus] size set: {0}: {1} has no legs of its own", piece.Label, option);
                    Out(SizeStatus.Skipped, Codes.NoOwnLegs, "no legs of its own: keeps the author's");
                    continue;
                }

                BodyOption? to = null;
                if (limbRel != null && OptionByFile(targetCat.For(limb, race), limbRel) is { } found) to = found;
                if (!skinOnly && to == null)
                {
                    string said = string.Format(Strings.SizeSet.NoSuchSizeFmt, piece.Label, option, target.Name);
                    notes.Add(said);
                    Out(SizeStatus.Skipped, Codes.NoSuchSize, said);
                    continue;
                }

                progress(string.Format(Strings.SizeSet.WorkingFmt, piece.Label, option));
                string fromLabel = RefitCore.LabelFrom(across ? sourceName : null, [fromPick.Option.Label]);
                if (skinOnly)
                {
                    // Its shape is the author's at every size: only its skin, drawn the way this body draws its own.
                    if (AutoRefitDecisions.Companion(targetCat.For(piece.Slot, race), to, null) is not { } end)
                    {
                        Out(SizeStatus.Skipped, Codes.NoEnd, $"{target.Name} has no {SlotName(piece.Slot)} to draw its skin by");
                        continue;
                    }
                    var model = RefitCore.MatchSkinToBody(from.Bytes, sourcePath, targetCat.PathOf(end), uvRemap, male, log);
                    if (ReferenceEquals(model, from.Bytes))
                    {
                        log.Information("[Proteus] size set: {0}: its skin already matches {1}", piece.Label, option);
                        Out(SizeStatus.Skipped, Codes.SameSkin, "its skin already matches");
                        continue;
                    }
                    log.Information("[Proteus] size set: {0}: skin drawn like {1}", piece.Label, option);
                    ct.ThrowIfCancellationRequested();
                    if (group.Save(target.Dir, fromLabel, [new BodyRetargetWriter.Refit(option, model, KeepEnds.SkinOnlyLabel)],
                                   sourceOption, notes))
                        Out(SizeStatus.Made, null, null, fromLabel, Measures.SkinOnlyEnd, sourceOption);
                    else
                        Out(SizeStatus.Failed, Codes.SaveFailed, notes[^1]);
                    continue;
                }

                if (AutoRefitDecisions.SameFile(fromPick.Dir, fromPick.Option, target.Dir, to!))
                {
                    log.Information("[Proteus] size set: {0}: {1} is the size it was made on", piece.Label, option);
                    Out(SizeStatus.Skipped, Codes.MadeOnIt, "the size it was made on");
                    continue;
                }
                if (!across && KeepEnds.SameShape(from.Reading.LimbView, fromPick.Option, to!, sourceCat, from.Bones, fromPick.FromCloth))
                {
                    log.Information("[Proteus] size set: {0}: {1} is the same shape as the size it was made on",
                                    piece.Label, option);
                    Out(SizeStatus.Skipped, Codes.MadeOnIt, "the same shape as the size it was made on");
                    continue;
                }
                if ((AuthorHas(authorSizes, targetCat, to!, limb, race)
                     ?? (madeOnOwn.Count > 0 ? TrustedOwn(madeOnOwn, key, to!.Rel) : null)) is { } theirs)
                {
                    log.Information("[Proteus] size set: {0}: {1} is the author's own \"{2}\"", piece.Label, option, theirs);
                    Out(SizeStatus.Skipped, Codes.AuthorsOwn, $"the author's own \"{theirs}\"");
                    continue;
                }

                var (endFrom, endTo) = KeepEnds.EndPair(sourceCat, targetCat, piece.Slot, race, fromPick.Option, to!);
                if (KeepEnds.BuildPairs(from.Reading, sourcePath, targetCat.PathOf(to!),
                                        endFrom == null ? null : sourceCat.PathOf(endFrom),
                                        endTo == null ? null : targetCat.PathOf(endTo), male,
                                        s => (sourceMask(s), targetMask(s)?.Onto(s == limb ? to : endTo)), SlotName, uvRemap,
                                        why => log.Information("[Proteus] size set: {0} {1} without the end's shape: {2}",
                                                               piece.Label, option, why),
                                        out var limbPair, out var endPair) is { } refusal)
                {
                    notes.Add(string.Format(Strings.SizeSet.RefusedFmt, piece.Label, option, refusal));
                    Out(SizeStatus.Refused, Codes.RefusedPair, refusal);
                    continue;
                }
                // Kept outside its chest or legs piece's refit in this very option, made before it in the run.
                var partner = from.Sibling is { } sibling
                    ? OutfitPartners.ForSizeSet(record, option, sibling, limb, fromPick, to!.Rel, Note)
                    : null;
                var outfit = partner != null ? OutfitPartners.Load(root, partner, Note) : null;
                // A kept foot or hand is always cut, whatever Request.KeepHiddenSkin says: uncut, a shoe would carry the
                // whole legs body through any skirt worn over it (see KeepEnds.Plan).
                var plan = KeepEnds.Plan(from.Garment, from.Bytes, from.Reading, limbPair, endPair, across,
                                         line => log.Debug("[Proteus] size set: {0}", line),
                                         line => log.Warning("[Proteus] size set: {0}", line), outfit,
                                         request.Piercings, carryPubes: request.Pubes);
                if (outfit != null)
                    log.Information("[Proteus] size set: {0} {1}: kept outside its {2} piece's refit {3}: {4} points "
                                  + "lifted, {5:F1} mm short at most", piece.Label, option,
                                    limb == "_top" ? "chest" : "legs", partner!.RefittedRel, plan.Report.Layered,
                                    plan.Report.LayerResidual * 1000f);
                plan = RefitCore.MatchSkinToBody(plan, sourcePath, targetCat.PathOf(to!), uvRemap, male, log);
                var (seamEdges, stretched) = KeepEnds.SeamStretch(from.Garment, from.Reading.Ends, plan);
                log.Information("[Proteus] size set: {0} onto {1} by its {2} (moved up to {3:F1} mm, the seam stretched "
                              + "up to {4:F1} mm over {5} edges, {6} triangles left folded)", piece.Label, option, limb,
                                plan.Report.WorstMove * 1000f, stretched * 1000f, seamEdges, plan.Report.Folded);
                if (RefitCore.ReleasedLog(plan.Report, from.Reading.FreeNames ?? []) is { } released)
                    log.Information("[Proteus] size set: {0} {1}: let go of kept piece(s) that would sink moved whole: {2}",
                                    piece.Label, option, released);
                // One save per size, as a chest's: each names the size it was cut from.
                // Stopped while this size was made: it is not saved — Stop keeps what was saved before it.
                ct.ThrowIfCancellationRequested();
                int folded = CrossSexRefit.ClothFolded(from.Bytes, plan);
                if (from.MadeOn != null)
                {
                    // A skinless boot's ankle is all cloth, which the seam above never sees: measured here, inside the pass.
                    var (joinEdges, joinWorst) = JoinStretch(from.Garment, from.Reading.Ends, plan);
                    log.Information("[Proteus] size set: {0} {1}: ankle join stretched up to {2:F1} mm over {3} edges, "
                                  + "{4} triangles folded", piece.Label, option, joinWorst * 1000f, joinEdges, folded);
                    if (joinWorst > KeepEnds.SeamWarn)
                        log.Warning("[Proteus] size set: {0} {1}: the ankle join stretched by {2:F1} mm — look at it in game",
                                    piece.Label, option, joinWorst * 1000f);
                }
                if (group.Save(target.Dir, fromLabel,
                               [new BodyRetargetWriter.Refit(option, plan.Model, to!.Label,
                                                             outfit != null ? OutfitPartners.LayersOf(root, partner!) : null,
                                                             plan.Materials)],
                               sourceOption, notes))
                {
                    Out(SizeStatus.Made, null, null, fromLabel, Measures.Of(plan, folded), sourceOption);
                    if (from.MadeOn is { } placed)
                        foreach (string warning in EndMadeOnWarnings(piece.Label, sourceOption, placed,
                                                                     madeOnBody?.Name ?? request.MadeOn ?? ""))
                            AddWarningOnce(runLog, warning);
                }
                else
                    Out(SizeStatus.Failed, Codes.SaveFailed, notes[^1]);
            }
        }
        group.RestoreDefault();
        return group.Saved;
    }

    /// <summary>
    /// The options a shoe or glove gets of one body, with the size each is of — its leg or arm is that size's, read once
    /// its source is known — or, with no size, the leg or arm itself (null for a skin-only one in its own group, which
    /// has none).
    /// <list type="bullet">
    /// <item>Following a chest: the chest's sizes, onto the arms of that chest, or the legs it goes with.</item>
    /// <item>A shoe in its own group, or following legs: the legs table's sizes.</item>
    /// <item>Gloves in their own group: one option per body, onto the body's middle chest — all its chests' arms are one.</item>
    /// <item>A skin-only piece in its own group: one option per body, named for it.</item>
    /// </list>
    /// </summary>
    private static IEnumerable<(string Option, SupportedBodies.Size? Size, string? LimbRel)> EndTargets(
        SupportedBodies.Body body, Piece piece, bool gloves, bool skinOnly, bool follows, bool across, AutoRefitDecisions.Pick pick)
    {
        if (!follows && (gloves || skinOnly))
        {
            var middle = body.Sizes.FirstOrDefault(s => s.Name == "M") ?? body.Sizes.FirstOrDefault();
            if (middle == null) yield break;
            // A skin-only shoe goes onto nothing: which legs it would have stood in does not matter.
            yield return (SupportedBodies.OptionName(body, new SupportedBodies.Size("", middle.Rel, null)), null,
                          skinOnly ? null : middle.Rel);
            yield break;
        }
        string? familyFrom = across || follows || skinOnly ? null : pick.Option.Rel;
        foreach (var size in SupportedBodies.SizesFor(body, familyFrom))
            yield return (SupportedBodies.OptionName(body, size), size, null);
    }

    /// <summary>The author's sizes of a shoe read by their legs alone, the foot left out (see <see cref="AuthorHas"/>).</summary>
    private static List<AuthorSize> AuthorLimbSizes(string root, Piece piece, IReadOnlySet<string> ours)
    {
        var sizes = new List<AuthorSize>();
        foreach (var (option, file) in piece.Options)
        {
            if (option == null || ours.Contains(option)) continue;
            string path = BodySizeCatalog.ResolveCaseInsensitive(root, file);
            if (!File.Exists(path)) continue;
            byte[] bytes = File.ReadAllBytes(path);
            if (ModelPartReader.Read(bytes) is not { } model || KeepEnds.Read(piece.Slot, model, bytes) is not { } reading)
                continue;
            sizes.Add(new AuthorSize(option, reading.LimbView, BodySizeMatch.ProbeKeys(reading.LimbView),
                                     new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal)));
        }
        return sizes;
    }
}
