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
public sealed partial class AutoRefitWatcher : IDisposable
{
    /// <summary>Glamourer signals an equip before the new model has loaded; a walk sooner finds the old one.</summary>
    private const int EquipSettleMs = 750;

    /// <summary>A redraw comes in bursts; one walk once they go quiet.</summary>
    private const int RedrawDebounceMs = 250;

    /// <summary>A walk that caught the character mid-rebuild tries again this soon.</summary>
    private const int BlankRetryMs = 500;

    /// <summary>How long a piece this watcher just switched on is left alone, whatever a walk makes of it.</summary>
    private const int OwnChangeQuietMs = 5000;

    // The redraw hold a batch takes (see RedrawHold): renewed at every job, let go when the queue drains.
    private const int HoldLeaseMs = 30_000;
    private const int HoldCapMs = 120_000;
    private const int ReleaseGraceMs = 1500;
    // How long a draining batch waits for the evaluations its own option switches started.
    private const int DrainWaitMs = 3000;

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

    // This batch's switch-ons, under queueGate: the mods it switched (whose evaluations a drain waits for) and the
    // pieces whose quiet runs on past the release (see AutoRefitDecisions.QuietUntil).
    private readonly HashSet<string> batchSwitchedDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> batchOurs = new(StringComparer.OrdinalIgnoreCase);
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
    /// <param name="Beside">For an accessory, shoe or glove: the chest and legs pieces worn with it at the walk, as they
    /// were drawn then — never resolved again, which would find their refits once their own jobs switched them on.</param>
    /// <param name="Chosen">For an accessory: the body and sizes chosen at the walk, which key its cover reading — read
    /// before anything is gathered, so jewellery read before costs no hop to the framework thread.</param>
    /// <param name="Walked">The piece as the walk saw it, when its job found another file of the mod drawn by the time
    /// it started and works on that instead: an accessory beside it looks its outcome up by what the walk saw.</param>
    /// <param name="ModSettings">The settings of the mod drawing it, as the walk saw them — for the log, should the piece
    /// be drawn from elsewhere by the time its job starts.</param>
    private sealed record Request(string Slot, Worn Worn, string? Resolved, string? ModRoot, Guid Collection,
                                 bool Forced = false, IReadOnlyDictionary<string, Beside>? Beside = null,
                                 Chosen? Chosen = null, Worn? Walked = null,
                                 PenumbraBridge.ModSettingsSnapshot? ModSettings = null);

    /// <summary>A chest or legs piece worn beside an accessory at the walk: what it is, the file drawn and its mod.</summary>
    private sealed record Beside(Worn Worn, string? Resolved, string? ModRoot);

    /// <summary>
    /// What each body piece's own refit came to since the queue last ran dry, by <see cref="OutcomeKey"/> — what an
    /// accessory worn over it is sized by (see <see cref="AccessoryCover.FromBeneath"/>). Cleared when the queue runs dry
    /// and with everything queued (<see cref="CancelAll"/>): an outcome from an earlier batch may since have been
    /// switched away from.
    /// </summary>
    private readonly ConcurrentDictionary<string, PieceOutcome> outcomes = new(StringComparer.Ordinal);

    /// <summary>The author's own size switched on as the size chosen: made on that very size.</summary>
    private static AutoRefitDecisions.Pick OnTarget(BodyModIndex.Entry targetMod, BodyOption target)
        => new(targetMod.Dir, target, Confidence.Exact, false);

    /// <summary>A body piece as <see cref="outcomes"/> knows it: its slot, what it is, and the file it was drawn from.</summary>
    internal static string OutcomeKey(string slot, Worn worn) => slot + "|" + worn.Key + "|" + RefitCore.Normal(worn.Rel ?? "");

    /// <summary>Note what a body piece's refit came to, for an accessory, shoe or glove worn over it. Accessories note
    /// nothing.</summary>
    /// <param name="file">The file it is drawn from now, in its mod; null when not known.</param>
    private void Outcome(Request r, bool onTarget, AutoRefitDecisions.Pick? source, string? file = null)
    {
        if (AutoRefitDecisions.IsAccessory(r.Slot)) return;
        foreach (string key in OutcomeKeys(r.Slot, r.Worn, r.Walked)) outcomes[key] = new PieceOutcome(onTarget, source, file);
    }

    /// <summary>The keys a body piece's outcome is noted under: the file worked on, and the one the walk saw when that
    /// was another (see <see cref="Request.Walked"/>).</summary>
    internal static List<string> OutcomeKeys(string slot, Worn worn, Worn? walked)
    {
        var keys = new List<string> { OutcomeKey(slot, worn) };
        if (walked is { } w && OutcomeKey(slot, w) is var first && first != keys[0]) keys.Add(first);
        return keys;
    }

    /// <summary>The file a refit option of <paramref name="group"/> was saved as, by its record; null when none says.</summary>
    private static string? RecordedFile(string root, string group, string option, string gamePath)
    {
        var record = BodyRetargetWriter.ReadRecord(root);
        return record?.Options.FirstOrDefault(e => string.Equals(e.Name, option, StringComparison.OrdinalIgnoreCase)
                                                   && string.Equals(e.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)
                                                   && string.Equals(record.GroupOf(e), group,
                                                                    StringComparison.OrdinalIgnoreCase))?.File;
    }

    /// <summary>The design binding: a design restore holds mods with temporary settings, which a refit must change too.</summary>
    private readonly DesignBindingService designs;

