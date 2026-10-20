using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Proteus.Services;

public partial class CompositorService
{
    // ── A mod renamed in Penumbra ────────────────────────────────────────────

    /// <summary>
    /// Recent renames, which every publish of <see cref="LastDiscovered"/> and of the per-run caches is remapped
    /// through: a composite or discovery that started before a rename would otherwise bring the old folder back.
    /// </summary>
    private readonly ModMoveLedger _moveLedger = new();

    private List<OverlayEntry> _lastDiscovered = [];

    /// <summary>Penumbra's mod root as this compositor knows it.</summary>
    internal string ModsRoot => modsRoot;

    /// <summary>The epoch of the newest composite started, for a caller telling its own run from an older one.</summary>
    internal long RecompositeEpoch => Volatile.Read(ref _recompositeEpoch);

    private IReadOnlyDictionary<string, ModMoveLedger.MoveRecord> LiveMoves() => _moveLedger.Live(Environment.TickCount64);

    private List<OverlayEntry> Ledgered(List<OverlayEntry> list) => ModMoveLedger.Remap(list, LiveMoves(), modsRoot);

    private Dictionary<string, T> Ledgered<T>(Dictionary<string, T> map) => ModMoveLedger.RemapKeys(map, LiveMoves());

    private Dictionary<(string ModDir, string? Group, string? Option), V> Ledgered<V>(
        Dictionary<(string ModDir, string? Group, string? Option), V> map)
        => ModMoveLedger.RemapShell(map, LiveMoves());

    private Dictionary<string, string> LedgeredPaths(Dictionary<string, string> map)
        => ModMoveLedger.RemapPathValues(map, LiveMoves(), modsRoot);

    /// <summary>Compare-and-swap <paramref name="field"/>: false when another thread replaced it since
    /// <paramref name="expected"/> was read, and the newer list stands.</summary>
    internal static bool TryPublish(ref List<OverlayEntry> field, List<OverlayEntry> expected, List<OverlayEntry> next)
        => ReferenceEquals(Interlocked.CompareExchange(ref field, next, expected), expected);

    private bool TryPublishDiscovered(List<OverlayEntry> expected, List<OverlayEntry> next)
        => TryPublish(ref _lastDiscovered, expected, Ledgered(next));

