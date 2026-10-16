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

    /// <summary>The notes generation that introduced Body size, hats and design restores.</summary>
    internal const int FirstNotes = 1;

    /// <summary>The generation that introduced the automatic body refit.</summary>
    internal const int AutoRefitNotes = 2;

    /// <summary>
    /// Whether a section introduced in <paramref name="generation"/> is news to someone who has read up to
    /// <paramref name="read"/>. Someone who closed the first notes is shown only what came after them.
    /// </summary>
    internal static bool IsNew(int generation, int read) => read < generation;

    public override void Draw()
    {
        var s = Strings.WhatsNew;
        // Read while open: the close is what moves it, so it stays put for as long as the window is drawn.
        int read = config.WhatsNewShown;

        ImGui.TextWrapped(IsNew(FirstNotes, read) ? s.IntroAll : s.IntroSince);
        ImGui.Spacing();

        if (IsNew(AutoRefitNotes, read)) Section(s.AutoRefitHead, s.AutoRefitBody);
        if (IsNew(FirstNotes, read))
        {
            Section(s.UpscalesHead, s.UpscalesBody);
            Section(s.HatsHead, s.HatsBody);
            Section(s.DesignsHead, s.DesignsBody);
        }

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
