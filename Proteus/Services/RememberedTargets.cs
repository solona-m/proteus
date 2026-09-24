using System;
using System.Collections.Generic;

namespace Proteus.Services;

/// <summary>
/// Which of the sizes the Body size tool was last used with may be filled in for the garment now open.
/// <para/>
/// A player refits onto the body they are wearing over and over, across every garment they own, so the last answer
/// is very nearly always the next one — and filling it in for EVERY body part, not just the garment's own, is the
/// difference between one dropdown and four.
/// <para/>
/// Every part the panel draws, including ones this garment cannot reach: a part takes part in the refit only once BOTH
/// its ends are chosen, so a size to refit onto with no "made for" size beside it simply sits out. The panel says so on
/// the row and counts only paired parts in its header; nothing downstream can see the unpaired one, because every
/// consumer goes through the pairing.
/// </summary>
internal static class RememberedTargets
{
    /// <summary>
    /// The slots to fill, and the option file to fill each with.
    /// </summary>
    /// <param name="remembered">Slot to option file, as last chosen — see <c>Configuration.RetargetTargets</c>.</param>
    /// <param name="bodyDir">The body mod being refitted onto now, or null while none is chosen.</param>
    /// <param name="rememberedFor">The body mod those sizes were chosen in — see <c>Configuration.RetargetBodyDir</c>.</param>
    /// <param name="isDrawn">
    /// Whether the panel offers that part at all for the garment now open. A part the panel leaves out — one this body
    /// mod has a single model for, or none for this garment's race — has no dropdown to show a filled-in size in, and
    /// no way to untick one, so filling it would put state where the user cannot see or reach it.
    /// </param>
    /// <param name="hasTarget">Whether that slot already has a size chosen, which is never overwritten.</param>
    internal static IEnumerable<(string Slot, string Rel)> Fillable(
        IReadOnlyDictionary<string, string> remembered, string? bodyDir, string? rememberedFor,
        Func<string, bool> isDrawn, Func<string, bool> hasTarget)
    {
        // Sizes belong to the mod they were chosen in: another's are at best not found and at worst — two mods
        // laying their files out alike — a size of this mod the user never picked.
        if (bodyDir == null || !string.Equals(bodyDir, rememberedFor, StringComparison.OrdinalIgnoreCase))
            yield break;

        foreach (var (slot, rel) in remembered)
        {
            if (hasTarget(slot)) continue;      // a choice already made, including one made moments ago
            if (!isDrawn(slot)) continue;       // no dropdown to show it in, and none to untick it from
            yield return (slot, rel);
        }
    }
}
