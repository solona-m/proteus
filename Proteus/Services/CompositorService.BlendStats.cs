using System;
using System.Collections.Generic;
using System.Linq;
using Penumbra.Api.Enums;

namespace Proteus.Services;

using static Proteus.Services.IslandBlur;

using static Proteus.Services.OverlayBlend;

public partial class CompositorService
{
    // ── Blend sub-phases ──────────────────────────────────────────────────────
    // "blend" in the phases line is a residual (composite wall time minus measured stages); these counters attribute it.
    // Materials composite in parallel, so these sum across workers.
    private readonly PhaseCounter blendIslandStats = new();
    private readonly PhaseCounter blendSeamStats   = new();
    private readonly PhaseCounter blendAoStats     = new();
    private readonly PhaseCounter blendTagStats    = new();

    // Inside AO: the garment silhouette rebuild and the island-restricted blur.
    private readonly PhaseCounter blendSilhouetteStats = new();
    private readonly PhaseCounter blendBlurStats       = new();
    private readonly PhaseCounter blendBlurCacheHits   = new();   // of blur's calls, those served by aoBlurCache

    // Mutually exclusive regions covering the material task; what survives them is per-material setup.
    private readonly PhaseCounter blendOverlayStats     = new();
    private readonly PhaseCounter blendMaskReliefStats  = new();
    private readonly PhaseCounter blendMaskDiffuseStats = new();

    // Per-kernel counters inside the overlay loop, timed at the static definitions to catch every call site.
    // Exclusivity is the invariant: overlayGlueMs subtracts all of them, so nesting kernels (LoadIndexMerged, Suppress)
    // time only their own exclusive region. Anything added here must keep it.
    internal static readonly PhaseCounter blendCovStats      = new();   // coverage build: clone-heavy leaves
    internal static readonly PhaseCounter blendDiffuseStats  = new();   // full-4K diffuse composites
    internal static readonly PhaseCounter blendNormalStats   = new();   // full-4K normal recombine
    private readonly PhaseCounter blendIdxMergeStats        = new();   // index clone + serial mask merge
    private readonly PhaseCounter blendSeamDropStats        = new();   // summed-area table, serial

    // The load path, the two remaining buffer-touching closures and upstream resolution. CombinedMaskAt is not among
    // them: it calls RemapIfNeeded, which would double-count against `load`.
    private readonly PhaseCounter blendLoadStats     = new();   // LoadPng + RemapIfNeeded
    private readonly PhaseCounter blendGen2Stats     = new();   //   of which: the vanilla-sibling crop, cached or not
    private readonly PhaseCounter blendBaseLoadStats = new();   // LoadBaseTexture
    private readonly PhaseCounter blendResolveStats  = new();   // ResolveUpstream
    private readonly PhaseCounter blendSuppressStats = new();   // Suppress: clone + SERIAL full-buffer pass

    private void ResetBlendStats()
    {
        blendIslandStats.Reset();
        blendSeamStats.Reset();
        blendAoStats.Reset();
        blendTagStats.Reset();
        blendSilhouetteStats.Reset();
        blendBlurStats.Reset();
        blendOverlayStats.Reset();
        blendMaskReliefStats.Reset();
        blendMaskDiffuseStats.Reset();
        blendCovStats.Reset();
        blendDiffuseStats.Reset();
        blendNormalStats.Reset();
        blendIdxMergeStats.Reset();
        blendSeamDropStats.Reset();
        blendLoadStats.Reset();
        blendGen2Stats.Reset();
        blendBlurCacheHits.Reset();
        blendBaseLoadStats.Reset();
        blendResolveStats.Reset();
        blendSuppressStats.Reset();
    }

