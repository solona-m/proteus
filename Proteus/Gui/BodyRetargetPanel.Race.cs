using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The "Refit for" choice: refitting a garment for another race of its sex than the one it is drawn for — a Midlander
/// man's shirt for Highlander men, or a Highlander-only one for Midlanders — whoever is logged in (see
/// <see cref="OtherRaceRefit"/>).
/// </summary>
internal sealed partial class BodyRetargetPanel
{
    /// <summary>The race picked in "Refit for", for the model it was picked on.</summary>
    private OtherRaceRefit.Choice raceChoice;

    /// <summary>The races offered for the open garment, and what they were worked out from.</summary>
    private IReadOnlyList<OtherRaceRefit.Candidate> offered = [];
    private (string? Model, object? Catalog, object? Redirects, object? Record) offeredFor;

    /// <summary>The route the character's race decides, this frame — what "as before" means in the list.</summary>
    private RacialModelBake.Route automaticRoute;

    /// <summary>What the bake for a picked race reads, gathered once per garment, race and manifest read.</summary>
    private OtherRaceWearer.Inputs? raceInputs;
    private string? raceInputsFor;
    private object? raceInputsRedirects;

    /// <summary>
    /// The refit-onto mod's bodies as the refit uses them. For a picked race, that race's own alone: a part the mod has
    /// no body of that race for would be refitted from the bent "made for" body onto an unbent one of another race, and
    /// its cloth would keep the whole racial bend on a body that does not have it. Such a part sits out. Otherwise the
    /// mod's catalog as it is, whose lists fall back across races of one sex the way they always have.
    /// </summary>
    private BodySizeCatalog? Onto
    {
        get
        {
            if (active is not { Explicit: true } b || catalog == null) return catalog;
            if (!ReferenceEquals(ontoOf, catalog) || ontoRace != b.Route.To)
            {
                ontoOf = catalog;
                ontoRace = b.Route.To;
                onto = OtherRaceRefit.OnlyRace(catalog, b.Route.To);
            }
            return onto;
        }
    }

    private BodySizeCatalog? onto, ontoOf;
    private ushort ontoRace;

    /// <summary>The "made for" bodies the sizes chosen now were chosen from.</summary>
    private BodySizeCatalog? madeForOf;

    /// <summary>Counts the changes of <see cref="madeForOf"/>, so a plan made against other bodies goes stale.</summary>
    private int madeForGeneration;

    /// <summary>
    /// The "made for" bodies changed under an open garment without its race changing — a pick put in force or taken back
    /// where the character's own route reaches the same race, so bent bodies came or went. A size chosen from the others
    /// names a file of theirs, so every "made for" choice and what was worked out from it goes, and detection runs again.
    /// </summary>
    private void OnMadeForChanged(in RetargetContext ctx)
    {
        var now = SourceCatalog;
        if (ReferenceEquals(now, madeForOf)) return;
        bool had = madeForOf != null;
        madeForOf = now;
        madeForGeneration++;
        if (!had) return;
        from.Clear();
        detected.Clear();
        detectedFor = null;
        refusals.Clear();
        validating.Clear();
        DropPlan(ctx);
    }

    /// <summary>The race picked for the open garment; none once another model opens.</summary>
    private ushort ChosenRace(in RetargetContext ctx) => raceChoice.For(ctx.ModelRel);

    private IReadOnlyList<OtherRaceRefit.Candidate> Offered(in RetargetContext ctx)
    {
        var key = (ctx.ModelRel, (object?)catalog, (object?)ctx.Redirects, (object?)ownRecord);
        if (offeredFor == key) return offered;
        offeredFor = key;
        offered = OtherRaceRefit.Offered(ctx.GamePath, catalog, ctx.Redirects, ctx.ModRoot != null ? ownRecord : null,
                                         ctx.IsVanilla, ctx.IsVanilla ? GameHas : null);
        return offered;
    }

    /// <summary>Whether the game's own data ships a file at a game path, asked once per path and garment.</summary>
    private readonly Dictionary<string, bool> gameHas = new(StringComparer.OrdinalIgnoreCase);

    private bool GameHas(string gamePath)
    {
        if (!gameHas.TryGetValue(gamePath, out bool has)) gameHas[gamePath] = has = gameFileExists(gamePath);
        return has;
    }

