using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CheapLoc;

namespace Proteus.Services;

/// <summary>
/// "Take the ears off Miqo'te hair mods": on the same walk, flight and reload as a port, the worn hairstyle of a Miqo'te
/// is pointed at an earless copy (<see cref="EarlessHairBuilder"/>, <see cref="EarlessHairWriter"/>); off, a pass over
/// every mod points each one back, and a copy found worn with the setting off — a config lost, an older build, a mod
/// imported with one — is pointed back when it is seen.
/// </summary>
public sealed partial class HairPortWatcher
{
    /// <summary>How long a restore pass that could not start (Penumbra not there, or it failed) waits to try again.</summary>
    private const int RestoreRetryMs = 30_000;

    /// <summary>Counts the setting being switched, so a restore pass that ran across a switch does not end what it owes.</summary>
    private int earlessToggleGen;

    /// <summary>The wait before the worn hairstyle's mod, unreadable at the last look, is looked at again. On the watcher:
    /// a host is made for every look.</summary>
    private readonly Backoff earlessBackoff = new();
    private long earlessLookDueAt;
    private long restoreRetryAt;

    /// <summary>The setting, switched in Settings &gt; Hair. On looks at what is worn now; off owes a pass putting every
    /// ear back, kept across a restart until it has run.</summary>
    public void SetEarless(bool on)
    {
        Interlocked.Increment(ref earlessToggleGen);
        config.EarlessMiqoteHair = on;
        config.EarlessRestorePending = !on;
        config.Save();
        earlessBackoff.Reset();
        restoreNotice = "";
        if (on) LookAfresh();
    }

    /// <summary>What the last restore pass has to say (the mods that kept a copy), kept on the panel across the looks
    /// that follow it until the setting is switched or another pass runs.</summary>
    private volatile string restoreNotice = "";

    /// <summary>The next examination ignores what the last one saw — however long it waits for a check or a port that
    /// holds the flight: a forced refresh owed while busy is served as a plain one, which would stop at the key.</summary>
    private int lookAfresh;

    private void LookAfresh()
    {
        Interlocked.Exchange(ref lookAfresh, 1);
        // Owed while Proteus is off: watching coming on walks, and that look takes it.
        if (Watching) Refresh();
    }

    private void LookAgainLater()
        => Interlocked.Exchange(ref earlessLookDueAt, Environment.TickCount64 + earlessBackoff.Next());

    /// <summary>On the framework thread, every frame Proteus watches: a look owed after an unreadable mod, and a
    /// restore pass owed.</summary>
    private void EarlessTick(long now)
    {
        long due = Interlocked.Read(ref earlessLookDueAt);
        if (due != 0 && now >= due && Interlocked.CompareExchange(ref earlessLookDueAt, 0, due) == due)
        {
            lastKey = null;
            RequestWalkIn(0);
        }
        if (Decide.RestoreDue(Watching, config.EarlessMiqoteHair, config.EarlessRestorePending, flight.Busy, now,
                              Interlocked.Read(ref restoreRetryAt)))
            StartEarlessRestore();
    }

    /// <summary>The pass putting every ear back, on a worker, holding the flight.</summary>
    private void StartEarlessRestore()
    {
        if (!flight.TryEnter()) return;
        int generation = Volatile.Read(ref earlessToggleGen);
        restoreNotice = "";
        modNames = penumbra.GetAllMods() ?? new Dictionary<string, string>();
        current = current with { Busy = true };
        Task.Run(() =>
        {
            try
            {
                var host = new Host(this);
                var modsRoot = host.ModsRoot;
                if (string.IsNullOrEmpty(modsRoot))
                {
                    // Penumbra is not there to ask: the pass is owed until it is.
                    Interlocked.Exchange(ref restoreRetryAt, Environment.TickCount64 + RestoreRetryMs);
                    return;
                }
                var worn = Decide.WornHair(livePartsCache);
                var pass = RestorePass(host, modsRoot, worn, () => config.EarlessMiqoteHair || disposed);
                lastKey = null;
                if (Decide.ClearPending(pass.Completed, generation, Volatile.Read(ref earlessToggleGen)))
                {
                    config.EarlessRestorePending = false;
                    config.Save();
                }
                log.Information("hair port: ears put back on {0} hairstyle(s); {1} mod(s) kept theirs, {2} could not be read",
                                pass.Restored, pass.Blocked.Count, pass.Unreadable.Count);
                var passView = PassView(pass, worn);
                restoreNotice = passView.Failed ? passView.Message : "";
                current = passView;
                RequestWalkIn(0);
            }
            catch (Exception ex)
            {
                log.Error(ex, "hair port: putting the ears back failed");
                Interlocked.Exchange(ref restoreRetryAt, Environment.TickCount64 + RestoreRetryMs);
                current = current with { Message = ex.Message, Failed = true };
            }
            finally
            {
                current = current with { Busy = false };
                if (flight.Exit(out var owedDelay)) Refresh(owedDelay);
            }
        });
    }

