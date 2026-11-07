#if PROTEUS_TEST_HARNESS
using System;
using System.Threading;

namespace Proteus.Services;

/// <summary>
/// The test harness's view into the boot probe (ADR 0002): re-arm it as a zone change would, and read where its back-off
/// stands. Compiled out of every CI build with the rest of the harness. Any thread (the gate is locked).
/// </summary>
public partial class CompositorService
{
    /// <summary>What the character going away does: the probe looks again from the start, its latch cleared. A re-arm
    /// with a Proteus mod enabled runs a normal boot composite, as every zone change does.</summary>
    internal void HarnessRearmBoot()
    {
        _boot.Rearm(Environment.TickCount64);
        Volatile.Write(ref _bootIdleLogged, 0);
    }

    /// <summary>The probe's latch, ladder step and time to the next look.</summary>
    internal (bool Latched, int Step, long NextDueInMs) HarnessBootState() => _boot.Snapshot(Environment.TickCount64);
}
#endif
