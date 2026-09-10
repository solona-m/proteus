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
        bool patched = HatCompatService.ReadRecord(target.ModRoot)?.Files
            .Contains(target.Rel, StringComparer.OrdinalIgnoreCase) ?? false;
        if (patched) { current = new View(target, null, Patched: true, Busy: true); return; }

        var proposal = HatCompatService.Inspect(target.Model, target.Rel, target.Head);
        if (proposal == null)
        {
            current = new View(target, Message: Strings.HatCompat.Unreadable, Failed: true, Busy: true);
            return;
        }

        current = new View(target, proposal, Busy: true);
        if (mayApply && config.AutoHatCompat && !proposal.AlreadyCompatible)
            Write(target, proposal, HideList(proposal));
    }

    /// <summary>
    /// The parts to tag, which is the whole proposal or nothing at all.
    /// <para/>
    /// No middle setting, because there is no way for a checkbox to express "these three tails but not that
    /// one" and the classifier is the only thing that has an opinion. The panel offers the individual
    /// tick boxes for a one-off correction; this is what the automatic path uses.
    /// </summary>
    private IReadOnlyList<ModelPart> HideList(HatCompatService.Proposal proposal)
        => config.HatCompatHidePonytails ? proposal.Hide : [];

    /// <summary>Apply what is currently proposed, with the caller's own choice of parts to hide.</summary>
    public void Apply(IReadOnlyList<ModelPart> hide)
    {
        var view = current;
        if (view.Target == null || view.Proposal == null || view.Busy) return;
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        current = view with { Busy = true };
        var target = view.Target;
        var proposal = view.Proposal;
        Task.Run(() =>
        {
            try { Write(target, proposal, hide); }
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

    private void Write(HatCompatService.Target target, HatCompatService.Proposal proposal,
                       IReadOnlyList<ModelPart> hide)
    {
        log.Information("hat compat: fitting {0} — pressing {1} vertices, hiding {2} part(s)",
                        target.Rel, proposal.Solve.Considered, hide.Count);
        var outcome = HatCompatService.Apply(target.ModRoot, target.Model, proposal, hide);
        if (!outcome.Ok)
        {
            log.Warning("hat compat: {0} was not fitted — {1}", target.Rel, outcome.Message);
            current = current with { Message = outcome.Message, Failed = true };
            return;
        }

        // The file just changed underneath the key that was computed from it, so the next poll would see a
        // difference and examine all over again. Re-key here instead.
        lastKey = compositor.HatCompatKey();

        current = new View(target, null, Patched: true, Busy: current.Busy);
        // The game has the old model open under this path and caches by resolved path, so nothing changes
        // on screen until it is made to load the file again.
        compositor.RestoreChangedAccessory();
    }

    /// <summary>Put this hairstyle back the way its author shipped it.</summary>
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
                var outcome = HatCompatService.Revert(target.ModRoot, target.Rel);
                if (!outcome.Ok)
                {
                    current = current with { Message = outcome.Message, Failed = true };
                    return;
                }
                compositor.RestoreChangedAccessory();
                // Look at the restored file rather than guessing what it now needs. Not through Refresh,
                // which would refuse while this examination still holds the flag — and it must NOT be
                // allowed to apply, or undoing would immediately be undone.
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
