using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>Halone's Chosen's men's top and glove from one size-set run (Make again, TBSE and TBSE-X for men).</summary>
public sealed class HalonesMensSizeSet : IDisposable
{
    private static readonly Lazy<Lumina.GameData> GameData = new(() => new Lumina.GameData(LocalData.Path(LocalData.GameData)));
    internal static byte[]? Game(string path) => GameData.Value.GetFile(path)?.Data;

    /// <summary>The copy the run wrote into; null when the local data is missing.</summary>
    public string? Root { get; }

    public HalonesMensSizeSet()
    {
        if (LocalData.Missing([LocalData.GameData, HandsFeetPortLocalTests.HalonesChosen, CrossSexRefitLocalTests.Bibo,
                               CrossSexRefitLocalTests.Tbse, CrossSexRefitLocalTests.TbseX, LocalData.TbseUvMaps]) != null)
            return;
        Root = SizeSetBatchData.CopyOf(HandsFeetPortLocalTests.HalonesChosen);
        var bodies = SizeSetBatch.ScanBodies(Path.GetDirectoryName(LocalData.Path(CrossSexRefitLocalTests.Tbse))!,
            [SizeSetBatchData.Folder(CrossSexRefitLocalTests.Bibo), SizeSetBatchData.Folder(CrossSexRefitLocalTests.Tbse),
             SizeSetBatchData.Folder(CrossSexRefitLocalTests.TbseX)]);
        string[] keys = ["tbse@women", "tbse-x@women"];
        var gameBodies = SizeSetRefit.GameBodies(keys, Game, ends: true);
        SizeSetRefit.Run(new SizeSetRefit.Request(Root, keys, Yas: false, Remake: true, bodies, Game, Ends: true,
                                                  race => gameBodies.GetValueOrDefault(race),
                                                  Across: new SizeSetRefit.AcrossInputs(_ => null)),
                         new SizeSetRefit.RunLog(), new UVRemapService(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps)),
                         Substitute.For<IPluginLog>(), _ => { }, CancellationToken.None);
    }

    /// <summary>The model the run saved for the men's body <paramref name="option"/> ("TBSE", "TBSE-X") at the slot.</summary>
    public byte[] Saved(string option, string slot)
        => File.ReadAllBytes(Directory.EnumerateFiles(Path.Combine(Root!, BodyRetargetWriter.Subfolder), $"c0101e0613{slot}.mdl",
                                                      SearchOption.AllDirectories)
                                      .Single(f => string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(
                                                       Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(f)))))),
                                                   option, StringComparison.OrdinalIgnoreCase)));

    public void Dispose()
    {
        if (Root != null) SizeSetBatchData.Delete(Root);
    }
}

/// <summary>
/// cross-sex-wrist-band: a women's glove made a men's draws the men's own arm and hand — its skin swapped for TBSE's chest
/// arm and hands, cut to what it drew — so the men's top's arm and the glove never cut through each other (Halone's
/// Chosen showed a line across the bare forearm where they did).
/// </summary>
[Trait("Speed", "Slow")]
public class CrossSexGloveReshapeLocalTests(HalonesMensSizeSet run, ITestOutputHelper o) : IClassFixture<HalonesMensSizeSet>
{
    private const string GamePath = "chara/equipment/e0613/model/c0201e0613_glv.mdl";

    private static string Option(string key) => key == "tbse" ? "TBSE" : "TBSE-X";
    private static string Onto(string key) => key == "tbse" ? CrossSexRefitLocalTests.Tbse : CrossSexRefitLocalTests.TbseX;
    private static bool LeftArm(Vector3 p) => p.X > 0.3f;

