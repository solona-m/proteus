using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using Data = Proteus.Tests.SizeSetBatchData;
using SizeStatus = Proteus.Services.SizeSetRefit.SizeStatus;

namespace Proteus.Tests;

/// <summary>
/// The size set's "Keep hidden skin" box on real garments and bodies: "Sirius"'s L top, its skin cut away under the
/// cloth (made on YAB; most other tops tried here keep the whole body and carry no cut), sized onto Bibo+ and Rue and
/// made a men's onto TBSE, once as today and once with the box ticked; a shoe and a bare chest beside it. Every run is
/// made once here and read by the tests below.
/// </summary>
public sealed class KeepSkinRuns : IDisposable
{
    internal const string Sirius = LocalData.Mods + ":Sirius - by Solona";
    internal const string Coat = "chara/equipment/e0154/model/c0201e0154_top.mdl";
    internal const string CoatRel = "size/Sirius/L/chara/equipment/e0154/model/c0201e0154_top.mdl";
    internal const string Shoe = "chara/equipment/e6219/model/c0201e6219_sho.mdl";
    internal const string BareTop = "chara/equipment/e6000/model/c0201e6000_top.mdl";

    internal static readonly string[] Bodies = [Data.Rue, Data.Yab, Data.Bibo, Data.Neolithe, Data.Tbse];
    internal static readonly string[] Needs =
        [Sirius, .. Bodies, LocalData.ShiftPack, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps];

    /// <summary>One run: its copy of the mod, what it logged, and its sizes.</summary>
    internal sealed record Run(string Root, SizeSetRefit.RunLog RunLog, int Made, List<(string Template, object[] Args)> Log);

    /// <summary>(a) the request as built before the box existed; (b) explicit false (Bibo+ only); (c) ticked.</summary>
    internal Run? Default, Off, On;
    /// <summary>(d) a copy of (a) run again ticked without "Make again"; (e) the same copy again with it (Bibo+ only).</summary>
    internal Run? Again, Remade;
    /// <summary>(a) and (d) as they were before (d) and (e) ran over the copy: the author's own files and ours.</summary>
    internal Dictionary<string, byte[]> BeforeAgain = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The men's versions, unticked and ticked.</summary>
    internal Run? MenOff, MenOn;
    /// <summary>A shoe and a bare chest, unticked and ticked.</summary>
    internal Run? ShoeOff, ShoeOn, BareOff, BareOn;

    private readonly List<string> roots = [];

