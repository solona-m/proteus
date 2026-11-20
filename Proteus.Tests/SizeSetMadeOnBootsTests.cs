extern alias RefitCli;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using Codes = Proteus.Services.SizeSetRefit.Codes;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;
using Fit = Proteus.Services.SizeSetRefit.MadeOnFit;
using How = Proteus.Services.SizeSetRefit.EndMadeOnHow;
using Kind = Proteus.Services.SizeSetRefit.EndReadKind;

namespace Proteus.Tests;

/// <summary>
/// A skinless women's shoe sized on the "Made on" body (<c>SizeSetRefit.MadeOnEnds.cs</c>): each of the author's sizes
/// placed on that body's legs by its shaft's cloth, else by its option name. The rules, on hand-made catalogs; the real
/// run on Oops I Did It Again's boots is in <see cref="SizeSetMadeOnBootsLocalTests"/>.
/// </summary>
public class SizeSetMadeOnBootsTests
{
    private const string Legs = "chara/equipment/e0000/model/c0201e0000_dwn.mdl";
    private const string Chest = "chara/equipment/e0000/model/c0201e0000_top.mdl";

    private static BodyOption Option(string rel)
        => new("Size", "", Path.GetFileName(Path.GetDirectoryName(rel.Replace('/', Path.DirectorySeparatorChar)) ?? rel) ?? rel, rel,
               rel.EndsWith("_dwn.mdl", StringComparison.OrdinalIgnoreCase) ? Legs : Chest,
               rel.EndsWith("_dwn.mdl", StringComparison.OrdinalIgnoreCase) ? "_dwn" : "_top");

    private static SupportedBodies.Body Table(string key, string slot) => SupportedBodies.Of(key, slot, false)!;