    /// <summary>
    /// <paramref name="list"/> with the moved mod's entry under its new folder, sidecar and name — the same
    /// <see cref="OverlayEntry.Metadata"/>, the same order; null when the old folder is not listed.
    /// </summary>
    internal static List<OverlayEntry>? MoveEntry(IReadOnlyList<OverlayEntry> list, string oldDir, string newDir,
                                                  string modsRoot, string? newName)
    {
        int idx = -1;
        for (int i = 0; i < list.Count; i++)
            if (ModMoveMigration.Same(list[i].ModDirectory, oldDir)) { idx = i; break; }
        if (idx < 0) return null;

        var moved = new List<OverlayEntry>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (i == idx)
                moved.Add(e with
                {
                    ModDirectory = newDir,
                    ModName      = newName ?? e.ModName,
                    SidecarRoot  = Path.Combine(modsRoot, newDir, SidecarDiscoveryService.SidecarSubdir),
                });
            else if (!ModMoveMigration.Same(e.ModDirectory, newDir))
                moved.Add(e);
        }
        return moved;
    }

    /// <summary>
    /// <paramref name="list"/> with each mod's display name as Penumbra gives it now; null when no name changed or
    /// <paramref name="names"/> is empty. <paramref name="vanished"/> lists the folders Penumbra no longer has.
    /// </summary>
    internal static List<OverlayEntry>? WithNames(IReadOnlyList<OverlayEntry> list, IReadOnlyDictionary<string, string> names,
                                                  out List<string> vanished)
    {
        vanished = [];
        if (names.Count == 0) return null;

        var lookup = names is Dictionary<string, string> d && ReferenceEquals(d.Comparer, StringComparer.OrdinalIgnoreCase)
            ? d
            : new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase);

        List<OverlayEntry>? next = null;
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (!lookup.TryGetValue(e.ModDirectory, out var name)) { vanished.Add(e.ModDirectory); continue; }
            if (string.Equals(name, e.ModName, StringComparison.Ordinal)) continue;
            next ??= [.. list];
            next[i] = e with { ModName = name };
        }
        return next;
    }

    /// <summary><paramref name="list"/> without the entries for <paramref name="dirs"/>.</summary>
    internal static List<OverlayEntry> WithoutDirs(IReadOnlyList<OverlayEntry> list, IReadOnlyCollection<string> dirs)
        => list.Where(e => !dirs.Any(d => ModMoveMigration.Same(d, e.ModDirectory))).ToList();

    /// <summary>
    /// Whether a rename is worth a composite: the moved mod is one of ours and switched on, or the last composite read
    /// a base from inside its folder. Asked before any cache is remapped.
    /// </summary>
    internal static bool MoveNeedsComposite(OverlayEntry? moved, IReadOnlyDictionary<string, string>? lastBaseUpstreams,
                                            string modsRoot, string oldDir)
        => moved is { Enabled: true }
        || (lastBaseUpstreams?.Values.Any(v => ModMoveMigration.Same(ModFolderOf(v, modsRoot), oldDir)) ?? false);

    /// <summary>Whether a probe throttled to <paramref name="intervalMs"/> may run again.</summary>
    internal static bool ProbeDue(long now, long last, int intervalMs) => unchecked(now - last) >= intervalMs;

    /// <summary>
    /// A mod renamed in Penumbra: its list entry and every per-mod cache follow it, and a composite is queued when the
    /// mod contributed (ambient: dropped with Auto redraw off — nothing here depends on it running). Framework thread,
    /// called by <see cref="ModMoveService"/> after the settings have moved.
    /// </summary>
    public void OnModMoved(string oldDir, string newDir, string? newName)
    {
        var snapshot = LastDiscovered;
        var entry = snapshot.FirstOrDefault(e => ModMoveMigration.Same(e.ModDirectory, oldDir));
        bool needs = MoveNeedsComposite(entry, _lastBaseUpstreams, modsRoot, oldDir);

        _moveLedger.Record(oldDir, newDir, newName ?? entry?.ModName, Environment.TickCount64);
        if (MoveEntry(snapshot, oldDir, newDir, modsRoot, newName) is { } moved && !TryPublishDiscovered(snapshot, moved))
            LastDiscovered = LastDiscovered;   // replaced meanwhile: the setter remaps the newer list through the ledger

        if (_lastBaseUpstreams is { } upstreams)
            _lastBaseUpstreams = LedgeredPaths(new Dictionary<string, string>(upstreams, StringComparer.OrdinalIgnoreCase));
        _appendHostSources = LedgeredPaths(_appendHostSources);
        _inertMods         = Ledgered(new Dictionary<string, InertReason>(_inertMods, StringComparer.OrdinalIgnoreCase));
        _contentMaterials  = Ledgered(_contentMaterials);
        _contentModels     = Ledgered(_contentModels);
        _shellMaterials    = Ledgered(_shellMaterials);
        _skinGlowTargets   = Ledgered(_skinGlowTargets);

        ModMoveMigration.RekeyKey(_bodyModCache, oldDir, newDir);
        ModMoveMigration.RekeyKey(_knownDisabled, oldDir, newDir);
        ModMoveMigration.RekeyKey(_ownModEditUntil, oldDir, newDir);

        textureLoader.EvictMod(oldDir);
        InvalidateUpstreamCache($"ModMoved:{oldDir}->{newDir}");

        if (needs)
        {
            log.Information("[Proteus] {0} was renamed to {1} in Penumbra — recompositing from its new folder", oldDir, newDir);
            TriggerRecomposite($"ModMoved:{oldDir}->{newDir}", force: false);
        }
        else
            log.Debug("[Proteus] {0} was renamed to {1} in Penumbra — it contributed nothing, no recomposite", oldDir, newDir);
    }

    /// <summary>A moved mod whose new folder is gone by the time the move is handled: forgotten as a deletion.</summary>
    public void ForgetMoved(string oldDir) => OnModDeleted(oldDir);

    // Name refresh: Penumbra has no event for a display-name change, so the lists poll while they are on screen.
    private const int NameProbeIntervalMs = 2000;
    private int _nameProbeRunning;
    private long _lastNameProbeTick;

    /// <summary>
    /// Pick up display names changed in Penumbra, for a tab showing the mod list. Safe to call every frame: one quiet
    /// read of Penumbra's mod list every two seconds at most, off the framework thread. A listed folder Penumbra no
    /// longer has (a move it never told us about) brings a full rediscovery.
    /// </summary>
    public void RefreshDisplayNames()
    {
        if (_disposed || !config.PluginEnabled || !penumbra.IsAvailable) return;
        var now = Environment.TickCount64;
        if (!ProbeDue(now, Interlocked.Read(ref _lastNameProbeTick), NameProbeIntervalMs)) return;
        if (Interlocked.Exchange(ref _nameProbeRunning, 1) == 1) return;
        Interlocked.Exchange(ref _lastNameProbeTick, now);

        Task.Run(() =>
        {
            try
            {
                if (_disposed || penumbra.GetAllModsQuiet() is not { Count: > 0 } names) return;
                var snapshot = LastDiscovered;
                var next = WithNames(snapshot, names, out var vanished);
                if (vanished.Count > 0)
                {
                    var found = discovery.DiscoverAll();
                    TryPublishDiscovered(snapshot, found.Count > 0 ? found : WithoutDirs(next ?? snapshot, vanished));
                    log.Debug("[Proteus] mod list: {0} no longer in Penumbra — rediscovered", string.Join(", ", vanished));
                }
                else if (next != null)
                    TryPublishDiscovered(snapshot, next);
            }
            catch (Exception ex) { log.Debug("[Proteus] name refresh failed: {0}", ex.Message); }
            finally { Interlocked.Exchange(ref _nameProbeRunning, 0); }
        });
    }
}
