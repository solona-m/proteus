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
    private static CrossSexRefitJob.Size Glove(string key, bool before = false) => Job(key, before).Sizes.Single();

    private static CrossSexRefitJob.Result Job(string key, bool before = false)
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
        return BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoReshapeEnds: before), () => CrossSexRefitJob.RunEnd(inputs!));
    }

    private static UVRemapService Maps() => new(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps));

    /// <summary>The chest and hands pairs the job built for Halone's glove, as it built them (bent made-for → men's).</summary>
    private static (BodyRetarget.SlotPair Chest, BodyRetarget.SlotPair Hands) Pairs(string key, CrossSexRefitJob.Result r)
    {
        var size = r.Sizes.Single();
        Assert.Null(BodyRetarget.BuildPair("_top", File.ReadAllBytes(r.SourcePath!), File.ReadAllBytes(size.TargetPath), "_top",
                                           false, null, null, Maps(), out var chest, null, true));
        var onto = OtherRaceRefit.OnlyRace(BodySizeCatalog.Read(LocalData.Path(Onto(key))), 101);
        var handsTo = AutoRefitDecisions.Companion(onto.For("_glv"), onto.For("_top").First(o => onto.PathOf(o) == size.TargetPath), null)!;
        var handsFrom = AutoRefitDecisions.Companion(r.Bent!.For("_glv"),
            r.Bent.For("_top").First(o => r.Bent.PathOf(o) == r.SourcePath), null)!;
        Assert.Null(BodyRetarget.BuildPair("_glv", File.ReadAllBytes(r.Bent.PathOf(handsFrom)), File.ReadAllBytes(onto.PathOf(handsTo)),
                                           "_glv", false, null, null, Maps(), out var hands, null, true));
        return (chest, hands);
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

    [LocalDataTheory(LocalData.GameData, HandsFeetPortLocalTests.HalonesChosen, CrossSexRefitLocalTests.Bibo,
                     CrossSexRefitLocalTests.Tbse, CrossSexRefitLocalTests.TbseX, LocalData.TbseUvMaps)]
    [InlineData("tbse")]
    [InlineData("tbse-x")]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-18")]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-20")]
    public void Halones_swap_drops_what_the_solve_parts_and_the_arm_comes_back(string key) // [EDGE-cross-sex-wrist-band-18] [EDGE-cross-sex-wrist-band-20]
    {
        var r = Job(key);
        var size = r.Sizes.Single();
        var garment = ModelPartReader.Read(r.Baked!)!;
        var (chest, hands) = Pairs(key, r);
        // One swapped-mesh set over the pairs as the rebuild sees them (the hands swapping): it holds the glove's skin mesh,
        // and the rebuild took out exactly that skin.
        var swapped = BodyRetarget.SwappedSkinMeshes(garment, [chest, hands with { ShapeOnly = false }]);
        var skinMeshes = garment.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                      .Select(p => p.Mesh).ToHashSet();
        Assert.Subset(swapped, skinMeshes);
        int skinTris = garment.Parts.Where(p => p.Island < 0 && skinMeshes.Contains(p.Mesh)
                                                && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                    .Sum(p => p.Triangles.Length / 3);
        Assert.Equal(skinTris, size.Plan!.Report.Swap!.Value.Removed);
        // Both the arm's and the hand's skin came in.
        o.WriteLine($"{key}: added by slot " + string.Join(", ", size.Plan.AddedBySlot!.Select(kv => $"{kv.Key} {kv.Value}")));
        Assert.True(size.Plan.AddedBySlot!["_top"] > 0);
        Assert.True(size.Plan.AddedBySlot!["_glv"] > 0);
        Assert.True(CrossSexRefitJob.ArmSkinCameIn(garment, size.Plan, chest));
        // A rebuilt glove with no skin where the arm lay is caught.
        byte[] skinless = File.ReadAllBytes(Path.Combine(LocalData.Path(HandsFeetPortLocalTests.HalonesChosen),
            "edengate greaves of fending", "chara", "equipment", "e0613", "model", "c0201e0613_sho.mdl"));
        Assert.False(CrossSexRefitJob.ArmSkinCameIn(garment, size.Plan with { Model = skinless }, chest));
    }

    [LocalDataTheory(LocalData.GameData, HandsFeetPortLocalTests.HalonesChosen, CrossSexRefitLocalTests.Bibo,
                     CrossSexRefitLocalTests.Tbse, CrossSexRefitLocalTests.TbseX, LocalData.TbseUvMaps)]
    [InlineData("tbse")]
    [InlineData("tbse-x")]
    [Trait("Plan", "AC-cross-sex-wrist-band-8")]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-8")]
    public void Halones_swapped_skin_keeps_only_the_gloves_tags_and_its_winding(string key) // [AC-cross-sex-wrist-band-8] [EDGE-cross-sex-wrist-band-8]
    {
        byte[] authored = File.ReadAllBytes(Path.Combine(LocalData.Path(HandsFeetPortLocalTests.HalonesChosen), "common", "2",
                                                         "c0201e0613_glv.mdl"));
        var own = KeepEnds.SkinTagsOf(ModelPartReader.Read(authored)!);
        var glove = ModelPartReader.Read(run.Saved(Option(key), "_glv"))!;
        var tags = KeepEnds.SkinTagsOf(glove);
        o.WriteLine($"{key}: the glove's own skin tags [{string.Join(", ", own)}], the saved skin's [{string.Join(", ", tags)}]");
        Assert.Subset(own.ToHashSet(), tags.ToHashSet());
        // No skin triangle turned: each is wound as the men's body winds the same triangle.
        var onto = BodySizeCatalog.Read(LocalData.Path(Onto(key)));
        var bodies = onto.For("_glv", "0101").Select(h => onto.PathOf(h))
                         .Append(Path.Combine(LocalData.Path(Onto(key)), SupportedBodies.Of(key, "_top", false)!.Sizes[0].Rel))
                         .Select(f => ModelPartReader.Read(File.ReadAllBytes(f))!).ToList();
        static (int, int, int) K(Vector3 p) => ((int)MathF.Round(p.X * 1e5f), (int)MathF.Round(p.Y * 1e5f), (int)MathF.Round(p.Z * 1e5f));
        var normalOf = new Dictionary<string, Vector3>();
        foreach (var body in bodies)
            foreach (var part in body.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    var (a, bb, c) = (At(body, part.Triangles[t]), At(body, part.Triangles[t + 1]), At(body, part.Triangles[t + 2]));
                    normalOf[string.Join("|", new[] { K(a), K(bb), K(c) }.Order())] = Vector3.Cross(bb - a, c - a);
                }
        int matched = 0, turned = 0;
        foreach (var part in glove.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                var (a, bb, c) = (At(glove, part.Triangles[t]), At(glove, part.Triangles[t + 1]), At(glove, part.Triangles[t + 2]));
                if (!normalOf.TryGetValue(string.Join("|", new[] { K(a), K(bb), K(c) }.Order()), out var n)) continue;
                matched++;
                if (Vector3.Dot(n, Vector3.Cross(bb - a, c - a)) <= 0f) turned++;
            }
        o.WriteLine($"{key}: {matched} skin triangles matched the men's body, {turned} turned");
        Assert.True(matched > 0);
        Assert.Equal(0, turned);
    }

    [LocalDataFact(LocalData.GameData, HandsFeetPortLocalTests.HalonesChosen, CrossSexRefitLocalTests.Bibo,
                   CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-cross-sex-wrist-band-7")]
    [Trait("Plan", "AC-cross-sex-wrist-band-5")]
    public void Halones_hard_pieces_on_the_bare_hand_move_whole() // [EDGE-cross-sex-wrist-band-7] [AC-cross-sex-wrist-band-5]
    {
        var r = Job("tbse");
        var garment = ModelPartReader.Read(r.Baked!)!;
        var reading = KeepEnds.Read("_glv", garment, r.Baked!)!;
        var reshaped = KeepEnds.Reshape(reading, garment, null, out string why);
        Assert.True(reshaped != null, why);
        var pieces = RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment), out var names);
        int onBare = 0, onHeld = 0;
        foreach (var piece in pieces)
        {
            bool inOldEnds = piece.All(reading.Ends.Contains);
            bool stillHeld = piece.All(reshaped!.Ends.Contains);
            bool free = reshaped.FreePieces.Any(f => f.Count == piece.Count && f.All(piece.Contains));
            if (inOldEnds && !stillHeld) { onBare++; Assert.True(free, "a piece on the bare hand must move whole"); }
            if (stillHeld) { onHeld++; Assert.False(free, "a piece on the held hand stays held"); }
        }
        o.WriteLine($"hard pieces: {onBare} on the let-go hand (moved whole), {onHeld} on the held hand");
    }
}
