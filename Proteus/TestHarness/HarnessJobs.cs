using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Proteus.TestHarness;

/// <summary>A job's error: its code, what happened, and the pre-restore snapshot when one was taken.</summary>
internal sealed record JobError(string Code, string Message, BackupRef? PreRestore = null);

/// <summary>A backup as job responses name it.</summary>
internal sealed record BackupRef(string Id, string Path);

/// <summary>What a restore did. Files and Deleted are null when it failed partway.</summary>
internal sealed record RestoredInfo(BackupRef From, BackupRef PreRestore, int? Files, int? Deleted, bool? ChangedSinceJob,
                                    IReadOnlyList<string> SkippedNewer, IReadOnlyList<string> OlderSessionBackups);

/// <summary>A job as <c>/jobs/{id}</c> reports it.</summary>
internal sealed record JobView(string Id, string Kind, string ModDir, string State, string Progress, DateTime StartedAt,
                               DateTime? EndedAt, BackupRef? Backup, bool? Reloaded, bool? RedrawRequested,
                               bool? RedrawObserved, bool? AutoRefitFollowed, object? Result, RestoredInfo? Restored,
                               JobError? Error);

/// <summary>One refit or restore. Mutated by its runner under its own lock; read through <see cref="View"/>.</summary>
internal sealed class HarnessJob(string id, string kind, string modDir)
{
    private readonly object gate = new();
    public string Id { get; } = id;
    public string Kind { get; } = kind;
    public string ModDir { get; private set; } = modDir;
    public CancellationTokenSource Cancel { get; } = new();

    private string state = "queued", progress = "";
    private readonly DateTime started = DateTime.UtcNow;
    private DateTime? ended;
    private BackupRef? backup;
    private bool? reloaded, redrawRequested, redrawObserved, autoRefitFollowed;
    private object? result;
    private RestoredInfo? restored;
    private JobError? error;

    /// <summary>The after-change the runner waits on before it finishes; set by the framework post.</summary>
    public Task<bool>? AfterChangeTask { get; set; }

    /// <summary>The redraw counter as the after-change saw it before asking for a redraw.</summary>
    public long? Baseline { get; private set; }

    public void Set(Action<HarnessJob> change) { lock (gate) change(this); }
    public void Running() => Set(j => j.state = "running");
    public void Progress(string p) => Set(j => j.progress = p);
    public void SetBackup(BackupRef b) => Set(j => j.backup = b);
    public void SetResult(object r) => Set(j => j.result = r);
    public void SetRestored(RestoredInfo r) => Set(j => j.restored = r);
    public void SetFollow(bool observed, bool? followed) => Set(j => { j.redrawObserved = observed; j.autoRefitFollowed = followed; });
    public void SetAfterChange(AfterChangeResult r)
        => Set(j => { j.reloaded = r.Reloaded; j.redrawRequested = r.RedrawRequested; j.Baseline = r.Baseline; });
    /// <summary>What Penumbra answered to the after-change's reload; null before it ran.</summary>
    public bool? Reloaded { get { lock (gate) return reloaded; } }

    /// <summary>The first error sticks: a later after-change skip does not hide why the run failed.</summary>
    public void Fail(JobError e) => Set(j => j.error ??= e);
    public bool Failed { get { lock (gate) return error != null; } }

    public void Finish()
        => Set(j =>
        {
            j.ended = DateTime.UtcNow;
            j.state = j.error == null ? "done" : j.error.Code == "cancelled" ? "cancelled" : "failed";
        });

    public bool Ended { get { lock (gate) return ended != null; } }

    public JobView View()
    {
        lock (gate)
            return new JobView(Id, Kind, ModDir, state, progress, started, ended, backup, reloaded, redrawRequested,
                               redrawObserved, autoRefitFollowed, result, restored, error);
    }
}

/// <summary>How long the harness waits for things; constructor parameters so tests run fast.</summary>
internal sealed record HarnessTimings(TimeSpan FrameworkStart, TimeSpan RedrawWait, TimeSpan FollowWindow,
                                      TimeSpan FollowSample, TimeSpan Capture)
{
    public static readonly HarnessTimings Default = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10),
                                                        TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500),
                                                        TimeSpan.FromSeconds(10));
}

