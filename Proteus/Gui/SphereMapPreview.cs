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

    // Index 0 means "no sphere map"; showing atlas slice 0 (a real sphere) for it is misleading, so we
    // draw a dedicated "none" image instead. Optional — drop Resources/none.png and add it as an
    // EmbeddedResource to enable it; without the resource this is null and index 0 falls back to slice 0.
    private IDalamudTextureWrap? none;
    private bool noneTried;

    public SphereMapPreview(ITextureProvider textures, IPluginLog log)
    {
        this.textures = textures;
        this.log = log;
    }

    private IDalamudTextureWrap? None()
    {
        if (none != null || noneTried) return none;
        noneTried = true;
        try
        {
            using var s = typeof(SphereMapPreview).Assembly.GetManifestResourceStream("Proteus.Resources.none.png");
            if (s == null) return null;   // not shipped yet — index 0 just shows slice 0
            var img = ImageResult.FromStream(s, ColorComponents.RedGreenBlueAlpha);
            none = textures.CreateFromRaw(RawImageSpecification.Rgba32(img.Width, img.Height), img.Data);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] failed to load the sphere 'none' image");
        }
        return none;
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

    /// <summary>Draw slice <paramref name="index"/> at the given size. Index 0 shows the "none" image when
    /// available. Falls back to a blank box.</summary>
    public void Draw(int index, float size)
    {
        if (index == 0 && None() is { } n)
        {
            Dalamud.Bindings.ImGui.ImGui.Image(n.Handle, new Vector2(size, size));
            return;
        }

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
        using var scope = Dalamud.Interface.Utility.Raii.ImRaii.PushId(id);

        if (index == 0 && None() is { } n)
            return Dalamud.Bindings.ImGui.ImGui.ImageButton(n.Handle, new Vector2(size, size), Vector2.Zero, Vector2.One);

        var a = Atlas();
        if (a == null)
        {
            Dalamud.Bindings.ImGui.ImGui.Dummy(new Vector2(size, size));
            return false;
        }

        var (uv0, uv1) = Uv(ref index);
        // This binding's ImageButton has no string-id overload; scope it with an ID instead.
        return Dalamud.Bindings.ImGui.ImGui.ImageButton(a.Handle, new Vector2(size, size), uv0, uv1);
    }

    /// <summary>Draw the shared "none" icon at the given size — reused by other pickers' None entry.
    /// Returns false (drawing nothing) when the image isn't available.</summary>
    public bool DrawNone(float size)
    {
        var n = None();
        if (n == null) return false;
        Dalamud.Bindings.ImGui.ImGui.Image(n.Handle, new Vector2(size, size));
        return true;
    }

    /// <summary>Clickable "none" icon. <paramref name="available"/> reports whether an image was drawn (so
    /// the caller knows whether to <c>SameLine</c>); returns true when clicked.</summary>
    public bool DrawNoneButton(string id, float size, out bool available)
    {
        var n = None();
        available = n != null;
        if (n == null) return false;
        using var scope = Dalamud.Interface.Utility.Raii.ImRaii.PushId(id);
        return Dalamud.Bindings.ImGui.ImGui.ImageButton(n.Handle, new Vector2(size, size), Vector2.Zero, Vector2.One);
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
        none?.Dispose();
        none = null;
    }
}
