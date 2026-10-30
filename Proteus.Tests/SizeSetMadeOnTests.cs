using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;
using Codes = Proteus.Services.SizeSetRefit.Codes;
using SizeStatus = Proteus.Services.SizeSetRefit.SizeStatus;

namespace Proteus.Tests;

/// <summary>
/// The size set's "Made on" body (<c>SizeSetRefit.Source.cs</c>) and the result line that says why nothing was added
/// (<see cref="SizeSetRefit.Summarize"/>): the rules, on hand-made readings and logs. The real runs are in
/// <see cref="SizeSetMadeOnLocalTests"/>.
/// </summary>
public class SizeSetMadeOnTests
{
    private const string Top = "chara/equipment/e0485/model/c0201e0485_top.mdl";
    private static readonly BodyOption NudeMedium = new("Breasts", "", "Nude - Medium", "nude - medium/top.mdl", Top, "_top");
    private static readonly BodyOption PerkyMedium = NudeMedium with { Name = "Perky - Medium", Rel = "perky - medium/top.mdl" };

    private static BodySizeMatch.Ranking Ranking(Confidence confidence, float rmsMm, bool fromCloth = false)
        => new(confidence, [new BodySizeMatch.Score(NudeMedium, 0f, rmsMm / 1000f),
                            new BodySizeMatch.Score(PerkyMedium, 0f, (rmsMm + 0.03f) / 1000f)], fromCloth);

    // ── reading on the "Made on" body ────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-size-set-made-on-7")]
    [Trait("Plan", "EDGE-size-set-made-on-2")]
    [Trait("Plan", "EDGE-size-set-made-on-3")]
    public void JudgeMadeOn_takes_and_refuses_as_measured() // [AC-size-set-made-on-7] [EDGE-size-set-made-on-2] [EDGE-size-set-made-on-3]
    {
        Assert.Equal(SizeSetRefit.MadeOnFit.Taken, SizeSetRefit.JudgeMadeOn(12, Ranking(Confidence.Guess, 3f), 0.004f));
        // Oblique's top Medium and Large: a tie between shapes of one size.
        Assert.Equal(SizeSetRefit.MadeOnFit.Taken, SizeSetRefit.JudgeMadeOn(12, Ranking(Confidence.Ambiguous, 2.67f), 0.004f));
        Assert.Equal(SizeSetRefit.MadeOnFit.Taken, SizeSetRefit.JudgeMadeOn(12, Ranking(Confidence.Ambiguous, 3.9f), 0.004f));
        // Its Medium Tummy: a size apart.
        Assert.Equal(SizeSetRefit.MadeOnFit.Unclear, SizeSetRefit.JudgeMadeOn(12, Ranking(Confidence.Ambiguous, 5.96f), 0.004f));
        Assert.Equal(SizeSetRefit.MadeOnFit.Unclear,
                     SizeSetRefit.JudgeMadeOn(12, Ranking(Confidence.Ambiguous, 1f, fromCloth: true), 0.004f));
        Assert.Equal(SizeSetRefit.MadeOnFit.NoEvidence, SizeSetRefit.JudgeMadeOn(12, Ranking(Confidence.TooLittle, 1f), 0.004f));
        Assert.Equal(SizeSetRefit.MadeOnFit.NoEvidence,
                     SizeSetRefit.JudgeMadeOn(12, new BodySizeMatch.Ranking(Confidence.Guess, []), null));
        Assert.Equal(SizeSetRefit.MadeOnFit.TooFar,
                     SizeSetRefit.JudgeMadeOn(12, Ranking(Confidence.Likely, 1f), SizeSetRefit.MadeOnMaxOff + 0.001f));
        Assert.Equal(SizeSetRefit.MadeOnFit.NoSizes, SizeSetRefit.JudgeMadeOn(0, Ranking(Confidence.Exact, 0f), 0f));
        Assert.Equal(SizeSetRefit.MadeOnFit.Taken, SizeSetRefit.JudgeMadeOn(12, Ranking(Confidence.Likely, 1f), null));
    }

