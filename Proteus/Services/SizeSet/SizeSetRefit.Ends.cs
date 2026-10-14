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
/// One that reaches no leg or arm has only its skin redrawn, where the new body draws skin another way.
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
                pick = RefitCore.DetectSource(opened.Model, opened.Bones, slot, race, bodies, prefer, _ => true, note);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                note($"{c.File} could not be read: {ex.Message}");
            }
        return readings[key] = pick;
    }

    /// <summary>One of the author's sizes of a shoe or glove, read: what its sizes may be cut from.</summary>
    private sealed record EndSource(string? Option, byte[] Bytes, ModelParts Garment, HashSet<string> Bones,
                                    KeepEnds.Reading Reading, KeepEnds.Found Found, SiblingPiece.Candidate? Sibling)
    {
        public bool SkinOnly => Found.Route == KeepEnds.Route.SkinOnly;
    }

    private static int RunEndsPiece(Request request, Piece piece, UVRemapService? uvRemap, IPluginLog log,
                                    Action<string> progress, List<string> notes,
                                    Dictionary<string, AutoRefitDecisions.Pick?> readings,
                                    Dictionary<string, string> leadSources, CancellationToken ct)
    {
        string root = request.ModRoot;
        string race = piece.Race;
        bool male = BodySizeCatalog.IsMaleRace(race);
        string limb = AutoRefitDecisions.LimbOf(piece.Slot)!;
        bool gloves = piece.Slot == "_glv";
        // The table the sizes come from: the chest's for gloves and for anything following a chest, else the legs'.
        string sizeSlot = gloves || piece.FollowsChest ? "_top" : "_dwn";
        bool follows = piece.FollowsChest || piece.FollowsLegs;

        if (!request.Keys.Any(k => ForRace(k, sizeSlot, race))) return 0;
        if (piece.Unpaired)
        {
            notes.Add(string.Format(Strings.SizeSet.UnpairedFmt, piece.Label));
            return 0;
        }

        // ── what it was made on: the author's first size the foot and leg can be read on ──
        progress(string.Format(Strings.SizeSet.ReadingFmt, piece.Label));
        var mods = request.Bodies.Select(b => (b.Dir, b.Catalog)).ToList();
        if (mods.Count == 0)
        {
            notes.Add(Strings.SizeSet.NoBodies);
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

        // Each of the author's sizes read once, by file: the first that reads sizes an end that follows nothing, and one
        // that follows is cut, option by option, from the size its lead piece there was.
        bool anyRead = false;
        var endSources = new Dictionary<string, EndSource?>(StringComparer.Ordinal);
        EndSource? ReadEndSource(string? option, string file)
        {
            string fileKey = RefitCore.Normal(file);
            if (endSources.TryGetValue(fileKey, out var known)) return known;
            EndSource? read = null;
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
                    if (found.Route is KeepEnds.Route.Skin or KeepEnds.Route.Cloth or KeepEnds.Route.SkinOnly
                                       or KeepEnds.Route.Sibling)
                        read = new EndSource(option, bytes, garment, bones, reading, found,
                                             found.Route == KeepEnds.Route.Sibling ? siblings[0] : null);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Note($"{option ?? file} could not be read: {e.Message}");
            }
            return endSources[fileKey] = read;
        }

        EndSource? primary = null;
        foreach (var (option, file) in piece.Options)
        {
            ct.ThrowIfCancellationRequested();
            if ((primary = ReadEndSource(option, file)) != null) break;
        }
        if (primary is not { } src)
        {
            notes.Add(string.Format(anyRead ? Strings.SizeSet.UnreadableFmt : Strings.SizeSet.EndsUnreadableFmt,
                                    piece.Label));
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

        foreach (string key in request.Keys)
        {
            ct.ThrowIfCancellationRequested();
            if (!ForRace(key, sizeSlot, race)) continue;
            var (body, target) = BodyFor(key, sizeSlot, yas, request.Bodies, sourceEntry, notes, piece.Label);
            if (body == null || target == null) continue;
            var targetCat = target.Catalog;
            ushort? targetMask(string slot) => RefitCore.MaskOf(target.Dir, targetCat, slot, null);
            bool primaryAcross = !string.Equals(target.Dir, pick.Dir, StringComparison.OrdinalIgnoreCase);

            foreach (var (option, size, fixedRel) in EndTargets(body, piece, gloves, skinOnly, follows, primaryAcross, pick))
            {
                ct.ThrowIfCancellationRequested();
                if (group.AlreadyThere(option, request.Remake))
                {
                    log.Information("[Proteus] size set: {0}: \"{1}\" already there", piece.Label, option);
                    continue;
                }
                // A follower only joins a size what it follows made: one made here alone would wear the author's
                // unrefitted chest or legs beside a refitted shoe.
                if (follows && !group.Ours.Contains(option))
                {
                    log.Information("[Proteus] size set: {0}: no \"{1}\" to join", piece.Label, option);
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
                        notes.Add(string.Format(request.Remake && group.Carries(option) ? Strings.SizeSet.EndNoLeadSourceRemakeFmt
                                                                                        : Strings.SizeSet.EndNoLeadSourceFmt,
                                                piece.Label, option));
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
                ushort? sourceMask(string slot) => RefitCore.MaskOf(fromPick.Dir, sourceCat, slot, null);
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
                    continue;
                }

                BodyOption? to = null;
                if (limbRel != null && OptionByFile(targetCat.For(limb, race), limbRel) is { } found) to = found;
                if (!skinOnly && to == null)
                {
                    notes.Add(string.Format(Strings.SizeSet.NoSuchSizeFmt, piece.Label, option, target.Name));
                    continue;
                }

                progress(string.Format(Strings.SizeSet.WorkingFmt, piece.Label, option));
                string fromLabel = RefitCore.LabelFrom(across ? sourceName : null, [fromPick.Option.Label]);
                if (skinOnly)
                {
                    // Its shape is the author's at every size: only its skin, drawn the way this body draws its own.
                    if (AutoRefitDecisions.Companion(targetCat.For(piece.Slot, race), to, null) is not { } end) continue;
                    var model = RefitCore.MatchSkinToBody(from.Bytes, sourcePath, targetCat.PathOf(end), uvRemap, male, log);
                    if (ReferenceEquals(model, from.Bytes))
                    {
                        log.Information("[Proteus] size set: {0}: its skin already matches {1}", piece.Label, option);
                        continue;
                    }
                    log.Information("[Proteus] size set: {0}: skin drawn like {1}", piece.Label, option);
                    ct.ThrowIfCancellationRequested();
                    group.Save(target.Dir, fromLabel, [new BodyRetargetWriter.Refit(option, model, KeepEnds.SkinOnlyLabel)],
                               sourceOption, notes);
                    continue;
                }

                if (AutoRefitDecisions.SameFile(fromPick.Dir, fromPick.Option, target.Dir, to!))
                {
                    log.Information("[Proteus] size set: {0}: {1} is the size it was made on", piece.Label, option);
                    continue;
                }
                if (!across && KeepEnds.SameShape(from.Reading.LimbView, fromPick.Option, to!, sourceCat, from.Bones, fromPick.FromCloth))
                {
                    log.Information("[Proteus] size set: {0}: {1} is the same shape as the size it was made on",
                                    piece.Label, option);
                    continue;
                }
                if (AuthorHas(authorSizes, targetCat, to!, limb, race) is { } theirs)
                {
                    log.Information("[Proteus] size set: {0}: {1} is the author's own \"{2}\"", piece.Label, option, theirs);
                    continue;
                }

                var (endFrom, endTo) = KeepEnds.EndPair(sourceCat, targetCat, piece.Slot, race, fromPick.Option, to!);
                if (KeepEnds.BuildPairs(from.Reading, sourcePath, targetCat.PathOf(to!),
                                        endFrom == null ? null : sourceCat.PathOf(endFrom),
                                        endTo == null ? null : targetCat.PathOf(endTo), male,
                                        s => (sourceMask(s), targetMask(s)), SlotName, uvRemap,
                                        why => log.Information("[Proteus] size set: {0} {1} without the end's shape: {2}",
                                                               piece.Label, option, why),
                                        out var limbPair, out var endPair) is { } refusal)
                {
                    notes.Add(string.Format(Strings.SizeSet.RefusedFmt, piece.Label, option, refusal));
                    continue;
                }
                // Kept outside its chest or legs piece's refit in this very option, made before it in the run.
                var partner = from.Sibling is { } sibling
                    ? OutfitPartners.ForSizeSet(record, option, sibling, limb, fromPick, to!.Rel, Note)
                    : null;
                var outfit = partner != null ? OutfitPartners.Load(root, partner, Note) : null;
                var plan = KeepEnds.Plan(from.Garment, from.Bytes, from.Reading, limbPair, endPair, across,
                                         line => log.Debug("[Proteus] size set: {0}", line),
                                         line => log.Warning("[Proteus] size set: {0}", line), outfit);
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
                group.Save(target.Dir, fromLabel,
                           [new BodyRetargetWriter.Refit(option, plan.Model, to!.Label,
                                                         outfit != null ? OutfitPartners.LayersOf(root, partner!) : null)],
                           sourceOption, notes);
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
