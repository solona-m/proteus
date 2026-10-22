using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// A mod's metadata waiting to be written, as the JSON it will be written as. Immutable: every reader deserializes its
/// own graph, so a composite thread never shares one with the editor.
/// </summary>
internal sealed record StagedMetadata(string ModDirectory, string SidecarRoot, string Json, long Version,
                                      int Failures, long RetryAt);

/// <summary>What became of a held edit whose write failed.</summary>
internal enum StagedFailure
{
    /// <summary>Something newer was held meanwhile, or it was moved: the failed one is simply gone.</summary>
    Superseded,
    /// <summary>Still held, due again after <see cref="MetadataWriteBehind.RetryDelayMs"/>.</summary>
    Retrying,
    /// <summary>The last try: dropped.</summary>
    GaveUp,
}

/// <summary>
/// The colour editor's metadata writes, held while a control is dragged and written once it is let go. Discovery reads
/// through it (see <see cref="SidecarDiscoveryService.ReadForDiscovery"/>), or every composite mid-drag would re-read the
/// old file and put the old values back in the editor. No IO here: <see cref="SidecarDiscoveryService"/> does the writing.
/// </summary>
internal sealed class MetadataWriteBehind
{
    /// <summary>A write that fails waits this long before it is due again: a locked file, or a mod being renamed.</summary>
    public const long RetryDelayMs = 1000;

    /// <summary>Failed writes before the edit is given up on.</summary>
    public const int MaxFailures = 5;

    private readonly object gate = new();
    private readonly Dictionary<string, StagedMetadata> staged = new(StringComparer.OrdinalIgnoreCase);
    private long version;

    /// <summary>Hold <paramref name="json"/> as the mod's metadata, replacing anything held, failures included.</summary>
    public void Stage(string modDir, string sidecarRoot, string json)
    {
        lock (gate) staged[modDir] = new StagedMetadata(modDir, sidecarRoot, json, ++version, Failures: 0, RetryAt: 0);
    }

    public StagedMetadata? Peek(string modDir)
    {
        lock (gate) return staged.GetValueOrDefault(modDir);
    }

    /// <summary>What should be written now: nothing while a control is held, else everything not waiting on a retry.</summary>
    public IReadOnlyList<StagedMetadata> Due(bool editing, long now)
    {
        if (editing) return [];
        lock (gate) return staged.Values.Where(s => s.RetryAt <= now).ToList();
    }

    /// <summary>Everything held, retry or not: for a close or an unload, which will not get another chance.</summary>
    public IReadOnlyList<StagedMetadata> All()
    {
        lock (gate) return staged.Values.ToList();
    }

    /// <summary><paramref name="written"/> is on disk. Removed unless something newer was staged meanwhile.</summary>
    public void Complete(StagedMetadata written)
    {
        lock (gate)
            if (IsCurrent(written)) staged.Remove(written.ModDirectory);
    }

    /// <summary>
    /// The write of <paramref name="failed"/> did not land. Kept, so discovery still reads it, and due again after
    /// <see cref="RetryDelayMs"/> — or dropped, on the last try. A newer stage is left alone.
    /// </summary>
    public StagedFailure Failed(StagedMetadata failed, long now)
    {
        lock (gate)
        {
            if (!IsCurrent(failed)) return StagedFailure.Superseded;
            int failures = failed.Failures + 1;
            if (failures >= MaxFailures)
            {
                staged.Remove(failed.ModDirectory);
                return StagedFailure.GaveUp;
            }
            staged[failed.ModDirectory] = failed with { Failures = failures, RetryAt = now + RetryDelayMs };
            return StagedFailure.Retrying;
        }
    }

    /// <summary>A mod renamed in Penumbra: what it holds moves to the new name and the new folder.</summary>
    public void Follow(string oldDir, string newDir)
    {
        lock (gate)
        {
            if (!staged.Remove(oldDir, out var s)) return;
            // The sidecar is <mods>/<mod>/Proteus: swap the mod folder, keep the rest.
            var modFolder = Path.GetDirectoryName(s.SidecarRoot);
            var mods = modFolder == null ? null : Path.GetDirectoryName(modFolder);
            var root = mods == null ? s.SidecarRoot
                     : Path.Combine(mods, newDir, Path.GetFileName(s.SidecarRoot));
            staged[newDir] = s with { ModDirectory = newDir, SidecarRoot = root };
        }
    }

    private bool IsCurrent(StagedMetadata s)
        => staged.TryGetValue(s.ModDirectory, out var now) && now.Version == s.Version;
}
