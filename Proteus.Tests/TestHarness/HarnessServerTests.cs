using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Proteus.Services;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>End to end: a real loopback server, HttpClient, a fake game.</summary>
[Collection(HarnessTimingCollection.Name)]
public class HarnessServerTests : IDisposable
{
    private readonly HarnessFixture f = new();
    public void Dispose() => f.Dispose();

    [Fact]
    public async Task ValidToken_200_MissingOrWrong_401()
    {
        // [AC-qa-harness-3]
        Assert.Equal(200, (await f.Get("/health")).Status);
        using var wrong = f.NewClient("not-the-token");
        Assert.Equal(401, (int)(await wrong.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public void BindsIpv4LoopbackOnly()
    {
        // [AC-qa-harness-3] [EDGE-qa-harness-14]
        Assert.Equal(IPAddress.Loopback, f.Server.LocalEndpoint.Address);
    }

    private async Task<HttpResponseMessage> Send(Action<HttpRequestMessage> shape, string path = "/health")
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{f.Server.Port}") };
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        shape(request);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task TokenInQuery_401()
    {
        // [EDGE-qa-harness-11] Only the Authorization header carries the token.
        var r = await Send(_ => { }, "/health?token=" + Uri.EscapeDataString(f.Token));
        Assert.Equal(401, (int)r.StatusCode);
    }

    [Fact]
    public async Task BasicAuth_401()
    {
        // [EDGE-qa-harness-11]
        var r = await Send(m => m.Headers.Authorization = new AuthenticationHeaderValue("Basic", f.Token));
        Assert.Equal(401, (int)r.StatusCode);
    }

    [Fact]
    public async Task MissingAuth_401()
    {
        // [EDGE-qa-harness-11]
        Assert.Equal(401, (int)(await Send(_ => { })).StatusCode);
    }

    [Fact]
    public async Task WrongToken_401()
    {
        // [EDGE-qa-harness-11]
        var r = await Send(m => m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", f.Token + "x"));
        Assert.Equal(401, (int)r.StatusCode);
        var body = await HarnessFixture.Parse(r);
        Assert.DoesNotContain(f.Token, body.ToString());
    }

    [Fact]
    public async Task OriginHeader_403_NoCorsHeaders()
    {
        // [EDGE-qa-harness-12] A browser's fetch: refused, and nothing invites it back.
        var r = await Send(m =>
        {
            m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", f.Token);
            m.Headers.Add("Origin", "http://evil.example");
        });
        Assert.Equal(403, (int)r.StatusCode);
        Assert.DoesNotContain(r.Headers, h => h.Key.StartsWith("Access-Control", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("forbidden-origin", (await HarnessFixture.Parse(r)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ForeignHost_403()
    {
        // [EDGE-qa-harness-13]
        var r = await Send(m =>
        {
            m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", f.Token);
            m.Headers.Host = "evil.example";
        });
        Assert.Equal(403, (int)r.StatusCode);
    }

    [Fact]
    public async Task StalledClient_Closed_ServerStillServes()
    {
        // [EDGE-qa-harness-15] A client that sends half a request and stalls is closed; the next is served.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, f.Server.Port);
        var s = tcp.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes("GET /health HTTP/1.1\r\nHost: 127.0.0.1"));
        var buffer = new byte[64];
        var read = await s.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, read);   // closed without an answer
        Assert.Equal(200, (await f.Get("/health")).Status);
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-a-3")]
    [Trait("Plan", "AC-issue-sweep-a-4")]
    [Trait("Plan", "EDGE-issue-sweep-a-12")]
    public async Task FifthConcurrentConnection_503()
    {
        // [EDGE-qa-harness-16] Four open connections hold the slots; a fifth is told too-many-connections; the others
        // complete. A long read timeout keeps the four stalled slots held however slow the machine; the wait is on the
        // server's own count, not a sleep; and the fifth reads its refusal BEFORE sending anything, so no request bytes
        // sit unread when the server closes (which would reset the socket and lose the answer).
        using var g = new HarnessFixture(readTimeout: TimeSpan.FromSeconds(30));
        var open = new List<TcpClient>();
        try
        {
            for (int i = 0; i < HarnessServer.MaxConnections; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, g.Server.Port);
                await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /health HTTP/1.1\r\n"));
                open.Add(c);
            }
            await WaitUntil(() => g.Server.OpenConnections == HarnessServer.MaxConnections);

            using var fifth = new TcpClient();
            await fifth.ConnectAsync(IPAddress.Loopback, g.Server.Port);
            var reply = await ReadToEnd(fifth.GetStream()).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.StartsWith("HTTP/1.1 503", reply);
            Assert.Contains("\"code\":\"too-many-connections\"", reply);
        }
        finally { foreach (var c in open) c.Dispose(); }
        await WaitUntil(() => g.Server.OpenConnections == 0);
        Assert.Equal(200, (await g.Get("/health")).Status);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out waiting");
            await Task.Delay(10);
        }
    }

    private static async Task<string> ReadToEnd(NetworkStream s)
    {
        using var all = new MemoryStream();
        var buffer = new byte[4096];
        int n;
        while ((n = await s.ReadAsync(buffer)) > 0) all.Write(buffer, 0, n);
        return Encoding.ASCII.GetString(all.ToArray());
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-a-3")]
    public void Adversarial_qa_documents_the_connection_cap_and_duplicate_keys()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var doc = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "agent-rules", "adversarial-qa.md"));
        Assert.Contains("too-many-connections", doc);
        Assert.Contains("repeats a field", doc);
        Assert.Contains("409 `busy`", doc);
    }

    [Fact]
    public async Task BodyTooLarge_413_ReachesTheClient()
    {
        // [EDGE-qa-harness-15] The answer to an oversized body arrives intact, not as a connection reset.
        for (int i = 0; i < 5; i++)
        {
            var r = await f.Client.PostAsync("/actions/refit", HarnessFixture.Json("{\"mod\":\"" + new string('a', 70_000) + "\"}"));
            Assert.Equal(413, (int)r.StatusCode);
        }
        Assert.Equal(200, (await f.Get("/health")).Status);
    }

    [Fact]
    public async Task UnknownPath_404()
    {
        // [EDGE-qa-harness-17]
        Assert.Equal(404, (await f.Get("/nope")).Status);
    }

    [Fact]
    public async Task WrongMethod_405_WithAllow()
    {
        // [EDGE-qa-harness-17]
        var r = await f.Client.PostAsync("/health", HarnessFixture.Json("{}"));
        Assert.Equal(405, (int)r.StatusCode);
        Assert.Equal("GET", r.Content.Headers.Allow.Single());
    }

    [Fact]
    public async Task StalledFramework_503_ThenRecovers()
    {
        // [AC-qa-harness-4] [EDGE-qa-harness-18]
        f.Dispatcher.Stall();
        var (status, body) = await f.Get("/health");
        Assert.Equal(503, status);
        Assert.Equal("framework-timeout", body.GetProperty("error").GetProperty("code").GetString());
        f.Dispatcher.Resume();
        Assert.Equal(200, (await f.Get("/health")).Status);
    }

    [Fact]
    public async Task RefitDispatchAbandoned_NoJobEver_SlotReleased()
    {
        // [EDGE-qa-harness-19] The request timed out before the framework started it: no job, ever, and the slot is free.
        f.AddMod();
        f.Dispatcher.Stall();
        var (status, _) = await f.Post("/actions/refit", """{"mod":"Outfit","bodies":["rue"]}""");
        Assert.Equal(503, status);
        f.Dispatcher.Resume();
        await Task.Delay(200);
        Assert.Null(f.Jobs.Current);
        Assert.Empty(f.Backups.List("Outfit"));
        Assert.True(f.Jobs.TryReserve());
        f.Jobs.Release();
    }

    [Fact]
    public async Task HandlerThrows_500_ServerSurvives()
    {
        // [EDGE-qa-harness-22]
        var real = f.Game.Capture;
        f.Game.ResourceMap = null;
        f.Game.Loaded = null!;   // State's join throws on it
        var (status, body) = await f.Get("/state");
        Assert.Equal(500, status);
        Assert.Equal("internal", body.GetProperty("error").GetProperty("code").GetString());
        f.Game.Loaded = [];
        Assert.Equal(200, (await f.Get("/state")).Status);
    }

    [Fact]
    public async Task Health_FieldsAndValues()
    {
        // [AC-qa-harness-5]
        var (status, b) = await f.Get("/health");
        Assert.Equal(200, status);
        Assert.True(b.GetProperty("ok").GetBoolean());
        Assert.Equal(1, b.GetProperty("harness").GetInt32());
        Assert.Equal("session-a", b.GetProperty("session").GetString());
        Assert.Equal(1411, b.GetProperty("build").GetInt32());
        Assert.Equal("11-01 12:00:00", b.GetProperty("buildStamp").GetString());
        Assert.True(b.GetProperty("penumbra").GetBoolean());
        Assert.True(b.GetProperty("loggedIn").GetBoolean());
        Assert.True(b.GetProperty("pluginEnabled").GetBoolean());
    }

    [Fact]
    public async Task State_ShapeAndValues()
    {
        // [AC-qa-harness-5] [EDGE-qa-harness-36] Every block, numbers only, no names; busy follows a running runner even
        // when the view says idle.
        f.Game.Loaded = [(1, $@"|p|{f.ModsRoot}\Outfit\chara\top.mdl")];
        f.Game.ResourceMap = new(StringComparer.OrdinalIgnoreCase)
        {
            [$@"{f.ModsRoot}\Outfit\chara\top.mdl"] = new(["chara/equipment/e0001/model/c0201e0001_top.mdl"]),
        };
        f.Game.Compositor = new CompositorService.ActivitySnapshot(1, 9, 0, 0, "", false);
        f.Names.Add("Secret Collection", NameKind.Collection);
        f.Game.View = new AutoRefitWatcher.View("refitting for Secret Collection", false, Busy: false);
        f.Game.Activity = new AutoRefitWatcher.ActivitySnapshot(true, 2, false, false);
        f.Game.Redraw();
        var (status, s) = await f.Get("/state");
        Assert.Equal(200, status);
        Assert.Equal(1, s.GetProperty("character").GetProperty("race").GetInt32());
        Assert.Equal("c0201", s.GetProperty("character").GetProperty("drawnRaceCode").GetString());
        Assert.Equal("11111111-2222-3333-4444-555555555555", s.GetProperty("collection").GetProperty("id").GetString());
        var model = s.GetProperty("models")[0];
        Assert.Equal(1, model.GetProperty("slot").GetInt32());
        Assert.Equal("Outfit", model.GetProperty("modDir").GetString());
        Assert.Equal("chara/equipment/e0001/model/c0201e0001_top.mdl", model.GetProperty("gamePath").GetString());
        Assert.Equal("compositing", s.GetProperty("compositor").GetProperty("phase").GetString());
        Assert.Equal(1, s.GetProperty("redraws").GetProperty("count").GetInt64());
        Assert.True(s.GetProperty("autoRefit").GetProperty("busy").GetBoolean());
        Assert.Equal(2, s.GetProperty("autoRefit").GetProperty("pending").GetProperty("queued").GetInt32());
        string text = s.ToString();
        Assert.DoesNotContain("Secret Collection", text);
        Assert.DoesNotContain("Hidden Person", text);
        Assert.Equal("refitting for <COLLECTION>", s.GetProperty("autoRefit").GetProperty("message").GetString());
        Assert.DoesNotContain("\"name\"", text);
    }

    [Fact]
    public async Task Log_SinceCursor_NextAndSession()
    {
        // [AC-qa-harness-6] Lines after the cursor, the next cursor, the session; names the game knows are scrubbed.
        f.Ring.Add("one");
        f.Ring.Add("collection Secret Collection for Hidden Person");
        f.Ring.Add("three");
        var (status, b) = await f.Get("/log?since=1");
        Assert.Equal(200, status);
        Assert.Equal("session-a", b.GetProperty("session").GetString());
        Assert.Equal(3, b.GetProperty("next").GetInt64());
        var lines = b.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()).ToList();
        Assert.Equal(["collection <COLLECTION> for <CHARACTER>", "three"], lines);
    }

    [Fact]
    public async Task Log_BadSince_400()
    {
        // [EDGE-qa-harness-26]
        Assert.Equal(400, (await f.Get("/log?since=-1")).Status);
        Assert.Equal(400, (await f.Get("/log?since=abc")).Status);
        Assert.Equal(400, (await f.Get("/log?max=0")).Status);
    }

    [Fact]
    public async Task Log_MaxCapped()
    {
        // [EDGE-qa-harness-26]
        for (int i = 0; i < LogRing.MaxMax + 10; i++) f.Ring.Add("x");
        var (_, b) = await f.Get("/log?since=0&max=999999");
        Assert.Equal(LogRing.MaxMax, b.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task Screenshot_ReturnsRgbPng()
    {
        // [AC-qa-harness-7] PNG bytes; ui=false reaches the capture as "before ImGui".
        var r = await f.Client.GetAsync("/screenshot?ui=false");
        Assert.Equal(200, (int)r.StatusCode);
        Assert.Equal("image/png", r.Content.Headers.ContentType!.MediaType);
        var bytes = await r.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, bytes[..4]);
        Assert.False(f.Game.LastCaptureUi);
        await f.Client.GetAsync("/screenshot");
        Assert.True(f.Game.LastCaptureUi);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-a-11")]
    public async Task Screenshot_Concurrent_429()
    {
        // [EDGE-qa-harness-28] Its own fixture with a long capture timeout, and the second request sent only once the
        // first is really inside the capture: under a loaded suite a fixed delay could send it first, or let the
        // first time out (seen once in the quick suite).
        using var g = new HarnessFixture(HarnessFixture.FastTimings with { Capture = TimeSpan.FromSeconds(30) });
        var slow = new TaskCompletionSource<RawFrame>();
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        g.Game.Capture = (_, _, _) => { inside.TrySetResult(); return slow.Task; };
        var first = g.Client.GetAsync("/screenshot");
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var (busyStatus, busyBody) = await g.Get("/screenshot");
        Assert.Equal(429, busyStatus);
        Assert.Equal("busy", busyBody.GetProperty("error").GetProperty("code").GetString());   // unchanged by #67
        slow.SetResult(new RawFrame(1, 1, 4, FramePng.B8G8R8A8Unorm, new byte[4]));
        Assert.Equal(200, (int)(await first).StatusCode);
    }

    [Fact]
    public async Task Screenshot_Throws_503()
    {
        // [EDGE-qa-harness-28]
        f.Game.Capture = (_, _, _) => Task.FromException<RawFrame>(new InvalidOperationException("device lost"));
        var (status, b) = await f.Get("/screenshot");
        Assert.Equal(503, status);
        Assert.Equal("capture-failed", b.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Screenshot_BadUi_400()
    {
        // [EDGE-qa-harness-28]
        Assert.Equal(400, (await f.Get("/screenshot?ui=maybe")).Status);
    }

    [Fact]
    public async Task Screenshot_Hangs_503_NextNot429()
    {
        // [EDGE-qa-harness-29] A capture that never completes times out, and the next one is not refused as busy.
        f.Game.Capture = (_, _, _) => new TaskCompletionSource<RawFrame>().Task;
        var (status, b) = await f.Get("/screenshot");
        Assert.Equal(503, status);
        Assert.Equal("capture-timeout", b.GetProperty("error").GetProperty("code").GetString());
        f.Game.Capture = (_, _, _) => Task.FromResult(new RawFrame(1, 1, 4, FramePng.B8G8R8A8Unorm, new byte[4]));
        Assert.Equal(200, (int)(await f.Client.GetAsync("/screenshot")).StatusCode);
    }

    [Fact]
    public async Task Screenshot_BadFrame_503()
    {
        // [EDGE-qa-harness-30]
        f.Game.Capture = (_, _, _) => Task.FromResult(new RawFrame(4, 4, 16, FramePng.B8G8R8A8Unorm, new byte[10]));
        var (status, b) = await f.Get("/screenshot");
        Assert.Equal(503, status);
        Assert.Equal("capture-bad-frame", b.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task NotLoggedIn_StateNulls_HealthFalse_ScreenshotOk()
    {
        // [EDGE-qa-harness-23]
        f.Game.LoggedIn = false;
        Assert.False((await f.Get("/health")).Body.GetProperty("loggedIn").GetBoolean());
        var (_, s) = await f.Get("/state");
        Assert.Equal(JsonValueKind.Null, s.GetProperty("character").ValueKind);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("collection").ValueKind);
        Assert.Equal(0, s.GetProperty("models").GetArrayLength());
        Assert.Equal(200, (int)(await f.Client.GetAsync("/screenshot")).StatusCode);
    }

    [Fact]
    public async Task PenumbraUnavailable_RefitRestore503_StateWithoutResolved()
    {
        // [EDGE-qa-harness-24]
        string mod = f.AddMod();
        f.Backups.Create(mod, "Outfit", BackupKind.Refit, "session-a", f.Jobs.NextSeq(), null, CancellationToken.None);
        f.Game.PenumbraUp = false;
        f.Game.Loaded = [(0, @"E:\x.mdl")];
        Assert.Equal(503, (await f.Post("/actions/refit", """{"mod":"Outfit","bodies":["rue"]}""")).Status);
        Assert.Equal(503, (await f.Post("/actions/restore", """{"mod":"Outfit"}""")).Status);
        var (_, s) = await f.Get("/state");
        Assert.Equal(JsonValueKind.Null, s.GetProperty("models")[0].GetProperty("resolved").ValueKind);
        Assert.True(f.Jobs.TryReserve());
        f.Jobs.Release();
    }

    [Fact]
    public async Task Refit_202_JobReachesDone_WithReport()
    {
        // [AC-qa-harness-8] 202 with a job; polling shows it done with what it made, the backup, the reload and redraw.
        f.AddMod();
        var (status, b) = await f.Post("/actions/refit", """{"mod":"outfit","bodies":["rue"]}""");
        Assert.Equal(202, status);
        Assert.Equal("Outfit", b.GetProperty("modDir").GetString());
        var job = await f.WaitJob(b.GetProperty("job").GetString()!);
        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.Equal(1, job.GetProperty("result").GetProperty("made").GetInt32());
        Assert.Equal(JsonValueKind.Object, job.GetProperty("result").GetProperty("report").ValueKind);
        Assert.Equal(JsonValueKind.String, job.GetProperty("backup").GetProperty("id").ValueKind);
        Assert.True(job.GetProperty("reloaded").GetBoolean());
        Assert.True(job.GetProperty("redrawRequested").GetBoolean());
        Assert.True(job.GetProperty("redrawObserved").GetBoolean());
        Assert.False(job.GetProperty("autoRefitFollowed").GetBoolean());
    }

    [Fact]
    public async Task Refit_EmptyBodies_400()
    {
        // [EDGE-qa-harness-33]
        f.AddMod();
        Assert.Equal(400, (await f.Post("/actions/refit", """{"mod":"Outfit","bodies":[]}""")).Status);
    }

    [Fact]
    public async Task UnknownBody_ParseKeysMessage_400()
    {
        // [EDGE-qa-harness-33] The size set's own key table decides, and its message comes back.
        f.AddMod();
        var (status, b) = await f.Post("/actions/refit", """{"mod":"Outfit","bodies":["nobody"]}""");
        Assert.Equal(400, status);
        Assert.Contains("no body called nobody", b.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task AcrossOnWomensKey_400()
    {
        // [EDGE-qa-harness-33]
        f.AddMod();
        Assert.Equal(400, (await f.Post("/actions/refit", """{"mod":"Outfit","bodies":["rue@women"]}""")).Status);
    }

    [Fact]
    public async Task WrongType_400()
    {
        // [EDGE-qa-harness-33]
        f.AddMod();
        Assert.Equal(400, (await f.Post("/actions/refit", """{"mod":"Outfit","bodies":"rue"}""")).Status);
        Assert.Equal(400, (await f.Post("/actions/refit", """{"mod":"Outfit","bodies":["rue"],"remake":"yes"}""")).Status);
        Assert.Equal(400, (await f.Post("/actions/refit", """{"mod":5,"bodies":["rue"]}""")).Status);
    }

    [Fact]
    public async Task UnknownField_400()
    {
        // [EDGE-qa-harness-33]
        f.AddMod();
        Assert.Equal(400, (await f.Post("/actions/refit", """{"mod":"Outfit","bodies":["rue"],"yas":true}""")).Status);
    }

    [Fact]
    public async Task BadJson_400()
    {
        // [EDGE-qa-harness-33]
        Assert.Equal(400, (await f.Post("/actions/refit", "{not json")).Status);
        Assert.Equal(400, (await f.Post("/actions/refit", "[1]")).Status);
    }

    [Fact]
    public async Task InvalidUtf8OrLoneSurrogate_400BadJson()
    {
        // [EDGE-qa-harness-33] In-game QA: these parsed, then threw on read, and came back 500 internal.
        var bytes = new System.Collections.Generic.List<byte>(Encoding.ASCII.GetBytes("{\"mod\":\""));
        bytes.Add(0xFF);
        bytes.AddRange(Encoding.ASCII.GetBytes("\",\"bodies\":[\"rue\"]}"));
        var content = new ByteArrayContent(bytes.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var r = await f.Client.PostAsync("/actions/refit", content);
        Assert.Equal(400, (int)r.StatusCode);
        Assert.Equal("bad-json", (await HarnessFixture.Parse(r)).GetProperty("error").GetProperty("code").GetString());
        var (status, b) = await f.Post("/actions/restore", "{\"mod\":\"\\ud800\"}");
        Assert.Equal(400, status);
        Assert.Equal("bad-json", b.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task NoContentType_415()
    {
        // [EDGE-qa-harness-33]
        var r = await f.Client.PostAsync("/actions/refit", new StringContent("""{"mod":"Outfit","bodies":["rue"]}""", Encoding.UTF8, "text/plain"));
        Assert.Equal(415, (int)r.StatusCode);
    }

    [Fact]
    public async Task Refit_WhileJobRunning_409()
    {
        // [EDGE-qa-harness-35]
        f.AddMod();
        var gate = new ManualResetEventSlim();
        f.Game.RunBody = (_, _, _) => { gate.Wait(TimeSpan.FromSeconds(10)); return 0; };
        Assert.Equal(202, (await f.Post("/actions/refit", """{"mod":"Outfit","bodies":["rue"]}""")).Status);
        var (status, b) = await f.Post("/actions/refit", """{"mod":"Outfit","bodies":["rue"]}""");
        Assert.Equal(409, status);
        Assert.Equal("busy", b.GetProperty("error").GetProperty("code").GetString());
        gate.Set();
    }

    [Fact]
    public async Task Restore_WhileJobRunning_409()
    {
        // [EDGE-qa-harness-35]
        f.AddMod();
        var gate = new ManualResetEventSlim();
        f.Game.RunBody = (_, _, _) => { gate.Wait(TimeSpan.FromSeconds(10)); return 0; };
        Assert.Equal(202, (await f.Post("/actions/refit", """{"mod":"Outfit","bodies":["rue"]}""")).Status);
        Assert.Equal(409, (await f.Post("/actions/restore", """{"mod":"Outfit"}""")).Status);
        gate.Set();
    }

    [Fact]
    public async Task WalkDueWhileAutoRefitDisabled_StateBusyFalse_RefitAndRestoreAccepted()
    {
        // [EDGE-qa-harness-37] The watcher reports a disabled walk as not due (WalkCounts); nothing else pending.
        f.AddMod();
        f.Game.Activity = new AutoRefitWatcher.ActivitySnapshot(false, 0, AutoRefitWatcher.WalkCounts(999, enabled: false, disposed: false), false);
        Assert.False((await f.Get("/state")).Body.GetProperty("autoRefit").GetProperty("busy").GetBoolean());
        var (status, b) = await f.Post("/actions/refit", """{"mod":"Outfit","bodies":["rue"]}""");
        Assert.Equal(202, status);
        await f.WaitJob(b.GetProperty("job").GetString()!);
        Assert.Equal(202, (await f.Post("/actions/restore", """{"mod":"Outfit"}""")).Status);
    }

    [Fact]
    public async Task Restore_202_JobDone_RestoredFields()
    {
        // [AC-qa-harness-9]
        string mod = f.AddMod();
        var before = FolderState.Of(mod);
        var (_, refit) = await f.Post("/actions/refit", """{"mod":"Outfit","bodies":["rue"]}""");
        string refitId = refit.GetProperty("job").GetString()!;
        await f.WaitJob(refitId);
        var (status, b) = await f.Post("/actions/restore", $$"""{"job":"{{refitId}}"}""");
        Assert.Equal(202, status);
        var job = await f.WaitJob(b.GetProperty("job").GetString()!);
        Assert.Equal("done", job.GetProperty("state").GetString());
        var restored = job.GetProperty("restored");
        Assert.False(restored.GetProperty("changedSinceJob").GetBoolean());
        Assert.Equal(1, restored.GetProperty("deleted").GetInt32());
        Assert.Equal(JsonValueKind.String, restored.GetProperty("preRestore").GetProperty("id").ValueKind);
        Assert.True(job.GetProperty("reloaded").GetBoolean());
        FolderState.AssertSame(before, mod);
    }

    [Fact]
    public async Task Restore_UnknownJob_404()
    {
        // [EDGE-qa-harness-45]
        Assert.Equal(404, (await f.Post("/actions/restore", """{"job":"abcdef123456"}""")).Status);
    }

    [Fact]
    public async Task Restore_ModWithoutBackup_404()
    {
        // [EDGE-qa-harness-45]
        f.AddMod();
        Assert.Equal(404, (await f.Post("/actions/restore", """{"mod":"Outfit"}""")).Status);
    }

    [Fact]
    public async Task Restore_EarlierSessionWithoutId_404()
    {
        // [EDGE-qa-harness-45] A backup from another session is only restored by its explicit id.
        string mod = f.AddMod();
        f.Backups.Create(mod, "Outfit", BackupKind.Refit, "older-session", 1, null, CancellationToken.None);
        Assert.Equal(404, (await f.Post("/actions/restore", """{"mod":"Outfit"}""")).Status);
    }

    [Fact]
    public async Task Restore_NoMatch_404()
    {
        // [EDGE-qa-harness-45]
        f.AddMod();
        Assert.Equal(404, (await f.Post("/actions/restore", """{"mod":"Outfit","backup":"20260101-000000-abcd"}""")).Status);
    }

    [Fact]
    public async Task Restore_ExplicitIncompleteBackup_409BackupIncomplete_NoSnapshotNoWrite()
    {
        // [EDGE-qa-harness-46]
        string mod = f.AddMod();
        var before = FolderState.Of(mod);
        var b = f.Backups.Create(mod, "Outfit", BackupKind.Refit, "older", 1, null, CancellationToken.None);
        File.Delete(Path.Combine(b.Path, ModBackup.MetaFile));
        var (status, body) = await f.Post("/actions/restore", $$"""{"mod":"Outfit","backup":"{{b.Id}}"}""");
        Assert.Equal(409, status);
        Assert.Equal("backup-incomplete", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(f.Backups.List("Outfit"));
        FolderState.AssertSame(before, mod);
    }
}
