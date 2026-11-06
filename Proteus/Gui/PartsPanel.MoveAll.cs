using System.Numerics;
using Proteus.Localization;

namespace Proteus.Gui;

/// <summary>
/// The "All" mode of Move, Rotate and Scale in the Studio: every ticked part of the open model moves as one piece.
/// The rules and the kept selection are <see cref="MoveAllSelection"/>; this is the panel's side of them.
/// </summary>
public sealed partial class PartsPanel
{
    /// <summary>
    /// Every ticked part moves together. Only under a part tool: the mode is kept while a brush or Body size is in use,
    /// whose lock sets the selection is not built from.
    /// </summary>
    private bool moveAll => PartTool && moveSelect == MoveSelectMode.All;

    private readonly MoveAllSelection allSelection = new();

    /// <summary>The live tint's cache key under All — salted apart from <see cref="MoveVersion"/>'s and the polygons'.</summary>
    private const int AllTintSalt = 0x2C1B3C6D;

    /// <summary>
    /// The All selection for the open model as it stands, built again only when the model, its solve, the locks or
    /// "Move skin too" changed — and the model view recoloured when it was. Call only under <see cref="moveAll"/>.
    /// </summary>
    private MoveAllSelection AllNow()
    {
        if (parts != null
            && allSelection.Update(parts, volume, partOfVertex,
                                   p => MoveAllSelection.Moves(IsSkin(p), IsLocked(p), SkinMovesNow),
                                   volume?.LockVersion ?? -1, SkinMovesNow))
            viewport.Recolour();
        return allSelection;
    }

    /// <summary>Mirror left/right as it takes effect: under a part tool, never under All; the brushes, as ticked.</summary>
    private bool MirrorNow => PartTool ? MoveAllSelection.MirrorApplies(moveSelect, mirrorBrush) : mirrorBrush;

    /// <summary>"Move adjacent parts" as it takes effect: never under All.</summary>
    private bool AdjacentNow => MoveAllSelection.AdjacentApplies(moveSelect, moveAdjacent);

    /// <summary>The All handle's place, and what it was worked out for: the selection's version and the cloth's.</summary>
    private Vector3? allPivot;
    private (int Selection, int Edit, object? Solve)? allPivotKey;

    /// <summary>The middle of the bounds of every vertex an All drag carries, as the cloth now stands; null with none.
    /// Walked again only when the selection or the cloth changed.</summary>
    private Vector3? AllPivot()
    {
        if (volume == null) return null;
        var sel = AllNow();
        var key = (sel.Version, volume.EditVersion, (object?)volume);
        if (allPivotKey != key)
        {
            allPivotKey = key;
            allPivot = sel.Seeds.Length == 0 ? null : MoveAllSelection.BoundsCentre(sel.Seeds, volume.Positions());
        }
        return allPivot;
    }

    /// <summary>The help line above the mode switch.</summary>
    internal static string HelpText(MoveHelpText which)
    {
        var ps = Strings.Parts;
        return which switch
        {
            MoveHelpText.MoveHelp          => ps.MoveHelp,
            MoveHelpText.MoveLiveHint      => ps.MoveLiveHint,
            MoveHelpText.RotateHelp        => ps.RotateHelp,
            MoveHelpText.RotateLiveHint    => ps.RotateLiveHint,
            MoveHelpText.ScaleHelp         => ps.ScaleHelp,
            MoveHelpText.ScaleLiveHint     => ps.ScaleLiveHint,
            MoveHelpText.MoveAllHelp       => ps.MoveAllHelp,
            MoveHelpText.MoveAllLiveHint   => ps.MoveAllLiveHint,
            MoveHelpText.RotateAllHelp     => ps.RotateAllHelp,
            MoveHelpText.RotateAllLiveHint => ps.RotateAllLiveHint,
            MoveHelpText.ScaleAllHelp      => ps.ScaleAllHelp,
            _                              => ps.ScaleAllLiveHint,
        };
    }
}
