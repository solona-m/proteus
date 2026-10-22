using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Proteus.Interop;
using XivLiveMesh;

namespace Proteus.Services;

/// <summary>
/// Watches which hairstyle the player is wearing and, when the game draws its own model because the enabled mod for
/// that hairstyle was made for another race only, ports that race's model onto the wearer's (when the setting is on).
/// Also looks through the player's hair mods before they are worn (<see cref="HairPortScan"/>): every hairstyle when
/// asked to, and a mod's when it is installed, switched on or changed. Off the UI thread, one piece of work at a time.
/// A trigger only: the decisions are <see cref="HairPortPlan"/>'s, <see cref="HairPortBuilder"/>'s and
/// <see cref="HairPortScanSchedule"/>'s, the writes <see cref="HairPortWriter"/>'s.
/// </summary>
public sealed partial class HairPortWatcher : IDisposable
{
    /// <summary>How long to wait after a Penumbra settings change before looking: the event arrives before Penumbra
    /// has finished re-resolving.</summary>
    private const int PenumbraSettleMs = 400;

    /// <summary>How long a burst of signals must go quiet before the draw object is walked; each signal resets it.</summary>
    private const int WalkDebounceMs = 250;

    /// <summary>How long to wait before walking again after a walk found no hair.</summary>
    private const int BlankRetryMs = 500;

    /// <summary>How often to walk again while a check waits to learn the player's race.</summary>
    private const int RaceAskMs = 2000;

    /// <summary>How many consecutive walks may find no hair before believing it: a blank walk is almost always a
    /// character mid-rebuild.</summary>
    internal const int BlanksBeforeNone = 4;

    private readonly CompositorService compositor;
    private readonly PenumbraBridge penumbra;
    private readonly GlamourerBridge glamourer;
    private readonly Configuration config;
    private readonly IPluginLog log;

    /// <summary>One examination, check or undo at a time; a look asked for meanwhile is owed, not dropped.</summary>
    private readonly SingleFlight flight = new();
    private readonly HairPortScanSchedule schedule = new();
    private readonly HairPortScan.Memo memo = new();
    private CancellationTokenSource? checkCts;
    private volatile string? lastKey;
    private long walkDueAt;
    private long raceAskedAt;
    private bool wasEnabled;
    private bool wasWatching;
    private int blanks;
    private volatile IReadOnlyList<string>? livePartsCache;
    private volatile bool disposed;
    /// <summary>The player's collection as the latest walk saw it; null before any. Read on Penumbra's event threads.</summary>
    private volatile string? playerCollection;
    private volatile IReadOnlyDictionary<string, string> modNames = new Dictionary<string, string>();
    private PbdFile? pbd;
    private int tableLogged;

    /// <summary>What the panel draws, replaced wholesale.</summary>
    /// <param name="Worn">The worn hairstyle's game path, or null for none.</param>
    /// <param name="ModRoot">The mod a port of the worn hairstyle lives in, or null.</param>
    /// <param name="Ported">The worn hairstyle is drawn from a port, which can be undone.</param>
    /// <param name="PortsInMod">How many ports that mod has, for the undo-all button.</param>
    /// <param name="Earless">The worn hairstyle is drawn from an earless copy in <paramref name="ModRoot"/>.</param>
    /// <param name="EarlessOptions">The mod's own earless versions of the worn hairstyle, as "Group / Option"
    /// (<see cref="EarlessHairPlan.EarlessOptionHint"/>); null or empty for none.</param>
    /// <param name="Warning">Ported this session with something to look at in game (<see cref="HairPortBaker.Result.Warning"/>).</param>
    public sealed record View(string? Worn = null, string? ModRoot = null, bool Ported = false, int PortsInMod = 0,
                              bool Busy = false, string Message = "", bool Failed = false, bool Earless = false,
                              IReadOnlyList<string>? EarlessOptions = null, string Warning = "");

    private volatile View current = new();
    public View Current => current;

    /// <summary>What the last check of the player's hair mods found, and how far one under way has got.</summary>
    /// <param name="Race">The race the rows are for; 0 before any check.</param>
    /// <param name="Rows">Every hairstyle of that race a mod was made for another race of, by number.</param>
    /// <param name="LastFitted">What the latest check fitted or remade — what "Undo everything this check fitted"
    /// takes back.</param>
    internal sealed record ScanView(bool Running = false, int Done = 0, int Total = 0, ushort Race = 0,
                                  IReadOnlyList<HairPortScan.Row>? Rows = null, string Message = "",
                                  IReadOnlyList<HairPortScan.Row>? LastFitted = null)
    {
        public IReadOnlyList<HairPortScan.Row> List => Rows ?? [];
        public IReadOnlyList<HairPortScan.Row> Last => LastFitted ?? [];
    }

    private volatile ScanView scan = new();
    private readonly object scanGate = new();

    /// <summary>Counts collection changes, so a check that ran across one does not bring back what it cleared.</summary>
    private int collectionGeneration;
    internal ScanView Scan => scan;

