using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Proteus.Services;

/// <summary>
/// The JSON report <c>Proteus.Refit --for-race</c> prints for a refit for the other sex (<see cref="CrossSexRefitJob"/>):
/// each size made or refused, the bones moved, and what to save with the models — the note that names the EQDP entry,
/// and the EST entry when the race needs the drawn race's extra skeleton. Built here, apart from the files the CLI
/// writes, so its shape is tested.
/// </summary>
internal static class CrossSexRefitReport
{
    /// <summary>Why a size is not written: its own refusal, or too little of the body matched. Never its folds — those
    /// are in the report beside it. Null when it is written.</summary>
    internal static string? Refusal(CrossSexRefitJob.Size size)
        => size.Refusal.Length > 0 ? size.Refusal
         : !size.SaveAllowed ? string.Format(Localization.Strings.Parts.RetargetRefuseCrossSexCoverageFmt,
                                             $"{size.Landed?.VertexCoverage:P0}", $"{CrossSexRefit.MinCoverage:P0}")
         : null;

    /// <summary>One size: its label, the body it was made for, its folds and coverage, and the file written or why not.</summary>
    internal static JsonObject Size(CrossSexRefitJob.Size size, string label, string? file)
    {
        var entry = new JsonObject
        {
            ["label"] = label, ["to"] = size.Target.Rel, ["folds"] = size.Folds,
            ["coverage"] = size.Landed?.VertexCoverage ?? 0f,
        };
        if (Refusal(size) is { } refusal) entry["refusal"] = refusal;
        else if (file != null) entry["file"] = file;
        return entry;
    }

    /// <summary>The whole report.</summary>
    /// <param name="withSkeleton">The wearer was read from a <c>--skeleton</c> file: the bones only it gives the race are
    /// listed.</param>
    internal static JsonObject Report(CrossSexRefitJob.Result result, string garmentPath, string gamePath, ushort drawn,
                                      ushort to, bool withSkeleton, JsonArray sizes)
    {
        string savePath = result.Route.Any ? result.Route.SavePath(gamePath) : gamePath;
        var movedBones = new JsonArray();
        foreach (var move in result.Moved ?? [])
            movedBones.Add(new JsonObject
            {
                ["from"] = move.From, ["to"] = move.To, ["how"] = move.How.ToString().ToLowerInvariant(),
            });
        var est = result.Est ?? EstPlan.NotNeeded;
        string note = $"save each model at {savePath} with an EQDP entry giving c{to:D4} a model of the set";
        if (est.Outcome == EstOutcome.Carry) note += $", and the EST entry {est.Entry} for c{to:D4}";
        var report = new JsonObject
        {
            ["garment"] = garmentPath, ["race"] = $"{drawn:D4}", ["forRace"] = $"{to:D4}", ["eqdpRace"] = $"{to:D4}",
            ["savePath"] = savePath, ["from"] = result.SourcePath,
            ["note"] = note,
            ["est"] = new JsonObject { ["outcome"] = est.Outcome.ToString(), ["entry"] = est.Entry },
            ["movedBones"] = movedBones,
            ["sizes"] = sizes,
        };
        if (withSkeleton)
            report["needsSkeleton"] = new JsonArray((result.NeedsSkeleton ?? []).Select(b => (JsonNode)b).ToArray());
        if (result.Refusal.Length > 0) report["refusal"] = result.Refusal;
        return report;
    }

    /// <summary>
    /// Whether to suggest <c>--from-skeleton</c>: a bone was placed by its part of the body or on the hip, not by the
    /// skeleton the garment was rigged to, which would place it exactly.
    /// </summary>
    internal static bool WantsFromSkeleton(IReadOnlyList<SkeletonRemap.Move>? moved)
        => (moved ?? []).Any(m => m.How is SkeletonRemap.How.StandIn or SkeletonRemap.How.Fallback);

    internal const string FromSkeletonHint = "pass --from-skeleton <the skeleton the garment was rigged to> for exact parents";
}
