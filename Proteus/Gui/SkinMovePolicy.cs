using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The Studio's rules for "Move skin too", apart from the ImGui that draws them, so they can be pinned by tests: when
/// skin may be painted, picked, locked or hit, and what a skin row in the part list offers. The part list, the model
/// view and the live character all ask here, so the three cannot disagree.
/// </summary>
internal static partial class SkinMovePolicy
{
    /// <summary>The checkbox is off every time the plugin starts: it is never saved.</summary>
    public static bool DefaultOn => false;

    /// <summary>What a skin part's row in the part list offers.</summary>
    public enum SkinRowKind
    {
        /// <summary>Under a brush, skin held still: a ticked box that cannot be unticked.</summary>
        FixedTicked,

        /// <summary>Under a brush, skin that moves: a box that locks it like any part.</summary>
        Lockable,

        /// <summary>Under Move, Rotate or Scale, skin held still: no lock box, and a radio that cannot be chosen.</summary>
        MoveDisabled,

        /// <summary>Under Move, Rotate or Scale, skin that moves: a lock box and a radio like any part.</summary>
        MovePickable,
    }

    /// <summary>Which tooltip the checkbox shows.</summary>
    public enum CheckboxTip
    {
        /// <summary>What it does.</summary>
        Explains,

        /// <summary>Bridge and wind never move skin.</summary>
        NotThisBrush,

        /// <summary>The clothes could not be bound to the skin, so it stays still.</summary>
        Failed,
    }

    /// <summary>
    /// Whether the checkbox can be changed, and what its tooltip says: not while a stroke, move or grab is open (the
    /// operation began with the answer it has), not under Bridge or Wind, and not once binding the clothes has failed.
    /// </summary>
    public static (bool Enabled, CheckboxTip Tip) Checkbox(bool busy, bool bridgeOrWind, bool bindingFailed)
        => (!busy && !bridgeOrWind && !bindingFailed,
            bindingFailed ? CheckboxTip.Failed : bridgeOrWind ? CheckboxTip.NotThisBrush : CheckboxTip.Explains);

    /// <summary>Skin moves now: the checkbox is on, the tool is not one of the two that never move skin, and the
    /// clothes have been bound to the skin.</summary>
    public static bool Applies(bool toggleOn, bool bridgeOrWind, bool followReady)
        => toggleOn && !bridgeOrWind && followReady;

    public static SkinRowKind SkinRow(bool skinMoves, bool moving)
        => moving ? skinMoves ? SkinRowKind.MovePickable : SkinRowKind.MoveDisabled
                  : skinMoves ? SkinRowKind.Lockable : SkinRowKind.FixedTicked;

    /// <summary>A part may be locked: anything but skin, and skin under Body size (whose refit moves it) or while
    /// skin moves.</summary>
    public static bool LockAllowed(bool isSkin, bool retarget, bool skinMoves) => !isSkin || retarget || skinMoves;

    /// <summary>A part may be chosen to move: not locked, and not skin unless skin moves.</summary>
    public static bool MovePickable(bool isSkin, bool locked, bool skinMoves) => !locked && (!isSkin || skinMoves);

    /// <summary>A brush or pick hitting a skin triangle counts as a hit, rather than passing through to what is
    /// behind it, only while skin moves.</summary>
    public static bool HitCounts(bool skinTriangle, bool skinMoves) => !skinTriangle || skinMoves;

    /// <summary>
    /// Take every skin part's label out of <paramref name="locks"/> — what turning the checkbox off does, as skin locks
    /// mean nothing to tools that never move skin and would linger unseen.
    /// </summary>
    /// <returns>How many were taken out.</returns>
    public static int DropSkinLocks(HashSet<string> locks, IEnumerable<ModelPart> parts)
    {
        int dropped = 0;
        foreach (var part in parts)
            if (SecondSkinWriter.IsBodySkinMaterial(part.Material) && locks.Remove(part.Label)) dropped++;
        return dropped;
    }

    /// <summary>
    /// The model is a body rather than a garment: it has skin, and nothing else but what a body model carries with its
    /// skin — its nails, piercings and the like (body materials, <c>mt_c0201b0001_…</c>) and its own smallclothes
    /// (set e0000). Garments made for a body are recognised by its exact shape, which is why sculpting one is warned.
    /// </summary>
    public static bool IsBodyModel(ModelParts model)
    {
        var whole = model.Parts.Where(p => p.Island < 0).ToList();
        if (!whole.Any(p => SecondSkinWriter.IsBodySkinMaterial(p.Material))) return false;
        return whole.All(p => SecondSkinWriter.IsBodySkinMaterial(p.Material)
                              || BodyMaterial().IsMatch(p.Material)
                              || BodySurface.IsSmallclothes(p.Material));
    }

    [GeneratedRegex(@"mt_c\d{4}b\d{4}_", RegexOptions.IgnoreCase)]
    private static partial Regex BodyMaterial();
}
