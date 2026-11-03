using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Keep shape's release (<see cref="BodyRetarget.ReleaseSink"/>) against the mods it was measured on: "Ash by Succubi"
/// (e0460), whose left cup cover is a closed 11.3 cm piece of cloth that sank into the breast moved whole, and "pop"
/// (e0367), whose arm bands, underwires and chain links are the metal Keep shape is for. Skipped where they are not
/// installed. Each refit is the one <c>Proteus.Refit --keep-default</c> runs, with the Studio's default ticks.
/// </summary>
[Trait("Speed", "Slow")]
public class KeepShapeReleaseLocalTests
{
    private const string AshPack = LocalData.ModPacks + ":Ash by Succubi RueYab ML WC (Model Only).pmp";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";
    private const string Uranus = LocalData.Mods + ":hs-Uranus Redux-4.4.0-VKT";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Pop = LocalData.Mods + ":pop_v1.2/p1/pop/top & accessories/chara/equipment/e0367/model/c0201e0367_top.mdl";

    private const string YiggleLarge = @"files\chest - smallclothes\yiggle - large\chara\equipment\e0000\model\c0201e0000_top.mdl";
    private const string YabMedium = @"files\chest - smallclothes\medium\chara\equipment\e0000\model\c0201e0000_top.mdl";
    private const string UranusRue = @"files\common\7\c0201e0000_top.mdl";

    private const string Cover = "2.1.7";
    private const string Choker = "2.1.3";

    /// <summary>
    /// The written models of these refits on <c>test</c> before this rule (build 1229, 7e76b72), SHA-256: with release
    /// off, the refit must still write exactly these — the stage the release re-runs was pulled out of the solve, and
    /// nothing else on the Keep shape path may have moved.
    /// <para/>
    /// Re-pinned for build 1261: Ash and pop have big double-walled pieces, which a thick shell's walls now move together
    /// when the skin is cleared out of a face (<see cref="BodyRetarget.FaceCheck.WallPartner"/>) and whose inner wall now
    /// covers what lies beneath; the rim lift's fold guard now judges against the authored face. Measured before
    /// re-pinning, against the same refits with <see cref="BodyRetarget.Tuning.NoWallPair"/>,
    /// <see cref="BodyRetarget.Tuning.NoWallCover"/> and <see cref="BodyRetarget.Tuning.NoRimTurnGuard"/> set, which wrote
    /// the builds before 1261 byte for byte (pinned here until the suite was trimmed): up to 354 points moved (worst
    /// 7.8 mm, pop onto Neolithe XS); the cloth inside the body the same on every case; folds on the solve's last stage the
    /// same or fewer (Ash Yab L onto Uranus 792 -> 771); a thick shell's walls through each other fewer or the same.
    /// <para/>
    /// Re-pinned for build 1266: Ash Yab L onto Uranus crosses to a body tessellated otherwise, where cloth the transfer
    /// snapped is now pushed clear of the new skin (<see cref="BodyRetarget.Tuning.NoSnapPush"/>). Measured against
    /// <see cref="BeforeSnapPush"/>: folds on the solve's last stage 801 -> 800, cloth inside 43 -> 42, skin through the
    /// cloth 930 -> 930. Every other case is between sizes of one mesh and did not move.
    /// <para/>
    /// Re-pinned for build 1323: pop's own skin stops short of its cloth, and 857 of its points past that open edge are now
    /// read against the YAB body it was made on (<see cref="BodyRetarget.TargetBody.Fitted"/>). Measured against
    /// <see cref="BeforeOpenEdgeFit"/>: cloth inside the body 144 -> 144 (Uranus), 131 -> 130 (Neolithe XS), 150 -> 150
    /// (Bibo S); folds on the solve's last stage the same; edges stretched against the authored garment the same; one more
    /// triangle turned over against it on Uranus and on Neolithe XS. Accepted (build 1323).
    /// <para/>
    /// Re-pinned for build 1365: a kept piece whose polar turn fit is no rotation is turned by Horn's quaternion fit
    /// (<see cref="BodyRetarget.TurnOf"/>). pop's refits took 89 such turns, chain links mostly; moved by the old answer they
    /// came out sheared by a median 4.5% (83 of 89 over 1%, up to 168%), by the new one not at all. Measured against
    /// <see cref="BeforeTurnFallback"/>, as the test asserts: folds on the solve's last stage and edges stretched against
    /// the authored garment no worse; cloth inside the body and triangles turned over against the authored garment no
    /// worse but on Uranus, 128 -> 133 and 468 -> 469 (<see cref="SunkByTurnFallback"/>, <see cref="TurnedByTurnFallback"/>).
    /// </summary>
    private static readonly Dictionary<string, string> Before = new()
    {
        ["ash Rue L > Uranus"] = "BA69821F8C9D0409C3C5EB2B18F27957AF259171355D6A54645A4E15EF562E0D",
        ["ash Yab L > Uranus"] = "DE3834F1EAB9F7B7EBA67EAD9D19BA3FB3726BED02E5AE45CEC9EBE9ABC45708",
        ["pop > Uranus"] = "BEFB8FEB668ECCAA22BB673A66D8912DFB52FA7F5363D9F5D8D13F06DE5E9A21",
        ["pop > Neolithe XS"] = "5130583791CCB00341695D5CB2CCF50FFEE6B6C8277712C10CDA4CED457335DC",
        ["pop > Bibo S"] = "19AF329D4F9BC2DD56940DA16105A38CE3E0C5B83E95CE372C752BD1E23D711F",
    };

