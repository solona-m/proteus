using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using XivLiveMesh;

namespace Proteus.Tests;

/// <summary>
/// A check of real installed hair mods for a Midlander woman, against the real game: which hairstyles a check would fit
/// and why others are refused, how long the bends take — and, on copies of a few mods, the whole check saved, with the
/// saved ports read back.
/// </summary>
[Trait("Speed", "Slow")]
public sealed class HairPortScanLocalTests(ITestOutputHelper o)
{
    private const ushort Me = 201;

    /// <summary>Penumbra stood in for: every mod switched on with its default options, the first one found winning.</summary>
    private sealed class Host(string modsRoot, HairPortLocalTests.Game game) : HairPortWatcher.IHost
    {
        public readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Lines = [];
        public bool Enabled => true;
        public string? ModsRoot => modsRoot;
        public string? Resolve(string gamePath) => Files.GetValueOrDefault(gamePath);
        public PbdFile? Pbd() => game.Pbd;
        public byte[]? ReadGame(string gamePath) => game.File(gamePath);
        public void Log(string message) => Lines.Add(message);
        public void Announce() { }
        public void Reload(string modDir) { }
        public string? WornNow() => null;
        public void Redraw() { }
        public bool GameHas(string gamePath) => game.Data.FileExists(gamePath);

        /// <summary>What a mod's default data and each group's first option publish, as Penumbra would serve them.</summary>
        public void Serve(string modRoot, IReadOnlyList<HairPortPlan.Owner> owners)
        {
            foreach (var owner in owners.Where(w => w.IsDefault || w.Option == owners.First(f => f.Group == w.Group).Option))
                foreach (var (path, rel) in owner.Files)
                    if (HairPortPlan.Parse(path) != null && !HairPortPlan.IsPortFile(rel))
                        Files.TryAdd(path, Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        }
    }

    /// <summary>Installed mods whose author made a woman's hairstyle for another race but not for Midlanders.</summary>
    private static IEnumerable<(string Root, List<HairPortPlan.Owner> Owners, List<int> Ids)> Candidates(string mods)
    {
        foreach (var root in Directory.EnumerateDirectories(mods))
        {
            if (HairPortPlan.ReadOwners(root) is not { } owners || owners.Any(w => w.IsCombining)) continue;
            var ids = HairPortScan.IdsInMod(owners, Me)
                                  .Where(id => !HairPortPlan.AuthoredRaces(owners, id).Contains(Me)).ToList();
            if (ids.Count > 0) yield return (root, owners, ids);
        }
    }

    [LocalDataFact(LocalData.Mods, LocalData.GameData)]
    [Trait("Plan", "AC-hair-port-scan-1")]
    public void Installed_mods_check_for_midlander_female()
    {
        var game = HairPortLocalTests.Load();
        var mods = LocalData.Path(LocalData.Mods);
        var candidates = Candidates(mods).Take(40).ToList();
        Assert.NotEmpty(candidates);

        // Decide and build only — nothing saved into the installed mods.
        var host = new Host(mods, game);
        foreach (var (root, owners, _) in candidates) host.Serve(root, owners);
        var candidatesFor = HairPortPlan.OrderSources(Me, HairPortPlan.SameGenderRaces(Me)).ToList();
        int ports = 0, refused = 0;
        var bends = new List<long>();
        var reasons = new Dictionary<string, int>();
        var clock = Stopwatch.StartNew();
        var ids = HairPortScan.IdsForFull(Me, host.GameHas);
        long idsMs = clock.ElapsedMilliseconds;
        o.WriteLine($"hairstyles a Midlander woman can choose: {ids.Count} (listed in {idsMs} ms), highest {ids.Max()}");
        Assert.Contains(7, ids);

        foreach (var (root, _, modIds) in candidates)
            foreach (int id in modIds)
            {
                var target = HairPortPlan.HairPath(Me, id);
                var resolved = HairPortScan.PathsFor(Me, id, candidatesFor).ToDictionary(p => p, p => (string?)host.Resolve(p),
                                                                                         StringComparer.OrdinalIgnoreCase);
                var d = HairPortWatcher.Decide.What(target, resolved, candidatesFor, mods, HairPortRecord.Read, game.File);
                if (d.Kind != HairPortWatcher.Decide.Kind.Port
                    || !string.Equals(d.ModRoot, root, StringComparison.OrdinalIgnoreCase)) continue;
                var t0 = clock.ElapsedMilliseconds;
                var plan = HairPortBuilder.Build(root, d.Source!, [Me], game.Pbd, game.File, withSharers: true);
                bends.Add(clock.ElapsedMilliseconds - t0);
                if (plan.Writes.Any(w => w.TargetGamePath.Equals(target, StringComparison.OrdinalIgnoreCase))) ports++;
                else if (plan.Refused.TryGetValue(Me, out var why))
                {
                    refused++;
                    var k = why.Length > 60 ? why[..60] : why;
                    reasons[k] = reasons.GetValueOrDefault(k) + 1;
                    o.WriteLine($"  {Path.GetFileName(root)} h{id:D4}: refused — {why}");
                }
            }

        bends.Sort();
        o.WriteLine($"{candidates.Count} mod(s): {ports} port(s) planned, {refused} refused");
        if (bends.Count > 0)
            o.WriteLine($"bend+build ms: p50 {bends[bends.Count / 2]}, p90 {bends[bends.Count * 9 / 10]}, max {bends[^1]}");
        foreach (var (why, n) in reasons.OrderByDescending(p => p.Value)) o.WriteLine($"  {n} × {why}");
        Assert.True(ports > 0, "no installed hair mod for another race could be fitted to a Midlander woman");
    }

