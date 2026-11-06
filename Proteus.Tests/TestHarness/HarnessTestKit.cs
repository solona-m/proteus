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
using Proteus.Interop;
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
    public bool OnFrameworkThread => OnFramework;

    /// <summary>Run <paramref name="work"/> on the fake framework thread and wait for it.</summary>
    public T Run<T>(Func<T> work) => Post(work).GetAwaiter().GetResult();
    public void Run(Action work) => Post(() => { work(); return 0; }).GetAwaiter().GetResult();

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
internal sealed partial class FakeGame : IHarnessGame
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

    /// <summary>What Penumbra answers to the reload (false: it threw, as it can after options were taken away).</summary>
    public bool ReloadSucceeds { get; set; } = true;
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
                            new AutoRefitInfo(View, Activity))
        {
            Gear = ReadGear(),
        };
    }

    // ── equip and hold ──────────────────────────────────────────────────────

    /// <summary>What Glamourer says is worn; every slot starts as item 1000+index with no dyes.</summary>
    public Dictionary<string, Worn> Gear { get; } =
        HarnessGear.Slots.Select((s, i) => (s, i)).ToDictionary(t => t.s, t => new Worn(1000UL + (ulong)t.i, 0, 0));
    public bool GlamourerUp { get; set; } = true;
    /// <summary>The next N gear reads fail (null) once this many reads have gone by; -1 never.</summary>
    public int GearReadsBeforeFailure { get; set; } = -1;
    /// <summary>Glamourer's answer to SetGear: null = Success.</summary>
    public Func<string, ulong, string?> SetGearAnswer { get; set; } = (_, _) => null;
    /// <summary>What Glamourer stores for a sent id (it may normalise a custom id to an item id).</summary>
    public Func<string, ulong, ulong> Normalise { get; set; } = (_, id) => id;
    public List<(string Slot, ulong Id, byte Dye1, byte Dye2)> GearSets { get; } = [];
    public HashSet<string> Carriers { get; } = new(StringComparer.Ordinal);

    public Dictionary<(Guid, string), PenumbraBridge.TemporaryHold> Temporary { get; } = new();
    public Dictionary<(Guid, string), int> TemporaryKeys { get; } = new();
    public bool QueryFails { get; set; }
    public Dictionary<string, PenumbraBridge.SettingsRead> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ReadThrows { get; set; }
    public string? SetHoldAnswer { get; set; }
    public string? ClearHoldAnswer { get; set; }
    public bool ClearHoldThrows { get; set; }
    public List<(Guid Collection, string Dir, bool Enabled, int Priority, IReadOnlyDictionary<string, List<string>> Options, string Source, int Key)> HoldSets { get; } = [];
    public List<(Guid Collection, int Key)> Sweeps { get; } = [];
    public Func<Guid, int, string>? SweepAnswer { get; set; }
    public int RedrawRequests;
    public Action<string>? OnSetGear { get; set; }

    private Dictionary<string, Worn>? ReadGear()
    {
        if (!GlamourerUp || !LoggedIn) return null;
        if (GearReadsBeforeFailure == 0) return null;
        if (GearReadsBeforeFailure > 0) GearReadsBeforeFailure--;
        return new Dictionary<string, Worn>(Gear);
    }

    public IReadOnlyDictionary<string, Worn>? GearNow() { Fw(nameof(GearNow)); return ReadGear(); }

    public bool PlayerPresent() { Fw(nameof(PlayerPresent)); return LoggedIn; }

    public string? SetGear(string slot, ulong id, byte dye1, byte dye2)
    {
        Fw(nameof(SetGear));
        OnSetGear?.Invoke(slot);
        if (!GlamourerUp) return "unavailable";
        if (SetGearAnswer(slot, id) is { } ec) return ec;
        GearSets.Add((slot, id, dye1, dye2));
        Gear[slot] = new Worn(Normalise(slot, id), dye1, dye2);
        return null;
    }

    public IReadOnlyCollection<string> CarrierSlots() { Fw(nameof(CarrierSlots)); return Carriers.ToList(); }

    public Guid? PlayerCollection() { Fw(nameof(PlayerCollection)); return PenumbraUp ? Collection : null; }

    public PenumbraBridge.TemporaryHold? QueryHold(Guid collection, string modDir)
    {
        Fw(nameof(QueryHold));
        if (QueryFails || !PenumbraUp) return null;
        return Temporary.TryGetValue((collection, modDir), out var h) ? h : new PenumbraBridge.TemporaryHold(false, false, "", false, 0);
    }

    public PenumbraBridge.SettingsRead ReadSettings(Guid collection, string modDir)
    {
        Fw(nameof(ReadSettings));
        if (ReadThrows) throw new InvalidOperationException("ipc gone");
        return Settings.TryGetValue(modDir, out var r) ? r : new PenumbraBridge.SettingsRead(PenumbraBridge.SettingsReadKind.None, null);
    }

    public string? SetHold(Guid collection, string modDir, bool enabled, int priority,
                           IReadOnlyDictionary<string, List<string>> options, string source, int key)
    {
        Fw(nameof(SetHold));
        if (SetHoldAnswer != null) return SetHoldAnswer;
        HoldSets.Add((collection, modDir, enabled, priority, options, source, key));
        Temporary[(collection, modDir)] = new PenumbraBridge.TemporaryHold(true, false, source, enabled, priority);
        TemporaryKeys[(collection, modDir)] = key;
        return null;
    }

    public string? ClearHold(Guid collection, string modDir)
    {
        Fw(nameof(ClearHold));
        if (ClearHoldThrows) throw new InvalidOperationException("ipc gone");
        if (ClearHoldAnswer != null) return ClearHoldAnswer;
        if (!Temporary.Remove((collection, modDir))) return "NothingChanged";
        TemporaryKeys.Remove((collection, modDir));
        return null;
    }

    public string SweepHolds(Guid collection, int key)
    {
        Fw(nameof(SweepHolds));
        Sweeps.Add((collection, key));
        if (SweepAnswer != null) return SweepAnswer(collection, key);
        var mine = TemporaryKeys.Where(kv => kv.Key.Item1 == collection && kv.Value == key).Select(kv => kv.Key).ToList();
        foreach (var k in mine) { Temporary.Remove(k); TemporaryKeys.Remove(k); }
        return mine.Count > 0 ? "Success" : "NothingChanged";
    }

    public void RequestRedraw() { Fw(nameof(RequestRedraw)); Interlocked.Increment(ref RedrawRequests); }

    /// <summary>The scripted Studio; set to null for a build with none.</summary>
    public FakeStudio? FakeStudio { get; set; }

    public IStudioHarness? Studio => FakeStudio;

    /// <summary>A fresh scripted Studio whose members record the thread they ran on into <see cref="Calls"/>.</summary>
    public FakeStudio UseStudio() => FakeStudio = new FakeStudio(Fw);

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
    /// <summary>Thrown by OfferedKeys when set: a mod manifest that cannot be read.</summary>
    public Exception? OfferedThrows { get; set; }
    public IReadOnlyList<(string Key, string Body)> OfferedKeys(string modRoot)
    {
        Order.Enqueue("OfferedKeys@" + Where());
        if (OfferedThrows != null) throw OfferedThrows;
        return Offered;
    }
    public IReadOnlySet<string> InstalledKeys(ModList mods)
    {
        Order.Enqueue("InstalledKeys@" + Where());
        AfterInstalledKeys?.Invoke();
        return Installed;
    }
    private string Where() => dispatcher.OnFramework ? "framework" : "worker";

    public RefitOptions Options(bool remake) => new(true, true, false, remake);

    /// <summary>Thrown by PrepareSizeSet when set: the game's bodies or the skeletons could not be read.</summary>
    public Exception? PrepareThrows { get; set; }
    public SizeSetLaunch.Prepared PrepareSizeSet(string modRoot, IReadOnlyList<string> keys, RefitOptions options, ModList mods)
    {
        Fw(nameof(PrepareSizeSet));
        if (PrepareThrows != null) throw PrepareThrows;
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
        return new AfterChangeResult(ReloadSucceeds, true, baseline);
    }

    public uint ViewportId() { Fw(nameof(ViewportId)); return 7; }

    public ShaderUpgradeWatcher.View ShaderUpgradeView { get; set; } = new();
    public ShaderUpgradeWatcher.View ShaderUpgrade() => ShaderUpgradeView;
    public bool ShaderUpgradeEnabled { get; set; }
    public string ShaderUpgradeGloss { get; set; } = "inverse-specular";
    public (bool Enabled, string MaskGloss) SetShaderUpgrade(bool? enabled, string? maskGloss)
    {
        Fw(nameof(SetShaderUpgrade));
        if (enabled is { } on) ShaderUpgradeEnabled = on;
        if (maskGloss != null) ShaderUpgradeGloss = maskGloss;
        return (ShaderUpgradeEnabled, ShaderUpgradeGloss);
    }

    public bool ShaderUpgradeBusy { get; set; }
    public List<(bool UndoWorn, bool Decline)> ShaderUpgradeRuns { get; } = [];
    public bool StartShaderUpgradeRun(bool undoWorn, bool decline)
    {
        Fw(nameof(StartShaderUpgradeRun));
        if (ShaderUpgradeBusy) return false;
        ShaderUpgradeRuns.Add((undoWorn, decline));
        return true;
    }

    public VariantRepointWatcher.View VariantRepointView { get; set; } = new();
    public VariantRepointWatcher.View VariantRepoint() => VariantRepointView;
    public bool VariantRepointEnabled { get; set; }
    public bool SetVariantRepoint(bool enabled)
    {
        Fw(nameof(SetVariantRepoint));
        VariantRepointEnabled = enabled;
        return VariantRepointEnabled;
    }

    /// <summary>The shader upgrade or auto refit is working.</summary>
    public bool VariantRepointOthersBusy { get; set; }
    public bool VariantRepointBlocked() { Fw(nameof(VariantRepointBlocked)); return VariantRepointOthersBusy; }

    public bool VariantRepointBusy { get; set; }
    public List<(bool UndoWorn, bool Decline)> VariantRepointRuns { get; } = [];
    public bool StartVariantRepointRun(bool undoWorn, bool decline)
    {
        Fw(nameof(StartVariantRepointRun));
        if (VariantRepointBusy) return false;
        VariantRepointRuns.Add((undoWorn, decline));
        return true;
    }

    public Task<RawFrame> CaptureRaw(uint viewportId, bool includeUi, CancellationToken ct)
    {
        LastCaptureUi = includeUi;
        return Capture(viewportId, includeUi, ct);
    }

    public bool? LastCaptureUi { get; private set; }

    /// <summary>The automatic hair path as /state and the busy rules see it.</summary>
    public HairPortInfo HairPortView { get; set; } = new(new HairPortWatcher.View(), new HairPortWatcher.ScanView(), false, true, false);
    public HairPortInfo HairPort() => HairPortView;

    /// <summary>The game's own files a hair port reads: heads, hair models, materials, skeletons, the racial deformer.</summary>
    public Dictionary<string, byte[]> GameFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Runs on the reading thread before each game-file read: where a test stalls a bend.</summary>
    public Action<string>? OnReadGameFile { get; set; }
    public byte[]? ReadGameFile(string gamePath)
    {
        Order.Enqueue("ReadGameFile@" + Where());
        OnReadGameFile?.Invoke(gamePath);
        return GameFiles.TryGetValue(gamePath, out var b) ? b : null;
    }

    public bool IsHairMod(string modRoot)
    {
        Order.Enqueue("IsHairMod@" + Where());
        return HairPortJob.IsHairMod(modRoot);
    }
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
    public HarnessChanges Changes { get; }
    public HarnessServer Server { get; }
    public HttpClient Client { get; }
    public string Token { get; } = HarnessAuth.NewToken();
    public string ModsRoot { get; }
    public bool Disposing { get; set; }
    public IPluginLog Log { get; } = Substitute.For<IPluginLog>();

    public static readonly HarnessTimings FastTimings = new(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(600),
                                                            TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(30),
                                                            TimeSpan.FromMilliseconds(400));

    public HarnessFixture(HarnessTimings? timings = null, Func<string>? backupIds = null, long refitCap = ModBackup.DefaultRefitCap,
                          string session = "session-a", TimeSpan? readTimeout = null)
    {
        ModsRoot = Temp.Sub("mods");
        Game = new FakeGame(Dispatcher, ModsRoot);
        Backups = new ModBackup(System.IO.Path.Combine(Temp.Path, "config", "test-harness", "backups"), backupIds, refitCap,
                                _ => long.MaxValue);
        Jobs = new HarnessJobs(Game, Dispatcher, Backups, session, () => Disposing, timings ?? FastTimings);
        Ring = new LogRing(Names);
        Changes = new HarnessChanges(Game, Names, Log, System.IO.Path.Combine(Temp.Path, "config", "test-harness", "backups"),
                                     session, 1411);
        Router = new HarnessRouter(Jobs, Ring, Names, new CaptureGate((timings ?? FastTimings).Capture), 1411, "11-01 12:00:00",
                                   Substitute.For<IPluginLog>(), Changes);
        Server = new HarnessServer(Router.Handle, Token, readTimeout ?? TimeSpan.FromMilliseconds(500));
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

    /// <summary>A women's hair mod (c0201 h0005 in option Cut / Long) installed, Penumbra knowing it, and the game's files
    /// a port of it reads — the racial deformer among them.</summary>
    public HairPortFixtures.Mod AddHairMod(string dir = "Hair", bool physics = false)
    {
        var mod = CrossSexFixtures.WomensMod(physics: physics, root: System.IO.Path.Combine(ModsRoot, dir));
        Game.ModFolders[dir] = dir + " (display)";
        Game.GameFiles = physics ? CrossSexFixtures.PhysicsFiles() : CrossSexFixtures.GameFiles();
        Game.GameFiles[Proteus.Interop.LiveCharacter.PbdGamePath] = CrossSexFixtures.PbdBytes();
        return mod;
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
