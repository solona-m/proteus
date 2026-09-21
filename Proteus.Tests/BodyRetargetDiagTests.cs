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
/// Instruments, not assertions — the house diag style. These run against the real body mods on this machine and print
/// numbers to judge by; they skip silently when those mods are not installed.
/// <para/>
/// The one that earns its keep is <see cref="Every_Neolithe_size_pair_shares_one_topology"/>: the whole v1 design rests
/// on the claim that every size in a family is the same mesh re-sculpted, and that claim was established by comparing
/// file lengths. This checks it across the real option space, through the reader the feature actually uses.
/// </summary>
public class BodyRetargetDiagTests(ITestOutputHelper output)
{
    private const string NeolitheRoot = @"E:\Penumbradt\Neolithe [ALL IN ONE]";
    private const string ChestFolder = NeolitheRoot + @"\DEFAULT CHEST - SmallClothes";
    private const string ExtraChestFolder = NeolitheRoot + @"\EXTRA CHEST - SmallClothes";
    private const string BuffChestFolder = NeolitheRoot + @"\EXTRA CHEST BUFF - SmallClothes";
    private const string LegsFolder = NeolitheRoot + @"\DEFAULT LEGS - SmallClothes";

    [Fact]
    public void Every_Neolithe_size_pair_shares_one_topology()
    {
        if (!Directory.Exists(ChestFolder)) return;

        // Sampled across the shape families as well as the size axis, because the picker allows any pair.
        var chest = Models(
            ChestFolder + @"\SFW XS.mdl",
            ChestFolder + @"\SFW S.mdl",
            ChestFolder + @"\SFW M.mdl",
            ChestFolder + @"\SFW L.mdl",
            ExtraChestFolder + @"\SFW Almond M.mdl",
            ExtraChestFolder + @"\SFW Hazelnut L.mdl",
            ExtraChestFolder + @"\SFW Macadamia XS.mdl",
            BuffChestFolder + @"\SFW Buff M.mdl");

        var legs = Models(
            LegsFolder + @"\SFW Small.mdl",
            LegsFolder + @"\SFW Medium.mdl",
            LegsFolder + @"\SFW Large.mdl");

        int pairs = 0, agreed = 0;
        foreach (var group in new[] { ("chest", chest), ("legs", legs) })
        {
            var (label, models) = group;
            output.WriteLine($"── {label}: {models.Count} models read ──");
            foreach (var a in models)
            foreach (var b in models)
            {
                if (ReferenceEquals(a.Model, b.Model)) continue;
                string nameA = a.Name, nameB = b.Name;
                pairs++;
                bool ok = IdentityCorrespondence.TryBuild(a.Model, b.Model, label, out var built, out string refusal,
                                                          a.Uv, b.Uv);
                if (ok)
                {
                    agreed++;
                    float worst = 0f;
                    foreach (var d in built!.Field)
                        if (d is { } v) worst = MathF.Max(worst, v.Length());
                    output.WriteLine($"  OK   {nameA,-22} -> {nameB,-22} worst move {worst * 1000f,7:F2} mm");
                }
                else
                {
                    output.WriteLine($"  FAIL {nameA,-22} -> {nameB,-22} {refusal}");
                }
            }
        }

        output.WriteLine($"{agreed}/{pairs} pairs share a topology.");
        Assert.Equal(pairs, agreed);
    }

    [Fact]
    public void A_different_genital_family_is_refused_not_retargeted()
    {
        // The legs axis is NOT one topology family: Gen A/B/C/Puffy each replace geometry, so they have their own
        // vertex counts. The picker offers every option, so the guard is the only thing standing between the user and
        // a silent nonsense result — this asserts it holds, and that the message says which way to go.
        if (!File.Exists(LegsFolder + @"\GEN B Medium.mdl") || !File.Exists(LegsFolder + @"\SFW Medium.mdl")) return;

        var sfwBytes = File.ReadAllBytes(LegsFolder + @"\SFW Medium.mdl");
        var genBytes = File.ReadAllBytes(LegsFolder + @"\GEN B Medium.mdl");

        bool ok = IdentityCorrespondence.TryBuild(ModelPartReader.Read(sfwBytes)!, ModelPartReader.Read(genBytes)!,
                                                  "legs", out _, out string refusal, Uv(sfwBytes), Uv(genBytes));

        output.WriteLine(refusal);
        Assert.False(ok);
        Assert.Contains("different meshes", refusal);
    }

