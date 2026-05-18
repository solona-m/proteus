using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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

    // Key: absolute index-texture path → 1-based row numbers that appear in it.
    // Cleared per-entry on each popup open so option switches are reflected.
    private readonly Dictionary<string, HashSet<int>> _indexRowCache = new();
    // Key: modDir → selected index into the active-options list (for the dropdown).
    private readonly Dictionary<string, int> _colorEditorSelection = new();

    public StatusWindow(
        CompositorService compositor,
        SidecarDiscoveryService discovery,
        PenumbraBridge penumbra,
        Configuration config)
        : base("Proteus###ProteusStatus", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.compositor = compositor;
        this.discovery  = discovery;
        this.penumbra   = penumbra;
        this.config     = config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(380, 80),
            MaximumSize = new System.Numerics.Vector2(900, 700),
        };
    }

    public override void Draw()
    {
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

        if (!penumbra.IsAvailable)
        {
            ImGui.SameLine();
            ImGui.TextColored(new System.Numerics.Vector4(1, 0.4f, 0.4f, 1), "Penumbra unavailable");
        }

        ImGui.Separator();

        // ── Overlay mod list ─────────────────────────────────────────────────
        var mods = compositor.LastDiscovered;
        if (mods.Count == 0)
        {
            ImGui.TextDisabled("No Proteus sidecar mods detected.");
        }
        else
        {
            ImGui.BeginTable("##mods", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV);
            ImGui.TableSetupColumn("##en",     ImGuiTableColumnFlags.WidthFixed, 20);
            ImGui.TableSetupColumn("Mod",      ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pri",      ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Colors",   ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Overlays", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();

            foreach (var entry in mods)
            {
                ImGui.TableNextRow();

                config.ModOverrides.TryGetValue(entry.ModDirectory, out var ov);

                // Checkbox
                ImGui.TableNextColumn();
                bool active = ov == null || !ov.Disabled;
                if (ImGui.Checkbox($"##en_{entry.ModDirectory}", ref active))
                {
                    if (ov == null) { ov = new ModOverride(); config.ModOverrides[entry.ModDirectory] = ov; }
                    ov.Disabled = !active;
                    config.Save();
                    compositor.TriggerRecomposite("override-enable");
                }

                // Mod name (dimmed when disabled)
                ImGui.TableNextColumn();
                if (active) ImGui.TextUnformatted(entry.ModName);
                else        ImGui.TextDisabled(entry.ModName);

                // Priority (drag to edit, Ctrl+click to type)
                ImGui.TableNextColumn();
                int pri = ov?.PriorityOverride ?? entry.Priority;
                ImGui.SetNextItemWidth(55);
                ImGui.DragInt($"##pri_{entry.ModDirectory}", ref pri, 1f);
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    if (ov == null) { ov = new ModOverride(); config.ModOverrides[entry.ModDirectory] = ov; }
                    ov.PriorityOverride = pri;
                    config.Save();
                    compositor.TriggerRecomposite("priority-change");
                }

                // Colors button + popup editor
                ImGui.TableNextColumn();
                var popupId = $"##colors_{entry.ModDirectory}";
                if (ImGui.Button($"Colors##{entry.ModDirectory}"))
                    ImGui.OpenPopup(popupId);

                if (ImGui.BeginPopup(popupId))
                {
                    DrawColorEditor(entry);
                    ImGui.EndPopup();
                }

                // Overlay count
                ImGui.TableNextColumn();
                var activeOverlays = discovery.ResolveActiveOverlays(entry);
                int ovCount = activeOverlays.Count;
                ImGui.TextUnformatted($"{ovCount} overlay{(ovCount != 1 ? "s" : "")}");
            }

            ImGui.EndTable();
        }

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

    private void DrawColorEditor(OverlayEntry entry)
    {
        ImGui.TextUnformatted(entry.ModName);
        ImGui.Separator();

        // Clear per-entry index cache on popup open so option switches are reflected.
        if (ImGui.IsWindowAppearing())
            foreach (var k in _indexRowCache.Keys.Where(k => k.StartsWith(entry.SidecarRoot)).ToList())
                _indexRowCache.Remove(k);

        // ── simple-mod path (top-level Overlays, no OptionGroups) ────────────
        if (entry.Metadata.OptionGroups is not { Count: > 0 })
        {
            var rows = entry.Metadata.ColorTableRows ??= [];
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
            HashSet<int>? filteredSimple = (hasIdxSimple && usedRowsSimple.Count > 0) ? usedRowsSimple : null;
            if (!hasIdxSimple)
                ImGui.TextDisabled("No index texture — only Row 16 is applied.");
            bool changedSimple = false;
            DrawRowControls(entry.ModDirectory, rows, filteredSimple, ref changedSimple);
            if (changedSimple) { discovery.SaveMetadata(entry); compositor.TriggerRecomposite("colors-change"); }
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
                activeOptions.Add((group.PenumbraGroupName, opt));
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

        if (usedRows == null && !activeOpt.Overlays.Any(o => o.Index != null))
            ImGui.TextDisabled("No index texture — only Row 16 is applied.");

        activeOpt.ColorTableRows ??= [];
        bool changed = false;
        DrawRowControls($"{entry.ModDirectory}_{groupName}", activeOpt.ColorTableRows, usedRows, ref changed);
        if (changed) { discovery.SaveMetadata(entry); compositor.TriggerRecomposite("colors-change"); }
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
