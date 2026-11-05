using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Proteus.Services;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>The refit and restore jobs, driven through the real server.</summary>
[Collection(HarnessTimingCollection.Name)]
public class HarnessJobsTests : IDisposable
{
    private HarnessFixture f = new();
    public void Dispose() => f.Dispose();

    private const string RefitRue = """{"mod":"Outfit","bodies":["rue"]}""";

    private async Task<JsonElement> Refit(string body = RefitRue)
    {
        var (status, b) = await f.Post("/actions/refit", body);
        Assert.Equal(202, status);
        return await f.WaitJob(b.GetProperty("job").GetString()!);
    }

    private async Task<JsonElement> Restore(string body)
    {
        var (status, b) = await f.Post("/actions/restore", body);
        Assert.True(status == 202, $"restore answered {status}: {b}");
        return await f.WaitJob(b.GetProperty("job").GetString()!);
    }

    private static string Error(JsonElement job) => job.GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    public async Task Refit_HopSequence()
    {
        // [AC-qa-harness-8] Each step on its thread, in order; the job ends only after the after-change ran.
        f.AddMod();
        var job = await Refit();
        Assert.Equal("done", job.GetProperty("state").GetString());
        var order = f.Game.Order.Where(o => !o.StartsWith("Mods@")).ToList();
        Assert.Equal(["IsLegacy@worker", "OfferedKeys@worker", "InstalledKeys@worker", "PrepareSizeSet@framework",
                      "RunSizeSet@worker", "AfterModChange@framework"], order);
        Assert.Contains("Mods@framework", f.Game.Order);
        Assert.True(job.GetProperty("reloaded").GetBoolean());
        Assert.Single(f.Backups.List("Outfit"));
    }

    [Fact]
    public async Task Refit_AfterChange_CallsNoteModelsChanged()
    {
        // [AC-qa-harness-8] [EDGE-qa-harness-51] An open Studio is told its model list changed.
        f.AddMod();
        await Refit();
        Assert.Equal(["Outfit"], f.Game.StudioRefreshed);
    }

    [Fact]
    public async Task Restore_HopSequence()
    {
        // [AC-qa-harness-9] Legacy check on the worker, the after-change on the framework thread.
        f.AddMod();
        var refit = await Refit();
        while (f.Game.Order.TryDequeue(out _)) { }
        await Restore($$"""{"job":"{{refit.GetProperty("id").GetString()}}"}""");
        Assert.Equal(["Mods@framework", "IsLegacy@worker", "AfterModChange@framework"], f.Game.Order.ToList());
    }

    [Fact]
    public async Task Restore_SnapshotFirst_ThenByteIdenticalWithMtimes()
    {
        // [AC-qa-harness-9] The mod as it was before the restore is kept; the mod ends as the backup, mtimes included.
        string mod = f.AddMod();
        var original = FolderState.Of(mod);
        await Refit();
        var refitted = FolderState.Of(mod);
        var job = await Restore("""{"mod":"Outfit"}""");
        FolderState.AssertSame(original, mod);
        var snap = f.Backups.List("Outfit").Single(b => b.Meta.Kind == BackupKind.PreRestore);
        FolderState.AssertSame(refitted, snap.FilesPath);
        Assert.Equal(snap.Id, job.GetProperty("restored").GetProperty("preRestore").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Restore_PartialFailure_RestoreFailedWithSnapshot_RetrySucceeds()
    {
        // [AC-qa-harness-9] [EDGE-qa-harness-48] A copy that fails partway: restore-failed, the snapshot named, the
        // after-change still run; a retry succeeds.
        string mod = f.AddMod();
        var original = FolderState.Of(mod);
        await Refit();
        var backup = f.Backups.List("Outfit").Single();
        JsonElement failed;
        using (new FileStream(Path.Combine(backup.FilesPath, "chara", "model", "top.mdl"), FileMode.Open, FileAccess.Read, FileShare.None))
            failed = await Restore("""{"mod":"Outfit"}""");
        Assert.Equal("failed", failed.GetProperty("state").GetString());
        Assert.Equal("restore-failed", Error(failed));
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("restored").GetProperty("files").ValueKind);
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("restored").GetProperty("deleted").ValueKind);
        Assert.Equal(JsonValueKind.String, failed.GetProperty("error").GetProperty("preRestore").GetProperty("id").ValueKind);
        Assert.True(failed.GetProperty("reloaded").GetBoolean());
        var retry = await Restore("""{"mod":"Outfit"}""");
        Assert.Equal("done", retry.GetProperty("state").GetString());
        FolderState.AssertSame(original, mod);
    }

