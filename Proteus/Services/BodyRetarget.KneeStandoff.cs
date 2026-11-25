using System;
using System.Collections.Generic;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// The knee standoff: coarse cloth on the back of the knee and calf held a few millimetres further off the new legs, so
/// it still covers the skin when the knee bends.
/// <para/>
/// A coarse stocking face (40-80 mm across) can only move linearly between its corners; the dense skin under it bends by
/// its own blend of the thigh, knee and calf bones. Behind the knee the two drift apart as the leg flexes and the skin
/// comes out through the face. Measured on Starlight's Valentione Skirt, every refit size (2026-11-25, build 1502, the
/// owner's live idle pose dumped through the harness, and knee bends added beyond bind): at rest nothing is through; in
/// idle 57-118 skin points per size come through, all but a few on the back of the leg, p90 2.7-4.3 mm deep; with 30°
/// added, 52-96, p90 at most 2.4 mm.
/// <para/>
/// A fixed standoff, not a pose run inside the refit: the refit has no skeleton, and the pose measure lives in the tests
/// that tuned these constants. Run as a second, opt-in pass of <see cref="ClearFaces"/>, so every guard of the face
/// settle (fold put-back, layer guard, wall partners, sink check) applies unchanged.
/// </summary>
internal static partial class BodyRetarget
{
    /// <summary>The bones of the knee band: the knee (<c>j_asi_b</c>) and the calf (<c>j_asi_c</c>). Measured: faces
    /// reading either cover 96.9% of the idle and bent poke-through points over the 12 sizes; the knee alone 75%; adding
    /// <c>n_hizasoubi</c> changes nothing.</summary>
    internal static bool IsKneeBone(string bone)
        => bone.StartsWith("j_asi_b", StringComparison.Ordinal) || bone.StartsWith("j_asi_c", StringComparison.Ordinal);

    /// <summary>The least share of the new body's weight on the knee band for a point to lie in it (5%): the threshold
    /// at which faces cover at least 95% of the measured poke-through points (96.9%; 77.5% at 10%).</summary>
    internal const float KneeWeight = 0.05f;

    /// <summary>How far behind a knee face the skin is held (4 mm). The deepest p90 the measure found in idle and with 30°
    /// added (4.3 mm, Rosaline in idle) plus 1 mm gave 5.5 mm to start from; at 5.5 mm the lift stepped down at the band's
    /// edge steeper than the stockings may move out of line (Neolithe S/M/L, Tre M/L, over the 1501 slope slack), and
    /// at 4 mm every size keeps that bound while the owner's idle loses 74-91% of its poke-through, every bent bar met
    /// (measured 2026-11-25).</summary>
    internal const float KneeStandoff = 0.004f;

    /// <summary>The most the knee standoff may ever be (6 mm): under what the face test reads (it sees skin no deeper than
    /// <see cref="ClearDepth"/>, 8 mm, through a face), so a larger standoff could never be seen as met.</summary>
    internal const float KneeStandoffMost = 0.006f;

    /// <summary>The most the knee pass moves any one corner in all (the standoff plus 1 mm).</summary>
    internal const float KneeBudget = KneeStandoff + 0.001f;

    /// <summary>A knee face looks back: its way out (the skin under it, as a whole) points behind the character (z below
    /// this). Measured: every poke-through point in idle and with 15-30° added is on the back of the leg.</summary>
    internal const float KneeFacing = 0f;

    /// <summary>The faces a second <see cref="ClearFaces"/> pass works on, and what it asks of them.</summary>
    internal sealed record FaceGoal(IReadOnlySet<int> Faces, float Standoff, float Budget);

    /// <summary>Whether a point lies over the knee band of any body in play; null when none carries those bones.</summary>
    internal static Func<Vector3, bool>? KneeTest(IReadOnlyList<SlotPair> pairs) => RegionTest(pairs, IsKneeBone, KneeWeight);

    /// <summary>
    /// The coarse cloth faces over the back of the knee band: coarse as the face settle reads it (its
    /// <see cref="ClearFaceCap(float)"/> above the <see cref="ClearFaceDeepest"/> floor), any of its corners, edge
    /// midpoints or centroid over the band, and looking back. Null when there are none.
    /// </summary>
    /// <param name="kneeTest">Builds the knee band's test (<see cref="KneeTest"/>), asked only once a coarse face looking
    /// back is found: reading every body's weights is not free, and most garments have no such face.</param>
    internal static FaceGoal? KneeFaces(FaceCheck check, Vec3[] nodeDelta, Func<Func<Vector3, bool>?> kneeTest)
    {
        // Coarse and looking back first: the cheap tests.
        var back = new List<int>();
        for (int f = 0; f < check.Faces.Count; f++)
        {
            if (!(ClearFaceCap(check.LongestEdge(check.Faces[f], nodeDelta)) > ClearFaceDeepest)) continue;
            var (_, outward) = check.Signed(f, nodeDelta);
            if (outward == default || !(outward.Z < KneeFacing)) continue;
            back.Add(f);
        }
        if (back.Count == 0 || kneeTest() is not { } isKnee) return null;
        var faces = new HashSet<int>();
        foreach (int f in back)
        {
            var face = check.Faces[f];
            Vector3 a = check.At(face.A, nodeDelta), b = check.At(face.B, nodeDelta), c = check.At(face.C, nodeDelta);
            if (isKnee(a) || isKnee(b) || isKnee(c) || isKnee((a + b) * 0.5f) || isKnee((b + c) * 0.5f)
                || isKnee((c + a) * 0.5f) || isKnee((a + b + c) / 3f)) faces.Add(f);
        }
        return faces.Count == 0 ? null : new FaceGoal(faces, KneeStandoff, KneeBudget);
    }

    /// <summary>What the knee pass did: the distinct cloth nodes it moved, and the largest move of any one.</summary>
    internal readonly record struct KneeTally(int Moved, float Worst);
}
