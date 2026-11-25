extern alias RefitCli;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using XivLiveMesh;

namespace Proteus.Tests;

/// <summary>
/// Every size of "Starlight"'s Valentione Skirt (the twelve its author made with the Studio's Body size), refit headless
/// from the author's own skirt as it is and as the build before the fix made it (see
/// <see cref="StarlightBottomsLocalTests"/>, which pins Tre Medium in the suite). Never reads the mod's own refits. On
/// demand: two refits a size.
/// </summary>
[Trait("Suite", "Diagnostic")]
[Trait("Speed", "Slow")]
public sealed class StarlightBottomsDiagnosticTests(ITestOutputHelper o)
{
    /// <summary>Each size: its body mod and the body file, as the mod's record names them.</summary>
    internal static readonly Dictionary<string, (string Mod, string Rel)> Sizes = new()
    {
        ["Neolithe S"] = (LocalData.NeolitheMod, "default legs - smallclothes\\sfw small.mdl"),
        ["Neolithe M"] = (LocalData.NeolitheMod, "default legs - smallclothes\\sfw medium.mdl"),
        ["Neolithe L"] = (LocalData.NeolitheMod, "default legs - smallclothes\\sfw large.mdl"),
        ["Bibo S"] = (LocalData.BiboPlusMod, "bottoms - small clothes\\type a - small\\chara\\equipment\\e0000\\model\\c0201e0000_dwn.mdl"),
        ["Bibo M"] = (LocalData.BiboPlusMod, "bottoms - small clothes\\type a - medium\\chara\\equipment\\e0000\\model\\c0201e0000_dwn.mdl"),
        ["Bibo L"] = (LocalData.BiboPlusMod, "bottoms - small clothes\\type a - large\\chara\\equipment\\e0000\\model\\c0201e0000_dwn.mdl"),
        ["Rue WC"] = (LocalData.RueMod, "files\\legs - smallclothes\\yiggle - watermelon crushers - a\\chara\\equipment\\e0000\\model\\c0201e0000_dwn.mdl"),
        ["YAB WC"] = (LocalData.YabMod, "files\\legs - smallclothes\\yiggle - watermelon crushers - a\\chara\\equipment\\e0000\\model\\c0201e0000_dwn.mdl"),
        ["YAB SC"] = (LocalData.YabMod, "files\\legs - smallclothes\\yiggle - skull crushers - a\\chara\\equipment\\e0000\\model\\c0201e0000_dwn.mdl"),
        ["Tre Medium"] = (LocalData.TreMod, "files\\common\\15\\c0201e0000_dwn.mdl"),
        ["Tre Large"] = (LocalData.TreMod, "files\\common\\10\\c0201e0000_dwn.mdl"),
        ["Rosaline"] = (LocalData.RosalineMod, "files\\common\\13\\c0201e0000_dwn.mdl"),
    };

    /// <summary>Each size's body file as measured (SHA-256): a change is a re-measure, not a pass.</summary>
    internal static readonly Dictionary<string, string> BodySha = new()
    {
        ["Neolithe S"] = "d8778ef1aa8bbe9659a4bc6803ffdb17ed140113d31398d7e82d26132045ca94",
        ["Neolithe M"] = "fa92845c6a4906795ff22d89d6290dd1ba12d2097be6acf664dba1cfc8205de1",
        ["Neolithe L"] = "ebbb48e118d7f711279366a55821abc18c3059122fae42338e7b96ea090fda48",
        ["Bibo S"] = "d532b8fb4a0553bc0e78fd634a0c93b6b8471424af0f2a7eaa54f2abd1bbfbb2",
        ["Bibo M"] = "e1f70edcad0511baf82b7f2ef836065eb25987ab8541dee63c67091974fff981",
        ["Bibo L"] = "7421b07571f7f8c95a9665c16bb3098c1569aefa8f6787ce84ba922b0a774ec1",
        ["Rue WC"] = "73ed108c61f11d586cb45cdb6fc30c0dff5701cdc24d6cd282814dcbfaa5f14b",
        ["YAB WC"] = "03ccf5ec28e39d9aaf546894c8bbe3f5484a772bbc7460d0db27a2c2ae1d96c5",
        ["YAB SC"] = "3c4cbded618bd5ca9cc684b9196a0a8c9cb43edb902526a00f480c5c48c72c50",
        ["Tre Medium"] = "da27c673e378439417cc0ace588b93c2df145191cfa33107ec49bc5b78848827",
        ["Tre Large"] = "b3be3f61a697068d9dedb3b23ca0a03bbfabf51d2cdcc90f11de6dea4461554b",
        ["Rosaline"] = "05f736321a0b17d75d9bae121d6a9cae6d8df8dd27d2bdfbae1fa1072f152717",
    };

