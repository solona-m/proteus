using System;
using System.Collections.Generic;
using System.Numerics;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>What Move, Rotate and Scale take hold of: one whole part, single polygons, or every ticked part at once.</summary>
internal enum MoveSelectMode { Parts, Polygons, All }

/// <summary>The tool a help line above the mode switch is for.</summary>
internal enum MoveHelpTool { Move, Rotate, Scale }

/// <summary>Which help line shows above the mode switch.</summary>
internal enum MoveHelpText
{
    MoveHelp, MoveLiveHint, RotateHelp, RotateLiveHint, ScaleHelp, ScaleLiveHint,
    MoveAllHelp, MoveAllLiveHint, RotateAllHelp, RotateAllLiveHint, ScaleAllHelp, ScaleAllLiveHint,
}

/// <summary>
/// The "All" mode of Move, Rotate and Scale: every ticked part of the open model moves as one piece. The rules the
/// panel reads (what moves, what is switched off) and the selection itself — the vertices a drag carries, the labels
/// shown as chosen, the counts — kept until the model, its solve, the locks or "Move skin too" change, so a large
/// model is not walked every frame.
/// </summary>
internal sealed class MoveAllSelection
{
    /// <summary>Whether a part moves under All: ticked (not locked), and skin only with "Move skin too" on — the same
    /// rule as a part that can be chosen to move.</summary>
    public static bool Moves(bool isSkin, bool locked, bool skinMoves) => SkinMovePolicy.MovePickable(isSkin, locked, skinMoves);

    /// <summary>"Move adjacent parts" takes effect: never under All, which moves the ticked parts as one rigid piece.</summary>
    public static bool AdjacentApplies(MoveSelectMode mode, bool adjacent) => adjacent && mode != MoveSelectMode.All;

    /// <summary>Mirror left/right takes effect for a part tool: never under All — a mirror moves the sides apart.</summary>
    public static bool MirrorApplies(MoveSelectMode mode, bool mirror) => mirror && mode != MoveSelectMode.All;

    /// <summary>A click on the model or the character chooses a part: not under All, where the ticks decide.</summary>
    public static bool ClickChooses(MoveSelectMode mode) => mode != MoveSelectMode.All;

    /// <summary>The help line above the mode switch for <paramref name="tool"/>, in the model view or on the character.</summary>
    public static MoveHelpText HelpFor(MoveHelpTool tool, bool modelView, bool all) => (tool, modelView, all) switch
    {
        (MoveHelpTool.Rotate, true, false)  => MoveHelpText.RotateHelp,
        (MoveHelpTool.Rotate, false, false) => MoveHelpText.RotateLiveHint,
        (MoveHelpTool.Scale, true, false)   => MoveHelpText.ScaleHelp,
        (MoveHelpTool.Scale, false, false)  => MoveHelpText.ScaleLiveHint,
        (MoveHelpTool.Move, true, false)    => MoveHelpText.MoveHelp,
        (MoveHelpTool.Move, false, false)   => MoveHelpText.MoveLiveHint,
        (MoveHelpTool.Rotate, true, true)   => MoveHelpText.RotateAllHelp,
        (MoveHelpTool.Rotate, false, true)  => MoveHelpText.RotateAllLiveHint,
        (MoveHelpTool.Scale, true, true)    => MoveHelpText.ScaleAllHelp,
        (MoveHelpTool.Scale, false, true)   => MoveHelpText.ScaleAllLiveHint,
        (_, true, true)                     => MoveHelpText.MoveAllHelp,
        _                                   => MoveHelpText.MoveAllLiveHint,
    };

    /// <summary>
    /// Whether radio buttons with labels <paramref name="labelWidths"/> wide fit on one line of <paramref name="avail"/>:
    /// each a frame-high <paramref name="box"/>, its label after <paramref name="inner"/>, apart by <paramref name="spacing"/>.
    /// </summary>
    public static bool RadiosFit(float[] labelWidths, float box, float inner, float spacing, float avail)
    {
        float total = 0f;
        for (int i = 0; i < labelWidths.Length; i++)
            total += box + inner + labelWidths[i] + (i > 0 ? spacing : 0f);
        return total <= avail;
    }

    /// <summary>The middle of the bounds of <paramref name="vertices"/> in <paramref name="positions"/>; null with none.</summary>
    public static Vector3? BoundsCentre(IEnumerable<int> vertices, float[] positions)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (int v in vertices)
        {
            if (v < 0 || v * 3 + 2 >= positions.Length) continue;
            var at = new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }
        return min.X <= max.X ? (min + max) * 0.5f : null;
    }

    private ModelParts? model;
    private object? solve;
    private int[]? partOfVertex;
    private int lockVersion;
    private bool skinMoves;
    private readonly HashSet<string> labels = new(StringComparer.Ordinal);

    /// <summary>Every vertex whose finest part moves, ascending: what an All drag carries.</summary>
    public int[] Seeds { get; private set; } = [];

    /// <summary>Every part label — submesh or island — that moves.</summary>
    public IReadOnlySet<string> Labels => labels;

    /// <summary>Submesh rows (the ones the closed list counts) that move.</summary>
    public int MovingRows { get; private set; }

    /// <summary>Submesh rows.</summary>
    public int Rows { get; private set; }

    /// <summary>A moving part is body skin.</summary>
    public bool IncludesSkin { get; private set; }

    /// <summary>Changes on every rebuild and every <see cref="Touch"/>: the live tint's cache key.</summary>
    public int Version { get; private set; }

    /// <summary>The mode was switched into All: hand the live tint a key it has not cached.</summary>
    public void Touch() => Version++;

    /// <summary>
    /// Build the selection again if the model, its solve, the vertex map, the locks or "Move skin too" changed since
    /// the last build. <paramref name="moves"/> says whether a part moves (<see cref="Moves"/>).
    /// </summary>
    /// <returns>True when it was built again.</returns>
    public bool Update(ModelParts model, object? solve, int[] partOfVertex, Func<ModelPart, bool> moves,
                       int lockVersion, bool skinMoves)
    {
        if (ReferenceEquals(this.model, model) && ReferenceEquals(this.solve, solve)
            && ReferenceEquals(this.partOfVertex, partOfVertex) && this.lockVersion == lockVersion
            && this.skinMoves == skinMoves)
            return false;
        this.model = model;
        this.solve = solve;
        this.partOfVertex = partOfVertex;
        this.lockVersion = lockVersion;
        this.skinMoves = skinMoves;

        var parts = model.Parts;
        var movesPart = new bool[parts.Count];
        labels.Clear();
        int rows = 0, moving = 0;
        bool skin = false;
        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            movesPart[p] = moves(part);
            if (part.Island < 0) rows++;
            if (!movesPart[p]) continue;
            labels.Add(part.Label);
            if (part.Island < 0) moving++;
            if (SecondSkinWriter.IsBodySkinMaterial(part.Material)) skin = true;
        }

        var seeds = new List<int>();
        for (int v = 0; v < partOfVertex.Length; v++)
            if (partOfVertex[v] is var p and >= 0 && p < movesPart.Length && movesPart[p]) seeds.Add(v);

        Seeds = seeds.ToArray();
        Rows = rows;
        MovingRows = moving;
        IncludesSkin = skin;
        Version++;
        return true;
    }
}
