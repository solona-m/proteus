using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Proteus.Services;

/// <summary>
/// Resolves the invisible ring the second skin hosts on when nothing the player wears loads in the shell's
/// own model space — "The Emperor's New Ring", set <c>a0053</c>.
/// <para/>
/// Why a ring and not the facewear host: the game race-deforms a model according to the race code of the
/// path it loaded it from. Facewear ships per-race (a Miqo'te loads <c>c0801e5501_met.mdl</c>), so a shell
/// cut from her c0201 body parts hosted there gets no deform and renders a whole race-size wrong. The ring
/// is different because WE write its EQDP entry: declaring the wearer's own race empty makes the game fall
/// through to the c0201 model we publish and deform it on the way — the same route a Midlander-only gear
/// mod takes onto every other race. Confirmed in game on a Miqo'te female.
/// <para/>
/// Glamourer needs a real Item row id to equip, and the redirect needs that row's model set, so both are
/// resolved from the sheet rather than hardcoded. Ring rows carry EquipSlotCategory 12 (RFinger — rings are
/// listed against the right finger and may be worn on either).
/// </summary>
public static class InvisibleRing
{
    /// <param name="Variant">The item's material variant. The shell's material is referenced from the model
    /// variant-relatively, so the game asks for it under chara/accessory/a0053/material/v{Variant}/ — publish
    /// it anywhere else and the carrier's mesh loads with no material and renders nothing. Not discoverable
    /// from the live resource tree the way a worn host's is, because the carrier is equipped AFTER the shell
    /// is built, so it has to come from the sheet.</param>
    public readonly record struct Identity(ulong ItemId, int ModelSet, int Variant);

    /// <summary>Emperor's New Ring model set — the same a0053 the shell's EQDP entry targets.</summary>
    public const int EmperorSetId = 53;

    /// <summary>EquipSlotCategory row for rings (Penumbra.GameData EquipSlot.RFinger = 12).</summary>
    private const uint RingSlotCategory = 12;

    /// <summary>
    /// The accessory slots an invisible carrier can be injected into, in the order the host chooser should
    /// try them, with the EquipSlotCategory row that names each in the Item sheet.
    /// <para/>
    /// Rings are BOTH listed under RFinger (12) — the sheet files a ring against the right finger and the
    /// game lets it be worn on either hand — so "ril" resolves the same item as "rir" and only the equip
    /// slot differs.
    /// <para/>
    /// An accessory SET covers every accessory slot (one id serves _ear, _nek, _wrs and _rir), so the
    /// Emperor's New pieces are looked for by that set in each slot rather than by name. A slot whose
    /// invisible piece cannot be found simply is not offered — see <see cref="ResolveFor"/>.
    /// </summary>
    public static readonly (string Slot, string EqdpSlot, uint Category)[] CarrierSlots =
    {
        ("rir", "RFinger", 12),
        ("ril", "LFinger", 12),
        ("wrs", "Wrists",  11),
        ("nek", "Neck",    10),
    };

    private static readonly object Gate = new();
    private static Identity? cached;
    private static readonly System.Collections.Generic.Dictionary<string, Identity?> cachedBySlot = new();
    private static bool warned;
    private static readonly System.Collections.Generic.HashSet<string> warnedSlots = new();

