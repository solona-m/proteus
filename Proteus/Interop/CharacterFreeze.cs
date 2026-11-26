using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Proteus.Services;

namespace Proteus.Interop;

/// <summary>What the harness reads about the freeze (dev builds only; the pose is added by the harness itself).</summary>
internal readonly record struct FreezeSnapshot(
    FreezeState State, string? BusyReason, bool Available, bool Faulted, float[]? SlotSpeeds, int[]? SlotTimelines,
    IReadOnlyList<(int Slot, float Speed, ushort Timeline)> Held, IReadOnlyDictionary<int, IReadOnlySet<ushort>> Learned,
    int UnknownReleases, int Writes, int Restores, int RestoreSkipped, (int Restored, int Skipped)? LastRelease,
    bool AwaitingIdleTimeline, ushort[][]? TimelineIds, int DrawObjectChanged, int Releases);

/// <summary>
/// Holds the local player's body animation still while a Studio tool works on the character, so its idle animation
/// does not move the garment under the brush.
/// <para>
/// The character's animation plays through the action-timeline slots of its own <c>ActionTimelineSequencer</c>. While a
/// freeze is held, each framework update sets every playing slot's speed to 0 through the sequencer's own
/// <c>GetSlotSpeed</c>/<c>SetSlotSpeed</c>/<c>GetSlotTimeline</c> (named and resolved by FFXIVClientStructs, MIT) — no
/// hook. The game resets a slot's speed whenever a new timeline starts in it: a restart on a timeline learned as idle is
/// stopped again; anything else (an emote, an action) makes the freeze let go, so the action plays (see
/// <see cref="FreezeSlots"/>).
/// </para>
/// <para>
/// Releasing writes each held slot's speed back while it still reads Proteus's 0, on the same character, on the framework
/// thread; a slot the game has set going again is left alone. Proteus never leaves a speed of its own behind.
/// </para>
/// <see cref="CharacterFreezePolicy"/> decides when to hold; the Studio asks with <see cref="Want"/> every frame.
/// </summary>
internal sealed unsafe class CharacterFreeze : IDisposable
{
    /// <summary>
    /// Every condition under which the character is never frozen: events, NPC talk, crafting, gathering, cutscenes
    /// (the AFK idle camera too), zone transitions, mounts, emotes, performing … — whenever the game may wait on an
    /// animation finishing, or the user is doing something else with the character.
    /// <para>
    /// A fashion accessory (a parasol) is not among them: holding one is a standing idle loop like any other, and
    /// opening or putting it away plays a new action timeline, which releases the freeze at once.
    /// </para>
    /// </summary>
    internal static readonly ConditionFlag[] BusyConditions =
    [
        ConditionFlag.Occupied, ConditionFlag.Occupied30, ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.Occupied33, ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.Occupied38, ConditionFlag.Occupied39,
        ConditionFlag.OccupiedSummoningBell, ConditionFlag.TradeOpen, ConditionFlag.Crafting, ConditionFlag.PreparingToCraft,
        ConditionFlag.ExecutingCraftingAction, ConditionFlag.Gathering, ConditionFlag.ExecutingGatheringAction,
        ConditionFlag.Fishing, ConditionFlag.InCombat, ConditionFlag.Casting, ConditionFlag.Casting87, ConditionFlag.Mounted,
        ConditionFlag.Mounting, ConditionFlag.Mounting71, ConditionFlag.MountOrOrnamentTransition, ConditionFlag.RidingPillion,
        ConditionFlag.Emoting, ConditionFlag.InThatPosition, ConditionFlag.Jumping, ConditionFlag.Jumping61,
        ConditionFlag.BeingMoved, ConditionFlag.UsingChocoboTaxi, ConditionFlag.InFlight, ConditionFlag.CarryingObject,
        ConditionFlag.CarryingItem, ConditionFlag.Performing, ConditionFlag.PlayingMiniGame, ConditionFlag.ChocoboRacing,
        ConditionFlag.PlayingLordOfVerminion, ConditionFlag.Swimming, ConditionFlag.Diving, ConditionFlag.Unconscious,
        ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78, ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51, ConditionFlag.LoggingOut,
        ConditionFlag.EditingPortrait, ConditionFlag.MeldingMateria, ConditionFlag.OperatingSiegeMachine,
    ];

