using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// LINXX's "Codeine": a mini dress whose one skin mesh runs from the neck to the feet (its EQP hides the legs model),
/// made on YAB+ — the author's Bibo+-material pack, and upscales for other bodies — refitted the automatic refit's way.
/// Reported with two slits through the backs of the thighs: onto Muse+ "Defined" legs the dress's laid skin bridges a
/// deeper fold under the buttock, and the cut of the legs it only reaches onto took the skin there (see
/// <see cref="BodyRetarget.GrowSpannedOpenSkin"/>). Measured on the skin the written file draws, against each body as
/// its mod draws it.
/// </summary>
public class CodeineDressHolesLocalTests(ITestOutputHelper output)
{
    private const string Main = LocalData.ModPacks + ":[LINXX] Codeine.pmp";
    private const string Upscales = LocalData.ModPacks + ":Codeine";
    private const string Dress = "base files/chara/equipment/e6169/model/c0201e6169_top.mdl";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";
    private const string Tre = LocalData.Mods + ":hs-[YAS] Tre-5.5.3-8xB";
    private const string LavaBod = LocalData.Mods + ":LavaBod+ 2.2";
    internal const string Muse = LocalData.Mods + ":hs-Muse+-3.2.2-LbM";
    private const string Rosaline = LocalData.Mods + ":hs-Rosaline-1.1.2-hM5N";
    private const string Race = "0201";

    /// <summary>Every body the automatic refit reads the garment's made-on body among, as the watcher reads every installed one.</summary>
    internal static readonly string[] AllBodies = [Bibo, Neolithe, Rue, Yab, Tre, LavaBod, Muse, Rosaline];

    private static readonly BodyRetarget.Tuning Before = new() { NoCutOpenSkin = true };

