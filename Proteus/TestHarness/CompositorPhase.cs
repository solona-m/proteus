using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>The compositor's activity as one word QA can wait on.</summary>
internal static class CompositorPhase
{
    /// <summary>boot-hold, then compositing, then pending (queued or unanswered triggers), else idle.</summary>
    internal static string Of(CompositorService.ActivitySnapshot a)
        => a.BootHold ? "boot-hold"
         : a.InFlight > 0 ? "compositing"
         : a.Pending > 0 || a.PendingTriggers > 0 ? "pending"
         : "idle";
}

/// <summary>Whether auto refit could still write: the view says busy, a runner is alive, work is queued, a walk that
/// can run is due, or an undo is running. Decides <c>/state.autoRefit.busy</c>, the 409, and the follow window.</summary>
internal static class AutoRefitQuiet
{
    internal static bool Of(AutoRefitWatcher.View view, AutoRefitWatcher.ActivitySnapshot a)
        => view.Busy || a.Running || a.Queued > 0 || a.WalkDue || a.Undoing;
}
