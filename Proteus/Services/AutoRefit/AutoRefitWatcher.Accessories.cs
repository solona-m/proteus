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
    /// <summary>The body and sizes chosen for the collection when an accessory was put on: what its cover reading is
    /// measured against, and so keyed by, before anything is gathered.</summary>
    private sealed record Chosen(string BodyDir, string? ChestRel, string? LegsRel);

    private async Task RunAccessory(Request r, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        if (r.ModRoot == null || r.Resolved == null || r.Chosen == null) return;   // the game's own: never queued
        string race = ReadAs(r.Worn.GamePath).Race;

        // ── jewellery read before needs nothing more: not even the framework thread ──
        byte[] bytes = await File.ReadAllBytesAsync(r.Resolved, ct);
        string coverKey = AccessoryCover.Key(bytes, r.Chosen.BodyDir, r.Chosen.ChestRel, r.Chosen.LegsRel, race);
        if (detections.TryGet(coverKey) is { } seen && AccessoryCover.FromCache(seen) is { CoversNothing: true })
        {
            log.Debug("[Proteus] auto refit: {0} {1} covers no body (read before), left as it is in {2} ms", r.Slot,
                      r.Worn.GamePath, clock.ElapsedMilliseconds);
            return;
        }

        var g = await OnFramework(() => Gather(r));
        if (g == null) return;
        ct.ThrowIfCancellationRequested();
        var state = Index.Current ?? Index.RefreshState(g.Mods, g.ModsRoot);
        if (state.Find(g.Preference.BodyDir) is not { } targetMod) return;   // said by the body pieces' own refits
        var targetCat = targetMod.Catalog;
        string item = ItemName(r, g);
        var chestTo = AutoRefitDecisions.Resolve(targetCat.For("_top", race), g.Preference.Chest);
        var legsTo = AutoRefitDecisions.Resolve(targetCat.For("_dwn", race), g.Preference.Legs);
        BodyOption? TargetFor(string slot) => slot == "_top" ? chestTo : slot == "_dwn" ? legsTo : null;

        if (ModelPartReader.Read(bytes) is not { } garment)
        {
            Fail(r, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"), item,
                                  Strings.Parts.Unreadable));
            return;
        }

        // ── a garment, and over what ──
        AssertOffFramework("measuring the accessory");
        var cover = detections.TryGet(coverKey) is { } hit && AccessoryCover.FromCache(hit) is { } cached
            ? cached
            : MeasureCover(garment, targetCat, chestTo, legsTo, coverKey);
        // A garment by its cover: a little, where its author sizes it with that part (its options ship a chest or legs
        // piece beside it); most of it, where nothing else says so — a big necklace covers more chest than a sleeve.
        var redirects = PenumbraModMeta.ReadAllRedirects(r.ModRoot);
        var ownRecord = BodyRetargetWriter.ReadRecord(r.ModRoot);
        void Note(string note) => log.Information("[Proteus] auto refit: {0}: {1}", item, note);
        bool SizedWith(string slot) => SiblingPiece.Find(redirects, ownRecord, r.Worn.Rel!, slot, race).Count > 0;
        var covered = cover.Covered(SizedWith);
        log.Information("[Proteus] auto refit: {0} covers {1:P0} of the chest and {2:P0} of the legs (sized with the "
                      + "chest: {3}, the legs: {4})", item, cover.Chest, cover.Legs, SizedWith("_top"), SizedWith("_dwn"));
        if (covered.Count == 0)
        {
            Say(string.Format(Loc.Localize("Chat.AutoRefit.Accessory.Jewellery.Fmt",
                    "[Proteus] {0} doesn't cover the body, so it was left as it is."), item));
            return;
        }
        string primary = covered[0];
        if (AccessoryCover.WithoutSize(covered, TargetFor) is { } unsized)
        {
            Say(string.Format(Loc.Localize("Chat.AutoRefit.NoSizeChosen.Fmt",
                    "[Proteus] No {0} size is chosen for {1}, so {2} was left as it is. Choose one under /proteus > "
                  + "Settings."), SlotName(unsized), targetMod.Name, item), problem: true);
            return;
        }
        var target = TargetFor(primary)!;
        // Read through the game, so on the framework thread, as RunJob reads it: a reading of a piece made on the game's
        // body, and every reading's cache key, go by it.
        var vanillaCat = await OnFramework(() => VanillaBodyCatalog.Read(race, readGameFile));
        ct.ThrowIfCancellationRequested();
        BodySizeCatalog? CatalogOf(string dir) => state.Find(dir)?.Catalog;
        AutoRefitDecisions.Pick? SiblingOf(string slot)
        {
            var candidates = SiblingPiece.Find(redirects, ownRecord, r.Worn.Rel!, slot, race, Note);
            if (candidates.Count == 0) return null;
            var stub = new EndsJob(r, g, state, targetMod, item, bytes, redirects, ownRecord, 0, r.Worn.GamePath, race,
                                   BodySizeCatalog.IsMaleRace(race), garment, new HashSet<string>(), vanillaCat,
                                   TargetFor, clock);
            return ReadSibling(stub, candidates, Note);
        }

        // ── the author's own sizes for bodies ──
        string root = r.ModRoot;
        if (AuthorsBodyOption(g, root, redirects, ownRecord, state, race) is { } authors)
        {
            if (SiblingOf(primary) is { } own && AutoRefitDecisions.SameFile(own.Dir, own.Option, targetMod.Dir, target))
                log.Information("[Proteus] auto refit: {0} is already the author's {1}, which is {2} / {3}", item, authors,
                                targetMod.Name, target.Label);
            else
                Say(string.Format(Loc.Localize("Chat.AutoRefit.Accessory.AuthorSizes.Fmt",
                        "[Proteus] {0} already comes in its author's own sizes for bodies (\"{1}\" is on); pick another "
                      + "in Penumbra. It was left as it is."), item, authors));
            return;
        }

        // ── what it was made on, per part it covers ──
        var sources = new Dictionary<string, AutoRefitDecisions.Pick>(StringComparer.Ordinal);
        var leftOut = new List<string>();
        foreach (string slot in covered)
        {
            if (TargetFor(slot) == null)
            {
                leftOut.Add(SlotName(slot));
                continue;
            }
            var (pick, why) = SiblingOf(slot) is { } sibling
                ? (sibling, (AccessoryCover.Refusal?)null)
                : SourceBeneath(r, g, slot, state, targetMod, vanillaCat, TargetFor(slot)!, race, Note);
            if (pick != null && CatalogOf(pick.Dir) is { } from
                && !AccessoryCover.ClearsSource(garment, from.PathOf(pick.Option)))
                (pick, why) = (null, AccessoryCover.Refusal.OffBody);
            if (pick != null)
            {
                log.Information("[Proteus] auto refit: {0} {1} as {2}: made on {3} / {4} ({5})", item, r.Slot, slot,
                                NameIn(state, pick.Dir), pick.Option.Label, pick.Confidence);
                sources[slot] = pick;
                continue;
            }
            log.Information("[Proteus] auto refit: {0} {1} as {2}: no source ({3})", item, r.Slot, slot, why?.ToString() ?? "");
            if (slot != primary)
            {
                leftOut.Add(SlotName(slot));
                continue;
            }
            Say(why is AccessoryCover.Refusal.Unsure or AccessoryCover.Refusal.OffBody
                    ? string.Format(Loc.Localize("Chat.AutoRefit.Unsure.Fmt",
                          "[Proteus] Couldn't tell which body {0} was made for, so it was left as it is. You can refit it "
                        + "by hand with the Studio's Body size tool."), item)
                    : string.Format(Loc.Localize("Chat.AutoRefit.Accessory.Beneath.Fmt",
                          "[Proteus] {0} was left as it is: it has no {1} piece of its own to read its size from, and the "
                        + "{1} worn under it is the game's own, isn't on {2}, or was refitted before this version — put "
                        + "it on again or use \"Refit what I'm wearing now\"."), item, SlotName(slot), targetMod.Name),
                problem: true);
            return;
        }
        ct.ThrowIfCancellationRequested();

        var pickOf = sources[primary];
        if (sources.All(s => AutoRefitDecisions.SameFile(s.Value.Dir, s.Value.Option, targetMod.Dir, TargetFor(s.Key)!)))
        {
            log.Information("[Proteus] auto refit: {0} already fits {1} / {2}", item, targetMod.Name, target.Label);
            return;
        }

        // ── where it goes: sized with other pieces in one of the author's groups, into their refit, or not at all ──
        if (WhereItGoes(r, g, targetMod, redirects, item) is not { } where) return;
        var (saveRoot, group, cutFrom) = where;
        var labels = covered.Where(sources.ContainsKey).Select(s => TargetFor(s)!.Label).ToList();
        var targets = sources.ToDictionary(s => s.Key, s => TargetFor(s.Key)!.Rel, StringComparer.Ordinal);
        string option = RefitCore.OptionName(targetMod.Name, labels);
        if (saveRoot != null && RefitCore.IsSharedSingleGroup(redirects, ownRecord, group, RefitCore.SingleGroups(saveRoot)))
        {
            // A new option of its own would switch the top's refit off the moment it went on: join that, or nothing.
            string joined = RefitCore.JoinOption(ownRecord, group, r.Worn.GamePath, targetMod.Dir,
                                                 ChosenTargets(TargetFor), targets, option);
            if (joined == option && !Existing(saveRoot, group, option, r.Worn.GamePath))
            {
                Say(string.Format(Loc.Localize("Chat.AutoRefit.Accessory.Shared.Fmt",
                        "[Proteus] {0} is sized together with the mod's other pieces, so it is refitted with them: once "
                      + "they are refitted to {1}, use \"Refit what I'm wearing now\". It was left as it is for now."),
                        item, targetMod.Name), problem: true);
                return;
            }
            option = joined;
        }
        if (await LetBeOrPutBack(r, g, saveRoot, group, option, r.Worn.GamePath, cutFrom, item, targetMod.Name, ct, pickOf))
            return;

        // ── the refit ──
        string sourceName = NameIn(state, pickOf.Dir);
        string targetLabel = targetMod.Name + " — " + string.Join(" + ", labels);
        Say(string.Format(Loc.Localize("Chat.AutoRefit.Start.Fmt",
                "[Proteus] Refitting {0} from {1} to {2}. It will be switched on when it is done."),
                item, sourceName + " — " + pickOf.Option.Label, targetLabel));
        current = current with { Message = string.Format(Strings.AutoRefit.WorkingFmt, item), Failed = false };

        bool male = BodySizeCatalog.IsMaleRace(race);
        var pairs = new List<BodyRetarget.SlotPair>();
        foreach (string slot in covered.Where(sources.ContainsKey))
        {
            var pick = sources[slot];
            var sourceCat = CatalogOf(pick.Dir)!;
            var to = TargetFor(slot)!;
            if (BodyRetarget.BuildPair(slot, sourceCat.PathOf(pick.Option), targetCat.PathOf(to), SlotName(slot), male,
                                       RefitCore.MaskOf(pick.Dir, sourceCat, slot, OptionsOf(g.Settings, pick.Dir)),
                                       RefitCore.MaskOf(targetMod.Dir, targetCat, slot, OptionsOf(g.Settings, targetMod.Dir)),
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
        bool across = !string.Equals(pickOf.Dir, targetMod.Dir, StringComparison.OrdinalIgnoreCase);
        var plan = RefitCore.PlanAccessoryRefit(garment, bytes, pairs, covered.Contains("_top") ? "_top" : null,
                                                across, out var pieceNames,
                                                line => log.Debug("[Proteus] auto refit: {0}", line),
                                                line => log.Warning("[Proteus] auto refit: {0}", line));
        ct.ThrowIfCancellationRequested();

        string labelFrom = RefitCore.LabelFrom(across ? sourceName : null,
                                               covered.Where(sources.ContainsKey).Select(s => sources[s].Option.Label));
        var refit = new BodyRetargetWriter.Refit(option, plan.Model, string.Join(" + ", labels));
        if (await SaveAndWear(r, g, saveRoot, group, r.Worn.GamePath, targetMod, labelFrom, refit, cutFrom, 0, item,
                              fromGame: false,
                              new BodyRetargetWriter.Provenance(pickOf.Dir, pickOf.Option.Rel, targets))
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
                item, targetLabel, option, SavedIn(g, savedRoot, targetMod));
        if (leftOut.Count > 0)
            done += " " + string.Format(Loc.Localize("Chat.AutoRefit.PartsLeftOut.Fmt",
                        "These parts could not be refitted with it and kept their size: {0}."), string.Join(", ", leftOut));
        Say(done);
        Finished(item, targetMod.Name);
    }

    /// <summary>How much of the chosen chest and legs the accessory covers, measured and kept (see <see cref="AccessoryCover"/>).</summary>
    private AccessoryCover.Cover MeasureCover(ModelParts garment, BodySizeCatalog targetCat, BodyOption? chestTo,
                                              BodyOption? legsTo, string key)
    {
        var clock = Stopwatch.StartNew();
        ModelParts? Body(BodyOption? option)
            => option == null ? null : ModelPartReader.Read(File.ReadAllBytes(targetCat.PathOf(option)));
        var cover = AccessoryCover.Read(garment, Body(chestTo), Body(legsTo));
        detections.Put(key, AccessoryCover.ToCache(cover));
        log.Debug("[Proteus] auto refit: cover measured in {0} ms", clock.ElapsedMilliseconds);
        return cover;
    }

    /// <summary>
    /// The author's option drawn for the accessory in its size group, when that option names a body (see
    /// <see cref="AutoRefitDecisions.NamesABody"/>) — read from the collection's own selection now, after the body pieces'
    /// refits, never from which option first serves the file. Null when the group's drawn option is one of Proteus's
    /// refits, names no body, or the accessory has no size group.
    /// </summary>
    private static string? AuthorsBodyOption(Gathered g, string root, List<PenumbraModMeta.Redirect> redirects,
                                             BodyRetargetWriter.Record? ownRecord, BodyModIndex.State state, string race)
    {
        string dir = Path.GetFileName(root);
        foreach (string group in RefitCore.SingleGroups(root))
        {
            if (ownRecord?.OwnGroups.Contains(group, StringComparer.OrdinalIgnoreCase) == true) continue;
            if (g.Settings == null || !g.Settings.TryGetValue(dir, out var s)
                || s.Options.FirstOrDefault(o => string.Equals(o.Key, group, StringComparison.OrdinalIgnoreCase)).Value
                   is not { Count: > 0 } ticked)
                continue;
            string drawn = ticked[0];
            // Only a group that sizes this accessory, through the option drawn.
            if (!redirects.Any(d => string.Equals(d.Source, group + " / " + drawn, StringComparison.OrdinalIgnoreCase)
                                    && DrawnModelPaths.EquippedAccessoryGarmentModelsFromModels([d.GamePath]).Count > 0))
                continue;
            if (ownRecord?.Options.Any(e => string.Equals(ownRecord.GroupOf(e), group, StringComparison.OrdinalIgnoreCase)
                                            && string.Equals(e.Name, drawn, StringComparison.OrdinalIgnoreCase)) == true)
                return null;
            var sizeWords = state.List.SelectMany(b => AutoRefitDecisions.Slots.SelectMany(slot => b.Catalog.For(slot, race)))
                                      .SelectMany(o => AutoRefitDecisions.Words(o.Group + " " + o.Section + " " + o.Name))
                                      .ToHashSet(StringComparer.Ordinal);
            var bodyNames = state.List.SelectMany(b => new[] { b.Name, b.Dir });
            return AutoRefitDecisions.NamesABody(drawn, bodyNames, sizeWords) ? drawn : null;
        }
        return null;
    }

    /// <summary>
    /// What the chest or legs piece worn beneath the accessory, as the walk saw it, says the accessory was made on — see
    /// <see cref="AccessoryCover.FromBeneath"/>.
    /// </summary>
    private (AutoRefitDecisions.Pick? Pick, AccessoryCover.Refusal? Why) SourceBeneath(
        Request r, Gathered g, string slot, BodyModIndex.State state, BodyModIndex.Entry targetMod,
        BodySizeCatalog vanillaCat, BodyOption chosen, string race, Action<string> note)
    {
        if (r.Beside == null || !r.Beside.TryGetValue(slot, out var beside))
            return (null, AccessoryCover.Refusal.NothingBeneath);
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
        var found = AccessoryCover.FromBeneath(
            beside.ModRoot == null, record, targetMod.Dir, slot, chosen.Rel, outcome,
            () =>
            {
                if (beside.Resolved == null || SiblingPiece.TryOpen(Path.GetDirectoryName(beside.Resolved)!,
                                                                    Path.GetFileName(beside.Resolved), note)
                                                is not { } opened)
                    return (null, false);
                var (pick, _) = ReadSource(g, state, targetMod, vanillaCat, opened.Bytes, opened.Model,
                                           opened.Bones, slot, race, false, $"the {SlotName(slot)} beneath");
                return (pick, pick != null && AutoRefitDecisions.SameFile(pick.Dir, pick.Option, targetMod.Dir, chosen));
            },
            (dir, rel) => state.Find(dir)?.Catalog.For(slot, race)
                               .FirstOrDefault(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(rel)) is { } option
                ? new AutoRefitDecisions.Pick(dir, option, Confidence.Exact, false)
                : null);
        return (found.Source, found.Why);
    }

    /// <summary>A body mod as chat names it.</summary>
    private static string NameIn(BodyModIndex.State state, string dir)
        => dir == VanillaBodyCatalog.Key ? Strings.Parts.RetargetFromVanilla : state.Find(dir)?.Name ?? dir;
}
