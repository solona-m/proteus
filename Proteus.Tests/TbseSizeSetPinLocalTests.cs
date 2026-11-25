using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// What tbsex-nude-legs must leave alone (AC-4, EDGE-10): the size set's "TBSE (for men)" run on a copy of three women's
/// mods, as build 1410 made it (pinned on test 3bab948e, before a coverage refusal could fall through to the nude legs).
/// Two tables per mod, compared item by item: the men's outcomes (status, reason, the body made from, coverage) and every
/// file the run itself wrote, by hash. Only the files this run wrote count — the copy carries the mod's own earlier
/// "Body Retarget" output, which changes whenever the size set is run on the real mod in game.
/// <para/>
/// A table is never re-captured to make it pass. The one legitimate change is a row a later plan makes on purpose, named
/// in its allow-list (or a leave-out) with the plan that made it.
/// </summary>
[Trait("Speed", "Slow")]
public class TbseSizeSetPinLocalTests(ITestOutputHelper o)
{
    private static readonly Lazy<Lumina.GameData> GameData = new(() => new Lumina.GameData(LocalData.Path(LocalData.GameData)));
    private static byte[]? Game(string path) => GameData.Value.GetFile(path)?.Data;
    private static UVRemapService Maps() => new(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps));

    /// <summary>Rows and files this run produced on 1410, Halone's glove left out (cross-sex-wrist-band changes it).</summary>
    private static readonly string[] HalonesRows =
    [
        "chara/equipment/e0613/model/c0101e0613_dwn.mdl|tbse||Skipped|authors-mens||",
        "chara/equipment/e0613/model/c0101e0613_sho.mdl|tbse|TBSE|Made||Bibo+ — Type C - Small|0.987",
        "chara/equipment/e0613/model/c0101e0613_top.mdl|tbse|TBSE|Made||Bibo+ — Perky - Small|0.993",
    ];
    private static readonly string[] HalonesFiles =
    [
        "Body Retarget/TBSE/chara/equipment/e0613/model/c0101e0613_sho.mdl|8A96857950687EA6C1325A2AB0FA9E0C041F50EA10A07707D0E1843E7CA39029",
        "Body Retarget/TBSE/chara/equipment/e0613/model/c0101e0613_top.mdl|300345430251D82E1406FC7BE9C8B7A9AE5E14F8B70A653E289951FDF22C95D6",
    ];
    private static readonly string[] SheerRows =
    [
        "chara/equipment/e6010/model/c0101e6010_top.mdl|tbse|TBSE|Made||Bibo+ — Perky - Medium|0.993",
        "chara/equipment/e6107/model/c0101e6107_dwn.mdl|tbse|TBSE|Made||Bibo+ — Type A - Large|0.987",
    ];
    private static readonly string[] SheerFiles =
    [
        "Body Retarget/TBSE/chara/equipment/e6010/model/c0101e6010_top.mdl|7B421E9B8F5457DA8D92CE10D056739E31714D3115BB56094F771E848792C74B",
        // Re-captured on test f04d524a (build 1504): moved by redo-starlight's coarse sag (1501) alone; with NoCoarseSag
        // it is the earlier EE92D92D... exactly.
        "Body Retarget/TBSE/chara/equipment/e6107/model/c0101e6107_dwn.mdl|E564659097528BFF9CD6C49A6365777A4B435F50584D9F8C6B01E8F4F27E9BC8",
    ];
    private static readonly string[] StormblessedRows =
    [
        "chara/equipment/e0633/model/c0101e0633_dwn.mdl|tbse|TBSE|Made||LavaBod+ 2.2 — Omit · Yiggle - Gen A|0.995",
        "chara/equipment/e0633/model/c0101e0633_glv.mdl|tbse|TBSE|Made||LavaBod+ 2.2 — Omit · Buff - Sugar|1.000",
        "chara/equipment/e0633/model/c0101e0633_sho.mdl|tbse|TBSE|Made||LavaBod+ 2.2 — Omit · Yiggle - Gen A|0.995",
        "chara/equipment/e0633/model/c0101e0633_top.mdl|tbse|TBSE|Made||LavaBod+ 2.2 — Omit · Yiggle - Teardrop|1.000",
    ];
    // _dwn, _sho and _top re-captured on test f04d524a (build 1504), with NoBulge as the no-bulge test runs them: _dwn
    // moved by redo-starlight's coarse sag (1501) with the layer floor (1495) or the pressed-skin cut (1501), _sho by the
    // coarse sag with knee-standoff (1504), _top by the coarse sag alone. With NoCoarseSag, NoLayerFloor, NoPressedSkinCut
    // and NoKneeStandoff all four are the earlier 3B2B3ADD..., 0BEF4A43..., A47AAC51..., 908AC32C... exactly.
    private static readonly string[] StormblessedFiles =
    [
        "Body Retarget/TBSE/chara/equipment/e0633/model/c0101e0633_dwn.mdl|A8A5D2BF5B4716753F12D155A5470D6638D6BC5E5913FAC737C36676505BDCEA",
        "Body Retarget/TBSE/chara/equipment/e0633/model/c0101e0633_glv.mdl|0BEF4A43AB48DB6F1BCA3E0E641CB9388392F58A95CC69D2164D480384A0A3B3",
        "Body Retarget/TBSE/chara/equipment/e0633/model/c0101e0633_sho.mdl|912E3224464F4478C93D06EB7ED441378074269292C398B7D8F1E64E5DE6B49B",
        "Body Retarget/TBSE/chara/equipment/e0633/model/c0101e0633_top.mdl|127CBEE9F9572E74FEA91109EA93B25CEAFE340B2530F988C3983AF1EE75B459",
    ];

    /// <summary>
    /// The size set's "TBSE (for men)" run on a copy of a women's mod, "Make again". Its men's outcomes as rows, and the
    /// files it wrote (new, or written over during the run) as "path|sha256", both sorted; anything whose game path or
    /// file path contains one of <paramref name="leaveOut"/> left out.
    /// </summary>
    internal static (List<string> Rows, List<string> Files) TbseSizeSet(string modNeed, params string[] leaveOut)
    {
        string copy = SizeSetBatchData.CopyOf(modNeed);
        bool Kept(string s) => !leaveOut.Any(l => s.Contains(l, StringComparison.OrdinalIgnoreCase));
        try
        {
            var before = Directory.EnumerateFiles(copy, "*", SearchOption.AllDirectories)
                                  .ToDictionary(f => f, File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);
            var bodies = SizeSetBatch.ScanBodies(Path.GetDirectoryName(LocalData.Path(CrossSexRefitLocalTests.Tbse))!,
                [SizeSetBatchData.Folder(CrossSexRefitLocalTests.Bibo), SizeSetBatchData.Folder(CrossSexRefitLocalTests.Tbse),
                 SizeSetBatchData.Folder(CrossSexNudeLegsLocalTests.LavaBod)]);
            string[] keys = ["tbse@women"];
            var gameBodies = SizeSetRefit.GameBodies(keys, Game, ends: true);
            var runLog = new SizeSetRefit.RunLog();
            SizeSetRefit.Run(new SizeSetRefit.Request(copy, keys, Yas: false, Remake: true, bodies, Game, Ends: true,
                                                      race => gameBodies.GetValueOrDefault(race),
                                                      Across: new SizeSetRefit.AcrossInputs(_ => null)),
                             runLog, Maps(), Substitute.For<IPluginLog>(), _ => { }, CancellationToken.None);

            string sub = Path.Combine(copy, BodyRetargetWriter.Subfolder);
            var files = (Directory.Exists(sub) ? Directory.EnumerateFiles(sub, "*.mdl", SearchOption.AllDirectories) : [])
                        .Where(f => !before.TryGetValue(f, out var at) || File.GetLastWriteTimeUtc(f) != at)
                        .Select(f => Path.GetRelativePath(copy, f).Replace('\\', '/'))
                        .Where(Kept)
                        .Select(rel => $"{rel}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(copy, rel))))}")
                        .Order(StringComparer.Ordinal).ToList();
            var rows = runLog.Sizes.Where(s => s.Men && Kept(s.GamePath))
                             .Select(s => $"{s.GamePath}|{s.Body}|{s.Option}|{s.Status}|{s.ReasonCode}|{s.From}|" +
                                          $"{s.Measures?.Coverage:F3}")
                             .Order(StringComparer.Ordinal).ToList();
            return (rows, files);
        }
        finally
        {
            SizeSetBatchData.Delete(copy);
        }
    }

    /// <summary>The run against its pinned tables, item by item. <paramref name="newlyMade"/> are game paths a later plan
    /// makes on purpose: their rows and files may differ; nothing else may.</summary>
    private void Check(string what, string modNeed, string[] rows, string[] files, string[]? leaveOut = null,
                       string[]? newlyMade = null)
    {
        var (gotRows, gotFiles) = TbseSizeSet(modNeed, leaveOut ?? []);
        o.WriteLine($"── {what} rows");
        foreach (string r in gotRows) o.WriteLine($"\"{r}\",");
        o.WriteLine($"── {what} files");
        foreach (string f in gotFiles) o.WriteLine($"\"{f}\",");
        bool Allowed(string item) => newlyMade?.Any(p => item.Contains(Path.GetFileName(p), StringComparison.OrdinalIgnoreCase)) == true;
        var problems = new List<string>();
        foreach (string r in rows.Except(gotRows).Where(r => !Allowed(r))) problems.Add("row gone or changed: " + r);
        foreach (string r in gotRows.Except(rows).Where(r => !Allowed(r))) problems.Add("row new: " + r);
        foreach (string f in files.Except(gotFiles).Where(f => !Allowed(f))) problems.Add("file gone or changed: " + f);
        foreach (string f in gotFiles.Except(files).Where(f => !Allowed(f))) problems.Add("file new: " + f);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [LocalDataFact(LocalData.GameData, HandsFeetPortLocalTests.HalonesChosen, CrossSexRefitLocalTests.Bibo,
                   CrossSexRefitLocalTests.Tbse, CrossSexNudeLegsLocalTests.LavaBod, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-tbsex-nude-legs-4")]
    [Trait("Plan", "EDGE-tbsex-nude-legs-10")]
    [Trait("Suite", "Diagnostic")]
    public void Halones_tbse_size_set_is_unchanged() // [AC-tbsex-nude-legs-4] [EDGE-tbsex-nude-legs-10]
        => Check("halone's", HandsFeetPortLocalTests.HalonesChosen, HalonesRows, HalonesFiles, leaveOut: ["_glv"]);

    [LocalDataFact(LocalData.GameData, SizeSetCrossSexLocalTests.Sheer, CrossSexRefitLocalTests.Bibo,
                   CrossSexRefitLocalTests.Tbse, CrossSexNudeLegsLocalTests.LavaBod, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-tbsex-nude-legs-4")]
    [Trait("Plan", "EDGE-tbsex-nude-legs-10")]
    [Trait("Plan", "AC-equinox-legs-slits-7")]
    [Trait("Suite", "Diagnostic")]
    public void Sheer_tbse_size_set_is_unchanged() // [AC-tbsex-nude-legs-4] [EDGE-tbsex-nude-legs-10]
        // equinox-legs-slits: Sheer's top is sized in "Chest size", the group the game draws it from (priority 1, over
        // "Content Type"), so its men's TBSE top is cut from that group's nearest size, Perky - Small, not Perky - Medium:
        // 114 folds before, 123 now, both over the site's drop limit; TBSE-X (113) and the legs (8, 14) as they were.
        // featureless-bulge: Sheer's legs onto TBSE Gen A go onto it made featureless (its penis out, the owner's bulge in).
        => Check("sheer", SizeSetCrossSexLocalTests.Sheer, SheerRows, SheerFiles, newlyMade: ["c0101e6010_top.mdl", "c0101e6107_dwn.mdl"]);

    [LocalDataFact(LocalData.GameData, SizeSetCrossSexLocalTests.Stormblessed, CrossSexRefitLocalTests.Bibo,
                   CrossSexRefitLocalTests.Tbse, CrossSexNudeLegsLocalTests.LavaBod, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-tbsex-nude-legs-4")]
    [Trait("Plan", "EDGE-tbsex-nude-legs-10")]
    [Trait("Suite", "Diagnostic")]
    public void Stormblessed_tbse_size_set_is_unchanged() // [AC-tbsex-nude-legs-4] [EDGE-tbsex-nude-legs-10]
        // featureless-bulge: Stormblessed's legs onto TBSE Gen A go onto it made featureless (69 -> 36 folds through the job).
        => Check("stormblessed", SizeSetCrossSexLocalTests.Stormblessed, StormblessedRows, StormblessedFiles,
                 newlyMade: ["c0101e0633_dwn.mdl"]);

    /// <summary>featureless-bulge: with the bulge switched off, the two rows it makes on purpose are today's again — every
    /// row and file of the Stormblessed and Sheer TBSE size sets as pinned, none allowed to differ.</summary>
    [LocalDataFact(LocalData.GameData, SizeSetCrossSexLocalTests.Stormblessed, CrossSexRefitLocalTests.Bibo,
                   CrossSexRefitLocalTests.Tbse, CrossSexNudeLegsLocalTests.LavaBod, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-featureless-bulge-7")]
    [Trait("Plan", "EDGE-featureless-bulge-24")]
    public void Stormblessed_tbse_size_set_without_the_bulge_is_unchanged()
        => BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoBulge: true), () =>
        {
            Check("stormblessed, no bulge", SizeSetCrossSexLocalTests.Stormblessed, StormblessedRows, StormblessedFiles);
            return 0;
        });

    [LocalDataFact(LocalData.GameData, SizeSetCrossSexLocalTests.Sheer, CrossSexRefitLocalTests.Bibo,
                   CrossSexRefitLocalTests.Tbse, CrossSexNudeLegsLocalTests.LavaBod, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-featureless-bulge-7")]
    [Trait("Plan", "EDGE-featureless-bulge-24")]
    public void Sheer_tbse_size_set_without_the_bulge_is_unchanged()
        => BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoBulge: true), () =>
        {
            Check("sheer, no bulge", SizeSetCrossSexLocalTests.Sheer, SheerRows, SheerFiles, newlyMade: ["c0101e6010_top.mdl"]);
            return 0;
        });
}
