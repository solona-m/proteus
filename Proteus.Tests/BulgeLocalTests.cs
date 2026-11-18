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
}
