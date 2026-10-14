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
    /// <param name="redirected">Every game path the manifest redirects. An effect counts only when the pack ships its
    /// <c>.avfx</c> too — the rule the import carries effects by: one switching on a game effect stays on the item.</param>
    internal static string? Detect(
        ProteusMetadata metadata, IEnumerable<PenumbraPackage.PackImc> manifestImc, IReadOnlySet<string> redirected)
    {
        var effects = manifestImc
            .Where(m => m.VfxId != 0 && KindOf(m.ObjectType) is { } kind
                     && redirected.Contains(PenumbraManipulations.VfxGamePath(kind, m.PrimaryId, m.VfxId)))
            .ToList();
        if (effects.Count == 0) return null;

        foreach (var piece in Pieces(metadata))
        {
            if (piece.Effect != null) continue;
            foreach (var (label, set) in ItemsOf(piece))
                if (effects.Any(m => m.PrimaryId == set
                                  && string.Equals(ContentSlot.LabelForEquipSlot(m.EquipSlot), label, StringComparison.Ordinal)))
                    return piece.Slot ?? label;
        }
        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex SetFolderRe = new(
        @"^chara/(?:accessory|equipment)/[ae](\d{4})/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The items (slot label, set id) a piece replaces. Its model files are archive entries a pack may name anything, so
    /// the set also comes from the game paths its materials were published under, with the slot the piece records.
    /// </summary>
    private static IEnumerable<(string Label, int Set)> ItemsOf(ContentPiece piece)
    {
        foreach (var model in piece.ModelFiles())
            if (ContentSlot.Parse(model) is { } slot && ContentSlot.SetIdOf(slot.SetTag) is { } set)
                yield return (slot.Label, set);

        if (piece.Slot == null) yield break;
        foreach (var gamePath in (piece.MaterialGamePaths ?? []).Values.SelectMany(v => v))
            if (SetFolderRe.Match(gamePath.Replace('\\', '/')) is { Success: true } m)
                yield return (piece.Slot, int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The set tag's kind letter for Penumbra's <c>ObjectType</c>, or null for one with no item effects.</summary>
    private static char? KindOf(string objectType)
        => objectType.Equals("Accessory", StringComparison.OrdinalIgnoreCase) ? 'a'
         : objectType.Equals("Equipment", StringComparison.OrdinalIgnoreCase) ? 'e'
         : null;

    private static IEnumerable<ContentPiece> Pieces(ProteusMetadata metadata)
        => (metadata.Content ?? [])
            .Concat((metadata.ContentGroups ?? []).SelectMany(g => g.Options).SelectMany(o => o.Pieces));
}
