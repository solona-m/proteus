using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using Choice = Proteus.Services.SizeSetRefit.EndMadeForChoice;
using Route = Proteus.Services.KeepEnds.Route;

namespace Proteus.Tests;

/// <summary>Women's shoes and gloves made men's in the size set (mens-ends-crotch): the decisions, without the bodies.</summary>
public class SizeSetEndsMenTests
{
    /// <summary>
    /// Which body a women's shoe or glove is made a men's from. Read off its own skin: crossed when that body pairs with a
    /// man's, refused when it is gen3. With no leg or arm skin: the "Made on" body, then the body its cloth sits on,
    /// then the game's own. A foot or hand alone that reads no layout is taken as skinless; one on gen3 is refused.
    /// </summary>
    [Theory]
    [InlineData("Skin", "bibo", false, false, true, "Read")]
    [InlineData("Cloth", "gen2", false, false, true, "Read")]
    [InlineData("Skin", "gen3", true, true, true, "NoSource")]
    [InlineData("NoSkin", null, true, true, true, "MadeOn")]
    [InlineData("NoSkin", null, false, true, true, "Cloth")]
    [InlineData("NoSkin", null, false, false, true, "GameBody")]
    [InlineData("NoSkin", null, false, false, false, "NoSource")]
    [InlineData("SkinOnly", "bibo", false, true, true, "Cloth")]
    [InlineData("SkinOnly", null, false, false, true, "GameBody")]
    [InlineData("SkinOnly", "gen3", true, true, true, "NoSource")]
    [InlineData("Unsure", "bibo", true, true, true, "Unreadable")]
    [InlineData("NoFamily", null, true, true, true, "Unreadable")]
    [Trait("Plan", "AC-mens-ends-crotch-4")]
    [Trait("Plan", "EDGE-mens-ends-crotch-2")]
    [Trait("Plan", "EDGE-mens-ends-crotch-3")]
    [Trait("Plan", "EDGE-mens-ends-crotch-19")]
    [Trait("Plan", "EDGE-mens-ends-crotch-20")]
    public void Made_for_choice(string route, string? layout, bool madeOn, bool cloth, bool game, string expected) // [AC-mens-ends-crotch-4] [EDGE-mens-ends-crotch-2] [EDGE-mens-ends-crotch-3] [EDGE-mens-ends-crotch-19] [EDGE-mens-ends-crotch-20]
        => Assert.Equal(Enum.Parse<Choice>(expected), SizeSetRefit.EndMadeFor(Enum.Parse<Route>(route), layout, madeOn, cloth, game));

    /// <summary>
    /// What Check in game says of the body a men's shoe or glove was made from: the game's own body always; a body read by
    /// its cloth only when the cloth sits more than <see cref="SizeSetRefit.EndClothGapWarn"/> off it on average.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-mens-ends-crotch-4")]
    [Trait("Plan", "EDGE-mens-ends-crotch-20")]
    public void Check_in_game_says_how_it_was_read() // [AC-mens-ends-crotch-4] [EDGE-mens-ends-crotch-20]
    {
        Assert.Equal(0.005f, SizeSetRefit.EndClothGapWarn);
        Assert.Equal(0.0005f, SizeSetRefit.EndClothTie);
        Assert.Equal([string.Format(Strings.SizeSet.EndFromGameBodyFmt, "Feet", "TBSE")],
                     SizeSetRefit.EndWarnings("Feet", "TBSE", Choice.GameBody, null, null));
        Assert.Empty(SizeSetRefit.EndWarnings("Hands", "TBSE", Choice.Cloth, 0.005f, "LavaBod+"));
        Assert.Equal([string.Format(Strings.SizeSet.EndClothGapFmt, "Hands", "TBSE", 5.01f, "LavaBod+")],
                     SizeSetRefit.EndWarnings("Hands", "TBSE", Choice.Cloth, 0.00501f, "LavaBod+"));
        Assert.Single(SizeSetRefit.EndWarnings("Hands", "TBSE", Choice.MadeOn, 0.009f, "LavaBod+"));
        Assert.Empty(SizeSetRefit.EndWarnings("Hands", "TBSE", Choice.Read, 0.02f, "LavaBod+"));
    }

