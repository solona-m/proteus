using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Proteus.Services;

/// <summary>
/// The extra-skeleton tables: which extra skeleton a race loads with one item in one slot, so the item's
/// <c>j_ex_*</c> bones — a coat's tails, a skirt's pleats — exist on that race's character. The game's own,
/// <c>chara/xls/charadb/extra_top.est</c> and <c>extra_met.est</c>, and the entries a mod adds as manipulations.
/// <para/>
/// Per race, with no fall-through: a race without an entry loads no extra skeleton for the item, whatever the race
/// it falls through to for the model has. That is why a model baked for another race needs its own entry.
/// </summary>
internal static class EstTable
{
    /// <summary>The game's table for a slot, or null for a slot that has none.</summary>
    internal static string? GamePath(string slot) => slot switch
    {
        "_top" => "chara/xls/charadb/extra_top.est",
        "_met" => "chara/xls/charadb/extra_met.est",
        _ => null,
    };

    /// <summary>Penumbra's name for a slot's table ("Body", "Head"), or null for a slot that has none.</summary>
    internal static string? EstSlot(string slot) => slot switch
    {
        "_top" => "Body",
        "_met" => "Head",
        _ => null,
    };

    /// <summary>
    /// Where a race's extra skeleton for one entry lives: <c>chara/human/c0301/skeleton/top/t6085/skl_c0301t6085.sklb</c>.
    /// The letter is the folder's first — t for top, m for met — as the game spells it.
    /// </summary>
    internal static string? SkeletonPath(ushort race, string slot, int entry) => slot switch
    {
        "_top" => $"chara/human/c{race:D4}/skeleton/top/t{entry:D4}/skl_c{race:D4}t{entry:D4}.sklb",
        "_met" => $"chara/human/c{race:D4}/skeleton/met/m{entry:D4}/skl_c{race:D4}m{entry:D4}.sklb",
        _ => null,
    };

    /// <summary>
    /// A game table: a count, then that many (set, race) pairs, then that many entries, all 16-bit. Null when the bytes
    /// do not hold one.
    /// </summary>
    internal static Dictionary<(int Set, ushort Race), int>? Parse(byte[]? data)
    {
        if (data is not { Length: >= 4 }) return null;
        int count = BitConverter.ToInt32(data, 0);
        if (count < 0 || 4L + count * 6L > data.Length) return null;

        var table = new Dictionary<(int, ushort), int>(count);
        int values = 4 + count * 4;
        for (int i = 0; i < count; i++)
        {
            ushort set = BitConverter.ToUInt16(data, 4 + i * 4);
            ushort race = BitConverter.ToUInt16(data, 4 + i * 4 + 2);
            table[(set, race)] = BitConverter.ToUInt16(data, values + i * 2);
        }
        return table;
    }

    /// <summary>One entry a mod sets: race code (301), Penumbra's slot name, the item's set, and the skeleton entry.</summary>
    internal readonly record struct ModEntry(ushort Race, string Slot, int Set, int Entry);

    /// <summary>The Est entries among a mod's manipulations, in whichever shape they were read (JSON, or built here).</summary>
    internal static List<ModEntry> FromManipulations(IEnumerable<object>? manipulations)
    {
        var found = new List<ModEntry>();
        foreach (var m in manipulations ?? [])
        {
            JsonElement e;
            // Read back as JSON, or built here as an anonymous object that serializes; anything else is not an entry.
            try { e = m is JsonElement j ? j : JsonSerializer.SerializeToElement(m); }
            catch (Exception ex) when (ex is NotSupportedException or JsonException) { continue; }
            if (e.ValueKind != JsonValueKind.Object
                || !e.TryGetProperty("Type", out var type) || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "Est", StringComparison.OrdinalIgnoreCase)
                || !e.TryGetProperty("Manipulation", out var body) || body.ValueKind != JsonValueKind.Object)
                continue;

            string? race = body.TryGetProperty("Race", out var r) ? r.ToString() : null;
            string? gender = body.TryGetProperty("Gender", out var g) ? g.ToString() : null;
            string? slot = body.TryGetProperty("Slot", out var s) ? s.ToString() : null;
            if (race == null || gender == null || slot == null) continue;
            if (!body.TryGetProperty("SetId", out var set) || !set.TryGetInt32(out int setId)) continue;
            if (!body.TryGetProperty("Entry", out var entry) || !entry.TryGetInt32(out int value)) continue;

            int index = Array.FindIndex(ModelRace.Names, n => string.Equals(n, race, StringComparison.OrdinalIgnoreCase));
            if (index < 0) continue;
            int n = index * 2 + (string.Equals(gender, "Male", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
            found.Add(new ModEntry((ushort)(n * 100 + 1), slot, setId, value));
        }
        return found;
    }
}
