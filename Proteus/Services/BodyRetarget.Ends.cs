using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// A vertex is the hand's or the foot's when at least this share of its weight is on hand or foot bones (50%).
    /// Read off the author's weights, not the geometry: a heeled foot is turned onto the toe and stands centimetres off
    /// any body's flat foot, its raised heel beside the calf, but it still follows the foot's bones.
    /// </summary>
    internal const float EndShare = 0.5f;

    /// <summary>The feet: <c>j_asi_d</c> (the foot), <c>j_asi_e</c> (the toes), and Rue's toes (<c>iv_asi_*</c>).</summary>
    internal static bool IsFootBone(string bone)
        => bone.StartsWith("j_asi_d_", StringComparison.Ordinal) || bone.StartsWith("j_asi_e_", StringComparison.Ordinal)
        || bone.StartsWith("iv_asi_", StringComparison.Ordinal);

    private static readonly string[] HandBonePrefixes =
    [
        "j_te_", "j_oya_", "j_hito_", "j_naka_", "j_kusu_", "j_ko_",
        "iv_te_", "iv_oya_", "iv_hito_", "iv_naka_", "iv_kusu_", "iv_ko_",
    ];

    /// <summary>The hands: the wrist (<c>j_te</c>), each finger's chain, and the IVCS fingers (<c>iv_</c>).</summary>
    internal static bool IsHandBone(string bone)
        => HandBonePrefixes.Any(p => bone.StartsWith(p, StringComparison.Ordinal));

    /// <summary>Which bones are the end a garment worn in <paramref name="slot"/> keeps: the feet for shoes, the hands for
    /// gloves. Null for any other slot.</summary>
    internal static Func<string, bool>? EndBonesOf(string slot) => slot switch
    {
        "_sho" => IsFootBone,
        "_glv" => IsHandBone,
        _ => null,
    };

    /// <summary>The share of vertex <paramref name="v"/>'s weight on <paramref name="isEnd"/> bones; 0 for a vertex with
    /// no weights or none read.</summary>
    internal static float EndShareOf(XivLiveMesh.SkinnedMesh skin, int v, Func<string, bool> isEnd)
    {
        if (v < 0 || (v + 1) * XivLiveMesh.SkinnedMesh.MaxInfluences > skin.BoneWeights.Length) return 0f;
        float all = 0f, end = 0f;
        foreach (var (bone, w) in Influences(skin, v))
        {
            all += w;
            if (isEnd(bone)) end += w;
        }
        return all > 1e-6f ? end / all : 0f;
    }

    /// <summary>
    /// The garment's vertices that are its hand or foot (see <see cref="EndShare"/>), which an automatic refit of shoes or
    /// gloves keeps exactly as their author made them.
    /// <para/>
    /// A hard piece (<paramref name="hardPieces"/>: a strap, a buckle, an anklet) is never split: <see cref="KeepShape"/>
    /// moves only the nodes that are free, so a piece held in part bends where the hold stops. It goes with the end when
    /// most of it is the end's, and it goes with the end as well when it touches the end at all — welded to a held foot,
    /// a piece left free is held there through the weld and bent the same way.
    /// </summary>
    /// <param name="slot">"_sho" or "_glv"; any other slot has no end and gives an empty set.</param>
    /// <param name="band">How many vertices have between 40% and 60% of their weight on the end: the ones the rule
    /// could have put either side, for the log.</param>
    internal static HashSet<int> EndVertices(ModelParts garment, XivLiveMesh.SkinnedMesh skin, string slot,
                                             IReadOnlyList<IReadOnlyCollection<int>> hardPieces, out int band)
    {
        var ends = new HashSet<int>();
        band = 0;
        if (EndBonesOf(slot) is not { } isEnd) return ends;

        int vc = garment.Positions.Length / 3;
        for (int v = 0; v < vc; v++)
        {
            float share = EndShareOf(skin, v, isEnd);
            if (share >= 0.4f && share <= 0.6f) band++;
            if (share >= EndShare) ends.Add(v);
        }

        // Where the end lies outside every hard piece — the foot's skin and the shoe around it — so a piece welded to it
        // is found by position, as the solver welds.
        var inSomePiece = new HashSet<int>(hardPieces.SelectMany(p => p));
        var endAt = new HashSet<(int, int, int)>();
        foreach (int v in ends)
            if (!inSomePiece.Contains(v)) endAt.Add(MeshMath.PositionKey(ToVec(At(garment, v)), SnapPerMetre));

        foreach (var piece in hardPieces)
        {
            var inPiece = piece.Where(v => v >= 0 && v < vc).ToList();
            if (inPiece.Count == 0) continue;
            int onEnd = inPiece.Count(ends.Contains);
            bool touches = inPiece.Any(v => endAt.Contains(MeshMath.PositionKey(ToVec(At(garment, v)), SnapPerMetre)));
            if (onEnd * 2 > inPiece.Count || touches)
                foreach (int v in inPiece) ends.Add(v);
            else
                foreach (int v in inPiece) ends.Remove(v);
        }
        return ends;
    }
}
