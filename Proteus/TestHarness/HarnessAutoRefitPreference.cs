using System;
using System.IO;
using Dalamud.Plugin.Services;

namespace Proteus.TestHarness;

/// <summary>
/// The collection's auto refit preference (<see cref="Configuration.AutoRefitByCollection"/>) as it stood before the
/// harness's worn-refit trigger (<see cref="AutoRefitNowJob"/>) and as the job left it. A forced refit changes it — a
/// declined piece is forgotten, each refit records the group's Before — so put-back restores it, but only while it is
/// still exactly what the job left: a preference the player changed meanwhile is theirs (ADR 0002, 2026-11-18). The
/// Before text is also written to the session's record folder, so a put-back that never happens leaves it on disk.
/// </summary>
internal sealed class HarnessAutoRefitPreference(IHarnessGame game, IPluginLog? log, string? recordRoot, string session)
{
    private Guid? collection;
    private string? before;
    private string? after;
    private bool timedOut;
    private string? jobId;

    /// <summary>A Before is recorded and not yet put back.</summary>
    public bool Recorded => before != null;

    /// <summary>The job whose refit the record is for, for put-back's notes.</summary>
    public string? JobId => jobId;

    private string? RecordPath => recordRoot == null ? null : Path.Combine(recordRoot, session, "autorefit-preference.json");

    /// <summary>Record the preference before a job (the first record of a session keeps its Before). False when it cannot
    /// be read or written down, and then nothing may start. Framework thread.</summary>
    public bool Begin(Guid coll, string job)
    {
        if (before != null && collection == coll)
        {
            jobId = job;
            after = null;
            timedOut = false;
            return true;
        }
        if (game.AutoRefitPreferenceJson(coll) is not { } text) return false;
        try
        {
            if (RecordPath is { } path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Warning(e, "[Proteus] test harness: the auto refit preference's Before could not be written");
            return false;
        }
        (collection, before, after, timedOut, jobId) = (coll, text, null, false, job);
        return true;
    }

    /// <summary>The job is over: record what it left. Framework thread.</summary>
    public void End(bool finished)
    {
        if (collection is not { } coll || before == null) return;
        timedOut = !finished;
        after = finished ? game.AutoRefitPreferenceJson(coll) : null;
    }

    /// <summary>The job failed before its refit could write anything: nothing to put back.</summary>
    public void Drop()
    {
        if (after == null && !timedOut) Forget();
    }

    /// <summary>
    /// Put the preference back while it is still what the job left: "restored", "changed" (the player's now, left),
    /// "skipped-busy" (auto refit is working: kept for the next put-back), "recorded-before-finish" (the job timed out
    /// before its refit ended: left as it is, for QA to check), "dropped" (a job that wrote nothing), "failed", or "none".
    /// Framework thread.
    /// </summary>
    public string PutBack(bool autoRefitBusy)
    {
        if (collection is not { } coll || before == null) return "none";
        if (timedOut)
        {
            log?.Warning("[Proteus] test harness: the auto refit preference was recorded before the refit finished; left as it is (Before kept in the session record)");
            Forget(keepFile: true);
            return "recorded-before-finish";
        }
        if (after == null)
        {
            Forget();
            return "dropped";
        }
        if (autoRefitBusy) return "skipped-busy";
        string? now = game.AutoRefitPreferenceJson(coll);
        if (now == null) return "failed";
        if (!string.Equals(now, after, StringComparison.Ordinal))
        {
            log?.Information("[Proteus] test harness: the auto refit preference changed since the job; left as it is");
            Forget();
            return "changed";
        }
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            Forget();
            return "restored";
        }
        if (!game.RestoreAutoRefitPreference(coll, before)) return "failed";
        Forget();
        return "restored";
    }

    private void Forget(bool keepFile = false)
    {
        if (!keepFile && RecordPath is { } path)
            try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        (collection, before, after, timedOut, jobId) = (null, null, null, false, null);
    }
}