    /// <summary>
    /// The "Refit for" dropdown, under the body mod refitted onto: shown only when that mod has a body of another race
    /// of the garment's sex, which is what makes the choice mean anything.
    /// </summary>
    private void DrawRacePicker(in RetargetContext ctx)
    {
        var list = Offered(ctx);
        if (list.Count == 0) return;
        var ps = Strings.Parts;
        ushort drawn = ModelSkinReader.RaceOf(ctx.GamePath);
        ushort chosen = ChosenRace(ctx);
        var entries = OtherRaceRefit.PickerEntries(automaticRoute, drawn, list);
        var current = OtherRaceRefit.Current(entries, chosen, automaticRoute, drawn);

        ImGui.TextUnformatted(ps.RetargetRaceLabel);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.RetargetRaceTip);
        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##retargetRace", EntryLabel(current, drawn)))
            if (combo)
                foreach (var entry in entries)
                    // The entry already chosen can be clicked again after a refusal: a pick is a new try, with the
                    // skeleton and files read afresh — the refusal may be gone now (a skeleton mod enabled since).
                    if (ImGui.Selectable(EntryLabel(entry, drawn) + "##race" + entry.Race + entry.Kind, entry == current)
                        && (entry != current || refusedTo != 0))
                    {
                        // Nothing picked and the entry shown clicked again: the character's own bake is retried as it
                        // is, not turned into a pick read from files.
                        if (!(chosen == 0 && entry == current))
                            raceChoice = new OtherRaceRefit.Choice(ctx.ModelRel, entry.Race);
                        bakeRefusalFor = null;
                        raceInputs = null;
                        raceInputsFor = null;
                        log.Information("[Proteus] retarget: {0} to be refitted for {1}", ctx.ModelRel,
                                        entry.Race == 0 ? "the character" : $"c{entry.Race:D4}");
                    }

        // A race the mod already has a model of: refitted all the same — it may be no more than a copy of the game's —
        // but said, since a save into the mod takes its place, with that model a click away for refitting it instead.
        if (OtherRaceRefit.OwnModelOf(chosen, list) is { } own)
        {
            string name = ModelRace.Describe($"{own.Race:D4}");
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(string.Format(ps.RetargetRaceOwnModelReplacedFmt, name));
            if (own.OwnModelPath != null && ctx.OpenModelAt?.Invoke(own.OwnModelPath) is { } open
                && ImGui.Button(string.Format(ps.RetargetRaceOpenModelFmt, name), FullWidth()))
                open();
        }

