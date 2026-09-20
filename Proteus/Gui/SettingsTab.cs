using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

internal sealed class SettingsTab
{
    private readonly Configuration config;
    private readonly CompositorService compositor;
    private readonly SidecarDiscoveryService discovery;
    private readonly HatCompatPanel hatCompat;
    private readonly LogExportService logExport;

    // Copy Logs: the running export, polled each frame, then the file it wrote or why it could not.
    private Task<string>? _copyLogsTask;
    private string? _copyLogsPath;
    private string? _copyLogsError;

    public SettingsTab(Configuration config, CompositorService compositor, SidecarDiscoveryService discovery, HatCompatPanel hatCompat,
        LogExportService logExport)
    {
        this.config = config;
        this.compositor = compositor;
        this.discovery = discovery;
        this.hatCompat = hatCompat;
        this.logExport = logExport;
    }

    /// <summary>The Settings tab, grouped into carded sections.</summary>
    internal void DrawSettingsTab()
    {
        var s = Strings.Settings;
        ProteusStyle.SectionHeader(s.SecGeneral);
        using (ProteusStyle.Card())
        {
            var enabled = config.PluginEnabled;
            // The master switch, drawn as a switch because it governs the other toggles.
            var enabledHelp = s.EnabledTip;
            if (ImGuiComponents.ToggleButton("##enabled", ref enabled))
            {
                config.PluginEnabled = enabled;
                config.Save();
                compositor.SetEnabled(enabled);   // clears output, redraws, then toggles the Penumbra mod
            }
            // Tooltip on both the switch and its label.
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

        // Always drawn: its governing switch lives inside it.
        ImGui.Spacing();
        ProteusStyle.SectionHeader(s.SecHatCompat);
        using (ProteusStyle.Card())
            hatCompat.Draw();

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
            // Turning it back on catches up with a forced recomposite: ambient triggers were dropped while off, and config knobs are outside the fingerprint.
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
        // Both a visible (?) marker and the hover tooltip.
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

        // A button into the scroll-map library in Proteus's own mod folder; null means Penumbra's mod directory is unavailable.
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

        // Said out loud, not just in the log: the setting is ticked but not in effect, and a silent override is
        // indistinguishable from the setting doing nothing.
        if (config.EnableCompression && TextureLoader.CompressionRefused)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f),
                string.Format(s.CompressionRefusedFmt, TextureLoader.EncodeMsPerMegapixel));

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

        // An in-place reload can't reload the hosting ring/bracelet's .mdl, so this forces a full redraw to restore its original model.
        if (ImGui.Button(s.RestoreAccessory))
            compositor.RestoreChangedAccessory();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.RestoreAccessoryTip);
    }

    private void DrawDiagnostics()
    {
        // Escape hatch for a stale texture: the decode cache keys on timestamp + size, which an edit can preserve.
        var s = Strings.Settings;

        if (ImGui.Button(s.ClearCache))
            compositor.ClearTextureCacheAndRecomposite();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.ClearCacheTip);

        DrawCopyLogs();

        // Which skin the overlays are painted onto; a composite on the wrong body mod otherwise looks fine.
        var upstreams = compositor.BaseUpstreams();
        // "###baseSkin" pins the id, so the count or a language change doesn't collapse the header.
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

        // What each composite actually blended per channel; a red row is an overlay whose diffuse reached nothing. Pre-sorted: this runs every frame.
        var contributions = compositor.ChannelContributions();
        if (contributions.Count > 0 &&
            ImGui.CollapsingHeader(string.Format(s.ReachHeaderFmt, contributions.Count) + "###overlayReach"))
        {
            foreach (var c in contributions)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(c.Material);
                if ((c.DiffuseWanted && c.Diffuse == 0) || (c.NormalWanted && c.Normal == 0))
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f),
                        string.Format(s.ReachFailedFmt, name, c.Diffuse, c.Normal, c.Mask));
                else if (c.Diffuse + c.Normal + c.Mask == 0)
                    // Reached only by ambient occlusion or skin-tint suppression.
                    ImGui.TextDisabled(string.Format(s.ReachEffectsOnlyFmt, name));
                else
                    ImGui.TextUnformatted(string.Format(s.ReachOkFmt, name, c.Diffuse, c.Normal, c.Mask));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(c.Material);
            }
            ImGui.TextDisabled(s.ReachNote);
        }
    }

    private void DrawCopyLogs()
    {
        var s = Strings.Settings;

        if (_copyLogsTask is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully) _copyLogsPath = done.Result;
            else _copyLogsError = done.Exception?.GetBaseException().Message ?? "cancelled";
            _copyLogsTask = null;
        }

        var busy = _copyLogsTask != null;
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.Button(busy ? s.CopyLogsBusy : s.CopyLogs))
            {
                _copyLogsPath = _copyLogsError = null;
                _copyLogsTask = Task.Run(logExport.ExportAsync);
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(s.CopyLogsTip);

        if (_copyLogsPath is { } path)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(s.CopyLogsSaved);
            ImGui.SameLine();
            // The path is the link: click opens the file in the user's text editor.
            ImGui.TextColored(ProteusStyle.Accent, path);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip(s.CopyLogsOpenTip);
            }
            if (ImGui.IsItemClicked())
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch { /* no handler for .txt — Show in folder still works */ }
            ImGui.SameLine();
            if (ImGui.SmallButton(s.CopyLogsShowInFolder))
                try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
                catch { /* no shell */ }
        }
        else if (_copyLogsError is { } error)
        {
            ImGui.TextColored(ProteusStyle.Bad, string.Format(s.CopyLogsFailedFmt, error));
        }
    }

    private void DrawSkinEffectSliders()
    {
        // Skin-tint suppression strength (global multiplier). The per-pixel amount is weighted by
        // overlay color: bright dyes get de-tinted, dark dyes are left skin-tinted and matte.
        var s = Strings.Settings;

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

    /// <summary>Light-sensitive glow: master switch, pinned level and probe readout. None of these recomposite; the light reaches the live colour table.</summary>
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

        // Only while the probe runs, or stale numbers would look current.
        if (enabled && StatusWindow.SceneLight is { } probe)
        {
            ImGui.TextDisabled(string.Format(s.LightResponseReadoutFmt,
                probe.Level, probe.HasSky ? probe.SkyTerm : 0f, probe.PlacedTerm,
                probe.LightsCounted, probe.LightsSeen));
            // The raw sky signals, the part of the estimate most likely to be wrong.
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
        // Logarithmic: the range spans 512 MB to 32 GB.
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

        // Skip skin the gear shell would otherwise draw twice (see Configuration).
        var hideRedundant = config.HideRedundantMeshes;
        if (ImGui.Checkbox(s.RedundantMeshes, ref hideRedundant))
        {
            config.HideRedundantMeshes = hideRedundant;
            config.Save();
            compositor.TriggerRecomposite("redundant-meshes");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.RedundantMeshesTip);
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(s.RedundantMeshesTip);
    }
}
