using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CheapLoc;
using Proteus.Localization;

namespace Proteus.Services;

/// <summary>
/// The automatic refit of shoes and gloves (see <see cref="KeepEnds"/>), apart from <see cref="RunJob"/>'s refit of
/// everything else. It takes over once the piece is gathered and read, and shares where a refit goes
/// (<see cref="WhereItGoes"/>), what was made before (<see cref="LetBeOrPutBack"/>), the author's own sizes
/// (<see cref="SwitchToAuthorSize"/>) and the save (<see cref="SaveAndWear"/>) with it.
/// </summary>
public sealed partial class AutoRefitWatcher
{
    /// <summary>What <see cref="RunJob"/> has gathered and read of a shoe or glove when it hands it on.</summary>
    /// <param name="TargetFor">The size the refit goes onto, per slot.</param>
    /// <param name="Pair">Where it goes and is cut from, and whether its option sizes the chest and the legs together
    /// (see <see cref="PlanPair"/>); null for the game's own gear.</param>
    private sealed record EndsJob(Request R, Gathered G, BodyModIndex.State State, BodyModIndex.Entry TargetMod,
                                  string Item, byte[] Bytes, List<PenumbraModMeta.Redirect> Redirects,
                                  BodyRetargetWriter.Record? OwnRecord, RacialModelBake.Route Route, string SavePath,
                                  string Race,
                                  bool Male, ModelParts Garment, IReadOnlySet<string> Bones, BodySizeCatalog VanillaCat,
                                  Func<string, BodyOption?> TargetFor, Stopwatch Clock, PairContext? Pair);

    /// <summary>What a shoe or glove becomes: the model saved, what its option says it is, and the line said when it is on.</summary>
    private sealed record EndsOutput(byte[] Model, string To, string LabelFrom, Func<string, string> Done);

