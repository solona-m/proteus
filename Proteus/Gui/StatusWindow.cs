using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Proteus.Interop;
using Proteus.Localization;
using Proteus.Services;
using StbImageSharp;

namespace Proteus.Gui;

public class StatusWindow : Window
{
    private readonly SettingsTab settingsTab;
    private readonly BindingsTab bindingsTab;
    private readonly ModsTab modsTab;
    private readonly ExportTab exportTab;
    private readonly CreateTab createTab;
    private readonly ImportTab importTab;

    /// <summary>Pumps the Import tab's background work; the plugin calls it every frame and once on unload.</summary>
    public void TickImport(bool unloading = false) => importTab.TickImport(unloading);
    /// <summary>How lit the wearer is, for the readout in Settings; null hides the readout.</summary>
    public static Proteus.Interop.SceneLightService? SceneLight { get; set; }

    private readonly CompositorService compositor;
    private readonly SidecarDiscoveryService discovery;
    private readonly PenumbraBridge penumbra;
    private readonly Configuration config;
    private readonly DesignBindingService designBindings;
    private readonly PresetService presets;
    private readonly OverlayEditRouter editRouter;

    /// <summary>The named-looks strip at the top of the colour editor.</summary>
    private readonly PresetBar presetBar;
    /// <summary>The one file dialog every tab shares; Draw pumps it each frame.</summary>
    private readonly FileDialogManager _fileDialog = new();
    private readonly UVMapDownloadService uvMapDl;
    private readonly UVRemapService uvRemap;
    private readonly ModCreationService modCreation;
    private readonly OnionImportService onionImport;
    private readonly ContentImportService contentImport;
    private readonly LuminisImportService luminisImport;
    private readonly EmissiveSkinImportService emissiveImport;
    private readonly EyeImportService eyeImport;
    // Decodes a content pack's own index .tex so the colour grid can say which rows it samples.
    private readonly TextureLoader textureLoader;
    private readonly ModExportService modExport;
    // Turns a mod's own geometry into on/off switches; works on any installed mod.
    private readonly PartsPanel parts;

    /// <summary>Drawn in Settings, and only while <c>AutoHatCompat</c> is on.</summary>
    private readonly HatCompatPanel hatCompat;

    // One-shot, set by the plugin-installer gear icon: the next Draw opens on Settings.
    private bool _forceSettingsTab;

    // ── sizing ─────────────────────────────────────────────────────────────────────────────────────────
    // Until the user drags the window it fits itself to each tab's controls on tab change; after that it keeps their size.
    // Whether the Studio tab drew last frame.
    private bool _studioDrawn;
    // Which tab drew last frame and this frame. A tab's selection is only known inside Draw, so its fit starts the frame after.
    private string? _lastTab, _tabDrawn;
    // Frames left in the current auto-fit; 0 when not fitting.
    private int _fitFrames;
    // The Studio controls' height the window was last fitted to; reset when the tab changes.
    private float _studioFitHeight;
    // The first frame of a fit, which also resets the width — see PreDraw.
    private bool _fitStarting;
    // Put the user's remembered size back on the next PreDraw (first open).
    private bool _restoreSize = true;
    // Frames during which a size change is the plugin's own doing, not the user dragging.
    private int _ownResizeFrames;
    // Live window size, unscaled: the host multiplies Size by the global scale.
    private Vector2 _togglesSize;
    private bool _sizeDirty;
    private long _sizeChangedAt;
    // Height DrawLastResult took last frame, so the Toggles tab knows how much to leave under itself.
    private float _footerReserve;

    // Whether the model viewer was on screen last frame, so only its appearance triggers a grow.
    private bool _modelWasShowing;
    // One-shot: grow the window next PreDraw, because the model viewer just appeared.
    private bool _growForModel;
    // One-shot: once the grown size has landed, pull the window back on screen if it now hangs off an edge.
    private bool _keepOnScreen;

    // Absolute index-texture path → rows that appear in it; cleared on each popup open.
    private readonly Dictionary<string, ContentIndexTexture.Scan> _indexRowCache = new();
    /// <summary>Mod whose colour editor window is open, or null. A window rather than a popup, so it survives clicks into the game.</summary>
    private string? _colorWindowMod;

    /// <summary>Open the colour editor for a mod, or close it when it is already open for that mod.</summary>
    internal void ToggleColorWindow(string modDirectory)
        => _colorWindowMod = _colorWindowMod == modDirectory ? null : modDirectory;
    // Mod dir → "group\0option" the colour editor is scoped to; by identity so it follows the option when reordered.
    private readonly Dictionary<string, string> _colorEditorSel = new();
    // Identity of the overlay tab currently being dragged to restack (payload carries only a marker).
    private (string Mod, string Group, string Option)? _stackDragSrc;

    /// <summary>The drag payload ImGui requires but we never read. Static, because a stackalloc inside the per-tab loop
    /// accumulates a frame per iteration.</summary>
    private static readonly byte[] StackDragMarker = new byte[1];

    /// <summary>Penumbra group → ordinal per mod, memoised: the tab strip needs it every frame and reading it walks the mod folder.</summary>
    private readonly Dictionary<string, Dictionary<string, int>> _groupOrderCache = new(StringComparer.OrdinalIgnoreCase);
    // mod|material → colour rows that material's index texture selects; cleared when the colour window reopens.
    private readonly Dictionary<string, ContentIndex> _contentIndexCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mods whose cold-boot glow-recipe warmup has fired, so it runs once per mod per session.</summary>
    private readonly HashSet<string> _glowWarmedMods = new(StringComparer.OrdinalIgnoreCase);
    // Key: editor scope → which color table row (1–16) is open in the editor.
    private readonly Dictionary<string, int> _rowSelection = new();
    // Wait this long after the last colour edit before recompositing; the on-screen swatches update live.
    private const int ColorEditDebounceMs = 400;

