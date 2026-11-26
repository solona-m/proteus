using System;
using System.Numerics;

namespace Proteus.Services;

/// <summary>Where the Studio's character freeze stands, as the harness and the reason line report it.</summary>
internal enum FreezeState
{
    /// <summary>The setting (or the harness override) is off.</summary>
    Off,

    /// <summary>Nothing asked for a freeze in the last <see cref="CharacterFreezePolicy.WantGraceMs"/>.</summary>
    Idle,

    /// <summary>The game's animation slot functions are missing, or a freeze call failed this session.</summary>
    Unavailable,

    /// <summary>No player character, or one with nothing drawn yet.</summary>
    NoCharacter,

    /// <summary>In GPose: the game's own motion pause owns freezing there.</summary>
    GPose,

    /// <summary>Not standing in normal mode, or busy (combat, a cast, an event, a cutscene …).</summary>
    Busy,

    /// <summary>Something moved or changed lately; the freeze comes back once it has settled.</summary>
    Settling,

    /// <summary>The character's body animation is held.</summary>
    Frozen,
}

/// <summary>Which settle a change starts.</summary>
internal enum ActivityKind { None, Move, Mode, Appear }

/// <summary>What the freeze watches on the character between updates, copied out of the game.</summary>
internal readonly record struct FreezeSample(Vector3 Position, float Rotation, byte Mode, byte ModeParam, ushort EmoteId,
                                             int TimelineHash, nint DrawObject);

/// <summary>One update's worth of everything the freeze decides from.</summary>
/// <param name="UpdatesSinceWant">Framework updates since the Studio last asked: one long frame (a save) never lapses a want.</param>
/// <param name="UnknownTimeline">A timeline not learned as idle started, or a held slot played again on one: an action.</param>
/// <param name="Slot0Learned">Slot 0 (the base, full-body slot) is on a timeline learned as idle.</param>
internal readonly record struct FreezeInputs(bool Enabled, long LastWantMs, int UpdatesSinceWant, bool Ready, bool UnknownTimeline,
                                             bool Slot0Learned, nint Character, bool GPosing, bool BetweenAreas, bool Busy,
                                             FreezeSample? Sample, long NowMs);

/// <summary>
/// Whether to hold the player character's animation while a Studio tool works on it — the pure half of the freeze,
/// with no game types, so every rule is testable headless. <see cref="Proteus.Interop.CharacterFreeze"/> feeds it one
/// <see cref="FreezeInputs"/> per framework update and holds the character while it answers <see cref="FreezeState.Frozen"/>.
/// <para>
/// Activity is recorded first, on every update, before any early answer: a character that walked, emoted or redrew
/// while nothing wanted a freeze still settles before the first want freezes it.
/// </para>
/// </summary>
internal sealed class CharacterFreezePolicy
{
    /// <summary>A freeze lapses this long after the Studio last asked for one, so every way of leaving the tool lets go —
    /// and only once <see cref="WantGraceUpdates"/> updates have also passed without a want.</summary>
    public const long WantGraceMs = 250;

    /// <summary>Framework updates without a want before a freeze lapses: a single long frame (a save) is not leaving.</summary>
    public const int WantGraceUpdates = 3;

    /// <summary>
    /// An unknown start in slot 0 alone (nothing else changed) arms the long-timeline wait. False (the fallback): it only
    /// settles <see cref="SettleModeMs"/>.
    /// </summary>
    public const bool Slot0UnknownArmsWait = true;

    /// <summary>Stillness owed after walking or turning before the freeze returns.</summary>
    public const long SettleMoveMs = 750;

    /// <summary>
    /// Stillness owed after a mode, emote or action-timeline change, or leaving GPose or a zone transition: long enough
    /// for a stand-up or a one-shot emote to play out before the freeze returns.
    /// </summary>
    public const long SettleModeMs = 2500;

    /// <summary>Stillness owed after the character or its draw object (re)appears — a zone-in, a redraw.</summary>
    public const long SettleAppearMs = 500;

    /// <summary>How long the freeze waits for the standing-idle timeline after an action before freezing anyway.</summary>
    public const long LongTimelineCapMs = 10000;

    /// <summary>When true, an appear settle is timed from the first want after it rather than from when it appeared.</summary>
    public const bool AppearFromWant = false;

