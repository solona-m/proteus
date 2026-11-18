using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Applenzo's Sarelle outfit, split at the waist between its top and its legs, refitted as the user reported it (top Tre
/// Omit M → S, legs Bibo+ Type A Large → Tre Omit Medium Gen C): the two dress edges, coincident in the author's files,
/// must still coincide in the two refits (see <see cref="BodyRetarget.PinSharedSeam"/>).
/// </summary>
[Trait("Speed", "Slow")]
public class SarelleSplitSeamLocalTests(ITestOutputHelper output)
{
    private const string Race = "0201";
    internal const string TreMTop = "top size/tre m/chara/equipment/e0628/model/c0201e0628_top.mdl";
    internal const string BiboLLegs = "leg size/bibo l/chara/equipment/e0628/model/c0201e0628_dwn.mdl";
    internal const string YabWcLegs = "leg size/yab wc/chara/equipment/e0628/model/c0201e0628_dwn.mdl";
    internal const string YabScLegs = "leg size/yab sc/chara/equipment/e0628/model/c0201e0628_dwn.mdl";

    internal static (byte[] Bytes, ModelParts Parts, XivLiveMesh.SkinnedMesh Skin, HashSet<string> Bones) Load(string rel)
        => LoadFrom(LocalData.SarelleMod, rel);

