using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// What the Studio's Body size tool refits a garment by, decided away from the panel so it can be tested without ImGui:
/// which slot the user must fill, which other slots are offered, what a refit is made from, and what it is called.
/// <para/>
/// A shoe or glove normally keeps its own foot or hand (<see cref="KeepEnds"/>): the leg or arm is then the slot filled
/// in, and the feet or hands go along by themselves, for their shape. Everything else is laid out as it always was.
/// </summary>
internal static class RetargetInputs
{
    /// <summary>How the tool is laid out for one garment.</summary>
    /// <param name="Primary">The slot whose sizes the user must choose: the garment's own, or its leg or arm.</param>
    /// <param name="WornSlot">The slot the garment is worn in, which the refit plans as.</param>
    /// <param name="EndSlot">The feet or hands kept, when the garment keeps them; they are offered in no row.</param>
    internal readonly record struct Layout(string Primary, string WornSlot, string? EndSlot)
    {
        public bool KeepEnds => EndSlot != null;
    }

    /// <summary>Whether a garment in <paramref name="slot"/> can keep its ends at all, which is when the box is shown.</summary>
    internal static bool OffersKeepEnds(string slot) => AutoRefitDecisions.LimbOf(slot) != null;

    /// <summary>
    /// The layout, from what is known at once — never from a reading still on its way, so the rows do not change under
    /// the user once the detector lands, except for the one way they can: a rig that turns out unreadable.
    /// </summary>
    /// <param name="keepEnds">The user's box: on unless unticked.</param>
    /// <param name="sourceHasLimb">The body mod it was made for has the leg or arm at this race; likewise the target.</param>
    /// <param name="endsUnreadable">The detector found the rig unreadable: no telling the foot from the leg.</param>
    internal static Layout LayoutFor(string wornSlot, bool keepEnds, bool sourceHasLimb, bool targetHasLimb,
                                     bool endsUnreadable)
        => keepEnds && !endsUnreadable && sourceHasLimb && targetHasLimb && AutoRefitDecisions.LimbOf(wornSlot) is { } limb
            ? new Layout(limb, wornSlot, wornSlot)
            : new Layout(wornSlot, wornSlot, null);

    /// <summary>
    /// A change of layout, which leaves every preset, remembered size and detection made for the old one wrong. The very
    /// first layout of a garment is not a change.
    /// </summary>
    internal static bool Flips(Layout? before, Layout now) => before is { } was && was != now;

    /// <summary>
    /// Every slot worth offering, the required one first: the others with more than one model to choose between, and
    /// never the kept end.
    /// </summary>
    internal static List<string> Slots(Layout layout, IEnumerable<string> catalogSlots, Func<string, int> distinct)
    {
        var slots = new List<string> { layout.Primary };
        foreach (string slot in catalogSlots)
            if (slot != layout.Primary && slot != layout.EndSlot && distinct(slot) > 1) slots.Add(slot);
        return slots;
    }

    /// <summary>What one refit is made from.</summary>
    /// <param name="PrimaryTargets">The sizes of <see cref="Layout.Primary"/> to refit onto, one refit each.</param>
    /// <param name="Shared">The other slots taking part, each one pair that rides along with every refit.</param>
    /// <param name="SkinOnly">A shoe or glove that reaches no leg or arm, and no leg size chosen by hand: its shape is
    /// kept and only its skin redrawn.</param>
    internal sealed record Inputs(string GarmentSlot, string Primary, string? EndSlot,
                                  IReadOnlyList<BodyOption> PrimaryTargets,
                                  IReadOnlyList<(string Slot, BodyOption From, BodyOption To)> Shared,
                                  bool SkinOnly)
    {
        public bool KeepEnds => EndSlot != null;
    }

    /// <summary>
    /// The slots taking part: those with both their sizes chosen. A slot with only one is the ordinary resting state of a
    /// part nobody is refitting, and sits out.
    /// </summary>
    internal static List<string> Chosen(IReadOnlyList<string> slots, IReadOnlyDictionary<string, BodyOption> from,
                                        IReadOnlyDictionary<string, List<BodyOption>> to)
        => slots.Where(s => from.ContainsKey(s) && to.TryGetValue(s, out var t) && t.Count > 0).ToList();