    public KeepSkinRuns()
    {
        if (LocalData.Missing(Needs) != null) return;
        byte[] coat = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Sirius), CoatRel));
        byte[] shoe = LocalData.PackEntryEndingWith(LocalData.ShiftPack, "e6219_sho.mdl");
        byte[] bare = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Data.Rue),
            "files/chest - smallclothes/buff - medium/chara/equipment/e0000/model/c0201e0000_top.mdl"));

        Default = Go(Mod(coat, Coat, "Orig"), ["bibo", "rue"], keep: null);
        Off = Go(Mod(coat, Coat, "Orig"), ["bibo"], keep: false);
        On = Go(Mod(coat, Coat, "Orig"), ["bibo", "rue"], keep: true);

        string again = Data.NewCopy();
        roots.Add(again);
        SizeSetRefitTests.CopyModels(Default.Root, again, withRecord: true);
        foreach (string file in Directory.EnumerateFiles(again, "*", SearchOption.AllDirectories))
            BeforeAgain[Path.GetRelativePath(again, file)] = File.ReadAllBytes(file);
        Again = Go(again, ["bibo", "rue"], keep: true);
        Remade = Go(again, ["bibo"], keep: true, remake: true);

        MenOff = Go(Mod(coat, Coat, "Orig"), ["tbse@women"], keep: false, men: true);
        MenOn = Go(Mod(coat, Coat, "Orig"), ["tbse@women"], keep: true, men: true);

        ShoeOff = Go(Mod(shoe, Shoe, "YAB WC", "Shoes"), ["bibo"], keep: false);
        ShoeOn = Go(Mod(shoe, Shoe, "YAB WC", "Shoes"), ["bibo"], keep: true);
        BareOff = Go(Mod(bare, BareTop, "Rue M"), ["yab"], keep: false);
        BareOn = Go(Mod(bare, BareTop, "Rue M"), ["yab"], keep: true);
    }

    /// <summary>A mod of one model in one Single group's one option.</summary>
    private string Mod(byte[] model, string gamePath, string option, string group = "Size")
    {
        string root = Data.NewCopy();
        roots.Add(root);
        Directory.CreateDirectory(Path.Combine(root, "piece"));
        File.WriteAllBytes(Path.Combine(root, "piece", "piece.mdl"), model);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile),
            "{ \"FileVersion\": 4, \"Name\": \"Keep skin\", \"DefaultData\": { \"Files\": {}, \"FileSwaps\": {}, \"Manipulations\": [] },"
          + $" \"Groups\": [ {{ \"Version\": 0, \"Name\": \"{group}\", \"Type\": \"Single\", \"Priority\": 0, \"DefaultSettings\": 0,"
          + $" \"Options\": [ {{ \"Name\": \"{option}\", \"Files\": {{ \"{gamePath}\": \"piece\\\\piece.mdl\" }} }} ] }} ] }}");
        return root;
    }

    /// <param name="keep">Null builds the request without the field, as every caller did before it existed.</param>
    private static Run Go(string root, IReadOnlyList<string> keys, bool? keep, bool remake = false, bool men = false)
    {
        string modsRoot = LocalData.Path(LocalData.Mods);
        var dirs = Bodies.Select(Data.Folder).ToDictionary(d => d, d => d);
        var bodies = new BodyModIndex().RefreshState(dirs, modsRoot).List;
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var request = new SizeSetRefit.Request(root, keys, Yas: false, Remake: remake, bodies,
                                               ReadGameFile: men ? Data.Game() : null,
                                               Across: men ? new SizeSetRefit.AcrossInputs(_ => null) : null);
        if (keep is { } k) request = request with { KeepHiddenSkin = k };
        var runLog = new SizeSetRefit.RunLog();
        int made = SizeSetRefit.Run(request, runLog,
                                    new UVRemapService(log, LocalData.Path(men ? LocalData.TbseUvMaps : LocalData.UvMaps)),
                                    log, _ => { }, CancellationToken.None);
        var logged = log.ReceivedCalls()
                        .Select(c => c.GetArguments())
                        .Select(a => (a.FirstOrDefault() as string ?? "", a.Skip(1).FirstOrDefault() as object[] ?? []))
                        .ToList();
        return new Run(root, runLog, made, logged);
    }

    public void Dispose()
    {
        foreach (string root in roots) Data.Delete(root);
    }
}

[Trait("Speed", "Slow")]
public class SizeSetKeepSkinLocalTests(ITestOutputHelper output, KeepSkinRuns runs) : IClassFixture<KeepSkinRuns>
{
    private const string KeptWhole = "[Proteus] size set: hidden skin kept whole";

