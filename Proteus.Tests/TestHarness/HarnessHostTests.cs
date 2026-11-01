using System;
using System.Collections.Concurrent;
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
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

public class HarnessHostTests : IDisposable
{
    private readonly TempDir temp = new();
    private readonly FakeFrameworkDispatcher dispatcher = new();
    private readonly FakeGame game;
    private readonly IPluginLog log = Substitute.For<IPluginLog>();
    private Action<string>? observer;
    private readonly string configDir;

    public HarnessHostTests()
    {
        configDir = temp.Sub("config");
        game = new FakeGame(dispatcher, temp.Sub("mods"));
    }

    public void Dispose()
    {
        dispatcher.Dispose();
        temp.Dispose();
    }

    private HarnessHost Host(bool isDev = true, IPluginLog? withLog = null)
        => new(configDir, isDev, game, dispatcher, o => observer = o, withLog ?? log, 1411, "stamp", HarnessFixture.FastTimings);

    private HarnessSession? SessionFile => HarnessSessionFile.Read(configDir);
    private bool Marker => File.Exists(HarnessSessionFile.MarkerIn(configDir));

    private static HttpClient Client(HarnessSession s)
    {
        var c = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{s.Port}") };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.Token);
        return c;
    }

    private string AddMod()
    {
        string root = temp.Sub("mods", "Outfit");
        File.WriteAllText(Path.Combine(root, "meta.json"), "{}");
        for (int i = 0; i < 50; i++) File.WriteAllText(Path.Combine(root, $"f{i}.mdl"), new string('x', 100));
        game.ModFolders["Outfit"] = "Outfit";
        return root;
    }

