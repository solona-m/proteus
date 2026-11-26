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
        bool replaceSkin = true, BodyRetarget.Tuning? tuning = null, bool carryPiercings = false, bool acrossBodies = false)
    {
        var log = new List<string>();
        var warn = new List<string>();
        var planned = BodyRetarget.WithTuning(tuning ?? new BodyRetarget.Tuning(), () =>
            BodyRetarget.Plan(ModelPartReader.Read(garment)!, (byte[])garment.Clone(), [pair], "_dwn", replaceSkin: replaceSkin,
                              acrossBodies: acrossBodies, log: log.Add, warn: warn.Add, carryPiercings: carryPiercings));
        return (planned, log, warn);
    }

    /// <summary>Today's output for the same refit: the switches off.</summary>
    private static string Today(byte[] garment, BodyRetarget.SlotPair pair, bool carryPiercings = false, bool acrossBodies = false)
        => Hash(Run(garment, pair, tuning: new BodyRetarget.Tuning(NoGenitalSwitches: true), carryPiercings: carryPiercings,
                    acrossBodies: acrossBodies).Plan.Model);

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

    /// <summary>The synthetic body standing as tall as real legs (see <see cref="SyntheticBulge.Body"/>'s feet): the switch
    /// gate judges a garment's skin against the height of the body it was made on.</summary>
    internal static byte[] SwBody(float height = 0.06f, bool mergedDome = false, string? domeBone = null, bool piercing = false,
                                  bool basePubes = false)
        => SyntheticBulge.Body(height: height, feet: true, mergedDome: mergedDome, domeBone: domeBone, piercing: piercing, basePubes: basePubes);

    /// <summary>The made-on body, footed as <see cref="SwBody"/> is.</summary>
    internal static byte[] SwSource() => SwBody(0.065f);

    /// <summary>Beach Briefs' IMC group: the default entry and each option's mask (Penumbra XORs a ticked option in).</summary>
    internal const int BriefsDefault = 514, OptBriefs = 576, OptGenA = 16, OptGenB = 32, OptGenC = 4, OptGenD = 8;

    /// <summary>Every setting worth telling apart: none, Briefs, each Gen, Briefs with a Gen, two Gens.</summary>
    internal static readonly int[][] Selections =
    [
        [], [OptBriefs], [OptGenA], [OptGenB], [OptGenC], [OptGenD], [OptBriefs, OptGenA], [OptBriefs, OptGenD], [OptGenA, OptGenB],
    ];

    internal static int MaskFor(IEnumerable<int> ticked) => ticked.Aggregate(BriefsDefault, (m, o) => m ^ o);

    /// <summary>Whether a part draws under an IMC mask: every variant tag it carries (atr_?v_x) has its letter's bit set.</summary>
    internal static bool Drawn(ModelParts m, ModelPart p, int mask)
        => GenitalSwitches.Names(m, p.AttributeMask).All(n => !SecondSkinWriter.IsVariantAttribute(n)
                                                              || (mask & (1 << (n[^1] - 'a'))) != 0);

    /// <summary>Skin parts carrying a whole switch set, drawn under <paramref name="mask"/>.</summary>
    internal static int DrawnCopies(ModelParts m, int mask, IReadOnlyList<string[]> sets)
        => m.Parts.Count(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material) && Drawn(m, p, mask)
                              && sets.Any(s => s.All(GenitalSwitches.Names(m, p.AttributeMask).Contains)));

    private static byte[] Switched(byte[] source, Func<ModelPart, bool>? keep = null, IReadOnlyList<string[]>? sets = null)
        => BulgeRefitTests.Garment(source, keep ?? (_ => true), switchedSkin: sets ?? BriefsSets, cap: BriefsTag, clothTags: BriefsTag);

    private static int DomeTriangles(byte[] body)
        => ModelPartReader.Read(body)!.Parts.Single(p => p.Island < 0 && p.Label == "1.2").Triangles.Length / 3;

    [Fact]
    [Trait("Plan", "AC-genital-switches-1")]
    [Trait("Plan", "AC-genital-switches-4")]
    [Trait("Plan", "EDGE-genital-switches-12")]
    public void Switched_garment_switches_the_new_bodys_penis()
    {
        var body = SwBody();
        var source = SwSource();
        var garment = Switched(source);
        var (plan, log, warn) = Run(garment, BulgeRefitTests.Pair(source, body));
        foreach (var l in log.Concat(warn)) output.WriteLine(l);
        var g = ModelPartReader.Read(garment)!;
        var o = ModelPartReader.Read(plan.Model)!;
        foreach (var p in o.Parts.Where(p => p.Island < 0))
            output.WriteLine($"{p.Label} {p.Material} {p.Triangles.Length / 3} [{string.Join(",", GenitalSwitches.Names(o, p.AttributeMask))}]");
        foreach (var sel in Selections)
        {
            int mask = MaskFor(sel);
            Assert.Equal(DrawnCopies(g, mask, BriefsSets), DrawnCopies(o, mask, BriefsSets));
        }
        Assert.Equal(0, DrawnCopies(o, MaskFor([OptBriefs]), BriefsSets));
        Assert.Equal(1, DrawnCopies(o, MaskFor([OptGenB]), BriefsSets));
        Assert.Contains(log, l => l.StartsWith("bulge: _dwn left as it is — the garment switches its own penis (4 switch sets)", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("genital switches: _dwn the new body's genitals", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.StartsWith("bulge: _dwn featureless", StringComparison.Ordinal));
        Assert.Empty(warn);
        var swap = plan.Report.Swap!.Value;
        Assert.Equal(4 * DomeTriangles(body), swap.Switched);
        Assert.Equal(4, swap.SwitchCopies);
        Assert.Equal(0, swap.HostSetSubmeshes);
        // No untagged copy of the new body's dome is left: nothing of it draws under the briefs.
        var dome = ModelPartReader.Read(body)!;
        var hole = TbseBulge.Shared!.Hole;
        var domeAt = dome.Parts.Single(p => p.Island < 0 && p.Label == "1.2").Triangles.Distinct()
                         .Select(v => GenitalParts.At(dome, v))
                         .Where(p => hole.All(h => System.Numerics.Vector3.Distance(h, p) > 1e-4f)).ToList();   // not the rim
        Assert.DoesNotContain(o.Parts.Where(p => p.Island < 0 && p.AttributeMask == 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)),
                              p => p.Triangles.Any(v => domeAt.Any(d => System.Numerics.Vector3.Distance(d, GenitalParts.At(o, v)) < 1e-4f)));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-45")]
    public void Set_tagged_cloth_still_gets_switches()
    {
        // The garment's cloth itself carries a whole switch set (a pouch drawn only with Gen A): counted beside the copies.
        var body = SwBody();
        var source = SwSource();
        var garment = BulgeRefitTests.Garment(source, _ => true, switchedSkin: BriefsSets, cap: BriefsTag, clothTags: BriefsSets[0]);
        var (plan, log, warn) = Run(garment, BulgeRefitTests.Pair(source, body));
        Assert.Empty(warn);
        Assert.Equal(1, plan.Report.Swap!.Value.HostSetSubmeshes);
        Assert.Equal(4, plan.Report.Swap!.Value.SwitchCopies);
        Assert.Contains(log, l => l.StartsWith("genital switches: _dwn the new body's genitals", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-genital-switches-7")]
    public void The_knob_tries_the_bulge_as_before()
    {
        // NoGenitalSwitches is today's build: the switched garment onto a body the bulge serves goes featureless again.
        var body = SwBody();
        var source = SwSource();
        var (_, log, _) = Run(Switched(source), BulgeRefitTests.Pair(source, body), tuning: new BodyRetarget.Tuning(NoGenitalSwitches: true));
        Assert.DoesNotContain(log, l => l.Contains("the garment switches its own penis", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("bulge: _dwn featureless", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-4")]
    public void Kept_skin_refit_is_today()
    {
        var body = SwBody();
        var source = SwSource();
        var garment = Switched(source);
        var (a, _, _) = Run(garment, BulgeRefitTests.Pair(source, body), replaceSkin: false);
        var (b, _, _) = Run(garment, BulgeRefitTests.Pair(source, body), replaceSkin: false, new BodyRetarget.Tuning(NoGenitalSwitches: true));
        Assert.Equal(Hash(b.Model), Hash(a.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-8")]
    public void One_copy_per_distinct_set()
    {
        var body = SwBody();
        var source = SwSource();
        var garment = Switched(source, sets: [BriefsSets[0], BriefsSets[0]]);
        var (plan, _, warn) = Run(garment, BulgeRefitTests.Pair(source, body));
        Assert.Empty(warn);
        Assert.Equal(1, plan.Report.Swap!.Value.SwitchCopies);
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-11")]
    public void Cap_and_knee_skin_are_not_switch_sets()
    {
        var source = SwSource();
        var garment = Switched(source);
        var parts = ModelPartReader.Read(garment)!;
        var src = ModelPartReader.Read(source)!;
        var reading = GenitalParts.Read(src, ModelSkinReader.Read(source, null, null)!);
        var sets = GenitalSwitches.Of(parts, ModelSkinReader.Read(garment, null, null), src, reading, out var why);
        Assert.NotNull(sets);
        Assert.Equal(4, sets!.Count);
        for (int i = 0; i < 4; i++) Assert.True(sets[i].SetEquals(BriefsSets[i]), string.Join("+", sets[i]));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-16")]
    public void Two_gen_options_draw_two_copies()
    {
        var body = SwBody();
        var source = SwSource();
        var (plan, _, _) = Run(Switched(source), BulgeRefitTests.Pair(source, body));
        Assert.Equal(2, DrawnCopies(ModelPartReader.Read(plan.Model)!, MaskFor([OptGenA, OptGenB]), BriefsSets));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-31")]
    [Trait("Plan", "AC-genital-switches-8")]
    public void A_fault_in_the_switch_path_is_today()
    {
        var body = SwBody();
        var source = SwSource();
        var garment = Switched(source);
        // The fallback (AC-8 as the plan records it): the new body's penis without switches and without the bulge, which was
        // already left out for a garment that switches its own penis — the refit with neither.
        var (today, _, _) = Run(garment, BulgeRefitTests.Pair(source, body) with { Featureless = null },
                                tuning: new BodyRetarget.Tuning(NoGenitalSwitches: true));
        foreach (var fault in new[] { BodyRetarget.SwitchFault.InFor, BodyRetarget.SwitchFault.ReadBack })
        {
            var (plan, _, warn) = Run(garment, BulgeRefitTests.Pair(source, body), tuning: new BodyRetarget.Tuning(GenitalSwitchFault: fault));
            Assert.Contains(warn, l => l.StartsWith("genital switches: _dwn left as today — failed:", StringComparison.Ordinal));
            Assert.Equal(Hash(today.Model), Hash(plan.Model));
        }
    }

    internal const string TodayNoGenitals = "A5D216059317C2378E137B0BE271F73065FD949E0359A44B2CF3A1617EB1FEF1";
    internal const string TodayUntagged = "B543A152C9AE58040BB922EBA25A68294C2B81D1DFF508A366DE0A9BEAFC3E5F";
    internal const string TodaySwitchedOntoFlat = "108732396AA4BFF93EDEB503E9E2E79787FBC147352457D4F9F45A6B8159E0E6";
    internal const string TodayFeatureless = "672922D9282023FC742ED0ABC0113768A7C85329A5A89E5C4B8AB200475A023F";
}
