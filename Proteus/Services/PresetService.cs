using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Proteus.Interop;

namespace Proteus.Services;

// ── Persisted model (presets.json) ──────────────────────────────────────────

public class ModPresetStore
{
    /// <summary>Checked at load: an unknown (future) version is loaded as empty rather than half-understood.</summary>
    public int Version { get; set; } = ModPresetStore.CurrentVersion;

    public const int CurrentVersion = 1;

    /// <summary>Penumbra mod directory → that mod's user-saved presets, in display order.</summary>
    public Dictionary<string, List<ModPreset>> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Penumbra mod directory → the preset currently pinned on it, if any.</summary>
    public Dictionary<string, Guid> Applied { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rebuild both maps case-insensitively: deserialization discards an initializer's comparer.</summary>
    public ModPresetStore Normalized()
    {
        Presets = new Dictionary<string, List<ModPreset>>(Presets, StringComparer.OrdinalIgnoreCase);
        Applied = new Dictionary<string, Guid>(Applied, StringComparer.OrdinalIgnoreCase);

        // A user preset without an id (a hand-edited file) gets one, or every such preset would pin as the same one.
        foreach (var mine in Presets.Values)
            foreach (var p in mine)
                if (p.Id == Guid.Empty) p.Id = Guid.NewGuid();

        return this;
    }
}

/// <summary>What an <see cref="PresetService.Apply"/> could and could not do.</summary>
/// <param name="GroupsApplied">Option groups actually written to Penumbra.</param>
/// <param name="MissingGroups">Groups the preset names that the mod no longer has.</param>
/// <param name="MissingOptions">(group, option) pairs the group no longer offers.</param>
public record PresetApplyReport(
    int GroupsApplied,
    IReadOnlyList<string> MissingGroups,
    IReadOnlyList<(string Group, string Option)> MissingOptions)
{
    public static readonly PresetApplyReport Empty = new(0, [], []);

    public bool FullyApplied => MissingGroups.Count == 0 && MissingOptions.Count == 0;
}

/// <summary>
/// Named looks for a single mod. Option ticks go to Penumbra; colours, layer settings and stack order ride in an
/// <see cref="OverlayOverrideBag"/> on the compositor's preset channel, so <c>metadata.json</c> is never written.
/// Precedence (<see cref="CompositorService.MergeByMod"/>): pinned preset, then design binding, then metadata.
/// </summary>
public class PresetService : IDisposable
{
    private readonly PenumbraBridge penumbra;
    private readonly SidecarDiscoveryService discovery;
    private readonly CompositorService compositor;
    private readonly DesignBindingService bindings;
    private readonly IPluginLog log;

    // Unescaped encoder: preset and option names are often non-ASCII. See ProteusJson.
    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Encoder = ProteusJson.Encoder };

    private readonly string storePath;
    private readonly object gate = new();
    private ModPresetStore store = new();

    /// <summary>The live colour / gear / stack overrides the pinned presets publish.</summary>
    private readonly OverlayOverrideBag overrides;

    public PresetService(
        PenumbraBridge penumbra, SidecarDiscoveryService discovery, CompositorService compositor,
        DesignBindingService bindings, IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.penumbra   = penumbra;
        this.discovery  = discovery;
        this.compositor = compositor;
        this.bindings   = bindings;
        this.log        = log;

        overrides = new OverlayOverrideBag(
            compositor.SetPresetColorOverride, compositor.SetPresetGearOverride, compositor.SetPresetStackOverride);

        storePath = Path.Combine(pluginInterface.ConfigDirectory.FullName, "presets.json");
        Load();

        penumbra.PenumbraReady += OnPenumbraReady;
        // Also now, for a plugin reload: Penumbra is already up, so PenumbraReady will never fire again.
        RepublishApplied(triggerComposite: false);
    }

    public void Dispose() => penumbra.PenumbraReady -= OnPenumbraReady;

    /// <summary>The bag the editor writes through while a preset is pinned; only <see cref="OverlayEditRouter"/> uses it.</summary>
    internal OverlayOverrideBag Overrides => overrides;

    // ── Reading ─────────────────────────────────────────────────────────────────

    /// <summary>Just enough of a preset to draw a chip or combo row, without deep-cloning colour tables every frame.</summary>
    public record PresetInfo(Guid Id, string Name, string? Description, PresetSource Source, DateTime LastEditUtc);

