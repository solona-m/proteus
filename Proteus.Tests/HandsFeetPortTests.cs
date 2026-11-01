extern alias RefitCli;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Localization;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Women's shoes and gloves made men's (hands-feet-port), without game files: what Studio's "Refit for" and the CLI's
/// --for-race offer, how Studio lays them out and words them, the one inputs builder, and what the size set says about the
/// shoes and gloves it leaves out or can't cross. Real refits are in <see cref="HandsFeetPortLocalTests"/>.
/// </summary>
public sealed class HandsFeetPortTests : IDisposable
{
    private readonly List<string> made = [];

    public void Dispose()
    {
        foreach (string root in made)
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
    }

    private const string WomansShoe = "chara/equipment/e6116/model/c0201e6116_sho.mdl";
    private const string WomansGlove = "chara/equipment/e0613/model/c0201e0613_glv.mdl";
    private const string MansShoe = "chara/equipment/e6116/model/c0101e6116_sho.mdl";
    private const string MansGlove = "chara/equipment/e0613/model/c0101e0613_glv.mdl";

    private static BodyOption Body(string race, string slot, string name, string group = "Body")
        => new(group, "", name, $"{name}/{race}{slot}.mdl", $"chara/equipment/e0000/model/c{race}e0000{slot}.mdl", slot);

    private static BodySizeCatalog Catalog(params BodyOption[] options) => new("root", options);

    /// <summary>A body mod of both sexes with legs and chests, and only the women's feet and hands.</summary>
    private static BodySizeCatalog Both()
        => Catalog(Body("0101", "_top", "M"), Body("0101", "_dwn", "M legs"),
                   Body("0201", "_top", "F"), Body("0201", "_dwn", "F legs"), Body("0201", "_sho", "F feet"),
                   Body("0201", "_glv", "F hands"));

    private static IReadOnlyList<OtherRaceRefit.Candidate> Offered(string path, BodySizeCatalog? target = null,
                                                                   bool vanilla = false)
        => OtherRaceRefit.Offered(path, target ?? Both(), [new PenumbraModMeta.Redirect(path, "x.mdl", "G / O")], null, vanilla);

