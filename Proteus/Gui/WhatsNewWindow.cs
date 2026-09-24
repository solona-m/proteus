using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Proteus.Localization;

namespace Proteus.Gui;

/// <summary>
/// The release notes, shown once. Opened by <see cref="Plugin"/> when the config has not yet recorded the current
/// <see cref="Plugin.CurrentWhatsNew"/> generation, and it is the CLOSE that records it: opening is not reading, and
/// a crash between the two should leave the notes still owed rather than silently spent.
/// </summary>
internal sealed class WhatsNewWindow : Window
{
    private readonly Configuration config;
    private readonly StatusWindow statusWindow;

    /// <summary>
    /// Wide enough that the body paragraphs wrap at a readable measure, and capped so a wide monitor does not
    /// stretch them into single lines. Dalamud's window host scales SizeConstraints itself, so these are NOT
    /// passed through <see cref="ProteusStyle.S(float)"/> — everything drawn inside <see cref="Draw"/> is.
    /// </summary>
    private static readonly WindowSizeConstraints Constraints = new()
    {
        MinimumSize = new Vector2(520f, 200f),
        MaximumSize = new Vector2(640f, 900f),
    };

    public WhatsNewWindow(Configuration config, StatusWindow statusWindow)
        // The stable "###ProteusWhatsNew" id is fused into the localized title (see WhatsNewStrings), so the window
        // keeps its identity, and its position, across a language change.
        : base(Strings.WhatsNew.Title, ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.config = config;
        this.statusWindow = statusWindow;
        SizeConstraints = Constraints;
    }

    public override void Draw()
    {
        var s = Strings.WhatsNew;

        ImGui.TextWrapped(s.Intro);
        ImGui.Spacing();

        Section(s.UpscalesHead, s.UpscalesBody);
        Section(s.HatsHead, s.HatsBody);
        Section(s.DesignsHead, s.DesignsBody);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button(s.Open, ProteusStyle.S(140f, 0f)))
        {
            statusWindow.Show();
            IsOpen = false;   // OnClose records the read
        }

        ImGui.SameLine();
        if (ImGui.Button(s.Close, ProteusStyle.S(140f, 0f)))
            IsOpen = false;
    }

    private static void Section(string heading, string body)
    {
        ImGui.Spacing();
        ProteusStyle.SectionHeader(heading);
        using (ProteusStyle.Card())
            ImGui.TextWrapped(body);
    }

    /// <summary>
    /// Records that the notes have been read. Covers the buttons, the titlebar cross and the Escape hotkey alike,
    /// which is why the flag is not written at the call sites above.
    /// </summary>
    public override void OnClose()
    {
        // Idempotent: Dalamud can raise OnClose more than once, and a Save per close is a needless file write.
        if (!config.WantsWhatsNew(Plugin.CurrentWhatsNew))
            return;

        config.WhatsNewShown = Plugin.CurrentWhatsNew;
        config.Save();
    }
}
