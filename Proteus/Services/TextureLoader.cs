using System;
using System.IO;
using System.Reflection;
using System.Text;
using Dalamud.Plugin.Services;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Data.Structs;
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
    private readonly IDataManager dataManager;

    // Standard FFXIV material sampler CRCs
    private const uint SamplerIdDiffuse    = 0x1E6FEF9Cu;
    private const uint SamplerIdColorMap0  = 0x115306BEu; // Bibo+ / custom body shaders
    private const uint SamplerIdNormal     = 0x0C5EC1F1u;
    private const uint SamplerIdMask       = 0x8A4E82B6u;

    public TextureLoader(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>Parse an on-disk .mtrl file and return the game paths of its textures.</summary>
    public MtrlTexturePaths ResolveMtrlTextures(string mtrlDiskPath)
    {
        try
        {
            var mtrl = LoadLuminaFile<MtrlFile>(mtrlDiskPath);
            if (mtrl == null) return new MtrlTexturePaths(null, null, null);
            return ParseMtrl(mtrl);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to parse mtrl from disk: {0}", mtrlDiskPath);
            return new MtrlTexturePaths(null, null, null);
        }
    }

    /// <summary>Read a .mtrl directly from the game's SqPack and return its texture game paths.</summary>
    public MtrlTexturePaths ResolveMtrlTexturesFromGame(string gamePath)
    {
        try
        {
            var mtrl = dataManager.GetFile<MtrlFile>(gamePath);
            if (mtrl == null) return new MtrlTexturePaths(null, null, null);
            return ParseMtrl(mtrl);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load mtrl from game data: {0}", gamePath);
            return new MtrlTexturePaths(null, null, null);
        }
    }

    private MtrlTexturePaths ParseMtrl(MtrlFile mtrl)
    {
        string? diffuse = null, normal = null, mask = null;
        foreach (var sampler in mtrl.Samplers)
        {
            var texIndex = sampler.TextureIndex;
            if (texIndex >= mtrl.TextureOffsets.Length) continue;
            var path = ReadNullTerminatedString(mtrl.Strings, mtrl.TextureOffsets[texIndex].Offset);
            if (string.IsNullOrEmpty(path)) continue;
            if (path.StartsWith("--", StringComparison.Ordinal)) path = path[2..];
            if      (sampler.SamplerId == SamplerIdDiffuse   || sampler.SamplerId == SamplerIdColorMap0) diffuse = path;
            else if (sampler.SamplerId == SamplerIdNormal)  normal  = path;
            else if (sampler.SamplerId == SamplerIdMask)    mask    = path;
        }
        return new MtrlTexturePaths(diffuse, normal, mask);
    }

    /// <summary>Load an on-disk .tex file as RGBA8. Returns null on failure.</summary>
    public (byte[] rgba, int width, int height)? LoadTexAsRgba(string diskPath)
    {
        try
        {
            var tex = LoadLuminaFile<TexFile>(diskPath);
            if (tex == null) return null;
            return ConvertTex(tex);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load .tex: {0}", diskPath);
            return null;
        }
    }

    /// <summary>
    /// Load a base texture as RGBA8, trying a Penumbra-resolved disk path first,
    /// then falling back to the game's SqPack for vanilla (unmodded) textures.
    /// </summary>
    public (byte[] rgba, int width, int height)? LoadBaseTexture(string? diskPath, string gamePath)
    {
        if (diskPath != null && File.Exists(diskPath))
        {
            var result = LoadTexAsRgba(diskPath);
            if (result.HasValue) return result;
        }
        try
        {
            var tex = dataManager.GetFile<TexFile>(gamePath);
            if (tex == null) return null;
            return ConvertTex(tex);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load base texture from game data: {0}", gamePath);
            return null;
        }
    }

    private static (byte[] rgba, int width, int height) ConvertTex(TexFile tex)
    {
        int w = tex.Header.Width;
        int h = tex.Header.Height;
        var bgra = tex.ImageData;
        var rgba = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            rgba[i]     = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }
        return (rgba, w, h);
    }

    /// <summary>Load a PNG from disk, scale to (targetW × targetH) if needed. Returns null on failure.</summary>
    public byte[]? LoadPngAsRgba(string pngPath, int targetW, int targetH)
    {
        try
        {
            using var stream = File.OpenRead(pngPath);
            var img = ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);

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

    /// <summary>
    /// Write an RGBA8 buffer as an uncompressed B8G8R8A8 .tex file.
    /// The game and Penumbra accept this format natively without any conversion.
    /// Returns true on success.
    /// </summary>
    public bool WriteTex(byte[] rgba, int width, int height, string outputPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            // Convert RGBA → BGRA
            var bgra = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                bgra[i]     = rgba[i + 2]; // B ← R
                bgra[i + 1] = rgba[i + 1]; // G
                bgra[i + 2] = rgba[i];     // R ← B
                bgra[i + 3] = rgba[i + 3]; // A
            }

            // 80-byte TexHeader (StructLayout Explicit, Size=80)
            var header = new byte[80];
            // Offset 0: Attribute = TextureType2D (0x00800000)
            BitConverter.TryWriteBytes(header.AsSpan(0), 0x00800000u);
            // Offset 4: Format = B8G8R8A8 (0x1450)
            BitConverter.TryWriteBytes(header.AsSpan(4), 0x1450u);
            // Offset 8: Width
            BitConverter.TryWriteBytes(header.AsSpan(8),  (ushort)width);
            // Offset 10: Height
            BitConverter.TryWriteBytes(header.AsSpan(10), (ushort)height);
            // Offset 12: Depth = 1
            BitConverter.TryWriteBytes(header.AsSpan(12), (ushort)1);
            // Offset 14: MipLevelsCount = 1
            header[14] = 1;
            // Offset 15: ArraySize = 0
            header[15] = 0;
            // Offset 16: LodOffset[3] = {0, 0, 0} (already zero)
            // Offset 28: OffsetToSurface[13] — first entry = 80 (header size)
            BitConverter.TryWriteBytes(header.AsSpan(28), 80u);
            // remaining OffsetToSurface entries stay zero

            using var stream = File.Create(outputPath);
            stream.Write(header, 0, header.Length);
            stream.Write(bgra, 0, bgra.Length);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to write .tex: {0}", outputPath);
            return false;
        }
    }

    /// <summary>Write an RGBA8 buffer as PNG to disk. Returns true on success.</summary>
    public bool WritePng(byte[] rgba, int width, int height, string outputPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var stream = File.Create(outputPath);
            var writer = new ImageWriter();
            writer.WritePng(rgba, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to write PNG: {0}", outputPath);
            return false;
        }
    }

    // Replicates GameData.GetFileFromDisk<T>() but without needing a GameData instance.
    // Data and Reader have internal setters, so we use reflection to set them.
    private static readonly PropertyInfo PropData   = typeof(FileResource).GetProperty("Data",   BindingFlags.Public | BindingFlags.Instance)!;
    private static readonly PropertyInfo PropReader = typeof(FileResource).GetProperty("Reader", BindingFlags.Public | BindingFlags.Instance)!;

    private static T? LoadLuminaFile<T>(string diskPath) where T : FileResource
    {
        if (!File.Exists(diskPath)) return null;
        var bytes = File.ReadAllBytes(diskPath);
        var file  = Activator.CreateInstance<T>();
        PropData.SetValue(file, bytes);
        PropReader.SetValue(file, new LuminaBinaryReader(bytes, PlatformId.Win32));
        file.LoadFile();
        return file;
    }

    private static string ReadNullTerminatedString(byte[] strings, int offset)
    {
        if (offset >= strings.Length) return string.Empty;
        int end = offset;
        while (end < strings.Length && strings[end] != 0) end++;
        return Encoding.UTF8.GetString(strings, offset, end - offset);
    }

    // Nearest-neighbour scale — prevents crashes if overlay PNG dimensions don't exactly match
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
