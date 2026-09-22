using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace Proteus.Gui;

/// <summary>
/// The colour tokens every hand-drawn surface reads. Brand values are fixed; surfaces are derived from the user's
/// WindowBg each call, so they follow the Dalamud theme. Everything is a property, so a theme editor can override a
/// token later without touching a call site.
/// </summary>
public static class ProteusTheme
{
    // ---- brand -------------------------------------------------------------------------------------

    /// <summary>#F27712, sampled from the logo art.</summary>
    public static Vector4 Accent { get; } = new(0.949f, 0.467f, 0.071f, 1f);

    /// <summary>A darker, redder partner to <see cref="Accent"/>, for the second ambient glow.</summary>
    public static Vector4 Ember { get; } = new(0.72f, 0.18f, 0.06f, 1f);

    // ---- surfaces ----------------------------------------------------------------------------------

    /// <summary>The window background, opaque: what every surface is mixed from.</summary>
    public static Vector4 Surface0
        => ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.WindowBg)).WithAlpha(1f);

    /// <summary>Raised one step: cards.</summary>
    public static Vector4 Surface1 => Mix(Surface0, White, 0.06f);

    /// <summary>Raised two steps: a hovered card, a nameplate.</summary>
    public static Vector4 Surface2 => Mix(Surface0, White, 0.12f);

    /// <summary>Raised three steps: controls sitting on a raised surface.</summary>
    public static Vector4 Surface3 => Mix(Surface0, White, 0.18f);

    /// <summary>The faint frame around a resting card.</summary>
    public static Vector4 Hairline => Mix(Surface0, White, 0.22f);

    /// <summary>The fill painted behind a <see cref="ProteusStyle.Card"/>; translucent so the ambient glow shows through.</summary>
    public static Vector4 CardFill => Surface1.WithAlpha(0.55f);

    /// <inheritdoc cref="CardFill"/>
    public static Vector4 CardFillHover => Surface2.WithAlpha(0.60f);

    /// <summary>The halo around a hovered card.</summary>
    public static Vector4 Glow => Accent.WithAlpha(0.22f);

    // ---- helpers -----------------------------------------------------------------------------------

    private static readonly Vector4 White = Vector4.One;

    /// <summary>Linear mix of two colours, alpha included.</summary>
    public static Vector4 Mix(Vector4 a, Vector4 b, float t) => Vector4.Lerp(a, b, t);
}
