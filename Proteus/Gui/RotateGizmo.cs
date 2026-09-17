using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Proteus.Gui;

/// <summary>
/// The Rotate tool's handle: three rings at a pivot — X red, Y green, Z blue — and a wider white ring facing the
/// viewer, dragged the way Blender's and 3ds Max's rotate gizmos are. Grab a ring and drag along it to turn the part
/// about that ring's axis; the outer ring turns it about the line of sight.
/// <para/>
/// A sibling of <see cref="TranslateGizmo"/>, built the same way: it works in the MODEL'S OWN SPACE, and the host
/// supplies where a model-space point lands on screen and which model-space ray lies under a pixel, so the model
/// viewer and the character in the game world share every line of it.
/// <para/>
/// The angle comes from how far the mouse travels ALONG the ring where it was grabbed, as 3ds Max measures it, not
/// from the angle around the pivot on screen. A ring seen edge-on is a line with no "around" to speak of, but it
/// still has a direction along it, so every ring stays draggable from every view.
/// </summary>
public sealed class RotateGizmo
{
    public enum Handle { None, X, Y, Z, View }

    /// <summary>The axis rings' radius on screen, in pixels — constant however far the camera is.</summary>
    private const float RingPixels = 80f;

    /// <summary>The view ring's radius, as a multiple of the axis rings'.</summary>
    private const float ViewRingScale = 1.2f;

    /// <summary>How near a ring the mouse must be to take it, in pixels.</summary>
    private const float GrabPixels = 7f;

    private const int Segments = 64;

    public Handle Hovered { get; private set; }

    public Handle Active { get; private set; }

    /// <summary>The mouse is over a ring or a drag is under way — the host must not orbit, pick or paint.</summary>
    public bool Capturing => Hovered != Handle.None || Active != Handle.None;

    /// <summary>A drag began this frame.</summary>
    public bool Started { get; private set; }

    /// <summary>A drag was released this frame; <see cref="Transform"/> still holds its final value.</summary>
    public bool Ended { get; private set; }

    /// <summary>The ImGui frame this was last updated in; <see cref="Started"/> and <see cref="Ended"/> describe that frame only.</summary>
    public int Frame { get; private set; } = -1;

    /// <summary>The turn so far, in radians, about <see cref="Axis"/>.</summary>
    public float Angle { get; private set; }

    /// <summary>The whole drag so far as one model-space transform about the pivot the drag started at.</summary>
    public Matrix4x4 Transform { get; private set; } = Matrix4x4.Identity;

    private Vector3 dragPivot, dragAxis;
    private Vector2 pressMouse, grabTangent;
    private float pixelsPerRadian, grabTheta;
    private float dragLen;

