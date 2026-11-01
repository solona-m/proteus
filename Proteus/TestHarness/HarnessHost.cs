using System;
using System.IO;
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
                try { File.WriteAllText(HarnessSessionFile.MarkerIn(configDir), "on"); }
                catch (Exception e) { log.Warning(e, "[Proteus] test harness: could not write the autostart marker"); }
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
        string session = HarnessJobs.NewJobId();
        token = HarnessAuth.NewToken();
        Names = new SessionNames();
        var ring = new LogRing(Names);
        jobs = new HarnessJobs(game, dispatcher, new ModBackup(BackupsRoot, backupIds), session, () => disposing, timings);
        var router = new HarnessRouter(jobs, ring, Names, new CaptureGate(timings.Capture), build, buildStamp, log);
        var s = new HarnessServer(router.Handle, token);
        s.Start();
        server = s;
        setLineObserver(ring.Add);
        game.NameSink = Names;
        HarnessSessionFile.Write(configDir, new HarnessSession(s.Port, token, Environment.ProcessId, build, buildStamp,
                                                               session, DateTime.UtcNow));
        // The port and session only: the token is never logged.
        log.Information("[Proteus] test harness listening on 127.0.0.1:{0} (session {1})", s.Port, session);
        return $"[Proteus] Test harness listening on 127.0.0.1:{s.Port}. QA reads {HarnessSessionFile.FileName} in the plugin's config folder.";
    }

    private void Stop()
    {
        if (server == null) return;
        jobs?.CancelRunning();
        setLineObserver(null);
        game.NameSink = null;
        server.Dispose();
        server = null;
        if (jobs != null) HarnessSessionFile.DeleteIfOwn(configDir, jobs.Session);
        log.Information("[Proteus] test harness stopped");
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
