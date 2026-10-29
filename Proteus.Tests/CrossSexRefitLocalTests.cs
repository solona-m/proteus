using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Refits across the sexes against the real files, gated (the plan's G1–G6 and G5b, see <see cref="RefitGate"/>): a men's
/// shirt and trousers made women's on Bibo+, a women's leg garment and a women's top made men's on The Body SE, and a
/// skirt rigged to YAS's own bones made men's with its bones moved onto the men's skeleton (cross-sex-tops-yas).
/// </summary>
public class CrossSexRefitLocalTests(ITestOutputHelper o)
{
    internal const string Tbse = LocalData.Mods + ":The Body SE";
    internal const string TbseX = LocalData.Mods + ":The Body SE-X IVCS2 V1_1_0";
    internal const string Bibo = LocalData.Mods + ":Bibo+";
    internal const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    internal const string SheerTop = LocalData.Mods + ":BiboPlus Sheer Elegance/Chest size/Small/chara/equipment/e6010/model/c0201e6010_top.mdl";
    internal const string SheerLegs = LocalData.Mods + ":BiboPlus Sheer Elegance/Content Type/SFW- Opaque/chara/equipment/e6107/model/c0201e6107_dwn.mdl";
    internal const string LumberjackMod = LocalData.Mods + ":the lumberjack - by solona";
    internal const string LumberjackShirt = LocalData.Mods + ":the lumberjack - by solona/summer indigo shirt/chara/equipment/e6091/model/c0101e6091_top.mdl";
    internal const string TwillTbse = LocalData.Mods + ":-Twill Trousers- [YAB, TBSE, TBSE-X]/male- legs style/tbse/chara/equipment/e6269/model/c0101e6269_dwn.mdl";
    internal const string Starlit = LocalData.Mods + ":[Nini] Starlit Skirt/rue sizes/wc/chara/equipment/e6212/model/c0201e6212_dwn.mdl";
    internal const string IvcsMen = LocalData.Mods
        + ":[IVCS] Illusio Vitae Custom Skeleton Base V1.1 (2)/chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb";
    internal const string YasSkeleton = LocalData.Mods
        + ":hs-Rue+-2.2.7-y0f/files/files/yet another skeleton/animation_general usage/chara/human/c0201/skeleton/base/b0001/skl_c0201b0001.sklb";

    /// <summary>The Rue+ legs Starlit's WC size was made on: Yiggle, which rigs YAS's physics bones.</summary>
    internal const string RueYiggleWc = "yiggle - watermelon crushers - a/";

    /// <summary>Cloth faces each shipped cell turns over today (measured 2026-10-20), guarded against growing below the
    /// gate's own cap.</summary>
    private const int KnownLumberjackFolds = 8, KnownTwillFolds = 0, KnownSheerLegsFolds = 1;

    /// <summary>The Bibo+ legs a women's SFW garment is made on: its smallclothes, matching TBSE's SFW option.</summary>
    private static string BiboSfwLegs => UvBakeInputs.BiboLegs[3].Rel;

    // BakedBodyCatalog.Dir is left as it is: one folder for the whole test process (TestAssemblySetup), shared by tests
    // running in parallel. Bends across the sexes carry "-alias" in their names, so they never meet a same-sex bend.
    private readonly IPluginLog log = Substitute.For<IPluginLog>();

    // One reader for the class: Lumina's index is costly to open, and Gather asks for files that may not exist.
    private static readonly Lazy<Lumina.GameData> GameData = new(() => new Lumina.GameData(LocalData.Path(LocalData.GameData)));

    private static byte[]? Game(string path) => GameData.Value.GetFile(path)?.Data;

    private static UVRemapService Maps() => new(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps));

    /// <summary>The wearer for <paramref name="to"/> as the panel and the CLI gather it: the game's skeleton, deformer and
    /// Est tables, or a modded base skeleton from <paramref name="skeletonPath"/>.</summary>
    /// <param name="drawnSkeletonPath">The drawn race's base skeleton as the collection gives it (a women's YAS); the
    /// game's when null.</param>
    internal static OtherRaceWearer.Inputs Wearer(ushort to, string gamePath, string? skeletonPath = null,
                                                  string? drawnSkeletonPath = null)
        => OtherRaceWearer.Gather(to, gamePath, Game,
               path => skeletonPath != null && path == OtherRaceWearer.BaseSkeletonPath(to) ? skeletonPath
                     : drawnSkeletonPath != null && path == OtherRaceWearer.BaseSkeletonPath(ModelSkinReader.RaceOf(gamePath))
                         ? drawnSkeletonPath
                     : null,
               _ => null, []);

    /// <summary>Picks the option of <paramref name="catalog"/> that is the file at <paramref name="rel"/>, bent or not
    /// (bending keeps an option's headings and changes only its file).</summary>
    internal static Func<BodyOption, bool> IsFile(BodySizeCatalog catalog, string rel)
    {
        string want = Norm(rel);
        var original = catalog.Options.First(o => Norm(o.Rel) == want);
        return o => o.Group == original.Group && o.Section == original.Section && o.Name == original.Name;
    }

    private static string Norm(string rel) => rel.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private static string GamePathOf(string localPath)
    {
        string p = localPath.Replace('\\', '/');
        return p[p.IndexOf("chara/", StringComparison.OrdinalIgnoreCase)..].ToLowerInvariant();
    }

    private CrossSexRefitJob.Inputs Inputs(string garmentNeed, ushort to, string madeForNeed, string madeForRel,
                                           string ontoNeed, string ontoRel, string? skeletonPath = null,
                                           string? drawnSkeletonPath = null)
    {
        string garmentPath = LocalData.Path(garmentNeed);
        string gamePath = GamePathOf(garmentPath);
        var madeFor = BodySizeCatalog.Read(LocalData.Path(madeForNeed));
        var onto = BodySizeCatalog.Read(LocalData.Path(ontoNeed));
        return new CrossSexRefitJob.Inputs(File.ReadAllBytes(garmentPath), gamePath, to, madeFor, IsFile(madeFor, madeForRel),
            onto, IsFile(onto, ontoRel),
            Wearer(to, gamePath, skeletonPath, drawnSkeletonPath), Maps(), log);
    }

    private (CrossSexRefitJob.Result R, CrossSexRefitJob.Size S) Run(CrossSexRefitJob.Inputs inputs)
    {
        var r = CrossSexRefitJob.Run(inputs);
        Assert.True(r.Refusal.Length == 0, r.Refusal);
        var s = Assert.Single(r.Sizes);
        Assert.True(s.Refusal.Length == 0, s.Refusal);
        o.WriteLine(Describe(Path.GetFileName(inputs.GamePath), r, s));
        Assert.True(s.SaveAllowed, $"the run-time check refuses it: {s.Landed?.VertexCoverage:P1} matched");
        return (r, s);
    }

    /// <summary>The same garment refitted between two bodies of its own sex: the folds G2 judges against.</summary>
    private int SameSexFolds(string garmentNeed, string fromNeed, string fromRel, string toNeed, string toSlotRace, bool male)
    {
        var garment = File.ReadAllBytes(LocalData.Path(garmentNeed));
        string slot = BodySizeCatalog.SlotOf(GamePathOf(LocalData.Path(garmentNeed)))!;
        var from = BodySizeCatalog.Read(LocalData.Path(fromNeed));
        var to = BodySizeCatalog.Read(LocalData.Path(toNeed));
        var target = to.For(slot, toSlotRace).First(b => BodySizeCatalog.RaceOf(b.GamePath) == toSlotRace);
        string fromPath = from.PathOf(from.Options.First(IsFile(from, fromRel)));
        // A pair the two bodies of one sex cannot make (TBSE-X's legs draw less skin than TBSE's SFW option) leaves the
        // gate's floor alone to judge by.
        if (BodyRetarget.BuildPair(slot, fromPath, to.PathOf(target), slot, male, null, null, Maps(), out var pair) is { } refused)
        {
            o.WriteLine($"no baseline onto {target.Label}: {refused}");
            return 0;
        }
        var plan = BodyRetarget.Plan(ModelPartReader.Read(garment)!, garment, [pair], slot, replaceSkin: true,
                                     acrossBodies: true, cutHidden: true);
        int folds = CrossSexRefit.ClothFolded(garment, plan);
        o.WriteLine($"baseline onto {target.Label}: {folds} folds");
        return folds;
    }

    [LocalDataFact(LocalData.GameData, LumberjackShirt, Tbse, TbseX, Bibo, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-cross-sex-refit-2")]
    [Trait("Plan", "AC-cross-sex-refit-3")]
    [Trait("Plan", "AC-cross-sex-refit-5")]
    [Trait("Plan", "EDGE-cross-sex-refit-13")]
    [Trait("Plan", "EDGE-cross-sex-refit-14")]
    [Trait("Speed", "Slow")]
    [Trait("Plan", "EDGE-uvmap-nipples-48")]
    [Trait("Plan", "AC-uvmap-nipples-8")]
    public void Lumberjack_refits_onto_bibo_for_midlander_women() // [AC-cross-sex-refit-2] [AC-cross-sex-refit-3] [AC-cross-sex-refit-5] [EDGE-cross-sex-refit-13] [EDGE-cross-sex-refit-14] [EDGE-uvmap-nipples-48] [AC-uvmap-nipples-8]
    {
        var inputs = Inputs(LumberjackShirt, 201, Tbse, UvBakeInputs.TbseChest, Bibo, UvBakeInputs.BiboChest);
        var (r, s) = Run(inputs);
        int baseline = SameSexFolds(LumberjackShirt, Tbse, UvBakeInputs.TbseChest, TbseX, "0101", male: true);
        RefitGate.Assert(o, r, s, baseline);
        Assert.True(s.Folds <= KnownLumberjackFolds, $"{s.Folds} folds, more than the {KnownLumberjackFolds} known");
        Assert.False(r.Route.Lowered);
        Assert.True(r.Route.ByAlias);
        Assert.Equal("chara/equipment/e6091/model/c0201e6091_top.mdl", r.Route.SavePath(inputs.GamePath));
        Assert.NotNull(r.Route.Eqdp(r.Route.SavePath(inputs.GamePath)));
    }

    [LocalDataFact(LocalData.GameData, TwillTbse, Tbse, TbseX, Bibo, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-cross-sex-refit-2")]
    [Trait("Plan", "AC-cross-sex-refit-4")]
    [Trait("Plan", "AC-cross-sex-refit-5")]
    [Trait("Speed", "Slow")]
    public void Twill_trousers_refit_onto_bibo_legs() // [AC-cross-sex-refit-2] [AC-cross-sex-refit-4] [AC-cross-sex-refit-5]
    {
        var inputs = Inputs(TwillTbse, 201, Tbse, UvBakeInputs.TbseLegs[0].Rel, Bibo, UvBakeInputs.BiboLegs[0].Rel);
        var (r, s) = Run(inputs);
        int baseline = SameSexFolds(TwillTbse, Tbse, UvBakeInputs.TbseLegs[0].Rel, TbseX, "0101", male: true);
        RefitGate.Assert(o, r, s, baseline);
        Assert.True(s.Folds <= KnownTwillFolds, $"{s.Folds} folds, more than the {KnownTwillFolds} known");
        Assert.Equal("chara/equipment/e6269/model/c0201e6269_dwn.mdl", r.Route.SavePath(inputs.GamePath));
    }

    [LocalDataFact(LocalData.GameData, SheerLegs, Bibo, Rue, Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-cross-sex-refit-1")]
    [Trait("Plan", "AC-cross-sex-refit-4")]
    [Trait("Plan", "AC-cross-sex-refit-5")]
    [Trait("Plan", "EDGE-cross-sex-refit-13")]
    [Trait("Speed", "Slow")]
    public void Sheer_elegance_legs_refit_onto_tbse_for_midlander_men() // [AC-cross-sex-refit-1] [AC-cross-sex-refit-4] [AC-cross-sex-refit-5] [EDGE-cross-sex-refit-13]
    {
        var inputs = Inputs(SheerLegs, 101, Bibo, BiboSfwLegs, Tbse, UvBakeInputs.TbseLegs[0].Rel);
        var (r, s) = Run(inputs);
        int baseline = SameSexFolds(SheerLegs, Bibo, BiboSfwLegs, Rue, "0201", male: false);
        RefitGate.Assert(o, r, s, baseline);
        Assert.True(s.Folds <= KnownSheerLegsFolds, $"{s.Folds} folds, more than the {KnownSheerLegsFolds} known");
        Assert.True(r.Route.Lowered);
        Assert.Equal("chara/equipment/e6107/model/c0101e6107_dwn.mdl", r.Route.SavePath(inputs.GamePath));
        Assert.NotNull(r.Route.Eqdp(r.Route.SavePath(inputs.GamePath)));
    }

    /// <summary>
    /// A women's top made a men's (cross-sex-tops-yas). Once not offered: flattening a bust onto a pec turns more of the
    /// cloth over than any other cell (115 of Sheer Elegance's 10,436 faces, measured 2026-10-20). Now offered and saved,
    /// its folds logged and said, never gated; the fit gates still hold.
    /// </summary>
    [LocalDataFact(LocalData.GameData, SheerTop, Bibo, Rue, Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-cross-sex-refit-7")]
    [Trait("Plan", "AC-cross-sex-tops-yas-1")]
    [Trait("Plan", "AC-cross-sex-tops-yas-2")]
    [Trait("Plan", "AC-cross-sex-tops-yas-3")]
    [Trait("Speed", "Slow")]
    public void Sheer_elegance_top_refits_onto_tbse_for_midlander_men() // [AC-cross-sex-tops-yas-1] [AC-cross-sex-tops-yas-2] [AC-cross-sex-tops-yas-3]
    {
        var offered = OtherRaceRefit.Offered("chara/equipment/e6010/model/c0201e6010_top.mdl",
                                             BodySizeCatalog.Read(LocalData.Path(Tbse)), [], null, isVanilla: false);
        Assert.True(Assert.Single(offered, c => c.Race == 101).CrossSex);

        var inputs = Inputs(SheerTop, 101, Bibo, UvBakeInputs.BiboChest, Tbse, UvBakeInputs.TbseChest);
        var (r, s) = Run(inputs);
        int baseline = SameSexFolds(SheerTop, Bibo, UvBakeInputs.BiboChest, Rue, "0201", male: false);
        RefitGate.Assert(o, r, s, baseline, RefitGate.Gates.Inside);
        Assert.True(r.Route.Lowered);
        Assert.Equal("chara/equipment/e6010/model/c0101e6010_top.mdl", r.Route.SavePath(inputs.GamePath));
        Assert.NotNull(r.Route.Eqdp(r.Route.SavePath(inputs.GamePath)));
        o.WriteLine($"women's top → TBSE: {s.Folds} cloth faces folded (said, not gated)");
    }

    /// <summary>
    /// Refit map <c>tbse_to_bibo</c>: a women's top made a men's puts the cloth over Bibo+'s nipples at TBSE's nipple
    /// height — per side, the cloth within 2.5 cm of Bibo+'s nipple, its mean height on the saved model against TBSE's
    /// nipple. Each side within half of what the v2 maps left (<see cref="SheerBustV2Mm"/>, measured 2026-10-28).
    /// </summary>
    [LocalDataFact(LocalData.GameData, SheerTop, Bibo, Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-uvmap-nipples-10")]
    [Trait("Speed", "Slow")]
    public void RefitMap_sheer_elegance_bust_lands_at_tbse_nipple_height() // [AC-uvmap-nipples-10] [EDGE-uvmap-nipples-31] [AC-uvmap-nipples-8]
    {
        var inputs = Inputs(SheerTop, 101, Bibo, UvBakeInputs.BiboChest, Tbse, UvBakeInputs.TbseChest);
        var (_, s) = Run(inputs);
        // The cloth before and after, paired by uv: the refit moves cloth and keeps its uvs, while the skin it carries is
        // the new body's own.
        static bool Cloth(string m) => !SecondSkinWriter.IsBodySkinMaterial(m);
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(inputs.Garment, out var bp, out var bu, out _, keepMaterial: Cloth));
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(s.Saved!.Model, out var ap, out var au, out _, keepMaterial: Cloth));
        static (int, int) Key(float[] uv, int v) => ((int)MathF.Round(uv[v * 2] * 65536f), (int)MathF.Round(uv[v * 2 + 1] * 65536f));
        var afterAt = new Dictionary<(int, int), System.Numerics.Vector3>();
        for (int v = 0; v < ap.Length / 3; v++) afterAt.TryAdd(Key(au, v), new(ap[v * 3], ap[v * 3 + 1], ap[v * 3 + 2]));

        var bibo = new BakeBody([UvBakeInputs.Part(File.ReadAllBytes(Path.Combine(LocalData.Path(Bibo), UvBakeInputs.BiboChest)))!]);
        var tbse = new BakeBody([UvBakeInputs.Part(File.ReadAllBytes(Path.Combine(LocalData.Path(Tbse), UvBakeInputs.TbseChest)))!]);
        var (bl, br) = UvBakeNipples.Find(bibo).Found!.Value;
        var (tl, tr) = UvBakeNipples.Find(tbse).Found!.Value;
        var misses = new List<string>();
        foreach (var (side, b, t, v2) in new[] { ("L", bl, tl, SheerBustV2Mm[0]), ("R", br, tr, SheerBustV2Mm[1]) })
        {
            var heights = new List<float>();
            for (int v = 0; v < bp.Length / 3; v++)
            {
                var p = new System.Numerics.Vector3(bp[v * 3], bp[v * 3 + 1], bp[v * 3 + 2]);
                if (System.Numerics.Vector3.Distance(p, b.Pos) > 0.025f || p.Z < b.Pos.Z - 0.01f) continue;
                if (afterAt.TryGetValue(Key(bu, v), out var q)) heights.Add(q.Y - t.Pos.Y);
            }
            Assert.NotEmpty(heights);
            float dy = heights.Average() * 1000f;
            o.WriteLine($"{side}: {heights.Count} cloth vertices over Bibo+'s nipple land {dy:+0.0;-0.0} mm from TBSE's nipple height "
                        + $"(v2 {v2:+0.0;-0.0} mm, bound {MathF.Abs(v2) / 2:F1} mm)");
            if (!(MathF.Abs(dy) <= MathF.Abs(v2) / 2)) misses.Add($"{side} {dy:F1} mm");
        }
        // Across the sexes the folds are said, not gated (cross-sex-tops-yas): set beside the v2 maps' for the record.
        o.WriteLine($"{s.Folds} cloth faces turned over (v2 {SheerTopFoldsV2})");
        Assert.True(misses.Count == 0, string.Join("; ", misses));
    }

    /// <summary>
    /// The cloth faces the Sheer Elegance top refit for TBSE turned over with the v2 maps (2026-10-28): 101. The v3 maps
    /// turn 143, the bust now over his nipples rather than under them — said, not gated, as every fold across the sexes is.
    /// </summary>
    private const int SheerTopFoldsV2 = 101;

    /// <summary>What the v2 maps left of <see cref="RefitMap_sheer_elegance_bust_lands_at_tbse_nipple_height"/>, left and right (mm).</summary>
    private static readonly float[] SheerBustV2Mm = [-29.1f, -29.1f];

    /// <summary>
    /// The underbust lift for a women's top made a men's, measured both ways inside the pass: kept unless leaving it out
    /// folds less and clips no more (CrossSexRefit.LiftsUnderbust).
    /// </summary>
    [LocalDataFact(LocalData.GameData, SheerTop, Bibo, Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-cross-sex-tops-yas-3")]
    [Trait("Speed", "Slow")]
    public void Underbust_lift_for_a_womans_top_is_measured() // [AC-cross-sex-tops-yas-3]
    {
        var results = new Dictionary<bool, (int Folds, float Inside)>();
        foreach (bool lift in new[] { true, false })
        {
            var inputs = Inputs(SheerTop, 101, Bibo, UvBakeInputs.BiboChest, Tbse, UvBakeInputs.TbseChest) with { LiftUnderbust = lift };
            var r = CrossSexRefitJob.Run(inputs);
            var s = Assert.Single(r.Sizes);
            var m = RefitGate.Measure(r, s, 0);
            results[lift] = (s.Folds, m.Inside);
            o.WriteLine($"lift {lift}: {s.Folds} folds, {m.Inside:P2} inside (made for {m.MadeForInside:P2})");
        }
        bool off = results[false].Folds < results[true].Folds && results[false].Inside <= results[true].Inside;
        Assert.Equal(!off, CrossSexRefit.LiftsUnderbust(201, 101));
    }

    /// <summary>
    /// A skirt rigged to YAS's own bones (Rue's WC Yiggle size) made a men's on The Body SE (cross-sex-tops-yas): once
    /// refused naming the iv_/ya_ bones with either men's skeleton; now each bone the skeleton lacks is moved onto one it
    /// has — and the Rue bodies it was made on the same way — and the skirt saved leaning on no bone the man lacks.
    /// </summary>
    [LocalDataTheory(LocalData.GameData, Starlit, Rue, Tbse, IvcsMen, YasSkeleton, LocalData.TbseUvMaps)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Plan", "AC-cross-sex-refit-6")]
    [Trait("Plan", "EDGE-cross-sex-refit-4")]
    [Trait("Plan", "AC-cross-sex-tops-yas-4")]
    [Trait("Plan", "AC-cross-sex-tops-yas-5")]
    [Trait("Plan", "AC-cross-sex-tops-yas-6")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-3")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-4")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-13")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-19")]
    [Trait("Speed", "Slow")]
    public void Starlit_rigged_to_yas_bones_refits_onto_tbse_legs(bool ivcs) // [AC-cross-sex-tops-yas-4] [AC-cross-sex-tops-yas-5] [AC-cross-sex-tops-yas-6] [EDGE-cross-sex-tops-yas-3] [EDGE-cross-sex-tops-yas-4] [EDGE-cross-sex-tops-yas-13] [EDGE-cross-sex-tops-yas-19]
    {
        var inputs = Inputs(Starlit, 101, Rue, RueRel(RueYiggleWc), Tbse, UvBakeInputs.TbseLegs[0].Rel,
                            ivcs ? LocalData.Path(IvcsMen) : null, LocalData.Path(YasSkeleton));
        var (r, s) = Run(inputs);
        var moved = r.Moved ?? [];
        o.WriteLine($"{(ivcs ? "IVCS" : "game")} skeleton: moved {SkeletonRemap.DescribeHow(moved)}; needs skeleton "
                  + $"[{string.Join(", ", r.NeedsSkeleton ?? [])}]");
        Assert.NotEmpty(moved);
        int baseline = SameSexFolds(Starlit, Rue, RueRel(RueYiggleWc), Rue, "0201", male: false);
        // G4 at the refit's own floor, the user's decision: Rue's legs onto TBSE's match 90.1% of the skin through the maps
        // (measured 2026-10-21) — a body pair's property, not the bone moves.
        RefitGate.Assert(o, r, s, baseline, RefitGate.Gates.Regions, (float)CrossSexRefit.MinCoverage);

        var saved = ModelSkinReader.Read(s.Saved!.Model, null, null)!;
        Assert.All(OtherRaceRefit.Weighted(saved), b => Assert.True(r.Wearer!.Parents.ContainsKey(b), $"{b} is not the man's"));
        for (int v = 0; v < saved.VertexCount; v++)
        {
            float sum = 0f;
            for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++) sum += saved.BoneWeights[v * 8 + k];
            Assert.True(MathF.Abs(sum - 1f) <= 1f / 255f + 1e-4f, $"vertex {v} weighs {sum}");
        }
        var (mean, mostOnSkin, strangers) = RefitGate.Strangers(r, s);
        o.WriteLine($"G5c bones: [{string.Join(", ", strangers)}]");
        o.WriteLine($"G5c: {mean:P2} of near cloth's weight on bones the new body does not rig; {mostOnSkin:P2} at most on the skin");
        Assert.True(mean <= RefitGate.MaxStrangerMean, $"G5c mean {mean:P2}");
        Assert.True(mostOnSkin <= RefitGate.MaxStrangerOnSkin, $"G5c on the skin {mostOnSkin:P2}");

        if (ivcs)
        {
            // IVCS has the buttock bones: YAS's twin moves onto them, and they stay weighted — named as needing IVCS.
            Assert.Contains(moved, m => m is { From: "ya_shiri_phys_l", To: "iv_shiri_l", How: SkeletonRemap.How.Counterpart });
            Assert.DoesNotContain(moved, m => m.From.StartsWith("iv_shiri", StringComparison.Ordinal));
            Assert.Contains("iv_shiri_l", OtherRaceRefit.Weighted(saved));
            Assert.Contains("iv_shiri_l", r.NeedsSkeleton ?? []);
        }
        else
        {
            Assert.Contains(moved, m => m.From == "iv_shiri_l" && m.How == SkeletonRemap.How.Ancestor);
            Assert.Empty(r.NeedsSkeleton ?? []);
        }
    }

    /// <summary>Without the women's custom skeleton in the collection, the moves fall back on each bone's part of the body
    /// — never breast or thigh onto the hip — and the skirt is still saved.</summary>
    [LocalDataFact(LocalData.GameData, Starlit, Rue, Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-5")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-6")]
    [Trait("Speed", "Slow")]
    public void Starlit_drawn_with_the_game_s_women_skeleton_still_refits() // [EDGE-cross-sex-tops-yas-5] [EDGE-cross-sex-tops-yas-6]
    {
        var inputs = Inputs(Starlit, 101, Rue, RueRel(RueYiggleWc), Tbse, UvBakeInputs.TbseLegs[0].Rel);
        var (r, s) = Run(inputs);
        var moved = r.Moved ?? [];
        o.WriteLine($"moved {SkeletonRemap.DescribeHow(moved)}");
        Assert.NotEmpty(moved);
        Assert.Contains(moved, m => m.How is SkeletonRemap.How.StandIn or SkeletonRemap.How.Fallback);
        Assert.DoesNotContain(moved, m => m.From.Contains("daitai", StringComparison.Ordinal) && m.To == "j_kosi");
        var m5 = RefitGate.Measure(r, s, 0);
        Assert.Empty(m5.MissingBones);
        Assert.Empty(m5.ForeignSkins);
    }

    private static string RueRel(string folder)
    {
        var rue = BodySizeCatalog.Read(LocalData.Path(Rue));
        return rue.For("_dwn", "0201").First(b => b.Rel.Replace('\\', '/').Contains(folder, StringComparison.OrdinalIgnoreCase)).Rel;
    }

    [LocalDataFact(LocalData.GameData, LumberjackMod, Tbse, Bibo)]
    [Trait("Plan", "EDGE-cross-sex-refit-1")]
    public void The_authors_own_women_shirt_is_pointed_at() // [EDGE-cross-sex-refit-1]
    {
        const string shirt = "chara/equipment/e6091/model/c0101e6091_top.mdl";
        var redirects = PenumbraModMeta.ReadAllRedirects(LocalData.Path(LumberjackMod));
        var bibo = BodySizeCatalog.Read(LocalData.Path(Bibo));
        var women = Assert.Single(OtherRaceRefit.Offered(shirt, bibo, redirects, null, false), c => c.Race == 201);
        Assert.True(women.CrossSex);
        Assert.Equal(OtherRaceRefit.Block.OwnModel, women.Blocked);
        Assert.Equal("chara/equipment/e6091/model/c0201e6091_top.mdl", women.OwnModelPath);

        var menOnly = redirects.Where(x => !x.GamePath.Contains("c0201", StringComparison.Ordinal)).ToList();
        Assert.Equal(OtherRaceRefit.Block.None,
                     Assert.Single(OtherRaceRefit.Offered(shirt, bibo, menOnly, null, false), c => c.Race == 201).Blocked);
    }

    /// <summary>Bibo+ and TBSE pair through the maps, and not without them. (The gen3 refusal is synthetic, in
    /// CrossSexRefitTests: YAB+ 4.2 draws bibo skin, and no gen3 women's body is installed here.)</summary>
    [LocalDataFact(Bibo, Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-cross-sex-refit-6")]
    [Trait("Plan", "EDGE-cross-sex-refit-3")]
    [Trait("Speed", "Slow")]
    public void Real_bodies_pair_across_sexes_only_through_the_maps() // [AC-cross-sex-refit-6] [EDGE-cross-sex-refit-3]
    {
        string bibo = Path.Combine(LocalData.Path(Bibo), UvBakeInputs.BiboChest);
        string tbse = Path.Combine(LocalData.Path(Tbse), UvBakeInputs.TbseChest);
        Assert.Null(BodyRetarget.BuildPair("_top", bibo, tbse, "_top", false, null, null, Maps(), out _, targetMale: true));

        string empty = Path.Combine(Path.GetTempPath(), "proteus-no-maps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(empty, "uvmaps"));
        try
        {
            var none = new UVRemapService(Substitute.For<IPluginLog>(), empty);
            string? missing = BodyRetarget.BuildPair("_top", bibo, tbse, "_top", false, null, null, none, out _, targetMale: true);
            Assert.NotNull(missing);
            Assert.Contains("body maps", missing);
        }
        finally { Directory.Delete(empty, true); }
    }

    [LocalDataFact(LocalData.GameData, TwillTbse, Tbse, Bibo, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-cross-sex-refit-8")]
    [Trait("Speed", "Slow")]
    public void The_job_gives_the_same_model_twice() // [AC-cross-sex-refit-8]
    {
        var inputs = Inputs(TwillTbse, 201, Tbse, UvBakeInputs.TbseLegs[0].Rel, Bibo, UvBakeInputs.BiboLegs[0].Rel);
        var a = ModelPartReader.Read(CrossSexRefitJob.Run(inputs).Sizes.Single().Saved!.Model)!.Positions;
        var b = ModelPartReader.Read(CrossSexRefitJob.Run(inputs).Sizes.Single().Saved!.Model)!.Positions;
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.True(MathF.Abs(a[i] - b[i]) <= 1e-6f, $"position {i / 3} differs");
    }

    internal static string Describe(string what, CrossSexRefitJob.Result r, CrossSexRefitJob.Size s)
    {
        var l = s.Landed;
        return $"{what}: from {Path.GetFileName(r.SourcePath)}; onto {s.Target.Label}; folds {s.Folds}; moved up to "
             + $"{s.Plan!.Report.WorstMove * 1000f:F1} mm; coverage {l?.VertexCoverage:P1}, astray {l?.Astray}, "
             + $"no landing {l?.NoLanding}, off sheet {l?.OffSheet}, p99 shift {l?.P99Shift * 1000f:F1} mm";
    }
}
