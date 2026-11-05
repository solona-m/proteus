using System;
using System.Threading;

namespace Proteus.Services;

/// <summary>
/// The gear shader upgrade's one-in-flight gate. A walk holds it from its first framework hop until its prune is done,
/// so no prune of superseded masks ever overlaps a walk's build or write (a prune could otherwise delete a mask a walk
/// has just found in its memo). Nothing ever WAITS for it: a teardown on the framework thread that waited for a walk
/// blocked on a framework hop would freeze the game, so a prune asked for while a walk holds the gate is owed to that
/// walk, which runs it as it leaves.
/// </summary>
internal sealed class WalkGate(Action prune)
{
    private int busy;
    private int pruneOwed;

    public bool Busy => Volatile.Read(ref busy) != 0;

    /// <summary>Run <paramref name="walk"/> if no other walk holds the gate; then prune. False when refused.</summary>
    public bool RunWalk(Action walk)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
        try { walk(); }
        finally
        {
            Interlocked.Exchange(ref pruneOwed, 0);
            Leave();
        }
        return true;
    }

    /// <summary>
    /// Prune now if no walk holds the gate, else owe it to the walk that does. Never blocks.
    /// </summary>
    public void RequestPrune()
    {
        Interlocked.Exchange(ref pruneOwed, 1);
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        Interlocked.Exchange(ref pruneOwed, 0);
        Leave();
    }

    /// <summary>Prune, release, and take the gate again for a prune owed while this one ran (so none is missed).</summary>
    private void Leave()
    {
        while (true)
        {
            try { prune(); }
            catch { /* a failed prune is retried by the next one */ }
            Interlocked.Exchange(ref busy, 0);
            if (Volatile.Read(ref pruneOwed) == 0 || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            Interlocked.Exchange(ref pruneOwed, 0);
        }
    }
}