    [Fact]
    [Trait("Plan", "AC-size-set-made-on-7")]
    [Trait("Plan", "EDGE-size-set-made-on-3")]
    public void A_piece_whose_every_reading_is_unclear_is_said_so() // [AC-size-set-made-on-7] [EDGE-size-set-made-on-3]
    {
        var s = Strings.SizeSet;
        Assert.Equal((string.Format(s.MadeOnUnclearFmt, "Body", "Bibo+"), Codes.NotMadeOn),
                     SizeSetRefit.UnreadableNote("Body", "Bibo+", "Bibo+", false,
                                                 [SizeSetRefit.MadeOnFit.Unclear, SizeSetRefit.MadeOnFit.Unclear]));
        Assert.Equal((string.Format(s.NotMadeOnFmt, "Body", "Bibo+"), Codes.NotMadeOn),
                     SizeSetRefit.UnreadableNote("Body", "Bibo+", "Bibo+", false,
                                                 [SizeSetRefit.MadeOnFit.Unclear, SizeSetRefit.MadeOnFit.TooFar]));
        Assert.Equal((string.Format(s.NotMadeOnFmt, "Body", "TBSE"), Codes.NotMadeOn),
                     SizeSetRefit.UnreadableNote("Body", "The Body SE", "TBSE", false, [SizeSetRefit.MadeOnFit.NoSizes]));
        Assert.Equal((string.Format(s.MadeOnMissingFmt, "Body", "gone"), Codes.NotMadeOn),
                     SizeSetRefit.UnreadableNote("Body", "gone", null, true, []));
        // Nothing read on it at all: its files could not be read, and the body chosen is not blamed.
        Assert.Equal((string.Format(s.UnreadableFmt, "Body"), Codes.Unreadable),
                     SizeSetRefit.UnreadableNote("Body", "Bibo+", "Bibo+", false, []));
        // Without "Made on": couldn't tell, and where to say it.
        Assert.Equal((string.Format(s.UnreadableMadeOnFmt, "Body"), Codes.Unreadable),
                     SizeSetRefit.UnreadableNote("Body", null, null, false, []));
    }

    [Fact]
    [Trait("Plan", "EDGE-size-set-made-on-5")]
    public void Made_on_readings_are_kept_apart() // [EDGE-size-set-made-on-5]
    {
        string key = SizeSetRefit.ReadingKey("top size/medium/top.mdl", "_top", "0201", "Bibo+");
        string madeOn = SizeSetRefit.ReadingKey("top size/medium/top.mdl", "_top", "0201", "Bibo+", "Bibo+");
        Assert.NotEqual(key, madeOn);
        Assert.Equal(madeOn, SizeSetRefit.ReadingKey("Top Size\\Medium\\top.mdl", "_top", "0201", "bibo+", "BIBO+"));
        Assert.Equal(key, SizeSetRefit.ReadingKey("Top Size\\Medium\\top.mdl", "_top", "0201", "bibo+"));

        // A variant read on the "Made on" body is found under its key, as one read automatically is under its own.
        var piece = new SizeSetRefit.Piece(Top, "_top", "Top Size", [("Medium", "m.mdl"), ("Medium Copy", "m2.mdl")], "Body",
                                           "Body — Top", "0201");
        var pick = new AutoRefitDecisions.Pick("Bibo+", NudeMedium, Confidence.Ambiguous, false);
        var auto = new Dictionary<string, AutoRefitDecisions.Pick?>
        {
            [SizeSetRefit.ReadingKey("m.mdl", "_top", "0201", "Bibo+")] = null,
            [SizeSetRefit.ReadingKey("m2.mdl", "_top", "0201", "Bibo+")] = null,
        };
        var madeOnReadings = new Dictionary<string, SizeSetRefit.SourceReading>
        {
            [SizeSetRefit.ReadingKey("m2.mdl", "_top", "0201", "Bibo+", "Bibo+")] = new(pick, true, SizeSetRefit.MadeOnFit.Taken),
        };
        Assert.Equal(["Medium Copy"], SizeSetRefit.OtherVariants(piece, "m.mdl", pick, "Bibo+", auto, madeOnReadings, "Bibo+"));
        Assert.Empty(SizeSetRefit.OtherVariants(piece, "m.mdl", pick, "Bibo+", auto));
    }

