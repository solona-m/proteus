using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The two map sets: the first every install fetches, the second only on demand, neither blocking the other.</summary>
public class UVMapDownloadServiceTests : IDisposable
{
    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "proteus-uvdl-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Tag, string Name)> fetched = new();
    private readonly HashSet<string> failing = [];
    private TaskCompletionSource? gate;

    public UVMapDownloadServiceTests() => Directory.CreateDirectory(Path.Combine(dataDir, "uvmaps"));

    public void Dispose()
    {
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    private async Task<FetchResult> Fetch(string[] urls, string name, long bytes, string sha, string dest, Action<long>? progress, CancellationToken ct)
    {
        if (gate != null) await gate.Task.WaitAsync(ct);
        // As the real downloader does: a cancelled fetch writes nothing.
        ct.ThrowIfCancellationRequested();
        var tag = urls[0].TrimEnd('/').Split('/').Last();
        fetched.Enqueue((tag, name));
        if (failing.Contains(name)) return new FetchResult(false, FetchFailure.Http, 404, 0, "");
        // At the pinned size, as a verified download is: the men's pair is held to it.
        using (var fs = File.Create(dest)) fs.SetLength(bytes);
        return FetchResult.Success;
    }

    private DateTime now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private UVMapDownloadService Service() => new(Substitute.For<IPluginLog>(), dataDir, null, Fetch, () => now);

    private string MapPath(string name) => Path.Combine(dataDir, "uvmaps", name);

    /// <summary>A file in the maps folder, <paramref name="length"/> bytes long.</summary>
    private void HaveAt(string name, long length)
    {
        using var fs = File.Create(MapPath(name));
        fs.SetLength(length);
    }

    /// <summary>The v2 pair's sizes: an install holding them is one revision behind.</summary>
    private static readonly long[] V2Bytes = [11798374L, 12911330L];

    private static UVMapDownloadService.MapFile A => UVMapDownloadService.MenMaps[0];
    private static UVMapDownloadService.MapFile B => UVMapDownloadService.MenMaps[1];
    private long Length(string name) => new FileInfo(MapPath(name)) is { Exists: true } f ? f.Length : -1;

    private static void WaitFor(Func<bool> done)
    {
        for (int i = 0; i < 3000 && !done(); i++) Thread.Sleep(10);   // 30 s: the full suite starves the thread pool
        Assert.True(done());
    }

    private void Have(params UVMapDownloadService.MapFile[] files)
    {
        foreach (var f in files) File.WriteAllBytes(Path.Combine(dataDir, "uvmaps", f.Name), [1]);
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-7")]
    public void Startup_fetches_only_v1_from_uvmaps_v1() // [AC-tbse-uvmap-7]
    {
        using var dl = Service();
        dl.EnsureMapsAsync();
        WaitFor(() => dl.State == UVMapDownloadState.Done);
        Assert.Equal(UVMapDownloadService.CoreMaps.Select(f => ("uvmaps-v1", f.Name)), fetched);
        Assert.False(dl.MenMapsPresent());
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-7")]
    [Trait("Plan", "AC-uvmap-nipples-7")]
    public void Men_maps_fetch_from_uvmaps_v3_on_demand() // [AC-tbse-uvmap-7] [AC-uvmap-nipples-7]
    {
        Have(UVMapDownloadService.CoreMaps);
        using var dl = Service();
        dl.EnsureMenMapsAsync();
        WaitFor(dl.MenMapsPresent);
        Assert.Equal(UVMapDownloadService.MenMaps.Select(f => ("uvmaps-v3", f.Name)), fetched);
    }

    [Fact]
    [Trait("Plan", "AC-uvmap-nipples-6")]
    public void Stale_men_maps_stage_in_background_with_no_pill() // [AC-uvmap-nipples-6]
    {
        Have(UVMapDownloadService.CoreMaps);
        HaveAt(A.Name, V2Bytes[0]);
        HaveAt(B.Name, V2Bytes[1]);
        gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dl = Service();
        int arrived = 0;
        dl.MapsArrived += () => Interlocked.Increment(ref arrived);
        dl.EnsureMapsAsync();
        // While the new pair comes, the pill shows nothing and the old pair draws.
        Assert.Equal(UVMapDownloadState.Done, dl.State);
        Assert.Equal(string.Empty, dl.StatusMessage);
        Assert.True(dl.MenMapsPresent());
        gate.SetResult();
        WaitFor(() => Length(A.Name + ".staged") == A.Bytes && Length(B.Name + ".staged") == B.Bytes);
        Assert.Equal(V2Bytes[0], Length(A.Name));
        Assert.Equal(V2Bytes[1], Length(B.Name));
        Assert.Equal(UVMapDownloadService.MenMaps.Select(f => ("uvmaps-v3", f.Name)), fetched);
        Assert.Equal(0, Volatile.Read(ref arrived));
        Assert.Equal(string.Empty, dl.StatusMessage);
    }

    [Fact]
    [Trait("Plan", "AC-uvmap-nipples-6")]
    [Trait("Plan", "EDGE-uvmap-nipples-11")]
    public void Staged_files_are_swapped_in_by_the_constructor() // [AC-uvmap-nipples-6] [EDGE-uvmap-nipples-11]
    {
        // Both staged: both swapped, nothing left beside them, nothing fetched.
        HaveAt(A.Name, V2Bytes[0]); HaveAt(B.Name, V2Bytes[1]);
        HaveAt(A.Name + ".staged", A.Bytes); HaveAt(B.Name + ".staged", B.Bytes);
        using (var dl = Service())
        {
            Assert.Equal(A.Bytes, Length(A.Name));
            Assert.Equal(B.Bytes, Length(B.Name));
            Assert.Empty(Directory.GetFiles(Path.Combine(dataDir, "uvmaps"), "*.staged"));
            Assert.Empty(Directory.GetFiles(Path.Combine(dataDir, "uvmaps"), "*.prev"));
            Assert.Null(dl.StartUpgradeIfStale());
        }
        Assert.True(fetched.IsEmpty);

        // One current, the other staged: the staged one swapped, the current one left alone.
        HaveAt(B.Name, V2Bytes[1]); HaveAt(B.Name + ".staged", B.Bytes);
        using (Service()) Assert.Equal(B.Bytes, Length(B.Name));

        // One staged, the other old and not staged: nothing swapped — never half new and half old.
        HaveAt(A.Name, V2Bytes[0]); HaveAt(B.Name, V2Bytes[1]); HaveAt(A.Name + ".staged", A.Bytes);
        using (Service())
        {
            Assert.Equal(V2Bytes[0], Length(A.Name));
            Assert.Equal(A.Bytes, Length(A.Name + ".staged"));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-20")]
    public void Failed_swap_keeps_a_usable_old_pair() // [EDGE-uvmap-nipples-20]
    {
        HaveAt(A.Name, V2Bytes[0]); HaveAt(B.Name, V2Bytes[1]);
        HaveAt(A.Name + ".staged", A.Bytes); HaveAt(B.Name + ".staged", B.Bytes);
        using (new FileStream(MapPath(B.Name), FileMode.Open, FileAccess.Read, FileShare.None))
        using (Service()) { }
        Assert.Equal(V2Bytes[0], Length(A.Name));
        Assert.Equal(V2Bytes[1], Length(B.Name));
        Assert.Equal(A.Bytes, Length(A.Name + ".staged"));
        Assert.Equal(B.Bytes, Length(B.Name + ".staged"));
        Assert.Empty(Directory.GetFiles(Path.Combine(dataDir, "uvmaps"), "*.prev"));
        // Unlocked, the next start swaps them.
        using (Service()) { }
        Assert.Equal(A.Bytes, Length(A.Name));
        Assert.Equal(B.Bytes, Length(B.Name));
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-28")]
    public void Constructor_restores_a_final_from_its_prev() // [EDGE-uvmap-nipples-28]
    {
        HaveAt(A.Name + ".prev", V2Bytes[0]); HaveAt(B.Name, V2Bytes[1]);
        using (Service()) { }
        Assert.Equal(V2Bytes[0], Length(A.Name));
        Assert.Equal(-1, Length(A.Name + ".prev"));
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-29")]
    public void Constructor_rolls_a_half_swap_forward_to_a_whole_v3_pair() // [EDGE-uvmap-nipples-29]
    {
        // The crash came after A was swapped in and B moved aside: B is rolled forward, not back.
        HaveAt(A.Name, A.Bytes); HaveAt(A.Name + ".prev", V2Bytes[0]);
        HaveAt(B.Name + ".staged", B.Bytes); HaveAt(B.Name + ".prev", V2Bytes[1]);
        using (Service()) { }
        Assert.Equal(A.Bytes, Length(A.Name));
        Assert.Equal(B.Bytes, Length(B.Name));
        Assert.Empty(Directory.GetFiles(Path.Combine(dataDir, "uvmaps"), "*.prev"));
        Assert.Empty(Directory.GetFiles(Path.Combine(dataDir, "uvmaps"), "*.staged"));
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-30")]
    public void Constructor_deletes_stray_prev_once_the_pair_is_complete() // [EDGE-uvmap-nipples-30]
    {
        HaveAt(A.Name, A.Bytes); HaveAt(B.Name, B.Bytes); HaveAt(A.Name + ".prev", V2Bytes[0]);
        using (Service()) { }
        Assert.Equal(-1, Length(A.Name + ".prev"));
        Assert.Equal(A.Bytes, Length(A.Name));
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-10")]
    public async Task Failed_upgrade_keeps_old_pair_and_waits_a_day() // [EDGE-uvmap-nipples-10]
    {
        Have(UVMapDownloadService.CoreMaps);
        HaveAt(A.Name, V2Bytes[0]); HaveAt(B.Name, V2Bytes[1]);
        failing.Add(A.Name);
        using (var dl = Service())
        {
            await dl.StartUpgradeIfStale()!;
            Assert.NotEqual(UVMapDownloadState.Failed, dl.State);
            Assert.Equal(string.Empty, dl.StatusMessage);
            int before = fetched.Count;
            dl.RetryFailed();
            Thread.Sleep(50);
            Assert.Equal(before, fetched.Count);
        }
        Assert.Equal(V2Bytes[0], Length(A.Name));
        Assert.True(File.Exists(MapPath(UVMapDownloadService.UpgradeFailedMarker)));

        now += TimeSpan.FromHours(1);
        using (var dl = Service()) Assert.Null(dl.StartUpgradeIfStale());

        now += TimeSpan.FromHours(24);
        failing.Clear();
        int fetchedBefore = fetched.Count;
        using (var dl = Service()) await dl.StartUpgradeIfStale()!;
        Assert.True(fetched.Count > fetchedBefore);
        Assert.False(File.Exists(MapPath(UVMapDownloadService.UpgradeFailedMarker)));
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-22")]
    public async Task Unreadable_or_future_marker_does_not_block_the_upgrade() // [EDGE-uvmap-nipples-22]
    {
        HaveAt(A.Name, V2Bytes[0]); HaveAt(B.Name, V2Bytes[1]);
        foreach (var marker in new[] { "not a time", now.AddDays(3).ToString("O") })
        {
            File.WriteAllText(MapPath(UVMapDownloadService.UpgradeFailedMarker), marker);
            using var dl = Service();
            var task = dl.StartUpgradeIfStale();
            Assert.NotNull(task);
            await task!;
            foreach (var f in UVMapDownloadService.MenMaps) File.Delete(MapPath(f.Name + ".staged"));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-21")]
    public void Staged_files_of_old_pins_are_discarded_and_not_swapped() // [EDGE-uvmap-nipples-21]
    {
        HaveAt(A.Name, V2Bytes[0]); HaveAt(B.Name, V2Bytes[1]);
        HaveAt(A.Name + ".staged", 1234); HaveAt(B.Name + ".staged", 5678);
        using (Service()) { }
        Assert.Equal(V2Bytes[0], Length(A.Name));
        Assert.Empty(Directory.GetFiles(Path.Combine(dataDir, "uvmaps"), "*.staged"));
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-12")]
    [Trait("Plan", "EDGE-uvmap-nipples-13")]
    public void No_men_maps_no_upgrade_and_core_of_another_size_kept() // [EDGE-uvmap-nipples-12] [EDGE-uvmap-nipples-13]
    {
        Have(UVMapDownloadService.CoreMaps);   // 1 byte each: an older revision of the first set, kept as it is
        using var dl = Service();
        Assert.Null(dl.StartUpgradeIfStale());
        dl.EnsureMapsAsync();
        Assert.Equal(UVMapDownloadState.Done, dl.State);
        Thread.Sleep(50);
        Assert.True(fetched.IsEmpty);
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-8")]
    [Trait("Plan", "EDGE-uvmap-nipples-34")]
    public void Lone_stale_file_is_fetched_on_demand_to_final_names() // [EDGE-uvmap-nipples-8] [EDGE-uvmap-nipples-34]
    {
        Have(UVMapDownloadService.CoreMaps);
        HaveAt(A.Name, V2Bytes[0]);
        using var dl = Service();
        dl.EnsureMapsAsync();
        Thread.Sleep(50);
        Assert.True(fetched.IsEmpty);
        Assert.Empty(Directory.GetFiles(Path.Combine(dataDir, "uvmaps"), "*.staged"));
        dl.EnsureMenMapsAsync();
        WaitFor(() => Length(A.Name) == A.Bytes && Length(B.Name) == B.Bytes);
        Assert.Equal(UVMapDownloadService.MenMaps.Select(f => ("uvmaps-v3", f.Name)), fetched);
        Assert.Empty(Directory.GetFiles(Path.Combine(dataDir, "uvmaps"), "*.staged"));
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-33")]
    public async Task Mixed_pair_becomes_whole_v3_within_two_starts_and_one_fetch() // [EDGE-uvmap-nipples-33]
    {
        Have(UVMapDownloadService.CoreMaps);
        HaveAt(A.Name, A.Bytes); HaveAt(B.Name, V2Bytes[1]);
        using (var dl = Service())
        {
            Assert.Equal(V2Bytes[1], Length(B.Name));
            await dl.StartUpgradeIfStale()!;
        }
        Assert.Equal([("uvmaps-v3", B.Name)], fetched);
        using (var dl = Service())
        {
            Assert.Equal(B.Bytes, Length(B.Name));
            Assert.Null(dl.StartUpgradeIfStale());
        }
        using (var dl = Service()) Assert.Null(dl.StartUpgradeIfStale());
        Assert.Single(fetched);
        Assert.Empty(Directory.GetFiles(Path.Combine(dataDir, "uvmaps"), "*.staged"));
        Assert.Empty(Directory.GetFiles(Path.Combine(dataDir, "uvmaps"), "*.prev"));
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-10")]
    public async Task A_cancelled_upgrade_leaves_no_marker_and_a_throwing_one_does() // [EDGE-uvmap-nipples-10]
    {
        Have(UVMapDownloadService.CoreMaps);
        HaveAt(A.Name, V2Bytes[0]); HaveAt(B.Name, V2Bytes[1]);
        // Stopped mid-fetch (the plugin unloading): not a failure, so tried again at the next start.
        gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dl = Service();
        var task = dl.StartUpgradeIfStale()!;
        dl.Dispose();
        gate.SetResult();
        await task.ContinueWith(_ => { });
        Assert.False(File.Exists(MapPath(UVMapDownloadService.UpgradeFailedMarker)));

        // A fetch that throws rather than failing: the old pair stays, and the day's wait begins.
        gate = null;
        var log = Substitute.For<IPluginLog>();
        using var throwing = new UVMapDownloadService(log, dataDir, null,
            (_, _, _, _, _, _, _) => throw new IOException("disk full"), () => now);
        await throwing.StartUpgradeIfStale()!.ContinueWith(_ => { });
        Assert.True(File.Exists(MapPath(UVMapDownloadService.UpgradeFailedMarker)));
        Assert.Equal(V2Bytes[0], Length(A.Name));
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-28")]
    public void Constructor_restores_both_finals_when_a_crash_left_neither() // [EDGE-uvmap-nipples-28]
    {
        // A crash while a failed swap was putting things back: both finals gone, both old ones beside them.
        HaveAt(A.Name + ".prev", V2Bytes[0]); HaveAt(B.Name + ".prev", V2Bytes[1]);
        HaveAt(A.Name + ".staged", A.Bytes); HaveAt(B.Name + ".staged", B.Bytes);
        using (Service()) { }
        // Rolled back, then swapped whole: a pair, never a half.
        Assert.Equal(A.Bytes, Length(A.Name));
        Assert.Equal(B.Bytes, Length(B.Name));
        Assert.Empty(Directory.GetFiles(Path.Combine(dataDir, "uvmaps"), "*.prev"));
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-16")]
    public void Pinned_v3_sizes_differ_from_v2_sizes() // [EDGE-uvmap-nipples-16]
    {
        // The upgrade tells an old pair by its size: were a new revision the same size, it would never be fetched.
        for (int i = 0; i < 2; i++) Assert.NotEqual(V2Bytes[i], UVMapDownloadService.MenMaps[i].Bytes);
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-7")]
    public void V2_failure_leaves_v1_done_and_v1_arrival_still_raised() // [AC-tbse-uvmap-7]
    {
        failing.Add(UVMapDownloadService.MenMaps[0].Name);
        using var dl = Service();
        int arrived = 0;
        dl.MapsArrived += () => Interlocked.Increment(ref arrived);
        dl.EnsureMapsAsync();
        WaitFor(() => Volatile.Read(ref arrived) == 1);
        dl.EnsureMenMapsAsync();
        WaitFor(() => dl.State == UVMapDownloadState.Failed);
        Assert.True(dl.MapsPresent());
        Assert.Equal(1, Volatile.Read(ref arrived));
        Assert.Contains("404", dl.StatusMessage);
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-15")]
    public void One_automatic_v2_attempt_per_session_and_RetryFailed_refetches() // [EDGE-tbse-uvmap-15]
    {
        Have(UVMapDownloadService.CoreMaps);
        failing.Add(UVMapDownloadService.MenMaps[0].Name);
        using var dl = Service();
        dl.EnsureMenMapsAsync();
        WaitFor(() => dl.State == UVMapDownloadState.Failed);
        int before = fetched.Count;
        dl.EnsureMenMapsAsync();   // a second automatic request does nothing
        Thread.Sleep(50);
        Assert.Equal(before, fetched.Count);

        failing.Clear();
        int arrived = 0;
        dl.MapsArrived += () => Interlocked.Increment(ref arrived);
        dl.RetryFailed();
        WaitFor(() => Volatile.Read(ref arrived) == 1);
        Assert.True(dl.MenMapsPresent());
        Assert.NotEqual(UVMapDownloadState.Failed, dl.State);
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-17")]
    public void Concurrent_sets_complete_independently() // [EDGE-tbse-uvmap-17]
    {
        gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dl = Service();
        int arrived = 0;
        dl.MapsArrived += () => Interlocked.Increment(ref arrived);
        dl.EnsureMapsAsync();
        dl.EnsureMenMapsAsync();
        Assert.Equal(UVMapDownloadState.Downloading, dl.State);
        gate.SetResult();
        WaitFor(() => Volatile.Read(ref arrived) == 2);
        Assert.True(dl.MapsPresent() && dl.MenMapsPresent());
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-18")]
    public void Http_404_on_v2_fails_only_men_set() // [EDGE-tbse-uvmap-18]
    {
        Have(UVMapDownloadService.CoreMaps);
        foreach (var f in UVMapDownloadService.MenMaps) failing.Add(f.Name);
        using var dl = Service();
        dl.EnsureMapsAsync();
        Assert.Equal(UVMapDownloadState.Done, dl.State);
        dl.EnsureMenMapsAsync();
        WaitFor(() => dl.State == UVMapDownloadState.Failed);
        Assert.True(dl.MapsPresent());
        Assert.DoesNotContain(fetched, f => f.Tag == "uvmaps-v1");
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-19")]
    public void Dispose_cancels_both_sets() // [EDGE-tbse-uvmap-19]
    {
        gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dl = Service();
        int arrived = 0;
        dl.MapsArrived += () => Interlocked.Increment(ref arrived);
        dl.EnsureMapsAsync();
        dl.EnsureMenMapsAsync();
        dl.Dispose();
        gate.SetResult();
        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref arrived));
        Assert.True(fetched.IsEmpty, string.Join(", ", fetched));
        Assert.False(dl.MapsPresent(), string.Join(", ", Directory.GetFiles(Path.Combine(dataDir, "uvmaps"))));
    }

    [LocalDataFact(LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-tbse-uvmap-8")]
    [Trait("Plan", "AC-uvmap-nipples-5")]
    public void MenMaps_pin_the_committed_bake() // [AC-tbse-uvmap-8] [AC-uvmap-nipples-5]
    {
        foreach (var f in UVMapDownloadService.MenMaps)
        {
            var bytes = File.ReadAllBytes(Path.Combine(LocalData.Path(LocalData.TbseUvMaps), "uvmaps", f.Name));
            Assert.Equal(f.Bytes, bytes.LongLength);
            Assert.Equal(f.Sha256, UvBakeInputs.Sha256(bytes));
            Assert.Equal("uvmaps-v3", f.Tag);
        }
    }
}

/// <summary>Map downloads joined to the remap and the composite.</summary>
public class UvMapsCoordinatorTests : IDisposable
{
    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "proteus-uvcoord-" + Guid.NewGuid().ToString("N"));

    public UvMapsCoordinatorTests() => Directory.CreateDirectory(Path.Combine(dataDir, "uvmaps"));

    public void Dispose()
    {
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    private bool failMen;

    private Task<FetchResult> Fetch(string[] urls, string name, long bytes, string sha, string dest, Action<long>? progress, CancellationToken ct)
    {
        if (failMen && UVMapDownloadService.MenMaps.Any(f => f.Name == name))
            return Task.FromResult(new FetchResult(false, FetchFailure.Http, 404, 0, ""));
        File.WriteAllBytes(dest, [1]);
        return Task.FromResult(FetchResult.Success);
    }

    private static void WaitFor(Func<bool> done)
    {
        for (int i = 0; i < 3000 && !done(); i++) Thread.Sleep(10);   // 30 s: the full suite starves the thread pool
        Assert.True(done());
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-7")]
    public void Wanted_starts_download_and_arrival_forgets_misses_and_recomposites() // [AC-tbse-uvmap-7]
    {
        var log = Substitute.For<IPluginLog>();
        var remap = new UVRemapService(log, dataDir);
        using var dl = new UVMapDownloadService(log, dataDir, null, Fetch);
        int recomposites = 0;
        using var coord = new UvMapsCoordinator(remap, dl, () => true, () => Interlocked.Increment(ref recomposites));
        var route = new MensRoute("bibo", "tbse");
        remap.MarkRouteMissing(route);

        remap.RequestMenMaps();
        WaitFor(() => Volatile.Read(ref recomposites) == 1);
        Assert.True(dl.MenMapsPresent());
        Assert.True(remap.MensRouteAvailable(route));   // the miss was forgotten on arrival
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-15")]
    public void Retry_success_recomposites() // [EDGE-tbse-uvmap-15]
    {
        var log = Substitute.For<IPluginLog>();
        var remap = new UVRemapService(log, dataDir);
        failMen = true;
        using var dl = new UVMapDownloadService(log, dataDir, null, Fetch);
        int recomposites = 0;
        using var coord = new UvMapsCoordinator(remap, dl, () => true, () => Interlocked.Increment(ref recomposites));
        remap.RequestMenMaps();
        WaitFor(() => dl.State == UVMapDownloadState.Failed);
        Assert.Equal(0, Volatile.Read(ref recomposites));
        failMen = false;
        dl.RetryFailed();
        WaitFor(() => Volatile.Read(ref recomposites) == 1);
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-19")]
    public void No_recomposite_after_dispose() // [EDGE-tbse-uvmap-19]
    {
        var log = Substitute.For<IPluginLog>();
        var remap = new UVRemapService(log, dataDir);
        using var dl = new UVMapDownloadService(log, dataDir, null, Fetch);
        int recomposites = 0;
        var coord = new UvMapsCoordinator(remap, dl, () => true, () => Interlocked.Increment(ref recomposites));
        coord.Dispose();
        dl.EnsureMapsAsync();
        WaitFor(dl.MapsPresent);
        Thread.Sleep(50);
        Assert.Equal(0, Volatile.Read(ref recomposites));
        remap.RequestMenMaps();
        Thread.Sleep(50);
        Assert.False(dl.MenMapsPresent());
    }
}

/// <summary>The upload workflow and the tag → files table it reads.</summary>
public class UploadUvMapsWorkflowTests
{
    private static string RepoFile(params string[] parts)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine([d.FullName, .. parts]);
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-8")]
    [Trait("Plan", "AC-uvmap-nipples-5")]
    [Trait("Plan", "EDGE-uvmap-nipples-17")]
    public void Releases_json_keeps_v1_v2_and_maps_v3_to_tbse_pair() // [AC-tbse-uvmap-8] [AC-uvmap-nipples-5] [EDGE-uvmap-nipples-17]
    {
        var table = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(RepoFile("Proteus", "uvmaps", "releases.json"))!;
        Assert.Equal(UVMapDownloadService.CoreMaps.Select(f => f.Name), table["uvmaps-v1"]);
        Assert.Equal(["bibo_to_tbse_transfer.tif", "tbse_to_bibo_transfer.tif"], table["uvmaps-v2"]);   // a published tag never changes
        Assert.Equal(UVMapDownloadService.MenMaps.Select(f => f.Name), table["uvmaps-v3"]);
        Assert.All(UVMapDownloadService.CoreMaps, f => Assert.Equal("uvmaps-v1", f.Tag));
        Assert.All(UVMapDownloadService.MenMaps, f => Assert.Equal("uvmaps-v3", f.Tag));
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-32")]
    public void Uvmaps_doc_tells_users_to_refit_again() // [EDGE-uvmap-nipples-32]
    {
        var doc = RepoFile("docs", "uvmaps.md");
        Assert.Contains("refit them again", doc);
        Assert.Contains("uvmaps-v3", doc);
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-8")]
    public void Workflow_reads_releases_json_and_hardcodes_no_file_names() // [AC-tbse-uvmap-8]
    {
        var yml = RepoFile(".github", "workflows", "upload-uvmaps.yml");
        Assert.Contains("Proteus\\uvmaps\\releases.json", yml);
        Assert.DoesNotContain("_transfer.tif", yml);
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-30")]
    public void Unknown_tag_untagged_dispatch_pointer_and_existing_release_fail() // [EDGE-tbse-uvmap-30]
    {
        var yml = RepoFile(".github", "workflows", "upload-uvmaps.yml");
        Assert.Contains("required: true", yml);
        Assert.Contains("is not listed in Proteus/uvmaps/releases.json", yml);
        Assert.Contains("No tag: push a uvmaps-* tag", yml);
        Assert.Contains("looks like an LFS pointer", yml);
        Assert.Contains("already exists", yml);
        // The tag comes in through the environment and must look like uvmaps-v<number>; never pasted into the script.
        Assert.Contains("-cnotmatch '^uvmaps-v\\d+\\z'", yml);
        Assert.DoesNotContain("\"${{ github.event.inputs.tag }}\"", yml);
        // Every guard comes before the release is created.
        int create = yml.IndexOf("gh release create", StringComparison.Ordinal);
        foreach (var guard in new[] { "is not listed", "looks like an LFS pointer", "already exists" })
            Assert.True(yml.IndexOf(guard, StringComparison.Ordinal) < create, guard);
    }
}