    /// <summary>
    /// A shoe or glove: its foot or hand kept exactly, the leg or arm it reaches read on its own and refitted, and a
    /// piece that reaches neither only drawn with the new body's skin.
    /// </summary>
    private async Task RunKeepingEnds(EndsJob j, CancellationToken ct)
    {
        var r = j.R;
        var g = j.G;
        var targetMod = j.TargetMod;
        string item = j.Item, slot = r.Slot;
        bool feet = slot == "_sho";

        AssertOffFramework("reading the garment's ends");
        if (KeepEnds.Read(slot, j.Garment, j.Bytes) is not { } reading)
        {
            log.Information("[Proteus] auto refit: {0} — its rig could not be read, so its {1} cannot be told from the rest",
                            item, feet ? "feet" : "hands");
            SayUnsure(item);
            return;
        }

        // ── the author's own size, compared by the legs (see KeepEnds.ComparesAuthorSizes) ──
        if (KeepEnds.ComparesAuthorSizes(slot) && j.TargetFor(reading.Limb) is { } limbTarget
            && await SwitchToAuthorSize(r, g, item, j.Redirects, j.OwnRecord, j.Route, targetMod, limbTarget,
                                        reading.Limb, j.Race, j.State.Version,
                                        ((b, m) => KeepEnds.Read(slot, m, b)?.LimbView, reading.CacheSlot), ct, j.Pair))
            return;

        // ── what it was made on, read from what it reaches ──
        BodySizeCatalog? CatalogOf(string dir) => dir == VanillaBodyCatalog.Key ? j.VanillaCat : j.State.Find(dir)?.Catalog;
        var found = ReadEnds(j, reading, CatalogOf, out var siblings);
        ct.ThrowIfCancellationRequested();
        log.Information("[Proteus] auto refit: {0} keeps its own {1}: {2} of {3} vertices ({4} in the 40-60% band); "
                      + "route {5}", item, feet ? "feet" : "hands", reading.Ends.Count, reading.Vertices, reading.Band,
                        KeepEnds.Letter(found.Route));

        // ── what to do with it, decided before anything is worked out ──
        var decision = KeepEnds.Decide(found, reading, j.Bones, j.Race, targetMod.Dir, CatalogOf, j.TargetFor);
        switch (decision)
        {
            case KeepEnds.LeftAsIs { Why: KeepEnds.Route.NoSkin }:
                Say(string.Format(Loc.Localize("Chat.AutoRefit.NoBodyMesh.Fmt",
                        "[Proteus] {0} carries no body skin to read its size from, so it was left as it is. You can refit "
                      + "it by hand with the Studio's Body size tool."), item), problem: true);
                return;
            case KeepEnds.LeftAsIs:
                SayUnsure(item);
                return;
            case KeepEnds.NoSize noSize:
                Say(string.Format(Loc.Localize("Chat.AutoRefit.NoSizeForRace.Fmt",
                        "[Proteus] {0} has no {1} size for {2}, so {3} was left as it is."),
                        targetMod.Name, SlotName(noSize.Slot), ModelRace.Describe(j.Race), item), problem: true);
                return;
            case KeepEnds.AlreadyFits fits:
                log.Information("[Proteus] auto refit: {0} already fits {1} / {2}", item, targetMod.Name, fits.Target.Label);
                return;
        }

        // A skin-only redraw is cheap and says by itself whether anything needs doing: one already drawn like the new
        // body — a redraw put on again among them — is left as it is, quietly, rather than put back and announced on every
        // equip.
        EndsOutput? skinOnly = null;
        if (decision is KeepEnds.SkinOnly skin)
        {
            skinOnly = RedrawSkin(j, skin, CatalogOf, out bool refused);
            if (refused) return;   // said why already
            if (skinOnly == null)
            {
                // Said neutrally: the skin may already be drawn like the new body's, or its layout may not convert.
                Say(string.Format(feet
                        ? Loc.Localize("Chat.AutoRefit.KeepsFeet.Fmt",
                              "[Proteus] {0} doesn't reach the legs, so it keeps its own shape and its skin was left as it is.")
                        : Loc.Localize("Chat.AutoRefit.KeepsHands.Fmt",
                              "[Proteus] {0} doesn't reach the arms, so it keeps its own shape and its skin was left as it is."),
                        item));
                return;
            }
        }

        if (WhereItGoes(r, g, targetMod, j.Redirects, item) is not { } where) return;
        var (root, group, cutFrom) = where;
        if (j.Pair != null) cutFrom = j.Pair.Plan.CutFrom;
        // What it is made from, which a refit saved before must have been made from to be put back on (see Existing),
        // and the size each part goes onto, for the record.
        var (source, targets) = decision switch
        {
            KeepEnds.Refit refitTo => (refitTo.Source, new Dictionary<string, string>(StringComparer.Ordinal)
                                                       {
                                                           [reading.Limb] = refitTo.Target.Rel,
                                                       }),
            KeepEnds.SkinOnly skinTo => (skinTo.Own, new Dictionary<string, string>(StringComparer.Ordinal)
                                                     {
                                                         [reading.Slot] = skinTo.Target.Rel,
                                                     }),
            _ => throw new InvalidOperationException("a refit or a skin redraw was decided above"),
        };
        string option = decision is KeepEnds.Refit refitOnto
            ? RefitCore.OptionName(targetMod.Name, [refitOnto.Target.Label])
            : KeepEnds.SkinOnlyOption(targetMod.Name);
        // Shipped in an option that sizes the chest and the legs together: into the one option their refits share, a
        // redraw of its skin alone too — an option of its own would switch them off.
        if (j.Pair is { Plan: { Paired: true, Option: { } shared } })
            option = shared;
        // Sized together with the chest or legs in one of the author's single-choice groups: into the option that piece's
        // refit went into, so switching one on does not switch the other off.
        else if (decision is KeepEnds.Refit && root != null
                 && RefitCore.IsSharedSingleGroup(j.Redirects, j.OwnRecord, group, RefitCore.SingleGroups(root)))
            option = RefitCore.JoinOption(j.OwnRecord, group, j.SavePath, targetMod.Dir, ChosenTargets(j.TargetFor),
                                          targets, option);
        // Sized by the chest or legs piece of its own option: kept outside that piece as it is drawn now, which a refit
        // saved before must have been kept outside too (see OutfitPartners).
        var partner = decision is KeepEnds.Refit sized && found.Route == KeepEnds.Route.Sibling && siblings.Count > 0
            ? PartnerOf(j, reading.Limb, siblings[0], sized)
            : null;
        var layers = partner != null ? OutfitPartners.LayersOf(r.ModRoot!, partner) : null;
        if (await LetBeOrPutBack(r, g, root, group, option, j.SavePath, cutFrom, item, targetMod.Name, ct,
                                 source, layers))
            return;

        // ── the refit ──
        var outfit = partner != null
            ? OutfitPartners.Load(r.ModRoot!, partner, note => log.Information("[Proteus] auto refit: {0}: {1}", item, note))
            : null;
        if (partner != null && outfit == null) layers = null;
        var output = skinOnly ?? RefitEnds(j, reading, (KeepEnds.Refit)decision, option, CatalogOf, ct, outfit);
        if (output == null) return;

        if (await SaveAndWear(r, g, root, group, j.SavePath, targetMod, output.LabelFrom,
                              new BodyRetargetWriter.Refit(option, output.Model, output.To, layers), cutFrom, j.Route,
                              item,
                              GameGear(r, j.Redirects, j.OwnRecord),
                              new BodyRetargetWriter.Provenance(source.Dir, source.Option.Rel, targets))
            is not { } savedRoot)
            return;
        log.Information("[Proteus] auto refit: {0} {1} onto {2} in {3:F1}s", item, slot, option,
                        j.Clock.Elapsed.TotalSeconds);
        Say(output.Done(SavedIn(g, savedRoot, targetMod)));
        Finished(item, targetMod.Name);
    }