    public HairPortWatcher(CompositorService compositor, PenumbraBridge penumbra, GlamourerBridge glamourer,
                           Configuration config, IPluginLog log)
    {
        this.compositor = compositor;
        this.penumbra = penumbra;
        this.glamourer = glamourer;
        this.config = config;
        this.log = log;

        // WHICH HAIRSTYLE: a new game path on the draw object, which only a walk can see.
        glamourer.LocalPlayerCustomizeChanged += RequestWalk;
        glamourer.LocalPlayerCustomizationChanged += RequestWalk;
        glamourer.LocalPlayerStateChanged += RequestWalk;
        compositor.HairChanged += RequestWalk;
        penumbra.LocalPlayerRedrawn += RequestWalk;

        // WHICH FILE serves it: a mod switched on or off changes what the hairstyle resolves to, not its path.
        penumbra.ModSettingChanged += OnModSettingChanged;
        penumbra.PlayerCollectionChanged += OnCollectionChanged;
        // WHICH MODS there are: a hair mod installed is checked before it is worn.
        penumbra.ModAdded += OnModAdded;
        penumbra.ModDeleted += OnModDeleted;

        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        disposed = true;
        // Never waited for: the check may be waiting on this very thread for a reload. A port it saves from here on
        // is not reloaded, and Penumbra reads it at its next load of the mod.
        StopCheck();
        Plugin.Framework.Update -= OnFrameworkUpdate;
        glamourer.LocalPlayerCustomizeChanged -= RequestWalk;
        glamourer.LocalPlayerCustomizationChanged -= RequestWalk;
        glamourer.LocalPlayerStateChanged -= RequestWalk;
        compositor.HairChanged -= RequestWalk;
        penumbra.LocalPlayerRedrawn -= RequestWalk;
        penumbra.ModSettingChanged -= OnModSettingChanged;
        penumbra.PlayerCollectionChanged -= OnCollectionChanged;
        penumbra.ModAdded -= OnModAdded;
        penumbra.ModDeleted -= OnModDeleted;
    }

    private Decide.GateState Gates => Decide.Gates(config.PluginEnabled, penumbra.IsAvailable, config.AutoHairPort,
                                                   config.EarlessMiqoteHair);

    /// <summary>Hair fitting may port: the scan, its schedule and the equip port.</summary>
    private bool Enabled => Gates.Porting;

    /// <summary>The worn hairstyle is looked at: for porting, for the earless setting, and to put ears back.</summary>
    private bool Watching => Gates.Watching;

    private void RequestWalk() => RequestWalkIn(WalkDebounceMs);

    private void RequestWalkIn(int ms)
    {
        if (disposed || !Watching) return;
        Interlocked.Exchange(ref walkDueAt, Environment.TickCount64 + ms);
    }

    private void OnModSettingChanged(Penumbra.Api.Enums.ModSettingChange change, Guid collection, string modDirectory,
                                     bool inherited)
    {
        if (!Watching) return;
        var mine = playerCollection;
        if (Enabled)
            schedule.ModEvent(change, modDirectory, mine == null || mine == collection.ToString("D"), Environment.TickCount64);
        Refresh(delayMs: PenumbraSettleMs);
    }

    private void OnCollectionChanged()
    {
        if (!Watching) return;
        // What the last check found was for the other collection; one under way is for it too.
        Interlocked.Increment(ref collectionGeneration);
        StopCheck();
        UpdateScan(s => s with { Rows = [] });
        Refresh(delayMs: PenumbraSettleMs);
    }

    private void OnModAdded(string modDirectory)
    {
        if (Enabled) schedule.ModAdded(modDirectory, Environment.TickCount64);
    }

    private void OnModDeleted(string modDirectory)
    {
        schedule.ModDeleted(modDirectory);
        UpdateScan(s => s with { Rows = Decide.DropMod(s.List, modDirectory) });
    }

    /// <summary>A mod renamed in Penumbra: its rows go, and a check owed for it is owed under the new folder. Called
    /// by <see cref="ModMoveService"/>.</summary>
    public void OnModMoved(string oldDir, string newDir)
    {
        if (Enabled) schedule.ModMoved(oldDir, newDir, Environment.TickCount64);
        else schedule.ModDeleted(oldDir);
        UpdateScan(s => s with { Rows = Decide.DropMod(s.List, oldDir) });
    }

    /// <summary>Look through every hair mod in the player's collection now, hairstyles unchanged since the last look
    /// included.</summary>
    public void CheckNow()
    {
        if (Enabled) schedule.OweFull(Environment.TickCount64, forced: true);
    }

    /// <summary>The player's Cancel: stop the check under way before its next hairstyle. Ports already saved stay, and
    /// are reloaded. A first-load check stopped so counts as done — "Check now" runs it again.</summary>
    public void CancelCheck()
    {
        userCancelled = true;
        StopCheck();
    }

    /// <summary>Stop the check under way because what it was for changed: the race, the collection, the setting.</summary>
    private void StopCheck() => Volatile.Read(ref checkCts)?.Cancel();

    /// <summary>The check under way was stopped by the player, not by a change.</summary>
    private volatile bool userCancelled;

    /// <summary>The walks a signal asked for, and the checks that are due, on the framework thread, where the draw
    /// object may be read and Penumbra asked.</summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed) return;
        long now = Environment.TickCount64;
        var gates = Gates;
        bool enabled = gates.Porting, watching = gates.Watching;
        var edge = Decide.Edges(wasWatching, wasEnabled, watching, enabled);
        if (edge.RequestWalk) RequestWalk();
        // Looked at again even where nothing resolves differently: with fitting off, the last look kept its key.
        if (edge.LookAfresh) LookAfresh();
        if (edge.ResumePorting)
        {
            // Penumbra back: what waited out a back-off while it was gone is asked for again now.
            Interlocked.Exchange(ref retryMs, 0);
            schedule.Resume(now);
            // The first load with the setting on looks through every mod once; after that, only what changes.
            if (!config.HairPortFullScanDone) schedule.OweFull(now);
            CheckOldCarries();
        }
        if (edge.StopCheck) StopCheck();
        wasEnabled = enabled;
        wasWatching = watching;
        if (!watching) return;

        EarlessTick(now);

        // A check waits to learn the player's race from a walk: make sure one comes.
        if (enabled && schedule.Pending && schedule.Race == 0 && now - raceAskedAt > RaceAskMs)
        {
            raceAskedAt = now;
            if (Interlocked.Read(ref walkDueAt) == 0) RequestWalkIn(0);
        }

