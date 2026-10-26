using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Proteus.Services;
using Xunit;
using XivLiveMesh;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>
/// A check of the player's hair mods: a Midlander woman (c0201) whose collection has hair mods made for Elezen women
/// (c0601) only — the "Genesis Floof for Elezen Hair 7" shape, default data and no material of its own — fitted before
/// any of them is worn, through <see cref="HairPortScan.Run"/> with Penumbra and the game stood in for.
/// </summary>
public class HairPortScanTests
{
    private const ushort Me = 201, Elezen = 601;
    private static readonly ushort[] Races = [101, 201, 401, 601, 801, 1001, 1101, 1201, 1401, 1601, 1801];
    private static readonly int[] Ids = [5, 6, 7, 9, 105];

    private sealed class Host(string modsRoot) : HairPortWatcher.IHost
    {
        public readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Calls = [];
        public readonly List<string> Lines = [];
        public readonly List<string> Resolved = [];
        public Func<string, byte[]?> Game = GameOf(Races, Ids);
        public int GameReads;
        public string? Worn { get; set; }
        public string? WornAfter { get; set; }

        public bool Enabled => true;
        public string? ModsRoot => modsRoot;
        public string? Resolve(string gamePath) { Resolved.Add(gamePath); return Files.GetValueOrDefault(gamePath); }
        public PbdFile? Pbd() => HairPortFixtures.Pbd();
        public byte[]? ReadGame(string gamePath) { GameReads++; return Game(gamePath); }
        public void Log(string message) => Lines.Add(message);
        public void Announce() => Calls.Add("announce");
        public void Reload(string modDir) => Calls.Add("reload " + modDir);
        public string? WornNow() => WornAfter ?? Worn;
        public void Redraw() => Calls.Add("redraw");
        public bool GameHas(string gamePath) => Game(gamePath) != null;
        public string ModName(string modDir) => modDir + " (name)";
        public void AnnounceScan(string line) => Calls.Add("summary " + line);
        public bool ResolveFails { get; set; }
        public IReadOnlyDictionary<string, string?>? ResolveAll(IReadOnlyList<string> gamePaths)
            => ResolveFails ? null : gamePaths.ToDictionary(p => p, p => (string?)Resolve(p), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A mods root with the mods a test adds, and Penumbra resolving what they publish by default.</summary>
    private sealed class Setup : IDisposable
    {
        public readonly string ModsRoot = Path.Combine(Path.GetTempPath(), "proteus_scan_" + Path.GetRandomFileName());
        public readonly Host Host;
        public readonly HairPortScan.Memo Memo = new();

        public Setup() { Directory.CreateDirectory(ModsRoot); Host = new Host(ModsRoot); }

        /// <summary>A mod publishing Elezen models of <paramref name="ids"/> from its default data, resolved.</summary>
        public string DefaultDataMod(string name, params int[] ids)
        {
            var root = Path.Combine(ModsRoot, name);
            var files = ids.ToDictionary(id => HairPortPlan.HairPath(Elezen, id), id => $"default group/h{id:D4}.mdl");
            WriteMod(root, name, "[]", files);
            foreach (var id in ids)
            {
                Put(root, $"default group/h{id:D4}.mdl", HairModelOf(Elezen, id));
                Host.Files[HairPortPlan.HairPath(Elezen, id)] = Path.Combine(root, "default group", $"h{id:D4}.mdl");
            }
            return root;
        }

        public static void WriteMod(string root, string name, string groups, Dictionary<string, string> defaults)
        {
            Directory.CreateDirectory(root);
            var dd = JsonSerializer.Serialize(new { Files = defaults, FileSwaps = new Dictionary<string, string>(),
                                                    Manipulations = Array.Empty<object>() });
            File.WriteAllText(Path.Combine(root, "meta.json"),
                $"{{\"FileVersion\":4,\"Name\":\"{name}\",\"Groups\":{groups},\"DefaultData\":{dd}}}");
        }

        public static void Put(string root, string rel, byte[] bytes)
        {
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        /// <summary>Penumbra after reloading <paramref name="root"/>: the player's race resolves to what it publishes.</summary>
        public void Reloaded(string root, params int[] ids)
        {
            var defaults = HairPortPlan.ReadOwners(root)!.First(o => o.IsDefault);
            foreach (var id in ids)
                if (HairPortPlan.FileFor(defaults, HairPortPlan.HairPath(Me, id)) is { } rel)
                    Host.Files[HairPortPlan.HairPath(Me, id)] = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        public HairPortScan.Result Run(IReadOnlyList<int> ids, bool force = false, CancellationToken ct = default,
                                       Action<int, int>? progress = null)
            => HairPortScan.Run(Host, Me, ids, Host.Worn, Memo, force, "test", ct, progress);

        public void Dispose() { try { Directory.Delete(ModsRoot, true); } catch { } }
    }

    private static string Target(int id) => HairPortPlan.HairPath(Me, id);

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-1")]
    [Trait("Plan", "EDGE-hair-port-scan-1")]
    public void Full_check_fits_default_data_elezen_hair_to_midlander_unworn()
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Genesis Floof", 7);
        s.Host.Worn = Target(5);

        var result = s.Run(HairPortScan.IdsForFull(Me, s.Host.GameHas));

        var row = Assert.Single(result.Rows);
        Assert.Equal((7, HairPortScan.RowState.Fitted, "Genesis Floof"), (row.Id, row.State, row.ModDir));
        var defaults = HairPortPlan.ReadOwners(root)!.First(o => o.IsDefault);
        var rel = HairPortPlan.FileFor(defaults, Target(7));
        Assert.NotNull(rel);
        Assert.True(HairPortPlan.IsPortFile(rel!));
        Assert.True(File.Exists(Path.Combine(root, rel!)));
        // The game's Elezen material, copied: a Midlander's hairstyle 7 reads its own path.
        var mtrl = HairMaterialPath.GamePath(Me, 7, "/mt_c0201h0007_hir_a.mtrl")!;
        Assert.NotNull(HairPortPlan.FileFor(defaults, mtrl));
        Assert.True(HairPortRecord.Read(root).Ports.Single().Auto);
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-3")]
    public void Mod_check_examines_only_that_mods_ids()
    {
        using var s = new Setup();
        var a = s.DefaultDataMod("A", 5);
        s.DefaultDataMod("B", 6);

        var ids = HairPortScan.IdsInMod(HairPortPlan.ReadOwners(a), Me);
        Assert.Equal([5], ids);
        var result = s.Run(ids);

        Assert.Equal([5], result.Rows.Select(r => r.Id));
        Assert.All(s.Host.Resolved, p => Assert.Equal(5, HairPortPlan.Parse(p)!.Value.Id));
        Assert.Null(HairPortPlan.FileFor(HairPortPlan.ReadOwners(Path.Combine(s.ModsRoot, "B"))!.First(o => o.IsDefault), Target(6)));
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-5")]
    [Trait("Plan", "EDGE-hair-port-scan-14")]
    [Trait("Plan", "EDGE-hair-port-scan-30")]
    public void Cancel_stops_before_next_id_and_reloads_saved_mods()
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Pack", 5, 6, 7);
        using var cts = new CancellationTokenSource();

        var result = s.Run([5, 6, 7], ct: cts.Token, progress: (done, _) => { if (done == 1) cts.Cancel(); });

        Assert.True(result.Cancelled);
        Assert.Equal([5], result.Rows.Select(r => r.Id));
        Assert.Equal(1, s.Host.Calls.Count(c => c == "reload Pack"));
        var defaults = HairPortPlan.ReadOwners(root)!.First(o => o.IsDefault);
        Assert.NotNull(HairPortPlan.FileFor(defaults, Target(5)));
        Assert.Null(HairPortPlan.FileFor(defaults, Target(6)));
        Assert.Null(HairPortPlan.FileFor(defaults, Target(7)));
        // Nothing half-written: every port file is one the manifest names.
        var named = HairPortRecord.Read(root).Ports.SelectMany(e => e.Materials.Values.Append(e.File)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, HairPortPlan.Folder), "*", SearchOption.AllDirectories))
            Assert.Contains(Path.GetRelativePath(root, f).Replace('\\', '/'), named);
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-6")]
    [Trait("Plan", "EDGE-hair-port-scan-23")]
    public void One_summary_line_names_numbers_and_mods()
    {
        using var s = new Setup();
        s.DefaultDataMod("Floofs", 5, 7);
        s.Run([5, 7]);
        var summary = Assert.Single(s.Host.Calls, c => c.StartsWith("summary ", StringComparison.Ordinal));
        Assert.Contains("hairstyle 7", summary);
        Assert.Contains("Floofs (name)", summary);

        s.Host.Calls.Clear();
        s.Run([9]);   // no mod covers it
        Assert.DoesNotContain(s.Host.Calls, c => c.StartsWith("summary ", StringComparison.Ordinal));

        var five = Enumerable.Range(1, 5).Select(i => new HairPortScan.Row(i, "r", "d", "Mod", Target(i), HairPortScan.RowState.Fitted)).ToList();
        var line = HairPortScan.SummaryLine(five);
        Assert.Contains("hairstyle 3", line);
        Assert.DoesNotContain("hairstyle 4", line);
        Assert.Contains("2 more", line);
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-7")]
    [Trait("Plan", "EDGE-hair-port-scan-25")]
    public void Row_undo_declines_and_next_check_skips()
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Floof", 7);
        var rows = s.Run([7]).Rows;

        // "Undo everything this check fitted" and a row's Undo take the same path: per mod, the rows' targets.
        foreach (var (modRoot, which) in HairPortWatcher.Decide.CheckedUndos(rows))
            Assert.True(HairPortWriter.Undo(modRoot, which, decline: true).Ok);
        Assert.Contains(HairPortPlan.DeclineKey(Target(7)), HairPortRecord.Read(root).Declined);
        Assert.Equal(HairPortScan.RowState.Undone,
                     HairPortWatcher.Decide.MarkUndone(rows, [Target(7)]).Single().State);

        s.Host.Calls.Clear();
        var again = s.Run([7], force: true);
        Assert.Equal(HairPortScan.RowState.Undone, again.Rows.Single().State);
        Assert.Empty(s.Host.Calls);
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-8")]
    [Trait("Plan", "EDGE-hair-port-scan-7")]
    public void Declined_is_not_refitted_by_check()
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Floof", 7);
        s.Run([7]);
        HairPortWriter.Undo(root, _ => true, decline: true);
        var manifest = File.ReadAllText(Path.Combine(root, "meta.json"));

        s.Run([7], force: true);
        Assert.Equal(manifest, File.ReadAllText(Path.Combine(root, "meta.json")));
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-8")]
    public void Current_port_is_not_rewritten()
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Floof", 7);
        s.Run([7]);
        s.Reloaded(root, 7);
        var port = s.Host.Files[Target(7)];
        var written = File.GetLastWriteTimeUtc(port);
        var manifest = File.ReadAllText(Path.Combine(root, "meta.json"));

        var again = s.Run([7], force: true);
        Assert.Equal(HairPortScan.RowState.AlreadyFitted, again.Rows.Single().State);
        Assert.Equal(written, File.GetLastWriteTimeUtc(port));
        Assert.Equal(manifest, File.ReadAllText(Path.Combine(root, "meta.json")));
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-8")]
    [Trait("Plan", "EDGE-hair-port-scan-8")]
    public void Stale_port_is_remade()
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Floof", 7);
        s.Run([7]);
        s.Reloaded(root, 7);
        var changed = HairModelOf(Elezen, 7, radius: 0.101f);
        Setup.Put(root, "default group/h0007.mdl", changed);

        var again = s.Run([7], force: true);
        Assert.Equal(HairPortScan.RowState.Remade, again.Rows.Single().State);
        Assert.Equal(HairPortPlan.Sha8(changed), HairPortRecord.Read(root).Ports.Single().SourceSha8);
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-8")]
    [Trait("Plan", "EDGE-hair-port-scan-9")]
    public void Edited_stale_port_is_refused_with_reason()
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Floof", 7);
        s.Run([7]);
        s.Reloaded(root, 7);
        var file = HairPortRecord.Read(root).Ports.Single().File;
        Setup.Put(root, $"Proteus/{MeshVolumeService.BackupSubdir}/{file}", [1]);
        Setup.Put(root, "default group/h0007.mdl", HairModelOf(Elezen, 7, radius: 0.101f));

        var row = s.Run([7], force: true).Rows.Single();
        Assert.Equal(HairPortScan.RowState.Refused, row.State);
        Assert.Contains(ModelBackupOrder.BlockedMessage("").Split(' ')[0], row.Message);
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-8")]
    [Trait("Plan", "EDGE-hair-port-scan-10")]
    public void Refusal_memo_skips_rebend_until_source_changes()
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Floof", 7);
        // A bone the racial deformer cannot place: the bend refuses it.
        Setup.Put(root, "default group/h0007.mdl", HairModelOf(Elezen, 7, bones: [("j_unknown_bone", 1f)]));

        Assert.Equal(HairPortScan.RowState.Refused, s.Run([7]).Rows.Single().State);
        s.Host.GameReads = 0;
        var again = s.Run([7], force: true);
        Assert.Equal(HairPortScan.RowState.Refused, again.Rows.Single().State);
        Assert.Equal(0, s.Host.GameReads);   // nothing bent

        Setup.Put(root, "default group/h0007.mdl", HairModelOf(Elezen, 7, radius: 0.101f));
        File.SetLastWriteTimeUtc(Path.Combine(root, "default group", "h0007.mdl"), DateTime.UtcNow.AddMinutes(1));
        s.Run([7], force: true);
        Assert.True(s.Host.GameReads > 0);
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-9")]
    [Trait("Plan", "EDGE-hair-port-scan-20")]
    public void Unworn_fit_reloads_without_redraw()
    {
        using var s = new Setup();
        s.DefaultDataMod("Floof", 7);
        s.Host.Worn = Target(5);
        s.Run([7]);
        Assert.Equal("reload Floof", s.Host.Calls[0]);
        Assert.DoesNotContain("redraw", s.Host.Calls);
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-9")]
    public void Worn_fit_redraws_once()
    {
        using var s = new Setup();
        s.DefaultDataMod("Floof", 6, 7);
        s.Host.Worn = Target(7);
        s.Run([6, 7]);
        Assert.Equal(1, s.Host.Calls.Count(c => c == "redraw"));
        Assert.True(s.Host.Calls.IndexOf("reload Floof") < s.Host.Calls.IndexOf("redraw"));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-21")]
    public void Worn_changed_during_check_no_redraw()
    {
        using var s = new Setup();
        s.DefaultDataMod("Floof", 7);
        s.Host.Worn = Target(7);
        s.Host.WornAfter = Target(5);
        s.Run([7]);
        Assert.DoesNotContain("redraw", s.Host.Calls);
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-10")]
    [Trait("Plan", "EDGE-hair-port-scan-19")]
    public void Check_runs_worn_id_first()
    {
        using var s = new Setup();
        s.DefaultDataMod("Pack", 5, 6, 7);
        s.Host.Worn = Target(7);
        var result = s.Run([5, 6, 7]);
        Assert.Equal(7, result.Rows[0].Id);
        Assert.Equal(7, HairPortPlan.Parse(s.Host.Lines.First(l => l.Contains(" → ")).Split(" → ")[1].Split(' ')[0])!.Value.Id);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-2")]
    public void Own_race_already_authored_writes_nothing_no_row()
    {
        using var s = new Setup();
        // The author's Midlander model, in the default data and resolved.
        var root = s.DefaultDataMod("Both", 7);
        var files = new Dictionary<string, string>
        {
            [HairPortPlan.HairPath(Elezen, 7)] = "default group/h0007.mdl",
            [Target(7)] = "default group/mine.mdl",
        };
        Setup.WriteMod(root, "Both", "[]", files);
        Setup.Put(root, "default group/mine.mdl", HairModelOf(Me, 7));
        s.Host.Files[Target(7)] = Path.Combine(root, "default group", "mine.mdl");
        // And in an option the player has off: the Midlander path resolves to the game's, but the author covers it.
        var other = Path.Combine(s.ModsRoot, "Option");
        Setup.WriteMod(other, "Option", "[" + Group("Cut", "Single", ("Elezen", new() { [HairPortPlan.HairPath(Elezen, 6)] = "e.mdl" }),
                                                    ("Mid", new() { [Target(6)] = "m.mdl" })) + "]", new());
        Setup.Put(other, "e.mdl", HairModelOf(Elezen, 6));
        Setup.Put(other, "m.mdl", HairModelOf(Me, 6));
        s.Host.Files[HairPortPlan.HairPath(Elezen, 6)] = Path.Combine(other, "e.mdl");
        var manifests = new[] { root, other }.Select(r => File.ReadAllText(Path.Combine(r, "meta.json"))).ToList();

        var result = s.Run([6, 7]);
        Assert.Empty(result.Rows);
        Assert.Equal(manifests, new[] { root, other }.Select(r => File.ReadAllText(Path.Combine(r, "meta.json"))));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-3")]
    public void Unselected_option_not_ported_until_resolved()
    {
        using var s = new Setup();
        var root = Path.Combine(s.ModsRoot, "Cuts");
        Setup.WriteMod(root, "Cuts", "[" + Group("Cut", "Single", ("Short", new() { [HairPortPlan.HairPath(Elezen, 7)] = "short.mdl" }),
                                                  ("Long", new() { [HairPortPlan.HairPath(Elezen, 7)] = "long.mdl" })) + "]", new());
        Setup.Put(root, "short.mdl", HairModelOf(Elezen, 7));
        Setup.Put(root, "long.mdl", HairModelOf(Elezen, 7, radius: 0.101f));

        Assert.Empty(s.Run([7]).Rows);   // switched off in the collection: nothing resolves
        Assert.False(Directory.Exists(Path.Combine(root, HairPortPlan.Folder)));

        s.Host.Files[HairPortPlan.HairPath(Elezen, 7)] = Path.Combine(root, "long.mdl");
        Assert.Equal(HairPortScan.RowState.Fitted, s.Run([7], force: true).Rows.Single().State);
        var owners = HairPortPlan.ReadOwners(root)!;
        Assert.NotNull(HairPortPlan.FileFor(owners.First(o => o.Option == "Long"), Target(7)));
        Assert.NotNull(HairPortPlan.FileFor(owners.First(o => o.Option == "Short"), Target(7)));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-5")]
    public void Higher_priority_mod_is_the_source()
    {
        using var s = new Setup();
        var a = s.DefaultDataMod("A", 7);
        var b = s.DefaultDataMod("B", 7);   // resolved last: wins
        var aManifest = File.ReadAllText(Path.Combine(a, "meta.json"));

        var row = s.Run([7]).Rows.Single();
        Assert.Equal("B", row.ModDir);
        Assert.Equal(aManifest, File.ReadAllText(Path.Combine(a, "meta.json")));
        Assert.NotNull(HairPortPlan.FileFor(HairPortPlan.ReadOwners(b)!.First(o => o.IsDefault), Target(7)));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-31")]
    public void Newer_winning_mod_of_the_same_race_is_ported_too()
    {
        using var s = new Setup();
        var a = s.DefaultDataMod("A", 7);
        s.Run([7]);
        s.Reloaded(a, 7);
        // B installed and switched on since, winning the Elezen model: A's port still wins the Midlander path.
        var b = s.DefaultDataMod("B", 7);

        var row = s.Run([7], force: true).Rows.Single();
        Assert.Equal(("B", HairPortScan.RowState.Fitted), (row.ModDir, row.State));
        Assert.NotNull(HairPortPlan.FileFor(HairPortPlan.ReadOwners(b)!.First(o => o.IsDefault), Target(7)));
        // A's stays, for when B is off again.
        Assert.NotNull(HairPortPlan.FileFor(HairPortPlan.ReadOwners(a)!.First(o => o.IsDefault), Target(7)));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-6")]
    public void Full_ids_only_where_game_has_wearer_model()
    {
        var game = GameOf([Me], [5, 7]);
        Assert.Equal([5, 7], HairPortScan.IdsForFull(Me, p => game(p) != null));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-6")]
    public void Mod_check_of_unavailable_id_is_refused_row()
    {
        using var s = new Setup();
        s.DefaultDataMod("Odd", 42);   // the game has no hairstyle 42 for anyone here
        s.Host.Game = GameOf(Races.Where(r => r != Me), [42]);
        var row = s.Run([42]).Rows.Single();
        Assert.Equal(HairPortScan.RowState.Refused, row.State);
        Assert.Contains("42", row.Message);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-15")]
    public void Cancelled_by_dispose_does_not_announce()
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Floof", 7);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = s.Run([7], ct: cts.Token);
        Assert.True(result.Cancelled);
        Assert.Empty(s.Host.Calls);
        Assert.False(Directory.Exists(Path.Combine(root, HairPortPlan.Folder)));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-22")]
    public void Pack_with_many_ids_reloads_once()
    {
        using var s = new Setup();
        s.DefaultDataMod("Pack", 5, 6, 7);
        var result = s.Run([5, 6, 7]);
        Assert.Equal(3, result.Rows.Count(r => r.State == HairPortScan.RowState.Fitted));
        Assert.Equal(1, s.Host.Calls.Count(c => c.StartsWith("reload ", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-24")]
    public void No_mods_root_does_nothing()
    {
        var host = new Host("");
        var result = HairPortScan.Run(host, Me, [7], null, new HairPortScan.Memo(), false, "test", default);
        Assert.Empty(result.Rows);
        Assert.Empty(host.Calls);
        Assert.Empty(host.Resolved);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-26")]
    public void Rows_without_mod_are_dropped()
    {
        var rows = new List<HairPortScan.Row>
        {
            new(5, "r1", "Gone", "Gone", Target(5), HairPortScan.RowState.Fitted),
            new(7, "r2", "Kept", "Kept", Target(7), HairPortScan.RowState.Fitted),
        };
        Assert.Equal(["Kept"], HairPortWatcher.Decide.DropMod(rows, "gone").Select(r => r.ModDir));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-28")]
    public void Miqote_sharers_ported_together_row_only_for_wearer()
    {
        using var s = new Setup();
        var root = Path.Combine(s.ModsRoot, "Miqo");
        var source = HairPortPlan.HairPath(801, 105);
        Setup.WriteMod(root, "Miqo", "[]", new() { [source] = "m.mdl" });
        Setup.Put(root, "m.mdl", HairModelOf(801, 105));
        s.Host.Files[source] = Path.Combine(root, "m.mdl");

        var result = s.Run([105]);
        var row = Assert.Single(result.Rows);
        Assert.Equal((105, HairPortScan.RowState.Fitted), (row.Id, row.State));
        Assert.True(result.Written.Count > 1, "the other races reading the same material were ported with it");
        Assert.Contains(Target(105), result.Written);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-29")]
    public void Log_and_summary_carry_no_full_path()
    {
        using var s = new Setup();
        s.DefaultDataMod("Floof", 7);
        s.Run([7]);
        Assert.NotEmpty(s.Host.Lines);
        Assert.All(s.Host.Lines.Concat(s.Host.Calls), l => Assert.DoesNotContain(s.ModsRoot, l, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-1")]
    public void Unchanged_resolution_is_skipped_unless_forced()
    {
        using var s = new Setup();
        s.DefaultDataMod("Floof", 9);
        s.Run([7, 9]);
        var again = s.Run([7, 9]);
        Assert.Empty(again.Examined);
        Assert.Equal([7, 9], s.Run([7, 9], force: true).Examined.OrderBy(i => i));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-27")]
    [Trait("Plan", "EDGE-hair-port-scan-10")]
    public void Mod_check_sees_a_model_replaced_in_place()
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Floof", 7);
        s.Run([7]);
        s.Reloaded(root, 7);
        Setup.Put(root, "default group/h0007.mdl", HairModelOf(Elezen, 7, radius: 0.101f));
        // The mod's Edited owes a check of that mod, which looks again though nothing resolves differently.
        var work = new HairPortScanSchedule.Work(Me, Full: false, ["Floof"], Force: false);
        Assert.True(work.LooksAgain);
        Assert.Equal(HairPortScan.RowState.Remade, s.Run([7], force: work.LooksAgain).Rows.Single().State);
        // An unasked full check does skip what resolves as before.
        Assert.False(new HairPortScanSchedule.Work(Me, Full: true, [], Force: false).LooksAgain);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-24")]
    public void Penumbra_unreachable_is_a_failed_check_not_an_empty_one()
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Floof", 7);
        s.Host.ResolveFails = true;
        var result = s.Run([7]);
        Assert.True(result.Failed);
        Assert.Empty(result.Examined);
        Assert.Empty(s.Host.Calls);
        // Nothing remembered: the same check once Penumbra answers fits it.
        s.Host.ResolveFails = false;
        Assert.Equal(HairPortScan.RowState.Fitted, s.Run([7]).Rows.Single().State);
        Assert.True(Directory.Exists(Path.Combine(root, HairPortPlan.Folder)));
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-scan-5")]
    public void An_unreadable_model_refuses_its_hairstyle_and_the_check_goes_on()
    {
        using var s = new Setup();
        var bad = s.DefaultDataMod("Broken", 5);
        Setup.Put(bad, "default group/h0005.mdl", [1, 2, 3, 4, 5, 6, 7, 8]);
        s.DefaultDataMod("Floof", 7);

        var result = s.Run([5, 7]);
        Assert.Equal(HairPortScan.RowState.Refused, result.Rows.Single(r => r.Id == 5).State);
        Assert.Equal(HairPortScan.RowState.Fitted, result.Rows.Single(r => r.Id == 7).State);
        Assert.Contains("reload Floof", s.Host.Calls);
        Assert.DoesNotContain("reload Broken", s.Host.Calls);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-31")]
    public void A_newer_mod_for_another_race_leaves_the_port_that_wins()
    {
        using var s = new Setup();
        // A: a Miqo'te model, fitted to Midlanders.
        var a = Path.Combine(s.ModsRoot, "A");
        var miqo = HairPortPlan.HairPath(801, 7);
        Setup.WriteMod(a, "A", "[]", new() { [miqo] = "m.mdl" });
        Setup.Put(a, "m.mdl", HairModelOf(801, 7));
        s.Host.Files[miqo] = Path.Combine(a, "m.mdl");
        Assert.Equal(HairPortScan.RowState.Fitted, s.Run([7]).Rows.Single().State);
        s.Reloaded(a, 7);
        // B: an Elezen model, nearer to Midlanders — but its port would lose to A's, which wins the Midlander path.
        var b = s.DefaultDataMod("B", 7);

        var row = s.Run([7], force: true).Rows.Single();
        Assert.Equal(("A", HairPortScan.RowState.AlreadyFitted), (row.ModDir, row.State));
        Assert.Null(HairPortPlan.FileFor(HairPortPlan.ReadOwners(b)!.First(o => o.IsDefault), Target(7)));
    }

    // A failure that may not happen next time is neither remembered as a refusal nor counts the hairstyle seen: the
    // next check — not only one asked for — looks again.

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-10")]
    public void A_manifest_that_could_not_be_read_is_tried_again_by_the_next_check()
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Floof", 7);
        // Held open elsewhere (Penumbra, an antivirus scan), not even readable.
        using (new FileStream(Path.Combine(root, "meta.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(HairPortScan.RowState.Refused, s.Run([7]).Rows.Single().State);

        Assert.Equal(HairPortScan.RowState.Fitted, s.Run([7]).Rows.Single().State);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-10")]
    public void A_save_that_failed_is_tried_again_by_the_next_check()
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Floof", 7);
        // Readable, so the port is bent, but not writable: the save fails.
        using (new FileStream(Path.Combine(root, "meta.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(HairPortScan.RowState.Refused, s.Run([7]).Rows.Single().State);
            Assert.Contains(s.Host.Lines, l => l.Contains("was not saved"));
        }

        Assert.Equal(HairPortScan.RowState.Fitted, s.Run([7]).Rows.Single().State);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-10")]
    public void A_model_that_could_not_be_read_is_tried_again_by_the_next_check()
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Floof", 7);
        using (new FileStream(Path.Combine(root, "default group", "h0007.mdl"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(HairPortScan.RowState.Refused, s.Run([7]).Rows.Single().State);

        Assert.Equal(HairPortScan.RowState.Fitted, s.Run([7]).Rows.Single().State);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-scan-10")]
    public void A_port_that_threw_is_tried_again_by_the_next_check()
    {
        using var s = new Setup();
        var bad = s.DefaultDataMod("Broken", 5);
        Setup.Put(bad, "default group/h0005.mdl", [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Equal(HairPortScan.RowState.Refused, s.Run([5]).Rows.Single().State);
        Assert.Equal(HairPortScan.RowState.Refused, s.Run([5]).Rows.Single().State);
        Assert.Equal(2, s.Host.Lines.Count(l => l.Contains("in Broken not ported")));
    }

    /// <summary>
    /// A port made with something to look at in game — a bend that turned part of it inside out — is fitted, its row
    /// says what in the warning colour, and nothing remembers it as a refusal (cross-sex-tops-yas).
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-8")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-23")]
    public void A_warned_port_is_fitted_with_its_warning() // [AC-cross-sex-tops-yas-8] [EDGE-cross-sex-tops-yas-23]
    {
        const string warning = "bent onto c1101, 40 of its triangles turn inside out and may show as holes; check it in game";
        var warned = new HairPortScan.Row(5, "root", "Mod", "Mod", "chara/human/c1101/obj/hair/h0005/model/c1101h0005_hir.mdl",
                                          HairPortScan.RowState.Fitted, warning);
        Assert.True(Proteus.Gui.HairPortSettingsPanel.Warned(warned));
        Assert.Contains(warning, Proteus.Gui.HairPortSettingsPanel.RowText(warned));
        Assert.Contains("fitted", Proteus.Gui.HairPortSettingsPanel.RowText(warned));
        Assert.False(Proteus.Gui.HairPortSettingsPanel.Warned(warned with { Message = "" }));
        Assert.False(Proteus.Gui.HairPortSettingsPanel.Warned(warned with { State = HairPortScan.RowState.Refused }));

        string scan = Source("Proteus/Services/HairPort/HairPortScan.cs");
        Assert.Contains("MakeRow(host, id, modRoot, target, d.Stale ? RowState.Remade : RowState.Fitted, outcome.Warning)", scan);
        Assert.Contains("return new Ported(saved, false, \"\", Warning: warning);", scan);   // not a refusal
        string builder = Source("Proteus/Services/HairPort/HairPortBuilder.cs");
        Assert.Contains("foreach (var to in refused.Keys) warnings.Remove(to);", builder);
        string settings = Source("Proteus/Gui/HairPortSettingsPanel.cs");
        Assert.Contains("if (view.Warning.Length > 0)", settings);
        Assert.Contains("Warning: ported.Warning", Source("Proteus/Services/HairPort/HairPortWatcher.cs"));
        string studio = Source("Proteus/Gui/HairPortPanel.cs");
        Assert.Contains("resultWarn = true;", studio);
    }

    /// <summary>
    /// A warned port is said again for the rest of the session: the next look finds the hairstyle already fitted, and its
    /// row and the worn line still carry the warning (once lost at the first look after the port).
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-8")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-23")]
    public void A_warned_port_is_said_again_when_found_already_fitted() // [AC-cross-sex-tops-yas-8] [EDGE-cross-sex-tops-yas-23]
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Floof", 7);
        // A stale warning: the port below warns of nothing, and PortOne clears it under the key the scan reads.
        HairPortScan.RememberWarning(root, Target(7), "stale");
        Assert.Equal(HairPortScan.RowState.Fitted, s.Run([7]).Rows.Single().State);
        s.Reloaded(root, 7);
        var first = s.Run([7], force: true).Rows.Single();
        Assert.Equal((HairPortScan.RowState.AlreadyFitted, ""), (first.State, first.Message));
        // What PortOne remembers for a port it made with a warning.
        const string warning = "bent onto c0101, 40 of its triangles turn inside out and may show as holes; check it in game";
        HairPortScan.RememberWarning(root, Target(7), warning);

        var again = s.Run([7], force: true).Rows.Single();
        Assert.Equal((HairPortScan.RowState.AlreadyFitted, warning), (again.State, again.Message));
        Assert.True(Proteus.Gui.HairPortSettingsPanel.Warned(again));
        Assert.Contains(warning, Proteus.Gui.HairPortSettingsPanel.RowText(again));
        Assert.Contains("Warning: HairPortScan.WarningFor(decision.ModRoot, worn)",
                        Source("Proteus/Services/HairPort/HairPortWatcher.cs"));
        Assert.Contains("RememberSaved(modRoot, saved, plan.Warnings);",
                        Source("Proteus/Services/HairPort/HairPortScan.cs"));

        // Ported again without one, it is forgotten.
        HairPortScan.RememberWarning(root, Target(7), "");
        Assert.Equal("", s.Run([7], force: true).Rows.Single().Message);
    }

    /// <summary>
    /// Every port a save wrote, sharers included, remembers what it warned of — and one that warned of nothing forgets
    /// an earlier port's warning; a race not written, or a port that only took an old skeleton out, keeps what it had.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-8")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-23")]
    public void Saved_ports_remember_their_warnings_sharers_too() // [AC-cross-sex-tops-yas-8] [EDGE-cross-sex-tops-yas-23]
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus_warn_" + Path.GetRandomFileName());
        var owner = new HairPortPlan.Owner("Cut", "Long", "", new Dictionary<string, string>());
        HairPortWriter.PortWrite Write(ushort race, bool stripped = false)
            => new(owner, "src", "src.mdl", "00000000", HairPortPlan.HairPath(race, 7), [], [], Stripped: stripped);
        foreach (ushort race in new ushort[] { 101, 201, 301, 401 })
            HairPortScan.RememberWarning(root, HairPortPlan.HairPath(race, 7), "stale");

        HairPortScan.RememberSaved(root, [Write(101), Write(201), Write(401, stripped: true)],
                                   new Dictionary<ushort, string> { [201] = "sharer warned" });

        Assert.Equal("", HairPortScan.WarningFor(root, HairPortPlan.HairPath(101, 7)));             // ported clean: forgotten
        Assert.Equal("sharer warned", HairPortScan.WarningFor(root, HairPortPlan.HairPath(201, 7))); // a sharer's own
        Assert.Equal("stale", HairPortScan.WarningFor(root, HairPortPlan.HairPath(301, 7)));        // not written: kept
        Assert.Equal("stale", HairPortScan.WarningFor(root, HairPortPlan.HairPath(401, 7)));        // only stripped: kept
        Assert.Contains("HairPortScan.RememberSaved(modRoot, outcome.SavedWrites, plan.Warnings);",
                        Source("Proteus/Gui/HairPortPanel.cs"));
    }

    /// <summary>
    /// A port current in the mod but not yet served (Penumbra has not reloaded it) is found already fitted by the bend
    /// itself, not by the look — and still says its warning.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-8")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-23")]
    public void A_warned_port_not_yet_reloaded_says_its_warning() // [AC-cross-sex-tops-yas-8] [EDGE-cross-sex-tops-yas-23]
    {
        using var s = new Setup();
        var root = s.DefaultDataMod("Floof", 7);
        Assert.Equal(HairPortScan.RowState.Fitted, s.Run([7]).Rows.Single().State);
        const string warning = "bent onto c0101, 40 of its triangles turn inside out and may show as holes; check it in game";
        HairPortScan.RememberWarning(root, Target(7), warning);

        var row = s.Run([7], force: true).Rows.Single();   // no Reloaded: the game still serves the Elezen model
        Assert.Equal((HairPortScan.RowState.AlreadyFitted, warning), (row.State, row.Message));
    }

    private static string Source(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, file);
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        throw new FileNotFoundException(file);
    }
}
