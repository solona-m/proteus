using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// One character's action-timeline slots, as the freeze reads and writes them: each slot's speed and the timeline
/// playing in it. The game side is the player's own <c>ActionTimelineSequencer</c>; tests use a fake.
/// </summary>
internal interface ISlotAccess
{
    int Count { get; }
    float GetSpeed(int slot);
    void SetSpeed(int slot, float speed);
    ushort GetTimeline(int slot);
}

/// <summary>
/// The pure ledger behind the Studio's character freeze: which action-timeline slots it set to speed 0 (and the speed
/// each had), and which timelines it has learned to be the character's standing idle.
/// <para>
/// The game resets a slot's speed whenever a new timeline starts in it. So after the freeze has stopped a slot, a
/// restart on a timeline already known to be idle (an idle variant) is stopped again, but a restart on anything else —
/// an emote, an action — is never stopped: it is reported, and the freeze lets go so the action plays.
/// </para>
/// <para>
/// An id is learned only after it has stayed in its slot for <see cref="LearnSustainMs"/> while a freeze could hold
/// (standing, settled, not busy), so a one-shot never is. The learned sets belong to one character (its address and
/// drawn race/sex) and survive a redraw.
/// </para>
/// </summary>
internal sealed class FreezeSlots
{
    public const int SlotCount = 14;

    /// <summary>A held slot restarting on a learned idle timeline is stopped again. False (the fallback): any restart lets go.</summary>
    public const bool RezeroIdleSwaps = true;

    /// <summary>How long an id must stay in its slot, while a freeze could hold, before it counts as idle.</summary>
    public const long LearnSustainMs = CharacterFreezePolicy.LongTimelineCapMs;

    private readonly bool rezeroIdleSwaps;
    private readonly Dictionary<int, HashSet<ushort>> learned = [];
    private readonly ushort[] prev = new ushort[SlotCount];
    private readonly (ushort Id, long Since)?[] candidate = new (ushort, long)?[SlotCount];
    private readonly List<(int Slot, float Speed, ushort Timeline)> held = [];
    private bool seeded;

    public FreezeSlots() : this(RezeroIdleSwaps) { }

    /// <summary>The fallback under test; the shipped default is the constant.</summary>
    internal FreezeSlots(bool rezeroIdleSwaps) => this.rezeroIdleSwaps = rezeroIdleSwaps;

    /// <summary>The slots this freeze stopped, with the speed and timeline each had.</summary>
    public IReadOnlyList<(int Slot, float Speed, ushort Timeline)> Held => held;

