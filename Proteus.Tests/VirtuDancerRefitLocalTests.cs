using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Virtu Dancer's Casaque and Bottoms, refitted from the game body as the automatic refit does it — reported thrown
/// metres off YAS Tre: the polar turn fit diverged on their flat ornament cards, and the scale fitted to the broken turn
/// was kept for the second pass (see <see cref="TurnFit.TurnTolerance"/>).
/// </summary>
public class VirtuDancerRefitLocalTests(ITestOutputHelper output)
{
    private const string Tre = LocalData.Mods + ":hs-[YAS] Tre-5.5.3-8xB";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";

    /// <param name="Unticked">The same refit with nothing kept whole: every piece bends with the body.</param>
    private sealed record Run(BodyRetarget.Planned Plan, byte[] Written, ModelParts Garment, List<string> Log,
                              Func<BodyRetarget.Planned> Unticked, string Target);

    private static byte[] Garment(Lumina.GameData data, string slot)
    {
        uint category = slot == "_top" ? 4u : 7u;
        foreach (var item in data.GetExcelSheet<Item>()!)
        {
            if (item.EquipSlotCategory.RowId != category
                || !item.Name.ExtractText().StartsWith("Virtu Dancer", StringComparison.Ordinal)) continue;
            int set = (int)(item.ModelMain & 0xFFFF);
            if (data.GetFile($"chara/equipment/e{set:D4}/model/c0201e{set:D4}{slot}.mdl") is { } file) return file.Data;
        }
        throw new InvalidOperationException($"no Virtu Dancer's {slot}");
    }

