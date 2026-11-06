using System;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;

namespace Proteus.TestHarness;

/// <summary>
/// The QA test harness's lifecycle (dev builds only; see docs/agent-rules/adversarial-qa.md and ADR 0002):
/// <c>/proteus harness on|off|status</c>, the session file QA reads, the autostart marker that keeps it on across a
/// rebuild-and-reload, and shutdown. It refuses to run in an install that is not dev-loaded, and it is only ever turned
/// on by the user typing the command: never from the dev-command file.
/// </summary>
internal sealed class HarnessHost : IDisposable
{
    private readonly string configDir;
    private readonly bool isDev;
    private readonly IHarnessGame game;
    private readonly IFrameworkDispatcher dispatcher;
    private readonly Action<Action<string>?> setLineObserver;
    private readonly IPluginLog log;
    private readonly int build;
    private readonly string buildStamp;
    private readonly HarnessTimings timings;
    private readonly Func<string>? backupIds;

    private HarnessServer? server;
    private HarnessJobs? jobs;
    private HarnessChanges? changes;
    private string? token;
    private volatile bool disposing;

    /// <param name="setLineObserver">Attaches the log ring to the plugin's log (<c>CapturingPluginLog.LineObserver</c>).</param>
    /// <param name="backupIds">A test's backup id source; null for the real one.</param>
    public HarnessHost(string configDir, bool isDev, IHarnessGame game, IFrameworkDispatcher dispatcher,
                       Action<Action<string>?> setLineObserver, IPluginLog log, int build, string buildStamp,
                       HarnessTimings? timings = null, Func<string>? backupIds = null)
    {
        this.configDir = configDir;
        this.isDev = isDev;
        this.game = game;
        this.dispatcher = dispatcher;
        this.setLineObserver = setLineObserver;
        this.log = log;
        this.build = build;
        this.buildStamp = buildStamp;
        this.timings = timings ?? HarnessTimings.Default;
        this.backupIds = backupIds;
    }

    public bool Listening => server != null;
    public int Port => server?.Port ?? 0;
    public string? Session => jobs?.Session;
    public SessionNames? Names { get; private set; }
    internal HarnessJobs? Jobs => jobs;
    internal HarnessChanges? Changes => changes;

    /// <summary>The backups folder: <c>&lt;ConfigDirectory&gt;\test-harness\backups</c>.</summary>
    public string BackupsRoot => Path.Combine(configDir, "test-harness", "backups");

    /// <summary>A dev-command-file line that only a typed command may run: the harness's opt-in is the user's.</summary>
    public static bool IsTypedOnly(string line)
        => line.TrimStart().StartsWith("harness", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>/proteus harness …</c>: the chat line to print.</summary>
    public string Command(string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on":
                if (!isDev) return "[Proteus] The test harness only runs in a dev-loaded build.";
                string started = Start();
                // Only once it is really on: a refused or undone start must not autostart at the next load.
                if (Listening)
                {
                    try { File.WriteAllText(HarnessSessionFile.MarkerIn(configDir), "on"); }
                    catch (Exception e) { log.Warning(e, "[Proteus] test harness: could not write the autostart marker"); }
                }
                return started;
            case "off":
                Stop();
                try { File.Delete(HarnessSessionFile.MarkerIn(configDir)); } catch { /* not there */ }
                return "[Proteus] Test harness off.";
            case "status":
            case "":
                return Listening ? $"[Proteus] Test harness listening on 127.0.0.1:{Port} (session {Session})."
                                 : "[Proteus] Test harness off.";
            default:
                return "[Proteus] /proteus harness on, off or status.";
        }
    }

    /// <summary>At load: on again when the marker says it was left on. The chat line, or null when it stays off.</summary>
    public string? TryAutostart()
    {
        if (!isDev || !File.Exists(HarnessSessionFile.MarkerIn(configDir))) return null;
        return Start() + " (it was left on; /proteus harness off turns it off)";
    }

