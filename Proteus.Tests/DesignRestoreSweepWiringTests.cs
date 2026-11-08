using System;
using Xunit;
using static Proteus.Tests.DesignBindingSweepCarryTests;

namespace Proteus.Tests;

/// <summary>
/// The sweep's order of operations, read from the service source: nothing is held before the providers are decided, a
/// round that can't be trusted writes nothing, and a hold is only ever let go when it is still Proteus's.
/// </summary>
public class DesignRestoreSweepWiringTests
{
    private static string Phase1() => Method(Service(), "private void RunPostLoadSweep(");
    private static string Phase2() => Method(Service(), "private void SweepPhase2(");

    private static int At(string s, string what)
    {
        int i = s.IndexOf(what, StringComparison.Ordinal);
        Assert.True(i >= 0, $"missing: {what}");
        return i;
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-1")]
    [Trait("Plan", "AC-sweep-keeps-deps-5")]
    public void Sweep_phases_hold_only_non_providers_after_FindProviders_and_log_spared()
    {
        Assert.DoesNotContain("SetTemporaryModSettings(", Phase1());   // phase 1 only reads
        var p2 = Phase2();
        int find = At(p2, "DesignRestoreSweep.FindProviders(inputs)");
        int hold = At(p2, "SetTemporaryModSettings(");
        Assert.True(hold > find, "no hold is written before the providers are decided");
        Assert.Contains("if (providers.ContainsKey(dir) || files[dir].ManifestFailed) continue;", p2);
        Assert.Contains("left on mod(s) the design doesn't have, because something kept needs a file only they supply and the game has none", p2);
        Assert.Contains("Task.Run(", Phase1());
        Assert.Contains("framework.RunOnFrameworkThread(", Phase1());
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-6")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-29")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-36")]
    public void Phase2_lets_go_of_held_providers_with_fresh_query_then_one_redraw_then_recomposite()
    {
        var p2 = Phase2();
        int query = At(p2, "penumbra.QueryTemporaryModSettings(collId, dir)");
        int ours = At(p2, "if (!string.Equals(h.Source, TemporarySource, StringComparison.OrdinalIgnoreCase))");
        int clear = At(p2, "penumbra.ClearTemporaryModSettings(collId, dir)");
        Assert.True(query < ours && ours < clear, "a fresh query, then only Proteus's own hold is cleared");
        int scopeEnd = At(p2, "sweepCarried.Clear();");
        int redraw = At(p2, "compositor.RedrawForChangedModel(");
        int recomposite = At(p2, "compositor.TriggerRecomposite(");
        Assert.True(clear < scopeEnd && scopeEnd < redraw && redraw < recomposite, "one redraw after the writes, before the recomposite");
        Assert.Equal(1, Count(p2, "RedrawForChangedModel("));
        Assert.Contains("if (redraw) compositor.RedrawForChangedModel(", p2);
        Assert.Contains("bool redraw = compositor.AutoRedrawOn;", p2);
        Assert.Contains("a redraw is needed to draw them (auto redraw is off)", p2);
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-8")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-21")]
    public void Player_releases_noted_before_candidates_and_excluded_from_them()
    {
        var p1 = Phase1();
        int note = At(p1, "NoteSweepHoldChanges(collId, effective);");
        int candidates = At(p1, "StrayDrawnMods(modsRoot, tree, postLoadSweepBound, postLoadSweepProteus)");
        Assert.True(note < candidates);
        Assert.Contains("!sweepPlayerReleased.Contains(d)", p1);
        var note2 = Method(Service(), "private void NoteSweepHoldChanges(");
        Assert.Contains("sweepPlayerReleased.Add(dir)", note2);
        Assert.Contains("heldTemporary.Remove(dir);", note2);
        Assert.Contains("not holding off again mod(s) the player put back", note2);
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-9")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-30")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-31")]
    public void NoteSweepHoldChanges_clears_on_collection_change_lost_holds_or_PenumbraReady()
    {
        var svc = Service();
        var note = Method(svc, "private void NoteSweepHoldChanges(");
        int collection = At(note, "ClearSweepRecords(\"collection changed\");");
        int lost = At(note, "ClearSweepRecords(\"Penumbra dropped the temporary settings\");");
        int classify = At(note, "DesignRestoreSweep.ClassifySweepHolds(");
        Assert.True(collection < classify && lost < classify, "records are cleared, not classified");
        Assert.Contains("penumbra.PenumbraReady += OnPenumbraReadyClearSweep;", svc);
        Assert.Contains("penumbra.PenumbraReady -= OnPenumbraReadyClearSweep;", svc);
        Assert.Contains("ClearSweepRecords(\"Penumbra restarted\")", svc);
    }

