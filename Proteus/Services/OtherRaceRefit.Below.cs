using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// "Refit for" a race BELOW the drawn one — a race the drawn race falls through to: a Highlander-only mod's shirt made
/// into a Midlander's. The garment is bent down into that race's shape and kept there (see
/// <see cref="RacialModelBake.Route.Lowered"/>); the bodies it was made on are bent down with it.
/// </summary>
internal static partial class OtherRaceRefit
{
    /// <summary>
    /// The races from <paramref name="drawn"/> down to <paramref name="to"/>, both included, nearest first: c0301 to
    /// c0101 gives c0301, c0101; c1501 to c0101 gives c1501, c0901, c0101. Empty when <paramref name="to"/> is not on
    /// the drawn race's same-sex line.
    /// </summary>
    internal static IEnumerable<ushort> LineDown(ushort drawn, ushort to)
    {
        var line = RacialModelBake.SameSexLine(drawn).ToList();
        int at = line.IndexOf(to);
        if (at < 0) return [];
        return line.Take(at + 1).Prepend(drawn);
    }

    /// <summary>
    /// The bodies a garment was made on, for a refit DOWN to <paramref name="to"/>: the game's own when the made-for side
    /// is the game's — each race on the line read for its own body alone, so the body a Highlander draws bent is never
    /// taken for his own — else the made-for mod (or the refit-onto one, when they are the same).
    /// </summary>
    /// <param name="readGame">Reads a game path out of the game's own data; asked, through
    /// <see cref="VanillaBodyCatalog.Read"/>, for each race's own body and never the one it falls through to — so the
    /// framework thread's to call.</param>
    internal static BodySizeCatalog? MadeForBelow(string? fromDir, BodySizeCatalog? fromCatalog, BodySizeCatalog? onto,
                                                  ushort drawn, ushort to, Func<string, byte[]?> readGame)
    {
        if (fromDir != VanillaBodyCatalog.Key) return fromCatalog ?? onto;
        var read = CrossSexRefit.MadeForLine(drawn, to).Select(r => VanillaBodyCatalog.Read($"{r:D4}", readGame, drawn: false)).ToList();
        return new BodySizeCatalog(read.FirstOrDefault()?.ModRoot ?? VanillaBodyCatalog.Dir,
                                   read.SelectMany(c => c.Options).ToList());
    }

    /// <summary>Who wears a saved refit for a picked race, as the panel says it.</summary>
    internal enum WearNote
    {
        /// <summary>That race alone: every other keeps drawing the model it draws now.</summary>
        OnlyPicked,

        /// <summary>That race, and every race that falls through to it with no model of its own for the item: a
        /// Midlander model is what they draw (see <see cref="CrossSexRefit.FallThroughTo"/>).</summary>
        PickedAndFallThrough,
    }

    /// <summary>A lowered refit, or one for the other sex, lands on a shared shape whole families fall through to.</summary>
    internal static WearNote Wears(RacialModelBake.Route route)
        => route.Lowered || route.ByAlias ? WearNote.PickedAndFallThrough : WearNote.OnlyPicked;

    /// <summary>
    /// Whether to warn that the races falling through to a lowered refit load no extra skeleton for it: the garment
    /// leans on one, and the entry saved gives it to the picked race alone.
    /// </summary>
    internal static bool WarnsFallThroughBones(RacialModelBake.Route route, EstPlan? est)
        => (route.Lowered || route.ByAlias) && est is { Outcome: EstOutcome.Carry or EstOutcome.Present };
}
