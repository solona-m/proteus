using System.Collections.Generic;
using System.IO;
using System.Linq;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// What every refit that does not tuck the new skin under the cloth wrote before the tuck existed, pinned as SHA-256 of
/// the planned model on <c>test</c> 054b7d3 (each value taken from two separate test runs that agreed). Equality of two
/// runs in one process proves nothing about a change to the shared cut; these do.
/// </summary>
[Collection(GameBodyCollection.Name)]
public class SkinTuckGoldenTests(ITestOutputHelper output)
{
    private static readonly HashSet<int> NoneHeld = [];

    private string Pinned(byte[] model, string expected)
    {
        string actual = SkinTuckSheet.Sha(model);
        output.WriteLine($"sha {actual}");
        Assert.Equal(expected, actual);
        return actual;
    }

    [Fact]
    [Trait("Plan", "AC-neckline-skin-tuck-3")]
    public void Studio_cut_plans_as_before()
    {
        var bytes = SkinTuckSheet.LooseTop();
        var garment = ModelPartReader.Read(bytes)!;
        var plan = BodyRetarget.Plan(garment, bytes, [SkinTuckSheet.Resized(SkinTuckSheet.Body())], "_top", held: NoneHeld,
                                     replaceSkin: true, acrossBodies: true, clearBody: false, cutHidden: true,
                                     keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)));
        Pinned(plan.Model, StudioCut);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-skin-tuck-3")]
    public void Studio_cut_and_clear_plans_as_before()
    {
        var bytes = SkinTuckSheet.LooseTop();
        var garment = ModelPartReader.Read(bytes)!;
        var plan = BodyRetarget.Plan(garment, bytes, [SkinTuckSheet.Resized(SkinTuckSheet.Body())], "_top", held: NoneHeld,
                                     replaceSkin: true, acrossBodies: true, clearBody: true, cutHidden: true,
                                     keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)));
        Pinned(plan.Model, StudioCutAndClear);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-skin-tuck-3")]
    [Trait("Plan", "EDGE-neckline-skin-tuck-5")]
    public void A_cut_PullToEdge_moves_plans_as_before()
    {
        // Cloth 1 mm over the strip past the author's edge: the kept corners there are pulled back to it.
        var body = SkinTuckSheet.Body();
        var bytes = SkinTuckSheet.Top(_ => 0.001f, clothFrom: 4);
        var rebuilt = BodyRetarget.SwapSkin(bytes, [SkinTuckSheet.Resized(body)], out var report)!;
        output.WriteLine($"{report}");

        var bodyAt = Positions(body);
        var read = ModelPartReader.Read(rebuilt)!;
        bool moved = read.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                         .SelectMany(p => p.Triangles).Distinct()
                         .Any(v => !bodyAt.Contains(Key(read, v)));
        Assert.True(moved, "nothing was pulled back: the fixture no longer exercises the pull");
        Pinned(rebuilt, PullMoves);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-skin-tuck-3")]
    public void A_game_top_without_the_tuck_plans_as_before()
    {
        var bytes = SkinTuckSheet.LooseTop();
        var garment = ModelPartReader.Read(bytes)!;
        var plan = RefitCore.PlanRefit(garment, bytes, [SkinTuckSheet.Resized(SkinTuckSheet.Body())], "_top", across: true,
                                       gameGear: true, out _);
        Pinned(plan.Model, GameTopUntucked);
    }

    // ── on the game's own files and the user's body mods: inconclusive when an input is not the one pinned ──

    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";

    /// <summary>
    /// Whether the input files are the ones pinned. A game patch or a body mod update is not a regression: the test says
    /// INCONCLUSIVE and checks nothing (xunit 2 has no run-time skip without another package).
    /// </summary>
    private bool SameInputs(params (string Name, byte[] Bytes, string Sha)[] inputs)
    {
        foreach (var (name, bytes, sha) in inputs)
            if (SkinTuckSheet.Sha(bytes) != sha)
            {
                output.WriteLine($"INCONCLUSIVE: {name} is not the file the golden was pinned on");
                return false;
            }
        return true;
    }

    private (byte[] Garment, string Source, string Target, BodyRetarget.SlotPair Pair, UVRemapService Uv)
        Setup(string garmentName, string rel)
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        byte[] bytes = VanillaGearRefitLocalTests.TopOf(data, garmentName);
        var vanilla = VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data);
        string source = vanilla.PathOf(vanilla.For("_top", "0201")[0]);
        var mod = BodySizeCatalog.Read(LocalData.Path(Neolithe));
        var option = mod.For("_top", "0201").First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(rel));
        string target = mod.PathOf(option);
        Assert.Null(BodyRetarget.BuildPair("_top", source, target, "_top", false, null, null, uv, out var pair));
        foreach (var (name, b) in new[] { ("garment", bytes), ("source", File.ReadAllBytes(source)), ("target", File.ReadAllBytes(target)) })
            output.WriteLine($"input {name} sha {SkinTuckSheet.Sha(b)}");
        return (bytes, source, target, pair, uv);
    }

    [LocalDataFact(LocalData.GameData, Neolithe)]
    [Trait("Plan", "AC-neckline-skin-tuck-3")]
    public void A_modders_cut_on_Neolithe_plans_as_before()
    {
        var (bytes, source, target, pair, _) = Setup("Neo-Ishgardian Top of Maiming", "neobelly chest - smallclothes/sfw m.mdl");
        if (!SameInputs(("garment", bytes, MaimingSha), ("source", File.ReadAllBytes(source), VanillaTopSha),
                        ("target", File.ReadAllBytes(target), NeobellyMSha))) return;
        var garment = ModelPartReader.Read(bytes)!;
        var plan = BodyRetarget.Plan(garment, bytes, [pair], "_top", held: NoneHeld, replaceSkin: true, acrossBodies: true,
                                     clearBody: false, cutHidden: true,
                                     keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)));
        Pinned(plan.Model, MaimingStudioCut);
    }

    [LocalDataFact(LocalData.GameData, Neolithe)]
    [Trait("Plan", "AC-neckline-skin-tuck-3")]
    public void The_Dirndl_game_refit_without_the_tuck_plans_as_before()
    {
        var (bytes, source, target, pair, uv) = Setup("Dirndl's Bodice", "extra chest - smallclothes/nsfw almond xs.mdl");
        if (!SameInputs(("garment", bytes, DirndlSha), ("source", File.ReadAllBytes(source), VanillaTopSha),
                        ("target", File.ReadAllBytes(target), AlmondXsSha))) return;
        var garment = ModelPartReader.Read(bytes)!;
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var plan = RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _);
        Pinned(RefitCore.MatchSkinToBody(plan.Model, source, target, uv, false, log), DirndlGameRefit);
    }

    private static HashSet<(int, int, int)> Positions(byte[] model)
    {
        var read = ModelPartReader.Read(model)!;
        return [.. Enumerable.Range(0, read.Positions.Length / 3).Select(v => Key(read, v))];
    }

    private static (int, int, int) Key(ModelParts m, int v)
        => ((int)System.MathF.Round(m.Positions[v * 3] * 1e5f), (int)System.MathF.Round(m.Positions[v * 3 + 1] * 1e5f),
            (int)System.MathF.Round(m.Positions[v * 3 + 2] * 1e5f));

    // Pinned on test 054b7d3.
    private const string StudioCut = "E3DA5ACD964F2FD5ACC8E7BC87EA163456C2955C9035B9B864F755DD3F9742F7";
    private const string StudioCutAndClear = "E3DA5ACD964F2FD5ACC8E7BC87EA163456C2955C9035B9B864F755DD3F9742F7";
    private const string PullMoves = "7859DFEE4147CB00F617F9325A92C1A76C1E23150EB68A1ED516F79DBD892A81";
    private const string GameTopUntucked = "E3DA5ACD964F2FD5ACC8E7BC87EA163456C2955C9035B9B864F755DD3F9742F7";
    private const string MaimingStudioCut = "6B0216E852B601965EDB47C63572FCA53FFCDB7C80CC1E36CB821DCDB30DADE1";
    private const string DirndlGameRefit = "1B6456AF7163D39F5EDB5B2E38EE8F50B35D1B15BDCE0DDEF414BD4FDC5FD61B";

    // The inputs those were pinned on.
    private const string MaimingSha = "E2985DE7C93082F91EB987372F6701429B0A280A5D5A213E044EED8C1E4210CB";
    private const string DirndlSha = "24BE9C61E0A7FB57D8B307AC761846B596896E8C6008AE8F84562EAAF2AD77DF";
    private const string VanillaTopSha = "2073B644BD3EBE2E21D84C98C622F9412E200642BAD1310DE4D20409355A2C6F";
    private const string NeobellyMSha = "EAFA97B411C607BF4F8D260EF4948A7958573F6E8A05644A633C3EA500A22C2E";
    private const string AlmondXsSha = "0D90681073C3F19A06D320210C79AF46854E3D512EE35E5C1076DE1CBFC16EA2";
}