    // ── which of the author's sizes ───────────────────────────────────────────────────────────────────────────────

    private static SizeSetRefit.SourceDistance D(float bodyMm, float ownMm, int order, bool byMadeOn,
                                                 float misfitMm = SizeSetRefit.MisfitMm)
        => new(bodyMm / 1000f, bodyMm / 1000f, 0f, 0f, order, ownMm / 1000f, byMadeOn, misfitMm);

    [Fact]
    [Trait("Plan", "EDGE-size-set-made-on-21")]
    public void Misfits_are_judged_per_reading_kind() // [EDGE-size-set-made-on-21]
    {
        float madeOn = SizeSetRefit.MadeOnMisfitTopMm;
        // One read exactly, sitting on its body; three read on the "Made on" body as far off its sizes as Oblique's plain
        // tops lie off Bibo+'s (2.7–3.1 mm): far more than the threshold above the exact one, within it of each other.
        (string, SizeSetRefit.SourceDistance)[] mixed =
        [
            ("exact", D(9, 0.5f, 0, byMadeOn: false)),
            ("S", D(6, 2.7f, 1, byMadeOn: true, madeOn)),
            ("M", D(4, 3.0f, 2, byMadeOn: true, madeOn)),
            ("L", D(5, 3.1f, 3, byMadeOn: true, madeOn)),
        ];
        Assert.True(3.1f - 0.5f > madeOn);
        Assert.Empty(SizeSetRefit.Misfits(mixed));
        var (chosen, step, _, misfits) = SizeSetRefit.ChooseWithMisfits(mixed);
        Assert.Equal(("M", "body"), (chosen, step));
        Assert.Empty(misfits);

        // One of them far off the body it was read as is passed over, against the best of its own kind.
        (string, SizeSetRefit.SourceDistance)[] far = [.. mixed, ("far", D(1, 2.7f + madeOn + 1, 4, byMadeOn: true, madeOn))];
        Assert.Equal([4], SizeSetRefit.Misfits(far));
        Assert.Equal([4], SizeSetRefit.ChooseWithMisfits(far).Misfits);
        Assert.Equal("M", SizeSetRefit.Choose(far).Chosen);
    }

    [Fact]
    [Trait("Plan", "EDGE-size-set-made-on-14")]
    public void Made_on_misfits_follow_their_own_threshold() // [EDGE-size-set-made-on-14]
    {
        // Oblique's Small Tummy, read on Bibo+ as Nude Large, lies further off it than its plain sizes do theirs.
        float plain = 3f, tummy = plain + SizeSetRefit.MadeOnMisfitTopMm + 0.5f;
        (string, SizeSetRefit.SourceDistance)[] sizes =
        [
            ("Small", D(8, plain, 0, byMadeOn: true, SizeSetRefit.MadeOnMisfitTopMm)),
            ("Small Tummy", D(2, tummy, 1, byMadeOn: true, SizeSetRefit.MadeOnMisfitTopMm)),
        ];
        Assert.Equal([1], SizeSetRefit.Misfits(sizes));
        Assert.Equal(("Small", "skin fit"), (SizeSetRefit.Choose(sizes).Chosen, SizeSetRefit.Choose(sizes).Step));
        // The same two judged by the automatic reading's threshold keep the Tummy, when the gap is below it.
        if (tummy - plain <= SizeSetRefit.MisfitMm)
            Assert.Empty(SizeSetRefit.Misfits(sizes.Select(s => (s.Item1, s.Item2 with { MisfitMm = SizeSetRefit.MisfitMm }))
                                                   .ToList()));
        // KeepsBoth by the earlier one's threshold: a later plain size beside an earlier misread one is kept.
        Assert.True(SizeSetRefit.KeepsBoth(tummy / 1000f, plain / 1000f, SizeSetRefit.MadeOnMisfitTopMm));
        Assert.Equal(SizeSetRefit.MadeOnMisfitLegsMm, SizeSetRefit.MadeOnMisfitMm("_dwn"));
        Assert.Equal(SizeSetRefit.MadeOnMisfitTopMm, SizeSetRefit.MadeOnMisfitMm("_top"));
    }

