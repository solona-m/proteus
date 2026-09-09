using System;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using StbImageSharp;

namespace Proteus.Gui;

/// <summary>
/// Thumbnails for the game's fabric weaves, so the tile picker shows what each slice actually looks like.
///
/// Same shape, and the same reason, as <see cref="SphereMapPreview"/>: the game keeps them in an ARRAY
/// texture (chara/common/texture/tile_norm_array.tex) that Lumina cannot decode — its texture path throws —
/// and Penumbra only manages it by slicing the LIVE GPU texture through native interop. So the 64 slices are
/// extracted offline and shipped as one 64x4096 vertical strip, drawn with UV sub-rects: one upload, not 64.
///
/// The one difference is that the strip is pre-LIT. tile_norm_array is a normal map, and drawn raw it is 64
/// near-identical lavender squares — useless for choosing between. The offline step reconstructs each texel's
/// normal and lights it from a fixed direction, so every slice reads as the embossed weave it is. Baking that
/// in is the whole payoff of shipping a strip rather than decoding the array at runtime.
/// </summary>
public sealed class TilePreview : IDisposable
{
    /// <summary>Slices in the array, per its .tex header. Matches the /64 in the colour row's tile index
    /// encoding — see <see cref="Proteus.Services.GearMaterialWriter"/>.</summary>
    public const int Count = 64;

    private readonly ITextureProvider textures;
    private readonly IPluginLog log;

    private IDalamudTextureWrap? atlas;
    private bool tried;

    public TilePreview(ITextureProvider textures, IPluginLog log)
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
            using var s = typeof(TilePreview).Assembly
                .GetManifestResourceStream("Proteus.Resources.tile_norm_array.png");
            if (s == null) { log.Warning("[Proteus] tile atlas resource missing"); return null; }

            // Shipped as a single grey channel; expanded to RGBA here because that is what the upload wants.
            var img = ImageResult.FromStream(s, ColorComponents.RedGreenBlueAlpha);
            atlas = textures.CreateFromRaw(
                RawImageSpecification.Rgba32(img.Width, img.Height), img.Data);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] failed to load the tile atlas");
        }
        return atlas;
    }

    /// <summary>Draw slice <paramref name="index"/> at the given size. Falls back to a blank box, which is
    /// what leaves the picker usable as plain numbers when the resource can't be loaded.</summary>
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
        using var scope = Dalamud.Interface.Utility.Raii.ImRaii.PushId(id);

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