    /// <summary>
    /// Triangles of the author's crotch hole the fix draws on these sizes, accepted at their measured count (2026-11-24):
    /// on Rue's and YAB's Yiggle legs 3% of the hole, at its rim, kept through a corner on inner-thigh skin the author
    /// pressed in. Every other size cuts the hole exactly as before.
    /// </summary>
    internal static readonly Dictionary<string, int> HoleDrawn = new() { ["Rue WC"] = 42, ["YAB WC"] = 48, ["YAB SC"] = 46 };

    /// <summary>
    /// Measured 2026-11-24 before the fix (both knobs on): skin points through the stockings. After it: 0 on every size.
    /// </summary>
    internal static readonly Dictionary<string, int> References = new()
    {
        ["Neolithe S"] = 178, ["Neolithe M"] = 201, ["Neolithe L"] = 181, ["Bibo S"] = 124, ["Bibo M"] = 130,
        ["Bibo L"] = 223, ["Rue WC"] = 278, ["YAB WC"] = 278, ["YAB SC"] = 479, ["Tre Medium"] = 642,
        ["Tre Large"] = 642, ["Rosaline"] = 330,
    };

    /// <summary>The thighs' band, where the author pressed the skin in under the stocking band (y 0.70-0.90 m, outside the
    /// crotch): no new-body skin left out there.</summary>
    private static bool InBand(Vector3 p) => p.Y is > 0.70f and < 0.90f && !StarlightMeasure.InCrotch(p);

    public static TheoryData<string> Names => new(Sizes.Keys);

