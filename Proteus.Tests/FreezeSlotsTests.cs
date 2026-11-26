using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The freeze's slot ledger: what it stops, what it learns as idle, and what it hands back.</summary>
public class FreezeSlotsTests
{
    /// <summary>A sequencer stand-in that records every write.</summary>
    private sealed class FakeSlots(int count = 14) : ISlotAccess
    {
        public readonly float[] Speed = new float[count];
        public readonly ushort[] Timeline = new ushort[count];
        public readonly List<(int Slot, float Speed)> Writes = [];
        public int Count => count;
        public float GetSpeed(int slot) => Speed[slot];
        public void SetSpeed(int slot, float speed) { Speed[slot] = speed; Writes.Add((slot, speed)); }
        public ushort GetTimeline(int slot) => Timeline[slot];

        /// <summary>A new timeline starts in a slot: the game resets its speed.</summary>
        public void Start(int slot, ushort id, float speed = 1f) { Timeline[slot] = id; Speed[slot] = speed; }
    }

    /// <summary>Observe slot 0 on <paramref name="id"/>, freezable, from <paramref name="from"/> to <paramref name="to"/> every 100 ms.</summary>
    private static void Stand(FreezeSlots s, FakeSlots a, long from, long to, bool freezable = true)
    {
        for (long t = from; t <= to; t += 100) s.Observe(a, freezable, t);
    }

