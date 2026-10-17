using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CheapLoc;
using Proteus.Localization;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;

namespace Proteus.Services;

/// <summary>
/// The automatic refit of a garment a modder put on an accessory slot — a jacket on a necklace, sleeves on a bracelet —
/// apart from <see cref="RunJob"/>'s refit of body pieces, as <see cref="RunKeepingEnds"/> is. The decisions are
/// <see cref="AccessoryCover"/>'s and <see cref="SiblingPiece"/>'s; this gathers, reads and saves.
/// <para/>
/// An accessory never hides the body, so its garments carry no body skin to read a size off. Whether it is a garment
/// at all is how much of the chosen chest or legs its cloth covers; what it was made on is the chest or legs piece its
/// author ships in the same option, or failing that the one worn beneath it — that piece's own body and size, never a
/// guess from the accessory's cloth.
/// </summary>
public sealed partial class AutoRefitWatcher
{
    private async Task RunAccessory(Request r, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        if (r.ModRoot == null || r.Resolved == null || r.Chosen == null || r.Worn.Rel == null) return;   // the game's own
        string root = r.ModRoot, race = ReadAs(r.Worn.GamePath).Race;

        // ── jewellery read before needs nothing more: not even the framework thread ──
        byte[] bytes = await File.ReadAllBytesAsync(r.Resolved, ct);
        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        var ownRecord = BodyRetargetWriter.ReadRecord(root);
        var sizedWith = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["_top"] = SiblingPiece.Find(redirects, ownRecord, r.Worn.Rel, "_top", race).Count > 0,
            ["_dwn"] = SiblingPiece.Find(redirects, ownRecord, r.Worn.Rel, "_dwn", race).Count > 0,
        };
        bool SizedWith(string slot) => sizedWith.GetValueOrDefault(slot);
        // The body and sizes chosen at the walk: what its cover reading is measured against, and so keyed by, before
        // anything is gathered.
        string walkKey = AccessoryCover.Key(bytes, r.Chosen, race);
        if (AccessoryCover.KnownJewellery(detections.TryGet(walkKey), SizedWith))
        {
            log.Debug("[Proteus] auto refit: {0} {1} covers too little of the body (read before), left as it is in {2} ms",
                      r.Slot, r.Worn.GamePath, clock.ElapsedMilliseconds);
            return;
        }

        var g = await OnFramework(() => Gather(r));
        if (g == null) return;
        ct.ThrowIfCancellationRequested();
        var state = Index.Current ?? Index.RefreshState(g.Mods, g.ModsRoot);
        // Measured against, and refitted onto, the chest's body and the legs' each (see AutoRefitTargets).
        var targets = new AutoRefitTargets(g.Preference, state.Find, race);
        if (targets.Body("_top") == null && targets.Body("_dwn") == null)
        {
            if ((targets.MissingDir("_top") ?? targets.MissingDir("_dwn")) is { } missing) SayBodyMissing(r, missing);
            return;
        }
        string item = ItemName(r, g);
        var chestTo = targets.Chosen("_top");
        var legsTo = targets.Chosen("_dwn");
        BodyOption? TargetFor(string slot) => slot == "_top" ? chestTo : slot == "_dwn" ? legsTo : null;
        bool HasSizes(string slot) => targets.Options(slot).Count > 0;
        if (!HasSizes("_top") && !HasSizes("_dwn"))
        {
            // Nothing at this race to measure a cover against, so a garment cannot be told from jewellery: said in the
            // log only. A body piece worn at this race says the body lacks it; every ring put on would otherwise too.
            log.Debug("[Proteus] auto refit: {0} — {1} has no chest or legs at c{2}, left as it is", item,
                      targets.NameOf(targets.Body("_top") != null ? "_top" : "_dwn"), race);
            return;
        }

