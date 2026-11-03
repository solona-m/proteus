using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Penumbra.Api.Enums;
using Proteus.Gui;
using Proteus.Interop;
using Proteus.Localization;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The Colors window's "Cap the toes" tick (<see cref="ToeCapToggle"/>): which mods get it, what it reads from
/// Penumbra, what a click writes back, and when it says the cap can't show.
/// </summary>
public sealed partial class ToeCapToggleTests : IDisposable
{
    private const string BodyPath = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl";
    private const string FacePath = "chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_fac_a.mtrl";
    private const string GearPath = "chara/weapon/w0801/obj/body/b0006/material/v0001/mt_w0801b0006_a.mtrl";
    private const string TopSkin  = "chara/equipment/e0000/material/v0001/mt_c0201e0000_top_bibo.mtrl";

    private readonly string root = Path.Combine(Path.GetTempPath(), "proteus-toecap-" + Guid.NewGuid().ToString("N"));
    private string ModRoot => root;
    private string SidecarRoot => Path.Combine(root, "Proteus");

    public ToeCapToggleTests() => Directory.CreateDirectory(Path.Combine(SidecarRoot, "Masks"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
    }

    // ── fixtures ────────────────────────────────────────────────────────────

    private void WriteMeta(string groupName, string type, params string[] options)
    {
        var opts = string.Join(",", options.Select(o => $"{{\"Name\":\"{o}\"}}"));
        File.WriteAllText(Path.Combine(ModRoot, "meta.json"),
            $"{{\"FileVersion\":3,\"Groups\":[{{\"Name\":\"Fabric\",\"Type\":\"Single\",\"Options\":[{{\"Name\":\"Silk\"}}]}},"
          + $"{{\"Name\":\"{groupName}\",\"Type\":\"{type}\",\"Options\":[{opts}]}}]}}");
    }

    private void WriteMap(string option, string ext = ".png")
        => File.WriteAllBytes(Path.Combine(SidecarRoot, "Masks", option.Trim() + ext), [0]);

    private static PenumbraBridge.ModSettingsSnapshot Snap(Dictionary<string, List<string>> options,
        bool enabled = true, bool inherited = false, bool temporary = false)
        => new(enabled, 0, options, inherited, temporary);

    private static Dictionary<string, List<string>> Sel(string group, params string[] options)
        => new() { [group] = [.. options] };

    private static ToeCapOptionRef Cap(params string[] capped) => new("Masks", capped[0], capped, true);

    private static List<ColorTableRowPreset> PrintRows()
        => [new ColorTableRowPreset { Row = 16, SubRowA = new ColorTableSubRowPreset { Blend = RowBlend.Multiply } }];

    private static List<ColorTableRowPreset> PaintRows()
        => [new ColorTableRowPreset { Row = 16, SubRowA = new ColorTableSubRowPreset { Blend = RowBlend.Paint } }];

    private static OverlayDescriptor Body(bool pinned = false, OverlayLayer layer = OverlayLayer.Skin,
        float? strength = null, params string[] paths)
        => new() { Layer = layer, ManualShaderLock = pinned, ToeCapStrength = strength,
                   MaterialGamePaths = paths.Length == 0 ? [BodyPath] : [.. paths] };

    private static ProteusMetadata Simple(params OverlayDescriptor[] overlays) => new() { Overlays = [.. overlays] };

    private static ProteusMetadata Grouped(params (string Name, OverlayDescriptor[] Overlays)[] options)
        => new()
        {
            OptionGroups =
            [
                new OverlayOptionGroup
                {
                    PenumbraGroupName = "Design",
                    Options = options.Select(o => new OverlayOption { Name = o.Name, Overlays = [.. o.Overlays] }).ToList(),
                },
            ],
        };

    // ── finding the option ──────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-1")]
    public void Find_returns_the_masks_option_with_a_map()
    {
        WriteMeta("Masks", "Multi", "Sleeves", "Toe Cap");
        WriteMap("Toe Cap");
        var found = SidecarDiscoveryService.FindToeCapOption(ModRoot, SidecarRoot);
        Assert.NotNull(found);
        Assert.Equal("Masks", found!.Group);
        Assert.Equal("Toe Cap", found.Option);
        Assert.Equal(["Toe Cap"], found.CappedOptions);
    }

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-1")]
    public void First_selected_rule_is_shared()
    {
        Assert.Equal("toe-cap", SidecarDiscoveryService.FirstSelectedToeCap(["Seam", "toe-cap", "Toe Cap"]));
        Assert.Null(SidecarDiscoveryService.FirstSelectedToeCap(null));
        Assert.Null(SidecarDiscoveryService.FirstSelectedToeCap(["Seam"]));
    }

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-4")]
    public void View_shows_the_flag_tick_without_a_usable_option()
    {
        Assert.Equal(new ToeCapTickView(true, false, false, false, ToeCapMode.Flag, false),
                     ToeCapToggle.View(null, Snap(Sel("Masks", "Toe Cap")), false));
        Assert.Equal(new ToeCapTickView(true, true, false, false, ToeCapMode.Flag, false),
                     ToeCapToggle.View(null, Snap(Sel("Masks")), true));

        Assert.Null(SidecarDiscoveryService.FindToeCapOption(ModRoot, SidecarRoot));   // no meta.json
        Assert.Null(SidecarDiscoveryService.FindToeCapOption(null, SidecarRoot));
        WriteMap("Toe Cap");
        WriteMeta("Patterns", "Multi", "Toe Cap");
        Assert.Null(SidecarDiscoveryService.FindToeCapOption(ModRoot, SidecarRoot));   // no Masks group
        WriteMeta("Masks", "Multi", "Sleeves");
        Assert.Null(SidecarDiscoveryService.FindToeCapOption(ModRoot, SidecarRoot));   // no toe-cap option
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-1")]
    public void Find_matches_spelling_variants_and_returns_the_manifest_name()
    {
        WriteMeta("Masks", "Multi", "ToeCap", "toe-cap ");
        WriteMap("ToeCap");
        WriteMap("toe-cap");
        var found = SidecarDiscoveryService.FindToeCapOption(ModRoot, SidecarRoot)!;
        Assert.Equal("ToeCap", found.Option);
        Assert.Equal(["ToeCap", "toe-cap "], found.CappedOptions);
        Assert.Empty(ToeCapToggle.SelectionAfter(["toe-cap "], "ToeCap", false));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-2")]
    public void Find_is_null_when_the_map_is_missing()
    {
        WriteMeta("Masks", "Multi", "Sleeves", "Toe Cap");
        WriteMap("Sleeves");
        Assert.Null(SidecarDiscoveryService.FindToeCapOption(ModRoot, SidecarRoot));
    }

    [Theory]
    [Trait("Plan", "EDGE-toecap-geometry-3")]
    [InlineData(".dds")]
    [InlineData(".tex")]
    public void Find_accepts_a_dds_or_tex_map(string ext)
    {
        WriteMeta("Masks", "Multi", "Toe Cap");
        WriteMap("Toe Cap", ext);
        Assert.NotNull(SidecarDiscoveryService.FindToeCapOption(ModRoot, SidecarRoot));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-4")]
    public void Find_marks_a_single_select_cap_read_only()
    {
        WriteMeta("Masks", "Single", "Sleeves", "Toe Cap");
        WriteMap("Toe Cap");
        var found = SidecarDiscoveryService.FindToeCapOption(ModRoot, SidecarRoot)!;
        Assert.Equal("Toe Cap", found.Option);
        Assert.False(found.Writable);
        WriteMeta("Masks", "Multi", "Sleeves", "Toe Cap");
        Assert.True(SidecarDiscoveryService.FindToeCapOption(ModRoot, SidecarRoot)!.Writable);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-5")]
    public void Group_name_case_is_ignored_and_kept()
    {
        WriteMeta("masks", "multi", "Toe Cap");
        WriteMap("Toe Cap");
        var found = SidecarDiscoveryService.FindToeCapOption(ModRoot, SidecarRoot)!;
        Assert.Equal("masks", found.Group);
        Assert.True(ToeCapToggle.View(found, Snap(Sel("MASKS", "Toe Cap")), false).On);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-13")]
    public void Find_reads_a_legacy_group_file()
    {
        File.WriteAllText(Path.Combine(ModRoot, "group_001_masks.json"),
            "{\"Name\":\"Masks\",\"Type\":\"Multi\",\"Options\":[{\"Name\":\"Seam\"},{\"Name\":\"Toe Cap\"}]}");
        WriteMap("Toe Cap");
        var found = SidecarDiscoveryService.FindToeCapOption(ModRoot, SidecarRoot);
        Assert.NotNull(found);
        Assert.Equal("Toe Cap", found!.Option);
        Assert.True(found.Writable);

        File.WriteAllText(Path.Combine(ModRoot, "group_001_masks.json"),
            "{\"Name\":\"Masks\",\"Type\":\"Single\",\"Options\":[{\"Name\":\"Toe Cap\"}]}");
        Assert.False(SidecarDiscoveryService.FindToeCapOption(ModRoot, SidecarRoot)!.Writable);
    }

    // ── the tick's state ────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-1")]
    [Trait("Plan", "EDGE-toecap-geometry-14")]
    public void View_is_on_exactly_when_the_compositor_would_cap()
    {
        var on = ToeCapToggle.View(Cap("Toe Cap"), Snap(Sel("Masks", "Sleeves", "Toe Cap")), false);
        Assert.Equal(new ToeCapTickView(true, true, false, false, ToeCapMode.Option, false), on);
        var off = ToeCapToggle.View(Cap("Toe Cap"), Snap(Sel("Masks", "Sleeves")), false);
        Assert.Equal(new ToeCapTickView(true, false, false, false, ToeCapMode.Option, false), off);
    }

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-5")]
    public void View_is_held_under_a_temporary_setting()
    {
        Assert.Equal(new ToeCapTickView(true, true, true, false, ToeCapMode.Option, false),
                     ToeCapToggle.View(Cap("Toe Cap"), Snap(Sel("Masks", "Toe Cap"), temporary: true), false));
        Assert.Equal(new ToeCapTickView(true, false, true, false, ToeCapMode.Option, false),
                     ToeCapToggle.View(Cap("Toe Cap"), Snap(Sel("Masks"), temporary: true), false));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-7")]
    public void View_is_hidden_without_settings()
        => Assert.False(ToeCapToggle.View(Cap("Toe Cap"), null, false).Visible);

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-15")]
    public void View_reports_inherited_settings()
    {
        var v = ToeCapToggle.View(Cap("Toe Cap"), Snap(Sel("Masks", "Toe Cap"), inherited: true), false);
        Assert.True(v.Visible);
        Assert.True(v.Inherited);
        Assert.False(v.Held);
        Assert.False(ToeCapToggle.View(Cap("Toe Cap"), Snap(Sel("Masks")), false).Inherited);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-16")]
    public void Untouched_masks_group_reads_off_and_ticks_to_the_cap()
    {
        var options = Sel("Fabric", "Silk");
        var v = ToeCapToggle.View(Cap("Toe Cap"), Snap(options), false);
        Assert.True(v.Visible);
        Assert.False(v.On);
        Assert.Equal(["Toe Cap"], ToeCapToggle.SelectionAfter(ToeCapToggle.GroupSelection(options, "Masks"), "Toe Cap", true));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-9")]
    public void Two_matching_options_follow_the_compositor()
    {
        WriteMeta("Masks", "Multi", "ToeCap", "Toe Cap");
        WriteMap("Toe Cap");
        var found = SidecarDiscoveryService.FindToeCapOption(ModRoot, SidecarRoot)!;
        Assert.Equal("Toe Cap", found.Option);
        Assert.Equal(["Toe Cap"], found.CappedOptions);

        Assert.False(ToeCapToggle.View(found, Snap(Sel("Masks", "ToeCap", "Toe Cap")), false).On);
        Assert.True(ToeCapToggle.View(found, Snap(Sel("Masks", "Toe Cap")), false).On);
        Assert.Equal(["Seam", "Toe Cap"], ToeCapToggle.SelectionAfter(["ToeCap", "Seam", "Toe Cap"], "Toe Cap", true));
        Assert.Equal(["Seam"], ToeCapToggle.SelectionAfter(["ToeCap", "Seam", "Toe Cap"], "Toe Cap", false));
    }

    // ── what a click writes ─────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-2")]
    public void Ticking_adds_the_cap_and_keeps_every_other_mask()
    {
        List<string> current = ["Seam", "Thigh Band"];
        Assert.Equal(["Seam", "Thigh Band", "Toe Cap"], ToeCapToggle.SelectionAfter(current, "Toe Cap", true));
        Assert.Equal(["Seam", "Thigh Band"], current);
    }

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-3")]
    public void Unticking_removes_only_the_cap()
        => Assert.Equal(["Seam", "Thigh Band"],
                        ToeCapToggle.SelectionAfter(["Seam", "Toe Cap", "Thigh Band"], "Toe Cap", false));

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-6")]
    public void Cap_alone_and_empty_selections()
    {
        Assert.Equal(["Toe Cap"], ToeCapToggle.SelectionAfter([], "Toe Cap", true));
        Assert.Equal(["Toe Cap"], ToeCapToggle.SelectionAfter(null, "Toe Cap", true));
        Assert.Empty(ToeCapToggle.SelectionAfter(["Toe Cap"], "Toe Cap", false));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-10")]
    public void Selection_drops_blanks_and_duplicates_without_mutating()
    {
        List<string> current = ["Seam", "", "seam", "Toe Cap"];
        Assert.Equal(["Seam", "Toe Cap"], ToeCapToggle.SelectionAfter(current, "Toe Cap", true));
        Assert.Equal(4, current.Count);
    }

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-6")]
    public void Only_success_and_nothing_changed_count_as_written()
    {
        Assert.True(ToeCapToggle.Succeeded(PenumbraApiEc.Success));
        Assert.True(ToeCapToggle.Succeeded(PenumbraApiEc.NothingChanged));
        foreach (var ec in new[] { PenumbraApiEc.ModMissing, PenumbraApiEc.OptionGroupMissing, PenumbraApiEc.OptionMissing,
                                   PenumbraApiEc.CollectionMissing, PenumbraApiEc.SystemDisposed, PenumbraApiEc.UnknownError })
            Assert.False(ToeCapToggle.Succeeded(ec), ec.ToString());
        Assert.Contains("OptionMissing", string.Format(new ColorsStrings().ToeCapFailedFmt, PenumbraApiEc.OptionMissing));
    }

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-7")]
    public void Tick_strings_are_present()
    {
        var cs = new ColorsStrings();
        foreach (var s in new[] { cs.ToeCap, cs.ToeCapTip, cs.ToeCapHeldTip, cs.ToeCapInheritedNote,
                                  cs.ToeCapFailedFmt, cs.ToeCapModOff, cs.ToeCapNoShell,
                                  cs.ToeCapFlagTip, cs.ToeCapLockedTip, cs.ToeCapSaveFailed })
            Assert.False(string.IsNullOrWhiteSpace(s));
        Assert.Contains("{0}", cs.ToeCapFailedFmt);
    }

    // ── the availability cache ──────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-11")]
    public void Cache_reads_once_until_forgotten()
    {
        WriteMeta("Masks", "Multi", "Toe Cap");
        WriteMap("Toe Cap");
        var cache = new ToeCapOptionCache();
        Assert.NotNull(cache.Get("ModA", ModRoot, SidecarRoot));
        File.Delete(Path.Combine(SidecarRoot, "Masks", "Toe Cap.png"));
        Assert.NotNull(cache.Get("ModA", ModRoot, SidecarRoot));
        cache.Forget("ModA");
        Assert.Null(cache.Get("ModA", ModRoot, SidecarRoot));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-12")]
    public void Cache_is_keyed_by_folder_case_insensitively()
    {
        WriteMeta("Masks", "Multi", "Toe Cap");
        WriteMap("Toe Cap");
        var cache = new ToeCapOptionCache();
        Assert.NotNull(cache.Get("ModA", ModRoot, SidecarRoot));
        File.Delete(Path.Combine(SidecarRoot, "Masks", "Toe Cap.png"));
        Assert.NotNull(cache.Get("moda", ModRoot, SidecarRoot));      // a hit, not a rescan
        Assert.Null(cache.Get("ModB", ModRoot, SidecarRoot));         // a new folder is scanned fresh
        cache.Forget("MODA");
        Assert.Null(cache.Get("ModA", ModRoot, SidecarRoot));
    }

    // ── will the cap show? ──────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-8")]
    public void Reach_draws_for_an_unpinned_skin_or_gear_body_overlay()
    {
        Assert.Equal(ToeCapReach.Draws, ToeCapToggle.Reach(Simple(Body()), true, null, false));
        Assert.Equal(ToeCapReach.Draws, ToeCapToggle.Reach(Simple(Body(pinned: true, layer: OverlayLayer.Gear)), true, null, false));
    }

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-8")]
    [Trait("Plan", "EDGE-toecap-geometry-8")]
    public void Reach_is_mod_off_when_disabled()
    {
        Assert.Equal(ToeCapReach.ModOff, ToeCapToggle.Reach(Simple(Body()), false, null, false));
        var v = ToeCapToggle.View(Cap("Toe Cap"), Snap(Sel("Masks", "Toe Cap"), enabled: false), false);
        Assert.True(v.Visible);
        Assert.False(v.Held);
    }

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-8")]
    public void Reach_draws_when_any_overlay_carries_the_cap()
    {
        var md = Grouped(("Pinned", [Body(pinned: true)]), ("Loose", [Body()]));
        Assert.Equal(ToeCapReach.Draws, ToeCapToggle.Reach(md, true, Sel("Design", "Pinned", "Loose"), false));
        Assert.Equal(ToeCapReach.NoShell, ToeCapToggle.Reach(md, true, Sel("Design", "Pinned"), false));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-17")]
    public void Reach_is_unknown_under_a_binding()
    {
        Assert.Equal(ToeCapReach.Unknown, ToeCapToggle.Reach(Simple(Body(pinned: true)), true, null, true));
        Assert.Equal(ToeCapReach.Unknown, ToeCapToggle.Reach(Simple(Body()), false, null, true));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-18")]
    public void Reach_pinned_skin()
    {
        Assert.Equal(ToeCapReach.NoShell, ToeCapToggle.Reach(Simple(Body(pinned: true)), true, null, false));
        var md = Simple(Body(pinned: true));
        md.BustBridge = true;
        Assert.Equal(ToeCapReach.Draws, ToeCapToggle.Reach(md, true, null, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-19")]
    public void Reach_print_never_draws()
    {
        var simple = Simple(Body());
        simple.ColorTableRows = PrintRows();
        Assert.Equal(ToeCapReach.NoShell, ToeCapToggle.Reach(simple, true, null, false));
        simple.BustBridge = true;
        Assert.Equal(ToeCapReach.NoShell, ToeCapToggle.Reach(simple, true, null, false));

        // An option with no rows of its own takes the mod's print rows; its own non-print rows win.
        var grouped = Grouped(("Print", [Body()]));
        grouped.ColorTableRows = PrintRows();
        Assert.Equal(ToeCapReach.NoShell, ToeCapToggle.Reach(grouped, true, Sel("Design", "Print"), false));
        grouped.OptionGroups![0].Options[0].ColorTableRows = PaintRows();
        Assert.Equal(ToeCapReach.Draws, ToeCapToggle.Reach(grouped, true, Sel("Design", "Print"), false));
    }

    [Theory]
    [Trait("Plan", "EDGE-toecap-geometry-20")]
    [InlineData(OverlayLayer.Skin, (int)ToeCapReach.NoShell, GearPath)]
    [InlineData(OverlayLayer.Gear, (int)ToeCapReach.NoShell, GearPath)]
    [InlineData(OverlayLayer.Skin, (int)ToeCapReach.NoShell, FacePath)]
    [InlineData(OverlayLayer.Skin, (int)ToeCapReach.NoShell, FacePath, BodyPath)]
    [InlineData(OverlayLayer.Skin, (int)ToeCapReach.Draws, BodyPath, FacePath)]
    [InlineData(OverlayLayer.Gear, (int)ToeCapReach.Draws, BodyPath)]
    public void Reach_counts_only_body_surfaces(OverlayLayer layer, int want, params string[] paths)
        => Assert.Equal((ToeCapReach)want, ToeCapToggle.Reach(Simple(Body(layer: layer, paths: paths)), true, null, false));

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-20")]
    public void Reach_counts_an_overlay_naming_no_material_as_body()
    {
        var d = new OverlayDescriptor { Layer = OverlayLayer.Skin, MaterialGamePaths = [] };
        Assert.Equal(ToeCapReach.Draws, ToeCapToggle.Reach(Simple(d), true, null, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-20")]
    public void OnBodySurface_mirrors_SurfaceKeyOf()
    {
        Assert.True(ToeCapToggle.OnBodySurface(null));
        Assert.True(ToeCapToggle.OnBodySurface([]));
        Assert.True(ToeCapToggle.OnBodySurface([BodyPath]));
        Assert.False(ToeCapToggle.OnBodySurface([FacePath]));
        Assert.False(ToeCapToggle.OnBodySurface([FacePath, BodyPath]));
        Assert.True(ToeCapToggle.OnBodySurface([BodyPath, FacePath]));
        Assert.True(ToeCapToggle.OnBodySurface([TopSkin]));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-21")]
    public void Reach_counts_only_selected_options()
    {
        var md = Grouped(("Off None", []), ("Loose", [Body()]));
        Assert.Equal(ToeCapReach.NoShell, ToeCapToggle.Reach(md, true, Sel("Other", "x"), false));
        Assert.Equal(ToeCapReach.NoShell, ToeCapToggle.Reach(md, true, Sel("Design", "Off None"), false));
        Assert.Equal(ToeCapReach.Draws, ToeCapToggle.Reach(md, true, Sel("Design", "Loose"), false));
    }

    /// <summary>A mod with its own overlays draws those whatever its groups select, as the compositor does.</summary>
    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-21")]
    public void Reach_prefers_the_mods_own_overlays_over_its_options()
    {
        var md = Grouped(("Loose", [Body()]));
        md.Overlays = [Body(pinned: true)];
        Assert.Equal(ToeCapReach.NoShell, ToeCapToggle.Reach(md, true, Sel("Design", "Loose"), false));
        md.Overlays = [Body()];
        Assert.Equal(ToeCapReach.Draws, ToeCapToggle.Reach(md, true, Sel("Other", "x"), false));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-22")]
    public void Reach_is_unknown_when_a_mask_shell_is_possible()
    {
        Dictionary<string, List<string>> With(params string[] masks) => Sel("Masks", masks);

        // (a) an all-skin mod whose masks render as cloth, another mask selected
        var a = Simple(Body(pinned: true));
        a.MaskDescriptor = new OverlayDescriptor { Layer = OverlayLayer.Gear };
        Assert.Equal(ToeCapReach.Unknown, ToeCapToggle.Reach(a, true, With("Seam", "Toe Cap"), false));
        // (b) only the cap itself is selected; a blank entry is no mask
        Assert.Equal(ToeCapReach.NoShell, ToeCapToggle.Reach(a, true, With("Toe Cap"), false));
        Assert.Equal(ToeCapReach.NoShell, ToeCapToggle.Reach(a, true, With(" ", "Toe Cap"), false));
        // (c) a shell off the body, another mask selected
        var c = Simple(Body(layer: OverlayLayer.Gear, paths: FacePath));
        c.MaskDescriptor = new OverlayDescriptor { Layer = OverlayLayer.Skin };
        Assert.Equal(ToeCapReach.Unknown, ToeCapToggle.Reach(c, true, With("Seam"), false));
        // (d) the same with no mask selected
        Assert.Equal(ToeCapReach.NoShell, ToeCapToggle.Reach(c, true, With(), false));
        // (e) no mask descriptor, only a pinned body overlay
        Assert.Equal(ToeCapReach.NoShell, ToeCapToggle.Reach(Simple(Body(pinned: true)), true, With("Seam"), false));
        // (f) a body garment with the cap off still gets a mask shell, which is capped at full strength
        var f = Simple(Body(layer: OverlayLayer.Gear, strength: 0f));
        Assert.Equal(ToeCapReach.Unknown, ToeCapToggle.Reach(f, true, With("Seam"), false));
        Assert.Equal(ToeCapReach.NoShell, ToeCapToggle.Reach(f, true, With("Toe Cap"), false));
    }

    [Theory]
    [Trait("Plan", "EDGE-toecap-geometry-23")]
    [InlineData(0f, (int)ToeCapReach.NoShell)]
    [InlineData(-1f, (int)ToeCapReach.NoShell)]
    [InlineData(null, (int)ToeCapReach.Draws)]
    [InlineData(0.5f, (int)ToeCapReach.Draws)]
    public void Reach_skips_overlays_with_the_cap_off(float? strength, int want)
        => Assert.Equal((ToeCapReach)want, ToeCapToggle.Reach(Simple(Body(strength: strength)), true, null, false));
}
