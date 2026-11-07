using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;
using Penumbra.Api.Enums;
using Proteus.Interop;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>Local-player redraws: how many, and when the last one came. Thread-safe.</summary>
internal sealed class RedrawCounter
{
    private long count;
    private long lastTicks;

    public void Note()
    {
        Interlocked.Exchange(ref lastTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref count);
    }

    public long Count => Interlocked.Read(ref count);

    public DateTime? LastUtc => Interlocked.Read(ref lastTicks) is var t and > 0 ? new DateTime(t, DateTimeKind.Utc) : null;
}

/// <summary>
/// The harness's one door into the game: Dalamud, Penumbra, the compositor, auto refit and the Studio. Kept thin —
/// it reads and forwards; every decision lives in the tested core. UNVERIFIED in game until adversarial QA runs it.
/// </summary>
internal sealed class GameHarnessAdapter : IHarnessGame, IDisposable
{
    private readonly PenumbraBridge penumbra;
    private readonly GlamourerBridge glamourer;
    private readonly CompositorService compositor;
    private readonly AutoRefitWatcher autoRefit;
    private readonly ShaderUpgradeWatcher shaderUpgrade;
    private readonly VariantRepointWatcher variantRepoint;
    private readonly HairPortWatcher hairPort;
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly ITextureProvider textures;
    private readonly ITextureReadbackProvider readback;
    private readonly Configuration config;
    private readonly UVRemapService uvRemap;
    private readonly Func<string, byte[]?> readGameFile;
    private readonly Action<string> onModChanged;
    private readonly IPluginLog log;
    private readonly LiveModelDump models;
    private readonly DesignBindingService designBindings;
    private readonly OverlayEditRouter editRouter;
    private readonly PresetService presets;
    private readonly SidecarDiscoveryService discovery;
    private readonly Glamourer.Api.IpcSubscribers.ApplyDesign applyDesign;
    private readonly Glamourer.Api.IpcSubscribers.ApplyState applyState;
    private long designApplied;

    private readonly RedrawCounter redraws = new();
    private long nextNameSample;

    /// <summary>Where the once-a-second character-name sample goes while the harness listens.</summary>
    public SessionNames? NameSink { get; set; }

    public GameHarnessAdapter(PenumbraBridge penumbra, GlamourerBridge glamourer, CompositorService compositor,
                              AutoRefitWatcher autoRefit, ShaderUpgradeWatcher shaderUpgrade,
                              VariantRepointWatcher variantRepoint, HairPortWatcher hairPort,
                              IObjectTable objects, IClientState clientState, IFramework framework,
                              ITextureProvider textures, ITextureReadbackProvider readback, Configuration config,
                              UVRemapService uvRemap, Func<string, byte[]?> readGameFile, Action<string> onModChanged,
                              IPluginLog log, IDalamudPluginInterface pluginInterface, DesignBindingService designBindings,
                              OverlayEditRouter editRouter, PresetService presets, SidecarDiscoveryService discovery,
                              IStudioHarness? studio = null)
    {
        Studio = studio;
        this.discovery = discovery;
        this.penumbra = penumbra;
        this.glamourer = glamourer;
        this.compositor = compositor;
        this.autoRefit = autoRefit;
        this.shaderUpgrade = shaderUpgrade;
        this.variantRepoint = variantRepoint;
        this.hairPort = hairPort;
        this.objects = objects;
        this.clientState = clientState;
        this.framework = framework;
        this.textures = textures;
        this.readback = readback;
        this.config = config;
        this.uvRemap = uvRemap;
        this.readGameFile = readGameFile;
        this.onModChanged = onModChanged;
        this.log = log;
        models = new LiveModelDump(objects);
        this.designBindings = designBindings;
        this.editRouter = editRouter;
        this.presets = presets;
        // Created here, not in GlamourerBridge: apply and state-write IPC never ship outside a harness build.
        applyDesign = new Glamourer.Api.IpcSubscribers.ApplyDesign(pluginInterface);
        applyState = new Glamourer.Api.IpcSubscribers.ApplyState(pluginInterface);
        glamourer.LocalPlayerStateFinalized += OnStateFinalized;
        penumbra.LocalPlayerRedrawn += redraws.Note;
        framework.Update += OnUpdate;
    }

