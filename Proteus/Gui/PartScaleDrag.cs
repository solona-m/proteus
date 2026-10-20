using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Proteus.Gui;

/// <summary>
/// The Scale tool's handles: three axis handles at the chosen part's centre (X red, Y green, Z blue) that stretch the
/// part along that axis only, and the part itself — or the dot at its centre — which grows it evenly when pressed and
/// dragged right, or shrinks it dragged left. Either way about the part's centre.
/// <para/>
/// A sibling of <see cref="TranslateGizmo"/> and <see cref="RotateGizmo"/>, driven the same way: it works in the
/// MODEL'S OWN SPACE, and the host supplies where a model-space point lands on screen and which model-space ray lies
/// under a pixel, so the model viewer and the character in the game world run the same code. The host also says
/// whether the mouse is over the part, which has no outline of its own to aim at.
/// <para/>
/// Each frame's hit test and drag is <see cref="Step"/>, free of ImGui, which stores everything it measured in
/// <see cref="Last"/>; <see cref="Draw"/> draws only that, so what is drawn is exactly what was hit-tested.
/// </summary>
public sealed class PartScaleDrag
{
    public enum Handle { None, Uniform, X, Y, Z }

    /// <summary>How fast an even drag grows the part per pixel: the factor is e^(pixels × this), so equal drags either
    /// way cancel.</summary>
    internal const float ScalePerPixel = 0.004f;

    internal const float MinScale = 0.05f, MaxScale = 20f;

    /// <summary>Axis handle length on screen, in pixels — shorter than Move's arrows, so the two read as different tools.</summary>
    internal const float HandlePixels = 80f;

    /// <summary>How near a handle the mouse must be to take it, in pixels.</summary>
    internal const float GrabPixels = 7f;

    /// <summary>How near the centre the mouse must be to scale evenly from there, in pixels.</summary>
    internal const float CentrePixels = 9f;

    /// <summary>
    /// The shortest a handle counts as on screen when an axis drag sets its pace, as a share of
    /// <see cref="HandlePixels"/>: a handle seen nearly end-on would otherwise turn a pixel into a jump.
    /// </summary>
    internal const float SpanFloor = 0.25f;

    private const float TipHalf = 5f;

    /// <summary>The handle under the mouse this frame, when not dragging.</summary>
    public Handle Hovered { get; private set; }

    /// <summary>The handle being dragged; <see cref="Handle.None"/> between drags.</summary>
    public Handle Active { get; private set; }

    /// <summary>The mouse belongs to this handle: over a handle or the chosen part, or dragging. The host must not
    /// orbit, pick or paint.</summary>
    public bool Capturing => Hovered != Handle.None || Active != Handle.None;

    /// <summary>A drag began this frame.</summary>
    public bool Started { get; private set; }

    /// <summary>A drag was released this frame; <see cref="Transform"/> still holds its final value.</summary>
    public bool Ended { get; private set; }

    /// <summary>The ImGui frame this was last updated in; <see cref="Started"/> and <see cref="Ended"/> describe that frame only.</summary>
    public int Frame { get; private set; } = -1;

    /// <summary>The drag so far, per axis: (1,1,1) at rest; an axis drag sets one component, an even drag all three.</summary>
    public Vector3 Factors { get; private set; } = Vector3.One;

    /// <summary>The whole drag so far as one model-space transform, about the pivot the drag started at.</summary>
    public Matrix4x4 Transform { get; private set; } = Matrix4x4.Identity;

    /// <summary>One axis handle as measured this frame: whether it can be taken, and where its tip is on screen.</summary>
    internal readonly record struct AxisShown(bool Usable, Vector2? Tip);

    /// <summary>
    /// Everything <see cref="Step"/> measured this frame, which is all <see cref="Draw"/> reads. Not
    /// <see cref="Shown.Valid"/> when the centre could not be shown.
    /// </summary>
    internal readonly record struct Shown(bool Valid, Vector3 Centre, Vector2 Sc, float Len, Vector3 View,
                                          AxisShown X, AxisShown Y, AxisShown Z,
                                          Vector2? ActiveTip, Vector2? GhostTip, Vector2 Mouse);

