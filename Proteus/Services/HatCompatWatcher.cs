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
    /// How long a burst of signals must go quiet before the draw object is walked, in milliseconds.
    /// <para/>
    /// Glamourer reports every customize value separately, and dragging a colour slider sends a stream of
    /// them; applying a design sends a burst. Each one resets this, so a burst costs one walk at its end
    /// rather than one per signal. Short enough that a hairstyle change still looks immediate.
    /// </summary>
    private const int WalkDebounceMs = 250;

    /// <summary>
    /// How long to wait before walking again after a walk found no hair, in milliseconds.
    /// <para/>
    /// Most walks follow a redraw, often our own, and for a frame or two while the character rebuilds the
    /// draw object carries no hair at all. That is not the hair coming off; see <see cref="BlanksBeforeNone"/>.
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
    /// When the next walk of the draw object is due, as a tick count — or zero for none requested.
    /// <para/>
    /// Set by the signals and cleared by the walk. The framework thread reads a long and returns on every
    /// frame nothing has asked for, which is essentially every frame.
    /// </summary>
    private long walkDueAt;

    /// <summary>Whether the feature was on at the last frame, to notice it being switched on.</summary>
    private bool wasEnabled;

    /// <summary>
    /// How many consecutive walks may find no hair before believing it is really gone.
    /// <para/>
    /// A blank walk is almost always a character mid-rebuild, and taken at face value it clears the
    /// comparison — so the ordinary reading that follows looks like a brand-new hairstyle and is fitted all
    /// over again. Undo was the visible casualty: it restored the file, its redraw blanked the walk, and the
    /// automatic path put the patch straight back. A few retries ride out the rebuild; genuinely taking the
    /// hair off still registers a couple of seconds later.
    /// </summary>
    private const int BlanksBeforeNone = 4;

    /// <summary>Consecutive walks that found no hair.</summary>
    private int blanks;

    /// <summary>
    /// The hairstyle the user undid, which the automatic path must not put straight back.
    /// <para/>
    /// Held as mod root and file, not as the examination's key, because the key includes the file's timestamp and
    /// an undo is precisely the thing that changes it. Cleared as soon as a DIFFERENT hairstyle is worn, so
    /// undo means "leave this one alone" rather than "stop fitting anything".
    /// </summary>
    private volatile string? undone;

    /// <summary>
    /// The model list the last live walk saw, for the examination to work from.
    /// <para/>
    /// Null until the first walk, in which case the compositor's own cache is the best available answer. A
    /// Penumbra settings change deliberately reuses the last walk rather than taking a new one: it swaps the
    /// FILE behind a game path, never the path itself, so the list is still right.
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
    /// How many files Proteus has patched in the worn hairstyle's mod — what the panel's undo-all is
    /// offered for and labelled with.
    /// <para/>
    /// Beside <see cref="current"/> rather than inside it, because it is answered by a file read and the
    /// panel must not do that once a frame. It can lag the view by an instant, which costs a button label
    /// nothing.
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

        // Two genuinely different ways the hairstyle in front of you can change, and they need different
        // responses.
        //
        // WHICH HAIRSTYLE — a new game path on the draw object. Glamourer does this, sometimes with a redraw
        // and sometimes entirely in place, and only a walk of the draw object can say what is on now. These
        // request a walk.
        glamourer.LocalPlayerCustomizeChanged += RequestWalk;
        glamourer.LocalPlayerCustomizationChanged += RequestWalk;
        glamourer.LocalPlayerStateChanged += RequestWalk;
        compositor.HairChanged += RequestWalk;
        penumbra.LocalPlayerRedrawn += RequestWalk;

        // WHICH FILE serves the hairstyle already on — Penumbra swapping the mod behind the same game path.
        // The path list does not change, so no walk: the last one is still right, and only the resolution
        // needs asking again, which happens off this thread.
        penumbra.ModSettingChanged += OnModSettingChanged;
        penumbra.PlayerCollectionChanged += OnCollectionChanged;

        // NOT a poll. There used to be one here, walking the draw object every second on the framework thread
        // on the reasoning that asking directly cannot miss. It could not miss, and it also stalled the game
        // for up to 1.4 s at a time: the walk asks Penumbra for every resource on the character, and it then
        // resolved a path and stat'ed a file on that same thread. It ran for every user whether or not they
        // had ever turned the feature on. The case it was really covering — a hairstyle changed in place,
        // with no redraw — now has its own signal above, which is why it can go. What it genuinely covered
        // that nothing else does is a hair model edited by hand on disk while being worn; that is left to
        // the next hairstyle change to notice.
        //
        // The frame handler below does nothing until a signal has asked it to.
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
    /// Ask for the draw object to be walked once things go quiet — see <see cref="WalkDebounceMs"/>.
    /// <para/>
    /// Safe from any thread: it only moves a timestamp. Each call pushes the walk back, so a burst of
    /// signals costs one walk. Does nothing while the feature is off, which is how the default config
    /// ends up paying nothing.
    /// </summary>
    private void RequestWalk() => RequestWalkIn(WalkDebounceMs);

    private void RequestWalkIn(int ms)
    {
        if (disposed || !Enabled) return;
        Interlocked.Exchange(ref walkDueAt, Environment.TickCount64 + ms);
    }

    /// <summary>
    /// The one walk a signal asked for, on the framework thread because the draw object may only be read
    /// there. On every other frame — which is nearly all of them — it reads a flag and a long and returns.
    /// <para/>
    /// Only the walk happens here. Turning its model list into a hairstyle identity means a Penumbra resolve
    /// and a file stat, and both happen in the examination on a worker, which already compares against the
    /// last identity and does nothing if it has not changed.
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed) return;

        // Proteus switched off, or the feature off — which is the default — means nothing runs. Switching it
        // ON is itself a reason to look, since whatever is being worn has never been examined.
        bool enabled = Enabled;
        if (enabled && !wasEnabled) RequestWalk();
        wasEnabled = enabled;
        if (!enabled) return;

        long due = Interlocked.Read(ref walkDueAt);
        if (due == 0 || Environment.TickCount64 < due) return;

        // An examination is running; leave the request standing and look on a later frame, rather than
        // walking now and having the result thrown away because Refresh refuses to overlap.
        if (Volatile.Read(ref busy) != 0) return;

        // Claim THIS request only. A signal that arrived while we were deciding moved the timestamp, and the
        // compare leaves that newer request in place rather than wiping it.
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

        // Hand the walk across. The examination runs on a worker, where the draw object must not be read, so
        // it cannot fetch this for itself — and re-reading the compositor's cache there would put back
        // exactly the staleness the walk exists to get around.
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

    // Gated like everything else. These are off the framework thread, so they never hitched, but each one
    // reads a multi-megabyte model and runs the solve — on every Penumbra settings change, for every user,
    // including the ones who have never switched the feature on.
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
        // Nothing that matters has changed, so there is nothing to do. Cheap on purpose: this runs on
        // every Penumbra settings change, and those arrive in bursts for reasons that have nothing to do
        // with hair. Reading a multi-megabyte model to conclude "same as last time" would make every
        // checkbox in Penumbra feel slow.
        var live = livePartsCache;
        var key = live != null ? compositor.HatCompatKeyFor(live) : compositor.HatCompatKey();
        if (key != null && key == lastKey && current.Target != null) return;
        lastKey = key;

        var target = live != null ? compositor.HatCompatTargetFor(live) : compositor.HatCompatTarget();
        if (target == null) { patchedInMod = 0; current = new View(); return; }

        patchedInMod = HatCompatService.PatchedCount(target.ModRoot);

        // Undo holds only until a different hairstyle is worn.
        if (undone != null && undone != Identity(target)) undone = null;

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
            if (!(stale && mayApply && config.PluginEnabled && config.AutoHatCompat))
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

        // The race code comes off the hair's own game path — chara/human/c0801/obj/hair/... — which is
        // where ContentSlot already reads it from. It picks which baked hat profile the cut measures
        // against, and every race's head is a different size and shape.
        var raceCode = ContentSlot.Parse(target.GamePath)?.RaceCode;
        var proposal = HatCompatService.Inspect(target.Model, target.Rel, target.Head, raceCode);
        if (proposal == null)
        {
            current = new View(target, Message: Strings.HatCompat.Unreadable, Failed: true, Busy: true);
            return;
        }

        // Nothing to fit against, so say so and stop — ahead of the undo check, because this is a fact
        // about the wearer rather than about this hairstyle and the user is entitled to it either way.
        // Logged at Warning and naming the file: it is the one line separating "Proteus looked and decided
        // the hair was fine" from "Proteus never found your head", and from outside those are one silence.
        if (proposal.Unmeasurable)
        {
            log.Warning("hat compat: {0} left alone — the wearer's face model could not be read, so there "
                      + "is no skull to measure a hat line against", target.Rel);
            current = new View(target, proposal, Message: Strings.HatCompat.NoHead, Busy: true);
            return;
        }

        current = new View(target, proposal, Busy: true);

        // The user's undo outranks the setting. Automatic fitting means "fit each hairstyle as you put it
        // on", not "overrule anyone who says no" — and without this the two fight: undo restores the file,
        // the file looks unfitted, and the next examination fits it again within the second.
        if (undone == Identity(target)) return;

        // Checked again here, not only where the signals arrive: a button press reaches this through Refresh
        // without passing any of those gates, and the setting may have changed while a delayed examination
        // was waiting.
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
            // automatic: false — the user pressed the button, so the "Proteus has done this to your hair"
            // notice would be telling them something they just did. The panel they pressed it in already
            // says where the backup goes and that it can be undone.
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
        // WHERE IT THINKS THE HEAD IS, every time. The hat line is an offset from that centre and every
        // other decision is measured from it, so a cut landing somewhere absurd is either a bad centre or
        // is not the cut at all — and those two look identical in a screenshot. Reading it out of the log
        // settles in one line what has otherwise taken a build apiece to guess at. The source matters too:
        // a wearer whose face is vanilla has no model to read, and the skull is guessed from the hair.
        log.Information("hat compat: head centre y={0:F4} r={1:F4} ({2}), hat line y={3:F4}, "
                      + "press fades out by y={4:F4}",
                        proposal.Solve.Centre.Y, proposal.Solve.Radius,
                        // Always the face model now — a fit is refused outright without one, and the face
                        // is read from the game's own data when no mod supplies one. Kept in the line
                        // because it is the assertion that proves both, not because it can vary.
                        "from the face model",
                        proposal.Solve.Centre.Y + HatCompatSolve.HatLine,
                        proposal.Solve.Centre.Y + HatCompatSolve.HatLine - HatCompatSolve.FanBelow);

        if (proposal.TookOver)
            log.Information("hat compat: {0} arrived with its own hat support that hides hair no hat "
                          + "covers — replacing its atr_kam mask and leaving its shape alone", target.Rel);

        log.Information("hat compat: fitting {0} — pressing {1} vertices, cutting {2} piece(s){3}",
                        target.Rel, proposal.Solve.Considered, proposal.Solve.Cut.Count,
                        // Only when it happened. A shape that had to be cut short leaves hair standing
                        // exactly where the budget ran out, which looks identical to the press deciding that
                        // hair was fine — and telling those apart from a screenshot alone is impossible.
                        // Which ceiling, and by how much. "Dropped" alone blamed the shape-value budget for
                        // both limits, and they are not the same problem: the value count is spent across
                        // the whole file, while the spare-vertex ceiling is per mesh and one dense mesh
                        // reaches it with budget to spare. Printing what was wanted against what there was
                        // says whether the shortfall is marginal or hopeless.
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

        // HERE, not before the apply: the notice reports something that happened, and a write that then
        // failed would have announced an edit the mod folder never received.
        if (automatic) Announce();

        // The same hairstyle again, from every other option that supplies it. Each gets its own solve —
        // a long version and a short one are different geometry and the tails are not in the same places.
        var worn = HatCompatService.Rel(target.Rel);
        foreach (var rel in FilesFor(target))
        {
            // Both sides canonicalised, so the file just patched is recognised as itself. It used to be
            // compared raw, and the mod manifest spells a path with backslashes where target.Rel has
            // forward slashes — so the worn file came round again as its own sibling and was patched
            // twice. Nothing went wrong only because the re-read below sees the shp_hib written a moment
            // ago and Inspect bails; that is an accident, not a guard.
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

        // The file just changed underneath the key that was computed from it, so the next poll would see a
        // difference and examine all over again. Re-key here instead.
        lastKey = compositor.HatCompatKey();

        // Re-counted here as well as in Examine: this path ends without re-examining, and the count is
        // exactly what the write just changed.
        patchedInMod = HatCompatService.PatchedCount(target.ModRoot);

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
    /// Say in chat that Proteus has fitted the hairstyle just put on — and where to undo it or switch the
    /// feature off.
    /// <para/>
    /// The price of having the feature on by default. The fit is a silent edit to someone else's mod
    /// folder, which the user did not ask for; saying so at the moment it happens is what separates that
    /// from Proteus rummaging about behind their back. It also puts the two ways out in front of them,
    /// which is the part a log line could never do.
    /// <para/>
    /// ONCE PER HAIRSTYLE, and that needs no bookkeeping of its own: a fit only reaches here when the
    /// hairstyle was not already patched, and <see cref="HatCompatService.Apply"/> records the file before
    /// reporting success — so wearing the same hairstyle again is silent, and only a refit by a newer
    /// solver speaks again. Wearing a SECOND hairstyle is a second edit to a second file, and a line each
    /// is the honest account of that. A once-ever flag was the wrong shape for precisely that reason: it
    /// bought quiet by letting the tenth mod folder be edited as silently as if nothing had been said.
    /// </summary>
    private void Announce()
    {
        // Fire and forget onto the framework thread: this runs on a worker that has just spent a while
        // reading and rewriting a model, and Dalamud's chat does not belong there.
        //
        // The catch goes INSIDE the lambda, not around the dispatch. RunOnFrameworkThread captures
        // whatever the action throws into the task it returns, and that task is discarded here — so a
        // catch out here would see only a failure to SCHEDULE, and a print that threw would disappear
        // without a word in the log.
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                // The fit can finish just as the plugin is being torn down, and this is queued for a frame
                // that may never come — by which time what it reaches for is gone.
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
    /// Undo every hat-compat edit Proteus has made ANYWHERE in this mod, not merely the ones serving the
    /// hairstyle currently worn. Runs on a thread that already holds the busy flag.
    /// <para/>
    /// The record is the authority here, and that is the whole point. <see cref="FilesFor"/> asks which
    /// files serve the WORN game path, and a hair mod ships one model per race — so fitting as a Miqo'te
    /// and then changing to a Midlander left the Miqo'te patch named by nothing the panel could reach, and
    /// therefore impossible to undo for good. <see cref="HatCompatService.Revert"/> has always taken
    /// <c>only: null</c> to mean "everything in this mod"; nothing called it that way.
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

    /// <summary>
    /// Put this hairstyle back the way its author shipped it.
    /// <para/>
    /// Undo only. There used to be a redo-with-the-new-settings twin of this, for a "hide ponytails" switch
    /// that no longer exists; a patch made stale by a newer Proteus is now redone by <see cref="Examine"/>
    /// on its own, which is the case that twin was really covering.
    /// </summary>
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

                // Remember it, and remember it HERE rather than in the restore — the automatic refit of a
                // stale patch reverts too, and that one is a step on the way to writing a better patch, not
                // a request to leave the hairstyle alone.
                undone = Identity(target);

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