    /// <summary>
    /// The body a skinless shoe's or glove's cloth sits on: the nearest, but within half a millimetre of it the one listed
    /// first — the body preferred is listed first. None when it sits on none.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-mens-ends-crotch-20")]
    public void The_nearest_body_by_cloth_keeps_the_preferred_on_a_tie() // [EDGE-mens-ends-crotch-20]
    {
        const float tie = SizeSetRefit.EndClothTie;
        Assert.Equal(-1, SizeSetRefit.NearestByCloth([null, null], tie));
        Assert.Equal(1, SizeSetRefit.NearestByCloth([null, 0.003f], tie));
        // A clear win, then a tie: within half a millimetre the first listed keeps it.
        Assert.Equal(1, SizeSetRefit.NearestByCloth([0.004f, 0.002f], tie));
        Assert.Equal(0, SizeSetRefit.NearestByCloth([0.0024f, 0.002f], tie));
        // Against the nearest, not the one before: 3.0, 2.6 and 2.4 mm go to the 2.6, listed before the 2.4.
        Assert.Equal(1, SizeSetRefit.NearestByCloth([0.003f, 0.0026f, 0.0024f], tie));
    }

    /// <summary>
    /// Studio's "Refit for" and the CLI's --for-race (the job's <see cref="CrossSexRefitJob.Run"/>) still refuse a shoe or
    /// glove before reading anything; only the size set's <see cref="CrossSexRefitJob.RunEnd"/> takes one, and only a
    /// women's one made a men's.
    /// </summary>
    [Theory]
    [InlineData("_sho")]
    [InlineData("_glv")]
    [Trait("Plan", "EDGE-mens-ends-crotch-22")]
    public void Run_still_refuses_shoes_and_gloves(string slot) // [EDGE-mens-ends-crotch-22]
    {
        string path = $"chara/equipment/e0633/model/c0201e0633{slot}.mdl";
        var r = CrossSexRefitJob.Run(new CrossSexRefitJob.Inputs([], path, 101, null!, null, null!, null, default!, null!, null!));
        Assert.Contains("is not refitted for c0101", r.Refusal);
        Assert.Empty(r.Sizes);
        Assert.True(CrossSexRefit.EndsCross(201, 101, slot));
        Assert.False(CrossSexRefit.EndsCross(101, 201, slot));
        Assert.False(CrossSexRefit.EndsCross(201, 101, "_top"));
        Assert.False(CrossSexRefit.Offerable(201, 101, slot));
    }

    /// <summary>The README in every language and the CLI's help say a women's mod's shoes and gloves get men's versions.</summary>
    [Fact]
    [Trait("Plan", "AC-mens-ends-crotch-6")]
    public void The_readmes_and_the_cli_help_say_shoes_and_gloves_cross() // [AC-mens-ends-crotch-6]
    {
        var markers = new (string File, string Marker)[]
        {
            ("README.md", "Its women's shoes and gloves get men's versions too"),
            ("docs/README.de.md", "Auch seine Schuhe und Handschuhe für Frauen bekommen Männerversionen"),
            ("docs/README.es.md", "Sus zapatos y guantes de mujer también reciben versión de hombre"),
            ("docs/README.fr.md", "Ses chaussures et gants pour femmes reçoivent aussi une version pour hommes"),
            ("docs/README.ja.md", "女性用の靴と手袋にも男性版が作られ"),
            ("docs/README.ko.md", "여성용 신발과 장갑도"),
            ("docs/README.ru.md", "Его женские обувь и перчатки тоже получают мужские версии"),
            ("docs/README.zh.md", "它的女性鞋子和手套也会得到男性版本"),
        };
        foreach (var (file, marker) in markers)
        {
            string text = CrossSexRefitTests.Source(file);
            Assert.Contains(marker, text);
            Assert.DoesNotContain("Shoes and gloves aren't refitted across the sexes", text);
        }
        Assert.Contains("--men: a women's mod's chest, legs, shoes and gloves get men's versions; --no-ends leaves",
                        CrossSexRefitTests.Source("Proteus.Refit/Program.cs"));
    }

    // ── #47: crossed ends are read on recognised bodies before any other installed body-model mod ──────

