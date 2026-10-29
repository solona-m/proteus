using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Every installed body option that draws smallclothes (an <c>e0000_</c> part), as a target for each installed body's
/// first option of the same slot: which pairs the skinless-target rule now accepts that the count refused, which it
/// still refuses and says why, and which the count accepted before and still does though the target draws smallclothes
/// where the source missed — the follow-up the rule leaves for later. The lists are for the PR; each pair it accepts
/// must meet the projection's limits.
/// </summary>
[Trait("Speed", "Slow")]
public class SkinlessTargetCensusLocalTests(ITestOutputHelper output)
{
    [LocalDataFact(LocalData.Mods)]
    [Trait("Plan", "AC-skinless-target-10")]
    public void Census_of_pairs_the_rule_changes()
    {
        var clock = Stopwatch.StartNew();
        var bodies = Directory.EnumerateDirectories(LocalData.Path(LocalData.Mods))
                              .Select(d => (Dir: d, Catalog: SafeRead(d)))
                              .Where(b => b.Catalog is { IsBody: true })
                              .ToList();
        output.WriteLine($"{bodies.Count} body mods");

        int accepted = 0, refused = 0, followUp = 0, pairs = 0;
        float worstUnprojected = 0f;
        int mostFoldedElsewhere = 0;
        foreach (string slot in new[] { "_dwn", "_top" })
        {
            var sources = bodies.Select(b => (b.Dir, Option: b.Catalog!.For(slot, "0201").FirstOrDefault(), b.Catalog))
                                .Where(s => s.Option != null).ToList();
            var targets = bodies.SelectMany(b => b.Catalog!.For(slot, "0201").Select(o => (b.Dir, Option: o, b.Catalog)))
                                .Select(t => (t.Dir, t.Option, Path: t.Catalog!.PathOf(t.Option)))
                                .Where(t => File.Exists(t.Path))
                                .Select(t => (t.Dir, t.Option, t.Path, Bytes: File.ReadAllBytes(t.Path)))
                                .Where(t => ModelPartReader.Read(t.Bytes) is { } m
                                            && m.Parts.Any(p => p.Island < 0 && BodySurface.IsSmallclothes(p.Material)
                                                                && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
                                .ToList();
            foreach (var source in sources)
            {
                var sourceBytes = File.ReadAllBytes(source.Catalog!.PathOf(source.Option!));
                var sourceParts = ModelPartReader.Read(sourceBytes);
                if (sourceParts == null) continue;
                var sourceUv = SkinlessTargetMeasureLocalTests.Uv(sourceBytes);
                foreach (var target in targets)
                {
                    if (string.Equals(source.Catalog.PathOf(source.Option!), target.Path, StringComparison.OrdinalIgnoreCase)) continue;
                    pairs++;
                    string name = $"{slot} {Path.GetFileName(source.Dir)} / {source.Option!.Label} -> " +
                                  $"{Path.GetFileName(target.Dir)} / {target.Option.Label}";
                    bool built = BodyCorrespondence.TryBuild(sourceParts, sourceUv, ModelPartReader.Read(target.Bytes)!,
                                                             SkinlessTargetMeasureLocalTests.Uv(target.Bytes), slot,
                                                             out var corr, out string refusal);
                    if (built && corr!.TargetWorn && corr is UvAtlasCorrespondence atlas)
                    {
                        var m = atlas.Measured;
                        accepted++;
                        output.WriteLine($"ACCEPTED NOW  {name}: {m.VertexCoverage:P1}, excused {m.Excused}, unprojected " +
                                         $"{m.Unprojected}, on skin {m.OnSkin}, folded elsewhere {m.FoldedElsewhere}");
                        // Recorded, not judged: how many the relax kept off the body to avoid a fold is no refusal any
                        // more (cross-sex-tops-yas), only how far off each is (CarriedOnlyReach). Elegy's "bjubjue"
                        // bottoms onto Bibo+'s SFW undies left 22 of 2,087 (1.05%), measured 2026-10-20.
                        output.WriteLine($"  set aside {m.FoldedSetAside} ({m.FoldedTouching} folds touching them)");
                        worstUnprojected = MathF.Max(worstUnprojected, (float)m.Unprojected / m.Excused);
                        // Folds the uv landings make by themselves, no corner excused or carried: recorded, not judged
                        // (Tre's Gen A onto Bibo+'s SFW undies: 4). The count path has never looked for them either.
                        mostFoldedElsewhere = Math.Max(mostFoldedElsewhere, m.FoldedElsewhere);
                    }
                    else if (!built && refusal.Contains("finds skin to land on", StringComparison.Ordinal))
                    {
                        refused++;
                        output.WriteLine($"NO SKIN THERE {name}: {refusal[..Math.Min(40, refusal.Length)]}…");
                    }
                    else if (built && corr is UvAtlasCorrespondence { Measured: var m } && m.Landed < m.Placeable)
                    {
                        followUp++;
                        output.WriteLine($"FOLLOW-UP     {name}: accepted as before at {m.VertexCoverage:P1}");
                    }
                }
            }
        }
        output.WriteLine($"{pairs} pairs in {clock.Elapsed.TotalSeconds:F0} s: {accepted} accepted now, {refused} refused " +
                         $"as having no skin there, {followUp} accepted before with misses onto smallclothes targets; " +
                         $"most left at the carry {worstUnprojected:P2}; most folded by the landings alone {mostFoldedElsewhere}");
    }

    private static BodySizeCatalog? SafeRead(string dir)
    {
        try { return BodySizeCatalog.Read(dir); }
        catch (Exception) { return null; }
    }
}
