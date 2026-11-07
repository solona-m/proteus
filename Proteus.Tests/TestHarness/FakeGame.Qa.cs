using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Services;
using Proteus.TestHarness;

namespace Proteus.Tests.TestHarness;

/// <summary>The idle-disk-io side of <see cref="FakeGame"/>: the boot probe, collections, the auto-refit override, the body
/// index, the counters, and Proteus sidecars read from and saved to real files under the mods root.</summary>
internal sealed partial class FakeGame
{
    public (bool Latched, int Step, long NextDueInMs) BootValue { get; set; } = (true, 0, 0);
    public int Rearms;

    public (bool Latched, int Step, long NextDueInMs) Boot() => BootValue;

    public void RearmBoot()
    {
        Rearms++;
        BootValue = (false, 0, 0);
    }

    public static readonly Guid OtherCollection = Guid.Parse("99999999-8888-7777-6666-555555555555");

    /// <summary>What <see cref="Collections"/> answers; null asks for the default (the player's plus one other).</summary>
    public List<(Guid Id, bool Player)>? CollectionList { get; set; }
    public bool CollectionsFail { get; set; }

    public IReadOnlyList<(Guid Id, bool Player)>? Collections()
    {
        Fw(nameof(Collections));
        if (CollectionsFail || !PenumbraUp) return null;
        return CollectionList ?? [(Collection!.Value, true), (OtherCollection, false)];
    }

    public bool AutoRefitConfig { get; set; }
    public bool? AutoRefitOverrideValue { get; set; }

    public (bool Config, bool? Override, bool Effective) AutoRefitEnabled()
        => (AutoRefitConfig, AutoRefitOverrideValue, AutoRefitOverrideValue ?? AutoRefitConfig);

    public void SetAutoRefitOverride(bool? enabled) => AutoRefitOverrideValue = enabled;

    // ── shell output failures (issue sweep D) ──
    public string OutputDir { get; set; } = Path.Combine(Path.GetTempPath(), "proteus-fake-output");
    public string ManagedOutputDir() => OutputDir;
    public (string Shell, string Slot, string Until)? ShellFaultValue { get; set; }
    public bool ArmShellFault(string shell, string slot, string until)
    {
        if (ShellFaultValue != null) return false;
        ShellFaultValue = (shell, slot, until);
        return true;
    }
    public bool ClearShellFault()
    {
        bool had = ShellFaultValue != null;
        ShellFaultValue = null;
        return had;
    }
    public bool AutoRedrawConfig { get; set; } = true;
    public bool? AutoRedrawOverrideValue { get; set; }
    public (bool Config, bool? Override, bool Effective) AutoRedrawEnabled()
        => (AutoRedrawConfig, AutoRedrawOverrideValue, AutoRedrawOverrideValue ?? AutoRedrawConfig);
    public void SetAutoRedrawOverride(bool? enabled) => AutoRedrawOverrideValue = enabled;
    public object RecoveryState() => new { writeRetry = (object?)null, shellDrawn = (object?)null,
                                           shellFault = ShellFaultValue is { } f ? new { shell = f.Shell } : null };

    public ConcurrentQueue<string> RefitsWhileOverridden { get; } = new();

    public IReadOnlyList<string> TakeRefitsWhileOverridden()
    {
        var list = new List<string>();
        while (RefitsWhileOverridden.TryDequeue(out var m)) list.Add(m);
        return list;
    }

    public (bool Built, bool Stale, bool Refreshing, long Generation) BodyIndexValue { get; set; } = (true, false, false, 3);
    public (bool Built, bool Stale, bool Refreshing, long Generation) BodyIndex() => BodyIndexValue;

    public (long WindowAgeMs, IReadOnlyDictionary<string, long> Counts)? IoValue { get; set; }
    public (long WindowAgeMs, IReadOnlyDictionary<string, long> Counts)? Io() => IoValue;

    // ── sidecars ────────────────────────────────────────────────────────────

    public string SidecarRoot(string modDir) => Path.Combine(ModsRoot, modDir, SidecarDiscoveryService.SidecarSubdir);
    public string MetaPath(string modDir) => Path.Combine(SidecarRoot(modDir), SidecarDiscoveryService.MetadataFile);

    /// <summary>Write a Proteus sidecar for an installed mod: top-level rows with the given diffuse colours.</summary>
    public void PutSidecar(string modDir, params (int Row, string Diffuse)[] rows)
    {
        Directory.CreateDirectory(SidecarRoot(modDir));
        var meta = new ProteusMetadata
        {
            ColorTableRows = rows.Select(r => new ColorTableRowPreset { Row = r.Row, SubRowA = new ColorTableSubRowPreset { Diffuse = r.Diffuse } }).ToList(),
        };
        File.WriteAllText(MetaPath(modDir), JsonSerializer.Serialize(meta, ProteusJson.MetadataWrite));
    }

    public OverlayEntry? DiscoverEntry(string modDir)
    {
        Fw(nameof(DiscoverEntry));
        var path = MetaPath(modDir);
        if (!File.Exists(path)) return null;
        var meta = JsonSerializer.Deserialize<ProteusMetadata>(File.ReadAllText(path), ProteusJson.MetadataRead)!;
        return new OverlayEntry(modDir, modDir, 0, true, meta, SidecarRoot(modDir));
    }

    /// <summary>Runs before the save writes; false fails it. Called on the edit's worker, under the sidecar lock.</summary>
    public Func<OverlayEntry, bool>? SaveHook { get; set; }

    /// <summary>Copy the file aside as metadata.default.json before the first save, as SaveMetadata's snapshot does.</summary>
    public bool SnapshotDefaultsOnSave { get; set; } = true;

    public int Saves;

    public bool SaveSidecar(OverlayEntry entry)
    {
        Fw(nameof(SaveSidecar));
        if (SaveHook != null && !SaveHook(entry)) return false;
        var path = Path.Combine(entry.SidecarRoot, SidecarDiscoveryService.MetadataFile);
        var defaults = Path.Combine(entry.SidecarRoot, "metadata.default.json");
        if (SnapshotDefaultsOnSave && !File.Exists(defaults) && File.Exists(path)) File.Copy(path, defaults);
        File.WriteAllText(path, JsonSerializer.Serialize(entry.Metadata, ProteusJson.MetadataWrite));
        System.Threading.Interlocked.Increment(ref Saves);
        return true;
    }

    public ConcurrentDictionary<string, byte> StagedMods { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>From this call on (1-based), every mod reads as having a staged edit: a user edit made mid-request.</summary>
    public int? StagedFromCall { get; set; }
    private int stagedCalls;

    public bool HasStaged(string modDir)
        => System.Threading.Interlocked.Increment(ref stagedCalls) >= (StagedFromCall ?? int.MaxValue)
        || StagedMods.ContainsKey(modDir);

    public ConcurrentQueue<string> Forgotten { get; } = new();
    public void ForgetSidecar(string modDir) => Forgotten.Enqueue(modDir);

    public ConcurrentQueue<string> Recomposites { get; } = new();

    public void Recomposite(string reason)
    {
        Fw(nameof(Recomposite));
        Recomposites.Enqueue(reason);
    }
}
