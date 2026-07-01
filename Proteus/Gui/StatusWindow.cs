using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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

    // Accent used to flag an active design binding (and the mods/colors it drives).
    private static readonly Vector4 BindingAccent = new(0.45f, 0.75f, 1f, 1f);

    // Indexed by (int)SiblingSynthesisMode: Off=0, BiboGen3Only=1, AllBodies=2.
    private static readonly string[] SiblingModeLabels = { "Off", "bibo+gen3", "All bodies" };

    // Key: absolute index-texture path → 1-based row numbers that appear in it.
    // Cleared per-entry on each popup open so option switches are reflected.
    private readonly Dictionary<string, HashSet<int>> _indexRowCache = new();
    // Key: modDir → selected index into the active-options list (for the dropdown).
    private readonly Dictionary<string, int> _colorEditorSelection = new();
    // Key: modDir → priority value being dragged; committed to Penumbra on edit-end.
    private readonly Dictionary<string, int> _priorityEdits = new();

    public StatusWindow(
        CompositorService compositor,
        SidecarDiscoveryService discovery,
        PenumbraBridge penumbra,
        Configuration config,
        DesignBindingService designBindings,
        UVMapDownloadService uvMapDl)
        : base("Proteus###ProteusStatus", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.compositor     = compositor;
        this.discovery      = discovery;
        this.penumbra       = penumbra;
        this.config         = config;
        this.designBindings = designBindings;
        this.uvMapDl        = uvMapDl;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(380, 80),
            MaximumSize = new System.Numerics.Vector2(900, 700),
        };
    }

    public override void Draw()
    {
        // ── UV map download banner ────────────────────────────────────────────
        if (uvMapDl.State == UVMapDownloadState.Downloading)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), uvMapDl.StatusMessage);
            ImGui.Separator();
        }
        else if (uvMapDl.State == UVMapDownloadState.Failed)
        {
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), uvMapDl.StatusMessage);
            ImGui.SameLine();
            if (ImGui.Button("Retry"))
                uvMapDl.EnsureMapsAsync();
            ImGui.Separator();
        }

        // ── Toolbar ──────────────────────────────────────────────────────────
        var enabled = config.PluginEnabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            config.PluginEnabled = enabled;
            config.Save();
            if (enabled && penumbra.IsAvailable)
                compositor.TriggerRecomposite("enabled");
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
            compositor.TriggerRecomposite("manual");

        ImGui.SameLine();
        var disableRedraw = config.DisableAutoRedraw;
        if (ImGui.Checkbox("Disable auto redraw", ref disableRedraw))
        {
            config.DisableAutoRedraw = disableRedraw;
            config.Save();
        }

        ImGui.SameLine();
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

        if (!penumbra.IsAvailable)
        {
            ImGui.SameLine();
            ImGui.TextColored(new System.Numerics.Vector4(1, 0.4f, 0.4f, 1), "Penumbra unavailable");
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

        ImGui.Separator();

        // ── Overlay mod list ─────────────────────────────────────────────────
        var mods = compositor.LastDiscovered;
        if (mods.Count == 0)
        {
            ImGui.TextDisabled("No Proteus sidecar mods detected.");
        }
        else
        {
            ImGui.BeginTable("##mods", 6, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV);
            ImGui.TableSetupColumn("##en",     ImGuiTableColumnFlags.WidthFixed, 20);
            ImGui.TableSetupColumn("Mod",      ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pri",      ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Colors",   ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Bodies",   ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Overlays", ImGuiTableColumnFlags.WidthFixed, 70);
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
                    // If a design binding drives this mod, fold the manual toggle into the binding so
                    // the next restore doesn't flip it back (UI toggle vs. restore would otherwise fight).
                    designBindings.UpdateActiveBindingMod(entry.ModDirectory, enabled: active);
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
                    // Fold the manual priority edit into the active binding (see enable toggle above).
                    designBindings.UpdateActiveBindingMod(entry.ModDirectory, priority: pri);
                    compositor.TriggerRecomposite("penumbra-priority");
                }

                // Colors button + popup editor. Tinted when a design binding is currently
                // driving this mod's colors (edits target the binding, not metadata).
                ImGui.TableNextColumn();
                var popupId = $"##colors_{entry.ModDirectory}";
                bool bindingDriven = designBindings.IsOverrideActiveFor(entry.ModDirectory);
                using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(BindingAccent with { W = 0.45f }), bindingDriven))
                {
                    if (ImGui.Button($"Colors##{entry.ModDirectory}"))
                        ImGui.OpenPopup(popupId);
                }
                if (bindingDriven && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Colors are driven by the active design binding.\nEdits update the binding; base colors are unchanged.");

                if (ImGui.BeginPopup(popupId))
                {
                    DrawColorEditor(entry);
                    ImGui.EndPopup();
                }

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

                // Overlay count
                ImGui.TableNextColumn();
                var activeOverlays = discovery.ResolveActiveOverlays(entry);
                int ovCount = activeOverlays.Count;
                ImGui.TextUnformatted($"{ovCount} overlay{(ovCount != 1 ? "s" : "")}");
            }

            ImGui.EndTable();
        }

        ImGui.Separator();

        // ── Design bindings ──────────────────────────────────────────────────
        DrawDesignBindings();

        ImGui.Separator();

        // ── Last result ───────────────────────────────────────────────────────
        var result = compositor.LastResult;
        if (result == null)
        {
            ImGui.TextDisabled("No composite result yet.");
        }
        else if (!result.Success)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1, 0.4f, 0.4f, 1),
                $"Error: {result.ErrorMessage ?? "unknown"}");
        }
        else
        {
            var elapsed = DateTime.UtcNow - result.Timestamp;
            var timeStr = elapsed.TotalSeconds < 60
                ? $"{elapsed.TotalSeconds:F1}s ago"
                : $"{elapsed.TotalMinutes:F0}m ago";

            ImGui.TextDisabled($"Last composite: {timeStr}   " +
                               $"{result.TexturesPatched} texture{(result.TexturesPatched != 1 ? "s" : "")} patched   " +
                               $"{result.OverlayModsUsed} mod{(result.OverlayModsUsed != 1 ? "s" : "")}");
        }
    }

    private void DrawDesignBindings()
    {
        if (!ImGui.CollapsingHeader("Design bindings"))
            return;

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
            ImGui.TextColored(BindingAccent, $"Editing binding '{name}' — base colors unchanged.");
        }

        ImGui.Separator();

        // Clear per-entry index cache on popup open so option switches are reflected.
        if (ImGui.IsWindowAppearing())
            foreach (var k in _indexRowCache.Keys.Where(k => k.StartsWith(entry.SidecarRoot)).ToList())
                _indexRowCache.Remove(k);

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
                    _indexRowCache[idxPath] = ScanIndexFile(idxPath);
                usedRowsSimple.UnionWith(_indexRowCache[idxPath]);
                hasIdxSimple = true;
            }
            // Active "Masks" options can inject additional rows via their own Index companion —
            // see the option-group path below for the full explanation.
            foreach (var asset in discovery.ResolveActiveMaskAssets(entry))
            {
                if (asset.IndexPath == null) continue;
                if (!_indexRowCache.ContainsKey(asset.IndexPath))
                    _indexRowCache[asset.IndexPath] = ScanIndexFile(asset.IndexPath);
                usedRowsSimple.UnionWith(_indexRowCache[asset.IndexPath]);
                hasIdxSimple = true;
            }
            HashSet<int>? filteredSimple = (hasIdxSimple && usedRowsSimple.Count > 0) ? usedRowsSimple : null;
            if (!hasIdxSimple)
                ImGui.TextDisabled("No index texture — only Row 16 is applied.");
            bool changedSimple = false;
            DrawRowControls(entry.ModDirectory, rows, filteredSimple, ref changedSimple);
            if (changedSimple)
            {
                if (ovrRows != null) designBindings.CommitActiveOverrideEdit();
                else                 discovery.SaveMetadata(entry);
                compositor.TriggerRecomposite("colors-change");
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
                _indexRowCache[idxPath] = ScanIndexFile(idxPath);
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
                _indexRowCache[asset.IndexPath] = ScanIndexFile(asset.IndexPath);
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

        bool changed = false;
        DrawRowControls($"{entry.ModDirectory}_{groupName}", editRows, usedRows, ref changed);
        if (changed)
        {
            if (ovrOptRows != null) designBindings.CommitActiveOverrideEdit();
            else                    discovery.SaveMetadata(entry);
            compositor.TriggerRecomposite("colors-change");
        }
    }

    // Renders the per-row A/B color/emissive/opacity controls, filtered by usedRows when non-null.
    // idScope is embedded in widget IDs to prevent collisions between groups.
    private static void DrawRowControls(
        string idScope,
        List<ColorTableRowPreset> rows,
        HashSet<int>? usedRows,
        ref bool changed)
    {
        for (int pairNum = 1; pairNum <= 16; pairNum++)
        {
            if (usedRows != null && !usedRows.Contains(pairNum)) continue;

            var preset = rows.FirstOrDefault(r => r.Row == pairNum);

            ImGui.TextUnformatted($"{pairNum,2}");

            ImGui.SameLine();
            ImGui.TextDisabled("A");
            ImGui.SameLine();

            var colA = HexToVec3(preset?.SubRowA?.Diffuse);
            ImGui.SetNextItemWidth(22);
            if (ImGui.ColorEdit3($"##dA_{idScope}_{pairNum}", ref colA, ImGuiColorEditFlags.NoInputs))
            {
                preset = EnsurePreset(rows, pairNum);
                preset.SubRowA ??= new ColorTableSubRowPreset();
                preset.SubRowA.Diffuse = Vec3ToHex(colA);
                changed = true;
            }

            ImGui.SameLine();
            float emA = preset?.SubRowA?.Emissive ?? 0f;
            ImGui.SetNextItemWidth(60);
            if (ImGui.DragFloat($"##eA_{idScope}_{pairNum}", ref emA, 0.01f, 0f, 1f, "%.2f"))
            {
                preset = EnsurePreset(rows, pairNum);
                preset.SubRowA ??= new ColorTableSubRowPreset();
                preset.SubRowA.Emissive = Math.Clamp(emA, 0f, 1f);
                changed = true;
            }

            ImGui.SameLine();
            int opA = preset?.SubRowA?.Opacity ?? 0;
            ImGui.SetNextItemWidth(50);
            if (ImGui.DragInt($"##opA_{idScope}_{pairNum}", ref opA, 1f, -100, 100, "%d%%"))
            {
                preset = EnsurePreset(rows, pairNum);
                preset.SubRowA ??= new ColorTableSubRowPreset();
                preset.SubRowA.Opacity = Math.Clamp(opA, -100, 100);
                changed = true;
            }

            ImGui.SameLine();
            ImGui.TextDisabled(" B");
            ImGui.SameLine();

            var colB = HexToVec3(preset?.SubRowB?.Diffuse);
            ImGui.SetNextItemWidth(22);
            if (ImGui.ColorEdit3($"##dB_{idScope}_{pairNum}", ref colB, ImGuiColorEditFlags.NoInputs))
            {
                preset = EnsurePreset(rows, pairNum);
                preset.SubRowB ??= new ColorTableSubRowPreset();
                preset.SubRowB.Diffuse = Vec3ToHex(colB);
                changed = true;
            }

            ImGui.SameLine();
            float emB = preset?.SubRowB?.Emissive ?? 0f;
            ImGui.SetNextItemWidth(60);
            if (ImGui.DragFloat($"##eB_{idScope}_{pairNum}", ref emB, 0.01f, 0f, 1f, "%.2f"))
            {
                preset = EnsurePreset(rows, pairNum);
                preset.SubRowB ??= new ColorTableSubRowPreset();
                preset.SubRowB.Emissive = Math.Clamp(emB, 0f, 1f);
                changed = true;
            }

            ImGui.SameLine();
            int opB = preset?.SubRowB?.Opacity ?? 0;
            ImGui.SetNextItemWidth(50);
            if (ImGui.DragInt($"##opB_{idScope}_{pairNum}", ref opB, 1f, -100, 100, "%d%%"))
            {
                preset = EnsurePreset(rows, pairNum);
                preset.SubRowB ??= new ColorTableSubRowPreset();
                preset.SubRowB.Opacity = Math.Clamp(opB, -100, 100);
                changed = true;
            }
        }
    }

    private HashSet<int> ScanIndexFile(string absolutePath)
    {
        var used = new HashSet<int>();
        try
        {
            using var stream = File.OpenRead(absolutePath);
            var img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            for (int i = 0; i < img.Data.Length; i += 4)
                used.Add(img.Data[i] / 17 + 1); // red channel → 1-based row number
        }
        catch { }
        return used;
    }

    private static ColorTableRowPreset EnsurePreset(List<ColorTableRowPreset> rows, int row)
    {
        var p = rows.FirstOrDefault(r => r.Row == row);
        if (p == null) { p = new ColorTableRowPreset { Row = row }; rows.Add(p); }
        return p;
    }

    private static Vector3 HexToVec3(string? hex)
    {
        if (hex == null) return Vector3.One;
        hex = hex.TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        try
        {
            int v = Convert.ToInt32(hex, 16);
            return new Vector3((v >> 16 & 0xFF) / 255f, (v >> 8 & 0xFF) / 255f, (v & 0xFF) / 255f);
        }
        catch { return Vector3.One; }
    }

    private static string Vec3ToHex(Vector3 c)
    {
        int r = Math.Clamp((int)(c.X * 255), 0, 255);
        int g = Math.Clamp((int)(c.Y * 255), 0, 255);
        int b = Math.Clamp((int)(c.Z * 255), 0, 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
