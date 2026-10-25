using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CheapLoc;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

public enum UVMapDownloadState { Idle, Downloading, Done, Failed }

/// <summary>
/// Fetches the UV transfer maps. They are deliberately NOT in the plugin zip.
/// <para/>
/// Two sets, each downloaded and reported on its own. The bibo↔gen3 pair (~128 MB each) every install fetches on first
/// run. The bibo↔TBSE pair only a player whose man meets a woman's art ever needs, so it is fetched on demand
/// (<see cref="EnsureMenMapsAsync"/>), and nothing that happens to it — a release not yet published, a throttled
/// mirror — can stop the first set or its recomposite.
/// <para/>
/// The maps live in the CONFIG directory, not next to the DLL — see <see cref="MigrateFromAssemblyDir"/>
/// for why that distinction is the entire point of this class.
/// </summary>
public class UVMapDownloadService : IDisposable
{
    /// <summary>
    /// One map: the release tag it is pinned to, its expected size and SHA-256 (see <see cref="ProteusAssets"/> for why
    /// they are pinned). A tag is immutable by contract: a revised map gets a NEW tag rather than replacing its assets,
    /// because a mirror caches by URL and would otherwise keep serving the old bytes for its whole TTL.
    /// </summary>
    internal sealed record MapFile(string Name, string Tag, long Bytes, string Sha256);

    /// <summary>The bibo↔gen3 pair, fetched by every install.</summary>
    internal static readonly MapFile[] CoreMaps =
    [
        new("bibo_to_gen3_transfer.tif", "uvmaps-v1", 134217960L, "155e736ddfb78448552968cdac7cd32f76012c83d5488058387e9fc53bd61cba"),
        new("gen3_to_bibo_transfer.tif", "uvmaps-v1", 134217960L, "1ec0280896cdd9b496dd5f76691ebbdbf1309229256f9126df5ee155bedbb946"),
    ];

    /// <summary>The bibo↔TBSE pair (see docs/uvmaps.md), fetched the first time a man needs it.</summary>
    internal static readonly MapFile[] MenMaps =
    [
        new(UVRemapService.BiboToTbseFile, "uvmaps-v2", 11798374L, "1ad198ed137abf5882ee0e38b3b91d7eb01771af35f7be2cd4eaf41d975e32a0"),
        new(UVRemapService.TbseToBiboFile, "uvmaps-v2", 12911330L, "701964922589185c3ed4d4b1af824f84bf0c2dd4552488a8e7383a08ce345c23"),
    ];

    /// <summary>Fetches one file: the seam tests replace; <see cref="ResilientDownloader.FetchAsync"/> otherwise.</summary>
    internal delegate Task<FetchResult> Fetch(string[] baseUrls, string fileName, long expectedBytes, string expectedSha,
                                              string destPath, Action<long>? onProgress, CancellationToken ct);

    private sealed class MapSet(MapFile[] files)
    {
        public readonly MapFile[] Files = files;
        public UVMapDownloadState State = UVMapDownloadState.Idle;
        public string Status = string.Empty;
    }

    private readonly IPluginLog log;
    private readonly ResilientDownloader? downloader;
    private readonly Fetch fetch;
    private readonly string mapsDir;
    private readonly List<string> legacyMapsDirs;
    private readonly CancellationTokenSource cts = new();
    private readonly MapSet core = new(CoreMaps);
    private readonly MapSet men = new(MenMaps);
    private readonly object gate = new();
    private int menAutoAttempted;

    /// <summary>
    /// Both sets as one pill: downloading if either is, failed if either failed, done once the first set is here.
    /// </summary>
    public UVMapDownloadState State
    {
        get
        {
            lock (gate)
            {
                if (core.State == UVMapDownloadState.Downloading || men.State == UVMapDownloadState.Downloading)
                    return UVMapDownloadState.Downloading;
                if (core.State == UVMapDownloadState.Failed || men.State == UVMapDownloadState.Failed)
                    return UVMapDownloadState.Failed;
                return core.State;
            }
        }
    }

