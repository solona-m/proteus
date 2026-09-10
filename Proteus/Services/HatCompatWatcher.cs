using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Proteus.Interop;
using Proteus.Localization;

namespace Proteus.Services;

/// <summary>
/// Watches which hairstyle the player is wearing, works out what it needs to fit under a hat, and — when
/// the setting is on — does it.
/// <para/>
/// A service rather than part of the panel, and that is the point of it. The examination is triggered by a
/// hairstyle change, which happens whether or not anyone is looking at the settings window, and the work
/// itself reads a multi-megabyte model and writes into a mod folder, which must not happen on the thread
/// that is drawing the UI. The panel is a view over <see cref="Current"/> and nothing more.
/// <para/>
/// Only ever one examination at a time: changing hairstyle in Glamourer fires several redraws in quick
/// succession, and each one would otherwise start its own solve over the same model.
/// </summary>
public sealed class HatCompatWatcher : IDisposable
{
    /// <summary>
    /// How long to wait before looking, after Penumbra reports a settings change.
    /// <para/>
    /// The event arrives before Penumbra has finished re-resolving, so asking immediately gets the file
    /// that was serving the hairstyle a moment ago. Long enough to be past that, short enough that the
    /// panel still feels like it is keeping up.
    /// </summary>
    private const int PenumbraSettleMs = 400;

    /// <summary>
    /// How often the poll asks what hairstyle is on, in milliseconds.
    /// <para/>
    /// The question costs one Penumbra path resolve and one file stat, so a second is generous. It is the
    /// backstop rather than the primary signal — the events fire first when they fire at all — so this
    /// only has to be quick enough that nobody is left wondering whether it noticed.
    /// </summary>
    private const int PollMs = 1000;

    private readonly CompositorService compositor;
    private readonly PenumbraBridge penumbra;
    private readonly Configuration config;
    private readonly IPluginLog log;

    /// <summary>Guards against overlapping examinations; see the class remarks.</summary>
    private int busy;

    /// <summary>What the last examination was of, so an event that changes nothing costs nothing.</summary>
    private volatile string? lastKey;

    /// <summary>When the poll may next ask, as a tick count.</summary>
    private long nextPoll;

    /// <summary>How many polls between heartbeat log lines — see <see cref="OnFrameworkUpdate"/>.</summary>
    private const int HeartbeatPolls = 15;

    /// <summary>Polls remaining before the next heartbeat. Starts at 1 so the first poll reports.</summary>
    private int beats = 1;

    /// <summary>
    /// The model list the last live walk saw, for the examination to work from.
    /// <para/>
    /// Null when the examination was triggered by an event rather than the poll, in which case there is no
    /// live walk to use and the compositor's own cache is the best available answer.
    /// </summary>
    private volatile IReadOnlyList<string>? livePartsCache;

    private volatile bool disposed;

    /// <summary>Everything the panel draws, replaced wholesale so a reader never sees half an update.</summary>
    /// <param name="Target">The hair being examined, or null for vanilla hair or none.</param>
    /// <param name="Proposal">What Proteus would do to it, or null while it has not looked yet.</param>
    /// <param name="Patched">This exact file already carries a Proteus patch, so it can be undone.</param>
    /// <param name="Busy">An examination or a write is in flight.</param>
    public sealed record View(
        HatCompatService.Target? Target = null,
        HatCompatService.Proposal? Proposal = null,
        bool Patched = false,
        bool Busy = false,
        string Message = "",
        bool Failed = false);

    /// <summary>The current state. Volatile: written from a worker, read from the UI thread every frame.</summary>
    private volatile View current = new();
    public View Current => current;

