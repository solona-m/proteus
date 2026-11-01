using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Proteus.TestHarness;

namespace Proteus.Tests.TestHarness;

/// <summary>
/// The game's framework thread, stood in for by one dedicated thread. It records its id, and can be stalled (work
/// queues up and waits) and resumed, so timeouts and never-abandoned hops are tested for real.
/// </summary>
internal sealed class FakeFrameworkDispatcher : IFrameworkDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread thread;
    private readonly ManualResetEventSlim running = new(true);

    public int ThreadId { get; }

    public FakeFrameworkDispatcher()
    {
        var ready = new ManualResetEventSlim();
        int id = 0;
        thread = new Thread(() =>
        {
            id = Environment.CurrentManagedThreadId;
            ready.Set();
            foreach (var work in queue.GetConsumingEnumerable())
            {
                running.Wait();
                work();
            }
        }) { IsBackground = true, Name = "fake framework" };
        thread.Start();
        ready.Wait();
        ThreadId = id;
    }

    public bool OnFramework => Environment.CurrentManagedThreadId == ThreadId;

    public void Stall() => running.Reset();
    public void Resume() => running.Set();

    /// <summary>Faults the next unabandonable post instead of running it (a framework shut down under it).</summary>
    public bool FaultNextUnabandonable { get; set; }

    private Task<T> Post<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    public Task<T> RunAsync<T>(Func<T> work, TimeSpan startTimeout, CancellationToken ct = default)
    {
        var ticket = new DispatchTicket();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Post(() =>
        {
            if (!ticket.TryStart()) return default!;
            started.TrySetResult();
            return work();
        });
        return DispatchWait.Await(ticket, started.Task, run, startTimeout, ct);
    }

    public Task<bool> RunUnabandonable(Action work, Func<bool> skip)
    {
        if (FaultNextUnabandonable)
        {
            FaultNextUnabandonable = false;
            return Task.FromException<bool>(new TaskCanceledException("framework gone"));
        }
        return Post(() =>
        {
            if (skip()) return false;
            work();
            return true;
        });
    }

    public void Dispose()
    {
        running.Set();
        queue.CompleteAdding();
    }
}

/// <summary>The game as the harness sees it, scripted. Framework-thread members record the thread they ran on.</summary>
internal sealed class FakeGame : IHarnessGame
{
    private readonly FakeFrameworkDispatcher dispatcher;
    public FakeGame(FakeFrameworkDispatcher dispatcher, string modsRoot)
    {
        this.dispatcher = dispatcher;
        ModsRoot = modsRoot;
    }

    public string ModsRoot { get; }
    public Dictionary<string, string> ModFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool PenumbraUp { get; set; } = true;
    public bool LoggedIn { get; set; } = true;
    public CharacterInfo? Character { get; set; } = new(1, 2, 1, 1, "c0201");
    public Guid? Collection { get; set; } = Guid.Parse("11111111-2222-3333-4444-555555555555");
    public string CollectionName { get; set; } = "Secret Collection";
    public string? CharacterName { get; set; } = "Hidden Person";
    public List<(int Slot, string? File)> Loaded { get; set; } = [];
    public Dictionary<string, HashSet<string>>? ResourceMap { get; set; }
    public CompositorService.ActivitySnapshot Compositor { get; set; }
    public AutoRefitWatcher.View View { get; set; } = new();
    public AutoRefitWatcher.ActivitySnapshot Activity { get; set; }
    public HashSet<string> Legacy { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Key, string Body)> Offered { get; set; } = [("rue", "rue"), ("bibo", "bibo"), ("tbse@women", "tbse")];
    public HashSet<string> Installed { get; set; } = new(StringComparer.Ordinal) { "rue", "bibo", "tbse" };

    /// <summary>The run's body (SizeSetLaunch's run seam): by default writes a size into the mod and returns 1.</summary>
    public Func<SizeSetRefit.Request, SizeSetRefit.RunLog, CancellationToken, int> RunBody { get; set; }
        = (request, _, _) =>
        {
            File.WriteAllText(Path.Combine(request.ModRoot, "refit_" + Guid.NewGuid().ToString("N")[..6] + ".mdl"), "made");
            return 1;
        };

    /// <summary>Redraws arrive by themselves after an after-change asks for one, unless turned off.</summary>
    public bool RedrawArrives { get; set; } = true;
    /// <summary>The redraw lands before the after-change returns (it must still be observed).</summary>
    public bool RedrawBeforeReturn { get; set; }
    public Func<uint, bool, CancellationToken, Task<RawFrame>> Capture { get; set; }
        = (_, _, _) => Task.FromResult(new RawFrame(2, 1, 8, FramePng.B8G8R8A8Unorm, [0, 0, 255, 255, 0, 255, 0, 255]));

