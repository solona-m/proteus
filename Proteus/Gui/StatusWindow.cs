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
    /// <summary>How lit the wearer is, for the readout in Settings. Set once at startup; null just hides
    /// the readout — the light response itself runs without anyone watching it.</summary>
    public static Proteus.Interop.SceneLightService? SceneLight { get; set; }

    private readonly CompositorService compositor;
    private readonly SidecarDiscoveryService discovery;
    private readonly PenumbraBridge penumbra;
    private readonly Configuration config;
    private readonly DesignBindingService designBindings;
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
    // Turns a mod's own geometry into on/off switches. Its own class: it works on any installed mod, not
    // just the sidecar ones this window otherwise lists.
    private readonly PartsPanel parts;

    // Accent used to flag an active design binding (and the mods/colors it drives).
    private static Vector4 BindingAccent => ProteusStyle.Binding;

    /// <summary>Amber for "this worked, but read it" — the Import tab's pack warnings and its result line.</summary>
    // Properties, not fields: ProteusStyle.Warn forwards to ImGuiColors, which the active Dalamud style
    // rewrites — caching it in a static readonly field would freeze it at whatever the style was on load.
    private static Vector4 ImportWarnColour => ProteusStyle.Warn;

    // Indexed by (int)SiblingSynthesisMode: Off=0, BiboGen3Only=1, AllBodies=2.
    // A METHOD, not the static readonly array this used to be: a static field captures its value once at
    // type-init, so the labels would have frozen in whatever language was active when the window first
    // drew and never followed a language change.
    private static string SiblingModeLabel(int mode) => mode switch
    {
        0 => Strings.ColorPanel.BodiesOff,
        1 => Strings.ColorPanel.BodiesSibling,
        _ => Strings.ColorPanel.BodiesAll,
    };

    // Set by the plugin-installer gear icon (UiBuilder.OpenConfigUi) so the window opens on Settings.
    // One-shot: consumed by the next Draw so the user can move off the tab freely afterwards.
    private bool _forceSettingsTab;

    // ── the Toggles tab's resizable mode ────────────────────────────────────────────────────────────────
    // Which tab drew last frame. It cannot be anything but last frame's: a tab's selection is only known
    // once BeginTabItem has run, which is inside Draw, which is after Begin has already read Flags. So the
    // mode is applied one frame late — a single frame of auto-fit on the way in, which reads as a window
    // opening at a remembered size rather than as a glitch.
    private bool _togglesTabActive;
    // What PreDraw actually did last frame, which is what tells an ENTRY apart from a frame already in the
    // mode. Driven off this rather than off a tab click because ImGui remembers the selected tab in its ini:
    // the very first frame after a game restart is an entry too, with no click anywhere.
    private bool _resizableActive;
    private bool _restoreSize;
    private bool _restoreAutoFit;
    // Live window size in the resizable mode, UNSCALED — the host multiplies Size by the global scale, so a
    // scaled value stored here would compound the scale on every restore.
    private Vector2 _togglesSize;
    private bool _sizeDirty;
    private long _sizeChangedAt;
    // The size the AUTO-FITTING tabs last settled at, unscaled — handed back on the way out of the Toggles
    // tab. Null until an auto-fitting tab has drawn once, which a config opening straight onto Toggles can
    // put off indefinitely.
    private Vector2? _autoFitSize;
    // Height DrawLastResult took last frame, so the Toggles tab knows how much to leave under itself.
    private float _footerReserve;

    // Key: absolute index-texture path → 1-based row numbers that appear in it.
    // Cleared per-entry on each popup open so option switches are reflected.
    private readonly Dictionary<string, ContentIndexTexture.Scan> _indexRowCache = new();
    // Key: modDir → selected index into the active-options list (for the dropdown).
    // Which active overlay the colour editor is scoped to, keyed by mod dir → "group\0option". Tracked by
    // identity (not slot index) so the selection follows the option when the stack is reordered.
    private readonly Dictionary<string, string> _colorEditorSel = new();
    // Identity of the overlay tab currently being dragged to restack (payload carries only a marker).
    private (string Mod, string Group, string Option)? _stackDragSrc;

    /// <summary>
    /// The drag payload ImGui requires but we never read — the source identity rides in
    /// <see cref="_stackDragSrc"/> instead. A static field rather than a stackalloc at the call site:
    /// that site sits inside the per-tab loop, where a stackalloc accumulates a frame per iteration
    /// that only unwinds when the whole draw method returns.
    /// </summary>
    private static readonly byte[] StackDragMarker = new byte[1];

    /// <summary>Penumbra group → ordinal per mod, memoised: the tab strip needs it every frame to show
    /// the true stacking order, and reading it walks the mod folder.</summary>
    private readonly Dictionary<string, Dictionary<string, int>> _groupOrderCache = new(StringComparer.OrdinalIgnoreCase);
    // mod|material -> the colour rows that material's index texture actually selects. Cleared with the rest
    // when the colour window reopens, so a pack whose index option changed is rescanned.
    private readonly Dictionary<string, ContentIndex> _contentIndexCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mods for which we've already fired the cold-boot glow-recipe warmup, so it runs at most
    /// once per mod per session. See the trigger in the colour-editor draw.</summary>
    private readonly HashSet<string> _glowWarmedMods = new(StringComparer.OrdinalIgnoreCase);
    // Key: editor scope → which color table row (1–16) is open in the editor.
    private readonly Dictionary<string, int> _rowSelection = new();
    // Colour edits arrive one per frame while a slider/swatch is dragged; a recomposite is multi-second,
    // so wait this long after the LAST change before recompositing (TriggerRecomposite restarts the
    // timer on each call). The on-screen editor swatches update live regardless — only the bake waits.
    private const int ColorEditDebounceMs = 1000;
    // Mod whose colour editor window is open, or null. A window rather than a popup: colour work means
    // clicking back and forth with the game, and a popup closes on any click outside it.
    private string? _colorWindowMod;
    // Key: modDir → priority value being dragged; committed to Penumbra on edit-end.
    private readonly Dictionary<string, int> _priorityEdits = new();

    // Mods-tab column sort (display only; the compositor orders by priority independently).
    private enum ModSort { Enabled, Name, Priority }
    private ModSort _modSort = ModSort.Priority;
    private bool _modSortDesc = true;   // Priority descending = default

    // Bindings-tab column sort (display only). Defaults to newest-first, which is the order
    // DesignBindingService.Bindings already hands back — so the tab looks unchanged until sorted.
    private enum BindingSort { Design, Captured }
    private BindingSort _bindingSort = BindingSort.Captured;
    private bool _bindingSortDesc = true;

    // ── Create tab state ──
    private readonly FileDialogManager _fileDialog = new();
    private string _createName = "";
    private string _createAuthor = "";
    private string _createMaterial = "";
    private string _createDiffuse = "";   // "" = no file picked
    private string _createMask = "";
    private string _createNormal = "";
    private string _createIndex = "";
    private bool _createWholeSkin;        // the textures ARE the skin → the normal replaces, never compounds
    private bool _createWholeSkinLocked;  // user ticked it by hand — stop auto-detecting over their answer
    private string _createWholeSkinProbedFor = "";   // the diffuse+normal pair the last probe was for
    private Task<bool>? _createWholeSkinProbe;       // decodes two images, so never on the frame thread
    private bool _createMaterialLocked;   // stop auto-detecting once we have a real body (or the user edits)
    private string _createMaterialAuto = "";  // the value we last auto-filled, to tell a user edit apart
    private long _createDetectNextTick;   // throttle the detect poll while the character isn't drawn yet
    private string? _createStatus;        // last create result message
    private bool _createStatusOk;
    // The picked face texture is a doubled sheet (two sides of the head in the two halves). Author-declared,
    // never probed — see the checkbox for why nothing in the image can tell the two layouts apart.
    private bool _createFaceSplit;
    // Whether the art glows and how. Never probed either: a transparent-background PNG is what an ordinary
    // tattoo looks like too, so nothing in the image says the author wanted it lit.
    private GlowStyle _createGlow = GlowStyle.None;

    // ── Import tab state ──
    // The parsed pack, held across frames from Browse until Import. Null = nothing picked yet.
    private OnionImportService.ImportPreview? _importPreview;
    // The material paths the pack's layouts will target, resolved ONCE per pick alongside the preview.
    // Resolving it walks 72 game-data probes on the first call and rebuilds a list per layout on every
    // one, so it can't sit in the draw — same rule as the Create tab's material picker above.
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _importMaterials;
    // Whether that list came from the game data or from the hardcoded female-only fallback. A fallback
    // list looks perfectly legitimate in the UI while naming no male body at all, so it has to be said.
    private bool _importMaterialsFromGameData;
    private string _importPath = "";      // what the dialog returned, kept so a parse failure can name it
    private string _importName = "";      // editable mod name, pre-filled from the pack
    private string _importAuthor = "";
    private bool _importAsTex;            // convert layers to BC7 .tex instead of keeping the pack's PNGs
    private string? _importStatus;
    private bool _importStatusOk;
    // The import worked but the user still has to act (e.g. pick a body layout Penumbra refused to select
    // for them). Amber, not green — a message ending "or nothing will paint" gets skimmed past in green.
    private bool _importStatusWarn;
    private bool _importBusy;             // an import is running on the pool — the button is inert meanwhile
    // Written by the pool task that does the disk work, read (and cleared) by PumpImport on the framework
    // thread. A plain reference assignment is the whole handoff; nothing else touches it.
    private volatile OnionImportService.PreparedImport? _importPrepared;

    // ── Content (.pmp) import state ──
    // The same three-phase handoff as the Onion import above, kept in its own fields rather than shared:
    // the two packs describe different things (layers vs geometry) and a half-switched tab that still held
    // the other kind's preview would offer to import it under the wrong rules.
    private ContentImportService.ImportPreview? _contentPreview;
    private volatile ContentImportService.PreparedImport? _contentPrepared;

    // ── Atramentum Luminis (.ttmp2) import state ──
    // A third set of fields for the same three-phase handoff, and kept apart from the other two for the
    // reason given above: the packs describe different things and must not be imported under each other's
    // rules.
    private LuminisImportService.ImportPreview? _luminisPreview;
    private volatile LuminisImportService.PreparedImport? _luminisPrepared;
    // The body material suffix the overlays will target. Seeded from the preview on the frame the pack is
    // picked, then owned by the user's combo — so re-seeding it per frame would fight their choice.
    private string _luminisSuffix = "";
    // Resolved ONCE per pick alongside the preview, like _importMaterials: the first call walks 72 game-data
    // probes, which is not a per-frame cost.
    private IReadOnlyList<string>? _luminisMaterials;
    private bool _luminisMaterialsFromGameData;
    // A registration Penumbra has accepted but not finished loading. Pumped every frame until it answers;
    // see TickLuminisImport for why this one import can span frames when the other two cannot.
    private bool _luminisAwaiting;
    private LuminisImportService.PreparedImport? _luminisAwaited;

    // ── Eye pack (.zip) import state ──
    // A fourth set, kept apart from the other three for the reason given above.
    private EyeImportService.ImportPreview? _eyePreview;
    private volatile EyeImportService.PreparedImport? _eyePrepared;
    // A registration Penumbra has accepted but not finished loading; see TickEyeImport.
    private bool _eyeAwaiting;
    private EyeImportService.PreparedImport? _eyeAwaited;

    // ── Emissive skin (.pmp) import state ──
    // A fifth set. This one shares an EXTENSION with the content import rather than merely a tab — both
    // read .pmp — so which reader a picked file gets is decided in LoadPenumbraPack, and holding the two
    // previews apart is what stops that decision from being quietly overwritten by a stale panel.
    private EmissiveSkinImportService.ImportPreview? _emissivePreview;
    private volatile EmissiveSkinImportService.PreparedImport? _emissivePrepared;
    // The body material suffix the overlays will target, owned by the user's combo once seeded — see
    // _luminisSuffix, which this mirrors.
    private string _emissiveSuffix = "";
    private IReadOnlyList<string>? _emissiveMaterials;
    private bool _emissiveMaterialsFromGameData;
    // A registration Penumbra has accepted but not finished loading; see TickEmissiveImport.
    private bool _emissiveAwaiting;
    private EmissiveSkinImportService.PreparedImport? _emissiveAwaited;
    // A pick whose textures are being read on the pool. The ONLY loader that does not answer on the frame
    // it was called: an 8192² mask is a measured second of decoding, which is a freeze rather than a hitch.
    private bool _emissiveLoading;
    // Which pick the running read belongs to, bumped by LoadPack for every pack of any kind. A result whose
    // token is stale is a read for a file the user has already replaced, and is dropped rather than drawn
    // over whatever they picked instead. Framework thread only.
    private int _emissivePickToken;
    private volatile EmissiveInspected? _emissiveInspected;

    /// <summary>What the pool read of a picked emissive pack came back with — the preview and the material
    /// list it resolved, or the message to show instead. <c>Token</c> is the pick it belongs to.</summary>
    private sealed record EmissiveInspected(
        int Token,
        EmissiveSkinImportService.ImportPreview? Preview,
        IReadOnlyList<string>? Materials,
        bool MaterialsFromGameData,
        string? Error);

    // ── Export tab state ──
    // Which mod is selected, by DIRECTORY rather than list index: the mod list is rebuilt by discovery and
    // can reorder, and an index would then quietly point at a different mod than the one on screen.
    private string _exportModDir = "";
    // Live only while the mod combo's popup is open — cleared each time it opens, so a filter left behind
    // from last time can never present an empty list as "you have no mods".
    private string _exportFilter = "";
    private string? _exportStatus;
    private bool _exportStatusOk;

    /// <summary>
    /// How far along an export is. Three states, not two: the seconds the FILE BROWSER is open are the
    /// window a second click actually lands in, so "busy" has to start there and not at the write. A flag
    /// raised only once a path was chosen leaves the button live throughout the dialog, and a double-click
    /// then stacks two dialogs and can run two zips onto the same target.
    /// <para/>
    /// Only ever written from the framework thread — here and in the dialog callback, which
    /// <c>FileDialogManager.Draw</c> invokes inline. The pool task writes <see cref="_exportDone"/> instead.
    /// </summary>
    private enum ExportPhase { Idle, Choosing, Writing }
    private ExportPhase _exportPhase = ExportPhase.Idle;
    // Written by that pool task, read and cleared by DrawExportTab. Unlike an import there is no Penumbra
    // IPC afterwards, so nothing has to be pumped from Plugin.DrawUi — the tab itself can finish it, and
    // leaving the result parked until the user looks costs nothing.
    private volatile ModExportService.ExportResult? _exportDone;

    // ── Material-target picker ──
    // Built ONCE on the frame the picker opens. BeginCombo returns true on EVERY frame it stays open, so
    // querying there unguarded would run a multi-ms Penumbra resource walk at frame rate. Null = not built.
    private List<(string Path, string Label, bool Skin)>? _matPickerItems;
    private bool _matPickerWasOpen;   // last frame's open state — gives the rising edge for the rebuild
    private bool _matPickerStale;     // list came from the cached snapshot, not a live query

    // ── Texture slots the picked material declares ──
    // null = not read (or unreadable) ⇒ FAIL OPEN, offer every row. A hand-typed path for a body the
    // player doesn't have installed can't resolve, and that must not lock the author out of Create.
    private MtrlTexturePaths? _createSlots;
    private string _createSlotsFor = "";    // the material _createSlots was resolved for
    // Throttle, not a debounce: armed on the first change and not re-armed while typing continues, so it
    // fires DURING typing rather than after it stops. That's the intent — it caps the .mtrl re-reads at
    // roughly four a second instead of one per keystroke. Programmatic changes bypass it entirely.
    private long _createSlotsNextTick;

    public StatusWindow(
        CompositorService compositor,
        SidecarDiscoveryService discovery,
        PenumbraBridge penumbra,
        Configuration config,
        DesignBindingService designBindings,
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
        PartsPanel parts)
        // "###ProteusStatus" is the stable window id (position/state persist); the text before it is the
        // visible title. Show the assembly version (yyMM.gitCommitCount, e.g. v2607.185.0.0 — computed in
        // Directory.Build.props), not the dev BuildNumber, so it matches the published plugin version.
        : base($"Proteus  v{typeof(Plugin).Assembly.GetName().Version}###ProteusStatus", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.compositor     = compositor;
        this.discovery      = discovery;
        this.penumbra       = penumbra;
        this.config         = config;
        this.designBindings = designBindings;
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

        SizeConstraints = AutoFitConstraints;

        // Free native chrome: the same two destinations as the band's button and the installer's gear,
        // reachable without the window having to give up any content space.
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

    /// <summary>
    /// What the window is on every tab but Toggles: it fits itself to its content and has no resize grip.
    /// </summary>
    /// <remarks>
    /// Unscaled on purpose — Dalamud's window host multiplies these by the global UI scale itself.
    /// </remarks>
    private static readonly WindowSizeConstraints AutoFitConstraints = new()
    {
        // Wide enough for the mod table, so switching to the sparser Bindings/Settings tabs
        // doesn't shrink the window (it's AlwaysAutoResize).
        MinimumSize = new Vector2(520, 80),
        // 774, not the old 700: the header band adds a fixed ~60px, and at 700 the tallest tab
        // (Settings) would start clipping into a scrollbar this window has never had. Raised by the
        // 14px the band itself gained when its caption became the capability row, so the margin
        // Settings was given at 760 survives that change rather than being quietly spent.
        MaximumSize = new Vector2(1100, 774),
    };

    /// <summary>
    /// What the window is on the Toggles tab, which is the one tab worth resizing: it is a model you click
    /// parts on beside a list of them, and both are better bigger.
    /// </summary>
    /// <remarks>
    /// 560 wide rather than the 520 above because this tab spends 55% of its width on the model — at 520 the
    /// list beside it is left with about 200px, which is not a list anyone can read. It stays ≥ 520 so
    /// <see cref="BrandHeader"/>'s width floor, which is fed from <see cref="AutoFitConstraints"/>'s minimum,
    /// means the same thing in both modes. 560 tall is the tab summed at its own minimum row height, so the
    /// vertical scrollbar is a genuine edge case rather than the normal state.
    /// <para/>
    /// A finite maximum rather than <c>float.MaxValue</c>: the host multiplies it by the global scale, and
    /// an infinity is harder to reason about later than a number no monitor reaches.
    /// </remarks>
    private static readonly WindowSizeConstraints ResizableConstraints = new()
    {
        MinimumSize = new Vector2(560, 560),
        MaximumSize = new Vector2(4000, 3000),
    };

    /// <summary>
    /// Pick the window's mode for this frame: auto-fitting everywhere, resizable on the Toggles tab.
    /// </summary>
    /// <remarks>
    /// This is the hook that can do it. Dalamud's window host runs <c>PreDraw</c>, then applies
    /// <see cref="Window.Size"/> / <see cref="Window.SizeConstraints"/>, then reads <see cref="Window.Flags"/>
    /// and calls <c>Begin</c> — so all three land in the same frame from here.
    /// <para/>
    /// <see cref="Window.Flags"/> is assigned in BOTH branches every frame, not cleared once on the way in:
    /// it is a plain property, and clearing the bit only on the transition would mean auto-fit never came
    /// back. <see cref="Window.Size"/> is released here rather than at the end of <see cref="Draw"/> because
    /// Draw does not run on a collapsed window — releasing it there would leave a window collapsed on the
    /// Toggles tab pinned to <c>ImGuiCond.Always</c> forever, unresizable.
    /// <para/>
    /// One thing this cannot beat: Dalamud's own title-bar "pin" ORs in <c>NoResize</c>, so a pinned window
    /// has no grip here either. That is the pin working, not this failing.
    /// <para/>
    /// LEAVING the tab needs an explicit size for a reason that is not symmetric with entering it. An
    /// auto-fitting window measures the content it drew, and roughly twenty <c>PushTextWrapPos(0)</c> calls
    /// across the other tabs wrap at the CONTENT EDGE — so a wrapped paragraph is exactly as wide as the
    /// window already is, and the measured width equals the current width at any width. That is a fixed
    /// point the window can climb but never fall from. It is a different thing from the width floor in
    /// <see cref="AutoFitConstraints"/>, which decides how narrow the window may START; this decides that it
    /// never comes back down. Harmless while nothing could widen the window past its own fit;
    /// now the Toggles tab can, and every other tab would keep the dragged width (up to MaximumSize) for the
    /// rest of the session. Handing back the size the auto-fitting tabs last settled at re-measures the
    /// wrapping against that width instead, which lands exactly where they were before.
    /// </remarks>
    public override void PreDraw()
    {
        FlushPendingSize();

        if (_togglesTabActive)
        {
            if (!_resizableActive) _restoreSize = true;
            _resizableActive = true;

            Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;
            SizeConstraints = ResizableConstraints;

            if (_restoreSize)
            {
                _restoreSize = false;
                _togglesSize = ClampToResizable(
                    new Vector2(config.TogglesWindowWidth, config.TogglesWindowHeight));
                Size = _togglesSize;
                SizeCondition = ImGuiCond.Always;
            }
            else
            {
                // Released, so the grip actually moves the edge instead of being overwritten each frame.
                Size = null;
            }
        }
        else
        {
            // Leaving the tab, which needs a size of its own even though what we are going back to fits
            // itself — see the remarks on the ratchet.
            if (_resizableActive) _restoreAutoFit = true;
            _resizableActive = false;

            Flags |= ImGuiWindowFlags.AlwaysAutoResize;
            SizeConstraints = AutoFitConstraints;

            if (_restoreAutoFit)
            {
                _restoreAutoFit = false;
                // Width is the axis that ratchets, so it is the one being put back. The fallback resets it
                // to the floor, which costs one narrow frame and is still better than staying stuck at
                // whatever the user dragged to.
                var fit = _autoFitSize ?? new Vector2(AutoFitConstraints.MinimumSize.X, _togglesSize.Y);

                // The height is pinned to whichever is TALLER, and that is not cosmetic. The remembered fit
                // belongs to the tab we left from, which may be sparser than the one we are landing on — and
                // a pinned frame too short for its content raises a vertical scrollbar, whose width comes off
                // the wrap edge. The paragraphs would then measure a scrollbar narrower, and since the fixed
                // point below never climbs back down, the window would keep those pixels for good and lose
                // another set on the next round trip. Too tall costs one frame that auto-fit immediately
                // corrects; too short is permanent. The constraints clamp this back to MaximumSize anyway.
                Size = new Vector2(fit.X, MathF.Max(fit.Y, _togglesSize.Y));
                SizeCondition = ImGuiCond.Always;
            }
            else
            {
                Size = null;
            }
        }
    }

    private static Vector2 ClampToResizable(Vector2 size)
    {
        var min = ResizableConstraints.MinimumSize;
        var max = ResizableConstraints.MaximumSize;
        // Not just a clamp: a config from before this setting existed, or one written at a different global
        // scale, can hold something the constraints would silently correct anyway — do it where it is visible.
        return new Vector2(
            float.IsFinite(size.X) ? Math.Clamp(size.X, min.X, max.X) : min.X,
            float.IsFinite(size.Y) ? Math.Clamp(size.Y, min.Y, max.Y) : min.Y);
    }

    /// <summary>
    /// Write the dragged size to the config, once the drag is over.
    /// <para/>
    /// Debounced, and deliberately not through <c>DebounceGate</c>. <see cref="Configuration.Save"/> is a
    /// synchronous serialize of the WHOLE config — <c>KnownBodyMods</c> alone is an entry per installed mod —
    /// so calling it per frame of a drag would stutter the game, and calling it off a worker thread would
    /// serialize those dictionaries while a composite is still mutating them. One write per drag, on the UI
    /// thread. Public because the plugin's own teardown calls it: a resize in the last moments before an
    /// unload is still a resize the user made.
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

    public override void OnClose() => FlushPendingSize();

    /// <summary>Open the window with the Settings tab selected (the plugin-installer gear icon).</summary>
    public void OpenToSettings()
    {
        _forceSettingsTab = true;
        // Force-expand: "take me to Settings" is useless if the window comes back collapsed, and
        // _forceSettingsTab would sit unconsumed anyway — Draw() never runs while collapsed.
        Show(forceExpand: true);
    }

    /// <summary>
    /// Make the window actually visible, not just "open". A collapsed window never reaches
    /// <see cref="Draw"/>, and an already-open one sits behind whatever the user clicked from (the plugin
    /// installer), so setting <see cref="Window.IsOpen"/> alone reads as "the button did nothing".
    /// </summary>
    /// <param name="forceExpand">
    /// Un-collapse the window even when reopening it from closed. For requests whose whole point is
    /// something the user must be able to READ — the Settings tab, an import result. A plain open
    /// leaves a deliberate collapse alone.
    /// </param>
    public void Show(bool forceExpand = false)
    {
        var wasOpen = IsOpen;
        IsOpen = true;

        // Only force the collapse state when the request would otherwise be invisible: an explicit
        // "show me X", or a window that was ALREADY open and so may be sitting collapsed behind the
        // plugin installer. Reopening from closed leaves Collapsed null, and THAT is what lets ImGui
        // restore the state it remembers for ###ProteusStatus — the point of the stable window id.
        if (forceExpand || wasOpen)
        {
            // One-shot un-collapse: Collapsed is re-applied every frame while it has a value, so leaving
            // it set would make the title bar permanently un-collapsible. Draw() clears it once landed.
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
        // Reaching Draw means the window is uncollapsed, so Show()'s forced state has done its job —
        // release it or the user could never collapse the window again.
        Collapsed = null;

        // Which tab is selected is answered below, by the tab that draws. Clearing it first means a frame in
        // which no tab draws at all — an empty tab bar, a tab removed by a future edit — falls back to
        // auto-fit rather than leaving the window stuck resizable with nothing in it.
        _togglesTabActive = false;

        // The dragged size, read back where ImGui has already applied this frame's grip movement. Stored
        // unscaled, since PreDraw hands it back to a host that scales it. The threshold is only there to
        // keep sub-pixel jitter from marking the config dirty forever.
        if (_resizableActive)
        {
            var live = ImGui.GetWindowSize() / ImGuiHelpers.GlobalScale;
            if (MathF.Abs(live.X - _togglesSize.X) > 0.5f || MathF.Abs(live.Y - _togglesSize.Y) > 0.5f)
            {
                _togglesSize   = live;
                _sizeDirty     = true;
                _sizeChangedAt = Environment.TickCount64;
            }
        }
        else
        {
            // What the auto-fitting tabs settled at, so leaving the Toggles tab can put it back. Read every
            // frame rather than once on the way in: the fit legitimately moves as content changes, and the
            // value wanted is the last one, not the one from whenever the user first opened Toggles.
            _autoFitSize = ImGui.GetWindowSize() / ImGuiHelpers.GlobalScale;
        }

        // A deferred UV transfer map finished loading, so index scans taken without the island mask counted
        // padding as coverage — drop them and let this frame recompute the rows accurately.
        if (_islandMapArrived)
        {
            _islandMapArrived = false;
            _indexRowCache.Clear();
        }

        // At game boot no composite has run yet (Penumbra isn't up when the plugin loads), so the mod list
        // would be empty until the user pressed Refresh. Fill it from a cheap discovery-only probe instead
        // — no compositing, no redraw. No-ops once populated.
        compositor.EnsureDiscovered();

        // Identity + status, outside the tabs so it is visible from any of them (the reason the old status
        // banner sat here too). The band paints itself and reports its rect; content is laid into that rect
        // afterwards, and only its HEIGHT is reserved in the layout.
        // AutoFitConstraints, not the live SizeConstraints: the floor is what the band lays itself out
        // against, and it must not move when the Toggles tab swaps in a wider minimum.
        var band = BrandHeader.Draw(minWindowWidth: AutoFitConstraints.MinimumSize.X * ImGuiHelpers.GlobalScale);
        DrawBandContent(band.Min, band.Max);
        BrandHeader.Reserve(band.Min);
        ImGui.Spacing();

        // Labels in the game's Jupiter face and the selection in the brand orange, matching
        // ProteusStyle.SectionHeader — the tab strip was the last stock-ImGui element in the window. The
        // font lives inside the Header* helpers so it covers only the Begin calls; the colour scope can
        // safely wrap everything, since ImGuiCol.Tab* is never read while tab content draws.
        //
        // barTop is captured BEFORE the bar so the refresh button can be centred on its line. The bar's
        // height is FontSize + FramePadding*2 measured in JUPITER (HeaderTabBar pushes it), which is not
        // what ImGui.GetFrameHeight() reports — that answers for the default font.
        var barTop = ImGui.GetCursorPosY();
        // ── the bar's id carries a generation suffix, and it is load-bearing ──
        // An ImGui tab bar remembers each tab's SLOT by id and does not reorder on resubmission (the same
        // fact the colour panel's stack strip works around by not using a TabBar at all). That state lives
        // in Dalamud's ImGui context, not in the plugin, so it survives a plugin reload and outlives any
        // rebuild — moving a tab in this file changes nothing until the game itself restarts.
        //
        // So when the ORDER here changes, bump the suffix. A bar id ImGui has never seen has no remembered
        // slots and lays its tabs out in submission order, which is the order written below. The cost is
        // that the selected tab resets to the first one, once, for anyone who had the window open.
        using (ProteusStyle.TabAccent())
        using (var tabs = ProteusStyle.HeaderTabBar("##proteusTabs2"))
        {
            if (tabs)
            {
                DrawTabBarRefresh(barTop);

                using (var t = ProteusStyle.HeaderTabItem(Strings.Tab.Mods, "mods"))
                    if (t) DrawModsTab();

                using (var t = ProteusStyle.HeaderTabItem(Strings.Tab.Bindings, "bindings"))
                    if (t) DrawBindingsTab();

                using (var t = ProteusStyle.HeaderTabItem(Strings.Tab.Create, "create"))
                    if (t) DrawCreateTab();

                using (var t = ProteusStyle.HeaderTabItem(Strings.Tab.Import, "import"))
                    if (t) DrawImportTab();

                using (var t = ProteusStyle.HeaderTabItem(Strings.Tab.Export, "export"))
                    if (t) DrawExportTab();

                using (var t = ProteusStyle.HeaderTabItem(Strings.Tab.Parts, "toggles"))
                    if (t)
                    {
                        // The one tab that asks for a resizable window; PreDraw grants it next frame.
                        _togglesTabActive = true;
                        parts.Draw(fillHeight: _resizableActive, reserveBelow: _footerReserve);
                    }

                using (var t = ProteusStyle.HeaderTabItem(Strings.Tab.Settings, "settings",
                           _forceSettingsTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
                {
                    _forceSettingsTab = false;
                    if (t) DrawSettingsTab();
                }
            }
        }

        // Measured, not guessed, so the Toggles tab knows how much of the window is still spoken for below
        // it. It over-reads by one ItemSpacing.Y, which is the safe direction: the tab leaves a few pixels
        // spare rather than pushing the footer into a scrollbar.
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
        // No colour editor open ⇒ no stranded glow. (You keep the editor open, aside, to watch the glow;
        // closing it stops it.)
        if (_colorWindowMod == null) { ColorTableEditor.Highlighter?.Clear(); return; }

        var entry = compositor.LastDiscovered.FirstOrDefault(e => e.ModDirectory == _colorWindowMod);
        if (entry == null) { _colorWindowMod = null; ColorTableEditor.Highlighter?.Clear(); return; }

        bool open = true;
        // Scaled by hand, unlike Window.SizeConstraints: this is a bare ImGui.Begin, so Dalamud's window
        // host never sees it and never applies the user's UI scale. Unscaled, a 1.5x user got a window
        // sized for 1.0x content and the row picker wrapped immediately.
        // Wide enough for the 16 row buttons to sit 8-across on two lines.
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
    /// The recomposite control, right-aligned onto the tab bar's OWN line instead of costing a row of its
    /// own inside a tab. Now that it rides the bar it is reachable from every tab, which suits it — a
    /// recomposite is a whole-plugin action, not a Mods-tab one.
    /// </summary>
    /// <remarks>
    /// Call immediately after <c>BeginTabBar</c> succeeds and before the first tab item. At that moment
    /// ImGui has reserved the bar and parked the cursor just below it, so this hops back up onto the bar,
    /// draws, and restores the cursor exactly — tab content then starts where ImGui intended. Drawing
    /// between BeginTabBar and the first BeginTabItem is legal; the bar's deferred layout runs inside that
    /// first item and does not read our cursor.
    /// <para/>
    /// Right-aligned against <c>GetContentRegionMax</c> rather than a measured window width. On the
    /// auto-fitting tabs that is what avoids a feedback loop of the kind BrandHeader documents: an item that
    /// ends exactly at the current content edge keeps the required width where it already was. On the
    /// Toggles tab, where the window is resizable, the same call is simply the true content edge (already
    /// net of any scrollbar), so the button is right-aligned in the ordinary sense. It answers both.
    /// </remarks>
    /// <param name="barTop">Cursor Y from before <c>BeginTabBar</c> — see the call site for why
    /// <c>GetFrameHeight</c> cannot stand in for the bar's real height.</param>
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
            compositor.TriggerRecomposite("manual");
            // The Parts tab lists every mod Penumbra knows, not the sidecar ones a composite discovers, so
            // a recomposite alone would leave a mod installed since the window opened out of its picker.
            parts.Refresh();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.Band.RecompositeTip);

        ImGui.SetCursorPos(resume);
    }

    /// <summary>
    /// The wordmark, the capability row (<see cref="CapabilityStrip"/>), the live status pills, and the
    /// Discord link, laid into the band's rect.
    /// <para/>
    /// This absorbs what used to be two separate pieces: a status banner that rendered NOTHING in the
    /// normal case (so the window opened onto a bare tab bar with no identity), and a right-aligned
    /// Discord button that had to rewind the cursor so the tab bar could share its row. Both now live in
    /// the band, which costs no extra vertical space. The tab bar's line is shared only with the refresh
    /// control (<see cref="DrawTabBarRefresh"/>), which rewinds the cursor the same way the Discord button
    /// used to — worth knowing before adding a third thing to that row.
    /// </summary>
    private void DrawBandContent(Vector2 min, Vector2 max)
    {
        var padX = ProteusStyle.S(10f);
        var padY = ProteusStyle.S(5f);

        // The Discord button's left edge is resolved FIRST, because everything else in the band is laid
        // out against it. Both the capability row and the download status can outgrow the space available —
        // the row because Dalamud's font size is configurable independently of UI scale (and because a
        // translation runs longer than the English), the status because the failure path reports an
        // arbitrary error string — and either one would otherwise run underneath the right-aligned button,
        // which paints over it. Measured outside the wordmark's font scope so it matches the font the
        // button is actually drawn in.
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
        // What used to be a one-phrase caption ("overlay compositor"), which was accurate at launch and
        // now covers about a quarter of the plugin. Deliberately still NOT carrying the version: the title
        // bar renders the same assembly version a few pixels above (see the base() call), and saying it
        // twice in one corner of the window is noise.
        //
        // rowY comes from the wordmark's MEASURED bottom rather than a constant, because Dalamud's font
        // size is configurable independently of UI scale — the same hazard the ellipsis budgets below guard
        // against, and a hardcoded offset would let a large body font collide with the wordmark.
        var rowX = min.X + padX;
        var rowY = ImGui.GetItemRectMax().Y + ProteusStyle.S(2f);
        var strip = CapabilityStrip.Draw(new Vector2(rowX, rowY), btnX - rowX - padX);

        // Icons alone are not self-explanatory, so the narrow layout owes the user words: the hovered
        // item's label, or the fallback caption when nothing is hovered. Ellipsized and length-guarded for
        // the same reasons the pills below are — Ellipsize returns "" when not even the ellipsis fits, and
        // drawing that would leave a stray gap rather than text.
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
        // Only when something needs doing. There used to be a green "active" pill here so the band never
        // read as empty chrome, but a badge lit on every frame of normal use stops being read at all —
        // and it spent horizontal room next to the wordmark to say nothing. The two states that are
        // ACTIONABLE keep their pill, and the band is still the one place visible from every tab.
        var pillX = afterMark + (padX * 1.5f);
        var pillY = min.Y + padY + ProteusStyle.S(3f);
        ImGui.SetCursorScreenPos(new Vector2(pillX, pillY));

        (string Text, Vector4 Colour)? state =
              !config.PluginEnabled ? (Strings.Band.PillDisabled,   ProteusStyle.Warn)
            : !penumbra.IsAvailable ? (Strings.Band.PillNoPenumbra, ProteusStyle.Bad)
            :                         null;
        // Ellipsized like the download pill below, which it never used to be: "disabled" fit next to the
        // Discord button in English, but "deaktiviert" and "отключено" do not, and an unbudgeted pill
        // draws straight under the button rather than being clipped by it.
        bool statePillDrawn = false;
        if (state is { } s)
        {
            // Same empty-result guard as the download pill: Ellipsize returns "" when not even the
            // ellipsis fits, and drawing anyway would leave a bare coloured lozenge saying nothing.
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

            // Whatever is left between the state pill and the button, less the Retry button the failure
            // path also needs room for. With no state pill drawn the last submitted item is the CAPTION,
            // a line above and the full band wide, so its rect would report almost no room left and
            // ellipsize this pill to nothing. Fall back to the cursor we parked at instead.
            // Keyed off whether the pill was actually DRAWN, not merely wanted: a state that ellipsized to
            // nothing submitted no item, so GetItemRectMax would report the caption a line above and
            // budget this pill against the wrong edge.
            var used   = statePillDrawn ? ImGui.GetItemRectMax().X : pillX;
            // Measured from the TRANSLATED caption — measuring the English literal while drawing the
            // translated button would budget the wrong width in every other language.
            var retryW = failed ? ImGui.CalcTextSize(Strings.Band.Retry).X + (ImGui.GetStyle().FramePadding.X * 2)
                                    + ImGui.GetStyle().ItemSpacing.X
                                : 0f;
            var budget = ProteusStyle.PillTextBudget(btnX - used - ImGui.GetStyle().ItemSpacing.X - retryW - padX);

            var full  = uvMapDl.StatusMessage;
            var shown = ProteusStyle.Ellipsize(full, budget);

            // An empty result means not even an ellipsis fits, so drawing the pill anyway would put a bare
            // rounded box under the button for no information. Drop it — the state pill still shows, and
            // the failure path keeps its Retry button, which is the part that is actionable.
            if (shown.Length > 0)
            {
                // SameLine only follows a pill that actually exists. Without one it would put this on the
                // caption's line; the cursor is still parked where the state pill would have gone, and
                // ProteusStyle.Pill draws at the current cursor, so leaving it alone lands correctly.
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

        // Discord's blurple is Discord's brand, so unlike everything else here it deliberately does not
        // follow the user's theme. Hover/active are derived rather than hand-picked.
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
        // Footer. No Separator above it — the status pill already reads as a distinct band, and stacking a
        // rule under the tab content as well just adds a line to look at.
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

        // The number is formatted here and the unit comes from the template, so a translator has one
        // placeholder to move and no format specifier to break.
        var elapsed = DateTime.UtcNow - result.Timestamp;
        var timeStr = elapsed.TotalSeconds < 60
            ? string.Format(f.SecondsAgoFmt, elapsed.TotalSeconds.ToString("F1"))
            : string.Format(f.MinutesAgoFmt, elapsed.TotalMinutes.ToString("F0"));

        ProteusStyle.Pill(f.PillOk, ProteusStyle.Ok);
        ImGui.SameLine();
        ImGui.TextDisabled(string.Format(f.LastCompositeFmt, timeStr, result.TexturesPatched, result.OverlayModsUsed));
    }

    /// <summary>
    /// Settings, grouped. Every control here is exactly the one that was here before — this is a
    /// reordering, not a rewrite — but they were previously twenty widgets in one undifferentiated column
    /// where the only explanation of any of them was an invisible hover tooltip.
    /// </summary>
    private void DrawSettingsTab()
    {
        var s = Strings.Settings;
        ProteusStyle.SectionHeader(s.SecGeneral);
        using (ProteusStyle.Card())
        {
            var enabled = config.PluginEnabled;
            // The master switch, and the only toggle drawn as a switch: it governs the other nine, and a
            // tenth identical checkbox gave no hint of that.
            // Was a `const string`; Loc.Localize is not a compile-time constant, so it is a local now.
            var enabledHelp = s.EnabledTip;
            if (ImGuiComponents.ToggleButton("##enabled", ref enabled))
            {
                config.PluginEnabled = enabled;
                config.Save();
                compositor.SetEnabled(enabled);   // clears output, redraws, then toggles the Penumbra mod
            }
            // On the switch AND on the label. A checkbox carries its own label, so one hover test covered
            // both; splitting them left the switch — the part the eye goes to — explaining nothing.
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(enabledHelp);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(s.Enabled);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(enabledHelp);

            DrawGeneralToggles();
        }

        ImGui.Spacing();
        ProteusStyle.SectionHeader(s.SecOutput);
        using (ProteusStyle.Card())
            DrawOutputSettings();

        ImGui.Spacing();
        ProteusStyle.SectionHeader(s.SecSkinEffects);
        using (ProteusStyle.Card())
            DrawSkinEffectSliders();

        ImGui.Spacing();
        ProteusStyle.SectionHeader(s.SecLightResponse);
        using (ProteusStyle.Card())
            DrawLightResponseSettings();

        ImGui.Spacing();
        ProteusStyle.SectionHeader(s.SecHosting);
        using (ProteusStyle.Card())
            DrawHostingSettings();

        ImGui.Spacing();
        ProteusStyle.SectionHeader(s.SecDiagnostics);
        using (ProteusStyle.Card())
            DrawDiagnostics();
    }

    private void DrawGeneralToggles()
    {
        var s = Strings.Settings;

        var autoRedraw = config.AutoRedraw;
        if (ImGui.Checkbox(s.AutoRedraw, ref autoRedraw))
        {
            config.AutoRedraw = autoRedraw;
            config.Save();
            // Turning it back ON catches up: while it was off every ambient trigger was dropped, so the
            // published output can be arbitrarily far behind the world (a collection switch, a body-mod
            // change, a zone). Forced, because the config knobs are deliberately outside the composite
            // fingerprint — an ambient trigger here would hit the unchanged-inputs gate and do nothing.
            // Turning it OFF stays silent: the point of it is that we stop touching the character.
            if (autoRedraw) compositor.TriggerRecomposite("auto-redraw-enabled");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.AutoRedrawTip);

        var autoRaise = config.AutoRaiseModPriority;
        if (ImGui.Checkbox(s.AutoRaise, ref autoRaise))
        {
            config.AutoRaiseModPriority = autoRaise;
            config.Save();
        }
        // Both a visible (?) AND the original hover. The marker is for discovery — this describes a
        // failure nobody would think to hover for — but removing the hover took the explanation away from
        // anyone who already had the habit of pointing at the control itself.
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.AutoRaiseTip);
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(s.AutoRaiseTip);

        var inPlaceReload = config.UseInPlaceReload;
        if (ImGui.Checkbox(s.InPlaceReload, ref inPlaceReload))
        {
            config.UseInPlaceReload = inPlaceReload;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.InPlaceReloadTip);

        // The scroll-map library lives in Proteus's own Penumbra mod folder — nothing to configure, so
        // the only thing worth surfacing is a way IN. This used to be a TextDisabled path with a small
        // "Open" tacked on the end; greyed text reads as status rather than a control, and "Open" next
        // to a long absolute path is easy to miss entirely. Name it after what it holds instead, and
        // keep the path in the tooltip — that line was the only place it was shown.
        //
        // Null means Penumbra's mod directory isn't available, so there is genuinely nothing to open.
        // Otherwise EffectsLibraryPath has already created the folder, so this never opens nothing.
        var lib = discovery.EffectsLibraryPath();
        if (lib != null)
        {
            ImGui.Spacing();
            if (ImGui.Button(s.GlowLibrary))
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(lib) { UseShellExecute = true }); }
                catch { /* no file manager — the path is in the tooltip anyway */ }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Format(s.GlowLibraryTipFmt, lib));
        }
    }

    private void DrawOutputSettings()
    {
        var s = Strings.Settings;

        var enableCompression = config.EnableCompression;
        if (ImGui.Checkbox(s.Compression, ref enableCompression))
        {
            config.EnableCompression = enableCompression;
            config.Save();
            // Re-encode existing output in the new format.
            compositor.TriggerRecomposite("compression-toggle");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.CompressionTip);

        var cutoutAlpha = config.GearCutoutAlpha;
        if (ImGui.Checkbox(s.SharpAlpha, ref cutoutAlpha))
        {
            config.GearCutoutAlpha = cutoutAlpha;
            config.Save();
            compositor.TriggerRecomposite("cutout-alpha-toggle");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.SharpAlphaTip);
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(s.SharpAlphaTip);

        DrawCacheAndMeshSettings();
    }

    private void DrawHostingSettings()
    {
        var s = Strings.Settings;

        bool autoGlasses = config.AutoInvisibleGlasses;
        if (ImGui.Checkbox(s.InvisibleGlasses, ref autoGlasses))
        {
            config.AutoInvisibleGlasses = autoGlasses;
            config.Save();
            // Recomposite so the injection/removal reconciles now (turning it off pulls the glasses).
            compositor.TriggerRecomposite("auto-glasses-toggle");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.InvisibleGlassesTip);


        // The second skin rides on an equipped ring/bracelet (its model is redirected to our merged
        // shell). An in-place reload can't reload that .mdl, so if a shell ever gets stuck on the
        // accessory this forces a full redraw to reload the accessory's original model.
        if (ImGui.Button(s.RestoreAccessory))
            compositor.RestoreChangedAccessory();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.RestoreAccessoryTip);
    }

    private void DrawDiagnostics()
    {
        // Escape hatch for a stale texture: Proteus caches decoded textures keyed by file
        // timestamp + size, so a mod edit that preserves both can keep showing the old image
        // until this drops the cache and recomposites. Mod toggles/reinstalls evict automatically.
        var s = Strings.Settings;

        if (ImGui.Button(s.ClearCache))
            compositor.ClearTextureCacheAndRecomposite();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.ClearCacheTip);

        // WHICH skin the overlays are painted onto. Nothing else surfaces this, and a composite built on
        // the wrong body mod looks like a perfectly good composite of a skin you didn't pick — so the
        // failure is invisible unless the source is named somewhere.
        var upstreams = compositor.BaseUpstreams();
        // "###baseSkin" pins the id. The count was previously part of it, so this header silently
        // collapsed itself whenever a texture entered or left the list — and once the label is translated
        // it would also reset on every language change.
        if (upstreams.Count > 0 &&
            ImGui.CollapsingHeader(string.Format(s.BaseSkinHeaderFmt, upstreams.Count) + "###baseSkin"))
        {
            foreach (var (gamePath, source, settled) in upstreams)
            {
                if (settled)
                    ImGui.TextUnformatted($"{source}");
                else
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), string.Format(s.BaseSkinUnconfirmedFmt, source));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(gamePath);
            }
            ImGui.TextDisabled(s.BaseSkinNote);
        }

        // The mirror of "Base skin": that names the file we READ, this says what we DID to it. Both exist
        // because a composite that applied nothing looks exactly like one that applied everything — the log
        // only ever proved a redirect was published, not that a pixel was blended. The red row is the whole
        // point: an overlay declared a diffuse and none of it reached the skin, which renders as the plain
        // base body with the overlay's normal on top.
        //
        // Already filtered and sorted by the composite — nothing here but formatting, because this runs on
        // every frame the window is open.
        var contributions = compositor.ChannelContributions();
        if (contributions.Count > 0 &&
            ImGui.CollapsingHeader(string.Format(s.ReachHeaderFmt, contributions.Count) + "###overlayReach"))
        {
            foreach (var c in contributions)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(c.Material);
                if (c.DiffuseWanted && c.Diffuse == 0)
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f),
                        string.Format(s.ReachFailedFmt, name, c.Diffuse, c.Normal, c.Mask));
                else if (c.Diffuse + c.Normal + c.Mask == 0)
                    // Reached only by ambient occlusion or skin-tint suppression — a gear-layer mod casting a
                    // contact shadow onto skin no overlay paints. Without the label this is three zeros and
                    // no way to tell it from a material nothing reached at all.
                    ImGui.TextDisabled(string.Format(s.ReachEffectsOnlyFmt, name));
                else
                    ImGui.TextUnformatted(string.Format(s.ReachOkFmt, name, c.Diffuse, c.Normal, c.Mask));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(c.Material);
            }
            ImGui.TextDisabled(s.ReachNote);
        }
    }

    private void DrawSkinEffectSliders()
    {
        // Skin-tint suppression strength (global multiplier). The per-pixel amount is weighted by
        // overlay color: bright dyes get de-tinted, dark dyes are left skin-tinted and matte.
        var s = Strings.Settings;

        // Wider than the original 140px: ImGui draws a slider's label to its RIGHT, and the four labels
        // here are among the longest in the window once translated.
        ImGui.SetNextItemWidth(ProteusStyle.S(140f));
        float skinSup = config.SkinColorSuppression;
        if (ImGui.SliderFloat(s.SkinTint, ref skinSup, 0f, 1f, "%.2f"))
            config.SkinColorSuppression = Math.Clamp(skinSup, 0f, 1f);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save();
            compositor.TriggerRecomposite("skin-suppression");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.SkinTintTip);

        // Ambient-occlusion contact shadow baked onto the skin around masked strap edges.
        ImGui.SetNextItemWidth(ProteusStyle.S(140f));
        float aoStr = config.AmbientOcclusionStrength;
        if (ImGui.SliderFloat(s.AmbientOcclusion, ref aoStr, 0f, 2f, "%.2f"))
            config.AmbientOcclusionStrength = Math.Clamp(aoStr, 0f, 2f);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save();
            compositor.TriggerRecomposite("ambient-occlusion");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.AmbientOcclusionTip);

        ImGui.SetNextItemWidth(ProteusStyle.S(140f));
        float aoSoft = config.AmbientOcclusionSoftness;
        if (ImGui.SliderFloat(s.ShadowSoftness, ref aoSoft, 0.001f, 0.005f, "%.3f"))
            config.AmbientOcclusionSoftness = Math.Clamp(aoSoft, 0.001f, 0.005f);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save();
            compositor.TriggerRecomposite("ambient-occlusion-softness");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.ShadowSoftnessTip);

        ImGui.SetNextItemWidth(ProteusStyle.S(140f));
        float aoNrm = config.AmbientOcclusionNormalDepth;
        if (ImGui.SliderFloat(s.Skindenting, ref aoNrm, 0f, 10f, "%.2f"))
            config.AmbientOcclusionNormalDepth = Math.Clamp(aoNrm, 0f, 10f);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save();
            compositor.TriggerRecomposite("ambient-occlusion-normal");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.SkindentingTip);
    }

    /// <summary>
    /// Light-sensitive glow: the master switch, the hand-pinned level, and a readout of what the probe is
    /// currently reading.
    /// <para/>
    /// None of these recomposite, and that is the point of the whole feature — the light reaches the
    /// character through the live colour table, so moving this slider changes what is on screen within a
    /// frame and nothing on disk is rebuilt. The readout is here because "why is my tattoo off in this
    /// room" otherwise has no answer short of a debugger: it names the two terms behind the one number.
    /// </summary>
    private void DrawLightResponseSettings()
    {
        var s = Strings.Settings;

        bool enabled = config.LightResponseEnabled;
        if (ImGui.Checkbox(s.LightResponseEnabled, ref enabled))
        {
            config.LightResponseEnabled = enabled;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.LightResponseEnabledTip);

        using var dim = ImRaii.PushStyle(ImGuiStyleVar.Alpha, enabled ? 1f : 0.5f);

        bool manual = config.LightResponseManual;
        if (ImGui.Checkbox(s.LightResponseManual, ref manual))
        {
            config.LightResponseManual = manual;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.LightResponseManualTip);

        if (manual)
        {
            ImGui.SetNextItemWidth(ProteusStyle.S(140f));
            float level = config.LightResponseManualLevel;
            if (ImGui.SliderFloat(s.LightResponseLevel, ref level, 0f, 1f, "%.2f"))
                config.LightResponseManualLevel = Math.Clamp(level, 0f, 1f);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(s.LightResponseLevelTip);
        }

        // Only while the probe is actually running. Switched off it stops evaluating, and the last numbers it
        // happened to hold would sit here looking current — the same lie the pinned case used to tell.
        if (enabled && SceneLight is { } probe)
        {
            ImGui.TextDisabled(string.Format(s.LightResponseReadoutFmt,
                probe.Level, probe.HasSky ? probe.SkyTerm : 0f, probe.PlacedTerm,
                probe.LightsCounted, probe.LightsSeen));
            // The raw signals behind the sky term. Deciding "is there a sky over me" from the layout is the
            // part of the estimate most likely to be wrong — outdoor data alone said yes inside a building —
            // so the three flags sit where they can be read off the screen rather than out of a log.
            ImGui.TextDisabled(string.Format(s.LightResponseSignalsFmt,
                probe.Outdoor ? "Y" : "n", probe.Indoor ? "Y" : "n",
                probe.InEnvSpace ? "Y" : "n", probe.HasSky ? "Y" : "n"));
        }
    }

    private void DrawCacheAndMeshSettings()
    {
        var s = Strings.Settings;

        ImGui.SetNextItemWidth(ProteusStyle.S(140f));
        int cacheMb = config.DecodeCacheBudgetMb;
        // Logarithmic: the useful range now spans 512 MB to 32 GB, and a linear slider over that puts every
        // value anyone actually wants inside the first few pixels.
        if (ImGui.SliderInt(s.TextureCache, ref cacheMb,
                Configuration.MinDecodeCacheBudgetMb, Configuration.MaxDecodeCacheBudgetMb,
                "%d MB", ImGuiSliderFlags.Logarithmic))
            config.DecodeCacheBudgetMb = Math.Clamp(cacheMb,
                Configuration.MinDecodeCacheBudgetMb, Configuration.MaxDecodeCacheBudgetMb);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save();
            compositor.ApplyDecodeCacheBudget();   // live — lowering it reclaims on the spot, no restart
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.TextureCacheTip);
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(s.TextureCacheTip);

        // Hide a body's redundant connector meshes on the gear shell (see Configuration).
        var connMode = config.HideConnectorMeshes;
        ImGui.SetNextItemWidth(ProteusStyle.S(140f));
        // The enum values (Off / Neolithe) stay untranslated: "Neolithe" is a body mod's name, and "Off"
        // beside it would read oddly translated alone.
        if (ImGui.BeginCombo(s.ConnectorMeshes, connMode.ToString()))
        {
            foreach (var opt in new[] { ConnectorMeshMode.Off, ConnectorMeshMode.Neolithe })
            {
                if (ImGui.Selectable(opt.ToString(), opt == connMode) && opt != connMode)
                {
                    config.HideConnectorMeshes = opt;
                    config.Save();
                    compositor.TriggerRecomposite("connector-meshes");
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.ConnectorMeshesTip);
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(s.ConnectorMeshesTip);
    }

    /// <summary>Author a basic skin-overlay mod: name + author + up to three textures → a new Penumbra mod.</summary>
    private void DrawCreateTab()
    {
        var cs = Strings.Create;

        ImGui.TextWrapped(cs.Intro);
        ImGui.Separator();

        ImGui.InputText(cs.ModName, ref _createName, 128);
        ImGui.InputText(cs.Author, ref _createAuthor, 128);

        // Material target — the exact body material the overlay paints on. Auto-fill from the body the
        // character is drawing. Detection returns null until the character loads, so keep polling (throttled)
        // rather than locking in the fallback default; stop the moment we resolve a real body OR the user
        // edits the box (so their choice is never clobbered).
        if (!_createMaterialLocked)
        {
            if (_createMaterial != _createMaterialAuto)
            {
                _createMaterialLocked = true;   // user typed something — hands off
            }
            else if (Environment.TickCount64 >= _createDetectNextTick)
            {
                _createDetectNextTick = Environment.TickCount64 + 500;
                var detected = modCreation.DetectBodyMaterial();
                if (detected != null)
                {
                    _createMaterial = _createMaterialAuto = detected;
                    _createMaterialLocked = true;
                }
                else if (_createMaterial.Length == 0)
                {
                    // Placeholder only — still NOT locked, so the live detect above wins once the character
                    // draws. The cached body beats the hardcoded default: it's the user's actual body.
                    _createMaterial = _createMaterialAuto =
                        modCreation.CachedBodyMaterial() ?? ModCreationService.DefaultBodyMaterial;
                }
            }
        }
        ImGui.SetNextItemWidth(560);
        ImGui.InputText(cs.MaterialTarget, ref _createMaterial, 256);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.MaterialTargetTip);

        // Equipped-material picker. NoPreview renders just the arrow, so this reads as a companion to the
        // text box above rather than a second, competing field — ImGui has no editable combo.
        ImGui.SameLine();
        // The width constraint is required BECAUSE of NoPreview: the popup would otherwise inherit the
        // arrow's width and clip every label to nothing. The height has to be capped here too — BeginCombo
        // applies its own constraint (and with it the ImGuiComboFlags.Height* row cap) ONLY when the caller
        // supplied none, so passing a width constraint silently disables that path. Without this the popup
        // grows one row per equipped material, past the window on a full wardrobe.
        var pickerMaxH = ImGui.GetTextLineHeightWithSpacing() * 20 + ImGui.GetStyle().WindowPadding.Y * 2;
        // Max widened for translation: the two notices inside are full sentences, and the popup does not
        // wrap or scroll horizontally — it clips.
        ImGui.SetNextWindowSizeConstraints(new Vector2(460, 0), new Vector2(950, pickerMaxH));
        bool pickerOpen = ImGui.BeginCombo("##matpick", "", ImGuiComboFlags.NoPreview);
        // Rising edge only. BeginCombo is true every frame the popup is open, so rebuilding unguarded would
        // run the Penumbra resource walk at frame rate. Assign after the test so the flag also falls on close.
        if (pickerOpen && !_matPickerWasOpen) RebuildMaterialPicker();
        _matPickerWasOpen = pickerOpen;
        if (pickerOpen)
        {
            var items = _matPickerItems;
            // Only when there IS a last-known list to show: the live query also reports "from cache" when
            // the cache is itself empty, and the two notices stacked would promise a list and then deny it.
            if (_matPickerStale && items is { Count: > 0 })
                ImGui.TextDisabled(cs.PickerStale);

            if (items == null || items.Count == 0)
            {
                ImGui.TextDisabled(cs.PickerEmpty);
            }
            else
            {
                if (items[0].Skin) ImGui.TextDisabled(cs.PickerSkin);
                // Starts true when nothing is skin, so a separator never leads the list.
                bool separated = !items[0].Skin;
                foreach (var it in items)
                {
                    if (!it.Skin && !separated) { ImGui.Separator(); separated = true; }
                    // ##path: two races can produce identical labels, and duplicate ImGui ids would route
                    // the click to the wrong row.
                    if (ImGui.Selectable($"{it.Label}##{it.Path}",
                            string.Equals(it.Path, _createMaterial, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Same idiom as auto-detect and Re-detect: set both, then lock, so the 500 ms poll
                        // can't overwrite the choice and no later unlock reads a spurious "user edited".
                        _createMaterial = _createMaterialAuto = it.Path;
                        _createMaterialLocked = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(it.Path);
                }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(cs.Redetect))
        {
            _createMaterial = _createMaterialAuto = modCreation.DetectBodyMaterial()
                ?? modCreation.CachedBodyMaterial() ?? ModCreationService.DefaultBodyMaterial;
            _createMaterialLocked = true;   // explicit request — take this value and stop polling
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.RedetectTip);

        // Which rows the picked material can actually consume.
        if (_createMaterial != _createSlotsFor)
        {
            // A PROGRAMMATIC change — picker, Re-detect, auto-detect — keeps _createMaterialAuto in step and
            // is one deliberate event, so resolve at once: the rows must never lag a selection the user just
            // made. TYPING is throttled instead, since each resolve is a Penumbra lookup plus a file read
            // and the field changes on every keystroke.
            if (_createMaterial == _createMaterialAuto) ResolveCreateSlots();
            else if (_createSlotsNextTick == 0) _createSlotsNextTick = Environment.TickCount64 + 250;
            else if (Environment.TickCount64 >= _createSlotsNextTick) ResolveCreateSlots();
        }
        else _createSlotsNextTick = 0;

        ImGui.Spacing();
        // ONE offset for all four rows, so the file names form a column instead of each sitting behind its
        // own label. Never narrower than the original 90px, and widened only by however much the widest
        // TRANSLATED label needs. Measured per frame because the font, the UI scale and the language can
        // all change under us; measuring the label and the colon separately keeps it allocation-free.
        var colonW = ImGui.CalcTextSize(":").X;
        var labelColumn = MathF.Max(ProteusStyle.S(90f), ProteusStyle.S(12f) + colonW + MathF.Max(
            MathF.Max(ImGui.CalcTextSize(cs.SlotDiffuse).X, ImGui.CalcTextSize(cs.SlotMask).X),
            MathF.Max(ImGui.CalcTextSize(cs.SlotNormal).X, ImGui.CalcTextSize(cs.SlotIndex).X)));

        DrawTextureRow("Diffuse", cs.SlotDiffuse, labelColumn, ref _createDiffuse, SlotEnabled("Diffuse"), cs.NoDiffuse);
        DrawTextureRow("Mask",    cs.SlotMask,    labelColumn, ref _createMask,    SlotEnabled("Mask"),    cs.NoMask);
        DrawTextureRow("Normal",  cs.SlotNormal,  labelColumn, ref _createNormal,  SlotEnabled("Normal"),  cs.NoNormal);
        DrawTextureRow("Index",   cs.SlotIndex,   labelColumn, ref _createIndex,   SlotEnabled("Index"),   cs.NoIndex);
        // Only once a read has actually been ATTEMPTED for this exact material (_createSlotsFor tracks
        // that). Testing _createSlots alone announced a failure during the window before the read ran,
        // so the note flashed on every material change and then corrected itself.
        if (_createSlots == null && _createSlotsFor == _createMaterial && _createMaterial.Length > 0)
            ImGui.TextDisabled(cs.SlotsUnreadable);

        // Auto-detect the answer from the art itself, on the same "fill it in until the user says
        // otherwise" rule the material target follows. Keyed on everything that decides it, so it runs once
        // per pick rather than per frame, and off the frame thread because it decodes the picked diffuse
        // and the material's own. Only for a skin target: on gear or an accessory the question doesn't
        // arise, and the comparison it rests on would have nothing to compare against.
        var wholeSkinKey = SlotEnabled("Diffuse") && SlotEnabled("Normal")
                        && _createDiffuse.Length > 0 && _createNormal.Length > 0
                        && IsSkinMaterial(_createMaterial)
            ? _createMaterial + " " + _createDiffuse + " " + _createNormal
            : "";
        if (!_createWholeSkinLocked && wholeSkinKey != _createWholeSkinProbedFor)
        {
            _createWholeSkinProbedFor = wholeSkinKey;
            if (wholeSkinKey.Length == 0)
            {
                // Nothing to judge — a half-filled pair, or a target this can't apply to. Back to the
                // default rather than leaving the last pick's verdict standing over different files.
                _createWholeSkinProbe = null;
                _createWholeSkin = false;
            }
            else
            {
                // Captured, not read from the fields inside the lambda: the user can browse again while
                // this runs, and the result has to belong to the paths it was started for.
                string m = _createMaterial, d = _createDiffuse, n = _createNormal;
                _createWholeSkinProbe = Task.Run(() => modCreation.LooksLikeWholeSkin(m, d, n));
            }
        }
        if (_createWholeSkinProbe is { IsCompleted: true } wholeSkinProbe)
        {
            _createWholeSkinProbe = null;
            // A faulted or cancelled probe reads as "not a whole skin" — the same answer as a definite no,
            // and the safe one: compounding a detail overlay is right, replacing a skin's normal by
            // accident is not. The service already logged whatever went wrong.
            if (!_createWholeSkinLocked)
                _createWholeSkin = wholeSkinProbe.Status == TaskStatus.RanToCompletion && wholeSkinProbe.Result;
        }

        // Only decides anything for the NORMAL — whether it stacks onto the map already on the material or
        // overwrites it — but drawn unconditionally rather than gated on a picked normal: this is the tab
        // someone converts a whole skin mod from, and a control that appears only once the right file is
        // browsed is a control nobody finds. Harmless with no normal, and the tooltip says what it touches.
        if (ImGui.Checkbox(cs.WholeSkin, ref _createWholeSkin))
            _createWholeSkinLocked = true;   // an explicit answer — auto-detect never writes over it again
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.WholeSkinTip);

        // Face targets only, and never auto-detected: the two halves of an ORDINARY face texture are two
        // regions of one face, not two sides of a head, so nothing in the image distinguishes the layouts.
        // Only the author knows, which is why this is a plain tick with no probe behind it.
        if (IsFaceMaterial(_createMaterial))
        {
            ImGui.Checkbox(cs.FaceSplit, ref _createFaceSplit);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.FaceSplitTip);
        }
        else if (_createFaceSplit)
        {
            // The target moved off a face; the tick described the old one and would otherwise declare a
            // body overlay to be in a face layout.
            _createFaceSplit = false;
        }

        // ── glow ─────────────────────────────────────────────────────────────
        // The glow takes its colour from the art, per pixel, so it needs a diffuse; and it renders on a
        // second skin, which RenderModeInference.ShouldPromoteToGear refuses to cut for a gear, accessory
        // or weapon target — such an overlay "stays skin and simply doesn't glow". Dimmed rather than
        // hidden in both cases, matching the texture rows: a control that vanishes teaches nothing.
        bool glowHasArt   = SlotEnabled("Diffuse") && _createDiffuse.Length > 0;
        bool glowCanShell = IsSkinMaterial(_createMaterial) || IsFaceMaterial(_createMaterial);
        bool glowAllowed  = glowHasArt && glowCanShell;
        if (!glowAllowed) _createGlow = GlowStyle.None;

        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha,
                   ImGui.GetStyle().Alpha * (glowAllowed ? 1f : 0.5f)))
        using (ImRaii.Disabled(!glowAllowed))
        {
            bool glowing = _createGlow != GlowStyle.None;
            if (ImGui.Checkbox(cs.Glow, ref glowing))
                _createGlow = glowing ? GlowStyle.Always : GlowStyle.None;
        }
        // ReasonTooltip, not a bare IsItemHovered: a disabled item reports no hover under the default
        // flags, so the two explanations below — the only reason to dim this rather than hide it — would
        // have been unreachable at exactly the moment they are wanted.
        ProteusStyle.ReasonTooltip(!glowHasArt ? cs.GlowNeedsDiffuse
                                 : !glowCanShell ? cs.GlowNeedsSkin
                                 : cs.GlowTip);

        if (_createGlow != GlowStyle.None)
        {
            ImGui.Indent();
            if (ImGui.RadioButton(cs.GlowAlways, _createGlow == GlowStyle.Always))
                _createGlow = GlowStyle.Always;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.GlowAlwaysTip);

            if (ImGui.RadioButton(cs.GlowDarkOnly, _createGlow == GlowStyle.DarkOnly))
                _createGlow = GlowStyle.DarkOnly;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.GlowDarkOnlyTip);
            ImGui.Unindent();
        }

        ImGui.Separator();

        // Only ENABLED slots count. The clearing above already empties disabled ones; this keeps the
        // button honest if that ever misses.
        bool anyTexture = (SlotEnabled("Diffuse") && _createDiffuse.Length > 0)
            || (SlotEnabled("Mask") && _createMask.Length > 0)
            || (SlotEnabled("Normal") && _createNormal.Length > 0)
            || (SlotEnabled("Index") && _createIndex.Length > 0);
        bool valid = !string.IsNullOrWhiteSpace(_createName)
            && !string.IsNullOrWhiteSpace(_createMaterial)
            && anyTexture;

        // Penumbra loads a freshly added mod asynchronously, so enabling it is retried across frames — see
        // ModCreationService.Pump. The button stays inert meanwhile, because the create genuinely is not
        // finished, and the status line carries the interim message until it answers.
        if (modCreation.IsAwaiting)
        {
            if (modCreation.Pump() is { } pumped)
            {
                _createStatus = pumped.Message;
                _createStatusOk = pumped.Ok;
            }
            valid = false;
        }

        using (ImRaii.Disabled(!valid))
            if (ImGui.Button(cs.CreateBtn))
            {
                var r = modCreation.Create(
                    _createName, _createAuthor, _createMaterial,
                    SlotEnabled("Diffuse") ? NullIfEmpty(_createDiffuse) : null,
                    SlotEnabled("Mask")    ? NullIfEmpty(_createMask)    : null,
                    SlotEnabled("Normal")  ? NullIfEmpty(_createNormal)  : null,
                    SlotEnabled("Index")   ? NullIfEmpty(_createIndex)   : null,
                    _createWholeSkin,
                    _createFaceSplit && IsFaceMaterial(_createMaterial),
                    _createGlow);
                _createStatus = r.Message;
                _createStatusOk = r.Ok;
                if (r.Ok)   // keep name/author/material for a quick second mod; clear the pickers
                {
                    _createDiffuse = _createMask = _createNormal = _createIndex = "";
                    // The tick belonged to the textures just consumed, not to the tab. Clearing the lock
                    // too is the point: a hand-set answer that survived would silently decide the NEXT
                    // mod, which is the one case the user has no reason to expect.
                    _createWholeSkin = _createWholeSkinLocked = false;
                    _createWholeSkinProbedFor = "";
                    _createWholeSkinProbe = null;
                    // Same rule, and it matters more here: nothing probes this one, and moving between two
                    // FACE materials never trips the target-changed reset — so a tick left standing would
                    // declare the next mod's ordinary face texture to be split, and un-mirror its two halves
                    // onto the two sides of the head.
                    _createFaceSplit = false;
                    // Same rule again: the choice belonged to the art just consumed.
                    _createGlow = GlowStyle.None;
                }
            }
        // Same reason as the glow checkbox above: the button is submitted disabled, so a bare
        // IsItemHovered never fires and this tooltip had never once been shown to anyone.
        ProteusStyle.ReasonTooltip(valid ? null : cs.CreateDisabledTip);

        if (_createStatus != null)
            ImGui.TextColored(
                _createStatusOk ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(1f, 0.5f, 0.4f, 1f),
                _createStatus);
    }

    /// <summary>
    /// Read the picked material and narrow the texture rows to what it can consume, dropping any file
    /// browsed into a row that just lost its slot so nothing invisible reaches Create.
    /// </summary>
    private void ResolveCreateSlots()
    {
        _createSlotsFor = _createMaterial;
        _createSlotsNextTick = 0;
        var slots = modCreation.ResolveMaterialSlots(_createMaterial);
        // All-null means UNREADABLE, not "has no textures" — keep null so every row stays live.
        _createSlots = slots.Diffuse == null && slots.Normal == null
                    && slots.Mask == null && slots.Index == null
            ? null : slots;
        if (!SlotEnabled("Diffuse")) _createDiffuse = "";
        if (!SlotEnabled("Mask"))    _createMask = "";
        if (!SlotEnabled("Normal"))  _createNormal = "";
        if (!SlotEnabled("Index"))   _createIndex = "";
    }

    /// <summary>
    /// Can the picked material consume this slot? Diffuse/Normal/Mask come straight from the material.
    /// <para/>
    /// Index is a union of two independent reasons, because neither covers the other:
    /// <list type="bullet">
    /// <item>The material declares its own <c>_id</c> sampler — gear and accessories nearly always do.</item>
    /// <item>It's skin or face. Those NEVER declare an index sampler (verified across ~1,400 body and face
    /// materials: not one), so this can't be read off the material — a Proteus index on skin is Proteus's
    /// own colour-table concept. Testing only the sampler would grey Index out on exactly the surfaces it
    /// was built for.</item>
    /// </list>
    /// <c>_createSlots == null</c> means the material couldn't be read, and everything stays enabled: the
    /// field accepts hand-typed paths for bodies the player isn't wearing, which don't resolve.
    /// </summary>
    private bool SlotEnabled(string label) => _createSlots == null || label switch
    {
        "Diffuse" => _createSlots.Diffuse != null,
        "Mask"    => _createSlots.Mask    != null,
        "Normal"  => _createSlots.Normal  != null,
        "Index"   => _createSlots.Index != null || IsSkinMaterial(_createMaterial),
        _         => true,
    };

    /// <summary>
    /// One texture slot: current file name, a Browse button, and a Clear button. A slot the material can't
    /// consume is dimmed and inert, with <paramref name="disabledReason"/> on hover.
    /// </summary>
    /// <param name="slot">The invariant slot token — "Diffuse", "Mask", "Normal" or "Index". Never
    /// translated: it is the ImGui id, and it is what the file-dialog callback below switches on to decide
    /// which field the picked path lands in. These used to be one parameter with
    /// <paramref name="label"/>, which meant a translated label would have matched none of the switch arms
    /// and silently dropped every file the user picked.</param>
    /// <param name="label">What the user reads. Localized.</param>
    /// <param name="labelColumn">Where the file name starts, in pixels from the row's left edge. Passed in
    /// rather than measured here so all four rows share ONE offset: measuring per row lines each file name
    /// up behind its own label, which staggers the column as soon as the translated labels differ in width
    /// (Japanese "インデックス:" is far wider than "マスク:").</param>
    private void DrawTextureRow(string slot, string label, float labelColumn, ref string path,
                                bool enabled = true, string? disabledReason = null)
    {
        // Dims the row's TEXT, which ImRaii.Disabled alone wouldn't reach — the label and file name sit
        // outside the disabled scope so their tooltips stay hoverable. The Browse button below is disabled
        // properly; this is presentation only.
        using var dim = ImRaii.PushStyle(ImGuiStyleVar.Alpha,
            ImGui.GetStyle().Alpha * (enabled ? 1f : 0.5f));

        // Only a disabled row has anything to explain, so an enabled one passes null and draws no tooltip.
        // ProteusStyle.ReasonTooltip carries the AllowWhenDisabled reasoning.
        var reason = enabled ? null : disabledReason;

        var shown = path.Length == 0 ? Strings.Common.None : Path.GetFileName(path);
        ImGui.TextUnformatted($"{label}:");
        ProteusStyle.ReasonTooltip(reason);
        ImGui.SameLine(labelColumn);
        ImGui.TextUnformatted(enabled ? shown : Strings.Create.SlotUnused);

        ImGui.SameLine(ProteusStyle.S(360f));
        // Capture the field by a local setter — ref can't cross the dialog callback. The SLOT string is
        // load-bearing here: this switch is how the picked path reaches the field. Don't rename them.
        var captured = slot;
        // ImRaii.Disabled, NOT `enabled && SmallButton(...)`: the short-circuit would skip submitting the
        // button entirely, and an unsubmitted item can't be hovered, so the reason tooltip would vanish.
        // This keeps the item in the layout, blocks the click properly, and still reads as pressable-off
        // rather than swallowing a press that looks like it landed.
        using (ImRaii.Disabled(!enabled))
        {
            if (ImGui.SmallButton($"{Strings.Common.Browse}##browse_{slot}"))
            {
                _fileDialog.OpenFileDialog(
                    string.Format(Strings.Create.PickTextureTitleFmt, label),
                    "Images{.png,.tex,.dds,.jpg,.jpeg,.bmp,.tga}",
                    (ok, paths) =>
                    {
                        if (!ok) return;
                        var picked = paths.FirstOrDefault() ?? "";
                        switch (captured)
                        {
                            case "Diffuse": _createDiffuse = picked; break;
                            case "Mask":    _createMask = picked; break;
                            case "Normal":  _createNormal = picked; break;
                            case "Index":   _createIndex = picked; break;
                        }
                    }, 1);
            }
        }
        ProteusStyle.ReasonTooltip(reason);
        if (enabled && path.Length > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"{Strings.Common.Clear}##clear_{slot}"))
                path = "";
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ── Import tab ───────────────────────────────────────────────────────────

    /// <summary>
    /// Finish an import whose disk work completed on the pool: register the written mod with Penumbra and
    /// report the outcome.
    /// <para/>
    /// Driven from <see cref="Plugin.DrawUi"/> rather than from <see cref="Draw"/>, because Dalamud only
    /// calls <see cref="Draw"/> while the window is OPEN — and closing Proteus the moment you press Import
    /// is the natural thing to do, since Penumbra is about to take over. Pumping from the window would
    /// leave the mod written into Penumbra's directory but never added, never enabled, with no layout
    /// selected and no message anywhere; a later reopen would then pop Penumbra open for no visible reason.
    /// The registration itself is Penumbra IPC and must stay on the framework thread, which this is.
    /// </summary>
    /// <param name="unloading">
    /// Called from <see cref="Plugin.Dispose"/> rather than from a frame. Registers the mod so it isn't
    /// orphaned and does nothing else: no Penumbra window, no recomposite, and no touching this window —
    /// there is no ImGui frame in scope during teardown, and nobody left to read a status message.
    /// </param>
    public void TickImport(bool unloading = false)
    {
        TickContentImport(unloading);
        TickLuminisImport(unloading);
        // Before the import half, and skipped during teardown: there is no frame left to draw a preview on
        // and nobody to read it, and AutoImport would start a write nothing could then register.
        if (!unloading) TickEmissiveInspect();
        TickEmissiveImport(unloading);
        TickEyeImport(unloading);

        var done = _importPrepared;
        if (done == null) return;
        _importPrepared = null;
        _importBusy = false;

        var r = onionImport.Register(done, quiet: unloading);
        if (unloading) return;

        _importStatus = r.Message;
        _importStatusOk = r.Ok;
        _importStatusWarn = r.Warning;

        // Clear the picked pack on success so the tab can't re-import the same file into a name that now
        // exists; a failure keeps it so the user can fix the name and retry. Guarded on identity because
        // Browse stays live during an import: if the user has since picked a DIFFERENT pack, that one is
        // not the one that just finished and must not be thrown away.
        if (r.Ok && ReferenceEquals(_importPreview, done.Preview))
        {
            _importPreview = null;
            _importMaterials = null;
        }

        // The result is worth nothing if it lands on a window the user closed. Show it — they asked for
        // this, and Penumbra is opening at the same moment anyway.
        if (!IsOpen) Show(forceExpand: true);
    }

    /// <summary>
    /// The content-pack half of <see cref="TickImport"/>, on the same terms: the disk work runs on the
    /// pool, and the Penumbra registration has to land back here on the framework thread.
    /// </summary>
    private void TickContentImport(bool unloading)
    {
        var done = _contentPrepared;
        if (done == null) return;
        _contentPrepared = null;
        _importBusy = false;

        var r = contentImport.Register(done, quiet: unloading);
        if (unloading) return;

        _importStatus = r.Message;
        _importStatusOk = r.Ok;
        _importStatusWarn = r.Warning;

        if (r.Ok && ReferenceEquals(_contentPreview, done.Preview))
            _contentPreview = null;

        if (!IsOpen) Show(forceExpand: true);
    }

    /// <summary>
    /// The Atramentum Luminis half of <see cref="TickImport"/>. Unlike the other two this can span several
    /// frames after the write: Penumbra loads an added mod asynchronously and will not enable one it is
    /// still reading, so the registration is pumped until it answers. Twenty megabytes of overlay art is
    /// enough for that to take a moment.
    /// </summary>
    private void TickLuminisImport(bool unloading)
    {
        if (_luminisAwaiting)
        {
            // Nothing to pump into during teardown: there are no more frames, no one to read a status
            // message, and Pump would open Penumbra and schedule a recomposite into half-disposed services
            // — the very things `quiet` exists to prevent. The mod is already added, so it isn't orphaned.
            if (unloading) return;

            // Still loading — try again next frame. The button stays inert meanwhile, because the import
            // genuinely is not finished.
            if (luminisImport.Pump() is not { } pumped) return;
            _luminisAwaiting = false;
            FinishLuminisImport(pumped, _luminisAwaited);
            _luminisAwaited = null;
            return;
        }

        var done = _luminisPrepared;
        if (done == null) return;
        _luminisPrepared = null;

        var r = luminisImport.Register(done, quiet: unloading);
        if (unloading) return;

        if (r == null)
        {
            // Penumbra has the mod but hasn't finished loading it. Hold the busy flag and keep pumping.
            _luminisAwaiting = true;
            _luminisAwaited = done;
            return;
        }

        FinishLuminisImport(r.Value, done);
    }

    private void FinishLuminisImport(
        LuminisImportService.ImportResult r, LuminisImportService.PreparedImport? done)
    {
        _importBusy = false;
        _importStatus = r.Message;
        _importStatusOk = r.Ok;
        _importStatusWarn = r.Warning;

        // Guarded on identity because Browse stays live during an import: if the user has since picked a
        // different pack, that one is not the one that just finished and must not be thrown away.
        if (r.Ok && done != null && ReferenceEquals(_luminisPreview, done.Preview))
        {
            _luminisPreview = null;
            _luminisMaterials = null;
        }

        if (!IsOpen) Show(forceExpand: true);
    }

    /// <summary>
    /// The emissive-skin half of <see cref="TickImport"/>, on the same terms as the Atramentum Luminis one
    /// beside it: Penumbra loads an added mod asynchronously and discards a settings write that lands while
    /// it is still reading, so the registration is pumped until it answers.
    /// </summary>
    private void TickEmissiveImport(bool unloading)
    {
        if (_emissiveAwaiting)
        {
            // Nothing to pump into during teardown — no more frames, nobody to read a message, and Pump
            // would open Penumbra and schedule a recomposite into half-disposed services.
            if (unloading) return;
            if (emissiveImport.Pump() is not { } pumped) return;
            _emissiveAwaiting = false;
            FinishEmissiveImport(pumped, _emissiveAwaited);
            _emissiveAwaited = null;
            return;
        }

        var done = _emissivePrepared;
        if (done == null) return;
        _emissivePrepared = null;

        var r = emissiveImport.Register(done, quiet: unloading);
        if (unloading) return;

        if (r == null)
        {
            // Penumbra has the mod but hasn't finished loading it. Hold the busy flag and keep pumping.
            _emissiveAwaiting = true;
            _emissiveAwaited = done;
            return;
        }

        FinishEmissiveImport(r.Value, done);
    }

    private void FinishEmissiveImport(
        EmissiveSkinImportService.ImportResult r, EmissiveSkinImportService.PreparedImport? done)
    {
        _importBusy = false;
        _importStatus = r.Message;
        _importStatusOk = r.Ok;
        _importStatusWarn = r.Warning;

        // Guarded on identity because Browse stays live during an import: if the user has since picked a
        // different pack, that one is not the one that just finished and must not be thrown away.
        if (r.Ok && done != null && ReferenceEquals(_emissivePreview, done.Preview))
        {
            _emissivePreview = null;
            _emissiveMaterials = null;
        }

        if (!IsOpen) Show(forceExpand: true);
    }

    /// <summary>
    /// The eye-pack half of <see cref="TickImport"/>. Like the Atramentum Luminis one this can span
    /// several frames after the write: Penumbra loads an added mod asynchronously and discards a settings
    /// write that lands while it is still reading, so the registration is pumped until it answers.
    /// </summary>
    private void TickEyeImport(bool unloading)
    {
        if (_eyeAwaiting)
        {
            // Nothing to pump into during teardown — no more frames, nobody to read a message, and Pump
            // would open Penumbra and schedule a recomposite into half-disposed services.
            if (unloading) return;
            if (eyeImport.Pump() is not { } pumped) return;
            _eyeAwaiting = false;
            FinishEyeImport(pumped, _eyeAwaited);
            _eyeAwaited = null;
            return;
        }

        var done = _eyePrepared;
        if (done == null) return;
        _eyePrepared = null;

        var r = eyeImport.Register(done, quiet: unloading);
        if (unloading) return;

        if (r == null)
        {
            _eyeAwaiting = true;
            _eyeAwaited = done;
            return;
        }

        FinishEyeImport(r.Value, done);
    }

    private void FinishEyeImport(EyeImportService.ImportResult r, EyeImportService.PreparedImport? done)
    {
        _importBusy = false;
        _importStatus = r.Message;
        _importStatusOk = r.Ok;
        _importStatusWarn = r.Warning;

        if (r.Ok && done != null && ReferenceEquals(_eyePreview, done.Preview))
            _eyePreview = null;

        if (!IsOpen) Show(forceExpand: true);
    }

    private void DrawImportTab()
    {
        var ims = Strings.Import;
        var cms = Strings.Content;

        // A bullet each, .pmp first: an ordinary Penumbra mod is what most people arrive holding, and an
        // overlay pack is the specialist case.
        //
        // Indent is a left inset for the list and nothing more. It is NOT what hangs the wrapped lines —
        // ImGui does that by itself, since wrapped text restarts every line at the x it began at, which is
        // already past the bullet. Removing this call would only move the list back to the window margin.
        // Two calls rather than a loop over an array: this runs every frame the tab is open, and the pair
        // cannot be hoisted into a static — Strings.Import is REPLACED on a language change, so a cached
        // one would go on showing the old language.
        ImGui.Indent();
        ImGui.PushTextWrapPos(0);
        BulletLine(cms.Intro);
        BulletLine(ims.Intro);
        BulletLine(Strings.Luminis.Intro);
        BulletLine(Strings.Emissive.Intro);
        BulletLine(Strings.Eye.Intro);
        ImGui.PopTextWrapPos();
        ImGui.Unindent();
        ImGui.Separator();

        // ONE button, for both formats. Which reader a file needs is written on the file, so asking someone
        // to say it again before they may pick one is a question with no purpose — and the wrong answer used
        // to be a dead end, since each dialog filtered the other format out of sight entirely.
        //
        // The braced suffix is the dialog's own filter syntax, not prose, so it is built here from the
        // format constants and a translator only ever sees the human half. The label is stripped of the
        // three characters that syntax is made of before it goes in: a translation carrying a brace or a
        // comma would corrupt the filter, and with one button that breaks the only route to ANY pack rather
        // than to one format. None of the eight current translations contain them; the point is that
        // nothing in the code was making that true.
        if (ImGui.Button(ims.BrowseBtn))
            _fileDialog.OpenFileDialog(ims.DialogTitle,
                FilterLabel(ims.DialogFilter)
                    + "{" + PenumbraPackage.Extension
                    + "," + OnionPackage.Extension
                    + "," + TexToolsPackage.Extension
                    + "," + EyePackage.Extension + "}",
                (ok, paths) =>
                {
                    if (!ok) return;
                    var picked = paths.FirstOrDefault();
                    if (string.IsNullOrEmpty(picked)) return;
                    RememberImportDir(picked);
                    LoadPack(picked);
                    AutoImport();
                }, 1, LastImportDir());
        // SameLine inside each branch rather than once above them. The picked file's name goes beside the
        // button — the content preview prints it first thing, the Onion path prints it just below — but
        // when nothing has been picked there is no such name, and a SameLine left hanging would drag the
        // "pick a pack" line up onto the button's row instead.
        if (_contentPreview != null)
        {
            ImGui.SameLine();
            DrawContentImport(_contentPreview);
            return;
        }

        if (_luminisPreview != null)
        {
            ImGui.SameLine();
            DrawLuminisImport(_luminisPreview);
            return;
        }

        if (_emissivePreview != null)
        {
            ImGui.SameLine();
            DrawEmissiveImport(_emissivePreview);
            return;
        }

        if (_eyePreview != null)
        {
            ImGui.SameLine();
            DrawEyeImport(_eyePreview);
            return;
        }

        // The one loader that answers on a later frame — see LoadEmissivePack. Without this the tab falls
        // through to "pick a pack", which is the opposite of what is happening.
        if (_emissiveLoading)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(Path.GetFileName(_importPath));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(_importPath);
            ImGui.Spacing();
            ImGui.TextDisabled(Strings.Emissive.Reading);
            DrawImportStatus();
            return;
        }

        if (_importPath.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(Path.GetFileName(_importPath));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(_importPath);
        }

        var preview = _importPreview;
        if (preview == null)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(ims.NoPack);
            DrawImportStatus();
            return;
        }

        ImGui.Spacing();
        ImGui.InputText(ims.ModName, ref _importName, 128);
        ImGui.InputText(ims.Author, ref _importAuthor, 128);

        if (preview.Description != null)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(ims.Description);
            ImGui.TextWrapped(preview.Description);
        }
        if (preview.Website != null)
        {
            ImGui.TextDisabled(preview.Website);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(ims.WebsiteTip);
        }

        ImGui.Separator();
        DrawImportLayers(preview);

        var layouts = preview.Layouts;
        if (layouts.Count > 1)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(string.Format(ims.LayoutsFmt,
                layouts.Count, OnionImportService.LayoutGroupName, string.Join(", ", layouts)));
            if (preview.DefaultLayoutMatchedBody && preview.DefaultLayout != null)
                ImGui.TextDisabled(string.Format(ims.DefaultLayoutMatchedFmt, preview.DefaultLayout));
        }

        DrawImportBodyFit(preview);
        DrawImportMaterials();

        ImGui.Spacing();
        using (ImRaii.Disabled(!TextureLoader.NativeEncoderAvailable))
            ImGui.Checkbox(ims.AsTex, ref _importAsTex);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TextureLoader.NativeEncoderAvailable ? ims.AsTexTip : ims.AsTexUnavailableTip);

        foreach (var w in preview.Warnings)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ImportWarnColour, w);
            ImGui.PopTextWrapPos();
        }

        ImGui.Separator();

        bool valid = preview.AnyImportable && !string.IsNullOrWhiteSpace(_importName) && !_importBusy;
        using (ImRaii.Disabled(!valid))
            // Both captions carry the same "###importGo" id, so the button stays one widget across the
            // busy flip rather than becoming a different item mid-import.
            if (ImGui.Button(_importBusy ? ims.ImportBusy : ims.ImportBtn))
                StartImport(preview);
        if (!valid && !_importBusy && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(preview.AnyImportable ? ims.NeedName : ims.NothingUsable);

        DrawImportStatus();
    }

    /// <summary>
    /// Parse a picked pack into the preview, or report why it isn't one. Both the parse and the material
    /// resolve are done HERE, on the one frame the file dialog reports a pick — they're multi-ms, and the
    /// draw runs them at frame rate otherwise.
    /// </summary>
    /// <summary>
    /// Read a picked pack with whichever reader its extension calls for.
    /// <para/>
    /// Everything that is not an <c>.omp</c> or a <c>.ttmp2</c> goes to the Penumbra reader rather than to
    /// an "unsupported" arm of its own. That arm would need a string to say something
    /// <see cref="PenumbraPackage.Read"/> already says better — it rejects a file with no manifest as "Not a
    /// Penumbra pack", which <see cref="LoadPenumbraPack"/> puts on screen. A file that is none of the three
    /// gets a true sentence, and the tab gets no message that exists only to be wrong about.
    /// </summary>
    private void LoadPack(string path)
    {
        // Every pick supersedes an emissive read still running on the pool, whatever kind of pack it is —
        // see EmissiveInspected. Both live in the one place a pick arrives rather than in each loader, so a
        // loader added later cannot forget them: an uncleared flag would hang "reading…" over the panel of
        // whatever was picked instead, since that branch is tested before the Onion preview's.
        _emissivePickToken++;
        _emissiveLoading = false;

        if (path.EndsWith(OnionPackage.Extension, StringComparison.OrdinalIgnoreCase)) LoadOnionPack(path);
        else if (path.EndsWith(TexToolsPackage.Extension, StringComparison.OrdinalIgnoreCase)) LoadLuminisPack(path);
        else if (path.EndsWith(EyePackage.Extension, StringComparison.OrdinalIgnoreCase)) LoadEyePack(path);
        else LoadPenumbraPack(path);
    }

    /// <summary>
    /// Choose between the two readers that both take a <c>.pmp</c>, and hand the parsed manifest to whichever
    /// wins so the pick costs one archive open rather than two.
    /// <para/>
    /// The question is settled from the manifest alone — see <see cref="EmissiveSkinImportService.Claims"/>
    /// — because it is only a choice of reader, and decoding a pack's textures to make it would decode them
    /// for the reader that then doesn't want them. A pack that ships geometry goes to the content importer
    /// whatever else it carries; one that ships none, but does ship art on a path the game will never ask
    /// for, is glow art that Penumbra on its own can do nothing with.
    /// <para/>
    /// A read that throws is NOT handled here. It falls through to the content importer, whose own catch
    /// turns it into the "couldn't read that pack" line the tab has always shown — one message for one
    /// failure, rather than two arms racing to describe it.
    /// </summary>
    private void LoadPenumbraPack(string path)
    {
        PenumbraPackage.Contents? pack = null;
        try { pack = PenumbraPackage.Read(path); }
        catch (Exception) { /* reported by LoadContentPack, which reads it again and fails the same way */ }

        if (pack != null && EmissiveSkinImportService.Claims(pack)) LoadEmissivePack(path, pack);
        else LoadContentPack(path, pack);
    }

    /// <summary>
    /// Import a just-picked pack outright when the preview has nothing to say about it.
    /// <para/>
    /// The preview earns a second click when it is telling the user something they might act on — a pack
    /// that drops pieces, one that needs the sibling mode raised, one carrying warnings of its own. When it
    /// says none of those, the click is a confirmation of a screen nobody needed to read, and the pack goes
    /// in either way.
    /// <para/>
    /// The condition is written as "everything the preview would have coloured amber is absent", so the two
    /// stay in step: each clause below is one of the warnings the panel draws. The quiet informational
    /// lines — the all-off note, the remap note, the material-target list — are NOT clauses, because
    /// nothing about them is a decision.
    /// <para/>
    /// Called from the file dialog's callback, which runs inside <see cref="Draw"/> on the framework thread,
    /// so it is the same context the Import button itself would be clicked in. Both Start methods only set a
    /// flag and hand the disk work to the pool, so nothing blocks here either way.
    /// <para/>
    /// The preview stays on screen after it fires. The panel it draws is still the truth about what went in,
    /// and the status line under it reports the result.
    /// </summary>
    private void AutoImport()
    {
        if (_importBusy || string.IsNullOrWhiteSpace(_importName)) return;

        if (_contentPreview is { } content)
        {
            // No piece that came out WRONG — see ImportPreview.FaultyUnits. Not "every piece imported":
            // that counts the body meshes Proteus drops on purpose, so an outfit pack shipping its own
            // fitted body would be sent back for a second click on an import the result line calls clean.
            // This is the same question the result colour asks, and the two must answer alike.
            if (content.CanImport && content.Warnings.Count == 0 && content.FaultyUnits == 0)
                StartContentImport(content);
            return;
        }

        if (_eyePreview is { } eye)
        {
            // Every file recognised, a glow to add, and nothing to warn about. A pack that can only bring
            // its textures in is still a real import, but "no animation" is the decision the user came
            // here to make, so it earns the second click.
            if (eye.AnyImportable && eye.Warnings.Count == 0 && eye.CanGlow
             && eye.Files.All(f => f.Import) && eyeImport.IrisesFromGameData)
                StartEyeImport(eye);
            return;
        }

        if (_luminisPreview is { } luminis)
        {
            // Every texture in, nothing to warn about. The warnings this preview raises are all decisions —
            // an unrecognised body, no race filter, a mask that covers the whole sheet — so any of them
            // earns the second click.
            if (luminis.AnyImportable
             && luminis.Warnings.Count == 0
             && luminis.Textures.All(t => t.Import)
             && (_luminisMaterials is null or { Count: 0 } || _luminisMaterialsFromGameData))
                StartLuminisImport(luminis);
            return;
        }

        if (_emissivePreview is { } emissive)
        {
            // Every texture in, nothing to warn about — the same test as the Atramentum Luminis arm above,
            // and for the same reason: every warning this preview raises is a decision.
            if (emissive.AnyImportable
             && emissive.Warnings.Count == 0
             && emissive.Textures.All(t => t.Import)
             && (_emissiveMaterials is null or { Count: 0 } || _emissiveMaterialsFromGameData))
                StartEmissiveImport(emissive);
            return;
        }

        if (_importPreview is { } onion
         && onion.AnyImportable
         && onion.Warnings.Count == 0
         && onion.Layers.All(l => l.Import)
         && !onion.NeedsAllBodies
         // Only a warning when there IS a material list to be wrong about; an unresolved one prints nothing.
         && (_importMaterials is null or { Count: 0 } || _importMaterialsFromGameData))
            StartImport(onion);
    }

    /// <summary>
    /// Where the import picker should open — the folder a pack was last taken from, or null for the
    /// dialog's own default.
    /// <para/>
    /// Checked for existence on the way OUT rather than trusted from the config: the folder may have been
    /// renamed, emptied or unplugged since it was recorded, and handing the dialog a path that no longer
    /// resolves is worse than handing it nothing.
    /// </summary>
    private string? LastImportDir()
    {
        var dir = config.LastImportDir;
        return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null;
    }

    /// <summary>Record the folder a pack was picked from, so the next import opens there.</summary>
    private void RememberImportDir(string packPath)
    {
        string? dir;
        try { dir = Path.GetDirectoryName(packPath); }
        catch { return; }   // a path shape GetDirectoryName rejects is not one worth remembering

        if (string.IsNullOrEmpty(dir)
         || string.Equals(dir, config.LastImportDir, StringComparison.OrdinalIgnoreCase)) return;
        config.LastImportDir = dir;
        config.Save();
    }

    /// <summary>
    /// The name an imported pack is offered under: the pack's own, marked as the Proteus copy.
    /// <para/>
    /// An import writes a SECOND mod beside the original — the pack stays installable in Penumbra on its
    /// own terms — so the two sit together in the mod list and need telling apart. It is only a default:
    /// the name box is right there and the user owns what finally gets written.
    /// <para/>
    /// A pack that already says Proteus is left alone rather than made to say it twice; the sample packs
    /// here are called things like "Neolithe Piercings for Proteus" already.
    /// </summary>
    private static string ProteusName(string packName)
    {
        var name = (packName ?? string.Empty).Trim();
        return name.Length == 0 || name.Contains("proteus", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + " (Proteus)";
    }

    /// <summary>One bulleted line of wrapped body text. Honours whatever wrap position is pushed around
    /// it; the bullet advances the cursor itself, so the text follows on the same row.</summary>
    private static void BulletLine(string text)
    {
        ImGui.Bullet();
        ImGui.TextUnformatted(text);
    }

    /// <summary>
    /// A localized file-dialog label with the filter syntax's own characters removed, so a translation
    /// cannot corrupt the filter it gets concatenated into. Called on click, not per frame.
    /// </summary>
    private static string FilterLabel(string label)
        => label.IndexOfAny(['{', '}', ',']) < 0
            ? label
            : new string([.. label.Where(c => c is not ('{' or '}' or ','))]);

    private void LoadOnionPack(string path)
    {
        _importPath = path;
        _importStatus = null;
        _importMaterials = null;
        // The kinds of pack are imported under different rules, and a stale preview of another kind would
        // keep drawing its own panel over this one.
        _contentPreview = null;
        _luminisPreview = null;
        _emissivePreview = null;
        _eyePreview = null;
        try
        {
            var preview = onionImport.Inspect(path);
            _importPreview = preview;
            _importName = ProteusName(preview.Name);
            _importAuthor = preview.Author;
            // Best effort: a failure here only costs the "Material targets" list, not the import, which
            // resolves them again for itself.
            try
            {
                _importMaterials = onionImport.MaterialsFor(preview);
                _importMaterialsFromGameData = onionImport.BodiesFromGameData;
            }
            catch { /* preview only */ }
        }
        catch (Exception ex)
        {
            _importPreview = null;
            _importStatus = $"Couldn't read that pack: {ex.Message}";
            _importStatusOk = false;
        }
    }

    /// <summary>
    /// Hand the disk work to the pool. Copying a pack is tens of megabytes (and, with BC7 on, several 4K
    /// encodes) — doing that inline would stall the game for seconds. <see cref="PumpImport"/> picks the
    /// result up and registers it.
    /// </summary>
    private void StartImport(OnionImportService.ImportPreview preview)
    {
        _importBusy = true;
        _importStatus = null;
        // Copy the editable fields now: the user can keep typing while the write runs.
        var (name, author, asTex) = (_importName, _importAuthor, _importAsTex);
        Task.Run(() =>
        {
            try { _importPrepared = onionImport.Prepare(preview, name, author, asTex); }
            catch (Exception ex)
            {
                _importPrepared = new(false, string.Format(Strings.Import.ImportFailedFmt, ex.Message), null, null, 0, 0);
            }
        });
    }

    /// <summary>
    /// Parse a picked <c>.pmp</c> into the content preview, or report why it isn't one. Done HERE, on the
    /// one frame the dialog reports a pick, for the same reason as the Onion path: reading every model in
    /// the pack is milliseconds, not a per-frame cost.
    /// </summary>
    /// <param name="pack">The manifest <see cref="LoadPenumbraPack"/> already parsed, or null to read it
    /// here.</param>
    private void LoadContentPack(string path, PenumbraPackage.Contents? pack = null)
    {
        _importPath = path;
        _importStatus = null;
        _importPreview = null;    // see LoadOnionPack
        _luminisPreview = null;
        _emissivePreview = null;
        _eyePreview = null;
        _importMaterials = null;
        try
        {
            var preview = ContentImportService.Inspect(
                path, Plugin.Log, ItemNames.Lookup(Plugin.DataManager, Plugin.Log), pack);
            _contentPreview = preview;
            // No "(Proteus)" on a pack that already IS one: the suffix exists to tell a converted copy from
            // the pack it was made out of, and nothing is being converted here.
            _importName = preview.InstallOnly ? preview.Name : ProteusName(preview.Name);
            _importAuthor = preview.Author;
        }
        catch (Exception ex)
        {
            _contentPreview = null;
            _importStatus = string.Format(Strings.Content.ReadFailedFmt, ex.Message);
            _importStatusOk = false;
        }
    }

    /// <summary>The content-pack preview: what each option ships, and whether Proteus can append it.</summary>
    private void DrawContentImport(ContentImportService.ImportPreview preview)
    {
        var cms = Strings.Content;
        var ims = Strings.Import;

        ImGui.TextUnformatted(Path.GetFileName(_importPath));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(_importPath);

        ImGui.Spacing();
        ImGui.InputText(ims.ModName, ref _importName, 128);
        ImGui.InputText(ims.Author, ref _importAuthor, 128);

        if (preview.Description != null)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(ims.Description);
            ImGui.TextWrapped(preview.Description);
        }
        if (preview.Website != null)
        {
            ImGui.TextDisabled(preview.Website);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(ims.WebsiteTip);
        }

        ImGui.Separator();

        // A ready-made Proteus mod has no piece list to draw — nothing of its geometry is being taken over.
        // Said plainly and NOT in the warning colour: copying it in unchanged is a correct outcome, not a
        // shortfall. See ImportPreview.InstallOnly.
        if (preview.InstallOnly)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(cms.AlreadyProteus);
            ImGui.PopTextWrapPos();
            ImGui.Separator();
            bool ok = !string.IsNullOrWhiteSpace(_importName) && !_importBusy;
            using (ImRaii.Disabled(!ok))
                if (ImGui.Button(_importBusy ? ims.ImportBusy : ims.ImportBtn))
                    StartContentImport(preview);
            DrawImportStatus();
            return;
        }

        ImGui.TextUnformatted(string.Format(cms.PieceCountFmt, preview.ImportableUnits, preview.TotalUnits));

        // One row per PIECE, not per file: a pack that ships the same garment for five races ships one
        // garment, and the race variants are folded into the count in the third column.
        using (var table = ImRaii.Table("##contentPieces", 5,
                   ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            if (table)
                foreach (var unit in preview.Units)
                {
                    var lead = unit.Variants.FirstOrDefault(v => v.Import) ?? unit.Variants[0];

                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    if (unit.Import) ImGui.TextUnformatted(unit.Slot.Label);
                    else ImGui.TextDisabled(unit.Slot.Label);

                    // The vanilla item the pack replaces, which is what the synthesized option is named
                    // after. Falls back to the set id, so the column is never blank.
                    ImGui.TableNextColumn();
                    if (unit.Import) ImGui.TextUnformatted(unit.ItemName ?? unit.Slot.SetTag);
                    else ImGui.TextDisabled(unit.ItemName ?? unit.Slot.SetTag);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(unit.Slot.SetTag);

                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(unit.Variants.Count > 1
                        ? string.Format(cms.RacesFmt, unit.Variants.Count)
                        : "");

                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(unit.Import
                        ? string.Format(cms.GeometryFmt, lead.Meshes, lead.Vertices)
                        : cms.Skipped);

                    ImGui.TableNextColumn();
                    if (unit.Import)
                    {
                        var mtrl = Path.GetFileName(lead.Bindings.Values.First());
                        ImGui.TextDisabled(lead.Bindings.Count > 1
                            ? string.Format(cms.MaterialsFmt, lead.Bindings.Count)
                            : mtrl);
                    }
                    // Amber only for a piece that came out WRONG. A body-only model is dropped on purpose —
                    // the wearer has their own — so it reads as a plain dimmed note. Colouring it, and
                    // labelling it "unbound material" when nothing is unbound, put an amber row under a
                    // green result line and made the two look like they disagreed about the same import.
                    else if (lead.BodyOnly)
                        ImGui.TextDisabled(cms.BodyOnly);
                    else
                        ImGui.TextColored(ImportWarnColour, cms.Unbound);

                    if (lead.Problem != null && ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Format(cms.ProblemFmt, unit.Label, lead.Problem));
                }
        }

        // Said before the button, not after the import: pieces arriving switched off is the single most
        // surprising thing about this flow, and a character that changes nothing looks like a failure.
        if (preview.PieceGroupName is { } gate && preview.AnyImportable)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(string.Format(cms.AllOffFmt, gate));
            ImGui.PopTextWrapPos();
        }

        foreach (var w in preview.Warnings)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ImportWarnColour, w);
            ImGui.PopTextWrapPos();
        }

        ImGui.Separator();

        bool valid = preview.CanImport && !string.IsNullOrWhiteSpace(_importName) && !_importBusy;
        using (ImRaii.Disabled(!valid))
            if (ImGui.Button(_importBusy ? ims.ImportBusy : ims.ImportBtn))
                StartContentImport(preview);
        if (!valid && !_importBusy && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(preview.CanImport ? ims.NeedName : cms.NothingUsable);

        DrawImportStatus();
    }

    /// <summary>Hand the unpack to the pool; <see cref="TickContentImport"/> registers what comes back.</summary>
    private void StartContentImport(ContentImportService.ImportPreview preview)
    {
        _importBusy = true;
        _importStatus = null;
        var (name, author) = (_importName, _importAuthor);
        Task.Run(() =>
        {
            try { _contentPrepared = contentImport.Prepare(preview, name, author); }
            catch (Exception ex)
            {
                _contentPrepared = new(false, string.Format(Strings.Import.ImportFailedFmt, ex.Message),
                    null, null, 0, 0);
            }
        });
    }

    /// <summary>
    /// Parse a picked <c>.ttmp2</c> into the Atramentum Luminis preview, or report why it isn't one.
    /// <para/>
    /// Costlier than the other two loaders — it DECODES every candidate texture, roughly 90 ms a sheet —
    /// and paid on this one frame for the reason <see cref="LuminisImportService.Inspect"/> gives: whether
    /// a modpack is an AL pack at all is a question about its pixels, and it is the question the user
    /// opened the preview to have answered.
    /// </summary>
    private void LoadLuminisPack(string path)
    {
        _importPath = path;
        _importStatus = null;
        _importPreview = null;    // see LoadOnionPack
        _contentPreview = null;
        _emissivePreview = null;
        _eyePreview = null;
        _importMaterials = null;
        _luminisMaterials = null;
        _emissiveMaterials = null;
        try
        {
            var preview = luminisImport.Inspect(path);
            _luminisPreview = preview;
            _importName = ProteusName(preview.Name);
            _importAuthor = preview.Author;
            _luminisSuffix = preview.DefaultSuffix ?? "";
            // Best effort: a failure here only costs the "Material targets" list, not the import, which
            // resolves them again for itself.
            try
            {
                _luminisMaterials = luminisImport.MaterialsFor(preview, NullIfEmpty(_luminisSuffix));
                _luminisMaterialsFromGameData = luminisImport.BodiesFromGameData;
            }
            catch { /* preview only */ }
        }
        catch (Exception ex)
        {
            _luminisPreview = null;
            _importStatus = string.Format(Strings.Luminis.ReadFailedFmt, ex.Message);
            _importStatusOk = false;
        }
    }

    /// <summary>The Atramentum Luminis preview: which textures carry a glow mask, and where they will
    /// land.</summary>
    private void DrawLuminisImport(LuminisImportService.ImportPreview preview)
    {
        var ls = Strings.Luminis;
        var ims = Strings.Import;

        ImGui.TextUnformatted(Path.GetFileName(_importPath));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(_importPath);

        ImGui.Spacing();
        ImGui.InputText(ims.ModName, ref _importName, 128);
        ImGui.InputText(ims.Author, ref _importAuthor, 128);

        if (preview.Description != null)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(ims.Description);
            ImGui.TextWrapped(preview.Description);
        }
        if (preview.Website != null)
        {
            ImGui.TextDisabled(preview.Website);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(ims.WebsiteTip);
        }

        ImGui.Separator();
        ImGui.TextUnformatted(string.Format(ls.TextureCountFmt,
            preview.Textures.Count(t => t.Import), preview.Textures.Count));

        // One row per PICTURE, not per manifest path: an AL pack aliases one texture to a path per race,
        // and listing six rows for one tattoo would read as six tattoos.
        using (var table = ImRaii.Table("##luminisTextures", 4,
                   ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            if (table)
                foreach (var t in preview.Textures)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    if (t.Import) ImGui.TextUnformatted(t.Label);
                    else ImGui.TextDisabled(t.Label);
                    if (t.Paths.Count > 1 && ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Format(ls.PathsFmt, string.Join("\n", t.Paths)));

                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(t.Import ? string.Format(ls.SizeFmt, t.Width, t.Height) : "");

                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(t.Paths.Count > 1
                        ? string.Format(ls.AliasesFmt, t.Paths.Count)
                        : "");

                    // The glow percentage is the evidence that this is an AL pack rather than an ordinary
                    // texture mod, so it goes in the table where it is read, not behind a hover.
                    ImGui.TableNextColumn();
                    if (t.Import) ImGui.TextUnformatted(string.Format(ls.GlowFmt, t.GlowFraction));
                    else ImGui.TextColored(ImportWarnColour, ls.Skipped);

                    if (t.SkipReason != null && ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Format(ls.SkippedReasonFmt, t.Label, t.SkipReason));
                }
        }

        // ── which body ──
        ImGui.Spacing();
        ImGui.SetNextItemWidth(200);
        var suffixes = LuminisImportService.BodySuffixes;
        var current = string.IsNullOrEmpty(_luminisSuffix) ? (preview.DefaultSuffix ?? "") : _luminisSuffix;
        if (ImGui.BeginCombo(ls.BodyTarget, current))
        {
            foreach (var s in suffixes)
                if (ImGui.Selectable(s, string.Equals(s, current, StringComparison.OrdinalIgnoreCase)))
                {
                    _luminisSuffix = s;
                    // Re-resolved on the CHANGE, not per frame: the catalogue rebuilds a list per call.
                    try
                    {
                        _luminisMaterials = luminisImport.MaterialsFor(preview, s);
                        _luminisMaterialsFromGameData = luminisImport.BodiesFromGameData;
                    }
                    catch { _luminisMaterials = null; }
                }
            // A body the combo doesn't list — the wearer is on something exotic — is still the live choice
            // and has to be selectable, or picking anything else would be a one-way door.
            if (!suffixes.Contains(current, StringComparer.OrdinalIgnoreCase) && current.Length > 0)
                if (ImGui.Selectable(current, true)) _luminisSuffix = current;
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ls.BodyTargetTip);

        var lead = preview.Importable.FirstOrDefault();
        if (lead is { FromWearer: false, Token: { } token })
            ImGui.TextDisabled(string.Format(ls.BodyFromPackFmt, token));

        DrawLuminisMaterials();

        ImGui.Spacing();
        using (ImRaii.Disabled(!TextureLoader.NativeEncoderAvailable))
            ImGui.Checkbox(ims.AsTex, ref _importAsTex);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TextureLoader.NativeEncoderAvailable ? ims.AsTexTip : ims.AsTexUnavailableTip);

        // Said before the button: the author's body texture is the surprising half of this import. Plain
        // text, not the warning colour — both of these are true of a CORRECT import, and amber would put a
        // caution under a green result line and make the two look like they disagreed.
        if (preview.AnyImportable)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(string.Format(ls.SkinIncludedFmt, LuminisImportService.GroupName));
            ImGui.Spacing();
            ImGui.TextUnformatted(ls.NoRaceFilter);
            ImGui.PopTextWrapPos();
        }

        foreach (var w in preview.Warnings)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ImportWarnColour, w);
            ImGui.PopTextWrapPos();
        }

        ImGui.Separator();

        bool valid = preview.AnyImportable && !string.IsNullOrWhiteSpace(_importName) && !_importBusy;
        using (ImRaii.Disabled(!valid))
            if (ImGui.Button(_importBusy ? ims.ImportBusy : ims.ImportBtn))
                StartLuminisImport(preview);
        if (!valid && !_importBusy && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(preview.AnyImportable ? ims.NeedName : ls.NothingUsable);

        DrawImportStatus();
    }

    /// <summary>The material paths the imported overlays will claim, collapsed by default. The Onion
    /// counterpart groups by layout; there is only ever one here, so this is the flat list.</summary>
    private void DrawLuminisMaterials()
    {
        var mats = _luminisMaterials;
        if (mats == null || mats.Count == 0) return;   // unresolved or unreadable — the import still works

        var ims = Strings.Import;

        // Outside the collapsing header, for the same reason as the Onion one: a fallback list is exactly
        // what the user won't expand to check, and it silently names no male body.
        if (!_luminisMaterialsFromGameData)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ImportWarnColour, ims.FallbackBodies);
            ImGui.PopTextWrapPos();
        }

        if (!ImGui.CollapsingHeader(string.Format(ims.MaterialTargetsFmt, mats.Count) + "###luminisMats"))
            return;

        ImGui.TextWrapped(_luminisMaterialsFromGameData ? ims.MaterialsFromGame : ims.MaterialsFallbackNote);
        foreach (var p in mats)
        {
            ImGui.Bullet();
            ImGui.TextUnformatted(Path.GetFileName(p));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(p);
        }
    }

    /// <summary>
    /// Start reading a picked <c>.pmp</c> into the emissive-skin preview. The only loader that does not
    /// answer on the frame it was called.
    /// <para/>
    /// The reason is measured, not defensive: these masks are authored at the body's own resolution, and
    /// the 8192² one the pack this was written against ships takes 1.06 seconds to decode. The file
    /// dialog's callback runs inside <c>Draw</c> on the framework thread, so doing that here stopped the
    /// game dead for over a second — where the Atramentum Luminis loader beside it, which reads its whole
    /// pack inline, is paying about 90 ms a sheet for the same kind of work.
    /// <para/>
    /// So the pick frame does only what it must: the manifest is already parsed, and the wearer's body has
    /// to be asked of Penumbra HERE because that is IPC. The decode goes to the pool, and
    /// <see cref="TickEmissiveInspect"/> puts the preview on screen when it lands — including the
    /// auto-import the dialog callback would otherwise have run before there was anything to import.
    /// </summary>
    /// <param name="pack">The manifest <see cref="LoadPenumbraPack"/> already parsed.</param>
    private void LoadEmissivePack(string path, PenumbraPackage.Contents pack)
    {
        _importPath = path;
        _importStatus = null;
        _importPreview = null;    // see LoadOnionPack
        _contentPreview = null;
        _luminisPreview = null;
        _emissivePreview = null;
        _eyePreview = null;
        _importMaterials = null;
        _luminisMaterials = null;
        _emissiveMaterials = null;
        _emissiveLoading = true;

        // Framework thread only — it asks Penumbra what the character has on. Everything after this point
        // is Lumina and pixels, which the write pass already does off-thread.
        var wearer = emissiveImport.DetectWearerBody();
        var token = _emissivePickToken;

        Task.Run(() =>
        {
            try
            {
                var preview = emissiveImport.Inspect(path, pack, wearer);
                // Resolved out here beside the preview, as the other loaders do on their own frame: the
                // catalogue walks 72 game-data probes on its first call, which is not a per-frame cost.
                // Best effort — a failure costs the "Material targets" list, not the import, which resolves
                // them again for itself.
                IReadOnlyList<string>? materials = null;
                bool fromGameData = false;
                try
                {
                    materials = emissiveImport.MaterialsFor(preview, NullIfEmpty(preview.DefaultSuffix ?? ""));
                    fromGameData = emissiveImport.BodiesFromGameData;
                }
                catch { /* preview only */ }

                _emissiveInspected = new EmissiveInspected(token, preview, materials, fromGameData, null);
            }
            catch (Exception ex)
            {
                _emissiveInspected = new EmissiveInspected(
                    token, null, null, false, string.Format(Strings.Emissive.ReadFailedFmt, ex.Message));
            }
        });
    }

    /// <summary>
    /// Put a finished pool read on screen, on the framework thread. Nothing else may assign
    /// <c>_emissivePreview</c>: the fields below have to move together or the panel draws one pack's
    /// textures over another's material list.
    /// </summary>
    private void TickEmissiveInspect()
    {
        var done = _emissiveInspected;
        if (done == null) return;
        _emissiveInspected = null;

        // A read for a file the user has already replaced. Dropped in silence — the pick that superseded it
        // has its own panel up, and a message about a pack nobody is looking at any more is noise. The
        // loading flag is NOT cleared here: it belongs to the newer pick, which may still be running.
        if (done.Token != _emissivePickToken) return;

        _emissiveLoading = false;

        if (done.Error != null)
        {
            _importStatus = done.Error;
            _importStatusOk = false;
            return;
        }

        var preview = done.Preview!;
        _emissivePreview = preview;
        _emissiveMaterials = done.Materials;
        _emissiveMaterialsFromGameData = done.MaterialsFromGameData;
        _importName = ProteusName(preview.Name);
        _importAuthor = preview.Author;
        _emissiveSuffix = preview.DefaultSuffix ?? "";

        // The dialog callback ran this the moment the file was picked, when there was still nothing to
        // import. This is where the decision can actually be made.
        AutoImport();
    }

    /// <summary>The emissive-skin preview: which textures carry a glow mask, and where they will land.</summary>
    private void DrawEmissiveImport(EmissiveSkinImportService.ImportPreview preview)
    {
        var es = Strings.Emissive;
        var ims = Strings.Import;

        ImGui.TextUnformatted(Path.GetFileName(_importPath));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(_importPath);

        ImGui.Spacing();
        ImGui.InputText(ims.ModName, ref _importName, 128);
        ImGui.InputText(ims.Author, ref _importAuthor, 128);

        if (preview.Description != null)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(ims.Description);
            ImGui.TextWrapped(preview.Description);
        }
        if (preview.Website != null)
        {
            ImGui.TextDisabled(preview.Website);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(ims.WebsiteTip);
        }

        ImGui.Separator();
        ImGui.TextUnformatted(string.Format(es.TextureCountFmt,
            preview.Textures.Count(t => t.Import), preview.Textures.Count));

        // One row per PICTURE, not per manifest path — a pack that aliases one sheet to several paths is
        // shipping one tattoo, and listing it once per path would read as several.
        using (var table = ImRaii.Table("##emissiveTextures", 4,
                   ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            if (table)
                foreach (var t in preview.Textures)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    if (t.Import) ImGui.TextUnformatted(t.Label);
                    else ImGui.TextDisabled(t.Label);
                    if (t.Paths.Count > 1 && ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Format(es.PathsFmt, string.Join("\n", t.Paths)));

                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(t.Import ? string.Format(es.SizeFmt, t.Width, t.Height) : "");

                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(t.Paths.Count > 1
                        ? string.Format(es.AliasesFmt, t.Paths.Count)
                        : "");

                    // The glow percentage is the evidence that this is glow art rather than an ordinary
                    // texture, so it goes in the table where it is read, not behind a hover.
                    ImGui.TableNextColumn();
                    if (t.Import) ImGui.TextUnformatted(string.Format(es.GlowFmt, t.GlowFraction));
                    else ImGui.TextColored(ImportWarnColour, es.Skipped);

                    if (t.SkipReason != null && ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Format(es.SkippedReasonFmt, t.Label, t.SkipReason));
                }
        }

        // ── which body ──
        ImGui.Spacing();
        ImGui.SetNextItemWidth(200);
        var suffixes = LuminisImportService.BodySuffixes;
        var current = string.IsNullOrEmpty(_emissiveSuffix) ? (preview.DefaultSuffix ?? "") : _emissiveSuffix;
        if (ImGui.BeginCombo(es.BodyTarget, current))
        {
            foreach (var s in suffixes)
                if (ImGui.Selectable(s, string.Equals(s, current, StringComparison.OrdinalIgnoreCase)))
                {
                    _emissiveSuffix = s;
                    // Re-resolved on the CHANGE, not per frame: the catalogue rebuilds a list per call.
                    try
                    {
                        _emissiveMaterials = emissiveImport.MaterialsFor(preview, s);
                        _emissiveMaterialsFromGameData = emissiveImport.BodiesFromGameData;
                    }
                    catch { _emissiveMaterials = null; }
                }
            // A body the combo doesn't list — the wearer is on something exotic — is still the live choice
            // and has to be selectable, or picking anything else would be a one-way door.
            if (!suffixes.Contains(current, StringComparer.OrdinalIgnoreCase) && current.Length > 0)
                if (ImGui.Selectable(current, true)) _emissiveSuffix = current;
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(es.BodyTargetTip);

        var lead = preview.Importable.FirstOrDefault();
        if (lead is { FromWearer: false, Token: { } token })
            ImGui.TextDisabled(string.Format(es.BodyFromPackFmt, token));

        DrawEmissiveMaterials();

        ImGui.Spacing();
        using (ImRaii.Disabled(!TextureLoader.NativeEncoderAvailable))
            ImGui.Checkbox(ims.AsTex, ref _importAsTex);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TextureLoader.NativeEncoderAvailable ? ims.AsTexTip : ims.AsTexUnavailableTip);

        // Said before the button, in plain text rather than the warning colour: both of these are true of a
        // CORRECT import, and amber would put a caution under a green result line.
        if (preview.AnyImportable)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(es.MaterialsIgnored);
            ImGui.Spacing();
            ImGui.TextUnformatted(es.NoRaceFilter);
            ImGui.PopTextWrapPos();
        }

        foreach (var w in preview.Warnings)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ImportWarnColour, w);
            ImGui.PopTextWrapPos();
        }

        ImGui.Separator();

        bool valid = preview.AnyImportable && !string.IsNullOrWhiteSpace(_importName) && !_importBusy;
        using (ImRaii.Disabled(!valid))
            if (ImGui.Button(_importBusy ? ims.ImportBusy : ims.ImportBtn))
                StartEmissiveImport(preview);
        if (!valid && !_importBusy && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(preview.AnyImportable ? ims.NeedName : es.NothingUsable);

        DrawImportStatus();
    }

    /// <summary>The material paths the imported overlays will claim — the Luminis panel's list, over the
    /// other glow format.</summary>
    private void DrawEmissiveMaterials()
    {
        var mats = _emissiveMaterials;
        if (mats == null || mats.Count == 0) return;   // unresolved or unreadable — the import still works

        var ims = Strings.Import;

        // Outside the collapsing header: a fallback list is exactly what the user won't expand to check,
        // and it silently names no male body.
        if (!_emissiveMaterialsFromGameData)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ImportWarnColour, ims.FallbackBodies);
            ImGui.PopTextWrapPos();
        }

        if (!ImGui.CollapsingHeader(string.Format(ims.MaterialTargetsFmt, mats.Count) + "###emissiveMats"))
            return;

        ImGui.TextWrapped(_emissiveMaterialsFromGameData ? ims.MaterialsFromGame : ims.MaterialsFallbackNote);
        foreach (var p in mats)
        {
            ImGui.Bullet();
            ImGui.TextUnformatted(Path.GetFileName(p));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(p);
        }
    }

    /// <summary>Hand the decode and write to the pool; <see cref="TickEmissiveImport"/> registers what comes
    /// back.</summary>
    private void StartEmissiveImport(EmissiveSkinImportService.ImportPreview preview)
    {
        _importBusy = true;
        _importStatus = null;
        var (name, author, asTex, suffix) =
            (_importName, _importAuthor, _importAsTex, NullIfEmpty(_emissiveSuffix));
        Task.Run(() =>
        {
            try { _emissivePrepared = emissiveImport.Prepare(preview, name, author, asTex, suffix); }
            catch (Exception ex)
            {
                _emissivePrepared = new(false, string.Format(Strings.Import.ImportFailedFmt, ex.Message),
                    null, null, [], 0, 0);
            }
        });
    }

    /// <summary>
    /// Parse a picked <c>.zip</c> as a loose eye-texture pack, or report why it isn't one.
    /// <para/>
    /// Decodes one image — the mask, to measure what it marks as glowing. The other two are copied through
    /// on the write without ever being looked at, so this stays cheap enough for the picking frame.
    /// </summary>
    private void LoadEyePack(string path)
    {
        _importPath = path;
        _importStatus = null;
        _importPreview = null;    // see LoadOnionPack
        _contentPreview = null;
        _luminisPreview = null;
        _emissivePreview = null;
        _importMaterials = null;
        _luminisMaterials = null;
        _emissiveMaterials = null;
        try
        {
            var preview = eyeImport.Inspect(path);
            _eyePreview = preview;
            _importName = ProteusName(preview.Name);
            _importAuthor = "";
        }
        catch (Exception ex)
        {
            _eyePreview = null;
            _importStatus = string.Format(Strings.Eye.ReadFailedFmt, ex.Message);
            _importStatusOk = false;
        }
    }

    /// <summary>The eye-pack preview: which textures were recognised, and what will glow.</summary>
    private void DrawEyeImport(EyeImportService.ImportPreview preview)
    {
        var es = Strings.Eye;
        var ims = Strings.Import;

        ImGui.TextUnformatted(Path.GetFileName(_importPath));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(_importPath);

        ImGui.Spacing();
        ImGui.InputText(ims.ModName, ref _importName, 128);
        ImGui.InputText(ims.Author, ref _importAuthor, 128);

        ImGui.Separator();
        ImGui.TextUnformatted(string.Format(es.TextureCountFmt,
            preview.Files.Count(f => f.Import), preview.Files.Count));

        using (var table = ImRaii.Table("##eyeFiles", 3,
                   ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            if (table)
                foreach (var f in preview.Files)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    if (f.Import) ImGui.TextUnformatted(f.Name);
                    else ImGui.TextDisabled(f.Name);

                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(f.Slot?.ToString() ?? "");

                    ImGui.TableNextColumn();
                    if (f.Import) ImGui.TextDisabled(Path.GetFileName(f.GamePath ?? ""));
                    else ImGui.TextColored(ImportWarnColour, es.Skipped);

                    if (f.SkipReason != null && ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Format(es.SkippedReasonFmt, f.Name, f.SkipReason));
                }
        }

        // How much of the mask to cut to. A taste decision that is baked into the written art, so it has
        // to be made HERE rather than tuned afterwards — the Glow dial can only scale what survived.
        if (preview.Fractions != null)
        {
            ImGui.Spacing();
            ImGui.SetNextItemWidth(220);
            // Inert while a write is running — the choice is already snapshotted, so changing it here
            // would only misreport what is being baked.
            using var busy = ImRaii.Disabled(_importBusy);
            if (ImGui.BeginCombo(es.CutoutLabel, CutoutLabel(preview.Cutout, es)))
            {
                foreach (var mode in new[] { EyeImportService.EyeCutout.Falloff,
                                             EyeImportService.EyeCutout.Artwork })
                    if (ImGui.Selectable(CutoutLabel(mode, es), preview.Cutout == mode))
                        preview.Cutout = mode;
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(es.CutoutTip);
        }

        // What the glow will be, before the button — it is the whole reason this importer exists, and a
        // pack that can't have one should say so where the decision is made.
        ImGui.Spacing();
        ImGui.PushTextWrapPos(0);
        if (preview.CanGlow)
            ImGui.TextUnformatted(string.Format(es.GlowFmt, preview.GlowFraction ?? 0f,
                preview.IrisMaterials.Count, EyeImportService.GroupName));
        else
            ImGui.TextUnformatted(es.NoGlow);
        ImGui.PopTextWrapPos();

        if (!eyeImport.IrisesFromGameData && preview.IrisMaterials.Count > 0)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ImportWarnColour, es.FallbackIrises);
            ImGui.PopTextWrapPos();
        }

        foreach (var w in preview.Warnings)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ImportWarnColour, w);
            ImGui.PopTextWrapPos();
        }

        ImGui.Separator();

        bool valid = preview.AnyImportable && !string.IsNullOrWhiteSpace(_importName) && !_importBusy;
        using (ImRaii.Disabled(!valid))
            if (ImGui.Button(_importBusy ? ims.ImportBusy : ims.ImportBtn))
                StartEyeImport(preview);
        if (!valid && !_importBusy && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(preview.AnyImportable ? ims.NeedName : es.NothingUsable);

        DrawImportStatus();
    }

    private static string CutoutLabel(EyeImportService.EyeCutout mode, Localization.EyeStrings es)
        => mode == EyeImportService.EyeCutout.Artwork ? es.CutoutArtwork : es.CutoutFalloff;

    /// <summary>Hand the decode and write to the pool; <see cref="TickEyeImport"/> registers what comes
    /// back.</summary>
    private void StartEyeImport(EyeImportService.ImportPreview preview)
    {
        _importBusy = true;
        _importStatus = null;
        // Cutout snapshotted with the rest: it is a mutable field on a preview whose combo stays on
        // screen, and the write reads it on a pool thread.
        var (name, author, cutout) = (_importName, _importAuthor, preview.Cutout);
        Task.Run(() =>
        {
            try { _eyePrepared = eyeImport.Prepare(preview, name, author, cutout); }
            catch (Exception ex)
            {
                _eyePrepared = new(false, string.Format(Strings.Import.ImportFailedFmt, ex.Message),
                    null, null, false, 0, 0);
            }
        });
    }

    /// <summary>Hand the decode and write to the pool; <see cref="TickLuminisImport"/> registers what comes
    /// back.</summary>
    private void StartLuminisImport(LuminisImportService.ImportPreview preview)
    {
        _importBusy = true;
        _importStatus = null;
        var (name, author, asTex, suffix) =
            (_importName, _importAuthor, _importAsTex, NullIfEmpty(_luminisSuffix));
        Task.Run(() =>
        {
            try { _luminisPrepared = luminisImport.Prepare(preview, name, author, asTex, suffix); }
            catch (Exception ex)
            {
                _luminisPrepared = new(false, string.Format(Strings.Import.ImportFailedFmt, ex.Message),
                    null, null, [], 0, 0);
            }
        });
    }

    /// <summary>
    /// The colour editor for an imported content pack: one tab per selected option, each editing the rows
    /// stamped into the material that option's meshes are bound to.
    /// <para/>
    /// Separate from the overlay editors rather than folded into their tab strip, and the reason is what a
    /// content option IS. An overlay option owns art Proteus composites, so its panel is built around
    /// coverage, an index texture and a render mode inferred from the features in use. A content option
    /// owns none of those: the pack shipped its own mesh, its own textures and its own shader, and the only
    /// thing left for the user to change is the colour table. Everything else in that panel would be a
    /// control with nothing behind it.
    /// <para/>
    /// Rows that are NOT edited stay exactly as the author wrote them — see
    /// <c>GearMaterialWriter.PatchColorTable</c>, which writes only the rows it is given.
    /// </summary>
    private void DrawContentColorEditor(
        OverlayEntry entry, bool editingBinding,
        IReadOnlyList<(string Name, string Path, bool FromMod)> effects)
    {
        // Resolved HERE rather than through discovery.ResolveActiveContent, which is otherwise the same
        // walk: that one re-reads the mod's meta.json for its group order, and this runs once a frame for
        // as long as the panel is open. The selection still comes from Penumbra — one IPC call, exactly as
        // the option-group editor below does it — and the group order comes off the cache the tab strip
        // already keeps.
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

        // The synthesized piece group, read exactly as ResolveActiveContent reads it. Without this the panel
        // and the composite disagree about what is being worn: a pack whose pieces are all switched off
        // still contributes nothing, but would draw a full colour grid whose glow button targets a material
        // that was never published.
        var gateOn = entry.Metadata.PieceGroupName is { Length: > 0 } gateGroup ? Selection(gateGroup) : null;

        // Which options are live. Collapsed into MATERIALS below — see there.
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

        // Said before anything else, and in amber: a pack the composite refused is enabled, selected and
        // completely correct-looking, so "no active options" below would be answering a question nobody
        // asked. The reason comes from the shell builder, which records it even on a run that hosts nothing.
        if (compositor.GetUnwearableContentReason(entry.ModDirectory) is { } unwearable)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ImportWarnColour, unwearable);
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

        // Which of this mod's materials the last composite found backing a DRAWN mesh. A piece's Materials
        // map is every binding its models declare, which is the larger set: a material bound only to meshes
        // with no LOD0 vertices — the norm in a pack built by gutting a stock model — is declared and never
        // drawn. A tab for one of those would save rows that reach nothing and offer a glow button with no
        // target, which is the same silent-nothing this whole panel exists to stop.
        //
        // Drawn, not hosted: a piece that spilled past the host's material budget is on screen and still the
        // user's to colour. Null means "no information", NOT "nothing is live" — a pack that has not been
        // composited yet looks identical, and hiding every tab in that state would be worse than showing one
        // tab too many.
        var liveMaterials = compositor.GetLiveContentMaterials(entry.ModDirectory);

        // A stamp of what Penumbra currently has selected in this mod. Feeds the index-scan cache key so a
        // pack whose index texture is itself an option re-reads it the moment that option changes — the
        // selection can be changed from Penumbra's own window while this panel stays open, which no
        // window-appearing sweep can see.
        var selectionStamp = settings == null
            ? "-"
            : string.Join(' ', settings.Value.Options
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Key + "=" + string.Join(',', kv.Value)));

        // Collapse the options into MATERIALS. Pieces binding the same .mtrl are published as one material
        // and cost one of the host's ten slots between them (see SecondSkinService.ContentUnitKey), so one
        // grid governs all of them — and the edit fans out to every option it covers, because rows differing
        // is precisely what would split that one slot back into several.
        var byMaterial = new List<(string Mtrl, List<string> Names, List<ContentOwner> Owners)>();
        foreach (var (group, option, _, _) in options)
        {
            var live = PiecesFor(entry, group, option, gateOn);
            foreach (var piece in live)
                foreach (var (leaf, mtrl) in piece.Materials)
                {
                    if (liveMaterials != null && !liveMaterials.Contains(mtrl)) continue;

                    // The pack's OWN checkboxes, for a pack that switches pieces on by model attribute
                    // rather than by shipping separate models. A material nothing currently reveals belongs
                    // to a piece the user left unticked: showing it would be nine tabs for two worn
                    // accessories, none of them saying which is which.
                    //
                    // Only gates Penumbra still recognises get a vote. Group and option names are recorded
                    // at import and are exactly what someone renames when they edit a mod afterwards; a gate
                    // naming a group that no longer exists cannot be evaluated, and hiding a material on the
                    // strength of a test that could not run would empty the panel with no way to tell why.
                    // Unevaluable gates leave the material ungated, which shows the tab under its file name.
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
                        // Two ticked options sharing one material: name it after both, since the colours
                        // below reach both of them. Compared name by name — testing whether the JOINED label
                        // already contained the new one let "Gold Trim" swallow a genuinely separate "Gold".
                        foreach (var n in gateNames)
                            if (!byMaterial[at].Names.Contains(n, StringComparer.OrdinalIgnoreCase))
                                byMaterial[at].Names.Add(n);
                    }

                    // The pieces carried along are the ones that made this owner a user of THIS material — an
                    // option binding two materials contributes different pieces to each. They are what the
                    // caption names, since a piece knows the switch that turned it on and its option may not.
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

        // A pack with one material — the ordinary shape — gets no tab strip at all. A strip of one tab is
        // furniture around a single grid.
        if (byMaterial.Count == 1)
        {
            DrawContentMaterial(entry, byMaterial[0].Mtrl, byMaterial[0].Owners, editingBinding,
                selectionStamp, effects);
            return;
        }

        using var tabs = ImRaii.TabBar($"##contentTabs_{entry.ModDirectory}");
        if (!tabs) return;

        foreach (var (mtrl, names, owners) in byMaterial)
        {
            // Named after the options that reveal it, falling back to the file name for a material nothing
            // gates — "mt_c0801e5505_met_a" tells nobody which accessory it is.
            var label = names.Count > 0 ? string.Join(", ", names) : Path.GetFileNameWithoutExtension(mtrl);
            using var tab = ImRaii.TabItem($"{label}##content_{mtrl}");
            if (!tab) continue;
            DrawContentMaterial(entry, mtrl, owners, editingBinding, selectionStamp, effects);
        }
    }

    /// <summary>
    /// One content material's colour grid, governing every option that shares it.
    /// <para/>
    /// The edit fans out to all of them. They share a published material only while their rows AGREE — the
    /// merge key includes the rows — so writing to one and not the others would quietly spend an extra
    /// material slot and leave half the pieces the old colour.
    /// </summary>
    private void DrawContentMaterial(
        OverlayEntry entry, string mtrl, List<ContentOwner> owners, bool editingBinding, string selectionStamp,
        IReadOnlyList<(string Name, string Path, bool FromMod)> effects)
    {
        var (leadGroup, leadOption, _) = owners[0];

        // Named by the switch that turned each piece ON, which is not always its option — see ContentLabels.
        var worn = ContentLabels.For(
            owners.Select(o => (o.Option, o.Pieces)), Strings.Content.Unconditional);
        ProteusStyle.DisabledWrapped(string.Format(Strings.Content.SharedByFmt, string.Join(", ", worn)));

        // Which cell the pack's index texture actually samples. Said out loud as well as dimmed, because a
        // row filter narrows the grid to one row but cannot narrow it to one COLUMN, and picking the wrong
        // sub-row of the right row fails exactly as silently as picking the wrong row did.
        //
        // Every state gets a sentence, and the sentence always matches the grid. Two of these used to share
        // one branch: a material that could not be opened printed the "no index texture, row 16" line over a
        // grid with all sixteen rows live, asserting a fact about a file nothing had read and contradicting
        // itself in the same frame.
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
                // Amber, not dimmed: everything below this line is a control with nothing behind it, and
                // that is worth interrupting for.
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(ImportWarnColour, Strings.Content.NoColorTable);
                ImGui.PopTextWrapPos();
                break;

            case ContentIndexState.Scanned when idx.Rows is { Count: 1 } && idx.SubRow != null:
                ProteusStyle.DisabledWrapped(
                    string.Format(Strings.Content.SamplesFmt, idx.Rows.First(), idx.SubRow));
                break;

            case ContentIndexState.Scanned:
                // Several rows, or one row read across both columns. The grid is narrowed just as hard, so
                // it needs saying just as much — leaving it silent was a screen of greyed rows with the
                // reason available only to someone who thought to hover one.
                ProteusStyle.DisabledWrapped(string.Format(Strings.Content.SamplesRowsFmt,
                    string.Join(", ", idx.Rows!.OrderBy(r => r))));
                break;

            default:
                // Said out loud rather than left as an unfiltered grid. Silence here is what makes every row
                // look live, and colouring one that nothing reads is indistinguishable from the feature
                // failing. Wrapped: TextColored draws ONE line and lets the window clip it, and this is the
                // longest string on the panel — see the same trap called out in DrawColorEditor.
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(ImportWarnColour, Strings.Content.IndexUnreadable);
                ImGui.PopTextWrapPos();
                break;
        }

        // Where the rows live. Same rule as every other editor here: while a binding is being edited we work
        // on a COPY and only install it once something actually changes, so merely opening the panel never
        // creates an override — and the compositor is reading the real one from another thread meanwhile.
        var stored  = StoredContentRows(entry, leadGroup, leadOption, mtrl);
        var ovrRows = editingBinding
            ? designBindings.PeekOverrideRows(entry.ModDirectory, leadGroup, leadOption) : null;
        var rows = editingBinding ? DesignBindingService.CopyRows(ovrRows ?? stored) : stored;

        // The glow, on the same copy-while-binding rule as the rows above.
        var storedGlow = StoredContentGlow(entry, leadGroup, leadOption, mtrl);
        var ovrGlow = editingBinding
            ? designBindings.PeekContentGearOverride(entry.ModDirectory, leadGroup, leadOption) : null;
        var glow = (ovrGlow ?? storedGlow).Clone();
        bool glowing = glow.GlowKey() != null;

        // The glow button targets the published material, which every owner maps to — the same one — so this
        // is a union of duplicates and comes out as a single entry.
        var targets = owners
            .SelectMany(o => compositor.GetShellMaterials(entry.ModDirectory, o.Group, o.Option) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool changed = false;
        var selKey = entry.ModDirectory + "|" + mtrl;
        int sel = _rowSelection.GetValueOrDefault(selKey, 0);        // 0 = never chosen; DrawRows lands it
        ColorTableEditor.DrawRows(selKey, rows, idx.Rows,
            // A content material is gear-space by construction — it hangs off an accessory. Its shader is
            // the PACK'S and nothing here infers one, with a single exception: an animated glow rebuilds the
            // material onto characterscroll, and the grid has to agree — the row emissive is what ARMS the
            // effect, so it must be reachable, while sphere and metal do nothing on that shader.
            gear: true,
            shader: glowing ? RenderModeInference.GlowShader : OverlayDescriptor.DefaultGearShader,
            targets,
            skinGlowTargets: null,
            out _, ref sel, ref changed,
            // The roughness and metalness in these rows are the PACK author's, grafted through from their
            // own material — unlike a shell's, which come from a neutral template. Animated glow hides that
            // block by default; here it must not, or a value that is in the material (the piercings carry
            // metalness 1.0) has no control at all.
            authoredPhysical: true,
            // And the grid shows what the PACK'S material holds for anything the sidecar hasn't overridden,
            // rather than this editor's neutral defaults — see DrawSubRow.
            physicalBaseline: idx.Physical,
            // A light response can't reach here. It is applied by re-asserting a shell's colour table onto
            // the live material every frame, and a content material is the pack's own, published verbatim
            // and never touched at runtime — so the controls would save a value that does nothing. Shown on
            // Proteus's own shells only, which is where the compositor publishes a profile for.
            lightResponseApplies: false,
            // The column its index actually lands in, which this editor has always KNOWN and until now could
            // only mention in prose — SamplesFmt ends "editing the other column of this row will do nothing
            // either", over two columns drawn identically. Now the dead one is dimmed like a dead row.
            usedSubRow: idx.SubRow);
        _rowSelection[selKey] = sel;

        // ── animated glow ────────────────────────────────────────────────────
        // Hidden when there is no colour table: the effect is armed by a row's emissive, so a material with
        // no rows has no way to switch it on and the picker would be a control with nothing behind it.
        bool glowChanged = false;
        if (idx.State != ContentIndexState.NoColorTable)
        {
            ImGui.Separator();
            // Said BEFORE the picker, not after the fact: characterscroll has no base-texture slot, so a
            // pack that paints its surface loses that painting the moment this is switched on.
            if (idx.HasDiffuse)
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(ImportWarnColour, Strings.Content.GlowDropsDiffuse);
                ImGui.PopTextWrapPos();
            }
            glowChanged = ColorTableEditor.DrawContentGlowFooter(selKey, effects, glow);

            // Picking an effect must be enough to see one, and clearing it must leave nothing behind.
            //
            // The row's Glow is what arms the shader — GearMaterialWriter sets the effect-enable field only
            // on a row that has one — so without arming, a material whose rows are all zero renders exactly
            // as it did before, with every other part of the setup correct. And without DISARMING, that same
            // value stays in the rows when the effect goes away: on the pack's own character.shpk it is an
            // ordinary emissive, so the piece keeps glowing with the animation switched off.
            if (glowChanged)
            {
                var (armRow, armA) = SampledCell(idx, sel);
                bool wrote = glow.GlowKey() != null
                    ? ContentGlowRow.Arm(rows, armRow, armA)
                    : ContentGlowRow.Disarm(rows, armRow, armA);
                if (wrote) changed = true;
            }

            // And the standing case, for a cell whose Glow is cleared later — or set on the wrong row.
            // Names the cell, because "raise Glow somewhere" is exactly the advice that let this go wrong:
            // the row an older colour edit happened to leave behind is not the row the material reads.
            if (glow.GlowKey() != null && !SampledCellEmits(rows, idx, sel))
            {
                var (needRow, needA) = SampledCell(idx, sel);
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(ImportWarnColour,
                    string.Format(Strings.Content.GlowNeedsEmissiveFmt, needRow, needA ? "A" : "B"));
                ImGui.PopTextWrapPos();
            }
        }

        if (!changed && !glowChanged) return;

        // Written once, against the MATERIAL this tab governs — not fanned out across the options that use
        // it. Fanning out was what made a pack with one always-on piece share one set of settings between
        // every tab, and it is no longer needed for the reason it existed: a per-material edit cannot make
        // two options disagree about a material they share, because there is only one place to disagree.
        if (!editingBinding)
        {
            if (changed) StoreContentRows(entry, mtrl, DesignBindingService.CopyRows(rows));
            if (glowChanged) StoreContentGlow(entry, mtrl, glow.Clone());
            discovery.SaveMetadata(entry);
            InvalidateDefaultsCache(entry);
            // A content piece is grafted geometry with its own material; it never touches a skin texture,
            // which is why the fingerprint's whole `content:` block is dropped under skinOnly. So the skin
            // fingerprint is authoritative here by construction, not just by what happens to be hashed.
            RecompositeForOverlay(entry, "content-colors-change", ColorEditDebounceMs,
                skinFingerprintAuthoritative: true);
            return;
        }

        // A design binding still keys on the option, which is the shape its override was built around. Every
        // owner gets its own COPY: one list shared between options would serialise identically but ALIAS in
        // memory, so a later edit to one would silently move the others.
        foreach (var (group, option, _) in owners)
        {
            if (changed)
                designBindings.SetOverrideRows(
                    entry.ModDirectory, group, option, DesignBindingService.CopyRows(rows));
            if (glowChanged)
                designBindings.GetEditableContentGearOverride(entry.ModDirectory, group, option, glow)
                    ?.ApplyScrollFrom(glow);
        }

        RecompositeForOverlay(entry, "content-colors-change", ColorEditDebounceMs,
            skinFingerprintAuthoritative: true);   // see above
    }

    /// <summary>Thin wrappers onto <see cref="ContentGlowRow"/>, which owns the rule — see there for why it
    /// is not written inline. These only unpack the index scan into the shape it takes.</summary>
    private static (int Row, bool SubRowA) SampledCell(ContentIndex idx, int selectedRow)
        => ContentGlowRow.Sampled(idx.Rows, idx.SubRow, selectedRow);

    private static bool SampledCellEmits(List<ColorTableRowPreset> rows, ContentIndex idx, int selectedRow)
    {
        var (row, subRowA) = SampledCell(idx, selectedRow);
        return ContentGlowRow.Emits(rows, row, subRowA);
    }

    /// <summary>
    /// The animated glow an option's material carries, as stored in the sidecar — or a fresh empty preset
    /// when none is set, so the picker has somewhere to put a first choice.
    /// <para/>
    /// Deliberately does NOT install that empty preset, on the same rule as <see cref="StoredContentRows"/>:
    /// drawing a panel must not change the mod.
    /// </summary>
    /// <remarks>Keyed by MATERIAL — see <see cref="StoredContentRows"/> for why.</remarks>
    private static GearSettingsPreset StoredContentGlow(
        OverlayEntry entry, string? group, string? option, string materialRel)
        => entry.Metadata.PeekMaterialSettings(materialRel)?.Glow
        ?? ContentOptionFor(entry, group, option)?.Glow
        ?? entry.Metadata.ContentGlow
        ?? new GearSettingsPreset();

    private static void StoreContentGlow(OverlayEntry entry, string materialRel, GearSettingsPreset glow)
    {
        // Stored even when it names no effect, and the entry is never dropped. A cleared glow IS a decision:
        // deleting it instead would fall back through to the older per-option glow, so clearing the effect
        // would make the pack's previous one reappear on the next composite. A preset with no glow key
        // publishes the material verbatim, which is what clearing is supposed to mean.
        entry.Metadata.MaterialSettings(materialRel).Glow = glow;
    }

    /// <summary>
    /// How much the panel actually knows about which colour-table cell a content material samples.
    /// <para/>
    /// Five states, not a bool, because several of them used to be indistinguishable and the panel said the
    /// wrong thing about all of them. "The material declares no index texture" and "Proteus could not read
    /// the material" are opposite facts — the first justifies pinning the grid to one row, the second
    /// justifies nothing at all — and collapsing them let the caption assert a row it had never looked for
    /// while the grid underneath it stayed unfiltered, contradicting the sentence above it in the same frame.
    /// </summary>
    private enum ContentIndexState
    {
        /// <summary>Nothing could be established: the material is missing, unparseable, or its index texture
        /// could not be found or decoded. Filter nothing and SAY so.</summary>
        Unknown,
        /// <summary>The material parsed cleanly and declares no <c>_id</c> sampler.</summary>
        NoSampler,
        /// <summary>The material parsed cleanly and carries no colour table at all, so there are no rows to
        /// select and nothing the grid writes will survive. Its own state: claiming a row here would be as
        /// baseless as claiming one for a file that was never opened.</summary>
        NoColorTable,
        /// <summary>The index texture was read and every texel is fully transparent, so it selects no row at
        /// all. Read fine — a different fact from Unknown, and a different message.</summary>
        SelectsNothing,
        /// <summary>
        /// Read, and it names a cell — but the texture is stored compressed, so the reading may be a row or
        /// two out. Filter on it anyway and say the caveat.
        /// <para/>
        /// Its red and green are row SELECTORS rather than colour, and a lossy codec can move a value across
        /// a bucket boundary. Refusing to read one at all was the first attempt and it was too blunt: a flat
        /// index decodes exactly (one pack's BC7 came back as a clean uniform value on every texel), so that
        /// threw away a filter that worked. Since the rows it does not name are dimmed and still clickable,
        /// being wrong here costs a moment rather than access to the row that renders.
        /// </summary>
        Compressed,
        /// <summary>The index texture was read and names rows.</summary>
        Scanned,
    }

    /// <summary>
    /// Which colour-table cell a content material actually samples.
    /// <para/>
    /// <paramref name="Rows"/> is 1-based and feeds the grid's availability filter; <paramref name="SubRow"/>
    /// is "A" or "B" when the index texture is uniform enough to name one. Null Rows = don't filter, which
    /// is the only honest answer in every state but the two that establish a row set.
    /// <para/>
    /// <paramref name="HasDiffuse"/> rides along because the same parse answers it and the panel needs it for
    /// the glow warning — characterscroll has no base-texture slot, so a pack that paints its surface loses
    /// that painting when the glow is switched on. Reading the material twice for one bool would be waste.
    /// </summary>
    private readonly record struct ContentIndex(
        HashSet<int>? Rows, string? SubRow, ContentIndexState State, bool HasDiffuse = false,
        /// <summary>The roughness and metalness the pack's material already holds, per sub-row (0–31), so
        /// the grid can show those instead of its own neutral defaults. Null when it has no colour table.
        /// From the same parse as the rest of this.</summary>
        IReadOnlyList<(float Roughness, float Metalness)>? Physical = null);

    /// <summary>
    /// The colour-table row a material with NO index texture falls back to.
    /// <para/>
    /// The <c>_id</c> sampler is what picks a row per pixel; with none bound there is nothing to pick with
    /// and the shader takes the last one. Proteus already leans on this elsewhere — a normal-only overlay
    /// synthesizes its tint from Row 16 for the same reason.
    /// <para/>
    /// The same row a SHELL without an <c>_id</c> lands on, by a different route: there SecondSkinService
    /// fabricates the index rather than binding none, and picks (255, 255, 0) to hit this row deliberately.
    /// One constant for both, so the two can't be reasoned about separately and drift.
    /// </summary>
    private const int DefaultContentRow = GlowShell.Row;

    /// <summary>
    /// Read a content material's index texture and work out which colour rows it selects, so the grid can
    /// dim the fifteen that do nothing.
    /// <para/>
    /// This is not cosmetic. Without it every row renders as live, and colouring the wrong one looks exactly
    /// like the feature being broken: the piece does not change, and the glow highlight — which drives the
    /// same row — does nothing either. A pack whose index points at row 1 is easy to spend an afternoon on.
    /// <para/>
    /// Cached per material path: this runs once a frame while the panel is open, and it costs a Penumbra
    /// resolve plus a texture decode. The cache key carries <paramref name="selectionStamp"/> — the mod's
    /// live Penumbra selection — because a pack whose index texture IS an option reads a different file the
    /// moment that selection changes, and Penumbra's own window can change it while this panel is open.
    /// </summary>
    private ContentIndex ContentIndexFor(OverlayEntry entry, string mtrlRel, string selectionStamp)
    {
        var prefix = entry.ModDirectory + "|" + mtrlRel + "|";
        var cacheKey = prefix + selectionStamp;
        if (_contentIndexCache.TryGetValue(cacheKey, out var hit)) return hit;

        // A miss means this material's answer under the OLD selection is now dead weight, so drop it before
        // adding the new one — one entry per material rather than one per selection ever tried.
        //
        // The stamp stays the whole mod's selection, deliberately. Narrowing it to the options that can
        // redirect THIS material's index would need the material parsed to know which texture it names,
        // which is the work being cached; and a stale row filter is worse than a re-decode, because it dims
        // the row that actually renders. So the cost of a checkbox is still one decode per material shown —
        // what this stops is those decodes' results piling up, one set per combination, until the window
        // happens to reappear.
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
                    // Logged, not swallowed: this is the case that used to print "ships no index texture,
                    // takes row 16" about a file nothing had opened.
                    Plugin.Log.Warning("[Proteus] content: {0} names material {1}, which is not in the mod "
                                     + "folder — its colour rows cannot be narrowed", entry.ModDirectory, mtrlRel);
                else
                {
                    // The material names its index by GAME path, so Penumbra has to say which file that is
                    // right now — the pack may well be redirecting it from one of its own options.
                    var mtrlBytes = File.ReadAllBytes(mtrlPath);
                    var slots = TextureLoader.ParseMtrlBytes(mtrlBytes);
                    if (!slots.Parsed)
                        // The parser is fail-open, so "no index" and "could not walk this file" arrive as the
                        // same null. Only Parsed separates them, and getting it wrong is expensive: a wrongly
                        // claimed row filter DISABLES the fifteen others, putting the working row out of reach.
                        Plugin.Log.Warning("[Proteus] content: could not read material {0} of {1} — its colour "
                                         + "rows cannot be narrowed", mtrlRel, entry.ModDirectory);
                    else if (!slots.HasColorTable)
                        // No rows exist, so no row can be claimed — and PatchColorTable will discard
                        // whatever the grid writes. Said, not filtered.
                        result = new ContentIndex(null, null, ContentIndexState.NoColorTable);
                    else if (string.IsNullOrEmpty(slots.Index))
                        result = new ContentIndex([DefaultContentRow], "A", ContentIndexState.NoSampler);
                    else
                    {
                        var disk = ResolveContentTexture(entry, modRoot, slots.Index);
                        if (disk != null && textureLoader.LoadTexAsRgba(disk) is { } tex)
                        {
                            result = ScanContentIndex(tex.rgba);
                            // A compressed index still gets read and still narrows the grid — refusing to
                            // read one at all threw away a perfectly good answer, since a flat index decodes
                            // exactly. It is only flagged, because the values are row SELECTORS and a lossy
                            // codec can move one across a bucket boundary. The rows it doesn't name are
                            // dimmed rather than disabled, so being wrong here costs a moment, not access.
                            if (result.State == ContentIndexState.Scanned
                                && TextureLoader.IsUncompressed(disk) == false)
                                result = result with { State = ContentIndexState.Compressed };
                        }
                        else
                            Plugin.Log.Warning("[Proteus] content: {0} names index texture {1}, which could not "
                                             + "be resolved or decoded", entry.ModDirectory, slots.Index);
                    }

                    // From the SAME parse — see ContentIndex.HasDiffuse. Recorded even on the branches that
                    // learned nothing about the index, because the glow warning is a separate question and a
                    // material whose index is unreadable can still have a base texture to lose. The physical
                    // values ride along for the same reason: the grid needs them whatever the index said.
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
    /// The file behind a texture a content material names, as a disk path.
    /// <para/>
    /// The answer must come from INSIDE the pack. A content material is the pack's own and its textures are
    /// the pack's own, so a resolution landing anywhere else is answering a different question — and it
    /// answered one here: this pack asks for <c>chara/neolithe/neolithe_piercings_index.tex</c>, a namespace
    /// its author invented, and the live resolve came back with something whose red channel read as row 16
    /// while the pack's actual file selects row 1. The grid then dimmed every row but 16, so the one row the
    /// material reads could not be edited at all.
    /// <para/>
    /// Penumbra is still asked first, because it is the only thing that knows which of the pack's options is
    /// selected — a pack whose index texture IS an option (a colour picker) ships several under one name and
    /// only the live one is right. Its answer is simply required to be a file within this mod's folder.
    /// <para/>
    /// Otherwise: the pack's own folder, matched on file name. Ambiguous for the colour-picker case, but a
    /// filter derived from a sibling beats none — without one every row reads as live and colouring the
    /// wrong one looks exactly like a broken feature.
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

    /// <summary>The scan as the panel needs it — a texture whose texels are all fully transparent selects
    /// nothing, so it filters nothing rather than narrowing the grid to an empty set. That is its OWN state:
    /// the texture was read perfectly, and reporting it as unreadable would send the user looking for a
    /// problem in the wrong file.</summary>
    private static ContentIndex ScanContentIndex(byte[] rgba)
    {
        var scan = ContentIndexTexture.Read(rgba);
        return scan.Rows.Count == 0
            ? new ContentIndex(null, null, ContentIndexState.SelectsNothing)
            : new ContentIndex(scan.Rows, scan.SubRow, ContentIndexState.Scanned);
    }

    /// <summary>
    /// One option's stake in a shared content material: which option it is, so an edit can be written back
    /// to it, and which of its pieces are drawn with that material, so the panel can say what it governs.
    /// <para/>
    /// <paramref name="Option"/> is null for a piece belonging to no option of the pack's own — those are
    /// gated through the synthesized piece group instead, and the piece carries that gate.
    /// </summary>
    private readonly record struct ContentOwner(
        string? Group, string? Option, IReadOnlyList<ContentPiece> Pieces);

    /// <summary>
    /// The sidecar pieces behind one live option — or the unconditional ones when it has no option — with
    /// anything the piece group is holding switched off removed.
    /// <para/>
    /// The gate filter is not optional decoration: it is the same rule
    /// <see cref="SidecarDiscoveryService.ResolveActiveContent"/> applies, and the panel exists to show what
    /// is actually being worn. Skipping it would offer a colour grid for a piece the composite is not
    /// publishing, with a glow button pointing at a material that does not exist.
    /// </summary>
    private static IReadOnlyList<ContentPiece> PiecesFor(
        OverlayEntry entry, string? group, string? option, IReadOnlyList<string>? gateOn)
    {
        var pieces = group == null || option == null
            ? entry.Metadata.Content ?? []
            : ContentOptionFor(entry, group, option)?.Pieces ?? [];
        return [.. pieces.Where(p => SidecarDiscoveryService.PieceIsOn(p, gateOn))];
    }

    /// <summary>
    /// The rows an option's material is stamped with, as stored in the sidecar — or a fresh empty list when
    /// the author wrote none, so the editor has somewhere to put a first edit.
    /// <para/>
    /// Deliberately does NOT install that empty list. Drawing a panel must not change the mod: the list is
    /// only written back by <see cref="StoreContentRows"/>, and only once something actually changed.
    /// </summary>
    /// <remarks>
    /// Keyed by MATERIAL, because that is what one of these tabs governs. The per-option values are still
    /// read as a fallback so packs edited before this keep their colours, but nothing writes there any more:
    /// a pack holding nine accessories in one always-on model has ONE option and nine materials, so
    /// per-option storage made every tab read and write the same settings — a glow set on the ear rings and
    /// the shin laces were already glowing.
    /// </remarks>
    private static List<ColorTableRowPreset> StoredContentRows(
        OverlayEntry entry, string? group, string? option, string materialRel)
        => entry.Metadata.PeekMaterialSettings(materialRel)?.ColorTableRows
        ?? ContentOptionFor(entry, group, option)?.ColorTableRows
        ?? entry.Metadata.ColorTableRows
        ?? [];

    private static void StoreContentRows(OverlayEntry entry, string materialRel, List<ColorTableRowPreset> rows)
        => entry.Metadata.MaterialSettings(materialRel).ColorTableRows = rows;

    /// <summary>The sidecar's content option for a (group, option) pair, or null for an unconditional piece
    /// — or for a name pair the sidecar no longer carries.</summary>
    private static ContentOption? ContentOptionFor(OverlayEntry entry, string? group, string? option)
    {
        if (group == null || option == null) return null;
        return entry.Metadata.ContentGroups?
            .FirstOrDefault(g => string.Equals(g.PenumbraGroupName, group, StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(o => string.Equals(o.Name, option, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One row per pack layer: what it is, and — dimmed — why it won't be imported.</summary>
    private static void DrawImportLayers(OnionImportService.ImportPreview preview)
    {
        var ims = Strings.Import;
        var kept = preview.Layers.Count(l => l.Import);
        ImGui.TextUnformatted(string.Format(ims.LayerCountFmt, kept, preview.Layers.Count));

        using var table = ImRaii.Table("##onionLayers", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg);
        if (!table) return;   // EndTable is only legal when BeginTable returned true

        foreach (var l in preview.Layers)
        {
            ImGui.TableNextRow();
            using var dim = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (l.Import ? 1f : 0.5f));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(l.LayoutToken.Length == 0 ? ims.NoLayout : l.LayoutToken);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(l.Slot ?? (l.MapToken.Length == 0 ? ims.NoMap : l.MapToken));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(l.Opacity < 0.999f ? $"{l.ModeToken}  {l.Opacity:0.##}" : l.ModeToken);
            ImGui.TableNextColumn();
            if (l.Import)
                ImGui.TextDisabled($"{l.Bytes / 1024f / 1024f:0.#} MB");
            else
                ImGui.TextColored(ImportWarnColour, ims.Skipped);

            // Hovering the last column (the size / "skipped" tag) explains the layer. The rows are only
            // DIMMED, never ImGui-disabled, so a plain hover test reaches a skipped one too — and the skip
            // reason is a sentence, far too long to sit in a column of its own.
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(l.SkipReason == null
                    ? string.Format(ims.LayerImportedFmt, l.File, l.Slot, l.BodyType)
                    : string.Format(ims.LayerSkippedFmt, l.File, l.SkipReason));
        }
    }

    /// <summary>
    /// What happens when the pack has nothing painted for the body the user is actually wearing.
    /// <para/>
    /// Drawn for EVERY pack, not just multi-layout ones: a single-layout bibo pack landing on a vanilla
    /// body is the case that most needs saying, and it has no option group to hang the note off. And the
    /// three outcomes genuinely differ — bibo↔gen3 is remapped with no action, gen2 needs the mod's
    /// sibling mode raised, and an undrawn character means Proteus simply doesn't know yet. Saying
    /// "Proteus will remap it" for all three would be wrong for two of them.
    /// </summary>
    private static void DrawImportBodyFit(OnionImportService.ImportPreview preview)
    {
        if (preview.DefaultLayout == null || preview.DefaultLayoutMatchedBody) return;

        var ims = Strings.Import;

        ImGui.Spacing();
        if (preview.WearerBodyType == null)
        {
            ImGui.TextDisabled(string.Format(ims.NotDrawnFmt, preview.DefaultLayout));
        }
        else if (preview.NeedsAllBodies)
        {
            // The one case that needs an action, and the one Proteus takes for the user on import.
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ImportWarnColour, string.Format(ims.NeedsAllBodiesFmt, preview.DefaultLayout));
            ImGui.PopTextWrapPos();
        }
        else
        {
            ImGui.TextDisabled(string.Format(ims.RemappedFmt, preview.WearerBodyType, preview.DefaultLayout));
        }
    }

    /// <summary>The material paths the imported overlays will claim, collapsed by default.</summary>
    private void DrawImportMaterials()
    {
        var mats = _importMaterials;
        if (mats == null || mats.Count == 0) return;   // unresolved or unreadable — the import still works

        // Outside the collapsing header: a fallback list is exactly what the user won't expand to check,
        // and it silently names no male body.
        var ims = Strings.Import;

        if (!_importMaterialsFromGameData)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ImportWarnColour, ims.FallbackBodies);
            ImGui.PopTextWrapPos();
        }

        var total = mats.Values.Sum(v => v.Count);
        if (!ImGui.CollapsingHeader(string.Format(ims.MaterialTargetsFmt, total) + "###onionMats")) return;

        ImGui.TextWrapped(_importMaterialsFromGameData ? ims.MaterialsFromGame : ims.MaterialsFallbackNote);
        foreach (var (layout, paths) in mats)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(string.Format(ims.LayoutGroupFmt, layout, paths.Count));
            foreach (var p in paths)
            {
                ImGui.Bullet();
                ImGui.TextUnformatted(Path.GetFileName(p));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(p);
            }
        }
    }

    // ── Export tab ───────────────────────────────────────────────────────────

    private void DrawExportTab()
    {
        // The pool task parked its result; adopt it. Done here rather than pumped from Plugin.DrawUi (as
        // the import is) because nothing is left to do afterwards — no Penumbra registration, nothing that
        // can be stranded by the user closing the window. The file is already on disk either way.
        if (_exportDone is { } done)
        {
            _exportDone = null;
            _exportPhase = ExportPhase.Idle;
            _exportStatus = done.Message;
            _exportStatusOk = done.Ok;
        }

        var xs = Strings.Export;

        ImGui.TextWrapped(xs.Intro);
        ImGui.Separator();

        var mods = compositor.LastDiscovered;
        if (!config.PluginEnabled)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), Strings.ModsList.Disabled);
            return;
        }
        if (mods.Count == 0)
        {
            ImGui.TextDisabled(Strings.ModsList.NoMods);
            return;
        }

        // Selection is held by directory, so a re-discovery that reorders or drops mods can't leave the
        // combo pointing at a different one than it shows. An unknown/stale directory falls back to the
        // first mod rather than exporting nothing.
        var selected = mods.FirstOrDefault(m =>
            string.Equals(m.ModDirectory, _exportModDir, StringComparison.OrdinalIgnoreCase)) ?? mods[0];
        _exportModDir = selected.ModDirectory;

        ImGui.SetNextItemWidth(360);
        // The height cap has to be explicit: BeginCombo applies its own row limit ONLY when the caller
        // supplied no size constraint, so passing a width silently disables it and the popup would grow one
        // row per mod, past the window on a big collection. Same trap as the Create tab's material picker.
        var popupMaxH = ImGui.GetTextLineHeightWithSpacing() * 18 + ImGui.GetStyle().WindowPadding.Y * 2;
        ImGui.SetNextWindowSizeConstraints(new Vector2(360, 0), new Vector2(780, popupMaxH));
        if (ImGui.BeginCombo(xs.ModCombo, selected.ModName))
        {
            // Fresh filter each open, with the caret already in the box so the list can just be typed at.
            // SetKeyboardFocusHere targets the NEXT item submitted, so it has to sit immediately before it.
            bool appearing = ImGui.IsWindowAppearing();
            if (appearing) _exportFilter = "";
            ImGui.SetNextItemWidth(-1);
            if (appearing) ImGui.SetKeyboardFocusHere();
            ImGui.InputTextWithHint("##exportfilter", xs.FilterHint, ref _exportFilter, 64);
            ImGui.Separator();

            int shown = 0;
            foreach (var m in mods.OrderBy(m => m.ModName, StringComparer.OrdinalIgnoreCase))
            {
                if (!MatchesExportFilter(m)) continue;
                shown++;
                // ##dir: two mods can share a display name, and duplicate ImGui ids would route the click
                // to the wrong row.
                if (ImGui.Selectable($"{m.ModName}##{m.ModDirectory}",
                        string.Equals(m.ModDirectory, _exportModDir, StringComparison.OrdinalIgnoreCase)))
                    _exportModDir = m.ModDirectory;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(m.Enabled
                        ? m.ModDirectory
                        : string.Format(xs.DisabledInPenumbraFmt, m.ModDirectory));
            }
            if (shown == 0)
                ImGui.TextDisabled(string.Format(xs.NoMatchFmt, _exportFilter));
            ImGui.EndCombo();
        }
        if (!selected.Enabled)
            ImGui.TextDisabled(xs.ModDisabledNote);

        ImGui.Spacing();
        var label = _exportPhase switch
        {
            ExportPhase.Choosing => xs.Choosing,
            ExportPhase.Writing  => xs.Exporting,
            _                    => xs.ExportBtn,
        };
        using (ImRaii.Disabled(_exportPhase != ExportPhase.Idle))
            if (ImGui.Button(label))
                BrowseForExport(selected);

        if (_exportStatus != null)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(
                _exportStatusOk ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(1f, 0.5f, 0.4f, 1f),
                _exportStatus);
            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>
    /// Does this mod survive the export combo's filter? Matches the display name AND the folder name:
    /// they routinely differ (Penumbra's folder is a sanitised form of the name, and the user can rename
    /// either), so filtering on the label alone hides mods the user is searching for by folder.
    /// <para/>
    /// Null-tolerant even though both fields are declared non-nullable: everything else that touches them
    /// interpolates them into a string, where a null from Penumbra prints empty — this is the one place it
    /// would throw, and a throw inside Draw takes the whole window down for the rest of the session.
    /// </summary>
    private bool MatchesExportFilter(OverlayEntry m)
        => _exportFilter.Length == 0
        || m.ModName?.Contains(_exportFilter, StringComparison.OrdinalIgnoreCase) == true
        || m.ModDirectory?.Contains(_exportFilter, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Ask where to save, then zip on the pool. The dialog callback runs on the framework thread, inline
    /// from <c>_fileDialog.Draw()</c>, so everything it touches here is single-threaded with the draw.
    /// </summary>
    private void BrowseForExport(OverlayEntry entry)
    {
        _exportStatus = null;
        // Claimed BEFORE the dialog opens, so a second click during the seconds the browser is up finds the
        // button already disabled. Released again on cancel, below.
        _exportPhase = ExportPhase.Choosing;

        _fileDialog.SaveFileDialog(
            Strings.Export.DialogTitle, Strings.Export.DialogFilter + "{.pmp}",
            ModExportService.SuggestedFileName(entry), ModExportService.Extension,
            (ok, path) =>
            {
                if (!ok || string.IsNullOrEmpty(path))
                {
                    _exportPhase = ExportPhase.Idle;   // cancelled — hand the button back
                    return;
                }

                // Remember where they put it BEFORE the write: the directory is what the next dialog wants,
                // and it is just as useful after a failed write as after a successful one.
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !string.Equals(dir, config.LastExportDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    config.LastExportDirectory = dir;
                    config.Save();
                }

                _exportPhase = ExportPhase.Writing;
                Task.Run(() =>
                {
                    try { _exportDone = modExport.Export(entry, path); }
                    catch (Exception ex) { _exportDone = new ModExportService.ExportResult(false, $"Export failed: {ex.Message}"); }
                });
            },
            ExportStartDirectory());
    }

    /// <summary>
    /// Where the save dialog opens: last used, else the desktop. Null hands the choice back to the dialog,
    /// which reuses whatever path it was last in — better than forcing it somewhere arbitrary.
    /// </summary>
    private string? ExportStartDirectory()
    {
        var last = config.LastExportDirectory;
        if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;

        // DesktopDirectory is the physical path and follows a OneDrive-redirected desktop; Desktop is the
        // virtual shell folder and can come back empty on some setups, so it's only the fallback.
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Desktop })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
        }
        return null;
    }

    private void DrawImportStatus()
    {
        if (_importStatus == null) return;
        var colour = !_importStatusOk ? new Vector4(1f, 0.5f, 0.4f, 1f)       // failed
                   : _importStatusWarn ? ImportWarnColour                      // worked, but act on it
                   : new Vector4(0.4f, 0.9f, 0.4f, 1f);                        // clean
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(colour, _importStatus);
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// Is this a material an overlay would paint skin onto — the player's body or face?
    /// <para/>
    /// Deliberately NOT <see cref="CompositorService.IsBodyUvMaterial"/>, which tests for <c>/obj/body/</c>
    /// anywhere in the path. Weapons carry that segment too —
    /// <c>chara/weapon/w0801/obj/body/b0006/material/v0001/mt_w0801b0006_a.mtrl</c> is a real path off a
    /// live character — so reusing it would file the equipped greatsword under "Skin". Anchoring on the
    /// <c>chara/human/</c> prefix is what keeps this to actual character skin.
    /// </summary>
    private static bool IsSkinMaterial(string p)
        => p.StartsWith("chara/human/", StringComparison.OrdinalIgnoreCase)
        && (p.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase)
         || p.Contains("/obj/face/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The face specifically — the only surface with a split-left/right art layout to offer.</summary>
    private static bool IsFaceMaterial(string p)
        => p.StartsWith("chara/human/", StringComparison.OrdinalIgnoreCase)
        && p.Contains("/obj/face/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Short "what is this" tag for a material path, the picker's left column. Path-derived only —
    /// no game data, so it's free to call while drawing.</summary>
    private static string SlotHint(string p)
    {
        // The skin surfaces come from the shared taxonomy, so the tag the user reads here and the surface
        // the shell builder actually cuts from can never disagree. It handles the weapon trap itself:
        // chara/weapon/w0801/obj/body/b0006/... carries an /obj/body/ segment of its own, so a body-first
        // test would file an equipped greatsword as "Body" — and it sorts into the skin group, putting a
        // "Body" row above the Skin separator.
        if (ShellSurface.KeyFor(p) is { } surface) return ShellSurface.Label(surface.Kind);
        if (p.Contains("chara/weapon/", StringComparison.OrdinalIgnoreCase)) return "Weapon";
        if (p.Contains("chara/equipment/", StringComparison.OrdinalIgnoreCase)) return "Gear";
        if (p.Contains("chara/accessory/", StringComparison.OrdinalIgnoreCase)) return "Accessory";
        return "Other";
    }

    /// <summary>Picker row text: the slot tag plus the file name — the tail is what distinguishes two
    /// materials, and the full path (60–100 chars) goes in the row's tooltip instead. Skin rows also carry
    /// the body type, since two bodies can otherwise differ only by a race code buried mid-path.</summary>
    /// <param name="skin">The caller's already-computed classification — passed in rather than recomputed,
    /// since the projection that builds the rows needs it for grouping anyway.</param>
    private static string PickerLabel(string p, bool skin)
    {
        var name = Path.GetFileName(p);
        var body = skin ? UVRemapService.InferBodyType(p) : null;
        return body != null
            ? $"{SlotHint(p)}  ·  {name}  ({body})"
            : $"{SlotHint(p)}  ·  {name}";
    }

    /// <summary>
    /// Snapshot the player's equipped materials for the picker, skin first. Called only on the frame the
    /// picker opens — <see cref="ModCreationService.ListActiveMaterials"/> walks the character's resources
    /// and costs several ms, which is a dropped frame if it runs while the popup is merely open.
    /// </summary>
    private void RebuildMaterialPicker()
    {
        var src = modCreation.ListActiveMaterials(out _matPickerStale);
        _matPickerItems = src
            .Select(p => { bool skin = IsSkinMaterial(p); return (Path: p, Label: PickerLabel(p, skin), Skin: skin); })
            .OrderByDescending(e => e.Skin)                          // skin group on top
            .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void DrawModsTab()
    {
        // ── Overlay mod list ─────────────────────────────────────────────────
        // No section header and no Refresh button: the tab names the list, and Refresh moved onto the tab
        // bar's own line (DrawTabBarRefresh). The list comes from the last composite, so while the plugin
        // is off it stays empty — say so rather than claiming there are no sidecar mods.
        var mods = compositor.LastDiscovered;
        ImGui.Spacing();
        if (!config.PluginEnabled)
        {
            ImGui.TextColored(ProteusStyle.Warn, Strings.ModsList.Disabled);
        }
        else if (mods.Count == 0)
        {
            ImGui.TextDisabled(Strings.ModsList.NoMods);
        }
        else
        {
            // EndTable is only legal when BeginTable returned true — it returns false when the table is
            // skipped (clipped away, or the host window isn't rendering), and calling EndTable anyway
            // trips an ImGui assertion in debug and corrupts table state in release.
            // Bodies is NOT here: it moved into the colour panel's Advanced disclosure, beside the other
            // per-mod knob that decides how the overlay renders rather than what it is.
            if (!ImGui.BeginTable("##mods", 5,
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
                return;
            // Widths are scaled: the table's text is, so unscaled columns clip their own headers at 1.5x.
            // The names here are IDENTIFIERS, not display text — the header row below is drawn by hand, so
            // these are never shown. Left in English on purpose: ImGui keys per-column state off them, and
            // a translated name would reset widths and sort state every time the language changed.
            // The three fixed widths carry headroom over what English needs, because the visible headers
            // ARE translated and German/Russian run about a third longer.
            ImGui.TableSetupColumn("On",     ImGuiTableColumnFlags.WidthFixed, ProteusStyle.S(32f));
            ImGui.TableSetupColumn("Mod",    ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pri",    ImGuiTableColumnFlags.WidthFixed, ProteusStyle.S(78f));
            ImGui.TableSetupColumn("Colors", ImGuiTableColumnFlags.WidthFixed, ProteusStyle.S(78f));
            ImGui.TableSetupColumn("Skindent", ImGuiTableColumnFlags.WidthFixed, ProteusStyle.S(96f));

            // Clickable sort headers for Enabled / Mod / Priority (the rest are plain). Clicking the active
            // column flips direction; switching column picks a sensible default direction — Name ascending,
            // the others descending.
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            var ms = Strings.Mods;
            ProteusStyle.SortableHeader(ms.ColOn,       "modOn",   ModSort.Enabled,  ref _modSort, ref _modSortDesc, defaultDesc: true);
            ProteusStyle.SortableHeader(ms.ColMod,      "modName", ModSort.Name,     ref _modSort, ref _modSortDesc, defaultDesc: false);
            ProteusStyle.SortableHeader(ms.ColPriority, "modPri",  ModSort.Priority, ref _modSort, ref _modSortDesc, defaultDesc: true);
            ImGui.TableNextColumn(); ImGui.TableHeader(ms.ColColors);
            ImGui.TableNextColumn(); ImGui.TableHeader(ms.ColSkindent);

            // Enable/priority controls write straight through to Penumbra (Proteus keeps no
            // override state of its own); both reflect the mod's live Penumbra values.
            var collId = penumbra.GetPlayerCollectionId();

            // Display-only sort of a COPY (never mutate LastDiscovered). Sorts by the committed
            // entry.Priority, not the in-progress _priorityEdits value, so a row won't jump mid-drag.
            //
            // Active mods are pinned to the top whatever the column: a disabled mod contributes nothing
            // to the composite, and a name or priority sort that interleaves the two buries the handful
            // of rows that are actually doing something. The chosen column orders WITHIN each group.
            //
            // Sorting by "On" is the deliberate exception — that header is the one control whose whole
            // job is the enabled state, so it keeps honouring its own direction and ascending still
            // brings the disabled ones up. Pinning there too would leave a sortable header that does
            // nothing when clicked.
            var byActive = mods.OrderByDescending(e => e.Enabled);
            IOrderedEnumerable<OverlayEntry> ordered = _modSort switch
            {
                ModSort.Enabled => _modSortDesc ? byActive : mods.OrderBy(e => e.Enabled),
                ModSort.Name    => _modSortDesc ? byActive.ThenByDescending(e => e.ModName, StringComparer.OrdinalIgnoreCase)
                                                : byActive.ThenBy(e => e.ModName, StringComparer.OrdinalIgnoreCase),
                _               => _modSortDesc ? byActive.ThenByDescending(e => e.Priority)
                                                : byActive.ThenBy(e => e.Priority),
            };
            var displayMods = ordered.ThenBy(e => e.ModName, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var entry in displayMods)
            {
                ImGui.TableNextRow();

                // Enable checkbox — toggles the mod in Penumbra.
                ImGui.TableNextColumn();
                bool active = entry.Enabled;
                if (ImGui.Checkbox($"##en_{entry.ModDirectory}", ref active) && collId.HasValue)
                {
                    penumbra.SetModEnabled(collId.Value, entry.ModDirectory, active);
                    // Live edit only — write straight to Penumbra. Folding this into the active binding
                    // happens solely via the "Update binding" button (see DrawDesignBindings).
                    compositor.TriggerRecomposite("penumbra-enable");
                }

                // Mod name, dimmed when disabled and for no other reason. An applied design binding used to
                // dim it too, for a mod the binding never captured — but that mod was ON, and a row greyed
                // out for a state the user never chose reads as "broken" rather than as "not in this
                // design". Enabled mods composite now, so enabled is the only thing this colour says.
                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled), !active))
                {
                    if (ImGui.Selectable($"{entry.ModName}##{entry.ModDirectory}"))
                    {
                        penumbra.OpenToMod(entry.ModDirectory);
                    }
                }

                // Priority (drag to edit, Ctrl+click to type) — writes to Penumbra on edit-end.
                ImGui.TableNextColumn();
                int pri = _priorityEdits.TryGetValue(entry.ModDirectory, out var pe) ? pe : entry.Priority;
                ImGui.SetNextItemWidth(ProteusStyle.S(55f));
                if (ImGui.DragInt($"##pri_{entry.ModDirectory}", ref pri, 0.1f))
                    _priorityEdits[entry.ModDirectory] = pri;
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    _priorityEdits.Remove(entry.ModDirectory);
                    if (collId.HasValue)
                        penumbra.SetModPriority(collId.Value, entry.ModDirectory, pri);
                    // Live edit only (see enable toggle above); folded into the binding via the button.
                    RecompositeForOverlay(entry, "penumbra-priority");
                }

                // Colors button. Opens a real window (not a popup) so it survives clicking away —
                // colour work means going back and forth with the game, and a popup dies on any
                // click outside it. Tinted when a design binding is driving this mod's colours.
                ImGui.TableNextColumn();
                bool bindingDriven = designBindings.IsOverrideActiveFor(entry.ModDirectory);
                using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(BindingAccent with { W = 0.45f }), bindingDriven))
                {
                    if (ImGui.Button($"{ms.ColorsBtn}##colors_{entry.ModDirectory}"))
                        _colorWindowMod = _colorWindowMod == entry.ModDirectory ? null : entry.ModDirectory;
                }
                if (bindingDriven && ImGui.IsItemHovered())
                    ImGui.SetTooltip(ms.ColorsBindingDrivenTip);

                // Ambient occlusion + Skindenting for this mod (OFF unless the pack asks). THREE states —
                // "the pack decides", "forced on", "forced off" — so this is a combo and not a checkbox: a
                // checkbox can't show the difference between "ticked because the pack asked" and "ticked
                // because you said so", and offers nowhere to put the third state except a hidden modifier
                // gesture.
                ImGui.TableNextColumn();
                bool? aoDeclared = entry.Metadata?.AmbientOcclusion;
                // The user's stored opinion: the new override, else a legacy opt-out, else none.
                bool? aoChoice = config.AmbientOcclusionOverrides.TryGetValue(entry.ModDirectory, out var aoUser)
                    ? aoUser
                    : config.AmbientOcclusionDisabledMods.Contains(entry.ModDirectory) ? false : null;
                string aoPackLabel = string.Format(ms.AoPackFmt, aoDeclared == true ? ms.AoOn : ms.AoOff);
                ImGui.SetNextItemWidth(ProteusStyle.S(90f));
                if (ImGui.BeginCombo($"##ao_{entry.ModDirectory}",
                        aoChoice == null ? aoPackLabel : aoChoice.Value ? ms.AoOn : ms.AoOff))
                {
                    foreach (var (label, choice) in new[] { (aoPackLabel, (bool?)null), (ms.AoOn, true), (ms.AoOff, false) })
                    {
                        if (!ImGui.Selectable(label, choice == aoChoice) || choice == aoChoice) continue;
                        if (choice == null) config.AmbientOcclusionOverrides.Remove(entry.ModDirectory);
                        else                config.AmbientOcclusionOverrides[entry.ModDirectory] = choice.Value;
                        // The legacy opt-out set is read-only now, and any of these three choices replaces
                        // it — leaving it would let it contradict the selection the user just made.
                        config.AmbientOcclusionDisabledMods.Remove(entry.ModDirectory);
                        config.Save();
                        RecompositeForOverlay(entry, "ambient-occlusion-mod");
                    }
                    ImGui.EndCombo();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(string.Format(ms.AoTipFmt, aoPackLabel));
            }

            ImGui.EndTable();
        }
    }

    private void DrawBindingsTab()
    {
        var bs = Strings.Bindings;

        bool bindEnabled = config.DesignBindingEnabled;
        if (ImGui.Checkbox(bs.Enable, ref bindEnabled))
        {
            config.DesignBindingEnabled = bindEnabled;
            config.Save();
            // Turning the feature off drops any active override immediately so colors fall back
            // to metadata, rather than lingering until the next design application.
            if (!bindEnabled)
                designBindings.ClearColorOverride();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(bs.EnableTip);

        ImGui.Indent();
        using (ImRaii.Disabled(!bindEnabled))
        {
            bool followAutomation = config.DesignBindingFollowsAutomation;
            if (ImGui.Checkbox(bs.FollowAutomation, ref followAutomation))
            {
                config.DesignBindingFollowsAutomation = followAutomation;
                config.Save();
                // No ClearColorOverride here, unlike the parent: this path only ever restores a binding,
                // so switching it off has nothing to undo.
            }
            // AllowWhenDisabled: without it the tooltip is unreachable exactly when it is most wanted —
            // greyed out because the parent toggle is off, with nothing to explain why.
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(bs.FollowAutomationTip);
        }
        ImGui.Unindent();

        var bindings = designBindings.Bindings;
        var activeId = designBindings.ActiveDesignId;

        if (activeId.HasValue)
        {
            var act = bindings.FirstOrDefault(b => b.DesignId == activeId.Value);
            ImGui.TextDisabled(string.Format(bs.ActiveFmt, act?.DesignName ?? activeId.Value.ToString()[..8]));
        }

        if (bindings.Count == 0)
        {
            ImGui.TextDisabled(bs.NoBindings);
            return;
        }

        // See the note in DrawModsTab: EndTable is only legal when BeginTable returned true. Returning here
        // also skips the deferred Apply/Unbind dispatch below, which is correct — no row was drawn, so
        // neither button can have been clicked.
        if (!ImGui.BeginTable("##bindings", 3,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
            return;
        // Identifiers, not display text — see the Mods table for why these stay English.
        ImGui.TableSetupColumn("Design",   ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Captured", ImGuiTableColumnFlags.WidthFixed, ProteusStyle.S(112f));
        // Wide enough for three icon+text buttons (Apply / Update / Unbind), with room for the longer
        // words those become once translated.
        ImGui.TableSetupColumn("##act",    ImGuiTableColumnFlags.WidthFixed, ProteusStyle.S(300f));

        // Clickable sort headers, same idiom as the Mods tab: clicking the active column flips direction,
        // switching column picks the sensible default for that column — name ascending, dates descending.
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ProteusStyle.SortableHeader(Strings.Bindings.ColDesign, "bindDesign",
            BindingSort.Design, ref _bindingSort, ref _bindingSortDesc, defaultDesc: false);
        ProteusStyle.SortableHeader(Strings.Bindings.ColCaptured, "bindCaptured",
            BindingSort.Captured, ref _bindingSort, ref _bindingSortDesc, defaultDesc: true);
        ImGui.TableNextColumn(); ImGui.TableHeader("##act");

        // Sort a COPY. Falls back to the design label so equal timestamps — a batch capture writes several
        // in the same second — keep a stable, readable order instead of shuffling frame to frame.
        string Label(DesignBinding x) => x.DesignName ?? x.DesignId.ToString();
        var ordered = _bindingSort switch
        {
            BindingSort.Design => _bindingSortDesc
                ? bindings.OrderByDescending(Label, StringComparer.OrdinalIgnoreCase)
                : bindings.OrderBy(Label, StringComparer.OrdinalIgnoreCase),
            _ => _bindingSortDesc
                ? bindings.OrderByDescending(x => x.CapturedUtc)
                : bindings.OrderBy(x => x.CapturedUtc),
        };
        var shown = ordered.ThenBy(Label, StringComparer.OrdinalIgnoreCase).ToList();

        Guid? toApply = null, toRemove = null;
        bool toUpdate = false;
        foreach (var b in shown)
        {
            ImGui.TableNextRow();

            bool isActive = activeId == b.DesignId;
            if (isActive)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                    ImGui.GetColorU32(BindingAccent with { W = 0.20f }));

            ImGui.TableNextColumn();
            var label = b.DesignName ?? b.DesignId.ToString()[..8];
            if (isActive)
            {
                ProteusStyle.Pill(bs.PillActive, ProteusStyle.Binding);
                ImGui.SameLine();
                ImGui.TextColored(BindingAccent, label);
            }
            else
                ImGui.TextUnformatted(label);

            ImGui.TableNextColumn();
            var ago = DateTime.UtcNow - b.CapturedUtc;
            ImGui.TextDisabled(
                ago.TotalSeconds  < 60 ? string.Format(bs.SecondsAgoFmt, ago.TotalSeconds.ToString("F0"))
                : ago.TotalMinutes < 60 ? string.Format(bs.MinutesAgoFmt, ago.TotalMinutes.ToString("F0"))
                :                         string.Format(bs.HoursAgoFmt, ago.TotalHours.ToString("F0")));

            ImGui.TableNextColumn();
            // ImGuiComponents.IconButtonWithText derives its id from the LABEL, so the "##{DesignId}"
            // suffixes these buttons used to carry have nowhere to go. Without this scope every row would
            // share one id and only the first row's buttons would respond to a click.
            using (ImRaii.PushId(b.DesignId.ToString()))
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.PlayCircle, bs.Apply))
                    toApply = b.DesignId;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(bs.ApplyTip);
                ImGui.SameLine();
                // Only the active binding can be re-captured — the snapshot comes from the live state, so
                // writing it into a binding that isn't driving that state would overwrite it with a look
                // the user never applied. Drawn disabled rather than hidden so Unbind keeps its position.
                using (ImRaii.Disabled(!isActive))
                {
                    if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, bs.Update))
                        toUpdate = true;
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(isActive ? bs.UpdateTip : bs.UpdateInactiveTip);
                ImGui.SameLine();
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Unlink, bs.Unbind))
                    toRemove = b.DesignId;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(bs.UnbindTip);
            }
        }
        ImGui.EndTable();

        // All deferred past the loop: each mutates the binding state the rows are drawn from.
        if (toUpdate)
            designBindings.UpdateActiveBindingFromCurrentState();
        if (toApply.HasValue)
            designBindings.Restore(toApply.Value);
        if (toRemove.HasValue)
            designBindings.RemoveBinding(toRemove.Value);
    }

    private void DrawColorEditor(OverlayEntry entry)
    {
        // display: false — this is a mod name the user chose, and Jupiter is a display face with narrow
        // glyph coverage, so a CJK or accented name would render as boxes.
        ProteusStyle.SectionHeader(entry.ModName, display: false);

        // When a design binding drives this mod, edits target the binding (not metadata.json).
        bool editingBinding = designBindings.IsOverrideActiveFor(entry.ModDirectory);
        if (editingBinding)
        {
            var activeId = designBindings.ActiveDesignId;
            var name = designBindings.Bindings.FirstOrDefault(b => b.DesignId == activeId)?.DesignName
                       ?? activeId?.ToString()[..8] ?? "?";
            // Both Advanced caveats, because the blanket "base colors unchanged" promise would be a lie
            // next to either: Reset rewrites the mod's own settings, and Bodies isn't a colour at all —
            // it's global config that no binding captures or restores.
            // Carded so the caveat reads as one bounded notice rather than two loose coloured lines that
            // look like part of the editor's own copy.
            using (ProteusStyle.Card(ProteusStyle.Binding))
            {
                // Both notices are full sentences and ImGui.TextColored does NOT wrap — it draws one line
                // and lets the window clip it. English happened to fit; German and Russian run a third
                // longer and ran straight off the right edge with no way to read the rest.
                // Stopping 14px short of the content edge keeps the wrapped text inside the card's border:
                // Card indents its contents by 8 and paints the frame 6 past the widest item.
                ImGui.PushTextWrapPos(ImGui.GetWindowContentRegionMax().X - ProteusStyle.S(14f));
                ProteusStyle.Pill(Strings.ColorPanel.PillBinding, ProteusStyle.Binding);
                ImGui.SameLine();
                ImGui.TextColored(BindingAccent, string.Format(Strings.ColorPanel.EditingBindingFmt, name));
                ImGui.TextColored(BindingAccent, Strings.ColorPanel.BaseUnchanged);
                ImGui.PopTextWrapPos();
            }
            ImGui.Spacing();
        }


        // Clear per-entry index cache on popup open so option switches are reflected.
        if (ImGui.IsWindowAppearing())
        {
            foreach (var k in _indexRowCache.Keys.Where(k => k.StartsWith(entry.SidecarRoot)).ToList())
                _indexRowCache.Remove(k);
            // Same reason, and it needs its own sweep: a content material's scan is keyed by mod directory,
            // material and selection rather than by sidecar path. The selection is IN the key, so a live
            // option change already re-reads without this — what this catches is the pack's own files
            // changing underneath an unchanged selection (a re-import, or an author editing in place).
            foreach (var k in _contentIndexCache.Keys
                         .Where(k => k.StartsWith(entry.ModDirectory + "|", StringComparison.OrdinalIgnoreCase))
                         .ToList())
                _contentIndexCache.Remove(k);
        }

        // Which body's UV this mod's art is painted in — needed to know where its UV islands are, so the
        // index scan can ignore the dilated bleed outside them.
        var bodyType = OverlayBodyType(entry);

        // Scroll maps a gear overlay can pick from: the mod's own Effects/ folder, then the user's.
        var effects = discovery.ResolveAvailableEffects(entry, discovery.EffectsLibraryPath());

        // ── content packs: the pack's OWN material, one tab per selected option ────
        // Drawn first and separately from the overlay paths below, because a content option has no overlay
        // descriptor at all — no art, no coverage, no index texture of ours. Its colours go into the
        // material the PACK ships, so the only control that means anything is the colour grid itself.
        if (entry.Metadata is { HasContent: true })
        {
            DrawContentColorEditor(entry, editingBinding, effects);
            // A pack may ship geometry AND overlays; only a pure content pack is finished here.
            if (entry.Metadata.Overlays is not { Count: > 0 } && entry.Metadata.OptionGroups is not { Count: > 0 })
            {
                ImGui.Separator();
                if (ImGui.TreeNodeEx($"{Strings.Colors.Advanced}##content_{entry.ModDirectory}",
                        ImGuiTreeNodeFlags.NoTreePushOnOpen))
                    DrawBodiesAdvanced(entry);
                return;
            }
            ImGui.Separator();
        }

        // ── simple-mod path (top-level Overlays, no OptionGroups) ────────────
        if (entry.Metadata.OptionGroups is not { Count: > 0 })
        {
            var metaRows = entry.Metadata.ColorTableRows ?? [];
            // Preview live without touching the binding until something is actually edited — see the Masks
            // tab below for why creating the override on read is wrong. While editing a binding this ALWAYS
            // works on a copy, even when an override already exists: the override is what the compositor
            // reads from another thread, so the draw loop must never be writing into it.
            var ovrRows  = editingBinding ? designBindings.PeekOverrideRows(entry.ModDirectory, null, null) : null;
            var rows = editingBinding ? DesignBindingService.CopyRows(ovrRows ?? metaRows) : metaRows;

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
            // Active "Masks" options can inject additional rows via their own Index companion —
            // see the option-group path below for the full explanation.
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

            // Layer/shader normally persist to metadata.json — but while a binding is being edited they
            // go into its gear override (live preview, saved on "Update binding"), like colour rows.
            var simpleOverlays = entry.Metadata.Overlays ?? [];
            var gearOvrSimple = editingBinding && simpleOverlays.Count > 0
                ? designBindings.GetEditableGearOverride(entry.ModDirectory, null, null, simpleOverlays[0])
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
            bool footerChangedSimple = ColorTableEditor.DrawGlowFooter(
                entry.ModDirectory, entry.ModDirectory, simpleOverlays, gearOvrSimple, effects,
                out var footerEditSimple,
                onReset: () => resetSimple = ResetToDefaults(entry, null, null),
                resetDisabledReason: ResetBlockedReason(entry),
                drawExtraAdvanced: () => DrawBodiesAdvanced(entry),
                editingBinding: editingBinding);

            // A reset just restored the recorded values — they ARE the intended state, so skip the mode
            // re-inference and glow transition this frame. Both compare against pre-reset state and would
            // happily re-pin the mode or zero/150% the glow rows we only just put back.
            bool modeChangedSimple = false;
            if (!resetSimple)
            {
                // Let the features drive the render mode (unless the user pinned it). Runs after all draws.
                modeChangedSimple = ReconcileMode(simpleOverlays, gearOvrSimple, rows,
                    rowEditSimple != FeatureEdit.Neutral ? rowEditSimple : footerEditSimple);

                // Default every row's Glow to 150%/white only when the mode actually ENTERS Animated glow, and
                // zero it only when it LEAVES — not on an effect-to-effect swap (which would wipe custom glow).
                ApplyGlowTransition(rows, modeBeforeSimple, EffectiveMode(simpleOverlays, gearOvrSimple));
            }

            if (changedSimple || footerChangedSimple || modeChangedSimple)
            {
                // Binding path: live-preview only — install the edit into the in-memory override now (the
                // first point we know one happened, so drawing alone never changes the binding) and fold it
                // into the stored binding on "Update binding". Base metadata persists only when NOT editing
                // a binding — except a reset, which exists precisely to rewrite the base and must land.
                if (editingBinding && !resetSimple)
                    designBindings.SetOverrideRows(entry.ModDirectory, null, null, rows);
                // NOT on a reset: ResetToDefaults has already restored entry.Metadata in place, and `rows`
                // is a working copy taken BEFORE it ran — writing that back would undo the restore and then
                // save the undone state over the mod's own settings.
                if (!editingBinding && !resetSimple)
                    entry.Metadata.ColorTableRows = rows;   // may be the list we created for an empty mod
                if (!editingBinding || resetSimple) { discovery.SaveMetadata(entry); InvalidateDefaultsCache(entry); }
                // Discrete footer/mode changes recomposite promptly; colour-row drags use the debounce.
                if (footerChangedSimple || modeChangedSimple) RecompositeForOverlay(entry, "mode-change");
                // Rows only — hashed at the fingerprint's `mtrl:` block, so skin reuse may apply. A change
                // that DOES move a skin texel still shows up in the skin fingerprint and rebuilds.
                else RecompositeForOverlay(entry, "colors-change", ColorEditDebounceMs,
                    skinFingerprintAuthoritative: true);
            }
            return;
        }

        // ── option-group path ─────────────────────────────────────────────────

        var collId   = penumbra.GetPlayerCollectionId();
        var settings = collId.HasValue ? penumbra.GetModSettings(collId.Value, entry.ModDirectory) : null;

        var activeOptions = new List<(string GroupName, OverlayOption Option)>();
        foreach (var group in entry.Metadata.OptionGroups)
        {
            if (group.Options.Count == 0) continue;
            // Matched case-insensitively, like SidecarDiscoveryService.ResolveActiveOverlays: the dictionary
            // comes from Penumbra's IPC and we don't control its comparer, and a missed key here would read
            // as "nothing selected".
            List<string>? selected = settings.HasValue
                ? settings.Value.Options
                    .FirstOrDefault(kv => string.Equals(kv.Key, group.PenumbraGroupName, StringComparison.OrdinalIgnoreCase))
                    .Value
                : null;

            // A group with NOTHING selected contributes nothing — the composite path (ResolveActiveOverlays)
            // skips it outright, so falling back to Options[0] here put a tab and a colorset on screen for an
            // option whose texture is never painted. Only when Penumbra can't be asked at all (no collection,
            // IPC down) do we preview the group's first option, so the editor isn't blank while it's away.
            IEnumerable<OverlayOption> active;
            if (selected is { Count: > 0 })
                active = group.Options.Where(o => selected.Any(s =>
                    string.Equals(o.Name, s, StringComparison.OrdinalIgnoreCase)));
            else if (settings.HasValue)
                continue;
            else
                active = [group.Options[0]];

            // Display (and thus stack) order within the group follows the user's saved order, top-first.
            // Stable, so options the user hasn't reordered keep their natural order.
            active = active.OrderBy(o => config.StackIndexOf(entry.ModDirectory, group.PenumbraGroupName, o.Name));

            foreach (var opt in active)
            {
                if (opt.Name.EndsWith("None", StringComparison.OrdinalIgnoreCase))
                    continue;
                activeOptions.Add((group.PenumbraGroupName, opt));
            }
        }

        // Masks are a layer too — one "Masks" tab, always on top, editing the shared MaskColorTableRows
        // colorset (see below). Present whenever any active mask carries an _id (its colour-row index).
        var maskAssets = discovery.ResolveActiveMaskAssets(entry);
        bool anyMaskWithId = maskAssets.Any(a => a.IndexPath != null);

        if (activeOptions.Count == 0 && !anyMaskWithId)
        {
            // Nothing selected means there are no colours to edit — but Bodies is a MOD-wide setting and
            // this panel is now its only home, so it can't leave with the tabs. An all-"None" mod is a mod
            // that isn't painting, and "the bake never reached my body type" is one of the reasons why, so
            // the control would otherwise disappear in exactly the state that sends someone looking for it.
            ProteusStyle.DisabledWrapped(Strings.ColorPanel.NoActiveOptions);
            if (ImGui.TreeNodeEx($"{Strings.Colors.Advanced}##noopt_{entry.ModDirectory}",
                    ImGuiTreeNodeFlags.NoTreePushOnOpen))
                DrawBodiesAdvanced(entry);
            return;
        }

        // Show the tabs in TRUE stacking order, top-first — the same ordering the compositor applies,
        // just reversed (it composites last-on-top). Until this, the strip listed groups in metadata
        // order while the composite ranked them by Penumbra group number, so the "leftmost = on top"
        // label could be a lie whenever two groups were active.
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

            // While a design binding is active the mod-wide order lives on the binding, not the global
            // config (the composite reads it via CompositorService.ModStackIndexFor) — so the tab strip must
            // order its buttons the same way, or a restack moves the cloth but leaves the buttons put.
            var stackOvr = designBindings.ActiveStackOrderFor(entry.ModDirectory);
            int ModStackIdx(string group, string option)
                => stackOvr != null
                    ? Configuration.ModStackIndexIn(stackOvr, group, option)
                    : config.ModStackIndexOf(entry.ModDirectory, group, option);

            activeOptions = activeOptions
                .OrderBy(x => ModStackIdx(x.GroupName, x.Option.Name))
                .ThenBy(x => GroupOrderOf(x.GroupName))
                .ThenBy(x => config.StackIndexOf(entry.ModDirectory, x.GroupName, x.Option.Name))
                .ToList();
        }

        // Masks always render on top → one "Masks" tab at the very top (index 0) of the strip. It's a
        // synthesized skin-layer option with no overlay descriptors; its rows live in MaskColorTableRows.
        if (anyMaskWithId)
            activeOptions.Insert(0, (SidecarDiscoveryService.MaskGroupName, new OverlayOption { Name = "Masks" }));

        static string SelKey(string g, string o) => g + "\0" + o;

        // Resolve the selected overlay by identity, so a reorder doesn't change what you're editing.
        int selIdx = _colorEditorSel.TryGetValue(entry.ModDirectory, out var wantKey)
            ? activeOptions.FindIndex(x => SelKey(x.GroupName, x.Option.Name) == wantKey)
            : -1;
        if (selIdx < 0) selIdx = 0;

        // Several overlays are active at once (a multi-select group, or several groups). Show one tab each
        // so it's clear WHICH you're editing; within a group the left→right order IS the stacking order.
        // These are custom-drawn tab buttons (not an ImGui TabBar) because a TabBar remembers each tab's
        // slot by id and won't visually reorder on resubmission — we need the order to follow the data.
        if (activeOptions.Count > 1)
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

                // The Masks tab is pinned to the top (it's re-injected at index 0 every frame), so it can
                // neither be dragged nor be a drop target — otherwise a drag would fire a pointless recomposite
                // while the tab visibly stays put. A small divider sets it apart from the overlay tabs.
                if (isMaskTab)
                {
                    ImGui.SameLine(0, 4);
                    ImGui.TextDisabled("|");
                    continue;
                }

                // Drag one tab onto another in the SAME group to restack (payload is a bare marker; the
                // source identity rides in _stackDragSrc).
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
                    // Any tab onto any other tab of the same MOD — crossing groups is the point (see
                    // Configuration.OverlayModStackOrder); the old same-group check is what made one
                    // group permanently outrank another.
                    if (!pl.IsNull && _stackDragSrc is { } s
                        && s.Mod == entry.ModDirectory && (s.Group != gName || s.Option != opt.Name))
                        pendingReorder = (SelKey(s.Group, s.Option), SelKey(gName, opt.Name));
                    ImGui.EndDragDropTarget();
                }
            }

            // The whole strip is one stack now, so both the arrows and the drag operate over every
            // active option of this mod — not just the selected one's group.
            var stackKeys = activeOptions.Select(x => SelKey(x.GroupName, x.Option.Name)).ToList();

            void PersistStack(List<string> keysTopFirst)
            {
                var topFirst = keysTopFirst.Select(k =>
                {
                    var parts = k.Split('\0');
                    return (Group: parts[0], Option: parts.Length > 1 ? parts[1] : "");
                }).ToList();

                // While a design binding is being edited the restack is a live-preview override on the
                // binding (folded in via "Update binding"), like colour/gear edits — the global stack
                // config is left untouched. Falls back to the global config when no binding is active.
                if (!(editingBinding && designBindings.SetEditableStackOrder(entry.ModDirectory, topFirst)))
                    config.SetModStackOrder(entry.ModDirectory, topFirst);
                RecompositeForOverlay(entry, "stack-reorder");
            }

            // ── ◀ ▶ arrows: restack the selected overlay (drag alternative) ──
            if (stackKeys.Count > 1)
            {
                int pos = selIdx;
                // The Masks tab is pinned to the top — it can't be restacked, so both arrows are disabled
                // while it's selected (moving it would recomposite yet leave it visibly at the top).
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
                    // Insert at the target's ORIGINAL index: dragging rightward (src<dst) the removal shifts
                    // the target left one, so this lands src just AFTER it; dragging leftward it lands just
                    // BEFORE. (Recomputing IndexOf after the remove always lands before — the earlier bug.)
                    stackKeys.RemoveAt(srcIdx);
                    stackKeys.Insert(dstIdx, pr.Src);
                    PersistStack(stackKeys);
                }
            }
        }

        var (groupName, activeOpt) = activeOptions[selIdx];

        // The synthesized "Masks" tab: one shared skin-layer colorset (MaskColorTableRows) coloured by the
        // combined mask _id, no overlay descriptors, no gear/promotion, no design-binding override.
        bool isMask = string.Equals(groupName, SidecarDiscoveryService.MaskGroupName, StringComparison.Ordinal);

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

        // Active "Masks" options can inject additional rows via their own Index companion
        // (Masks/<Option>_id.png), overriding row selection at composite time (see
        // LoadIndexMerged in CompositorService) — union those in too, so a row referenced
        // only by a mask still gets a color picker here.
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

        // ── Masks tab: one shared colorset for all active masks ───────────────────────────────────────
        // The active masks are composited together (coverage/relief/_id) into one top layer; these rows
        // colour it. When the mod has gear the mask is forced to a Cloth shell; when it's ALL SKIN the mask
        // gets its own Skin/Cloth/Glow mode (same auto-detection + Advanced as an overlay tab), stored in
        // MaskDescriptor. Used-rows are the combined mask _id already unioned above.
        if (isMask)
        {
            // Don't create the list just by drawing the tab — only commit it to metadata on an actual edit
            // (below), so merely selecting the Masks tab has no persistent side effect. While a design
            // binding is being edited the rows/mode come from (and mutate) the binding's mask overrides
            // instead, so they're captured per-design (live preview until "Update binding").
            var baseMaskRows = entry.Metadata.MaskColorTableRows ?? [];
            // While a binding is being edited, preview live WITHOUT touching it until something is actually
            // edited: take the override only when one already exists, otherwise work on a COPY of the
            // metadata and install it below the moment a change happens. Materialising on read (which is
            // what the old GetEditableMaskRows did) snapshotted the metadata just for drawing the tab, and
            // that snapshot then shadowed every later metadata edit for as long as the design stayed
            // applied — the editor showed the new colour while the composite kept painting the old one.
            var storedMaskRows = editingBinding ? designBindings.PeekMaskRows(entry.ModDirectory) : null;
            var maskRows = editingBinding
                ? DesignBindingService.CopyRows(storedMaskRows ?? baseMaskRows)
                : baseMaskRows;
            var maskScope = $"{entry.ModDirectory}_{SidecarDiscoveryService.MaskGroupName}";
            int maskSel   = _rowSelection.GetValueOrDefault(maskScope, 0);
            bool maskChanged = false;

            // When the mod has any gear layer, the mask is FORCED to a top Cloth shell (it stacks over gear),
            // so no mode choice is offered. When it's all skin, the mask carries its own mode descriptor and
            // gets the full auto-detection + Advanced, exactly like an overlay option.
            // EFFECTIVE layer, not the stored one: an active design binding can flip an option to Skin
            // (captured per-design), and the compositor honours that when deciding whether the mask rides a
            // shell. Reading the raw descriptor here left the badge claiming Cloth — and hid Advanced — for a
            // mod whose only "gear" option the design had overridden back to Skin.
            bool modHasGear = activeOptions.Any(x =>
            {
                if (string.Equals(x.GroupName, SidecarDiscoveryService.MaskGroupName, StringComparison.Ordinal))
                    return false;
                var ovr = designBindings.PeekGearOverride(entry.ModDirectory, x.GroupName, x.Option.Name);
                // Tested per DESCRIPTOR, not just the option's first: the compositor turns EVERY Gear
                // descriptor into its own shell, so one gear descriptor anywhere in the option is enough to
                // put the mask on a shell. (EffectiveLayerShader alone reads only overlays[0].)
                return x.Option.Overlays.Any(d => ColorTableEditor.EffectiveLayerShader([d], ovr).Gear);
            });

            // The mask's working descriptor: its stored mode, or a fresh Skin default. Mutated in place by
            // ReconcileMode/DrawGlowFooter when not editing a binding; the binding's gear override is mutated
            // instead when one is active.
            var maskDesc = entry.Metadata.MaskDescriptor ?? new OverlayDescriptor { Layer = OverlayLayer.Skin };
            var maskGearOvr = editingBinding
                ? designBindings.GetEditableMaskGearOverride(entry.ModDirectory, maskDesc)
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

            // Cold-boot warmup, same as the overlay path: the Glow-locator's backing data (shell materials or
            // skin-glow targets) only exists after a composite has processed this mask. Guard the one-shot by
            // the mask LAYER too — flipping the mask between a skin bake and a shell changes which locator data
            // it needs, so a per-mod guard would leave the button missing.
            bool maskLocatorMissing = maskAsGear
                ? maskShellMaterials == null || maskShellMaterials.Count == 0
                : maskGlowTargets == null || maskGlowTargets.Count == 0;
            // entry.Enabled ahead of the one-shot — see the overlay warmup below.
            if (config.PluginEnabled && entry.Enabled && maskLocatorMissing
                && _glowWarmedMods.Add($"{entry.ModDirectory}\0masks\0{(maskAsGear ? 'g' : 's')}"))
                compositor.TriggerRecomposite("mask-glow-warmup");

            ColorTableEditor.DrawRows(maskScope, maskRows, usedRows, maskAsGear, maskShader,
                maskShellMaterials,
                maskGlowTargets,
                out var maskRowEdit, ref maskSel, ref maskChanged,
                // A mask on the GEAR layer becomes a mask shell, and there a half-authored row pair renders
                // with its unset half mirrored (SecondSkinService.BuildRows) — so the swatches have to show
                // it that way. A mask still on skin is painted into the diffuse, where an unset sub-row is a
                // neutral multiply and mirroring would misreport it.
                mirrorUnsetSubRows: maskAsGear,
                usedSubRow: usedColumn);
            _rowSelection[maskScope] = maskSel;

            ImGui.Separator();
            bool maskFooterChanged = false, maskModeChanged = false;
            if (modHasGear)
            {
                // Forced Cloth shell — the mask's layer isn't user-chosen when it stacks over gear.
                ColorTableEditor.DrawRenderingAsBadge(RenderMode.Cloth);
                ImGui.SameLine();
                ImGui.TextDisabled(Strings.ColorPanel.Forced);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Strings.ColorPanel.ForcedTip);
            }
            else
            {
                // Same footer as the overlay tabs: the "Rendering as" badge + Advanced force-mode radios +
                // glow-effect picker. No per-option reset (the mask has no defaults cache) → onReset null.
                maskFooterChanged = ColorTableEditor.DrawGlowFooter(
                    maskScope, entry.ModDirectory, [maskDesc], maskGearOvr, effects,
                    out var maskFooterEdit, onReset: null,
                    drawExtraAdvanced: () => DrawBodiesAdvanced(entry),
                    // Nothing reads MaskDescriptor.SkinToneMask — the mask paints into the diffuse in its
                    // own pass, and a promoted mask gets a shell descriptor built from scratch — so the
                    // slider would be a control that saves and does nothing.
                    skinTintApplies: false,
                    editingBinding: editingBinding);
                maskModeChanged = ReconcileMode([maskDesc], maskGearOvr, maskRows,
                    maskRowEdit != FeatureEdit.Neutral ? maskRowEdit : maskFooterEdit);
                ApplyGlowTransition(maskRows, maskModeBefore, EffectiveMode([maskDesc], maskGearOvr));
            }

            if (maskChanged || maskFooterChanged || maskModeChanged)
            {
                // Binding path: install the edited rows as the binding's LIVE override now — this is the
                // first point at which we know an edit actually happened, so the binding never changes just
                // from being looked at. Still preview-only; the stored binding on disk is untouched until
                // "Update binding". Base metadata persists only when NOT editing a binding.
                if (editingBinding)
                {
                    designBindings.SetMaskRows(entry.ModDirectory, maskRows);
                }
                else
                {
                    entry.Metadata.MaskColorTableRows = maskRows;
                    if (maskFooterChanged || maskModeChanged) entry.Metadata.MaskDescriptor = maskDesc;
                    discovery.SaveMetadata(entry);
                    InvalidateDefaultsCache(entry);
                }
                if (maskFooterChanged || maskModeChanged) RecompositeForOverlay(entry, "mask-mode-change");
                // Rows only: hashed at BuildCompositeFingerprint's `maskrow:` block, so the skin-reuse gate
                // can be trusted with this one and a mask that lives on a shell costs no skin re-blend.
                else RecompositeForOverlay(entry, "mask-colors-change", ColorEditDebounceMs,
                    skinFingerprintAuthoritative: true);
            }
            return;
        }

        var optRows = activeOpt.ColorTableRows ?? [];
        var ovrOptRows = editingBinding
            ? designBindings.PeekOverrideRows(entry.ModDirectory, groupName, activeOpt.Name)
            : null;
        var editRows = editingBinding ? DesignBindingService.CopyRows(ovrOptRows ?? optRows) : optRows;

        var scope = $"{entry.ModDirectory}_{groupName}_{activeOpt.Name}";

        // Layer/shader normally persist to metadata.json — but while a binding is being edited they go
        // into its gear override (live preview, saved on "Update binding"), like colour rows.
        var gearOvrOpt = editingBinding && activeOpt.Overlays.Count > 0
            ? designBindings.GetEditableGearOverride(entry.ModDirectory, groupName, activeOpt.Name, activeOpt.Overlays[0])
            : null;
        var modeBefore = EffectiveMode(activeOpt.Overlays, gearOvrOpt);
        var (gear, shader) = ColorTableEditor.EffectiveLayerShader(activeOpt.Overlays, gearOvrOpt);

        // Auto promotion — mirror the compositor through the SAME predicate it uses
        // (RenderModeInference.ShouldPromoteToGear), so the editor can't disagree with what was actually
        // composited: a skin overlay on auto (not pinned) renders as a gear shell when it sits above a gear
        // layer, or when its rows ask for something skin.shpk can't do (sphere/metal/specular/glow).
        // activeOptions is already sorted top-first, so any option below this one (index > selIdx) that is
        // gear means this one sits above gear. Without this the Glow button never shows — it keys off shell
        // materials for gear vs skin-glow targets for skin, and the compositor built the former, not the latter.
        // Whether a cloth/glow layer is reachable for this option at all. Shells are cut from your own skin —
        // body, face, hair, tail, ears — so an option painting gear, an accessory or a weapon has no surface
        // to become one, and the compositor refuses it either way (CanRenderAsShell). Path-based, so this
        // answer is stable rather than flickering with whatever the draw-object walk last saw.
        bool canShell = activeOpt.Overlays.Count == 0
                     || activeOpt.Overlays.Any(CompositorService.CanRenderAsShell);
        var noShellReason = canShell ? null
            : "This overlay paints something Proteus can't build a layer over — gear, an accessory or a\n"
            + "weapon. Glow and Cloth need a layer over your own skin: body, face, hair, tail or ears.";

        bool promotedToGear = false;
        if (!gear)
        {
            // !gear means the effective layer (override first) is Skin — the state the promotion acts on.
            // The binding's pin, when one is being edited, outranks the descriptor's.
            bool pinned = gearOvrOpt?.ManualShaderLock
                ?? activeOpt.Overlays.FirstOrDefault()?.ManualShaderLock ?? false;
            bool aboveGear = activeOptions.Skip(selIdx + 1).Any(x => x.Option.Overlays.Any(d => d.Layer == OverlayLayer.Gear));
            // The third promotion reason: asymmetric art on the mirrored (vanilla) body, which the skin layer
            // would fold in half. Asked through the compositor so the two share one answer — unlike the
            // others this one depends on which body is worn right now, so the editor cannot derive it.
            bool needsUnmirrored = activeOpt.Overlays.Any(compositor.NeedsUnmirroredShell);
            if (RenderModeInference.ShouldPromoteToGear(OverlayLayer.Skin, pinned, editRows, aboveGear, canShell,
                                                        needsUnmirrored))
            {
                // The shader comes from the shared predicate too, not a hardcoded character.shpk: a promoted
                // whole-skin overlay actually renders on skin.shpk, and showing it as cloth here offered the
                // sphere/metal/glow controls that shader cannot honour (it has no row selector at all).
                gear = true; promotedToGear = true;
                shader = RenderModeInference.PromotedShader(
                    activeOpt.Overlays.FirstOrDefault() ?? new OverlayDescriptor(), editRows);
            }
        }
        // A stored Gear layer on a surface no shell can cover renders as skin (the compositor demotes it),
        // so the colour panel below has to read as skin too — otherwise it edits a shell that isn't there.
        else if (!canShell)
        {
            gear = false; shader = OverlayDescriptor.SkinShader;
        }

        bool changed = false;
        int sel = _rowSelection.GetValueOrDefault(scope, 0);

        var skinGlowTargets = compositor.GetSkinGlowTargets(entry.ModDirectory, groupName, activeOpt.Name);
        var shellMaterials  = compositor.GetShellMaterials(entry.ModDirectory, groupName, activeOpt.Name);

        // Cold-boot: the Glow-locator button's backing data is a byproduct of a composite, so it only
        // exists after one has processed this option. When the plugin loads after the character already
        // exists, no model/customize event fires to trigger one, so the boot composite can leave it empty
        // and the button missing until the user nudges something. Fire one recomposite the first time we
        // draw an option whose data is missing — guarded per-mod so it can't loop.
        //
        // BOTH layers need this: skin uses the diffuse-composite glow recipe (GetSkinGlowTargets), gear
        // uses the built shell's materials (GetShellMaterials, driving the live colour-table highlighter).
        // Both are republished empty at the top of every composite and only filled when the work runs.
        // (We can't cheaply build just that data: it falls out of the diffuse pass / the shell build, so
        // isolating it would duplicate most of the composite. Lazy-firing the real one, once, is contained.)
        bool locatorDataMissing = gear
            ? shellMaterials  == null || shellMaterials.Count  == 0
            : skinGlowTargets == null || skinGlowTargets.Count == 0;
        // Guard the one-shot per (mod, group, option, LAYER): switching an option Skin↔Cloth changes which
        // locator data it needs (skin-glow targets vs the shell's materials), and the freshly-needed one
        // hasn't been built yet. A per-mod guard would have spent its single warmup on the old layer and
        // never fire for the new one, leaving the Glow button missing after the switch.
        // entry.Enabled ahead of the one-shot, not after: a disabled mod's composite would produce no
        // locator data anyway, and consuming the warmup here would spend it on a run that never happens —
        // leaving the Glow button missing for the whole session. See RecompositeForOverlay.
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
        bool footerChanged = ColorTableEditor.DrawGlowFooter(
            scope, entry.ModDirectory, activeOpt.Overlays, gearOvrOpt, effects, out var footerEdit,
            onReset: () => resetOpt = ResetToDefaults(entry, groupName, activeOpt),
            resetDisabledReason: ResetBlockedReason(entry),
            drawExtraAdvanced: () => DrawBodiesAdvanced(entry),
            promotedToGear: promotedToGear,
            noShellReason: noShellReason,
            editingBinding: editingBinding);

        // A reset just restored the recorded values — they ARE the intended state, so skip the mode
        // re-inference and glow transition this frame (both would re-derive from pre-reset state).
        bool modeChanged = false;
        if (!resetOpt)
        {
            modeChanged = ReconcileMode(activeOpt.Overlays, gearOvrOpt, editRows,
                rowEdit != FeatureEdit.Neutral ? rowEdit : footerEdit, canShell);

            // Default every row's Glow to 150%/white only when the mode actually ENTERS Animated glow, and zero
            // it only when it LEAVES — not on an effect-to-effect swap (which would wipe custom glow).
            ApplyGlowTransition(editRows, modeBefore, EffectiveMode(activeOpt.Overlays, gearOvrOpt));
        }

        if (changed || footerChanged || modeChanged)
        {
            // Binding path: live-preview only (folded in via "Update binding"). Base metadata persists only
            // when NOT editing a binding — gate on that directly, not on whether a gear override exists
            // (an option with colour rows but no overlay descriptors has a null gear override even mid-binding).
            // A reset is the exception: it rewrites the base on purpose, so it must always land.
            // Install the edited rows as the binding's live override at the first point we know an edit
            // happened, so the binding never changes just from being drawn. Preview only — the stored
            // binding is written on "Update binding".
            if (editingBinding && !resetOpt)
                designBindings.SetOverrideRows(entry.ModDirectory, groupName, activeOpt.Name, editRows);
            // NOT on a reset — see the simple-mod path: the reset already rewrote activeOpt in place.
            if (!editingBinding && !resetOpt)
                activeOpt.ColorTableRows = editRows;
            if (!editingBinding || resetOpt) { discovery.SaveMetadata(entry); InvalidateDefaultsCache(entry); }
            // Discrete footer/mode changes recomposite promptly; colour-row drags use the debounce.
            if (footerChanged || modeChanged) RecompositeForOverlay(entry, "mode-change");
            // Rows only — see the simple-mod path above.
            else RecompositeForOverlay(entry, "colors-change", ColorEditDebounceMs,
                skinFingerprintAuthoritative: true);
        }
    }

    /// <summary>
    /// The mod's sibling-synthesis mode — which body types its overlays get baked onto — drawn inside the
    /// colour panel's Advanced disclosure.
    /// <para/>
    /// It lives here rather than in the Mods table because it is not a property of the mod so much as of
    /// how its overlay renders, which is what the colour panel is for; and because getting it wrong looks
    /// like "the overlay doesn't paint", which is diagnosed in front of the colours, not in a list. Per-MOD
    /// while everything else in that disclosure is per-option, so it repeats identically on every option
    /// tab, and it commits for itself: what it edits is neither this option's colours nor its metadata, so
    /// reporting back through the footer's change flag would make every caller save option metadata — or
    /// install a design-binding override — for something that has nothing to do with either.
    /// </summary>
    private void DrawBodiesAdvanced(OverlayEntry entry)
    {
        var mode = config.SiblingModeFor(entry.ModDirectory);

        ImGui.SetNextItemWidth(120);
        var cp = Strings.ColorPanel;
        if (ImGui.BeginCombo($"{cp.Bodies}##bodies_{entry.ModDirectory}", SiblingModeLabel((int)mode)))
        {
            foreach (var opt in new[] { SiblingSynthesisMode.AllBodies, SiblingSynthesisMode.BiboGen3Only, SiblingSynthesisMode.Off })
            {
                if (ImGui.Selectable(SiblingModeLabel((int)opt), opt == mode) && opt != mode)
                {
                    config.SiblingSynthesis[entry.ModDirectory] = opt;
                    config.Save();
                    RecompositeForOverlay(entry, "sibling-mode");
                }
            }
            ImGui.EndCombo();
        }
        bool binding = designBindings.IsOverrideActiveFor(entry.ModDirectory);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cp.BodiesTip + (binding ? cp.BodiesGlobalSuffix : ""));

        // Said in the panel, not only on hover: everything else in this window previews into the binding,
        // so a control that quietly writes global config instead has to break that expectation out loud.
        if (binding)
            ImGui.TextDisabled(cp.BodiesGlobalNote);
    }

    /// <summary>
    /// After the header + rows are drawn, point Layer/Shader at the mode the features imply — a sphere map
    /// or metal ⇒ Cloth, a glow effect ⇒ Animated glow, nothing special ⇒ Skin — unless the user pinned it
    /// in Advanced (<see cref="OverlayDescriptor.ManualShaderLock"/>). The one thing that outranks the pin is
    /// a selected glow effect, which forces Animated glow and releases the pin — see the comment below for
    /// why a pinned Cloth over an effect renders nothing at all. Writes the override when a design binding is
    /// being edited, else the descriptors. Returns true when the mode actually changed.
    /// </summary>
    private static bool ReconcileMode(IReadOnlyList<OverlayDescriptor> overlays, GearSettingsPreset? ovr,
        List<ColorTableRowPreset> rows, FeatureEdit edited, bool canShell = true)
    {
        // Only respond to an actual mode-relevant edit this frame. Running on every frame would force a
        // deliberately plain Gear overlay (no sphere/metal/scroll — used for shell transparency) to Skin.
        if (edited == FeatureEdit.Neutral) return false;
        if (overlays.Count == 0) return false;
        bool locked = ovr != null ? (ovr.ManualShaderLock ?? false) : overlays.Any(d => d.ManualShaderLock);

        var cur  = RenderModeInference.ModeOf(ovr?.Layer ?? overlays[0].Layer,
                                              ovr != null ? ovr.Shader : overlays[0].Shader);

        // A chosen glow effect OUTRANKS the pin, and releases it.
        //
        // characterscroll.shpk is the only shader that emits: GearMaterialWriter.PatchColorTable arms the
        // effect-enable field (23) — the master switch, without which nothing renders however right the rest
        // is — solely under `isScroll`. So a pin holding Cloth (or Skin) over a selected effect does not
        // render a subdued glow, it renders NONE, while the editor keeps the Glow slider and the effect
        // thumbnail live and looking applied. That is the "I set Glow and the mask stays dark" report.
        //
        // Picking an effect is an unambiguous request to glow, so it wins. The pin is CLEARED rather than
        // carried onto Glow: the effect is now what decides the mode, and a pin left behind would strand the
        // option on characterscroll with no scroll map the moment the effect is cleared again.
        //
        // Narrow on purpose. THIS EDIT must be the effect pick — FeatureEdit.Glow, which only the effect
        // picker raises (ColorTableEditor's DrawEffectPicker branch). Testing merely that a scroll IS
        // assigned was wrong and destructive: "pinned Cloth with an effect still assigned" is a deliberate
        // state (pick an effect, then force Cloth in Advanced — ApplyMode never clears Scroll), and any
        // later Cloth edit on such an overlay — a sphere-map nudge, which reports FeatureEdit.Cloth —
        // would flip it to Glow, whereupon the caller's ApplyGlowTransition rewrites EVERY row to 150%
        // white and SaveMetadata persists it, with no undo.
        //
        // The Glow SLIDER deliberately reports FeatureEdit.Cloth outside Glow mode, so it doesn't trip
        // this — correct, since raising Glow without choosing an effect still cannot make anything emit.
        //
        // A pin with no effect selected also still holds, so Advanced's force-mode radios keep working,
        // and a pin that ALREADY reads as Glow is left alone.
        if (locked)
        {
            if (edited != FeatureEdit.Glow || !canShell || cur == RenderMode.Glow
                || !RenderModeInference.HasGlow(overlays, ovr))
                return false;
            ColorTableEditor.ApplyMode(overlays, ovr, RenderMode.Glow);
            ColorTableEditor.SetManualShaderLock(overlays, ovr, false);
            return true;
        }

        // Leaving Animated glow (the effect was just cleared): drop the Glow those rows carry BEFORE
        // inferring. ApplyGlowTransition zeroes it, but only after this runs — and since Glow now counts
        // as a Cloth feature, the 150% this mode put there itself would otherwise infer Cloth and strand
        // the overlay in a mode the user never asked for, with nothing cloth-like left to justify it.
        // Safe to do unconditionally here: a pinned overlay already returned above, and `edited` proves
        // this frame carries a real edit, so it can't fight an author tuning a Glow-pinned overlay.
        if (cur == RenderMode.Glow && !RenderModeInference.HasGlow(overlays, ovr))
            SetRowsEmissive(rows, 0f);

        var want = RenderModeInference.Infer(rows, overlays, ovr, cur, edited);
        // An option we cannot build a layer for renders as skin — but that is the COMPOSITOR's call to make,
        // every composite, from what it can actually cut (see CompositorService's demotion). The editor must
        // not write the downgrade into the mod.
        //
        // It did, and it was destructive. While the shell builder was body-only, this clamped every face
        // overlay to Skin and SaveMetadata persisted it — silently erasing Layer/Shader/Scroll from all 14
        // overlays of a scale mod whose author had set them to animated glow, with no undo and nothing in
        // the defaults file to restore from (it predates the Layer field). The user's own recorded settings
        // are not ours to rewrite because we currently happen to be unable to honour them; support for the
        // surface can arrive later, and then the setting is right again.
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

    /// <summary>On the transition INTO Animated glow, default every row's Glow to 150% and its glow colour
    /// to white (so the scroll map reads cleanly); on the transition OUT, zero the glow. A no-op when the
    /// mode didn't cross the Glow boundary — so swapping one effect for another keeps the user's tuning.</summary>
    private static void ApplyGlowTransition(List<ColorTableRowPreset> rows, RenderMode before, RenderMode after)
    {
        if (before != RenderMode.Glow && after == RenderMode.Glow)
            SetRowsEmissive(rows, RenderModeInference.GlowEmissive, RenderModeInference.GlowEmissiveColour);
        else if (before == RenderMode.Glow && after != RenderMode.Glow) SetRowsEmissive(rows, 0f);
    }

    /// <summary>Set every existing sub-row's Glow (emissive) to <paramref name="v"/> — 1.0 = 100%, 0 = off —
    /// and, when <paramref name="emissiveColor"/> is given, its glow colour too (white on entering Animated glow,
    /// so the scroll map's own colour reads cleanly). Only touches rows that already exist.</summary>
    private static void SetRowsEmissive(List<ColorTableRowPreset> rows, float v, string? emissiveColor = null)
    {
        foreach (var r in rows)
        {
            if (r.SubRowA != null) { r.SubRowA.Emissive = v; if (emissiveColor != null) r.SubRowA.EmissiveColor = emissiveColor; }
            if (r.SubRowB != null) { r.SubRowB.Emissive = v; if (emissiveColor != null) r.SubRowB.EmissiveColor = emissiveColor; }
        }
    }

    /// <summary>
    /// Which color table rows an index texture actually selects (1-based).
    ///
    /// Counts pixels per row and drops the stragglers: index art is antialiased, so the blend pixels
    /// along every edge sweep the red channel through intermediate values, and a naive scan reports
    /// nearly all 16 rows as "in use". Only rows with real coverage are returned.
    /// </summary>
    /// <summary>
    /// Which color table rows an index texture actually selects (1-based).
    ///
    /// ONLY pixels inside a UV island count. Art tools dilate colour outward past the island edges so
    /// that bilinear filtering doesn't sample background, and that bleed smears the red channel through
    /// values the artist never used — which then read as rows the overlay doesn't have. The game never
    /// samples those pixels; neither should we.
    /// </summary>
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

    // Body types whose transfer map we've already kicked off a background load for, so a redraw-per-frame
    // editor can't queue the same load repeatedly.
    private readonly HashSet<string> _islandWarmupStarted = new(StringComparer.OrdinalIgnoreCase);
    // Set by the background load, consumed on the UI thread — index scans taken WITHOUT the island mask
    // counted padding, so they must be recomputed once the map is available.
    private volatile bool _islandMapArrived;

    /// <summary>
    /// Load a body type's UV transfer map off the UI thread. Opening the colour-set editor must not pay a
    /// ~4K map load just to scan an index texture; this defers it, and flags the scan cache stale so the
    /// row list corrects itself a moment later.
    /// </summary>
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

    /// <summary>
    /// Restore ONE option's settings — its colour rows and its overlays' gear/glow/mode fields — from the
    /// snapshot Proteus recorded before it first wrote to this mod. Other options are left alone. Pass a
    /// null <paramref name="groupName"/> for a simple (no option-group) mod, which restores the top level.
    /// Returns false when there's no snapshot or no matching option, so the caller skips the save.
    /// </summary>
    private bool ResetToDefaults(OverlayEntry entry, string? groupName, OverlayOption? option)
    {
        var defaults = discovery.TryLoadDefaults(entry);
        if (defaults == null) return false;

        // Resolve and validate everything BEFORE mutating anything: the binding clear below is persisted
        // and unrecoverable, so it must never run on a path that then bails out having restored nothing.
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

        // Only now that the base really was restored: a design binding keeps its OWN captured
        // Layer/Shader/colours for this option and re-imposes them on every apply, so the restore would
        // look like nothing happened while that override survives. Other designs keep theirs.
        designBindings.ClearOptionOverride(entry.ModDirectory, groupName, option?.Name);

        // Descriptor Index paths may differ from the edited ones, so the cached row scans are stale.
        foreach (var k in _indexRowCache.Keys.Where(k => k.StartsWith(entry.SidecarRoot)).ToList())
            _indexRowCache.Remove(k);

        Plugin.Log.Information("[Proteus] reset {0}{1} to recorded defaults",
            entry.ModDirectory, groupName == null ? "" : $" [{groupName}/{option!.Name}]");
        return true;
    }

    // Both swaps mutate the live list IN PLACE rather than reassigning: the editor captured these lists
    // into locals before the button was drawn, and the compositor holds them too — handing back a new
    // instance would leave every one of those references pointing at the pre-reset data.
    // `live` is non-null by contract — callers create the list first (`??= []`) so a snapshot's overlays
    // are never silently dropped, which would half-restore the option and then save that.
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

    // Whether each mod has a defaults snapshot. Cached because ResetBlockedReason is evaluated as an
    // argument on EVERY draw, and the uncached answer is a filesystem stat. The snapshot can only appear
    // as a result of our own SaveMetadata, so invalidating there (InvalidateDefaultsCache) is complete.
    private readonly Dictionary<string, bool> _hasDefaultsCache = new(StringComparer.OrdinalIgnoreCase);

    private void InvalidateDefaultsCache(OverlayEntry entry) => _hasDefaultsCache.Remove(entry.SidecarRoot);

    /// <summary>
    /// Recomposite because something changed on THIS overlay mod — but only if the mod is switched on.
    /// <para/>
    /// A disabled mod paints nothing, so editing its colours or its masks, restacking its options, changing
    /// its priority, its AO choice or its sibling mode cannot move a pixel of the character; a 5-7s rebuild
    /// and a redraw for it is pure waste. The edit is still saved by the caller, and enabling the mod fires
    /// its own composite that picks it up. Every entry-scoped trigger in this window goes through here —
    /// the two glow warmups reach the same rule inline, since they must not spend their one-shot on a
    /// composite that will not run. This is the same rule <c>CompositorService.OnModSettingChanged</c> applies to
    /// Penumbra's own setting events — both entry points into "the user changed something on a mod that is
    /// off" have to agree, or the one that doesn't becomes the reason the rebuild still happens.
    /// <para/>
    /// Deliberately NOT used for the enable toggle: that one is the transition itself.
    /// </summary>
    private void RecompositeForOverlay(OverlayEntry entry, string reason, int delayMs = 200,
                                       bool skinFingerprintAuthoritative = false)
    {
        if (!entry.Enabled) return;
        compositor.TriggerRecomposite(reason, delayMs,
            skinFingerprintAuthoritative: skinFingerprintAuthoritative);
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
    /// The colour-table cell an overlay's art can actually reach: the rows to leave live in the picker, and
    /// the column, or nulls for "no idea, leave everything alone".
    /// <para/>
    /// The case this exists for is an overlay with NO index texture, which is most of them — every
    /// single-plateau Atramentum Luminis import, every emissive-skin import, every tattoo the Create tab
    /// makes. A shell without an <c>_id</c> samples the index SecondSkinService fabricates, (255, 255, 0),
    /// which is row pair <see cref="GlowShell.Row"/> sub-row A and nothing else. The panel has always SAID
    /// so in prose (<c>ColorPanel.NoIndexTexture</c>) while handing the picker a null filter, so all sixteen
    /// rows drew as live and the cursor opened on row 1 — the one row that provably does nothing. Someone
    /// looking for the glow controls edited row 1, saw no change, and concluded the feature was missing.
    /// <para/>
    /// This is the fix the CONTENT editor already got, and the comment above ContentIndexFor describes the
    /// same contradiction being found there first. Fabricated at the call site rather than inside
    /// <c>ColorTableEditor.DrawRows</c>, because null has to keep meaning UNKNOWN: an index that would not
    /// decode, or a material with no colour table, must dim nothing at all rather than assert a fact about a
    /// file nothing has read.
    /// </summary>
    /// <param name="hasAnyIndex">Whether any overlay or active mask names an index at all.</param>
    /// <param name="scanned">The union of every scan's rows. Empty means the files were named and read and
    /// selected nothing — which is not the same as there being none.</param>
    private static (HashSet<int>? Rows, string? Column) OverlayRowFilter(
        bool hasAnyIndex, HashSet<int> scanned, string? column)
        => !hasAnyIndex ? ([GlowShell.Row], ShellDefaultColumn)
         : scanned.Count > 0 ? (scanned, column)
         : (null, null);

    /// <summary>The sub-row a shell with no <c>_id</c> lands in: the fabricated index's green is 255, which
    /// is column A. Spelled the way <see cref="ContentIndexTexture.Scan.SubRow"/> spells it.</summary>
    private const string ShellDefaultColumn = "A";

    /// <summary>
    /// One column out of several scans: kept only while every scan that has an opinion agrees on it.
    /// A scan that read nothing, or one that genuinely uses both columns, reports null and takes the whole
    /// answer with it — the conservative direction, since the cost of being wrong is a control dimmed for a
    /// cell that does render.
    /// </summary>
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

    /// <summary>
    /// Which colour-table rows an overlay's own <c>_id</c> art selects, and which column they land in.
    /// <para/>
    /// The decode and the island fetch only — the rules live in
    /// <see cref="ContentIndexTexture.ReadOverlay"/>, which states how they differ from the ones this
    /// file's content editor reads a PACK's index by, and is where they can be tested. They are worth
    /// testing: the editor DIMS what this does not name, so a binning one row out no longer merely
    /// mislabels a row, it puts the working one behind a dimmed button.
    /// </summary>
    private ContentIndexTexture.Scan ScanIndexFile(string absolutePath, string? bodyType)
    {
        try
        {
            using var stream = File.OpenRead(absolutePath);
            var img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

            // Mask to UV islands so padding (art tools bleed colour outside the islands) isn't counted as
            // real coverage. The ~4K transfer map must NOT be loaded synchronously here though — that would
            // stall the first draw of the editor. So take the map only if it's already in memory, and
            // otherwise kick off a background load; when it lands, the scan cache is dropped and the rows
            // are recomputed accurately. Until then we simply count every pixel.
            int islandW = 0, islandH = 0;
            bool[]? island = bodyType != null ? uvRemap.IslandMask(bodyType, out islandW, out islandH, loadIfMissing: false) : null;
            if (islandW == 0 || islandH == 0) island = null;
            if (island == null && bodyType != null) StartIslandMaskWarmup(bodyType);

            return ContentIndexTexture.ReadOverlay(img.Data, img.Width, img.Height, island, islandW, islandH);
        }
        catch
        {
            // Unreadable is UNKNOWN, and an empty scan is how that is spelled: OverlayRowFilter turns it
            // into a null filter, so nothing is dimmed on the strength of a file nothing could read.
            return new([], null);
        }
    }
}