    /// <summary>The message of the set the pill shows: the first set's before the second's, a download before a failure.</summary>
    public string StatusMessage
    {
        get
        {
            lock (gate)
            {
                foreach (var s in new[] { core, men }) if (s.State == UVMapDownloadState.Downloading) return s.Status;
                foreach (var s in new[] { core, men }) if (s.State == UVMapDownloadState.Failed) return s.Status;
                return string.Empty;
            }
        }
    }

    /// <summary>Raised, on a worker thread, after either set finishes downloading.</summary>
    public event Action? MapsArrived;

    /// <param name="dataDir">
    /// Persistent per-user directory (the plugin's ConfigDirectory). Must NOT be the assembly directory.
    /// </param>
    /// <param name="assemblyDir">
    /// Where a pre-519 install left its maps, so they can be reclaimed instead of re-downloaded.
    /// </param>
    public UVMapDownloadService(IPluginLog log, string dataDir, string? assemblyDir = null)
        : this(log, dataDir, assemblyDir, null) { }

    internal UVMapDownloadService(IPluginLog log, string dataDir, string? assemblyDir, Fetch? fetch)
    {
        this.log = log;
        if (fetch == null) downloader = new ResilientDownloader(log);
        this.fetch = fetch ?? downloader!.FetchAsync;
        mapsDir = Path.Combine(dataDir, "uvmaps");
        legacyMapsDirs = ProteusAssets.LegacyAssetDirs(assemblyDir, "uvmaps");
    }

    private bool Present(MapSet set)
        => set.Files.All(f => new FileInfo(Path.Combine(mapsDir, f.Name)) is { Exists: true, Length: > 0 });

    /// <summary>Whether the bibo↔gen3 pair is here. The bibo↔TBSE pair is not asked: it is not every install's.</summary>
    public bool MapsPresent()
    {
        MigrateFromAssemblyDir();
        return Present(core);
    }

    /// <summary>Whether the bibo↔TBSE pair is here.</summary>
    public bool MenMapsPresent() => Present(men);

    /// <summary>
    /// Reclaims maps left in the old assembly-directory home by an earlier version.
    /// <para/>
    /// Dalamud installs each plugin version into its own folder, so maps stored next to the DLL were
    /// destroyed by every update — which is how ~1.4k installs generated ~48k downloads of a 128 MB
    /// release asset and got the whole repo's assets throttled. Moving rather than re-fetching means
    /// an existing user pays nothing for the relocation.
    /// <para/>
    /// Deliberately does NOT verify the reclaimed file against <see cref="CoreMaps"/>: an older map
    /// revision that works is worth more than a forced 256 MB re-download, and the checksums exist to
    /// guard transfers, not to police what is already on disk. Only the first set: no version that kept
    /// maps beside the DLL ever had the second.
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

