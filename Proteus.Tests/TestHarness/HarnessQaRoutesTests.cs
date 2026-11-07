using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>
/// The idle-disk-io harness routes over a real loopback server and a fake game: boot re-arm, sidecar edit and view, the
/// session auto-refit override, other-collection holds, the new <c>/state</c> blocks, and what put-back does with them.
/// </summary>
[Collection(HarnessTimingCollection.Name)]
public class HarnessQaRoutesTests : IDisposable
{
    private readonly HarnessFixture fx = new();

    public void Dispose() => fx.Dispose();

    private string SidecarMod(string dir = "Skin")
    {
        fx.AddMod(dir);
        fx.Game.PutSidecar(dir, (16, "#111111"), (17, "#222222"));
        return dir;
    }

    // ── boot re-arm ─────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-9")]
    [Trait("Plan", "EDGE-idle-disk-io-35")]
    public async Task BootRearm_RearmsAndReturnsBoot()
    {
        // [AC-idle-disk-io-9] [EDGE-idle-disk-io-35]
        var (status, body) = await fx.Post("/actions/boot-rearm", "{}");
        Assert.Equal(200, status);
        Assert.True(body.GetProperty("rearmed").GetBoolean());
        Assert.False(body.GetProperty("boot").GetProperty("latched").GetBoolean());
        Assert.Equal(1, fx.Game.Rearms);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-35")]
    public async Task BootRearm_RejectsBodyFields()
    {
        // [EDGE-idle-disk-io-35]
        var (status, body) = await fx.Post("/actions/boot-rearm", "{\"now\":true}");
        Assert.Equal(400, status);
        Assert.Equal("invalid-field", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fx.Game.Rearms);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-35")]
    public async Task BootRearm_Stopping_503()
    {
        // [EDGE-idle-disk-io-35]
        fx.Changes.BeginStop();
        var (status, _) = await fx.Post("/actions/boot-rearm", "{}");
        Assert.Equal(503, status);
        Assert.Equal(0, fx.Game.Rearms);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-9")]
    public async Task State_HasCompositorBoot()
    {
        // [AC-idle-disk-io-9]
        fx.Game.BootValue = (false, 2, 29_500);
        var (_, state) = await fx.Get("/state");
        var boot = state.GetProperty("compositor").GetProperty("boot");
        Assert.Equal((false, 2, 29_500L), (boot.GetProperty("latched").GetBoolean(), boot.GetProperty("step").GetInt32(),
                                           boot.GetProperty("nextDueInMs").GetInt64()));
    }

    // ── sidecar edit ────────────────────────────────────────────────────────

    [Theory]
    [Trait("Plan", "AC-idle-disk-io-10")]
    [Trait("Plan", "EDGE-idle-disk-io-36")]
    [InlineData("{\"row\":16,\"diffuse\":\"#123456\"}")]                                      // no mod
    [InlineData("{\"mod\":\"Skin\",\"diffuse\":\"#123456\"}")]                                // no row
    [InlineData("{\"mod\":\"Skin\",\"row\":16}")]                                            // no diffuse
    [InlineData("{\"mod\":\"Skin\",\"row\":16,\"diffuse\":\"red\"}")]                        // bad colour
    [InlineData("{\"mod\":\"Skin\",\"row\":1.5,\"diffuse\":\"#123456\"}")]                   // fractional row
    [InlineData("{\"mod\":\"Skin\",\"row\":16,\"diffuse\":\"#123456\",\"sub\":\"C\"}")]      // bad sub
    [InlineData("{\"mod\":\"Skin\",\"row\":16,\"diffuse\":\"#123456\",\"group\":\"G\"}")]    // half of group/option
    [InlineData("{\"mod\":\"Skin\",\"row\":16,\"diffuse\":\"#123456\",\"restoreMtime\":1}")] // bad restoreMtime
    [InlineData("{\"mod\":\"Skin\",\"row\":16,\"diffuse\":\"#123456\",\"lock\":true}")]      // unknown field
    [InlineData("{\"mod\":\"..\\\\Skin\",\"row\":16,\"diffuse\":\"#123456\"}")]              // not a plain folder
    public async Task SidecarEdit_Validation(string json)
    {
        // [AC-idle-disk-io-10] [EDGE-idle-disk-io-36]
        SidecarMod();
        var (status, body) = await fx.Post("/actions/sidecar-edit", json);
        Assert.Equal(400, status);
        Assert.Equal("invalid-field", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fx.Game.Saves);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-10")]
    [Trait("Plan", "EDGE-idle-disk-io-36")]
    public async Task SidecarEdit_UnknownMod_ManagedMod_NoSidecar_RowMissing()
    {
        // [AC-idle-disk-io-10] [EDGE-idle-disk-io-36]
        SidecarMod();
        fx.AddMod("Plain");
        fx.Game.ModFolders[fx.Game.ManagedModDir] = "Proteus";
        async Task<(int, string?)> Try(string mod, int row)
        {
            var (s, b) = await fx.Post("/actions/sidecar-edit", $"{{\"mod\":\"{mod}\",\"row\":{row},\"diffuse\":\"#123456\"}}");
            return (s, b.GetProperty("error").GetProperty("code").GetString());
        }
        Assert.Equal((404, "mod-not-found"), await Try("Nope", 16));
        Assert.Equal((400, "managed-mod"), await Try(fx.Game.ManagedModDir, 16));
        Assert.Equal((404, "not-a-proteus-mod"), await Try("Plain", 16));
        Assert.Equal((404, "row-not-found"), await Try("Skin", 3));
        Assert.Equal(0, fx.Game.Saves);
        Assert.True(fx.Changes.Sidecar.Empty);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-10")]
    [Trait("Plan", "EDGE-idle-disk-io-11")]
    public async Task SidecarEdit_Saves_ReadsBack_RestoresMtime_PutBackRestores()
    {
        // [AC-idle-disk-io-10] [EDGE-idle-disk-io-11]
        var mod = SidecarMod();
        var original = File.ReadAllBytes(fx.Game.MetaPath(mod));
        var (status, body) = await fx.Post("/actions/sidecar-edit",
            $"{{\"mod\":\"{mod}\",\"row\":16,\"diffuse\":\"#ABCDEF\",\"restoreMtime\":true}}");
        Assert.Equal(200, status);
        Assert.Equal("#111111", body.GetProperty("before").GetString());
        Assert.Equal("#ABCDEF", body.GetProperty("readBack").GetString());
        Assert.True(body.GetProperty("mtimeRestored").GetBoolean());
        Assert.Equal(body.GetProperty("stampBefore").GetProperty("ticks").GetInt64(),
                     body.GetProperty("stampAfter").GetProperty("ticks").GetInt64());
        Assert.StartsWith("test-harness/backups/", body.GetProperty("backup").GetString());
        Assert.Contains("test harness: sidecar edit", fx.Game.Recomposites);

        var (_, putBack) = await fx.Post("/actions/put-back", "{}");
        var undo = Assert.Single(putBack.GetProperty("sidecar").EnumerateArray());
        Assert.Equal("restored", undo.GetProperty("result").GetString());
        Assert.Equal("deleted", undo.GetProperty("defaults").GetString());
        Assert.Equal(original, File.ReadAllBytes(fx.Game.MetaPath(mod)));
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-10")]
    [Trait("Plan", "EDGE-idle-disk-io-40")]
    public async Task SidecarEdit_UserEditHeld_409()
    {
        // [AC-idle-disk-io-10] [EDGE-idle-disk-io-40]
        var mod = SidecarMod();
        fx.Game.StagedMods[mod] = 0;
        var (status, body) = await fx.Post("/actions/sidecar-edit", $"{{\"mod\":\"{mod}\",\"row\":16,\"diffuse\":\"#ABCDEF\"}}");
        Assert.Equal(409, status);
        Assert.Equal("user-edit-held", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fx.Game.Saves);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-10")]
    public async Task SidecarView_ReadsThroughDiscovery()
    {
        // [AC-idle-disk-io-10]
        var mod = SidecarMod();
        var (status, body) = await fx.Get($"/sidecar?mod={mod}&row=17");
        Assert.Equal(200, status);
        Assert.True(body.GetProperty("found").GetBoolean());
        var row = Assert.Single(body.GetProperty("rows").EnumerateArray());
        Assert.Equal("#222222", row.GetProperty("diffuseA").GetString());
        Assert.False(body.GetProperty("staged").GetBoolean());
        Assert.Contains(fx.Game.Order, o => o == "DiscoverEntry@framework");
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-59")]
    public async Task NoFrameworkHopWhileLockHeld()
    {
        // [EDGE-idle-disk-io-59] A hop asked for under the sidecar lock throws, which would answer 500.
        var mod = SidecarMod();
        fx.Dispatcher.RefuseHopWhen = () => fx.Changes.Sidecar.LockHeldByCaller;
        bool sawLock = false;
        fx.Game.SaveHook = _ => { sawLock = fx.Changes.Sidecar.LockHeldByCaller; return true; };
        var (status, _) = await fx.Post("/actions/sidecar-edit", $"{{\"mod\":\"{mod}\",\"row\":16,\"diffuse\":\"#ABCDEF\"}}");
        Assert.Equal(200, status);
        Assert.True(sawLock);   // the save really ran under the lock
        var (pb, _) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal(200, pb);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-59")]
    [Trait("Plan", "EDGE-idle-disk-io-56")]
    public async Task FrameworkPutBack_CompletesWhileSaveBlocked()
    {
        // [EDGE-idle-disk-io-59] [EDGE-idle-disk-io-56]
        var mod = SidecarMod();
        var original = File.ReadAllBytes(fx.Game.MetaPath(mod));
        using var inSave = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fx.Game.SaveHook = _ => { inSave.Set(); release.Wait(5000); return true; };
        var edit = fx.Post("/actions/sidecar-edit", $"{{\"mod\":\"{mod}\",\"row\":16,\"diffuse\":\"#ABCDEF\"}}");
        Assert.True(inSave.Wait(5000));
        var putBack = fx.Post("/actions/put-back", "{}");   // runs on the framework thread and waits on the lock
        await Task.Delay(100);
        Assert.False(putBack.IsCompleted);
        release.Set();
        Assert.Equal(200, (await edit).Status);
        var (pb, body) = await putBack;
        Assert.Equal(200, pb);
        Assert.Equal("restored", Assert.Single(body.GetProperty("sidecar").EnumerateArray()).GetProperty("result").GetString());
        Assert.Equal(original, File.ReadAllBytes(fx.Game.MetaPath(mod)));
    }

    // ── other-collection holds ──────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-11")]
    [Trait("Plan", "EDGE-idle-disk-io-42")]
    public async Task ModSettings_Collection_HoldsInThatCollection()
    {
        // [AC-idle-disk-io-11] [EDGE-idle-disk-io-42]
        fx.AddMod("Outfit");
        var (status, body) = await fx.Post("/actions/mod-settings",
            $"{{\"mod\":\"Outfit\",\"enabled\":false,\"collection\":\"{FakeGame.OtherCollection}\"}}");
        Assert.Equal(200, status);
        Assert.Equal(FakeGame.OtherCollection, body.GetProperty("collection").GetGuid());
        Assert.Equal(FakeGame.OtherCollection, fx.Game.HoldSets.Last().Collection);

        await fx.Post("/actions/put-back", "{}");
        Assert.DoesNotContain((FakeGame.OtherCollection, "Outfit"), fx.Game.Temporary.Keys);
    }

    [Theory]
    [Trait("Plan", "AC-idle-disk-io-11")]
    [Trait("Plan", "EDGE-idle-disk-io-42")]
    [InlineData("\"not-a-guid\"", false, 400, "invalid-field")]
    [InlineData("\"99999999-8888-7777-6666-555555555555\"", true, 400, "invalid-field")]
    [InlineData("\"12345678-1234-1234-1234-123456789abc\"", false, 404, "collection-not-found")]
    public async Task ModSettings_Collection_Malformed_WithPermanent_Unknown(string collection, bool permanent, int code, string error)
    {
        // [AC-idle-disk-io-11] [EDGE-idle-disk-io-42]
        fx.AddMod("Outfit");
        var (status, body) = await fx.Post("/actions/mod-settings",
            $"{{\"mod\":\"Outfit\",\"enabled\":false,\"collection\":{collection}{(permanent ? ",\"permanent\":true" : "")}}}");
        Assert.Equal(code, status);
        Assert.Equal(error, body.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(fx.Game.HoldSets);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-11")]
    [Trait("Plan", "EDGE-idle-disk-io-42")]
    public async Task ModSettings_PlayerCollectionGuid_SameAsNone()
    {
        // [AC-idle-disk-io-11] [EDGE-idle-disk-io-42]
        fx.AddMod("Outfit");
        var (status, _) = await fx.Post("/actions/mod-settings",
            $"{{\"mod\":\"Outfit\",\"enabled\":false,\"collection\":\"{fx.Game.Collection}\"}}");
        Assert.Equal(200, status);
        Assert.Equal(fx.Game.Collection, fx.Game.HoldSets.Last().Collection);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-11")]
    [Trait("Plan", "EDGE-idle-disk-io-49")]
    public async Task State_Collections_IdsAndPlayerFlagOnly()
    {
        // [AC-idle-disk-io-11] [EDGE-idle-disk-io-49]
        var (_, state) = await fx.Get("/state");
        var list = state.GetProperty("collections").EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        foreach (var c in list)
            Assert.Equal(["id", "player"], c.EnumerateObject().Select(p => p.Name).ToList());
        Assert.DoesNotContain(fx.Game.CollectionName, state.ToString());
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-11")]
    [Trait("Plan", "EDGE-idle-disk-io-53")]
    public async Task PutBack_SweepsEveryListedCollection()
    {
        // [AC-idle-disk-io-11] [EDGE-idle-disk-io-53] An earlier session's hold in another collection is swept too.
        var third = Guid.NewGuid();
        fx.Game.CollectionList = [(fx.Game.Collection!.Value, true), (FakeGame.OtherCollection, false), (third, false)];
        fx.Game.TemporaryKeys[(third, "Leftover")] = HarnessChanges.HoldKey;
        await fx.Post("/actions/put-back", "{}");
        Assert.Contains(fx.Game.Sweeps, s => s.Collection == third);
        Assert.Contains(fx.Game.Sweeps, s => s.Collection == FakeGame.OtherCollection);
        Assert.DoesNotContain((third, "Leftover"), fx.Game.TemporaryKeys.Keys);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-53")]
    public async Task PutBack_GetCollectionsNull_FallsBackToRecordedPlusPlayer()
    {
        // [EDGE-idle-disk-io-53]
        fx.AddMod("Outfit");
        await fx.Post("/actions/mod-settings", $"{{\"mod\":\"Outfit\",\"enabled\":false,\"collection\":\"{FakeGame.OtherCollection}\"}}");
        fx.Game.CollectionsFail = true;
        await fx.Post("/actions/put-back", "{}");
        Assert.Equal([fx.Game.Collection!.Value, FakeGame.OtherCollection],
                     fx.Game.Sweeps.Select(s => s.Collection).Distinct().OrderBy(g => g == fx.Game.Collection ? 0 : 1).ToList());
    }

    // ── the auto-refit override ─────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-12")]
    public async Task AutoRefit_SetsOverride_ReportsConfigOverrideEffective()
    {
        // [AC-idle-disk-io-12]
        fx.Game.AutoRefitConfig = true;
        var (status, body) = await fx.Post("/actions/auto-refit", "{\"enabled\":false}");
        Assert.Equal(200, status);
        Assert.True(body.GetProperty("config").GetBoolean());
        Assert.False(body.GetProperty("harnessOverride").GetBoolean());
        Assert.False(body.GetProperty("effective").GetBoolean());
        Assert.True(body.GetProperty("before").GetBoolean());

        var (bad, _) = await fx.Post("/actions/auto-refit", "{}");
        Assert.Equal(400, bad);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-12")]
    public async Task State_AutoRefit_BodyIndexBlock()
    {
        // [AC-idle-disk-io-12]
        fx.Game.BodyIndexValue = (false, true, false, 7);
        await fx.Post("/actions/auto-refit", "{\"enabled\":true}");
        var (_, state) = await fx.Get("/state");
        var a = state.GetProperty("autoRefit");
        Assert.True(a.GetProperty("enabled").GetProperty("harnessOverride").GetBoolean());
        Assert.True(a.GetProperty("enabled").GetProperty("effective").GetBoolean());
        var bi = a.GetProperty("bodyIndex");
        Assert.Equal((false, true, false, 7L), (bi.GetProperty("built").GetBoolean(), bi.GetProperty("stale").GetBoolean(),
                                                bi.GetProperty("refreshing").GetBoolean(), bi.GetProperty("generation").GetInt64()));
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-12")]
    [Trait("Plan", "EDGE-idle-disk-io-44")]
    public async Task PutBack_ClearsAutoRefitOverride()
    {
        // [AC-idle-disk-io-12] [EDGE-idle-disk-io-44]
        await fx.Post("/actions/auto-refit", "{\"enabled\":true}");
        fx.Game.AutoRefitConfig = true;   // the user flipped the checkbox meanwhile: the override still decides until put-back
        Assert.False(fx.Changes.Empty);
        var (_, body) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal("cleared", body.GetProperty("autoRefit").GetProperty("result").GetString());
        Assert.Null(fx.Game.AutoRefitOverrideValue);
        Assert.True(fx.Changes.Empty);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-54")]
    public async Task PutBack_ReportsRefitsWhileOverridden()
    {
        // [EDGE-idle-disk-io-54]
        await fx.Post("/actions/auto-refit", "{\"enabled\":true}");
        fx.Game.RefitsWhileOverridden.Enqueue("Some Outfit");
        var (_, body) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal(["Some Outfit"], body.GetProperty("autoRefit").GetProperty("refitsWhileOverridden")
                                          .EnumerateArray().Select(e => e.GetString()).ToList());
    }

    [Fact]
    public async Task Empty_FalseWhileOverrideOrSidecarRecorded()
    {
        Assert.True(fx.Changes.Empty);
        var mod = SidecarMod();
        await fx.Post("/actions/sidecar-edit", $"{{\"mod\":\"{mod}\",\"row\":16,\"diffuse\":\"#ABCDEF\"}}");
        Assert.False(fx.Changes.Empty);
        Assert.Contains("Skin's sidecar", fx.Changes.Leftovers());
        await fx.Post("/actions/put-back", "{}");
        Assert.True(fx.Changes.Empty);
    }

    // ── counters ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-13")]
    [Trait("Plan", "EDGE-idle-disk-io-46")]
    public async Task State_Io_SnapshotDoesNotReset()
    {
        // [AC-idle-disk-io-13] [EDGE-idle-disk-io-46]
        var counters = new Proteus.Services.IoCounters(_ => { });
        counters.Count(Proteus.Services.IoCounters.Redraws, 2);
        counters.Count(Proteus.Services.IoCounters.RedrawTreeReads, 2);
        fx.Game.IoValue = counters.Snapshot();
        var (_, state) = await fx.Get("/state");
        var counts = state.GetProperty("io").GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("redraws").GetInt64());
        Assert.Equal(2, counts.GetProperty("redraw tree reads").GetInt64());
        Assert.Equal(2, counters.Snapshot().Counts["redraws"]);   // reading did not reset it
    }
}
