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
public class BulgeLocalTests(ITestOutputHelper output)
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
    [Trait("Plan", "AC-featureless-bulge-8")]
    [Trait("Plan", "AC-featureless-bulge-9")]
    [Trait("Plan", "AC-featureless-bulge-5")]
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
}
