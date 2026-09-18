using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The one-time question: share usage statistics or not. Opened by <see cref="Plugin"/> the first time the
/// status window is open while <see cref="UsageStats.NeedsConsentPrompt"/> holds — never at login, where it
/// would interrupt someone who has not asked for Proteus.
/// <para/>
/// Built to make "no" exactly as easy as "yes": two buttons of the same size and style, neither preselected,
/// and closing the window counts as "no". That is what makes the answer freely given consent.
/// </summary>
internal sealed class UsageConsentWindow : Window
{
    private readonly UsageStats stats;
    private bool answered;

    public UsageConsentWindow(UsageStats stats)
        : base(Strings.Privacy.ConsentTitle + "###ProteusUsageConsent",
               ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings)
    {
        this.stats = stats;
    }

    public override void OnOpen() => answered = false;

    /// <summary>Centred over the game each time it appears; the pivot makes that exact whatever its size.</summary>
    public override void PreDraw()
    {
        var vp = ImGuiHelpers.MainViewport;
        ImGui.SetNextWindowPos(vp.Pos + vp.Size / 2, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    }

    public override void Draw()
    {
        var s = Strings.Privacy;
        // Re-read each frame so a language change while it is open reaches the title too.
        WindowName = s.ConsentTitle + "###ProteusUsageConsent";

        float wrap = ProteusStyle.S(420f);
        ImGui.PushTextWrapPos(wrap);
        ImGui.TextUnformatted(s.ConsentBody);
        ImGui.Spacing();
        ImGui.TextUnformatted(s.ConsentSentHeader);
        ImGui.Indent();
        ImGui.TextUnformatted(s.ConsentSentList);
        ImGui.Unindent();
        ImGui.Spacing();
        ImGui.TextUnformatted(s.ConsentNeverHeader);
        ImGui.Indent();
        ImGui.TextUnformatted(s.ConsentNeverList);
        ImGui.Unindent();
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        if (ImGui.SmallButton(s.ReadNotice))
            OpenNotice();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(UsageStats.PrivacyUrl);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Same size for both, so neither reads as the default.
        var size = new Vector2(ProteusStyle.S(200f), 0);
        if (ImGui.Button(s.ConsentAccept, size))
        {
            answered = true;
            stats.OptIn();
            IsOpen = false;
        }
        ImGui.SameLine();
        if (ImGui.Button(s.ConsentDecline, size))
        {
            answered = true;
            stats.Decline();
            IsOpen = false;
        }
    }

    /// <summary>Closed with the X (or Escape) without an answer: that is a no.</summary>
    public override void OnClose()
    {
        if (!answered) stats.Decline();
    }

    internal static void OpenNotice()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UsageStats.PrivacyUrl) { UseShellExecute = true }); }
        catch { /* no browser — the address is in the tooltip */ }
    }
}
