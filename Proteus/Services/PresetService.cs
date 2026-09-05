using System;
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
    /// <summary>
    /// Read at load, not merely written. <c>design_bindings.json</c> stamps a version it never looks at,
    /// which left every later field change to be absorbed one nullable at a time; this file does not
    /// repeat that. An unknown (future) version is loaded as empty rather than half-understood.
    /// </summary>
    public int Version { get; set; } = ModPresetStore.CurrentVersion;

    public const int CurrentVersion = 1;

    /// <summary>Penumbra mod directory → that mod's user-saved presets, in display order.</summary>
    public Dictionary<string, List<ModPreset>> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Penumbra mod directory → the preset currently pinned on it, if any.</summary>
    public Dictionary<string, Guid> Applied { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rebuild both maps case-insensitively. Deserialization ignores a property initializer's comparer
    /// and hands back an ordinal dictionary, so without this a mod whose Penumbra folder is looked up
    /// with different casing than it was saved under would appear to have lost every preset — silently,
    /// and only after a restart. Mod directories are matched case-insensitively everywhere else here.
    /// </summary>
    public ModPresetStore Normalized()
    {
        Presets = new Dictionary<string, List<ModPreset>>(Presets, StringComparer.OrdinalIgnoreCase);
        Applied = new Dictionary<string, Guid>(Applied, StringComparer.OrdinalIgnoreCase);
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
/// Named looks for a single mod: save what is on screen, apply it back, share it.
/// <para/>
/// A preset is applied NON-DESTRUCTIVELY. Its option ticks go to Penumbra, because that is the only
/// place option state exists — but its colours, layer settings and stack order ride in an
/// <see cref="OverlayOverrideBag"/> published to the compositor's preset channel, exactly as a design
/// binding's do on its own channel. The mod's <c>metadata.json</c> is never written, so trying five
/// looks in a row costs nothing and "No preset" always gets the author's own colours back.
/// <para/>
/// Precedence, resolved per mod in <see cref="CompositorService.MergeByMod"/>: a pinned preset beats an
/// active design binding beats the mod's metadata. Applying a design drops the pins
/// (<see cref="DesignBindingService.PresetsSuperseded"/>) — a whole-look switch that visibly skipped one
/// mod would read as a bug — but never deletes a saved preset.
/// </summary>
public class PresetService : IDisposable
{
    private readonly PenumbraBridge penumbra;
    private readonly SidecarDiscoveryService discovery;
    private readonly CompositorService compositor;
    private readonly DesignBindingService bindings;
    private readonly IPluginLog log;

    // Write-only encoder, as with design bindings: preset and option names are user- and author-authored
    // and often non-ASCII, and escaping them makes the file unreadable. See ProteusJson.
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

    /// <summary>The bag the editor writes through while a preset is pinned. Exposed for
    /// <see cref="OverlayEditRouter"/>, which is the only thing that should touch it.</summary>
    internal OverlayOverrideBag Overrides => overrides;

    // ── Reading ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Just enough of a preset to draw a chip or a combo row. The UI asks for this every frame, for every
    /// preset of every mod on screen, so it must not deep-clone colour tables to answer — which is all
    /// <see cref="PresetsFor"/> does that this does not.
    /// </summary>
    public record PresetInfo(Guid Id, string Name, string? Description, PresetSource Source, DateTime LastEditUtc);

    /// <summary>The per-frame listing. See <see cref="PresetInfo"/> for why it is not just PresetsFor.</summary>
    public List<PresetInfo> ListFor(OverlayEntry entry)
    {
        var result = new List<PresetInfo>();

        foreach (var p in entry.Metadata.Presets ?? [])
            result.Add(new PresetInfo(
                p.Id == Guid.Empty ? StableId(entry.ModDirectory, p.Name) : p.Id,
                p.Name, p.Description, PresetSource.Pack, p.LastEditUtc));

        lock (gate)
            if (store.Presets.TryGetValue(entry.ModDirectory, out var mine))
                foreach (var p in mine)
                    result.Add(new PresetInfo(p.Id, p.Name, p.Description, PresetSource.User, p.LastEditUtc));

        return result;
    }

    /// <summary>One preset by id, as an independent copy — for the paths that need its actual contents
    /// (apply, fork, export, share code).</summary>
    public ModPreset? Get(OverlayEntry entry, Guid id)
        => PresetsFor(entry).FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Every preset offered for this mod: the author's first, then the wearer's. Pack presets are read
    /// fresh from the sidecar each call rather than cached, so a mod update takes effect immediately —
    /// they are never persisted on our side, which is exactly why updating them is safe.
    /// </summary>
    public List<ModPreset> PresetsFor(OverlayEntry entry)
    {
        var result = new List<ModPreset>();

        foreach (var packPreset in entry.Metadata.Presets ?? [])
        {
            var copy = packPreset.Clone();
            copy.Source = PresetSource.Pack;
            // A pack preset's identity has to be stable across sessions (the pin points at it) but the
            // author need not have written one. Derive it from the mod and the name so the same preset
            // keeps the same id, and two packs' "Sheer" never collide.
            if (copy.Id == Guid.Empty) copy.Id = StableId(entry.ModDirectory, copy.Name);
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

    /// <summary>The pinned preset's display name, or null when nothing is pinned (or the pin dangles —
    /// a preset deleted from a pack by a mod update, say, which reads as "no preset" rather than as an
    /// error the wearer can do anything about).</summary>
    public string? AppliedNameFor(OverlayEntry entry)
    {
        if (AppliedIdFor(entry.ModDirectory) is not { } id) return null;
        return PresetsFor(entry).FirstOrDefault(p => p.Id == id)?.Name;
    }

    // Drift is asked for once per chip per frame, and answering it honestly costs a Penumbra IPC call
    // plus two full serializations — far too much at 60 fps for a one-character marker. Cached per mod
    // and recomputed a few times a second, which is well inside the time it takes to notice a dot appear.
    private const int DriftCacheMs = 250;
    private readonly Dictionary<string, (Guid Id, long Tick, bool Modified)> driftCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the live look has drifted from the pinned preset — the `●` on the chip. Compares a fresh
    /// capture against what was saved, so it answers "would Update change anything", which is the only
    /// question the marker is asked.
    /// </summary>
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

    /// <summary>Forget the cached drift answer for a mod, so the marker updates on the very next frame
    /// rather than up to <see cref="DriftCacheMs"/> later. Called wherever we already know it changed.</summary>
    private void InvalidateDrift(string modDir)
    {
        lock (gate) driftCache.Remove(modDir);
    }

    /// <summary>
    /// Whether the live look still matches what was saved — the question the <c>●</c> marker asks, which
    /// is "did YOU change something", not "did anything change".
    /// <para/>
    /// Not symmetric, and deliberately so: <paramref name="saved"/> is the stored preset and
    /// <paramref name="live"/> is a fresh capture. Options are compared only over the groups the preset
    /// actually names AND the mod still offers. Both exclusions matter —
    /// <list type="bullet">
    ///   <item>a group the mod has GAINED since is not something the wearer did, and counting it marked
    ///         every preset in a pack as edited the moment its author shipped an update;</item>
    ///   <item>a group the mod has LOST is not something the wearer can put back, so flagging it only
    ///         offers an Update that would quietly drop it from the preset.</item>
    /// </list>
    /// Colours, gear and stack order are compared whole, via their JSON — the same round-trip the rest of
    /// this codebase uses for deep-cloning overrides, so it agrees by construction with what is stored.
    /// Name, id and timestamps are never compared: renaming a preset is not drift.
    /// </summary>
    internal static bool SameLook(ModPreset saved, ModPreset live)
        => SameOptions(saved.Options, live.Options) && AppearanceJson(saved) == AppearanceJson(live);

    private static bool SameOptions(
        Dictionary<string, List<string>> saved, Dictionary<string, List<string>> live)
    {
        foreach (var (group, wanted) in saved)
        {
            if (!live.TryGetValue(group, out var now)) continue;   // the mod dropped this group

            // Order-insensitive: Penumbra hands back a multi-select group's ticks in its own order, and
            // the same two options arriving the other way round is not an edit.
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

    /// <summary>
    /// Snapshot what this mod looks like right now into a new preset. Goes through
    /// <see cref="DesignBindingService.CaptureMod"/> so a preset and a design binding can never disagree
    /// about what "the current look" means, then keeps only the portable fields.
    /// </summary>
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

    /// <summary>Save a new preset for this mod and pin it. Returns the STORED preset — which is not the
    /// captured one: <see cref="Add"/> mints a fresh id and may disambiguate the name, and pinning the
    /// pre-storage copy would leave the pin pointing at an id no preset has.</summary>
    public ModPreset Save(OverlayEntry entry, Guid collId, string name)
    {
        var stored = Add(entry.ModDirectory, Capture(entry, collId, name));
        Apply(entry, collId, stored);
        return stored;
    }

    /// <summary>
    /// Fold the current look back into an existing user preset — the "Update" button. A pack preset
    /// cannot be updated in place; the UI forks it first.
    /// </summary>
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

    /// <summary>Store a preset against a mod without applying it — the import and fork paths. The
    /// preset is cloned in, so the caller keeps no handle on what is now stored.</summary>
    public ModPreset Add(string modDir, ModPreset preset)
    {
        var stored = preset.Clone();
        // Whatever it claimed to be, anything entering this store is the wearer's own: a shared file
        // must not be able to arrive read-only, and a fork of a pack preset is by definition editable.
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

    /// <summary>"Sheer" beside an existing "Sheer" becomes "Sheer (2)". Silently deduplicating beats
    /// refusing the save: the wearer is naming a look, not filling in a key.</summary>
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

    /// <summary>A deterministic id for a pack preset the author gave none, so the pin survives a
    /// restart. Guid.CreateVersion8 style would be nicer but this only has to be stable and collision-
    /// free across one mod's own preset names.</summary>
    private static Guid StableId(string modDir, string name)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes($"proteus-pack-preset {modDir.ToLowerInvariant()} {name}"));
        return new Guid(bytes);
    }

    // ── Applying ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Put a preset on. Option ticks are written to Penumbra; colours, gear and stack order are
    /// published as a live override. Anything the mod no longer has is reported rather than forced —
    /// a renamed group is a mod update, not a reason to refuse the other nine.
    /// </summary>
    public PresetApplyReport Apply(OverlayEntry entry, Guid collId, ModPreset preset)
    {
        // The mod's real option catalogue, straight out of its Penumbra manifest. Reading the manifest
        // rather than asking Penumbra keeps this honest about what the MOD offers rather than what the
        // collection currently has selected, which is the thing a stale preset has to be checked against.
        var plan = PlanOptionWrites(preset.Options, ReadCatalogue(entry));

        foreach (var (group, selection) in plan.Writes)
            penumbra.SetModOption(collId, entry.ModDirectory, group, selection);

        var missingGroups  = plan.MissingGroups;
        var missingOptions = plan.MissingOptions;
        var applied        = plan.Writes.Count;

        // A clone, so editing the live look through the colour editor never writes into the saved preset:
        // that is what makes the `●` drift marker meaningful and Update a deliberate act.
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

    /// <summary>
    /// Work out which of a preset's option selections the mod can still honour. Pure, and separated from
    /// <see cref="Apply"/> for that reason — it is the whole of "apply what matches, report the rest", and
    /// the cases that matter (a renamed group, a dropped option, an unreadable manifest) are ones nobody
    /// wants to reproduce by hand in a running game.
    /// </summary>
    /// <param name="catalogue">
    /// Group → the options it offers, or NULL when the mod's manifest could not be read. Null is not "the
    /// mod has no groups": it means we know nothing, so the preset is written verbatim rather than being
    /// reported as entirely missing.
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

            // Every option in this group is gone. Writing the empty list would CLEAR the group, which is a
            // real and different instruction — "wear none of these" — that the preset never gave. Leave
            // whatever is selected alone and say so.
            if (keep.Count == 0 && wanted.Count > 0) continue;

            writes.Add((group, keep));
        }

        return new OptionPlan(writes, missingGroups, missingOptions);
    }

    /// <summary>Unpin whatever is on this mod. Colours fall straight back to the mod's own metadata;
    /// the option ticks are deliberately left where they are, because unpinning a look is not a request
    /// to undo the wearer's own toggling.</summary>
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
    /// Drop every pin, without touching a single saved preset. Wired to
    /// <see cref="DesignBindingService.PresetsSuperseded"/>: a design carries its own colours for every
    /// mod it captured, so a preset left pinned on top would make that design look broken on one mod.
    /// <para/>
    /// Does not recomposite — the design restore that raised this is about to do it anyway, and doing it
    /// here would only add a composite in the middle of one.
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
    /// Republish the pinned presets' overrides, so a reload comes back looking the same.
    /// <para/>
    /// Deliberately writes NOTHING to Penumbra: those option ticks were written when the preset was
    /// applied and have persisted in the collection ever since, and re-writing them here would race the
    /// design binding's boot restore for the same settings.
    /// <para/>
    /// Tried from the constructor (which is the plugin-reload case, where Penumbra is already up and
    /// <c>PenumbraReady</c> will never fire again) and again on <c>PenumbraReady</c> for a cold boot.
    /// Before Penumbra answers, <see cref="SidecarDiscoveryService.DiscoverAll"/> returns an EMPTY list
    /// rather than failing — so a pin that cannot be resolved here is never treated as dead. Pruning on
    /// that signal would have deleted every pin on every launch that got here first.
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

        // On the cold-boot path the compositor's own PenumbraReady handler is subscribed ahead of ours
        // (it is constructed first), so the first composite can already have been kicked off against
        // metadata colours. Ask for another now that the overrides are up.
        if (done > 0 && triggerComposite) compositor.TriggerRecomposite("preset-republish");
    }

    /// <summary>
    /// Group name → the option names that group actually offers, read from the mod's Penumbra manifest.
    /// Null when the manifest cannot be read at all, which is the signal to apply the preset verbatim
    /// rather than to report every group as missing — an unreadable manifest says nothing about the mod.
    /// </summary>
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

    /// <summary>Serialize now (the caller holds <c>gate</c>, so <c>store</c> is consistent) but write off
    /// the calling thread — every caller here is an ImGui button on the framework thread. Whichever flush
    /// runs first writes the newest snapshot; superseded ones find nothing and skip.</summary>
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