    /// <summary>The timelines learned as idle, per slot.</summary>
    public IReadOnlyDictionary<int, IReadOnlySet<ushort>> Learned
        => learned.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<ushort>)kv.Value);

    public bool IsLearned(int slot, ushort timeline) => learned.TryGetValue(slot, out var set) && set.Contains(timeline);

    private static int Slots<T>(T a) where T : ISlotAccess => Math.Min(a.Count, SlotCount);

    /// <summary>
    /// Every update: advance learning (an id that stays in its slot for <see cref="LearnSustainMs"/> while
    /// <paramref name="freezable"/> is learned; anything else restarts the count), remember each slot's timeline, and
    /// answer true when a slot's timeline changed to one not learned while it plays — an action starting. The first
    /// call after <see cref="ForgetLearned"/> or <see cref="Reseed"/> only records where each slot stands.
    /// </summary>
    public bool Observe<T>(T a, bool freezable, long nowMs) where T : ISlotAccess
    {
        int n = Slots(a);
        bool unknown = false;
        for (int s = 0; s < n; s++)
        {
            ushort id = a.GetTimeline(s);
            if (seeded && id != prev[s] && id != 0 && a.GetSpeed(s) != 0 && !IsLearned(s, id)) unknown = true;
            if (freezable && id != 0)
            {
                if (candidate[s] is { } c && c.Id == id)
                {
                    if (nowMs - c.Since >= LearnSustainMs) Learn(s, id);
                }
                else candidate[s] = (id, nowMs);
            }
            else candidate[s] = null;
            prev[s] = id;
        }
        seeded = true;
        return unknown;
    }

    private void Learn(int slot, ushort id)
    {
        if (!learned.TryGetValue(slot, out var set)) learned[slot] = set = [];
        set.Add(id);
    }

    /// <summary>
    /// While holding: a slot playing again on a timeline not learned as idle (with the fallback, any held slot playing
    /// again). The freeze must let go; that slot is never stopped again.
    /// </summary>
    public bool UnknownRestart<T>(T a) where T : ISlotAccess
    {
        if (held.Count == 0) return false;
        int n = Slots(a);
        for (int s = 0; s < n; s++)
        {
            if (a.GetSpeed(s) == 0) continue;
            ushort id = a.GetTimeline(s);
            if (id != 0 && !IsLearned(s, id)) return true;
            if (!rezeroIdleSwaps && held.Any(h => h.Slot == s)) return true;
        }
        return false;
    }

    /// <summary>The character changed (another address, or another drawn race/sex): forget everything learned.</summary>
    public void ForgetLearned()
    {
        learned.Clear();
        Reseed();
    }

    /// <summary>The draw object changed (a redraw): keep what was learned, but start the slot history over.</summary>
    public void Reseed()
    {
        Array.Clear(candidate);
        Array.Clear(prev);
        seeded = false;
    }

    /// <summary>
    /// Stop the slots: on <paramref name="entry"/> every playing slot; afterwards only a playing slot on a learned idle
    /// timeline (an idle variant swapped in). A slot with no timeline (id 0) plays nothing and is never touched — the
    /// game keeps an empty slot's speed at 1 by itself (seen in game). Returns how many slots were written.
    /// </summary>
    public int Hold<T>(T a, bool entry) where T : ISlotAccess
    {
        int n = Slots(a), writes = 0;
        for (int s = 0; s < n; s++)
        {
            float speed = a.GetSpeed(s);
            if (speed == 0) continue;
            ushort id = a.GetTimeline(s);
            if (id == 0) continue;
            if (!entry && !(rezeroIdleSwaps && IsLearned(s, id))) continue;
            a.SetSpeed(s, 0);
            writes++;
            int at = held.FindIndex(h => h.Slot == s);
            if (at >= 0) held[at] = (s, speed, id);
            else held.Add((s, speed, id));
        }
        return writes;
    }

    /// <summary>
    /// Let go: every held slot still at 0 gets its speed back, whatever plays in it now; a slot the game has set going
    /// again is left alone. The ledger is cleared; what was learned stays.
    /// </summary>
    public (int Restored, int Skipped) Release<T>(T a) where T : ISlotAccess
    {
        int restored = 0, skipped = 0;
        foreach (var (slot, speed, _) in held)
        {
            if (RestoreValue(a.GetSpeed(slot), speed) is { } v)
            {
                a.SetSpeed(slot, v);
                restored++;
            }
            else skipped++;
        }
        held.Clear();
        return (restored, skipped);
    }

    /// <summary>Forget the ledger without touching the game (the character is gone, changed, or between areas).</summary>
    public void Drop() => held.Clear();

    /// <summary>The speed to write back: the remembered one, only over the freeze's own 0.</summary>
    public static float? RestoreValue(float speedNow, float rememberedSpeed)
        => speedNow == 0 && rememberedSpeed != 0 ? rememberedSpeed : null;
}

/// <summary>What a change of owner means for the learned idle timelines.</summary>
internal enum OwnerChange { None, Reseed, Reset }

/// <summary>
/// Whose idle timelines the freeze has learned: the character's address, its drawn race/sex (null while unknown — nothing
/// drawn, or not a Human), and the draw object. Pure, so the redraw rule is testable headless.
/// </summary>
internal readonly record struct FreezeOwner(nint Character, ushort? RaceSex, nint DrawObject)
{
    /// <summary>
    /// The owner after this update, and what it means: another address or a known, different race/sex resets what was
    /// learned; only another draw object (a redraw) reseeds. A race/sex that is unknown mid-redraw is never a change.
    /// </summary>
    public static (FreezeOwner Owner, OwnerChange Change) Next(FreezeOwner current, nint address, ushort? raceSex, nint drawObject)
    {
        var next = new FreezeOwner(address, raceSex ?? (address == current.Character ? current.RaceSex : null), drawObject);
        if (address != current.Character) return (next, OwnerChange.Reset);
        if (raceSex is { } rs && current.RaceSex is { } was && rs != was) return (next, OwnerChange.Reset);
        if (drawObject != current.DrawObject) return (next, OwnerChange.Reseed);
        return (next, OwnerChange.None);
    }
}