    /// <param name="route">What the detector read a kept-end garment as; only <see cref="KeepEnds.Route.SkinOnly"/> matters
    /// here, and only until a leg size is chosen by hand.</param>
    internal static Inputs Assemble(Layout layout, IReadOnlyList<string> slots,
                                    IReadOnlyDictionary<string, BodyOption> from,
                                    IReadOnlyDictionary<string, List<BodyOption>> to, KeepEnds.Route? route)
    {
        var chosen = Chosen(slots, from, to);
        var shared = chosen.Where(s => s != layout.Primary).Select(s => (s, from[s], to[s][0])).ToList();
        bool skinOnly = layout.KeepEnds && route == KeepEnds.Route.SkinOnly && !from.ContainsKey(layout.Primary);
        IReadOnlyList<BodyOption> targets = to.TryGetValue(layout.Primary, out var t) ? t : [];
        return new Inputs(layout.WornSlot, layout.Primary, layout.EndSlot, targets, shared, skinOnly);
    }

    /// <summary>
    /// Whether Preview can run. A refit needs its required slot's two sizes; a skin-only redraw needs only the garment's
    /// own feet or hands and the new body to have some.
    /// </summary>
    internal static bool Ready(Inputs inputs, IReadOnlyDictionary<string, BodyOption> from, bool targetHasEnd,
                               bool refused)
        => inputs.SkinOnly
            ? inputs.EndSlot != null && from.ContainsKey(inputs.EndSlot) && targetHasEnd
            : from.ContainsKey(inputs.Primary) && inputs.PrimaryTargets.Count > 0 && !refused;

    /// <summary>
    /// Whether a skin-only redraw is worth saving: it changed the model, or the garment is baked for another race — the
    /// bake is itself a change, though the skin may already match. Otherwise there is nothing to change.
    /// </summary>
    internal static bool SkinOnlyChanges(bool baking, byte[] before, byte[] after)
        => baking || !ReferenceEquals(before, after);

    /// <summary>One refit's sizes, as its option is named: the required slot's <paramref name="target"/>, then each
    /// other slot's — or <see cref="KeepEnds.SkinOnlyLabel"/> for a skin-only redraw, the same at every size.</summary>
    internal static List<string> ToLabels(Inputs inputs, IReadOnlyList<string> chosen, BodyOption target,
                                          IReadOnlyDictionary<string, List<BodyOption>> to)
        => inputs.SkinOnly
            ? [KeepEnds.SkinOnlyLabel]
            : chosen.Select(s => s == inputs.Primary ? target.Label : to[s][0].Label).ToList();

    /// <summary>The sizes a refit is recorded as made from: each chosen slot's, or the kept feet or hands for a skin-only
    /// redraw.</summary>
    internal static List<string> FromLabels(Inputs inputs, IReadOnlyList<string> chosen,
                                            IReadOnlyDictionary<string, BodyOption> from)
        => inputs.SkinOnly && inputs.EndSlot != null && from.TryGetValue(inputs.EndSlot, out var own)
            ? [own.Label]
            : chosen.Select(s => from[s].Label).ToList();

    /// <summary>
    /// What a plan was made from: the model, each chosen pair, which parts were held and kept in shape, and the options.
    /// A plan whose key no longer matches is stale.
    /// </summary>
    internal static string Key(string modelRel, string? fromBodyDir, Inputs inputs, IReadOnlyList<string> chosen,
                               IReadOnlyDictionary<string, BodyOption> from,
                               IReadOnlyDictionary<string, List<BodyOption>> to, IEnumerable<string> held,
                               IEnumerable<string>? keepShape, bool replaceSkin, bool cutHidden, bool clearBody)
        => modelRel + "|from:" + (fromBodyDir ?? "") + "|"
         + string.Join("|", chosen.Select(s => $"{s}:{from[s].Rel}>{string.Join(",", to[s].Select(t => t.Rel))}"))
         + "|held:" + string.Join(",", held.OrderBy(h => h, StringComparer.Ordinal))
         + "|shape:" + string.Join(",", (keepShape ?? []).OrderBy(h => h, StringComparer.Ordinal))
         + (replaceSkin ? "|lay" : "")
         + (replaceSkin && !cutHidden && !inputs.KeepEnds ? "|whole" : "")
         + (clearBody ? "|clear" : "")
         + (inputs.KeepEnds ? "|keep" : "")
         + (inputs.SkinOnly && inputs.EndSlot != null && from.TryGetValue(inputs.EndSlot, out var own)
                ? "|skin:" + own.Rel + ">" + string.Join(",", inputs.PrimaryTargets.Select(t => t.Rel))
                : "");
}