    [Fact]
    [Trait("Plan", "EDGE-size-set-made-on-14")]
    public void A_lone_made_on_size_is_said_to_have_no_misfit_check() // [EDGE-size-set-made-on-14]
    {
        (string, SizeSetRefit.SourceDistance)[] sizes = [("exact", D(9, 0.5f, 0, false)), ("Tummy", D(2, 9, 1, true))];
        Assert.NotNull(SizeSetRefit.LoneMadeOn(sizes, "Tummy"));
        Assert.Null(SizeSetRefit.LoneMadeOn(sizes, "exact"));
        Assert.Null(SizeSetRefit.LoneMadeOn([.. sizes, ("M", D(4, 3, 2, true))], "Tummy"));
    }

    // ── an automatic reading on another body ──────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-size-set-made-on-10")]
    [Trait("Plan", "EDGE-size-set-made-on-20")]
    public void WarnsOtherBody_rule() // [AC-size-set-made-on-10] [EDGE-size-set-made-on-20]
    {
        const string tre = "hs-[YAS] Tre-5.5.3-8xB";
        SizeSetRefit.SourceReading Auto(string dir, Confidence c) => new(new AutoRefitDecisions.Pick(dir, NudeMedium, c, false), false, null);
        Assert.True(SizeSetRefit.WarnsOtherBody("Bibo+", "_top", Auto(tre, Confidence.Likely)));
        Assert.True(SizeSetRefit.WarnsOtherBody("Bibo+", "_dwn", Auto(tre, Confidence.Guess)));
        Assert.False(SizeSetRefit.WarnsOtherBody(null, "_top", Auto(tre, Confidence.Guess)));
        Assert.False(SizeSetRefit.WarnsOtherBody("Bibo+", "_top", Auto(tre, Confidence.Exact)));
        Assert.False(SizeSetRefit.WarnsOtherBody("Bibo+", "_top", Auto("bibo+", Confidence.Guess)));
        Assert.False(SizeSetRefit.WarnsOtherBody("Bibo+", "_top",
                                                 new(new AutoRefitDecisions.Pick("Bibo+", NudeMedium, Confidence.Guess, false), true,
                                                     SizeSetRefit.MadeOnFit.Taken)));
        // Shoes and gloves are always read automatically, and never said so.
        Assert.False(SizeSetRefit.WarnsOtherBody("Bibo+", "_sho", Auto(tre, Confidence.Guess)));
        Assert.False(SizeSetRefit.WarnsOtherBody("Bibo+", "_glv", Auto(tre, Confidence.Guess)));
    }

    // ── what the result line says ─────────────────────────────────────────────────────────────────────────────────

    private static SizeSetRefit.Piece P(string slot) => new("chara/equipment/e0485/model/c0201e0485" + slot + ".mdl", slot,
                                                            "Top Size", [], "Body", "Body — Top", "0201");

    private static SizeSetRefit.RunLog Log(params (string Slot, string? Option, SizeStatus Status, string? Code)[] sizes)
    {
        var log = new SizeSetRefit.RunLog();
        foreach (var (slot, option, status, code) in sizes) log.Add(P(slot), status, code, code, "Top Size", option);
        return log;
    }

