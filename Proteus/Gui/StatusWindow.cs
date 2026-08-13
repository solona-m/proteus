using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Proteus.Interop;
using Proteus.Services;
using StbImageSharp;

namespace Proteus.Gui;

public class StatusWindow : Window
{
    private readonly CompositorService compositor;
    private readonly SidecarDiscoveryService discovery;
    private readonly PenumbraBridge penumbra;
    private readonly Configuration config;
    private readonly DesignBindingService designBindings;
    private readonly UVMapDownloadService uvMapDl;
    private readonly UVRemapService uvRemap;
    private readonly ModCreationService modCreation;

    // Accent used to flag an active design binding (and the mods/colors it drives).
    private static readonly Vector4 BindingAccent = new(0.45f, 0.75f, 1f, 1f);

    // Indexed by (int)SiblingSynthesisMode: Off=0, BiboGen3Only=1, AllBodies=2.
    private static readonly string[] SiblingModeLabels = { "Off", "bibo+gen3", "All bodies" };

    // Key: absolute index-texture path → 1-based row numbers that appear in it.
    // Cleared per-entry on each popup open so option switches are reflected.
    private readonly Dictionary<string, HashSet<int>> _indexRowCache = new();
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
    private bool _createMaterialLocked;   // stop auto-detecting once we have a real body (or the user edits)
    private string _createMaterialAuto = "";  // the value we last auto-filled, to tell a user edit apart
    private long _createDetectNextTick;   // throttle the detect poll while the character isn't drawn yet
    private string? _createStatus;        // last create result message
    private bool _createStatusOk;

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
        ModCreationService modCreation)
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

        SizeConstraints = new WindowSizeConstraints
        {
            // Wide enough for the mod table, so switching to the sparser Bindings/Settings tabs
            // doesn't shrink the window (it's AlwaysAutoResize).
            MinimumSize = new System.Numerics.Vector2(520, 80),
            MaximumSize = new System.Numerics.Vector2(1100, 700),
        };
    }

    public override void Draw()
    {
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

        // Status — not controls — so it stays outside the tabs and is visible from any of them.
        DrawStatusBanner();

        DrawDiscordButton();

        using (var tabs = ImRaii.TabBar("##proteusTabs"))
        {
            if (tabs)
            {
                using (var t = ImRaii.TabItem("Mods"))
                    if (t) DrawModsTab();

                using (var t = ImRaii.TabItem("Bindings"))
                    if (t) DrawBindingsTab();

                using (var t = ImRaii.TabItem("Create"))
                    if (t) DrawCreateTab();

                using (var t = ImRaii.TabItem("Settings"))
                    if (t) DrawSettingsTab();
            }
        }

        ImGui.Separator();
        DrawLastResult();

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
        // Wide enough for the 16 row buttons to sit 8-across on two lines.
        ImGui.SetNextWindowSize(new Vector2(720, 580), ImGuiCond.FirstUseEver);
        // Narrow enough and the row picker wraps; this just stops it collapsing to something useless.
        ImGui.SetNextWindowSizeConstraints(new Vector2(400, 300), new Vector2(float.MaxValue, float.MaxValue));
        if (ImGui.Begin($"Colors — {entry.ModName}###ProteusColors", ref open))
            DrawColorEditor(entry);
        ImGui.End();

        if (!open) _colorWindowMod = null;
    }

    private const string DiscordUrl = "https://discord.gg/solona";

    /// <summary>A right-aligned Discord link that sits in the top-right of the window, level with the tab bar.</summary>
    private void DrawDiscordButton()
    {
        const string label = "Discord";
        var  style = ImGui.GetStyle();
        float width = ImGui.CalcTextSize(label).X + style.FramePadding.X * 2;

        // Right-align to the content region, then re-anchor the cursor so the tab bar draws on this same line.
        float startX = ImGui.GetCursorPosX();
        float startY = ImGui.GetCursorPosY();
        float avail  = ImGui.GetContentRegionAvail().X;
        if (avail > width)
            ImGui.SetCursorPosX(startX + avail - width);

        using (ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.35f, 0.40f, 0.95f, 1f))
                     .Push(ImGuiCol.ButtonHovered,      new Vector4(0.45f, 0.50f, 1.00f, 1f))
                     .Push(ImGuiCol.ButtonActive,       new Vector4(0.30f, 0.35f, 0.85f, 1f)))
        {
            if (ImGui.Button(label))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DiscordUrl) { UseShellExecute = true }); }
                catch { /* opening a browser is best-effort */ }
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(DiscordUrl);

        // Let the tab bar share this row rather than dropping below the button.
        ImGui.SameLine();
        ImGui.SetCursorPos(new Vector2(startX, startY));
    }

    private void DrawStatusBanner()
    {
        bool any = false;

        if (uvMapDl.State == UVMapDownloadState.Downloading)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), uvMapDl.StatusMessage);
            any = true;
        }
        else if (uvMapDl.State == UVMapDownloadState.Failed)
        {
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), uvMapDl.StatusMessage);
            ImGui.SameLine();
            if (ImGui.Button("Retry"))
                uvMapDl.EnsureMapsAsync();
            any = true;
        }

        if (!penumbra.IsAvailable)
        {
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), "Penumbra unavailable");
            any = true;
        }

        if (any)
            ImGui.Separator();
    }

    private void DrawLastResult()
    {
        var result = compositor.LastResult;
        if (result == null)
        {
            ImGui.TextDisabled("No composite result yet.");
            return;
        }

        if (!result.Success)
        {
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), $"Error: {result.ErrorMessage ?? "unknown"}");
            return;
        }

        var elapsed = DateTime.UtcNow - result.Timestamp;
        var timeStr = elapsed.TotalSeconds < 60
            ? $"{elapsed.TotalSeconds:F1}s ago"
            : $"{elapsed.TotalMinutes:F0}m ago";

        ImGui.TextDisabled($"Last composite: {timeStr}   " +
                           $"{result.TexturesPatched} texture{(result.TexturesPatched != 1 ? "s" : "")} patched   " +
                           $"{result.OverlayModsUsed} mod{(result.OverlayModsUsed != 1 ? "s" : "")}");
    }

    private void DrawSettingsTab()
    {
        var enabled = config.PluginEnabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            config.PluginEnabled = enabled;
            config.Save();
            compositor.SetEnabled(enabled);   // clears output, redraws, then toggles the Penumbra mod
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Turning this off clears Proteus's output, redraws you without it,\n" +
                             "and disables the managed \"Proteus\" mod in Penumbra.");

        var disableRedraw = config.DisableAutoRedraw;
        if (ImGui.Checkbox("Disable auto redraw", ref disableRedraw))
        {
            config.DisableAutoRedraw = disableRedraw;
            config.Save();
        }

        var skipUnchanged = config.SkipUnchangedComposites;
        if (ImGui.Checkbox("Skip unchanged recomposites", ref skipUnchanged))
        {
            config.SkipUnchangedComposites = skipUnchanged;
            config.Save();
            compositor.TriggerRecomposite("skip-gate-toggle");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Let a recomposite triggered by zoning or a redraw stop early when nothing that\n" +
                             "affects the output has changed. Anything you change yourself always recomposites.\n" +
                             "Turn off only to rule this out when an edit isn't taking effect.");

        var inPlaceReload = config.UseInPlaceReload;
        if (ImGui.Checkbox("In-place reload", ref inPlaceReload))
        {
            config.UseInPlaceReload = inPlaceReload;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Refresh textures via Glamourer's in-place equipment reload instead of a full\n" +
                "redraw, avoiding the despawn/respawn flicker. Falls back to a full redraw\n" +
                "automatically when Glamourer can't service it.");

        var enableCompression = config.EnableCompression;
        if (ImGui.Checkbox("Enable Compression", ref enableCompression))
        {
            config.EnableCompression = enableCompression;
            config.Save();
            // Re-encode existing output in the new format.
            compositor.TriggerRecomposite("compression-toggle");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Block-compress the baked textures (BC7), cutting each to about a quarter of its\n" +
                             "uncompressed size on disk and in VRAM. The index texture stays uncompressed to keep\n" +
                             "its exact row values. Off = uncompressed (byte-identical to before).");

        var cutoutAlpha = config.GearCutoutAlpha;
        if (ImGui.Checkbox("Sharp alpha (gpose sphere/metal)", ref cutoutAlpha))
        {
            config.GearCutoutAlpha = cutoutAlpha;
            config.Save();
            compositor.TriggerRecomposite("cutout-alpha-toggle");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "EXPERIMENTAL. Renders shell coverage as a hard alpha-test cutout instead of smooth\n" +
                "transparency, so sphere maps and metalness survive gpose (which drops them on\n" +
                "transparent surfaces). Trade-off: sheer edges become hard/aliased. Best for\n" +
                "mostly-opaque fabrics; a very sheer fabric will look coarse. Recomposite after toggling.");

        bool autoGlasses = config.AutoInvisibleGlasses;
        if (ImGui.Checkbox("Host on invisible glasses (keep rings free)", ref autoGlasses))
        {
            config.AutoInvisibleGlasses = autoGlasses;
            config.Save();
            // Recomposite so the injection/removal reconciles now (turning it off pulls the glasses).
            compositor.TriggerRecomposite("auto-glasses-toggle");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When on and you have no glasses equipped, Proteus has Glamourer equip an\n" +
                             "invisible glasses item so the second skin rides the facewear slot instead of a\n" +
                             "ring. This writes a (hidden) bonus item to your Glamourer state; it's removed\n" +
                             "when you disable Proteus, equip real glasses, or turn this off.");

        bool autoRing = config.AutoEmperorRing;
        if (ImGui.Checkbox("Host on the Emperor's New Ring when nothing else can", ref autoRing))
        {
            config.AutoEmperorRing = autoRing;
            config.Save();
            // Recomposite so the injection/removal reconciles now (turning it off pulls the ring).
            compositor.TriggerRecomposite("auto-ring-toggle");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When on and nothing you wear can host the second skin, Proteus has Glamourer\n" +
                             "equip the invisible Emperor's New Ring in your FREE right ring slot and hosts\n" +
                             "there. A ring you're already wearing is never taken. Removed when you disable\n" +
                             "Proteus or turn this off.");

        // The second skin rides on an equipped ring/bracelet (its model is redirected to our merged
        // shell). An in-place reload can't reload that .mdl, so if a shell ever gets stuck on the
        // accessory this forces a full redraw to reload the accessory's original model.
        if (ImGui.Button("Restore changed accessory"))
            compositor.RestoreChangedAccessory();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force a full redraw to reload any ring/bracelet the second skin replaced,\n" +
                "restoring it to its original model. Use if a gear shell stays stuck on an\n" +
                "accessory after disabling or swapping.");

        // Escape hatch for a stale texture: Proteus caches decoded textures keyed by file
        // timestamp + size, so a mod edit that preserves both can keep showing the old image
        // until this drops the cache and recomposites. Mod toggles/reinstalls evict automatically.
        ImGui.SameLine();
        if (ImGui.Button("Clear texture cache"))
            compositor.ClearTextureCacheAndRecomposite();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Drop all cached decoded textures and recomposite now. Use if a texture edit\n" +
                "isn't showing up — e.g. you re-exported an overlay at the same size and the\n" +
                "change won't appear without restarting the plugin.\n\n" +
                "Also re-derives which mod each base skin texture comes from — use this if the\n" +
                "Base skin below names the wrong mod.");

        // WHICH skin the overlays are painted onto. Nothing else surfaces this, and a composite built on
        // the wrong body mod looks like a perfectly good composite of a skin you didn't pick — so the
        // failure is invisible unless the source is named somewhere.
        var upstreams = compositor.BaseUpstreams();
        if (upstreams.Count > 0 && ImGui.CollapsingHeader($"Base skin ({upstreams.Count})"))
        {
            foreach (var (gamePath, source, settled) in upstreams)
            {
                if (settled)
                    ImGui.TextUnformatted($"{source}");
                else
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), $"{source} (unconfirmed)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(gamePath);
            }
            ImGui.TextDisabled("The mod each base texture is read from. Hover for the game path.");
        }

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
            ImGui.SameLine();
            if (ImGui.Button("Glow Effect Textures"))
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(lib) { UseShellExecute = true }); }
                catch { /* no file manager — the path is in the tooltip anyway */ }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Open the folder Proteus reads animated-glow scroll maps from — the \"_o\"\n" +
                                 "textures that ARE the glow. Anything dropped in here appears in every\n" +
                                 "gear overlay's Effect dropdown.\n\n" +
                                 $"{lib}\n\n" +
                                 "Accepts .tex, .dds, .png, .jpg, .bmp, .tga, .psd and .gif.\n" +
                                 "A mod's own Proteus/Effects/ folder takes precedence over it.");
        }

        // Skin-tint suppression strength (global multiplier). The per-pixel amount is weighted by
        // overlay color: bright dyes get de-tinted, dark dyes are left skin-tinted and matte.
        ImGui.SetNextItemWidth(140);
        float skinSup = config.SkinColorSuppression;
        if (ImGui.SliderFloat("Skin-tint suppression", ref skinSup, 0f, 1f, "%.2f"))
            config.SkinColorSuppression = Math.Clamp(skinSup, 0f, 1f);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save();
            compositor.TriggerRecomposite("skin-suppression");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "How strongly overlays resist skin-tone tinting (global multiplier).\n" +
                "Applied per pixel by color: white/bright dyes keep their authored color on any\n" +
                "skin tone (slightly shinier), dark dyes stay skin-tinted and matte automatically.\n" +
                "0.00 disables it entirely (original look).");

        // Ambient-occlusion contact shadow baked onto the skin around masked strap edges.
        ImGui.SetNextItemWidth(140);
        float aoStr = config.AmbientOcclusionStrength;
        if (ImGui.SliderFloat("Ambient occlusion", ref aoStr, 0f, 2f, "%.2f"))
            config.AmbientOcclusionStrength = Math.Clamp(aoStr, 0f, 2f);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save();
            compositor.TriggerRecomposite("ambient-occlusion");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Soft contact shadow on the skin just outside masked strap edges, giving straps depth.\n" +
                "0.00 disables it entirely (skin diffuse unchanged).");

        ImGui.SetNextItemWidth(140);
        float aoSoft = config.AmbientOcclusionSoftness;
        if (ImGui.SliderFloat("Shadow softness", ref aoSoft, 0.001f, 0.005f, "%.3f"))
            config.AmbientOcclusionSoftness = Math.Clamp(aoSoft, 0.001f, 0.005f);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save();
            compositor.TriggerRecomposite("ambient-occlusion-softness");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "How far the ambient-occlusion shadow spreads from a strap edge (fraction of texture width).\n" +
                "Larger = wider, softer shadow. Shared by the shadow and the strap indent.");

        ImGui.SetNextItemWidth(140);
        float aoNrm = config.AmbientOcclusionNormalDepth;
        if (ImGui.SliderFloat("Skindenting", ref aoNrm, 0f, 10f, "%.2f"))
            config.AmbientOcclusionNormalDepth = Math.Clamp(aoNrm, 0f, 10f);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save();
            compositor.TriggerRecomposite("ambient-occlusion-normal");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Indents the skin normal at strap/garment edges so straps look pressed into the skin.\n" +
                "0.00 disables it (skin normal unchanged). Uses the same edges/softness as the shadow.");

        ImGui.SetNextItemWidth(140);
        int cacheMb = config.DecodeCacheBudgetMb;
        if (ImGui.SliderInt("Texture cache (MB)", ref cacheMb, 512, 4096))
            config.DecodeCacheBudgetMb = Math.Clamp(cacheMb, 512, 4096);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save();
            compositor.ApplyDecodeCacheBudget();   // live — lowering it reclaims on the spot, no restart
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "How much decoded texture data Proteus keeps in memory between composites.\n\n" +
                "A 4K texture costs 64 MB decoded, so this is really a count: 2048 MB ≈ 30 of them.\n" +
                "It only helps if it covers a whole composite's worth — below that, every run evicts\n" +
                "what the next one needs and nothing is reused.\n\n" +
                "Check the \"cache N entries, M MB\" figure in the recomposite log: if a SECOND\n" +
                "composite with nothing changed still reports misses, raise this. Lower it if the\n" +
                "game starts paging. Released automatically after 60s idle.");

        // Hide a body's redundant connector meshes on the gear shell (see Configuration).
        var connMode = config.HideConnectorMeshes;
        ImGui.SetNextItemWidth(140);
        if (ImGui.BeginCombo("Hide Connector Meshes", connMode.ToString()))
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
            ImGui.SetTooltip(
                "Skip each body part's connector ring on the gear \"second skin\" — the small extra\n" +
                "submesh at a joint (wrist/ankle/…). Some bodies (Neolithe) reinforce joints with a ring\n" +
                "that overlaps an already-complete body; on a sheer overlay the overlap doubles up and\n" +
                "shows as a more-opaque seam. Leave Off for other bodies — there that submesh is real\n" +
                "skin, and hiding it would leave gaps.");
    }

    /// <summary>Author a basic skin-overlay mod: name + author + up to three textures → a new Penumbra mod.</summary>
    private void DrawCreateTab()
    {
        ImGui.TextWrapped("Make a basic Proteus overlay mod. Pick at least one texture; Proteus writes a " +
            "new Penumbra mod and opens it so you can enable and tweak it.");
        ImGui.Separator();

        ImGui.InputText("Mod name", ref _createName, 128);
        ImGui.InputText("Author", ref _createAuthor, 128);

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
        ImGui.InputText("Material target", ref _createMaterial, 256);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The material this overlay composites onto. Auto-filled from the body you're\n" +
                "currently wearing; pick one you have equipped from the list, or type a path by hand to\n" +
                "target a body/race you aren't wearing right now.");

        // Equipped-material picker. NoPreview renders just the arrow, so this reads as a companion to the
        // text box above rather than a second, competing field — ImGui has no editable combo.
        ImGui.SameLine();
        // The width constraint is required BECAUSE of NoPreview: the popup would otherwise inherit the
        // arrow's width and clip every label to nothing. The height has to be capped here too — BeginCombo
        // applies its own constraint (and with it the ImGuiComboFlags.Height* row cap) ONLY when the caller
        // supplied none, so passing a width constraint silently disables that path. Without this the popup
        // grows one row per equipped material, past the window on a full wardrobe.
        var pickerMaxH = ImGui.GetTextLineHeightWithSpacing() * 20 + ImGui.GetStyle().WindowPadding.Y * 2;
        ImGui.SetNextWindowSizeConstraints(new Vector2(460, 0), new Vector2(760, pickerMaxH));
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
                ImGui.TextDisabled("Showing the last known list — character isn't drawn.");

            if (items == null || items.Count == 0)
            {
                ImGui.TextDisabled("No equipped materials known yet.\nZone in or redraw, then reopen this list.");
            }
            else
            {
                if (items[0].Skin) ImGui.TextDisabled("Skin");
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
        if (ImGui.SmallButton("Re-detect"))
        {
            _createMaterial = _createMaterialAuto = modCreation.DetectBodyMaterial()
                ?? modCreation.CachedBodyMaterial() ?? ModCreationService.DefaultBodyMaterial;
            _createMaterialLocked = true;   // explicit request — take this value and stop polling
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-run body detection and overwrite the field.");

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
        DrawTextureRow("Diffuse", ref _createDiffuse, SlotEnabled("Diffuse"), "This material has no diffuse texture.");
        DrawTextureRow("Mask", ref _createMask, SlotEnabled("Mask"), "This material has no mask texture.");
        DrawTextureRow("Normal", ref _createNormal, SlotEnabled("Normal"), "This material has no normal texture.");
        DrawTextureRow("Index", ref _createIndex, SlotEnabled("Index"),
            "This material has no index texture, and it isn't skin or face — nothing here\n" +
            "would read a colour-table row selector.");
        // Only once a read has actually been ATTEMPTED for this exact material (_createSlotsFor tracks
        // that). Testing _createSlots alone announced a failure during the window before the read ran,
        // so the note flashed on every material change and then corrected itself.
        if (_createSlots == null && _createSlotsFor == _createMaterial && _createMaterial.Length > 0)
            ImGui.TextDisabled("Couldn't read this material — offering every slot.");

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

        using (ImRaii.Disabled(!valid))
            if (ImGui.Button("Create"))
            {
                var r = modCreation.Create(
                    _createName, _createAuthor, _createMaterial,
                    SlotEnabled("Diffuse") ? NullIfEmpty(_createDiffuse) : null,
                    SlotEnabled("Mask")    ? NullIfEmpty(_createMask)    : null,
                    SlotEnabled("Normal")  ? NullIfEmpty(_createNormal)  : null,
                    SlotEnabled("Index")   ? NullIfEmpty(_createIndex)   : null);
                _createStatus = r.Message;
                _createStatusOk = r.Ok;
                if (r.Ok)   // keep name/author/material for a quick second mod; clear the pickers
                    _createDiffuse = _createMask = _createNormal = _createIndex = "";
            }
        if (!valid && ImGui.IsItemHovered())
            ImGui.SetTooltip("Enter a mod name, a material target, and pick at least one texture.");

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
    private void DrawTextureRow(string label, ref string path, bool enabled = true, string? disabledReason = null)
    {
        // Dims the row's TEXT, which ImRaii.Disabled alone wouldn't reach — the label and file name sit
        // outside the disabled scope so their tooltips stay hoverable. The Browse button below is disabled
        // properly; this is presentation only.
        using var dim = ImRaii.PushStyle(ImGuiStyleVar.Alpha,
            ImGui.GetStyle().Alpha * (enabled ? 1f : 0.5f));

        // A disabled item reports no hover under the default flags, so every "why is this off" tooltip
        // below asks with AllowWhenDisabled — otherwise the explanation is unreachable precisely when it
        // is needed.
        void ReasonTooltip()
        {
            if (!enabled && disabledReason != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(disabledReason);
        }

        var shown = path.Length == 0 ? "(none)" : Path.GetFileName(path);
        ImGui.TextUnformatted($"{label}:");
        ReasonTooltip();
        ImGui.SameLine(90);
        ImGui.TextUnformatted(enabled ? shown : "(not used by this material)");

        ImGui.SameLine(360);
        // Capture the field by a local setter — ref can't cross the dialog callback. The label string is
        // load-bearing here: this switch is how the picked path reaches the field. Don't rename them.
        var captured = label;
        // ImRaii.Disabled, NOT `enabled && SmallButton(...)`: the short-circuit would skip submitting the
        // button entirely, and an unsubmitted item can't be hovered, so the reason tooltip would vanish.
        // This keeps the item in the layout, blocks the click properly, and still reads as pressable-off
        // rather than swallowing a press that looks like it landed.
        using (ImRaii.Disabled(!enabled))
        {
            if (ImGui.SmallButton($"Browse##{label}"))
            {
                _fileDialog.OpenFileDialog(
                    $"Select {label} texture", "Images{.png,.tex,.dds,.jpg,.jpeg,.bmp,.tga}",
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
        ReasonTooltip();
        if (enabled && path.Length > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Clear##{label}"))
                path = "";
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

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

    /// <summary>Short "what is this" tag for a material path, the picker's left column. Path-derived only —
    /// no game data, so it's free to call while drawing.</summary>
    private static string SlotHint(string p)
    {
        // Weapons FIRST: chara/weapon/w0801/obj/body/b0006/... carries an /obj/body/ segment of its own, so
        // testing that first would tag an equipped greatsword "Body" — and it sorts into the non-skin group,
        // making a "Body" row appear below the Skin separator.
        if (p.Contains("chara/weapon/", StringComparison.OrdinalIgnoreCase)) return "Weapon";
        // Then /obj/<slot>/ ahead of chara/equipment/: a body mod can route body-UV skin through an
        // equipment slot, and that should still read as Body rather than Gear.
        if (p.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase)) return "Body";
        if (p.Contains("/obj/face/", StringComparison.OrdinalIgnoreCase)) return "Face";
        if (p.Contains("/obj/hair/", StringComparison.OrdinalIgnoreCase)) return "Hair";
        if (p.Contains("/obj/tail/", StringComparison.OrdinalIgnoreCase)) return "Tail";
        if (p.Contains("/obj/zear/", StringComparison.OrdinalIgnoreCase)) return "Ear";
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
        if (ImGui.Button("Refresh"))
            compositor.TriggerRecomposite("manual");

        ImGui.Separator();

        // ── Overlay mod list ─────────────────────────────────────────────────
        // The list comes from the last composite, so while the plugin is off it stays empty — say so
        // rather than claiming there are no sidecar mods.
        var mods = compositor.LastDiscovered;
        if (!config.PluginEnabled)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "Proteus is disabled — enable it in Settings.");
        }
        else if (mods.Count == 0)
        {
            ImGui.TextDisabled("No Proteus sidecar mods detected.");
        }
        else
        {
            // EndTable is only legal when BeginTable returned true — it returns false when the table is
            // skipped (clipped away, or the host window isn't rendering), and calling EndTable anyway
            // trips an ImGui assertion in debug and corrupts table state in release.
            if (!ImGui.BeginTable("##mods", 6, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
                return;
            ImGui.TableSetupColumn("On",     ImGuiTableColumnFlags.WidthFixed, 32);
            ImGui.TableSetupColumn("Mod",    ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pri",    ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Colors", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Bodies", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Skindent", ImGuiTableColumnFlags.WidthFixed, 78);

            // Clickable sort headers for Enabled / Mod / Priority (the rest are plain). Clicking the active
            // column flips direction; switching column picks a sensible default direction.
            void SortableHeader(string label, ModSort col)
            {
                ImGui.TableNextColumn();
                var arrow = _modSort == col ? (_modSortDesc ? " ▼" : " ▲") : "";
                ImGui.TableHeader(label + arrow);
                if (ImGui.IsItemClicked())
                {
                    if (_modSort == col) _modSortDesc = !_modSortDesc;
                    else { _modSort = col; _modSortDesc = col != ModSort.Name; }   // Name asc, others desc
                }
            }
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            SortableHeader("On",  ModSort.Enabled);
            SortableHeader("Mod", ModSort.Name);
            SortableHeader("Pri", ModSort.Priority);
            ImGui.TableNextColumn(); ImGui.TableHeader("Colors");
            ImGui.TableNextColumn(); ImGui.TableHeader("Bodies");
            ImGui.TableNextColumn(); ImGui.TableHeader("Skindent");

            // Enable/priority controls write straight through to Penumbra (Proteus keeps no
            // override state of its own); both reflect the mod's live Penumbra values.
            var collId = penumbra.GetPlayerCollectionId();

            // Display-only sort of a COPY (never mutate LastDiscovered). Sorts by the committed
            // entry.Priority, not the in-progress _priorityEdits value, so a row won't jump mid-drag.
            IOrderedEnumerable<OverlayEntry> ordered = _modSort switch
            {
                ModSort.Enabled => _modSortDesc ? mods.OrderByDescending(e => e.Enabled) : mods.OrderBy(e => e.Enabled),
                ModSort.Name    => _modSortDesc ? mods.OrderByDescending(e => e.ModName, StringComparer.OrdinalIgnoreCase)
                                                : mods.OrderBy(e => e.ModName, StringComparer.OrdinalIgnoreCase),
                _               => _modSortDesc ? mods.OrderByDescending(e => e.Priority) : mods.OrderBy(e => e.Priority),
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

                // Mod name (dimmed when disabled)
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
                ImGui.SetNextItemWidth(55);
                if (ImGui.DragInt($"##pri_{entry.ModDirectory}", ref pri, 0.1f))
                    _priorityEdits[entry.ModDirectory] = pri;
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    _priorityEdits.Remove(entry.ModDirectory);
                    if (collId.HasValue)
                        penumbra.SetModPriority(collId.Value, entry.ModDirectory, pri);
                    // Live edit only (see enable toggle above); folded into the binding via the button.
                    compositor.TriggerRecomposite("penumbra-priority");
                }

                // Colors button. Opens a real window (not a popup) so it survives clicking away —
                // colour work means going back and forth with the game, and a popup dies on any
                // click outside it. Tinted when a design binding is driving this mod's colours.
                ImGui.TableNextColumn();
                bool bindingDriven = designBindings.IsOverrideActiveFor(entry.ModDirectory);
                using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(BindingAccent with { W = 0.45f }), bindingDriven))
                {
                    if (ImGui.Button($"Colors##{entry.ModDirectory}"))
                        _colorWindowMod = _colorWindowMod == entry.ModDirectory ? null : entry.ModDirectory;
                }
                if (bindingDriven && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Colors are driven by the active design binding.\nEdits preview live; click \"Update binding\" to save them. Base colors are unchanged.");

                // Sibling-synthesis mode (which body types to generate for this mod).
                ImGui.TableNextColumn();
                var mode = config.SiblingModeFor(entry.ModDirectory);
                ImGui.SetNextItemWidth(105);
                if (ImGui.BeginCombo($"##bodies_{entry.ModDirectory}", SiblingModeLabels[(int)mode]))
                {
                    foreach (var opt in new[] { SiblingSynthesisMode.AllBodies, SiblingSynthesisMode.BiboGen3Only, SiblingSynthesisMode.Off })
                    {
                        if (ImGui.Selectable(SiblingModeLabels[(int)opt], opt == mode) && opt != mode)
                        {
                            config.SiblingSynthesis[entry.ModDirectory] = opt;
                            config.Save();
                            compositor.TriggerRecomposite("sibling-mode");
                        }
                    }
                    ImGui.EndCombo();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Which body types to synthesize for this mod:\n" +
                        "All bodies = sibling body (bibo↔gen3/Eve) + vanilla (gen2)\n" +
                        "bibo+gen3 = bake to the sibling body only (default)\n" +
                        "Off = no synthesis");

                // Ambient occlusion + Skindenting for this mod (OFF unless the pack asks). THREE states —
                // "the pack decides", "forced on", "forced off" — so this is a combo, like the Bodies column
                // beside it, and not a checkbox: a checkbox can't show the difference between "ticked
                // because the pack asked" and "ticked because you said so", and offers nowhere to put the
                // third state except a hidden modifier gesture.
                ImGui.TableNextColumn();
                bool? aoDeclared = entry.Metadata?.AmbientOcclusion;
                // The user's stored opinion: the new override, else a legacy opt-out, else none.
                bool? aoChoice = config.AmbientOcclusionOverrides.TryGetValue(entry.ModDirectory, out var aoUser)
                    ? aoUser
                    : config.AmbientOcclusionDisabledMods.Contains(entry.ModDirectory) ? false : null;
                string aoPackLabel = $"Pack ({(aoDeclared == true ? "on" : "off")})";
                ImGui.SetNextItemWidth(74);
                if (ImGui.BeginCombo($"##ao_{entry.ModDirectory}",
                        aoChoice == null ? aoPackLabel : aoChoice.Value ? "On" : "Off"))
                {
                    foreach (var (label, choice) in new[] { (aoPackLabel, (bool?)null), ("On", true), ("Off", false) })
                    {
                        if (!ImGui.Selectable(label, choice == aoChoice) || choice == aoChoice) continue;
                        if (choice == null) config.AmbientOcclusionOverrides.Remove(entry.ModDirectory);
                        else                config.AmbientOcclusionOverrides[entry.ModDirectory] = choice.Value;
                        // The legacy opt-out set is read-only now, and any of these three choices replaces
                        // it — leaving it would let it contradict the selection the user just made.
                        config.AmbientOcclusionDisabledMods.Remove(entry.ModDirectory);
                        config.Save();
                        compositor.TriggerRecomposite("ambient-occlusion-mod");
                    }
                    ImGui.EndCombo();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Ambient-occlusion shadow + Skindenting normal indent for this mod's\n" +
                        "straps/garment edges. It treats coverage as cloth pressed into skin, which is\n" +
                        "wrong for tattoos and skin details — so it is off unless asked for.\n\n" +
                        $"{aoPackLabel} = whatever the pack declares (\"AmbientOcclusion\" in its\n" +
                        "metadata.json; absent means off).\n" +
                        "On / Off = your own setting for this mod, overriding the pack.\n\n" +
                        "(The global strength sliders are in Settings.)");
            }

            ImGui.EndTable();
        }
    }

    private void DrawBindingsTab()
    {
        bool bindEnabled = config.DesignBindingEnabled;
        if (ImGui.Checkbox("Bind Proteus state to Glamourer designs", ref bindEnabled))
        {
            config.DesignBindingEnabled = bindEnabled;
            config.Save();
            // Turning the feature off drops any active override immediately so colors fall back
            // to metadata, rather than lingering until the next design application.
            if (!bindEnabled)
                designBindings.ClearColorOverride();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When on, saving a Glamourer design snapshots the current Proteus state.\n" +
                             "Applying that design later restores it (best-effort gear match).");

        ImGui.Indent();
        using (ImRaii.Disabled(!bindEnabled))
        {
            bool followAutomation = config.DesignBindingFollowsAutomation;
            if (ImGui.Checkbox("Follow Glamourer automation (gearset / job changes)", ref followAutomation))
            {
                config.DesignBindingFollowsAutomation = followAutomation;
                config.Save();
                // No ClearColorOverride here, unlike the parent: this path only ever restores a binding,
                // so switching it off has nothing to undo.
            }
            // AllowWhenDisabled: without it the tooltip is unreachable exactly when it is most wanted —
            // greyed out because the parent toggle is off, with nothing to explain why.
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Glamourer reports nothing when automation applies a design on a gearset\n" +
                                 "or job change, so Proteus infers it from the signals that do arrive.\n\n" +
                                 "Only ever restores a binding — it never clears one.\n" +
                                 "The one redraw Proteus itself causes is discounted, so its own work\n" +
                                 "can't be mistaken for an automation apply.\n\n" +
                                 "Needs \"Bind Proteus state to Glamourer designs\" above.");
        }
        ImGui.Unindent();

        var bindings = designBindings.Bindings;
        var activeId = designBindings.ActiveDesignId;

        if (activeId.HasValue)
        {
            var act = bindings.FirstOrDefault(b => b.DesignId == activeId.Value);
            ImGui.TextDisabled($"Active: {act?.DesignName ?? activeId.Value.ToString()[..8]}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Update binding"))
                designBindings.UpdateActiveBindingFromCurrentState();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Snapshot the current Proteus state (enable / priority / options / colors)\n" +
                                 "into the active binding. Manual edits only persist when you click this.");
        }

        if (bindings.Count == 0)
        {
            ImGui.TextDisabled("No bound designs yet.");
            return;
        }

        // See the note in DrawModsTab: EndTable is only legal when BeginTable returned true. Returning here
        // also skips the deferred Apply/Unbind dispatch below, which is correct — no row was drawn, so
        // neither button can have been clicked.
        if (!ImGui.BeginTable("##bindings", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
            return;
        ImGui.TableSetupColumn("Design",   ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Captured", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("##act",    ImGuiTableColumnFlags.WidthFixed, 120);

        // Clickable sort headers, same idiom as the Mods tab: clicking the active column flips direction,
        // switching column picks the sensible default for that column.
        void SortableHeader(string label, BindingSort col)
        {
            ImGui.TableNextColumn();
            var arrow = _bindingSort == col ? (_bindingSortDesc ? " ▼" : " ▲") : "";
            ImGui.TableHeader(label + arrow);
            if (ImGui.IsItemClicked())
            {
                if (_bindingSort == col) _bindingSortDesc = !_bindingSortDesc;
                else { _bindingSort = col; _bindingSortDesc = col != BindingSort.Design; }   // name asc, dates desc
            }
        }
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        SortableHeader("Design",   BindingSort.Design);
        SortableHeader("Captured", BindingSort.Captured);
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
                label = "● " + label; // ● marks the active binding
                ImGui.TextColored(BindingAccent, label);
            }
            else
                ImGui.TextUnformatted(label);

            ImGui.TableNextColumn();
            var ago = DateTime.UtcNow - b.CapturedUtc;
            ImGui.TextDisabled(
                ago.TotalSeconds < 60 ? $"{ago.TotalSeconds:F0}s ago"
                : ago.TotalMinutes < 60 ? $"{ago.TotalMinutes:F0}m ago"
                : $"{ago.TotalHours:F0}h ago");

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"Apply##{b.DesignId}"))
                toApply = b.DesignId;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Restore this design's Proteus state now, without going through Glamourer —\n" +
                    "enable / priority / options / colours for every mod it captured.\n\n" +
                    "Proteus mods NOT in the binding are switched off, so this replaces the current\n" +
                    "look rather than adding to it.");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Unbind##{b.DesignId}"))
                toRemove = b.DesignId;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Forget this binding. The Glamourer design itself is untouched.");
        }
        ImGui.EndTable();

        // Both deferred past the loop: each mutates the binding state the rows are drawn from.
        if (toApply.HasValue)
            designBindings.Restore(toApply.Value);
        if (toRemove.HasValue)
            designBindings.RemoveBinding(toRemove.Value);
    }

    private void DrawColorEditor(OverlayEntry entry)
    {
        ImGui.TextUnformatted(entry.ModName);

        // When a design binding drives this mod, edits target the binding (not metadata.json).
        bool editingBinding = designBindings.IsOverrideActiveFor(entry.ModDirectory);
        if (editingBinding)
        {
            var activeId = designBindings.ActiveDesignId;
            var name = designBindings.Bindings.FirstOrDefault(b => b.DesignId == activeId)?.DesignName
                       ?? activeId?.ToString()[..8] ?? "?";
            // Note the Reset caveat: it is the one control here that DOES rewrite the mod's own settings,
            // so the blanket "base colors unchanged" promise would be a lie next to it.
            ImGui.TextColored(BindingAccent, $"Editing binding '{name}' — previewing live; click \"Update binding\" to save.");
            ImGui.TextColored(BindingAccent, "Base colors unchanged, except \"Reset to defaults\" (Advanced), which rewrites them.");
        }

        ImGui.Separator();

        // Clear per-entry index cache on popup open so option switches are reflected.
        if (ImGui.IsWindowAppearing())
            foreach (var k in _indexRowCache.Keys.Where(k => k.StartsWith(entry.SidecarRoot)).ToList())
                _indexRowCache.Remove(k);

        // Which body's UV this mod's art is painted in — needed to know where its UV islands are, so the
        // index scan can ignore the dilated bleed outside them.
        var bodyType = OverlayBodyType(entry);

        // Scroll maps a gear overlay can pick from: the mod's own Effects/ folder, then the user's.
        var effects = discovery.ResolveAvailableEffects(entry, discovery.EffectsLibraryPath());

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
            bool hasIdxSimple  = false;
            foreach (var ov in entry.Metadata.Overlays ?? [])
            {
                if (ov.Index == null) continue;
                var idxPath = Path.Combine(entry.SidecarRoot, ov.Index);
                if (!_indexRowCache.ContainsKey(idxPath))
                    _indexRowCache[idxPath] = ScanIndexFile(idxPath, bodyType);
                usedRowsSimple.UnionWith(_indexRowCache[idxPath]);
                hasIdxSimple = true;
            }
            // Active "Masks" options can inject additional rows via their own Index companion —
            // see the option-group path below for the full explanation.
            foreach (var asset in discovery.ResolveActiveMaskAssets(entry))
            {
                if (asset.IndexPath == null) continue;
                if (!_indexRowCache.ContainsKey(asset.IndexPath))
                    _indexRowCache[asset.IndexPath] = ScanIndexFile(asset.IndexPath, bodyType);
                usedRowsSimple.UnionWith(_indexRowCache[asset.IndexPath]);
                hasIdxSimple = true;
            }
            HashSet<int>? filteredSimple = (hasIdxSimple && usedRowsSimple.Count > 0) ? usedRowsSimple : null;
            if (!hasIdxSimple)
                ImGui.TextDisabled("No index texture — only Row 16 is applied.");

            // Layer/shader normally persist to metadata.json — but while a binding is being edited they
            // go into its gear override (live preview, saved on "Update binding"), like colour rows.
            var simpleOverlays = entry.Metadata.Overlays ?? [];
            var gearOvrSimple = editingBinding && simpleOverlays.Count > 0
                ? designBindings.GetEditableGearOverride(entry.ModDirectory, null, null, simpleOverlays[0])
                : null;
            var modeBeforeSimple = EffectiveMode(simpleOverlays, gearOvrSimple);
            var (gearSimple, shaderSimple) = ColorTableEditor.EffectiveLayerShader(simpleOverlays, gearOvrSimple);

            bool changedSimple = false;
            int selSimple = _rowSelection.GetValueOrDefault(entry.ModDirectory, 1);
            ColorTableEditor.DrawRows(entry.ModDirectory, rows, filteredSimple, gearSimple, shaderSimple,
                compositor.GetShellMaterials(entry.ModDirectory, null, null),
                compositor.GetSkinGlowTargets(entry.ModDirectory, null, null),
                out var rowEditSimple, ref selSimple, ref changedSimple);
            _rowSelection[entry.ModDirectory] = selSimple;

            // Glow effect + Advanced live at the very bottom, below the rows.
            ImGui.Separator();
            bool resetSimple = false;
            bool footerChangedSimple = ColorTableEditor.DrawGlowFooter(
                entry.ModDirectory, simpleOverlays, gearOvrSimple, effects, out var footerEditSimple,
                onReset: () => resetSimple = ResetToDefaults(entry, null, null),
                resetDisabledReason: ResetBlockedReason(entry));

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
                if (footerChangedSimple || modeChangedSimple) compositor.TriggerRecomposite("mode-change");
                else compositor.TriggerRecomposite("colors-change", ColorEditDebounceMs);
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
            List<string>? selected = null;
            settings?.Options.TryGetValue(group.PenumbraGroupName, out selected);

            IEnumerable<OverlayOption> active = (selected is { Count: > 0 })
                ? group.Options.Where(o => selected.Any(s =>
                      string.Equals(o.Name, s, StringComparison.OrdinalIgnoreCase)))
                : [group.Options[0]];

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

        if (activeOptions.Count == 0 && !anyMaskWithId) return;

        // Show the tabs in TRUE stacking order, top-first — the same ordering the compositor applies,
        // just reversed (it composites last-on-top). Until this, the strip listed groups in metadata
        // order while the composite ranked them by Penumbra group number, so the "leftmost = on top"
        // label could be a lie whenever two groups were active.
        {
            var modRoot = Path.GetDirectoryName(
                entry.SidecarRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
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

            ImGui.TextDisabled("Editing overlay  (drag a tab to restack — leftmost = on top):");

            // Src/Dst are SelKey values, so a tab is identified across groups, not just within one.
            (string Src, string Dst)? pendingReorder = null;
            for (int i = 0; i < activeOptions.Count; i++)
            {
                if (i > 0) ImGui.SameLine();
                var (gName, opt) = activeOptions[i];
                bool isMaskTab = string.Equals(gName, SidecarDiscoveryService.MaskGroupName, StringComparison.Ordinal);
                var label = isMaskTab
                    ? "Masks"
                    : multiGroup ? $"{gName}: {opt.Name}" : opt.Name;

                using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), i == selIdx))
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
                compositor.TriggerRecomposite("stack-reorder");
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
                    if (ImGui.SmallButton($"◀ Toward top##stackup_{entry.ModDirectory}")) MoveTo(pos - 1);
                ImGui.SameLine();
                using (ImRaii.Disabled(pos == stackKeys.Count - 1 || selIsMask))
                    if (ImGui.SmallButton($"Toward bottom ▶##stackdn_{entry.ModDirectory}")) MoveTo(pos + 1);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Reorder how this mod's overlays stack on your body, across groups.\n" +
                                     "Leftmost tab = top of the stack (composites last, on top).");
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

        HashSet<int>? usedRows = null;
        var idxDesc = activeOpt.Overlays.FirstOrDefault(o => o.Index != null);
        if (idxDesc?.Index != null)
        {
            var idxPath = Path.Combine(entry.SidecarRoot, idxDesc.Index);
            if (!_indexRowCache.ContainsKey(idxPath))
                _indexRowCache[idxPath] = ScanIndexFile(idxPath, bodyType);
            var scan = _indexRowCache[idxPath];
            if (scan.Count > 0) usedRows = scan;
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
            if (maskScan.Count == 0) continue;
            usedRows ??= [];
            usedRows.UnionWith(maskScan);
        }

        bool hasAnyIndex = idxDesc?.Index != null || maskAssets.Any(a => a.IndexPath != null);
        if (usedRows == null && !hasAnyIndex)
            ImGui.TextDisabled("No index texture — only Row 16 is applied.");

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
            int maskSel   = _rowSelection.GetValueOrDefault(maskScope, 1);
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
            if (config.PluginEnabled && maskLocatorMissing
                && _glowWarmedMods.Add($"{entry.ModDirectory}\0masks\0{(maskAsGear ? 'g' : 's')}"))
                compositor.TriggerRecomposite("mask-glow-warmup");

            ColorTableEditor.DrawRows(maskScope, maskRows, usedRows, maskAsGear, maskShader,
                maskShellMaterials,
                maskGlowTargets,
                out var maskRowEdit, ref maskSel, ref maskChanged);
            _rowSelection[maskScope] = maskSel;

            ImGui.Separator();
            bool maskFooterChanged = false, maskModeChanged = false;
            if (modHasGear)
            {
                // Forced Cloth shell — the mask's layer isn't user-chosen when it stacks over gear.
                ColorTableEditor.DrawRenderingAsBadge(RenderMode.Cloth);
                ImGui.SameLine();
                ImGui.TextDisabled("(forced)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Another active option in this mod renders as gear, so the mask has to sit on that\n" +
                        "shell — it can't be painted into the skin underneath it. Switch those options to\n" +
                        "Skin (Advanced on their tabs) and the mask gets its own mode choice back.");
            }
            else
            {
                // Same footer as the overlay tabs: the "Rendering as" badge + Advanced force-mode radios +
                // glow-effect picker. No per-option reset (the mask has no defaults cache) → onReset null.
                maskFooterChanged = ColorTableEditor.DrawGlowFooter(maskScope, [maskDesc], maskGearOvr, effects,
                    out var maskFooterEdit, onReset: null);
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
                if (maskFooterChanged || maskModeChanged) compositor.TriggerRecomposite("mask-mode-change");
                else compositor.TriggerRecomposite("mask-colors-change", ColorEditDebounceMs);
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
        bool promotedToGear = false;
        if (!gear)
        {
            // !gear means the effective layer (override first) is Skin — the state the promotion acts on.
            // The binding's pin, when one is being edited, outranks the descriptor's.
            bool pinned = gearOvrOpt?.ManualShaderLock
                ?? activeOpt.Overlays.FirstOrDefault()?.ManualShaderLock ?? false;
            bool aboveGear = activeOptions.Skip(selIdx + 1).Any(x => x.Option.Overlays.Any(d => d.Layer == OverlayLayer.Gear));
            if (RenderModeInference.ShouldPromoteToGear(OverlayLayer.Skin, pinned, editRows, aboveGear))
            {
                gear = true; shader = OverlayDescriptor.DefaultGearShader; promotedToGear = true;
            }
        }

        bool changed = false;
        int sel = _rowSelection.GetValueOrDefault(scope, 1);

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
        if (config.PluginEnabled && activeOpt.Overlays.Count > 0 && locatorDataMissing
            && _glowWarmedMods.Add($"{entry.ModDirectory}\0{groupName}\0{activeOpt.Name}\0{(gear ? 'g' : 's')}"))
            compositor.TriggerRecomposite("glow-warmup");

        ColorTableEditor.DrawRows(scope, editRows, usedRows, gear, shader,
            shellMaterials,
            skinGlowTargets,
            out var rowEdit, ref sel, ref changed);
        _rowSelection[scope] = sel;

        // Glow effect + Advanced live at the very bottom, below the rows.
        ImGui.Separator();
        bool resetOpt = false;
        bool footerChanged = ColorTableEditor.DrawGlowFooter(scope, activeOpt.Overlays, gearOvrOpt, effects, out var footerEdit,
            onReset: () => resetOpt = ResetToDefaults(entry, groupName, activeOpt),
            resetDisabledReason: ResetBlockedReason(entry),
            promotedToGear: promotedToGear);

        // A reset just restored the recorded values — they ARE the intended state, so skip the mode
        // re-inference and glow transition this frame (both would re-derive from pre-reset state).
        bool modeChanged = false;
        if (!resetOpt)
        {
            modeChanged = ReconcileMode(activeOpt.Overlays, gearOvrOpt, editRows,
                rowEdit != FeatureEdit.Neutral ? rowEdit : footerEdit);

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
            if (footerChanged || modeChanged) compositor.TriggerRecomposite("mode-change");
            else compositor.TriggerRecomposite("colors-change", ColorEditDebounceMs);
        }
    }

    /// <summary>
    /// After the header + rows are drawn, point Layer/Shader at the mode the features imply — a sphere map
    /// or metal ⇒ Cloth, a glow effect ⇒ Animated glow, nothing special ⇒ Skin — unless the user pinned it
    /// in Advanced (<see cref="OverlayDescriptor.ManualShaderLock"/>). Writes the override when a design
    /// binding is being edited, else the descriptors. Returns true when the mode actually changed.
    /// </summary>
    private static bool ReconcileMode(IReadOnlyList<OverlayDescriptor> overlays, GearSettingsPreset? ovr,
        List<ColorTableRowPreset> rows, FeatureEdit edited)
    {
        // Only respond to an actual mode-relevant edit this frame. Running on every frame would force a
        // deliberately plain Gear overlay (no sphere/metal/scroll — used for shell transparency) to Skin.
        if (edited == FeatureEdit.Neutral) return false;
        if (overlays.Count == 0) return false;
        bool locked = ovr != null ? (ovr.ManualShaderLock ?? false) : overlays.Any(d => d.ManualShaderLock);
        if (locked) return false;

        var cur  = RenderModeInference.ModeOf(ovr?.Layer ?? overlays[0].Layer,
                                              ovr != null ? ovr.Shader : overlays[0].Shader);

        // Leaving Animated glow (the effect was just cleared): drop the Glow those rows carry BEFORE
        // inferring. ApplyGlowTransition zeroes it, but only after this runs — and since Glow now counts
        // as a Cloth feature, the 150% this mode put there itself would otherwise infer Cloth and strand
        // the overlay in a mode the user never asked for, with nothing cloth-like left to justify it.
        // Safe to do unconditionally here: a pinned overlay already returned above, and `edited` proves
        // this frame carries a real edit, so it can't fight an author tuning a Glow-pinned overlay.
        if (cur == RenderMode.Glow && !RenderModeInference.HasGlow(overlays, ovr))
            SetRowsEmissive(rows, 0f);

        var want = RenderModeInference.Infer(rows, overlays, ovr, cur, edited);
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
        if (before != RenderMode.Glow && after == RenderMode.Glow) SetRowsEmissive(rows, 1.5f, "#FFFFFF");
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

    private HashSet<int> ScanIndexFile(string absolutePath, string? bodyType)
    {
        var used = new HashSet<int>();
        try
        {
            using var stream = File.OpenRead(absolutePath);
            var img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            int w = img.Width, h = img.Height;

            // Mask to UV islands so padding (art tools bleed colour outside the islands) isn't counted as
            // real coverage. The ~4K transfer map must NOT be loaded synchronously here though — that would
            // stall the first draw of the editor. So take the map only if it's already in memory, and
            // otherwise kick off a background load; when it lands, the scan cache is dropped and the rows
            // are recomputed accurately. Until then we simply count every pixel.
            int islandW = 0, islandH = 0;
            bool[]? island = bodyType != null ? uvRemap.IslandMask(bodyType, out islandW, out islandH, loadIfMissing: false) : null;
            if (islandW == 0 || islandH == 0) island = null;
            if (island == null && bodyType != null) StartIslandMaskWarmup(bodyType);

            var counts = new int[17];
            int total = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (island != null)
                    {
                        // The map is its own resolution; sample it nearest-neighbour.
                        int mx = x * islandW / w, my = y * islandH / h;
                        int mi = my * islandW + mx;
                        if (mi >= island.Length || !island[mi]) continue;   // outside the islands
                    }

                    counts[img.Data[(y * w + x) * 4] / 17 + 1]++;   // red → 1-based row
                    total++;
                }
            }

            if (total == 0) return used;

            int threshold = Math.Max(64, total / 1000);   // 0.1% of the island area
            for (int row = 1; row <= 16; row++)
                if (counts[row] >= threshold)
                    used.Add(row);
        }
        catch { }
        return used;
    }
}
