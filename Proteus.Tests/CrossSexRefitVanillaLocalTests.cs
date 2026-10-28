using System;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The game's own women's legs made men's on The Body SE and TBSE-X (vanilla-cross-sex-legs): from the game's women's
/// body, as the Body size panel reads it for the game's gear, bent to the men's shape and matched through the bibo↔TBSE
/// maps — gated as the shipped cross-sex cells were (<see cref="RefitGate"/>).
/// </summary>
public class CrossSexRefitVanillaLocalTests(ITestOutputHelper o)
{
    private const string Tbse = CrossSexRefitLocalTests.Tbse, TbseX = CrossSexRefitLocalTests.TbseX,
                         Bibo = CrossSexRefitLocalTests.Bibo, IvcsMen = CrossSexRefitLocalTests.IvcsMen;

    private const string Fuath = "Fuath Pantaloons";

    private readonly IPluginLog log = Substitute.For<IPluginLog>();

    private static readonly Lazy<Lumina.GameData> GameData = new(() => new Lumina.GameData(LocalData.Path(LocalData.GameData)));

    private static byte[]? Game(string path) => GameData.Value.GetFile(path)?.Data;

    private static UVRemapService Maps() => new(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps));

    /// <summary>The c0201 legs model of the first game item named <paramref name="name"/>, and its game path.</summary>
    internal static (byte[] Bytes, string GamePath) LegsOf(Lumina.GameData data, string name)
    {
        foreach (var item in data.GetExcelSheet<Item>()!)
        {
            if (item.EquipSlotCategory.RowId != 7 || !item.Name.ExtractText().StartsWith(name, StringComparison.Ordinal))
                continue;
            int set = (int)(item.ModelMain & 0xFFFF);
            string path = $"chara/equipment/e{set:D4}/model/c0201e{set:D4}_dwn.mdl";
            if (data.GetFile(path) is { } file) return (file.Data, path);
        }
        throw new InvalidOperationException($"no c0201 game legs named {name}");
    }

    /// <summary>The made-for side as the panel builds it for the game's gear picked for Midlander men.</summary>
    private static BodySizeCatalog MadeFor()
        => OtherRaceRefit.MadeForBelow(VanillaBodyCatalog.Key, null, null, 201, 101, Game)!;

    /// <summary>The legs option of <paramref name="need"/> in the folder <paramref name="folder"/>.</summary>
    private static Func<BodyOption, bool> Legs(string need, string folder)
    {
        var catalog = BodySizeCatalog.Read(LocalData.Path(need));
        var pick = catalog.For("_dwn", "0101").First(b => BodySizeCatalog.RaceOf(b.GamePath) == "0101"
            && b.Rel.Replace('\\', '/').Contains("/" + folder + "/", StringComparison.OrdinalIgnoreCase));
        return CrossSexRefitLocalTests.IsFile(catalog, pick.Rel);
    }

    /// <summary>TBSE-X rigs IVCS's bones: its men wear the IVCS skeleton, as they would in game.</summary>
    private CrossSexRefitJob.Inputs Inputs(string ontoNeed, string folder, bool pushOut)
    {
        var (bytes, gamePath) = LegsOf(GameData.Value, Fuath);
        var onto = BodySizeCatalog.Read(LocalData.Path(ontoNeed));
        string? skeleton = ontoNeed == TbseX ? LocalData.Path(IvcsMen) : null;
        return new CrossSexRefitJob.Inputs(bytes, gamePath, 101, MadeFor(), null, onto, Legs(ontoNeed, folder),
                                           CrossSexRefitLocalTests.Wearer(101, gamePath, skeleton), Maps(), log,
                                           PushOut: pushOut, CutHidden: true);
    }

    /// <summary>The same garment refitted from the game's women's body onto Bibo+'s legs: the folds G2 judges against.</summary>
    private int SameSexFolds(byte[] garment)
    {
        var vanilla = VanillaBodyCatalog.Read("0201", Game);
        string from = vanilla.PathOf(vanilla.For("_dwn", "0201")[0]);
        string to = Path.Combine(LocalData.Path(Bibo), UvBakeInputs.BiboLegs[3].Rel);
        if (BodyRetarget.BuildPair("_dwn", from, to, "_dwn", false, null, null, Maps(), out var pair) is { } refused)
        {
            o.WriteLine($"no baseline onto Bibo+: {refused}");
            return 0;
        }
        var plan = BodyRetarget.Plan(ModelPartReader.Read(garment)!, garment, [pair], "_dwn", replaceSkin: true,
                                     acrossBodies: true, cutHidden: true);
        return CrossSexRefit.ClothFolded(garment, plan);
    }

    /// <summary>
    /// Every gate as the shipped cells have it, but G5b on The Body SE, which is logged: its hip line keeps ~38 cloth points
    /// on the pelvis where his skin is on the thigh (regions 95.5–96.3% against 98.0% on the body they were made for;
    /// 98.2% for the game's own men's model refitted the same way). No single weight pass accounts for it, and worn in game
    /// on build 1386 (the Antiquated Aoidos' Tights on The Body SE) it was accepted as it is. TBSE-X asserts it
    /// (97.2–97.5%).
    /// </summary>
    [LocalDataTheory(LocalData.GameData, Tbse, TbseX, Bibo, IvcsMen, LocalData.TbseUvMaps)]
    [InlineData(Tbse, "sfw", false)]
    [InlineData(Tbse, "sfw", true)]
    [InlineData(Tbse, "gen a", false)]
    [InlineData(Tbse, "gen a", true)]
    [InlineData(Tbse, "gen d", false)]
    [InlineData(Tbse, "gen d", true)]
    [InlineData(TbseX, "flaccid m a", false)]
    [InlineData(TbseX, "flaccid m a", true)]
    [InlineData(TbseX, "erect l cut", false)]
    [InlineData(TbseX, "erect l cut", true)]
    [Trait("Plan", "AC-vanilla-cross-sex-legs-3")]
    public void Fuath_pantaloons_refit_from_the_vanilla_body_onto_tbse_for_men(string onto, string folder, bool pushOut) // [AC-vanilla-cross-sex-legs-3]
    {
        var inputs = Inputs(onto, folder, pushOut);
        var r = CrossSexRefitJob.Run(inputs);
        Assert.True(r.Refusal.Length == 0, r.Refusal);
        var s = Assert.Single(r.Sizes);
        Assert.True(s.Refusal.Length == 0, s.Refusal);
        o.WriteLine(CrossSexRefitLocalTests.Describe(Path.GetFileName(inputs.GamePath), r, s));
        Assert.True(s.SaveAllowed, $"the run-time check refuses it: {s.Landed?.VertexCoverage:P1} matched");
        var gates = RefitGate.Gates.Inside | RefitGate.Gates.Standoff | (onto == Tbse ? 0 : RefitGate.Gates.Regions);
        RefitGate.Assert(o, r, s, SameSexFolds(inputs.Garment), gates, (float)CrossSexRefit.MinCoverage);
        Assert.True(r.Route.Lowered && r.Route.ByAlias);
        Assert.Equal(inputs.GamePath.Replace("/c0201e", "/c0101e"), r.Route.SavePath(inputs.GamePath));
    }

    /// <summary>TBSE-X's SFW legs draw too little skin to match the game's women's body: refused, as the panel says.</summary>
    [LocalDataFact(LocalData.GameData, TbseX, IvcsMen, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-9")]
    public void Tbse_x_sfw_legs_match_too_little_and_are_not_saved() // [EDGE-vanilla-cross-sex-legs-9]
    {
        var s = Assert.Single(CrossSexRefitJob.Run(Inputs(TbseX, "sfw", false)).Sizes);
        o.WriteLine($"TBSE-X SFW: {s.Landed?.VertexCoverage:P1} matched");
        Assert.False(s.SaveAllowed);
    }

    /// <summary>
    /// The game's Antiquated Aoidos' Tights on The Body SE: its women's legs draw skin only in ~10 mm bands between the bare
    /// thigh's straps, and The Body SE's thigh triangles are ~20 mm across. The cut kept a body triangle only for a corner
    /// the author's skin covered, and one with every corner under a strap went though its middle showed between them — a
    /// hole in game (build 1385). Seen from behind as the game draws it, back faces culled, every ray through the strap
    /// band that meets his thigh meets the refit too.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Tbse, LocalData.TbseUvMaps)]
    public void The_tights_straps_leave_no_hole_in_the_thigh_between_them()
    {
        var (bytes, gamePath) = LegsOf(GameData.Value, "Antiquated Aoidos");
        var onto = BodySizeCatalog.Read(LocalData.Path(Tbse));
        CrossSexRefitJob.Size Refit() => Assert.Single(CrossSexRefitJob.Run(new CrossSexRefitJob.Inputs(
            bytes, gamePath, 101, MadeFor(), null, onto, Legs(Tbse, "sfw"),
            CrossSexRefitLocalTests.Wearer(101, gamePath), Maps(), log, PushOut: false, CutHidden: true)).Sizes);
        var fixedSize = Refit();
        var body = ModelPartReader.Read(File.ReadAllBytes(fixedSize.TargetPath))!;
        int holes = Holes(ModelPartReader.Read(fixedSize.Saved!.Model)!, body);
        int before = Holes(ModelPartReader.Read(BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoCutInside: true), Refit)
                                                .Saved!.Model)!, body);
        o.WriteLine($"rays through the strap band with no skin: {holes} (before 1386: {before})");
        Assert.True(before > 0, "the old cut no longer leaves the hole this test was written for");
        Assert.Equal(0, holes);
    }

    /// <summary>Rays from behind on a 2 mm grid over the bare (+x) thigh's strap band that meet the body's own thigh
    /// facing them, and nothing of <paramref name="refit"/> facing them.</summary>
    private static int Holes(ModelParts refit, ModelParts body)
    {
        static bool Meets(ModelParts m, float x, float y, bool skinOnly)
        {
            foreach (var part in m.Parts.Where(p => p.Island < 0 && (!skinOnly || SecondSkinWriter.IsBodySkinMaterial(p.Material))))
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    var a = At(m, part.Triangles[t]);
                    var b = At(m, part.Triangles[t + 1]);
                    var c = At(m, part.Triangles[t + 2]);
                    // Looking along +z from behind: only faces turned toward -z are drawn.
                    if (System.Numerics.Vector3.Cross(b - a, c - a).Z >= 0) continue;
                    if (Inside(x, y, a, b, c)) return true;
                }
            return false;
        }
        int holes = 0;
        for (float x = 0.04f; x <= 0.14f; x += 0.002f)
        for (float y = 0.79f; y <= 0.87f; y += 0.002f)
            if (Meets(body, x, y, skinOnly: true) && !Meets(refit, x, y, skinOnly: false))
                holes++;
        return holes;
    }

    private static System.Numerics.Vector3 At(ModelParts m, int v)
        => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>Whether (x, y) falls inside the triangle's outline seen along z.</summary>
    private static bool Inside(float x, float y, System.Numerics.Vector3 a, System.Numerics.Vector3 b, System.Numerics.Vector3 c)
    {
        float Side(System.Numerics.Vector3 p, System.Numerics.Vector3 q) => (q.X - p.X) * (y - p.Y) - (q.Y - p.Y) * (x - p.X);
        float s1 = Side(a, b), s2 = Side(b, c), s3 = Side(c, a);
        return (s1 >= 0 && s2 >= 0 && s3 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0);
    }

    /// <summary>The made-for side is the game's own women's body, never the men's it falls through to.</summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-7")]
    public void The_made_for_body_is_the_game_s_womens_own() // [EDGE-vanilla-cross-sex-legs-7]
    {
        var made = MadeFor();
        Assert.NotEmpty(made.For("_dwn"));
        Assert.All(made.Options, b => Assert.Equal("0201", BodySizeCatalog.RaceOf(b.GamePath)));
    }
}
