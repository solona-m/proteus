using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BitMiracle.LibTiff.Classic;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>
/// Remaps overlay PNG pixels between FFXIV body UV spaces (bibo, gen3, gen2/vanilla)
/// using pre-baked 16-bit RGBA TIFF transfer maps from LooseTextureCompilerCore (MIT).
/// Maps are loaded lazily on first use and cached for the plugin lifetime.
/// </summary>
public class UVRemapService
{
    private readonly IPluginLog log;
    private readonly string mapsDir;

    private readonly Dictionary<(string From, string To), TransferMap?> cache = new();
    private readonly object cacheLock = new();

    private sealed class TransferMap(ushort[] x, ushort[] y, bool[] valid, int w, int h)
    {
        public readonly ushort[] X = x;
        public readonly ushort[] Y = y;
        public readonly bool[]   Valid = valid;
        public readonly int      W = w, H = h;
        // Dest pixels with round-trip error (UV-seam region); ApplyRemap drops them only where the
        // neighbourhood is sparse, so genuine garment edges survive.
        public byte[]? DropLevel;
        // Valid dest pixels within BorderReach px of a UV-island border; the seam-drop is confined to these so
        // interior fine detail is never touched.
        public bool[]? NearBorder;
    }

    public UVRemapService(IPluginLog log, string pluginDir)
    {
        this.log = log;
        mapsDir = Path.Combine(pluginDir, "uvmaps");
    }