    /// <summary>
    /// What the piece was made on, read from what it reaches (<see cref="KeepEnds.Find"/>), or as it was read before —
    /// kept apart from the old way of reading by <see cref="KeepEnds.Reading.CacheSlot"/>.
    /// </summary>
    /// <param name="siblings">The chest or legs pieces of its own option (see <see cref="SiblingPiece.Find"/>).</param>
    private KeepEnds.Found ReadEnds(EndsJob j, KeepEnds.Reading reading, Func<string, BodySizeCatalog?> catalogOf,
                                    out IReadOnlyList<SiblingPiece.Candidate> siblings)
    {
        var g = j.G;
        // The siblings are part of the key, as they stand on disk: a piece read by its cloth before it had a sibling to
        // go by must not be served that reading again. Only on a miss are they opened and read.
        void Note(string note) => log.Information("[Proteus] auto refit: {0}: {1}", j.Item, note);
        siblings = SiblingPiece.Applies(j.R.ModRoot, j.Route.To) && j.R.Worn.Rel is { } wornRel
            ? SiblingPiece.Find(j.Redirects, j.OwnRecord, wornRel, reading.Limb, j.Race, Note)
            : [];
        string suffix = SiblingPiece.CacheSuffix(siblings, file => Stamp(j.R.ModRoot!, file));
        string cacheKey = DetectionCache.KeyOf(j.Bytes, reading.CacheSlot + suffix, j.Race, j.TargetMod.Dir,
                                               DetectionCache.BodiesOf(j.State.Version, EnabledBodies(g, j.State),
                                                                       VanillaPrint(j.VanillaCat, reading.Limb, j.Race) + "|"
                                                                     + VanillaPrint(j.VanillaCat, reading.Slot, j.Race)));
        if (detections.TryGet(cacheKey) is { } hit && KeepEnds.FromCache(hit, reading, j.Race, catalogOf) is { } cached)
        {
            log.Information("[Proteus] auto refit: {0} read before (route {1}), not read again", j.Item,
                            KeepEnds.Letter(cached.Route));
            return cached;
        }

        AssertOffFramework("reading the garment");
        bool isVanilla = j.R.ModRoot == null;
        var sibling = siblings.Count > 0 ? ReadSibling(j, siblings, Note) : null;
        var found = KeepEnds.Find(reading, j.Garment, j.Bones, j.Race,
                                  (view, s) => isVanilla
                                      ? VanillaSource(j.VanillaCat, s, j.Race)
                                      : DetectAcross(view, j.Bones, s, j.Race, j.TargetMod, j.State.List, j.VanillaCat,
                                                     g.Permanent),
                                  catalogOf, sibling: sibling,
                                  note: why => Note($"its {(reading.Limb == "_top" ? "chest" : "legs")} piece in the "
                                                  + $"same option was not taken: {why}; read as before"));
        if (found.Route == KeepEnds.Route.Sibling)
            log.Information("[Proteus] auto refit: {0} sized by its {1} piece in the same option: {2} / {3} ({4})", j.Item,
                            reading.Limb == "_top" ? "chest" : "legs", NameOf(j, found.Pick!.Dir), found.Pick.Option.Label,
                            found.Pick.Confidence);
        detections.Put(cacheKey, KeepEnds.ToCache(found));
        return found;
    }

