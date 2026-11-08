using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Proteus.Interop;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// #83: re-applying the active bound design released every hold, and the sweep it armed read the resources of a redraw
/// made while the earlier sweep's strays were held off — so they looked undrawn and, with no redraw following, stayed
/// drawn. A same-design restore now carries the strays the sweep still holds to the next completed sweep, which holds
/// them off again once it has checked nothing kept needs their files (sweep-keeps-deps).
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

    private static HashSet<string> Set(params string[] dirs) => new(dirs, StringComparer.OrdinalIgnoreCase);

    private static List<string> Carry(IReadOnlyDictionary<string, PenumbraModSetting> held, Guid sweepDesign, Guid design,
                                      IReadOnlySet<string>? bound = null, IReadOnlySet<string>? proteus = null,
                                      IReadOnlySet<string>? installed = null, IReadOnlySet<string>? sweepHeld = null,
                                      IReadOnlySet<string>? released = null, IReadOnlyDictionary<string, string>? source = null)
        => DesignBindingService.SweepHoldsToCarry(held, sweepDesign, design, bound ?? None, proteus ?? None,
                                                  installed ?? Installed, SidecarDiscoveryService.ManagedModDir,
                                                  sweepHeld ?? Set(held.Keys.ToArray()), released ?? None,
                                                  source ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                                                  DesignBindingService.TemporarySource);

    [Fact]
    [Trait("Plan", "AC-issue-sweep-c-6")]
    [Trait("Plan", "AC-sweep-keeps-deps-6")]
    public void SweepHoldsToCarry_SameDesign_ReholdsStillHeldStrays()
        => Assert.Equal(["Other Stray", "Stray"], Carry(Held(("Stray", false), ("Other Stray", false)), Design, Design));

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-14")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-17")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-19")]
    public void SweepHoldsToCarry_OtherDesign_Empty()
    {
        Assert.Empty(Carry(Held(("Stray", false)), Design, Guid.NewGuid()));
        Assert.Empty(Carry(Held(("Stray", false)), Guid.Empty, Guid.Empty));   // no sweep has run (a fresh plugin)
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-15")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-17")]
    public void SweepHoldsToCarry_SkipsBoundProteusManagedUninstalledPlannedAndEnabled()
    {
        var held = Held(("Bound", false), ("Overlay", false), (SidecarDiscoveryService.ManagedModDir, false),
                        ("Gone", false), ("Stray", true));
        var carry = Carry(held, Design, Design, bound: Set("bound"), proteus: Set("Overlay"));
        Assert.Empty(carry);   // bound, a Proteus mod, the managed mod, not installed, or a hold that turns it ON
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-35")]
    public void SweepHoldsToCarry_DroppedHold_NotCarried()
        => Assert.Equal(["Other Stray"], Carry(Held(("Other Stray", false)), Design, Design));

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-16")]
    public void SweepHoldsToCarry_DesignHoldNotSweepPlaced_NotCarried()
        => Assert.Equal(["Stray"], Carry(Held(("Stray", false), ("Old Design Hold", false)), Design, Design, sweepHeld: Set("Stray"),
                                          installed: Set("Stray", "Old Design Hold")));

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-8")]
    public void SweepHoldsToCarry_PlayerReleased_NotCarried()
        => Assert.Equal(["Other Stray"], Carry(Held(("Stray", false), ("Other Stray", false)), Design, Design, released: Set("Stray")));

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-13")]
    public void SweepHoldsToCarry_OtherSourceLastSeen_NotCarried()
    {
        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Stray"] = "Glamourer", ["Other Stray"] = "Proteus" };
        Assert.Equal(["Other Stray"], Carry(Held(("Stray", false), ("Other Stray", false)), Design, Design, source: source));
    }

    // ── wiring ───────────────────────────────────────────────────────────────────────────────────────────────────

    internal static string Service([CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", "Services", "DesignBindingService.cs"));

    internal static string Method(string source, string signature)
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
    [Trait("Plan", "AC-sweep-keeps-deps-6")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-45")]
    public void Restore_defers_the_carry_to_the_first_phase_2()
    {
        var restore = Method(Service(), "private void RestoreCharacter(");
        int carry = restore.IndexOf("SweepHoldsToCarry(heldBefore, postLoadSweepDesign, designId,", StringComparison.Ordinal);
        int merge = restore.IndexOf("DesignRestoreSweep.MergeCarry(", StringComparison.Ordinal);
        int assign = restore.IndexOf("sweepCarried.UnionWith(carry);", StringComparison.Ordinal);
        int scope = restore.IndexOf("using (compositor.SuppressModSettingEvents())", StringComparison.Ordinal);
        int release = restore.IndexOf("ReleaseEveryKey(collId.Value);", StringComparison.Ordinal);
        int hold = restore.IndexOf("foreach (var (recorded, priority) in plan.Hold)", StringComparison.Ordinal);
        Assert.True(merge > 0 && carry > merge && assign > carry && assign < scope && scope < release && hold > release,
            "the carry is read and stored before the release drops the holds");
        Assert.Contains("designId != postLoadSweepDesign || collId.Value != postLoadSweepCollection", restore[..scope]);
        // The carry writes no hold: the only hold writes in the scope are plan.Hold's.
        var inScope = restore[scope..];
        Assert.DoesNotContain("carry.Where(", inScope);
        Assert.Equal(1, Count(inScope, "SetTemporaryModSettings("));
        // The sweep takes the carried dirs as candidates and empties them only once a check completes.
        var phase1 = Method(Service(), "private void RunPostLoadSweep(");
        Assert.Contains(".Concat(sweepCarried)", phase1);
        var phase2 = Method(Service(), "private void SweepPhase2(");
        int holdLoop = phase2.IndexOf("SetTemporaryModSettings(", StringComparison.Ordinal);
        int cleared = phase2.IndexOf("sweepCarried.Clear();", StringComparison.Ordinal);
        Assert.True(holdLoop > 0 && cleared > holdLoop, "carried dirs are cleared after the hold loop");
        // The core never logs "captured from the collection instead": a restore isn't a capture.
        Assert.DoesNotContain("log.", Method(Service(), "private IReadOnlyDictionary<string, PenumbraModSetting> HeldNowCore("));
        Assert.Contains("var live = HeldNowCore(collId, out int dropped, out _);", Method(Service(), "private IReadOnlyDictionary<string, PenumbraModSetting> StillHeld("));
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-45")]
    public void Second_same_design_restore_before_phase_2_keeps_the_carry_wiring()
    {
        var restore = Method(Service(), "private void RestoreCharacter(");
        int merge = restore.IndexOf("DesignRestoreSweep.MergeCarry(", StringComparison.Ordinal);
        int pending = restore.IndexOf("sweepCarried, boundDirs, proteusDirs, installed", StringComparison.Ordinal);
        int clear = restore.IndexOf("sweepCarried.Clear();", StringComparison.Ordinal);
        Assert.True(merge > 0 && pending > merge && clear > pending, "the pending carry is merged in before it is replaced");
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-9")]
    public void Restore_reuses_the_pre_release_settings_read_and_keeps_the_post_release_reads()
    {
        var restore = Method(Service(), "private void RestoreCharacter(");
        int scope = restore.IndexOf("using (compositor.SuppressModSettingEvents())", StringComparison.Ordinal);
        var before = restore[..scope];
        Assert.Contains("HeldNowCore(collId.Value, out _, out var effectiveBefore)", before);
        Assert.Contains("NoteSweepHoldChanges(collId.Value, effectiveBefore);", before);
        Assert.DoesNotContain("GetCollectionModSettings(", before);   // the one read before the release is HeldNowCore's
        var after = restore[scope..];
        Assert.Contains("GetCollectionModSettings(collId.Value, ignoreTemporary: true)", after);
        Assert.Contains("GetCollectionModSettings(collId.Value, ignoreTemporary: false)", after);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-17")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-15")]
    public void A_refused_sweep_hold_warns_and_records_nothing()
    {
        var phase2 = Method(Service(), "private void SweepPhase2(");
        int warn = phase2.IndexOf("log.Warning(\"[Proteus] design-restore: couldn't hold {0} off ({1}).\", dir, ec);", StringComparison.Ordinal);
        int cont = phase2.IndexOf("continue;", warn, StringComparison.Ordinal);
        int record = phase2.IndexOf("heldTemporary[dir] = new PenumbraModSetting { ModDirectory = dir, Enabled = false };", StringComparison.Ordinal);
        int held = phase2.IndexOf("sweepHeld.Add(dir);", StringComparison.Ordinal);
        int source = phase2.IndexOf("sweepHoldSource[dir] = TemporarySource;", StringComparison.Ordinal);
        Assert.True(warn > 0 && cont > warn && record > cont && held > cont && source > cont, "a failure warns and skips the records");
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-16")]
    [Trait("Plan", "EDGE-issue-sweep-c-18")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-18")]
    [Trait("Plan", "AC-sweep-keeps-deps-9")]
    public void Sweep_records_reset_on_release_all_and_follow_a_rename()
    {
        var svc = Service();
        var release = Method(svc, "private bool ReleaseHeldMods(");
        Assert.Contains("heldTemporary.Clear()", release);
        Assert.Contains("ClearSweepRecords(null);", release);
        var clear = Method(svc, "private void ClearSweepRecords(");
        foreach (var record in new[] { "sweepHeld", "sweepHoldSource", "sweepPlayerReleased", "sweepSpared", "sweepCarried" })
            Assert.Contains($"{record}.Clear();", clear);
        Assert.DoesNotContain("existsMemo", clear);   // game data doesn't change in a session
        var move = Method(svc, "public void MoveMod(");
        Assert.Contains("ModMoveMigration.MoveHeld(heldTemporary, oldDir, newDir)", move);
        Assert.Contains("ModMoveMigration.RekeySet(sweepHeld, oldDir, newDir);", move);
        Assert.Contains("ModMoveMigration.RekeyKey(sweepHoldSource, oldDir, newDir);", move);
        Assert.Contains("ModMoveMigration.RekeySet(sweepPlayerReleased, oldDir, newDir);", move);
        Assert.Contains("ModMoveMigration.RekeyKey(sweepSpared, oldDir, newDir);", move);
        Assert.Contains("ModMoveMigration.RekeySet(sweepCarried, oldDir, newDir);", move);
        // A different design (or collection) forgets the records before the carry.
        var restore = Method(svc, "private void RestoreCharacter(");
        Assert.Contains("ClearSweepRecords(null);", restore[..restore.IndexOf("using (compositor.SuppressModSettingEvents())", StringComparison.Ordinal)]);
        Assert.Contains("ReleaseEveryKey(collId.Value);", restore);   // unconditional: a plugin reload's holds go by key
    }

    internal static int Count(string s, string what)
    {
        int n = 0;
        for (int at = s.IndexOf(what, StringComparison.Ordinal); at >= 0; at = s.IndexOf(what, at + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
