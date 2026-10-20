using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using XivLiveMesh;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>
/// "Take the ears off Miqo'te hair mods" on the automatic path: what a Miqo'te wearing a hair mod with cat ears gets,
/// and how the ears come back — through <see cref="HairPortWatcher.Run"/> and the restore pass with Penumbra and the
/// game stood in for.
/// </summary>
public class EarlessHairWatcherTests
{
    private static readonly string Worn = HairPortPlan.HairPath(801, Id);
    private static readonly ushort[] Races = [101, 201, 401, 601, 801, 1001, 1101, 1201, 1401, 1601, 1801];

    private sealed class Host(string modsRoot) : HairPortWatcher.IHost
    {
        public readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Calls = [];
        public readonly List<string> Lines = [];
        public readonly List<string> Resolved = [];
        public bool Enabled { get; set; }
        public bool Earless { get; set; } = true;
        public string? WornAfter { get; set; }
        public string WornPath { get; set; } = Worn;

        public string? ModsRoot => modsRoot;
        public string? Resolve(string gamePath)
        {
            Resolved.Add(gamePath);
            return Files.GetValueOrDefault(gamePath);
        }
        public PbdFile? Pbd() => HairPortFixtures.Pbd();
        public byte[]? ReadGame(string gamePath) => Game(Races)(gamePath);
        public void Log(string message) => Lines.Add(message);
        public void Announce() => Calls.Add("announce");
        public void Reload(string modDir) => Calls.Add("reload " + modDir);
        public string? WornNow() => WornAfter ?? WornPath;
        public void Redraw() => Calls.Add("redraw");
        public void CheckMod(string modDir) => Calls.Add("check " + modDir);
        public void AnnounceEarless(string line) { Calls.Add("announce-earless"); Lines.Add(line); }
        public void RequestLookAgain() => Calls.Add("look-again");
        public void EarlessSettled() { }
    }

    /// <summary>A mods root with Miqo'te hair mods, each one option "Cut / Long" serving <see cref="Worn"/> (or the
    /// path given) from an eared long.mdl, and Penumbra serving the first.</summary>
    private sealed class Setup : IDisposable
    {
        public readonly string ModsRoot = Path.Combine(Path.GetTempPath(), "proteus_mods_" + Path.GetRandomFileName());
        public readonly Host Host;
        public string ModRoot => Path.Combine(ModsRoot, "Miqo Hair");

        public Setup(string? path = null, byte[]? model = null)
        {
            Host = new Host(ModsRoot);
            AddMod("Miqo Hair", path ?? Worn, model ?? EaredHairModel(801));
            Host.Files[path ?? Worn] = Path.Combine(ModRoot, "long.mdl");
        }

