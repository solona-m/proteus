using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Proteus.Interop;

namespace Proteus.Services;

public class CompositorResult
{
    public bool Success { get; init; }
    public int TexturesPatched { get; init; }
    public int OverlayModsUsed { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public class CompositorService : IDisposable
{
    private readonly PenumbraBridge penumbra;
    private readonly SidecarDiscoveryService discovery;
    private readonly TextureLoader textureLoader;
    private readonly Configuration config;
    private readonly IPluginLog log;

    private string modsRoot;
    private string managedModDir;

    private CancellationTokenSource? currentCts;
    private readonly object triggerLock = new();

    public CompositorResult? LastResult { get; private set; }
    public List<OverlayEntry> LastDiscovered { get; private set; } = [];
    public event Action? ResultChanged;

    public CompositorService(
        PenumbraBridge penumbra,
        SidecarDiscoveryService discovery,
        TextureLoader textureLoader,
        Configuration config,
        IPluginLog log)
    {
        this.penumbra = penumbra;
        this.discovery = discovery;
        this.textureLoader = textureLoader;
        this.config = config;
        this.log = log;

        modsRoot      = penumbra.GetModDirectory() ?? string.Empty;
        managedModDir = Path.Combine(modsRoot, SidecarDiscoveryService.ManagedModDir);

        penumbra.ModSettingChanged += OnModSettingChanged;
        penumbra.ModAdded          += OnModAdded;
        penumbra.ModDeleted        += OnModDeleted;
        penumbra.PenumbraReady     += OnPenumbraReady;
    }

    public void Dispose()
    {
        penumbra.ModSettingChanged -= OnModSettingChanged;
        penumbra.ModAdded          -= OnModAdded;
        penumbra.ModDeleted        -= OnModDeleted;
        penumbra.PenumbraReady     -= OnPenumbraReady;

        currentCts?.Cancel();
        currentCts?.Dispose();
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnModSettingChanged(ModSettingChange change, Guid collId, string modDir, bool inherited)
    {
        if (string.Equals(modDir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return;
        if (!HasSidecar(modDir))
            return;
        var playerColl = penumbra.GetPlayerCollectionId();
        if (playerColl == null || collId != playerColl.Value)
            return;
        TriggerRecomposite($"ModSettingChanged:{change}:{modDir}");
    }

    private void OnModAdded(string modDir)
    {
        if (!HasSidecar(modDir)) return;
        TriggerRecomposite($"ModAdded:{modDir}");
    }

    private void OnModDeleted(string modDir)
    {
        if (LastDiscovered.All(e => !string.Equals(e.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase)))
            return;
        TriggerRecomposite($"ModDeleted:{modDir}");
    }

    private void OnPenumbraReady()
    {
        modsRoot      = penumbra.GetModDirectory() ?? string.Empty;
        managedModDir = Path.Combine(modsRoot, SidecarDiscoveryService.ManagedModDir);
        if (!config.PluginEnabled) return;
        // Only trigger if discovery already sees mods. PenumbraReady can fire before Penumbra's
        // mod settings are readable; if discovery returns empty we'd wipe the existing output.
        // Leave previous-session files intact — ModSettingChanged/ModAdded will fire the first
        // real composite once settings are available.
        if (discovery.DiscoverEnabled().Count > 0)
            TriggerRecomposite("PenumbraReady");
    }

    private bool HasSidecar(string modDir)
    {
        var metaPath = Path.Combine(modsRoot, modDir, "Proteus", "metadata.json");
        return File.Exists(metaPath);
    }

    // ── Trigger ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Schedule a recomposite on a background thread.
    /// Any in-flight recomposite is cancelled first (debounce).
    /// </summary>
    public void TriggerRecomposite(string reason)
    {
        if (!config.PluginEnabled || !penumbra.IsAvailable) return;

        CancellationTokenSource cts;
        lock (triggerLock)
        {
            currentCts?.Cancel();
            currentCts?.Dispose();
            cts = currentCts = new CancellationTokenSource();
        }

        log.Debug("[Proteus] Recomposite triggered: {0}", reason);
        var token = cts.Token;
        Task.Run(async () =>
        {
            try { await Task.Delay(500, token); }
            catch (OperationCanceledException) { return; }
            Recomposite(token);
        });
    }

    // ── Core compositor ──────────────────────────────────────────────────────

    private void Recomposite(CancellationToken ct)
    {
        try
        {
            log.Debug("[Proteus] Recomposite START");
            EnsureManagedModExists();

            // Delete previously written files BEFORE clearing redirects so that
            // File.Exists checks fail even if the Penumbra IPC reload is asynchronous.
            // This prevents us from loading our own stale output as the base texture.
            var texturesDirEarly  = Path.Combine(managedModDir, "textures");
            var materialsDirEarly = Path.Combine(managedModDir, "materials");
            if (Directory.Exists(texturesDirEarly))
                foreach (var f in Directory.GetFiles(texturesDirEarly, "*.tex"))
                    try { File.Delete(f); } catch { }
            if (Directory.Exists(materialsDirEarly))
                foreach (var f in Directory.GetFiles(materialsDirEarly, "*.mtrl"))
                    try { File.Delete(f); } catch { }

            // Clear redirects and reload. Penumbra's IPC reload may process asynchronously
            // on the game main thread, so sleep briefly to let it take effect before any
            // ResolvePlayer calls that determine which mod's file is the upstream source.
            WriteManagedModJson(new Dictionary<string, string>());
            penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
            Thread.Sleep(80);

            var allEntries = discovery.DiscoverEnabled();
            if (ct.IsCancellationRequested) return;

            LastDiscovered = allEntries;

            var entries = ApplyOverrides(allEntries);

            if (entries.Count == 0)
            {
                WriteManagedModJson(new Dictionary<string, string>());
                ReloadAndRedraw();
                LastResult = new CompositorResult { Success = true, TexturesPatched = 0, OverlayModsUsed = 0 };
                ResultChanged?.Invoke();
                return;
            }

            // Flatten: (entry, resolvedOverlay) pairs, grouped by material game path
            var byMaterial = new Dictionary<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                var overlays = discovery.ResolveActiveOverlays(entry);
                foreach (var overlay in overlays)
                {
                    foreach (var mtrlPath in overlay.Descriptor.MaterialGamePaths)
                    {
                        if (string.IsNullOrEmpty(mtrlPath)) continue;
                        if (!byMaterial.TryGetValue(mtrlPath, out var list))
                            byMaterial[mtrlPath] = list = new();
                        list.Add((entry, overlay));
                    }
                }
            }

            if (ct.IsCancellationRequested) return;

            var texturesDir  = texturesDirEarly;
            var materialsDir = materialsDirEarly;
            Directory.CreateDirectory(texturesDir);

            var redirects = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int texturesPatched = 0;

            // Unique suffix for all output files in this composite run. FFXIV caches textures
            // by their resolved path; using the same filename across runs means the game never
            // reloads the file even after the content changes. A new suffix each run guarantees
            // Penumbra sees a genuinely different redirect path → forces a cache miss.
            var runId = Guid.NewGuid().ToString("N")[..8];

            // Process all race-variant materials in parallel — each is independent.
            // PNG cache is per-iteration (not shared) so each thread decodes at its own dimensions.
            Parallel.ForEach(byMaterial, new ParallelOptions { CancellationToken = ct }, kvp =>
            {
                var (mtrlGamePath, pairs) = kvp;

                var pngCache = new Dictionary<(string path, int w, int h), byte[]?>();
                byte[]? LoadPng(string path, int w, int h)
                {
                    var key = (path, w, h);
                    if (!pngCache.TryGetValue(key, out var data))
                        pngCache[key] = data = textureLoader.LoadPngAsRgba(path, w, h);
                    return data;
                }

                if (ct.IsCancellationRequested) return;

                var mtrlDisk = penumbra.ResolvePlayer(mtrlGamePath);
                var texPaths = (mtrlDisk != null && File.Exists(mtrlDisk))
                    ? textureLoader.ResolveMtrlTextures(mtrlDisk)
                    : textureLoader.ResolveMtrlTexturesFromGame(mtrlGamePath);

                if (texPaths.Diffuse == null && texPaths.Normal == null && texPaths.Mask == null)
                {
                    log.Warning("[Proteus] No textures found for material: {0}", mtrlGamePath);
                    return;
                }

                // If any entry in this material's stack uses emissive, the normal alpha must
                // start at 0 so that only overlay-covered pixels receive emissive intensity.
                // (BC5-decoded normals have alpha=255 everywhere; without this reset, the
                // entire material would glow when the emissive shader key is active.)
                bool anyEmissive = pairs.Any(p =>
                    p.Overlay.ColorTableRows?.Any(r =>
                        r.SubRowA?.Emissive > 0.001f || r.SubRowB?.Emissive > 0.001f) == true);

                byte[]? baseD = null, baseN = null, baseM = null;
                int wD = 0, hD = 0, wN = 0, hN = 0, wM = 0, hM = 0;

                foreach (var (entry, resolved) in pairs)
                {
                    if (ct.IsCancellationRequested) return;

                    var desc   = resolved.Descriptor;
                    var rows   = BuildRowDict(resolved.ColorTableRows);
                    rows.TryGetValue(15, out var row16);
                    var row16A = row16?.A ?? new ColorTableSubRow();

                    byte[]? diffuseOv = null;
                    byte[]? normalOv  = null;

                    // Coverage mask: the alpha of the diffuse overlay defines WHERE this overlay
                    // applies. When there is no diffuse overlay (normal-only), the mask is
                    // synthesized from the normal map's blue channel. Every compositing channel
                    // — diffuse, normal, emissive, mask texture — is gated by this same mask.
                    byte[]? covSrc = null;  // coverage source at (covW × covH)
                    int covW = 0, covH = 0;

                    // ── Step 1: load diffuse overlay (establishes coverage) ───
                    if (desc.Diffuse != null && texPaths.Diffuse != null)
                    {
                        if (baseD == null)
                        {
                            var diffDisk = penumbra.ResolvePlayer(texPaths.Diffuse);
                            var loaded = textureLoader.LoadBaseTexture(diffDisk, texPaths.Diffuse);
                            if (loaded.HasValue) { baseD = loaded.Value.rgba; wD = loaded.Value.width; hD = loaded.Value.height; }
                            baseD ??= Array.Empty<byte>();
                        }
                        if (baseD.Length > 0)
                        {
                            diffuseOv = LoadPng(Path.Combine(entry.SidecarRoot, desc.Diffuse), wD, hD);
                            if (diffuseOv != null)
                            {
                                // Apply per-row opacity to coverage before downstream compositing.
                                // Indexed: blend per-pixel from the index texture (same as diffuse/emissive).
                                // Flat: apply row 16A's opacity uniformly across the whole overlay.
                                if (desc.Index != null && rows.Values.Any(r => r.A.Opacity != 0 || r.B.Opacity != 0))
                                {
                                    var idD = LoadPng(Path.Combine(entry.SidecarRoot, desc.Index), wD, hD);
                                    if (idD != null) diffuseOv = ApplyIndexedOpacity(diffuseOv, idD, rows);
                                }
                                else if (desc.Index == null && row16A.Opacity != 0)
                                    diffuseOv = ScaleOverlayAlpha(diffuseOv, row16A.Opacity);
                                covSrc = diffuseOv; covW = wD; covH = hD;
                            }
                        }
                    }

                    // ── Step 2: load normal overlay; synthesize coverage if needed ──
                    if (desc.Normal != null && texPaths.Normal != null)
                    {
                        if (baseN == null)
                        {
                            baseN = LoadBaseNormal(texPaths.Normal, ref wN, ref hN);
                            if (anyEmissive && baseN.Length > 0)
                                for (int ai = 3; ai < baseN.Length; ai += 4) baseN[ai] = 0;
                        }
                        if (baseN.Length > 0)
                            normalOv = LoadPng(Path.Combine(entry.SidecarRoot, desc.Normal), wN, hN);

                        if (normalOv != null && covSrc == null)
                        {
                            // No diffuse overlay — synthesize coverage from normal blue channel.
                            var synth = new byte[normalOv.Length];
                            for (int si = 0; si < normalOv.Length; si += 4)
                            {
                                synth[si] = synth[si + 1] = synth[si + 2] = 255;
                                synth[si + 3] = normalOv[si + 2]; // blue → opacity
                            }
                            if (desc.Index != null && rows.Values.Any(r => r.A.Opacity != 0 || r.B.Opacity != 0))
                            {
                                var idN = LoadPng(Path.Combine(entry.SidecarRoot, desc.Index), wN, hN);
                                if (idN != null) synth = ApplyIndexedOpacity(synth, idN, rows);
                            }
                            else if (desc.Index == null && row16A.Opacity != 0)
                                synth = ScaleOverlayAlpha(synth, row16A.Opacity);
                            diffuseOv = synth;
                            covSrc = synth; covW = wN; covH = hN;
                        }
                    }

                    if (covSrc == null) continue; // no coverage — nothing to composite

                    // Returns the coverage mask resized to (tw × th) on demand.
                    // Re-loads from the diffuse PNG when possible; falls back to scaling.
                    byte[]? CovAt(int tw, int th)
                    {
                        if (tw == covW && th == covH) return covSrc; // already scaled
                        byte[]? cov = desc.Diffuse != null
                            ? LoadPng(Path.Combine(entry.SidecarRoot, desc.Diffuse), tw, th)
                            : textureLoader.ScaleRgba(covSrc!, covW, covH, tw, th);
                        if (cov != null && desc.Index == null && row16A.Opacity != 0)
                            cov = ScaleOverlayAlpha(cov, row16A.Opacity);
                        return cov;
                    }

                    // ── Phase A: diffuse composite ────────────────────────────
                    if (desc.Diffuse != null && diffuseOv != null && baseD is { Length: > 0 })
                    {
                        if (desc.Index != null)
                        {
                            var idD = LoadPng(Path.Combine(entry.SidecarRoot, desc.Index), wD, hD);
                            if (idD != null) ApplyIndexedOverlay(baseD, diffuseOv, idD, rows, false, wD, hD);
                            else             ApplyFlatOverlay(baseD, diffuseOv, row16A, wD, hD);
                        }
                        else ApplyFlatOverlay(baseD, diffuseOv, row16A, wD, hD);
                    }
                    else if (desc.Diffuse == null && normalOv != null && texPaths.Diffuse != null)
                    {
                        // Normal-only overlay: apply synthesized white tint to the diffuse channel.
                        if (baseD == null)
                        {
                            var diffDisk = penumbra.ResolvePlayer(texPaths.Diffuse);
                            var loaded   = textureLoader.LoadBaseTexture(diffDisk, texPaths.Diffuse);
                            if (loaded.HasValue) { baseD = loaded.Value.rgba; wD = loaded.Value.width; hD = loaded.Value.height; }
                            baseD ??= Array.Empty<byte>();
                        }
                        if (baseD.Length > 0)
                        {
                            var tint = CovAt(wD, hD);
                            if (tint != null) ApplyFlatOverlay(baseD, tint, row16A, wD, hD);
                        }
                    }

                    // ── Phase B: normal composite ─────────────────────────────
                    if (normalOv != null && baseN is { Length: > 0 })
                        AlphaComposite(baseN, normalOv, wN, hN, CovAt(wN, hN));

                    // ── Phase C: emissive → normal alpha ──────────────────────
                    // skin.shpk: normal alpha = per-pixel emissive intensity (key 0x380CAED0).
                    bool thisOverlayHasEmissive = rows.Values.Any(r => r.A.Emissive > 0.001f || r.B.Emissive > 0.001f);
                    if (thisOverlayHasEmissive)
                    {
                        if (texPaths.Normal == null)
                        {
                            log.Warning("[Proteus] Emissive set but material has no normal texture: {0}", mtrlGamePath);
                        }
                        else
                        {
                            if (baseN == null)
                            {
                                baseN = LoadBaseNormal(texPaths.Normal, ref wN, ref hN);
                                if (anyEmissive && baseN.Length > 0)
                                    for (int ai = 3; ai < baseN.Length; ai += 4) baseN[ai] = 0;
                            }
                            if (baseN.Length > 0)
                            {
                                if (desc.Index != null)
                                {
                                    // Index texture maps each pixel to a color table row.
                                    // Write configured emissive for that row to normal alpha.
                                    // Pixels outside the overlay have R=0 → unmapped → stay at 0.
                                    var idN = LoadPng(Path.Combine(entry.SidecarRoot, desc.Index), wN, hN);
                                    var emMask = CovAt(wN, hN);
                                    if (idN != null && emMask != null) ApplyIndexedEmissive(baseN, idN, emMask, rows, wN, hN);
                                }
                                else
                                {
                                    var emMask = CovAt(wN, hN);
                                    if (emMask != null) ApplyFlatEmissive(baseN, emMask, row16A, wN, hN);
                                    else log.Warning("[Proteus] No emissive mask for: {0}", texPaths.Normal);
                                }
                            }
                        }
                    }

                    // ── Phase D: mask texture composite ───────────────────────
                    if (desc.Mask != null && texPaths.Mask != null)
                    {
                        if (baseM == null)
                        {
                            var loaded = textureLoader.LoadBaseTexture(penumbra.ResolvePlayer(texPaths.Mask), texPaths.Mask);
                            if (loaded.HasValue) { baseM = loaded.Value.rgba; wM = loaded.Value.width; hM = loaded.Value.height; }
                            baseM ??= Array.Empty<byte>();
                        }
                        if (baseM.Length > 0)
                        {
                            var ov = LoadPng(Path.Combine(entry.SidecarRoot, desc.Mask), wM, hM);
                            if (ov != null) AlphaComposite(baseM, ov, wM, hM, CovAt(wM, hM));
                        }
                    }
                }

                var baseName = SanitizeName(mtrlGamePath) + "_" + runId;
                var channels = new System.Text.StringBuilder();

                if (baseD is { Length: > 0 } && texPaths.Diffuse != null)
                {
                    var outPath = Path.Combine(texturesDir, baseName + "_d.tex");
                    var relPath = "textures/" + baseName + "_d.tex";
                    if (textureLoader.WriteTex(baseD, wD, hD, outPath))
                    { redirects[texPaths.Diffuse] = relPath; Interlocked.Increment(ref texturesPatched); channels.Append(" diffuse"); }
                }
                if (baseN is { Length: > 0 } && texPaths.Normal != null)
                {
                    var outPath = Path.Combine(texturesDir, baseName + "_n.tex");
                    var relPath = "textures/" + baseName + "_n.tex";
                    if (textureLoader.WriteTex(baseN, wN, hN, outPath))
                    { redirects[texPaths.Normal] = relPath; Interlocked.Increment(ref texturesPatched); channels.Append(" normal"); }
                }
                if (baseM is { Length: > 0 } && texPaths.Mask != null)
                {
                    var outPath = Path.Combine(texturesDir, baseName + "_m.tex");
                    var relPath = "textures/" + baseName + "_m.tex";
                    if (textureLoader.WriteTex(baseM, wM, hM, outPath))
                    { redirects[texPaths.Mask] = relPath; Interlocked.Increment(ref texturesPatched); channels.Append(" mask"); }
                }

                if (channels.Length > 0)
                    log.Debug("[Proteus] Composited {0}:{1}", mtrlGamePath, channels);

                // Patch .mtrl with emissive shader key + color table if any row has Emissive > 0
                bool needsEmissive = pairs.Any(p =>
                    p.Overlay.ColorTableRows?.Any(r =>
                        r.SubRowA?.Emissive > 0.001f || r.SubRowB?.Emissive > 0.001f) == true);

                if (needsEmissive)
                {
                    var raw = textureLoader.LoadRawMtrl(mtrlDisk, mtrlGamePath);
                    if (raw == null)
                    {
                        log.Warning("[Proteus] Could not load raw mtrl for emissive patch: {0}", mtrlGamePath);
                    }
                    else
                    {
                        var combinedRows = new Dictionary<int, ColorTableRowOverride>();
                        foreach (var (_, ov2) in pairs)
                        {
                            var dict = BuildRowDict(ov2.ColorTableRows);
                            foreach (var (pairIdx, row) in dict)
                                if (!combinedRows.ContainsKey(pairIdx))
                                    combinedRows[pairIdx] = row;
                        }

                        raw = TextureLoader.EnsureShaderKey(raw, 0x380CAED0u, 0x72E697CDu);
                        raw = TextureLoader.PatchColorTableEmissive(raw, combinedRows);

                        // Ensure emissive color constant (0x38A64362) is neutral [1, 1, 1] so the
                        // glow color matches the diffuse color set in the color picker exactly.
                        // Some skins omit this constant; without it the glow defaults to black.
                        var (rawEmConst, emConstPatched) = TextureLoader.PatchEmissiveColorConstant(raw, 1f, 1f, 1f);
                        raw = emConstPatched ? rawEmConst : TextureLoader.EnsureEmissiveColorConstant(raw, 1f, 1f, 1f);

                        Directory.CreateDirectory(materialsDir);
                        var outPath = Path.Combine(materialsDir, baseName + ".mtrl");
                        var relPath = "materials/" + baseName + ".mtrl";
                        if (textureLoader.WriteMtrl(raw, outPath))
                            redirects[mtrlGamePath] = relPath;
                    }
                }

            });

            WriteManagedModJson(redirects);
            ReloadAndRedraw();

            LastResult = new CompositorResult
            {
                Success = true,
                TexturesPatched = texturesPatched,
                OverlayModsUsed = entries.Count,
            };
            ResultChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] Recomposite failed");
            LastResult = new CompositorResult { Success = false, ErrorMessage = ex.Message };
            ResultChanged?.Invoke();
        }
    }

    // ── Override application ─────────────────────────────────────────────────

    private List<OverlayEntry> ApplyOverrides(List<OverlayEntry> entries)
    {
        return entries
            .Where(e => !(config.ModOverrides.TryGetValue(e.ModDirectory, out var ov) && ov.Disabled))
            .Select(e => config.ModOverrides.TryGetValue(e.ModDirectory, out var ov) && ov.PriorityOverride.HasValue
                ? e with { Priority = ov.PriorityOverride.Value }
                : e)
            .OrderBy(e => e.Priority)
            .ToList();
    }

    // ── Managed mod helpers ──────────────────────────────────────────────────

    private void EnsureManagedModExists()
    {
        if (Directory.Exists(managedModDir)) return;

        Directory.CreateDirectory(managedModDir);
        Directory.CreateDirectory(Path.Combine(managedModDir, "textures"));

        File.WriteAllText(
            Path.Combine(managedModDir, "meta.json"),
            """{"FileVersion":3,"Name":"Proteus","Author":"Proteus","Description":"Managed by the Proteus overlay compositor plugin.","Version":"","Website":"","ModTags":[]}""");

        WriteManagedModJson(new Dictionary<string, string>());

        var ec = penumbra.AddModDirectory(SidecarDiscoveryService.ManagedModDir);
        log.Information("[Proteus] AddMod({0}) -> {1}", managedModDir, ec);

        var collId = penumbra.GetPlayerCollectionId();
        if (collId.HasValue)
        {
            penumbra.SetModEnabled(collId.Value, SidecarDiscoveryService.ManagedModDir, true);
            penumbra.SetModPriority(collId.Value, SidecarDiscoveryService.ManagedModDir, config.ManagedModPriority);
        }
    }

    private void WriteManagedModJson(IDictionary<string, string> redirects)
    {
        // Penumbra default_mod.json: { "Files": { "gamePath": "relPath", ... }, "Swaps": {}, "Manipulations": [] }
        var files = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gamePath, relPath) in redirects)
            files[gamePath] = relPath;

        var obj = new { Files = files, Swaps = new { }, Manipulations = Array.Empty<object>() };
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        var target = Path.Combine(managedModDir, "default_mod.json");
        var tmp    = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, json);
        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, target, overwrite: true); break; }
            catch (Exception) when (i < 5) { Thread.Sleep(50 << i); } // 50 100 200 400 800ms
        }
    }

    private void ReloadAndRedraw(bool redraw = true)
    {
        var ec = penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
        log.Debug("[Proteus] ReloadMod -> {0}", ec);
        if (redraw && !config.DisableAutoRedraw)
        {
            // Give Penumbra's async reload time to process before the redraw re-requests textures.
            Thread.Sleep(300);
            penumbra.RedrawPlayer();
        }
    }

    // ── Compositing ──────────────────────────────────────────────────────────

    // Load the base normal texture, guarding against our own managed mod output
    // (feedback loop: Penumbra may still resolve our path after a reload if the IPC
    // is processed asynchronously, or if path separators differ).
    // Falls back to game SqPack if the resolved path points into managedModDir.
    // After loading, resets alpha to 0 if >50% of pixels are 255 — a reliable
    // fingerprint of our own stale all-255 output (natural base normals avg ~5).
    private byte[] LoadBaseNormal(string gamePath, ref int w, ref int h)
    {
        var diskPath = penumbra.ResolvePlayer(gamePath);
        if (diskPath != null)
        {
            // Normalize separators before comparing so forward/back-slash mismatches don't bypass the guard.
            var diskFull    = Path.GetFullPath(diskPath);
            var managedFull = Path.GetFullPath(managedModDir);
            if (diskFull.StartsWith(managedFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(diskFull, managedFull, StringComparison.OrdinalIgnoreCase))
            {
                log.Warning("[Proteus] ResolvePlayer returned our own managed file for {0} — falling back to game data", gamePath);
                diskPath = null;
            }
        }

        var loaded = textureLoader.LoadBaseTexture(diskPath, gamePath);
        if (!loaded.HasValue) return Array.Empty<byte>();

        var rgba = loaded.Value.rgba;
        w = loaded.Value.width;
        h = loaded.Value.height;
        return rgba;
    }

    // Tint + alpha composite using a flat sub-row color (no index texture).
    // When DiffuseR/G/B = 1, this is a standard alpha-over composite.
    private static void ApplyFlatOverlay(byte[] baseTex, byte[] ov, ColorTableSubRow row, int w, int h)
    {
        float cr = row.DiffuseR, cg = row.DiffuseG, cb = row.DiffuseB;
        int len = w * h * 4;
        for (int i = 0; i < len; i += 4)
        {
            float a = ov[i + 3] / 255f;
            if (a <= 0f) continue;
            float ia = 1f - a;
            baseTex[i]     = (byte)(ov[i]     / 255f * cr * a * 255f + baseTex[i]     * ia);
            baseTex[i + 1] = (byte)(ov[i + 1] / 255f * cg * a * 255f + baseTex[i + 1] * ia);
            baseTex[i + 2] = (byte)(ov[i + 2] / 255f * cb * a * 255f + baseTex[i + 2] * ia);
        }
    }

    // Write emissive intensity to the normal map alpha where the overlay is opaque.
    private static void ApplyFlatEmissive(byte[] baseN, byte[] ov, ColorTableSubRow row, int w, int h)
    {
        if (row.Emissive <= 0.001f) return;
        byte intensity = (byte)(row.Emissive * 255f);
        int len = w * h * 4;
        for (int i = 0; i < len; i += 4)
            if (ov[i + 3] > 0)
                baseN[i + 3] = Math.Max(baseN[i + 3], intensity);
    }

    // Write emissive intensity to normal alpha driven by index texture row mapping.
    // cov gates which pixels belong to this overlay (diffuse alpha > 0 = inside overlay).
    // For covered pixels, pairIdx (idx R/17) selects the row; only rows in `rows` with
    // emissive > 0 write a value. All other pixels remain at 0 (set by the anyEmissive reset).
    private static void ApplyIndexedEmissive(
        byte[] baseN, byte[] idx, byte[] cov,
        Dictionary<int, ColorTableRowOverride> rows,
        int w, int h)
    {
        int len = w * h * 4;
        for (int i = 0; i < len; i += 4)
        {
            if (cov[i + 3] == 0) continue; // outside this overlay's coverage
            int pairIdx = idx[i] / 17;
            if (!rows.TryGetValue(pairIdx, out var pair)) continue;
            float blendA = idx[i + 1] / 255f;
            float em = pair.B.Emissive + (pair.A.Emissive - pair.B.Emissive) * blendA;
            if (em > 0.001f)
                baseN[i + 3] = Math.Max(baseN[i + 3], (byte)(em * 255f));
        }
    }

    // Per-pixel color and emissive driven by index texture.
    // isNormal = false: tint+composite diffuse; isNormal = true: write emissive to normal alpha.
    private static void ApplyIndexedOverlay(
        byte[] baseTex, byte[] ov, byte[] idx,
        Dictionary<int, ColorTableRowOverride> rows,
        bool isNormal, int w, int h)
    {
        int len = w * h * 4;
        for (int i = 0; i < len; i += 4)
        {
            float ovA = ov[i + 3] / 255f;
            if (ovA <= 0f) continue;

            int   pairIdx = idx[i]     / 17;        // red → pair 0–15
            float blendA  = idx[i + 1] / 255f;      // green → lerp B→A (1 = full A, 0 = full B)

            if (!rows.TryGetValue(pairIdx, out var pair)) pair = new ColorTableRowOverride();

            float dr = pair.B.DiffuseR + (pair.A.DiffuseR - pair.B.DiffuseR) * blendA;
            float dg = pair.B.DiffuseG + (pair.A.DiffuseG - pair.B.DiffuseG) * blendA;
            float db = pair.B.DiffuseB + (pair.A.DiffuseB - pair.B.DiffuseB) * blendA;
            float em = pair.B.Emissive  + (pair.A.Emissive  - pair.B.Emissive)  * blendA;

            if (!isNormal)
            {
                float ia = 1f - ovA;
                baseTex[i]     = (byte)(ov[i]     / 255f * dr * ovA * 255f + baseTex[i]     * ia);
                baseTex[i + 1] = (byte)(ov[i + 1] / 255f * dg * ovA * 255f + baseTex[i + 1] * ia);
                baseTex[i + 2] = (byte)(ov[i + 2] / 255f * db * ovA * 255f + baseTex[i + 2] * ia);
            }
            else
            {
                baseTex[i + 3] = Math.Max(baseTex[i + 3], (byte)(em * 255f));
            }
        }
    }

    // Standard alpha-over: dst = src * src.a + dst * (1 - src.a). Dst alpha unchanged.
    // mask: if provided, effective alpha = min(src alpha, mask alpha) — used so a diffuse overlay
    // silhouette gates the normal composite (invisible diffuse pixels stay at base normal).
    private static void AlphaComposite(byte[] dst, byte[] src, int w, int h, byte[]? mask = null)
    {
        int len = w * h * 4;
        for (int i = 0; i < len; i += 4)
        {
            float a = src[i + 3] / 255f;
            if (mask != null) a = Math.Min(a, mask[i + 3] / 255f);
            if (a <= 0f) continue;
            float ia = 1f - a;
            dst[i]     = (byte)(src[i]     * a + dst[i]     * ia);
            dst[i + 1] = (byte)(src[i + 1] * a + dst[i + 1] * ia);
            dst[i + 2] = (byte)(src[i + 2] * a + dst[i + 2] * ia);
        }
    }

    private static Dictionary<int, ColorTableRowOverride> BuildRowDict(List<ColorTableRowPreset>? presets)
    {
        var dict = new Dictionary<int, ColorTableRowOverride>();
        if (presets == null) return dict;
        foreach (var p in presets)
        {
            var row = new ColorTableRowOverride();
            if (p.SubRowA is { } a)
            {
                if (a.Diffuse != null) (row.A.DiffuseR, row.A.DiffuseG, row.A.DiffuseB) = ParseHex(a.Diffuse);
                row.A.Emissive = a.Emissive;
                row.A.Opacity  = a.Opacity;
            }
            if (p.SubRowB is { } b)
            {
                if (b.Diffuse != null) (row.B.DiffuseR, row.B.DiffuseG, row.B.DiffuseB) = ParseHex(b.Diffuse);
                row.B.Emissive = b.Emissive;
                row.B.Opacity  = b.Opacity;
            }
            dict[p.Row - 1] = row; // 1-based JSON → 0-based internal
        }
        return dict;
    }

    private static (float r, float g, float b) ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        int v = Convert.ToInt32(hex, 16);
        return ((v >> 16 & 0xFF) / 255f, (v >> 8 & 0xFF) / 255f, (v & 0xFF) / 255f);
    }

    // Returns the FFXIV body model character code (e.g. "c0201") for the local player,
    // accounting for mid-body sharing (Elezen/Lalafell/Miqo'te/Roegadyn all use c0201/c0101).
    // Returns null if the player is not in game or the race cannot be determined.
    private static string? GetPlayerBodyCode()
    {
        try
        {
            var ps = Plugin.PlayerState;
            if (ps == null || !ps.IsLoaded) return null;
            return BodyCodeFromCustomize((byte)ps.Race.RowId, (byte)ps.Tribe.RowId, (byte)ps.Sex);
        }
        catch { return null; }
    }

    private static string? BodyCodeFromCustomize(byte race, byte tribe, byte sex)
    {
        bool f = sex == 1;
        if (race == 1) return (tribe == 2, f) switch // Hyur: tribe 2 = Highlander
        {
            (false, false) => "c0101",
            (false, true)  => "c0201",
            (true,  false) => "c0301",
            _              => "c0401",
        };
        return race switch
        {
            2 or 3 or 4 or 5 => f ? "c0201" : "c0101", // Elezen/Lalafell/Miqo'te/Roegadyn share mid bodies
            6 => f ? "c1401" : "c1301", // Au Ra
            7 => f ? "c1601" : "c1501", // Hrothgar
            8 => f ? "c1801" : "c1701", // Viera
            _ => null,
        };
    }

    // Apply per-pixel opacity from the index texture, blending sub-row A/B values just
    // like diffuse color and emissive. Returns a new array; src and pngCache are not mutated.
    private static byte[] ApplyIndexedOpacity(byte[] src, byte[] idx, Dictionary<int, ColorTableRowOverride> rows)
    {
        var dst = (byte[])src.Clone();
        for (int i = 0; i < dst.Length; i += 4)
        {
            float a = dst[i + 3] / 255f;
            if (a <= 0f) continue;
            int pairIdx = idx[i] / 17;
            if (!rows.TryGetValue(pairIdx, out var pair)) continue;
            float blendA = idx[i + 1] / 255f;
            float op = pair.B.Opacity + (pair.A.Opacity - pair.B.Opacity) * blendA;
            if (op == 0f) continue;
            float newA = op < 0f
                ? a * (100f + op) / 100f
                : a + (1f - a) * op / 100f;
            dst[i + 3] = (byte)(newA * 255f + 0.5f);
        }
        return dst;
    }

    private static byte[] ScaleOverlayAlpha(byte[] src, int opacity)
    {
        var dst = (byte[])src.Clone();
        for (int i = 3; i < dst.Length; i += 4)
        {
            int a = dst[i];
            if (opacity < 0)
                dst[i] = (byte)(a * (100 + opacity) / 100);
            else if (a > 0)
                dst[i] = (byte)Math.Min(255, a + (255 - a) * opacity / 100);
        }
        return dst;
    }

    private static string SanitizeName(string gamePath)
    {
        var name = Path.GetFileNameWithoutExtension(gamePath);
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return name;
    }
}