    /// <summary>Hit test, drag and draw for one frame. Parameters as <see cref="TranslateGizmo.Update"/>.</summary>
    public void Update(Vector3 pivot, Func<Vector3, Vector2?> toScreen, Func<Vector2, (Vector3 Origin, Vector3 Dir)?> screenRay,
                       Vector2 mouse, bool mouseAllowed, bool pressed, bool down, bool background)
    {
        Started = Ended = false;
        Frame = ImGui.GetFrameCount();
        var centre = Active != Handle.None ? dragPivot : pivot;

        if (toScreen(centre) is not { } sc) { CancelHover(); return; }
        float len = Active != Handle.None ? dragLen : ModelLengthForPixels(centre, sc, toScreen);
        if (len <= 0f) { CancelHover(); return; }

        // Toward the viewer, at the pivot.
        var toViewer = screenRay(sc) is { } r && r.Dir.LengthSquared() > 1e-12f ? -Vector3.Normalize(r.Dir) : Vector3.UnitZ;

        if (Active != Handle.None)
        {
            if (!down)
            {
                Ended = true;
                Active = Handle.None;
            }
            else
            {
                Angle = Vector2.Dot(mouse - pressMouse, grabTangent) / pixelsPerRadian;
                Transform = About(dragPivot, dragAxis, Angle);
            }
        }
        else
        {
            Hovered = mouseAllowed ? HitTest(centre, len, mouse, toViewer, toScreen, out _) : Handle.None;
            if (pressed && Hovered != Handle.None)
            {
                HitTest(centre, len, mouse, toViewer, toScreen, out float theta);
                var axis = AxisOf(Hovered, toViewer);
                float radius = RadiusOf(Hovered, len);
                // The ring's direction on screen where it was grabbed, and how many pixels one radian covers there.
                const float Step = 0.05f;
                if (toScreen(RingPoint(centre, axis, radius, theta + Step)) is { } ahead
                    && toScreen(RingPoint(centre, axis, radius, theta - Step)) is { } behind)
                {
                    var along = ahead - behind;
                    float pixels = along.Length();
                    Active = Hovered;
                    dragPivot = centre;
                    dragAxis = axis;
                    dragLen = len;
                    grabTheta = theta;
                    pressMouse = mouse;
                    grabTangent = pixels > 1e-3f ? along / pixels : Vector2.UnitX;
                    // Floored: at a ring's edge-on extreme the tangent shrinks to nothing, and a tiny divisor would
                    // turn a pixel of drag into a full spin.
                    pixelsPerRadian = MathF.Max(pixels / (2f * Step), ProteusStyle.S(RingPixels) * 0.25f);
                    Angle = 0f;
                    Transform = Matrix4x4.Identity;
                    Started = true;
                }
            }
        }

        Draw(background, centre, len, toViewer, toScreen);
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

    // ── geometry ────────────────────────────────────────────────────────────

    /// <summary>A turn of <paramref name="angle"/> radians about <paramref name="axis"/> through <paramref name="pivot"/>.</summary>
    internal static Matrix4x4 About(Vector3 pivot, Vector3 axis, float angle)
        => Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateFromAxisAngle(axis, angle) * Matrix4x4.CreateTranslation(pivot);

    /// <summary>
    /// The point at <paramref name="theta"/> on the ring of <paramref name="radius"/> about <paramref name="axis"/>
    /// through <paramref name="centre"/>. Parameterised so a turn of +α about the axis carries θ to θ+α.
    /// </summary>
    internal static Vector3 RingPoint(Vector3 centre, Vector3 axis, float radius, float theta)
    {
        var (u, v) = Basis(axis);
        return centre + (u * MathF.Cos(theta) + v * MathF.Sin(theta)) * radius;
    }

    /// <summary>Two unit vectors across <paramref name="axis"/>, with v = axis × u, so turning u by +90° about it gives v.</summary>
    internal static (Vector3 U, Vector3 V) Basis(Vector3 axis)
    {
        axis = Vector3.Normalize(axis);
        var seed = MathF.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var u = Vector3.Normalize(seed - axis * Vector3.Dot(seed, axis));
        return (u, Vector3.Cross(axis, u));
    }

    private static Vector3 AxisOf(Handle h, Vector3 toViewer) => h switch
    {
        Handle.X => Vector3.UnitX,
        Handle.Y => Vector3.UnitY,
        Handle.Z => Vector3.UnitZ,
        _ => toViewer,
    };

    private static float RadiusOf(Handle h, float len) => h == Handle.View ? len * ViewRingScale : len;

    /// <summary>How long, in model units, a radius must be to span <see cref="RingPixels"/> on screen.</summary>
    private static float ModelLengthForPixels(Vector3 centre, Vector2 sc, Func<Vector3, Vector2?> toScreen)
    {
        const float Probe = 0.01f;
        float best = 0f;
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            if (toScreen(centre + axis * Probe) is { } s)
                best = MathF.Max(best, Vector2.Distance(s, sc));
        return best > 1e-4f ? Probe * ProteusStyle.S(RingPixels) / best : 0f;
    }

    /// <summary>The ring nearest the mouse within reach, and where along it the mouse is.</summary>
    private static Handle HitTest(Vector3 centre, float len, Vector2 mouse, Vector3 toViewer, Func<Vector3, Vector2?> toScreen,
                                  out float theta)
    {
        theta = 0f;
        var best = Handle.None;
        float bestD = ProteusStyle.S(GrabPixels);
        foreach (var h in new[] { Handle.X, Handle.Y, Handle.Z, Handle.View })
        {
            var axis = AxisOf(h, toViewer);
            float radius = RadiusOf(h, len);
            Vector2? previous = null;
            for (int i = 0; i <= Segments; i++)
            {
                float t = i * MathF.Tau / Segments;
                if (toScreen(RingPoint(centre, axis, radius, t)) is not { } p) { previous = null; continue; }
                if (previous is { } q)
                {
                    float d = TranslateGizmo.SegmentDistance(mouse, q, p);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = h;
                        theta = t - MathF.Tau / Segments * 0.5f;
                    }
                }
                previous = p;
            }
        }
        return best;
    }

