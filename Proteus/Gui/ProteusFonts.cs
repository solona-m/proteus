using System;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Plugin;

namespace Proteus.Gui;

/// <summary>
/// The plugin's display typography: the game's own Jupiter, so headings read as part of FFXIV rather
/// than as stock ImGui. Body text deliberately stays on Dalamud's default font — Jupiter is a display
/// face with narrow glyph coverage, and setting whole paragraphs (or anything the user typed) in it
/// would render missing glyphs as boxes.
/// </summary>
public sealed class ProteusFonts : IDisposable
{
    private readonly IFontHandle header;
    private readonly IFontHandle wordmark;

    public ProteusFonts(IDalamudPluginInterface pi)
    {
        // UiBuilder.FontAtlas is the plugin-private atlas and is itself global-scaled, so these sizes are
        // deliberately UNSCALED. Running them through ImGuiHelpers.GlobalScale would apply the user's UI
        // scale twice — a 1.5x user would get headings at 2.25x.
        var atlas = pi.UiBuilder.FontAtlas;
        header   = atlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Jupiter, 17f));
        wordmark = atlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Jupiter, 26f));
    }

    /// <summary>
    /// Section headings. Safe to call before the atlas has finished building — per IFontAtlas, Push works
    /// "regardless of the state of the font handle" and falls back to the default font, so the first frame
    /// or two after load simply render unstyled rather than crashing or needing a readiness gate. Never
    /// block on <see cref="IFontHandle.WaitAsync()"/> to avoid that: the atlas builds ON the framework
    /// thread, so waiting for it from a draw call deadlocks the game.
    /// </summary>
    public IDisposable PushHeader() => header.Push();

    /// <summary>The banner wordmark — the same face, sized for the header band.</summary>
    public IDisposable PushWordmark() => wordmark.Push();

    public void Dispose()
    {
        header.Dispose();
        wordmark.Dispose();
    }
}
