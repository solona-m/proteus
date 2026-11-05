using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Proteus.Interop;
using static Proteus.Services.ShaderUpgradePlan;

namespace Proteus.Services;

/// <summary>
/// Moves worn gear from <c>characterlegacy.shpk</c> to Dawntrail's <c>character.shpk</c> as it is put on, when
/// <see cref="Configuration.AutoUpgradeLegacyShaders"/> is on. Nothing is written into the user's mods: each worn legacy
/// material is redirected, through one Penumbra temporary mod per collection, to an upgraded copy in the managed mod's
/// <see cref="ManagedModPaths.ShaderUpgradeSubdir"/> (with a derived mask for the diffuse form). Turning the setting off
/// takes the temporary mod down and reloads once.
/// <para/>
/// Only orchestration lives here: every decision is in <see cref="ShaderUpgradePlan"/>, the files in
/// <see cref="ShaderUpgradeCopies"/>, the redirect state in <see cref="ShaderUpgradeRedirects"/> and the one-in-flight
/// rule in <see cref="WalkGate"/>. A walk reads the draw object on the framework thread (hop A), builds and writes on a
/// worker, and applies on the framework thread again (hop B); workers block on their hops, the framework thread never
/// waits for a worker.
/// </summary>
public sealed class ShaderUpgradeWatcher : IShaderUpgradeSources, IDisposable
{
    private const long SlowFrameMs = 8;
    private const int BlanksBeforeNone = 4;

    private readonly CompositorService compositor;
    private readonly PenumbraBridge penumbra;
    private readonly GlamourerBridge glamourer;
    private readonly TextureLoader textureLoader;
    private readonly Configuration config;
    private readonly IPluginLog log;

    private readonly ShaderUpgradeRedirects redirects;
    private readonly WalkGate gate;
    private readonly MaskMemo memo = new();
    private readonly ShaderUpgradeCopies.ReplacedTail tail = new(MaskTail);
    private readonly ReloadLearning learning = new();
    private readonly Escalation escalation = new();
    private readonly int pid = Environment.ProcessId;

    private volatile bool stopped;
    private bool wasEnabled, leftoverRemoved, askedManagedMod, swept;
    private int blanks;
    private long walkDueAt;
    private int peekDue;
    private int verifyDue;

    /// <summary>Penumbra setting changes since the last walk: (collection, mod folder), judged on the walk's worker.</summary>
    private readonly ConcurrentQueue<(Guid Collection, string Mod)> pendingSettings = new();

