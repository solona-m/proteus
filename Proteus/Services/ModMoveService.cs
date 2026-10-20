using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// Follows a mod renamed in Penumbra (its folder; a display-name change has no event, see
/// <see cref="CompositorService.RefreshDisplayNames"/>). One handler sequences every owner of per-mod state in a fixed
/// order — settings, presets, design bindings, then the compositor, the watchers and the UI — so no composite the move
/// queues runs before the overrides it needs have moved. Each owner moves its own state; the pure work is in
/// <see cref="ModMoveMigration"/>. Framework thread: the bridge raises <see cref="PenumbraBridge.ModMoved"/> a tick
/// after Penumbra reports it.
/// </summary>
public sealed class ModMoveService : IDisposable
{
    /// <summary>The order the owners move in. Overrides before the compositor, whose composite reads them.</summary>
    internal static readonly string[] Order = ["config", "presets", "bindings", "compositor", "autoRefit", "hatCompat", "hairPort", "ui"];

    private readonly PenumbraBridge penumbra;
    private readonly Configuration config;
    private readonly PresetService presets;
    private readonly DesignBindingService designBindings;
    private readonly CompositorService compositor;
    private readonly HatCompatWatcher hatCompat;
    private readonly HairPortWatcher hairPort;
    private readonly AutoRefitWatcher autoRefit;
    private readonly IPluginLog log;
    private volatile bool disposed;

    /// <summary>Raised last, on the framework thread, for windows holding a selection by folder.</summary>
    public event Action<string, string>? Moved;

    public ModMoveService(PenumbraBridge penumbra, Configuration config, PresetService presets,
                          DesignBindingService designBindings, CompositorService compositor, HatCompatWatcher hatCompat,
                          HairPortWatcher hairPort, AutoRefitWatcher autoRefit, IPluginLog log)
    {
        this.penumbra       = penumbra;
        this.config         = config;
        this.presets        = presets;
        this.designBindings = designBindings;
        this.compositor     = compositor;
        this.hatCompat      = hatCompat;
        this.hairPort       = hairPort;
        this.autoRefit      = autoRefit;
        this.log            = log;

        penumbra.ModMoved += OnModMoved;
    }

    public void Dispose()
    {
        disposed = true;
        penumbra.ModMoved -= OnModMoved;
    }

    /// <summary>
    /// The steps of <paramref name="order"/>, each from <paramref name="map"/>. Throws when a step is missing or one is
    /// left over, so the order cannot silently drift from what is wired.
    /// </summary>
    internal static IReadOnlyList<(string Name, Action Run)> BuildSteps(IReadOnlyList<string> order,
                                                                        IReadOnlyDictionary<string, Action> map)
    {
        var missing = order.Where(n => !map.ContainsKey(n)).ToList();
        var extra = map.Keys.Where(k => !order.Contains(k)).ToList();
        if (missing.Count > 0 || extra.Count > 0)
            throw new ArgumentException($"mod move steps do not match the order: missing [{string.Join(", ", missing)}], "
                                      + $"extra [{string.Join(", ", extra)}]");
        return order.Select(n => (n, map[n])).ToList();
    }

    /// <summary>Run the steps in order. A step that throws is reported and the rest still run: one owner's failure must
    /// not strand the others on the old folder.</summary>
    internal static void RunSteps(IReadOnlyList<(string Name, Action Run)> steps, Action<string, Exception> onError)
    {
        foreach (var (name, run) in steps)
        {
            try { run(); }
            catch (Exception ex) { onError(name, ex); }
        }
    }

    /// <summary>
    /// Whether the move goes in the compositor's ledger: not when the old folder is installed again already (a mod
    /// added under that name before the move was handled), which the ledger would otherwise remap away for minutes.
    /// </summary>
    internal static bool RecordLedger(string oldDir, IReadOnlyDictionary<string, string>? installed)
        => installed == null || !installed.Keys.Any(k => ModMoveMigration.Same(k, oldDir));

    /// <summary>Penumbra answers that leave a mod switched off: done now, or it already was.</summary>
    internal static bool SwitchedOff(Penumbra.Api.Enums.PenumbraApiEc ec)
        => ec is Penumbra.Api.Enums.PenumbraApiEc.Success or Penumbra.Api.Enums.PenumbraApiEc.NothingChanged;

    /// <summary>What a rename of Proteus's own output mod calls for.</summary>
    internal enum ManagedAction { WarnOnly, RebuildThenSwitchOff }

    internal static ManagedAction PlanManaged(bool pluginEnabled, bool penumbraAvailable)
        => pluginEnabled && penumbraAvailable ? ManagedAction.RebuildThenSwitchOff : ManagedAction.WarnOnly;

    /// <summary>
    /// Whether the renamed copy of the output mod may be switched off: only once a composite started after the rename
    /// has published, and the new output mod is switched on — otherwise the copy is all that draws the look.
    /// </summary>
    internal static bool SwitchOffManagedCopy(CompositorResult? result, long startEpoch, bool managedEnabled)
        => result is { Success: true } && result.Epoch > startEpoch && managedEnabled;

    /// <summary>The collections a renamed copy of the output mod is switched on in: every one, not only the player's.</summary>
    internal static IReadOnlyList<Guid> CollectionsToSwitchOff(IReadOnlyDictionary<Guid, bool?> enabledByCollection)
        => enabledByCollection.Where(kv => kv.Value == true).Select(kv => kv.Key).ToList();

