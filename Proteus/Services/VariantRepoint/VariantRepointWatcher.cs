using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Proteus.Interop;
using Proteus.Localization;
using static Proteus.Services.VariantRepointPlan;

namespace Proteus.Services;

/// <summary>
/// "Gear variants": when <see cref="Configuration.AutoRepointVariants"/> is on, a worn piece whose mod ships one variant's
/// material folder, worn in a variant that uses another, has every such variant pointed at the mod's folder by IMC
/// entries written into the mod's default data (<see cref="VariantRepointWriter"/>: recorded, undoable) — what TexTools'
/// "Apply to All Variants" does by hand. Turning the setting off only stops further writes.
/// <para/>
/// Orchestration only, as <see cref="ShaderUpgradeWatcher"/>: decisions are in <see cref="VariantRepointPlan"/>, every
/// write and undo in the writer. One run at a time; a run reads the draw object on the framework thread, plans and writes
/// on a worker, and reloads on the framework thread; the worker blocks on its hops, the framework thread never waits.
/// </summary>
public sealed class VariantRepointWatcher : IDisposable
{
    private const long SlowFrameMs = 8;
    private const int BlanksBeforeNone = 4;

    private readonly CompositorService compositor;
    private readonly PenumbraBridge penumbra;
    private readonly GlamourerBridge glamourer;
    private readonly AutoRefitWatcher autoRefit;
    private readonly ShaderUpgradeWatcher shaderUpgrade;
    private readonly Func<string, byte[]?> readGameFile;
    private readonly Configuration config;
    private readonly IPluginLog log;

    private readonly HashSet<string> announced = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> refreshPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object scheduleSync = new();

    private volatile bool stopped;
    /// <summary>Something was written with no redraw after it (Auto redraw off): until the next redraw, the status says so.</summary>
    private volatile bool awaitingRedraw;
    private bool wasEnabled;
    private int busy, blanks;
    private long walkDueAt, firstRequestAt;

    /// <summary>One worn piece, as QA cross-checks it: the mod it is drawn from, the material folder in use and loaded.</summary>
    public sealed record WornEntry(string? ModDir, string Item, int Variant, int? EffectiveMaterialId, string? LoadedFolder);

    /// <summary>One worn piece whose variants Proteus pointed at its mod's folder.</summary>
    /// <param name="Folders">The material folders Proteus copied the mod's material paths into.</param>
    /// <param name="Variants">The variants those folders fix.</param>
    /// <param name="LegacyImcVariants">Variants with an IMC entry written by build 1428 (record version 1).</param>
    public sealed record RepointedEntry(string ModDir, string Item, int MaterialId, IReadOnlyList<int> Folders,
                                        IReadOnlyList<int> Variants, IReadOnlyList<int>? LegacyImcVariants = null)
    {
        public IReadOnlyList<int> LegacyList => LegacyImcVariants ?? [];
    }

    public sealed record LeftEntry(string ModDir, string Item, string Reason);

    /// <summary>What the last run did.</summary>
    /// <param name="FoldersWritten">Material folders copied into, over every mod changed.</param>
    public sealed record RunInfo(string Kind, int ModsChanged, int FoldersWritten, int Removed, int Kept, int Refused,
                                 bool Reloaded, bool RedrawRequested, DateTime AtUtc, string Message = "",
                                 bool NothingRecorded = false);

    /// <summary>Everything the panel and the harness show, replaced wholesale.</summary>
    public sealed record View(
        bool Enabled = false, bool Busy = false, string Waiting = "none", bool AwaitingRedraw = false,
        IReadOnlyList<WornEntry>? Worn = null, IReadOnlyList<RepointedEntry>? Repointed = null,
        IReadOnlyList<LeftEntry>? Left = null, RunInfo? LastRun = null)
    {
        public IReadOnlyList<WornEntry> WornList => Worn ?? [];
        public IReadOnlyList<RepointedEntry> RepointedList => Repointed ?? [];
        public IReadOnlyList<LeftEntry> LeftList => Left ?? [];
    }

