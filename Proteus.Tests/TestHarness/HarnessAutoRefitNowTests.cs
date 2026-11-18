using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>
/// The worn-refit trigger (<c>POST /actions/auto-refit {"refitWornNow":true,"mod":…}</c>, <see cref="AutoRefitNowJob"/>):
/// one worn mod, backed up first and restorable by job, its collection setting and the auto refit preference put back
/// while still what the harness left, never armed when auto refit cannot run.
/// </summary>
[Collection(HarnessTimingCollection.Name)]
public sealed class HarnessAutoRefitNowTests : IDisposable
{
    private readonly HarnessFixture fx = new(HarnessFixture.FastTimings with { AutoRefitNowWait = TimeSpan.FromMilliseconds(1500) });

    public void Dispose() => fx.Dispose();

    private string Worn(string dir = "Sarelle")
    {
        string root = fx.AddMod(dir);
        fx.Game.WornFrom.Add(dir);
        fx.Game.Permanent[dir] = new PermanentSetting(true, 0);
        return root;
    }

    private Task<(int Status, JsonElement Body)> Trigger(string mod = "Sarelle")
        => fx.Post("/actions/auto-refit", $"{{\"refitWornNow\":true,\"mod\":\"{mod}\"}}");

    private static string Code(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-36")]
    public async Task AutoRefit_RefitWornNow_SingleModScoped()
    {
        string root = Worn();
        Worn("Other");
        var (status, body) = await Trigger();
        Assert.Equal(202, status);
        var job = await fx.WaitJob(body.GetProperty("job").GetString()!);
        Assert.Equal(JsonValueKind.Null, job.GetProperty("error").ValueKind);
        Assert.Equal(["Sarelle"], fx.Game.RunNowAsked);              // never the other worn mod
        var piece = Assert.Single(job.GetProperty("result").GetProperty("pieces").EnumerateArray());
        Assert.Equal("done", piece.GetProperty("outcome").GetString());
        Assert.Equal("refitted", File.ReadAllText(Path.Combine(root, "chara", "model", "top.mdl")));
        Assert.NotNull(fx.Jobs.BackupOfJob(job.GetProperty("id").GetString()!));
    }

    [Theory]
    [InlineData("{\"refitWornNow\":false,\"mod\":\"Sarelle\"}")]
    [InlineData("{\"refitWornNow\":true}")]
    [InlineData("{\"refitWornNow\":true,\"mod\":\"Sarelle\",\"enabled\":true}")]
    [InlineData("{\"mod\":\"Sarelle\",\"enabled\":true}")]
    [InlineData("{\"refitWornNow\":true,\"mods\":[\"Sarelle\"]}")]
    [Trait("Plan", "EDGE-split-waist-seam-36")]
    public async Task AutoRefit_RefitWornNow_BadFields_400(string json)
    {
        Worn();
        var (status, _) = await fx.Post("/actions/auto-refit", json);
        Assert.Equal(400, status);
        Assert.Empty(fx.Game.RunNowAsked);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-36")]
    public async Task AutoRefit_RefitWornNow_Refusals()
    {
        Worn();
        fx.AddMod("NotWorn");
        // A path escape is never one of Penumbra's folder names: refused before anything touches the disk.
        Assert.Equal((404, "mod-not-found"), await Answer(Trigger("..\\\\..")));
        Assert.Equal((404, "mod-not-found"), await Answer(Trigger("Nope")));
        Assert.Equal((400, "mod-not-worn"), await Answer(Trigger("NotWorn")));
        fx.Game.AutoRefitCanRun = false;
        Assert.Equal((409, "auto-refit-off"), await Answer(Trigger()));
        fx.Game.AutoRefitCanRun = true;
        fx.Game.PreferenceJson = null;
        Assert.Equal((503, "record-failed"), await Answer(Trigger()));
        Assert.Empty(fx.Game.RunNowAsked);
        Assert.False(fx.Changes.AutoRefitPreference.Recorded);
    }

    private static async Task<(int, string)> Answer(Task<(int Status, JsonElement Body)> call)
    {
        var (status, body) = await call;
        return (status, status >= 400 ? Code(body) : "");
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-37")]
    public async Task AutoRefitNow_NothingEnqueued_FailsNothingWritten()
    {
        string root = Worn();
        fx.Game.RunNowBody = _ => [("_top", "chara/equipment/e0628/model/c0201e0628_top.mdl", "not-enqueued:declined")];
        var (_, body) = await Trigger();
        var job = await fx.WaitJob(body.GetProperty("job").GetString()!);
        Assert.Equal("nothing-enqueued", Code(job));
        Assert.Equal("not-enqueued:declined",
                     job.GetProperty("result").GetProperty("pieces")[0].GetProperty("outcome").GetString());
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, "chara", "model", "top.mdl")));
        Assert.False(fx.Changes.AutoRefitPreference.Recorded);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-38")]
    public async Task AutoRefitNow_WaitCap_TimesOut()
    {
        Worn();
        fx.Game.RunNowBody = root =>
        {
            fx.Game.Activity = new Proteus.Services.AutoRefitWatcher.ActivitySnapshot(true, 1, false, false);   // never goes quiet
            return [("_top", "chara/equipment/e0628/model/c0201e0628_top.mdl", "enqueued")];
        };
        var (_, body) = await Trigger();
        var job = await fx.WaitJob(body.GetProperty("job").GetString()!);
        Assert.False(job.GetProperty("result").GetProperty("finished").GetBoolean());
        Assert.Equal("timed-out", job.GetProperty("result").GetProperty("pieces")[0].GetProperty("outcome").GetString());
        fx.Game.Activity = default;
        var (_, putBack) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal("recorded-before-finish", putBack.GetProperty("autoRefit").GetProperty("preference").GetString());
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-39")]
    public async Task PutBack_PreferenceUnchanged_RestoredAndSaved_ChangedLeftAsIs()
    {
        Worn();
        string before = fx.Game.PreferenceJson!;
        var (_, body) = await Trigger();
        await fx.WaitJob(body.GetProperty("job").GetString()!);
        var (_, putBack) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal("restored", putBack.GetProperty("autoRefit").GetProperty("preference").GetString());
        Assert.Equal(before, fx.Game.PreferenceJson);
        Assert.Equal(1, fx.Game.PreferenceRestores);
        Assert.Equal("Sarelle", putBack.GetProperty("autoRefit").GetProperty("jobBackedMods")[0].GetProperty("mod").GetString());

        // Again, and the player changes the preference after the job: put-back leaves it, saving nothing.
        (_, body) = await Trigger();
        await fx.WaitJob(body.GetProperty("job").GetString()!);
        fx.Game.PreferenceJson = "{\"BodyDir\":\"Neolithe\"}";
        (_, putBack) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal("changed", putBack.GetProperty("autoRefit").GetProperty("preference").GetString());
        Assert.Equal("{\"BodyDir\":\"Neolithe\"}", fx.Game.PreferenceJson);
        Assert.Equal(1, fx.Game.PreferenceRestores);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-39")]
    public async Task PutBack_AutoRefitBusy_PreferenceSkippedBusy_RestCarriesOn()
    {
        Worn();
        var (_, body) = await Trigger();
        await fx.WaitJob(body.GetProperty("job").GetString()!);
        fx.Game.Activity = new Proteus.Services.AutoRefitWatcher.ActivitySnapshot(true, 0, false, false);
        var (status, putBack) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal(200, status);
        Assert.Equal("skipped-busy", putBack.GetProperty("autoRefit").GetProperty("preference").GetString());
        Assert.True(fx.Changes.AutoRefitPreference.Recorded);
        fx.Game.Activity = default;
        (_, putBack) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal("restored", putBack.GetProperty("autoRefit").GetProperty("preference").GetString());
    }

    [Fact]
    [Trait("Plan", "AC-split-waist-seam-10")]
    [Trait("Plan", "EDGE-split-waist-seam-40")]
    public async Task RestoreByJob_FilesOnly_ResultReportsTargets()
    {
        string root = Worn();
        var (_, body) = await Trigger();
        string id = body.GetProperty("job").GetString()!;
        var job = await fx.WaitJob(id);
        // [EDGE-split-waist-seam-40] The piece's targets and seam come from its record entry.
        var piece = job.GetProperty("result").GetProperty("pieces")[0];
        Assert.Equal("Tre S", piece.GetProperty("option").GetString());
        Assert.Equal("Tre|Tre|top size/tre s.mdl", piece.GetProperty("seam").GetString());
        var top = piece.GetProperty("targets").GetProperty("_top");
        Assert.Equal("Tre", top.GetProperty("dir").GetString());
        Assert.Equal("files/common/3/c0201e0000_top.mdl", top.GetProperty("rel").GetString());
        var (status, restore) = await fx.Post("/actions/restore", $"{{\"job\":\"{id}\"}}");
        Assert.Equal(202, status);
        await fx.WaitJob(restore.GetProperty("job").GetString()!);
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, "chara", "model", "top.mdl")));
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-36")]
    public async Task PutBack_CollectionSetting_RestoredOrLeftWhenChanged()
    {
        Worn();
        var (_, body) = await Trigger();
        await fx.WaitJob(body.GetProperty("job").GetString()!);
        Assert.Equal(7, fx.Game.Permanent["Sarelle"].Priority);        // the refit switched the setting
        var (_, putBack) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal(0, fx.Game.Permanent["Sarelle"].Priority);
        Assert.Contains(putBack.GetProperty("binding").GetProperty("permanent").EnumerateArray(),
                        e => e.GetProperty("mod").GetString() == "Sarelle" && e.GetProperty("result").GetString() == "restored");

        // Again, and the player changes the setting after the job: left as they set it.
        (_, body) = await Trigger();
        await fx.WaitJob(body.GetProperty("job").GetString()!);
        fx.Game.Permanent["Sarelle"] = new PermanentSetting(false, 9);
        await fx.Post("/actions/put-back", "{}");
        Assert.Equal(new PermanentSetting(false, 9).Priority, fx.Game.Permanent["Sarelle"].Priority);
        Assert.False(fx.Game.Permanent["Sarelle"].Enabled);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-36")]
    public async Task BusyAtTheJobsRecheck_FailsNoBackup()
    {
        string root = Worn();
        fx.Game.BusyAfterRequest = true;
        var (status, body) = await Trigger();
        Assert.Equal(202, status);
        var job = await fx.WaitJob(body.GetProperty("job").GetString()!);
        Assert.Equal("auto-refit-busy", Code(job));
        Assert.Null(fx.Jobs.BackupOfJob(job.GetProperty("id").GetString()!));
        Assert.Empty(fx.Game.RunNowAsked);
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, "chara", "model", "top.mdl")));
        Assert.False(fx.Changes.AutoRefitPreference.Recorded);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-36")]
    public async Task BackupFails_RecordDropped()
    {
        using var small = new HarnessFixture(HarnessFixture.FastTimings, refitCap: 1);
        small.AddMod("Sarelle");
        small.Game.WornFrom.Add("Sarelle");
        small.Game.Permanent["Sarelle"] = new PermanentSetting(true, 0);
        var (_, body) = await small.Post("/actions/auto-refit", "{\"refitWornNow\":true,\"mod\":\"Sarelle\"}");
        var job = await small.WaitJob(body.GetProperty("job").GetString()!);
        Assert.NotEqual(JsonValueKind.Null, job.GetProperty("error").ValueKind);
        Assert.Empty(small.Game.RunNowAsked);
        Assert.False(small.Changes.AutoRefitPreference.Recorded);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-36")]
    public async Task SecondJobFailing_LeavesTheFirstJobsRecord()
    {
        // The review's F1: a second job of the session that fails before writing must not wipe the first job's record.
        Worn();
        string before = fx.Game.PreferenceJson!;
        var (_, body) = await Trigger();
        await fx.WaitJob(body.GetProperty("job").GetString()!);
        fx.Game.RunNowBody = _ => [("_top", FakeGame.RefitTopPath, "not-enqueued:just-refitted")];
        (_, body) = await Trigger();
        var second = await fx.WaitJob(body.GetProperty("job").GetString()!);
        Assert.Equal("nothing-enqueued", Code(second));
        Assert.True(fx.Changes.AutoRefitPreference.Recorded);
        var (_, putBack) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal("restored", putBack.GetProperty("autoRefit").GetProperty("preference").GetString());
        Assert.Equal(before, fx.Game.PreferenceJson);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-36")]
    [Trait("Plan", "EDGE-split-waist-seam-38")]
    public async Task PutBack_WhileTheJobRuns_SkippedThenRestored()
    {
        Worn();
        string before = fx.Game.PreferenceJson!;
        var busy = new Proteus.Services.AutoRefitWatcher.ActivitySnapshot(true, 1, false, false);
        fx.Game.RunNowBody = root =>
        {
            File.WriteAllText(Path.Combine(root, "chara", "model", "top.mdl"), "refitted");
            fx.Game.PreferenceJson = "{\"BodyDir\":\"Rue\",\"Before\":1}";
            fx.Game.Activity = busy;
            return [("_top", FakeGame.RefitTopPath, "enqueued")];
        };
        var (_, body) = await Trigger();
        string id = body.GetProperty("job").GetString()!;
        await Asked();
        var (_, putBack) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal("skipped-running", putBack.GetProperty("autoRefit").GetProperty("preference").GetString());
        fx.Game.Activity = default;
        await fx.WaitJob(id);
        (_, putBack) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal("restored", putBack.GetProperty("autoRefit").GetProperty("preference").GetString());
        Assert.Equal(before, fx.Game.PreferenceJson);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-36")]
    public async Task PutBack_OffOrUnload_LeavesRecordLogged()
    {
        // Off or unload mid-job cancels it: what the refits leave is not known, so the Before is kept on disk and put-back
        // says so rather than deleting it.
        Worn();
        fx.Game.RunNowBody = root =>
        {
            fx.Game.Activity = new Proteus.Services.AutoRefitWatcher.ActivitySnapshot(true, 1, false, false);
            return [("_top", FakeGame.RefitTopPath, "enqueued")];
        };
        var (_, body) = await Trigger();
        string id = body.GetProperty("job").GetString()!;
        await Asked();
        fx.Jobs.CancelRunning();
        var job = await fx.WaitJob(id);
        Assert.Equal("cancelled", Code(job));
        fx.Game.Activity = default;
        var record = Directory.GetFiles(Path.Combine(fx.Temp.Path, "config"), "autorefit-preference.json", SearchOption.AllDirectories);
        Assert.Single(record);
        var (_, putBack) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal("recorded-before-finish", putBack.GetProperty("autoRefit").GetProperty("preference").GetString());
        Assert.True(File.Exists(record[0]), "the Before is kept on disk");
        Assert.Equal(0, fx.Game.PreferenceRestores);
    }

    /// <summary>Until the job has asked for the refit, at most 5 s: a job that died early fails the test, not hangs it.</summary>
    private async Task Asked()
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            lock (fx.Game.RunNowAsked) if (fx.Game.RunNowAsked.Count > 0) return;
            if (DateTime.UtcNow > until) throw new TimeoutException("the job never asked for the refit");
            await Task.Delay(10);
        }
    }
}