        if (ModelPartReader.Read(bytes) is not { } garment)
        {
            Fail(r, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"), item,
                                  Strings.Parts.Unreadable));
            return;
        }

        // ── a garment, and over what: a little of a part its author sizes it with, most of a part where nothing else
        //    says so — a big necklace covers more chest than a sleeve does ──
        AssertOffFramework("measuring the accessory");
        string coverKey = AccessoryCover.Key(bytes, AutoRefitTargets.ChosenOf(g.Preference), race);
        var cover = detections.TryGet(coverKey) is { } hit && AccessoryCover.FromCache(hit) is { } cached
            ? cached
            : MeasureCover(garment, targets, AccessoryCover.MeasuredOn(chestTo, targets.Options("_top")),
                           AccessoryCover.MeasuredOn(legsTo, targets.Options("_dwn")), coverKey);
        var covered = cover.Covered(SizedWith);
        log.Information("[Proteus] auto refit: {0} covers {1:P0} of the chest and {2:P0} of the legs (sized with the "
                      + "chest: {3}, the legs: {4})", item, cover.Chest, cover.Legs, SizedWith("_top"), SizedWith("_dwn"));
        if (covered.Count == 0)
        {
            Say(string.Format(Loc.Localize("Chat.AutoRefit.Accessory.Jewellery.Fmt",
                    "[Proteus] {0} doesn't cover the body, so it was left as it is."), item));
            return;
        }
        bool Chosen(string slot) => (slot == "_top" ? g.Preference.Chest : g.Preference.Legs) != null;
        if (AccessoryCover.WithoutSize(covered, TargetFor, Chosen) is { } unsized)
        {
            if (unsized.Why == AccessoryCover.NoSize.ForRace) SayNoSizeForRace(targets.NameOf(unsized.Slot), unsized.Slot, race, item);
            else
                Say(string.Format(Loc.Localize("Chat.AutoRefit.NoSizeChosen.Fmt",
                        "[Proteus] No {0} size is chosen for {1}, so {2} was left as it is. Choose one under /proteus > "
                      + "Settings."), SlotName(unsized.Slot), targets.NameOf(unsized.Slot), item), problem: true);
            return;
        }
        string primary = covered[0];
        var target = TargetFor(primary)!;
        // A part with a size chosen at this race has its body installed.
        var lead = targets.Body(primary)!;

        // The game's own bodies, at the accessory's race and at every race a piece worn beneath it is drawn at: read
        // through the game, so on the framework thread, as RunJob reads them. A piece made on the game's body is read
        // against them, and every reading's cache key goes by them.
        var races = new[] { race }.Concat(r.Beside?.Values.Select(b => ReadAs(b.Worn.GamePath).Race) ?? [])
                                  .Distinct(StringComparer.Ordinal).ToList();
        var vanilla = await OnFramework(() => races.ToDictionary(x => x, x => VanillaBodyCatalog.Read(x, readGameFile),
                                                                 StringComparer.Ordinal));
        ct.ThrowIfCancellationRequested();
        BodySizeCatalog? CatalogOf(string dir) => AccessoryCover.CatalogOf(dir, vanilla[race], d => state.Find(d)?.Catalog);
        void Note(string note) => log.Information("[Proteus] auto refit: {0}: {1}", item, note);
        var siblings = new Dictionary<string, AutoRefitDecisions.Pick?>(StringComparer.Ordinal);
        AutoRefitDecisions.Pick? SiblingOf(string slot)
        {
            if (siblings.TryGetValue(slot, out var known)) return known;
            var candidates = SiblingPiece.Find(redirects, ownRecord, r.Worn.Rel!, slot, race, Note);
            var job = new EndsJob(r, g, state, targets.Body(slot) ?? lead, targets, item, bytes, redirects, ownRecord,
                                  RacialModelBake.Route.None,
                                  r.Worn.GamePath, race,
                                  BodySizeCatalog.IsMaleRace(race), garment, new HashSet<string>(), vanilla[race],
                                  TargetFor, clock, null);
            return siblings[slot] = candidates.Count == 0 ? null : ReadSibling(job, candidates, Note);
        }

        // ── the author's own sizes for bodies ──
        string dir = Path.GetFileName(root);
        var sizeWords = state.List.SelectMany(b => AutoRefitDecisions.Slots.SelectMany(s => b.Catalog.For(s, race)))
                                  .SelectMany(o => AutoRefitDecisions.Words(o.Group + " " + o.Section + " " + o.Name))
                                  .ToHashSet(StringComparer.Ordinal);
        string? authors = AccessoryCover.AuthorsBodyOption(
            r.Worn.GamePath, redirects, ownRecord, RefitCore.SingleGroups(root),
            group => OptionsOf(g.Settings, dir)?.FirstOrDefault(o => string.Equals(o.Key, group,
                                                                                  StringComparison.OrdinalIgnoreCase)).Value,
            state.List.SelectMany(b => new[] { b.Name, b.Dir }), sizeWords);
        switch (AccessoryCover.ForAuthor(authors, authors != null ? SiblingOf(primary) : null, lead.Dir, target))
        {
            case AccessoryCover.AuthorGate.AlreadyFits:
                log.Information("[Proteus] auto refit: {0} is already the author's {1}, which is {2} / {3}", item, authors!,
                                lead.Name, target.Label);
                return;
            case AccessoryCover.AuthorGate.LeftToAuthor:
                Say(string.Format(Loc.Localize("Chat.AutoRefit.Accessory.AuthorSizes.Fmt",
                        "[Proteus] {0} already comes in its author's own sizes for bodies (\"{1}\" is on); pick another "
                      + "in Penumbra. It was left as it is."), item, authors));
                return;
        }

        // ── what it was made on, per part it covers ──
        var parts = AccessoryCover.SourcesFor(
            covered, TargetFor, SiblingOf,
            slot => SourceBeneath(r, g, slot, state, targets.Body(slot)!, vanilla, TargetFor(slot)!, Note),
            pick => CatalogOf(pick.Dir) is { } from && AccessoryCover.ClearsSource(garment, from.PathOf(pick.Option)));
        foreach (var (slot, pick) in parts.Sources)
            log.Information("[Proteus] auto refit: {0} {1} as {2}: made on {3} / {4} ({5})", item, r.Slot, slot,
                            NameIn(state, pick.Dir), pick.Option.Label, pick.Confidence);
        if (parts.Refused is { } refused)
        {
            log.Information("[Proteus] auto refit: {0} {1} as {2}: no source ({3})", item, r.Slot, primary, refused);
            Say(refused is AccessoryCover.Refusal.Unsure or AccessoryCover.Refusal.OffBody
                    ? string.Format(Loc.Localize("Chat.AutoRefit.Unsure.Fmt",
                          "[Proteus] Couldn't tell which body {0} was made for, so it was left as it is. You can refit it "
                        + "by hand with the Studio's Body size tool."), item)
                    : string.Format(Loc.Localize("Chat.AutoRefit.Accessory.Beneath.Fmt",
                          "[Proteus] {0} was left as it is: it has no {1} piece of its own to read its size from, and the "
                        + "{1} worn under it is the game's own, isn't on {2}, or was refitted before this version — put "
                        + "it on again or use \"Refit what I'm wearing now\"."), item, SlotName(primary), lead.Name),
                problem: true);
            return;
        }
        var sources = parts.Sources;
        var leftOut = parts.LeftOut.Select(SlotName).ToList();
        ct.ThrowIfCancellationRequested();

        var pickOf = sources[primary];
        if (sources.All(s => AutoRefitDecisions.SameFile(s.Value.Dir, s.Value.Option, targets.DirOf(s.Key), TargetFor(s.Key)!)))
        {
            log.Information("[Proteus] auto refit: {0} already fits {1} / {2}", item, lead.Name, target.Label);
            return;
        }

        // ── where it goes: sized with other pieces in one of the author's groups, into their refit, or not at all ──
        if (WhereItGoes(r, g, lead, redirects, item) is not { } where) return;
        var (saveRoot, group, cutFrom) = where;
        var onBodies = covered.Where(sources.ContainsKey).Select(s => (Body: targets.NameOf(s), TargetFor(s)!.Label)).ToList();
        var labels = onBodies.Select(p => p.Label).ToList();
        var onto = sources.ToDictionary(s => s.Key, s => TargetFor(s.Key)!.Rel, StringComparer.Ordinal);
        var ontoBodies = onto.Keys.ToDictionary(k => k, targets.DirOf, StringComparer.Ordinal);
        var chosenFiles = new Dictionary<string, (string Dir, string Rel)>(StringComparer.Ordinal);
        foreach (string slot in new[] { "_top", "_dwn" })
            if (TargetFor(slot) is { } to) chosenFiles[slot] = (targets.DirOf(slot), to.Rel);
        string proposed = RefitCore.OptionName(onBodies);
        bool shared = saveRoot != null
                      && RefitCore.IsSharedSingleGroup(redirects, ownRecord, group, RefitCore.SingleGroups(saveRoot));
        string? sharedOption = AccessoryCover.SharedOption(
            shared, proposed,
            shared ? RefitCore.JoinOption(ownRecord, group, r.Worn.GamePath, chosenFiles, onto, proposed) : proposed,
            shared && Existing(saveRoot!, group, proposed, r.Worn.GamePath));
        if (sharedOption is not { } option)
        {
            Say(string.Format(Loc.Localize("Chat.AutoRefit.Accessory.Shared.Fmt",
                    "[Proteus] {0} is sized together with the mod's other pieces, so it is refitted with them: once "
                  + "they are refitted to {1}, use \"Refit what I'm wearing now\". It was left as it is for now."),
                    item, lead.Name), problem: true);
            return;
        }
        // Sized by the chest or legs piece of its own option: kept outside that piece as it is drawn now (see
        // OutfitPartners). One sized by the piece worn beneath has no author's file of it to read their relation from.
        var siblingFile = SiblingOf(primary) is { } bySibling
                          && string.Equals(bySibling.Dir, pickOf.Dir, StringComparison.OrdinalIgnoreCase)
                          && RefitCore.Normal(bySibling.Option.Rel) == RefitCore.Normal(pickOf.Option.Rel)
            ? SiblingPiece.Find(redirects, ownRecord, r.Worn.Rel!, primary, race).FirstOrDefault()
            : null;
        OutfitPartners.Partner? partner = null;
        if (siblingFile != null)
        {
            Beside? beside = null;
            r.Beside?.TryGetValue(primary, out beside);
            PieceOutcome? outcome = null;
            if (beside != null) outcomes.TryGetValue(OutcomeKey(primary, beside.Worn), out outcome);
            partner = OutfitPartners.ForAutoRefit(root, siblingFile, primary, pickOf, target.Rel, beside?.ModRoot,
                                                  beside?.Worn.GamePath, beside?.Worn.Rel, outcome,
                                                  BodyRetargetWriter.ReadRecord(root), Note, lead.Dir);
        }
        var layers = partner != null ? OutfitPartners.LayersOf(root, partner) : null;
        if (await LetBeOrPutBack(r, g, saveRoot, group, option, r.Worn.GamePath, cutFrom, item, lead.Name, ct, pickOf,
                                 layers))
            return;
        var outfit = partner != null ? OutfitPartners.Load(root, partner, Note) : null;
        if (partner != null && outfit == null) layers = null;

        // ── the refit ──
        string sourceName = NameIn(state, pickOf.Dir);
        string targetLabel = RefitCore.OptionName(onBodies);
        Say(string.Format(Loc.Localize("Chat.AutoRefit.Start.Fmt",
                "[Proteus] Refitting {0} from {1} to {2}. It will be switched on when it is done."),
                item, sourceName + " — " + pickOf.Option.Label, targetLabel));
        current = current with { Message = string.Format(Strings.AutoRefit.WorkingFmt, item), Failed = false };

        bool male = BodySizeCatalog.IsMaleRace(race);
        var pairs = new List<BodyRetarget.SlotPair>();
        foreach (string slot in covered.Where(sources.ContainsKey))
        {
            var pick = sources[slot];
            var sourceCat = CatalogOf(pick.Dir)!;   // a source with no catalog does not clear (SourcesFor)
            var to = TargetFor(slot)!;
            string toDir = targets.DirOf(slot);
            if (BodyRetarget.BuildPair(slot, sourceCat.PathOf(pick.Option), targets.PathOf(slot, to), SlotName(slot), male,
                                       RefitCore.MaskOf(pick.Dir, sourceCat, slot, OptionsOf(g.Settings, pick.Dir)),
                                       RefitCore.MaskOf(toDir, targets.Catalog(slot), slot, OptionsOf(g.Settings, toDir)),
                                       uvRemap, out var pair) is { } refusal)
            {
                if (slot == primary)
                {
                    Fail(r, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"),
                                          item, refusal));
                    return;
                }
                log.Information("[Proteus] auto refit: {0} {1} left out: {2}", item, slot, refusal);
                leftOut.Add(SlotName(slot));
                continue;
            }
            pairs.Add(pair);
        }
        ct.ThrowIfCancellationRequested();

        AssertOffFramework("the refit");
        bool across = AutoRefitTargets.Across(pickOf.Dir, covered.Where(sources.ContainsKey).Select(targets.DirOf));
        var plan = RefitCore.PlanAccessoryRefit(garment, bytes, pairs, covered.Contains("_top") ? "_top" : null,
                                                across, out var pieceNames,
                                                line => log.Debug("[Proteus] auto refit: {0}", line),
                                                line => log.Warning("[Proteus] auto refit: {0}", line), outfit);
        if (outfit != null)
            log.Information("[Proteus] auto refit: {0} kept outside its {1} piece {2}: {3} points lifted, {4:F1} mm short "
                          + "at most", item, primary == "_top" ? "chest" : "legs", partner!.RefittedRel,
                            plan.Report.Layered, plan.Report.LayerResidual * 1000f);
        ct.ThrowIfCancellationRequested();

        string labelFrom = RefitCore.LabelFrom(across ? sourceName : null,
                                               covered.Where(sources.ContainsKey).Select(s => sources[s].Option.Label));
        var refit = new BodyRetargetWriter.Refit(option, plan.Model, string.Join(" + ", labels), layers);
        if (await SaveAndWear(r, g, saveRoot, group, r.Worn.GamePath, lead, labelFrom, refit, cutFrom, RacialModelBake.Route.None, item,
                              fromGame: false,
                              new BodyRetargetWriter.Provenance(pickOf.Dir, pickOf.Option.Rel, onto,
                                                                AutoRefitTargets.BodiesOf(ontoBodies, lead.Dir)))
            is not { } savedRoot)
            return;
        log.Information("[Proteus] auto refit: {0} {1} as {2} onto {3} in {4:F1}s ({5} folded, moved up to {6:F1} mm)",
                        item, r.Slot, primary, option, clock.Elapsed.TotalSeconds, plan.Report.Folded,
                        plan.Report.WorstMove * 1000f);
        if (RefitCore.ReleasedLog(plan.Report, pieceNames) is { } released)
            log.Information("[Proteus] auto refit: {0}: let go of kept piece(s) that would sink moved whole: {1}", item,
                            released);

        string done = string.Format(Loc.Localize("Chat.AutoRefit.Done.Fmt",
                "[Proteus] {0} now fits {1}. Saved as \"{2}\" in {3} and switched on."),
                item, targetLabel, option, SavedIn(g, savedRoot, lead));
        if (leftOut.Count > 0)
            done += " " + string.Format(Loc.Localize("Chat.AutoRefit.PartsLeftOut.Fmt",
                        "These parts could not be refitted with it and kept their size: {0}."), string.Join(", ", leftOut));
        Say(done);
        Finished(item, lead.Name);
    }

    /// <summary>How much of the chest and legs the accessory covers, measured and kept (see <see cref="AccessoryCover"/>).</summary>
    /// <param name="targets">Each part's body, which its size is a size of.</param>
    /// <param name="chest">The chest size measured against (see <see cref="AccessoryCover.MeasuredOn"/>); null for none.</param>
    private AccessoryCover.Cover MeasureCover(ModelParts garment, AutoRefitTargets targets, BodyOption? chest,
                                              BodyOption? legs, string key)
    {
        var clock = Stopwatch.StartNew();
        ModelParts? Body(string slot, BodyOption? option)
            => option == null ? null : ModelPartReader.Read(File.ReadAllBytes(targets.PathOf(slot, option)));
        var cover = AccessoryCover.Read(garment, Body("_top", chest), Body("_dwn", legs));
        detections.Put(key, AccessoryCover.ToCache(cover));
        log.Debug("[Proteus] auto refit: cover measured in {0} ms", clock.ElapsedMilliseconds);
        return cover;
    }

    /// <summary>
    /// What the chest or legs piece worn beneath the accessory, as the walk saw it, says the accessory was made on — see
    /// <see cref="AccessoryCover.FromBeneath"/>. Read at that piece's own race, so its reading is the one its own refit
    /// made and kept (see <see cref="ReadAs"/>).
    /// </summary>
    /// <param name="vanilla">The game's own bodies by race, read on the framework thread.</param>
    private AccessoryCover.Beneath SourceBeneath(Request r, Gathered g, string slot, BodyModIndex.State state,
                                                 BodyModIndex.Entry targetMod,
                                                 IReadOnlyDictionary<string, BodySizeCatalog> vanilla, BodyOption chosen,
                                                 Action<string> note)
    {
        if (r.Beside == null || !r.Beside.TryGetValue(slot, out var beside))
            return new AccessoryCover.Beneath(null, AccessoryCover.Refusal.NothingBeneath);
        string race = ReadAs(beside.Worn.GamePath).Race;
        BodySizeCatalog? CatalogOf(string dir) => AccessoryCover.CatalogOf(dir, vanilla[race], d => state.Find(d)?.Catalog);
        BodyRetargetWriter.Entry? record = null;
        if (beside.ModRoot != null && beside.Worn.Rel is { } rel
            && AutoRefitDecisions.ShouldRefit(beside.Worn, true, false) == AutoRefitDecisions.Skip.OwnRefit)
        {
            var entries = BodyRetargetWriter.ReadRecord(beside.ModRoot)?.Options ?? [];
            record = entries.FirstOrDefault(e => RefitCore.Normal(e.File) == RefitCore.Normal(rel)
                                                 && string.Equals(e.GamePath, beside.Worn.GamePath,
                                                                  StringComparison.OrdinalIgnoreCase))
                     ?? new BodyRetargetWriter.Entry();   // a refit of ours with no record: says nothing
        }
        outcomes.TryGetValue(OutcomeKey(slot, beside.Worn), out var outcome);
        return AccessoryCover.FromBeneath(
            beside.ModRoot == null, record, targetMod.Dir, slot, chosen.Rel, outcome,
            () =>
            {
                if (beside.Resolved == null || SiblingPiece.TryOpen(Path.GetDirectoryName(beside.Resolved)!,
                                                                    Path.GetFileName(beside.Resolved), note)
                                                is not { } opened)
                    return (null, false);
                var (pick, _) = ReadSource(g, state, targetMod, vanilla[race], opened.Bytes, opened.Model, opened.Bones,
                                           slot, race, false, $"the {SlotName(slot)} beneath");
                return (pick, pick != null && AutoRefitDecisions.SameFile(pick.Dir, pick.Option, targetMod.Dir, chosen));
            },
            (dir, sourceRel) => CatalogOf(dir)?.For(slot, race)
                                    .FirstOrDefault(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(sourceRel)) is { } option
                ? new AutoRefitDecisions.Pick(dir, option, Confidence.Exact, false)
                : null);
    }

    /// <summary>A body mod chosen for this collection is not installed: said, and in chat once a session.</summary>
    /// <param name="dir">The body chosen for the garment's own part.</param>
    private void SayBodyMissing(Request r, string dir)
    {
        string missing = string.Format(Loc.Localize("Chat.AutoRefit.BodyMissing.Fmt",
                "[Proteus] The body chosen for automatic refits in this collection, \"{0}\", isn't installed, "
              + "so nothing is being refitted. Choose another under /proteus > Settings."),
                dir);
        Say(missing, problem: true);
        if (toldMissing.TryAdd(r.Collection, 0)) Chat(missing, problem: true);
    }

    /// <summary>The body chosen has no size of <paramref name="slot"/> at <paramref name="race"/>: said.</summary>
    private void SayNoSizeForRace(string bodyName, string slot, string race, string item)
        => Say(string.Format(Loc.Localize("Chat.AutoRefit.NoSizeForRace.Fmt",
                   "[Proteus] {0} has no {1} size for {2}, so {3} was left as it is."),
                   bodyName, SlotName(slot), ModelRace.Describe(race), item), problem: true);

    /// <summary>A body mod as chat names it.</summary>
    private static string NameIn(BodyModIndex.State state, string dir)
        => dir == VanillaBodyCatalog.Key ? Strings.Parts.RetargetFromVanilla : state.Find(dir)?.Name ?? dir;
}