    [Fact]
    public void The_catalog_reads_Neolithe_s_real_option_list()
    {
        if (!Directory.Exists(NeolitheRoot)) return;

        var catalog = BodySizeCatalog.Read(NeolitheRoot);
        Assert.True(catalog.IsBody);

        foreach (string slot in catalog.Slots)
        {
            var options = catalog.For(slot);
            output.WriteLine($"{slot}: {options.Count} options across " +
                             $"{options.Select(o => o.Group).Distinct().Count()} groups");
            foreach (var group in options.GroupBy(o => o.Group))
                output.WriteLine($"    {group.Count(),4}  {group.Key}");
        }

        // Separator options like "--- DEFAULT ---" carry no file, so they must not be offered.
        Assert.DoesNotContain(catalog.Options, o => o.Name.StartsWith("---", StringComparison.Ordinal));

        // Every listed option must actually resolve on disk, case-insensitively.
        var missing = catalog.Options.Where(o => !File.Exists(catalog.PathOf(o))).ToList();
        foreach (var m in missing.Take(10)) output.WriteLine($"MISSING {m.Label} -> {m.Rel}");
        Assert.Empty(missing);
    }

    [Fact]
    public void The_detector_finds_the_size_a_model_was_built_from()
    {
        string probe = ChestFolder + @"\SFW M.mdl";
        if (!File.Exists(probe) || !Directory.Exists(NeolitheRoot)) return;

        var catalog = BodySizeCatalog.Read(NeolitheRoot);
        var candidates = catalog.For("_top");
        if (candidates.Count == 0) return;

        // A body model stands in for a garment here: its skin mesh is trivially a copy of one size's, which is exactly
        // the situation a real gear model is in. If the detector cannot find the size a model IS, it will not find the
        // size a garment was built against either.
        var garment = ModelPartReader.Read(File.ReadAllBytes(probe))!;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ranking = BodySizeMatch.Rank(garment, candidates, catalog.PathOf);
        clock.Stop();

        output.WriteLine($"{candidates.Count} candidates scored in {clock.ElapsedMilliseconds} ms");
        output.WriteLine($"confidence {ranking.Confidence}");
        foreach (var s in ranking.Scores.Take(5))
            output.WriteLine($"  {s.HitRate,7:P1}  rms {s.Rms * 1000f,7:F3} mm  {s.Option.FullLabel}");

        Assert.Equal(BodySizeMatch.Confidence.Exact, ranking.Confidence);
        Assert.Equal("SFW M", ranking.Best!.Value.Option.Name);
    }

