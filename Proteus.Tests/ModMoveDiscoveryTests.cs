using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The mod list and the compositor's per-run caches following a rename: <see cref="CompositorService"/>'s
/// pure helpers and <see cref="ModMoveLedger"/>.</summary>
public class ModMoveDiscoveryTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "mods");

    private static OverlayEntry Entry(string dir, string? name = null, int priority = 0, bool enabled = true)
        => new(dir, name ?? dir, priority, enabled, new ProteusMetadata(),
               Path.Combine(Root, dir, SidecarDiscoveryService.SidecarSubdir));

    private static List<OverlayEntry> List() => [Entry("A", priority: 1), Entry("Old", "Old name", 2), Entry("C", priority: 3)];

    [Fact]
    [Trait("Plan", "AC-mod-rename-1")]
    public void MoveEntry_renames_the_entry_once_with_new_folder_name_and_sidecar()
    {
        var moved = CompositorService.MoveEntry(List(), "Old", "New", Root, "New name")!;

        Assert.Equal(["A", "New", "C"], moved.Select(e => e.ModDirectory));
        var e = moved[1];
        Assert.Equal("New name", e.ModName);
        Assert.Equal(Path.Combine(Root, "New", "Proteus"), e.SidecarRoot);
        Assert.Equal(2, e.Priority);
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-1")]
    public void MoveEntry_drops_a_stale_entry_for_the_new_folder()
    {
        var moved = CompositorService.MoveEntry([Entry("Old"), Entry("New", "stale")], "Old", "New", Root, null)!;
        Assert.Single(moved);
        Assert.Equal("New", moved[0].ModDirectory);
        Assert.Equal("Old", moved[0].ModName);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-17")]
    public void A_folder_counts_as_vanished_only_on_two_probes_in_a_row()
    {
        Assert.Empty(CompositorService.StillMissing([], ["Old"]));
        Assert.Equal(["Old"], CompositorService.StillMissing(["old"], ["Old"]));
        Assert.Empty(CompositorService.StillMissing(["Old"], []));
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-1")]
    public void MoveEntry_keeps_the_name_when_penumbra_gave_none()
        => Assert.Equal("Old name", CompositorService.MoveEntry(List(), "Old", "New", Root, null)![1].ModName);

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-13")]
    public void MoveEntry_keeps_metadata_reference_and_points_sidecar_into_new_folder()
    {
        var list = List();
        var moved = CompositorService.MoveEntry(list, "Old", "New", Root, null)!;

        Assert.Same(list[1].Metadata, moved[1].Metadata);
        Assert.Equal(Path.Combine(Root, "New", "Proteus", "metadata.json"), Path.Combine(moved[1].SidecarRoot, "metadata.json"));
        Assert.Equal(Path.Combine(Root, "New"), moved[1].ModRoot);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-6")]
    public void MoveEntry_keeps_disabled_flag()
    {
        var moved = CompositorService.MoveEntry([Entry("Old", enabled: false)], "Old", "New", Root, null)!;
        Assert.False(moved.Single().Enabled);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-12")]
    public void MoveEntry_on_empty_list_returns_null()
    {
        Assert.Null(CompositorService.MoveEntry([], "Old", "New", Root, null));
        Assert.Null(CompositorService.MoveEntry(List(), "Missing", "New", Root, null));
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-2")]
    public void WithNames_updates_only_changed_names_and_keeps_metadata()
    {
        var list = List();
        var names = new Dictionary<string, string> { ["A"] = "A", ["Old"] = "Renamed", ["C"] = "C" };

        var next = CompositorService.WithNames(list, names, out var vanished)!;

        Assert.Empty(vanished);
        Assert.Equal("Renamed", next[1].ModName);
        Assert.Same(list[1].Metadata, next[1].Metadata);
        Assert.Same(list[0], next[0]);
        Assert.Same(list[2], next[2]);
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-2")]
    public void WithNames_returns_null_when_nothing_changed()
    {
        var names = new Dictionary<string, string> { ["A"] = "A", ["Old"] = "Old name", ["C"] = "C" };
        Assert.Null(CompositorService.WithNames(List(), names, out var vanished));
        Assert.Empty(vanished);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-17")]
    [Trait("Plan", "EDGE-mod-rename-32")]
    public void WithNames_flags_a_vanished_folder()
    {
        var names = new Dictionary<string, string> { ["a"] = "A", ["C"] = "C" };
        CompositorService.WithNames(List(), names, out var vanished);
        Assert.Equal(["Old"], vanished);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-17")]
    public void WithNames_with_empty_names_publishes_nothing()
    {
        Assert.Null(CompositorService.WithNames(List(), new Dictionary<string, string>(), out var vanished));
        Assert.Empty(vanished);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-30")]
    [Trait("Plan", "EDGE-mod-rename-32")]
    public void WithoutDirs_drops_vanished_entries()
        => Assert.Equal(["A", "C"], CompositorService.WithoutDirs(List(), ["old"]).Select(e => e.ModDirectory));

    [Fact]
    [Trait("Plan", "AC-mod-rename-6")]
    [Trait("Plan", "EDGE-mod-rename-10")]
    public void MoveNeedsComposite_true_for_enabled_entry()
        => Assert.True(CompositorService.MoveNeedsComposite(Entry("Old"), null, Root, "Old"));

    [Fact]
    [Trait("Plan", "AC-mod-rename-6")]
    [Trait("Plan", "EDGE-mod-rename-6")]
    public void MoveNeedsComposite_false_for_disabled_entry_with_no_bases()
    {
        var bases = new Dictionary<string, string> { ["chara/skin.tex"] = Path.Combine(Root, "Else", "skin.tex") };
        Assert.False(CompositorService.MoveNeedsComposite(Entry("Old", enabled: false), bases, Root, "Old"));
        Assert.False(CompositorService.MoveNeedsComposite(null, null, Root, "Old"));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-20")]
    public void MoveNeedsComposite_true_when_a_base_lived_in_old_folder()
    {
        var bases = new Dictionary<string, string> { ["chara/skin.tex"] = Path.Combine(Root, "Body", "skin.tex") };
        Assert.True(CompositorService.MoveNeedsComposite(null, bases, Root, "Body"));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-9")]
    public void MoveNeedsComposite_is_independent_of_auto_redraw()
    {
        // The verdict takes no setting at all: whether the composite it asks for runs is RedrawHold.Admit's to say.
        var parameters = typeof(CompositorService).GetMethod(nameof(CompositorService.MoveNeedsComposite),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetParameters();
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(Configuration) || p.ParameterType == typeof(bool));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-20")]
    public void ModFolderOf_static_matches_instance_semantics()
    {
        Assert.Equal("Body", CompositorService.ModFolderOf(Path.Combine(Root, "Body", "a", "b.tex"), Root));
        Assert.Null(CompositorService.ModFolderOf(@"C:\elsewhere\b.tex", Root));
        Assert.Null(CompositorService.ModFolderOf(null, Root));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-11")]
    public void TryPublish_refuses_when_list_was_replaced()
    {
        var expected = List();
        var field = expected;
        Assert.True(CompositorService.TryPublish(ref field, expected, []));

        var newer = List();
        field = newer;
        Assert.False(CompositorService.TryPublish(ref field, expected, []));
        Assert.Same(newer, field);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-23")]
    public void ProbeDue_throttles_to_interval()
    {
        Assert.False(CompositorService.ProbeDue(1_999, 0, 2000));
        Assert.True(CompositorService.ProbeDue(2_000, 0, 2000));
    }

    // ── the ledger ───────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-10")]
    [Trait("Plan", "EDGE-mod-rename-11")]
    public void Ledger_remaps_a_list_published_by_an_earlier_run()
    {
        var ledger = new ModMoveLedger();
        ledger.Record("Old", "New", "New name", now: 0);

        var remapped = ModMoveLedger.Remap(List(), ledger.Live(10), Root);

        Assert.Equal(["A", "New", "C"], remapped.Select(e => e.ModDirectory));
        Assert.Equal("New name", remapped[1].ModName);
        Assert.Equal(Path.Combine(Root, "New", "Proteus"), remapped[1].SidecarRoot);
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-6")]
    [Trait("Plan", "EDGE-mod-rename-9")]
    [Trait("Plan", "EDGE-mod-rename-25")]
    public void Per_run_caches_are_remapped_without_a_composite()
    {
        var ledger = new ModMoveLedger();
        ledger.Record("Old", "New", null, now: 0);
        var moves = ledger.Live(1);

        var inert = ModMoveLedger.RemapKeys(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Old"] = 1, ["A"] = 2 }, moves);
        var shell = ModMoveLedger.RemapShell(new Dictionary<(string ModDir, string? Group, string? Option), int>
        {
            [("Old", "g", "o")] = 3,
        }, moves);
        var bases = ModMoveLedger.RemapPathValues(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["chara/skin.tex"] = Path.Combine(Root, "Old", "skin.tex"),
            ["chara/other.tex"] = Path.Combine(Root, "A", "other.tex"),
        }, moves, Root);

        Assert.Equal(1, inert["New"]);
        Assert.Equal(2, inert["A"]);
        Assert.False(inert.ContainsKey("Old"));
        Assert.Equal(3, shell[("New", "g", "o")]);
        Assert.Equal(Path.Combine(Root, "New", "skin.tex"), bases["chara/skin.tex"]);
        Assert.Equal(Path.Combine(Root, "A", "other.tex"), bases["chara/other.tex"]);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-16")]
    public void Ledger_collapses_chains()
    {
        var ledger = new ModMoveLedger();
        ledger.Record("A", "B", null, 0);
        ledger.Record("B", "C", null, 1);
        var live = ledger.Live(2);
        Assert.Equal("C", live["A"].NewDir);
        Assert.Equal("C", live["B"].NewDir);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-26")]
    public void Ledger_rename_back_A_B_A_never_self_maps()
    {
        var ledger = new ModMoveLedger();
        ledger.Record("A", "B", null, 0);
        ledger.Record("B", "A", null, 1);
        var live = ledger.Live(2);

        Assert.False(live.ContainsKey("A"));
        Assert.Equal("A", live["B"].NewDir);
        var remapped = ModMoveLedger.Remap([Entry("A"), Entry("B")], live, Root);
        Assert.Equal(["A"], remapped.Select(e => e.ModDirectory));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-3")]
    public void Ledger_case_only_rename_keeps_the_entry()
    {
        var ledger = new ModMoveLedger();
        ledger.Record("Bone", "bone", null, 0);

        var remapped = ModMoveLedger.Remap([Entry("A"), Entry("Bone")], ledger.Live(1), Root);

        Assert.Equal(["A", "bone"], remapped.Select(e => e.ModDirectory));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-26")]
    public void Ledger_forgets_a_reused_folder()
    {
        var ledger = new ModMoveLedger();
        ledger.Record("Old", "New", null, 0);
        ledger.Forget("old");
        Assert.Empty(ledger.Live(1));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-26")]
    public void Ledger_entry_expires_after_120s()
    {
        var ledger = new ModMoveLedger();
        ledger.Record("Old", "New", null, 0);
        Assert.NotEmpty(ledger.Live(ModMoveLedger.LifetimeMs - 1));
        Assert.Empty(ledger.Live(ModMoveLedger.LifetimeMs));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-26")]
    public void Ledger_empty_returns_same_instance()
    {
        var list = List();
        var map = new Dictionary<string, int> { ["A"] = 1 };
        var moves = new ModMoveLedger().Live(0);
        Assert.Same(list, ModMoveLedger.Remap(list, moves, Root));
        Assert.Same(map, ModMoveLedger.RemapKeys(map, moves));
    }

    // ── a renamed copy of the managed mod ────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-mod-rename-9")]
    public void Recreating_the_managed_mod_keeps_the_upstream_memo()
    {
        // The rebuild after a rename registers "Proteus" again; its ModAdded must not wipe the only record of the real
        // bases while the renamed copy still masks them.
        Assert.False(CompositorService.ModAddedInvalidatesUpstreams("Proteus"));
        Assert.False(CompositorService.ModAddedInvalidatesUpstreams("proteus"));
        Assert.True(CompositorService.ModAddedInvalidatesUpstreams("Proteus 2"));
        Assert.True(CompositorService.ModAddedInvalidatesUpstreams("Some Body Mod"));
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-9")]
    [Trait("Plan", "EDGE-mod-rename-5")]
    public void IsOwnOutput_treats_alias_folder_as_own_output()
    {
        var managed = Path.Combine(Root, "Proteus");
        string[] aliases = [Path.Combine(Root, "Proteus 2")];

        Assert.True(CompositorService.IsOwnOutputCore(Path.Combine(Root, "Proteus 2", "textures", "skin.tex"), managed, aliases));
        Assert.False(CompositorService.IsOwnOutputCore(Path.Combine(Root, "ProteusX", "textures", "skin.tex"), managed, aliases));
        Assert.False(CompositorService.IsOwnOutputCore(Path.Combine(Root, "Proteus 2b", "x.tex"), managed, aliases));
        Assert.False(CompositorService.IsOwnOutputCore(Path.Combine(Root, "Body", "skin.tex"), managed, aliases));
        Assert.Null(CompositorService.IsOwnOutputCore(null, managed, aliases));
    }
}
