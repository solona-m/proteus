using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Proteus.Interop;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// #83: re-applying the active bound design released every hold, and the sweep it armed read the resources of a redraw
/// made while the earlier sweep's strays were held off — so they looked undrawn and, with no redraw following, stayed
/// drawn. A same-design restore now holds off again the strays Proteus still holds.
/// </summary>
public class DesignBindingSweepCarryTests
{
    private static readonly Guid Design = Guid.NewGuid();
    private static readonly HashSet<string> None = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Installed = new(StringComparer.OrdinalIgnoreCase)
        { "Stray", "Other Stray", "Bound", "Overlay", SidecarDiscoveryService.ManagedModDir };

    private static Dictionary<string, PenumbraModSetting> Held(params (string Dir, bool Enabled)[] holds)
    {
        var d = new Dictionary<string, PenumbraModSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, enabled) in holds) d[dir] = new PenumbraModSetting { ModDirectory = dir, Enabled = enabled };
        return d;
    }

    private static List<string> Carry(IReadOnlyDictionary<string, PenumbraModSetting> held, Guid sweepDesign, Guid design,
                                      IReadOnlySet<string>? bound = null, IReadOnlySet<string>? proteus = null,
                                      IReadOnlySet<string>? installed = null)
        => DesignBindingService.SweepHoldsToCarry(held, sweepDesign, design, bound ?? None, proteus ?? None,
                                                  installed ?? Installed, SidecarDiscoveryService.ManagedModDir);

    [Fact]
    [Trait("Plan", "AC-issue-sweep-c-6")]
    public void SweepHoldsToCarry_SameDesign_ReholdsStillHeldStrays()
        => Assert.Equal(["Other Stray", "Stray"], Carry(Held(("Stray", false), ("Other Stray", false)), Design, Design));

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-14")]
    public void SweepHoldsToCarry_OtherDesign_Empty()
    {
        Assert.Empty(Carry(Held(("Stray", false)), Design, Guid.NewGuid()));
        Assert.Empty(Carry(Held(("Stray", false)), Guid.Empty, Guid.Empty));   // no sweep has run
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-15")]
    public void SweepHoldsToCarry_SkipsBoundProteusManagedUninstalledPlannedAndEnabled()
    {
        var held = Held(("Bound", false), ("Overlay", false), (SidecarDiscoveryService.ManagedModDir, false),
                        ("Gone", false), ("Stray", true));
        var carry = Carry(held, Design, Design,
                          bound: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bound" },   // now bound (any case)
                          proteus: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Overlay" });
        Assert.Empty(carry);   // bound, a Proteus mod, the managed mod, not installed, or a hold that turns it ON
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-35")]
    public void SweepHoldsToCarry_DroppedHold_NotCarried()
    {
        // The player dropped "Stray"'s hold from Penumbra's banner: HeldNowCore leaves it out, so it isn't written back.
        var stillHeld = Held(("Other Stray", false));
        Assert.Equal(["Other Stray"], Carry(stillHeld, Design, Design));
    }

    // ── wiring ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static string Service([CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", "Services", "DesignBindingService.cs"));

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
        Assert.Fail($"unbalanced {signature}");
        return "";
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-c-6")]
    [Trait("Plan", "EDGE-issue-sweep-c-44")]
    public void Restore_reholds_the_sweeps_strays_inside_the_suppression_scope()
    {
        var restore = Method(Service(), "private void RestoreCharacter(");
        int carry = restore.IndexOf("SweepHoldsToCarry(HeldNowCore(collId.Value, out _), postLoadSweepDesign, designId,", StringComparison.Ordinal);
        int scope = restore.IndexOf("using (compositor.SuppressModSettingEvents())", StringComparison.Ordinal);
        int release = restore.IndexOf("ReleaseEveryKey(collId.Value);", StringComparison.Ordinal);
        int hold = restore.IndexOf("foreach (var (recorded, priority) in plan.Hold)", StringComparison.Ordinal);
        int rehold = restore.IndexOf("foreach (var dir in carry.Where(d => !planned.Contains(d)))", StringComparison.Ordinal);
        int scopeEnd = restore.IndexOf("if (carried.Count > 0)", StringComparison.Ordinal);
        Assert.True(carry > 0 && carry < scope && scope < release, "the carry is read before the release drops the holds");
        Assert.True(hold > release && rehold > hold && rehold < scopeEnd, "re-held after plan.Hold, inside the scope");
        Assert.Contains("designId == postLoadSweepDesign", restore[..scope]);
        // The core never logs "captured from the collection instead": a restore isn't a capture.
        Assert.DoesNotContain("log.", Method(Service(), "private IReadOnlyDictionary<string, PenumbraModSetting> HeldNowCore("));
        Assert.Contains("var live = HeldNowCore(collId, out int dropped);", Method(Service(), "private IReadOnlyDictionary<string, PenumbraModSetting> StillHeld("));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-17")]
    public void A_refused_rehold_warns_and_records_nothing()
    {
        var restore = Method(Service(), "private void RestoreCharacter(");
        var loop = restore[restore.IndexOf("foreach (var dir in carry.Where(", StringComparison.Ordinal)..restore.IndexOf("if (carried.Count > 0)", StringComparison.Ordinal)];
        int warn = loop.IndexOf("log.Warning(\"[Proteus] design-restore: couldn't hold {0} off again ({1}).\", dir, ec);", StringComparison.Ordinal);
        int cont = loop.IndexOf("continue;", StringComparison.Ordinal);
        int record = loop.IndexOf("heldTemporary[dir] = new PenumbraModSetting { ModDirectory = dir, Enabled = false };", StringComparison.Ordinal);
        Assert.True(warn > 0 && cont > warn && record > cont, "a failure warns and skips the record");
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-16")]
    [Trait("Plan", "EDGE-issue-sweep-c-18")]
    public void Carry_lives_in_heldTemporary_which_release_clears_and_renames_follow()
    {
        var svc = Service();
        Assert.Contains("heldTemporary.Clear()", Method(svc, "private bool ReleaseHeldMods("));
        Assert.Contains("ModMoveMigration.MoveHeld(heldTemporary, oldDir, newDir)", svc);
    }
}
