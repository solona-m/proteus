using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>
/// Watches Glamourer's designs directory and notifies <see cref="DesignBindingService"/> when a
/// design is saved or deleted. Purely passive: it never opens design file contents (the GUID
/// comes from the filename) and never writes to the directory, so it cannot lock or corrupt
/// Glamourer's files. Per-GUID debounced because Glamourer autosaves on every edit; save and
/// delete share the debounce so an atomic save (briefly deletes-then-recreates) lands as a save.
/// </summary>
public sealed class GlamourerDesignWatcher : IDisposable
{
    private const int DebounceMs = 400;

    private readonly DesignBindingService bindingService;
    private readonly IPluginLog log;
    private readonly FileSystemWatcher? watcher;
    // One debounce slot per design GUID. Kept for the session rather than removed when a run fires:
    // the gate IS the per-GUID slot, and dropping it mid-flight would let a concurrent Schedule build a
    // second gate that can't cancel the first. Bounded by the number of designs touched in a session.
    private readonly ConcurrentDictionary<Guid, DebounceGate> debounce = new();

    // Set first thing in Dispose. Stopping the gates we already hold is not enough on its own: a
    // FileSystemWatcher event dispatched to the pool just before the handlers came off can still be
    // inside Schedule, and its GetOrAdd would mint a BRAND-NEW gate that the teardown loop never sees —
    // then fire OnDesignSaved at a DesignBindingService the plugin has already torn down.
    private volatile bool stopped;

    public GlamourerDesignWatcher(DesignBindingService bindingService, string? designsDir, IPluginLog log)
    {
        this.bindingService = bindingService;
        this.log            = log;

        if (string.IsNullOrEmpty(designsDir) || !Directory.Exists(designsDir))
        {
            log.Warning("[Proteus] Glamourer designs directory not found ({0}); design auto-binding disabled.",
                designsDir ?? "null");
            return;
        }

        try
        {
            watcher = new FileSystemWatcher(designsDir, "*.json")
            {
                NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error   += OnError;
            watcher.EnableRaisingEvents = true;
            log.Information("[Proteus] Watching Glamourer designs at {0}", designsDir);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Failed to watch Glamourer designs dir; auto-binding disabled.");
            watcher = null;
        }
    }

    private void OnChanged(object _, FileSystemEventArgs e) => Schedule(e.FullPath);
    private void OnRenamed(object _, RenamedEventArgs e)    => Schedule(e.FullPath);
    private void OnError(object _, ErrorEventArgs e)        => log.Warning(e.GetException(), "[Proteus] Design watcher error.");

    private void Schedule(string fullPath)
    {
        if (stopped) return; // tearing down — see the field
        if (!Guid.TryParse(Path.GetFileNameWithoutExtension(fullPath), out var id))
            return; // not a {guid}.json design file

        var gate = debounce.GetOrAdd(id, _ => new DebounceGate());
        // Second half of the teardown check: Dispose can have run between the check above and this
        // GetOrAdd, and a gate inserted after its loop is one nothing else will ever stop. Stop it here.
        if (stopped) { gate.Stop(); return; }

        // Supersedes the pending run for this design and issues our token, both under the gate's lock.
        // Glamourer saves by deleting and recreating the file, so several events for one GUID arrive
        // within milliseconds — the previous hand-rolled version could dispose this call's own source
        // before it read .Token, throwing out of a FileSystemWatcher event handler. See DebounceGate.
        var token = gate.Next();

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(DebounceMs, token); }
            catch (OperationCanceledException) { return; }
            // Decide save vs delete by the final state: if the file is present after the
            // debounce window, treat as save (covers Glamourer's atomic "delete + recreate"
            // save flow); if it's gone, the design was actually deleted.
            if (File.Exists(fullPath))
                bindingService.OnDesignSaved(id); // marshals to the framework thread internally
            else
                bindingService.OnDesignDeleted(id);
        }, token);
    }

    public void Dispose()
    {
        stopped = true;   // before anything else, so a Schedule already in flight bails or self-stops
        if (watcher != null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnChanged;
            watcher.Changed -= OnChanged;
            watcher.Deleted -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error   -= OnError;
            watcher.Dispose();
        }
        foreach (var gate in debounce.Values)
            gate.Stop();
        debounce.Clear();
    }
}
