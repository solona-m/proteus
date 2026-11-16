extern alias RefitCli;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;
using Codes = Proteus.Services.SizeSetRefit.Codes;
using Fit = Proteus.Services.SizeSetRefit.MadeOnFit;

namespace Proteus.Tests;

/// <summary>
/// KAN-6: a chest or legs piece without enough skin to tell its sizes by, placed on the "Made on" body's sizes by its
/// option names (<c>SizeSetRefit.Source.cs</c>, <see cref="SupportedBodies"/>). The rules, on hand-made catalogs; the
/// real run on Tutting is in <see cref="SizeSetMadeOnSkinlessLocalTests"/>.
/// </summary>
public class SizeSetMadeOnSkinlessTests
{
    private const string Legs = "chara/equipment/e0000/model/c0201e0000_dwn.mdl";
    private const string Chest = "chara/equipment/e0000/model/c0201e0000_top.mdl";

    private static BodyOption Option(string rel)
        => new("Size", "", Path.GetFileName(Path.GetDirectoryName(rel.Replace('/', Path.DirectorySeparatorChar)) ?? rel) ?? rel, rel,
               rel.EndsWith("_dwn.mdl", StringComparison.OrdinalIgnoreCase) ? Legs : Chest,
               rel.EndsWith("_dwn.mdl", StringComparison.OrdinalIgnoreCase) ? "_dwn" : "_top");

    private static SupportedBodies.Body Table(string key, string slot, bool yas = false)
        => SupportedBodies.Of(key, slot, yas) ?? throw new InvalidOperationException($"no {key} {slot} table");

    /// <summary>A body mod holding every file of the given tables (chest and paired legs), but for <paramref name="without"/>.</summary>
    private static BodySizeCatalog Catalog(IEnumerable<SupportedBodies.Body> tables, params string[] without)
    {
        var rels = tables.SelectMany(t => t.Sizes).SelectMany(s => new[] { s.Rel, s.LegsRel }).OfType<string>()
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(r => !without.Any(w => r.Contains(w, StringComparison.OrdinalIgnoreCase)));
        return new BodySizeCatalog("<body>", rels.Select(Option).ToList());
    }

    private static BodySizeCatalog Catalog(params string[] rels) => new("<body>", rels.Select(Option).ToList());

    private static BodyModIndex.Entry Entry(string name, BodySizeCatalog catalog) => new(name, name, catalog);

    // ── size words ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-1")]
    public void SizeToken_reads_one_size_word() // [EDGE-kan-6-skinless-made-on-1]
    {
        var expect = new Dictionary<string, string>
        {
            ["XLarge"] = "XL", ["X-Large"] = "XL", ["Extra Large"] = "XL", ["XL"] = "XL", ["Med"] = "M", ["Bibo+ Small"] = "S",
            ["Small Tummy"] = "S", ["s"] = "S", ["Women's Small"] = "S", ["Small"] = "S", ["Medium"] = "M", ["Large"] = "L",
            ["S"] = "S", ["XS"] = "XS", ["X-Small"] = "XS", ["extra small"] = "XS",
        };
        foreach (var (name, token) in expect) Assert.True(SupportedBodies.SizeToken(name) == token, $"{name} → {token}");
        foreach (string? name in new[]
                 {
                     "S/M", "Small to Medium", "Petite", "Type A", "Default", "Women's", "Men's Fit", "Solona's", "2XL", "3XL",
                     "Size 2", "XXL", "XXS", "XXXL", "", null, "WC", "SC", "Coconut",
                 })
            Assert.True(SupportedBodies.SizeToken(name) == null, $"{name ?? "null"} gives no size");
    }