    [Fact]
    [Trait("Plan", "AC-sweep-keeps-deps-10")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-11")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-27")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-42")]
    public void Sweep_failures_write_nothing_warn_once_and_retry_bounded()
    {
        var p1 = Phase1();
        Assert.True(At(p1, "BlockSweep(DesignRestoreSweep.SweepBlock.MapUnreadable, null)") < At(p1, "NoteSweepHoldChanges("));
        Assert.Contains("BlockSweep(DesignRestoreSweep.SweepBlock.SettingsUnreadable, null)", p1);
        var p2 = Phase2();
        int hold = At(p2, "SetTemporaryModSettings(");
        Assert.True(At(p2, "WarnSweepOnce($\"reading the mods failed") < hold);
        Assert.True(At(p2, "penumbra.GetPlayerCollectionId() != job.Collection") < hold);
        Assert.True(At(p2, "BlockSweep(DesignRestoreSweep.SweepBlock.ResolveFailed, null)") < hold);
        Assert.True(At(p2, "BlockSweep(block, blockDir)") < hold);
        var block = Method(Service(), "private void BlockSweep(");
        Assert.Contains("sweepBlockRetries < DesignRestoreSweep.MaxBlockRetriesPerWindow", block);
        Assert.Contains("if (retry) QueuePostLoadSweep();", block);
        // A background failure or a collection change is not retried.
        int bgFail = At(p2, "if (read == null)");
        Assert.DoesNotContain("BlockSweep(", p2[bgFail..At(p2, "var files = read.Files;")]);
        var arm = Method(Service(), "private void ArmPostLoadSweep(");
        Assert.Contains("sweepFailureLogged      = false;", arm);
        Assert.Contains("sweepBlockRetries       = 0;", arm);
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-33")]
    public void Failures_warned_once_per_window_and_timing_line_fields()
    {
        var warn = Method(Service(), "private void WarnSweepOnce(");
        Assert.Contains("if (sweepFailureLogged) return;", warn);
        var p2 = Phase2();
        Assert.Contains("settings {2} ms", p2);
        Assert.Contains("exists {7} checked ({8} new)", p2);
        Assert.Contains("resolved {9} path(s)", p2);
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-10")]
    public void Spared_logged_once_and_skipped()
    {
        Assert.Contains("sweepSpared.TryAdd(dir, needs)) spared.Add(dir);", Phase2());
        Assert.Contains("!sweepSpared.ContainsKey(d)", Phase1());
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-13")]
    public void Candidates_exclude_holds_another_plugin_took_over()
        => Assert.Contains("bool Ours(string d) => !sweepHoldSource.TryGetValue(d, out var s) || s == TemporarySource;", Phase1());

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-26")]
    public void Phase2_skips_candidates_whose_manifest_failed_and_keeps_such_held_strays()
    {
        var p2 = Phase2();
        Assert.Contains("files[dir].ManifestFailed) continue;", p2);
        // A held stray with no readable files can't be a provider, so it is never in the let-go loop's providers.
        Assert.Contains("if (!providers.TryGetValue(dir, out var needs)) continue;", p2);
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-28")]
    public void Phase2_discards_on_unload_or_superseded()
    {
        var p2 = Phase2();
        int guard = At(p2, "job.Token != postLoadSweepToken || job.Generation != characterWorkGeneration");
        Assert.True(At(p2, "if (disposed || !FeatureOn) return;") < guard);
        Assert.True(guard < At(p2, "SetTemporaryModSettings(") && guard < At(p2, "sweepCarried.Clear();"));
        Assert.Contains("if (disposed || ct.IsCancellationRequested) return;", Phase1());
        var dispose = Method(Service(), "public void Dispose(");
        Assert.Contains("disposed = true;", dispose);
        Assert.Contains("sweepCts.Cancel();", dispose);
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-35")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-41")]
    public void Window_checked_only_where_redraws_queue()
    {
        Assert.DoesNotContain("postLoadSweepUntil", Phase1());
        Assert.DoesNotContain("postLoadSweepUntil", Phase2());
        Assert.Contains("if (Environment.TickCount64 < postLoadSweepUntil) QueuePostLoadSweep();", Method(Service(), "private void OnLocalPlayerRedrawn("));
        Assert.Contains("private void QueuePostLoadSweep()", Service());
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-1")]
    public void SweepFileExists_Throws_IsAbsent_AndMemoizedForSession()
    {
        var p2 = Phase2();
        Assert.Contains("catch { e = false; }", p2);
        Assert.Contains("existsMemo[g] = e;", p2);
        Assert.DoesNotContain("existsMemo.Clear", Service());
    }
}