    public HatCompatWatcher(CompositorService compositor, PenumbraBridge penumbra, Configuration config,
                            IPluginLog log)
    {
        this.compositor = compositor;
        this.penumbra = penumbra;
        this.config = config;
        this.log = log;

        // Three ways the hairstyle in front of you can become a different one, and they are genuinely
        // different events. Glamourer changes WHICH hairstyle, which redraws — that is HairChanged.
        // Penumbra changes WHICH FILE serves the one you already have on, and redraws nothing this can
        // see. A collection switch can do either. Subscribing to the redraw alone is why right-clicking a
        // hair mod in Penumbra left the panel describing the mod that had just been switched away from.
        compositor.HairChanged += OnHairChanged;
        penumbra.ModSettingChanged += OnModSettingChanged;
        penumbra.PlayerCollectionChanged += OnCollectionChanged;
        penumbra.LocalPlayerRedrawn += OnRedrawn;

        // And a POLL behind all of them, which is what actually makes this reliable. Two rounds of
        // subscribing to the event that ought to fire both missed the case that prompted them — swapping
        // which mod serves a hairstyle from Penumbra's own list — and the events above are a guess about
        // somebody else's plumbing that will go on being a guess as that plumbing changes. Asking the
        // question directly cannot miss: resolve one path, stat one file, compare a string. That is cheap
        // enough to do every second forever, and it also covers a file edited on disk by hand, which no
        // event will ever report.
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        disposed = true;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        compositor.HairChanged -= OnHairChanged;
        penumbra.ModSettingChanged -= OnModSettingChanged;
        penumbra.PlayerCollectionChanged -= OnCollectionChanged;
        penumbra.LocalPlayerRedrawn -= OnRedrawn;
    }