    public ConcurrentQueue<(string Member, int Thread)> Calls { get; } = new();
    public ConcurrentQueue<string> Order { get; } = new();
    public int AfterChanges;
    public List<string> StudioRefreshed { get; } = [];

    private long redraws;
    public long RedrawCount => Interlocked.Read(ref redraws);
    public void Redraw() => Interlocked.Increment(ref redraws);
    public DateTime? LastRedrawUtc => RedrawCount > 0 ? DateTime.UtcNow : null;

    public SessionNames? NameSink { get; set; }
    public string ManagedModDir => SidecarDiscoveryService.ManagedModDir;

    private void Fw(string member)
    {
        Calls.Enqueue((member, Environment.CurrentManagedThreadId));
        Order.Enqueue(member + (dispatcher.OnFramework ? "@framework" : "@worker"));
    }

    public HealthInfo Health() { Fw(nameof(Health)); return new HealthInfo(PenumbraUp, LoggedIn, true); }

    public RawState State()
    {
        Fw(nameof(State));
        return new RawState(LoggedIn ? Character : null, LoggedIn && PenumbraUp ? Collection : null, LoggedIn ? Loaded : [],
                            PenumbraUp ? ResourceMap : null, PenumbraUp ? ModsRoot : null, Compositor, null, null,
                            new AutoRefitInfo(View, Activity));
    }

    public (IReadOnlyCollection<string> Collections, string? Character) NamesNow()
    {
        Fw(nameof(NamesNow));
        return ([CollectionName], CharacterName);
    }

    public ModList? Mods()
    {
        Fw(nameof(Mods));
        return PenumbraUp ? new ModList(new Dictionary<string, string>(ModFolders), ModsRoot) : null;
    }

    public AutoRefitInfo AutoRefit() => new(View, Activity);

    /// <summary>Runs on the job's worker at its first step (refit and restore): where a test stalls or cancels.</summary>
    public Action? OnLegacyCheck { get; set; }
    /// <summary>Runs on the refit's worker once the bodies are read, before the prepare hop.</summary>
    public Action? AfterInstalledKeys { get; set; }
    /// <summary>Runs inside the after-change, on the framework thread.</summary>
    public Action? OnAfterChange { get; set; }

    public bool IsLegacyFolder(string modRoot)
    {
        Order.Enqueue("IsLegacy@" + Where());
        OnLegacyCheck?.Invoke();
        return Legacy.Contains(Path.GetFileName(modRoot));
    }
    public IReadOnlyList<(string Key, string Body)> OfferedKeys(string modRoot) { Order.Enqueue("OfferedKeys@" + Where()); return Offered; }
    public IReadOnlySet<string> InstalledKeys(ModList mods)
    {
        Order.Enqueue("InstalledKeys@" + Where());
        AfterInstalledKeys?.Invoke();
        return Installed;
    }
    private string Where() => dispatcher.OnFramework ? "framework" : "worker";

    public RefitOptions Options(bool remake) => new(true, true, false, remake);

    public SizeSetLaunch.Prepared PrepareSizeSet(string modRoot, IReadOnlyList<string> keys, RefitOptions options, ModList mods)
    {
        Fw(nameof(PrepareSizeSet));
        return new SizeSetLaunch.Prepared(new SizeSetRefit.Request(modRoot, keys, options.Yas, options.Remake, []),
                                          mods.Mods, mods.ModsRoot);
    }

    public Task<SizeSetLaunch.Outcome> RunSizeSet(SizeSetLaunch.Prepared prepared, Action<string> progress, Action afterChange,
                                                  Action<Action> postToFramework, CancellationToken ct)
    {
        Order.Enqueue("RunSizeSet@" + Where());
        return SizeSetLaunch.Start(prepared, new BodyModIndex(), null, Substitute.For<IPluginLog>(), progress, afterChange,
                                   postToFramework, ct, RunBody);
    }

    public AfterChangeResult AfterModChange(string modDir)
    {
        Fw(nameof(AfterModChange));
        Interlocked.Increment(ref AfterChanges);
        long baseline = RedrawCount;
        StudioRefreshed.Add(modDir);
        OnAfterChange?.Invoke();
        if (RedrawBeforeReturn) Redraw();
        else if (RedrawArrives) _ = Task.Delay(30).ContinueWith(_ => Redraw());
        return new AfterChangeResult(true, true, baseline);
    }

    public uint ViewportId() { Fw(nameof(ViewportId)); return 7; }

    public Task<RawFrame> CaptureRaw(uint viewportId, bool includeUi, CancellationToken ct)
    {
        LastCaptureUi = includeUi;
        return Capture(viewportId, includeUi, ct);
    }

    public bool? LastCaptureUi { get; private set; }
}