        long due = Interlocked.Read(ref walkDueAt);
        if (due != 0 && now >= due)
        {
            Interlocked.CompareExchange(ref walkDueAt, 0, due);
            Walk(now);
        }

        if (enabled && !flight.Busy && schedule.Take(now) is { } work) StartCheck(work);
    }

    /// <summary>
    /// Each mod holding a port that carries its skeleton where the race does not load it — made before the game's table
    /// of hairstyle skeletons was read (<see cref="Decide.ModsWithOldCarries"/>) — checked for the player's race, so it is
    /// remade (or its skeleton taken out) without that hairstyle being worn: such a copy can change another hairstyle of
    /// the race while its mod is on. On a worker: it reads every mod's record.
    /// </summary>
    private void CheckOldCarries()
    {
        var modsRoot = penumbra.GetModDirectory();
        if (string.IsNullOrEmpty(modsRoot)) return;
        Task.Run(() =>
        {
            try
            {
                var mods = Decide.ModsWithOldCarries(Directory.EnumerateDirectories(modsRoot).Where(HairPortRecord.Exists),
                                                     ReadGame);
                if (mods.Count == 0) return;
                log.Information("hair port: {0} mod(s) hold a port to remake for the game's skeletons", mods.Count);
                // Looked at again even where nothing resolves differently since the last check of the session: what
                // changed is how the port is judged, not what it resolves to.
                memo.ForgetResolutions();
                foreach (var dir in mods) schedule.ModAdded(Path.GetFileName(dir), Environment.TickCount64);
            }
            catch (Exception ex) { log.Warning(ex, "hair port: looking for ports to remake failed"); }
        });
    }

    /// <summary>One walk of the draw object. It runs while a port or check is under way too, so a race change stops a
    /// check, and the hairstyle a check may redraw is the one worn now.</summary>
    private void Walk(long now)
    {
        IReadOnlyList<string>? parts;
        try { parts = compositor.HatCompatLiveParts(out _); }
        catch (Exception ex) { log.Error(ex, "hair port: walking the worn hairstyle failed"); return; }

        if (penumbra.GetPlayerCollectionId() is { } collection) playerCollection = collection.ToString("D");
        if (schedule.Walked(Decide.RaceOf(parts), now)) StopCheck();

        if (Decide.Blank(parts, ref blanks)) { RequestWalkIn(BlankRetryMs); return; }
        livePartsCache = parts;
        Refresh();
    }

    /// <summary>
    /// Look again at what is worn, on a worker. A caller that finds an examination running leaves one more look owed,
    /// taken when it ends: a mod switched on or off mid-port changes what the hairstyle resolves to, and no other signal
    /// would bring the watcher back to it.
    /// </summary>
    public void Refresh(int delayMs = 0, bool force = false)
    {
        if (disposed) return;
        if (!flight.TryEnter(delayMs)) return;
        current = current with { Busy = true };
        Task.Run(() =>
        {
            try
            {
                if (delayMs > 0) Thread.Sleep(delayMs);
                if (force) lastKey = null;
                Examine();
            }
            catch (Exception ex)
            {
                log.Error(ex, "hair port: examining the worn hairstyle failed");
                current = current with { Message = ex.Message, Failed = true };
            }
            finally
            {
                current = current with { Busy = false };
                if (flight.Exit(out var owedDelay)) Refresh(owedDelay);
            }
        });
    }

    /// <summary>
    /// One piece of work at a time, without losing a request that arrives meanwhile: <see cref="TryEnter"/> fails while
    /// busy and leaves a request owed, and <see cref="Exit"/> says whether one is — with the longest wait any of the
    /// requests asked for, since a Penumbra settings change is only worth looking at once Penumbra has settled.
    /// </summary>
    internal sealed class SingleFlight
    {
        private int busy;

        /// <summary>The owed request's wait plus one; 0 for none owed.</summary>
        private int owed;

        public bool Busy => Volatile.Read(ref busy) != 0;

        /// <param name="delayMs">How long this request wanted to wait before looking.</param>
        public bool TryEnter(int delayMs = 0)
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) == 0) return true;
            for (int seen = Volatile.Read(ref owed); seen < delayMs + 1;)
            {
                int was = Interlocked.CompareExchange(ref owed, delayMs + 1, seen);
                if (was == seen) break;
                seen = was;
            }
            // The holder may have left between the two steps above, without seeing the request: take it here instead.
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
            Interlocked.Exchange(ref owed, 0);
            return true;
        }

        /// <returns>Whether a request came in while busy, which the caller should now serve after
        /// <paramref name="delayMs"/>.</returns>
        public bool Exit(out int delayMs)
        {
            Interlocked.Exchange(ref busy, 0);
            delayMs = Interlocked.Exchange(ref owed, 0) - 1;
            if (delayMs < 0) { delayMs = 0; return false; }
            return true;
        }
    }

    /// <summary>A check of the player's hair mods, on a worker, holding the flight: the equip path waits for it.</summary>
    private void StartCheck(HairPortScanSchedule.Work work)
    {
        if (!flight.TryEnter())
        {
            // Lost to a look that started this frame: the check is owed again, not dropped.
            schedule.Return(work, Environment.TickCount64);
            return;
        }
        modNames = penumbra.GetAllMods() ?? new Dictionary<string, string>();
        var cts = new CancellationTokenSource();
        Volatile.Write(ref checkCts, cts);
        int generation = Volatile.Read(ref collectionGeneration);
        userCancelled = false;
        current = current with { Busy = true };
        UpdateScan(s => s with { Running = true, Done = 0, Total = 0, Message = "" });
        Task.Run(() =>
        {
            try
            {
                var host = new Host(this);
                var modsRoot = host.ModsRoot;
                if (string.IsNullOrEmpty(modsRoot))
                {
                    // Penumbra is not there to ask: the check is owed until it is.
                    schedule.Return(work, Environment.TickCount64 + NextRetry());
                    return;
                }
                var ids = work.Full
                    ? HairPortScan.IdsForFull(work.Race, host.GameHas)
                    : work.Mods.SelectMany(d => HairPortScan.IdsInMod(HairPortPlan.ReadOwners(Path.Combine(modsRoot, d)), work.Race))
                               .Distinct().ToList();
                string label = work.Full ? "full" : "mod " + string.Join(", ", work.Mods);
                if (ids.Count == 0) { log.Debug("hair port: {0} — no hairstyle to look at", label); return; }

                var worn = Decide.WornHair(livePartsCache);
                var result = HairPortScan.Run(host, work.Race, ids, worn, memo, work.LooksAgain, label, cts.Token,
                                              (d, t) => UpdateScan(s => s with { Done = d, Total = t }));
                if (result.Failed)
                {
                    int wait = NextRetry();
                    log.Warning("hair port: {0} — Penumbra could not resolve the hairstyles; trying again in {1} s",
                                label, wait / 1000);
                    schedule.Return(work, Environment.TickCount64 + wait);
                    return;
                }
                retryMs = 0;
                // A collection change while it ran: what it found is the other collection's, which was cleared.
                bool stale = generation != Volatile.Read(ref collectionGeneration);
                UpdateScan(s => Decide.Merge(s, work.Race, result, stale));
                if (result.Written.Count > 0) lastKey = null;
                // Stopped by a change of race or collection: what was owed still is, for the new one.
                if (Decide.OweAgain(result.Cancelled, userCancelled, disposed, config.PluginEnabled && config.AutoHairPort))
                    schedule.Return(work, Environment.TickCount64);
                if (work.Full && (!result.Cancelled || userCancelled) && !config.HairPortFullScanDone)
                {
                    config.HairPortFullScanDone = true;
                    config.Save();
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, "hair port: checking the hair mods failed");
                UpdateScan(s => s with { Message = ex.Message });
            }
            finally
            {
                Interlocked.CompareExchange(ref checkCts, null, cts);
                UpdateScan(s => s with { Running = false });
                current = current with { Busy = false };
                if (flight.Exit(out var owedDelay)) Refresh(owedDelay);
            }
        });
    }

    /// <summary>How long the next check that cannot ask Penumbra waits; 0 after one that could.</summary>
    private int retryMs;

    private int NextRetry() => retryMs = Decide.NextRetry(retryMs);

    /// <summary>Change what the panel shows of the checks: from the worker, Penumbra's event threads and the UI alike,
    /// one change at a time, so none is lost.</summary>
    private void UpdateScan(Func<ScanView, ScanView> change)
    {
        lock (scanGate) scan = change(scan);
    }

    private void Examine()
    {
        string? key = Decide.KeyForLook(lastKey, Interlocked.Exchange(ref lookAfresh, 0) != 0);
        var view = Run(new Host(this), Decide.WornHair(livePartsCache), ref key);
        lastKey = key;
        if (!ReferenceEquals(view, Unchanged)) current = Decide.WithNotice(view, restoreNotice);
    }

    /// <summary>What the examination needs from the plugin, so the examination itself can be tested.</summary>
    internal interface IHost
    {
        bool Enabled { get; }
        string? ModsRoot { get; }
        string? Resolve(string gamePath);
        /// <summary>The game's racial deformer; null when it cannot be read.</summary>
        PbdFile? Pbd();
        byte[]? ReadGame(string gamePath);
        void Log(string message);
        void Announce();
        void Reload(string modDir);
        /// <summary>The hairstyle the latest walk saw, which may have changed while a port ran.</summary>
        string? WornNow();
        void Redraw();

        /// <summary>Every path of a check resolved for the player at once; null when Penumbra could not be asked.</summary>
        IReadOnlyDictionary<string, string?>? ResolveAll(IReadOnlyList<string> gamePaths)
        {
            var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in gamePaths) map[p] = Resolve(p);
            return map;
        }

        /// <summary>Whether the game has a file, without reading it.</summary>
        bool GameHas(string gamePath) => ReadGame(gamePath) != null;

        /// <summary>A mod's name as Penumbra shows it; its folder when unknown.</summary>
        string ModName(string modDir) => modDir;

        /// <summary>Post a check's one chat line.</summary>
        void AnnounceScan(string line) { }

        /// <summary>An equip port was saved in <paramref name="modDir"/>: look at its other hairstyles too.</summary>
        void CheckMod(string modDir) { }

        /// <summary>"Take the ears off Miqo'te hair mods" is on (and Proteus, and Penumbra).</summary>
        bool Earless => false;

        /// <summary>Proteus is on and Penumbra there: off, a look writes nothing at all — not even ears put back.</summary>
        bool Watching => true;

        /// <summary>Post an earless conversion's chat line.</summary>
        void AnnounceEarless(string line) { }

        /// <summary>The worn hairstyle's mod could not be read: look again after the earless back-off.</summary>
        void RequestLookAgain() { }

        /// <summary>A look got an answer from the mod: the earless back-off starts over.</summary>
        void EarlessSettled() { }

        /// <summary>A line for the log that something did not come out as it should.</summary>
        void Warn(string message) => Log(message);
    }

    private sealed class Host(HairPortWatcher w) : IHost
    {
        public bool Enabled => w.Enabled;
        public string? ModsRoot => w.penumbra.GetModDirectory();
        public string? Resolve(string gamePath) => w.penumbra.ResolvePlayer(gamePath);
        public PbdFile? Pbd() => w.Pbd();
        public byte[]? ReadGame(string gamePath)
        {
            var bytes = HairPortWatcher.ReadGame(gamePath);
            // Once a session: whether the table every skeleton decision rests on could be read at all.
            if (gamePath == HairSkeletonTable.GamePath && Interlocked.Exchange(ref w.tableLogged, 1) == 0)
                w.log.Information("hair port: the game's table of hairstyle skeletons — {0}",
                                  HairSkeletonTable.Parse(bytes) is { } t ? $"{t.Count} entries" : "could not be read");
            return bytes;
        }
        public void Log(string message) => w.log.Information("hair port: {0}", message);
        public void Announce() => w.Announce(null);
        public void Reload(string modDir)
        {
            if (w.disposed) return;
            w.schedule.OwnReload(modDir, Environment.TickCount64);
            w.compositor.ExpectOwnModEdit(modDir);
            var ec = w.penumbra.ReloadModDirectory(modDir);
            w.log.Information("hair port: asked Penumbra to reload {0} — {1}", modDir, ec);
        }
        public string? WornNow() => Decide.WornHair(w.livePartsCache);
        // Not deferred into an auto refit batch's hold: only that batch's own work waits for its one redraw.
        public void Redraw()
        {
            if (!w.disposed) w.compositor.RedrawForChangedModel("hair port");
        }

        // On the framework thread, where Penumbra resolves for the player — all of a check's paths in one call.
        public IReadOnlyDictionary<string, string?>? ResolveAll(IReadOnlyList<string> gamePaths)
            => Plugin.Framework.RunOnFrameworkThread(() => w.penumbra.ResolvePlayerPaths(gamePaths)).GetAwaiter().GetResult();
        public bool GameHas(string gamePath)
        {
            try { return Plugin.DataManager.FileExists(gamePath); }
            catch (Exception) { return false; }
        }
        public string ModName(string modDir) => w.modNames.TryGetValue(modDir, out var n) && n.Length > 0 ? n : modDir;
        public void AnnounceScan(string line) => w.Announce(line);
        public void CheckMod(string modDir)
        {
            if (w.Enabled) w.schedule.ModAdded(modDir, Environment.TickCount64);
        }
        public bool Earless => w.Gates.Earless;
        public bool Watching => w.Watching;
        public void AnnounceEarless(string line) => w.Announce(line);
        // The back-off lives on the watcher: a host is made for every look.
        public void RequestLookAgain() => w.LookAgainLater();
        public void EarlessSettled() => w.earlessBackoff.Reset();
        public void Warn(string message) => w.log.Warning("hair port: {0}", message);
    }

    /// <summary>
    /// One examination of the worn hairstyle <paramref name="worn"/>: what it resolves to, whether a mod made it for
    /// another race only, and — with the feature on — the port, its save, the chat line, the reload and the redraw.
    /// </summary>
    /// <param name="lastKey">What the last examination saw; an unchanged one returns at once.</param>
    internal static View Run(IHost host, string? worn, ref string? lastKey)
    {
        var modsRoot = host.ModsRoot;
        if (worn == null || string.IsNullOrEmpty(modsRoot)) return new View();
        var (race, id) = HairPortPlan.Parse(worn)!.Value;

        // Proteus off (a refresh can still be asked for from the settings): nothing is written into any mod.
        if (!host.Watching)
        {
            // Nothing was judged: once Proteus is back, the same hairstyle is looked at, not taken as unchanged.
            lastKey = null;
            return new View(worn);
        }

        // Neither fitting nor the earless setting on: all there can be to do is put back ears an earless copy took off
        // — one left by a config lost, an older build, or a mod imported with it — so only the worn path is resolved.
        if (!host.Enabled && !host.Earless)
        {
            var served = host.Resolve(worn);
            var only = $"{worn}>{served}";
            if (only == lastKey) return Unchanged;
            lastKey = only;
            return EarlessRestoreAtEquip(host, modsRoot!, worn, race, served, ref lastKey)
                   ?? new View(worn, EarlessOptions: EarlessHint(modsRoot!, worn, race, served));
        }

        // Cheap on purpose: Penumbra's settings events come in bursts. What the worn hairstyle and every same-sex
        // race's copy of it resolve to is everything a decision below reads.
        var candidates = HairPortPlan.OrderSources(race, HairPortPlan.SameGenderRaces(race)).ToList();
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [worn] = host.Resolve(worn) };
        foreach (var r in candidates) resolved[HairPortPlan.HairPath(r, id)] = host.Resolve(HairPortPlan.HairPath(r, id));
        var key = string.Join("|", resolved.Select(p => $"{p.Key}>{p.Value}"));
        if (key == lastKey) return Unchanged;
        lastKey = key;

        var decision = Decide.What(worn, resolved, candidates, modsRoot!, HairPortRecord.Read, host.ReadGame);
        switch (decision.Kind)
        {
            case Decide.Kind.None:
                if (decision.Message.Length > 0) host.Log(decision.Message);
                // Drawn from a mod made for the wearer's race: whether its ears come off is the earless setting's.
                var file = resolved[worn];
                return (host.Earless ? EarlessStep(host, modsRoot!, worn, race, file, ref lastKey)
                                     : EarlessRestoreAtEquip(host, modsRoot!, worn, race, file, ref lastKey))
                       ?? new View(worn, Message: decision.Message, EarlessOptions: EarlessHint(modsRoot!, worn, race, file));
            case Decide.Kind.Ported:
                // A port made earlier this session says again what it warned of (HairPortScan.WarningFor).
                return new View(worn, decision.ModRoot, Ported: true, PortsInMod: HairPortRecord.Read(decision.ModRoot!).Ports.Count,
                                Warning: HairPortScan.WarningFor(decision.ModRoot, worn));
        }

        // Port (or remake a stale port of) the worn hairstyle from decision.Source in decision.ModRoot.
        // Fitting off (the earless setting on): the key is kept, so the next walk does not resolve every race again —
        // switching fitting on looks afresh (Decide.Edges, Decide.KeyForLook).
        if (!host.Enabled) return new View(worn);
        var modRoot = decision.ModRoot!;
        string modDir = Path.GetFileName(modRoot);
        host.Log($"{worn} is drawn as the game's own — porting {decision.Source} from "
               + $"{(decision.Stale ? "an out-of-date port" : "the author's model")} in {modDir}");

        if (host.Pbd() is not { } pbd)
        {
            host.Log($"{worn} not ported — {NoPbd}");
            return new View(worn, modRoot, Message: NoPbd, Failed: true);
        }
        var ported = HairPortScan.PortOne(host, modRoot, decision, race, pbd);
        if (ported.Writes.Count == 0)
            return new View(worn, modRoot, Message: ported.Message, Failed: ported.Message.Length > 0);
        if (ported.Stripped)
        {
            // Not ported again, but its old skeleton no longer changes another hairstyle: loaded, and said why not.
            ReloadAndRedraw(host, modDir, worn);
            lastKey = null;
            return new View(worn, modRoot, Message: ported.Message, Failed: true);
        }

        host.Announce();
        ReloadAndRedraw(host, modDir, worn);
        // The mod's other hairstyles for this race are fitted too, as a check of the mod would.
        host.CheckMod(modDir);
        lastKey = null;
        return new View(worn, modRoot, Ported: true, PortsInMod: HairPortRecord.Read(modRoot).Ports.Count,
                        Warning: ported.Warning);
    }

    /// <summary>Returned for an examination that found nothing changed: the panel keeps what it shows.</summary>
    internal static readonly View Unchanged = new();

    /// <summary>
    /// Make Penumbra re-read the mod, then redraw so the game loads the hairstyle again — skipped when a different
    /// hairstyle went on meanwhile, which its own walk handles. For a port and an undo alike.
    /// </summary>
    internal static void ReloadAndRedraw(IHost host, string modDir, string worn)
    {
        host.Reload(modDir);
        if (host.WornNow() is not { } now || string.Equals(now, worn, StringComparison.OrdinalIgnoreCase)) host.Redraw();
    }

    /// <summary>Undo the port the worn hairstyle is drawn from, and keep it undone.</summary>
    public void Undo()
    {
        var view = current;
        if (view.ModRoot == null || view.Worn == null) return;
        var worn = view.Worn;
        RunUndo([(view.ModRoot, e => e.TargetGamePath.Equals(worn, StringComparison.OrdinalIgnoreCase))]);
    }

    /// <summary>Undo every port in the worn hairstyle's mod, and keep them undone.</summary>
    public void UndoAll()
    {
        var view = current;
        if (view.ModRoot == null || view.Worn == null) return;
        RunUndo([(view.ModRoot, _ => true)]);
    }

    /// <summary>Undo one row of the last check, and keep it undone.</summary>
    internal void UndoRow(HairPortScan.Row row)
        => RunUndo([(row.ModRoot, e => e.TargetGamePath.Equals(row.TargetGamePath, StringComparison.OrdinalIgnoreCase))]);

    /// <summary>Undo every port the last check fitted or remade, and keep them undone.</summary>
    public void UndoChecked()
    {
        var jobs = Decide.CheckedUndos(scan.Last);
        if (jobs.Count > 0) RunUndo(jobs);
    }

    /// <summary>The undo of some ports in some mods, each mod reloaded once and the player redrawn when the hairstyle
    /// worn was one of them.</summary>
    private void RunUndo(IReadOnlyList<(string ModRoot, Func<HairPortRecord.Entry, bool> Which)> jobs)
    {
        if (current.Busy || scan.Running) return;
        if (!flight.TryEnter()) return;
        current = current with { Busy = true };
        Task.Run(() =>
        {
            try
            {
                var host = new Host(this);
                var worn = Decide.WornHair(livePartsCache);
                var undone = new List<string>();
                string message = "";
                bool failed = false;
                foreach (var (modRoot, which) in jobs)
                {
                    var before = HairPortRecord.Read(modRoot).Ports.Select(e => e.TargetGamePath).ToList();
                    var outcome = HairPortWriter.Undo(modRoot, which, decline: true);
                    if (!outcome.Ok) { message = outcome.Message; failed = true; continue; }
                    var after = HairPortRecord.Read(modRoot).Ports.Select(e => e.TargetGamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    undone.AddRange(before.Where(t => !after.Contains(t)));
                    log.Information("hair port: undid {0} port(s) in {1}", outcome.Count, Path.GetFileName(modRoot));
                    host.Reload(Path.GetFileName(modRoot));
                    if (!failed) message = outcome.Message;
                }
                if (worn != null && undone.Contains(worn, StringComparer.OrdinalIgnoreCase)
                    && (host.WornNow() is not { } now || string.Equals(now, worn, StringComparison.OrdinalIgnoreCase)))
                    host.Redraw();
                lastKey = null;
                UpdateScan(s => s with { Rows = Decide.MarkUndone(s.List, undone),
                                         LastFitted = Decide.MarkUndone(s.Last, undone) });
                current = new View(worn, Message: message, Failed: failed);
            }
            catch (Exception ex)
            {
                log.Error(ex, "hair port: undoing failed");
                current = current with { Message = ex.Message, Failed = true };
            }
            finally
            {
                current = current with { Busy = false };
                if (flight.Exit(out var owedDelay)) Refresh(owedDelay);
            }
        });
    }

    /// <summary>The game's own deformer — never a modded one, so a port that travels with the mod is not shaped by one
    /// user's pbd mod.</summary>
    private PbdFile? Pbd()
    {
        if (pbd != null) return pbd;
        try { return pbd = Plugin.DataManager.GetFile(LiveCharacter.PbdGamePath) is { } f ? new PbdFile(f.Data) : null; }
        catch (Exception ex)
        {
            log.Warning(ex, "hair port: the game's racial deformer could not be read");
            return null;
        }
    }

    /// <summary>Why nothing can be ported without the deformer.</summary>
    internal static string NoPbd
        => CheapLoc.Loc.Localize("HairPort.Refused.NoPbd", "the game's racial deformer could not be read");

    private static byte[]? ReadGame(string gamePath)
    {
        try { return Plugin.DataManager.GetFile(gamePath)?.Data; }
        catch (Exception) { return null; }
    }

    /// <summary>A chat line: an equip port's (<paramref name="line"/> null) or a check's.</summary>
    private void Announce(string? line)
    {
        if (disposed) return;
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                if (disposed) return;
                Plugin.ChatGui.Print(new Dalamud.Game.Text.SeStringHandling.SeStringBuilder()
                    .AddUiForeground(line ?? CheapLoc.Loc.Localize("Chat.HairPort",
                        "[Proteus] This hairstyle's mod was made for another race, so Proteus has fitted it to yours and "
                      + "added that to the mod — everyone drawn with this mod on now sees it. You can undo that, or turn "
                      + "the feature off, under /proteus > Settings > Hair."), 45)
                    .Build());
            }
            catch (Exception ex) { log.Warning(ex, "hair port: could not announce the port in chat"); }
        });
    }

    /// <summary>The watcher's decisions, apart from its threads and IPC, so they can be tested.</summary>
    internal static partial class Decide
    {
        /// <summary>The hairstyle among a walk's models; null for none.</summary>
        public static string? WornHair(IReadOnlyList<string>? parts)
            => parts?.FirstOrDefault(p => HairPortPlan.Parse(p) != null);

        /// <summary>The player's head race from a walk's models: the hairstyle's, else the face's (a hat can hide the
        /// hair); 0 when neither is drawn.</summary>
        public static ushort RaceOf(IReadOnlyList<string>? parts)
        {
            if (WornHair(parts) is { } hair) return HairPortPlan.Parse(hair)!.Value.Race;
            foreach (var p in parts ?? [])
                if (FaceModel().Match(p.Replace('\\', '/')) is { Success: true } m && m.Groups[1].Value == m.Groups[3].Value)
                    return ushort.Parse(m.Groups[1].Value);
            return 0;
        }

        [GeneratedRegex(@"^chara/human/c(\d{4})/obj/face/f(\d{4})/model/c(\d{4})f\d{4}_fac\.mdl$", RegexOptions.IgnoreCase)]
        private static partial Regex FaceModel();

        /// <summary>Whether a walk found no hair and should be retried rather than believed, counting the run.</summary>
        public static bool Blank(IReadOnlyList<string>? parts, ref int blanks)
        {
            if (WornHair(parts) != null) { blanks = 0; return false; }
            return ++blanks < BlanksBeforeNone;
        }

        /// <summary>The undos "Undo everything the last check fitted" makes: per mod, the ports its rows fitted.</summary>
        public static List<(string ModRoot, Func<HairPortRecord.Entry, bool> Which)> CheckedUndos(IReadOnlyList<HairPortScan.Row> rows)
            => rows.Where(r => r.State is HairPortScan.RowState.Fitted or HairPortScan.RowState.Remade)
                   .GroupBy(r => r.ModRoot, StringComparer.OrdinalIgnoreCase)
                   .Select(g =>
                   {
                       var targets = g.Select(r => r.TargetGamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                       return (g.Key, (Func<HairPortRecord.Entry, bool>)(e => targets.Contains(e.TargetGamePath)));
                   })
                   .ToList();

        /// <summary>
        /// A check's rows over the last ones: the hairstyles it looked at are replaced, the rest kept — a check of one mod
        /// leaves the others' rows alone, and a hairstyle skipped as unchanged keeps its row. Another race's rows are
        /// dropped. A check <paramref name="stale"/> from a collection change since it began changes nothing.
        /// </summary>
        public static ScanView Merge(ScanView view, ushort race, HairPortScan.Result result, bool stale)
        {
            if (stale) return view;
            var keep = view.Race == race ? view.List.Where(r => !result.Examined.Contains(r.Id)) : [];
            return view with
            {
                Race = race,
                Rows = keep.Concat(result.Rows).OrderBy(r => r.Id).ToList(),
                LastFitted = result.Rows.Where(r => r.State is HairPortScan.RowState.Fitted or HairPortScan.RowState.Remade).ToList(),
            };
        }

        /// <summary>Whether a check that stopped is owed again: not when the player stopped it, or the setting or the
        /// plugin went off — when the race or the collection it was for changed, or Penumbra went away for a while.</summary>
        /// <param name="settingOn">Proteus and hair fitting are on — Penumbra's being there aside.</param>
        public static bool OweAgain(bool cancelled, bool byPlayer, bool disposed, bool settingOn)
            => cancelled && !byPlayer && !disposed && settingOn;

        /// <summary>The wait before the next try of a check Penumbra could not answer: 5 s, doubling each time it
        /// still cannot, up to 5 minutes — so a Penumbra that never answers is not asked every few seconds all session.</summary>
        public static int NextRetry(int last) => last <= 0 ? 5000 : Math.Min(last * 2, 300_000);

        /// <summary>
        /// The mods among <paramref name="modRoots"/> holding a port made before the game's table of hairstyle skeletons
        /// was read — a skeleton copied to the hairstyle's own number, with no entry — that is out of date by that table.
        /// One the race really loads, and nothing else of it does, works and is left.
        /// </summary>
        public static List<string> ModsWithOldCarries(IEnumerable<string> modRoots, Func<string, byte[]?> readGame)
        {
            var table = HairSkeletonTable.Lazy(readGame);
            var mods = new List<string>();
            foreach (var root in modRoots)
            {
                var record = HairPortRecord.Read(root);
                var old = record.Ports.Where(e => e is { Est: 0, SkeletonFrom: > 0 }).ToList();
                if (old.Count == 0) continue;
                var owners = HairPortPlan.ReadOwners(root);
                if (old.Any(e => HairPortBuilder.ReadMod(root, e.SourceFile) is { } source
                                 && HairPortBuilder.IsOutOfDate(root, e, source, owners, record.Ports, readGame, table)))
                    mods.Add(root);
            }
            return mods;
        }

        /// <summary>The rows left once a mod is deleted: none of its own.</summary>
        public static List<HairPortScan.Row> DropMod(IReadOnlyList<HairPortScan.Row> rows, string modDir)
            => rows.Where(r => !r.ModDir.Equals(modDir, StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>Rows whose port was just undone, shown so.</summary>
        public static List<HairPortScan.Row> MarkUndone(IReadOnlyList<HairPortScan.Row> rows, IReadOnlyCollection<string> undone)
            => rows.Select(r => undone.Contains(r.TargetGamePath, StringComparer.OrdinalIgnoreCase)
                                    ? r with { State = HairPortScan.RowState.Undone, Message = "" } : r).ToList();

        public enum Kind { None, Ported, Port }

        /// <param name="Source">For <see cref="Kind.Port"/>: the game path of the model to port.</param>
        /// <param name="Stale">The worn hairstyle is drawn from a port of ours that is out of date.</param>
        public sealed record Decision(Kind Kind, string? ModRoot = null, string? Source = null, bool Stale = false,
                                      string Message = "");

        /// <summary>
        /// What to do about the worn hairstyle, from what it and every same-sex race's copy of it resolve to.
        /// </summary>
        /// <param name="resolved">Game path to the file Penumbra serves for the player (null or not under the mods root:
        /// the game's own).</param>
        /// <param name="candidates">The races to port from, best first (<see cref="HairPortPlan.OrderSources"/>).</param>
        /// <param name="readGame">Reads the game's own files, to judge a port of ours against the skeleton its race
        /// loads.</param>
        public static Decision What(string worn, IReadOnlyDictionary<string, string?> resolved,
                                    IReadOnlyList<ushort> candidates, string modsRoot,
                                    Func<string, HairPortRecord> readRecord, Func<string, byte[]?> readGame)
        {
            var (_, id) = HairPortPlan.Parse(worn)!.Value;
            if (resolved.GetValueOrDefault(worn) is { } file && HatCompatService.InMods(file, modsRoot, out var modRoot, out var rel))
            {
                // Already modded for the wearer's race: the author's, another mod's — or a port of ours.
                if (!HairPortPlan.IsPortFile(rel)) return new Decision(Kind.None);
                var record = readRecord(modRoot);
                var entry = record.Ports.FirstOrDefault(e => e.File.Equals(HairPortRecord.Rel(rel), StringComparison.OrdinalIgnoreCase));
                if (entry == null) return new Decision(Kind.Ported, modRoot);
                // Another mod now wins the race the port was made from — one installed or switched on since: port that
                // one too. Its port wins the wearer's path the way its source wins the source's, and this one stays
                // for when that mod is off again.
                // Only a mod covering the same race: one made for another race has a port that loses to this one, by
                // the same priority this one's source won by, and would never be seen.
                if (Source() is { Kind: Kind.Port } other
                    && !string.Equals(other.ModRoot, modRoot, StringComparison.OrdinalIgnoreCase)
                    && HairPortPlan.Parse(other.Source!)?.Race == HairPortPlan.Parse(entry.SourceGamePath)?.Race)
                    return other;
                byte[]? source = null;
                try { source = File.ReadAllBytes(Path.Combine(modRoot, entry.SourceFile.Replace('/', Path.DirectorySeparatorChar))); }
                catch (Exception) { /* the author's file is gone: nothing to remake it from */ }
                // Judged on each look that gets this far — but a borrow changed in the manifest alone resolves no path
                // differently, so the look that sees it is one a different resolution brings: another hairstyle, the
                // mod switched off and on, the feature switched on again, or a plugin reload. As for a changed model.
                return source != null && HairPortBuilder.IsOutOfDate(modRoot, entry, source, HairPortPlan.ReadOwners(modRoot), record.Ports,
                                                                     readGame)
                    ? new Decision(Kind.Port, modRoot, entry.SourceGamePath, Stale: true)
                    : new Decision(Kind.Ported, modRoot);
            }
            return Source() ?? new Decision(Kind.None);

            // The mod model the wearer's race would be ported from: the nearest race a mod's author covers.
            Decision? Source()
            {
                foreach (var race in candidates)
                {
                    var path = HairPortPlan.HairPath(race, id);
                    if (resolved.GetValueOrDefault(path) is not { } f || !HatCompatService.InMods(f, modsRoot, out var root, out var r))
                        continue;
                    // Our own port of another race is never a source: the author's model is.
                    if (HairPortPlan.IsPortFile(r)) continue;
                    if (readRecord(root).Declined.Contains(HairPortPlan.DeclineKey(worn)))
                        return new Decision(Kind.None, root, Message: string.Format(CheapLoc.Loc.Localize(
                            "HairPort.Declined.Fmt", "The port of hairstyle {0} in {1} was undone, so it is left alone."),
                            $"h{id:D4}", Path.GetFileName(root)));
                    return new Decision(Kind.Port, root, path);
                }
                return null;
            }
        }
    }
}
