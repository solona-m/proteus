using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CheapLoc;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Proteus.Interop;
using Proteus.Localization;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;
using Worn = Proteus.Services.AutoRefitDecisions.Worn;

namespace Proteus.Services;

/// <summary>
/// Refits chest, legs, hands and feet gear onto the collection's preferred body as it is put on — the Studio's Body size
/// tool, run unattended.
/// <para/>
/// When a slot starts drawing a different piece, this works out which installed body (or the game's own) and which
/// size the piece was made on, refits it — and the other parts of the body it reaches — onto the sizes chosen for the
/// collection in Settings, saves the refit into the piece's own mod as the panel would, and switches it on. Chat says so
/// when it starts and when it is done, or why it left the piece alone.
/// <para/>
/// Only the change of piece acts. A different OPTION of the same mod (the author's own sizes, the refit group's
/// "Original") is the player choosing, and is never overridden; and the first look after loading, after switching
/// the feature on, or after a collection change only takes note of what is worn, so logging in refits nothing.
/// <para/>
/// Every file read and every refit runs on a worker; Penumbra IPC and the character are touched on the framework thread,
/// in short hops. One refit runs at a time.
/// </summary>
public sealed class AutoRefitWatcher : IDisposable
{
    /// <summary>Glamourer signals an equip before the new model has loaded; a walk sooner finds the old one.</summary>
    private const int EquipSettleMs = 750;

    /// <summary>A redraw comes in bursts; one walk once they go quiet.</summary>
    private const int RedrawDebounceMs = 250;

    /// <summary>A walk that caught the character mid-rebuild tries again this soon.</summary>
    private const int BlankRetryMs = 500;

    /// <summary>How long a piece this watcher just switched on is left alone, whatever a walk makes of it.</summary>
    private const int OwnChangeQuietMs = 5000;

    /// <summary>Mods arrive and go in bursts (an import, a folder move): the index is re-read once they stop.</summary>
    private const int IndexDebounceMs = 2000;

    /// <summary>How long to wait for a mod made for the game's own gear to be registered and switched on.</summary>
    private const int MadeModTimeoutMs = 15_000;

    /// <summary>
    /// Bodies the ranking may keep cached between refits — see <see cref="BodySizeMatch.ForgetCandidatesBeyond"/>. High
    /// enough to hold every part of one large pack (Neolithe's chests alone are 114): a cache cleared after every refit
    /// made each change of gear read the whole pack again, which is what froze the game.
    /// </summary>
    private const int CachedBodiesKept = 400;

    private readonly CompositorService compositor;
    private readonly PenumbraBridge penumbra;
    private readonly GlamourerBridge glamourer;
    private readonly UVRemapService uvRemap;
    private readonly Func<string, byte[]?> readGameFile;
    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly RefitModService refitMods;

    /// <summary>The installed body mods, for the settings and for every refit.</summary>
    internal BodyModIndex Index { get; } = new();

    private volatile bool disposed;
    private bool wasEnabled;

    /// <summary>When the next walk of the character is due, as a tick count, or zero for none.</summary>
    private long walkDueAt;

    /// <summary>When the body-mod index is next due to be re-read, or zero for not.</summary>
    private long indexDueAt;
    private int indexing;

    /// <summary>What each slot drew at the last walk; null before the first, which only takes note. Framework thread.</summary>
    private Dictionary<string, Worn>? lastSeen;

    /// <summary>The collection <see cref="lastSeen"/> was taken in: another one is another character's look.</summary>
    private Guid? seenIn;

    /// <summary>The next walk queues every slot, changed or not — "Refit what I'm wearing now".</summary>
    private volatile bool forceAll;

    private int blanks;

    /// <summary>Pieces this watcher switched on itself, by slot and game path, until the tick count given.</summary>
    private readonly ConcurrentDictionary<string, long> ours = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Collections already told their body mod is missing, so a session of equips says it once.</summary>
    private readonly ConcurrentDictionary<Guid, byte> toldMissing = new();

    /// <summary>Pieces whose failure has already been put in chat this session; another failure of one goes to the log.</summary>
    private readonly ConcurrentDictionary<string, byte> toldFailed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What garments were read as, on disk — see <see cref="DetectionCache"/>.</summary>
    private readonly DetectionCache detections;

    /// <summary>
    /// The pieces refitted (or switched back to a refit) since the queue last ran dry, with the body they now fit. Said
    /// in ONE chat line when it does: a design changing four slots used to print eight. Guarded by <see cref="queueGate"/>.
    /// </summary>
    private readonly List<(string Item, string Body)> finished = [];

    private readonly object queueGate = new();
    private readonly Dictionary<string, Request> queue = new(StringComparer.Ordinal);
    private Task? runner;
    private CancellationTokenSource? running;
    private string? runningSlot;

    /// <summary>The racial deformer, read once.</summary>
    private XivLiveMesh.PbdFile? pbd;
    private bool pbdTried;
    private Func<int, int, string?>? itemNames;

    /// <summary>What the settings show under the feature: the last thing it did, and whether it is working now.</summary>
    public sealed record View(string Message = "", bool Failed = false, bool Busy = false);

    private volatile View current = new();
    public View Current => current;

    /// <param name="Collection">The collection the piece was put on in — where the refit is switched on.</param>
    /// <param name="Resolved">The file the game draws, or null for the game's own gear.</param>
    /// <param name="ModRoot">The mod supplying it, or null for the game's own gear.</param>
    /// <param name="Forced">Asked for with "Refit what I'm wearing now": the player's earlier choice of another size is
    /// overridden, since they asked for exactly that.</param>
    private sealed record Request(string Slot, Worn Worn, string? Resolved, string? ModRoot, Guid Collection,
                                 bool Forced = false);

