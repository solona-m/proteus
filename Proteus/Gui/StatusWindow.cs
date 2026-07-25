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

        // The second skin rides on an equipped ring/bracelet (its model is redirected to our merged
        // shell). An in-place reload can't reload that .mdl, so if a shell ever gets stuck on the
        // accessory this forces a full redraw to reload the accessory's original model.
        if (ImGui.Button("Restore changed accessory"))
            compositor.RestoreChangedAccessory();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force a full redraw to reload any ring/bracelet the second skin replaced,\n" +
                "restoring it to its original model. Use if a gear shell stays stuck on an\n" +
                "accessory after disabling or swapping.");

        // The scroll-map library lives in Proteus's own Penumbra mod folder — nothing to configure.
        var lib = discovery.EffectsLibraryPath();
        if (lib != null)
        {
            ImGui.TextDisabled($"Effects library: {lib}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Drop scroll maps (the \"_o\" textures that ARE the animated glow) in here and\n" +
                                 "they appear in every gear overlay's Effect dropdown.\n" +
                                 "Accepts .tex, .dds, .png, .jpg, .bmp, .tga, .psd and .gif.\n" +
                                 "A mod's own Proteus/Effects/ folder takes precedence over it.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Open"))
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(lib) { UseShellExecute = true }); }
                catch { /* no file manager — the path is shown anyway */ }
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
                    _createMaterial = _createMaterialAuto = ModCreationService.DefaultBodyMaterial;
                }
            }
        }
        ImGui.SetNextItemWidth(560);
        ImGui.InputText("Material target", ref _createMaterial, 256);
        ImGui.SameLine();
        if (ImGui.SmallButton("Re-detect"))
        {
            _createMaterial = _createMaterialAuto = modCreation.DetectBodyMaterial() ?? ModCreationService.DefaultBodyMaterial;
            _createMaterialLocked = true;   // explicit request — take this value and stop polling
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The body material this overlay composites onto. Auto-filled from the body\n" +
                "you're currently wearing; edit it to target a different body/race.");

        ImGui.Spacing();
        DrawTextureRow("Diffuse", ref _createDiffuse);
        DrawTextureRow("Mask", ref _createMask);
        DrawTextureRow("Normal", ref _createNormal);
        DrawTextureRow("Index", ref _createIndex);

        ImGui.Separator();

        bool valid = !string.IsNullOrWhiteSpace(_createName)
            && !string.IsNullOrWhiteSpace(_createMaterial)
            && (_createDiffuse.Length > 0 || _createMask.Length > 0
                || _createNormal.Length > 0 || _createIndex.Length > 0);

        using (ImRaii.Disabled(!valid))
            if (ImGui.Button("Create"))
            {
                var r = modCreation.Create(
                    _createName, _createAuthor, _createMaterial,
                    NullIfEmpty(_createDiffuse), NullIfEmpty(_createMask),
                    NullIfEmpty(_createNormal), NullIfEmpty(_createIndex));
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

    /// <summary>One texture slot: current file name, a Browse button, and a Clear button.</summary>
    private void DrawTextureRow(string label, ref string path)
    {
        var shown = path.Length == 0 ? "(none)" : Path.GetFileName(path);
        ImGui.TextUnformatted($"{label}:");
        ImGui.SameLine(90);
        ImGui.TextUnformatted(shown);

        ImGui.SameLine(360);
        // Capture the field by a local setter — ref can't cross the dialog callback.
        var captured = label;
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
        if (path.Length > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Clear##{label}"))
                path = "";
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

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
            ImGui.BeginTable("##mods", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV);
            ImGui.TableSetupColumn("##en",   ImGuiTableColumnFlags.WidthFixed, 20);
            ImGui.TableSetupColumn("Mod",    ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pri",    ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Colors", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Bodies", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableHeadersRow();

            // Enable/priority controls write straight through to Penumbra (Proteus keeps no
            // override state of its own); both reflect the mod's live Penumbra values.
            var collId = penumbra.GetPlayerCollectionId();

            foreach (var entry in mods)
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

        ImGui.BeginTable("##bindings", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV);
        ImGui.TableSetupColumn("Design",   ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Captured", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("##rm",     ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableHeadersRow();

        Guid? toRemove = null;
        foreach (var b in bindings)
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
            if (ImGui.SmallButton($"Unbind##{b.DesignId}"))
                toRemove = b.DesignId;
        }
        ImGui.EndTable();

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
            var metaRows = entry.Metadata.ColorTableRows ??= [];
            var ovrRows  = editingBinding
                ? designBindings.GetEditableOverrideRows(entry.ModDirectory, null, null, metaRows)
                : null;
            var rows = ovrRows ?? metaRows;

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
                // Binding path: live-preview only — edits stay in the in-memory overrides and fold into the
                // binding via "Update binding". Base metadata persists only when NOT editing a binding —
                // except a reset, which exists precisely to rewrite the base and must always land.
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
                    ReadOnlySpan<byte> marker = stackalloc byte[1];
                    ImGui.SetDragDropPayload("PROTEUS_STACK", marker, ImGuiCond.None);
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
            var maskRows  = (editingBinding
                ? designBindings.GetEditableMaskRows(entry.ModDirectory, baseMaskRows)
                : null) ?? baseMaskRows;
            var maskScope = $"{entry.ModDirectory}_{SidecarDiscoveryService.MaskGroupName}";
            int maskSel   = _rowSelection.GetValueOrDefault(maskScope, 1);
            bool maskChanged = false;

            // When the mod has any gear layer, the mask is FORCED to a top Cloth shell (it stacks over gear),
            // so no mode choice is offered. When it's all skin, the mask carries its own mode descriptor and
            // gets the full auto-detection + Advanced, exactly like an overlay option.
            bool modHasGear = activeOptions.Any(x =>
                !string.Equals(x.GroupName, SidecarDiscoveryService.MaskGroupName, StringComparison.Ordinal)
                && x.Option.Overlays.Any(d => d.Layer == OverlayLayer.Gear));

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
                // Binding path: edits already landed in the overrides (live preview, folded in via "Update
                // binding"); base metadata persists only when NOT editing a binding — same split as the
                // overlay tabs. The mode descriptor is written only when the mode/footer actually changed.
                if (!editingBinding)
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

        activeOpt.ColorTableRows ??= [];
        var ovrOptRows = editingBinding
            ? designBindings.GetEditableOverrideRows(entry.ModDirectory, groupName, activeOpt.Name, activeOpt.ColorTableRows)
            : null;
        var editRows = ovrOptRows ?? activeOpt.ColorTableRows;

        var scope = $"{entry.ModDirectory}_{groupName}_{activeOpt.Name}";

        // Layer/shader normally persist to metadata.json — but while a binding is being edited they go
        // into its gear override (live preview, saved on "Update binding"), like colour rows.
        var gearOvrOpt = editingBinding && activeOpt.Overlays.Count > 0
            ? designBindings.GetEditableGearOverride(entry.ModDirectory, groupName, activeOpt.Name, activeOpt.Overlays[0])
            : null;
        var modeBefore = EffectiveMode(activeOpt.Overlays, gearOvrOpt);
        var (gear, shader) = ColorTableEditor.EffectiveLayerShader(activeOpt.Overlays, gearOvrOpt);

        // Auto skin-above-gear promotion — mirror the compositor (CompositorService split): a skin overlay
        // on auto (not pinned) stacked above a gear layer renders as a gear shell, so the editor must treat
        // it as gear too. activeOptions is already sorted top-first, so any option below this one (index >
        // selIdx) that is gear means this one sits above gear. Without this the Glow button never shows —
        // it keys off shell materials for gear vs skin-glow targets for skin, and the compositor built the
        // former, not the latter.
        bool promotedToGear = false;
        if (!gear)
        {
            bool pinned = gearOvrOpt?.ManualShaderLock ?? activeOpt.Overlays.FirstOrDefault()?.ManualShaderLock ?? false;
            bool aboveGear = activeOptions.Skip(selIdx + 1).Any(x => x.Option.Overlays.Any(d => d.Layer == OverlayLayer.Gear));
            if (!pinned && aboveGear) { gear = true; shader = OverlayDescriptor.DefaultGearShader; promotedToGear = true; }
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