    /// <param name="Restored">Redirects pointed back at the author's file.</param>
    /// <param name="Blocked">Mods that kept a copy — edited since, or the author's file gone — each told once in chat.</param>
    /// <param name="Unreadable">Mods whose manifest or record could not be read: left to the next pass, or to the
    /// hairstyle being worn.</param>
    /// <param name="Completed">Every mod was looked at: not stopped by the setting going on again.</param>
    /// <param name="Lines">The chat line told for each blocked mod, in order.</param>
    internal sealed record PassResult(int Restored, bool WornRestored, IReadOnlyList<string> Blocked,
                                      IReadOnlyList<string> Unreadable, bool Completed, IReadOnlyList<string>? Lines = null);

    /// <summary>What the panel shows once a pass has ended: the mods that kept a copy and why, else how many hairstyles
    /// got their ears back.</summary>
    internal static View PassView(PassResult pass, string? worn)
    {
        if (pass.Lines is { Count: > 0 } lines)
            return new View(worn, Message: lines.Count == 1 ? lines[0] : string.Join("\n", lines), Failed: true);
        return new View(worn, Message: pass.Restored > 0 ? string.Format(RestoredFmt, pass.Restored) : "");
    }

    /// <summary>
    /// Put the ears back in every mod under <paramref name="modsRoot"/> that has an earless copy or a record of one: each
    /// mod restored is reloaded, and the player redrawn once when the hairstyle worn was among them. Stops between mods
    /// when <paramref name="stop"/> says the setting is on again.
    /// </summary>
    internal static PassResult RestorePass(IHost host, string modsRoot, string? worn, Func<bool> stop)
    {
        int restored = 0;
        bool wornRestored = false;
        var blocked = new List<string>();
        var unreadable = new List<string>();
        var lines = new List<string>();
        var mods = Directory.EnumerateDirectories(modsRoot)
                            .Where(d => EarlessHairRecord.Exists(d) || Directory.Exists(Path.Combine(d, EarlessHairPlan.Folder)))
                            .ToList();
        foreach (var dir in mods)
        {
            if (stop()) return new PassResult(restored, wornRestored, blocked, unreadable, Completed: false, lines);
            string modDir = Path.GetFileName(dir);
            EarlessHairWriter.Outcome outcome;
            // One mod that cannot be restored never holds up the rest.
            try { outcome = EarlessHairWriter.Restore(dir); }
            catch (Exception ex) { outcome = new EarlessHairWriter.Outcome(false, ex.Message, 0); }
            if (outcome.Unreadable)
            {
                host.Log($"{modDir}: ears not put back — {outcome.Message}");
                unreadable.Add(modDir);
                continue;
            }
            if (outcome.Warning.Length > 0) host.Warn($"{modDir}: ears put back, but its record was not updated — {outcome.Warning}");
            if (outcome.Count > 0)
            {
                host.Log($"{modDir}: ears put back on {outcome.Count} hairstyle(s)");
                host.Reload(modDir);
                restored += outcome.Count;
                if (worn != null && outcome.RestoredPaths.Contains(worn, StringComparer.OrdinalIgnoreCase)) wornRestored = true;
            }
            if (outcome.Message.Length > 0)
            {
                host.Log($"{modDir}: an earless copy was kept — {outcome.Message}");
                blocked.Add(modDir);
                var line = string.Format(RestoreBlockedFmt, host.ModName(modDir), outcome.Message);
                lines.Add(line);
                host.AnnounceEarless(line);
            }
        }
        if (wornRestored && (host.WornNow() is not { } now || string.Equals(now, worn, StringComparison.OrdinalIgnoreCase)))
            host.Redraw();
        return new PassResult(restored, wornRestored, blocked, unreadable, Completed: true, lines);
    }

