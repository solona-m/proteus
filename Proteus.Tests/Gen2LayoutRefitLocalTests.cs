using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// [HS] Code, made on Tight&amp;Firm Gen2: its legs were refused for every size the size set made (only 44% of T&amp;F's
/// legs find their place on Bibo+'s by texture coordinate — its NSFW crotch is 60% of their vertices and lands nowhere),
/// though 93% of their surface does. Judged by surface, they refit, the crotch following the skin around it.
/// </summary>
public class Gen2LayoutRefitLocalTests(ITestOutputHelper output)
{
    private const string Code = LocalData.Mods + ":hs-Code-1.0.1-0uk";
    private const string TandF = LocalData.Mods + ":tight&firmgen2body-nsfw-installer";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";
    private const string TreYas = LocalData.Mods + ":hs-[YAS] Tre-5.5.3-8xB";
    private const string Rosaline = LocalData.Mods + ":hs-Rosaline-1.1.2-hM5N";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string AbBody = LocalData.Mods + ":[Cry] AB Body [Body][DT] v2.0.9";

    private const string TandFLegs = "hip size/medium/chara/equipment/e0000/model/c0201e0000_dwn.mdl";
    private const string TandFChest = "chest size/medium/chara/equipment/e0000/model/c0201e0000_top.mdl";
    private const string CodeLegs = "files/common/6/c0201e0147_dwn.mdl";
    private const string HsLegs = "files/legs - smallclothes/{0}/chara/equipment/e0000/model/c0201e0000_dwn.mdl";
    private const string BiboLegs = "bottoms - small clothes/type a - {0}/chara/equipment/e0000/model/c0201e0000_dwn.mdl";

    private static readonly Dalamud.Plugin.Services.IPluginLog Log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();

    /// <summary>One service for every test here: a gen3 map is two 134 MB TIFFs read and checked on first use.</summary>
    private static readonly Lazy<UVRemapService> Maps = new(() => new UVRemapService(Log, LocalData.Path(LocalData.UvMaps)));

    public static TheoryData<string, string> WomensLegs => new()
    {
        { Bibo, string.Format(BiboLegs, "small") },
        { Bibo, string.Format(BiboLegs, "medium") },
        { Bibo, string.Format(BiboLegs, "large") },
        { Rue, string.Format(HsLegs, "watermelon crushers - a") },
        { Yab, string.Format(HsLegs, "watermelon crushers - a") },
        { Yab, string.Format(HsLegs, "skull crushers - a") },
        { TreYas, "files/common/15/c0201e0000_dwn.mdl" },
        { TreYas, "files/common/10/c0201e0000_dwn.mdl" },
        { Rosaline, "files/common/13/c0201e0000_dwn.mdl" },
        { Neolithe, "default legs - smallclothes/sfw small.mdl" },
        { Neolithe, "default legs - smallclothes/sfw medium.mdl" },
        { Neolithe, "default legs - smallclothes/sfw large.mdl" },
    };

    [LocalDataTheory(Code, TandF, Bibo, Rue, Yab, TreYas, Rosaline, Neolithe)]
    [MemberData(nameof(WomensLegs))]
    [Trait("Plan", "AC-gen2-legs-refit-2")]
    [Trait("Plan", "AC-gen2-legs-refit-4")]
    public void TandF_legs_pair_with_every_womens_legs(string body, string rel)
        => PairsAndFits(body, rel, new UVRemapService(Log, "."));

    [LocalDataFact(Code, TandF, AbBody, LocalData.UvMaps)]
    [Trait("Plan", "AC-gen2-legs-refit-3")]
    [Trait("Plan", "EDGE-gen2-legs-refit-12")]
    public void TandF_legs_pair_with_AB_Body_legs()
        => PairsAndFits(AbBody, "Smallclothes Legs/AB - Innie 1/chara/equipment/e0000/model/c0201e0000_dwn.mdl", Maps.Value);

