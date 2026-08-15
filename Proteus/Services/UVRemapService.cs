using System;
using System.Collections.Generic;
using System.IO;
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
        // Dest pixels with moderate round-trip error (UV-seam region). Dropped by ApplyRemap
        // only where the local neighbourhood is sparse, so stranded seam coverage goes but
        // genuine garment edges (which read as ~half covered) are left intact.
        public byte[]? DropLevel;
        // Valid dest pixels within BorderReach px of a UV-island border (a valid↔invalid edge).
        // The seam-drop is confined to these, so interior fine detail (e.g. a fishnet's net lines,
        // which read as stranded slivers between holes) is never touched.
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
        // _a/_b only mean body UV types when under /obj/body/ — equipment paths also end in _a/_b.
        // _a = vanilla (gen2 UV), _b = gen3 UV (used by body mods like AB Body, SPS gen3, etc.)
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
        // Timed from the top so a first-call transfer-map load is attributed to remap, not to blend.
        RemapStats.Stop(t0);
        return result;
    }

    /// <summary>
    /// Recomposite instrumentation: time spent in <see cref="Remap"/> (including a first-call
    /// transfer-map load). Reset and read by <see cref="CompositorService"/> around one run.
    /// </summary>
    public readonly PhaseCounter RemapStats = new();

    /// <summary>
    /// A per-VERTEX UV converter — the geometry counterpart of <see cref="Remap"/>, which moves pixels.
    /// Takes a UV authored in <paramref name="from"/> space and answers where that same point on the body
    /// sits in <paramref name="to"/> space, or null for a point the maps have no correspondence for.
    /// Returns null outright when the pair needs no conversion or no transfer map covers it, in which case
    /// the caller must leave its UVs alone.
    /// <para/>
    /// The map that converts a UV OUT of space S is the one whose DESTINATION is S — the reverse of the
    /// one <see cref="Remap"/> uses to move art INTO S — because a transfer map is indexed by its
    /// destination pixel and stores the source coordinate. gen2 is not a transfer-map space at all: it is
    /// the right half of bibo (see <see cref="CropRightHalf"/>), so it enters and leaves by halving U.
    /// </summary>
    public Func<float, float, (float U, float V)?>? UvConverter(string? from, string? to)
    {
        if (from == null || to == null) return null;
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return null;

        bool fromGen2 = string.Equals(from, "gen2", StringComparison.OrdinalIgnoreCase);
        bool toGen2   = string.Equals(to,   "gen2", StringComparison.OrdinalIgnoreCase);
        var srcSpace  = fromGen2 ? "bibo" : from;   // gen2 is already in bibo space after the affine
        var dstSpace  = toGen2   ? "bibo" : to;

        // Null when both ends land in bibo (gen2↔bibo) — then the affine IS the whole conversion.
        TransferMap? map = null;
        if (!string.Equals(srcSpace, dstSpace, StringComparison.OrdinalIgnoreCase))
        {
            map = GetMap(dstSpace, srcSpace);   // indexed by a srcSpace pixel, stores the dstSpace coord
            if (map == null) return null;
        }

        // The writer rebuilds the same source's vertices once per layer per host, and a miss walks up to
        // UvLookupReach rings — so memoize. Keyed on the exact UV, which repeats bit-for-bit across those
        // rebuilds, giving a full hit rate after the first pass. Concurrent because the converter outlives
        // one Build call and composites parallelise elsewhere.
        var memo = new System.Collections.Concurrent.ConcurrentDictionary<(float U, float V), (float U, float V)?>();
        return (u, v) => memo.GetOrAdd((u, v), static (key, s) =>
        {
            var (u, v) = key;
            if (s.FromGen2) u = 0.5f + u * 0.5f;
            if (s.Map != null && !TryLookupUv(s.Map, u, v, out u, out v)) return null;
            if (s.ToGen2)
            {
                // Vanilla space is only the RIGHT half of bibo; the left half is bibo's own added detail
                // area and has no vanilla home at all. Without this guard the affine hands such a point a
                // NEGATIVE u, which the sampler wraps to the far edge of the sheet — so the triangle spans
                // the whole texture, survives the coverage trim (its box trips AnyVisible's bail-out) and
                // renders as a smeared band. Report it unmapped instead: the vertex then keeps its
                // authored UV like any other point the maps can't place.
                if (u < 0.5f) return null;
                u = (u - 0.5f) * 2f;
            }
            return (u, v);
        }, (Map: map, FromGen2: fromGen2, ToGen2: toGen2));
    }

    /// <summary>
    /// How far <see cref="TryLookupUv"/> widens its search, in map pixels (~1.2% of a 4096 map). Vertices
    /// sit ON island borders at least as often as inside them, and a border pixel's own cell is often
    /// outside every island, so an exact hit is not enough. Bounded because past this the body really is
    /// unmapped there, and snapping a vertex across that gap would smear its triangles over the texture.
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
                // Interior rows contribute only their two edge columns — the rest were covered by
                // a smaller r, so each ring visits its perimeter and nothing else.
                int step = Math.Abs(dy) == r ? 1 : Math.Max(1, 2 * r);
                for (int dx = -r; dx <= r; dx += step)
                {
                    int x = x0 + dx;
                    if (x < 0 || x >= map.W) continue;
                    int i = y * map.W + x;
                    if (!map.Valid[i]) continue;
                    // A grossly mismapped pixel (level 2) points a third of the texture away. Art can
                    // absorb that as one stray texel; a VERTEX drags its whole triangle there, so the
                    // search steps over those rather than snapping to one.
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

    // ── Map cache ────────────────────────────────────────────────────────────

    /// <summary>
    /// Which pixels of a body's texture actually lie inside a UV island, at the transfer map's
    /// resolution. Everything outside is padding — and art tools bleed/dilate colour into that padding,
    /// so anything read from there is an artefact of the export, not something the game ever samples.
    ///
    /// Derived from a transfer map whose DESTINATION is this body type (its Valid mask is exactly the
    /// islands). Returns null for body types we ship no map for, in which case callers should assume
    /// every pixel counts.
    /// </summary>
    /// <param name="loadIfMissing">
    /// When false, only an ALREADY-loaded transfer map is used; a cache miss returns null instead of
    /// loading the ~4K map from disk. The colour-set editor passes false so merely opening it (to scan an
    /// index texture) doesn't pay the load — the maps are loaded lazily when sibling synthesis actually
    /// remaps. Callers that get null simply treat every pixel as inside an island.
    /// </param>
    public bool[]? IslandMask(string bodyType, out int w, out int h, bool loadIfMissing = true)
    {
        w = h = 0;
        // gen2 (vanilla) is a right-half crop of bibo space, not a transfer-map destination —
        // we ship no *_to_gen2 map, so there's no island mask to derive. Bail before GetMap
        // logs a spurious "transfer map not found".
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

    // Latched when the LibTiff codec itself can't be loaded, so the failure is reported once rather
    // than per material per composite. Written under cacheLock, like the cache beside it.
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
                // This is the ONLY place the codec's absence can be caught. The CLR resolves
                // BitMiracle.LibTiff.NET when it JITs LoadMapRaw — i.e. on ENTRY, before that method's
                // own try block is in scope — which is why a report of this lands on its first line
                // rather than on Tiff.Open. Left uncaught it escapes IslandMask into RecompositeBody's
                // Parallel.ForEach and takes the whole composite down, turning one missing file into a
                // plugin that does nothing. A null map is already an expected outcome (see IslandMask):
                // every caller treats it as "no island mask", so compositing continues.
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
    /// True when <paramref name="ex"/> is the CLR failing to load the LibTiff ASSEMBLY, as opposed to a
    /// problem with a map file. Matched on the name because the two arrive as the same exception types.
    /// <para/>
    /// A file problem cannot actually reach here today — <see cref="LoadMapRaw"/> ends in a catch-all that
    /// logs and returns null — so this is a guard against that catch ever being narrowed, not against the
    /// current code. Without it, one unreadable .tif could latch the codec off for the whole session.
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

    // Loads the forward map (from→to) and, when the reverse map (to→from) is available,
    // invalidates destination pixels whose forward→reverse round trip doesn't return near
    // the start. Those pixels are where the transfer map is unreliable (typically UV-island
    // seams like the shoulder): the forward map points them at a source location whose true
    // destination is elsewhere, so they pull in coverage that doesn't belong, producing a
    // dyed seam after compositing. Dropping only the inconsistent pixels leaves accurate
    // continuous coverage intact (unlike a blanket boundary erosion, which would gap-seam
    // garments that legitimately span two islands).
    private TransferMap? LoadMap(string from, string to)
    {
        var fwd = LoadMapRaw(from, to);
        if (fwd == null) return null;

        var rev = LoadMapRaw(to, from);
        if (rev != null)
        {
            // Flag round-trip-inconsistent pixels by severity, but DON'T invalidate them here —
            // the drop decision is made later against the actual coverage so the garment interior
            // is spared. A gross (≥512px) mismap buried inside the garment (an internal seam) must
            // be kept; only one adjacent to bare skin (the shoulder bleed) is dropped.
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
                // Forward: this dest pixel (in to-space) → source coord in from-space,
                // which is the index space of the reverse map.
                int sx = (int)((float)fwd.X[i] / 65535f * (rev.W - 1) + 0.5f);
                int sy = (int)((float)fwd.Y[i] / 65535f * (rev.H - 1) + 0.5f);
                int qi = sy * rev.W + sx;
                // Source lands where the reverse map has no correspondence (a gutter near an
                // island edge). Can't measure the round trip — keep it, so legitimate garment
                // edges aren't carved away.
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

            // Mark valid pixels within BorderReach of a UV-island border, so the seam-drop only
            // operates near borders and leaves interior detail alone. Check BOTH UVs: a pixel is
            // near a border if it's near a DESTINATION (this map's) island edge OR its mapped
            // SOURCE location is near a source island edge — seams originate from either side.
            const int BorderReach = 150;

            // Near-border mask for a validity grid: valid pixels within `reach` of an invalid one.
            // Uses a summed-area table of the INVALID mask so each box query is O(1).
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

    // Computes which flagged seam pixels to drop, from the FINAL composited (post-mask) coverage.
    // For each flagged (moderate round-trip error) pixel it looks across the boundary:
    //   • both opposing sides totally transparent  → the bleed is stranded in bare skin
    //     (the shoulder seam) → SHOULDER scenario, dropped readily (ShoulderMaxCover).
    //   • coverage present on a side               → the garment continues across the boundary
    //     (an internal seam) → INTERNAL scenario, kept unless nearly isolated (InternalMaxCover).
    // Returns a per-pixel drop mask (true = remove) so the caller can apply it to every coverage
    // buffer consistently, or null when nothing is flagged. `decision` is the post-mask coverage;
    // analysing it (not the raw remap) means masked-out regions read as the bare skin they are.
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

        // Summed-area table of binary coverage (alpha>16), zero-padded, so every box query — the
        // side tests and the local-coverage test — is O(1). Lets us classify EVERY covered pixel,
        // not only round-trip-flagged ones: the shoulder bleed maps cleanly (low error, unflagged)
        // yet sits stranded in bare skin, so it must be reachable by the both-sides-bare test.
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

            // Confine the cleanup to within ~10px of a UV-island border. Away from borders there
            // are no seam artefacts, only genuine detail (e.g. fishnet net lines), which must be
            // left intact.
            int mx = mw == w ? cx : cx * mw / w;
            int my = mh == h ? cy : cy * mh / h;
            int mi = my * mw + mx;
            if (!nearBorder[mi]) continue;

            // Both opposing sides bare → a sliver stranded in bare skin → SHOULDER. This is tested
            // on every covered pixel because the shoulder bleed is usually NOT round-trip-flagged.
            bool left  = Transparent(cx - SideReach, cx - SideOffset, cy - SideThick, cy + SideThick);
            bool right = Transparent(cx + SideOffset, cx + SideReach, cy - SideThick, cy + SideThick);
            bool up    = Transparent(cx - SideThick, cx + SideThick, cy - SideReach, cy - SideOffset);
            bool down  = Transparent(cx - SideThick, cx + SideThick, cy + SideOffset, cy + SideReach);
            bool shoulder = (left && right) || (up && down);

            // Non-shoulder pixels are only candidates for the gentle internal-seam drop when they
            // are round-trip-flagged (a UV-island boundary); otherwise they're plain garment, kept.
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

    public static byte[] ResizeBilinear(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        if (srcW == dstW && srcH == dstH) return src;
        var dst = new byte[dstW * dstH * 4];
        float xScale = (float)srcW / dstW;
        float yScale = (float)srcH / dstH;
        for (int dy = 0; dy < dstH; dy++)
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
        return dst;
    }
}