    [Fact]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-22")]
    public void NamesOtherBody_finds_another_body_by_whole_word() // [EDGE-kan-6-skinless-made-on-22]
    {
        Assert.Equal("Rue", SupportedBodies.NamesOtherBody("Rue Small", "bibo"));
        Assert.Equal("TBSE", SupportedBodies.NamesOtherBody("TBSE Large", "bibo"));
        Assert.Equal("YAB", SupportedBodies.NamesOtherBody("Yet Another Body Small", "bibo"));
        Assert.Equal("Rue", SupportedBodies.NamesOtherBody("Rue WC", "bibo"));
        Assert.Null(SupportedBodies.NamesOtherBody("Rue WC", "rue"));
        Assert.Null(SupportedBodies.NamesOtherBody("Street Small", "bibo"));
        Assert.Null(SupportedBodies.NamesOtherBody("True Blue M", "bibo"));
        Assert.Null(SupportedBodies.NamesOtherBody("Bibo+ Small", "bibo"));
        Assert.Null(SupportedBodies.NamesOtherBody("YAB Small", "yab-buff"));
        Assert.Null(SupportedBodies.NamesOtherBody(null, "bibo"));
        Assert.Null(SupportedBodies.NamesOtherBody("Small", "bibo"));
    }

    // ── which of the table's bodies the "Made on" body is ─────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-18")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-19")]
    public void MadeOnTable_finds_the_bodys_own_table() // [EDGE-kan-6-skinless-made-on-18] [EDGE-kan-6-skinless-made-on-19]
    {
        var bibo = Entry("Bibo+", Catalog([Table("bibo", "_top"), Table("bibo", "_dwn")]));
        Assert.Equal(("bibo", "_dwn"), Key(SupportedBodies.MadeOnTable(bibo, "_dwn", "0201", false)));
        Assert.Equal(("bibo", "_top"), Key(SupportedBodies.MadeOnTable(bibo, "_top", "0201", false)));
        Assert.Null(SupportedBodies.MadeOnTable(bibo, "_dwn", "0101", false));

        // Rue+ and Yet Another Body+ file their legs alike: the name says which.
        var hsLegs = Catalog([Table("yab", "_dwn"), Table("rue", "_dwn")]);
        Assert.Equal("yab", SupportedBodies.MadeOnTable(Entry("hs-Yet Another Body+", hsLegs), "_dwn", "0201", false)?.Key);
        Assert.Equal("rue", SupportedBodies.MadeOnTable(Entry("hs-Rue+", hsLegs), "_dwn", "0201", false)?.Key);

        // Plain or YAS, as the garment is rigged.
        var both = Catalog([Table("rue", "_dwn"), Table("rue", "_dwn", yas: true)]);
        Assert.False(SupportedBodies.MadeOnTable(Entry("Rue+", both), "_dwn", "0201", false)!.Yas);
        Assert.True(SupportedBodies.MadeOnTable(Entry("Rue+", both), "_dwn", "0201", true)!.Yas);

        // Rosaline's folders are numbered like YAS Tre's: a renamed copy is told by how many of a table's files it holds…
        var rosalineChest = Entry("Hips Mod", Catalog("files/common/5/c0201e0000_top.mdl", "files/common/6/c0201e0000_top.mdl",
                                                       "files/common/7/c0201e0000_top.mdl"));
        Assert.Equal("rosaline", SupportedBodies.MadeOnTable(rosalineChest, "_top", "0201", true)?.Key);
        // …and, when that ties, by holding one table whole.
        var rosalineLegs = Entry("Hips Mod", Catalog("files/common/13/c0201e0000_dwn.mdl", "files/common/15/c0201e0000_dwn.mdl"));
        Assert.Equal("rosaline", SupportedBodies.MadeOnTable(rosalineLegs, "_dwn", "0201", true)?.Key);

        Assert.Null(SupportedBodies.MadeOnTable(Entry("Outfit", Catalog("some/other/c0201e0000_dwn.mdl")), "_dwn", "0201", false));
    }

    private static (string, string)? Key(SupportedBodies.Body? b) => b == null ? null : (b.Key, b.Slot);

    // ── placing a size by its name ────────────────────────────────────────────────────────────────────────────────

    private static string? Rel((SizeSetRefit.TrustedSize? Trusted, Fit Fit, string Why) r) => r.Trusted?.On.Rel;