    /// <summary>
    /// What the chest or legs pieces shipped in the same option as this one were made on (see <see cref="SiblingPiece"/>),
    /// each read off its own skin through <see cref="ReadSource"/> — in the slot and race its own refit reads it in
    /// (<see cref="ReadAs"/>), so the reading is the very one that refit makes and keeps. Null when they cannot be read
    /// or do not agree.
    /// </summary>
    private AutoRefitDecisions.Pick? ReadSibling(EndsJob j, IReadOnlyList<SiblingPiece.Candidate> candidates,
                                                 Action<string> note)
    {
        string root = j.R.ModRoot!;
        return SiblingPiece.Agree(candidates, c =>
        {
            if (ReadAs(c.GamePath) is not { Slot: { } slot, Race: var race }) return null;
            if (SiblingPiece.TryOpen(root, c.File, note) is not { } opened) return null;
            var (bytes, model, bones) = opened;
            return ReadSource(j.G, j.State, j.TargetMod, j.VanillaCat, bytes, model, bones, slot, race, false,
                              j.Item + " (" + SiblingPiece.Shown(c.Source) + ")").Source;
        }, note);
    }

    /// <summary>
    /// The chest or legs piece of its own option that the piece is kept outside (see <see cref="OutfitPartners"/>): as
    /// worn beside it at the walk, refitted in this batch or already a refit for the size chosen. Null, logged, when it
    /// is not on that size.
    /// </summary>
    private OutfitPartners.Partner? PartnerOf(EndsJob j, string limb, SiblingPiece.Candidate sibling, KeepEnds.Refit refit)
    {
        Beside? beside = null;
        j.R.Beside?.TryGetValue(limb, out beside);
        PieceOutcome? outcome = null;
        if (beside != null) outcomes.TryGetValue(OutcomeKey(limb, beside.Worn), out outcome);
        string root = j.R.ModRoot!;
        var partner = OutfitPartners.ForAutoRefit(root, sibling, limb, refit.Source, refit.Target.Rel, beside?.ModRoot,
                                                  beside?.Worn.GamePath, beside?.Worn.Rel, outcome,
                                                  BodyRetargetWriter.ReadRecord(root),
                                                  note => log.Information("[Proteus] auto refit: {0}: {1}", j.Item, note));
        if (partner != null)
            log.Information("[Proteus] auto refit: {0} is kept outside its {1} piece as drawn now: {2}", j.Item,
                            limb == "_top" ? "chest" : "legs", partner.RefittedRel);
        return partner;
    }

    /// <summary>A file's state on disk — its length and time written — for a cache key; "-" when it is not there.</summary>
    private static string Stamp(string root, string file)
    {
        var info = new FileInfo(BodySizeCatalog.ResolveCaseInsensitive(root, file));
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-";
    }

    /// <summary>A body mod as the log names it.</summary>
    private static string NameOf(EndsJob j, string dir)
        => dir == VanillaBodyCatalog.Key ? Strings.Parts.RetargetFromVanilla : j.State.Find(dir)?.Name ?? dir;

