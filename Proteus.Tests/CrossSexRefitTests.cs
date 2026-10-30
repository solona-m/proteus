using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Gui;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>Refit for the other sex: the decisions, on synthetic inputs (the real-data gates are CrossSexRefitLocalTests).</summary>
public class CrossSexRefitTests
{
    private const string MansTop = "chara/equipment/e6091/model/c0101e6091_top.mdl";
    private const string MansLegs = "chara/equipment/e6269/model/c0101e6269_dwn.mdl";
    private const string WomansTop = "chara/equipment/e6010/model/c0201e6010_top.mdl";
    private const string WomansLegs = "chara/equipment/e6107/model/c0201e6107_dwn.mdl";

    private static BodyOption Body(string race, string slot, string name)
        => new("Body", "", name, $"{name}/{race}{slot}.mdl", $"chara/equipment/e0000/model/c{race}e0000{slot}.mdl", slot);

    /// <summary>A body mod of both sexes: Midlander M and F chests and legs.</summary>
    private static BodySizeCatalog Both()
        => new("root", [Body("0101", "_top", "M"), Body("0101", "_dwn", "M legs"), Body("0101", "_glv", "M hands"),
                        Body("0201", "_top", "F"), Body("0201", "_dwn", "F legs"), Body("0201", "_glv", "F hands"),
                        Body("0301", "_top", "M Highlander")]);

    private static IReadOnlyList<OtherRaceRefit.Candidate> Offered(string path, BodySizeCatalog? target = null,
                                                                   bool vanilla = false)
        => OtherRaceRefit.Offered(path, target ?? Both(), [new PenumbraModMeta.Redirect(path, "x.mdl", "G / O")], null, vanilla);

    private static ModelParts Skin(string material)
        => ModelPartReader.Read(SyntheticModel.Build([], new SyntheticModel.Mesh(material, new SyntheticModel.Sub(0, TrianglesPerIsland: 3))))!;

