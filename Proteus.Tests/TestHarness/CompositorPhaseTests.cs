using Proteus.Services;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

public class CompositorPhaseTests
{
    [Theory]
    [InlineData(true, 1, 1, 1, "boot-hold")]
    [InlineData(false, 1, 1, 1, "compositing")]
    [InlineData(false, 0, 1, 0, "pending")]
    [InlineData(false, 0, 0, 2, "pending")]
    [InlineData(false, 0, 0, 0, "idle")]
    public void Of_PrecedenceTable(bool bootHold, int inFlight, int pending, int triggers, string expected)
    {
        // [AC-qa-harness-5]
        Assert.Equal(expected, CompositorPhase.Of(new CompositorService.ActivitySnapshot(inFlight, 3, pending, triggers, "", bootHold)));
    }
}

public class AutoRefitQuietTests
{
    [Theory]
    [InlineData(false, false, 0, false, false, false)]
    [InlineData(true, false, 0, false, false, true)]
    [InlineData(false, true, 0, false, false, true)]
    [InlineData(false, false, 1, false, false, true)]
    [InlineData(false, false, 0, true, false, true)]
    [InlineData(false, false, 0, false, true, true)]
    public void Of_BusyWhenAnyPendingSignal(bool viewBusy, bool running, int queued, bool walkDue, bool undoing, bool busy)
    {
        // [AC-qa-harness-8] [EDGE-qa-harness-36] Running counts even when the view says idle.
        Assert.Equal(busy, AutoRefitQuiet.Of(new AutoRefitWatcher.View(Busy: viewBusy),
                                             new AutoRefitWatcher.ActivitySnapshot(running, queued, walkDue, undoing)));
    }

    [Fact]
    public void WalkDueWhileDisabled_NotBusy()
    {
        // [EDGE-qa-harness-37] A walk left scheduled while auto refit is off (or disposed) never runs: not pending.
        Assert.False(AutoRefitWatcher.WalkCounts(12345, enabled: false, disposed: false));
        Assert.False(AutoRefitWatcher.WalkCounts(12345, enabled: true, disposed: true));
        Assert.False(AutoRefitWatcher.WalkCounts(0, enabled: true, disposed: false));
        Assert.True(AutoRefitWatcher.WalkCounts(12345, enabled: true, disposed: false));
    }
}