    /// <summary>
    /// Where a bridged garment has lifted clear of the bust, as a body-UV suppression map for the skindent
    /// (<see cref="BodyBridge.BustStandoffMap"/>). Null for a mod with no bust bridge. Blurred by the indent's own radius,
    /// since the raw map's edge follows the triangulation. Cached by mod, body model identity, size and radius; the
    /// garment silhouette is deliberately not in the key.
    /// </summary>
    private byte[]? BustStandoff(string modDir, IReadOnlyList<UvSeamMapService.SeamModel>? models,
                                 byte[]? coverage, int w, int h, int radius)
    {
        if (models is not { Count: > 0 } || w <= 0 || w != h) return null;
        var entry = discovery.DiscoverAll().FirstOrDefault(e =>
            string.Equals(e.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase));
        if (entry?.Metadata.BustBridge != true) return null;
        float strength = Math.Clamp(entry.Metadata.BustBridgeStrength ?? 1f, 0f, 1f);
        if (strength <= 0f) return null;

        var key = $"{modDir}\0{w}\0{radius}\0{strength}\0{string.Join("|", models.Select(m => m.Id))}";
        if (_bustStandoff.TryGetValue(key, out var hit)) return hit;

        var bodies = new List<byte[]>(models.Count);
        foreach (var m in models)
        {
            try { if (m.Load() is { Length: > 0 } b) bodies.Add(b); }
            catch { /* unreadable body — the map simply covers less */ }
        }
        var map = bodies.Count == 0
            ? null
            : BodyBridge.BustStandoffMap(bodies, coverage, w, h, strength, w, BustStandoffFull,
                                               msg => log.Debug("[Proteus] bust standoff: {0}", msg));
        // Grow first, then feather: a texel's shadow is cast by cloth within reach, so suppression takes the neighbourhood
        // maximum. The halo reaches 2r and a one-pass feather bites r, so grow by 3r.
        if (map != null && radius >= 1)
        {
            map = MaxFilter(map, w, h, radius * (BlurCoveragePasses + 1));
            map = BlurCoverage(map, w, h, radius, iterations: 1);
        }
        _bustStandoff[key] = map;
        log.Debug("[Proteus] bust standoff: {0} for {1} at {2}, feathered by {3} ({4} body model(s))",
                  map == null ? "no map" : "built", modDir, w, radius, bodies.Count);
        return map;
    }

    /// <summary>
    /// Lift at which the skindent is fully suppressed, in model units. Above the shell's resting offset
    /// (<c>SecondSkinWriter.BaseOffset</c>), so cloth lying on the skin still marks it.
    /// </summary>
    private const float BustStandoffFull = 0.0015f;

    private readonly Dictionary<string, byte[]?> _bustStandoff = new(StringComparer.Ordinal);

    /// <summary>
    /// Blur the AO silhouette at half resolution and interpolate back: the halo carries nothing at texel scale.
    /// </summary>
    private const bool AoBlurHalfRes = true;

    private readonly AoBlurCache aoBlurCache = new();

    /// <summary>
    /// The blurred silhouette a garment casts its contact shadow from, from <see cref="aoBlurCache"/> or blurred now (at
    /// half resolution when <see cref="AoBlurHalfRes"/>). Both blur sites go through here so key and resolution agree.
    /// Null islands select the plain blur.
    /// </summary>
    private byte[] BlurSilhouette(byte[] strap, int w, int h, int radius, string? dstBodyType,
                                  byte[]? insidePlane, int[]? islandLabels, int[]? islandOwner, int islandCount,
                                  IReadOnlyList<UvSeamMapService.SeamModel>? bodyMdls,
                                  IslandBlurCache islandBlurCache, HalfResIslandPlanes halfPlanes)
    {
        var t = PhaseCounter.Begin();
        try
        {
            bool islands = insidePlane != null && islandLabels != null && islandOwner != null;
            bool half = AoBlurHalfRes && w % 2 == 0 && h % 2 == 0 && radius >= 4;
            // Everything the result depends on: silhouette content, islands, seam map, radius and resolution.
            var key = $"{dstBodyType ?? "-"}|{w}x{h}|r{radius}|{(islands ? "i" : "p")}|h{(half ? 1 : 0)}|"
                    + (islands && bodyMdls != null ? string.Join(",", bodyMdls.Select(m => m.Id)) : "-")
                    + $"|{SecondSkinService.SlotHash(strap):x16}";
            if (aoBlurCache.TryGet(key) is { } hit) { blendBlurCacheHits.Count(); return hit; }

            byte[] result;
            if (!islands)
            {
                result = half
                    ? HalfResIslandPlanes.UpsampleBilinear(
                          BlurCoverage(HalfResIslandPlanes.DownsampleAverage(strap, w, h), w / 2, h / 2, Math.Max(1, radius / 2)),
                          w / 2, h / 2, w, h)
                    : BlurCoverage(strap, w, h, radius);
            }
            else
            {
                var seam = bodyMdls == null ? null : TimedSeamSource(bodyMdls, w, h, SeamReach(radius));
                if (half)
                {
                    halfPlanes.EnsureIslands(islandLabels!, islandOwner!, insidePlane!, w, h);
                    halfPlanes.EnsureSeam(seam, w, h);
                    var blurredH = BlurCoverageWithinIslands(
                        HalfResIslandPlanes.DownsampleAverage(strap, w, h), halfPlanes.Labels!, halfPlanes.Owner!,
                        islandCount, halfPlanes.Inside!, halfPlanes.Seam, w / 2, h / 2, Math.Max(1, radius / 2),
                        islandBlurCache);
                    result = HalfResIslandPlanes.UpsampleBilinear(blurredH, w / 2, h / 2, w, h);
                }
                else
                    result = BlurCoverageWithinIslands(strap, islandLabels!, islandOwner!, islandCount, insidePlane!,
                                                       seam, w, h, radius, islandBlurCache);
            }
            aoBlurCache.Put(key, result);
            return result;
        }
        finally { blendBlurStats.Stop(t); }
    }

