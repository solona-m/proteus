using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using XivLiveMesh;
using X = Proteus.Tests.CrossSexFixtures;

namespace Proteus.Tests;

/// <summary>
/// The automatic path and ports across the sexes: a Midlander man (c0101) wearing hairstyle 5, with a women's hair mod
/// for it on — never fitted by the automatic path, but a port the Studio made there kept current.
/// </summary>
public partial class HairPortWatcherDecisionTests
{
    private static readonly string Man = X.Target;

    private sealed class CrossHost(string modsRoot, Func<string, byte[]?> game) : HairPortWatcher.IHost
    {
        public readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Calls = [];
        public bool Enabled => true;
        public string? ModsRoot => modsRoot;
        public string? Resolve(string gamePath) => Files.GetValueOrDefault(gamePath);
        public PbdFile? Pbd() => X.Pbd();
        public byte[]? ReadGame(string gamePath) => game(gamePath);
        public void Log(string message) { }
        public void Announce() => Calls.Add("announce");
        public void Reload(string modDir) => Calls.Add("reload " + modDir);
        public string? WornNow() => Man;
        public void Redraw() => Calls.Add("redraw");
        public void CheckMod(string modDir) => Calls.Add("check " + modDir);
    }

    /// <summary>A mods root holding one women's hair mod ("Her Hair", option Long) for hairstyle 5, served for c0201.</summary>
    private sealed class CrossSetup : IDisposable
    {
        public readonly string ModsRoot = Path.Combine(Path.GetTempPath(), "proteus_mods_" + Path.GetRandomFileName());
        public readonly HairPortFixtures.Mod Mod;
        public readonly CrossHost Host;
        public readonly Func<string, byte[]?> Game;

        public CrossSetup(bool physics = false)
        {
            var root = Path.Combine(ModsRoot, "Her Hair");
            Mod = X.WomensMod(physics: physics, root: root);
            Game = X.Game(physics ? X.PhysicsFiles() : X.GameFiles());
            Host = new CrossHost(ModsRoot, Game);
            Host.Files[X.Source] = Path.Combine(root, "long.mdl");
        }

