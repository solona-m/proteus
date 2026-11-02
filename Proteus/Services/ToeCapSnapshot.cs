using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Proteus.Services;

/// <summary>
/// One composite's toe-cap answer (<see cref="CompositorService.ToeCapWanted"/>): which mods want a cap, which of those
/// take it from their own Toe Cap option, and each one's map. Immutable, so the composite thread and the editor can read
/// it at the same time.
/// </summary>
public sealed record ToeCapSnapshot(
    IReadOnlySet<string> Wanted,
    IReadOnlySet<string> FromOption,
    IReadOnlyDictionary<string, ToeCapMap> Maps)
{
    public static readonly ToeCapSnapshot Empty = From([]);

    /// <summary>The snapshot for each mod's resolved map (null = no cap), by mod folder in any case.</summary>
    internal static ToeCapSnapshot From(IEnumerable<(string Mod, ToeCapMap? Map)> maps)
    {
        var byMod = new Dictionary<string, ToeCapMap>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mod, map) in maps)
            if (map != null) byMod.TryAdd(mod, map);

        var fromOption = new List<string>();
        foreach (var (mod, map) in byMod)
            if (!map.BuiltIn) fromOption.Add(mod);

        return new ToeCapSnapshot(
            byMod.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            fromOption.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            byMod.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }
}
