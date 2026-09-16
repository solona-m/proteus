using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Proteus.Gui;

/// <summary>
/// The Move tool's handle: three arrows and three plane squares at a pivot, dragged the way Blender's and 3ds
/// Max's translate gizmos are.
/// <para/>
/// Works entirely in the MODEL'S OWN SPACE, the one the solve edits and the file is written in, and knows nothing
/// about which surface it is drawn on. The host supplies the two things that differ between the model viewer and
/// the character in the game world: where a model-space point lands on screen, and which model-space ray lies under
/// a screen pixel. Everything else — hit testing, the drag, the drawing — is the same code for both, so the gizmo
/// cannot behave one way in the viewer and another on the character.
/// </summary>
public sealed class TranslateGizmo
{
    public enum Handle { None, X, Y, Z, XY, YZ, XZ }

    /// <summary>Arrow length on screen, in pixels — constant however far the camera is, as in every DCC tool.</summary>
    private const float ArrowPixels = 90f;

    /// <summary>How near an arrow the mouse must be to take it, in pixels.</summary>
    private const float GrabPixels = 7f;

    /// <summary>A plane square's inner and outer corners, as fractions of the arrow length.</summary>
    private const float PlaneNear = 0.22f, PlaneFar = 0.42f;

    /// <summary>
    /// An axis this nearly pointing at the camera (|cos| of its angle to the view ray) cannot be dragged: the
    /// mouse moving a pixel would throw the part to the horizon. Its plane squares go the same way when the plane
    /// is this close to edge-on.
    /// </summary>
    private const float Degenerate = 0.985f;

    /// <summary>The handle under the mouse this frame, when not dragging.</summary>
    public Handle Hovered { get; private set; }

    /// <summary>The handle being dragged; <see cref="Handle.None"/> between drags.</summary>
    public Handle Active { get; private set; }

    /// <summary>The mouse is over a handle or a drag is under way — the host must not orbit, pick or paint.</summary>
    public bool Capturing => Hovered != Handle.None || Active != Handle.None;

    /// <summary>A drag began this frame.</summary>
    public bool Started { get; private set; }

    /// <summary>A drag was released this frame; <see cref="Offset"/> still holds its final value.</summary>
    public bool Ended { get; private set; }

    /// <summary>The whole drag so far, in model units, from where it started.</summary>
    public Vector3 Offset { get; private set; }

    /// <summary>
    /// The ImGui frame this was last updated in. <see cref="Started"/> and <see cref="Ended"/> describe THAT frame
    /// only, so a host that did not update the gizmo this frame must not read them as news.
    /// </summary>
    public int Frame { get; private set; } = -1;

    private Vector3 dragPivot;
    private float dragT0;
    private Vector3 dragHit0;

