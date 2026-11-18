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

namespace Proteus.Tests;

/// <summary>Path A (plan featureless-bulge rev11): TBSE Gen A's genital piece and the hole it plugs, for the owner's sculpt.</summary>
public partial class BulgeSpikeLocalTests
{
    /// <summary>
    /// Diagnostic: Gen A's genital piece (<c>0.3</c>) — its open rim, which is the hole in <c>0.0</c> it plugs — written to
    /// the temp folder as JSON (positions of the rim loop in order, and of every <c>0.0</c> vertex), so an exported sculpt
    /// can be compared against it.
    /// </summary>
    [LocalDataFact(GenALegs)]
    public void Gen_a_genital_rim()
    {
        var bytes = File.ReadAllBytes(LocalData.Path(GenALegs));
        var parts = ModelPartReader.Read(bytes)!;
        var penis = parts.Parts.Single(p => p.Island < 0 && p.Label == "1.4");
        var skin0 = parts.Parts.Single(p => p.Island < 0 && p.Label == "1.1");
        // Open edges of 0.3, by welded position.
        string Key(int v) => $"{MathF.Round(At(parts, v).X * 1e5f)},{MathF.Round(At(parts, v).Y * 1e5f)},{MathF.Round(At(parts, v).Z * 1e5f)}";
        var edges = new Dictionary<(string, string), int>();
        for (int t = 0; t + 2 < penis.Triangles.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                string a = Key(penis.Triangles[t + k]), b = Key(penis.Triangles[t + (k + 1) % 3]);
                var e = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
                edges[e] = edges.GetValueOrDefault(e) + 1;
            }
        var rimKeys = edges.Where(kv => kv.Value == 1).SelectMany(kv => new[] { kv.Key.Item1, kv.Key.Item2 }).ToHashSet();
        var keyAt = penis.Triangles.Distinct().GroupBy(Key).ToDictionary(g => g.Key, g => At(parts, g.First()));
        var rim = rimKeys.Select(k => keyAt[k]).ToList();
        var skin0Keys = skin0.Triangles.Select(Key).ToHashSet();
        int onSkin = rimKeys.Count(skin0Keys.Contains);
        output.WriteLine($"Gen A 0.3: {penis.Triangles.Distinct().Count()} vertices, {penis.Triangles.Length / 3} triangles; rim {rim.Count} points, "
                         + $"{onSkin} of them on 0.0; open edges {edges.Count(kv => kv.Value == 1)}");
        var all0 = skin0.Triangles.Distinct().Select(v => At(parts, v)).ToList();
        var path = Path.Combine(Path.GetTempPath(), "proteus_genA_rim.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            rim = rim.Select(p => new[] { p.X, p.Y, p.Z }),
            skin0 = all0.Select(p => new[] { p.X, p.Y, p.Z }),
        }));
        output.WriteLine("written " + path);
        // Is the piece a disk (one boundary, Euler characteristic 1) once welded by position?
        var wtris = new List<(string, string, string)>();
        for (int t = 0; t + 2 < penis.Triangles.Length; t += 3)
            wtris.Add((Key(penis.Triangles[t]), Key(penis.Triangles[t + 1]), Key(penis.Triangles[t + 2])));
        var wv = wtris.SelectMany(t => new[] { t.Item1, t.Item2, t.Item3 }).Distinct().Count();
        var we = wtris.SelectMany(t => new[] { (t.Item1, t.Item2), (t.Item2, t.Item3), (t.Item3, t.Item1) })
                      .Select(e => string.CompareOrdinal(e.Item1, e.Item2) < 0 ? e : (e.Item2, e.Item1)).Distinct().Count();
        output.WriteLine($"0.3 welded: V {wv} E {we} F {wtris.Count}, Euler {wv - we + wtris.Count}");
        // Do the genital piece's vertex slots belong to it alone?
        var used0 = parts.Parts.Where(q => q.Island < 0 && q != penis).SelectMany(q => q.Triangles).ToHashSet();
        var pv = penis.Triangles.Distinct().ToList();
        output.WriteLine($"0.3 vertex slots {pv.Count} [{pv.Min()}..{pv.Max()}], shared with other parts {pv.Count(used0.Contains)}; "
                         + $"mesh spans " + string.Join(" ", parts.MeshSpans.Select(m => $"m{m.Mesh}:{m.BaseVertex}+{m.Count}")));
        var src = SecondSkinWriter.Parse(bytes);
        output.WriteLine("mesh 0 declaration: " + string.Join(" ", src.Decls[0].Select(e => $"u{e.Usage}.{e.UsageIndex}:t{e.Type}@s{e.Stream}+{e.Offset}"))
                         + $"; bone table {string.Join(",", src.BoneTables.Length > 0 ? src.BoneTables[0].Select(i => src.BoneNames[i]) : [])}; shapes {src.Shapes.Count}");
    }

    /// <summary>Displaced source skin folds in the crotch field for one pair (the A0.3 measure).</summary>
    internal static int SourceFolds(BodyRetarget.SlotPair pair, byte[] source)
    {
        var src = pair.Correspondence.Source;
        var anchor = BodyRetarget.FindCrotch([(src, ModelSkinReader.Read(source, null, null)!)])!.Value;
        var whole = BodyRetarget.SourceBody.Whole(src, pair.Correspondence.Field);
        int folds = 0;
        foreach (var part in src.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int x = part.Triangles[t], y = part.Triangles[t + 1], z = part.Triangles[t + 2];
                var c = (At(src, x) + At(src, y) + At(src, z)) / 3;
                if (MathF.Abs(c.X - anchor.MidX) > 0.06f * anchor.Scale || c.Y < anchor.Y - 0.05f * anchor.Scale
                    || c.Y > anchor.Y + 0.14f * anchor.Scale) continue;
                if (whole[x] is not { } dx || whole[y] is not { } dy || whole[z] is not { } dz) continue;
                var p0 = At(src, x) + new Vector3(dx.X, dx.Y, dx.Z);
                var p1 = At(src, y) + new Vector3(dy.X, dy.Y, dy.Z);
                var p2 = At(src, z) + new Vector3(dz.X, dz.Y, dz.Z);
                var n0 = Vector3.Cross(At(src, y) - At(src, x), At(src, z) - At(src, x));
                var n1 = Vector3.Cross(p1 - p0, p2 - p0);
                if (n0.LengthSquared() > 1e-14f && Vector3.Dot(n0, n1) < 0f) folds++;
            }
        return folds;
    }

    /// <summary>
    /// Path A, A0.3 for real: Gen A made featureless with the owner's bulge (BulgeSwap), Stormblessed's bottoms landed on
    /// it — with the bulge's own uvs, and with its landing uvs (the bibo garment's place on it) standing in for them in
    /// the correspondence only.
    /// </summary>
    [LocalDataFact(GenALegs, LocalData.TbseUvMaps, CrossSexNudeLegsLocalTests.LavaBod, CrossSexNudeLegsLocalTests.StormblessedLegs)]
    public void Path_a_landing()
    {
        var bulge = TbseBulge.Shared;
        Assert.NotNull(bulge);
        var genA = File.ReadAllBytes(LocalData.Path(GenALegs));
        var swapped = BulgeSwap.Featureless(genA, bulge!, out var reason);
        Assert.True(swapped != null, reason);
        output.WriteLine($"featureless Gen A: {swapped!.SlotOf.Length} bulge vertices written, {swapped.Collapsed} triangles collapsed");
        var featureless = swapped.Model;
        var fParts = ModelPartReader.Read(featureless)!;
        var maps = new UVRemapService(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps));
        var garment = File.ReadAllBytes(LocalData.Path(CrossSexNudeLegsLocalTests.StormblessedLegs));
        var (cat, option) = CrossSexNudeLegsLocalTests.MadeFor(CrossSexNudeLegsLocalTests.StormblessedLegs, CrossSexNudeLegsLocalTests.LavaBod);
        var source = File.ReadAllBytes(cat.PathOf(option));
        var sourceParts = ModelPartReader.Read(source)!;

        var landed = BodyRetarget.Uv(featureless);
        for (int i = 0; i < swapped.SlotOf.Length; i++)
            if (bulge!.Data.land[i] is { } l) { landed[swapped.SlotOf[i] * 2] = l[0]; landed[swapped.SlotOf[i] * 2 + 1] = l[1]; }
        // The measure's baseline: the same source onto a women's body of the same layout, where nothing scrambles.
        var biboA = File.ReadAllBytes(LocalData.Path(LocalData.Mods + ":Bibo+/bottoms - small clothes/type a - medium/chara/equipment/e0000/model/c0201e0000_dwn.mdl"));
        Assert.Null(BodyRetarget.BuildPair("_dwn", source, biboA, "_dwn", false, null, null, maps, out var basePair));
        var basePlan = BodyRetarget.Plan(ModelPartReader.Read(garment)!, (byte[])garment.Clone(), [basePair], "_dwn",
                                         replaceSkin: true, acrossBodies: true, cutHidden: true);
        output.WriteLine($"baseline Stormblessed -> Bibo+ Type A M: source skin folds in the field {SourceFolds(basePair, source)}; "
                         + $"cloth folds {CrossSexRefit.ClothFolded(garment, basePlan)}");
        foreach (var (name, uvT) in new[] { ("own uvs", BodyRetarget.Uv(featureless)), ("landing uvs", landed) })
        {
            Assert.True(BodyCorrespondence.TryBuild(sourceParts, BodyRetarget.Uv(source), fParts, uvT, "_dwn", out var corr, out var refusal,
                                                    out var stats, maps, false, true), refusal);
            var pair = new BodyRetarget.SlotPair("_dwn", corr!, fParts, featureless, source);
            var plan = BodyRetarget.Plan(ModelPartReader.Read(garment)!, (byte[])garment.Clone(), [pair], "_dwn",
                                         replaceSkin: true, acrossBodies: true, cutHidden: true);
            output.WriteLine($"Stormblessed -> featureless Gen A ({name}): coverage {stats?.VertexCoverage:P1}; source skin folds in the field "
                             + $"{SourceFolds(pair, source)}; cloth folds {CrossSexRefit.ClothFolded(garment, plan)}, missed {plan.Report.Missed}");
        }
    }
}
