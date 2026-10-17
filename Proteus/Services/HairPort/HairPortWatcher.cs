using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Proteus.Interop;
using XivLiveMesh;

namespace Proteus.Services;

/// <summary>
/// Watches which hairstyle the player is wearing and, when the game draws its own model because the enabled mod for
/// that hairstyle was made for another race only, ports that race's model onto the wearer's (when the setting is on).
/// Off the UI thread, one examination at a time. A trigger only: the decisions are <see cref="HairPortPlan"/>'s and
/// <see cref="HairPortBuilder"/>'s, the writes <see cref="HairPortWriter"/>'s.
/// </summary>
public sealed class HairPortWatcher : IDisposable
{
    /// <summary>How long to wait after a Penumbra settings change before looking: the event arrives before Penumbra
    /// has finished re-resolving.</summary>
    private const int PenumbraSettleMs = 400;

    /// <summary>How long a burst of signals must go quiet before the draw object is walked; each signal resets it.</summary>
    private const int WalkDebounceMs = 250;

    /// <summary>How long to wait before walking again after a walk found no hair.</summary>
    private const int BlankRetryMs = 500;

    /// <summary>How many consecutive walks may find no hair before believing it: a blank walk is almost always a
    /// character mid-rebuild.</summary>
    internal const int BlanksBeforeNone = 4;

    private readonly CompositorService compositor;
    private readonly PenumbraBridge penumbra;
    private readonly GlamourerBridge glamourer;
    private readonly Configuration config;
    private readonly IPluginLog log;

    /// <summary>One examination or undo at a time; a look asked for meanwhile is owed, not dropped.</summary>
    private readonly SingleFlight flight = new();
    private volatile string? lastKey;
    private long walkDueAt;
    private bool wasEnabled;
    private int blanks;
    private volatile IReadOnlyList<string>? livePartsCache;
    private volatile bool disposed;
    private PbdFile? pbd;

    /// <summary>What the panel draws, replaced wholesale.</summary>
    /// <param name="Worn">The worn hairstyle's game path, or null for none.</param>
    /// <param name="ModRoot">The mod a port of the worn hairstyle lives in, or null.</param>
    /// <param name="Ported">The worn hairstyle is drawn from a port, which can be undone.</param>
    /// <param name="PortsInMod">How many ports that mod has, for the undo-all button.</param>
    public sealed record View(string? Worn = null, string? ModRoot = null, bool Ported = false, int PortsInMod = 0,
                              bool Busy = false, string Message = "", bool Failed = false);

    private volatile View current = new();
    public View Current => current;

    public HairPortWatcher(CompositorService compositor, PenumbraBridge penumbra, GlamourerBridge glamourer,
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

        // WHICH FILE serves it: a mod switched on or off changes what the hairstyle resolves to, not its path.
        penumbra.ModSettingChanged += OnModSettingChanged;
        penumbra.PlayerCollectionChanged += OnCollectionChanged;

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

    private bool Enabled => config.PluginEnabled && config.AutoHairPort && penumbra.IsAvailable;

    private void RequestWalk() => RequestWalkIn(WalkDebounceMs);

    private void RequestWalkIn(int ms)
    {
        if (disposed || !Enabled) return;
        Interlocked.Exchange(ref walkDueAt, Environment.TickCount64 + ms);
    }

    private void OnModSettingChanged(Penumbra.Api.Enums.ModSettingChange change, Guid collection, string modDirectory,
                                     bool inherited)
    {
        if (Enabled) Refresh(delayMs: PenumbraSettleMs);
    }

    private void OnCollectionChanged()
    {
        if (Enabled) Refresh(delayMs: PenumbraSettleMs);
    }

    /// <summary>The one walk a signal asked for, on the framework thread, where the draw object may be read.</summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed) return;
        bool enabled = Enabled;
        if (enabled && !wasEnabled) RequestWalk();
        wasEnabled = enabled;
        if (!enabled) return;

        long due = Interlocked.Read(ref walkDueAt);
        if (due == 0 || Environment.TickCount64 < due) return;
        if (flight.Busy) return;
        Interlocked.CompareExchange(ref walkDueAt, 0, due);

        IReadOnlyList<string>? parts;
        try { parts = compositor.HatCompatLiveParts(out _); }
        catch (Exception ex) { log.Error(ex, "hair port: walking the worn hairstyle failed"); return; }

        if (Decide.Blank(parts, ref blanks)) { RequestWalkIn(BlankRetryMs); return; }
        livePartsCache = parts;
        Refresh();
    }

