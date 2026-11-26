using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Proteus.Services.Bulge;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The genital switches on the garment they were made for (plan genital-switches): red's Beach Briefs (TBSE), its own
/// skin carrying Gen A–D's penises behind its options, refitted from Gen A onto TBSE and TBSE-X bodies.
/// </summary>
[Trait("Speed", "Slow")]
public class GenitalSwitchesLocalTests(ITestOutputHelper output)
{
    private const string TbseOpt = LocalData.Mods + ":The Body SE/p2/step 6- smallclothes options/";
    private const string TbseX = LocalData.Mods + ":The Body SE-X IVCS2 V1_1_0/";
    private const string E0 = "/chara/equipment/e0000/model/c0101e0000_dwn.mdl";
    private const string Penis = "files/p1/body type/default (penis)/chara/equipment/e0194/model/c0101e0194_dwn.mdl";

    /// <summary>The bodies: name, need, across body mods.</summary>
    private static readonly (string Name, string Need, bool Across)[] Bodies =
    [
        ("TBSE Gen B", TbseOpt + "gen b" + E0, false),
        ("TBSE Gen C", TbseOpt + "gen c" + E0, false),
        ("TBSE Gen D", TbseOpt + "gen d" + E0, false),
        ("TBSE-X Flaccid M A", TbseX + "p1/legs (smallclothes legs)/flaccid m a" + E0, true),
        ("TBSE-X Flaccid L B", TbseX + "common/2/c0101e0000_dwn.mdl", true),
        ("TBSE-X Erect L cut", TbseX + "p1/legs (smallclothes legs)/erect l cut" + E0, true),
    ];

    private static Vector3 At(ModelParts m, int v) => GenitalParts.At(m, v);

    /// <summary>The pack's IMC groups: name, default mask, options (name, mask).</summary>
    private static List<(string Name, int Default, List<(string Name, int Mask)> Options)> Groups()
    {
        using var doc = JsonDocument.Parse(LocalData.PackEntry(LocalData.BeachBriefsPack, "meta.json"));
        var groups = new List<(string, int, List<(string, int)>)>();
        foreach (var g in doc.RootElement.GetProperty("Groups").EnumerateArray())
        {
            if (!g.TryGetProperty("DefaultEntry", out var de)) continue;
            var options = g.GetProperty("Options").EnumerateArray()
                           .Where(o => o.TryGetProperty("AttributeMask", out _))
                           .Select(o => (o.GetProperty("Name").GetString()!, o.GetProperty("AttributeMask").GetInt32())).ToList();
            groups.Add((g.GetProperty("Name").GetString()!, de.GetProperty("AttributeMask").GetInt32(), options));
        }
        return groups;
    }

    /// <summary>Each setting of a group worth telling apart: Briefs or not × no Gen or one × Maro or not, as Penumbra
    /// builds the mask (the default XOR every ticked option).</summary>
    private static IEnumerable<(string Label, int Mask)> Masks((string Name, int Default, List<(string Name, int Mask)> Options) g)
    {
        int Opt(string prefix) => g.Options.FirstOrDefault(o => o.Name.StartsWith(prefix, StringComparison.Ordinal)).Mask;
        var gens = new[] { ("none", 0), ("A", Opt("Gen. A")), ("B", Opt("Gen. B")), ("C", Opt("Gen. C")), ("D", Opt("Gen. D")) };
        int maro = Opt("Maro");
        foreach (bool briefs in new[] { false, true })
            foreach (var (gen, gm) in gens)
                foreach (bool m in maro != 0 ? new[] { false, true } : new[] { false })
                {
                    int mask = g.Default ^ (briefs ? Opt("Briefs") : 0) ^ gm ^ (m ? maro : 0);
                    yield return ($"{g.Name}: briefs {briefs}, gen {gen}, maro {m}", mask);
                }
    }