    public AutoRefitWatcher(CompositorService compositor, PenumbraBridge penumbra, GlamourerBridge glamourer,
                            UVRemapService uvRemap, Func<string, byte[]?> readGameFile, Configuration config,
                            IPluginLog log, string dataDir)
    {
        detections = new DetectionCache(Path.Combine(dataDir, "autorefit-garments.json"), log);
        this.compositor = compositor;
        this.penumbra = penumbra;
        this.glamourer = glamourer;
        this.uvRemap = uvRemap;
        this.readGameFile = readGameFile;
        this.config = config;
        this.log = log;
        refitMods = new RefitModService(penumbra, compositor, log);

        glamourer.LocalPlayerEquipmentChanged += OnEquipmentChanged;
        glamourer.LocalPlayerStateChanged += OnEquipmentChanged;
        penumbra.LocalPlayerRedrawn += OnRedrawn;
        penumbra.PlayerCollectionChanged += OnCollectionChanged;
        penumbra.ModAdded += OnModsChanged;
        penumbra.ModDeleted += OnModsChanged;
        penumbra.ModSettingChanged += OnModSettingChanged;
        penumbra.PenumbraReady += RequestIndexSoon;

        RequestIndexSoon();
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        disposed = true;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        glamourer.LocalPlayerEquipmentChanged -= OnEquipmentChanged;
        glamourer.LocalPlayerStateChanged -= OnEquipmentChanged;
        penumbra.LocalPlayerRedrawn -= OnRedrawn;
        penumbra.PlayerCollectionChanged -= OnCollectionChanged;
        penumbra.ModAdded -= OnModsChanged;
        penumbra.ModDeleted -= OnModsChanged;
        penumbra.ModSettingChanged -= OnModSettingChanged;
        penumbra.PenumbraReady -= RequestIndexSoon;
        CancelAll();
        detections.Flush();   // a reading made in a batch the unload cut short is still a good reading
    }

    private bool Enabled => config.PluginEnabled && config.AutoRefitEnabled;

    // ── signals ─────────────────────────────────────────────────────────────

    private void OnEquipmentChanged() => RequestWalkIn(EquipSettleMs);

    private void OnRedrawn() => RequestWalkIn(RedrawDebounceMs);

    /// <summary>Another collection is another look: what was seen in the old one says nothing, and what was queued for
    /// it would be switched on in the wrong place.</summary>
    private void OnCollectionChanged()
    {
        CancelAll();
        RequestWalkIn(RedrawDebounceMs);
    }

    private void OnModsChanged(string _) => RequestIndexIn(IndexDebounceMs);

    private void OnModSettingChanged(Penumbra.Api.Enums.ModSettingChange change, Guid collection, string modDir,
                                     bool inherited)
    {
        // Only an edit can change which sizes a mod publishes; a setting change cannot. Proteus's own managed mod is
        // rewritten by every composite and is never a body, so its edits are not a reason to look at 1000 mods again.
        if (change == Penumbra.Api.Enums.ModSettingChange.Edited
            && !string.Equals(modDir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            RequestIndexIn(IndexDebounceMs);
    }

    private void RequestWalkIn(int ms)
    {
        if (disposed || !Enabled) return;
        Interlocked.Exchange(ref walkDueAt, Environment.TickCount64 + ms);
    }

    private void RequestIndexSoon() => RequestIndexIn(0);

    private void RequestIndexIn(int ms) => Interlocked.Exchange(ref indexDueAt, Environment.TickCount64 + Math.Max(ms, 1));

    /// <summary>Re-read the body mods now, for the settings' body list. Safe from the framework thread.</summary>
    public void RefreshBodies() => RequestIndexSoon();

    /// <summary>Refit every piece being worn now, as if each had just been put on.</summary>
    public void RunNow()
    {
        forceAll = true;
        Interlocked.Exchange(ref walkDueAt, Environment.TickCount64 + 1);
    }

    // ── the framework thread ────────────────────────────────────────────────

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed) return;

        if (refitMods.Pump() is { } made) log.Information("[Proteus] auto refit: {0}", made);

        long indexDue = Interlocked.Read(ref indexDueAt);
        if (indexDue != 0 && Environment.TickCount64 >= indexDue && Volatile.Read(ref indexing) == 0)
        {
            Interlocked.CompareExchange(ref indexDueAt, 0, indexDue);
            StartIndexRefresh();
        }

        bool enabled = Enabled;
        if (enabled && !wasEnabled)
        {
            // Switching it on takes note of what is worn rather than refitting all of it.
            lastSeen = null;
            RequestWalkIn(RedrawDebounceMs);
        }
        else if (!enabled && wasEnabled)
        {
            CancelAll();
        }
        wasEnabled = enabled;
        if (!enabled) return;

        long due = Interlocked.Read(ref walkDueAt);
        if (due == 0 || Environment.TickCount64 < due) return;
        Interlocked.CompareExchange(ref walkDueAt, 0, due);

        try
        {
            var clock = Stopwatch.StartNew();
            Walk();
            if (clock.ElapsedMilliseconds > SlowFrameMs)
                log.Warning("[Proteus] auto refit: reading what is worn took {0} ms", clock.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] auto refit: reading what the character wears failed");
        }
    }

    private void StartIndexRefresh()
    {
        var mods = penumbra.GetAllMods();
        string? modsRoot = penumbra.GetModDirectory();
        if (mods == null || modsRoot == null) return;
        if (Interlocked.CompareExchange(ref indexing, 1, 0) != 0) return;
        Task.Run(() =>
        {
            try
            {
                var bodies = Index.Refresh(mods, modsRoot);
                log.Debug("[Proteus] auto refit: {0} body mod(s) installed", bodies.Count);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[Proteus] auto refit: reading the body mods failed");
            }
            finally
            {
                Interlocked.Exchange(ref indexing, 0);
            }
        });
    }

    /// <summary>Read what each body slot draws, and queue the pieces that are new since the last look.</summary>
    private void Walk()
    {
        var models = penumbra.GetActivePlayerModelPaths();
        if (!DrawnModelPaths.HasBodySlotModel(models))
        {
            // Most likely mid-rebuild: a drawn character always has a body slot.
            if (++blanks < 4) RequestWalkIn(BlankRetryMs);
            return;
        }
        blanks = 0;

        if (penumbra.GetPlayerCollection() is not { } collection) return;
        if (seenIn != collection.Id)
        {
            seenIn = collection.Id;
            lastSeen = null;
        }

        string? modsRoot = penumbra.GetModDirectory();
        var now = new Dictionary<string, Worn>(StringComparer.Ordinal);
        var resolvedOf = new Dictionary<string, (string? Resolved, string? Root, bool Outside)>(StringComparer.Ordinal);
        foreach (var (suffix, gamePath) in DrawnModelPaths.EquippedPartModelsFromModels(models))
        {
            string slot = "_" + suffix;
            string? resolved = penumbra.ResolvePlayer(gamePath);
            if (resolved == null || !Path.IsPathRooted(resolved))
            {
                now[slot] = new Worn(gamePath, null, null);
                resolvedOf[slot] = (null, null, false);
            }
            else if (modsRoot != null && HatCompatService.InMods(resolved, modsRoot, out string root, out string rel))
            {
                now[slot] = new Worn(gamePath, Path.GetFileName(root), rel);
                resolvedOf[slot] = (resolved, root, false);
            }
            else
            {
                now[slot] = new Worn(gamePath, null, null);
                resolvedOf[slot] = (resolved, null, true);
            }
        }

        bool all = forceAll;
        forceAll = false;
        var changed = all ? now.Keys.ToList() : AutoRefitDecisions.Changed(lastSeen, now);
        lastSeen = now;
        if (changed.Count == 0) return;

        if (!config.AutoRefitByCollection.TryGetValue(collection.Id.ToString("D"), out var pref)
            || pref.BodyDir.Length == 0)
        {
            log.Debug("[Proteus] auto refit: {0} has no preferred body, nothing refitted", collection.Name);
            return;
        }

        long tick = Environment.TickCount64;
        foreach (string slot in changed)
        {
            var worn = now[slot];
            var (resolved, root, outside) = resolvedOf[slot];
            if (ours.TryGetValue(slot + "|" + worn.GamePath, out long until) && tick < until) continue;

            var skip = AutoRefitDecisions.ShouldRefit(worn, config.AutoRefitVanilla, outside);
            if (skip != AutoRefitDecisions.Skip.None)
            {
                log.Debug("[Proteus] auto refit: {0} {1} left alone ({2})", slot, worn.GamePath, skip);
                continue;
            }
            Enqueue(new Request(slot, worn, resolved, root, collection.Id, all));
        }
    }