    /// <summary>The per-frame listing (see <see cref="PresetInfo"/>).</summary>
    public List<PresetInfo> ListFor(OverlayEntry entry)
    {
        var result = new List<PresetInfo>();

        foreach (var p in entry.Metadata.Presets ?? [])
            result.Add(new PresetInfo(
                PackId(entry.ModDirectory, p),
                p.Name, p.Description, PresetSource.Pack, p.LastEditUtc));

        lock (gate)
            if (store.Presets.TryGetValue(entry.ModDirectory, out var mine))
                foreach (var p in mine)
                    result.Add(new PresetInfo(p.Id, p.Name, p.Description, PresetSource.User, p.LastEditUtc));

        return result;
    }

    /// <summary>One preset by id, as an independent copy.</summary>
    public ModPreset? Get(OverlayEntry entry, Guid id)
        => PresetsFor(entry).FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Every preset offered for this mod, as copies: the author's first (read fresh from the sidecar), then the
    /// wearer's.
    /// </summary>
    public List<ModPreset> PresetsFor(OverlayEntry entry)
    {
        var result = new List<ModPreset>();

        foreach (var packPreset in entry.Metadata.Presets ?? [])
        {
            var copy = packPreset.Clone();
            copy.Source = PresetSource.Pack;
            copy.Id     = PackId(entry.ModDirectory, copy);
            copy.ModName   ??= entry.ModName;
            copy.ModAuthor ??= entry.Metadata.Author;
            result.Add(copy);
        }

        lock (gate)
            if (store.Presets.TryGetValue(entry.ModDirectory, out var mine))
                result.AddRange(mine.Select(p => p.Clone()));

        return result;
    }

    /// <summary>The preset currently pinned on this mod, or null for "the mod's own look".</summary>
    public Guid? AppliedIdFor(string modDir)
    {
        lock (gate) return store.Applied.TryGetValue(modDir, out var id) ? id : null;
    }

    /// <summary>The pinned preset's display name, or null when nothing is pinned or the pin dangles.</summary>
    public string? AppliedNameFor(OverlayEntry entry)
    {
        if (AppliedIdFor(entry.ModDirectory) is not { } id) return null;
        return PresetsFor(entry).FirstOrDefault(p => p.Id == id)?.Name;
    }

    // Drift costs an IPC call and two serializations, so it is cached per mod for this long (ms).
    private const int DriftCacheMs = 250;
    private readonly Dictionary<string, (Guid Id, long Tick, bool Modified)> driftCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the live look has drifted from the pinned preset (the `●` on the chip): would Update change anything.</summary>
    public bool IsModified(OverlayEntry entry, Guid collId)
    {
        if (AppliedIdFor(entry.ModDirectory) is not { } id) return false;

        var now = Environment.TickCount64;
        lock (gate)
            if (driftCache.TryGetValue(entry.ModDirectory, out var cached)
                && cached.Id == id && now - cached.Tick < DriftCacheMs)
                return cached.Modified;

        var saved = PresetsFor(entry).FirstOrDefault(p => p.Id == id);
        var modified = saved != null && !SameLook(saved, Capture(entry, collId, saved.Name));

        lock (gate) driftCache[entry.ModDirectory] = (id, now, modified);
        return modified;
    }

    /// <summary>Forget the cached drift answer for a mod, so the marker updates on the next frame.</summary>
    private void InvalidateDrift(string modDir)
    {
        lock (gate) driftCache.Remove(modDir);
    }

    /// <summary>
    /// Whether the live look still matches what was saved: "did YOU change something". Not symmetric: options are
    /// compared only over groups the preset names AND the mod still offers; colours, gear and stack order whole.
    /// </summary>
    internal static bool SameLook(ModPreset saved, ModPreset live)
        => SameOptions(saved.Options, live.Options) && AppearanceJson(saved) == AppearanceJson(live);