    /// <summary>What this frame measured — see <see cref="Shown"/>.</summary>
    internal Shown Last { get; private set; }

    private float uiScale = 1f;
    /// <summary>The pivot the current or last drag started at — what <see cref="Transform"/> scales about. Kept after
    /// release, so the frame a drag ends can still read it.</summary>
    public Vector3 DragPivot { get; private set; }

    private Vector2 pressMouse;
    private Vector2 dragDir;
    private float dragSpan;
    private float dragLen;
    private Vector3 dragView;
    private Vector2? ghostTip;

    /// <summary>Hit, drag and draw for one frame. Parameters as <see cref="TranslateGizmo.Update"/>, plus:</summary>
    /// <param name="pivot">The chosen part's centre in model space. Held from the press for the rest of a drag.</param>
    /// <param name="overPart">The mouse is over the chosen part.</param>
    public void Update(Vector3 pivot, Func<Vector3, Vector2?> toScreen, Func<Vector2, (Vector3 Origin, Vector3 Dir)?> screenRay,
                       Vector2 mouse, bool overPart, bool mouseAllowed, bool pressed, bool down, bool background)
    {
        Frame = ImGui.GetFrameCount();
        Step(pivot, toScreen, screenRay, mouse, overPart, mouseAllowed, pressed, down, ProteusStyle.S(1f));
        Draw(background);
    }

    /// <summary>Drop a drag without finishing it — the surface went away. Reported as ended, so the host records it.</summary>
    public void Release()
    {
        Frame = ImGui.GetFrameCount();
        ReleaseCore();
    }

    internal void ReleaseCore()
    {
        Started = false;
        Ended = Active != Handle.None;
        Active = Handle.None;
        Hovered = Handle.None;
    }

    /// <summary>The frame's hit test and drag, with no ImGui: what <see cref="Update"/> runs before it draws.</summary>
    /// <param name="uiScale">Pixels per design pixel (the user's UI scale).</param>
    internal void Step(Vector3 pivot, Func<Vector3, Vector2?> toScreen, Func<Vector2, (Vector3 Origin, Vector3 Dir)?> screenRay,
                       Vector2 mouse, bool overPart, bool mouseAllowed, bool pressed, bool down, float uiScale)
    {
        this.uiScale = uiScale;
        Started = Ended = false;
        bool dragging = Active != Handle.None;
        var centre = dragging ? DragPivot : pivot;

        if (toScreen(centre) is not { } sc) { CancelHover(); return; }
        float len = dragging ? dragLen : TranslateGizmo.LengthForPixels(centre, sc, toScreen, HandlePixels * uiScale);
        if (len <= 0f) { CancelHover(); return; }

        Vector3 view = dragView;
        if (!dragging)
            view = screenRay(sc) is { } ray && ray.Dir.LengthSquared() > 1e-12f ? Vector3.Normalize(ray.Dir) : Vector3.UnitZ;

        AxisShown Measure(Handle h)
        {
            var tip = toScreen(centre + AxisOf(h) * len);
            return new AxisShown(AxisUsable(h, view, sc, tip), tip);
        }
        var x = Measure(Handle.X);
        var y = Measure(Handle.Y);
        var z = Measure(Handle.Z);
        Vector2? activeTip = null, ghost = null;

        if (dragging)
        {
            if (!down)
            {
                Ended = true;
                Active = Handle.None;
            }
            else
            {
                float pixels = Vector2.Dot(mouse - pressMouse, dragDir);
                float s;
                if (Active == Handle.Uniform) s = ScaleFor(pixels);
                else
                {
                    s = AxisScaleFor(pixels, dragSpan);
                    activeTip = toScreen(centre + AxisOf(Active) * len * s);
                    ghost = ghostTip;
                }
                Factors = FactorsFor(Active, s);
                Transform = Build(DragPivot, Factors);
            }
        }
        else
        {
            var measured = new Shown(true, centre, sc, len, view, x, y, z, null, null, mouse);
            Hovered = mouseAllowed ? HitTest(measured, mouse, overPart, uiScale) : Handle.None;
            if (pressed && Hovered != Handle.None)
            {
                if (Hovered == Handle.Uniform)
                {
                    // Today's even scale: right grows, left shrinks, up and down do nothing.
                    dragDir = Vector2.UnitX;
                    ghostTip = null;
                }
                else
                {
                    var tip = Pick(measured, Hovered).Tip!.Value;
                    var along = tip - sc;
                    float span = along.Length();
                    dragDir = along / span;
                    dragSpan = MathF.Max(span, SpanFloor * HandlePixels * uiScale);
                    ghostTip = tip;
                }
                Active = Hovered;
                Hovered = Handle.None;
                DragPivot = centre;
                dragLen = len;
                dragView = view;
                pressMouse = mouse;
                Factors = Vector3.One;
                Transform = Matrix4x4.Identity;
                Started = true;
                if (Active != Handle.Uniform) { activeTip = ghostTip; ghost = ghostTip; }
            }
        }

        Last = new Shown(true, centre, sc, len, view, x, y, z, activeTip, ghost, mouse);
    }

