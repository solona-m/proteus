using System;
using System.Collections.Generic;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// The turn that best carries one set of points onto another, for a piece moved whole: whether an answer is a real
/// rotation (<see cref="IsTurn"/>), and one that always is (<see cref="HornTurn"/>).
/// </summary>
internal static class TurnFit
{
    /// <summary>
    /// How far a turn may be from a true rotation and still be taken as one (1e-4): the most any entry of RᵀR may differ
    /// from the identity's.
    /// <para/>
    /// <see cref="SecondSkinWriter.BestRotation"/> takes the polar factor of the points' cross-covariance by Newton
    /// iteration, each step mapping a singular value σ to (σ + 1/σ)/2. A flat piece — a double-sided card, its 8 vertices
    /// welded to 4 points in one plane — has a cross-covariance of rank two, and float noise leaves its third singular value
    /// near 1e-11 rather than zero: the first step throws it to 1e10, which 24 steps cannot halve back to 1, and the answer
    /// is a matrix with entries in the millions that still passes a determinant check. Virtu Dancer's Bottoms refitted onto
    /// YAS Tre carried 15 such cards: the scale fitted to the broken turn (as much as 177) was kept for the second pass,
    /// where it met a true rotation and put one card 3.1 m off the body.
    /// <para/>
    /// Measured (build 1365, every keep-shape piece of 464 garments — every 6th game top and legs model and every 2nd
    /// installed mod's — refitted from the game body onto Tre: 16,881 pieces): 16,230 come out within 1e-6 of a rotation,
    /// 107 more within 1e-4, and the rest anywhere from 1e-4 to 1e11 with no gap between them — chain links, studs and
    /// beads as well as cards. Within 1e-4 a 66 mm piece is sheared by under 7 µm, so those keep their answer exactly; past
    /// it they are turned by <see cref="HornTurn"/> instead.
    /// <para/>
    /// A true rotation keeps the scale fitted with it the piece's own, so it is safe to carry to the second pass. Measured
    /// (build 1365, 155 of those garments refitted as the automatic refit does, push-out on): the 293 turns made by Horn's
    /// fit took scales of 0.83 to 2.20, median 1.00, in both passes; the 6,711 polar ones 0.40 to 1.97.
    /// </summary>
    internal const float TurnTolerance = 1e-4f;

    /// <summary>Whether <paramref name="r"/> (row-major 3×3) is a proper rotation: every entry finite, RᵀR within
    /// <see cref="TurnTolerance"/> of the identity, and no reflection. A NaN anywhere fails.</summary>
    internal static bool IsTurn(float[] r)
    {
        if (r.Length != 9) return false;
        foreach (float x in r)
            if (!float.IsFinite(x)) return false;
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
        {
            float dot = r[i] * r[j] + r[3 + i] * r[3 + j] + r[6 + i] * r[6 + j];
            if (!(MathF.Abs(dot - (i == j ? 1f : 0f)) <= TurnTolerance)) return false;
        }
        float det = r[0] * (r[4] * r[8] - r[5] * r[7]) - r[1] * (r[3] * r[8] - r[5] * r[6])
                  + r[2] * (r[3] * r[7] - r[4] * r[6]);
        return det > 0.5f;
    }

