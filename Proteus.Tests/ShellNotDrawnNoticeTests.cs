using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Services.ShellNotDrawnNotice;

namespace Proteus.Tests;

/// <summary>One automatic redraw, then one message naming the accessory (the owner's report on #91's message).</summary>
public class ShellNotDrawnNoticeTests
{
    private const string Key = "k";

    private static Step N(string? redrawn = null, string? noticed = null, bool autoRedraw = true, bool after = false,
                          bool current = true, bool busy = false, bool held = false, bool writeFailures = false,
                          int redraws = 0, int deferrals = 0)
        => Next(Key, redrawn, noticed, autoRedraw, after, current, busy, held, writeFailures, redraws, deferrals);

    private static string? Item(int category, int set)
        => (category, set) switch { (12, 53) => "Emperor's New Ring", (9, 211) => "Pearl Earrings", _ => null };

    private static string? Glasses(int set) => set == 5521 ? "Invisible Glasses" : null;

    private static string Label(params string[] paths)
        => HostLabel(paths, Item, Glasses, set => set == 5521, s => s);

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-7")]
    public void First_failure_all_clear_redraws()
    {
        // [AC-issue-sweep-d-7]
        Assert.Equal(Step.AutoRedraw, N());
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-8")]
    public void After_own_redraw_notifies()
    {
        // [AC-issue-sweep-d-8]
        Assert.Equal(Step.Notify, N(after: true));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-8")]
    public void Host_label_names_item_and_slot()
    {
        // [AC-issue-sweep-d-8]
        Assert.Equal("Pearl Earrings (Earrings)", Label("chara/accessory/a0211/model/c0201a0211_ear.mdl"));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-8")]
    public void Ordinary_strings_never_claim_a_redraw()
    {
        // [AC-issue-sweep-d-8] They also go out on paths where Proteus made no redraw: neutral wording.
        Assert.DoesNotContain("redraw", StringFor(partly: true, carrier: false, redrawOff: false).Fallback,
                              System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("redraw", StringFor(partly: false, carrier: false, redrawOff: false).Fallback,
                              System.StringComparison.OrdinalIgnoreCase);
        Assert.All(new[] { (true, false, false), (false, false, false), (true, true, false), (true, false, true) },
            w => Assert.Contains("{0}", StringFor(w.Item1, w.Item2, w.Item3).Fallback));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-13")]
    public void Same_shell_never_redraws_twice()
    {
        // [EDGE-issue-sweep-d-13] The redraw's aftermath composite builds the same shell: no second redraw.
        Assert.Equal(Step.Notify, N(redrawn: Key));
        Assert.Equal(Step.AutoRedraw, N(redrawn: "another shell"));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-14")]
    public void Unknown_item_falls_back_to_slot_and_set()
    {
        // [EDGE-issue-sweep-d-14]
        Assert.Equal("Necklace (a0999)", Label("chara/accessory/a0999/model/c0101a0999_nek.mdl"));
        Assert.Equal("weird", Label("chara/odd/weird.mdl"));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-15")]
    public void Carrier_and_ordinary_hosts_get_one_line_each()
    {
        // [EDGE-issue-sweep-d-15] PrintShellNotice groups by IsCarrierHost: one line per wording, every host named.
        var hosts = new[]
        {
            "chara/accessory/a0053/model/c0101a0053_rir.mdl",
            "chara/accessory/a0211/model/c0201a0211_ear.mdl",
            "chara/accessory/a0053/model/c0101a0053_wrs.mdl",
        };
        var groups = hosts.GroupBy(h => IsCarrierHost(h, 5521)).ToList();
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups.Single(g => g.Key).Count());
        Assert.Equal("Emperor's New Ring (Right ring), Bracelets (a0053)", Label(groups.Single(g => g.Key).ToArray()));
        Assert.Equal("Pearl Earrings (Earrings)", Label(groups.Single(g => !g.Key).ToArray()));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-17")]
    public void Superseded_probe_is_silent()
    {
        // [EDGE-issue-sweep-d-17]
        Assert.Equal(Step.Silent, N(current: false));
        Assert.Equal(Step.Silent, N(current: false, after: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-19")]
    public void Busy_or_held_defers_once_then_notifies()
    {
        // [EDGE-issue-sweep-d-19]
        Assert.Equal(Step.Defer, N(busy: true));
        Assert.Equal(Step.Defer, N(held: true));
        Assert.Equal(Step.Notify, N(busy: true, deferrals: 1));
        Assert.Equal(Step.Notify, N(held: true, deferrals: 1));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-20")]
    public void Noticed_shell_is_silent()
    {
        // [EDGE-issue-sweep-d-20]
        Assert.Equal(Step.Silent, N(noticed: Key));
        Assert.Equal(Step.Silent, N(noticed: Key, autoRedraw: false));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-26")]
    public void Write_failure_publish_defers_never_redraws()
    {
        // [EDGE-issue-sweep-d-26] The retry composite owns recovery of a publish that had write failures.
        Assert.Equal(Step.Defer, N(writeFailures: true));
        Assert.Equal(Step.Notify, N(writeFailures: true, deferrals: 1));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-27")]
    public void Rate_limit_notifies_instead()
    {
        // [EDGE-issue-sweep-d-27]
        Assert.Equal(Step.AutoRedraw, N(redraws: MaxAutoRedraws - 1));
        Assert.Equal(Step.Notify, N(redraws: MaxAutoRedraws));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-12")]
    public void Carrier_hosts_use_carrier_wording()
    {
        // [AC-issue-sweep-d-12] Proteus's own invisible carriers: the ring, bracelets, necklace, and the glasses.
        Assert.True(IsCarrierHost("chara/accessory/a0053/model/c0101a0053_rir.mdl", null));
        Assert.True(IsCarrierHost("chara/accessory/a0053/model/c0101a0053_ril.mdl", null));
        Assert.True(IsCarrierHost("chara/accessory/a0053/model/c0101a0053_wrs.mdl", null));
        Assert.True(IsCarrierHost("chara/accessory/a0053/model/c0101a0053_nek.mdl", null));
        Assert.False(IsCarrierHost("chara/accessory/a0053/model/c0101a0053_ear.mdl", null));
        Assert.False(IsCarrierHost("chara/accessory/a0211/model/c0201a0211_rir.mdl", null));
        Assert.True(IsCarrierHost("chara/equipment/e5521/model/c0101e5521_met.mdl", 5521));
        Assert.False(IsCarrierHost("chara/equipment/e5521/model/c0101e5521_met.mdl", null));
        var (key, text) = StringFor(partly: true, carrier: true, redrawOff: false);
        Assert.Equal("Chat.ShellCarrierNotDrawn.Fmt", key);
        Assert.Contains("Recomposite now", text);
        Assert.Contains("invisible accessory", text);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-30")]
    public void Facewear_named_from_glasses_lookup_else_slot()
    {
        // [EDGE-issue-sweep-d-30]
        Assert.Equal("Invisible Glasses", Label("chara/equipment/e5521/model/c0101e5521_met.mdl"));
        Assert.Equal("Head (e5522)", HostLabel(["chara/equipment/e5522/model/c0101e5522_met.mdl"], Item, Glasses,
                                                set => set == 5522, s => s));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-9")]
    public void Auto_redraw_off_never_redraws()
    {
        // [AC-issue-sweep-d-9]
        Assert.Equal(Step.NotifyRedrawOff, N(autoRedraw: false));
        Assert.Equal(Step.NotifyRedrawOff, N(autoRedraw: false, busy: true));
        Assert.Equal(Step.NotifyRedrawOff, N(autoRedraw: false, writeFailures: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-31")]
    public void No_hosts_no_chat()
    {
        // [EDGE-issue-sweep-d-31] Nothing to name: an empty label; PrintShellNotice returns before any chat (wiring test).
        Assert.Equal("", HostLabel([], Item, Glasses, _ => false, s => s));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-10")]
    public void Localized_picks_the_same_wording_as_StringFor()
    {
        // [AC-issue-sweep-d-10] With no translation loaded, the localized template is StringFor's English, every combination.
        foreach (var p in new[] { false, true })
        foreach (var c in new[] { false, true })
        foreach (var r in new[] { false, true })
            Assert.Equal(StringFor(p, c, r).Fallback, Localized(p, c, r));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-32")]
    public void Redraw_off_picks_the_redraw_off_string()
    {
        // [EDGE-issue-sweep-d-32] Whatever the host, with auto redraw off the message asks the user to redraw.
        foreach (var carrier in new[] { false, true })
        foreach (var partly in new[] { false, true })
        {
            var (key, text) = StringFor(partly, carrier, redrawOff: true);
            Assert.Equal("Chat.ShellNotDrawnRedrawOff.Fmt", key);
            Assert.Contains("Redraw", text);
        }
    }
}
