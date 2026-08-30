using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CheapLoc;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

public enum UVMapDownloadState { Idle, Downloading, Done, Failed }

/// <summary>
/// Fetches the two UV transfer maps on first run. They are ~128 MB each and are deliberately NOT in
/// the plugin zip.
/// <para/>
/// The maps live in the CONFIG directory, not next to the DLL — see <see cref="MigrateFromAssemblyDir"/>
/// for why that distinction is the entire point of this class.
/// </summary>
public class UVMapDownloadService : IDisposable
{
    /// <summary>
    /// The release tag the maps are pinned to. Immutable by contract: a revised map gets a NEW tag
    /// rather than replacing this one's assets, because a mirror caches by URL and would otherwise
    /// keep serving the old bytes for its whole TTL. Bump this and <see cref="MapFiles"/> together.
    /// </summary>
    private const string MapsTag = "uvmaps-v1";

    /// <summary>Expected size and SHA-256 per map. See <see cref="ProteusAssets"/> for why they are pinned.</summary>
    private static readonly (string Name, long Bytes, string Sha256)[] MapFiles =
    [
        ("bibo_to_gen3_transfer.tif", 134217960L, "155e736ddfb78448552968cdac7cd32f76012c83d5488058387e9fc53bd61cba"),
        ("gen3_to_bibo_transfer.tif", 134217960L, "1ec0280896cdd9b496dd5f76691ebbdbf1309229256f9126df5ee155bedbb946"),
    ];

    private readonly IPluginLog log;
    private readonly ResilientDownloader downloader;
    private readonly string mapsDir;
    private readonly List<string> legacyMapsDirs;
    private readonly CancellationTokenSource cts = new();

    public UVMapDownloadState State { get; private set; } = UVMapDownloadState.Idle;
    public string StatusMessage { get; private set; } = string.Empty;

    /// <param name="dataDir">
    /// Persistent per-user directory (the plugin's ConfigDirectory). Must NOT be the assembly directory.
    /// </param>
    /// <param name="assemblyDir">
    /// Where a pre-519 install left its maps, so they can be reclaimed instead of re-downloaded.
    /// </param>
    public UVMapDownloadService(IPluginLog log, string dataDir, string? assemblyDir = null)
    {
        this.log = log;
        downloader = new ResilientDownloader(log);
        mapsDir = Path.Combine(dataDir, "uvmaps");
        legacyMapsDirs = ProteusAssets.LegacyAssetDirs(assemblyDir, "uvmaps");
    }

