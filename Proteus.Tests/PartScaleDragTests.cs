using System;
using System.Numerics;
using Proteus.Gui;
using Xunit;
using Handle = Proteus.Gui.PartScaleDrag.Handle;

namespace Proteus.Tests;

/// <summary>
/// The Scale tool's handles, driven frame by frame through <see cref="PartScaleDrag.Step"/> — what the model viewer
/// and the character both call — against synthetic orthographic cameras, so where every handle lands on screen is
/// known exactly.
/// </summary>
public class PartScaleDragTests
{
    private static readonly Vector2 Centre = new(400f, 300f);

    /// <summary>
    /// An orthographic camera looking along <paramref name="view"/>, 1000 px per metre, the model origin at
    /// <see cref="Centre"/>. Screen right is the view's right, screen down its down.
    /// </summary>
    private sealed class Camera
    {
        public readonly Vector3 View, Right, Up;
        public Func<Vector3, Vector2?> ToScreen;
        public Func<Vector2, (Vector3 Origin, Vector3 Dir)?> ScreenRay;

        public Camera(Vector3 view)
        {
            View = Vector3.Normalize(view);
            Right = Vector3.Normalize(Vector3.Cross(View, Vector3.UnitY));
            Up = Vector3.Cross(Right, View);
            var (r, u, v) = (Right, Up, View);
            ToScreen = p => Centre + new Vector2(1000f * Vector3.Dot(p, r), -1000f * Vector3.Dot(p, u));
            ScreenRay = s => (r * ((s.X - Centre.X) / 1000f) - u * ((s.Y - Centre.Y) / 1000f) - v * 10f, v);
        }
    }

    /// <summary>Looking straight down −Z: X and Y show 80 px handles, Z points at the camera.</summary>
    private static readonly Camera Ortho = new(-Vector3.UnitZ);

    /// <summary>Tilted so all three axes show.</summary>
    private static readonly Camera Oblique = new(new Vector3(0.5f, 0.5f, -0.7071f));

    /// <summary>A view whose |cos| with Z is <paramref name="cos"/>, leaning into −X −Y so Z's handle shows down-left,
    /// clear of the X and Y handles.</summary>
    private static Camera LeaningZ(float cos)
    {
        float side = MathF.Sqrt((1f - cos * cos) / 2f);
        return new Camera(new Vector3(-side, -side, -cos));
    }

    private static PartScaleDrag Frame(PartScaleDrag drag, Camera cam, Vector2 mouse, bool pressed = false,
                                       bool down = false, bool overPart = false, bool allowed = true,
                                       Vector3? pivot = null)
    {
        drag.Step(pivot ?? Vector3.Zero, cam.ToScreen, cam.ScreenRay, mouse, overPart, allowed, pressed, down, 1f);
        return drag;
    }

    private static Vector2 TipOf(PartScaleDrag drag, Handle h) => (h switch
    {
        Handle.X => drag.Last.X,
        Handle.Y => drag.Last.Y,
        _ => drag.Last.Z,
    }).Tip!.Value;

    /// <summary>Press on a handle's tip and hold it there: the drag's first frame.</summary>
    private static PartScaleDrag PressOn(Camera cam, Handle h, Vector3? pivot = null)
    {
        var drag = new PartScaleDrag();
        Frame(drag, cam, new Vector2(-1000f, -1000f), pivot: pivot);    // measure, mouse far away
        Frame(drag, cam, TipOf(drag, h), pressed: true, down: true, pivot: pivot);
        Assert.Equal(h, drag.Active);
        return drag;
    }

