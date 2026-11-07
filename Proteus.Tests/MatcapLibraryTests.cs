using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using Proteus.Services.Matcap;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

public sealed class MatcapLibraryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "proteus-matcap-lib-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    private const string SlotA = "chara/common/texture/testlib_a.tex", SlotB = "chara/common/texture/testlib_b.tex";

    [Fact]
    [Trait("Plan", "AC-matcaps-3")]
    public void Detects_switch_sampled_by_reflection_material()
    {
        var dir = SyntheticMatcapMod.Library(root, "Lib", "Test Library", "https://example.test/lib", [SlotA], ["Gem Red", "Pearl"]);
        var libs = MatcapLibrary.Detect(dir, "Lib", "Lib (Penumbra)");
        var lib = Assert.Single(libs);
        Assert.Equal("Test Library", lib.Name);
        Assert.Equal("https://example.test/lib", lib.Website);
        Assert.Equal("Texture Slot A", lib.Group);
        Assert.Equal(SlotA, lib.Texture);
        Assert.Equal(["Gem Red", "Pearl"], lib.Options.Select(o => o.Name));
        Assert.Equal("Test Library — Texture Slot A", lib.Label);
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-3")]
    public void Ignores_switch_not_sampled_and_non_reflection_materials()
    {
        var dir = SyntheticMatcapMod.Library(root, "Lib", "L", "", [SlotA], ["One", "Two"]);
        // Repoint the library's material at another path: the switch is then sampled by nothing.
        File.WriteAllBytes(Path.Combine(dir, "mat0.mtrl"), SyntheticMatcapMod.Reflection(matcap: "chara/common/texture/other.tex"));
        Assert.Empty(MatcapLibrary.Detect(dir, "Lib", "L"));
        // A gear material naming the path is not a reflection material either.
        File.WriteAllBytes(Path.Combine(dir, "mat0.mtrl"), SyntheticMatcapMod.Gear(SlotA));
        Assert.Empty(MatcapLibrary.Detect(dir, "Lib", "L"));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-16")]
    public void Options_without_path_not_offered()
    {
        var dir = SyntheticMatcapMod.Library(root, "Lib", "L", "", [SlotA], ["One", "Two"]);
        var lib = Assert.Single(MatcapLibrary.Detect(dir, "Lib", "L"));
        Assert.DoesNotContain(lib.Options, o => o.Name == "None");
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-17")]
    public void Two_slots_two_libraries()
    {
        var dir = SyntheticMatcapMod.Library(root, "Lib", "L", "", [SlotA, SlotB], ["One", "Two", "Three"]);
        var libs = MatcapLibrary.Detect(dir, "Lib", "L");
        Assert.Equal([SlotA, SlotB], libs.Select(l => l.Texture).Order());
        Assert.Equal(["Texture Slot A", "Texture Slot B"], libs.Select(l => l.Group).Order());
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-18")]
    public void Item_folder_switches_excluded()
    {
        const string ItemTex = "chara/equipment/e6255/texture/v01_c0201e6255_top_m.tex";
        var dir = SyntheticMatcapMod.Library(root, "Gear", "Gem Gear", "", [ItemTex], ["Red", "Blue"]);
        Assert.Empty(MatcapLibrary.Detect(dir, "Gear", "Gem Gear"));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-19")]
    public void V3_material_in_off_option_detected()
    {
        var dir = SyntheticMatcapMod.Library(root, "Old", "Old Library", "", [SlotA], ["One", "Two"], v3: true);
        Assert.True(File.Exists(Path.Combine(dir, "default_mod.json")));
        var lib = Assert.Single(MatcapLibrary.Detect(dir, "Old", "Old"));
        Assert.Equal(SlotA, lib.Texture);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-12")]
    public void Default_redirects_besides_the_slot_are_listed()
    {
        var dir = SyntheticMatcapMod.Library(root, "Lib", "L", "", [SlotA], ["One", "Two"],
                                             extraDefaultRedirect: "chara/common/texture/modded_mask.tex");
        var lib = Assert.Single(MatcapLibrary.Detect(dir, "Lib", "L"));
        Assert.Contains("chara/common/texture/modded_mask.tex", lib.AlsoRedirects);
        Assert.DoesNotContain(SlotA, lib.AlsoRedirects);
    }
}

public sealed class MatcapIndexTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "proteus-matcap-idx-" + Guid.NewGuid().ToString("N"));
    private string Mods => Path.Combine(root, "mods");
    private string Config => Path.Combine(root, "config");

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    private Dictionary<string, string> Listed(params string[] dirs) => dirs.ToDictionary(d => d, d => d);

    [Fact]
    [Trait("Plan", "AC-matcaps-3")]
    public void Rereads_only_changed_mods_and_persists_cache()
    {
        SyntheticMatcapMod.Library(Mods, "LibA", "A", "", ["chara/common/texture/a.tex"], ["1", "2"]);
        SyntheticMatcapMod.Library(Mods, "LibB", "B", "", ["chara/common/texture/b.tex"], ["1", "2"]);
        var index = new MatcapIndex(Config);
        var state = index.Refresh(Listed("LibA", "LibB"), Mods);
        Assert.Equal(2, state.Libraries.Count);
        Assert.Equal(2, index.LastReads);

        index.Refresh(Listed("LibA", "LibB"), Mods);
        Assert.Equal(0, index.LastReads);

        // Touch one manifest: only that one is read again.
        var meta = Path.Combine(Mods, "LibB", "meta.json");
        File.AppendAllText(meta, " ");
        File.SetLastWriteTimeUtc(meta, DateTime.UtcNow.AddMinutes(1));
        index.Refresh(Listed("LibA", "LibB"), Mods);
        Assert.Equal(1, index.LastReads);

        // A new session starts from the cache, before any refresh.
        Assert.True(File.Exists(Path.Combine(Config, MatcapIndex.CacheFile)));
        var later = new MatcapIndex(Config);
        Assert.Equal(2, later.Current!.Libraries.Count);
        later.Refresh(Listed("LibA", "LibB"), Mods);
        Assert.Equal(0, later.LastReads);
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-3")]
    public void Rescan_forces_reread()
    {
        SyntheticMatcapMod.Library(Mods, "LibA", "A", "", ["chara/common/texture/a.tex"], ["1", "2"]);
        var index = new MatcapIndex(Config);
        index.Refresh(Listed("LibA"), Mods);
        index.Refresh(Listed("LibA"), Mods, force: true);
        Assert.Equal(1, index.LastReads);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-14")]
    public void Duplicate_providers_flagged()
    {
        const string Slot = "chara/common/texture/shared.tex";
        SyntheticMatcapMod.Library(Mods, "Lib1", "One", "", [Slot], ["1", "2"]);
        SyntheticMatcapMod.Library(Mods, "Lib2", "Two", "", [Slot], ["x", "y"]);
        var state = new MatcapIndex(null).Refresh(Listed("Lib1", "Lib2"), Mods);
        Assert.Equal(["Lib1", "Lib2"], state.ProvidersOf(Slot));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-27")]
    public void Corrupt_or_old_cache_discarded()
    {
        Directory.CreateDirectory(Config);
        File.WriteAllText(Path.Combine(Config, MatcapIndex.CacheFile), "{ not json");
        Assert.Null(new MatcapIndex(Config).Current);
        File.WriteAllText(Path.Combine(Config, MatcapIndex.CacheFile), new JsonObject { ["Version"] = 99, ["Mods"] = new JsonObject() }.ToJsonString());
        Assert.Null(new MatcapIndex(Config).Current);

        SyntheticMatcapMod.Library(Mods, "LibA", "A", "", ["chara/common/texture/a.tex"], ["1", "2"]);
        var index = new MatcapIndex(Config);
        Assert.Single(index.Refresh(Listed("LibA"), Mods).Libraries);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-38")]
    public void Managed_mod_excluded()
    {
        SyntheticMatcapMod.Library(Mods, "Proteus", "Proteus", "", ["chara/common/texture/a.tex"], ["1", "2"]);
        Assert.Empty(new MatcapIndex(null).Refresh(Listed("Proteus"), Mods).Libraries);
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-3")]
    public void Removed_mods_are_forgotten()
    {
        SyntheticMatcapMod.Library(Mods, "LibA", "A", "", ["chara/common/texture/a.tex"], ["1", "2"]);
        var index = new MatcapIndex(null);
        index.Refresh(Listed("LibA"), Mods);
        Assert.Empty(index.Refresh(Listed(), Mods).Libraries);
    }
}

/// <summary>The real MatCap Library (Westlaketea), unpacked to a temp folder — owner's machine only.</summary>
public sealed class MatcapIndexLocalTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "proteus-matcap-real-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    [LocalDataFact(LocalData.MatcapLibraryPack)]
    [Trait("Plan", "AC-matcaps-3")]
    public void Westlaketea_library_two_slots_48_options_with_website()
    {
        var dir = Path.Combine(root, "MatCap Library");
        ZipFile.ExtractToDirectory(LocalData.Path(LocalData.MatcapLibraryPack), dir);
        var sw = Stopwatch.StartNew();
        var state = new MatcapIndex(null).Refresh(new Dictionary<string, string> { ["MatCap Library"] = "MatCap Library" }, root);
        output.WriteLine($"scan {sw.ElapsedMilliseconds} ms");
        Assert.Equal(2, state.Libraries.Count);
        var a = state.Libraries.Single(l => l.Texture == "chara/common/texture/matcaplib_a.tex");
        var b = state.Libraries.Single(l => l.Texture == "chara/common/texture/matcaplib_b.tex");
        Assert.Equal("MatCap Library", a.Name);
        Assert.Equal("https://westlaketeamods.carrd.co/", a.Website);
        Assert.Equal(48, a.Options.Count);                                  // 47 matcaps and the user slot
        Assert.Equal(48, b.Options.Count);
        Assert.Contains(a.Options, o => o.Name == "Gem Red");
        // Switching it on with its defaults also publishes the cushion and the generic modded_* textures.
        Assert.Contains("chara/common/texture/modded_mask.tex", a.AlsoRedirects);
        Assert.Contains(a.AlsoRedirects, p => p.EndsWith(".mdl", StringComparison.Ordinal));
        output.WriteLine(string.Join("\n", a.AlsoRedirects));
    }
}