    // ── Studio: what "Refit for" offers ──────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-hands-feet-port-1")]
    public void A_womans_shoe_and_glove_are_offered_midlander_men() // [AC-hands-feet-port-1]
    {
        foreach (string path in new[] { WomansShoe, WomansGlove })
        {
            var men = Assert.Single(Offered(path), c => c.Race == 101);
            Assert.True(men.CrossSex);
            Assert.True(men.Below);
            var route = OtherRaceRefit.Resolve(RacialModelBake.Route.None, 101, 201, Offered(path));
            Assert.Equal(new RacialModelBake.Route(201, 101, Down: false, Lowered: true) { ByAlias = true }, route);
            Assert.Equal(path.Replace("c0201", "c0101"), route.SavePath(path));
            Assert.NotNull(route.Eqdp(route.SavePath(path)));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-1")]
    public void A_mans_shoe_and_glove_are_not_offered_women() // [EDGE-hands-feet-port-1]
    {
        var both = Catalog([.. Both().Options, Body("0201", "_dwn", "F legs 2")]);
        Assert.DoesNotContain(Offered(MansShoe, both), c => c.CrossSex);
        Assert.DoesNotContain(Offered(MansGlove, both), c => c.CrossSex);
        Assert.False(CrossSexRefit.EndsCross(101, 201, "_sho"));
    }

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-2")]
    public void The_games_own_womans_shoe_is_not_offered_men() // [EDGE-hands-feet-port-2]
    {
        Assert.DoesNotContain(Offered(WomansShoe, vanilla: true), c => c.CrossSex);
        Assert.DoesNotContain(Offered(WomansGlove, vanilla: true), c => c.CrossSex);
    }

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-3")]
    public void Offered_by_the_limb_not_the_feet() // [EDGE-hands-feet-port-3]
    {
        // Men's feet but no men's legs: nothing to refit a shoe's leg onto.
        var feetOnly = Catalog(Body("0101", "_sho", "M feet"), Body("0201", "_dwn", "F legs"));
        Assert.DoesNotContain(Offered(WomansShoe, feetOnly), c => c.CrossSex);
        // Men's legs without men's feet: offered; the job refits without the foot's shape.
        var legsOnly = Catalog(Body("0101", "_dwn", "M legs"), Body("0201", "_dwn", "F legs"));
        Assert.Contains(Offered(WomansShoe, legsOnly), c => c.CrossSex && c.Race == 101);
        // A glove goes by the men's chest.
        Assert.Contains(Offered(WomansGlove, Catalog(Body("0101", "_top", "M"))), c => c.CrossSex && c.Race == 101);
        Assert.DoesNotContain(Offered(WomansGlove, Catalog(Body("0101", "_glv", "M hands"))), c => c.CrossSex);
    }

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-20")]
    public void Same_sex_offers_unchanged_for_ends() // [EDGE-hands-feet-port-20]
    {
        // A women's-only body mod gives a women's shoe nothing to pick.
        var bibo = Catalog(Body("0201", "_top", "F"), Body("0201", "_dwn", "F legs"), Body("0201", "_sho", "F feet"));
        Assert.Empty(Offered(WomansShoe, bibo));
        // Same sex, another race: still by the shoe's own slot, as before.
        var highlander = Catalog(Body("0401", "_dwn", "Hi legs"), Body("0201", "_dwn", "F legs"));
        Assert.DoesNotContain(Offered(WomansShoe, highlander), c => c.Race == 401);
        var highlanderFeet = Catalog(Body("0401", "_sho", "Hi feet"), Body("0201", "_sho", "F feet"));
        Assert.Contains(Offered(WomansShoe, highlanderFeet), c => c.Race == 401 && !c.CrossSex);
    }

    // ── Studio: layout ──────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-hands-feet-port-4")]
    [Trait("Plan", "EDGE-hands-feet-port-5")]
    [Trait("Plan", "EDGE-hands-feet-port-24")]
    public void A_crossed_end_is_read_from_the_pick() // [AC-hands-feet-port-4] [EDGE-hands-feet-port-5] [EDGE-hands-feet-port-24]
    {
        OtherRaceRefit.Candidate[] men = [new(101, CrossSex: true, Below: true)];
        Assert.True(RetargetInputs.CrossSexEnd(101, 201, men, "_sho"));
        Assert.True(RetargetInputs.CrossSexEnd(101, 201, men, "_glv"));
        Assert.False(RetargetInputs.CrossSexEnd(0, 201, men, "_sho"));
        Assert.False(RetargetInputs.CrossSexEnd(201, 201, men, "_sho"));
        Assert.False(RetargetInputs.CrossSexEnd(301, 201, [new OtherRaceRefit.Candidate(301)], "_sho"));
        Assert.False(RetargetInputs.CrossSexEnd(101, 201, men, "_top"));
        Assert.False(RetargetInputs.CrossSexEnd(101, 201, [], "_sho"));   // a stale pick

        // The user's own tick stays theirs: in force under the pick, back as it was without it.
        Assert.True(RetargetInputs.KeepEndsInForce(false, true));
        Assert.True(RetargetInputs.KeepEndsInForce(true, false));
        Assert.False(RetargetInputs.KeepEndsInForce(false, false));
        var layout = RetargetInputs.LayoutFor("_sho", RetargetInputs.KeepEndsInForce(false, true), true, true, false);
        Assert.True(layout.KeepEnds);
        Assert.Equal("_dwn", layout.Primary);
        Assert.False(RetargetInputs.LayoutFor("_sho", RetargetInputs.KeepEndsInForce(false, false), true, true, false).KeepEnds);
    }

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-38")]
    public void Crossed_end_check_is_safe_before_the_bodies_load() // [EDGE-hands-feet-port-38]
    {
        OtherRaceRefit.Candidate[] men = [new(101, CrossSex: true, Below: true)];
        Assert.False(RetargetInputs.CrossSexEndOf(WomansShoe, 101, men, hasCatalog: false));
        Assert.True(RetargetInputs.CrossSexEndOf(WomansShoe, 101, men, hasCatalog: true));
        Assert.True(RetargetInputs.CrossSexEndOf(WomansGlove, 101, men, hasCatalog: true));
        Assert.False(RetargetInputs.CrossSexEndOf(WomansShoe, 101, [], hasCatalog: true));
        Assert.False(RetargetInputs.CrossSexEndOf("chara/equipment/e0001/model/c0201e0001.mdl", 101, men, true));
        Assert.False(RetargetInputs.CrossSexEndOf("chara/equipment/e6010/model/c0201e6010_top.mdl", 101, men, true));
        foreach (string path in new[] { WomansShoe, WomansGlove, MansShoe })
            foreach (ushort chosen in new ushort[] { 0, 101, 201 })
                Assert.Equal(RetargetInputs.CrossSexEnd(chosen, ModelSkinReader.RaceOf(path), men, BodySizeCatalog.SlotOf(path)!),
                             RetargetInputs.CrossSexEndOf(path, chosen, men, true));
    }

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-14")]
    public void A_crossed_end_offers_no_other_rows() // [EDGE-hands-feet-port-14]
    {
        var layout = RetargetInputs.LayoutFor("_sho", true, true, true, false);
        Assert.Equal(["_dwn"], RetargetInputs.Slots(layout, ["_top", "_dwn", "_sho"], _ => 3, onlyPrimary: true));
        Assert.Equal(["_dwn", "_top"], RetargetInputs.Slots(layout, ["_top", "_dwn", "_sho"], _ => 3));
    }

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-7")]
    public void A_crossed_end_never_redraws_skin_only() // [EDGE-hands-feet-port-7]
    {
        var layout = RetargetInputs.LayoutFor("_sho", true, true, true, false);
        var feet = Body("0201", "_sho", "F feet");
        var legs = Body("0201", "_dwn", "F legs");
        var to = new Dictionary<string, List<BodyOption>> { ["_dwn"] = [Body("0101", "_dwn", "M legs")] };
        var onlyFeet = new Dictionary<string, BodyOption> { ["_sho"] = feet };
        // Read as skin-only, the panel would redraw it; under the pick, the route is not handed on.
        Assert.True(RetargetInputs.Assemble(layout, ["_dwn"], onlyFeet, to, KeepEnds.Route.SkinOnly).SkinOnly);
        var crossed = RetargetInputs.Assemble(layout, ["_dwn"], onlyFeet, to, route: null);
        Assert.False(crossed.SkinOnly);
        Assert.False(RetargetInputs.Ready(crossed, onlyFeet, targetHasEnd: true, refused: false));
        var withLegs = new Dictionary<string, BodyOption> { ["_sho"] = feet, ["_dwn"] = legs };
        Assert.True(RetargetInputs.Ready(RetargetInputs.Assemble(layout, ["_dwn"], withLegs, to, null), withLegs, true, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-6")]
    public void A_crossed_end_that_cannot_keep_its_end_says_why() // [EDGE-hands-feet-port-6]
    {
        var ps = Strings.Parts;
        Assert.Equal(ps.RetargetEndsUnreadableCrossSex, RetargetInputs.CrossSexEndRefusal(true, true, true, "0201"));
        Assert.Equal(string.Format(ps.RetargetRefuseCrossSexMadeForFmt, ModelRace.Describe("0201")),
                     RetargetInputs.CrossSexEndRefusal(false, false, true, "0201"));
        Assert.Equal(ps.RetargetCrossSexEndNoSizes, RetargetInputs.CrossSexEndRefusal(false, true, false, "0201"));
        Assert.Null(RetargetInputs.CrossSexEndRefusal(false, true, true, "0201"));
        // The panel's crossed branch asks it before it hands anything to the job.
        string panel = CrossSexRefitTests.Source("Proteus/Gui/BodyRetargetPanel.CrossSex.cs");
        int refusal = panel.IndexOf("RetargetInputs.CrossSexEndRefusal(", StringComparison.Ordinal);
        int job = panel.IndexOf("CrossSexRefitJob.EndInputs(", StringComparison.Ordinal);
        Assert.True(refusal >= 0 && job > refusal);
    }

    // ── what the job's sizes become in Studio ───────────────────────────────

    private static BodyRetarget.Planned Planned()
        => (BodyRetarget.Planned)RuntimeHelpers.GetUninitializedObject(typeof(BodyRetarget.Planned));

    private static UvAtlasCorrespondence.Stats Landed(float coverage) => default(UvAtlasCorrespondence.Stats) with { VertexCoverage = coverage };

    private static CrossSexRefitJob.Size Size(BodyOption target, float coverage, int folds = 0, BodyOption? asked = null,
                                              float askedCoverage = 0f, bool saved = true, string refusal = "")
        => new(target, "t.mdl", null, saved ? Planned() : null, saved ? [1] : null, folds, Landed(coverage), refusal, asked,
               asked != null ? Landed(askedCoverage) : null);

    private static CrossSexRefitJob.Result Result(string refusal, params CrossSexRefitJob.Size[] sizes)
        => new(refusal, RacialModelBake.Route.None, null, null, null, null, sizes);

    [Fact]
    [Trait("Plan", "AC-hands-feet-port-2")]
    [Trait("Plan", "EDGE-hands-feet-port-8")]
    [Trait("Plan", "EDGE-hands-feet-port-9")]
    [Trait("Plan", "EDGE-hands-feet-port-27")]
    [Trait("Plan", "EDGE-hands-feet-port-33")]
    public void ToStudio_maps_the_jobs_sizes() // [AC-hands-feet-port-2] [EDGE-hands-feet-port-8] [EDGE-hands-feet-port-9] [EDGE-hands-feet-port-27] [EDGE-hands-feet-port-33]
    {
        var a = Body("0101", "_dwn", "A");
        var b = Body("0101", "_dwn", "B");
        var both = CrossSexRefitJob.ToStudio(Result("", Size(a, 1f), Size(b, 0.95f, folds: 5)), 2);
        Assert.Equal("", both.Error);
        Assert.Equal([a, b], both.Planned!.Select(p => p.To));
        Assert.Equal([0, 5], both.Folds);
        Assert.Equal(2, both.Quality.Split('\n').Length);
        Assert.Contains(string.Format(Strings.Parts.RetargetRaceCrossSexQualityFmt, 5,
                                      0.95.ToString("P0", System.Globalization.CultureInfo.CurrentCulture)), both.Quality);

        // One size under 90%: the whole plan is refused, that size named.
        var low = CrossSexRefitJob.ToStudio(Result("", Size(a, 1f), Size(b, 0.87f)), 2);
        Assert.Null(low.Planned);
        Assert.StartsWith(b.Label + ": ", low.Error);
        Assert.Contains(CrossSexRefit.Judged(0.87, 0).Refusal!, low.Error);

        // The job's own refusal.
        Assert.Equal("no pair", CrossSexRefitJob.ToStudio(Result("no pair"), 1).Error);

        // Fitted to the nude legs instead: kept under the size picked, saying so.
        var nude = Body("0101", "_dwn", "Gen A");
        var fitted = CrossSexRefitJob.ToStudio(Result("", Size(nude, 0.99f, asked: a, askedCoverage: 0.8f)), 1);
        Assert.Equal(a, Assert.Single(fitted.Planned!).To);
        Assert.Contains(nude.Label, fitted.Quality);
        Assert.Contains(a.Label, fitted.Quality);

        // No sizes and no refusal: a stale pick, never an empty plan.
        var none = CrossSexRefitJob.ToStudio(Result(""), 1);
        Assert.Null(none.Planned);
        Assert.Equal(Strings.Parts.RetargetCrossSexEndNoSizes, none.Error);
    }

    [Fact]
    [Trait("Plan", "AC-hands-feet-port-2")]
    public void Judged_words_like_the_panel() // [AC-hands-feet-port-2]
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var (refusal, _) = CrossSexRefit.Judged(0.87, 0);
        Assert.Equal(string.Format(Strings.Parts.RetargetRefuseCrossSexCoverageFmt, 0.87.ToString("P0", culture),
                                   CrossSexRefit.MinCoverage.ToString("P0", culture)), refusal);
        var (none, quality) = CrossSexRefit.Judged(0.95, 3);
        Assert.Null(none);
        Assert.Equal(string.Format(Strings.Parts.RetargetRaceCrossSexQualityFmt, 3, 0.95.ToString("P0", culture)), quality);
        // The size set says the same.
        var (save, note, _) = SizeSetRefit.Judge("Feet", "TBSE", "", "", true, 0.87, 0, null, null);
        Assert.False(save);
        Assert.Contains(refusal!, note);
        // One wording everywhere: the panel's check and the size set's judge both call it.
        Assert.Contains("CrossSexRefit.Judged(", CrossSexRefitTests.Source("Proteus/Gui/BodyRetargetPanel.CrossSex.cs"));
        Assert.Contains("CrossSexRefit.Judged(", CrossSexRefitTests.Source("Proteus/Services/SizeSet/SizeSetRefit.CrossSex.cs"));
    }

    // ── the one inputs builder ───────────────────────────────────────────────

    private static IPluginLog Log() => Substitute.For<IPluginLog>();
    private static UVRemapService NoMaps() => new(Log(), ".");

    [Fact]
    [Trait("Plan", "AC-hands-feet-port-3")]
    [Trait("Plan", "EDGE-hands-feet-port-30")]
    [Trait("Plan", "EDGE-hands-feet-port-27")]
    public void End_inputs_are_built_one_way() // [AC-hands-feet-port-3] [EDGE-hands-feet-port-30] [EDGE-hands-feet-port-27]
    {
        var legsS = Body("0201", "_dwn", "S", "Legs");
        var legsM = Body("0201", "_dwn", "M", "Legs");
        var feet = Body("0201", "_sho", "Feet", "Feet");
        var madeFor = Catalog(legsS, legsM, feet);
        // Studio's lists are bent: the same option under another file.
        var bentM = legsM with { Rel = "bent/m.mdl" };
        var sfw = SupportedBodies.Of("tbse", "_dwn", false)!.Sizes[0];
        var tbseSfw = new BodyOption("Legs", "", "SFW", sfw.Rel, "chara/equipment/e0000/model/c0101e0000_dwn.mdl", "_dwn");
        var tbseNude = tbseSfw with { Name = "Gen A", Rel = sfw.NudeRel! };
        var onto = Catalog(tbseSfw, tbseNude);
        var wearer = (OtherRaceWearer.Inputs)RuntimeHelpers.GetUninitializedObject(typeof(OtherRaceWearer.Inputs));
        byte[] drawn = [1, 2, 3], baked = [4, 5, 6];

        var inputs = CrossSexRefitJob.EndInputs(drawn, WomansShoe, 101, madeFor, bentM, onto, [tbseSfw.Rel], wearer, NoMaps(),
                                                Log(), pushOut: false, carryPiercings: true, ["Buckle"], baked, out string? why)!;
        Assert.Null(why);
        Assert.Same(drawn, inputs.Garment);
        Assert.Equal([legsM, feet], inputs.MadeFor.Options);
        Assert.Null(inputs.FromPick);
        Assert.True(inputs.TargetPick!(tbseSfw));
        Assert.False(inputs.TargetPick!(tbseNude));
        Assert.False(inputs.PushOut);
        Assert.True(inputs.CarryPiercings);
        Assert.Equal(["Buckle"], inputs.HeldParts!);
        Assert.Equal(tbseNude, inputs.Nude!(tbseSfw));
        Assert.Null(inputs.Nude!(tbseNude));

        // A glove: no nude legs.
        var chest = Body("0201", "_top", "F", "Chest");
        var glove = CrossSexRefitJob.EndInputs(drawn, WomansGlove, 101, Catalog(chest), chest, Catalog(Body("0101", "_top", "M")),
                                               ["M/0101_top.mdl"], wearer, NoMaps(), Log(), true, false, null, null, out _)!;
        Assert.Null(glove.Nude);
        Assert.Null(glove.HeldParts);

        // A made-for size that matches nothing: the made-for refusal.
        Assert.Null(CrossSexRefitJob.EndInputs(drawn, WomansShoe, 101, madeFor, Body("0201", "_dwn", "XL", "Legs"), onto,
                                               [tbseSfw.Rel], wearer, NoMaps(), Log(), true, false, null, null, out why));
        Assert.Equal(string.Format(Strings.Parts.RetargetRefuseCrossSexMadeForFmt, ModelRace.Describe("0201")), why);

        // Handed the bake's model as if drawn: it would be bent twice, so it throws.
        Assert.Throws<InvalidOperationException>(() => CrossSexRefitJob.EndInputs(baked, WomansShoe, 101, madeFor, bentM, onto,
            [tbseSfw.Rel], wearer, NoMaps(), Log(), true, false, null, baked, out _));
        Assert.Throws<InvalidOperationException>(() => CrossSexRefitJob.EndInputs([.. baked], WomansShoe, 101, madeFor, bentM,
            onto, [tbseSfw.Rel], wearer, NoMaps(), Log(), true, false, null, baked, out _));
        Assert.NotNull(CrossSexRefitJob.EndInputs(drawn, WomansShoe, 101, madeFor, bentM, onto, [tbseSfw.Rel], wearer, NoMaps(),
                                                  Log(), true, false, null, null, out _));

        // The size set narrows its made-for body the same way.
        Assert.Contains("CrossSexRefitJob.NarrowMadeFor(",
                        CrossSexRefitTests.Source("Proteus/Services/SizeSet/SizeSetRefit.CrossSexEnds.cs"));
        Assert.Contains("KeepEnds.ClothSitsOn(", CrossSexRefitTests.Source("Proteus/Services/SizeSet/SizeSetRefit.CrossSexEnds.cs"));
    }

    // ── the CLI ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-hands-feet-port-6")]
    public void ValidateCli_takes_womens_ends_for_men() // [AC-hands-feet-port-6]
    {
        string[] ok = ["game", "uvmaps", "from-root"];
        Assert.Null(CrossSexRefit.ValidateCli("0201", "0101", "_sho", ok));
        Assert.Null(CrossSexRefit.ValidateCli("0201", "0101", "_glv", ok));
        Assert.Equal("--for-race refits chest (_top) and legs (_dwn) garments both ways, and women's shoes (_sho) and gloves (_glv) for men",
                     CrossSexRefit.ValidateCli("0101", "0201", "_glv", ok));
        Assert.Equal(CrossSexRefit.CliSlotError, CrossSexRefit.ValidateCli("0201", "0101", "_met", ok));
    }

    private string Folder(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-handsfeet-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        made.Add(root);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>A body mod of one race: a legs, a chest, a feet and a hands option, each in its own group.</summary>
    private string BodyMod(string race)
    {
        string root = Folder("body" + race);
        string Option(string group, string slot)
            => $$"""{ "Version": 0, "Name": "{{group}}", "Type": "Single", "Priority": 0, "DefaultSettings": 0, "Options": [ { "Name": "A", "Files": { "chara/equipment/e0000/model/c{{race}}e0000{{slot}}.mdl": "{{group}}/a.mdl" } } ] }""";
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
            { "FileVersion": 4, "Name": "Body", "DefaultData": { "Files": {}, "FileSwaps": {}, "Manipulations": [] },
              "Groups": [ {{Option("legs", "_dwn")}}, {{Option("chest", "_top")}}, {{Option("feet", "_sho")}}, {{Option("hands", "_glv")}} ] }
            """);
        return root;
    }

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = RefitCli::Proteus.Refit.Program.Dispatch(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string[] ForRace(string garment, string from, string to, params string[] more)
    {
        string women = BodyMod("0201"), men = BodyMod("0101");
        return ["--garment", garment, "--race", "0201", "--for-race", "0101", "--from-root", women, "--body-root", men,
                "--from", from, "--to", to, "--out-dir", Folder("out"),
                "--game", Path.Combine(Folder("nogame"), "sqpack"), "--uvmaps", Folder("nomaps"), .. more];
    }

    [Fact]
    [Trait("Plan", "AC-hands-feet-port-6")]
    [Trait("Plan", "EDGE-hands-feet-port-15")]
    public void Cli_from_names_the_limb_body() // [AC-hands-feet-port-6] [EDGE-hands-feet-port-15]
    {
        string shoe = Path.Combine(Folder("garment"), "c0201e6116_sho.mdl");
        // The feet body named for a shoe: refused on the legs slot, before the (missing) game data is opened.
        var (exit, _, err) = Cli(ForRace(shoe, "feet/a.mdl", "legs/a.mdl=TBSE"));
        Assert.Equal(2, exit);
        Assert.Contains("--from names no c0201 _dwn body of ", err);
        // A chest body named as the target for a shoe.
        (exit, _, err) = Cli(ForRace(shoe, "legs/a.mdl", "chest/a.mdl=TBSE"));
        Assert.Equal(2, exit);
        Assert.Contains("--to names no c0101 _dwn body of ", err);
        // A men's shoe for women: the slot error.
        (exit, _, err) = Cli(["--garment", Path.Combine(Folder("garment"), "c0101e6116_sho.mdl"), "--race", "0101",
                              "--for-race", "0201", "--from-root", BodyMod("0101"), "--body-root", BodyMod("0201"),
                              "--from", "legs/a.mdl", "--to", "legs/a.mdl", "--out-dir", Folder("out"),
                              "--game", Folder("nogame"), "--uvmaps", Folder("nomaps")]);
        Assert.Equal(2, exit);
        Assert.Contains(CrossSexRefit.CliSlotError, err);
    }

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-16")]
    public void Cli_slot_override_reads_the_limb() // [EDGE-hands-feet-port-16]
    {
        string glove = Path.Combine(Folder("garment"), "x.mdl");
        var (exit, _, err) = Cli(ForRace(glove, "hands/a.mdl", "chest/a.mdl=TBSE", "--slot", "_glv", "--set", "613"));
        Assert.Equal(2, exit);
        Assert.Contains("--from names no c0201 _top body of ", err);
    }

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-27")]
    public void Cli_names_a_nude_fit_by_the_size_asked() // [EDGE-hands-feet-port-27]
    {
        var sfw = Body("0101", "_dwn", "SFW");
        var nude = Body("0101", "_dwn", "Gen A");
        var targets = new Dictionary<string, string> { [sfw.Rel.ToLowerInvariant()] = "TBSE" };
        var fitted = Size(nude, 0.99f, asked: sfw, askedCoverage: 0.8f);
        Assert.Equal("TBSE", RefitCli::Proteus.Refit.CrossSexCommand.LabelOf(targets, fitted));
        Assert.Throws<KeyNotFoundException>(() => targets[nude.Rel.ToLowerInvariant()]);   // the old lookup's crash
        Assert.Equal("TBSE", RefitCli::Proteus.Refit.CrossSexCommand.LabelOf(targets, Size(sfw, 0.95f)));

        var entry = CrossSexRefitReport.Size(fitted, "TBSE", null);
        Assert.Equal(nude.Rel, (string)entry["to"]!);
        Assert.Equal(sfw.Rel, (string)entry["fittedInsteadOf"]!);
        Assert.Null(CrossSexRefitReport.Size(Size(sfw, 0.95f), "TBSE", null)["fittedInsteadOf"]);
        Assert.Contains("LabelOf(targets, size)", CrossSexRefitTests.Source("Proteus.Refit/CrossSexCommand.cs"));
    }

    // ── the panel's wiring ──────────────────────────────────────────────────

    private static string StripComments(string code)
        => string.Join("\n", code.Split('\n').Select(l => l.IndexOf("//", StringComparison.Ordinal) is var i and >= 0 ? l[..i] : l));

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-30")]
    [Trait("Plan", "EDGE-hands-feet-port-35")]
    [Trait("Plan", "EDGE-hands-feet-port-24")]
    [Trait("Plan", "EDGE-hands-feet-port-38")]
    [Trait("Plan", "AC-hands-feet-port-10")]
    public void Panel_wiring_follows_the_plan() // [EDGE-hands-feet-port-30] [EDGE-hands-feet-port-35] [EDGE-hands-feet-port-24] [EDGE-hands-feet-port-38] [AC-hands-feet-port-10]
    {
        string crossSex = StripComments(CrossSexRefitTests.Source("Proteus/Gui/BodyRetargetPanel.CrossSex.cs"));
        string branch = crossSex[crossSex.IndexOf("void StartCrossSexEndPlan(", StringComparison.Ordinal)..];
        branch = branch[..branch.IndexOf("void DrawFoldLine(", StringComparison.Ordinal)];
        Assert.Contains("bake.Source", branch);
        Assert.Contains("bake.Model", branch);
        Assert.DoesNotContain("GarmentBytes", branch);

        string panel = StripComments(CrossSexRefitTests.Source("Proteus/Gui/BodyRetargetPanel.cs"));
        string detect = panel[panel.IndexOf("void StartDetect(", StringComparison.Ordinal)..];
        detect = detect[..detect.IndexOf("void StartValidate(", StringComparison.Ordinal)];
        Assert.Contains("RetargetInputs.ShapesFor(crossEnd, ctx.KeepShape)", detect);
        Assert.Contains("clothWhenSkinless: crossEnd", detect);

        string draw = panel[panel.IndexOf("public void Draw(in RetargetContext given)", StringComparison.Ordinal)..];
        string beforeEffective = draw[..draw.IndexOf("Effective(", StringComparison.Ordinal)];
        Assert.Contains("TakePendingPick(given)", beforeEffective);
        Assert.Contains("CrossSexEndOpen = RetargetInputs.CrossSexEndOf(", beforeEffective);
        foreach (string unsafeUse in new[] { "CrossSexEnd(ctx)", "CrossSexEnd(given)", "WornSlot(", "Onto!", "Onto?", "Onto." })
            Assert.DoesNotContain(unsafeUse, beforeEffective);
        Assert.DoesNotMatch(@"\brace\b", beforeEffective);

        string crossEnd = panel[panel.IndexOf("private bool CrossSexEnd(in RetargetContext ctx)", StringComparison.Ordinal)..];
        crossEnd = crossEnd[..crossEnd.IndexOf(';')];
        Assert.Contains("CrossSexEndOf(", crossEnd);
        Assert.DoesNotContain("WornSlot(", crossEnd);
    }

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-34")]
    public void Effective_skin_swap_under_a_crossed_end() // [EDGE-hands-feet-port-34]
    {
        Assert.True(RetargetInputs.EffectiveReplaceSkin(false, true));
        Assert.False(RetargetInputs.EffectiveReplaceSkin(false, false));
        Assert.True(RetargetInputs.CarryPiercings(RetargetInputs.EffectiveReplaceSkin(false, true), true));
        Assert.False(RetargetInputs.CarryPiercings(RetargetInputs.EffectiveReplaceSkin(false, false), true));
        Assert.Contains("RetargetInputs.CarryPiercings(RetargetInputs.EffectiveReplaceSkin(replaceSkin, true), carryPiercings)",
                        CrossSexRefitTests.Source("Proteus/Gui/BodyRetargetPanel.CrossSex.cs"));
    }

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-35")]
    public void Shapes_never_reach_a_crossed_end() // [EDGE-hands-feet-port-35]
    {
        var garment = ModelPartReader.Read(SyntheticModel.Build([], new SyntheticModel.Mesh("cloth.mtrl",
                                                                    new SyntheticModel.Sub(0, TrianglesPerIsland: 3))))!;
        string label = garment.Parts[0].Label;
        Assert.Null(RetargetInputs.ShapesFor(true, [label]));
        Assert.Empty(RefitCore.ShapePieces(garment, new HashSet<string>(RetargetInputs.ShapesFor(true, [label]) ?? [])));
        Assert.Equal([label], RetargetInputs.ShapesFor(false, [label])!);
    }

    [Fact]
    [Trait("Plan", "AC-hands-feet-port-11")]
    [Trait("Plan", "EDGE-hands-feet-port-21")]
    public void Keep_shape_is_greyed_under_a_crossed_end() // [AC-hands-feet-port-11] [EDGE-hands-feet-port-21]
    {
        Assert.Equal((false, false, true), RetargetInputs.KeepShapeBox(moves: true, keeps: true, crossEnd: true));
        Assert.Equal((true, true, false), RetargetInputs.KeepShapeBox(moves: true, keeps: true, crossEnd: false));
        Assert.Equal((false, false, false), RetargetInputs.KeepShapeBox(moves: false, keeps: true, crossEnd: false));
        Assert.Contains("RetargetInputs.KeepShapeBox(", CrossSexRefitTests.Source("Proteus/Gui/PartsPanel.cs"));
        Assert.Contains("RetargetInputs.ShapesFor(crossEnd, ctx.KeepShape)", CrossSexRefitTests.Source("Proteus/Gui/BodyRetargetPanel.cs"));
    }

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-26")]
    public void A_skinless_crossed_end_gets_a_size_or_the_hint() // [EDGE-hands-feet-port-26]
    {
        var noSkin = new KeepEnds.StudioReading(KeepEnds.Route.NoSkin, null, null);
        Assert.True(RetargetInputs.CrossSexEndHint(true, noSkin, limbChosen: false));
        Assert.False(RetargetInputs.CrossSexEndHint(true, noSkin with { Pick = Body("0201", "_dwn", "L") }, false));
        Assert.False(RetargetInputs.CrossSexEndHint(true, noSkin, limbChosen: true));
        Assert.False(RetargetInputs.CrossSexEndHint(false, noSkin, false));
        // A pick from the cloth reading fills the limb, not the feet.
        var layout = RetargetInputs.LayoutFor("_sho", true, true, true, false);
        var legs = Body("0201", "_dwn", "L");
        Assert.Equal(("_dwn", legs), RetargetInputs.StudioPrefill(layout, noSkin with { Pick = legs }, new Dictionary<string, BodyOption>()));
    }

    // ── the men's model points at the women's ───────────────────────────────

    [Fact]
    [Trait("Plan", "AC-hands-feet-port-10")]
    [Trait("Plan", "EDGE-hands-feet-port-22")]
    [Trait("Plan", "EDGE-hands-feet-port-36")]
    public void A_mans_end_points_at_the_womens_model() // [AC-hands-feet-port-10] [EDGE-hands-feet-port-22] [EDGE-hands-feet-port-36]
    {
        Assert.Equal(WomansGlove, OtherRaceRefit.WomensModelOf(MansGlove, [WomansGlove], false));
        Assert.Equal(WomansShoe, OtherRaceRefit.WomensModelOf(MansShoe, [WomansShoe.ToUpperInvariant()], false)!.ToLowerInvariant());
        // The author's own men's design: never pointed away from.
        Assert.Null(OtherRaceRefit.WomensModelOf(MansGlove, [WomansGlove, MansGlove], false));
        // The women's model is a game copy or our own refit (no piece): nothing to point at.
        Assert.Null(OtherRaceRefit.WomensModelOf(MansGlove, [], false));
        Assert.Null(OtherRaceRefit.WomensModelOf(MansGlove, [WomansGlove], isVanilla: true));
        Assert.Null(OtherRaceRefit.WomensModelOf("chara/equipment/e0613/model/c0101e0613_top.mdl",
                                                 ["chara/equipment/e0613/model/c0201e0613_top.mdl"], false));
        Assert.Null(OtherRaceRefit.WomensModelOf(WomansGlove, [WomansGlove], false));

        // A women's glove that is our own refit is never one of the mod's pieces.
        string root = Folder("record");
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
            { "FileVersion": 4, "Name": "Gloves", "DefaultData": { "Files": { "{{WomansGlove}}": "{{BodyRetargetWriter.Subfolder}}/g.mdl" }, "FileSwaps": {}, "Manipulations": [] },
              "Groups": [] }
            """);
        Assert.DoesNotContain(SizeSetRefit.PiecesOf(root, includeEnds: true), p => p.GamePath == WomansGlove);
    }

    [Fact]
    [Trait("Plan", "AC-hands-feet-port-10")]
    [Trait("Plan", "EDGE-hands-feet-port-23")]
    public void The_open_button_picks_men_once() // [AC-hands-feet-port-10] [EDGE-hands-feet-port-23]
    {
        var pick = new OtherRaceRefit.PendingPick();
        pick.Set(WomansGlove, 101);
        Assert.Null(pick.Take(MansGlove, "other.mdl"));
        Assert.Equal(new OtherRaceRefit.Choice("glove.mdl", 101), pick.Take(WomansGlove.ToUpperInvariant(), "glove.mdl"));
        Assert.Null(pick.Take(WomansGlove, "glove.mdl"));
        // Men not offered there: the pick falls back to the automatic route.
        var automatic = new RacialModelBake.Route(201, 101, Down: true);
        Assert.Equal(automatic, OtherRaceRefit.Resolve(automatic, 101, 201, []));
    }

    // ── the size set ────────────────────────────────────────────────────────

    /// <summary>A mod giving <paramref name="paths"/> in one "Size" group of options S and M; models absent.</summary>
    private string Mod(params string[] paths)
    {
        string root = Folder("mod");
        string Files(string option) => string.Join(", ", paths.Select((p, i) => $"\"{p}\": \"{option}\\\\{i}.mdl\""));
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
            { "FileVersion": 4, "Name": "Outfit",
              "DefaultData": { "Files": {}, "FileSwaps": {}, "Manipulations": [] },
              "Groups": [
                { "Version": 0, "Name": "Size", "Type": "Single", "Priority": 0, "DefaultSettings": 0, "Options": [
                  { "Name": "S", "Files": { {{Files("s")}} } },
                  { "Name": "M", "Files": { {{Files("m")}} } } ] } ] }
            """);
        return root;
    }

    private const string Top = "chara/equipment/e6000/model/c0201e6000_top.mdl";
    private const string Shoe = "chara/equipment/e6000/model/c0201e6000_sho.mdl";
    private const string Glove = "chara/equipment/e6000/model/c0201e6000_glv.mdl";
    private const string MensGlove = "chara/equipment/e6000/model/c0101e6000_glv.mdl";

    private static SizeSetRefit.RunLog Run(string root, IReadOnlyList<string> keys, bool ends)
    {
        var runLog = new SizeSetRefit.RunLog();
        SizeSetRefit.Run(new SizeSetRefit.Request(root, keys, false, false, [], Ends: ends), runLog, null, Log(), _ => { },
                         CancellationToken.None);
        return runLog;
    }

    private static string LeftOut(params string[] labels)
        => string.Format(Strings.SizeSet.EndsLeftOutFmt, string.Join(", ", labels));

    [Fact]
    [Trait("Plan", "AC-hands-feet-port-8")]
    [Trait("Plan", "EDGE-hands-feet-port-17")]
    [Trait("Plan", "EDGE-hands-feet-port-18")]
    public void Ends_left_out_are_said() // [AC-hands-feet-port-8] [EDGE-hands-feet-port-17] [EDGE-hands-feet-port-18]
    {
        string topAndShoe = Mod(Top, Shoe);
        var shoe = SizeSetRefit.PiecesOf(topAndShoe).Single(p => p.Slot == "_sho");
        var run = Run(topAndShoe, ["rue"], ends: false);
        Assert.Contains(LeftOut(shoe.Label), run.Notes);
        Assert.Single(run.Sizes, s => s.ReasonCode == SizeSetRefit.Codes.EndsLeftOut && s.GamePath == Shoe);

        var shoesOnly = Run(Mod(Shoe), ["rue"], ends: false);
        Assert.Contains(LeftOut(shoe.Label), shoesOnly.Notes);
        Assert.Contains(shoesOnly.Sizes, s => s.ReasonCode == SizeSetRefit.Codes.NoPieces);

        Assert.DoesNotContain(Run(Mod(Top), ["rue"], ends: false).Sizes, s => s.ReasonCode == SizeSetRefit.Codes.EndsLeftOut);
        Assert.DoesNotContain(Run(Mod(Top, Shoe), ["rue"], ends: true).Sizes, s => s.ReasonCode == SizeSetRefit.Codes.EndsLeftOut);

        // One read, then split: the pieces kept equal those read without shoes and gloves.
        var (kept, leftOut, all) = SizeSetRefit.ReadPieces(topAndShoe, null, ends: false);
        static string Key(SizeSetRefit.Piece p)
            => $"{p.GamePath}|{p.Slot}|{p.Group}|{p.FollowsChest}|{p.FollowsLegs}|{p.Unpaired}|"
             + string.Join(",", p.Options.Select(x => x.Option + "=" + x.File));
        Assert.Equal(SizeSetRefit.PiecesOf(topAndShoe, includeEnds: false).Select(Key), kept.Select(Key));
        Assert.Equal([Key(shoe)], leftOut.Select(Key));
        Assert.Equal(2, all.Count);
        Assert.Empty(SizeSetRefit.ReadPieces(topAndShoe, null, ends: true).EndsLeftOut);
    }

    private readonly string work = Path.Combine(Path.GetTempPath(), "proteus-handsfeet-cli-" + Guid.NewGuid().ToString("N"));

    private (int Exit, JsonNode Report) SizeSetCli(string mod)
    {
        made.Add(work);
        string mods = Path.Combine(work, "penumbra"), plugin = Path.Combine(work, "plugin");
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(Path.Combine(work, "sqpack", "ffxiv"));
        // Maps that open as TIFFs: the header alone, which is all the check reads (as SizeSetBatchTests writes them).
        Directory.CreateDirectory(Path.Combine(plugin, "uvmaps"));
        foreach (string name in UvMapFiles.WomenMaps.Concat(UvMapFiles.MenMaps))
            File.WriteAllBytes(Path.Combine(plugin, "uvmaps", name), [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0]);
        var (exit, stdout, stderr) = Cli("--size-set", mod, "--bodies", "rue", "--mods-root", mods,
                                         "--game", Path.Combine(work, "sqpack"), "--uvmaps", plugin, "--no-ends");
        Assert.True(exit == 0, stderr);
        return (exit, JsonNode.Parse(stdout)!);
    }

    [Fact]
    [Trait("Plan", "AC-hands-feet-port-8")]
    public void Cli_no_ends_report_says_ends_left_out() // [AC-hands-feet-port-8]
    {
        string mod = Mod(Top, Shoe);
        var shoe = SizeSetRefit.PiecesOf(mod).Single(p => p.Slot == "_sho");
        var (_, report) = SizeSetCli(mod);
        Assert.Contains(report["notes"]!.AsArray(), n => (string?)n == LeftOut(shoe.Label));
        Assert.Contains(report["sizes"]!.AsArray(), s => (string?)s!["reasonCode"] == "ends-left-out");
    }

    [Fact]
    [Trait("Plan", "AC-hands-feet-port-8")]
    [Trait("Plan", "EDGE-hands-feet-port-28")]
    public void Cli_no_ends_on_a_shoes_only_mod_says_why() // [AC-hands-feet-port-8] [EDGE-hands-feet-port-28]
    {
        string mod = Mod(Shoe);
        var shoe = SizeSetRefit.PiecesOf(mod).Single();
        var (_, report) = SizeSetCli(mod);
        Assert.Single(report["notes"]!.AsArray(), n => (string?)n == LeftOut(shoe.Label));
        var codes = report["sizes"]!.AsArray().Select(s => (string)s!["reasonCode"]!).ToList();
        Assert.Single(codes, c => c == "ends-left-out");
        Assert.Single(codes, c => c == "no-pieces");
    }

    private static SizeSetRefit.Piece PieceAt(string path)
        => new(path, BodySizeCatalog.SlotOf(path)!, "Size", [("S", "s.mdl")], "Hands — " + path[^12..^8], "Size",
               BodySizeCatalog.RaceOf(path)!);

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-25")]
    [Trait("Plan", "EDGE-hands-feet-port-31")]
    [Trait("Plan", "EDGE-hands-feet-port-32")]
    public void Authors_mens_model_is_a_stated_exclusion() // [EDGE-hands-feet-port-25] [EDGE-hands-feet-port-31] [EDGE-hands-feet-port-32]
    {
        // Run level: only "for men" ticked, so the author's men's glove gets only no-body-for-race.
        string mod = Mod(Top, Glove, MensGlove);
        var run = Run(mod, ["tbse@women"], ends: true);
        var women = SizeSetRefit.PiecesOf(mod).Single(p => p.GamePath == Glove);
        var stated = Assert.Single(run.Sizes, s => s.ReasonCode == SizeSetRefit.Codes.AuthorsMens);
        Assert.True(stated.Men);
        Assert.Equal("tbse", stated.Body);
        Assert.Contains(string.Format(Strings.SizeSet.AuthorsMensUntickedFmt, women.Label, "TBSE"), run.Notes);
        Assert.DoesNotContain(string.Format(Strings.SizeSet.AuthorsMensFmt, women.Label), run.Notes);

        // Rule level, by what became of the author's men's glove.
        var pieces = new[] { PieceAt(Glove), PieceAt(MensGlove) };
        string wrong = string.Format(Strings.SizeSet.AuthorsMensFmt, pieces[0].Label);
        string unticked = string.Format(Strings.SizeSet.AuthorsMensUntickedFmt, pieces[0].Label, "TBSE");
        SizeSetRefit.RunLog Seeded(params (SizeSetRefit.SizeStatus Status, string? Code)[] outcomes)
        {
            var log = new SizeSetRefit.RunLog();
            foreach (var (status, code) in outcomes) log.Add(pieces[1], status, code, "x");
            SizeSetRefit.StateAuthorsMens(pieces, ["tbse"], log);
            Assert.Single(log.Sizes, s => s.ReasonCode == SizeSetRefit.Codes.AuthorsMens && s.Men);
            return log;
        }
        foreach (var bad in new (SizeSetRefit.SizeStatus, string?)[]
                 {
                     (SizeSetRefit.SizeStatus.Skipped, SizeSetRefit.Codes.Unreadable),
                     (SizeSetRefit.SizeStatus.Skipped, SizeSetRefit.Codes.EndsUnreadable),
                     (SizeSetRefit.SizeStatus.Refused, SizeSetRefit.Codes.RefusedPair),
                     (SizeSetRefit.SizeStatus.Failed, SizeSetRefit.Codes.SaveFailed),
                 })
            Assert.Equal([wrong], Seeded(bad).Notes);
        foreach (var fine in new (SizeSetRefit.SizeStatus, string?)[][]
                 {
                     [(SizeSetRefit.SizeStatus.Made, null)],
                     [(SizeSetRefit.SizeStatus.Skipped, SizeSetRefit.Codes.AlreadyThere)],
                     [(SizeSetRefit.SizeStatus.Skipped, SizeSetRefit.Codes.AuthorsOwn)],
                     [(SizeSetRefit.SizeStatus.Skipped, SizeSetRefit.Codes.MadeOnIt)],
                     [(SizeSetRefit.SizeStatus.Made, null), (SizeSetRefit.SizeStatus.Refused, SizeSetRefit.Codes.RefusedPair)],
                 })
            Assert.Empty(Seeded(fine).Notes);
        Assert.Equal([unticked], Seeded((SizeSetRefit.SizeStatus.Skipped, SizeSetRefit.Codes.NoBodyForRace)).Notes);
    }

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-20")]
    public void A_same_sex_skinless_end_reads_as_before() // [EDGE-hands-feet-port-20]
    {
        // The flag defaults off, and only the crossed pick sets it.
        Assert.Contains("clothWhenSkinless: crossEnd", CrossSexRefitTests.Source("Proteus/Gui/BodyRetargetPanel.cs"));
        Assert.Contains("bool clothWhenSkinless = false", CrossSexRefitTests.Source("Proteus/Services/AutoRefit/KeepEnds.Studio.cs"));
    }
}
