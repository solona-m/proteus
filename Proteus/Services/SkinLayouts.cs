namespace Proteus.Services;

/// <summary>
/// Which races' bodies share a skin UV layout, by a race code's first two digits: every female body shares one
/// (Hrothgar included), every male body another. None for a body with its own layout that shares with no one: Lalafell
/// of either sex (11, 12) and male Hrothgar (15).
/// </summary>
internal static class SkinLayouts
{
    /// <summary>'F' or 'M' for race number <paramref name="race"/> (1 for c01xx), null for a layout of its own.</summary>
    internal static char? GroupOf(int race) => race switch
    {
        11 or 12 or 15 => null,
        var r when r % 2 == 0 => 'F',
        _ => 'M',
    };

    /// <summary><see cref="GroupOf(int)"/> by the race's two digits ("01"); null when they are not a number.</summary>
    internal static char? GroupOf(string race) => int.TryParse(race, out int r) ? GroupOf(r) : null;

    /// <summary><see cref="GroupOf(int)"/> by a character code ("c0101"); null for anything else.</summary>
    internal static char? GroupOfCode(string? code)
        => code is { Length: 5 } && (code[0] == 'c' || code[0] == 'C') ? GroupOf(code.Substring(1, 2)) : null;
}