    [LocalDataFact(LocalData.Mods, LocalData.GameData)]
    [Trait("Plan", "AC-hair-port-scan-1")]
    [Trait("Plan", "EDGE-hair-port-scan-1")]
    public void A_check_of_copied_real_mods_saves_ports_that_read_back()
    {
        var game = HairPortLocalTests.Load();
        var mods = LocalData.Path(LocalData.Mods);
        var temp = Path.Combine(Path.GetTempPath(), "proteus_scan_local_" + Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        try
        {
            // A few small mods, default-data-only ones first (the shape of the mod that prompted this).
            var picked = Candidates(mods)
                .Where(c => DirSize(c.Root) < 40L << 20)
                .OrderBy(c => c.Owners.Any(w => !w.IsDefault && w.Files.Keys.Any(k => HairPortPlan.Parse(k) != null)) ? 1 : 0)
                .Take(4).ToList();
            Assert.NotEmpty(picked);

            var host = new Host(temp, game);
            foreach (var (root, _, _) in picked)
            {
                var copy = Path.Combine(temp, Path.GetFileName(root));
                CopyDir(root, copy);
                // A copy of a mod already fitted on this machine starts again from the author's files.
                HairPortWriter.Undo(copy, _ => true, decline: false);
                var record = HairPortRecord.Read(copy);
                record.Declined.Clear();
                HairPortRecord.Write(copy, record);
                host.Serve(copy, HairPortPlan.ReadOwners(copy)!);
            }

            var ids = picked.SelectMany(p => p.Ids).Distinct().ToList();
            var result = HairPortScan.Run(host, Me, ids, null, new HairPortScan.Memo(), force: true, "local", default);
            foreach (var r in result.Rows) o.WriteLine($"  h{r.Id:D4} {r.ModDir}: {r.State} {r.Message}");
            foreach (var l in host.Lines.Where(l => l.StartsWith("checked", StringComparison.Ordinal))) o.WriteLine(l);

            var fitted = result.Rows.Where(r => r.State == HairPortScan.RowState.Fitted).ToList();
            Assert.NotEmpty(fitted);
            foreach (var row in fitted)
            {
                var entry = HairPortRecord.Read(row.ModRoot).Ports.First(e => e.TargetGamePath.Equals(row.TargetGamePath, StringComparison.OrdinalIgnoreCase));
                var saved = File.ReadAllBytes(Path.Combine(row.ModRoot, entry.File));
                var source = File.ReadAllBytes(Path.Combine(row.ModRoot, entry.SourceFile));
                var port = ModelPartReader.Read(saved);
                var from = ModelPartReader.Read(source);
                Assert.NotNull(port);
                Assert.NotNull(from);
                // The same mesh, bent: as many vertices, none lost, and every one a real position.
                Assert.Equal(from!.Positions.Length, port!.Positions.Length);
                Assert.All(port.Positions, v => Assert.True(float.IsFinite(v)));
                Assert.All(entry.Materials.Values, rel => Assert.True(File.Exists(Path.Combine(row.ModRoot, rel)), rel));
            }
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    private static long DirSize(string root)
        => new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    private static void CopyDir(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
    }
}