    private volatile View current = new();
    public View Current => current with { Enabled = Enabled, Busy = Volatile.Read(ref busy) != 0 };

    public VariantRepointWatcher(CompositorService compositor, PenumbraBridge penumbra, GlamourerBridge glamourer,
                                 AutoRefitWatcher autoRefit, ShaderUpgradeWatcher shaderUpgrade,
                                 Func<string, byte[]?> readGameFile, Configuration config, IPluginLog log)
    {
        this.compositor = compositor;
        this.penumbra = penumbra;
        this.glamourer = glamourer;
        this.autoRefit = autoRefit;
        this.shaderUpgrade = shaderUpgrade;
        this.readGameFile = readGameFile;
        this.config = config;
        this.log = log;

        glamourer.LocalPlayerEquipmentChanged += OnEquip;
        glamourer.LocalPlayerStateChangedAny += OnStateChangedAny;
        penumbra.LocalPlayerRedrawn += OnRedrawn;
        penumbra.PlayerCollectionChanged += OnCollectionChanged;
        penumbra.ModSettingChanged += OnModSettingChanged;
        penumbra.PenumbraReady += OnPenumbraReady;
        penumbra.ModMoved += OnModMoved;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        // A run checks this before every item and before its reload hop, and writes nothing more.
        stopped = true;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        glamourer.LocalPlayerEquipmentChanged -= OnEquip;
        glamourer.LocalPlayerStateChangedAny -= OnStateChangedAny;
        penumbra.LocalPlayerRedrawn -= OnRedrawn;
        penumbra.PlayerCollectionChanged -= OnCollectionChanged;
        penumbra.ModSettingChanged -= OnModSettingChanged;
        penumbra.PenumbraReady -= OnPenumbraReady;
        penumbra.ModMoved -= OnModMoved;
    }

    private bool Enabled => config.PluginEnabled && config.AutoRepointVariants;

    // ── signals ─────────────────────────────────────────────────────────────

    private void Request(ShaderUpgradePlan.Signal s)
    {
        if (stopped || !Enabled) return;
        lock (scheduleSync)
        {
            var (due, first) = ShaderUpgradePlan.NextDue(Environment.TickCount64, ShaderUpgradePlan.SignalFor(s),
                                                         Interlocked.Read(ref walkDueAt), firstRequestAt);
            firstRequestAt = first;
            Interlocked.Exchange(ref walkDueAt, due);
        }
    }

    private void OnEquip() => Request(ShaderUpgradePlan.Signal.Equip);

    private void OnStateChangedAny(Glamourer.Api.Enums.StateChangeType change)
    {
        if (change is Glamourer.Api.Enums.StateChangeType.Equip or Glamourer.Api.Enums.StateChangeType.Stains
            or Glamourer.Api.Enums.StateChangeType.Design or Glamourer.Api.Enums.StateChangeType.Reset
            or Glamourer.Api.Enums.StateChangeType.BonusItem or Glamourer.Api.Enums.StateChangeType.Other)
            Request(ShaderUpgradePlan.Signal.Equip);
    }

    private void OnRedrawn()
    {
        // The reload and redraw after our own write have landed: the collection's entries can be judged again.
        lock (refreshPending) refreshPending.Clear();
        awaitingRedraw = false;
        current = current with { AwaitingRedraw = false };
        Request(ShaderUpgradePlan.Signal.Redraw);
    }

    private void OnCollectionChanged() => Request(ShaderUpgradePlan.Signal.Collection);
    private void OnPenumbraReady() => Request(ShaderUpgradePlan.Signal.PenumbraReady);
    private void OnModMoved(string oldDir, string newDir) => Request(ShaderUpgradePlan.Signal.ModMoved);

    private void OnModSettingChanged(ModSettingChange change, Guid collection, string modDirectory, bool inherited)
    {
        if (string.Equals(modDirectory, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase)) return;
        Request(ShaderUpgradePlan.Signal.ModSetting);
    }

