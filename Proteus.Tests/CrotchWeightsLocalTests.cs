using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The crotch ease on real garments and bodies, through the automatic refit: panties made for YAB onto Rue, Neolithe and
/// Bibo+, a swimsuit's crotch, and pants that must come out as before. Measured inside the refit, on the model it writes,
/// against the same refit without the ease.
/// </summary>
public class CrotchWeightsLocalTests(ITestOutputHelper output)
{
    private const string Sylvie = LocalData.Mods + ":Elegy - Sylvie Bra & Panties (RueYABUranusRe)";
    private const string Swimsuit = LocalData.Mods + ":[HS] Competition Swimsuit - by Solona (Default)";
    private const string Scarlet = LocalData.Mods + ":Scarlet - by Solona";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string FixPubes = LocalData.Mods + ":fix pubes";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Tbse = LocalData.Mods + ":The Body SE";
    private const string Race = "0201";
    private const string Panties = "Smallclothes  Panty Shape/YAB WC/YAB WC.mdl";
    private const string RuePanties = "Smallclothes  Panty Shape/Rue WC/Rue WC.mdl";

    private static (string Dir, BodySizeCatalog Catalog) Body(string need)
        => (Path.GetFileName(LocalData.Path(need)), BodySizeCatalog.Read(LocalData.Path(need)));

    private static readonly string[] GenitalBones = ["iv_omanko", "iv_kuritto", "iv_inshin_l", "iv_inshin_r"];

    /// <summary>One refit, as the automatic refit makes it, and what it wrote.</summary>
    private sealed record Run(BodyRetarget.Planned Plan, List<string> Log, BodyRetarget.CrotchAnchor? Anchor,
                              Vector3[] At, List<(string Bone, float W)>[] Weights, bool[] Cloth);