    // ── handles ──────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-axis-scale-1")]
    [Trait("Plan", "EDGE-axis-scale-11")]
    public void HandlesHitAtEachTipWithoutHoveringThePart()
    {
        foreach (var pivot in new[] { Vector3.Zero, new Vector3(0.03f, -0.02f, 0.01f) })
        {
            var drag = Frame(new PartScaleDrag(), Oblique, new Vector2(-1000f, -1000f), pivot: pivot);
            Assert.True(drag.Last.X.Usable && drag.Last.Y.Usable && drag.Last.Z.Usable);
            foreach (var h in new[] { Handle.X, Handle.Y, Handle.Z })
            {
                Frame(drag, Oblique, TipOf(drag, h), overPart: false, pivot: pivot);
                Assert.Equal(h, drag.Hovered);
                Assert.True(drag.Capturing);
            }
        }
    }

    [Fact]
    [Trait("Plan", "AC-axis-scale-2")]
    public void DraggingTheYHandleScalesYOnly()
    {
        var drag = PressOn(Ortho, Handle.Y);
        Assert.True(drag.Started);
        var tip = TipOf(drag, Handle.Y);
        Assert.Equal(Centre + new Vector2(0f, -80f), tip);

        Frame(drag, Ortho, tip + new Vector2(0f, -40f), down: true);
        float s = MathF.Exp(0.5f);
        Assert.Equal(Handle.Y, drag.Active);
        Assert.Equal(1f, drag.Factors.X);
        Assert.Equal(s, drag.Factors.Y, 1e-5f);
        Assert.Equal(1f, drag.Factors.Z);

        var p = new Vector3(0.01f, 0.02f, 0.03f);
        var moved = Vector3.Transform(p, drag.Transform);
        Assert.Equal(0.01f, moved.X, 1e-6f);
        Assert.Equal(0.02f * s, moved.Y, 1e-6f);
        Assert.Equal(0.03f, moved.Z, 1e-6f);
    }

    /// <summary>The tip starts out under the cursor and stays within a pixel of it over a short drag.</summary>
    [Fact]
    [Trait("Plan", "AC-axis-scale-2")]
    public void AxisTipFollowsTheCursor()
    {
        for (int d = -10; d <= 10; d++)
        {
            var drag = PressOn(Ortho, Handle.X);
            var press = TipOf(drag, Handle.X);
            Frame(drag, Ortho, press + new Vector2(d, 0f), down: true);
            var tip = drag.Last.ActiveTip!.Value;
            Assert.True(MathF.Abs(tip.X - (press.X + d)) < 1f, $"drag {d}: tip at {tip.X}, cursor at {press.X + d}");
            if (d == 0) Assert.Equal(press, tip);
        }
    }

    /// <summary>A drag one handle length long scales by e: the tip runs ahead of the cursor on a long drag.</summary>
    [Fact]
    [Trait("Plan", "AC-axis-scale-2")]
    public void AxisTipAtOneSpanIsEScaled()
    {
        var drag = PressOn(Ortho, Handle.X);
        Frame(drag, Ortho, TipOf(drag, Handle.X) + new Vector2(80f, 0f), down: true);
        Assert.Equal(MathF.E, drag.Factors.X, 1e-4f);
    }

    [Fact]
    [Trait("Plan", "AC-axis-scale-2")]
    public void DraggingInwardShrinksAndBackRestores()
    {
        var drag = PressOn(Ortho, Handle.X);
        var press = TipOf(drag, Handle.X);
        Frame(drag, Ortho, press + new Vector2(-40f, 0f), down: true);
        Assert.Equal(MathF.Exp(-0.5f), drag.Factors.X, 1e-5f);

        Frame(drag, Ortho, press, down: true);
        Assert.Equal(Vector3.One, drag.Factors);
        Assert.Equal(Matrix4x4.Identity, drag.Transform);
    }

    [Fact]
    [Trait("Plan", "AC-axis-scale-2")]
    public void ReleaseEndsTheDragKeepingTheStretch()
    {
        var drag = PressOn(Ortho, Handle.Y);
        Frame(drag, Ortho, TipOf(drag, Handle.Y) + new Vector2(0f, -40f), down: true);
        var kept = drag.Transform;

        Frame(drag, Ortho, TipOf(drag, Handle.Y) + new Vector2(0f, -40f), down: false);
        Assert.True(drag.Ended);
        Assert.Equal(Handle.None, drag.Active);
        Assert.Equal(kept, drag.Transform);
    }

