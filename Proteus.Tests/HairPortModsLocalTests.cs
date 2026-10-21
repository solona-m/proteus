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
/// Hair ports measured against what hair modders shipped themselves: for every installed hair mod option that publishes
/// a hairstyle for Midlanders AND for other races of the same sex, the Midlander model is ported to each of those races
/// and compared with the author's own model for it.
/// </summary>
public sealed class HairPortModsLocalTests(ITestOutputHelper o)
{
    /// <param name="AsIsP90">The same measure for the source left unbent: how far the author's own race models differ
    /// from each other, which a port cannot be expected to undo.</param>
    private sealed record Row(string Mod, string Option, int Id, ushort To, double Median, double P90, bool Refused,
                              string Why, bool LmMod, double AsIsMedian, double AsIsP90);

    [LocalDataFact(LocalData.Mods, LocalData.GameData)]
    [Trait("Plan", "AC-hair-race-port-2")]
    public void Ports_of_installed_mods_land_near_the_authors_own_ports()
    {
        var game = HairPortLocalTests.Load();
        var mods = LocalData.Path(LocalData.Mods);
        var rows = new List<Row>();
        foreach (var modRoot in Directory.EnumerateDirectories(mods))
        {
            if (HairPortPlan.ReadOwners(modRoot) is not { } owners) continue;
            string mod = Path.GetFileName(modRoot);
            bool lm = mod.StartsWith("LM", StringComparison.OrdinalIgnoreCase) || mod.StartsWith("[LM]", StringComparison.Ordinal);
            foreach (var owner in owners.Where(w => !w.IsCombining))
            {
                // Per hairstyle id: which races this place publishes, by file.
                var byId = owner.Files
                    .Select(f => (At: HairPortPlan.Parse(f.Key), File: f.Value))
                    .Where(f => f.At != null && !HairPortPlan.IsPortFile(f.File))
                    .GroupBy(f => f.At!.Value.Id);
                foreach (var group in byId)
                {
                    var races = group.ToDictionary(f => f.At!.Value.Race, f => f.File);
                    foreach (ushort root in new ushort[] { 201, 101 })
                    {
                        if (!races.TryGetValue(root, out var srcRel)) continue;
                        if (Read(modRoot, srcRel) is not { } src) continue;
                        foreach (var (to, rel) in races.Where(r => r.Key != root && r.Key / 100 % 2 == root / 100 % 2))
                        {
                            if (Read(modRoot, rel) is not { } theirs) continue;
                            var row = Compare(game, modRoot, owners, owner, group.Key, root, to, src, theirs, lm);
                            if (row != null) rows.Add(row);
                        }
                    }
                }
            }
            if (rows.Count > 400) break;
        }

        foreach (var r in rows.OrderBy(r => r.Mod).ThenBy(r => r.Id).ThenBy(r => r.To))
            o.WriteLine($"{r.Mod} | {r.Option} | h{r.Id:D4} -> c{r.To:D4}: median {r.Median * 1000:F1} mm, p90 {r.P90 * 1000:F1} mm "
                      + $"(unbent {r.AsIsMedian * 1000:F1} / {r.AsIsP90 * 1000:F1} mm)"
                      + (r.Refused ? $"  REFUSED {r.Why}" : ""));
        foreach (var g in rows.GroupBy(r => r.To).OrderBy(g => g.Key))
            o.WriteLine($"c{g.Key:D4}: {g.Count()} ports, mean median {g.Average(r => r.Median) * 1000:F1} mm, " +
                        $"worst median {g.Max(r => r.Median) * 1000:F1} mm, refused {g.Count(r => r.Refused)}");
        var lmRows = rows.Where(r => r.LmMod).ToList();
        if (lmRows.Count > 0)
            o.WriteLine($"LM mods: {lmRows.Count} ports, mean median {lmRows.Average(r => r.Median) * 1000:F1} mm, " +
                        $"refused {lmRows.Count(r => r.Refused)}");

        Assert.True(rows.Count > 0, "no installed mod ships a hairstyle for Midlanders and another race");
        // Physics hair borrows another hairstyle's skeleton per race (an Est entry): the port carries it, so LM's
        // hairstyles — made that way — port at all.
        Assert.DoesNotContain(rows, r => r.LmMod && r.Refused);
        // Authors fit by hand, so a port is not expected to match to the millimetre — but it must land on the same head.
        Assert.True(rows.Average(r => r.Median) < 0.015, $"ports land {rows.Average(r => r.Median) * 1000:F1} mm from the authors' on average");
    }