    /// <summary>
    /// (array, slot) pairs of the sequencer's timeline arrays left out of the activity hash: slot 0 of each, where the
    /// base idle variants swap while the character merely stands (measured in game: 8062↔8063). A full-body action in
    /// slot 0 is still caught — by its emote id, or by <see cref="FreezeSlots"/> as a timeline not learned as idle.
    /// </summary>
    internal static readonly (int Array, int Slot)[] IgnoredTimelineSlots = [(0, 0), (1, 0), (2, 0), (3, 0)];

    /// <summary>The player's own sequencer, as the freeze's ledger reads and writes it. Main thread only.</summary>
    private readonly struct SequencerSlots(ActionTimelineSequencer* seq) : ISlotAccess
    {
        public int Count => FreezeSlots.SlotCount;
        public float GetSpeed(int slot) => seq->GetSlotSpeed((uint)slot);
        public void SetSpeed(int slot, float speed) => seq->SetSlotSpeed((uint)slot, speed);
        public ushort GetTimeline(int slot) => seq->GetSlotTimeline((uint)slot);
    }

    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly Func<bool> enabled;
    private readonly IPluginLog log;
    private readonly bool resolved;
    private readonly CharacterFreezePolicy policy = new();
    private readonly FreezeSlots slots = new();

    private long lastWantMs = long.MinValue / 2;
    private int updatesSinceWant = int.MaxValue;
    private bool faulted;
    private bool lastFreezable;
    private FreezeOwner owner;      // whose idle timelines the ledger has learned
    private nint ledgerOwner;       // the character the held slots belong to

    private int unknownReleases, writes, restores, restoreSkipped;
    private (int Restored, int Skipped)? lastRelease;

    public CharacterFreeze(IFramework framework, IObjectTable objects, IClientState clientState, ICondition condition,
                           Func<bool> enabled, IPluginLog log)
    {
        this.framework = framework;
        this.objects = objects;
        this.clientState = clientState;
        this.condition = condition;
        this.enabled = enabled;
        this.log = log;
        try
        {
            resolved = ActionTimelineSequencer.Addresses.GetSlotSpeed.Value != 0
                    && ActionTimelineSequencer.Addresses.SetSlotSpeed.Value != 0
                    && ActionTimelineSequencer.Addresses.GetSlotTimeline.Value != 0;
        }
        catch (Exception e)
        {
            log.Warning(e, "character freeze unavailable: animation slot functions not found");
            resolved = false;
        }
        if (!resolved) log.Warning("character freeze unavailable: animation slot functions not found");
        framework.Update += OnUpdate;
    }

    /// <summary>The game's slot functions were found, and no freeze call has failed this session.</summary>
    public bool Available => resolved && !faulted;

    /// <summary>The harness's in-memory override of the setting for its session; null means the setting decides.</summary>
    internal bool? SessionOverride { get; set; }

    /// <summary>The setting in effect: the harness override if there is one, else the user's choice.</summary>
    public bool Enabled => SessionOverride ?? enabled();

    public FreezeState State => policy.State;

    /// <summary>The first reason the character counts as busy, while <see cref="State"/> is Busy.</summary>
    public string? BusyReason { get; private set; }

    /// <summary>The Studio asks for a freeze this frame. Main thread; lapses once 250 ms and 3 updates pass without one.</summary>
    public void Want()
    {
        lastWantMs = Environment.TickCount64;
        updatesSinceWant = 0;
    }

    private Character* Player()
    {
        var addr = objects.Length > 0 ? objects[0]?.Address ?? nint.Zero : nint.Zero;
        return (Character*)addr;
    }

