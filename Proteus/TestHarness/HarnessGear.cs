using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Proteus.TestHarness;

/// <summary>One gear slot as Glamourer reports it: the item id and both dyes.</summary>
internal sealed record Worn(ulong Item, byte Dye1, byte Dye2);

/// <summary>
/// The harness's gear vocabulary: the ten slot names, and Glamourer's custom item id for a model (set + variant) so QA can
/// equip a model whether or not an item uses it. Pure.
/// </summary>
internal static class HarnessGear
{
    /// <summary>Glamourer's <c>CustomItemId</c> flag (Penumbra.GameData <c>IdTypes</c>): an id that names a model, not an item.</summary>
    public const ulong CustomFlag = 1UL << 48;
    public const ulong BonusFlag = 1UL << 49;

    /// <summary>The first id of Glamourer's Nothing/Smallclothes range (<c>uint.MaxValue - 512</c>): never sent as an item.</summary>
    public const ulong FirstGlamourerPseudoId = uint.MaxValue - 512UL;

    /// <summary>The ten gear slots, Glamourer's names, in Glamourer's order.</summary>
    public static readonly string[] Slots = ["Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger"];

    /// <summary>The slot's canonical name, matched case-insensitively; null for anything else (glasses and weapons included).</summary>
    public static string? Canonical(string? slot)
    {
        if (string.IsNullOrWhiteSpace(slot)) return null;
        foreach (var s in Slots)
            if (string.Equals(s, slot.Trim(), StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    /// <summary>Penumbra.GameData <c>FullEquipType</c> of a slot: Head 1 … Wrists 8, both rings 9 (Finger).</summary>
    public static byte EquipType(string slot) => slot switch
    {
        "Head" => 1, "Body" => 2, "Hands" => 3, "Legs" => 4, "Feet" => 5,
        "Ears" => 6, "Neck" => 7, "Wrists" => 8, "RFinger" or "LFinger" => 9,
        _ => throw new ArgumentException($"not a gear slot: {slot}", nameof(slot)),
    };

    /// <summary>
    /// Glamourer's custom id for a model in a slot: <c>set | variant&lt;&lt;32 | type&lt;&lt;40 | CustomFlag</c> (secondary 0, as
    /// for gear). Glamourer resolves it to the item using that model, or to a nameless custom item when none does.
    /// </summary>
    public static ulong CustomId(string slot, int set, int variant)
        => (ulong)(ushort)set | (ulong)(byte)variant << 32 | (ulong)EquipType(slot) << 40 | CustomFlag;

    /// <summary>The model a custom id names; null for a real item id, a bonus id, or Nothing.</summary>
    public static (int Set, int Variant)? TryDecode(ulong id)
    {
        if ((id & CustomFlag) == 0 || (id & BonusFlag) != 0) return null;
        return ((int)(id & 0xFFFF), (int)((id >> 32) & 0xFF));
    }

    /// <summary>
    /// The ten slots from Glamourer's state JSON (<c>Equipment.&lt;Slot&gt;.ItemId/Stain/Stain2</c>, as
    /// <c>DesignBindingService</c> reads it); null when there is no equipment block. A slot that cannot be read is missing.
    /// </summary>
    public static Dictionary<string, Worn>? Parse(JObject? state)
    {
        if (state?["Equipment"] is not JObject equipment) return null;
        var gear = new Dictionary<string, Worn>(StringComparer.Ordinal);
        foreach (var slot in Slots)
        {
            if (equipment[slot] is not JObject s || s["ItemId"] is not { } id) continue;
            try
            {
                gear[slot] = new Worn(id.ToObject<ulong>(), (byte)(s["Stain"]?.ToObject<int>() ?? 0),
                                      (byte)(s["Stain2"]?.ToObject<int>() ?? 0));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException
                                           or Newtonsoft.Json.JsonException) { /* unreadable: left out */ }
        }
        return gear;
    }
}