    public const float MoveEpsilonM = 0.001f, TurnEpsilonRad = 0.001f;

    private readonly long settleAppearMs;
    private readonly bool appearFromWant;
    private readonly bool slot0UnknownArmsWait;

    private nint prevCharacter;
    private FreezeSample? prevSample;
    private bool prevGPosing, prevBetweenAreas;
    private bool appearPending;
    private bool started;
    private long settleUntil;
    private long awaitIdleUntil;
    private int? idleHash;
    private FreezeState state = FreezeState.Idle;

    public CharacterFreezePolicy() : this(SettleAppearMs, AppearFromWant, Slot0UnknownArmsWait) { }

    /// <summary>The fallbacks under test; the shipped defaults are the constants.</summary>
    internal CharacterFreezePolicy(long settleAppearMs, bool appearFromWant, bool slot0UnknownArmsWait = Slot0UnknownArmsWait)
    {
        this.settleAppearMs = settleAppearMs;
        this.appearFromWant = appearFromWant;
        this.slot0UnknownArmsWait = slot0UnknownArmsWait;
    }

    /// <summary>The Studio is still asking: within <see cref="WantGraceMs"/> or fewer than <see cref="WantGraceUpdates"/> updates.</summary>
    internal static bool Wanting(in FreezeInputs i)
        => i.NowMs - i.LastWantMs <= WantGraceMs || i.UpdatesSinceWant < WantGraceUpdates;

    /// <summary>
    /// The last <see cref="Step"/> found the character freezable bar the setting and the Studio asking: present, standing,
    /// not busy or in GPose, settled. Timelines are learned as idle only while this holds.
    /// </summary>
    public bool Freezable { get; private set; }

    /// <summary>The state the last <see cref="Step"/> answered.</summary>
    public FreezeState State => state;

    /// <summary>The character a <see cref="FreezeState.Frozen"/> answer holds, else 0.</summary>
    public nint Target { get; private set; }

    /// <summary>Draw-object address changes seen this session (redraws), for the harness view.</summary>
    public int DrawObjectChanges { get; private set; }

    /// <summary>How many times a freeze has been let go (Frozen → anything else), for the harness view.</summary>
    public int Releases { get; private set; }

    /// <summary>A freeze is held off until the character is back on its standing-idle timeline (within the cap).</summary>
    public bool AwaitingIdleTimeline { get; private set; }

    public FreezeState Step(in FreezeInputs i)
    {
        Record(i);
        var next = Decide(i);
        if (state == FreezeState.Frozen && next != FreezeState.Frozen) Releases++;
        state = next;
        Target = next == FreezeState.Frozen ? i.Character : 0;
        return next;
    }

    private void Record(in FreezeInputs i)
    {
        long now = i.NowMs;
        var sample = i.Sample;
        bool present = i.Character != 0 && sample is { DrawObject: not 0 };
        bool otherModeChange = false;

        if (!started)
        {
            // The first update is the baseline (the plugin just loaded): nothing has changed yet, so nothing settles.
            started = true;
        }
        else if (i.Character != prevCharacter)
        {
            // Another character (a zone reload) or one coming back: a fresh baseline, and it settles once drawn.
            if (i.Character != 0) appearPending = true;
            prevSample = null;
        }
        else if (i.Character != 0 && sample is { } s && prevSample is { } p)
        {
            if (s.DrawObject != p.DrawObject)
            {
                if (p.DrawObject != 0 && s.DrawObject != 0) DrawObjectChanges++;
                appearPending = true;   // a redraw, or the draw object coming or going
            }
            if (Vector3.Distance(s.Position, p.Position) > MoveEpsilonM || MathF.Abs(WrapAngle(s.Rotation - p.Rotation)) > TurnEpsilonRad)
                Settle(now, SettleMoveMs);
            if (s.Mode != p.Mode || s.ModeParam != p.ModeParam || s.EmoteId != p.EmoteId || s.TimelineHash != p.TimelineHash)
            {
                ModeActivity(now);
                otherModeChange = true;
            }
        }
        else if (i.Character != 0 && sample is null)
        {
            appearPending = true;   // present but unreadable: it settles once it can be sampled
        }

        // An action starting that the hash cannot see (slot 0, or a repeat of the same emote): mode activity. Under the
        // fallback, slot 0 alone only settles.
        if (i.UnknownTimeline)
        {
            if (slot0UnknownArmsWait || otherModeChange) ModeActivity(now);
            else Settle(now, SettleModeMs);
        }
        if (prevGPosing && !i.GPosing) ModeActivity(now);   // (both false on the first update)
        if (prevBetweenAreas && !i.BetweenAreas) ModeActivity(now);

        if (appearPending && present)
        {
            bool wanting = Wanting(i);
            if (!appearFromWant)
            {
                Settle(now, settleAppearMs);
                appearPending = false;
            }
            else if (wanting)
            {
                Settle(i.LastWantMs, settleAppearMs);
                appearPending = false;
            }
        }

        prevCharacter = i.Character;
        prevSample = i.Character != 0 ? sample : null;
        prevGPosing = i.GPosing;
        prevBetweenAreas = i.BetweenAreas;
    }

