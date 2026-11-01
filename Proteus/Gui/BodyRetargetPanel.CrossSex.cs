using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// A refit for the other sex in the Body size panel (see <see cref="CrossSexRefit"/>): the check each size passes before
/// it may be saved, and the fold line said beside the size shown.
/// </summary>
internal sealed partial class BodyRetargetPanel
{
    /// <summary>With <see cref="planned"/> made for the other sex, the cloth faces each size turned over; else null.</summary>
    private IReadOnlyList<int>? plannedCrossSexFolds;

    /// <summary>
    /// One size of a refit for the other sex, measured on every user's garment: matched through too little of the body,
    /// it is never written into a mod — the refusal, worded for the user. Its folds never stop it: they are said, beside
    /// the size, as holes in game. Worker thread.
    /// </summary>
    /// <param name="label">The size, prefixed to what is said when several are made.</param>
    /// <param name="quality">Gets the size's quality line.</param>
    /// <param name="folds">Gets the size's cloth faces turned over.</param>
    private string? CrossSexCheck(byte[] garment, BodyRetarget.Planned plan, UvAtlasCorrespondence.Stats? landed,
                                  string label, bool several, List<string> quality, List<int> folds)
    {
        int folded = CrossSexRefit.ClothFolded(garment, plan);
        double coverage = landed?.VertexCoverage ?? 1.0;
        log.Information("[Proteus] retarget: across the sexes {0}: {1} cloth faces folded, {2:P1} of the body matched",
                        label, folded, coverage);
        var (refused, line) = CrossSexRefit.Judged(coverage, folded);
        if (refused != null) return several ? $"{label}: {refused}" : refused;
        quality.Add(several ? $"{label}: {line}" : line);
        folds.Add(folded);
        return null;
    }

    /// <summary>
    /// A women's shoe or glove made a men's (<see cref="CrossSexRefit.EndsCross"/>): planned by the job the size set and
    /// the CLI run (<see cref="CrossSexRefitJob.RunEnd"/>), built the one way they build it
    /// (<see cref="CrossSexRefitJob.EndInputs"/>), never by the panel's own kept-end plan. Its foot or hand is always
    /// kept; when it can't be, the reason is said and nothing is planned. Framework thread; the job runs on a worker.
    /// </summary>
    private void StartCrossSexEndPlan(in RetargetContext ctx, RetargetInputs.Inputs inputs, BodySizeCatalog snapshot)
    {
        string key = Key(ctx);
        planDone = 0;
        planTotal = Math.Max(1, inputs.PrimaryTargets.Count);
        string worn = WornSlot(ctx);
        string limb = AutoRefitDecisions.LimbOf(worn)!;
        bool sourceHasLimb = SourceCatalog?.For(limb, race).Count > 0, targetHasLimb = snapshot.For(limb, race).Count > 0;
        string drawnRace = $"{ModelSkinReader.RaceOf(ctx.GamePath):D4}";
        string? cannot = RetargetInputs.CrossSexEndRefusal(endsUnreadable, sourceHasLimb, targetHasLimb, drawnRace);
        if (cannot != null || active is not { Explicit: true, MadeFor: { } madeFor } bake
            || !from.TryGetValue(limb, out var limbFrom))
        {
            planTask = Task.FromResult(new PlanResult(key, null, cannot ?? string.Format(
                Strings.Parts.RetargetRefuseCrossSexMadeForFmt, ModelRace.Describe(drawnRace))));
            return;
        }

        // Everything the worker reads, taken here: the garment AS DRAWN — the bake's source, never the baked model the
        // effective context now carries, which the job would bend a second time — the unbent made-for bodies, the
        // refit-onto mod, the wearer read from files, and the user's choices.
        byte[] drawn = bake.Source, baked = bake.Model;
        string gamePath = ctx.GamePath;
        ushort to = bake.Route.To;
        var onto = catalog!;
        var wearer = RaceInputs(ctx, to, BakeCutFrom(ctx));
        var targetRels = inputs.PrimaryTargets.Select(t => t.Rel).ToList();
        bool push = clearBody;
        bool piercings = RetargetInputs.CarryPiercings(RetargetInputs.EffectiveReplaceSkin(replaceSkin, true), carryPiercings);
        var held = ctx.Held.ToList();
        var uv = uvRemap;
        planTask = Task.Run(() =>
        {
            try
            {
                var job = CrossSexRefitJob.EndInputs(drawn, gamePath, to, madeFor, limbFrom, onto, targetRels, wearer, uv,
                                                     log, push, piercings, held, baked, out string? refusal);
                if (job == null) return new PlanResult(key, null, refusal ?? "");
                var made = CrossSexRefitJob.ForStudio(job, targetRels.Count, log);
                if (made.Planned == null) return new PlanResult(key, null, made.Error);
                var results = new List<(BodyOption, BodyRetarget.Planned)>();
                foreach (var (option, saved, preview) in made.Planned)
                {
                    if (preview != null) previews.AddOrUpdate(saved.Model, preview);
                    results.Add((option, saved));
                    Interlocked.Increment(ref planDone);
                }
                return new PlanResult(key, results, "", Quality: made.Quality, CrossSexFolds: made.Folds);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[Proteus] retarget: planning a shoe or glove for the other sex failed");
                return new PlanResult(key, null, ex.Message);
            }
        });
    }

    /// <summary>
    /// The fold line under the size shown: across the sexes, the cloth faces it turned over, said as holes in game;
    /// otherwise the refit's own count (<see cref="CrossSexRefit.FoldLineFor"/>).
    /// </summary>
    private void DrawFoldLine(BodyRetarget.Planned done, int showing)
    {
        var ps = Strings.Parts;
        int clothFolded = plannedCrossSexFolds is { } folds && showing < folds.Count ? folds[showing] : 0;
        switch (CrossSexRefit.FoldLineFor(plannedCrossSexFolds != null, clothFolded, done.Report.Folded))
        {
            case CrossSexRefit.FoldLine.CrossSex:
                using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                    ImGui.TextWrapped(string.Format(ps.RetargetRaceCrossSexFoldedFmt, clothFolded));
                break;
            case CrossSexRefit.FoldLine.Report:
                using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                    ImGui.TextWrapped(string.Format(ps.RetargetFoldedFmt, done.Report.Folded));
                break;
        }
    }
}