    private void CancelHover()
    {
        Hovered = Handle.None;
        if (Active != Handle.None) { Active = Handle.None; Ended = true; }
        Last = default;
    }

    /// <summary>
    /// Which handle the mouse is on: the centre first (the smallest target, and even scaling must stay reachable on a
    /// tiny selection), then the nearest usable axis, then the part itself.
    /// </summary>
    internal static Handle HitTest(Shown shown, Vector2 mouse, bool overPart, float uiScale)
    {
        if (!shown.Valid) return Handle.None;
        if (Vector2.Distance(mouse, shown.Sc) <= CentrePixels * uiScale) return Handle.Uniform;

        var best = Handle.None;
        float bestD = GrabPixels * uiScale;
        foreach (var h in new[] { Handle.X, Handle.Y, Handle.Z })
        {
            var axis = Pick(shown, h);
            if (!axis.Usable || axis.Tip is not { } tip) continue;
            float d = TranslateGizmo.SegmentDistance(mouse, shown.Sc, tip);
            if (d < bestD) { bestD = d; best = h; }
        }
        if (best != Handle.None) return best;
        return overPart ? Handle.Uniform : Handle.None;
    }

    private static AxisShown Pick(Shown shown, Handle h) => h switch
    {
        Handle.X => shown.X,
        Handle.Y => shown.Y,
        _ => shown.Z,
    };

    internal static Vector3 AxisOf(Handle h) => h switch
    {
        Handle.X => Vector3.UnitX,
        Handle.Y => Vector3.UnitY,
        Handle.Z => Vector3.UnitZ,
        _ => Vector3.Zero,
    };

    private static TranslateGizmo.Handle MoveHandleOf(Handle h) => h switch
    {
        Handle.X => TranslateGizmo.Handle.X,
        Handle.Y => TranslateGizmo.Handle.Y,
        Handle.Z => TranslateGizmo.Handle.Z,
        _ => TranslateGizmo.Handle.None,
    };

    /// <summary>
    /// Whether an axis handle can be taken from this view: not pointing (nearly) at the camera — the same test, and
    /// the same axes hidden, as Move's arrows — and showing at least a pixel long.
    /// </summary>
    internal static bool AxisUsable(Handle h, Vector3 view, Vector2 sc, Vector2? tip)
        => tip is { } t && Vector2.Distance(t, sc) >= 1f && TranslateGizmo.Usable(MoveHandleOf(h), view);

    /// <summary>The per-axis factors a drag of factor <paramref name="s"/> on this handle makes.</summary>
    internal static Vector3 FactorsFor(Handle h, float s) => h switch
    {
        Handle.X => new Vector3(s, 1f, 1f),
        Handle.Y => new Vector3(1f, s, 1f),
        Handle.Z => new Vector3(1f, 1f, s),
        Handle.Uniform => new Vector3(s),
        _ => Vector3.One,
    };