        // The game's gear picked for a race the game has its own model of: the save takes that model's place while its mod
        // is on — said from the pick on, before anything is saved, with the game's model a click away.
        if (OtherRaceRefit.ReplacesGame(chosen, list) is { GameModelPath: { } gameModel } game)
        {
            string name = ModelRace.Describe($"{game.Race:D4}");
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(string.Format(ps.RetargetRaceReplacesGameFmt, name));
            if (ctx.OpenModelAt?.Invoke(gameModel) is { } open
                && ImGui.Button(string.Format(ps.RetargetRaceOpenModelFmt, name) + "##game", FullWidth()))
                open();
        }
        ImGui.Spacing();
    }

    private string EntryLabel(OtherRaceRefit.Entry entry, ushort drawn) => entry.Kind switch
    {
        OtherRaceRefit.EntryKind.ThisModel => string.Format(Strings.Parts.RetargetRaceThisModelFmt,
                                                            ModelRace.Describe($"{entry.Race:D4}")),
        OtherRaceRefit.EntryKind.AsWorn => string.Format(Strings.Parts.RetargetRaceAsWornFmt,
                                                         ModelRace.Describe($"{automaticRoute.To:D4}")),
        OtherRaceRefit.EntryKind.OtherSex => string.Format(Strings.Parts.RetargetRaceOtherSexFmt,
                                                           ModelRace.Describe($"{entry.Race:D4}"),
                                                           ModelRace.Describe($"{drawn:D4}")),
        _ => ModelRace.Describe($"{entry.Race:D4}"),
    };

    /// <summary>
    /// What is said under a refit for a picked race: who will wear it, that the bodies it was made on were bent too,
    /// any extra skeleton carried over, a skeleton that had to come from the game, and what the live preview shows.
    /// </summary>
    private void DrawRaceNotes(Baked b)
    {
        if (!b.Explicit) return;
        var ps = Strings.Parts;
        string to = ModelRace.Describe($"{b.Route.To:D4}");
        string drawn = ModelRace.Describe($"{b.Route.Drawn:D4}");
        if (b.Route.ByAlias)
        {
            // Across the sexes the races wearing it are named: a c0101 save reaches Lalafell women too.
            var also = b.Wearers.Select(r => ModelRace.Describe($"{r:D4}"));
            ImGui.TextWrapped(string.Format(ps.RetargetRaceCrossSexWearsFmt, to, string.Join(", ", also), drawn));
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(string.Format(ps.RetargetRaceCrossSexNoteFmt, drawn, to));
            // The game's gear: a man whose race has the game's own model of it keeps drawing that, not the save.
            ushort character = wearer?.Race ?? 0;
            if (b.IsVanilla && b.DrawnPath != null && OtherRaceRefit.MissesCharacter(character, b.Route, b.Wearers)
                && GameHas(RacialModelBake.WithRace(b.DrawnPath, character)))
                using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                    ImGui.TextWrapped(string.Format(ps.RetargetRaceCharacterKeepsGameFmt,
                                                    ModelRace.Describe($"{character:D4}")));
        }
        else
            ImGui.TextWrapped(string.Format(OtherRaceRefit.Wears(b.Route) == OtherRaceRefit.WearNote.PickedAndFallThrough
                                                ? ps.RetargetRaceBelowFmt : ps.RetargetRaceOnlyFmt, to, drawn));
        ImGui.TextDisabled(string.Format(ps.RetargetRaceSourceBakedFmt, to));
        if (b.Est is { Outcome: EstOutcome.Carry } est)
            ImGui.TextWrapped(string.Format(ps.RetargetRaceEstCarriedFmt, to, drawn, est.Entry));
        if (OtherRaceRefit.WarnsFallThroughBones(b.Route, b.Est))
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(string.Format(ps.RetargetRaceBelowExtraBonesFmt, to));
        if (b.FellBack)
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(string.Format(ps.RetargetRaceSkeletonFallbackFmt, to));
        if (b.Moves.Count > 0)
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(string.Format(ps.RetargetRaceBonesMovedFmt, to, b.Moves.Count, SkeletonRemap.Describe(b.Moves)));
        if (b.NeedsSkeleton.Count > 0)
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(string.Format(ps.RetargetRaceNeedsSkeletonFmt, to, b.SkeletonFrom,
                                                b.NeedsSkeleton.Count, string.Join(", ", b.NeedsSkeleton.Take(12))));
        if (b.LeftOut.Count > 0)
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(string.Format(ps.RetargetRaceLeftOutFmt, string.Join("; ", b.LeftOut)));
        string? preview = b.Preview switch
        {
            OtherRaceRefit.PreviewNote.OtherRace => string.Format(ps.RetargetRacePreviewOtherFmt, to),
            OtherRaceRefit.PreviewNote.NotDrawn => string.Format(ps.RetargetRacePreviewNotDrawnFmt, to),
            _ => null,
        };
        if (preview != null) ImGui.TextDisabled(preview);
    }

    /// <summary>
    /// The mod a skeleton file comes from, by its folder — the nearest one up from the file with a mod's meta.json — else
    /// the file's own name.
    /// </summary>
    internal static string SkeletonMod(string? file)
    {
        if (string.IsNullOrEmpty(file)) return "";
        for (var dir = Path.GetDirectoryName(file); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "meta.json"))) return Path.GetFileName(dir);
        return Path.GetFileName(file);
    }

    /// <summary>
    /// Gather, on the framework thread, what the bake for <paramref name="race"/> reads: the race's base skeleton as the
    /// collection gives it and as the game ships it, the game's deformer, and the extra-skeleton entries that may apply
    /// with the skeletons they name — from the mod's default files, the option the refit is cut from (see
    /// <see cref="BakeCutFrom"/>), or the game's.
    /// </summary>
    private OtherRaceWearer.Inputs RaceInputs(in RetargetContext ctx, ushort race, string? cutFrom)
    {
        // Keyed as the bake is: a manifest edited under the open panel can change what the option gives without changing
        // its name, and the bake made again for it must read it again.
        string key = ctx.ModelRel + "|" + race + "|" + cutFrom + "|" + BakeFingerprint(ctx);
        if (raceInputs != null && raceInputsFor == key && ReferenceEquals(raceInputsRedirects, ctx.Redirects))
            return raceInputs;
        raceInputsRedirects = ctx.Redirects;

        var given = ctx;
        raceInputs = OtherRaceWearer.Gather(race, ctx.GamePath, ReadGame, penumbra.ResolvePlayer,
                                            path => ModFile(given, cutFrom, path),
                                            ctx.ModRoot != null ? ModEstEntries(ctx, cutFrom) : [],
                                            (file, ex) => log.Warning(ex, "[Proteus] retarget: could not read {0}", file));
        raceInputsFor = key;
        return raceInputs;
    }

    /// <summary>
    /// The option a refit of the open model saved where Save now points is cut from, as the mod spells it ("Group /
    /// Option"); null for a mod of its own, the game's gear, or a group that publishes no such option. Its files are what
    /// the saved option carries, so the extra skeleton is looked for there.
    /// </summary>
    private string? BakeCutFrom(in RetargetContext ctx)
    {
        if (toNewMod || ctx.ModRoot == null) return null;
        // Asked every frame a race is picked, and a walk over every redirect of a manifest that can be large.
        string destination = Destination();
        if (!ReferenceEquals(cutFromRedirects, ctx.Redirects) || cutFromModel != ctx.ModelRel
            || cutFromDestination != destination)
        {
            cutFromRedirects = ctx.Redirects;
            cutFromModel = ctx.ModelRel;
            cutFromDestination = destination;
            cutFromAnswer = OtherRaceRefit.CutFrom(ctx.Redirects, ctx.ModelRel, destination);
        }
        return cutFromAnswer;
    }

    private object? cutFromRedirects;
    private string? cutFromModel, cutFromDestination, cutFromAnswer;

    /// <summary>
    /// What the option a save now goes into gives the bake to read (<see cref="OtherRaceRefit.EstFingerprint"/>): part of
    /// the bake's key, so a change of where to save makes the model again only when it changes what will load — and
    /// checked again when saving. Read once per option: the option's entries are in the manifest, which is not read per
    /// frame.
    /// </summary>
    private string BakeFingerprint(in RetargetContext ctx)
    {
        string? cutFrom = BakeCutFrom(ctx);
        if (ReferenceEquals(fingerprintRedirects, ctx.Redirects) && fingerprintCutFrom == cutFrom
            && fingerprintModel == ctx.ModelRel)
            return fingerprint;
        fingerprintRedirects = ctx.Redirects;
        fingerprintCutFrom = cutFrom;
        fingerprintModel = ctx.ModelRel;
        var optionEst = cutFrom != null && ctx.ModRoot is { } root
            ? OtherRaceRefit.EstEntriesFor(PenumbraModMeta.TryReadGroups(root), null, cutFrom)
            : [];
        fingerprint = OtherRaceRefit.EstFingerprint(ctx.Redirects, cutFrom, optionEst,
                                                    BodySizeCatalog.SlotOf(ctx.GamePath) ?? "_top",
                                                    PenumbraManipulations.ParseSetId(ctx.GamePath, 'e') ?? 0);
        return fingerprint;
    }

    private string fingerprint = "";
    private object? fingerprintRedirects;
    private string? fingerprintCutFrom, fingerprintModel;

    private byte[]? ReadGame(string path)
    {
        try { return readGameFile(path); }
        catch (Exception ex)
        {
            // The game's reader throws for a path it does not have as readily as it returns null, and most asked for
            // here are only maybe there (an extra skeleton per entry): said at debug, with why, rather than not at all.
            log.Debug("[Proteus] retarget: the game gave nothing for {0}: {1}", path, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// A file the garment's own mod publishes at <paramref name="gamePath"/> where a refit cut from
    /// <paramref name="cutFrom"/> loads it — its default files or that option (see <see cref="OtherRaceRefit.CarriedFile"/>)
    /// — read; null when it has none.
    /// </summary>
    private static byte[]? ModFile(in RetargetContext ctx, string? cutFrom, string gamePath)
    {
        if (ctx.ModRoot == null || OtherRaceRefit.CarriedFile(ctx.Redirects, cutFrom, gamePath) is not { } rel)
            return null;
        try { return File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(ctx.ModRoot, rel)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The Est entries that apply with a refit cut from <paramref name="cutFrom"/>: that option's first — Penumbra lays an
    /// option over the default data, so where both set one, the option's is the one the game reads — then the mod's
    /// default data.
    /// </summary>
    private static List<EstTable.ModEntry> ModEstEntries(in RetargetContext ctx, string? cutFrom)
        => ctx.ModRoot is not { } root ? []
         : OtherRaceRefit.EstEntriesFor(PenumbraModMeta.TryReadGroups(root),
                                        PenumbraModMeta.TryReadDefaultData(root)?.Manipulations, cutFrom);

    /// <summary>
    /// Bake for a picked race: the wearer made from files, the garment baked with it, and the bodies it was made on bent
    /// the same way — all on a worker, from what <see cref="RaceInputs"/> gathered here.
    /// </summary>
    private void StartRaceBake(in RetargetContext given, RacialModelBake.Route route, string key)
    {
        var bytes = given.GarmentBytes;
        string savePath = route.SavePath(given.GamePath);
        string drawnPath = given.GamePath;
        bool vanilla = given.IsVanilla;
        string? cutFrom = BakeCutFrom(given);
        string print = BakeFingerprint(given);
        var inputs = RaceInputs(given, route.To, cutFrom);
        string drawnCode = $"{route.Drawn:D4}";
        // Down to a race the drawn one falls through to, the game's bodies are each race's own: the one a Highlander
        // draws bent is the Midlander's, and bending it down again would bend it twice.
        var source = route.Lowered
            ? OtherRaceRefit.MadeForBelow(fromBodyDir, fromCatalog, catalog, route.Drawn, route.To, readGameFile)
            : OtherRaceRefit.MadeFor(fromBodyDir, fromCatalog, catalog, drawnCode,
                                     code => VanillaBodyCatalog.Read(code, readGameFile, drawn: true));
        // Only the parts the refit-onto mod has a body of the race for take part (see Onto), so only those are bent.
        var slots = catalog != null ? OtherRaceRefit.OnlyRace(catalog, route.To).SlotsFor($"{route.To:D4}").ToList() : [];
        string? garmentSlot = BodySizeCatalog.SlotOf(drawnPath);
        // Across the sexes the bodies are matched through the bibo↔TBSE maps; ask for them now, while the bake runs.
        bool mapsHere = !route.ByAlias || uvRemap.MenMapsPresent();
        if (!mapsHere) uvRemap.RequestMenMaps();

        bakeTask = Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            if (ModelSkinReader.Read(bytes, null, null) is not { } skin)
                return (key, (Baked?)null, Strings.Parts.RetargetBakeReadBack);
            var made = OtherRaceWearer.Build(inputs, skin);
            if (made.Wearer is not { } who) return (key, null, made.Refusal);

            // The garment's earring nodes and the bones the race lacks, moved onto ones it has before anything bends it;
            // the bodies it was made on are moved the same way (BakedBodyCatalog).
            var moves = made.Moves ?? [];
            var earrings = made.Earrings ?? [];
            byte[] drawnModel = bytes;
            if (made.Renames is { Count: > 0 } renames)
            {
                if (SkeletonRemap.Rename(bytes, renames, out string refusal) is not { } renamed)
                    return (key, null, SkeletonRemap.RenameRefusal(refusal));
                drawnModel = renamed;
                if (earrings.Count > 0)
                    log.Information("[Proteus] retarget: earring bones moved for c{0:D4}: {1}", route.To, SkeletonRemap.DescribeHow(earrings));
                if (moves.Count > 0)
                    log.Information("[Proteus] retarget: bones moved for c{0:D4}: {1}", route.To, SkeletonRemap.DescribeHow(moves));
            }

            var model = route.Into(drawnModel, who, out string why);
            if (model == null) return (key, null, why);
            if (ModelPartReader.Read(model) is not { } parts) return (key, null, Strings.Parts.RetargetBakeReadBack);

            BodySizeCatalog? bodies = null;
            List<string> leftOut = [];
            if (source != null)
            {
                void LogBody(string line) => log.Information("[Proteus] retarget: {0}", line);
                bodies = route.Lowered
                    ? BakedBodyCatalog.Lowered(source, route.Drawn, route.To, who, slots, out leftOut, route.ByAlias,
                                               made.Planner, LogBody)
                    : BakedBodyCatalog.Of(source, drawnCode, route.To, who, slots, out leftOut, route.ByAlias,
                                          made.Planner, LogBody);
                foreach (string f in leftOut) log.Warning("[Proteus] retarget: a made-for body could not be bent: {0}", f);
            }
            if (route.ByAlias)
            {
                log.Information("[Proteus] retarget: across the sexes c{0:D4} → c{1:D4}: {2} made-for bodies bent, maps {3}",
                                route.Drawn, route.To, bodies?.Options.Count ?? 0, mapsHere ? "present" : "requested");
                // Never the target race's own bodies in their place (CrossSexRefit.MadeForLine): with none of the drawn
                // race's for the garment's part, there is nothing it was made on to refit from.
                if (garmentSlot != null && bodies?.For(garmentSlot).Count is null or 0)
                    return (key, null, string.Format(Strings.Parts.RetargetRefuseCrossSexMadeForFmt,
                                                     ModelRace.Describe(drawnCode)));
            }
            log.Information("[Proteus] retarget: c{0:D4} wearer from {1}{2}; est {3}; {4} made-for bodies bent in {5} ms",
                            route.To, inputs.ModdedFrom ?? "the game", made.FellBack ? " (unreadable, the game's used)" : "",
                            made.Est.Outcome, bodies?.Options.Count ?? 0, clock.ElapsedMilliseconds);
            return (key, new Baked(key, bytes, route, model, parts, savePath, who)
            {
                Fingerprint = print,
                Explicit = true, Bodies = bodies, LeftOut = leftOut, Est = made.Est, FellBack = made.FellBack, DrawnPath = drawnPath,
                IsVanilla = vanilla,
                Moves = moves, NeedsSkeleton = made.NeedsSkeleton ?? [], SkeletonFrom = SkeletonMod(inputs.ModdedFrom),
            }, "");
        });
    }

    /// <summary>
    /// A bake for a picked race has landed: say how the preview relates to what that race will see, and when the
    /// character IS that race, how the skeleton read from the file compares with the live one.
    /// </summary>
    /// <param name="redirects">The garment mod's redirects: a race the mod has a model of draws that, not the save.</param>
    private void OnRaceBaked(Baked made, IEnumerable<PenumbraModMeta.Redirect> redirects)
    {
        ushort live = wearer?.Race ?? 0;
        bool draws = made.DrawnPath != null && penumbra.GetActivePlayerModelPaths()?.Contains(made.DrawnPath) == true;
        // Across the sexes, who wears the save: only the races with no model of the item of their own on the way down —
        // one the game ships, or for a mod's garment one the mod publishes (Thorn Princess has its Highlander,
        // Roegadyn and Lalafell men's, and those races keep them).
        if (made.Route.ByAlias && made.DrawnPath is { } drawnPath)
            made.Wearers = CrossSexRefit.FallThroughTo(made.Route.To, made.Route.Drawn,
                CrossSexRefit.OwnModels(drawnPath, made.IsVanilla ? null : redirects, GameHas));
        made.Preview = OtherRaceRefit.NoteFor(draws, live, made.Route.To);

        if (wearer is { } character && character.Race == made.Route.To)
        {
            var differ = OtherRaceWearer.Parity(made.Wearer.Parents, character.Parents);
            if (differ.Count == 0)
                log.Information("[Proteus] retarget: skeleton parity c{0:D4}: the file's parents match the character's",
                                made.Route.To);
            else
                log.Warning("[Proteus] retarget: skeleton parity c{0:D4}: {1}", made.Route.To,
                            string.Join("; ", differ.Take(20)));
        }
    }
}
