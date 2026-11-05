using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The compositor reads through the gear shader upgrade's copies to their sources, and never treats one of them as a
/// loss or as its own output.
/// </summary>
public class CompositorUpstreamTests
{
    private const string Managed = @"C:\Penumbra\Proteus";
    private const string Copy = @"C:\Penumbra\Proteus\ShaderUpgrade\1-aaaaaaaaaaaaaaaa.mtrl";
    private const string GamePath = "chara/equipment/e0001/material/v0001/mt_c0201e0001_top_a.mtrl";

    private sealed class Sources(Dictionary<string, string> map) : IShaderUpgradeSources
    {
        public string? SourceFor(string gamePath) => map.TryGetValue(gamePath, out var s) ? s : null;
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-29")]
    public void SeeThrough_reads_the_source_not_the_copy()
    {
        var src = new Sources(new() { [GamePath] = @"C:\Penumbra\Dress\top.mtrl" });
        Assert.Equal(@"C:\Penumbra\Dress\top.mtrl",
                     CompositorService.SeeThrough(GamePath, Copy, Managed, src.SourceFor, _ => null));
        Assert.Equal(@"C:\Penumbra\Dress\top.mtrl",
                     CompositorService.SeeThrough(GamePath, "|x|" + Copy.Replace('\\', '/'), Managed, src.SourceFor, _ => null));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-29")]
    public void HeldBy_names_shader_upgrade_and_live_brush_holds()
    {
        Assert.Equal(CompositorService.Hold.ShaderUpgrade, CompositorService.HeldBy(Copy, Managed));
        Assert.Equal(CompositorService.Hold.None, CompositorService.HeldBy(@"C:\Penumbra\Proteus\materials\a.mtrl", Managed));
        Assert.Equal(CompositorService.Hold.None, CompositorService.HeldBy(null, Managed));
        Assert.Equal(CompositorService.Hold.LiveBrush,
                     CompositorService.HeldBy(Path.Combine(Path.GetTempPath(), "proteus-live-brush", "1-ab-000001.mdl"), Managed));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-37")]
    public void SeeThrough_uses_the_per_path_source_for_shared_copies()
    {
        var src = new Sources(new() { ["p/top.mtrl"] = @"C:\Penumbra\Dress\a.mtrl", ["p/dwn.mtrl"] = @"C:\Penumbra\Skirt\a.mtrl" });
        Assert.Equal(@"C:\Penumbra\Dress\a.mtrl", CompositorService.SeeThrough("p/top.mtrl", Copy, Managed, src.SourceFor, _ => null));
        Assert.Equal(@"C:\Penumbra\Skirt\a.mtrl", CompositorService.SeeThrough("p/dwn.mtrl", Copy, Managed, src.SourceFor, _ => null));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-41")]
    public void SeeThrough_falls_back_to_the_memo_and_passes_other_files()
    {
        Assert.Equal(@"C:\memo.mtrl", CompositorService.SeeThrough(GamePath, Copy, Managed, null, _ => @"C:\memo.mtrl"));
        Assert.Equal(@"C:\Penumbra\Dress\top.mtrl",
                     CompositorService.SeeThrough(GamePath, @"C:\Penumbra\Dress\top.mtrl", Managed, _ => "x", _ => "y"));
        Assert.Null(CompositorService.SeeThrough(GamePath, null, Managed, _ => "x", _ => "y"));
        // Every base read goes through it; the redirect check (which classifies holds) reads raw.
        var upstream = File.ReadAllText(SourceFile("Proteus/Services/CompositorService.Upstream.cs"));
        Assert.Contains("var disk = ResolveSeeThrough(gamePath);", upstream);
        Assert.Contains("var d = ResolveSeeThrough(p);", upstream);
        foreach (var f in new[] { "Events", "OcclusionPass", "MaterialComposite" })
            Assert.Contains("ResolveSeeThrough(", File.ReadAllText(SourceFile($"Proteus/Services/CompositorService.{f}.cs")));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-41")]
    public void WaitForManifestLive_skips_held_paths()
    {
        var src = new Sources(new() { [GamePath] = @"C:\Penumbra\Dress\top.mtrl" });
        Assert.False(CompositorService.CanProbe(GamePath, src));
        Assert.True(CompositorService.CanProbe("chara/other.tex", src));
        Assert.True(CompositorService.CanProbe(GamePath, null));
        Assert.Equal(2, Regex.Matches(File.ReadAllText(SourceFile("Proteus/Services/CompositorService.Publish.cs")),
                                      @"CanProbe\(gamePath, held\)").Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-41")]
    public void ForgetUpstreams_drops_only_the_named_paths()
    {
        var memo = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = "1", ["b"] = "2", ["c"] = "3",
        };
        var settled = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase) { ["a"] = 0, ["c"] = 0 };
        Assert.Equal(1, CompositorService.ForgetPaths(["A", "zzz"], memo, settled));
        Assert.Equal(["b", "c"], memo.Keys.Order());
        Assert.Equal(["c"], settled.Keys);
    }

    internal static string SourceFile(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, file);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(file);
    }
}

/// <summary>What the managed mod publishes is snapshotted by real publishes only, never by the prime's narrowed manifest.</summary>
public class CompositorPublishSnapshotTests
{
    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-42")]
    public void Snapshot_callers_are_exactly_the_real_and_empty_publishes()
    {
        string Read(string f) => File.ReadAllText(CompositorUpstreamTests.SourceFile($"Proteus/Services/CompositorService.{f}.cs"));
        var run = Read("CompositeRun");
        Assert.Contains("compositor.WriteManagedModJson(empty, snapshot: true);", run);
        Assert.Contains("compositor.WriteManagedModJson(redirects, manipulations, snapshot: true);", run);
        Assert.Contains("WriteManagedModJson(new Dictionary<string, string>(), snapshot: true);", Read("ManagedMod"));
        Assert.Contains("WriteManagedModJson(new Dictionary<string, string>(), snapshot: true);", Read("Overrides"));
        var upstream = Read("Upstream");
        Assert.Contains("WriteManagedModJson(narrowed, manips, snapshot: false);", upstream);
        Assert.Contains("WriteManagedModJson(files, manips, snapshot: false);", upstream);
        // No other caller.
        var all = Directory.GetFiles(Path.GetDirectoryName(CompositorUpstreamTests.SourceFile("Proteus/Services/CompositorService.cs"))!,
                                     "CompositorService*.cs")
                           .Sum(f => Regex.Matches(File.ReadAllText(f), @"WriteManagedModJson\(").Count);
        Assert.Equal(7, all);   // six calls and the definition
    }
}

/// <summary>A design captured while gear is upgraded still names the mods the copies came from.</summary>
public class DesignBindingSelectionTests
{
    private const string ModsRoot = @"C:\Penumbra";
    private const string Copy = @"C:\Penumbra\Proteus\ShaderUpgrade\1-aaaaaaaaaaaaaaaa.mtrl";

    private static readonly IReadOnlyDictionary<string, string> Installed =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Recolour"] = "Recolour", ["Skirt"] = "Skirt", ["Proteus"] = "Proteus" };

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-30")]
    public void Copies_are_attributed_to_their_source_mod()
    {
        var mods = DesignBindingService.SelectCharacterMods(ModsRoot, [Copy],
            new Dictionary<string, Proteus.Interop.PenumbraBridge.ModSettingsSnapshot>(),
            new Dictionary<string, PenumbraModSetting>(), Installed, new HashSet<string>(),
            f => f == Copy ? @"C:\Penumbra\Recolour\top.mtrl" : null);
        Assert.Equal("Recolour", Assert.Single(mods).ModDirectory);

        // Without the attribution the material-only mod would be lost to the managed mod.
        Assert.Empty(DesignBindingService.SelectCharacterMods(ModsRoot, [Copy],
            new Dictionary<string, Proteus.Interop.PenumbraBridge.ModSettingsSnapshot>(),
            new Dictionary<string, PenumbraModSetting>(), Installed, new HashSet<string>()));

        var tree = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase) { [Copy] = ["p/top.mtrl"] };
        var strays = DesignBindingService.StrayDrawnMods(ModsRoot, tree, new HashSet<string>(), new HashSet<string>(),
                                                         (f, _) => f == Copy ? @"C:\Penumbra\Recolour\top.mtrl" : null);
        Assert.Equal(["Recolour"], strays);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-37")]
    public void Identical_materials_from_two_mods_keep_their_own_mods()
    {
        var tree = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase) { [Copy] = ["p/top.mtrl", "p/dwn.mtrl"] };
        string? Source(string f, IEnumerable<string>? paths)
            => paths?.Contains("p/dwn.mtrl") == true && paths.Count() == 1 ? @"C:\Penumbra\Skirt\a.mtrl"
             : @"C:\Penumbra\Recolour\a.mtrl";
        var redirects = new ShaderUpgradeRedirects((_, _) => true, _ => true);
        redirects.Apply(redirects.Begin(), Guid.NewGuid(), [
            new RedirectUnit("p/top.mtrl", Copy, null, Upstream: @"C:\Penumbra\Recolour\a.mtrl"),
            new RedirectUnit("p/dwn.mtrl", Copy, null, Upstream: @"C:\Penumbra\Skirt\a.mtrl"),
        ]);
        Assert.Equal(@"C:\Penumbra\Skirt\a.mtrl", redirects.SourceForCopy(Copy, ["p/dwn.mtrl"]));
        Assert.Equal(@"C:\Penumbra\Recolour\a.mtrl", redirects.SourceForCopy(Copy, ["p/top.mtrl"]));
        Assert.Equal(["Recolour"], DesignBindingService.StrayDrawnMods(ModsRoot, tree, new HashSet<string>(), new HashSet<string>(), Source));
    }
}

/// <summary>The Create tab never sees a shader-upgrade copy or its invented mask path: it reads through them.</summary>
public class ModCreationSeeThroughTests
{
    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-4")]
    [Trait("Plan", "EDGE-dt-shader-upgrade-48")]
    public void Created_mods_never_name_a_copy_or_invented_path()
    {
        var src = File.ReadAllText(CompositorUpstreamTests.SourceFile("Proteus/Services/ModCreationService.cs"));
        Assert.DoesNotContain("penumbra.ResolvePlayer(", src);
        Assert.Equal(3, Regex.Matches(src, @"compositor\.ResolveSeeThrough\(").Count);
        // What it resolves through: a copy is its source, so the slots read are the author's, never chara/proteus/….
        Assert.Equal(@"C:\Penumbra\Dress\top.mtrl", CompositorService.SeeThrough(
            "chara/equipment/e0001/material/v0001/mt_c0201e0001_top_a.mtrl",
            @"C:\Penumbra\Proteus\ShaderUpgrade\1-a.mtrl", @"C:\Penumbra\Proteus", _ => @"C:\Penumbra\Dress\top.mtrl", _ => null));
    }
}

/// <summary>The second skin's content resolves accept a copy's source only from inside the pack.</summary>
public class SecondSkinContentFileTests
{
    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-49")]
    public void ContentFileFrom_accepts_a_copys_source_inside_the_pack_and_falls_back_otherwise()
    {
        static bool Under(string root, string f) => f.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        const string pack = @"C:\Penumbra\Pack";
        Assert.Equal(@"C:\Penumbra\Pack\a.mtrl",
                     SecondSkinService.ContentFileFrom(@"C:\Penumbra\Pack\a.mtrl", "p/a.mtrl", pack, _ => true, Under));
        Assert.Null(SecondSkinService.ContentFileFrom(@"C:\Penumbra\Other\a.mtrl", "p/a.mtrl", pack, _ => true, Under));
        Assert.Null(SecondSkinService.ContentFileFrom("p/a.mtrl", "p/a.mtrl", pack, _ => true, Under));
        Assert.Null(SecondSkinService.ContentFileFrom(@"C:\Penumbra\Pack\gone.mtrl", "p/a.mtrl", pack, _ => false, Under));
        Assert.Null(SecondSkinService.ContentFileFrom(null, "p/a.mtrl", pack, _ => true, Under));
        // A copy would never pass (it is not inside the pack), and the compositor hands the see-through resolver in.
        Assert.Null(SecondSkinService.ContentFileFrom(@"C:\Penumbra\Proteus\ShaderUpgrade\1-a.mtrl", "p/a.mtrl", pack, _ => true, Under));
        Assert.Contains("ResolveContent = ResolveSeeThrough", File.ReadAllText(CompositorUpstreamTests.SourceFile("Proteus/Services/CompositorService.cs")));
    }
}