    /// <summary>The refits <see cref="BodyRetarget.Tuning.NoTurnFallback"/> changes, as builds before 1365 wrote them,
    /// nipple smoothing off — the proof that nothing but that rule moved when <see cref="Before"/> was re-pinned.</summary>
    private static readonly Dictionary<string, string> BeforeTurnFallback = new()
    {
        ["pop > Uranus"] = "5DD8CD877CA3091CC45BEBB0CE6FAE996AC3DF665004C96FD670F572F83D10A6",
        ["pop > Neolithe XS"] = "ACCF44614E696ADB0400CF547656123AE580D2D30447F29A55A3AEE7D59B297E",
        ["pop > Bibo S"] = "E1F0D50CF24783720C1E2A71C3A88BB59E26C1EA1409A3F5A667602A594DDA61",
    };

    /// <summary>The cloth points inside the new body that turning kept pieces truly (<see cref="BodyRetarget.TurnOf"/>)
    /// adds, accepted when <see cref="Before"/> was re-pinned for build 1365: pop onto Uranus 128 -> 133 — chain links
    /// that, sheared, had bent along the body, and moved whole sit a little into it (pieces under
    /// <see cref="BodyRetarget.ReleaseMinSize"/> stay whole however deep they sink).</summary>
    private static readonly Dictionary<string, int> SunkByTurnFallback = new()
    {
        ["pop > Uranus"] = 5, ["pop > Neolithe XS"] = 0, ["pop > Bibo S"] = 0,
    };

    /// <summary>The triangles turned over against the authored garment that turning kept pieces truly adds, accepted with
    /// <see cref="SunkByTurnFallback"/>: pop onto Uranus 468 -> 469.</summary>
    private static readonly Dictionary<string, int> TurnedByTurnFallback = new()
    {
        ["pop > Uranus"] = 1, ["pop > Neolithe XS"] = 0, ["pop > Bibo S"] = 0,
    };

    /// <summary>The refits <see cref="BodyRetarget.Tuning.NoOpenEdgeFit"/> changes, as builds before 1323 wrote them,
    /// release off and nipple smoothing off — the proof that nothing but that rule moved when <see cref="Before"/> was
    /// re-pinned.</summary>
    private static readonly Dictionary<string, string> BeforeOpenEdgeFit = new()
    {
        ["pop > Uranus"] = "3ACD75DF39D6248760C50355E06B59FE1E61C311D6AA6D1337AA9ACADD887647",
        ["pop > Neolithe XS"] = "448E7E303B1D235DA036F3C1D12236E937D7FBF8ACB34D04C6E4359A244906FA",
        ["pop > Bibo S"] = "C619167AB0778D46CCF9C620423730FD4FFB1634F70706878404BAF61C1E4B1A",
    };

    /// <summary>The triangles turned over against the authored garment that <see cref="BodyRetarget.Tuning.NoOpenEdgeFit"/>
    /// off adds, accepted when <see cref="Before"/> was re-pinned for build 1323.</summary>
    private static readonly Dictionary<string, int> TurnedByOpenEdgeFit = new()
    {
        ["pop > Uranus"] = 1, ["pop > Neolithe XS"] = 1, ["pop > Bibo S"] = 0,
    };

