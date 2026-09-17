using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Proteus.Services;

/// <summary>
/// Reads a model's game path as "which slot is this, and which item set does it belong to" — what a content
/// pack's pieces are named and grouped by (paths differing only in race code are one piece).
/// </summary>
public static class ContentSlot
{
    /// <summary>
    /// One equipment or accessory slot: the suffix a model path ends with, the label a person reads, and the
    /// <c>EquipSlotCategory</c> row that names it in the game's <c>Item</c> sheet. Both ring suffixes share 12
    /// (RFinger), matching <see cref="InvisibleRing.CarrierSlots"/>.
    /// </summary>
    public readonly record struct Slot(string Suffix, string Label, int Category);

    private static readonly Slot[] Slots =
    [
        new("met", "Head",       3),
        new("top", "Body",       4),
        new("glv", "Hands",      5),
        new("dwn", "Legs",       7),
        new("sho", "Feet",       8),
        new("ear", "Earrings",   9),
        new("nek", "Necklace",  10),
        new("wrs", "Bracelets", 11),
        new("rir", "Right ring",12),
        new("ril", "Left ring", 12),
        // Character parts have no Item row, so category 0: they name themselves by id.
        new("hir", "Hair", 0),
        new("fac", "Face", 0),
        new("til", "Tail", 0),
        new("zer", "Ears", 0),
    ];

    // chara/equipment/e6085/model/c0201e6085_met.mdl  ->  race 0201, kind e, set 6085, suffix met
    // chara/human/c0201/obj/hair/h0001/model/c0201h0001_hir.mdl -> race 0201, kind h, set 0001, suffix hir
    private static readonly Regex ModelRe = new(
        @"(?:^|/)c(?<race>\d{4})(?<kind>[a-z])(?<set>\d{4})_(?<suffix>[a-z0-9]+)\.mdl$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// What a model path says about itself. Null when it isn't a character model path; never guessed.
    /// </summary>
    /// <param name="Label">"Head", "Body", … or the raw suffix when it is one we don't know.</param>
    /// <param name="SetTag">"e6085" — the kind letter and set number, as modders write it.</param>
    /// <param name="RaceCode">"0201" — the equipment model code this file is authored for.</param>
    /// <param name="Category">The <c>EquipSlotCategory</c> to look an item name up under; 0 = no such item.</param>
    public readonly record struct Parsed(string Label, string SetTag, string RaceCode, int Category);

    public static Parsed? Parse(string modelGamePath)
    {
        if (string.IsNullOrEmpty(modelGamePath)) return null;
        var m = ModelRe.Match(modelGamePath.Replace('\\', '/'));
        if (!m.Success) return null;

        var suffix = m.Groups["suffix"].Value.ToLowerInvariant();
        var known = Slots.FirstOrDefault(s => s.Suffix == suffix);
        return new Parsed(
            // An unrecognised suffix labels itself; it still groups and gates, only the item lookup is lost.
            known.Label ?? suffix,
            m.Groups["kind"].Value.ToLowerInvariant() + m.Groups["set"].Value,
            m.Groups["race"].Value,
            known.Category);
    }

    /// <summary>
    /// Penumbra's <c>EquipSlot</c> name as this table's <see cref="Slot.Label"/>, or null for a name it does
    /// not know. Needed because a pack's IMC groups can differ only by slot.
    /// </summary>
    public static string? LabelForEquipSlot(string equipSlot) => equipSlot.ToLowerInvariant() switch
    {
        "head"    => "Head",
        "body"    => "Body",
        "hands"   => "Hands",
        "legs"    => "Legs",
        "feet"    => "Feet",
        "ears"    => "Earrings",
        "neck"    => "Necklace",
        "wrists"  => "Bracelets",
        "rfinger" => "Right ring",
        "lfinger" => "Left ring",
        _         => null,
    };

    /// <summary>The numeric set id out of a set tag ("e6085" → 6085), or null.</summary>
    public static int? SetIdOf(string setTag)
        => setTag.Length > 1 && int.TryParse(setTag[1..], out var n) ? n : null;

    /// <summary>
    /// How a piece is labelled: its slot, then the vanilla item that occupies that model set if the game
    /// knows one (the item the pack replaces, as Penumbra shows it), else the set id.
    /// </summary>
    public static string Label(Parsed p, string? itemName)
        => string.IsNullOrWhiteSpace(itemName) ? $"{p.Label} — {p.SetTag}" : $"{p.Label} — {itemName}";

    /// <summary>
    /// Names items by (EquipSlotCategory, model set) out of the game's <c>Item</c> sheet, built in one pass.
    /// A delegate so the importer can be unit-tested without game data.
    /// </summary>
    /// <param name="rows">Item rows as (RowId, EquipSlotCategory, ModelMain).</param>
    public static Func<int, int, string?> NameLookup(IEnumerable<(uint RowId, uint Category, ulong ModelMain)> rows,
        Func<uint, string> nameOf)
    {
        var best = new Dictionary<(int, int), (uint RowId, string Name)>();
        foreach (var (rowId, category, modelMain) in rows)
        {
            var key = ((int)category, (int)(modelMain & 0xFFFF));
            // Variants share a model set; lowest RowId wins so the label is stable.
            if (best.TryGetValue(key, out var have) && have.RowId <= rowId) continue;
            var name = nameOf(rowId);
            if (string.IsNullOrWhiteSpace(name)) continue;
            best[key] = (rowId, name);
        }
        return (category, setId) => best.TryGetValue((category, setId), out var hit) ? hit.Name : null;
    }
}