    private static bool SameOptions(
        Dictionary<string, List<string>> saved, Dictionary<string, List<string>> live)
    {
        foreach (var (group, wanted) in saved)
        {
            if (!live.TryGetValue(group, out var now)) continue;   // the mod dropped this group

            // Order-insensitive: Penumbra returns a multi-select group's ticks in its own order.
            if (wanted.Count != now.Count) return false;
            var a = wanted.OrderBy(v => v, StringComparer.OrdinalIgnoreCase);
            var b = now.OrderBy(v => v, StringComparer.OrdinalIgnoreCase);
            if (!a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static string AppearanceJson(ModPreset p)
        => JsonSerializer.Serialize(new { p.Colors, p.Gear, p.StackOrder }, JsonOpts);

    // ── Capturing ───────────────────────────────────────────────────────────────

    /// <summary>Snapshot this mod's current look into a new preset, via <see cref="DesignBindingService.CaptureMod"/>.</summary>
    public ModPreset Capture(OverlayEntry entry, Guid collId, string name)
    {
        var snapshot = bindings.CaptureMod(entry, collId);
        return new ModPreset
        {
            Name       = name,
            Source     = PresetSource.User,
            ModName    = entry.ModName,
            ModAuthor  = entry.Metadata.Author,
            Options    = snapshot.Options,
            Colors     = snapshot.Colors,
            Gear       = snapshot.Gear,
            StackOrder = snapshot.StackOrder,
        };
    }

    /// <summary>Save a new preset for this mod and pin it. Returns the STORED preset, whose id <see cref="Add"/> minted.</summary>
    public ModPreset Save(OverlayEntry entry, Guid collId, string name)
    {
        var stored = Add(entry.ModDirectory, Capture(entry, collId, name));
        Apply(entry, collId, stored);
        return stored;
    }

    /// <summary>Fold the current look back into an existing user preset; a pack preset is forked first.</summary>
    public bool Update(OverlayEntry entry, Guid collId, Guid presetId)
    {
        var live = Capture(entry, collId, string.Empty);

        lock (gate)
        {
            if (!store.Presets.TryGetValue(entry.ModDirectory, out var mine)) return false;
            var existing = mine.FirstOrDefault(p => p.Id == presetId);
            if (existing == null) return false;

            existing.Options     = live.Options;
            existing.Colors      = live.Colors;
            existing.Gear        = live.Gear;
            existing.StackOrder  = live.StackOrder;
            existing.LastEditUtc = DateTime.UtcNow;
            SaveDeferred();
        }

        InvalidateDrift(entry.ModDirectory);
        log.Information("[Proteus] preset: updated {0} on {1} from the current look", presetId, entry.ModDirectory);
        return true;
    }

    /// <summary>Store a clone of a preset against a mod without applying it.</summary>
    public ModPreset Add(string modDir, ModPreset preset)
    {
        var stored = preset.Clone();
        // Anything entering this store is the wearer's own, whatever it claimed to be.
        stored.Source = PresetSource.User;
        stored.Id     = Guid.NewGuid();

        lock (gate)
        {
            if (!store.Presets.TryGetValue(modDir, out var mine)) store.Presets[modDir] = mine = [];
            stored.Name = UniqueName(mine, stored.Name);
            mine.Add(stored);
            SaveDeferred();
        }

        return stored;
    }

    /// <summary>Rename a user preset. Pack presets are read-only and return false.</summary>
    public bool Rename(string modDir, Guid presetId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;

        lock (gate)
        {
            if (!store.Presets.TryGetValue(modDir, out var mine)) return false;
            var p = mine.FirstOrDefault(x => x.Id == presetId);
            if (p == null || p.Name == newName) return false;

            p.Name        = UniqueName(mine.Where(x => x.Id != presetId), newName);
            p.LastEditUtc = DateTime.UtcNow;
            SaveDeferred();
            return true;
        }
    }

    /// <summary>Delete a user preset, unpinning it first when it is the applied one.</summary>
    public bool Delete(OverlayEntry entry, Guid presetId)
    {
        bool removed;
        lock (gate)
        {
            removed = store.Presets.TryGetValue(entry.ModDirectory, out var mine)
                   && mine.RemoveAll(x => x.Id == presetId) > 0;
            if (removed)
            {
                if (store.Presets[entry.ModDirectory].Count == 0) store.Presets.Remove(entry.ModDirectory);
                SaveDeferred();
            }
        }

        if (removed && AppliedIdFor(entry.ModDirectory) == presetId) ClearApplied(entry.ModDirectory);
        return removed;
    }

    /// <summary>"Sheer" beside an existing "Sheer" becomes "Sheer (2)".</summary>
    internal static string UniqueName(IEnumerable<ModPreset> existing, string wanted)
        => UniqueName(existing.Select(p => p.Name), wanted);

    internal static string UniqueName(IEnumerable<string> existingNames, string wanted)
    {
        var taken = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(wanted)) return wanted;
        for (var n = 2; ; n++)
        {
            var candidate = $"{wanted} ({n})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>
    /// A pack preset's identity: the author's id, else derived from the mod and name. Both listings resolve ids
    /// through here, so a pin and the picker always agree.
    /// </summary>
    internal static Guid PackId(string modDir, ModPreset preset)
        => preset.Id == Guid.Empty ? StableId(modDir, preset.Name) : preset.Id;

    /// <summary>A deterministic id for a pack preset the author gave none, so the pin survives a restart.</summary>
    private static Guid StableId(string modDir, string name)
        => StableIds.GetOrAdd((modDir, name), static key => Derive(key.ModDir, key.Name));

    /// <summary>Memo for <see cref="StableId"/>, which <see cref="ListFor"/> hits every frame; never invalidated (pure function).</summary>
    private static readonly ConcurrentDictionary<(string ModDir, string Name), Guid> StableIds = new();

    /// <summary>The hash itself, for the memo's non-capturing lambda.</summary>
    private static Guid Derive(string modDir, string name)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes($"proteus-pack-preset {modDir.ToLowerInvariant()} {name}"));
        return new Guid(bytes);
    }

    // ── Applying ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Put a preset on: option ticks to Penumbra, colours, gear and stack order as a live override. Anything the mod
    /// no longer has is reported rather than forced.
    /// </summary>
    public PresetApplyReport Apply(OverlayEntry entry, Guid collId, ModPreset preset)
    {
        // Checked against what the mod's manifest offers, not what the collection has selected.
        var plan = PlanOptionWrites(preset.Options, ReadCatalogue(entry));

        foreach (var (group, selection) in plan.Writes)
            penumbra.SetModOption(collId, entry.ModDirectory, group, selection);

        var missingGroups  = plan.MissingGroups;
        var missingOptions = plan.MissingOptions;
        var applied        = plan.Writes.Count;

        // A clone, so editing the live look never writes into the saved preset.
        var live = preset.Clone();
        overrides.SetMod(entry.ModDirectory, live.Colors, live.Gear,
            live.StackOrder.Count > 0 ? live.StackOrder : null);

        lock (gate)
        {
            store.Applied[entry.ModDirectory] = preset.Id;
            SaveDeferred();
        }

        InvalidateDrift(entry.ModDirectory);
        compositor.TriggerRecomposite($"preset-apply:{entry.ModDirectory}");

        var report = new PresetApplyReport(applied, missingGroups, missingOptions);
        if (!report.FullyApplied)
            log.Information("[Proteus] preset '{0}' on {1}: {2} group(s) applied, {3} missing group(s), {4} missing option(s)",
                preset.Name, entry.ModDirectory, applied, missingGroups.Count, missingOptions.Count);
        return report;
    }

    /// <summary>What <see cref="Apply"/> will write, and what it had to leave out.</summary>
    internal record OptionPlan(
        List<(string Group, List<string> Selection)> Writes,
        List<string> MissingGroups,
        List<(string Group, string Option)> MissingOptions);

    /// <summary>Work out which of a preset's option selections the mod can still honour.</summary>
    /// <param name="catalogue">
    /// Group → the options it offers, or NULL when the manifest could not be read, in which case the preset is
    /// written verbatim.
    /// </param>
    internal static OptionPlan PlanOptionWrites(
        Dictionary<string, List<string>> wantedByGroup, Dictionary<string, HashSet<string>>? catalogue)
    {
        var writes         = new List<(string, List<string>)>();
        var missingGroups  = new List<string>();
        var missingOptions = new List<(string, string)>();

        foreach (var (group, wanted) in wantedByGroup)
        {
            if (catalogue == null)
            {
                writes.Add((group, wanted));
                continue;
            }

            if (!catalogue.TryGetValue(group, out var available))
            {
                missingGroups.Add(group);
                continue;
            }

            var keep = new List<string>();
            foreach (var option in wanted)
            {
                if (available.Contains(option)) keep.Add(option);
                else missingOptions.Add((group, option));
            }

            // Every option in this group is gone: writing the empty list would CLEAR the group, which the preset
            // never asked for.
            if (keep.Count == 0 && wanted.Count > 0) continue;

            writes.Add((group, keep));
        }

        return new OptionPlan(writes, missingGroups, missingOptions);
    }

    /// <summary>Unpin whatever is on this mod. Colours fall back to the metadata; option ticks are left as they are.</summary>
    public bool ClearApplied(string modDir)
    {
        bool had;
        lock (gate)
        {
            had = store.Applied.Remove(modDir);
            if (had) SaveDeferred();
        }

        var dropped = overrides.RemoveMod(modDir);
        InvalidateDrift(modDir);
        if (had || dropped) compositor.TriggerRecomposite($"preset-clear:{modDir}");
        return had || dropped;
    }

    /// <summary>
    /// Drop every pin without touching saved presets, on <see cref="DesignBindingService.PresetsSuperseded"/>. Does not
    /// recomposite: the design restore that raised this will.
    /// </summary>
    public void ClearAllApplied()
    {
        bool had;
        lock (gate)
        {
            had = store.Applied.Count > 0;
            if (had) { store.Applied.Clear(); SaveDeferred(); }
            driftCache.Clear();
        }

        var dropped = overrides.Clear();
        if (had || dropped) log.Debug("[Proteus] preset: pins cleared, superseded by a design");
    }

    private bool republished;

    private void OnPenumbraReady() => RepublishApplied(triggerComposite: true);

    /// <summary>
    /// Republish the pinned presets' overrides after a reload. Writes nothing to Penumbra (the ticks persisted), and
    /// never prunes an unresolved pin: <see cref="SidecarDiscoveryService.DiscoverAll"/> is empty until Penumbra answers.
    /// </summary>
    private void RepublishApplied(bool triggerComposite)
    {
        if (republished) return;

        List<(string Mod, Guid Id)> pins;
        lock (gate) pins = store.Applied.Select(kv => (kv.Key, kv.Value)).ToList();
        if (pins.Count == 0) { republished = true; return; }

        var discovered = discovery.DiscoverAll();
        if (discovered.Count == 0) return;   // Penumbra isn't answering yet; try again when it is

        var byMod  = discovered.ToDictionary(e => e.ModDirectory, StringComparer.OrdinalIgnoreCase);
        var done   = 0;
        foreach (var (modDir, id) in pins)
        {
            if (!byMod.TryGetValue(modDir, out var entry)) continue;
            if (PresetsFor(entry).FirstOrDefault(p => p.Id == id) is not { } preset) continue;

            var live = preset.Clone();
            overrides.SetMod(modDir, live.Colors, live.Gear,
                live.StackOrder.Count > 0 ? live.StackOrder : null);
            done++;
        }

        republished = true;
        log.Debug("[Proteus] preset: republished {0} of {1} pin(s)", done, pins.Count);

        // On cold boot the compositor's PenumbraReady handler runs first, so its composite may predate the overrides.
        if (done > 0 && triggerComposite) compositor.TriggerRecomposite("preset-republish");
    }

    /// <summary>Group name → the options it offers, from the mod's Penumbra manifest; null when it cannot be read.</summary>
    private Dictionary<string, HashSet<string>>? ReadCatalogue(OverlayEntry entry)
    {
        if (entry.ModRoot is not { } modRoot) return null;

        try
        {
            var groups = PenumbraModMeta.TryReadGroups(modRoot);
            if (groups == null) return null;

            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, group) in groups)
                result[name] = PenumbraModMeta.ReadOptionNames(group).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return result;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[Proteus] preset: could not read the option catalogue for {0}", entry.ModDirectory);
            return null;
        }
    }

