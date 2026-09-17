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
/// Watches which hairstyle the player is wearing, works out what it needs to fit under a hat, and (when the
/// setting is on) does it, off the UI thread. Only one examination runs at a time.
/// </summary>
public sealed class HatCompatWatcher : IDisposable
{
    /// <summary>
    /// How long to wait after a Penumbra settings change before looking: the event arrives before Penumbra has
    /// finished re-resolving.
    /// </summary>
    private const int PenumbraSettleMs = 400;

    /// <summary>
    /// How long a burst of signals must go quiet before the draw object is walked, in milliseconds; each
    /// signal resets it.
    /// </summary>
    private const int WalkDebounceMs = 250;

    /// <summary>
    /// How long to wait before walking again after a walk found no hair, in milliseconds; see
    /// <see cref="BlanksBeforeNone"/>.
    /// </summary>
    private const int BlankRetryMs = 500;

    private readonly CompositorService compositor;
    private readonly PenumbraBridge penumbra;
    private readonly GlamourerBridge glamourer;
    private readonly Configuration config;
    private readonly IPluginLog log;

    /// <summary>Guards against overlapping examinations; see the class remarks.</summary>
    private int busy;

    /// <summary>What the last examination was of, so an event that changes nothing costs nothing.</summary>
    private volatile string? lastKey;

    /// <summary>
    /// When the next walk of the draw object is due, as a tick count, or zero for none requested.
    /// </summary>
    private long walkDueAt;

    /// <summary>Whether the feature was on at the last frame, to notice it being switched on.</summary>
    private bool wasEnabled;

    /// <summary>
    /// How many consecutive walks may find no hair before believing it is really gone: a blank walk is almost
    /// always a character mid-rebuild, and believing it would refit a hairstyle the user just undid.
    /// </summary>
    private const int BlanksBeforeNone = 4;

    /// <summary>Consecutive walks that found no hair.</summary>
    private int blanks;

    /// <summary>
    /// The hairstyle the user undid, which the automatic path must not put straight back. Held as mod root and
    /// file (the key includes a timestamp an undo changes); cleared when a different hairstyle is worn.
    /// </summary>
    private volatile string? undone;

    /// <summary>
    /// The model list the last live walk saw, or null before the first walk. A Penumbra settings change reuses
    /// it, since it swaps the file behind a game path, never the path.
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

    /// <summary>
    /// How many files Proteus has patched in the worn hairstyle's mod, for the undo-all button. Kept outside
    /// <see cref="current"/> because it needs a file read the panel must not do per frame.
    /// </summary>
    private volatile int patchedInMod;
    public int PatchedInMod => patchedInMod;

    public HatCompatWatcher(CompositorService compositor, PenumbraBridge penumbra, GlamourerBridge glamourer,
                            Configuration config, IPluginLog log)
    {
        this.compositor = compositor;
        this.penumbra = penumbra;
        this.glamourer = glamourer;
        this.config = config;
        this.log = log;

        // WHICH HAIRSTYLE: a new game path on the draw object, which only a walk can see.
        glamourer.LocalPlayerCustomizeChanged += RequestWalk;
        glamourer.LocalPlayerCustomizationChanged += RequestWalk;
        glamourer.LocalPlayerStateChanged += RequestWalk;
        compositor.HairChanged += RequestWalk;
        penumbra.LocalPlayerRedrawn += RequestWalk;

        // WHICH FILE serves the hairstyle already on: the path list is unchanged, so only re-resolve.
        penumbra.ModSettingChanged += OnModSettingChanged;
        penumbra.PlayerCollectionChanged += OnCollectionChanged;

        // No poll: a per-second walk on the framework thread stalled the game. The frame handler does nothing
        // until a signal asks it to.
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        disposed = true;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        glamourer.LocalPlayerCustomizeChanged -= RequestWalk;
        glamourer.LocalPlayerCustomizationChanged -= RequestWalk;
        glamourer.LocalPlayerStateChanged -= RequestWalk;
        compositor.HairChanged -= RequestWalk;
        penumbra.LocalPlayerRedrawn -= RequestWalk;
        penumbra.ModSettingChanged -= OnModSettingChanged;
        penumbra.PlayerCollectionChanged -= OnCollectionChanged;
    }