    internal static (byte[] Bytes, ModelParts Parts, XivLiveMesh.SkinnedMesh Skin, HashSet<string> Bones) LoadFrom(
        string mod, string rel)
    {
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(mod), rel));
        return (bytes, ModelPartReader.Read(bytes)!, ModelSkinReader.Read(bytes, null, null)!,
                new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal));
    }

    private static (string Dir, BodySizeCatalog Catalog) Body(string need)
        => (Path.GetFileName(LocalData.Path(need)), BodySizeCatalog.Read(LocalData.Path(need)));

    private static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>The top's and the legs' dress vertices coincident in the author's files, as (top vertex, legs vertex).</summary>
    internal static List<(int Top, int Legs)> AuthoredPairs(ModelParts top, ModelParts legs)
    {
        var legsCloth = Cloth(legs);
        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / 1e-4f), (int)MathF.Floor(p.Y / 1e-4f), (int)MathF.Floor(p.Z / 1e-4f));
        foreach (int j in legsCloth)
        {
            var key = Cell(At(legs, j));
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(j);
        }
        var pairs = new List<(int, int)>();
        foreach (int i in Cloth(top))
        {
            var p = At(top, i);
            var (cx, cy, cz) = Cell(p);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                            foreach (int j in list)
                                if (Vector3.Distance(p, At(legs, j)) <= 5e-5f) pairs.Add((i, j));
        }
        return pairs;
    }

    private static int[] Cloth(ModelParts m)
        => [.. m.Parts.Where(p => p.Island < 0 && p.Material.Contains("top_dress", StringComparison.Ordinal))
                      .SelectMany(p => p.Triangles).Distinct()];

    /// <summary>
    /// How far apart each authored pair ends in the pass (authored position plus the refit's move, in the part reader's
    /// order), and how many end within 0.05 mm.
    /// </summary>
    internal static float Gap(ModelParts top, BodyRetarget.Planned topPlan, ModelParts legs, BodyRetarget.Planned legsPlan,
                              List<(int Top, int Legs)> pairs, out int within)
    {
        float worst = 0f;
        within = 0;
        foreach (var (i, j) in pairs)
        {
            var dt = topPlan.Edit.DeltaAt(i);
            var dl = legsPlan.Edit.DeltaAt(j);
            float d = Vector3.Distance(At(top, i) + new Vector3(dt.X, dt.Y, dt.Z), At(legs, j) + new Vector3(dl.X, dl.Y, dl.Z));
            worst = MathF.Max(worst, d);
            if (d <= 5e-5f) within++;
        }
        return worst;
    }

    /// <summary>
    /// On the written models: how many of the authored pairs' raw vertices (both sheets) are drawn within 0.05 mm of
    /// where the author put them in BOTH written files — the written models are reordered and cut, so by position.
    /// </summary>
    internal static int HeldOnWritten(ModelParts top, byte[] topOut, byte[] legsOut, List<(int Top, int Legs)> pairs)
    {
        var a = Positions(topOut);
        var b = Positions(legsOut);
        return pairs.Count(p => Has(a, At(top, p.Top)) && Has(b, At(top, p.Top)));
    }

    private static Dictionary<(int, int, int), List<Vector3>> Positions(byte[] model)
    {
        var m = ModelPartReader.Read(model)!;
        var grid = new Dictionary<(int, int, int), List<Vector3>>();
        for (int v = 0; v < m.Positions.Length / 3; v++)
        {
            var p = At(m, v);
            var key = ((int)MathF.Floor(p.X / 1e-4f), (int)MathF.Floor(p.Y / 1e-4f), (int)MathF.Floor(p.Z / 1e-4f));
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(p);
        }
        return grid;
    }

    private static bool Has(Dictionary<(int, int, int), List<Vector3>> grid, Vector3 p)
    {
        var (cx, cy, cz) = ((int)MathF.Floor(p.X / 1e-4f), (int)MathF.Floor(p.Y / 1e-4f), (int)MathF.Floor(p.Z / 1e-4f));
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    if (grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list) && list.Any(q => Vector3.Distance(p, q) <= 5e-5f))
                        return true;
        return false;
    }

    /// <summary>The user's pairs, as the sidecar recorded them, with or without the shared seam.</summary>
    internal static (BodyRetarget.Planned Top, BodyRetarget.Planned Legs) RefitUserCombination(
        BodyRetarget.SharedSeam? topSeam, BodyRetarget.SharedSeam? legsSeam, Action<string>? log = null)
    {
        var tre = Body(LocalData.TreMod);
        var bibo = Body(LocalData.BiboPlusMod);
        var bodies = new List<(string Dir, BodySizeCatalog Catalog)> { tre, bibo };

        var top = Load(TreMTop);
        Assert.Null(BodyRetarget.BuildPair("_top", Path.Combine(LocalData.Path(LocalData.TreMod), "files/common/4/c0201e0000_top.mdl"),
                                           Path.Combine(LocalData.Path(LocalData.TreMod), "files/common/3/c0201e0000_top.mdl"),
                                           "_top", false, null, null, null, out var topPair));
        var topPlan = BodyRetarget.Plan(top.Parts, top.Bytes, [topPair], "_top", log: log, sharedSeam: topSeam);

        var legs = Load(BiboLLegs);
        var pick = RefitCore.DetectSource(legs.Parts, legs.Bones, "_dwn", Race, bodies, tre.Dir, _ => true)!;
        Assert.Equal(bibo.Dir, pick.Dir);
        var beneath = RefitCore.ChestBeneathOf(legs.Parts, pick, Race, bodies,
                                               dir => bodies.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase)).Catalog);
        Assert.Null(BodyRetarget.BuildPair("_dwn", bibo.Catalog.PathOf(pick.Option),
                                           Path.Combine(LocalData.Path(LocalData.TreMod), "files/common/17/c0201e0000_dwn.mdl"),
                                           "_dwn", false, null, null, null, out var legsPair));
        var pairs = new List<BodyRetarget.SlotPair> { legsPair };
        if (beneath != null)
        {
            Assert.Null(BodyRetarget.BuildPair("_top", beneath.Catalog.PathOf(beneath.Chest),
                                               Path.Combine(LocalData.Path(LocalData.TreMod), "files/common/3/c0201e0000_top.mdl"),
                                               "_top", false, null, null, null, out var chestPair));
            pairs.Add(chestPair with { ShapeOnly = true });
        }
        var legsPlan = BodyRetarget.Plan(legs.Parts, legs.Bytes, pairs, "_dwn", replaceSkin: true, acrossBodies: true,
                                         log: log, sharedSeam: legsSeam);
        return (topPlan, legsPlan);
    }

    /// <summary>The gate on the installed mod onto <paramref name="body"/>, reading over Tre, Bibo+ and Rue.</summary>
    internal static RefitCore.SeamGate Gate(string body, out RefitCore.SeamFiles files, List<string> notes, out int reads,
                                            bool bellyCheck = true)
    {
        var bodies = new[] { Body(LocalData.TreMod), Body(LocalData.BiboPlusMod), Body(LocalData.RueMod), Body(LocalData.YabMod) }.ToList();
        var target = bodies.First(b => string.Equals(b.Dir, Path.GetFileName(LocalData.Path(body)), StringComparison.OrdinalIgnoreCase));
        string root = LocalData.Path(LocalData.SarelleMod);
        files = RefitCore.SeamFilesOf(PenumbraModMeta.ReadAllRedirects(root), BodyRetargetWriter.ReadRecord(root), Race, notes.Add)!;
        int count = 0;
        var gate = RefitCore.ResolveSeamGate(root, files, Race, target.Dir, target.Catalog, target.Dir, target.Catalog,
                                             (_, _, parts, bones) =>
                                             {
                                                 count++;
                                                 return RefitCore.DetectSource(parts, bones, "_top", Race, bodies, target.Dir, _ => true);
                                             }, notes.Add, bellyCheck);
        reads = count;
        return gate;
    }

    [LocalDataFact(LocalData.SarelleMod, LocalData.TreMod, LocalData.BiboPlusMod, LocalData.RueMod)]
    [Trait("Plan", "EDGE-split-waist-seam-7")]
    [Trait("Plan", "EDGE-split-waist-seam-12")]
    public void Gate_SarelleOntoTre_PicksTheMainWaist()
    {
        var notes = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var gate = Gate(LocalData.TreMod, out var files, notes, out int reads);
        output.WriteLine($"Step 0(a): {files.Tops.Count} chest files, {files.Legs.Count} legs files");
        output.WriteLine($"gate onto Tre in {clock.Elapsed.TotalSeconds:0.0} s, {reads} readings: "
                         + $"{gate.Top?.File ?? "none"} ({gate.Top?.Points.Count ?? 0} places) {gate.Refusal}");
        notes.ForEach(output.WriteLine);
        var noBelly = Gate(LocalData.TreMod, out _, [], out _, bellyCheck: false);
        output.WriteLine($"without the belly check: {noBelly.Top?.File ?? noBelly.Refusal}");
        Assert.All(files.Tops.Concat(files.Legs), f => Assert.DoesNotContain("body retarget", f.File, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(gate.Top);
        Assert.Equal(133, gate.Top!.Points.Count);
        Assert.All(gate.Top.Points.At, p => Assert.InRange(p.Y, 1.025f, 1.070f));
    }

    [LocalDataFact(LocalData.SarelleMod, LocalData.TreMod, LocalData.BiboPlusMod, LocalData.RueMod)]
    [Trait("Plan", "EDGE-split-waist-seam-2")]
    public void Gate_SarelleOntoBibo_HoldsNothing()
    {
        var notes = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var gate = Gate(LocalData.BiboPlusMod, out _, notes, out int reads);
        output.WriteLine($"Step 0(h): all-miss gate onto Bibo+ in {clock.Elapsed.TotalSeconds:0.0} s cold, {reads} readings: {gate.Refusal}");
        Assert.Null(gate.Top);
        Assert.Contains("made on", gate.Refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// The size set on a copy of the mod: every size it cuts from a main-waist source holds the split where the author put
    /// it, so the author's own pieces on that body (which it leaves as they are) close against it; a size cut from another
    /// waist's source is left to the refit and said so. Onto YAB it makes only YAB S (the author ships YAB's legs).
    /// <para/>
    /// Onto plain YAB, whose Yab M top and Yab WC/SC legs are on the main waist. A YAS run takes only YAS-rigged sources,
    /// and Sarelle's only ones are its Rue-waist pieces (Yiggles Rue tops, Yiggle Rue WC legs: 0 of 133 places): measured
    /// 2026-11-18 onto YAS Tre, both legs sizes left unheld and logged — the off-waist case, a deferred known-issue.
    /// </para>
    /// </summary>
    [LocalDataTheory(LocalData.SarelleMod, LocalData.TreMod, LocalData.BiboPlusMod, LocalData.RueMod, LocalData.YabMod,
                     LocalData.UvMaps)]
    [InlineData("yab", false, 1)]
    [InlineData("tre", true, 0)]
    [Trait("Plan", "AC-split-waist-seam-4")]
    [Trait("Plan", "EDGE-split-waist-seam-33")]
    public void SizeSet_FloorPinned_ZeroDrops(string key, bool yas, int floor)
    {
        var gate = Gate(key == "yab" ? LocalData.YabMod : LocalData.TreMod, out _, [], out _);
        var points = gate.Top!.Points;
        string copy = Path.Combine(Path.GetTempPath(), "proteus-sarelle-seam-" + Guid.NewGuid().ToString("N"));
        try
        {
            SizeSetRefitTests.CopyModels(LocalData.Path(LocalData.SarelleMod), copy, withRecord: true);
            var before = Directory.GetFiles(copy, "*.mdl", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            var log = NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
            NSubstitute.SubstituteExtensions.When(log, l => l.Information(NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Any<object[]>()))
                       .Do(ci => lines.Add(string.Format(ci.ArgAt<string>(0), ci.ArgAt<object[]>(1))));
            var bodies = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods));
            SizeSetRefit.Run(new SizeSetRefit.Request(copy, [key], Yas: yas, Remake: false, bodies, _ => null, Ends: false,
                                                      _ => null, Piercings: false),
                             new UVRemapService(log, LocalData.Path(LocalData.UvMaps)), log, _ => { }, System.Threading.CancellationToken.None);

            lines.Where(l => l.Contains("shared seam", StringComparison.Ordinal) || l.Contains("size set:", StringComparison.Ordinal) && l.Contains(" from ", StringComparison.Ordinal))
                 .ToList().ForEach(output.WriteLine);
            var written = Directory.GetFiles(copy, "*.mdl", SearchOption.AllDirectories).Where(f => !before.Contains(f)).ToList();
            int pinned = 0;
            foreach (string file in written.Where(f => f.EndsWith("_dwn.mdl", StringComparison.OrdinalIgnoreCase)
                                                      || f.EndsWith("_top.mdl", StringComparison.OrdinalIgnoreCase)))
            {
                var grid = Positions(File.ReadAllBytes(file));
                int held = points.At.Count(p => Has(grid, p));
                output.WriteLine($"{Path.GetRelativePath(copy, file)}: {held} of {points.Count} split places where the author put them");
                if (held == points.Count) pinned++;
            }
            Assert.DoesNotContain(lines, l => l.Contains("shared seam not held", StringComparison.Ordinal));
            Assert.True(pinned >= floor, $"{pinned} size(s) held the split, fewer than {floor}");
            if (floor == 0)
                Assert.Contains(lines, l => l.Contains("another waist", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(copy, recursive: true); } catch (IOException) { }
        }
    }

    [LocalDataFact(LocalData.SarelleMod, LocalData.TreMod, LocalData.BiboPlusMod)]
    [Trait("Plan", "EDGE-split-waist-seam-35")]
    public void Sarelle_OpenEdges_WithAndWithoutMerge()
    {
        var top = Load(TreMTop);
        var legs = new[] { BiboLLegs, YabWcLegs, YabScLegs }.Select(Load).ToList();
        var points = BodyRetarget.SplitPoints(top.Parts, top.Skin, legs.Select(l => (l.Parts, l.Skin)));
        Assert.NotNull(points);
        output.WriteLine($"split places of the tre m top against the main-waist legs: {points!.Count}");
        foreach (var l in legs)
        {
            var one = BodyRetarget.SplitPoints(top.Parts, top.Skin, [(l.Parts, l.Skin)]);
            var noMerge = BodyRetarget.OpenEdgesOf(top.Parts, mergeSheets: false);
            output.WriteLine($"  one legs file: {one?.Count ?? 0}; open nodes without the merge {noMerge.Open.Count(o => o)}");
            Assert.Equal(133, one!.Count);
        }
        Assert.Equal(133, points.Count);
        foreach (var l in legs)
        {
            var (verts, positions, coverage) = BodyRetarget.SeamVertices(l.Parts, l.Skin, points);
            output.WriteLine($"  legs: {positions} places, {verts.Length} vertices, coverage {coverage:0.000}");
            Assert.True(coverage >= BodyRetarget.SeamCover);
        }
    }

    [LocalDataFact(LocalData.SarelleMod, LocalData.TreMod, LocalData.BiboPlusMod)]
    [Trait("Plan", "AC-split-waist-seam-1")]
    public void UserCombination_WaistHeld()
    {
        var top = Load(TreMTop);
        var legs = Load(BiboLLegs);
        var pairs = AuthoredPairs(top.Parts, legs.Parts);
        output.WriteLine($"authored coincident dress pairs: {pairs.Count}");

        var (topBare, legsBare) = RefitUserCombination(null, null);
        float bare = Gap(top.Parts, topBare, legs.Parts, legsBare, pairs, out int bareWithin);
        output.WriteLine($"without the hold: worst {bare * 1000f:0.00} mm, {bareWithin} within 0.05 mm");

        var main = new[] { BiboLLegs, YabWcLegs, YabScLegs }.Select(Load).ToList();
        var points = BodyRetarget.SplitPoints(top.Parts, top.Skin, main.Select(l => (l.Parts, l.Skin)))!;
        var tv = BodyRetarget.SeamVertices(top.Parts, top.Skin, points);
        var lv = BodyRetarget.SeamVertices(legs.Parts, legs.Skin, points);
        var log = new List<string>();
        var (topHeld, legsHeld) = RefitUserCombination(new(tv.Vertices, tv.Positions, "tre m top on Tre"),
                                                       new(lv.Vertices, lv.Positions, "tre m top on Tre"), log.Add);
        log.Where(l => l.StartsWith("shared seam", StringComparison.Ordinal)).ToList().ForEach(output.WriteLine);
        float held = Gap(top.Parts, topHeld, legs.Parts, legsHeld, pairs, out int heldWithin);
        int written = HeldOnWritten(top.Parts, topHeld.Model, legsHeld.Model, pairs);
        output.WriteLine($"written models: {written} of {pairs.Count} pairs drawn where the author put them in both "
                         + $"(without the hold {HeldOnWritten(top.Parts, topBare.Model, legsBare.Model, pairs)})");
        output.WriteLine($"with the hold: worst {held * 1000f:0.00} mm, {heldWithin} of {pairs.Count} within 0.05 mm; "
                         + $"top pinned {topHeld.Report.SeamPinnedNodes}/{topHeld.Report.SeamPinnedVertices}, "
                         + $"legs pinned {legsHeld.Report.SeamPinnedNodes}/{legsHeld.Report.SeamPinnedVertices}; "
                         + $"folds {topBare.Report.Folded}/{legsBare.Report.Folded} → {topHeld.Report.Folded}/{legsHeld.Report.Folded}; "
                         + $"band held {legsHeld.Report.SeamBandHeld}, near {legsHeld.Report.SeamBandNear}");
        Assert.Null(topHeld.Report.SeamDropped);
        Assert.Null(legsHeld.Report.SeamDropped);
        Assert.Equal(133, topHeld.Report.SeamPinnedNodes);
        Assert.Equal(133, legsHeld.Report.SeamPinnedNodes);
        Assert.Equal(pairs.Count, heldWithin);
        Assert.Equal(pairs.Count, written);
        Assert.True(held <= 5e-5f, $"{held * 1000f:0.000} mm");
        Assert.True(topHeld.Report.Folded <= topBare.Report.Folded);
        Assert.True(legsHeld.Report.Folded <= legsBare.Report.Folded);
    }
}
