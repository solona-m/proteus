using System;
using System.Collections.Generic;
using System.Linq;
using Penumbra.Api.Enums;

namespace Proteus.Services;

/// <summary>
/// When to look through the player's hair mods for ones made for another race, and which: every hairstyle (a full
/// check) or only those of the mods that just changed. Pure — fed the signals with the time they arrived, asked for the
/// work once it is due — so the watcher only wires events and threads.
/// <para/>
/// A full check is owed when it is asked for (the setting switched on, "Check now", the first load that has not done
/// one yet), or when Penumbra changes many mods at once. A mod installed, switched on, set or edited in the player's
/// collection owes a check of that mod. Nothing is taken until the player's race has held still for a while: a race
/// tried on and put back in a Glamourer preview is not one to write ports for.
/// </summary>
internal sealed class HairPortScanSchedule
{
    /// <summary>How long signals must go quiet before the work is due: Penumbra reports a change before it has
    /// re-resolved, and an import of many mods arrives as a burst.</summary>
    public const long SettleMs = 400;

    /// <summary>How long a mod's <c>Edited</c> is taken for the echo of the watcher's own reload of it.</summary>
    public const long OwnEchoMs = 2000;

    /// <summary>How long the player's race must hold before a check writes for it.</summary>
    public const long RaceSettleMs = 5000;

    /// <summary>More mods than this waiting at once are checked as a full check: one pass over every hairstyle costs
    /// less than as many passes as mods.</summary>
    public const int FullOver = 25;

    /// <param name="Full">Every hairstyle the player's race can wear, not only <paramref name="Mods"/>'.</param>
    /// <param name="Force">Asked for by the player: look again even at hairstyles whose resolution is unchanged.</param>
    public sealed record Work(ushort Race, bool Full, IReadOnlyList<string> Mods, bool Force)
    {
        /// <summary>Whether hairstyles whose resolution is unchanged are looked at again: when asked for, and always
        /// for a check of particular mods — a model replaced at the same path resolves no differently, and the mod's
        /// Edited is the only sign of it.</summary>
        public bool LooksAgain => Force || !Full;
    }

    /// <summary>Work taken but not done — Penumbra could not be asked — owed again, due at <paramref name="dueAt"/>.</summary>
    public void Return(Work work, long dueAt)
    {
        lock (gate)
        {
            if (work.Full) { full = true; mods.Clear(); }
            else if (!full) foreach (var dir in work.Mods) mods.Add(dir);
            force |= work.Force;
            this.dueAt = Math.Max(dueAt, this.dueAt);
        }
    }

    private readonly object gate = new();
    private bool full, force;
    private readonly HashSet<string> mods = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> ownReloads = new(StringComparer.OrdinalIgnoreCase);
    private long dueAt;
    private ushort race;
    private long raceSince;

    /// <summary>The race the last walk saw, 0 before any.</summary>
    public ushort Race { get { lock (gate) return race; } }

    /// <summary>Whether any check is owed.</summary>
    public bool Pending { get { lock (gate) return full || mods.Count > 0; } }

    /// <summary>A check of every hairstyle is owed.</summary>
    /// <param name="forced">Asked for by the player, so unchanged hairstyles are looked at again.</param>
    public void OweFull(long now, bool forced = false)
    {
        lock (gate)
        {
            full = true;
            force |= forced;
            mods.Clear();
            dueAt = now + SettleMs;
        }
    }

    /// <summary>A Penumbra settings change. Only the player's collection's count; one with no mod named (all mods
    /// switched together) owes a full check.</summary>
    /// <param name="mine">It came from the collection the player is drawn with.</param>
    public void ModEvent(ModSettingChange change, string? dir, bool mine, long now)
    {
        if (!mine) return;
        switch (change)
        {
            case ModSettingChange.MultiEnableState:
            case ModSettingChange.MultiInheritance:
                OweFull(now);
                return;
            case ModSettingChange.EnableState:
            case ModSettingChange.Setting:
            case ModSettingChange.Priority:
            case ModSettingChange.Inheritance:
            case ModSettingChange.TemporarySetting:
            case ModSettingChange.Edited:
                if (string.IsNullOrEmpty(dir)) { OweFull(now); return; }
                if (change == ModSettingChange.Edited && IsOwnEcho(dir, now)) return;
                OweMod(dir, now);
                return;
            default:
                return;   // temporary mods: the live brush's and other plugins' previews, never a hair mod's install
        }
    }

    /// <summary>A mod newly installed.</summary>
    public void ModAdded(string dir, long now) => OweMod(dir, now);

    /// <summary>A mod gone: nothing of it is left to check.</summary>
    public void ModDeleted(string dir)
    {
        lock (gate) mods.Remove(dir);
    }

    /// <summary>The watcher reloaded <paramref name="dir"/> itself; the <c>Edited</c> that comes back is not news.</summary>
    public void OwnReload(string dir, long now)
    {
        lock (gate) ownReloads[dir] = now;
    }

    /// <summary>
    /// What the latest walk saw the player drawn as; 0 for nothing readable.
    /// </summary>
    /// <returns>Whether that is a different race than before: a check under way for the old one stops.</returns>
    public bool Walked(ushort seen, long now)
    {
        if (seen == 0) return false;
        lock (gate)
        {
            if (seen == race) return false;
            bool changed = race != 0;
            race = seen;
            raceSince = now;
            return changed;
        }
    }

    /// <summary>The work due at <paramref name="now"/>, taken; null when none is, or the race is not yet settled.</summary>
    public Work? Take(long now)
    {
        lock (gate)
        {
            if (!full && mods.Count == 0) return null;
            if (now < dueAt || race == 0 || now - raceSince < RaceSettleMs) return null;
            var work = new Work(race, full || mods.Count > FullOver, full ? [] : mods.ToList(), force);
            full = force = false;
            mods.Clear();
            return work;
        }
    }

    private void OweMod(string dir, long now)
    {
        if (string.Equals(dir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase)) return;
        lock (gate)
        {
            if (!full) mods.Add(dir);
            dueAt = now + SettleMs;
        }
    }

    private bool IsOwnEcho(string dir, long now)
    {
        lock (gate)
            return ownReloads.TryGetValue(dir, out var at) && now - at < OwnEchoMs;
    }
}