    // ── the queue ───────────────────────────────────────────────────────────

    private void Enqueue(Request request)
    {
        lock (queueGate)
        {
            queue[request.Slot] = request;
            // A newer piece in the slot being refitted: the refit in flight is for something no longer worn.
            if (runningSlot == request.Slot) running?.Cancel();
            if (runner == null)
            {
                int id = ++runnerId;
                runner = Task.Run(() => RunQueue(id));
            }
        }
    }

    /// <summary>
    /// Which runner <see cref="runner"/> is. A runner only ever clears the field while it still holds its own id:
    /// clearing it after another had already been started in its place let a third start beside that one, and two
    /// refits then ran at once.
    /// </summary>
    private int runnerId;

    private void CancelAll()
    {
        lock (queueGate)
        {
            queue.Clear();
            running?.Cancel();
        }
    }

    private async Task RunQueue(int id)
    {
        try
        {
            while (!disposed)
            {
                Request? request = null;
                CancellationTokenSource? cts = null;
                lock (queueGate)
                {
                    string? next = AutoRefitDecisions.Slots.FirstOrDefault(queue.ContainsKey);
                    if (next == null)
                    {
                        // Cleared under the same lock Enqueue starts a runner under: a request arriving after this
                        // starts a new runner, and one arriving before it was taken by the loop.
                        runner = null;
                        FlushSummary();
                    }
                    else
                    {
                        request = queue[next];
                        queue.Remove(next);
                        cts = running = new CancellationTokenSource();
                        runningSlot = next;
                    }
                }
                if (request == null || cts == null)
                {
                    // Outside the lock: writing the cache is a megabyte or two to disk, and Enqueue — called from the
                    // framework thread when gear changes — takes that lock, so the frame would wait for the disk.
                    detections.Flush();
                    return;
                }

                current = current with { Busy = true };
                // A refit reads hundreds of megabytes of models. A blocking full collection in the middle of that stops
                // every thread in the process, the game's included, for as long as it takes; this asks the collector
                // to do that work in the background instead while a refit runs.
                var latency = System.Runtime.GCSettings.LatencyMode;
                System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
                long allocated = GC.GetTotalAllocatedBytes();
                int blocking = GC.CollectionCount(2);
                var clock = Stopwatch.StartNew();
                try
                {
                    await RunJob(request, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    log.Information("[Proteus] auto refit: {0} {1} cancelled", request.Slot, request.Worn.GamePath);
                }
                catch (Exception ex)
                {
                    log.Error(ex, "[Proteus] auto refit: {0} failed", request.Worn.GamePath);
                    Fail(request, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt",
                                                    "[Proteus] Couldn't refit {0}: {1}"),
                                                Path.GetFileName(request.Worn.GamePath), ex.Message));
                }
                finally
                {
                    System.Runtime.GCSettings.LatencyMode = latency;
                    log.Information("[Proteus] auto refit: {0} took {1:F1} s, {2} MB allocated, {3} full collection(s)",
                                    request.Slot, clock.Elapsed.TotalSeconds,
                                    (GC.GetTotalAllocatedBytes() - allocated) / 1048576, GC.CollectionCount(2) - blocking);
                    lock (queueGate)
                    {
                        running = null;
                        runningSlot = null;
                    }
                    cts.Dispose();
                    current = current with { Busy = false };
                }
            }
        }
        finally
        {
            // Only while the field is still this runner: once the queue emptied, it may already hold the next one.
            lock (queueGate)
                if (runnerId == id)
                    runner = null;
            BodySizeMatch.ForgetCandidatesBeyond(CachedBodiesKept);
        }
    }

    // ── one refit ───────────────────────────────────────────────────────────

    /// <summary>What the framework thread hands a refit before it starts.</summary>
    private sealed record Gathered(
        AutoRefitPreference Preference, IReadOnlyDictionary<string, string> Mods, string ModsRoot,
        IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot>? Settings,
        RacialModelBake.Wearer? Wearer, byte[]? VanillaBytes, VanillaPick? Vanilla);

    /// <summary>
    /// Run a short piece of a refit on the framework thread. Timed, because anything slow here is a frame the player
    /// sees drop: a hop over a few milliseconds is logged with what called it.
    /// <para/>
    /// After <see cref="Dispose"/> it throws <see cref="OperationCanceledException"/> instead. A refit's CPU work does not
    /// stop for the cancel — the planner and the writer never look at the token — so a refit caught by an unload or a
    /// dev reload would otherwise reload, select and redraw through bridges and a compositor already disposed; and the
    /// switch-on after a save runs with no token at all.
    /// <para/>
    /// The caller carries on on a WORKER afterwards, never on the framework thread — and that is the point of the
    /// <c>ForceYielding</c> below, not a detail. The task Dalamud hands back is completed on the framework thread, and an
    /// await continues on whichever thread completed what it awaited; so without it, everything a refit did after its
    /// first hop — sampling every body, ranking a pack's sizes, the refit itself, the save — ran inside the game's frame,
    /// and the game hung for as long as the refit took.
    /// </summary>
    private async Task<T> OnFramework<T>(Func<T> work, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (disposed) throw new OperationCanceledException("auto refit disposed");
        var hop = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (disposed) throw new OperationCanceledException("auto refit disposed");
            var clock = Stopwatch.StartNew();
            try { return work(); }
            finally
            {
                if (clock.ElapsedMilliseconds > SlowFrameMs)
                    log.Warning("[Proteus] auto refit: framework-thread step at line {0} took {1} ms", line,
                                clock.ElapsedMilliseconds);
            }
        });
        return await hop.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
    }

    /// <summary>
    /// Log, once per refit, when work meant for a worker finds itself on the framework thread — the bug
    /// <see cref="OnFramework{T}"/> describes, should anything bring it back.
    /// </summary>
    private void AssertOffFramework(string step)
    {
        if (Plugin.Framework.IsInFrameworkUpdateThread)
            log.Error("[Proteus] auto refit: {0} is running on the framework thread — the game waits for it", step);
    }

    /// <summary>A framework-thread step longer than this is logged.</summary>
    private const long SlowFrameMs = 8;

    private async Task RunJob(Request r, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        bool isVanilla = r.ModRoot == null;

        var g = await OnFramework(() => Gather(r));
        if (g == null) return;
        ct.ThrowIfCancellationRequested();

        // Kept current by Penumbra's add, delete and edit events; read fresh only before the first scan has finished.
        // The list and its version taken TOGETHER, and used for the whole refit: a refresh landing part way through must
        // not have the garment searched against one set of bodies and its reading filed under another's version.
        var state = Index.Current ?? Index.RefreshState(g.Mods, g.ModsRoot);
        var bodies = state.List;
        if (state.Find(g.Preference.BodyDir) is not { } targetMod)
        {
            string missing = string.Format(Loc.Localize("Chat.AutoRefit.BodyMissing.Fmt",
                    "[Proteus] The body chosen for automatic refits in this collection, \"{0}\", isn't installed, "
                  + "so nothing is being refitted. Choose another under /proteus > Settings."),
                    g.Preference.BodyDir);
            Say(missing, problem: true);
            if (toldMissing.TryAdd(r.Collection, 0)) Chat(missing, problem: true);
            return;
        }
        var targetCat = targetMod.Catalog;

        // ── the garment, baked for the wearer's race when it is drawn from another's ──
        byte[] bytes = isVanilla ? g.VanillaBytes ?? [] : await File.ReadAllBytesAsync(r.Resolved!, ct);
        if (bytes.Length == 0) return;
        string item = ItemName(r, g);

        List<PenumbraModMeta.Redirect> redirects = isVanilla ? [] : PenumbraModMeta.ReadAllRedirects(r.ModRoot!);
        var ownRecord = isVanilla ? null : BodyRetargetWriter.ReadRecord(r.ModRoot!);
        var targetRaces = RefitCore.RacesOf(targetCat);
        ushort drawn = ModelSkinReader.RaceOf(r.Worn.GamePath);
        ushort bakeTo = g.Wearer == null
            ? (ushort)0
            : RefitCore.BakeTarget(r.Worn.GamePath, g.Wearer.Race, code => targetRaces.Contains($"{code:D4}"),
                                   redirects, ownRecord);
        string savePath = r.Worn.GamePath;
        if (bakeTo != 0)
        {
            var baked = RacialModelBake.Bake(bytes, drawn, bakeTo, g.Wearer!.Pbd, g.Wearer.ParentOf, out string why);
            if (baked != null)
            {
                log.Information("[Proteus] auto refit: baked {0} from c{1:D4} to c{2:D4}", r.Worn.GamePath, drawn, bakeTo);
                bytes = baked;
                savePath = RacialModelBake.WithRace(r.Worn.GamePath, bakeTo);
            }
            else
            {
                log.Warning("[Proteus] auto refit: could not bake {0} to c{1:D4}: {2}", r.Worn.GamePath, bakeTo, why);
                bakeTo = 0;
            }
        }
        string race = bakeTo != 0 ? $"{bakeTo:D4}" : BodySizeCatalog.RaceOf(r.Worn.GamePath) ?? "0201";
        bool male = BodySizeCatalog.IsMaleRace(race);

        if (ModelPartReader.Read(bytes) is not { } garment)
        {
            Fail(r, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"), item,
                               Strings.Parts.Unreadable));
            return;
        }
        var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);

        // The game's own body at this race, and where Penumbra resolves each part of the preferred body for the player:
        // both read through the game or Penumbra, so on the framework thread. Only the asking happens there — a body
        // part is a handful of game paths, but matching the answers to sizes touches every size's file on disk (Neolithe
        // has 114 chests), which is too long a stall for a frame.
        var bodyPaths = AutoRefitDecisions.Slots.SelectMany(s => targetCat.For(s, race))
                                                .Select(o => o.GamePath)
                                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                                .ToList();
        var (vanillaCat, resolved) = await OnFramework(() =>
        {
            var vanilla = VanillaBodyCatalog.Read(race, readGameFile);
            var answers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in bodyPaths) answers[path] = penumbra.ResolvePlayer(path);
            return (vanilla, answers);
        });
        ct.ThrowIfCancellationRequested();
        var worn = new Dictionary<string, BodyOption?>(StringComparer.Ordinal);
        foreach (string slot in AutoRefitDecisions.Slots)
            worn[slot] = RefitCore.WornOption(targetCat, slot, race, path => resolved.GetValueOrDefault(path));

        // ── what to refit onto ──
        var chestTo = AutoRefitDecisions.Resolve(targetCat.For("_top", race), g.Preference.Chest);
        var legsTo = AutoRefitDecisions.Resolve(targetCat.For("_dwn", race), g.Preference.Legs);
        BodyOption? TargetFor(string slot) => slot switch
        {
            "_top" => chestTo ?? AutoRefitDecisions.Companion(targetCat.For(slot, race), legsTo, worn[slot]),
            "_dwn" => legsTo ?? AutoRefitDecisions.Companion(targetCat.For(slot, race), chestTo, worn[slot]),
            _ => AutoRefitDecisions.Companion(targetCat.For(slot, race), chestTo ?? legsTo, worn[slot]),
        };

        string primary = r.Slot;
        if (TargetFor(primary) is not { } target)
        {
            Say(string.Format(Loc.Localize("Chat.AutoRefit.NoSizeForRace.Fmt",
                    "[Proteus] {0} has no {1} size for {2}, so {3} was left as it is."),
                    targetMod.Name, SlotName(primary), ModelRace.Describe(race), item), problem: true);
            return;
        }

        // ── what it was made on, and which other parts of that body it reaches ──
        BodySizeCatalog? CatalogOf(string dir) => dir == VanillaBodyCatalog.Key ? vanillaCat : state.Find(dir)?.Catalog;
        // The collection's own choice only. A body mod a Glamourer design holds on or off with a temporary setting comes
        // and goes with every design; counted, it would make each design change read every garment again.
        var enabledBodies = bodies.Where(b => g.Settings != null && g.Settings.TryGetValue(b.Dir, out var s)
                                              && s.Enabled && !s.Temporary)
                                  .Select(b => b.Dir);
        string cacheKey = DetectionCache.KeyOf(bytes, primary, race, targetMod.Dir,
                                               DetectionCache.BodiesOf(state.Version, enabledBodies,
                                                                       VanillaPrint(vanillaCat, primary, race)));
        AutoRefitDecisions.Pick? source = null;
        Dictionary<string, string> otherFrom;
        if (detections.TryGet(cacheKey) is { } hit && Restore(hit, primary, race, CatalogOf) is { } restored)
        {
            source = restored.Pick;
            otherFrom = hit.Others;
            log.Information("[Proteus] auto refit: {0} read before ({1}), not read again", item, hit.Confidence);
        }
        else
        {
            AssertOffFramework("reading the garment");
            source = isVanilla
                ? VanillaSource(vanillaCat, primary, race)
                : DetectAcross(garment, bones, primary, race, targetMod, bodies, vanillaCat, g.Settings);
            otherFrom = new Dictionary<string, string>(StringComparer.Ordinal);
            if (source is { } found && AutoRefitDecisions.Proceed(found.Confidence) && CatalogOf(found.Dir) is { } from)
                foreach (string slot in AutoRefitDecisions.Slots)
                {
                    if (slot == primary || from.For(slot, race).Count == 0) continue;
                    var ranking = BodySizeMatch.Rank(garment, from.For(slot, race), from.PathOf, bones);
                    if (AutoRefitDecisions.AcceptOther(ranking, found.Option)) otherFrom[slot] = ranking.Best!.Value.Option.Rel;
                }
            detections.Put(cacheKey, new DetectionCache.Entry(source?.Dir ?? "", source?.Option.Rel ?? "",
                                                               source?.Confidence ?? Confidence.Ambiguous,
                                                               source?.FromCloth ?? false, otherFrom));
        }
        ct.ThrowIfCancellationRequested();

        if (source is not { } pick)
        {
            Say(string.Format(Loc.Localize("Chat.AutoRefit.Unsure.Fmt",
                    "[Proteus] Couldn't tell which body {0} was made for, so it was left as it is. You can refit it by "
                  + "hand with the Studio's Body size tool."), item), problem: true);
            return;
        }
        if (pick.Confidence == Confidence.NoBodyMesh)
        {
            Say(string.Format(Loc.Localize("Chat.AutoRefit.NoBodyMesh.Fmt",
                    "[Proteus] {0} carries no body skin to read its size from, so it was left as it is. You can refit "
                  + "it by hand with the Studio's Body size tool."), item), problem: true);
            return;
        }
        if (!AutoRefitDecisions.Proceed(pick.Confidence))
        {
            Say(string.Format(Loc.Localize("Chat.AutoRefit.Unsure.Fmt",
                    "[Proteus] Couldn't tell which body {0} was made for, so it was left as it is. You can refit it by "
                  + "hand with the Studio's Body size tool."), item), problem: true);
            return;
        }

        var sourceCat = pick.Dir == VanillaBodyCatalog.Key ? vanillaCat : state.Find(pick.Dir)!.Catalog;
        string sourceName = pick.Dir == VanillaBodyCatalog.Key ? Strings.Parts.RetargetFromVanilla
                          : state.Find(pick.Dir)?.Name ?? pick.Dir;
        log.Information("[Proteus] auto refit: {0} {1} made on {2} / {3} ({4}{5})", item, primary, sourceName,
                        pick.Option.Label, pick.Confidence, pick.FromCloth ? ", from cloth" : "");

        if (AutoRefitDecisions.SameFile(pick.Dir, pick.Option, targetMod.Dir, target))
        {
            log.Information("[Proteus] auto refit: {0} already fits {1} / {2}", item, targetMod.Name, target.Label);
            return;
        }
        bool across = !string.Equals(pick.Dir, targetMod.Dir, StringComparison.OrdinalIgnoreCase);

        // ── the other parts of the body the garment reaches, each onto its chosen size ──
        var others = new List<(string Slot, BodyOption From, BodyOption To)>();
        foreach (string slot in AutoRefitDecisions.Slots)
        {
            if (slot == primary || !otherFrom.TryGetValue(slot, out string? fromRel)) continue;
            if (OptionByFile(sourceCat.For(slot, race), fromRel) is not { } from) continue;
            if (TargetFor(slot) is not { } to || AutoRefitDecisions.SameFile(pick.Dir, from, targetMod.Dir, to)) continue;
            others.Add((slot, from, to));
        }

        ushort? SourceMask(string slot) => RefitCore.MaskOf(pick.Dir, sourceCat, slot, OptionsOf(g.Settings, pick.Dir));
        ushort? TargetMask(string slot) => RefitCore.MaskOf(targetMod.Dir, targetCat, slot, OptionsOf(g.Settings, targetMod.Dir));

        // ── where it goes ──
        string? root = r.ModRoot;
        string group;
        string? cutFrom = null;
        if (isVanilla)
        {
            root = RefitModService.Find(g.ModsRoot, g.Vanilla!.Value.ItemName, targetMod.Name);
            group = string.Format(Strings.Parts.RetargetGroupFmt, g.Vanilla.Value.Label);

            // The mod made for this item before, switched off in this collection: that is the player taking the
            // refit off, which is why the game's own model is showing — not a piece newly put on.
            if (root != null && g.Settings != null && g.Settings.TryGetValue(Path.GetFileName(root), out var held)
                && !held.Enabled)
            {
                log.Information("[Proteus] auto refit: {0} is switched off in this collection, left off", root);
                return;
            }
        }
        else
        {
            var record = BodyRetargetWriter.ReadRecord(root!);
            var model = redirects.FirstOrDefault(d => RefitCore.Normal(d.File) == RefitCore.Normal(r.Worn.Rel!)
                                                     && string.Equals(d.GamePath, r.Worn.GamePath,
                                                                      StringComparison.OrdinalIgnoreCase));
            group = RefitCore.SwitchingGroup(redirects, r.Worn.GamePath, record?.OwnGroups, RefitCore.SingleGroups(root!),
                                             r.Worn.Rel, RefitCore.MultiGroups(root!))
                 ?? string.Format(Strings.Parts.RetargetGroupFmt,
                                  model.GamePath != null ? Gui.PartsPanel.ModelLabel(model) : item);
            cutFrom = BodyRetargetWriter.OptionOfFile(redirects, r.Worn.Rel!, group);
        }

        string OptionFor(IEnumerable<(string Slot, BodyOption From, BodyOption To)> parts)
            => RefitCore.OptionName(targetMod.Name, new[] { target.Label }.Concat(parts.Select(p => p.To.Label)));

        // A refit was switched on in this group before and the player has since picked something else — "Original", or
        // one of the author's sizes. That choice reads as the piece newly put on; refitting would undo it.
        if (!r.Forced && root != null && ChoseOtherwise(g, root, group))
        {
            log.Information("[Proteus] auto refit: {0} / {1} was switched away from its refit in this collection, left as "
                          + "chosen", Path.GetFileName(root), group);
            return;
        }

        // Made before, and still there: put it back on rather than refitting again.
        string option = OptionFor(others);
        if (root != null && Existing(root, group, option, savePath))
        {
            if (await Apply(r, root, group, option, cutFrom, isVanilla, ct) is { } offExisting)
            {
                Fail(r, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"),
                                      item, offExisting));
                return;
            }
            Say(string.Format(Loc.Localize("Chat.AutoRefit.Existing.Fmt",
                    "[Proteus] {0} was already refitted to {1}; that size is switched on."), item, option));
            Finished(item, targetMod.Name);
            return;
        }

        // ── the refit ──
        string targetLabel = targetMod.Name + " — " + target.Label;
        Say(string.Format(Loc.Localize("Chat.AutoRefit.Start.Fmt",
                "[Proteus] Refitting {0} from {1} to {2}. It will be switched on when it is done."),
                item, sourceName + " — " + pick.Option.Label, targetLabel));
        current = current with { Message = string.Format(Strings.AutoRefit.WorkingFmt, item), Failed = false };

        string sourcePath = sourceCat.PathOf(pick.Option), targetPath = targetCat.PathOf(target);
        if (BodyRetarget.BuildPair(primary, sourcePath, targetPath, SlotName(primary), male, SourceMask(primary),
                                   TargetMask(primary), uvRemap, out var primaryPair) is { } refusal)
        {
            Fail(r, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"), item,
                               refusal));
            return;
        }
        var pairs = new List<BodyRetarget.SlotPair> { primaryPair };
        var kept = new List<(string Slot, BodyOption From, BodyOption To)>();
        var dropped = new List<string>();
        foreach (var other in others)
        {
            if (BodyRetarget.BuildPair(other.Slot, sourceCat.PathOf(other.From), targetCat.PathOf(other.To),
                                       SlotName(other.Slot), male, SourceMask(other.Slot), TargetMask(other.Slot),
                                       uvRemap, out var pair) is { } why)
            {
                log.Information("[Proteus] auto refit: {0} {1} left out: {2}", item, other.Slot, why);
                dropped.Add(SlotName(other.Slot));
                continue;
            }
            pairs.Add(pair);
            kept.Add(other);
        }
        option = OptionFor(kept);
        ct.ThrowIfCancellationRequested();

        AssertOffFramework("the refit");
        var pieces = RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment));
        var plan = BodyRetarget.Plan(garment, bytes, pairs, primary, held: new HashSet<int>(),
                                     replaceSkin: across, acrossBodies: across, clearBody: false, cutHidden: true,
                                     keepShape: pieces);
        if (bakeTo != 0) plan = RefitCore.WithBodySkin(plan, targetPath, log);
        ct.ThrowIfCancellationRequested();

        // A mod of Proteus's own for the game's gear, made only now that there is something to put in it.
        if (root == null)
        {
            var made = await OnFramework(() => refitMods.Ensure(g.Vanilla!.Value.ItemName, targetMod.Name));
            if (!made.Ok)
            {
                Fail(r, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"), item,
                                   made.Message));
                return;
            }
            root = made.Root;
        }

        string labelFrom = RefitCore.LabelFrom(across ? sourceName : null,
                                               new[] { pick.Option.Label }.Concat(kept.Select(k => k.From.Label)));
        List<object>? manipulations = bakeTo != 0 && RacialModelBake.Switch(savePath, bakeTo) is { } eqdp ? [eqdp] : null;
        var outcome = BodyRetargetWriter.Save(root, group, savePath, targetMod.Dir, labelFrom,
                                              [new BodyRetargetWriter.Refit(option, plan.Model, string.Join(" + ",
                                                  new[] { target.Label }.Concat(kept.Select(k => k.To.Label))))],
                                              cutFrom, manipulations);
        if (!outcome.Ok)
        {
            Fail(r, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"), item,
                               outcome.Message));
            return;
        }

        if (await Apply(r, root, group, option, cutFrom, isVanilla, CancellationToken.None) is { } off)
        {
            // Saved, so the size is there to pick by hand; but it is not on, and must not be reported as worn.
            Fail(r, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"), item,
                                  off));
            return;
        }
        log.Information("[Proteus] auto refit: {0} {1} onto {2} in {3:F1}s (moved up to {4:F1} mm)", item, primary,
                        option, clock.Elapsed.TotalSeconds, plan.Report.WorstMove * 1000f);

        string savedIn = isVanilla ? RefitModService.NameFor(g.Vanilla!.Value.ItemName, targetMod.Name)
                       : g.Mods.TryGetValue(Path.GetFileName(root), out string? modName) && modName.Length > 0 ? modName
                       : Path.GetFileName(root);
        string done = string.Format(Loc.Localize("Chat.AutoRefit.Done.Fmt",
                "[Proteus] {0} now fits {1}. Saved as \"{2}\" in {3} and switched on."),
                item, targetLabel, option, savedIn);
        if (dropped.Count > 0)
            done += " " + string.Format(Loc.Localize("Chat.AutoRefit.PartsLeftOut.Fmt",
                        "These parts could not be refitted with it and kept their size: {0}."),
                        string.Join(", ", dropped));
        Say(done);
        Finished(item, targetMod.Name);
    }

    /// <summary>Collect what only the framework thread may read. Null when there is nothing to do after all.</summary>
    private Gathered? Gather(Request r)
    {
        if (!Enabled || disposed) return null;
        if (!config.AutoRefitByCollection.TryGetValue(r.Collection.ToString("D"), out var pref) || pref.BodyDir.Length == 0)
            return null;
        if (penumbra.GetAllMods() is not { } mods || penumbra.GetModDirectory() is not { } modsRoot) return null;

        var settings = penumbra.GetCollectionModSettings(r.Collection, ignoreTemporary: false);

        RacialModelBake.Wearer? wearer = null;
        if (LiveCharacter.PlayerSkeleton(Plugin.ObjectTable) is { } skeleton)
        {
            if (!pbdTried)
            {
                pbdTried = true;
                pbd = LiveCharacter.LoadPbd(penumbra, Plugin.DataManager, log);
            }
            if (pbd != null) wearer = new RacialModelBake.Wearer(skeleton.GenderRace, skeleton.Parents, pbd);
        }

        byte[]? vanillaBytes = null;
        VanillaPick? vanilla = null;
        if (r.ModRoot == null)
        {
            itemNames ??= ItemNames.Lookup(Plugin.DataManager, log);
            vanilla = VanillaPick.From(r.Worn.GamePath, itemNames);
            if (vanilla is not { Refittable: true }) return null;
            vanillaBytes = readGameFile(r.Worn.GamePath);
        }
        return new Gathered(pref, mods, modsRoot, settings, wearer, vanillaBytes, vanilla);
    }

    /// <summary>
    /// The body mod — and which of its sizes — the garment was made on, searched over every installed body mod and the
    /// game's own body: the preferred mod first, the game's body last. See <see cref="RefitCore.DetectSource"/>.
    /// </summary>
    private AutoRefitDecisions.Pick? DetectAcross(ModelParts garment, IReadOnlySet<string> bones, string slot, string race,
                                                  BodyModIndex.Entry targetMod, IReadOnlyList<BodyModIndex.Entry> bodies,
                                                  BodySizeCatalog vanilla,
                                                  IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot>? settings)
    {
        var order = new List<(string Dir, BodySizeCatalog Catalog)> { (targetMod.Dir, targetMod.Catalog) };
        order.AddRange(bodies.Where(b => !string.Equals(b.Dir, targetMod.Dir, StringComparison.OrdinalIgnoreCase))
                             .Select(b => (b.Dir, b.Catalog)));
        if (vanilla.IsBody) order.Add((VanillaBodyCatalog.Key, vanilla));

        return RefitCore.DetectSource(garment, bones, slot, race, order, targetMod.Dir,
                                      dir => settings != null && settings.TryGetValue(dir, out var s) && s.Enabled,
                                      note => log.Information("[Proteus] auto refit: {0}", note));
    }

    /// <summary>The game's own body's fingerprint per extracted file, hashed once a session: a game patch changes it.</summary>
    private readonly ConcurrentDictionary<string, string> vanillaPrints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A fingerprint of the game's own body in <paramref name="slot"/> at <paramref name="race"/> — a candidate for every
    /// garment, and the source of every piece of the game's gear — so a patch that reshapes it reads garments again.
    /// "" when the game has no body there.
    /// </summary>
    private string VanillaPrint(BodySizeCatalog vanilla, string slot, string race)
    {
        if (vanilla.For(slot, race).FirstOrDefault() is not { } body) return "";
        string path = vanilla.PathOf(body);
        return vanillaPrints.GetOrAdd(path, p =>
        {
            try { return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))[..16]; }
            catch (Exception) { return ""; }
        });
    }

    /// <summary>A cached reading, as it was. Its pick is null when the garment could not be read.</summary>
    private sealed record Restored(AutoRefitDecisions.Pick? Pick);

    /// <summary>
    /// A cached reading turned back into the body option it names — or null when the bodies installed now no longer
    /// have it (the key moves with every body mod change, so this is a belt to those braces), which reads the garment
    /// again.
    /// </summary>
    private static Restored? Restore(DetectionCache.Entry hit, string slot, string race,
                                     Func<string, BodySizeCatalog?> catalogOf)
    {
        if (hit.Dir.Length == 0) return new Restored(null);
        if (catalogOf(hit.Dir) is not { } catalog || OptionByFile(catalog.For(slot, race), hit.Rel) is not { } option)
            return null;
        return new Restored(new AutoRefitDecisions.Pick(hit.Dir, option, hit.Confidence, hit.FromCloth));
    }

    /// <summary>The option whose model is <paramref name="rel"/>, slashes and case as Penumbra treats them.</summary>
    private static BodyOption? OptionByFile(IReadOnlyList<BodyOption> options, string rel)
    {
        string want = RefitCore.Normal(rel);
        return options.FirstOrDefault(o => RefitCore.Normal(o.Rel) == want);
    }

    /// <summary>The game's own gear is made on the game's own body: one size, no search.</summary>
    private static AutoRefitDecisions.Pick? VanillaSource(BodySizeCatalog vanilla, string slot, string race)
        => vanilla.For(slot, race) is { Count: > 0 } options
            ? new AutoRefitDecisions.Pick(VanillaBodyCatalog.Key, options[0], Confidence.Exact, false)
            : null;

    /// <summary>See <see cref="AutoRefitDecisions.ChoseOtherwise"/>: read from the collection's settings as gathered.</summary>
    private static bool ChoseOtherwise(Gathered g, string root, string group)
    {
        string dir = Path.GetFileName(root);
        bool before = g.Preference.SwitchedOn.Contains(AutoRefitDecisions.GroupKey(dir, group));
        var ticked = g.Settings != null && g.Settings.TryGetValue(dir, out var s)
            ? s.Options.FirstOrDefault(o => string.Equals(o.Key, group, StringComparison.OrdinalIgnoreCase)).Value
            : null;
        var record = BodyRetargetWriter.ReadRecord(root);
        var refits = record?.Options.Where(e => string.Equals(record.GroupOf(e), group, StringComparison.OrdinalIgnoreCase))
                                    .Select(e => e.Name) ?? [];
        return AutoRefitDecisions.ChoseOtherwise(before, ticked, refits);
    }

    /// <summary>Whether this exact refit is already saved in the mod and still offered by the group.</summary>
    private static bool Existing(string root, string group, string option, string savePath)
    {
        var record = BodyRetargetWriter.ReadRecord(root);
        if (record == null) return false;
        bool recorded = record.Options.Any(e => string.Equals(e.Name, option, StringComparison.OrdinalIgnoreCase)
                                                && string.Equals(e.GamePath, savePath, StringComparison.OrdinalIgnoreCase)
                                                && string.Equals(record.GroupOf(e), group, StringComparison.OrdinalIgnoreCase));
        return recorded && (PenumbraModMeta.TryReadFileOptions(root, group) ?? [])
                           .Any(o => string.Equals(o.Name, option, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Wear what was saved: Penumbra re-reads the mod, then the option is selected — in that order, or an option the
    /// save just added does not exist to it yet — and the character is redrawn.
    /// </summary>
    /// <param name="cutFrom">The option the refitted model came from: in a multi-choice group, the one it replaces.</param>
    /// <returns>Null when the refit is on; otherwise what Penumbra answered, for the player — a refit that was saved but
    /// could not be switched on must not be reported as worn.</returns>
    private async Task<string?> Apply(Request r, string root, string group, string option, string? cutFrom,
                                      bool madeForGame, CancellationToken ct)
    {
        string dir = Path.GetFileName(root);
        bool multi = RefitCore.IsMulti(root, group);
        await OnFramework(() =>
        {
            compositor.ExpectOwnModEdit(dir);
            return penumbra.ReloadModDirectory(dir);
        });

        // A mod just made for the game's gear is still being registered; a setting written before it is, is lost.
        if (madeForGame)
        {
            var until = Environment.TickCount64 + MadeModTimeoutMs;
            while (await OnFramework(() => refitMods.IsPending) && Environment.TickCount64 < until)
                await Task.Delay(250, ct);
        }

        var answer = await OnFramework(() =>
        {
            var settings = penumbra.GetModSettings(r.Collection, dir);
            if (madeForGame && settings is not { Enabled: true })
                penumbra.SetModEnabled(r.Collection, dir, true);
            var ticked = settings?.Options.FirstOrDefault(o => string.Equals(o.Key, group,
                                                                              StringComparison.OrdinalIgnoreCase)).Value;
            var selection = RefitCore.SelectionFor(multi, option, ticked, cutFrom);
            var ec = penumbra.SetModOption(r.Collection, dir, group, selection);
            log.Information("[Proteus] auto refit: selected {0} / {1} in {2}: {3}", group, string.Join(", ", selection),
                            dir, ec);
            if (ec is not (Penumbra.Api.Enums.PenumbraApiEc.Success or Penumbra.Api.Enums.PenumbraApiEc.NothingChanged))
                return ec.ToString();

            // Remembered, so that the player picking something else in this group later is taken as their choice.
            if (config.AutoRefitByCollection.TryGetValue(r.Collection.ToString("D"), out var pref)
                && pref.SwitchedOn.Add(AutoRefitDecisions.GroupKey(dir, group)))
                config.Save();

            // The walk after this redraw sees the refit worn and must leave it be.
            long quiet = Environment.TickCount64 + OwnChangeQuietMs;
            ours[r.Slot + "|" + r.Worn.GamePath] = quiet;
            compositor.RedrawForChangedModel();
            return (string?)null;
        });
        return answer;
    }

    private static IReadOnlyDictionary<string, List<string>>? OptionsOf(
        IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot>? settings, string dir)
        => settings != null && settings.TryGetValue(dir, out var s) ? s.Options : null;

    /// <summary>What chat calls the piece: the game's name for the game's gear, the mod and the part for a mod's.</summary>
    private static string ItemName(Request r, Gathered g)
    {
        if (g.Vanilla is { } pick) return pick.ItemName;
        string dir = r.Worn.ModDir ?? "";
        string mod = g.Mods.TryGetValue(dir, out string? name) && name.Length > 0 ? name : dir;
        return string.Format(Strings.AutoRefit.ItemInModFmt, mod, SlotName(r.Slot));
    }

    private static string SlotName(string slot) => slot switch
    {
        "_top" => Strings.Parts.RetargetChest,
        "_dwn" => Strings.Parts.RetargetLegs,
        "_glv" => Strings.Parts.RetargetHands,
        "_sho" => Strings.Parts.RetargetFeet,
        _ => slot,
    };

    // ── telling the player ──────────────────────────────────────────────────

    // Chat is for what the player has to know: a refit finished (one line for the whole batch), a refit failed (once per
    // piece a session), the chosen body is missing (once per collection). Everything else — a piece left alone because
    // it already fits, could not be read or has no size here — goes to the status line under the setting and to the
    // log. Players reported the chat log flooding: a refit said it was starting and that it was done, per slot, and a
    // piece with no body skin said so on every equip.

    /// <summary>A refit failed: shown under the setting, and in chat the first time this piece fails this session.</summary>
    private void Fail(Request r, string message)
    {
        Say(message, problem: true);
        if (toldFailed.TryAdd(r.Worn.Key, 0)) Chat(message, problem: true);
    }

    /// <summary>Show under the setting, and in the log. Not chat — see above.</summary>
    private void Say(string message, bool problem = false)
    {
        current = current with { Message = message, Failed = problem };
        log.Information("[Proteus] auto refit: {0}", message);
    }

    /// <summary>A piece now wears its refit; said with the rest of the batch when the queue runs dry.</summary>
    private void Finished(string item, string body)
    {
        lock (queueGate) finished.Add((item, body));
    }

    /// <summary>Say, in one chat line, everything refitted since the last time. Called under <see cref="queueGate"/>.</summary>
    private void FlushSummary()
    {
        if (finished.Count == 0) return;
        var byBody = finished.GroupBy(f => f.Body, StringComparer.Ordinal)
                             .Select(g => string.Format(Loc.Localize("Chat.AutoRefit.Summary.Fmt",
                                                            "[Proteus] Refitted to {0}: {1}."),
                                                        g.Key, string.Join(", ", g.Select(f => f.Item).Distinct())));
        finished.Clear();
        Chat(string.Join(" ", byBody), problem: false);
    }

    /// <summary>
    /// Print to chat. Onto the framework thread, with the catch INSIDE the lambda, since the returned task (which would
    /// capture the exception) is discarded.
    /// </summary>
    private void Chat(string message, bool problem)
    {
        if (disposed) return;
        try
        {
            _ = Plugin.Framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    if (disposed) return;
                    Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(message, problem ? (ushort)17 : (ushort)45)
                                                              .Build());
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "[Proteus] auto refit: could not print to chat");
                }
            });
        }
        catch (Exception ex)
        {
            // Thrown before the hop, by a framework being torn down: from inside RunQueue's catch it would escape the
            // runner, faulting a task nobody observes.
            log.Warning(ex, "[Proteus] auto refit: could not reach the framework to print to chat");
        }
    }
}