    [Fact]
    [Trait("Plan", "AC-kan-6-skinless-made-on-8")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-2")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-4")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-5")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-14")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-21")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-22")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-23")]
    public void TrustOn_places_by_name() // [AC-kan-6-skinless-made-on-8] [EDGE-kan-6-skinless-made-on-2] [EDGE-kan-6-skinless-made-on-4] [EDGE-kan-6-skinless-made-on-5] [EDGE-kan-6-skinless-made-on-14] [EDGE-kan-6-skinless-made-on-21] [EDGE-kan-6-skinless-made-on-22] [EDGE-kan-6-skinless-made-on-23]
    {
        var biboLegs = Table("bibo", "_dwn");
        var biboChest = Table("bibo", "_top");
        var bibo = Catalog([biboChest, biboLegs]);
        (SizeSetRefit.TrustedSize?, Fit, string) On(string? option, SupportedBodies.Body? table, BodySizeCatalog cat, string slot)
            => SizeSetRefit.TrustOn(option, table, cat, slot, "0201", followsChest: false);

        var small = On("Small", biboLegs, bibo, "_dwn");
        Assert.Equal(Fit.Trusted, small.Item2);
        Assert.Contains("type a - small", small.Item1!.On.Rel);
        Assert.Equal(("bibo", "S"), (small.Item1.Key, small.Item1.Size));
        Assert.Null(small.Item1.Legs);
        Assert.Contains("type a - large", Rel(On("Large", biboLegs, bibo, "_dwn")));
        Assert.Equal(Fit.NoMatch, On("XLarge", biboLegs, bibo, "_dwn").Item2);
        Assert.Equal(Fit.NoMatch, On(null, biboLegs, bibo, "_dwn").Item2);
        var rue = On("Rue Small", biboLegs, bibo, "_dwn");
        Assert.Equal(Fit.NoMatch, rue.Item2);
        Assert.Contains("names another body", rue.Item3);

        // A chest takes the legs its size is worn with — and, lacking them, keeps its hips.
        var medium = On("Medium", biboChest, bibo, "_top").Item1!;
        Assert.Contains("nude - medium", medium.On.Rel);
        Assert.Contains("type a - medium", medium.Legs!.Rel);
        var noLegs = On("Medium", biboChest, Catalog([biboChest, biboLegs], "type a - medium"), "_top");
        Assert.Equal(Fit.Trusted, noLegs.Item2);
        Assert.Null(noLegs.Item1!.Legs);
        // A size whose model the body lacks is not placed.
        Assert.Equal(Fit.NoMatch, On("Medium", biboLegs, Catalog([biboLegs], "type a - medium"), "_dwn").Item2);

        // Tre's legs are Medium and Large: a Small has no counterpart.
        var treLegs = Table("tre", "_dwn");
        var tre = Catalog([treLegs]);
        Assert.Equal(Fit.NoMatch, On("Small", treLegs, tre, "_dwn").Item2);
        Assert.Contains("common/28", Rel(On("Tre Medium", treLegs, tre, "_dwn")));

        var neoChest = Table("neolithe", "_top");
        var neo = Catalog([neoChest]);
        foreach (string xs in new[] { "XS", "Neolithe XS" })
        {
            var r = On(xs, neoChest, neo, "_top");
            Assert.Equal(Fit.Trusted, r.Item2);
            Assert.Contains("nsfw xs", r.Item1!.On.Rel);
            Assert.Null(r.Item1.Legs);
        }

        // Named as the table names them.
        var yabLegs = Table("yab", "_dwn");
        var yab = Catalog([yabLegs]);
        Assert.Contains("watermelon crushers - a", Rel(On("WC", yabLegs, yab, "_dwn")));
        Assert.Contains("skull crushers - a", Rel(On("sc", yabLegs, yab, "_dwn")));
        var rueLegs = Table("rue", "_dwn");
        var rueCat = Catalog([rueLegs]);
        Assert.Contains("watermelon crushers - a", Rel(On("Rue WC", rueLegs, rueCat, "_dwn")));
        Assert.Equal(Fit.NoMatch, On("Small", rueLegs, rueCat, "_dwn").Item2);

        // A body with no named sizes for the piece.
        var rosalineLegs = Table("rosaline", "_dwn");
        Assert.Equal(Fit.NoTable, On("Small", rosalineLegs, Catalog([rosalineLegs]), "_dwn").Item2);
        Assert.Equal(Fit.NoTable, On("Small", null, bibo, "_dwn").Item2);
    }

    [Fact]
    [Trait("Plan", "AC-kan-6-skinless-made-on-8")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-15")]
    public void TrustOn_places_a_follower_on_its_chest_sizes_legs() // [AC-kan-6-skinless-made-on-8] [EDGE-kan-6-skinless-made-on-15]
    {
        (SizeSetRefit.TrustedSize?, Fit, string) On(string option, SupportedBodies.Body table, BodySizeCatalog cat)
            => SizeSetRefit.TrustOn(option, table, cat, "_dwn", "0201", followsChest: true);

        var rueChest = Table("rue", "_top");
        var rue = On("Medium", rueChest, Catalog([rueChest]));
        Assert.Equal(Fit.Trusted, rue.Item2);
        Assert.Equal("files/legs - smallclothes/watermelon crushers - a/chara/equipment/e0000/model/c0201e0000_dwn.mdl",
                     rue.Item1!.On.Rel);
        Assert.Equal("M", rue.Item1.Size);
        Assert.Null(rue.Item1.Legs);

        var treChest = Table("tre", "_top");
        Assert.Equal("common/28/c0201e0000_dwn.mdl", On("Small", treChest, Catalog([treChest])).Item1!.On.Rel);

        var neoChest = Table("neolithe", "_top");
        Assert.Equal(Fit.NoMatch, On("XS", neoChest, Catalog([neoChest])).Item2);

        var biboChest = Table("bibo", "_top");
        Assert.Contains("type a - large", On("Large", biboChest, Catalog([biboChest])).Item1!.On.Rel);
        Assert.Equal(Fit.NoMatch, On("Large", biboChest, Catalog([biboChest], "type a - large")).Item2);
    }

    // ── when to trust ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-kan-6-skinless-made-on-6")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-7")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-10")]
    public void Only_a_garment_with_nothing_to_read_is_trusted() // [AC-kan-6-skinless-made-on-6] [EDGE-kan-6-skinless-made-on-7] [EDGE-kan-6-skinless-made-on-10]
    {
        Assert.True(SizeSetRefit.Trusts(Confidence.NoBodyMesh, Fit.NoEvidence));
        Assert.True(SizeSetRefit.Trusts(Confidence.TooLittle, Fit.NoEvidence));
        Assert.False(SizeSetRefit.Trusts(Confidence.Guess, Fit.NoEvidence));
        Assert.False(SizeSetRefit.Trusts(Confidence.Ambiguous, Fit.NoEvidence));
        foreach (var confidence in Enum.GetValues<Confidence>())
            foreach (var fit in new[] { Fit.Taken, Fit.TooFar, Fit.Unclear, Fit.NoSizes })
                Assert.False(SizeSetRefit.Trusts(confidence, fit), $"{confidence}/{fit}");
    }

    [Fact]
    [Trait("Plan", "AC-kan-6-skinless-made-on-5")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-3")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-4")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-8")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-9")]
    public void UnreadableNote_is_honest_about_why() // [AC-kan-6-skinless-made-on-5] [EDGE-kan-6-skinless-made-on-3] [EDGE-kan-6-skinless-made-on-4] [EDGE-kan-6-skinless-made-on-8] [EDGE-kan-6-skinless-made-on-9]
    {
        var s = Strings.SizeSet;
        Assert.Equal((string.Format(s.MadeOnNoSizeTableFmt, "Legs", "Rosaline"), Codes.NotMadeOn),
                     SizeSetRefit.UnreadableNote("Legs", "Rosaline", "Rosaline", false, [Fit.NoTable, Fit.NoTable], noneParsed: false));
        Assert.Equal((string.Format(s.MadeOnNoSizeNameFmt, "Legs", "Bibo+"), Codes.NotMadeOn),
                     SizeSetRefit.UnreadableNote("Legs", "Bibo+", "Bibo+", false, [Fit.NoMatch, Fit.NoMatch], noneParsed: false));
        Assert.Equal((string.Format(s.MadeOnNoSizeNameFmt, "Legs", "Bibo+"), Codes.NotMadeOn),
                     SizeSetRefit.UnreadableNote("Legs", "Bibo+", "Bibo+", false, [Fit.NoMatch, Fit.NoTable], noneParsed: false));
        Assert.Equal((string.Format(s.NotMadeOnFmt, "Legs", "Bibo+"), Codes.NotMadeOn),
                     SizeSetRefit.UnreadableNote("Legs", "Bibo+", "Bibo+", false, [Fit.NoMatch, Fit.TooFar], noneParsed: false));
        Assert.Equal((string.Format(s.UnreadableMadeOnFmt, "Legs"), Codes.Unreadable),
                     SizeSetRefit.UnreadableNote("Legs", null, null, false, [], noneParsed: false));
        Assert.Equal((string.Format(s.MadeOnMissingFmt, "Legs", "gone"), Codes.NotMadeOn),
                     SizeSetRefit.UnreadableNote("Legs", "gone", null, true, [], noneParsed: false));
    }

    // ── the author's own sizes ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-kan-6-skinless-made-on-1")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-11")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-15")]
    public void TrustedOwn_matches_key_and_size_file() // [AC-kan-6-skinless-made-on-1] [EDGE-kan-6-skinless-made-on-11] [EDGE-kan-6-skinless-made-on-15]
    {
        var biboLegs = Table("bibo", "_dwn");
        string RelOf(SupportedBodies.Body t, string size) => t.Sizes.First(s => s.Name == size).Rel;
        SizeSetRefit.TrustedSize T(string key, SupportedBodies.Body t, string size) => new(key, size, Option(RelOf(t, size)), null);

        (string?, SizeSetRefit.TrustedSize?)[] choices =
            [("Small", T("bibo", biboLegs, "S")), ("Medium", T("bibo", biboLegs, "M")), ("Untrusted", null)];
        Assert.Equal("Small", SizeSetRefit.TrustedOwn(choices, "bibo", RelOf(biboLegs, "S")));
        Assert.Equal("Medium", SizeSetRefit.TrustedOwn(choices, "bibo", RelOf(biboLegs, "M").ToUpperInvariant()));
        Assert.Null(SizeSetRefit.TrustedOwn(choices, "bibo", RelOf(biboLegs, "L")));
        Assert.Null(SizeSetRefit.TrustedOwn(choices, "rue", RelOf(biboLegs, "S")));

        // Plain Rue S trusted is no YAS Rue S.
        var rueChest = Table("rue", "_top");
        var rueYas = Table("rue", "_top", yas: true);
        Assert.Null(SizeSetRefit.TrustedOwn([("Small", T("rue", rueChest, "S"))], "rue", RelOf(rueYas, "S")));
        Assert.Equal("Small", SizeSetRefit.TrustedOwn([("Small", T("rue", rueChest, "S"))], "rue", RelOf(rueChest, "S")));

        // A follower's trusted legs against a target's legs.
        var biboChest = Table("bibo", "_top");
        var followerLegs = new SizeSetRefit.TrustedSize("bibo", "M", Option(biboChest.Sizes.First(s => s.Name == "M").LegsRel!), null);
        Assert.Equal("Medium", SizeSetRefit.TrustedOwn([("Medium", followerLegs)], "bibo", RelOf(biboLegs, "M")));
    }

    // ── a reading kept whole ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-6")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-13")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-16")]
    public void A_trusted_reading_is_kept_whole() // [EDGE-kan-6-skinless-made-on-6] [EDGE-kan-6-skinless-made-on-13] [EDGE-kan-6-skinless-made-on-16]
    {
        var biboLegs = Table("bibo", "_dwn");
        var bibo = Catalog([biboLegs]);
        var small = SizeSetRefit.TrustOn("Small", biboLegs, bibo, "_dwn", "0201", false).Trusted!;
        var tummy = SizeSetRefit.TrustOn("Small Tummy", biboLegs, bibo, "_dwn", "0201", false).Trusted!;
        var medium = SizeSetRefit.TrustOn("Medium", biboLegs, bibo, "_dwn", "0201", false).Trusted!;
        // Read again — the men's pass does, with caches of its own — it comes to the very same size.
        Assert.Equal(small, SizeSetRefit.TrustOn("Small", biboLegs, bibo, "_dwn", "0201", false).Trusted);
        Assert.Equal(small.On, tummy.On);

        const string Pants = "chara/equipment/e6118/model/c0201e6118_dwn.mdl";
        var piece = new SizeSetRefit.Piece(Pants, "_dwn", "Pant Size", [("Small", "s.mdl"), ("Small Tummy", "st.mdl"), ("Medium", "m.mdl")],
                                           "Pants", "Pants — Legs", "0201");
        SizeSetRefit.SourceReading R(SizeSetRefit.TrustedSize t)
            => new(new AutoRefitDecisions.Pick("Bibo+", t.On, Confidence.NoBodyMesh, false), true, Fit.Trusted, t);
        var auto = new Dictionary<string, AutoRefitDecisions.Pick?>
        {
            [SizeSetRefit.ReadingKey("s.mdl", "_dwn", "0201", "Bibo+")] = null,
            [SizeSetRefit.ReadingKey("st.mdl", "_dwn", "0201", "Bibo+")] = null,
            [SizeSetRefit.ReadingKey("m.mdl", "_dwn", "0201", "Bibo+")] = null,
        };
        var madeOn = new Dictionary<string, SizeSetRefit.SourceReading>
        {
            [SizeSetRefit.ReadingKey("st.mdl", "_dwn", "0201", "Bibo+", "Bibo+")] = R(tummy),
            [SizeSetRefit.ReadingKey("m.mdl", "_dwn", "0201", "Bibo+", "Bibo+")] = R(medium),
        };
        // Two options on one trusted size are variants of one another (the dedupe keeps the first); another size is not.
        Assert.Equal(["Small Tummy"], SizeSetRefit.OtherVariants(piece, "s.mdl", R(small).Pick!, "Bibo+", auto, madeOn, "Bibo+"));

        // A trusted size's own skin is never measured, so it is never passed over as a misfit.
        var d = new SizeSetRefit.SourceDistance(0.004f, 0.004f, 0f, 0f, 1, null, true, SizeSetRefit.MadeOnMisfitLegsMm);
        var exact = new SizeSetRefit.SourceDistance(0.009f, 0.009f, 0f, 0f, 0, 0.0005f, false, SizeSetRefit.MisfitMm);
        Assert.Empty(SizeSetRefit.Misfits([("exact", exact), ("trusted", d)]));
    }

    // ── what is said ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-kan-6-skinless-made-on-4")]
    public void Trusted_warning_is_added_once() // [AC-kan-6-skinless-made-on-4]
    {
        var log = new SizeSetRefit.RunLog();
        string text = string.Format(Strings.SizeSet.MadeOnTrustedFmt, "Pants — Legs", "Bibo+");
        SizeSetRefit.AddWarningOnce(log, text);
        SizeSetRefit.AddWarningOnce(log, text);
        Assert.Single(log.Warnings, text);
    }

    [Fact]
    [Trait("Plan", "AC-kan-6-skinless-made-on-7")]
    public void Cli_usage_says_skinless_pieces_are_placed_by_name() // [AC-kan-6-skinless-made-on-7]
        => Assert.Contains("without enough skin to tell its sizes by is placed on that body's size its option names give",
                           System.Text.RegularExpressions.Regex.Replace(RefitCli::Proteus.Refit.Program.Usage, @"\s+", " "));

    private static readonly string[] NewKeys = ["SizeSet.MadeOnNoSizeName.Fmt", "SizeSet.MadeOnNoSizeTable.Fmt", "SizeSet.MadeOnTrusted.Fmt"];

    [Fact]
    [Trait("Plan", "AC-kan-6-skinless-made-on-9")]
    public void New_strings_are_translated() // [AC-kan-6-skinless-made-on-9]
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
        foreach (string key in NewKeys)
        {
            Assert.True(en.ContainsKey(key), $"en.json has no {key}");
            Assert.DoesNotContain("no skin", en[key], StringComparison.OrdinalIgnoreCase);
        }
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
