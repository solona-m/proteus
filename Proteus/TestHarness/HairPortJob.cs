using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Proteus.Gui;
using Proteus.Interop;
using Proteus.Localization;
using Proteus.Services;
using XivLiveMesh;

namespace Proteus.TestHarness;

/// <summary>
/// The Studio's "Port to all races" for QA: the rows the panel lists (<see cref="Read"/>), and its Port and Remove ports
/// buttons as a job (<see cref="Run"/>) — the panel's own code (<see cref="HairPortPanel.ReadRows"/>,
/// <see cref="HairPortPanel.PlanByHand"/>, <see cref="HairPortPanel.SaveByHand"/>, <see cref="HairPortPanel.RemoveByHand"/>,
/// <see cref="HairPortPanel.ResultOf"/>), with a backup before the first write. Only races the Studio would let the user
/// tick are ported. The hop sequence after the request's own framework hop (which found the folder and checked the busy
/// rules):
/// <list type="number">
/// <item>Worker: refuse a legacy, unreadable or hairless mod, writing nothing; read the rows.</item>
/// <item>Worker: skip races the Studio could not tick; none left, done. Bend (read-only).</item>
/// <item>Worker: nothing to write, done — the refusals logged as the Studio logs them. Else back the mod up, then save.</item>
/// <item>Framework, never abandoned: the after-change (reload, redraw, Studio refresh) when something was saved.</item>
/// <item>Worker: record the after-state, watch the redraw.</item>
/// </list>
/// </summary>
internal static class HairPortJob
{
    /// <param name="Hair">The hair model's game path as QA gave it; looked up without regard to case or slashes.</param>
    /// <param name="Races">The races to port; null for the Studio's default ticks.</param>
    internal sealed record Args(string ModDir, string ModRoot, string Hair, IReadOnlyList<ushort>? Races, bool Remove);

    internal static string RaceCode(ushort race) => $"c{race:D4}";

    /// <summary>Whether a mod holds hair: a record of our ports, or a hair model its manifest publishes. Reads files.</summary>
    internal static bool IsHairMod(string modRoot)
    {
        if (HairPortRecord.Exists(modRoot)) return true;
        return HairPortPlan.ReadOwners(modRoot) is { } owners
               && owners.Any(o => o.Files.Keys.Any(k => HairPortPlan.Parse(k) != null));
    }

