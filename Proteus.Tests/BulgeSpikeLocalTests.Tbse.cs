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

    /// <summary>
    /// Diagnostic (plan genital-switches, Phase 0.2a): every TBSE-X IVCS2 legs file — each skin submesh's tags, size, window
    /// share, mean penis weight and rule, and each candidate piece's rim against the TBSE bulge's hole — the data the XL
    /// plug decision and the re-stitch reach are taken from.
    /// </summary>
    [LocalDataFact(LocalData.Mods)]
    [Trait("Suite", "Diagnostic")]
    public void Tbsex_hole_probe()
    {
        var bulge = TbseBulge.Shared!;
        string root = LocalData.Path(M + "The Body SE-X IVCS2 V1_1_0");
        if (!Directory.Exists(root)) { output.WriteLine("TBSE-X not installed"); return; }
        output.WriteLine($"bulge hole points: {bulge.Hole.Length}");
        foreach (var f in Directory.GetFiles(root, "c0101e0000_dwn.mdl", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(f);
            var parts = ModelPartReader.Read(bytes);
            var skin = ModelSkinReader.Read(bytes, null, null);
            string rel = Path.GetRelativePath(root, f);
            if (parts == null || skin == null) { output.WriteLine($"{rel}: unreadable"); continue; }
            var reading = GenitalParts.Read(parts, skin);
            output.WriteLine($"{rel}: penis {reading?.Penis}");
            if (reading == null) continue;
            float OnPenis(int v)
            {
                float w = 0f;
                for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
                    if (skin.BoneWeights[v * 8 + k] > 0f && GenitalParts.IsPenisBone(skin.BoneNames[skin.BoneIndices[v * 8 + k]]))
                        w += skin.BoneWeights[v * 8 + k];
                return w;
            }
            var skinParts = parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToList();
            var largest = skinParts.MaxBy(p => p.Triangles.Length);
            foreach (var p in skinParts)
            {
                var vs = p.Triangles.Distinct().ToArray();
                float window = vs.Count(v => GenitalParts.InWindow(At(parts, v), reading.Anchor)) / (float)vs.Length;
                float mean = vs.Average(OnPenis);
                var g = reading.Genitals.FirstOrDefault(x => x.Part == p);
                output.WriteLine($"   {p.Label} mask {p.AttributeMask:x} tris {p.Triangles.Length / 3}{(p == largest ? " (largest)" : "")} "
                                 + $"window {window:P0} penis weight {mean:0.00} rule {g?.Rule ?? "-"} ({g?.Triangles.Count ?? 0} tris)");
                bool narrow = p.AttributeMask == 0 && p != largest && mean >= GenitalParts.PenisWeight;
                if (g?.Rule != "c" && !narrow) continue;
                var (count, worst) = RimAgainst(parts, p, bulge.Hole);
                var match = BulgeSwap.MatchHole(parts, p, bulge.Hole);
                output.WriteLine($"      candidate ({(g?.Rule == "c" ? "rule c" : "narrow rule")}): rim {count} points, worst nearest "
                                 + $"{worst * 1000f:0.0} mm, MatchHole {(match != null ? "accepts" : "refuses")}");
            }
        }
    }

    /// <summary>
    /// Diagnostic (plan genital-switches, Phase 0.2b): every TBSE-X IVCS2 legs file's genital piece (rule c, and rule b for
    /// XL) — the hole its skin leaves (<see cref="BulgeSwap.HoleLoop"/>), the alignment's worst pair and whether
    /// <see cref="BulgeSwap.FitHole"/> takes it. Measured: every hole has the bulge's own 28 points; Flaccid L 10.6 mm (fits
    /// HoleFit), Flaccid XL 19.5 mm (refused) — what RestitchReach and the XL decision were taken from.
    /// </summary>
    [LocalDataFact(LocalData.Mods)]
    [Trait("Suite", "Diagnostic")]
    public void Tbsex_restitch_probe()
    {
        var bulge = TbseBulge.Shared!;
        string root = LocalData.Path(M + "The Body SE-X IVCS2 V1_1_0");
        if (!Directory.Exists(root)) { output.WriteLine("TBSE-X not installed"); return; }
        foreach (var f in Directory.GetFiles(root, "c0101e0000_dwn.mdl", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(f);
            var parts = ModelPartReader.Read(bytes);
            var skin = ModelSkinReader.Read(bytes, null, null);
            if (parts == null || skin == null || GenitalParts.Read(parts, skin) is not { } reading) continue;
            string rel = Path.GetRelativePath(root, f);
            foreach (var g in reading.Genitals.Where(g => SecondSkinWriter.IsBodySkinMaterial(g.Part.Material) && g.Part.AttributeMask == 0
                                                          && g.Part != parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).MaxBy(p => p.Triangles.Length)))
            {
                var loop = BulgeSwap.HoleLoop(parts, g.Part);
                var al = loop == null ? null : BulgeSwap.Align(bulge.Hole, loop.Select(v => At(parts, v)).ToList());
                var fit = BulgeSwap.FitHole(parts, g.Part, bulge, out var why);
                output.WriteLine($"{rel} {g.Part.Label} (rule {g.Rule}): hole {(loop?.Count.ToString() ?? "none")}, worst "
                                 + $"{(al is { } a ? $"{a.Worst * 1000f:0.0} mm" : "-")}, {(fit != null ? $"fitted ({fit.Merged} merged, {fit.Inserted} added)" : why)}");
            }
        }
    }

    /// <summary>A piece's rim on the body's other skin: its point count and the worst distance from one to the nearest hole point.</summary>
    private static (int Count, float Worst) RimAgainst(ModelParts parts, ModelPart piece, IReadOnlyList<Vector3> hole)
    {
        (long, long, long) K(Vector3 p) => ((long)MathF.Round(p.X * 1e5f), (long)MathF.Round(p.Y * 1e5f), (long)MathF.Round(p.Z * 1e5f));
        var other = new Dictionary<(long, long, long), Vector3>();
        foreach (var part in parts.Parts.Where(p => p.Island < 0 && p != piece && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            foreach (int v in part.Triangles) other.TryAdd(K(At(parts, v)), At(parts, v));
        var use = new Dictionary<((long, long, long), (long, long, long)), int>();
        for (int t = 0; t + 2 < piece.Triangles.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                var a = K(At(parts, piece.Triangles[t + k]));
                var b = K(At(parts, piece.Triangles[t + (k + 1) % 3]));
                var e = a.CompareTo(b) < 0 ? (a, b) : (b, a);
                use[e] = (use.TryGetValue(e, out int c) ? c : 0) + 1;
            }
        var pts = new HashSet<(long, long, long)>();
        foreach (var (e, n) in use)
            if (n == 1 && other.ContainsKey(e.Item1) && other.ContainsKey(e.Item2)) { pts.Add(e.Item1); pts.Add(e.Item2); }
        float worst = pts.Count == 0 ? float.NaN : pts.Max(p => hole.Min(h => Vector3.Distance(h, other[p])));
        return (pts.Count, worst);
    }
}
