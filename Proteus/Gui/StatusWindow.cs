using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using Proteus.Interop;
using Proteus.Services;

namespace Proteus.Gui;

public class StatusWindow : Window
{
    private readonly CompositorService compositor;
    private readonly SidecarDiscoveryService discovery;
    private readonly PenumbraBridge penumbra;
    private readonly Configuration config;

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

    public override bool DrawConditions()
        => !Plugin.Condition[ConditionFlag.WatchingCutscene];

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
                    DrawColorEditor(entry, discovery.GetActiveColorRows(entry));
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

    private void DrawColorEditor(OverlayEntry entry, List<ColorTableRowPreset> rows)
    {
        ImGui.TextUnformatted(entry.ModName);
        ImGui.Separator();

        var overlays  = discovery.ResolveActiveOverlays(entry);
        bool hasIndex = overlays.Any(o => o.Descriptor.Index != null);

        bool changed = false;

        if (!hasIndex)
            ImGui.TextDisabled("No index texture — only Row 16 is applied.");

        for (int pairNum = 1; pairNum <= 16; pairNum++)
        {
            var preset = rows.FirstOrDefault(r => r.Row == pairNum);

            ImGui.TextUnformatted($"{pairNum,2}");

            // Sub-row A
            ImGui.SameLine();
            ImGui.TextDisabled("A");
            ImGui.SameLine();

            var colA = HexToVec3(preset?.SubRowA?.Diffuse);
            ImGui.SetNextItemWidth(22);
            if (ImGui.ColorEdit3($"##dA_{entry.ModDirectory}_{pairNum}", ref colA,
                ImGuiColorEditFlags.NoInputs))
            {
                preset = EnsurePreset(rows, pairNum);
                preset.SubRowA ??= new ColorTableSubRowPreset();
                preset.SubRowA.Diffuse = Vec3ToHex(colA);
                changed = true;
            }

            ImGui.SameLine();
            float emA = preset?.SubRowA?.Emissive ?? 0f;
            ImGui.SetNextItemWidth(60);
            if (ImGui.DragFloat($"##eA_{entry.ModDirectory}_{pairNum}", ref emA, 0.01f, 0f, 1f, "%.2f"))
            {
                preset = EnsurePreset(rows, pairNum);
                preset.SubRowA ??= new ColorTableSubRowPreset();
                preset.SubRowA.Emissive = Math.Clamp(emA, 0f, 1f);
                changed = true;
            }

            // Sub-row B
            ImGui.SameLine();
            ImGui.TextDisabled(" B");
            ImGui.SameLine();

            var colB = HexToVec3(preset?.SubRowB?.Diffuse);
            ImGui.SetNextItemWidth(22);
            if (ImGui.ColorEdit3($"##dB_{entry.ModDirectory}_{pairNum}", ref colB,
                ImGuiColorEditFlags.NoInputs))
            {
                preset = EnsurePreset(rows, pairNum);
                preset.SubRowB ??= new ColorTableSubRowPreset();
                preset.SubRowB.Diffuse = Vec3ToHex(colB);
                changed = true;
            }

            ImGui.SameLine();
            float emB = preset?.SubRowB?.Emissive ?? 0f;
            ImGui.SetNextItemWidth(60);
            if (ImGui.DragFloat($"##eB_{entry.ModDirectory}_{pairNum}", ref emB, 0.01f, 0f, 1f, "%.2f"))
            {
                preset = EnsurePreset(rows, pairNum);
                preset.SubRowB ??= new ColorTableSubRowPreset();
                preset.SubRowB.Emissive = Math.Clamp(emB, 0f, 1f);
                changed = true;
            }
        }

        if (changed)
        {
            discovery.SaveMetadata(entry);
            compositor.TriggerRecomposite("colors-change");
        }
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