    private void OnModMoved(string oldDir, string newDir)
    {
        if (disposed) return;
        var installed = penumbra.GetAllModsQuiet();
        switch (ModMoveMigration.Classify(oldDir, newDir, installed))
        {
            case ModMoveMigration.MoveKind.Ignore:
                return;
            case ModMoveMigration.MoveKind.OntoManaged:
                log.Warning("[Proteus] {0} was renamed to \"{1}\", the name of Proteus's own output mod — nothing of it "
                          + "is carried over, and the next composite writes into that folder", oldDir, newDir);
                return;
            case ModMoveMigration.MoveKind.Gone:
                log.Information("[Proteus] {0} was renamed to {1}, which is gone again — forgotten as deleted", oldDir, newDir);
                compositor.ForgetMoved(oldDir);
                return;
            case ModMoveMigration.MoveKind.ManagedRenamed:
                OnManagedRenamed(newDir);
                return;
        }

        var snapshotEntry = compositor.LastDiscovered.FirstOrDefault(e => ModMoveMigration.Same(e.ModDirectory, oldDir));
        string? newName = installed?.FirstOrDefault(kv => ModMoveMigration.Same(kv.Key, newDir)).Value;
        string modsRoot = compositor.ModsRoot;
        int settings = 0;

        var steps = BuildSteps(Order, new Dictionary<string, Action>
        {
            ["config"] = () =>
            {
                lock (config.Gate) settings = ModMoveMigration.Configuration(config, oldDir, newDir);
                if (settings > 0) config.Save();
            },
            ["presets"]    = () => presets.MoveMod(oldDir, newDir, snapshotEntry?.Metadata.Presets, modsRoot),
            ["bindings"]   = () => designBindings.MoveMod(oldDir, newDir),
            ["compositor"] = () => compositor.OnModMoved(oldDir, newDir, newName, RecordLedger(oldDir, installed)),
            ["autoRefit"]  = () => autoRefit.OnModMoved(oldDir, newDir),
            ["hatCompat"]  = () => hatCompat.OnModMoved(oldDir, newDir, modsRoot),
            ["hairPort"]   = () => hairPort.OnModMoved(oldDir, newDir),
            ["ui"]         = () => Moved?.Invoke(oldDir, newDir),
        });
        RunSteps(steps, (step, ex) => log.Error(ex, "[Proteus] following the rename of {0} to {1}: step {2} failed",
                                                oldDir, newDir, step));

        if (settings > 0 || snapshotEntry != null)
            log.Information("[Proteus] mod renamed in Penumbra: {0} -> {1}; {2} setting(s) carried over", oldDir, newDir, settings);
        else
            log.Debug("[Proteus] mod renamed in Penumbra: {0} -> {1}; nothing of Proteus's to carry over", oldDir, newDir);
    }

    /// <summary>
    /// Proteus's own output mod renamed. The renamed copy keeps drawing the look until a new output mod is built and
    /// switched on; only then is the copy switched off, in every collection it is on in. Until then its folder counts
    /// as Proteus's own output, so the rebuild never reads the previous composite as its base. Nothing is deleted.
    /// </summary>
    private void OnManagedRenamed(string copyDir)
    {
        log.Warning("[Proteus] Proteus's output mod was renamed to \"{0}\" in Penumbra. Proteus rebuilds \"{1}\" and then "
                  + "switches the renamed copy off; delete it in Penumbra when you no longer want it",
                    copyDir, SidecarDiscoveryService.ManagedModDir);

        if (PlanManaged(config.PluginEnabled, penumbra.IsAvailable) == ManagedAction.WarnOnly) return;

        compositor.AddManagedAlias(copyDir);
        long startEpoch = compositor.RecompositeEpoch;
        _ = Task.Run(async () =>
        {
            CompositorResult? result = null;
            string? notRun;
            try { (result, notRun) = await compositor.FullRefreshAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false); }
            catch (Exception ex) { notRun = ex.Message; }

            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (disposed) return;
                var player = penumbra.GetPlayerCollectionId();
                bool managedOn = player is { } p
                              && penumbra.GetModSettings(p, SidecarDiscoveryService.ManagedModDir) is { Enabled: true };
                if (!SwitchOffManagedCopy(result, startEpoch, managedOn))
                {
                    log.Warning("[Proteus] the renamed output mod \"{0}\" stays on: the rebuild of \"{1}\" did not finish "
                              + "switched on ({2})", copyDir, SidecarDiscoveryService.ManagedModDir,
                                notRun ?? result?.ErrorMessage ?? (managedOn ? "no result" : "not switched on"));
                    return;
                }

                // Each collection's own setting, under any temporary one; an inherited one is switched off where it is
                // set, and writing it into the child would give the child a setting of its own.
                var enabled = new Dictionary<Guid, bool?>();
                foreach (var id in penumbra.GetCollections()?.Keys ?? Enumerable.Empty<Guid>())
                    enabled[id] = penumbra.GetCollectionModSettings(id, ignoreTemporary: true) is { } all
                                  && all.TryGetValue(copyDir, out var s) && !s.Inherited
                        ? s.Enabled
                        : null;
                var off = CollectionsToSwitchOff(enabled);
                var failed = off.Where(id => !SwitchedOff(penumbra.SetModEnabled(id, copyDir, false))).ToList();
                if (failed.Count > 0)
                {
                    // Still on somewhere: still ours, or the next composite reads the old composite as its base.
                    log.Warning("[Proteus] the renamed output mod \"{0}\" could not be switched off in {1} of {2} "
                              + "collection(s); switch it off in Penumbra", copyDir, failed.Count, off.Count);
                    return;
                }
                compositor.RemoveManagedAlias(copyDir);
                log.Information("[Proteus] the renamed output mod \"{0}\" is switched off in {1} collection(s)", copyDir, off.Count);
            }).ConfigureAwait(false);
        });
    }
}
