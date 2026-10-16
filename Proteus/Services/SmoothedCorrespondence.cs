using System;
using System.Collections.Generic;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// A correspondence onto a target body the refit has since smoothed (see <see cref="BodyRetarget.SmoothCoveredNipples"/>):
/// each source vertex lands where it did, then moves as the smoothed target moved there — the barycentric blend of the
/// target's movement over the triangle it landed on. The cloth the field carries then lands on the surface the swap draws,
/// not the one the smoothing took away.
/// <para/>
/// Shifted, not rebuilt: rebuilding would need the bodies' texture coordinates and remap again, and repeat an atlas
/// solve whose answer is unchanged everywhere the smoothing did not reach.
/// </summary>
internal sealed class SmoothedCorrespondence : IBodyCorrespondence
{
    private readonly IBodyCorrespondence inner;
    private readonly Vector3?[] shifted;

    /// <param name="unsmoothedTarget">The target as the landings were made on it.</param>
    /// <param name="targetDelta">How far the smoothing moved each of its vertices.</param>
    public SmoothedCorrespondence(IBodyCorrespondence inner, ModelParts unsmoothedTarget, Vector3[] targetDelta)
    {
        this.inner = inner;
        var source = inner.Source;
        var was = inner.Field;
        shifted = new Vector3?[was.Count];

        // Only landings near something that moved can change; the rest are copied.
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (int v = 0; v < targetDelta.Length && v * 3 + 2 < unsmoothedTarget.Positions.Length; v++)
        {
            if (targetDelta[v].LengthSquared() <= 0f) continue;
            var p = new Vector3(unsmoothedTarget.Positions[v * 3], unsmoothedTarget.Positions[v * 3 + 1],
                                unsmoothedTarget.Positions[v * 3 + 2]);
            lo = Vector3.Min(lo, p);
            hi = Vector3.Max(hi, p);
        }
        var reach = new Vector3(BodyRetarget.NippleShiftReach);
        lo -= reach;
        hi += reach;

        BodySurface? surface = null;
        for (int s = 0; s < was.Count; s++)
        {
            shifted[s] = was[s];
            if (was[s] is not { } d || s * 3 + 2 >= source.Positions.Length) continue;
            var landing = new Vector3(source.Positions[s * 3], source.Positions[s * 3 + 1], source.Positions[s * 3 + 2]) + d;
            if (landing.X < lo.X || landing.Y < lo.Y || landing.Z < lo.Z
                || landing.X > hi.X || landing.Y > hi.Y || landing.Z > hi.Z) continue;
            surface ??= new BodySurface(unsmoothedTarget, BodySurface.CellFor(BodyRetarget.MeanEdgeOf(unsmoothedTarget)));
            if (!surface.Nearest(landing, BodyRetarget.NippleShiftReach, out var hit)) continue;
            var shift = targetDelta[hit.A] * hit.U + targetDelta[hit.B] * hit.V + targetDelta[hit.C] * hit.W;
            if (shift.LengthSquared() > 0f) shifted[s] = d + shift;
        }
    }

    public ModelParts Source => inner.Source;

    /// <summary>The smoothing moves the target's vertices, never renumbers them: whatever the landings were made on.</summary>
    public bool SameMesh => inner.SameMesh;

    public IReadOnlyList<Vector3?> Field => shifted;

    public string Describe() => inner.Describe();
}