    /// <summary>
    /// The poll. Runs on the framework thread, where resolving a path through Penumbra is safe, and does
    /// nothing at all unless the answer has changed.
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed || Volatile.Read(ref busy) != 0) return;
        var now = Environment.TickCount64;
        if (now < nextPoll) return;
        nextPoll = now + PollMs;

        string? key;
        IReadOnlyList<string>? parts;
        try { (key, parts) = compositor.HatCompatWalkLive(); }
        catch (Exception ex) { log.Error(ex, "hat compat: polling the equipped hairstyle failed"); return; }

        // A heartbeat, so that "nothing happened" can be told apart from "nothing is running". Without it
        // a silent poll and a poll that finds no hair look identical in the log, and they were confused
        // for each other once already.
        if (--beats <= 0)
        {
            beats = HeartbeatPolls;
            log.Debug("hat compat: watching — key={0}  {1}", key ?? "(null)", compositor.HatCompatDiag());
        }

        if (key == lastKey) return;

        log.Information("hat compat: hairstyle changed ({0} -> {1})", lastKey ?? "(none)", key ?? "(none)");
        // Hand the walk itself across. The examination runs on a worker, where the draw object must not be
        // read, so it cannot go and fetch this for itself — and re-reading the cache there would put back
        // exactly the staleness this poll exists to get around.
        livePartsCache = parts;
        Refresh(mayApply: true);
    }

    /// <summary>
    /// Look again at whatever is being worn now.
    /// <para/>
    /// <paramref name="mayApply"/> is what separates a hairstyle change from the user pressing a button:
    /// only the former is allowed to write by itself, and only while the setting says so.
    /// </summary>
    /// <param name="delayMs">Wait before looking. For Penumbra's events, which arrive before it has
    /// finished re-resolving.</param>
    /// <param name="force">Examine even if nothing appears to have changed — for a button press, where
    /// the user is entitled to an answer whatever the cache thinks.</param>
    public void Refresh(bool mayApply, int delayMs = 0, bool force = false)
    {
        if (disposed) return;
        // Interlocked rather than a lock: a caller that finds an examination already running should carry
        // on, not queue a second one behind it. The one in flight is looking at the same character.
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        current = current with { Busy = true };
        Task.Run(() =>
        {
            try
            {
                if (delayMs > 0) Thread.Sleep(delayMs);
                if (force) lastKey = null;
                Examine(mayApply);
            }
            catch (Exception ex)
            {
                log.Error(ex, "hat compat: examining the equipped hairstyle failed");
                current = new View(Message: Strings.HatCompat.Unreadable, Failed: true);
            }
            finally
            {
                Interlocked.Exchange(ref busy, 0);
                current = current with { Busy = false };
            }
        });
    }

    private void OnHairChanged() => Refresh(mayApply: true);

    private void OnModSettingChanged(Penumbra.Api.Enums.ModSettingChange change, Guid collection,
                                     string modDirectory, bool inherited)
        => Refresh(mayApply: true, delayMs: PenumbraSettleMs);

    private void OnCollectionChanged() => Refresh(mayApply: true, delayMs: PenumbraSettleMs);

    private void OnRedrawn() => Refresh(mayApply: true, delayMs: PenumbraSettleMs);

    private void Examine(bool mayApply)
    {
        // Nothing that matters has changed, so there is nothing to do. Cheap on purpose: this runs on
        // every Penumbra settings change, and those arrive in bursts for reasons that have nothing to do
        // with hair. Reading a multi-megabyte model to conclude "same as last time" would make every
        // checkbox in Penumbra feel slow.
        var live = livePartsCache;
        var key = live != null ? compositor.HatCompatKeyFor(live) : compositor.HatCompatKey();
        if (key != null && key == lastKey && current.Target != null) return;
        lastKey = key;

        var target = live != null ? compositor.HatCompatTargetFor(live) : compositor.HatCompatTarget();
        if (target == null) { current = new View(); return; }

        // Per FILE, not per mod: a hair pack ships a dozen hairstyles out of one folder, and a mod-level
        // test would report every one of them as already done the moment any one was.
        if (HatCompatService.IsPatched(target.ModRoot, target.Rel, out var stale))
        {
            // A patch is a one-time write, so this is the only moment anything ever looks at a hairstyle
            // that already carries one — and until now it looked, saw a patch, and stopped. Every
            // improvement to the press since therefore reached only hairstyles that had never been worn,
            // and a hairstyle fitted by an older Proteus kept that older Proteus's idea of where a hat sits
            // for good. Redo it from the author's own backup, which is what the setting already promises:
            // it says Proteus checks each hairstyle as you put it on, not each hairstyle once ever.
            if (!(stale && mayApply && config.AutoHatCompat))
            {
                current = new View(target, null, Patched: true, Busy: true);
                return;
            }

            log.Information("hat compat: {0} was fitted by an older build — fitting it again", target.Rel);
            if (!RevertFiles(target))
            {
                // Still patched, just with the older patch — which is a working hairstyle, not a failure to
                // report loudly. Keep whatever the revert had to say about why.
                current = new View(target, null, Patched: true, Busy: true,
                                   Message: current.Message, Failed: current.Failed);
                return;
            }

            // Re-read: the bytes in hand are the patched ones, and solving from those would press an
            // already-pressed model. The backup is on disk now, so ask for the target afresh.
            target = live != null ? compositor.HatCompatTargetFor(live) : compositor.HatCompatTarget();
            if (target == null) { current = new View(); return; }
            lastKey = live != null ? compositor.HatCompatKeyFor(live) : compositor.HatCompatKey();
        }

        var proposal = HatCompatService.Inspect(target.Model, target.Rel, target.Head);
        if (proposal == null)
        {
            current = new View(target, Message: Strings.HatCompat.Unreadable, Failed: true, Busy: true);
            return;
        }

        current = new View(target, proposal, Busy: true);
        if (mayApply && config.AutoHatCompat && !proposal.AlreadyCompatible)
            Write(target, proposal);
    }

    /// <summary>Apply what is currently proposed.</summary>
    public void Apply()
    {
        var view = current;
        if (view.Target == null || view.Proposal == null || view.Busy) return;
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        current = view with { Busy = true };
        var target = view.Target;
        var proposal = view.Proposal;
        Task.Run(() =>
        {
            try { Write(target, proposal); }
            catch (Exception ex)
            {
                log.Error(ex, "hat compat: applying to {0} failed", target.Rel);
                current = current with { Message = ex.Message, Failed = true };
            }
            finally
            {
                Interlocked.Exchange(ref busy, 0);
                current = current with { Busy = false };
            }
        });
    }

    private void Write(HatCompatService.Target target, HatCompatService.Proposal proposal)
    {
        log.Information("hat compat: fitting {0} — pressing {1} vertices, cutting {2} piece(s){3}",
                        target.Rel, proposal.Solve.Considered, proposal.Solve.Cut.Count,
                        // Only when it happened. A shape that had to be cut short leaves hair standing
                        // exactly where the budget ran out, which looks identical to the press deciding that
                        // hair was fine — and telling those apart from a screenshot alone is impossible.
                        proposal.Solve.Dropped > 0
                            ? $", {proposal.Solve.Dropped} left unpressed for want of shape budget"
                            : "");
        var outcome = HatCompatService.Apply(target.ModRoot, target.Model, proposal);
        if (!outcome.Ok)
        {
            log.Warning("hat compat: {0} was not fitted — {1}", target.Rel, outcome.Message);
            current = current with { Message = outcome.Message, Failed = true };
            return;
        }
        if (outcome.Unaddressable > 0)
            log.Warning("hat compat: {0} — {1} vertices could not be given a shape value at all; this hair is "
                      + "welded into meshes too large for the format to address", target.Rel,
                        outcome.Unaddressable);

        // The same hairstyle again, from every other option that supplies it. Each gets its own solve —
        // a long version and a short one are different geometry and the tails are not in the same places.
        foreach (var rel in FilesFor(target))
        {
            if (rel.Equals(target.Rel, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(
                    target.ModRoot, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                if (HatCompatService.Inspect(bytes, rel, target.Head) is not { AlreadyCompatible: false } sib)
                    continue;
                var sibOutcome = HatCompatService.Apply(target.ModRoot, bytes, sib);
                log.Information("hat compat: sibling {0} — {1}", rel,
                                sibOutcome.Ok ? "fitted" : sibOutcome.Message);
            }
            catch (Exception ex) { log.Warning(ex, "hat compat: sibling {0} could not be fitted", rel); }
        }

        // The file just changed underneath the key that was computed from it, so the next poll would see a
        // difference and examine all over again. Re-key here instead.
        lastKey = compositor.HatCompatKey();

        current = new View(target, null, Patched: true, Busy: current.Busy);

        // Make Penumbra re-read the mod BEFORE forcing the redraw, and in that order.
        //
        // The file was overwritten in place, which is the one case the game handles worst: it keeps the
        // model it already loaded for a resolved path, so a redraw on its own re-resolves the path and is
        // handed back the very bytes that were there before. That is how a hairstyle could come back
        // correctly pressed — from an earlier apply that did get loaded — while the tags written moments
        // ago were nowhere, which looks exactly like the tagging having failed.
        var modDir = System.IO.Path.GetFileName(target.ModRoot.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        if (modDir.Length > 0)
        {
            var ec = penumbra.ReloadModDirectory(modDir);
            log.Information("hat compat: asked Penumbra to reload {0} — {1}", modDir, ec);
        }
        compositor.RedrawForChangedModel();
    }

    /// <summary>
    /// Every file in the mod that serves the hairstyle being worn.
    /// <para/>
    /// The unit of work for BOTH directions, and it has to be — an apply that writes four files and an undo
    /// that restores one leaves three carrying a patch nobody can see or reach. Worse, they then read as
    /// already hat-compatible, so the next apply skips them and they keep the old patch for good. That was
    /// exactly the shape of "I clicked undo and it still will not fit".
    /// </summary>
    private static List<string> FilesFor(HatCompatService.Target target)
        => HatCompatService.SiblingFiles(target.ModRoot, target.GamePath, target.Rel);

    /// <summary>
    /// Put every file serving this hairstyle back the way its author shipped it. Runs on a thread that
    /// already holds the busy flag.
    /// </summary>
    /// <returns>False if nothing could be restored, with the reason already on <see cref="Current"/>.</returns>
    private bool RevertFiles(HatCompatService.Target target)
    {
        var restored = 0;
        var reason = "";
        foreach (var rel in FilesFor(target))
        {
            var outcome = HatCompatService.Revert(target.ModRoot, rel);
            if (outcome.Ok) { restored += outcome.FilesPatched; continue; }
            // Only the file actually being worn was necessarily patched. A sibling that was never touched
            // reports "nothing to undo", which is the right answer and not a failure — so this is kept
            // only to explain a revert that restored nothing at all.
            reason = outcome.Message;
        }

        if (restored == 0)
        {
            current = current with { Message = reason, Failed = reason.Length > 0 };
            return false;
        }

        log.Information("hat compat: restored {0} file(s) in {1}", restored, target.ModRoot);
        compositor.RedrawForChangedModel();
        return true;
    }

    /// <summary>
    /// Put this hairstyle back the way its author shipped it.
    /// <para/>
    /// Undo only. There used to be a redo-with-the-new-settings twin of this, for a "hide ponytails" switch
    /// that no longer exists; a patch made stale by a newer Proteus is now redone by <see cref="Examine"/>
    /// on its own, which is the case that twin was really covering.
    /// </summary>
    public void Revert()
    {
        var view = current;
        if (view.Target == null || view.Busy) return;
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        current = view with { Busy = true };
        var target = view.Target;
        Task.Run(() =>
        {
            try
            {
                if (!RevertFiles(target)) return;

                // Look at the restored file rather than guessing what it now needs. Not through Refresh,
                // which would refuse while this examination still holds the flag — and never with
                // mayApply, or an undo would immediately undo itself.
                lastKey = null;
                Examine(mayApply: false);
            }
            catch (Exception ex)
            {
                log.Error(ex, "hat compat: reverting {0} failed", target.ModRoot);
                current = current with { Message = ex.Message, Failed = true };
            }
            finally
            {
                Interlocked.Exchange(ref busy, 0);
                current = current with { Busy = false };
            }
        });
    }
}
