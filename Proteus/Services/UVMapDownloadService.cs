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

    /// <summary>
    /// The bibo↔TBSE pair (see docs/uvmaps.md), fetched the first time a man needs it. Unlike the first set it is held
    /// to its pinned size: an install with an older revision (v2, whose nipples fall 3 cm low) is brought up to this
    /// one in the background (<see cref="StartUpgradeIfStale"/>) and swapped in whole at the next start.
    /// </summary>
    internal static readonly MapFile[] MenMaps =
    [
        new(UVRemapService.BiboToTbseFile, "uvmaps-v3", 11834120L, "559efe148403b12c168f20b41dd614952708933f4404fedc98c7ab79a6a36ca1"),
        new(UVRemapService.TbseToBiboFile, "uvmaps-v3", 13027942L, "820fd4c1cb38930698a12daba7bd624ddc43a5e417d53436b34a165d14a6f227"),
    ];

    /// <summary>How long a failed background upgrade of the men's pair waits before it is tried again.</summary>
    internal static readonly TimeSpan UpgradeRetry = TimeSpan.FromHours(24);

    /// <summary>The file that says when the men's pair last failed to upgrade: one ISO-8601 UTC time.</summary>
    internal const string UpgradeFailedMarker = "men-upgrade.failed";

    /// <summary>Fetches one file: the seam tests replace; <see cref="ResilientDownloader.FetchAsync"/> otherwise.</summary>
    internal delegate Task<FetchResult> Fetch(string[] baseUrls, string fileName, long expectedBytes, string expectedSha,
                                              string destPath, Action<long>? onProgress, CancellationToken ct);

    /// <param name="exactSize">
    /// Whether a file on disk counts as this set's only at its pinned size. The men's pair is; the first set is not
    /// (see <see cref="MigrateFromAssemblyDir"/>: an older revision there is worth more than 256 MB fetched again).
    /// </param>
    private sealed class MapSet(MapFile[] files, bool exactSize)
    {
        public readonly MapFile[] Files = files;
        public readonly bool ExactSize = exactSize;
        public UVMapDownloadState State = UVMapDownloadState.Idle;
        public string Status = string.Empty;
    }

    private readonly IPluginLog log;
    private readonly ResilientDownloader? downloader;
    private readonly Fetch fetch;
    private readonly string mapsDir;
    private readonly List<string> legacyMapsDirs;
    private readonly CancellationTokenSource cts = new();
    private readonly MapSet core = new(CoreMaps, exactSize: false);
    private readonly MapSet men = new(MenMaps, exactSize: true);
    private readonly object gate = new();
    private readonly Func<DateTime> utcNow;
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

    /// <remarks>
    /// Finishes any swap of the men's pair a crash cut short, then swaps in a staged pair: here, before the plugin
    /// builds anything that reads a map, so no map is open or cached while its file is replaced.
    /// </remarks>
    internal UVMapDownloadService(IPluginLog log, string dataDir, string? assemblyDir, Fetch? fetch, Func<DateTime>? utcNow = null)
    {
        this.log = log;
        if (fetch == null) downloader = new ResilientDownloader(log);
        this.fetch = fetch ?? downloader!.FetchAsync;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        mapsDir = Path.Combine(dataDir, "uvmaps");
        legacyMapsDirs = ProteusAssets.LegacyAssetDirs(assemblyDir, "uvmaps");
        RecoverInterruptedSwap();
        SwapInStagedMenMaps();
    }

    private string PathOf(MapFile f) => Path.Combine(mapsDir, f.Name);
    private string StagedOf(MapFile f) => PathOf(f) + ".staged";
    private string PrevOf(MapFile f) => PathOf(f) + ".prev";
    private static bool AtPin(string path, MapFile f) => new FileInfo(path) is { Exists: true } fi && fi.Length == f.Bytes;

    /// <summary>A file of <paramref name="set"/> on disk that will do: any non-empty one, or for the men's pair one at its pinned size.</summary>
    private bool Done(MapSet set, MapFile f)
        => set.ExactSize ? AtPin(PathOf(f), f) : new FileInfo(PathOf(f)) is { Exists: true, Length: > 0 };

    /// <summary>Whether every file of <paramref name="set"/> is on disk and usable — of whatever revision.</summary>
    private bool Present(MapSet set)
        => set.Files.All(f => new FileInfo(PathOf(f)) is { Exists: true, Length: > 0 });

    /// <summary>
    /// A swap of the men's pair a crash cut short, finished: a file whose final is missing is rolled forward from its
    /// staged copy when the other is already the current revision, else back from its <c>.prev</c>; once both finals
    /// are here, the <c>.prev</c> files go. A roll-back may leave one current and one old — the next upgrade, which
    /// fetches only what is not current, makes them whole.
    /// </summary>
    private void RecoverInterruptedSwap()
    {
        try
        {
            foreach (var f in MenMaps)
            {
                if (File.Exists(PathOf(f))) continue;
                bool othersCurrent = MenMaps.Where(o => o != f).All(o => AtPin(PathOf(o), o));
                if (AtPin(StagedOf(f), f) && othersCurrent) File.Move(StagedOf(f), PathOf(f));
                else if (File.Exists(PrevOf(f))) File.Move(PrevOf(f), PathOf(f));
                else continue;
                log.Information("[Proteus] Recovered {0} after an interrupted UV map swap.", f.Name);
            }
            if (MenMaps.All(f => File.Exists(PathOf(f))))
                foreach (var f in MenMaps) TryDelete(PrevOf(f));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Could not recover an interrupted UV map swap");
        }
    }

    /// <summary>
    /// The staged men's files moved into place — only when every file of the pair is then the current revision
    /// (staged at its pin, or already final at it), so the pair is never left half new and half old. Staged files of
    /// an older pin are dropped. A failure puts back what it moved; the next start's recovery finishes the rest.
    /// </summary>
    private void SwapInStagedMenMaps()
    {
        var moved = new List<MapFile>();
        try
        {
            foreach (var f in MenMaps)
                if (File.Exists(StagedOf(f)) && !AtPin(StagedOf(f), f)) TryDelete(StagedOf(f));
            var staged = MenMaps.Where(f => AtPin(StagedOf(f), f)).ToList();
            if (staged.Count == 0 || !MenMaps.All(f => AtPin(StagedOf(f), f) || AtPin(PathOf(f), f))) return;
            foreach (var f in staged)
            {
                if (File.Exists(PathOf(f))) File.Move(PathOf(f), PrevOf(f), overwrite: true);
                moved.Add(f);
                File.Move(StagedOf(f), PathOf(f));
            }
            foreach (var f in staged) TryDelete(PrevOf(f));
            log.Information("[Proteus] Swapped in the upgraded UV maps ({0}).", string.Join(", ", staged.Select(f => f.Name)));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Could not swap in the upgraded UV maps; keeping the old ones");
            foreach (var f in moved)
            {
                try
                {
                    if (!File.Exists(PrevOf(f))) continue;
                    if (File.Exists(PathOf(f))) File.Move(PathOf(f), StagedOf(f), overwrite: true);
                    File.Move(PrevOf(f), PathOf(f));
                }
                catch (Exception inner) { log.Warning(inner, "[Proteus] Could not put back {0}", f.Name); }
            }
        }
    }

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
        StartUpgradeIfStale();
        if (MapsPresent())
        {
            lock (gate) core.State = UVMapDownloadState.Done;
            return;
        }
        Start(core);
    }

    /// <summary>
    /// When both men's files are here but not both the pinned revision, the ones that are neither current nor already
    /// staged are fetched beside them as <c>.staged</c>, out of sight: no pill, no <see cref="MapsArrived"/>, the old
    /// pair drawing meanwhile, the new one swapped in at the next start. A failure keeps the old pair, says so in the
    /// log and is not tried again for <see cref="UpgradeRetry"/>. A lone file is no upgrade: the on-demand download
    /// fetches what is missing or not current straight to its place.
    /// </summary>
    internal Task? StartUpgradeIfStale()
    {
        if (!MenMaps.All(f => File.Exists(PathOf(f))) || MenMaps.All(f => AtPin(PathOf(f), f))) return null;
        if (FailedRecently()) return null;
        // The pair is usable as it is: the on-demand download has nothing to do this session.
        if (Interlocked.Exchange(ref menAutoAttempted, 1) != 0) return null;
        return Task.Run(UpgradeMenMaps, cts.Token);
    }

    private async Task UpgradeMenMaps()
    {
        try
        {
            foreach (var f in MenMaps)
            {
                if (AtPin(PathOf(f), f) || AtPin(StagedOf(f), f)) continue;
                var r = await fetch(ProteusAssets.BaseUrls(f.Tag), f.Name, f.Bytes, f.Sha256, StagedOf(f), null, cts.Token);
                if (!r.Ok)
                {
                    log.Warning("[Proteus] Could not upgrade {0} ({1}); keeping the maps on disk, trying again in a day.",
                                f.Name, r.Detail ?? r.Failure.ToString());
                    MarkUpgradeFailed();
                    return;
                }
            }
            TryDelete(Path.Combine(mapsDir, UpgradeFailedMarker));
            log.Information("[Proteus] Upgraded UV maps fetched; they take over at the next start.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Could not upgrade the men's UV maps; keeping the maps on disk, trying again in a day.");
            MarkUpgradeFailed();
        }
    }

    private void MarkUpgradeFailed()
    {
        try { File.WriteAllText(Path.Combine(mapsDir, UpgradeFailedMarker), utcNow().ToString("O")); }
        catch (Exception ex) { log.Warning(ex, "[Proteus] Could not note the failed UV map upgrade"); }
    }

    /// <summary>Whether the last upgrade failed less than <see cref="UpgradeRetry"/> ago. A marker that will not read, or from the future, is none.</summary>
    private bool FailedRecently()
    {
        try
        {
            var path = Path.Combine(mapsDir, UpgradeFailedMarker);
            if (!File.Exists(path)) return false;
            if (!DateTime.TryParse(File.ReadAllText(path).Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)) return false;
            var age = utcNow() - at.ToUniversalTime();
            return age >= TimeSpan.Zero && age < UpgradeRetry;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Could not read when the UV map upgrade last failed; trying it");
            return false;
        }
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
                if (Done(set, file)) continue;
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
                var dest = PathOf(file);
                if (Done(set, file))
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
