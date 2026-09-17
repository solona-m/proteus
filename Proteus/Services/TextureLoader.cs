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
/// How an overlay is resampled when its art is not at the requested size. <see cref="Auto"/> filters and suits
/// continuous channels; <see cref="Nearest"/> is the only correct choice for an index (<c>_id</c>) texture, whose
/// red/green are discrete colour-table row selectors that averaging would corrupt.
/// </summary>
public enum ResampleFilter { Auto, Nearest }

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
    // The material's own colour-table row selector (_id); body and face skin materials never declare one.
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
    /// Resolve StbImageSharp now, while the plugin's AssemblyLoadContext is certainly alive, so a composite running
    /// during a plugin reload can't trigger an assembly load after the ALC starts unloading.
    /// </summary>
    private static void PreloadImageCodec(IPluginLog log)
    {
        try
        {
            // Decoding a 1x1 PNG forces the assembly load and the JIT of the decode path.
            ImageResult.FromMemory(OnePixelPng, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception ex)
        {
            // Never fatal. Warning, not Debug, so a failed preload is visible; pass ex to keep the inner cause.
            log.Warning(ex, "[Proteus] image codec preload FAILED, plugin-reload crashes may return");
        }
    }

    /// <summary>A 1x1 RGBA PNG: signature, IHDR, one zlib-deflated scanline, IEND. Covered by
    /// ImageCodecPreloadTests, since a bad CRC would be swallowed silently by the preload.</summary>
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
    // BC7/BC5 encoding (and BC decoding) in native code, fanned out across cores by 4x4 block-row range.
    // Falls back to the managed encoder if the DLL is missing or a call throws.
    private const string NativeLib = "proteus_bcn";
    private static int _nativeProbed;
    private static volatile bool _nativeAvailable;

    /// <summary>
    /// Which block-compression backend a call would use: <c>true</c> native shim, <c>false</c> managed BCnEncoder.
    /// The two produce different bytes, so this belongs in the content tag that names an output file.
    /// </summary>
    public static bool NativeEncoderAvailable => _nativeAvailable;

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void proteus_encode_bc7(IntPtr rgba, int width, int height, int blockRowStart, int blockRowCount, IntPtr outPtr);

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void proteus_encode_bc5(IntPtr rgba, int width, int height, int blockRowStart, int blockRowCount, IntPtr outPtr);

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int proteus_decode_bcn(int format, IntPtr blocks, int width, int blockRowStart, int blockRowCount, IntPtr rgbaOut);

    /// <summary>Compressed bytes per 4×4 block, or 0 for a format the shim doesn't handle. Asked of the native side
    /// so the caller's stride and the decoder's cannot drift.</summary>
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int proteus_bcn_block_bytes(int format);

    /// <summary>Formats <see cref="proteus_decode_bcn"/> understands. Values must match the
    /// <c>proteus_bcn_format</c> enum in <c>native/src/proteus_bcn.cpp</c>.</summary>
    private enum NativeBcFormat { Bc1 = 1, Bc3 = 3, Bc5 = 5, Bc7 = 7 }

    /// <summary>
    /// Copy the native DLL to a private path and return it, so the build output is never the file Windows maps
    /// (the lock outlives a plugin unload). Named by the source's length and write time, so a rebuilt DLL gets a
    /// fresh path. Returns the original path if anything goes wrong.
    /// </summary>
    private static string ShadowCopyNative(string dll, IPluginLog log)
    {
        try
        {
            var fi = new FileInfo(dll);
            if (!fi.Exists) return dll;

            var root = Path.Combine(Path.GetTempPath(), "Proteus.native");
            var stamp = $"{fi.Length:X}-{fi.LastWriteTimeUtc.Ticks:X}";
            var dir = Path.Combine(root, stamp);
            var shadow = Path.Combine(dir, NativeLib + ".dll");

            if (!File.Exists(shadow) || new FileInfo(shadow).Length != fi.Length)
            {
                Directory.CreateDirectory(dir);
                // Copy to a unique name and move into place, so concurrent instances never read a half-written DLL.
                var tmp = shadow + "." + Path.GetRandomFileName() + ".tmp";
                File.Copy(dll, tmp, overwrite: true);
                try { File.Move(tmp, shadow, overwrite: true); }
                catch (IOException) when (File.Exists(shadow)) { try { File.Delete(tmp); } catch { } }
            }

            // Best-effort sweep of copies from older builds; ones still mapped refuse to delete and are skipped.
            try
            {
                foreach (var old in Directory.GetDirectories(root))
                    if (!string.Equals(old, dir, StringComparison.OrdinalIgnoreCase))
                        try { Directory.Delete(old, recursive: true); } catch { }
            }
            catch { }

            return shadow;
        }
        catch (Exception ex)
        {
            log.Warning("[Proteus] native shim shadow copy failed ({0}) — loading it in place, which will "
                      + "lock the build output until the game exits", ex.Message);
            return dll;
        }
    }

    // Load proteus_bcn.dll from the plugin's own directory, which Dalamud's load context doesn't put on the
    // native search path. Runs once.
    private static void EnsureNativeCompressor(IPluginLog log)
    {
        if (Interlocked.Exchange(ref _nativeProbed, 1) == 1) return;

        // The shim is built /arch:AVX2 with no runtime dispatch, so MSVC emits AVX/AVX2/FMA/BMI/LZCNT everywhere,
        // including scalar code. LoadLibrary succeeds on any CPU; the first call then dies of
        // STATUS_ILLEGAL_INSTRUCTION, which no catch can see — the CLR fail-fasts the game. Pentium/Celeron parts
        // lacked AVX entirely until Alder Lake, so this has to be decided before the DLL is ever called.
        // IsSupported also covers the OS not enabling YMM state.
        if (!(System.Runtime.Intrinsics.X86.Avx2.IsSupported && System.Runtime.Intrinsics.X86.Fma.IsSupported
           && System.Runtime.Intrinsics.X86.Bmi1.IsSupported && System.Runtime.Intrinsics.X86.Bmi2.IsSupported
           && System.Runtime.Intrinsics.X86.Lzcnt.IsSupported))
        {
            _nativeAvailable = false;
            log.Warning("[Proteus] native block compressor skipped: this CPU lacks AVX2/FMA/BMI — using managed BCnEncoder and Lumina");
            return;
        }

        // PluginInterface.AssemblyLocation is authoritative (Assembly.Location can be empty under the load context).
        string? dir = null;
        try { dir = Plugin.PluginInterface.AssemblyLocation.DirectoryName; } catch { }
        if (string.IsNullOrEmpty(dir))
            dir = Path.GetDirectoryName(typeof(TextureLoader).Assembly.Location);
        var dll = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, NativeLib + ".dll");
        // Load a private copy, never the build output itself — see ShadowCopyNative.
        if (dll != null) dll = ShadowCopyNative(dll, log);

        // Resolver so DllImport("proteus_bcn") at call time finds the DLL by full path.
        NativeLibrary.SetDllImportResolver(typeof(TextureLoader).Assembly, (name, _, _) =>
            (string.Equals(name, NativeLib, StringComparison.OrdinalIgnoreCase) && dll != null
                && File.Exists(dll) && NativeLibrary.TryLoad(dll, out var h)) ? h : IntPtr.Zero);

        // Probe once with NativeLibrary.Load, which throws with the real reason, so a failure is diagnosable.
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

    // Decode runs on prefetch threads as well as the composite thread; time is split by caller, since only the
    // composite thread's time is on the critical path.
    [ThreadStatic] private static bool bgPrefetch;

    /// <summary>
    /// Marks the calling thread's decodes as background prefetch, reported separately from composite wait time.
    /// </summary>
    public static bool BackgroundPrefetch
    {
        get => bgPrefetch;
        set => bgPrefetch = value;
    }

    /// <summary>
    /// Time the composite thread spent inside the decode path (decoding, blocking on a prefetch, or cache hits):
    /// the critical-path cost.
    /// </summary>
    public readonly PhaseCounter DecodeWaitStats = new();

    /// <summary>Elapsed decode time on background prefetch threads. Overlapped work, not critical path.</summary>
    public readonly PhaseCounter PrefetchWaitStats = new();

    /// <summary>Time spent actually decoding (cache misses only), summed across all threads. Calls = misses.</summary>
    public readonly PhaseCounter DecodeStats = new();
    /// <summary>Calls served from an ALREADY-MATERIALIZED cache entry — a true hit, near free.</summary>
    public readonly PhaseCounter DecodeHitStats = new();
    /// <summary>
    /// Calls that found a cache entry but blocked on another thread finishing its decode (usually a prefetch).
    /// Counted apart from true hits.
    /// </summary>
    public readonly PhaseCounter DecodeBlockedStats = new();
    /// <summary>
    /// Decodes served by the native shim rather than Lumina.
    /// </summary>
    public readonly PhaseCounter DecodeNativeStats = new();

    /// <summary>The RGBA→BGRA conversion loop in <see cref="WriteTex"/>.</summary>
    public readonly PhaseCounter SwizzleStats = new();
    /// <summary>The .tex disk write itself, with bytes written.</summary>
    public readonly PhaseCounter WriteStats = new();

    public void ResetStats()
    {
        // One recomposite = one generation; entries it touches are protected from the trim — see runGeneration.
        Interlocked.Increment(ref runGeneration);
        DecodeStats.Reset();
        DecodeHitStats.Reset();
        DecodeBlockedStats.Reset();
        DecodeNativeStats.Reset();
        DecodeWaitStats.Reset();
        PrefetchWaitStats.Reset();
        SwizzleStats.Reset();
        WriteStats.Reset();
        Volatile.Write(ref evictions, 0);
    }

    // ── Decode cache ───────────────────────────────────────────────────────────
    // Decoded RGBA keyed by path + last-write-time + length, so each file is decoded once until it changes on disk.
    // Byte-budgeted with generation-aware LRU eviction; the Lazy guarantees one decode under concurrent requests.
    //
    // Mutation contract: LoadBaseTexture returns a CLONE on a hit (bases are composited in place); LoadPngAsRgba
    // shares the cached array, and every mutating consumer clones first.
    private sealed class DecodedTex
    {
        public byte[] Rgba = Array.Empty<byte>();
        public int Width;
        public int Height;
        public long LastAccess;
        /// <summary>The run that last touched this entry. See <see cref="runGeneration"/>.</summary>
        public long Generation;
    }

    private readonly ConcurrentDictionary<string, Lazy<DecodedTex?>> decodeCache = new();
    private long accessClock;

    /// <summary>
    /// Bumped by <see cref="ResetStats"/>, once per recomposite. Entries stamped with the current generation are
    /// evicted only as a last resort, so a file prefetched for this run is not evicted before the run consumes it.
    /// </summary>
    private long runGeneration;
    private long lastTouchTick = Environment.TickCount64;
    private int evictions;

    /// <summary>
    /// Ceiling on decoded bytes held (one 4K RGBA entry is 64 MB). Must exceed a composite's whole working set,
    /// since the cyclic access pattern defeats LRU. Set from Configuration.DecodeCacheBudgetMb; lowering it
    /// reclaims immediately.
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

    // Default until Plugin's constructor pushes the configured value; modest so tests and tooling don't reserve gigabytes.
    private long decodeCacheBudgetBytes = 2048L * 1024 * 1024;

    /// <summary>Entries currently materialized, and the bytes they hold, for the phases log.</summary>
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
    /// Drop everything if nothing has touched the cache for <paramref name="idle"/>. Returns the number of entries
    /// released, 0 when still warm. The arrays become collectable; LOH memory is not necessarily returned to the OS.
    /// </summary>
    public int ReleaseIfIdle(TimeSpan idle)
    {
        if (Environment.TickCount64 - Volatile.Read(ref lastTouchTick) < idle.TotalMilliseconds) return 0;
        int n = decodeCache.Count;
        if (n == 0) return 0;
        decodeCache.Clear();
        return n;
    }

    // Cache key for an on-disk file: prefix + path + write-time + length. Null (bypass the cache) if the file is
    // missing or unreadable.
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
        // Charge the whole call to its caller: decoding, blocking on a prefetch, and cache lookups.
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
        // The factory runs only on a miss, so this flag separates decode cost from cache hits.
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

        // Read before blocking on lazy.Value: an unmaterialized value we aren't producing means this call blocks
        // on another thread's decode, which is counted separately from a hit.
        var wasMaterialized = lazy.IsValueCreated;

        DecodedTex? entry;
        try { entry = lazy.Value; }
        catch { decodeCache.TryRemove(new KeyValuePair<string, Lazy<DecodedTex?>>(key, lazy)); throw; }

        // Don't keep failed decodes in the cache — let the next call retry.
        if (entry == null)
        {
            decodeCache.TryRemove(new KeyValuePair<string, Lazy<DecodedTex?>>(key, lazy));
            return null;
        }

        if (!decoded)
        {
            if (wasMaterialized) DecodeHitStats.Count();
            else DecodeBlockedStats.Count();
        }

        entry.Generation = Volatile.Read(ref runGeneration);
        entry.LastAccess = Interlocked.Increment(ref accessClock);
        Volatile.Write(ref lastTouchTick, Environment.TickCount64);   // keeps ReleaseIfIdle off a live cache
        TrimCache();
        return entry;
    }

    /// <summary>
    /// Drop every cached decode whose source file lives under the given mod directory, since a reinstall can keep
    /// the timestamp and length the key relies on. Returns the count removed.
    /// </summary>
    public int EvictMod(string modDir)
    {
        if (string.IsNullOrEmpty(modDir)) return 0;
        // Disk paths are "<modsRoot>\<modDir>\...", so match with separators on both sides ("Bone" never evicts "Boney").
        var needle = string.Concat(Path.DirectorySeparatorChar, modDir, Path.DirectorySeparatorChar);
        int removed = 0;
        foreach (var key in decodeCache.Keys)   // Keys is a snapshot; safe to remove while iterating
            if (key.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 &&
                decodeCache.TryRemove(key, out _))
                removed++;
        return removed;
    }

    /// <summary>
    /// Drop the entire decode cache, for edits the mtime+length key can't detect. Returns the count removed.
    /// </summary>
    public int ClearCache()
    {
        int n = decodeCache.Count;
        decodeCache.Clear();
        return n;
    }

    // Evict materialized entries until under the byte budget: oldest generation first, least-recently accessed
    // within a generation. The current run's entries go only as a last resort — see runGeneration.
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
        live.Sort((a, b) => a.d.Generation != b.d.Generation
            ? a.d.Generation.CompareTo(b.d.Generation)
            : a.d.LastAccess.CompareTo(b.d.LastAccess));

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
    /// Texture game paths from a material's raw bytes — mod redirect first, else game data (see
    /// <see cref="LoadRawFile"/>). The only way to read a material's texture paths: Lumina reports zero samplers
    /// for real Dawntrail materials.
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
            log.Error(ex, "[Proteus] Failed to parse mtrl bytes: {0}", gamePath);
            return new MtrlTexturePaths(null, null, null);
        }
    }

    /// <summary>
    /// Walk a .mtrl's header → sampler table by hand (v1.3 layout). Every step is bounds-checked and bails to
    /// "nothing found" rather than throwing.
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
            // Strips the marker only when it leads the whole string. A marker on the file name is left verbatim on
            // purpose: these paths are the compositor's redirect keys. Confirm in game before changing this.
            if (path.StartsWith("--", StringComparison.Ordinal)) path = path[2..];

            if      (samplerId is SamplerIdDiffuse or SamplerIdColorMap0) diffuse = path;
            else if (samplerId == SamplerIdNormal)                        normal  = path;
            else if (samplerId == SamplerIdMask)                          mask    = path;
            else if (samplerId == SamplerIdIndex)                         index   = path;
        }
        // Parsed is true only at this exit, so callers can tell "no index texture" from "unreadable".
        // HasColorTable uses the very test PatchColorTable refuses on, so the two cannot disagree.
        bool hasColorTable = GearMaterialWriter.ColorTableStart(b) >= 0;
        return new MtrlTexturePaths(diffuse, normal, mask, index, Parsed: true, HasColorTable: hasColorTable);
    }

    /// <summary>
    /// A copy of a material whose texture table names different files: every texture path that is a key of
    /// <paramref name="retarget"/> (compared as <see cref="ParseMtrlBytes"/> reports paths) points at its value.
    /// Null when nothing matched or the file could not be walked. New strings are appended to the string table,
    /// so no existing string offset moves.
    /// </summary>
    internal static byte[]? RetargetTexturePaths(byte[] b, IReadOnlyDictionary<string, string> retarget)
    {
        const int HeaderSize = 0x10;
        if (b.Length < HeaderSize || retarget.Count == 0) return null;

        ushort fileSize        = BitConverter.ToUInt16(b, 0x04);
        ushort stringTableSize = BitConverter.ToUInt16(b, 0x08);
        byte   textureCount    = b[0x0C];
        byte   uvSetCount      = b[0x0D];
        byte   colorSetCount   = b[0x0E];

        int stringsAt = HeaderSize + textureCount * 4 + uvSetCount * 4 + colorSetCount * 4;
        int stringsEnd = stringsAt + stringTableSize;
        if (stringsEnd > b.Length) return null;

        var appended  = new List<byte>();
        var offsetOf  = new Dictionary<string, int>(StringComparer.Ordinal);   // one copy per distinct target
        var newOffset = new Dictionary<int, int>();                            // texture index → new offset
        for (int i = 0; i < textureCount; i++)
        {
            int at = stringsAt + BitConverter.ToUInt16(b, HeaderSize + i * 4);
            if (at >= stringsEnd) continue;
            int end = at;
            while (end < stringsEnd && b[end] != 0) end++;
            var path = Encoding.UTF8.GetString(b, at, end - at);
            if (path.StartsWith("--", StringComparison.Ordinal)) path = path[2..];
            if (!retarget.TryGetValue(path, out var target)) continue;

            if (!offsetOf.TryGetValue(target, out var off))
            {
                off = stringTableSize + appended.Count;
                appended.AddRange(Encoding.UTF8.GetBytes(target));
                appended.Add(0);
                offsetOf[target] = off;
            }
            newOffset[i] = off;
        }
        if (newOffset.Count == 0) return null;

        // Padded on its own account, so the table keeps whatever alignment it had.
        while (appended.Count % 4 != 0) appended.Add(0);
        // Every size and offset is a u16; a wrapped offset would name garbage rather than fail.
        if (stringTableSize + appended.Count > ushort.MaxValue || fileSize + appended.Count > ushort.MaxValue)
            return null;

        var result = new byte[b.Length + appended.Count];
        Array.Copy(b, 0, result, 0, stringsEnd);
        appended.CopyTo(result, stringsEnd);
        Array.Copy(b, stringsEnd, result, stringsEnd + appended.Count, b.Length - stringsEnd);

        BitConverter.TryWriteBytes(result.AsSpan(0x04), (ushort)(fileSize + appended.Count));
        BitConverter.TryWriteBytes(result.AsSpan(0x08), (ushort)(stringTableSize + appended.Count));
        // The low half of each texture entry is the offset; the high half is flags, left as they were.
        foreach (var (i, off) in newOffset)
            BitConverter.TryWriteBytes(result.AsSpan(HeaderSize + i * 4), (ushort)off);
        return result;
    }

    /// <summary>
    /// Whether an on-disk <c>.tex</c> stores its pixels uncompressed, or null when unreadable. Asked before trusting
    /// a texture whose values are data (an <c>_id</c> map's row selectors), which a lossy codec would corrupt.
    /// </summary>
    public static bool? IsUncompressed(string diskPath)
    {
        try
        {
            if (!File.Exists(diskPath)) return null;
            using var fs = File.OpenRead(diskPath);
            Span<byte> head = stackalloc byte[8];
            if (fs.Read(head) < 8) return null;
            uint format = BitConverter.ToUInt32(head[4..]);
            return format is 0x1450 or 0x1451;   // B8G8R8A8 / B8G8R8X8
        }
        catch { return null; }
    }

    /// <summary>Load an on-disk .tex file as RGBA8. Returns null on failure.</summary>
    public (byte[] rgba, int width, int height)? LoadTexAsRgba(string diskPath)
    {
        try
        {
            if (!File.Exists(diskPath)) return null;
            return LoadTexBytesAsRgba(File.ReadAllBytes(diskPath), diskPath);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] Failed to load .tex: {0}", diskPath);
            return null;
        }
    }

    /// <summary>
    /// Load a <c>.tex</c> already in memory as RGBA8 (e.g. reassembled from a <c>.ttmp2</c> by
    /// <see cref="TexToolsPackage"/>). Returns null on failure.
    /// </summary>
    /// <param name="what">What these bytes are, for the log line when they will not decode.</param>
    public (byte[] rgba, int width, int height)? LoadTexBytesAsRgba(byte[] bytes, string what)
    {
        try
        {
            var sanitized = SanitizeTexBytes(bytes);
            var tex = LoadLuminaFileFromBytes<TexFile>(sanitized);
            if (tex == null) return null;
            return DecodeTexSurface(sanitized, tex);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] Failed to load .tex: {0}", what);
            return null;
        }
    }

    /// <summary>
    /// Load a .dds file as RGBA8. BC payloads are wrapped as a single-mip .tex for Lumina's decoder; uncompressed
    /// 32-bpp payloads are reordered via the channel masks. Returns null on failure or an unsupported format.
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
            log.Error(ex, "[Proteus] Failed to load .dds: {0}", diskPath);
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

        // Native decode straight off the DDS payload, trusted only once this format has been verified against Lumina.
        var fast = TryDecodeNative(luminaFmt, dds, dataOffset, width, height);
        if (fast != null && _nativeDecodeVerified.ContainsKey(luminaFmt))
            return (fast, width, height);

        // Wrap mip 0 as a minimal single-surface .tex (80-byte header + block data) for Lumina's decoder.
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
        if (texFile == null) return null;
        var reference = ConvertTex(texFile);

        // First .dds of this format: compare native against Lumina and record the verdict. BC decode is exact,
        // so a mismatch is a shim bug.
        if (fast != null)
        {
            if (!fast.AsSpan().SequenceEqual(reference.rgba))
            {
                _nativeDecodeRejected.TryAdd(luminaFmt, 0);
                log.Warning("[Proteus] native BC decode disagreed with Lumina on a {0}x{1} format 0x{2:X4} "
                          + ".dds — falling back to Lumina for THIS FORMAT for the rest of the session",
                          width, height, luminaFmt);
                return reference;
            }
            _nativeDecodeVerified.TryAdd(luminaFmt, 0);
            log.Information("[Proteus] native BC decode verified against Lumina ({0}x{1}, format 0x{2:X4}, .dds)",
                width, height, luminaFmt);
        }
        return reference;
    }

    // Reorders an uncompressed 32-bpp DDS surface to RGBA8 via per-channel masks, each assumed to select a whole
    // byte. aMask == 0 means opaque.
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
    /// Load a base texture as RGBA8, trying a Penumbra-resolved disk path first, then the game's SqPack.
    /// </summary>
    // The base texture's size sets the output resolution, so every base below this is upscaled (bilinear) to it.
    public const int BaseTargetSize = 4096;

    // Clone (for in-place compositing) when already ≥4K, otherwise upscale, which yields a fresh buffer.
    private static (byte[] rgba, int width, int height) CloneOrUpscaleBase(byte[] rgba, int width, int height)
        => width >= BaseTargetSize && height >= BaseTargetSize
            ? ((byte[])rgba.Clone(), width, height)
            : (UVRemapService.ResizeBilinear(rgba, width, height, BaseTargetSize, BaseTargetSize), BaseTargetSize, BaseTargetSize);

    /// <summary>
    /// Bring a freshly decoded base texture up to <see cref="BaseTargetSize"/> before it enters the cache, so the
    /// upscale is paid once per file instead of once per composite.
    /// </summary>
    private static (byte[] rgba, int width, int height)? UpscaleForCache((byte[] rgba, int width, int height)? decoded)
        => decoded is not { } v ? null
         : v.width >= BaseTargetSize && v.height >= BaseTargetSize ? v
         : (UVRemapService.ResizeBilinear(v.rgba, v.width, v.height, BaseTargetSize, BaseTargetSize),
            BaseTargetSize, BaseTargetSize);

    /// <summary>Memo for <see cref="BaseNativeSize"/>'s game-data branch; vanilla game data is immutable for the session.</summary>
    private readonly ConcurrentDictionary<string, (int W, int H)?> nativeSizes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A base texture's native dimensions, before <see cref="LoadBaseTexture"/> upscales it to
    /// <see cref="BaseTargetSize"/>, for callers that need the sheet's real aspect.
    /// </summary>
    public (int Width, int Height)? BaseNativeSize(string? diskPath, string gamePath)
    {
        if (diskPath != null && ProbeSize(diskPath) is { } onDisk) return onDisk;
        return nativeSizes.GetOrAdd(gamePath, p =>
        {
            try
            {
                var tex = dataManager.GetFile<TexFile>(p);
                return tex == null ? null : ((int, int)?)(tex.Header.Width, tex.Header.Height);
            }
            catch (Exception ex)
            {
                log.Error(ex, "[Proteus] Failed to read the native size of {0}", p);
                return null;
            }
        });
    }

    public (byte[] rgba, int width, int height)? LoadBaseTexture(string? diskPath, string gamePath)
    {
        if (diskPath != null && File.Exists(diskPath))
        {
            var key = DiskKey("BD", diskPath);
            if (key != null)
            {
                var hit = GetOrDecode(key, () => UpscaleForCache(LoadTexAsRgba(diskPath)));
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
                // Upscaled before caching for the same reason as the disk path above.
                return UpscaleForCache(ConvertTex(tex));
            }
            catch (Exception ex)
            {
                log.Error(ex, "[Proteus] Failed to load base texture from game data: {0}", gamePath);
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
        // Per-pixel with no carried state, so ParallelPixels partitions it without changing a byte.
        OverlayBlend.ParallelPixels(0, bgra.Length, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                rgba[i]     = bgra[i + 2];
                rgba[i + 1] = bgra[i + 1];
                rgba[i + 2] = bgra[i];
                rgba[i + 3] = bgra[i + 3];
            }
        });
        return (rgba, w, h);
    }

    /// <summary>
    /// Load any image the Create tab's picker accepts (.tex, .dds, and whatever Stb reads) as RGBA8 at its own size.
    /// Unlike <see cref="LoadPngAsRgba"/>, never scales. Uncached.
    /// </summary>
    public (byte[] rgba, int width, int height)? LoadImageAsRgba(string path)
    {
        try
        {
            if (path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) return LoadTexAsRgba(path);
            if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) return LoadDdsAsRgba(path);

            using var stream = File.OpenRead(path);
            var img = ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            return img.Data is { Length: > 0 } ? (img.Data, img.Width, img.Height) : null;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] Failed to load image at its own size: {0}", path);
            return null;
        }
    }

    /// <summary>
    /// Cache a buffer derived from one source file under that file's identity, so it is budgeted, evicted with its
    /// mod (<see cref="EvictMod"/>), and missed when the source changes. <paramref name="derivation"/> must name
    /// everything besides the file that decides the result. A null from <paramref name="derive"/> is not cached.
    /// The returned array is shared and read-only.
    /// </summary>
    public byte[]? GetOrDerive(string sourcePath, string derivation, int w, int h, Func<byte[]?> derive)
    {
        var key = DiskKey("DERIVED", sourcePath);
        if (key == null) return derive();
        return GetOrDecode(key + "|" + derivation + "|" + w + "x" + h,
                           () => derive() is { } d ? (d, w, h) : null)?.Rgba;
    }

    /// <summary>
    /// Load an overlay image (<c>.png</c>, <c>.tex</c> or <c>.dds</c>, by extension) as RGBA8, scaled to
    /// (targetW × targetH) if needed. Returns null on failure. <paramref name="filter"/> decides the resample —
    /// see <see cref="ResampleFilter"/>; index-texture callers must pass Nearest.
    /// </summary>
    public byte[]? LoadPngAsRgba(string path, int targetW, int targetH,
                                 ResampleFilter filter = ResampleFilter.Auto)
    {
        // Extension tolerance: if the exact file is absent, resolve a sibling .png/.dds/.tex.
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
                    // Decompress to RGBA at stored size, then scale to the requested target.
                    var full = isTex ? LoadTexAsRgba(path) : LoadDdsAsRgba(path);
                    if (full == null) return null;
                    var (rgba, sw, sh) = full.Value;
                    var data = (sw == targetW && sh == targetH)
                        ? rgba
                        : Resample(rgba, sw, sh, targetW, targetH, filter);
                    return (data, targetW, targetH);
                }

                using var stream = File.OpenRead(path);
                var img = ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                var pdata = (img.Width == targetW && img.Height == targetH)
                    ? img.Data
                    : Resample(img.Data, img.Width, img.Height, targetW, targetH, filter);
                return (pdata, targetW, targetH);
            }
            catch (Exception ex)
            {
                log.Error(ex, "[Proteus] Failed to load overlay image: {0}", path);
                return null;
            }
        }

        // Key includes the target size; distinct prefixes keep same-stem .tex/.dds/.png apart.
        var key = DiskKey(isTex ? "TEXO" : isDds ? "DDSO" : "PNG", path);
        if (key == null) return Decode()?.rgba;

        // ...and the filter, since the same file at the same size decodes differently under each.
        var fkey = filter == ResampleFilter.Nearest ? "|n" : "|a";

        // Read-only for callers, so the cached array is shared (no clone).
        return GetOrDecode(key + "|" + targetW + "x" + targetH + fkey, Decode)?.Rgba;
    }

    /// <summary>
    /// Whether <paramref name="path"/> holds a complete .tex written by <see cref="WriteTex"/>: its length is exactly
    /// what its own header describes. Anything missing, truncated or unrecognised reads as incomplete, since output
    /// names carry a content hash and an interrupted write would otherwise be reused forever.
    /// </summary>
    public static bool IsCompleteTex(string path)
    {
        // FileInfo is constructed inside the try: it throws on a malformed path, which should read as false.
        try { return IsCompleteTex(new FileInfo(path)); }
        catch { return false; }
    }

    /// <inheritdoc cref="IsCompleteTex(string)"/>
    /// <remarks>
    /// Takes the <see cref="FileInfo"/> so a caller that has already stat'd the file doesn't stat it again.
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
    /// Write an RGBA8 buffer as an uncompressed B8G8R8A8 .tex file. Returns true on success.
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
                // Convert RGBA → BGRA. Per-pixel with no carried state, so partitioning cannot change the bytes.
                var tSwizzle = PhaseCounter.Begin();
                var dst = new byte[rgba.Length];
                OverlayBlend.ParallelPixels(0, rgba.Length, 4, (from, to) =>
                {
                    for (int i = from; i < to; i += 4)
                    {
                        dst[i]     = rgba[i + 2]; // B ← R
                        dst[i + 1] = rgba[i + 1]; // G
                        dst[i + 2] = rgba[i];     // R ← B
                        dst[i + 3] = rgba[i + 3]; // A
                    }
                });
                payload = dst;
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
            log.Error(ex, "[Proteus] Failed to write .tex: {0}", outputPath);
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
        // The native code reads unchecked pinned memory, so an undersized buffer would be an uncatchable
        // AccessViolation. Throw a managed exception first so the caller's fallback absorbs it.
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

    /// <summary>
    /// Map a Lumina/FFXIV texture format code onto a format the native decoder understands, or null when
    /// it is not block-compressed (or is a BC variant the shim doesn't carry).
    /// </summary>
    private static NativeBcFormat? NativeFormatFor(uint luminaFormat) => luminaFormat switch
    {
        0x3420u => NativeBcFormat.Bc1,   // DXT1
        0x3431u => NativeBcFormat.Bc3,   // DXT5
        0x6230u => NativeBcFormat.Bc5,
        0x6432u => NativeBcFormat.Bc7,
        // BC2 (0x3430) is deliberately absent: rgbcx has no unpack for it, and the game does not ship it.
        _ => null,
    };

    /// <summary>
    /// Native block decode via proteus_bcn.dll, fanned out across cores by 4x4 block-rows, emitting RGBA directly.
    /// Returns null (use the managed path) when the format isn't handled, the payload is short, or the call fails.
    /// </summary>
    private static byte[]? DecodeBlockCompressedNative(uint luminaFormat, byte[] blocks, int blockOffset, int width, int height)
    {
        if (!_nativeAvailable) return null;
        if (_nativeDecodeRejected.ContainsKey(luminaFormat)) return null;
        if (NativeFormatFor(luminaFormat) is not { } fmt) return null;
        if (width <= 0 || height <= 0 || width % 4 != 0 || height % 4 != 0) return null;

        // Bytes per block is format-dependent (BC1 is 8, the rest 16); asked of the native side so the slicing
        // stride here can never disagree with the walking stride there.
        int stride = proteus_bcn_block_bytes((int)fmt);
        if (stride <= 0) return null;

        int bw = width / 4, bh = height / 4;
        long need = (long)bw * bh * stride;
        // The native code reads and writes with no bounds check, so a short buffer would be an uncatchable
        // AccessViolation. Checked before anything is pinned.
        if (blockOffset < 0 || blockOffset + need > blocks.Length) return null;

        var rgba = new byte[(long)width * height * 4];
        var hIn = GCHandle.Alloc(blocks, GCHandleType.Pinned);
        var hOut = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            long inPtr = hIn.AddrOfPinnedObject().ToInt64() + blockOffset;
            long outPtr = hOut.AddrOfPinnedObject().ToInt64();
            int workers   = Math.Min(Environment.ProcessorCount, 16);
            int chunkRows = Math.Max(1, (bh + workers - 1) / workers);
            int chunks    = (bh + chunkRows - 1) / chunkRows;
            int ok = 1;
            Parallel.For(0, chunks, ci =>
            {
                int start = ci * chunkRows;
                int count = Math.Min(chunkRows, bh - start);
                if (count <= 0) return;
                // Blocks are this worker's slice; the output pointer is the whole image, since the native scatter
                // computes absolute row offsets.
                var blockP = new IntPtr(inPtr + (long)start * bw * stride);
                if (proteus_decode_bcn((int)fmt, blockP, width, start, count, new IntPtr(outPtr)) == 0)
                    Interlocked.Exchange(ref ok, 0);
            });
            return ok == 1 ? rgba : null;
        }
        finally { hIn.Free(); hOut.Free(); }
    }

    /// <summary>
    /// <see cref="DecodeBlockCompressedNative"/> with the encoder's fallback discipline: any throw latches the native
    /// path off for the session.
    /// </summary>
    private byte[]? TryDecodeNative(uint luminaFormat, byte[] blocks, int blockOffset, int width, int height)
    {
        if (!_nativeAvailable) return null;
        try
        {
            var r = DecodeBlockCompressedNative(luminaFormat, blocks, blockOffset, width, height);
            if (r != null) DecodeNativeStats.Count();
            return r;
        }
        catch (Exception ex)
        {
            _nativeAvailable = false;
            log.Warning("[Proteus] native BC decode failed ({0}) — falling back to Lumina", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Texture formats whose native decode has been checked against Lumina and agreed this session. Per format, so
    /// verifying one format never waves another through.
    /// </summary>
    private static readonly ConcurrentDictionary<uint, byte> _nativeDecodeVerified = new();

    /// <summary>
    /// Formats whose native decode disagreed with Lumina; those fall back for the rest of the session while every
    /// other format stays native.
    /// </summary>
    private static readonly ConcurrentDictionary<uint, byte> _nativeDecodeRejected = new();

    /// <summary>
    /// Decode a sanitized .tex through the native shim, falling back to Lumina for anything it cannot do. The first
    /// native decode of each format is checked byte for byte against Lumina (BC decode is exact).
    /// </summary>
    private (byte[] rgba, int width, int height) DecodeTexSurface(byte[] sanitized, TexFile tex)
    {
        int w = tex.Header.Width, h = tex.Header.Height;
        if (sanitized.Length < 80) return ConvertTex(tex);

        // Format and mip-0 offset from the header Lumina just read, so both decoders see the same bytes.
        uint fmt = BitConverter.ToUInt32(sanitized, 4);
        long off = BitConverter.ToUInt32(sanitized, 28);
        if (off <= 0 || off > int.MaxValue) return ConvertTex(tex);

        var native = TryDecodeNative(fmt, sanitized, (int)off, w, h);
        if (native == null) return ConvertTex(tex);

        if (!_nativeDecodeVerified.ContainsKey(fmt))
        {
            var reference = ConvertTex(tex);
            if (!native.AsSpan().SequenceEqual(reference.rgba))
            {
                _nativeDecodeRejected.TryAdd(fmt, 0);
                log.Warning("[Proteus] native BC decode disagreed with Lumina on a {0}x{1} format 0x{2:X4} "
                          + "surface — falling back to Lumina for THIS FORMAT for the rest of the session",
                          w, h, fmt);
                return reference;
            }
            _nativeDecodeVerified.TryAdd(fmt, 0);
            log.Information("[Proteus] native BC decode verified against Lumina ({0}x{1}, format 0x{2:X4})", w, h, fmt);
        }
        return (native, w, h);
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
            log.Error(ex, "[Proteus] Failed to write PNG: {0}", outputPath);
            return false;
        }
    }

    // Replicates GameData.GetFileFromDisk<T>() without a GameData instance; Data and Reader have internal setters.
    private static readonly PropertyInfo PropData   = typeof(FileResource).GetProperty("Data",   BindingFlags.Public | BindingFlags.Instance)!;
    private static readonly PropertyInfo PropReader = typeof(FileResource).GetProperty("Reader", BindingFlags.Public | BindingFlags.Instance)!;

    internal static T? LoadLuminaFileFromBytes<T>(byte[] bytes) where T : FileResource
    {
        var file = Activator.CreateInstance<T>();
        PropData.SetValue(file, bytes);
        PropReader.SetValue(file, new LuminaBinaryReader(bytes, PlatformId.Win32));
        file.LoadFile();
        return file;
    }

    // Some mod tools write MipCount > 1 with zeroed OffsetToSurface slots, which makes Lumina throw. Pre-patch
    // MipCount to the offsets actually populated.
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

        // Some TexTools exports write a bogus but monotonic offset table. If the second surface starts before mip 0
        // could end, collapse to a single mip so Lumina reads mip 0 right after the header.
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
            // Reject zero, non-monotonic, or out-of-bounds offsets (some tools write uncompressed-size offsets for BC).
            if (cur == 0 || cur <= prev || cur >= (uint)bytes.Length) break;
            prev = cur;
            validMips = i + 1;
        }
        if (validMips == mipCount) return bytes;

        var patched = (byte[])bytes.Clone();
        patched[14] = (byte)((patched[14] & 0x80) | (validMips & 0x7F));
        return patched;
    }

    // Byte size of mip level 0 for a .tex pixel format (Lumina TexFile.TextureFormat codes). 0 for unknown formats,
    // so callers skip the consistency check.
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
    /// Raw bytes of any game file, from the mod redirect if present else the game's own data. For an unmodded file
    /// <paramref name="diskPath"/> is the game path, not a real file, so it falls through. No Lumina parse, so the
    /// bytes come back exact.
    /// </summary>
    public byte[]? LoadRawFile(string? diskPath, string gamePath)
    {
        if (diskPath != null && File.Exists(diskPath))
        {
            try { return File.ReadAllBytes(diskPath); }
            catch (Exception ex) { log.Error(ex, "[Proteus] Failed to read raw file: {0}", diskPath); }
        }
        try { return dataManager.GetFile(gamePath)?.Data; }
        catch (Exception ex) { log.Error(ex, "[Proteus] Failed to load raw file from game: {0}", gamePath); return null; }
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

    // Patches the key in place if found; otherwise inserts a ShaderKey entry and updates ShaderKeyCount and FileSize.
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

    // Writes a unique .tmp in the same directory, then atomically moves it into place, so file watchers (Mare
    // Synchronos) only ever see a fully written file.
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
    // Deliberately nearest and not routed through Resample: callers ask for point sampling by name.
    public byte[] ScaleRgba(byte[] src, int sw, int sh, int dw, int dh)
        => ScaleNearest(src, sw, sh, dw, dh);

    /// <summary>
    /// Resize an RGBA8 buffer under a <see cref="ResampleFilter"/>. <see cref="ResampleFilter.Auto"/> uses area-average
    /// when an axis strictly shrinks, bilinear otherwise (an equal axis must not reach the box filter, which would
    /// point-sample the growing axis). Nearest is passed through for index maps.
    /// </summary>
    internal static byte[] Resample(byte[] src, int sw, int sh, int dw, int dh, ResampleFilter filter)
    {
        if (sw == dw && sh == dh) return src;
        if (filter == ResampleFilter.Nearest) return ScaleNearest(src, sw, sh, dw, dh);
        return dw < sw || dh < sh
            ? UVRemapService.ResizeBox(src, sw, sh, dw, dh)
            : UVRemapService.ResizeBilinear(src, sw, sh, dw, dh);
    }

    /// <summary>
    /// The stored dimensions of an image, read from its header without decoding. Null for anything missing or
    /// unrecognised. Uses the same sibling-extension tolerance as <see cref="LoadPngAsRgba"/>, so it measures the
    /// file that will be loaded.
    /// </summary>
    public static (int Width, int Height)? ProbeSize(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                var dir = Path.GetDirectoryName(path) ?? string.Empty;
                var stem = Path.GetFileNameWithoutExtension(path);
                foreach (var ext in new[] { ".png", ".dds", ".tex" })
                {
                    var cand = Path.Combine(dir, stem + ext);
                    if (File.Exists(cand)) { path = cand; break; }
                }
                if (!File.Exists(path)) return null;
            }

            Span<byte> head = stackalloc byte[32];
            using (var fs = File.OpenRead(path))
            {
                int got = 0;
                while (got < head.Length)
                {
                    int n = fs.Read(head[got..]);
                    if (n <= 0) break;
                    got += n;
                }
                if (got < 32) return null;
            }

            // PNG: IHDR comes first, so width/height are fixed at 16..23, big-endian.
            if (head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
            {
                int w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
                int h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
                return w > 0 && h > 0 ? (w, h) : null;
            }

            // DDS: "DDS " magic, then DDS_HEADER — dwHeight at 12, dwWidth at 16, little-endian.
            if (head[0] == 'D' && head[1] == 'D' && head[2] == 'S' && head[3] == ' ')
            {
                int h = BitConverter.ToInt32(head[12..16]);
                int w = BitConverter.ToInt32(head[16..20]);
                return w > 0 && h > 0 ? (w, h) : null;
            }

            // .tex has no magic, so identify by extension. Width at 0x08, height at 0x0A, u16 little-endian.
            if (path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                int w = BitConverter.ToUInt16(head[8..10]);
                int h = BitConverter.ToUInt16(head[10..12]);
                return w > 0 && h > 0 ? (w, h) : null;
            }

            return null;
        }
        catch
        {
            // A probe only informs the size choice; the caller falls back and the real load reports the error.
            return null;
        }
    }

    // Nearest-neighbour scale. Rows are independent, so partitioning by row doesn't change the output.
    private static byte[] ScaleNearest(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        void Row(int dy)
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

        // Partitioned by row, not ParallelPixels: that helper's small-image guard counts steps and would run serial.
        if (dh * dw < 256 * 256 || Environment.ProcessorCount < 2)
            for (int dy = 0; dy < dh; dy++) Row(dy);
        else
            Parallel.For(0, dh, Row);
        return dst;
    }
}