    private static BodySizeCatalog Catalog(IEnumerable<SupportedBodies.Body> tables, params string[] extra)
        => new("<body>", tables.SelectMany(t => t.Sizes).SelectMany(s => new[] { s.Rel, s.LegsRel }).OfType<string>()
                               .Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase).Select(Option).ToList());

    private static BodyModIndex.Entry Entry(string name, BodySizeCatalog catalog) => new(name, name, catalog);

    private static readonly SupportedBodies.Body BiboLegs = Table("bibo", "_dwn");
    private static readonly SupportedBodies.Body BiboChest = Table("bibo", "_top");
    private static BodyModIndex.Entry Bibo(params string[] extra) => Entry("Bibo+", Catalog([BiboChest, BiboLegs], extra));

    private static BodyOption LegsOf(BodyModIndex.Entry body, string size)
        => SizeSetRefit.OptionByFile(body.Catalog.For("_dwn", "0201"), BiboLegs.Sizes.First(s => s.Name == size).Rel)!;

    private static (BodyOption, float, BodySizeMatch.Ranking) Cloth(BodyOption on, Confidence confidence, float gap = 0.002f)
        => (on, gap, new BodySizeMatch.Ranking(confidence, [new BodySizeMatch.Score(on, 0f, gap)], FromCloth: true));

    private static readonly Func<BodyOption, (int Near, int Inside, float Gap)?> Sits = _ => (300, 0, 0.002f);

    private static SizeSetRefit.EndMadeOnRead Place((BodyOption, float, BodySizeMatch.Ranking)? cloth, string? option,
                                                    BodyModIndex.Entry body, bool followsChest = false)
        => SizeSetRefit.PlaceEndOnMadeOn(cloth, option, body, "0201", yas: false, followsChest, Sits);

    // ── placing one size ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-made-on-skinless-boots-2")]
    [Trait("Plan", "EDGE-made-on-skinless-boots-3")]
    public void Clear_cloth_places_the_size_on_the_made_on_legs() // [AC-made-on-skinless-boots-2] [EDGE-made-on-skinless-boots-3]
    {
        var bibo = Bibo();
        var small = LegsOf(bibo, "S");
        foreach (string option in new[] { "Small", "Large" })
        {
            var r = Place(Cloth(small, Confidence.Likely), option, bibo);
            Assert.Equal(How.Cloth, r.How);
            Assert.Equal(small, r.On);
            Assert.Equal(Confidence.Likely, r.Confidence);
            Assert.Equal(Fit.Taken, r.Fit);
            Assert.Equal(("bibo", "S"), (r.Own!.Key, r.Own.Size));
            Assert.Equal((300, 0, 0.002f), r.ClothOn);
            Assert.Contains("by its shaft's cloth", r.Why);
        }
    }

    [Fact]
    [Trait("Plan", "AC-made-on-skinless-boots-3")]
    [Trait("Plan", "EDGE-made-on-skinless-boots-1")]
    public void Unclear_cloth_falls_back_to_the_option_name() // [AC-made-on-skinless-boots-3] [EDGE-made-on-skinless-boots-1]
    {
        var bibo = Bibo();
        var medium = LegsOf(bibo, "M");
        Assert.False(SizeSetRefit.ClothClear(null));
        foreach (var c in new[] { Confidence.Ambiguous, Confidence.TooLittle, Confidence.NoBodyMesh })
            Assert.False(SizeSetRefit.ClothClear(Cloth(medium, c)), c.ToString());

        var r = Place(null, "Small", bibo);
        Assert.Equal(How.Name, r.How);
        Assert.Contains("type a - small", r.On!.Rel);
        Assert.Equal("bibo", r.Own!.Key);
        Assert.Equal(Confidence.NoBodyMesh, r.Confidence);
        Assert.Equal(Fit.Trusted, r.Fit);
        Assert.Equal((300, 0, 0.002f), r.ClothOn);
    }

    [Fact]
    [Trait("Plan", "AC-made-on-skinless-boots-3")]
    [Trait("Plan", "EDGE-made-on-skinless-boots-2")]
    public void A_guess_cloth_reading_loses_to_the_option_name() // [AC-made-on-skinless-boots-3] [EDGE-made-on-skinless-boots-2]
    {
        var bibo = Bibo();
        var medium = LegsOf(bibo, "M");
        Assert.False(SizeSetRefit.ClothClear(Cloth(medium, Confidence.Guess)));
        Assert.True(SizeSetRefit.ClothClear(Cloth(medium, Confidence.Likely)));
        Assert.True(SizeSetRefit.ClothClear(Cloth(medium, Confidence.Exact)));

        // A Guess on M is not kept: the size is placed as "Small" says.
        var guess = Cloth(medium, Confidence.Guess);
        var r = Place(SizeSetRefit.ClothClear(guess) ? guess : null, "Small", bibo);
        Assert.Equal(How.Name, r.How);
        Assert.Contains("type a - small", r.On!.Rel);
    }

    [Fact]
    [Trait("Plan", "EDGE-made-on-skinless-boots-4")]
    [Trait("Plan", "EDGE-made-on-skinless-boots-5")]
    public void Names_that_give_no_size_place_nothing() // [EDGE-made-on-skinless-boots-4] [EDGE-made-on-skinless-boots-5]
    {
        var bibo = Bibo();
        foreach (string? option in new[] { "Rue Small", "Wild Rose", null })
        {
            var r = Place(null, option, bibo);
            Assert.Equal(How.None, r.How);
            Assert.Equal(Fit.NoMatch, r.Fit);
            Assert.Null(r.On);
            Assert.Null(r.Own);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-made-on-skinless-boots-6")]
    [Trait("Plan", "EDGE-made-on-skinless-boots-16")]
    public void The_table_limits_the_legs_ranked() // [EDGE-made-on-skinless-boots-6] [EDGE-made-on-skinless-boots-16]
    {
        const string TypeB = "bottoms - small clothes/type b - small/chara/equipment/e0000/model/c0201e0000_dwn.mdl";
        var bibo = Bibo(TypeB);
        var legs = SizeSetRefit.MadeOnLegs(bibo, "0201", yas: false, followsChest: false);
        Assert.Equal(3, legs.Count);
        Assert.All(legs, o => Assert.Contains("type a - ", o.Rel));
        Assert.DoesNotContain(legs, o => o.Rel == TypeB);

        // A body Proteus knows no named sizes of: every legs model is ranked, and nothing is placed by name.
        var outfit = Entry("Outfit", new BodySizeCatalog("<body>", [Option("some/a/" + Legs), Option("some/b/" + Legs)]));
        Assert.Equal(2, SizeSetRefit.MadeOnLegs(outfit, "0201", false, false).Count);
        var unclear = Place(null, "Small", outfit);
        Assert.Equal(How.None, unclear.How);
        Assert.Equal(Fit.NoTable, unclear.Fit);
        var clear = Place(Cloth(outfit.Catalog.For("_dwn", "0201")[0], Confidence.Likely), "Small", outfit);
        Assert.Equal(How.Cloth, clear.How);
        Assert.Null(clear.Own);
    }

    [Fact]
    [Trait("Plan", "EDGE-made-on-skinless-boots-12")]
    public void A_follower_is_placed_on_its_chest_sizes_legs() // [EDGE-made-on-skinless-boots-12]
    {
        var bibo = Bibo();
        var medium = Place(null, "Medium", bibo, followsChest: true);
        Assert.Equal(How.Name, medium.How);
        Assert.Equal(RefitCore.Normal(BiboChest.Sizes.First(s => s.Name == "M").LegsRel!), RefitCore.Normal(medium.On!.Rel));
        // Two lead options over one file: each placed by its own name.
        var small = Place(null, "Small", bibo, followsChest: true);
        Assert.NotEqual(RefitCore.Normal(small.On!.Rel), RefitCore.Normal(medium.On.Rel));
        // The follower's legs ranked are the chest sizes' legs.
        Assert.All(SizeSetRefit.MadeOnLegs(bibo, "0201", false, followsChest: true), o => Assert.Contains("type a - ", o.Rel));
    }

    // ── the piece as a whole ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-made-on-skinless-boots-13")]
    [Trait("Plan", "AC-made-on-skinless-boots-3")]
    [Trait("Plan", "AC-made-on-skinless-boots-5")]
    public void A_cloth_collision_sends_every_size_to_its_name() // [EDGE-made-on-skinless-boots-13] [AC-made-on-skinless-boots-3] [AC-made-on-skinless-boots-5]
    {
        var bibo = Bibo();
        BodyOption s = LegsOf(bibo, "S"), m = LegsOf(bibo, "M"), l = LegsOf(bibo, "L");
        Assert.True(SizeSetRefit.ClothCollides([("Small", s, s), ("Medium", s, m)], out string why));
        Assert.Contains("\"Small\"", why);
        Assert.Contains("\"Medium\"", why);
        // Cloth on the size another option's name gives, that option's cloth being unclear.
        Assert.True(SizeSetRefit.ClothCollides([("Small", m, s), ("Medium", null, m)], out why));
        Assert.Contains("\"Small\"", why);
        Assert.False(SizeSetRefit.ClothCollides([("Small", s, s), ("Medium", m, m), ("Large", null, l)], out why));
        Assert.Equal("", why);
        // Two options the author named one size are no collision of the cloth.
        Assert.False(SizeSetRefit.ClothCollides([("Small", null, s), ("Small Tummy", null, s)], out _));
    }

    [Fact]
    [Trait("Plan", "EDGE-made-on-skinless-boots-14")]
    public void Options_sharing_a_file_are_placed_by_their_own_names() // [EDGE-made-on-skinless-boots-14]
    {
        const string File = "boot size/all/c0201e0284_sho.mdl";
        Assert.NotEqual(SizeSetRefit.EndSourceKey(File, "Small", true), SizeSetRefit.EndSourceKey(File, "Medium", true));
        Assert.Equal(SizeSetRefit.EndSourceKey(File, "Small", false), SizeSetRefit.EndSourceKey(File, "Medium", false));
        Assert.Equal(SizeSetRefit.EndSourceKey(File, "Small", false), SizeSetRefit.EndSourceKey(File.ToUpperInvariant(), null, false));
        var bibo = Bibo();
        Assert.NotEqual(Place(null, "Small", bibo).On, Place(null, "Medium", bibo).On);
    }

    [Fact]
    [Trait("Plan", "EDGE-made-on-skinless-boots-7")]
    [Trait("Plan", "EDGE-made-on-skinless-boots-8")]
    [Trait("Plan", "EDGE-made-on-skinless-boots-9")]
    [Trait("Plan", "EDGE-made-on-skinless-boots-10")]
    [Trait("Plan", "AC-made-on-skinless-boots-7")]
    public void Only_a_skinless_shoe_with_an_installed_made_on_body_reads_it() // [EDGE-made-on-skinless-boots-7] [EDGE-made-on-skinless-boots-8] [EDGE-made-on-skinless-boots-9] [EDGE-made-on-skinless-boots-10] [AC-made-on-skinless-boots-7]
    {
        var bibo = Bibo();
        SizeSetRefit.Request With(string? madeOn) => new("", ["bibo"], false, false, [bibo], MadeOn: madeOn);
        Assert.Same(bibo, SizeSetRefit.EndMadeOnBody(With("Bibo+"), "_sho", "0201", KeepEnds.Route.NoSkin));
        Assert.Same(bibo, SizeSetRefit.EndMadeOnBody(With("BIBO+"), "_sho", "0201", KeepEnds.Route.NoSkin));
        Assert.Null(SizeSetRefit.EndMadeOnBody(With(null), "_sho", "0201", KeepEnds.Route.NoSkin));
        Assert.Null(SizeSetRefit.EndMadeOnBody(With("Rue+"), "_sho", "0201", KeepEnds.Route.NoSkin));
        Assert.Null(SizeSetRefit.EndMadeOnBody(With("Bibo+"), "_glv", "0201", KeepEnds.Route.NoSkin));
        Assert.Null(SizeSetRefit.EndMadeOnBody(With("Bibo+"), "_sho", "0101", KeepEnds.Route.NoSkin));
        foreach (var route in Enum.GetValues<KeepEnds.Route>().Where(r => r != KeepEnds.Route.NoSkin))
            Assert.Null(SizeSetRefit.EndMadeOnBody(With("Bibo+"), "_sho", "0201", route));
    }

    [Fact]
    [Trait("Plan", "EDGE-made-on-skinless-boots-11")]
    [Trait("Plan", "AC-made-on-skinless-boots-7")]
    public void Cloth_beats_an_earlier_name_and_reading_stops_at_the_first_read() // [EDGE-made-on-skinless-boots-11] [AC-made-on-skinless-boots-7]
    {
        (int At, int Calls) Primary(params Kind[] kinds)
        {
            int calls = 0;
            int at = SizeSetRefit.PrimaryOf(kinds.Length, i => { calls++; return kinds[i]; });
            return (at, calls);
        }
        Assert.Equal(1, Primary(Kind.Name, Kind.Read).At);
        Assert.Equal(0, Primary(Kind.Name, Kind.None).At);
        Assert.Equal(1, Primary(Kind.None, Kind.Name, Kind.Name).At);
        Assert.Equal(-1, Primary(Kind.None, Kind.None).At);
        Assert.Equal((0, 1), Primary(Kind.Read, Kind.Read));
        Assert.Equal((1, 2), Primary(Kind.None, Kind.Read, Kind.Read));
    }

    [Fact]
    [Trait("Plan", "AC-made-on-skinless-boots-5")]
    public void The_made_on_bodys_own_sizes_are_the_authors() // [AC-made-on-skinless-boots-5]
    {
        var bibo = Bibo();
        var placed = new[] { "Small", "Medium", "Large" }.Select(o => (Option: (string?)o, Place(null, o, bibo).Own)).ToList();
        Assert.Equal("Medium", SizeSetRefit.TrustedOwn(placed, "bibo", LegsOf(bibo, "M").Rel));
        Assert.Equal("Large", SizeSetRefit.TrustedOwn(placed, "bibo", LegsOf(bibo, "L").Rel));
        Assert.Null(SizeSetRefit.TrustedOwn(placed, "rue", LegsOf(bibo, "M").Rel));
        // By cloth, the same.
        var byCloth = Place(Cloth(LegsOf(bibo, "M"), Confidence.Likely), "Small", bibo).Own;
        Assert.Equal("Small", SizeSetRefit.TrustedOwn([("Small", byCloth)], "bibo", LegsOf(bibo, "M").Rel));
    }

    // ── what is said ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-made-on-skinless-boots-3")]
    [Trait("Plan", "AC-made-on-skinless-boots-4")]
    [Trait("Plan", "EDGE-made-on-skinless-boots-15")]
    public void Check_in_game_says_how_the_size_was_placed() // [AC-made-on-skinless-boots-3] [AC-made-on-skinless-boots-4] [EDGE-made-on-skinless-boots-15]
    {
        const string Boot = "Feet — e0284";
        var on = LegsOf(Bibo(), "S");
        var s = Strings.SizeSet;
        string trusted = string.Format(s.MadeOnTrustedFmt, Boot, "Bibo+");
        string Gap(float mm) => string.Format(s.EndClothGapFmt, Boot, "Small", mm, "Bibo+");
        SizeSetRefit.EndMadeOnRead Read(How how, (int, int, float)? cloth)
            => new(how, how == How.None ? null : on, Confidence.NoBodyMesh, cloth, null,
                   how == How.Name ? Fit.Trusted : Fit.Taken, "");
        IReadOnlyList<string> Said(How how, (int, int, float)? cloth)
            => SizeSetRefit.EndMadeOnWarnings(Boot, "Small", Read(how, cloth), "Bibo+");

        Assert.Equal([trusted], Said(How.Name, (300, 0, 0.002f)));
        Assert.Equal([trusted, Gap(2f)], Said(How.Name, (300, 10, 0.002f)));   // 10 > 300 × 2% inside
        Assert.Equal([trusted, Gap(6f)], Said(How.Name, (300, 0, 0.006f)));
        Assert.Equal([trusted], Said(How.Name, (50, 0, 0.02f)));                // too little near to say
        Assert.Equal([trusted], Said(How.Name, null));
        Assert.Equal([Gap(6f)], Said(How.Cloth, (300, 0, 0.006f)));
        Assert.Empty(Said(How.Cloth, (300, 0, 0.005f)));
        Assert.Empty(Said(How.Cloth, (300, 10, 0.002f)));                       // a clear cloth reading clears the size
        Assert.Empty(Said(How.None, (300, 0, 0.02f)));

        var log = new SizeSetRefit.RunLog();
        foreach (string w in Said(How.Name, (300, 0, 0.006f)).Concat(Said(How.Name, (300, 0, 0.006f))))
            SizeSetRefit.AddWarningOnce(log, w);
        Assert.Equal([trusted, Gap(6f)], log.Warnings);
    }

    [Fact]
    [Trait("Plan", "AC-made-on-skinless-boots-6")]
    [Trait("Plan", "EDGE-made-on-skinless-boots-5")]
    [Trait("Plan", "EDGE-made-on-skinless-boots-6")]
    [Trait("Plan", "EDGE-made-on-skinless-boots-17")]
    public void A_shoe_placed_nowhere_says_why() // [AC-made-on-skinless-boots-6] [EDGE-made-on-skinless-boots-5] [EDGE-made-on-skinless-boots-6] [EDGE-made-on-skinless-boots-17]
    {
        const string Boot = "Feet — e0284";
        var s = Strings.SizeSet;
        Assert.Equal((string.Format(s.MadeOnNoSizeNameFmt, Boot, "Bibo+"), Codes.NotMadeOn),
                     SizeSetRefit.EndsUnplaced(Boot, true, "Bibo+", "Bibo+", [Fit.NoMatch, Fit.NoMatch]));
        Assert.Equal((string.Format(s.MadeOnNoSizeTableFmt, Boot, "Bibo+"), Codes.NotMadeOn),
                     SizeSetRefit.EndsUnplaced(Boot, true, "Bibo+", "Bibo+", [Fit.NoTable, Fit.NoTable]));
        Assert.Equal((string.Format(s.MadeOnNoSizeNameFmt, Boot, "Bibo+"), Codes.NotMadeOn),
                     SizeSetRefit.EndsUnplaced(Boot, true, "Bibo+", "Bibo+", [Fit.NoMatch, Fit.NoTable]));
        // Never tried on the "Made on" body: as before.
        Assert.Equal((string.Format(s.UnreadableFmt, Boot), Codes.Unreadable),
                     SizeSetRefit.EndsUnplaced(Boot, true, "Bibo+", "Bibo+", []));
        Assert.Equal((string.Format(s.EndsUnreadableFmt, Boot), Codes.EndsUnreadable),
                     SizeSetRefit.EndsUnplaced(Boot, false, "Bibo+", "Bibo+", []));
        Assert.Equal((string.Format(s.UnreadableFmt, Boot), Codes.Unreadable),
                     SizeSetRefit.EndsUnplaced(Boot, true, null, null, []));
    }

    [Fact]
    [Trait("Plan", "AC-made-on-skinless-boots-10")]
    [Trait("Plan", "EDGE-made-on-skinless-boots-21")]
    public void Join_stretch_counts_every_edge_from_held_to_moved() // [AC-made-on-skinless-boots-10] [EDGE-made-on-skinless-boots-21]
    {
        // A strip of two triangles: 0 and 1 held (the foot), 2 and 3 moved (the shaft).
        var before = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0) };
        int[][] strip = [[0, 1, 2, 1, 3, 2]];
        var held = new HashSet<int> { 0, 1 };

        var lifted = (Vector3[])before.Clone();
        lifted[2] += new Vector3(0, 0.003f, 0);
        var (edges, worst) = SizeSetRefit.JoinStretch(strip, held, v => before[v], v => lifted[v]);
        Assert.Equal(4, edges);   // 1-2, 2-0, 1-3, 2-1; never 0-1 (both held) or 3-2 (both moved)
        Assert.Equal(0.003f, worst, 1e-5f);

        var shrunk = (Vector3[])before.Clone();
        shrunk[2] = new Vector3(0, 0.5f, 0);
        Assert.Equal(0f, SizeSetRefit.JoinStretch(strip, held, v => before[v], v => shrunk[v]).Worst);

        // Moving a held-held or moved-moved pair together stretches nothing across the join.
        Assert.Equal(0, SizeSetRefit.JoinStretch([[0, 1, 0]], held, v => before[v], v => lifted[v]).Edges);
    }

    [Fact]
    [Trait("Plan", "AC-made-on-skinless-boots-8")]
    public void Cli_usage_says_a_skinless_shoe_is_read_on_made_on() // [AC-made-on-skinless-boots-8]
    {
        string usage = System.Text.RegularExpressions.Regex.Replace(RefitCli::Proteus.Refit.Program.Usage, @"\s+", " ");
        Assert.Contains("a skinless shoe's own sizes are read there by where its shaft sits on that body's legs, else by their "
                        + "option names", usage);
        Assert.Contains("without enough skin to tell its sizes by is placed on that body's size its option names give", usage);
        Assert.Contains("a skinless shoe's or glove's men's version is read there first", usage);
    }
}