    private bool BetweenAreas => condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];

    private void OnUpdate(IFramework _)
    {
        // A failed update latched the freeze off: nothing more runs, so nothing logs again.
        if (faulted) return;
        try
        {
            if (updatesSinceWant < int.MaxValue) updatesSinceWant++;
            long now = Environment.TickCount64;

            var chara = Player();
            FreezeSample? sample = null;
            BusyReason = null;
            bool busy = false, unknown = false, slot0Learned = false;
            if (chara != null)
            {
                sample = Sample(chara);
                BusyReason = BusyReasonOf((byte)chara->Mode, chara->InCombat, chara->IsCasting, chara->IsMounted(),
                                          f => condition[f]);
                busy = BusyReason != null;

                // What was learned belongs to this character and its drawn race/sex; a redraw only starts the slot
                // history over. Mid-redraw the race/sex is unknown, never a change.
                var (next, change) = FreezeOwner.Next(owner, (nint)chara, RaceSexOf(chara), (nint)chara->GameObject.DrawObject);
                owner = next;
                if (change == OwnerChange.Reset) slots.ForgetLearned();
                else if (change == OwnerChange.Reseed) slots.Reseed();

                if (Available)
                {
                    var access = new SequencerSlots(&chara->Timeline.TimelineSequencer);
                    unknown = slots.Observe(access, lastFreezable, now);
                    if (ledgerOwner == (nint)chara && slots.UnknownRestart(access)) unknown = true;
                    slot0Learned = slots.IsLearned(0, access.GetTimeline(0));
                }
            }

            var before = policy.State;
            var state = policy.Step(new FreezeInputs(Enabled, lastWantMs, updatesSinceWant, Available, unknown, slot0Learned,
                                                     (nint)chara, clientState.IsGPosing, BetweenAreas, busy, sample, now));
            if (state != FreezeState.Busy) BusyReason = null;

            if (state == FreezeState.Frozen && chara != null)
            {
                if (ledgerOwner != 0 && ledgerOwner != (nint)chara) slots.Drop();
                bool entry = before != FreezeState.Frozen;
                if (entry) writes = restores = restoreSkipped = 0;
                writes += slots.Hold(new SequencerSlots(&chara->Timeline.TimelineSequencer), entry, nowMs: now);
                ledgerOwner = (nint)chara;
            }
            else if (slots.Held.Count > 0)
            {
                if (unknown) unknownReleases++;
                ReleaseNow();
            }
            lastFreezable = policy.Freezable;
        }
        catch (Exception e)
        {
            // One attempt to hand the slots back, then the freeze is off for the session.
            try { ReleaseNow(); }
            catch
            {
                slots.Drop();   // never retried through the game: the slots self-heal on their next timeline
                ledgerOwner = 0;
            }
            faulted = true;
            log.Error(e, "character freeze: an update failed; freezing is off for this session");
        }
    }

    /// <summary>
    /// Hand the held slots back. Framework thread only: written back only on the same character outside a zone
    /// transition; otherwise the ledger is dropped without a write (the slots self-heal on their next timeline).
    /// </summary>
    private void ReleaseNow()
    {
        if (slots.Held.Count == 0)
        {
            ledgerOwner = 0;
            return;
        }
        var chara = Player();
        if (chara != null && (nint)chara == ledgerOwner && !BetweenAreas)
        {
            var r = slots.Release(new SequencerSlots(&chara->Timeline.TimelineSequencer), Environment.TickCount64);
            lastRelease = r;
            restores += r.Restored;
            restoreSkipped += r.Skipped;
            log.Debug($"character freeze: released, {r.Restored} slot(s) handed back, {r.Skipped} already playing");
        }
        else slots.Drop();
        ledgerOwner = 0;
    }

    /// <summary>The drawn race/sex, or null while unknown (nothing drawn, or not a Human).</summary>
    private static ushort? RaceSexOf(Character* chara)
    {
        var draw = chara->GameObject.DrawObject;
        if (draw == null || draw->GetObjectType() != ObjectType.CharacterBase) return null;
        var cb = (CharacterBase*)draw;
        return cb->GetModelType() == CharacterBase.ModelType.Human ? ((Human*)cb)->RaceSexId : null;
    }

    private static FreezeSample Sample(Character* chara)
    {
        var go = &chara->GameObject;
        var seq = &chara->Timeline.TimelineSequencer;
        int hash = CharacterFreezePolicy.TimelineHash(seq->TimelineIds, seq->TimelineIds2, seq->TimelineIds3,
                                                      seq->TimelineIds4, IgnoredTimelineSlots);
        return new FreezeSample(new Vector3(go->Position.X, go->Position.Y, go->Position.Z), go->Rotation,
                                (byte)chara->Mode, chara->ModeParam, chara->EmoteController.EmoteId, hash,
                                (nint)go->DrawObject);
    }

    /// <summary>
    /// The first reason the character counts as busy, in a fixed order: not standing in normal mode, combat, a cast, a
    /// mount, then the first <see cref="BusyConditions"/> flag set. Null when none holds.
    /// </summary>
    internal static string? BusyReasonOf(byte mode, bool inCombat, bool casting, bool mounted, Func<ConditionFlag, bool> flagSet)
    {
        if (!CharacterFreezePolicy.AllowedMode(mode)) return "mode:" + (CharacterModes)mode;
        if (inCombat) return "InCombat";
        if (casting) return "Casting";
        if (mounted) return "Mounted";
        foreach (var f in BusyConditions)
            if (flagSet(f)) return f.ToString();
        return null;
    }

    /// <summary>
    /// Model-space positions (metres) of the pelvis and the lower spine — bones only the body animation drives, the
    /// harness's check that the body really holds still — and the head (information only: look-at moves it). Main thread.
    /// </summary>
    internal (float[] Kosi, float[] SeboB, float[]? Kao)? PoseSample()
    {
        var cb = LiveCharacter.Player(objects);
        var pose = new XivLiveMesh.LivePose();
        if (cb == null || !pose.Read(cb)) return null;
        float[]? At(string bone)
        {
            if (!pose.TryGetBone(bone, out int b)) return null;
            ref readonly var m = ref pose.Pose(b);
            return [m.M41, m.M42, m.M43];
        }
        return At("j_kosi") is { } kosi && At("j_sebo_b") is { } seboB ? (kosi, seboB, At("j_kao")) : null;
    }

    /// <summary>Everything the harness's <c>/studio</c> reports about the freeze. Main thread.</summary>
    internal FreezeSnapshot Snapshot()
    {
        var chara = Player();
        ushort[][]? ids = null;
        float[]? speeds = null;
        int[]? timelines = null;
        if (chara != null)
        {
            var seq = &chara->Timeline.TimelineSequencer;
            ids = [((ReadOnlySpan<ushort>)seq->TimelineIds).ToArray(), ((ReadOnlySpan<ushort>)seq->TimelineIds2).ToArray(),
                   ((ReadOnlySpan<ushort>)seq->TimelineIds3).ToArray(), ((ReadOnlySpan<ushort>)seq->TimelineIds4).ToArray()];
            if (Available)
            {
                var access = new SequencerSlots(seq);
                speeds = Enumerable.Range(0, FreezeSlots.SlotCount).Select(access.GetSpeed).ToArray();
                timelines = Enumerable.Range(0, FreezeSlots.SlotCount).Select(sl => (int)access.GetTimeline(sl)).ToArray();
            }
        }
        return new FreezeSnapshot(State, BusyReason, Available, faulted, speeds, timelines, slots.Held.ToList(), slots.Learned,
                                  unknownReleases, writes, restores, restoreSkipped, lastRelease,
                                  policy.AwaitingIdleTimeline, ids, policy.DrawObjectChanges, policy.Releases);
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        try
        {
            // Always through the framework thread, where the ledger lives: an update in flight may still be holding.
            if (framework.IsInFrameworkUpdateThread) ReleaseNow();
            else if (!framework.RunOnFrameworkThread(ReleaseNow).Wait(TimeSpan.FromMilliseconds(500)))
                log.Information("character freeze: release left to self-heal");
        }
        catch (Exception e)
        {
            log.Information(e, "character freeze: release left to self-heal");
        }
    }
}