    /// <summary>
    /// The transform an even drag of <paramref name="pixels"/> makes: a uniform scale about
    /// <paramref name="pivot"/>. Positive pixels are a drag to the right, which grows the part.
    /// </summary>
    internal static Matrix4x4 Build(Vector3 pivot, float pixels)
        => Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateScale(ScaleFor(pixels)) * Matrix4x4.CreateTranslation(pivot);

    /// <summary>A scale by <paramref name="factors"/> along the model's axes, about <paramref name="pivot"/>.</summary>
    internal static Matrix4x4 Build(Vector3 pivot, Vector3 factors)
        => Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateScale(factors) * Matrix4x4.CreateTranslation(pivot);

    internal static float ScaleFor(float pixels) => Math.Clamp(MathF.Exp(pixels * ScalePerPixel), MinScale, MaxScale);

    /// <summary>
    /// An axis drag's factor: <paramref name="pixels"/> along the handle over the handle's on-screen length
    /// <paramref name="span"/>, so the tip starts out under the cursor — and, being a power, can never pass through
    /// zero and mirror the part however far inward the drag goes. It runs ahead of the cursor on a long drag, by a
    /// factor of e for every handle length.
    /// </summary>
    internal static float AxisScaleFor(float pixels, float span)
        => Math.Clamp(MathF.Exp(pixels / MathF.Max(span, 1e-3f)), MinScale, MaxScale);

    private void Draw(bool background)
    {
        var shown = Last;
        if (!shown.Valid) return;
        var dl = background ? ImGui.GetBackgroundDrawList() : ImGui.GetWindowDrawList();
        float scale = uiScale;
        var sc = shown.Sc;

        if (Active == Handle.Uniform)
        {
            dl.AddCircleFilled(sc, 3.5f * scale, 0xFFFFFFFFu);
            dl.AddLine(sc, shown.Mouse, 0x80FFFFFFu, 1f * scale);
            dl.AddText(shown.Mouse + new Vector2(14f, -8f) * scale, 0xE0FFFFFFu, $"{Factors.X * 100f:0}%");
            return;
        }

        if (Active != Handle.None)
        {
            uint rgb = TranslateGizmo.Brighten(TranslateGizmo.ColourOf(MoveHandleOf(Active)));
            if (shown.GhostTip is { } ghost) Tip(dl, ghost, 0x60000000u | rgb, scale);
            if (shown.ActiveTip is { } tip)
            {
                dl.AddLine(sc, tip, 0xFF000000u | rgb, 3.5f * scale);
                Tip(dl, tip, 0xFF000000u | rgb, scale);
            }
            dl.AddCircleFilled(sc, 3.5f * scale, 0xFFFFFFFFu);
            float f = Active switch { Handle.X => Factors.X, Handle.Y => Factors.Y, _ => Factors.Z };
            dl.AddText(shown.Mouse + new Vector2(14f, -8f) * scale, 0xE0FFFFFFu, $"{Active} {f * 100f:0}%");
            return;
        }

        foreach (var h in new[] { Handle.X, Handle.Y, Handle.Z })
        {
            var axis = Pick(shown, h);
            if (!axis.Usable || axis.Tip is not { } tip) continue;
            bool lit = Hovered == h;
            uint rgb = TranslateGizmo.ColourOf(MoveHandleOf(h));
            uint colour = (lit ? 0xFF000000u : 0xD0000000u) | (lit ? TranslateGizmo.Brighten(rgb) : rgb);
            dl.AddLine(sc, tip, colour, (lit ? 3.5f : 2.5f) * scale);
            Tip(dl, tip, colour, scale);
        }

        dl.AddCircleFilled(sc, 3.5f * scale, 0xFFFFFFFFu);
        if (Hovered == Handle.Uniform) dl.AddCircle(sc, CentrePixels * scale, 0xB0FFFFFFu, 24, 1.5f * scale);
    }

    /// <summary>A scale handle's square tip.</summary>
    private static void Tip(ImDrawListPtr dl, Vector2 at, uint colour, float scale)
    {
        var half = new Vector2(TipHalf * scale);
        dl.AddRectFilled(at - half, at + half, colour);
    }
}