    /// <summary>Once a second, no IPC: the local character's name, for the log scrub.</summary>
    private void OnUpdate(IFramework _)
    {
        if (NameSink is not { } sink) return;
        long now = Environment.TickCount64;
        if (now < nextNameSample) return;
        nextNameSample = now + 1000;
        try { sink.Add(objects.LocalPlayer?.Name.TextValue, NameKind.Character); }
        catch (Exception) { /* mid-zone: next second */ }
    }

    public long RedrawCount => redraws.Count;

    public DateTime? LastRedrawUtc => redraws.LastUtc;

    public string ManagedModDir => SidecarDiscoveryService.ManagedModDir;

    public ShaderUpgradeWatcher.View ShaderUpgrade() => shaderUpgrade.Current;

    public (bool Enabled, string MaskGloss) SetShaderUpgrade(bool? enabled, string? maskGloss)
    {
        if (maskGloss != null && ShaderUpgradeWatcher.ParseGloss(maskGloss) is { } g) shaderUpgrade.Gloss = g;
        if (enabled is { } on) shaderUpgrade.SetEnabled(on);
        return (config.AutoUpgradeLegacyShaders, ShaderUpgradeWatcher.GlossName(shaderUpgrade.Gloss));
    }

    public bool StartShaderUpgradeRun(bool undoWorn, bool decline)
        => undoWorn ? shaderUpgrade.UndoWorn(decline) : shaderUpgrade.UpgradeWornNow();

    public VariantRepointWatcher.View VariantRepoint() => variantRepoint.Current;

    public bool SetVariantRepoint(bool enabled)
    {
        variantRepoint.SetEnabled(enabled);
        return config.AutoRepointVariants;
    }

    public bool VariantRepointBlocked() => variantRepoint.OthersBusy();

    public bool StartVariantRepointRun(bool undoWorn, bool decline)
        => undoWorn ? variantRepoint.UndoWorn(decline) : variantRepoint.RepointWornNow();

    public HairPortInfo HairPort()
        => new(hairPort.Current, hairPort.Scan, hairPort.CheckOwed, config.AutoHairPort, config.EarlessMiqoteHair);

    public byte[]? ReadGameFile(string gamePath) => readGameFile(gamePath);

    public bool IsHairMod(string modRoot) => HairPortJob.IsHairMod(modRoot);

    public HealthInfo Health() => new(penumbra.IsAvailable, clientState.IsLoggedIn, config.PluginEnabled);