    /// <summary>
    /// Hit test, drag and draw for one frame.
    /// </summary>
    /// <param name="pivot">The gizmo's centre in model space — while dragging, the host's pivot is ignored and the
    /// drag's own starting pivot is used, so the handle does not run away from the mouse as the part follows it.</param>
    /// <param name="toScreen">A model-space point to absolute screen pixels; null when it cannot be shown.</param>
    /// <param name="screenRay">Absolute screen pixels to a model-space ray (direction need not be unit); null when
    /// there is none.</param>
    /// <param name="mouse">The mouse, in absolute screen pixels.</param>
    /// <param name="mouseAllowed">The mouse is over the surface and free to act: not over another window, not
    /// handed to the game. Ignored while a drag is under way, which owns the mouse until release.</param>
    /// <param name="pressed">The left button went down this frame.</param>
    /// <param name="down">The left button is held.</param>
    /// <param name="background">Draw on the background draw list (over the game world) rather than the current window's.</param>
    public void Update(Vector3 pivot, Func<Vector3, Vector2?> toScreen, Func<Vector2, (Vector3 Origin, Vector3 Dir)?> screenRay,
                       Vector2 mouse, bool mouseAllowed, bool pressed, bool down, bool background)
    {
        Started = Ended = false;
        Frame = ImGui.GetFrameCount();
        var centre = Active != Handle.None ? dragPivot : pivot;

        if (toScreen(centre) is not { } sc) { CancelHover(); return; }
        float len = ModelLengthForPixels(centre, sc, toScreen);
        if (len <= 0f) { CancelHover(); return; }

        var ray = screenRay(sc);
        var view = ray is { } r && r.Dir.LengthSquared() > 1e-12f ? Vector3.Normalize(r.Dir) : Vector3.UnitZ;

        // ── the drag ──
        if (Active != Handle.None)
        {
            if (!down)
            {
                Ended = true;
                Active = Handle.None;
            }
            else if (screenRay(mouse) is { } mr && Measure(Active, centre, mr.Origin, mr.Dir, view, out float t, out var hit))
            {
                Offset = IsAxis(Active) ? AxisOf(Active) * (t - dragT0) : hit - dragHit0;
            }
        }
        else
        {
            Hovered = mouseAllowed ? HitTest(centre, len, sc, mouse, view, toScreen) : Handle.None;
            if (pressed && Hovered != Handle.None && screenRay(mouse) is { } mr
                && Measure(Hovered, centre, mr.Origin, mr.Dir, view, out float t, out var hit))
            {
                Active = Hovered;
                dragPivot = centre;
                dragT0 = t;
                dragHit0 = hit;
                Offset = Vector3.Zero;
                Started = true;
            }
        }

        Draw(background, centre, len, view, toScreen);
    }

    /// <summary>Drop a drag without finishing it — the surface went away. Reported as ended, so the host records it.</summary>
    public void Release()
    {
        Frame = ImGui.GetFrameCount();
        Started = false;
        Ended = Active != Handle.None;
        Active = Handle.None;
        Hovered = Handle.None;
    }

    private void CancelHover()
    {
        Hovered = Handle.None;
        if (Active != Handle.None) { Active = Handle.None; Ended = true; }
    }

    /// <summary>How long, in model units, a segment from <paramref name="centre"/> must be to span
    /// <see cref="ArrowPixels"/> on screen — measured along whichever axis shows longest.</summary>
    private static float ModelLengthForPixels(Vector3 centre, Vector2 sc, Func<Vector3, Vector2?> toScreen)
    {
        const float Probe = 0.01f;
        float best = 0f;
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            if (toScreen(centre + axis * Probe) is { } s)
                best = MathF.Max(best, Vector2.Distance(s, sc));
        return best > 1e-4f ? Probe * ProteusStyle.S(ArrowPixels) / best : 0f;
    }

    private static bool IsAxis(Handle h) => h is Handle.X or Handle.Y or Handle.Z;

    internal static Vector3 AxisOf(Handle h) => h switch
    {
        Handle.X => Vector3.UnitX,
        Handle.Y => Vector3.UnitY,
        Handle.Z => Vector3.UnitZ,
        _ => Vector3.Zero,
    };

    /// <summary>A plane handle's normal: the axis it does not contain.</summary>
    internal static Vector3 NormalOf(Handle h) => h switch
    {
        Handle.XY => Vector3.UnitZ,
        Handle.YZ => Vector3.UnitX,
        Handle.XZ => Vector3.UnitY,
        _ => Vector3.Zero,
    };

    /// <summary>The two axes a plane handle spans.</summary>
    private static (Vector3 A, Vector3 B) PlaneAxes(Handle h) => h switch
    {
        Handle.XY => (Vector3.UnitX, Vector3.UnitY),
        Handle.YZ => (Vector3.UnitY, Vector3.UnitZ),
        _ => (Vector3.UnitX, Vector3.UnitZ),
    };

    /// <summary>Whether a handle can be dragged from this view at all — see <see cref="Degenerate"/>.</summary>
    internal static bool Usable(Handle h, Vector3 view)
    {
        if (h == Handle.None) return false;
        return IsAxis(h)
            ? MathF.Abs(Vector3.Dot(AxisOf(h), view)) < Degenerate
            : MathF.Abs(Vector3.Dot(NormalOf(h), view)) > 1f - Degenerate;
    }

