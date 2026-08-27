using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus;

/// <summary>
/// The race/gender a model path is authored for, and the fall-through the game uses when a set ships no
/// model for one.
/// <para/>
/// Out here rather than inside the shell builder because three parts of Proteus need the same answers and
/// must not disagree: the composite decides whether a piece can be worn at all, the importer says which
/// races a pack covers, and the panel tells someone why an enabled pack shows nothing. A pack authored for
/// Miqo'te F named "0801" in one place and "race 8" in another is how that goes wrong.
/// </summary>
public static class ModelRace
{
    /// <summary>Indexed by <c>(index - 1) / 2</c> — the game's own order.</summary>
    public static readonly string[] Names =
    [
        "Midlander", "Highlander", "Elezen", "Miqote", "Roegadyn",
        "Lalafell", "AuRa", "Hrothgar", "Viera",
    ];

    /// <summary>The leading race/gender index of a char code ("0801" → 8), or null when it carries none.
    /// Two digits, matching how the game numbers them: odd = male, even = female, and (n-1)/2 indexes
    /// <see cref="Names"/>.
    /// <para/>
    /// Bounded to the playable range 1..18. Out of it there is no race, so callers get null rather than a
    /// number: unbounded, <see cref="Fallback"/>'s catch-all arm made every unknown index look like a child
    /// of Midlander, so the fall-through check waved through pairs like c9101 -> c0101. A guard that decides
    /// how a shell is published should not accept a race that cannot exist.</summary>
    public static int? Index(string? code)
        => code is { Length: >= 2 } && int.TryParse(code.AsSpan(0, 2), out var n)
        && n > 0 && n <= Names.Length * 2 ? n : null;

    /// <summary>
    /// The race the game falls through to when a set declares no model for <paramref name="n"/>, or 0 at
    /// the root. Mirrors the game's own table (the same one Penumbra.GameData's <c>GenderRace.Fallback</c>
    /// encodes): most races fall to their own gender's Midlander, with three exceptions — Hrothgar males
    /// go to Roegadyn males, Lalafell females to Lalafell males, and Midlander females to Midlander males.
    /// </summary>
    public static int Fallback(int n) => n switch
    {
        1  => 0,   // Midlander male — the root, nothing below it
        2  => 1,   // Midlander female -> Midlander male
        11 => 1,   // Lalafell male    -> Midlander male
        12 => 11,  // Lalafell female  -> Lalafell male
        15 => 9,   // Hrothgar male    -> Roegadyn male
        _  => n % 2 == 1 ? 1 : 2,
    };

    /// <summary>
    /// Whether a code is the SHARED shape the game resizes onto everyone — Midlander male or female, the
    /// two roots every other race's fall-through chain ends at.
    /// <para/>
    /// This is the distinction that decides whether a content piece can be worn by anyone or only by one
    /// race. Gear at c0101/c0201 is deformed onto the wearer by the game; gear at any other code is already
    /// that race's shape and must not be.
    /// </summary>
    public static bool IsSharedShape(string? code) => Index(code) is 1 or 2;

    /// <summary>A code as a person reads it — "0801" → "Miqote F". Falls back to the raw code when it names
    /// no playable race, because a message about a path is worse than useless if it renames the path.</summary>
    public static string Describe(string? code)
        => Index(code) is not { } n
            ? code ?? "?"
            : $"{Names[(n - 1) / 2]} {(n % 2 == 1 ? "M" : "F")}";

    /// <summary>Several codes, deduplicated and in the order given — for "this pack covers …".</summary>
    public static string DescribeAll(IEnumerable<string> codes)
        => string.Join(", ", codes.Select(Describe).Distinct(StringComparer.Ordinal));
}
