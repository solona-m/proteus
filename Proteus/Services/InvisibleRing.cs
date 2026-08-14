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

    private static readonly object Gate = new();
    private static Identity? cached;
    private static bool warned;

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
