using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>
/// The pose routes (knee standoff QA): an allowlisted looping emote, the camera's orbit and a live pose dump, each put
/// back by the one-hop put-back and confirmed on one later hop; a busy gate while anything settles.
/// </summary>
[Collection(HarnessTimingCollection.Name)]
public class HarnessPoseRoutesTests : IDisposable
{
    private static readonly HarnessTimings Fast = HarnessFixture.FastTimings with
    {
        EmoteSettle = TimeSpan.FromMilliseconds(60), CameraSettle = TimeSpan.FromMilliseconds(40),
        PutBackConfirm = TimeSpan.FromMilliseconds(60), EmoteInterval = TimeSpan.Zero, PoseDumpInterval = TimeSpan.Zero,
    };

    private HarnessFixture fx;
    private FakeGame Game => fx.Game;

    public HarnessPoseRoutesTests() => fx = new HarnessFixture(Fast);

    public void Dispose() => fx.Dispose();

    private void Use(HarnessTimings timings)
    {
        fx.Dispose();
        fx = new HarnessFixture(timings);
    }

    private static string Code(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString()!;

    private async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    // ── emote ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-knee-standoff-8")]
    [Trait("Plan", "EDGE-knee-standoff-21")]
    public async Task Emote_plays_an_allowlisted_loop_and_refuses_the_rest()
    {
        var (status, body) = await fx.Post("/actions/emote", """{"emote":"groundsit"}""");
        Assert.Equal(200, status);
        Assert.True(body.GetProperty("settled").GetBoolean());
        Assert.Equal(HarnessPose.LoopMode, body.GetProperty("mode").GetString());
        Assert.Equal([(ushort)52], Game.Played);
        Assert.Equal("groundsit", fx.Changes.EmoteRecordNow!.Name);

        // Already in the loop (the harness's own or the player's): emote-busy, nothing played.
        Assert.Equal((409, "emote-busy"), await Refusal("""{"emote":"sit"}"""));
        Game.EmoteValue = new EmoteNow(HarnessPose.LoopMode, 88);
        Assert.Equal((409, "emote-busy"), await Refusal("""{"emote":"sit"}"""));
        Game.EmoteValue = new EmoteNow(HarnessPose.NormalMode, 0);

        Assert.Equal((400, "emote-not-allowed"), await Refusal("""{"emote":"dance"}"""));
        Game.Blocked = "InCombat";
        Assert.Equal((409, "emote-blocked:InCombat"), await Refusal("""{"emote":"sit"}"""));
        Game.Blocked = null;
        Game.Unusable.Add(50);
        Assert.Equal((409, "emote-unavailable"), await Refusal("""{"emote":"sit"}"""));
        Game.Unusable.Clear();
        Game.EmoteValue = null;
        Assert.Equal((409, "no-player"), await Refusal("""{"emote":"sit"}"""));
        Game.EmoteValue = new EmoteNow("EmoteSitting", 50);   // between modes
        Assert.Equal((429, "emote-settling"), await Refusal("""{"emote":"sit"}"""));
        Game.EmoteValue = new EmoteNow(HarnessPose.NormalMode, 0);
        Assert.Equal((400, "invalid-field"), await Refusal("""{}"""));
        Assert.Equal((400, "invalid-field"), await Refusal("""{"emote":"sit","target":1}"""));
        Assert.Single(Game.Played);
    }

    private async Task<(int, string)> Refusal(string json)
    {
        var (s, b) = await fx.Post("/actions/emote", json);
        return (s, Code(b));
    }

    [Fact]
    [Trait("Plan", "EDGE-knee-standoff-21")]
    public async Task A_second_emote_within_the_interval_and_a_changed_sheet_are_refused()
    {
        Use(Fast with { EmoteInterval = TimeSpan.FromSeconds(30) });
        Assert.Equal(200, (await fx.Post("/actions/emote", """{"emote":"groundsit"}""")).Status);
        Game.EmoteValue = new EmoteNow(HarnessPose.NormalMode, 0);
        Assert.Equal((429, "emote-settling"), await Refusal("""{"emote":"sit"}"""));

        Use(Fast);
        Game.Rows = new() { [50] = "/sit", [52] = "/lounge" };
        Assert.Equal((409, "emote-table-changed"), await Refusal("""{"emote":"sit"}"""));
        Assert.Empty(Game.Played);
        fx.Changes.BeginStop();
        Assert.Equal((503, "harness-stopping"), await Refusal("""{"emote":"sit"}"""));
    }

    [Fact]
    [Trait("Plan", "AC-knee-standoff-8")]
    public async Task Emote_settles_by_two_hops_without_blocking_the_framework()
    {
        Use(Fast with { EmoteSettle = TimeSpan.FromMilliseconds(500) });
        int before = fx.Dispatcher.Hops;
        var emote = fx.Post("/actions/emote", """{"emote":"sit"}""");
        await WaitUntil(() => fx.Changes.PoseSettling);
        // The wait is off the framework thread: a read answers meanwhile.
        Assert.Equal(200, (await fx.Get("/state")).Status);
        var (status, _) = await emote;
        Assert.Equal(200, status);
        // One hop to play, one to read, and the /state read's own hop.
        Assert.Equal(3, fx.Dispatcher.Hops - before);
        Assert.False(fx.Changes.PoseSettling);

        // A character that has not reached the loop by the read hop: settled false, the record keeps what was read.
        Use(Fast);
        Game.EmoteValue = new EmoteNow(HarnessPose.NormalMode, 0);
        Game.PlayLeavesMode = "EmoteSitting";
        var (_, slow) = await fx.Post("/actions/emote", """{"emote":"groundsit"}""");
        Assert.False(slow.GetProperty("settled").GetBoolean());
        Assert.Equal("EmoteSitting", fx.Changes.EmoteRecordNow!.Mode);
    }

    // ── put-back ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-knee-standoff-8")]
    [Trait("Plan", "EDGE-knee-standoff-22")]
    public async Task Http_put_back_is_one_hop_then_one_confirming_hop()
    {
        Assert.Equal(200, (await fx.Post("/actions/emote", """{"emote":"groundsit"}""")).Status);
        Assert.Equal(200, (await fx.Post("/actions/camera", """{"yaw":180}""")).Status);
        int before = fx.Dispatcher.Hops;
        var (status, body) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal(200, status);
        Assert.Equal(2, fx.Dispatcher.Hops - before);
        var pose = body.GetProperty("pose");
        Assert.Equal("stood", pose.GetProperty("emote").GetString());
        Assert.Equal("restored", pose.GetProperty("camera").GetString());
        Assert.Equal([(ushort)52, 52], Game.Played);
        Assert.True(fx.Changes.Empty);

        // Still seated at the confirming hop: failed, recorded again for the next put-back.
        Assert.Equal(200, (await fx.Post("/actions/emote", """{"emote":"groundsit"}""")).Status);
        Game.StandFails = true;
        var (_, failed) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal("failed", failed.GetProperty("pose").GetProperty("emote").GetString());
        Assert.NotNull(fx.Changes.EmoteRecordNow);
        Game.StandFails = false;
        var (_, retried) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal("stood", retried.GetProperty("pose").GetProperty("emote").GetString());

        // Moved on since: changed, nothing sent, no confirming hop.
        Assert.Equal(200, (await fx.Post("/actions/emote", """{"emote":"groundsit"}""")).Status);
        Game.EmoteValue = new EmoteNow(HarnessPose.NormalMode, 0);
        int played = Game.Played.Count;
        before = fx.Dispatcher.Hops;
        var (_, changed) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal("changed", changed.GetProperty("pose").GetProperty("emote").GetString());
        Assert.Equal(played, Game.Played.Count);
        Assert.Equal(1, fx.Dispatcher.Hops - before);

        // Nothing recorded: none, one hop.
        var (_, none) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal("none", none.GetProperty("pose").GetProperty("emote").GetString());
        Assert.Equal("none", none.GetProperty("pose").GetProperty("camera").GetString());
    }

    [Fact]
    [Trait("Plan", "EDGE-knee-standoff-22")]
    public async Task The_host_stopping_during_the_confirming_wait_skips_the_read()
    {
        Use(Fast with { PutBackConfirm = TimeSpan.FromMilliseconds(400) });
        Assert.Equal(200, (await fx.Post("/actions/emote", """{"emote":"groundsit"}""")).Status);
        int before = fx.Dispatcher.Hops;
        var putBack = fx.Post("/actions/put-back", "{}");
        await WaitUntil(() => fx.Changes.PoseSettling);
        fx.Changes.BeginStop();
        var (status, body) = await putBack;
        Assert.Equal(200, status);
        Assert.Equal("harness-stopping", body.GetProperty("pose").GetProperty("confirm").GetString());
        Assert.Equal("stand-sent", body.GetProperty("pose").GetProperty("emote").GetString());
        Assert.Equal(1, fx.Dispatcher.Hops - before);
        Assert.False(fx.Changes.PoseSettling);
    }

    [Fact]
    [Trait("Plan", "AC-knee-standoff-8")]
    [Trait("Plan", "EDGE-knee-standoff-31")]
    public async Task Busy_while_a_pose_change_settles()
    {
        Use(Fast with { EmoteSettle = TimeSpan.FromMilliseconds(800), PutBackConfirm = TimeSpan.FromMilliseconds(800) });
        fx.AddMod("Equinox");
        var emote = fx.Post("/actions/emote", """{"emote":"groundsit"}""");
        await WaitUntil(() => fx.Changes.PoseSettling);
        foreach (var (path, json) in new[]
                 {
                     ("/actions/camera", """{"yaw":90}"""), ("/actions/equip", """{"slot":"Body","item":500}"""),
                     ("/actions/mod-settings", """{"mod":"Equinox","enabled":false}"""),
                     ("/actions/glamourer-apply", $$$"""{"design":"{{{Guid.NewGuid()}}}"}"""), ("/actions/put-back", "{}"),
                     ("/actions/emote", """{"emote":"sit"}"""),
                 })
        {
            var (s, b) = await fx.Post(path, json);
            Assert.True((s, Code(b)) == (409, "busy"), $"{path}: {s} {b}");
        }
        Assert.Equal(200, (await fx.Get("/state")).Status);
        Assert.Equal(200, (await fx.Post("/actions/posedump", """{"label":"mid-settle"}""")).Status);
        Assert.Equal(200, (await emote).Status);
        Assert.False(fx.Changes.PoseSettling);

        // An emote during put-back's confirming wait: busy, and the put-back still confirms its stand-up.
        var putBack = fx.Post("/actions/put-back", "{}");
        await WaitUntil(() => fx.Changes.PoseSettling);
        var (es, eb) = await fx.Post("/actions/emote", """{"emote":"sit"}""");
        Assert.Equal((409, "busy"), (es, Code(eb)));
        var (_, pb) = await putBack;
        Assert.Equal("stood", pb.GetProperty("pose").GetProperty("emote").GetString());
        Assert.False(fx.Changes.PoseSettling);
    }

    [Fact]
    [Trait("Plan", "AC-knee-standoff-9")]
    [Trait("Plan", "EDGE-knee-standoff-31")]
    public async Task Busy_while_the_camera_settles()
    {
        Use(Fast with { CameraSettle = TimeSpan.FromMilliseconds(800) });
        var camera = fx.Post("/actions/camera", """{"yaw":180}""");
        await WaitUntil(() => fx.Changes.PoseSettling);
        foreach (var (path, json) in new[]
                 {
                     ("/actions/emote", """{"emote":"sit"}"""), ("/actions/equip", """{"slot":"Body","item":500}"""),
                     ("/actions/put-back", "{}"), ("/actions/camera", """{"yaw":90}"""),
                 })
        {
            var (s, b) = await fx.Post(path, json);
            Assert.True((s, Code(b)) == (409, "busy"), $"{path}: {s} {b}");
        }
        Assert.Equal(200, (await fx.Post("/actions/posedump", """{"label":"camera-settle"}""")).Status);
        Assert.Equal(200, (await camera).Status);
        Assert.False(fx.Changes.PoseSettling);

        // Stopping during the settle: harness-stopping, the camera still recorded (so put-back or the leftovers know it).
        var again = fx.Post("/actions/camera", """{"yaw":90}""");
        await WaitUntil(() => fx.Changes.PoseSettling);
        fx.Changes.BeginStop();
        var (status, body) = await again;
        Assert.Equal((503, "harness-stopping"), (status, Code(body)));
        Assert.NotNull(fx.Changes.CameraRecordNow);
        Assert.False(fx.Changes.PoseSettling);
    }

    [Fact]
    [Trait("Plan", "EDGE-knee-standoff-22")]
    public async Task An_emote_is_recorded_as_soon_as_it_is_played()
    {
        Use(Fast with { EmoteSettle = TimeSpan.FromMilliseconds(600) });
        var emote = fx.Post("/actions/emote", """{"emote":"groundsit"}""");
        await WaitUntil(() => fx.Changes.PoseSettling);
        Assert.Contains("emote:groundsit", fx.Changes.Leftovers());
        fx.Changes.BeginStop();
        var (status, body) = await emote;
        Assert.Equal((503, "harness-stopping"), (status, Code(body)));
        Assert.Contains("emote:groundsit", fx.Changes.Leftovers());
    }

    [Fact]
    [Trait("Plan", "EDGE-knee-standoff-21")]
    public async Task An_unreadable_sheet_is_asked_again_not_taken_as_changed()
    {
        Game.Rows = null;
        Assert.Equal((409, "emote-unavailable"), await Refusal("""{"emote":"sit"}"""));
        Game.Rows = new() { [50] = "/lounge|/sit", [52] = "/groundsit" };
        Assert.Equal(200, (await fx.Post("/actions/emote", """{"emote":"sit"}""")).Status);
    }

    [Fact]
    [Trait("Plan", "EDGE-knee-standoff-31")]
    public async Task The_gate_clears_when_the_read_hop_throws()
    {
        Use(Fast with { EmoteSettle = TimeSpan.FromMilliseconds(200) });
        var emote = fx.Post("/actions/emote", """{"emote":"groundsit"}""");
        await WaitUntil(() => fx.Changes.PoseSettling);
        fx.Dispatcher.RefuseHopWhen = () => true;   // the read hop throws
        var (status, _) = await emote;
        fx.Dispatcher.RefuseHopWhen = null;
        Assert.Equal(500, status);
        Assert.False(fx.Changes.PoseSettling);
    }

    // ── camera ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-knee-standoff-9")]
    [Trait("Plan", "EDGE-knee-standoff-23")]
    public async Task Camera_orbits_and_puts_back()
    {
        var found = Game.CameraValue!.Value;
        var (status, body) = await fx.Post("/actions/camera", """{"yaw":180,"pitch":-200,"distance":50}""");
        Assert.Equal(200, status);
        Assert.Equal(["pitch", "distance"], body.GetProperty("clamped").EnumerateArray().Select(e => e.GetString()).ToArray());
        var written = Game.CameraWrites.Single();
        Assert.Equal(HarnessPose.DirHFor(1.0f, 180), written.DirH, 4);
        Assert.Equal(found.MinDirV, written.DirV, 4);
        Assert.Equal(found.MaxDistance, written.Distance, 4);
        Assert.Equal(180f, MathF.Abs(body.GetProperty("settled").GetProperty("yaw").GetSingle()), 2);

        // A second move keeps the first Before.
        Assert.Equal(200, (await fx.Post("/actions/camera", """{"yaw":90}""")).Status);
        Assert.Equal(new CameraPose(found.DirH, found.DirV, found.Distance), fx.Changes.CameraRecordNow!.Before);

        var (_, pb) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal("restored", pb.GetProperty("pose").GetProperty("camera").GetString());
        Assert.Equal(found.DirH, Game.CameraValue!.Value.DirH, 4);
        Assert.Equal(found.Distance, Game.CameraValue!.Value.Distance, 4);

        // Moved beyond the tolerance since (auto-adjust, the player): changed, left alone.
        Assert.Equal(200, (await fx.Post("/actions/camera", """{"yaw":90}""")).Status);
        Game.CameraValue = Game.CameraValue!.Value with { DirH = Game.CameraValue!.Value.DirH + 0.1f };
        int writes = Game.CameraWrites.Count;
        var (_, moved) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal("changed", moved.GetProperty("pose").GetProperty("camera").GetString());
        Assert.Equal(writes, Game.CameraWrites.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-knee-standoff-23")]
    public async Task Camera_refuses_bad_values_and_no_camera()
    {
        Assert.Equal((400, "bad-value"), await CameraRefusal("""{"yaw":"back"}"""));
        Assert.Equal((400, "bad-value"), await CameraRefusal("""{"distance":1e400}"""));
        Assert.Equal((400, "invalid-field"), await CameraRefusal("""{}"""));
        Game.CameraValue = null;
        Assert.Equal((409, "camera-unavailable"), await CameraRefusal("""{"yaw":90}"""));
        Assert.Empty(Game.CameraWrites);
        Assert.Null(fx.Changes.CameraRecordNow);
    }

    private async Task<(int, string)> CameraRefusal(string json)
    {
        var (s, b) = await fx.Post("/actions/camera", json);
        return (s, Code(b));
    }

    // ── posedump ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-knee-standoff-10")]
    [Trait("Plan", "EDGE-knee-standoff-24")]
    public async Task Posedump_writes_inside_the_session_folder()
    {
        var (status, body) = await fx.Post("/actions/posedump", """{"label":"idle-fresh"}""");
        Assert.Equal(200, status);
        string folder = body.GetProperty("folder").GetString()!;
        Assert.Equal("posedumps/session-a/idle-fresh", folder);
        Assert.Equal(120, body.GetProperty("bones").GetInt32());
        Assert.Equal(7, body.GetProperty("models").GetInt32());
        Assert.DoesNotContain(":", body.GetRawText().Replace("\"bones\":", "").Replace("\"models\":", "").Replace("\"folder\":", ""));
        var dir = Game.Dumps.Single();
        Assert.StartsWith(Path.Combine(fx.Temp.Path, "config", "test-harness", "posedumps", "session-a"), dir);

        Assert.Equal((409, "exists"), await DumpRefusal("""{"label":"idle-fresh"}"""));
        foreach (var bad in new[] { "../x", "", "Idle", new string('a', 33), "a b" })
            Assert.Equal((400, "bad-label"), await DumpRefusal(JsonSerializer.Serialize(new { label = bad })));
        Game.DumpFails = true;
        Assert.Equal((409, "no-player"), await DumpRefusal("""{"label":"nobody"}"""));
        Assert.True(Directory.Exists(dir));   // never deleted

        Use(Fast with { PoseDumpInterval = TimeSpan.FromSeconds(30) });
        Assert.Equal(200, (await fx.Post("/actions/posedump", """{"label":"one"}""")).Status);
        Assert.Equal((429, "posedump-settling"), await DumpRefusal("""{"label":"two"}"""));
    }

    private async Task<(int, string)> DumpRefusal(string json)
    {
        var (s, b) = await fx.Post("/actions/posedump", json);
        return (s, Code(b));
    }

    // ── records, leftovers and /state ─────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-knee-standoff-8")]
    [Trait("Plan", "AC-knee-standoff-9")]
    public async Task Pose_records_count_as_changes_and_leftovers()
    {
        Assert.True(fx.Changes.Empty);
        await fx.Post("/actions/emote", """{"emote":"groundsit"}""");
        Assert.False(fx.Changes.Empty);
        Assert.Contains("emote:groundsit", fx.Changes.Leftovers());
        await fx.Post("/actions/camera", """{"yaw":180}""");
        Assert.Contains("camera", fx.Changes.Leftovers());

        var (_, state) = await fx.Get("/state");
        Assert.Equal("groundsit", state.GetProperty("pose").GetProperty("emote").GetString());
        Assert.Equal(HarnessPose.LoopMode, state.GetProperty("pose").GetProperty("mode").GetString());
        Assert.Equal(180f, MathF.Abs(state.GetProperty("camera").GetProperty("yaw").GetSingle()), 2);
        Assert.Equal("groundsit", state.GetProperty("harnessChanges").GetProperty("emote").GetProperty("name").GetString());
    }
}
