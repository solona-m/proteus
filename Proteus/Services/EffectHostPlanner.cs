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
            // A piece that found no carrier for its placeholder: its effect goes with it, as the content notice says.
            if (r.OwnHost < 0)
            {
                dropped.Add(new Drop(r.Id, "its piece found no free carrier slot"));
                continue;
            }

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

    /// <summary>
    /// The effects to place, one per owner that carries one, in placement order, each with the host its unit went to
    /// (-1 for a unit that found none). An owner serving several units (one piece, several materials) asks once, for the
    /// first host it reached.
    /// </summary>
    internal static List<(T Owner, int Host)> CollectRequests<T>(
        IEnumerable<(int Unit, int Host)> placed, Func<int, IEnumerable<T>> ownersOf,
        Func<T, bool> carriesEffect, Func<T, T, bool> same)
    {
        var result = new List<(T Owner, int Host)>();
        foreach (var (unit, host) in placed)
            foreach (var owner in ownersOf(unit))
                if (carriesEffect(owner) && !result.Any(r => same(r.Owner, owner)))
                    result.Add((owner, host));
        return result;
    }

    /// <summary>
    /// A host as an effect candidate, or null when it is not drawn this build (nothing would spawn its effect). The
    /// lookups only run for a carrier, since only a carrier can carry.
    /// </summary>
    internal static Candidate? CandidateFor(
        int index, string slot, bool hasBaseModel, char prefix, bool drawn,
        Func<int?> variantOf, Func<int, int?> ownVfxIdAt, Func<int, bool> idFree)
    {
        if (!drawn) return null;
        bool carrier = CanHostEffectOnly(hasBaseModel, prefix);
        int? variant = carrier ? variantOf() : null;
        int? own = variant is { } v ? ownVfxIdAt(v) : null;
        int id = VfxIdFor(slot);
        return new Candidate(index, slot, carrier, variant, own, id == 0 || idFree(id));
    }

    /// <summary>
    /// Whether a list of mods that lost their effect is news: said once per change of WHICH mods, and re-armed once
    /// nothing is lost. <paramref name="last"/> becomes what was said (null when nothing).
    /// </summary>
    internal static bool ShouldSay(ref string? last, IEnumerable<string> mods)
    {
        var now = string.Join("\n", mods.Distinct(StringComparer.OrdinalIgnoreCase)
                                       .OrderBy(m => m, StringComparer.OrdinalIgnoreCase));
        if (now.Length == 0) { last = null; return false; }
        if (string.Equals(now, last, StringComparison.Ordinal)) return false;
        last = now;
        return true;
    }

    /// <summary>
    /// Whether an effect-only piece's placeholder may go on a host: only an invisible ACCESSORY carrier (no base model,
    /// kind <c>a</c>), the one kind whose IMC entry is ours. Never a worn item, never facewear.
    /// </summary>
    internal static bool CanHostEffectOnly(bool hasBaseModel, char prefix) => !hasBaseModel && prefix == 'a';

    /// <summary>
    /// What a build published, as one string: an IMC entry only takes effect when the item is recreated, so any change
    /// here (host, variant, id, file — or the effect going away) has to force a redraw, and nothing else should.
    /// </summary>
    internal static string EffectKey(IEnumerable<(string Host, int Variant, int VfxId, string File)> published)
        => string.Join("\n", published.Select(p => $"{p.Host}/v{p.Variant}/{p.VfxId}={p.File}")
                                      .OrderBy(k => k, StringComparer.Ordinal));
}