    /// <summary>
    /// The skin of a piece that reaches no leg or arm, drawn the way the new body draws its own (see
    /// <see cref="RefitCore.MatchSkinToBody(byte[], string, string, UVRemapService?, bool, IPluginLog)"/>); its shape is
    /// the author's. Null when nothing changes — it is drawn so already, or its layout cannot be converted — and the piece
    /// is not baked for another race, whose bake is itself a change worth saving. One only bent down to be read is not:
    /// the bend there and back changes nothing.
    /// </summary>
    /// <param name="refused">The redraw could not be bent back into the drawn race, which the player has been told.</param>
    private EndsOutput? RedrawSkin(EndsJob j, KeepEnds.SkinOnly skin, Func<string, BodySizeCatalog?> catalogOf,
                                   out bool refused)
    {
        refused = false;
        var ownCat = catalogOf(skin.Own.Dir)!;
        string targetPath = j.TargetMod.Catalog.PathOf(skin.Target);
        var model = RefitCore.MatchSkinToBody(j.Bytes, ownCat.PathOf(skin.Own.Option), targetPath, uvRemap, j.Male, log);
        if (j.Route.ChangesModel) model = RacialModelBake.SkinLikeBody(model, File.ReadAllBytes(targetPath), out _);
        else if (ReferenceEquals(model, j.Bytes)) return null;
        // Bent down to be read, its skin redrawn: bent back up to be saved where it is drawn.
        else if (j.Route.Down)
        {
            var (finished, _, bentBack) = RefitCore.Finish(KeepEnds.SkinOnlyPlanned(model), j.Route, j.G.Wearer,
                                                           targetPath, log);
            if (finished == null)
            {
                Fail(j.R, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"),
                                        j.Item, bentBack));
                refused = true;
                return null;
            }
            model = finished.Model;
        }

