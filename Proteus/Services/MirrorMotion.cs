using System;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// The arithmetic of a mirrored Move, Rotate or Scale: a drag reflected through the body's centre plane X = 0, and the
/// symmetric part of a drag for a piece that is its own mirror. Pure; <see cref="MeshVolumeSolve"/> applies it per node.
/// <para/>
/// Matrices follow System.Numerics' row-vector convention, as <see cref="LinearNormals"/> does: a point maps as
/// <c>Vector3.Transform(p, m)</c>. <c>S = diag(−1, 1, 1)</c> is its own inverse, so the reflected transform is
/// <c>S·T·S</c> in either convention.
/// </summary>
internal static class MirrorMotion
{
    private static readonly Matrix4x4 S = Matrix4x4.CreateScale(-1f, 1f, 1f);

    /// <summary>
    /// Below this an entry of a centred step's matrix is taken as exactly 0. A turn built about a pivot leaves float
    /// dust of about 1e-8 where it should leave nothing, and a sideways turn on a centred piece must move nothing bit
    /// for bit, or ending it would count as an edit — an undo entry, a save, a redraw of the game.
    /// </summary>
    private const float Snap = 1e-6f;

    /// <summary><paramref name="v"/> across the centre plane.</summary>
    internal static Vector3 Reflect(Vector3 v) => new(-v.X, v.Y, v.Z);

    /// <summary>
    /// <paramref name="t"/> as seen in the mirror, <c>S·t·S</c>: what the other side does when this side does
    /// <paramref name="t"/>. Its pivot is the mirrored pivot, and a turn turns the other way.
    /// </summary>
    internal static Matrix4x4 Reflect(Matrix4x4 t) => S * t * S;

    /// <summary>A move of a piece that is its own mirror: the offset with its sideways part left out.</summary>
    internal static Vector3 CentredOffset(Vector3 offset) => new(0f, offset.Y, offset.Z);

    /// <summary>
    /// The symmetric part of a turn or a stretch, as <c>L_s − I</c> on its 3×3 linear part, translation dropped: a
    /// node's step is then <see cref="CentredStep"/>. Entries below <see cref="Snap"/> are exactly 0.
    /// <list type="bullet">
    /// <item>A turn keeps only its turn about X — the one turn that is its own mirror. Averaging a Y turn with its
    /// mirror would give <c>diag(cos θ, 1, cos θ)</c>, a part that shrinks as it turns.</item>
    /// <item>A stretch keeps <c>(L + S·L·S)/2</c>: the gizmo's axis scales unchanged, any shear across the centre
    /// dropped.</item>
    /// <item>A translation has no linear part: all zeros.</item>
    /// </list>
    /// </summary>
    internal static Matrix4x4 CentredLinear(Matrix4x4 t, MeshVolumeSolve.MoveKind kind)
    {
        if (kind == MeshVolumeSolve.MoveKind.Translate) return default;

        var l = LinearNormals.Linear(t);
        Matrix4x4 symmetric;
        if (kind == MeshVolumeSolve.MoveKind.Rotate)
        {
            // The rotation about X nearest l: CreateRotationX(φ) has M23 = sin φ, M32 = −sin φ, M22 = M33 = cos φ.
            float phi = MathF.Atan2(l.M23 - l.M32, l.M22 + l.M33);
            symmetric = Matrix4x4.CreateRotationX(phi);
        }
        else
        {
            var m = S * l * S;
            symmetric = new Matrix4x4(
                (l.M11 + m.M11) * 0.5f, (l.M12 + m.M12) * 0.5f, (l.M13 + m.M13) * 0.5f, 0f,
                (l.M21 + m.M21) * 0.5f, (l.M22 + m.M22) * 0.5f, (l.M23 + m.M23) * 0.5f, 0f,
                (l.M31 + m.M31) * 0.5f, (l.M32 + m.M32) * 0.5f, (l.M33 + m.M33) * 0.5f, 0f,
                0f, 0f, 0f, 1f);
        }

        static float Clean(float v) => MathF.Abs(v) < Snap ? 0f : v;
        return new Matrix4x4(
            Clean(symmetric.M11 - 1f), Clean(symmetric.M12), Clean(symmetric.M13), 0f,
            Clean(symmetric.M21), Clean(symmetric.M22 - 1f), Clean(symmetric.M23), 0f,
            Clean(symmetric.M31), Clean(symmetric.M32), Clean(symmetric.M33 - 1f), 0f,
            0f, 0f, 0f, 0f);
    }

    /// <summary>
    /// How far a centred turn or stretch moves the point <paramref name="p"/>: <c>(p − c)·(L_s − I)</c>, about
    /// <c>c</c>, the gizmo's pivot brought onto the centre plane. Never routed through a translation, so a
    /// <paramref name="linearMinusI"/> of all zeros moves nothing, exactly.
    /// </summary>
    internal static Vector3 CentredStep(Vector3 p, Matrix4x4 linearMinusI, Vector3 pivot)
        => Vector3.TransformNormal(new Vector3(p.X, p.Y - pivot.Y, p.Z - pivot.Z), linearMinusI);

    /// <summary>
    /// How much of the gizmo's own motion a node at <paramref name="x"/> takes, 0..1 — the rest is the mirror's.
    /// 1 on the dragged side (<paramref name="sign"/>) beyond <paramref name="band"/>, 0 beyond it on the other, ½ on
    /// the centre plane, and linear between, so <c>Side(−x) = 1 − Side(x)</c> and the result stays symmetric.
    /// </summary>
    internal static float Side(float x, float sign, float band)
    {
        float s = sign * x;
        if (band <= 0f) return s > 0f ? 1f : s < 0f ? 0f : 0.5f;
        return Math.Clamp(0.5f + s / (2f * band), 0f, 1f);
    }
}
