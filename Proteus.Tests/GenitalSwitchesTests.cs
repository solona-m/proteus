using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Proteus.Services;
using Proteus.Services.Bulge;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The new body's genitals given the garment's own switches (plan genital-switches, part 1), on the synthetic TBSE-like
/// body through <see cref="BodyRetarget.Plan"/> (CI).
/// </summary>
public partial class GenitalSwitchesTests(ITestOutputHelper output)
{
    /// <summary>Beach Briefs' four penis switch sets (Gen A–D), its cap's and its briefs' tag.</summary>
    internal static readonly string[][] BriefsSets =
    [
        ["atr_dv_e", "atr_dv_j"], ["atr_dv_f", "atr_dv_j"], ["atr_dv_c", "atr_dv_j"], ["atr_dv_d", "atr_dv_j"],
    ];
    internal static readonly string[] BriefsTag = ["atr_dv_g"];

    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    private static (BodyRetarget.Planned Plan, List<string> Log, List<string> Warn) Run(byte[] garment, BodyRetarget.SlotPair pair,
        bool replaceSkin = true, BodyRetarget.Tuning? tuning = null)
    {
        var log = new List<string>();
        var warn = new List<string>();
        var planned = BodyRetarget.WithTuning(tuning ?? new BodyRetarget.Tuning(), () =>
            BodyRetarget.Plan(ModelPartReader.Read(garment)!, (byte[])garment.Clone(), [pair], "_dwn", replaceSkin: replaceSkin,
                              acrossBodies: false, log: log.Add, warn: warn.Add));
        return (planned, log, warn);
    }

    /// <summary>
    /// Today's outputs, captured before any change (Phase 0.1): the refits the switches and the re-stitch must leave
    /// byte for byte as they were — a garment with no genitals of its own, one carrying its old body's dome untagged, a
    /// switched garment onto a body with no penis — and the featureless synthetic body.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-genital-switches-7")]
    [Trait("Plan", "EDGE-genital-switches-1")]
    [Trait("Plan", "EDGE-genital-switches-3")]
    public void Today_is_pinned()
    {
        var body = SyntheticBulge.Body();
        var source = BulgeRefitTests.Source();
        var noGenitals = BulgeRefitTests.Garment(source, _ => true);
        var untagged = BulgeRefitTests.Garment(source, p => p.Label == "1.1", genitalSkin: true);
        var switched = BulgeRefitTests.Garment(source, _ => true, switchedSkin: BriefsSets, cap: BriefsTag, clothTags: BriefsTag);
        var flat = SyntheticBulge.Body(height: 0.004f);
        string a = Hash(Run(noGenitals, BulgeRefitTests.Pair(source, body)).Plan.Model);
        string b = Hash(Run(untagged, BulgeRefitTests.Pair(source, body)).Plan.Model);
        string c = Hash(Run(switched, BulgeRefitTests.Pair(source, flat)).Plan.Model);
        string d = Hash(BulgeSwap.Featureless(body, TbseBulge.Shared!, out _)!.Model);
        output.WriteLine($"noGenitals {a}\nuntagged {b}\nswitchedOntoFlat {c}\nfeatureless {d}");
        Assert.Equal(TodayNoGenitals, a);
        Assert.Equal(TodayUntagged, b);
        Assert.Equal(TodaySwitchedOntoFlat, c);
        Assert.Equal(TodayFeatureless, d);
    }

    internal const string TodayNoGenitals = "A5D216059317C2378E137B0BE271F73065FD949E0359A44B2CF3A1617EB1FEF1";
    internal const string TodayUntagged = "B543A152C9AE58040BB922EBA25A68294C2B81D1DFF508A366DE0A9BEAFC3E5F";
    internal const string TodaySwitchedOntoFlat = "108732396AA4BFF93EDEB503E9E2E79787FBC147352457D4F9F45A6B8159E0E6";
    internal const string TodayFeatureless = "672922D9282023FC742ED0ABC0113768A7C85329A5A89E5C4B8AB200475A023F";
}
