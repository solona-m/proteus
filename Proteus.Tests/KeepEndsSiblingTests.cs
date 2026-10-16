using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;

namespace Proteus.Tests;

/// <summary>
/// A glove or shoe sized by the chest or legs piece its author ships in the same option (<see cref="SiblingPiece"/>,
/// <see cref="KeepEnds.Route.Sibling"/>), what a refit records it was made from, and joining a shared size group's option
/// (<see cref="RefitCore.JoinOption"/>). Against hand-built models and mods; Geometric itself is
/// <see cref="KeepEndsSiblingLocalTests"/>.
/// </summary>
public class KeepEndsSiblingTests
{
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";
    private const string Cloth = "/mt_c0201e6070_sho_a.mtrl";
    private static readonly (string, float)[] Foot = [("j_asi_d_l", 1f)];
    private static readonly (string, float)[] Calf = [("j_asi_c_l", 1f)];

    private static readonly BodyOption LegsS = new("Legs", "", "S", "s.mdl", "chara/equipment/e0000/model/c0201e0000_dwn.mdl", "_dwn");
    private static readonly BodyOption LegsM = new("Legs", "", "M", "m.mdl", "chara/equipment/e0000/model/c0201e0000_dwn.mdl", "_dwn");
    private static readonly BodyOption FeetM = new("Feet", "", "M", "fm.mdl", "chara/equipment/e0000/model/c0201e0000_sho.mdl", "_sho");
    private static readonly BodySizeCatalog Body = new("<body>", [LegsS, LegsM, FeetM]);
    private static readonly IReadOnlySet<string> NoBones = new HashSet<string>();

    private static AutoRefitDecisions.Pick Pick(BodyOption option, Confidence confidence = Confidence.Exact,
                                                string dir = "body")
        => new(dir, option, confidence, false);