    [Fact]
    [Trait("Plan", "AC-axis-scale-2")]
    public void FactorsForLeavesOtherAxesAtOne()
    {
        Assert.Equal(new Vector3(2f, 1f, 1f), PartScaleDrag.FactorsFor(Handle.X, 2f));
        Assert.Equal(new Vector3(1f, 2f, 1f), PartScaleDrag.FactorsFor(Handle.Y, 2f));
        Assert.Equal(new Vector3(1f, 1f, 2f), PartScaleDrag.FactorsFor(Handle.Z, 2f));
        Assert.Equal(new Vector3(2f, 2f, 2f), PartScaleDrag.FactorsFor(Handle.Uniform, 2f));
    }

    // ── even scale ───────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-axis-scale-3")]
    public void PressOnPartAwayFromHandlesScalesEvenly()
    {
        var drag = new PartScaleDrag();
        var away = Centre + new Vector2(-40f, 40f);         // 40 px from every handle and the centre's reach
        Frame(drag, Ortho, away, overPart: true);
        Assert.Equal(Handle.Uniform, drag.Hovered);

        Frame(drag, Ortho, away, pressed: true, down: true, overPart: true);
        Assert.Equal(Handle.Uniform, drag.Active);
        Frame(drag, Ortho, away + new Vector2(100f, 37f), down: true, overPart: true);   // the 37 px down does nothing

        float s = PartScaleDrag.ScaleFor(100f);
        Assert.Equal(new Vector3(s), drag.Factors);
        Assert.Equal(PartScaleDrag.Build(Vector3.Zero, 100f), drag.Transform);
    }

    [Fact]
    [Trait("Plan", "AC-axis-scale-3")]
    public void CentreDotScalesEvenly()
    {
        var drag = new PartScaleDrag();
        Frame(drag, Ortho, Centre, pressed: true, down: true, overPart: false);
        Assert.Equal(Handle.Uniform, drag.Active);
    }

    // ── capture ──────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-axis-scale-4")]
    public void HoveredHandleCapturesAndOthersDoNot()
    {
        var drag = Frame(new PartScaleDrag(), Ortho, new Vector2(-1000f, -1000f));
        Assert.Equal(Handle.None, drag.Hovered);
        Assert.False(drag.Capturing);

        Frame(drag, Ortho, TipOf(drag, Handle.X));
        Assert.Equal(Handle.X, drag.Hovered);
        Assert.True(drag.Capturing);

        Frame(drag, Ortho, Centre + new Vector2(200f, 200f), overPart: false);
        Assert.Equal(Handle.None, drag.Hovered);
        Assert.False(drag.Capturing);
    }

    [Fact]
    [Trait("Plan", "AC-axis-scale-4")]
    public void ActiveDragHoldsOnlyItsHandle()
    {
        var drag = PressOn(Ortho, Handle.X);
        var pressTip = TipOf(drag, Handle.X);
        var overY = TipOf(drag, Handle.Y);

        Frame(drag, Ortho, overY, down: true);
        Assert.Equal(Handle.X, drag.Active);
        // Only X's direction counts: the Y tip is 80 px left of the press along X.
        Assert.Equal(PartScaleDrag.AxisScaleFor(-80f, 80f), drag.Factors.X, 1e-6f);
        Assert.Equal(1f, drag.Factors.Y);
        Assert.NotNull(drag.Last.ActiveTip);
        Assert.Equal(pressTip, drag.Last.GhostTip);
    }

