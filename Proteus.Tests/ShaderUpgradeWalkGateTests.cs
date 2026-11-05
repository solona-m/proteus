using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The gear shader upgrade's one-in-flight gate (<see cref="WalkGate"/>): no prune ever overlaps a walk, and nothing
/// ever waits for it — a teardown on the framework thread must not freeze behind a walk blocked on a framework hop.
/// </summary>
public class ShaderUpgradeWalkGateTests
{
    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-53")]
    public void A_prune_scheduled_mid_walk_never_deletes_a_mask_that_walk_applies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "proteus-gate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var m = Path.Combine(dir, "7-m_mask.tex");
            File.WriteAllBytes(m, [1]);
            bool referenced = false;      // walk 1 replaced M: unreferenced until walk 2 applies it
            int prunes = 0;
            var gate = new WalkGate(() =>
            {
                prunes++;
                if (!referenced && File.Exists(m)) File.Delete(m);
            });

            using var paused = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            bool existedAtApply = false;
            var walk = Task.Run(() => gate.RunWalk(() =>
            {
                // Build: a memo hit on M, then paused.
                Assert.True(File.Exists(m));
                paused.Set();
                release.Wait();
                // Hop B applies M.
                existedAtApply = File.Exists(m);
                referenced = true;
            }));

            Assert.True(paused.Wait(5000));
            gate.RequestPrune();               // owed to the walk, not run now
            Assert.True(File.Exists(m));
            Assert.Equal(0, prunes);
            release.Set();
            Assert.True(walk.Wait(5000));
            Assert.True(walk.Result);
            Assert.True(existedAtApply);
            Assert.True(prunes >= 1);          // ran as the walk left
            Assert.True(File.Exists(m));       // kept: referenced by then
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-54")]
    public void A_teardown_while_a_walk_waits_on_a_framework_hop_completes_without_waiting()
    {
        int prunes = 0;
        var gate = new WalkGate(() => Interlocked.Increment(ref prunes));
        using var hop = new ManualResetEventSlim();   // the walk's framework hop, which the "framework thread" never runs
        using var inHop = new ManualResetEventSlim();
        var walk = Task.Run(() => gate.RunWalk(() => { inHop.Set(); hop.Wait(); }));
        Assert.True(inHop.Wait(5000));

        // The "framework thread": a teardown asks for a prune. It must return at once.
        var teardown = Task.Run(gate.RequestPrune);
        Assert.True(teardown.Wait(1000), "the teardown blocked on the walk");
        Assert.Equal(0, prunes);

        hop.Set();
        Assert.True(walk.Wait(5000));
        Assert.True(prunes >= 1);   // the owed prune still happened when the walk ended
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-53")]
    public void With_no_walk_a_prune_runs_at_once_and_a_second_walk_is_refused()
    {
        int prunes = 0;
        var gate = new WalkGate(() => prunes++);
        gate.RequestPrune();
        Assert.Equal(1, prunes);
        bool inner = true;
        Assert.True(gate.RunWalk(() => inner = gate.RunWalk(() => { })));
        Assert.False(inner);
        Assert.False(gate.Busy);
    }
}
