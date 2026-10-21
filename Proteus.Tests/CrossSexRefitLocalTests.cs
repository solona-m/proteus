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
/// shirt and trousers made women's on Bibo+, a women's leg garment made men's on The Body SE — and the women's top that
/// cannot be, which is why that cell is not offered.
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
    internal static OtherRaceWearer.Inputs Wearer(ushort to, string gamePath, string? skeletonPath = null)
        => OtherRaceWearer.Gather(to, gamePath, Game,
               path => skeletonPath != null && path == OtherRaceWearer.BaseSkeletonPath(to) ? skeletonPath : null,
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
                                           string ontoNeed, string ontoRel, string? skeletonPath = null)
    {
        string garmentPath = LocalData.Path(garmentNeed);
        string gamePath = GamePathOf(garmentPath);
        var madeFor = BodySizeCatalog.Read(LocalData.Path(madeForNeed));
        var onto = BodySizeCatalog.Read(LocalData.Path(ontoNeed));
        return new CrossSexRefitJob.Inputs(File.ReadAllBytes(garmentPath), gamePath, to, madeFor, IsFile(madeFor, madeForRel),
            onto, IsFile(onto, ontoRel),
            Wearer(to, gamePath, skeletonPath), Maps(), log);
    }

    private (CrossSexRefitJob.Result R, CrossSexRefitJob.Size S) Run(CrossSexRefitJob.Inputs inputs)
    {
        var r = CrossSexRefitJob.Run(inputs);
        Assert.True(r.Refusal.Length == 0, r.Refusal);
        var s = Assert.Single(r.Sizes);
        Assert.True(s.Refusal.Length == 0, s.Refusal);
        o.WriteLine(Describe(Path.GetFileName(inputs.GamePath), r, s));
        Assert.True(s.SaveAllowed, $"the run-time check refuses it: {s.Folds} folds, {s.Landed?.VertexCoverage:P1} matched");
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
    public void Lumberjack_refits_onto_bibo_for_midlander_women() // [AC-cross-sex-refit-2] [AC-cross-sex-refit-3] [AC-cross-sex-refit-5] [EDGE-cross-sex-refit-13] [EDGE-cross-sex-refit-14]
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
    /// A women's top made a men's is not offered: flattening a bust onto a pec turns 115 of Sheer Elegance's 10,436 cloth
    /// faces over (measured 2026-10-20; the same top between women's bodies turns 8, and no pass of the refit brought it
    /// under 100). The job refuses it, as the panel never offers it.
    /// </summary>
    [LocalDataFact(LocalData.GameData, SheerTop, Bibo, Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-cross-sex-refit-7")]
    public void A_womans_top_is_not_refitted_for_men() // [AC-cross-sex-refit-7]
    {
        var r = CrossSexRefitJob.Run(Inputs(SheerTop, 101, Bibo, UvBakeInputs.BiboChest, Tbse, UvBakeInputs.TbseChest));
        Assert.NotEqual("", r.Refusal);
        Assert.Empty(r.Sizes);
        var offered = OtherRaceRefit.Offered("chara/equipment/e6010/model/c0201e6010_top.mdl",
                                             BodySizeCatalog.Read(LocalData.Path(Tbse)), [], null, isVanilla: false);
        Assert.DoesNotContain(offered, c => c.Race == 101);
    }

    [LocalDataFact(LocalData.GameData, Starlit, Rue, Tbse, IvcsMen, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-cross-sex-refit-6")]
    [Trait("Plan", "EDGE-cross-sex-refit-4")]
    public void Starlit_rigged_to_yas_bones_is_refused_for_men_with_either_skeleton() // [AC-cross-sex-refit-6] [EDGE-cross-sex-refit-4]
    {
        var rue = BodySizeCatalog.Read(LocalData.Path(Rue));
        string legsRel = rue.For("_dwn", "0201").First(b => BodySizeCatalog.RaceOf(b.GamePath) == "0201").Rel;
        foreach (var skeleton in new[] { null, LocalData.Path(IvcsMen) })
        {
            var r = CrossSexRefitJob.Run(Inputs(Starlit, 101, Rue, legsRel, Tbse, UvBakeInputs.TbseLegs[0].Rel, skeleton));
            o.WriteLine($"{(skeleton == null ? "game" : "IVCS")} skeleton: {r.Refusal}");
            Assert.NotEqual("", r.Refusal);
            Assert.Contains("iv_", r.Refusal);
            Assert.Empty(r.Sizes);
        }
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
