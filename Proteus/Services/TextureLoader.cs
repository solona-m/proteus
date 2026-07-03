using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
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

    // ── Decode cache ───────────────────────────────────────────────────────────
    // A recomposite re-runs on every colour/design/enable change, but the underlying
    // .tex/.png files almost never change between those triggers — so identical bytes
    // were being BC-decompressed (skin .tex) and PNG-decoded (4K overlays) every run.
    // Cache the decoded RGBA keyed by path + last-write-time + length so each file is
    // decoded once and reused until it actually changes on disk. Bounded by a byte
    // budget with LRU eviction. The Lazy wrapper guarantees a single decode even when
    // several parallel composite tasks request the same file simultaneously.
    //
    // Mutation contract: base textures are composited in place, so LoadBaseTexture
    // hands back a CLONE on a hit; overlay PNGs are treated read-only by every caller
    // (each mutating consumer clones first), so LoadPngAsRgba shares the cached array.
    private sealed class DecodedTex
    {
        public byte[] Rgba = Array.Empty<byte>();
        public int Width;
        public int Height;
        public long LastAccess;
    }

    private readonly ConcurrentDictionary<string, Lazy<DecodedTex?>> decodeCache = new();
    private long accessClock;
    private const long DecodeCacheBudgetBytes = 512L * 1024 * 1024; // 512 MB

    // Cache key for an on-disk file: prefix + path + write-time + length. Returns null
    // (→ bypass the cache) if the file is missing or its metadata can't be read.
    private static string? DiskKey(string prefix, string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            return string.Concat(prefix, "|", path, "|",
                fi.LastWriteTimeUtc.Ticks.ToString(), "|", fi.Length.ToString());
        }
        catch { return null; }
    }

    private DecodedTex? GetOrDecode(string key, Func<(byte[] rgba, int width, int height)?> decode)
    {
        var lazy = decodeCache.GetOrAdd(key, _ => new Lazy<DecodedTex?>(() =>
        {
            var r = decode();
            return r == null
                ? null
                : new DecodedTex { Rgba = r.Value.rgba, Width = r.Value.width, Height = r.Value.height };
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        DecodedTex? entry;
        try { entry = lazy.Value; }
        catch { decodeCache.TryRemove(new KeyValuePair<string, Lazy<DecodedTex?>>(key, lazy)); throw; }

        // Don't keep failed decodes in the cache — let the next call retry.
        if (entry == null)
        {
            decodeCache.TryRemove(new KeyValuePair<string, Lazy<DecodedTex?>>(key, lazy));
            return null;
        }

        entry.LastAccess = Interlocked.Increment(ref accessClock);
        TrimCache();
        return entry;
    }

    // Evict least-recently-accessed materialized entries until under the byte budget.
    // O(n) over the cache, but n is small (tens of entries) so this stays cheap.
    private void TrimCache()
    {
        long total = 0;
        foreach (var kv in decodeCache)
            if (kv.Value.IsValueCreated && kv.Value.Value is { } d)
                total += d.Rgba.Length;
        if (total <= DecodeCacheBudgetBytes) return;

        var live = new List<(string key, Lazy<DecodedTex?> lazy, DecodedTex d)>();
        foreach (var kv in decodeCache)
            if (kv.Value.IsValueCreated && kv.Value.Value is { } d)
                live.Add((kv.Key, kv.Value, d));
        live.Sort((a, b) => a.d.LastAccess.CompareTo(b.d.LastAccess));

        foreach (var (k, lz, d) in live)
        {
            if (total <= DecodeCacheBudgetBytes) break;
            if (decodeCache.TryRemove(new KeyValuePair<string, Lazy<DecodedTex?>>(k, lz)))
                total -= d.Rgba.Length;
        }
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
            if (!File.Exists(diskPath)) return null;
            var bytes = SanitizeTexBytes(File.ReadAllBytes(diskPath));
            var tex = LoadLuminaFileFromBytes<TexFile>(bytes);
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
    /// Load a .dds file as RGBA8. BC-compressed payloads (BC1/2/3/5/7) are wrapped as an in-memory
    /// single-mip .tex and handed to Lumina's decoder — the same path base skin textures use — so no
    /// standalone BC7 decoder is needed. Uncompressed 32-bpp payloads are reordered directly via the
    /// pixel-format channel masks. Returns null on failure or an unsupported format.
    /// </summary>
    public (byte[] rgba, int width, int height)? LoadDdsAsRgba(string diskPath)
    {
        try
        {
            if (!File.Exists(diskPath)) return null;
            return DecodeDds(File.ReadAllBytes(diskPath));
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load .dds: {0}", diskPath);
            return null;
        }
    }

    // DDS layout: 4-byte magic "DDS ", 124-byte DDS_HEADER, then (if fourCC == "DX10") a 20-byte
    // DDS_HEADER_DXT10, then the surface data. Only mip 0 is read. Offsets are into the whole file.
    private (byte[] rgba, int width, int height)? DecodeDds(byte[] dds)
    {
        const uint DdsMagic   = 0x20534444; // "DDS "
        const uint DdpfFourCC = 0x4;
        const uint DdpfRgb    = 0x40;
        if (dds.Length < 128 || BitConverter.ToUInt32(dds, 0) != DdsMagic) return null;

        int  height  = (int)BitConverter.ToUInt32(dds, 12);
        int  width   = (int)BitConverter.ToUInt32(dds, 16);
        uint pfFlags = BitConverter.ToUInt32(dds, 80);
        uint fourCC  = BitConverter.ToUInt32(dds, 84);
        if (width <= 0 || height <= 0) return null;

        int  dataOffset = 128;
        uint luminaFmt  = 0;   // 0 → not a BC format handled via the .tex wrap below

        if ((pfFlags & DdpfFourCC) != 0)
        {
            if (fourCC == 0x30315844) // "DX10" extended header
            {
                if (dds.Length < 148) return null;
                uint dxgi = BitConverter.ToUInt32(dds, 128);
                dataOffset = 148;
                // Uncompressed DXGI formats decode directly; BC formats go through Lumina.
                if (dxgi is 28 or 29)  return DecodeUncompressedDds(dds, dataOffset, width, height, 0x000000ff, 0x0000ff00, 0x00ff0000, 0xff000000); // R8G8B8A8
                if (dxgi == 87)        return DecodeUncompressedDds(dds, dataOffset, width, height, 0x00ff0000, 0x0000ff00, 0x000000ff, 0xff000000); // B8G8R8A8
                luminaFmt = dxgi switch
                {
                    71 or 72 => 0x3420u, // BC1
                    74 or 75 => 0x3430u, // BC2
                    77 or 78 => 0x3431u, // BC3
                    83 or 84 => 0x6230u, // BC5
                    98 or 99 => 0x6432u, // BC7
                    _        => 0u,
                };
            }
            else
            {
                luminaFmt = fourCC switch
                {
                    0x31545844 => 0x3420u, // "DXT1" → BC1
                    0x33545844 => 0x3430u, // "DXT3" → BC2
                    0x35545844 => 0x3431u, // "DXT5" → BC3
                    0x32495441 => 0x6230u, // "ATI2" → BC5
                    _          => 0u,
                };
            }
        }
        else if ((pfFlags & DdpfRgb) != 0 && BitConverter.ToUInt32(dds, 88) == 32)
        {
            // Uncompressed 32-bpp: reorder using the DDS_PIXELFORMAT channel masks.
            uint rMask = BitConverter.ToUInt32(dds, 92);
            uint gMask = BitConverter.ToUInt32(dds, 96);
            uint bMask = BitConverter.ToUInt32(dds, 100);
            uint aMask = BitConverter.ToUInt32(dds, 104);
            return DecodeUncompressedDds(dds, dataOffset, width, height, rMask, gMask, bMask, aMask);
        }

        if (luminaFmt == 0)
        {
            log.Warning("[Proteus] Unsupported .dds format (fourCC=0x{0:X8}, flags=0x{1:X8})", fourCC, pfFlags);
            return null;
        }

        long mip0 = Mip0ByteSize(luminaFmt, width, height);
        if (mip0 <= 0 || dataOffset + mip0 > dds.Length) return null;

        // Wrap mip 0 as a minimal single-surface .tex (80-byte header + block data) so Lumina's
        // format decoder — which already handles every BC format the game ships — does the work.
        var tex = new byte[80 + mip0];
        BitConverter.TryWriteBytes(tex.AsSpan(0),  0x00800000u);   // attribute
        BitConverter.TryWriteBytes(tex.AsSpan(4),  luminaFmt);     // format
        BitConverter.TryWriteBytes(tex.AsSpan(8),  (ushort)width);
        BitConverter.TryWriteBytes(tex.AsSpan(10), (ushort)height);
        BitConverter.TryWriteBytes(tex.AsSpan(12), (ushort)1);     // depth
        tex[14] = 1;                                               // mip count
        BitConverter.TryWriteBytes(tex.AsSpan(28), 80u);           // OffsetToSurface[0]
        Array.Copy(dds, dataOffset, tex, 80, mip0);

        var texFile = LoadLuminaFileFromBytes<TexFile>(tex);
        return texFile == null ? null : ConvertTex(texFile);
    }

    // Reorders an uncompressed 32-bpp DDS surface to RGBA8 using per-channel bit masks. Assumes each
    // mask selects a contiguous 8-bit byte (true for all standard 32-bpp DDS layouts). aMask == 0
    // means no alpha channel → opaque.
    private static (byte[] rgba, int width, int height)? DecodeUncompressedDds(
        byte[] dds, int offset, int width, int height, uint rMask, uint gMask, uint bMask, uint aMask)
    {
        long need = (long)width * height * 4;
        if (offset + need > dds.Length) return null;

        int rSh = MaskShift(rMask), gSh = MaskShift(gMask), bSh = MaskShift(bMask), aSh = MaskShift(aMask);
        var rgba = new byte[need];
        for (int i = 0; i < width * height; i++)
        {
            uint px = BitConverter.ToUInt32(dds, offset + i * 4);
            int o = i * 4;
            rgba[o]     = (byte)((px & rMask) >> rSh);
            rgba[o + 1] = (byte)((px & gMask) >> gSh);
            rgba[o + 2] = (byte)((px & bMask) >> bSh);
            rgba[o + 3] = aMask == 0 ? (byte)255 : (byte)((px & aMask) >> aSh);
        }
        return (rgba, width, height);
    }

    private static int MaskShift(uint mask)
    {
        if (mask == 0) return 0;
        int shift = 0;
        while ((mask & 1) == 0) { mask >>= 1; shift++; }
        return shift;
    }

    /// <summary>
    /// Load a base texture as RGBA8, trying a Penumbra-resolved disk path first,
    /// then falling back to the game's SqPack for vanilla (unmodded) textures.
    /// </summary>
    public (byte[] rgba, int width, int height)? LoadBaseTexture(string? diskPath, string gamePath)
    {
        if (diskPath != null && File.Exists(diskPath))
        {
            var key = DiskKey("BD", diskPath);
            if (key != null)
            {
                var hit = GetOrDecode(key, () => LoadTexAsRgba(diskPath));
                // Clone: the caller composites overlays into this buffer in place.
                if (hit != null) return ((byte[])hit.Rgba.Clone(), hit.Width, hit.Height);
                // Decode failed — fall through to the game-data fallback below.
            }
            else
            {
                var result = LoadTexAsRgba(diskPath);
                if (result.HasValue) return result;
            }
        }

        // Vanilla game data is immutable for the session, so key by game path alone.
        var ge = GetOrDecode("BG|" + gamePath, () =>
        {
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
        });
        return ge == null ? null : ((byte[])ge.Rgba.Clone(), ge.Width, ge.Height);
    }

    private static (byte[] rgba, int width, int height) ConvertTex(TexFile tex)
    {
        int w = tex.Header.Width;
        int h = tex.Header.Height;
        var bgra = tex.TextureBuffer.Filter(mip: 0, z: 0, format: TexFile.TextureFormat.B8G8R8A8).RawData;
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

    /// <summary>
    /// Load an overlay image from disk as RGBA8, scaled to (targetW × targetH) if needed.
    /// Accepts <c>.png</c> (StbImageSharp), <c>.tex</c> (Lumina) and <c>.dds</c> (parsed here). The
    /// two GPU-texture containers decompress any format — including BC7 — to RGBA via Lumina's
    /// decoder, the same path the base skin textures use. Dispatch is by extension because Proteus
    /// mods reference each overlay by its exact file path, so a BC7-packaged mod names its overlays
    /// <c>*.dds</c> (or <c>*.tex</c>). Returns null on failure.
    /// </summary>
    public byte[]? LoadPngAsRgba(string path, int targetW, int targetH)
    {
        bool isTex = path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase);
        bool isDds = path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);

        (byte[] rgba, int width, int height)? Decode()
        {
            try
            {
                if (isTex || isDds)
                {
                    // Decompress the native format (BC7/BC5/uncompressed) to RGBA at its stored size;
                    // scale to the requested target to match the PNG path's contract.
                    var full = isTex ? LoadTexAsRgba(path) : LoadDdsAsRgba(path);
                    if (full == null) return null;
                    var (rgba, sw, sh) = full.Value;
                    var data = (sw == targetW && sh == targetH)
                        ? rgba
                        : ScaleNearest(rgba, sw, sh, targetW, targetH);
                    return (data, targetW, targetH);
                }

                using var stream = File.OpenRead(path);
                var img = ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                var pdata = (img.Width == targetW && img.Height == targetH)
                    ? img.Data
                    : ScaleNearest(img.Data, img.Width, img.Height, targetW, targetH);
                return (pdata, targetW, targetH);
            }
            catch (Exception ex)
            {
                log.Error(ex, "Failed to load overlay image: {0}", path);
                return null;
            }
        }

        // Key includes the target size — the same source is cached separately per scale. Distinct
        // prefixes keep a .tex/.dds/.png that happen to share a stem from colliding in the cache.
        var key = DiskKey(isTex ? "TEXO" : isDds ? "DDSO" : "PNG", path);
        if (key == null) return Decode()?.rgba;

        // Read-only for callers, so the cached array is shared (no clone).
        return GetOrDecode(key + "|" + targetW + "x" + targetH, Decode)?.Rgba;
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
            BitConverter.TryWriteBytes(header.AsSpan(0), 0x00800000u);
            BitConverter.TryWriteBytes(header.AsSpan(4), 0x1450u);
            BitConverter.TryWriteBytes(header.AsSpan(8),  (ushort)width);
            BitConverter.TryWriteBytes(header.AsSpan(10), (ushort)height);
            BitConverter.TryWriteBytes(header.AsSpan(12), (ushort)1);
            header[14] = 1;
            BitConverter.TryWriteBytes(header.AsSpan(28), 80u);

            WriteWithRetry(outputPath, stream =>
            {
                stream.Write(header, 0, header.Length);
                stream.Write(bgra,   0, bgra.Length);
            });
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
        return LoadLuminaFileFromBytes<T>(File.ReadAllBytes(diskPath));
    }

    private static T? LoadLuminaFileFromBytes<T>(byte[] bytes) where T : FileResource
    {
        var file = Activator.CreateInstance<T>();
        PropData.SetValue(file, bytes);
        PropReader.SetValue(file, new LuminaBinaryReader(bytes, PlatformId.Win32));
        file.LoadFile();
        return file;
    }

    // Some mod tools write .tex files with MipCount > 1 but leave the extra OffsetToSurface slots
    // at zero. Lumina computes negative mipmap allocations from those zeroed offsets and passes a
    // negative count to Buffer.BlockCopy, which throws ArgumentException. Pre-patch the header so
    // MipCount only reflects the offsets that are actually populated.
    internal static byte[] SanitizeTexBytes(byte[] bytes)
    {
        if (bytes.Length < 80) return bytes;
        int mipCount = bytes[14] & 0x7F;
        if (mipCount <= 1) return bytes;

        uint prev = BitConverter.ToUInt32(bytes, 28);
        if (prev == 0)
        {
            var p = (byte[])bytes.Clone();
            p[14] = (byte)((p[14] & 0x80) | 1);
            return p;
        }

        // Some TexTools exports write a full MipCount but a bogus OffsetToSurface table whose
        // values are tiny yet still monotonic and in-bounds — e.g. surf0=80, surf1=343 for a
        // 2048×2048 BC7 whose mip 0 alone occupies 4 MB. The monotonic/in-bounds loop below
        // accepts those, so Lumina then reads mip 0 from a 263-byte slice and decodes solid
        // magenta. Guard against it: if the second surface starts before mip 0 could possibly
        // end, the table is unusable — collapse to a single mip so Lumina reads mip 0 from the
        // contiguous block right after the header (verified to decode correctly).
        uint fmt = BitConverter.ToUInt32(bytes, 4);
        int w    = BitConverter.ToUInt16(bytes, 8);
        int h    = BitConverter.ToUInt16(bytes, 10);
        long mip0 = Mip0ByteSize(fmt, w, h);
        uint surf1 = BitConverter.ToUInt32(bytes, 32);
        if (mip0 > 0 && surf1 != 0 && surf1 < prev + mip0)
        {
            var p = (byte[])bytes.Clone();
            p[14] = (byte)((p[14] & 0x80) | 1);
            return p;
        }

        int validMips = 1;
        for (int i = 1; i < Math.Min(mipCount, 13); i++)
        {
            uint cur = BitConverter.ToUInt32(bytes, 28 + i * 4);
            // Reject zero, non-monotonic, OR out-of-bounds offsets.
            // Some mod tools write OffsetToSurface as if the texture were uncompressed
            // (4 bytes/pixel) even for BC formats, making offsets 4× too large and
            // pointing past the end of the compressed file.
            if (cur == 0 || cur <= prev || cur >= (uint)bytes.Length) break;
            prev = cur;
            validMips = i + 1;
        }
        if (validMips == mipCount) return bytes;

        var patched = (byte[])bytes.Clone();
        patched[14] = (byte)((patched[14] & 0x80) | (validMips & 0x7F));
        return patched;
    }

    // Byte size of mip level 0 for a .tex pixel format at the given dimensions.
    // Returns 0 for formats we don't recognise so callers skip the consistency check
    // rather than risk a false positive. Format codes are Lumina TexFile.TextureFormat values.
    internal static long Mip0ByteSize(uint format, int w, int h)
    {
        if (w <= 0 || h <= 0) return 0;
        long px = (long)w * h;
        long blocks = (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4);
        return format switch
        {
            0x1130 or 0x1131                       => px,        // L8 / A8
            0x1440 or 0x1441                       => px * 2,    // B4G4R4A4 / B5G5R5A1
            0x1450 or 0x1451                       => px * 4,    // B8G8R8A8 / B8G8R8X8
            0x2140                                 => px * 2,    // R16F
            0x2150                                 => px * 4,    // R32F
            0x2460                                 => px * 8,    // R16G16B16A16F
            0x2470                                 => px * 16,   // R32G32B32A32F
            0x3420                                 => blocks * 8,  // BC1 (DXT1)
            0x3430 or 0x3431 or 0x6230 or 0x6432   => blocks * 16, // BC2 / BC3 / BC5 / BC7
            _                                      => 0,         // unknown — don't second-guess
        };
    }

    private static string ReadNullTerminatedString(byte[] strings, int offset)
    {
        if (offset >= strings.Length) return string.Empty;
        int end = offset;
        while (end < strings.Length && strings[end] != 0) end++;
        return Encoding.UTF8.GetString(strings, offset, end - offset);
    }

    public byte[]? LoadRawMtrl(string? diskPath, string gamePath)
    {
        if (diskPath != null && File.Exists(diskPath))
        {
            try { return File.ReadAllBytes(diskPath); }
            catch (Exception ex) { log.Error(ex, "Failed to read raw mtrl: {0}", diskPath); }
        }
        try { return dataManager.GetFile<MtrlFile>(gamePath)?.Data; }
        catch (Exception ex) { log.Error(ex, "Failed to load raw mtrl from game: {0}", gamePath); return null; }
    }

    // Returns (patchedBytes, true) when the key was found and the value replaced,
    // or (originalClone, false) when the key was not present in the file.
    public static (byte[] data, bool found) PatchShaderKey(byte[] mtrl, uint key, uint value)
    {
        var result = (byte[])mtrl.Clone();
        Span<byte> kb = stackalloc byte[4];
        BitConverter.TryWriteBytes(kb, key);
        for (int i = 0; i <= result.Length - 8; i++)
        {
            if (result[i] == kb[0] && result[i+1] == kb[1] && result[i+2] == kb[2] && result[i+3] == kb[3])
            {
                BitConverter.TryWriteBytes(result.AsSpan(i + 4), value);
                return (result, true);
            }
        }
        return (result, false);
    }

    // Patches the key in-place if found; otherwise inserts a new ShaderKey entry and updates
    // ShaderKeyCount and FileSize in the MaterialFileHeader.
    // MaterialHeader layout (12 bytes): ShaderValueListSize(2) ShaderKeyCount(2) ConstantCount(2)
    //   SamplerCount(2) Unknown1(2) Unknown2(2) — followed by ShaderKeys[ShaderKeyCount] (8 bytes each).
    public static byte[] EnsureShaderKey(byte[] mtrl, uint category, uint value)
    {
        var (patched, found) = PatchShaderKey(mtrl, category, value);
        if (found) return patched;
        if (mtrl.Length < 16) return patched;

        uint packed      = BitConverter.ToUInt32(mtrl, 4);
        int  dataSetSize = (ushort)(packed >> 16);
        int  strSize     = BitConverter.ToUInt16(mtrl, 8);
        int  texCount    = mtrl[12];
        int  uvCount     = mtrl[13];
        int  colorCount  = mtrl[14];
        int  addlSize    = mtrl[15];

        int matHeaderStart = 16 + texCount * 4 + uvCount * 4 + colorCount * 4 + strSize + addlSize + dataSetSize;
        if (matHeaderStart + 12 > mtrl.Length) return patched;

        int    keyCountOffset = matHeaderStart + 2;  // after ShaderValueListSize(2)
        ushort keyCount       = BitConverter.ToUInt16(mtrl, keyCountOffset);
        int    insertAt       = matHeaderStart + 12 + keyCount * 8;
        if (insertAt > mtrl.Length) return patched;

        var result = new byte[mtrl.Length + 8];
        Array.Copy(mtrl, 0, result, 0, insertAt);
        BitConverter.TryWriteBytes(result.AsSpan(insertAt),     category);
        BitConverter.TryWriteBytes(result.AsSpan(insertAt + 4), value);
        Array.Copy(mtrl, insertAt, result, insertAt + 8, mtrl.Length - insertAt);

        BitConverter.TryWriteBytes(result.AsSpan(keyCountOffset), (ushort)(keyCount + 1));

        // Update FileSize (lower 16 bits of the uint32 at offset 4)
        ushort fileSize = BitConverter.ToUInt16(result, 4);
        BitConverter.TryWriteBytes(result.AsSpan(4), (ushort)(fileSize + 8));

        return result;
    }

    // Shared header parsing used by both constant helpers below.
    private static bool TryParseMtrlHeader(byte[] mtrl,
        out int matHeaderStart, out int svListSize,
        out int keyCount, out int constCount, out int sampCount, out int constBase, out int svBase)
    {
        matHeaderStart = svListSize = keyCount = constCount = sampCount = constBase = svBase = 0;
        if (mtrl.Length < 16) return false;

        uint packed      = BitConverter.ToUInt32(mtrl, 4);
        int  dataSetSize = (ushort)(packed >> 16);
        int  strSize     = BitConverter.ToUInt16(mtrl, 8);
        int  texCount    = mtrl[12];
        int  uvCount     = mtrl[13];
        int  colorCount  = mtrl[14];
        int  addlSize    = mtrl[15];

        matHeaderStart = 16 + texCount * 4 + uvCount * 4 + colorCount * 4 + strSize + addlSize + dataSetSize;
        if (matHeaderStart + 12 > mtrl.Length) return false;

        svListSize = BitConverter.ToUInt16(mtrl, matHeaderStart);
        keyCount   = BitConverter.ToUInt16(mtrl, matHeaderStart + 2);
        constCount = BitConverter.ToUInt16(mtrl, matHeaderStart + 4);
        sampCount  = BitConverter.ToUInt16(mtrl, matHeaderStart + 6);

        constBase = matHeaderStart + 12 + keyCount * 8;
        svBase    = constBase + constCount * 8 + sampCount * 12;
        return svBase <= mtrl.Length;
    }

    // Returns (shaderName, constId[]) for diagnostic logging.
    public static (string shader, uint[] constIds) GetMtrlInfo(byte[] mtrl)
    {
        if (mtrl.Length < 16) return ("?", []);

        int strSize  = BitConverter.ToUInt16(mtrl, 8);
        int shOff    = BitConverter.ToUInt16(mtrl, 10);
        int texCount = mtrl[12], uvCount = mtrl[13], colorCount = mtrl[14];
        int strBase  = 16 + texCount * 4 + uvCount * 4 + colorCount * 4;

        string shaderName = "?";
        if (strBase + shOff < mtrl.Length)
            shaderName = ReadNullTerminatedString(mtrl, strBase + shOff);

        if (!TryParseMtrlHeader(mtrl, out _, out _, out _, out int constCount, out _, out int constBase, out _))
            return (shaderName, []);

        var ids = new uint[constCount];
        for (int i = 0; i < constCount; i++)
        {
            int e = constBase + i * 8;
            if (e + 4 > mtrl.Length) break;
            ids[i] = BitConverter.ToUInt32(mtrl, e);
        }
        return (shaderName, ids);
    }

    // Patches an existing float-array constant's values in-place, found by ID. Writes up to
    // vals.Length floats (4 bytes each) into the constant's ShaderValues slot. Returns
    // (patched, true) when the constant is present with room; otherwise (originalClone, false).
    public static (byte[] data, bool found) PatchConstantValues(byte[] mtrl, uint id, params float[] vals)
    {
        if (!TryParseMtrlHeader(mtrl, out _, out _, out _, out int constCount, out _, out int constBase, out int svBase))
            return ((byte[])mtrl.Clone(), false);

        int need = vals.Length * 4;
        for (int i = 0; i < constCount; i++)
        {
            int e = constBase + i * 8;
            if (e + 8 > mtrl.Length) break;
            if (BitConverter.ToUInt32(mtrl, e) != id) continue;

            int valOffset = BitConverter.ToUInt16(mtrl, e + 4);
            int valCount  = BitConverter.ToUInt16(mtrl, e + 6);
            int byteOff   = svBase + valOffset;
            if (valCount < need || byteOff + need > mtrl.Length) break;

            var result = (byte[])mtrl.Clone();
            for (int k = 0; k < vals.Length; k++)
                BitConverter.TryWriteBytes(result.AsSpan(byteOff + k * 4), vals[k]);
            return (result, true);
        }
        return ((byte[])mtrl.Clone(), false);
    }

    // Patches the float32 emissive-color constant (ID 0x38A64362) if present.
    // Returns (patched, true) on success; (originalClone, false) if not found.
    public static (byte[] data, bool found) PatchEmissiveColorConstant(byte[] mtrl, float r, float g, float b)
    {
        if (!TryParseMtrlHeader(mtrl, out _, out int svListSize,
                out _, out int constCount, out _, out int constBase, out int svBase))
            return ((byte[])mtrl.Clone(), false);

        const uint EmissiveColorId = 0x38A64362u;

        for (int i = 0; i < constCount; i++)
        {
            int e = constBase + i * 8;
            if (e + 8 > mtrl.Length) break;
            if (BitConverter.ToUInt32(mtrl, e) != EmissiveColorId) continue;

            int valOffset = BitConverter.ToUInt16(mtrl, e + 4); // byte offset into ShaderValues
            int valCount  = BitConverter.ToUInt16(mtrl, e + 6); // byte count
            int byteOff   = svBase + valOffset;
            if (valCount < 12 || byteOff + 12 > mtrl.Length) break;

            var result = (byte[])mtrl.Clone();
            BitConverter.TryWriteBytes(result.AsSpan(byteOff),      r);
            BitConverter.TryWriteBytes(result.AsSpan(byteOff + 4),  g);
            BitConverter.TryWriteBytes(result.AsSpan(byteOff + 8),  b);
            return (result, true);
        }
        return ((byte[])mtrl.Clone(), false);
    }

    // Ensures emissive color constant (0x38A64362) is present with the given RGB values.
    // Patches in-place if found; inserts a new constant entry + ShaderValues data if not.
    public static byte[] EnsureEmissiveColorConstant(byte[] mtrl, float r, float g, float b)
    {
        var (patched, found) = PatchEmissiveColorConstant(mtrl, r, g, b);
        if (found) return patched;

        if (!TryParseMtrlHeader(mtrl, out int matHeaderStart, out int svListSize,
                out _, out int constCount, out _, out int constBase, out int svBase))
            return (byte[])mtrl.Clone();
        if (svBase + svListSize > mtrl.Length) return (byte[])mtrl.Clone();

        // Constant entry (8 bytes) goes after existing const entries, before samplers.
        int insertConstAt = constBase + constCount * 8;
        // 3 float values (12 bytes) appended at end of ShaderValues; svBase shifts +8 after const entry insert.
        int appendValAt   = svBase + 8 + svListSize;
        ushort newValOffset = (ushort)svListSize;   // byte offset = current svListSize (values go right after)
        const ushort newValCount = 12;              // 12 bytes = 3 float32 (vec3 RGB, matching skin.shpk)

        var result = new byte[mtrl.Length + 20];  // +8 const entry, +12 float values

        // Copy bytes before insertion point
        Array.Copy(mtrl, 0, result, 0, insertConstAt);

        // Write new constant entry
        BitConverter.TryWriteBytes(result.AsSpan(insertConstAt),     0x38A64362u);
        BitConverter.TryWriteBytes(result.AsSpan(insertConstAt + 4), newValOffset);
        BitConverter.TryWriteBytes(result.AsSpan(insertConstAt + 6), newValCount);

        // Copy samplers + existing ShaderValues (shifted by 8)
        Array.Copy(mtrl, insertConstAt, result, insertConstAt + 8, mtrl.Length - insertConstAt);

        // Append new float values (vec3: R, G, B)
        BitConverter.TryWriteBytes(result.AsSpan(appendValAt),     r);
        BitConverter.TryWriteBytes(result.AsSpan(appendValAt + 4), g);
        BitConverter.TryWriteBytes(result.AsSpan(appendValAt + 8), b);

        // Update MaterialHeader: constCount +1, svListSize +12 (3 floats × 4 bytes)
        BitConverter.TryWriteBytes(result.AsSpan(matHeaderStart + 4), (ushort)(constCount + 1));
        BitConverter.TryWriteBytes(result.AsSpan(matHeaderStart),     (ushort)(svListSize + 12));

        // Update MaterialFileHeader FileSize (lower 16 bits of uint32 at offset 4)
        // +8 const entry + +12 float values = +20 total
        ushort fileSize = BitConverter.ToUInt16(result, 4);
        BitConverter.TryWriteBytes(result.AsSpan(4), (ushort)(fileSize + 20));

        return result;
    }

    // Patches the ColorSetInfo block with per-row emissive intensities from the configured
    // row overrides. Sub-rows with emissive > 0 are written; all others are zeroed.
    // Sub-row layout: index i → pairIdx = i/2 (0-based row), sub-row A when even, B when odd.
    public static byte[] PatchColorTableEmissive(byte[] mtrl, Dictionary<int, ColorTableRowOverride> rows)
    {
        if (mtrl.Length < 16) return (byte[])mtrl.Clone();

        uint packed        = BitConverter.ToUInt32(mtrl, 4);
        ushort dataSetSize = (ushort)(packed >> 16);
        if (dataSetSize == 0) return (byte[])mtrl.Clone();

        int texCount   = mtrl[12];
        int uvCount    = mtrl[13];
        int colorCount = mtrl[14];
        int addlSize   = mtrl[15];
        int strSize    = BitConverter.ToUInt16(mtrl, 8);

        int colorSetOffset = 16 + texCount * 4 + uvCount * 4 + colorCount * 4 + strSize + addlSize;
        if (colorSetOffset + 512 > mtrl.Length) return (byte[])mtrl.Clone();

        var result = (byte[])mtrl.Clone();

        for (int i = 0; i < 32; i++)
        {
            int pairIdx = i / 2;
            bool isB    = (i % 2) == 1;
            float er = 0f, eg = 0f, eb = 0f;
            if (rows.TryGetValue(pairIdx, out var pair))
            {
                var sub = isB ? pair.B : pair.A;
                if (sub.Emissive > 0.001f) { er = sub.DiffuseR; eg = sub.DiffuseG; eb = sub.DiffuseB; }
            }
            int off = colorSetOffset + i * 16;
            BitConverter.TryWriteBytes(result.AsSpan(off + 8),  (ushort)BitConverter.HalfToInt16Bits((Half)er));
            BitConverter.TryWriteBytes(result.AsSpan(off + 10), (ushort)BitConverter.HalfToInt16Bits((Half)eg));
            BitConverter.TryWriteBytes(result.AsSpan(off + 12), (ushort)BitConverter.HalfToInt16Bits((Half)eb));
        }
        return result;
    }

    public bool WriteMtrl(byte[] bytes, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteWithRetry(path, stream => stream.Write(bytes, 0, bytes.Length));
            return true;
        }
        catch (Exception ex) { log.Error(ex, "Failed to write .mtrl: {0}", path); return false; }
    }

    // Writes to a uniquely-named .tmp file in the same directory, then atomically moves it to
    // the final path. Mare Synchronos watches .tex/.mtrl extensions and will hash-lock them
    // immediately on creation; writing to a .tmp first means the final path appears fully-written
    // or not at all, eliminating the file-lock retries that previously added seconds per run.
    private static void WriteWithRetry(string path, Action<FileStream> write, int attempts = 5, int delayMs = 40)
    {
        var tmp = path + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            using (var stream = File.Create(tmp))
                write(stream);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>Nearest-neighbour resize of an RGBA8 buffer.</summary>
    public byte[] ScaleRgba(byte[] src, int sw, int sh, int dw, int dh)
        => ScaleNearest(src, sw, sh, dw, dh);

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