    private static UVRemapService NoMaps() => new(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");

    internal static string Source(string file) => File.ReadAllText(SourcePath(file));

    private static string SourcePath(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, file);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(file);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-refit-2")]
    public void A_mans_midlander_top_is_offered_midlander_women_above() // [AC-cross-sex-refit-2]
    {
        var women = Assert.Single(Offered(MansTop), c => c.Race == 201);
        Assert.True(women.CrossSex);
        Assert.False(women.Below);
        var route = OtherRaceRefit.Resolve(RacialModelBake.Route.None, 201, 101, Offered(MansTop));
        Assert.Equal(new RacialModelBake.Route(101, 201, Down: false) { ByAlias = true }, route);
        Assert.True(route.CrossesSex);
        Assert.Equal("chara/equipment/e6091/model/c0201e6091_top.mdl", route.SavePath(MansTop));
        Assert.NotNull(route.Eqdp(route.SavePath(MansTop)));
        Assert.Contains("Female", route.Eqdp(route.SavePath(MansTop))!.ToString());
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-refit-1")]
    public void A_womans_midlander_legs_garment_is_offered_midlander_men_below() // [AC-cross-sex-refit-1]
    {
        var men = Assert.Single(Offered(WomansLegs), c => c.Race == 101);
        Assert.True(men.CrossSex);
        Assert.True(men.Below);
        var route = OtherRaceRefit.Resolve(RacialModelBake.Route.None, 101, 201, Offered(WomansLegs));
        Assert.Equal(new RacialModelBake.Route(201, 101, Down: false, Lowered: true) { ByAlias = true }, route);
        Assert.Equal("chara/equipment/e6107/model/c0101e6107_dwn.mdl", route.SavePath(WomansLegs));
        Assert.Contains("= Male", route.Eqdp(route.SavePath(WomansLegs))!.ToString());
        // Men's legs go the other way too.
        Assert.Contains(Offered(MansLegs), c => c.Race == 201 && c.CrossSex);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-refit-1")]
    [Trait("Plan", "AC-cross-sex-refit-2")]
    public void Across_the_sexes_every_race_falling_through_wears_it() // [AC-cross-sex-refit-1] [AC-cross-sex-refit-2]
    {
        var down = new RacialModelBake.Route(201, 101, false, Lowered: true) { ByAlias = true };
        var up = new RacialModelBake.Route(101, 201, false) { ByAlias = true };
        Assert.Equal(OtherRaceRefit.WearNote.PickedAndFallThrough, OtherRaceRefit.Wears(down));
        Assert.Equal(OtherRaceRefit.WearNote.PickedAndFallThrough, OtherRaceRefit.Wears(up));
        Assert.Equal(OtherRaceRefit.WearNote.OnlyPicked, OtherRaceRefit.Wears(new RacialModelBake.Route(101, 301, false)));
        // The automatic "as worn" route of a men's garment on a woman is not an explicit pick, and says nothing new.
        Assert.Equal(OtherRaceRefit.WearNote.OnlyPicked, OtherRaceRefit.Wears(new RacialModelBake.Route(101, 201, false)));
        Assert.True(OtherRaceRefit.WarnsFallThroughBones(up, new EstPlan(EstOutcome.Carry)));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-refit-1")]
    public void A_c0101_save_also_reaches_lalafell_women() // [AC-cross-sex-refit-1]
    {
        // Made from a c0201 model, which the item keeps: women falling through to c0201 stop there and keep it.
        var men = CrossSexRefit.FallThroughTo(101, drawn: 201);
        Assert.Contains((ushort)1201, men);   // Lalafell F → Lalafell M → Midlander M
        Assert.Contains((ushort)301, men);
        Assert.DoesNotContain((ushort)201, men);
        Assert.DoesNotContain((ushort)401, men);   // Highlander F → Midlander F, the model refitted
        Assert.DoesNotContain((ushort)101, men);
        // Made from a c0101 model: the women's races reach the c0201 save; men and Lalafell stop at c0101.
        var women = CrossSexRefit.FallThroughTo(201, drawn: 101);
        Assert.Contains((ushort)401, women);
        Assert.DoesNotContain((ushort)1201, women);
        Assert.DoesNotContain((ushort)301, women);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-refit-7")]
    [Trait("Plan", "EDGE-cross-sex-refit-9")]
    public void Automatic_cross_sex_route_is_unchanged() // [AC-cross-sex-refit-7] [EDGE-cross-sex-refit-9]
    {
        // A woman playing in a men's shirt, through the automatic refit's own route: baked up as before, never by alias.
        var auto = RefitCore.Route(MansTop, 201, r => r == 201, _ => true, [], null);
        Assert.Equal(new RacialModelBake.Route(101, 201, Down: false), auto);
        Assert.False(auto.ByAlias);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-refit-2")]
    public void A_passing_cross_sex_plan_is_taken_with_its_quality_said() // [AC-cross-sex-refit-2]
    {
        // The quality line rides beside the plan: a notice would drop the plan, and nothing could be saved.
        var planned = new List<(BodyOption, BodyRetarget.Planned)> { (Body("0201", "_top", "Nude - Small"), null!) };
        Assert.True(BodyRetargetPanel.Takes(new BodyRetargetPanel.PlanResult("k", planned, "", Quality: "8 folded faces")));
        Assert.False(BodyRetargetPanel.Takes(new BodyRetargetPanel.PlanResult("k", planned, "", Notice: "nothing to do")));
        Assert.False(BodyRetargetPanel.Takes(new BodyRetargetPanel.PlanResult("k", planned, "refused")));
        Assert.False(BodyRetargetPanel.Takes(new BodyRetargetPanel.PlanResult("k", [], "")));
        Assert.Contains("Quality: string.Join(", Source("Proteus/Gui/BodyRetargetPanel.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-refit-3")]
    [Trait("Plan", "AC-cross-sex-tops-yas-3")]
    public void Underbust_lift_follows_the_measurement() // [AC-cross-sex-refit-3] [AC-cross-sex-tops-yas-3]
    {
        // Measured (CrossSexRefitLocalTests.Underbust_lift_for_a_womans_top_is_measured): off across the sexes both ways.
        Assert.False(CrossSexRefit.LiftsUnderbust(101, 201));
        Assert.False(CrossSexRefit.LiftsUnderbust(201, 101));
        Assert.True(CrossSexRefit.LiftsUnderbust(101, 301));
        Assert.True(CrossSexRefit.LiftsUnderbust(201, 201));
        Assert.Contains("liftUnderbust: i.LiftUnderbust ?? CrossSexRefit.LiftsUnderbust(drawn, i.To)", Source("Proteus/Services/CrossSexRefitJob.cs"));
        Assert.Contains("CrossSexRefit.LiftsUnderbust(bake!.Route.Drawn, bake.Route.To)", Source("Proteus/Gui/BodyRetargetPanel.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-refit-1")]
    public void The_cross_sex_entry_has_its_own_kind_and_label() // [AC-cross-sex-refit-1]
    {
        var offered = Offered(WomansLegs);
        var entries = OtherRaceRefit.PickerEntries(RacialModelBake.Route.None, 201, offered);
        var other = Assert.Single(entries, e => e.Race == 101);
        Assert.Equal(OtherRaceRefit.EntryKind.OtherSex, other.Kind);
        Assert.Equal(other, OtherRaceRefit.Current(entries, 101, RacialModelBake.Route.None, 201));
        Assert.Contains("OtherRaceRefit.EntryKind.OtherSex => string.Format(Strings.Parts.RetargetRaceOtherSexFmt",
                        Source("Proteus/Gui/BodyRetargetPanel.Race.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-refit-7")]
    [Trait("Plan", "EDGE-cross-sex-refit-9")]
    public void As_worn_stays_beside_the_explicit_cross_sex_entry() // [AC-cross-sex-refit-7] [EDGE-cross-sex-refit-9]
    {
        var automatic = new RacialModelBake.Route(101, 201, Down: false);   // a woman playing in a men's shirt
        var offered = Offered(MansTop);
        var entries = OtherRaceRefit.PickerEntries(automatic, 101, offered);
        Assert.Equal(OtherRaceRefit.EntryKind.AsWorn, entries[0].Kind);
        Assert.Contains(entries, e => e.Race == 201 && e.Kind == OtherRaceRefit.EntryKind.OtherSex);
        Assert.Equal(OtherRaceRefit.EntryKind.AsWorn, OtherRaceRefit.Current(entries, 0, automatic, 101).Kind);
        Assert.Equal(OtherRaceRefit.EntryKind.OtherSex, OtherRaceRefit.Current(entries, 201, automatic, 101).Kind);
        // Unpicked, the automatic route is the one in force, bending as before (no alias).
        Assert.False(OtherRaceRefit.Resolve(automatic, 0, 101, offered).ByAlias);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-refit-7")]
    [Trait("Plan", "EDGE-cross-sex-refit-5")]
    [Trait("Plan", "EDGE-cross-sex-refit-6")]
    [Trait("Plan", "EDGE-cross-sex-refit-7")]
    public void Only_the_shipped_cells_cross() // [AC-cross-sex-refit-7] [EDGE-cross-sex-refit-5] [EDGE-cross-sex-refit-6] [EDGE-cross-sex-refit-7]
    {
        // A women's top crosses now (cross-sex-tops-yas): its folds are said, never refused over.
        Assert.Contains(Offered(WomansTop), c => c.CrossSex && c.Race == 101);
        Assert.DoesNotContain(Offered("chara/equipment/e6091/model/c0301e6091_top.mdl"), c => c.CrossSex);
        Assert.DoesNotContain(Offered("chara/equipment/e6091/model/c1201e6091_top.mdl"), c => c.CrossSex);
        Assert.DoesNotContain(Offered("chara/equipment/e6091/model/c0101e6091_glv.mdl"), c => c.CrossSex);
        Assert.DoesNotContain(Offered("chara/equipment/e6091/model/c0201e6091_sho.mdl"), c => c.CrossSex);
        Assert.Empty(Offered(MansTop, vanilla: true));
        Assert.True(CrossSexRefit.Supported(101, 201, "_top"));
        Assert.True(CrossSexRefit.Supported(101, 201, "_dwn"));
        Assert.True(CrossSexRefit.Supported(201, 101, "_dwn"));
        Assert.True(CrossSexRefit.Supported(201, 101, "_top"));
        Assert.False(CrossSexRefit.Supported(101, 301, "_top"));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-2")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-17")]
    public void A_womans_midlander_top_is_offered_midlander_men() // [AC-cross-sex-tops-yas-2] [EDGE-cross-sex-tops-yas-17]
    {
        var men = Assert.Single(Offered(WomansTop), c => c.Race == 101);
        Assert.True(men.CrossSex);
        Assert.True(men.Below);
        Assert.Null(men.OwnModelPath);
        Assert.True(CrossSexRefit.Supported(201, 101, "_top"));
        string[] ok = ["game", "uvmaps", "from-root"];
        Assert.Null(CrossSexRefit.ValidateCli("0201", "0101", "_top", ok));
        Assert.Equal("--for-race refits chest (_top) and legs (_dwn) garments only",
                     CrossSexRefit.ValidateCli("0201", "0101", "_glv", ok));
        Assert.Equal("--for-race refits chest (_top) and legs (_dwn) garments only",
                     CrossSexRefit.ValidateCli("0101", "0201", "_sho", ok));
        // --from-skeleton must name a file there is (the review's decision): checked with the other usage errors.
        Assert.Equal("--from-skeleton names no file: \"yas.sklb\"",
                     CrossSexRefit.ValidateCli("0201", "0101", "_top", [.. ok, "from-skeleton"], "yas.sklb", _ => false));
        Assert.Null(CrossSexRefit.ValidateCli("0201", "0101", "_top", [.. ok, "from-skeleton"], "yas.sklb", _ => true));
        Assert.Contains("opts.GetValueOrDefault(\"from-skeleton\"))", Source("Proteus.Refit/CrossSexCommand.cs"));
        var route = OtherRaceRefit.Resolve(RacialModelBake.Route.None, 101, 201, Offered(WomansTop));
        Assert.True(route.ByAlias && route.Lowered);
        Assert.Equal("chara/equipment/e6010/model/c0101e6010_top.mdl", route.SavePath(WomansTop));
    }

    /// <summary>
    /// The rule this test was written for (EDGE-cross-sex-tops-yas-16, the author's men's top pointed at, nothing made)
    /// was changed on purpose by own-model-overwrite: it is said, and the men's version is made over it.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-2")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-16")]
    [Trait("Plan", "AC-own-model-overwrite-1")]
    public void The_authors_own_mens_top_is_warned_and_refitted_over() // [AC-own-model-overwrite-1]
    {
        const string mens = "chara/equipment/e6010/model/c0101e6010_top.mdl";
        var offered = OtherRaceRefit.Offered(WomansTop, Both(),
            [new PenumbraModMeta.Redirect(WomansTop, "x.mdl", "G / O"), new PenumbraModMeta.Redirect(mens, "m.mdl", "G / O")],
            null, false);
        var men = Assert.Single(offered, c => c.Race == 101);
        Assert.Equal(mens, men.OwnModelPath);
        Assert.True(OtherRaceRefit.InForce(101, 201, offered));
        var route = OtherRaceRefit.Resolve(RacialModelBake.Route.None, 101, 201, offered);
        Assert.True(route.ByAlias && route.Lowered);
        Assert.Equal(mens, route.SavePath(WomansTop));
    }

    [Fact]
    [Trait("Plan", "AC-own-model-overwrite-5")]
    public void The_save_row_warns_when_an_author_group_is_outranked() // [AC-own-model-overwrite-5]
    {
        Assert.Contains("BodyRetargetWriter.Outranking(", Source("Proteus/Gui/BodyRetargetPanel.cs"));
        var ps = Proteus.Localization.Strings.Parts;
        Assert.Contains("\"Body\"", string.Format(ps.RetargetClashOutrankedFmt, "Body", "Size"));
        Assert.Contains("\"Size\"", string.Format(ps.RetargetClashOutrankedFmt, "Body", "Size"));
        Assert.Contains("\"Undershirt\"", string.Format(ps.RetargetClashOptionOutrankedFmt, "Undershirt", "Items"));
        Assert.Contains("\"Items\"", string.Format(ps.RetargetClashOptionOutrankedFmt, "Undershirt", "Items"));
    }

    [Fact]
    [Trait("Plan", "AC-own-model-overwrite-8")]
    public void OwnModels_stops_the_chain_at_a_mod_redirect_ours_included() // [AC-own-model-overwrite-8]
    {
        // A refit saved earlier counts the same as the author's: either is what that race draws.
        var redirects = new[] { new PenumbraModMeta.Redirect("chara/equipment/e6010/model/c0301e6010_top.mdl",
                                                             "Body Retarget/h.mdl", "Body — x / Large") };
        var has = CrossSexRefit.OwnModels(WomansTop, redirects, null);
        Assert.True(has(301));
        Assert.False(has(901));
        Assert.True(CrossSexRefit.OwnModels(WomansTop, null, p => p.Contains("c0901"))(901));
        Assert.False(CrossSexRefit.OwnModels(WomansTop, null, null)(901));
    }

    [Fact]
    [Trait("Plan", "EDGE-own-model-overwrite-17")]
    public void Wearers_leave_out_races_with_their_own_model() // [EDGE-own-model-overwrite-17]
    {
        string At(int race) => $"chara/equipment/e6010/model/c{race:D4}e6010_top.mdl";
        var redirects = new[] { 301, 901, 1101 }.Select(r => new PenumbraModMeta.Redirect(At(r), $"m/{r}.mdl", "Body / All"));
        var worn = CrossSexRefit.FallThroughTo(101, 201, CrossSexRefit.OwnModels(WomansTop, redirects, null));
        foreach (ushort race in new ushort[] { 301, 901, 1101, 1201, 1501 })
            Assert.DoesNotContain(race, worn);
        Assert.NotEmpty(worn);

        // The game's gear (no redirects) gets what it always got.
        Func<string, bool> game = p => p.Contains("c0301") || p.Contains("c1101");
        Assert.Equal(CrossSexRefit.FallThroughTo(101, 201, r => game(RacialModelBake.WithRace(WomansTop, r))),
                     CrossSexRefit.FallThroughTo(101, 201, CrossSexRefit.OwnModels(WomansTop, null, game)));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-1")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-1")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-2")]
    public void Only_coverage_stops_a_cross_sex_save() // [AC-cross-sex-tops-yas-1] [EDGE-cross-sex-tops-yas-1] [EDGE-cross-sex-tops-yas-2]
    {
        Assert.True(CrossSexRefit.CoverageAllowed(0.9));
        Assert.False(CrossSexRefit.CoverageAllowed(0.899));
        var plan = (BodyRetarget.Planned)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BodyRetarget.Planned));
        UvAtlasCorrespondence.Stats Landed(float coverage) => default(UvAtlasCorrespondence.Stats) with { VertexCoverage = coverage };
        Assert.True(new CrossSexRefitJob.Size(Body("0101", "_top", "M"), "t", plan, plan, null, 500, Landed(0.95f), "").SaveAllowed);
        Assert.False(new CrossSexRefitJob.Size(Body("0101", "_top", "M"), "t", plan, plan, null, 0, Landed(0.85f), "").SaveAllowed);
        Assert.False(new CrossSexRefitJob.Size(Body("0101", "_top", "M"), "t", plan, null, null, 0, Landed(0.95f), "").SaveAllowed);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-1")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-2")]
    public void Folds_after_a_cross_sex_refit_are_said_as_holes() // [AC-cross-sex-tops-yas-1] [EDGE-cross-sex-tops-yas-2]
    {
        Assert.Equal(CrossSexRefit.FoldLine.CrossSex, CrossSexRefit.FoldLineFor(true, 30, 12));
        Assert.Equal(CrossSexRefit.FoldLine.Report, CrossSexRefit.FoldLineFor(true, 0, 5));
        Assert.Equal(CrossSexRefit.FoldLine.Report, CrossSexRefit.FoldLineFor(false, 30, 5));
        Assert.Equal(CrossSexRefit.FoldLine.None, CrossSexRefit.FoldLineFor(true, 0, 0));
        Assert.Equal(CrossSexRefit.FoldLine.None, CrossSexRefit.FoldLineFor(false, 30, 0));
        string panel = Source("Proteus/Gui/BodyRetargetPanel.CrossSex.cs");
        Assert.Contains("switch (CrossSexRefit.FoldLineFor(plannedCrossSexFolds != null, clothFolded, done.Report.Folded))", panel);
        int at = panel.IndexOf("case CrossSexRefit.FoldLine.CrossSex:", StringComparison.Ordinal);
        Assert.True(at > 0);
        string arm = panel.Substring(at, 300);
        Assert.Contains("ProteusStyle.Warn", arm);
        Assert.Contains("ps.RetargetRaceCrossSexFoldedFmt", arm);
        Assert.Contains("hole", Localization.Strings.Parts.RetargetRaceCrossSexFoldedFmt);
    }

    /// <summary>
    /// The CLI's report (CrossSexRefitReport): folds and coverage on every size, a refusal only under the coverage floor,
    /// the bones moved, the EST entry named on Carry, the custom skeleton's bones only with --skeleton.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-1")]
    [Trait("Plan", "AC-cross-sex-tops-yas-6")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-17")]
    public void The_cli_report_says_what_to_save() // [AC-cross-sex-tops-yas-1] [AC-cross-sex-tops-yas-6] [EDGE-cross-sex-tops-yas-17]
    {
        var plan = (BodyRetarget.Planned)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BodyRetarget.Planned));
        UvAtlasCorrespondence.Stats Landed(float c) => default(UvAtlasCorrespondence.Stats) with { VertexCoverage = c };
        var kept = new CrossSexRefitJob.Size(Body("0101", "_top", "M"), "t", plan, plan, null, 115, Landed(0.99f), "");
        var thin = new CrossSexRefitJob.Size(Body("0101", "_top", "M2"), "t2", plan, plan, null, 0, Landed(0.8f), "");

        var saved = CrossSexRefitReport.Size(kept, "M", "out/M.mdl");
        Assert.Equal(115, (int)saved["folds"]!);   // folds said, never a refusal
        Assert.Equal("out/M.mdl", (string)saved["file"]!);
        Assert.Null(saved["refusal"]);
        Assert.Null(CrossSexRefitReport.Refusal(kept));
        var refused = CrossSexRefitReport.Size(thin, "M2", null);
        Assert.Contains("matched only", (string)refused["refusal"]!);
        Assert.Null(refused["file"]);
        // A size's own refusal comes first, and nothing is written for it.
        var own = new CrossSexRefitJob.Size(Body("0101", "_top", "M3"), "t3", null, null, null, 0, Landed(0.5f), "pair refused");
        Assert.Equal("pair refused", CrossSexRefitReport.Refusal(own));
        var ownEntry = CrossSexRefitReport.Size(own, "M3", "out/M3.mdl");
        Assert.Equal("pair refused", (string)ownEntry["refusal"]!);
        Assert.Null(ownEntry["file"]);

        var route = new RacialModelBake.Route(201, 101, Down: false, Lowered: true) { ByAlias = true };
        var moves = new List<SkeletonRemap.Move>
        {
            new("ya_shiri_phys_l", "iv_shiri_l", SkeletonRemap.How.Counterpart),
            new("foo_cape", "j_kosi", SkeletonRemap.How.Fallback),
        };
        var result = new CrossSexRefitJob.Result("", route, null, null, null, "made-for.mdl", [kept], moves,
                                                 new EstPlan(EstOutcome.Carry, 6085), ["iv_shiri_l"]);
        const string gamePath = "chara/equipment/e6010/model/c0201e6010_top.mdl";
        var report = CrossSexRefitReport.Report(result, "garment.mdl", gamePath, 201, 101, withSkeleton: true, []);
        Assert.Equal("chara/equipment/e6010/model/c0101e6010_top.mdl", (string)report["savePath"]!);
        Assert.EndsWith(", and the EST entry 6085 for c0101", (string)report["note"]!);
        Assert.Equal("Carry", (string)report["est"]!["outcome"]!);
        Assert.Equal(6085, (int)report["est"]!["entry"]!);
        var movedBones = report["movedBones"]!.AsArray();
        Assert.Equal(2, movedBones.Count);
        Assert.Equal(("ya_shiri_phys_l", "iv_shiri_l", "counterpart"),
                     ((string)movedBones[0]!["from"]!, (string)movedBones[0]!["to"]!, (string)movedBones[0]!["how"]!));
        Assert.Equal("fallback", (string)movedBones[1]!["how"]!);
        Assert.Equal("iv_shiri_l", (string)report["needsSkeleton"]!.AsArray().Single()!);
        Assert.Null(report["refusal"]);
        Assert.True(CrossSexRefitReport.WantsFromSkeleton(moves));

        // No --skeleton, nothing carried, refused early: no needsSkeleton, NotNeeded, the refusal said.
        var early = CrossSexRefitReport.Report(new CrossSexRefitJob.Result("nope", route, null, null, null, null, []),
                                               "garment.mdl", gamePath, 201, 101, withSkeleton: false, []);
        Assert.Null(early["needsSkeleton"]);
        Assert.Equal("NotNeeded", (string)early["est"]!["outcome"]!);
        Assert.DoesNotContain("EST entry", (string)early["note"]!);
        Assert.Equal("nope", (string)early["refusal"]!);
        Assert.Empty(early["movedBones"]!.AsArray());
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-1")]
    [Trait("Plan", "AC-cross-sex-tops-yas-7")]
    [Trait("Plan", "AC-cross-sex-tops-yas-8")]
    public void Nothing_refuses_on_folds() // [AC-cross-sex-tops-yas-1] [AC-cross-sex-tops-yas-7] [AC-cross-sex-tops-yas-8]
    {
        string root = Path.GetDirectoryName(Path.GetDirectoryName(SourcePath("Proteus/Services/CrossSexRefit.cs")))!;
        root = Path.GetDirectoryName(root)!;
        var files = new[] { "Proteus/Services", "Proteus/Gui", "Proteus.Refit" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories));
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (string banned in new[] { "MaxFolds", "RetargetRefuseCrossSexQualityFmt", "SaveAllowed(folds",
                                              "HairPort.Refused.Folded", "if (ours) fits = false" })
                Assert.False(text.Contains(banned, StringComparison.Ordinal), $"{Path.GetFileName(file)} still has {banned}");
        }
        Assert.Contains("CrossSexRefit.CoverageAllowed(coverage)", Source("Proteus/Gui/BodyRetargetPanel.CrossSex.cs"));
        Assert.Contains("RetargetRaceCrossSexQualityFmt", Source("Proteus/Gui/BodyRetargetPanel.CrossSex.cs"));
        Assert.Contains("[\"folds\"] = size.Folds", Source("Proteus/Services/CrossSexRefitReport.cs"));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-refit-8")]
    [Trait("Plan", "EDGE-cross-sex-refit-13")]
    public void The_made_for_line_and_direction_cross_only_between_midlanders() // [EDGE-cross-sex-refit-8] [EDGE-cross-sex-refit-13]
    {
        // Across the sexes the made-for side is the drawn race's alone — never the picked race's own bodies.
        Assert.Equal([(ushort)201], CrossSexRefit.MadeForLine(201, 101));
        Assert.Equal([(ushort)101], CrossSexRefit.MadeForLine(101, 201));
        Assert.Equal([(ushort)301, 101], CrossSexRefit.MadeForLine(301, 101));
        Assert.True(CrossSexRefit.BendsDown(201, 101));
        Assert.False(CrossSexRefit.BendsDown(101, 201));
        Assert.True(CrossSexRefit.BendsDown(301, 101));
        Assert.True(CrossSexRefit.BendsDown(1401, 201));
        Assert.False(CrossSexRefit.BendsDown(201, 201));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-refit-16")]
    public void A_made_for_mod_without_the_drawn_race_is_refused_not_matched_to_itself() // [EDGE-cross-sex-refit-16]
    {
        // TBSE alone as "made for" a women's skirt: none of its bodies are c0201, so nothing is offered to bend.
        var tbse = new BodySizeCatalog("root", [Body("0101", "_dwn", "SFW")]);
        Assert.Empty(tbse.For("_dwn").Where(o => CrossSexRefit.MadeForLine(201, 101).Any(r => BodySizeCatalog.RaceOf(o.GamePath) == $"{r:D4}")));
        Assert.Contains("RetargetRefuseCrossSexMadeForFmt", Source("Proteus/Gui/BodyRetargetPanel.Race.cs"));
        Assert.Contains("RetargetRefuseCrossSexMadeForFmt", Source("Proteus/Services/CrossSexRefitJob.cs"));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-refit-14")]
    public void Only_an_explicit_cross_sex_pick_bends_by_alias() // [EDGE-cross-sex-refit-14]
    {
        Assert.True(OtherRaceRefit.Resolve(RacialModelBake.Route.None, 101, 201, Offered(WomansLegs)).ByAlias);
        Assert.False(OtherRaceRefit.Resolve(RacialModelBake.Route.None, 301, 101, Offered(MansTop)).ByAlias);
        Assert.False(new RacialModelBake.Route(101, 201, false).ByAlias);   // RefitCore's automatic route
        // An up route's preview is never bent back by alias (the game bends it forward by the bones as named).
        string bake = Source("Proteus/Services/RacialModelBake.cs");
        Assert.Contains(": Unbake(refitted, Drawn, To, who.Pbd, who.ParentOf, out refusal);", bake);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-refit-5")]
    public void Pair_sexes_follow_the_route() // [AC-cross-sex-refit-5]
    {
        var down = new RacialModelBake.Route(201, 101, false, Lowered: true) { ByAlias = true };
        var up = new RacialModelBake.Route(101, 201, false) { ByAlias = true };
        Assert.Equal((false, true), CrossSexRefit.Sexes(down, explicitPick: true, garmentMale: true));
        Assert.Equal((true, false), CrossSexRefit.Sexes(up, explicitPick: true, garmentMale: false));
        Assert.Equal((false, false), CrossSexRefit.Sexes(new RacialModelBake.Route(101, 201, false), false, false));
        Assert.Equal((true, true), CrossSexRefit.Sexes(new RacialModelBake.Route(101, 301, false), true, true));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-refit-6")]
    [Trait("Plan", "EDGE-cross-sex-refit-3")]
    public void A_cross_sex_pair_without_maps_is_refused_saying_so() // [AC-cross-sex-refit-6] [EDGE-cross-sex-refit-3]
    {
        var bibo = Skin("/mt_c0201b0001_bibo.mtrl");
        var tbse = Skin("/mt_c0101b0001_b.mtrl");
        var uv = new float[bibo.Positions.Length / 3 * 2];
        Assert.False(BodyCorrespondence.TryBuild(bibo, uv, tbse, new float[tbse.Positions.Length / 3 * 2], "_top", out _,
                                                 out string refusal, NoMaps(), male: false, targetMale: true));
        Assert.Contains("body maps", refusal);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-refit-6")]
    [Trait("Plan", "EDGE-cross-sex-refit-2")]
    public void A_gen3_womans_body_is_refused_across_sexes() // [AC-cross-sex-refit-6] [EDGE-cross-sex-refit-2]
    {
        var gen3 = Skin("/mt_c0201b0001_b.mtrl");
        var tbse = Skin("/mt_c0101b0001_b.mtrl");
        Assert.False(BodyCorrespondence.TryBuild(gen3, new float[gen3.Positions.Length / 3 * 2], tbse,
                                                 new float[tbse.Positions.Length / 3 * 2], "_top", out _, out string refusal,
                                                 NoMaps(), male: false, targetMale: true));
        Assert.Contains("gen3", refusal);
        Assert.Contains("tbse", refusal);
        Assert.True(CrossSexRefit.LayoutsPair("bibo", "tbse"));
        Assert.True(CrossSexRefit.LayoutsPair("gen2", UVRemapService.MaleVanillaSpace));
        Assert.False(CrossSexRefit.LayoutsPair("gen3", "tbse"));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-refit-18")]
    public void Target_sex_left_out_is_the_old_behaviour() // [EDGE-cross-sex-refit-18]
    {
        var a = Skin("/mt_c0101b0001_b.mtrl");
        var b = Skin("/mt_c0101b0001_a.mtrl");
        var uvA = new float[a.Positions.Length / 3 * 2];
        var uvB = new float[b.Positions.Length / 3 * 2];
        bool before = BodyCorrespondence.TryBuild(a, uvA, b, uvB, "_top", out _, out string r1, NoMaps(), male: true);
        bool after = BodyCorrespondence.TryBuild(a, uvA, b, uvB, "_top", out _, out string r2, NoMaps(), male: true, targetMale: true);
        Assert.Equal(before, after);
        Assert.Equal(r1, r2);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-refit-8")]
    [Trait("Plan", "EDGE-cross-sex-refit-19")]
    public void Cli_for_race_is_validated() // [AC-cross-sex-refit-8] [EDGE-cross-sex-refit-19]
    {
        string[] ok = ["game", "uvmaps", "from-root"];
        Assert.Null(CrossSexRefit.ValidateCli("0101", "0201", "_top", ok));
        Assert.Null(CrossSexRefit.ValidateCli("0201", "0101", "_dwn", ok));
        Assert.StartsWith("--for-race crosses the sexes", CrossSexRefit.ValidateCli("0101", "0301", "_top", ok));
        Assert.StartsWith("--for-race crosses the sexes", CrossSexRefit.ValidateCli("0101", "0101", "_top", ok));
        Assert.Null(CrossSexRefit.ValidateCli("0201", "0101", "_top", ok));   // cross-sex-tops-yas
        Assert.StartsWith("--for-race refits", CrossSexRefit.ValidateCli("0101", "0201", "_glv", ok));
        Assert.StartsWith("--for-race needs", CrossSexRefit.ValidateCli("0101", "0201", "_top", ["game", "uvmaps"]));
        Assert.StartsWith("--for-race is not combined", CrossSexRefit.ValidateCli("0101", "0201", "_top", [.. ok, "finish"]));
        Assert.StartsWith("--for-race is not combined", CrossSexRefit.ValidateCli("0101", "0201", "_top", [.. ok, "whole"]));
        Assert.StartsWith("--for-race is not combined", CrossSexRefit.ValidateCli("0101", "0201", "_top", [.. ok, "keep-default"]));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-refit-11")]
    public void A_pick_no_longer_offered_falls_back_to_the_automatic_route() // [EDGE-cross-sex-refit-11]
    {
        var automatic = new RacialModelBake.Route(101, 201, false);
        var menOnly = new BodySizeCatalog("root", [Body("0101", "_top", "M")]);
        Assert.Equal(automatic, OtherRaceRefit.Resolve(automatic, 201, 101, Offered(MansTop, menOnly)));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-refit-12")]
    public void A_cross_sex_option_is_named_for_its_race() // [EDGE-cross-sex-refit-12]
    {
        Assert.Contains("Midlander", OtherRaceRefit.OptionName("Nude - Small", 201));
        Assert.Equal("TBSE M Midlander", OtherRaceRefit.OptionName("TBSE M Midlander", 101));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-refit-10")]
    public void The_preview_note_says_whose_body_it_is_bent_onto() // [EDGE-cross-sex-refit-10]
    {
        Assert.Equal(OtherRaceRefit.PreviewNote.OtherRace, OtherRaceRefit.NoteFor(true, 201, 101));
        Assert.Equal(OtherRaceRefit.PreviewNote.NotDrawn, OtherRaceRefit.NoteFor(false, 101, 201));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-refit-17")]
    public void A_body_mod_of_both_sexes_refits_across_itself() // [EDGE-cross-sex-refit-17]
    {
        var both = Both();
        Assert.Contains(Offered(MansTop, both), c => c.Race == 201 && c.OwnModelPath == null);
        Assert.Same(both, OtherRaceRefit.MadeFor(null, null, both, "0101", _ => throw new InvalidOperationException()));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-refit-15")]
    public void Shared_pairs_are_built_with_the_sides_sexes() // [EDGE-cross-sex-refit-15]
    {
        string panel = Source("Proteus/Gui/BodyRetargetPanel.cs");
        Assert.Contains("if (Build(sourcePath, targetPath, name, male, masks, to, out var pair, out _, targetMale, report: true)", panel);
        Assert.Contains("return Build(sourcePath, targetPath, name, male, masks, target, out _, out _, targetMale) ?? \"\";", panel);
        Assert.Contains("plan = RefitCore.MatchSkinToBody(plan, garmentSource, targetPath, uvRemap, male, log, targetMale);", panel);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-refit-6")]
    [Trait("Plan", "AC-cross-sex-tops-yas-1")]
    public void A_refit_matching_too_little_of_the_body_is_never_saved() // [AC-cross-sex-refit-6] [AC-cross-sex-tops-yas-1]
    {
        // Once folds and coverage; folds are never a reason any more (cross-sex-tops-yas), only coverage.
        Assert.True(CrossSexRefit.CoverageAllowed(0.989));
        Assert.False(CrossSexRefit.CoverageAllowed(CrossSexRefit.MinCoverage - 0.01));
        Assert.Contains("CrossSexRefit.CoverageAllowed(coverage)", Source("Proteus/Gui/BodyRetargetPanel.CrossSex.cs"));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-refit-16")]
    public void No_bodies_message_no_longer_says_a_refit_cant_change_sex() // [EDGE-cross-sex-refit-16]
    {
        string en = Source("Proteus/Localization/en.json");
        Assert.DoesNotContain("can't change sex", en);
        Assert.Contains("\"Parts.Retarget.Race.CrossSexHint.Fmt\"", en);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-area-coverage-2")]
    [Trait("Plan", "AC-cross-sex-area-coverage-4")]
    [Trait("Plan", "AC-cross-sex-area-coverage-5")]
    [Trait("Plan", "EDGE-cross-sex-area-coverage-1")]
    [Trait("Plan", "EDGE-cross-sex-area-coverage-2")]
    [Trait("Plan", "EDGE-cross-sex-area-coverage-3")]
    [Trait("Plan", "EDGE-cross-sex-area-coverage-7")]
    public void Nude_smallclothes_only_when_sfw_matches_too_little() // [AC-cross-sex-area-coverage-2] [AC-cross-sex-area-coverage-4] [AC-cross-sex-area-coverage-5] [EDGE-cross-sex-area-coverage-1] [EDGE-cross-sex-area-coverage-2] [EDGE-cross-sex-area-coverage-3] [EDGE-cross-sex-area-coverage-7]
    {
        static UvAtlasCorrespondence.Stats L(float c) => default(UvAtlasCorrespondence.Stats) with { VertexCoverage = c };
        Assert.True(CrossSexRefit.TakesNude(L(0.8926f), L(0.9955f)));   // LavaBod+ onto TBSE's SFW, then Gen A
        Assert.False(CrossSexRefit.TakesNude(L(0.9811f), L(0.9834f)));  // Bibo+ SFW Undies: SFW kept though Gen A is higher
        Assert.False(CrossSexRefit.TakesNude(L(0.88f), L(0.89f)));      // both short: SFW's refusal stands
        Assert.False(CrossSexRefit.TakesNude(L(0.9001f), L(0.99f)));
        Assert.True(CrossSexRefit.TakesNude(L(0.8999f), L(0.9001f)));
        Assert.False(CrossSexRefit.TakesNude(L(0.85f), L(0.8999f)));
        Assert.False(CrossSexRefit.TakesNude(null, L(0.99f)));
        Assert.False(CrossSexRefit.TakesNude(L(0.85f), null));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-area-coverage-5")]
    public void The_save_check_stays_by_count() // [AC-cross-sex-area-coverage-5]
    {
        Assert.Equal(0.9, CrossSexRefit.MinCoverage);
        // A float coverage of exactly 0.9 widens to just under it, and is refused today; that is left as it is.
        Assert.False(CrossSexRefit.CoverageAllowed(0.9f));
        var plan = (BodyRetarget.Planned)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BodyRetarget.Planned));
        var dense = default(UvAtlasCorrespondence.Stats) with { VertexCoverage = 0.89f, AreaCoverage = 0.99f };
        Assert.False(new CrossSexRefitJob.Size(Body("0101", "_dwn", "SFW"), "t", plan, plan, null, 0, dense, "").SaveAllowed);
        var nude = default(UvAtlasCorrespondence.Stats) with { VertexCoverage = 0.995f };
        Assert.True(new CrossSexRefitJob.Size(Body("0101", "_dwn", "Gen A"), "t", plan, plan, null, 0, nude, "",
                                              Asked: Body("0101", "_dwn", "SFW"), AskedLanded: dense).SaveAllowed);
        Assert.Contains("CoverageAllowed(Landed?.VertexCoverage", Source("Proteus/Services/CrossSexRefitJob.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-area-coverage-6")]
    [Trait("Plan", "EDGE-cross-sex-area-coverage-10")]
    public void The_cli_fits_the_legs_it_was_given() // [AC-cross-sex-area-coverage-6] [EDGE-cross-sex-area-coverage-10]
    {
        Assert.DoesNotContain("Nude:", Source("Proteus.Refit/CrossSexCommand.cs"));
        var nude = typeof(CrossSexRefitJob.Inputs).GetConstructors().Single().GetParameters().Single(p => p.Name == "Nude");
        Assert.True(nude.HasDefaultValue);
        Assert.Null(nude.DefaultValue);
    }

    // ── the game's own women's legs made men's (vanilla-cross-sex-legs) ─────

    [Fact]
    [Trait("Plan", "AC-vanilla-cross-sex-legs-6")]
    public void Only_womens_legs_cross_for_the_game_s_gear() // [AC-vanilla-cross-sex-legs-6]
    {
        Assert.True(CrossSexRefit.OfferableForGame(201, 101, "_dwn"));
        Assert.True(CrossSexRefit.OfferableForGame(201, 101, "_DWN"));
        Assert.False(CrossSexRefit.OfferableForGame(201, 101, "_top"));
        Assert.False(CrossSexRefit.OfferableForGame(101, 201, "_dwn"));
        Assert.False(CrossSexRefit.OfferableForGame(101, 201, "_top"));
        Assert.False(CrossSexRefit.OfferableForGame(201, 101, "_glv"));
        Assert.False(CrossSexRefit.OfferableForGame(1401, 101, "_dwn"));
        Assert.False(CrossSexRefit.OfferableForGame(201, 101, null));
        // A mod's garments keep every cell they had.
        Assert.True(CrossSexRefit.Offerable(201, 101, "_top"));
        Assert.True(CrossSexRefit.Offerable(201, 101, "_dwn"));
        Assert.True(CrossSexRefit.Offerable(101, 201, "_top"));
        Assert.True(CrossSexRefit.Offerable(101, 201, "_dwn"));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-cross-sex-legs-8")]
    public void Vanilla_wearers_are_the_races_that_draw_the_c0101_model() // [AC-vanilla-cross-sex-legs-8]
    {
        var all = CrossSexRefit.FallThroughTo(101, 201);
        Assert.Equal(all, CrossSexRefit.FallThroughTo(101, 201, null));

        // Highlander M has the game's model; Lalafell M too, so Lalafell F stops there on her way down.
        var some = CrossSexRefit.FallThroughTo(101, 201, r => r is 301 or 1101);
        Assert.DoesNotContain((ushort)301, some);
        Assert.DoesNotContain((ushort)1101, some);
        Assert.DoesNotContain((ushort)1201, some);
        Assert.Contains((ushort)701, some);

        // The game's Midlander M model is the one replaced, never a reason to empty the list.
        var midlander = CrossSexRefit.FallThroughTo(101, 201, r => r == 101);
        Assert.NotEmpty(midlander);
        Assert.Contains((ushort)701, midlander);
        Assert.Equal(all, midlander);

        // Roegadyn M has the game's model: Hrothgar M, who falls through Roegadyn, draws that too.
        var roe = CrossSexRefit.FallThroughTo(101, 201, r => r == 901);
        Assert.DoesNotContain((ushort)901, roe);
        Assert.DoesNotContain((ushort)1501, roe);
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-11")]
    public void A_c0101_save_from_c0201_is_worn_by_lalafell_women_too_for_modded_gear() // [EDGE-vanilla-cross-sex-legs-11]
    {
        var men = CrossSexRefit.FallThroughTo(101, 201);
        Assert.Contains((ushort)1201, men);
        Assert.Contains((ushort)1101, men);
        Assert.DoesNotContain((ushort)201, men);
        Assert.DoesNotContain((ushort)801, men);
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-7")]
    public void Vanilla_made_for_below_reads_the_womens_own_body() // [EDGE-vanilla-cross-sex-legs-7]
    {
        // MadeForBelow reads the game's body for each race of this line alone (VanillaBodyCatalog.Read, drawn: false) —
        // not through a reader here: the catalog keeps what it read for the whole test process. The real read is
        // checked in CrossSexRefitVanillaLocalTests.
        Assert.Equal([(ushort)201], CrossSexRefit.MadeForLine(201, 101));
        Assert.Contains("CrossSexRefit.MadeForLine(drawn, to).Select(r => VanillaBodyCatalog.Read($\"{r:D4}\", readGame, drawn: false))",
                        Source("Proteus/Services/OtherRaceRefit.Below.cs"));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-9")]
    public void Coverage_under_ninety_percent_is_not_saved() // [EDGE-vanilla-cross-sex-legs-9]
    {
        Assert.False(CrossSexRefit.CoverageAllowed(0.89));
        Assert.True(CrossSexRefit.CoverageAllowed(0.9));
    }
}