        string sourceName = skin.Own.Dir == VanillaBodyCatalog.Key ? Strings.Parts.RetargetFromVanilla
                          : j.State.Find(skin.Own.Dir)?.Name ?? skin.Own.Dir;
        string option = j.Pair?.Plan.Option ?? KeepEnds.SkinOnlyOption(j.TargetMod.Name);
        return new EndsOutput(model, KeepEnds.SkinOnlyLabel, RefitCore.LabelFrom(sourceName, [skin.Own.Option.Label]),
                              savedIn => string.Format(Loc.Localize("Chat.AutoRefit.SkinOnly.Fmt",
                                  "[Proteus] {0} keeps its own shape; its skin is now drawn like {1}'s. Saved as \"{2}\" in "
                                + "{3} and switched on."), j.Item, j.TargetMod.Name, option, savedIn));
    }

    /// <summary>
    /// Refit the leg or arm onto <paramref name="refit"/>'s size, the end held, its skin then drawn like the new body's.
    /// Null when the pair cannot be built, which the player has been told.
    /// </summary>
    /// <param name="outfit">The chest or legs piece it is kept outside (see <see cref="PartnerOf"/>); null for none.</param>
    private EndsOutput? RefitEnds(EndsJob j, KeepEnds.Reading reading, KeepEnds.Refit refit, string option,
                                  Func<string, BodySizeCatalog?> catalogOf, CancellationToken ct,
                                  BodyRetarget.Outfit? outfit = null)
    {
        var g = j.G;
        var targetMod = j.TargetMod;
        var targetCat = targetMod.Catalog;
        string item = j.Item, slot = reading.Slot, limb = reading.Limb;
        bool feet = slot == "_sho";
        var pick = refit.Source;
        var sourceCat = catalogOf(pick.Dir)!;
        string sourceName = pick.Dir == VanillaBodyCatalog.Key ? Strings.Parts.RetargetFromVanilla
                          : j.State.Find(pick.Dir)?.Name ?? pick.Dir;
        bool across = !string.Equals(pick.Dir, targetMod.Dir, StringComparison.OrdinalIgnoreCase);
        string targetLabel = targetMod.Name + " — " + refit.Target.Label;
        Say(string.Format(Loc.Localize("Chat.AutoRefit.Start.Fmt",
                "[Proteus] Refitting {0} from {1} to {2}. It will be switched on when it is done."),
                item, sourceName + " — " + pick.Option.Label, targetLabel));
        current = current with { Message = string.Format(Strings.AutoRefit.WorkingFmt, item), Failed = false };

        ushort? SourceMask(string s) => RefitCore.MaskOf(pick.Dir, sourceCat, s, OptionsOf(g.Settings, pick.Dir));
        ushort? TargetMask(string s) => RefitCore.MaskOf(targetMod.Dir, targetCat, s, OptionsOf(g.Settings, targetMod.Dir));

        string sourcePath = sourceCat.PathOf(pick.Option), targetPath = targetCat.PathOf(refit.Target);
        // The feet or hands, for their shape only: they run up the shin or forearm, under the stocking or sleeve.
        bool withEnd = refit is { EndFrom: not null, EndTo: not null };
        if (KeepEnds.BuildPairs(reading, sourcePath, targetPath,
                                withEnd ? sourceCat.PathOf(refit.EndFrom!) : null,
                                withEnd ? targetCat.PathOf(refit.EndTo!) : null,
                                j.Male, s => (SourceMask(s), TargetMask(s)), SlotName, uvRemap,
                                why => log.Information("[Proteus] auto refit: {0} refitted without the {1}' shape: {2}",
                                                       item, feet ? "feet" : "hands", why),
                                out var limbPair, out var endPair) is { } refusal)
        {
            Fail(j.R, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"),
                                    item, refusal));
            return null;
        }
        ct.ThrowIfCancellationRequested();

        AssertOffFramework("the refit");
        var plan = KeepEnds.Plan(j.Garment, j.Bytes, reading, limbPair, endPair, across,
                                 line => log.Debug("[Proteus] auto refit: {0}", line),
                                 line => log.Warning("[Proteus] auto refit: {0}", line), outfit);
        if (outfit != null)
            log.Information("[Proteus] auto refit: {0} kept outside its {1} piece: {2} points lifted, {3:F1} mm short at "
                          + "most", item, limb == "_top" ? "chest" : "legs", plan.Report.Layered,
                            plan.Report.LayerResidual * 1000f);
        plan = RefitCore.MatchSkinToBody(plan, sourcePath, targetPath, uvRemap, j.Male, log);
        ct.ThrowIfCancellationRequested();
        var (seamEdges, stretched) = KeepEnds.SeamStretch(j.Garment, reading.Ends, plan);
        log.Information("[Proteus] auto refit: {0} {1} by its {2} ({3} folded, moved up to {4:F1} mm, the {5} "
                      + "seam stretched up to {6:F1} mm over {7} edges)", item, slot, limb, plan.Report.Folded,
                        plan.Report.WorstMove * 1000f, feet ? "ankle" : "wrist", stretched * 1000f, seamEdges);
        if (stretched > KeepEnds.SeamWarn)
            log.Warning("[Proteus] auto refit: {0}'s {1} seam stretched by {2:F1} mm — the kept {3} and the new body's {4} "
                      + "differ there; look at it in game", item, feet ? "ankle" : "wrist", stretched * 1000f,
                        feet ? "foot" : "hand", feet ? "legs" : "arms");
        if (RefitCore.ReleasedLog(plan.Report, reading.FreeNames ?? []) is { } released)
            log.Information("[Proteus] auto refit: {0}: let go of kept piece(s) that would sink moved whole: {1}", item,
                            released);

        // After the seam is measured, which compares the plan with the garment as it was refitted: up, the kept skin
        // named like the body's; down, bent back into the drawn race — or not saved at all.
        var (finished, _, bentBack) = RefitCore.Finish(plan, j.Route, g.Wearer, targetPath, log);
        if (finished == null)
        {
            Fail(j.R, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"),
                                    item, bentBack));
            return null;
        }
        return new EndsOutput(finished.Model, refit.Target.Label,
                              RefitCore.LabelFrom(across ? sourceName : null, [pick.Option.Label]),
                              savedIn => string.Format(Loc.Localize("Chat.AutoRefit.Done.Fmt",
                                  "[Proteus] {0} now fits {1}. Saved as \"{2}\" in {3} and switched on."),
                                  item, targetLabel, option, savedIn));
    }

    private void SayUnsure(string item)
        => Say(string.Format(Loc.Localize("Chat.AutoRefit.Unsure.Fmt",
                   "[Proteus] Couldn't tell which body {0} was made for, so it was left as it is. You can refit it by "
                 + "hand with the Studio's Body size tool."), item), problem: true);
}