    private static FreezeSlots Learned8062(FakeSlots a)
    {
        var s = new FreezeSlots();
        a.Start(0, 8062);
        s.Observe(a, true, 0);
        Stand(s, a, 0, 10000);
        Assert.True(s.IsLearned(0, 8062));
        return s;
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-1")]
    [Trait("Plan", "EDGE-studio-freeze-14")]
    [Trait("Plan", "EDGE-studio-freeze-56")]
    [Trait("Plan", "EDGE-studio-freeze-57")]
    public void Entry_holds_unlearned_slots_and_their_restart_releases()
    {
        var a = new FakeSlots(16);
        a.Start(0, 8062, 1f);
        a.Start(2, 300, 0.5f);
        a.Start(15, 77, 1f);   // beyond the 14 slots: never touched
        a.Start(4, 0, 1f);     // no timeline: plays nothing, never touched
        var s = new FreezeSlots();
        Assert.Equal(2, s.Hold(a, entry: true));
        Assert.Equal(0f, a.Speed[0]);
        Assert.Equal(0f, a.Speed[2]);
        Assert.Equal(1f, a.Speed[4]);
        Assert.DoesNotContain(a.Writes, w => w.Slot == 1 || w.Slot == 4 || w.Slot == 15);
        Assert.Equal(new[] { (0, 1f, (ushort)8062), (2, 0.5f, (ushort)300) }, s.Held.ToArray());

        // Slot 0 plays again on its timeline, not learned: the freeze must let go, and never stops it again.
        a.Speed[0] = 1f;
        Assert.True(s.UnknownRestart(a));
        Assert.Equal(0, s.Hold(a, entry: false));
        Assert.Equal(1f, a.Speed[0]);
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-4")]
    [Trait("Plan", "EDGE-studio-freeze-35")]
    [Trait("Plan", "EDGE-studio-freeze-51")]
    public void An_idle_id_sustained_10s_while_freezable_is_learned_and_a_swap_back_is_rezeroed()
    {
        var a = new FakeSlots();
        var s = new FreezeSlots();
        a.Start(0, 8062);
        s.Observe(a, true, 0);
        s.Observe(a, true, 9999);
        Assert.False(s.IsLearned(0, 8062));
        s.Observe(a, true, 10000);
        Assert.True(s.IsLearned(0, 8062));
        a.Start(0, 8063);
        s.Observe(a, true, 10100);
        s.Observe(a, true, 20100);
        Assert.True(s.IsLearned(0, 8063));

        s.Hold(a, entry: true);
        a.Start(0, 8062);   // an idle variant swaps in while held
        Assert.False(s.UnknownRestart(a));
        Assert.Equal(1, s.Hold(a, entry: false));
        Assert.Equal(0f, a.Speed[0]);
        Assert.Equal((0, 1f, (ushort)8062), s.Held.Single());
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-51")]
    [Trait("Plan", "EDGE-studio-freeze-52")]
    public void Sustain_restarts_when_not_freezable_or_on_another_id()
    {
        var a = new FakeSlots();
        var s = new FreezeSlots();
        a.Start(0, 8062);
        Stand(s, a, 0, 6000);
        s.Observe(a, false, 6100);
        Stand(s, a, 6200, 12000);
        Assert.False(s.IsLearned(0, 8062));

        var t = new FreezeSlots();
        Stand(t, a, 0, 6000);
        a.Start(0, 8063);
        t.Observe(a, true, 6100);
        a.Start(0, 8062);
        Stand(t, a, 6200, 12000);
        Assert.False(t.IsLearned(0, 8062));

        Stand(t, a, 12100, 17000);
        Assert.True(t.IsLearned(0, 8062));
        t.ForgetLearned();
        Assert.False(t.IsLearned(0, 8062));
        Assert.Empty(t.Learned);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-60")]
    public void Draw_object_change_reseeds_and_keeps_learned_ids()
    {
        var a = new FakeSlots();
        var s = Learned8062(a);
        s.Reseed();
        Assert.False(s.Observe(a, true, 20000));
        Assert.True(s.IsLearned(0, 8062));
        // The sustain count started over: a new id needs its full 10 s again.
        a.Start(0, 8063);
        s.Observe(a, true, 20100);
        s.Observe(a, true, 30099);
        Assert.False(s.IsLearned(0, 8063));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-61")]
    public void First_observe_after_reset_or_reseed_seeds_without_flagging()
    {
        var a = new FakeSlots();
        for (int i = 0; i < 14; i++) a.Start(i, (ushort)(100 + i));
        var s = new FreezeSlots();
        Assert.False(s.Observe(a, false, 0));
        s.ForgetLearned();
        Assert.False(s.Observe(a, false, 100));
        s.Reseed();
        Assert.False(s.Observe(a, false, 200));
        a.Start(0, 9001);
        Assert.True(s.Observe(a, false, 300));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-4")]
    [Trait("Plan", "EDGE-studio-freeze-53")]
    public void A_long_slot0_one_shot_after_an_emote_change_is_neither_learned_nor_held()
    {
        var a = new FakeSlots();
        var s = Learned8062(a);
        a.Start(0, 9001);
        Assert.True(s.Observe(a, false, 10100));   // the policy is settling after the emote change
        Stand(s, a, 10200, 14100, freezable: false);
        Assert.False(s.IsLearned(0, 9001));
        s.Hold(a, entry: true);
        a.Start(0, 9001);
        Assert.Equal(0, s.Hold(a, entry: false));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-4")]
    [Trait("Plan", "EDGE-studio-freeze-54")]
    public void A_repeated_slot0_emote_while_unfrozen_is_not_learned_and_is_activity()
    {
        var a = new FakeSlots();
        var s = Learned8062(a);
        long t = 10100;
        for (int round = 0; round < 2; round++)
        {
            a.Start(0, 9001);
            Assert.True(s.Observe(a, true, t));
            Stand(s, a, t + 100, t + 3000);
            a.Start(0, 8062);
            Assert.False(s.Observe(a, true, t + 3100));
            Stand(s, a, t + 3200, t + 6000);
            t += 6100;
        }
        Assert.False(s.IsLearned(0, 9001));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-49")]
    [Trait("Plan", "EDGE-studio-freeze-55")]
    public void A_slot0_repeat_while_frozen_releases()
    {
        var a = new FakeSlots();
        var s = Learned8062(a);
        s.Hold(a, entry: true);
        a.Start(0, 9001);
        Assert.True(s.Observe(a, true, 10100));
        Assert.True(s.UnknownRestart(a));
        Assert.Equal(0, s.Hold(a, entry: false));
        Assert.Equal(1f, a.Speed[0]);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-50")]
    public void A_timeline_in_a_slot_idle_at_entry_is_an_unknown_start()
    {
        var a = new FakeSlots();
        var s = Learned8062(a);
        s.Hold(a, entry: true);
        Assert.DoesNotContain(s.Held, h => h.Slot == 3);
        a.Start(3, 4242);
        Assert.True(s.Observe(a, true, 10100));
        Assert.True(s.UnknownRestart(a));
        s.Hold(a, entry: false);
        Assert.Equal(1f, a.Speed[3]);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-35")]
    public void RezeroIdleSwaps_false_treats_any_restart_as_activity()
    {
        var a = new FakeSlots();
        var s = new FreezeSlots(rezeroIdleSwaps: false);
        a.Start(0, 8062);
        Stand(s, a, 0, 10000);
        Assert.True(s.IsLearned(0, 8062));
        s.Hold(a, entry: true);
        a.Start(0, 8062);
        Assert.True(s.UnknownRestart(a));
        Assert.Equal(0, s.Hold(a, entry: false));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-3")]
    [Trait("Plan", "EDGE-studio-freeze-15")]
    [Trait("Plan", "EDGE-studio-freeze-29")]
    public void Release_writes_back_every_held_slot_still_zero()
    {
        var a = new FakeSlots();
        var s = Learned8062(a);
        a.Start(1, 500, 0.8f);
        a.Start(2, 600, 1.2f);
        s.Hold(a, entry: true);
        a.Timeline[1] = 501;       // another timeline, still at Proteus's 0
        a.Speed[2] = 0.7f;         // the game set this one going again
        Assert.Equal((2, 1), s.Release(a));
        Assert.Equal(1f, a.Speed[0]);
        Assert.Equal(0.8f, a.Speed[1]);
        Assert.Equal(0.7f, a.Speed[2]);
        Assert.Empty(s.Held);
        Assert.True(s.IsLearned(0, 8062));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-3")]
    public void RestoreValue_truth_table()
    {
        Assert.Equal(1.5f, FreezeSlots.RestoreValue(0, 1.5f));
        Assert.Null(FreezeSlots.RestoreValue(0.5f, 1.5f));
        Assert.Null(FreezeSlots.RestoreValue(0, 0));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-10")]
    [Trait("Plan", "EDGE-studio-freeze-11")]
    public void Drop_writes_nothing()
    {
        var a = new FakeSlots();
        a.Start(0, 8062);
        var s = new FreezeSlots();
        s.Hold(a, entry: true);
        int writes = a.Writes.Count;
        s.Drop();
        Assert.Empty(s.Held);
        Assert.Equal((0, 0), s.Release(a));
        Assert.Equal(writes, a.Writes.Count);
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-8")]
    [Trait("Plan", "EDGE-studio-freeze-16")]
    public void Hold_and_release_touch_only_the_given_sequencer()
    {
        var mine = new FakeSlots();
        var theirs = new FakeSlots();
        mine.Start(0, 8062);
        theirs.Start(0, 8062);
        var s = new FreezeSlots();
        s.Hold(mine, entry: true);
        s.Release(mine);
        Assert.NotEmpty(mine.Writes);
        Assert.Empty(theirs.Writes);
        Assert.Equal(1f, theirs.Speed[0]);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-62")]
    [Trait("Plan", "EDGE-studio-freeze-63")]
    public void Owner_key_keeps_race_sex_while_unknown()
    {
        var o = new FreezeOwner(0x10, 0x0201, 0x20);
        // Mid-redraw: nothing drawn, race/sex unknown → only a reseed.
        var (mid, c1) = FreezeOwner.Next(o, 0x10, null, 0);
        Assert.Equal(OwnerChange.Reseed, c1);
        Assert.Equal((ushort?)0x0201, mid.RaceSex);
        // Back with the same race/sex on a new draw object → a reseed, never a reset.
        var (back, c2) = FreezeOwner.Next(mid, 0x10, 0x0201, 0x30);
        Assert.Equal(OwnerChange.Reseed, c2);
        // A known, different race/sex → reset.
        Assert.Equal(OwnerChange.Reset, FreezeOwner.Next(back, 0x10, 0x0101, 0x30).Change);
        // Not a Human (race/sex null) → no reset.
        Assert.Equal(OwnerChange.None, FreezeOwner.Next(back, 0x10, null, 0x30).Change);
        // Another character → reset.
        Assert.Equal(OwnerChange.Reset, FreezeOwner.Next(back, 0x11, 0x0201, 0x30).Change);
        Assert.Equal(OwnerChange.None, FreezeOwner.Next(back, 0x10, 0x0201, 0x30).Change);
    }
}
