using System;
using System.Collections.Generic;
using System.IO;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>When the auto refit's body mod index is read, and how a stale one is dropped.</summary>
public class BodyIndexScheduleTests : IDisposable
{
    private const long T0 = 50_000;
    private readonly string temp = Path.Combine(Path.GetTempPath(), "proteus-bodyidx-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(temp, true); } catch { }
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-6")]
    public void Disabled_PanelHidden_Changes_NotDue_ReturnsMarkStale()
    {
        // [AC-idle-disk-io-6]
        var s = new BodyIndexSchedule();
        // Startup and every later mod event, auto refit off, the list never drawn.
        Assert.Equal(BodyIndexSchedule.Change.MarkStale, s.Changed(T0, 0, enabled: false));
        Assert.Equal(BodyIndexSchedule.Change.MarkStale, s.Changed(T0 + 10, 2000, enabled: false));
        Assert.False(s.TakeDue(T0 + 1_000_000));
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-6")]
    [Trait("Plan", "EDGE-idle-disk-io-23")]
    public void Disabled_PanelShownWithin1s_ChangesDueAfterDebounce()
    {
        // [AC-idle-disk-io-6] [EDGE-idle-disk-io-23]
        var s = new BodyIndexSchedule();
        s.Shown(T0, built: true, refreshing: false);
        Assert.Equal(BodyIndexSchedule.Change.Scheduled, s.Changed(T0 + 500, 2000, enabled: false));
        Assert.False(s.TakeDue(T0 + 2_499));
        Assert.True(s.TakeDue(T0 + 2_500));

        // Closed for more than a second: back to dropping.
        Assert.Equal(BodyIndexSchedule.Change.MarkStale, s.Changed(T0 + 5_000, 2000, enabled: false));
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-6")]
    public void Asked_DueNow()
    {
        // [AC-idle-disk-io-6]
        var s = new BodyIndexSchedule();
        s.Asked(T0);
        Assert.True(s.TakeDue(T0));
        Assert.False(s.TakeDue(T0 + 1));
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-6")]
    [Trait("Plan", "EDGE-idle-disk-io-25")]
    public void Enable_StaleOrUnbuilt_DueNow()
    {
        // [AC-idle-disk-io-6] [EDGE-idle-disk-io-25]
        var unbuilt = new BodyIndexSchedule();
        Assert.True(unbuilt.Enabled(T0, built: false));
        Assert.True(unbuilt.TakeDue(T0));

        var stale = new BodyIndexSchedule();
        stale.Changed(T0, 2000, enabled: false);
        Assert.True(stale.Enabled(T0 + 1, built: true));
        Assert.True(stale.TakeDue(T0 + 1));
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-6")]
    public void Enable_FreshBuilt_NotDue()
    {
        // [AC-idle-disk-io-6]
        var s = new BodyIndexSchedule();
        Assert.False(s.Enabled(T0, built: true));
        Assert.False(s.TakeDue(T0 + 1_000_000));
    }

    [Fact]
    public void Enabled_Changes_DueAfterDebounce()
    {
        // Unchanged behaviour while auto refit is on.
        var s = new BodyIndexSchedule();
        Assert.Equal(BodyIndexSchedule.Change.Scheduled, s.Changed(T0, 2000, enabled: true));
        Assert.False(s.TakeDue(T0 + 1_999));
        Assert.True(s.TakeDue(T0 + 2_000));
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-6")]
    [Trait("Plan", "EDGE-idle-disk-io-33")]
    public void Shown_NullIndex_NotRefreshing_DueNow()
    {
        // [AC-idle-disk-io-6] [EDGE-idle-disk-io-33]
        var s = new BodyIndexSchedule();
        Assert.True(s.Shown(T0, built: false, refreshing: false));
        Assert.True(s.TakeDue(T0));
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-6")]
    [Trait("Plan", "EDGE-idle-disk-io-33")]
    public void Shown_NullIndex_Refreshing_NotDue()
    {
        // [AC-idle-disk-io-6] [EDGE-idle-disk-io-33]
        var s = new BodyIndexSchedule();
        Assert.False(s.Shown(T0, built: false, refreshing: true));
        Assert.False(s.TakeDue(T0));
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-6")]
    [Trait("Plan", "EDGE-idle-disk-io-33")]
    public void Shown_Built_NotDue()
    {
        // [AC-idle-disk-io-6] [EDGE-idle-disk-io-33]
        var s = new BodyIndexSchedule();
        Assert.False(s.Shown(T0, built: true, refreshing: false));
        Assert.False(s.TakeDue(T0));
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-6")]
    [Trait("Plan", "EDGE-idle-disk-io-33")]
    public void Shown_DueNow_ThrottledFor3s()
    {
        // [AC-idle-disk-io-6] [EDGE-idle-disk-io-33]
        var s = new BodyIndexSchedule();
        Assert.True(s.Shown(T0, built: false, refreshing: false));
        s.TakeDue(T0);
        // A refresh that failed leaves the index null; drawn every frame, it asks again only after 3 s.
        for (long t = T0 + 16; t < T0 + BodyIndexSchedule.ShownAskThrottleMs; t += 16)
            Assert.False(s.Shown(t, built: false, refreshing: false));
        Assert.True(s.Shown(T0 + BodyIndexSchedule.ShownAskThrottleMs, built: false, refreshing: false));
    }

    private (Dictionary<string, string> Mods, string Root) SyntheticModsRoot()
    {
        Directory.CreateDirectory(Path.Combine(temp, "Plain Mod"));
        File.WriteAllText(Path.Combine(temp, "Plain Mod", "meta.json"), "{\"FileVersion\":3,\"Name\":\"Plain Mod\"}");
        return (new Dictionary<string, string> { ["Plain Mod"] = "Plain Mod" }, temp);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-25")]
    [Trait("Plan", "EDGE-idle-disk-io-26")]
    public void BodyModIndex_MarkStale_DropsCurrent_RefreshRestores()
    {
        // [EDGE-idle-disk-io-25] [EDGE-idle-disk-io-26]
        var (mods, root) = SyntheticModsRoot();
        var index = new BodyModIndex();
        index.RefreshState(mods, root);
        Assert.NotNull(index.Current);

        index.MarkStale();
        Assert.Null(index.Current);
        Assert.Null(index.Snapshot);

        // A refit's `Current ?? RefreshState(...)` reads it again instead of using the stale list.
        var state = index.Current ?? index.RefreshState(mods, root);
        Assert.NotNull(state);
        Assert.Same(state, index.Current);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-26")]
    public void MarkStaleBetweenCaptureAndPublish_CasFails()
    {
        // [EDGE-idle-disk-io-26]
        var (mods, root) = SyntheticModsRoot();
        var index = new BodyModIndex();
        long gen = index.Generation;
        index.BeforePublish = () => index.MarkStale();

        var state = index.RefreshState(mods, root);

        Assert.NotNull(state);                    // the caller still gets what it read
        Assert.False(index.LastPublished);
        Assert.Null(index.Current);               // the stale mark stands
        Assert.Equal(gen + 1, index.Generation);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-26")]
    public void TwoRefreshesInSequence_BothPublish()
    {
        // [EDGE-idle-disk-io-26]
        var (mods, root) = SyntheticModsRoot();
        var index = new BodyModIndex();
        index.RefreshState(mods, root);
        Assert.True(index.LastPublished);
        var second = index.RefreshState(mods, root);
        Assert.True(index.LastPublished);
        Assert.Same(second, index.Current);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-24")]
    [Trait("Plan", "EDGE-idle-disk-io-27")]
    [Trait("Plan", "EDGE-idle-disk-io-28")]
    public void NullCurrent_IsReadingState()
    {
        // [EDGE-idle-disk-io-24] [EDGE-idle-disk-io-27] [EDGE-idle-disk-io-28]
        var (mods, root) = SyntheticModsRoot();
        var index = new BodyModIndex();
        // Never refreshed: the state the Settings panel shows as "Finding bodies" and PumpFills returns on.
        Assert.Null(index.Snapshot);
        Assert.Null(index.Current);
        // The Body size run's inline refresh works on a fresh index, schedule or not.
        var state = index.RefreshState(mods, root);
        Assert.Empty(state.List);
        Assert.NotNull(index.Snapshot);
    }
}