    public bool MapsPresent()
    {
        MigrateFromAssemblyDir();
        foreach (var (name, _, _) in MapFiles)
        {
            var path = Path.Combine(mapsDir, name);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Reclaims maps left in the old assembly-directory home by an earlier version.
    /// <para/>
    /// Dalamud installs each plugin version into its own folder, so maps stored next to the DLL were
    /// destroyed by every update — which is how ~1.4k installs generated ~48k downloads of a 128 MB
    /// release asset and got the whole repo's assets throttled. Moving rather than re-fetching means
    /// an existing user pays nothing for the relocation.
    /// <para/>
    /// Deliberately does NOT verify the reclaimed file against <see cref="MapFiles"/>: an older map
    /// revision that works is worth more than a forced 256 MB re-download, and the checksums exist to
    /// guard transfers, not to police what is already on disk.
    /// </summary>
    private bool migrated;
    private void MigrateFromAssemblyDir()
    {
        if (migrated) return;
        migrated = true;

        foreach (var legacyDir in legacyMapsDirs)
        {
            if (!Directory.Exists(legacyDir)) continue;
            if (string.Equals(Path.GetFullPath(legacyDir), Path.GetFullPath(mapsDir),
                              StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var (name, _, _) in MapFiles)
            {
                var dst = Path.Combine(mapsDir, name);
                var src = Path.Combine(legacyDir, name);
                if (File.Exists(dst) && new FileInfo(dst).Length > 0) continue;
                if (!File.Exists(src) || new FileInfo(src).Length == 0) continue;

                try
                {
                    Directory.CreateDirectory(mapsDir);
                    // Move is a rename within a volume and a copy across one. XIVLauncher's plugin
                    // folder and AppData are usually the same volume, but nothing guarantees it.
                    try { File.Move(src, dst, overwrite: true); }
                    catch (IOException) { File.Copy(src, dst, overwrite: true); TryDelete(src); }
                    log.Information("[Proteus] Reclaimed UV map from {0}: {1}", legacyDir, name);
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "[Proteus] Could not reclaim {0} from {1}", name, legacyDir);
                }
            }
        }
    }

    public void EnsureMapsAsync(Action? onComplete = null)
    {
        if (State == UVMapDownloadState.Downloading) return;
        if (MapsPresent())
        {
            State = UVMapDownloadState.Done;
            return;
        }

        State = UVMapDownloadState.Downloading;
        // Localized at PRODUCTION time, on a thread-pool thread, and then held as a plain string: switching
        // language mid-download leaves this one pill in the old language until the next progress tick.
        // Accepted deliberately — the alternative is storing a key plus boxed args and formatting at draw
        // time, which is real machinery for a string that is replaced within seconds.
        StatusMessage = Loc.Localize("Service.UVMaps.Downloading", "Downloading UV maps...");
        Task.Run(() => DownloadAll(onComplete), cts.Token);
    }

    private async Task DownloadAll(Action? onComplete)
    {
        try
        {
            Directory.CreateDirectory(mapsDir);

            // Only the files still missing count toward the total, each at its FULL size. A partial
            // .tmp is not subtracted: the downloader reports absolute per-file progress that already
            // includes the resumed prefix, so subtracting it here would double-discount it and the pill
            // would finish reading well past 100%.
            long totalExpected = 0;
            foreach (var (name, bytes, _) in MapFiles)
            {
                var dest = Path.Combine(mapsDir, name);
                if (File.Exists(dest) && new FileInfo(dest).Length > 0) continue;
                totalExpected += bytes;
            }
            if (totalExpected <= 0) totalExpected = 1;   // guard the division in the progress line

            // Bytes belonging to files already finished this run. The in-flight file's contribution is
            // whatever it last reported, which is why the two are tracked separately: an attempt that
            // restarts rewinds `current` to zero without disturbing what is already banked.
            long completed = 0;
            long current = 0;
            long nextReport = 0;

            void Progress(long fileBytes)
            {
                current = fileBytes;
                var done = completed + current;
                if (done < nextReport) return;
                StatusMessage = string.Format(
                    Loc.Localize("Service.UVMaps.Progress.Fmt", "Downloading UV maps... ({0} MB / {1} MB)"),
                    done / (1024 * 1024), totalExpected / (1024 * 1024));
                nextReport = done + 5L * 1024 * 1024;
            }

            foreach (var (name, bytes, sha) in MapFiles)
            {
                var dest = Path.Combine(mapsDir, name);
                if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                    continue;

                current = 0;
                var r = await downloader.FetchAsync(
                    ProteusAssets.BaseUrls(MapsTag), name, bytes, sha, dest, Progress, cts.Token);

                if (!r.Ok)
                {
                    Fail(r.Failure switch
                    {
                        FetchFailure.Http => string.Format(
                            Loc.Localize("Service.UVMaps.HttpFailed.Fmt", "Download failed: HTTP {0} for {1}"),
                            r.StatusCode, name),
                        FetchFailure.TooSmall => string.Format(
                            Loc.Localize("Service.UVMaps.TooSmall.Fmt",
                                "Download too small ({0} bytes) for {1} — possible LFS pointer"), r.Bytes, name),
                        _ => string.Format(
                            Loc.Localize("Service.UVMaps.Error.Fmt", "Download error: {0}"), r.Detail),
                    });
                    return;
                }

                // Bank the file at its pinned size rather than at whatever the last progress call
                // reported, so the running total cannot drift from the figure the pill is counting to.
                completed += bytes;
                current = 0;
            }

            State = UVMapDownloadState.Done;
            StatusMessage = string.Empty;
            log.Information("[Proteus] UV maps download complete.");
            onComplete?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // The .tmp files are deliberately LEFT in place: they are the resume point for the next
            // session, and every one of them is re-hashed before a byte is appended to it.
            State = UVMapDownloadState.Idle;
        }
        catch (Exception ex)
        {
            Fail(string.Format(Loc.Localize("Service.UVMaps.Error.Fmt", "Download error: {0}"), ex.Message));
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void Fail(string message)
    {
        State = UVMapDownloadState.Failed;
        StatusMessage = message;
        log.Error("[Proteus] {0}", message);
    }

    /// <summary>
    /// Cancels any in-flight fetch and releases the HTTP client.
    /// <para/>
    /// Disposing the downloader is not tidiness: its <c>HttpClient</c> owns connection-pool timers that
    /// the runtime roots from outside the plugin's load context, so leaving it alive can pin the context
    /// and keep the plugin's native libraries mapped after an unload. See ResilientDownloader's field.
    /// </summary>
    public void Dispose()
    {
        cts.Cancel();
        downloader.Dispose();
    }
}