    /// <summary>The upstream each worn material game path was last learned to come from.</summary>
    private readonly ConcurrentDictionary<string, string?> knownUpstream = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per mod folder: (option files' newest write, the game paths its option files name).</summary>
    private readonly ConcurrentDictionary<string, (long Key, HashSet<string> Paths)> shipsCache =
        new(StringComparer.OrdinalIgnoreCase);

    private ReloadRoute lastRoute = ReloadRoute.None;

    // Counters for /state.
    private int memoHits, decodes, pruned, missingBeforeApply, peeksTrusted, peeksSettled, peeksFailedOpen,
                reloadsInPlace, reloadsRedraw, gaveUp;

    private volatile MaskGloss gloss = MaskGloss.InverseSpecular;

    /// <summary>One upgraded material as <c>/state</c> and the status line report it.</summary>
    public sealed record UpgradedEntry(string GamePath, string Source, string Copy, string Form,
                                       string? MaskGamePath, string? MaskCopy, int MaskWidth, int MaskHeight, bool Loaded);

    public sealed record LeftEntry(string GamePath, string Source, string Reason);

    public sealed record PeekStats(int TrustedFirstTry, int Settled, int FailedOpen);
    public sealed record ReloadStats(bool? InPlaceLoadsMaterials, int InPlace, int Redraw, int GaveUp);
    public sealed record MaskStats(int MemoHits, int Decodes, int Pruned, int MissingBeforeApply);

    /// <summary>Everything the panel and the harness show, replaced wholesale.</summary>
    public sealed record View(
        bool Enabled = false, bool Busy = false, string Waiting = "none", bool AwaitingRedraw = false,
        string MaskGloss = "inverse-specular",
        IReadOnlyList<UpgradedEntry>? Upgraded = null, IReadOnlyList<LeftEntry>? Left = null,
        PeekStats? Peeks = null, ReloadStats? Reload = null, MaskStats? Masks = null, DateTime? LastAppliedUtc = null)
    {
        public IReadOnlyList<UpgradedEntry> UpgradedList => Upgraded ?? [];
        public IReadOnlyList<LeftEntry> LeftList => Left ?? [];
    }

    private volatile View current = new();
    public View Current => current with { Enabled = Enabled, Busy = gate.Busy };

    public ShaderUpgradeWatcher(CompositorService compositor, PenumbraBridge penumbra, GlamourerBridge glamourer,
                                TextureLoader textureLoader, Configuration config, IPluginLog log)
    {
        this.compositor = compositor;
        this.penumbra = penumbra;
        this.glamourer = glamourer;
        this.textureLoader = textureLoader;
        this.config = config;
        this.log = log;
        redirects = new ShaderUpgradeRedirects(
            (c, map) => penumbra.SetTemporaryMod(Tag, c, map, Priority),
            c => penumbra.RemoveTemporaryMod(Tag, c, Priority));
        gate = new WalkGate(Prune);

        compositor.UseShaderUpgradeSources(this);
        glamourer.LocalPlayerEquipmentChanged += OnEquip;
        glamourer.LocalPlayerStateChangedAny += OnStateChangedAny;
        penumbra.LocalPlayerRedrawn += OnRedrawn;
        penumbra.PlayerCollectionChanged += OnCollectionChanged;
        penumbra.ModSettingChanged += OnModSettingChanged;
        penumbra.PenumbraReady += OnPenumbraReady;
        penumbra.ModMoved += OnModMoved;
        compositor.PublishedGamePathsChanged += OnPublishedChanged;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        // Marked stopped first: a walk checks before every write and before hop B, and writes nothing more.
        stopped = true;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        glamourer.LocalPlayerEquipmentChanged -= OnEquip;
        glamourer.LocalPlayerStateChangedAny -= OnStateChangedAny;
        penumbra.LocalPlayerRedrawn -= OnRedrawn;
        penumbra.PlayerCollectionChanged -= OnCollectionChanged;
        penumbra.ModSettingChanged -= OnModSettingChanged;
        penumbra.PenumbraReady -= OnPenumbraReady;
        penumbra.ModMoved -= OnModMoved;
        compositor.PublishedGamePathsChanged -= OnPublishedChanged;
        compositor.UseShaderUpgradeSources(null);
        try
        {
            // No reload on unload; the redirect simply goes.
            redirects.Teardown();
            Prune();
        }
        catch (Exception ex) { log.Warning(ex, "[Proteus] shader upgrade: tearing down on unload failed"); }
    }

    private bool Enabled => config.PluginEnabled && config.AutoUpgradeLegacyShaders;

    /// <summary>The mask strategy for this session (QA only; never persisted).</summary>
    internal MaskGloss Gloss
    {
        get => gloss;
        set
        {
            if (gloss == value) return;
            gloss = value;
            current = current with { MaskGloss = GlossName(value) };
            Request(Signal.Enabled);
        }
    }

    internal static string GlossName(MaskGloss g) => g switch
    {
        MaskGloss.LowConstant => "low",
        MaskGloss.MidConstant => "mid",
        _ => "inverse-specular",
    };

    internal static MaskGloss? ParseGloss(string? s) => s switch
    {
        "inverse-specular" => MaskGloss.InverseSpecular,
        "low" => MaskGloss.LowConstant,
        "mid" => MaskGloss.MidConstant,
        _ => null,
    };

    // ── IShaderUpgradeSources and the design binding ───────────────────────

    public string? SourceFor(string gamePath) => redirects.SourceFor(gamePath);

    /// <summary>The source mod file a drawn copy stands for (per game path when given), or null when not a copy.</summary>
    public string? SourceForCopy(string file, IEnumerable<string>? gamePaths = null)
        => redirects.SourceForCopy(file, gamePaths);

    // ── signals ─────────────────────────────────────────────────────────────

    private void Request(Signal s)
    {
        if (stopped || !Enabled) return;
        var (delay, peek) = SignalFor(s);
        if (peek) Interlocked.Exchange(ref peekDue, 1);
        if (s == Signal.Verify) Interlocked.Exchange(ref verifyDue, 1);
        long due = Environment.TickCount64 + delay;
        // A later signal never pushes an earlier due walk back past its own delay, but a burst collapses to one walk.
        Interlocked.Exchange(ref walkDueAt, due);
    }

    private void OnEquip() => Request(Signal.Equip);

    private void OnStateChangedAny(Glamourer.Api.Enums.StateChangeType change)
    {
        if (change is Glamourer.Api.Enums.StateChangeType.Equip or Glamourer.Api.Enums.StateChangeType.Stains
            or Glamourer.Api.Enums.StateChangeType.Design or Glamourer.Api.Enums.StateChangeType.Reset
            or Glamourer.Api.Enums.StateChangeType.BonusItem or Glamourer.Api.Enums.StateChangeType.Other)
            Request(Signal.Equip);
    }

    private void OnRedrawn() => Request(Signal.Redraw);
    private void OnCollectionChanged() => Request(Signal.Collection);
    private void OnPenumbraReady() => Request(Signal.PenumbraReady);
    private void OnPublishedChanged() => Request(Signal.Published);

    private void OnModMoved(string oldDir, string newDir)
    {
        shipsCache.TryRemove(oldDir, out _);
        memo.Clear();
        Request(Signal.ModMoved);
    }

    private void OnModSettingChanged(ModSettingChange change, Guid collection, string modDirectory, bool inherited)
    {
        if (stopped || !Enabled) return;
        if (string.Equals(modDirectory, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase)) return;
        if (change == ModSettingChange.TemporaryMod && string.Equals(modDirectory, Tag, StringComparison.Ordinal)) return;
        if (change == ModSettingChange.Edited && penumbra.GetModDirectory() is { } root && !string.IsNullOrEmpty(modDirectory))
        {
            memo.EvictUnder(Path.Combine(root, modDirectory));
            shipsCache.TryRemove(modDirectory, out _);
        }
        pendingSettings.Enqueue((collection, modDirectory ?? ""));
        var (delay, _) = SignalFor(Signal.ModSetting);
        Interlocked.Exchange(ref walkDueAt, Environment.TickCount64 + delay);
    }

    // ── the frame ───────────────────────────────────────────────────────────

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (stopped) return;

        // Leftovers from an earlier load of the plugin in this game (a dev reload): ours, so ours to take down.
        if (!leftoverRemoved && penumbra.IsAvailable && penumbra.GetPlayerCollectionId() is { } coll)
        {
            leftoverRemoved = true;
            penumbra.RemoveTemporaryMod(Tag, coll, Priority);
        }

        bool enabled = Enabled;
        switch (Transition(wasEnabled, enabled))
        {
            case TransitionKind.Evaluate:
                wasEnabled = true;
                Request(Signal.Enabled);
                break;
            case TransitionKind.Teardown:
                wasEnabled = false;
                TearDown(reload: true, cause: "setting off");
                return;
        }
        if (!enabled) return;

        long due = Interlocked.Read(ref walkDueAt);
        if (due == 0 || Environment.TickCount64 < due) return;
        if (gate.Busy || penumbra.MovePending) return;
        Interlocked.CompareExchange(ref walkDueAt, 0, due);

        bool peek = Interlocked.Exchange(ref peekDue, 0) == 1;
        bool verify = Interlocked.Exchange(ref verifyDue, 0) == 1;
        Task.Run(() =>
        {
            if (!gate.RunWalk(() => Walk(peek, verify)))
                Interlocked.Exchange(ref walkDueAt, Environment.TickCount64 + 100);   // taken meanwhile: try again
        });
    }