    /// <summary>
    /// T&amp;F's legs onto <paramref name="rel"/> of <paramref name="body"/>: paired by surface, every skin vertex placed,
    /// no landing far from its point; then Code's legs refitted onto them as the size set refits them, measured inside the
    /// pass — triangles folded at the end of it, and cloth points inside the new body.
    /// </summary>
    private void PairsAndFits(string body, string rel, UVRemapService uvRemap)
    {
        var (source, sourceCat) = Mod(TandF);
        var (target, targetCat) = Mod(body);
        string targetPath = BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(body), rel);
        Assert.Null(BodyRetarget.BuildPair("_dwn", BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(TandF), TandFLegs),
                                           targetPath, "_dwn", false, RefitCore.MaskOf(source, sourceCat, "_dwn", null),
                                           RefitCore.MaskOf(target, targetCat, "_dwn", null), uvRemap, out var pair));
        var atlas = Assert.IsType<UvAtlasCorrespondence>(pair.Correspondence);
        var m = atlas.Measured;
        output.WriteLine($"{target} / {rel}: by count {m.VertexCoverage:P1} ({m.Landed} of {m.Placeable}), by area " +
                         $"{m.AreaCoverage:P1}, {m.Carried} carried, {m.OffSheet} off-sheet, shift max " +
                         $"{m.MaxShift * 1000f:F1} mm, p99 {m.P99Shift * 1000f:F1} mm");
        output.WriteLine(atlas.Describe());

        var far = Enumerable.Range(0, atlas.Field.Count)
                            .Where(v => atlas.Field[v] is { } d && d.Length() > MaxLanding).ToList();
        foreach (int v in far.Take(12))
            output.WriteLine($"  far: vertex {v} at ({atlas.Source.Positions[v * 3]:F3}, {atlas.Source.Positions[v * 3 + 1]:F3}, " +
                             $"{atlas.Source.Positions[v * 3 + 2]:F3}) moved {atlas.Field[v]!.Value.Length() * 1000f:F1} mm " +
                             $"by {atlas.Field[v]}");
        output.WriteLine($"  {far.Count} landed further than {MaxLanding * 1000f:F0} mm");

        Assert.True(m.AreaCoverage >= 0.8f, $"by area {m.AreaCoverage:P1}");
        var skin = atlas.Source.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                        .SelectMany(p => p.Triangles).Distinct().ToList();
        Assert.All(skin, v => Assert.True(atlas.Field[v].HasValue, $"skin vertex {v} has no displacement"));
        // Two bodies' points lie centimetres apart, not a body's length; a landing further than this is a wrong island.
        Assert.True(m.MaxShift < MaxLanding, $"a landing moved {m.MaxShift * 1000f:F1} mm");

        // Code's legs onto them, as the size set refits them; measured on the pass's last nodes.
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Code), CodeLegs));
        var legs = ModelPartReader.Read(bytes)!;
        Vec3[]? final = null;
        var plan = RefitCore.PlanRefit(legs, bytes, [pair], "_dwn", true, false, out _,
                                       trace: (s, d) => { if (s == "layer-order") final = d; });
        Assert.NotNull(final);
        var sets = BodyRetarget.Sets.From(legs, new HashSet<int>(), BodyRetarget.PartedSkinMeshes(legs, [pair], "_dwn", true, null));
        int folds = BodyRetarget.FoldedTriangles(sets, final!);
        var inside = BodySizeMatch.ClothAgainst(ModelPartReader.Read(plan.Model)!, targetPath)!.Value;
        output.WriteLine($"Code's legs: {folds} folded at the end of the pass, {inside.Inside} cloth points inside the body, " +
                         $"worst move {plan.Report.WorstMove * 1000f:F1} mm");
        Assert.NotNull(ModelPartReader.Read(plan.Model));
    }

    /// <summary>The furthest a point of one body may land from its own on another (60 mm).</summary>
    private const float MaxLanding = 0.06f;

    [LocalDataFact(TandF, Bibo)]
    [Trait("Plan", "AC-gen2-legs-refit-6")]
    public void TandF_top_onto_Bibo_is_matched_as_before()
    {
        // Accepted by count before this change: no surface judgement, nothing carried, and what missed still has no
        // displacement of its own — left for the refit's fill, as it always was.
        var (source, sourceCat) = Mod(TandF);
        var (target, targetCat) = Mod(Bibo);
        Assert.Null(BodyRetarget.BuildPair("_top", BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(TandF), TandFChest),
                                           BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Bibo),
                                               "breasts - small clothes/nude - medium/chara/equipment/e0000/model/c0201e0000_top.mdl"),
                                           "_top", false, RefitCore.MaskOf(source, sourceCat, "_top", null),
                                           RefitCore.MaskOf(target, targetCat, "_top", null), new UVRemapService(Log, "."),
                                           out var pair));
        var atlas = Assert.IsType<UvAtlasCorrespondence>(pair.Correspondence);
        output.WriteLine(atlas.Describe());
        Assert.True(atlas.Measured.VertexCoverage >= 0.8f);
        Assert.Equal(0, atlas.Measured.Carried);
        Assert.DoesNotContain("no place on the target's layout", atlas.Describe());
        Assert.Equal(atlas.Measured.Placeable - atlas.Measured.Landed, atlas.Field.Count(f => f == null)
                     - (atlas.Source.Positions.Length / 3 - Skin(atlas.Source).Count));
    }

    [LocalDataFact(Code, TandF, Bibo, Rue)]
    [Trait("Plan", "AC-gen2-legs-refit-1")]
    [Trait("Plan", "AC-gen2-legs-refit-7")]
    [Trait("Plan", "AC-gen2-legs-refit-2")]
    public void Code_gains_legs_for_every_size_its_top_gets()
    {
        string copy = CodeAsShipped();
        try
        {
            var (result, log, request) = Run(copy, ["bibo", "rue"], remake: false);
            Assert.True(result.Made > 0);
            Assert.DoesNotContain(result.Notes, n => n.Contains("Legs", StringComparison.Ordinal));
            AssertEveryOptionHasRefittedLegs(copy);
            Assert.Contains(log, l => l.Contains("size set: Legs", StringComparison.Ordinal)
                                      && l.Contains("no place on the target's layout", StringComparison.Ordinal));
            Assert.DoesNotContain(log, l => l.Contains("Only 44%", StringComparison.Ordinal));

            var (again, _, _) = Run(copy, ["bibo", "rue"], remake: false);
            Assert.Equal(0, again.Made);
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    [LocalDataFact(Code, TandF, Bibo, Rue)]
    [Trait("Plan", "EDGE-gen2-legs-refit-10")]
    public void A_mod_sized_before_gains_legs_on_a_plain_re_run()
    {
        // As the user's Code was left by the run before this change: each size we added carries the author's legs.
        string copy = CodeAsShipped();
        try
        {
            Assert.True(Run(copy, ["bibo", "rue"], remake: false).Result.Made > 0);
            var ours = BodyRetargetWriter.ReadRecord(copy)!.Options.Where(e => e.GamePath.EndsWith("_dwn.mdl", StringComparison.Ordinal))
                                         .Select(e => e.Name).ToHashSet();
            Assert.NotEmpty(ours);
            RestoreAuthorsLegs(copy, ours);

            var (result, _, _) = Run(copy, ["bibo", "rue"], remake: false);
            Assert.True(result.Made > 0);
            AssertEveryOptionHasRefittedLegs(copy);
            Assert.Equal(0, Run(copy, ["bibo", "rue"], remake: false).Result.Made);
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    [LocalDataFact(Code, TandF, Bibo, Rue)]
    [Trait("Plan", "EDGE-gen2-legs-refit-11")]
    public void Remaking_moves_each_top_s_hem_onto_the_new_hips()
    {
        string copy = CodeAsShipped();
        try
        {
            Assert.True(Run(copy, ["bibo", "rue"], remake: false).Result.Made > 0);
            var (remade, log, _) = Run(copy, ["bibo", "rue"], remake: true);
            Assert.True(remade.Made > 0);
            Assert.DoesNotContain(log, l => l.Contains("hips left as they are", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("size set: Body", StringComparison.Ordinal)
                                      && l.Contains(": hips: ", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    private static (string Dir, BodySizeCatalog Catalog) Mod(string need)
        => (Path.GetFileName(LocalData.Path(need)), BodySizeCatalog.Read(LocalData.Path(need)));

    private static List<int> Skin(ModelParts m)
        => m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                  .SelectMany(p => p.Triangles).Distinct().ToList();

    /// <summary>Code as its author ships it: its manifests and models, without the sizes Proteus added to it.</summary>
    private static string CodeAsShipped()
    {
        string copy = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        SizeSetRefitTests.CopyModels(LocalData.Path(Code), copy, withRecord: false);
        foreach (var option in PenumbraModMeta.TryReadFileOptions(copy, "Size") ?? [])
            if (option.Files != null
                && option.Files.Values.Any(f => RefitCore.Normal(f).StartsWith("body retarget/", StringComparison.Ordinal)))
                Assert.NotNull(PenumbraModMeta.RemoveOption(copy, "Size", option.Name));
        Assert.Equal(["Large", "Medium"], (PenumbraModMeta.TryReadFileOptions(copy, "Size") ?? []).Select(o => o.Name).Order());
        return copy;
    }

    private (SizeSetRefit.Result Result, List<string> Log, SizeSetRefit.Request Request) Run(string copy,
        IReadOnlyList<string> keys, bool remake)
    {
        string modsRoot = LocalData.Path(LocalData.Mods);
        var mods = new[] { TandF, Bibo, Rue }.Select(n => Path.GetFileName(LocalData.Path(n))).ToDictionary(d => d, d => d);
        var bodies = new BodyModIndex().RefreshState(mods, modsRoot).List;
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var request = new SizeSetRefit.Request(copy, keys, Yas: false, Remake: remake, bodies);
        var result = SizeSetRefit.Run(request, new UVRemapService(log, "."), log, p => output.WriteLine(p), CancellationToken.None);
        foreach (string note in result.Notes) output.WriteLine("note: " + note);
        var lines = new List<string>();
        foreach (var call in log.ReceivedCalls())
        {
            var args = call.GetArguments();
            if (args.FirstOrDefault() is not string template) continue;
            var values = args.Skip(1).FirstOrDefault() as object[] ?? [];
            string line;
            try { line = string.Format(template.Replace("{0:F1}", "{0}"), values); }
            catch (FormatException) { line = template + " | " + string.Join(", ", values); }
            lines.Add(line);
            output.WriteLine("log: " + line);
        }
        return (result, lines, request);
    }

    /// <summary>Every size the run added to Code's "Size" group draws refitted legs, not the author's.</summary>
    private static void AssertEveryOptionHasRefittedLegs(string copy)
    {
        var added = (PenumbraModMeta.TryReadFileOptions(copy, "Size") ?? [])
                    .Where(o => o.Name is not ("Large" or "Medium")).ToList();
        Assert.NotEmpty(added);
        foreach (var option in added)
        {
            Assert.True(option.Files!.TryGetValue("chara/equipment/e0147/model/c0201e0147_dwn.mdl", out var legs),
                        $"{option.Name} has no legs");
            Assert.StartsWith("body retarget/", RefitCore.Normal(legs!));
        }
    }

    /// <summary>
    /// Points the legs of the named options back at the author's file and forgets they were refitted, as a run before
    /// this change left them: the record lists only their tops.
    /// </summary>
    private static void RestoreAuthorsLegs(string copy, IReadOnlySet<string> names)
    {
        var indented = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        string meta = Path.Combine(copy, PenumbraModMeta.MetaFile);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(meta))!;
        foreach (var group in json["Groups"]!.AsArray())
        {
            if ((string?)group!["Name"] != "Size") continue;
            foreach (var option in group["Options"]!.AsArray())
                if (names.Contains((string)option!["Name"]!) && option["Files"] is System.Text.Json.Nodes.JsonObject files)
                    files["chara/equipment/e0147/model/c0201e0147_dwn.mdl"] = "files\\common\\6\\c0201e0147_dwn.mdl";
        }
        File.WriteAllText(meta, json.ToJsonString(indented));

        string recordPath = Path.Combine(copy, SidecarDiscoveryService.SidecarSubdir, BodyRetargetWriter.RecordFile);
        var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(recordPath))!;
        var options = record["Options"]!.AsArray();
        foreach (var entry in options.Where(e => ((string?)e!["GamePath"])?.EndsWith("_dwn.mdl", StringComparison.Ordinal) == true)
                                     .ToList())
            options.Remove(entry);
        File.WriteAllText(recordPath, record.ToJsonString(indented));
        Assert.DoesNotContain(BodyRetargetWriter.ReadRecord(copy)!.Options, e => e.GamePath.EndsWith("_dwn.mdl", StringComparison.Ordinal));
    }
}