    private int[]? TimedSeamSource(IReadOnlyList<UvSeamMapService.SeamModel> models, int w, int h, int reach)
    {
        var t = PhaseCounter.Begin();
        try { return seamMaps.SeamSource(models, w, h, reach); }
        finally { blendSeamStats.Stop(t); }
    }

    /// <summary>Time a <see cref="ContentTag"/> call (an FNV hash of a whole output buffer, four per material).</summary>
    private string TimedContentTag(byte[] data, params int[] salt)
    {
        var t = PhaseCounter.Begin();
        try { return ContentTag(data, salt); }
        finally { blendTagStats.Stop(t); }
    }

    /// <summary>
    /// The end-to-end line: first trigger to the composite's outcome, attributed span by span. Marks the run completed,
    /// so its triggers are not handed on.
    /// </summary>
    /// <remarks>
    /// When a shell was published, <see cref="SchedulePostRedrawShellCheck"/> adds a "refresh drawn" line later.
    /// </remarks>
    private void LogRefreshTimeline(RefreshTimeline tl, string outcome)
    {
        tl.Completed = true;
        log.Information("[Proteus] refresh timeline: {0:F0}ms from first trigger to {1} ({2} trigger(s): {3}) — {4}",
            tl.TotalMs, outcome, tl.Triggers, tl.Reasons.Length == 0 ? "?" : tl.Reasons, tl.Describe());
    }

