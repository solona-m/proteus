using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Proteus.Interop;
using Proteus.Localization;
using static Proteus.Services.ShaderUpgradePlan;

namespace Proteus.Services;

/// <summary>
/// Moves worn gear from <c>characterlegacy.shpk</c> to Dawntrail's <c>character.shpk</c> as it is put on, when
/// <see cref="Configuration.AutoUpgradeLegacyShaders"/> is on, by rewriting the worn mod's own material files
/// (<see cref="ShaderUpgradeWriter"/>: backed up first, undoable). Turning the setting off only stops further upgrades.
/// <para/>
/// Orchestration only: decisions are in <see cref="ShaderUpgradePlan"/>, every write and undo in the writer. One run at
/// a time (an automatic walk, "Upgrade what I'm wearing now" or "Undo for what I'm wearing"). A run reads the draw object
/// on the framework thread, plans and writes on a worker, and reloads on the framework thread; the worker blocks on its
/// hops, the framework thread never waits for a worker.
/// </summary>
public sealed class ShaderUpgradeWatcher : IDisposable
{
    private const long SlowFrameMs = 8;
    private const int BlanksBeforeNone = 4;

    private readonly CompositorService compositor;
    private readonly PenumbraBridge penumbra;
    private readonly GlamourerBridge glamourer;
    private readonly AutoRefitWatcher autoRefit;
    private readonly TextureLoader textureLoader;
    private readonly Configuration config;
    private readonly IPluginLog log;

    private readonly MaskMemo memo = new();
    private readonly HashSet<string> announced = new(StringComparer.OrdinalIgnoreCase);
    private readonly object scheduleSync = new();

    private volatile bool stopped;
    private bool wasEnabled;
    private int busy, blanks;
    private long walkDueAt, firstRequestAt;
    private int memoHits, decodes;
    private volatile MaskGloss gloss = MaskGloss.InverseSpecular;

    /// <summary>One upgraded material on what is worn.</summary>
    public sealed record UpgradedEntry(string GamePath, string ModDir, string File, string Form,
                                       string? MaskGamePath = null, string? MaskFile = null);

    public sealed record LeftEntry(string GamePath, string Source, string Reason);

    /// <summary>What the last run did.</summary>
    public sealed record RunInfo(string Kind, int ModsChanged, int FilesChanged, int Restored, int Kept, int Refused,
                                 bool Reloaded, bool RedrawRequested, DateTime AtUtc, string Message = "");

    /// <summary>Everything the panel and the harness show, replaced wholesale.</summary>
    public sealed record View(
        bool Enabled = false, bool Busy = false, string Waiting = "none", bool AwaitingRedraw = false,
        string MaskGloss = "inverse-specular",
        IReadOnlyList<UpgradedEntry>? Upgraded = null, IReadOnlyList<LeftEntry>? Left = null,
        int MemoHits = 0, int Decodes = 0, RunInfo? LastRun = null)
    {
        public IReadOnlyList<UpgradedEntry> UpgradedList => Upgraded ?? [];
        public IReadOnlyList<LeftEntry> LeftList => Left ?? [];
    }

    private volatile View current = new();
    public View Current => current with { Enabled = Enabled, Busy = Volatile.Read(ref busy) != 0 };

