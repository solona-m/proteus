using System;
using System.Collections.Generic;
using System.IO;
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

        var modsRoot = penumbra.GetModDirectory() ?? string.Empty;
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
        // Skip changes to the managed mod itself (we write it — would cause a loop).
        if (string.Equals(modDir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return;
        TriggerRecomposite($"ModSettingChanged:{change}:{modDir}");
    }

    private void OnModAdded(string modDir)   => TriggerRecomposite($"ModAdded:{modDir}");
    private void OnModDeleted(string modDir) => TriggerRecomposite($"ModDeleted:{modDir}");

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
        Task.Run(() => Recomposite(cts.Token), cts.Token);
    }

    // ── Core compositor ──────────────────────────────────────────────────────

    private void Recomposite(CancellationToken ct)
    {
        try
        {
            EnsureManagedModExists();

            // Clear any previous redirects first so ResolvePlayer sees the original
            // mod textures rather than our own previously-composited output.
            WriteManagedModJson(new Dictionary<string, string>());
            penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);

            var entries = discovery.DiscoverEnabled();
            if (ct.IsCancellationRequested) return;

            LastDiscovered = entries;

            if (entries.Count == 0)
            {
                WriteManagedModJson(new Dictionary<string, string>());
                ReloadAndRedraw();
                LastResult = new CompositorResult { Success = true, TexturesPatched = 0, OverlayModsUsed = 0 };
                ResultChanged?.Invoke();
                return;
            }

            // Flatten: (entry, overlayDescriptor) pairs, grouped by material game path
            var byMaterial = new Dictionary<string, List<(OverlayEntry, OverlayDescriptor)>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                var overlays = discovery.ResolveActiveOverlays(entry);
                foreach (var overlay in overlays)
                {
                    if (string.IsNullOrEmpty(overlay.MaterialGamePath)) continue;
                    if (!byMaterial.TryGetValue(overlay.MaterialGamePath, out var list))
                        byMaterial[overlay.MaterialGamePath] = list = new();
                    list.Add((entry, overlay));
                }
            }

            if (ct.IsCancellationRequested) return;

            var texturesDir = Path.Combine(managedModDir, "textures");
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

                // Load base + composite each channel that has at least one overlay
                byte[]? baseD = null, baseN = null, baseM = null;
                int w = 0, h = 0;

                foreach (var (entry, overlay) in pairs) // already sorted by priority
                {
                    if (ct.IsCancellationRequested) return;

                    if (overlay.Diffuse != null && texPaths.Diffuse != null)
                    {
                        if (baseD == null)
                        {
                            var diffDisk = penumbra.ResolvePlayer(texPaths.Diffuse);
                            var loaded = textureLoader.LoadBaseTexture(diffDisk, texPaths.Diffuse);
                            if (loaded.HasValue) { baseD = loaded.Value.rgba; w = loaded.Value.width; h = loaded.Value.height; }
                            baseD ??= new byte[0];
                        }
                        if (baseD.Length > 0)
                        {
                            var ovPath = Path.Combine(entry.SidecarRoot, overlay.Diffuse);
                            var ov = textureLoader.LoadPngAsRgba(ovPath, w, h);
                            if (ov != null) AlphaComposite(baseD, ov, w, h);
                        }
                    }

                    if (overlay.Normal != null && texPaths.Normal != null)
                    {
                        if (baseN == null)
                        {
                            var loaded = textureLoader.LoadBaseTexture(penumbra.ResolvePlayer(texPaths.Normal), texPaths.Normal);
                            if (loaded.HasValue) { baseN = loaded.Value.rgba; if (w == 0) { w = loaded.Value.width; h = loaded.Value.height; } }
                            baseN ??= new byte[0];
                        }
                        if (baseN.Length > 0)
                        {
                            var ovPath = Path.Combine(entry.SidecarRoot, overlay.Normal);
                            var ov = textureLoader.LoadPngAsRgba(ovPath, w, h);
                            if (ov != null) AlphaComposite(baseN, ov, w, h);
                        }
                    }

                    if (overlay.Mask != null && texPaths.Mask != null)
                    {
                        if (baseM == null)
                        {
                            var loaded = textureLoader.LoadBaseTexture(penumbra.ResolvePlayer(texPaths.Mask), texPaths.Mask);
                            if (loaded.HasValue) { baseM = loaded.Value.rgba; if (w == 0) { w = loaded.Value.width; h = loaded.Value.height; } }
                            baseM ??= new byte[0];
                        }
                        if (baseM.Length > 0)
                        {
                            var ovPath = Path.Combine(entry.SidecarRoot, overlay.Mask);
                            var ov = textureLoader.LoadPngAsRgba(ovPath, w, h);
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
                }
                if (baseM is { Length: > 0 } && texPaths.Mask != null)
                {
                    var outPath = Path.Combine(texturesDir, baseName + "_m.tex");
                    var relPath = "textures/" + baseName + "_m.tex";
                    if (textureLoader.WriteTex(baseM, w, h, outPath))
                    { redirects[texPaths.Mask] = relPath; texturesPatched++; }
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
        File.WriteAllText(Path.Combine(managedModDir, "default_mod.json"), json);
    }

    private void ReloadAndRedraw()
    {
        var ec = penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
        log.Debug("[Proteus] ReloadMod -> {0}", ec);
        penumbra.RedrawPlayer();
    }

    // ── Compositing ──────────────────────────────────────────────────────────

    // Standard alpha-over: dst = src * src.a + dst * (1 - src.a)
    private static void AlphaComposite(byte[] dst, byte[] src, int w, int h)
    {
        int len = w * h * 4;
        for (int i = 0; i < len; i += 4)
        {
            float a = src[i + 3] / 255f;
            float ia = 1f - a;
            dst[i]     = (byte)(src[i]     * a + dst[i]     * ia);
            dst[i + 1] = (byte)(src[i + 1] * a + dst[i + 1] * ia);
            dst[i + 2] = (byte)(src[i + 2] * a + dst[i + 2] * ia);
            // dst alpha: keep base alpha unchanged
        }
    }

    private static string SanitizeName(string gamePath)
    {
        // e.g. "chara/human/c1401/obj/body/b0001/material/v0001/mt_c1401b0001_a.mtrl"
        // -> "mt_c1401b0001_a" (filename without extension)
        var name = Path.GetFileNameWithoutExtension(gamePath);
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return name;
    }
}