    private static IEnumerable<(BodyOption Option, Func<BodyRetarget.Tuning?, Run> Refit)> Refits(string body, string slot,
                                                                                                  string? only = null)
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        byte[] bytes = Garment(data, slot);
        var garment = ModelPartReader.Read(bytes)!;
        var vanilla = VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data);
        string source = vanilla.PathOf(vanilla.For(slot, "0201")[0]);
        var mod = BodySizeCatalog.Read(LocalData.Path(body));
        foreach (var option in mod.For(slot, "0201"))
        {
            if (only != null && RefitCore.Normal(option.Rel) != RefitCore.Normal(only)) continue;
            string target = mod.PathOf(option);
            Assert.Null(BodyRetarget.BuildPair(slot, source, target, slot, false, null, null, uv, out var pair));
            yield return (option, tuning =>
            {
                var lines = new List<string>();
                BodyRetarget.Planned Plan() => RefitCore.PlanRefit(garment, bytes, [pair], slot, across: true, gameGear: true,
                                                                   out _, log: lines.Add);
                var plan = tuning == null ? Plan() : BodyRetarget.WithTuning(tuning, Plan);
                // As PlanRefit plans it, but keeping no piece whole.
                var skin = AutoRefitDecisions.SkinFor(true);
                BodyRetarget.Planned Unticked() => BodyRetarget.Plan(
                    garment, bytes, [pair], slot, held: new HashSet<int>(), replaceSkin: true, acrossBodies: true,
                    clearBody: skin.ClearBody, cutHidden: skin.CutHidden, keepShape: null,
                    tuckSlot: RefitCore.TuckSlot(skin.TuckUnderCloth, slot),
                    deepClear: RefitCore.DeepClearFor(skin.DeepClear, slot),
                    keepStandoff: RefitCore.KeepStandoffFor(skin.KeepStandoff, slot),
                    seeInside: RefitCore.SeeInsideFor(skin.SeeInside, slot), relaxSinkGuard: skin.RelaxSinkGuard);
                return new Run(plan, RefitCore.MatchSkinToBody(plan.Model, source, target, uv, false, log), garment, lines,
                               Unticked, target);
            });
        }
    }

    private static float Extent(ModelParts m)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i + 2 < m.Positions.Length; i += 3)
        {
            var v = new Vector3(m.Positions[i], m.Positions[i + 1], m.Positions[i + 2]);
            Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z), $"position {i / 3} is no number");
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
        return (max - min).Length();
    }

    /// <summary>
    /// Cloth points a kept piece may leave inside the body beyond what bending it would (2). A piece under
    /// <see cref="BodyRetarget.ReleaseMinSize"/> stays whole however deep it sinks (bent point by point it comes out
    /// squashed flat on the skin); Virtu Dancer's 15.8 mm cards are such pieces. Measured (build 1365): every Tre option and
    /// Rue+'s small and medium chests no more than bent; Rue+'s four Large chests 4 points against 2.
    /// </summary>
    private const int InsideSlack = 2;

    public static TheoryData<string, string> Bodies => new()
    {
        { Tre, "_top" }, { Tre, "_dwn" }, { Rue, "_top" }, { Rue, "_dwn" },
    };

    [LocalDataTheory(LocalData.GameData, Tre, Rue)]
    [MemberData(nameof(Bodies))]
    [Trait("Plan", "AC-keep-shape-flat-card-1")]
    [Trait("Plan", "EDGE-keep-shape-flat-card-12")]
    public void Virtu_Dancer_stays_body_scale_on_every_option(string body, string slot)
    {
        var failures = new List<string>();
        int options = 0;
        foreach (var (option, refit) in Refits(body, slot))
        {
            options++;
            var run = refit(null);
            float before = Extent(run.Garment);
            float after = Extent(ModelPartReader.Read(run.Written)!);
            output.WriteLine($"{option.Rel}: worst move {run.Plan.Report.WorstMove * 1000:0.0} mm, extent {before:0.000} -> "
                             + $"{after:0.000} m, {run.Plan.Report.ShapeFallbacks} fallback turns");
            if (run.Plan.Report.WorstMove > 0.1f) failures.Add($"{option.Rel} moved {run.Plan.Report.WorstMove * 1000:0.0} mm");
            if (after > before + 0.05f) failures.Add($"{option.Rel} grew {before:0.000} -> {after:0.000} m");

            // And the kept pieces sit on the body as well as bending them would: no more cloth inside it, no more folds.
            var unticked = run.Unticked();
            int inside = BodySizeMatch.ClothAgainst(ModelPartReader.Read(run.Plan.Model)!, run.Target)!.Value.Inside;
            int insideBent = BodySizeMatch.ClothAgainst(ModelPartReader.Read(unticked.Model)!, run.Target)!.Value.Inside;
            output.WriteLine($"    inside the body {inside} points (bent {insideBent}); folded {run.Plan.Report.Folded} "
                             + $"(bent {unticked.Report.Folded})");
            if (inside > insideBent + InsideSlack) failures.Add($"{option.Rel} has {inside} points inside against {insideBent}");
            if (run.Plan.Report.Folded > unticked.Report.Folded)
                failures.Add($"{option.Rel} folded {run.Plan.Report.Folded} against {unticked.Report.Folded}");
        }
        Assert.True(options > 0, "no options for this slot");
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    public static TheoryData<string, string> Reported => new()
    {
        { "_dwn", "files/common/10/c0201e0000_dwn.mdl" },
        { "_top", "files/common/3/c0201e0000_top.mdl" },
    };

    [LocalDataTheory(LocalData.GameData, Tre)]
    [MemberData(nameof(Reported))]
    [Trait("Plan", "AC-keep-shape-flat-card-1")]
    [Trait("Plan", "AC-keep-shape-flat-card-4")]
    public void The_reported_cases_reproduce_without_the_fallback_and_log_it(string slot, string rel)
    {
        var (_, refit) = Assert.Single(Refits(Tre, slot, rel));

        var broken = refit(new BodyRetarget.Tuning(NoTurnFallback: true));
        Assert.True(broken.Plan.Report.WorstMove > 0.5f,
                    $"without the fallback the worst move is only {broken.Plan.Report.WorstMove * 1000:0.0} mm — the fixture "
                    + "no longer reproduces the report");

        var run = refit(null);
        Assert.True(run.Plan.Report.WorstMove <= 0.1f, $"moved {run.Plan.Report.WorstMove * 1000:0.0} mm");
        int n = run.Plan.Report.ShapeFallbacks;
        Assert.True(n >= 1);
        Assert.Contains(run.Log, l => l.StartsWith($"keep shape: {n} ", StringComparison.Ordinal));
    }
}