/// <summary>A temp folder deleted at the end.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "proteus-harness-tests", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string Sub(params string[] parts)
    {
        var p = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }
    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, true);
        }
        catch { /* best effort */ }
    }
}

/// <summary>
/// A real loopback harness server over a fake game: dispatcher, jobs, backups, log ring, router and an HttpClient
/// that carries the token.
/// </summary>
internal sealed class HarnessFixture : IDisposable
{
    public TempDir Temp { get; } = new();
    public FakeFrameworkDispatcher Dispatcher { get; } = new();
    public FakeGame Game { get; }
    public ModBackup Backups { get; }
    public HarnessJobs Jobs { get; }
    public SessionNames Names { get; } = new();
    public LogRing Ring { get; }
    public HarnessRouter Router { get; }
    public HarnessServer Server { get; }
    public HttpClient Client { get; }
    public string Token { get; } = HarnessAuth.NewToken();
    public string ModsRoot { get; }
    public bool Disposing { get; set; }

    public static readonly HarnessTimings FastTimings = new(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(600),
                                                            TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(30),
                                                            TimeSpan.FromMilliseconds(400));

    public HarnessFixture(HarnessTimings? timings = null, Func<string>? backupIds = null, long refitCap = ModBackup.DefaultRefitCap,
                          string session = "session-a")
    {
        ModsRoot = Temp.Sub("mods");
        Game = new FakeGame(Dispatcher, ModsRoot);
        Backups = new ModBackup(System.IO.Path.Combine(Temp.Path, "config", "test-harness", "backups"), backupIds, refitCap,
                                _ => long.MaxValue);
        Jobs = new HarnessJobs(Game, Dispatcher, Backups, session, () => Disposing, timings ?? FastTimings);
        Ring = new LogRing(Names);
        Router = new HarnessRouter(Jobs, Ring, Names, new CaptureGate((timings ?? FastTimings).Capture), 1411, "11-01 12:00:00",
                                   Substitute.For<IPluginLog>());
        Server = new HarnessServer(Router.Handle, Token, TimeSpan.FromMilliseconds(500));
        Server.Start();
        Client = NewClient();
    }

    public HttpClient NewClient(string? token = null)
    {
        var c = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Server.Port}"), Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Token);
        return c;
    }

    /// <summary>An installed mod folder with a few files (nested), and Penumbra knowing it.</summary>
    public string AddMod(string dir = "Outfit")
    {
        string root = Temp.Sub("mods", dir);
        File.WriteAllText(System.IO.Path.Combine(root, "meta.json"), "{\"FileVersion\":3}");
        File.WriteAllText(System.IO.Path.Combine(root, "default_mod.json"), "{}");
        Directory.CreateDirectory(System.IO.Path.Combine(root, "chara", "model"));
        File.WriteAllText(System.IO.Path.Combine(root, "chara", "model", "top.mdl"), "original");
        File.SetLastWriteTimeUtc(System.IO.Path.Combine(root, "chara", "model", "top.mdl"), new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        Game.ModFolders[dir] = dir + " (display)";
        return root;
    }

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public async Task<(int Status, JsonElement Body)> Get(string path)
    {
        var r = await Client.GetAsync(path);
        return ((int)r.StatusCode, await Parse(r));
    }

    public async Task<(int Status, JsonElement Body)> Post(string path, string json)
    {
        var r = await Client.PostAsync(path, Json(json));
        return ((int)r.StatusCode, await Parse(r));
    }

    public static async Task<JsonElement> Parse(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        if (text.Length == 0 || r.Content.Headers.ContentType?.MediaType != "application/json") return default;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<JsonElement> WaitJob(string id, int timeoutMs = 10_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var (status, body) = await Get($"/jobs/{id}");
            if (status == 200 && body.GetProperty("endedAt").ValueKind != JsonValueKind.Null) return body;
            if (DateTime.UtcNow > until) throw new TimeoutException($"job {id} did not end: {body}");
            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
        Dispatcher.Dispose();
        Temp.Dispose();
    }
}

/// <summary>Fingerprints of a folder for byte-identity checks: relative path → (bytes, last-write).</summary>
internal static class FolderState
{
    public static Dictionary<string, (string Content, DateTime Mtime)> Of(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .ToDictionary(f => System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'),
                                  f => (Convert.ToBase64String(File.ReadAllBytes(f)), File.GetLastWriteTimeUtc(f)),
                                  StringComparer.OrdinalIgnoreCase);

    public static void AssertSame(Dictionary<string, (string Content, DateTime Mtime)> expected, string root)
    {
        var actual = Of(root);
        Xunit.Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase),
                           actual.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        foreach (var (k, v) in expected) Xunit.Assert.Equal(v, actual[k]);
    }
}