        public string AddMod(string name, string path, byte[] model, string? groups = null)
        {
            var root = Path.Combine(ModsRoot, name);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "meta.json"),
                "{\"FileVersion\":4,\"Name\":\"" + name + "\",\"Groups\":["
                + (groups ?? Group("Cut", "Single", ("Long", new() { [path] = "long.mdl" })))
                + "],\"DefaultData\":{\"Files\":{},\"FileSwaps\":{},\"Manipulations\":[]}}");
            File.WriteAllBytes(Path.Combine(root, "long.mdl"), model);
            return root;
        }

        public string? Serves(string? modRoot = null, string? path = null)
            => HairPortPlan.FileFor(HairPortPlan.ReadOwners(modRoot ?? ModRoot)!.First(o => o.Option == "Long"), path ?? Worn);

        /// <summary>Penumbra serving what the manifest now says, as after a reload.</summary>
        public void Reloaded() => Host.Files[Worn] = Path.Combine(ModRoot, Serves()!.Replace('/', Path.DirectorySeparatorChar));

        public HairPortWatcher.View Run(string? key = null) => HairPortWatcher.Run(Host, Host.WornPath, ref key);

        public void Dispose() { try { Directory.Delete(ModsRoot, true); } catch { } }
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-1")]
    public void Gates_truth_table()
    {
        foreach (bool plugin in new[] { false, true })
        foreach (bool penumbra in new[] { false, true })
        foreach (bool port in new[] { false, true })
        foreach (bool earless in new[] { false, true })
        {
            var g = HairPortWatcher.Decide.Gates(plugin, penumbra, port, earless);
            Assert.Equal(plugin && penumbra, g.Watching);
            Assert.Equal(plugin && penumbra && port, g.Porting);
            Assert.Equal(plugin && penumbra && earless, g.Earless);
        }
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-1")]
    public void Earless_works_with_hair_fitting_off()
    {
        using var s = new Setup();
        s.Host.Enabled = false;
        var view = s.Run();
        Assert.True(view.Earless, view.Message);
        Assert.Equal("Proteus Earless/long.mdl", s.Serves());
    }

    [Theory]
    [Trait("Plan", "AC-earless-miqote-hair-2")]
    [InlineData((ushort)801)]
    [InlineData((ushort)701)]
    public void Miqote_wearing_eared_mod_hair_gets_an_earless_copy(ushort race)
    {
        var path = HairPortPlan.HairPath(race, Id);
        var model = EaredHairModel(race == 701 ? (ushort)801 : race);
        using var s = new Setup(path, model);
        s.Host.WornPath = path;
        s.Host.Enabled = true;
        var view = s.Run();
        Assert.True(view.Earless, view.Message);
        Assert.Equal("Proteus Earless/long.mdl", s.Serves(path: path));
        Assert.Equal(0, EarlessHair.DrawnFur(File.ReadAllBytes(Path.Combine(s.ModRoot, "Proteus Earless", "long.mdl"))));
        Assert.Equal(model, File.ReadAllBytes(Path.Combine(s.ModRoot, "long.mdl")));
        Assert.Equal(["announce-earless", "reload Miqo Hair", "redraw"], s.Host.Calls);
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-4")]
    public void Turning_on_acts_on_what_is_worn_now()
    {
        using var s = new Setup();
        s.Host.Earless = false;
        string? key = null;
        HairPortWatcher.Run(s.Host, Worn, ref key);
        Assert.Equal("long.mdl", s.Serves());
        // On: the setting looks afresh (a forced refresh drops the key).
        s.Host.Earless = true;
        key = null;
        var view = HairPortWatcher.Run(s.Host, Worn, ref key);
        Assert.True(view.Earless);
        Assert.Equal(1, s.Host.Calls.Count(c => c == "announce-earless"));
        Assert.Contains(HairPortWatcher.ChatLine, s.Host.Lines);
        Assert.Contains("every collection", HairPortWatcher.ChatLine);
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-4")]
    public void Second_look_at_an_earless_copy_says_nothing_more()
    {
        using var s = new Setup();
        s.Run();
        s.Reloaded();
        s.Host.Calls.Clear();
        var view = s.Run();
        Assert.True(view.Earless);
        Assert.Empty(s.Host.Calls);
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-5")]
    public void Restore_pass_covers_every_mod_reloads_each_and_redraws_worn()
    {
        using var s = new Setup();
        var other = s.AddMod("Other Hair", Worn, EaredHairModel(801, radius: 0.104f));
        s.Run();
        Assert.True(EarlessHairWriter.Save(other, EarlessHairBuilder.Build(other, Worn).Writes).Ok);
        s.Host.Calls.Clear();

        var pass = HairPortWatcher.RestorePass(s.Host, s.ModsRoot, Worn, () => false);
        Assert.True(pass.Completed);
        Assert.Equal(2, pass.Restored);
        // Each mod reloaded, in whatever order the folders come, then one redraw.
        Assert.Equal(["redraw", "reload Miqo Hair", "reload Other Hair"], s.Host.Calls.Order(StringComparer.Ordinal));
        Assert.Equal("redraw", s.Host.Calls.Last());
        Assert.Equal("long.mdl", s.Serves());
        Assert.Equal("long.mdl", s.Serves(other));
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-5")]
    [Trait("Plan", "EDGE-earless-miqote-hair-14")]
    public void Restore_due_truth_table()
    {
        Assert.True(HairPortWatcher.Decide.RestoreDue(watching: true, earlessOn: false, pending: true, busy: false, 10, 0));
        Assert.False(HairPortWatcher.Decide.RestoreDue(false, false, true, false, 10, 0));   // Penumbra or Proteus away
        Assert.False(HairPortWatcher.Decide.RestoreDue(true, true, true, false, 10, 0));     // on again
        Assert.False(HairPortWatcher.Decide.RestoreDue(true, false, false, false, 10, 0));   // nothing owed
        Assert.False(HairPortWatcher.Decide.RestoreDue(true, false, true, true, 10, 0));     // busy
        Assert.False(HairPortWatcher.Decide.RestoreDue(true, false, true, false, 10, 20));   // waiting to retry
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-5")]
    [Trait("Plan", "EDGE-earless-miqote-hair-30")]
    public void Setting_off_restores_a_worn_copy_at_equip()
    {
        using var s = new Setup();
        s.Run();
        s.Reloaded();
        s.Host.Calls.Clear();
        s.Host.Earless = false;
        s.Host.Enabled = false;
        var view = s.Run();
        Assert.False(view.Earless);
        Assert.Equal("long.mdl", s.Serves());
        Assert.Equal(["reload Miqo Hair", "redraw"], s.Host.Calls);
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-6")]
    [Trait("Plan", "EDGE-earless-miqote-hair-1")]
    public void Vanilla_hair_is_left_alone()
    {
        using var s = new Setup();
        s.Host.Files.Remove(Worn);
        s.Run();
        var outside = Path.Combine(Path.GetTempPath(), "proteus_outside_" + Path.GetRandomFileName() + ".mdl");
        File.WriteAllBytes(outside, EaredHairModel(801));
        try
        {
            s.Host.Files[Worn] = outside;
            s.Run();
        }
        finally { File.Delete(outside); }
        Assert.Empty(s.Host.Calls);
        Assert.False(Directory.Exists(Path.Combine(s.ModRoot, EarlessHairPlan.Folder)));
        Assert.False(EarlessHairRecord.Exists(s.ModRoot));
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-6")]
    [Trait("Plan", "EDGE-earless-miqote-hair-2")]
    public void Non_miqote_wearer_is_left_alone()
    {
        var path = HairPortPlan.HairPath(201, Id);
        using var s = new Setup(path, EaredHairModel(201));
        s.Host.WornPath = path;
        s.Run();
        Assert.Equal("long.mdl", s.Serves(path: path));
        Assert.Empty(s.Host.Calls);
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-7")]
    [Trait("Plan", "EDGE-earless-miqote-hair-12")]
    public void Port_file_serving_the_worn_path_is_not_swapped()
    {
        const string port = "Hair Ports/Cut - Long abcdef/long.mdl";
        using var s = new Setup();
        File.WriteAllText(Path.Combine(s.ModRoot, "meta.json"),
            "{\"FileVersion\":4,\"Name\":\"Miqo Hair\",\"Groups\":[" + Group("Cut", "Single", ("Long", new() { [Worn] = port }))
            + "],\"DefaultData\":{\"Files\":{},\"FileSwaps\":{},\"Manipulations\":[]}}");
        Directory.CreateDirectory(Path.Combine(s.ModRoot, "Hair Ports", "Cut - Long abcdef"));
        File.WriteAllBytes(Path.Combine(s.ModRoot, port), EaredHairModel(801));
        s.Host.Files[Worn] = Path.Combine(s.ModRoot, port);
        s.Run();
        Assert.Equal(port, s.Serves());
        Assert.False(EarlessHairRecord.Exists(s.ModRoot));
        Assert.DoesNotContain("announce-earless", s.Host.Calls);
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-8")]
    public void Author_change_is_remade_at_the_next_resolution_change()
    {
        using var s = new Setup();
        s.Run();
        s.Reloaded();
        string? key = null;
        HairPortWatcher.Run(s.Host, Worn, ref key);
        var newer = EaredHairModel(801, radius: 0.103f);
        File.WriteAllBytes(Path.Combine(s.ModRoot, "long.mdl"), newer);
        // Nothing resolves differently: the look stops at the key.
        Assert.Same(HairPortWatcher.Unchanged, HairPortWatcher.Run(s.Host, Worn, ref key));
        Assert.NotEqual(HairPortPlan.Sha8(newer), EarlessHairRecord.Read(s.ModRoot).Swaps.Single().AuthorSha8);
        // A plugin reload (or another resolution) looks again, and the copy is made again.
        key = null;
        HairPortWatcher.Run(s.Host, Worn, ref key);
        Assert.Equal(HairPortPlan.Sha8(newer), EarlessHairRecord.Read(s.ModRoot).Swaps.Single().AuthorSha8);
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-9")]
    public void Refusal_reason_reaches_the_panel()
    {
        var combining = "{\"Name\":\"Mix\",\"Priority\":0,\"Type\":\"Combining\",\"DefaultSettings\":0,"
                      + "\"Options\":[{\"Name\":\"a\"}],\"Containers\":[{\"Files\":{\"" + Worn + "\":\"long.mdl\"}}]}";
        using var s = new Setup();
        s.AddMod("Miqo Hair", Worn, EaredHairModel(801), combining);
        var view = s.Run();
        Assert.True(view.Failed);
        Assert.Contains("Mix", view.Message);
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-15")]
    public void Turning_on_mid_restore_stops_between_mods()
    {
        using var s = new Setup();
        var other = s.AddMod("Other Hair", Worn, EaredHairModel(801, radius: 0.104f));
        s.Run();
        Assert.True(EarlessHairWriter.Save(other, EarlessHairBuilder.Build(other, Worn).Writes).Ok);
        int asked = 0;
        var pass = HairPortWatcher.RestorePass(s.Host, s.ModsRoot, Worn, () => asked++ >= 1);
        Assert.False(pass.Completed);
        Assert.Equal(1, pass.Restored);
        Assert.False(HairPortWatcher.Decide.ClearPending(pass.Completed, 1, 1));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-16")]
    public void Race_change_keeps_swaps()
    {
        using var s = new Setup();
        s.Run();
        s.Host.WornPath = HairPortPlan.HairPath(201, Id);
        s.Run();
        Assert.Equal("Proteus Earless/long.mdl", s.Serves());
        Assert.Single(EarlessHairRecord.Read(s.ModRoot).Swaps);
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-21")]
    public void Off_with_nothing_to_restore_does_nothing()
    {
        using var s = new Setup();
        var pass = HairPortWatcher.RestorePass(s.Host, s.ModsRoot, Worn, () => false);
        Assert.True(pass.Completed);
        Assert.Equal(0, pass.Restored);
        Assert.Empty(s.Host.Calls);
        Assert.True(HairPortWatcher.Decide.ClearPending(pass.Completed, 3, 3));
        Assert.Equal("long.mdl", s.Serves());
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-23")]
    public void No_redraw_when_another_hairstyle_went_on()
    {
        using var s = new Setup();
        s.Host.WornAfter = HairPortPlan.HairPath(801, 7);
        s.Run();
        Assert.Equal(["announce-earless", "reload Miqo Hair"], s.Host.Calls);
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-31")]
    [Trait("Plan", "EDGE-earless-miqote-hair-32")]
    public void Pass_with_blocked_and_failed_mods_clears_pending_with_one_line_each()
    {
        using var s = new Setup();
        s.Run();
        File.Delete(Path.Combine(s.ModRoot, "long.mdl"));   // the author's file gone: the copy has to stay
        s.Host.Calls.Clear();
        var pass = HairPortWatcher.RestorePass(s.Host, s.ModsRoot, Worn, () => false);
        Assert.True(pass.Completed);
        Assert.Equal(["Miqo Hair"], pass.Blocked);
        Assert.Equal(1, s.Host.Calls.Count(c => c == "announce-earless"));
        Assert.True(HairPortWatcher.Decide.ClearPending(pass.Completed, 4, 4));
        // Switched while the pass ran (off, on, off): it owes another.
        Assert.False(HairPortWatcher.Decide.ClearPending(pass.Completed, 4, 6));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-33")]
    public void Unreadable_at_equip_clears_the_key_and_asks_again_later()
    {
        using var s = new Setup();
        Directory.CreateDirectory(Path.Combine(s.ModRoot, "Proteus"));
        File.WriteAllText(Path.Combine(s.ModRoot, "Proteus", EarlessHairRecord.RecordFile), "{ not json");
        string? key = null;
        var view = HairPortWatcher.Run(s.Host, Worn, ref key);
        Assert.True(view.Failed);
        Assert.Null(key);
        Assert.Equal(["look-again"], s.Host.Calls);
        Assert.Equal("long.mdl", s.Serves());
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-33")]
    public void Backoff_doubles_resets_and_survives_across_looks()
    {
        var backoff = new HairPortWatcher.Backoff();
        Assert.Equal([5000, 10000, 20000], new[] { backoff.Next(), backoff.Next(), backoff.Next() });
        for (int i = 0; i < 10; i++) backoff.Next();
        Assert.Equal(300_000, backoff.Next());
        backoff.Reset();
        Assert.Equal(5000, backoff.Next());
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-35")]
    public void Pass_logs_an_unreadable_mod_once_and_still_clears_pending()
    {
        using var s = new Setup();
        s.Run();
        File.WriteAllText(Path.Combine(s.ModRoot, "Proteus", EarlessHairRecord.RecordFile), "{ not json");
        s.Host.Calls.Clear();
        var pass = HairPortWatcher.RestorePass(s.Host, s.ModsRoot, Worn, () => false);
        Assert.Equal(["Miqo Hair"], pass.Unreadable);
        Assert.True(pass.Completed);
        Assert.Empty(s.Host.Calls);
        Assert.Single(s.Host.Lines, l => l.Contains("not put back"));
        Assert.True(HairPortWatcher.Decide.ClearPending(pass.Completed, 0, 0));
    }

    /// <summary>A Miqo'te wearing a hairstyle whose only mod was made for Midlanders.</summary>
    private static Setup OtherRaceOnly()
    {
        var mid = HairPortPlan.HairPath(201, Id);
        var s = new Setup(mid, HairModel(201));
        s.Host.Files.Remove(Worn);
        return s;
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-36")]
    public void Earless_on_porting_off_never_ports_or_schedules()
    {
        using var s = OtherRaceOnly();
        s.Host.Enabled = false;
        var view = s.Run();
        Assert.False(view.Ported);
        Assert.Empty(s.Host.Calls);
        Assert.False(Directory.Exists(Path.Combine(s.ModRoot, HairPortPlan.Folder)));
        Assert.False(HairPortWatcher.Decide.Gates(true, true, autoPort: false, earless: true).Porting);
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-37")]
    public void Both_off_resolves_only_the_worn_path()
    {
        using var s = new Setup();
        s.Host.Enabled = false;
        s.Host.Earless = false;
        s.Run();
        Assert.Equal([Worn], s.Host.Resolved);
        Assert.Equal("long.mdl", s.Serves());
        Assert.Empty(s.Host.Calls);
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-39")]
    public void Porting_off_keeps_the_key_for_an_other_race_only_hair()
    {
        using var s = OtherRaceOnly();
        s.Host.Enabled = false;
        string? key = null;
        HairPortWatcher.Run(s.Host, Worn, ref key);
        Assert.NotNull(key);
        Assert.Same(HairPortWatcher.Unchanged, HairPortWatcher.Run(s.Host, Worn, ref key));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-39")]
    public void Porting_turned_on_ports_the_worn_hairstyle()
    {
        using var s = OtherRaceOnly();
        s.Host.Enabled = false;
        string? key = null;
        HairPortWatcher.Run(s.Host, Worn, ref key);
        // Fitting switched on: the watcher drops the key on that edge, so the same hairstyle is looked at afresh.
        Assert.Null(HairPortWatcher.Decide.KeyAfterPortingEdge(key, wasPorting: false, porting: true));
        Assert.Equal(key, HairPortWatcher.Decide.KeyAfterPortingEdge(key, wasPorting: true, porting: true));
        key = HairPortWatcher.Decide.KeyAfterPortingEdge(key, false, true);
        s.Host.Enabled = true;
        var view = HairPortWatcher.Run(s.Host, Worn, ref key);
        Assert.True(view.Ported, view.Message);
    }
}
