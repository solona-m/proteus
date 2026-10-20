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
        File.WriteAllBytes(dest, [1]);
        return FetchResult.Success;
    }

    private UVMapDownloadService Service() => new(Substitute.For<IPluginLog>(), dataDir, null, Fetch);

    private static void WaitFor(Func<bool> done)
    {
        for (int i = 0; i < 500 && !done(); i++) Thread.Sleep(10);
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
    public void Men_maps_fetch_from_uvmaps_v2_on_demand() // [AC-tbse-uvmap-7]
    {
        Have(UVMapDownloadService.CoreMaps);
        using var dl = Service();
        dl.EnsureMenMapsAsync();
        WaitFor(dl.MenMapsPresent);
        Assert.Equal(UVMapDownloadService.MenMaps.Select(f => ("uvmaps-v2", f.Name)), fetched);
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
    public void MenMaps_pin_the_committed_bake() // [AC-tbse-uvmap-8]
    {
        foreach (var f in UVMapDownloadService.MenMaps)
        {
            var bytes = File.ReadAllBytes(Path.Combine(LocalData.Path(LocalData.TbseUvMaps), "uvmaps", f.Name));
            Assert.Equal(f.Bytes, bytes.LongLength);
            Assert.Equal(f.Sha256, UvBakeInputs.Sha256(bytes));
            Assert.Equal("uvmaps-v2", f.Tag);
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
        for (int i = 0; i < 500 && !done(); i++) Thread.Sleep(10);
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
    public void Releases_json_maps_v1_to_gen3_pair_and_v2_to_tbse_pair_matching_client_sets() // [AC-tbse-uvmap-8]
    {
        var table = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(RepoFile("Proteus", "uvmaps", "releases.json"))!;
        Assert.Equal(UVMapDownloadService.CoreMaps.Select(f => f.Name), table["uvmaps-v1"]);
        Assert.Equal(UVMapDownloadService.MenMaps.Select(f => f.Name), table["uvmaps-v2"]);
        Assert.All(UVMapDownloadService.CoreMaps, f => Assert.Equal("uvmaps-v1", f.Tag));
        Assert.All(UVMapDownloadService.MenMaps, f => Assert.Equal("uvmaps-v2", f.Tag));
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
        Assert.Contains("'^uvmaps-v\\d+$'", yml);
        Assert.DoesNotContain("\"${{ github.event.inputs.tag }}\"", yml);
        // Every guard comes before the release is created.
        int create = yml.IndexOf("gh release create", StringComparison.Ordinal);
        foreach (var guard in new[] { "is not listed", "looks like an LFS pointer", "already exists" })
            Assert.True(yml.IndexOf(guard, StringComparison.Ordinal) < create, guard);
    }
}