    /// <summary>Where the mouse ray meets a handle: the parameter along its axis, or the point on its plane.</summary>
    private static bool Measure(Handle h, Vector3 centre, Vector3 origin, Vector3 dir, Vector3 view, out float t, out Vector3 hit)
    {
        t = 0f;
        hit = default;
        if (IsAxis(h))
            return ClosestOnAxis(centre, AxisOf(h), origin, dir, out t);
        return RayPlane(origin, dir, centre, NormalOf(h), out hit);
    }

    private Handle HitTest(Vector3 centre, float len, Vector2 sc, Vector2 mouse, Vector3 view, Func<Vector3, Vector2?> toScreen)
    {
        // Planes first: a square sits between two arrows and is the smaller target, so where the two overlap near
        // the centre the square is what the user was aiming for.
        foreach (var h in new[] { Handle.XY, Handle.YZ, Handle.XZ })
        {
            if (!Usable(h, view)) continue;
            var (a, b) = PlaneAxes(h);
            if (toScreen(centre + (a * PlaneNear + b * PlaneNear) * len) is not { } p0
                || toScreen(centre + (a * PlaneFar + b * PlaneNear) * len) is not { } p1
                || toScreen(centre + (a * PlaneFar + b * PlaneFar) * len) is not { } p2
                || toScreen(centre + (a * PlaneNear + b * PlaneFar) * len) is not { } p3)
                continue;
            if (InQuad(mouse, p0, p1, p2, p3)) return h;
        }

        var best = Handle.None;
        float bestD = ProteusStyle.S(GrabPixels);
        foreach (var h in new[] { Handle.X, Handle.Y, Handle.Z })
        {
            if (!Usable(h, view) || toScreen(centre + AxisOf(h) * len) is not { } tip) continue;
            float d = SegmentDistance(mouse, sc, tip);
            if (d < bestD) { bestD = d; best = h; }
        }
        return best;
    }

    private void Draw(bool background, Vector3 centre, float len, Vector3 view, Func<Vector3, Vector2?> toScreen)
    {
        var dl = background ? ImGui.GetBackgroundDrawList() : ImGui.GetWindowDrawList();
        float scale = ProteusStyle.S(1f);

        // The ghost: where the drag started, so how far it has gone reads at a glance.
        if (Active != Handle.None && toScreen(centre) is { } ghost && toScreen(centre + Offset) is { } now)
        {
            dl.AddLine(ghost, now, 0x80FFFFFFu, 1f * scale);
            dl.AddCircleFilled(ghost, 3f * scale, 0x80FFFFFFu);
        }

        var at = Active != Handle.None ? centre + Offset : centre;
        if (toScreen(at) is not { } sc) return;

        foreach (var h in new[] { Handle.XY, Handle.YZ, Handle.XZ })
        {
            if (!Usable(h, view)) continue;
            if (Active != Handle.None && Active != h) continue;
            var (a, b) = PlaneAxes(h);
            if (toScreen(at + (a * PlaneNear + b * PlaneNear) * len) is not { } p0
                || toScreen(at + (a * PlaneFar + b * PlaneNear) * len) is not { } p1
                || toScreen(at + (a * PlaneFar + b * PlaneFar) * len) is not { } p2
                || toScreen(at + (a * PlaneNear + b * PlaneFar) * len) is not { } p3)
                continue;
            bool lit = Hovered == h || Active == h;
            uint rgb = ColourOf(h);
            dl.AddQuadFilled(p0, p1, p2, p3, (lit ? 0xA0000000u : 0x50000000u) | rgb);
            dl.AddQuad(p0, p1, p2, p3, (lit ? 0xFF000000u : 0xB0000000u) | rgb, 1.5f * scale);
        }

        foreach (var h in new[] { Handle.X, Handle.Y, Handle.Z })
        {
            if (!Usable(h, view)) continue;
            if (Active != Handle.None && Active != h) continue;
            if (toScreen(at + AxisOf(h) * len) is not { } tip) continue;
            bool lit = Hovered == h || Active == h;
            uint colour = (lit ? 0xFF000000u : 0xD0000000u) | (lit ? Brighten(ColourOf(h)) : ColourOf(h));
            dl.AddLine(sc, tip, colour, (lit ? 3.5f : 2.5f) * scale);

            // The arrowhead, drawn in screen space along the projected shaft.
            var along = tip - sc;
            float n = along.Length();
            if (n < 1f) continue;
            along /= n;
            var side = new Vector2(-along.Y, along.X);
            float head = 11f * scale, half = 5f * scale;
            dl.AddTriangleFilled(tip + along * head * 0.4f, tip - along * head * 0.6f + side * half,
                                 tip - along * head * 0.6f - side * half, colour);
        }

        dl.AddCircleFilled(sc, 3.5f * scale, 0xFFFFFFFFu);
    }

