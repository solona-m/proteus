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
/// The automatic path: what a Midlander woman wearing hairstyle 130 gets when the only enabled mod for it was made for
/// Miqo'te — through <see cref="HairPortWatcher.Run"/> with Penumbra and the game stood in for.
/// </summary>
public class HairPortWatcherDecisionTests
{
    private static readonly string Worn = HairPortPlan.HairPath(201, Id);
    private static readonly string Source = HairPortPlan.HairPath(801, Id);
    private static readonly ushort[] Races = [101, 201, 401, 601, 801, 1001, 1101, 1201, 1401, 1601, 1801];

    private sealed class Host(string modsRoot) : HairPortWatcher.IHost
    {
        public readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Calls = [];
        public readonly List<string> Lines = [];
        public bool Enabled { get; set; } = true;
        public string? WornAfter { get; set; }

        public string? ModsRoot => modsRoot;
        public string? Resolve(string gamePath) => Files.GetValueOrDefault(gamePath);
        public bool NoPbd { get; set; }
        public PbdFile? Pbd() => NoPbd ? null : HairPortFixtures.Pbd();
        public byte[]? ReadGame(string gamePath) => Game(Races)(gamePath);
        public void Log(string message) => Lines.Add(message);
        public void Announce() => Calls.Add("announce");
        public void Reload(string modDir) => Calls.Add("reload " + modDir);
        public string? WornNow() => WornAfter ?? Worn;
        public void Redraw() => Calls.Add("redraw");
        public void CheckMod(string modDir) => Calls.Add("check " + modDir);
    }

    /// <summary>A Miqo'te-only hair mod in a mods root of its own, and Penumbra serving it for the Miqo'te path.</summary>
    private sealed class Setup : IDisposable
    {
        public readonly string ModsRoot = Path.Combine(Path.GetTempPath(), "proteus_mods_" + Path.GetRandomFileName());
        public readonly string ModRoot;
        public readonly Host Host;

        public Setup()
        {
            ModRoot = Path.Combine(ModsRoot, "Miqo Hair");
            Directory.CreateDirectory(ModRoot);
            File.WriteAllText(Path.Combine(ModRoot, "meta.json"),
                "{\"FileVersion\":4,\"Name\":\"Miqo Hair\",\"Groups\":[" + Group("Cut", "Single", ("Long", new() { [Source] = "long.mdl" }))
                + "],\"DefaultData\":{\"Files\":{},\"FileSwaps\":{},\"Manipulations\":[]}}");
            File.WriteAllBytes(Path.Combine(ModRoot, "long.mdl"), HairModel(801));
            Host = new Host(ModsRoot);
            Host.Files[Source] = Path.Combine(ModRoot, "long.mdl");
        }

