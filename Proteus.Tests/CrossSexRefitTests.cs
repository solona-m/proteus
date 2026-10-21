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

    private static string Source(string file) => File.ReadAllText(SourcePath(file));

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
        Assert.Equal(OtherRaceRefit.Block.None, men.Blocked);
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

    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-2")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-16")]
    public void The_authors_own_mens_top_is_pointed_at_for_a_womans_top() // [AC-cross-sex-tops-yas-2] [EDGE-cross-sex-tops-yas-16]
    {
        const string mens = "chara/equipment/e6010/model/c0101e6010_top.mdl";
        var offered = OtherRaceRefit.Offered(WomansTop, Both(),
            [new PenumbraModMeta.Redirect(WomansTop, "x.mdl", "G / O"), new PenumbraModMeta.Redirect(mens, "m.mdl", "G / O")],
            null, false);
        var men = Assert.Single(offered, c => c.Race == 101);
        Assert.Equal(OtherRaceRefit.Block.OwnModel, men.Blocked);
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
        string panel = Source("Proteus/Gui/BodyRetargetPanel.cs");
        Assert.Contains("switch (CrossSexRefit.FoldLineFor(plannedCrossSexFolds != null, clothFolded, done.Report.Folded))", panel);
        int at = panel.IndexOf("case CrossSexRefit.FoldLine.CrossSex:", StringComparison.Ordinal);
        Assert.True(at > 0);
        string arm = panel.Substring(at, 300);
        Assert.Contains("ProteusStyle.Warn", arm);
        Assert.Contains("ps.RetargetRaceCrossSexFoldedFmt", arm);
        Assert.Contains("hole", Localization.Strings.Parts.RetargetRaceCrossSexFoldedFmt);
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
        Assert.Contains("CrossSexRefit.CoverageAllowed(coverage)", Source("Proteus/Gui/BodyRetargetPanel.cs"));
        Assert.Contains("RetargetRaceCrossSexQualityFmt", Source("Proteus/Gui/BodyRetargetPanel.cs"));
        Assert.Contains("[\"folds\"] = size.Folds", Source("Proteus.Refit/CrossSexCommand.cs"));
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
        Assert.Contains(Offered(MansTop, both), c => c.Race == 201 && c.Blocked == OtherRaceRefit.Block.None);
        Assert.Same(both, OtherRaceRefit.MadeFor(null, null, both, "0101", _ => throw new InvalidOperationException()));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-refit-15")]
    public void Shared_pairs_are_built_with_the_sides_sexes() // [EDGE-cross-sex-refit-15]
    {
        string panel = Source("Proteus/Gui/BodyRetargetPanel.cs");
        Assert.Contains("if (Build(sourcePath, targetPath, name, male, masks, out var pair, out _, targetMale) is { } refusal)", panel);
        Assert.Contains("return Build(sourcePath, targetPath, name, male, masks, out _, out _, targetMale) ?? \"\";", panel);
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
        Assert.Contains("CrossSexRefit.CoverageAllowed(coverage)", Source("Proteus/Gui/BodyRetargetPanel.cs"));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-refit-16")]
    public void No_bodies_message_no_longer_says_a_refit_cant_change_sex() // [EDGE-cross-sex-refit-16]
    {
        string en = Source("Proteus/Localization/en.json");
        Assert.DoesNotContain("can't change sex", en);
        Assert.Contains("\"Parts.Retarget.Race.CrossSexHint.Fmt\"", en);
    }
}
