using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Proteus.Services.Bulge;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>The featureless bulge on real files (plan featureless-bulge): garments refitted onto bodies with a penis.</summary>
[Trait("Speed", "Slow")]
public partial class BulgeLocalTests(ITestOutputHelper output)
{
    private static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>A logger that keeps every line, for the bulge's own.</summary>
    private sealed class Lines
    {
        public readonly List<string> All = [];
        private static string Fmt(NSubstitute.Core.CallInfo c)
        {
            string f = (string)c[0];
            var args = c[1] as object[] ?? [];
            try { return System.Text.RegularExpressions.Regex.Replace(args.Length == 0 ? f : string.Format(f, args), @"^\[Proteus\] [^:]+: ", ""); }
            catch (FormatException) { return f; }
        }

        public IPluginLog Log()
        {
            var log = Substitute.For<IPluginLog>();
            log.When(l => l.Information(Arg.Any<string>(), Arg.Any<object[]>())).Do(c => All.Add(Fmt(c)));
            log.When(l => l.Info(Arg.Any<string>(), Arg.Any<object[]>())).Do(c => All.Add(Fmt(c)));
            log.When(l => l.Debug(Arg.Any<string>(), Arg.Any<object[]>())).Do(c => All.Add(Fmt(c)));
            log.When(l => l.Warning(Arg.Any<string>(), Arg.Any<object[]>())).Do(c => All.Add(Fmt(c)));
            return log;
        }
    }

    /// <summary>Output SKIN vertices standing where Gen A's penis was: within 1 mm of one of its vertices more than 5 mm off the bulge.</summary>
    internal static int PenisVertices(byte[] model, byte[] genA)
    {
        var g = ModelPartReader.Read(genA)!;
        var penis = g.Parts.Single(p => p.Island < 0 && p.Label == "1.4").Triangles.Distinct().Select(v => At(g, v)).ToList();
        var bulge = TbseBulge.Shared!.Positions;
        var proud = penis.Where(p => bulge.Min(b => Vector3.Distance(b, p)) > 0.005f).ToList();
        var m = ModelPartReader.Read(model)!;
        var drawn = m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                     .SelectMany(p => p.Triangles).Distinct().Select(v => At(m, v)).ToList();
        return drawn.Count(d => proud.Any(p => Vector3.Distance(p, d) < 0.001f));
    }

