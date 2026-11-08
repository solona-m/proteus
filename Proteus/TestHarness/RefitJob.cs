using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>A body skipped by a refit, and why: "not-offered" (the mod's pieces don't offer it) or "not-installed".</summary>
internal sealed record SkippedBody(string Key, string Reason);

/// <summary>
/// The refit action: the size set on one installed mod, the Studio's "Make every size", with a backup first. The hop
/// sequence after the request's own framework hop (which checked auto refit, Penumbra and the folder):
/// <list type="number">
/// <item>Worker: refuse a legacy (v3) folder, writing nothing.</item>
/// <item>Worker: read the mod's pieces and the installed bodies; skip bodies not offered or not installed. None left:
/// done, nothing made, no backup.</item>
/// <item>Framework, abandonable: prepare the request. A timeout fails the job (framework-timeout), nothing written.</item>
/// <item>Worker: back the mod up, then run the size set.</item>
/// <item>Framework, never abandoned: the after-change (reload, redraw, Studio refresh), which the job waits for.</item>
/// <item>Worker: record the after-state, watch the redraw and auto refit, then release the slot.</item>
/// </list>
/// </summary>
internal static class RefitJob
{
    /// <param name="KeepHiddenSkin">The request's override of the Studio's saved "Keep hidden skin" for this run alone,
    /// never saved; null runs with the saved choice.</param>
    internal sealed record Args(string ModDir, string ModRoot, IReadOnlyList<string> Keys, bool Remake, ModList Mods,
                                bool? KeepHiddenSkin = null);

    internal static async Task Run(HarnessJobs jobs, HarnessJob job, Args args)
    {
        var game = jobs.Game;
        var ct = job.Cancel.Token;

        job.Progress("checking the mod");
        if (game.IsLegacyFolder(args.ModRoot))
        {
            job.Fail(new JobError("legacy-mod", "the mod is an old (v3) Penumbra mod; the size set never writes those"));
            return;
        }

        job.Progress("reading the mod's pieces");
        IReadOnlyList<(string Key, string Body)> offered;
        IReadOnlySet<string> installed;
        try
        {
            offered = game.OfferedKeys(args.ModRoot);
            installed = game.InstalledKeys(args.Mods);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A mod's or a body's data could not be read: the run's failure, not the harness's.
            job.Fail(new JobError("run-failed", $"reading the mod's pieces or the installed bodies failed: {e.GetType().Name}: {e.Message}"));
            return;
        }
        var keys = new List<string>();
        var skipped = new List<SkippedBody>();
        foreach (string key in args.Keys)
        {
            var hit = offered.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.Ordinal));
            if (hit.Key == null) skipped.Add(new SkippedBody(key, "not-offered"));
            else if (!installed.Contains(hit.Body)) skipped.Add(new SkippedBody(key, "not-installed"));
            else keys.Add(key);
        }
        var options = game.Options(args.Remake);
        // Before the nothing-to-make answer too, so every result reports the choice the run would use.
        if (args.KeepHiddenSkin is { } keepSkin) options = options with { KeepHiddenSkin = keepSkin };
        if (keys.Count == 0)
        {
            job.SetResult(new
            {
                summary = "nothing-to-make", resultLine = "No body asked for can be made for this mod.", made = 0,
                notes = Array.Empty<string>(), warnings = Array.Empty<string>(), skippedBodies = skipped, options,
                report = (object?)null,
            });
            return;
        }
        ct.ThrowIfCancellationRequested();

        job.Progress("preparing the size set");
        SizeSetLaunch.Prepared prepared;
        try
        {
            prepared = await jobs.Dispatcher.RunAsync(() => game.PrepareSizeSet(args.ModRoot, keys, options, args.Mods),
                                                      jobs.Timings.FrameworkStart, ct).ConfigureAwait(false);
        }
        catch (FrameworkTimeoutException)
        {
            job.Fail(new JobError("framework-timeout", "the game's framework did not prepare the run in time; nothing was written"));
            return;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The game's bodies or the collection's skeletons could not be read: nothing was written.
            job.Fail(new JobError("run-failed", $"preparing the run failed: {e.GetType().Name}: {e.Message}"));
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
            job.Fail(new JobError(e.Code, e.Message));
            return;
        }
        jobs.RememberBackup(job.Id, backup);
        job.SetBackup(new BackupRef(backup.Id, backup.Path));

        SizeSetLaunch.Outcome? outcome = null;
        bool cancelled = false;
        try
        {
            outcome = await game.RunSizeSet(prepared, job.Progress,
                                            () => job.SetAfterChange(game.AfterModChange(args.ModDir)),
                                            a => jobs.PostAfterChange(job, a), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            job.Fail(new JobError("cancelled", "the run was cancelled"));
        }
        catch (Exception e)
        {
            job.Fail(new JobError("run-failed", $"{e.GetType().Name}: {e.Message}"));
        }

        await jobs.AwaitAfterChange(job).ConfigureAwait(false);

        try { jobs.Backups.WriteAfter(backup, args.ModRoot); }
        catch (Exception e)
        {
            // changedSinceJob is then null: a warning lost, not data.
            jobs.Log?.Warning(e, "[Proteus] test harness: could not record {0}'s state after the refit", args.ModDir);
        }

        if (outcome != null)
        {
            var r = outcome.Result;
            var batch = new SizeSetBatch.Outcome(keys, null, null, null, cancelled, [], [], outcome.Seconds, 0, 0, null);
            object? report = null;
            try
            {
                var json = SizeSetReport.Build(args.ModDir, Plugin.BuildNumber, batch, outcome.RunLog,
                                               SizeSetRefit.MensKeys(keys).Count > 0, options.Ends).ToJsonString();
                report = System.Text.Json.Nodes.JsonNode.Parse(LogAnonymizer.Scrub(json));
            }
            catch (Exception e)
            {
                // The summary below still says what happened.
                jobs.Log?.Warning(e, "[Proteus] test harness: could not build the size set report for {0}", args.ModDir);
            }
            job.SetResult(new
            {
                summary = r.Summary.ToString(), resultLine = SizeSetRefit.ResultLine(r), made = r.Made, notes = r.Notes,
                warnings = r.Warnings ?? [], skippedBodies = skipped, options, report,
            });
        }

        job.Progress("watching the redraw");
        await jobs.Follow(job).ConfigureAwait(false);
        job.Progress("");
    }
}