    /// <summary>Each size our record names for a game path, by option: the saved model's bytes.</summary>
    private static Dictionary<string, byte[]> Saved(KeepSkinRuns.Run run, string gamePath)
        => (BodyRetargetWriter.ReadRecord(run.Root)?.Options ?? [])
           .Where(e => string.Equals(e.GamePath, gamePath, StringComparison.OrdinalIgnoreCase))
           .ToDictionary(e => e.Name, e => File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(run.Root, e.File)));

    private static int SkinTriangles(byte[] model)
        => ModelPartReader.Read(model)!.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                            .Sum(p => p.Triangles.Length / 3);

    /// <summary>The lowest point of the model's body skin.</summary>
    private static float LowestSkin(byte[] model)
    {
        var m = ModelPartReader.Read(model)!;
        return m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                      .SelectMany(p => p.Triangles).Select(v => m.Positions[v * 3 + 1]).DefaultIfEmpty(float.MaxValue).Min();
    }

    /// <summary>Each made size's (added, cut) skin triangles, read off its own log line.</summary>
    private static Dictionary<string, (int Added, int Cut)> SkinLogged(KeepSkinRuns.Run run)
        => run.Log.Where(l => l.Template.Contains("folded; skin {4} added, {5} cut", StringComparison.Ordinal))
                  .ToDictionary(l => (string)l.Args[1], l => (Convert.ToInt32(l.Args[4]), Convert.ToInt32(l.Args[5])));

    private static Dictionary<string, int> Folds(KeepSkinRuns.Run run)
        => run.RunLog.Sizes.Where(s => s.Status == SizeStatus.Made)
                           .ToDictionary(s => s.Option, s => s.Measures?.Folded ?? -1);

    private void Print(string what, KeepSkinRuns.Run run, string gamePath)
    {
        foreach (var s in run.RunLog.Sizes)
            output.WriteLine($"{what}: {s.Slot} {s.Option} {s.Status} {s.ReasonCode} folded {s.Measures?.Folded}");
        foreach (var (option, bytes) in Saved(run, gamePath))
            output.WriteLine($"{what}: {option} skin {SkinTriangles(bytes)} lowest {LowestSkin(bytes):F4}");
        foreach (var (option, (added, cut)) in SkinLogged(run)) output.WriteLine($"{what}: {option} added {added} cut {cut}");
    }

    [LocalDataFact(KeepSkinRuns.Sirius, Data.Rue, Data.Yab, Data.Bibo, Data.Neolithe, Data.Tbse, LocalData.ShiftPack, LocalData.GameData,
                   LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-size-set-keep-skin-2")]
    public void Unticked_is_byte_identical_to_the_default()
    {
        // [AC-size-set-keep-skin-2] The request built without the field and with it off make the very same files.
        var a = Saved(runs.Default!, KeepSkinRuns.Coat);
        var b = Saved(runs.Off!, KeepSkinRuns.Coat);
        Assert.NotEmpty(b);
        foreach (var (option, bytes) in b) Assert.Equal(a[option], bytes);
    }

    [LocalDataFact(KeepSkinRuns.Sirius, Data.Rue, Data.Yab, Data.Bibo, Data.Neolithe, Data.Tbse, LocalData.ShiftPack, LocalData.GameData,
                   LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-size-set-keep-skin-3")]
    public void Ticked_keeps_the_skin_whole_on_two_families()
    {
        // [AC-size-set-keep-skin-3] The same sizes made, each with at least the skin it had, more on each family; fewer
        // triangles cut; the cloth turned over no differently, the solve never reading the box.
        Print("off", runs.Default!, KeepSkinRuns.Coat);
        Print("on", runs.On!, KeepSkinRuns.Coat);
        var off = Saved(runs.Default!, KeepSkinRuns.Coat);
        var on = Saved(runs.On!, KeepSkinRuns.Coat);
        Assert.NotEmpty(off);
        Assert.Equal(off.Keys.Order(), on.Keys.Order());
        var loggedOff = SkinLogged(runs.Default!);
        var loggedOn = SkinLogged(runs.On!);
        Assert.Contains(loggedOff.Values, v => v.Cut > 0);   // the case is reached: the author's cut was carried
        foreach (string family in new[] { "Bibo", "Rue" })
        {
            var sizes = off.Keys.Where(o => o.StartsWith(family, StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.NotEmpty(sizes);
            Assert.Contains(sizes, o => SkinTriangles(on[o]) > SkinTriangles(off[o]));
            Assert.Contains(sizes, o => loggedOn[o].Cut < loggedOff[o].Cut);
        }
        foreach (string option in off.Keys)
        {
            Assert.True(SkinTriangles(on[option]) >= SkinTriangles(off[option]), option);
            Assert.True(loggedOn[option].Cut <= loggedOff[option].Cut, option);
        }
        Assert.Equal(Folds(runs.Default!), Folds(runs.On!));
    }

    [LocalDataFact(KeepSkinRuns.Sirius, Data.Rue, Data.Yab, Data.Bibo, Data.Neolithe, Data.Tbse, LocalData.ShiftPack, LocalData.GameData,
                   LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-size-set-keep-skin-3")]
    public void Reached_onto_skin_is_not_carried_further_down()
    {
        // [EDGE-size-set-keep-skin-3] No body skin goes in below where the cut run put it: a slot the coat only reaches
        // onto stays cut, and the legs never come down to the ankles.
        var off = Saved(runs.Default!, KeepSkinRuns.Coat);
        var on = Saved(runs.On!, KeepSkinRuns.Coat);
        Assert.NotEmpty(off);
        foreach (string option in off.Keys)
            Assert.True(LowestSkin(on[option]) >= LowestSkin(off[option]) - 0.0005f,
                        $"{option}: {LowestSkin(on[option]):F4} below {LowestSkin(off[option]):F4}");
    }

    [LocalDataFact(KeepSkinRuns.Sirius, Data.Rue, Data.Yab, Data.Bibo, Data.Neolithe, Data.Tbse, LocalData.ShiftPack, LocalData.GameData,
                   LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-size-set-keep-skin-4")]
    [Trait("Plan", "EDGE-size-set-keep-skin-12")]
    public void Mens_versions_keep_the_skin_whole()
    {
        // [AC-size-set-keep-skin-4] [EDGE-size-set-keep-skin-12] The same men's sizes saved, none refused for the box,
        // with at least the skin they had and more on one; folds as they were.
        Print("men off", runs.MenOff!, KeepSkinRuns.Coat.Replace("c0201", "c0101"));
        Print("men on", runs.MenOn!, KeepSkinRuns.Coat.Replace("c0201", "c0101"));
        string mens = KeepSkinRuns.Coat.Replace("c0201", "c0101");
        foreach (var (what, run) in new[] { ("men off", runs.MenOff!), ("men on", runs.MenOn!) })
            foreach (var l in run.Log.Where(l => l.Template.Contains("for men: {1} in {2} ms", StringComparison.Ordinal)))
                output.WriteLine($"{what}: {l.Args[1]}: {l.Args[5]}");
        var off = Saved(runs.MenOff!, mens);
        var on = Saved(runs.MenOn!, mens);
        Assert.NotEmpty(off);
        Assert.Equal(off.Keys.Order(), on.Keys.Order());
        Assert.Equal(runs.MenOff!.RunLog.Sizes.Select(s => (s.Option, s.Status)),
                     runs.MenOn!.RunLog.Sizes.Select(s => (s.Option, s.Status)));
        foreach (string option in off.Keys) Assert.True(SkinTriangles(on[option]) >= SkinTriangles(off[option]), option);
        Assert.Equal(Folds(runs.MenOff!), Folds(runs.MenOn!));
        // Ticked, nothing of the men's body is cut. (On this top the men's route cut nothing unticked either — its laid
        // skin covers the men's chest — so "more skin" is not observable here; that the box reaches the job is pinned by
        // SizeSetKeepSkinWiringTests.Mens_versions_take_the_request.)
        var said = runs.MenOn!.Log.Where(l => l.Template.Contains("for men: {1} in {2} ms", StringComparison.Ordinal))
                                  .Select(l => (string)l.Args[5]).ToList();
        Assert.NotEmpty(said);
        Assert.All(said, s => Assert.Contains(" 0 cut", s));
    }

    [LocalDataFact(KeepSkinRuns.Sirius, Data.Rue, Data.Yab, Data.Bibo, Data.Neolithe, Data.Tbse, LocalData.ShiftPack, LocalData.GameData,
                   LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-size-set-keep-skin-5")]
    [Trait("Plan", "EDGE-size-set-keep-skin-4")]
    public void Shoes_are_cut_whatever_the_box()
    {
        // [AC-size-set-keep-skin-5] [EDGE-size-set-keep-skin-4] A kept foot is cut either way: the same files.
        var off = Saved(runs.ShoeOff!, KeepSkinRuns.Shoe);
        var on = Saved(runs.ShoeOn!, KeepSkinRuns.Shoe);
        Assert.NotEmpty(off);
        Assert.Equal(off.Keys.Order(), on.Keys.Order());
        foreach (var (option, bytes) in off) Assert.Equal(bytes, on[option]);
    }

    [LocalDataFact(KeepSkinRuns.Sirius, Data.Rue, Data.Yab, Data.Bibo, Data.Neolithe, Data.Tbse, LocalData.ShiftPack, LocalData.GameData,
                   LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-size-set-keep-skin-6")]
    [Trait("Plan", "EDGE-size-set-keep-skin-6")]
    public void Old_sizes_change_only_through_make_again()
    {
        // [AC-size-set-keep-skin-6] [EDGE-size-set-keep-skin-6] Ticked without "Make again": nothing made, nothing
        // changed. With it: the Bibo+ sizes carry the ticked run's skin; the author's own file never changes.
        Assert.Equal(0, runs.Again!.Made);
        Assert.All(runs.Again.RunLog.Sizes, s => Assert.NotEqual(SizeStatus.Made, s.Status));
        string root = runs.Again.Root;
        var authorsAfterRemake = File.ReadAllBytes(Path.Combine(root, "piece", "piece.mdl"));
        Assert.Equal(runs.BeforeAgain[Path.Combine("piece", "piece.mdl")], authorsAfterRemake);

        var remade = Saved(runs.Remade!, KeepSkinRuns.Coat);
        var ticked = Saved(runs.On!, KeepSkinRuns.Coat);
        var bibo = remade.Keys.Where(o => o.StartsWith("Bibo", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(bibo);
        Assert.True(runs.Remade!.Made > 0);
        foreach (string option in bibo) Assert.Equal(SkinTriangles(ticked[option]), SkinTriangles(remade[option]));
    }

    [LocalDataFact(KeepSkinRuns.Sirius, Data.Rue, Data.Yab, Data.Bibo, Data.Neolithe, Data.Tbse, LocalData.ShiftPack, LocalData.GameData,
                   LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-size-set-keep-skin-6")]
    public void Ticked_without_make_again_leaves_every_file()
    {
        // [EDGE-size-set-keep-skin-6] Run (d) wrote nothing: every file of the copy as it was before it — checked on (d)'s
        // own record, so run (e) after it does not hide a write.
        var record = Saved(runs.Again!, KeepSkinRuns.Coat);
        var before = Saved(runs.Default!, KeepSkinRuns.Coat);
        Assert.NotEmpty(before);
        foreach (var (option, bytes) in before)
            if (!option.StartsWith("Bibo", StringComparison.OrdinalIgnoreCase)) Assert.Equal(bytes, record[option]);
    }

    [LocalDataFact(KeepSkinRuns.Sirius, Data.Rue, Data.Yab, Data.Bibo, Data.Neolithe, Data.Tbse, LocalData.ShiftPack, LocalData.GameData,
                   LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-size-set-keep-skin-7")]
    public void Bare_chest_is_the_same_either_way()
    {
        // [EDGE-size-set-keep-skin-7] Nothing cut away to keep: the same files and the same notes.
        var off = Saved(runs.BareOff!, KeepSkinRuns.BareTop);
        var on = Saved(runs.BareOn!, KeepSkinRuns.BareTop);
        Assert.NotEmpty(off);
        Assert.Equal(off.Keys.Order(), on.Keys.Order());
        foreach (var (option, bytes) in off) Assert.Equal(bytes, on[option]);
        Assert.Equal(runs.BareOff!.RunLog.Notes, runs.BareOn!.RunLog.Notes);
    }

    [LocalDataFact(KeepSkinRuns.Sirius, Data.Rue, Data.Yab, Data.Bibo, Data.Neolithe, Data.Tbse, LocalData.ShiftPack, LocalData.GameData,
                   LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-size-set-keep-skin-7")]
    public void Ticked_run_says_so_once()
    {
        // [AC-size-set-keep-skin-7]
        Assert.Single(runs.On!.Log, l => l.Template == KeptWhole);
        Assert.DoesNotContain(runs.Default!.Log, l => l.Template == KeptWhole);
        Assert.DoesNotContain(runs.Off!.Log, l => l.Template == KeptWhole);
    }
}
