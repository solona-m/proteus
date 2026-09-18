using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Penumbra.Api.Enums;

namespace Proteus.Services;

using static Proteus.Services.DrawnModelPaths;

public partial class CompositorService
{
    /// <summary>
    /// The last few redirect maps we published, newest last. <see cref="PruneSupersededOutput"/> keeps anything any of
    /// them names: the reload is asynchronous and composites overlap, so the newest map need not be live. Empty on
    /// startup, so nothing is pruned until we publish (last session's manifest is still served). Guarded by
    /// <see cref="_publishHistoryLock"/>.
    /// </summary>
    private readonly Queue<IDictionary<string, string>> _publishHistory = new();
    private readonly object _publishHistoryLock = new();

    /// <summary>How many past manifests to treat as possibly-live (room for a reload queued behind another).</summary>
    private const int PublishHistoryDepth = 3;

    /// <summary>
    /// Remember a manifest we just published as possibly-live, retiring the oldest, and return the one it replaces (null
    /// on the first publish of a session), so the caller can probe only changed redirects.
    /// </summary>
    private IDictionary<string, string>? RecordPublish(IDictionary<string, string> redirects)
    {
        // Snapshot: the caller's map is a live ConcurrentDictionary this reference would otherwise outlive.
        var snapshot = new Dictionary<string, string>(redirects, StringComparer.OrdinalIgnoreCase);
        lock (_publishHistoryLock)
        {
            var previous = _publishHistory.Count > 0 ? _publishHistory.Last() : null;
            _publishHistory.Enqueue(snapshot);
            while (_publishHistory.Count > PublishHistoryDepth) _publishHistory.Dequeue();
            return previous;
        }
    }

    /// <summary>
    /// Delete output files nothing can still point at, at the start of a composite (so a cancelled run's files are
    /// collected too). Keeps anything named by any of the last <see cref="PublishHistoryDepth"/> manifests, and stands
    /// down while another composite is in flight. A dangling live redirect makes the body invisible. Safe for files in
    /// SecondSkinService's hash cache: the write path re-checks File.Exists.
    /// </summary>
    private void PruneSupersededOutput()
    {
        // >1 because this composite has already counted itself in.
        var others = Volatile.Read(ref _compositesInFlight) - 1;
        if (others > 0)
        {
            log.Debug("[Proteus] prune deferred — {0} other composite(s) still running and writing", others);
            return;
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_publishHistoryLock)
        {
            if (_publishHistory.Count == 0) return;   // see the field: last session's manifest is still live
            foreach (var map in _publishHistory)
                foreach (var rel in map.Values)
                    keep.Add(rel.Replace('\\', '/'));   // Rel() emits backslashes, the skin path forward slashes
        }

        PruneManagedOutput(keep);
    }

