using System;
using System.Numerics;
using Proteus.Gui;
using Proteus.Services;
using Xunit;
using Kind = Proteus.Services.MeshVolumeSolve.MoveKind;

namespace Proteus.Tests;

/// <summary>
/// The mirror's arithmetic for Move, Rotate and Scale: the reflected drag, and the symmetric part a piece that is its
/// own mirror keeps. Built with the gizmos' own matrix builders, so the conventions are the ones the tools use.
/// </summary>
public class MirrorMotionTests
{
    private static readonly Vector3 OffCentre = new(0.05f, 0.1f, 0.2f);
    private static readonly Vector3 OnCentre = new(0f, 0.1f, 0.2f);

    private static readonly Vector3[] Points =
    [
        new(0.03f, 0.12f, 0.25f), new(-0.07f, 0.05f, 0.18f), new(0.11f, 0.2f, 0.1f), new(0f, 0.09f, 0.3f),
    ];

    private static void Near(Vector3 expected, Vector3 actual, float tolerance, string what)
        => Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"{what}: expected {expected}, got {actual}");

    private static bool IsZero(Matrix4x4 m)
        => m.M11 == 0f && m.M12 == 0f && m.M13 == 0f && m.M21 == 0f && m.M22 == 0f && m.M23 == 0f
           && m.M31 == 0f && m.M32 == 0f && m.M33 == 0f;

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-1")]
    public void ReflectIsItsOwnInverseAndCommutesWithReflection()
    {
        var turn = RotateGizmo.About(OffCentre, Vector3.Normalize(new Vector3(0.3f, 1f, -0.4f)), 0.7f);
        var stretch = PartScaleDrag.Build(OffCentre, new Vector3(1.5f, 0.8f, 1.2f));
        foreach (var t in new[] { turn, stretch, Matrix4x4.CreateTranslation(0.01f, 0.02f, -0.03f) })
        {
            var back = MirrorMotion.Reflect(MirrorMotion.Reflect(t));
            foreach (var p in Points) Near(Vector3.Transform(p, t), Vector3.Transform(p, back), 1e-6f, "reflected twice");

            // What the other side does at the mirror of p is the mirror of what this side does at p.
            var mirrored = MirrorMotion.Reflect(t);
            foreach (var p in Points)
                Near(MirrorMotion.Reflect(Vector3.Transform(p, t)),
                     Vector3.Transform(MirrorMotion.Reflect(p), mirrored), 1e-6f, "reflection commutes");
        }

        // A turn about Y turns the other way on the other side.
        var yTurn = RotateGizmo.About(OffCentre, Vector3.UnitY, 0.5f);
        var other = MirrorMotion.Reflect(yTurn);
        Near(Vector3.Transform(MirrorMotion.Reflect(Points[0]), RotateGizmo.About(MirrorMotion.Reflect(OffCentre), Vector3.UnitY, -0.5f)),
             Vector3.Transform(MirrorMotion.Reflect(Points[0]), other), 1e-6f, "the mirrored Y turn");
    }

    private static Vector3 CentredDisplacement(Vector3 p, Matrix4x4 t, Kind kind, Vector3 pivot)
        => MirrorMotion.CentredStep(p, MirrorMotion.CentredLinear(t, kind), pivot);

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-3")]
    public void CentredKeepsAlreadySymmetricTransforms()
    {
        // A turn about X is its own mirror, whatever its pivot's X: the axis is the same line either way.
        foreach (float angle in new[] { 0.5f, -0.5f, 2.1f, -2.1f })
        {
            var t = RotateGizmo.About(OffCentre, Vector3.UnitX, angle);
            foreach (var p in Points)
                Near(Vector3.Transform(p, t) - p, CentredDisplacement(p, t, Kind.Rotate, OffCentre), 1e-6f, $"X turn {angle}");
        }

        // A stretch about a pivot on the centre is its own mirror.
        var stretch = PartScaleDrag.Build(OnCentre, new Vector3(1.5f, 0.7f, 1.3f));
        foreach (var p in Points)
            Near(Vector3.Transform(p, stretch) - p, CentredDisplacement(p, stretch, Kind.Scale, OnCentre), 1e-6f, "stretch");
    }

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-3")]
    [Trait("Plan", "EDGE-mirror-move-tools-1")]
    [Trait("Plan", "EDGE-mirror-move-tools-24")]
    public void CentredRotationKeepsOnlyTheTurnAboutX()
    {
        // φ round-trips, either sign, past 90°.
        foreach (float degrees in new[] { 30f, -30f, 120f, -120f })
        {
            float angle = degrees * MathF.PI / 180f;
            var t = RotateGizmo.About(OffCentre, Vector3.UnitX, angle);
            var expected = Matrix4x4.CreateRotationX(angle);
            var got = MirrorMotion.CentredLinear(t, Kind.Rotate);
            Assert.Equal(expected.M22 - 1f, got.M22, 1e-6f);
            Assert.Equal(expected.M23, got.M23, 1e-6f);
            Assert.Equal(expected.M32, got.M32, 1e-6f);
            Assert.Equal(expected.M33 - 1f, got.M33, 1e-6f);
        }

        // Sideways turns keep nothing — exactly, so a drag of them is a tap.
        foreach (var axis in new[] { Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitY })
        foreach (float degrees in new[] { 30f, 89f, 135f, 179f, 180f, -60f })
        {
            var t = RotateGizmo.About(OffCentre, axis, degrees * MathF.PI / 180f);
            Assert.True(IsZero(MirrorMotion.CentredLinear(t, Kind.Rotate)), $"{axis} {degrees}° kept something");
            foreach (var p in Points)
                Assert.Equal(Vector3.Zero, CentredDisplacement(p, t, Kind.Rotate, OffCentre));
        }

        // An oblique view ring keeps a pure turn about X, the same way round as its X part.
        var oblique = RotateGizmo.About(OffCentre, Vector3.Normalize(new Vector3(1f, 1f, 1f)), 0.8f);
        var kept = MirrorMotion.CentredLinear(oblique, Kind.Rotate);
        Assert.Equal(0f, kept.M11);
        Assert.Equal(0f, kept.M12);
        Assert.Equal(0f, kept.M13);
        Assert.Equal(0f, kept.M21);
        Assert.Equal(0f, kept.M31);
        Assert.True(kept.M23 > 0f, "the kept turn goes the wrong way");
        float c = kept.M22 + 1f, s = kept.M23;
        Assert.Equal(1f, c * c + s * s, 1e-5f);                      // a rotation: no squash
        Assert.Equal(kept.M22, kept.M33, 1e-6f);
        Assert.Equal(-kept.M23, kept.M32, 1e-6f);
    }

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-3")]
    public void CentredOffsetZeroesXAndScaleIsAboutTheCentrePivot()
    {
        Assert.Equal(new Vector3(0f, 0.02f, -0.01f), MirrorMotion.CentredOffset(new Vector3(0.03f, 0.02f, -0.01f)));

        // A stretch whose gizmo sits off the centre grows from the centre plane instead.
        var stretch = PartScaleDrag.Build(OffCentre, new Vector3(2f, 1f, 1.5f));
        var aboutCentre = PartScaleDrag.Build(OnCentre, new Vector3(2f, 1f, 1.5f));
        foreach (var p in Points)
            Near(Vector3.Transform(p, aboutCentre) - p, CentredDisplacement(p, stretch, Kind.Scale, OffCentre), 1e-6f, "stretch");

        // A move has no turn or stretch.
        Assert.True(IsZero(MirrorMotion.CentredLinear(Matrix4x4.CreateTranslation(0.1f, 0f, 0f), Kind.Translate)));
    }

    [Fact]
    [Trait("Plan", "AC-mirror-move-tools-4")]
    public void SideShareIsSymmetric()
    {
        const float band = 0.01f;
        foreach (float sign in new[] { 1f, -1f })
        {
            Assert.Equal(0.5f, MirrorMotion.Side(0f, sign, band));
            Assert.Equal(1f, MirrorMotion.Side(0.02f * sign, sign, band));
            Assert.Equal(0f, MirrorMotion.Side(-0.02f * sign, sign, band));
            foreach (float x in new[] { 0.001f, 0.004f, 0.0099f, 0.03f })
                Assert.Equal(1f - MirrorMotion.Side(x, sign, band), MirrorMotion.Side(-x, sign, band), 1e-6f);
        }
        Assert.Equal(1f, MirrorMotion.Side(0.001f, 1f, 0f));
        Assert.Equal(0.5f, MirrorMotion.Side(0f, 1f, 0f));
    }
}