    /// <summary>The hair path QA gave, as the mod spells it: a game path a place of the mod publishes, else the target of a
    /// port the record holds. Null when the mod has neither.</summary>
    internal static string? ResolveHair(IReadOnlyList<HairPortPlan.Owner> owners, HairPortRecord record, string hair)
    {
        string want = hair.Replace('\\', '/');
        foreach (var owner in owners)
            foreach (var path in owner.Files.Keys)
                if (string.Equals(path.Replace('\\', '/'), want, StringComparison.OrdinalIgnoreCase)) return path;
        return record.Ports.Select(e => e.TargetGamePath)
                     .FirstOrDefault(t => string.Equals(t, want, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The mod read for a hair port, or the job error refusing it.</summary>
    private static (IReadOnlyList<HairPortPlan.Owner>? Owners, HairPortRecord? Record, string? Hair, JobError? Error)
        Open(IHarnessGame game, string modRoot, string hair)
    {
        if (game.IsLegacyFolder(modRoot))
            return (null, null, null, new JobError("legacy-mod", "the mod is an old (v3) Penumbra mod; hair is never ported into those"));
        var owners = HairPortPlan.ReadOwners(modRoot);
        var record = HairPortRecord.Read(modRoot);
        if (owners == null || record.Unreadable)
            return (null, null, null, new JobError("mod-unreadable", "the mod's manifest or its record of hair ports could not be read; try again"));
        if (ResolveHair(owners, record, hair) is not { } spelled)
            return (null, null, null, new JobError("hair-not-in-mod", "the mod does not publish that hairstyle"));
        return (owners, record, spelled, null);
    }

    private static string Code(HairPortPanel.State s) => s switch
    {
        HairPortPanel.State.Open => "open",
        HairPortPanel.State.Covered => "covered",
        HairPortPanel.State.Ported => "ported",
        HairPortPanel.State.Stale => "stale",
        HairPortPanel.State.NoStyle => "no-style",
        _ => "other-sex-authored",
    };

    /// <summary>Why the Studio would not let the user tick a row.</summary>
    private static string SkipReason(HairPortPanel.Row r)
        => r.OwnVersion ? "own-version" : r.OwnSkeleton ? "own-skeleton" : Code(r.State);

    /// <summary>The GET body: what the Studio panel lists for the hairstyle, and the ports of it the record holds.
    /// Reads the mod and the game; never the framework thread.</summary>
    internal static (object? View, HarnessResponse? Error) Read(IHarnessGame game, string modRoot, string modDir, string hair,
                                                               SessionNames names)
    {
        var (owners, record, spelled, error) = Open(game, modRoot, hair);
        if (error != null)
            return (null, HarnessResponse.Error(error.Code == "hair-not-in-mod" ? 404 : 409, error.Code, error.Message));
        var (source, rows) = HairPortPanel.ReadRows(modRoot, spelled!, null, game.ReadGameFile);
        var at = HairPortPlan.Parse(source)!.Value;
        var ticks = HairPortPanel.DefaultTicks(rows).ToHashSet();
        string Target(ushort race) => HairPortPlan.HairPath(race, at.Id);
        static string Norm(string p) => p.Replace('\\', '/');
        var view = new
        {
            modDir, hair = spelled, source, sourceRace = RaceCode(at.Race), id = at.Id,
            otherSexNote = HairPortPanel.OtherSexNote(at.Id),
            rows = rows.Select(r => new
            {
                race = RaceCode(r.Race), state = Code(r.State), otherSex = r.OtherSex, ownVersion = r.OwnVersion,
                ownSkeleton = r.OwnSkeleton, tickable = HairPortPanel.CanTick(r), defaultTicked = ticks.Contains(r.Race),
                declined = record!.Declined.Contains(HairPortPlan.DeclineKey(Target(r.Race))),
                stateText = HairPortPanel.StateText(r),
                // A warning remembered for a target is only the row's while its port is there.
                warning = r.State is HairPortPanel.State.Ported or HairPortPanel.State.Stale
                    ? names.Scrub(HairPortScan.WarningFor(modRoot, Target(r.Race))) : "",
            }),
            ports = record!.Ports.Where(e => string.Equals(Norm(e.SourceGamePath), Norm(source), StringComparison.OrdinalIgnoreCase))
                           .Select(e => new
                           {
                               race = HairPortPlan.Parse(e.TargetGamePath) is { } t ? RaceCode(t.Race) : null,
                               targetGamePath = e.TargetGamePath, sourceGamePath = e.SourceGamePath, group = e.Group,
                               option = e.Option, file = e.File, auto = e.Auto, est = e.Est, skeletonFrom = e.SkeletonFrom,
                               portVersion = e.PortVersion,
                           }),
        };
        return (view, null);
    }

    internal static async Task Run(HarnessJobs jobs, HarnessJob job, Args args)
    {
        var game = jobs.Game;
        var ct = job.Cancel.Token;

        job.Progress("checking the mod");
        var (_, _, spelled, error) = Open(game, args.ModRoot, args.Hair);
        if (error != null)
        {
            job.Fail(error);
            return;
        }
        var (source, rows) = HairPortPanel.ReadRows(args.ModRoot, spelled!, null, game.ReadGameFile);
        ct.ThrowIfCancellationRequested();

        if (args.Remove)
        {
            await Remove(jobs, job, args, source, rows).ConfigureAwait(false);
            return;
        }

        // The Studio's checkboxes: only a race it lets the user tick is ported.
        var asked = args.Races ?? HairPortPanel.DefaultTicks(rows).ToList();
        var targets = new List<ushort>();
        var skipped = new List<object>();
        foreach (ushort race in asked)
        {
            if (rows.FirstOrDefault(r => r.Race == race) is not { } row) skipped.Add(new { race = RaceCode(race), reason = "not-listed" });
            else if (!HairPortPanel.CanTick(row)) skipped.Add(new { race = RaceCode(race), reason = SkipReason(row) });
            else targets.Add(race);
        }
        var askedCodes = asked.Select(RaceCode).ToList();
        if (targets.Count == 0)
        {
            job.SetResult(new
            {
                action = "port", summary = "nothing-to-port", resultLine = Strings.HairPort.NothingToDo, source, asked = askedCodes,
                skippedRaces = skipped, ported = Array.Empty<object>(), stripped = Array.Empty<object>(),
                refused = Array.Empty<object>(), warnings = Array.Empty<object>(), current = Array.Empty<string>(), saved = 0,
            });
            return;
        }

        job.Progress("reading the game's racial deformer");
        if (game.ReadGameFile(LiveCharacter.PbdGamePath) is not { } pbdBytes)
        {
            job.Fail(new JobError("run-failed", HairPortWatcher.NoPbd));
            return;
        }
        PbdFile pbd;
        try { pbd = new PbdFile(pbdBytes); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The game's file could not be read as a deformer: the run's failure, not the harness's.
            job.Fail(new JobError("run-failed", $"{HairPortWatcher.NoPbd} ({e.GetType().Name})"));
            return;
        }

        var plan = HairPortPanel.PlanByHand(args.ModRoot, source, targets, pbd, game.ReadGameFile, ct,
                                            (done, total) => job.Progress($"bending {done}/{total}"));
        ct.ThrowIfCancellationRequested();

        BackupInfo? backup = null;
        if (plan.Writes.Count > 0)
        {
            job.Progress("backing up the mod");
            try
            {
                backup = jobs.Backups.Create(args.ModRoot, args.ModDir, BackupKind.HairPort, jobs.Session, jobs.NextSeq(), job.Id, ct);
            }
            catch (BackupException e)
            {
                job.Fail(new JobError(e.Code, e.Message));
                return;
            }
            jobs.RememberBackup(job.Id, backup);
            job.SetBackup(new BackupRef(backup.Id, backup.Path));
            ct.ThrowIfCancellationRequested();
        }

        // With nothing to write this still runs, as the Studio's does: an empty save touches no file and logs the refusals.
        job.Progress("saving");
        var outcome = HairPortPanel.SaveByHand(args.ModRoot, plan, jobs.Log);
        bool changed = outcome.Count > 0;
        if (changed)
        {
            jobs.PostAfterChange(job, () => job.SetAfterChange(game.AfterModChange(args.ModDir)));
            await jobs.AwaitAfterChange(job).ConfigureAwait(false);
        }
        if (backup != null) WriteAfter(jobs, backup, args);

        var (line, _, _) = HairPortPanel.ResultOf(plan, outcome, removed: false);
        var saved = outcome.SavedWrites;
        object Place(HairPortWriter.PortWrite w) => new
        {
            race = HairPortPlan.Parse(w.TargetGamePath) is { } t ? RaceCode(t.Race) : null, targetGamePath = w.TargetGamePath,
            group = w.Into.Group, option = w.Into.Option,
        };
        job.SetResult(new
        {
            action = "port",
            summary = saved.Any(w => !w.Stripped) ? "ported" : "nothing-ported",
            resultLine = line, source, asked = askedCodes, skippedRaces = skipped,
            ported = saved.Where(w => !w.Stripped).Select(w => new
            {
                race = HairPortPlan.Parse(w.TargetGamePath) is { } t ? RaceCode(t.Race) : null, targetGamePath = w.TargetGamePath,
                group = w.Into.Group, option = w.Into.Option, est = w.Est, skeletonFrom = w.SkeletonFrom, millis = w.Millis,
            }),
            stripped = saved.Where(w => w.Stripped).Select(Place),
            refused = plan.Refused.OrderBy(r => r.Key).Select(r => new { race = RaceCode(r.Key), reason = r.Value }),
            warnings = (plan.Warnings ?? new Dictionary<ushort, string>()).OrderBy(w => w.Key)
                       .Select(w => new { race = RaceCode(w.Key), warning = w.Value }),
            current = plan.Current.Order().Select(RaceCode), saved = outcome.Count,
        });
        if (!outcome.Ok) job.Fail(new JobError("run-failed", outcome.Message));

        if (changed)
        {
            job.Progress("watching the redraw");
            await jobs.Follow(job).ConfigureAwait(false);
        }
        job.Progress("");
    }

    /// <summary>Remove ports: every port of the source out, after a backup — as the Studio's button does.</summary>
    private static async Task Remove(HarnessJobs jobs, HarnessJob job, Args args, string source, List<HairPortPanel.Row> rows)
    {
        var game = jobs.Game;
        var ct = job.Cancel.Token;
        if (!rows.Any(r => r.State is HairPortPanel.State.Ported or HairPortPanel.State.Stale))
        {
            job.SetResult(new
            {
                action = "remove", summary = "nothing-to-remove", resultLine = "", source, removed = 0, message = "",
                remaining = Array.Empty<object>(),
            });
            return;
        }

        job.Progress("backing up the mod");
        BackupInfo backup;
        try
        {
            backup = jobs.Backups.Create(args.ModRoot, args.ModDir, BackupKind.HairPort, jobs.Session, jobs.NextSeq(), job.Id, ct);
        }
        catch (BackupException e)
        {
            job.Fail(new JobError(e.Code, e.Message));
            return;
        }
        jobs.RememberBackup(job.Id, backup);
        job.SetBackup(new BackupRef(backup.Id, backup.Path));
        ct.ThrowIfCancellationRequested();

        job.Progress("removing");
        var outcome = HairPortPanel.RemoveByHand(args.ModRoot, source);
        bool changed = outcome.Count > 0;
        if (changed)
        {
            jobs.PostAfterChange(job, () => job.SetAfterChange(game.AfterModChange(args.ModDir)));
            await jobs.AwaitAfterChange(job).ConfigureAwait(false);
        }
        WriteAfter(jobs, backup, args);

        var empty = new HairPortBuilder.Plan([], new Dictionary<ushort, string>(), new HashSet<ushort>());
        var (line, _, _) = HairPortPanel.ResultOf(empty, outcome, removed: true);
        var left = HairPortRecord.Read(args.ModRoot).Ports
            .Where(e => string.Equals(e.SourceGamePath, source.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            .Select(e => new
            {
                race = HairPortPlan.Parse(e.TargetGamePath) is { } t ? RaceCode(t.Race) : null, targetGamePath = e.TargetGamePath,
                group = e.Group, option = e.Option,
            }).ToList();
        job.SetResult(new
        {
            action = "remove", summary = "removed", resultLine = line, source, removed = outcome.Count,
            message = outcome.Message, remaining = left,
        });
        if (!outcome.Ok) job.Fail(new JobError("run-failed", outcome.Message));

        if (changed)
        {
            job.Progress("watching the redraw");
            await jobs.Follow(job).ConfigureAwait(false);
        }
        job.Progress("");
    }

    private static void WriteAfter(HarnessJobs jobs, BackupInfo backup, Args args)
    {
        try { jobs.Backups.WriteAfter(backup, args.ModRoot); }
        catch (Exception e)
        {
            // changedSinceJob is then null: a warning lost, not data.
            jobs.Log?.Warning(e, "[Proteus] test harness: could not record {0}'s state after the hair port", args.ModDir);
        }
    }
}