    /// <summary>
    /// Proteus's master switch is about to change. Turning it off takes the upgrade down FIRST, without a reload of its
    /// own, and makes the compositor's withdraw a full redraw when in-place reloads are not known to pick materials up:
    /// that redraw is then the single reload.
    /// </summary>
    internal void BeforePluginEnabledChange(bool enabling)
    {
        var (first, force) = OrderMasterSwitch(!enabling, redirects.AnythingHeld, learning.InPlaceLoadsMaterials);
        if (!first) return;
        bool held = redirects.AnythingHeld;
        TearDown(reload: false, cause: "Proteus off");
        wasEnabled = false;
        if (force && held) compositor.RequireFullRedrawOnce();
    }

    /// <summary>
    /// Take every redirect down at once and forget the compositor's memo for those paths. Never waits for a walk: the
    /// prune is owed to the walk holding the gate, or runs now.
    /// </summary>
    private void TearDown(bool reload, string cause)
    {
        var (removed, paths) = redirects.Teardown();
        compositor.ForgetUpstreams(paths);
        gate.RequestPrune();
        escalation.Reset();
        current = new View(MaskGloss: GlossName(gloss), Peeks: Peeks(), Reload: Reloads(), Masks: Masks());
        log.Information("[Proteus] shader upgrade: taken down ({0}; {1} path(s))", cause, paths.Count);
        if (!removed || !reload) return;

        // The user asked for this: it reloads even with auto redraw off.
        bool inPlace = learning.InPlaceLoadsMaterials == true && config.UseInPlaceReload && glamourer.IsAvailable;
        if (inPlace && compositor.ReloadGearInPlace("shader upgrade off")) reloadsInPlace++;
        else
        {
            compositor.RedrawForChangedModel("shader upgrade off");
            reloadsRedraw++;
        }
    }