    public AutoRefitWatcher(CompositorService compositor, PenumbraBridge penumbra, GlamourerBridge glamourer,
                            DesignBindingService designs, UVRemapService uvRemap, Func<string, byte[]?> readGameFile,
                            Configuration config, IPluginLog log, string dataDir)
    {
        detections = new DetectionCache(Path.Combine(dataDir, "autorefit-garments.json"), log);
        this.compositor = compositor;
        this.penumbra = penumbra;
        this.glamourer = glamourer;
        this.designs = designs;
        this.uvRemap = uvRemap;
        this.readGameFile = readGameFile;
        this.config = config;
        this.log = log;
        refitMods = new RefitModService(penumbra, compositor, log, ownedByAutoRefit: true);

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

    /// <summary>How long undo waits for a refit already running to finish before taking things out from under it.</summary>
    private const int UndoWaitMs = 30_000;

    private int undoing;

    /// <summary>An undo of the automatic refit is under way (see <see cref="UndoWorn"/>).</summary>
    public bool Undoing => Volatile.Read(ref undoing) != 0;

    /// <summary>
    /// Take back the automatic refit of what the player is wearing now, for one who does not like it: per body slot, the
    /// size it saved that the slot draws is taken out of the mod (see <see cref="AutoRefitUndo"/>), a mod it made for
    /// the game's gear is deleted once that leaves it empty, the group is put back to what was ticked before (where that
    /// was recorded — see <see cref="AutoRefitPreference.Before"/>), and the piece is remembered as declined in this
    /// collection (<see cref="AutoRefitPreference.Declined"/>) so it is not refitted again the next time it is put on.
    /// The author's own size switched on in a refit's stead is switched back the same way. A size made by hand in the
    /// Body size tool is left alone, and so is every refit not being worn. Framework thread; the work runs in the
    /// background and its outcome shows in <see cref="Current"/>.
    /// </summary>
    public void UndoWorn()
    {
        if (disposed || Interlocked.Exchange(ref undoing, 1) != 0) return;

        string? modsRoot = penumbra.GetModDirectory();
        var collection = penumbra.GetPlayerCollection();
        var worn = ReadWorn(penumbra.GetActivePlayerModelPaths(), modsRoot, out _);
        if (modsRoot is not { Length: > 0 } || collection is not { } c
            || !config.AutoRefitByCollection.TryGetValue(c.Id.ToString("D"), out var pref))
        {
            current = new View(Strings.AutoRefit.UndoNothing);
            Volatile.Write(ref undoing, 0);
            return;
        }

        // Nothing queued for these pieces may run after them: what is worn is about to be put back. Only these: a piece
        // put on in another slot and not refitted yet keeps its turn.
        CancelSlots(worn.Keys.ToList());
        var switchedOn = new HashSet<string>(pref.SwitchedOn, StringComparer.OrdinalIgnoreCase);
        var authorSwitched = new Dictionary<string, string>(pref.AuthorSwitchedOn, StringComparer.OrdinalIgnoreCase);
        Task? job;
        lock (queueGate) job = runner;
        current = new View(Strings.AutoRefit.UndoWorking, Busy: true);

        _ = Task.Run(async () =>
        {
            try
            {
                // A refit already saving finishes first: undone under its feet, its save would land after the undo.
                if (job != null) await Task.WhenAny(job, Task.Delay(UndoWaitMs));

                // Per slot: the piece's automatic refits, on disk — by the piece, whichever size it draws right now. A
                // piece with only sizes made by hand has nothing of the automatic refit's to take out.
                var results = new List<(Worn Worn, AutoRefitUndo.ModResult Result)>();
                var authorGroups = new List<(Worn Worn, List<string> Keys)>();
                foreach (var (slot, piece) in worn)
                {
                    if (piece.ModDir is not { } dir)
                    {
                        log.Information("[Proteus] auto refit undo: {0} {1} is the game's own, nothing to undo", slot, piece.GamePath);
                        continue;
                    }
                    string root = Path.Combine(modsRoot, dir);
                    // The author's own size switched on as the size chosen, in a group of THIS piece: nothing to remove,
                    // only to switch back. Read before the undo edits the manifest.
                    var mine = AutoRefitUndo.GroupsServing(root, piece.GamePath, authorSwitched.Keys);
                    if (mine.Count > 0) authorGroups.Add((piece, mine));

                    if (AutoRefitUndo.Undo(root, piece.GamePath, switchedOn) is { } result)
                    {
                        results.Add((piece, result));
                        log.Information("[Proteus] auto refit undo: {0} {1}: removed [{2}] from {3}{4}{5}", slot, piece.GamePath,
                                        string.Join(", ", result.Removed), dir,
                                        result.Failed.Count > 0 ? "; failed [" + string.Join("; ", result.Failed) + "]" : "",
                                        result.Empty ? "; mod now empty, deleting" : "");
                    }
                    else if (mine.Count == 0)
                        log.Information("[Proteus] auto refit undo: {0} {1} in {2} has no automatic refit (drawing {3})", slot,
                                        piece.GamePath, dir, piece.Rel ?? "");
                }

                await OnFramework(() =>
                {
                    foreach (var (_, r) in results)
                    {
                        if (r.Empty)
                            log.Information("[Proteus] auto refit undo: deleting {0}: {1}", r.Dir, penumbra.DeleteModDirectory(r.Dir));
                        else if (r.Removed.Count > 0)
                        {
                            compositor.ExpectOwnModEdit(r.Dir);
                            penumbra.ReloadModDirectory(r.Dir);
                        }
                    }
                    return true;
                });

                // On a later frame than the reloads, as Apply does: a setting written into a mod Penumbra is still
                // re-reading is lost.
                await Task.Delay(250);
                int restored = await OnFramework(() =>
                {
                    int count = 0;
                    var groups = results.Where(r => !r.Result.Empty)
                                        .SelectMany(r => r.Result.Groups.Select(g => AutoRefitDecisions.GroupKey(r.Result.Dir, g)))
                                        .Concat(authorGroups.SelectMany(a => a.Keys))
                                        .Distinct(StringComparer.OrdinalIgnoreCase);
                    // Every mod's settings once for the whole loop, not once per group per call (see Apply).
                    var effective = penumbra.GetCollectionModSettings(c.Id, ignoreTemporary: false);
                    var keys = groups.ToList();
                    foreach (string key in keys)
                    {
                        // Out of the record of groups switched on, so a refit asked for again records its "before"
                        // afresh — left in, a second undo of it had nothing to put back. The piece is declined below,
                        // so the restored selection is not read as the player's choice either.
                        pref.SwitchedOn.Remove(key);
                        pref.AuthorSwitchedOn.Remove(key);
                    }
                    foreach (string key in keys)
                    {
                        int bar = key.IndexOf('|');
                        string dir = key[..bar], group = key[(bar + 1)..];

                        // The collection's own setting, from what it had. A mod that had none — inherited, or never
                        // set — is handed back to inheritance instead, unless another group of it still carries a
                        // refit here: inheriting drops the whole mod's own setting, that refit with it.
                        bool hadBefore = pref.Before.Remove(key, out var before);
                        bool stillSwitched = pref.SwitchedOn.Any(k => k.StartsWith(dir + "|", StringComparison.OrdinalIgnoreCase));
                        // Per mod: kept while another group of it is still switched, and used by the last one undone.
                        if (!stillSwitched && pref.InheritedBefore.Remove(dir))
                        {
                            var ec = penumbra.SetModInherited(c.Id, dir);
                            log.Information("[Proteus] auto refit undo: {0} back to inheriting its setting: {1}", dir, ec);
                            if (ec is Penumbra.Api.Enums.PenumbraApiEc.Success or Penumbra.Api.Enums.PenumbraApiEc.NothingChanged)
                                count++;
                        }
                        else if (hadBefore)
                        {
                            var ec = penumbra.SetModOption(c.Id, dir, group, before!);
                            log.Information("[Proteus] auto refit undo: {0} / {1} back to [{2}]: {3}", dir, group,
                                            string.Join(", ", before!), ec);
                            if (ec is Penumbra.Api.Enums.PenumbraApiEc.Success or Penumbra.Api.Enums.PenumbraApiEc.NothingChanged)
                                count++;
                        }

                        // A design's hold, from what IT had — whatever the collection's own setting answered: an own
                        // setting already as it was says nothing about the hold, which may still name the refit just
                        // removed. A refit switched before the hold's own "before" was kept recorded the hold's
                        // selection as Before, so that stands in.
                        var heldBefore = pref.HeldBefore.Remove(key, out var hb) ? hb : hadBefore ? before : null;
                        if (heldBefore != null && designs.HeldSelection(c.Id, dir, group, effective) != null)
                            ChangeHold(c.Id, dir, group, heldBefore, effective);
                    }

                    // Remembered, so the piece is not refitted again the next time it goes on. The game's own gear is
                    // drawn from the game once its mod is gone, which is another key. Only the pieces undone — and those
                    // an undone option served with them (see AutoRefitUndo.ModResult.Paths): another piece of the same
                    // mod with a refit of its own keeps it.
                    foreach (var (piece, r) in results)
                    {
                        pref.Declined.Add(piece.Key);
                        if (r.Empty) pref.Declined.Add(new Worn(piece.GamePath, null, null).Key);
                        // The other pieces the options taken out served — the legs of an option sizing the chest and the
                        // legs together, gone with the chest — where worn or refitted: their own undo found nothing.
                        var wornHere = worn.Values.Where(w => string.Equals(w.ModDir, r.Dir, StringComparison.OrdinalIgnoreCase))
                                                  .Select(w => w.GamePath);
                        foreach (string path in AutoRefitUndo.ToDecline(r.Paths, r.Recorded, wornHere))
                            pref.Declined.Add(new Worn(path, r.Dir, null).Key);
                    }
                    foreach (var (piece, _) in authorGroups) pref.Declined.Add(piece.Key);

                    config.Save();
                    compositor.RedrawForChangedModel("auto refit undone");
                    return count;
                });

                int removed = results.Sum(r => r.Result.Removed.Count), failed = results.Sum(r => r.Result.Failed.Count);
                int gone = results.Count(r => r.Result.Empty);
                current = failed > 0
                    ? new View(string.Format(Strings.AutoRefit.UndoPartialFmt, removed, failed), Failed: true)
                    : removed == 0 && restored == 0 && authorGroups.Count == 0
                        ? new View(Strings.AutoRefit.UndoNothing)
                        : new View(string.Format(Strings.AutoRefit.UndoDoneFmt, removed, gone));
            }
            catch (OperationCanceledException)
            {
                // Unloading: nothing to tell anyone.
            }
            catch (Exception e)
            {
                log.Error(e, "[Proteus] auto refit undo failed");
                current = new View(string.Format(Strings.AutoRefit.UndoFailedFmt, e.Message), Failed: true);
            }
            finally
            {
                Volatile.Write(ref undoing, 0);
            }
        });
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

        var now = ReadWorn(models, penumbra.GetModDirectory(), out var resolvedOf);

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

            var skip = AutoRefitDecisions.ShouldRefit(slot, worn, config.AutoRefitVanilla, outside);
            if (skip != AutoRefitDecisions.Skip.None)
            {
                // A garment the second skin is appended into is drawn from Proteus's managed mod, rebuilt from its own
                // file by every shell build: a refit saved into its mod would never reach what is drawn. Named, so a
                // report of one left unrefitted can be told apart from jewellery.
                if (skip == AutoRefitDecisions.Skip.Managed && AutoRefitDecisions.IsAccessory(slot)
                    && compositor.GetAppendHostSource(worn.GamePath) is { } host)
                    log.Information("[Proteus] auto refit: {0} {1} hosts the second skin (from {2}), left as it is", slot,
                                    worn.GamePath, host);
                else
                    log.Debug("[Proteus] auto refit: {0} {1} left alone ({2})", slot, worn.GamePath, skip);
                continue;
            }

            // A piece whose refit the player undid stays as it is — unless they ask for what they are wearing now.
            if (pref.Declined.Contains(worn.Key))
            {
                if (!all)
                {
                    log.Information("[Proteus] auto refit: {0} {1} had its refit undone, left as it is", slot, worn.GamePath);
                    continue;
                }
                pref.Declined.Remove(worn.Key);
                config.Save();
            }
            // An accessory is sized off the chest or legs piece worn with it, and a shoe or glove is kept outside it
            // (OutfitPartners): what those draw now, before their own refits switch them to another file.
            bool accessory = AutoRefitDecisions.IsAccessory(slot);
            var beside = accessory || slot is "_glv" or "_sho"
                ? AutoRefitDecisions.Slots.Where(s => s is "_top" or "_dwn" && now.ContainsKey(s))
                                          .ToDictionary(s => s, s => new Beside(now[s], resolvedOf[s].Resolved,
                                                                                resolvedOf[s].Root))
                : null;
            // Read only for a body piece drawn from a mod: one call per piece, on the framework thread.
            var modSettings = !accessory && worn.ModDir != null
                              && penumbra.GetModSettings(collection.Id, worn.ModDir) is { } ms
                ? new PenumbraBridge.ModSettingsSnapshot(ms.Enabled, ms.Priority, ms.Options, Inherited: false, Temporary: false)
                : (PenumbraBridge.ModSettingsSnapshot?)null;
            Enqueue(new Request(slot, worn, resolved, root, collection.Id, all, beside,
                                accessory ? new Chosen(pref.BodyDir, pref.Chest?.Rel, pref.Legs?.Rel) : null,
                                ModSettings: modSettings));
        }
    }

    /// <summary>What each body and accessory slot draws: the mod and file supplying it, or the game's own. Framework
    /// thread.</summary>
    /// <param name="resolvedOf">Per slot, the file the game resolved, the mod folder holding it, and whether it is a file
    /// outside Penumbra's mods altogether.</param>
    private Dictionary<string, Worn> ReadWorn(HashSet<string>? models, string? modsRoot,
                                              out Dictionary<string, (string? Resolved, string? Root, bool Outside)> resolvedOf)
    {
        var now = new Dictionary<string, Worn>(StringComparer.Ordinal);
        resolvedOf = new Dictionary<string, (string? Resolved, string? Root, bool Outside)>(StringComparer.Ordinal);
        foreach (var (suffix, gamePath) in DrawnModelPaths.EquippedPartModelsFromModels(models)
                                                          .Concat(DrawnModelPaths.EquippedAccessoryGarmentModelsFromModels(models)))
        {
            string slot = "_" + suffix;
            var (worn, resolved, root, outside) = Resolve(gamePath, modsRoot);
            now[slot] = worn;
            resolvedOf[slot] = (resolved, root, outside);
        }
        return now;
    }

