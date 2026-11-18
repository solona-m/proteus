using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>
/// The worn-refit trigger: "Refit what I'm wearing now" for the pieces worn from ONE mod, for QA, which cannot click
/// Settings (ADR 0002, 2026-11-18). The request's framework hop has already checked auto refit can run, the folder, that
/// it is worn, and recorded the mod's collection setting and the collection's auto refit preference. Then:
/// <list type="number">
/// <item>Framework: auto refit still quiet, or the job fails with nothing written.</item>
/// <item>Worker: back the whole mod up (restore by job puts its files back).</item>
/// <item>Framework: <see cref="AutoRefitWatcher.RunNowFor"/> enqueues that mod's worn pieces as forced — nothing of any
/// other mod, and the walk's "refit everything" never armed. Nothing enqueued: the job fails, nothing written.</item>
/// <item>Worker: wait for auto refit to go quiet, at most <see cref="HarnessTimings.AutoRefitNowWait"/>.</item>
/// <item>Framework: record what the refits left of the mod's setting and the preference, for put-back.</item>
/// </list>
/// </summary>
internal static class AutoRefitNowJob
{
    internal sealed record Args(string ModDir, string ModRoot, Guid Collection, PermanentSetting Before, HarnessChanges Changes);

    /// <summary>How often the wait looks at auto refit, and how many quiet looks in a row end it.</summary>
    private static readonly TimeSpan Sample = TimeSpan.FromMilliseconds(250);
    private const int QuietLooks = 3;

    internal static async Task Run(HarnessJobs jobs, HarnessJob job, Args args)
    {
        var game = jobs.Game;
        var ct = job.Cancel.Token;

        job.Progress("checking auto refit");
        bool busy;
        try
        {
            busy = await jobs.Dispatcher.RunAsync(() => game.AutoRefit().Busy, jobs.Timings.FrameworkStart, ct).ConfigureAwait(false);
        }
        catch (FrameworkTimeoutException)
        {
            args.Changes.AutoRefitPreference.Drop();
            job.Fail(new JobError("framework-timeout", "the game's framework did not answer in time; nothing was written"));
            return;
        }
        if (busy)
        {
            args.Changes.AutoRefitPreference.Drop();
            job.Fail(new JobError("auto-refit-busy", "auto refit started working; nothing was written"));
            return;
        }

        job.Progress("backing up the mod");
        BackupInfo backup;
        try
        {
            backup = jobs.Backups.Create(args.ModRoot, args.ModDir, BackupKind.Refit, jobs.Session, jobs.NextSeq(), job.Id, ct);
        }
        catch (BackupException e)
        {
            args.Changes.AutoRefitPreference.Drop();
            job.Fail(new JobError(e.Code, e.Message));
            return;
        }
        jobs.RememberBackup(job.Id, backup);
        job.SetBackup(new BackupRef(backup.Id, backup.Path));

        job.Progress("refitting what is worn from the mod");
        IReadOnlyList<(string Slot, string GamePath, string Outcome)>? outcomes;
        try
        {
            outcomes = await jobs.Dispatcher.RunAsync(() => game.RunAutoRefitNowFor(args.ModRoot), jobs.Timings.FrameworkStart, ct)
                                 .ConfigureAwait(false);
        }
        catch (FrameworkTimeoutException)
        {
            args.Changes.AutoRefitPreference.Drop();
            job.Fail(new JobError("framework-timeout", "the game's framework did not start the refit in time; nothing was written"));
            return;
        }
        if (outcomes == null || !outcomes.Any(o => o.Outcome == "enqueued"))
        {
            args.Changes.AutoRefitPreference.Drop();
            job.SetResult(new { pieces = (outcomes ?? []).Select(o => new { slot = o.Slot, gamePath = o.GamePath, outcome = o.Outcome }) });
            job.Fail(new JobError("nothing-enqueued", outcomes == null
                ? "auto refit could not run; nothing was written"
                : "no piece worn from the mod was refitted (see result.pieces); nothing was written"));
            return;
        }
        args.Changes.NoteJobBackedMod(args.ModDir, job.Id);

        job.Progress("waiting for the refits");
        var clock = Stopwatch.StartNew();
        int quiet = 0;
        bool finished = false;
        while (clock.Elapsed < jobs.Timings.AutoRefitNowWait)
        {
            await Task.Delay(Sample, ct).ConfigureAwait(false);
            if (game.AutoRefit().Busy) quiet = 0;
            else if (++quiet >= QuietLooks)
            {
                finished = true;
                break;
            }
        }

        try
        {
            await jobs.Dispatcher.RunAsync(() =>
            {
                args.Changes.Bindings.NoteExternalPermanent(args.Collection, args.ModDir, args.Before);
                args.Changes.AutoRefitPreference.End(finished);
                return true;
            }, jobs.Timings.FrameworkStart, ct).ConfigureAwait(false);
        }
        catch (FrameworkTimeoutException)
        {
            jobs.Log?.Warning("[Proteus] test harness: the settings after the worn refit could not be recorded in time");
        }
        try { jobs.Backups.WriteAfter(backup, args.ModRoot); }
        catch (Exception e) { jobs.Log?.Warning(e, "[Proteus] test harness: could not record {0}'s state after the worn refit", args.ModDir); }

        var record = BodyRetargetWriter.ReadRecord(args.ModRoot);
        job.SetResult(new
        {
            finished,
            pieces = outcomes.Select(o =>
            {
                var entry = record?.Options.LastOrDefault(e => e.Auto && string.Equals(e.GamePath, o.GamePath, StringComparison.OrdinalIgnoreCase));
                return new
                {
                    slot = o.Slot, gamePath = o.GamePath,
                    outcome = o.Outcome != "enqueued" ? o.Outcome : finished ? "done" : "timed-out",
                    option = entry?.Name, from = entry?.From, to = entry?.To, seam = entry?.Seam,
                    targets = entry?.Targets?.ToDictionary(t => t.Key, t => new { dir = entry.BodyOf(t.Key), rel = t.Value }),
                };
            }).ToList(),
        });
        job.Progress("");
    }
}
