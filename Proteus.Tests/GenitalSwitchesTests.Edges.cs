using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Proteus.Services.Bulge;
using Xunit;

namespace Proteus.Tests;

/// <summary>The genital switches' edge cases (plan genital-switches): every way the switches are left out, and the
/// layouts they reach — a refit of a refit, a penis merged into the main skin, piercings on it.</summary>
public partial class GenitalSwitchesTests
{
    /// <summary>The pair from the synthetic made-on body onto a body of another layout (its own extra meshes, its dome merged
    /// into the main skin), matched by uv as a refit across layouts is; no featureless factory.</summary>
    private static BodyRetarget.SlotPair PairByUv(byte[] source, byte[] body)
    {
        var sp = ModelPartReader.Read(source)!;
        var tp = ModelPartReader.Read(body)!;
        Assert.True(BodyCorrespondence.TryBuild(sp, BodyRetarget.Uv(source), tp, BodyRetarget.Uv(body), "_dwn", out var corr, out string why,
                                                out _, null, true, true), why);
        return new BodyRetarget.SlotPair("_dwn", corr!, tp, body, source);
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-2")]
    public void An_untagged_penis_piece_means_no_switches()
    {
        var body = SyntheticBulge.Body();
        var source = BulgeRefitTests.Source();
        var garment = BulgeRefitTests.Garment(source, _ => true, switchedSkin: BriefsSets, cap: BriefsTag, untaggedPenis: true);
        var pair = BulgeRefitTests.Pair(source, body) with { Featureless = null };
        var (plan, log, warn) = Run(garment, pair);
        Assert.Contains(log, l => l == "genital switches: _dwn left as today — the garment draws an untagged penis");
        Assert.Empty(warn);
        Assert.Equal(Today(garment, pair), Hash(plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-33")]
    public void A_merged_untagged_penis_means_no_switches()
    {
        var body = SyntheticBulge.Body();
        var source = BulgeRefitTests.Source();
        var garment = BulgeRefitTests.Garment(source, _ => true, switchedSkin: BriefsSets, cap: BriefsTag, mergedUntaggedPenis: true);
        var pair = BulgeRefitTests.Pair(source, body) with { Featureless = null };
        var (plan, log, _) = Run(garment, pair);
        Assert.Contains(log, l => l == "genital switches: _dwn left as today — the garment draws an untagged penis");
        Assert.Equal(Today(garment, pair), Hash(plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-35")]
    public void A_switched_piece_past_the_window_means_no_switches()
    {
        var body = SyntheticBulge.Body();
        var source = BulgeRefitTests.Source();
        var garment = BulgeRefitTests.Garment(source, _ => true, switchedSkin: BriefsSets, cap: BriefsTag, switchedPastWindow: true);
        var pair = BulgeRefitTests.Pair(source, body) with { Featureless = null };
        var (plan, log, _) = Run(garment, pair);
        Assert.Contains(log, l => l == "genital switches: _dwn left as today — a switched piece reaches past the crotch window");
        Assert.Equal(Today(garment, pair), Hash(plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-5")]
    public void Switched_pieces_in_a_kept_mesh_is_today()
    {
        var body = SyntheticBulge.Body();
        var source = BulgeRefitTests.Source();
        var garment = BulgeRefitTests.Garment(source, _ => true, switchedSkin: BriefsSets, cap: BriefsTag, liftedSwitched: true);
        var pair = BulgeRefitTests.Pair(source, body) with { Featureless = null };
        var (plan, log, warn) = Run(garment, pair);
        // The mesh holding the switched pieces is kept whole (partly off the body): no copy is written — said as such when
        // another skin mesh of the slot is swapped, and here, its only one kept, nothing is rebuilt at all.
        Assert.True(log.Contains("genital switches: _dwn left as today — the garment's switched pieces stay in a skin mesh this refit keeps")
                    || plan.Report.Swap is { Kept: > 0, SwitchCopies: 0 }, string.Join(" | ", log));
        Assert.Equal(0, plan.Report.Swap?.SwitchCopies ?? 0);
        Assert.Empty(warn);
        Assert.Equal(Today(garment, pair), Hash(plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-10")]
    [Trait("Plan", "EDGE-genital-switches-36")]
    public void No_crotch_or_no_source_skin_is_today()
    {
        var body = SyntheticBulge.Body();
        var source = BulgeRefitTests.Source();
        var garment = Switched(source);
        var pair = BulgeRefitTests.Pair(source, body) with { Featureless = null, SourceModel = null };
        var (plan, log, warn) = Run(garment, pair);
        Assert.Contains(log, l => l == "genital switches: _dwn left as today — the garment's body has no crotch to read");
        Assert.Empty(warn);
        Assert.Equal(Today(garment, pair), Hash(plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-13")]
    public void Refit_of_a_refit_keeps_four_sets()
    {
        var body = SyntheticBulge.Body();
        var source = BulgeRefitTests.Source();
        var (first, _, _) = Run(Switched(source), BulgeRefitTests.Pair(source, body) with { Featureless = null });
        var third = SyntheticBulge.Body(height: 0.07f);
        var (second, _, warn) = Run(first.Model, BulgeRefitTests.Pair(body, third) with { Featureless = null });
        Assert.Empty(warn);
        Assert.Equal(4, second.Report.Swap!.Value.SwitchCopies);
        var o = ModelPartReader.Read(second.Model)!;
        Assert.Equal(4, o.Parts.Count(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material) && p.AttributeMask != 0
                                           && BriefsSets.Any(s => s.All(GenitalSwitches.Names(o, p.AttributeMask).Contains))));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-6")]
    [Trait("Plan", "AC-genital-switches-2")]
    public void Merged_genitals_are_split_into_copies()
    {
        // The new body's penis merged into its main skin (rule b, as TBSE-X's erect options): only its triangles are copied.
        var body = SyntheticBulge.Body(mergedDome: true, domeBone: "iv_ochinko_a");
        var source = BulgeRefitTests.Source();
        var (plan, log, warn) = Run(Switched(source), PairByUv(source, body));
        foreach (var l in log.Concat(warn)) output.WriteLine(l);
        Assert.Empty(warn);
        var swap = plan.Report.Swap!.Value;
        Assert.Equal(4, swap.SwitchCopies);
        var o = ModelPartReader.Read(plan.Model)!;
        var copies = o.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material) && p.AttributeMask != 0).ToList();
        Assert.Equal(4, copies.Count);
        Assert.All(copies, c => Assert.Equal(swap.Switched / 4, c.Triangles.Length / 3));
        Assert.True(swap.Switched / 4 <= DomeTriangles(SyntheticBulge.Body()), "more than the dome was copied");
        // The rest of the main skin is still drawn, untagged.
        Assert.Contains(o.Parts, p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material) && p.AttributeMask == 0 && p.Triangles.Length > 0);
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-32")]
    public void Genital_piercing_is_switched()
    {
        // The switches the new body's piercing and pube layers get, read off its genitals as Rebuild reads them: the
        // piercing on the dome is copied once per set, whole; the pubes round the base, on the hips, are not genital and
        // get none (they stay whole and untagged).
        var body = SyntheticBulge.Body(piercing: true, basePubes: true);
        var parts = ModelPartReader.Read(body)!;
        var skin = ModelSkinReader.Read(body, null, null)!;
        var reading = GenitalParts.Read(parts, skin)!;
        Assert.True(reading.Penis);
        var sets = BriefsSets.Select(s => (IReadOnlySet<string>)s.ToHashSet()).ToList();
        var ring = parts.Parts.Single(p => p.Island < 0 && p.Material.Contains("pierce", StringComparison.Ordinal));
        var ringSpecs = GenitalSwitches.For(parts, skin, reading, sets, m => m == ring.Material, out _, out int ringTris);
        var spec = Assert.Single(ringSpecs);
        Assert.Equal((ring.Mesh, ring.Submesh), (spec.Mesh, spec.Submesh));
        Assert.Null(spec.Ordinals);   // every triangle of it lies on the dome
        Assert.Equal(4, spec.TagSets.Count);
        Assert.Equal(4 * ring.Triangles.Length / 3, ringTris);
        var pubes = parts.Parts.Single(p => p.Island < 0 && p.Material.Contains("pube", StringComparison.Ordinal));
        Assert.Empty(GenitalSwitches.For(parts, skin, reading, sets, m => m == pubes.Material, out _, out _));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-48")]
    public void Dropped_set_tagged_piercing_is_not_counted()
    {
        // The garment's own genital piercing, tagged with a set, refitted across body mods: it goes with the old body's
        // extras, so it is not counted beside the copies — and the read-back agrees.
        var body = SyntheticBulge.Body();
        var source = BulgeRefitTests.Source();
        var garment = BulgeRefitTests.Garment(source, _ => true, switchedSkin: BriefsSets, cap: BriefsTag, setTaggedPiercing: BriefsSets[0]);
        var (plan, log, warn) = Run(garment, BulgeRefitTests.Pair(source, body) with { Featureless = null }, acrossBodies: true);
        foreach (var l in log.Concat(warn)) output.WriteLine(l);
        Assert.Empty(warn);
        var swap = plan.Report.Swap!.Value;
        Assert.Equal(4, swap.SwitchCopies);
        Assert.Equal(0, swap.HostSetSubmeshes);
        Assert.DoesNotContain(ModelPartReader.Read(plan.Model)!.Parts, p => p.Material.Contains("pierce", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-9")]
    [Trait("Plan", "AC-genital-switches-8")]
    public void A_set_name_the_garment_lacks_is_today()
    {
        // A set naming a tag the garment's table does not hold: Rebuild writes no switches and says so. The garment is made
        // on the new body itself, so its skin lies on it and the swap claims it.
        var body = SyntheticBulge.Body();
        var garment = Switched(body);
        var pair = BulgeRefitTests.Pair(SyntheticBulge.Body(height: 0.065f), body) with { Featureless = null };
        var lines = new List<string>();
        var plain = BodyRetarget.Rebuild(garment, [pair], true, null, out _);
        var odd = BodyRetarget.Rebuild(garment, [pair], true, null, out var report,
            genitalSwitches: new Dictionary<string, IReadOnlyList<IReadOnlySet<string>>> { ["_dwn"] = [new HashSet<string> { "atrx_nowhere" }] },
            switchLog: lines.Add);
        Assert.Contains(lines, l => l == "genital switches: _dwn left as today — a tag has no attribute slot");
        Assert.Equal(0, report.SwitchCopies);
        Assert.Equal(Hash(plain!), Hash(odd!));
    }

    [Fact]
    [Trait("Plan", "AC-genital-switches-9")]
    [Trait("Plan", "EDGE-genital-switches-34")]
    public void Gate_costs_nothing()
    {
        // Tagged skin only at the knees: the gate passes it by, and the refit is today's.
        var source = BulgeRefitTests.Source();
        var body = SyntheticBulge.Body();
        var knees = BulgeRefitTests.Garment(source, _ => true, kneeSkin: true);
        var src = ModelPartReader.Read(source)!;
        var kneeParts = ModelPartReader.Read(knees)!;
        Assert.False(GenitalSwitches.MayHaveSwitches(kneeParts, src));
        Assert.False(GenitalSwitches.MayHaveSwitches(ModelPartReader.Read(BulgeRefitTests.Garment(source, _ => true))!, src));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) GenitalSwitches.MayHaveSwitches(kneeParts, src);
        Assert.True(sw.ElapsedMilliseconds < 50, $"{sw.ElapsedMilliseconds} ms for 20 gates");
        var pair = BulgeRefitTests.Pair(source, body) with { Featureless = null };
        Assert.Equal(Today(knees, pair), Hash(Run(knees, pair).Plan.Model));
    }
}
