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
            ImGui.BeginTable("##mods", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV);
            ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pri", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Overlays", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();

            foreach (var entry in mods)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.ModName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Priority.ToString());
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
