using System;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The carrier writes — the only things Proteus puts on the player's own body — and the one ordering that keeps a
/// torn-down instance from leaving an item behind that nothing claims.
/// </summary>
public class CarrierWritesTests
{
    /// <summary>
    /// A framework thread that tears the instance down between being handed the write and running it.
    /// <para/>
    /// This is the real sequence, compressed: a composite decides to equip a carrier, the plugin is unloaded while
    /// that decision is in flight (a reload, an update), and the write arrives afterwards. Measured on a reload, the
    /// gap between the two was 9.5 seconds.
    /// </summary>
    private static Func<Func<bool>, bool> UnloadedWhileWaiting(Action unload)
        => write =>
        {
            unload();
            return write();
        };

    [Fact]
    public void A_write_that_arrives_after_teardown_never_touches_the_player()
    {
        bool gone = false;
        bool wrote = false;

        bool result = CarrierWrites.Guarded(
            UnloadedWhileWaiting(() => gone = true),
            () => gone,
            () => { wrote = true; return true; });

        // Not "it returned false": the item must never go on. A refused write reports false so the caller keeps no
        // record of equipping it either — which is what lets the live instance equip it and remember that it did.
        Assert.False(wrote);
        Assert.False(result);
    }

    [Fact]
    public void A_write_from_a_live_instance_goes_through()
    {
        bool wrote = false;

        bool result = CarrierWrites.Guarded(write => write(), () => false,
                                            () => { wrote = true; return true; });

        Assert.True(wrote);
        Assert.True(result);
    }

    [Fact]
    public void A_write_the_game_refuses_is_reported_as_refused()
    {
        // Glamourer can decline — a locked slot, no player. The caller reads that the same way as a teardown: it
        // happened or it did not, and only "it did" may be recorded.
        bool result = CarrierWrites.Guarded(write => write(), () => false, () => false);

        Assert.False(result);
    }

    [Fact]
    public void Teardown_takes_the_carriers_off_before_refusing_writes()
    {
        // The two halves wired together, as Dispose wires them: the removal goes through the SAME guard every other
        // write does, so an order that marked this instance gone first would refuse teardown its own removal and
        // leave on the player exactly what it is there to take off.
        bool gone = false;
        bool removed = false;

        CarrierWrites.Teardown(
            removeCarriers: () => removed = CarrierWrites.Guarded(write => write(), () => gone, () => true),
            markGone: () => gone = true);

        Assert.True(removed);

        // And afterwards nothing else gets through.
        bool later = CarrierWrites.Guarded(write => write(), () => gone, () => true);
        Assert.False(later);
    }

    [Fact]
    public void Teardown_refuses_writes_even_if_the_removal_throws()
    {
        // A removal can fail — Glamourer is disposed after us, and a write can land in that gap. What must not
        // happen is the instance staying writable afterwards, with a composite still running behind it.
        bool gone = false;

        Assert.Throws<InvalidOperationException>(() => CarrierWrites.Teardown(
            removeCarriers: () => throw new InvalidOperationException("Glamourer went away"),
            markGone: () => gone = true));

        Assert.True(gone);
    }
}