    // Settles, and holds the freeze off until the character is back on its standing idle (within the cap) — armed even
    // before any idle hash is known, since slot 0 must still be on a learned idle timeline.
    private void ModeActivity(long now)
    {
        Settle(now, SettleModeMs);
        awaitIdleUntil = Math.Max(awaitIdleUntil, now + LongTimelineCapMs);
    }

    // A later, shorter settle never cuts an earlier, longer one.
    private void Settle(long from, long ms) => settleUntil = Math.Max(settleUntil, from + ms);

    private FreezeState Decide(in FreezeInputs i)
    {
        AwaitingIdleTimeline = false;
        bool present = i.Character != 0 && i.Sample is { DrawObject: not 0 } && !appearPending;
        var s = i.Sample.GetValueOrDefault();
        bool busy = i.Busy || !AllowedMode(s.Mode);
        // A missing idle hash counts as matched; slot 0 must still be on a learned idle timeline.
        bool notIdle = (idleHash is { } idle && s.TimelineHash != idle) || !i.Slot0Learned;
        bool waiting = present && i.NowMs < awaitIdleUntil && notIdle;
        Freezable = present && !i.GPosing && !busy && i.NowMs >= settleUntil && !waiting;

        if (!i.Ready) return FreezeState.Unavailable;
        if (!i.Enabled) return FreezeState.Off;
        if (!Wanting(i)) return FreezeState.Idle;
        if (!present) return FreezeState.NoCharacter;
        if (i.GPosing) return FreezeState.GPose;
        if (busy) return FreezeState.Busy;
        if (i.NowMs < settleUntil) return FreezeState.Settling;
        if (waiting)
        {
            AwaitingIdleTimeline = true;
            return FreezeState.Settling;
        }
        idleHash = s.TimelineHash;
        awaitIdleUntil = 0;
        return FreezeState.Frozen;
    }

    private static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= 2 * MathF.PI;
        while (a < -MathF.PI) a += 2 * MathF.PI;
        return a;
    }

    /// <summary>The freeze holds only for a character standing in its normal mode (<c>CharacterModes.Normal</c>).</summary>
    public static bool AllowedMode(byte mode) => mode == 1;

    /// <summary>
    /// One number for the four action-timeline arrays, leaving out the <paramref name="ignored"/> (array, slot) pairs
    /// that churn while the character merely stands idle. Any other slot changing changes the hash.
    /// </summary>
    public static int TimelineHash(ReadOnlySpan<ushort> ids1, ReadOnlySpan<ushort> ids2, ReadOnlySpan<ushort> ids3,
                                   ReadOnlySpan<ushort> ids4, ReadOnlySpan<(int Array, int Slot)> ignored)
    {
        var hash = new HashCode();
        Add(ref hash, 0, ids1, ignored);
        Add(ref hash, 1, ids2, ignored);
        Add(ref hash, 2, ids3, ignored);
        Add(ref hash, 3, ids4, ignored);
        return hash.ToHashCode();

        static void Add(ref HashCode hash, int array, ReadOnlySpan<ushort> ids, ReadOnlySpan<(int Array, int Slot)> ignored)
        {
            for (int s = 0; s < ids.Length; s++)
            {
                bool skip = false;
                foreach (var (a, sl) in ignored)
                    if (a == array && sl == s) { skip = true; break; }
                hash.Add(skip ? (ushort)0 : ids[s]);
            }
        }
    }
}