    private static async Task<string> StartRefit(HarnessSession s)
    {
        using var c = Client(s);
        var r = await c.PostAsync("/actions/refit", new StringContent("""{"mod":"Outfit","bodies":["rue"]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(202, (int)r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("job").GetString()!;
    }

    private static async Task<JobView> WaitEnded(HarnessHost host, string id)
    {
        for (int i = 0; i < 500; i++)
        {
            if (host.Jobs?.Find(id)?.View() is { EndedAt: not null } v) return v;
            await Task.Delay(20);
        }
        throw new TimeoutException(id);
    }

    [Fact]
    public async Task On_StartsListening_WritesSessionFileAndMarker()
    {
        // [AC-qa-harness-2]
        using var host = Host();
        string said = host.Command("on");
        Assert.True(host.Listening);
        Assert.Contains($"127.0.0.1:{host.Port}", said);
        var s = SessionFile!;
        Assert.Equal(host.Port, s.Port);
        Assert.Equal(Environment.ProcessId, s.Pid);
        Assert.Equal(1411, s.Build);
        Assert.Equal(host.Session, s.Session);
        Assert.True(s.Token.Length >= 43);
        Assert.True(Marker);
        Assert.NotNull(observer);
        using var c = Client(s);
        Assert.Equal(200, (int)(await c.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public void Off_Stops_DeletesSessionFileAndMarker()
    {
        // [AC-qa-harness-2]
        using var host = Host();
        host.Command("on");
        host.Command("off");
        Assert.False(host.Listening);
        Assert.Null(SessionFile);
        Assert.False(Marker);
        Assert.Null(observer);
    }

    [Fact]
    public void Autostart_WithMarker_Starts_WithoutMarker_DoesNot()
    {
        // [AC-qa-harness-2] Left on, it starts again at the next load and says so.
        using (var host = Host()) Assert.Null(host.TryAutostart());
        File.WriteAllText(HarnessSessionFile.MarkerIn(configDir), "on");
        using var again = Host();
        Assert.Contains("was left on", again.TryAutostart());
        Assert.True(again.Listening);
    }

    [Fact]
    public void Default_NotListening()
    {
        // [AC-qa-harness-2] Constructed is not started: no file, no port.
        using var host = Host();
        Assert.False(host.Listening);
        Assert.Null(SessionFile);
        Assert.Equal(0, host.Port);
    }

    [Fact]
    public void NotDev_RefusesOn_AndAutostart()
    {
        // [EDGE-qa-harness-4]
        File.WriteAllText(HarnessSessionFile.MarkerIn(configDir), "on");
        using var host = Host(isDev: false);
        Assert.Null(host.TryAutostart());
        Assert.Contains("dev-loaded", host.Command("on"));
        Assert.False(host.Listening);
        Assert.Null(SessionFile);
    }

    [Fact]
    public void IsTypedOnly_HarnessLines_AnyCase()
    {
        // [EDGE-qa-harness-5] The dev-command file can never turn it on.
        Assert.True(HarnessHost.IsTypedOnly("harness on"));
        Assert.True(HarnessHost.IsTypedOnly("  HARNESS off"));
        Assert.True(HarnessHost.IsTypedOnly("Harness"));
        Assert.False(HarnessHost.IsTypedOnly("refresh"));
        Assert.False(HarnessHost.IsTypedOnly("models harness"));
    }

    [Fact]
    public void OnTwice_Idempotent_SamePortAndToken()
    {
        // [EDGE-qa-harness-6]
        using var host = Host();
        host.Command("on");
        var first = SessionFile!;
        Assert.Contains("already", host.Command("on"));
        var second = SessionFile!;
        Assert.Equal(first.Port, second.Port);
        Assert.Equal(first.Token, second.Token);
    }

    [Fact]
    public void StaleSessionFile_Overwritten()
    {
        // [EDGE-qa-harness-7] A crash left a file: the next start writes its own.
        HarnessSessionFile.Write(configDir, new HarnessSession(1, "old", 1, 1, "x", "old-session", DateTime.UtcNow));
        using var host = Host();
        host.Command("on");
        Assert.Equal(host.Session, SessionFile!.Session);
        Assert.NotEqual("old", SessionFile!.Token);
    }

    [Fact]
    public void Delete_OnlyWhenSessionMatches()
    {
        // [EDGE-qa-harness-7] A stopping instance never deletes another session's file.
        using var host = Host();
        host.Command("on");
        HarnessSessionFile.Write(configDir, new HarnessSession(2, "theirs", 1, 1, "x", "another-session", DateTime.UtcNow));
        host.Command("off");
        Assert.Equal("another-session", SessionFile!.Session);
    }

    [Fact]
    public async Task Restart_NewSession_LogCursorResets()
    {
        // [EDGE-qa-harness-8] A reload with the marker: a new port, token and session; the log numbers from 1 again.
        string firstSession, firstToken;
        using (var host = Host())
        {
            host.Command("on");
            observer!("a line");
            observer!("another");
            firstSession = host.Session!;
            firstToken = SessionFile!.Token;
        }
        using var again = Host();
        again.TryAutostart();
        Assert.NotEqual(firstSession, again.Session);
        Assert.NotEqual(firstToken, SessionFile!.Token);
        observer!("first of the new session");
        using var c = Client(SessionFile!);
        var page = JsonDocument.Parse(await c.GetStringAsync("/log")).RootElement;
        Assert.Equal(1, page.GetProperty("lines")[0].GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task OffDuringBackup_PartialDeleted_JobCancelled()
    {
        // [EDGE-qa-harness-9] Off while the backup copies: the copy stops, its partial folder goes, the job is cancelled.
        AddMod();
        using var host = Host();
        host.Command("on");
        var s = SessionFile!;
        host.Jobs!.Backups.BetweenCheckAndClaim = _ => host.Command("off");
        string id = await StartRefit(s);
        var jobs = host.Jobs!;
        JobView? v = null;
        for (int i = 0; i < 500 && (v = jobs.Find(id)?.View()) is not { EndedAt: not null }; i++) await Task.Delay(20);
        Assert.Equal("cancelled", v!.State);
        Assert.Empty(jobs.Backups.List("Outfit"));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(jobs.Backups.Root, "Outfit")));
        Assert.Null(SessionFile);
    }

    [Fact]
    public async Task OffMidRun_AfterChangeRuns_FileDeleted()
    {
        // [EDGE-qa-harness-9] Off mid-run: the run is cancelled, and Penumbra is still told.
        AddMod();
        using var host = Host();
        host.Command("on");
        var s = SessionFile!;
        game.RunBody = (_, _, ct) => { host.Command("off"); ct.ThrowIfCancellationRequested(); return 0; };
        var jobs = host.Jobs!;
        string id = await StartRefit(s);
        JobView? v = null;
        for (int i = 0; i < 500 && (v = jobs.Find(id)?.View()) is not { EndedAt: not null }; i++) await Task.Delay(20);
        Assert.Equal("cancelled", v!.State);
        Assert.Equal(1, game.AfterChanges);
        Assert.Null(SessionFile);
    }

    [Fact]
    public async Task Dispose_OffFrameworkThread_DoesNotBlock_AfterChangeSkipped()
    {
        // [EDGE-qa-harness-10] Unload with the framework stalled behind an after-change: Dispose returns at once, the
        // after-change is skipped when it finally comes up, and the marker stays.
        AddMod();
        var host = Host();
        host.Command("on");
        var s = SessionFile!;
        var reachedRun = new ManualResetEventSlim();
        game.RunBody = (_, _, _) => { dispatcher.Stall(); reachedRun.Set(); return 1; };
        var jobs = host.Jobs!;
        string id = await StartRefit(s);
        Assert.True(reachedRun.Wait(TimeSpan.FromSeconds(10)));
        await Task.Delay(100);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        host.Dispose();
        Assert.True(clock.ElapsedMilliseconds < 1000, $"Dispose took {clock.ElapsedMilliseconds} ms");
        dispatcher.Resume();
        JobView? v = null;
        for (int i = 0; i < 500 && (v = jobs.Find(id)?.View()) is not { EndedAt: not null }; i++) await Task.Delay(20);
        Assert.Equal("after-change-skipped", v!.Error!.Code);
        Assert.Equal(0, game.AfterChanges);
        Assert.True(Marker);
        Assert.Null(game.NameSink);
    }

    [Fact]
    public async Task Token_NeverLogged()
    {
        // [EDGE-qa-harness-11]
        var lines = new ConcurrentQueue<string>();
        var recording = new Proteus.Services.CapturingPluginLog(Substitute.For<IPluginLog>()) { LineObserver = lines.Enqueue };
        using var host = Host(withLog: recording);
        host.Command("on");
        var s = SessionFile!;
        using var c = Client(s);
        await c.GetAsync("/health");
        host.Command("off");
        Assert.NotEmpty(lines);
        Assert.DoesNotContain(lines, l => l.Contains(s.Token));
    }

    [Fact]
    public void RedrawEvents_CountedWithTime()
    {
        // [AC-qa-harness-5] The adapter's counter: every local-player redraw counted, with its time.
        var counter = new RedrawCounter();
        Assert.Equal(0, counter.Count);
        Assert.Null(counter.LastUtc);
        counter.Note();
        counter.Note();
        Assert.Equal(2, counter.Count);
        Assert.True(DateTime.UtcNow - counter.LastUtc!.Value < TimeSpan.FromSeconds(5));
    }
}
