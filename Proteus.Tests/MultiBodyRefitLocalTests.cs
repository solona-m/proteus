using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// A garment refitted with its parts on different bodies (see <see cref="AutoRefitTargets"/>), against the installed
/// mods: Solona's sweater dress, made on Bibo+ "Perky - Medium" with its skin from the chest to the legs, its chest sent
/// to another body — YAB+, or Tight&amp;Firm Gen2, whose skin is in another layout — and its legs kept on Bibo+, through
/// the refit between bodies as it stands, the engine unchanged; and the one case that refit cannot do. Measured inside
/// the pass, against the same dress refitted onto one body.
/// </summary>
[Trait("Speed", "Slow")]
public class MultiBodyRefitLocalTests(ITestOutputHelper output)
{
    private const string Dresses = LocalData.Mods + ":I Just Really Like Sweater Dresses, OK - by Solona";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string TandF = LocalData.Mods + ":tight&firmgen2body-nsfw-installer";
    private const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";
    private const string Dress = "size/medium/chara/equipment/e6057/model/c0201e6057_top.mdl";
    private const string Race = "0201";

    /// <summary>Half the height of the band around the waist the cloth's stretch is read over: the chest and legs
    /// models meet there, and each half of a one-mesh dress follows its own body's.</summary>
    private const float WaistBand = 0.06f;

