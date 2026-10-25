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
    /// The choice the Studio's reading of a shoe or glove fills in when nothing is chosen there yet: its best guess, the
    /// limb's size, or a skin-only piece's own feet or hands. The rankings fill a row only when they have evidence behind
    /// them; a skin-only piece's own feet are its only row, and a sandal with little skin would otherwise leave Preview
    /// waiting on a row that is not shown. Null when there is nothing to fill or it is filled already.
    /// </summary>
    internal static (string Slot, BodyOption Option)? StudioPrefill(Layout layout, KeepEnds.StudioReading? read,
                                                                    IReadOnlyDictionary<string, BodyOption> from)
    {
        if (!layout.KeepEnds || read?.Pick is not { } pick) return null;
        string slot = read.Route == KeepEnds.Route.SkinOnly ? layout.EndSlot! : layout.Primary;
        return from.ContainsKey(slot) ? null : (slot, pick);
    }

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

    /// <summary>
    /// The refit-onto sizes chosen, kept only where the list for a new race still offers them — the same file — so a
    /// race change that leaves the body mod's sizes as they were (racial gear bent into the shape the bodies are made
    /// for: her race's list is the shared shape's) does not empty what was picked. Slots left with none are dropped.
    /// </summary>
    internal static Dictionary<string, List<BodyOption>> StillOffered(
        IReadOnlyDictionary<string, List<BodyOption>> chosen, Func<string, IReadOnlyList<BodyOption>> offered)
    {
        var kept = new Dictionary<string, List<BodyOption>>(StringComparer.Ordinal);
        foreach (var (slot, sizes) in chosen)
        {
            var now = offered(slot);
            var still = sizes.Select(s => now.FirstOrDefault(o => string.Equals(o.Rel, s.Rel, StringComparison.OrdinalIgnoreCase)))
                             .OfType<BodyOption>().ToList();
            if (still.Count > 0) kept[slot] = still;
        }
        return kept;
    }

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

    /// <summary>Whether the Body size tool's "Add the new body's piercings" takes effect: only the swap brings the body's
    /// skin in, and its piercings with it.</summary>
    internal static bool CarryPiercings(bool replaceSkin, bool carryPiercings) => replaceSkin && carryPiercings;

    /// <summary>How the Body size tool's whole-garment refit treats the body under the cloth (see <see cref="StudioSkin"/>).</summary>
    /// <param name="ClearForced">"Push cloth out of the body" is on whatever the box says, and the box shows it greyed.</param>
    /// <param name="ClearBody">Push the cloth clear of the body (see <see cref="BodyRetarget.Plan"/>).</param>
    /// <param name="TuckSlot">The slot whose cut skin is tucked in under the cloth (see <see cref="RefitCore.TuckSlot"/>).</param>
    /// <param name="DeepClear">See <see cref="RefitCore.DeepClearFor"/>.</param>
    /// <param name="KeepStandoff">See <see cref="RefitCore.KeepStandoffFor"/>.</param>
    /// <param name="SeeInside">See <see cref="RefitCore.SeeInsideFor"/>: only with a tuck.</param>
    /// <param name="RelaxSinkGuard">See <see cref="BodyRetarget.RelaxSinkTolerance"/>: the game's own top or legs with the
    /// new body's skin, as the automatic refit has it — in effect only with the body cleared.</param>
    internal readonly record struct StudioSkinRule(bool ClearForced, bool ClearBody, string? TuckSlot, bool DeepClear,
                                                   bool KeepStandoff, bool SeeInside, bool RelaxSinkGuard);

    /// <summary>
    /// The Body size tool's rule for the body under a whole garment's cloth. A modder's garment gets the user's boxes, as
    /// it always has: cloth tucked under the skin is often the author's intent, and the refit is written into their mod.
    /// The game's own top, with the new body's skin brought in, gets the automatic refit's rule for game gear
    /// (<see cref="AutoRefitDecisions.SkinFor"/>) whatever the "Push cloth out of the body" box says: the game buries its
    /// tops' cloth in its own body on purpose, and the refit is written into Proteus's own mod. Left to the box's default,
    /// the game's Witch's Coatee on Rosaline's Coconut was neither cleared nor tucked, and the bust stood open under its
    /// lapels. A top bent across the sexes keeps the boxes: nothing of this was measured there.
    /// </summary>
    /// <param name="gameGear">The garment is the game's own (see <see cref="AutoRefitDecisions.IsGameGear"/>).</param>
    /// <param name="crossSex">The user picked a race of the other sex (see <see cref="CrossSexRefit"/>).</param>
    internal static StudioSkinRule StudioSkin(bool replaceSkin, bool cutHidden, bool clearBody, bool gameGear,
                                              string garmentSlot, bool crossSex)
    {
        bool forced = gameGear && garmentSlot == "_top" && replaceSkin && !crossSex;
        bool clear = clearBody || forced;
        var game = AutoRefitDecisions.SkinFor(true);
        string? tuck = RefitCore.TuckSlot(cutHidden && clear, garmentSlot);
        // The relax guard follows the automatic refit, which plans the game's tops and legs (its gloves and shoes keep
        // their ends): with the box ticked the Studio's game legs are the automatic refit's, argument for argument.
        bool guard = gameGear && replaceSkin && !crossSex && (garmentSlot is "_top" or "_dwn") && game.RelaxSinkGuard;
        return new StudioSkinRule(forced, clear, tuck,
                                  forced && RefitCore.DeepClearFor(game.DeepClear, garmentSlot),
                                  forced && RefitCore.KeepStandoffFor(game.KeepStandoff, garmentSlot),
                                  forced && tuck != null && RefitCore.SeeInsideFor(game.SeeInside, garmentSlot), guard);
    }

    /// <summary>
    /// The Body size tool's refit of a whole garment through its own slot, under <paramref name="rule"/>: the call the
    /// panel makes, here so the tests that hold the Studio to the automatic refit make exactly that call.
    /// </summary>
    /// <param name="layOnBody">Replace the garment's skin with the new body's (see <see cref="BodyRetarget.Plan"/>).</param>
    /// <param name="cut">Leave out the new body's skin where the author deleted theirs.</param>
    /// <param name="pieces">Pieces moved whole (see <see cref="BodyRetarget.KeepShape"/>).</param>
    /// <param name="carryPiercings">See <see cref="CarryPiercings"/>.</param>
    /// <param name="liftUnderbust">See <see cref="BodyRetarget.Plan"/>.</param>
    internal static BodyRetarget.Planned StudioPlan(ModelParts garment, byte[] bytes, IReadOnlyList<BodyRetarget.SlotPair> pairs,
                                                    string garmentSlot, IReadOnlySet<int>? held, bool layOnBody,
                                                    bool acrossBodies, StudioSkinRule rule, bool cut,
                                                    IReadOnlyList<IReadOnlyCollection<int>>? pieces, bool carryPiercings,
                                                    bool liftUnderbust, Action<string>? log = null, Action<string>? warn = null)
        => BodyRetarget.Plan(garment, bytes, pairs, garmentSlot, held: held, replaceSkin: layOnBody,
                             acrossBodies: acrossBodies, clearBody: rule.ClearBody, cutHidden: cut, keepShape: pieces,
                             log: log, warn: warn, tuckSlot: rule.TuckSlot, deepClear: rule.DeepClear,
                             keepStandoff: rule.KeepStandoff, carryPiercings: carryPiercings,
                             liftUnderbust: liftUnderbust, seeInside: rule.SeeInside, relaxSinkGuard: rule.RelaxSinkGuard);

    /// <summary>The headless refit's <c>--piercings</c>: the new body's piercings come in only when it is given.</summary>
    internal static bool CliCarryPiercings(IReadOnlyDictionary<string, string> opts) => opts.ContainsKey("piercings");

    /// <summary>
    /// What a plan was made from: the model, each chosen pair, which parts were held and kept in shape, and the options.
    /// A plan whose key no longer matches is stale. The piercings choice counts only where it changes the plan (see
    /// <see cref="CarryPiercings"/>): never on a skin-only redraw, which swaps nothing.
    /// </summary>
    /// <param name="gameTop">The game's own top's rule applies (see <see cref="StudioSkinRule.ClearForced"/>): a plan made
    /// under it is never shown or saved for the garment judged a mod's, nor the other way round.</param>
    internal static string Key(string modelRel, string? fromBodyDir, Inputs inputs, IReadOnlyList<string> chosen,
                               IReadOnlyDictionary<string, BodyOption> from,
                               IReadOnlyDictionary<string, List<BodyOption>> to, IEnumerable<string> held,
                               IEnumerable<string>? keepShape, bool replaceSkin, bool cutHidden, bool clearBody,
                               bool carryPiercings = false, bool gameTop = false)
        => modelRel + "|from:" + (fromBodyDir ?? "") + "|"
         + string.Join("|", chosen.Select(s => $"{s}:{from[s].Rel}>{string.Join(",", to[s].Select(t => t.Rel))}"))
         + "|held:" + string.Join(",", held.OrderBy(h => h, StringComparer.Ordinal))
         + "|shape:" + string.Join(",", (keepShape ?? []).OrderBy(h => h, StringComparer.Ordinal))
         + (replaceSkin ? "|lay" : "")
         + (replaceSkin && !cutHidden && !inputs.KeepEnds ? "|whole" : "")
         + (clearBody ? "|clear" : "")
         + (inputs.KeepEnds ? "|keep" : "")
         + (CarryPiercings(replaceSkin, carryPiercings) && !inputs.SkinOnly ? "|pierce" : "")
         + (inputs.SkinOnly && inputs.EndSlot != null && from.TryGetValue(inputs.EndSlot, out var own)
                ? "|skin:" + own.Rel + ">" + string.Join(",", inputs.PrimaryTargets.Select(t => t.Rel))
                : "")
         + (gameTop ? "|gametop" : "");
}