    /// <summary>A shoe whose leg carries skin, or only cloth (the shape of Geometric's jacket sleeve).</summary>
    private static (KeepEnds.Reading Reading, ModelParts Parts) ReadShoe(bool legSkin)
    {
        byte[] bytes = legSkin
            ? SyntheticModel.Build([], new SyntheticModel.Mesh(Skin,
                  new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: Foot),
                  new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 100f, Weights: Calf)))
            : SyntheticModel.Build([],
                  new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: Foot)),
                  new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 100f, Weights: Calf)));
        var parts = ModelPartReader.Read(bytes)!;
        return (KeepEnds.Read("_sho", parts, bytes)!, parts);
    }

    private static Func<ModelParts, string, AutoRefitDecisions.Pick?> Detect(AutoRefitDecisions.Pick? limb,
                                                                             AutoRefitDecisions.Pick? own)
        => (_, slot) => slot == "_dwn" ? limb : own;

    /// <summary>A cloth reading of <paramref name="near"/> points near the size, <paramref name="inside"/> of them inside.</summary>
    private static Func<ModelParts, BodyOption, BodySizeCatalog, (int, int, float)?> On(int near, int inside,
                                                                                     List<BodyOption>? asked = null)
        => (_, option, _) =>
        {
            asked?.Add(option);
            return (near, inside, 0.002f);
        };

    // ── the route ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-jacket-refit-1")]
    public void A_cloth_only_sleeve_is_sized_by_the_top_in_its_option()
    {
        // The arm has no skin; the cloth reading would pick the snuggest size it clears. The sibling's reading wins,
        // checked against that one size only.
        var (reading, parts) = ReadShoe(legSkin: false);
        var asked = new List<BodyOption>();
        var found = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, Pick(FeetM)), _ => Body,
                                  (_, _, _, _) => throw new InvalidOperationException("the cloth ranking was asked"),
                                  sibling: Pick(LegsM, Confidence.Likely), clothOn: On(400, 0, asked));

        Assert.Equal(KeepEnds.Route.Sibling, found.Route);
        Assert.Equal(LegsM, found.Pick!.Option);
        Assert.Equal(Confidence.Likely, found.Pick.Confidence);
        Assert.True(found.Pick.FromCloth);
        Assert.Equal([LegsM], asked);
        Assert.Equal("D", KeepEnds.Letter(found.Route));
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-1")]
    [Trait("Plan", "EDGE-jacket-refit-7")]
    public void A_sleeve_with_skin_no_body_matched_takes_the_sibling()
    {
        // Geometric: its arm skin matched no installed body (detect gives nothing), so the top beside it decides — its
        // skin lying on that size.
        var (reading, parts) = ReadShoe(legSkin: true);
        var found = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, Pick(FeetM)), _ => Body,
                                  (_, options, _, skin) => skin
                                      ? new BodySizeMatch.Ranking(Confidence.Likely, [new BodySizeMatch.Score(options[0], 0.6f, 0.002f)])
                                      : null,
                                  sibling: Pick(LegsM), clothOn: On(0, 0));

        Assert.Equal(KeepEnds.Route.Sibling, found.Route);
        Assert.False(found.Pick!.FromCloth);
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-13")]
    public void Skin_too_little_to_read_but_cloth_on_the_size_is_sized_by_the_sibling_through_its_cloth()
    {
        // Skin present but not enough to rank on the size, while the cloth over it is near and clear: the sibling is
        // taken, and its same-shape check reads the cloth.
        var (reading, parts) = ReadShoe(legSkin: true);
        var tooLittle = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, Pick(FeetM)), _ => Body,
                                      (_, options, _, skin) => skin
                                          ? new BodySizeMatch.Ranking(Confidence.TooLittle, [new BodySizeMatch.Score(options[0], 0f, 0.03f)])
                                          : null,
                                      sibling: Pick(LegsM), clothOn: On(400, 0));
        Assert.Equal(KeepEnds.Route.Sibling, tooLittle.Route);
        Assert.True(tooLittle.Pick!.FromCloth);

        // A skin ranking that itself fell back to the cloth is not skin evidence either.
        var byCloth = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, Pick(FeetM)), _ => Body,
                                    (_, options, _, skin) => skin
                                        ? new BodySizeMatch.Ranking(Confidence.Likely, [new BodySizeMatch.Score(options[0], 0f, 0.002f)],
                                                                    FromCloth: true)
                                        : null,
                                    sibling: Pick(LegsM), clothOn: On(400, 0));
        Assert.Equal(KeepEnds.Route.Sibling, byCloth.Route);
        Assert.True(byCloth.Pick!.FromCloth);
        var none = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, Pick(FeetM)), _ => Body,
                                 (_, options, _, skin) => skin
                                     ? new BodySizeMatch.Ranking(Confidence.Likely, [new BodySizeMatch.Score(options[0], 0f, 0.002f)],
                                                                 FromCloth: true)
                                     : null,
                                 sibling: Pick(LegsM), clothOn: On(12, 0));
        Assert.NotEqual(KeepEnds.Route.Sibling, none.Route);   // read as before: here by its cloth (route B)
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-7")]
    [Trait("Plan", "EDGE-jacket-refit-35")]
    public void A_shoe_whose_few_shin_vertices_reach_no_legs_is_not_sized_by_them()
    {
        // A feet model runs a little up the shin: too little skin there to be evidence of any legs, and no cloth over
        // them. It keeps its own shape (skin-only), however the legs beside it in its option read.
        var (reading, parts) = ReadShoe(legSkin: true);
        var notes = new List<string>();
        var found = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, Pick(FeetM)), _ => Body,
                                  (_, options, _, skin) => skin
                                      ? new BodySizeMatch.Ranking(Confidence.TooLittle, [new BodySizeMatch.Score(options[0], 0f, 0.03f)])
                                      : null,
                                  sibling: Pick(LegsM), clothOn: On(0, 0), note: notes.Add);
        Assert.Equal(KeepEnds.Route.SkinOnly, found.Route);
        Assert.Contains("neither its skin nor its cloth reaches", notes.Single());
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-2")]
    public void A_confident_skin_reading_wins_over_the_sibling()
    {
        var (reading, parts) = ReadShoe(legSkin: true);
        var notes = new List<string>();
        var found = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(Pick(LegsM), null), _ => Body,
                                  sibling: Pick(LegsS), clothOn: On(400, 0), note: notes.Add);
        Assert.Equal(KeepEnds.Route.Skin, found.Route);
        Assert.Equal(LegsM, found.Pick!.Option);
        Assert.Empty(notes);   // never weighed, so never said to be turned down

        // And an unclear skin reading stays unclear: the sibling never overrides the leg's own skin.
        var unsure = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(Pick(LegsM, Confidence.Ambiguous), null),
                                   _ => Body, sibling: Pick(LegsS), clothOn: On(400, 0));
        Assert.Equal(KeepEnds.Route.Unsure, unsure.Route);
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-2")]
    [Trait("Plan", "EDGE-jacket-refit-6")]
    public void An_unclear_sibling_falls_back_to_the_cloth_reading()
    {
        var (reading, parts) = ReadShoe(legSkin: false);
        var found = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, Pick(FeetM)), _ => Body,
                                  (_, _, _, _) => new BodySizeMatch.Ranking(Confidence.Likely,
                                      [new BodySizeMatch.Score(LegsS, 0f, 0.002f)], FromCloth: true),
                                  sibling: Pick(LegsM, Confidence.Ambiguous), clothOn: On(400, 0));
        Assert.Equal(KeepEnds.Route.Cloth, found.Route);
        Assert.Equal(LegsS, found.Pick!.Option);
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-2")]
    public void Cloth_sitting_inside_the_siblings_size_is_not_sized_by_it()
    {
        // More than 2% of the cloth near the size inside it: not cloth made on that size. An author's tucks — under 1%,
        // inside every size alike — are not.
        var (reading, parts) = ReadShoe(legSkin: false);
        var notes = new List<string>();
        var found = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, Pick(FeetM)), _ => Body,
                                  (_, _, _, _) => null, sibling: Pick(LegsM), clothOn: On(400, 12), note: notes.Add);
        Assert.Equal(KeepEnds.Route.SkinOnly, found.Route);
        Assert.Equal("12 of 400 of its cloth points sit inside M", notes.Single());

        var tucked = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, Pick(FeetM)), _ => Body,
                                   (_, _, _, _) => null, sibling: Pick(LegsM), clothOn: On(400, 4));
        Assert.Equal(KeepEnds.Route.Sibling, tucked.Route);
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-7")]
    public void A_short_glove_whose_cloth_misses_the_sibling_body_is_skin_only()
    {
        var (reading, parts) = ReadShoe(legSkin: false);
        var found = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, Pick(FeetM)), _ => Body,
                                  (_, _, _, _) => null, sibling: Pick(LegsM), clothOn: On(12, 0));
        Assert.Equal(KeepEnds.Route.SkinOnly, found.Route);
        Assert.Equal(FeetM, found.Pick!.Option);
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-2")]
    [Trait("Plan", "EDGE-jacket-refit-30")]
    public void A_sibling_size_the_body_no_longer_has_is_ignored()
    {
        var (reading, parts) = ReadShoe(legSkin: false);
        var gone = new BodyOption("Legs", "", "XL", "xl.mdl", LegsM.GamePath, "_dwn");
        var found = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, Pick(FeetM)), _ => Body,
                                  (_, _, _, _) => null, sibling: Pick(gone), clothOn: On(400, 0));
        Assert.NotEqual(KeepEnds.Route.Sibling, found.Route);

        // Cached, a sibling reading comes back in the limb's slot — and not at all once that size is gone.
        var cached = KeepEnds.ToCache(new KeepEnds.Found(KeepEnds.Route.Sibling, Pick(LegsM)));
        var back = KeepEnds.FromCache(cached, reading, "0201", _ => Body)!;
        Assert.Equal(KeepEnds.Route.Sibling, back.Route);
        Assert.Equal(LegsM, back.Pick!.Option);
        Assert.Null(KeepEnds.FromCache(cached, reading, "0201", _ => new BodySizeCatalog("<body>", [LegsS, FeetM])));
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-8")]
    public void A_sibling_already_at_the_target_size_already_fits()
    {
        var (reading, _) = ReadShoe(legSkin: false);
        var decision = KeepEnds.Decide(new KeepEnds.Found(KeepEnds.Route.Sibling, Pick(LegsM)), reading, NoBones, "0201",
                                       "body", _ => Body, _ => LegsM);
        Assert.IsType<KeepEnds.AlreadyFits>(decision);
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-2")]
    [Trait("Plan", "EDGE-jacket-refit-1")]
    public void A_piece_with_a_sibling_reads_under_a_key_of_its_own()
    {
        // A piece read by its cloth before it had a sibling to go by must not be served that reading again; one whose
        // sibling the author changed is read again; one put on again is not, without its siblings being opened.
        Assert.Equal("", SiblingPiece.CacheSuffix([], _ => throw new InvalidOperationException("stamped")));
        SiblingPiece.Candidate[] medium = [new(Top, "Chest Size\\Medium\\top.mdl", "Chest Size / Medium")];
        Assert.Equal("|sib2=chest size/medium/top.mdl@100:7", SiblingPiece.CacheSuffix(medium, _ => "100:7"));
        Assert.NotEqual(SiblingPiece.CacheSuffix(medium, _ => "100:7"), SiblingPiece.CacheSuffix(medium, _ => "100:8"));
        SiblingPiece.Candidate[] small = [new(Top, "Chest Size\\Small\\top.mdl", "Chest Size / Small")];
        Assert.NotEqual(SiblingPiece.CacheSuffix(medium, _ => "1"), SiblingPiece.CacheSuffix(small, _ => "1"));

        Assert.True(SiblingPiece.Applies("C:/mods/x", 0));
        Assert.False(SiblingPiece.Applies("C:/mods/x", 201));   // baked for another race
        Assert.False(SiblingPiece.Applies(null, 0));            // the game's own gear
    }

    // ── finding the sibling ──────────────────────────────────────────────────────────────────────────────

    private const string Top = "chara/equipment/e6010/model/c0201e6010_top.mdl";
    private const string Glove = "chara/equipment/e0068/model/c0201e0068_glv.mdl";
    private const string Shoe = "chara/equipment/e0362/model/c0201e0362_sho.mdl";
    private const string Legs = "chara/equipment/e6010/model/c0201e6010_dwn.mdl";

    private static PenumbraModMeta.Redirect R(string gamePath, string file, string source) => new(gamePath, file, source);

    /// <summary>Geometric's "Chest Size" group as it is on disk: three sizes each serving the top and the jacket (its
    /// gloves), and Proteus's refit option serving both from "Body Retarget".</summary>
    private static List<PenumbraModMeta.Redirect> Geometric() =>
    [
        R(Glove, "chest size\\small\\glv.mdl", "Chest Size / Small"),
        R(Top, "chest size\\small\\top.mdl", "Chest Size / Small"),
        R(Glove, "chest size\\medium\\glv.mdl", "Chest Size / Medium"),
        R(Top, "chest size\\medium\\top.mdl", "Chest Size / Medium"),
        R(Glove, "chest size\\large\\glv.mdl", "Chest Size / Large"),
        R(Top, "chest size\\large\\top.mdl", "Chest Size / Large"),
        R(Glove, "body retarget\\neo xs\\glv.mdl", "Chest Size / Neo — XS"),
        R(Top, "body retarget\\neo xs\\top.mdl", "Chest Size / Neo — XS"),
        R("chara/equipment/e0068/model/c0101e0068_glv.mdl", "male\\glv.mdl", "Gryphonskin Gloves / Him"),
    ];

    private static BodyRetargetWriter.Record RecordOf(params (string Group, string Name, string GamePath)[] entries)
        => new()
        {
            Options = entries.Select(e => new BodyRetargetWriter.Entry
            {
                Group = e.Group, InAuthorGroup = true, Name = e.Name, GamePath = e.GamePath, Auto = true,
            }).ToList(),
        };

    [Fact]
    [Trait("Plan", "AC-jacket-refit-1")]
    public void Geometrics_jacket_finds_the_top_of_its_own_size()
    {
        var found = SiblingPiece.Find(Geometric(), null, "chest size/medium/glv.mdl", "_top", "0201");
        var top = Assert.Single(found);
        Assert.Equal("chest size\\medium\\top.mdl", top.File);
        Assert.Equal("Chest Size / Medium", top.Source);
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-4")]
    public void Proteus_refit_options_are_never_the_authors()
    {
        // The refit option copies the jacket's author file in beside the refit top, as Scarlet's does: that option is
        // Proteus's and is ignored; and a refit file is never a sibling even under an author's name.
        var redirects = Geometric();
        redirects.Add(R(Glove, "chest size\\medium\\glv.mdl", "Chest Size / Neo — XS + legs"));
        redirects.Add(R(Top, "body retarget\\neo xs legs\\top.mdl", "Chest Size / Neo — XS + legs"));
        var record = RecordOf(("Chest Size", "Neo — XS + legs", Top));
        var found = SiblingPiece.Find(redirects, record, "chest size/medium/glv.mdl", "_top", "0201");
        Assert.Equal("chest size\\medium\\top.mdl", Assert.Single(found).File);

        redirects.Add(R(Top, "Body Retarget\\stray\\top.mdl", "Chest Size / Medium"));
        Assert.Single(SiblingPiece.Find(redirects, record, "chest size/medium/glv.mdl", "_top", "0201"));
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-2")]
    public void The_mods_default_files_are_one_option()
    {
        var found = SiblingPiece.Find([R(Glove, "glv.mdl", ""), R(Top, "top.mdl", "")], null, "glv.mdl", "_top", "0201");
        Assert.Equal("top.mdl", Assert.Single(found).File);
        Assert.Equal("default files", SiblingPiece.Shown(found[0].Source));
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-3")]
    [Trait("Plan", "EDGE-jacket-refit-5")]
    public void Only_the_pieces_race_and_every_option_serving_the_piece()
    {
        // The man's top in the same option is another race's; the woman's glove served by two options brings both of
        // their tops (Scarlet's S and M share one jacket file).
        List<PenumbraModMeta.Redirect> redirects =
        [
            R(Glove, "shared\\glv.mdl", "Size / S"),
            R(Top, "s\\top.mdl", "Size / S"),
            R("chara/equipment/e6010/model/c0101e6010_top.mdl", "s\\top_m.mdl", "Size / S"),
            R(Glove, "shared\\glv.mdl", "Size / M"),
            R(Top, "m\\top.mdl", "Size / M"),
        ];
        var found = SiblingPiece.Find(redirects, null, "shared/glv.mdl", "_top", "0201");
        Assert.Equal(["s\\top.mdl", "m\\top.mdl"], found.Select(c => c.File));
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-5")]
    public void Siblings_must_agree_and_reading_stops_at_the_first_that_does_not()
    {
        var candidates = new List<SiblingPiece.Candidate>
        {
            new(Top, "s.mdl", "Size / S"), new(Top, "m.mdl", "Size / M"), new(Top, "l.mdl", "Size / L"),
        };
        int read = 0;
        var notes = new List<string>();
        var disagree = SiblingPiece.Agree(candidates, c =>
        {
            read++;
            return c.File == "s.mdl" ? Pick(LegsS) : Pick(LegsM);
        }, notes.Add);
        Assert.Null(disagree);
        Assert.Equal(2, read);
        Assert.Contains("disagree", notes.Single());

        var agree = SiblingPiece.Agree(candidates, _ => Pick(LegsM));
        Assert.Equal(LegsM, agree!.Option);

        Assert.Null(SiblingPiece.Agree(candidates, _ => Pick(LegsM, Confidence.Ambiguous)));
        Assert.Null(SiblingPiece.Agree(candidates, _ => null));
        Assert.Null(SiblingPiece.Agree([], _ => Pick(LegsM)));
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-5")]
    [Trait("Plan", "EDGE-jacket-refit-33")]
    public void A_file_every_size_shares_is_not_read_at_all()
    {
        var redirects = new List<PenumbraModMeta.Redirect>();
        foreach (string size in new[] { "XS", "S", "M", "L" })
        {
            redirects.Add(R(Glove, "common\\glv.mdl", "Size / " + size));
            redirects.Add(R(Top, size + "\\top.mdl", "Size / " + size));
        }
        var notes = new List<string>();
        Assert.Empty(SiblingPiece.Find(redirects, null, "common/glv.mdl", "_top", "0201", notes.Add));
        Assert.Contains("more than 3", notes.Single());

        // Three are still read.
        Assert.Equal(SiblingPiece.MostRead,
                     SiblingPiece.Find(redirects.Take(6).ToList(), null, "common/glv.mdl", "_top", "0201").Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-6")]
    public void A_sibling_that_cannot_be_read_is_noted_and_skipped()
    {
        string root = NewMod();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "garbage.mdl"), [1, 2, 3, 4, 5, 6, 7, 8]);
            File.WriteAllBytes(Path.Combine(root, "top.mdl"), Model);
            var notes = new List<string>();

            Assert.Null(SiblingPiece.TryOpen(root, "missing.mdl", notes.Add));
            Assert.Null(SiblingPiece.TryOpen(root, "garbage.mdl", notes.Add));
            Assert.Equal(2, notes.Count);
            Assert.StartsWith("missing.mdl could not be read", notes[0]);
            Assert.StartsWith("garbage.mdl could not be read", notes[1]);

            var opened = SiblingPiece.TryOpen(root, "top.mdl", notes.Add);
            Assert.NotNull(opened);
            Assert.Equal(Model, opened!.Value.Bytes);
            Assert.Equal(2, notes.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-1")]
    [Trait("Plan", "EDGE-jacket-refit-10")]
    public void A_shoe_finds_the_legs_in_its_option()
    {
        List<PenumbraModMeta.Redirect> redirects =
        [
            R(Shoe, "m\\sho.mdl", "Bottom Size / M"), R(Legs, "m\\dwn.mdl", "Bottom Size / M"),
            R(Top, "m\\top.mdl", "Bottom Size / M"),
        ];
        Assert.Equal("m\\dwn.mdl", Assert.Single(SiblingPiece.Find(redirects, null, "m/sho.mdl", "_dwn", "0201")).File);
        // The game's own gear has no options at all.
        Assert.Empty(SiblingPiece.Find([], null, "m/sho.mdl", "_dwn", "0201"));
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-1")]
    [Trait("Plan", "EDGE-jacket-refit-32")]
    public void Only_the_same_option_of_a_multi_group_is_a_sibling()
    {
        List<PenumbraModMeta.Redirect> together =
            [R(Glove, "glv.mdl", "Items / Everything"), R(Top, "top.mdl", "Items / Everything")];
        Assert.Single(SiblingPiece.Find(together, null, "glv.mdl", "_top", "0201"));

        List<PenumbraModMeta.Redirect> apart = [R(Glove, "glv.mdl", "Items / Jacket"), R(Top, "top.mdl", "Items / Top")];
        Assert.Empty(SiblingPiece.Find(apart, null, "glv.mdl", "_top", "0201"));
    }

    // ── one reading for the top and the glove ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-jacket-refit-3")]
    [Trait("Plan", "EDGE-jacket-refit-27")]
    public void The_sibling_is_read_under_the_top_jobs_own_key_with_its_other_parts()
    {
        // The glove reads its sibling in the slot and race the top's own refit reads it in: the slot the walk files the
        // top under, and the top's race — so both land on one key.
        var sibling = Assert.Single(SiblingPiece.Find(Geometric(), null, "chest size/medium/glv.mdl", "_top", "0201"));
        var walked = Assert.Single(DrawnModelPaths.EquippedPartModelsFromModels([sibling.GamePath]));
        Assert.Equal(("_" + walked.Key, "0201"), AutoRefitWatcher.ReadAs(sibling.GamePath));
        Assert.Equal(("_glv", "0201"), AutoRefitWatcher.ReadAs(Glove));
        Assert.Equal((null, "0101"), AutoRefitWatcher.ReadAs("chara/equipment/e0000/model/c0101e0000_top.mdl"));

        byte[] top = [1, 2, 3, 4];
        Assert.Equal(DetectionCache.KeyOf(top, "_top", "0201", "neo", "v1"),
                     AutoRefitWatcher.SourceCacheKey(top, "_top", "0201", "neo", "v1"));
        Assert.NotEqual(AutoRefitWatcher.SourceCacheKey(top, "_top", "0201", "neo", "v1"),
                        AutoRefitWatcher.SourceCacheKey(top, "_glv", "0201", "neo", "v1"));

        // The other parts a top reaches come out the same whoever reads it: a dress's legs beside its chest.
        var chestM = new BodyOption("Chest", "", "M", "cm.mdl", Top, "_top");
        var body = new BodySizeCatalog("<body>", [chestM, LegsS, LegsM]);
        var garment = ModelPartReader.Read(SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0))))!;
        BodySizeMatch.Ranking Rank(string slot, IReadOnlyList<BodyOption> options)
            => new(Confidence.Likely, [new BodySizeMatch.Score(options.Last(), 0f, 0.001f)]);
        var others = AutoRefitWatcher.OtherParts(garment, "_top", "0201", Pick(chestM), body, Rank);
        Assert.Equal(new Dictionary<string, string> { ["_dwn"] = "m.mdl" }, others);
        Assert.Equal(others, AutoRefitWatcher.OtherParts(garment, "_top", "0201", Pick(chestM), body, Rank));
    }

    // ── what a refit was made from ───────────────────────────────────────────────────────────────────────

    private static string NewMod()
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-sibling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), PenumbraModMeta.NewMetaJson("Geometric", "Someone", ""));
        return root;
    }

    private static readonly byte[] Model = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0)));

    private static BodyRetargetWriter.Provenance Made(string dir, string rel, string slot = "_top", string to = "xs.mdl")
        => new(dir, rel, new Dictionary<string, string> { [slot] = to });

    [Fact]
    [Trait("Plan", "AC-jacket-refit-4")]
    [Trait("Plan", "EDGE-jacket-refit-9")]
    public void A_refit_recorded_from_another_source_is_not_put_back()
    {
        string root = NewMod();
        try
        {
            Assert.True(BodyRetargetWriter.Save(root, "Size", Glove, "neo", "[IVCS] Bibo+ — Smallclothes",
                                                [new BodyRetargetWriter.Refit("Neo — XS", Model, "XS")], auto: true,
                                                provenance: Made("[IVCS] Bibo+", "smallclothes.mdl")).Ok);
            var entry = BodyRetargetWriter.ReadRecord(root)!.Options.Single();
            Assert.Equal("[IVCS] Bibo+", entry.SourceDir);
            Assert.Equal("smallclothes.mdl", entry.SourceRel);
            Assert.Equal("xs.mdl", entry.Targets!["_top"]);

            Assert.False(AutoRefitWatcher.Existing(root, "Size", "Neo — XS", Glove, ("Bibo+", "nude/medium.mdl")));
            Assert.False(AutoRefitWatcher.Existing(root, "Size", "Neo — XS", Glove, ("[IVCS] Bibo+", "other.mdl")));
            // A fork: the same file under another body mod's folder is another source.
            Assert.False(AutoRefitWatcher.Existing(root, "Size", "Neo — XS", Glove, ("Bibo+", "smallclothes.mdl")));
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Neo — XS", Glove, ("[ivcs] bibo+", "SmallClothes.mdl")));
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Neo — XS", Glove));

            // An entry from before sources were recorded is put back, as it always was.
            Assert.True(BodyRetargetWriter.Save(root, "Size", Top, "neo", "Bibo+ — M",
                                                [new BodyRetargetWriter.Refit("Neo — XS", Model, "XS")], auto: true).Ok);
            Assert.Null(BodyRetargetWriter.ReadRecord(root)!.Options.Single(e => e.GamePath == Top).SourceDir);
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Neo — XS", Top, ("Bibo+", "anything.mdl")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-4")]
    [Trait("Plan", "EDGE-jacket-refit-9")]
    public void Remaking_the_glove_replaces_its_file_and_entry_and_keeps_the_tops()
    {
        string root = NewMod();
        try
        {
            var top = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, OffsetX: 5f)));
            var wrong = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, OffsetX: 7f)));
            var right = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, OffsetX: 9f)));
            Assert.True(BodyRetargetWriter.Save(root, "Size", Top, "neo", "Bibo+ — M",
                                                [new BodyRetargetWriter.Refit("Neo — XS", top, "XS")], auto: true,
                                                provenance: Made("Bibo+", "m.mdl")).Ok);
            Assert.True(BodyRetargetWriter.Save(root, "Size", Glove, "neo", "[IVCS] Bibo+ — Smallclothes",
                                                [new BodyRetargetWriter.Refit("Neo — XS", wrong, "XS")], auto: true,
                                                provenance: Made("[IVCS] Bibo+", "sc.mdl")).Ok);
            Assert.True(BodyRetargetWriter.Save(root, "Size", Glove, "neo", "Bibo+ — M",
                                                [new BodyRetargetWriter.Refit("Neo — XS", right, "XS")], auto: true,
                                                provenance: Made("Bibo+", "m.mdl")).Ok);

            var option = PenumbraModMeta.TryReadFileOptions(root, "Size")!.Single(o => o.Name == "Neo — XS");
            Assert.Equal(2, option.Files!.Count);
            Assert.Equal(right, File.ReadAllBytes(Path.Combine(root, option.Files[Glove])));
            Assert.Equal(top, File.ReadAllBytes(Path.Combine(root, option.Files[Top])));
            var entries = BodyRetargetWriter.ReadRecord(root)!.Options;
            Assert.Equal(2, entries.Count);
            var glove = entries.Single(e => e.GamePath == Glove);
            Assert.Equal("Bibo+", glove.SourceDir);
            Assert.Equal("Bibo+ — M", glove.From);
            Assert.Equal("Bibo+", entries.Single(e => e.GamePath == Top).SourceDir);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── a size group shared by several pieces ────────────────────────────────────────────────────────────

    private static BodyRetargetWriter.Entry Entry(string name, string gamePath, Dictionary<string, string>? targets,
                                                  bool auto = true, string bodyMod = "neo")
        => new()
        {
            Group = "Chest Size", InAuthorGroup = true, Name = name, GamePath = gamePath, BodyMod = bodyMod, Auto = auto,
            Targets = targets,
        };

    private static readonly Dictionary<string, string> Chosen = new() { ["_top"] = "xs.mdl", ["_dwn"] = "m.mdl" };
    private static readonly Dictionary<string, string> GloveOnto = new() { ["_top"] = "xs.mdl" };

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-24")]
    public void A_glove_joins_the_tops_automatic_option_onto_the_same_sizes()
    {
        var record = new BodyRetargetWriter.Record
        {
            Options = [Entry("Neo — XS + M", Top, new() { ["_top"] = "xs.mdl", ["_dwn"] = "m.mdl" })],
        };
        Assert.Equal("Neo — XS + M", RefitCore.JoinOption(record, "Chest Size", Glove, "neo", Chosen, GloveOnto, "Neo — XS"));
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-24")]
    public void An_option_made_onto_other_sizes_or_by_hand_is_not_joined()
    {
        // Saved while legs S was chosen; legs M is chosen now.
        var stale = new BodyRetargetWriter.Record
        {
            Options = [Entry("Neo — XS + S", Top, new() { ["_top"] = "xs.mdl", ["_dwn"] = "s.mdl" })],
        };
        Assert.Equal("Neo — XS", RefitCore.JoinOption(stale, "Chest Size", Glove, "neo", Chosen, GloveOnto, "Neo — XS"));

        // A refit made by hand in the Studio: undoing the automatic one would take it too.
        var byHand = new BodyRetargetWriter.Record
        {
            Options = [Entry("Neo — XS + M", Top, new() { ["_top"] = "xs.mdl", ["_dwn"] = "m.mdl" }, auto: false)],
        };
        Assert.Equal("Neo — XS", RefitCore.JoinOption(byHand, "Chest Size", Glove, "neo", Chosen, GloveOnto, "Neo — XS"));

        // From before targets were recorded; for another body; already holding this piece; sharing no part with it.
        foreach (var e in new[]
                 {
                     Entry("A", Top, null), Entry("B", Top, new() { ["_top"] = "xs.mdl" }, bodyMod: "rue"),
                     Entry("C", Glove, new() { ["_top"] = "xs.mdl" }), Entry("D", Legs, new() { ["_dwn"] = "m.mdl" }),
                 })
            Assert.Equal("Neo — XS", RefitCore.JoinOption(new BodyRetargetWriter.Record { Options = [e] }, "Chest Size",
                                                          Glove, "neo", Chosen, GloveOnto, "Neo — XS"));

        // The same refit saved before keeps its own name.
        var same = new BodyRetargetWriter.Record
        {
            Options = [Entry("Neo — XS", Glove, GloveOnto), Entry("Neo — XS + M", Top, new() { ["_top"] = "xs.mdl" })],
        };
        Assert.Equal("Neo — XS", RefitCore.JoinOption(same, "Chest Size", Glove, "neo", Chosen, GloveOnto, "Neo — XS"));
    }

    [Fact]
    [Trait("Plan", "EDGE-jacket-refit-24")]
    public void A_group_is_shared_only_by_several_piece_models()
    {
        Assert.True(RefitCore.IsSharedSingleGroup(Geometric(), null, "Chest Size", ["Chest Size"]));
        Assert.False(RefitCore.IsSharedSingleGroup(Geometric(), null, "Chest Size", []));   // not single-choice

        // One model and its material: one piece.
        List<PenumbraModMeta.Redirect> onePiece =
        [
            R(Top, "s\\top.mdl", "Size / S"), R("chara/equipment/e6010/material/v0001/mt_c0201e6010_top_a.mtrl", "s\\a.mtrl", "Size / S"),
            R(Top, "m\\top.mdl", "Size / M"),
        ];
        Assert.False(RefitCore.IsSharedSingleGroup(onePiece, null, "Size", ["Size"]));

        // Proteus's own options do not make it shared.
        List<PenumbraModMeta.Redirect> ours = [R(Top, "s\\top.mdl", "Size / S"), R(Glove, "x\\glv.mdl", "Size / Neo — XS")];
        var record = new BodyRetargetWriter.Record
        {
            Options = [new BodyRetargetWriter.Entry { Group = "Size", Name = "Neo — XS", GamePath = Glove }],
        };
        Assert.False(RefitCore.IsSharedSingleGroup(ours, record, "Size", ["Size"]));
    }

    // ── the cloth measure ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A flat square <paramref name="half"/> m across each way at height <paramref name="z"/>, in
    /// <paramref name="n"/>×<paramref name="n"/> cells, facing +Z.</summary>
    private static (float, float, float)[] Sheet(float half, float z, int n)
    {
        var corners = new List<(float, float, float)>();
        float step = 2 * half / n;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                float x = -half + i * step, y = 1f - half + j * step;
                corners.AddRange([(x, y, z), (x + step, y, z), (x, y + step, z)]);
                corners.AddRange([(x + step, y, z), (x + step, y + step, z), (x, y + step, z)]);
            }
        return corners.ToArray();
    }

    [Fact]
    [Trait("Plan", "AC-jacket-refit-1")]
    public void Cloth_over_a_body_clears_it_and_cloth_through_it_does_not()
    {
        string dir = Path.Combine(Path.GetTempPath(), "proteus-cloth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string body = Path.Combine(dir, "body.mdl");
            File.WriteAllBytes(body, SyntheticModel.Build([], new SyntheticModel.Mesh(Skin,
                new SyntheticModel.Sub(0, Corners: Sheet(0.12f, 0f, 24)))));
            ModelParts Shell(float z) => ModelPartReader.Read(SyntheticModel.Build([], new SyntheticModel.Mesh(Cloth,
                new SyntheticModel.Sub(0, Corners: Sheet(0.1f, z, 14)))))!;

            var over = BodySizeMatch.ClothAgainst(Shell(0.005f), body)!.Value;
            Assert.True(over.Near >= BodySizeMatch.MinEvidence, $"only {over.Near} cloth points near the body");
            Assert.Equal(0, over.Inside);
            Assert.True(BodySizeMatch.ClothClears(Shell(0.005f), body));

            var through = BodySizeMatch.ClothAgainst(Shell(-0.005f), body)!.Value;
            Assert.Equal(through.Near, through.Inside);
            Assert.False(BodySizeMatch.ClothClears(Shell(-0.005f), body));

            // The ranking reads the same counts: the inside shell does not clear the body, the outside one does.
            var option = new BodyOption("Chest", "", "M", "body.mdl", Top, "_top");
            var ranking = BodySizeMatch.RankByCloth(Shell(0.005f), [option], _ => body)!;
            Assert.Equal(over.Gap, ranking.Scores.Single().Rms, 4);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