    [Fact]
    public void Retarget_a_garment_between_two_Neolithe_sizes()
    {
        // Point PROTEUS_RETARGET_GARMENT at a .mdl authored for Neolithe to exercise the whole solve on real
        // geometry and drop .obj files on the Desktop to look at.
        string? garmentPath = Environment.GetEnvironmentVariable("PROTEUS_RETARGET_GARMENT");
        if (string.IsNullOrWhiteSpace(garmentPath) || !File.Exists(garmentPath)) return;
        if (!File.Exists(ChestFolder + @"\SFW M.mdl")) return;

        var garmentBytes = File.ReadAllBytes(garmentPath);
        var garment = ModelPartReader.Read(garmentBytes);
        Assert.NotNull(garment);

        var pairs = new List<BodyRetarget.SlotPair>();
        AddPair(pairs, "_top", ChestFolder + @"\SFW M.mdl", ChestFolder + @"\SFW L.mdl");
        AddPair(pairs, "_dwn", LegsFolder + @"\SFW Medium.mdl", LegsFolder + @"\SFW Large.mdl");

        var planned = BodyRetarget.Plan(garment!, garmentBytes, pairs);
        var r = planned.Report;

        output.WriteLine($"garment      {Path.GetFileName(garmentPath)}");
        output.WriteLine($"vertices     {r.Nodes:N0}");
        output.WriteLine($"snapped      {r.Snapped:N0} ({r.SnapRate:P0} of the {r.Transferred:N0} moved)");
        output.WriteLine($"missed       {r.Missed:N0}");
        output.WriteLine($"pushed out   {r.Pushed:N0}, worst {r.WorstPush * 1000f:F2} mm");
        output.WriteLine($"worst move   {r.WorstMove * 1000f:F2} mm");
        output.WriteLine($"spares       {r.UnmappedSpares:N0}   other LODs: {r.HasOtherLods}");
        output.WriteLine($"bytes        {garmentBytes.Length:N0} -> {planned.Model.Length:N0}");

        Assert.Equal(garmentBytes.Length, planned.Model.Length);

        // Clearance histogram before and after, so the push-out's effect and the Clearance constant are numbers
        // rather than a guess.
        var targetTop = Read(ChestFolder + @"\SFW L.mdl");
        var targetDwn = Read(LegsFolder + @"\SFW Large.mdl");
        var before = ModelPartReader.Read(garmentBytes)!;
        var after = ModelPartReader.Read(planned.Model)!;
        output.WriteLine("");
        output.WriteLine("distance from the TARGET body, cloth vertices only:");
        Histogram("before", before, [targetTop, targetDwn]);
        Histogram("after ", after, [targetTop, targetDwn]);

        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                      "OneDrive", "Desktop");
        if (!Directory.Exists(desktop)) return;
        File.WriteAllBytes(Path.Combine(desktop, "retarget_after.mdl"), planned.Model);
        output.WriteLine($"wrote {Path.Combine(desktop, "retarget_after.mdl")}");
    }

    private void Histogram(string label, ModelParts garment, IReadOnlyList<ModelParts> bodies)
    {
        var surfaces = bodies.Select(b => new BodySurface(b, 0.02f)).ToArray();
        int[] buckets = new int[6];   // inside, <0.5mm, <2mm, <10mm, <30mm, beyond
        foreach (var part in garment.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles.Distinct())
            {
                var p = new Vector3(garment.Positions[v * 3], garment.Positions[v * 3 + 1],
                                    garment.Positions[v * 3 + 2]);
                float best = float.MaxValue;
                foreach (var s in surfaces)
                    if (s.Nearest(p, 0.03f, out var hit)) best = MathF.Min(best, hit.Distance);

                buckets[best == float.MaxValue ? 5
                      : best < 0.0005f ? 1
                      : best < 0.002f ? 2
                      : best < 0.01f ? 3
                      : 4]++;
            }
        }
        output.WriteLine($"  {label}  <0.5mm {buckets[1],6}  <2mm {buckets[2],6}  <10mm {buckets[3],6}  " +
                         $"<30mm {buckets[4],6}  beyond {buckets[5],6}");
    }

    private static void AddPair(List<BodyRetarget.SlotPair> pairs, string slot, string sourcePath, string targetPath)
    {
        if (!File.Exists(sourcePath) || !File.Exists(targetPath)) return;
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var targetBytes = File.ReadAllBytes(targetPath);
        var source = ModelPartReader.Read(sourceBytes)!;
        var target = ModelPartReader.Read(targetBytes)!;
        if (!IdentityCorrespondence.TryBuild(source, target, slot, out var built, out string refusal,
                                             Uv(sourceBytes), Uv(targetBytes)))
            throw new InvalidOperationException(refusal);
        pairs.Add(new BodyRetarget.SlotPair(slot, built!, target));
    }

    private static ModelParts Read(string path)
        => ModelPartReader.Read(File.ReadAllBytes(path))
           ?? throw new InvalidOperationException($"could not read {path}");

    private static List<(string Name, ModelParts Model, float[] Uv)> Models(params string[] paths)
    {
        var list = new List<(string, ModelParts, float[])>();
        foreach (string path in paths)
        {
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            var model = ModelPartReader.Read(bytes);
            if (model == null) continue;
            list.Add((Path.GetFileNameWithoutExtension(path), model, Uv(bytes)));
        }
        return list;
    }

    /// <summary>uv0 in <see cref="ModelPartReader"/>'s vertex order, which is what the guard's proof needs.</summary>
    internal static float[] Uv(byte[] mdl)
        => SecondSkinWriter.TryReadLod0Geometry(mdl, out _, out var uv, out _, out _, out _, false, false, null)
            ? uv
            : [];
}
