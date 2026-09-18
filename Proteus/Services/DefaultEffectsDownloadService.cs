using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>
/// Fetches the starter scroll-effect library (pinned release assets) into the config directory on first run.
/// Deliberately quiet: a failure is a log line, no UI state, and a retry on the next plugin load.
/// </summary>
public sealed class DefaultEffectsDownloadService : IDisposable
{
    /// <summary>
    /// Immutable by contract, like the UV map tag: bump it whenever <see cref="Effects"/> changes, and
    /// never re-upload assets under an existing tag, or a mirror will keep serving the old bytes.
    /// </summary>
    private const string EffectsTag = "effects-v1";

    /// <summary>Shipped loose rather than zipped: zip bytes are not reproducible, so a pinned checksum would break.</summary>
    private static readonly (string Name, long Bytes, string Sha256)[] Effects =
    [
        ("Moon and Stars.jpeg", 1621608L, "c6c7655f1d374334c1b81acbe9975a535bf4a064bf4e731567e3a4becaa44cf6"),
        ("flames.jpeg", 491191L, "5b56ac6848dc28ab7a70915f0f5237844c89033b0d5b300fc8f43e1532aa350f"),
        ("geometric.jpeg", 192565L, "0b11f45055d8d977e291820417bf2bc3e695362c93c6c7b1fe658d4da2866e91"),
        ("hello kitty.png", 311900L, "891d75ed6aeead872619bd90b0760d8caebb560c7b6bba29921dc495efd004b7"),
        ("lips.png", 496612L, "4a79b217dd75280fc7351034defde5a41c279e82f634d74fbf857fb6d28daa97"),
        ("polka dots.png", 1047037L, "f3c9fcf06bef1f7c26eb18f745fdcb4e243dbc0e7e709b2663a8f83600166871"),
        ("rgb.jpeg", 276412L, "0adef290f65ae5161eabbdb0c3d013370d5d86b7c948f2b8fdd5fc0cfd1b2664"),
        ("sky.jpeg", 1237346L, "b216dab955e9bcb93d94d830053efb930b5422dc9e7c36c98155cd5d1a0a5147"),
        ("starfield.jpeg", 154196L, "6a8f414c7b56aed528400d462d84c33656d8651e8424645620c58bef0bce938a"),
        ("unicorns.png", 687411L, "17e258044e40e13855d24255eae00b83d3093e343276a406f8c0801218251b36"),
        ("wildflowers and butterflies.png", 4594896L, "ac5bc9791c9934b01fcf63cca510452f9120d6dadd613aa7f6990b85a1ac535b"),
    ];

    /// <summary>
    /// The asset name to ask for, which is not the name the file is saved under: GitHub rewrites spaces, and
    /// upload-effects.yml renames its uploads with this same substitution. The local name is what a sidecar's
    /// <c>Scroll</c> refers to, so it must not change.
    /// </summary>
    private static string RemoteName(string localName) => localName.Replace(' ', '.');

    private readonly IPluginLog log;
    private readonly ResilientDownloader downloader;
    private readonly string effectsDir;
    private readonly List<string> legacyEffectsDirs;
    private readonly CancellationTokenSource cts = new();
    private int started;

    /// <summary>Where <see cref="SidecarDiscoveryService"/> should seed the user's library from.</summary>
    public string EffectsDir => effectsDir;

    public DefaultEffectsDownloadService(IPluginLog log, string dataDir, string? assemblyDir = null)
    {
        this.log = log;
        downloader = new ResilientDownloader(log);
        effectsDir = Path.Combine(dataDir, "DefaultEffects");
        legacyEffectsDirs = ProteusAssets.LegacyAssetDirs(assemblyDir, "DefaultEffects");
    }

    /// <summary>
    /// Fetches anything missing, in the background. <paramref name="onProgress"/> fires after each file
    /// lands so the caller can re-seed incrementally.
    /// </summary>
    public void EnsureAsync(Action? onProgress = null)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        Task.Run(() => Run(onProgress), cts.Token);
    }

    private async Task Run(Action? onProgress)
    {
        try
        {
            Directory.CreateDirectory(effectsDir);
            if (ReclaimFromAssemblyDir()) { Notify(onProgress); return; }

            bool any = false;
            foreach (var (name, bytes, sha) in Effects)
            {
                cts.Token.ThrowIfCancellationRequested();

                var dest = Path.Combine(effectsDir, name);
                // Non-empty is enough to skip, not a match against the pinned size: a reclaimed older revision works.
                if (File.Exists(dest) && new FileInfo(dest).Length > 0) continue;

                var r = await downloader.FetchAsync(
                    ProteusAssets.BaseUrls(EffectsTag), RemoteName(name), bytes, sha, dest, null, cts.Token);

                if (r.Ok) { any = true; Notify(onProgress); }
                else log.Warning("[Proteus] Starter effect {0} unavailable ({1}); will retry next load", name, r.Detail);
            }

            if (any) log.Information("[Proteus] Starter effect library ready.");
        }
        catch (OperationCanceledException) { /* plugin unloading */ }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Starter effect download failed; the Effect dropdown may be empty");
        }
    }

    /// <summary>
    /// Uses the copy an older build left next to the DLL instead of downloading. Returns true when every
    /// effect in <see cref="Effects"/> is now present locally. Sizes are not checked, and completeness is
    /// decided by name, never by counting what was moved (the folder may hold the user's own files).
    /// </summary>
    private bool ReclaimFromAssemblyDir()
    {
        int moved = 0;

        foreach (var legacyDir in legacyEffectsDirs)
        {
            if (!Directory.Exists(legacyDir)) continue;
            if (string.Equals(Path.GetFullPath(legacyDir), Path.GetFullPath(effectsDir),
                              StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var src in Directory.EnumerateFiles(legacyDir))
            {
                var dst = Path.Combine(effectsDir, Path.GetFileName(src));
                if (File.Exists(dst) && new FileInfo(dst).Length > 0) continue;
                try
                {
                    try { File.Move(src, dst, overwrite: true); }
                    catch (IOException) { File.Copy(src, dst, overwrite: true); TryDelete(src); }
                    moved++;
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "[Proteus] Could not reclaim starter effect {0}", Path.GetFileName(src));
                }
            }

            // Stop as soon as the set is complete; later siblings are older copies of the same art.
            if (HaveEveryEffect()) break;
        }

        if (moved > 0) log.Information("[Proteus] Reclaimed {0} starter effect(s) from an older install", moved);
        return HaveEveryEffect();
    }

    /// <summary>
    /// Runs the caller's "a file landed" callback, absorbing anything it throws: overlapping seeds can
    /// collide on a file, and that must not cancel the remaining downloads.
    /// </summary>
    private void Notify(Action? onProgress)
    {
        try { onProgress?.Invoke(); }
        catch (Exception ex) { log.Warning(ex, "[Proteus] Re-seeding the effect library failed"); }
    }

    /// <summary>Whether every name in <see cref="Effects"/> exists locally and is non-empty.</summary>
    private bool HaveEveryEffect()
    {
        foreach (var (name, _, _) in Effects)
        {
            var p = Path.Combine(effectsDir, name);
            if (!File.Exists(p) || new FileInfo(p).Length == 0) return false;
        }
        return true;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// Cancels any in-flight fetch and releases the HTTP client, which load-context unloadability requires.
    /// </summary>
    public void Dispose()
    {
        cts.Cancel();
        downloader.Dispose();
    }
}
