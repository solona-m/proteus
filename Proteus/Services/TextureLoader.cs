using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BCnEncoder.Encoder;
using CommunityToolkit.HighPerformance;
using Dalamud.Plugin.Services;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Data.Structs;
using StbImageSharp;
using StbImageWriteSharp;

namespace Proteus.Services;

/// <summary>How a baked .tex is encoded on disk. BC5/BC7 map to the FFXIV texture-format codes the decode
/// path already recognises (0x6230 / 0x6432).</summary>
public enum TexEncoding { Uncompressed, Bc5, Bc7 }

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
    // The material's own colour-table row selector (_id). Gear and accessories declare one almost always;
    // body and face skin materials never do.
    private const uint SamplerIdIndex      = 0x565F8FD8u;
    /// <summary>Bytes per entry in a material's sampler table: id, flags, texture index, padding.</summary>
    private const int SamplerStride = 12;

    public TextureLoader(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
        EnsureNativeCompressor(log);
        PreloadImageCodec(log);
    }

    /// <summary>
    /// Resolve StbImageSharp NOW, while the plugin's AssemblyLoadContext is certainly alive.
    /// <para/>
    /// PNG decoding is the first thing that touches this assembly, and it happens deep inside a
    /// background <c>Parallel.ForEach</c> in the compositor. Reloading the plugin while a composite is
    /// still running therefore hit the lazy resolve after Dalamud had begun tearing the ALC down, and the
    /// run died with "AssemblyLoadContext is unloading or was already unloaded" — an alarming red stack
    /// trace for what is really just a cancelled run on a dying instance. Loading it up front means the
    /// type is already resolved and no composite can trigger an assembly load at an unsafe moment.
    /// </summary>
    private static void PreloadImageCodec(IPluginLog log)
    {
        try
        {
            // Decoding a 1x1 PNG forces the assembly load AND the JIT of the decode path itself.
            // Smallest valid PNG: 8-byte signature, IHDR, a single-pixel IDAT, IEND.
            ImageResult.FromMemory(OnePixelPng, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception ex)
        {
            // Never fatal: worst case we are back to the lazy load we had before. Warning, not Debug —
            // if this fails the ALC-unload crash comes back, and a Debug line nobody sees would make the
            // fix look installed when it isn't.
            // Pass ex, not ex.Message: an assembly-load failure carries its real cause one level down
            // (FileLoadException -> "AssemblyLoadContext is unloading"), and the message alone drops it.
            log.Warning(ex, "[Proteus] image codec preload FAILED, plugin-reload crashes may return");
        }
    }

    /// <summary>A 1x1 RGBA PNG: signature, IHDR, one zlib-deflated scanline, IEND. Generated rather than
    /// hand-written — the first attempt had a bad IDAT CRC, which the preload's catch would have swallowed,
    /// leaving the fix inert while looking installed. Covered by ImageCodecPreloadTests.</summary>
    internal static readonly byte[] OnePixelPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41,
        0x54, 0x78, 0xDA, 0x63, 0x60, 0x60, 0x60, 0x60,
        0x00, 0x00, 0x00, 0x05, 0x00, 0x01, 0x7A, 0xA8,
        0x57, 0x50, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45,
        0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    ];

    // ── Native SIMD block compressor (proteus_bcn.dll = bc7enc + rgbcx) ─────────────
    // BC7/BC5 encoding in managed BCnEncoder.Net is scalar C# and painfully slow (a compressed composite
    // could take a minute). The native shim does the same encode with fast, multi-threadable native code
    // (bc7enc modes 1/6, rgbcx BC5). Each call encodes a range of 4x4 block-rows, so we fan out across
    // cores. Falls back to the managed encoder if the DLL is missing or a call ever throws.
    private const string NativeLib = "proteus_bcn";
    private static int _nativeProbed;
    private static volatile bool _nativeAvailable;

    /// <summary>
    /// Which block-compression backend a call would use right now: <c>true</c> native shim, <c>false</c>
    /// managed BCnEncoder. The two encode the same pixels to DIFFERENT bytes, so this belongs in the
    /// content tag that names an output file — otherwise a session that fell back to managed reuses (and
    /// keeps serving) a file the native encoder produced, under a name that claims to describe its bytes.
    /// Both outputs are valid BC7, so this is about keeping the name honest, not about visible corruption.
    /// </summary>
    public static bool NativeEncoderAvailable => _nativeAvailable;

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void proteus_encode_bc7(IntPtr rgba, int width, int height, int blockRowStart, int blockRowCount, IntPtr outPtr);

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void proteus_encode_bc5(IntPtr rgba, int width, int height, int blockRowStart, int blockRowCount, IntPtr outPtr);

    // Load proteus_bcn.dll from the plugin's own directory — Dalamud's assembly load context doesn't add
    // it to the native search path, so a plain DllImport("proteus_bcn") wouldn't find it. Runs once.
    private static void EnsureNativeCompressor(IPluginLog log)
    {
        if (Interlocked.Exchange(ref _nativeProbed, 1) == 1) return;

        // The plugin's real on-disk folder. PluginInterface.AssemblyLocation is Dalamud's authoritative path
        // (Assembly.Location can be empty under the plugin load context); fall back to it only if needed.
        string? dir = null;
        try { dir = Plugin.PluginInterface.AssemblyLocation.DirectoryName; } catch { }
        if (string.IsNullOrEmpty(dir))
            dir = Path.GetDirectoryName(typeof(TextureLoader).Assembly.Location);
        var dll = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, NativeLib + ".dll");

        // Resolver so DllImport("proteus_bcn") at call time finds the DLL by full path.
        NativeLibrary.SetDllImportResolver(typeof(TextureLoader).Assembly, (name, _, _) =>
            (string.Equals(name, NativeLib, StringComparison.OrdinalIgnoreCase) && dll != null
                && File.Exists(dll) && NativeLibrary.TryLoad(dll, out var h)) ? h : IntPtr.Zero);

        // Probe once with the DETAILED loader (NativeLibrary.Load throws with the real reason — missing file
        // vs missing dependency — so a failure is diagnosable in the log instead of a silent fallback).
        try
        {
            if (dll == null || !File.Exists(dll))
            {
                log.Warning("[Proteus] native block compressor not found at \"{0}\" — using managed BCnEncoder", dll ?? "(plugin dir unknown)");
                return;
            }
            var h = NativeLibrary.Load(dll);
            NativeLibrary.Free(h);
            _nativeAvailable = true;
            log.Information("[Proteus] native block compressor: loaded \"{0}\"", dll);
        }
        catch (Exception ex)
        {
            _nativeAvailable = false;
            log.Warning("[Proteus] native block compressor failed to load ({0}) — using managed BCnEncoder", ex.Message);
        }
    }

    // ── Recomposite instrumentation ────────────────────────────────────────────
    // Reset and read by CompositorService around one run; see PhaseCounter.

    // Decode now happens on background prefetch threads as well as the composite thread, so a single
    // summed timer would over-count (it adds concurrent work together) and make every stage derived
    // from it wrong. Split by who is calling: only the composite thread's time is on the critical path.
    [ThreadStatic] private static bool bgPrefetch;

    /// <summary>
    /// Marks the calling thread's decodes as background prefetch. Set inside a prefetch task so its
    /// decode time is reported separately from time the composite actually waited on.
    /// </summary>
    public static bool BackgroundPrefetch
    {
        get => bgPrefetch;
        set => bgPrefetch = value;
    }

    /// <summary>
    /// Elapsed time the COMPOSITE thread spent inside the decode path — decoding, blocking on a decode
    /// a prefetch thread already started, or hitting the cache. This is the real critical-path cost.
    /// </summary>
    public readonly PhaseCounter DecodeWaitStats = new();

    /// <summary>Elapsed decode time on background prefetch threads. Overlapped work, not critical path.</summary>
    public readonly PhaseCounter PrefetchWaitStats = new();

    /// <summary>Time spent actually decoding (cache misses only), summed across all threads. Calls = misses.</summary>
    public readonly PhaseCounter DecodeStats = new();
    /// <summary>Calls served from the decode cache — no time recorded, only the count.</summary>
    public readonly PhaseCounter DecodeHitStats = new();
    /// <summary>The RGBA→BGRA conversion loop in <see cref="WriteTex"/>.</summary>
    public readonly PhaseCounter SwizzleStats = new();
    /// <summary>The .tex disk write itself, with bytes written.</summary>
    public readonly PhaseCounter WriteStats = new();

    public void ResetStats()
    {
        DecodeStats.Reset();
        DecodeHitStats.Reset();
        DecodeWaitStats.Reset();
        PrefetchWaitStats.Reset();
        SwizzleStats.Reset();
        WriteStats.Reset();
        Volatile.Write(ref evictions, 0);
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
    private long lastTouchTick = Environment.TickCount64;
    private int evictions;

    /// <summary>
    /// Ceiling on decoded bytes held. One 4K RGBA entry is 64 MB, so this is really a count: 2 GB ≈ 30
    /// textures.
    /// <para/>
    /// It has to exceed the composite's whole working set or it buys nothing. The access pattern is a
    /// cyclic scan — the same files in the same order every composite — which is the pathological case for
    /// LRU: whatever the next run asks for first was the first evicted. Measured at 1 GB against an 18-file
    /// (~1.2 GB) run, that produced 18 misses on EVERY composite rather than 18 once, and 1420 ms of a
    /// 3244 ms composite. Settable (see Configuration.DecodeCacheBudgetMb) because the right number is a
    /// property of the user's outfit and their RAM, not something to hard-code.
    /// <para/>
    /// LOWERING it reclaims immediately. The trim otherwise only runs off the back of a decode, so someone
    /// who drops the budget because the game is paging would get nothing back until they next composited —
    /// precisely the moment they can least afford to wait for it.
    /// </summary>
    public long DecodeCacheBudgetBytes
    {
        get => Volatile.Read(ref decodeCacheBudgetBytes);
        set
        {
            Volatile.Write(ref decodeCacheBudgetBytes, value);
            TrimCache();   // no-op when raising (the scan early-outs under budget); reclaims when lowering
        }
    }

    private long decodeCacheBudgetBytes = 2048L * 1024 * 1024;

    /// <summary>Entries currently materialized, and the bytes they hold. For the phases log — the number
    /// that says whether the budget actually covers the working set.</summary>
    public (int Entries, long Bytes) CacheState()
    {
        int n = 0; long b = 0;
        foreach (var kv in decodeCache)
            if (kv.Value.IsValueCreated && kv.Value.Value is { } d) { n++; b += d.Rgba.Length; }
        return (n, b);
    }

    /// <summary>Entries dropped by the budget since the last <see cref="ResetStats"/>.</summary>
    public int Evictions => Volatile.Read(ref evictions);

    /// <summary>
    /// Drop everything if nothing has touched the cache for <paramref name="idle"/>. Returns the number of
    /// entries released, 0 when still warm.
    /// <para/>
    /// Without this the plugin holds the full budget for its entire lifetime — the cache is only trimmed
    /// when it EXCEEDS the budget, so it never shrinks on its own. The trade is that the first composite
    /// after a gap pays the decode again; that's the right side of it, because the cache exists for the
    /// burst of recomposites while editing, not for holding gigabytes overnight.
    /// <para/>
    /// Note this makes the arrays collectable, not necessarily returned to the OS: they are Large Object
    /// Heap allocations and the CLR neither compacts nor releases LOH segments by default, so the process's
    /// working set may not drop straight away. Forcing that needs LOH compaction plus a blocking collect,
    /// which is a multi-hundred-millisecond stall and deliberately not done here.
    /// </summary>
    public int ReleaseIfIdle(TimeSpan idle)
    {
        if (Environment.TickCount64 - Volatile.Read(ref lastTouchTick) < idle.TotalMilliseconds) return 0;
        int n = decodeCache.Count;
        if (n == 0) return 0;
        decodeCache.Clear();
        return n;
    }

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
        // Charge the whole call to whoever made it: on the composite thread that covers decoding, blocking
        // on a decode a prefetch thread got to first, and cache lookups — everything the composite waits on.
        var tCall = PhaseCounter.Begin();
        var background = bgPrefetch;
        try
        {
            return GetOrDecodeCore(key, decode);
        }
        finally
        {
            (background ? PrefetchWaitStats : DecodeWaitStats).Stop(tCall);
        }
    }

    private DecodedTex? GetOrDecodeCore(string key, Func<(byte[] rgba, int width, int height)?> decode)
    {
        // Instrumentation: the factory runs only on a miss, so this flag separates real decode cost
        // from cache hits without timing the (near-free) hit path.
        var decoded = false;
        var lazy = decodeCache.GetOrAdd(key, _ => new Lazy<DecodedTex?>(() =>
        {
            decoded = true;
            var t0 = PhaseCounter.Begin();
            var r = decode();
            DecodeStats.Stop(t0);
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

        if (!decoded) DecodeHitStats.Count();

        entry.LastAccess = Interlocked.Increment(ref accessClock);
        Volatile.Write(ref lastTouchTick, Environment.TickCount64);   // keeps ReleaseIfIdle off a live cache
        TrimCache();
        return entry;
    }

    /// <summary>
    /// Drop every cached decode whose source file lives under the given mod directory. The cache keys
    /// files by path + last-write-time + length; a mod reinstall or edit that preserves the timestamp
    /// or keeps the same byte length (common with archive-preserving extractors and same-size re-exports)
    /// would otherwise keep serving the STALE decode until a plugin restart. Called when a mod's enable
    /// state or files change so the next composite re-reads that mod's textures. Returns the count removed.
    /// </summary>
    public int EvictMod(string modDir)
    {
        if (string.IsNullOrEmpty(modDir)) return 0;
        // Disk paths are "<modsRoot>\<modDir>\..." (SidecarRoot and Penumbra-resolved alike), so the mod
        // folder always appears as a bounded path segment. Match with separators on both sides so a mod
        // named "Bone" can't evict "Boney"'s entries.
        var needle = string.Concat(Path.DirectorySeparatorChar, modDir, Path.DirectorySeparatorChar);
        int removed = 0;
        foreach (var key in decodeCache.Keys)   // Keys is a snapshot; safe to remove while iterating
            if (key.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 &&
                decodeCache.TryRemove(key, out _))
                removed++;
        return removed;
    }

    /// <summary>
    /// Drop the entire decode cache — a manual escape hatch for when a texture edit isn't reflected
    /// (e.g. a same-size, timestamp-preserving overwrite the mtime+length key can't distinguish).
    /// Returns the count removed.
    /// </summary>
    public int ClearCache()
    {
        int n = decodeCache.Count;
        decodeCache.Clear();
        return n;
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
            {
                total -= d.Rgba.Length;
                Interlocked.Increment(ref evictions);
            }
        }
    }

    /// <summary>
    /// Texture game paths from a material's RAW bytes — mod redirect first, else the game's own data (see
    /// <see cref="LoadRawFile"/>, which handles that fallback).
    /// <para/>
    /// This is the ONLY way to read a material's texture paths. Two Lumina-typed wrappers used to sit here
    /// (one for disk, one for SqPack) and both were deleted rather than left as a shorter-named trap:
    /// Lumina reports zero samplers for real Dawntrail materials even when it reads their texture table
    /// correctly, so they returned all-null on vanilla materials. That looks exactly like "this material has
    /// no textures", and it silently stopped overlays targeting vanilla materials from compositing at all.
    /// Walking the bytes ourselves sidesteps the typed reader entirely.
    /// </summary>
    public MtrlTexturePaths ResolveMtrlTexturesRaw(string? diskPath, string gamePath)
    {
        try
        {
            var bytes = LoadRawFile(diskPath, gamePath);
            return bytes == null ? new MtrlTexturePaths(null, null, null) : ParseMtrlBytes(bytes);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to parse mtrl bytes: {0}", gamePath);
            return new MtrlTexturePaths(null, null, null);
        }
    }

    /// <summary>
    /// Walk a .mtrl's header → sampler table by hand. Layout (v1.3): header, texture offsets, UV-set and
    /// colour-set offsets, string table, additional data, the data set, then the shader block whose tail is
    /// the sampler array. Each sampler names a texture by index into the offset table.
    /// <para/>
    /// Every step is bounds-checked and bails to "nothing found" rather than throwing — a truncated or
    /// unexpected file must degrade to the caller's fail-open path, not take down the draw loop.
    /// </summary>
    internal static MtrlTexturePaths ParseMtrlBytes(byte[] b)
    {
        const int HeaderSize = 0x10;
        if (b.Length < HeaderSize) return new MtrlTexturePaths(null, null, null);

        ushort dataSetSize     = BitConverter.ToUInt16(b, 0x06);
        ushort stringTableSize = BitConverter.ToUInt16(b, 0x08);
        byte   textureCount    = b[0x0C];
        byte   uvSetCount      = b[0x0D];
        byte   colorSetCount   = b[0x0E];
        byte   additionalSize  = b[0x0F];

        int o = HeaderSize;
        int texTableEnd = o + textureCount * 4;
        if (texTableEnd > b.Length) return new MtrlTexturePaths(null, null, null);
        var texOffsets = new ushort[textureCount];
        for (int i = 0; i < textureCount; i++) texOffsets[i] = BitConverter.ToUInt16(b, o + i * 4);

        o = texTableEnd + uvSetCount * 4 + colorSetCount * 4;
        int stringsAt = o;
        if (stringsAt + stringTableSize > b.Length) return new MtrlTexturePaths(null, null, null);

        o = stringsAt + stringTableSize + additionalSize + dataSetSize;
        if (o + 12 > b.Length) return new MtrlTexturePaths(null, null, null);
        ushort shaderKeyCount = BitConverter.ToUInt16(b, o + 2);
        ushort constantCount  = BitConverter.ToUInt16(b, o + 4);
        ushort samplerCount   = BitConverter.ToUInt16(b, o + 6);

        o += 12 + shaderKeyCount * 8 + constantCount * 8;   // header + flags, then the two preceding tables
        if (o + samplerCount * SamplerStride > b.Length) return new MtrlTexturePaths(null, null, null);

        string? diffuse = null, normal = null, mask = null, index = null;
        for (int i = 0; i < samplerCount; i++)
        {
            int s = o + i * SamplerStride;
            uint samplerId = BitConverter.ToUInt32(b, s);
            byte texIndex  = b[s + 8];
            if (texIndex >= texOffsets.Length) continue;

            int at = stringsAt + texOffsets[texIndex];
            if (at >= stringsAt + stringTableSize) continue;
            int end = at;
            while (end < stringsAt + stringTableSize && b[end] != 0) end++;
            var path = Encoding.UTF8.GetString(b, at, end - at);
            if (path.Length == 0) continue;
            // Strips the marker only when it leads the whole string. In practice it almost never does —
            // real files carry it on the FILE NAME instead (chara/.../texture/--c1401b0001_c_n.tex), and
            // those are left verbatim ON PURPOSE: these paths become the compositor's redirect keys, so
            // rewriting them would change which resource Penumbra is asked to replace. Don't "fix" this to
            // strip mid-path without first confirming in game which form the game actually requests.
            if (path.StartsWith("--", StringComparison.Ordinal)) path = path[2..];

            if      (samplerId is SamplerIdDiffuse or SamplerIdColorMap0) diffuse = path;
            else if (samplerId == SamplerIdNormal)                        normal  = path;
            else if (samplerId == SamplerIdMask)                          mask    = path;
            else if (samplerId == SamplerIdIndex)                         index   = path;
        }
        return new MtrlTexturePaths(diffuse, normal, mask, index);
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
                    0x55354342 => 0x6230u, // "BC5U" → BC5 (unsigned)
                    0x53354342 => 0x6230u, // "BC5S" → BC5 (signed)
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
    // The composite runs at the base skin's native resolution and writes the final textures back at
    // that size, so the base texture dimensions determine the output resolution. To guarantee 4K
    // output, every base texture below 4096 is upscaled (bilinear) to 4096×4096; textures already at
    // 4096+ are left untouched. The cache stays native — upscaling happens on the returned buffer.
    public const int BaseTargetSize = 4096;

    // Clone (for in-place compositing) when already ≥4K, otherwise upscale to 4K. Upscaling produces a
    // fresh buffer, so no separate clone is needed on that path.
    private static (byte[] rgba, int width, int height) CloneOrUpscaleBase(byte[] rgba, int width, int height)
        => width >= BaseTargetSize && height >= BaseTargetSize
            ? ((byte[])rgba.Clone(), width, height)
            : (UVRemapService.ResizeBilinear(rgba, width, height, BaseTargetSize, BaseTargetSize), BaseTargetSize, BaseTargetSize);

    public (byte[] rgba, int width, int height)? LoadBaseTexture(string? diskPath, string gamePath)
    {
        if (diskPath != null && File.Exists(diskPath))
        {
            var key = DiskKey("BD", diskPath);
            if (key != null)
            {
                var hit = GetOrDecode(key, () => LoadTexAsRgba(diskPath));
                // Clone (or upscale to 4K): the caller composites overlays into this buffer in place.
                if (hit != null) return CloneOrUpscaleBase(hit.Rgba, hit.Width, hit.Height);
                // Decode failed — fall through to the game-data fallback below.
            }
            else
            {
                var result = LoadTexAsRgba(diskPath);
                // Fresh, unshared decode — safe to upscale in place of a clone.
                if (result.HasValue) return CloneOrUpscaleBase(result.Value.rgba, result.Value.width, result.Value.height);
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
        return ge == null ? null : CloneOrUpscaleBase(ge.Rgba, ge.Width, ge.Height);
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
        // Extension tolerance: a mod may ship diffuse.png while its metadata references diffuse.dds (or vice
        // versa). If the exact file is absent, resolve a sibling extension (.png/.dds/.tex) — the same fallback
        // masks already use. Central here so every consumer (skin overlays, gear shells, effects) benefits and
        // a wrong extension can't silently fail to load. Only a miss pays the extra lookups.
        if (!File.Exists(path))
        {
            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(path);
            foreach (var ext in new[] { ".png", ".dds", ".tex" })
            {
                var cand = Path.Combine(dir, stem + ext);
                if (File.Exists(cand)) { path = cand; break; }
            }
        }

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
    /// Whether <paramref name="path"/> holds a COMPLETE .tex written by <see cref="WriteTex"/> — its
    /// length is exactly what its own header describes. False for anything missing, truncated, or written
    /// in a format this never emits.
    /// <para/>
    /// The header is self-describing (format at +4, dimensions at +8/+10), so no caller has to know how
    /// many bytes the encode produced. Both block formats are 16 bytes per 4×4 block, and compression is
    /// only ever chosen for 4-aligned dimensions, so the payload is w·h for either of them.
    /// <para/>
    /// Exists because a write interrupted partway — a crash, a full disk, antivirus — leaves a file whose
    /// NAME promises content it does not have. Output names carry a content hash, so an existence check
    /// alone re-approves that file on every composite afterwards: the damage is permanent, silent, and
    /// only clearable by deleting the file by hand. Wrong answers here are cheap in one direction only
    /// (a needless re-encode) and expensive in the other, so anything unrecognised reads as incomplete.
    /// </summary>
    public static bool IsCompleteTex(string path)
    {
        // The FileInfo construction is inside the try, not an expression-bodied hand-off: it throws on an
        // empty or malformed path, and this returns false for "anything missing" — a caller asking about a
        // junk path wants that answer, not an exception from what reads like a predicate.
        try { return IsCompleteTex(new FileInfo(path)); }
        catch { return false; }
    }

    /// <inheritdoc cref="IsCompleteTex(string)"/>
    /// <remarks>
    /// Takes the <see cref="FileInfo"/> so a caller that has already stat'd the file doesn't pay for a
    /// second one — <see cref="FileInfo.Exists"/> and <see cref="FileInfo.Length"/> are served from the
    /// snapshot taken on first access. The header read cannot be avoided in the same way: the only other
    /// route to an expected length is re-deriving it from the source buffer, which means repeating
    /// <see cref="IsSolidColor"/>'s scan over the whole image to find the flat-colour shrink — far more
    /// expensive than reading 16 bytes.
    /// </remarks>
    public static bool IsCompleteTex(FileInfo fi)
    {
        try
        {
            if (!fi.Exists || fi.Length < 80) return false;

            var header = new byte[16];
            using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int read = 0;
                while (read < header.Length)
                {
                    int n = fs.Read(header, read, header.Length - read);
                    if (n <= 0) return false;
                    read += n;
                }
            }

            var format = BitConverter.ToUInt32(header, 4);
            int  w     = BitConverter.ToUInt16(header, 8);
            int  h     = BitConverter.ToUInt16(header, 10);
            if (w <= 0 || h <= 0) return false;

            long payload = format switch
            {
                0x1450u            => (long)w * h * 4,   // B8G8R8A8
                0x6230u or 0x6432u => (long)w * h,       // BC5 / BC7: 16 bytes per 4×4 block
                _                  => -1,
            };
            return payload >= 0 && fi.Length == 80 + payload;
        }
        catch { return false; }
    }

    /// <summary>
    /// Write an RGBA8 buffer as an uncompressed B8G8R8A8 .tex file.
    /// The game and Penumbra accept this format natively without any conversion.
    /// Returns true on success.
    /// </summary>
    public bool WriteTex(byte[] rgba, int width, int height, string outputPath)
        => WriteTex(rgba, width, height, outputPath, TexEncoding.Uncompressed);

    /// <summary>
    /// Write an RGBA8 buffer as a single-surface .tex. Two transforms apply:
    /// <list type="bullet">
    /// <item>Flat-colour shrink (always): a buffer that is one solid colour renders identically at any size
    /// (UVs are normalised, nothing asserts a texture size), so it collapses to a 16×16 square.</item>
    /// <item>Block compression (<paramref name="encoding"/>): BC5 (0x6230) / BC7 (0x6432) instead of the
    /// uncompressed B8G8R8A8 (0x1450). Requires 4-aligned dimensions; falls back to uncompressed otherwise.</item>
    /// </list>
    /// </summary>
    public bool WriteTex(byte[] rgba, int width, int height, string outputPath, TexEncoding encoding)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            // Flat-colour shrink: a 16×16 square of the one colour is enough. It needs no compression.
            if (IsSolidColor(rgba, out byte sr, out byte sg, out byte sb, out byte sa))
            {
                const int n = 16;
                var small = new byte[n * n * 4];
                for (int i = 0; i < small.Length; i += 4)
                {
                    small[i] = sr; small[i + 1] = sg; small[i + 2] = sb; small[i + 3] = sa;
                }
                rgba = small; width = n; height = n; encoding = TexEncoding.Uncompressed;
            }

            // Block-compressed formats are 4×4 blocks — dimensions must be 4-aligned, else write uncompressed.
            if (encoding != TexEncoding.Uncompressed && (width % 4 != 0 || height % 4 != 0))
                encoding = TexEncoding.Uncompressed;

            byte[] payload;
            uint formatCode;
            if (encoding == TexEncoding.Uncompressed)
            {
                // Convert RGBA → BGRA
                var tSwizzle = PhaseCounter.Begin();
                payload = new byte[rgba.Length];
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    payload[i]     = rgba[i + 2]; // B ← R
                    payload[i + 1] = rgba[i + 1]; // G
                    payload[i + 2] = rgba[i];     // R ← B
                    payload[i + 3] = rgba[i + 3]; // A
                }
                SwizzleStats.Stop(tSwizzle);
                formatCode = 0x1450u;             // B8G8R8A8
            }
            else
            {
                payload    = EncodeBlockCompressed(rgba, width, height, encoding);
                formatCode = encoding == TexEncoding.Bc5 ? 0x6230u : 0x6432u;
            }

            // 80-byte TexHeader (StructLayout Explicit, Size=80)
            var header = new byte[80];
            BitConverter.TryWriteBytes(header.AsSpan(0), 0x00800000u);
            BitConverter.TryWriteBytes(header.AsSpan(4), formatCode);
            BitConverter.TryWriteBytes(header.AsSpan(8),  (ushort)width);
            BitConverter.TryWriteBytes(header.AsSpan(10), (ushort)height);
            BitConverter.TryWriteBytes(header.AsSpan(12), (ushort)1);
            header[14] = 1;
            BitConverter.TryWriteBytes(header.AsSpan(28), 80u);

            var tWrite = PhaseCounter.Begin();
            WriteWithRetry(outputPath, stream =>
            {
                stream.Write(header,  0, header.Length);
                stream.Write(payload, 0, payload.Length);
            });
            WriteStats.Stop(tWrite, header.Length + payload.Length);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to write .tex: {0}", outputPath);
            return false;
        }
    }

    /// <summary>True when every pixel in the RGBA buffer is the identical 4-byte colour, which is output.
    /// Early-outs on the first mismatch, so the common non-uniform case is cheap.</summary>
    private static bool IsSolidColor(byte[] rgba, out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = a = 0;
        if (rgba.Length < 8) return false;   // 0 or 1 pixel — nothing to gain
        r = rgba[0]; g = rgba[1]; b = rgba[2]; a = rgba[3];
        for (int i = 4; i + 3 < rgba.Length; i += 4)
            if (rgba[i] != r || rgba[i + 1] != g || rgba[i + 2] != b || rgba[i + 3] != a)
                return false;
        return true;
    }

    /// <summary>Encode an RGBA8 buffer to raw BC5/BC7 blocks (mip 0 only), linear block order — the layout
    /// FFXIV/Lumina expect on read-back. BC5 keeps only R,G; callers pick it only where B/A carry no data.
    /// Uses the native SIMD shim when available; falls back to managed BCnEncoder.Net otherwise.</summary>
    private byte[] EncodeBlockCompressed(byte[] rgba, int width, int height, TexEncoding encoding)
    {
        if (_nativeAvailable)
        {
            try { return EncodeBlockCompressedNative(rgba, width, height, encoding); }
            catch (Exception ex)
            {
                _nativeAvailable = false;   // don't keep retrying a broken native path this session
                log.Warning("[Proteus] native BC encode failed ({0}) — falling back to managed", ex.Message);
            }
        }
        return EncodeBlockCompressedManaged(rgba, width, height, encoding);
    }

    /// <summary>Native encode via proteus_bcn.dll, fanned out across cores by 4x4 block-rows.</summary>
    private static byte[] EncodeBlockCompressedNative(byte[] rgba, int width, int height, TexEncoding encoding)
    {
        // The native code reads width*height*4 bytes of pinned memory with no managed bounds check — an
        // undersized buffer would be an AccessViolation (uncatchable, crashes the game), not the graceful
        // managed fallback. Guard with a MANAGED throw before pinning so the caller's catch absorbs it.
        if ((long)rgba.Length < (long)width * height * 4)
            throw new ArgumentException($"rgba buffer too small: {rgba.Length} < {(long)width * height * 4} for {width}x{height}");

        int bw = width / 4, bh = height / 4;
        var outBuf = new byte[bw * bh * 16];
        var hIn = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        var hOut = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
        try
        {
            long inPtr = hIn.AddrOfPinnedObject().ToInt64();
            long outPtr = hOut.AddrOfPinnedObject().ToInt64();
            bool bc7 = encoding == TexEncoding.Bc7;
            int workers   = Math.Min(Environment.ProcessorCount, 16);
            int chunkRows = Math.Max(1, (bh + workers - 1) / workers);   // block-rows per worker
            int chunks    = (bh + chunkRows - 1) / chunkRows;
            Parallel.For(0, chunks, ci =>
            {
                int start = ci * chunkRows;
                int count = Math.Min(chunkRows, bh - start);
                if (count <= 0) return;
                var rgbaP = new IntPtr(inPtr);
                var chunkO = new IntPtr(outPtr + (long)start * bw * 16);   // this worker's slice of the output
                if (bc7) proteus_encode_bc7(rgbaP, width, height, start, count, chunkO);
                else     proteus_encode_bc5(rgbaP, width, height, start, count, chunkO);
            });
        }
        finally { hIn.Free(); hOut.Free(); }
        return outBuf;
    }

    private static byte[] EncodeBlockCompressedManaged(byte[] rgba, int width, int height, TexEncoding encoding)
    {
        var pixels = new BCnEncoder.Shared.ColorRgba32[width * height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new BCnEncoder.Shared.ColorRgba32(
                rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);

        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality         = CompressionQuality.Fast;
        encoder.OutputOptions.Format          = encoding == TexEncoding.Bc5
            ? BCnEncoder.Shared.CompressionFormat.Bc5
            : BCnEncoder.Shared.CompressionFormat.Bc7;

        var mem = new ReadOnlyMemory2D<BCnEncoder.Shared.ColorRgba32>(pixels, height, width);
        return encoder.EncodeToRawBytes(mem, 0, out _, out _);
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

    // The disk-path variant went with the Lumina-typed mtrl wrappers — nothing loads a typed file straight
    // off disk any more. Callers that want raw bytes use LoadRawFile.
    internal static T? LoadLuminaFileFromBytes<T>(byte[] bytes) where T : FileResource
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

    public byte[]? LoadRawMtrl(string? diskPath, string gamePath) => LoadRawFile(diskPath, gamePath);

    /// <summary>
    /// Raw bytes of any game file (e.g. a .mdl or .mtrl), from the mod redirect if present else the game's
    /// own data. <paramref name="diskPath"/> is Penumbra's resolved path — for an UNMODDED file that is the
    /// game path unchanged, not a real file — so a missing/relative disk path falls through to the game
    /// data. Lets the second skin cut a shell from vanilla (unmodded) body/gear models, which never
    /// resolve to an on-disk file. Reads the raw FileResource — no Lumina parse, so a Dawntrail file whose
    /// typed layout Lumina misreads still comes back byte-exact.
    /// </summary>
    public byte[]? LoadRawFile(string? diskPath, string gamePath)
    {
        if (diskPath != null && File.Exists(diskPath))
        {
            try { return File.ReadAllBytes(diskPath); }
            catch (Exception ex) { log.Error(ex, "Failed to read raw file: {0}", diskPath); }
        }
        try { return dataManager.GetFile(gamePath)?.Data; }
        catch (Exception ex) { log.Error(ex, "Failed to load raw file from game: {0}", gamePath); return null; }
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
