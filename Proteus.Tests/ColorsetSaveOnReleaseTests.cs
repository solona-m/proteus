using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The colour editor writes a mod's metadata.json once a control is let go, not every frame of a drag, while discovery
/// — and so the live preview — reads the held edit in the meantime.
/// </summary>
public class ColorsetSaveOnReleaseTests : IDisposable
{
    private readonly string mods = Path.Combine(Path.GetTempPath(), "proteus-test-" + Guid.NewGuid().ToString("N"));
    private readonly SidecarDiscoveryService discovery =
        new(null!, Substitute.For<IPluginLog>());

    public void Dispose()
    {
        try { Directory.Delete(mods, recursive: true); } catch { /* best effort */ }
    }

    private string Sidecar(string mod = "Skin") => Path.Combine(mods, mod, SidecarDiscoveryService.SidecarSubdir);
    private string MetaPath(string mod = "Skin") => Path.Combine(Sidecar(mod), SidecarDiscoveryService.MetadataFile);

    private static ColorTableRowPreset Row(int row, string diffuse)
        => new() { Row = row, SubRowA = new ColorTableSubRowPreset { Diffuse = diffuse } };

    private static ProteusMetadata Meta(string diffuse) => new()
    {
        ColorTableRows = [Row(1, diffuse)],
        OptionGroups =
        [
            new OverlayOptionGroup { Options =
            [
                new OverlayOption { Name = "Sheer", Overlays = [new OverlayDescriptor { Diffuse = "s.png" }],
                                    ColorTableRows = [Row(2, diffuse)] },
            ]},
        ],
        MaskColorTableRows = [Row(3, diffuse)],
    };

    /// <summary>A mod whose sidecar holds <paramref name="onDisk"/>; the entry is its own parse, as discovery's would be.</summary>
    private OverlayEntry Mod(ProteusMetadata onDisk, string mod = "Skin")
    {
        Directory.CreateDirectory(Sidecar(mod));
        File.WriteAllText(MetaPath(mod), JsonSerializer.Serialize(onDisk));
        var parsed = JsonSerializer.Deserialize<ProteusMetadata>(File.ReadAllText(MetaPath(mod)))!;
        return new OverlayEntry(mod, mod, 0, true, parsed, Sidecar(mod));
    }

    private string? DiskDiffuse(string mod = "Skin")
        => SidecarDiscoveryService.TryReadMetadata(Path.Combine(mods, mod))?.ColorTableRows?[0].SubRowA?.Diffuse;

    private static void Recolour(OverlayEntry e, string diffuse) => e.Metadata.ColorTableRows![0].SubRowA!.Diffuse = diffuse;

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-1")]
    public void A_held_drag_never_touches_the_file_and_is_written_once_when_let_go()
    {
        // [AC-colorset-save-on-release-1]
        var entry = Mod(Meta("#000000"));
        var before = File.ReadAllBytes(MetaPath());
        var stamp = File.GetLastWriteTimeUtc(MetaPath());

        for (int i = 0; i < 60; i++)
        {
            Recolour(entry, $"#0000{i:X2}");
            discovery.StageMetadata(entry);
            discovery.FlushStaged(editing: true);
        }

        Assert.Equal(before, File.ReadAllBytes(MetaPath()));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(MetaPath()));
        Assert.Empty(Directory.GetFiles(Sidecar(), "*.tmp"));
        Assert.Empty(discovery.TakeWrittenRoots());

        discovery.FlushStaged(editing: false);
        Assert.Equal("#00003B", DiskDiffuse());
        Assert.Equal([Sidecar()], discovery.TakeWrittenRoots());

