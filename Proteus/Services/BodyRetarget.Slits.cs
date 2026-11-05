using System.Collections.Generic;
using System.Numerics;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// How far from the garment's laid skin a body corner may lie and still be drawn into a slit (10 mm): see
    /// <see cref="GrowSeenSlits"/>. Equinox's bikini top on Rosaline's Coconut left one ring of body triangles open just
    /// past the author's edge — its edge corners 4.2-6.0 mm from the laid skin, past <see cref="CutReach"/>, and its outer
    /// corners 6.5-8.2 mm off; the ring beyond lies under the cup.
    /// </summary>
    internal const float SlitBand = 0.010f;

    /// <summary>How near this garment's cloth a slit corner must lie (15 mm): the gap between the author's edge and the
    /// garment's own cloth, never skin an author cut in the open for another piece.</summary>
    internal const float SlitClothNear = 0.015f;

    /// <summary>
    /// The ring of the new body's skin left out in the open just past the author's edge, drawn after all — on the size
    /// set's tops (see <see cref="Plan"/>'s slitSlot).
    /// <para/>
    /// The cut keeps a body vertex only where the garment's laid skin comes within <see cref="CutReach"/> (see
    /// <see cref="CutLike"/>). Refitted onto a much fuller bust, the cloth carries the author's edge further than the skin
    /// beside it: Equinox's bikini top, cut from its Rue Large size onto Rosaline's Coconut, left one ring of body
    /// triangles open just past the author's edge at the inner top of each cup — their edge corners 4.2-6.0 mm from the
    /// laid skin, so no corner was drawn — and a camera looking down past the cup's rim saw into the cup or straight
    /// through the body (10.5 cm² from views all round: 2.9 into the garment, 7.7 into the void).
    /// <para/>
    /// A body vertex goes in when every corner of each triangle it would newly draw lies within <see cref="SlitBand"/> of
    /// the laid skin, is neither crowded (<see cref="TuckClear"/>, <see cref="TuckBehind"/>) nor under cloth within
    /// <see cref="TuckOver"/> along its normal or level — judged against the cloth with its IMC variants counted, any of
    /// which may be drawn — lies within <see cref="SlitClothNear"/> of the garment's own always-drawn cloth, was not moved
    /// by the pull, and is torso (under <see cref="TuckArmShare"/> on the arm); and no cloth crowds the triangle's middle
    /// or the middles of its edges (see <see cref="GrowOpenSkin"/>). The ring beyond, under the cup, is crowded, so the
    /// growth stops at the ring. Nothing moves.
    /// <para/>
    /// Measured on that Coconut top: 41.8 cm² of torso skin undrawn before, 37.8 after; uncovered by cloth 10.2 → 6.2;
    /// seen into from views all round 10.5 → 6.5; skin through the cloth 37.41 cm² both ways. What stays open lies along
    /// the cup's rim, in body triangles that pass under it: drawn, their skin would stand through the rim. A test of
    /// two clear neighbouring views as well changed nothing there (byte for byte), so there is none: open within
    /// <see cref="TuckOver"/> already means in sight.
    /// <para/>
    /// Only the size set: its sizes are new options beside the author's, never the author's own models, so skin left out
    /// in the open is drawn there. The Studio's refit is written into the modder's own model, where the cut is theirs.
    /// </summary>
    /// <param name="cloth">The garment's always-drawn cloth: what a slit lies beside.</param>
    /// <param name="clothAll">The garment's cloth with its IMC variants counted: what may cover or crowd the skin.</param>
    /// <param name="body">The new body's file, for its weights.</param>
    /// <returns>Per mesh, the vertices to draw as well (mesh-local), how many triangles they newly draw, and whether the
    /// body's weights could not be read — then nothing is drawn.</returns>
    private static (Dictionary<int, HashSet<ushort>> Grown, int Triangles, bool Unread) GrowSeenSlits(
        BodySurface drawn, ClothCrossings cloth, ClothCrossings clothAll, byte[] body, ModelParts parts, bool[] covered,
        IReadOnlyCollection<(int A, int B, int C)> keptTris, IReadOnlySet<Vector3> movedAt,
        IReadOnlyDictionary<int, int> baseOf)
    {
        int vc = parts.Positions.Length / 3;
        // Unread weights cannot tell the torso from the arm: nothing is drawn, the safe way round.
        if (ModelSkinReader.Read(body, null, null) is not { } skin || skin.VertexCount != vc) return ([], 0, true);

        var (grown, triangles) = GrowOpenSkin(clothAll, parts, covered, keptTris, baseOf, (v, p, nrm) =>
        {
            var level = new Vector3(nrm.X, 0f, nrm.Z);
            return drawn.Nearest(p, SlitBand, out _)
                   && !movedAt.Contains(p)
                   && !clothAll.Crowds(p, nrm, TuckClear, TuckBehind)
                   && !clothAll.Over(p, nrm, TuckOver)
                   && !(level.LengthSquared() > 1e-6f && clothAll.Over(p, Vector3.Normalize(level), TuckOver))
                   && cloth.Near(p, SlitClothNear)
                   && EndShareOf(skin, v, IsArmBone) < TuckArmShare;
        });
        return (grown, triangles, false);
    }
}