    public ShaderUpgradeWatcher(CompositorService compositor, PenumbraBridge penumbra, GlamourerBridge glamourer,
                                AutoRefitWatcher autoRefit, TextureLoader textureLoader, Configuration config,
                                IPluginLog log, PlayerSettingsFilter settings)
    {
        this.settings = settings;
        this.compositor = compositor;
        this.penumbra = penumbra;
        this.glamourer = glamourer;
        this.autoRefit = autoRefit;
        this.textureLoader = textureLoader;
        this.config = config;
        this.log = log;

        glamourer.LocalPlayerEquipmentChanged += OnEquip;
        glamourer.LocalPlayerStateChangedAny += OnStateChangedAny;
        penumbra.LocalPlayerRedrawn += OnRedrawn;
        penumbra.PlayerCollectionChanged += OnCollectionChanged;
        // Only changes that can reach what the player wears: not sync plugins' collections, not our own mod.
        settings.Passed += OnModSettingChanged;
        penumbra.PenumbraReady += OnPenumbraReady;
        penumbra.ModMoved += OnModMoved;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    private readonly PlayerSettingsFilter settings;

    /// <summary>Counts runs for the idle I/O summary; null counts nothing.</summary>
    public IoCounters? Io { get; set; }

    public void Dispose()
    {
        // A run checks this before every file and before its reload hop, and writes nothing more.
        stopped = true;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        glamourer.LocalPlayerEquipmentChanged -= OnEquip;
        glamourer.LocalPlayerStateChangedAny -= OnStateChangedAny;
        penumbra.LocalPlayerRedrawn -= OnRedrawn;
        penumbra.PlayerCollectionChanged -= OnCollectionChanged;
        settings.Passed -= OnModSettingChanged;
        penumbra.PenumbraReady -= OnPenumbraReady;
        penumbra.ModMoved -= OnModMoved;
    }

    private bool Enabled => config.PluginEnabled && config.AutoUpgradeLegacyShaders;

    /// <summary>The mask strategy for this session (QA only; never persisted). Affects only files written after it.</summary>
    internal MaskGloss Gloss
    {
        get => gloss;
        set
        {
            gloss = value;
            current = current with { MaskGloss = GlossName(value) };
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

    internal static string ReasonName(LeftReason r) => r switch
    {
        LeftReason.OldTable => "old-table",
        LeftReason.UnsupportedForm => "unsupported-form",
        LeftReason.Unreadable => "unreadable",
        LeftReason.Compositor => "compositor",
        LeftReason.LegacyMod => "legacy-mod",
        LeftReason.MixedSpecular => "mixed-specular",
        LeftReason.SpecularFromOtherMod => "specular-from-other-mod",
        LeftReason.UnsupportedPlacement => "unsupported-placement",
        LeftReason.ProteusPack => "proteus-pack",
        LeftReason.Declined => "declined",
        _ => "write-failed",
    };

    // ── signals ─────────────────────────────────────────────────────────────

    private void Request(Signal s)
    {
        if (stopped || !Enabled) return;
        Schedule(SignalFor(s));
    }

    /// <summary>A burst collapses into one walk, but never later than <see cref="MaxWalkWaitMs"/> after its first signal.</summary>
    private void Schedule(int delayMs)
    {
        lock (scheduleSync)
        {
            var (due, first) = NextDue(Environment.TickCount64, delayMs, Interlocked.Read(ref walkDueAt), firstRequestAt);
            firstRequestAt = first;
            Interlocked.Exchange(ref walkDueAt, due);
        }
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

    private void OnModMoved(string oldDir, string newDir)
    {
        memo.Clear();
        Request(Signal.ModMoved);
    }

    private void OnModSettingChanged(ModSettingChange change, string modDirectory)
    {
        if (change == ModSettingChange.Edited) memo.Clear();
        Request(Signal.ModSetting);
    }

    // ── the frame ───────────────────────────────────────────────────────────

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (stopped) return;
        bool enabled = Enabled;
        switch (Transition(wasEnabled, enabled))
        {
            case TransitionKind.Evaluate:
                wasEnabled = true;
                Request(Signal.Enabled);
                break;
            case TransitionKind.Stop:
                wasEnabled = false;   // stops: nothing already upgraded is touched
                return;
        }
        if (!enabled) return;

        long due = Interlocked.Read(ref walkDueAt);
        if (due == 0 || Environment.TickCount64 < due) return;
        if (Volatile.Read(ref busy) != 0 || penumbra.MovePending) return;
        var a = autoRefit.Activity();
        if (!MayWalk(a.Running, a.Queued, a.WalkDue, a.Undoing))
        {
            // Both write into worn mods on equip: let the refit finish, then look again.
            lock (scheduleSync) Interlocked.Exchange(ref walkDueAt, 0);
            Schedule(SignalFor(Signal.AutoRefitBusy));
            return;
        }
        lock (scheduleSync) Interlocked.CompareExchange(ref walkDueAt, 0, due);
        Start(RunKind.Upgrade);
    }

    /// <summary>"Upgrade what I'm wearing now": lifts the declines on the worn files and upgrades them.</summary>
    public bool UpgradeWornNow() => Start(RunKind.UpgradeNow);

    /// <summary>"Undo for what I'm wearing": puts the worn pieces' files back and (by default) declines them.</summary>
    public bool UndoWorn(bool decline = true) => Start(RunKind.UndoWorn, decline);

    private bool Start(RunKind kind, bool decline = true)
    {
        if (stopped || !MayStart(Volatile.Read(ref busy) != 0)) return false;
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
        Task.Run(() =>
        {
            try { Run(kind, decline); }
            catch (Exception ex) { log.Error(ex, "[Proteus] shader upgrade: a {0} run failed", kind); }
            finally { Interlocked.Exchange(ref busy, 0); }
        });
        return true;
    }

    private T OnFramework<T>(Func<T> work, string step)
        => Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var clock = Stopwatch.StartNew();
            try { return work(); }
            finally
            {
                if (clock.ElapsedMilliseconds > SlowFrameMs)
                    log.Warning("[Proteus] shader upgrade: framework-thread step {0} took {1} ms", step, clock.ElapsedMilliseconds);
            }
        }).GetAwaiter().GetResult();

    // ── a run ───────────────────────────────────────────────────────────────

    private sealed record WornState(List<Worn> Materials, string ModsRoot, Dictionary<string, string> ModNames);

    /// <summary>The worn gear materials, read on the framework thread; null when there is nothing to read yet.</summary>
    private WornState? ReadWorn()
    {
        var modsRoot = penumbra.GetModDirectory();
        if (WaitingFor(penumbra.IsAvailable, modsRoot) != Waiting.None)
        {
            current = current with { Waiting = "penumbra" };
            Request(Signal.BlankRetry);
            return null;
        }
        var loaded = GearMaterials(penumbra.GetActivePlayerResourceMap());
        if (loaded == null)
        {
            if (++blanks < BlanksBeforeNone) Request(Signal.BlankRetry);
            return null;
        }
        blanks = 0;
        var names = penumbra.GetAllModsQuiet() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new WornState(loaded.Select(kv => new Worn(kv.Key, kv.Value)).ToList(), modsRoot!, names);
    }

    private Inputs InputsFor(string modsRoot, TexEncoding encoding) => new()
    {
        ModsRoot = modsRoot,
        ManagedRoot = compositor.ManagedModFolder,
        Read = File.ReadAllBytes,
        DecodeNative = (disk, g) => textureLoader.LoadRawFile(disk, g) is { } raw
                                    && textureLoader.LoadTexBytesAsRgba(raw, g) is { } t
            ? (t.rgba, t.width, t.height) : null,
        Stat = p => { try { var fi = new FileInfo(p); return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc.Ticks) : null; }
                      catch { return null; } },
        ReadOwners = HairPortPlan.ReadOwners,
        IsDeclined = ShaderUpgradeWriter.IsDeclined,
        MaskFileOk = (modRoot, rel) => TextureLoader.IsCompleteTex(Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar))),
        Memo = memo,
        Gloss = gloss,
        Encoding = encoding,
    };

    private void Run(RunKind kind, bool decline)
    {
        if (stopped) return;
        Io?.Count(IoCounters.GearShadersRuns);
        var worn = OnFramework(ReadWorn, "read");
        // A button pressed mid-redraw (or before Penumbra is up) waits for the character rather than doing nothing.
        for (int tries = 0; worn == null && kind != RunKind.Upgrade && tries < BlanksBeforeNone && !stopped; tries++)
        {
            Thread.Sleep(SignalFor(Signal.BlankRetry));
            worn = OnFramework(ReadWorn, "read");
        }
        if (worn == null || stopped)
        {
            if (kind != RunKind.Upgrade)
                current = current with { LastRun = new RunInfo(KindName(kind), 0, 0, 0, 0, 0, false, false, DateTime.UtcNow,
                                                               Strings.Settings.GearShadersWaiting) };
            return;
        }

        var encoding = config.EnableCompression && textureLoader.CompressionAffordable() ? TexEncoding.Bc7
                                                                                         : TexEncoding.Uncompressed;
        var inputs = InputsFor(worn.ModsRoot, encoding);
        if (kind == RunKind.UndoWorn) { RunUndo(worn, inputs, decline); return; }

        if (kind == RunKind.UpgradeNow)
            foreach (var g in WornModFiles(worn, inputs).GroupBy(f => f.ModRoot, StringComparer.OrdinalIgnoreCase))
                ShaderUpgradeWriter.ClearDeclines(g.Key, g.Select(f => f.Rel));

        // A diffuse/specular material an older wind save moved to character.shpk without its diffuse (dark, #65) goes back
        // to the author's file first, so the upgrade below moves it properly in this same run.
        // Counts are the upgrade's own (they feed the chat notice); a mod whose files were only restored is reloaded all
        // the same, through `repaired`.
        var changed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var repaired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool aborted = false;
        foreach (var g in WornModFiles(worn, inputs).GroupBy(f => f.ModRoot, StringComparer.OrdinalIgnoreCase))
        {
            if (stopped || (kind == RunKind.Upgrade && !Enabled)) { aborted = true; break; }
            var restored = MeshVolumeService.RepairWindDiffuse(g.Key, g.Select(f => f.Rel),
                why => log.Warning("[Proteus] shader upgrade: {0}", why));
            if (restored.Count == 0) continue;
            repaired.Add(g.Key);
            log.Information("[Proteus] shader upgrade: restored {0} wind-brushed material(s) in {1} to the author's "
                          + "diffuse/specular file (an older wind save had moved them to character.shpk without their "
                          + "diffuse, drawing them dark); save the wind again to bring back their sway",
                            restored.Count, Path.GetFileName(g.Key));
        }

        // Switched off mid-repair: what was restored is still reloaded below; nothing more is planned. (Stopped — the
        // plugin going away — returns before any reload, as an upgrade does.)
        var (candidates, left) = aborted ? (new List<Candidate>(), new List<Left>()) : Candidates(worn.Materials, inputs);
        var slots = candidates.Where(c => c.SpecularGamePath != null).Select(c => c.SpecularGamePath!)
                              .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var resolvedS = slots.Count == 0
            ? new Dictionary<string, string?>()
            : OnFramework(() => penumbra.ResolvePlayerPaths(slots), "resolve") ?? new Dictionary<string, string?>();
        var plan = Build(candidates, resolvedS, inputs, left);
        Interlocked.Add(ref decodes, plan.Decodes);
        Interlocked.Add(ref memoHits, plan.MemoHits);

        bool rewalk = false;
        foreach (var mod in plan.PerMod)
        {
            if (stopped || (kind == RunKind.Upgrade && !Enabled)) break;
            var outcome = ShaderUpgradeWriter.Apply(mod.ModRoot, mod.Writes,
                () => !stopped && (kind != RunKind.Upgrade || Enabled), textureLoader.WriteTex, encoding);
            rewalk |= outcome.Rewalk;
            foreach (var (rel, reason) in outcome.Left)
                foreach (var w in mod.Writes.Where(w => w.Rel == rel))
                    foreach (var g in w.GamePaths)
                        left.Add(new Left(g, $"{Path.GetFileName(mod.ModRoot)}/{rel}", reason));
            if (outcome.ChangedAny)
            {
                changed[mod.ModRoot] = outcome.Written.Count;
                log.Information("[Proteus] shader upgrade: rewrote {0} legacy material(s) in {1} to character.shpk",
                                outcome.Written.Count, Path.GetFileName(mod.ModRoot));
            }
        }
        if (stopped) return;

        var reload = changed.Keys.Union(repaired, StringComparer.OrdinalIgnoreCase).ToList();
        var (reloaded, redrew) = OnFramework(() => ReloadChanged(reload, kind, worn.ModNames, changed), "reload");
        if (rewalk) Request(Signal.Redraw);
        Publish(worn, inputs, left, new RunInfo(KindName(kind), changed.Count, changed.Values.Sum(), 0, 0, 0,
                                                reloaded, redrew, DateTime.UtcNow),
                awaiting: reload.Count > 0 && !redrew);
    }

    /// <summary>The worn materials' mod files (not game data, not Proteus's own), one per file.</summary>
    private static List<(string ModRoot, string Rel)> WornModFiles(WornState worn, Inputs inputs)
    {
        var files = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in worn.Materials)
        {
            if (Classify(w.GamePath, w.Resolved, worn.ModsRoot, inputs.ManagedRoot, inputs.IsProteusPack) != Source.Mod)
                continue;
            var file = StripPenumbraPrefix(w.Resolved!);
            if (!seen.Add(NormalPath(file))) continue;
            if (HatCompatService.InMods(file, worn.ModsRoot, out var root, out var rel))
                files.Add((root, MeshVolumeService.Rel(rel)));
        }
        return files;
    }

    private void RunUndo(WornState worn, Inputs inputs, bool decline)
    {
        int restored = 0, kept = 0, refused = 0;
        var messages = new List<string>();
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (modRoot, rel) in WornModFiles(worn, inputs))
        {
            if (stopped) return;
            if (ShaderUpgradeWriter.ReadRecord(modRoot) is not { } r
                || !r.Files.Any(f => string.Equals(f.Rel, rel, StringComparison.OrdinalIgnoreCase)))
                continue;
            switch (ShaderUpgradeWriter.RevertOrForget(modRoot, rel, decline, out var why))
            {
                case ShaderUpgradeWriter.Undone.Restored: restored++; changed.Add(modRoot); break;
                case ShaderUpgradeWriter.Undone.Forgotten:
                    kept++;
                    changed.Add(modRoot);
                    if (why.Length > 0) log.Information("[Proteus] shader upgrade: {0}", why);
                    break;
                case ShaderUpgradeWriter.Undone.Nothing: changed.Add(modRoot); break;
                case ShaderUpgradeWriter.Undone.Refused: refused++; messages.Add(why); break;
                case ShaderUpgradeWriter.Undone.Failed:
                    refused++;
                    log.Warning("[Proteus] shader upgrade: undo of {0} failed — {1}", rel, why);
                    break;
            }
        }
        if (stopped) return;
        log.Information("[Proteus] shader upgrade: undo for what is worn — {0} restored, {1} kept as the author's update, "
                      + "{2} refused", restored, kept, refused);
        var (reloaded, redrew) = OnFramework(() => ReloadChanged([.. changed], RunKind.UndoWorn, worn.ModNames, null), "reload");
        Publish(worn, inputs, Candidates(worn.Materials, inputs).Left,
                new RunInfo(KindName(RunKind.UndoWorn), changed.Count, restored + kept, restored, kept, refused, reloaded,
                            redrew, DateTime.UtcNow, string.Join(" ", messages.Distinct())),
                awaiting: false);
    }

    private static string KindName(RunKind k) => k switch
    {
        RunKind.UndoWorn => "undo-worn",
        RunKind.UpgradeNow => "upgrade-now",
        _ => "upgrade",
    };

    /// <summary>
    /// Reload each changed mod (marked as our own edit), say so in chat once per mod per session, and redraw once when
    /// allowed. Framework thread.
    /// </summary>
    private (bool Reloaded, bool Redrew) ReloadChanged(List<string> modRoots, RunKind kind, Dictionary<string, string> names,
                                                       Dictionary<string, int>? counts)
    {
        if (stopped || modRoots.Count == 0) return (false, false);
        bool reloaded = true;
        foreach (var root in modRoots)
        {
            var dir = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            compositor.ExpectOwnModEdit(dir, "the gear shader upgrade");
            var ec = penumbra.ReloadModDirectory(dir);
            reloaded &= ec == PenumbraApiEc.Success;
            if (counts != null && counts.TryGetValue(root, out var n) && ShouldAnnounce(announced, dir))
                Announce(n, names.TryGetValue(dir, out var name) ? name : dir);
        }
        bool redraw = RedrawAfter(kind, config.AutoRedraw, true);
        // Never held for the auto refit's batch (only its own work is): the walk already waits until it is idle.
        if (redraw) compositor.RedrawForChangedModel("shader upgrade");
        return (reloaded, redraw);
    }

    private void Announce(int count, string modName)
    {
        try
        {
            var text = string.Format(ProteusLoc.Localize("Chat.ShaderUpgrade.Fmt",
                "[Proteus] Moved {0} old-shader material(s) in \"{1}\" to Dawntrail's shader (each file backed up). "
              + "Undo under /proteus > Settings > Gear shaders."), count, modName);
            Plugin.ChatGui.Print(new Dalamud.Game.Text.SeStringHandling.SeStringBuilder().AddUiForeground(text, 45).Build());
        }
        catch (Exception ex) { log.Warning(ex, "[Proteus] shader upgrade: could not announce in chat"); }
    }

    /// <summary>What is worn now: upgraded files (recorded and still as Proteus left them), and what was left.</summary>
    private void Publish(WornState worn, Inputs inputs, List<Left> left, RunInfo run, bool awaiting)
    {
        var upgraded = new List<UpgradedEntry>();
        foreach (var w in worn.Materials)
        {
            if (Classify(w.GamePath, w.Resolved, worn.ModsRoot, inputs.ManagedRoot, inputs.IsProteusPack) != Source.Mod) continue;
            var file = StripPenumbraPrefix(w.Resolved!);
            if (!HatCompatService.InMods(file, worn.ModsRoot, out var root, out var rel)) continue;
            rel = MeshVolumeService.Rel(rel);
            if (!ShaderUpgradeWriter.IsUpgraded(root, rel)) continue;
            var e = ShaderUpgradeWriter.ReadRecord(root)?.Files
                        .FirstOrDefault(f => string.Equals(f.Rel, rel, StringComparison.OrdinalIgnoreCase));
            upgraded.Add(new UpgradedEntry(w.GamePath, Path.GetFileName(root), rel, e?.Form ?? "index-mask",
                                           e?.Mask?.GamePath, e?.Mask?.Rel));
        }
        current = new View(
            Waiting: "none", AwaitingRedraw: awaiting, MaskGloss: GlossName(gloss), Upgraded: upgraded,
            Left: left.Select(l => new LeftEntry(l.GamePath, l.Source, ReasonName(l.Reason))).ToList(),
            MemoHits: memoHits, Decodes: decodes, LastRun: run);
    }

    // ── harness and settings ────────────────────────────────────────────────

    /// <summary>Switch the setting, as the checkbox does.</summary>
    internal void SetEnabled(bool enabled)
    {
        lock (config.Gate) config.AutoUpgradeLegacyShaders = enabled;
        config.Save();
    }
}
