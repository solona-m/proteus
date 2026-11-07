using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Proteus.Services;

/// <summary>
/// Names the vanilla item that occupies a given equip slot and model set, so an imported content pack's
/// pieces can be listed as "Head — Far Eastern Schoolgirl's Hair Ribbon" rather than "Head — e6085".
/// <para/>
/// The lookup is the one <see cref="InvisibleRing"/> already relies on: an <c>Item</c> row's
/// <c>ModelMain</c> packs the model set in its low 16 bits, and <c>EquipSlotCategory</c> says which slot the
/// row belongs to. It is also the same lookup TexTools performs — the names this produces for a pack match
/// the ones a <c>.ttmp2</c> of the same mod records.
/// </summary>
public static class ItemNames
{
    /// <summary>
    /// Build a <c>(EquipSlotCategory, model set) → item name</c> resolver from the game's Item sheet.
    /// <para/>
    /// ONE pass, not one per lookup. <see cref="InvisibleRing"/> found the hard way that re-scanning this
    /// sheet is expensive enough to matter, and an import asks about every piece in the pack.
    /// <para/>
    /// Returns a resolver that answers null for everything when the sheet cannot be read. That is the right
    /// failure: the caller falls back to the set id, which is always available, so an unreadable sheet costs
    /// a nicer label and nothing else.
    /// </summary>
    public static Func<int, int, string?> Lookup(IDataManager data, IPluginLog? log = null)
    {
        try
        {
            var sheet = data.GetExcelSheet<Item>();
            if (sheet == null) return static (_, _) => null;

            return ContentSlot.NameLookup(
                sheet.Where(r => r.EquipSlotCategory.RowId != 0)
                     .Select(r => (r.RowId, r.EquipSlotCategory.RowId, r.ModelMain)),
                rowId => sheet.TryGetRow(rowId, out var row) ? row.Name.ExtractText() : string.Empty);
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "[Proteus] item names unavailable — pieces will be listed by set id");
            return static (_, _) => null;
        }
    }

    /// <summary>
    /// A <c>facewear model set → name</c> resolver from the game's Glasses sheet (facewear has no Item row), lowest row
    /// winning per set like <see cref="Lookup"/>. Answers null for everything when the sheet cannot be read.
    /// </summary>
    public static Func<int, string?> FacewearLookup(IDataManager data, IPluginLog? log = null)
    {
        try
        {
            var sheet = data.GetExcelSheet<Glasses>();
            if (sheet == null) return static _ => null;
            var names = new Dictionary<int, string>();
            foreach (var row in sheet)
            {
                int set = (int)row.Model & 0xFFFF;   // Model packs set | variant<<16
                if (set <= 0 || names.ContainsKey(set)) continue;
                var name = row.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name)) names[set] = name;
            }
            return set => names.TryGetValue(set, out var n) ? n : null;
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "[Proteus] facewear names unavailable — glasses will be listed by set id");
            return static _ => null;
        }
    }
}