    /// <summary>ABGR, without alpha: X red, Y green, Z blue, as in every DCC tool.</summary>
    private static uint ColourOf(Handle h) => h switch
    {
        Handle.X => 0x003C3CE6u,
        Handle.Y => 0x0050C850u,
        Handle.Z => 0x00E68C3Cu,
        Handle.XY => 0x0028C8E6u,   // the colour of the plane's normal axis would mislead; a neutral amber per plane
        Handle.YZ => 0x00E6C83Cu,
        _ => 0x00E63CC8u,
    };

    private static uint Brighten(uint rgb)
    {
        uint r = Math.Min(255u, (rgb & 0xFF) + 60), g = Math.Min(255u, ((rgb >> 8) & 0xFF) + 60), b = Math.Min(255u, ((rgb >> 16) & 0xFF) + 60);
        return r | (g << 8) | (b << 16);
    }

    // ── pure geometry, unit tested ──────────────────────────────────────────

    /// <summary>
    /// The parameter <paramref name="t"/> of the point on the line <c>centre + axis·t</c> closest to the ray
    /// <c>origin + dir·s</c>. False when the two are parallel and every point is equally close.
    /// </summary>
    internal static bool ClosestOnAxis(Vector3 centre, Vector3 axis, Vector3 origin, Vector3 dir, out float t)
    {
        t = 0f;
        var w = centre - origin;
        float a = Vector3.Dot(axis, axis), b = Vector3.Dot(axis, dir), c = Vector3.Dot(dir, dir);
        float d = Vector3.Dot(axis, w), e = Vector3.Dot(dir, w);
        float denom = a * c - b * b;
        if (MathF.Abs(denom) < 1e-10f * a * c) return false;
        t = (b * e - c * d) / denom;
        return float.IsFinite(t);
    }

    /// <summary>Where the ray <c>origin + dir·s</c> (s ≥ 0) meets the plane through <paramref name="point"/> across
    /// <paramref name="normal"/>. False when it runs parallel or the plane is behind it.</summary>
    internal static bool RayPlane(Vector3 origin, Vector3 dir, Vector3 point, Vector3 normal, out Vector3 hit)
    {
        hit = default;
        float den = Vector3.Dot(normal, dir);
        if (MathF.Abs(den) < 1e-6f * dir.Length()) return false;
        float s = Vector3.Dot(normal, point - origin) / den;
        if (s < 0f || !float.IsFinite(s)) return false;
        hit = origin + dir * s;
        return true;
    }

    /// <summary>Distance from <paramref name="p"/> to the segment <paramref name="a"/>–<paramref name="b"/>.</summary>
    internal static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        float t = l2 > 0f ? Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0f, 1f) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>Whether <paramref name="p"/> lies inside the convex quad, whichever way it winds.</summary>
    internal static bool InQuad(Vector2 p, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        static float Cross(Vector2 o, Vector2 u, Vector2 v) => (u.X - o.X) * (v.Y - o.Y) - (u.Y - o.Y) * (v.X - o.X);
        float c0 = Cross(a, b, p), c1 = Cross(b, c, p), c2 = Cross(c, d, p), c3 = Cross(d, a, p);
        return (c0 >= 0 && c1 >= 0 && c2 >= 0 && c3 >= 0) || (c0 <= 0 && c1 <= 0 && c2 <= 0 && c3 <= 0);
    }
}
