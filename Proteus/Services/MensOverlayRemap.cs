using System;

namespace Proteus.Services;

/// <summary>What <see cref="MensOverlayRemap"/> needs of the composite it runs in.</summary>
internal interface IMensRemapHost
{
    UVRemapService UvRemap { get; }

    /// <summary>An overlay image decoded at a given size.</summary>
    byte[]? Load(string path, int w, int h, ResampleFilter filter);

    /// <summary>A result derived from an overlay file, cached by the file, <paramref name="key"/> and size; null is not cached.</summary>
    byte[]? GetOrDerive(string path, string key, int w, int h, Func<byte[]?> make);
}

/// <summary>
/// A woman's overlay brought onto a man's material along a <see cref="MensRoute"/>: decoded at 4096, carried by
/// <see cref="UVRemapService.ConvertSheet"/>, resampled to the material's size with the caller's filter, and cached per
/// file, route, filter and size — so a two-map route is paid once.
/// </summary>
internal static class MensOverlayRemap
{
    /// <summary>The overlay at <paramref name="overlayPath"/> in <paramref name="route"/>'s destination layout at w×h; null when it cannot be.</summary>
    /// <param name="mapFailed">Whether the null came from the MAPS (one missing or unreadable) rather than the overlay
    /// (a file that would not decode): only the former says anything about the route.</param>
    /// <param name="sheet">The maps' size; 4096 for the shipped ones.</param>
    internal static byte[]? Convert(IMensRemapHost host, int w, int h, MensRoute route, string? overlayPath, ResampleFilter filter,
                                    out bool mapFailed, int sheet = 4096)
    {
        mapFailed = false;
        if (overlayPath == null) return null;
        bool failed = false;
        var result = host.GetOrDerive(overlayPath, DeriveKey(route, filter), w, h, () =>
        {
            var native = host.Load(overlayPath, sheet, sheet, filter);
            if (native == null) return null;
            var converted = host.UvRemap.ConvertSheet(native, sheet, sheet, route.Src, route.Dst, sheet);
            if (converted == null) { failed = true; return null; }
            return TextureLoader.Resample(converted.Value.Rgba, converted.Value.W, converted.Value.H, w, h, filter);
        });
        mapFailed = failed;
        return result;
    }

    /// <summary>The cache key of a route's result: distinct from every key the women's paths use (<c>gen2&lt;…</c>).</summary>
    internal static string DeriveKey(MensRoute route, ResampleFilter filter)
        => $"m2:{route.Dst}<{route.Src}|{(filter == ResampleFilter.Nearest ? "n" : "a")}";
}