    private void Draw(bool background, Vector3 centre, float len, Vector3 toViewer, Func<Vector3, Vector2?> toScreen)
    {
        var dl = background ? ImGui.GetBackgroundDrawList() : ImGui.GetWindowDrawList();
        float scale = ProteusStyle.S(1f);
        Span<Vector2> ring = stackalloc Vector2[Segments + 1];

        foreach (var h in new[] { Handle.View, Handle.X, Handle.Y, Handle.Z })
        {
            if (Active != Handle.None && Active != h) continue;
            var axis = Active == h ? dragAxis : AxisOf(h, toViewer);
            float radius = RadiusOf(h, len);
            bool lit = Hovered == h || Active == h;
            uint colour = (lit ? 0xFF000000u : 0xC0000000u) | ColourOf(h, lit);
            float width = (lit ? 3f : 2f) * scale;

            int count = 0;
            for (int i = 0; i <= Segments; i++)
            {
                if (toScreen(RingPoint(centre, axis, radius, i * MathF.Tau / Segments)) is not { } p)
                {
                    for (int k = 1; k < count; k++) dl.AddLine(ring[k - 1], ring[k], colour, width);
                    count = 0;
                    continue;
                }
                ring[count++] = p;
            }
            for (int k = 1; k < count; k++) dl.AddLine(ring[k - 1], ring[k], colour, width);
        }

        if (toScreen(centre) is not { } sc) return;
        dl.AddCircleFilled(sc, 3.5f * scale, 0xFFFFFFFFu);

        // While turning: a spoke from where the ring was grabbed and one to where that point has turned to, and the angle.
        if (Active != Handle.None)
        {
            float radius = RadiusOf(Active, len);
            if (toScreen(RingPoint(centre, dragAxis, radius, grabTheta)) is { } from
                && toScreen(RingPoint(centre, dragAxis, radius, grabTheta + Angle)) is { } to)
            {
                dl.AddLine(sc, from, 0x80FFFFFFu, 1f * scale);
                dl.AddLine(sc, to, 0xE0FFFFFFu, 1.5f * scale);
                dl.AddText(to + new Vector2(10f, -8f) * scale, 0xE0FFFFFFu, $"{Angle * 180f / MathF.PI:0}°");
            }
        }
    }

    /// <summary>ABGR, without alpha: X red, Y green, Z blue, the view ring white — as in every DCC tool.</summary>
    private static uint ColourOf(Handle h, bool lit) => h switch
    {
        Handle.X => lit ? 0x007878FFu : 0x003C3CE6u,
        Handle.Y => lit ? 0x0090FF90u : 0x0050C850u,
        Handle.Z => lit ? 0x00FFC878u : 0x00E68C3Cu,
        _ => lit ? 0x00FFFFFFu : 0x00C8C8C8u,
    };
}