    [Fact]
    [Trait("Plan", "AC-size-set-made-on-5")]
    [Trait("Plan", "EDGE-size-set-made-on-10")]
    [Trait("Plan", "EDGE-size-set-made-on-11")]
    public void Summary_is_read_from_every_outcome() // [AC-size-set-made-on-5] [EDGE-size-set-made-on-10] [EDGE-size-set-made-on-11]
    {
        var S = SizeSetRefit.Summarize;
        Assert.Equal(SizeSetRefit.Summary.Added, S(Log(("_top", "Rue S", SizeStatus.Made, null)), 1));
        Assert.Equal(SizeSetRefit.Summary.HadEverySize,
                     S(Log(("_top", "Rue S", SizeStatus.Skipped, Codes.AlreadyThere), ("_top", "Rue M", SizeStatus.Skipped, Codes.AuthorsOwn)), 0));
        foreach (string code in new[] { Codes.NoBodyForRace, Codes.SameSkin, Codes.NoEnd })
            Assert.Equal(SizeSetRefit.Summary.HadEverySize,
                         S(Log(("_top", "Rue S", SizeStatus.Skipped, Codes.AlreadyThere), ("_sho", null, SizeStatus.Skipped, code)), 0));

        // Legs with no chest option to join: nothing to do when every chest outcome there is the author's or already made.
        Assert.Equal(SizeSetRefit.Summary.HadEverySize,
                     S(Log(("_top", "Rue M", SizeStatus.Skipped, Codes.AuthorsOwn), ("_dwn", "Rue M", SizeStatus.Skipped, Codes.NoChestOption)), 0));
        Assert.Equal(SizeSetRefit.Summary.HadEverySize,
                     S(Log(("_top", "Rue M", SizeStatus.Skipped, Codes.AuthorsOwn), ("_top", "Rue M", SizeStatus.Skipped, Codes.AlreadyThere),
                           ("_dwn", "Rue M", SizeStatus.Skipped, Codes.NoChestOption)), 0));
        var refused = Log(("_top", "Rue M", SizeStatus.Skipped, Codes.AuthorsOwn), ("_top", "Rue M", SizeStatus.Refused, Codes.RefusedPair),
                          ("_dwn", "Rue M", SizeStatus.Skipped, Codes.NoChestOption));
        refused.Notes.Add("refused");
        Assert.Equal(SizeSetRefit.Summary.SeeLeftOut, S(refused, 0));

        // A men's shoe already there beside a men's top already there: nothing to do.
        var ends = Log(("_top", "TBSE", SizeStatus.Skipped, Codes.AlreadyThere), ("_sho", "TBSE", SizeStatus.Skipped, Codes.AlreadyThere));
        Assert.Equal(SizeSetRefit.Summary.HadEverySize, S(ends, 0));

        var unreadable = Log(("_top", null, SizeStatus.Skipped, Codes.Unreadable));
        unreadable.Notes.Add("unreadable");
        Assert.Equal(SizeSetRefit.Summary.SeeLeftOut, S(unreadable, 0));
        Assert.Equal(SizeSetRefit.Summary.SeeLog, S(Log(("_top", "Rue S", SizeStatus.Skipped, Codes.NoSource)), 0));
        var none = new SizeSetRefit.RunLog();
        SizeSetRefit.NoPieces(none);
        Assert.Equal(SizeSetRefit.Summary.SeeLeftOut, S(none, 0));
    }

    [Fact]
    [Trait("Plan", "AC-size-set-made-on-5")]
    public void Result_line_says_why_nothing_was_added() // [AC-size-set-made-on-5]
    {
        var s = Strings.SizeSet;
        Assert.Equal(string.Format(s.DoneFmt, 3), SizeSetRefit.ResultLine(new(3, [], [], SizeSetRefit.Summary.Added)));
        Assert.Equal(s.NothingAdded, SizeSetRefit.ResultLine(new(0, [], [], SizeSetRefit.Summary.HadEverySize)));
        Assert.Equal(s.NothingAddedSeeLeftOut, SizeSetRefit.ResultLine(new(0, ["x"], [], SizeSetRefit.Summary.SeeLeftOut)));
        Assert.Equal(s.NothingAddedSeeLog, SizeSetRefit.ResultLine(new(0, [], [], SizeSetRefit.Summary.SeeLog)));

        // Oblique as it was reported: both pieces unreadable. Never "already has every size".
        var oblique = Log(("_top", null, SizeStatus.Skipped, Codes.Unreadable), ("_dwn", null, SizeStatus.Skipped, Codes.Unreadable));
        oblique.Notes.AddRange(["top unreadable", "legs unreadable"]);
        var summary = SizeSetRefit.Summarize(oblique, 0);
        Assert.NotEqual(s.NothingAdded, SizeSetRefit.ResultLine(new(0, oblique.Notes, [], summary)));
    }