    /// <summary>
    /// Installed hair mods that give the source race its skeleton in an option of another group than the model's ([SME]
    /// Vale: models in "Models:", the h0178 → 127 entry in "Textures + Core Files"): their ports move with that skeleton,
    /// and none is refused for bones the target race lacks.
    /// </summary>
    [LocalDataFact(LocalData.Mods, LocalData.GameData)]
    [Trait("Plan", "EDGE-hair-race-port-6")]
    public void A_skeleton_entry_in_another_group_ports_installed_mods()
    {
        var game = HairPortLocalTests.Load();
        var refused = new List<string>();
        int ported = 0;
        foreach (var modRoot in Directory.EnumerateDirectories(LocalData.Path(LocalData.Mods)))
        {
            if (HairPortPlan.ReadOwners(modRoot) is not { } owners) continue;
            var defaults = owners.FirstOrDefault(w => w.IsDefault);
            foreach (var owner in owners.Where(w => !w.IsCombining))
                foreach (var (path, rel) in owner.Files)
                {
                    if (HairPortPlan.IsPortFile(rel) || HairPortPlan.Parse(path) is not { } at) continue;
                    if (HairPortPlan.SourceEst(owner, defaults, at.Race, at.Id) != null
                        || HairPortPlan.SourceEst(owner, defaults, at.Race, at.Id, owners) is not { } est) continue;
                    var plan = HairPortBuilder.Build(modRoot, path, HairPortPlan.SameGenderRaces(at.Race), game.Pbd, game.File,
                                                     withSharers: false);
                    foreach (var w in plan.Writes)
                        o.WriteLine($"{Path.GetFileName(modRoot)} | {owner.Group} / {owner.Option} | h{at.Id:D4} (skeleton {est})"
                                    + $" -> {w.TargetGamePath}: entry {w.Est}, carried from {w.SkeletonFrom}");
                    ported += plan.Writes.Count + plan.Current.Count;
                    foreach (var (to, why) in plan.Refused)
                    {
                        o.WriteLine($"{Path.GetFileName(modRoot)} | h{at.Id:D4} -> c{to:D4} REFUSED {why}");
                        if (why.Contains("j_ex_", StringComparison.Ordinal)) refused.Add($"{Path.GetFileName(modRoot)} c{to:D4}: {why}");
                    }
                }
        }
        Assert.True(ported > 0, "no installed hair mod gives its skeleton in another group");
        Assert.Empty(refused);
    }

    private static Row? Compare(HairPortLocalTests.Game game, string modRoot, IReadOnlyList<HairPortPlan.Owner> owners,
                                HairPortPlan.Owner owner, int id, ushort from, ushort to, byte[] src, byte[] theirs,
                                bool lm)
    {
        string mod = Path.GetFileName(modRoot);
        var port = RacialModelBake.Port(src, from, to, game.Pbd, HairPortBaker.ParentOf, out _);
        if (port == null || ModelPartReader.Read(port) is not { } p || ModelPartReader.Read(theirs) is not { } t) return null;
        // The builder's own skeleton choice and bone set: the skeleton the target race loads, an entry to one of its own
        // that fits, or the source's carried.
        var place = new HairPortSkeleton.Place(modRoot, owners, owner, null, [], from, to, id, game.File,
                                               HairSkeletonTable.Lazy(game.File));
        var choice = HairPortSkeleton.Choose(place, src);
        IReadOnlySet<string> bones = HairPortSkeleton.Carry(place, new HairPortRecord(), src, choice, 0) is { } carried
            ? carried.Bones : choice.Bones;
        var result = HairPortBaker.Prepare(src, from, to, id, game.Pbd, bones, game.Head(from), game.Head(to),
                                           out string why);
        var (median, p90) = Nearest(p.Positions, t.Positions);
        var (asIs, asIsP90) = Nearest(ModelPartReader.Read(src)!.Positions, t.Positions);
        return new Row(mod, owner.IsDefault ? "(default)" : $"{owner.Group} / {owner.Option}", id, to, median, p90,
                       result == null, why, lm, asIs, asIsP90);
    }

    private static byte[]? Read(string modRoot, string rel)
    {
        try { return File.ReadAllBytes(Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar))); }
        catch (Exception) { return null; }
    }

    /// <summary>Median and 90th percentile, over a sample of <paramref name="a"/>'s vertices, of the distance to the
    /// nearest of <paramref name="b"/>'s.</summary>
    private static (double Median, double P90) Nearest(float[] a, float[] b)
    {
        int na = a.Length / 3, nb = b.Length / 3;
        int step = Math.Max(1, na / 800);
        var d = new List<float>();
        for (int i = 0; i < na; i += step)
        {
            float best = float.MaxValue;
            for (int j = 0; j < nb; j++)
            {
                float dx = b[j * 3] - a[i * 3], dy = b[j * 3 + 1] - a[i * 3 + 1], dz = b[j * 3 + 2] - a[i * 3 + 2];
                best = MathF.Min(best, dx * dx + dy * dy + dz * dz);
            }
            d.Add(MathF.Sqrt(best));
        }
        d.Sort();
        return (d[d.Count / 2], d[(int)(d.Count * 0.9)]);
    }
}