    private const string RueSmall = "files/chest - smallclothes/small/chara/equipment/e0000/model/c0201e0000_top.mdl";
    private const string NeoSmall = "default chest - smallclothes/nsfw s.mdl";
    private const string TopPath = "chara/equipment/e0000/model/c0201e0000_top.mdl";
    private const string GlvPath = "chara/equipment/e0000/model/c0201e0000_glv.mdl";

    private static BodyModIndex.Entry Body(string dir, params string[] rels)
        => new(dir, dir, new BodySizeCatalog("<" + dir + ">",
            rels.Select(r => new BodyOption("Chest", "", Path.GetFileNameWithoutExtension(r), r,
                                            r.EndsWith("_glv.mdl") ? GlvPath : TopPath,
                                            r.EndsWith("_glv.mdl") ? "_glv" : "_top")).ToList()));

    private static readonly BodyModIndex.Entry Rue = Body("Rue", RueSmall);
    private static readonly BodyModIndex.Entry Elegy = Body("Elegy - Sylvie Bra & Panties (Rue-YAB-UranusRe)",
        "elegy/bra/c0201e0000_top.mdl", "elegy/hands/c0201e0000_glv.mdl");

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-3")]
    [Trait("Plan", "EDGE-issue-sweep-b-9")]
    public void Recognised_bodies()
    {
        var neo = Body("Neolithe", NeoSmall);
        var neoYas = Body("Neolithe YAS AIO", NeoSmall);
        var set = SupportedBodies.Recognised([Elegy, Rue, neo, neoYas]);
        Assert.Contains("Rue", set);
        Assert.Contains("RUE", set);   // case-insensitive
        Assert.Contains("Neolithe", set);
        Assert.DoesNotContain(Elegy.Dir, set);
        Assert.DoesNotContain("Neolithe YAS AIO", set);   // plain Neolithe is there: the YAS re-release is no body chosen
        Assert.Contains("Neolithe YAS AIO", SupportedBodies.Recognised([neoYas]));   // alone, it is the body
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-3")]
    [Trait("Plan", "EDGE-issue-sweep-b-7")]
    public void Cloth_guess_prefers_recognised_with_unrecognised_first()
    {
        var opt = (BodyModIndex.Entry e) => e.Catalog.Options[0];
        (BodyOption, float)? Gap(BodyModIndex.Entry e) => e == Elegy ? (opt(e), 0.001f) : (opt(e), 0.004f);
        var pick = SizeSetRefit.PickClothGuess([Elegy, Rue], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rue" },
                                               Gap, SizeSetRefit.EndClothTie);
        Assert.NotNull(pick);
        Assert.Same(Rue, pick!.Value.Entry);
        Assert.Equal(Rue.Catalog.Options[0], pick.Value.Option);   // its own reading, not Elegy's
        Assert.Equal(0.004f, pick.Value.Gap);
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-3")]
    [Trait("Plan", "EDGE-issue-sweep-b-6")]
    public void Cloth_guess_falls_back_to_unrecognised_then_none()
    {
        var recognised = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Rue" };
        (BodyOption, float)? OnlyElegy(BodyModIndex.Entry e) => e == Elegy ? (e.Catalog.Options[0], 0.003f) : null;
        var pick = SizeSetRefit.PickClothGuess([Rue, Elegy], recognised, OnlyElegy, SizeSetRefit.EndClothTie);
        Assert.Same(Elegy, pick!.Value.Entry);   // NIRÆ, AB Body… users keep a cloth reading
        Assert.Null(SizeSetRefit.PickClothGuess([Rue, Elegy], recognised, _ => null, SizeSetRefit.EndClothTie));
        Assert.Equal(Choice.GameBody, SizeSetRefit.EndMadeFor(Route.NoSkin, null, false, false, true));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-3")]
    [Trait("Plan", "EDGE-issue-sweep-b-8")]
    public void Only_the_cloth_guess_changed()
    {
        var src = File.ReadAllText(Path.Combine(SourceDir(), "SizeSetRefit.CrossSexEnds.cs"));
        Assert.Contains("PickClothGuess(women, SupportedBodies.Recognised(request.Bodies)", src);
        Assert.Contains("var mods = women.Select(b => (b.Dir, b.Catalog)).ToList();", src);   // DetectSource: every body, in order
        Assert.Contains("KeepEnds.Find(reading, garment, bones, \"0201\",", src);
    }

    private static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", "Services", "SizeSet");
}