    // ── the panel's choice ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-size-set-made-on-9")]
    [Trait("Plan", "EDGE-size-set-made-on-13")]
    public void Made_on_state_is_per_mod() // [AC-size-set-made-on-9] [EDGE-size-set-made-on-13]
    {
        var madeOn = new SizeSetMadeOn();
        madeOn.Open("Oblique - by Solona");
        Assert.Null(madeOn.Dir);
        madeOn.Choose("Bibo+");
        madeOn.Open("oblique - by solona");
        Assert.Equal("Bibo+", madeOn.Dir);
        madeOn.Prune(["bibo+", "Neolithe"]);
        Assert.Equal("Bibo+", madeOn.Dir);
        madeOn.Prune(["Neolithe"]);
        Assert.Null(madeOn.Dir);
        madeOn.Choose("Bibo+");
        madeOn.Open("Sanguine - by Solona");
        Assert.Null(madeOn.Dir);

        var choices = SizeSetMadeOn.Choices([("zz", "Yet Another Body+"), ("Oblique - by Solona", "Oblique"), ("Bibo+", "Bibo+"),
                                             ("neo", "neolithe")], "Oblique - by Solona");
        Assert.Equal(["Bibo+", "neolithe", "Yet Another Body+"], choices.Select(c => c.Name));
    }

    [Fact]
    [Trait("Plan", "AC-size-set-made-on-9")]
    public void Made_on_tooltip_says_it_is_only_a_fallback() // [AC-size-set-made-on-9]
        => Assert.Contains("only for sizes Proteus can't read itself", Strings.SizeSet.MadeOnTip);

    // ── the strings ───────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] NewKeys =
    [
        "SizeSet.NothingAdded.SeeLeftOut", "SizeSet.NothingAdded.SeeLog", "SizeSet.MadeOn", "SizeSet.MadeOn.Auto",
        "SizeSet.MadeOn.Tip", "SizeSet.UnreadableMadeOn.Fmt", "SizeSet.NotMadeOn.Fmt", "SizeSet.MadeOnMissing.Fmt",
        "SizeSet.MadeOnUnclear.Fmt", "SizeSet.MadeOnOtherBody.Fmt",
    ];

    [Fact]
    [Trait("Plan", "AC-size-set-made-on-8")]
    public void New_size_set_strings_are_translated() // [AC-size-set-made-on-8]
    {
        Dictionary<string, string> Messages(string lang)
        {
            using var stream = typeof(Plugin).Assembly.GetManifestResourceStream($"Proteus.Localization.{lang}.json");
            Assert.NotNull(stream);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("message").GetString()!);
        }
        var en = Messages("en");
        static string Holes(string s) => string.Join(",", System.Text.RegularExpressions.Regex.Matches(s, @"\{\d\}")
                                                                   .Select(m => m.Value).Distinct().OrderBy(h => h));
        foreach (string lang in new[] { "de", "es", "fr", "ja", "ko", "ru", "zh" })
        {
            var other = Messages(lang);
            foreach (string key in NewKeys)
            {
                Assert.True(other.ContainsKey(key), $"{lang}.json has no {key}");
                Assert.NotEqual(en[key], other[key]);
                Assert.Equal(Holes(en[key]), Holes(other[key]));
            }
        }
    }
}
