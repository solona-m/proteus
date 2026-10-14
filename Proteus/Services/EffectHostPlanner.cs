using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Which host carries each imported item effect (<see cref="ContentPiece.Effect"/>). Pure, so the rules are testable
/// without the game.
/// <list type="bullet">
/// <item>Only an invisible accessory CARRIER: its IMC entry is ours to replace. A worn item's entry may be another mod's
/// (material, hide toggles), and the whole entry is replaced.</item>
/// <item>Never a host whose own entry already has an effect, and one effect per host: an entry has one <c>VfxId</c>.</item>
/// <item>The carrier holding the piece's own placeholder first, then any other carrier drawn this build.</item>
/// <item>Each carrier slot has an id of its own (<see cref="VfxIdFor"/>): every slot of a set shares one effect folder,
/// so two carriers of one set on one id would load the same file.</item>
/// </list>
/// </summary>
internal static class EffectHostPlanner
{
    /// <summary>One host drawn this build.</summary>
    /// <param name="Variant">The variant it is drawn at, or null when unknown (then it cannot carry: the entry would miss).</param>
    /// <param name="OwnVfxId">The effect its own entry already names (0 = none), or null when its entry is unreadable.</param>
    /// <param name="IdFree">Whether the game ships nothing at the effect path its slot's id would use.</param>
    internal sealed record Candidate(int Index, string Slot, bool IsCarrier, int? Variant, int? OwnVfxId, bool IdFree = true);

    /// <summary>One effect to place, and the host its piece landed on (-1 when it is on none).</summary>
    internal sealed record Request(int Id, int OwnHost);

    internal sealed record Assignment(int Request, int Host, int VfxId);

    internal sealed record Drop(int Request, string Reason);

    /// <summary>Carrier slots in a fixed order; a slot's effect id is its position plus one.</summary>
    private static readonly string[] SlotOrder = ["rir", "ril", "wrs", "nek", "ear"];

    /// <summary>The effect id a carrier in <paramref name="slot"/> publishes under, or 0 for a slot that carries none.</summary>
    internal static int VfxIdFor(string slot) => Array.IndexOf(SlotOrder, slot) + 1;

    internal static (List<Assignment> Assigned, List<Drop> Dropped) Assign(
        IReadOnlyList<Candidate> candidates, IReadOnlyList<Request> requests)
    {
        var assigned = new List<Assignment>();
        var dropped = new List<Drop>();
        var taken = new HashSet<int>();

        string? WhyNot(Candidate c)
            => !c.IsCarrier ? "it is a worn item, whose effect slot is not Proteus's to take"
             : VfxIdFor(c.Slot) == 0 ? "its slot carries no item effect"
             : c.Variant == null ? "the variant it is drawn at is unknown"
             : c.OwnVfxId == null ? "its own entry could not be read"
             : c.OwnVfxId != 0 ? "it has an effect of its own"
             : !c.IdFree ? "the game already ships an effect at its path"
             : taken.Contains(c.Index) ? "it already carries another effect"
             : null;

        foreach (var r in requests)
        {
            var order = candidates.Where(c => c.Index == r.OwnHost)
                .Concat(candidates.Where(c => c.Index != r.OwnHost));
            var pick = order.FirstOrDefault(c => WhyNot(c) == null);
            if (pick != null)
            {
                taken.Add(pick.Index);
                assigned.Add(new Assignment(r.Id, pick.Index, VfxIdFor(pick.Slot)));
                continue;
            }

            var own = candidates.FirstOrDefault(c => c.Index == r.OwnHost);
            dropped.Add(new Drop(r.Id, candidates.Count == 0 ? "no accessory is drawn for it"
                : own != null ? $"its host cannot carry it ({WhyNot(own)}) and no other carrier can"
                : "no drawn carrier can carry it"));
        }
        return (assigned, dropped);
    }
}