    /// <summary>The refits <see cref="BodyRetarget.Tuning.NoSnapPush"/> changes, as builds before 1266 wrote them, release
    /// off — the proof that nothing but that rule moved when <see cref="Before"/> was re-pinned.</summary>
    private static readonly Dictionary<string, string> BeforeSnapPush = new()
    {
        ["ash Yab L > Uranus"] = "5A87F2DB114D70D0E8C72AF8F156E308C76E4E5230D1D991EB6ECDC25C140217",
    };

    private sealed record Run(BodyRetarget.Planned Plan, List<string> Names)
    {
        public BodyRetarget.ShapeOutcome Of(string piece) => Plan.Report.Shapes![Names.IndexOf(piece)];
        public string Hash => Convert.ToHexString(SHA256.HashData(Plan.Model));
    }

    /// <summary>One refit as <c>Proteus.Refit --keep-default</c> runs it, the default ticks less <paramref name="untick"/>.
    /// The new body's piercings carried, as every refit did when <see cref="Before"/> was pinned, unless
    /// <paramref name="carryPiercings"/> says otherwise. A seam's split vertices weighted each on its own, as builds before
    /// 1382 did (<see cref="BodyRetarget.Tuning.NoSeamWeights"/>): every hash here was taken before that rule, which
    /// SeamWeightsTests and DeiGratiaRefitLocalTests judge.</summary>
    private static Run Refit(byte[] bytes, string fromNeed, string fromRel, string toNeed, string toRel,
                             bool noRelease = false, string? untick = null, bool noNippleSmooth = false,
                             bool beforeSnapPush = false, bool carryPiercings = true, bool noOpenEdgeFit = false,
                             bool noTurnFallback = false, bool seams = false)
    {
        var garment = ModelPartReader.Read(bytes)!;
        string fromRoot = LocalData.Path(fromNeed), toRoot = LocalData.Path(toNeed);
        static BodyRetarget.BodyDraw? Mask(string root) => RefitCore.MaskOf(root, BodySizeCatalog.Read(root), "_top", null);
        Assert.Null(BodyRetarget.BuildPair("_top", BodySizeCatalog.ResolveCaseInsensitive(fromRoot, fromRel),
                                           BodySizeCatalog.ResolveCaseInsensitive(toRoot, toRel), "_top", false,
                                           Mask(fromRoot), Mask(toRoot), null, out var pair));

        var labels = RefitCore.DefaultKeepShape(garment);
        if (untick != null) Assert.True(labels.Remove(untick), $"{untick} is not ticked by default");
        var pieces = RefitCore.ShapePieces(garment, labels, out var names);
        bool across = !string.Equals(fromRoot, toRoot, StringComparison.OrdinalIgnoreCase);
        var plan = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoRelease: noRelease, NoNippleSmooth: noNippleSmooth,
                                                                       NoSnapPush: beforeSnapPush,
                                                                       NoOpenEdgeFit: noOpenEdgeFit,
                                                                       NoTurnFallback: noTurnFallback,
                                                                       NoSeamWeights: !seams), () => BodyRetarget.Plan(
            garment, bytes, [pair], "_top", replaceSkin: true, acrossBodies: across, keepShape: pieces,
            carryPiercings: carryPiercings));
        return new Run(plan, names);
    }

    private static byte[] Ash(string size) => LocalData.PackEntry(AshPack, $"default group/Top Sizes/{size}.mdl");

    /// <summary>
    /// Ash Rue L onto Uranus: kept whole, the left cup cover sinks into the bigger breast; let go, it bends with it, exactly
    /// as unticked, while the choker stays kept. The sunk cover also left the nipple reading bare, so the new body's
    /// nipple piercing and its chain came in with the skin; let go, the cover covers it again, as unticked.
    /// </summary>
    [LocalDataFact(AshPack, Rue, Uranus)]
    [Trait("Plan", "AC-keep-shape-release-1")]
    [Trait("Plan", "AC-keep-shape-release-2")]
    [Trait("Plan", "AC-keep-shape-release-3")]
    [Trait("Plan", "AC-keep-shape-release-9")]
    [Trait("Plan", "EDGE-seam-weights-15")]
    public void Ash_cover_bends_with_Uranus()
    {
        var bytes = Ash("Rue L");

        // The harness reproduces the report first: kept whole, the cover sinks into the bigger breast — and the refit
        // with release off is byte for byte the one this rule replaced.
        var whole = Refit(bytes, Rue, YiggleLarge, Uranus, UranusRue, noRelease: true);
        var wholeCover = whole.Of(Cover);
        Assert.True(wholeCover.Sink > 0.005f, $"moved whole the cover should sink past 5 mm, not {wholeCover.Sink * 1000:F2}");
        Assert.True(wholeCover.Share * 893 > 100, $"only {wholeCover.Share * 893:F0} of the cover's points sank");
        Assert.Equal(Before["ash Rue L > Uranus"], whole.Hash);
        // [EDGE-seam-weights-15] With 1382's seam weights (measured: 13,550 vertices on 4,577 seams took one weighting, the
        // most any moved 0.048): the same refit, weights apart.
        SeamWeightsTests.SameGeometry(whole.Plan.Model, Refit(bytes, Rue, YiggleLarge, Uranus, UranusRue, noRelease: true,
                                                              seams: true).Plan.Model);

        var released = Refit(bytes, Rue, YiggleLarge, Uranus, UranusRue);
        var unticked = Refit(bytes, Rue, YiggleLarge, Uranus, UranusRue, untick: Cover);

        Assert.True(released.Of(Cover).Released);
        // Let go, it comes out exactly as if the user had unticked it.
        Assert.Equal(unticked.Hash, released.Hash);
        Assert.Equal(unticked.Plan.Report.Folded, released.Plan.Report.Folded);

        // [AC-keep-shape-release-2] The choker is judged and stays kept: only the cover is let go.
        var choker = released.Of(Choker);
        Assert.True(choker.Judged);
        Assert.False(choker.Released, $"the choker sinks {choker.Sink * 1000:F2} mm");
        Assert.Equal([Cover], RefitCore.KeptPieces(released.Plan.Report, released.Names).Where(p => p.Released).Select(p => p.Name));

        // [AC-keep-shape-release-3] No nipple piercing carried in under the cover let go.
        int Adorned(Run r) => r.Plan.Report.Swap?.Adorned ?? 0;
        Assert.Equal(Adorned(unticked), Adorned(released));
        Assert.True(Adorned(released) < Adorned(whole),
                    $"released {Adorned(released)} adornment triangles carried, kept whole {Adorned(whole)}");
    }

    /// <summary>
    /// The same refit with the piercings not asked for: Uranus's nipple ring and its chain, which the cover kept whole
    /// left bare, stay out.
    /// </summary>
    [LocalDataFact(AshPack, Rue, Uranus)]
    [Trait("Plan", "AC-refit-piercings-option-1")]
    [Trait("Plan", "AC-refit-piercings-option-2")]
    public void Ash_cover_carries_no_piercing_with_the_option_off()
    {
        // [AC-refit-piercings-option-1] [AC-refit-piercings-option-2] First that the case is reached: asked for, the ring
        // comes in, and the refit is byte for byte the one pinned before the option existed.
        var bytes = Ash("Rue L");
        var on = Refit(bytes, Rue, YiggleLarge, Uranus, UranusRue, noRelease: true);
        Assert.Equal(Before["ash Rue L > Uranus"], on.Hash);
        Assert.True(on.Plan.Report.Swap?.Adorned > 0, $"no adornment came in: {on.Plan.Report.Swap}");
        static bool Pierced(byte[] model)
            => ModelPartReader.Read(model)!.Parts.Any(p => p.Island < 0 && BodyRetarget.IsBodyPiercingMaterial(p.Material));
        Assert.True(Pierced(on.Plan.Model));

        var off = Refit(bytes, Rue, YiggleLarge, Uranus, UranusRue, noRelease: true, carryPiercings: false);
        Assert.Equal(0, off.Plan.Report.Swap?.Adorned ?? 0);
        Assert.False(Pierced(off.Plan.Model));
    }

    [LocalDataFact(AshPack, Yab, Uranus)]
    [Trait("Plan", "AC-keep-shape-release-2")]
    [Trait("Plan", "AC-keep-shape-release-9")]
    public void Ash_other_source_keeps_its_choker()
    {
        var bytes = Ash("Yab L");

        Assert.Equal(Before["ash Yab L > Uranus"], Refit(bytes, Yab, YiggleLarge, Uranus, UranusRue, noRelease: true).Hash);
        Assert.Equal(BeforeSnapPush["ash Yab L > Uranus"],
                     Refit(bytes, Yab, YiggleLarge, Uranus, UranusRue, noRelease: true, beforeSnapPush: true).Hash);

        var run = Refit(bytes, Yab, YiggleLarge, Uranus, UranusRue);
        Assert.Equal([Cover], RefitCore.KeptPieces(run.Plan.Report, run.Names).Where(p => p.Released).Select(p => p.Name));
        Assert.Equal(Refit(bytes, Yab, YiggleLarge, Uranus, UranusRue, untick: Cover).Hash, run.Hash);
    }

    private const string LaVieEnRose =
        LocalData.Mods + ":La Vie en Rose - by Solona/size/large/chara/equipment/e6098/model/c0201e6098_top.mdl";
    private const string BiboPerkyLarge = @"breasts - small clothes\perky - large\chara\equipment\e0000\model\c0201e0000_top.mdl";

    [LocalDataFact(LaVieEnRose, Bibo, Rue)]
    [Trait("Plan", "AC-keep-shape-release-4")]
    public void Small_ornaments_stay_whole_however_deep_they_sink()
    {
        // La Vie en Rose's little roses and beads, onto Rue+ Yiggle Large: moved whole some sink far past ReleaseSink —
        // 16.5 mm for a 1.7 cm rose when measured — but under ReleaseMinSize every one stays whole.
        var bytes = File.ReadAllBytes(LocalData.Path(LaVieEnRose));
        var garment = ModelPartReader.Read(bytes)!;
        var run = Refit(bytes, Bibo, BiboPerkyLarge, Rue, YiggleLarge);

        float SizeOf(string label)
        {
            var part = garment.Parts.First(p => p.Label == label);
            var d = part.Max - part.Min;
            return MathF.Max(d.X, MathF.Max(d.Y, d.Z));
        }
        var small = RefitCore.KeptPieces(run.Plan.Report, run.Names)
                             .Where(p => SizeOf(p.Name) < BodyRetarget.ReleaseMinSize).ToList();

        Assert.Contains(small, p => p.Sink > BodyRetarget.ReleaseSink);   // or this proves nothing
        Assert.DoesNotContain(small, p => p.Released);
    }

    public static TheoryData<string> PopTargets => new() { "pop > Uranus", "pop > Neolithe XS", "pop > Bibo S" };

    [LocalDataTheory(Pop, Yab, Uranus, Neolithe, Bibo)]
    [MemberData(nameof(PopTargets))]
    [Trait("Plan", "AC-keep-shape-release-2")]
    [Trait("Plan", "AC-keep-shape-release-9")]
    [Trait("Plan", "AC-open-patch-authored-4")]
    [Trait("Plan", "AC-open-patch-authored-9")]
    [Trait("Plan", "AC-keep-shape-flat-card-3")]
    [Trait("Plan", "EDGE-seam-weights-15")]
    public void Pop_hard_pieces_stay_kept(string which)
    {
        var (toNeed, toRel) = which switch
        {
            "pop > Uranus" => (Uranus, UranusRue),
            "pop > Neolithe XS" => (Neolithe, @"extra chest - smallclothes\nsfw almond xs.mdl"),
            _ => (Bibo, @"breasts - small clothes\nude - small\chara\equipment\e0000\model\c0201e0000_top.mdl"),
        };

        // Without the nipple smoothing, which smooths YAB+ Medium's excess off Uranus and Bibo+ Nude: the hashes are
        // this rule's, taken before it — the smoothing came later and has its own (RefitNippleSmoothTests).
        var run = Refit(File.ReadAllBytes(LocalData.Path(Pop)), Yab, YabMedium, toNeed, toRel, noNippleSmooth: true);

        // Arm bands, underwires and 173 chain links: every one judged stays whole, and the refit is the one before.
        var kept = RefitCore.KeptPieces(run.Plan.Report, run.Names);
        Assert.NotEmpty(kept);
        Assert.DoesNotContain(kept, p => p.Released);
        Assert.True(Before[which] == run.Hash, $"refit is now {run.Hash}");
        // [EDGE-seam-weights-15] With 1382's seam weights (measured: ~865 vertices on ~383 seams took one weighting, the most
        // any moved 0.095): the same refit, weights apart.
        SeamWeightsTests.SameGeometry(run.Plan.Model, Refit(File.ReadAllBytes(LocalData.Path(Pop)), Yab, YabMedium, toNeed, toRel,
                                                            noNippleSmooth: true, seams: true).Plan.Model);

        // [AC-open-patch-authored-4] Without the made-on body's reading, the refit is the one before it; [-9] and with it,
        // no worse: no more cloth inside the new body, no more folds, nothing more stretched, and no more turned over than
        // was accepted. Judged as builds before 1365 turned kept pieces, as it was measured.
        var bytes = File.ReadAllBytes(LocalData.Path(Pop));
        var old = Refit(bytes, Yab, YabMedium, toNeed, toRel, noNippleSmooth: true, noTurnFallback: true);
        var off = Refit(bytes, Yab, YabMedium, toNeed, toRel, noNippleSmooth: true, noOpenEdgeFit: true, noTurnFallback: true);
        Assert.Equal(BeforeOpenEdgeFit[which], off.Hash);
        var garment = ModelPartReader.Read(bytes)!;
        string target = BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(toNeed), toRel);
        int Inside(Run r) => BodySizeMatch.ClothAgainst(ModelPartReader.Read(r.Plan.Model)!, target)!.Value.Inside;
        var laidOld = SkinTuckSheet.Laid(bytes, old.Plan);
        var laidOff = SkinTuckSheet.Laid(bytes, off.Plan);
        Assert.True(Inside(old) <= Inside(off));
        Assert.True(old.Plan.Report.Folded <= off.Plan.Report.Folded);
        Assert.True(SkinTuckSheet.ClothStretch(garment, laidOld) <= SkinTuckSheet.ClothStretch(garment, laidOff));
        Assert.True(SkinTuckSheet.FoldedAgainstAuthored(garment, laidOld)
                    <= SkinTuckSheet.FoldedAgainstAuthored(garment, laidOff) + TurnedByOpenEdgeFit[which]);

        // [AC-keep-shape-flat-card-3] Without the quaternion fit, the refit is the one before it; and with it no worse: no
        // more cloth inside the new body, no more folds, nothing more stretched against the authored garment.
        Assert.Equal(BeforeTurnFallback[which], old.Hash);
        Assert.True(run.Plan.Report.ShapeFallbacks > 0, "pop's refit should take quaternion turns, or this proves nothing");
        var laidRun = SkinTuckSheet.Laid(bytes, run.Plan);
        Assert.True(Inside(run) <= Inside(old) + SunkByTurnFallback[which], $"inside {Inside(old)} -> {Inside(run)}");
        Assert.True(run.Plan.Report.Folded <= old.Plan.Report.Folded,
                    $"folded {old.Plan.Report.Folded} -> {run.Plan.Report.Folded}");
        int stretchedOld = SkinTuckSheet.ClothStretch(garment, laidOld), stretchedRun = SkinTuckSheet.ClothStretch(garment, laidRun);
        Assert.True(stretchedRun <= stretchedOld, $"stretched {stretchedOld} -> {stretchedRun}");
        // [AC-open-patch-authored-9] on the refit as it is made now: no more turned over against the authored garment.
        int turnedOld = SkinTuckSheet.FoldedAgainstAuthored(garment, laidOld);
        int turnedRun = SkinTuckSheet.FoldedAgainstAuthored(garment, laidRun);
        Assert.True(turnedRun <= turnedOld + TurnedByTurnFallback[which], $"turned over against the author {turnedOld} -> {turnedRun}");

        // And as the refit really runs, smoothing on: still nothing let go.
        var on = Refit(File.ReadAllBytes(LocalData.Path(Pop)), Yab, YabMedium, toNeed, toRel);
        var keptOn = RefitCore.KeptPieces(on.Plan.Report, on.Names);
        Assert.NotEmpty(keptOn);
        Assert.DoesNotContain(keptOn, p => p.Released);
    }
}
