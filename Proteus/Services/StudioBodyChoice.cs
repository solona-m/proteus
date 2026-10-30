using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Which body mod the Studio's Body size tool can offer, in one click, for a garment the chosen body mod has no sizes
/// for — decided away from the panel so it can be tested without ImGui or Penumbra.
/// <para/>
/// Players often wear one body mod for the chest and another for the legs, while the tool refits onto a single one.
/// When that one has no legs, the answer is the body mod the character is wearing for them — the same signal the tool
/// already trusts for its first guess at the body — and never a guess among every installed body mod. It is only ever
/// offered: picking it is the user's, like any other pick in the dropdown.
/// </summary>
internal static class StudioBodyChoice
{
    /// <param name="Known">Whether the answer can be trusted yet. Not while the body-mod scan is still running: the
    /// worn body cannot be matched to a mod before the mods are known, and a "none" then is not an answer to keep.</param>
    /// <param name="Dir">The body mod to offer, by folder; null when there is none.</param>
    internal readonly record struct Candidate(bool Known, string? Dir);

    /// <summary>
    /// What a candidate was worked out for: the garment, the race its sizes are filtered by, the body mods refitted
    /// onto and from, and whether the user wants a shoe's or glove's ends kept — a change of any of them is another
    /// question. The model is keyed by its file within its mod, which two mods can share; harmless, since the answer
    /// depends only on the garment's part, the race and the bodies.
    /// </summary>
    internal static string Key(string modelRel, string? race, string? bodyDir, string? sourceDir, bool keepEndsWanted)
        => modelRel + "|" + race + "|" + bodyDir + "|" + sourceDir + "|" + keepEndsWanted;

    /// <summary>
    /// Whether a body mod offering <paramref name="offered"/> sizes a garment worn in <paramref name="garmentSlot"/>:
    /// it has that part, or — a shoe or glove that will keep its own foot or hand — the leg or arm it is refitted by.
    /// </summary>
    /// <param name="limbUsable">Whether the ends will be kept once this body is picked. Without that the shoe is laid
    /// out as feet, and a body with only legs leaves it as short of sizes as before.</param>
    internal static bool Sizes(IEnumerable<string> offered, string garmentSlot, bool limbUsable)
    {
        string? limb = limbUsable ? AutoRefitDecisions.LimbOf(garmentSlot) : null;
        return offered.Any(s => s == garmentSlot || s == limb);
    }

    /// <summary>
    /// The body mod to offer for a garment worn in <paramref name="own"/>: the one worn for that part, else — a shoe or
    /// glove whose own foot or hand comes from the game or the current mod — the one worn for its leg or arm, when the
    /// ends will be kept. Only one that is not the current body and that sizes the garment at this race.
    /// </summary>
    /// <param name="own">The garment's part, or null for one that is not a body part (an accessory).</param>
    /// <param name="currentDir">The body mod being refitted onto now.</param>
    /// <param name="bodiesKnown">Whether the installed body mods have been found yet.</param>
    /// <param name="wornDirFor">The body mod the character's body model for a part comes from, or null.</param>
    /// <param name="offered">The parts a body mod has sizes for at this race, or null when it cannot be read.</param>
    /// <param name="limbUsable">Whether a shoe's or glove's ends would be kept with that body mod picked (see
    /// <see cref="Sizes"/>).</param>
    internal static Candidate WornCandidate(string? own, string? currentDir, bool bodiesKnown,
                                            Func<string, string?> wornDirFor,
                                            Func<string, IReadOnlyCollection<string>?> offered,
                                            Func<string, bool> limbUsable)
    {
        if (!bodiesKnown) return new Candidate(false, null);
        if (own == null) return new Candidate(true, null);

        foreach (string? slot in new[] { own, AutoRefitDecisions.LimbOf(own) })
        {
            if (slot == null || wornDirFor(slot) is not { } dir) continue;
            if (string.Equals(dir, currentDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (offered(dir) is { } parts && Sizes(parts, own, limbUsable(dir))) return new Candidate(true, dir);
        }
        return new Candidate(true, null);
    }

    /// <summary>
    /// The installed mod, of <paramref name="dirs"/>, a resolved file lies in. A whole folder only: <c>Bibo</c> does not
    /// own a file of <c>Bibo+</c>.
    /// </summary>
    internal static string? ModOwning(string resolvedFullPath, string modsRoot, IEnumerable<string> dirs)
    {
        foreach (string dir in dirs)
        {
            string root = Path.GetFullPath(Path.Combine(modsRoot, dir)) + Path.DirectorySeparatorChar;
            if (resolvedFullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return dir;
        }
        return null;
    }

    /// <summary>
    /// The "made for" body mod once <paramref name="pickedDir"/> is picked to refit onto: none when it is that same mod,
    /// which is a refit between its own sizes, else unchanged.
    /// </summary>
    internal static string? SourceAfterPick(string? fromBodyDir, string pickedDir)
        => string.Equals(fromBodyDir, pickedDir, StringComparison.OrdinalIgnoreCase) ? null : fromBodyDir;
}
