using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Proteus.TestHarness;

/// <summary>
/// The restore action. Selection (off the framework thread, before the request's framework hop):
/// <list type="bullet">
/// <item>by refit job id, through this session's map;</item>
/// <item>by explicit backup id, from any session (an incomplete folder is refused before anything is written);</item>
/// <item>by mod alone: this session's oldest complete refit backup, by seq. <c>olderSessionBackups</c> lists every
/// complete backup of the mod from other sessions: the chosen one is the original only when that list is empty.</item>
/// </list>
/// Then, on a worker: refuse a legacy folder; snapshot the mod as it is now (a pre-restore backup, no size cap); make
/// the mod byte-identical to the chosen backup's files. Then the after-change (framework, never abandoned) runs
/// whenever the copy/delete phase started, even when it failed; and the redraw is followed. A failure partway is
/// <c>restore-failed</c> with the snapshot named, and a retry simply runs again. Nothing is lost: the snapshot holds
/// whatever the restore replaced or deleted.
/// </summary>
internal static class RestoreJob
{
    /// <summary>What to restore from, and the lists the job reports.</summary>
    internal sealed record Selection(BackupInfo Backup, IReadOnlyList<string> SkippedNewer, IReadOnlyList<string> OlderSessionBackups);

    /// <summary>The outcome of selecting: a selection, or the error response.</summary>
    internal readonly record struct Selected(Selection? Selection, HarnessResponse? Error);

    /// <param name="jobId">A refit job id of this session, or null.</param>
    /// <param name="modDir">The mod folder, or null (with a job id).</param>
    /// <param name="backupId">An explicit backup id, or null.</param>
    internal static Selected Select(HarnessJobs jobs, string? jobId, string? modDir, string? backupId)
    {
        if (jobId != null)
        {
            if (jobs.BackupOfJob(jobId) is not { } fromJob)
                return new Selected(null, HarnessResponse.Error(404, "no-backup", $"no backup of this session belongs to job {jobId}"));
            if (jobs.Backups.Find(fromJob.ModDir, fromJob.Id).Backup is not { } stillThere)
                return new Selected(null, HarnessResponse.Error(404, "no-backup", $"backup {fromJob.Id} is no longer on disk"));
            return new Selected(new Selection(stillThere, [], OlderSessions(jobs, stillThere.ModDir, stillThere.Id)), null);
        }
        if (modDir == null)
            return new Selected(null, HarnessResponse.Error(400, "invalid-field", "give job, or mod (with an optional backup)"));
        if (!ModBackup.SafeModDir(modDir))
            return new Selected(null, HarnessResponse.Error(400, "invalid-field", "mod is not a plain folder name"));

        if (backupId != null)
        {
            var (backup, incomplete) = jobs.Backups.Find(modDir, backupId);
            if (incomplete)
                return new Selected(null, HarnessResponse.Error(409, "backup-incomplete", $"backup {backupId} was never completed; it is never restored"));
            if (backup == null)
                return new Selected(null, HarnessResponse.Error(404, "no-backup", $"no backup {backupId} of {modDir}"));
            return new Selected(new Selection(backup, [], OlderSessions(jobs, modDir, backup.Id)), null);
        }

        var mine = ModBackup.InSessionOrder(jobs.Backups.List(modDir)
                                                .Where(b => b.Meta.Session == jobs.Session && b.Meta.Kind == BackupKind.Refit));
        if (mine.Count == 0)
            return new Selected(null, HarnessResponse.Error(404, "no-backup",
                $"no refit backup of {modDir} from this harness session; restore an earlier one by its backup id"));
        return new Selected(new Selection(mine[0], mine.Skip(1).Select(b => b.Id).ToList(), OlderSessions(jobs, modDir)), null);
    }

    /// <summary>Every complete backup of the mod from other sessions (but the chosen one), by creation time then seq.</summary>
    private static IReadOnlyList<string> OlderSessions(HarnessJobs jobs, string modDir, string? exceptId = null)
        => ModBackup.AcrossSessionsOrder(jobs.Backups.List(modDir)
                                             .Where(b => b.Meta.Session != jobs.Session && b.Id != exceptId))
                    .Select(b => b.Id).ToList();

    internal static async Task Run(HarnessJobs jobs, HarnessJob job, Selection selection, string modRoot)
    {
        var game = jobs.Game;
        var ct = job.Cancel.Token;
        var from = selection.Backup;

        if (game.IsLegacyFolder(modRoot))
        {
            job.Fail(new JobError("legacy-mod", "the mod folder is an old (v3) Penumbra mod; it is never written"));
            return;
        }

        job.Progress("snapshotting the mod as it is now");
        BackupInfo snapshot;
        try
        {
            snapshot = jobs.Backups.Create(modRoot, job.ModDir, BackupKind.PreRestore, jobs.Session, jobs.NextSeq(), job.Id, ct);
        }
        catch (BackupException e)
        {
            job.Fail(new JobError(e.Code, e.Message));
            return;
        }
        var snapRef = new BackupRef(snapshot.Id, snapshot.Path);
        job.SetBackup(snapRef);
        bool? changed = ModBackup.ChangedSince(from, modRoot);

        job.Progress("restoring");
        int files = 0, deleted = 0;
        try
        {
            (files, deleted) = ModBackup.Restore(from, modRoot, ct);
        }
        catch (Exception e)
        {
            job.Fail(new JobError(e is OperationCanceledException ? "cancelled" : "restore-failed",
                                  $"the restore stopped partway ({e.GetType().Name}: {e.Message}); the mod as it was is in the snapshot, and a retry runs again",
                                  snapRef));
        }
        job.SetRestored(new RestoredInfo(new BackupRef(from.Id, from.Path), snapRef, files, deleted, changed,
                                         selection.SkippedNewer, selection.OlderSessionBackups));

        // The copy/delete phase started, so Penumbra must be told, success or not.
        jobs.PostAfterChange(job, () => job.SetAfterChange(game.AfterModChange(job.ModDir)));
        await jobs.AwaitAfterChange(job).ConfigureAwait(false);

        job.Progress("watching the redraw");
        await jobs.Follow(job).ConfigureAwait(false);
        job.Progress("");
    }
}
