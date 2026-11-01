using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>
/// Starting a size set on an installed mod: what the Studio's "Make every size" button does, shared so the QA test
/// harness (Proteus/TestHarness/, dev builds only) runs the identical code. Two halves, on the threads they need:
/// <see cref="Prepare"/> on the framework thread, with Penumbra's answers handed in; <see cref="Start"/> on a worker,
/// which always posts the after-change, however the run ends (except a cancel before the worker started, when nothing
/// was written).
/// </summary>
internal static class SizeSetLaunch
{
    /// <summary>Bodies the ranking may keep cached between runs — as the automatic refit keeps them.</summary>
    internal const int CachedBodiesKept = 400;

    /// <summary>A request ready to run, with the Penumbra answers the worker needs.</summary>
    internal sealed record Prepared(SizeSetRefit.Request Request, IReadOnlyDictionary<string, string> Mods, string ModsRoot);

    /// <summary>What a run came to: the result the panel shows, the outcome of every size, and how long it took.</summary>
    internal sealed record Outcome(SizeSetRefit.Result Result, SizeSetRefit.RunLog RunLog, double Seconds);

    /// <summary>
    /// The request for a run. Framework thread: the game's own bodies are read here (a read that failed on the worker
    /// would leave the automatic refit without the game's body too), and the collection's skeletons are asked for once.
    /// </summary>
    /// <param name="mods">Penumbra's mod list (folder → name), asked for by the caller.</param>
    /// <param name="modsRoot">Penumbra's mod folder.</param>
    /// <param name="resolvePlayerPaths">Penumbra's resolve for the player's collection (IPC); null answers mean none.</param>
    internal static Prepared Prepare(IReadOnlyDictionary<string, string> mods, string modsRoot,
                                     Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string?>?> resolvePlayerPaths,
                                     string modRoot, IReadOnlyList<string> keys, bool yas, bool remake, bool ends,
                                     bool piercings, string? madeOn, Func<string, byte[]?> readGameFile, IPluginLog log)
    {
        var gameBodies = SizeSetRefit.GameBodies(keys, readGameFile, ends);
        // A women's mod's men's versions are made for the men's skeleton the player's collection gives (IVCS when
        // installed), asked for here — IPC — once; the game's own when Penumbra gives nothing.
        SizeSetRefit.AcrossInputs? across = null;
        if (SizeSetRefit.MensKeys(keys).Count > 0)
        {
            var skeletons = new[] { OtherRaceWearer.BaseSkeletonPath(CrossSexRefit.Men),
                                    OtherRaceWearer.BaseSkeletonPath(CrossSexRefit.Women) };
            var resolved = resolvePlayerPaths(skeletons);
            if (resolved == null) log.Information("[Proteus] size set: the collection's skeletons could not be asked for; the game's are used");
            across = new SizeSetRefit.AcrossInputs(path => resolved?.GetValueOrDefault(path));
        }
        var request = new SizeSetRefit.Request(modRoot, keys, yas, remake, [], readGameFile, ends,
                                               race => gameBodies.GetValueOrDefault(race), piercings, across, madeOn);
        return new Prepared(request, mods, modsRoot);
    }

    /// <summary>
    /// Run on a worker. However the run ends — done, failed or stopped mid-run — <paramref name="afterChange"/> is handed
    /// to <paramref name="postToFramework"/> as it ends: a run that threw after its first save must still tell Penumbra.
    /// </summary>
    /// <param name="postToFramework">Puts the after-change on the framework thread. The Studio posts and forgets; the
    /// harness keeps the task to wait on it.</param>
    /// <param name="run">The run itself; <see cref="SizeSetRefit.Run(SizeSetRefit.Request, SizeSetRefit.RunLog, UVRemapService?, IPluginLog, Action{string}, CancellationToken)"/>
    /// unless a test stands in.</param>
    internal static Task<Outcome> Start(Prepared prepared, BodyModIndex index, UVRemapService? uvRemap, IPluginLog log,
                                        Action<string> progress, Action afterChange, Action<Action> postToFramework,
                                        CancellationToken ct,
                                        Func<SizeSetRefit.Request, SizeSetRefit.RunLog, CancellationToken, int>? run = null)
    {
        run ??= (request, runLog, token) => SizeSetRefit.Run(request, runLog, uvRemap, log, progress, token);
        return Task.Run(() =>
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            // A run reads hundreds of megabytes of models: a blocking full collection partway would stop the game too.
            var latency = System.Runtime.GCSettings.LatencyMode;
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
            try
            {
                var bodies = index.RefreshState(prepared.Mods, prepared.ModsRoot).List;
                var runLog = new SizeSetRefit.RunLog();
                int made = run(prepared.Request with { Bodies = bodies }, runLog, ct);
                return new Outcome(SizeSetRefit.ResultOf(runLog, made), runLog, started.Elapsed.TotalSeconds);
            }
            finally
            {
                System.Runtime.GCSettings.LatencyMode = latency;
                BodySizeMatch.ForgetCandidatesBeyond(CachedBodiesKept);
                Post(afterChange, postToFramework, log);
            }
        }, ct);
    }

    /// <summary>Hand the after-change to the framework; a plugin unloading under a run has nothing left to tell.</summary>
    private static void Post(Action afterChange, Action<Action> postToFramework, IPluginLog log)
    {
        try { postToFramework(afterChange); }
        catch (Exception e) { log.Warning(e, "[Proteus] size set: could not reach the framework to reload the mod"); }
    }
}