        var written = File.GetLastWriteTimeUtc(MetaPath());
        discovery.FlushStaged(editing: false);
        Assert.Equal(written, File.GetLastWriteTimeUtc(MetaPath()));
        Assert.Empty(discovery.TakeWrittenRoots());
    }

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-2")]
    [Trait("Plan", "EDGE-colorset-save-on-release-2")]
    public void Discovery_mid_drag_reads_the_held_edit_as_its_own_copy()
    {
        // [AC-colorset-save-on-release-2] [EDGE-colorset-save-on-release-2]
        var entry = Mod(Meta("#000000"));
        Recolour(entry, "#FF0000");
        discovery.StageMetadata(entry);

        var a = discovery.ReadForDiscovery("Skin", MetaPath())!;
        var b = discovery.ReadForDiscovery("Skin", MetaPath())!;

        Assert.Equal("#FF0000", a.ColorTableRows![0].SubRowA!.Diffuse);
        Assert.Equal("#000000", DiskDiffuse());
        Assert.NotSame(a, b);
        Assert.NotSame(entry.Metadata, a);
    }

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-3")]
    public void The_released_values_are_what_a_reload_reads()
    {
        // [AC-colorset-save-on-release-3]
        var entry = Mod(Meta("#000000"));
        entry.Metadata.OptionGroups![0].Options[0].ColorTableRows![0].SubRowA!.Diffuse = "#00FF00";
        entry.Metadata.MaskColorTableRows![0].SubRowA!.Diffuse = "#0000FF";
        discovery.StageMetadata(entry);

        discovery.FlushStaged(editing: false);

        var reloaded = SidecarDiscoveryService.TryReadMetadata(Path.Combine(mods, "Skin"))!;
        Assert.Equal("#00FF00", reloaded.OptionGroups![0].Options[0].ColorTableRows![0].SubRowA!.Diffuse);
        Assert.Equal("#0000FF", reloaded.MaskColorTableRows![0].SubRowA!.Diffuse);
    }

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-4")]
    public void A_discrete_edit_is_written_on_the_frame_it_is_made()
    {
        // [AC-colorset-save-on-release-4] — a checkbox or button holds nothing once its frame is over
        var entry = Mod(Meta("#000000"));
        Recolour(entry, "#123456");
        discovery.StageMetadata(entry);
        discovery.FlushStaged(editing: false);

        Assert.Equal("#123456", DiskDiffuse());
    }

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-5")]
    public void The_toe_density_under_a_preset_is_written_alone_and_once()
    {
        // [AC-colorset-save-on-release-5]
        var entry = Mod(Meta("#000000"));
        var edited = entry.Metadata.OptionGroups![0].Options[0].Overlays;
        edited[0].AsymmetricArt = true;   // a preview edit the preset made
        Recolour(entry, "#FFFFFF");       // and another
        var before = File.ReadAllBytes(MetaPath());

        for (int d = 10; d <= 45; d += 5)
        {
            edited[0].ToeCapDensity = d;
            Assert.True(discovery.StageToeCapDensity(entry, edited));
            discovery.FlushStaged(editing: true);
        }
        Assert.Equal(before, File.ReadAllBytes(MetaPath()));
        Assert.Equal(45, discovery.ReadForDiscovery("Skin", MetaPath())!.OptionGroups![0].Options[0].Overlays[0].ToeCapDensity);

        discovery.FlushStaged(editing: false);

        var disk = SidecarDiscoveryService.TryReadMetadata(Path.Combine(mods, "Skin"))!;
        var twin = disk.OptionGroups![0].Options[0].Overlays[0];
        Assert.Equal(45, twin.ToeCapDensity);
        Assert.Null(twin.AsymmetricArt);
        Assert.Equal("#000000", disk.ColorTableRows![0].SubRowA!.Diffuse);
        Assert.Single(discovery.TakeWrittenRoots());
    }

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-6")]
    [Trait("Plan", "EDGE-colorset-save-on-release-11")]
    public async System.Threading.Tasks.Task Another_save_writes_the_held_edit_first_and_then_its_own()
    {
        // [AC-colorset-save-on-release-6] [EDGE-colorset-save-on-release-11]
        var entry = Mod(Meta("#000000"));
        Recolour(entry, "#AAAAAA");
        discovery.StageMetadata(entry);

        var other = new OverlayEntry("Skin", "Skin", 0, true, Meta("#BBBBBB"), Sidecar());
        await System.Threading.Tasks.Task.Run(() => discovery.SaveMetadata(other));   // as IPC, off the draw thread

        Assert.Equal("#BBBBBB", DiskDiffuse());
        Assert.Equal([Sidecar(), Sidecar()], discovery.TakeWrittenRoots());

        // Nothing still held to land over it later.
        discovery.FlushAllStaged();
        Assert.Equal("#BBBBBB", DiskDiffuse());
        Assert.Empty(discovery.TakeWrittenRoots());
    }

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-6")]
    [Trait("Plan", "EDGE-colorset-save-on-release-12")]
    public void Closing_or_unloading_writes_what_is_held_even_mid_drag()
    {
        // [AC-colorset-save-on-release-6] [EDGE-colorset-save-on-release-12]
        var entry = Mod(Meta("#000000"));
        Recolour(entry, "#CCCCCC");
        discovery.StageMetadata(entry);

        discovery.FlushStaged(editing: true);
        Assert.Equal("#000000", DiskDiffuse());

        discovery.FlushAllStaged();
        Assert.Equal("#CCCCCC", DiskDiffuse());
    }

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-7")]
    [Trait("Plan", "EDGE-colorset-save-on-release-5")]
    public void The_defaults_are_the_file_before_the_drag_and_appear_only_once_written()
    {
        // [AC-colorset-save-on-release-7] [EDGE-colorset-save-on-release-5]
        var entry = Mod(Meta("#000000"));
        var original = File.ReadAllBytes(MetaPath());

        Recolour(entry, "#DDDDDD");
        discovery.StageMetadata(entry);
        discovery.FlushStaged(editing: true);
        Assert.False(discovery.HasDefaults(entry));

        discovery.FlushStaged(editing: false);

        Assert.True(discovery.HasDefaults(entry));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Sidecar(), "metadata.default.json")));
        Assert.Contains(Sidecar(), discovery.TakeWrittenRoots());
    }

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-8")]
    [Trait("Plan", "EDGE-colorset-save-on-release-7")]
    public void A_locked_file_keeps_the_edit_held_and_it_lands_on_a_later_try()
    {
        // [AC-colorset-save-on-release-8] [EDGE-colorset-save-on-release-7]
        var entry = Mod(Meta("#000000"));
        File.Copy(MetaPath(), Path.Combine(Sidecar(), "metadata.default.json"));   // keep the lock to the one file
        Recolour(entry, "#EEEEEE");
        discovery.StageMetadata(entry);

        using (new FileStream(MetaPath(), FileMode.Open, FileAccess.Read, FileShare.None))
            discovery.FlushStaged(editing: false);

        Assert.Equal("#000000", DiskDiffuse());
        Assert.Empty(discovery.TakeWrittenRoots());
        Assert.Equal("#EEEEEE", discovery.ReadForDiscovery("Skin", MetaPath())!.ColorTableRows![0].SubRowA!.Diffuse);

        discovery.FlushStaged(editing: false);   // inside the retry wait: not yet
        Assert.Equal("#000000", DiskDiffuse());

        discovery.FlushAllStaged();
        Assert.Equal("#EEEEEE", DiskDiffuse());
    }

    [Fact]
    [Trait("Plan", "EDGE-colorset-save-on-release-1")]
    public void Nothing_held_nothing_written()
    {
        // [EDGE-colorset-save-on-release-1]
        Mod(Meta("#000000"));
        var stamp = File.GetLastWriteTimeUtc(MetaPath());

        discovery.FlushStaged(editing: false);
        discovery.FlushAllStaged();

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(MetaPath()));
        Assert.Empty(discovery.TakeWrittenRoots());
    }

    [Fact]
    [Trait("Plan", "EDGE-colorset-save-on-release-4")]
    public void The_write_keeps_the_files_own_content_attributes()
    {
        // [EDGE-colorset-save-on-release-4]
        var entry = Mod(Meta("#000000"));
        Recolour(entry, "#101010");
        discovery.StageMetadata(entry);

        // The Parts tab adds a switch on disk after the edit was held.
        var disk = Meta("#000000");
        disk.ContentAttributes = [new ContentAttributeGroup { Group = "Straps" }];
        File.WriteAllText(MetaPath(), JsonSerializer.Serialize(disk));

        discovery.FlushStaged(editing: false);

        var written = SidecarDiscoveryService.TryReadMetadata(Path.Combine(mods, "Skin"))!;
        Assert.Equal("#101010", written.ColorTableRows![0].SubRowA!.Diffuse);
        Assert.Equal("Straps", Assert.Single(written.ContentAttributes!).Group);
    }

    [Fact]
    [Trait("Plan", "EDGE-colorset-save-on-release-6")]
    public void A_deleted_mod_is_not_brought_back_and_its_edit_is_given_up()
    {
        // [EDGE-colorset-save-on-release-6]
        var entry = Mod(Meta("#000000"));
        Recolour(entry, "#202020");
        discovery.StageMetadata(entry);
        Directory.Delete(Path.Combine(mods, "Skin"), recursive: true);

        Assert.Null(discovery.ReadForDiscovery("Skin", MetaPath()));
        for (int i = 0; i < MetadataWriteBehind.MaxFailures; i++) discovery.FlushAllStaged();

        Assert.False(Directory.Exists(Path.Combine(mods, "Skin")));
        Assert.Empty(discovery.TakeWrittenRoots());

        // Given up: a mod of that name coming back is not written over by it.
        Mod(Meta("#000000"));
        discovery.FlushAllStaged();
        Assert.Equal("#000000", DiskDiffuse());
    }

    [Fact]
    [Trait("Plan", "EDGE-colorset-save-on-release-8")]
    public void A_toe_option_that_moved_on_disk_is_not_saved()
    {
        // [EDGE-colorset-save-on-release-8]
        var entry = Mod(Meta("#000000"));
        var renamed = Meta("#000000");
        renamed.OptionGroups![0].Options[0].Name = "Velvet";
        File.WriteAllText(MetaPath(), JsonSerializer.Serialize(renamed));
        var before = File.ReadAllBytes(MetaPath());

        var edited = entry.Metadata.OptionGroups![0].Options[0].Overlays;
        edited[0].ToeCapDensity = 30;

        Assert.False(discovery.StageToeCapDensity(entry, edited));
        discovery.FlushAllStaged();
        Assert.Equal(before, File.ReadAllBytes(MetaPath()));
    }

    [Fact]
    [Trait("Plan", "EDGE-colorset-save-on-release-9")]
    public void An_edit_held_through_a_rename_is_written_into_the_new_folder()
    {
        // [EDGE-colorset-save-on-release-9]
        var entry = Mod(Meta("#000000"));
        Recolour(entry, "#303030");
        discovery.StageMetadata(entry);

        Directory.Move(Path.Combine(mods, "Skin"), Path.Combine(mods, "Skin v2"));   // Penumbra moves, then says so
        discovery.FollowStaged("Skin", "Skin v2");
        discovery.FlushStaged(editing: false);

        Assert.Equal("#303030", DiskDiffuse("Skin v2"));
        Assert.False(Directory.Exists(Path.Combine(mods, "Skin")));
    }

    // ── The ImGui wiring, which no test can drive: pinned in the source ──────────────────────────────────────────

    private static string Source(string file, [CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", file));

    /// <summary>The body of the first method whose signature contains <paramref name="signature"/>, braces matched.</summary>
    private static string Method(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {signature}");
        int open = source.IndexOf('{', at), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        throw new InvalidOperationException("unbalanced braces");
    }

    private static int Count(string s, string what)
    {
        int n = 0;
        for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-1")]
    [Trait("Plan", "AC-colorset-save-on-release-4")]
    [Trait("Plan", "EDGE-colorset-save-on-release-13")]
    public void The_tick_runs_every_frame_after_the_windows_and_waits_for_no_control_held()
    {
        // [AC-colorset-save-on-release-1] [AC-colorset-save-on-release-4] [EDGE-colorset-save-on-release-13]
        var draw = Method(Source("Plugin.cs"), "private void DrawUi()");
        int windows = draw.IndexOf("windowSystem.Draw();", StringComparison.Ordinal);
        int tick = draw.IndexOf("statusWindow.TickMetadataSaves();", StringComparison.Ordinal);
        Assert.True(windows >= 0 && tick > windows);

        var body = Method(Source(Path.Combine("Gui", "StatusWindow.cs")), "public void TickMetadataSaves()");
        Assert.Contains("discovery.FlushStaged(editing: ImGui.IsAnyItemActive())", body);
    }

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-7")]
    public void Written_sidecars_are_forgotten_by_the_defaults_cache_on_the_draw_thread()
    {
        // [AC-colorset-save-on-release-7]
        var src = Source(Path.Combine("Gui", "StatusWindow.cs"));
        Assert.Contains("ForgetWrittenDefaults();", Method(src, "public void TickMetadataSaves()"));
        Assert.Contains("ForgetWrittenDefaults();", Method(src, "public void FlushMetadataSaves()"));
        Assert.Contains("discovery.TakeWrittenRoots()", Method(src, "private void ForgetWrittenDefaults()"));
    }

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-1")]
    [Trait("Plan", "EDGE-colorset-save-on-release-14")]
    public void The_colour_panels_hold_their_edits_instead_of_saving()
    {
        // [AC-colorset-save-on-release-1] [EDGE-colorset-save-on-release-14]
        var src = Source(Path.Combine("Gui", "StatusWindow.cs"));
        Assert.DoesNotContain("SaveToeCapDensity", src);
        Assert.Equal(4, Count(src, "discovery.StageMetadata(entry)"));
        Assert.Equal(2, Count(src, "discovery.StageToeCapDensity(entry,"));
        // The one direct save left is the discrete Geometry checkbox.
        Assert.Equal(1, Count(src, "discovery.SaveMetadata("));
        Assert.Contains("discovery.SaveMetadata(", Method(src, "private void DrawBustBridgeAdvanced("));
        // Preset/design edits still only hold a reset or the toe: the gates are unchanged.
        Assert.Equal(2, Count(src, "if (!overrideActive || reset"));
    }

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-2")]
    public void Discovery_reads_through_what_is_held()
    {
        // [AC-colorset-save-on-release-2]
        var discover = Method(Source(Path.Combine("Services", "SidecarDiscoveryService.cs")),
                              "private List<OverlayEntry> Discover(bool enabledOnly)");
        Assert.Contains("ReadForDiscovery(modDir, metaPath)", discover);
        Assert.DoesNotContain("TryParseMetadata(", discover);
    }

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-6")]
    [Trait("Plan", "EDGE-colorset-save-on-release-12")]
    public void Closing_the_window_and_unloading_the_plugin_write_what_is_held()
    {
        // [AC-colorset-save-on-release-6] [EDGE-colorset-save-on-release-12]
        Assert.Contains("FlushMetadataSaves();",
                        Method(Source(Path.Combine("Gui", "StatusWindow.cs")), "public override void OnClose()"));

        var dispose = Method(Source("Plugin.cs"), "private void DisposeServices()");
        int flush = dispose.IndexOf("statusWindow.FlushMetadataSaves()", StringComparison.Ordinal);
        int unhook = dispose.IndexOf("UiBuilder.Draw -= DrawUi", StringComparison.Ordinal);
        Assert.True(flush >= 0 && unhook > flush);
    }

    [Fact]
    [Trait("Plan", "EDGE-colorset-save-on-release-9")]
    public void A_rename_moves_what_is_held()
    {
        // [EDGE-colorset-save-on-release-9]
        Assert.Contains("discovery.FollowStaged(oldDir, newDir)",
                        Method(Source(Path.Combine("Gui", "StatusWindow.cs")), "private void FollowMove("));
    }
}