    /// <summary>
    /// The earless setting on, a Miqo'te wearing <paramref name="worn"/> served from <paramref name="file"/>: every place in
    /// its mod publishing it with ears is pointed at an earless copy, then the mod reloaded and the player redrawn. Null
    /// when there is nothing to do here — not a Miqo'te, not a mod's file, a port, or a hairstyle with no ears.
    /// </summary>
    internal static View? EarlessStep(IHost host, string modsRoot, string worn, ushort race, string? file, ref string? lastKey)
    {
        if (!HairPortBaker.HasEars(race) || file == null || !HatCompatService.InMods(file, modsRoot, out var modRoot, out var rel)
            || HairPortPlan.IsPortFile(rel)) return null;
        string modDir = Path.GetFileName(modRoot);
        var hint = EarlessHint(modsRoot, worn, race, file);
        var plan = EarlessHairBuilder.Build(modRoot, worn);
        var saved = plan.Writes.Count > 0 && !plan.Unreadable && plan.Refusal.Length == 0
            ? EarlessHairWriter.Save(modRoot, plan.Writes) : null;

        if (plan.Unreadable || saved is { Unreadable: true })
        {
            // No verdict: the mod is likely mid-update. Looked at again after a back-off, not on every walk.
            var why = saved?.Message ?? plan.Refusal;
            host.Log($"{worn} not made earless yet — {why}");
            lastKey = null;
            host.RequestLookAgain();
            return new View(worn, modRoot, Message: why, Failed: true, EarlessOptions: hint);
        }
        host.EarlessSettled();
        if (plan.Refusal.Length > 0 || saved is { Ok: false })
        {
            var why = saved?.Message ?? plan.Refusal;
            host.Log($"{worn} keeps its ears — {why}");
            return new View(worn, modRoot, Message: why, Failed: true, EarlessOptions: hint);
        }
        if (saved == null)
            return plan.Current > 0 ? new View(worn, modRoot, Earless: true, EarlessOptions: hint) : null;

        foreach (var w in plan.Writes.Where(w => w.TrimRefusal.Length > 0))
            host.Warn($"{w.To} keeps its lower levels of detail, which still draw the ears from a distance — {w.TrimRefusal}");
        host.Log($"{worn} is drawn without its ears — {saved.Count} place(s) in {modDir}"
               + (saved.Warning.Length > 0 ? $" (its record was not updated: {saved.Warning})" : ""));
        if (plan.Writes.Any(w => !w.Remake && EarlessHairPlan.IsEarlessFile(w.To))) host.AnnounceEarless(ChatLine);
        ReloadAndRedraw(host, modDir, worn);
        lastKey = null;
        bool earless = plan.Current > 0 || plan.Writes.Any(w => EarlessHairPlan.IsEarlessFile(w.To));
        return new View(worn, modRoot, Earless: earless, EarlessOptions: hint);
    }

    /// <summary>
    /// The earless setting off, <paramref name="worn"/> served from an earless copy of ours: its ears are put back — every
    /// place in the mod publishing it from a copy pointed back at the author's file — then the mod reloaded and the
    /// player redrawn. Null when the worn hairstyle is not drawn from a copy.
    /// </summary>
    internal static View? EarlessRestoreAtEquip(IHost host, string modsRoot, string worn, ushort race, string? file,
                                                ref string? lastKey)
    {
        if (!HairPortBaker.HasEars(race) || file == null || !HatCompatService.InMods(file, modsRoot, out var modRoot, out var rel)
            || !EarlessHairPlan.IsEarlessFile(rel)) return null;
        string modDir = Path.GetFileName(modRoot);
        var outcome = EarlessHairWriter.Restore(modRoot, worn);
        if (outcome.Unreadable)
        {
            host.Log($"{worn}: ears not put back yet — {outcome.Message}");
            lastKey = null;
            host.RequestLookAgain();
            return new View(worn, modRoot, Earless: true, Message: outcome.Message, Failed: true);
        }
        host.EarlessSettled();
        if (outcome.Warning.Length > 0) host.Warn($"{worn}: ears put back, but its record was not updated — {outcome.Warning}");
        if (outcome.Count == 0)
        {
            host.Log($"{worn}: the earless copy was kept — {outcome.Message}");
            return new View(worn, modRoot, Earless: true, Message: outcome.Message, Failed: outcome.Message.Length > 0);
        }
        host.Log($"{worn}: ears put back on {outcome.Count} place(s) in {modDir}");
        ReloadAndRedraw(host, modDir, worn);
        lastKey = null;
        return new View(worn, Message: outcome.Message, Failed: outcome.Message.Length > 0);
    }

