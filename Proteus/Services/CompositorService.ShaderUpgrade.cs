using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>Where a shader-upgrade copy came from, per game path. Implemented by <see cref="ShaderUpgradeWatcher"/>.</summary>
public interface IShaderUpgradeSources
{
    /// <summary>The mod file the copy redirected at <paramref name="gamePath"/> was made from, or null.</summary>
    string? SourceFor(string gamePath);
}

/// <summary>
/// The compositor's side of the gear shader upgrade (<see cref="ShaderUpgradeWatcher"/>). The two own paths in both
/// directions: the watcher never redirects what the compositor publishes (<see cref="PublishedGamePaths"/>), and the
/// compositor reads through the watcher's copies to their sources (<see cref="ResolveSeeThrough"/>), so a composite
/// never stands on an upgraded copy that disappears when the setting goes off.
/// </summary>
public partial class CompositorService
{
    private volatile IShaderUpgradeSources? _shaderUpgradeSources;

    /// <summary>Who to ask where a copy came from; null to stop asking.</summary>
    internal void UseShaderUpgradeSources(IShaderUpgradeSources? sources) => _shaderUpgradeSources = sources;

    private volatile IReadOnlySet<string> _publishedGamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private volatile bool _publishedKnown;

    /// <summary>
    /// The game paths the managed mod publishes, as of the last real publish (never the prime's narrowed manifest).
    /// Before the first write of a session it is read once from the live manifest.
    /// </summary>
    internal IReadOnlySet<string> PublishedGamePaths
    {
        get
        {
            if (!_publishedKnown && !string.IsNullOrEmpty(modsRoot))
            {
                if (PenumbraModMeta.TryReadDefaultData(managedModDir) is { } live)
                    _publishedGamePaths = new HashSet<string>(live.Files.Keys, StringComparer.OrdinalIgnoreCase);
                _publishedKnown = true;
            }
            return _publishedGamePaths;
        }
    }

    /// <summary>The published set changed (in either direction).</summary>
    public event Action? PublishedGamePathsChanged;

    private void SnapshotPublished(IEnumerable<string> gamePaths)
    {
        var next = new HashSet<string>(gamePaths, StringComparer.OrdinalIgnoreCase);
        var was = _publishedGamePaths;
        _publishedGamePaths = next;
        bool changed = !_publishedKnown || !next.SetEquals(was);
        _publishedKnown = true;
        if (changed)
        {
            try { PublishedGamePathsChanged?.Invoke(); }
            catch (Exception ex) { log.Warning(ex, "[Proteus] a published-set listener failed"); }
        }
    }

    /// <summary>
    /// What a raw resolve means to a composite: a shader-upgrade copy stands for its source (per game path from the
    /// watcher, else the upstream memo); anything else is passed through.
    /// </summary>
    internal static string? SeeThrough(string gamePath, string? raw, string? managedRoot,
                                       Func<string, string?>? sourceFor, Func<string, string?> memo)
    {
        if (raw == null || !ManagedModPaths.IsShaderUpgradeCopy(ShaderUpgradePlan.StripPenumbraPrefix(raw), managedRoot))
            return raw;
        return sourceFor?.Invoke(gamePath) ?? memo(gamePath);
    }

    /// <summary>
    /// <see cref="PenumbraBridge.ResolvePlayer"/> seen through the gear shader upgrade's copies. Every base read goes
    /// through this; the redirect check (which classifies holds) reads raw.
    /// </summary>
    internal string? ResolveSeeThrough(string gamePath)
    {
        var raw = penumbra.ResolvePlayer(gamePath);
        var sources = _shaderUpgradeSources;
        return SeeThrough(gamePath, raw, ManagedModFolder, sources == null ? null : sources.SourceFor,
                          p => _upstreamByGamePath.TryGetValue(p, out var d) ? d : null);
    }

    internal enum Hold { None, LiveBrush, ShaderUpgrade }

    /// <summary>Whether a resolved file is one of the temporary holds above our mod (not a loss, not our stale file).</summary>
    internal static Hold HeldBy(string? disk, string? managedRoot)
        => disk == null ? Hold.None
         : LiveBrushPreview.IsPreviewFile(disk) ? Hold.LiveBrush
         : ManagedModPaths.IsShaderUpgradeCopy(ShaderUpgradePlan.StripPenumbraPrefix(disk), managedRoot) ? Hold.ShaderUpgrade
         : Hold.None;

    /// <summary>Forget the upstream memo for these paths (the shader upgrade went off: they may have memoised a copy).</summary>
    internal void ForgetUpstreams(IEnumerable<string> gamePaths)
    {
        int n = ForgetPaths(gamePaths, _upstreamByGamePath, _upstreamSettled, _upstreamUnsettled, _upstreamIsGameData);
        if (n > 0) log.Debug("[Proteus] forgot {0} upstream(s) the shader upgrade held", n);
    }

    /// <summary>Drop exactly these paths from the memo and its marks; every other path is kept. Returns how many memo
    /// entries went.</summary>
    internal static int ForgetPaths(IEnumerable<string> gamePaths,
                                    System.Collections.Concurrent.ConcurrentDictionary<string, string> memo,
                                    params System.Collections.Concurrent.ConcurrentDictionary<string, byte>[] marks)
    {
        int n = 0;
        foreach (var p in gamePaths)
        {
            if (memo.TryRemove(p, out _)) n++;
            foreach (var m in marks) m.TryRemove(p, out _);
        }
        return n;
    }

    /// <summary>
    /// Whether a changed path can prove a new manifest live: not while the gear shader upgrade holds it, since it then
    /// resolves to the copy until the upgrade yields it.
    /// </summary>
    internal static bool CanProbe(string gamePath, IShaderUpgradeSources? held) => held?.SourceFor(gamePath) == null;

    /// <summary>Make the next reload Proteus starts a full redraw (the shader upgrade going off with Proteus).</summary>
    internal void RequireFullRedrawOnce() => _needFullRedraw = true;

    /// <summary>An auto refit batch is holding redraws for its one redraw at the end.</summary>
    internal bool RedrawsHeld => redrawHold.IsHeld;
}