    [Fact]
    public async Task FourRefits_RestoreEachNewestFirst_EndsAtOriginal_NoBackupDeleted()
    {
        // [AC-qa-harness-9] [EDGE-qa-harness-50] No backup is ever deleted: four refits, four restores newest first,
        // and the mod is the original again.
        string mod = f.AddMod();
        var original = FolderState.Of(mod);
        var ids = new string[4];
        for (int i = 0; i < 4; i++) ids[i] = (await Refit()).GetProperty("id").GetString()!;
        for (int i = 3; i >= 0; i--)
            Assert.Equal("done", (await Restore($$"""{"job":"{{ids[i]}}"}""")).GetProperty("state").GetString());
        FolderState.AssertSame(original, mod);
        Assert.Equal(8, f.Backups.List("Outfit").Count);
    }

    [Fact]
    public async Task Refit_StallAtPrepareHop_FrameworkTimeout_NothingWritten()
    {
        // [EDGE-qa-harness-21] The prepare hop times out: framework-timeout, no backup, nothing written.
        string mod = f.AddMod();
        var before = FolderState.Of(mod);
        f.Game.AfterInstalledKeys = f.Dispatcher.Stall;
        var job = await Refit();
        f.Dispatcher.Resume();
        Assert.Equal("framework-timeout", Error(job));
        Assert.Empty(f.Backups.List("Outfit"));
        FolderState.AssertSame(before, mod);
    }

    [Fact]
    public async Task Refit_StallAtAfterChangeOver10s_ReloadsAndRedrawsOnResume()
    {
        // [EDGE-qa-harness-21] The after-change is never abandoned: through a stall far past the dispatch timeout the
        // job stays running and holds the slot; on resume it reloads and its redraw is observed.
        f.AddMod();
        f.Game.RunBody = (_, _, _) => { f.Dispatcher.Stall(); return 1; };
        var (_, started) = await f.Post("/actions/refit", RefitRue);
        string id = started.GetProperty("job").GetString()!;
        await Task.Delay(f.Jobs.Timings.FrameworkStart * 3);
        Assert.Equal("running", (await f.Get($"/jobs/{id}")).Body.GetProperty("state").GetString());
        var (busy, b) = await f.Post("/actions/refit", RefitRue);
        Assert.Equal(409, busy);
        Assert.Equal("busy", b.GetProperty("error").GetProperty("code").GetString());
        f.Dispatcher.Resume();
        var job = await f.WaitJob(id);
        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.True(job.GetProperty("reloaded").GetBoolean());
        Assert.True(job.GetProperty("redrawObserved").GetBoolean());
    }

    [Fact]
    public async Task Restore_StallAtAfterChangeOver10s_ReloadsAndRedrawsOnResume()
    {
        // [EDGE-qa-harness-21] The same for a restore.
        f.AddMod();
        await Refit();
        f.Game.OnLegacyCheck = f.Dispatcher.Stall;
        var (_, started) = await f.Post("/actions/restore", """{"mod":"Outfit"}""");
        string id = started.GetProperty("job").GetString()!;
        await Task.Delay(f.Jobs.Timings.FrameworkStart * 3);
        Assert.Equal("running", (await f.Get($"/jobs/{id}")).Body.GetProperty("state").GetString());
        Assert.Equal(409, (await f.Post("/actions/restore", """{"mod":"Outfit"}""")).Status);
        f.Game.OnLegacyCheck = null;
        f.Dispatcher.Resume();
        var job = await f.WaitJob(id);
        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.True(job.GetProperty("reloaded").GetBoolean());
        Assert.True(job.GetProperty("redrawObserved").GetBoolean());
    }