/// <summary>
/// The harness's jobs: one at a time (the slot is reserved off the framework thread before any dispatch, released
/// when the job ends or its dispatch is abandoned), the current job and the last finished one, and this session's map
/// from refit job to the backup it took (which outlives the registry, so a restore by job id keeps working).
/// </summary>
internal sealed class HarnessJobs(IHarnessGame game, IFrameworkDispatcher dispatcher, ModBackup backups, string session,
                                  Func<bool> disposing, HarnessTimings timings)
{
    private readonly object gate = new();
    private int slot;
    private long seq;
    private HarnessJob? current, lastFinished;
    private readonly Dictionary<string, BackupInfo> sessionBackups = new(StringComparer.Ordinal);

    public IHarnessGame Game { get; } = game;
    public IFrameworkDispatcher Dispatcher { get; } = dispatcher;
    public ModBackup Backups { get; } = backups;
    public string Session { get; } = session;
    public Func<bool> Disposing { get; } = disposing;
    public HarnessTimings Timings { get; } = timings;

    /// <summary>Where a job says what it lost (a report it could not build); none in tests that don't care.</summary>
    public Dalamud.Plugin.Services.IPluginLog? Log { get; init; }

    public bool TryReserve() => Interlocked.CompareExchange(ref slot, 1, 0) == 0;
    public void Release() => Volatile.Write(ref slot, 0);

    public long NextSeq() => Interlocked.Increment(ref seq);

    public static string NewJobId() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    public HarnessJob? Current { get { lock (gate) return current; } }

    public HarnessJob? Find(string id)
    {
        lock (gate)
            return current?.Id == id ? current : lastFinished?.Id == id ? lastFinished : null;
    }

    public void RememberBackup(string jobId, BackupInfo backup) { lock (gate) sessionBackups[jobId] = backup; }
    public BackupInfo? BackupOfJob(string jobId) { lock (gate) return sessionBackups.GetValueOrDefault(jobId); }

    /// <summary>Run a job whose slot is reserved. The slot is released and the job finished however the body ends.</summary>
    public Task Start(HarnessJob job, Func<HarnessJob, Task> body)
    {
        lock (gate) current = job;
        return Task.Run(async () =>
        {
            try
            {
                job.Running();
                await body(job).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                job.Fail(new JobError("cancelled", "the job was cancelled"));
            }
            catch (Exception e)
            {
                job.Fail(new JobError("internal", $"{e.GetType().Name}: {e.Message}"));
            }
            finally
            {
                job.Finish();
                lock (gate)
                {
                    lastFinished = job;
                    if (ReferenceEquals(current, job)) current = null;
                }
                Release();
            }
        });
    }

    /// <summary>Cancel the running job (<c>harness off</c>, dispose).</summary>
    public void CancelRunning()
    {
        HarnessJob? job;
        lock (gate) job = current;
        try { job?.Cancel.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Post the after-change for a job, never abandoned, and keep the task so the job waits on it. Skipped (false) once
    /// the harness is disposing: the bridges it calls are about to go.
    /// </summary>
    public void PostAfterChange(HarnessJob job, Action afterChange)
        => job.AfterChangeTask = Dispatcher.RunUnabandonable(afterChange, Disposing);

    /// <summary>
    /// Wait for the job's after-change. False, or a cancelled or faulted task (game shutdown), is recorded as
    /// <c>after-change-skipped</c> — observed, never left to surface unobserved.
    /// </summary>
    public async Task AwaitAfterChange(HarnessJob job)
    {
        if (job.AfterChangeTask is not { } task) return;
        bool ran;
        try { ran = await task.ConfigureAwait(false); }
        catch (Exception) { ran = false; }
        if (!ran) job.Fail(new JobError("after-change-skipped", "the reload and redraw were skipped (the harness was stopping)"));
        // The files are as the job says, but Penumbra did not take them: the game is not drawing what is on disk.
        else if (job.Reloaded == false)
            job.Fail(new JobError("reload-failed",
                "Penumbra refused to reload the mod (see /log); the files on disk are as reported, but the game may not match them until Penumbra reloads its mods"));
    }

    /// <summary>
    /// After the after-change: wait for the redraw it asked for (the counter passing the baseline it read, so a redraw
    /// that landed before this started waiting still counts), then watch whether auto refit wakes up behind it.
    /// </summary>
    public async Task Follow(HarnessJob job)
    {
        if (job.Baseline is not { } baseline)
        {
            job.SetFollow(false, null);
            return;
        }
        var deadline = DateTime.UtcNow + Timings.RedrawWait;
        while (Game.RedrawCount <= baseline && DateTime.UtcNow < deadline && !Disposing())
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50, Timings.FollowSample.TotalMilliseconds))).ConfigureAwait(false);
        if (Game.RedrawCount <= baseline)
        {
            job.SetFollow(false, null);
            return;
        }
        bool followed = false;
        var until = DateTime.UtcNow + Timings.FollowWindow;
        do
        {
            // Real auto-refit work only: a walk being due is what every redraw schedules, refit or not.
            var a = Game.AutoRefit();
            if (a.View.Busy || a.Activity.Running || a.Activity.Queued > 0 || a.Activity.Undoing) followed = true;
            if (followed || Disposing()) break;
            await Task.Delay(Timings.FollowSample).ConfigureAwait(false);
        } while (DateTime.UtcNow < until);
        job.SetFollow(true, followed);
    }
}