    public RawState State()
    {
        CharacterInfo? character = null;
        var (loaded, _) = models.LoadedModels();
        var resourceMap = penumbra.GetActivePlayerResourceMap();
        if (objects.LocalPlayer is ICharacter c && c.Customize is { Length: > 4 } cz)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (resourceMap != null) foreach (var gamePaths in resourceMap.Values) paths.UnionWith(gamePaths);
            foreach (var (_, file) in loaded) if (file != null) paths.Add(LiveCharacter.FilePath(file));
            string? code = DrawnModelPaths.DrawnRaceCodeFromModels(paths);
            character = new CharacterInfo(cz[0], cz[4], cz[1], cz[2], code == null ? null : "c" + code);
        }
        var view = autoRefit.Current;
        return new RawState(character, penumbra.GetPlayerCollectionId(), loaded, resourceMap, penumbra.GetModDirectory(),
                            compositor.Activity(), compositor.LastResult, compositor.LastRefresh,
                            new AutoRefitInfo(view, autoRefit.Activity()))
        {
            Gear = GearNow(),
        };
    }

    // ── equip and hold ──────────────────────────────────────────────────────

    public IReadOnlyDictionary<string, Worn>? GearNow()
        => objects.LocalPlayer == null ? null : HarnessGear.Parse(glamourer.GetObjectState(0));

    public bool PlayerPresent() => objects.LocalPlayer != null;

    public string? SetGear(string slot, ulong id, byte dye1, byte dye2)
        => glamourer.SetGearItem(slot, id, dye1, dye2) is { } ec
            ? ec == Glamourer.Api.Enums.GlamourerApiEc.Success ? null : ec.ToString()
            : "unavailable";

    public IReadOnlyCollection<string> CarrierSlots() => compositor.InjectedCarrierSlots;

    public Guid? PlayerCollection() => penumbra.GetPlayerCollectionId();

    public PenumbraBridge.TemporaryHold? QueryHold(Guid collection, string modDir)
        => penumbra.QueryTemporaryModSettings(collection, modDir);

    public PenumbraBridge.SettingsRead ReadSettings(Guid collection, string modDir)
        => penumbra.ReadModSettings(collection, modDir);

    public string? SetHold(Guid collection, string modDir, bool enabled, int priority,
                           IReadOnlyDictionary<string, List<string>> options, string source, int key)
        => penumbra.SetTemporaryModSettings(collection, modDir, enabled, priority, options, source, key) is var ec
           && ec == PenumbraApiEc.Success ? null : ec.ToString();

    public string? ClearHold(Guid collection, string modDir)
        => penumbra.ClearTemporaryModSettings(collection, modDir) is var ec && ec == PenumbraApiEc.Success ? null : ec.ToString();

    public string SweepHolds(Guid collection, int key) => penumbra.RemoveAllTemporaryModSettings(collection, key).ToString();

    public void RequestRedraw() => compositor.RedrawForChangedModel("test harness");

    /// <summary>The Studio panel, in harness builds; its members run on the framework thread.</summary>
    public IStudioHarness? Studio { get; }

    public (IReadOnlyCollection<string> Collections, string? Character) NamesNow()
        => ((IReadOnlyCollection<string>?)penumbra.GetCollections()?.Values.ToList() ?? [],
            objects.LocalPlayer?.Name.TextValue);

    public ModList? Mods()
        => penumbra.GetAllMods() is { } mods && penumbra.GetModDirectory() is { } root ? new ModList(mods, root) : null;

    public AutoRefitInfo AutoRefit() => new(autoRefit.Current, autoRefit.Activity());

    public bool IsLegacyFolder(string modRoot) => PenumbraModMeta.IsLegacyFolder(modRoot);

    public IReadOnlyList<(string Key, string Body)> OfferedKeys(string modRoot)
    {
        var (pieces, _, _) = SizeSetRefit.ReadPieces(modRoot, readGameFile, config.SizeSetEnds);
        return SizeSetRefit.Offered(pieces, modRoot).Select(c => (c.Key, c.Body)).ToList();
    }

    public IReadOnlySet<string> InstalledKeys(ModList mods)
    {
        var bodies = autoRefit.Index.RefreshState(mods.Mods, mods.ModsRoot).List;
        return SupportedBodies.Choices.Select(c => c.Key).Where(k => SupportedBodies.Installed(k, bodies))
                              .ToHashSet(StringComparer.Ordinal);
    }

    public RefitOptions Options(bool remake) => new(config.SizeSetYas, config.SizeSetEnds, config.SizeSetPiercings, remake);

    public SizeSetLaunch.Prepared PrepareSizeSet(string modRoot, IReadOnlyList<string> keys, RefitOptions options, ModList mods)
        => SizeSetLaunch.Prepare(mods.Mods, mods.ModsRoot, penumbra.ResolvePlayerPaths, modRoot, keys, options.Yas,
                                 options.Remake, options.Ends, options.Piercings, null, readGameFile, log);

    public Task<SizeSetLaunch.Outcome> RunSizeSet(SizeSetLaunch.Prepared prepared, Action<string> progress, Action afterChange,
                                                  Action<Action> postToFramework, CancellationToken ct)
        => SizeSetLaunch.Start(prepared, autoRefit.Index, uvRemap, log, progress, afterChange, postToFramework, ct);

    public AfterChangeResult AfterModChange(string modDir)
    {
        compositor.ExpectOwnModEdit(modDir, "the test harness");
        bool reloaded = penumbra.ReloadModDirectory(modDir) == PenumbraApiEc.Success;
        long baseline = RedrawCount;
        compositor.RedrawForChangedModel("test harness");
        try { onModChanged(modDir); }
        catch (Exception e) { log.Warning(e, "[Proteus] test harness: the Studio could not refresh {0}", modDir); }
        return new AfterChangeResult(reloaded, true, baseline);
    }

    public uint ViewportId() => ImGui.GetMainViewport().ID;

    public async Task<RawFrame> CaptureRaw(uint viewportId, bool includeUi, CancellationToken ct)
    {
        var wrap = await textures.CreateFromImGuiViewportAsync(
            new ImGuiViewportTextureArgs { ViewportId = viewportId, TakeBeforeImGuiRender = !includeUi, KeepTransparency = false },
            "proteus-test-harness", ct).ConfigureAwait(false);
        // Disposed here, however the readback ends (a cancelled token included), rather than trusting the readback to.
        using (wrap)
        {
            var (spec, data) = await readback.GetRawImageAsync(wrap, new TextureModificationArgs { DxgiFormat = FramePng.B8G8R8A8Unorm },
                                                              leaveWrapOpen: true, ct).ConfigureAwait(false);
            return new RawFrame(spec.Width, spec.Height, spec.Pitch, spec.DxgiFormat, data);
        }
    }

    // ── design binding ──────────────────────────────────────────────────────

    public IReadOnlyDictionary<Guid, string>? Designs() => glamourer.IsAvailable ? glamourer.GetDesigns() : null;

    public JObject? DesignJson(Guid id) => glamourer.GetDesign(id);

    public JObject? GlamourerState() => objects.LocalPlayer == null ? null : glamourer.GetObjectState(0);

    public string? ApplyDesign(Guid id)
    {
        try
        {
            var ec = applyDesign.Invoke(id, 0, 0, ApplyFlagEx.DesignDefault);
            return ec == GlamourerApiEc.Success ? null : ec.ToString();
        }
        catch (Exception e)
        {
            log.Warning(e, "[Proteus] test harness: Glamourer's ApplyDesign failed");
            return "unavailable";
        }
    }

    public long DesignAppliedCount => Interlocked.Read(ref designApplied);

    public string? RestoreGlamourerState(JObject state, IReadOnlyCollection<string> skipSlots, bool skipGlasses)
    {
        var copy = (JObject)state.DeepClone();
        if (copy["Equipment"] is JObject equip)
            foreach (var slot in skipSlots)
                if (equip[slot] is JObject s) { s["Apply"] = false; s["ApplyStain"] = false; }
        if (skipGlasses && copy["Bonus"]?["Glasses"] is JObject g) g["Apply"] = false;

        designBindings.HarnessSuppressApplySignals();   // the restore raises DesignApplied: never read as an apply
        try
        {
            var ec = applyState.Invoke(copy, 0, 0, ApplyFlag.Once | ApplyFlag.Equipment | ApplyFlag.Customization);
            return ec == GlamourerApiEc.Success ? null : ec.ToString();
        }
        catch (Exception e)
        {
            log.Warning(e, "[Proteus] test harness: Glamourer's ApplyState failed");
            return "unavailable";
        }
    }

    public DesignBindingService.Carriers CarriersNow()
    {
        var glasses = compositor.InjectedGlassesItemId;
        return new DesignBindingService.Carriers(glasses, compositor.InjectedCarrierItemIds, compositor.InjectedCarrierSlots,
                                                 GlassesSlotOwned: glasses != null);
    }

    public long EchoMsLeft() => designBindings.HarnessEchoMsLeft();

    public void QuiesceBinding() => designBindings.HarnessQuiesce();

    public HarnessBindingView BindingView(string? mod, string? group, string? option) => designBindings.HarnessView(mod, group, option);

    public IReadOnlyList<string>? DesignFields(Guid id) => designBindings.HarnessDesignFields(id);

    public bool IsBound(Guid id) => designBindings.HarnessIsBound(id);

    public bool PresetsPinned() => presets.Overrides.Active;

    public BindingEditState EditState(string modDir)
        => new(config.PluginEnabled && config.DesignBindingEnabled, designBindings.ActiveDesignId != null,
               designBindings.IsOverrideActiveFor(modDir), editRouter.IsPresetDriven(modDir));

    public List<ColorTableRowPreset>? LiveRows(string modDir, string? group, string? option)
        => editRouter.PeekOverrideRows(modDir, group, option);

    public bool SetLiveRows(string modDir, string? group, string? option, List<ColorTableRowPreset> rows)
    {
        if (!editRouter.SetOverrideRows(modDir, group, option, rows)) return false;
        compositor.TriggerRecomposite("test harness: binding edit");
        return true;
    }

    public bool ClearLiveOption(string modDir, string? group, string? option)
    {
        if (!editRouter.ClearOptionOverride(modDir, group, option)) return false;
        compositor.TriggerRecomposite("test harness: binding clear");
        return true;
    }

    public HarnessLiveSnapshot LiveSnapshot() => designBindings.HarnessSnapshotLive();

    public void RestoreLive(HarnessLiveSnapshot snapshot) => designBindings.HarnessRestoreLive(snapshot);

    public IReadOnlySet<string> ProteusDirs() => designBindings.HarnessProteusDirs();

    public IReadOnlyDictionary<string, PermanentSetting>? PermanentSettings(Guid collection)
    {
        if (penumbra.GetCollectionModSettings(collection, ignoreTemporary: true) is not { } all) return null;
        return all.ToDictionary(kv => kv.Key,
            // An inherited mod keeps the values it inherits (a partial write starts from them), with Own false.
            kv => new PermanentSetting(kv.Value.Enabled, kv.Value.Priority, kv.Value.Options, Own: !kv.Value.Inherited),
            StringComparer.OrdinalIgnoreCase);
    }

    public string? WritePermanent(Guid collection, string modDir, PermanentSetting setting)
    {
        var codes = new List<PenumbraApiEc>();
        using (compositor.SuppressModSettingEvents())
        {
            if (!setting.Own)
                codes.Add(penumbra.SetModInherited(collection, modDir));   // back to no setting of its own
            else
            {
                codes.Add(penumbra.SetModEnabled(collection, modDir, setting.Enabled));
                codes.Add(penumbra.SetModPriority(collection, modDir, setting.Priority));
                foreach (var (group, options) in setting.Options)
                    codes.Add(penumbra.SetModOption(collection, modDir, group, options));
            }
        }
        var bad = codes.Where(ec => ec is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged)).ToList();
        if (bad.Count == 0) { compositor.TriggerRecomposite("test harness: permanent mod setting"); return null; }
        return bad[0].ToString();
    }

    public bool CompositorIdle() => CompositorPhase.Of(compositor.Activity()) == "idle";

    public IReadOnlyList<string> AutoWritersOn()
    {
        var on = new List<string>();
        // The effective switch: the harness's session override counts.
        if (autoRefit.EffectivelyEnabled) on.Add("auto refit");
        if (config.AutoUpgradeLegacyShaders) on.Add("gear shader upgrade");
        if (config.AutoRepointVariants) on.Add("Gear variants");
        if (config.AutoHairPort) on.Add("auto hair port");
        if (config.AutoHatCompat) on.Add("auto hat compat");
        return on;
    }

    // ── idle disk I/O checks ────────────────────────────────────────────────

    public (bool Latched, int Step, long NextDueInMs) Boot() => compositor.HarnessBootState();

    public void RearmBoot() => compositor.HarnessRearmBoot();

    public IReadOnlyList<(Guid Id, bool Player)>? Collections()
    {
        if (penumbra.GetCollections() is not { } all) return null;
        var player = penumbra.GetPlayerCollectionId();
        return all.Keys.Select(id => (id, id == player)).ToList();
    }

    public (bool Config, bool? Override, bool Effective) AutoRefitEnabled()
        => (config.AutoRefitEnabled, autoRefit.HarnessOverride, autoRefit.EffectivelyEnabled);

    public void SetAutoRefitOverride(bool? enabled) => autoRefit.HarnessOverride = enabled;

    public IReadOnlyList<string> TakeRefitsWhileOverridden() => autoRefit.HarnessTakeRefitsWhileOverridden();

    public (bool Built, bool Stale, bool Refreshing, long Generation) BodyIndex() => autoRefit.HarnessBodyIndexState();

    public (long WindowAgeMs, IReadOnlyDictionary<string, long> Counts)? Io() => compositor.Io?.Snapshot();

    public OverlayEntry? DiscoverEntry(string modDir)
        => discovery.DiscoverAll().FirstOrDefault(e => string.Equals(e.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase));

    public bool SaveSidecar(OverlayEntry entry) => discovery.SaveMetadata(entry);

    public bool HasStaged(string modDir) => discovery.HarnessHasStaged(modDir);

    public void ForgetSidecar(string modDir) => discovery.Forget(modDir);

    public void Recomposite(string reason) => compositor.TriggerRecomposite(reason);

    private void OnStateFinalized(StateFinalizationType type)
    {
        if (type == StateFinalizationType.DesignApplied) Interlocked.Increment(ref designApplied);
    }

    public void Dispose()
    {
        glamourer.LocalPlayerStateFinalized -= OnStateFinalized;
        penumbra.LocalPlayerRedrawn -= redraws.Note;
        framework.Update -= OnUpdate;
        NameSink = null;
    }
}