    /// <summary>The mod's own earless versions of an eared Miqo'te hairstyle being worn, or null for none (see
    /// <see cref="EarlessHairPlan.EarlessOptionHint"/>).</summary>
    internal static IReadOnlyList<string>? EarlessHint(string modsRoot, string worn, ushort race, string? file)
    {
        if (!HairPortBaker.HasEars(race) || file == null || !HatCompatService.InMods(file, modsRoot, out var modRoot, out var rel)
            || HairPortPlan.IsPortFile(rel)) return null;
        // Only for a hairstyle with ears to take off — or already taken off by us.
        if (!EarlessHairPlan.IsEarlessFile(rel)
            && (HairPortBuilder.ReadMod(modRoot, rel) is not { } bytes || EarlessHair.DrawnFur(bytes) == 0)) return null;
        if (HairPortPlan.ReadOwners(modRoot) is not { } owners) return null;
        var hint = EarlessHairPlan.EarlessOptionHint(owners, worn, rel, r => HairPortBuilder.ReadMod(modRoot, r));
        return hint.Count > 0 ? hint : null;
    }

    internal static string ChatLine => ProteusLoc.Localize("HairPort.Earless.Chat",
        "[Proteus] This hairstyle's cat ears are taken off: Proteus pointed its mod at an earless copy, so every character "
      + "drawn with this mod on, in every collection, has no ears with it now. Turn off \"Take the ears off Miqo'te hair "
      + "mods\" under /proteus > Settings > Hair to put them back.");

    internal static string RestoreBlockedFmt => ProteusLoc.Localize("HairPort.Earless.RestoreBlocked.Fmt",
        "[Proteus] The ears could not be put back in {0}: {1}");

    internal static string RestoredFmt => ProteusLoc.Localize("HairPort.Earless.Restored.Fmt", "Ears put back on {0} hairstyle(s).");

    /// <summary>A wait that doubles each time it is asked for again, from 5 s up to 5 minutes, until reset.</summary>
    internal sealed class Backoff
    {
        private int ms;

        public int Next()
        {
            int next = Decide.NextRetry(Volatile.Read(ref ms));
            Volatile.Write(ref ms, next);
            return next;
        }

        public void Reset() => Volatile.Write(ref ms, 0);
    }

    internal static partial class Decide
    {
        /// <param name="Watching">The worn hairstyle is looked at: Proteus on and Penumbra there.</param>
        /// <param name="Porting">Hair fitting is on too: the scan, its schedule and the equip port.</param>
        /// <param name="Earless">The earless setting is on too.</param>
        public readonly record struct GateState(bool Watching, bool Porting, bool Earless);

        /// <summary>Which of the watcher's work runs. Porting is what "enabled" always meant; watching goes on for either
        /// setting — and for neither, so an earless copy left worn is still seen and pointed back.</summary>
        public static GateState Gates(bool pluginEnabled, bool penumbra, bool autoPort, bool earless)
        {
            bool watching = pluginEnabled && penumbra;
            return new GateState(watching, watching && autoPort, watching && earless);
        }

        /// <summary>Whether the pass putting the ears back is to start now.</summary>
        public static bool RestoreDue(bool watching, bool earlessOn, bool pending, bool busy, long now, long retryAt)
            => watching && !earlessOn && pending && !busy && now >= retryAt;

        /// <summary>Whether a pass that ended clears what it owed: it looked at every mod, and the setting was not switched
        /// while it ran.</summary>
        public static bool ClearPending(bool completed, int generationAtStart, int generationNow)
            => completed && generationAtStart == generationNow;

        /// <summary>A look's view with the last restore pass's notice kept on it: a message of the look's own wins.</summary>
        public static View WithNotice(View view, string notice)
            => notice.Length == 0 || view.Message.Length > 0 || ReferenceEquals(view, Unchanged)
                ? view : view with { Message = notice, Failed = true };

        /// <param name="LookAfresh">Look at the worn hairstyle ignoring the last look's key.</param>
        /// <param name="ResumePorting">Fitting came on: the scan's resume, first full check and old-carry sweep.</param>
        public readonly record struct EdgeActions(bool RequestWalk, bool LookAfresh, bool ResumePorting, bool StopCheck);

        /// <summary>What a frame does about the gates changing since the last: watching coming on asks for a walk;
        /// fitting coming on asks for one too, looks afresh and resumes the scan; fitting going off stops a check.</summary>
        public static EdgeActions Edges(bool wasWatching, bool wasPorting, bool watching, bool porting)
        {
            bool portingRose = porting && !wasPorting;
            return new EdgeActions(RequestWalk: (watching && !wasWatching) || portingRose, LookAfresh: portingRose,
                                   ResumePorting: portingRose, StopCheck: !porting && wasPorting);
        }

        /// <summary>The key an examination starts from: none when a fresh look was asked for since the last one began —
        /// by the setting switched on, or fitting coming on (with fitting off a look keeps its key, and would otherwise
        /// see nothing new) — even if that look had to wait for the flight.</summary>
        public static string? KeyForLook(string? lastKey, bool afreshOwed) => afreshOwed ? null : lastKey;
    }
}
