using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Proteus.Gui;

/// <summary>
/// The Scale tool's handle: press on the chosen part and drag right to grow it or left to shrink it, about its pivot.
/// <para/>
/// A sibling of <see cref="TranslateGizmo"/> and <see cref="RotateGizmo"/>, driven the same way: it works in the
/// MODEL'S OWN SPACE, and the host supplies where a model-space point lands on screen and which model-space ray lies
/// under a pixel, so the model viewer and the character in the game world run the same code. Unlike arrows or rings
/// there is nothing to aim at but the part itself, so the host also says whether the mouse is over it.
/// </summary>
public sealed class PartScaleDrag
{
    /// <summary>How fast the part grows per pixel: the factor is e^(pixels × this), so equal drags either way cancel.</summary>
    internal const float ScalePerPixel = 0.004f;

    internal const float MinScale = 0.05f, MaxScale = 20f;

    /// <summary>A drag is under way.</summary>
    public bool Active { get; private set; }

    /// <summary>The mouse is over the chosen part and free to act — a press now starts a drag.</summary>
    public bool OverPart { get; private set; }

    /// <summary>The mouse belongs to this handle: over the chosen part, or dragging. The host must not orbit, pick or paint.</summary>
    public bool Capturing => Active || OverPart;

    /// <summary>A drag began this frame.</summary>
    public bool Started { get; private set; }

    /// <summary>A drag was released this frame; <see cref="Transform"/> still holds its final value.</summary>
    public bool Ended { get; private set; }

    /// <summary>The ImGui frame this was last updated in; <see cref="Started"/> and <see cref="Ended"/> describe that frame only.</summary>
    public int Frame { get; private set; } = -1;

    /// <summary>The whole drag so far as one model-space transform, about the pivot the drag started at.</summary>
    public Matrix4x4 Transform { get; private set; } = Matrix4x4.Identity;

    private Vector2 startMouse;
    private Vector3 dragPivot;
    private float dragPixels;

    /// <summary>Hit, drag and draw for one frame. Parameters as <see cref="TranslateGizmo.Update"/>, plus:</summary>
    /// <param name="pivot">The chosen part's centre in model space. Held from the press for the rest of a drag.</param>
    /// <param name="overPart">The mouse is over the chosen part.</param>
    public void Update(Vector3 pivot, Func<Vector3, Vector2?> toScreen, Vector2 mouse, bool overPart, bool mouseAllowed,
                       bool pressed, bool down, bool background)
    {
        Started = Ended = false;
        Frame = ImGui.GetFrameCount();
        OverPart = !Active && mouseAllowed && overPart;

        if (Active)
        {
            if (!down)
            {
                Ended = true;
                Active = false;
            }
            else
            {
                dragPixels = mouse.X - startMouse.X;
                Transform = Build(dragPivot, dragPixels);
            }
        }
        else if (pressed && OverPart)
        {
            dragPivot = pivot;
            startMouse = mouse;
            dragPixels = 0f;
            Transform = Matrix4x4.Identity;
            Active = true;
            OverPart = false;
            Started = true;
        }

        Draw(background, Active ? dragPivot : pivot, mouse, toScreen);
    }

    /// <summary>Drop a drag without finishing it — the surface went away. Reported as ended, so the host records it.</summary>
    public void Release()
    {
        Frame = ImGui.GetFrameCount();
        Started = false;
        Ended = Active;
        Active = false;
        OverPart = false;
    }

    /// <summary>
    /// The transform a horizontal drag of <paramref name="pixels"/> makes: a uniform scale about
    /// <paramref name="pivot"/>. Positive pixels are a drag to the right, which grows the part.
    /// </summary>
    internal static Matrix4x4 Build(Vector3 pivot, float pixels)
        => Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateScale(ScaleFor(pixels)) * Matrix4x4.CreateTranslation(pivot);

    internal static float ScaleFor(float pixels) => Math.Clamp(MathF.Exp(pixels * ScalePerPixel), MinScale, MaxScale);

    private void Draw(bool background, Vector3 pivot, Vector2 mouse, Func<Vector3, Vector2?> toScreen)
    {
        if (!Active && !OverPart) return;
        if (toScreen(pivot) is not { } sc) return;
        var dl = background ? ImGui.GetBackgroundDrawList() : ImGui.GetWindowDrawList();
        float scale = ProteusStyle.S(1f);

        dl.AddCircleFilled(sc, 3.5f * scale, 0xFFFFFFFFu);
        if (!Active)
        {
            dl.AddCircle(sc, 9f * scale, 0xB0FFFFFFu, 24, 1.5f * scale);
            return;
        }

        dl.AddLine(sc, mouse, 0x80FFFFFFu, 1f * scale);
        dl.AddText(mouse + new Vector2(14f, -8f) * scale, 0xE0FFFFFFu, $"{ScaleFor(dragPixels) * 100f:0}%");
    }
}