            foreach (var file in CoreMaps)
            {
                var dst = Path.Combine(mapsDir, file.Name);
                var src = Path.Combine(legacyDir, file.Name);
                if (File.Exists(dst) && new FileInfo(dst).Length > 0) continue;
                if (!File.Exists(src) || new FileInfo(src).Length == 0) continue;

                try
                {
                    Directory.CreateDirectory(mapsDir);
                    // Move is a rename within a volume and a copy across one. XIVLauncher's plugin
                    // folder and AppData are usually the same volume, but nothing guarantees it.
                    try { File.Move(src, dst, overwrite: true); }
                    catch (IOException) { File.Copy(src, dst, overwrite: true); TryDelete(src); }
                    log.Information("[Proteus] Reclaimed UV map from {0}: {1}", legacyDir, file.Name);
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "[Proteus] Could not reclaim {0} from {1}", file.Name, legacyDir);
                }
            }
        }
    }

    /// <summary>Downloads the bibo↔gen3 pair when it is not here. <see cref="MapsArrived"/> follows a download.</summary>
    public void EnsureMapsAsync()
    {
        if (MapsPresent())
        {
            lock (gate) core.State = UVMapDownloadState.Done;
            return;
        }
        Start(core);
    }

    /// <summary>
    /// Downloads the bibo↔TBSE pair when it is not here — once a session on its own; <see cref="RetryFailed"/> again.
    /// </summary>
    public void EnsureMenMapsAsync()
    {
        if (Interlocked.Exchange(ref menAutoAttempted, 1) != 0) return;
        if (MenMapsPresent()) { lock (gate) men.State = UVMapDownloadState.Done; return; }
        Start(men);
    }

    /// <summary>The pill's Retry: every set that failed, downloaded again.</summary>
    public void RetryFailed()
    {
        foreach (var set in new[] { core, men })
        {
            bool failed;
            lock (gate) failed = set.State == UVMapDownloadState.Failed;
            if (failed) Start(set);
        }
    }

    private void Start(MapSet set)
    {
        lock (gate)
        {
            if (set.State == UVMapDownloadState.Downloading) return;
            set.State = UVMapDownloadState.Downloading;
            // Localized at PRODUCTION time, on a thread-pool thread, and then held as a plain string: switching
            // language mid-download leaves this one pill in the old language until the next progress tick.
            // Accepted deliberately — the alternative is storing a key plus boxed args and formatting at draw
            // time, which is real machinery for a string that is replaced within seconds.
            set.Status = ProteusLoc.Localize("Service.UVMaps.Downloading", "Downloading UV maps...");
        }
        Task.Run(() => DownloadAll(set), cts.Token);
    }

    private async Task DownloadAll(MapSet set)
    {
        try
        {
            Directory.CreateDirectory(mapsDir);

            // Only the files still missing count toward the total, each at its FULL size. A partial
            // .tmp is not subtracted: the downloader reports absolute per-file progress that already
            // includes the resumed prefix, so subtracting it here would double-discount it and the pill
            // would finish reading well past 100%.
            long totalExpected = 0;
            foreach (var file in set.Files)
            {
                var dest = Path.Combine(mapsDir, file.Name);
                if (File.Exists(dest) && new FileInfo(dest).Length > 0) continue;
                totalExpected += file.Bytes;
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
                var status = string.Format(
                    ProteusLoc.Localize("Service.UVMaps.Progress.Fmt", "Downloading UV maps... ({0} MB / {1} MB)"),
                    done / (1024 * 1024), totalExpected / (1024 * 1024));
                lock (gate) set.Status = status;
                nextReport = done + 5L * 1024 * 1024;
            }

            foreach (var file in set.Files)
            {
                var dest = Path.Combine(mapsDir, file.Name);
                if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                    continue;

                current = 0;
                var r = await fetch(ProteusAssets.BaseUrls(file.Tag), file.Name, file.Bytes, file.Sha256, dest, Progress, cts.Token);

                if (!r.Ok)
                {
                    Fail(set, r.Failure switch
                    {
                        FetchFailure.Http => string.Format(
                            ProteusLoc.Localize("Service.UVMaps.HttpFailed.Fmt", "Download failed: HTTP {0} for {1}"),
                            r.StatusCode, file.Name),
                        FetchFailure.TooSmall => string.Format(
                            ProteusLoc.Localize("Service.UVMaps.TooSmall.Fmt",
                                "Download too small ({0} bytes) for {1} — possible LFS pointer"), r.Bytes, file.Name),
                        _ => string.Format(
                            ProteusLoc.Localize("Service.UVMaps.Error.Fmt", "Download error: {0}"), r.Detail),
                    });
                    return;
                }

                // Bank the file at its pinned size rather than at whatever the last progress call
                // reported, so the running total cannot drift from the figure the pill is counting to.
                completed += file.Bytes;
                current = 0;
            }

            lock (gate)
            {
                set.State = UVMapDownloadState.Done;
                set.Status = string.Empty;
            }
            log.Information("[Proteus] UV maps download complete ({0}).", string.Join(", ", set.Files.Select(f => f.Name)));
            if (!cts.IsCancellationRequested) MapsArrived?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // The .tmp files are deliberately LEFT in place: they are the resume point for the next
            // session, and every one of them is re-hashed before a byte is appended to it.
            lock (gate) set.State = UVMapDownloadState.Idle;
        }
        catch (Exception ex)
        {
            Fail(set, string.Format(ProteusLoc.Localize("Service.UVMaps.Error.Fmt", "Download error: {0}"), ex.Message));
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void Fail(MapSet set, string message)
    {
        lock (gate)
        {
            set.State = UVMapDownloadState.Failed;
            set.Status = message;
        }
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
        downloader?.Dispose();
    }
}