    /// <summary>The name of the first differing fingerprint block, cut short.</summary>
    private static string FirstDifferingBlock(string was, string now)
    {
        var a = was.Split('\n');
        var b = now.Split('\n');
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var la = i < a.Length ? a[i] : "";
            var lb = i < b.Length ? b[i] : "";
            if (la == lb) continue;
            var head = lb.Length > 0 ? lb : la;
            int cut = head.IndexOfAny(['{', '#']);
            return cut > 0 && cut < 160 ? head[..cut] : head[..Math.Min(head.Length, 80)];
        }
        return "(identical?)";
    }

    private void LogPhaseBreakdown(long runStart, long setupEnd, int materialCount)
    {
        var totalMs     = PhaseCounter.MsSince(runStart);
        var compositeMs = PhaseCounter.MsSince(setupEnd);
        var setupMs     = totalMs - compositeMs;

        var decode   = textureLoader.DecodeStats;
        var hits     = textureLoader.DecodeHitStats;
        var blocked  = textureLoader.DecodeBlockedStats;
        var nativeD  = textureLoader.DecodeNativeStats;
        var wait     = textureLoader.DecodeWaitStats;
        var prefetch = textureLoader.PrefetchWaitStats;
        var swizzle  = textureLoader.SwizzleStats;
        var write    = textureLoader.WriteStats;
        var remap    = uvRemap.RemapStats;

        // Blend = composite minus stages measured on the composite thread only (decode also runs on prefetch threads).
        // Foreground counters sum across workers, so the split is exact only for one material.
        var blendMs = compositeMs - (wait.Ms + remap.Ms + swizzle.Ms + write.Ms);

        // Cache state beside the miss count shows how far the budget is under the working set.
        var (cacheEntries, cacheBytes) = textureLoader.CacheState();

        // The AO timer contains the seam-map and content-tag work measured separately, so subtract those.
        var aoMs   = Math.Max(0, blendAoStats.Ms - (blendSeamStats.Ms + blendTagStats.Ms));
        // The per-overlay loop and the two Masks passes: mutually exclusive, none overlapping AO. On a cold run `overlay`
        // includes loads also reported as decode-wait/remap, so `rest` is a floor.
        var restMs = Math.Max(0, blendMs - (blendIslandStats.Ms + blendSeamStats.Ms + aoMs + blendTagStats.Ms
                                          + blendOverlayStats.Ms + blendMaskReliefStats.Ms
                                          + blendMaskDiffuseStats.Ms));
        // What the kernel counters do not cover inside the overlay loop: loads, remaps and glue. Clamped like `rest`.
        var overlayGlueMs = Math.Max(0, blendOverlayStats.Ms
                                      - (blendCovStats.Ms + blendIdxMergeStats.Ms + blendDiffuseStats.Ms
                                       + blendNormalStats.Ms + blendSeamDropStats.Ms
                                       + blendLoadStats.Ms + blendBaseLoadStats.Ms + blendResolveStats.Ms
                                       + blendSuppressStats.Ms));
        // The measured pieces inside AO; "apply" is the remainder.
        var aoApplyMs = Math.Max(0, aoMs - (blendSilhouetteStats.Ms + blendBlurStats.Ms));

        log.Information(
            "[Proteus] recomposite phases: setup {0:F0}ms | decode-wait {1:F0}ms ({2} miss, {3} hit, {4} blocked) | " +
            "prefetch {5:F0}ms bg (decode work {6:F0}ms, {7} native of {8}) | remap {9:F0}ms ({10}) | " +
            "blend {11:F0}ms (islands {12:F0} | seam {13:F0}/{14} | ao {15:F0} [sil {16:F0}/{17} + blur {18:F0}/{19} ({59} cached) " +
            "+ apply {20:F0}] | tag {21:F0}/{22} | overlays {23:F0} [cov {24:F0}/{25} + idxmerge {26:F0}/{27} " +
            "+ diffuse {28:F0}/{29} + normal {30:F0}/{31} + seamdrop {32:F0}/{33} + load {34:F0}/{35} (gen2 {57:F0}/{58}) " +
            "+ baseload {36:F0}/{37} + resolve {38:F0}/{39} + suppress {40:F0}/{41} + glue {42:F0}] | " +
            "maskrelief {43:F0} | maskdiffuse {44:F0} | rest {45:F0}) | " +
            "swizzle {46:F0}ms | write {47:F0}ms ({48} files, {49:F0} MB) | composite {50:F0}ms | total {51:F0}ms | " +
            "{52} material(s) | cache {53} entries, {54:F0} MB, {55} evicted (budget {56:F0} MB)",
            setupMs, wait.Ms, decode.Calls, hits.Calls, blocked.Calls,
            prefetch.Ms, decode.Ms, nativeD.Calls, decode.Calls, remap.Ms, remap.Calls,
            blendMs, blendIslandStats.Ms, blendSeamStats.Ms, blendSeamStats.Calls, aoMs,
            blendSilhouetteStats.Ms, blendSilhouetteStats.Calls, blendBlurStats.Ms, blendBlurStats.Calls,
            aoApplyMs, blendTagStats.Ms, blendTagStats.Calls,
            blendOverlayStats.Ms,
            blendCovStats.Ms, blendCovStats.Calls, blendIdxMergeStats.Ms, blendIdxMergeStats.Calls,
            blendDiffuseStats.Ms, blendDiffuseStats.Calls, blendNormalStats.Ms, blendNormalStats.Calls,
            blendSeamDropStats.Ms, blendSeamDropStats.Calls,
            blendLoadStats.Ms, blendLoadStats.Calls, blendBaseLoadStats.Ms, blendBaseLoadStats.Calls,
            blendResolveStats.Ms, blendResolveStats.Calls,
            blendSuppressStats.Ms, blendSuppressStats.Calls, overlayGlueMs,
            blendMaskReliefStats.Ms, blendMaskDiffuseStats.Ms, restMs,
            swizzle.Ms, write.Ms, write.Calls, write.Bytes / (1024.0 * 1024.0),
            compositeMs, totalMs, materialCount,
            cacheEntries, cacheBytes / (1024.0 * 1024.0), textureLoader.Evictions,
            textureLoader.DecodeCacheBudgetBytes / (1024.0 * 1024.0),
            blendGen2Stats.Ms, blendGen2Stats.Calls, blendBlurCacheHits.Calls);
    }
}
