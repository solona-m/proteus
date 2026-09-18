using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Proteus.Services;

/// <summary>
/// Resolves the invisible ring the second skin hosts on when nothing the player wears loads in the shell's
/// own model space — "The Emperor's New Ring", set <c>a0053</c>. A ring works because we write its EQDP entry,
/// so the game falls through to our c0201 model and race-deforms it.
/// Item row id and model set are resolved from the sheet rather than hardcoded.
/// </summary>
public static class InvisibleRing
{
    /// <param name="Variant">The item's material variant: the shell's material must be published under
    /// chara/accessory/a0053/material/v{Variant}/ or the carrier renders nothing.</param>
    public readonly record struct Identity(ulong ItemId, int ModelSet, int Variant);

    /// <summary>Emperor's New Ring model set — the same a0053 the shell's EQDP entry targets.</summary>
    public const int EmperorSetId = 53;

    /// <summary>EquipSlotCategory row for rings (Penumbra.GameData EquipSlot.RFinger = 12).</summary>
    private const uint RingSlotCategory = 12;

    /// <summary>
    /// The accessory slots an invisible carrier can be injected into, in the order the host chooser should
    /// try them, with the EquipSlotCategory row that names each in the Item sheet. Both rings are RFinger (12).
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
    /// sheet isn't readable yet). Callers must treat null as "don't offer this slot", never substitute a
    /// visible item. Memoised per slot including a definitive absence; a missing sheet or a throw is not cached.
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

                // Not in the sheet: that cannot change while the game runs, so cache it.
                cachedBySlot[slot] = null;
                if (warnedSlots.Add(slot))
                    log.Information("[Proteus] invisible carrier: no {0} item with model set a{1:D4} — that slot "
                                  + "will not be offered as a carrier", slot, EmperorSetId);
                return null;
            }
            catch (System.Exception ex)
            {
                // Not cached: a throw is transient.
                if (warnedSlots.Add(slot))
                    log.Warning("[Proteus] invisible carrier: {0} sheet read failed: {1}", slot, ex.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// The (item id, model set) of the Emperor's New Ring, or null if the Item sheet isn't readable yet.
    /// Memoised on success only, so an early call before the sheet is readable does not disable the feature.
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
