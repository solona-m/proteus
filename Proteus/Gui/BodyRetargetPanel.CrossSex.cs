using System.Collections.Generic;
using System.Globalization;
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
        var ps = Strings.Parts;
        int folded = CrossSexRefit.ClothFolded(garment, plan);
        double coverage = landed?.VertexCoverage ?? 1.0;
        log.Information("[Proteus] retarget: across the sexes {0}: {1} cloth faces folded, {2:P1} of the body matched",
                        label, folded, coverage);
        string cover = coverage.ToString("P0", CultureInfo.CurrentCulture);
        if (!CrossSexRefit.CoverageAllowed(coverage))
        {
            string refused = string.Format(ps.RetargetRefuseCrossSexCoverageFmt, cover,
                                           CrossSexRefit.MinCoverage.ToString("P0", CultureInfo.CurrentCulture));
            return several ? $"{label}: {refused}" : refused;
        }
        string line = string.Format(ps.RetargetRaceCrossSexQualityFmt, folded, cover);
        quality.Add(several ? $"{label}: {line}" : line);
        folds.Add(folded);
        return null;
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