    /// <summary>
    /// Look again at what is worn, on a worker. A caller that finds an examination running leaves one more look owed,
    /// taken when it ends: a mod switched on or off mid-port changes what the hairstyle resolves to, and no other signal
    /// would bring the watcher back to it.
    /// </summary>
    public void Refresh(int delayMs = 0, bool force = false)
    {
        if (disposed) return;
        if (!flight.TryEnter()) return;
        current = current with { Busy = true };
        Task.Run(() =>
        {
            try
            {
                if (delayMs > 0) Thread.Sleep(delayMs);
                if (force) lastKey = null;
                Examine();
            }
            catch (Exception ex)
            {
                log.Error(ex, "hair port: examining the worn hairstyle failed");
                current = current with { Message = ex.Message, Failed = true };
            }
            finally
            {
                current = current with { Busy = false };
                if (flight.Exit()) Refresh();
            }
        });
    }

    /// <summary>
    /// One piece of work at a time, without losing a request that arrives meanwhile: <see cref="TryEnter"/> fails while
    /// busy and leaves a request owed, and <see cref="Exit"/> says whether one is.
    /// </summary>
    internal sealed class SingleFlight
    {
        private int busy, owed;

        public bool Busy => Volatile.Read(ref busy) != 0;

        public bool TryEnter()
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) == 0) return true;
            Volatile.Write(ref owed, 1);
            // The holder may have left between the two lines above, without seeing the request: take it here instead.
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
            Volatile.Write(ref owed, 0);
            return true;
        }

        /// <returns>Whether a request came in while busy, which the caller should now serve.</returns>
        public bool Exit()
        {
            Interlocked.Exchange(ref busy, 0);
            return Interlocked.Exchange(ref owed, 0) == 1;
        }
    }

    private void Examine()
    {
        string? key = lastKey;
        var view = Run(new Host(this), Decide.WornHair(livePartsCache), ref key);
        lastKey = key;
        if (!ReferenceEquals(view, Unchanged)) current = view;
    }

    /// <summary>What the examination needs from the plugin, so the examination itself can be tested.</summary>
    internal interface IHost
    {
        bool Enabled { get; }
        string? ModsRoot { get; }
        string? Resolve(string gamePath);
        /// <summary>The game's racial deformer; null when it cannot be read.</summary>
        PbdFile? Pbd();
        byte[]? ReadGame(string gamePath);
        void Log(string message);
        void Announce();
        void Reload(string modDir);
        /// <summary>The hairstyle the latest walk saw, which may have changed while a port ran.</summary>
        string? WornNow();
        void Redraw();
    }

    private sealed class Host(HairPortWatcher w) : IHost
    {
        public bool Enabled => w.Enabled;
        public string? ModsRoot => w.penumbra.GetModDirectory();
        public string? Resolve(string gamePath) => w.penumbra.ResolvePlayer(gamePath);
        public PbdFile? Pbd() => w.Pbd();
        public byte[]? ReadGame(string gamePath) => HairPortWatcher.ReadGame(gamePath);
        public void Log(string message) => w.log.Information("hair port: {0}", message);
        public void Announce() => w.Announce();
        public void Reload(string modDir)
        {
            w.compositor.ExpectOwnModEdit(modDir);
            var ec = w.penumbra.ReloadModDirectory(modDir);
            w.log.Information("hair port: asked Penumbra to reload {0} — {1}", modDir, ec);
        }
        public string? WornNow() => Decide.WornHair(w.livePartsCache);
        // Not deferred into an auto refit batch's hold: only that batch's own work waits for its one redraw.
        public void Redraw() => w.compositor.RedrawForChangedModel("hair port");
    }

    /// <summary>
    /// One examination of the worn hairstyle <paramref name="worn"/>: what it resolves to, whether a mod made it for
    /// another race only, and — with the feature on — the port, its save, the chat line, the reload and the redraw.
    /// </summary>
    /// <param name="lastKey">What the last examination saw; an unchanged one returns at once.</param>
    internal static View Run(IHost host, string? worn, ref string? lastKey)
    {
        var modsRoot = host.ModsRoot;
        if (worn == null || string.IsNullOrEmpty(modsRoot)) return new View();
        var (race, id) = HairPortPlan.Parse(worn)!.Value;

        // Cheap on purpose: Penumbra's settings events come in bursts. What the worn hairstyle and every same-sex
        // race's copy of it resolve to is everything a decision below reads.
        var candidates = HairPortPlan.OrderSources(race, HairPortPlan.SameGenderRaces(race)).ToList();
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [worn] = host.Resolve(worn) };
        foreach (var r in candidates) resolved[HairPortPlan.HairPath(r, id)] = host.Resolve(HairPortPlan.HairPath(r, id));
        var key = string.Join("|", resolved.Select(p => $"{p.Key}>{p.Value}"));
        if (key == lastKey) return Unchanged;
        lastKey = key;

        var decision = Decide.What(worn, resolved, candidates, modsRoot!, HairPortRecord.Read);
        switch (decision.Kind)
        {
            case Decide.Kind.None:
                if (decision.Message.Length > 0) host.Log(decision.Message);
                return new View(worn, Message: decision.Message);
            case Decide.Kind.Ported:
                return new View(worn, decision.ModRoot, Ported: true, PortsInMod: HairPortRecord.Read(decision.ModRoot!).Ports.Count);
        }

        // Port (or remake a stale port of) the worn hairstyle from decision.Source in decision.ModRoot.
        if (!host.Enabled) { lastKey = null; return new View(worn); }
        var modRoot = decision.ModRoot!;
        string modDir = Path.GetFileName(modRoot);
        host.Log($"{worn} is drawn as the game's own — porting {decision.Source} from "
               + $"{(decision.Stale ? "an out-of-date port" : "the author's model")} in {modDir}");

        if (host.Pbd() is not { } pbd)
        {
            host.Log($"{worn} not ported — {NoPbd}");
            return new View(worn, modRoot, Message: NoPbd, Failed: true);
        }
        var plan = HairPortBuilder.Build(modRoot, decision.Source!, [race], pbd, host.ReadGame, withSharers: true);
        foreach (var (r, why) in plan.Refused) host.Log($"c{r:D4} not ported — {why}");
        if (plan.Writes.Count == 0)
        {
            var why = plan.Refused.TryGetValue(race, out var w) ? w : "";
            return new View(worn, modRoot, Message: why, Failed: why.Length > 0);
        }

        var outcome = HairPortWriter.Save(modRoot, plan.Writes, auto: true);
        if (!outcome.Ok)
        {
            host.Log($"{modDir} was not saved — {outcome.Message}");
            return new View(worn, modRoot, Message: outcome.Message, Failed: true);
        }
        foreach (var w in plan.Writes)
            host.Log($"{w.SourceGamePath} → {w.TargetGamePath} in {modDir}: {HairPortWriter.Describe(w.Into)} "
                   + $"({w.Materials.Count} material redirect(s), bent in {w.Millis} ms)");

        host.Announce();
        ReloadAndRedraw(host, modDir, worn);
        lastKey = null;
        return new View(worn, modRoot, Ported: true, PortsInMod: HairPortRecord.Read(modRoot).Ports.Count);
    }

    /// <summary>Returned for an examination that found nothing changed: the panel keeps what it shows.</summary>
    internal static readonly View Unchanged = new();

    /// <summary>
    /// Make Penumbra re-read the mod, then redraw so the game loads the hairstyle again — skipped when a different
    /// hairstyle went on meanwhile, which its own walk handles. For a port and an undo alike.
    /// </summary>
    internal static void ReloadAndRedraw(IHost host, string modDir, string worn)
    {
        host.Reload(modDir);
        if (host.WornNow() is not { } now || string.Equals(now, worn, StringComparison.OrdinalIgnoreCase)) host.Redraw();
    }

    /// <summary>Undo the port the worn hairstyle is drawn from, and keep it undone.</summary>
    public void Undo() => RunUndo(all: false);

    /// <summary>Undo every port in the worn hairstyle's mod, and keep them undone.</summary>
    public void UndoAll() => RunUndo(all: true);

    private void RunUndo(bool all)
    {
        var view = current;
        if (view.ModRoot == null || view.Worn == null || view.Busy) return;
        if (!flight.TryEnter()) return;
        current = view with { Busy = true };
        Task.Run(() =>
        {
            try
            {
                var worn = view.Worn;
                var outcome = HairPortWriter.Undo(view.ModRoot,
                    e => all || e.TargetGamePath.Equals(worn, StringComparison.OrdinalIgnoreCase), decline: true);
                if (!outcome.Ok)
                {
                    current = current with { Message = outcome.Message, Failed = true };
                    return;
                }
                log.Information("hair port: undid {0} port(s) in {1}", outcome.Count, Path.GetFileName(view.ModRoot));
                ReloadAndRedraw(new Host(this), Path.GetFileName(view.ModRoot), worn);
                lastKey = null;
                current = new View(worn, Message: outcome.Message);
            }
            catch (Exception ex)
            {
                log.Error(ex, "hair port: undoing failed");
                current = current with { Message = ex.Message, Failed = true };
            }
            finally
            {
                current = current with { Busy = false };
                if (flight.Exit()) Refresh();
            }
        });
    }

    /// <summary>The game's own deformer — never a modded one, so a port that travels with the mod is not shaped by one
    /// user's pbd mod.</summary>
    private PbdFile? Pbd()
    {
        if (pbd != null) return pbd;
        try { return pbd = Plugin.DataManager.GetFile(LiveCharacter.PbdGamePath) is { } f ? new PbdFile(f.Data) : null; }
        catch (Exception ex)
        {
            log.Warning(ex, "hair port: the game's racial deformer could not be read");
            return null;
        }
    }

    /// <summary>Why nothing can be ported without the deformer.</summary>
    internal static string NoPbd
        => CheapLoc.Loc.Localize("HairPort.Refused.NoPbd", "the game's racial deformer could not be read");

    private static byte[]? ReadGame(string gamePath)
    {
        try { return Plugin.DataManager.GetFile(gamePath)?.Data; }
        catch (Exception) { return null; }
    }

    private void Announce()
    {
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                if (disposed) return;
                Plugin.ChatGui.Print(new Dalamud.Game.Text.SeStringHandling.SeStringBuilder()
                    .AddUiForeground(CheapLoc.Loc.Localize("Chat.HairPort",
                        "[Proteus] This hairstyle's mod was made for another race, so Proteus has fitted it to yours and "
                      + "added that to the mod — everyone drawn with this mod on now sees it. You can undo that, or turn "
                      + "the feature off, under /proteus > Settings > Hair."), 45)
                    .Build());
            }
            catch (Exception ex) { log.Warning(ex, "hair port: could not announce the port in chat"); }
        });
    }

    /// <summary>The watcher's decisions, apart from its threads and IPC, so they can be tested.</summary>
    internal static class Decide
    {
        /// <summary>The hairstyle among a walk's models; null for none.</summary>
        public static string? WornHair(IReadOnlyList<string>? parts)
            => parts?.FirstOrDefault(p => HairPortPlan.Parse(p) != null);

        /// <summary>Whether a walk found no hair and should be retried rather than believed, counting the run.</summary>
        public static bool Blank(IReadOnlyList<string>? parts, ref int blanks)
        {
            if (WornHair(parts) != null) { blanks = 0; return false; }
            return ++blanks < BlanksBeforeNone;
        }

        public enum Kind { None, Ported, Port }

        /// <param name="Source">For <see cref="Kind.Port"/>: the game path of the model to port.</param>
        /// <param name="Stale">The worn hairstyle is drawn from a port of ours that is out of date.</param>
        public sealed record Decision(Kind Kind, string? ModRoot = null, string? Source = null, bool Stale = false,
                                      string Message = "");

        /// <summary>
        /// What to do about the worn hairstyle, from what it and every same-sex race's copy of it resolve to.
        /// </summary>
        /// <param name="resolved">Game path to the file Penumbra serves for the player (null or not under the mods root:
        /// the game's own).</param>
        /// <param name="candidates">The races to port from, best first (<see cref="HairPortPlan.OrderSources"/>).</param>
        public static Decision What(string worn, IReadOnlyDictionary<string, string?> resolved,
                                    IReadOnlyList<ushort> candidates, string modsRoot,
                                    Func<string, HairPortRecord> readRecord)
        {
            var (_, id) = HairPortPlan.Parse(worn)!.Value;
            if (resolved.GetValueOrDefault(worn) is { } file && HatCompatService.InMods(file, modsRoot, out var modRoot, out var rel))
            {
                // Already modded for the wearer's race: the author's, another mod's — or a port of ours.
                if (!HairPortPlan.IsPortFile(rel)) return new Decision(Kind.None);
                var record = readRecord(modRoot);
                var entry = record.Ports.FirstOrDefault(e => e.File.Equals(HairPortRecord.Rel(rel), StringComparison.OrdinalIgnoreCase));
                if (entry == null) return new Decision(Kind.Ported, modRoot);
                byte[]? source = null;
                try { source = File.ReadAllBytes(Path.Combine(modRoot, entry.SourceFile.Replace('/', Path.DirectorySeparatorChar))); }
                catch (Exception) { /* the author's file is gone: nothing to remake it from */ }
                return source != null && HairPortPlan.IsStale(entry, source)
                    ? new Decision(Kind.Port, modRoot, entry.SourceGamePath, Stale: true)
                    : new Decision(Kind.Ported, modRoot);
            }

            foreach (var race in candidates)
            {
                var path = HairPortPlan.HairPath(race, id);
                if (resolved.GetValueOrDefault(path) is not { } f || !HatCompatService.InMods(f, modsRoot, out var root, out var r))
                    continue;
                // Our own port of another race is never a source: the author's model is.
                if (HairPortPlan.IsPortFile(r)) continue;
                if (readRecord(root).Declined.Contains(HairPortPlan.DeclineKey(worn)))
                    return new Decision(Kind.None, root, Message: string.Format(CheapLoc.Loc.Localize(
                        "HairPort.Declined.Fmt", "The port of hairstyle {0} in {1} was undone, so it is left alone."),
                        $"h{id:D4}", Path.GetFileName(root)));
                return new Decision(Kind.Port, root, path);
            }
            return new Decision(Kind.None);
        }
    }
}
