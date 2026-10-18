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
/// man's shirt for Highlander men — whoever is logged in (see <see cref="OtherRaceRefit"/>).
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

    /// <summary>What the bake for a picked race reads, gathered once per garment and race.</summary>
    private OtherRaceWearer.Inputs? raceInputs;
    private string? raceInputsFor;

    /// <summary>The race picked for the open garment; none once another model opens.</summary>
    private ushort ChosenRace(in RetargetContext ctx) => raceChoice.For(ctx.ModelRel);

    private IReadOnlyList<OtherRaceRefit.Candidate> Offered(in RetargetContext ctx)
    {
        var key = (ctx.ModelRel, (object?)catalog, (object?)ctx.Redirects, (object?)ownRecord);
        if (offeredFor == key) return offered;
        offeredFor = key;
        offered = OtherRaceRefit.Offered(ctx.GamePath, catalog, ctx.Redirects, ctx.ModRoot != null ? ownRecord : null,
                                         ctx.IsVanilla);
        return offered;
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
        using (var combo = ImRaii.Combo("##retargetRace", EntryLabel(current)))
            if (combo)
                foreach (var entry in entries)
                    if (ImGui.Selectable(EntryLabel(entry) + "##race" + entry.Race + entry.Kind, entry == current)
                        && entry != current)
                    {
                        raceChoice = new OtherRaceRefit.Choice(ctx.ModelRel, entry.Race);
                        log.Information("[Proteus] retarget: {0} to be refitted for {1}", ctx.ModelRel,
                                        entry.Race == 0 ? "the character" : $"c{entry.Race:D4}");
                    }

        // A race the mod already has a model of: that model is the one to refit, and it is a click away.
        if (list.FirstOrDefault(c => c.Race == chosen && c.Blocked == OtherRaceRefit.Block.OwnModel) is { } own)
        {
            string name = ModelRace.Describe($"{own.Race:D4}");
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(string.Format(ps.RetargetRaceOwnModelFmt, name));
            if (own.OwnModelPath != null && ctx.OpenModelAt?.Invoke(own.OwnModelPath) is { } open
                && ImGui.Button(string.Format(ps.RetargetRaceOpenModelFmt, name), FullWidth()))
                open();
        }
        ImGui.Spacing();
    }

    private string EntryLabel(OtherRaceRefit.Entry entry) => entry.Kind switch
    {
        OtherRaceRefit.EntryKind.ThisModel => string.Format(Strings.Parts.RetargetRaceThisModelFmt,
                                                            ModelRace.Describe($"{entry.Race:D4}")),
        OtherRaceRefit.EntryKind.AsWorn => string.Format(Strings.Parts.RetargetRaceAsWornFmt,
                                                         ModelRace.Describe($"{automaticRoute.To:D4}")),
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
        ImGui.TextWrapped(string.Format(ps.RetargetRaceOnlyFmt, to, drawn));
        ImGui.TextDisabled(string.Format(ps.RetargetRaceSourceBakedFmt, to));
        if (b.Est is { Outcome: EstOutcome.Carry } est)
            ImGui.TextWrapped(string.Format(ps.RetargetRaceEstCarriedFmt, to, drawn, est.Entry));
        if (b.FellBack)
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(string.Format(ps.RetargetRaceSkeletonFallbackFmt, to));
        string? preview = b.Preview switch
        {
            OtherRaceRefit.PreviewNote.OtherRace => string.Format(ps.RetargetRacePreviewOtherFmt, to),
            OtherRaceRefit.PreviewNote.NotDrawn => string.Format(ps.RetargetRacePreviewNotDrawnFmt, to),
            _ => null,
        };
        if (preview != null) ImGui.TextDisabled(preview);
    }

    /// <summary>
    /// The bodies the garment was made on, OF THE DRAWN RACE — what a refit for a picked race bends alongside it. The
    /// game's own is read for that race here, on the thread that may read the game's data: the panel's own copy follows
    /// the race the lists are filtered by, which is the picked one by now.
    /// </summary>
    private BodySizeCatalog? MadeForOfDrawnRace(string drawn)
        => fromBodyDir == VanillaBodyCatalog.Key ? VanillaBodyCatalog.Read(drawn, readGameFile, drawn: true)
         : fromCatalog ?? catalog;

    /// <summary>
    /// Gather, on the framework thread, what the bake for <paramref name="race"/> reads: the race's base skeleton as the
    /// collection gives it and as the game ships it, the game's deformer, and the extra-skeleton entries that may apply
    /// with the skeletons they name — those only from the mod's own files or the game's.
    /// </summary>
    private OtherRaceWearer.Inputs RaceInputs(in RetargetContext ctx, ushort race)
    {
        string key = ctx.ModelRel + "|" + race;
        if (raceInputs != null && raceInputsFor == key) return raceInputs;

        var given = ctx;
        raceInputs = OtherRaceWearer.Gather(race, ctx.GamePath, ReadGame, penumbra.ResolvePlayer,
                                            path => ModFile(given, path),
                                            ctx.ModRoot != null ? ModEstEntries(ctx) : [],
                                            (file, ex) => log.Warning(ex, "[Proteus] retarget: could not read {0}", file));
        raceInputsFor = key;
        return raceInputs;
    }

    private byte[]? ReadGame(string path)
    {
        try { return readGameFile(path); }
        catch (Exception) { return null; }   // the game's reader throws for a path it does not have
    }

    /// <summary>A file of the garment's own mod published at <paramref name="gamePath"/>, read; null when it has none.</summary>
    private static byte[]? ModFile(in RetargetContext ctx, string gamePath)
    {
        if (ctx.ModRoot == null) return null;
        foreach (var r in ctx.Redirects)
        {
            if (!string.Equals(r.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)) continue;
            try { return File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(ctx.ModRoot, r.File)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>
    /// The Est entries that apply with the open model: those of every option that publishes it, and the mod's default
    /// data — what the game reads beside the model when that option is on.
    /// </summary>
    private static List<EstTable.ModEntry> ModEstEntries(in RetargetContext ctx)
    {
        var found = new List<EstTable.ModEntry>();
        if (ctx.ModRoot is not { } root) return found;
        if (PenumbraModMeta.TryReadDefaultData(root) is { } dflt) found.AddRange(EstTable.FromManipulations(dflt.Manipulations));

        string want = MeshVolumeService.Rel(ctx.ModelRel);
        var sources = ctx.Redirects.Where(r => string.Equals(MeshVolumeService.Rel(r.File), want, StringComparison.OrdinalIgnoreCase))
                                   .Select(r => r.Source).Where(s => s.Contains(" / ", StringComparison.Ordinal))
                                   .Distinct(StringComparer.Ordinal).ToList();
        foreach (var (group, json) in PenumbraModMeta.TryReadGroups(root) ?? [])
            foreach (var option in PenumbraModMeta.FileOptionsOf(json) ?? [])
                if (sources.Contains($"{group} / {option.Name}", StringComparer.Ordinal))
                    found.AddRange(EstTable.FromManipulations(option.Manipulations));
        return found;
    }

    /// <summary>
    /// Bake for a picked race: the wearer made from files, the garment baked with it, and the bodies it was made on bent
    /// the same way — all on a worker, from what <see cref="RaceInputs"/> gathered here.
    /// </summary>
    private void StartRaceBake(in RetargetContext given, RacialModelBake.Route route, string key)
    {
        var bytes = given.GarmentBytes;
        string savePath = route.SavePath(given.GamePath);
        string drawnPath = given.GamePath;
        var inputs = RaceInputs(given, route.To);
        string drawnCode = $"{route.Drawn:D4}";
        var source = MadeForOfDrawnRace(drawnCode);
        var slots = catalog?.SlotsFor($"{route.To:D4}").ToList() ?? [];

        bakeTask = Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            if (ModelSkinReader.Read(bytes, null, null) is not { } skin)
                return (key, (Baked?)null, Strings.Parts.RetargetBakeReadBack);
            var made = OtherRaceWearer.Build(inputs, skin);
            if (made.Wearer is not { } who) return (key, null, made.Refusal);

            var model = route.Into(bytes, who, out string why);
            if (model == null) return (key, null, why);
            if (ModelPartReader.Read(model) is not { } parts) return (key, null, Strings.Parts.RetargetBakeReadBack);

            BodySizeCatalog? bodies = null;
            if (source != null)
            {
                bodies = BakedBodyCatalog.Of(source, drawnCode, route.To, who, slots, out var failed);
                foreach (string f in failed) log.Warning("[Proteus] retarget: a made-for body could not be bent: {0}", f);
            }
            log.Information("[Proteus] retarget: c{0:D4} wearer from {1}{2}; est {3}; {4} made-for bodies bent in {5} ms",
                            route.To, inputs.ModdedFrom ?? "the game", made.FellBack ? " (unreadable, the game's used)" : "",
                            made.Est.Outcome, bodies?.Options.Count ?? 0, clock.ElapsedMilliseconds);
            return (key, new Baked(key, bytes, route, model, parts, savePath, who)
            {
                Explicit = true, Bodies = bodies, Est = made.Est, FellBack = made.FellBack, DrawnPath = drawnPath,
            }, "");
        });
    }

    /// <summary>
    /// A bake for a picked race has landed: say how the preview relates to what that race will see, and when the
    /// character IS that race, how the skeleton read from the file compares with the live one.
    /// </summary>
    private void OnRaceBaked(Baked made)
    {
        ushort live = wearer?.Race ?? 0;
        bool draws = made.DrawnPath != null && penumbra.GetActivePlayerModelPaths()?.Contains(made.DrawnPath) == true;
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