    /// <summary>What one game path draws for the player: the mod and file supplying it, or the game's own. Framework
    /// thread.</summary>
    private (Worn Worn, string? Resolved, string? Root, bool Outside) Resolve(string gamePath, string? modsRoot)
    {
        string? resolved = penumbra.ResolvePlayer(gamePath);
        if (resolved == null || !Path.IsPathRooted(resolved)) return (new Worn(gamePath, null, null), null, null, false);
        if (modsRoot != null && HatCompatService.InMods(resolved, modsRoot, out string root, out string rel))
            return (new Worn(gamePath, Path.GetFileName(root), rel), resolved, root, false);
        return (new Worn(gamePath, null, null), resolved, null, true);
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
            // Held from the first request, before its runner starts: nothing the batch causes may redraw in between.
            // Lock order: queueGate, then the hold's.
            compositor.HoldRedraws(AutoRefitDecisions.RunnerKey(runnerId), HoldLeaseMs, HoldCapMs);
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
            // What the batch switched so far is drawn now, not when its runner gets round to draining.
            if (runner != null) compositor.ReleaseRedraws(AutoRefitDecisions.RunnerKey(runnerId), 0);
        }
        outcomes.Clear();
    }

    /// <summary>
    /// Drop what is queued or running for <paramref name="slots"/>, and forget having seen only the slots that actually
    /// had something dropped, so a piece put on and not refitted yet is looked at again on the next walk. Forgetting
    /// every slot made every worn piece look newly put on, and an undo refitted the jacket worn beside the boots it undid.
    /// </summary>
    private void CancelSlots(IReadOnlyCollection<string> slots)
    {
        var dropped = new List<string>();
        lock (queueGate)
        {
            foreach (string slot in slots)
                if (queue.Remove(slot)) dropped.Add(slot);
            if (runningSlot != null && slots.Contains(runningSlot))
            {
                running?.Cancel();
                dropped.Add(runningSlot);
            }
        }
        if (lastSeen != null)
            foreach (string slot in dropped) lastSeen.Remove(slot);
    }

    /// <summary>
    /// Put a selection into the design hold on a mod as well, where a design restore holds it — see
    /// <see cref="DesignBindingService.ChangeHeldOption"/>. A hold outranks the collection's own setting: without this a
    /// refit switched on "Success" and went on drawing the author's size. Framework thread.
    /// </summary>
    private void ChangeHold(Guid collection, string dir, string group, IReadOnlyList<string> selection,
                            Dictionary<string, PenumbraBridge.ModSettingsSnapshot>? effective)
    {
        if (designs.ChangeHeldOption(collection, dir, group, selection, effective) is { } ec)
            log.Information("[Proteus] auto refit: {0} is held by a design; {1} set to [{2}] there too: {3}", dir, group,
                            string.Join(", ", selection), ec);
    }

    private async Task RunQueue(int id)
    {
        string holdKey = AutoRefitDecisions.RunnerKey(id);
        bool releasedAtDrain = false;
        bool drained = false;   // waited for this round's own evaluations already
        try
        {
            while (!disposed)
            {
                Request? request = null;
                CancellationTokenSource? cts = null;
                bool letGo = false;
                lock (queueGate)
                {
                    string? next = AutoRefitDecisions.WornSlots.FirstOrDefault(queue.ContainsKey);
                    if (next == null && drained)
                    {
                        // Cleared under the same lock Enqueue starts a runner under: a request arriving after this
                        // starts a new runner, and one arriving before it was taken by the loop.
                        runner = null;
                        outcomes.Clear();
                        FlushSummary();
                        // The batch is done: its redraws and composites go as one, after the grace.
                        compositor.ReleaseRedraws(holdKey, ReleaseGraceMs);
                        releasedAtDrain = true;
                        batchSwitchedDirs.Clear();
                        batchOurs.Clear();
                    }
                    else if (next == null)
                        letGo = true;
                    else
                    {
                        request = queue[next];
                        queue.Remove(next);
                        cts = running = new CancellationTokenSource();
                        runningSlot = next;
                        drained = false;
                        compositor.HoldRedraws(holdKey, HoldLeaseMs, HoldCapMs);
                    }
                }
                if (letGo)
                {
                    // Out of the lock: wait for the evaluations this batch's own switches started, so the composite they
                    // set off is let go with the rest rather than after it, then look at the queue once more.
                    await DrainOwnEvaluations();
                    drained = true;
                    continue;
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
            // Its own key only, and at once unless the drain already let it go.
            if (AutoRefitDecisions.RunnerExitRelease(releasedAtDrain) is { } grace)
                compositor.ReleaseRedraws(holdKey, grace);
            BodySizeMatch.ForgetCandidatesBeyond(CachedBodiesKept);
        }
    }

    /// <summary>
    /// At the end of a batch: wait (at most <see cref="DrainWaitMs"/>) for the body-mod evaluations and re-checks its
    /// own option switches set off, and run the walk's quiet on from the release the refits' redraw now waits for.
    /// </summary>
    private async Task DrainOwnEvaluations()
    {
        string[] dirs;
        lock (queueGate) dirs = [.. batchSwitchedDirs];
        var clock = Stopwatch.StartNew();
        while (!RedrawHold.ReleaseAfterWait(compositor.BodyEvaluationPending(dirs), clock.ElapsedMilliseconds, DrainWaitMs))
            await Task.Delay(50);

        long releaseAt = Environment.TickCount64 + ReleaseGraceMs;
        string[] pieces;
        lock (queueGate) pieces = [.. batchOurs];
        foreach (string piece in pieces)
            if (ours.TryGetValue(piece, out long until))
                ours[piece] = AutoRefitDecisions.QuietUntil(until, releaseAt, OwnChangeQuietMs);
    }

    // ── one refit ───────────────────────────────────────────────────────────

    /// <summary>What the framework thread hands a refit before it starts.</summary>
    /// <param name="Permanent">The collection's own settings, beneath any design's temporary ones: which body mods it has
    /// switched on, for the detection's cache key and its ties alike. The same object as <paramref name="Settings"/>
    /// when nothing is held temporarily.</param>
    /// <param name="Now">The piece's file read again as its job starts — see <see cref="AutoRefitDecisions.Reresolved"/>.
    /// Null for an accessory.</param>
    /// <param name="Drawn">Every model the character draws now, by game path: whether the other piece of a pair is
    /// worn.</param>
    /// <param name="GameBodyRaces">The races, of the piece's own and the shared shapes it falls through to, that the game
    /// ships bodies of (<c>"0201"</c>): which one the game's own racial gear is refitted as (see
    /// <see cref="RefitCore.Route"/>). Empty for a piece of the shared shapes, which is never bent down.</param>
    private sealed record Gathered(
        AutoRefitPreference Preference, IReadOnlyDictionary<string, string> Mods, string ModsRoot,
        IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot>? Settings,
        RacialModelBake.Wearer? Wearer, byte[]? VanillaBytes, VanillaPick? Vanilla,
        IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot>? Permanent,
        (Worn Worn, string? Resolved, string? Root)? Now = null, IReadOnlySet<string>? Drawn = null,
        IReadOnlySet<string>? GameBodyRaces = null);

    /// <summary>
    /// Run a short piece of a refit on the framework thread. Timed, because anything slow here is a frame the player
    /// sees drop: a hop over a few milliseconds is logged with what called it.
    /// <para/>
    /// After <see cref="Dispose"/> it throws <see cref="OperationCanceledException"/> instead. A refit's CPU work does not
    /// stop for the cancel — the planner and the writer never look at the token — so a refit caught by an unload or a
    /// dev reload would otherwise reload, select and redraw through bridges and a compositor already disposed; and the
    /// switch-on after a save runs with no token at all.
    /// <para/>
    /// The caller carries on on a WORKER afterwards, never on the framework thread — see <see cref="OffThread{T}"/>.
    /// Without it, everything a refit did after its first hop — sampling every body, ranking a pack's sizes, the refit
    /// itself, the save — ran inside the game's frame, and the game hung for as long as the refit took.
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
        return await OffThread(hop);
    }

    /// <summary>
    /// <paramref name="task"/>'s result, with whatever awaits it continuing on a thread-pool worker — never on the thread
    /// that completed it.
    /// <para/>
    /// An await continues INLINE on whichever thread completes what it awaited. <c>ConfigureAwait(false)</c> does not
    /// change that, and neither does <c>ConfigureAwaitOptions.ForceYielding</c>, which only yields when the task is
    /// already complete at the await — a hop to the framework thread completes a frame later, so with it the refit
    /// still ran inside the game's frame (testing-569/570 logged "running on the framework thread" on every refit, one
    /// of them 17 s). A completion source that runs its continuations asynchronously queues them instead.
    /// </summary>
    internal static async Task<T> OffThread<T>(Task<T> task)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted) done.TrySetException(t.Exception!.InnerExceptions);
            else if (t.IsCanceled) done.TrySetCanceled();
            else done.TrySetResult(t.Result);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await done.Task.ConfigureAwait(false);
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
        // A garment on an accessory slot: see AutoRefitWatcher.Accessories.cs.
        if (AutoRefitDecisions.IsAccessory(r.Slot))
        {
            await RunAccessory(r, ct);
            return;
        }
        var clock = Stopwatch.StartNew();
        bool isVanilla = r.ModRoot == null;

        var g = await OnFramework(() => Gather(r));
        if (g == null) return;
        ct.ThrowIfCancellationRequested();

        // ── what it draws now, which an earlier job of this pass may have switched ──
        if (g.Now is { } now)
            switch (AutoRefitDecisions.Reresolved(r.Worn, now.Worn))
            {
                case AutoRefitDecisions.Reread.OwnRefit:
                    log.Information("[Proteus] auto refit: {0} {1} is already drawing a refit ({2}), nothing to do", r.Slot,
                                    r.Worn.GamePath, now.Worn.Rel ?? "");
                    return;
                case AutoRefitDecisions.Reread.Abandon:
                    log.Information("[Proteus] auto refit: {0} {1} is no longer drawn from {2}, left to the next look — {3}",
                                    r.Slot, r.Worn.GamePath, r.Worn.ModDir ?? "the game",
                                    AutoRefitDecisions.AbandonDetail(now.Worn, r.Worn.ModDir,
                                        SettingsOf(g.Settings, r.Worn.ModDir), SettingsOf(g.Settings, now.Worn.ModDir),
                                        r.ModSettings));
                    return;
                case AutoRefitDecisions.Reread.Retarget:
                    log.Information("[Proteus] auto refit: {0} {1}: its file changed since it was put on ({2} → {3}); "
                                  + "refitting what is drawn now", r.Slot, r.Worn.GamePath, r.Worn.Rel ?? "",
                                    now.Worn.Rel ?? "");
                    r = r with { Worn = now.Worn, Resolved = now.Resolved, ModRoot = now.Root, Walked = r.Walked ?? r.Worn };
                    break;
            }

        // Kept current by Penumbra's add, delete and edit events; read fresh only before the first scan has finished.
        // The list and its version taken TOGETHER, and used for the whole refit: a refresh landing part way through must
        // not have the garment searched against one set of bodies and its reading filed under another's version.
        var state = Index.Current ?? Index.RefreshState(g.Mods, g.ModsRoot);
        var bodies = state.List;
        if (state.Find(g.Preference.BodyDir) is not { } targetMod)
        {
            SayBodyMissing(r, g);
            return;
        }
        var targetCat = targetMod.Catalog;
        string item = ItemName(r, g);
        string primary = r.Slot;

        // ── a size chosen for this part, before anything is read ──
        // Never a guess at the size itself: picking a body in Settings clears both sizes, and a refit in the moment before
        // they were chosen again took the body's first size — every piece put on came out in Neolithe "SFW XS". Hands and
        // feet follow whichever of chest and legs was chosen.
        bool chosen = primary switch
        {
            "_top" => g.Preference.Chest != null,
            "_dwn" => g.Preference.Legs != null,
            _ => g.Preference.Chest != null || g.Preference.Legs != null,
        };
        if (!chosen)
        {
            Say(string.Format(Loc.Localize("Chat.AutoRefit.NoSizeChosen.Fmt",
                    "[Proteus] No {0} size is chosen for {1}, so {2} was left as it is. Choose one under /proteus > "
                  + "Settings."), SlotName(primary), targetMod.Name, item), problem: true);
            return;
        }

        // ── the garment, baked for the wearer's race when it is drawn from another's ──
        byte[] bytes = isVanilla ? g.VanillaBytes ?? [] : await File.ReadAllBytesAsync(r.Resolved!, ct);
        if (bytes.Length == 0) return;

        List<PenumbraModMeta.Redirect> redirects = isVanilla ? [] : PenumbraModMeta.ReadAllRedirects(r.ModRoot!);
        var ownRecord = isVanilla ? null : BodyRetargetWriter.ReadRecord(r.ModRoot!);
        var targetRaces = RefitCore.RacesOf(targetCat);
        bool TargetHas(ushort code) => targetRaces.Contains($"{code:D4}");
        // The game's own gear is read on the game's own bodies, which most races have none of; a mod's garment was
        // made on some body mod, read whichever it is.
        bool gameMade = GameGear(r, redirects, ownRecord);
        var route = g.Wearer == null
            ? RacialModelBake.Route.None
            : RefitCore.Route(r.Worn.GamePath, g.Wearer.Race, TargetHas,
                              AutoRefitDecisions.ShapeBodies(TargetHas, gameMade, g.GameBodyRaces), redirects, ownRecord);
        if (route.Any)
        {
            var made = route.Into(bytes, g.Wearer!, out string why);
            if (made != null)
            {
                log.Information("[Proteus] auto refit: {0} {1} from c{2:D4} to c{3:D4}", route.Down ? "shaped" : "baked",
                                r.Worn.GamePath, route.Drawn, route.To);
                bytes = made;
            }
            else
            {
                log.Warning("[Proteus] auto refit: could not {0} {1} to c{2:D4}: {3}", route.Down ? "shape" : "bake",
                            r.Worn.GamePath, route.To, why);
                route = RacialModelBake.Route.None;
            }
        }
        string savePath = route.SavePath(r.Worn.GamePath);
        string race = route.Any ? route.Race(r.Worn.GamePath)! : ReadAs(r.Worn.GamePath).Race;
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
        // The piece's own part goes only onto a size the player chose (checked above). A part it merely reaches — the
        // legs of a dress, with only a chest size chosen — follows the chosen one, as hands and feet always do: left
        // out, the hem would keep the old body's thighs over the new body's skin.
        BodyOption? Follow(string slot) => (chestTo ?? legsTo) is { } anchor
            ? AutoRefitDecisions.Companion(targetCat.For(slot, race), anchor, worn[slot])
            : null;
        BodyOption? TargetFor(string slot) => slot switch
        {
            "_top" => chestTo ?? (slot == primary ? null : Follow(slot)),
            "_dwn" => legsTo ?? (slot == primary ? null : Follow(slot)),
            _ => Follow(slot),
        };

        // ── an author's option sizing the chest and the legs together: one option for both refits ──
        var together = PlanPair(r, g, targetMod, redirects, ownRecord, item, TargetFor);

        // Shoes and gloves keep their own feet and hands: only the legs or arms they reach are refitted. See KeepEnds.
        if (AutoRefitDecisions.LimbOf(primary) != null)
        {
            await RunKeepingEnds(new EndsJob(r, g, state, targetMod, item, bytes, redirects, ownRecord, route, savePath,
                                             race, male, garment, bones, vanillaCat, TargetFor, clock, together), ct);
            return;
        }

        if (TargetFor(primary) is not { } target)
        {
            Say(string.Format(Loc.Localize("Chat.AutoRefit.NoSizeForRace.Fmt",
                    "[Proteus] {0} has no {1} size for {2}, so {3} was left as it is."),
                    targetMod.Name, SlotName(primary), ModelRace.Describe(race), item), problem: true);
            return;
        }

        if (await SwitchToAuthorSize(r, g, item, redirects, ownRecord, route, targetMod, target, primary, race,
                                     state.Version, null, ct, together))
            return;

        // ── what it was made on, and which other parts of that body it reaches ──
        var (source, otherFrom) = ReadSource(g, state, targetMod, vanillaCat, bytes, garment, bones, primary, race,
                                             isVanilla, item);
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
            Outcome(r, onTarget: true, pick, r.Worn.Rel);
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
        if (WhereItGoes(r, g, targetMod, redirects, item) is not { } where) return;
        var (root, group, cutFrom) = where;
        if (together != null) cutFrom = together.Plan.CutFrom;

        string OptionFor(IEnumerable<(string Slot, BodyOption From, BodyOption To)> parts)
            => RefitCore.OptionName(targetMod.Name, new[] { target.Label }.Concat(parts.Select(p => p.To.Label)));

        // Sized together with other pieces in one of the author's single-choice groups: into the option another piece's
        // refit onto these sizes went into, so switching one on does not switch the other off. Worked out once: a part
        // left out below still goes into the option joined here.
        string option = OptionFor(others);
        string? joined = null;
        if (together is { Plan: { Paired: true, Option: { } shared } })
            option = joined = shared;
        else if (root != null && RefitCore.IsSharedSingleGroup(redirects, ownRecord, group, RefitCore.SingleGroups(root))
                 && RefitCore.JoinOption(ownRecord, group, savePath, targetMod.Dir, ChosenTargets(TargetFor),
                                         TargetsOf(primary, target, others), option) is { } join
                 && join != option)
            option = joined = join;

        // A legs garment whose waist lies over the belly: the chest beneath goes along for its shape (see
        // RefitCore.ChestBeneath) — never named in the option, only recorded. Not when its skin already reads the chest.
        BodyOption? chestBeneathTo = null;
        BodyRetarget.SlotPair chestBeneathPair = default;
        if (RefitCore.WantsChestBeneath(primary, otherFrom.Keys, chestTo, across))
        {
            void Note(string note) => log.Information("[Proteus] auto refit: {0}: {1}", item, note);
            if (RefitCore.ChestBeneathOf(garment, pick, race, state.List.Select(b => (b.Dir, b.Catalog)).ToList(),
                                         dir => dir == VanillaBodyCatalog.Key ? vanillaCat : state.Find(dir)?.Catalog,
                                         Note) is { } beneath
                && RefitCore.ChestBeneathPair(beneath, targetMod.Dir, targetCat, chestTo!, male,
                                              RefitCore.MaskOf(beneath.Dir, beneath.Catalog, "_top",
                                                               OptionsOf(g.Settings, beneath.Dir)),
                                              TargetMask("_top"), uvRemap, Note, out chestBeneathPair))
                chestBeneathTo = chestTo;
        }
        if (await LetBeOrPutBack(r, g, root, group, option, savePath, cutFrom, item, targetMod.Name, ct,
                                 pick, chestBeneath: chestBeneathTo?.Rel))
            return;

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
        if (chestBeneathTo != null) pairs.Add(chestBeneathPair);
        option = joined ?? OptionFor(kept);
        ct.ThrowIfCancellationRequested();

        AssertOffFramework("the refit");
        // Whole skin, but for the game's own gear — see AutoRefitDecisions.SkinFor.
        bool gameGear = GameGear(r, redirects, ownRecord);
        if (gameGear)
            log.Information("[Proteus] auto refit: {0} is the game's own gear: hidden skin cut, cloth cleared of the body",
                            item);
        bool piercings = config.AutoRefitPiercings;
        log.Debug("[Proteus] auto refit: {0}: piercings {1}", item, piercings ? "carried" : "left out");
        var plan = RefitCore.PlanRefit(garment, bytes, pairs, primary, across, gameGear, out var pieceNames,
                                       line => log.Debug("[Proteus] auto refit: {0}", line),
                                       line => log.Warning("[Proteus] auto refit: {0}", line),
                                       carryPiercings: piercings);
        plan = RefitCore.MatchSkinToBody(plan, sourcePath, targetPath, uvRemap, male, log);
        // Up, the kept skin named like the body's; down, bent back into the drawn race — or not saved at all.
        var (finished, _, bentBack) = RefitCore.Finish(plan, route, g.Wearer, targetPath, log);
        if (finished == null)
        {
            Fail(r, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"), item,
                               bentBack));
            return;
        }
        plan = finished;
        ct.ThrowIfCancellationRequested();

        string labelFrom = RefitCore.LabelFrom(across ? sourceName : null,
                                               new[] { pick.Option.Label }.Concat(kept.Select(k => k.From.Label)));
        var refit = new BodyRetargetWriter.Refit(option, plan.Model, string.Join(" + ",
                                                     new[] { target.Label }.Concat(kept.Select(k => k.To.Label))));
        if (await SaveAndWear(r, g, root, group, savePath, targetMod, labelFrom, refit, cutFrom, route, item, gameGear,
                              new BodyRetargetWriter.Provenance(pick.Dir, pick.Option.Rel,
                                                                TargetsOf(primary, target, kept, chestBeneathTo)))
            is not { } savedRoot)
            return;
        Outcome(r, onTarget: true, pick, RecordedFile(savedRoot, group, option, savePath));
        log.Information("[Proteus] auto refit: {0} {1} onto {2} in {3:F1}s (moved up to {4:F1} mm)", item, primary,
                        option, clock.Elapsed.TotalSeconds, plan.Report.WorstMove * 1000f);
        if (RefitCore.ReleasedLog(plan.Report, pieceNames) is { } released)
            log.Information("[Proteus] auto refit: {0}: let go of kept piece(s) that would sink moved whole: {1}", item,
                            released);

        string done = string.Format(Loc.Localize("Chat.AutoRefit.Done.Fmt",
                "[Proteus] {0} now fits {1}. Saved as \"{2}\" in {3} and switched on."),
                item, targetLabel, option, SavedIn(g, savedRoot, targetMod));
        if (dropped.Count > 0)
            done += " " + string.Format(Loc.Localize("Chat.AutoRefit.PartsLeftOut.Fmt",
                        "These parts could not be refitted with it and kept their size: {0}."),
                        string.Join(", ", dropped));
        Say(done);
        Finished(item, targetMod.Name);
    }

    /// <summary>
    /// The author's own size, when the mod already ships it, switched on instead of a refit.
    /// <para/>
    /// Farfalla offers "Rue M" beside "Neolithe XS", and its Rue M is Rue+'s Yiggle Medium exactly. Put on fresh, the
    /// shirt sits on its default — Neolithe XS — and refitting that to Yiggle Medium makes a copy of a size the author
    /// already made, worse than theirs; a refit that stopped short left the player in XS. Their own size is the answer.
    /// </summary>
    /// <param name="slot">The slot <paramref name="target"/> is a size of: the piece's own, or the legs a shoe is read on.</param>
    /// <param name="view">What of each size is compared — a shoe's leg without its kept foot (see
    /// <see cref="KeepEnds.Reading.LimbView"/>) — and the slot its comparison is cached under; null for whole models.</param>
    /// <returns>True when nothing more is to be done: the author's size is on, already was, or was switched away from.</returns>
    private async Task<bool> SwitchToAuthorSize(Request r, Gathered g, string item, List<PenumbraModMeta.Redirect> redirects,
                                                BodyRetargetWriter.Record? ownRecord, RacialModelBake.Route route,
                                                BodyModIndex.Entry targetMod, BodyOption target, string slot, string race,
                                                string bodiesVersion,
                                                (Func<byte[], ModelParts, ModelParts?> Of, string CacheSlot)? view,
                                                CancellationToken ct, PairContext? pair = null)
    {
        if (r.ModRoot == null || !AutoRefitDecisions.AuthorSizesApply(r.ModRoot, route)
            || RefitCore.SwitchingGroup(redirects, r.Worn.GamePath, ownRecord?.OwnGroups, RefitCore.SingleGroups(r.ModRoot))
               is not { } sizeGroup)
            return false;

        // The author's sizes only: a refit Proteus saved here before is a copy of OURS, put back on — with its save
        // path and other parts checked — by the refit's own "made before" step, and must not be called the author's.
        var refitNames = new HashSet<string>(
            ownRecord?.Options.Where(e => string.Equals(ownRecord.GroupOf(e), sizeGroup, StringComparison.OrdinalIgnoreCase))
                              .Select(e => e.Name) ?? [],
            StringComparer.OrdinalIgnoreCase);
        var sizes = RefitCore.SizesOf(redirects, sizeGroup, r.Worn.GamePath, refitNames);
        string? wornSize = BodyRetargetWriter.OptionOfFile(redirects, r.Worn.Rel!, sizeGroup);
        if (AuthorSizeCached(r.ModRoot, sizeGroup, sizes, targetMod, target, slot, race, bodiesVersion, view)
            is not { } authored)
            return false;

        // An option sizing the chest and the legs together, both worn: only an option that is the size chosen for BOTH
        // may be switched on — one that fits this piece alone would switch the other's size off, and the other's job
        // would then switch it back. Otherwise this piece is refitted into the option both refits share.
        if (pair != null && RefitCore.PairMustFitAll(pair.Plan, pair.OtherWorn, pair.Group, sizeGroup))
        {
            string modRoot = r.ModRoot;
            string? both = RefitCore.AuthorOptionForPieces(
                redirects, sizeGroup, r.Worn.GamePath, authored, pair.Plan.PairPaths,
                (otherSlot, otherPath, otherSizes) => pair.TargetFor(otherSlot) is { } otherTarget
                    ? AuthorSizeCached(modRoot, sizeGroup, otherSizes, targetMod, otherTarget, otherSlot,
                                       BodySizeCatalog.RaceOf(otherPath) ?? race, bodiesVersion)
                    : null,
                refitNames, path => g.Drawn?.Contains(path) == true);
            if (both == null)
            {
                log.Information("[Proteus] auto refit: {0}: the author's \"{1}\" fits it but not the other piece's size "
                              + "chosen; refitted into \"{2}\" instead", item, authored, pair.Plan.Option ?? "");
                return false;
            }
            authored = both;
        }

        // By file, not by name: two options carrying one model would name the worn one either way.
        if (string.Equals(authored, wornSize, StringComparison.OrdinalIgnoreCase)
            || sizes.Any(s => string.Equals(s.Option, authored, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(RefitCore.Normal(s.File), RefitCore.Normal(r.Worn.Rel!),
                                               StringComparison.OrdinalIgnoreCase)))
        {
            log.Information("[Proteus] auto refit: {0} is already the author's {1}, which is {2} / {3}", item,
                            authored, targetMod.Name, target.Label);
            Outcome(r, onTarget: true, OnTarget(targetMod, target));
            return true;
        }
        if (!r.Forced && ChoseOtherwise(g, r.ModRoot, sizeGroup))
        {
            log.Information("[Proteus] auto refit: {0} / {1} was switched away in this collection, left as chosen",
                            Path.GetFileName(r.ModRoot), sizeGroup);
            Outcome(r, onTarget: false, null);
            return true;
        }
        if (await Apply(r, r.ModRoot, sizeGroup, authored, wornSize, false, ct, authorSize: true) is { } offAuthor)
        {
            Fail(r, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"),
                                  item, offAuthor));
            return true;
        }
        Outcome(r, onTarget: true, OnTarget(targetMod, target));
        Say(string.Format(Loc.Localize("Chat.AutoRefit.Author.Fmt",
                "[Proteus] {0} already comes in {1}; its own \"{2}\" is switched on."),
                item, targetMod.Name + " — " + target.Label, authored));
        Finished(item, targetMod.Name);
        return true;
    }

    /// <summary>
    /// The mod and group a refit of the worn piece is saved into, and the option it was cut from: the author's size
    /// group where the mod has one, a group of its own otherwise, and for the game's gear a mod of Proteus's own (Root
    /// null when that mod is yet to be made). Null when that mod is switched off in this collection — the player taking
    /// the refit off, which is why the game's own model is showing, not a piece newly put on.
    /// </summary>
    private (string? Root, string Group, string? CutFrom)? WhereItGoes(Request r, Gathered g, BodyModIndex.Entry targetMod,
                                                                       List<PenumbraModMeta.Redirect> redirects, string item)
    {
        if (r.ModRoot == null)
        {
            string? made = RefitModService.Find(g.ModsRoot, g.Vanilla!.Value.ItemName, targetMod.Name);
            string own = string.Format(Strings.Parts.RetargetGroupFmt, g.Vanilla.Value.Label);
            if (made != null && g.Settings != null && g.Settings.TryGetValue(Path.GetFileName(made), out var held)
                && !held.Enabled)
            {
                log.Information("[Proteus] auto refit: {0} is switched off in this collection, left off", made);
                return null;
            }
            return (made, own, null);
        }

        string root = r.ModRoot;
        var record = BodyRetargetWriter.ReadRecord(root);
        var model = redirects.FirstOrDefault(d => RefitCore.Normal(d.File) == RefitCore.Normal(r.Worn.Rel!)
                                                 && string.Equals(d.GamePath, r.Worn.GamePath,
                                                                  StringComparison.OrdinalIgnoreCase));
        string group = RefitCore.SwitchingGroup(redirects, r.Worn.GamePath, record?.OwnGroups, RefitCore.SingleGroups(root),
                                                r.Worn.Rel, RefitCore.MultiGroups(root))
                    ?? string.Format(Strings.Parts.RetargetGroupFmt,
                                     model.GamePath != null ? Gui.PartsPanel.ModelLabel(model) : item);
        return (root, group, BodyRetargetWriter.OptionOfFile(redirects, r.Worn.Rel!, group));
    }

    /// <summary>
    /// A refit's option and cut-from as <see cref="RefitCore.PlanOption"/> decides them, for a mod's piece.
    /// </summary>
    /// <param name="Group">The group the refit goes into.</param>
    /// <param name="OtherWorn">The other piece of a pair option is being worn too.</param>
    /// <param name="TargetFor">The size each slot is refitted onto.</param>
    private sealed record PairContext(RefitCore.PairPlan Plan, string Group, bool OtherWorn,
                                      Func<string, BodyOption?> TargetFor);

    /// <summary>
    /// Where the refit of a mod's piece goes and is cut from, worked out before the author's sizes are looked at — which
    /// in an option sizing the chest and the legs together must think of both. Null for the game's own gear, which is
    /// never in an author's group, and keeps its order: see <see cref="WhereItGoes"/>.
    /// </summary>
    private PairContext? PlanPair(Request r, Gathered g, BodyModIndex.Entry targetMod,
                                  List<PenumbraModMeta.Redirect> redirects, BodyRetargetWriter.Record? ownRecord,
                                  string item, Func<string, BodyOption?> targetFor)
    {
        if (r.ModRoot == null || r.Worn.Rel == null) return null;
        if (WhereItGoes(r, g, targetMod, redirects, item) is not { Root: { } root } where) return null;
        string group = where.Group;
        var selected = OptionsOf(g.Settings, Path.GetFileName(root))?
                       .FirstOrDefault(o => string.Equals(o.Key, group, StringComparison.OrdinalIgnoreCase)).Value;
        bool single = RefitCore.SingleGroups(root).Contains(group, StringComparer.OrdinalIgnoreCase);
        var plan = RefitCore.PlanOption(redirects, ownRecord, group, single, selected, r.Worn.GamePath, r.Worn.Rel,
                                        targetMod.Name,
                                        slot => slot switch
                                        {
                                            "_top" => g.Preference.Chest != null,
                                            "_dwn" => g.Preference.Legs != null,
                                            _ => false,
                                        },
                                        targetFor);
        if (plan.Note != null) log.Information("[Proteus] auto refit: {0}: {1}", item, plan.Note);
        bool otherWorn = RefitCore.OtherPieceWorn(plan.PairPaths, r.Worn.GamePath, g.Drawn);
        if (plan.Paired)
            log.Information("[Proteus] auto refit: {0}: \"{1}\" / \"{2}\" sizes the chest and the legs together; its refits "
                          + "go into \"{3}\"{4}", item, group, plan.CutFrom ?? "", plan.Option ?? "",
                            otherWorn ? ", the other piece worn too" : "");
        return new PairContext(plan, group, otherWorn, targetFor);
    }

    /// <summary>
    /// Before any refit is worked out: the player switched away from a refit in this group — "Original", or one of the
    /// author's sizes, which reads as the piece newly put on and must not be undone — or this very option was made
    /// before and is still there, and is put back on.
    /// </summary>
    /// <param name="source">What the refit would be made from now (see <see cref="Existing"/>); null for no check.</param>
    /// <param name="layers">The other piece of its outfit it would be kept outside now (see <see cref="Existing"/>);
    /// null for none.</param>
    /// <param name="chestBeneath">The chest beneath a legs garment's waist it would be refitted along with now (see
    /// <see cref="Existing"/>); null for none.</param>
    /// <returns>True when nothing more is to be done.</returns>
    private async Task<bool> LetBeOrPutBack(Request r, Gathered g, string? root, string group, string option,
                                            string savePath, string? cutFrom, string item, string body,
                                            CancellationToken ct, AutoRefitDecisions.Pick? source = null,
                                            IReadOnlyDictionary<string, string>? layers = null,
                                            string? chestBeneath = null)
    {
        if (!r.Forced && root != null && ChoseOtherwise(g, root, group))
        {
            log.Information("[Proteus] auto refit: {0} / {1} was switched away from its refit in this collection, left as "
                          + "chosen", Path.GetFileName(root), group);
            Outcome(r, onTarget: false, null);
            return true;
        }
        if (root == null) return false;
        (string, string)? from = source == null ? null : (source.Dir, source.Option.Rel);
        if (!Existing(root, group, option, savePath, from, layers, r.Forced, chestBeneath))
        {
            if (from != null && !Existing(root, group, option, savePath, from) && Existing(root, group, option, savePath))
                log.Information("[Proteus] auto refit: {0}: \"{1}\" was made from another size; making it again", item,
                                option);
            else if (Existing(root, group, option, savePath, from))
                log.Information("[Proteus] auto refit: {0}: \"{1}\" was made for another piece or chest beside it than "
                              + "now; making it again", item, option);
            return false;
        }

        if (await Apply(r, root, group, option, cutFrom, r.ModRoot == null, ct) is { } offExisting)
        {
            Fail(r, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"),
                                  item, offExisting));
            return true;
        }
        Outcome(r, onTarget: true, source, RecordedFile(root, group, option, savePath));
        Say(string.Format(Loc.Localize("Chat.AutoRefit.Existing.Fmt",
                "[Proteus] {0} was already refitted to {1}; that size is switched on."), item, option));
        Finished(item, body);
        return true;
    }

    /// <summary>
    /// Save <paramref name="refit"/> into the mod and switch it on, making the mod first for the game's gear — only now
    /// that there is something to put in it.
    /// </summary>
    /// <param name="fromGame">The garment is the game's own (see <see cref="BodyRetargetWriter.Entry.FromGame"/>).</param>
    /// <param name="provenance">What the refit was made from and onto, for the record (see
    /// <see cref="BodyRetargetWriter.Entry.SourceDir"/>).</param>
    /// <returns>The mod it was saved into, or null when it failed, which the player has been told.</returns>
    private async Task<string?> SaveAndWear(Request r, Gathered g, string? root, string group, string savePath,
                                            BodyModIndex.Entry targetMod, string labelFrom, BodyRetargetWriter.Refit refit,
                                            string? cutFrom, RacialModelBake.Route route, string item, bool fromGame,
                                            BodyRetargetWriter.Provenance? provenance)
    {
        bool isVanilla = r.ModRoot == null;
        if (root == null)
        {
            var made = await OnFramework(() => refitMods.Ensure(g.Vanilla!.Value.ItemName, targetMod.Name,
                                                                    folder: RefitModService.OwnModsFolder));
            if (!made.Ok)
            {
                Fail(r, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"), item,
                                   made.Message));
                return null;
            }
            root = made.Root;
        }

        List<object>? manipulations = route.Eqdp(savePath) is { } eqdp ? [eqdp] : null;
        var outcome = BodyRetargetWriter.Save(root, group, savePath, targetMod.Dir, labelFrom, [refit], cutFrom,
                                              manipulations, auto: true, fromGame: fromGame, provenance: provenance);
        if (!outcome.Ok)
        {
            Fail(r, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"), item,
                               outcome.Message));
            return null;
        }

        if (await Apply(r, root, group, refit.Option, cutFrom, isVanilla, CancellationToken.None) is { } off)
        {
            // Saved, so the size is there to pick by hand; but it is not on, and must not be reported as worn.
            Fail(r, string.Format(Loc.Localize("Chat.AutoRefit.Failed.Fmt", "[Proteus] Couldn't refit {0}: {1}"), item,
                                  off));
            return null;
        }
        return root;
    }

    /// <summary>The target body's file chosen now for every body part that has one (see <see cref="RefitCore.JoinOption"/>).</summary>
    private static Dictionary<string, string> ChosenTargets(Func<string, BodyOption?> targetFor)
    {
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string slot in AutoRefitDecisions.Slots)
            if (targetFor(slot) is { } to) chosen[slot] = to.Rel;
        return chosen;
    }

    /// <summary>The target body's file per part a refit goes onto: its own, then each other part's — and the chest
    /// beneath a legs garment's waist, refitted along for its shape (see <see cref="RefitCore.ChestBeneath"/>).</summary>
    internal static Dictionary<string, string> TargetsOf(string slot, BodyOption target,
                                                         IEnumerable<(string Slot, BodyOption From, BodyOption To)> others,
                                                         BodyOption? chestBeneath = null)
    {
        var targets = new Dictionary<string, string>(StringComparer.Ordinal) { [slot] = target.Rel };
        foreach (var other in others) targets[other.Slot] = other.To.Rel;
        if (chestBeneath != null) targets[RefitCore.ChestBeneathKey] = chestBeneath.Rel;
        return targets;
    }

    /// <summary>What chat calls the mod a refit was saved into.</summary>
    private static string SavedIn(Gathered g, string root, BodyModIndex.Entry targetMod)
        => g.Vanilla is { } vanilla ? RefitModService.NameFor(vanilla.ItemName, targetMod.Name)
         : g.Mods.TryGetValue(Path.GetFileName(root), out string? modName) && modName.Length > 0 ? modName
         : Path.GetFileName(root);

    private static PenumbraBridge.ModSettingsSnapshot? SettingsOf(
        IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot>? settings, string? modDir)
        => modDir != null && settings != null && settings.TryGetValue(modDir, out var s) ? s : null;

    /// <summary>Collect what only the framework thread may read. Null when there is nothing to do after all.</summary>
    private Gathered? Gather(Request r)
    {
        if (!Enabled || disposed) return null;
        if (!config.AutoRefitByCollection.TryGetValue(r.Collection.ToString("D"), out var pref) || pref.BodyDir.Length == 0)
            return null;
        if (penumbra.GetAllMods() is not { } mods || penumbra.GetModDirectory() is not { } modsRoot) return null;

        var settings = penumbra.GetCollectionModSettings(r.Collection, ignoreTemporary: false);
        // Read again beneath the temporary settings only when some mod has one: every mod's settings are one call, and
        // not a cheap one.
        // A failed second call falls back to the first rather than reading every body mod as switched off.
        var permanent = settings != null && settings.Values.Any(s => s.Temporary)
            ? penumbra.GetCollectionModSettings(r.Collection, ignoreTemporary: true) ?? settings
            : settings;

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

        // A body piece's file, read again: see AutoRefitDecisions.Reresolved. Never an accessory's — the second skin's
        // host is drawn from Proteus's own mod, which this would read as another mod.
        (Worn, string?, string?)? now = null;
        if (!AutoRefitDecisions.IsAccessory(r.Slot))
        {
            var (worn, resolved, root, _) = Resolve(r.Worn.GamePath, modsRoot);
            now = (worn, resolved, root);
        }
        var drawn = penumbra.GetActivePlayerModelPaths() is { } models
            ? new HashSet<string>(models, StringComparer.OrdinalIgnoreCase)
            : null;
        // A copy for the worker: the framework thread changes the live sets in place (Apply, UndoWorn).
        return new Gathered(pref.Copy(), mods, modsRoot, settings, wearer, vanillaBytes, vanilla, permanent, now, drawn,
                            GameBodyRaces(r.Worn.GamePath));
    }

    /// <summary>
    /// The races down the same-sex fall-through from a racial piece's own that the game ships bodies of — read through
    /// the game, so here on the framework thread. Nothing is read for a piece already of a shared shape.
    /// </summary>
    private HashSet<string> GameBodyRaces(string gamePath)
    {
        var races = new HashSet<string>(StringComparer.Ordinal);
        ushort drawn = ModelSkinReader.RaceOf(gamePath);
        if (drawn / 100 is < 1 or > 18 || ModelRace.IsSharedShape($"{drawn:D4}")) return races;
        foreach (ushort race in RacialModelBake.SameSexLine(drawn).Prepend(drawn))
        {
            string code = $"{race:D4}";
            if (VanillaBodyCatalog.Read(code, readGameFile).IsBody) races.Add(code);
        }
        return races;
    }

    /// <summary>
    /// What a garment in <paramref name="slot"/> was made on, and the size of each other part of that body it reaches —
    /// read once and kept in the detection cache. One reading for every caller: a glove sized by the top beside it in the
    /// same option (<see cref="SiblingPiece"/>) reads that top here, under the very key the top's own refit reads it
    /// under, so whichever comes first the other finds it, other parts and all.
    /// </summary>
    private (AutoRefitDecisions.Pick? Source, Dictionary<string, string> Others) ReadSource(
        Gathered g, BodyModIndex.State state, BodyModIndex.Entry targetMod, BodySizeCatalog vanillaCat, byte[] bytes,
        ModelParts garment, IReadOnlySet<string> bones, string slot, string race, bool isVanilla, string item)
    {
        BodySizeCatalog? CatalogOf(string dir) => dir == VanillaBodyCatalog.Key ? vanillaCat : state.Find(dir)?.Catalog;
        string cacheKey = SourceCacheKey(bytes, slot, race, targetMod.Dir,
                                         DetectionCache.BodiesOf(state.Version, EnabledBodies(g, state),
                                                                 VanillaPrint(vanillaCat, slot, race)));
        if (detections.TryGet(cacheKey) is { } hit && Restore(hit, slot, race, CatalogOf) is { } restored)
        {
            log.Information("[Proteus] auto refit: {0} read before ({1}), not read again", item, hit.Confidence);
            return (restored.Pick, hit.Others);
        }

        AssertOffFramework("reading the garment");
        var source = isVanilla
            ? VanillaSource(vanillaCat, slot, race)
            : DetectAcross(garment, bones, slot, race, targetMod, state.List, vanillaCat, g.Permanent);
        var otherFrom = source is { } found && AutoRefitDecisions.Proceed(found.Confidence) && CatalogOf(found.Dir) is { } from
            ? OtherParts(garment, slot, race, found, from,
                         (s, options) => BodySizeMatch.Rank(garment, options, from.PathOf, bones))
            : new Dictionary<string, string>(StringComparer.Ordinal);
        detections.Put(cacheKey, new DetectionCache.Entry(source?.Dir ?? "", source?.Option.Rel ?? "",
                                                           source?.Confidence ?? Confidence.Ambiguous,
                                                           source?.FromCloth ?? false, otherFrom));
        return (source, otherFrom);
    }

    /// <summary>
    /// The slot and race a piece drawn at <paramref name="gamePath"/>, not baked, is read in — the walk's slot (see
    /// <see cref="DrawnModelPaths.EquippedPartModelsFromModels"/>) and the path's race. One answer for a piece's own refit
    /// and for a glove reading it as its sibling, so both read it under one cache key. Null slot for no body part.
    /// </summary>
    internal static (string? Slot, string Race) ReadAs(string gamePath)
        => (DrawnModelPaths.EquippedPartModelsFromModels([gamePath]).Keys.Select(s => "_" + s).FirstOrDefault(),
            BodySizeCatalog.RaceOf(gamePath) ?? "0201");

    /// <summary>The detection cache's key for a garment read in <paramref name="slot"/> — see
    /// <see cref="DetectionCache.KeyOf"/>.</summary>
    internal static string SourceCacheKey(byte[] bytes, string slot, string race, string targetDir, string bodies)
        => DetectionCache.KeyOf(bytes, slot, race, targetDir, bodies);

    /// <summary>
    /// The body mods switched on in the collection's own choice — beneath any design's temporary setting, which comes and
    /// goes with every design; counted, it would make each design change read every garment again. The detection's ties
    /// read the same.
    /// </summary>
    private static IEnumerable<string> EnabledBodies(Gathered g, BodyModIndex.State state)
        => state.List.Where(b => g.Permanent != null && g.Permanent.TryGetValue(b.Dir, out var s) && s.Enabled
                                 && !s.Temporary)
                     .Select(b => b.Dir);

    /// <summary>
    /// The size, per other body part the garment reaches, of the body it was made on — the legs of a dress beside its
    /// chest — by file. A part goes in only where its ranking agrees with the garment's own size (see
    /// <see cref="AutoRefitDecisions.AcceptOther"/>).
    /// </summary>
    /// <param name="rank">The garment ranked against one part's sizes; a test passes its own.</param>
    internal static Dictionary<string, string> OtherParts(ModelParts garment, string slot, string race,
                                                          AutoRefitDecisions.Pick source, BodySizeCatalog from,
                                                          Func<string, IReadOnlyList<BodyOption>, BodySizeMatch.Ranking> rank)
    {
        var otherFrom = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string other in AutoRefitDecisions.Slots)
        {
            if (other == slot || from.For(other, race).Count == 0) continue;
            var ranking = rank(other, from.For(other, race));
            if (AutoRefitDecisions.AcceptOther(ranking, source.Option)) otherFrom[other] = ranking.Best!.Value.Option.Rel;
        }
        return otherFrom;
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
        var order = RefitCore.SearchOrder(targetMod.Dir, targetMod.Catalog, bodies.Select(b => (b.Dir, b.Catalog)), vanilla);

        // The collection's own settings (Gathered.Permanent), as the cache key counts them: a design's temporary setting
        // is not the collection's, and a reading tied by it would be filed under a key that does not record it. A
        // temporary one is left out too, for when the permanent read failed and these are the effective settings.
        return RefitCore.DetectSource(garment, bones, slot, race, order, targetMod.Dir,
                                      dir => settings != null && settings.TryGetValue(dir, out var s) && s.Enabled
                                             && !s.Temporary,
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

    /// <summary>
    /// <see cref="AuthorSize"/>, kept with the garment readings: it reads every size the group offers, and a pack-made
    /// garment offers thirty, so a piece put on again must not read them all again. Keyed by each option's file as it
    /// stands on disk (name, length, time written), so an author's update or a size added changes the key, and by the
    /// installed bodies' version, which changes with any edit to the body the sizes are compared against.
    /// </summary>
    /// <param name="view">See <see cref="SwitchToAuthorSize"/>.</param>
    private string? AuthorSizeCached(string modRoot, string group, IReadOnlyList<(string Option, string File)> sizes,
                                     BodyModIndex.Entry targetMod, BodyOption target, string slot, string race,
                                     string bodiesVersion,
                                     (Func<byte[], ModelParts, ModelParts?> Of, string CacheSlot)? view = null)
    {
        var files = new System.Text.StringBuilder();
        foreach (var (option, file) in sizes.OrderBy(s => s.Option, StringComparer.Ordinal))
        {
            var info = new FileInfo(BodySizeCatalog.ResolveCaseInsensitive(modRoot, file));
            files.Append(option).Append('|').Append(RefitCore.Normal(file)).Append('|')
                 .Append(info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-").Append('\n');
        }
        string key = "author|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                                     System.Text.Encoding.UTF8.GetBytes(files.ToString())))
                   + $"|{Path.GetFileName(modRoot).ToLowerInvariant()}|{group}|{view?.CacheSlot ?? slot}|{race}"
                   + $"|{targetMod.Dir.ToLowerInvariant()}|{RefitCore.Normal(target.Rel)}|{bodiesVersion}";
        // An entry's Rel is the author's option, or "" for none.
        if (detections.TryGet(key) is { } hit) return hit.Rel.Length > 0 ? hit.Rel : null;

        AssertOffFramework("reading the author's sizes");
        string? found = AuthorSize(modRoot, sizes, targetMod.Catalog, target, slot, race, view?.Of);
        detections.Put(key, new DetectionCache.Entry(targetMod.Dir, found ?? "",
                                                     found != null ? Confidence.Exact : Confidence.Ambiguous, false, []));
        return found;
    }

    /// <summary>
    /// The author's own option in a size group whose model IS <paramref name="target"/> — the body skin it carries a
    /// copy of that size, not merely near it — or null when none is.
    /// <para/>
    /// Cheap first: every option's share of skin points lying exactly on the target body (see
    /// <see cref="BodySizeMatch.Shared"/>; the other sizes of one body share their arms and back, so only the very size
    /// reaches <see cref="BodySizeMatch.Exact"/>). Then the best is ranked against the target mod's sizes as the Studio
    /// ranks them, and must come out an exact copy of that very size.
    /// </summary>
    /// <param name="view">What of each size's model is compared, from its bytes and parts: a shoe's leg without its kept
    /// foot, which on a heeled shoe lies on no body and holds the share of the rest below <see cref="BodySizeMatch.Exact"/>.
    /// Null compares whole models. A size it gives nothing for — a model whose rig cannot be read, so its foot cannot be
    /// told from its leg — is not compared at all.</param>
    internal static string? AuthorSize(string modRoot, IReadOnlyList<(string Option, string File)> sizes,
                                       BodySizeCatalog targetCat, BodyOption target, string slot, string race,
                                       Func<byte[], ModelParts, ModelParts?>? view = null)
    {
        string targetPath = targetCat.PathOf(target);
        using var reading = BodySizeMatch.BeginReading();
        float bestShare = 0f;
        string? best = null;
        ModelParts? bestModel = null;
        byte[]? bestBytes = null;
        foreach (var (option, file) in sizes)
        {
            string path = BodySizeCatalog.ResolveCaseInsensitive(modRoot, file);
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            if (ModelPartReader.Read(bytes) is not { } read) continue;
            if ((view == null ? read : view(bytes, read)) is not { } model) continue;   // no leg part to compare
            float share = BodySizeMatch.Shared(BodySizeMatch.ProbeKeys(model), targetPath, out _);
            if (share <= bestShare) continue;
            (bestShare, best, bestModel, bestBytes) = (share, option, model, bytes);
        }
        // Seen through a view, part of what is compared lies on another slot's body — a shoe's shin is on the feet
        // model, under the legs' hem — and never reaches the share a whole copy would; the ranking below, which reads
        // only where the bodies are, still has to call it an exact copy of that very size.
        if (best == null || bestShare < (view == null ? BodySizeMatch.Exact : 0f) || bestShare <= 0f) return null;

        var bones = new HashSet<string>(SecondSkinWriter.Parse(bestBytes!).BoneNames, StringComparer.Ordinal);
        var ranking = BodySizeMatch.Rank(bestModel!, targetCat.For(slot, race), targetCat.PathOf, bones);
        return ranking.Confidence == Confidence.Exact && ranking.Best is { } top
               && RefitCore.Normal(top.Option.Rel) == RefitCore.Normal(target.Rel)
            ? best
            : null;
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

    /// <summary>Whether the worn piece is the game's own garment — see <see cref="AutoRefitDecisions.IsGameGear"/>.</summary>
    private static bool GameGear(Request r, IReadOnlyList<PenumbraModMeta.Redirect> redirects,
                                 BodyRetargetWriter.Record? record)
        => AutoRefitDecisions.IsGameGear(r.ModRoot, r.Worn.GamePath, redirects, record,
                                         r.ModRoot != null && RefitModService.IsMadeByProteus(r.ModRoot));

    /// <summary>The game's own gear is made on the game's own body: one size, no search.</summary>
    private static AutoRefitDecisions.Pick? VanillaSource(BodySizeCatalog vanilla, string slot, string race)
        => KeepEnds.VanillaPick(vanilla, slot, race);

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
        // The author's size switched on in its stead is as much ours: still worn, it is not the player's choice.
        if (g.Preference.AuthorSwitchedOn.TryGetValue(AutoRefitDecisions.GroupKey(dir, group), out string? authored))
            refits = refits.Append(authored);
        return AutoRefitDecisions.ChoseOtherwise(before, ticked, refits);
    }

    /// <summary>
    /// Whether this exact refit is already saved in the mod and still offered by the group — made from
    /// <paramref name="source"/>, where its entry records what it was made from: one read from another size before (a
    /// glove sized by its cloth, before it was sized by its top) is made again rather than put back on. An entry from
    /// before sources were recorded is put back, as it always was.
    /// </summary>
    /// <param name="source">The body mod folder and file the refit would be made from now; null compares no source.</param>
    /// <param name="layers">The other piece of its outfit it would be kept outside now (see
    /// <see cref="OutfitPartners.SameLayers"/>); null for none.</param>
    /// <param name="forced">Asked for with "Refit what I'm wearing now": a refit made before outfits were kept in
    /// order, or before a legs garment's chest went along, is made again rather than put back.</param>
    /// <param name="chestBeneath">The target chest a legs garment's waist is refitted along with now (see
    /// <see cref="SameChestBeneath"/>); null for none.</param>
    internal static bool Existing(string root, string group, string option, string savePath,
                                  (string Dir, string Rel)? source = null,
                                  IReadOnlyDictionary<string, string>? layers = null, bool forced = false,
                                  string? chestBeneath = null)
    {
        var record = BodyRetargetWriter.ReadRecord(root);
        if (record == null) return false;
        bool recorded = record.Options.Any(e => string.Equals(e.Name, option, StringComparison.OrdinalIgnoreCase)
                                                && string.Equals(e.GamePath, savePath, StringComparison.OrdinalIgnoreCase)
                                                && string.Equals(record.GroupOf(e), group, StringComparison.OrdinalIgnoreCase)
                                                && SameSource(e, source)
                                                && OutfitPartners.SameLayers(e.Layers, layers, forced)
                                                && SameChestBeneath(e, chestBeneath, forced));
        return recorded && (PenumbraModMeta.TryReadFileOptions(root, group) ?? [])
                           .Any(o => string.Equals(o.Name, option, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a legs garment's refit went along with <paramref name="chestBeneath"/> (see <see cref="TargetsOf"/>): only
    /// legs entries are asked, and only when a chest goes along now. One made onto another chest is made again — the
    /// belly differs between them; one recording none was made before the chest went along, and is put back as it was
    /// unless the player asked for what they are wearing now.
    /// </summary>
    private static bool SameChestBeneath(BodyRetargetWriter.Entry e, string? chestBeneath, bool forced)
    {
        if (chestBeneath == null || !e.GamePath.EndsWith("_dwn.mdl", StringComparison.OrdinalIgnoreCase)) return true;
        if (e.Targets == null || !e.Targets.TryGetValue(RefitCore.ChestBeneathKey, out string? recorded)) return !forced;
        return RefitCore.Normal(recorded) == RefitCore.Normal(chestBeneath);
    }

    /// <summary>Whether an entry was made from <paramref name="source"/>: by body mod folder and file where it records
    /// them, and taken as so where it does not.</summary>
    private static bool SameSource(BodyRetargetWriter.Entry e, (string Dir, string Rel)? source)
        => source is not { } s || e.SourceDir == null || e.SourceRel == null
           || (string.Equals(e.SourceDir, s.Dir, StringComparison.OrdinalIgnoreCase)
               && RefitCore.Normal(e.SourceRel) == RefitCore.Normal(s.Rel));

    /// <summary>
    /// Wear what was saved: Penumbra re-reads the mod, then the option is selected — in that order, or an option the
    /// save just added does not exist to it yet — and the character is redrawn.
    /// </summary>
    /// <param name="cutFrom">The option the refitted model came from: in a multi-choice group, the one it replaces.</param>
    /// <returns>Null when the refit is on; otherwise what Penumbra answered, for the player — a refit that was saved but
    /// could not be switched on must not be reported as worn.</returns>
    private async Task<string?> Apply(Request r, string root, string group, string option, string? cutFrom,
                                      bool madeForGame, CancellationToken ct, bool authorSize = false)
    {
        string dir = Path.GetFileName(root);
        bool multi = RefitCore.IsMulti(root, group);
        // The batch's own writes: what they set off waits for the batch's end (see CompositorService.ExpectOwnRefitWrite).
        lock (queueGate) batchSwitchedDirs.Add(dir);
        await OnFramework(() =>
        {
            compositor.ExpectOwnModEdit(dir);
            compositor.ExpectOwnRefitWrite(dir);
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
            // Marked again in this framework step: the registration wait above may have outlived the first mark.
            compositor.ExpectOwnRefitWrite(dir);
            var settings = penumbra.GetModSettings(r.Collection, dir);
            if (madeForGame && settings is not { Enabled: true })
                penumbra.SetModEnabled(r.Collection, dir, true);

            // The collection's OWN setting, beneath any design hold, and the hold's — each switched from what it has
            // itself, and remembered apart (see AutoRefitPreference.HeldBefore). Every mod's settings are one call and
            // not a cheap one: read once, and read again beneath the temporary ones only when this mod is held.
            var effective = penumbra.GetCollectionModSettings(r.Collection, ignoreTemporary: false);
            var underneath = effective != null && effective.TryGetValue(dir, out var e) && e.Temporary
                ? penumbra.GetCollectionModSettings(r.Collection, ignoreTemporary: true)
                : effective;
            bool ownSetting = underneath != null && underneath.TryGetValue(dir, out var mine) && !mine.Inherited;
            var ticked = underneath != null && underneath.TryGetValue(dir, out mine)
                ? mine.Options.FirstOrDefault(o => string.Equals(o.Key, group, StringComparison.OrdinalIgnoreCase)).Value
                : null;
            var held = designs.HeldSelection(r.Collection, dir, group, effective);

            var selection = RefitCore.SelectionFor(multi, option, ticked, cutFrom);
            var ec = penumbra.SetModOption(r.Collection, dir, group, selection);
            log.Information("[Proteus] auto refit: selected {0} / {1} in {2}: {3}", group, string.Join(", ", selection),
                            dir, ec);
            if (ec is not (Penumbra.Api.Enums.PenumbraApiEc.Success or Penumbra.Api.Enums.PenumbraApiEc.NothingChanged))
                return ec.ToString();
            if (held != null)
                ChangeHold(r.Collection, dir, group, RefitCore.SelectionFor(multi, option, held, cutFrom), effective);

            // Remembered, so that the player picking something else in this group later is taken as their choice.
            // An author's size switched on is remembered by name, so that it too reads as ours (see ChoseOtherwise).
            if (config.AutoRefitByCollection.TryGetValue(r.Collection.ToString("D"), out var pref))
            {
                string key = AutoRefitDecisions.GroupKey(dir, group);
                // Whether the collection had a setting of its own for the mod, judged when Proteus switches the FIRST
                // group of it: by the second, the setting the first switch made would read as the player's.
                if (!ownSetting && !pref.SwitchedOn.Any(k => k.StartsWith(dir + "|", StringComparison.OrdinalIgnoreCase)))
                    pref.InheritedBefore.Add(dir);
                // What was ticked before the FIRST switch, for undo: a later switch would only record a refit.
                if (!pref.SwitchedOn.Contains(key) && !pref.Before.ContainsKey(key))
                {
                    pref.Before[key] = ticked?.ToList() ?? [];
                    if (held != null) pref.HeldBefore[key] = [.. held];
                }
                bool changed = pref.SwitchedOn.Add(key);
                if (authorSize)
                {
                    changed |= !pref.AuthorSwitchedOn.TryGetValue(key, out string? had) || had != option;
                    pref.AuthorSwitchedOn[key] = option;
                }
                else changed |= pref.AuthorSwitchedOn.Remove(key);
                if (changed) config.Save();
            }

            // The walk after this redraw sees the refit worn and must leave it be.
            long quiet = Environment.TickCount64 + OwnChangeQuietMs;
            string piece = r.Slot + "|" + r.Worn.GamePath;
            ours[piece] = quiet;
            lock (queueGate) batchOurs.Add(piece);
            // Held for the one redraw at the batch's end while the batch runs.
            compositor.RedrawForChangedModel($"auto refit: {r.Slot} switched on", deferWhileHeld: true);
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

    internal static string SlotName(string slot) => slot switch
    {
        "_top" => Strings.Parts.RetargetChest,
        "_dwn" => Strings.Parts.RetargetLegs,
        "_glv" => Strings.Parts.RetargetHands,
        "_sho" => Strings.Parts.RetargetFeet,
        "_nek" => Strings.Common.SlotNecklace,
        "_ear" => Strings.Common.SlotEarrings,
        "_wrs" => Strings.Common.SlotBracelets,
        "_rir" => Strings.Common.SlotRightRing,
        "_ril" => Strings.Common.SlotLeftRing,
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
        Outcome(r, onTarget: false, null);
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
