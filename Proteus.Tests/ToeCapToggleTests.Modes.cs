using System;
using System.Collections.Generic;
using Penumbra.Api.Enums;
using Proteus.Gui;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The tick on every mod: the mod's own Toe Cap option where it has a writable one, else its "Cap the toes" setting
/// (<see cref="ProteusMetadata.CapToes"/>), and what one click writes to each.
/// </summary>
public sealed partial class ToeCapToggleTests
{
    private static ToeCapOptionRef SingleCap() => new("Masks", "Toe Cap", ["Toe Cap"], false);

    private static ToeCapTickView ViewOf(ToeCapOptionRef? opt, Dictionary<string, List<string>> sel, bool capToes,
                                         bool temporary = false, bool inherited = false)
        => ToeCapToggle.View(opt, Snap(sel, temporary: temporary, inherited: inherited), capToes);

    // ── the tick's state in both modes ──────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-1")]
    public void View_is_on_when_either_store_would_cap()
    {
        Assert.True(ViewOf(Cap("Toe Cap"), Sel("Masks", "Toe Cap"), false).On);    // option only
        Assert.True(ViewOf(Cap("Toe Cap"), Sel("Masks"), true).On);                 // flag only
        Assert.True(ViewOf(Cap("Toe Cap"), Sel("Masks", "Toe Cap"), true).On);      // both
        Assert.False(ViewOf(Cap("Toe Cap"), Sel("Masks", "Seam"), false).On);       // neither
        Assert.False(ViewOf(null, Sel("Masks", "Toe Cap"), false).On);              // a selection with no mapped cap is not a cap
    }

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-5")]
    public void View_held_only_in_option_mode()
    {
        Assert.True(ViewOf(Cap("Toe Cap"), Sel("Masks", "Toe Cap"), false, temporary: true).Held);
        Assert.False(ViewOf(null, Sel("Masks"), true, temporary: true).Held);
        Assert.False(ViewOf(SingleCap(), Sel("Masks"), false, temporary: true).Held);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-4")]
    public void View_locks_a_selected_single_select_cap()
    {
        var v = ViewOf(SingleCap(), Sel("Masks", "Toe Cap"), false);
        Assert.Equal(new ToeCapTickView(true, true, false, false, ToeCapMode.Flag, true), v);
        Assert.False(ViewOf(SingleCap(), Sel("Masks"), false).Locked);
        // Unselected: ticking writes the setting, never Penumbra.
        var w = ToeCapToggle.WriteFor(SingleCap(), Sel("Masks"), ViewOf(SingleCap(), Sel("Masks"), false), false, true);
        Assert.Equal(new ToeCapWrite(null, true), w);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-7")]
    public void View_is_hidden_without_settings_in_both_modes()
    {
        Assert.False(ToeCapToggle.View(null, null, true).Visible);
        Assert.False(ToeCapToggle.View(Cap("Toe Cap"), null, true).Visible);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-8")]
    public void View_is_live_for_a_disabled_flag_mod()
    {
        var v = ToeCapToggle.View(null, Snap(Sel("Masks"), enabled: false), false);
        Assert.True(v.Visible);
        Assert.False(v.Held || v.Locked);
        Assert.Equal(new ToeCapWrite(null, true), ToeCapToggle.WriteFor(null, Sel("Masks"), v, false, true));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-31")]
    public void View_flag_mode_ignores_temporary_and_inherited()
    {
        var v = ViewOf(null, Sel("Masks"), true, temporary: true, inherited: true);
        Assert.Equal(new ToeCapTickView(true, true, false, false, ToeCapMode.Flag, false), v);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-33")]
    public void View_reads_a_hand_written_false_as_off()
    {
        var md = System.Text.Json.JsonSerializer.Deserialize<ProteusMetadata>("{\"CapToes\":false}", ProteusJson.MetadataRead)!;
        Assert.False(md.CapToes);
        Assert.False(ViewOf(null, Sel("Masks"), md.CapToes == true).On);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-36")]
    public void View_option_mode_flag_only_while_held_is_held_and_on()
    {
        var v = ViewOf(Cap("Toe Cap"), Sel("Masks"), true, temporary: true);
        Assert.True(v.Held);
        Assert.True(v.On);
        Assert.Equal(ToeCapMode.Option, v.Mode);
    }

    // ── what a click writes ─────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-2")]
    public void WriteFor_option_mode_tick_selects_the_option()
    {
        var sel = Sel("Masks", "Seam");
        var w = ToeCapToggle.WriteFor(Cap("Toe Cap"), sel, ViewOf(Cap("Toe Cap"), sel, false), false, true);
        Assert.Equal(["Seam", "Toe Cap"], w.MaskSelection);
        Assert.Null(w.CapToes);
    }

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-3")]
    public void WriteFor_untick_clears_option_and_flag()
    {
        var sel = Sel("Masks", "Seam", "Toe Cap");
        var w = ToeCapToggle.WriteFor(Cap("Toe Cap"), sel, ViewOf(Cap("Toe Cap"), sel, true), true, false);
        Assert.Equal(["Seam"], w.MaskSelection);
        Assert.False(w.CapToes);
    }

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-4")]
    public void WriteFor_flag_mode_sets_and_clears_CapToes()
    {
        var sel = Sel("Masks");
        Assert.Equal(new ToeCapWrite(null, true), ToeCapToggle.WriteFor(null, sel, ViewOf(null, sel, false), false, true));
        Assert.Equal(new ToeCapWrite(null, false), ToeCapToggle.WriteFor(null, sel, ViewOf(null, sel, true), true, false));
        // Already off: nothing to write.
        Assert.Equal(new ToeCapWrite(null, null), ToeCapToggle.WriteFor(null, sel, ViewOf(null, sel, false), false, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-24")]
    public void WriteFor_untick_flag_only_on_option_mod_skips_ipc()
    {
        var none = Sel("Masks", "Seam");
        Assert.Equal(new ToeCapWrite(null, false),
                     ToeCapToggle.WriteFor(Cap("Toe Cap"), none, ViewOf(Cap("Toe Cap"), none, true), true, false));
        var both = Sel("Masks", "Seam", "Toe Cap");
        var w = ToeCapToggle.WriteFor(Cap("Toe Cap"), both, ViewOf(Cap("Toe Cap"), both, true), true, false);
        Assert.Equal(["Seam"], w.MaskSelection);
        Assert.False(w.CapToes);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-32")]
    public void WriteFor_flag_mode_has_no_binding_input()
    {
        // The write never depends on a design binding: under one it writes base metadata like the other geometry ticks.
        var sel = Sel("Masks");
        Assert.Equal(new ToeCapWrite(null, true), ToeCapToggle.WriteFor(null, sel, ViewOf(null, sel, false), false, true));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-35")]
    public void WriteFor_writable_option_never_writes_flag_on_tick()
    {
        var sel = Sel("Masks");
        var w = ToeCapToggle.WriteFor(Cap("Toe Cap"), sel, ViewOf(Cap("Toe Cap"), sel, false), false, true);
        Assert.Equal(["Toe Cap"], w.MaskSelection);
        Assert.Null(w.CapToes);
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-35")]
    public void WriteFor_read_only_option_never_writes_penumbra()
    {
        var sel = Sel("Masks", "Seam");
        Assert.Null(ToeCapToggle.WriteFor(SingleCap(), sel, ViewOf(SingleCap(), sel, false), false, true).MaskSelection);
        Assert.Null(ToeCapToggle.WriteFor(SingleCap(), sel, ViewOf(SingleCap(), sel, true), true, false).MaskSelection);
    }

    // ── carrying a click out ────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-6")]
    public void Apply_penumbra_refusal_skips_the_flag()
    {
        int flagCalls = 0;
        var r = ToeCapToggle.Apply(new ToeCapWrite(["Seam"], false),
            _ => PenumbraApiEc.OptionMissing, _ => { flagCalls++; return true; });
        Assert.Equal(new ToeCapApplyResult(ToeCapApplyStatus.PenumbraRefused, PenumbraApiEc.OptionMissing), r);
        Assert.Equal(0, flagCalls);
    }

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-6")]
    [Trait("Plan", "EDGE-toecap-geometry-38")]
    public void Apply_penumbra_ok_but_save_failed()
    {
        int optionCalls = 0, flagCalls = 0;
        var r = ToeCapToggle.Apply(new ToeCapWrite(["Seam"], false),
            _ => { optionCalls++; return PenumbraApiEc.Success; }, _ => { flagCalls++; return false; });
        Assert.Equal(ToeCapApplyStatus.SaveFailed, r.Status);
        Assert.Equal((1, 1), (optionCalls, flagCalls));
    }

    [Fact]
    [Trait("Plan", "EDGE-toecap-geometry-39")]
    public void Apply_flag_only_save_failed_never_calls_penumbra()
    {
        var r = ToeCapToggle.Apply(new ToeCapWrite(null, true),
            _ => throw new InvalidOperationException("no Penumbra write"), _ => false);
        Assert.Equal(ToeCapApplyStatus.SaveFailed, r.Status);
    }

    [Theory]
    [Trait("Plan", "EDGE-toecap-geometry-38")]
    [Trait("Plan", "EDGE-toecap-geometry-39")]
    [InlineData(null, true)]
    [InlineData(true, false)]
    public void WriteFlag_restores_the_setting_when_the_save_fails(bool? before, bool flag)
    {
        var md = new ProteusMetadata { CapToes = before };
        bool? during = null;
        Assert.False(ToeCapToggle.WriteFlag(md, flag, () => { during = md.CapToes; return false; }));
        Assert.Equal(flag ? true : null, during);   // the save saw the new value
        Assert.Equal(before, md.CapToes);           // and the failure put the old one back
    }

    [Theory]
    [Trait("Plan", "AC-toecap-geometry-4")]
    [Trait("Plan", "EDGE-toecap-geometry-33")]
    [InlineData(true, true)]
    [InlineData(false, null)]
    public void WriteFlag_writes_true_or_removes_never_false(bool flag, bool? want)
    {
        var md = new ProteusMetadata { CapToes = flag ? null : true };
        Assert.True(ToeCapToggle.WriteFlag(md, flag, () => true));
        Assert.Equal(want, md.CapToes);
    }

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-6")]
    public void Apply_ok_writes_both_in_order()
    {
        var order = new List<string>();
        var r = ToeCapToggle.Apply(new ToeCapWrite(["Seam"], false),
            _ => { order.Add("penumbra"); return PenumbraApiEc.NothingChanged; }, _ => { order.Add("flag"); return true; });
        Assert.Equal(ToeCapApplyStatus.Ok, r.Status);
        Assert.Equal(["penumbra", "flag"], order);
    }

    [Fact]
    [Trait("Plan", "AC-toecap-geometry-8")]
    public void Reach_is_the_same_for_a_flag_mod()
    {
        // Reach reads the overlays and the mod's state, never which store the cap lives in.
        var md = Simple(Body());
        var flagged = Simple(Body());
        flagged.CapToes = true;
        Assert.Equal(ToeCapToggle.Reach(md, true, Sel("Masks", "Toe Cap"), false),
                     ToeCapToggle.Reach(flagged, true, Sel("Masks"), false));
    }
}