        /// <summary>Penumbra serving what the manifest now says for the worn path, as after a reload.</summary>
        public void Reloaded()
        {
            var rel = HairPortPlan.FileFor(HairPortPlan.ReadOwners(ModRoot)!.First(o => o.Option == "Long"), Worn);
            if (rel != null) Host.Files[Worn] = Path.Combine(ModRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            else Host.Files.Remove(Worn);
        }

        public HairPortWatcher.View Run()
        {
            string? key = null;
            return HairPortWatcher.Run(Host, Worn, ref key);
        }

        public void Dispose() { try { Directory.Delete(ModsRoot, true); } catch { } }
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-1")]
    [Trait("Plan", "AC-hair-port-scan-10")]
    public void Vanilla_worn_hair_with_a_same_gender_mod_ports_into_that_mod()
    {
        using var s = new Setup();
        var view = s.Run();
        Assert.True(view.Ported, view.Message);
        var rel = HairPortPlan.FileFor(HairPortPlan.ReadOwners(s.ModRoot)!.First(o => o.Option == "Long"), Worn);
        Assert.Equal(HairPortPlan.PortFile(new HairPortPlan.Owner("Cut", "Long", "", new Dictionary<string, string>()), Worn), rel);
        Assert.StartsWith("Hair Ports/Cut - Long ", rel);
        Assert.True(File.Exists(Path.Combine(s.ModRoot, rel!)));
    }

    /// <summary>
    /// The worn hairstyle, ported earlier this session with a warning, is found already ported at the next look — and
    /// the worn line still says the warning (cross-sex-tops-yas). A port made with none clears it: PortOne writes the key
    /// the look reads.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-8")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-23")]
    public void A_warned_worn_port_keeps_its_warning_at_the_next_look() // [AC-cross-sex-tops-yas-8] [EDGE-cross-sex-tops-yas-23]
    {
        using var s = new Setup();
        // Stale from before: the port below warns of nothing, so PortOne clears it under the same key.
        HairPortScan.RememberWarning(s.ModRoot, Worn, "stale");
        var ported = s.Run();
        Assert.True(ported.Ported, ported.Message);
        Assert.Equal("", ported.Warning);
        s.Reloaded();
        Assert.Equal("", s.Run().Warning);

        const string warning = "bent onto c0101, 40 of its triangles turn inside out and may show as holes; check it in game";
        HairPortScan.RememberWarning(s.ModRoot, Worn, warning);
        var again = s.Run();
        Assert.True(again.Ported, again.Message);
        Assert.Equal(warning, again.Warning);
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-1")]
    [Trait("Plan", "AC-hair-port-scan-10")]
    public void After_saving_the_mod_is_reloaded_then_redrawn()
    {
        using var s = new Setup();
        s.Run();
        // Then the mod's other hairstyles for this race are looked at too, as a check of the mod.
        Assert.Equal(["announce", "reload Miqo Hair", "redraw", "check Miqo Hair"], s.Host.Calls);
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-9")]
    public void Automatic_port_announces_once()
    {
        using var s = new Setup();
        s.Run();
        s.Reloaded();
        s.Run();   // now drawn from the port: nothing more to say
        Assert.Equal(1, s.Host.Calls.Count(c => c == "announce"));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-1")]
    public void Already_modded_for_own_race_does_nothing()
    {
        using var s = new Setup();
        File.WriteAllBytes(Path.Combine(s.ModRoot, "mine.mdl"), HairModel(201));
        s.Host.Files[Worn] = Path.Combine(s.ModRoot, "mine.mdl");
        var view = s.Run();
        Assert.False(view.Ported);
        Assert.Empty(s.Host.Calls);
        Assert.False(Directory.Exists(Path.Combine(s.ModRoot, HairPortPlan.Folder)));
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-7")]
    public void Declined_port_is_not_redone()
    {
        using var s = new Setup();
        s.Run();
        HairPortWriter.Undo(s.ModRoot, _ => true, decline: true);
        s.Reloaded();
        s.Host.Calls.Clear();
        var view = s.Run();
        Assert.False(view.Ported);
        Assert.Empty(s.Host.Calls);
    }

    [Theory]
    [Trait("Plan", "AC-hair-race-port-7")]
    [Trait("Plan", "EDGE-hair-race-port-24")]
    [InlineData("off")]
    [InlineData("no-mods-root")]
    public void Inert_when_off_or_without_penumbra(string why)
    {
        using var s = new Setup();
        s.Host.Enabled = why != "off";
        HairPortWatcher.View view;
        string? key = null;
        view = why == "no-mods-root"
            ? HairPortWatcher.Run(new Host(""), Worn, ref key)
            : HairPortWatcher.Run(s.Host, Worn, ref key);
        Assert.False(view.Ported);
        Assert.Empty(s.Host.Calls);
        Assert.False(Directory.Exists(Path.Combine(s.ModRoot, HairPortPlan.Folder)));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-11")]
    public void No_redraw_when_hair_changed_during_port()
    {
        using var s = new Setup();
        s.Host.WornAfter = HairPortPlan.HairPath(201, 131);
        var view = s.Run();
        Assert.True(view.Ported);
        Assert.DoesNotContain("redraw", s.Host.Calls);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-12")]
    public void Unchanged_key_skips_examination()
    {
        using var s = new Setup();
        // A hairstyle no mod covers: the second look at the same resolutions does nothing at all.
        var worn = HairPortPlan.HairPath(201, 999);
        string? key = null;
        Assert.NotSame(HairPortWatcher.Unchanged, HairPortWatcher.Run(s.Host, worn, ref key));
        Assert.Same(HairPortWatcher.Unchanged, HairPortWatcher.Run(s.Host, worn, ref key));
        // Something it resolves changing is a reason to look again.
        s.Host.Files[HairPortPlan.HairPath(401, 999)] = "x";
        Assert.NotSame(HairPortWatcher.Unchanged, HairPortWatcher.Run(s.Host, worn, ref key));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-13")]
    public void Blank_walks_retry_before_none()
    {
        int blanks = 0;
        for (int i = 1; i < HairPortWatcher.BlanksBeforeNone; i++)
            Assert.True(HairPortWatcher.Decide.Blank(["chara/equipment/e0001/model/c0201e0001_top.mdl"], ref blanks));
        Assert.False(HairPortWatcher.Decide.Blank(null, ref blanks));
        Assert.False(HairPortWatcher.Decide.Blank([Worn], ref blanks));
        Assert.Equal(0, blanks);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-15")]
    public void Worn_stale_port_is_redone()
    {
        using var s = new Setup();
        s.Run();
        s.Reloaded();
        File.WriteAllBytes(Path.Combine(s.ModRoot, "long.mdl"), HairModel(801, material: "/mt_c0801h0130_hir_b.mtrl"));
        s.Host.Calls.Clear();
        var view = s.Run();
        Assert.True(view.Ported, view.Message);
        Assert.Contains("redraw", s.Host.Calls);
        var entry = HairPortRecord.Read(s.ModRoot).Ports.Single();
        Assert.Equal(HairPortPlan.Sha8(File.ReadAllBytes(Path.Combine(s.ModRoot, "long.mdl"))), entry.SourceSha8);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-17")]
    public void Own_port_is_never_a_source()
    {
        using var s = new Setup();
        s.Host.Files.Remove(Source);
        var portOf401 = Path.Combine(s.ModRoot, "Hair Ports", "Cut - Long", "c0401.mdl");
        Directory.CreateDirectory(Path.GetDirectoryName(portOf401)!);
        File.WriteAllBytes(portOf401, HairModel(401));
        s.Host.Files[HairPortPlan.HairPath(401, Id)] = portOf401;
        var view = s.Run();
        Assert.False(view.Ported);
        Assert.Empty(s.Host.Calls);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-18")]
    public void Candidate_outside_mods_root_is_skipped()
    {
        using var s = new Setup();
        var elsewhere = Path.Combine(Path.GetTempPath(), "proteus_elsewhere_" + Path.GetRandomFileName() + ".mdl");
        File.WriteAllBytes(elsewhere, HairModel(801));
        try
        {
            s.Host.Files[Source] = elsewhere;
            var view = s.Run();
            Assert.False(view.Ported);
            Assert.Empty(s.Host.Calls);
        }
        finally { File.Delete(elsewhere); }
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-30")]
    public void Fit_refusal_writes_nothing()
    {
        using var s = new Setup();
        // A target head a third larger than the hair: the port would sink into it.
        var game = Game(Races, new Dictionary<string, byte[]> { [HairPortPlan.HeadPaths(201).First()] = HeadModel(201, 0.13f) });
        var host = new BigHead(s.ModsRoot, game);
        foreach (var (k, v) in s.Host.Files) host.Files[k] = v;
        string? key = null;
        var view = HairPortWatcher.Run(host, Worn, ref key);
        Assert.False(view.Ported);
        Assert.True(view.Failed);
        Assert.Empty(host.Calls);
        Assert.False(Directory.Exists(Path.Combine(s.ModRoot, HairPortPlan.Folder)));
    }

    private sealed class BigHead(string modsRoot, Func<string, byte[]?> game) : HairPortWatcher.IHost
    {
        public readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Calls = [];
        public bool Enabled => true;
        public string? ModsRoot => modsRoot;
        public string? Resolve(string gamePath) => Files.GetValueOrDefault(gamePath);
        public PbdFile? Pbd() => HairPortFixtures.Pbd();
        public byte[]? ReadGame(string gamePath) => game(gamePath);
        public void Log(string message) { }
        public void Announce() => Calls.Add("announce");
        public void Reload(string modDir) => Calls.Add("reload");
        public string? WornNow() => Worn;
        public void Redraw() => Calls.Add("redraw");
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-27")]
    public void Log_lines_name_mod_folder_and_rel_only()
    {
        using var s = new Setup();
        s.Run();
        Assert.NotEmpty(s.Host.Lines);
        Assert.All(s.Host.Lines, line => Assert.DoesNotContain(s.ModsRoot, line, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(s.Host.Lines, line => line.Contains("Miqo Hair"));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-19")]
    public void Without_the_games_deformer_nothing_is_ported()
    {
        using var s = new Setup();
        s.Host.NoPbd = true;
        var view = s.Run();
        Assert.False(view.Ported);
        Assert.True(view.Failed);
        Assert.Equal(HairPortWatcher.NoPbd, view.Message);
        Assert.Empty(s.Host.Calls);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-23")]
    public void A_declined_port_says_so_in_the_panel()
    {
        using var s = new Setup();
        s.Run();
        HairPortWriter.Undo(s.ModRoot, _ => true, decline: true);
        s.Reloaded();
        var view = s.Run();
        Assert.Contains("h0130", view.Message);
        Assert.Contains("Miqo Hair", view.Message);
    }

    /// <summary>A look asked for while one runs is owed and served once, not dropped and not queued twice.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-12")]
    public void Second_request_while_busy_is_owed_once()
    {
        var flight = new HairPortWatcher.SingleFlight();
        Assert.True(flight.TryEnter());
        Assert.True(flight.Busy);
        Assert.False(flight.TryEnter());
        Assert.False(flight.TryEnter(400));   // a Penumbra settings change: worth a look once Penumbra has settled
        Assert.False(flight.TryEnter(100));
        Assert.True(flight.Exit(out int delay));   // one look owed
        Assert.Equal(400, delay);                  // after the longest wait asked for
        Assert.False(flight.Busy);
        Assert.True(flight.TryEnter());
        Assert.False(flight.Exit(out delay));      // and only one
        Assert.Equal(0, delay);
    }
}
