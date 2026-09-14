using System.Numerics;
using Proteus.Gui;
using Xunit;

namespace Proteus.Tests;

/// <summary>The Move gizmo's geometry — the part that decides where a drag puts the part, without an ImGui context.</summary>
public class TranslateGizmoTests
{
    /// <summary>A ray crossing the X axis above x = 0.3 reads t = 0.3, whichever way the ray comes in.</summary>
    [Fact]
    public void ClosestOnAxisFindsWhereTheRayPassesTheAxis()
    {
        var centre = new Vector3(1f, 2f, 3f);
        var origin = centre + new Vector3(0.3f, 5f, 4f);
        var dir = new Vector3(0f, -5f, -4f);

        Assert.True(TranslateGizmo.ClosestOnAxis(centre, Vector3.UnitX, origin, dir, out float t));
        Assert.Equal(0.3f, t, 1e-5f);

        // Skew: the ray misses the axis by 0.2 in Z and still reads the X it passes over.
        Assert.True(TranslateGizmo.ClosestOnAxis(centre, Vector3.UnitX, origin + new Vector3(0f, 0f, 0.2f), new Vector3(0f, -1f, 0f), out t));
        Assert.Equal(0.3f, t, 1e-5f);
    }

    [Fact]
    public void ClosestOnAxisRefusesARayAlongTheAxis()
    {
        Assert.False(TranslateGizmo.ClosestOnAxis(Vector3.Zero, Vector3.UnitZ, new Vector3(0f, 1f, 5f), new Vector3(0f, 0f, -1f), out _));
    }

    [Fact]
    public void RayPlaneHitsInFrontAndNotBehindOrEdgeOn()
    {
        var point = new Vector3(0f, 1f, 0f);
        Assert.True(TranslateGizmo.RayPlane(new Vector3(0.5f, 3f, -0.25f), new Vector3(0f, -2f, 0f), point, Vector3.UnitY, out var hit));
        Assert.Equal(new Vector3(0.5f, 1f, -0.25f), hit);

        Assert.False(TranslateGizmo.RayPlane(new Vector3(0f, 3f, 0f), new Vector3(0f, 1f, 0f), point, Vector3.UnitY, out _));
        Assert.False(TranslateGizmo.RayPlane(new Vector3(0f, 3f, 0f), new Vector3(1f, 0f, 0f), point, Vector3.UnitY, out _));
    }

    [Fact]
    public void SegmentDistanceClampsToTheEnds()
    {
        var a = new Vector2(0f, 0f);
        var b = new Vector2(10f, 0f);
        Assert.Equal(3f, TranslateGizmo.SegmentDistance(new Vector2(5f, 3f), a, b), 1e-5f);
        Assert.Equal(5f, TranslateGizmo.SegmentDistance(new Vector2(13f, 4f), a, b), 1e-5f);
        Assert.Equal(2f, TranslateGizmo.SegmentDistance(new Vector2(-2f, 0f), a, b), 1e-5f);
    }

    [Fact]
    public void InQuadEitherWinding()
    {
        Vector2 a = new(0f, 0f), b = new(4f, 0f), c = new(4f, 4f), d = new(0f, 4f);
        Assert.True(TranslateGizmo.InQuad(new Vector2(2f, 2f), a, b, c, d));
        Assert.True(TranslateGizmo.InQuad(new Vector2(2f, 2f), d, c, b, a));
        Assert.False(TranslateGizmo.InQuad(new Vector2(5f, 2f), a, b, c, d));
    }

    /// <summary>An arrow pointing at the camera cannot be taken, and a plane seen edge-on cannot either.</summary>
    [Fact]
    public void HandlesFacingTheViewAreNotUsable()
    {
        var view = Vector3.UnitZ;
        Assert.False(TranslateGizmo.Usable(TranslateGizmo.Handle.Z, view));
        Assert.True(TranslateGizmo.Usable(TranslateGizmo.Handle.X, view));
        Assert.True(TranslateGizmo.Usable(TranslateGizmo.Handle.XY, view));
        Assert.False(TranslateGizmo.Usable(TranslateGizmo.Handle.YZ, view));
        Assert.False(TranslateGizmo.Usable(TranslateGizmo.Handle.None, view));
    }
}