    /// <summary>
    /// The invisible carrier item for one accessory slot, or null when this slot has no such piece (or the
    /// sheet isn't readable yet). Callers must treat null as "don't offer this slot" rather than substitute
    /// anything: equipping a VISIBLE item as a carrier would put jewellery on the player that they never
    /// chose, and the shell replaces the carrier's model outright so they would never see it to remove it.
    /// <para/>
    /// Memoised per slot, and — unlike the single-slot path below — a definitive ABSENCE is memoised too.
    /// The memoise-on-success-only rule exists to survive a sheet that isn't readable yet; it is not meant to
    /// re-derive "this slot has no invisible piece" forever. Without the negative cache each call re-scanned
    /// the whole Item sheet, and the callers are not occasional: the carrier sweep walks all four slots on
    /// every redraw, so two unresolvable slots meant two full sheet scans per zone change and per gearset
    /// swap. A missing sheet still returns null UNCACHED, so it retries.
    /// </summary>
    public static Identity? ResolveFor(IDataManager data, IPluginLog log, string slot)
    {
        var entry = System.Array.Find(CarrierSlots, c => c.Slot == slot);
        if (entry.Slot == null) return null;

        lock (Gate)
        {
            // Present-and-null is a settled "no such piece", so this returns it rather than rescanning.
            if (cachedBySlot.TryGetValue(slot, out var hit)) return hit;
            try
            {
                var sheet = data.GetExcelSheet<Item>();
                if (sheet == null) return null;   // not readable YET — deliberately not cached

                foreach (var row in sheet.Where(r => r.EquipSlotCategory.RowId == entry.Category)
                             .OrderBy(r => r.RowId))
                {
                    int set = (int)(row.ModelMain & 0xFFFF);
                    if (set != EmperorSetId) continue;
                    int variant = (int)((row.ModelMain >> 16) & 0xFFFF);
                    var id = new Identity(row.RowId, set, variant);
                    cachedBySlot[slot] = id;
                    log.Information("[Proteus] invisible carrier: {0} -> item #{1} (model a{2:D4}, variant v{3:D4})",
                        slot, row.RowId, set, variant);
                    return id;
                }

                // Scanned the whole sheet and it is not there. That answer cannot change while the game is
                // running, so cache it — this is the branch that was costing a full rescan per redraw.
                cachedBySlot[slot] = null;
                if (warnedSlots.Add(slot))
                    log.Information("[Proteus] invisible carrier: no {0} item with model set a{1:D4} — that slot "
                                  + "will not be offered as a carrier", slot, EmperorSetId);
                return null;
            }
            catch (System.Exception ex)
            {
                // NOT cached: a throw is transient (a sheet mid-load, a Lumina hiccup), and latching it would
                // disable the slot for the session on one bad read.
                if (warnedSlots.Add(slot))
                    log.Warning("[Proteus] invisible carrier: {0} sheet read failed: {1}", slot, ex.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// The (item id, model set) of the Emperor's New Ring, or null if the Item sheet isn't readable yet.
    /// Locked and memoised on success only, for the same reason as the glasses resolver: caching a failure
    /// would disable the feature for the whole session if the first call beat the sheet being readable.
    /// </summary>
    public static Identity? Resolve(IDataManager data, IPluginLog log)
    {
        lock (Gate)
        {
            if (cached is { } hit) return hit;
            try
            {
                var sheet = data.GetExcelSheet<Item>();
                if (sheet == null)
                {
                    WarnOnce(log, "Item sheet unavailable");
                    return null;
                }

                foreach (var row in sheet.Where(r => r.EquipSlotCategory.RowId == RingSlotCategory)
                             .OrderBy(r => r.RowId))
                {
                    // ModelMain packs set | variant<<16 | … exactly like the Glasses sheet's Model column.
                    int set = (int)(row.ModelMain & 0xFFFF);
                    if (set != EmperorSetId) continue;
                    int variant = (int)((row.ModelMain >> 16) & 0xFFFF);
                    cached = new Identity(row.RowId, set, variant);
                    log.Information("[Proteus] invisible ring: using item #{0} (model a{1:D4}, variant v{2:D4})",
                        row.RowId, set, variant);
                    return cached;
                }

                WarnOnce(log, $"no ring item with model set a{EmperorSetId:D4} found");
                return null;
            }
            catch (System.Exception ex)
            {
                WarnOnce(log, $"sheet read failed: {ex.Message}");
                return null;
            }
        }
    }

    private static void WarnOnce(IPluginLog log, string reason)
    {
        if (warned) return;
        warned = true;
        log.Warning("[Proteus] invisible ring: {0} — will retry on a later composite", reason);
    }
}
