using System;
using System.IO;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using StbImageSharp;

namespace Proteus.Gui;

/// <summary>
/// Thumbnails for the game's sphere maps, so the picker shows what each index actually reflects.
///
/// The game keeps them in an ARRAY texture (chara/common/texture/sphere_d_array.tex). Lumina cannot
/// decode array textures at all — it throws — and Penumbra only manages it by slicing the LIVE GPU
/// texture through native interop (CharacterUtility + D3D11 views). Rather than take on that interop,
/// the 32 slices are extracted offline and shipped as a single 64x2048 vertical strip; each slice is
/// then drawn straight from it with UV sub-rects, so we upload one texture, not thirty-two.
/// </summary>
public sealed class SphereMapPreview : IDisposable
{
    public const int Count = 32;

    private readonly ITextureProvider textures;
    private readonly IPluginLog log;

    private IDalamudTextureWrap? atlas;
    private bool tried;

    public SphereMapPreview(ITextureProvider textures, IPluginLog log)
    {
        this.textures = textures;
        this.log = log;
    }

    private IDalamudTextureWrap? Atlas()
    {
        if (atlas != null || tried) return atlas;
        tried = true;

        try
        {
            using var s = typeof(SphereMapPreview).Assembly
                .GetManifestResourceStream("Proteus.Resources.sphere_d_array.png");
            if (s == null) { log.Warning("[Proteus] sphere map atlas resource missing"); return null; }

            var img = ImageResult.FromStream(s, ColorComponents.RedGreenBlueAlpha);
            atlas = textures.CreateFromRaw(
                RawImageSpecification.Rgba32(img.Width, img.Height), img.Data);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] failed to load the sphere map atlas");
        }
        return atlas;
    }

    /// <summary>Draw slice <paramref name="index"/> at the given size. Falls back to a blank box.</summary>
    public void Draw(int index, float size)
    {
        var a = Atlas();
        if (a == null)
        {
            Dalamud.Bindings.ImGui.ImGui.Dummy(new Vector2(size, size));
            return;
        }

        var (uv0, uv1) = Uv(ref index);
        Dalamud.Bindings.ImGui.ImGui.Image(a.Handle, new Vector2(size, size), uv0, uv1);
    }

    /// <summary>Same, but clickable — the picture itself selects the slice.</summary>
    public bool DrawButton(string id, int index, float size)
    {
        var a = Atlas();
        if (a == null)
        {
            Dalamud.Bindings.ImGui.ImGui.Dummy(new Vector2(size, size));
            return false;
        }

        var (uv0, uv1) = Uv(ref index);
        // This binding's ImageButton has no string-id overload; scope it with an ID instead.
        using var scope = Dalamud.Interface.Utility.Raii.ImRaii.PushId(id);
        return Dalamud.Bindings.ImGui.ImGui.ImageButton(a.Handle, new Vector2(size, size), uv0, uv1);
    }

    private static (Vector2 Uv0, Vector2 Uv1) Uv(ref int index)
    {
        index = Math.Clamp(index, 0, Count - 1);
        return (new Vector2(0, index / (float)Count), new Vector2(1, (index + 1) / (float)Count));
    }

    public void Dispose()
    {
        atlas?.Dispose();
        atlas = null;
    }
}