    /// <summary>Whether hat compat is doing anything at all right now.</summary>
    private bool Enabled => config.PluginEnabled && config.AutoHatCompat;

    /// <summary>
    /// Ask for the draw object to be walked once things go quiet (see <see cref="WalkDebounceMs"/>). Safe from
    /// any thread; does nothing while the feature is off.
    /// </summary>
    private void RequestWalk() => RequestWalkIn(WalkDebounceMs);

    private void RequestWalkIn(int ms)
    {
        if (disposed || !Enabled) return;
        Interlocked.Exchange(ref walkDueAt, Environment.TickCount64 + ms);
    }

    /// <summary>
    /// The one walk a signal asked for, on the framework thread because the draw object may only be read there.
    /// Resolving and stat'ing happen later in the examination, on a worker.
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed) return;

        // Switching the feature ON is itself a reason to look.
        bool enabled = Enabled;
        if (enabled && !wasEnabled) RequestWalk();
        wasEnabled = enabled;
        if (!enabled) return;

        long due = Interlocked.Read(ref walkDueAt);
        if (due == 0 || Environment.TickCount64 < due) return;

        // An examination is running; leave the request standing for a later frame.
        if (Volatile.Read(ref busy) != 0) return;

        // Claim THIS request only; a newer signal's timestamp stays in place.
        Interlocked.CompareExchange(ref walkDueAt, 0, due);

        IReadOnlyList<string>? parts;
        try { parts = compositor.HatCompatLiveParts(); }
        catch (Exception ex) { log.Error(ex, "hat compat: walking the equipped hairstyle failed"); return; }

        bool hasHair = parts?.Any(p => p.Contains("/obj/hair/", StringComparison.OrdinalIgnoreCase)) == true;
        if (!hasHair)
        {
            // Most likely mid-rebuild — see BlanksBeforeNone. Look again shortly instead of believing it.
            if (++blanks < BlanksBeforeNone) { RequestWalkIn(BlankRetryMs); return; }
        }
        else blanks = 0;

        // The worker must not read the draw object, so hand it the walk.
        livePartsCache = parts;
        Refresh(mayApply: true);
    }

    /// <summary>
    /// Look again at whatever is being worn now. Only <paramref name="mayApply"/> callers (hairstyle changes)
    /// may write by themselves, and only while the setting says so.
    /// </summary>
    /// <param name="delayMs">Wait before looking. For Penumbra's events, which arrive before it has
    /// finished re-resolving.</param>
    /// <param name="force">Examine even if nothing appears to have changed.</param>
    public void Refresh(bool mayApply, int delayMs = 0, bool force = false)
    {
        if (disposed) return;
        // A caller that finds an examination running carries on rather than queueing a second.
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

    // Gated on the feature: each reads a model and runs the solve.
    private void OnModSettingChanged(Penumbra.Api.Enums.ModSettingChange change, Guid collection,
                                     string modDirectory, bool inherited)
    {
        if (Enabled) Refresh(mayApply: true, delayMs: PenumbraSettleMs);
    }

    private void OnCollectionChanged()
    {
        if (Enabled) Refresh(mayApply: true, delayMs: PenumbraSettleMs);
    }

    private void Examine(bool mayApply)
    {
        // Nothing that matters has changed: cheap on purpose, since Penumbra settings changes arrive in bursts.
        var live = livePartsCache;
        var key = live != null ? compositor.HatCompatKeyFor(live) : compositor.HatCompatKey();
        if (key != null && key == lastKey && current.Target != null) return;
        lastKey = key;

        var target = live != null ? compositor.HatCompatTargetFor(live) : compositor.HatCompatTarget();
        if (target == null) { patchedInMod = 0; current = new View(); return; }

        patchedInMod = HatCompatService.PatchedCount(target.ModRoot);

        // Undo holds only until a different hairstyle is worn.
        if (undone != null && undone != Identity(target)) undone = null;

        // Per FILE, not per mod: a hair pack ships many hairstyles from one folder.
        if (HatCompatService.IsPatched(target.ModRoot, target.Rel, out var stale))
        {
            // A stale patch (older solve version) is redone from the author's backup when fitting is automatic.
            if (!(stale && mayApply && config.PluginEnabled && config.AutoHatCompat))
            {
                current = new View(target, null, Patched: true, Busy: true);
                return;
            }

            log.Information("hat compat: {0} was fitted by an older build — fitting it again", target.Rel);
            if (!RevertFiles(target))
            {
                // Still patched with the older patch, which is a working hairstyle, not a failure.
                current = new View(target, null, Patched: true, Busy: true,
                                   Message: current.Message, Failed: current.Failed);
                return;
            }

            // Re-read from the restored backup: solving the patched bytes would press twice.
            target = live != null ? compositor.HatCompatTargetFor(live) : compositor.HatCompatTarget();
            if (target == null) { current = new View(); return; }
            lastKey = live != null ? compositor.HatCompatKeyFor(live) : compositor.HatCompatKey();
        }

        // The race code from the hair's game path picks which baked hat profile the cut measures against.
        var raceCode = ContentSlot.Parse(target.GamePath)?.RaceCode;
        var proposal = HatCompatService.Inspect(target.Model, target.Rel, target.Head, raceCode);
        if (proposal == null)
        {
            current = new View(target, Message: Strings.HatCompat.Unreadable, Failed: true, Busy: true);
            return;
        }

        // Nothing to fit against: reported ahead of the undo check, since it is about the wearer. Logged at
        // Warning so "never found your head" is distinguishable from "hair was fine".
        if (proposal.Unmeasurable)
        {
            log.Warning("hat compat: {0} left alone — the wearer's face model could not be read, so there "
                      + "is no skull to measure a hat line against", target.Rel);
            current = new View(target, proposal, Message: Strings.HatCompat.NoHead, Busy: true);
            return;
        }

        current = new View(target, proposal, Busy: true);

        // The user's undo outranks the setting, or undo and auto-fit would fight.
        if (undone == Identity(target)) return;

        // Checked again here: a button press reaches this without the signal gates, and the setting may have
        // changed during a delay.
        if (mayApply && config.PluginEnabled && config.AutoHatCompat && !proposal.AlreadyCompatible)
            Write(target, proposal, automatic: true);
    }

    /// <summary>Which hairstyle this is, independent of anything an edit to it would change.</summary>
    private static string Identity(HatCompatService.Target target) => $"{target.ModRoot}|{target.Rel}";

    /// <summary>Apply what is currently proposed.</summary>
    public void Apply()
    {
        var view = current;
        if (view.Target == null || view.Proposal == null || view.Busy) return;
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        undone = null;                        // asked for it explicitly, so the undo no longer stands
        current = view with { Busy = true };
        var target = view.Target;
        var proposal = view.Proposal;
        Task.Run(() =>
        {
            // automatic: false, since the user pressed the button; no chat notice.
            try { Write(target, proposal, automatic: false); }
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

    /// <param name="automatic">The fit was Proteus's own idea rather than a button press, so it announces
    /// itself in chat. See <see cref="Announce"/>.</param>
    private void Write(HatCompatService.Target target, HatCompatService.Proposal proposal, bool automatic)
    {
        // Log where it thinks the head is: every cut decision is measured from that centre.
        log.Information("hat compat: head centre y={0:F4} r={1:F4} ({2}), hat line y={3:F4}, "
                      + "press fades out by y={4:F4}",
                        proposal.Solve.Centre.Y, proposal.Solve.Radius,
                        "from the face model",
                        proposal.Solve.Centre.Y + HatCompatSolve.HatLine,
                        proposal.Solve.Centre.Y + HatCompatSolve.HatLine - HatCompatSolve.FanBelow);

        if (proposal.TookOver)
            log.Information("hat compat: {0} arrived with its own hat support that hides hair no hat "
                          + "covers — replacing its atr_kam mask and leaving its shape alone", target.Rel);

        log.Information("hat compat: fitting {0} — pressing {1} vertices, cutting {2} piece(s){3}",
                        target.Rel, proposal.Solve.Considered, proposal.Solve.Cut.Count,
                        // Only when it happened; which ceiling, and wanted against available.
                        proposal.Solve.Dropped > 0
                            ? $", {proposal.Solve.Dropped} left unpressed "
                            + $"({proposal.Solve.DroppedForValues} over the file's shape-value budget, "
                            + $"{proposal.Solve.DroppedForSpares} over a mesh's spare-vertex ceiling; "
                            + $"wanted {proposal.Solve.WantedValues} values of {proposal.Solve.Budget})"
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

        // Announce only after a successful write.
        if (automatic) Announce();

        // The same hairstyle from every other option that supplies it, each with its own solve.
        var worn = HatCompatService.Rel(target.Rel);
        foreach (var rel in FilesFor(target))
        {
            // Both sides canonicalised: the manifest spells paths with backslashes.
            if (HatCompatService.Rel(rel).Equals(worn, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(
                    target.ModRoot, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                if (HatCompatService.Inspect(bytes, rel, target.Head,
                        ContentSlot.Parse(target.GamePath)?.RaceCode)
                    is not { AlreadyCompatible: false } sib)
                    continue;
                var sibOutcome = HatCompatService.Apply(target.ModRoot, bytes, sib);
                log.Information("hat compat: sibling {0} — {1}", rel,
                                sibOutcome.Ok ? "fitted" : sibOutcome.Message);
            }
            catch (Exception ex) { log.Warning(ex, "hat compat: sibling {0} could not be fitted", rel); }
        }

        // The file just changed under the key computed from it, so re-key here.
        lastKey = compositor.HatCompatKey();

        // Re-counted: this path ends without re-examining.
        patchedInMod = HatCompatService.PatchedCount(target.ModRoot);

        current = new View(target, null, Patched: true, Busy: current.Busy);

        // Make Penumbra re-read the mod BEFORE forcing the redraw: the game caches a loaded model per resolved
        // path, so a redraw alone gets the old bytes.
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
    /// Say in chat that Proteus has fitted the hairstyle just put on, and where to undo it or switch the feature
    /// off. Once per file: an already-patched hairstyle never reaches here.
    /// </summary>
    private void Announce()
    {
        // Onto the framework thread for chat. The catch is INSIDE the lambda, since the returned task (which
        // would capture the exception) is discarded.
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                // The plugin may have been torn down before this frame came.
                if (disposed) return;
                Plugin.ChatGui.Print(new Dalamud.Game.Text.SeStringHandling.SeStringBuilder()
                    .AddUiForeground(
                        CheapLoc.Loc.Localize("Chat.HatCompat",
                            "[Proteus] Proteus has made this hairstyle fit under hats. You can undo that, or "
                          + "turn the feature off, under /proteus > Settings > Hats."), 45)
                    .Build());
            }
            catch (Exception ex) { log.Warning(ex, "hat compat: could not announce the fit in chat"); }
        });
    }

    /// <summary>
    /// Every file in the mod that serves the hairstyle being worn: the unit of work for both apply and undo, so
    /// no sibling keeps an unreachable patch.
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
            // A never-touched sibling reports "nothing to undo", which is not a failure; kept only to explain
            // a revert that restored nothing.
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
    /// Undo every hat-compat edit Proteus has made ANYWHERE in this mod per its record, including other race
    /// variants. Runs on a thread that already holds the busy flag.
    /// </summary>
    /// <returns>False if nothing could be restored, with the reason already on <see cref="Current"/>.</returns>
    private bool RevertWholeMod(HatCompatService.Target target)
    {
        var outcome = HatCompatService.Revert(target.ModRoot, only: null);
        if (!outcome.Ok)
        {
            current = current with { Message = outcome.Message, Failed = outcome.Message.Length > 0 };
            return false;
        }

        log.Information("hat compat: restored every patched file ({0}) in {1}",
                        outcome.FilesPatched, target.ModRoot);
        compositor.RedrawForChangedModel();
        return true;
    }

    /// <summary>Put this hairstyle back the way its author shipped it.</summary>
    public void Revert() => RunRevert(RevertFiles);

    /// <summary>
    /// Put every hairstyle in this mod back the way its author shipped it — including the race variants
    /// the wearer is not currently wearing. See <see cref="RevertWholeMod"/>.
    /// </summary>
    public void RevertAll() => RunRevert(RevertWholeMod);

    /// <summary>
    /// The shared body of both undos: take the flag, restore off-thread, then look at what is left.
    /// </summary>
    private void RunRevert(Func<HatCompatService.Target, bool> restore)
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
                if (!restore(target)) return;

                // Remembered here, not in the restore: a stale-patch refit also reverts, and must not stick.
                undone = Identity(target);

                // Examine directly (Refresh would refuse while this holds the flag), and never with mayApply,
                // or the undo would undo itself.
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