    private static readonly float[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    /// <summary>
    /// The rotation that best carries <paramref name="from"/> about <paramref name="cFrom"/> onto <paramref name="to"/>
    /// about <paramref name="cTo"/>, by Horn's quaternion method: the eigenvector of the largest eigenvalue of a symmetric
    /// 4×4 built from the cross-covariance. Unlike <see cref="SecondSkinWriter.BestRotation"/> it is always a proper
    /// rotation and needs no inverse, so a flat piece (rank two) is turned exactly; a line of points is turned onto its
    /// target's line, the twist about it being anyone's. In double throughout. Row-major, as BestRotation's: the turned
    /// point is (r[0]x + r[1]y + r[2]z, …). The identity when the points carry no turn at all or are not finite.
    /// </summary>
    internal static float[] HornTurn(IReadOnlyList<Vec3> from, IReadOnlyList<Vec3> to, Vector3 cFrom, Vector3 cTo)
    {
        double sxx = 0, sxy = 0, sxz = 0, syx = 0, syy = 0, syz = 0, szx = 0, szy = 0, szz = 0;
        for (int i = 0; i < from.Count && i < to.Count; i++)
        {
            double ax = from[i].X - cFrom.X, ay = from[i].Y - cFrom.Y, az = from[i].Z - cFrom.Z;
            double bx = to[i].X - cTo.X, by = to[i].Y - cTo.Y, bz = to[i].Z - cTo.Z;
            sxx += ax * bx; sxy += ax * by; sxz += ax * bz;
            syx += ay * bx; syy += ay * by; syz += ay * bz;
            szx += az * bx; szy += az * by; szz += az * bz;
        }
        var n = new double[4, 4]
        {
            { sxx + syy + szz, syz - szy, szx - sxz, sxy - syx },
            { syz - szy, sxx - syy - szz, sxy + syx, szx + sxz },
            { szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy },
            { sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz },
        };
        double norm = 0;
        foreach (double x in n) norm += x * x;
        if (!double.IsFinite(norm) || norm == 0) return (float[])Identity.Clone();

        var q = LargestEigenvector(n, norm);
        if (q == null) return (float[])Identity.Clone();
        double w = q[0], x1 = q[1], y = q[2], z = q[3];
        return
        [
            (float)(w * w + x1 * x1 - y * y - z * z), (float)(2 * (x1 * y - w * z)), (float)(2 * (x1 * z + w * y)),
            (float)(2 * (x1 * y + w * z)), (float)(w * w - x1 * x1 + y * y - z * z), (float)(2 * (y * z - w * x1)),
            (float)(2 * (x1 * z - w * y)), (float)(2 * (y * z + w * x1)), (float)(w * w - x1 * x1 - y * y + z * z),
        ];
    }

    /// <summary>The unit eigenvector of the largest eigenvalue of the symmetric 4×4 <paramref name="m"/>, by cyclic Jacobi
    /// (at most 50 sweeps, stopping once what is off the diagonal is 1e-24 of <paramref name="norm"/>, the matrix's squared
    /// size); null when it comes out unusable.</summary>
    private static double[]? LargestEigenvector(double[,] m, double norm)
    {
        var a = (double[,])m.Clone();
        var v = new double[4, 4];
        for (int i = 0; i < 4; i++) v[i, i] = 1;
        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = 0;
            for (int p = 0; p < 4; p++)
            for (int r = p + 1; r < 4; r++) off += a[p, r] * a[p, r];
            if (off <= 1e-24 * norm) break;
            for (int p = 0; p < 4; p++)
            for (int r = p + 1; r < 4; r++)
            {
                if (a[p, r] == 0) continue;
                double theta = (a[r, r] - a[p, p]) / (2 * a[p, r]);
                double t = theta == 0 ? 1 : Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                for (int k = 0; k < 4; k++)
                {
                    double akp = a[k, p], akr = a[k, r];
                    a[k, p] = c * akp - s * akr;
                    a[k, r] = s * akp + c * akr;
                }
                for (int k = 0; k < 4; k++)
                {
                    double apk = a[p, k], ark = a[r, k];
                    a[p, k] = c * apk - s * ark;
                    a[r, k] = s * apk + c * ark;
                }
                for (int k = 0; k < 4; k++)
                {
                    double vkp = v[k, p], vkr = v[k, r];
                    v[k, p] = c * vkp - s * vkr;
                    v[k, r] = s * vkp + c * vkr;
                }
            }
        }
        int best = 0;
        for (int i = 1; i < 4; i++)
            if (a[i, i] > a[best, best]) best = i;
        double len = Math.Sqrt(v[0, best] * v[0, best] + v[1, best] * v[1, best] + v[2, best] * v[2, best]
                               + v[3, best] * v[3, best]);
        if (!double.IsFinite(len) || len == 0) return null;
        return [v[0, best] / len, v[1, best] / len, v[2, best] / len, v[3, best] / len];
    }
}
