using System;
using System.IO;
using Dalamud.Plugin.Services;

namespace Proteus.TestHarness;

/// <summary>
/// The collection's auto refit preference (<see cref="Configuration.AutoRefitByCollection"/>) as it stood before the
/// harness's worn-refit trigger (<see cref="AutoRefitNowJob"/>) and as its last finished job left it. A forced refit changes
/// it — a declined piece is forgotten, each refit records the group's Before — so put-back restores it, but only while it
/// is still exactly what the job left: a preference the player changed meanwhile is theirs (ADR 0002, 2026-11-18).
/// <para/>
/// The Before text is written to the session's record folder first, and is only ever deleted once put back (or found
/// changed by the player): a job still running, cancelled or timed out leaves it there, said in the log, for QA to check.
/// A later job of the same session keeps the first Before, and a later job that fails before writing anything leaves the
/// earlier job's record exactly as it was.
/// </summary>
internal sealed class HarnessAutoRefitPreference(IHarnessGame game, IPluginLog? log, string? recordRoot, string session)
{
    private readonly object gate = new();
    private Guid? collection;
    private string? before;
    /// <summary>What the last finished job left; null while none has finished.</summary>
    private string? after;
    /// <summary>A job is between <see cref="Begin"/> and <see cref="End"/>/<see cref="Drop"/>.</summary>
    private bool running;
    /// <summary>A job ended without its refits finishing (timed out or cancelled): what it left is not known.</summary>
    private bool unfinished;
    private string? jobId;

    /// <summary>A Before is recorded and not yet put back.</summary>
    public bool Recorded { get { lock (gate) return before != null; } }

    /// <summary>The job whose refit the record is for, for put-back's notes.</summary>
    public string? JobId { get { lock (gate) return jobId; } }

    private string? RecordPath => recordRoot == null ? null : Path.Combine(recordRoot, session, "autorefit-preference.json");

    /// <summary>Record the preference before a job (a session's first record keeps its Before). False when it cannot be
    /// read or written down, and then nothing may start. Framework thread.</summary>
    public bool Begin(Guid coll, string job)
    {
        lock (gate)
        {
            if (before != null)
            {
                if (collection != coll) return false;
                (running, jobId) = (true, job);
                return true;
            }
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
        lock (gate) (collection, before, after, running, unfinished, jobId) = (coll, text, null, true, false, job);
        return true;
    }

    /// <summary>The job is over: record what it left (finished), or that it did not finish. Framework thread when
    /// finished (it reads the configuration); any thread otherwise.</summary>
    public void End(bool finished)
    {
        Guid? coll;
        lock (gate)
        {
            if (before == null) return;
            running = false;
            if (!finished)
            {
                unfinished = true;
                return;
            }
            coll = collection;
        }
        var now = coll is { } c ? game.AutoRefitPreferenceJson(c) : null;
        lock (gate)
        {
            after = now;
            unfinished = now == null;
        }
    }

    /// <summary>The job ended without saying how (it threw): treated as not finished. A no-op when it already ended.</summary>
    public void EndIfRunning()
    {
        lock (gate)
        {
            if (before == null || !running) return;
            running = false;
            unfinished = true;
        }
    }

    /// <summary>The job failed before its refit could write anything: an earlier job's record stands as it was; a first
    /// job's record has nothing to put back.</summary>
    public void Drop()
    {
        bool forget;
        lock (gate)
        {
            running = false;
            forget = before != null && after == null && !unfinished;
        }
        if (forget) Forget();
    }

    /// <summary>
    /// Put the preference back while it is still what the job left: "restored", "changed" (the player's now, left),
    /// "skipped-busy" (auto refit is working: kept for the next put-back), "skipped-running" (a job is still running:
    /// kept), "recorded-before-finish" (no finished job's state is known: left as it is, Before kept on disk and logged),
    /// "failed", or "none". Framework thread.
    /// </summary>
    public string PutBack(bool autoRefitBusy)
    {
        Guid coll;
        string was, left;
        lock (gate)
        {
            if (collection is not { } c || before == null) return "none";
            if (running) return "skipped-running";
            if (unfinished || after == null)
            {
                log?.Warning("[Proteus] test harness: the auto refit preference was recorded before the refit finished; left "
                             + "as it is (its Before is kept in {0})", RecordPath is { } p ? Path.GetFileName(p) : "memory only");
                (collection, before, after, running, unfinished, jobId) = (null, null, null, false, false, null);
                return "recorded-before-finish";
            }
            (coll, was, left) = (c, before, after);
        }
        if (autoRefitBusy) return "skipped-busy";
        string? now = game.AutoRefitPreferenceJson(coll);
        if (now == null) return "failed";
        if (!string.Equals(now, left, StringComparison.Ordinal))
        {
            log?.Information("[Proteus] test harness: the auto refit preference changed since the job; left as it is");
            Forget();
            return "changed";
        }
        if (!string.Equals(was, left, StringComparison.Ordinal) && !game.RestoreAutoRefitPreference(coll, was)) return "failed";
        Forget();
        return "restored";
    }

    private void Forget()
    {
        if (RecordPath is { } path)
            try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        lock (gate) (collection, before, after, running, unfinished, jobId) = (null, null, null, false, false, null);
    }
}