    private static readonly Lazy<UVRemapService> Maps =
        new(() => new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), LocalData.Path(LocalData.UvMaps)));

    private static (string Dir, BodySizeCatalog Catalog) Body(string need)
        => (Path.GetFileName(LocalData.Path(need)), BodySizeCatalog.Read(LocalData.Path(need)));

    /// <summary>What one automatic refit came to.</summary>
    /// <param name="Undrawn">Skin of the bodies, cm², neither drawn nor under cloth, at their full width.</param>
    /// <param name="Bands">Where: by 10 cm of height, front or back.</param>
    /// <param name="Through">Cloth area, cm², the drawn skin comes through.</param>
    internal sealed record Outcome(string Made, BodyRetarget.SwapReport? Swap, double Undrawn,
                                   SortedDictionary<(float, string), double> Bands, double Through, byte[] Model, long Ms)
    {
        public double Band(float y, string side) => Bands.GetValueOrDefault((y, side));
        public string Where => string.Join(", ", Bands.Where(b => b.Value >= 0.3).Select(b => $"{b.Key.Item1:0.0} {b.Key.Item2} {b.Value:0.0}"));
    }

    /// <summary>
    /// The garment refitted as the automatic refit does it (<see cref="AutoRefitWatcher"/>'s refit): its made-on body read
    /// across every installed body, the other parts it reaches read on that body, each pair with the body mods' own IMC
    /// masks, between body mods when the target is another, the skin whole but for slots it only reaches onto, and the
    /// kept skin named like the new body's. Onto <paramref name="target"/>'s chest <paramref name="chest"/> and legs
    /// <paramref name="legs"/> (indices into its options). Null when nothing could be read as its source.
    /// </summary>
    internal static Outcome? Refit(byte[] bytes, string target, int chest = 0, int legs = 0, string slot = "_top")
    {
        var garment = ModelPartReader.Read(bytes)!;
        var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
        var bodies = AllBodies.Select(Body).ToList();
        var (targetDir, onto) = Body(target);
        var pick = RefitCore.DetectSource(garment, bones, slot, Race, bodies, targetDir, _ => true);
        if (pick == null || !AutoRefitDecisions.Proceed(pick.Confidence)) return null;
        var from = bodies.First(b => b.Dir == pick.Dir).Catalog;
        var others = AutoRefitWatcher.OtherParts(garment, slot, Race, pick, from,
                                                 (_, options) => BodySizeMatch.Rank(garment, options, from.PathOf, bones));
        ushort? Mask(string dir, BodySizeCatalog mod, string s) => RefitCore.MaskOf(dir, mod, s, null);

        // The garment's own slot first, the lead; then each other part it reaches, onto the same body.
        var pairs = new List<BodyRetarget.SlotPair>();
        string? leadTo = null;
        foreach (var (s, fromRel) in new[] { (slot, pick.Option.Rel) }.Concat(others.Where(o => o.Key != slot)
                                                                                   .Select(o => (o.Key, o.Value))))
        {
            if (s is not ("_top" or "_dwn")) continue;
            var sourceOption = from.For(s, Race).First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(fromRel));
            var options = onto.For(s, Race);
            string to = onto.PathOf(options[Math.Min(s == "_top" ? chest : legs, options.Count - 1)]);
            var why = BodyRetarget.BuildPair(s, from.PathOf(sourceOption), to, s, false, Mask(pick.Dir, from, s),
                                             Mask(targetDir, onto, s), Maps.Value, out var pair);
            if (s == slot) Assert.Null(why);
            if (why != null) continue;
            pairs.Add(pair);
            leadTo ??= to;
        }
        bool across = !string.Equals(pick.Dir, targetDir, StringComparison.OrdinalIgnoreCase);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var plan = RefitCore.PlanRefit(garment, bytes, pairs, slot, across, gameGear: false, out _);
        long ms = clock.ElapsedMilliseconds;
        plan = RefitCore.MatchSkinToBody(plan, from.PathOf(pick.Option), [leadTo!], Maps.Value, false,
                                         NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>());
        var model = ModelPartReader.Read(plan.Model)!;
        var bands = new SortedDictionary<(float, string), double>();
        double undrawn = pairs.Sum(p => VanillaGearRefitLocalTests.UncoveredUndrawn(model, p.Target, bands, armFrom: float.MaxValue));
        return new Outcome($"{pick.Dir} / {pick.Option.Label} ({pick.Confidence}), parts {string.Join(", ", others.Keys)}",
                           plan.Report.Swap, undrawn, bands, VanillaGearRefitLocalTests.SkinThrough(model), plan.Model, ms);
    }

    /// <summary>The same refit with the pass and without it, and the two compared: no more skin left out in the open, no
    /// more skin through the cloth.</summary>
    internal static (Outcome Now, Outcome Before) NoWorse(ITestOutputHelper output, string what, byte[] bytes, string target,
                                                          int chest = 0, int legs = 0, string slot = "_top")
    {
        var now = Refit(bytes, target, chest, legs, slot);
        var before = BodyRetarget.WithTuning(Before, () => Refit(bytes, target, chest, legs, slot));
        Assert.NotNull(now);
        Assert.NotNull(before);
        output.WriteLine($"{what}: undrawn {before.Undrawn:0.0} -> {now.Undrawn:0.0} cm² [{now.Where}]; through "
                         + $"{before.Through:0.0} -> {now.Through:0.0}; cut {before.Swap?.Cut} -> {now.Swap?.Cut}, spanned "
                         + $"{now.Swap?.Spanned}; {before.Ms} / {now.Ms} ms; made on {now.Made}");
        Assert.True(now.Undrawn <= before.Undrawn + 0.5, $"{what}: undrawn {now.Undrawn:0.0} cm² against {before.Undrawn:0.0}");
        Assert.True(now.Through <= before.Through + 0.5, $"{what}: through {now.Through:0.0} cm² against {before.Through:0.0}");
        return (now, before);
    }

    internal static int LegsIndex(string body, string label)
    {
        int index = Body(body).Catalog.For("_dwn", Race).ToList().FindIndex(o => o.Label == label);
        Assert.True(index >= 0, $"{label} is not a legs option of {body}");
        return index;
    }

    public static TheoryData<string> Defined => new()
    {
        "Defined - Gen A", "Defined - Gen B", "Defined - Gen C", "Defined - Transmasc", "Defined - Intersex",
    };

    [LocalDataTheory(Main, Bibo, Neolithe, Rue, Yab, Tre, LavaBod, Muse, Rosaline, LocalData.UvMaps)]
    [MemberData(nameof(Defined))]
    [Trait("Plan", "AC-cut-open-skin-1")]
    public void Codeine_onto_Muse_Defined_legs_has_no_thigh_holes(string legs)
    {
        byte[] bytes = LocalData.PackEntryEndingWith(Main, Dress);
        var (now, before) = NoWorse(output, $"Codeine -> Muse+ {legs}", bytes, Muse, 0, LegsIndex(Muse, legs));
        var underCloth = BodyRetarget.WithTuning(new BodyRetarget.Tuning { CutOpenSkinUnderCloth = true },
                                                 () => Refit(bytes, Muse, 0, LegsIndex(Muse, legs)))!;
        output.WriteLine($"  keeping spanned skin under cloth too: undrawn {underCloth.Undrawn:0.0} [{underCloth.Where}], "
                         + $"through {underCloth.Through:0.0}, spanned {underCloth.Swap?.Spanned}");

        // The reported slits, before: the backs of the thighs just below the buttock.
        double was = before.Band(0.6f, "back") + before.Band(0.7f, "back");
        Assert.True(was >= 10.0, $"the backs of the thighs had {was:0.0} cm² open before: the test no longer sees the fault");
        Assert.True(now.Band(0.6f, "back") <= 0.5, $"0.6 back: {now.Band(0.6f, "back"):0.0} cm²");
        Assert.True(now.Band(0.7f, "back") <= 0.5, $"0.7 back: {now.Band(0.7f, "back"):0.0} cm²");
        Assert.True(now.Swap!.Value.Spanned > 0);
    }

    /// <summary>The legs sizes a body is swept over: every distinct file of Muse+'s, the first, middle and last of the
    /// others' — and always Muse+'s Flaccid and Erect and Neolithe's Neobelly Gen C Small, the cases the cut takes most.</summary>
    private static IEnumerable<int> Swept(string body)
    {
        var options = Body(body).Catalog.For("_dwn", Race).ToList();
        var distinct = options.Select((o, i) => (o, i)).GroupBy(x => RefitCore.Normal(x.o.Rel)).Select(g => g.First().i).ToList();
        if (body == Muse) return distinct;
        var picked = new SortedSet<int> { distinct[0], distinct[distinct.Count / 2], distinct[^1] };
        if (body == Neolithe)
        {
            int neobelly = options.FindIndex(o => o.Label == "NEOBELLY · Gen C Small");
            Assert.True(neobelly >= 0, "Neolithe has no NEOBELLY · Gen C Small legs");
            picked.Add(neobelly);
        }
        return picked;
    }

    public static TheoryData<string> Bodies => new() { Bibo, Neolithe, Rue, Yab, Tre, LavaBod, Muse, Rosaline };

    [LocalDataTheory(Main, Bibo, Neolithe, Rue, Yab, Tre, LavaBod, Muse, Rosaline, LocalData.UvMaps)]
    [MemberData(nameof(Bodies))]
    [Trait("Plan", "AC-cut-open-skin-2")]
    [Trait("Plan", "EDGE-cut-open-skin-14")]
    public void Codeine_onto_other_bodies_is_no_worse(string body)
    {
        byte[] bytes = LocalData.PackEntryEndingWith(Main, Dress);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var options = Body(body).Catalog.For("_dwn", Race);
        foreach (int legs in Swept(body))
        {
            var (now, _) = NoWorse(output, $"Codeine -> {Path.GetFileName(LocalData.Path(body))} {options[legs].Label}",
                                   bytes, body, 0, legs);
            if (options[legs].Label.Contains("Flaccid") || options[legs].Label.Contains("Erect"))
                output.WriteLine($"  skin through the cloth on {options[legs].Label}: {now.Through:0.0} cm²");
        }
        output.WriteLine($"{clock.ElapsedMilliseconds / 1000} s");
    }

    /// <summary>The upscales' dresses, by pack under <see cref="Upscales"/> and the ending of the entry: a sample, one or two
    /// sizes of each pack made for a body installed here (each is refitted onto eight bodies, about a minute apiece), not
    /// every entry. Left out: Bimbo+ and Citrus+, made for bodies not among them, and the Rue Medium Yiggle Fix, a
    /// variant of the Rue M pack swept here.</summary>
    public static TheoryData<string, string> UpscaleDresses => new()
    {
        { "Codeine [YAB].pmp", "full outfit - yab wc/medium/chara/equipment/e6169/model/c0201e6169_top.mdl" },
        { "Codeine [YAB].pmp", "outfit - buff wc/medium/chara/equipment/e6169/model/c0201e6169_top.mdl" },
        { "Codeine for Lava+.pmp", "with harness/cupcake rue/chara/equipment/e6169/model/c0201e6169_top.mdl" },
        { "Codeine for Lava+.pmp", "with harness/teardrop/chara/equipment/e6169/model/c0201e6169_top.mdl" },
        { "[LINXX] Codeine Rue M.pmp", "size- option/full - wc/chara/equipment/e6169/model/c0201e6169_top.mdl" },
        { "[Linxx] Codeine [Tre+].pmp", "medium butt options/main/chara/equipment/e6169/model/c0201e6169_top.mdl" },
    };

    [LocalDataTheory(Upscales, Bibo, Neolithe, Rue, Yab, Tre, LavaBod, Muse, Rosaline, LocalData.UvMaps)]
    [MemberData(nameof(UpscaleDresses))]
    [Trait("Plan", "AC-cut-open-skin-2")]
    public void Codeine_upscales_are_no_worse(string pack, string entry)
    {
        byte[] bytes = LocalData.PackEntryEndingWith(Upscales + "/" + pack, entry);
        // Every upscale is read on every body (NoWorse asserts it): Codeine's skin names its body.
        foreach (string body in new[] { Bibo, Neolithe, Rue, Yab, Tre, LavaBod, Muse, Rosaline })
        {
            int legs = body == Muse ? LegsIndex(Muse, "Defined - Gen A") : 0;
            NoWorse(output, $"{pack} {entry.Split("/chara/")[0]} -> {Path.GetFileName(LocalData.Path(body))}", bytes, body, 0, legs);
        }
    }
}
