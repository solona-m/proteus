using System;
using System.Numerics;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using ImGui = Dalamud.Bindings.ImGui.ImGui;

namespace Proteus.Gui;

/// <summary>
/// Thumbnails for scroll-effect image files, so the "Glow effect" picker shows what each one looks like
/// (like the sphere-map picker). Dalamud's texture provider decodes and caches each file by path; formats
/// it can't decode (or a file still loading) return no wrap, and the picker falls back to the name.
/// </summary>
public sealed class EffectPreview
{
    private readonly ITextureProvider textures;

    public EffectPreview(ITextureProvider textures) => this.textures = textures;

    private IDalamudTextureWrap? Wrap(string absPath)
    {
        try { return textures.GetFromFileAbsolute(absPath).GetWrapOrDefault(); }
        catch { return null; }
    }

    /// <summary>Draw the effect's thumbnail at the given size, or nothing (a spacer) if unavailable.</summary>
    public bool Draw(string absPath, float size)
    {
        var w = Wrap(absPath);
        if (w == null) return false;
        ImGui.Image(w.Handle, new Vector2(size, size));
        return true;
    }

    /// <summary>Clickable thumbnail — the picture itself selects the effect. False if no thumbnail (caller
    /// draws a text fallback) or if not clicked.</summary>
    public bool DrawButton(string id, string absPath, float size, out bool hasThumb)
    {
        var w = Wrap(absPath);
        hasThumb = w != null;
        if (w == null) return false;
        using var scope = ImRaii.PushId(id);
        return ImGui.ImageButton(w.Handle, new Vector2(size, size), Vector2.Zero, Vector2.One);
    }
}
