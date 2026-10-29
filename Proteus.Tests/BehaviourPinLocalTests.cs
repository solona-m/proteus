using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// What this change must leave alone, byte for byte (mens-ends-crotch, AC-9): pinned on the unchanged code of test 3ab1c98
/// (build 1397), before the drape, the net worst push or men's shoes and gloves existed. A men's top made women's, a
/// women's top made men's, women's legs made men's with no drape, and a same-sex size set with shoes. A pin is never
/// re-captured to make it pass: the one legitimate re-capture is on a newer test head's own unchanged code, in a scratch
/// worktree, as a commit of its own naming the upstream change that moved it.
/// </summary>
[Trait("Speed", "Slow")]
public class BehaviourPinLocalTests(ITestOutputHelper o)
{
    private const string LumberjackPin = "1FE367DFEFEB9D1277223A57D2AD7AB637FF1EB515EAC99B7B0BD8271578C214";
    private const string SheerTopPin = "066EE8258755CEF2E21223A47AF1B9D90160978CF2464DC2C6DA14659DBE8351";
    private const string StormblessedLegsPin = "3B2B3ADD084CBB2295D78499DCE8769656B8FCF49260C2C4190BC359ED846DA0";
    private const string HeelsSizeSetPin = "E53697A7A3235D1FB3EF57DF52FCA160BF6C3BAF193C8E49507F252B6A42F955";

