using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Proteus's own matcaps: fetched on first use (never at construction), verified against the pinned list, which must equal
/// the committed folder; stop/skip on failure; Try again; presence at the pinned size.
/// </summary>
public sealed class DefaultMatcapsDownloadServiceTests : IDisposable
{
    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "proteus-mcpack-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string[] Urls, string Name, long Bytes, string Sha, string Dest)> calls = new();
    private static readonly (string Name, long Bytes, string Sha256)[] Pins = DefaultMatcapsDownloadService.Matcaps;

    public void Dispose()
    {
        try { Directory.Delete(dataDir, true); } catch { }
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, "run from the checkout: proteus.slnx not found");
        return dir!.FullName;
    }

    internal static string CommittedFolder() => Path.Combine(RepoRoot(), "Proteus", "DefaultMatcaps");

    private string Dir => Path.Combine(dataDir, "DefaultMatcaps");

    /// <summary>A fetch that records the call and, unless <paramref name="fail"/> says otherwise, writes a file of the pinned size.</summary>
    private DefaultMatcapsDownloadService.Fetch Recording(Func<string, FetchResult?>? fail = null, Func<string, Task>? gate = null)
        => async (urls, name, bytes, sha, dest, _, ct) =>
        {
            calls.Enqueue((urls, name, bytes, sha, dest));
            if (gate != null) await gate(name);
            if (fail?.Invoke(name) is { } f) return f;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, new byte[bytes]);
            return FetchResult.Success;
        };

    private DefaultMatcapsDownloadService Service(DefaultMatcapsDownloadService.Fetch fetch) =>
        new(Substitute.For<IPluginLog>(), dataDir, fetch);

    private static void WaitFor(Func<bool> done, int ms = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            Thread.Sleep(5);
        }
    }

    private static void WaitIdle(DefaultMatcapsDownloadService s) =>
        WaitFor(() => s.Status.State is not MatcapPackState.Downloading);

    private void PlaceAll(Func<string, bool>? which = null)
    {
        Directory.CreateDirectory(Dir);
        foreach (var (name, bytes, _) in Pins)
            if (which?.Invoke(name) ?? true) File.WriteAllBytes(Path.Combine(Dir, name), new byte[bytes]);
    }

    private static string Remote(int i) => DefaultMatcapsDownloadService.RemoteName(Pins[i].Name);

    private static FetchResult Http(int code) => new(false, FetchFailure.Http, code, 0, $"HTTP {code}");
    private static readonly FetchResult TransportFail = new(false, FetchFailure.Transport, 0, 0, "connection refused");

    [Fact]
    [Trait("Plan", "AC-matcap-pack-1")]
    public void Constructing_fetches_nothing_until_EnsureAsync()
    {
        using var s = Service(Recording());
        Thread.Sleep(50);
        Assert.Empty(calls);
        Assert.Equal(MatcapPackState.Idle, s.Status.State);

        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal(Pins.Length, calls.Count);
        var expectedBases = ProteusAssets.BaseUrls("matcaps-v1");
        foreach (var c in calls)
        {
            Assert.Equal(expectedBases, c.Urls);
            var pin = Pins.Single(p => DefaultMatcapsDownloadService.RemoteName(p.Name) == c.Name);
            Assert.Equal(Path.Combine(Dir, pin.Name), c.Dest);
        }
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-3")]
    public void Files_at_their_pinned_size_are_not_fetched_again()
    {
        PlaceAll();
        using var s = Service(Recording());
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Empty(calls);
        Assert.Equal(MatcapPackState.Ready, s.Status.State);
        Assert.Equal(Pins.Length, s.Available().Count);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-6")]
    [Trait("Plan", "EDGE-matcap-pack-3")]
    public void Transport_failure_stops_the_run_and_Try_again_fetches_only_the_missing()
    {
        bool failing = true;
        using var s = Service(Recording(n => failing && n == Remote(2) ? TransportFail : null));
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal(3, calls.Count);                      // 1, 2, then 3 fails: nothing after it asked
        Assert.Equal(MatcapPackState.Failed, s.Status.State);
        Assert.NotEmpty(s.Status.Detail);
        Assert.Equal(2, s.Available().Count);

        calls.Clear();
        failing = false;
        s.EnsureAsync(retryFailed: true);
        WaitIdle(s);
        Assert.Equal(Pins.Length - 2, calls.Count);         // 3–39 only
        Assert.Equal(MatcapPackState.Ready, s.Status.State);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-3")]
    public void Transport_failure_on_the_first_file_asks_once()
    {
        using var s = Service(Recording(_ => TransportFail));
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Single(calls);
        Assert.Equal(MatcapPackState.Failed, s.Status.State);
    }

    [Theory]
    [Trait("Plan", "EDGE-matcap-pack-4")]
    [InlineData(404)]
    [InlineData(410)]
    public void Http_404_or_410_skips_that_file_and_continues(int code)
    {
        using var s = Service(Recording(n => n == Remote(2) ? Http(code) : null));
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal(Pins.Length, calls.Count);
        Assert.Equal(MatcapPackState.Failed, s.Status.State);
        Assert.Equal(Pins.Length - 1, s.Available().Count);
    }

    [Theory]
    [Trait("Plan", "EDGE-matcap-pack-17")]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public void Other_Http_status_stops_the_run(int code)
    {
        using var s = Service(Recording(n => n == Remote(2) ? Http(code) : null));
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal(3, calls.Count);
        Assert.Equal(MatcapPackState.Failed, s.Status.State);
        Assert.Contains(code.ToString(), s.Status.Detail);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-7")]
    public void Every_file_404_ends_Failed_and_retry_rearms()
    {
        using var s = Service(Recording(_ => Http(404)));
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal(Pins.Length, calls.Count);
        Assert.Equal(MatcapPackState.Failed, s.Status.State);
        s.EnsureAsync(retryFailed: true);
        WaitIdle(s);
        Assert.Equal(2 * Pins.Length, calls.Count);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-7")]
    public void Pinned_list_matches_the_committed_folder()
    {
        Assert.Equal("matcaps-v1", DefaultMatcapsDownloadService.MatcapsTag);
        var folder = CommittedFolder();
        var onDisk = Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(Pins.Select(p => p.Name).Order(StringComparer.Ordinal), onDisk);
        foreach (var (name, bytes, sha) in Pins)
        {
            var data = File.ReadAllBytes(Path.Combine(folder, name));
            Assert.Equal(bytes, data.LongLength);
            Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
        }
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-7")]
    public void Each_fetch_passes_the_pinned_size_and_sha()
    {
        using var s = Service(Recording());
        s.EnsureAsync();
        WaitIdle(s);
        foreach (var c in calls)
        {
            var pin = Pins.Single(p => DefaultMatcapsDownloadService.RemoteName(p.Name) == c.Name);
            Assert.Equal(pin.Bytes, c.Bytes);
            Assert.Equal(pin.Sha256, c.Sha);
        }
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-8")]
    public void Upload_workflow_publishes_the_committed_folder_on_matcaps_tags_only()
    {
        var yml = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "upload-matcaps.yml"));
        var on = yml[yml.IndexOf("\non:", StringComparison.Ordinal)..yml.IndexOf("\npermissions:", StringComparison.Ordinal)];
        Assert.Contains("tags:", on);
        Assert.Contains("'matcaps-*'", on);
        Assert.DoesNotContain("workflow_dispatch", yml);
        Assert.DoesNotContain("branches", on);
        Assert.Contains("REF_NAME: ${{ github.ref_name }}", yml);
        Assert.Contains("'^matcaps-v\\d+\\z'", yml);
        Assert.Contains("Proteus\\DefaultMatcaps", yml);
        Assert.Contains("-replace ' ', '.'", yml);
        Assert.Contains("gh release view $tag", yml);
        Assert.Contains("exit 1", yml);
        Assert.Contains("--latest=false", yml);
        Assert.DoesNotContain("licen", yml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-9")]
    public void EvenIfReady_rearms_a_Ready_idle_pack_only()
    {
        PlaceAll();
        using var s = Service(Recording());
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal(MatcapPackState.Ready, s.Status.State);
        File.Delete(Path.Combine(Dir, Pins[5].Name));

        s.EnsureAsync(retryFailed: true, evenIfReady: false);
        Thread.Sleep(50);
        WaitIdle(s);
        Assert.Empty(calls);

        s.EnsureAsync(retryFailed: true, evenIfReady: true);
        WaitIdle(s);
        Assert.Single(calls);
        Assert.Equal(Remote(5), calls.Single().Name);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-2")]
    public void Dispose_mid_run_cancels_without_failing()
    {
        var entered = new TaskCompletionSource();
        var s = Service(async (urls, name, bytes, sha, dest, _, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return FetchResult.Success;
        });
        s.EnsureAsync();
        Assert.True(entered.Task.Wait(5000));
        s.Dispose();
        WaitIdle(s);
        Assert.NotEqual(MatcapPackState.Failed, s.Status.State);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-2")]
    public void Available_never_lists_tmp()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllBytes(Path.Combine(Dir, "Gold Metal.png.tmp"), new byte[10]);
        using var s = Service(Recording());
        Assert.Empty(s.Available());
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-5")]
    public void Wrong_size_file_is_refetched_and_not_listed()
    {
        PlaceAll(n => n != "Gold Metal.png");
        File.WriteAllBytes(Path.Combine(Dir, "Gold Metal.png"), new byte[10]);
        using var s = Service(Recording());
        Assert.DoesNotContain(s.Available(), i => i.Name == "Gold Metal.png");
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal("Gold.Metal.png", Assert.Single(calls).Name);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-6")]
    public void Available_lists_only_pinned_names()
    {
        PlaceAll();
        File.WriteAllBytes(Path.Combine(Dir, "stray.png"), new byte[5]);
        File.WriteAllBytes(Path.Combine(Dir, "x.png.tmp"), new byte[5]);
        using var s = Service(Recording());
        var names = s.Available().Select(i => i.Name).ToList();
        Assert.Equal(Pins.Length, names.Count);
        Assert.DoesNotContain("stray.png", names);
        Assert.All(s.Available(), i => Assert.True(i.FromPack));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-9")]
    public void EnsureAsync_twice_runs_once_and_retry_only_rearms_a_failed_idle_run()
    {
        var release = new SemaphoreSlim(0);
        using var s = Service(Recording(gate: async _ => await release.WaitAsync()));
        s.EnsureAsync();
        s.EnsureAsync();
        s.EnsureAsync(retryFailed: true);            // Downloading: no new run
        s.EnsureAsync(evenIfReady: true);            // Downloading: no new run
        release.Release(1000);
        WaitIdle(s);
        Assert.Equal(Pins.Length, calls.Count);
        s.EnsureAsync(retryFailed: true);            // Ready: no new run
        Thread.Sleep(50);
        Assert.Equal(Pins.Length, calls.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-11")]
    public void Every_committed_pack_image_is_square_and_bakes_at_its_own_size()
    {
        foreach (var (name, _, _) in Pins)
        {
            using var fs = File.OpenRead(Path.Combine(CommittedFolder(), name));
            var info = StbImageSharp.ImageInfo.FromStream(fs);
            Assert.True(info.HasValue, name);
            Assert.Equal(info!.Value.Width, info.Value.Height);
            var side = MatcapImages.BakeSide(info.Value.Width, info.Value.Height, out _);
            Assert.Equal(name == "copper.png" ? 256 : 512, side);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-12")]
    public void Remote_names_are_unique_and_match_the_worker_class()
    {
        var remote = Pins.Select(p => DefaultMatcapsDownloadService.RemoteName(p.Name)).ToList();
        Assert.Equal(remote.Count, remote.Distinct(StringComparer.Ordinal).Count());
        Assert.All(remote, r => Assert.Matches(new Regex(@"^[A-Za-z0-9_.-]{1,80}\.png$"), r));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-13")]
    public void Deleted_file_alone_is_fetched_again()
    {
        PlaceAll(n => n != Pins[7].Name);
        using var s = Service(Recording());
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal(Remote(7), Assert.Single(calls).Name);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-18")]
    public void Unexpected_exception_fails_and_retry_rearms()
    {
        int attempt = 0;
        using var s = Service((urls, name, bytes, sha, dest, _, ct) =>
        {
            if (Interlocked.Increment(ref attempt) == 2) throw new UnauthorizedAccessException("denied");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, new byte[bytes]);
            return Task.FromResult(FetchResult.Success);
        });
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal(MatcapPackState.Failed, s.Status.State);
        Assert.Contains("denied", s.Status.Detail);
        s.EnsureAsync(retryFailed: true);
        WaitIdle(s);
        Assert.Equal(MatcapPackState.Ready, s.Status.State);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-18")]
    public void Stray_cancellation_fails_and_retry_rearms()
    {
        bool throwIt = true;
        using var s = Service((urls, name, bytes, sha, dest, _, ct) =>
        {
            if (throwIt) throw new OperationCanceledException();
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, new byte[bytes]);
            return Task.FromResult(FetchResult.Success);
        });
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal(MatcapPackState.Failed, s.Status.State);
        throwIt = false;
        s.EnsureAsync(retryFailed: true);           // proves the active-run flag was cleared
        WaitIdle(s);
        Assert.Equal(MatcapPackState.Ready, s.Status.State);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-18")]
    public void Exception_after_Dispose_is_not_a_failure()
    {
        var entered = new TaskCompletionSource();
        var go = new TaskCompletionSource();
        var s = Service(async (urls, name, bytes, sha, dest, _, ct) =>
        {
            entered.TrySetResult();
            await go.Task;
            throw new ObjectDisposedException("http");
        });
        s.EnsureAsync();
        Assert.True(entered.Task.Wait(5000));
        s.Dispose();
        go.SetResult();
        WaitIdle(s);
        Assert.NotEqual(MatcapPackState.Failed, s.Status.State);
    }
}
