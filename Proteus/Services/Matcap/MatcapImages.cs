using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services.Matcap;

/// <summary>One of the user's own matcap images.</summary>
/// <param name="Name">The file name — what a record stores.</param>
/// <param name="Path">Where it is on disk.</param>
/// <param name="FromMod">It is the open mod's own copy (<c>&lt;mod&gt;\Proteus\Matcaps</c>), not the global library's.</param>
public sealed record MatcapImage(string Name, string Path, bool FromMod);

/// <summary>
/// The user's own matcap images — listed the way the glow effects are (the mod's folder first, then the global library,
/// one entry per file name, the mod's copy winning) — and baked into a texture of their own.
/// </summary>
public static class MatcapImages
{
    /// <summary>The largest side a baked matcap gets. Matcaps are sampled per pixel by direction; 1024 is ample.</summary>
    public const int MaxSide = 1024;

    /// <summary>The image types listed (the effects list without layered and animated formats).</summary>
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds", ".tex"];

    /// <summary>The mod's own image folder: <c>&lt;mod&gt;\Proteus\Matcaps</c>.</summary>
    public static string ModFolder(string modRoot) =>
        System.IO.Path.Combine(modRoot, SidecarDiscoveryService.ManagedModDir, SidecarDiscoveryService.MatcapsSubdir);

    /// <summary>Images from <paramref name="modFolder"/> then <paramref name="globalFolder"/>, deduped by name.</summary>
    public static List<MatcapImage> List(string? globalFolder, string? modFolder)
    {
        var result = new List<MatcapImage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Scan(string? dir, bool fromMod)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Extensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
                    var name = System.IO.Path.GetFileName(f);
                    if (seen.Add(name)) result.Add(new MatcapImage(name, f, fromMod));
                }
            }
            catch { }
        }
        Scan(modFolder, true);
        Scan(globalFolder, false);
        return result;
    }

    /// <summary>The image by name, from the same lookup <see cref="List"/> makes, or null.</summary>
    public static MatcapImage? Find(string name, string? globalFolder, string? modFolder) =>
        List(globalFolder, modFolder).FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The side a <paramref name="width"/>×<paramref name="height"/> image is baked at, or null with a reason: matcaps are
    /// square by nature (a crop or stretch would silently change the look), and are resampled down to the largest power
    /// of two that fits, at most <see cref="MaxSide"/>.
    /// </summary>
    public static int? BakeSide(int width, int height, out string refusal)
    {
        refusal = "";
        if (width <= 0 || height <= 0) { refusal = "unreadable"; return null; }
        if (width != height) { refusal = "not-square"; return null; }
        int side = Math.Min(width, MaxSide);
        int p = 1;
        while (p * 2 <= side) p *= 2;
        if (p < 4) { refusal = "unreadable"; return null; }
        return p;
    }

    /// <summary>The result of baking an image: the .tex written (to a temp file), or why not.</summary>
    public sealed record Baked(string? TempTex, int Side, string? Refusal);

    /// <summary>
    /// Bake <paramref name="imagePath"/> into a .tex at <paramref name="tempTex"/>: square check on the image's own size,
    /// resample to <see cref="BakeSide"/>, BC7 when <paramref name="compress"/>. Heavy — call on a worker.
    /// </summary>
    public static Baked Bake(TextureLoader loader, string imagePath, string tempTex, bool compress)
    {
        var own = loader.LoadImageAsRgba(imagePath);
        if (own == null) return new Baked(null, 0, "unreadable");
        if (BakeSide(own.Value.width, own.Value.height, out var why) is not { } side) return new Baked(null, 0, why);
        // A .tex already at the baked size (one baked before, on a worker) is used as it is: no second encode.
        if (side == own.Value.width && imagePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tempTex)!);
                File.Copy(imagePath, tempTex, overwrite: true);
                return new Baked(tempTex, side, null);
            }
            catch { /* fall through to a fresh encode */ }
        }
        var rgba = side == own.Value.width ? own.Value.rgba : loader.LoadPngAsRgba(imagePath, side, side);
        if (rgba == null) return new Baked(null, 0, "unreadable");
        return loader.WriteTex(rgba, side, side, tempTex, compress ? TexEncoding.Bc7 : TexEncoding.Uncompressed)
            ? new Baked(tempTex, side, null)
            : new Baked(null, 0, "unreadable");
    }
}