    // ── edges ────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-1")]
    public void AxisFacingTheCameraCannotBeGrabbed()
    {
        var cam = LeaningZ(0.987f);
        var drag = Frame(new PartScaleDrag(), cam, new Vector2(-1000f, -1000f));
        var zTip = drag.Last.Z.Tip!.Value;
        var sc = drag.Last.Sc;

        // Only the facing test can stop it: it is outside the centre's reach and clear of the other two handles.
        Assert.True(Vector2.Distance(zTip, sc) > PartScaleDrag.CentrePixels);
        Assert.True(TranslateGizmo.SegmentDistance(zTip, sc, TipOf(drag, Handle.X)) > PartScaleDrag.GrabPixels);
        Assert.True(TranslateGizmo.SegmentDistance(zTip, sc, TipOf(drag, Handle.Y)) > PartScaleDrag.GrabPixels);

        Frame(drag, cam, zTip);
        Assert.False(drag.Last.Z.Usable);
        Assert.NotEqual(Handle.Z, drag.Hovered);

        Frame(drag, cam, TipOf(drag, Handle.X));
        Assert.Equal(Handle.X, drag.Hovered);
        Frame(drag, cam, TipOf(drag, Handle.Y));
        Assert.Equal(Handle.Y, drag.Hovered);
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-1")]
    public void AxisWithUnderOnePixelProjectionIsNotUsable()
    {
        var view = Vector3.Normalize(new Vector3(0.5f, 0.5f, -0.7071f));
        Assert.False(PartScaleDrag.AxisUsable(Handle.Z, view, Centre, Centre + new Vector2(0.5f, 0f)));
        Assert.True(PartScaleDrag.AxisUsable(Handle.Z, view, Centre, Centre + new Vector2(2f, 0f)));
        Assert.False(PartScaleDrag.AxisUsable(Handle.Z, view, Centre, null));
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-2")]
    public void InwardDragClampsAndNeverMirrors()
    {
        var drag = PressOn(Ortho, Handle.X);
        Frame(drag, Ortho, TipOf(drag, Handle.X) + new Vector2(-5000f, 0f), down: true);
        Assert.Equal(PartScaleDrag.MinScale, drag.Factors.X);
        Assert.True(drag.Factors.X > 0f);
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-3")]
    public void OutwardDragClampsAtMaxScale()
    {
        var drag = PressOn(Ortho, Handle.X);
        Frame(drag, Ortho, TipOf(drag, Handle.X) + new Vector2(5000f, 0f), down: true);
        Assert.Equal(PartScaleDrag.MaxScale, drag.Factors.X);
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-4")]
    public void CentreBeatsShaftsNearThePivot()
    {
        var drag = Frame(new PartScaleDrag(), Ortho, Centre + new Vector2(2f, -2f));
        Assert.True(TranslateGizmo.SegmentDistance(Centre + new Vector2(2f, -2f), Centre, TipOf(drag, Handle.X))
                    < PartScaleDrag.GrabPixels);
        Assert.Equal(Handle.Uniform, drag.Hovered);
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-5")]
    public void ShaftBeatsThePartUnderIt()
    {
        var drag = Frame(new PartScaleDrag(), Ortho, Centre + new Vector2(40f, 2f), overPart: true);
        Assert.Equal(Handle.X, drag.Hovered);
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-6")]
    public void NotAllowedHoversNothingButADragContinues()
    {
        var drag = Frame(new PartScaleDrag(), Ortho, new Vector2(-1000f, -1000f));
        var tip = TipOf(drag, Handle.X);
        Frame(drag, Ortho, tip, pressed: true, down: true, allowed: false);
        Assert.Equal(Handle.None, drag.Hovered);
        Assert.Equal(Handle.None, drag.Active);

        Frame(drag, Ortho, tip, down: false);
        Frame(drag, Ortho, tip, pressed: true, down: true);
        Assert.Equal(Handle.X, drag.Active);
        Frame(drag, Ortho, tip + new Vector2(40f, 0f), down: true, allowed: false);
        Assert.Equal(Handle.X, drag.Active);
        Assert.Equal(MathF.Exp(0.5f), drag.Factors.X, 1e-5f);
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-7")]
    public void ReleaseCoreMidDragReportsEndedAndKeepsTransform()
    {
        var drag = PressOn(Ortho, Handle.X);
        Frame(drag, Ortho, TipOf(drag, Handle.X) + new Vector2(20f, 0f), down: true);
        var kept = drag.Transform;

        drag.ReleaseCore();
        Assert.True(drag.Ended);
        Assert.Equal(Handle.None, drag.Active);
        Assert.Equal(kept, drag.Transform);
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-8")]
    public void UnprojectablePivotCancelsTheDrag()
    {
        var drag = PressOn(Ortho, Handle.X);
        Func<Vector3, Vector2?> nowhere = _ => null;
        drag.Step(Vector3.Zero, nowhere, Ortho.ScreenRay, Centre, false, true, false, true, 1f);
        Assert.True(drag.Ended);
        Assert.Equal(Handle.None, drag.Active);

        drag.Step(Vector3.Zero, nowhere, Ortho.ScreenRay, Centre, true, true, false, false, 1f);
        Assert.Equal(Handle.None, drag.Hovered);
        Assert.False(drag.Last.Valid);
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-9")]
    public void OffscreenTipIsSkipped()
    {
        float len = 0.08f;
        Func<Vector3, Vector2?> toScreen = p => p.X > len * 0.5f ? null : Ortho.ToScreen(p);
        var drag = new PartScaleDrag();
        drag.Step(Vector3.Zero, toScreen, Ortho.ScreenRay, Centre + new Vector2(80f, 0f), false, true, false, false, 1f);
        Assert.False(drag.Last.X.Usable);
        Assert.NotEqual(Handle.X, drag.Hovered);

        drag.Step(Vector3.Zero, toScreen, Ortho.ScreenRay, Centre + new Vector2(0f, -80f), false, true, false, false, 1f);
        Assert.Equal(Handle.Y, drag.Hovered);
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-10")]
    public void PivotIsHeldFromThePress()
    {
        var drag = PressOn(Ortho, Handle.X);
        Frame(drag, Ortho, TipOf(drag, Handle.X) + new Vector2(40f, 0f), down: true, pivot: new Vector3(0.05f, 0.05f, 0f));
        Assert.Equal(PartScaleDrag.Build(Vector3.Zero, new Vector3(MathF.Exp(0.5f), 1f, 1f)), drag.Transform);
        Assert.Equal(Vector3.Zero, drag.Last.Centre);
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-14")]
    public void TapOnHandleIsIdentity()
    {
        var drag = PressOn(Ortho, Handle.Y);
        Assert.True(drag.Started);
        Frame(drag, Ortho, TipOf(drag, Handle.Y), down: false);
        Assert.True(drag.Ended);
        Assert.Equal(Vector3.One, drag.Factors);
        Assert.Equal(Matrix4x4.Identity, drag.Transform);
    }

    /// <summary>A handle seen nearly end-on, 16 px long, is paced as if it were 20 px: a pixel cannot jump the part.</summary>
    [Fact]
    [Trait("Plan", "EDGE-axis-scale-22")]
    public void ForeshortenedHandleUsesTheSpanFloor()
    {
        var cam = LeaningZ(MathF.Sqrt(1f - 0.2f * 0.2f));
        var drag = Frame(new PartScaleDrag(), cam, new Vector2(-1000f, -1000f));
        var zTip = drag.Last.Z.Tip!.Value;
        float shown = Vector2.Distance(zTip, drag.Last.Sc);
        Assert.InRange(shown, 14f, 18f);
        Assert.True(drag.Last.Z.Usable);

        Frame(drag, cam, zTip, pressed: true, down: true);
        Assert.Equal(Handle.Z, drag.Active);
        var along = Vector2.Normalize(zTip - drag.Last.Sc);
        Frame(drag, cam, zTip + along, down: true);
        float floor = PartScaleDrag.SpanFloor * PartScaleDrag.HandlePixels;
        Assert.Equal(MathF.Exp(1f / floor), drag.Factors.Z, 1e-5f);
        Assert.True(drag.Factors.Z < MathF.Exp(1f / shown));
    }
}