        /// <summary>Penumbra serving what option Long now publishes for the man's path.</summary>
        public void Reloaded()
        {
            var rel = HairPortPlan.FileFor(Mod.Owner("Cut", "Long"), Man);
            if (rel != null) Host.Files[Man] = Path.Combine(Mod.Root, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        public HairPortWatcher.View Run()
        {
            string? key = null;
            return HairPortWatcher.Run(Host, Man, ref key);
        }

        public void Dispose() { try { Directory.Delete(ModsRoot, true); } catch { } }
    }

    /// <summary>[AC-hair-material-scalp-fit-9] A Studio port made by an older build, worn: the watcher remakes it to the
    /// current version (with the scalp push and its own material).</summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-9")]
    public void The_watcher_remakes_a_worn_old_port()
    {
        using var s = new CrossSetup();
        X.HandPort(s.Mod, [101]);
        var record = HairPortRecord.Read(s.Mod.Root);
        record.Ports.Single().PortVersion = 1;
        HairPortRecord.Write(s.Mod.Root, record);
        s.Reloaded();

        var view = s.Run();
        Assert.True(view.Ported);
        Assert.Equal(HairPortBaker.Version, HairPortRecord.Read(s.Mod.Root).Ports.Single().PortVersion);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-5")]
    public void A_womans_hair_mod_never_fits_a_man()
    {
        using var s = new CrossSetup();
        var view = s.Run();
        Assert.False(view.Ported);
        Assert.Empty(s.Host.Calls);
        Assert.False(Directory.Exists(Path.Combine(s.Mod.Root, HairPortPlan.Folder)));
        Assert.False(HairPortRecord.Exists(s.Mod.Root));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-6")]
    public void A_stale_cross_sex_port_worn_is_remade()
    {
        using var s = new CrossSetup();
        X.HandPort(s.Mod, [101], s.Game);
        s.Reloaded();
        var changed = X.Cap(201, radius: 0.101f);
        s.Mod.Put("long.mdl", changed);

        var view = s.Run();
        Assert.True(view.Ported, view.Message);
        Assert.Contains("reload Her Hair", s.Host.Calls);
        Assert.Equal(HairPortPlan.Sha8(changed), HairPortRecord.Read(s.Mod.Root).Ports.Single().SourceSha8);
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-13")]
    public void A_mens_mod_of_another_race_does_not_displace_a_studio_cross_sex_port()
    {
        using var s = new CrossSetup();
        X.HandPort(s.Mod, [101], s.Game);
        s.Reloaded();
        // A men's mod for a Highlander's hairstyle 5, also on.
        var other = Path.Combine(s.ModsRoot, "His Hair");
        using var his = new HairPortFixtures.Mod(defaults: new Dictionary<string, string> { [HairPortPlan.HairPath(301, X.Id)] = "h.mdl" },
                                                 root: other);
        his.Put("h.mdl", X.Cap(301));
        s.Host.Files[HairPortPlan.HairPath(301, X.Id)] = Path.Combine(other, "h.mdl");

        var resolved = HairPortScan.PathsFor(101, X.Id, HairPortPlan.OrderSources(101, HairPortPlan.SameGenderRaces(101)).ToList())
                                   .ToDictionary(p => p, p => (string?)s.Host.Resolve(p), StringComparer.OrdinalIgnoreCase);
        var d = HairPortWatcher.Decide.What(Man, resolved, HairPortPlan.OrderSources(101, HairPortPlan.SameGenderRaces(101)).ToList(),
                                            s.ModsRoot, HairPortRecord.Read, s.Game);
        Assert.Equal((HairPortWatcher.Decide.Kind.Ported, s.Mod.Root), (d.Kind, d.ModRoot));
        s.Run();
        Assert.False(Directory.Exists(Path.Combine(other, HairPortPlan.Folder)));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-7")]
    [Trait("Plan", "EDGE-cross-sex-hair-port-14")]
    public void A_cross_sex_port_left_for_the_authors_own_version_shows_as_fitted()
    {
        using var s = new CrossSetup();
        X.HandPort(s.Mod, [101], s.Game);
        s.Reloaded();
        X.AddFile(s.Mod, "Long", HairPortPlan.HairPath(301, X.Id), "men.mdl", X.Cap(301));
        s.Mod.Put("long.mdl", X.Cap(201, radius: 0.101f));
        var record = File.ReadAllText(Path.Combine(s.Mod.Root, "Proteus", HairPortRecord.RecordFile));

        var view = s.Run();
        Assert.True(view.Ported, view.Message);
        Assert.False(view.Failed);
        Assert.Equal("", view.Message);
        Assert.Empty(s.Host.Calls);
        Assert.Equal(record, File.ReadAllText(Path.Combine(s.Mod.Root, "Proteus", HairPortRecord.RecordFile)));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-9")]
    [Trait("Plan", "EDGE-cross-sex-hair-port-24")]
    public void A_stripped_cross_sex_port_is_not_refused_on_every_look()
    {
        using var s = new CrossSetup(physics: true);
        X.HandPort(s.Mod, [101], s.Game);
        X.AddEst(s.Mod, "Long", "0101", X.Id, 77);
        var strip = X.Build(s.Mod, [101], auto: true, s.Game);
        Assert.True(HairPortWriter.Save(s.Mod.Root, strip.Writes, auto: true).Ok);
        s.Reloaded();
        s.Mod.Put("long.mdl", X.Physics(201, radius: 0.101f));

        for (int look = 0; look < 2; look++)
        {
            var view = s.Run();
            Assert.True(view.Ported, view.Message);
            Assert.False(view.Failed);
        }
        Assert.Empty(s.Host.Calls);
    }
}