    /// <summary>Skin parts holding one of the switch sets, drawn under <paramref name="mask"/>.</summary>
    private static int DrawnPenises(ModelParts m, int mask, IReadOnlyList<IReadOnlySet<string>> sets)
        => m.Parts.Count(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material) && p.Triangles.Length > 0
                              && GenitalSwitchesTests.Drawn(m, p, mask)
                              && sets.Any(s => s.IsSubsetOf(GenitalSwitches.Names(m, p.AttributeMask))));

    private static (byte[] Garment, byte[] GenA, UVRemapService Maps) Inputs()
        => (LocalData.PackEntry(LocalData.BeachBriefsPack, Penis), File.ReadAllBytes(LocalData.Path(TbseOpt + "gen a" + E0)),
            new UVRemapService(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps)));

    private static BodyRetarget.Planned Refit(byte[] garment, byte[] genA, byte[] target, bool across, UVRemapService maps,
                                              List<string> log, BodyRetarget.Tuning? tuning = null)
    {
        Assert.Null(BodyRetarget.BuildPair("_dwn", genA, target, "_dwn", true, null, null, maps, out var pair));
        return BodyRetarget.WithTuning(tuning ?? new BodyRetarget.Tuning(), () =>
            BodyRetarget.Plan(ModelPartReader.Read(garment)!, (byte[])garment.Clone(), [pair], "_dwn", replaceSkin: true,
                              acrossBodies: across, cutHidden: true, log: log.Add, warn: l => log.Add("WARN " + l)));
    }

    /// <summary>
    /// Every body, every group, every setting (AC-1, AC-2): the refit draws a penis exactly when the garment drew one —
    /// none under the briefs, one for one Gen — and none of the new body's genitals is left untagged (on a merged body, only
    /// the counted fringe). The Briefs-only cloth is the cloth the refit fitted, as before: its folds are logged.
    /// </summary>
    [LocalDataFact(LocalData.BeachBriefsPack, LocalData.TbseUvMaps, TbseOpt + "gen a" + E0, TbseOpt + "gen b" + E0,
                   TbseOpt + "gen c" + E0, TbseOpt + "gen d" + E0, TbseX + "common/2/c0101e0000_dwn.mdl")]
    [Trait("Plan", "AC-genital-switches-1")]
    [Trait("Plan", "AC-genital-switches-2")]
    [Trait("Plan", "EDGE-genital-switches-15")]
    public void Beach_briefs_switch_on_every_body()
    {
        var (garment, genA, maps) = Inputs();
        var g = ModelPartReader.Read(garment)!;
        var gSkin = ModelSkinReader.Read(garment, null, null);
        var gaParts = ModelPartReader.Read(genA)!;
        var sets = GenitalSwitches.Of(g, gSkin, gaParts, GenitalParts.Read(gaParts, ModelSkinReader.Read(genA, null, null)!), out var why);
        Assert.True(sets != null, why);
        output.WriteLine($"switch sets: {string.Join("; ", sets!.Select(s => string.Join("+", s.OrderBy(n => n))))}");
        Assert.Equal(4, sets.Count);
        var groups = Groups();
        Assert.Equal(4, groups.Count);
        foreach (var (name, need, across) in Bodies)
        {
            if (LocalData.Missing([need]) is { } miss) { output.WriteLine($"{name}: skipped ({miss})"); continue; }
            var target = File.ReadAllBytes(LocalData.Path(need));
            var log = new List<string>();
            var plan = Refit(garment, genA, target, across, maps, log);
            var today = Refit(garment, genA, target, across, maps, [], new BodyRetarget.Tuning(NoGenitalSwitches: true, NoBulge: true));   // the same refit, switches off: the bulge is left out of both
            foreach (var l in log.Where(l => l.StartsWith("genital switches:", StringComparison.Ordinal) || l.StartsWith("bulge:", StringComparison.Ordinal)
                                             || l.StartsWith("WARN", StringComparison.Ordinal)))
                output.WriteLine($"{name}: {l}");
            Assert.DoesNotContain(log, l => l.StartsWith("WARN genital switches", StringComparison.Ordinal));
            var o = ModelPartReader.Read(plan.Model);
            Assert.NotNull(o);
            Assert.Contains(log, l => l.StartsWith("genital switches: _dwn the new body's genitals", StringComparison.Ordinal));
            foreach (var group in groups)
                foreach (var (label, mask) in Masks(group))
                    Assert.True(DrawnPenises(g, mask, sets) == DrawnPenises(o!, mask, sets),
                                $"{name}, {label}: the garment draws {DrawnPenises(g, mask, sets)} penises, the refit {DrawnPenises(o!, mask, sets)}");

            // None of the new body's genitals drawn untagged: no untagged output skin vertex where one of its genital
            // triangles stands clear of its plain skin (a merged body's fringe aside).
            var t = ModelPartReader.Read(target)!;
            var reading = GenitalParts.Read(t, ModelSkinReader.Read(target, null, null)!)!;
            var plain = GenitalParts.PlainSkin(t, reading.Genitals);
            // Every genital part of the new body (its smallclothes' genital piece, TBSE-X's _dwn_c, among them: EDGE-15).
            var proud = reading.Genitals
                               .SelectMany(x => x.Triangles.SelectMany(i => new[] { x.Part.Triangles[i * 3], x.Part.Triangles[i * 3 + 1], x.Part.Triangles[i * 3 + 2] }))
                               .Distinct().Select(v => At(t, v)).Where(p => plain.Nearest(p, 0.02f) > 0.01f).ToList();
            var grid = new GenitalParts.PointGrid(proud, 0.01f);
            // Untagged output parts the refit brought in: skin, and any material the garment did not have (the body's
            // smallclothes, under the garment's own name for their copy).
            var garmentMaterials = g.Parts.Select(p => p.Material).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int untagged = o!.Parts.Where(p => p.Island < 0 && p.AttributeMask == 0
                                               && (SecondSkinWriter.IsBodySkinMaterial(p.Material) || !garmentMaterials.Contains(p.Material)))
                             .SelectMany(p => p.Triangles).Distinct().Count(v => grid.Nearest(At(o, v), 0.002f) < 0.001f);
            var swap = plan.Report.Swap!.Value;
            output.WriteLine($"{name}: {swap.SwitchCopies} copies, {swap.Switched} switched triangles, fringe {swap.SwitchFringe}; untagged genital "
                             + $"vertices {untagged}; cloth folds {CrossSexRefit.ClothFolded(garment, plan)} (today {CrossSexRefit.ClothFolded(garment, today)})");
            Assert.Equal(4, swap.SwitchCopies);
            if (swap.SwitchFringe == 0) Assert.Equal(0, untagged);
            // The cloth is the refit's as before: the switches change the skin only.
            Assert.Equal(CrossSexRefit.ClothFolded(garment, today), CrossSexRefit.ClothFolded(garment, plan));
        }
    }

    /// <summary>
    /// Under the briefs no hole shows (AC-3): with Briefs ticked, every ray cast outward from the open edge of the drawn
    /// skin in the crotch window hits drawn cloth or skin.
    /// </summary>
    [LocalDataFact(LocalData.BeachBriefsPack, LocalData.TbseUvMaps, TbseOpt + "gen a" + E0, TbseOpt + "gen b" + E0)]
    [Trait("Plan", "AC-genital-switches-3")]
    public void Briefs_cover_the_open_hole()
    {
        var (garment, genA, maps) = Inputs();
        int briefsMask = 514 ^ 576;
        int worst = 0;
        foreach (var (name, need, across) in Bodies)
        {
            if (LocalData.Missing([need]) is { } miss) { output.WriteLine($"{name}: skipped ({miss})"); continue; }
            var target = File.ReadAllBytes(LocalData.Path(need));
            var plan = Refit(garment, genA, target, across, maps, []);
            var o = ModelPartReader.Read(plan.Model)!;
            var t = ModelPartReader.Read(target)!;
            var anchor = GenitalParts.Read(t, ModelSkinReader.Read(target, null, null)!)!.Anchor;
            var drawn = o.Parts.Where(p => p.Island < 0 && p.Triangles.Length > 0 && GenitalSwitchesTests.Drawn(o, p, briefsMask)).ToList();
            var tris = drawn.SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                             .Select(i => (At(o, p.Triangles[i * 3]), At(o, p.Triangles[i * 3 + 1]), At(o, p.Triangles[i * 3 + 2])))).ToList();
            // The open edges of the drawn skin, welded by position, inside the crotch window.
            (long, long, long) K(Vector3 p) => ((long)MathF.Round(p.X * 1e4f), (long)MathF.Round(p.Y * 1e4f), (long)MathF.Round(p.Z * 1e4f));
            var edges = new Dictionary<((long, long, long), (long, long, long)), (Vector3, Vector3, int)>();
            foreach (var p in drawn.Where(p => SecondSkinWriter.IsBodySkinMaterial(p.Material)))
                for (int i = 0; i + 2 < p.Triangles.Length; i += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        var a = At(o, p.Triangles[i + k]); var b = At(o, p.Triangles[i + (k + 1) % 3]);
                        var key = K(a).CompareTo(K(b)) < 0 ? (K(a), K(b)) : (K(b), K(a));
                        edges[key] = edges.TryGetValue(key, out var e) ? (a, b, e.Item3 + 1) : (a, b, 1);
                    }
            var rim = edges.Values.Where(e => e.Item3 == 1 && GenitalParts.InWindow(e.Item1, anchor) && GenitalParts.InWindow(e.Item2, anchor))
                                  .Select(e => (e.Item1 + e.Item2) / 2).ToList();
            var dirs = new List<Vector3> { Vector3.UnitZ };
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, 1), (1, -1), (-1, -1) })
                dirs.Add(Vector3.Normalize(Vector3.UnitZ + 0.8f * Vector3.Normalize(new Vector3(dx, dy, 0))));
            int misses = 0;
            foreach (var from in rim)
                foreach (var d in dirs)
                    if (!tris.Any(tr => Hit(from + d * 0.001f, d, tr, 0.5f))) misses++;
            output.WriteLine($"{name}: {rim.Count} open skin edges in the crotch window, {misses} of {rim.Count * dirs.Count} rays miss under the briefs");
            worst = Math.Max(worst, misses);
        }
        Assert.Equal(0, worst);
    }

    /// <summary>Möller–Trumbore: whether the ray hits the triangle within <paramref name="reach"/>.</summary>
    private static bool Hit(Vector3 o, Vector3 d, (Vector3 A, Vector3 B, Vector3 C) t, float reach)
    {
        var e1 = t.B - t.A; var e2 = t.C - t.A;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        float inv = 1f / det;
        var s = o - t.A;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        float dist = Vector3.Dot(e2, q) * inv;
        return dist > 1e-4f && dist <= reach;
    }

    /// <summary>
    /// The gate on real skin (AC-9, EDGE-34): Beach Briefs' knee and shin skin (atr_hiz, atr_sne) falls outside the crotch
    /// box on the TBSE Gen A body it was made on — a garment whose only tagged skin is such pays no genital reading — and
    /// its switched penises fall inside it.
    /// </summary>
    [LocalDataFact(LocalData.BeachBriefsPack, TbseOpt + "gen a" + E0)]
    [Trait("Plan", "AC-genital-switches-9")]
    [Trait("Plan", "EDGE-genital-switches-34")]
    public void Knee_and_shin_skin_stay_out_of_the_gate()
    {
        var (garment, genA, _) = Inputs();
        var g = ModelPartReader.Read(garment)!;
        var body = ModelPartReader.Read(genA)!;
        foreach (var p in g.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material) && p.AttributeMask != 0))
        {
            var names = GenitalSwitches.Names(g, p.AttributeMask);
            bool inBox = GenitalSwitches.InCrotchBox(p, body);
            output.WriteLine($"{p.Label} [{string.Join(",", names)}]: {(inBox ? "in" : "out of")} the crotch box (top {p.Max.Y:0.000} m)");
            if (names.Contains("atr_hiz") || names.Contains("atr_sne")) Assert.False(inBox, $"{p.Label}: knee or shin skin reaches the gate");
            if (names.Contains("atr_dv_j")) Assert.True(inBox, $"{p.Label}: a switched penis misses the gate");
        }
    }

    /// <summary>
    /// The switches' cost on the real refit (AC-9), run alone: its 500 ms is wall time, and beside the parallel full suite
    /// both refits stretch (testing-621's run: switched 4643 ms, today 4066 ms, 577 ms; alone 1669 / 1502 ms, 167 ms).
    /// </summary>
    [Collection(SwitchCostCollection.Name)]
    [Trait("Speed", "Slow")]
    public class Cost(ITestOutputHelper output)
    {
        /// <summary>The switched Gen B refit against today's, median of three.</summary>
        [LocalDataFact(LocalData.BeachBriefsPack, LocalData.TbseUvMaps, TbseOpt + "gen a" + E0, TbseOpt + "gen b" + E0)]
        [Trait("Plan", "AC-genital-switches-9")]
        public void Switch_cost()
        {
            var (garment, genA, maps) = Inputs();
            var target = File.ReadAllBytes(LocalData.Path(TbseOpt + "gen b" + E0));
            long Time(BodyRetarget.Tuning tuning)
            {
                var ms = new List<long>();
                for (int i = 0; i < 3; i++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Refit(garment, genA, target, false, maps, [], tuning);
                    ms.Add(sw.ElapsedMilliseconds);
                }
                return ms.OrderBy(x => x).ElementAt(1);
            }
            long with = Time(new BodyRetarget.Tuning(NoBulge: true)), without = Time(new BodyRetarget.Tuning(NoBulge: true, NoGenitalSwitches: true));
            output.WriteLine($"switched {with} ms, today {without} ms");
            Assert.True(with - without <= 500, $"the switches cost {with - without} ms");
        }
    }
}

/// <summary>
/// The genital switches' cost test runs alone: xunit runs a collection that disables parallelisation after every parallel
/// one has finished, so its wall-time bound is measured on an idle machine (as <see cref="HarnessTimingCollection"/> does
/// for the harness timings, #49).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SwitchCostCollection
{
    public const string Name = "Genital switch cost, alone";
}
