using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// A content pack imported before item effects were carried: its manifest still gives a piece's item an effect
/// (<c>Imc</c> with a <c>VfxId</c>), and its sidecar has no <see cref="ContentPiece.Effect"/> for it. That effect then
/// plays only on the vanilla item, which no longer shows the pack's model — re-importing fixes it.
/// </summary>
internal static class StaleEffectImport
{
    /// <summary>The label of the first piece whose item effect was left behind, or null when there is none.</summary>
    internal static string? Detect(ProteusMetadata metadata, IEnumerable<PenumbraPackage.PackImc> manifestImc)
    {
        var effects = manifestImc.Where(m => m.VfxId != 0).ToList();
        if (effects.Count == 0) return null;

        foreach (var piece in Pieces(metadata))
        {
            if (piece.Effect != null) continue;
            foreach (var model in piece.ModelFiles())
            {
                if (ContentSlot.Parse(model) is not { } slot || ContentSlot.SetIdOf(slot.SetTag) is not { } set) continue;
                if (effects.Any(m => m.PrimaryId == set
                                  && string.Equals(ContentSlot.LabelForEquipSlot(m.EquipSlot), slot.Label, StringComparison.Ordinal)))
                    return piece.Slot ?? slot.Label;
            }
        }
        return null;
    }

    private static IEnumerable<ContentPiece> Pieces(ProteusMetadata metadata)
        => (metadata.Content ?? [])
            .Concat((metadata.ContentGroups ?? []).SelectMany(g => g.Options).SelectMany(o => o.Pieces));
}
