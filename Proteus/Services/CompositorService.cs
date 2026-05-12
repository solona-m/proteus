using System;
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

    private readonly string modsRoot;
    private readonly string managedModDir;

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
    }

    public void Dispose()
    {
        penumbra.ModSettingChanged -= OnModSettingChanged;
        penumbra.ModAdded          -= OnModAdded;
        penumbra.ModDeleted        -= OnModDeleted;

        currentCts?.Cancel();
        currentCts?.Dispose();
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnModSettingChanged(ModSettingChange change, Guid collId, string modDir, bool inherited)
    {
        if (string.Equals(modDir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
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
            log.Debug("[Proteus] Recomposite START v3");
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
                    if (string.IsNullOrEmpty(overlay.Descriptor.MaterialGamePath)) continue;
                    if (!byMaterial.TryGetValue(overlay.Descriptor.MaterialGamePath, out var list))
                        byMaterial[overlay.Descriptor.MaterialGamePath] = list = new();
                    list.Add((entry, overlay));
                }
            }

            if (ct.IsCancellationRequested) return;

            var texturesDir  = texturesDirEarly;
            var materialsDir = materialsDirEarly;
            Directory.CreateDirectory(texturesDir);

            var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int texturesPatched = 0;

            foreach (var (mtrlGamePath, pairs) in byMaterial)
            {
                if (ct.IsCancellationRequested) return;

                var mtrlDisk = penumbra.ResolvePlayer(mtrlGamePath);
                var texPaths = (mtrlDisk != null && File.Exists(mtrlDisk))
                    ? textureLoader.ResolveMtrlTextures(mtrlDisk)
                    : textureLoader.ResolveMtrlTexturesFromGame(mtrlGamePath);

                if (texPaths.Diffuse == null && texPaths.Normal == null && texPaths.Mask == null)
                {
                    log.Warning("[Proteus] No textures found for material: {0}", mtrlGamePath);
                    continue;
                }

                byte[]? baseD = null, baseN = null, baseM = null;
                int w = 0, h = 0;

                foreach (var (entry, resolved) in pairs)
                {
                    if (ct.IsCancellationRequested) return;

                    var desc   = resolved.Descriptor;
                    var rows   = BuildRowDict(resolved.ColorTableRows);
                    rows.TryGetValue(15, out var row16);
                    var row16A = row16?.A ?? new ColorTableSubRow();

                    byte[]? idRgba    = null;  // index texture, loaded lazily once per overlay
                    byte[]? diffuseOv = null;  // diffuse overlay, kept for emissive fallback
                    byte[]? normalOv  = null;  // normal overlay, kept for emissive pass

                    // ── Diffuse ──────────────────────────────────────────────
                    if (desc.Diffuse != null && texPaths.Diffuse != null)
                    {
                        if (baseD == null)
                        {
                            var diffDisk = penumbra.ResolvePlayer(texPaths.Diffuse);
                            var loaded = textureLoader.LoadBaseTexture(diffDisk, texPaths.Diffuse);
                            if (loaded.HasValue) { baseD = loaded.Value.rgba; w = loaded.Value.width; h = loaded.Value.height; }
                            baseD ??= Array.Empty<byte>();
                        }
                        if (baseD.Length > 0)
                        {
                            diffuseOv = textureLoader.LoadPngAsRgba(Path.Combine(entry.SidecarRoot, desc.Diffuse), w, h);
                            if (diffuseOv != null)
                            {
                                if (desc.Index != null)
                                {
                                    idRgba ??= textureLoader.LoadPngAsRgba(Path.Combine(entry.SidecarRoot, desc.Index), w, h);
                                    if (idRgba != null)
                                        ApplyIndexedOverlay(baseD, diffuseOv, idRgba, rows, false, w, h);
                                    else
                                        ApplyFlatOverlay(baseD, diffuseOv, row16A, w, h);
                                }
                                else
                                    ApplyFlatOverlay(baseD, diffuseOv, row16A, w, h);
                            }
                        }
                    }

                    // ── Normal RGB composite ──────────────────────────────────
                    log.Debug("[Proteus] NormDbg entry={0} overlayNormal={1} texNormal={2} baseNNull={3}",
                        entry.ModDirectory, desc.Normal ?? "(null)", texPaths.Normal ?? "(null)", baseN == null);
                    if (desc.Normal != null && texPaths.Normal != null)
                    {
                        if (baseN == null)
                            baseN = LoadBaseNormal(texPaths.Normal, ref w, ref h);
                        if (baseN.Length > 0)
                        {
                            normalOv = textureLoader.LoadPngAsRgba(Path.Combine(entry.SidecarRoot, desc.Normal), w, h);
                            if (normalOv != null)
                            {
                                // Normal-only overlay: synthesize a white diffuse using the normal's blue
                                // channel as opacity. This gates the normal composite to the detail region
                                // and applies a matching white tint to the base diffuse.
                                if (diffuseOv == null)
                                {
                                    diffuseOv = new byte[normalOv.Length];
                                    for (int si = 0; si < normalOv.Length; si += 4)
                                    {
                                        diffuseOv[si]     = 255;
                                        diffuseOv[si + 1] = 255;
                                        diffuseOv[si + 2] = 255;
                                        diffuseOv[si + 3] = normalOv[si + 2]; // blue → opacity
                                    }
                                    if (texPaths.Diffuse != null)
                                    {
                                        if (baseD == null)
                                        {
                                            var diffDisk = penumbra.ResolvePlayer(texPaths.Diffuse);
                                            var loaded   = textureLoader.LoadBaseTexture(diffDisk, texPaths.Diffuse);
                                            if (loaded.HasValue) { baseD = loaded.Value.rgba; if (w == 0) { w = loaded.Value.width; h = loaded.Value.height; } }
                                            baseD ??= Array.Empty<byte>();
                                        }
                                        if (baseD.Length > 0)
                                            ApplyFlatOverlay(baseD, diffuseOv, row16A, w, h);
                                    }
                                }
                                AlphaComposite(baseN, normalOv, w, h, diffuseOv);
                            }
                        }
                    }

                    // ── Emissive → normal alpha ───────────────────────────────
                    // skin.shpk: normal alpha = per-pixel emissive intensity mask (key 0x380CAED0 = EMISSIVE).
                    var emissiveMask = diffuseOv ?? normalOv;
                    if (emissiveMask != null && row16A.Emissive > 0.001f)
                    {
                        if (texPaths.Normal == null)
                        {
                            log.Warning("[Proteus] Emissive set but material has no normal texture: {0}", mtrlGamePath);
                        }
                        else
                        {
                            if (baseN == null)
                                baseN = LoadBaseNormal(texPaths.Normal, ref w, ref h);
                            if (baseN.Length > 0)
                            {
                                log.Debug("[Proteus] Writing emissive intensity={0:F2} to normal alpha for {1}", row16A.Emissive, mtrlGamePath);
                                if (desc.Index != null)
                                {
                                    idRgba ??= textureLoader.LoadPngAsRgba(Path.Combine(entry.SidecarRoot, desc.Index), w, h);
                                    if (idRgba != null)
                                        ApplyIndexedOverlay(baseN, emissiveMask, idRgba, rows, true, w, h);
                                    else
                                        ApplyFlatEmissive(baseN, emissiveMask, row16A, w, h);
                                }
                                else
                                    ApplyFlatEmissive(baseN, emissiveMask, row16A, w, h);
                            }
                            else
                                log.Warning("[Proteus] Base normal failed to load for emissive: {0}", texPaths.Normal);
                        }
                    }

                    // ── Mask ─────────────────────────────────────────────────
                    if (desc.Mask != null && texPaths.Mask != null)
                    {
                        if (baseM == null)
                        {
                            var loaded = textureLoader.LoadBaseTexture(penumbra.ResolvePlayer(texPaths.Mask), texPaths.Mask);
                            if (loaded.HasValue) { baseM = loaded.Value.rgba; if (w == 0) { w = loaded.Value.width; h = loaded.Value.height; } }
                            baseM ??= Array.Empty<byte>();
                        }
                        if (baseM.Length > 0)
                        {
                            var ov = textureLoader.LoadPngAsRgba(Path.Combine(entry.SidecarRoot, desc.Mask), w, h);
                            if (ov != null) AlphaComposite(baseM, ov, w, h);
                        }
                    }
                }

                var baseName = SanitizeName(mtrlGamePath);

                if (baseD is { Length: > 0 } && texPaths.Diffuse != null)
                {
                    var outPath = Path.Combine(texturesDir, baseName + "_d.tex");
                    var relPath = "textures/" + baseName + "_d.tex";
                    if (textureLoader.WriteTex(baseD, w, h, outPath))
                    { redirects[texPaths.Diffuse] = relPath; texturesPatched++; }
                }
                if (baseN is { Length: > 0 } && texPaths.Normal != null)
                {
                    var outPath = Path.Combine(texturesDir, baseName + "_n.tex");
                    var relPath = "textures/" + baseName + "_n.tex";
                    if (textureLoader.WriteTex(baseN, w, h, outPath))
                    { redirects[texPaths.Normal] = relPath; texturesPatched++; }
                    // Debug: write alongside as PNG so alpha channel can be inspected visually
                    textureLoader.WritePng(baseN, w, h, Path.Combine(texturesDir, baseName + "_n_debug.png"));
                    int emPx = 0;
                    for (int ai = 3; ai < baseN.Length; ai += 4) if (baseN[ai] > 0) emPx++;
                    log.Debug("[Proteus] Normal tex written: {0}×{1}, emissive pixels={2}/{3}", w, h, emPx, baseN.Length / 4);
                }
                if (baseM is { Length: > 0 } && texPaths.Mask != null)
                {
                    var outPath = Path.Combine(texturesDir, baseName + "_m.tex");
                    var relPath = "textures/" + baseName + "_m.tex";
                    if (textureLoader.WriteTex(baseM, w, h, outPath))
                    { redirects[texPaths.Mask] = relPath; texturesPatched++; }
                }

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
                        float er = 1f, eg = 1f, eb = 1f;
                        var topRows = pairs[0].Overlay.ColorTableRows;
                        var row16p  = topRows?.FirstOrDefault(r => r.Row == 16);
                        if (row16p?.SubRowA?.Diffuse != null)
                            (er, eg, eb) = ParseHex(row16p.SubRowA.Diffuse);

                        var (shaderName, constIds) = TextureLoader.GetMtrlInfo(raw);
                        log.Debug("[Proteus] Mtrl shader={0}, consts=[{1}]",
                            shaderName, string.Join(",", constIds.Select(id => $"0x{id:X8}")));

                        raw = TextureLoader.EnsureShaderKey(raw, 0x380CAED0u, 0x72E697CDu);
                        raw = TextureLoader.PatchColorTableEmissive(raw, er, eg, eb);

                        // Ensure emissive color constant (0x38A64362) is warm gold [1.4, 0.931, 0.574].
                        // clia skin and some other bibo mods omit this constant; without it the
                        // emissive color defaults to black and the glow is invisible.
                        var (rawEmConst, emConstPatched) = TextureLoader.PatchEmissiveColorConstant(raw, 1.4f, 0.931f, 0.574f);
                        raw = emConstPatched ? rawEmConst : TextureLoader.EnsureEmissiveColorConstant(raw, 1.4f, 0.931f, 0.574f);
                        log.Debug("[Proteus] Emissive mtrl patch: color=({0:F2},{1:F2},{2:F2}) constOp={3} → {4}",
                            er, eg, eb, emConstPatched ? "patched" : "inserted", baseName);

                        var (verifyShader, verifyConsts) = TextureLoader.GetMtrlInfo(raw);
                        log.Debug("[Proteus] Post-patch verify: shader={0} constCount={1} has38A64362={2}",
                            verifyShader, verifyConsts.Length,
                            verifyConsts.Any(id => id == 0x38A64362u));

                        Directory.CreateDirectory(materialsDir);
                        var outPath = Path.Combine(materialsDir, baseName + ".mtrl");
                        var relPath = "materials/" + baseName + ".mtrl";
                        if (textureLoader.WriteMtrl(raw, outPath))
                            redirects[mtrlGamePath] = relPath;
                    }
                }
            }

            if (ct.IsCancellationRequested) return;

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

    private void WriteManagedModJson(Dictionary<string, string> redirects)
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

    private void ReloadAndRedraw()
    {
        var ec = penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
        log.Debug("[Proteus] ReloadMod -> {0}", ec);
        // Give Penumbra's async reload time to process before the redraw re-requests textures.
        Thread.Sleep(300);
        penumbra.RedrawPlayer();
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
            else
            {
                log.Debug("[Proteus] Loading base normal from: {0}", diskPath);
            }
        }
        else
        {
            log.Debug("[Proteus] Loading base normal from: game-data");
        }

        var loaded = textureLoader.LoadBaseTexture(diskPath, gamePath);
        if (!loaded.HasValue) return Array.Empty<byte>();

        var rgba = loaded.Value.rgba;
        if (w == 0) { w = loaded.Value.width; h = loaded.Value.height; }
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
            }
            if (p.SubRowB is { } b)
            {
                if (b.Diffuse != null) (row.B.DiffuseR, row.B.DiffuseG, row.B.DiffuseB) = ParseHex(b.Diffuse);
                row.B.Emissive = b.Emissive;
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

    private static string SanitizeName(string gamePath)
    {
        var name = Path.GetFileNameWithoutExtension(gamePath);
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return name;
    }
}
