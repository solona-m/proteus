using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
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
    private readonly Dictionary<string, int> _colorEditorSelection = new();
    // Key: editor scope → which color table row (1–16) is open in the editor.
    private readonly Dictionary<string, int> _rowSelection = new();
    // Colour edits arrive one per frame while a slider/swatch is dragged; a recomposite is multi-second,
    // so wait this long after the LAST change before recompositing (TriggerRecomposite restarts the
    // timer on each call). The on-screen editor swatches update live regardless — only the bake waits.
    private const int ColorEditDebounceMs = 5000;
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
        : base("Proteus###ProteusStatus", ImGuiWindowFlags.AlwaysAutoResize)
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
        if (_colorWindowMod == null) return;

        var entry = compositor.LastDiscovered.FirstOrDefault(e => e.ModDirectory == _colorWindowMod);
        if (entry == null) { _colorWindowMod = null; return; }

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
            ImGui.TextColored(BindingAccent, $"Editing binding '{name}' — previewing live; click \"Update binding\" to save. Base colors unchanged.");
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
            if (ColorTableEditor.DrawLayerHeader(entry.ModDirectory, simpleOverlays, gearOvrSimple, effects))
            {
                if (gearOvrSimple == null) discovery.SaveMetadata(entry);
                compositor.TriggerRecomposite("layer-change");
            }
            var (gearSimple, shaderSimple) = ColorTableEditor.EffectiveLayerShader(simpleOverlays, gearOvrSimple);

            ImGui.Separator();

            bool changedSimple = false;
            int selSimple = _rowSelection.GetValueOrDefault(entry.ModDirectory, 1);
            ColorTableEditor.DrawRows(entry.ModDirectory, rows, filteredSimple, gearSimple, shaderSimple,
                ref selSimple, ref changedSimple);
            _rowSelection[entry.ModDirectory] = selSimple;

            if (changedSimple)
            {
                // Binding path (ovrRows != null): live-preview only — the edit stays in the in-memory
                // override and is folded into the binding solely via "Update binding". Metadata path
                // (base colors) persists immediately as before.
                if (ovrRows == null) discovery.SaveMetadata(entry);
                compositor.TriggerRecomposite("colors-change", ColorEditDebounceMs);
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

            foreach (var opt in active)
            {
                if (opt.Name.EndsWith("None", StringComparison.OrdinalIgnoreCase))
                    continue;
                activeOptions.Add((group.PenumbraGroupName, opt));
            }
        }

        if (activeOptions.Count == 0) return;

        int selIdx = _colorEditorSelection.GetValueOrDefault(entry.ModDirectory, 0);
        if (selIdx >= activeOptions.Count) selIdx = 0;

        if (activeOptions.Count > 1)
        {
            var labels = activeOptions.Select(x => $"{x.GroupName} / {x.Option.Name}").ToArray();
            ImGui.SetNextItemWidth(220);
            if (ImGui.Combo($"##optsel_{entry.ModDirectory}", ref selIdx, labels, labels.Length))
                _colorEditorSelection[entry.ModDirectory] = selIdx;
        }

        var (groupName, activeOpt) = activeOptions[selIdx];

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
        var maskAssets = discovery.ResolveActiveMaskAssets(entry);
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
        if (ColorTableEditor.DrawLayerHeader(scope, activeOpt.Overlays, gearOvrOpt, effects))
        {
            if (gearOvrOpt == null) discovery.SaveMetadata(entry);
            compositor.TriggerRecomposite("layer-change");
        }
        var (gear, shader) = ColorTableEditor.EffectiveLayerShader(activeOpt.Overlays, gearOvrOpt);

        ImGui.Separator();

        bool changed = false;
        int sel = _rowSelection.GetValueOrDefault(scope, 1);
        ColorTableEditor.DrawRows(scope, editRows, usedRows, gear, shader, ref sel, ref changed);
        _rowSelection[scope] = sel;

        if (changed)
        {
            // Binding path: live-preview only (folded in via "Update binding"). Metadata path persists.
            if (ovrOptRows == null) discovery.SaveMetadata(entry);
            compositor.TriggerRecomposite("colors-change", ColorEditDebounceMs);
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

    private HashSet<int> ScanIndexFile(string absolutePath, string? bodyType)
    {
        var used = new HashSet<int>();
        try
        {
            using var stream = File.OpenRead(absolutePath);
            var img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            int w = img.Width, h = img.Height;

            int islandW = 0, islandH = 0;
            bool[]? island = bodyType != null ? uvRemap.IslandMask(bodyType, out islandW, out islandH) : null;
            if (islandW == 0 || islandH == 0) island = null;

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