    public StatusWindow(
        CompositorService compositor,
        SidecarDiscoveryService discovery,
        PenumbraBridge penumbra,
        Configuration config,
        DesignBindingService designBindings,
        PresetService presets,
        OverlayEditRouter editRouter,
        UVMapDownloadService uvMapDl,
        UVRemapService uvRemap,
        ModCreationService modCreation,
        OnionImportService onionImport,
        ContentImportService contentImport,
        LuminisImportService luminisImport,
        EmissiveSkinImportService emissiveImport,
        EyeImportService eyeImport,
        ModExportService modExport,
        TextureLoader textureLoader,
        PartsPanel parts,
        HatCompatWatcher hatCompatWatcher,
        LogExportService logExport)
        // "###ProteusStatus" is the stable window id; the title shows the assembly version, not the dev BuildNumber.
        : base($"Proteus  v{typeof(Plugin).Assembly.GetName().Version}###ProteusStatus", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.compositor     = compositor;
        this.discovery      = discovery;
        this.penumbra       = penumbra;
        this.config         = config;
        this.designBindings = designBindings;
        this.presets        = presets;
        this.editRouter     = editRouter;
        this.uvMapDl        = uvMapDl;
        this.uvRemap        = uvRemap;
        this.modCreation    = modCreation;
        this.onionImport    = onionImport;
        this.contentImport  = contentImport;
        this.luminisImport  = luminisImport;
        this.emissiveImport = emissiveImport;
        this.eyeImport      = eyeImport;
        this.textureLoader  = textureLoader;
        this.modExport      = modExport;
        this.parts          = parts;

        // Shares this window's one FileDialogManager, which Draw pumps every frame.
        presetBar = new PresetBar(presets, penumbra, _fileDialog, config, Plugin.Log);
        importTab = new ImportTab(contentImport, luminisImport, this, emissiveImport, eyeImport, onionImport, _fileDialog, config, discovery, presets);
        createTab = new CreateTab(modCreation, _fileDialog);
        exportTab = new ExportTab(_fileDialog, config, compositor, modExport);
        modsTab = new ModsTab(penumbra, compositor, this, presets, designBindings, config);
        bindingsTab = new BindingsTab(config, designBindings, penumbra);
        hatCompat = new HatCompatPanel(hatCompatWatcher, config);
        settingsTab = new SettingsTab(config, compositor, discovery, designBindings, hatCompat, logExport);

        SizeConstraints = AutoFitConstraints;

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Comments,
            IconOffset = new Vector2(2f, 1f),
            ShowTooltip = () => ImGui.SetTooltip(DiscordUrl),
            Click = _ =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DiscordUrl) { UseShellExecute = true }); }
                catch { /* opening a browser is best-effort */ }
            },
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(2f, 1f),
            ShowTooltip = () => ImGui.SetTooltip(Strings.Band.SettingsTip),
            Click = _ => OpenToSettings(),
        });
    }

    /// <summary>The window on every tab but Toggles: fits its content, no resize grip. Unscaled: the host applies the global scale.</summary>
    private static readonly WindowSizeConstraints AutoFitConstraints = new()
    {
        // Wide enough for the mod table, so sparser tabs don't shrink the window.
        MinimumSize = new Vector2(520, 80),
        // Tall enough for the Settings tab plus the header band without a scrollbar.
        MaximumSize = new Vector2(1100, 774),
    };

    /// <summary>The window when not fitting itself: user-resizable, with the same 520 floor as <see cref="AutoFitConstraints"/>.
    /// The maximum is finite because the host multiplies it by the global scale.</summary>
    private static readonly WindowSizeConstraints ResizableConstraints = new()
    {
        MinimumSize = new Vector2(520, 160),
        MaximumSize = new Vector2(4000, 3000),
    };

    /// <summary>Frames of auto-fit per fit — see <see cref="PreDraw"/>.</summary>
    private const int FitFrameCount = 4;

    /// <summary>Fit the window to the current tab, unless the user has given it a size of their own.</summary>
    private void StartFit()
    {
        if (config.WindowUserSized) return;
        _fitFrames = FitFrameCount;
        _fitStarting = true;
    }

    /// <summary>Size the window for this frame: fit to the current tab on tab change until the user drags it, then keep their size.</summary>
    /// <remarks>
    /// The host applies Size, SizeConstraints and Flags after PreDraw, so all three land this frame. A fit is several frames of
    /// AlwaysAutoResize because auto-fit measures the previous frame's content. Flags is assigned every frame so a missed frame
    /// cannot stick, and Size is released here rather than in Draw because Draw does not run on a collapsed window.
    /// Each fit first resets the width to the floor: text wrapped at the content edge measures as wide as the window already is.
    /// </remarks>
    public override void PreDraw()
    {
        FlushPendingSize();
        if (_ownResizeFrames > 0) _ownResizeFrames--;

        // Released by default, so the grip moves the edge instead of being overwritten each frame.
        Size = null;

        if (_restoreSize)
        {
            _restoreSize = false;
            if (config.WindowUserSized)
            {
                _togglesSize = ClampToResizable(new Vector2(config.TogglesWindowWidth, config.TogglesWindowHeight));
                Size = _togglesSize;
                SizeCondition = ImGuiCond.Always;
                _ownResizeFrames = 3;
            }
            else
            {
                StartFit();
            }
        }

        if (_fitFrames > 0)
        {
            _fitFrames--;
            Flags |= ImGuiWindowFlags.AlwaysAutoResize;
            SizeConstraints = AutoFitConstraints;
            _ownResizeFrames = Math.Max(_ownResizeFrames, 3);

            // First frame: reset the width to the floor, since wrapped paragraphs measure as wide as the window already is.
            if (_fitStarting)
            {
                _fitStarting = false;
                Size = new Vector2(AutoFitConstraints.MinimumSize.X, MathF.Max(_togglesSize.Y, AutoFitConstraints.MinimumSize.Y));
                SizeCondition = ImGuiCond.Always;
            }
        }
        else
        {
            Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;
            SizeConstraints = ResizableConstraints;
        }

        if (_growForModel)
        {
            _growForModel = false;
            GrowForModel();
        }
    }

    /// <summary>Grow the window to at least half the screen per axis, because the model viewer just appeared.</summary>
    /// <remarks>Grow only. Fires once per viewer appearance, and goes through <see cref="_togglesSize"/> so the grown size is remembered.</remarks>
    private void GrowForModel()
    {
        float scale = ImGuiHelpers.GlobalScale;
        var screen = ImGuiHelpers.MainViewport.Size / scale;     // unscaled, like every size stored here
        var target = ClampToResizable(screen * 0.5f);

        var grown = new Vector2(MathF.Max(_togglesSize.X, target.X), MathF.Max(_togglesSize.Y, target.Y));
        if (grown == _togglesSize) return;

        _togglesSize = grown;
        if (config.WindowUserSized)
        {
            _sizeDirty = true;
            _sizeChangedAt = Environment.TickCount64;
        }
        Size = grown;
        SizeCondition = ImGuiCond.Always;
        _ownResizeFrames = Math.Max(_ownResizeFrames, 3);
        _keepOnScreen = true;
    }

    private static Vector2 ClampToResizable(Vector2 size)
    {
        var min = ResizableConstraints.MinimumSize;
        var max = ResizableConstraints.MaximumSize;
        // Also replaces non-finite values from an old or differently-scaled config.
        return new Vector2(
            float.IsFinite(size.X) ? Math.Clamp(size.X, min.X, max.X) : min.X,
            float.IsFinite(size.Y) ? Math.Clamp(size.Y, min.Y, max.Y) : min.Y);
    }

    /// <summary>
    /// Write the dragged size to the config once the drag is over. Debounced, on the UI thread: Save serializes the whole
    /// config, too slow per frame and unsafe off-thread while a composite mutates it. Public for the plugin's teardown.
    /// </summary>
    public void FlushPendingSize()
    {
        if (!_sizeDirty) return;
        if (ImGui.IsAnyMouseDown()) return;                     // the grip may still be held
        if (Environment.TickCount64 - _sizeChangedAt < 400) return;

        _sizeDirty = false;
        config.TogglesWindowWidth  = _togglesSize.X;
        config.TogglesWindowHeight = _togglesSize.Y;
        config.Save();
    }

    public override void OnClose()
    {
        FlushPendingSize();
        // The brush's autosave only runs while the Toggles tab draws, so flush it on close; the live preview comes down with it.
        parts.Leave();
    }

    /// <summary>
    /// Write any brush edit still waiting to save, without reloading the mod or redrawing — for the plugin's
    /// own teardown, where the file must land but nothing else should be touched.
    /// </summary>
    public void FlushPendingBrush() => parts.Leave(refreshGame: false);

    /// <summary>Open the window with the Settings tab selected (the plugin-installer gear icon).</summary>
    public void OpenToSettings()
    {
        _forceSettingsTab = true;
        // Force-expand: Draw never runs while collapsed, so _forceSettingsTab would sit unconsumed.
        Show(forceExpand: true);
    }

    /// <summary>Open, un-collapse if needed, and bring to front; setting <see cref="Window.IsOpen"/> alone can leave it collapsed or behind the installer.</summary>
    /// <param name="forceExpand">Un-collapse even when reopening from closed; a plain open leaves a deliberate collapse alone.</param>
    public void Show(bool forceExpand = false)
    {
        var wasOpen = IsOpen;
        IsOpen = true;

        // Reopening from closed leaves Collapsed null so ImGui restores the state it remembers for ###ProteusStatus.
        if (forceExpand || wasOpen)
        {
            // One-shot: Collapsed is re-applied every frame while it has a value; Draw clears it.
            Collapsed = false;
            CollapsedCondition = ImGuiCond.Always;
        }
        else
        {
            // Release any forcing left over from an earlier Show() whose Draw() never got to clear it.
            Collapsed = null;
        }

        BringToFront();   // after IsOpen — Dalamud ignores the request on a closed window
    }

    public override void Draw()
    {
        // The whole window, timed: a frame slow enough to stall the game is logged with which part of it was slow.
        windowFrame.Begin();
        try
        {
            DrawWindow();
        }
        finally
        {
            windowFrame.Mark(_tabDrawn ?? "no tab");
            windowFrame.End(Plugin.Log, "Proteus window");
        }
    }

    private readonly FrameTimer windowFrame = new();

    private void DrawWindow()
    {
        // Reaching Draw means uncollapsed: release Show()'s forced state or the window could never collapse again.
        Collapsed = null;

        // Which tab is selected is answered below, by the tab that draws.
        _tabDrawn = null;
        bool studioWasDrawn = _studioDrawn;
        _studioDrawn = false;

        // Pull a window grown near the screen edge back on screen, now that the new size has been applied.
        if (_keepOnScreen)
        {
            _keepOnScreen = false;
            var vp = ImGuiHelpers.MainViewport;
            var pos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            var fit = new Vector2(
                Math.Clamp(pos.X, vp.Pos.X, MathF.Max(vp.Pos.X, vp.Pos.X + vp.Size.X - size.X)),
                Math.Clamp(pos.Y, vp.Pos.Y, MathF.Max(vp.Pos.Y, vp.Pos.Y + vp.Size.Y - size.Y)));
            if (fit != pos) ImGui.SetWindowPos(fit);
        }

        {
            var live = ImGui.GetWindowSize() / ImGuiHelpers.GlobalScale;
            if (MathF.Abs(live.X - _togglesSize.X) > 0.5f || MathF.Abs(live.Y - _togglesSize.Y) > 0.5f)
            {
                _togglesSize = live;

                // An unrequested size change with the mouse held is the user dragging: stop auto-fitting from now on.
                if (_fitFrames == 0 && _ownResizeFrames == 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    config.WindowUserSized = true;

                if (config.WindowUserSized)
                {
                    _sizeDirty     = true;
                    _sizeChangedAt = Environment.TickCount64;
                }
            }
        }

        // A deferred UV transfer map arrived: index scans taken without the island mask are wrong, so rescan.
        if (_islandMapArrived)
        {
            _islandMapArrived = false;
            _indexRowCache.Clear();
        }

        // Fill the mod list from a cheap discovery-only probe so it is not empty before the first composite.
        compositor.EnsureDiscovered();

        // The band sits outside the tabs so it shows on all of them; it lays out against the fixed auto-fit floor, not the live constraints.
        var band = BrandHeader.Draw(minWindowWidth: AutoFitConstraints.MinimumSize.X * ImGuiHelpers.GlobalScale);
        DrawBandContent(band.Min, band.Max);
        BrandHeader.Reserve(band.Min);
        ImGui.Spacing();

        // barTop is captured before the bar: the bar's height is measured in the Jupiter font, which GetFrameHeight does not report.
        var barTop = ImGui.GetCursorPosY();
        // ── the bar's id carries a generation suffix, and it is load-bearing ──
        // ImGui remembers tab slots by id, even across plugin reloads: bump the suffix whenever the tab order here changes.
        using (ProteusStyle.TabAccent())
        using (var tabs = ProteusStyle.HeaderTabBar("##proteusTabs3"))
        {
            if (tabs)
            {
                DrawTabBarRefresh(barTop);

                using (var t = ProteusStyle.HeaderTabItem(Strings.Tab.Mods, "mods"))
                    if (t) { _tabDrawn = "mods"; modsTab.DrawModsTab(); }

                // The Studio second, right after the mods it works on.
                using (var t = ProteusStyle.HeaderTabItem(Strings.Tab.Parts, "toggles"))
                    if (t)
                    {
                        _tabDrawn = "toggles";
                        if (!DrawnAsDisabled())
                        {
                            _studioDrawn = true;
                            // Fill the height only while not auto-fitting, or the row and the window grow each other without bound.
                            parts.Draw(fillHeight: _fitFrames == 0, reserveBelow: _footerReserve);

                            // The controls grew past the fitted height, so fit again; grow only.
                            if (_fitFrames == 0 && parts.ControlsHeight > _studioFitHeight + 1f)
                            {
                                _studioFitHeight = parts.ControlsHeight;
                                StartFit();
                            }

                            // Grow when the viewer appears, not when the tab opens.
                            if (parts.ShowingModel && !_modelWasShowing) _growForModel = true;
                            _modelWasShowing = parts.ShowingModel;
                        }
                    }

                using (var t = ProteusStyle.HeaderTabItem(Strings.Tab.Bindings, "bindings"))
                    if (t) { _tabDrawn = "bindings"; bindingsTab.DrawBindingsTab(); }

                using (var t = ProteusStyle.HeaderTabItem(Strings.Tab.Create, "create"))
                    if (t) { _tabDrawn = "create"; if (!DrawnAsDisabled()) createTab.DrawCreateTab(); }

                using (var t = ProteusStyle.HeaderTabItem(Strings.Tab.Import, "import"))
                    if (t) { _tabDrawn = "import"; if (!DrawnAsDisabled()) importTab.DrawImportTab(); }

                using (var t = ProteusStyle.HeaderTabItem(Strings.Tab.Export, "export"))
                    if (t) { _tabDrawn = "export"; exportTab.DrawExportTab(); }

                using (var t = ProteusStyle.HeaderTabItem(Strings.Tab.Settings, "settings",
                           _forceSettingsTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
                {
                    _forceSettingsTab = false;
                    if (t) { _tabDrawn = "settings"; settingsTab.DrawSettingsTab(); }
                }
            }
        }

        // A different tab than last frame: fit to its controls, unless the user has sized the window.
        if (_tabDrawn != null && _tabDrawn != _lastTab)
        {
            if (_lastTab != null) StartFit();
            _lastTab = _tabDrawn;
            _studioFitHeight = 0f;
        }

        // Left the Studio tab this frame: save anything waiting and take the live preview down.
        if (studioWasDrawn && !_studioDrawn) parts.Leave();

        // Measured, so the Toggles tab knows how much room the footer needs below it.
        var footerTop = ImGui.GetCursorPosY();
        DrawLastResult();
        _footerReserve = ImGui.GetCursorPosY() - footerTop;

        DrawColorWindow();

        // File-picker dialogs must pump every frame while open.
        _fileDialog.Draw();
    }

    /// <summary>The colour editor, as its own window — stays open until closed.</summary>
    private void DrawColorWindow()
    {
        // No colour editor open means no highlight glow.
        if (_colorWindowMod == null) { ColorTableEditor.Highlighter?.Clear(); return; }

        var entry = compositor.LastDiscovered.FirstOrDefault(e => e.ModDirectory == _colorWindowMod);
        if (entry == null) { _colorWindowMod = null; ColorTableEditor.Highlighter?.Clear(); return; }

        bool open = true;
        // Scaled by hand: a bare ImGui.Begin never gets Dalamud's UI scale. Wide enough for the 16 row buttons 8-across.
        ImGui.SetNextWindowSize(ProteusStyle.S(720f, 580f), ImGuiCond.FirstUseEver);
        // Narrow enough and the row picker wraps; this just stops it collapsing to something useless.
        ImGui.SetNextWindowSizeConstraints(ProteusStyle.S(400f, 300f), new Vector2(float.MaxValue, float.MaxValue));
        if (ImGui.Begin($"Colors — {entry.ModName}###ProteusColors", ref open))
            DrawColorEditor(entry);
        ImGui.End();

        if (!open) _colorWindowMod = null;
    }

    private const string DiscordUrl = "https://discord.gg/solona";

    /// <summary>
    /// The master switch governs every feature, so a tab whose controls would act on the game or on the
    /// user's mods draws this notice in place of its body. True when the body must be skipped. The Mods
    /// tab says the same thing inline, where it stands in for the "no mods" line.
    /// </summary>
    private bool DrawnAsDisabled()
    {
        if (config.PluginEnabled) return false;
        ImGui.Spacing();
        ImGui.TextColored(ProteusStyle.Warn, Strings.ModsList.Disabled);
        return true;
    }

    /// <summary>The recomposite control, right-aligned onto the tab bar's own line so it is reachable from every tab.</summary>
    /// <remarks>
    /// Call right after <c>BeginTabBar</c> and before the first tab item; the cursor is restored so tab content starts where ImGui intended.
    /// Aligned against <c>GetContentRegionMax</c>, which does not ratchet the auto-fitting window's width.
    /// </remarks>
    /// <param name="barTop">Cursor Y from before <c>BeginTabBar</c>.</param>
    private void DrawTabBarRefresh(float barTop)
    {
        var resume = ImGui.GetCursorPos();
        var barH   = (resume.Y - ImGui.GetStyle().ItemSpacing.Y) - barTop;
        var btnW   = ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.SyncAlt, "Refresh");
        var btnH   = ImGui.GetFrameHeight();

        ImGui.SetCursorPos(new Vector2(
            ImGui.GetContentRegionMax().X - btnW,
            barTop + ((barH - btnH) * 0.5f)));

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.SyncAlt, "Refresh"))
        {
            // Shift: rebuild everything, forgetting what was published. See RefreshAndRecomposite.
            compositor.RefreshAndRecomposite(full: ImGui.GetIO().KeyShift);
            // The Parts tab lists every Penumbra mod, so refresh it too; a recomposite alone would miss newly installed ones.
            parts.Refresh();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.Band.RecompositeTip + "\n" + Strings.Band.RecompositeFullTip);

        ImGui.SetCursorPos(resume);
    }

    /// <summary>The wordmark, capability row (<see cref="CapabilityStrip"/>), status pills and Discord link, laid into the band's rect.</summary>
    private void DrawBandContent(Vector2 min, Vector2 max)
    {
        var padX = ProteusStyle.S(10f);
        var padY = ProteusStyle.S(5f);

        // Resolve the Discord button's left edge first: everything else in the band is budgeted against it.
        const string label = "Discord";
        var btnW = ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2);
        var btnH = ImGui.GetFrameHeight();
        var btnX = max.X - btnW - padX;

        // ── wordmark ─────────────────────────────────────────────────────────
        ImGui.SetCursorScreenPos(min + new Vector2(padX, padY));
        using (ProteusStyle.Fonts?.PushWordmark())
            ImGui.TextUnformatted("PROTEUS");

        var afterMark = ImGui.GetItemRectMax().X;

        // ── capability row ───────────────────────────────────────────────────
        // rowY comes from the wordmark's measured bottom: Dalamud's font size is independent of UI scale.
        var rowX = min.X + padX;
        var rowY = ImGui.GetItemRectMax().Y + ProteusStyle.S(2f);
        var strip = CapabilityStrip.Draw(new Vector2(rowX, rowY), btnX - rowX - padX);

        // Collapsed to icons: show the hovered item's label or the caption. Ellipsize returns "" when nothing fits.
        if (strip.Collapsed)
        {
            var readoutX = strip.Right + padX;
            var readout  = ProteusStyle.Ellipsize(strip.Hovered ?? Strings.Band.Caption, btnX - readoutX - padX);
            if (readout.Length > 0)
            {
                ImGui.SetCursorScreenPos(new Vector2(readoutX, rowY));
                ImGui.TextDisabled(readout);
            }
        }

        // ── status pills ─────────────────────────────────────────────────────
        // Only the actionable states get a pill.
        var pillX = afterMark + (padX * 1.5f);
        var pillY = min.Y + padY + ProteusStyle.S(3f);
        ImGui.SetCursorScreenPos(new Vector2(pillX, pillY));

        (string Text, Vector4 Colour)? state =
              !config.PluginEnabled ? (Strings.Band.PillDisabled,   ProteusStyle.Warn)
            : !penumbra.IsAvailable ? (Strings.Band.PillNoPenumbra, ProteusStyle.Bad)
            :                         null;
        // Ellipsized so a long translation cannot draw under the Discord button.
        bool statePillDrawn = false;
        if (state is { } s)
        {
            // Ellipsize returns "" when not even the ellipsis fits; skip the empty pill.
            var stateText = ProteusStyle.Ellipsize(s.Text, ProteusStyle.PillTextBudget(btnX - pillX - padX));
            if (stateText.Length > 0)
            {
                ProteusStyle.Pill(stateText, s.Colour);
                statePillDrawn = true;
            }
        }

        if (uvMapDl.State is UVMapDownloadState.Downloading or UVMapDownloadState.Failed)
        {
            var failed = uvMapDl.State == UVMapDownloadState.Failed;

            // Budget from the drawn state pill's edge, or the parked cursor if none was drawn (the last item would be the caption).
            var used   = statePillDrawn ? ImGui.GetItemRectMax().X : pillX;
            // Measured from the translated caption.
            var retryW = failed ? ImGui.CalcTextSize(Strings.Band.Retry).X + (ImGui.GetStyle().FramePadding.X * 2)
                                    + ImGui.GetStyle().ItemSpacing.X
                                : 0f;
            var budget = ProteusStyle.PillTextBudget(btnX - used - ImGui.GetStyle().ItemSpacing.X - retryW - padX);

            var full  = uvMapDl.StatusMessage;
            var shown = ProteusStyle.Ellipsize(full, budget);

            // Nothing fits: drop the pill; the Retry button still shows.
            if (shown.Length > 0)
            {
                // SameLine only after a drawn pill; otherwise put the cursor back where the state pill would be.
                if (statePillDrawn)
                    ImGui.SameLine();
                else
                    ImGui.SetCursorScreenPos(new Vector2(pillX, pillY));
                ProteusStyle.Pill(shown, failed ? ProteusStyle.Bad : ProteusStyle.Warn);
                // Only when something was actually cut — an untruncated pill already says everything.
                if (shown != full && ImGui.IsItemHovered())
                    ImGui.SetTooltip(full);
            }

            if (failed)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(Strings.Band.Retry))
                    uvMapDl.EnsureMapsAsync();
            }
        }

        // ── Discord ──────────────────────────────────────────────────────────
        // Vertically centred in the band; btnX was resolved at the top of this method.
        ImGui.SetCursorScreenPos(new Vector2(btnX, min.Y + ((max.Y - min.Y - btnH) * 0.5f)));

        // Discord's brand blurple, deliberately not themed.
        var blurple = new Vector4(0.35f, 0.40f, 0.95f, 1f);
        using (ImRaii.PushColor(ImGuiCol.Button,   blurple)
                     .Push(ImGuiCol.ButtonHovered, blurple.Lighten(0.10f))
                     .Push(ImGuiCol.ButtonActive,  blurple.Darken(0.10f)))
        {
            if (ImGui.Button(label))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DiscordUrl) { UseShellExecute = true }); }
                catch { /* opening a browser is best-effort */ }
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(DiscordUrl);
    }

    private void DrawLastResult()
    {
        ImGui.Spacing();

        var f = Strings.Footer;

        var result = compositor.LastResult;
        if (result == null)
        {
            ImGui.TextDisabled(f.NoResult);
            return;
        }

        if (!result.Success)
        {
            ProteusStyle.Pill(f.PillFailed, ProteusStyle.Bad);
            ImGui.SameLine();
            ImGui.TextColored(ProteusStyle.Bad, result.ErrorMessage ?? f.UnknownError);
            return;
        }

        // The number is formatted here and the unit comes from the template, so a translator has no format specifier to break.
        var elapsed = DateTime.UtcNow - result.Timestamp;
        var timeStr = elapsed.TotalSeconds < 60
            ? string.Format(f.SecondsAgoFmt, elapsed.TotalSeconds.ToString("F1"))
            : string.Format(f.MinutesAgoFmt, elapsed.TotalMinutes.ToString("F0"));

        ProteusStyle.Pill(f.PillOk, ProteusStyle.Ok);
        ImGui.SameLine();
        ImGui.TextDisabled(string.Format(f.LastCompositeFmt, timeStr, result.TexturesPatched, result.OverlayModsUsed));
    }

    internal static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// The colour editor for an imported content pack: one tab per material its selected options draw. Only the colour
    /// table is editable, since the pack ships its own mesh, textures and shader; unedited rows stay as the author wrote them.
    /// </summary>
    private void DrawContentColorEditor(
        OverlayEntry entry, bool overrideActive,
        IReadOnlyList<(string Name, string Path, bool FromMod)> effects)
    {
        // Resolved here rather than via discovery.ResolveActiveContent, which re-reads meta.json; this runs every frame.
        var collId = penumbra.GetPlayerCollectionId();
        var settings = collId.HasValue ? penumbra.GetModSettings(collId.Value, entry.ModDirectory) : null;

        if (!_groupOrderCache.TryGetValue(entry.ModDirectory, out var groupOrder))
        {
            var modRoot = entry.ModRoot;
            groupOrder = modRoot != null
                ? SidecarDiscoveryService.ReadGroupOrder(modRoot)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _groupOrderCache[entry.ModDirectory] = groupOrder;
        }

        List<string>? Selection(string group)
            => settings?.Options
                .FirstOrDefault(kv => string.Equals(kv.Key, group, StringComparison.OrdinalIgnoreCase))
                .Value;

        // The piece group, read as ResolveActiveContent reads it, so the panel and the composite agree on what is worn.
        var gateOn = entry.Metadata.PieceGroupName is { Length: > 0 } gateGroup ? Selection(gateGroup) : null;

        // Which options are live; collapsed into materials below.
        var options = new List<(string? Group, string? Option, int Order, int Pieces)>();

        int unconditional = PiecesFor(entry, null, null, gateOn).Count;
        if (unconditional > 0)
            options.Add((null, null, int.MaxValue, unconditional));

        foreach (var g in entry.Metadata.ContentGroups ?? [])
        {
            var selected = Selection(g.PenumbraGroupName);
            // A group with nothing selected contributes nothing to the composite either, so it gets no tab.
            if (selected is not { Count: > 0 }) continue;

            int order = groupOrder.TryGetValue(g.PenumbraGroupName, out var n) ? n : int.MaxValue;
            foreach (var o in g.Options.Where(o => selected.Any(sel =>
                         string.Equals(o.Name, sel, StringComparison.OrdinalIgnoreCase))))
            {
                int live = PiecesFor(entry, g.PenumbraGroupName, o.Name, gateOn).Count;
                if (live > 0) options.Add((g.PenumbraGroupName, o.Name, order, live));
            }
        }

        // A pack the composite refused otherwise looks correct, so say why first, in amber.
        if (compositor.GetUnwearableContentReason(entry.ModDirectory) is { } unwearable)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ProteusStyle.Warn, unwearable);
            ImGui.PopTextWrapPos();
            return;
        }

        if (options.Count == 0)
        {
            ProteusStyle.DisabledWrapped(Strings.ColorPanel.NoActiveOptions);
            return;
        }

        options = options
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Option, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Materials the last composite found backing a drawn mesh; a declared material may never be drawn.
        // Null means no information yet, so show every tab.
        var liveMaterials = compositor.GetLiveContentMaterials(entry.ModDirectory);

        // Stamp of Penumbra's current selection, part of the index-scan cache key, so an option-driven index texture is re-read when it changes.
        var selectionStamp = settings == null
            ? "-"
            : string.Join(' ', settings.Value.Options
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Key + "=" + string.Join(',', kv.Value)));

        // Collapse options into materials: pieces binding the same .mtrl publish as one material, so one grid governs them all.
        var byMaterial = new List<(string Mtrl, List<string> Names, List<ContentOwner> Owners)>();
        foreach (var (group, option, _, _) in options)
        {
            var live = PiecesFor(entry, group, option, gateOn);
            foreach (var piece in live)
                foreach (var (leaf, mtrl) in piece.Materials)
                {
                    if (liveMaterials != null && !liveMaterials.Contains(mtrl)) continue;

                    // A material gated by the pack's own attribute options shows only when one of its gates is ticked.
                    // Gates naming a group Penumbra no longer has cannot be evaluated and leave the material ungated.
                    var gates = piece.GatesFor(leaf);
                    List<string>? gateNames = null;
                    if (gates.Count > 0)
                    {
                        var known = gates.Where(g => Selection(g.Group) != null).ToList();
                        if (known.Count > 0)
                        {
                            gateNames = [.. known
                                .Where(g => Selection(g.Group)!.Any(s =>
                                    string.Equals(s, g.Option, StringComparison.OrdinalIgnoreCase)))
                                .Select(g => g.Option)
                                .Distinct(StringComparer.OrdinalIgnoreCase)];
                            if (gateNames.Count == 0) continue;
                        }
                    }

                    int at = byMaterial.FindIndex(m =>
                        string.Equals(m.Mtrl, mtrl, StringComparison.OrdinalIgnoreCase));
                    if (at < 0)
                    {
                        byMaterial.Add((mtrl, gateNames ?? [], []));
                        at = byMaterial.Count - 1;
                    }
                    else if (gateNames != null)
                    {
                        // Two ticked options sharing one material: name it after both, comparing name by name.
                        foreach (var n in gateNames)
                            if (!byMaterial[at].Names.Contains(n, StringComparer.OrdinalIgnoreCase))
                                byMaterial[at].Names.Add(n);
                    }

                    // Carry the pieces that make this owner use this material; the caption names them.
                    if (!byMaterial[at].Owners.Any(o =>
                            string.Equals(o.Group, group, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(o.Option, option, StringComparison.OrdinalIgnoreCase)))
                        byMaterial[at].Owners.Add(new ContentOwner(group, option,
                            [.. live.Where(p => p.Materials.Values.Contains(mtrl, StringComparer.OrdinalIgnoreCase))]));
                }
        }

        if (byMaterial.Count == 0)
        {
            ProteusStyle.DisabledWrapped(Strings.ColorPanel.NoActiveOptions);
            return;
        }

        // One material gets no tab strip.
        if (byMaterial.Count == 1)
        {
            DrawContentMaterial(entry, byMaterial[0].Mtrl, byMaterial[0].Owners, overrideActive,
                selectionStamp, effects);
            return;
        }

        using var tabs = ImRaii.TabBar($"##contentTabs_{entry.ModDirectory}");
        if (!tabs) return;

        foreach (var (mtrl, names, owners) in byMaterial)
        {
            // Named after the options that reveal it, else the file name.
            var label = names.Count > 0 ? string.Join(", ", names) : Path.GetFileNameWithoutExtension(mtrl);
            using var tab = ImRaii.TabItem($"{label}##content_{mtrl}");
            if (!tab) continue;
            DrawContentMaterial(entry, mtrl, owners, overrideActive, selectionStamp, effects);
        }
    }

    /// <summary>One content material's colour grid; the edit governs every option that shares it.</summary>
    private void DrawContentMaterial(
        OverlayEntry entry, string mtrl, List<ContentOwner> owners, bool overrideActive, string selectionStamp,
        IReadOnlyList<(string Name, string Path, bool FromMod)> effects)
    {
        var (leadGroup, leadOption, _) = owners[0];

        // Named by the switch that turned each piece ON, which is not always its option — see ContentLabels.
        var worn = ContentLabels.For(
            owners.Select(o => (o.Option, o.Pieces)), Strings.Content.Unconditional);
        ProteusStyle.DisabledWrapped(string.Format(Strings.Content.SharedByFmt, string.Join(", ", worn)));

        // Say which cell the index texture samples: a row filter cannot narrow the grid to one column.
        var idx = ContentIndexFor(entry, mtrl, selectionStamp);
        switch (idx.State)
        {
            case ContentIndexState.NoSampler:
                ProteusStyle.DisabledWrapped(string.Format(Strings.Content.NoIndexFmt, DefaultContentRow));
                break;

            case ContentIndexState.SelectsNothing:
                ProteusStyle.DisabledWrapped(Strings.Content.IndexEmpty);
                break;

            case ContentIndexState.Compressed:
                ProteusStyle.DisabledWrapped(string.Format(Strings.Content.IndexCompressedFmt,
                    string.Join(", ", idx.Rows!.OrderBy(r => r)),
                    idx.SubRow ?? Strings.Content.EitherColumn));
                break;

            case ContentIndexState.NoColorTable:
                // Amber: nothing below this has any effect.
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(ProteusStyle.Warn, Strings.Content.NoColorTable);
                ImGui.PopTextWrapPos();
                break;

            case ContentIndexState.FollowsHairColor:
                // Amber for the same reason, but the colour has a source worth naming: the character, not the textures.
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(ProteusStyle.Warn, Strings.Content.FollowsHairColor);
                ImGui.PopTextWrapPos();
                break;

            case ContentIndexState.Scanned when idx.Rows is { Count: 1 } && idx.SubRow != null:
                ProteusStyle.DisabledWrapped(
                    string.Format(Strings.Content.SamplesFmt, idx.Rows.First(), idx.SubRow));
                break;

            case ContentIndexState.Scanned:
                // Several rows, or one row across both columns.
                ProteusStyle.DisabledWrapped(string.Format(Strings.Content.SamplesRowsFmt,
                    string.Join(", ", idx.Rows!.OrderBy(r => r))));
                break;

            default:
                // Say so rather than show a silently unfiltered grid. Wrapped: TextColored draws one line and lets the window clip it.
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(ProteusStyle.Warn, Strings.Content.IndexUnreadable);
                ImGui.PopTextWrapPos();
                break;
        }

        // While a binding is edited, work on a copy and install it only on change, so opening the panel never creates an override.
        var stored  = StoredContentRows(entry, leadGroup, leadOption, mtrl);
        var ovrRows = overrideActive
            ? EffectiveOverrideContentRows(entry, leadGroup, leadOption, mtrl) : null;
        var rows = overrideActive ? DesignBindingService.CopyRows(ovrRows ?? stored) : stored;

        // The glow, on the same copy-while-binding rule as the rows above.
        var storedGlow = StoredContentGlow(entry, leadGroup, leadOption, mtrl);
        var ovrGlow = overrideActive
            ? EffectiveOverrideContentGlow(entry, leadGroup, leadOption, mtrl) : null;
        var glow = (ovrGlow ?? storedGlow).Clone();
        bool glowing = glow.GlowKey() != null;

        // Every owner maps to the same published material, so this comes out as one entry.
        var targets = owners
            .SelectMany(o => compositor.GetShellMaterials(entry.ModDirectory, o.Group, o.Option) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool changed = false;
        var selKey = entry.ModDirectory + "|" + mtrl;
        int sel = _rowSelection.GetValueOrDefault(selKey, 0);        // 0 = never chosen; DrawRows lands it
        ColorTableEditor.DrawRows(selKey, rows, idx.Rows,
            // Content materials are gear-space; the shader is the pack's unless an animated glow moves it to characterscroll.
            gear: true,
            shader: glowing ? RenderModeInference.GlowShader : OverlayDescriptor.DefaultGearShader,
            targets,
            skinGlowTargets: null,
            out _, ref sel, ref changed,
            // Roughness and metalness here are the pack author's, so keep them visible even under animated glow.
            authoredPhysical: true,
            // Show the pack's own material values for anything the sidecar hasn't overridden.
            physicalBaseline: idx.Physical,
            // Light response re-asserts shell colour tables at runtime; content materials are published verbatim, so it cannot apply.
            lightResponseApplies: false,
            // Dim the column the index texture does not sample.
            usedSubRow: idx.SubRow);
        _rowSelection[selKey] = sel;

        // ── animated glow ────────────────────────────────────────────────────
        // Hidden without a colour table: a row's emissive is what arms the effect.
        bool glowChanged = false;
        if (idx.State is not (ContentIndexState.NoColorTable or ContentIndexState.FollowsHairColor))
        {
            ImGui.Separator();
            // Warn before the picker: characterscroll has no base-texture slot, so the pack's diffuse painting is lost.
            if (idx.HasDiffuse)
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(ProteusStyle.Warn, Strings.Content.GlowDropsDiffuse);
                ImGui.PopTextWrapPos();
            }
            glowChanged = ColorTableEditor.DrawContentGlowFooter(selKey, effects, glow);

            // Arm the sampled cell's Glow when an effect is picked and disarm it when cleared: the row's Glow enables the effect,
            // and on the pack's own character.shpk it is an ordinary emissive.
            if (glowChanged)
            {
                var (armRow, armA) = SampledCell(idx, sel);
                bool wrote = glow.GlowKey() != null
                    ? ContentGlowRow.Arm(rows, armRow, armA)
                    : ContentGlowRow.Disarm(rows, armRow, armA);
                if (wrote) changed = true;
            }

            // Warn, naming the cell, when an effect is set but the sampled cell does not emit.
            if (glow.GlowKey() != null && !SampledCellEmits(rows, idx, sel))
            {
                var (needRow, needA) = SampledCell(idx, sel);
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(ProteusStyle.Warn,
                    string.Format(Strings.Content.GlowNeedsEmissiveFmt, needRow, needA ? "A" : "B"));
                ImGui.PopTextWrapPos();
            }
        }

        if (!changed && !glowChanged) return;

        // Written once per material, not fanned out across the options that use it.
        if (!overrideActive)
        {
            if (changed) StoreContentRows(entry, mtrl, DesignBindingService.CopyRows(rows));
            if (glowChanged) StoreContentGlow(entry, mtrl, glow.Clone());
            discovery.SaveMetadata(entry);
            InvalidateDefaultsCache(entry);
            // Content pieces never touch skin textures, so the skin fingerprint is authoritative.
            RecompositeForOverlay(entry, "content-colors-change", ColorEditDebounceMs,
                skinFingerprintAuthoritative: true);
            return;
        }

        // Keyed by MATERIAL, like the metadata write above: an option-keyed override would sit under the mod's own
        // per-material rows and never reach the character, and two materials sharing an option would overwrite each
        // other. One key covers every owner, since they all publish this same material.
        if (changed)
            editRouter.SetContentMaterialRows(
                entry.ModDirectory, mtrl, DesignBindingService.CopyRows(rows));
        if (glowChanged)
            editRouter.GetEditableContentMaterialGearOverride(entry.ModDirectory, mtrl, glow)
                ?.ApplyScrollFrom(glow);

        RecompositeForOverlay(entry, "content-colors-change", ColorEditDebounceMs,
            skinFingerprintAuthoritative: true);   // see above
    }

    /// <summary>Unpack the index scan for <see cref="ContentGlowRow"/>, which owns the rule.</summary>
    private static (int Row, bool SubRowA) SampledCell(ContentIndex idx, int selectedRow)
        => ContentGlowRow.Sampled(idx.Rows, idx.SubRow, selectedRow);

    private static bool SampledCellEmits(List<ColorTableRowPreset> rows, ContentIndex idx, int selectedRow)
    {
        var (row, subRowA) = SampledCell(idx, selectedRow);
        return ContentGlowRow.Emits(rows, row, subRowA);
    }

    /// <summary>The material's animated glow from the sidecar, or a fresh empty preset that is not installed: drawing must not change the mod.</summary>
    /// <remarks>Keyed by material.</remarks>
    private static GearSettingsPreset StoredContentGlow(
        OverlayEntry entry, string? group, string? option, string materialRel)
        => entry.Metadata.PeekMaterialSettings(materialRel)?.Glow
        ?? ContentOptionFor(entry, group, option)?.Glow
        ?? entry.Metadata.ContentGlow
        ?? new GearSettingsPreset();

    private static void StoreContentGlow(OverlayEntry entry, string materialRel, GearSettingsPreset glow)
    {
        // Stored even when empty: a cleared glow is a decision, and deleting it would let an older per-option glow reappear.
        entry.Metadata.MaterialSettings(materialRel).Glow = glow;
    }

    /// <summary>How much the panel knows about which colour-table cell a content material samples; each state gets its own caption.</summary>
    private enum ContentIndexState
    {
        /// <summary>Nothing could be established (material or index texture missing or unreadable): filter nothing and say so.</summary>
        Unknown,
        /// <summary>The material parsed cleanly and declares no <c>_id</c> sampler.</summary>
        NoSampler,
        /// <summary>The material parsed and has no colour table, so nothing the grid writes survives.</summary>
        NoColorTable,
        /// <summary><see cref="NoColorTable"/> on <c>hair.shpk</c>: the piece is tinted by the character's hair colour, which is why it has no table.</summary>
        FollowsHairColor,
        /// <summary>The index texture was read and every texel is transparent, so it selects no row.</summary>
        SelectsNothing,
        /// <summary>Read and names a cell, but the texture is compressed so the reading may be a row out: filter on it and say the caveat.</summary>
        Compressed,
        /// <summary>The index texture was read and names rows.</summary>
        Scanned,
    }

    /// <summary>
    /// Which colour-table cell a content material samples. <paramref name="Rows"/> is 1-based, null meaning don't filter;
    /// <paramref name="SubRow"/> is "A" or "B" when the index is uniform enough to name one; <paramref name="HasDiffuse"/> feeds the glow warning.
    /// </summary>
    private readonly record struct ContentIndex(
        HashSet<int>? Rows, string? SubRow, ContentIndexState State, bool HasDiffuse = false,
        /// <summary>The pack material's roughness and metalness per sub-row (0–31); null without a colour table.</summary>
        IReadOnlyList<(float Roughness, float Metalness)>? Physical = null);

    /// <summary>The colour-table row a material with no index texture falls back to: with no <c>_id</c> the shader takes the last row.
    /// Shared with shells, whose fabricated index targets the same row.</summary>
    private const int DefaultContentRow = GlowShell.Row;

    /// <summary>The shader ears and tails are authored on so they match the character's hair; it declares no colour table.</summary>
    private const string HairShader = "hair.shpk";

    /// <summary>
    /// Read a content material's index texture and work out which colour rows it selects, so the grid can dim the rest.
    /// Cached per material and <paramref name="selectionStamp"/>, since an index texture may itself be an option.
    /// </summary>
    private ContentIndex ContentIndexFor(OverlayEntry entry, string mtrlRel, string selectionStamp)
    {
        var prefix = entry.ModDirectory + "|" + mtrlRel + "|";
        var cacheKey = prefix + selectionStamp;
        if (_contentIndexCache.TryGetValue(cacheKey, out var hit)) return hit;

        // A miss makes this material's entries under older selections dead weight: keep one entry per material.
        foreach (var stale in _contentIndexCache.Keys
                     .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToList())
            _contentIndexCache.Remove(stale);

        var result = new ContentIndex(null, null, ContentIndexState.Unknown);
        try
        {
            var modRoot = entry.ModRoot;
            if (modRoot == null)
                Plugin.Log.Warning("[Proteus] content: no mod folder for {0}, so {1}'s index is unknown",
                    entry.ModDirectory, mtrlRel);
            else
            {
                var mtrlPath = Path.Combine(modRoot, mtrlRel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(mtrlPath))
                    // Logged, not swallowed.
                    Plugin.Log.Warning("[Proteus] content: {0} names material {1}, which is not in the mod "
                                     + "folder — its colour rows cannot be narrowed", entry.ModDirectory, mtrlRel);
                else
                {
                    // The material names its index by game path; Penumbra says which file that is now.
                    var mtrlBytes = File.ReadAllBytes(mtrlPath);
                    var slots = TextureLoader.ParseMtrlBytes(mtrlBytes);
                    if (!slots.Parsed)
                        // The parser is fail-open: only Parsed separates "no index" from "unreadable", and a wrong row filter dims the working row.
                        Plugin.Log.Warning("[Proteus] content: could not read material {0} of {1} — its colour "
                                         + "rows cannot be narrowed", mtrlRel, entry.ModDirectory);
                    else if (!slots.HasColorTable)
                        // No rows exist, and PatchColorTable discards whatever the grid writes. The material is published
                        // byte-for-byte, so a hair-shader piece keeps its shader and goes on following the character's hair.
                        result = new ContentIndex(null, null,
                            string.Equals(TextureLoader.GetMtrlInfo(mtrlBytes).shader, HairShader,
                                          StringComparison.OrdinalIgnoreCase)
                                ? ContentIndexState.FollowsHairColor
                                : ContentIndexState.NoColorTable);
                    else if (string.IsNullOrEmpty(slots.Index))
                        result = new ContentIndex([DefaultContentRow], "A", ContentIndexState.NoSampler);
                    else
                    {
                        var disk = ResolveContentTexture(entry, modRoot, slots.Index);
                        if (disk != null && textureLoader.LoadTexAsRgba(disk) is { } tex)
                        {
                            result = ScanContentIndex(tex.rgba);
                            // A compressed index still narrows the grid but is flagged: a lossy codec can move a row selector across a bucket.
                            if (result.State == ContentIndexState.Scanned
                                && TextureLoader.IsUncompressed(disk) == false)
                                result = result with { State = ContentIndexState.Compressed };
                        }
                        else
                            Plugin.Log.Warning("[Proteus] content: {0} names index texture {1}, which could not "
                                             + "be resolved or decoded", entry.ModDirectory, slots.Index);
                    }

                    // Recorded whatever the index said: the glow warning and the physical values are separate questions.
                    if (slots.Parsed)
                        result = result with
                        {
                            HasDiffuse = !string.IsNullOrEmpty(slots.Diffuse),
                            Physical = GearMaterialWriter.ReadPhysical(mtrlBytes),
                        };
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning("[Proteus] could not read the index texture for {0}: {1}", mtrlRel, ex.Message);
        }

        _contentIndexCache[cacheKey] = result;
        return result;
    }

    /// <summary>
    /// The disk file behind a texture a content material names, which must lie inside the pack's own folder.
    /// Penumbra is asked first (it knows which option is selected), then the pack's folder is searched by file name.
    /// </summary>
    private string? ResolveContentTexture(OverlayEntry entry, string modRoot, string gamePath)
    {
        var viaPenumbra = penumbra.ResolvePlayer(gamePath);
        // ResolvePlayer echoes the request back when nothing redirects it, which is not a file.
        if (viaPenumbra != null
            && !string.Equals(viaPenumbra, gamePath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(viaPenumbra))
        {
            if (IsUnder(modRoot, viaPenumbra)) return viaPenumbra;
            Plugin.Log.Debug("[Proteus] content: ignoring \"{0}\" for {1} — outside {2}, so it is not this "
                           + "pack's own texture", viaPenumbra, gamePath, entry.ModDirectory);
        }

        try
        {
            var leaf = Path.GetFileName(gamePath.Replace('\\', '/'));
            if (leaf.Length == 0) return null;
            var hit = Directory.EnumerateFiles(modRoot, leaf, SearchOption.AllDirectories).FirstOrDefault();
            Plugin.Log.Debug("[Proteus] content: {0} -> {1}", gamePath, hit ?? "(not in the mod folder)");
            return hit;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug("[Proteus] no file for {0} under {1}: {2}", gamePath, entry.ModDirectory, ex.Message);
            return null;
        }
    }

    /// <summary>Whether <paramref name="path"/> sits inside <paramref name="root"/>. Compared through
    /// <see cref="Path.GetRelativePath"/> so <c>..</c> and mixed separators cannot smuggle a path out.</summary>
    private static bool IsUnder(string root, string path)
    {
        try
        {
            var rel = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            return !Path.IsPathRooted(rel)
                && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && rel != "..";
        }
        catch { return false; }
    }

    /// <summary>The scan as the panel needs it: an all-transparent texture selects nothing, its own state rather than unreadable.</summary>
    private static ContentIndex ScanContentIndex(byte[] rgba)
    {
        var scan = ContentIndexTexture.Read(rgba);
        return scan.Rows.Count == 0
            ? new ContentIndex(null, null, ContentIndexState.SelectsNothing)
            : new ContentIndex(scan.Rows, scan.SubRow, ContentIndexState.Scanned);
    }

    /// <summary>One option's stake in a shared content material: the option to write edits back to, and its pieces drawn with it.
    /// <paramref name="Option"/> is null for a piece in no option of the pack's own.</summary>
    private readonly record struct ContentOwner(
        string? Group, string? Option, IReadOnlyList<ContentPiece> Pieces);

    /// <summary>The sidecar pieces behind one live option (or the unconditional ones), minus those the piece group switches off,
    /// by the same rule as <see cref="SidecarDiscoveryService.ResolveActiveContent"/>.</summary>
    private static IReadOnlyList<ContentPiece> PiecesFor(
        OverlayEntry entry, string? group, string? option, IReadOnlyList<string>? gateOn)
    {
        var pieces = group == null || option == null
            ? entry.Metadata.Content ?? []
            : ContentOptionFor(entry, group, option)?.Pieces ?? [];
        return [.. pieces.Where(p => SidecarDiscoveryService.PieceIsOn(p, gateOn))];
    }

    /// <summary>The rows stored for a content material, or a fresh empty list that is not installed: drawing must not change the mod.</summary>
    /// <remarks>Keyed by material, since one option may hold many materials; per-option rows are only read as a fallback.</remarks>
    private static List<ColorTableRowPreset> StoredContentRows(
        OverlayEntry entry, string? group, string? option, string materialRel)
        => entry.Metadata.PeekMaterialSettings(materialRel)?.ColorTableRows
        ?? ContentOptionFor(entry, group, option)?.ColorTableRows
        ?? entry.Metadata.ColorTableRows
        ?? [];

    private static void StoreContentRows(OverlayEntry entry, string materialRel, List<ColorTableRowPreset> rows)
        => entry.Metadata.MaterialSettings(materialRel).ColorTableRows = rows;

    /// <summary>
    /// What a design binding or pinned preset says this material's rows are, at the precedence the composite
    /// applies (<see cref="ContentSettingLevels"/>): its material entry outranks everything, while its option
    /// entry reaches the panel only when the mod has no material entry of its own. Null ⇒ show the mod's own.
    /// </summary>
    private List<ColorTableRowPreset>? EffectiveOverrideContentRows(
        OverlayEntry entry, string? group, string? option, string materialRel)
    {
        if (editRouter.PeekContentMaterialRows(entry.ModDirectory, materialRel) is { } perMaterial)
            return perMaterial;
        if (entry.Metadata.PeekMaterialSettings(materialRel)?.ColorTableRows != null) return null;
        return editRouter.PeekOverrideRows(entry.ModDirectory, group, option);
    }

    /// <inheritdoc cref="EffectiveOverrideContentRows"/>
    private GearSettingsPreset? EffectiveOverrideContentGlow(
        OverlayEntry entry, string? group, string? option, string materialRel)
    {
        if (editRouter.PeekContentMaterialGearOverride(entry.ModDirectory, materialRel) is { } perMaterial)
            return perMaterial;
        if (entry.Metadata.PeekMaterialSettings(materialRel)?.Glow != null) return null;
        return editRouter.PeekContentGearOverride(entry.ModDirectory, group, option);
    }

    /// <summary>The sidecar's content option for a (group, option) pair, or null for an unconditional piece
    /// — or for a name pair the sidecar no longer carries.</summary>
    private static ContentOption? ContentOptionFor(OverlayEntry entry, string? group, string? option)
    {
        if (group == null || option == null) return null;
        return entry.Metadata.ContentGroups?
            .FirstOrDefault(g => string.Equals(g.PenumbraGroupName, group, StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(o => string.Equals(o.Name, option, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Does this mod survive a filter box? Matches display name or folder name.
    /// Null-tolerant: a throw inside Draw takes the window down for the session.</summary>
    internal static bool MatchesNameOrFolder(OverlayEntry m, string filter)
        => filter.Length == 0
        || m.ModName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true
        || m.ModDirectory?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Is this a material an overlay paints skin onto (body or face)? Anchored on <c>chara/human/</c>, unlike
    /// <see cref="CompositorService.IsBodyUvMaterial"/>, because weapon paths also contain <c>/obj/body/</c>.</summary>
    internal static bool IsSkinMaterial(string p)
        => p.StartsWith("chara/human/", StringComparison.OrdinalIgnoreCase)
        && (p.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase)
         || p.Contains("/obj/face/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The face specifically, by the service's definition, which excludes the eye materials inside the face folder.</summary>
    internal static bool IsFaceMaterial(string p) => FaceUvDoublingService.IsFaceMaterial(p);

    /// <summary>One <see cref="InertModDiagnosis.InertReason"/> as a localized sentence; must state the same facts as <c>InertModDiagnosis.EnglishInert</c>.</summary>
    internal static string DescribeInert(InertModDiagnosis.InertReason r)
    {
        var ms = Strings.Mods;
        return r.Cause switch
        {
            InertModDiagnosis.InertCause.SettingsUnreadable => ms.InertSettingsUnreadable,
            InertModDiagnosis.InertCause.GroupsMissing      => string.Format(ms.InertGroupsMissingFmt, r.Groups),
            InertModDiagnosis.InertCause.NothingTicked      => string.Format(ms.InertNothingTickedFmt, r.GroupCount, r.Groups),
            InertModDiagnosis.InertCause.WrongRace          => string.Format(ms.InertWrongRaceFmt, r.Wants, r.Have),
            InertModDiagnosis.InertCause.MaskNeedsShell     => string.Format(ms.InertMaskNeedsShellFmt, r.Groups),
            _                                               => ms.InertNothingReached,
        };
    }

    /// <summary>Reserve <paramref name="width"/> of horizontal space so an AlwaysAutoResize window keeps its width after a table goes.
    /// Zero-height; a non-positive width draws nothing.</summary>
    internal static void HoldWindowWidth(float width)
    {
        if (width > 0f)
            ImGui.Dummy(new Vector2(width, 0f));
    }

    private void DrawColorEditor(OverlayEntry entry)
    {
        DrawColorEditorBody(entry);

        // Presets go last and outside the body, which returns early on several paths; a preset belongs to the mod.
        presetBar.Draw(entry);
    }

    private void DrawColorEditorBody(OverlayEntry entry)
    {
        // display: false — Jupiter lacks CJK and accented glyphs for a user-chosen name.
        ProteusStyle.SectionHeader(entry.ModName, display: false);

        // When a pinned preset or design binding owns the look, edits preview into it instead of metadata.json.
        bool overrideActive = editRouter.HasOverride(entry.ModDirectory);

        // Asks the binding service, not the router: a preset pinned on an unbound mod must not claim a design is editing it.
        if (designBindings.IsOverrideActiveFor(entry.ModDirectory))
        {
            var activeId = designBindings.ActiveDesignId;
            var name = designBindings.Bindings.FirstOrDefault(b => b.DesignId == activeId)?.DesignName
                       ?? activeId?.ToString()[..8] ?? "?";
            // Carded notice with both caveats: Reset rewrites the mod's own settings, and Bodies is global config.
            using (ProteusStyle.Card(ProteusStyle.Binding))
            {
                // TextColored does not wrap; wrap 14px short of the content edge to stay inside the card's border.
                ImGui.PushTextWrapPos(ImGui.GetWindowContentRegionMax().X - ProteusStyle.S(14f));
                ProteusStyle.Pill(Strings.ColorPanel.PillBinding, ProteusStyle.Binding);
                ImGui.SameLine();
                ImGui.TextColored(ProteusStyle.Binding, string.Format(Strings.ColorPanel.EditingBindingFmt, name));
                ImGui.TextColored(ProteusStyle.Binding, Strings.ColorPanel.BaseUnchanged);
                ImGui.PopTextWrapPos();
            }
            ImGui.Spacing();
        }

        // Clear per-entry index cache on popup open so option switches are reflected.
        if (ImGui.IsWindowAppearing())
        {
            foreach (var k in _indexRowCache.Keys.Where(k => k.StartsWith(entry.SidecarRoot)).ToList())
                _indexRowCache.Remove(k);
            // Content scans are keyed by mod directory; sweep them too so files changed under an unchanged selection are re-read.
            foreach (var k in _contentIndexCache.Keys
                         .Where(k => k.StartsWith(entry.ModDirectory + "|", StringComparison.OrdinalIgnoreCase))
                         .ToList())
                _contentIndexCache.Remove(k);
        }

        // The body UV this mod's art is painted in, so the index scan can ignore bleed outside the islands.
        var bodyType = OverlayBodyType(entry);

        // Scroll maps a gear overlay can pick from: the mod's own Effects/ folder, then the user's.
        var effects = discovery.ResolveAvailableEffects(entry, discovery.EffectsLibraryPath());

        // ── content packs: the pack's OWN material, one tab per selected option ────
        // Drawn separately: a content option has no overlay descriptor, only the pack's own material's colour grid.
        if (entry.Metadata is { HasContent: true })
        {
            DrawContentColorEditor(entry, overrideActive, effects);
            // A pack may ship geometry AND overlays; only a pure content pack is finished here.
            if (entry.Metadata.Overlays is not { Count: > 0 } && entry.Metadata.OptionGroups is not { Count: > 0 })
            {
                ImGui.Separator();
                // Geometry sits directly above Advanced, as on the other paths.
                if (ColorTableEditor.EffectsHeader(entry.ModDirectory))
                    DrawSkindent(entry);
                DrawGeometrySection(entry);
                // "###" keeps the localized label out of the id, so a language switch doesn't close it.
                if (ImGui.CollapsingHeader($"{Strings.Colors.Advanced}###content_{entry.ModDirectory}"))
                    DrawBodiesAdvanced(entry);
                return;
            }
            ImGui.Separator();
        }

        // ── simple-mod path (top-level Overlays, no OptionGroups) ────────────
        if (entry.Metadata.OptionGroups is not { Count: > 0 })
        {
            DrawSimpleModEditor(entry, overrideActive, bodyType, effects);
            return;
        }

        // ── option-group path ─────────────────────────────────────────────────
        DrawOptionGroupEditor(entry, overrideActive, bodyType, effects);
    }

    private void DrawSimpleModEditor(OverlayEntry entry, bool overrideActive, string? bodyType, List<(string Name, string Path, bool FromMod)> effects)
    {
        var metaRows = entry.Metadata.ColorTableRows ?? [];
        // While editing a binding, always work on a copy: the compositor reads the override from another thread.
        var ovrRows  = overrideActive ? editRouter.PeekOverrideRows(entry.ModDirectory, null, null) : null;
        var rows = overrideActive ? DesignBindingService.CopyRows(ovrRows ?? metaRows) : metaRows;

        var usedRowsSimple = new HashSet<int>();
        var columnsSimple  = new List<string?>();
        bool hasIdxSimple  = false;
        foreach (var ov in entry.Metadata.Overlays ?? [])
        {
            if (ov.Index == null) continue;
            var idxPath = Path.Combine(entry.SidecarRoot, ov.Index);
            if (!_indexRowCache.ContainsKey(idxPath))
                _indexRowCache[idxPath] = ScanIndexFile(idxPath, bodyType);
            usedRowsSimple.UnionWith(_indexRowCache[idxPath].Rows);
            columnsSimple.Add(_indexRowCache[idxPath].SubRow);
            hasIdxSimple = true;
        }
        // Active "Masks" options can add rows via their own Index companion.
        foreach (var asset in discovery.ResolveActiveMaskAssets(entry))
        {
            if (asset.IndexPath == null) continue;
            if (!_indexRowCache.ContainsKey(asset.IndexPath))
                _indexRowCache[asset.IndexPath] = ScanIndexFile(asset.IndexPath, bodyType);
            usedRowsSimple.UnionWith(_indexRowCache[asset.IndexPath].Rows);
            columnsSimple.Add(_indexRowCache[asset.IndexPath].SubRow);
            hasIdxSimple = true;
        }
        var (filteredSimple, columnSimple) =
            OverlayRowFilter(hasIdxSimple, usedRowsSimple, AgreedColumn(columnsSimple));
        if (!hasIdxSimple)
            ProteusStyle.DisabledWrapped(Strings.ColorPanel.NoIndexTexture);

        // While a binding is edited, layer/shader go into its gear override instead of metadata.json.
        var simpleOverlays = entry.Metadata.Overlays ?? [];
        var gearOvrSimple = overrideActive && simpleOverlays.Count > 0
            ? editRouter.GetEditableGearOverride(entry.ModDirectory, null, null, simpleOverlays[0])
            : null;
        var modeBeforeSimple = EffectiveMode(simpleOverlays, gearOvrSimple);
        var (gearSimple, shaderSimple) = ColorTableEditor.EffectiveLayerShader(simpleOverlays, gearOvrSimple);

        bool changedSimple = false;
        int selSimple = _rowSelection.GetValueOrDefault(entry.ModDirectory, 0);
        ColorTableEditor.DrawRows(entry.ModDirectory, rows, filteredSimple, gearSimple, shaderSimple,
            compositor.GetShellMaterials(entry.ModDirectory, null, null),
            compositor.GetSkinGlowTargets(entry.ModDirectory, null, null),
            out var rowEditSimple, ref selSimple, ref changedSimple,
            usedSubRow: columnSimple);
        _rowSelection[entry.ModDirectory] = selSimple;

        // Glow effect + Advanced live at the very bottom, below the rows.
        ImGui.Separator();
        bool resetSimple = false;
        // Reinforced toe is the one footer control a preset does not capture; note it so it can be saved separately.
        int toeDensityBeforeSimple = simpleOverlays.FirstOrDefault()?.ToeCapDensity ?? 0;
        bool footerChangedSimple = ColorTableEditor.DrawGlowFooter(
            entry.ModDirectory, entry.ModDirectory, simpleOverlays, gearOvrSimple, effects,
            out var footerEditSimple,
            onReset: () => resetSimple = ResetToDefaults(entry, null, null),
            resetDisabledReason: ResetBlockedReason(entry),
            drawExtraAdvanced: () => DrawBodiesAdvanced(entry),
            drawBelowGlow: () => DrawGeometrySection(entry),
            drawInEffects: () => DrawSkindent(entry),
            overrideActive: overrideActive,
            toeCapActive: compositor.ToeCapWantedFor(entry.ModDirectory)
                       && CanReinforceToe(gearSimple, shaderSimple));

        // A reset restored the intended values, so skip mode re-inference and the glow transition this frame.
        bool modeChangedSimple = false;
        if (!resetSimple)
        {
            // Let the features drive the render mode (unless the user pinned it). Runs after all draws.
            modeChangedSimple = ReconcileMode(simpleOverlays, gearOvrSimple, rows,
                rowEditSimple != FeatureEdit.Neutral ? rowEditSimple : footerEditSimple);

            // Default the glow rows only when the mode enters Animated glow and zero them when it leaves; not on an effect swap.
            ApplyGlowTransition(rows, modeBeforeSimple, EffectiveMode(simpleOverlays, gearOvrSimple));
        }

        if (changedSimple || footerChangedSimple || modeChangedSimple)
        {
            // Binding path: install the edit into the in-memory override now; base metadata persists only when not binding, or on a reset.
            if (overrideActive && !resetSimple)
                editRouter.SetOverrideRows(entry.ModDirectory, null, null, rows);
            // Not on a reset: `rows` predates the restore and would undo it.
            if (!overrideActive && !resetSimple)
                entry.Metadata.ColorTableRows = rows;   // may be the list we created for an empty mod
            if (!overrideActive || resetSimple) { discovery.SaveMetadata(entry); InvalidateDefaultsCache(entry); }
            // Under a preset, save the reinforced toe alone; a full save would make every preview edit permanent.
            else if ((simpleOverlays.FirstOrDefault()?.ToeCapDensity ?? 0) != toeDensityBeforeSimple)
                discovery.SaveToeCapDensity(entry, simpleOverlays);
            // Discrete footer/mode changes recomposite promptly; colour-row drags use the debounce.
            if (footerChangedSimple || modeChangedSimple) RecompositeForOverlay(entry, "mode-change");
            // Rows only, so skin reuse may apply: a change that moves a skin texel still shows in the skin fingerprint.
            else RecompositeForOverlay(entry, "colors-change", ColorEditDebounceMs,
                skinFingerprintAuthoritative: true);
        }
    }

    private void DrawOptionGroupEditor(OverlayEntry entry, bool overrideActive, string? bodyType, List<(string Name, string Path, bool FromMod)> effects)
    {
        var activeOptions = ActiveOptionsFor(entry);

        // One "Masks" tab, on top, editing the shared MaskColorTableRows, whenever an active mask carries an _id.
        var maskAssets = discovery.ResolveActiveMaskAssets(entry);
        bool anyMaskWithId = maskAssets.Any(a => a.IndexPath != null);

        // Why this mod is painting nothing, drawn above the tab strip: the hardest causes leave options ticked and tabs on screen.
        var inertReason = compositor.GetInertReason(entry.ModDirectory);
        var inertText   = inertReason is { } r ? DescribeInert(r) : null;

        if (activeOptions.Count == 0 && !anyMaskWithId)
        {
            // Nothing selected: keep Bodies and geometry reachable, and say the specific inert reason when known.
            if (inertText != null)
                ProteusStyle.WarnWrapped(inertText);
            else
                ProteusStyle.DisabledWrapped(Strings.ColorPanel.NoActiveOptions);
            // The only place geometry features can be reached for a mod with no active option.
            if (ColorTableEditor.EffectsHeader(entry.ModDirectory))
                DrawSkindent(entry);
            DrawGeometrySection(entry);
            if (ImGui.CollapsingHeader($"{Strings.Colors.Advanced}###noopt_{entry.ModDirectory}"))
                DrawBodiesAdvanced(entry);
            return;
        }

        if (inertText != null)
        {
            ProteusStyle.WarnWrapped(inertText);
            ImGui.Spacing();
        }

        // Tabs in true stacking order, top-first: the compositor's order reversed.
        activeOptions = SortTopFirst(entry, activeOptions);

        // Masks always render on top: a synthesized "Masks" tab at index 0, its rows in MaskColorTableRows.
        if (anyMaskWithId)
            activeOptions.Insert(0, (SidecarDiscoveryService.MaskGroupName, new OverlayOption { Name = "Masks" }));

        // Resolve the selected overlay by identity, so a reorder doesn't change what you're editing.
        int selIdx = _colorEditorSel.TryGetValue(entry.ModDirectory, out var wantKey)
            ? activeOptions.FindIndex(x => SelKey(x.GroupName, x.Option.Name) == wantKey)
            : -1;
        if (selIdx < 0) selIdx = 0;

        // One tab per active overlay; left→right is stacking order. Custom buttons, not a TabBar: a TabBar remembers each
        // tab's slot by id and won't reorder on resubmission.
        if (activeOptions.Count > 1)
            selIdx = DrawStackTabs(entry, activeOptions, selIdx, overrideActive);

        var (groupName, activeOpt) = activeOptions[selIdx];

        // The synthesized "Masks" tab: one shared colorset coloured by the combined mask _id.
        bool isMask = string.Equals(groupName, SidecarDiscoveryService.MaskGroupName, StringComparison.Ordinal);
        var (usedRows, usedColumn) = ScanOptionRows(entry, activeOpt, maskAssets, bodyType);

        // ── Masks tab: one shared colorset for all active masks ───────────────────────────────────────
        // Masks composite into one top layer: forced to a Cloth shell when the mod has gear, otherwise its own mode in MaskDescriptor.
        if (isMask)
        {
            DrawMasksTab(entry, activeOptions, overrideActive, effects, usedRows, usedColumn);
            return;
        }

        DrawOptionTab(entry, activeOptions, selIdx, groupName, activeOpt, overrideActive, effects, usedRows, usedColumn);
    }

    private static string SelKey(string g, string o) => g + "\0" + o;

    /// <summary>The selected options of every option group, excluding "None"; the first option of each group when Penumbra cannot be asked.</summary>
    private List<(string GroupName, OverlayOption Option)> ActiveOptionsFor(OverlayEntry entry)
    {
        var collId   = penumbra.GetPlayerCollectionId();
        var settings = collId.HasValue ? penumbra.GetModSettings(collId.Value, entry.ModDirectory) : null;

        var activeOptions = new List<(string GroupName, OverlayOption Option)>();
        // Only called once DrawColorEditorBody has seen option groups.
        foreach (var group in entry.Metadata.OptionGroups!)
        {
            if (group.Options.Count == 0) continue;
            // Case-insensitive: we don't control the comparer of Penumbra's IPC dictionary.
            List<string>? selected = settings.HasValue
                ? settings.Value.Options
                    .FirstOrDefault(kv => string.Equals(kv.Key, group.PenumbraGroupName, StringComparison.OrdinalIgnoreCase))
                    .Value
                : null;

            // Nothing selected contributes nothing, as in the composite; preview the first option only when Penumbra can't be asked.
            IEnumerable<OverlayOption> active;
            if (selected is { Count: > 0 })
                active = group.Options.Where(o => selected.Any(s =>
                    string.Equals(o.Name, s, StringComparison.OrdinalIgnoreCase)));
            else if (settings.HasValue)
                continue;
            else
                active = [group.Options[0]];

            // Within a group, the user's saved stack order, top-first; stable for options never reordered.
            active = active.OrderBy(o => config.StackIndexOf(entry.ModDirectory, group.PenumbraGroupName, o.Name));

            foreach (var opt in active)
            {
                if (opt.Name.EndsWith("None", StringComparison.OrdinalIgnoreCase))
                    continue;
                activeOptions.Add((group.PenumbraGroupName, opt));
            }
        }
        return activeOptions;
    }

    /// <summary>Top-first stacking order, the reverse of the order the compositor paints in.</summary>
    private List<(string GroupName, OverlayOption Option)> SortTopFirst(OverlayEntry entry, List<(string GroupName, OverlayOption Option)> activeOptions)
    {
        var modRoot = entry.ModRoot;
        if (!_groupOrderCache.TryGetValue(entry.ModDirectory, out var gOrder))
        {
            gOrder = modRoot != null
                ? SidecarDiscoveryService.ReadGroupOrder(modRoot)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _groupOrderCache[entry.ModDirectory] = gOrder;
        }

        int GroupOrderOf(string g) => gOrder.TryGetValue(g, out var v) ? v : int.MaxValue;

        // Under an active design binding the mod-wide order lives on the binding; order the buttons as the composite does.
        var stackOvr = editRouter.ActiveStackOrderFor(entry.ModDirectory);
        int ModStackIdx(string group, string option)
            => stackOvr != null
                ? Configuration.ModStackIndexIn(stackOvr, group, option)
                : config.ModStackIndexOf(entry.ModDirectory, group, option);

        return activeOptions
            .OrderBy(x => ModStackIdx(x.GroupName, x.Option.Name))
            .ThenBy(x => GroupOrderOf(x.GroupName))
            .ThenBy(x => config.StackIndexOf(entry.ModDirectory, x.GroupName, x.Option.Name))
            .ToList();
    }

    /// <summary>One tab per active option, drag or arrow to restack; returns the selected index.</summary>
    private int DrawStackTabs(OverlayEntry entry, List<(string GroupName, OverlayOption Option)> activeOptions, int selIdx, bool overrideActive)
    {
        bool multiGroup = activeOptions.Select(x => x.GroupName).Distinct().Count() > 1;

        var cp = Strings.ColorPanel;
        ProteusStyle.DisabledWrapped(cp.StackHint);

        // Src/Dst are SelKey values, so a tab is identified across groups, not just within one.
        (string Src, string Dst)? pendingReorder = null;
        for (int i = 0; i < activeOptions.Count; i++)
        {
            if (i > 0) ImGui.SameLine();
            var (gName, opt) = activeOptions[i];
            bool isMaskTab = string.Equals(gName, SidecarDiscoveryService.MaskGroupName, StringComparison.Ordinal);
            var label = isMaskTab
                ? cp.MasksTab
                : multiGroup ? string.Format(cp.GroupOptionFmt, gName, opt.Name) : opt.Name;

            using (ProteusStyle.Selected(i == selIdx))
                if (ImGui.Button($"{label}##otab_{entry.ModDirectory}_{gName}_{opt.Name}"))
                {
                    selIdx = i;
                    _colorEditorSel[entry.ModDirectory] = SelKey(gName, opt.Name);
                }

            // The Masks tab is pinned on top: neither a drag source nor a drop target.
            if (isMaskTab)
            {
                ImGui.SameLine(0, 4);
                ImGui.TextDisabled("|");
                continue;
            }

            // Drag source; the payload is a bare marker and the identity rides in _stackDragSrc.
            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
            {
                _stackDragSrc = (entry.ModDirectory, gName, opt.Name);
                ImGui.SetDragDropPayload("PROTEUS_STACK", StackDragMarker, ImGuiCond.None);
                ImGui.Text(label);
                ImGui.EndDragDropSource();
            }
            if (ImGui.BeginDragDropTarget())
            {
                var pl = ImGui.AcceptDragDropPayload("PROTEUS_STACK", ImGuiDragDropFlags.None);
                // Any tab onto any other tab of the same mod, across groups (see Configuration.OverlayModStackOrder).
                if (!pl.IsNull && _stackDragSrc is { } s
                    && s.Mod == entry.ModDirectory && (s.Group != gName || s.Option != opt.Name))
                    pendingReorder = (SelKey(s.Group, s.Option), SelKey(gName, opt.Name));
                ImGui.EndDragDropTarget();
            }
        }

        // Arrows and drag operate over every active option of this mod as one stack.
        var stackKeys = activeOptions.Select(x => SelKey(x.GroupName, x.Option.Name)).ToList();

        void PersistStack(List<string> keysTopFirst)
        {
            var topFirst = keysTopFirst.Select(k =>
            {
                var parts = k.Split('\0');
                return (Group: parts[0], Option: parts.Length > 1 ? parts[1] : "");
            }).ToList();

            // Under a binding the restack is a live-preview override on it; otherwise it goes to the global config.
            if (!(overrideActive && editRouter.SetEditableStackOrder(entry.ModDirectory, topFirst)))
                config.SetModStackOrder(entry.ModDirectory, topFirst);
            RecompositeForOverlay(entry, "stack-reorder");
        }

        // ── ◀ ▶ arrows: restack the selected overlay (drag alternative) ──
        if (stackKeys.Count > 1)
        {
            int pos = selIdx;
            // The pinned Masks tab can't be restacked.
            bool selIsMask = string.Equals(activeOptions[selIdx].GroupName,
                SidecarDiscoveryService.MaskGroupName, StringComparison.Ordinal);

            void MoveTo(int np)
            {
                if (np < 0 || np >= stackKeys.Count) return;
                var moved = stackKeys[pos];
                stackKeys.RemoveAt(pos);
                stackKeys.Insert(np, moved);
                PersistStack(stackKeys);
            }

            using (ImRaii.Disabled(pos == 0 || selIsMask))
                if (ImGui.SmallButton($"◀ {cp.TowardTop}##stackup_{entry.ModDirectory}")) MoveTo(pos - 1);
            ImGui.SameLine();
            using (ImRaii.Disabled(pos == stackKeys.Count - 1 || selIsMask))
                if (ImGui.SmallButton($"{cp.TowardBottom} ▶##stackdn_{entry.ModDirectory}")) MoveTo(pos + 1);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cp.StackTip);
        }

        // Apply a drag drop after drawing (persist + recomposite; next frame re-sorts the tabs).
        if (pendingReorder is { } pr)
        {
            int srcIdx = stackKeys.IndexOf(pr.Src);
            int dstIdx = stackKeys.IndexOf(pr.Dst);
            if (srcIdx >= 0 && dstIdx >= 0 && srcIdx != dstIdx)
            {
                // Insert at the target's original index: this lands after it when dragging rightward, before it when dragging leftward.
                stackKeys.RemoveAt(srcIdx);
                stackKeys.Insert(dstIdx, pr.Src);
                PersistStack(stackKeys);
            }
        }
        return selIdx;
    }

    /// <summary>The colour rows the option's index texture and the active masks' index textures reach.</summary>
    private (HashSet<int>? Rows, string? Column) ScanOptionRows(OverlayEntry entry, OverlayOption activeOpt,
        List<(string MaskPath, string? NormalPath, string? IndexPath)> maskAssets, string? bodyType)
    {
        var scannedRows = new HashSet<int>();
        var scannedColumns = new List<string?>();
        var idxDesc = activeOpt.Overlays.FirstOrDefault(o => o.Index != null);
        if (idxDesc?.Index != null)
        {
            var idxPath = Path.Combine(entry.SidecarRoot, idxDesc.Index);
            if (!_indexRowCache.ContainsKey(idxPath))
                _indexRowCache[idxPath] = ScanIndexFile(idxPath, bodyType);
            var scan = _indexRowCache[idxPath];
            scannedRows.UnionWith(scan.Rows);
            scannedColumns.Add(scan.SubRow);
        }

        // Active "Masks" options can add rows via their own Index companion, so union those in.
        foreach (var asset in maskAssets)
        {
            if (asset.IndexPath == null) continue;
            if (!_indexRowCache.ContainsKey(asset.IndexPath))
                _indexRowCache[asset.IndexPath] = ScanIndexFile(asset.IndexPath, bodyType);
            var maskScan = _indexRowCache[asset.IndexPath];
            scannedRows.UnionWith(maskScan.Rows);
            scannedColumns.Add(maskScan.SubRow);
        }

        bool hasAnyIndex = idxDesc?.Index != null || maskAssets.Any(a => a.IndexPath != null);
        var (usedRows, usedColumn) =
            OverlayRowFilter(hasAnyIndex, scannedRows, AgreedColumn(scannedColumns));
        if (!hasAnyIndex)
            ImGui.TextDisabled(Strings.ColorPanel.NoIndexTexture);
        return (usedRows, usedColumn);
    }

    private void DrawMasksTab(OverlayEntry entry, List<(string GroupName, OverlayOption Option)> activeOptions, bool overrideActive,
        List<(string Name, string Path, bool FromMod)> effects, HashSet<int>? usedRows, string? usedColumn)
    {
        // Commit the list to metadata only on an actual edit, so selecting the tab has no side effect.
        var baseMaskRows = entry.Metadata.MaskColorTableRows ?? [];
        // Under a binding, work on a copy until an edit happens: materialising the override on read would shadow later metadata edits.
        var storedMaskRows = overrideActive ? editRouter.PeekMaskRows(entry.ModDirectory) : null;
        var maskRows = overrideActive
            ? DesignBindingService.CopyRows(storedMaskRows ?? baseMaskRows)
            : baseMaskRows;
        var maskScope = $"{entry.ModDirectory}_{SidecarDiscoveryService.MaskGroupName}";
        int maskSel   = _rowSelection.GetValueOrDefault(maskScope, 0);
        bool maskChanged = false;

        // With any gear layer the mask is forced to a top Cloth shell; all skin, it gets its own mode. Uses the effective
        // layer, since a binding can flip an option to Skin.
        bool modHasGear = activeOptions.Any(x =>
        {
            if (string.Equals(x.GroupName, SidecarDiscoveryService.MaskGroupName, StringComparison.Ordinal))
                return false;
            var ovr = editRouter.PeekGearOverride(entry.ModDirectory, x.GroupName, x.Option.Name);
            // Per descriptor: the compositor turns every Gear descriptor into its own shell.
            return x.Option.Overlays.Any(d => ColorTableEditor.EffectiveLayerShader([d], ovr).Gear);
        });

        // The mask's working descriptor, or a fresh Skin default; the binding's gear override is mutated instead when active.
        var maskDesc = entry.Metadata.MaskDescriptor ?? new OverlayDescriptor { Layer = OverlayLayer.Skin };
        var maskGearOvr = overrideActive
            ? editRouter.GetEditableMaskGearOverride(entry.ModDirectory, maskDesc)
            : null;

        var maskModeBefore = EffectiveMode([maskDesc], maskGearOvr);
        bool maskGear; string? maskShader;
        if (modHasGear) { maskGear = true; maskShader = OverlayDescriptor.DefaultGearShader; }
        else (maskGear, maskShader) = ColorTableEditor.EffectiveLayerShader([maskDesc], maskGearOvr);
        bool maskAsGear = maskGear;

        var maskShellMaterials = maskAsGear
            ? compositor.GetShellMaterials(entry.ModDirectory, SidecarDiscoveryService.MaskGroupName, "Masks")
            : null;
        var maskGlowTargets = maskAsGear
            ? null
            : compositor.GetSkinGlowTargets(entry.ModDirectory, SidecarDiscoveryService.MaskGroupName, "Masks");

        // Cold-boot warmup, as on the overlay path, guarded per mask layer since skin and shell need different locator data.
        bool maskLocatorMissing = maskAsGear
            ? maskShellMaterials == null || maskShellMaterials.Count == 0
            : maskGlowTargets == null || maskGlowTargets.Count == 0;
        // entry.Enabled ahead of the one-shot — see the overlay warmup below.
        if (config.PluginEnabled && entry.Enabled && maskLocatorMissing
            && _glowWarmedMods.Add($"{entry.ModDirectory}\0masks\0{(maskAsGear ? 'g' : 's')}"))
            compositor.TriggerRecomposite("mask-glow-warmup");

        // The toe cap is stripped from every mask list yet promotes the mod's skin overlays to cloth, so show it on the tab owning its group.
        if (compositor.ToeCapWantedFor(entry.ModDirectory))
        {
            ImGui.TextDisabled(Strings.ColorPanel.ToeCapOn);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Strings.ColorPanel.ToeCapOnTip);
            ImGui.Separator();
        }

        ColorTableEditor.DrawRows(maskScope, maskRows, usedRows, maskAsGear, maskShader,
            maskShellMaterials,
            maskGlowTargets,
            out var maskRowEdit, ref maskSel, ref maskChanged,
            // On the gear layer a half-authored row pair renders with its unset half mirrored; on skin an unset sub-row is neutral.
            mirrorUnsetSubRows: maskAsGear,
            usedSubRow: usedColumn);
        _rowSelection[maskScope] = maskSel;

        ImGui.Separator();
        bool maskFooterChanged = false, maskModeChanged = false;
        {
            // The same footer as the overlay tabs, with only the mode radios replaced when forced. No per-option reset for masks,
            // and Bodies is mod-wide, so it is drawn on the option tabs instead.
            maskFooterChanged = ColorTableEditor.DrawGlowFooter(
                maskScope, entry.ModDirectory, [maskDesc], maskGearOvr, effects,
                out var maskFooterEdit, onReset: null,
                drawExtraAdvanced: null,
                drawBelowGlow: () => DrawGeometrySection(entry),
                drawInEffects: () => DrawSkindent(entry),
                modeForced: modHasGear ? Strings.ColorPanel.Forced : null,
                modeForcedTip: modHasGear ? Strings.ColorPanel.ForcedTip : null,
                // Shows the badge as Cloth without persisting it, which is what "forced" means here.
                promotedToGear: modHasGear,
                // Nothing reads MaskDescriptor.SkinToneMask, so the slider would do nothing.
                skinTintApplies: false,
                overrideActive: overrideActive);
            var maskEdit = maskRowEdit != FeatureEdit.Neutral ? maskRowEdit : maskFooterEdit;

            // While the mode is forced only an explicit glow pick may move it: inference would silently overwrite the recorded layer,
            // but characterscroll is the only shader that emits, so a glow pick must still switch it.
            if (!modHasGear || maskEdit == FeatureEdit.Glow)
                maskModeChanged = ReconcileMode([maskDesc], maskGearOvr, maskRows, maskEdit);

            // Always, forced or not: defaults the glow rows on entering Glow and zeroes them on leaving.
            ApplyGlowTransition(maskRows, maskModeBefore, EffectiveMode([maskDesc], maskGearOvr));
        }

        if (maskChanged || maskFooterChanged || maskModeChanged)
        {
            // Binding path: install the edited rows as the binding's live override; base metadata persists only when not binding.
            if (overrideActive)
            {
                editRouter.SetMaskRows(entry.ModDirectory, maskRows);
            }
            else
            {
                entry.Metadata.MaskColorTableRows = maskRows;
                if (maskFooterChanged || maskModeChanged) entry.Metadata.MaskDescriptor = maskDesc;
                discovery.SaveMetadata(entry);
                InvalidateDefaultsCache(entry);
            }
            if (maskFooterChanged || maskModeChanged) RecompositeForOverlay(entry, "mask-mode-change");
            // Rows only: hashed in the fingerprint's `maskrow:` block, so the skin-reuse gate can be trusted.
            else RecompositeForOverlay(entry, "mask-colors-change", ColorEditDebounceMs,
                skinFingerprintAuthoritative: true);
        }
    }

    private void DrawOptionTab(OverlayEntry entry, List<(string GroupName, OverlayOption Option)> activeOptions, int selIdx, string groupName,
        OverlayOption activeOpt, bool overrideActive, List<(string Name, string Path, bool FromMod)> effects, HashSet<int>? usedRows, string? usedColumn)
    {
        var optRows = activeOpt.ColorTableRows ?? [];
        var ovrOptRows = overrideActive
            ? editRouter.PeekOverrideRows(entry.ModDirectory, groupName, activeOpt.Name)
            : null;
        var editRows = overrideActive ? DesignBindingService.CopyRows(ovrOptRows ?? optRows) : optRows;

        var scope = $"{entry.ModDirectory}_{groupName}_{activeOpt.Name}";

        // While a binding is edited, layer/shader go into its gear override instead of metadata.json.
        var gearOvrOpt = overrideActive && activeOpt.Overlays.Count > 0
            ? editRouter.GetEditableGearOverride(entry.ModDirectory, groupName, activeOpt.Name, activeOpt.Overlays[0])
            : null;
        var modeBefore = EffectiveMode(activeOpt.Overlays, gearOvrOpt);
        var (gear, shader) = ColorTableEditor.EffectiveLayerShader(activeOpt.Overlays, gearOvrOpt);

        // Auto promotion mirrors the compositor through the same predicate (RenderModeInference.ShouldPromoteToGear).
        // canShell: shells are cut from the wearer's own skin, so an option painting gear, an accessory or a weapon cannot become one.
        bool canShell = activeOpt.Overlays.Count == 0
                     || activeOpt.Overlays.Any(CompositorService.CanRenderAsShell);
        var noShellReason = canShell ? null
            : "This overlay paints something Proteus can't build a layer over — gear, an accessory or a\n"
            + "weapon. Glow and Cloth need a layer over your own skin: body, face, hair, tail or ears.";

        bool promotedToGear = false;
        if (!gear)
        {
            // !gear: the effective layer is Skin. A binding's pin outranks the descriptor's.
            bool pinned = gearOvrOpt?.ManualShaderLock
                ?? activeOpt.Overlays.FirstOrDefault()?.ManualShaderLock ?? false;
            bool aboveGear = activeOptions.Skip(selIdx + 1).Any(x => x.Option.Overlays.Any(d => d.Layer == OverlayLayer.Gear));
            // Asymmetric art on the mirrored body; asked of the compositor because it depends on the body worn now.
            bool needsUnmirrored = activeOpt.Overlays.Any(compositor.NeedsUnmirroredShell);
            // A toe cap selected in this mod promotes its skin overlays; asked of the compositor, which reads that group's selection.
            bool capWanted = compositor.ToeCapWantedFor(entry.ModDirectory);
            // This mod's geometry passes promote too, through the shared predicate.
            bool geometryWanted = RenderModeInference.WantsGeometry(entry.Metadata);
            if (RenderModeInference.ShouldPromoteToGear(OverlayLayer.Skin, pinned, editRows, aboveGear, canShell,
                                                        needsUnmirrored, capWanted, geometryWanted))
            {
                // The shader comes from the shared predicate: a promoted whole-skin overlay renders on skin.shpk.
                gear = true; promotedToGear = true;
                shader = RenderModeInference.PromotedShader(
                    activeOpt.Overlays.FirstOrDefault() ?? new OverlayDescriptor(), editRows);
            }
        }
        // A stored Gear layer no shell can cover renders as skin, so the panel reads as skin too.
        else if (!canShell)
        {
            gear = false; shader = OverlayDescriptor.SkinShader;
        }

        bool changed = false;
        int sel = _rowSelection.GetValueOrDefault(scope, 0);

        var skinGlowTargets = compositor.GetSkinGlowTargets(entry.ModDirectory, groupName, activeOpt.Name);
        var shellMaterials  = compositor.GetShellMaterials(entry.ModDirectory, groupName, activeOpt.Name);

        // Cold-boot warmup: the Glow button's locator data only exists after a composite processed this option, so fire one
        // recomposite the first time it is missing.
        bool locatorDataMissing = gear
            ? shellMaterials  == null || shellMaterials.Count  == 0
            : skinGlowTargets == null || skinGlowTargets.Count == 0;
        // Guarded per (mod, group, option, layer), since switching layer needs different locator data, and only for enabled
        // mods, so the one-shot is not spent on a run that never happens.
        if (config.PluginEnabled && entry.Enabled && activeOpt.Overlays.Count > 0 && locatorDataMissing
            && _glowWarmedMods.Add($"{entry.ModDirectory}\0{groupName}\0{activeOpt.Name}\0{(gear ? 'g' : 's')}"))
            compositor.TriggerRecomposite("glow-warmup");

        ColorTableEditor.DrawRows(scope, editRows, usedRows, gear, shader,
            shellMaterials,
            skinGlowTargets,
            out var rowEdit, ref sel, ref changed,
            usedSubRow: usedColumn);
        _rowSelection[scope] = sel;

        // Glow effect + Advanced live at the very bottom, below the rows.
        ImGui.Separator();
        bool resetOpt = false;
        // See the simple-mod path: the one footer control a preset does not capture.
        int toeDensityBefore = activeOpt.Overlays.FirstOrDefault()?.ToeCapDensity ?? 0;
        bool footerChanged = ColorTableEditor.DrawGlowFooter(
            scope, entry.ModDirectory, activeOpt.Overlays, gearOvrOpt, effects, out var footerEdit,
            onReset: () => resetOpt = ResetToDefaults(entry, groupName, activeOpt),
            resetDisabledReason: ResetBlockedReason(entry),
            drawExtraAdvanced: () => DrawBodiesAdvanced(entry),
            drawBelowGlow: () => DrawGeometrySection(entry),
            drawInEffects: () => DrawSkindent(entry),
            promotedToGear: promotedToGear,
            noShellReason: noShellReason,
            overrideActive: overrideActive,
            // `gear` and `shader` are what actually renders, after promotion and demotion.
            toeCapActive: compositor.ToeCapWantedFor(entry.ModDirectory) && CanReinforceToe(gear, shader));

        // A reset restored the intended values, so skip mode re-inference and the glow transition this frame.
        bool modeChanged = false;
        if (!resetOpt)
        {
            modeChanged = ReconcileMode(activeOpt.Overlays, gearOvrOpt, editRows,
                rowEdit != FeatureEdit.Neutral ? rowEdit : footerEdit, canShell);

            // Default the glow rows only when the mode enters Animated glow and zero them when it leaves; not on an effect swap.
            ApplyGlowTransition(editRows, modeBefore, EffectiveMode(activeOpt.Overlays, gearOvrOpt));
        }

        if (changed || footerChanged || modeChanged)
        {
            // Binding path: install the edited rows as the binding's live override; base metadata persists only when not binding, or on a reset.
            if (overrideActive && !resetOpt)
                editRouter.SetOverrideRows(entry.ModDirectory, groupName, activeOpt.Name, editRows);
            // NOT on a reset — see the simple-mod path: the reset already rewrote activeOpt in place.
            if (!overrideActive && !resetOpt)
                activeOpt.ColorTableRows = editRows;
            if (!overrideActive || resetOpt) { discovery.SaveMetadata(entry); InvalidateDefaultsCache(entry); }
            // Under a preset, save the reinforced toe alone, as on the simple-mod path.
            else if ((activeOpt.Overlays.FirstOrDefault()?.ToeCapDensity ?? 0) != toeDensityBefore)
                discovery.SaveToeCapDensity(entry, activeOpt.Overlays);
            // Discrete footer/mode changes recomposite promptly; colour-row drags use the debounce.
            if (footerChanged || modeChanged) RecompositeForOverlay(entry, "mode-change");
            // Rows only — see the simple-mod path above.
            else RecompositeForOverlay(entry, "colors-change", ColorEditDebounceMs,
                skinFingerprintAuthoritative: true);
        }
    }

    /// <summary>
    /// Whether the mod's overlays bake onto vanilla (gen2) skin the character wears, in the colour panel's Advanced disclosure.
    /// Per mod and global config, so it commits for itself rather than through the footer's change flag.
    /// </summary>
    private void DrawBodiesAdvanced(OverlayEntry entry)
    {
        bool vanilla = config.OverlaysVanillaFor(entry.ModDirectory);

        var cp = Strings.ColorPanel;
        if (ImGui.Checkbox($"{cp.OverlayVanilla}##bodies_{entry.ModDirectory}", ref vanilla))
        {
            // On is the absent default, so ticking removes the entry.
            if (vanilla) config.SiblingSynthesis.Remove(entry.ModDirectory);
            else config.SiblingSynthesis[entry.ModDirectory] = SiblingSynthesisMode.Off;
            config.Save();
            RecompositeForOverlay(entry, "sibling-mode");
        }
        // The router, not the binding service: neither a binding nor a preset captures Bodies.
        bool binding = editRouter.HasOverride(entry.ModDirectory);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cp.BodiesTip + (binding ? cp.BodiesGlobalSuffix : ""));

        // Said in the panel, not only on hover: this control writes global config while everything else previews.
        if (binding)
            ImGui.TextDisabled(cp.BodiesGlobalNote);
    }

    /// <summary>Ambient occlusion + Skindenting for this mod, in the Effects section; global config, so it commits for itself.
    /// A combo for three states: the pack decides, forced on, forced off.</summary>
    private void DrawSkindent(OverlayEntry entry)
    {
        var ms = Strings.Mods;
        bool? aoDeclared = entry.Metadata?.AmbientOcclusion;
        // The user's stored opinion: the new override, else a legacy opt-out, else none.
        bool? aoChoice = config.AmbientOcclusionOverrides.TryGetValue(entry.ModDirectory, out var aoUser)
            ? aoUser
            : config.AmbientOcclusionDisabledMods.Contains(entry.ModDirectory) ? false : null;
        string aoPackLabel = string.Format(ms.AoPackFmt, aoDeclared == true ? ms.AoOn : ms.AoOff);
        ImGui.SetNextItemWidth(ProteusStyle.S(120f));
        if (ImGui.BeginCombo($"{ms.Skindent}###ao_{entry.ModDirectory}",
                aoChoice == null ? aoPackLabel : aoChoice.Value ? ms.AoOn : ms.AoOff))
        {
            foreach (var (label, choice) in new[] { (aoPackLabel, (bool?)null), (ms.AoOn, true), (ms.AoOff, false) })
            {
                if (!ImGui.Selectable(label, choice == aoChoice) || choice == aoChoice) continue;
                if (choice == null) config.AmbientOcclusionOverrides.Remove(entry.ModDirectory);
                else                config.AmbientOcclusionOverrides[entry.ModDirectory] = choice.Value;
                // The legacy opt-out set is read-only; any choice replaces it.
                config.AmbientOcclusionDisabledMods.Remove(entry.ModDirectory);
                config.Save();
                RecompositeForOverlay(entry, "ambient-occlusion-mod");
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Format(ms.AoTipFmt, aoPackLabel));
    }

    /// <summary>Whether an option on this layer and shader can carry a reinforced toe: a shell not on <c>skin.shpk</c>,
    /// where the normal's blue channel is skin-colour influence rather than density.</summary>
    private static bool CanReinforceToe(bool gear, string? shader)
        => gear && !string.Equals(shader, OverlayDescriptor.SkinShader, StringComparison.OrdinalIgnoreCase);

    private void DrawGeometrySection(OverlayEntry entry)
    {
        // The mod-wide geometry section, drawn on every tab. "###" keeps the localized label out of the id.
        if (ImGui.CollapsingHeader($"{Strings.Colors.GeometrySection}###geometry_{entry.ModDirectory}"))
            DrawBustBridgeAdvanced(entry);
    }

    /// <summary>
    /// The four mod-wide geometry passes, written to the sidecar because they are authoring decisions that ship with the pack.
    /// Ticking any promotes the mod's skin overlays to a shell, so <see cref="RenderModeInference.WantsGeometry"/> must list every one.
    /// </summary>
    private void DrawBustBridgeAdvanced(OverlayEntry entry)
    {
        var md = entry.Metadata;
        var cs = Strings.Colors;

        // On or off, no amount: ticking writes "on, full", clearing any authored strength (which the compositor still honours).
        void Toggle(string label, string id, string tip, bool current,
                    Action<bool> set, string reason)
        {
            bool on = current;
            if (ImGui.Checkbox($"{label}##{id}_{entry.ModDirectory}", ref on))
            {
                set(on);
                discovery.SaveMetadata(entry);
                RecompositeForOverlay(entry, reason);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(tip);
        }

        // Cleared to null rather than false, so an untick restores the "absent = off" default.
        Toggle(cs.BustBridge, "bustbridge", cs.BustBridgeTip, md.BustBridge == true,
               on => { md.BustBridge = on ? true : null; md.BustBridgeStrength = null; }, "bust-bridge");

        Toggle(cs.SmoothNipples, "smoothnipples", cs.SmoothNipplesTip, md.SmoothNipples == true,
               on => { md.SmoothNipples = on ? true : null; md.SmoothNipplesStrength = null; }, "smooth-nipples");

        Toggle(cs.CleftBridge, "cleftbridge", cs.CleftBridgeTip, md.CleftBridge == true,
               on => { md.CleftBridge = on ? true : null; md.CleftBridgeStrength = null; }, "cleft-bridge");

        Toggle(cs.SmoothFold, "smoothfold", cs.SmoothFoldTip, md.SmoothFold == true,
               on => { md.SmoothFold = on ? true : null; md.SmoothFoldStrength = null; }, "smooth-fold");
    }

    /// <summary>
    /// Point Layer/Shader at the mode the features imply (sphere/metal ⇒ Cloth, glow effect ⇒ Animated glow, else Skin) unless pinned
    /// in Advanced; a glow-effect pick outranks and releases the pin. Writes the override when binding. Returns true when the mode changed.
    /// </summary>
    private static bool ReconcileMode(IReadOnlyList<OverlayDescriptor> overlays, GearSettingsPreset? ovr,
        List<ColorTableRowPreset> rows, FeatureEdit edited, bool canShell = true)
    {
        // Only on a mode-relevant edit, or a deliberately plain Gear overlay would be forced to Skin.
        if (edited == FeatureEdit.Neutral) return false;
        if (overlays.Count == 0) return false;
        bool locked = ovr != null ? (ovr.ManualShaderLock ?? false) : overlays.Any(d => d.ManualShaderLock);

        var cur  = RenderModeInference.ModeOf(ovr?.Layer ?? overlays[0].Layer,
                                              ovr != null ? ovr.Shader : overlays[0].Shader);

        // A glow-effect pick outranks the pin and clears it: characterscroll.shpk is the only shader that emits, so a pinned Cloth
        // or Skin over an effect renders no glow at all. Only FeatureEdit.Glow (the effect picker) counts, not a scroll merely
        // being assigned; a pin with no effect, or one already on Glow, still holds.
        if (locked)
        {
            if (edited != FeatureEdit.Glow || !canShell || cur == RenderMode.Glow
                || !RenderModeInference.HasGlow(overlays, ovr))
                return false;
            ColorTableEditor.ApplyMode(overlays, ovr, RenderMode.Glow);
            ColorTableEditor.SetManualShaderLock(overlays, ovr, false);
            return true;
        }

        // Leaving Animated glow: zero the rows' Glow before inferring, or the leftover 150% would infer Cloth.
        if (cur == RenderMode.Glow && !RenderModeInference.HasGlow(overlays, ovr))
            SetRowsEmissive(rows, 0f);

        var want = RenderModeInference.Infer(rows, overlays, ovr, cur, edited);
        // An option we cannot build a layer for renders as skin, but that is the compositor's call each composite: never write the downgrade into the mod.
        if (!canShell) return false;
        if (want == cur) return false;

        ColorTableEditor.ApplyMode(overlays, ovr, want);
        return true;
    }

    /// <summary>The render mode currently represented by an option's descriptors (override first), for
    /// detecting a transition into/out of Animated glow.</summary>
    private static RenderMode EffectiveMode(IReadOnlyList<OverlayDescriptor> overlays, GearSettingsPreset? ovr)
    {
        if (overlays.Count == 0) return RenderMode.Skin;
        var d = overlays[0];
        return RenderModeInference.ModeOf(ovr?.Layer ?? d.Layer, ovr != null ? ovr.Shader : d.Shader);
    }

    /// <summary>Entering Animated glow, default every row's Glow to 150% white; leaving, zero it. No-op on an effect-to-effect swap.</summary>
    private static void ApplyGlowTransition(List<ColorTableRowPreset> rows, RenderMode before, RenderMode after)
    {
        if (before != RenderMode.Glow && after == RenderMode.Glow)
            SetRowsEmissive(rows, RenderModeInference.GlowEmissive, RenderModeInference.GlowEmissiveColour);
        else if (before == RenderMode.Glow && after != RenderMode.Glow) SetRowsEmissive(rows, 0f);
    }

    /// <summary>Set every existing sub-row's Glow to <paramref name="v"/> (1.0 = 100%) and, when given, its glow colour.</summary>
    private static void SetRowsEmissive(List<ColorTableRowPreset> rows, float v, string? emissiveColor = null)
    {
        foreach (var r in rows)
        {
            if (r.SubRowA != null) { r.SubRowA.Emissive = v; if (emissiveColor != null) r.SubRowA.EmissiveColor = emissiveColor; }
            if (r.SubRowB != null) { r.SubRowB.Emissive = v; if (emissiveColor != null) r.SubRowB.EmissiveColor = emissiveColor; }
        }
    }

    /// <summary>The body type whose UV this mod's art is painted in, or null.</summary>
    private static string? OverlayBodyType(OverlayEntry entry)
    {
        IEnumerable<OverlayDescriptor> All()
        {
            foreach (var o in entry.Metadata.Overlays ?? []) yield return o;
            foreach (var g in entry.Metadata.OptionGroups ?? [])
                foreach (var opt in g.Options)
                    foreach (var o in opt.Overlays)
                        yield return o;
        }

        foreach (var d in All())
        {
            if (d.SourceBodyType != null) return d.SourceBodyType;
            foreach (var p in d.MaterialGamePaths)
            {
                var t = UVRemapService.InferBodyType(p);
                if (t != null) return t;
            }
        }
        return null;
    }

    // Body types whose transfer map load has started, so per-frame redraws don't queue it again.
    private readonly HashSet<string> _islandWarmupStarted = new(StringComparer.OrdinalIgnoreCase);
    // Set by the background load, consumed on the UI thread: scans taken without the island mask must be redone.
    private volatile bool _islandMapArrived;

    /// <summary>Load a body type's UV transfer map off the UI thread, then flag the scan cache stale.</summary>
    private void StartIslandMaskWarmup(string bodyType)
    {
        if (!_islandWarmupStarted.Add(bodyType)) return;
        Task.Run(() =>
        {
            try
            {
                uvRemap.IslandMask(bodyType, out _, out _, loadIfMissing: true);
                _islandMapArrived = true;
            }
            catch { /* the scan just keeps counting every pixel */ }

        });
    }

    /// <summary>Restore one option's colour rows and overlay settings from the snapshot recorded before Proteus first wrote to the mod.
    /// A null <paramref name="groupName"/> restores a simple mod's top level. Returns false when nothing was restored.</summary>
    private bool ResetToDefaults(OverlayEntry entry, string? groupName, OverlayOption? option)
    {
        var defaults = discovery.TryLoadDefaults(entry);
        if (defaults == null) return false;

        // Validate before mutating: the binding clear below is persisted and must not run on a path that restores nothing.
        if (groupName == null)
        {
            // Simple mod: the top-level overlays and colour rows ARE the option.
            ReplaceRows(entry.Metadata.ColorTableRows ??= [], defaults.ColorTableRows);
            entry.Metadata.Overlays ??= [];
            ReplaceOverlays(entry.Metadata.Overlays, defaults.Overlays);
        }
        else
        {
            if (option == null) return false;
            var srcOpt = defaults.OptionGroups?
                .FirstOrDefault(g => string.Equals(g.PenumbraGroupName, groupName, StringComparison.OrdinalIgnoreCase))?
                .Options.FirstOrDefault(o => string.Equals(o.Name, option.Name, StringComparison.OrdinalIgnoreCase));
            if (srcOpt == null)
            {
                Plugin.Log.Warning("[Proteus] reset: {0} has no recorded defaults for [{1}/{2}] — nothing restored",
                    entry.ModDirectory, groupName, option.Name);
                return false;
            }

            ReplaceRows(option.ColorTableRows ??= [], srcOpt.ColorTableRows);
            ReplaceOverlays(option.Overlays, srcOpt.Overlays);
        }

        // A design binding would re-impose its own captured values for this option, so clear its override.
        editRouter.ClearOptionOverride(entry.ModDirectory, groupName, option?.Name);

        // Descriptor Index paths may differ from the edited ones, so the cached row scans are stale.
        foreach (var k in _indexRowCache.Keys.Where(k => k.StartsWith(entry.SidecarRoot)).ToList())
            _indexRowCache.Remove(k);

        Plugin.Log.Information("[Proteus] reset {0}{1} to recorded defaults",
            entry.ModDirectory, groupName == null ? "" : $" [{groupName}/{option!.Name}]");
        return true;
    }

    // Mutate the live lists in place: the editor and compositor hold references to them. `live` is non-null by contract.
    private static void ReplaceOverlays(List<OverlayDescriptor> live, List<OverlayDescriptor>? from)
    {
        live.Clear();
        if (from != null) live.AddRange(from);
    }

    private static void ReplaceRows(List<ColorTableRowPreset> live, List<ColorTableRowPreset>? from)
    {
        live.Clear();
        if (from != null) live.AddRange(from);
    }

    // Whether each mod has a defaults snapshot, cached because it is a filesystem stat per draw; invalidated on our own SaveMetadata.
    private readonly Dictionary<string, bool> _hasDefaultsCache = new(StringComparer.OrdinalIgnoreCase);

    private void InvalidateDefaultsCache(OverlayEntry entry) => _hasDefaultsCache.Remove(entry.SidecarRoot);

    /// <summary>
    /// Recomposite because something changed on this overlay mod, only if the mod is enabled: a disabled mod paints nothing.
    /// Same rule as <c>CompositorService.OnModSettingChanged</c>; not used for the enable toggle itself.
    /// </summary>
    internal void RecompositeForOverlay(OverlayEntry entry, string reason, int delayMs = 200,
                                       bool skinFingerprintAuthoritative = false)
    {
        if (!entry.Enabled) return;
        // drawStateStable: every trigger through here is an edit in this window, not the character moving.
        compositor.TriggerRecomposite(reason, delayMs,
            skinFingerprintAuthoritative: skinFingerprintAuthoritative, drawStateStable: true);
    }

    /// <summary>Why "Reset to defaults" can't run right now, or null when it can.</summary>
    private string? ResetBlockedReason(OverlayEntry entry)
    {
        if (!_hasDefaultsCache.TryGetValue(entry.SidecarRoot, out var has))
            _hasDefaultsCache[entry.SidecarRoot] = has = discovery.HasDefaults(entry);

        return has
            ? null
            : "No original settings recorded for this mod yet — Proteus captures them the first time\n" +
              "it saves a change here.";
    }

    /// <summary>
    /// The colour-table cell an overlay's art can reach: rows to leave live and the column, or nulls for unknown.
    /// With no index texture a shell samples the fabricated index, row pair <see cref="GlowShell.Row"/> sub-row A.
    /// </summary>
    /// <param name="hasAnyIndex">Whether any overlay or active mask names an index at all.</param>
    /// <param name="scanned">The union of every scan's rows; empty means the files were read and selected nothing.</param>
    private static (HashSet<int>? Rows, string? Column) OverlayRowFilter(
        bool hasAnyIndex, HashSet<int> scanned, string? column)
        => !hasAnyIndex ? ([GlowShell.Row], ShellDefaultColumn)
         : scanned.Count > 0 ? (scanned, column)
         : (null, null);

    /// <summary>The sub-row a shell with no <c>_id</c> lands in: the fabricated index's green is 255, which
    /// is column A. Spelled the way <see cref="ContentIndexTexture.Scan.SubRow"/> spells it.</summary>
    private const string ShellDefaultColumn = "A";

    /// <summary>The one column every scan agrees on, or null if any scan has none or they disagree.</summary>
    private static string? AgreedColumn(IEnumerable<string?> columns)
    {
        string? one = null;
        foreach (var c in columns)
        {
            if (c == null) return null;
            if (one == null) one = c;
            else if (!string.Equals(one, c, StringComparison.Ordinal)) return null;
        }
        return one;
    }

    /// <summary>Which colour-table rows and column an overlay's own <c>_id</c> art selects; the rules live in <see cref="ContentIndexTexture.ReadOverlay"/>.</summary>
    private ContentIndexTexture.Scan ScanIndexFile(string absolutePath, string? bodyType)
    {
        try
        {
            using var stream = File.OpenRead(absolutePath);
            var img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

            // Mask to UV islands so bleed isn't counted, using the map only if already loaded; otherwise load it in the background and count every pixel for now.
            int islandW = 0, islandH = 0;
            bool[]? island = bodyType != null ? uvRemap.IslandMask(bodyType, out islandW, out islandH, loadIfMissing: false) : null;
            if (islandW == 0 || islandH == 0) island = null;
            if (island == null && bodyType != null) StartIslandMaskWarmup(bodyType);

            return ContentIndexTexture.ReadOverlay(img.Data, img.Width, img.Height, island, islandW, islandH);
        }
        catch
        {
            // Unreadable is unknown: an empty scan becomes a null filter.
            return new([], null);
        }
    }
}