    /// <summary>
    /// Infers the body type ("bibo", "gen3", "gen2") from a material game path suffix.
    /// Returns null for equipment/accessory materials that don't use a body-UV layout.
    /// </summary>
    public static string? InferBodyType(string mtrlGamePath)
    {
        if (mtrlGamePath.EndsWith("_bibo.mtrl", StringComparison.OrdinalIgnoreCase)) return "bibo";
        if (mtrlGamePath.EndsWith("_eve.mtrl",  StringComparison.OrdinalIgnoreCase)) return "gen3";
        // _a/_b mean body UV types only under /obj/body/: _a = vanilla (gen2), _b = gen3.
        if (mtrlGamePath.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase))
        {
            if (mtrlGamePath.EndsWith("_b.mtrl", StringComparison.OrdinalIgnoreCase)) return "gen3";
            if (mtrlGamePath.EndsWith("_a.mtrl", StringComparison.OrdinalIgnoreCase)) return "gen2";
        }
        return null;
    }

    /// <summary>
    /// Remaps <paramref name="srcRgba"/> from <paramref name="from"/> UV space to
    /// <paramref name="to"/> UV space. Returns the input unchanged on failure.
    /// </summary>
    public byte[] Remap(byte[] srcRgba, int srcW, int srcH, string from, string to)
    {
        var t0 = PhaseCounter.Begin();
        var map = GetMap(from, to);
        if (map == null) return srcRgba;
        var result = ApplyRemap(srcRgba, srcW, srcH, map);
        // Timed from the top so a first-call transfer-map load counts as remap.
        RemapStats.Stop(t0);
        return result;
    }

    /// <summary>
    /// Recomposite instrumentation: time spent in <see cref="Remap"/> (including a first-call
    /// transfer-map load). Reset and read by <see cref="CompositorService"/> around one run.
    /// </summary>
    public readonly PhaseCounter RemapStats = new();

    /// <summary>
    /// Which side of the character a vertex is on: <c>+1</c> = +X, <c>-1</c> = -X, <c>0</c> = unknown, which behaves
    /// as +1.
    /// </summary>
    public delegate (float U, float V)? UvConversion(float u, float v, int side);

    /// <summary>
    /// Reflect a U coordinate onto the other half of an asymmetric body sheet. bibo and gen3 lay the two sides out as
    /// mirror images about u = 0.5, and the right half (the one vanilla space is a crop of, see
    /// <see cref="CropRightHalf"/>) is the +X side.
    /// </summary>
    public static float MirrorU(float u) => 1f - u;

    /// <summary>
    /// The vanilla face layout. Mirrored like gen2: both sides of the face sample the same texels.
    /// </summary>
    public const string FaceSpace = "face";

    /// <summary>
    /// A doubled face sheet: the right half holds the +X side as <see cref="FaceSpace"/> lays it out, the left its
    /// mirror, so one-sided face art has somewhere to live. Declared through
    /// <see cref="OverlayDescriptor.SourceBodyType"/>; converted by the same affine as gen2 → bibo.
    /// </summary>
    public const string FaceSplitSpace = "facelr";

    /// <summary>
    /// The asymmetric space a mirrored one is one half of (gen2 → bibo, face → facelr), or null when the space is not
    /// mirrored. Both pairs convert by the same affine.
    /// </summary>
    public static string? DoubledSpaceOf(string? space)
        => string.Equals(space, "gen2", StringComparison.OrdinalIgnoreCase) ? "bibo"
         : string.Equals(space, FaceSpace, StringComparison.OrdinalIgnoreCase) ? FaceSplitSpace
         : null;

    /// <summary>
    /// A per-vertex UV converter, the geometry counterpart of <see cref="Remap"/>: maps a UV in <paramref name="from"/>
    /// space to <paramref name="to"/> space, or null where the maps have no correspondence. Returns null outright when
    /// no conversion is needed or no map covers the pair; the caller then leaves its UVs alone.
    /// Converting out of space S uses the map whose destination is S. Mirrored spaces enter and leave by halving U.
    /// <paramref name="unmirror"/> sends a -X vertex coming out of a mirrored space to the mirrored half, so asymmetric
    /// art survives a vanilla body.
    /// </summary>
    public UvConversion? UvConverter(string? from, string? to, bool unmirror = false)
    {
        if (from == null || to == null) return null;
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return null;

        // A mirrored space enters and leaves by the affine below, landing in its doubled counterpart, so to the
        // transfer maps it already is that space.
        var fromDoubled = DoubledSpaceOf(from);
        var toDoubled   = DoubledSpaceOf(to);
        bool fromMirrored = fromDoubled != null;
        bool toMirrored   = toDoubled != null;
        var srcSpace  = fromDoubled ?? from;
        var dstSpace  = toDoubled ?? to;

        // Null when both ends land in the same doubled space; then the affine is the whole conversion.
        TransferMap? map = null;
        if (!string.Equals(srcSpace, dstSpace, StringComparison.OrdinalIgnoreCase))
        {
            map = GetMap(dstSpace, srcSpace);   // indexed by a srcSpace pixel, stores the dstSpace coord
            if (map == null) return null;
        }

        // Memoized: the same UVs repeat bit-for-bit across per-layer rebuilds, and a miss walks up to
        // UvLookupReach rings. The side is part of the key, since un-mirroring gives one UV two answers.
        var memo = new System.Collections.Concurrent.ConcurrentDictionary<(float U, float V, int Side), (float U, float V)?>();
        return (u, v, side) => memo.GetOrAdd((u, v, unmirror ? side : 0), static (key, s) =>
        {
            var (u, v, side) = key;
            // A -X vertex reads the other half of the sheet; unknown (0) takes the +X branch.
            if (s.FromMirrored) u = s.Unmirror && side < 0 ? MirrorU(0.5f + u * 0.5f) : 0.5f + u * 0.5f;
            if (s.Map != null && !TryLookupUv(s.Map, u, v, out u, out v)) return null;
            if (s.ToMirrored)
            {
                // A mirrored space is only the right half of its doubled counterpart; a left-half point would get a
                // negative u that wraps and smears, so report it unmapped.
                if (u < 0.5f) return null;
                u = (u - 0.5f) * 2f;
            }
            return (u, v);
        }, (Map: map, FromMirrored: fromMirrored, ToMirrored: toMirrored, Unmirror: unmirror));
    }

    /// <summary>
    /// How far <see cref="TryLookupUv"/> widens its search, in map pixels. Vertices often sit on island borders whose
    /// own cell is outside every island; bounded so a vertex never snaps across a truly unmapped gap.
    /// </summary>
    private const int UvLookupReach = 48;

    /// <summary>Nearest valid correspondence to (u,v) in <paramref name="map"/>'s index space.</summary>
    private static bool TryLookupUv(TransferMap map, float u, float v, out float su, out float sv)
    {
        su = u; sv = v;
        int x0 = (int)MathF.Round(Math.Clamp(u, 0f, 1f) * (map.W - 1));
        int y0 = (int)MathF.Round(Math.Clamp(v, 0f, 1f) * (map.H - 1));
        for (int r = 0; r <= UvLookupReach; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                int y = y0 + dy;
                if (y < 0 || y >= map.H) continue;
                // Interior rows contribute only their two edge columns, so each ring visits its perimeter only.
                int step = Math.Abs(dy) == r ? 1 : Math.Max(1, 2 * r);
                for (int dx = -r; dx <= r; dx += step)
                {
                    int x = x0 + dx;
                    if (x < 0 || x >= map.W) continue;
                    int i = y * map.W + x;
                    if (!map.Valid[i]) continue;
                    // Skip grossly mismapped pixels (level 2): a vertex would drag its whole triangle there.
                    if (map.DropLevel != null && map.DropLevel[i] >= 2) continue;
                    su = (float)map.X[i] / 65535f;
                    sv = (float)map.Y[i] / 65535f;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the right half (x = w/2 .. w-1) of a 4-channel RGBA image as a (w/2 × h) buffer.
    /// Used for bibo→gen2: the right half of a 4096×4096 bibo overlay is in vanilla UV space.
    /// </summary>
    public static byte[] CropRightHalf(byte[] src, int srcW, int srcH)
    {
        int halfW = srcW / 2;
        var dst = new byte[halfW * srcH * 4];
        for (int y = 0; y < srcH; y++)
            Array.Copy(src, (y * srcW + halfW) * 4, dst, y * halfW * 4, halfW * 4);
        return dst;
    }

    /// <summary>
    /// Expands a gen2 (vanilla) sheet over a full asymmetric sheet, the inverse of <see cref="CropRightHalf"/>: the art
    /// goes into the right half and its mirror into the left. Needed because there are no <c>gen2_to_*</c> transfer maps.
    /// </summary>
    public static byte[] ExpandMirrored(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        for (int y = 0; y < dstH; y++)
        {
            float sy = Math.Clamp((y + 0.5f) / dstH * srcH - 0.5f, 0, srcH - 1);
            int y1 = (int)sy, y2 = Math.Min(y1 + 1, srcH - 1);
            float yf = sy - y1;
            for (int x = 0; x < dstW; x++)
            {
                float u = (x + 0.5f) / dstW;
                // Right half is the vanilla layout as-is; left half is its mirror image.
                float gu = u >= 0.5f ? (u - 0.5f) * 2f : (0.5f - u) * 2f;
                float sx = Math.Clamp(gu * srcW - 0.5f, 0, srcW - 1);
                int x1 = (int)sx, x2 = Math.Min(x1 + 1, srcW - 1);
                float xf = sx - x1;
                int o = (y * dstW + x) * 4;
                for (int c = 0; c < 4; c++)
                    dst[o + c] = (byte)(src[(y1 * srcW + x1) * 4 + c] * (1 - xf) * (1 - yf)
                                      + src[(y1 * srcW + x2) * 4 + c] * xf       * (1 - yf)
                                      + src[(y2 * srcW + x1) * 4 + c] * (1 - xf) * yf
                                      + src[(y2 * srcW + x2) * 4 + c] * xf       * yf
                                      + 0.5f);
            }
        }
        return dst;
    }

    // ── Asymmetry detection ──────────────────────────────────────────────────

    /// <summary>Sampling stride, in source pixels.</summary>
    private const int AsymStride = 4;
    /// <summary>Per-channel difference (of 255) that counts a texel as differing; above compression noise.</summary>
    private const int AsymChannelDelta = 24;
    /// <summary>Fraction of compared texels that must differ before the sheet counts as asymmetric.</summary>
    private const float AsymFraction = 0.001f;
    /// <summary>Floor on the differing count, so a tiny sheet can't trip the fraction on a handful of texels.</summary>
    private const int AsymMinTexels = 64;

    /// <summary>
    /// Whether art painted for <paramref name="space"/> differs between the character's two sides, comparing each
    /// right-half texel with its mirror partner (see <see cref="MirrorU"/>). Restricted to UV islands where a mask is
    /// available, since padding bleed is an export artefact. RGB is weighted by the pair's alpha; alpha always counts.
    /// </summary>
    public bool IsArtAsymmetric(byte[] rgba, int w, int h, string? space)
    {
        // A mirrored space is one sheet both sides share, so the question is meaningless; an unknown space answers false.
        if (space == null || DoubledSpaceOf(space) != null) return false;
        if (w < 8 || h < 8 || rgba.Length < w * h * 4) return false;

        var island = IslandMask(space, out int mw, out int mh, loadIfMissing: false);
        int compared = 0, differing = 0;
        for (int y = 0; y < h; y += AsymStride)
        {
            for (int x = w / 2; x < w; x += AsymStride)
            {
                int mx = w - 1 - x;   // the mirror partner, about the sheet's centre
                if (island != null)
                {
                    int ix = x * mw / w, iy = y * mh / h;
                    if (!island[iy * mw + ix]) continue;
                }
                int a = (y * w + x) * 4, b = (y * w + mx) * 4;
                compared++;
                int aA = rgba[a + 3], bA = rgba[b + 3];
                int d = Math.Abs(aA - bA);
                int cover = Math.Max(aA, bA);
                for (int c = 0; c < 3; c++)
                    d = Math.Max(d, Math.Abs(rgba[a + c] - rgba[b + c]) * cover / 255);
                if (d >= AsymChannelDelta) differing++;
            }
        }
        if (compared == 0) return false;
        return differing >= AsymMinTexels && differing >= compared * AsymFraction;
    }

    // ── Map cache ────────────────────────────────────────────────────────────

    /// <summary>
    /// Which pixels of a body's texture lie inside a UV island, at the transfer map's resolution, derived from a map
    /// whose destination is this body type. Null for body types we ship no map for (treat every pixel as inside).
    /// </summary>
    /// <param name="loadIfMissing">
    /// When false, only an already-loaded map is used and a cache miss returns null instead of loading from disk.
    /// </param>
    public bool[]? IslandMask(string bodyType, out int w, out int h, bool loadIfMissing = true)
    {
        w = h = 0;
        // gen2 is a crop of bibo space with no *_to_gen2 map; bail before GetMap logs "transfer map not found".
        if (string.Equals(bodyType, "gen2", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var from in new[] { "bibo", "gen3" })
        {
            if (string.Equals(from, bodyType, StringComparison.OrdinalIgnoreCase)) continue;
            var map = GetMap(from, bodyType, loadIfMissing);
            if (map == null) continue;
            w = map.W;
            h = map.H;
            return map.Valid;
        }
        return null;
    }

    // Latched when the LibTiff codec can't be loaded, so the failure is reported once. Written under cacheLock.
    private bool tiffUnavailable;

    private TransferMap? GetMap(string from, string to, bool loadIfMissing = true)
    {
        var key = (from.ToLowerInvariant(), to.ToLowerInvariant());
        lock (cacheLock)
        {
            if (cache.TryGetValue(key, out var hit)) return hit;
            if (!loadIfMissing) return null;   // peek-only: don't pay the disk load just to check
            if (tiffUnavailable) return null;

            TransferMap? map;
            try
            {
                map = LoadMap(from, to);
            }
            catch (Exception ex) when (IsTiffCodecMissing(ex))
            {
                // The only place the codec's absence can be caught: the CLR resolves LibTiff when it JITs LoadMapRaw.
                // Uncaught it would take down the whole composite; a null map already means "no island mask".
                tiffUnavailable = true;
                log.Error(ex, "[Proteus] TIFF codec unavailable — UV transfer maps are disabled for this " +
                              "session. BitMiracle.LibTiff.NET.dll is missing from the plugin folder, which " +
                              "means a damaged or interrupted install: reinstall Proteus. Compositing " +
                              "continues without island masks (expect softer sibling-body seams).");
                return null;
            }

            if (map != null) cache[key] = map;
            return map;
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> is the CLR failing to load the LibTiff assembly, not a problem with a map file
    /// (matched by name, since both arrive as the same exception types). Guards against one bad .tif latching the codec off.
    /// </summary>
    private static bool IsTiffCodecMissing(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            var name = e switch
            {
                FileNotFoundException f    => f.FileName,
                FileLoadException f        => f.FileName,
                BadImageFormatException f  => f.FileName,
                TypeLoadException t        => t.TypeName,
                _                          => null,
            };
            if (name?.Contains("LibTiff", StringComparison.OrdinalIgnoreCase) == true) return true;
        }
        return false;
    }

    // Loads the forward map (from→to) and, when the reverse map is available, flags destination pixels whose
    // forward→reverse round trip doesn't return near the start: those are unreliable UV-island seams.
    private TransferMap? LoadMap(string from, string to)
    {
        var fwd = LoadMapRaw(from, to);
        if (fwd == null) return null;

        var rev = LoadMapRaw(to, from);
        if (rev != null)
        {
            // Flag round-trip-inconsistent pixels by severity; the drop decision is made later against actual coverage.
            //   level 1 — moderate (softThresh..ultraThresh)
            //   level 2 — gross    (≥ ultraThresh): nothing legitimate maps 1/8 of the texture away
            float softThresh  = Math.Max(16f, fwd.W / 128f);  // ~32px  @4096
            float ultraThresh = fwd.W / 8f;                   // ~512px @4096
            float softT2 = softThresh * softThresh, ultraT2 = ultraThresh * ultraThresh;
            var dropLevel = new byte[fwd.Valid.Length];
            int moderate = 0, gross = 0;
            for (int i = 0; i < fwd.Valid.Length; i++)
            {
                if (!fwd.Valid[i]) continue;
                // Forward: this dest pixel → source coord in from-space, the reverse map's index space.
                int sx = (int)((float)fwd.X[i] / 65535f * (rev.W - 1) + 0.5f);
                int sy = (int)((float)fwd.Y[i] / 65535f * (rev.H - 1) + 0.5f);
                int qi = sy * rev.W + sx;
                // No reverse correspondence (a gutter): the round trip can't be measured, so keep it.
                if (!rev.Valid[qi]) continue;
                // Reverse: that from-space pixel → coord back in to-space (this map's index space).
                float bx = (float)rev.X[qi] / 65535f * (fwd.W - 1);
                float by = (float)rev.Y[qi] / 65535f * (fwd.H - 1);
                float dx = bx - (i % fwd.W);
                float dy = by - (i / fwd.W);
                float e2 = dx * dx + dy * dy;
                if (e2 > ultraT2)     { dropLevel[i] = 2; gross++; }
                else if (e2 > softT2) { dropLevel[i] = 1; moderate++; }
            }
            fwd.DropLevel = (moderate + gross) > 0 ? dropLevel : null;

            // Mark valid pixels within BorderReach of a UV-island border in either the destination or the mapped
            // source space, since seams originate from either side.
            const int BorderReach = 150;

            // Valid pixels within `reach` of an invalid one, via a summed-area table of the invalid mask.
            static bool[] NearBorderMask(bool[] valid, int w, int h, int reach)
            {
                int sw = w + 1;
                var invSat = new int[sw * (h + 1)];
                for (int y = 0; y < h; y++)
                {
                    int above = y * sw, cur = (y + 1) * sw, rowSum = 0, row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        rowSum += valid[row + x] ? 0 : 1;
                        invSat[cur + x + 1] = invSat[above + x + 1] + rowSum;
                    }
                }
                var nb = new bool[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (!valid[i]) continue;
                        int x0 = Math.Max(0, x - reach), x1 = Math.Min(w - 1, x + reach);
                        int y0 = Math.Max(0, y - reach), y1 = Math.Min(h - 1, y + reach);
                        int invalid = invSat[(y1 + 1) * sw + (x1 + 1)] - invSat[y0 * sw + (x1 + 1)]
                                    - invSat[(y1 + 1) * sw + x0] + invSat[y0 * sw + x0];
                        if (invalid > 0) nb[i] = true;
                    }
                return nb;
            }

            var destNB = NearBorderMask(fwd.Valid, fwd.W, fwd.H, BorderReach);
            var srcNB  = NearBorderMask(rev.Valid, rev.W, rev.H, BorderReach);
            var nearBorder = new bool[fwd.Valid.Length];
            for (int i = 0; i < fwd.Valid.Length; i++)
            {
                if (!fwd.Valid[i]) continue;
                if (destNB[i]) { nearBorder[i] = true; continue; }
                // Mapped source (e.g. gen3) location for this destination pixel.
                int sx = (int)((float)fwd.X[i] / 65535f * (rev.W - 1) + 0.5f);
                int sy = (int)((float)fwd.Y[i] / 65535f * (rev.H - 1) + 0.5f);
                if (srcNB[sy * rev.W + sx]) nearBorder[i] = true;
            }
            fwd.NearBorder = nearBorder;
        }
        return fwd;
    }

    private TransferMap? LoadMapRaw(string from, string to)
    {
        var filename = $"{from.ToLowerInvariant()}_to_{to.ToLowerInvariant()}_transfer.tif";
        var path = Path.Combine(mapsDir, filename);
        if (!File.Exists(path))
        {
            log.Warning("[Proteus] UV transfer map not found: {0}", path);
            return null;
        }

        log.Information("[Proteus] Loading UV transfer map {0} ...", filename);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var tiff = Tiff.Open(path, "r");
            if (tiff == null)
            {
                log.Error("[Proteus] Failed to open TIFF: {0}", path);
                return null;
            }

            int w = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            int h = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
            int n = w * h;

            var mapX  = new ushort[n];
            var mapY  = new ushort[n];
            var valid = new bool[n];

            int scanlineBytes = tiff.ScanlineSize();
            var scanline = new byte[scanlineBytes];

            for (int row = 0; row < h; row++)
            {
                tiff.ReadScanline(scanline, row);
                int rowOff = row * w;
                for (int x = 0; x < w; x++)
                {
                    int si = x * 8; // 8 bytes per RGBA16 pixel
                    mapX [rowOff + x] = BitConverter.ToUInt16(scanline, si);
                    mapY [rowOff + x] = BitConverter.ToUInt16(scanline, si + 2);
                    valid[rowOff + x] = BitConverter.ToUInt16(scanline, si + 6) > 0;
                }
            }

            sw.Stop();
            log.Information("[Proteus] UV map loaded {0}×{1} in {2:F1}s", w, h, sw.Elapsed.TotalSeconds);
            return new TransferMap(mapX, mapY, valid, w, h);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] Failed to load UV transfer map: {0}", path);
            return null;
        }
    }

    // ── Remap ────────────────────────────────────────────────────────────────

    private static byte[] ApplyRemap(byte[] src, int srcW, int srcH, TransferMap map)
    {
        int dstW = map.W, dstH = map.H;
        var dst = new byte[dstW * dstH * 4];

        for (int dy = 0; dy < dstH; dy++)
        {
            for (int dx = 0; dx < dstW; dx++)
            {
                int idx = dy * dstW + dx;
                if (!map.Valid[idx]) continue; // leave transparent (zero-initialised)

                float srcXf = (float)map.X[idx] / 65535f * (srcW - 1);
                float srcYf = (float)map.Y[idx] / 65535f * (srcH - 1);

                int x1 = (int)srcXf;
                int y1 = (int)srcYf;
                int x2 = Math.Min(x1 + 1, srcW - 1);
                int y2 = Math.Min(y1 + 1, srcH - 1);
                float xf = srcXf - x1;
                float yf = srcYf - y1;

                int dstOff = idx * 4;
                for (int c = 0; c < 4; c++)
                {
                    float topLeft     = src[(y1 * srcW + x1) * 4 + c];
                    float topRight    = src[(y1 * srcW + x2) * 4 + c];
                    float bottomLeft  = src[(y2 * srcW + x1) * 4 + c];
                    float bottomRight = src[(y2 * srcW + x2) * 4 + c];
                    dst[dstOff + c] = (byte)(topLeft     * (1 - xf) * (1 - yf)
                                           + topRight    * xf       * (1 - yf)
                                           + bottomLeft  * (1 - xf) * yf
                                           + bottomRight * xf       * yf
                                           + 0.5f);
                }
            }
        }

        return dst;
    }

    // Computes which seam pixels to drop, from the final post-mask coverage `decision`:
    //   • both opposing sides transparent → a sliver stranded in bare skin → SHOULDER, dropped readily.
    //   • coverage on a side → the garment continues across → INTERNAL, kept unless nearly isolated.
    // Returns a per-pixel drop mask (true = remove), or null when nothing is flagged.
    private const int   EmptyRadius      = 8;     // dest neighbourhood half-size, dest pixels
    private const int   SideReach        = 10;    // how far across the boundary to test, dest px
    private const int   SideThick        = 3;     // perpendicular half-thickness of a side block
    private const int   SideOffset       = 2;     // skip pixels nearest the centre (the sliver)
    private const float SideBlackFrac    = 0.10f; // a side block is "transparent" below this cover
    private const float ShoulderMaxCover = 0.97f; // both-sides-bare: drop unless almost fully covered
    private const float InternalMaxCover = 0.05f; // coverage adjacent: drop only when nearly isolated
    public bool[]? ComputeSeamDropMask(byte[] decision, int w, int h, string? from, string? to)
    {
        if (from == null || to == null || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return null;
        var map = GetMap(from, to);
        var lvl = map?.DropLevel;
        var nearBorder = map?.NearBorder;
        if (lvl == null || nearBorder == null) return null;
        int mw = map!.W, mh = map.H, n = w * h;

        var a0 = new byte[n];
        for (int i = 0; i < n; i++) a0[i] = decision[i * 4 + 3];

        // Summed-area table of binary coverage (alpha>16), so every box query is O(1).
        int sw = w + 1;
        var sat = new int[sw * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            int above = y * sw, cur = (y + 1) * sw, rowSum = 0, srcRow = y * w;
            for (int x = 0; x < w; x++)
            {
                rowSum += a0[srcRow + x] > 16 ? 1 : 0;
                sat[cur + x + 1] = sat[above + x + 1] + rowSum;
            }
        }
        (int cov, int tot) Box(int x0, int x1, int y0, int y1)
        {
            x0 = Math.Max(0, x0); x1 = Math.Min(w - 1, x1);
            y0 = Math.Max(0, y0); y1 = Math.Min(h - 1, y1);
            if (x1 < x0 || y1 < y0) return (0, 0);
            int cov = sat[(y1 + 1) * sw + (x1 + 1)] - sat[y0 * sw + (x1 + 1)]
                    - sat[(y1 + 1) * sw + x0] + sat[y0 * sw + x0];
            return (cov, (x1 - x0 + 1) * (y1 - y0 + 1));
        }
        bool Transparent(int x0, int x1, int y0, int y1)
        {
            var (cov, tot) = Box(x0, x1, y0, y1);
            return tot == 0 || cov < tot * SideBlackFrac;
        }

        bool[]? mask = null;
        for (int idx = 0; idx < n; idx++)
        {
            if (a0[idx] == 0) continue;
            int cx = idx % w, cy = idx / w;

            // Confine the cleanup to near UV-island borders; elsewhere there is only genuine detail.
            int mx = mw == w ? cx : cx * mw / w;
            int my = mh == h ? cy : cy * mh / h;
            int mi = my * mw + mx;
            if (!nearBorder[mi]) continue;

            // Both opposing sides bare → SHOULDER. Tested on every covered pixel: the shoulder bleed is often unflagged.
            bool left  = Transparent(cx - SideReach, cx - SideOffset, cy - SideThick, cy + SideThick);
            bool right = Transparent(cx + SideOffset, cx + SideReach, cy - SideThick, cy + SideThick);
            bool up    = Transparent(cx - SideThick, cx + SideThick, cy - SideReach, cy - SideOffset);
            bool down  = Transparent(cx - SideThick, cx + SideThick, cy + SideOffset, cy + SideReach);
            bool shoulder = (left && right) || (up && down);

            // Non-shoulder pixels are candidates only when round-trip-flagged.
            bool flagged = lvl[mi] != 0;
            if (!shoulder && !flagged) continue;

            var (covered, total) = Box(cx - EmptyRadius, cx + EmptyRadius, cy - EmptyRadius, cy + EmptyRadius);
            float thr = shoulder ? ShoulderMaxCover : InternalMaxCover;
            if (covered < total * thr)
            {
                mask ??= new bool[n];
                mask[idx] = true;
            }
        }
        return mask;
    }

    /// <summary>
    /// Area-average ("box") reduction of an RGBA8 buffer: each destination texel is the mean of the source rectangle it
    /// covers, which avoids the aliasing bilinear and nearest give when shrinking. Unweighted (no premultiply), since a
    /// normal map's RGB is a direction that alpha weighting would corrupt.
    /// </summary>
    public static byte[] ResizeBox(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        if (srcW == dstW && srcH == dstH) return src;
        var dst = new byte[Math.Max(0, dstW) * Math.Max(0, dstH) * 4];
        // A zero-dimension source would divide by zero below.
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0) return dst;

        void Row(int dy)
        {
            // Half-open source span, never empty: an axis larger than the source degrades to point sampling.
            int y0 = (int)((long)dy * srcH / dstH);
            int y1 = (int)(((long)dy + 1) * srcH / dstH);
            if (y1 <= y0) y1 = y0 + 1;
            if (y1 > srcH) y1 = srcH;

            for (int dx = 0; dx < dstW; dx++)
            {
                int x0 = (int)((long)dx * srcW / dstW);
                int x1 = (int)(((long)dx + 1) * srcW / dstW);
                if (x1 <= x0) x1 = x0 + 1;
                if (x1 > srcW) x1 = srcW;

                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int rowOff = sy * srcW * 4;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int si = rowOff + sx * 4;
                        r += src[si];
                        g += src[si + 1];
                        b += src[si + 2];
                        a += src[si + 3];
                        n++;
                    }
                }

                int di = (dy * dstW + dx) * 4;
                dst[di]     = (byte)((r + n / 2) / n);
                dst[di + 1] = (byte)((g + n / 2) / n);
                dst[di + 2] = (byte)((b + n / 2) / n);
                dst[di + 3] = (byte)((a + n / 2) / n);
            }
        }

        // Rows are independent, so partitioning by row is byte-identical to the serial form.
        if (dstH * dstW < 256 * 256 || Environment.ProcessorCount < 2)
            for (int dy = 0; dy < dstH; dy++) Row(dy);
        else
            Parallel.For(0, dstH, Row);
        return dst;
    }

    public static byte[] ResizeBilinear(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        if (srcW == dstW && srcH == dstH) return src;
        var dst = new byte[dstW * dstH * 4];
        float xScale = (float)srcW / dstW;
        float yScale = (float)srcH / dstH;

        void Row(int dy)
        {
            for (int dx = 0; dx < dstW; dx++)
            {
                float srcXf = Math.Clamp((dx + 0.5f) * xScale - 0.5f, 0, srcW - 1);
                float srcYf = Math.Clamp((dy + 0.5f) * yScale - 0.5f, 0, srcH - 1);
                int x1 = (int)srcXf, y1 = (int)srcYf;
                int x2 = Math.Min(x1 + 1, srcW - 1), y2 = Math.Min(y1 + 1, srcH - 1);
                float xf = srcXf - x1, yf = srcYf - y1;
                int dstOff = (dy * dstW + dx) * 4;
                for (int c = 0; c < 4; c++)
                {
                    float topLeft     = src[(y1 * srcW + x1) * 4 + c];
                    float topRight    = src[(y1 * srcW + x2) * 4 + c];
                    float bottomLeft  = src[(y2 * srcW + x1) * 4 + c];
                    float bottomRight = src[(y2 * srcW + x2) * 4 + c];
                    dst[dstOff + c] = (byte)(topLeft     * (1 - xf) * (1 - yf)
                                           + topRight    * xf       * (1 - yf)
                                           + bottomLeft  * (1 - xf) * yf
                                           + bottomRight * xf       * yf
                                           + 0.5f);
                }
            }
        }

        // Parallelised: this runs on the composite's critical path whenever an overlay is smaller than its sheet.
        if (dstH * dstW < 256 * 256 || Environment.ProcessorCount < 2)
            for (int dy = 0; dy < dstH; dy++) Row(dy);
        else
            Parallel.For(0, dstH, Row);
        return dst;
    }
}
