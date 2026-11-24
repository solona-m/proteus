namespace Proteus.Gui;

/// <summary>
/// The decisions behind a destructive one-click button (an Undo that throws away an edit saved into a mod). Proteus has
/// no confirmation dialogs: such a button is disabled and dimmed until Ctrl or Shift is held. Drawn by
/// <see cref="ProteusStyle.ArmedButton"/>; kept apart so the rules are testable without ImGui.
/// </summary>
internal static class ArmedAction
{
    /// <summary>Armed while Ctrl or Shift is held.</summary>
    public static bool Armed(bool ctrl, bool shift) => ctrl || shift;

    /// <summary>
    /// The hover text. A blocked button with a reason shows that reason alone (holding a key would not help); otherwise
    /// the button's own tip, unchanged, then a blank line and the arm tip; with no tip, the arm tip alone.
    /// <paramref name="blockedTip"/> is passed only while the button is blocked.
    /// </summary>
    public static string Tooltip(string? tip, string? blockedTip, string armTip)
    {
        if (!string.IsNullOrEmpty(blockedTip)) return blockedTip;
        return string.IsNullOrEmpty(tip) ? armTip : tip + "\n\n" + armTip;
    }

    /// <summary>A press counts only while armed and not blocked (a disabled ImGui button already returns false).</summary>
    public static bool Clicked(bool pressed, bool armed, bool blocked) => pressed && armed && !blocked;
}