    // ── the frame ───────────────────────────────────────────────────────────

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (stopped) return;
        bool enabled = Enabled;
        switch (ShaderUpgradePlan.Transition(wasEnabled, enabled))
        {
            case ShaderUpgradePlan.TransitionKind.Evaluate:
                wasEnabled = true;
                Request(ShaderUpgradePlan.Signal.Enabled);
                break;
            case ShaderUpgradePlan.TransitionKind.Stop:
                wasEnabled = false;   // stops: nothing already written is touched
                return;
        }
        if (!enabled) return;

        long due = Interlocked.Read(ref walkDueAt);
        if (due == 0 || Environment.TickCount64 < due) return;
        if (Volatile.Read(ref busy) != 0 || penumbra.MovePending) return;
        var a = autoRefit.Activity();
        if (!MayWalk(a.Running, a.Queued, a.WalkDue, a.Undoing, shaderUpgrade.Current.Busy))
        {
            // All three write into worn mods on equip: let the others finish, then look again.
            lock (scheduleSync) Interlocked.Exchange(ref walkDueAt, 0);
            Request(ShaderUpgradePlan.Signal.AutoRefitBusy);
            return;
        }
        lock (scheduleSync) Interlocked.CompareExchange(ref walkDueAt, 0, due);
        Start(RunKind.Repoint);
    }

    /// <summary>Whether a run could start now: nothing of this feature, the shader upgrade or auto refit is working.</summary>
    internal bool OthersBusy()
    {
        var a = autoRefit.Activity();
        return !MayWalk(a.Running, a.Queued, a.WalkDue, a.Undoing, shaderUpgrade.Current.Busy);
    }

    /// <summary>"Repoint what I'm wearing now": lifts the declines on the worn pieces and repoints them.</summary>
    public bool RepointWornNow() => Start(RunKind.RepointNow);

    /// <summary>"Undo for what I'm wearing": takes the worn pieces' entries out and (by default) declines them.</summary>
    public bool UndoWorn(bool decline = true) => Start(RunKind.UndoWorn, decline);

    private bool Start(RunKind kind, bool decline = true)
    {
        if (stopped || Volatile.Read(ref busy) != 0) return false;
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
        Task.Run(() =>
        {
            try { Run(kind, decline); }
            catch (Exception ex) { log.Error(ex, "[Proteus] gear variants: a {0} run failed", kind); }
            finally { Interlocked.Exchange(ref busy, 0); }
        });
        return true;
    }

    private T OnFramework<T>(Func<T> work, string step)
        => Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var clock = Stopwatch.StartNew();
            try { return work(); }
            finally
            {
                if (clock.ElapsedMilliseconds > SlowFrameMs)
                    log.Warning("[Proteus] gear variants: framework-thread step {0} took {1} ms", step, clock.ElapsedMilliseconds);
            }
        }).GetAwaiter().GetResult();

    // ── a run ───────────────────────────────────────────────────────────────

    private sealed record WornState(List<WornItem> Items, string ModsRoot, Dictionary<string, string> ModNames);

    private static readonly Regex ModelRe = new(
        @"^chara/(?:equipment|accessory)/(?<tag>[ea]\d{4})/model/c\d{4}\k<tag>_(?<suffix>[a-z]{3})\.mdl$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MaterialRe = new(
        @"^chara/(?:equipment|accessory)/(?<tag>[ea]\d{4})/material/v(?<v>\d{4})/mt_c\d{4}\k<tag>_(?<suffix>[a-z]{3})_",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The worn pieces, read on the framework thread; null when there is nothing to read yet.</summary>
    private WornState? ReadWorn()
    {
        var modsRoot = penumbra.GetModDirectory();
        if (ShaderUpgradePlan.WaitingFor(penumbra.IsAvailable, modsRoot) != ShaderUpgradePlan.Waiting.None)
        {
            current = current with { Waiting = "penumbra" };
            Request(ShaderUpgradePlan.Signal.BlankRetry);
            return null;
        }
        var map = penumbra.GetActivePlayerResourceMap();
        var models = map?.Values.SelectMany(g => g).Where(g => g.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (map == null || models == null || !DrawnModelPaths.HasBodySlotModel(models))
        {
            if (++blanks < BlanksBeforeNone) Request(ShaderUpgradePlan.Signal.BlankRetry);
            return null;
        }
        blanks = 0;
        var slots = EquippedSlotVariants.Read(Plugin.ObjectTable.LocalPlayer?.Address ?? 0);
        var names = penumbra.GetAllModsQuiet() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new WornState(WornItems(slots, map), modsRoot!, names);
    }

    /// <summary>The worn gear pieces: each slot's item and variant, with the model it is drawn with and the folder loaded.</summary>
    internal static List<WornItem> WornItems(IEnumerable<EquippedSlotVariants.Slot> slots,
                                             Dictionary<string, HashSet<string>> resourceMap)
    {
        var modelOf = new Dictionary<(string Tag, string Suffix), (string GamePath, string Resolved)>();
        var folderOf = new Dictionary<(string Tag, string Suffix), int>();
        foreach (var (resolved, gamePaths) in resourceMap)
            foreach (var g in gamePaths)
            {
                var p = g.Replace('\\', '/');
                if (ModelRe.Match(p) is { Success: true } m)
                    modelOf[(m.Groups["tag"].Value.ToLowerInvariant(), m.Groups["suffix"].Value.ToLowerInvariant())] = (p, resolved);
                else if (MaterialRe.Match(p) is { Success: true } mt && int.TryParse(mt.Groups["v"].Value, out var v))
                    folderOf[(mt.Groups["tag"].Value.ToLowerInvariant(), mt.Groups["suffix"].Value.ToLowerInvariant())] = v;
            }

        var items = new List<WornItem>();
        foreach (var s in slots)
        {
            if (ItemFor(s.Suffix, s.SetId) is not { } item) continue;
            var key = (item.SetTag, item.Suffix);
            modelOf.TryGetValue(key, out var model);
            items.Add(new WornItem(item, s.Variant, model.GamePath, model.Resolved, s.Glasses,
                                   folderOf.TryGetValue(key, out var f) ? f : null));
        }
        return items;
    }

    private Inputs InputsFor(string modsRoot) => new()
    {
        ModsRoot = modsRoot,
        ManagedRoot = compositor.ManagedModFolder,
        ReadGameFile = readGameFile,
    };

    /// <summary>The root of the mod a worn piece's model comes from, or null.</summary>
    private static string? ModRootOfWorn(WornItem w, string modsRoot)
        => w.ModelResolved != null
           && HatCompatService.InMods(ShaderUpgradePlan.StripPenumbraPrefix(w.ModelResolved), modsRoot, out var root, out _)
            ? root : null;

    /// <summary>The roots of the mods the worn pieces' models are drawn from.</summary>
    private static List<string> WornModRoots(WornState worn, Inputs inputs)
        => worn.Items.Where(w => !w.Glasses && w.ModelGamePath != null
                                 && ShaderUpgradePlan.Classify(w.ModelGamePath, w.ModelResolved, worn.ModsRoot,
                                                               inputs.ManagedRoot, inputs.IsProteusPack)
                                    == ShaderUpgradePlan.Source.Mod)
                     .Select(w => HatCompatService.InMods(ShaderUpgradePlan.StripPenumbraPrefix(w.ModelResolved!), worn.ModsRoot,
                                                          out var root, out _) ? root : null)
                     .Where(r => r != null).Select(r => r!)
                     .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void Run(RunKind kind, bool decline)
    {
        if (stopped) return;
        var worn = OnFramework(ReadWorn, "read");
        // A button pressed mid-redraw (or before Penumbra is up) waits for the character rather than doing nothing.
        for (int tries = 0; worn == null && kind != RunKind.Repoint && tries < BlanksBeforeNone && !stopped; tries++)
        {
            Thread.Sleep(ShaderUpgradePlan.SignalFor(ShaderUpgradePlan.Signal.BlankRetry));
            worn = OnFramework(ReadWorn, "read");
        }
        if (worn == null || stopped)
        {
            if (kind != RunKind.Repoint)
                current = current with { LastRun = new RunInfo(KindName(kind), 0, 0, 0, 0, 0, false, false, DateTime.UtcNow,
                                                               Strings.Settings.GearVariantsWaiting) };
            return;
        }

        var inputs = InputsFor(worn.ModsRoot);
        var roots = WornModRoots(worn, inputs);
        // Mods whose manifest the reconcile changed (entries for a folder no longer shipped taken out): reloaded with the
        // rest, or Penumbra keeps the old entries in memory — and could save them back.
        var reconciled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int forgotten = 0;   // replaced entries of the WORN pieces only, counted as kept by an undo
        foreach (var root in roots)
        {
            if (stopped) return;
            try
            {
                var rc = VariantRepointWriter.Reconcile(root);
                if (rc.ManifestChanged) reconciled.Add(root);
                forgotten += worn.Items.Where(w => ModRootOfWorn(w, worn.ModsRoot) is { } r
                                                    && string.Equals(r, root, StringComparison.OrdinalIgnoreCase))
                                       .Select(w => w.Item).Distinct().Sum(rc.ForgottenOf);
                if (rc.ManifestChanged || rc.Replanned || rc.Dropped > 0 || rc.Forgotten > 0)
                    log.Information("[Proteus] gear variants: {0} — {1} entr(y/ies) forgotten as replaced, {2} removed by "
                                  + "someone (declined){3}{4}", Path.GetFileName(root), rc.Forgotten, rc.Dropped,
                                    rc.Replanned ? ", the author's update is planned afresh" : "",
                                    rc.ManifestChanged ? ", entries for a folder it no longer ships taken out" : "");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or System.Text.Json.JsonException or PenumbraModMeta.LegacyFolderException)
            {
                // The manifest may already be changed (the record write after it failed): reload it anyway, harmless if not.
                reconciled.Add(root);
                log.Warning("[Proteus] gear variants: could not reconcile {0} — {1}", Path.GetFileName(root), ex.Message);
            }
        }

        if (kind == RunKind.UndoWorn) { RunUndo(worn, inputs, decline, reconciled, forgotten); return; }
        if (kind == RunKind.RepointNow)
            foreach (var root in roots)
                VariantRepointWriter.ClearDeclines(root, worn.Items.Select(w => w.Item));

        var (candidates, left) = Candidates(worn.Items, inputs);
        Dictionary<PenumbraMetaImc.Key, ImcEntry>? effective = null;
        IReadOnlyDictionary<string, string?> resolved = new Dictionary<string, string?>();
        if (candidates.Count > 0)
        {
            effective = PenumbraMetaImc.Parse(OnFramework(penumbra.GetPlayerMetaManipulations, "meta"));
            if (effective != null)
            {
                var requests = ResolveRequests(candidates, effective);
                if (requests.Count > 0)
                {
                    var r = OnFramework(() => penumbra.ResolvePlayerPaths(requests), "resolve");
                    if (r == null) effective = null;   // could not tell what the variants load: write nothing
                    else resolved = r;
                }
            }
        }
        HashSet<string> pending;
        lock (refreshPending) pending = new HashSet<string>(refreshPending, StringComparer.OrdinalIgnoreCase);
        var plan = Build(candidates, effective, resolved, pending, inputs, left);

        var changed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in plan.PerMod)
        {
            if (stopped || (kind == RunKind.Repoint && !Enabled)) break;
            var outcome = VariantRepointWriter.Apply(mod.ModRoot, mod.Items,
                () => !stopped && (kind != RunKind.Repoint || Enabled));
            foreach (var (item, reason) in outcome.Left)
                left.Add(new Left(Path.GetFileName(mod.ModRoot), item.Label, reason));
            foreach (var (item, folders, variants, copies) in outcome.Written)
                log.Information("[Proteus] gear variants: copied {0} material path(s) of {1} in {2} from v{3:D4} to {4} "
                              + "(variants {5})", copies, item.Label, Path.GetFileName(mod.ModRoot),
                                mod.Items.First(i => i.Item == item).Folder,
                                string.Join(", ", folders.Select(f => $"v{f:D4}")), string.Join(", ", variants));
            if (outcome.ChangedAny) changed[mod.ModRoot] = outcome.Written.Sum(x => x.Folders.Count);
        }
        if (stopped) return;

        var reload = changed.Keys.Union(reconciled, StringComparer.OrdinalIgnoreCase).ToList();
        var (reloaded, redrew) = OnFramework(() => ReloadChanged(reload, kind, worn.ModNames, changed), "reload");
        if (changed.Count > 0 && !redrew) awaitingRedraw = true;
        Publish(worn, inputs, effective, left, new RunInfo(KindName(kind), changed.Count, changed.Values.Sum(), 0, 0, 0,
                                                           reloaded, redrew, DateTime.UtcNow));
    }

    /// <param name="forgotten">Entries the reconcile just forgot as replaced by someone: kept, and counted so.</param>
    private void RunUndo(WornState worn, Inputs inputs, bool decline, ISet<string> reconciled, int forgotten)
    {
        int removed = 0, kept = forgotten, refused = 0;
        bool anyRecorded = forgotten > 0;
        var messages = new List<string>();
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in worn.Items)
        {
            if (stopped) return;
            if (w.Glasses || w.ModelResolved == null
                || !HatCompatService.InMods(ShaderUpgradePlan.StripPenumbraPrefix(w.ModelResolved), worn.ModsRoot,
                                            out var root, out _))
                continue;
            switch (VariantRepointWriter.UndoItem(root, w.Item, decline, out var r, out var k, out var why))
            {
                case VariantRepointWriter.Undone.Removed:
                    anyRecorded = true;
                    removed += r;
                    kept += k;
                    if (r > 0) changed.Add(root);
                    break;
                case VariantRepointWriter.Undone.Failed:
                    anyRecorded = true;
                    refused++;
                    messages.Add(why);
                    log.Warning("[Proteus] gear variants: undo of {0} in {1} failed — {2}", w.Item.Label,
                                Path.GetFileName(root), why);
                    break;
            }
        }
        if (stopped) return;
        log.Information("[Proteus] gear variants: undo for what is worn — {0} removed, {1} kept as changed since, {2} refused",
                        removed, kept, refused);
        var reload = changed.Union(reconciled, StringComparer.OrdinalIgnoreCase).ToList();
        var (reloaded, redrew) = OnFramework(() => ReloadChanged(reload, RunKind.UndoWorn, worn.ModNames, null), "reload");
        Publish(worn, inputs, null, Candidates(worn.Items, inputs).Left,
                new RunInfo(KindName(RunKind.UndoWorn), changed.Count, 0, removed, kept, refused, reloaded, redrew,
                            DateTime.UtcNow, string.Join(" ", messages.Distinct()), NothingRecorded: !anyRecorded));
    }

    /// <summary>
    /// Reload each changed mod (marked as our own edit), say so in chat once per mod per session, and redraw once when
    /// allowed. Framework thread.
    /// </summary>
    private (bool Reloaded, bool Redrew) ReloadChanged(List<string> modRoots, RunKind kind, Dictionary<string, string> names,
                                                       Dictionary<string, int>? counts)
    {
        if (stopped || modRoots.Count == 0) return (false, false);
        bool reloaded = true;
        foreach (var root in modRoots)
        {
            var dir = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            compositor.ExpectOwnModEdit(dir);
            var ec = penumbra.ReloadModDirectory(dir);
            reloaded &= ec == PenumbraApiEc.Success;
            if (counts != null)
            {
                lock (refreshPending) refreshPending.Add(ShaderUpgradePlan.NormalPath(root));
                if (counts.TryGetValue(root, out var n) && ShaderUpgradePlan.ShouldAnnounce(announced, dir))
                    Announce(n, names.TryGetValue(dir, out var name) ? name : dir);
            }
        }
        bool redraw = RedrawAfter(kind, config.AutoRedraw, true);
        if (redraw) compositor.RedrawForChangedModel("gear variants");
        return (reloaded, redraw);
    }

    private void Announce(int count, string modName)
    {
        try
        {
            var text = string.Format(Strings.Settings.GearVariantsChatFmt, count, modName);
            Plugin.ChatGui.Print(new Dalamud.Game.Text.SeStringHandling.SeStringBuilder().AddUiForeground(text, 45).Build());
        }
        catch (Exception ex) { log.Warning(ex, "[Proteus] gear variants: could not announce in chat"); }
    }

    /// <summary>What is worn now: each piece's mod and folders, the pieces repointed (recorded), and what was left.</summary>
    private void Publish(WornState worn, Inputs inputs, IReadOnlyDictionary<PenumbraMetaImc.Key, ImcEntry>? effective,
                         List<Left> left, RunInfo run)
    {
        var wornList = new List<WornEntry>();
        var repointed = new List<RepointedEntry>();
        foreach (var w in worn.Items)
        {
            if (w.Glasses) continue;
            string? modDir = null, root = null;
            if (w.ModelResolved != null
                && ShaderUpgradePlan.Classify(w.ModelGamePath ?? "", w.ModelResolved, worn.ModsRoot, inputs.ManagedRoot,
                                              inputs.IsProteusPack) == ShaderUpgradePlan.Source.Mod
                && HatCompatService.InMods(ShaderUpgradePlan.StripPenumbraPrefix(w.ModelResolved), worn.ModsRoot,
                                           out var r, out _))
            {
                root = r;
                modDir = Path.GetFileName(r);
            }
            int? materialId = null;
            var vanilla = ImcEntrySource.GameEntries(readGameFile, w.Item.SetTag, w.Item.SlotLabel);
            int FolderOf(int v)
                => effective != null
                   && effective.TryGetValue(new PenumbraMetaImc.Key(w.Item.Kind, w.Item.SetId, w.Item.SlotLabel, v), out var e)
                    ? e.MaterialId : vanilla![v].MaterialId;
            if (vanilla != null) materialId = FolderOf(w.Variant >= 1 && w.Variant < vanilla.Length ? w.Variant : 0);
            wornList.Add(new WornEntry(modDir, w.Item.Label, w.Variant, materialId,
                                       w.LoadedFolder is { } f ? $"v{f:D4}" : null));
            if (root != null && VariantRepointWriter.ReadRecord(root)?.Find(w.Item) is { } rec
                && (rec.Redirects.Count > 0 || rec.Variants.Count > 0))
            {
                var folders = rec.Redirects.Select(x => x.Folder).Distinct().Order().ToList();
                var fixedVariants = vanilla == null ? []
                    : Enumerable.Range(0, vanilla.Length).Where(v => folders.Contains(FolderOf(v))).ToList();
                repointed.Add(new RepointedEntry(modDir!, w.Item.Label, rec.MaterialId, folders, fixedVariants,
                                                 rec.Variants.Select(v => v.Variant).Order().ToList()));
            }
        }
        current = new View(
            Waiting: "none", AwaitingRedraw: awaitingRedraw, Worn: wornList, Repointed: repointed,
            Left: left.Select(l => new LeftEntry(l.ModDir, l.Item, ReasonName(l.Reason))).Distinct().ToList(),
            LastRun: run);
    }

    // ── harness and settings ────────────────────────────────────────────────

    /// <summary>Switch the setting, as the checkbox does.</summary>
    internal void SetEnabled(bool enabled)
    {
        lock (config.Gate) config.AutoRepointVariants = enabled;
        config.Save();
    }
}