    [Fact]
    public async Task AfterChangeTaskCancelledOrFaulted_AfterChangeSkipped_NoUnobservedException()
    {
        // [EDGE-qa-harness-21] A framework shut down under the after-change: after-change-skipped, observed.
        f.AddMod();
        bool unobserved = false;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, _) => unobserved = true;
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            f.Dispatcher.FaultNextUnabandonable = true;
            var job = await Refit();
            Assert.Equal("after-change-skipped", Error(job));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.False(unobserved);
        }
        finally { TaskScheduler.UnobservedTaskException -= handler; }
    }

    [Fact]
    public async Task Refit_NotOfferedNotInstalled_Skipped_NoneLeft_DoneMadeZero_NoBackup()
    {
        // [EDGE-qa-harness-34]
        f.AddMod();
        f.Game.Offered = [("bibo", "bibo")];
        f.Game.Installed = [];
        var job = await Refit("""{"mod":"Outfit","bodies":["rue","bibo"]}""");
        Assert.Equal("done", job.GetProperty("state").GetString());
        var result = job.GetProperty("result");
        Assert.Equal(0, result.GetProperty("made").GetInt32());
        var skipped = result.GetProperty("skippedBodies").EnumerateArray()
                            .Select(s => (s.GetProperty("key").GetString(), s.GetProperty("reason").GetString())).ToList();
        Assert.Equal([("rue", "not-offered"), ("bibo", "not-installed")], skipped);
        Assert.Empty(f.Backups.List("Outfit"));
    }

    [Fact]
    public async Task Refit_AutoRefitNotQuiet_409()
    {
        // [EDGE-qa-harness-36]
        f.AddMod();
        f.Game.Activity = new AutoRefitWatcher.ActivitySnapshot(false, 1, false, false);
        var (status, b) = await f.Post("/actions/refit", RefitRue);
        Assert.Equal(409, status);
        Assert.Equal("auto-refit-busy", b.GetProperty("error").GetProperty("code").GetString());
        Assert.True(f.Jobs.TryReserve());
        f.Jobs.Release();
    }

    [Fact]
    public async Task Restore_AutoRefitNotQuiet_409()
    {
        // [EDGE-qa-harness-36]
        f.AddMod();
        await Refit();
        f.Game.View = new AutoRefitWatcher.View(Busy: true);
        Assert.Equal(409, (await f.Post("/actions/restore", """{"mod":"Outfit"}""")).Status);
    }

    [Fact]
    public async Task RedrawBeforeNextRedrawCalled_StillObserved()
    {
        // [EDGE-qa-harness-36] The redraw landed before the job started waiting: the baseline still sees it.
        f.AddMod();
        f.Game.RedrawArrives = false;
        f.Game.RedrawBeforeReturn = true;
        Assert.True((await Refit()).GetProperty("redrawObserved").GetBoolean());
    }

    [Fact]
    public async Task FollowWindow_RedrawThenBusy_True()
    {
        // [EDGE-qa-harness-36] Auto refit woke up behind the harness's redraw.
        f.AddMod();
        f.Game.OnAfterChange = () => _ = Task.Delay(50).ContinueWith(_ => f.Game.Activity = new AutoRefitWatcher.ActivitySnapshot(true, 0, false, false));
        var job = await Refit();
        Assert.True(job.GetProperty("autoRefitFollowed").GetBoolean());
    }

    [Fact]
    public async Task ReloadRefused_ReloadFailed_NotDone()
    {
        // [EDGE-qa-harness-48] In-game QA: Penumbra threw on the reload after a restore took options away, and the job
        // still said done. A refused reload is now the job's error.
        string mod = f.AddMod();
        var original = FolderState.Of(mod);
        string refit = (await Refit()).GetProperty("id").GetString()!;
        f.Game.ReloadSucceeds = false;
        var job = await Restore($$"""{"job":"{{refit}}"}""");
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("reload-failed", Error(job));
        Assert.False(job.GetProperty("reloaded").GetBoolean());
        FolderState.AssertSame(original, mod);   // the files are still restored
    }

    [Fact]
    public async Task FollowWindow_WalkDueOnly_False()
    {
        // [EDGE-qa-harness-36] In-game QA: every redraw schedules a walk, so a due walk alone is not "auto refit followed".
        f.AddMod();
        f.Game.OnAfterChange = () => f.Game.Activity = new AutoRefitWatcher.ActivitySnapshot(false, 0, true, false);
        var job = await Refit();
        Assert.False(job.GetProperty("autoRefitFollowed").GetBoolean());
    }

    [Fact]
    public async Task EndedJob_SlotFreeAndNotCurrent()
    {
        // [EDGE-qa-harness-35] A job shows ended only once its slot is free and it is no longer current: a client
        // that sees endedAt and sends the next job at once is never refused busy (seen under the loaded quick suite).
        // Checked at the very moment the job shows ended, so the old ordering's gap is hit every time.
        Assert.True(f.Jobs.TryReserve());
        var job = new HarnessJob(HarnessJobs.NewJobId(), "refit", "Outfit");
        bool? current = null, slotFree = null;
        job.Finished = _ =>
        {
            current = f.Jobs.Current != null;
            slotFree = f.Jobs.TryReserve();
            if (slotFree == true) f.Jobs.Release();
        };
        await f.Jobs.Start(job, _ => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(current, "the job showed ended while still current");
        Assert.True(slotFree, "the job showed ended while its slot was still taken");
    }

    [Fact]
    public async Task FollowWindow_RedrawQuiet_False()
    {
        // [EDGE-qa-harness-36]
        f.AddMod();
        Assert.False((await Refit()).GetProperty("autoRefitFollowed").GetBoolean());
    }

    [Fact]
    public async Task FollowWindow_NoRedraw_Null()
    {
        // [EDGE-qa-harness-36] No redraw came: not observed, and nothing to say about auto refit.
        f.AddMod();
        f.Game.RedrawArrives = false;
        var job = await Refit();
        Assert.False(job.GetProperty("redrawObserved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("autoRefitFollowed").ValueKind);
    }

    [Fact]
    public async Task Refit_TooLarge_JobError_RunNeverCalled()
    {
        // [EDGE-qa-harness-38] A job error, not a request error; the run never starts.
        f.Dispose();
        f = new HarnessFixture(refitCap: 3);
        f.AddMod();
        bool ran = false;
        f.Game.RunBody = (_, _, _) => { ran = true; return 0; };
        var job = await Refit();
        Assert.Equal("backup-too-large", Error(job));
        Assert.False(ran);
    }

    [Fact]
    public async Task Refit_BackupFails_RunNeverCalled_ModUntouched()
    {
        // [EDGE-qa-harness-39]
        string mod = f.AddMod();
        var before = FolderState.Of(mod);
        bool ran = false;
        f.Game.RunBody = (_, _, _) => { ran = true; return 0; };
        JsonElement job;
        using (new FileStream(Path.Combine(mod, "meta.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            job = await Refit();
        Assert.Equal("backup-failed", Error(job));
        Assert.False(ran);
        FolderState.AssertSame(before, mod);
    }

    [Fact]
    public async Task Refit_RunThrows_Failed_AfterChange_BackupKept_AfterJsonWritten()
    {
        // [EDGE-qa-harness-41]
        f.AddMod();
        f.Game.RunBody = (request, _, _) =>
        {
            File.WriteAllText(Path.Combine(request.ModRoot, "half.mdl"), "half");
            throw new InvalidOperationException("boom");
        };
        var job = await Refit();
        Assert.Equal("run-failed", Error(job));
        Assert.Equal(1, f.Game.AfterChanges);
        var backup = Assert.Single(f.Backups.List("Outfit"));
        Assert.True(File.Exists(Path.Combine(backup.Path, ModBackup.AfterFile)));
    }

    [Fact]
    public async Task Refit_CancelledMidRun_AfterChange()
    {
        // [EDGE-qa-harness-41] Cancelled mid-run: cancelled, and Penumbra still told.
        f.AddMod();
        f.Game.RunBody = (_, _, ct) => { f.Jobs.CancelRunning(); ct.ThrowIfCancellationRequested(); return 0; };
        var job = await Refit();
        Assert.Equal("cancelled", job.GetProperty("state").GetString());
        Assert.Equal(1, f.Game.AfterChanges);
    }

    [Fact]
    public async Task Refit_CancelBeforeWorker_Cancelled_NothingWritten()
    {
        // [EDGE-qa-harness-42] Cancelled before the run: nothing written, no backup, no reload.
        string mod = f.AddMod();
        var before = FolderState.Of(mod);
        f.Game.AfterInstalledKeys = f.Jobs.CancelRunning;
        var job = await Refit();
        Assert.Equal("cancelled", job.GetProperty("state").GetString());
        Assert.Empty(f.Backups.List("Outfit"));
        Assert.Equal(0, f.Game.AfterChanges);
        FolderState.AssertSame(before, mod);
    }

    [Fact]
    public async Task Refit_UnreadableMod_RunFailed_NotInternal()
    {
        // [EDGE-qa-harness-41] A mod's data that cannot be read is the run's failure (run-failed), not a harness bug.
        f.AddMod();
        f.Game.OfferedThrows = new InvalidDataException("manifest is not JSON");
        var job = await Refit();
        Assert.Equal("run-failed", Error(job));
        Assert.Empty(f.Backups.List("Outfit"));
    }

    [Fact]
    public async Task Refit_PrepareThrows_RunFailed_NothingWritten()
    {
        // [EDGE-qa-harness-41] The game's bodies or skeletons could not be read while preparing: run-failed, before any
        // backup or write.
        string mod = f.AddMod();
        var before = FolderState.Of(mod);
        f.Game.PrepareThrows = new InvalidOperationException("skeleton IPC failed");
        var job = await Refit();
        Assert.Equal("run-failed", Error(job));
        Assert.Empty(f.Backups.List("Outfit"));
        FolderState.AssertSame(before, mod);
    }

    [Fact]
    public async Task Refit_LegacyMod_Refused_NothingWritten_NoBackup()
    {
        // [EDGE-qa-harness-43]
        string mod = f.AddMod();
        var before = FolderState.Of(mod);
        f.Game.Legacy.Add("Outfit");
        var job = await Refit();
        Assert.Equal("legacy-mod", Error(job));
        Assert.Empty(f.Backups.List("Outfit"));
        FolderState.AssertSame(before, mod);
    }

    [Fact]
    public async Task Restore_OntoLegacyFolder_Refused_NoSnapshot()
    {
        // [EDGE-qa-harness-43]
        f.AddMod();
        await Refit();
        f.Game.Legacy.Add("Outfit");
        var job = await Restore("""{"mod":"Outfit"}""");
        Assert.Equal("legacy-mod", Error(job));
        Assert.DoesNotContain(f.Backups.List("Outfit"), b => b.Meta.Kind == BackupKind.PreRestore);
    }

    [Fact]
    public async Task Restore_ByRefitJobId_AfterRegistryDisplaced()
    {
        // [EDGE-qa-harness-45] The session map outlives the two-job registry.
        string mod = f.AddMod();
        var original = FolderState.Of(mod);
        string first = (await Refit()).GetProperty("id").GetString()!;
        await Refit();
        await Refit();
        Assert.Equal(404, (await f.Get($"/jobs/{first}")).Status);
        await Restore($$"""{"job":"{{first}}"}""");
        FolderState.AssertSame(original, mod);
    }

    [Fact]
    public async Task Restore_ByExplicitIdFromEarlierSession()
    {
        // [EDGE-qa-harness-45]
        string mod = f.AddMod();
        var original = FolderState.Of(mod);
        var earlier = f.Backups.Create(mod, "Outfit", BackupKind.Refit, "earlier", 1, null, CancellationToken.None);
        File.WriteAllText(Path.Combine(mod, "since.mdl"), "since");
        await Restore($$"""{"mod":"Outfit","backup":"{{earlier.Id}}"}""");
        FolderState.AssertSame(original, mod);
    }

    [Fact]
    public async Task Restore_ByModAlone_OldestSessionBackup_ListsSkippedNewer()
    {
        // [EDGE-qa-harness-45] Mod alone: this session's oldest (the original), the newer ones listed.
        string mod = f.AddMod();
        var original = FolderState.Of(mod);
        await Refit();
        string second = (await Refit()).GetProperty("backup").GetProperty("id").GetString()!;
        var job = await Restore("""{"mod":"Outfit"}""");
        FolderState.AssertSame(original, mod);
        Assert.Equal([second], job.GetProperty("restored").GetProperty("skippedNewer").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(0, job.GetProperty("restored").GetProperty("olderSessionBackups").GetArrayLength());
    }

    [Fact]
    public async Task Restore_ByModAlone_EarlierSessionBackupExists_ListedInOlderSessionBackups()
    {
        // [EDGE-qa-harness-45] The session's oldest is not the original when an earlier session backed the mod up.
        string mod = f.AddMod();
        var earlier = f.Backups.Create(mod, "Outfit", BackupKind.Refit, "earlier", 1, null, CancellationToken.None);
        await Refit();
        var job = await Restore("""{"mod":"Outfit"}""");
        Assert.Equal([earlier.Id], job.GetProperty("restored").GetProperty("olderSessionBackups").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Restore_ByModAlone_EarlierSessionBackupWithLaterCreatedUtc_StillListed()
    {
        // [EDGE-qa-harness-45] Every other-session backup is listed, whatever its timestamp (a clock step).
        string mod = f.AddMod();
        await Refit();
        var other = f.Backups.Create(mod, "Outfit", BackupKind.Refit, "earlier", 1, null, CancellationToken.None);
        string metaPath = Path.Combine(other.Path, ModBackup.MetaFile);
        var meta = JsonSerializer.Deserialize<BackupMeta>(File.ReadAllBytes(metaPath), HarnessJson.Options)!;
        File.WriteAllBytes(metaPath, HarnessJson.Bytes(meta with { CreatedUtc = DateTime.UtcNow.AddDays(1) }));
        var job = await Restore("""{"mod":"Outfit"}""");
        Assert.Equal([other.Id], job.GetProperty("restored").GetProperty("olderSessionBackups").EnumerateArray().Select(e => e.GetString()));
    }
}