    [LocalDataTheory(LocalData.StarlightMod, LocalData.TightFirmMod, LocalData.NeolitheMod, LocalData.BiboPlusMod,
                     LocalData.RueMod, LocalData.YabMod, LocalData.TreMod, LocalData.RosalineMod, LocalData.UvMaps)]
    [MemberData(nameof(Names))]
    [Trait("Plan", "AC-starlight-bottoms-1")]
    [Trait("Plan", "AC-starlight-bottoms-2")]
    [Trait("Plan", "AC-starlight-bottoms-3")]
    [Trait("Plan", "EDGE-starlight-bottoms-12")]
    [Trait("Plan", "EDGE-starlight-bottoms-21")]
    public void Every_bottoms_size_is_clean(string size)
    {
        var (mod, rel) = Sizes[size];
        foreach (var (inMod, inRel, sha) in new[]
                 {
                     (LocalData.StarlightMod, StarlightBottomsLocalTests.Garment, StarlightBottomsLocalTests.Inputs[0].Sha),
                     (LocalData.TightFirmMod, StarlightBottomsLocalTests.MadeOn, StarlightBottomsLocalTests.Inputs[1].Sha),
                     (mod, rel, BodySha[size]),
                 })
        {
            string got = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(Path.Combine(LocalData.Path(inMod), inRel))));
            Assert.True(got == sha, $"inputs changed ({inRel}): re-measure the references");
        }
        string dir = Path.Combine(Path.GetTempPath(), "proteus-starlight-sweep-" + Guid.NewGuid().ToString("N"));
        try
        {
            var original = ModelPartReader.Read(File.ReadAllBytes(
                Path.Combine(LocalData.Path(LocalData.StarlightMod), StarlightBottomsLocalTests.Garment)))!;
            var body = ModelPartReader.Read(File.ReadAllBytes(Path.Combine(LocalData.Path(mod), rel)))!;
            var now = Refit(dir, mod, rel, "");
            var before = Refit(dir, mod, rel, StarlightBottomsLocalTests.Before);

            int stockNow = StarlightMeasure.StockingSkinOut(now), stockBefore = StarlightMeasure.StockingSkinOut(before);
            var outNow = StarlightMeasure.LeftOutBodySkin(body, now);
            var outBefore = StarlightMeasure.LeftOutBodySkin(body, before);
            int holeNow = StarlightMeasure.LeftOutInAuthorsHole(body, now), holeBefore = StarlightMeasure.LeftOutInAuthorsHole(body, before);
            int smallNow = Smallclothes(now), smallBefore = Smallclothes(before);
            var turnedNow = StarlightMeasure.TurnedOver(original, now);
            var turnedBefore = StarlightMeasure.TurnedOver(original, before);
            o.WriteLine($"{size}: stockings {stockBefore} -> {stockNow}; left out on the legs {outBefore.Count} -> {outNow.Count} "
                        + $"(band {outBefore.Count(InBand)} -> {outNow.Count(InBand)}); crotch hole {holeBefore} -> {holeNow}; "
                        + $"smallclothes {smallBefore} -> {smallNow}; turned over {turnedBefore.Values.Sum()} -> {turnedNow.Values.Sum()}");

            Assert.Equal(References[size], stockBefore);
            Assert.True(stockNow <= stockBefore / 10, $"{stockNow} skin points through the stockings ({stockBefore} before)");
            Assert.DoesNotContain(outNow, InBand);
            Assert.True(outNow.Count <= outBefore.Count, $"{outNow.Count} triangles left out on the legs ({outBefore.Count} before)");
            Assert.True(holeNow >= holeBefore - HoleDrawn.GetValueOrDefault(size), $"the crotch hole lost {holeBefore - holeNow} of {holeBefore}");
            int waistNow = StarlightMeasure.LeftOutAboveLegs(body, now), waistBefore = StarlightMeasure.LeftOutAboveLegs(body, before);
            Assert.True(waistNow >= waistBefore, $"the waist edge lost {waistBefore - waistNow} triangles");
            var coarse = StarlightMeasure.CoarseParts(original);
            foreach (string part in StarlightMeasure.LegsParts(original))
            {
                bool slope = coarse.Contains(part);
                var lineNow = StarlightMeasure.OutOfLine(original, now, [part], perLength: slope);
                var lineBefore = StarlightMeasure.OutOfLine(original, before, [part], perLength: slope);
                float slack = slope ? StarlightBottomsLocalTests.CoarseSlopeSlack : StarlightBottomsLocalTests.OutOfLineSlack;
                Assert.True(lineNow.Max <= lineBefore.Max + slack && lineNow.P99 <= lineBefore.P99 + slack,
                            $"part {part} out of line {lineNow.Max:F4}/{lineNow.P99:F4}, before {lineBefore.Max:F4}/{lineBefore.P99:F4}");
            }
            Assert.Equal(smallBefore, smallNow);
            foreach (var (part, n) in turnedNow) Assert.True(n <= turnedBefore.GetValueOrDefault(part), $"part {part} folds more");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static int Smallclothes(ModelParts m)
        => m.Parts.Where(p => p.Island < 0 && BodySurface.IsSmallclothes(p.Material)).Sum(p => p.Triangles.Length / 3);

    private static ModelParts Refit(string dir, string mod, string rel, string tune)
        => ModelPartReader.Read(File.ReadAllBytes(RefitFile(dir, mod, rel, tune)))!;

    /// <summary>The refit written for a tune, in its own folder (the tune's name, sanitised).</summary>
    private static string RefitFile(string dir, string mod, string rel, string tune)
    {
        string outDir = Path.Combine(dir, tune.Length == 0 ? "as-is" : new string(tune.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()));
        Directory.CreateDirectory(outDir);
        var args = new List<string>
        {
            "--garment", Path.Combine(LocalData.Path(LocalData.StarlightMod), StarlightBottomsLocalTests.Garment),
            "--from-root", LocalData.Path(LocalData.TightFirmMod), "--from", StarlightBottomsLocalTests.MadeOn,
            "--body-root", LocalData.Path(mod), "--to", rel + "=Size", "--slot", "_dwn",
            "--uvmaps", LocalData.Path(LocalData.UvMaps), "--out-dir", outDir,
        };
        if (tune.Length > 0) args.AddRange(["--tune", tune]);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var console = Console.Out;
        int exit;
        Console.SetOut(stdout);
        try { exit = RefitCli::Proteus.Refit.Program.Dispatch([.. args], stdout, stderr); }
        finally { Console.SetOut(console); }
        if (exit != 0) throw new InvalidOperationException($"refit failed: {stderr}");
        return Path.Combine(outDir, "Size.mdl");
    }

    // ── the knee standoff's pose measure (Phase B: measured at 1502, before any pass) ──

    /// <summary>The poses each size is measured in: the owner's idle as dumped and mirrored, and knee bends added beyond
    /// bind (the hip held as dumped).</summary>
    internal static IReadOnlyList<(string Name, LivePose Pose)> MeasuredPoses()
    {
        var idle = KneeBend.Load(LocalData.Path(LocalData.IdlePose));
        return
        [
            ("idle", idle), ("idle-mirrored", KneeBend.Mirrored(idle)),
            ("+15", KneeBend.Scaled(idle, 15)), ("+30", KneeBend.Scaled(idle, 30)),
            ("+60", KneeBend.Scaled(idle, 60)), ("+90", KneeBend.Scaled(idle, 90)),
        ];
    }

    [LocalDataTheory(LocalData.StarlightMod, LocalData.TightFirmMod, LocalData.NeolitheMod, LocalData.BiboPlusMod,
                     LocalData.RueMod, LocalData.YabMod, LocalData.TreMod, LocalData.RosalineMod, LocalData.UvMaps, LocalData.IdlePose)]
    [MemberData(nameof(Names))]
    [Trait("Suite", "Diagnostic")]
    [Trait("Plan", "AC-knee-standoff-1")]
    public void Bent_figures_per_size(string size)
    {
        var (mod, rel) = Sizes[size];
        string dir = Path.Combine(Path.GetTempPath(), "proteus-starlight-bent-" + Guid.NewGuid().ToString("N"));
        try
        {
            string file = RefitFile(dir, mod, rel, "");
            var bytes = File.ReadAllBytes(file);
            var rest = ModelPartReader.Read(bytes)!;
            var mesh = ModelSkinReader.Read(bytes, null, null)!;
            o.WriteLine($"{size}: at rest {StarlightMeasure.StockingSkinOut(rest)} through");
            foreach (var (name, pose) in MeasuredPoses())
            {
                var (posed, unmapped) = KneeBend.Posed(bytes, pose);
                var bent = StarlightMeasure.BentStockingSkinOut(rest, posed);
                int back = bent.Through.Count(t => t.Facing < -0.3f), front = bent.Through.Count(t => t.Facing > 0.3f);
                var bones = bent.Through.SelectMany(t => KneeBend.BonesOf(mesh, t.Vertex)).GroupBy(b => b)
                                .OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key}:{g.Count()}");
                float yLo = bent.Count == 0 ? 0 : bent.Through.Min(t => t.RestAt.Y), yHi = bent.Count == 0 ? 0 : bent.Through.Max(t => t.RestAt.Y);
                o.WriteLine($"  {name,-14} {bent.Count,4} through of {bent.Paired}; p50 {bent.Percentile(0.5f) * 1000:F1} p90 {bent.Percentile(0.9f) * 1000:F1} "
                            + $"max {bent.Percentile(1f) * 1000:F1} mm; back {back} front {front} side {bent.Count - back - front}; "
                            + $"y {yLo:F2}-{yHi:F2}; bones {string.Join(" ", bones)}"
                            + (unmapped.Count > 0 ? $"; unmapped {string.Join(",", unmapped)}" : ""));
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The bone sets the knee band may read (decision 2), smallest first.</summary>
    internal static readonly (string Name, string[] Prefixes)[] BandSets =
    [
        ("j_asi_b", ["j_asi_b"]), ("j_asi_b+n_hizasoubi", ["j_asi_b", "n_hizasoubi"]), ("j_asi_b+j_asi_c", ["j_asi_b", "j_asi_c"]),
        ("j_asi_b+c+hizasoubi", ["j_asi_b", "j_asi_c", "n_hizasoubi"]),
    ];

    /// <summary>A vertex's share of weight on bones starting with any of <paramref name="prefixes"/>.</summary>
    internal static float Share(SkinnedMesh mesh, int v, string[] prefixes)
    {
        float on = 0, all = 0;
        int o = v * SkinnedMesh.MaxInfluences;
        for (int k = 0; k < SkinnedMesh.MaxInfluences; k++)
        {
            float w = mesh.BoneWeights[o + k];
            if (w <= 0) continue;
            all += w;
            if (prefixes.Any(p => mesh.BoneNames[mesh.BoneIndices[o + k]].StartsWith(p, StringComparison.Ordinal))) on += w;
        }
        return all > 0 ? on / all : 0;
    }

    [LocalDataFact(LocalData.StarlightMod, LocalData.TightFirmMod, LocalData.NeolitheMod, LocalData.BiboPlusMod,
                   LocalData.RueMod, LocalData.YabMod, LocalData.TreMod, LocalData.RosalineMod, LocalData.UvMaps, LocalData.IdlePose)]
    [Trait("Suite", "Diagnostic")]
    [Trait("Plan", "AC-knee-standoff-1")]
    public void Band_coverage_over_every_size()
    {
        var poses = MeasuredPoses().Where(p => p.Name is "idle" or "idle-mirrored" or "+15" or "+30").ToList();
        var shares = BandSets.ToDictionary(s => s.Name, _ => new List<float>());
        // As the pass selects: a face counts when any of its samples (corners, edge midpoints, centroid) reads in the band,
        // the share read from the nearest skin vertex of the new body.
        var faceShares = BandSets.ToDictionary(s => s.Name, _ => new List<float>());
        string dir = Path.Combine(Path.GetTempPath(), "proteus-starlight-band-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (size, (mod, rel)) in Sizes)
            {
                var bytes = File.ReadAllBytes(RefitFile(Path.Combine(dir, size.Replace(' ', '-')), mod, rel, ""));
                var rest = ModelPartReader.Read(bytes)!;
                var mesh = ModelSkinReader.Read(bytes, null, null)!;
                var skin = rest.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                               .SelectMany(p => p.Triangles).Distinct().ToArray();
                Vector3 V(int i) => new(rest.Positions[i * 3], rest.Positions[i * 3 + 1], rest.Positions[i * 3 + 2]);
                int Nearest(Vector3 q)
                {
                    int best = -1;
                    float bd = float.MaxValue;
                    foreach (int s in skin)
                    {
                        float d = Vector3.DistanceSquared(V(s), q);
                        if (d < bd) { bd = d; best = s; }
                    }
                    return best;
                }
                foreach (var (_, pose) in poses)
                {
                    var bent = StarlightMeasure.BentStockingSkinOut(rest, KneeBend.Posed(bytes, pose).Posed);
                    foreach (var t in bent.Through)
                    {
                        Vector3 a = V(t.A), b = V(t.B), c = V(t.C);
                        var samples = new[] { a, b, c, (a + b) / 2, (b + c) / 2, (c + a) / 2, (a + b + c) / 3 }.Select(Nearest).ToArray();
                        foreach (var (name, prefixes) in BandSets)
                        {
                            shares[name].Add(Share(mesh, t.Vertex, prefixes));
                            faceShares[name].Add(samples.Max(s => Share(mesh, s, prefixes)));
                        }
                    }
                }
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (var (label, table) in new[] { ("point", shares), ("face", faceShares) })
            foreach (var (name, list) in table)
            {
                var row = new[] { 0.05f, 0.1f, 0.2f, 0.3f, 0.5f }.Select(th => $"≥{th:F2}: {100f * list.Count(s => s >= th) / Math.Max(1, list.Count):F1}%");
                o.WriteLine($"{label} {name,-22} {list.Count} points; {string.Join("  ", row)}");
            }
    }

    [LocalDataFact(LocalData.IdlePose)]
    [Trait("Suite", "Diagnostic")]
    public void The_idle_dump_s_leg_angles()
    {
        var idle = KneeBend.Load(LocalData.Path(LocalData.IdlePose));
        foreach (var (name, pose) in MeasuredPoses())
            foreach (var side in new[] { "l", "r" })
            {
                var a = KneeBend.Angles(pose, side);
                o.WriteLine($"{name,-14} {side}: knee bind {a.KneeBind:F1} pose {a.KneePose:F1} added {a.Added:F1}; hip {a.Hip:F1}; j_asi_b {a.KneeB:F1} j_asi_c {a.KneeC:F1}");
            }
    }
}
