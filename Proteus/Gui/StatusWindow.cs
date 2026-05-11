using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
            MaximumSize = new System.Numerics.Vector2(800, 600),
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
            ImGui.BeginTable("##mods", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV);
            ImGui.TableSetupColumn("##en",     ImGuiTableColumnFlags.WidthFixed, 20);
            ImGui.TableSetupColumn("Mod",      ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pri",      ImGuiTableColumnFlags.WidthFixed, 60);
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

                // Overlay count
                ImGui.TableNextColumn();
                var activeOverlays = discovery.ResolveActiveOverlays(entry);
                ImGui.TextUnformatted($"{activeOverlays.Count} overlay{(activeOverlays.Count != 1 ? "s" : "")}");
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
}