    /// <summary>Halone's glove made a men's through EndInputs (Studio and the CLI), as the size set reads its arm.</summary>
    private static CrossSexRefitJob.Size Glove(string key, bool before = false)
    {
        byte[] glove = File.ReadAllBytes(Path.Combine(LocalData.Path(HandsFeetPortLocalTests.HalonesChosen), "common", "2",
                                                      "c0201e0613_glv.mdl"));
        var bibo = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Bibo));
        var chest = bibo.For("_top", "0201").First(b => b.Label.Contains("SFW Bra - Small", StringComparison.Ordinal));
        var onto = BodySizeCatalog.Read(LocalData.Path(Onto(key)));
        var inputs = CrossSexRefitJob.EndInputs(glove, GamePath, 101, bibo, chest, onto,
                                                [SupportedBodies.Of(key, "_top", false)!.Sizes[0].Rel],
                                                CrossSexRefitLocalTests.Wearer(101, GamePath),
                                                new UVRemapService(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps)),
                                                Substitute.For<IPluginLog>(), true, false, null, null, out string? why);
        Assert.True(inputs != null, why);
        return BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoReshapeEnds: before),
                                       () => CrossSexRefitJob.RunEnd(inputs!)).Sizes.Single();
    }

    private static Vector3 At(ModelParts m, int v) => new(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]);

    [LocalDataTheory(LocalData.GameData, HandsFeetPortLocalTests.HalonesChosen, CrossSexRefitLocalTests.Bibo,
                     CrossSexRefitLocalTests.Tbse, CrossSexRefitLocalTests.TbseX, LocalData.TbseUvMaps)]
    [InlineData("tbse")]
    [InlineData("tbse-x")]
    [Trait("Plan", "AC-cross-sex-wrist-band-1")]
    [Trait("Plan", "AC-cross-sex-wrist-band-3")]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-14")]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-17")]
    public void Halones_top_and_glove_do_not_cross(string key) // [AC-cross-sex-wrist-band-1] [AC-cross-sex-wrist-band-3] [EDGE-cross-sex-wrist-band-14] [EDGE-cross-sex-wrist-band-17]
    {
        var top = ModelPartReader.Read(run.Saved(Option(key), "_top"))!;
        var glove = ModelPartReader.Read(run.Saved(Option(key), "_glv"))!;
        var beforeGlove = ModelPartReader.Read(Glove(key, before: true).Saved!.Model)!;
        float before = SkinIntersections.Length(SkinIntersections.Segments(top, beforeGlove, LeftArm));
        float after = SkinIntersections.Length(SkinIntersections.Segments(top, glove, LeftArm));
        // The body's own chest and hands, the reference: a bare-handed man's own crossing.
        var onto = BodySizeCatalog.Read(LocalData.Path(Onto(key)));
        var chest = ModelPartReader.Read(File.ReadAllBytes(Path.Combine(LocalData.Path(Onto(key)),
                                                                        SupportedBodies.Of(key, "_top", false)!.Sizes[0].Rel)))!;
        float body = 0f;
        foreach (var hands in onto.For("_glv", "0101"))
            body = MathF.Max(body, SkinIntersections.Length(SkinIntersections.Segments(chest,
                ModelPartReader.Read(File.ReadAllBytes(onto.PathOf(hands)))!, LeftArm)));
        o.WriteLine($"{key}: top x glove cut {before * 1000:F0} mm before, {after * 1000:F0} mm now; the body's own chest x hands {body * 1000:F0} mm");
        Assert.True(before > 1f, $"the 1410 glove should cross the top: {before * 1000:F0} mm");
        Assert.True(after <= 1.1f * body + 0.005f, $"{after * 1000:F0} mm of cut against the body's own {body * 1000:F0} mm");
        // Worn alone, over the men's own chest arm: the same.
        float alone = SkinIntersections.Length(SkinIntersections.Segments(chest, glove, LeftArm));
        Assert.True(alone <= 1.1f * body + 0.005f, $"{alone * 1000:F0} mm of cut against the men's chest");
    }

    [LocalDataTheory(LocalData.GameData, HandsFeetPortLocalTests.HalonesChosen, CrossSexRefitLocalTests.Bibo,
                     CrossSexRefitLocalTests.Tbse, CrossSexRefitLocalTests.TbseX, LocalData.TbseUvMaps)]
    [InlineData("tbse")]
    [InlineData("tbse-x")]
    [Trait("Plan", "AC-cross-sex-wrist-band-2")]
    [Trait("Plan", "AC-cross-sex-wrist-band-8")]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-10")]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-9")]
    public void Halones_glove_skin_is_the_mens_body(string key) // [AC-cross-sex-wrist-band-2] [AC-cross-sex-wrist-band-8] [EDGE-cross-sex-wrist-band-10] [EDGE-cross-sex-wrist-band-9]
    {
        byte[] saved = run.Saved(Option(key), "_glv");
        var glove = ModelPartReader.Read(saved)!;
        var onto = BodySizeCatalog.Read(LocalData.Path(Onto(key)));
        var bodyPoints = new List<Vector3>();
        string chestPath = Path.Combine(LocalData.Path(Onto(key)), SupportedBodies.Of(key, "_top", false)!.Sizes[0].Rel);
        var chest = ModelPartReader.Read(File.ReadAllBytes(chestPath))!;
        foreach (var body in onto.For("_glv", "0101").Select(h => ModelPartReader.Read(File.ReadAllBytes(onto.PathOf(h)))!).Append(chest))
            bodyPoints.AddRange(body.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                          .SelectMany(p => p.Triangles).Distinct().Select(v => At(body, v)));
        var skin = glove.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToList();
        var points = skin.SelectMany(p => p.Triangles).Distinct().Select(v => At(glove, v)).ToList();
        int own = points.Count(p => bodyPoints.Any(q => Vector3.DistanceSquared(p, q) < 1e-10f));
        o.WriteLine($"{key}: {own} of {points.Count} glove skin points are the men's body's own; skin " +
                    string.Join(", ", skin.Select(p => p.Material).Distinct()));
        // Both arms and the bare hand, every point but the few the cut pulls to its edge.
        Assert.True(own >= points.Count * 0.995, $"{own} of {points.Count}");
        // Drawn with the men's own skin material, and no material named that nothing draws.
        var chestSkin = chest.Parts.First(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).Material;
        Assert.All(skin, p => Assert.Equal(chestSkin, p.Material));
        MaterialAssert.NoNewUndrawnNames(saved, File.ReadAllBytes(Path.Combine(LocalData.Path(HandsFeetPortLocalTests.HalonesChosen),
                                                                               "common", "2", "c0201e0613_glv.mdl")));
        // No skin material the men's body does not draw.
        Assert.Empty(RefitCore.ForeignSkin(saved, [chestPath]));
    }

    [LocalDataTheory(LocalData.GameData, HandsFeetPortLocalTests.HalonesChosen, CrossSexRefitLocalTests.Bibo,
                     CrossSexRefitLocalTests.Tbse, CrossSexRefitLocalTests.TbseX, LocalData.TbseUvMaps)]
    [InlineData("tbse")]
    [InlineData("tbse-x")]
    [Trait("Plan", "AC-cross-sex-wrist-band-4")]
    [Trait("Plan", "AC-cross-sex-wrist-band-5")]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-5")]
    public void Halones_right_gauntlet_unchanged_and_the_left_hand_folds_no_more(string key) // [AC-cross-sex-wrist-band-4] [AC-cross-sex-wrist-band-5] [EDGE-cross-sex-wrist-band-5]
    {
        var before = Glove(key, before: true);
        var after = Glove(key);
        var b = ModelPartReader.Read(before.Saved!.Model)!;
        var a = ModelPartReader.Read(after.Saved!.Model)!;
        var clothB = b.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToList();
        var clothA = a.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToList();
        Assert.Equal(clothB.Count, clothA.Count);
        int right = 0;
        foreach (var (pb, pa) in clothB.Zip(clothA))
        {
            var vb = pb.Triangles.Distinct().Order().ToArray();
            var va = pa.Triangles.Distinct().Order().ToArray();
            Assert.Equal(vb.Length, va.Length);
            for (int k = 0; k < vb.Length; k++)
            {
                if (At(b, vb[k]).X > -0.3f) continue;
                right++;
                Assert.True(Vector3.Distance(At(b, vb[k]), At(a, va[k])) <= 1e-6f, $"right gauntlet point {vb[k]} moved");
            }
        }
        o.WriteLine($"{key}: {right} right-gauntlet points unchanged; folds {before.Folds} → {after.Folds}");
        Assert.True(right > 0);
        Assert.True(after.Folds <= 1.25 * before.Folds + 10, $"{after.Folds} folds against {before.Folds}");
    }

    [LocalDataTheory(LocalData.GameData, HandsFeetPortLocalTests.HalonesChosen, CrossSexRefitLocalTests.Bibo,
                     CrossSexRefitLocalTests.Tbse, CrossSexRefitLocalTests.TbseX, LocalData.TbseUvMaps)]
    [InlineData("tbse")]
    [InlineData("tbse-x")]
    [Trait("Plan", "AC-cross-sex-wrist-band-7")]
    public void Studio_cli_and_size_set_make_one_glove(string key) // [AC-cross-sex-wrist-band-7]
        => Assert.Equal(Glove(key).Saved!.Model, run.Saved(Option(key), "_glv"));
}
