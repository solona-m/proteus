using System.Collections.Generic;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// Where each vertex of the SOURCE body lands on the TARGET body.
/// <para/>
/// This is the one thing that differs between the two jobs the retarget has to do, and the reason it is an interface.
/// A Neolithe size change is between two models of identical topology, so the answer is the other model's vertex of
/// the same index and costs nothing. A Neolithe to Rue+ refit is between different topologies, so the answer has to be
/// solved — through the UV atlas, since both bodies name <c>mt_c0201b0001_bibo.mtrl</c> and therefore share a uv
/// space. Everything the retarget does WITH the answer — carrying it onto the garment, the falloff, the exact-match
/// snap, the push-out, the normals, the write — is the same either way and is written once.
/// <para/>
/// Weight transfer, which Rue also needs because its bone set genuinely differs from Neolithe's, is NOT part of this
/// seam: it changes the write path rather than the displacement, and the length-neutral rewrite this feature relies on
/// is what has to give for it.
/// </summary>
internal interface IBodyCorrespondence
{
    /// <summary>The surface the field lives on: the source body.</summary>
    ModelParts Source { get; }

    /// <summary>
    /// Per source-body vertex, indexed like <see cref="ModelParts.Positions"/>: how far that point of the body moved.
    /// Null where the source vertex has no landing on the target at all, which cannot happen for an identical
    /// topology but will for a cross-body refit with a hole in the atlas.
    /// </summary>
    IReadOnlyList<Vector3?> Field { get; }

    /// <summary>One line for the status bar and the saved record.</summary>
    string Describe();

    /// <summary>
    /// The target is the source's own mesh, vertex for vertex — a size of the same body — so a point that sat exactly on
    /// a source vertex lands exactly on the target's, and a face between such points on the target's own face. False
    /// unless proven: a body tessellated otherwise bulges between those points (see
    /// <see cref="BodyRetarget.SnappedLandsElsewhere"/>).
    /// </summary>
    bool SameMesh => false;

    /// <summary>
    /// The target draws something other than skin over part of the source's skin — underwear modelled onto the body
    /// (Muse+'s SFW legs: boxers, with the skin cut away under them) — and the field carries that part onto it. The rest
    /// of the refit then reads the target as it is worn, its smallclothes with its skin (see
    /// <see cref="BodySurface.OfBodyWorn"/>), and the swap brings those smallclothes in. False for every other pair.
    /// </summary>
    bool TargetWorn => false;
}