    // ── a walk ──────────────────────────────────────────────────────────────

    private sealed record HopA(Guid Collection, List<Worn> Worn, Dictionary<string, string> Loaded, int Generation,
                               HashSet<string> Unresolved);

    private T OnFramework<T>(Func<T> work, string step)
    {
        return Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var clock = Stopwatch.StartNew();
            try { return work(); }
            finally
            {
                if (clock.ElapsedMilliseconds > SlowFrameMs)
                    log.Warning("[Proteus] shader upgrade: framework-thread step {0} took {1} ms", step, clock.ElapsedMilliseconds);
            }
        }).GetAwaiter().GetResult();
    }

    /// <summary>Holds the gate throughout. Runs on a worker.</summary>
    private void Walk(bool peekSignal, bool verify)
    {
        try { WalkCore(peekSignal, verify); }
        catch (Exception ex) { log.Error(ex, "[Proteus] shader upgrade: a walk failed"); }
    }

    private void WalkCore(bool peekSignal, bool verify)
    {
        if (stopped || !Enabled) return;
        var modsRoot = penumbra.GetModDirectory();
        var managedRoot = compositor.ManagedModFolder;

        // Which setting changes are worth a peek: judged here, on the worker, where reading a mod's option files is fine.
        bool peek = peekSignal || SettingsWantPeek(modsRoot);

        var a = OnFramework(() => HopAOnFramework(peek, modsRoot, managedRoot), "read");
        if (a == null || stopped) return;

        // An untrusted peek: the redirect stays down and the answers settle over later ticks, or fail open.
        if (a.Unresolved.Count > 0) Settle(a, modsRoot, managedRoot);

        var encoding = config.EnableCompression && textureLoader.CompressionAffordable() ? TexEncoding.Bc7
                                                                                         : TexEncoding.Uncompressed;
        var published = compositor.PublishedGamePaths;
        var inputs = new Inputs
        {
            ModsRoot = modsRoot,
            ManagedRoot = managedRoot!,
            Pid = pid,
            Read = File.ReadAllBytes,
            ResolveTexture = g => penumbra.ResolvePlayer(g),
            DecodeNative = (disk, g) => textureLoader.LoadRawFile(disk, g) is { } raw
                                        && textureLoader.LoadTexBytesAsRgba(raw, g) is { } t
                ? (t.rgba, t.width, t.height) : null,
            Stat = p => { try { var fi = new FileInfo(p); return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc.Ticks) : null; }
                          catch { return null; } },
            MaskFileOk = name => TextureLoader.IsCompleteTex(ShaderUpgradeCopies.PathFor(managedRoot!, name)),
            Memo = memo,
            Gloss = gloss,
            Encoding = encoding,
            Published = published,
        };
        var plan = Build(a.Worn.Where(w => !a.Unresolved.Contains(w.GamePath)), inputs);
        Interlocked.Add(ref decodes, plan.Decodes);
        Interlocked.Add(ref memoHits, plan.MemoHits);
        var left = plan.Left.ToList();
        left.AddRange(a.Unresolved.Select(g => new Left(g, a.Worn.FirstOrDefault(w => w.GamePath == g).Upstream ?? g,
                                                        LeftReason.Unresolved)));

        if (!swept)
        {
            swept = true;
            int n = ShaderUpgradeCopies.Sweep(managedRoot!, pid, ProcessAlive);
            if (n > 0) log.Information("[Proteus] shader upgrade: swept {0} copy(ies) from games no longer running", n);
        }

        // Write, checking before every write that the watcher has not been stopped meanwhile.
        var units = new List<RedirectUnit>();
        foreach (var u in plan.Units)
        {
            if (stopped) return;
            try
            {
                var copy = ShaderUpgradeCopies.WriteMaterial(managedRoot!, u.MaterialCopyName, u.MaterialBytes);
                RedirectMask? mask = null;
                if (u.Mask is { } m)
                {
                    var maskPath = ShaderUpgradeCopies.WriteMask(managedRoot!, m, encoding, textureLoader.WriteTex);
                    memo.Put(m.Key, new MaskMemo.Entry(m.CopyName, m.GamePath, m.Width, m.Height));
                    mask = new RedirectMask(m.GamePath, maskPath);
                }
                units.Add(new RedirectUnit(u.MaterialGamePath, copy, mask, u.Upstream, u.SourceLabel, u.Form,
                                           u.Mask?.Width ?? 0, u.Mask?.Height ?? 0));
            }
            catch (ShaderUpgradeCopies.WriteRefused ex)
            {
                log.Warning("[Proteus] shader upgrade: {0} left on the old shader — {1}", u.SourceLabel, ex.Message);
                left.Add(new Left(u.MaterialGamePath, u.SourceLabel, LeftReason.WriteFailed));
            }
        }
        if (stopped) return;

        OnFramework(() => { HopBOnFramework(a, units, left, verify, modsRoot); return 0; }, "apply");
    }

    private HopA? HopAOnFramework(bool peek, string? modsRoot, string? managedRoot)
    {
        if (stopped || !Enabled) return null;
        bool metaExists = managedRoot != null && File.Exists(Path.Combine(managedRoot, PenumbraModMeta.MetaFile));
        var waiting = WaitingFor(penumbra.IsAvailable, managedRoot, metaExists);
        if (waiting != Waiting.None)
        {
            current = current with { Waiting = waiting == Waiting.Penumbra ? "penumbra" : "managed-mod" };
            if (waiting == Waiting.ManagedMod && !askedManagedMod)
            {
                askedManagedMod = true;
                log.Information("[Proteus] shader upgrade: no managed mod to put copies in yet; asking for one");
                compositor.RequestManagedModForPreview();
            }
            Request(Signal.BlankRetry);
            return null;
        }

        if (penumbra.GetPlayerCollectionId() is not { } collection) return null;
        var loaded = GearMaterials(penumbra.GetActivePlayerResourceMap());
        if (loaded == null)
        {
            // Mid-rebuild, most likely: keep what is redirected, look again shortly.
            if (++blanks < BlanksBeforeNone) Request(Signal.BlankRetry);
            return null;
        }
        blanks = 0;

        var held = redirects.CurrentUnits.Select(u => u.MaterialGamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool collectionMoved = redirects.CurrentCollection is { } was && was != collection;
        var fresh = loaded.Keys.Where(g => !held.Contains(g) || collectionMoved || peek).ToList();
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A peek takes our redirect down for one resolve: trusted when no answer is one of our copies, and then the map
        // goes straight back; otherwise it stays down and settles.
        bool peeking = peek && held.Count > 0 && !collectionMoved;
        if (peeking) redirects.PeekBegin(collection);
        var answers = fresh.Count > 0 ? penumbra.ResolvePlayerPaths(fresh) : new Dictionary<string, string?>();
        if (answers == null)
        {
            if (peeking) redirects.PeekRestore(collection);
            return null;
        }
        bool anyCopy = answers.Values.Any(v => v != null
            && ManagedModPaths.IsShaderUpgradeCopy(StripPenumbraPrefix(v), managedRoot));
        if (peeking)
        {
            if (!anyCopy) { redirects.PeekRestore(collection); Interlocked.Increment(ref peeksTrusted); }
            else foreach (var (g, v) in answers)
                    if (v != null && ManagedModPaths.IsShaderUpgradeCopy(StripPenumbraPrefix(v), managedRoot))
                        unresolved.Add(g);
        }
        foreach (var (g, v) in answers)
            if (!unresolved.Contains(g)
                && !(v != null && ManagedModPaths.IsShaderUpgradeCopy(StripPenumbraPrefix(v), managedRoot)))
                knownUpstream[g] = v;

        var worn = loaded.Keys.Select(g => new Worn(g, knownUpstream.TryGetValue(g, out var up) ? up : null)).ToList();
        int gen = redirects.Begin();
        return new HopA(collection, worn, loaded, gen, unresolved);
    }

    /// <summary>Re-resolve an untrusted peek's paths every 50 ms until two rounds agree with no copy, or fail open.</summary>
    private void Settle(HopA a, string? modsRoot, string? managedRoot)
    {
        var clock = Stopwatch.StartNew();
        IReadOnlyDictionary<string, string?>? previous = null;
        var paths = a.Unresolved.ToList();
        while (!stopped)
        {
            Thread.Sleep(PeekSettle.IntervalMs);
            var now = OnFramework(() => penumbra.ResolvePlayerPaths(paths), "settle");
            if (now == null) continue;
            var step = PeekSettle.Step(previous, now,
                v => v != null && ManagedModPaths.IsShaderUpgradeCopy(StripPenumbraPrefix(v), managedRoot),
                clock.ElapsedMilliseconds);
            if (step == PeekSettle.Outcome.Accept)
            {
                foreach (var (g, v) in now) knownUpstream[g] = v;
                var known = a.Worn.Select(w => a.Unresolved.Contains(w.GamePath)
                    ? w with { Upstream = now.TryGetValue(w.GamePath, out var v) ? v : w.Upstream } : w).ToList();
                a.Worn.Clear();
                a.Worn.AddRange(known);
                a.Unresolved.Clear();
                Interlocked.Increment(ref peeksSettled);
                return;
            }
            if (step == PeekSettle.Outcome.FailOpen)
            {
                Interlocked.Increment(ref peeksFailedOpen);
                log.Warning("[Proteus] shader upgrade: {0} path(s) did not settle after a peek — left on the original "
                          + "material until the next change", paths.Count);
                return;
            }
            previous = now;
        }
    }

    private void HopBOnFramework(HopA a, List<RedirectUnit> units, List<Left> left, bool verify, string? modsRoot)
    {
        if (stopped || !Enabled) return;
        var before = redirects.CurrentUnits;

        var (ready, missing) = UnitsReadyToApply(units, File.Exists, TextureLoader.IsCompleteTex);
        if (missing.Count > 0)
        {
            Interlocked.Add(ref missingBeforeApply, missing.Count);
            foreach (var m in missing) if (m.Mask != null) memo.EvictMask(Path.GetFileName(m.Mask.Copy));
            Request(Signal.Redraw);   // the next walk decodes and writes them again
        }

        if (!redirects.Apply(a.Generation, a.Collection, ready)) return;   // taken down meanwhile, or superseded

        // Verify: every material and mask path must resolve to its own copy; either missing drops the whole unit.
        var asked = ready.SelectMany(u => u.Mask is { } m ? new[] { u.MaterialGamePath, m.GamePath } : [u.MaterialGamePath])
                         .ToList();
        if (asked.Count > 0 && penumbra.ResolvePlayerPaths(asked) is { } resolved)
        {
            var outranked = Outranked(ready, resolved);
            if (outranked.Count > 0)
            {
                ready = ready.Except(outranked).ToList();
                redirects.Apply(a.Generation, a.Collection, ready);
                foreach (var o in outranked) left.Add(new Left(o.MaterialGamePath, o.SourceLabel, LeftReason.Outranked));
                log.Information("[Proteus] shader upgrade: {0} material(s) outranked by another temporary redirect — left alone",
                                outranked.Count);
            }
        }

        // Masks the new map no longer names were just replaced: kept for a while in case the game is still reading one.
        var nowMasks = ready.Where(u => u.Mask != null).Select(u => Path.GetFileName(u.Mask!.Copy))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var b in before)
            if (b.Mask != null && !nowMasks.Contains(Path.GetFileName(b.Mask.Copy))) tail.Add(Path.GetFileName(b.Mask.Copy));

        // Reload when the character still draws a redirected material from another file.
        bool awaiting = false;
        bool needs = NeedsReload(ready, a.Loaded);
        if (verify && lastRoute == ReloadRoute.InPlace)
            learning.Observe(ReloadRoute.InPlace, resolvesToCopy: true, loadedCopy: !needs);
        if (needs)
        {
            bool escalate = false;
            if (verify)
            {
                var stamp = string.Join("|", ready.Select(u => u.MaterialGamePath + ">" + u.MaterialCopy).Order());
                var next = escalation.Fail(stamp);
                if (next == Escalation.Next.GaveUp)
                {
                    Interlocked.Increment(ref gaveUp);
                    log.Warning("[Proteus] shader upgrade: the character still draws the original material after a full "
                              + "redraw — giving up on this set until something changes");
                    needs = false;
                }
                escalate = next == Escalation.Next.Redraw;
            }
            if (needs)
            {
                var route = ChooseReloadRoute(config.AutoRedraw, userRequested: false, compositor.RedrawsHeld,
                                              config.UseInPlaceReload, glamourer.IsAvailable, learning.Broken, escalate);
                lastRoute = route;
                switch (route)
                {
                    case ReloadRoute.None:
                        awaiting = true;
                        break;
                    case ReloadRoute.Deferred:
                        compositor.RedrawForChangedModel("shader upgrade", deferWhileHeld: true);
                        Request(Signal.Verify);
                        break;
                    case ReloadRoute.InPlace:
                        if (compositor.ReloadGearInPlace("shader upgrade")) Interlocked.Increment(ref reloadsInPlace);
                        else
                        {
                            lastRoute = ReloadRoute.Redraw;
                            compositor.RedrawForChangedModel("shader upgrade");
                            Interlocked.Increment(ref reloadsRedraw);
                        }
                        Request(Signal.Verify);
                        break;
                    case ReloadRoute.Redraw:
                        compositor.RedrawForChangedModel("shader upgrade");
                        Interlocked.Increment(ref reloadsRedraw);
                        Request(Signal.Verify);
                        break;
                }
            }
        }
        else if (!verify) escalation.Reset();

        var upgraded = ready.Select(u => new UpgradedEntry(
            u.MaterialGamePath, u.SourceLabel, Path.GetFileName(u.MaterialCopy),
            u.Form == LegacyForm.Diffuse ? "diffuse" : "index-mask",
            u.Mask?.GamePath, u.Mask == null ? null : Path.GetFileName(u.Mask.Copy), u.MaskWidth, u.MaskHeight,
            a.Loaded.TryGetValue(u.MaterialGamePath, out var file)
                && string.Equals(NormalPath(StripPenumbraPrefix(file)), NormalPath(u.MaterialCopy),
                                 StringComparison.OrdinalIgnoreCase))).ToList();
        current = new View(
            Waiting: "none", AwaitingRedraw: awaiting, MaskGloss: GlossName(gloss),
            Upgraded: upgraded,
            Left: left.Select(l => new LeftEntry(l.GamePath, l.Source, ReasonName(l.Reason))).ToList(),
            Peeks: Peeks(), Reload: Reloads(), Masks: Masks(),
            LastAppliedUtc: ready.Count > 0 || before.Count > 0 ? DateTime.UtcNow : current.LastAppliedUtc);
    }

    internal static string ReasonName(LeftReason r) => r switch
    {
        LeftReason.OldTable => "old-table",
        LeftReason.UnsupportedForm => "unsupported-form",
        LeftReason.Unreadable => "unreadable",
        LeftReason.Outranked => "outranked",
        LeftReason.Unresolved => "unresolved",
        LeftReason.Compositor => "compositor",
        _ => "write-failed",
    };

    private PeekStats Peeks() => new(peeksTrusted, peeksSettled, peeksFailedOpen);
    private ReloadStats Reloads() => new(learning.InPlaceLoadsMaterials, reloadsInPlace, reloadsRedraw, gaveUp);
    private MaskStats Masks() => new(memoHits, decodes, pruned, missingBeforeApply);

    /// <summary>The prune, run only under the walk gate (see <see cref="WalkGate"/>) or after the watcher stopped.</summary>
    private void Prune()
    {
        var managedRoot = compositor.ManagedModFolder;
        if (managedRoot == null) return;
        int n = ShaderUpgradeCopies.PruneMasks(managedRoot, redirects.ReferencedMasks(), tail, pid);
        if (n > 0)
        {
            Interlocked.Add(ref pruned, n);
            current = current with { Masks = Masks() };
        }
    }

    // ── peeks for setting changes ───────────────────────────────────────────

    private bool SettingsWantPeek(string? modsRoot)
    {
        bool want = false;
        Guid? player = null;
        bool askedPlayer = false;
        var units = redirects.CurrentUnits;
        var redirected = units.Select(u => u.MaterialGamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceMods = units.Select(u => u.SourceLabel.Split('/')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (pendingSettings.TryDequeue(out var e))
        {
            if (want) continue;   // drain the rest
            if (!askedPlayer) { player = penumbra.GetPlayerCollectionId(); askedPlayer = true; }
            want = PeekFor(e.Collection, player, e.Mod, units.Count > 0, sourceMods,
                           mod => ShipsAny(modsRoot, mod, redirected));
        }
        return want;
    }

    /// <summary>Whether a mod's option files name any of these game paths, cached on the files' newest write.</summary>
    private bool ShipsAny(string? modsRoot, string mod, IReadOnlySet<string> gamePaths)
    {
        if (string.IsNullOrEmpty(modsRoot)) return true;
        var dir = Path.Combine(modsRoot, mod);
        try
        {
            if (!Directory.Exists(dir)) return true;
            var files = Directory.GetFiles(dir, "*.json").Select(f => (Path.GetFileName(f), new FileInfo(f).LastWriteTimeUtc.Ticks))
                                 .ToList();
            long key = PeekCacheKey(files);
            if (!shipsCache.TryGetValue(mod, out var cached) || cached.Key != key)
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, _) in files)
                {
                    if (!(name.Equals("meta.json", StringComparison.OrdinalIgnoreCase)
                          || name.Equals("default_mod.json", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("group_", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var text = File.ReadAllText(Path.Combine(dir, name));
                    foreach (System.Text.RegularExpressions.Match m in
                             System.Text.RegularExpressions.Regex.Matches(text, "\"(chara/[^\"]+\\.mtrl)\""))
                        paths.Add(m.Groups[1].Value);
                }
                cached = (key, paths);
                shipsCache[mod] = cached;
            }
            return gamePaths.Any(cached.Paths.Contains);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    // ── harness and settings ────────────────────────────────────────────────

    /// <summary>Switch the setting, as the checkbox does.</summary>
    internal void SetEnabled(bool enabled)
    {
        lock (config.Gate) config.AutoUpgradeLegacyShaders = enabled;
        config.Save();
    }

    private static bool ProcessAlive(int id)
    {
        try
        {
            using var p = Process.GetProcessById(id);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }
}
