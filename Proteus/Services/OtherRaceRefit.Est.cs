using System;
using System.Collections.Generic;
using System.Linq;
using XivLiveMesh;

namespace Proteus.Services;

/// <summary>What a model baked for another race needs of the extra-skeleton table to move as it did.</summary>
internal enum EstOutcome
{
    /// <summary>The garment leans on no bone the race's own skeleton lacks.</summary>
    NotNeeded,

    /// <summary>The race already loads an extra skeleton for the item — the mod's entry or the game's — and it has the
    /// bones.</summary>
    Present,

    /// <summary>The drawn race's entry, given to the new race too: its skeleton for that entry ships in the mod or the
    /// game and has the bones.</summary>
    Carry,

    /// <summary>Bones the new race would not have. The bake is refused over them, naming them (see
    /// <see cref="OtherRaceWearer.Build"/>).</summary>
    Missing,
}

/// <param name="Entry">The extra skeleton's entry, with <see cref="EstOutcome.Present"/> or <see cref="EstOutcome.Carry"/>.</param>
/// <param name="Skeleton">That skeleton, read; its bones join the wearer's so they bend like their parents.</param>
/// <param name="Missing">With <see cref="EstOutcome.Missing"/>, the bones not found.</param>
internal sealed record EstPlan(EstOutcome Outcome, int Entry = 0, HavokSkeleton? Skeleton = null,
                               IReadOnlyList<string>? Missing = null)
{
    public static readonly EstPlan NotNeeded = new(EstOutcome.NotNeeded);
}

internal static partial class OtherRaceRefit
{
    /// <summary>
    /// What the extra-skeleton table must say for <paramref name="target"/> to load the bones a garment drawn as
    /// <paramref name="drawn"/> leans on beyond its base skeleton.
    /// <para/>
    /// An entry the race already has — the mod's own, else the game's — is used as it is. Otherwise the drawn race's
    /// entry is carried over, but only when the new race's skeleton for it is a file the mod itself ships or the game
    /// has: one that exists only through some other mod in the creator's collection would not be there for the people
    /// who install this one, and the entry would point them at nothing.
    /// </summary>
    /// <param name="weighted">The bones the garment's vertices lean on.</param>
    /// <param name="baseBones">The new race's base skeleton.</param>
    /// <param name="modEst">The Est entries the garment's option and the mod's default data set.</param>
    /// <param name="gameEst">The game's table for the slot.</param>
    /// <param name="skeletonAt">A skeleton by game path, from the mod's own files or the game's — never the collection.</param>
    internal static EstPlan PlanEst(ushort target, ushort drawn, string slot, int set,
                                    IEnumerable<string> weighted, IReadOnlySet<string> baseBones,
                                    IReadOnlyList<EstTable.ModEntry> modEst,
                                    IReadOnlyDictionary<(int Set, ushort Race), int>? gameEst,
                                    Func<string, HavokSkeleton?> skeletonAt)
    {
        var needed = weighted.Where(b => !baseBones.Contains(b)).Order(StringComparer.Ordinal).ToList();
        if (needed.Count == 0) return EstPlan.NotNeeded;
        if (EstTable.EstSlot(slot) is not { } estSlot) return new EstPlan(EstOutcome.Missing, Missing: needed);

        int? EntryOf(ushort race)
        {
            foreach (var e in modEst)
                if (e.Race == race && e.Set == set && string.Equals(e.Slot, estSlot, StringComparison.OrdinalIgnoreCase))
                    return e.Entry;
            return gameEst != null && gameEst.TryGetValue((set, race), out int value) ? value : null;
        }

        List<string> Lacking(HavokSkeleton? skeleton)
            => skeleton == null ? needed : needed.Where(b => !skeleton.Names.Contains(b)).ToList();

        if (EntryOf(target) is { } own and > 0)
        {
            var skeleton = EstTable.SkeletonPath(target, slot, own) is { } at ? skeletonAt(at) : null;
            var lacking = Lacking(skeleton);
            return lacking.Count == 0 ? new EstPlan(EstOutcome.Present, own, skeleton)
                                      : new EstPlan(EstOutcome.Missing, Missing: lacking);
        }
        if (EntryOf(drawn) is { } carried and > 0)
        {
            var skeleton = EstTable.SkeletonPath(target, slot, carried) is { } at ? skeletonAt(at) : null;
            var lacking = Lacking(skeleton);
            return lacking.Count == 0 ? new EstPlan(EstOutcome.Carry, carried, skeleton)
                                      : new EstPlan(EstOutcome.Missing, Missing: lacking);
        }
        return new EstPlan(EstOutcome.Missing, Missing: needed);
    }
}