    /// <summary>
    /// <paramref name="rel"/> of <paramref name="mod"/> refitted onto <paramref name="target"/>'s
    /// <paramref name="label"/> in each of <paramref name="slots"/> — the first the garment's own — from whichever body it
    /// reads as made on.
    /// </summary>
    private static Run Refit(string mod, string rel, string[] slots, (string Dir, BodySizeCatalog Catalog) target,
                             Func<string, string> label, bool ease)
    {
        var bodies = new[] { Body(FixPubes), Body(Bibo), Body(Rue), Body(Neolithe) }.ToList();
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(mod), rel));
        var garment = ModelPartReader.Read(bytes)!;
        var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
        var pairs = new List<BodyRetarget.SlotPair>();
        bool across = false;
        foreach (var slot in slots)
        {
            var pick = RefitCore.DetectSource(garment, bones, slot, Race, bodies, target.Dir, _ => true)!;
            var from = bodies.First(b => string.Equals(b.Dir, pick.Dir, StringComparison.OrdinalIgnoreCase)).Catalog;
            var to = target.Catalog.For(slot, Race).First(o => o.Label == label(slot));
            Assert.Null(BodyRetarget.BuildPair(slot, from.PathOf(pick.Option), target.Catalog.PathOf(to), slot, false,
                                               null, null, null, out var pair));
            pairs.Add(pair);
            across |= !string.Equals(pick.Dir, target.Dir, StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(across, "the refit must be between body mods for the ease to run");

        var log = new List<string>();
        var plan = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoCrotchEase: !ease),
            () => RefitCore.PlanRefit(garment, bytes, pairs, slots[0], across, false, out _, log: log.Add));
        var anchor = BodyRetarget.FindCrotch([.. pairs.Select(p => (p.Target, ModelSkinReader.Read(p.TargetModel!, null, null)!))]);

        var written = ModelPartReader.Read(plan.Model)!;
        var at = Enumerable.Range(0, written.Positions.Length / 3)
                           .Select(v => new Vector3(written.Positions[v * 3], written.Positions[v * 3 + 1], written.Positions[v * 3 + 2]))
                           .ToArray();
        var cloth = new bool[at.Length];
        foreach (var part in written.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            foreach (int v in part.Triangles) cloth[v] = true;
        return new Run(plan, log, anchor, at, SyntheticCrotch.Weights(plan.Model), cloth);
    }

    /// <summary>Cloth where the ease is whole and the refit without it left real leg weight.</summary>
    private static List<int> Gusset(Run eased, Run before)
        => [.. Enumerable.Range(0, eased.At.Length)
                         .Where(v => eased.Cloth[v] && BodyRetarget.CrotchStrength(eased.At[v], eased.Anchor!.Value) >= 0.99f
                                     && SyntheticCrotch.Legs(before.Weights[v]) >= 0.1f)];

    private void Report(string what, Run eased, Run before, List<int> gusset)
    {
        var a = eased.Anchor!.Value;
        output.WriteLine($"{what}: crotch at {a.Y * 1000f:0} mm (x {a.MidX * 1000f:0}, z {a.At.Z * 1000f:0}), scale {a.Scale:0.00}; "
                         + $"{gusset.Count} gusset points, legs {gusset.Average(v => SyntheticCrotch.Legs(before.Weights[v])):0.000} -> "
                         + $"{gusset.Average(v => SyntheticCrotch.Legs(eased.Weights[v])):0.000}");
        foreach (var line in eased.Log.Where(l => l.StartsWith("crotch:", StringComparison.Ordinal))) output.WriteLine("  " + line);
    }

    /// <summary>Every influence placed, every cloth vertex summing to one and given no more slots than without the ease.</summary>
    private static void Sound(Run run, Run against)
    {
        Assert.Equal(0, run.Plan.Report.Swap?.Unplaced ?? 0);
        for (int v = 0; v < run.At.Length; v++)
        {
            if (!run.Cloth[v]) continue;
            Assert.True(run.Weights[v].Count <= Math.Max(4, against.Weights[v].Count),
                        $"cloth vertex {v} carries {run.Weights[v].Count} influences, {against.Weights[v].Count} without the ease");
            Assert.Equal(1f, run.Weights[v].Sum(i => i.W), 2);
        }
    }

    private static void Same(Run eased, Run before)
    {
        Assert.Equal(before.At.Length, eased.At.Length);
        for (int v = 0; v < eased.At.Length; v++)
            Assert.True(eased.Weights[v].OrderBy(i => i.Bone).SequenceEqual(before.Weights[v].OrderBy(i => i.Bone)),
                        $"vertex {v} at {eased.At[v]}: {string.Join(",", eased.Weights[v])} against {string.Join(",", before.Weights[v])}");
    }

    public static TheoryData<string, string, string> Targets => new()
    {
        { Panties, Neolithe, "DEFAULT · Gen A Medium" },
        { Panties, Bibo, "Type A - Medium" },
        { RuePanties, Neolithe, "DEFAULT · Gen B Medium" },
        { RuePanties, Bibo, "Type B - Medium" },
    };

    /// <remarks>Onto Rue the YAB cut reads as Rue's already (Sylvie makes both on one topology), which is no refit
    /// between bodies; Rue as the new body is the swimsuit's, below.</remarks>
    [LocalDataTheory(Sylvie, Bibo, FixPubes, Rue, Neolithe)]
    [MemberData(nameof(Targets))]
    public void Sylvie_panties_onto_Neolithe_and_Bibo(string panties, string body, string label)
    {
        // [AC-crotch-leg-weights-9]
        var target = Body(body);
        var eased = Refit(Sylvie, panties, ["_dwn"], target, _ => label, ease: true);
        var before = Refit(Sylvie, panties, ["_dwn"], target, _ => label, ease: false);
        Assert.NotNull(eased.Anchor);
        var gusset = Gusset(eased, before);
        Report($"Sylvie {Path.GetFileNameWithoutExtension(panties)} onto {target.Dir} / {label}", eased, before, gusset);

        Assert.True(gusset.Count >= 10, $"only {gusset.Count} gusset points");
        Assert.All(gusset, v => Assert.True(SyntheticCrotch.Legs(eased.Weights[v]) <= 0.1f * SyntheticCrotch.Legs(before.Weights[v]) + 0.02f,
                                            $"vertex {v} at {eased.At[v]}: {SyntheticCrotch.Legs(eased.Weights[v])} of "
                                            + $"{SyntheticCrotch.Legs(before.Weights[v])}"));
        // The crotch point lies where the gusset is lowest, give or take.
        float lowest = Enumerable.Range(0, eased.At.Length).Where(v => eased.Cloth[v]
                                  && MathF.Abs(eased.At[v].X - eased.Anchor!.Value.MidX) < 0.01f
                                  && MathF.Abs(eased.At[v].Z - eased.Anchor.Value.At.Z) < 0.03f).Min(v => eased.At[v].Y);
        Assert.True(MathF.Abs(lowest - eased.Anchor!.Value.Y) < 0.02f, $"gusset lowest at {lowest}, crotch at {eased.Anchor.Value.Y}");
        Sound(eased, before);
        Assert.True((eased.Plan.Report.Swap?.Slotted ?? 0) <= (before.Plan.Report.Swap?.Slotted ?? 0));

        // No genital bone gains anything (Rue's Gen B carries them).
        for (int v = 0; v < eased.At.Length; v++)
            foreach (var bone in GenitalBones)
                Assert.True(eased.Weights[v].Where(i => i.Bone == bone).Sum(i => i.W)
                            <= before.Weights[v].Where(i => i.Bone == bone).Sum(i => i.W) + 0.01f, $"vertex {v} gained {bone}");
    }

    [LocalDataTheory(Sylvie, Bibo, FixPubes, Rue, Neolithe)]
    [InlineData(Neolithe, "DEFAULT · Gen A Large")]
    [InlineData(Bibo, "Type A - Large")]
    public void Full_back_briefs_are_not_vetoed_on_the_largest_sizes(string body, string label)
    {
        // [AC-crotch-leg-weights-9] [EDGE-crotch-leg-weights-22]
        var eased = Refit(Sylvie, Panties, ["_dwn"], Body(body), _ => label, ease: true);
        foreach (var line in eased.Log.Where(l => l.StartsWith("crotch:", StringComparison.Ordinal))) output.WriteLine(line);
        Assert.DoesNotContain(eased.Log, l => l.Contains("left on the legs", StringComparison.Ordinal));
        Assert.Contains(eased.Log, l => l.EndsWith("eased off them", StringComparison.Ordinal));
    }

    [LocalDataFact(Tbse)]
    public void Tbse_crotch_point_is_the_perineum_not_the_pouch()
    {
        // [AC-crotch-leg-weights-9] [EDGE-crotch-leg-weights-25]
        var tbse = Body(Tbse);
        var option = tbse.Catalog.For("_dwn", "0101").First(o => o.Label == "Gen A");
        var bytes = File.ReadAllBytes(tbse.Catalog.PathOf(option));
        var parts = ModelPartReader.Read(bytes)!;
        var skin = ModelSkinReader.Read(bytes, null, null)!;
        var a = BodyRetarget.FindCrotch([(parts, skin)])!.Value;

        // The lowest point of anything rigged mostly to bones that are neither the hips nor a leg: the pouch.
        float pouch = float.MaxValue;
        for (int v = 0; v < skin.VertexCount; v++)
        {
            float own = 0f, total = 0f;
            for (int k = 0; k < 8; k++)
            {
                float w = skin.BoneWeights[v * 8 + k];
                if (w <= 0f) continue;
                string bone = skin.BoneNames[skin.BoneIndices[v * 8 + k]];
                total += w;
                if (!BodyRetarget.IsLegBone(bone) && bone != "j_kosi") own += w;
            }
            if (own >= 0.5f * total) pouch = MathF.Min(pouch, parts.Positions[v * 3 + 1]);
        }
        output.WriteLine($"TBSE Gen A: crotch at {a.Y * 1000f:0} mm (x {a.MidX * 1000f:0}, z {a.At.Z * 1000f:0}); "
                         + (pouch < float.MaxValue ? $"pouch's lowest {pouch * 1000f:0} mm" : "no pouch on its own bones"));
        // TBSE splits its crotch down the middle: found where the sides meet, on the midline, at a crotch's height.
        Assert.True(MathF.Abs(a.MidX) < 0.01f, $"midline at {a.MidX}");
        Assert.InRange(a.Y, 0.7f, 1.0f);
        Assert.True(a.Y > pouch || pouch == float.MaxValue, $"the crotch ({a.Y}) was taken at or below the pouch ({pouch})");
    }

    [LocalDataFact(Scarlet, Bibo, FixPubes, Rue, Neolithe)]
    public void Pants_unchanged()
    {
        // [AC-crotch-leg-weights-9] [AC-crotch-leg-weights-3] Pants down the legs onto Rue and Neolithe: the crotch seam
        // stays with the legs.
        int compared = 0;
        foreach (var (mod, rel) in new[]
                 {
                     (Scarlet, "pant size/neolithe m/chara/equipment/e0041/model/c0201e0041_dwn.mdl"),
                 })
            foreach (var (body, label) in new[] { (Rue, "None · Watermelon Crushers - A"), (Neolithe, "DEFAULT · SFW Medium") })
            {
                var target = Body(body);
                Run eased, before;
                try
                {
                    eased = Refit(mod, rel, ["_dwn"], target, _ => label, ease: true);
                    before = Refit(mod, rel, ["_dwn"], target, _ => label, ease: false);
                }
                catch (Xunit.Sdk.TrueException) { continue; }   // made on that body: no refit between bodies to judge
                output.WriteLine($"{Path.GetFileName(LocalData.Path(mod))} onto {target.Dir}:");
                foreach (var line in eased.Log.Where(l => l.StartsWith("crotch:", StringComparison.Ordinal))) output.WriteLine("  " + line);
                Same(eased, before);
                Assert.Contains(eased.Log, l => l.Contains("left on the legs", StringComparison.Ordinal));
                compared++;
            }
        Assert.True(compared > 0, "no pair of these pants was refitted between bodies: nothing was compared");
    }

    [LocalDataTheory(Swimsuit, Bibo, FixPubes, Rue, Neolithe)]
    [InlineData("None · Watermelon Crushers - A")]
    [InlineData("None · Watermelon Crushers - B")]
    public void Competition_swimsuit_crotch_eased(string legs)
    {
        // [AC-crotch-leg-weights-6] [AC-crotch-leg-weights-9] The Neolithe M size onto Rue, its top and the legs it
        // reaches: Gen A and Gen B, whose genitals have bones of their own.
        const string rel = "size/neolithe m/chara/equipment/e0317/model/c0201e0317_top.mdl";
        var target = Body(Rue);
        string Label(string slot) => slot == "_top" ? "None · Medium" : legs;
        var eased = Refit(Swimsuit, rel, ["_top", "_dwn"], target, Label, ease: true);
        var before = Refit(Swimsuit, rel, ["_top", "_dwn"], target, Label, ease: false);
        var gusset = Gusset(eased, before);
        Report($"Competition Swimsuit (Neolithe M) onto Rue / {legs}", eased, before, gusset);
        for (int v = 0; v < eased.At.Length; v++)
            foreach (var bone in GenitalBones)
                Assert.True(eased.Weights[v].Where(i => i.Bone == bone).Sum(i => i.W)
                            <= before.Weights[v].Where(i => i.Bone == bone).Sum(i => i.W) + 0.01f, $"vertex {v} gained {bone}");
        Assert.True(gusset.Count >= 10, $"only {gusset.Count} gusset points");
        Assert.All(gusset, v => Assert.True(SyntheticCrotch.Legs(eased.Weights[v]) <= 0.1f * SyntheticCrotch.Legs(before.Weights[v]) + 0.02f));
        var a = eased.Anchor!.Value;
        for (int v = 0; v < eased.At.Length; v++)
            if (eased.At[v].Y >= a.Y + BodyRetarget.CrotchFade * a.Scale)
                Assert.True(eased.Weights[v].OrderBy(i => i.Bone).SequenceEqual(before.Weights[v].OrderBy(i => i.Bone)),
                            $"vertex {v} above the crotch changed");
        Sound(eased, before);
    }
}