    [LocalDataFact(LocalData.GameData, CrossSexNudeLegsLocalTests.StormblessedLegs, CrossSexNudeLegsLocalTests.LavaBod,
                   CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-featureless-bulge-3")]
    public void Stormblessed_on_gen_a()
    {
        var lines = new Lines();
        var log = lines.Log();
        var onto = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse));
        var genAOption = onto.Options.First(b => RefitCore.Normal(b.Rel) == RefitCore.Normal(UvBakeInputs.TbseLegs[1].Rel));
        var inputs = CrossSexNudeLegsLocalTests.JobInputs(CrossSexNudeLegsLocalTests.StormblessedLegs, CrossSexNudeLegsLocalTests.LavaBod, 0, log) with
        {
            Nude = b => RefitCore.Normal(b.Rel) == RefitCore.Normal(UvBakeInputs.TbseLegs[0].Rel) ? genAOption : null,
        };
        var withBulge = CrossSexRefitJob.Run(inputs).Sizes.Single();
        var plain = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoBulge: true), () => CrossSexRefitJob.Run(inputs).Sizes.Single());
        var genA = File.ReadAllBytes(onto.PathOf(genAOption));
        foreach (var l in lines.All.Where(l => l.StartsWith("bulge:", StringComparison.Ordinal)).Distinct()) output.WriteLine(l);
        output.WriteLine($"with the bulge: {withBulge.Folds} folds, penis vertices {PenisVertices(withBulge.Plan!.Model, genA)}; "
                         + $"without: {plain.Folds} folds, penis vertices {PenisVertices(plain.Plan!.Model, genA)}");
        Assert.Contains(lines.All, l => l.StartsWith("bulge: _dwn featureless", StringComparison.Ordinal));
        Assert.True(withBulge.Folds < plain.Folds, $"{withBulge.Folds} folds with the bulge, {plain.Folds} without");
        Assert.Equal(0, PenisVertices(withBulge.Plan!.Model, genA));
    }

    private const string TbseOpt = LocalData.Mods + ":The Body SE/p2/step 6- smallclothes options/";
    private const string TbseXOpt = LocalData.Mods + ":The Body SE-X IVCS2 V1_1_0/p1/legs (smallclothes legs)/";
    private const string E0 = "/chara/equipment/e0000/model/c0101e0000_dwn.mdl";

    /// <summary>
    /// Which TBSE-family legs the owner's bulge fits (AC-9): every option whose genital piece plugs the bulge's hole is
    /// made featureless — no penis skin left, the bulge drawn, every vertex on no penis bone — and the rest are refused
    /// with the reason. Gen A and TBSE-X Flaccid M A must fit.
    /// </summary>
    [LocalDataFact(LocalData.Mods)]
    [Trait("Plan", "AC-featureless-bulge-9")]
    [Trait("Plan", "AC-featureless-bulge-6")]
    [Trait("Plan", "AC-featureless-bulge-10")]
    [Trait("Plan", "EDGE-featureless-bulge-39")]
    public void Tbse_family_bodies()
    {
        var bulge = TbseBulge.Shared!;
        var options = new[] { TbseOpt + "gen a", TbseOpt + "gen b", TbseOpt + "gen c", TbseOpt + "gen d" }
            .Concat(new[] { "flaccid m a", "flaccid m c", "flaccid m d", "flaccid l a", "flaccid xl d", "erect l cut", "erect m uncut" }.Select(o => TbseXOpt + o))
            .ToList();
        var fitted = new List<string>();
        foreach (var opt in options)
        {
            string need = opt + E0;
            if (LocalData.Missing([need]) is { } miss) { output.WriteLine($"{opt}: skipped ({miss})"); continue; }
            var bytes = File.ReadAllBytes(LocalData.Path(need));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var swapped = BulgeSwap.Featureless(bytes, bulge, out var why);
            sw.Stop();
            string name = opt[(opt.LastIndexOf('/') + 1)..];
            if (swapped == null)
            {
                // How far its genital piece's rim is from the bulge's hole.
                var bp = ModelPartReader.Read(bytes)!;
                var rd = GenitalParts.Read(bp, ModelSkinReader.Read(bytes, null, null)!);
                string rim = "";
                foreach (var g in rd?.Genitals.Where(g => g.Rule == "c") ?? [])
                {
                    var verts = g.Part.Triangles.Distinct().Select(v => new Vector3(bp.Positions[v * 3], bp.Positions[v * 3 + 1], bp.Positions[v * 3 + 2])).ToList();
                    float far = bulge.Hole.Max(h => verts.Min(q => Vector3.Distance(q, h)));
                    rim += $" piece {g.Part.Label}: farthest hole point {far * 1000f:0.00} mm from it";
                }
                output.WriteLine($"{name}: left as it is — {why} ({sw.ElapsedMilliseconds} ms);{rim}");
                continue;
            }
            fitted.Add(name);
            var skin = ModelSkinReader.Read(swapped.Model, null, null)!;
            var parts = ModelPartReader.Read(swapped.Model)!;
            var drawnSkin = parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                 .SelectMany(p => p.Triangles).Distinct().ToList();
            int onPenis = drawnSkin.Count(v => Enumerable.Range(0, XivLiveMesh.SkinnedMesh.MaxInfluences)
                                                       .Any(k => skin.BoneWeights[v * 8 + k] > 0f && GenitalParts.IsPenisBone(skin.BoneNames[skin.BoneIndices[v * 8 + k]])));
            var reading = GenitalParts.Read(parts, skin);
            output.WriteLine($"{name}: featureless in {sw.ElapsedMilliseconds} ms — {swapped.Collapsed} triangles collapsed; penis read after: "
                             + $"{reading?.Penis}; drawn skin vertices on penis bones {onPenis}");
            Assert.False(reading?.Penis ?? false, $"{name} still reads as having a penis");
            Assert.Equal(0, onPenis);
            Assert.True(sw.ElapsedMilliseconds < 3000, $"{name}: {sw.ElapsedMilliseconds} ms");
        }
        Assert.Contains("gen a", fitted);
        Assert.Contains("flaccid m a", fitted);
    }

    /// <summary>
    /// Men's pants moved between men's bodies (AC-4): Twill Trousers, made on TBSE SFW, onto TBSE Gen A (within one body
    /// mod) and onto TBSE-X Flaccid M A (across body mods), as Studio's Body size builds the pair and plans it — fewer
    /// folds than today, no penis skin left, the bulge's log line said.
    /// </summary>
    [LocalDataFact(CrossSexRefitLocalTests.TwillTbse, LocalData.TbseUvMaps, TbseOpt + "sfw" + E0, TbseOpt + "gen a" + E0, TbseXOpt + "flaccid m a" + E0)]
    [Trait("Plan", "AC-featureless-bulge-4")]
    [Trait("Plan", "EDGE-featureless-bulge-9")]
    public void Twill_sfw_to_gen_a_and_tbsex_flaccid()
    {
        var maps = new UVRemapService(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps));
        var garment = File.ReadAllBytes(LocalData.Path(CrossSexRefitLocalTests.TwillTbse));
        var sfw = File.ReadAllBytes(LocalData.Path(TbseOpt + "sfw" + E0));
        foreach (var (name, need, across) in new[] { ("TBSE Gen A", TbseOpt + "gen a" + E0, false), ("TBSE-X Flaccid M A", TbseXOpt + "flaccid m a" + E0, true) })
        {
            var target = File.ReadAllBytes(LocalData.Path(need));
            Assert.Null(BodyRetarget.BuildPair("_dwn", sfw, target, "_dwn", true, null, null, maps, out var pair));
            BodyRetarget.Planned Run(bool noBulge, List<string> log)
                => BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoBulge: noBulge), () =>
                    BodyRetarget.Plan(ModelPartReader.Read(garment)!, (byte[])garment.Clone(), [pair], "_dwn", replaceSkin: true,
                                      acrossBodies: across, cutHidden: true, log: log.Add));
            var log = new List<string>();
            var withBulge = Run(false, log);
            var plain = Run(true, []);
            int Penis(byte[] m) => PenisVerticesOf(m, target);
            // How far the crotch cloth stands off the bulge: fitted to it, near; pushed out over the penis, far.
            var bulge = TbseBulge.Shared!.Positions;
            var centre = bulge.Aggregate(Vector3.Zero, (a, p) => a + p) / bulge.Length;
            float Standoff(byte[] model)
            {
                var m = ModelPartReader.Read(model)!;
                var cloth = m.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)).SelectMany(p => p.Triangles)
                             .Distinct().Select(v => At(m, v)).Where(p => Vector3.Distance(p, centre) < 0.05f).ToList();
                return cloth.Count == 0 ? 0f : cloth.Max(p => bulge.Min(b => Vector3.Distance(b, p)));
            }
            output.WriteLine($"{name}: {string.Join(" | ", log.Where(l => l.StartsWith("bulge:", StringComparison.Ordinal)))}");
            output.WriteLine($"{name}: with the bulge {CrossSexRefit.ClothFolded(garment, withBulge)} folds, penis skin {Penis(withBulge.Model)}, "
                             + $"crotch cloth up to {Standoff(withBulge.Model) * 1000f:0} mm off the bulge; without {CrossSexRefit.ClothFolded(garment, plain)} "
                             + $"folds, penis skin {Penis(plain.Model)}, {Standoff(plain.Model) * 1000f:0} mm");
            Assert.Contains(log, l => l.StartsWith("bulge: _dwn featureless", StringComparison.Ordinal));
            Assert.Equal(0, Penis(withBulge.Model));
            Assert.True(CrossSexRefit.ClothFolded(garment, withBulge) <= CrossSexRefit.ClothFolded(garment, plain),
                        $"{name}: more folds with the bulge");
        }
    }

    /// <summary>Output skin triangles where a body's own genital piece stood proud of the bulge (any TBSE-family body).</summary>
    internal static int PenisVerticesOf(byte[] model, byte[] body)
    {
        var g = ModelPartReader.Read(body)!;
        var reading = GenitalParts.Read(g, ModelSkinReader.Read(body, null, null)!)!;
        var piece = reading.Genitals.Where(x => x.Rule == "c").Select(x => x.Part).ToHashSet();
        var penis = piece.SelectMany(x => x.Triangles.Distinct()).Select(v => At(g, v)).ToList();
        var bulge = TbseBulge.Shared!.Positions;
        var mainSkin = g.Parts.Where(p => p.Island < 0 && !piece.Contains(p) && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                        .SelectMany(p => p.Triangles).Distinct().Select(v => At(g, v)).ToList();
        // Proud: more than 5 mm off both the bulge's SURFACE and the rest of the skin — not the piece's rim, welded to the
        // skin, nor a penis point the bulge's surface passes over (its vertices are a centimetre apart).
        var bt = TbseBulge.Shared.Data.triangles;
        float OffBulge(Vector3 p)
        {
            float best = float.MaxValue;
            for (int t = 0; t + 2 < bt.Length; t += 3)
                best = MathF.Min(best, PointTriangle(p, bulge[bt[t]], bulge[bt[t + 1]], bulge[bt[t + 2]]));
            return best;
        }
        var proud = penis.Where(p => OffBulge(p) > 0.005f && mainSkin.Min(q => Vector3.Distance(q, p)) > 0.005f).ToList();
        // Drawn skin TRIANGLES lying wholly on the penis: every corner within 1 mm of a proud penis point.
        var m = ModelPartReader.Read(model)!;
        bool OnPenis(int v) => proud.Any(p => Vector3.Distance(p, At(m, v)) < 0.001f);
        int count = 0;
        foreach (var part in m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                if (a == b || b == c || a == c || !OnPenis(a) || !OnPenis(b) || !OnPenis(c)) continue;
                count++;
                if (LastPenisHits.Count < 3) LastPenisHits.Add(At(m, a));
            }
        return count;
    }

    /// <summary>The distance from a point to a triangle (Ericson's closest point).</summary>
    internal static float PointTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return Vector3.Distance(p, a);
        var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return Vector3.Distance(p, b);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return Vector3.Distance(p, a + ab * (d1 / (d1 - d3)));
        var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return Vector3.Distance(p, c);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return Vector3.Distance(p, a + ac * (d2 / (d2 - d6)));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return Vector3.Distance(p, b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))));
        float den = 1f / (va + vb + vc);
        return Vector3.Distance(p, a + ab * (vb * den) + ac * (vc * den));
    }

    /// <summary>Where the last count found penis skin (diagnostic).</summary>
    internal static readonly List<Vector3> LastPenisHits = [];
}