    private static readonly Lazy<UVRemapService> Maps =
        new(() => new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), LocalData.Path(LocalData.UvMaps)));

    private static (string Dir, BodySizeCatalog Catalog) Body(string need)
        => (Path.GetFileName(LocalData.Path(need)), BodySizeCatalog.Read(LocalData.Path(need)));

    /// <summary>The dress and what it was read as made on, with each part's source size.</summary>
    private sealed record Read(byte[] Bytes, ModelParts Garment, AutoRefitDecisions.Pick Pick, BodySizeCatalog From,
                               BodyOption LegsFrom);

    private Read Setup()
    {
        var bodies = new[] { Body(Bibo), Body(Rue) }.ToList();
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Dresses), Dress));
        var garment = ModelPartReader.Read(bytes)!;
        var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
        var pick = RefitCore.DetectSource(garment, bones, "_top", Race, bodies, null, _ => true)!;
        Assert.Equal("Bibo+", pick.Dir);
        var from = bodies[0].Catalog;
        var others = AutoRefitWatcher.OtherParts(garment, "_top", Race, pick, from,
                                                 (_, options) => BodySizeMatch.Rank(garment, options, from.PathOf, bones));
        // Its skin reaches the legs: a refit of it has a legs part to send to another body.
        Assert.True(others.ContainsKey("_dwn"), "the dress must be read as reaching the legs");
        var legsFrom = from.For("_dwn", Race).First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(others["_dwn"]));
        return new Read(bytes, garment, pick, from, legsFrom);
    }

    /// <summary>What a refit came to, inside the pass.</summary>
    /// <param name="Stretch">The most any cloth edge in the waist band grew or shrank, in metres.</param>
    private sealed record Outcome(BodyRetarget.Planned Plan, int Folded, int Unplaced, int Kept, int Posed, float Stretch);

    private Outcome Refit(Read d, string chestTo, string legsTo, IReadOnlyList<string> skinOf)
    {
        Assert.Null(BodyRetarget.BuildPair("_top", d.From.PathOf(d.Pick.Option), chestTo, "_top", false, null, null, Maps.Value,
                                           out var chest));
        Assert.Null(BodyRetarget.BuildPair("_dwn", d.From.PathOf(d.LegsFrom), legsTo, "_dwn", false, null, null, Maps.Value,
                                           out var legs));
        SecondSkinWriter.Vec3[]? final = null;
        var plan = RefitCore.PlanRefit(d.Garment, d.Bytes, [chest, legs], "_top", across: true, gameGear: false, out _,
                                       trace: (stage, nodes) => { if (stage == "layer-order") final = nodes; });
        Assert.NotNull(final);
        plan = RefitCore.MatchSkinToBody(plan, d.From.PathOf(d.Pick.Option), skinOf, Maps.Value, false, NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>());

        return Measure(d, plan, chest, legs, chestTo, final!);
    }

    /// <summary>What a plan came to, with the cloth's stretch in the waist band read off the pass's final positions.</summary>
    private static Outcome Measure(Read d, BodyRetarget.Planned plan, BodyRetarget.SlotPair chest, BodyRetarget.SlotPair legs,
                                   string chestTo, SecondSkinWriter.Vec3[] final)
    {
        var sets = BodyRetarget.Sets.From(d.Garment, new HashSet<int>(),
                                          BodyRetarget.PartedSkinMeshes(d.Garment, [chest, legs], "_top", true, null));
        float waist = ModelPartReader.Read(File.ReadAllBytes(chestTo))!.Positions.Where((_, i) => i % 3 == 1).Min();
        var p = d.Garment.Positions;
        float stretch = 0f;
        foreach (var part in d.Garment.Parts.Where(q => q.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(q.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = part.Triangles[t + e], b = part.Triangles[t + (e + 1) % 3];
                    if (MathF.Abs(p[a * 3 + 1] - waist) > WaistBand || MathF.Abs(p[b * 3 + 1] - waist) > WaistBand) continue;
                    float before = Distance(p[a * 3], p[a * 3 + 1], p[a * 3 + 2], p[b * 3], p[b * 3 + 1], p[b * 3 + 2]);
                    var fa = final[sets.NodeOf[a]];
                    var fb = final[sets.NodeOf[b]];
                    stretch = MathF.Max(stretch, MathF.Abs(Distance(fa.X, fa.Y, fa.Z, fb.X, fb.Y, fb.Z) - before));
                }
        var swap = plan.Report.Swap ?? default;
        return new Outcome(plan, plan.Report.Folded, swap.Unplaced, swap.Kept, swap.Posed, stretch);
    }

    /// <summary>
    /// The dress refitted the way the Studio's Body size tool plans a whole garment (BodyRetargetPanel.StartPlan): its
    /// chest onto <paramref name="chestDir"/>'s <paramref name="chestTo"/>, its legs onto <paramref name="legsDir"/>'s
    /// <paramref name="legsTo"/> (a body of their own when that is another) with the tool's default boxes, between bodies
    /// as StudioPartBodies.Across says, and the skin matched to every body it went onto (StudioPartBodies.TargetPaths).
    /// </summary>
    private Outcome StudioRefit(Read d, string chestDir, string chestTo, string legsDir, string legsTo)
    {
        Assert.Null(BodyRetarget.BuildPair("_top", d.From.PathOf(d.Pick.Option), chestTo, "_top", false, null, null, Maps.Value,
                                           out var chest));
        Assert.Null(BodyRetarget.BuildPair("_dwn", d.From.PathOf(d.LegsFrom), legsTo, "_dwn", false, null, null, Maps.Value,
                                           out var legs));
        bool across = StudioPartBodies.Across(d.Pick.Dir, chestDir, [legsDir], explicitRace: false);
        Assert.True(across);
        var rule = RetargetInputs.StudioSkin(replaceSkin: true, cutHidden: true, clearBody: false, gameGear: false, "_top",
                                             crossSex: false);
        SecondSkinWriter.Vec3[]? final = null;
        var plan = RetargetInputs.StudioPlan(d.Garment, d.Bytes, [chest, legs], "_top", new HashSet<int>(), layOnBody: true,
                                             acrossBodies: across, rule, cut: true, pieces: null, carryPiercings: false,
                                             liftUnderbust: true,
                                             trace: (stage, nodes) => { if (stage == "layer-order") final = nodes; });
        Assert.NotNull(final);
        var targetPaths = StudioPartBodies.TargetPaths(chestTo, chestDir, [(legsDir, legsTo)]);
        plan = RefitCore.MatchSkinToBody(plan, d.From.PathOf(d.Pick.Option), targetPaths, Maps.Value, false,
                                         NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>());
        Assert.NotNull(ModelPartReader.Read(plan.Model));
        return Measure(d, plan, chest, legs, chestTo, final!);
    }

    private static float Distance(float ax, float ay, float az, float bx, float by, float bz)
        => MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz));

    private void Say(string what, Outcome o)
        => output.WriteLine($"{what}: folded {o.Folded}, unplaced {o.Unplaced}, kept {o.Kept}, posed {o.Posed}, "
                          + $"waist stretch {o.Stretch * 1000f:F2} mm; skin {string.Join(", ", SkinOf(o.Plan.Model))}; "
                          + $"swap {o.Plan.Report.Swap}; parts "
                          + string.Join(", ", ModelPartReader.Read(o.Plan.Model)!.Parts.GroupBy(p => p.Material)
                                                             .Select(g => $"{g.Key} x{g.Sum(p => p.Triangles.Length / 3)}")));

    private static List<string> SkinOf(byte[] model)
        => SecondSkinWriter.Parse(model).MatNames.Where(SecondSkinWriter.IsBodySkinMaterial).ToList();

    private static string FirstOf(string need, string slot)
    {
        var body = BodySizeCatalog.Read(LocalData.Path(need));
        return body.PathOf(body.For(slot, Race)[0]);
    }

    [LocalDataFact(Dresses, Bibo, Rue, Yab, LocalData.UvMaps)]
    [Trait("Plan", "AC-auto-refit-part-bodies-6")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-17")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-18")]
    public void A_dress_whose_chest_goes_to_another_body_and_legs_stay_swaps_both_without_folds()
    {
        var d = Setup();
        string yabChest = FirstOf(Yab, "_top"), yabLegs = FirstOf(Yab, "_dwn");
        string biboLegs = d.From.PathOf(d.LegsFrom);
        // One body: chest and legs both onto YAB+ — today's refit between bodies.
        var one = Refit(d, yabChest, yabLegs, [yabChest]);
        // Two: the chest goes to YAB+, the legs stay on Bibo+, the body the dress was made on — refitted whole between
        // bodies (decision 4), each part's skin swapped from its own body.
        var two = Refit(d, yabChest, biboLegs, [yabChest, biboLegs]);
        Say("one body (YAB+)", one);
        Say("two bodies (YAB+ chest, Bibo+ legs)", two);

        var swapOne = one.Plan.Report.Swap!.Value;
        var swapTwo = two.Plan.Report.Swap!.Value;
        // The skin swapped as on one body: this dress's skin, chest to thigh, is claimed by its chest, and goes in from
        // the chest's body whichever body the legs are on.
        Assert.Equal(swapOne.Removed, swapTwo.Removed);
        Assert.Equal(swapOne.Added, swapTwo.Added);
        Assert.Equal(0, two.Kept);
        Assert.Equal(0, two.Posed);
        Assert.Equal(0, two.Unplaced);
        Assert.True(two.Folded <= one.Folded, $"{two.Folded} folded against {one.Folded} on one body");
        Assert.True(two.Stretch <= MathF.Max(one.Stretch * 1.5f, one.Stretch + 0.002f),
                    $"waist stretched {two.Stretch * 1000f:F2} mm against {one.Stretch * 1000f:F2} on one body");
    }

    /// <summary>
    /// Why the watcher keeps a reached part on the garment's own body when the garment stays on the body mod it was
    /// made on (AutoRefitWatcher, "goes onto that same body too"): the refit between bodies cannot do that one. Measured
    /// 2026-10-17 on the dress kept on Bibo+ with its legs sent to Rue+: on one body 14 030 skin triangles go in for its
    /// 7 046; with the chest on its own file, 7 046 — the skin is not swapped as it is on one body — and on another Bibo+
    /// chest the skin is kept whole, unswapped, with 130 folds. Should the engine learn it, this fails, and the guard can go.
    /// </summary>
    [LocalDataFact(Dresses, Bibo, Rue, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-31")]
    [Trait("Plan", "AC-per-part-target-body-3")]
    public void A_dress_kept_on_its_own_body_with_its_legs_sent_elsewhere_is_not_swapped_as_on_one_body()
    {
        var d = Setup();
        string chest = d.From.PathOf(d.Pick.Option), rueLegs = FirstOf(Rue, "_dwn");
        var one = Refit(d, FirstOf(Rue, "_top"), rueLegs, [FirstOf(Rue, "_top")]);
        var same = Refit(d, chest, rueLegs, [chest, rueLegs]);
        string otherChest = d.From.PathOf(d.From.For("_top", Race).First(o => o != d.Pick.Option));
        var other = Refit(d, otherChest, rueLegs, [otherChest, rueLegs]);
        Say("one body (Rue)", one);
        Say("chest kept on its own file, legs to Rue", same);
        Say("chest on another Bibo+ size, legs to Rue", other);

        Assert.True(same.Plan.Report.Swap!.Value.Added < one.Plan.Report.Swap!.Value.Added, "swapped as on one body");
        Assert.True(other.Posed > 0 || other.Folded > one.Folded, "the skin was swapped and folds nothing");
    }

    [LocalDataFact(Dresses, Bibo, Rue, TandF, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-19")]
    public void Parts_on_bodies_of_two_layouts_keep_each_bodys_own_skin()
    {
        var d = Setup();
        string tfChest = FirstOf(TandF, "_top"), biboLegs = d.From.PathOf(d.LegsFrom);
        string? gen2 = BodyCorrespondence.LayoutOf(ModelPartReader.Read(File.ReadAllBytes(tfChest))!, false);
        string? bibo = BodyCorrespondence.LayoutOf(ModelPartReader.Read(File.ReadAllBytes(biboLegs))!, false);
        output.WriteLine($"layouts: chest {gen2}, legs {bibo}");
        Assert.NotEqual(gen2, bibo);
        var biboSkin = SkinOf(File.ReadAllBytes(biboLegs));
        Assert.DoesNotContain(biboSkin, SkinOf(File.ReadAllBytes(tfChest)).Contains);

        var both = Refit(d, tfChest, biboLegs, [tfChest, biboLegs]);
        var leadOnly = Refit(d, tfChest, biboLegs, [tfChest]);
        Say("skin of both bodies", both);
        Say("skin of the chest's body alone", leadOnly);
        var alone = Refit(d, tfChest, FirstOf(TandF, "_dwn"), [tfChest]);
        Say("one body (T&F)", alone);

        // The swap itself is what T&F alone gives: this dress's skin goes in from the chest's body.
        Assert.Equal(alone.Plan.Report.Swap!.Value.Added, both.Plan.Report.Swap!.Value.Added);
        // Both bodies' skin counts: Bibo+'s skin material, the legs' body's, is left as it is, not renamed to T&F's.
        Assert.Contains(SkinOf(both.Plan.Model), biboSkin.Contains);
        // The chest's alone: Bibo+'s reads as the garment's own leftover skin and is renamed to T&F's — what the union
        // of every body's skin (RefitCore.MatchSkinToBody) is for.
        Assert.DoesNotContain(SkinOf(leadOnly.Plan.Model), biboSkin.Contains);
        Assert.Equal(0, both.Unplaced);
    }

    // ── the Studio: a part onto a body of its own (per-part-target-body) ─────

    /// <summary>
    /// The Studio's refit with the legs on a body of their own: made on Bibo+, the chest onto YAB+ (the body picked above),
    /// the legs onto Bibo+ (picked for them). Measured against the same Studio refit onto YAB+ alone. Thresholds fixed
    /// before the first run.
    /// </summary>
    [LocalDataFact(Dresses, Bibo, Rue, Yab, LocalData.UvMaps)]
    [Trait("Plan", "AC-per-part-target-body-2")]
    [Trait("Plan", "EDGE-per-part-target-body-13")]
    public void Studio_refit_with_chest_and_legs_on_two_bodies_swaps_both()
    {
        var d = Setup();
        var yab = Body(Yab);
        string yabChest = FirstOf(Yab, "_top"), yabLegs = FirstOf(Yab, "_dwn");
        string biboLegs = d.From.PathOf(d.LegsFrom);
        var one = StudioRefit(d, yab.Dir, yabChest, yab.Dir, yabLegs);
        var two = StudioRefit(d, yab.Dir, yabChest, d.Pick.Dir, biboLegs);
        Say("Studio, one body (YAB+)", one);
        Say("Studio, YAB+ chest and Bibo+ legs", two);

        Assert.Equal(0, two.Unplaced);
        Assert.Equal(0, two.Kept);
        Assert.Equal(0, two.Posed);
        Assert.True(two.Folded <= one.Folded, $"{two.Folded} folded against {one.Folded} on one body");
        Assert.True(two.Stretch <= MathF.Max(one.Stretch * 1.5f, one.Stretch + 0.002f),
                    $"waist stretched {two.Stretch * 1000f:F2} mm against {one.Stretch * 1000f:F2} on one body");

        // The garment's own leftover skin is drawn as the lead's.
        Assert.Contains(SkinOf(two.Plan.Model), SkinOf(File.ReadAllBytes(yabChest)).Contains);
        // Not asserted here: that the legs' body's own skin stays that body's. YAB+ and Bibo+ draw their skin with one
        // material name (/mt_c0201b0001_bibo.mtrl, measured 2026-10-31), so this pair cannot tell the two apart; bodies
        // whose skin is named apart are held to it by Parts_on_bodies_of_two_layouts_keep_each_bodys_own_skin.
    }

    /// <summary>
    /// Three bodies in the Studio: made on Bibo+, the chest onto YAB+, the legs onto Rue+, across the Rue line too, which
    /// the Studio only warns of. Its folds and the cloth's stretch at the waist, where the two bodies meet, against the
    /// one-body Studio refits onto YAB+ and onto Rue+. Thresholds fixed before the first run; a failure is a re-plan
    /// (hold the Studio across the Rue line), never a looser bound.
    /// </summary>
    [LocalDataFact(Dresses, Bibo, Rue, Yab, LocalData.UvMaps)]
    [Trait("Plan", "AC-per-part-target-body-2")]
    [Trait("Plan", "AC-per-part-target-body-9")]
    public void Studio_refit_onto_three_bodies_made_for_x_main_y_legs_z()
    {
        var d = Setup();
        var yab = Body(Yab);
        var rue = Body(Rue);
        string yabChest = FirstOf(Yab, "_top"), yabLegs = FirstOf(Yab, "_dwn");
        string rueChest = FirstOf(Rue, "_top"), rueLegs = FirstOf(Rue, "_dwn");
        var oneYab = StudioRefit(d, yab.Dir, yabChest, yab.Dir, yabLegs);
        var oneRue = StudioRefit(d, rue.Dir, rueChest, rue.Dir, rueLegs);
        var three = StudioRefit(d, yab.Dir, yabChest, rue.Dir, rueLegs);
        Say("Studio, one body (YAB+)", oneYab);
        Say("Studio, one body (Rue+)", oneRue);
        Say("Studio, made on Bibo+, YAB+ chest, Rue+ legs", three);

        Assert.Equal(0, three.Unplaced);
        Assert.Equal(0, three.Kept);
        Assert.True(three.Folded <= Math.Max(oneYab.Folded, oneRue.Folded),
                    $"{three.Folded} folded against {oneYab.Folded} (YAB+) and {oneRue.Folded} (Rue+)");
        float maxOne = MathF.Max(oneYab.Stretch, oneRue.Stretch);
        Assert.True(three.Stretch <= MathF.Max(maxOne * 1.5f, maxOne + 0.002f),
                    $"waist stretched {three.Stretch * 1000f:F2} mm against {maxOne * 1000f:F2} on one body");
    }
}
