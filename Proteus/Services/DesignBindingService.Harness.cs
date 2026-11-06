#if PROTEUS_TEST_HARNESS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Proteus.Services;

/// <summary>The live design-binding state the test harness puts back: ids, and the live bag as JSON (null when inactive).</summary>
internal sealed record HarnessLiveSnapshot(Guid? Active, Guid? Suspended, Guid? LastActive, string? BagJson);

/// <summary>One binding as <c>GET /bindings</c> lists it.</summary>
internal sealed record HarnessBindingEntry(Guid Id, string? Name, DateTime CapturedUtc, int Mods, string Sha256);

/// <summary>Design binding as the test harness reads it. Names are the caller's to drop or scrub.</summary>
internal sealed record HarnessBindingView(
    bool Enabled, bool FollowsAutomation, bool RestoresCharacterMods, bool UnequipUnset, long EchoMsLeft,
    Guid? Active, string? ActiveName, IReadOnlyList<string>? ActiveFields, string? StoreSha256,
    IReadOnlyList<HarnessBindingEntry> Bindings, List<ColorTableRowPreset>? StoredRows);

/// <summary>
/// The test harness's view into design binding (ADR 0002). Compiled out of every CI build with the rest of the harness;
/// <see cref="HarnessSuppressApplySignals"/> is the release guard's second marker. Writes no binding store. Framework thread.
/// </summary>
public partial class DesignBindingService
{
    private sealed record BagJsonShape(Dictionary<string, OverlayColorOverride> Colors,
                                       Dictionary<string, OverlayGearOverride> Gear,
                                       Dictionary<string, List<string>> Stack);

    /// <summary>Open the echo window, so the finalization the harness's own Glamourer restore raises is ignored.</summary>
    internal void HarnessSuppressApplySignals()
    {
        lock (gate) suppressUntilTick = Environment.TickCount64 + RestoreSuppressMs;
    }

    /// <summary>Milliseconds left in the echo window (0 when closed).</summary>
    internal long HarnessEchoMsLeft()
    {
        lock (gate) return Math.Max(0, suppressUntilTick - Environment.TickCount64);
    }

    /// <summary>Stop a restore's deferred work: its post-load sweep, the temporary-setting guard and a queued unequip.</summary>
    internal void HarnessQuiesce()
    {
        characterWorkGeneration++;
        DisarmTemporaryGuard();
        unequipEpoch++;
    }

    internal HarnessLiveSnapshot HarnessSnapshotLive()
    {
        Guid? active, suspended;
        lock (gate) { active = activeDesignId; suspended = suspendedDesignId; }
        string? bag = null;
        if (overrides.Colors is { } colors)
            bag = JsonSerializer.Serialize(new BagJsonShape(
                new Dictionary<string, OverlayColorOverride>(colors, StringComparer.OrdinalIgnoreCase),
                overrides.Gear is { } g ? new Dictionary<string, OverlayGearOverride>(g, StringComparer.OrdinalIgnoreCase) : new(),
                overrides.Stack is { } s ? new Dictionary<string, List<string>>(s, StringComparer.OrdinalIgnoreCase) : new()));
        return new HarnessLiveSnapshot(active, suspended, config.LastActiveDesignId, bag);
    }

    /// <summary>Put the live state back as snapshotted: ids, the bag (fresh clones), and the holds its design implies.</summary>
    internal void HarnessRestoreLive(HarnessLiveSnapshot s)
    {
        lock (gate)
        {
            activeDesignId    = s.Active;
            suspendedDesignId = s.Suspended;
        }
        PersistActiveDesignId(s.LastActive);

        if (s.BagJson != null && JsonSerializer.Deserialize<BagJsonShape>(s.BagJson) is { } bag)
            overrides.Adopt(new(bag.Colors, StringComparer.OrdinalIgnoreCase), new(bag.Gear, StringComparer.OrdinalIgnoreCase),
                            new(bag.Stack, StringComparer.OrdinalIgnoreCase));
        else
            overrides.Clear();

        ReleaseHeldMods();
        DesignBinding? b = null;
        if (s.Active is { } id) lock (gate) store.Bindings.TryGetValue(id, out b);
        if (b != null && FeatureOn) Rehold(b, s.Active!.Value);
        compositor.TriggerRecomposite("test harness: binding put back");
    }

    /// <summary>The Proteus (sidecar) mods' folder names.</summary>
    internal IReadOnlySet<string> HarnessProteusDirs()
        => discovery.DiscoverAll().Select(e => e.ModDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The applied-field keys of a Glamourer design, or null when it can't be read.</summary>
    internal IReadOnlyList<string>? HarnessDesignFields(Guid id)
        => GetDesignCached(id) is { } d ? AppliedFieldKeys(d).OrderBy(k => k, StringComparer.Ordinal).ToList() : null;

    internal bool HarnessIsBound(Guid id) { lock (gate) return store.Bindings.ContainsKey(id); }

    internal HarnessBindingView HarnessView(string? mod, string? group, string? option)
    {
        Guid? active;
        List<HarnessBindingEntry> entries;
        List<ColorTableRowPreset>? stored = null;
        string? activeName = null;
        lock (gate)
        {
            active = activeDesignId;
            entries = store.Bindings.Values.OrderBy(b => b.DesignId)
                           .Select(b => new HarnessBindingEntry(b.DesignId, b.DesignName, b.CapturedUtc, b.Mods.Count,
                                                                Sha256(JsonSerializer.Serialize(b, JsonOpts))))
                           .ToList();
            if (active is { } id && store.Bindings.TryGetValue(id, out var ab))
            {
                activeName = ab.DesignName;
                if (mod != null
                    && ab.Mods.FirstOrDefault(m => string.Equals(m.ModDirectory, mod, StringComparison.OrdinalIgnoreCase)) is { } pm)
                    stored = group == null || option == null ? pm.Colors.Top
                           : pm.Colors.Options != null && pm.Colors.Options.TryGetValue(group, out var inner)
                             && inner.TryGetValue(option, out var rows) ? rows : null;
            }
        }

        string? storeSha = null;
        try { if (File.Exists(storePath)) storeSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(storePath))); }
        catch (IOException) { /* mid-save: reported as unknown */ }

        return new HarnessBindingView(config.DesignBindingEnabled && config.PluginEnabled, config.DesignBindingFollowsAutomation,
                                      config.DesignBindingRestoresCharacterMods, config.DesignBindingUnequipUnsetSlots,
                                      HarnessEchoMsLeft(), active, activeName,
                                      active is { } a ? HarnessDesignFields(a) : null, storeSha, entries,
                                      stored?.Select(r => r.Clone()).ToList());
    }

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
#endif
