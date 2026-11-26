using System;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The pure rules of the Studio's character freeze: when it holds, lets go and comes back.</summary>
public class CharacterFreezePolicyTests
{
    private const nint Chara = 0x1000, Draw = 0x2000;
    private const int Idle = 111;

    private static FreezeSample Standing(Vector3 pos = default, float rot = 0, byte mode = 1, byte param = 0,
                                         ushort emote = 0, int hash = Idle, nint draw = Draw)
        => new(pos, rot, mode, param, emote, hash, draw);

    /// <summary>One update. A want older than now is taken as long enough ago in updates too, unless <paramref name="updates"/> says.</summary>
    private static FreezeInputs In(long now, FreezeSample? sample, long? want = null, bool enabled = true, bool hook = true,
                                   nint chara = Chara, bool gpose = false, bool between = false, bool busy = false,
                                   int? updates = null, bool unknown = false, bool slot0 = true)
        => new(enabled, want ?? now, updates ?? ((want ?? now) == now ? 0 : 99), hook, unknown, slot0, chara, gpose, between,
               busy, sample, now);

    /// <summary>A policy already frozen on a still, standing character at t = 0.</summary>
    private static CharacterFreezePolicy FrozenAtZero(FreezeSample? s = null)
    {
        var p = new CharacterFreezePolicy();
        Assert.Equal(FreezeState.Frozen, p.Step(In(0, s ?? Standing())));
        return p;
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-1")]
    [Trait("Plan", "EDGE-studio-freeze-1")]
    public void Freezes_at_once_when_wanted_enabled_standing_and_still()
    {
        var p = new CharacterFreezePolicy();
        Assert.Equal(FreezeState.Frozen, p.Step(In(0, Standing())));
        Assert.Equal(Chara, p.Target);
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-2")]
    [Trait("Plan", "EDGE-studio-freeze-8")]
    public void Lapses_to_idle_after_the_want_grace()
    {
        var p = FrozenAtZero();
        Assert.Equal(FreezeState.Frozen, p.Step(In(250, Standing(), want: 0)));
        Assert.Equal(FreezeState.Idle, p.Step(In(251, Standing(), want: 0)));
        Assert.Equal((nint)0, p.Target);
        Assert.Equal(1, p.Releases);
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-4")]
    [Trait("Plan", "EDGE-studio-freeze-2")]
    [Trait("Plan", "EDGE-studio-freeze-4")]
    public void Movement_or_turn_releases_and_refreezes_after_750ms()
    {
        var moved = new Vector3(0.002f, 0, 0);
        var p = FrozenAtZero();
        Assert.Equal(FreezeState.Settling, p.Step(In(100, Standing(moved))));
        Assert.Equal(FreezeState.Settling, p.Step(In(849, Standing(moved))));
        Assert.Equal(FreezeState.Frozen, p.Step(In(850, Standing(moved))));

        var t = FrozenAtZero();
        Assert.Equal(FreezeState.Settling, t.Step(In(100, Standing(rot: 0.01f))));
        Assert.Equal(FreezeState.Settling, t.Step(In(849, Standing(rot: 0.01f))));
        Assert.Equal(FreezeState.Frozen, t.Step(In(850, Standing(rot: 0.01f))));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-5")]
    public void Rotation_wrap_is_not_movement()
    {
        var p = FrozenAtZero(Standing(rot: 3.1415f));
        Assert.Equal(FreezeState.Frozen, p.Step(In(16, Standing(rot: -3.1415f))));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-4")]
    [Trait("Plan", "EDGE-studio-freeze-30")]
    public void Mode_param_emote_or_timeline_change_settles_for_2500ms()
    {
        foreach (var changed in new[] { Standing(param: 2), Standing(emote: 7) })
        {
            var p = FrozenAtZero();
            Assert.Equal(FreezeState.Settling, p.Step(In(100, changed)));
            Assert.Equal(FreezeState.Settling, p.Step(In(2599, changed)));
            Assert.Equal(FreezeState.Frozen, p.Step(In(2600, changed)));
        }

        // A one-shot action's timeline comes and goes: settled 2500 ms after the last change.
        var q = FrozenAtZero();
        Assert.Equal(FreezeState.Settling, q.Step(In(100, Standing(hash: 222))));
        Assert.Equal(FreezeState.Settling, q.Step(In(200, Standing())));
        Assert.Equal(FreezeState.Settling, q.Step(In(2699, Standing())));
        Assert.Equal(FreezeState.Frozen, q.Step(In(2700, Standing())));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-4")]
    [Trait("Plan", "AC-studio-freeze-5")]
    [Trait("Plan", "EDGE-studio-freeze-3")]
    public void Sit_releases_stays_busy_and_refreezes_after_standing()
    {
        var p = FrozenAtZero();
        Assert.Equal(FreezeState.Busy, p.Step(In(100, Standing(mode: 11, hash: 333))));
        Assert.Equal(FreezeState.Busy, p.Step(In(9000, Standing(mode: 11, hash: 333))));
        // Stood up: normal again, the stand-up timeline played and the idle one back.
        Assert.Equal(FreezeState.Settling, p.Step(In(10000, Standing(hash: 444))));
        Assert.Equal(FreezeState.Settling, p.Step(In(10500, Standing())));
        Assert.Equal(FreezeState.Settling, p.Step(In(12999, Standing())));
        Assert.Equal(FreezeState.Frozen, p.Step(In(13000, Standing())));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-5")]
    [Trait("Plan", "EDGE-studio-freeze-13")]
    [Trait("Plan", "EDGE-studio-freeze-32")]
    public void Busy_never_freezes()
    {
        var p = FrozenAtZero();
        Assert.Equal(FreezeState.Busy, p.Step(In(16, Standing(), busy: true)));
        Assert.Equal(1, p.Releases);
        Assert.Equal(FreezeState.Busy, p.Step(In(60000, Standing(), busy: true)));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-5")]
    public void AllowedMode_is_normal_only()
    {
        for (int b = 0; b <= 16; b++)
            Assert.Equal(b == 1, CharacterFreezePolicy.AllowedMode((byte)b));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-5")]
    [Trait("Plan", "EDGE-studio-freeze-12")]
    public void Leaving_GPose_settles_for_2500ms()
    {
        var p = FrozenAtZero();
        Assert.Equal(FreezeState.GPose, p.Step(In(100, Standing(), gpose: true)));
        Assert.Equal(FreezeState.Settling, p.Step(In(5000, Standing())));
        Assert.Equal(FreezeState.Settling, p.Step(In(7499, Standing())));
        Assert.Equal(FreezeState.Frozen, p.Step(In(7500, Standing())));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-3")]
    [Trait("Plan", "EDGE-studio-freeze-11")]
    public void Leaving_between_areas_settles_for_2500ms()
    {
        var p = FrozenAtZero();
        Assert.Equal(FreezeState.Busy, p.Step(In(100, Standing(), between: true, busy: true)));
        Assert.Equal(FreezeState.Settling, p.Step(In(5000, Standing())));
        Assert.Equal(FreezeState.Settling, p.Step(In(7499, Standing())));
        Assert.Equal(FreezeState.Frozen, p.Step(In(7500, Standing())));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-6")]
    [Trait("Plan", "EDGE-studio-freeze-6")]
    public void Setting_off_releases_and_on_refreezes()
    {
        var p = FrozenAtZero();
        Assert.Equal(FreezeState.Off, p.Step(In(16, Standing(), enabled: false)));
        Assert.Equal((nint)0, p.Target);
        Assert.Equal(FreezeState.Frozen, p.Step(In(32, Standing())));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-10")]
    [Trait("Plan", "EDGE-studio-freeze-18")]
    public void Unavailable_never_freezes()
    {
        var p = new CharacterFreezePolicy();
        Assert.Equal(FreezeState.Unavailable, p.Step(In(0, Standing(), hook: false)));
        Assert.Equal(FreezeState.Unavailable, p.Step(In(5000, Standing(), hook: false, enabled: false)));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-3")]
    [Trait("Plan", "EDGE-studio-freeze-9")]
    [Trait("Plan", "EDGE-studio-freeze-10")]
    public void No_character_releases_and_a_new_or_returning_character_settles_500ms_after_present()
    {
        var p = FrozenAtZero();
        Assert.Equal(FreezeState.NoCharacter, p.Step(In(16, null, chara: 0)));
        Assert.Equal(1, p.Releases);
        // A new address, drawn: 500 ms.
        Assert.Equal(FreezeState.Settling, p.Step(In(1000, Standing(), chara: 0x3000)));
        Assert.Equal(FreezeState.Settling, p.Step(In(1499, Standing(), chara: 0x3000)));
        Assert.Equal(FreezeState.Frozen, p.Step(In(1500, Standing(), chara: 0x3000)));
        Assert.Equal((nint)0x3000, p.Target);

        // Present with nothing drawn: the 500 ms start when the draw object appears.
        var q = FrozenAtZero();
        Assert.Equal(FreezeState.NoCharacter, q.Step(In(16, null, chara: 0)));
        Assert.Equal(FreezeState.NoCharacter, q.Step(In(1000, Standing(draw: 0))));
        Assert.Equal(FreezeState.NoCharacter, q.Step(In(3000, Standing(draw: 0))));
        Assert.Equal(FreezeState.Settling, q.Step(In(4000, Standing())));
        Assert.Equal(FreezeState.Settling, q.Step(In(4499, Standing())));
        Assert.Equal(FreezeState.Frozen, q.Step(In(4500, Standing())));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-21")]
    public void Draw_object_change_releases_and_refreezes_after_500ms()
    {
        var p = FrozenAtZero();
        Assert.Equal(FreezeState.Settling, p.Step(In(100, Standing(draw: 0x4000))));
        Assert.Equal(1, p.DrawObjectChanges);
        Assert.Equal(1, p.Releases);
        Assert.Equal(FreezeState.Settling, p.Step(In(599, Standing(draw: 0x4000))));
        Assert.Equal(FreezeState.Frozen, p.Step(In(600, Standing(draw: 0x4000))));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-39")]
    public void Activity_during_idle_busy_or_gpose_delays_the_first_want()
    {
        var moved = new Vector3(0.01f, 0, 0);
        // Walked while nothing wanted a freeze; a want 300 ms later settles until 750 ms after the move.
        var p = new CharacterFreezePolicy();
        Assert.Equal(FreezeState.Idle, p.Step(In(0, Standing(), want: -1000)));
        Assert.Equal(FreezeState.Idle, p.Step(In(100, Standing(moved), want: -1000)));
        Assert.Equal(FreezeState.Settling, p.Step(In(400, Standing(moved))));
        Assert.Equal(FreezeState.Settling, p.Step(In(849, Standing(moved))));
        Assert.Equal(FreezeState.Frozen, p.Step(In(850, Standing(moved))));

        // A timeline change while busy: settled until 2500 ms after it once busy clears.
        var b = new CharacterFreezePolicy();
        Assert.Equal(FreezeState.Busy, b.Step(In(0, Standing(), busy: true)));
        Assert.Equal(FreezeState.Busy, b.Step(In(100, Standing(hash: 9), busy: true)));
        Assert.Equal(FreezeState.Settling, b.Step(In(1000, Standing(hash: 9))));
        Assert.Equal(FreezeState.Frozen, b.Step(In(2600, Standing(hash: 9))));

        // A redraw in GPose still counts.
        var g = new CharacterFreezePolicy();
        g.Step(In(0, Standing(), gpose: true));
        g.Step(In(100, Standing(draw: 0x5000), gpose: true));
        Assert.Equal(1, g.DrawObjectChanges);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-31")]
    public void TimelineHash_covers_four_arrays_and_ignores_excluded_pairs()
    {
        ushort[] Arr() => new ushort[14];
        var a = new[] { Arr(), Arr(), Arr(), Arr() };
        (int, int)[] ignored = [(2, 7)];
        int Hash() => CharacterFreezePolicy.TimelineHash(a[0], a[1], a[2], a[3], ignored);

        int baseHash = Hash();
        a[2][7] = 55;
        Assert.Equal(baseHash, Hash());
        a[2][7] = 0;
        for (int arr = 0; arr < 4; arr++)
            for (int s = 0; s < 14; s++)
            {
                if ((arr, s) == (2, 7)) continue;
                a[arr][s] = 9;
                Assert.NotEqual(baseHash, Hash());
                a[arr][s] = 0;
            }
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-22")]
    public void Continuous_want_across_tool_switch_stays_frozen()
    {
        var p = FrozenAtZero();
        for (long t = 16; t < 5000; t += 16)
            Assert.Equal(FreezeState.Frozen, p.Step(In(t, Standing())));
        Assert.Equal(0, p.Releases);
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-4")]
    [Trait("Plan", "EDGE-studio-freeze-42")]
    public void Settle_deadline_takes_the_later_of_overlapping_activities()
    {
        var p = FrozenAtZero();
        Assert.Equal(FreezeState.Settling, p.Step(In(1000, Standing(param: 3))));
        var moved = new Vector3(0.002f, 0, 0);
        Assert.Equal(FreezeState.Settling, p.Step(In(1100, Standing(moved, param: 3))));
        Assert.Equal(FreezeState.Settling, p.Step(In(1850, Standing(moved, param: 3))));
        Assert.Equal(FreezeState.Settling, p.Step(In(3499, Standing(moved, param: 3))));
        Assert.Equal(FreezeState.Frozen, p.Step(In(3500, Standing(moved, param: 3))));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-4")]
    [Trait("Plan", "EDGE-studio-freeze-43")]
    public void Long_timeline_waits_for_the_standing_idle_hash()
    {
        var p = FrozenAtZero();
        Assert.Equal(FreezeState.Settling, p.Step(In(1000, Standing(hash: 222))));
        Assert.Equal(FreezeState.Settling, p.Step(In(3500, Standing(hash: 222))));
        Assert.True(p.AwaitingIdleTimeline);
        // Back on the idle timeline: that is itself a change, so it settles 2500 ms from it.
        Assert.Equal(FreezeState.Settling, p.Step(In(5000, Standing())));
        Assert.Equal(FreezeState.Frozen, p.Step(In(7500, Standing())));
        Assert.False(p.AwaitingIdleTimeline);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-44")]
    public void Long_timeline_wait_is_capped_at_10s()
    {
        var p = FrozenAtZero();
        Assert.Equal(FreezeState.Settling, p.Step(In(1000, Standing(hash: 222))));
        Assert.Equal(FreezeState.Settling, p.Step(In(10999, Standing(hash: 222))));
        Assert.Equal(FreezeState.Frozen, p.Step(In(11000, Standing(hash: 222))));
        // The new hash is the standing-idle one now: a change back to it and away again waits on 222.
        Assert.Equal(FreezeState.Settling, p.Step(In(12000, Standing(hash: 333))));
        Assert.Equal(FreezeState.Settling, p.Step(In(15000, Standing(hash: 333))));
        Assert.True(p.AwaitingIdleTimeline);

        // With no standing-idle hash recorded yet (never frozen), a missing hash counts as matched: with slot 0 on a
        // learned idle the armed wait holds nothing back.
        var q = new CharacterFreezePolicy();
        Assert.Equal(FreezeState.Idle, q.Step(In(0, Standing(), want: -1000)));
        Assert.Equal(FreezeState.Idle, q.Step(In(100, Standing(hash: 222), want: -1000)));
        Assert.Equal(FreezeState.Frozen, q.Step(In(2600, Standing(hash: 222))));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-45")]
    public void Appear_settle_from_first_want_under_the_fallback()
    {
        var p = new CharacterFreezePolicy(settleAppearMs: 1500, appearFromWant: true);
        Assert.Equal(FreezeState.Frozen, p.Step(In(0, Standing())));
        // Redrawn by 400; the brush wants nothing while the model is not drawn.
        Assert.Equal(FreezeState.Idle, p.Step(In(400, Standing(draw: 0x6000), want: 0)));
        Assert.Equal(FreezeState.Idle, p.Step(In(450, Standing(draw: 0x6000), want: 0)));
        // First want after it at 500: settled until 2000.
        Assert.Equal(FreezeState.Settling, p.Step(In(500, Standing(draw: 0x6000))));
        Assert.Equal(FreezeState.Settling, p.Step(In(1999, Standing(draw: 0x6000))));
        Assert.Equal(FreezeState.Frozen, p.Step(In(2000, Standing(draw: 0x6000))));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-2")]
    [Trait("Plan", "EDGE-studio-freeze-47")]
    public void One_long_frame_does_not_lapse_the_want()
    {
        var p = FrozenAtZero();
        // One update 400 ms later (a save's long frame): only one update without a want.
        Assert.Equal(FreezeState.Frozen, p.Step(In(400, Standing(), want: 0, updates: 1)));
        Assert.Equal(FreezeState.Frozen, p.Step(In(420, Standing(), want: 0, updates: 2)));
        Assert.Equal(FreezeState.Idle, p.Step(In(440, Standing(), want: 0, updates: 3)));
        Assert.Equal(FreezeState.Frozen, p.Step(In(460, Standing())));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-21")]
    [Trait("Plan", "EDGE-studio-freeze-60")]
    public void Draw_object_change_on_a_learned_slot0_gives_the_appear_settle_only()
    {
        var p = FrozenAtZero();
        Assert.Equal(FreezeState.Settling, p.Step(In(1000, Standing(draw: 0x7000))));
        Assert.False(p.AwaitingIdleTimeline);
        Assert.Equal(FreezeState.Settling, p.Step(In(1499, Standing(draw: 0x7000))));
        Assert.False(p.AwaitingIdleTimeline);
        Assert.Equal(FreezeState.Frozen, p.Step(In(1500, Standing(draw: 0x7000))));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-4")]
    [Trait("Plan", "EDGE-studio-freeze-49")]
    [Trait("Plan", "EDGE-studio-freeze-50")]
    [Trait("Plan", "EDGE-studio-freeze-55")]
    public void Unknown_timeline_is_mode_activity()
    {
        var p = FrozenAtZero();
        Assert.Equal(FreezeState.Settling, p.Step(In(1000, Standing(), unknown: true)));
        Assert.Equal(1, p.Releases);
        Assert.Equal(FreezeState.Settling, p.Step(In(3499, Standing())));
        Assert.Equal(FreezeState.Frozen, p.Step(In(3500, Standing())));

        // Unfrozen too: it delays the next freeze.
        var q = new CharacterFreezePolicy();
        Assert.Equal(FreezeState.Idle, q.Step(In(0, Standing(), want: -1000)));
        Assert.Equal(FreezeState.Idle, q.Step(In(100, Standing(), want: -1000, unknown: true)));
        Assert.Equal(FreezeState.Settling, q.Step(In(500, Standing())));
        Assert.Equal(FreezeState.Frozen, q.Step(In(2600, Standing())));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-4")]
    [Trait("Plan", "EDGE-studio-freeze-49")]
    [Trait("Plan", "EDGE-studio-freeze-55")]
    public void Unknown_start_arms_the_long_timeline_wait()
    {
        // The shipped default: an action starting in slot 0 (nothing else changed) waits for a learned idle.
        var p = FrozenAtZero();
        Assert.Equal(FreezeState.Settling, p.Step(In(1000, Standing(), unknown: true, slot0: false)));
        Assert.Equal(FreezeState.Settling, p.Step(In(4000, Standing(), slot0: false)));
        Assert.True(p.AwaitingIdleTimeline);
        Assert.Equal(FreezeState.Frozen, p.Step(In(5000, Standing())));

        // Never back on a learned idle: frozen at the cap.
        var q = FrozenAtZero();
        q.Step(In(1000, Standing(), unknown: true, slot0: false));
        Assert.Equal(FreezeState.Settling, q.Step(In(10999, Standing(), slot0: false)));
        Assert.Equal(FreezeState.Frozen, q.Step(In(11000, Standing(), slot0: false)));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-3")]
    [Trait("Plan", "EDGE-studio-freeze-20")]
    public void A_new_instance_starts_unfrozen()
    {
        // A reloaded plugin's freeze: no want yet (CharacterFreeze starts its last want long ago), so Idle, never Frozen.
        var p = new CharacterFreezePolicy();
        Assert.Equal(FreezeState.Idle, p.Step(In(5000, Standing(), want: long.MinValue / 2, updates: int.MaxValue)));
        Assert.Equal((nint)0, p.Target);
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-4")]
    [Trait("Plan", "EDGE-studio-freeze-53")]
    public void Long_timeline_wait_covers_an_unlearned_slot0()
    {
        var p = FrozenAtZero();
        // An emote: its id changes, the hash stays on the standing idle, but slot 0 plays something not learned.
        Assert.Equal(FreezeState.Settling, p.Step(In(1000, Standing(emote: 5), slot0: false)));
        Assert.Equal(FreezeState.Settling, p.Step(In(4000, Standing(emote: 5), slot0: false)));
        Assert.True(p.AwaitingIdleTimeline);
        Assert.Equal(FreezeState.Frozen, p.Step(In(5000, Standing(emote: 5))));

        var q = FrozenAtZero();
        q.Step(In(1000, Standing(emote: 5), slot0: false));
        Assert.Equal(FreezeState.Settling, q.Step(In(10999, Standing(emote: 5), slot0: false)));
        Assert.Equal(FreezeState.Frozen, q.Step(In(11000, Standing(emote: 5), slot0: false)));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-4")]
    [Trait("Plan", "EDGE-studio-freeze-58")]
    public void First_freeze_after_a_long_slot0_one_shot_waits_for_a_learned_slot0()
    {
        var p = new CharacterFreezePolicy();
        Assert.Equal(FreezeState.Idle, p.Step(In(0, Standing(), want: -1000, slot0: false)));
        Assert.Equal(FreezeState.Idle, p.Step(In(100, Standing(emote: 9), want: -1000, slot0: false)));
        Assert.Equal(FreezeState.Settling, p.Step(In(1100, Standing(emote: 9), slot0: false)));
        Assert.Equal(FreezeState.Settling, p.Step(In(2700, Standing(emote: 9), slot0: false)));
        Assert.True(p.AwaitingIdleTimeline);
        Assert.Equal(FreezeState.Frozen, p.Step(In(6000, Standing(emote: 9))));

        var q = new CharacterFreezePolicy();
        q.Step(In(0, Standing(), want: -1000, slot0: false));
        q.Step(In(100, Standing(emote: 9), want: -1000, slot0: false));
        Assert.Equal(FreezeState.Settling, q.Step(In(10099, Standing(emote: 9), slot0: false)));
        Assert.Equal(FreezeState.Frozen, q.Step(In(10100, Standing(emote: 9), slot0: false)));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-59")]
    public void Slot0_only_unknown_start_skips_the_wait_under_the_fallback()
    {
        var p = new CharacterFreezePolicy(CharacterFreezePolicy.SettleAppearMs, false, slot0UnknownArmsWait: false);
        Assert.Equal(FreezeState.Frozen, p.Step(In(0, Standing())));
        Assert.Equal(FreezeState.Settling, p.Step(In(1000, Standing(), unknown: true, slot0: false)));
        Assert.Equal(FreezeState.Settling, p.Step(In(3499, Standing(), slot0: false)));
        Assert.Equal(FreezeState.Frozen, p.Step(In(3500, Standing(), slot0: false)));

        // With the emote id changed too, the wait still arms.
        var q = new CharacterFreezePolicy(CharacterFreezePolicy.SettleAppearMs, false, slot0UnknownArmsWait: false);
        q.Step(In(0, Standing()));
        q.Step(In(1000, Standing(emote: 4), unknown: true, slot0: false));
        Assert.Equal(FreezeState.Settling, q.Step(In(3500, Standing(emote: 4), slot0: false)));
        Assert.True(q.AwaitingIdleTimeline);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-51")]
    public void Freezable_only_when_every_check_bar_want_passes()
    {
        var p = new CharacterFreezePolicy();
        Assert.Equal(FreezeState.Idle, p.Step(In(0, Standing(), want: -1000)));
        Assert.True(p.Freezable);
        p.Step(In(100, Standing(), want: -1000, busy: true));
        Assert.False(p.Freezable);
        p.Step(In(200, Standing(), want: -1000, gpose: true));
        Assert.False(p.Freezable);
        p.Step(In(300, Standing(param: 1), want: -1000));   // settling after a mode change
        Assert.False(p.Freezable);
        p.Step(In(2900, Standing(param: 1), want: -1000, slot0: false));   // the long-timeline wait
        Assert.False(p.Freezable);
        p.Step(In(2950, Standing(param: 1), want: -1000));
        Assert.True(p.Freezable);
    }
}