    private static UVRemapService Maps() => new(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps));

    private static string GamePathOf(string path)
    {
        string p = path.Replace('\\', '/');
        return p[p.IndexOf("chara/", StringComparison.OrdinalIgnoreCase)..].ToLowerInvariant();
    }

    private static CrossSexRefitJob.Inputs Inputs(string garmentNeed, ushort to, string madeForNeed, string madeForRel,
                                                  string ontoNeed, string ontoRel)
    {
        string path = LocalData.Path(garmentNeed);
        string gamePath = GamePathOf(path);
        var madeFor = BodySizeCatalog.Read(LocalData.Path(madeForNeed));
        var onto = BodySizeCatalog.Read(LocalData.Path(ontoNeed));
        return new CrossSexRefitJob.Inputs(File.ReadAllBytes(path), gamePath, to, madeFor,
            CrossSexRefitLocalTests.IsFile(madeFor, madeForRel), onto, CrossSexRefitLocalTests.IsFile(onto, ontoRel),
            CrossSexRefitLocalTests.Wearer(to, gamePath), Maps(), Substitute.For<IPluginLog>());
    }

    /// <summary>The saved model and its materials, hashed together.</summary>
    internal static string Hash(CrossSexRefitJob.Result r)
    {
        var s = Assert.Single(r.Sizes);
        Assert.NotNull(s.Saved);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(s.Saved!.Model);
        foreach (var m in s.Saved.Materials ?? [])
        {
            sha.AppendData(Encoding.UTF8.GetBytes(m.Name));
            sha.AppendData(m.Bytes);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private void Check(string what, string pin, string got)
    {
        o.WriteLine($"{what}: {got}");
        Assert.Equal(pin, got);
    }

    [LocalDataFact(LocalData.GameData, CrossSexRefitLocalTests.LumberjackShirt, CrossSexRefitLocalTests.Tbse,
                   CrossSexRefitLocalTests.Bibo, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-mens-ends-crotch-9")]
    [Trait("Plan", "EDGE-mens-ends-crotch-26")]
    public void Lumberjack_made_womens_is_unchanged() // [AC-mens-ends-crotch-9] [EDGE-mens-ends-crotch-26]
        => Check("lumberjack", LumberjackPin,
                 Hash(CrossSexRefitJob.Run(Inputs(CrossSexRefitLocalTests.LumberjackShirt, 201, CrossSexRefitLocalTests.Tbse,
                                                  UvBakeInputs.TbseChest, CrossSexRefitLocalTests.Bibo, UvBakeInputs.BiboChest))));

    [LocalDataFact(LocalData.GameData, CrossSexRefitLocalTests.SheerTop, CrossSexRefitLocalTests.Bibo,
                   CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-mens-ends-crotch-9")]
    public void Sheer_top_made_mens_is_unchanged() // [AC-mens-ends-crotch-9]
        => Check("sheer top", SheerTopPin,
                 Hash(CrossSexRefitJob.Run(Inputs(CrossSexRefitLocalTests.SheerTop, 101, CrossSexRefitLocalTests.Bibo,
                                                  UvBakeInputs.BiboChest, CrossSexRefitLocalTests.Tbse, UvBakeInputs.TbseChest))));

    /// <summary>Women's legs made men's with the drape off: today's output, the reference the drape is measured from.</summary>
    [LocalDataFact(LocalData.GameData, CrossSexNudeLegsLocalTests.StormblessedLegs, CrossSexNudeLegsLocalTests.LavaBod,
                   CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-mens-ends-crotch-9")]
    public void Stormblessed_legs_without_the_drape_are_unchanged() // [AC-mens-ends-crotch-9]
    {
        var log = Substitute.For<IPluginLog>();
        var inputs = CrossSexNudeLegsLocalTests.JobInputs(CrossSexNudeLegsLocalTests.StormblessedLegs,
                                                          CrossSexNudeLegsLocalTests.LavaBod, 1, log) with { DrapeCrotch = false };
        Check("stormblessed legs", StormblessedLegsPin, Hash(CrossSexRefitJob.Run(inputs)));
    }

    /// <summary>A same-sex size set with shoes on: every file it writes and its report, timings and the worst push aside
    /// (the worst push is measured differently by AC-10, on purpose).</summary>
    [LocalDataFact(LocalData.ImcToggleMod, SizeSetBatchData.Bibo, SizeSetBatchData.Rue, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-mens-ends-crotch-9")]
    [Trait("Plan", "EDGE-mens-ends-crotch-26")]
    public void A_same_sex_size_set_with_shoes_is_unchanged() // [AC-mens-ends-crotch-9] [EDGE-mens-ends-crotch-26]
    {
        string copy = SizeSetBatchData.CopyOf(LocalData.ImcToggleMod);
        try
        {
            var bodies = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods));
            var readGame = SizeSetBatchData.Game();
            var pieces = SizeSetRefit.PiecesOf(copy, readGame, includeEnds: true);
            var keys = SizeSetBatch.RunKeys(pieces, SizeSetBatch.ParseKeys("bibo,rue", out _)!, men: false);
            var gameBodies = SizeSetRefit.GameBodies(keys, readGame, ends: true);
            var log = Substitute.For<IPluginLog>();
            var runLog = new SizeSetRefit.RunLog();
            SizeSetRefit.Run(new SizeSetRefit.Request(copy, keys, Yas: false, Remake: true, bodies, readGame, Ends: true,
                                                      race => gameBodies.GetValueOrDefault(race), Piercings: false),
                             runLog, new UVRemapService(log, LocalData.Path(LocalData.UvMaps)), log, _ => { },
                             CancellationToken.None);

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            string sub = Path.Combine(copy, BodyRetargetWriter.Subfolder);
            var files = Directory.Exists(sub)
                ? Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList()
                : [];
            foreach (string file in files)
            {
                sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(copy, file).Replace('\\', '/')));
                sha.AppendData(File.ReadAllBytes(file));
            }
            foreach (var s in runLog.Sizes)
            {
                var m = s.Measures;
                string line = $"{s.Piece}|{s.GamePath}|{s.Slot}|{s.Group}|{s.Option}|{s.Body}|{s.BodyMod}|{s.Yas}|{s.Men}|" +
                              $"{s.Status}|{s.ReasonCode}|{s.Reason}|{s.From}|{s.CutFrom}|" +
                              (m is null ? "-" : $"{m.Folded}|{m.Unmatched}|{m.WorstMoveMm:F4}|{m.Pushed}|{m.SnapRate:F4}|" +
                                                 $"{m.ShapeFallbacks}|{m.Coverage:F4}|{m.SkinOnly}");
                o.WriteLine(line);
                sha.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
            }
            foreach (string note in runLog.Notes.Concat(runLog.Warnings)) sha.AppendData(Encoding.UTF8.GetBytes(note + "\n"));
            Assert.Contains(runLog.Sizes, s => s.Slot == "_sho" && s.Status == SizeSetRefit.SizeStatus.Made);
            Check("heels size set", HeelsSizeSetPin, Convert.ToHexString(sha.GetHashAndReset()));
        }
        finally
        {
            SizeSetBatchData.Delete(copy);
        }
    }
}