    private string Start()
    {
        if (server != null) return $"[Proteus] Test harness already listening on 127.0.0.1:{Port}.";
        // One job at a time across sessions too: a job the last "off" cancelled may still be writing its mod.
        if (jobs?.Current is { } stopping)
            return $"[Proteus] A test harness job ({stopping.Kind} of {stopping.ModDir}) is still stopping; try again in a moment.";
        string session = HarnessJobs.NewJobId();
        token = HarnessAuth.NewToken();
        Names = new SessionNames();
        var ring = new LogRing(Names);
        jobs = new HarnessJobs(game, dispatcher, new ModBackup(BackupsRoot, backupIds), session, () => disposing, timings)
        {
            Log = log,
        };
        changes = new HarnessChanges(game, Names, log, BackupsRoot, session, build);
        var router = new HarnessRouter(jobs, ring, Names, new CaptureGate(timings.Capture), build, buildStamp, log, changes);
        var s = new HarnessServer(router.Handle, token);
        s.Start();
        server = s;
        setLineObserver(ring.Add);
        game.NameSink = Names;
        try
        {
            HarnessSessionFile.Write(configDir, new HarnessSession(s.Port, token, Environment.ProcessId, build, buildStamp,
                                                                   session, DateTime.UtcNow));
        }
        catch (Exception e)
        {
            // Listening with no session file is a harness QA cannot find: undo the start.
            log.Warning(e, "[Proteus] test harness: could not write its session file");
            Stop();
            return $"[Proteus] Test harness could not start: the session file could not be written ({e.Message}).";
        }
        // The port and session only: the token is never logged.
        log.Information("[Proteus] test harness listening on 127.0.0.1:{0} (session {1})", s.Port, session);
        return $"[Proteus] Test harness listening on 127.0.0.1:{s.Port}. QA reads {HarnessSessionFile.FileName} in the plugin's config folder.";
    }

    private void Stop()
    {
        if (server == null) return;
        PutBackOnStop();
        jobs?.CancelRunning();
        setLineObserver(null);
        game.NameSink = null;
        server.Dispose();
        server = null;
        if (jobs != null) HarnessSessionFile.DeleteIfOwn(configDir, jobs.Session);
        log.Information("[Proteus] test harness stopped");
    }

    /// <summary>
    /// Put back this session's equips and holds as the harness stops: inline when stopping on the framework thread (the
    /// command, or an unload there), before any bridge goes. Off it nothing is posted (an unload must not wait on the
    /// framework); what is left is logged instead.
    /// </summary>
    private void PutBackOnStop()
    {
        if (changes is not { } c) return;
        c.BeginStop();
        if (!dispatcher.OnFrameworkThread)
        {
            // The record is framework-only; a change landing mid-read must not throw out of Dispose.
            string left;
            try { left = c.Empty ? "" : c.Leftovers(); }
            catch (Exception) { left = "unknown (the record changed while it was read)"; }
            if (left.Length > 0) log.Warning("[Proteus] test harness stopped off the framework thread; not put back: {0}", left);
            return;
        }
        try
        {
            // An apply still settling has half-recorded its session: that part is left as recorded (and logged).
            var r = c.PutBack(skipHalfRecordedBinding: c.ApplySettling);
            log.Information("[Proteus] test harness put back as it stopped: {0}{1}",
                            string.Join(", ", r.Gear.Concat(r.Holds).Select(e => $"{e.Name} {e.Result}{(e.Code is { } code ? $" ({code})" : "")}")),
                            r.Binding is { } b ? $"; binding: glamourer {b.Glamourer}, live {b.Live}, {b.Permanent.Count} permanent" : "");
        }
        catch (Exception e) { log.Warning(e, "[Proteus] test harness: putting back as it stopped failed"); }
    }

    /// <summary>
    /// Plugin unload, possibly off the framework thread. Never waits on a framework hop: a queued after-change sees
    /// <c>disposing</c> and skips. The marker is kept, so the next load starts the harness again.
    /// </summary>
    public void Dispose()
    {
        disposing = true;
        Stop();
        (game as IDisposable)?.Dispose();
    }
}
