using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>
/// The Studio routes over the real server with a scripted Studio: forwarding, refusals, gates, the session, and the
/// Studio step of put-back.
/// </summary>
[Collection(HarnessTimingCollection.Name)]
public class HarnessStudioRouteTests : IDisposable
{
    private readonly HarnessFixture f = new();
    private readonly FakeStudio studio;

    public HarnessStudioRouteTests()
    {
        studio = f.Game.UseStudio();
        f.AddMod();
    }

    public void Dispose() => f.Dispose();

    private Task<(int Status, System.Text.Json.JsonElement Body)> Open(string json = """{"mod":"outfit","model":"chara/model/top.mdl","show":true}""")
        => f.Post("/actions/studio-open", json);

    private static string Code(System.Text.Json.JsonElement body) => body.GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    [Trait("Plan", "AC-move-all-parts-11")]
    public async Task Studio_Get_ShapesTheState()
    {
        await Open();
        var (status, body) = await f.Get("/studio");
        Assert.Equal(200, status);
        foreach (var field in new[] { "open", "tool", "mode", "view", "settings", "effective", "parts", "chosen", "all", "help", "edit", "status", "session" })
            Assert.True(body.TryGetProperty(field, out _), field);
        Assert.False(body.TryGetProperty("drawer", out _));
        Assert.False(body.GetProperty("settings").TryGetProperty("falloffMm", out _));
        Assert.True(body.GetProperty("parts")[0].GetProperty("moves").GetBoolean());
        var session = body.GetProperty("session");
        Assert.Equal("Outfit", session.GetProperty("modDir").GetString());
        Assert.Equal(1, session.GetProperty("editVersion").GetInt32());
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-12")]
    public async Task Open_OpensTheModel_AndShows()
    {
        var (status, body) = await Open();
        Assert.Equal(200, status);
        var (mod, model, show) = Assert.Single(studio.Opens);
        Assert.Equal(("Outfit", "chara/model/top.mdl", true), (mod, model, show));   // folder matched case-insensitively
        Assert.True(f.Changes.HasStudioSession);
        Assert.Equal("Outfit", body.GetProperty("session").GetProperty("modDir").GetString());
    }

    [Theory]
    [Trait("Plan", "AC-move-all-parts-12")]
    [Trait("Plan", "EDGE-move-all-parts-18")]
    [Trait("Plan", "EDGE-move-all-parts-19")]
    [Trait("Plan", "EDGE-move-all-parts-26")]
    [InlineData("managed", 400, "managed-mod")]
    [InlineData("unknown", 404, "mod-not-found")]
    [InlineData("has-edits", 409, "studio-has-edits")]
    [InlineData("busy-studio", 409, "studio-busy")]
    [InlineData("legacy", 409, "legacy-mod")]
    [InlineData("no-model", 404, "model-not-in-mod")]
    [InlineData("auto-refit", 409, "auto-refit-busy")]
    public async Task Open_Refusals(string why, int expected, string code)
    {
        string mod = "Outfit";
        switch (why)
        {
            case "managed": mod = f.Game.ManagedModDir; f.Game.ModFolders[mod] = mod; break;
            case "unknown": mod = "Nope"; break;
            case "has-edits": studio.OpenRefusal = new StudioRefusal(409, "studio-has-edits", "edits"); break;
            case "busy-studio": studio.OpenRefusal = new StudioRefusal(409, "studio-busy", "busy"); break;
            case "legacy": studio.OpenRefusal = new StudioRefusal(409, "legacy-mod", "legacy"); break;
            case "no-model": studio.OpenRefusal = new StudioRefusal(404, "model-not-in-mod", "models: top.mdl (chara/x.mdl)"); break;
            case "auto-refit": f.Game.Activity = new Proteus.Services.AutoRefitWatcher.ActivitySnapshot(false, 1, false, false); break;
        }
        var (status, body) = await Open($$"""{"mod":"{{mod}}","model":"top.mdl"}""");
        Assert.Equal(expected, status);
        Assert.Equal(code, Code(body));
        Assert.False(f.Changes.HasStudioSession);
        if (why is "managed" or "unknown" or "auto-refit") Assert.Empty(studio.Opens);
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-30")]
    public async Task Stopping_503()
    {
        await Open();
        f.Changes.BeginStop();
        foreach (var (path, json) in new[] { ("/actions/studio-open", """{"mod":"Outfit","model":"top.mdl"}"""),
                                             ("/actions/studio-set", """{"mode":"all"}"""),
                                             ("/actions/studio-drag", """{"offsetMm":[1,0,0]}""") })
        {
            var (status, body) = await f.Post(path, json);
            Assert.Equal(503, status);
            Assert.Equal("harness-stopping", Code(body));
        }
        Assert.Equal(200, (await f.Post("/actions/put-back", "{}")).Status);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-13")]
    [Trait("Plan", "EDGE-move-all-parts-25")]
    public async Task Set_ForwardsAndAnswersState()
    {
        await Open();
        studio.LockResults = [("1.1", "locked"), ("1.4", "refused")];
        studio.Chosen = false;
        var (status, body) = await f.Post("/actions/studio-set", """{"tool":"rotate","mode":"all","lock":["1.1","1.4"],"part":"1.2"}""");
        Assert.Equal(200, status);
        var set = Assert.Single(studio.Sets);
        Assert.Equal(("rotate", "all", "1.2"), (set.Tool, set.Mode, set.Part));
        Assert.Equal(["locked", "refused"], body.GetProperty("locks").EnumerateArray().Select(l => l.GetProperty("result").GetString()));
        Assert.False(body.GetProperty("chosen").GetBoolean());
        Assert.Equal("rotate", body.GetProperty("state").GetProperty("tool").GetString());
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-14")]
    public async Task Drag_ForwardsAndAnswers()
    {
        await Open();
        foreach (var json in new[] { """{"offsetMm":[5,0,0]}""", """{"axis":"y","degrees":20}""", """{"factor":1.2}""", """{"undo":true}""" })
        {
            var (status, body) = await f.Post("/actions/studio-drag", json);
            Assert.Equal(200, status);
            foreach (var field in new[] { "kind", "moved", "nodes", "worstMm", "pivot", "saved", "status", "state" })
                Assert.True(body.TryGetProperty(field, out _), field);
        }
        Assert.Equal(["move", "rotate", "scale", "undo"], studio.Drags.Select(d => d.Request.Kind));
        // The session hands the files it saved so far to the next drag, and records their hashes.
        Assert.Empty(studio.Drags[0].HarnessFiles);
        Assert.Equal(["chara/model/top.mdl"], studio.Drags[1].HarnessFiles);
        Assert.Equal("AA", f.Changes.StudioSessionNow!.Files["chara/model/top.mdl"]);
        Assert.Equal(studio.EditVersion, f.Changes.StudioSessionNow.EditVersion);
    }

    [Theory]
    [Trait("Plan", "EDGE-move-all-parts-22")]
    [Trait("Plan", "EDGE-move-all-parts-24")]
    [Trait("Plan", "EDGE-move-all-parts-27")]
    [InlineData("nothing-to-move")]
    [InlineData("wrong-tool")]
    [InlineData("nothing-to-undo")]
    public async Task Drag_Refusals(string code)
    {
        await Open();
        studio.Status = "Nothing ticked can move: it is all skin or locked.";
        studio.DragRefusal = new StudioRefusal(409, code, studio.Status);
        var (status, body) = await f.Post("/actions/studio-drag", """{"offsetMm":[1,0,0]}""");
        Assert.Equal(409, status);
        Assert.Equal(code, Code(body));
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-32")]
    public async Task Drag_SaveFailed()
    {
        await Open();
        studio.Saved = false;
        studio.StatusIsError = true;
        studio.Status = @"Could not write C:\Users\someone\mods\Outfit\top.mdl";
        var (status, body) = await f.Post("/actions/studio-drag", """{"offsetMm":[1,0,0]}""");
        Assert.Equal(200, status);
        Assert.False(body.GetProperty("saved").GetBoolean());
        Assert.True(body.GetProperty("status").GetProperty("isError").GetBoolean());
        Assert.DoesNotContain("someone", body.GetProperty("status").GetProperty("text").GetString());
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-20")]
    public async Task SetAndDrag_StudioChanged_OrNoSession()
    {
        var (s0, b0) = await f.Post("/actions/studio-set", """{"mode":"all"}""");
        Assert.Equal((409, "no-studio-session"), (s0, Code(b0)));
        await Open();
        studio.OpenFile = "other.mdl";   // the user opened another model
        foreach (var (path, json) in new[] { ("/actions/studio-set", """{"mode":"all"}"""), ("/actions/studio-drag", """{"offsetMm":[1,0,0]}""") })
        {
            var (status, body) = await f.Post(path, json);
            Assert.Equal((409, "studio-changed"), (status, Code(body)));
        }
        Assert.Empty(studio.Sets);
        Assert.Empty(studio.Drags);
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-26")]
    public async Task SetAndDrag_Gates()
    {
        await Open();
        f.Game.Activity = new Proteus.Services.AutoRefitWatcher.ActivitySnapshot(false, 1, false, false);
        Assert.Equal("auto-refit-busy", Code((await f.Post("/actions/studio-set", """{"mode":"all"}""")).Body));
        Assert.Equal("auto-refit-busy", Code((await f.Post("/actions/studio-drag", """{"offsetMm":[1,0,0]}""")).Body));
        Assert.Equal(200, (await f.Get("/studio")).Status);
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-23")]
    public async Task BadBodies_400_NothingCalled()
    {
        await Open();
        foreach (var (path, json) in new[] { ("/actions/studio-set", """{}"""), ("/actions/studio-set", """{"drawer":true}"""),
                                             ("/actions/studio-drag", """{"offsetMm":[1e9,0,0]}"""), ("/actions/studio-open", """{"mod":"Outfit"}""") })
            Assert.Equal(400, (await f.Post(path, json)).Status);
        Assert.Empty(studio.Sets);
        Assert.Empty(studio.Drags);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-16")]
    public async Task StudioRoutes_NeedTheToken()
    {
        using var anonymous = f.NewClient("wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/studio")).StatusCode);
        foreach (var path in new[] { "/actions/studio-open", "/actions/studio-set", "/actions/studio-drag" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(path, HarnessFixture.Json("{}"))).StatusCode);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-11")]
    public async Task StudioMembers_RunOnTheFrameworkThread()
    {
        await Open();
        await f.Get("/studio");
        await f.Post("/actions/studio-set", """{"mode":"all"}""");
        await f.Post("/actions/studio-drag", """{"offsetMm":[1,0,0]}""");
        await f.Post("/actions/put-back", "{}");
        var members = f.Game.Calls.Select(c => c.Member).ToHashSet();
        foreach (var m in new[] { "View", "Open", "IsOpen", "Set", "Drag", "EndSession" }) Assert.Contains(m, members);
        Assert.All(f.Game.Calls, c => Assert.Equal(f.Dispatcher.ThreadId, c.Thread));
    }

    // ── put back ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-move-all-parts-15")]
    public async Task PutBack_EndsTheStudioSession()
    {
        await Open();
        await f.Post("/actions/studio-drag", """{"offsetMm":[1,0,0]}""");
        var (status, body) = await f.Post("/actions/put-back", "{}");
        Assert.Equal(200, status);
        var entry = Assert.Single(body.GetProperty("studio").EnumerateArray());
        Assert.Equal(("reverted", "restored"), (entry.GetProperty("files").GetString(), entry.GetProperty("panel").GetString()));
        var ended = Assert.Single(studio.Ended);
        Assert.Equal("snapshot", ended.Snapshot);
        Assert.Equal("AA", ended.Files["chara/model/top.mdl"]);
        Assert.False(f.Changes.HasStudioSession);
        // After the hold and gear steps: the Studio is the last thing put back here.
        Assert.Equal("EndSession@framework", f.Game.Order.Last(o => o.StartsWith("EndSession", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-15")]
    [Trait("Plan", "EDGE-move-all-parts-21")]
    public async Task PutBack_TouchedModel_KeepsTheSession()
    {
        await Open();
        studio.EndAnswer = s => new StudioPutBack(s.ModDir, "changed", "changed", "hash-changed", true);
        var (_, body) = await f.Post("/actions/put-back", "{}");
        var entry = Assert.Single(body.GetProperty("studio").EnumerateArray());
        Assert.Equal("hash-changed", entry.GetProperty("code").GetString());
        Assert.True(f.Changes.HasStudioSession);   // still recorded, not an error
        Assert.Contains("Studio edits in Outfit", f.Changes.Leftovers());
        Assert.False(f.Changes.Empty);
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-28")]
    [Trait("Plan", "EDGE-move-all-parts-36")]
    [Trait("Plan", "EDGE-move-all-parts-37")]
    public async Task PutBack_FailedThenRetry()
    {
        var (s0, b0) = await f.Post("/actions/put-back", "{}");
        Assert.Empty(b0.GetProperty("studio").EnumerateArray());   // no session: nothing
        await Open();
        studio.EndAnswer = s => new StudioPutBack(s.ModDir, "failed", "restored", "still-patched", true);
        await f.Post("/actions/put-back", "{}");
        Assert.True(f.Changes.HasStudioSession);
        studio.EndAnswer = s => new StudioPutBack(s.ModDir, "failed", "restored", "blocked:hat compatibility", true);
        var (_, blocked) = await f.Post("/actions/put-back", "{}");
        Assert.Equal("blocked:hat compatibility", blocked.GetProperty("studio")[0].GetProperty("code").GetString());
        studio.EndAnswer = null;
        await f.Post("/actions/put-back", "{}");
        Assert.False(f.Changes.HasStudioSession);
        Assert.Equal(3, studio.Ended.Count);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-15")]
    public async Task HasStudioSession_Lifecycle_AndOneAtATime()
    {
        Assert.False(f.Changes.HasStudioSession);
        await Open();
        Assert.True(f.Changes.HasStudioSession);
        var (status, body) = await Open();
        Assert.Equal((409, "put-back-first"), (status, Code(body)));
        await f.Post("/actions/put-back", "{}");
        Assert.False(f.Changes.HasStudioSession);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-15")]
    public void PutBack_AStepThrowing_DoesNotStopTheStudio()
    {
        f.Dispatcher.Run(() => f.Changes.StudioOpen("Outfit", "top.mdl", false));
        var collection = f.Game.Collection!.Value;
        f.Dispatcher.Run(() => f.Changes.Hold(collection, "Outfit", false, null));
        f.Game.ClearHoldThrows = true;
        var report = f.Dispatcher.Run(f.Changes.PutBack);
        Assert.Equal("failed", Assert.Single(report.Holds).Result);
        Assert.Equal("reverted", Assert.Single(report.Studio).Files);
        Assert.Single(studio.Ended);
    }
}