    /// <summary>
    /// Delete any file under textures/ materials/ models/ whose <c>sub/name</c> is not in <paramref name="keep"/>, which
    /// callers build from every manifest that could still be live.
    /// </summary>
    private void PruneManagedOutput(HashSet<string> keep)
    {
        foreach (var sub in new[] { "textures", "materials", "models" })
        {
            var dir = Path.Combine(managedModDir, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir))
                if (!keep.Contains(sub + "/" + Path.GetFileName(f)))
                    try { File.Delete(f); } catch { }
        }
    }

    /// <param name="userRequested">See <see cref="RefreshPlayerTextures"/>: the redraw is the action, so auto redraw does not gate it.</param>
    private void ReloadAndRedraw(bool redraw = true, bool userRequested = false)
    {
        var ec = penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
        log.Debug("[Proteus] ReloadMod -> {0}", ec);
        if (redraw && (config.AutoRedraw || userRequested))
        {
            // Give Penumbra's async reload time to process before the refresh re-requests textures.
            Thread.Sleep(300);
            RefreshPlayerTextures(userRequested);
        }
    }

    /// <summary>
    /// True when the last run wrote a second-skin shell: Glamourer's in-place reload only re-requests textures, so a
    /// changed .mdl or .mtrl needs a real redraw.
    /// </summary>
    private volatile bool _needFullRedraw;

    /// <summary>
    /// True when the last composite redirected an accessory's model to our merged shell model; reverting that needs a
    /// full redraw, since an in-place reload never reloads an accessory's .mdl.
    /// </summary>
    private volatile bool _secondSkinActive;

    /// <summary>The shell host model paths redirected last composite; when the set changes the vacated accessory needs a
    /// full redraw to reload its real model.</summary>
    private HashSet<string> _lastShellHostPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The face models rewritten into the doubled layout by the last composite. A set that shrinks forces a
    /// redraw so the face reloads its real model.</summary>
    private HashSet<string> _lastFaceUvModelPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Skin material game path → the rewritten copy the last composite published. Any change forces
    /// a full redraw, and so does withdrawing them: the copy names a texture only our manifest serves.</summary>
    private Dictionary<string, string> _lastSkinMaterialRedirects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The face materials the last composite rendered doubled, read by the editor's
    /// <see cref="NeedsUnmirroredShell(OverlayDescriptor)"/>. Replaced wholesale, never mutated.
    /// </summary>
    private volatile IReadOnlySet<string> _faceDoubledMaterials =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The subset of <see cref="_lastShellHostPaths"/> we appended into (the player's own item, read back as the merge
    /// base): the only published .mdl paths <see cref="PrimeUpstreamCache"/> may unpublish. Seeded from config, since the
    /// manifest outlives the session.
    /// </summary>
    private volatile HashSet<string> _appendHostModelPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The hair model path the player was last seen wearing, for change detection.</summary>
    private volatile string? _lastHairPath;

    /// <summary>
    /// Raised on the framework thread when the player puts on a different hairstyle. A hairstyle change fires no other
    /// event, so both model walks route through here.
    /// </summary>
    internal event Action? HairChanged;

    /// <summary>Compare the walked hair path against the last one and raise <see cref="HairChanged"/>.</summary>
    private void NoteHairChange()
    {
        var hair = _humanPartModels?.FirstOrDefault(
            p => p.Contains("/obj/hair/", StringComparison.OrdinalIgnoreCase));
        // Null means the walk carried no hair (teardown, or none): not a change to report.
        if (hair == null || string.Equals(hair, _lastHairPath, StringComparison.Ordinal)) return;
        _lastHairPath = hair;
        try { HairChanged?.Invoke(); }
        catch (Exception ex) { log.Error(ex, "hair-changed listener threw"); }
    }

    /// <summary>
    /// The hairstyle Proteus could make hat-compatible and the mod supplying it, or null for vanilla hair, no walk yet, or
    /// no Penumbra. Reads a volatile snapshot, so safe off the framework thread and possibly one walk stale.
    /// </summary>
    internal HatCompatService.Target? HatCompatTarget()
        => HatCompatService.FindEquippedHair(_humanPartModels, penumbra.ResolvePlayer, modsRoot,
                                             ReadGameOrModFile);

    /// <summary>
    /// A cheap identity for the equipped hairstyle, for deciding whether a re-examination is worth doing.
    /// Resolves a path but reads no model — see <see cref="HatCompatService.EquippedHairKey"/>.
    /// </summary>
    internal string? HatCompatKey()
        => HatCompatService.EquippedHairKey(_humanPartModels, penumbra.ResolvePlayer, modsRoot);

    /// <summary>
    /// Walk the character now and report the model list it draws. Framework thread only. Live because a hairstyle can
    /// change in place without a redraw or composite. Returns only the list: resolving belongs on a worker thread. Does
    /// not publish into the shell builder's caches.
    /// </summary>
    internal IReadOnlyList<string>? HatCompatLiveParts()
    {
        var paths = penumbra.GetActivePlayerModelPaths();
        return paths is { Count: > 0 } ? HumanPartModelsFromModels(paths) : null;
    }

    /// <summary>The hair named by a model list the caller already has, resolved through Penumbra.</summary>
    internal HatCompatService.Target? HatCompatTargetFor(IReadOnlyList<string>? parts)
        => HatCompatService.FindEquippedHair(parts, penumbra.ResolvePlayer, modsRoot,
                                             ReadGameOrModFile);

    /// <summary>
    /// Raw bytes for a game path, from the mod that redirects it or from the game's own data. Shared so both hat-compat
    /// call sites read game data the same way.
    /// </summary>
    private byte[]? ReadGameOrModFile(string gamePath)
        => textureLoader.LoadRawFile(penumbra.ResolvePlayer(gamePath), gamePath);

    /// <summary>That same list's hairstyle identity, without reading the model.</summary>
    internal string? HatCompatKeyFor(IReadOnlyList<string>? parts)
        => HatCompatService.EquippedHairKey(parts, penumbra.ResolvePlayer, modsRoot);

    /// <summary>Until this tick, per mod, an Edited event is the live brush's own reload of that mod.</summary>
    private readonly ConcurrentDictionary<string, long> _ownModEditUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The live brush is about to reload <paramref name="modDir"/> after saving a garment into it, which Penumbra reports
    /// as an edit that looks like a moved base. Marks the coming edit as our own for a moment.
    /// </summary>
    public void ExpectOwnModEdit(string modDir) => _ownModEditUntil[modDir] = Environment.TickCount64 + 2000;

    /// <summary>Until this tick, Glamourer state changes are the echo of a reload Proteus caused.</summary>
    private long _glamourerEchoUntil;

    /// <summary>
    /// Reload the player's gear in place through Glamourer (the live brush's preview) and mark the resulting state change
    /// as our own. Framework thread only. Called explicitly: Glamourer's own queued reapply does not arrive in game.
    /// </summary>
    /// <returns>False when Glamourer is unavailable or refused, so the caller falls back to a redraw.</returns>
    public bool ReloadGearInPlace()
    {
        if (!glamourer.IsAvailable) return false;
        long until = Environment.TickCount64 + 1000;
        Interlocked.Exchange(ref _glamourerEchoUntil, until);
        glamourer.ExpectOwnReapplyUntil(until);
        Interlocked.Exchange(ref _lastOwnReapplyTick, Environment.TickCount64);
        return glamourer.ReapplyPlayerState();
    }

    /// <summary>
    /// Redraw the player so the game re-reads a model file that changed on disk (someone else's mod, e.g. hat-compatible
    /// hair), and do nothing else. The caller has already reloaded the owning mod. Not
    /// <see cref="RestoreChangedAccessory"/>, which also re-renders the whole composite.
    /// </summary>
    public void RedrawForChangedModel()
    {
        Task.Run(() =>
        {
            try
            {
                // Stamped so the compositor recognises the echo as its own doing.
                StampOwnRedraw();
                if (!Plugin.Framework.RunOnFrameworkThread(penumbra.RedrawPlayer).GetAwaiter().GetResult())
                    CancelOwnRedrawEcho();
            }
            catch (Exception ex) { log.Error(ex, "[Proteus] redraw for a changed model failed"); }
        });
    }

    /// <summary>
    /// Restore any accessory whose model the second skin replaced, by forcing a full player redraw so the game reloads the
    /// accessory's own .mdl. Runs off the framework thread; safe to call anytime.
    /// </summary>
    public void RestoreChangedAccessory()
    {
        Task.Run(() =>
        {
            try
            {
                penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
                Thread.Sleep(300);
                StampOwnRedraw();
                if (!Plugin.Framework.RunOnFrameworkThread(penumbra.RedrawPlayer).GetAwaiter().GetResult())
                    CancelOwnRedrawEcho();
                log.Information("[Proteus] restored changed accessory via full redraw");
            }
            catch (Exception ex) { log.Error(ex, "[Proteus] restore changed accessory failed"); }
        });
    }

    /// <param name="userRequested">
    /// The reload is the action the user asked for (e.g. switching Proteus off), so auto redraw does not gate it.
    /// </param>
    private void RefreshPlayerTextures(bool userRequested = false)
    {
        // The chokepoint every self-initiated reload goes through, so the auto-redraw check is enforced here as well as at
        // call sites (which also skip other reload-dependent work).
        if (!config.AutoRedraw && !userRequested)
        {
            log.Debug("[Proteus] Player reload skipped — auto redraw is off.");
            return;
        }

        if (config.UseInPlaceReload && !_needFullRedraw)
        {
            Interlocked.Exchange(ref _lastOwnReapplyTick, Environment.TickCount64);
            // ReapplyState mutates game objects synchronously, so it must run on the framework thread (a background call crashes).
            // Penumbra's RedrawObject queues internally and is safe from any thread.
            bool reapplied;
            try
            {
                reapplied = Plugin.Framework.RunOnFrameworkThread(glamourer.ReapplyPlayerState)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                log.Warning("[Proteus] In-place reload failed on framework thread: {0}", ex.Message);
                reapplied = false;
            }

            if (reapplied)
            {
                log.Debug("[Proteus] Refreshed textures via Glamourer in-place reload.");
                return;
            }
        }

        StampOwnRedraw();
        if (!penumbra.RedrawPlayer())
            CancelOwnRedrawEcho();
    }

    /// <summary>
    /// True when <paramref name="ex"/> (or anything it wraps) is the AssemblyLoadContext-unloading failure of a teardown.
    /// Matched on the message: the CLR surfaces it with no distinguishing type.
    /// </summary>
    private static bool IsLoadContextUnloading(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is AggregateException agg)
                foreach (var inner in agg.InnerExceptions)
                    if (IsLoadContextUnloading(inner)) return true;
            if (e.Message.Contains("AssemblyLoadContext is unloading", StringComparison.OrdinalIgnoreCase)
             || e.Message.Contains("was already unloaded", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>How long to keep polling for the reload to land before redrawing anyway.</summary>
    private static readonly TimeSpan ManifestLiveTimeout = TimeSpan.FromMilliseconds(1500);

    // Reload the managed mod, then poll until Penumbra has processed the new redirects before redrawing (ReloadMod is
    // async, and redrawing early renders the previous composite).
    /// <returns>
    /// Whether the published manifest was observed live. False means unproven, not disproven;
    /// <see cref="VerifyRedirectsLive"/> needs the distinction.
    /// </returns>
    private bool ReloadAndRedrawWhenReady(IDictionary<string, string> redirects,
                                          IDictionary<string, string>? previous)
    {
        var ec = penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
        log.Debug("[Proteus] ReloadMod -> {0}", ec);
        if (!config.AutoRedraw) return false;

        bool live = WaitForManifestLive(redirects, previous);

        RefreshPlayerTextures();
        SchedulePostRedrawBodyTypeCheck();
        return live;
    }

    /// <summary>
    /// Block until Penumbra resolves a path this composite changed to the file we just wrote for it
    /// (<paramref name="previous"/> is the manifest being replaced). Compares full paths (shell output names equal their
    /// game path's filename), and probes a changed entry (content-hashed names make unchanged ones prove nothing).
    /// </summary>
    /// <returns>True only when a probe confirmed the manifest is live; "nothing to wait for" and a timeout return false.</returns>
    private bool WaitForManifestLive(IDictionary<string, string> redirects,
                                     IDictionary<string, string>? previous)
    {
        string? probe = null, expectedFull = null;
        foreach (var (gamePath, rel) in redirects)
        {
            if (previous != null && previous.TryGetValue(gamePath, out var was)
                && string.Equals(was, rel, StringComparison.OrdinalIgnoreCase))
                continue;                         // unchanged — proves nothing
            var full = TryCanonicalise(Path.Combine(managedModDir, rel));
            if (full == null) continue;
            probe = gamePath; expectedFull = full; break;
        }

        // A removed key is a change too: wait until the path stops resolving to the file we dropped.
        string? goneProbe = null, goneFull = null;
        if (probe == null && previous != null)
        {
            foreach (var (gamePath, rel) in previous)
            {
                if (redirects.ContainsKey(gamePath)) continue;
                var full = TryCanonicalise(Path.Combine(managedModDir, rel));
                if (full == null) continue;
                goneProbe = gamePath; goneFull = full; break;
            }
        }

        if (probe == null && goneProbe == null)
        {
            log.Debug("[Proteus] no redirect changed this composite — nothing to wait for");
            return false;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < ManifestLiveTimeout)
        {
            if (probe != null)
            {
                var resolved = TryCanonicalise(penumbra.ResolvePlayer(probe));
                if (resolved != null && string.Equals(resolved, expectedFull, StringComparison.OrdinalIgnoreCase))
                {
                    log.Debug("[Proteus] manifest live after {0}ms", sw.ElapsedMilliseconds);
                    return true;
                }
            }
            else
            {
                // Anything other than the withdrawn file means the removal landed, including null.
                var resolved = TryCanonicalise(penumbra.ResolvePlayer(goneProbe!));
                if (resolved == null || !string.Equals(resolved, goneFull, StringComparison.OrdinalIgnoreCase))
                {
                    log.Debug("[Proteus] withdrawn redirect cleared after {0}ms ({1})",
                        sw.ElapsedMilliseconds, goneProbe!);
                    return true;
                }
            }
            Thread.Sleep(15);
        }

        // Distinct from the success line: the redraw may render the previous composite. One probe is non-null here.
        log.Warning("[Proteus] manifest not live after {0}ms (probe {1}) — redrawing anyway",
                    sw.ElapsedMilliseconds, probe ?? goneProbe!);
        return false;
    }
}