    // ── Persistence ─────────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(storePath)) return;
            var loaded = JsonSerializer.Deserialize<ModPresetStore>(File.ReadAllText(storePath), JsonOpts);
            if (loaded == null) return;

            if (loaded.Version > ModPresetStore.CurrentVersion)
            {
                log.Warning("[Proteus] presets.json is version {0}, newer than this build understands ({1}); " +
                            "starting empty so a downgrade cannot silently rewrite it.",
                    loaded.Version, ModPresetStore.CurrentVersion);
                return;
            }

            store = loaded.Normalized();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Failed to load presets; starting empty.");
            store = new();
        }
    }

    private readonly object writeGate = new();
    private string? pendingJson;

    /// <summary>Serialize now (the caller holds <c>gate</c>) but write off the calling thread; the newest snapshot wins.</summary>
    private void SaveDeferred()
    {
        try { Interlocked.Exchange(ref pendingJson, JsonSerializer.Serialize(store, JsonOpts)); }
        catch (Exception ex) { log.Warning(ex, "[Proteus] Failed to serialize presets."); return; }

        Task.Run(() =>
        {
            lock (writeGate)
            {
                var json = Interlocked.Exchange(ref pendingJson, null);
                if (json == null) return;   // a later flush already wrote a newer snapshot
                try { PenumbraModMeta.AtomicWrite(storePath, json); }
                catch (Exception ex) { log.Warning(ex, "[Proteus] Failed to save presets."); }
            }
        });
    }
}
