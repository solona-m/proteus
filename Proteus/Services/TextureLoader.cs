using System;
using System.IO;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using StbImageSharp;
using StbImageWriteSharp;

namespace Proteus.Services;

/// <summary>
/// Loads textures from disk (.tex via Lumina, .png via StbImageSharp) as raw RGBA byte arrays,
/// and extracts texture game paths from .mtrl files.
/// </summary>
public class TextureLoader
{
    private readonly IPluginLog log;

    // Standard FFXIV material sampler CRCs (CRC32/IEEE of the sampler name string)
    private const uint SamplerIdDiffuse = 0x1E6FEF9Cu;
    private const uint SamplerIdNormal  = 0x0C5EC1F1u;
    private const uint SamplerIdMask    = 0x8A4E82B6u;

    public TextureLoader(IPluginLog log) => this.log = log;

    /// <summary>
    /// Parse an on-disk .mtrl file and return the game paths of its diffuse, normal, and mask textures.
    /// Any channel not found in the material will be null.
    /// </summary>
    public MtrlTexturePaths ResolveMtrlTextures(string mtrlDiskPath)
    {
        try
        {
            var mtrl = Lumina.Data.Files.MtrlFile.LoadFromFile(mtrlDiskPath);
            if (mtrl == null) return new MtrlTexturePaths(null, null, null);

            string? diffuse = null, normal = null, mask = null;

            foreach (var sampler in mtrl.Samplers)
            {
                var texIndex = sampler.TextureIndex;
                if (texIndex >= mtrl.Textures.Length) continue;
                var texPath = mtrl.Textures[texIndex].Path;
                if (string.IsNullOrEmpty(texPath)) continue;

                if (sampler.SamplerId == SamplerIdDiffuse) diffuse = NormalizePath(texPath);
                else if (sampler.SamplerId == SamplerIdNormal) normal = NormalizePath(texPath);
                else if (sampler.SamplerId == SamplerIdMask)   mask   = NormalizePath(texPath);
            }

            return new MtrlTexturePaths(diffuse, normal, mask);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to parse mtrl: {0}", mtrlDiskPath);
            return new MtrlTexturePaths(null, null, null);
        }
    }

    // Strip the leading '--' FFXIV texture prefix if present
    private static string NormalizePath(string path)
        => path.StartsWith("--", StringComparison.Ordinal) ? path[2..] : path;

    /// <summary>
    /// Load an on-disk .tex file (FFXIV format) as an RGBA8 byte array.
    /// Returns null on failure.
    /// </summary>
    public (byte[] rgba, int width, int height)? LoadTexAsRgba(string diskPath)
    {
        try
        {
            var tex = Lumina.Data.Files.TexFile.LoadFromFile(diskPath);
            if (tex == null) return null;

            int w = tex.Header.Width;
            int h = tex.Header.Height;
            var rgba = tex.GetRgbaImageData();
            if (rgba == null || rgba.Length == 0) return null;

            return (rgba, w, h);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load .tex: {0}", diskPath);
            return null;
        }
    }

    /// <summary>
    /// Load a PNG from disk, scale/crop to (targetW × targetH), return as RGBA8.
    /// Returns null on failure.
    /// </summary>
    public byte[]? LoadPngAsRgba(string pngPath, int targetW, int targetH)
    {
        try
        {
            using var stream = File.OpenRead(pngPath);
            var img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

            if (img.Width == targetW && img.Height == targetH)
                return img.Data;

            return ScaleNearest(img.Data, img.Width, img.Height, targetW, targetH);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load PNG: {0}", pngPath);
            return null;
        }
    }

    /// <summary>Write an RGBA8 buffer as a PNG to disk.</summary>
    public bool WritePng(byte[] rgba, int width, int height, string outputPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var stream = File.Create(outputPath);
            var writer = new ImageWriter();
            writer.WritePng(rgba, width, height, ColorComponents.RedGreenBlueAlpha, stream);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to write PNG: {0}", outputPath);
            return false;
        }
    }

    // Nearest-neighbour scale — overlay PNGs should already be correct size,
    // but this prevents a crash if they're slightly off.
    private static byte[] ScaleNearest(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (int dy = 0; dy < dh; dy++)
        {
            int sy = dy * sh / dh;
            for (int dx = 0; dx < dw; dx++)
            {
                int sx = dx * sw / dw;
                int si = (sy * sw + sx) * 4;
                int di = (dy * dw + dx) * 4;
                dst[di]     = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }
        return dst;
    }
}
